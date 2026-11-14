#include "vnotch_common.h"
#include "vnotch_com.h"
#include "vnotch_handle_registry.h"
#include "vnotch_json_view.h"
#include "LyricsInternal.h"
#include <xmllite.h>
#include <shlwapi.h>
#include <array>
#include <charconv>
#include <stdexcept>
#pragma comment(lib, "xmllite.lib")
#pragma comment(lib, "shlwapi.lib")

extern "C" int32_t __stdcall vn_youtube_subtitle_normalize_cue_text(const char16_t*, int32_t, uint8_t, char16_t*, int32_t, int32_t*) noexcept;
extern "C" int32_t __stdcall vn_youtube_subtitle_clamp_timestamp_ticks(int64_t, double, uint8_t, int64_t*, uint8_t*) noexcept;
extern "C" int32_t vn_lyrics_try_parse_lrc_line(const char16_t*, int32_t, int64_t*, int32_t*, int32_t*, uint8_t*) noexcept;

namespace {
using Text = std::u16string;
using View = std::u16string_view;
using Json = vnotch::json::Value;
constexpr View ttml = u"http://www.w3.org/ns/ttml";
constexpr View metadata = u"http://www.w3.org/ns/ttml#metadata";
constexpr View xml_namespace = u"http://www.w3.org/XML/1998/namespace";
void check(HRESULT status) { if (FAILED(status)) throw std::invalid_argument("Invalid lyric XML"); }
bool valid_words(int64_t time, View text, std::span<const VnLyricWord> words) {
    if (words.empty() || words.size() > 256) return false;
    int32_t offset = 0;
    for (const auto& word : words) {
        if (word.start < time || word.end < word.start || word.offset < offset || word.length <= 0 ||
            static_cast<size_t>(word.offset) > text.size() || static_cast<size_t>(word.length) > text.size() - word.offset) return false;
        time = word.start; offset = word.offset + word.length;
    }
    return true;
}
struct Result {
    std::vector<VnLyricLine> lines;
    std::vector<VnLyricWord> words;
    Text text;
    void add(int64_t time, View label, std::span<const VnLyricWord> parts = {}) {
        if (vnotch::is_null_or_whitespace(label) || label.size() > 4096) return;
        const bool valid = valid_words(time, label, parts);
        lines.push_back({time, static_cast<int32_t>(text.size()), static_cast<int32_t>(label.size()),
            static_cast<int32_t>(words.size()), valid ? static_cast<int32_t>(parts.size()) : 0});
        text += label;
        if (valid) words.insert(words.end(), parts.begin(), parts.end());
    }
};
vnotch::HandleRegistry<Result> results;

bool clock_time(View input, int64_t& ticks) {
    if (input.ends_with(u's')) input.remove_suffix(1);
    double seconds = 0;
    int parts = 1;
    for (auto c : input) if (c == u':') ++parts;
    if (input.empty() || parts > 3) return false;
    for (int i = 0; i < parts; ++i) {
        const auto colon = input.find(u':');
        const auto part = input.substr(0, colon);
        double number = 0, scale = 1;
        bool dot = false, digit = false;
        for (auto c : part) {
            if (c == u'.' && !dot && i == parts - 1) { dot = true; continue; }
            if (c < u'0' || c > u'9') return false;
            digit = true;
            if (dot) { scale *= 10; number += (c - u'0') / scale; }
            else number = number * 10 + c - u'0';
            if (number > 86400) return false;
        }
        if (!digit || (parts > 1 && (i > 0 || parts == 2) && number >= 60)) return false;
        seconds = seconds * 60 + number;
        if (colon != View::npos) input.remove_prefix(colon + 1);
    }
    if (seconds > 86400) return false;
    ticks = static_cast<int64_t>(seconds * 10000000);
    return true;
}
View xml_value(IXmlReader* reader) {
    const wchar_t* value = nullptr; UINT count = 0;
    check(reader->GetValue(&value, &count));
    return {reinterpret_cast<const char16_t*>(value), count};
}
View local_name(IXmlReader* reader) {
    const wchar_t* value = nullptr; UINT count = 0;
    check(reader->GetLocalName(&value, &count));
    return {reinterpret_cast<const char16_t*>(value), count};
}
View namespace_uri(IXmlReader* reader) {
    const wchar_t* value = nullptr; UINT count = 0;
    check(reader->GetNamespaceUri(&value, &count));
    return {reinterpret_cast<const char16_t*>(value), count};
}
struct Frame {
    bool content = false, preserve = false, span = false, cue = false, has_begin = false, has_end = false;
    bool begin_valid = false, end_valid = false;
    int64_t begin = 0, end = 0;
    size_t offset = 0, words = 0;
};
void xml(const uint8_t* bytes, int length, bool utf16, bool amll, Result& output) {
    vnotch::com::ComPtr<IStream> stream;
    stream.ptr = SHCreateMemStream(bytes, static_cast<UINT>(length));
    if (!stream) throw std::bad_alloc();
    vnotch::com::ComPtr<IXmlReader> reader;
    check(CreateXmlReader(__uuidof(IXmlReader), reinterpret_cast<void**>(reader.Put()), nullptr));
    check(reader->SetProperty(XmlReaderProperty_DtdProcessing, DtdProcessing_Prohibit));
    check(reader->SetProperty(XmlReaderProperty_MaxElementDepth, 64));
    check(reader->SetProperty(XmlReaderProperty_XmlResolver, 0));
    vnotch::com::ComPtr<IXmlReaderInput> input;
    if (utf16) {
        check(CreateXmlReaderInputWithEncodingName(stream.get(), nullptr, L"UTF-16LE", FALSE, nullptr, input.Put()));
        check(reader->SetInput(input.get()));
    } else check(reader->SetInput(stream.get()));
    std::array<Frame, 65> frames{};
    Text text;
    std::vector<VnLyricWord> words;
    bool root_valid = !amll, in_body = false, in_cue = false, words_valid = true;
    UINT body_depth = 0, cue_depth = 0;
    int64_t line_begin = 0, line_end = 0;
    XmlNodeType type{};
    UINT nesting = 0;
    HRESULT status;
    auto close = [&](UINT depth) {
        const auto& frame = frames[depth];
        if (amll && in_cue && frame.span && frame.content && words.size() == frame.words && text.size() > frame.offset &&
            !vnotch::is_null_or_whitespace(View(text).substr(frame.offset)) && (frame.has_begin || frame.has_end)) {
            if (!frame.begin_valid || !frame.end_valid || frame.begin < line_begin || frame.end < frame.begin || frame.end > line_end)
                words_valid = false;
            else if (words.size() < 257) words.push_back({frame.begin, frame.end, static_cast<int32_t>(frame.offset), static_cast<int32_t>(text.size() - frame.offset)});
        }
        if (in_cue && depth == cue_depth) {
            if (amll) output.add(line_begin, text, words_valid ? std::span<const VnLyricWord>(words) : std::span<const VnLyricWord>{});
            else {
                Text normalized(text.size() + 1, u'\0'); int32_t count = 0;
                if (vn_youtube_subtitle_normalize_cue_text(text.data(), static_cast<int32_t>(text.size()), 1,
                    normalized.data(), static_cast<int32_t>(normalized.size()), &count) == VN_OK) output.add(line_begin, View(normalized).substr(0, count));
            }
            in_cue = false;
        }
        if (in_body && depth == body_depth) in_body = false;
    };
    while ((status = reader->Read(&type)) == S_OK) {
        const UINT depth = type == XmlNodeType_EndElement ? --nesting : nesting;
        if (type == XmlNodeType_Element && !reader->IsEmptyElement()) ++nesting;
        if (depth >= frames.size()) throw std::invalid_argument("XML too deep");
        if (type == XmlNodeType_Element) {
            const bool is_ttml = namespace_uri(reader.get()) == ttml;
            const auto name = local_name(reader.get());
            if (depth == 0) root_valid = !amll || (is_ttml && name == u"tt");
            const bool body = amll && root_valid && depth == 1 && is_ttml && name == u"body";
            const bool span = amll && is_ttml && name == u"span";
            const bool cue = !in_cue && (amll ? in_body && is_ttml && name == u"p" : namespace_uri(reader.get()).empty() && (name == u"p" || name == u"text"));
            Frame frame;
            frame.content = in_cue && frames[depth - 1].content && span;
            frame.preserve = depth > 0 && frames[depth - 1].preserve;
            frame.span = span; frame.cue = cue; frame.offset = text.size(); frame.words = words.size();
            bool suppressed = false, has_ms = false, has_seconds = false;
            int64_t milliseconds = 0; double seconds = 0;
            for (HRESULT attr = reader->MoveToFirstAttribute(); attr == S_OK; attr = reader->MoveToNextAttribute()) {
                const auto attr_name = local_name(reader.get()), ns = namespace_uri(reader.get()), value = xml_value(reader.get());
                if (ns.empty() && attr_name == u"begin") { frame.has_begin = true; frame.begin_valid = clock_time(value, frame.begin); }
                else if (ns.empty() && attr_name == u"end") { frame.has_end = true; frame.end_valid = clock_time(value, frame.end); }
                else if (ns == xml_namespace && attr_name == u"space" && value == u"preserve") frame.preserve = true;
                else if (ns == metadata && attr_name == u"role") suppressed = value == u"x-translation" || value == u"x-roman" || value == u"x-bg";
                else if (!amll && ns.empty() && (attr_name == u"t" || attr_name == u"start")) {
                    std::string number;
                    for (auto c : value) number += c <= 127 ? static_cast<char>(c) : '?';
                    if (attr_name == u"t") {
                        auto trimmed = vnotch::vn_trim(value); number.clear();
                        for (auto c : trimmed) number += c <= 127 ? static_cast<char>(c) : '?';
                        if (!number.empty() && number.front() == '+') number.erase(0, 1);
                        auto parsed = std::from_chars(number.data(), number.data() + number.size(), milliseconds);
                        has_ms = parsed.ec == std::errc{} && parsed.ptr == number.data() + number.size();
                    } else {
                        auto parsed = std::from_chars(number.data(), number.data() + number.size(), seconds);
                        has_seconds = parsed.ec == std::errc{} && parsed.ptr == number.data() + number.size() && std::isfinite(seconds);
                    }
                }
            }
            reader->MoveToElement();
            if (body) { in_body = true; body_depth = depth; }
            if (suppressed) frame.content = false;
            if (cue) {
                bool valid = frame.begin_valid && frame.end_valid && frame.end > frame.begin;
                if (!amll) {
                    uint8_t ok = 0;
                    if (has_ms || has_seconds) vn_youtube_subtitle_clamp_timestamp_ticks(milliseconds, seconds, has_ms ? 0 : 1, &frame.begin, &ok);
                    valid = ok != 0;
                }
                if (valid) {
                    in_cue = true; cue_depth = depth; line_begin = frame.begin; line_end = frame.end;
                    text.clear(); words.clear(); words_valid = true; frame.content = true;
                }
            }
            frames[depth] = frame;
            if (reader->IsEmptyElement()) close(depth);
        } else if (type == XmlNodeType_EndElement) close(depth);
        else if (in_cue && (type == XmlNodeType_Text || type == XmlNodeType_Whitespace || type == XmlNodeType_CDATA)) {
            const auto value = xml_value(reader.get());
            const auto& parent = frames[depth - 1];
            if (amll && (!parent.content || (!parent.preserve && vnotch::is_null_or_whitespace(value) && value.find_first_of(u"\r\n") != View::npos))) continue;
            if (text.size() + value.size() > 4096) { words_valid = false; text.resize(4097); }
            else text += value;
        }
    }
    check(status);
}
bool json_time(Json value, int64_t& ticks) {
    double ms = -1;
    if (value.bytes.empty() || value.is('"')) return false;
    const auto parsed = std::from_chars(value.bytes.data(), value.bytes.data() + value.bytes.size(), ms);
    if (parsed.ec != std::errc{} || parsed.ptr != value.bytes.data() + value.bytes.size() || !std::isfinite(ms) || ms < 0 || ms > 86400000) return false;
    ticks = static_cast<int64_t>(ms * 10000); return true;
}
void timed_json(Json root, bool duration, Result& output) {
    auto lines = root.get("lines"), map = root.get("wordMap");
    if (root.is('[')) lines = root;
    lines.each([&](Json, Json item) {
        auto text = item.get("text").text(); int64_t time = 0;
        if (!json_time(item.get(duration ? "time" : "start"), time) || text.size() > 4096) return true;
        auto parts = item.get(duration ? "syllabus" : "words");
        if (parts.bytes.empty()) {
            auto id = item.get("id");
            if (map.is('{')) {
                const auto key = id.is('"') ? id.text() : Text(id.bytes.begin(), id.bytes.end());
                const int length = WideCharToMultiByte(CP_UTF8, 0, reinterpret_cast<const wchar_t*>(key.data()), static_cast<int>(key.size()), nullptr, 0, nullptr, nullptr);
                std::string utf8(length, '\0');
                if (length) WideCharToMultiByte(CP_UTF8, 0, reinterpret_cast<const wchar_t*>(key.data()), static_cast<int>(key.size()), utf8.data(), length, nullptr, nullptr);
                parts = map.get(utf8);
            } else if (map.is('[')) {
                int64_t index = id.integer(-1), at = 0;
                map.each([&](Json, Json value) { if (at++ == index) { parts = value; return false; } return true; });
            }
        }
        if (!parts.is('[')) parts = {};
        std::vector<VnLyricWord> words; size_t offset = 0;
        parts.each([&](Json, Json part) {
            auto label = part.get("text").text(); int64_t start = 0, end = 0;
            if (words.size() == 256 || label.empty() || !json_time(part.get(duration ? "time" : "start"), start) ||
                !json_time(part.get(duration ? "duration" : "end"), end)) { words.clear(); return false; }
            if (duration) end += start;
            const auto found = text.find(label, offset);
            if (found == Text::npos || end > 864000000000LL) { words.clear(); return false; }
            words.push_back({start, end, static_cast<int32_t>(found), static_cast<int32_t>(label.size())});
            offset = found + label.size(); return true;
        });
        output.add(time, text, words); return true;
    });
}
bool word_tag(View text, size_t at, size_t& end, int64_t& ticks) {
    if (text[at] != u'<') return false;
    size_t pos = at + 1, begin = pos;
    int minutes = 0, seconds = 0, fraction = 0;
    while (pos < text.size() && text[pos] >= u'0' && text[pos] <= u'9' && pos - begin < 3) minutes = minutes * 10 + text[pos++] - u'0';
    if (pos == begin || pos + 3 >= text.size() || text[pos++] != u':' || text[pos] < u'0' || text[pos] > u'5' || text[pos + 1] < u'0' || text[pos + 1] > u'9') return false;
    seconds = (text[pos] - u'0') * 10 + text[pos + 1] - u'0'; pos += 2;
    if (text[pos] == u'.') {
        begin = ++pos; int scale = 100;
        while (pos < text.size() && text[pos] >= u'0' && text[pos] <= u'9' && pos - begin < 3) { fraction += (text[pos++] - u'0') * scale; scale /= 10; }
        if (pos == begin) return false;
    }
    if (pos >= text.size() || text[pos] != u'>') return false;
    end = pos + 1; ticks = (minutes * 60000LL + seconds * 1000LL + fraction) * 10000; return true;
}
void line_format(View source, int kind, Result& output, bool filter_credit = false) {
    std::array<char16_t, 4097> text{};
    std::array<VnLyricWord, 256> words{};
    while (!source.empty()) {
        const auto end = source.find_first_of(u"\r\n");
        const auto line = source.substr(0, end);
        if (end == View::npos) source = {};
        else { source.remove_prefix(end + 1); if (!source.empty() && line.size() == end && source.front() == u'\n') source.remove_prefix(1); }
        int64_t time = 0; int32_t length = 0, count = 0; uint8_t ok = 0;
        if (kind == 6) {
            int32_t start = 0;
            if (vn_lyrics_try_parse_lrc_line(line.data(), static_cast<int32_t>(line.size()), &time, &start, &length, &ok) != VN_OK || !ok || length > 4096 || length <= 0) continue;
            auto label = line.substr(start, length);
            vn_lyrics_parse_enhanced(label.data(), length, time, text.data(), static_cast<int32_t>(text.size()), &length,
                words.data(), static_cast<int32_t>(words.size()), &count);
        } else {
            const auto parse = kind == 4
                ? vnotch::services::lyrics_internal::TryParseYrcLine
                : vnotch::services::lyrics_internal::TryParseKrcLine;
            if (parse(line.data(), static_cast<int32_t>(line.size()), &time, text.data(), static_cast<int32_t>(text.size()), &length,
                words.data(), static_cast<int32_t>(words.size()), &count, &ok) != VN_OK || !ok || length <= 0 || count > 256) continue;
        }
        uint8_t credit = 0;
        if (kind != 6 || filter_credit) vn_lyrics_is_credit_header_line(text.data(), length, time, &credit);
        if (!credit) output.add(time, View(text.data(), length), std::span<const VnLyricWord>(words.data(), count));
    }
}
}

// kind 0 = TTML, 1 = YouTube timed-text XML, 2/3 = timed JSON start/end or duration.
VN_EXPORT int32_t vn_lyrics_parse(const uint8_t* bytes, int32_t length, int32_t encoding, int32_t kind,
    int64_t* handle, const VnLyricLine** lines, int32_t* count, const VnLyricWord** words, const char16_t** text) noexcept {
    if (!handle || !lines || !count || !words || !text) return VN_ERR_INVALID_ARG;
    *handle = 0; *lines = nullptr; *count = 0; *words = nullptr; *text = nullptr;
    if (!bytes || length <= 0 || length > 4 * 1024 * 1024 || encoding < 0 || encoding > 1 || (encoding && length % 2) || kind < 0 || kind > 7) return VN_ERR_INVALID_ARG;
    try {
        auto result = std::make_unique<Result>();
        if (kind < 2) xml(bytes, length, encoding == 1, kind == 0, *result);
        else if (kind < 4) timed_json(vnotch::json::parse({reinterpret_cast<const char*>(bytes), static_cast<size_t>(length)}, 64), kind == 3, *result);
        else if (kind == 7) {
            const auto root = vnotch::json::parse({reinterpret_cast<const char*>(bytes), static_cast<size_t>(length)}, 64);
            if (root.get("nolyric").bytes != "true") {
                line_format(root.get("yrc").get("lyric").text(), 4, *result);
                if (result->lines.empty()) line_format(root.get("lrc").get("lyric").text(), 6, *result, true);
            }
        }
        else if (encoding) line_format({reinterpret_cast<const char16_t*>(bytes), static_cast<size_t>(length / 2)}, kind, *result);
        else {
            const auto chars = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(bytes), length, nullptr, 0);
            if (!chars) return VN_ERR_INVALID_ARG;
            Text source(chars, u'\0');
            MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(bytes), length, reinterpret_cast<wchar_t*>(source.data()), chars);
            line_format(source, kind, *result);
        }
        std::stable_sort(result->lines.begin(), result->lines.end(), [](auto& a, auto& b) { return a.start < b.start; });
        const auto id = results.Add(std::move(result));
        auto owner = results.Acquire(id);
        *handle = id; *lines = owner->lines.data(); *count = static_cast<int32_t>(owner->lines.size());
        *words = owner->words.data(); *text = owner->text.data(); return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}
VN_EXPORT int32_t vn_lyrics_parsed_close(int64_t handle) noexcept { return results.Remove(handle) ? VN_OK : VN_ERR_INVALID_ARG; }
VN_EXPORT int32_t vn_lyrics_parse_enhanced(const char16_t* source, int32_t length, int64_t time,
    char16_t* output, int32_t capacity, int32_t* written, VnLyricWord* words, int32_t word_capacity, int32_t* count) noexcept {
    if (!written || !count) return VN_ERR_INVALID_ARG;
    *written = 0; *count = 0;
    if (length < 0 || !source || capacity < length || !output || word_capacity < 256 || !words) return VN_ERR_INVALID_ARG;
    View input(source, length); size_t segment = 0, out = 0; int total = 0; int64_t start = 0;
    for (size_t pos = 0; pos < input.size(); ++pos) {
        size_t end = 0; int64_t next = 0;
        if (!word_tag(input, pos, end, next)) continue;
        const size_t size = pos - segment;
        std::copy_n(input.data() + segment, size, output + out);
        if (total > 0 && total <= 256) words[total - 1] = {start, next, static_cast<int32_t>(out), static_cast<int32_t>(size)};
        out += size; ++total; start = next; segment = end; pos = end - 1;
    }
    const size_t size = input.size() - segment;
    std::copy_n(input.data() + segment, size, output + out);
    if (total > 0 && total <= 256) words[total - 1] = {start, start, static_cast<int32_t>(out), static_cast<int32_t>(size)};
    out += size; *written = static_cast<int32_t>(out);
    if (total > 0 && total <= 256 && valid_words(time, {output, out}, {words, static_cast<size_t>(total)})) *count = total;
    return VN_OK;
}
