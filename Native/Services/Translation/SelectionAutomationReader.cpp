#include "vnotch_common.h"
#include "vnotch_com.h"
#include "vnotch_handle_registry.h"
#include <UIAutomation.h>
#include <wrl/client.h>
#include <array>
#include <deque>
#include <cmath>
#include <set>
#pragma comment(lib, "uiautomationcore.lib")
#pragma comment(lib, "oleaut32.lib")

namespace {
template<class T> using Ptr = Microsoft::WRL::ComPtr<T>;
using Text = std::u16string;
struct Variant {
    VARIANT value;
    Variant() { VariantInit(&value); }
    ~Variant() { VariantClear(&value); }
};
struct Bstr { BSTR value = nullptr; ~Bstr() { SysFreeString(value); } };
struct Array { SAFEARRAY* value = nullptr; ~Array() { if (value) SafeArrayDestroy(value); } };
struct Bounds {
    double x = 0, y = 0, width = 0, height = 0;
    bool valid() const noexcept { return std::isfinite(x) && std::isfinite(y) && std::isfinite(width) && std::isfinite(height) && width > 0 && height > 0; }
    bool contains(POINT point) const noexcept { return valid() && point.x >= x && point.x <= x + width && point.y >= y && point.y <= y + height; }
    void unite(Bounds other) noexcept {
        if (!other.valid()) return;
        if (!valid()) { *this = other; return; }
        const double right = std::max(x + width, other.x + other.width), bottom = std::max(y + height, other.y + other.height);
        x = std::min(x, other.x); y = std::min(y, other.y); width = right - x; height = bottom - y;
    }
};
struct Selection {
    Ptr<IUIAutomation> api;
    Ptr<IUIAutomationElement> element;
    Ptr<IUIAutomationTextRange> range;
    HWND window = nullptr;
    Text text;
    Bounds bounds;
    bool editable = false, truncated = false;
};
vnotch::HandleRegistry<Selection> selections;
int number(IUIAutomationElement* element, PROPERTYID property) {
    Variant v;
    return element && SUCCEEDED(element->GetCachedPropertyValue(property, &v.value)) && v.value.vt == VT_I4 ? v.value.lVal : 0;
}
bool boolean(IUIAutomationElement* element, PROPERTYID property, bool fallback = false) {
    Variant v;
    return element && SUCCEEDED(element->GetCachedPropertyValue(property, &v.value)) && v.value.vt == VT_BOOL ? v.value.boolVal != VARIANT_FALSE : fallback;
}
bool password(IUIAutomationElement* element) { return boolean(element, UIA_IsPasswordPropertyId, true); }
Text property_text(IUIAutomationElement* element, PROPERTYID property) {
    Variant v;
    if (!element || FAILED(element->GetCachedPropertyValue(property, &v.value)) || v.value.vt != VT_BSTR || !v.value.bstrVal) return {};
    return {reinterpret_cast<const char16_t*>(v.value.bstrVal), SysStringLen(v.value.bstrVal)};
}
Bounds bounds(IUIAutomationElement* element) {
    RECT r{};
    if (!element || FAILED(element->get_CachedBoundingRectangle(&r))) return {};
    return {static_cast<double>(r.left), static_cast<double>(r.top), static_cast<double>(r.right) - r.left, static_cast<double>(r.bottom) - r.top};
}
Text range_text(IUIAutomationTextRange* range, int limit) {
    Bstr b;
    if (!range || FAILED(range->GetText(limit, &b.value)) || !b.value) return {};
    return {reinterpret_cast<const char16_t*>(b.value), SysStringLen(b.value)};
}
bool readonly_range(IUIAutomationTextRange* range) {
    Variant value;
    return !range || FAILED(range->GetAttributeValue(UIA_IsReadOnlyAttributeId, &value.value)) || value.value.vt != VT_BOOL || value.value.boolVal != VARIANT_FALSE;
}
void append(Text& output, std::u16string_view value, int limit, char16_t separator = u'\n') {
    Text clean;
    clean.reserve(std::min<size_t>(value.size(), limit + 1));
    for (char16_t c : value) if (c && c != 0xFFFC) clean.push_back(c);
    if (vnotch::is_null_or_whitespace(clean)) return;
    if (!output.empty() && output.size() < static_cast<size_t>(limit + 1)) output.push_back(separator);
    const auto remaining = static_cast<size_t>(limit + 1) - output.size();
    output.append(clean, 0, std::min(remaining, clean.size()));
}
bool same_range(IUIAutomationTextRange* a, IUIAutomationTextRange* b) {
    if (!a || !b) return false;
    int start = 1, end = 1;
    return SUCCEEDED(a->CompareEndpoints(TextPatternRangeEndpoint_Start, b, TextPatternRangeEndpoint_Start, &start)) && start == 0 &&
        SUCCEEDED(a->CompareEndpoints(TextPatternRangeEndpoint_End, b, TextPatternRangeEndpoint_End, &end)) && end == 0;
}
struct Reader {
    Ptr<IUIAutomation> api;
    Ptr<IUIAutomationCacheRequest> cache, children_cache;
    Ptr<IUIAutomationTreeWalker> raw, control;
    std::set<std::vector<int32_t>> visited;
    HWND window; DWORD pid; POINT cursor; bool manual; int limit;
    ULONGLONG started = GetTickCount64();
    Reader(HWND w, DWORD p, POINT c, bool m, int l) : window(w), pid(p), cursor(c), manual(m), limit(l) { }
    bool time() const noexcept { return GetTickCount64() - started < 900; }
    bool initialize(IUIAutomation* previous) {
        api = previous;
        if (!api && FAILED(CoCreateInstance(CLSID_CUIAutomation8, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(api.GetAddressOf())))) return false;
        if (FAILED(api->CreateCacheRequest(cache.GetAddressOf()))) return false;
        cache->put_TreeScope(TreeScope_Element);
        for (const auto id : {UIA_RuntimeIdPropertyId, UIA_IsPasswordPropertyId, UIA_ControlTypePropertyId, UIA_NativeWindowHandlePropertyId, UIA_ProcessIdPropertyId,
            UIA_IsEnabledPropertyId, UIA_BoundingRectanglePropertyId, UIA_NamePropertyId, UIA_HelpTextPropertyId, UIA_ValueValuePropertyId,
            UIA_SelectionItemIsSelectedPropertyId}) if (FAILED(cache->AddProperty(id))) return false;
        for (const auto id : {UIA_TextPatternId, UIA_SelectionPatternId}) if (FAILED(cache->AddPattern(id))) return false;
        if (FAILED(cache->Clone(children_cache.GetAddressOf()))) return false;
        children_cache->put_TreeScope(static_cast<TreeScope>(TreeScope_Element | TreeScope_Children));
        return SUCCEEDED(api->get_RawViewWalker(raw.GetAddressOf())) && SUCCEEDED(api->get_ControlViewWalker(control.GetAddressOf()));
    }
    Ptr<IUIAutomationElement> update(IUIAutomationElement* element) {
        Ptr<IUIAutomationElement> result;
        if (element && time()) element->BuildUpdatedCache(cache.Get(), result.GetAddressOf());
        return result;
    }
    Ptr<IUIAutomationElement> from(HWND handle) {
        Ptr<IUIAutomationElement> result;
        if (handle && time()) api->ElementFromHandleBuildCache(handle, cache.Get(), result.GetAddressOf());
        return result;
    }
    Ptr<IUIAutomationElement> at(POINT point) {
        Ptr<IUIAutomationElement> result;
        if (time()) api->ElementFromPointBuildCache(point, cache.Get(), result.GetAddressOf());
        return result;
    }
    Ptr<IUIAutomationElement> parent(IUIAutomationElement* element, bool use_raw = true) {
        Ptr<IUIAutomationElement> result;
        if (element && time()) (use_raw ? raw : control)->GetParentElementBuildCache(element, cache.Get(), result.GetAddressOf());
        return result;
    }
    bool same_window(HWND candidate) const noexcept {
        if (!candidate) return false;
        const auto owner = GetAncestor(window, GA_ROOTOWNER);
        return candidate == window || IsChild(window, candidate) || GetAncestor(candidate, GA_ROOT) == window ||
            GetAncestor(candidate, GA_ROOTOWNER) == window || (owner && GetAncestor(candidate, GA_ROOTOWNER) == owner);
    }
    bool within(IUIAutomationElement* element) {
        const auto process = static_cast<DWORD>(number(element, UIA_ProcessIdPropertyId));
        if (process == GetCurrentProcessId() && pid != process) return false;
        auto handle = reinterpret_cast<HWND>(static_cast<intptr_t>(number(element, UIA_NativeWindowHandlePropertyId)));
        if (same_window(handle) || (pid && process == pid)) return true;
        Ptr<IUIAutomationElement> current = element;
        for (int depth = 0; current && depth < 24 && time(); ++depth) {
            handle = reinterpret_cast<HWND>(static_cast<intptr_t>(number(current.Get(), UIA_NativeWindowHandlePropertyId)));
            DWORD owner = 0; if (handle) GetWindowThreadProcessId(handle, &owner);
            if (same_window(handle) || (pid && (owner == pid || static_cast<DWORD>(number(current.Get(), UIA_ProcessIdPropertyId)) == pid))) return true;
            current = parent(current.Get(), false);
        }
        return false;
    }
    bool seen(IUIAutomationElement* element) {
        Variant id;
        if (FAILED(element->GetCachedPropertyValue(UIA_RuntimeIdPropertyId, &id.value)) || id.value.vt != (VT_ARRAY | VT_I4) || !id.value.parray) return false;
        LONG low = 0, high = -1;
        if (FAILED(SafeArrayGetLBound(id.value.parray, 1, &low)) || FAILED(SafeArrayGetUBound(id.value.parray, 1, &high)) || high < low || high - low > 64) return false;
        int32_t* data = nullptr;
        if (FAILED(SafeArrayAccessData(id.value.parray, reinterpret_cast<void**>(&data)))) return false;
        std::vector<int32_t> key;
        try { key.assign(data, data + (high - low + 1)); }
        catch (...) { SafeArrayUnaccessData(id.value.parray); throw; }
        SafeArrayUnaccessData(id.value.parray);
        return !visited.insert(std::move(key)).second;
    }
    std::unique_ptr<Selection> finish(IUIAutomationElement* element, Text text, Bounds rectangle, IUIAutomationTextRange* range = nullptr, bool editable = false) {
        if (text.empty()) return {};
        auto result = std::make_unique<Selection>();
        result->api = api; result->element = element; result->range = range; result->window = window;
        result->text = std::move(text); result->bounds = rectangle; result->editable = editable;
        result->truncated = result->text.size() > static_cast<size_t>(limit);
        return result;
    }
    std::unique_ptr<Selection> read(IUIAutomationElement* element) {
        if (!element || !time() || password(element)) return {};
        Ptr<IUIAutomationTextPattern> pattern;
        if (FAILED(element->GetCachedPatternAs(UIA_TextPatternId, IID_PPV_ARGS(pattern.GetAddressOf()))) || !pattern) return {};
        Ptr<IUIAutomationTextRangeArray> ranges; pattern->GetSelection(ranges.GetAddressOf());
        int count = 0; if (ranges) ranges->get_Length(&count);
        const int range_limit = std::min(count, 128);
        Text text; Bounds rectangle;
        Ptr<IUIAutomationTextRange> single;
        for (int i = 0; i < range_limit && time(); ++i) {
            Ptr<IUIAutomationTextRange> range;
            if (FAILED(ranges->GetElement(i, range.GetAddressOf())) || !range) continue;
            if (count == 1) single = range;
            if (text.size() <= static_cast<size_t>(limit)) append(text, range_text(range.Get(), limit + 256), limit);
            // Text range geometry is not cacheable by IUIAutomationCacheRequest.
            Array array;
            if (SUCCEEDED(range->GetBoundingRectangles(&array.value)) && array.value && SafeArrayGetDim(array.value) == 1 && SafeArrayGetElemsize(array.value) == sizeof(double)) {
                LONG low = 0, high = -1;
                SafeArrayGetLBound(array.value, 1, &low); SafeArrayGetUBound(array.value, 1, &high);
                double* data = nullptr;
                if (SUCCEEDED(SafeArrayAccessData(array.value, reinterpret_cast<void**>(&data)))) {
                    for (LONG index = 0; index + 3 <= high - low; index += 4) rectangle.unite({data[index], data[index + 1], data[index + 2], data[index + 3]});
                    SafeArrayUnaccessData(array.value);
                }
            }
        }
        const int type = number(element, UIA_ControlTypePropertyId);
        if (text.empty() && manual && time()) {
            SupportedTextSelection supported = SupportedTextSelection_Single;
            pattern->get_SupportedTextSelection(&supported);
            if (supported == SupportedTextSelection_None || type == UIA_TextControlTypeId || type == UIA_ToolTipControlTypeId) {
                Ptr<IUIAutomationTextRange> document; pattern->get_DocumentRange(document.GetAddressOf());
                append(text, range_text(document.Get(), limit + 256), limit);
            }
        }
        if (text.empty()) return {};
        if (!rectangle.valid()) {
            const auto control_bounds = bounds(element);
            rectangle = manual || !control_bounds.valid() || control_bounds.contains(cursor) ? Bounds{static_cast<double>(cursor.x), static_cast<double>(cursor.y), 1, 1} :
                Bounds{control_bounds.x + control_bounds.width, control_bounds.y + control_bounds.height, 1, 1};
        }
        const bool editable = boolean(element, UIA_IsEnabledPropertyId) && text.size() <= static_cast<size_t>(limit) && count == 1 &&
            !readonly_range(single.Get()) && (type == UIA_EditControlTypeId || type == UIA_DocumentControlTypeId);
        Ptr<IUIAutomationTextRange> clone; if (single) single->Clone(clone.GetAddressOf());
        return finish(element, std::move(text), rectangle, clone.Get(), editable);
    }
    Text single_text(IUIAutomationElement* element) {
        if (!element || password(element)) return {};
        for (const auto id : {UIA_ValueValuePropertyId, UIA_NamePropertyId, UIA_HelpTextPropertyId}) {
            auto value = property_text(element, id);
            if (!vnotch::is_null_or_whitespace(value)) return value;
        }
        Ptr<IUIAutomationElement> updated;
        element->BuildUpdatedCache(children_cache.Get(), updated.GetAddressOf());
        Ptr<IUIAutomationElementArray> children;
        if (updated) updated->GetCachedChildren(children.GetAddressOf());
        int count = 0; if (children) children->get_Length(&count);
        Text output;
        for (int i = 0; i < std::min(count, 8) && time(); ++i) {
            Ptr<IUIAutomationElement> child; children->GetElement(i, child.GetAddressOf());
            if (child && !password(child.Get())) append(output, property_text(child.Get(), UIA_NamePropertyId), limit, u' ');
        }
        return output;
    }
    Bounds fallback_bounds(IUIAutomationElement* element) const {
        auto r = bounds(element);
        return r.valid() && !r.contains(cursor) ? r : Bounds{static_cast<double>(cursor.x), static_cast<double>(cursor.y), 1, 1};
    }
    std::unique_ptr<Selection> fallback(IUIAutomationElement* element) {
        Ptr<IUIAutomationElement> container = element;
        for (int depth = 0; container && depth < 4 && time(); ++depth) {
            if (password(container.Get())) break;
            Ptr<IUIAutomationSelectionPattern> pattern;
            container->GetCachedPatternAs(UIA_SelectionPatternId, IID_PPV_ARGS(pattern.GetAddressOf()));
            Ptr<IUIAutomationElementArray> items;
            if (pattern) pattern->GetCurrentSelection(items.GetAddressOf());
            int count = 0; if (items) items->get_Length(&count);
            Text text; Ptr<IUIAutomationElement> first;
            for (int i = 0; i < std::min(count, 32) && time(); ++i) {
                Ptr<IUIAutomationElement> item; items->GetElement(i, item.GetAddressOf()); item = update(item.Get());
                if (!item || password(item.Get())) continue;
                if (!first) first = item;
                append(text, single_text(item.Get()), limit);
            }
            if (!text.empty()) return finish(first.Get(), std::move(text), fallback_bounds(first.Get()));
            container = parent(container.Get(), false);
        }
        const int type = number(element, UIA_ControlTypePropertyId);
        if (boolean(element, UIA_SelectionItemIsSelectedPropertyId) || type == UIA_TextControlTypeId || type == UIA_DataItemControlTypeId ||
            type == UIA_ListItemControlTypeId || type == UIA_TreeItemControlTypeId || type == UIA_HyperlinkControlTypeId || type == UIA_ToolTipControlTypeId ||
            type == UIA_StatusBarControlTypeId || type == UIA_HeaderItemControlTypeId || type == UIA_DocumentControlTypeId || type == UIA_EditControlTypeId) {
            Text text; append(text, single_text(element), limit);
            return finish(element, std::move(text), fallback_bounds(element));
        }
        return {};
    }
    std::unique_ptr<Selection> scan(IUIAutomationElement* previous, const int32_t* event_id, int event_id_length, HWND focus_handle, bool& protected_input) {
        Ptr<IUIAutomationElement> focused; api->GetFocusedElementBuildCache(cache.Get(), focused.GetAddressOf());
        if (focused && password(focused.Get())) { protected_input = true; return {}; }
        auto point = at(cursor), focus = from(same_window(focus_handle) ? focus_handle : nullptr), root = from(window);
        Ptr<IUIAutomationElement> event;
        if (root && event_id && event_id_length > 0 && time()) {
            Variant id; id.value.vt = VT_ARRAY | VT_I4;
            id.value.parray = SafeArrayCreateVector(VT_I4, 0, event_id_length);
            if (id.value.parray) {
                void* data = nullptr;
                if (SUCCEEDED(SafeArrayAccessData(id.value.parray, &data))) {
                    std::copy_n(event_id, event_id_length, static_cast<int32_t*>(data)); SafeArrayUnaccessData(id.value.parray);
                    Ptr<IUIAutomationCondition> condition;
                    if (SUCCEEDED(api->CreatePropertyCondition(UIA_RuntimeIdPropertyId, id.value, condition.GetAddressOf())))
                        root->FindFirstBuildCache(TreeScope_Subtree, condition.Get(), cache.Get(), event.GetAddressOf());
                }
            }
        }
        std::vector<Ptr<IUIAutomationElement>> seeds{event, update(previous), focused, focus, point};
        if (manual) for (const POINT offset : {POINT{-6, 0}, POINT{6, 0}, POINT{0, -6}, POINT{0, 6}})
            if (time()) seeds.push_back(at({cursor.x + offset.x, cursor.y + offset.y}));
        for (const auto& seed : seeds) {
            if (!seed || !time() || !within(seed.Get())) continue;
            Ptr<IUIAutomationElement> current = seed;
            for (int depth = 0; current && depth < 64 && time(); ++depth) {
                if (password(current.Get())) { if (depth == 0) { protected_input = true; return {}; } break; }
                if (seen(current.Get())) break;
                if (auto result = read(current.Get())) return result;
                if (reinterpret_cast<HWND>(static_cast<intptr_t>(number(current.Get(), UIA_NativeWindowHandlePropertyId))) == window) break;
                current = parent(current.Get(), depth < 48);
            }
        }
        // Fetch each container's immediate children as one cached batch. Bound the
        // traversal itself instead of materializing an unlimited FindAll subtree.
        for (const auto& container : {point, focus, focused, root}) {
            if (!container || !time() || !within(container.Get()) || password(container.Get())) continue;
            struct Node { Ptr<IUIAutomationElement> element; int depth; };
            std::deque<Node> pending; pending.push_back({container, 0}); int nodes = 0, texts = 0;
            while (!pending.empty() && nodes++ < 256 && texts < 64 && time()) {
                auto node = std::move(pending.front()); pending.pop_front();
                if (!node.element || password(node.element.Get())) continue;
                if (!seen(node.element.Get())) {
                    Ptr<IUIAutomationTextPattern> text_pattern;
                    if (SUCCEEDED(node.element->GetCachedPatternAs(UIA_TextPatternId, IID_PPV_ARGS(text_pattern.GetAddressOf()))) && text_pattern) {
                        ++texts;
                        if (auto result = read(node.element.Get())) return result;
                    }
                }
                if (node.depth >= 24) continue;
                Ptr<IUIAutomationElement> updated; node.element->BuildUpdatedCache(children_cache.Get(), updated.GetAddressOf());
                Ptr<IUIAutomationElementArray> children; if (updated) updated->GetCachedChildren(children.GetAddressOf());
                int count = 0; if (children) children->get_Length(&count);
                for (int i = 0; i < std::min(count, 32) && pending.size() + static_cast<size_t>(nodes) < 256; ++i) {
                    Ptr<IUIAutomationElement> child; children->GetElement(i, child.GetAddressOf());
                    if (child) pending.push_back({std::move(child), node.depth + 1});
                }
            }
        }
        if (manual) for (const auto& candidate : {point, focused, focus, event, root}) {
            if (candidate && time() && within(candidate.Get()) && !password(candidate.Get()))
                if (auto result = fallback(candidate.Get())) return result;
        }
        return {};
    }
};
}

VN_EXPORT int32_t vn_selection_read(intptr_t hwnd, const int32_t* event_id, int32_t event_id_length, intptr_t focus_hwnd, int64_t previous_handle,
    uint8_t manual, uint8_t allow_self, int32_t limit, char16_t* text, int32_t capacity, int32_t* length,
    double* rectangle, uint8_t* protected_input, uint8_t* editable, uint8_t* truncated, int64_t* handle) noexcept {
    if (event_id_length < 0 || event_id_length > 64 || (event_id_length && !event_id) || !text || limit <= 0 || limit > 65536 || capacity < limit + 1 || !length || !rectangle || !protected_input || !editable || !truncated || !handle) return VN_ERR_INVALID_ARG;
    *length = 0; *protected_input = *editable = *truncated = 0; *handle = 0;
    try {
        vnotch::ComScope com;
        const auto window = reinterpret_cast<HWND>(hwnd); DWORD pid = 0; GetWindowThreadProcessId(window, &pid);
        if (!window || !pid || (!allow_self && pid == GetCurrentProcessId())) return VN_OK;
        POINT point{}; GetCursorPos(&point);
        auto previous = selections.Acquire(previous_handle);
        Reader reader(window, pid, point, manual != 0, limit);
        if (!reader.initialize(previous ? previous->api.Get() : nullptr)) return VN_ERR_FAIL;
        bool protected_value = false;
        auto result = reader.scan(previous && previous->window == window ? previous->element.Get() : nullptr,
            event_id, event_id_length, reinterpret_cast<HWND>(focus_hwnd), protected_value);
        *protected_input = protected_value ? 1 : 0;
        if (!result) return VN_OK;
        std::copy(result->text.begin(), result->text.end(), text); *length = static_cast<int32_t>(result->text.size());
        rectangle[0] = result->bounds.x; rectangle[1] = result->bounds.y; rectangle[2] = result->bounds.width; rectangle[3] = result->bounds.height;
        *editable = result->editable ? 1 : 0; *truncated = result->truncated ? 1 : 0;
        *handle = selections.Add(std::move(result));
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}
VN_EXPORT int32_t vn_selection_close(int64_t handle) noexcept {
    vnotch::ComScope com;
    return selections.Remove(handle) ? VN_OK : VN_ERR_INVALID_ARG;
}
VN_EXPORT int32_t vn_selection_same(int64_t first, int64_t second, uint8_t* equal) noexcept {
    if (!equal) return VN_ERR_INVALID_ARG;
    *equal = 0;
    try {
        vnotch::ComScope com;
        auto a = selections.Acquire(first), b = selections.Acquire(second);
        if (!a || !b || a->window != b->window) return VN_OK;
        BOOL same = FALSE;
        if (SUCCEEDED(a->api->CompareElements(a->element.Get(), b->element.Get(), &same)) && same && same_range(a->range.Get(), b->range.Get())) *equal = 1;
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_selection_prepare_replace(int64_t handle, const char16_t* expected, int32_t expected_length,
    const char16_t* replacement, int32_t replacement_length, int32_t* cancellation, int32_t* outcome) noexcept {
    if (!expected || expected_length < 0 || !replacement || replacement_length < 0 || !outcome) return VN_ERR_INVALID_ARG;
    *outcome = 0;
    auto cancelled = [&]() noexcept { return cancellation && InterlockedCompareExchange(reinterpret_cast<volatile LONG*>(cancellation), 0, 0) != 0; };
    if (cancelled()) return VN_OK;
    if (std::u16string_view(replacement, replacement_length).find(u'\0') != std::u16string_view::npos) return VN_OK;
    try {
        vnotch::ComScope com;
        auto state = selections.Acquire(handle);
        if (!state || !state->editable || !state->element || !state->range) return VN_OK;
        auto foreground = GetForegroundWindow(); DWORD pid = 0; GetWindowThreadProcessId(foreground, &pid);
        if (foreground != state->window && pid != GetCurrentProcessId()) return VN_OK;
        for (const int key : {VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN}) if (GetAsyncKeyState(key) & 0x8000) return VN_OK;
        Variant is_password;
        if (FAILED(state->element->GetCurrentPropertyValue(UIA_IsPasswordPropertyId, &is_password.value)) ||
            is_password.value.vt != VT_BOOL || is_password.value.boolVal != VARIANT_FALSE || readonly_range(state->range.Get())) return VN_OK;
        Ptr<IUIAutomationTextPattern> pattern;
        if (FAILED(state->element->GetCurrentPatternAs(UIA_TextPatternId, IID_PPV_ARGS(pattern.GetAddressOf()))) || !pattern) return VN_OK;
        auto validate = [&]() {
            Ptr<IUIAutomationTextRangeArray> ranges; pattern->GetSelection(ranges.GetAddressOf());
            int count = 0; if (ranges) ranges->get_Length(&count);
            Ptr<IUIAutomationTextRange> current; if (count == 1) ranges->GetElement(0, current.GetAddressOf());
            return current && same_range(state->range.Get(), current.Get()) &&
                range_text(current.Get(), expected_length + 1) == std::u16string_view(expected, expected_length);
        };
        if (!validate() || !SetForegroundWindow(state->window) || FAILED(state->element->SetFocus()) || GetForegroundWindow() != state->window || !validate()) return VN_OK;
        Ptr<IUIAutomationValuePattern> value;
        state->element->GetCurrentPatternAs(UIA_ValuePatternId, IID_PPV_ARGS(value.GetAddressOf()));
        BOOL readonly = TRUE;
        if (value && SUCCEEDED(value->get_CurrentIsReadOnly(&readonly)) && !readonly) {
            Ptr<IUIAutomationTextRange> document; pattern->get_DocumentRange(document.GetAddressOf());
            const auto original = range_text(document.Get(), 20001);
            Bstr current_value;
            if (original.size() <= 20000 && SUCCEEDED(value->get_CurrentValue(&current_value.value)) && current_value.value &&
                original == std::u16string_view(reinterpret_cast<const char16_t*>(current_value.value), SysStringLen(current_value.value))) {
                Ptr<IUIAutomationTextRange> prefix; if (document) document->Clone(prefix.GetAddressOf());
                if (prefix && SUCCEEDED(prefix->MoveEndpointByRange(TextPatternRangeEndpoint_End, state->range.Get(), TextPatternRangeEndpoint_Start))) {
                    const auto offset = range_text(prefix.Get(), 20001).size();
                    if (offset <= original.size() && static_cast<size_t>(expected_length) <= original.size() - offset &&
                        original.compare(offset, expected_length, expected, expected_length) == 0) {
                        Text changed = original.substr(0, offset); changed.append(replacement, replacement_length); changed.append(original, offset + expected_length);
                        Bstr verify;
                        if (cancelled() || GetForegroundWindow() != state->window || !validate() || FAILED(value->get_CurrentValue(&verify.value)) || !verify.value ||
                            original != std::u16string_view(reinterpret_cast<const char16_t*>(verify.value), SysStringLen(verify.value))) return VN_OK;
                        Bstr argument; argument.value = SysAllocStringLen(reinterpret_cast<const wchar_t*>(changed.data()), static_cast<UINT>(changed.size()));
                        if (!cancelled() && argument.value && SUCCEEDED(value->SetValue(argument.value))) *outcome = 1;
                        return VN_OK;
                    }
                }
            }
        }
        if (!cancelled() && validate() && SUCCEEDED(state->range->Select()) && GetForegroundWindow() == state->window) *outcome = 2;
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}
