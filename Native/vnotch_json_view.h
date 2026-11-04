#pragma once
#include "vnotch_common.h"
#include <charconv>
#include <stdexcept>

// A bounded JSON cursor. Values borrow the UTF-8 input; only requested strings
// are decoded. No object tree or copies of the response body are constructed.
namespace vnotch::json {
using Bytes = std::string_view;
inline void whitespace(Bytes input, size_t& pos) noexcept {
    while (pos < input.size() && (input[pos] == ' ' || input[pos] == '\r' || input[pos] == '\n' || input[pos] == '\t')) ++pos;
}
inline int hex(char value) noexcept {
    if (value >= '0' && value <= '9') return value - '0';
    if (value >= 'a' && value <= 'f') return value - 'a' + 10;
    if (value >= 'A' && value <= 'F') return value - 'A' + 10;
    return -1;
}
inline bool string_end(Bytes input, size_t& pos) noexcept {
    if (pos >= input.size() || input[pos++] != '"') return false;
    while (pos < input.size()) {
        const auto c = static_cast<unsigned char>(input[pos++]);
        if (c == '"') return true;
        if (c < 0x20) return false;
        if (c != '\\') continue;
        if (pos == input.size()) return false;
        const char escape = input[pos++];
        if (escape == 'u') {
            for (int i = 0; i < 4; ++i) if (pos == input.size() || hex(input[pos++]) < 0) return false;
        } else if (Bytes("\"\\/bfnrt").find(escape) == Bytes::npos) return false;
    }
    return false;
}
inline bool skip(Bytes input, size_t& pos, int depth = 0, int max_depth = 32) noexcept {
    whitespace(input, pos);
    if (pos == input.size()) return false;
    const char type = input[pos];
    if (type == '"') return string_end(input, pos);
    if (type == '{' || type == '[') {
        if (depth >= max_depth) return false;
        const char end = type == '{' ? '}' : ']'; ++pos; whitespace(input, pos);
        if (pos < input.size() && input[pos] == end) { ++pos; return true; }
        while (pos < input.size()) {
            if (type == '{') {
                if (!string_end(input, pos)) return false;
                whitespace(input, pos); if (pos == input.size() || input[pos++] != ':') return false;
            }
            if (!skip(input, pos, depth + 1, max_depth)) return false;
            whitespace(input, pos);
            if (pos == input.size()) return false;
            if (input[pos] == end) { ++pos; return true; }
            if (input[pos++] != ',') return false;
            whitespace(input, pos);
        }
        return false;
    }
    for (const auto literal : {Bytes("true"), Bytes("false"), Bytes("null")}) {
        if (input.substr(pos, literal.size()) == literal) { pos += literal.size(); return true; }
    }
    if (input[pos] == '-') { if (++pos == input.size()) return false; }
    if (input[pos] == '0') ++pos;
    else {
        if (input[pos] < '1' || input[pos] > '9') return false;
        while (pos < input.size() && input[pos] >= '0' && input[pos] <= '9') ++pos;
    }
    if (pos < input.size() && input[pos] == '.') {
        const size_t begin = ++pos;
        while (pos < input.size() && input[pos] >= '0' && input[pos] <= '9') ++pos;
        if (pos == begin) return false;
    }
    if (pos < input.size() && (input[pos] == 'e' || input[pos] == 'E')) {
        ++pos; if (pos < input.size() && (input[pos] == '-' || input[pos] == '+')) ++pos;
        const size_t begin = pos;
        while (pos < input.size() && input[pos] >= '0' && input[pos] <= '9') ++pos;
        if (pos == begin) return false;
    }
    return true;
}
struct Value {
    Bytes bytes;
    int max_depth = 32;
    bool is(char type) const noexcept { return !bytes.empty() && bytes.front() == type; }
    std::u16string text() const {
        if (!is('"')) return {};
        std::u16string result;
        auto append_utf8 = [&](Bytes part) {
            if (part.empty()) return;
            const int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, part.data(), static_cast<int>(part.size()), nullptr, 0);
            if (!length) throw std::invalid_argument("Invalid UTF-8");
            const auto offset = result.size(); result.resize(offset + length);
            if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, part.data(), static_cast<int>(part.size()), reinterpret_cast<wchar_t*>(result.data() + offset), length))
                throw std::invalid_argument("Invalid UTF-8");
        };
        size_t pos = 1, start = pos;
        while (pos + 1 < bytes.size()) {
            if (bytes[pos] != '\\') { ++pos; continue; }
            append_utf8(bytes.substr(start, pos - start)); ++pos;
            const char escape = bytes[pos++];
            switch (escape) {
                case 'u': {
                    uint16_t value = 0;
                    for (int i = 0; i < 4; ++i) value = static_cast<uint16_t>((value << 4) | hex(bytes[pos++]));
                    if (value >= 0xD800 && value <= 0xDBFF) {
                        if (pos + 6 >= bytes.size() || bytes[pos] != '\\' || bytes[pos + 1] != 'u') throw std::invalid_argument("Invalid surrogate pair");
                        pos += 2;
                        uint16_t low = 0;
                        for (int i = 0; i < 4; ++i) low = static_cast<uint16_t>((low << 4) | hex(bytes[pos++]));
                        if (low < 0xDC00 || low > 0xDFFF) throw std::invalid_argument("Invalid surrogate pair");
                        result.push_back(static_cast<char16_t>(value)); result.push_back(static_cast<char16_t>(low));
                    } else {
                        if (value >= 0xDC00 && value <= 0xDFFF) throw std::invalid_argument("Invalid surrogate pair");
                        result.push_back(static_cast<char16_t>(value));
                    }
                    break;
                }
                case 'b': result.push_back(u'\b'); break;
                case 'f': result.push_back(u'\f'); break;
                case 'n': result.push_back(u'\n'); break;
                case 'r': result.push_back(u'\r'); break;
                case 't': result.push_back(u'\t'); break;
                default: result.push_back(static_cast<char16_t>(escape)); break;
            }
            start = pos;
        }
        append_utf8(bytes.substr(start, bytes.size() - 1 - start));
        return result;
    }
    template<class F> void each(F&& action) const {
        if (!is('[') && !is('{')) return;
        size_t pos = 1; whitespace(bytes, pos);
        const char end = is('[') ? ']' : '}';
        while (pos < bytes.size() && bytes[pos] != end) {
            Bytes key;
            if (is('{')) {
                const auto begin = pos; if (!string_end(bytes, pos)) return;
                key = bytes.substr(begin, pos - begin); whitespace(bytes, pos); ++pos;
            }
            whitespace(bytes, pos); const auto begin = pos;
            if (!skip(bytes, pos, 0, max_depth)) return;
            if (!action(Value{key, max_depth}, Value{bytes.substr(begin, pos - begin), max_depth})) return;
            whitespace(bytes, pos);
            if (pos < bytes.size() && bytes[pos] == ',') { ++pos; whitespace(bytes, pos); }
        }
    }
    Value get(Bytes name) const {
        Value result;
        if (!is('{')) return result;
        each([&](Value key, Value value) {
            bool matches = key.bytes.size() == name.size() + 2 && key.bytes.substr(1, name.size()) == name;
            if (!matches && key.bytes.find('\\') != Bytes::npos) {
                const auto decoded = key.text();
                matches = decoded.size() == name.size() && std::equal(decoded.begin(), decoded.end(), name.begin());
            }
            if (matches) result = value; // JsonElement.TryGetProperty uses the last duplicate.
            return true;
        });
        return result;
    }
    Value first() const { Value result; if (is('[')) each([&](Value, Value value) { result = value; return false; }); return result; }
    int64_t integer(int64_t fallback = 0) const noexcept {
        int64_t value = 0;
        if (bytes.empty()) return fallback;
        const auto [end, error] = std::from_chars(bytes.data(), bytes.data() + bytes.size(), value);
        return error == std::errc{} && end == bytes.data() + bytes.size() ? value : fallback;
    }
};
inline Value parse(Bytes input, int max_depth = 32) {
    if (input.starts_with("\xEF\xBB\xBF")) input.remove_prefix(3);
    if (input.empty() || !MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, input.data(), static_cast<int>(input.size()), nullptr, 0))
        throw std::invalid_argument("Invalid UTF-8");
    size_t begin = 0; whitespace(input, begin); size_t end = begin;
    if (!skip(input, end, 0, max_depth)) throw std::invalid_argument("Invalid JSON");
    const auto value = input.substr(begin, end - begin); whitespace(input, end);
    if (end != input.size()) throw std::invalid_argument("Trailing JSON");
    return {value, max_depth};
}
}
