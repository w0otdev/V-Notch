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

        struct Keys {
            std::u16string title, subtitle, stem, filename;
        };

        static Keys prepare_keys(std::u16string_view display, std::u16string_view subtitle,
                                 std::u16string_view target, int32_t kind)
        {
            Keys keys{normalize_search(display), normalize_search(subtitle), {}, {}};
            auto stem = get_title_without_any_extension(display);
            if (stem != display) keys.stem = normalize_search(stem);
            if ((kind == 0 || kind == 1) && is_path_fully_qualified(target))
                keys.filename = normalize_search(get_file_name(target));
            return keys;
        }

        static double score_keys(std::u16string_view title, std::u16string_view subtitle,
                                 std::u16string_view stem, std::u16string_view filename, std::u16string_view query)
        {
            double score = calculate_lexical_score(title, subtitle, query);
            if (!filename.empty()) score = std::max(score, calculate_lexical_score(filename, {}, query));
            if (!stem.empty() && stem != title) score = std::max(score, calculate_lexical_score(stem, {}, query));
            return score;
        }

        static int32_t executable_bonus(std::u16string_view raw, std::u16string_view target) noexcept
        {
            return is_popular_executable(raw, target) ? 400 : 300;
        }
    };
}

using Ranker = vnotch::services::SpotlightRanker;
using Text = std::u16string_view;

VN_EXPORT int32_t __stdcall vn_spotlight_ranker_score(const char16_t* display, int32_t display_len,
    const char16_t* raw, int32_t raw_len, const char16_t* subtitle, int32_t subtitle_len,
    const char16_t* display_subtitle, int32_t display_subtitle_len, const char16_t* target, int32_t target_len,
    int32_t kind, const char16_t* query, int32_t query_len, double* out_score) noexcept
{
    if (!out_score) return VN_ERR_INVALID_ARG;
    *out_score = 0;
    try {
        auto q = normalize_search(make_view(query, query_len));
        if (q.empty()) return VN_OK;
        auto sub = make_view(subtitle, subtitle_len);
        auto path = make_view(target, target_len);
        auto name = make_view(raw, raw_len);
        auto keys = Ranker::prepare_keys(make_view(display, display_len),
            starts_with_ordinal(sub, u"spotlight.") ? make_view(display_subtitle, display_subtitle_len) : sub, path, kind);
        double score = Ranker::score_keys(keys.title, keys.subtitle, keys.stem, keys.filename, q);
        if (score <= 0) return VN_OK;
        if (Ranker::is_executable_or_app(kind, name, path)) score += Ranker::executable_bonus(name, path);
        else if (kind == 2) score += 50;
        else if (Ranker::is_asset_junk(name, path)) score = std::max(10.0, score - 300.0);
        *out_score = score;
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t __stdcall vn_spotlight_prepare_app(const char16_t* display, int32_t display_len,
    const char16_t* raw, int32_t raw_len, const char16_t* subtitle, int32_t subtitle_len,
    const char16_t* target, int32_t target_len, char16_t* buffer, int32_t capacity,
    VnAppRankRow* row, int32_t* written) noexcept
{
    if (!row || !written || capacity < 0 || (capacity && !buffer) ||
        display_len < 0 || (display_len && !display) || raw_len < 0 || (raw_len && !raw) ||
        subtitle_len < 0 || (subtitle_len && !subtitle) || target_len < 0 || (target_len && !target))
        return VN_ERR_INVALID_ARG;
    *row = {};
    *written = 0;
    try {
        auto name = make_view(raw, raw_len);
        auto path = make_view(target, target_len);
        auto keys = Ranker::prepare_keys(make_view(display, display_len), make_view(subtitle, subtitle_len), path, 0);
        const Text fields[] = {name, keys.title, keys.subtitle, keys.stem, keys.filename};
        size_t required = 0;
        for (Text field : fields) required += field.size();
        if (required > INT32_MAX) return VN_ERR_FAIL;
        *written = static_cast<int32_t>(required);
        if (capacity < *written) return VN_ERR_BUFFER_TOO_SMALL;
        for (Text field : fields) {
            if (!field.empty()) buffer = std::copy(field.begin(), field.end(), buffer);
        }
        *row = {0, raw_len, static_cast<int32_t>(keys.title.size()), static_cast<int32_t>(keys.subtitle.size()),
            static_cast<int32_t>(keys.stem.size()), static_cast<int32_t>(keys.filename.size()), Ranker::executable_bonus(name, path)};
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t __stdcall vn_spotlight_rank_apps(const VnAppRankRow* rows, int32_t count,
    const char16_t* arena, int32_t arena_len, const char16_t* query, int32_t query_len,
    VnAppRankMatch* matches, int32_t limit, int32_t* found) noexcept
{
    if (!found || count < 0 || (count && !rows) || arena_len < 0 || (arena_len && !arena) ||
        query_len < 0 || (query_len && !query) || limit < 0 || (limit && !matches)) return VN_ERR_INVALID_ARG;
    *found = 0;
    try {
        auto q = normalize_search(make_view(query, query_len));
        if (!limit || q.empty()) return VN_OK;
        Text storage = make_view(arena, arena_len);
        // The root is the worst retained match. Only accepted rows reach the comparator.
        auto better = [&](const VnAppRankMatch& a, const VnAppRankMatch& b) {
            if (a.score != b.score) return a.score > b.score;
            const auto& x = rows[a.index];
            const auto& y = rows[b.index];
            int32_t cmp = 0;
            vn_spotlight_search_compare_keys(a.score, storage.substr(x.offset, x.raw_title).data(), x.raw_title, nullptr, 0,
                b.score, storage.substr(y.offset, y.raw_title).data(), y.raw_title, nullptr, 0, &cmp);
            return cmp ? cmp < 0 : a.index < b.index;
        };
        int32_t kept = 0;
        for (int32_t i = 0; i < count; ++i) {
            const auto& row = rows[i];
            if (row.offset < 0 || row.raw_title < 0 || row.title < 0 || row.subtitle < 0 || row.stem < 0 ||
                row.filename < 0 || int64_t(row.offset) + row.raw_title + row.title + row.subtitle + row.stem + row.filename > arena_len)
                return VN_ERR_INVALID_ARG;
            auto fields = storage.substr(row.offset + row.raw_title);
            auto take = [&](int32_t length) {
                auto field = fields.substr(0, length);
                fields.remove_prefix(length);
                return field;
            };
            auto title = take(row.title);
            auto subtitle = take(row.subtitle);
            auto stem = take(row.stem);
            double score = Ranker::score_keys(title, subtitle, stem, take(row.filename), q);
            if (score <= 0) continue;
            VnAppRankMatch match{score + row.bonus, i, 0};
            if (kept == limit) {
                if (!better(match, matches[0])) continue;
                std::pop_heap(matches, matches + kept, better);
                --kept;
            }
            matches[kept++] = match;
            std::push_heap(matches, matches + kept, better);
        }
        std::sort_heap(matches, matches + kept, better);
        *found = kept;
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}
