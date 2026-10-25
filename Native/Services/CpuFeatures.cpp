#include "../vnotch_cpu.h"
#include <intrin.h>

namespace vnotch::cpu
{
    namespace
    {
        bool DetectX64V3() noexcept
        {
            int regs[4] = {};
            __cpuid(regs, 0);
            if (regs[0] < 7)
                return false;

            __cpuid(regs, 1);
            const uint32_t ecx1 = static_cast<uint32_t>(regs[2]);
            constexpr uint32_t kFma = 1u << 12;
            constexpr uint32_t kMovbe = 1u << 22;
            constexpr uint32_t kOsXsave = 1u << 27;
            constexpr uint32_t kAvx = 1u << 28;
            constexpr uint32_t kF16c = 1u << 29;
            constexpr uint32_t required1 = kFma | kMovbe | kOsXsave | kAvx | kF16c;
            if ((ecx1 & required1) != required1)
                return false;

            // XCR0 bits 1 and 2: the OS saves XMM and YMM state across context switches.
            if ((_xgetbv(0) & 0x6u) != 0x6u)
                return false;

            __cpuidex(regs, 7, 0);
            const uint32_t ebx7 = static_cast<uint32_t>(regs[1]);
            constexpr uint32_t kBmi1 = 1u << 3;
            constexpr uint32_t kAvx2 = 1u << 5;
            constexpr uint32_t kBmi2 = 1u << 8;
            constexpr uint32_t required7 = kBmi1 | kAvx2 | kBmi2;
            if ((ebx7 & required7) != required7)
                return false;

            __cpuid(regs, static_cast<int>(0x80000000u));
            if (static_cast<uint32_t>(regs[0]) < 0x80000001u)
                return false;
            __cpuid(regs, static_cast<int>(0x80000001u));
            constexpr uint32_t kLzcnt = 1u << 5;
            return (static_cast<uint32_t>(regs[2]) & kLzcnt) != 0;
        }
    }

    bool SupportsX64V3() noexcept
    {
        static const bool supported = DetectX64V3();
        return supported;
    }
}

extern "C" __declspec(dllexport) int32_t __stdcall vn_cpu_supports_avx2() noexcept
{
    return vnotch::cpu::SupportsX64V3() ? 1 : 0;
}
