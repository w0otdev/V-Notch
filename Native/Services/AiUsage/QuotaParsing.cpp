#include "Parsing/Utf16Text.h"
#include "Parsing/Utf16Json.h"

using namespace vnotch::parsing;

namespace {
constexpr int64_t kMinUnixTimeSeconds = -62135596800LL;
constexpr int64_t kMaxUnixTimeSeconds = 253402300799LL;

constexpr bool is_valid_bucket_id_view(std::u16string_view id) noexcept
{
    if (id.size() > 64) return false;
    for (char16_t c : id)
    {
        if (!is_ascii_alnum(c) && c != u'_' && c != u'-') return false;
    }
    return true;
}

bool parse_quota_window(
    JsonSpan value,
    std::u16string_view percent_key,
    std::u16string_view reset_key,
    uint8_t label_kind,
    bool has_duration,
    int32_t duration_minutes,
    bool has_bucket,
    std::u16string_view bucket_id,
    VnAiParsedWindow& out_win)
{
    JsonSpan p_span{};
    double percent = 0.0;
    if (!json_try_get_property(value, percent_key, p_span) ||
        !json_try_get_double(p_span, percent) ||
        percent < 0.0 || percent > 100.0)
    {
        return false;
    }

    bool has_reset = false;
    int64_t reset_seconds = 0;
    JsonSpan r_span{};
    if (json_try_get_property(value, reset_key, r_span) && r_span.kind != JsonKind::Null)
    {
        if (!json_try_get_int64(r_span, reset_seconds) ||
            reset_seconds < kMinUnixTimeSeconds ||
            reset_seconds > kMaxUnixTimeSeconds)
        {
            return false;
        }
        has_reset = true;
    }

    out_win = {};
    out_win.used_percent = percent;
    out_win.reset_unix_seconds = reset_seconds;
    out_win.duration_minutes = has_duration ? duration_minutes : 0;
    out_win.has_duration = has_duration ? 1 : 0;
    out_win.has_reset = has_reset ? 1 : 0;
    out_win.label_kind = label_kind;
    if (has_bucket && bucket_id.size() <= 64)
    {
        out_win.has_bucket = 1;
        out_win.bucket_len = static_cast<uint8_t>(bucket_id.size());
        for (size_t i = 0; i < bucket_id.size(); ++i)
        {
            out_win.bucket_ascii[i] = static_cast<uint8_t>(bucket_id[i]);
        }
    }
    return true;
}

void add_codex_bucket_slot(
    JsonSpan bucket,
    std::u16string_view slot_name,
    uint8_t label_kind,
    bool has_bucket,
    std::u16string_view bucket_id,
    std::vector<VnAiParsedWindow>& windows)
{
    JsonSpan value{};
    if (!json_try_get_property(bucket, slot_name, value) || value.kind != JsonKind::Object)
        return;

    bool has_duration = false;
    int32_t mins = 0;
    JsonSpan dur_span{};
    if (json_try_get_property(value, u"windowDurationMins", dur_span) &&
        json_try_get_int32(dur_span, mins) && mins > 0)
    {
        has_duration = true;
    }

    VnAiParsedWindow win{};
    if (parse_quota_window(value, u"usedPercent", u"resetsAt", label_kind,
                           has_duration, mins, has_bucket, bucket_id, win))
    {
        windows.push_back(win);
    }
}

void add_codex_bucket(JsonSpan bucket, std::u16string_view id, std::vector<VnAiParsedWindow>& windows)
{
    if (bucket.kind != JsonKind::Object) return;
    const bool valid_id = is_valid_bucket_id_view(id);
    add_codex_bucket_slot(bucket, u"primary", 0, valid_id, id, windows);
    add_codex_bucket_slot(bucket, u"secondary", 1, valid_id, id, windows);
}

}

VN_EXPORT int32_t __stdcall vn_ai_usage_is_valid_bucket_id(
    const wchar_t* id_ptr, int32_t id_len, uint8_t* out_valid) noexcept
{
    if (out_valid == nullptr || id_len < 0 || (id_len > 0 && id_ptr == nullptr))
        return VN_ERR_INVALID_ARG;
    *out_valid = is_valid_bucket_id_view(make_u16_view(id_ptr, id_len)) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_ai_usage_parse_codex(
    const wchar_t* json_ptr, int32_t json_len,
    VnAiParsedWindow* out_windows, int32_t window_cap, int32_t* out_count) noexcept
{
    try
    {
        if (out_count == nullptr || json_len < 0) return VN_ERR_INVALID_ARG;
        *out_count = 0;
        const std::u16string_view src = make_u16_view(json_ptr, json_len);
        JsonSpan root{};
        if (!json_parse_document(src, root) || root.kind != JsonKind::Object)
            return VN_OK;

        std::vector<VnAiParsedWindow> windows;
        windows.reserve(8);

        JsonSpan buckets{};
        if (json_try_get_property(root, u"rateLimitsByLimitId", buckets) && buckets.kind == JsonKind::Object)
        {
            struct BucketEntry
            {
                std::u16string name;
                JsonSpan value;
            };
            std::vector<BucketEntry> entries;
            entries.reserve(16);

            size_t pos = 1;
            skip_json_ws(buckets.raw, pos);
            if (pos < buckets.raw.size() && buckets.raw[pos] != u'}')
            {
                int32_t count = 0;
                while (pos < buckets.raw.size() && count < 16)
                {
                    skip_json_ws(buckets.raw, pos);
                    std::u16string key;
                    if (!scan_json_string(buckets.raw, pos, &key)) break;
                    skip_json_ws(buckets.raw, pos);
                    if (pos >= buckets.raw.size() || buckets.raw[pos] != u':') break;
                    ++pos;
                    JsonSpan val{};
                    if (!json_parse_value(buckets.raw, pos, val, 1)) break;
                    entries.push_back({std::move(key), val});
                    ++count;
                    skip_json_ws(buckets.raw, pos);
                    if (pos >= buckets.raw.size() || buckets.raw[pos] == u'}') break;
                    if (buckets.raw[pos] != u',') break;
                    ++pos;
                }
            }

            for (const auto& entry : entries)
            {
                if (entry.name == u"codex")
                    add_codex_bucket(entry.value, entry.name, windows);
            }
            for (const auto& entry : entries)
            {
                if (entry.name != u"codex")
                    add_codex_bucket(entry.value, entry.name, windows);
            }
        }

        if (windows.empty())
        {
            JsonSpan legacy{};
            if (json_try_get_property(root, u"rateLimits", legacy))
                add_codex_bucket(legacy, u"codex", windows);
        }

        const int32_t total = static_cast<int32_t>(windows.size());
        *out_count = total;
        if (total == 0) return VN_OK;
        if (out_windows == nullptr || window_cap < total) return VN_ERR_BUFFER_TOO_SMALL;
        std::memcpy(out_windows, windows.data(), static_cast<size_t>(total) * sizeof(VnAiParsedWindow));
        return VN_OK;
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_ai_usage_parse_claude(
    const wchar_t* json_ptr, int32_t json_len,
    VnAiParsedWindow* out_windows, int32_t window_cap, int32_t* out_count) noexcept
{
    try
    {
        if (out_count == nullptr || json_len < 0) return VN_ERR_INVALID_ARG;
        *out_count = 0;
        const std::u16string_view src = make_u16_view(json_ptr, json_len);
        JsonSpan root{};
        if (!json_parse_document(src, root) || root.kind != JsonKind::Object)
            return VN_OK;

        VnAiParsedWindow parsed[2]{};
        int32_t count = 0;

        JsonSpan limits{};
        if (json_try_get_property(root, u"rate_limits", limits) && limits.kind == JsonKind::Object)
        {
            JsonSpan five_hour{};
            if (json_try_get_property(limits, u"five_hour", five_hour) && five_hour.kind == JsonKind::Object)
            {
                if (parse_quota_window(five_hour, u"used_percentage", u"resets_at", 2, true, 300, false, {}, parsed[count]))
                    ++count;
            }

            JsonSpan seven_day{};
            if (json_try_get_property(limits, u"seven_day", seven_day) && seven_day.kind == JsonKind::Object)
            {
                if (parse_quota_window(seven_day, u"used_percentage", u"resets_at", 3, true, 10080, false, {}, parsed[count]))
                    ++count;
            }
        }

        *out_count = count;
        if (count == 0) return VN_OK;
        if (out_windows == nullptr || window_cap < count) return VN_ERR_BUFFER_TOO_SMALL;
        std::memcpy(out_windows, parsed, static_cast<size_t>(count) * sizeof(VnAiParsedWindow));
        return VN_OK;
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

