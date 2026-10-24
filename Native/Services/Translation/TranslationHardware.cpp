#include "vnotch_common.h"
#include <windows.h>
#include <cstdint>
#include <string_view>

namespace vnotch::services {

class TranslationHardware final {
public:
    static double DetectSystemRamGiB() noexcept {
        MEMORYSTATUSEX mem{};
        mem.dwLength = sizeof(mem);
        return ::GlobalMemoryStatusEx(&mem) ? static_cast<double>(mem.ullTotalPhys) / 1073741824.0 : 0.0;
    }

    static constexpr bool Fits(double ram_gib, int32_t minimum_ram_gib) noexcept {
        return ram_gib + 0.5 >= static_cast<double>(minimum_ram_gib);
    }

    static constexpr std::u16string_view RecommendedModelId(double ram_gib, double vram_gib, int32_t logical_processors) noexcept {
        if (ram_gib < 7.5) return {};
        if (ram_gib >= 47.5 && vram_gib >= 23.5) return u"gemma4-31b";
        if (ram_gib >= 31.5 && vram_gib >= 19.5) return u"gemma4-26b";
        if (ram_gib >= 23.5 && vram_gib >= 11.5 && logical_processors >= 8) return u"translategemma-12b";
        if (ram_gib >= 15.5 && vram_gib >= 7.5 && logical_processors >= 8) return u"hunyuan-mt-7b";
        return u"translategemma-4b";
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_transhardware_detect_ram_gib(double* out_ram_gib) noexcept {
    if (!out_ram_gib) return VN_ERR_INVALID_ARG;
    *out_ram_gib = vnotch::services::TranslationHardware::DetectSystemRamGiB();
    return VN_OK;
}

VN_EXPORT int32_t vn_transhardware_fits(
    double ram_gib, int32_t min_ram_gib, uint8_t* out_fits) noexcept
{
    if (!out_fits) return VN_ERR_INVALID_ARG;
    *out_fits = vnotch::services::TranslationHardware::Fits(ram_gib, min_ram_gib) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_transhardware_recommended_id(
    double ram_gib, double vram_gib, int32_t logical_processors,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    if (!out_len) return VN_ERR_INVALID_ARG;
    *out_len = 0;
    auto id = vnotch::services::TranslationHardware::RecommendedModelId(ram_gib, vram_gib, logical_processors);
    return vnotch::copy_out_u16(id, out_buf, out_cap, out_len);
}

} // extern "C"
