#include "vnotch_common.h"

namespace vnotch::services
{

class AppIntegrityService final
{
public:
    static constexpr bool HasEmbeddedNull(std::u16string_view sv) noexcept
    {
        return sv.find(u'\0') != std::u16string_view::npos;
    }

    static bool IsOfficialRepositoryPath(std::u16string_view path) noexcept
    {
        if (HasEmbeddedNull(path))
        {
            return false;
        }
        if (vnotch::equals_icase_ascii(path, u"/w0otdev/V-Notch") ||
            vnotch::equals_icase_ascii(path, u"/rainaku/V-Notch"))
        {
            return true;
        }
        return vnotch::starts_with_icase_ascii(path, u"/w0otdev/V-Notch/") ||
               vnotch::starts_with_icase_ascii(path, u"/rainaku/V-Notch/");
    }

    static bool IsReleaseAssetHost(std::u16string_view host) noexcept
    {
        if (HasEmbeddedNull(host))
        {
            return false;
        }
        return vnotch::equals_icase_ascii(host, u"objects.githubusercontent.com") ||
               vnotch::equals_icase_ascii(host, u"release-assets.githubusercontent.com");
    }

    static bool IsOfficialReleaseUrl(
        bool is_absolute,
        std::u16string_view scheme,
        int32_t port,
        std::u16string_view user_info,
        std::u16string_view host,
        std::u16string_view abs_path) noexcept
    {
        if (!is_absolute || port != 443 || !user_info.empty() ||
            HasEmbeddedNull(scheme) || HasEmbeddedNull(host) || HasEmbeddedNull(abs_path))
        {
            return false;
        }
        if (!vnotch::equals_icase_ascii(scheme, u"https"))
        {
            return false;
        }
        if (!vnotch::equals_icase_ascii(host, u"github.com"))
        {
            return false;
        }
        return vnotch::starts_with_icase_ascii(abs_path, u"/w0otdev/V-Notch/releases/download/") ||
               vnotch::starts_with_icase_ascii(abs_path, u"/rainaku/V-Notch/releases/download/");
    }

    static bool IsTrustedReleaseRedirect(
        bool is_absolute,
        std::u16string_view scheme,
        int32_t port,
        std::u16string_view user_info,
        std::u16string_view host,
        std::u16string_view abs_path) noexcept
    {
        if (HasEmbeddedNull(scheme) || HasEmbeddedNull(user_info) ||
            HasEmbeddedNull(host) || HasEmbeddedNull(abs_path))
        {
            return false;
        }
        if (IsOfficialReleaseUrl(is_absolute, scheme, port, user_info, host, abs_path))
        {
            return true;
        }
        return is_absolute &&
               port == 443 &&
               user_info.empty() &&
               vnotch::equals_icase_ascii(scheme, u"https") &&
               IsReleaseAssetHost(host);
    }

    static bool IsTrustedDownloadUri(
        bool is_absolute,
        std::u16string_view scheme,
        int32_t port,
        std::u16string_view user_info,
        std::u16string_view host,
        std::u16string_view abs_path) noexcept
    {
        if (!is_absolute || port != 443 || !user_info.empty() ||
            HasEmbeddedNull(scheme) || HasEmbeddedNull(host) || HasEmbeddedNull(abs_path) ||
            !vnotch::equals_icase_ascii(scheme, u"https"))
        {
            return false;
        }
        if (vnotch::equals_icase_ascii(host, u"github.com") ||
            vnotch::equals_icase_ascii(host, u"www.github.com") ||
            vnotch::equals_icase_ascii(host, u"raw.githubusercontent.com") ||
            vnotch::equals_icase_ascii(host, u"media.githubusercontent.com"))
        {
            return IsOfficialRepositoryPath(abs_path);
        }
        return IsReleaseAssetHost(host);
    }

    static bool IsApplicableChecksumAsset(std::u16string_view asset_name, bool is_installer) noexcept
    {
        if (HasEmbeddedNull(asset_name) || vnotch::trim_u16(asset_name).empty())
        {
            return false;
        }

        bool is_sha_file = vnotch::ends_with_icase_ascii(asset_name, u".sha256");
        bool is_checksum_list = vnotch::equals_icase_ascii(asset_name, u"checksums.txt") ||
                                vnotch::equals_icase_ascii(asset_name, u"SHA256SUMS.txt");
        bool is_manifest = vnotch::ends_with_icase_ascii(asset_name, u".manifest.json");

        if (!is_sha_file && !is_checksum_list && !is_manifest)
        {
            return false;
        }

        bool is_setup_asset = vnotch::contains_icase_ascii(asset_name, u"Setup") || is_manifest;

        if (is_installer)
        {
            if (vnotch::equals_icase_ascii(asset_name, u"V-Notch.exe.sha256") ||
                vnotch::equals_icase_ascii(asset_name, u"V-Notch-SelfContained.exe.sha256"))
            {
                return false;
            }
            return true;
        }
        return !is_setup_asset;
    }

    static void ParseZoneIdentifier(
        std::u16string_view content,
        int32_t& out_host_start,
        int32_t& out_host_len,
        int32_t& out_ref_start,
        int32_t& out_ref_len) noexcept
    {
        out_host_start = -1;
        out_host_len = 0;
        out_ref_start = -1;
        out_ref_len = 0;

        if (vnotch::trim_u16(content).empty())
        {
            return;
        }

        size_t pos = 0;
        while (pos < content.size())
        {
            size_t line_end = pos;
            while (line_end < content.size() && content[line_end] != u'\r' && content[line_end] != u'\n')
            {
                ++line_end;
            }
            std::u16string_view line = content.substr(pos, line_end - pos);
            std::u16string_view trimmed = vnotch::trim_u16(line);
            if (vnotch::starts_with_icase_ascii(trimmed, u"HostUrl="))
            {
                std::u16string_view val = vnotch::trim_u16(trimmed.substr(8));
                if (!HasEmbeddedNull(val))
                {
                    out_host_start = static_cast<int32_t>(val.data() - content.data());
                    out_host_len = static_cast<int32_t>(val.size());
                }
                else
                {
                    out_host_start = -1;
                    out_host_len = 0;
                }
            }
            else if (vnotch::starts_with_icase_ascii(trimmed, u"ReferrerUrl="))
            {
                std::u16string_view val = vnotch::trim_u16(trimmed.substr(12));
                if (!HasEmbeddedNull(val))
                {
                    out_ref_start = static_cast<int32_t>(val.data() - content.data());
                    out_ref_len = static_cast<int32_t>(val.size());
                }
                else
                {
                    out_ref_start = -1;
                    out_ref_len = 0;
                }
            }

            pos = line_end;
            if (pos < content.size() && content[pos] == u'\r') ++pos;
            if (pos < content.size() && content[pos] == u'\n') ++pos;
        }
    }
};

} // namespace vnotch::services

VN_EXPORT int32_t __stdcall vn_app_integrity_parse_zone_identifier(
    const char16_t* content_ptr,
    int32_t content_len,
    int32_t* out_host_start,
    int32_t* out_host_len,
    int32_t* out_ref_start,
    int32_t* out_ref_len) noexcept
{
    if (!out_host_start || !out_host_len || !out_ref_start || !out_ref_len)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_host_start = -1;
    *out_host_len = 0;
    *out_ref_start = -1;
    *out_ref_len = 0;
    std::u16string_view content_view;
    if (!vnotch::try_make_u16_view(content_ptr, content_len, content_view))
    {
        return VN_ERR_INVALID_ARG;
    }
    vnotch::services::AppIntegrityService::ParseZoneIdentifier(
        content_view,
        *out_host_start, *out_host_len, *out_ref_start, *out_ref_len);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_app_integrity_is_trusted_download_uri(
    uint8_t is_absolute,
    const char16_t* scheme_ptr,
    int32_t scheme_len,
    int32_t port,
    const char16_t* user_info_ptr,
    int32_t user_info_len,
    const char16_t* host_ptr,
    int32_t host_len,
    const char16_t* abs_path_ptr,
    int32_t abs_path_len,
    uint8_t* out_trusted) noexcept
{
    if (out_trusted == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_trusted = 0u;
    std::u16string_view scheme, user_info, host, abs_path;
    if (!vnotch::try_make_u16_view(scheme_ptr, scheme_len, scheme) ||
        !vnotch::try_make_u16_view(user_info_ptr, user_info_len, user_info) ||
        !vnotch::try_make_u16_view(host_ptr, host_len, host) ||
        !vnotch::try_make_u16_view(abs_path_ptr, abs_path_len, abs_path))
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_trusted = vnotch::services::AppIntegrityService::IsTrustedDownloadUri(
        is_absolute != 0u, scheme, port, user_info, host, abs_path) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_app_integrity_is_official_release_url(
    uint8_t is_absolute,
    const char16_t* scheme_ptr,
    int32_t scheme_len,
    int32_t port,
    const char16_t* user_info_ptr,
    int32_t user_info_len,
    const char16_t* host_ptr,
    int32_t host_len,
    const char16_t* abs_path_ptr,
    int32_t abs_path_len,
    uint8_t* out_official) noexcept
{
    if (out_official == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_official = 0u;
    std::u16string_view scheme, user_info, host, abs_path;
    if (!vnotch::try_make_u16_view(scheme_ptr, scheme_len, scheme) ||
        !vnotch::try_make_u16_view(user_info_ptr, user_info_len, user_info) ||
        !vnotch::try_make_u16_view(host_ptr, host_len, host) ||
        !vnotch::try_make_u16_view(abs_path_ptr, abs_path_len, abs_path))
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_official = vnotch::services::AppIntegrityService::IsOfficialReleaseUrl(
        is_absolute != 0u, scheme, port, user_info, host, abs_path) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_app_integrity_is_release_asset_host(
    const char16_t* host_ptr,
    int32_t host_len,
    uint8_t* out_asset_host) noexcept
{
    if (out_asset_host == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_asset_host = 0u;
    std::u16string_view host;
    if (!vnotch::try_make_u16_view(host_ptr, host_len, host))
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_asset_host = vnotch::services::AppIntegrityService::IsReleaseAssetHost(host) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_app_integrity_is_trusted_release_redirect(
    uint8_t is_absolute,
    const char16_t* scheme_ptr,
    int32_t scheme_len,
    int32_t port,
    const char16_t* user_info_ptr,
    int32_t user_info_len,
    const char16_t* host_ptr,
    int32_t host_len,
    const char16_t* abs_path_ptr,
    int32_t abs_path_len,
    uint8_t* out_trusted) noexcept
{
    if (out_trusted == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_trusted = 0u;
    std::u16string_view scheme, user_info, host, abs_path;
    if (!vnotch::try_make_u16_view(scheme_ptr, scheme_len, scheme) ||
        !vnotch::try_make_u16_view(user_info_ptr, user_info_len, user_info) ||
        !vnotch::try_make_u16_view(host_ptr, host_len, host) ||
        !vnotch::try_make_u16_view(abs_path_ptr, abs_path_len, abs_path))
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_trusted = vnotch::services::AppIntegrityService::IsTrustedReleaseRedirect(
        is_absolute != 0u, scheme, port, user_info, host, abs_path) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_app_integrity_is_applicable_checksum_asset(
    const char16_t* asset_name_ptr,
    int32_t asset_name_len,
    uint8_t is_installer,
    uint8_t* out_applicable) noexcept
{
    if (out_applicable == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_applicable = 0u;
    std::u16string_view asset_name;
    if (!vnotch::try_make_u16_view(asset_name_ptr, asset_name_len, asset_name))
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_applicable = vnotch::services::AppIntegrityService::IsApplicableChecksumAsset(
        asset_name, is_installer != 0u) ? 1u : 0u;
    return VN_OK;
}
