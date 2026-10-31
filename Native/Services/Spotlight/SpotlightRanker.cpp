#include "vnotch_common.h"

using namespace vnotch;

VN_EXPORT int32_t vn_settings_search_is_normalized_match(
    const char16_t* candidate_ptr, int32_t candidate_len,
    const char16_t* query_ptr, int32_t query_len,
    uint8_t* out_result) noexcept;

namespace vnotch::services
{
    class SpotlightRanker final
    {
    public:
        static constexpr std::u16string_view kPopularExecutables[] = {
                u"cmd", u"cmd.exe", u"powershell", u"powershell.exe", u"pwsh", u"pwsh.exe",
                u"wt", u"wt.exe", u"bash", u"bash.exe", u"wsl", u"wsl.exe",
                u"calc", u"calc.exe", u"notepad", u"notepad.exe",
                u"explorer", u"explorer.exe", u"taskmgr", u"taskmgr.exe",
                u"regedit", u"regedit.exe", u"control", u"control.exe",
                u"mstsc", u"mstsc.exe", u"mspaint", u"mspaint.exe", u"paint", u"paint.exe",
                u"snippingtool", u"snippingtool.exe", u"cleanmgr", u"cleanmgr.exe",
                u"dxdiag", u"dxdiag.exe", u"msconfig", u"msconfig.exe",
                u"resmon", u"resmon.exe", u"perfmon", u"perfmon.exe",
                u"cmdkey", u"cmdkey.exe", u"ping", u"ping.exe", u"ipconfig", u"ipconfig.exe",
                u"systeminfo", u"systeminfo.exe", u"taskkill", u"taskkill.exe",
                u"services", u"services.msc", u"ncpa", u"ncpa.cpl",
                u"sysdm", u"sysdm.cpl", u"appwiz", u"appwiz.cpl",
                u"devmgmt", u"devmgmt.msc", u"diskmgmt", u"diskmgmt.msc",
                u"compmgmt", u"compmgmt.msc", u"eventvwr", u"eventvwr.msc",
                u"main.cpl", u"powercfg.cpl", u"firewall.cpl",
                u"chrome", u"chrome.exe", u"msedge", u"msedge.exe",
                u"firefox", u"firefox.exe", u"brave", u"brave.exe", u"opera", u"opera.exe",
                u"code", u"code.exe", u"devenv", u"devenv.exe",
                u"rider", u"rider64", u"rider64.exe", u"datagrip", u"datagrip64", u"datagrip64.exe",
                u"pycharm", u"pycharm64", u"pycharm64.exe", u"idea", u"idea64", u"idea64.exe",
                u"discord", u"discord.exe", u"spotify", u"spotify.exe",
                u"vlc", u"vlc.exe", u"steam", u"steam.exe",
                u"everything", u"everything.exe", u"putty", u"putty.exe",
                u"winscp", u"winscp.exe", u"7z", u"7z.exe", u"7zfm", u"7zfm.exe",
                u"python", u"python.exe", u"node", u"node.exe", u"git", u"git.exe",
                u"docker", u"docker.exe", u"word", u"winword", u"winword.exe",
                u"excel", u"excel.exe", u"powerpnt", u"powerpnt.exe"
            };
        
            static constexpr std::u16string_view kExecutableExtensions[] = {
                u".exe", u".cmd", u".bat", u".msc", u".cpl", u".com", u".ps1", u".appref-ms", u".lnk"
            };
        
            static constexpr std::u16string_view kAssetJunkExtensions[] = {
                u".dds", u".bmp", u".png", u".jpg", u".jpeg", u".gif", u".svg", u".ico", u".webp", u".tga", u".tif", u".tiff", u".psd", u".ai",
                u".dll", u".sys", u".drv", u".ocx", u".ax", u".so", u".dylib", u".lib", u".a", u".o", u".obj", u".exp", u".ilk", u".pdb",
                u".dat", u".tmp", u".temp", u".log", u".cache", u".bak", u".chk", u".dmp", u".swp",
                u".meta", u".asset", u".mat", u".prefab", u".unity", u".nfo", u".manifest"
            };
        
            static bool in_set_icase(std::u16string_view value, std::span<const std::u16string_view> set) noexcept
            {
                if (value.empty()) return false;
                for (std::u16string_view item : set)
                {
                    if (vnotch::equals_icase(value, item)) return true;
                }
                return false;
            }
        
            static bool is_path_fully_qualified(std::u16string_view path) noexcept
            {
                if (path.size() >= 3 && vnotch::is_ascii_alpha(path[0]) && path[1] == u':' &&
                    (path[2] == u'\\' || path[2] == u'/'))
                {
                    return true;
                }
                if (path.size() >= 2 && (path[0] == u'\\' || path[0] == u'/') &&
                    (path[1] == u'\\' || path[1] == u'/'))
                {
                    return true;
                }
                return false;
            }
        
            static std::u16string_view get_file_name(std::u16string_view path) noexcept
            {
                return vnotch::get_file_name(path);
            }
        
            static std::u16string_view get_extension(std::u16string_view path) noexcept
            {
                return vnotch::get_extension(path);
            }
        
            static std::u16string_view get_title_without_any_extension(std::u16string_view title) noexcept
            {
                if (vnotch::trim(title).empty()) return {};
                size_t last_dot = title.rfind(u'.');
                if (last_dot == std::u16string_view::npos || last_dot == 0) return title;
                return title.substr(0, last_dot);
            }
        
            static double match_word_boundaries(std::u16string_view title, std::u16string_view query) noexcept
            {
                bool prefix_match = false;
                size_t i = 0;
                while (i < title.size())
                {
                    while (i < title.size() && title[i] == u' ') ++i;
                    if (i >= title.size()) break;
        
                    size_t start = i;
                    while (i < title.size() && title[i] != u' ') ++i;
                    std::u16string_view word = title.substr(start, i - start);
                    if (word == query) return 850.0;
                    if (!prefix_match && vnotch::starts_with_ordinal(word, query))
                        prefix_match = true;
                }
                return prefix_match ? 800.0 : 0.0;
            }
        
            static bool is_normalized_match(std::u16string_view candidate, std::u16string_view query) noexcept
            {
                uint8_t res = 0;
                vn_settings_search_is_normalized_match(
                    candidate.data(), static_cast<int32_t>(candidate.size()),
                    query.data(), static_cast<int32_t>(query.size()),
                    &res);
                return res != 0;
            }
        
            static double calculate_lexical_score(std::u16string_view title, std::u16string_view subtitle, std::u16string_view normalized_query) noexcept
            {
                if (title.empty()) return 0.0;
        
                if (title == normalized_query) return 1000.0;
                if (vnotch::starts_with_ordinal(title, normalized_query))
                {
                    size_t diff = title.size() - normalized_query.size();
                    return 900.0 - static_cast<double>(diff < 100 ? diff : 100);
                }
        
                double word_score = match_word_boundaries(title, normalized_query);
                if (word_score > 0.0) return word_score;
                if (title.find(normalized_query) != std::u16string_view::npos) return 700.0;
                if (is_normalized_match(title, normalized_query)) return 500.0;
                if (!subtitle.empty() && subtitle.find(normalized_query) != std::u16string_view::npos) return 350.0;
                if (!subtitle.empty() && is_normalized_match(subtitle, normalized_query)) return 250.0;
                return 0.0;
            }
        
            static bool is_executable_or_app(int32_t kind, std::u16string_view raw_title, std::u16string_view target) noexcept
            {
                if (kind == 0) return true; // Application
                std::u16string_view ext = get_extension(raw_title);
                if (ext.empty() && !target.empty())
                    ext = get_extension(target);
                return in_set_icase(ext, kExecutableExtensions);
            }
        
            static bool is_asset_junk(std::u16string_view raw_title, std::u16string_view target) noexcept
            {
                std::u16string_view ext = get_extension(raw_title);
                if (ext.empty() && !target.empty())
                    ext = get_extension(target);
                return in_set_icase(ext, kAssetJunkExtensions);
            }
        
            static bool is_popular_executable(std::u16string_view raw_title, std::u16string_view target) noexcept
            {
                if (in_set_icase(raw_title, kPopularExecutables)) return true;
        
                std::u16string_view title_without_ext = get_title_without_any_extension(raw_title);
                if (in_set_icase(title_without_ext, kPopularExecutables)) return true;
        
                if (!target.empty())
                {
                    std::u16string_view target_name = get_file_name(target);
                    if (in_set_icase(target_name, kPopularExecutables)) return true;
        
                    std::u16string_view target_without_ext = get_title_without_any_extension(target_name);
                    if (in_set_icase(target_without_ext, kPopularExecutables)) return true;
                }
                return false;
            }

        static int32_t vn_spotlight_ranker_score_impl(const char16_t* display_title_ptr, int32_t display_title_len,
    const char16_t* raw_title_ptr, int32_t raw_title_len,
    const char16_t* subtitle_ptr, int32_t subtitle_len,
    const char16_t* display_subtitle_ptr, int32_t display_subtitle_len,
    const char16_t* target_ptr, int32_t target_len,
    int32_t kind,
    const char16_t* query_ptr, int32_t query_len,
    double* out_score)
        {
    if (!out_score) return VN_ERR_INVALID_ARG;
    *out_score = 0.0;

    std::u16string normalized_query = vnotch::normalize_search(vnotch::make_view(query_ptr, query_len));
    if (normalized_query.empty()) return VN_OK;

    std::u16string_view display_title = vnotch::make_view(display_title_ptr, display_title_len);
    std::u16string_view raw_title = vnotch::make_view(raw_title_ptr, raw_title_len);
    std::u16string_view subtitle_raw = vnotch::make_view(subtitle_ptr, subtitle_len);
    std::u16string_view display_subtitle = vnotch::make_view(display_subtitle_ptr, display_subtitle_len);
    std::u16string_view target = vnotch::make_view(target_ptr, target_len);

    std::u16string title = vnotch::normalize_search(display_title);
    std::u16string subtitle = vnotch::normalize_search(
        vnotch::starts_with_ordinal(subtitle_raw, u"spotlight.") ? display_subtitle : subtitle_raw);

    std::u16string_view title_without_ext_raw = get_title_without_any_extension(display_title);
    std::u16string title_without_ext = vnotch::normalize_search(title_without_ext_raw);

    double base_score = calculate_lexical_score(title, subtitle, normalized_query);
    if ((kind == 0 || kind == 1) && is_path_fully_qualified(target))
    {
        std::u16string file_name = vnotch::normalize_search(get_file_name(target));
        base_score = std::max(base_score, calculate_lexical_score(file_name, {}, normalized_query));
    }
    if (!title_without_ext.empty() && title_without_ext != title)
    {
        double ext_score = calculate_lexical_score(title_without_ext, {}, normalized_query);
        base_score = std::max(base_score, ext_score);
    }

    if (base_score <= 0.0) return VN_OK;

    if (is_executable_or_app(kind, raw_title, target))
    {
        base_score += 300.0;
        if (is_popular_executable(raw_title, target))
            base_score += 100.0;
    }
    else if (kind == 2) // Folder
    {
        base_score += 50.0;
    }
    else if (is_asset_junk(raw_title, target))
    {
        base_score = std::max(10.0, base_score - 300.0);
    }

    *out_score = base_score;
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_spotlight_ranker_score(const char16_t* display_title_ptr, int32_t display_title_len,
    const char16_t* raw_title_ptr, int32_t raw_title_len,
    const char16_t* subtitle_ptr, int32_t subtitle_len,
    const char16_t* display_subtitle_ptr, int32_t display_subtitle_len,
    const char16_t* target_ptr, int32_t target_len,
    int32_t kind,
    const char16_t* query_ptr, int32_t query_len,
    double* out_score) noexcept
{
    try { return vnotch::services::SpotlightRanker::vn_spotlight_ranker_score_impl(display_title_ptr, display_title_len, raw_title_ptr, raw_title_len, subtitle_ptr, subtitle_len, display_subtitle_ptr, display_subtitle_len, target_ptr, target_len, kind, query_ptr, query_len, out_score);
 } catch (...) { return VN_ERR_FAIL; }
}

