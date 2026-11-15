#include "vnotch_ext_ui.h"

#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <limits>
#include <string_view>

#include "Controls/UiValueMath.h"
using namespace vnotch::ui;

VN_EXPORT int32_t __stdcall vn_ai_usage_resolve_account_windows(
    const VnAiUsageWindowEntry* entries,
    int32_t count,
    int32_t* out_short_idx,
    int32_t* out_long_idx) noexcept
{
    if (!out_short_idx || !out_long_idx)
        return VN_ERR_INVALID_ARG;

    *out_short_idx = -1;
    *out_long_idx = -1;

    if (!entries || count <= 0)
        return VN_OK;

    bool use_codex_bucket = false;
    for (int32_t i = 0; i < count; ++i)
    {
        if (entries[i].is_codex_bucket != 0)
        {
            use_codex_bucket = true;
            break;
        }
    }

    int32_t five_hour_idx = -1;
    int32_t fallback_idx = -1;
    int32_t long_idx = -1;

    for (int32_t i = 0; i < count; ++i)
    {
        const auto& e = entries[i];
        const bool in_target_bucket = use_codex_bucket
            ? (e.is_codex_bucket != 0)
            : (e.matches_first_bucket != 0);
        if (!in_target_bucket)
            continue;

        if (e.has_duration != 0 && e.duration_minutes == 10080)
        {
            if (long_idx < 0)
                long_idx = i;
            continue;
        }

        if (fallback_idx < 0)
            fallback_idx = i;

        if (e.has_duration != 0 && e.duration_minutes == 300 && five_hour_idx < 0)
            five_hour_idx = i;
    }

    *out_short_idx = five_hour_idx >= 0 ? five_hour_idx : fallback_idx;
    *out_long_idx = long_idx;
    return VN_OK;
}

