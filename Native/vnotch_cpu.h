#pragma once

#include <cstdint>

// Runtime CPU dispatch for VNotch.Native.dll.
//
// Every translation unit except *.avx2.cpp is compiled for baseline x64 (SSE2). Files named
// *.avx2.cpp are compiled with /arch:AVX2 and may only be entered after SupportsX64V3()
// returned true. They must not include this header, the STL or windows.h so that no
// AVX2-encoded COMDAT can be folded into baseline code by the linker.
namespace vnotch::cpu
{
    // True when the CPU and the OS both support the x86-64-v3 feature level that /arch:AVX2
    // may emit: AVX, AVX2, FMA, F16C, BMI1, BMI2, LZCNT, MOVBE and YMM state saving (XCR0).
    // The result is computed once and cached.
    bool SupportsX64V3() noexcept;
}

extern "C" __declspec(dllexport) int32_t __stdcall vn_cpu_supports_avx2() noexcept;
