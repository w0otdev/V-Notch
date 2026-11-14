#include "../vnotch_ext_core.h"

#include <tlhelp32.h>
#include <devpropdef.h>
#include <cfgmgr32.h>
#include <cwchar>

#pragma comment(lib, "cfgmgr32.lib")

namespace
{
    constexpr bool is_dotnet_whitespace(wchar_t c) noexcept
    {
        switch (c)
        {
        case 0x0009:
        case 0x000A:
        case 0x000B:
        case 0x000C:
        case 0x000D:
        case 0x0020:
        case 0x0085:
        case 0x00A0:
        case 0x1680:
        case 0x2000:
        case 0x2001:
        case 0x2002:
        case 0x2003:
        case 0x2004:
        case 0x2005:
        case 0x2006:
        case 0x2007:
        case 0x2008:
        case 0x2009:
        case 0x200A:
        case 0x2028:
        case 0x2029:
        case 0x202F:
        case 0x205F:
        case 0x3000:
            return true;
        default:
            return false;
        }
    }

    constexpr bool is_ascii_letter(wchar_t c) noexcept
    {
        return (c >= L'A' && c <= L'Z') || (c >= L'a' && c <= L'z');
    }

    constexpr bool is_ascii_digit(wchar_t c) noexcept
    {
        return c >= L'0' && c <= L'9';
    }

    constexpr bool is_ascii_hex(wchar_t c) noexcept
    {
        return (c >= L'0' && c <= L'9') ||
               (c >= L'a' && c <= L'f') ||
               (c >= L'A' && c <= L'F');
    }

    constexpr wchar_t to_ascii_lower_w(wchar_t c) noexcept
    {
        return (c >= L'A' && c <= L'Z') ? static_cast<wchar_t>(c + (L'a' - L'A')) : c;
    }

    constexpr uint8_t to_ascii_lower_b(uint8_t c) noexcept
    {
        return (c >= 'A' && c <= 'Z') ? static_cast<uint8_t>(c + ('a' - 'A')) : c;
    }

    constexpr bool equals_ascii_icase_w(std::wstring_view a, std::wstring_view b) noexcept
    {
        if (a.size() != b.size()) return false;
        for (size_t i = 0; i < a.size(); ++i)
        {
            if (to_ascii_lower_w(a[i]) != to_ascii_lower_w(b[i]))
                return false;
        }
        return true;
    }

    constexpr bool ends_with_ascii_icase_w(std::wstring_view text, std::wstring_view suffix) noexcept
    {
        if (text.size() < suffix.size()) return false;
        return equals_ascii_icase_w(text.substr(text.size() - suffix.size()), suffix);
    }

    inline float clamp_f32_preserve_nan(float v, float lo, float hi) noexcept
    {
        if (std::isnan(v)) return v;
        if (v < lo) return lo;
        if (v > hi) return hi;
        return v;
    }

    constexpr int32_t parse_digits(std::wstring_view sv) noexcept
    {
        int32_t acc = 0;
        for (wchar_t ch : sv)
        {
            if (!is_ascii_digit(ch)) return -1;
            acc = acc * 10 + static_cast<int32_t>(ch - L'0');
        }
        return acc;
    }

    constexpr int32_t days_in_month(int32_t year, int32_t month) noexcept
    {
        switch (month)
        {
        case 1:
        case 3:
        case 5:
        case 7:
        case 8:
        case 10:
        case 12:
            return 31;
        case 4:
        case 6:
        case 9:
        case 11:
            return 30;
        case 2:
        {
            const bool leap = ((year % 4 == 0) && (year % 100 != 0)) || (year % 400 == 0);
            return leap ? 29 : 28;
        }
        default:
            return 0;
        }
    }

    constexpr bool is_html_space(wchar_t c) noexcept
    {
        return c == L' ' || c == L'\t' || c == L'\r' || c == L'\n' || c == L'\f';
    }
}

VN_EXPORT int32_t __stdcall vn_smartcrop_select_single_prominent_person(
    const VnCoreDetection* detections,
    int32_t count,
    VnCoreDetection* out_person,
    uint8_t* out_found) noexcept
{
    if (!out_person || !out_found || count < 0 || (count > 0 && !detections))
        return VN_ERR_INVALID_ARG;

    *out_person = {};
    *out_found = 0;
    if (count == 0)
        return VN_OK;

    bool has_person = false;
    float best_confidence = 0.0f;
    for (int32_t i = 0; i < count; ++i)
    {
        if (detections[i].class_id != 0)
            continue;
        const float c = detections[i].confidence;
        if (!has_person)
        {
            best_confidence = c;
            has_person = true;
            if (std::isnan(c))
                break;
        }
        else if (std::isnan(c))
        {
            best_confidence = c;
            break;
        }
        else if (c > best_confidence)
        {
            best_confidence = c;
        }
    }

    if (!has_person)
        return VN_OK;

    const float min_confidence = best_confidence * 0.5f;
    int32_t match_count = 0;
    VnCoreDetection chosen{};
    for (int32_t i = 0; i < count; ++i)
    {
        if (detections[i].class_id != 0)
            continue;
        const float c = detections[i].confidence;
        if (std::isnan(c) || std::isnan(min_confidence) || !(c >= min_confidence))
            continue;
        if (++match_count > 1)
            return VN_OK;
        chosen = detections[i];
    }

    if (match_count == 1)
    {
        *out_person = chosen;
        *out_found = 1;
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_smartcrop_translate_crop_detections(
    const VnCoreDetection* src,
    int32_t src_count,
    int32_t crop_x,
    int32_t crop_y,
    int32_t crop_width,
    int32_t crop_height,
    VnCoreDetection* out_dst,
    int32_t dst_cap,
    int32_t* out_written) noexcept
{
    if (!out_written || src_count < 0 || dst_cap < 0 ||
        (src_count > 0 && !src) || (dst_cap > 0 && !out_dst))
    {
        return VN_ERR_INVALID_ARG;
    }

    *out_written = 0;
    if (src_count == 0 || crop_width <= 0 || crop_height <= 0)
        return VN_OK;

    const float fx = static_cast<float>(crop_x);
    const float fy = static_cast<float>(crop_y);
    const float fw = static_cast<float>(crop_width);
    const float fh = static_cast<float>(crop_height);

    int32_t written = 0;
    for (int32_t i = 0; i < src_count; ++i)
    {
        VnCoreDetection t = src[i];
        t.x1 = clamp_f32_preserve_nan(t.x1 - fx, 0.0f, fw);
        t.x2 = clamp_f32_preserve_nan(t.x2 - fx, 0.0f, fw);
        t.y1 = clamp_f32_preserve_nan(t.y1 - fy, 0.0f, fh);
        t.y2 = clamp_f32_preserve_nan(t.y2 - fy, 0.0f, fh);
        if (!std::isnan(t.x1) && !std::isnan(t.x2) && !std::isnan(t.y1) && !std::isnan(t.y2) &&
            t.x2 > t.x1 && t.y2 > t.y1)
        {
            if (written >= dst_cap)
            {
                *out_written = written;
                return VN_ERR_BUFFER_TOO_SMALL;
            }
            out_dst[written++] = t;
        }
    }

    *out_written = written;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_smartcrop_partition_hybrid_candidates(
    const VnCoreDetection* src,
    int32_t src_count,
    VnCoreDetection* out_candidates,
    int32_t cap,
    int32_t* out_person_count,
    int32_t* out_object_count,
    int32_t* out_kept_count,
    uint8_t* out_is_person_mode) noexcept
{
    if (!out_person_count || !out_object_count || !out_kept_count || !out_is_person_mode ||
        src_count < 0 || cap < 0 || (src_count > 0 && !src) || (cap > 0 && !out_candidates))
    {
        return VN_ERR_INVALID_ARG;
    }

    *out_person_count = 0;
    *out_object_count = 0;
    *out_kept_count = 0;
    *out_is_person_mode = 0;

    if (src_count == 0)
        return VN_OK;
    if (cap < src_count)
        return VN_ERR_BUFFER_TOO_SMALL;

    int32_t person_count = 0;
    for (int32_t i = 0; i < src_count; ++i)
    {
        if (src[i].class_id == 0)
            out_candidates[person_count++] = src[i];
    }
    const int32_t object_count = src_count - person_count;
    *out_person_count = person_count;
    *out_object_count = object_count;

    if (person_count >= 1)
    {
        float best_confidence = out_candidates[0].confidence;
        if (!std::isnan(best_confidence))
        {
            for (int32_t i = 1; i < person_count; ++i)
            {
                const float c = out_candidates[i].confidence;
                if (std::isnan(c))
                {
                    best_confidence = c;
                    break;
                }
                if (c > best_confidence)
                    best_confidence = c;
            }
        }

        const float min_confidence = best_confidence * 0.5f;
        int32_t kept = 0;
        for (int32_t i = 0; i < person_count; ++i)
        {
            const float c = out_candidates[i].confidence;
            // Exact IEEE-754 parity with C# `!(c < minConfidence)` (true when NaN)
            const bool less_than = !std::isnan(c) && !std::isnan(min_confidence) && (c < min_confidence);
            if (!less_than)
                out_candidates[kept++] = out_candidates[i];
        }

        *out_kept_count = kept;
        *out_is_person_mode = 1;
        return VN_OK;
    }

    if (object_count > 0)
    {
        int32_t written = 0;
        for (int32_t i = 0; i < src_count; ++i)
        {
            if (src[i].class_id != 0)
                out_candidates[written++] = src[i];
        }
        *out_kept_count = written;
    }

    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_smartcrop_select_portrait_face(
    const VnCoreRect* face_boxes,
    int32_t face_count,
    double scale,
    const VnCoreDetection* person,
    VnCoreDetection* out_best_face,
    uint8_t* out_found) noexcept
{
    if (!person || !out_best_face || !out_found || face_count < 0 || (face_count > 0 && !face_boxes))
        return VN_ERR_INVALID_ARG;

    *out_best_face = {};
    *out_found = 0;
    if (face_count == 0 || !(scale > 0.0) || !std::isfinite(scale))
        return VN_OK;

    VnCoreDetection best{};
    float best_area = 0.0f;
    bool has_best = false;

    for (int32_t i = 0; i < face_count; ++i)
    {
        const VnCoreRect& box = face_boxes[i];
        VnCoreDetection cand{};
        cand.x1 = static_cast<float>(static_cast<double>(box.x) / scale);
        cand.y1 = static_cast<float>(static_cast<double>(box.y) / scale);
        cand.x2 = static_cast<float>(static_cast<double>(static_cast<int64_t>(box.x) + box.width) / scale);
        cand.y2 = static_cast<float>(static_cast<double>(static_cast<int64_t>(box.y) + box.height) / scale);

        const float cx = (cand.x1 + cand.x2) / 2.0f;
        const float cy = (cand.y1 + cand.y2) / 2.0f;
        const float area = (cand.x2 - cand.x1) * (cand.y2 - cand.y1);

        if (std::isnan(cx) || std::isnan(cy) || std::isnan(area))
            continue;
        if (cx < person->x1 || cx > person->x2 || cy < person->y1 || cy > person->y2 || area <= best_area)
            continue;

        best = cand;
        best_area = area;
        has_best = true;
    }

    if (has_best)
    {
        *out_best_face = best;
        *out_found = 1;
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_anim_clock_try_advance(
    VnCoreAnimClockState* io_state,
    int64_t now_tick,
    int64_t stopwatch_freq,
    double target_fps,
    double* out_seconds,
    uint8_t* out_advanced) noexcept
{
    if (!io_state || !out_seconds || !out_advanced || stopwatch_freq <= 0)
        return VN_ERR_INVALID_ARG;

    *out_seconds = 0.0;
    *out_advanced = 0;

    target_fps = std::isfinite(target_fps) ? std::max(1.0, target_fps) : 60.0;
    const double freq = static_cast<double>(stopwatch_freq);
    const double interval = freq / target_fps;

    if (io_state->target_fps != target_fps)
    {
        io_state->target_fps = target_fps;
        io_state->next_tick = static_cast<double>(io_state->last_tick) + interval;
    }

    if (now_tick <= io_state->last_tick || static_cast<double>(now_tick) + freq * 0.0001 < io_state->next_tick)
        return VN_OK;

    const double elapsed = static_cast<double>(now_tick - io_state->last_tick) / freq;
    *out_seconds = std::clamp(elapsed, 0.0, 0.05);
    io_state->last_tick = now_tick;
    io_state->next_tick += std::max(1.0, std::floor((static_cast<double>(now_tick) - io_state->next_tick) / interval) + 1.0) * interval;
    *out_advanced = 1;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_appnotify_compute_icon_crop_bounds(
    const uint8_t* bgra,
    int32_t width,
    int32_t height,
    int32_t stride,
    uint8_t alpha_threshold,
    VnCoreRect* out_crop,
    uint8_t* out_has_opaque,
    uint8_t* out_should_crop) noexcept
{
    if (!out_crop || !out_has_opaque || !out_should_crop)
        return VN_ERR_INVALID_ARG;

    *out_crop = {};
    *out_has_opaque = 0;
    *out_should_crop = 0;

    if (!bgra || width <= 0 || height <= 0 || stride < width * 4)
        return VN_ERR_INVALID_ARG;

    int32_t left = width;
    int32_t top = height;
    int32_t right = -1;
    int32_t bottom = -1;

    for (int32_t y = 0; y < height; ++y)
    {
        const uint8_t* row = bgra + static_cast<size_t>(y) * static_cast<size_t>(stride);
        for (int32_t x = 0; x < width; ++x)
        {
            if (row[x * 4 + 3] <= alpha_threshold)
                continue;
            if (x < left) left = x;
            if (y < top) top = y;
            if (x > right) right = x;
            if (y > bottom) bottom = y;
        }
    }

    if (right < left)
        return VN_OK;

    *out_has_opaque = 1;
    const int32_t max_span = std::max(right - left + 1, bottom - top + 1);
    const int32_t padding = std::max(1, static_cast<int32_t>(std::ceil(static_cast<double>(max_span) * 0.04)));

    left = std::max(0, left - padding);
    top = std::max(0, top - padding);
    right = std::min(width - 1, right + padding);
    bottom = std::min(height - 1, bottom + padding);

    out_crop->x = left;
    out_crop->y = top;
    out_crop->width = right - left + 1;
    out_crop->height = bottom - top + 1;
    *out_should_crop = (left == 0 && top == 0 && right == width - 1 && bottom == height - 1) ? 0 : 1;
    return VN_OK;
}



VN_EXPORT int32_t __stdcall vn_lyrics_provider_order_parse(
    const wchar_t* raw,
    int32_t raw_len,
    uint8_t is_null,
    uint8_t* out_indices,
    int32_t cap,
    int32_t* out_count) noexcept
{
    if (!out_indices || !out_count || raw_len < 0 || (raw_len > 0 && !raw))
        return VN_ERR_INVALID_ARG;
    if (cap < 6)
    {
        *out_count = 6;
        return VN_ERR_BUFFER_TOO_SMALL;
    }

    static constexpr std::wstring_view kProviders[6] = {
        L"AMLL TTML DB",
        L"NetEase",
        L"Kugou",
        L"QQ Music",
        L"LRCLIB",
        L"lrc mux"
    };

    static constexpr std::wstring_view kLegacy1 = L"LRCLIB,NetEase,lrc mux";
    static constexpr std::wstring_view kLegacy2 = L"NetEase,Kugou,QQ Music,LRCLIB,lrc mux";

    const std::wstring_view input = (!is_null && raw && raw_len > 0)
        ? std::wstring_view(raw, static_cast<size_t>(raw_len))
        : std::wstring_view{};

    if (!is_null && (input == kLegacy1 || input == kLegacy2))
    {
        for (uint8_t i = 0; i < 6; ++i)
            out_indices[i] = i;
        *out_count = 6;
        return VN_OK;
    }

    uint32_t seen_mask = 0;
    int32_t count = 0;

    if (!input.empty())
    {
        size_t start = 0;
        while (start <= input.size())
        {
            const size_t comma = input.find(L',', start);
            const size_t end = (comma == std::wstring_view::npos) ? input.size() : comma;

            size_t tok_start = start;
            size_t tok_end = end;
            while (tok_start < tok_end && is_dotnet_whitespace(input[tok_start]))
                ++tok_start;
            while (tok_end > tok_start && is_dotnet_whitespace(input[tok_end - 1]))
                --tok_end;

            const std::wstring_view token = input.substr(tok_start, tok_end - tok_start);
            for (uint8_t k = 0; k < 6; ++k)
            {
                if (token == kProviders[k])
                {
                    if ((seen_mask & (1u << k)) == 0)
                    {
                        seen_mask |= (1u << k);
                        out_indices[count++] = k;
                    }
                    break;
                }
            }

            if (comma == std::wstring_view::npos)
                break;
            start = comma + 1;
        }
    }

    for (uint8_t k = 0; k < 6; ++k)
    {
        if ((seen_mask & (1u << k)) == 0)
            out_indices[count++] = k;
    }

    *out_count = count;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_soundcloud_extract_anchor_hrefs(
    const wchar_t* html,
    int32_t html_len,
    VnCoreTextSlice* out_slices,
    int32_t slice_cap,
    int32_t* out_total_count) noexcept
{
    if (!out_total_count || html_len < 0 || slice_cap < 0 ||
        (html_len > 0 && !html) || (slice_cap > 0 && !out_slices))
    {
        return VN_ERR_INVALID_ARG;
    }

    *out_total_count = 0;
    if (html_len < 4)
        return VN_OK;

    int32_t total = 0;
    int32_t i = 0;
    while (i + 3 < html_len)
    {
        if (html[i] != L'<' || to_ascii_lower_w(html[i + 1]) != L'a' || !is_html_space(html[i + 2]))
        {
            ++i;
            continue;
        }

        i += 3;
        bool found_href_in_tag = false;
        while (i < html_len && html[i] != L'>')
        {
            while (i < html_len && (is_html_space(html[i]) || html[i] == L'/'))
            {
                if (html[i] == L'>') break;
                ++i;
            }
            if (i >= html_len || html[i] == L'>')
                break;

            const int32_t attr_start = i;
            while (i < html_len && !is_html_space(html[i]) &&
                   html[i] != L'=' && html[i] != L'>' && html[i] != L'"' && html[i] != L'\'')
            {
                ++i;
            }
            const int32_t attr_len = i - attr_start;

            while (i < html_len && is_html_space(html[i]))
                ++i;

            if (i < html_len && html[i] == L'=')
            {
                ++i;
                while (i < html_len && is_html_space(html[i]))
                    ++i;

                int32_t val_start = i;
                int32_t val_len = 0;
                if (i < html_len && (html[i] == L'"' || html[i] == L'\''))
                {
                    const wchar_t quote = html[i++];
                    val_start = i;
                    while (i < html_len && html[i] != quote)
                        ++i;
                    val_len = i - val_start;
                    if (i < html_len && html[i] == quote)
                        ++i;
                }
                else
                {
                    val_start = i;
                    while (i < html_len && !is_html_space(html[i]) && html[i] != L'>')
                        ++i;
                    val_len = i - val_start;
                }

                if (!found_href_in_tag &&
                    attr_len == 4 &&
                    equals_ascii_icase_w(std::wstring_view(html + attr_start, 4), L"href"))
                {
                    found_href_in_tag = true;
                    if (val_len > 0)
                    {
                        if (total < slice_cap && out_slices)
                        {
                            out_slices[total].offset = val_start;
                            out_slices[total].length = val_len;
                        }
                        ++total;
                    }
                }
            }
            else if (attr_len == 0 && i < html_len && html[i] != L'>')
            {
                // Skip stray quote or delimiter inside malformed markup
                ++i;
            }
        }

        if (i < html_len && html[i] == L'>')
            ++i;
    }

    *out_total_count = total;
    return (total > slice_cap) ? VN_ERR_BUFFER_TOO_SMALL : VN_OK;
}

VN_EXPORT int32_t __stdcall vn_spotify_canvas_may_be_mp4_utf8(
    const uint8_t* utf8,
    int32_t len,
    uint8_t* out_maybe) noexcept
{
    if (!out_maybe || len < 0 || (len > 0 && !utf8))
        return VN_ERR_INVALID_ARG;

    *out_maybe = 0;
    // Minimum valid URL is "https://scdn.co/.mp4" (20 bytes)
    if (len < 20)
        return VN_OK;

    int32_t start = 0;
    while (start < len && utf8[start] <= 0x20)
        ++start;

    if (len - start < 20)
        return VN_OK;

    // Must begin with ASCII "https:" (case-insensitive) if ASCII-prefixed
    if (utf8[start] < 0x80)
    {
        static constexpr uint8_t kPrefix[6] = { 'h', 't', 't', 'p', 's', ':' };
        for (int32_t i = 0; i < 6; ++i)
        {
            if (to_ascii_lower_b(utf8[start + i]) != kPrefix[i])
                return VN_OK;
        }
    }

    *out_maybe = 1;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_spotify_canvas_is_trusted_uri_parts(
    const wchar_t* scheme,
    int32_t scheme_len,
    const wchar_t* host,
    int32_t host_len,
    const wchar_t* abs_path,
    int32_t abs_path_len,
    uint8_t* out_trusted) noexcept
{
    if (!out_trusted || scheme_len < 0 || host_len < 0 || abs_path_len < 0 ||
        (scheme_len > 0 && !scheme) || (host_len > 0 && !host) || (abs_path_len > 0 && !abs_path))
    {
        return VN_ERR_INVALID_ARG;
    }

    *out_trusted = 0;
    const std::wstring_view scheme_sv(scheme ? scheme : L"", static_cast<size_t>(scheme_len));
    const std::wstring_view host_sv(host ? host : L"", static_cast<size_t>(host_len));
    const std::wstring_view path_sv(abs_path ? abs_path : L"", static_cast<size_t>(abs_path_len));

    if (!equals_ascii_icase_w(scheme_sv, L"https"))
        return VN_OK;

    const bool trusted_host = equals_ascii_icase_w(host_sv, L"scdn.co") ||
                              ends_with_ascii_icase_w(host_sv, L".scdn.co");
    if (!trusted_host)
        return VN_OK;

    if (!ends_with_ascii_icase_w(path_sv, L".mp4"))
        return VN_OK;

    *out_trusted = 1;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_settings_is_generated_backup_name(
    const wchar_t* name,
    int32_t len,
    uint8_t* out_valid) noexcept
{
    if (!out_valid || len < 0 || (len > 0 && !name))
        return VN_ERR_INVALID_ARG;

    *out_valid = 0;
    static constexpr std::wstring_view kPrefix = L"settings.bak-";
    static constexpr std::wstring_view kSuffix = L".json";

    if (len != 37 && len != 70)
        return VN_OK;

    const std::wstring_view full(name, static_cast<size_t>(len));
    if (!full.starts_with(kPrefix) || !full.ends_with(kSuffix))
        return VN_OK;

    std::wstring_view stamp = full.substr(kPrefix.size(), full.size() - kPrefix.size() - kSuffix.size());
    if (stamp.size() == 52)
    {
        if (stamp[19] != L'-')
            return VN_OK;
        for (size_t i = 20; i < 52; ++i)
        {
            if (!is_ascii_hex(stamp[i]))
                return VN_OK;
        }
        stamp = stamp.substr(0, 19);
    }

    if (stamp.size() != 19 || stamp[8] != L'-' || stamp[15] != L'-')
        return VN_OK;

    const int32_t year = parse_digits(stamp.substr(0, 4));
    const int32_t month = parse_digits(stamp.substr(4, 2));
    const int32_t day = parse_digits(stamp.substr(6, 2));
    const int32_t hour = parse_digits(stamp.substr(9, 2));
    const int32_t minute = parse_digits(stamp.substr(11, 2));
    const int32_t second = parse_digits(stamp.substr(13, 2));
    const int32_t ms = parse_digits(stamp.substr(16, 3));

    if (year < 1 || year > 9999 || month < 1 || month > 12)
        return VN_OK;

    const int32_t max_day = days_in_month(year, month);
    if (day < 1 || day > max_day)
        return VN_OK;
    if (hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 59 || ms < 0 || ms > 999)
        return VN_OK;

    *out_valid = 1;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_file_icon_can_probe_local_path(
    const wchar_t* path,
    int32_t len,
    uint8_t* out_can_probe) noexcept
{
    if (!out_can_probe || len < 0 || (len > 0 && !path))
        return VN_ERR_INVALID_ARG;

    *out_can_probe = 0;
    if (len == 0)
        return VN_OK;

    int32_t start = 0;
    int32_t end = len;
    while (start < end && is_dotnet_whitespace(path[start]))
        ++start;
    while (end > start && is_dotnet_whitespace(path[end - 1]))
        --end;

    const int32_t trimmed_len = end - start;
    if (trimmed_len < 3)
        return VN_OK;

    for (int32_t i = start; i < end; ++i)
    {
        if (path[i] == L'\0')
            return VN_OK;
    }

    if (!is_ascii_letter(path[start]) || path[start + 1] != L':' ||
        (path[start + 2] != L'\\' && path[start + 2] != L'/'))
    {
        return VN_OK;
    }

    *out_can_probe = 1;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_perf_diag_sample_thread_and_handle_counts(
    int64_t now_ticks,
    int64_t* io_last_thread_ticks,
    int32_t* io_cached_thread_count,
    int64_t stopwatch_freq,
    int32_t* out_thread_count,
    int32_t* out_handle_count) noexcept
{
    if (!io_last_thread_ticks || !io_cached_thread_count ||
        !out_thread_count || !out_handle_count || stopwatch_freq <= 0)
    {
        return VN_ERR_INVALID_ARG;
    }

    const bool refresh_threads =
        (*io_last_thread_ticks == 0) ||
        (static_cast<double>(now_ticks - *io_last_thread_ticks) / static_cast<double>(stopwatch_freq) >= 2.0);

    if (refresh_threads)
    {
        const HANDLE snap = ::CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snap != INVALID_HANDLE_VALUE)
        {
            const DWORD pid = ::GetCurrentProcessId();
            THREADENTRY32 te{};
            te.dwSize = sizeof(THREADENTRY32);
            int32_t count = 0;
            if (::Thread32First(snap, &te))
            {
                do
                {
                    if (te.dwSize >= FIELD_OFFSET(THREADENTRY32, th32OwnerProcessID) + sizeof(DWORD) &&
                        te.th32OwnerProcessID == pid)
                    {
                        ++count;
                    }
                    te.dwSize = sizeof(THREADENTRY32);
                } while (::Thread32Next(snap, &te));
            }
            ::CloseHandle(snap);
            if (count > 0)
                *io_cached_thread_count = count;
        }
        *io_last_thread_ticks = now_ticks;
    }

    *out_thread_count = *io_cached_thread_count;

    DWORD h_count = 0;
    *out_handle_count = ::GetProcessHandleCount(::GetCurrentProcess(), &h_count)
        ? static_cast<int32_t>(h_count)
        : 0;

    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_perf_diag_sample_global_memory(
    uint64_t* out_ram_used,
    uint64_t* out_ram_total,
    uint64_t* out_ram_avail,
    double* out_ram_percent) noexcept
{
    if (!out_ram_used || !out_ram_total || !out_ram_avail || !out_ram_percent)
        return VN_ERR_INVALID_ARG;

    *out_ram_used = 0;
    *out_ram_total = 0;
    *out_ram_avail = 0;
    *out_ram_percent = 0.0;

    MEMORYSTATUSEX ms{};
    ms.dwLength = sizeof(MEMORYSTATUSEX);
    if (!::GlobalMemoryStatusEx(&ms))
        return VN_ERR_FAIL;

    *out_ram_total = ms.ullTotalPhys;
    *out_ram_avail = ms.ullAvailPhys;
    *out_ram_used = (ms.ullTotalPhys > ms.ullAvailPhys) ? (ms.ullTotalPhys - ms.ullAvailPhys) : 0ULL;
    *out_ram_percent = std::clamp(static_cast<double>(ms.dwMemoryLoad), 0.0, 100.0);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_bluetooth_battery_parse_percentage(
    const uint8_t* buf,
    int32_t len,
    int32_t* out_percentage) noexcept
{
    if (!out_percentage || len < 0 || (len > 0 && !buf))
        return VN_ERR_INVALID_ARG;

    *out_percentage = (len == 1 && buf[0] <= 100) ? static_cast<int32_t>(buf[0]) : -1;
    return VN_OK;
}

namespace
{
    constexpr DEVPROPKEY kPropBatteryLevel = {
        { 0x104EA319, 0x6EE2, 0x4701, { 0xBD, 0x47, 0x8D, 0xDB, 0xF4, 0x25, 0xBB, 0xE5 } }, 2
    };
    constexpr DEVPROPKEY kPropBatteryLife = {
        { 0x49CD1F76, 0x5626, 0x4B17, { 0xA4, 0xE8, 0x18, 0xB4, 0xAA, 0x1A, 0x22, 0x13 } }, 10
    };
    constexpr DEVPROPKEY kPropFriendlyName = {
        { 0xA45C254E, 0xDF1C, 0x4EFD, { 0x80, 0x20, 0x67, 0xD1, 0x46, 0xA8, 0x50, 0xE0 } }, 14
    };
    constexpr DEVPROPKEY kPropDescription = {
        { 0xA45C254E, 0xDF1C, 0x4EFD, { 0x80, 0x20, 0x67, 0xD1, 0x46, 0xA8, 0x50, 0xE0 } }, 2
    };
    constexpr DEVPROPKEY kPropContainerId = {
        { 0x8C7ED206, 0x3F8A, 0x4827, { 0xB3, 0xAB, 0xAE, 0x9E, 0x1F, 0xAE, 0xFC, 0x6C } }, 2
    };

    bool equals_ordinal_icase(std::wstring_view a, std::wstring_view b) noexcept
    {
        if (a.size() != b.size()) return false;
        if (a.empty()) return true;
        return ::CompareStringOrdinal(
            a.data(), static_cast<int>(a.size()),
            b.data(), static_cast<int>(b.size()),
            TRUE) == CSTR_EQUAL;
    }

    bool enumerate_id_multi_sz(const wchar_t* enumerator, bool present_only, std::vector<wchar_t>& out_buf)
    {
        out_buf.clear();
        const ULONG flags = CM_GETIDLIST_FILTER_ENUMERATOR | (present_only ? CM_GETIDLIST_FILTER_PRESENT : 0u);
        for (int attempt = 0; attempt < 3; ++attempt)
        {
            ULONG size = 0;
            if (::CM_Get_Device_ID_List_SizeW(&size, enumerator, flags) != CR_SUCCESS || size < 2 || size > 1048576u)
                return false;

            out_buf.assign(static_cast<size_t>(size) + 1u, L'\0');
            const CONFIGRET cr = ::CM_Get_Device_ID_ListW(enumerator, out_buf.data(), size, flags);
            if (cr == CR_SUCCESS)
                return true;
            if (cr != CR_BUFFER_SMALL)
                break;
        }
        out_buf.clear();
        return false;
    }

    void split_multi_sz(std::vector<wchar_t>& buf, std::vector<wchar_t*>& out_ptrs)
    {
        out_ptrs.clear();
        size_t i = 0;
        const size_t n = buf.size();
        while (i < n)
        {
            while (i < n && buf[i] == L'\0')
                ++i;
            if (i >= n)
                break;
            wchar_t* start = buf.data() + i;
            while (i < n && buf[i] != L'\0')
                ++i;
            if (i < n && start[0] != L'\0')
                out_ptrs.push_back(start);
        }
    }

    int32_t read_devnode_percentage(DEVINST node, const DEVPROPKEY& key) noexcept
    {
        uint8_t prop_buf[16]{};
        ULONG cb = sizeof(prop_buf);
        DEVPROPTYPE prop_type = 0;
        if (::CM_Get_DevNode_PropertyW(node, &key, &prop_type, prop_buf, &cb, 0) != CR_SUCCESS ||
            prop_type != DEVPROP_TYPE_BYTE || cb != 1)
        {
            return -1;
        }
        return prop_buf[0] <= 100 ? static_cast<int32_t>(prop_buf[0]) : -1;
    }

    bool read_devnode_string(DEVINST node, const DEVPROPKEY& key, wchar_t* out_str, size_t max_chars, uint16_t* out_len) noexcept
    {
        uint8_t prop_buf[4096]{};
        ULONG cb = sizeof(prop_buf);
        DEVPROPTYPE prop_type = 0;
        if (::CM_Get_DevNode_PropertyW(node, &key, &prop_type, prop_buf, &cb, 0) != CR_SUCCESS ||
            prop_type != DEVPROP_TYPE_STRING || cb > sizeof(prop_buf) || (cb & 1u) != 0)
        {
            return false;
        }

        const wchar_t* wptr = reinterpret_cast<const wchar_t*>(prop_buf);
        size_t wlen = static_cast<size_t>(cb / sizeof(wchar_t));
        while (wlen > 0 && wptr[wlen - 1] == L'\0')
            --wlen;

        bool any_non_ws = false;
        for (size_t i = 0; i < wlen; ++i)
        {
            if (!is_dotnet_whitespace(wptr[i]))
            {
                any_non_ws = true;
                break;
            }
        }
        if (!any_non_ws)
            return false;

        const size_t copy_len = std::min(wlen, max_chars - 1);
        std::memcpy(out_str, wptr, copy_len * sizeof(wchar_t));
        out_str[copy_len] = L'\0';
        *out_len = static_cast<uint16_t>(copy_len);
        return true;
    }

    bool is_empty_guid(const GUID& g) noexcept
    {
        static constexpr GUID kZero{};
        return std::memcmp(&g, &kZero, sizeof(GUID)) == 0;
    }
}

VN_EXPORT int32_t __stdcall vn_bluetooth_battery_read_devices(
    VnCoreBtBatteryDevice* out_devices,
    int32_t cap,
    int32_t* out_total_count) noexcept
{
    if (!out_total_count || cap < 0 || (cap > 0 && !out_devices))
        return VN_ERR_INVALID_ARG;

    *out_total_count = 0;

    try
    {
        static constexpr const wchar_t* kEnumerators[3] = {
            L"BTHENUM",
            L"BTHLE",
            L"BTHLEDEVICE"
        };

        std::vector<VnCoreBtBatteryDevice> collected;
        collected.reserve(16);

        std::vector<wchar_t> present_buf;
        std::vector<wchar_t*> present_ids;
        std::vector<wchar_t> all_buf;
        std::vector<wchar_t*> all_ids;

        for (const wchar_t* enumerator : kEnumerators)
        {
            present_ids.clear();
            if (enumerate_id_multi_sz(enumerator, true, present_buf))
                split_multi_sz(present_buf, present_ids);

            all_ids.clear();
            if (!enumerate_id_multi_sz(enumerator, false, all_buf))
                continue;
            split_multi_sz(all_buf, all_ids);

            for (wchar_t* instance_id : all_ids)
            {
                DEVINST node = 0;
                if (::CM_Locate_DevNodeW(&node, instance_id, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS)
                    continue;

                int32_t percentage = read_devnode_percentage(node, kPropBatteryLevel);
                if (percentage < 0)
                    percentage = read_devnode_percentage(node, kPropBatteryLife);
                if (percentage < 0)
                    continue;

                VnCoreBtBatteryDevice entry{};
                entry.percentage = percentage;

                GUID container_guid{};
                ULONG guid_cb = sizeof(container_guid);
                DEVPROPTYPE guid_type = 0;
                const std::wstring_view inst_sv(instance_id);

                if (::CM_Get_DevNode_PropertyW(
                        node, &kPropContainerId, &guid_type,
                        reinterpret_cast<PBYTE>(&container_guid), &guid_cb, 0) == CR_SUCCESS &&
                    guid_type == DEVPROP_TYPE_GUID && guid_cb == sizeof(GUID) && !is_empty_guid(container_guid))
                {
                    const int written = ::swprintf_s(
                        entry.id,
                        L"bluetooth:%08lx-%04x-%04x-%02x%02x-%02x%02x%02x%02x%02x%02x",
                        container_guid.Data1,
                        static_cast<unsigned int>(container_guid.Data2),
                        static_cast<unsigned int>(container_guid.Data3),
                        static_cast<unsigned int>(container_guid.Data4[0]),
                        static_cast<unsigned int>(container_guid.Data4[1]),
                        static_cast<unsigned int>(container_guid.Data4[2]),
                        static_cast<unsigned int>(container_guid.Data4[3]),
                        static_cast<unsigned int>(container_guid.Data4[4]),
                        static_cast<unsigned int>(container_guid.Data4[5]),
                        static_cast<unsigned int>(container_guid.Data4[6]),
                        static_cast<unsigned int>(container_guid.Data4[7]));
                    entry.id_len = written > 0 ? static_cast<uint16_t>(written) : 0;
                }
                else
                {
                    const size_t copy_len = std::min(inst_sv.size(), std::size(entry.id) - 1);
                    std::memcpy(entry.id, inst_sv.data(), copy_len * sizeof(wchar_t));
                    entry.id[copy_len] = L'\0';
                    entry.id_len = static_cast<uint16_t>(copy_len);
                }

                uint16_t name_len = 0;
                if (!read_devnode_string(node, kPropFriendlyName, entry.name, std::size(entry.name), &name_len) &&
                    !read_devnode_string(node, kPropDescription, entry.name, std::size(entry.name), &name_len))
                {
                    const size_t copy_len = std::min(inst_sv.size(), std::size(entry.name) - 1);
                    std::memcpy(entry.name, inst_sv.data(), copy_len * sizeof(wchar_t));
                    entry.name[copy_len] = L'\0';
                    name_len = static_cast<uint16_t>(copy_len);
                }
                entry.name_len = name_len;

                bool is_present = false;
                for (const wchar_t* pid : present_ids)
                {
                    if (equals_ordinal_icase(inst_sv, std::wstring_view(pid)))
                    {
                        is_present = true;
                        break;
                    }
                }

                ULONG dev_status = 0;
                ULONG dev_problem = 0;
                constexpr ULONG kDisconnectedOrProblem = 0x02000000u | 0x400u;
                const bool connected = is_present &&
                    ::CM_Get_DevNode_Status(&dev_status, &dev_problem, node, 0) == CR_SUCCESS &&
                    (dev_status & kDisconnectedOrProblem) == 0;
                entry.is_connected = connected ? 1 : 0;

                const std::wstring_view entry_id_sv(entry.id, entry.id_len);
                size_t existing_idx = collected.size();
                for (size_t k = 0; k < collected.size(); ++k)
                {
                    if (equals_ordinal_icase(std::wstring_view(collected[k].id, collected[k].id_len), entry_id_sv))
                    {
                        existing_idx = k;
                        break;
                    }
                }

                if (existing_idx == collected.size())
                {
                    collected.push_back(entry);
                }
                else if (collected[existing_idx].is_connected == 0 && connected)
                {
                    collected[existing_idx] = entry;
                }
            }
        }

        const int32_t total = static_cast<int32_t>(collected.size());
        *out_total_count = total;
        if (total == 0)
            return VN_OK;
        if (total > cap)
            return VN_ERR_BUFFER_TOO_SMALL;

        std::memcpy(out_devices, collected.data(), static_cast<size_t>(total) * sizeof(VnCoreBtBatteryDevice));
        return VN_OK;
    }
    catch (...)
    {
        *out_total_count = 0;
        return VN_ERR_FAIL;
    }
}
