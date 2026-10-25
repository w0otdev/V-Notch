#pragma once

#include <cstdint>

// Entry points of the AVX2 kernels (files named *.avx2.cpp, compiled with /arch:AVX2).
//
// Callers must check vnotch::cpu::SupportsX64V3() (vnotch_cpu.h) before calling any of them and
// must keep a baseline implementation for every operation. Kernels that process a bulk of the
// input return how much they consumed so the caller finishes the tail with the baseline code.
namespace vnotch::kernels::avx2
{
    // FastBlurKernels.avx2.cpp
    // Darkens BGRA pixels in [0, length) 32 bytes at a time, requires 0 <= factor <= 1. Returns the first unprocessed byte index.
    int32_t DarkenPixelsBulk(uint8_t* pixels, int32_t length, float factor) noexcept;
    void BoxBlurHorizontal(const uint8_t* source, uint8_t* target, int32_t w, int32_t h, int32_t radius) noexcept;
    void BoxBlurVertical(const uint8_t* source, uint8_t* target, int32_t w, int32_t h, int32_t radius) noexcept;

    // GlassBoxBlurKernels.avx2.cpp
    // Requires 2 * radius + 1 <= 4097 so the float divide stays exact.
    void GlassBoxBlurHorizontal(const uint8_t* src, uint8_t* dst, int32_t w, int32_t radius, int32_t y0, int32_t y1) noexcept;
    // Blurs columns in 32 byte strips starting at column_start. Returns the first unprocessed byte column.
    int32_t GlassBoxBlurVerticalBulk(const uint8_t* src, uint8_t* dst, int32_t w, int32_t h, int32_t radius, int32_t column_start, int32_t column_end) noexcept;

    // WebcamPixelKernels.avx2.cpp (each returns the first unprocessed pixel or byte index)
    int32_t WebcamRgbInPlaceBulk(uint8_t* pixels, int32_t length, uint8_t is_rgba, int32_t alpha_mode) noexcept;
    int32_t WebcamGray8Bulk(const uint8_t* src, int32_t pixel_count, uint8_t* dst) noexcept;
    int32_t WebcamNv12RowBulk(const uint8_t* luma_row, const uint8_t* chroma_row, int32_t width, uint8_t* dst_row) noexcept;
    int32_t WebcamYuy2Bulk(const uint8_t* src, int32_t pixel_count, uint8_t* dst) noexcept;

    // VisualizerFftKernel.avx2.cpp
    // Runs the radix-2 butterflies for k in [0, l1 & ~3) of every block. Returns the number of k values handled.
    int32_t FftStageBulk(float* flat, const float* stage, int32_t n, int32_t l1) noexcept;
}
