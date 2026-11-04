#include "vnotch_common.h"
#include <cstdint>

namespace vnotch::services {

class NotchShellState final {
public:
    static constexpr double DefaultExpandedWidth = 480.0;
    static constexpr double CompactNoWidgetExpandedWidth = 328.0;

    static constexpr bool IsEffectivelyNotchVisible(bool is_notch_visible, bool is_hidden_by_fullscreen, bool is_hidden_by_idle) noexcept {
        return is_notch_visible && !is_hidden_by_fullscreen && !is_hidden_by_idle;
    }

    static constexpr double ComputeExpandedWidth(bool is_no_widget_mode, bool is_lyrics_active, bool is_music_expanded) noexcept {
        return (is_no_widget_mode && !is_lyrics_active && !is_music_expanded)
            ? CompactNoWidgetExpandedWidth
            : DefaultExpandedWidth;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_notchshell_is_effectively_visible(
    uint8_t is_notch_visible, uint8_t is_hidden_by_fullscreen, uint8_t is_hidden_by_idle,
    uint8_t* out_visible) noexcept
{
    if (!out_visible) return VN_ERR_INVALID_ARG;
    *out_visible = vnotch::services::NotchShellState::IsEffectivelyNotchVisible(
        is_notch_visible != 0, is_hidden_by_fullscreen != 0, is_hidden_by_idle != 0) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_notchshell_compute_expanded_width(
    uint8_t is_no_widget_mode,
    uint8_t is_lyrics_active,
    uint8_t is_music_expanded,
    double* out_width) noexcept
{
    if (!out_width) return VN_ERR_INVALID_ARG;
    *out_width = vnotch::services::NotchShellState::ComputeExpandedWidth(
        is_no_widget_mode != 0,
        is_lyrics_active != 0,
        is_music_expanded != 0);
    return VN_OK;
}

} // extern "C"
