#include "vnotch_common.h"

namespace vnotch::services
{

class MediaSessionVolumeService final
{
    static constexpr bool IsExeNameChar(char16_t ch) noexcept
    {
        return vnotch::is_ascii_alpha(ch) || vnotch::is_ascii_digit(ch) || ch == u'_' || ch == u'-';
    }

    static bool ContainsEitherWay(std::u16string_view left, std::u16string_view right) noexcept
    {
        left = vnotch::trim_u16(left);
        right = vnotch::trim_u16(right);
        if (left.empty() || right.empty())
        {
            return false;
        }
        return vnotch::contains_icase_win(left, right) || vnotch::contains_icase_win(right, left);
    }

    static void AppendCandidate(std::u16string& out_list, std::u16string_view name)
    {
        if (name.empty()) return;
        if (!out_list.empty())
        {
            out_list.push_back(u';');
        }
        out_list.append(name.data(), name.size());
    }

public:
    static bool SessionMatchesSourceAppId(
        std::u16string_view display_name,
        std::u16string_view session_id,
        std::u16string_view instance_id,
        std::u16string_view source_app_id) noexcept
    {
        if (vnotch::trim_u16(source_app_id).empty())
        {
            return false;
        }
        return ContainsEitherWay(display_name, source_app_id) ||
               ContainsEitherWay(session_id, source_app_id) ||
               ContainsEitherWay(instance_id, source_app_id);
    }

    static double ComputeBaseSessionScore(
        bool matched_by_process,
        bool matched_by_metadata,
        bool source_matches_last,
        uint32_t process_id,
        uint32_t last_process_id,
        bool session_id_matches_last) noexcept
    {
        if (!matched_by_process && !matched_by_metadata)
        {
            return -1e300;
        }
        double score = 0.0;
        if (matched_by_process) score += 1000.0;
        if (matched_by_metadata) score += 200.0;
        if (source_matches_last)
        {
            if (process_id != 0u && process_id == last_process_id) score += 300.0;
            if (session_id_matches_last) score += 400.0;
        }
        return score;
    }

    static std::u16string ExtractProcessNameCandidates(std::u16string_view source_app_id)
    {
        std::u16string result;
        if (vnotch::trim_u16(source_app_id).empty())
        {
            return result;
        }

        // Scan for ([A-Za-z0-9_\-]+)\.exe (case-insensitive)
        size_t n = source_app_id.size();
        for (size_t i = 0; i + 4 <= n; ++i)
        {
            if (source_app_id[i] == u'.' &&
                vnotch::to_lower_ascii(source_app_id[i + 1]) == u'e' &&
                vnotch::to_lower_ascii(source_app_id[i + 2]) == u'x' &&
                vnotch::to_lower_ascii(source_app_id[i + 3]) == u'e')
            {
                size_t start = i;
                while (start > 0 && IsExeNameChar(source_app_id[start - 1]))
                {
                    --start;
                }
                if (start < i)
                {
                    std::u16string_view candidate = vnotch::trim_u16(source_app_id.substr(start, i - start));
                    if (!candidate.empty())
                    {
                        AppendCandidate(result, candidate);
                    }
                }
            }
        }

        struct Alias { std::u16string_view token; std::u16string_view proc; };
        static constexpr Alias kAliases[] = {
            { u"spotify", u"Spotify" },
            { u"msedge", u"msedge" },
            { u"edge", u"msedge" },
            { u"chrome", u"chrome" },
            { u"firefox", u"firefox" },
            { u"opera", u"opera" },
            { u"brave", u"brave" },
            { u"vivaldi", u"vivaldi" },
            { u"coccoc", u"browser" },
            { u"arc", u"arc" },
            { u"sidekick", u"sidekick" },
            { u"zen", u"zen" },
            { u"applemusic", u"AppleMusic" },
            { u"apple music", u"AppleMusic" }
        };

        for (const auto& a : kAliases)
        {
            if (vnotch::contains_icase_ascii(source_app_id, a.token))
            {
                AppendCandidate(result, a.proc);
            }
        }

        return result;
    }
};

} // namespace vnotch::services

VN_EXPORT int32_t vn_media_session_volume_matches_source(
    const char16_t* display_ptr, int32_t display_len,
    const char16_t* session_id_ptr, int32_t session_id_len,
    const char16_t* instance_id_ptr, int32_t instance_id_len,
    const char16_t* source_app_ptr, int32_t source_app_len,
    uint8_t* out_matches) noexcept
{
    if (out_matches == nullptr) return VN_ERR_INVALID_ARG;
    *out_matches = vnotch::services::MediaSessionVolumeService::SessionMatchesSourceAppId(
        vnotch::make_u16_view(display_ptr, display_len),
        vnotch::make_u16_view(session_id_ptr, session_id_len),
        vnotch::make_u16_view(instance_id_ptr, instance_id_len),
        vnotch::make_u16_view(source_app_ptr, source_app_len)) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_media_session_volume_compute_base_score(
    uint8_t matched_by_process,
    uint8_t matched_by_metadata,
    uint8_t source_matches_last,
    uint32_t process_id,
    uint32_t last_process_id,
    uint8_t session_id_matches_last,
    double* out_score) noexcept
{
    if (out_score == nullptr) return VN_ERR_INVALID_ARG;
    *out_score = vnotch::services::MediaSessionVolumeService::ComputeBaseSessionScore(
        matched_by_process != 0u,
        matched_by_metadata != 0u,
        source_matches_last != 0u,
        process_id,
        last_process_id,
        session_id_matches_last != 0u);
    return VN_OK;
}

VN_EXPORT int32_t vn_media_session_volume_extract_candidates(
    const char16_t* source_app_ptr,
    int32_t source_app_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
{
    std::u16string res = vnotch::services::MediaSessionVolumeService::ExtractProcessNameCandidates(
        vnotch::make_u16_view(source_app_ptr, source_app_len));
    return vnotch::copy_out_u16(res, out_buf, out_cap, out_len);
}
