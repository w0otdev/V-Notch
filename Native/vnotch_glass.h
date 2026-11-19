#pragma once
#include "vnotch_api_types.h"

VN_EXPORT int32_t __stdcall vn_glass_build_refraction_lut(
    double f_power,
    double refraction_a,
    double refraction_b,
    double refraction_c,
    double refraction_d,
    double* out_lut,
    int32_t lut_len) noexcept;
VN_EXPORT int32_t __stdcall vn_glass_build_refraction_rows(
    const VnGlassMapParams* params,
    const double* lut,
    int32_t lut_len,
    int32_t y_start,
    int32_t y_end,
    int32_t* out_map) noexcept;
VN_EXPORT int32_t __stdcall vn_glass_refract_gather(
    const uint8_t* src,
    uint8_t* dst,
    const int32_t* refract_map,
    int32_t src_stride,
    int32_t start_pixel,
    int32_t end_pixel,
    uint8_t adjust_color,
    float sat_factor,
    float bright_add) noexcept;
VN_EXPORT int32_t __stdcall vn_glass_analyze_backdrop_samples(
    const int32_t* rgb_samples,
    int32_t sample_len,
    int32_t columns,
    int32_t rows,
    VnGlassBackdropOpticsOut* out_optics) noexcept;
VN_EXPORT int32_t __stdcall vn_glass_sample_backdrop_optics(
    const uint8_t* dib_bits,
    int32_t src_w,
    int32_t src_h,
    int32_t sample_x,
    int32_t sample_y,
    int32_t sample_w,
    int32_t sample_h,
    VnGlassBackdropOpticsOut* out_optics) noexcept;
VN_EXPORT int32_t __stdcall vn_glass_gaussian_box_radii(
    double sigma,
    int32_t passes,
    int32_t* out_radii,
    int32_t radii_cap,
    int32_t* out_written) noexcept;
VN_EXPORT int32_t __stdcall vn_glass_compute_capture_interval_ms(
    int32_t configured_fps,
    int32_t display_refresh_hz,
    uint8_t ultimate_gpu,
    double* out_interval_ms) noexcept;
VN_EXPORT int32_t __stdcall vn_glass_should_submit_frame(
    int64_t now_ticks,
    int64_t last_submit_ticks,
    int64_t stopwatch_freq,
    double active_interval_ms,
    uint8_t force_present,
    uint8_t* out_should_submit) noexcept;
VN_EXPORT int32_t __stdcall vn_glass_score_d3d9_adapter(
    const char16_t* description,
    int32_t desc_len,
    uint32_t vendor_id,
    int64_t* out_score) noexcept;
VN_EXPORT int32_t __stdcall vn_glass_compute_squircle_lut(
    double power,
    int32_t segments,
    double* out_pow_cos,
    double* out_pow_sin) noexcept;
VN_EXPORT int32_t __stdcall vn_glass_binder_compute_cpu_rim_opacities(
    double edge_highlight, double specular, double fresnel, double chroma, VnCpuRimOpacities* out_opacities) noexcept;
VN_EXPORT int32_t __stdcall vn_glass_binder_step_dynamic_fresnel(
    int64_t now_ticks, int64_t last_ticks, double active_fresnel_level,
    double light_x, double light_y, double contrast,
    uint8_t optic_r, uint8_t optic_g, uint8_t optic_b,
    double cur_x, double cur_y, double cur_contrast,
    uint8_t cur_tint_r, uint8_t cur_tint_g, uint8_t cur_tint_b,
    VnDynamicFresnelStep* out_step) noexcept;
VN_EXPORT int32_t __stdcall vn_glass_binder_read_system_transparency(uint8_t* out_enabled) noexcept;
