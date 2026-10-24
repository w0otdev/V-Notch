#include "vnotch_common.h"
#include <algorithm>
#include <limits>

namespace vnotch::services
{

class UpdateService final
{
    static constexpr bool IsAsciiAlphaOrDigit(char16_t ch) noexcept
    {
        return vnotch::is_ascii_alpha(ch) || vnotch::is_ascii_digit(ch);
    }

    static constexpr bool IsAllAsciiDigits(std::u16string_view s) noexcept
    {
        if (s.empty())
        {
            return false;
        }
        for (char16_t ch : s)
        {
            if (!vnotch::is_ascii_digit(ch))
            {
                return false;
            }
        }
        return true;
    }

    static bool ValidIdentifiers(std::u16string_view value, bool numeric_leading_zeros_allowed) noexcept
    {
        if (value.empty())
        {
            return false;
        }
        size_t pos = 0;
        while (pos <= value.size())
        {
            size_t dot = value.find(u'.', pos);
            std::u16string_view part = (dot == std::u16string_view::npos)
                ? value.substr(pos)
                : value.substr(pos, dot - pos);
            if (part.empty())
            {
                return false;
            }
            bool all_digits = true;
            for (char16_t ch : part)
            {
                if (!IsAsciiAlphaOrDigit(ch) && ch != u'-')
                {
                    return false;
                }
                if (!vnotch::is_ascii_digit(ch))
                {
                    all_digits = false;
                }
            }
            if (!numeric_leading_zeros_allowed && part.size() > 1 && part[0] == u'0' && all_digits)
            {
                return false;
            }
            if (dot == std::u16string_view::npos)
            {
                break;
            }
            pos = dot + 1;
        }
        return true;
    }

    static bool TryParseNonNegativeInt32(std::u16string_view s, int32_t& out_val) noexcept
    {
        s = vnotch::trim_u16(s);
        if (s.empty())
        {
            return false;
        }
        int64_t acc = 0;
        for (char16_t ch : s)
        {
            if (!vnotch::is_ascii_digit(ch))
            {
                return false;
            }
            acc = acc * 10 + static_cast<int64_t>(ch - u'0');
            if (acc > std::numeric_limits<int32_t>::max())
            {
                return false;
            }
        }
        out_val = static_cast<int32_t>(acc);
        return true;
    }

    static bool TryParseDotNetVersion(
        std::u16string_view s,
        int32_t& out_major,
        int32_t& out_minor,
        int32_t& out_build,
        int32_t& out_revision) noexcept
    {
        int32_t parts[4] = { 0, 0, 0, 0 };
        int32_t count = 0;
        size_t pos = 0;
        while (pos <= s.size())
        {
            if (count >= 4)
            {
                return false;
            }
            size_t dot = s.find(u'.', pos);
            std::u16string_view seg = (dot == std::u16string_view::npos)
                ? s.substr(pos)
                : s.substr(pos, dot - pos);
            if (!TryParseNonNegativeInt32(seg, parts[count]))
            {
                return false;
            }
            ++count;
            if (dot == std::u16string_view::npos)
            {
                break;
            }
            pos = dot + 1;
        }
        if (count < 2 || count > 4)
        {
            return false;
        }
        out_major = parts[0];
        out_minor = parts[1];
        out_build = (count >= 3) ? parts[2] : 0;
        out_revision = (count >= 4) ? parts[3] : 0;
        return true;
    }

    static int32_t CompareOrdinal(std::u16string_view a, std::u16string_view b) noexcept
    {
        size_t min_len = std::min(a.size(), b.size());
        for (size_t i = 0; i < min_len; ++i)
        {
            if (a[i] != b[i])
            {
                return static_cast<int32_t>(a[i]) - static_cast<int32_t>(b[i]);
            }
        }
        if (a.size() < b.size()) return -1;
        if (a.size() > b.size()) return 1;
        return 0;
    }

public:
    static bool TryParseReleaseVersion(
        std::u16string_view raw,
        int32_t& out_major,
        int32_t& out_minor,
        int32_t& out_build,
        int32_t& out_revision,
        int32_t& out_pre_start,
        int32_t& out_pre_len) noexcept
    {
        out_major = 0;
        out_minor = 0;
        out_build = 0;
        out_revision = 0;
        out_pre_start = -1;
        out_pre_len = 0;

        std::u16string_view value = vnotch::trim_u16(raw);
        if (value.empty())
        {
            return false;
        }
        if (value[0] == u'v' || value[0] == u'V')
        {
            value.remove_prefix(1);
        }

        size_t metadata = value.find(u'+');
        if (metadata != std::u16string_view::npos)
        {
            if (!ValidIdentifiers(value.substr(metadata + 1), true))
            {
                return false;
            }
            value = value.substr(0, metadata);
        }

        size_t suffix = value.find(u'-');
        if (suffix != std::u16string_view::npos)
        {
            std::u16string_view pre = value.substr(suffix + 1);
            if (!ValidIdentifiers(pre, false))
            {
                return false;
            }
            out_pre_start = static_cast<int32_t>(pre.data() - raw.data());
            out_pre_len = static_cast<int32_t>(pre.size());
            value = value.substr(0, suffix);
        }

        return TryParseDotNetVersion(value, out_major, out_minor, out_build, out_revision);
    }

    static int32_t CompareVersions(std::u16string_view left, std::u16string_view right) noexcept
    {
        int32_t amaj = 0, amin = 0, abld = 0, arev = 0, apre_s = -1, apre_l = 0;
        int32_t bmaj = 0, bmin = 0, bbld = 0, brev = 0, bpre_s = -1, bpre_l = 0;
        if (!TryParseReleaseVersion(left, amaj, amin, abld, arev, apre_s, apre_l) ||
            !TryParseReleaseVersion(right, bmaj, bmin, bbld, brev, bpre_s, bpre_l))
        {
            return 0;
        }

        if (amaj != bmaj) return (amaj < bmaj) ? -1 : 1;
        if (amin != bmin) return (amin < bmin) ? -1 : 1;
        if (abld != bbld) return (abld < bbld) ? -1 : 1;
        if (arev != brev) return (arev < brev) ? -1 : 1;

        std::u16string_view apre = (apre_s >= 0 && apre_l > 0) ? left.substr(static_cast<size_t>(apre_s), static_cast<size_t>(apre_l)) : std::u16string_view{};
        std::u16string_view bpre = (bpre_s >= 0 && bpre_l > 0) ? right.substr(static_cast<size_t>(bpre_s), static_cast<size_t>(bpre_l)) : std::u16string_view{};

        if (apre.empty() || bpre.empty())
        {
            if (apre.empty() && bpre.empty()) return 0;
            return apre.empty() ? 1 : -1;
        }

        size_t apos = 0;
        size_t bpos = 0;
        while (true)
        {
            bool a_has = apos <= apre.size();
            bool b_has = bpos <= bpre.size();
            if (!a_has || !b_has)
            {
                if (!a_has && !b_has) return 0;
                return a_has ? 1 : -1;
            }

            size_t adot = apre.find(u'.', apos);
            size_t bdot = bpre.find(u'.', bpos);
            std::u16string_view apart = (adot == std::u16string_view::npos) ? apre.substr(apos) : apre.substr(apos, adot - apos);
            std::u16string_view bpart = (bdot == std::u16string_view::npos) ? bpre.substr(bpos) : bpre.substr(bpos, bdot - bpos);

            bool a_num = IsAllAsciiDigits(apart);
            bool b_num = IsAllAsciiDigits(bpart);
            int32_t cmp = 0;
            if (a_num && b_num)
            {
                if (apart.size() != bpart.size())
                {
                    cmp = (apart.size() < bpart.size()) ? -1 : 1;
                }
                else
                {
                    cmp = CompareOrdinal(apart, bpart);
                }
            }
            else if (a_num != b_num)
            {
                cmp = a_num ? -1 : 1;
            }
            else
            {
                cmp = CompareOrdinal(apart, bpart);
            }

            if (cmp != 0)
            {
                return (cmp < 0) ? -1 : 1;
            }

            apos = (adot == std::u16string_view::npos) ? (apre.size() + 1) : (adot + 1);
            bpos = (bdot == std::u16string_view::npos) ? (bpre.size() + 1) : (bdot + 1);
        }
    }
};

} // namespace vnotch::services

VN_EXPORT int32_t vn_update_service_try_parse_release_version(
    const char16_t* value_ptr,
    int32_t value_len,
    int32_t* out_major,
    int32_t* out_minor,
    int32_t* out_build,
    int32_t* out_revision,
    int32_t* out_pre_start,
    int32_t* out_pre_len,
    uint8_t* out_ok) noexcept
{
    if (!out_major || !out_minor || !out_build || !out_revision || !out_pre_start || !out_pre_len || !out_ok)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_ok = vnotch::services::UpdateService::TryParseReleaseVersion(
        vnotch::make_u16_view(value_ptr, value_len),
        *out_major, *out_minor, *out_build, *out_revision, *out_pre_start, *out_pre_len) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_update_service_compare_versions(
    const char16_t* left_ptr,
    int32_t left_len,
    const char16_t* right_ptr,
    int32_t right_len,
    int32_t* out_cmp) noexcept
{
    if (out_cmp == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_cmp = vnotch::services::UpdateService::CompareVersions(
        vnotch::make_u16_view(left_ptr, left_len),
        vnotch::make_u16_view(right_ptr, right_len));
    return VN_OK;
}
