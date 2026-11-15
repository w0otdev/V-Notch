#include "vnotch_ext_core.h"

#include <tlhelp32.h>
#include <devpropdef.h>
#include <cfgmgr32.h>
#include <cwchar>

#pragma comment(lib, "cfgmgr32.lib")

#include "Parsing/WindowsText.h"
using namespace vnotch::windows_text;

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

