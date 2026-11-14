#include "vnotch_common.h"
#include "vnotch_com.h"
#include <UIAutomation.h>
#include <wrl/client.h>
#include <deque>
#include <mutex>

#pragma comment(lib, "uiautomationcore.lib")
#pragma comment(lib, "oleaut32.lib")

extern "C" int32_t vn_wtscanner_is_address_bar_value(const char16_t*, int32_t, uint8_t*) noexcept;
extern "C" int32_t vn_wtscanner_looks_like_url(const char16_t*, int32_t, uint8_t*) noexcept;
VN_EXPORT int32_t vn_wtscanner_is_media_url(const char16_t*, int32_t, uint8_t*) noexcept;
extern "C" int32_t vn_wtscanner_normalize_url(const char16_t*, int32_t, char16_t*, int32_t, int32_t*) noexcept;
VN_EXPORT int32_t vn_mediaactivator_score_tab_item(const char16_t*, int32_t, const char16_t*, int32_t,
    const char16_t*, int32_t, const char16_t*, int32_t, const char16_t*, int32_t,
    const char16_t*, int32_t, const char16_t*, int32_t, int32_t, int32_t*) noexcept;
VN_EXPORT int32_t vn_mediaactivator_normalize_title(const char16_t*, int32_t, char16_t*, int32_t, int32_t*) noexcept;
extern "C" int32_t vn_mediaactivator_normalize_media_title(const char16_t*, int32_t, const char16_t*, int32_t, char16_t*, int32_t, int32_t*) noexcept;
VN_EXPORT int32_t vn_mediaactivator_try_activate_window(intptr_t, uint8_t*) noexcept;
VN_EXPORT int32_t vn_mediaactivator_is_chromium_window(intptr_t, uint8_t*) noexcept;

namespace {
template<class T> using Ptr = Microsoft::WRL::ComPtr<T>;
using Text = std::u16string;
using View = std::u16string_view;
struct Variant {
    VARIANT value;
    Variant() noexcept { VariantInit(&value); }
    ~Variant() { VariantClear(&value); }
};
Text property(IUIAutomationElement* element, PROPERTYID id) {
    Variant value;
    if (FAILED(element->GetCachedPropertyValue(id, &value.value)) || value.value.vt != VT_BSTR || !value.value.bstrVal) return {};
    return Text(reinterpret_cast<const char16_t*>(value.value.bstrVal), SysStringLen(value.value.bstrVal));
}
bool boolean(IUIAutomationElement* element, PROPERTYID id) noexcept {
    Variant value;
    return SUCCEEDED(element->GetCachedPropertyValue(id, &value.value)) && value.value.vt == VT_BOOL && value.value.boolVal != VARIANT_FALSE;
}
bool predicate(View value, int32_t(*fn)(const char16_t*, int32_t, uint8_t*) noexcept) noexcept {
    uint8_t result = 0;
    return fn(value.data(), static_cast<int32_t>(value.size()), &result) == VN_OK && result != 0;
}
Text normalize_url(View value) {
    Text output(value.size() + 8, u'\0'); int32_t written = 0;
    if (vn_wtscanner_normalize_url(value.data(), static_cast<int32_t>(value.size()), output.data(), static_cast<int32_t>(output.size()), &written) != VN_OK) return {};
    output.resize(written); return output;
}
bool spotify(View value) noexcept { return vnotch::contains_ignore_case(value, u"open.spotify.com"); }

struct Tab {
    Ptr<IUIAutomationElement> element;
    Text title, help, url;
    bool has_spotify = false;
};
struct Chrome {
    std::vector<Tab> tabs;
    std::vector<Ptr<IUIAutomationElement>> back_buttons;
    Text address;
    int32_t toolbars = 0;
    bool address_spotify = false;
};
struct Automation {
    Ptr<IUIAutomation> api;
    Ptr<IUIAutomationCondition> all;
    Ptr<IUIAutomationCacheRequest> cache, tab_cache;
    bool initialize() {
        if (FAILED(CoCreateInstance(CLSID_CUIAutomation, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(api.GetAddressOf()))) ||
            FAILED(api->CreateTrueCondition(all.GetAddressOf())) || FAILED(api->CreateCacheRequest(cache.GetAddressOf()))) return false;
        Ptr<IUIAutomationCondition> raw;
        if (FAILED(api->get_RawViewCondition(raw.GetAddressOf()))) return false;
        cache->put_TreeScope(TreeScope_Element);
        cache->put_TreeFilter(raw.Get());
        constexpr PROPERTYID properties[]{UIA_ControlTypePropertyId, UIA_NamePropertyId, UIA_HelpTextPropertyId,
            UIA_AutomationIdPropertyId, UIA_IsControlElementPropertyId, UIA_ValueValuePropertyId,
            UIA_IsEnabledPropertyId, UIA_IsOffscreenPropertyId};
        for (auto id : properties) if (FAILED(cache->AddProperty(id))) return false;
        if (FAILED(cache->Clone(tab_cache.GetAddressOf()))) return false;
        tab_cache->put_TreeScope(static_cast<TreeScope>(TreeScope_Element | TreeScope_Children));
        return true;
    }
    Tab read_tab(IUIAutomationElement* element, bool include_children) {
        Tab tab; tab.element = element;
        tab.title = property(element, UIA_NamePropertyId); tab.help = property(element, UIA_HelpTextPropertyId);
        auto inspect = [&](IUIAutomationElement* child, bool own) {
            if (!own && !boolean(child, UIA_IsControlElementPropertyId)) return;
            const auto help = property(child, UIA_HelpTextPropertyId), name = property(child, UIA_NamePropertyId);
            tab.has_spotify |= spotify(help) || spotify(name);
            if (tab.url.empty() && predicate(help, vn_wtscanner_looks_like_url)) tab.url = normalize_url(help);
            if (tab.url.empty() && predicate(name, vn_wtscanner_looks_like_url)) tab.url = normalize_url(name);
            if (!own) {
                const auto value = property(child, UIA_ValueValuePropertyId);
                tab.has_spotify |= spotify(value);
                if (tab.url.empty() && predicate(value, vn_wtscanner_looks_like_url)) tab.url = normalize_url(value);
            }
        };
        inspect(element, true);
        if (include_children) {
            Ptr<IUIAutomationElement> updated;
            Ptr<IUIAutomationElementArray> children;
            if (SUCCEEDED(element->BuildUpdatedCache(tab_cache.Get(), updated.GetAddressOf())) &&
                SUCCEEDED(updated->GetCachedChildren(children.GetAddressOf())) && children) {
                int count = 0; children->get_Length(&count);
                for (int i = 0; i < count && i < 256; ++i) {
                    Ptr<IUIAutomationElement> child;
                    if (SUCCEEDED(children->GetElement(i, child.GetAddressOf())) && child) inspect(child.Get(), false);
                }
            }
        }
        return tab;
    }
    Chrome scan(HWND hwnd, bool firefox, bool include_children = true) {
        Chrome result;
        Ptr<IUIAutomationElement> root;
        if (FAILED(api->ElementFromHandle(hwnd, root.GetAddressOf())) || !root) return result;
        struct Node { Ptr<IUIAutomationElement> element; int depth; bool toolbar; };
        std::deque<Node> pending;
        pending.push_back({root, 0, false});
        int visited = 0;
        while (!pending.empty() && visited < 256) {
            Node parent = std::move(pending.front()); pending.pop_front();
            if (parent.depth >= 8) continue;
            Ptr<IUIAutomationElementArray> children;
            if (FAILED(parent.element->FindAllBuildCache(TreeScope_Children, all.Get(), cache.Get(), children.GetAddressOf())) || !children) continue;
            int count = 0; children->get_Length(&count);
            for (int i = 0; i < count && visited < 256; ++i) {
                Ptr<IUIAutomationElement> child;
                ++visited;
                if (FAILED(children->GetElement(i, child.GetAddressOf())) || !child) continue;
                CONTROLTYPEID type = 0;
                if (FAILED(child->get_CachedControlType(&type)) || type == UIA_DocumentControlTypeId) continue;
                const bool toolbar = parent.toolbar || type == UIA_ToolBarControlTypeId;
                if (type == UIA_ToolBarControlTypeId) ++result.toolbars;
                if (type == UIA_TabItemControlTypeId) {
                    result.tabs.push_back(read_tab(child.Get(), include_children));
                    continue;
                }
                if (type == UIA_EditControlTypeId &&
                    ((firefox && property(child.Get(), UIA_AutomationIdPropertyId) == u"urlbar-input") || (!firefox && toolbar))) {
                    const auto value = property(child.Get(), UIA_ValueValuePropertyId);
                    result.address_spotify |= spotify(value);
                    if (result.address.empty() && ((firefox && !vnotch::is_null_or_whitespace(value)) || (!firefox && predicate(value, vn_wtscanner_is_address_bar_value))))
                        result.address = normalize_url(value);
                }
                if (type == UIA_ButtonControlTypeId && toolbar) {
                    const auto name = property(child.Get(), UIA_NamePropertyId), id = property(child.Get(), UIA_AutomationIdPropertyId);
                    if ((id == u"back_button" || vnotch::equals_ignore_case(name, u"Back") || vnotch::starts_with_ignore_case(name, u"Back (") ||
                        vnotch::equals_ignore_case(name, u"Quay lại") || vnotch::starts_with_ignore_case(name, u"Quay lại (")) &&
                        boolean(child.Get(), UIA_IsEnabledPropertyId) && !boolean(child.Get(), UIA_IsOffscreenPropertyId)) result.back_buttons.push_back(child);
                }
                pending.push_back({std::move(child), parent.depth + 1, toolbar});
            }
        }
        return result;
    }
};
bool select_tab(IUIAutomationElement* element) noexcept {
    Ptr<IUIAutomationSelectionItemPattern> selection;
    if (SUCCEEDED(element->GetCurrentPatternAs(UIA_SelectionItemPatternId, IID_PPV_ARGS(selection.GetAddressOf()))) &&
        selection && SUCCEEDED(selection->Select())) return true;
    Ptr<IUIAutomationInvokePattern> invoke;
    return SUCCEEDED(element->GetCurrentPatternAs(UIA_InvokePatternId, IID_PPV_ARGS(invoke.GetAddressOf()))) && invoke && SUCCEEDED(invoke->Invoke());
}
Text normalize_title(View value, View source, bool media) {
    Text output(value.size() + 1, u'\0'); int32_t written = 0;
    const auto status = media ? vn_mediaactivator_normalize_media_title(value.data(), static_cast<int32_t>(value.size()), source.data(),
        static_cast<int32_t>(source.size()), output.data(), static_cast<int32_t>(output.size()), &written) :
        vn_mediaactivator_normalize_title(value.data(), static_cast<int32_t>(value.size()), output.data(), static_cast<int32_t>(output.size()), &written);
    if (status != VN_OK) return {};
    output.resize(written); return output;
}
}

namespace {

bool is_spotify_window(HWND hwnd) noexcept {
    wchar_t cls[64]{};
    if (GetClassNameW(hwnd, cls, 63) <= 0 ||
        !vnotch::equals_ignore_case(View(reinterpret_cast<const char16_t*>(cls)), u"Chrome_WidgetWin_1")) {
        return false;
    }
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid == 0) return false;
    Text exe_path;
    if (!vnotch::try_get_process_image_path(pid, &exe_path) || exe_path.size() < 11) return false;
    const View path_sv(exe_path);
    return vnotch::ends_with_ignore_case(path_sv, u"\\spotify.exe") ||
           vnotch::equals_ignore_case(path_sv, u"spotify.exe");
}

BOOL CALLBACK enum_spotify_windows(HWND hwnd, LPARAM lparam) noexcept {
    try {
        auto* list = reinterpret_cast<std::vector<HWND>*>(lparam);
        if (list && is_spotify_window(hwnd)) {
            list->push_back(hwnd);
        }
    } catch (...) {}
    return TRUE;
}

bool parse_document_title_sv(View doc_name, View& out_title, View& out_artists) noexcept {
    out_title = {};
    out_artists = {};
    const size_t sep = doc_name.rfind(u" \u2022 ");
    if (sep == View::npos || sep == 0) return false;
    const View title = vnotch::trim_view(doc_name.substr(0, sep));
    const View artists = vnotch::trim_view(doc_name.substr(sep + 3));
    if (title.empty() || artists.empty()) return false;
    out_title = title;
    out_artists = artists;
    return true;
}

Text current_name(IUIAutomationElement* element) {
    Variant value;
    if (!element || FAILED(element->get_CurrentName( &value.value.bstrVal )) || !value.value.bstrVal) return {};
    value.value.vt = VT_BSTR;
    return Text(reinterpret_cast<const char16_t*>(value.value.bstrVal), SysStringLen(value.value.bstrVal));
}

} // namespace

VN_EXPORT int32_t __stdcall vn_spotify_read_local_track(
    uint8_t include_track_id,
    char16_t* out_title, int32_t title_cap, int32_t* out_title_len,
    char16_t* out_artists, int32_t artists_cap, int32_t* out_artists_len,
    char16_t* out_track_url, int32_t url_cap, int32_t* out_url_len) noexcept {
    if (!out_title || title_cap <= 0 || !out_title_len ||
        !out_artists || artists_cap <= 0 || !out_artists_len ||
        !out_track_url || url_cap <= 0 || !out_url_len) {
        return VN_ERR_INVALID_ARG;
    }
    *out_title_len = 0;
    *out_artists_len = 0;
    *out_url_len = 0;

    try {
        vnotch::com::EnsureMta();
        static std::mutex s_gate;
        static Ptr<IUIAutomation> s_api;
        static Ptr<IUIAutomationCondition> s_doc_cond;
        static Ptr<IUIAutomationCacheRequest> s_link_cache;

        std::lock_guard lock(s_gate);
        auto reset_automation = [&]() noexcept {
            s_link_cache.Reset();
            s_doc_cond.Reset();
            s_api.Reset();
        };
        auto ensure_automation = [&]() noexcept -> bool {
            if (s_api && s_doc_cond && s_link_cache) return true;
            reset_automation();
            if (FAILED(CoCreateInstance(CLSID_CUIAutomation, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(s_api.GetAddressOf()))) || !s_api) {
                reset_automation();
                return false;
            }
            Variant doc_type;
            doc_type.value.vt = VT_I4;
            doc_type.value.lVal = UIA_DocumentControlTypeId;
            if (FAILED(s_api->CreatePropertyCondition(UIA_ControlTypePropertyId, doc_type.value, s_doc_cond.GetAddressOf())) || !s_doc_cond) {
                reset_automation();
                return false;
            }
            if (FAILED(s_api->CreateCacheRequest(s_link_cache.GetAddressOf())) || !s_link_cache ||
                FAILED(s_link_cache->put_TreeScope(TreeScope_Element)) ||
                FAILED(s_link_cache->AddProperty(UIA_NamePropertyId)) ||
                FAILED(s_link_cache->AddProperty(UIA_ValueValuePropertyId)) ||
                FAILED(s_link_cache->AddProperty(UIA_LegacyIAccessibleValuePropertyId))) {
                reset_automation();
                return false;
            }
            return true;
        };
        auto is_stale_com_error = [](HRESULT hr) noexcept -> bool {
            return hr == RPC_E_DISCONNECTED ||
                   hr == CO_E_OBJNOTCONNECTED ||
                   hr == CO_E_NOTINITIALIZED ||
                   hr == RPC_E_SERVER_DIED ||
                   hr == RPC_E_SERVER_DIED_DNE ||
                   hr == HRESULT_FROM_WIN32(RPC_S_SERVER_UNAVAILABLE) ||
                   hr == HRESULT_FROM_WIN32(RPC_S_UNKNOWN_IF);
        };

        if (!ensure_automation()) return VN_ERR_FAIL;

        auto try_read_doc = [&](IUIAutomationElement* elem, Text& doc_name, View& title_sv, View& artists_sv) -> bool {
            if (!elem) return false;
            doc_name = property(elem, UIA_NamePropertyId);
            if (doc_name.empty()) doc_name = current_name(elem);
            return parse_document_title_sv(doc_name, title_sv, artists_sv);
        };

        std::vector<HWND> windows;
        windows.reserve(4);
        EnumWindows(enum_spotify_windows, reinterpret_cast<LPARAM>(&windows));

        Ptr<IUIAutomationElement> doc;
        Text doc_name;
        View title_sv{}, artists_sv{};

        for (HWND hwnd : windows) {
            if (!IsWindow(hwnd)) continue;
            Ptr<IUIAutomationElement> root, candidate_doc;
            HRESULT hr = s_api->ElementFromHandle(hwnd, root.GetAddressOf());
            if (is_stale_com_error(hr)) {
                reset_automation();
                vnotch::com::EnsureMta();
                if (!ensure_automation()) return VN_ERR_FAIL;
                hr = s_api->ElementFromHandle(hwnd, root.GetAddressOf());
            }
            if (FAILED(hr) || !root) continue;

            hr = root->FindFirstBuildCache(TreeScope_Descendants, s_doc_cond.Get(), s_link_cache.Get(), candidate_doc.GetAddressOf());
            if (is_stale_com_error(hr)) {
                reset_automation();
                vnotch::com::EnsureMta();
                if (!ensure_automation()) return VN_ERR_FAIL;
                root.Reset();
                if (FAILED(s_api->ElementFromHandle(hwnd, root.GetAddressOf())) || !root) continue;
                hr = root->FindFirstBuildCache(TreeScope_Descendants, s_doc_cond.Get(), s_link_cache.Get(), candidate_doc.GetAddressOf());
            }
            if (FAILED(hr) || !candidate_doc) continue;

            if (try_read_doc(candidate_doc.Get(), doc_name, title_sv, artists_sv)) {
                doc = std::move(candidate_doc);
                break;
            }
        }

        if (!doc) return VN_OK;

        const int32_t t_len = std::min(static_cast<int32_t>(title_sv.size()), title_cap);
        std::copy_n(title_sv.data(), t_len, out_title);
        *out_title_len = t_len;

        const int32_t a_len = std::min(static_cast<int32_t>(artists_sv.size()), artists_cap);
        std::copy_n(artists_sv.data(), a_len, out_artists);
        *out_artists_len = a_len;

        if (include_track_id && doc) {
            Text title_str(title_sv);
            Variant ctrl_var, name_var;
            ctrl_var.value.vt = VT_I4;
            ctrl_var.value.lVal = UIA_HyperlinkControlTypeId;
            name_var.value.vt = VT_BSTR;
            name_var.value.bstrVal = SysAllocStringLen(reinterpret_cast<const OLECHAR*>(title_str.data()), static_cast<UINT>(title_str.size()));
            if (name_var.value.bstrVal) {
                Ptr<IUIAutomationCondition> by_type, by_name, link_cond;
                if (SUCCEEDED(s_api->CreatePropertyCondition(UIA_ControlTypePropertyId, ctrl_var.value, by_type.GetAddressOf())) &&
                    SUCCEEDED(s_api->CreatePropertyCondition(UIA_NamePropertyId, name_var.value, by_name.GetAddressOf())) &&
                    SUCCEEDED(s_api->CreateAndCondition(by_type.Get(), by_name.Get(), link_cond.GetAddressOf()))) {
                    Ptr<IUIAutomationElementArray> links;
                    const HRESULT find_hr = doc->FindAllBuildCache(TreeScope_Descendants, link_cond.Get(), s_link_cache.Get(), links.GetAddressOf());
                    if (is_stale_com_error(find_hr)) {
                        reset_automation();
                    } else if (SUCCEEDED(find_hr) && links) {
                        int count = 0;
                        links->get_Length(&count);
                        if (count > 0 && count <= 100) {
                            Text url;
                            bool ambiguous = false;
                            for (int i = 0; i < count; ++i) {
                                Ptr<IUIAutomationElement> link;
                                if (FAILED(links->GetElement(i, link.GetAddressOf())) || !link) continue;
                                Text candidate = property(link.Get(), UIA_ValueValuePropertyId);
                                if (candidate.empty()) candidate = property(link.Get(), UIA_LegacyIAccessibleValuePropertyId);
                                if (!vnotch::starts_with_ignore_case(candidate, u"https://open.spotify.com/") &&
                                    !vnotch::starts_with_ignore_case(candidate, u"spotify:track:")) {
                                    continue;
                                }
                                if (!url.empty() && url != candidate) {
                                    ambiguous = true;
                                    break;
                                }
                                url = std::move(candidate);
                            }
                            if (!ambiguous && !url.empty() && current_name(doc.Get()) == doc_name) {
                                const int32_t u_len = std::min(static_cast<int32_t>(url.size()), url_cap);
                                std::copy_n(url.data(), u_len, out_track_url);
                                *out_url_len = u_len;
                            }
                        }
                    }
                } else {
                    reset_automation();
                }
            }
        }
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_wtscanner_scan_browser(intptr_t hwnd, uint8_t firefox, VnBrowserChromeSnapshot* result,
    VnBrowserTabSnapshot* tabs, int32_t tab_capacity, char16_t* text, int32_t capacity, int32_t* required) noexcept {
    if (!result || !required || tab_capacity < 0 || capacity < 0 || (tab_capacity && !tabs) || (capacity && !text)) return VN_ERR_INVALID_ARG;
    *result = {}; *required = 0;
    try {
        vnotch::ComScope com;
        Automation automation;
        if (!automation.initialize()) return VN_ERR_FAIL;
        auto chrome = automation.scan(reinterpret_cast<HWND>(hwnd), firefox != 0);
        Text url;
        if (predicate(chrome.address, vn_wtscanner_is_media_url)) url = chrome.address;
        bool has_spotify = chrome.address_spotify;
        for (const auto& tab : chrome.tabs) {
            has_spotify |= tab.has_spotify;
            if (url.empty() && predicate(tab.url, vn_wtscanner_is_media_url)) url = tab.url;
        }
        if (url.empty()) url = chrome.address;
        size_t size = url.size() + chrome.address.size();
        if (tab_capacity) for (const auto& tab : chrome.tabs) size += tab.url.size();
        if (size > INT32_MAX) return VN_ERR_FAIL;
        *required = static_cast<int32_t>(size);
        result->toolbar_count = chrome.toolbars;
        result->tab_count = static_cast<int32_t>(chrome.tabs.size());
        result->spotify = has_spotify ? 1 : 0;
        if (size > static_cast<size_t>(capacity) || (tab_capacity && chrome.tabs.size() > static_cast<size_t>(tab_capacity))) return VN_ERR_BUFFER_TOO_SMALL;
        int32_t offset = 0;
        auto copy = [&](View value, int32_t& start, int32_t& length) {
            start = offset; length = static_cast<int32_t>(value.size());
            if (length) std::memcpy(text + offset, value.data(), value.size() * sizeof(char16_t));
            offset += length;
        };
        copy(url, result->url_offset, result->url_length);
        copy(chrome.address, result->address_offset, result->address_length);
        if (tab_capacity) for (size_t i = 0; i < chrome.tabs.size(); ++i) {
            copy(chrome.tabs[i].url, tabs[i].url_offset, tabs[i].url_length);
            tabs[i].spotify = chrome.tabs[i].has_spotify ? 1 : 0;
        }
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t __stdcall vn_media_browser_tab_action(const intptr_t* windows, int32_t count, uint8_t back,
    const char16_t* track, int32_t track_length, const char16_t* artist, int32_t artist_length,
    const char16_t* youtube_title, int32_t youtube_title_length, const char16_t* youtube_id, int32_t youtube_id_length,
    const char16_t* source, int32_t source_length, int32_t platform, uint8_t* success) noexcept {
    if (!success || count < 0 || (count && !windows) || track_length < 0 || artist_length < 0 || youtube_title_length < 0 || youtube_id_length < 0 || source_length < 0 ||
        (track_length && !track) || (artist_length && !artist) || (youtube_title_length && !youtube_title) || (youtube_id_length && !youtube_id) || (source_length && !source)) return VN_ERR_INVALID_ARG;
    *success = 0;
    try {
        vnotch::ComScope com;
        Automation automation;
        if (!automation.initialize()) return VN_ERR_FAIL;
        const auto normalized_track = back ? normalize_title(View(track ? track : u"", track_length), View(source ? source : u"", source_length), true) : Text{};
        if (back && normalized_track.size() < 3) return VN_OK;
        Ptr<IUIAutomationElement> best;
        intptr_t best_window = 0; int32_t best_score = 0;
        for (int32_t i = 0; i < count; ++i) {
            auto chrome = automation.scan(reinterpret_cast<HWND>(windows[i]), false, false);
            for (const auto& tab : chrome.tabs) {
                int32_t score = 0;
                if (back) {
                    if (!vnotch::contains_ignore_case(normalize_title(tab.title, {}, false), normalized_track)) continue;
                    if (best) return VN_OK; // Never navigate an ambiguous media tab.
                    score = 100;
                } else vn_mediaactivator_score_tab_item(tab.title.data(), static_cast<int32_t>(tab.title.size()), tab.help.data(), static_cast<int32_t>(tab.help.size()),
                    track, track_length, artist, artist_length, youtube_title, youtube_title_length, youtube_id, youtube_id_length, source, source_length, platform, &score);
                if (score > best_score) { best = tab.element; best_window = windows[i]; best_score = score; }
            }
        }
        if (!best || best_score < 30) return VN_OK;
        if (!back) {
            uint8_t activated = 0;
            vn_mediaactivator_try_activate_window(best_window, &activated);
            const bool selected = select_tab(best.Get());
            *success = activated || selected ? 1 : 0;
            return VN_OK;
        }
        if (!select_tab(best.Get())) return VN_OK;
        Ptr<IUIAutomationSelectionItemPattern> selected;
        BOOL is_selected = FALSE;
        if (FAILED(best->GetCurrentPatternAs(UIA_SelectionItemPatternId, IID_PPV_ARGS(selected.GetAddressOf()))) || !selected ||
            FAILED(selected->get_CurrentIsSelected(&is_selected)) || !is_selected) return VN_OK;
        uint8_t chromium = 0;
        vn_mediaactivator_is_chromium_window(best_window, &chromium);
        if (chromium) {
            DWORD_PTR unused = 0;
            *success = SendMessageTimeoutW(reinterpret_cast<HWND>(best_window), WM_APPCOMMAND, static_cast<WPARAM>(best_window),
                static_cast<LPARAM>(APPCOMMAND_BROWSER_BACKWARD << 16), SMTO_ABORTIFHUNG, 500, &unused) ? 1 : 0;
        } else {
            auto chrome = automation.scan(reinterpret_cast<HWND>(best_window), true, false);
            for (const auto& button : chrome.back_buttons) {
                Ptr<IUIAutomationInvokePattern> invoke;
                if (SUCCEEDED(button->GetCurrentPatternAs(UIA_InvokePatternId, IID_PPV_ARGS(invoke.GetAddressOf()))) && invoke && SUCCEEDED(invoke->Invoke())) {
                    *success = 1; break;
                }
            }
        }
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}
