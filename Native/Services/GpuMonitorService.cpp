#include "vnotch_common.h"
#include <dxgi.h>
#include <wrl/client.h>
#include <algorithm>
#include <cstdint>
#include <cwchar>
#include <iterator>
#include <string_view>

#pragma comment(lib, "dxgi.lib")

namespace vnotch::services {

class GpuMonitorService final {
public:
    GpuMonitorService() = delete;

    [[nodiscard]] static constexpr int64_t ScoreAdapter(std::u16string_view name, uint64_t vram_bytes) noexcept {
        int64_t score = static_cast<int64_t>(vram_bytes / (1024ULL * 1024ULL));
        if (vn_contains_icase_ascii(name, u"NVIDIA") ||
            vn_contains_icase_ascii(name, u"GeForce") ||
            vn_contains_icase_ascii(name, u"RTX") ||
            vn_contains_icase_ascii(name, u"GTX") ||
            vn_contains_icase_ascii(name, u"Radeon RX") ||
            vn_contains_icase_ascii(name, u"Arc")) {
            score += 100000;
        }
        return score;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t __stdcall vn_gpu_monitor_find_best_adapter(
    wchar_t* out_name,
    int32_t out_name_cap,
    int32_t* out_name_len,
    uint64_t* out_dedicated_vram) noexcept {
    if (!out_name || out_name_cap <= 0 || !out_name_len || !out_dedicated_vram) {
        return VN_ERR_INVALID_ARG;
    }

    *out_name_len = 0;
    *out_dedicated_vram = 0;

    try {
        Microsoft::WRL::ComPtr<IDXGIFactory1> factory;
        if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))) || !factory) {
            return VN_ERR_FAIL;
        }

        wchar_t best_name[128]{};
        size_t best_name_len = 0;
        uint64_t best_vram = 0;
        int64_t best_score = -1;

        for (UINT i = 0; ; ++i) {
            Microsoft::WRL::ComPtr<IDXGIAdapter1> adapter;
            if (FAILED(factory->EnumAdapters1(i, &adapter)) || !adapter) {
                break;
            }

            DXGI_ADAPTER_DESC1 desc{};
            if (FAILED(adapter->GetDesc1(&desc))) {
                continue;
            }

            if ((desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) {
                continue;
            }

            const size_t raw_len = wcsnlen(desc.Description, std::size(desc.Description));
            const std::u16string_view trimmed = vnotch::trim_view(
                std::u16string_view(reinterpret_cast<const char16_t*>(desc.Description), raw_len));
            const uint64_t vram = static_cast<uint64_t>(desc.DedicatedVideoMemory);
            const int64_t score = vnotch::services::GpuMonitorService::ScoreAdapter(trimmed, vram);

            if (score > best_score) {
                best_score = score;
                best_vram = vram;
                best_name_len = std::min(trimmed.size(), std::size(best_name));
                for (size_t k = 0; k < best_name_len; ++k) {
                    best_name[k] = static_cast<wchar_t>(trimmed[k]);
                }
            }
        }

        if (best_name_len > 0) {
            if (static_cast<int32_t>(best_name_len) > out_name_cap) {
                *out_name_len = static_cast<int32_t>(best_name_len);
                return VN_ERR_BUFFER_TOO_SMALL;
            }
            for (size_t k = 0; k < best_name_len; ++k) {
                out_name[k] = best_name[k];
            }
            *out_name_len = static_cast<int32_t>(best_name_len);
            *out_dedicated_vram = best_vram;
        }

        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

} // extern "C"
