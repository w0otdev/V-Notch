#include "vnotch_common.h"
#include <objidl.h>
#include <xmllite.h>
#include <shlwapi.h>
#include <wrl/client.h>
#pragma comment(lib, "xmllite.lib")
#pragma comment(lib, "shlwapi.lib")

namespace {
std::wstring Quote(std::wstring_view text) {
    std::wstring result = L"\"";
    for (wchar_t ch : text) {
        if (ch == L'"' || ch == L'\\') { result += L'\\'; result += ch; }
        else if (ch == L'\n') result += L"\\n";
        else if (ch == L'\r') result += L"\\r";
        else if (ch == L'\t') result += L"\\t";
        else if (ch >= 32) result += ch;
    }
    return result + L'"';
}
std::wstring PlainText(std::wstring_view html) {
    std::wstring result;
    bool tag = false;
    for (wchar_t ch : html) {
        if (ch == L'<') { tag = true; result += L'\n'; }
        else if (ch == L'>') tag = false;
        else if (!tag) result += ch;
    }
    return result;
}
}

VN_EXPORT int32_t __stdcall vn_update_parse_atom(const uint8_t* data, int32_t size,
    char16_t* output, int32_t capacity, int32_t* written) noexcept {
    if (!data || size <= 0 || size > 2 * 1024 * 1024 || !written) return VN_ERR_INVALID_ARG;
    try {
        Microsoft::WRL::ComPtr<IStream> stream;
        stream.Attach(SHCreateMemStream(data, static_cast<UINT>(size)));
        Microsoft::WRL::ComPtr<IXmlReader> reader;
        if (!stream || FAILED(CreateXmlReader(__uuidof(IXmlReader),
            reinterpret_cast<void**>(reader.GetAddressOf()), nullptr))) return VN_ERR_FAIL;
        reader->SetProperty(XmlReaderProperty_DtdProcessing, DtdProcessing_Prohibit);
        if (FAILED(reader->SetInput(stream.Get()))) return VN_ERR_FAIL;
        std::wstring json = L"[", id, date, content, field;
        bool entry = false, first = true;
        int32_t entry_depth = 0;
        XmlNodeType type;
        HRESULT status;
        while ((status = reader->Read(&type)) == S_OK) {
            const wchar_t* value = nullptr;
            UINT length = 0;
            if (type == XmlNodeType_Element || type == XmlNodeType_EndElement) {
                if (FAILED(reader->GetLocalName(&value, &length))) return VN_ERR_FAIL;
                std::wstring_view name(value, length);
                if (type == XmlNodeType_Element) {
                    const BOOL is_empty = reader->IsEmptyElement();
                    if (!entry) {
                        if (name == L"entry" && !is_empty) {
                            entry = true;
                            entry_depth = 0;
                            id.clear();
                            date.clear();
                            content.clear();
                            field.clear();
                        }
                    } else if (!is_empty) {
                        ++entry_depth;
                        if (entry_depth == 1) field = name;
                        else field.clear();
                    } else {
                        field.clear();
                    }
                } else if (entry) {
                    if (entry_depth == 0 && name == L"entry") {
                        auto slash = id.find_last_of(L'/');
                        auto tag = slash == std::wstring::npos ? id : id.substr(slash + 1);
                        if (!tag.empty() && !date.empty()) {
                            if (!first) json += L',';
                            first = false;
                            json += L"{\"tag_name\":" + Quote(tag) + L",\"published_at\":" + Quote(date)
                                + L",\"body\":" + Quote(PlainText(content)) + L",\"assets\":[]}";
                        }
                        entry = false;
                    } else if (entry_depth > 0) {
                        --entry_depth;
                    }
                    field.clear();
                }
            } else if (entry && entry_depth == 1 && (type == XmlNodeType_Text || type == XmlNodeType_CDATA)) {
                if (FAILED(reader->GetValue(&value, &length))) return VN_ERR_FAIL;
                if (field == L"id") id.append(value, length);
                else if (field == L"updated") date.append(value, length);
                else if (field == L"content") content.append(value, length);
            }
        }
        if (FAILED(status)) return VN_ERR_FAIL;
        json += L']';
        return vnotch::copy_out_u16(std::u16string_view(reinterpret_cast<const char16_t*>(json.data()), json.size()), output, capacity, written);
    } catch (...) { return VN_ERR_FAIL; }
}
