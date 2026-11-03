#include "vnotch_common.h"
#include "vnotch_json_view.h"
#include "vnotch_handle_registry.h"
#include <array>

using namespace vnotch;

namespace vnotch::services
{
    class YouTubeMetadataParsing final
    {
    public:
        static int32_t vn_youtube_looks_like_quota_exceeded_impl(const char16_t* body_ptr, int32_t body_len, uint8_t* out_exceeded) noexcept
        {
    if (out_exceeded == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    std::u16string_view sv = make_u16_view(body_ptr, body_len);
    if (is_null_or_whitespace(sv))
    {
        *out_exceeded = 0u;
        return VN_OK;
    }
    bool ex = contains_ordinal(sv, u"quotaExceeded") ||
              contains_ordinal(sv, u"dailyLimitExceeded") ||
              contains_ordinal(sv, u"rateLimitExceeded");
    *out_exceeded = ex ? 1u : 0u;
    return VN_OK;
}

        static int32_t vn_youtube_parse_iso8601_duration_ticks_impl(const char16_t* iso_ptr, int32_t iso_len, int64_t* out_ticks) noexcept
        {
    if (out_ticks == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_ticks = 0;
    std::u16string_view sv = make_u16_view(iso_ptr, iso_len);
    int32_t pt_pos = index_of_ordinal(sv, u"PT");
    if (pt_pos < 0)
    {
        return VN_OK;
    }
    size_t idx = static_cast<size_t>(pt_pos + 2);
    int64_t hours = 0;
    int64_t minutes = 0;
    int64_t seconds = 0;
    char last_unit = 0;

    while (idx < sv.size())
    {
        size_t num_start = idx;
        int64_t val = 0;
        bool overflow = false;
        while (idx < sv.size() && sv[idx] >= u'0' && sv[idx] <= u'9')
        {
            int64_t digit = static_cast<int64_t>(sv[idx] - u'0');
            if (val > (0x7FFFFFFFLL - digit) / 10LL)
            {
                overflow = true;
            }
            if (!overflow) val = val * 10LL + digit;
            ++idx;
        }
        if (idx == num_start || idx >= sv.size())
        {
            break;
        }
        char16_t unit = sv[idx];
        if (unit == u'H' && last_unit == 0)
        {
            if (overflow) { *out_ticks = 0; return VN_OK; }
            hours = val;
            last_unit = 'H';
            ++idx;
        }
        else if (unit == u'M' && (last_unit == 0 || last_unit == 'H'))
        {
            if (overflow) { *out_ticks = 0; return VN_OK; }
            minutes = val;
            last_unit = 'M';
            ++idx;
        }
        else if (unit == u'S' && (last_unit == 0 || last_unit == 'H' || last_unit == 'M'))
        {
            if (overflow) { *out_ticks = 0; return VN_OK; }
            seconds = val;
            last_unit = 'S';
            ++idx;
            break;
        }
        else
        {
            break;
        }
    }
    if (hours > INT64_MAX / TICKS_PER_HOUR) return VN_OK;
    int64_t ticks = hours * TICKS_PER_HOUR;
    if (minutes > (INT64_MAX - ticks) / TICKS_PER_MINUTE) return VN_OK;
    ticks += minutes * TICKS_PER_MINUTE;
    if (seconds > (INT64_MAX - ticks) / TICKS_PER_SECOND) return VN_OK;
    *out_ticks = ticks + seconds * TICKS_PER_SECOND;
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_youtube_looks_like_quota_exceeded(const char16_t* body_ptr, int32_t body_len, uint8_t* out_exceeded) noexcept
{
    return vnotch::services::YouTubeMetadataParsing::vn_youtube_looks_like_quota_exceeded_impl(body_ptr, body_len, out_exceeded);
}

VN_EXPORT int32_t vn_youtube_parse_iso8601_duration_ticks(const char16_t* iso_ptr, int32_t iso_len, int64_t* out_ticks) noexcept
{
    return vnotch::services::YouTubeMetadataParsing::vn_youtube_parse_iso8601_duration_ticks_impl(iso_ptr, iso_len, out_ticks);
}

VN_EXPORT int32_t vn_youtube_title_matches(const char16_t*, int32_t, const char16_t*, int32_t, uint8_t*) noexcept;
VN_EXPORT int32_t vn_youtube_subtitle_determine_is_auto_generated(const char16_t*, int32_t, const char16_t*, int32_t,
    const char16_t*, int32_t, const char16_t*, int32_t, uint8_t, uint8_t, uint8_t*) noexcept;
VN_EXPORT int32_t vn_youtube_subtitle_normalize_cue_text(const char16_t*, int32_t, uint8_t, char16_t*, int32_t, int32_t*) noexcept;
VN_EXPORT int32_t vn_youtube_subtitle_clamp_timestamp_ticks(int64_t, double, uint8_t, int64_t*, uint8_t*) noexcept;

namespace {
using Json = vnotch::json::Value;
using Text = std::u16string;
using JsonRow = VnYoutubeJsonRow;
struct JsonResponse {
    std::vector<JsonRow> rows;
    Text characters;
    bool has_collection = false;
    void append(const std::array<Text, 5>& fields, int64_t ticks = 0, int32_t flags = 0) {
        JsonRow row; row.ticks = ticks; row.flags = flags;
        for (size_t i = 0; i < fields.size(); ++i) {
            row.offset[i] = static_cast<int32_t>(characters.size()); row.length[i] = static_cast<int32_t>(fields[i].size());
            characters += fields[i];
        }
        rows.push_back(row);
    }
};
vnotch::HandleRegistry<JsonResponse> responses;
int64_t duration(Json item) {
    const auto iso = item.get("contentDetails").get("duration").text(); int64_t ticks = 0;
    vn_youtube_parse_iso8601_duration_ticks(iso.data(), static_cast<int32_t>(iso.size()), &ticks);
    return ticks;
}
Text thumbnail(Json item) {
    const auto thumbnails = item.get("snippet").get("thumbnails");
    for (const auto key : {"maxres", "standard", "high", "medium", "default"}) {
        auto url = thumbnails.get(key).get("url").text();
        if (!vnotch::is_null_or_whitespace(url)) return url;
    }
    return {};
}
bool title_matches(const Text& title, std::u16string_view expected) {
    uint8_t match = 0;
    return vn_youtube_title_matches(title.data(), static_cast<int32_t>(title.size()), expected.data(), static_cast<int32_t>(expected.size()), &match) == VN_OK && match;
}
void metadata(Json root, int kind, std::u16string_view expected, JsonResponse& output) {
    if (kind == 3) {
        if (root.is('{')) output.append({Text(expected), root.get("title").text(), root.get("author_name").text(), {}, {}});
        return;
    }
    Json items = kind == 2 ? root : root.get("items");
    output.has_collection = items.is('[');
    items.each([&](Json, Json item) {
        Text id, title, author, thumb; int64_t ticks = 0;
        if (kind == 0) {
            id = item.get("id").get("videoId").text(); title = item.get("snippet").get("title").text(); author = item.get("snippet").get("channelTitle").text();
        } else if (kind == 1) {
            const auto url = item.get("url").text();
            int32_t start = 0, count = 0; uint8_t found = 0;
            if (vn_media_metadata_extract_youtube_video_id(url.data(), static_cast<int32_t>(url.size()), 2, &start, &count, &found) != VN_OK || !found) return true;
            id.assign(url, start, count); title = item.get("title").text(); author = item.get("uploaderName").text(); thumb = item.get("thumbnail").text();
        } else if (kind == 2) {
            id = item.get("videoId").text(); title = item.get("title").text(); author = item.get("author").text();
        } else {
            id = Text(expected); title = item.get("snippet").get("title").text(); author = item.get("snippet").get("channelTitle").text();
            thumb = thumbnail(item); ticks = duration(item);
        }
        if (kind == 1 || kind == 2) {
            const int64_t seconds = item.get(kind == 1 ? "duration" : "lengthSeconds").integer();
            if (seconds < INT64_MIN / 10000000 || seconds > INT64_MAX / 10000000) return true;
            ticks = seconds * 10000000;
        }
        if (id.empty() || (kind < 3 && !title_matches(title, expected))) return true;
        output.append({id, title, author, thumb, {}}, ticks);
        return kind < 3;
    });
}
void captions(Json root, JsonResponse& output) {
    const auto tracks = root.get("captions").get("playerCaptionsTracklistRenderer").get("captionTracks");
    output.has_collection = tracks.is('[');
    tracks.each([&](Json, Json track) {
        auto url = track.get("baseUrl").text(), language = track.get("languageCode").text(), vss = track.get("vssId").text(), kind = track.get("kind").text();
        auto name = track.get("name").get("simpleText").text();
        if (name.empty()) name = track.get("name").get("runs").first().get("text").text();
        if (name.empty()) name = language;
        uint8_t automatic = 0;
        vn_youtube_subtitle_determine_is_auto_generated(kind.data(), static_cast<int32_t>(kind.size()), vss.data(), static_cast<int32_t>(vss.size()),
            url.data(), static_cast<int32_t>(url.size()), name.data(), static_cast<int32_t>(name.size()),
            track.get("isAutoGenerated").bytes == "true", track.get("isAsr").bytes == "true", &automatic);
        output.append({url, language, name, vss, {}}, 0, automatic);
        return true;
    });
}
void json3(Json root, JsonResponse& output) {
    const auto events = root.get("events"); output.has_collection = events.is('[');
    events.each([&](Json, Json event) {
        const auto segments = event.get("segs"), start = event.get("tStartMs");
        if (!segments.is('[') || start.bytes.empty()) return true;
        Text text;
        segments.each([&](Json, Json segment) { text += segment.get("utf8").text(); return true; });
        Text normalized(text.size(), u'\0'); int32_t length = 0;
        if (vn_youtube_subtitle_normalize_cue_text(text.data(), static_cast<int32_t>(text.size()), 0,
            normalized.data(), static_cast<int32_t>(normalized.size()), &length) != VN_OK) return true;
        normalized.resize(length);
        if (vnotch::is_null_or_whitespace(normalized)) return true;
        int64_t ticks = 0; uint8_t valid = 0;
        vn_youtube_subtitle_clamp_timestamp_ticks(start.integer(), 0, 0, &ticks, &valid);
        if (valid) output.append({normalized, {}, {}, {}, {}}, ticks);
        return true;
    });
    std::stable_sort(output.rows.begin(), output.rows.end(), [](const auto& a, const auto& b) { return a.ticks < b.ticks; });
}
}

// kind: 0 Data API search, 1 Piped, 2 Invidious, 3 oEmbed,
// 4 Data API video/duration, 5 Innertube caption tracks, 6 JSON3 cues.
VN_EXPORT int32_t vn_youtube_parse_utf8(const uint8_t* bytes, int32_t length, int32_t kind, const char16_t* expected, int32_t expected_length,
    int64_t* handle, const JsonRow** rows, int32_t* count, const char16_t** characters, uint8_t* has_collection) noexcept {
    if (!bytes || length <= 0 || length > 2 * 1024 * 1024 || kind < 0 || kind > 6 || expected_length < 0 || (!expected && expected_length) ||
        !handle || !rows || !count || !characters || !has_collection) return VN_ERR_INVALID_ARG;
    *handle = 0; *rows = nullptr; *count = 0; *characters = nullptr; *has_collection = 0;
    try {
        const auto root = vnotch::json::parse({reinterpret_cast<const char*>(bytes), static_cast<size_t>(length)});
        auto result = std::make_unique<JsonResponse>();
        if (kind == 5) captions(root, *result);
        else if (kind == 6) json3(root, *result);
        else metadata(root, kind, {expected ? expected : u"", static_cast<size_t>(expected_length)}, *result);
        *rows = result->rows.data(); *count = static_cast<int32_t>(result->rows.size());
        *characters = result->characters.data(); *has_collection = result->has_collection ? 1 : 0;
        *handle = responses.Add(std::move(result));
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}
VN_EXPORT int32_t vn_youtube_parsed_close(int64_t handle) noexcept { return responses.Remove(handle) ? VN_OK : VN_ERR_INVALID_ARG; }

