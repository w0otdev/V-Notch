#include "vnotch_common.h"
#include "vnotch_cpu.h"
#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <immintrin.h>
#include <limits>
#include <string_view>


extern "C" {
VN_EXPORT int32_t __stdcall vn_audio_mixer_select_icon_glyph(
    float volume,
    uint8_t muted,
    int32_t* out_codepoint) noexcept
{
    if (!out_codepoint)
    {
        return VN_ERR_INVALID_ARG;
    }
    if (muted != 0 || !std::isfinite(volume) || volume <= 0.01f)
    {
        *out_codepoint = 0xE74F;
    }
    else if (volume < 0.33f)
    {
        *out_codepoint = 0xE993;
    }
    else if (volume < 0.66f)
    {
        *out_codepoint = 0xE994;
    }
    else
    {
        *out_codepoint = 0xE995;
    }
    return VN_OK;
}


}
