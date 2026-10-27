#include "vnotch_common.h"
#include <algorithm>
#include <cmath>
#include <cstring>
#include <vector>

namespace vnotch::services
{

class InteractionSounds final
{
    static constexpr double kPi = 3.14159265358979323846;
    static constexpr int32_t kRate = 44100;
    static constexpr int32_t kVariantCount = 6;

    static constexpr int16_t kSplPressKernel[] = {
        0, -2, 0, 18, -4, 52, 59, 76, 172, 159, 179, 146, 533, 576, 842, 1629, 2212, 1239, -982, -4264, -6302, 1777, 7998, 7974, 6831, 3615, -2580, -5987, -6748, -6628, -4862, -3882, -5254, -3140, -3718, -6893, -4432, -3401, -962, 4025, -1696, -4852, -2883, -2446, 3456, 5786, 5554, 6543, 5057, 3379, 2524, 2854, 4576, 2966, -749, -2700, -1347, 1285, 1929, -34, -2167, -2053, -1595, -124, 748, 739, 1445, 1956, 2202, 1545, 1037, 442, -978, -1676, -847, -751, -1009, 235, 625, -230, -368, -527, -1092, 68, 543, -993, -1004, -367, -84, -197, -1230, -1222, 215, 1087, 467, -633, -864, -295, 315, 124, -1683, -2266, -226, 909, 352, -501, -314, 640, 973, 406, 60, 350, 809, 1382, 969, -83, 119, 1121, 996, 81, -758, -1133, -700, 352, 555, -1087, -1116, -940, -1188, -290, -168, 297, 651, 97, 500, 1091, 1406, 660, 207, 731, 394, 198, 83, -213, 428, 173, -184, 701, 294, 138, 393, 204, 159, -140, -302, -500, -545, -163, -545, -447, -22, -549, -1219, -1330, -873, -994, -1012, -506, -359, -127, -496, 233, 794, 24, 346, 614, 898, 1001, 378, 478, 615, 813, 322, -76, 353, 190, -108, -570, -554, -22, -167, -408, -518, -282, 18, -51, 34, 96, -30, 250, 534, 228, 142, -164, -215, 355, -115, -312, -218, -89, 334, 37, -43, -100, -290, 324, 154, -459, -705, -492, -26, 111, -35, -272, -261, -359, -356, 54, -80, -123, -93, -138, 16, -33, 215, 468, 230, -48, -381, -136, 135, -15, 76, 341, 441, 102, -186, -160, 13, 208, -135, -172, -123, -65, 257, 123, 175, 373, 358, 264, -181, -79, 202, 280, 285, 56, -112, -37, 64, -73, -157, -223, -159, 83, -66, -261, -252, -45, 36, -94, -35, -79, -116, 23, 124, 133, -33, -31, 44, 20, -12, -91, 15, 58, 37, 291, 240, 6, -28, 159, 266, 161, 153, 82, -15, -52, -92, 79, 65, -48, -60, -126, -111, -59, 17, 74, -60, -107, -82, -17, 77, 80, 169, 153, -1, -80, -101, 30, 86, 33, 0, -88, -55, 2, -34, -16, 41, 77, 54, -54, -58, 45, 98, 50, 11, 40, 33, 12, 0, 23, 56, -4, -32, -38, -43, -22, -16, -18, -22, -15, 17, 28, -8, -16, 8, 7, 1, 2, -5, -16, -21, -10, 16, 26, 8, -8, -8, -1, 3, 2, 0, -1, 0, 0
    };

    static constexpr int16_t kSplReleaseKernel[] = {
        0, 1, 4, 1, -2, -6, -22, -48, -110, -141, -162, -292, -381, -684, -1331, -1784, -1868, -973, 2437, 5417, 3498, -5794, -8000, -7948, -6434, -3222, -1940, 1132, 6121, 7543, 7352, 5861, 3538, 5296, 7164, 6723, 4532, 2095, -492, 591, 4216, 6401, 7082, 5817, 311, -4577, -5236, -4300, -3916, -4710, -5981, -6856, -7474, -7482, -6294, -4058, -2735, -2064, -1570, -1079, 845, 2761, 3128, 2827, 1475, -23, -598, -566, 272, 1024, 1029, 1056, 1350, 1310, 1639, 2032, 1453, 500, 75, 119, 308, 809, 678, 93, 148, 640, 953, 940, 1082, 1609, 2048, 1224, -127, -606, -379, 241, 515, 256, -90, 181, 686, 863, 693, -270, -431, -366, -991, -1472, -1733, -1490, -1389, -1749, -2075, -2123, -1465, -1080, -1264, -1560, -1497, -785, 37, 468, 465, 193, 238, 536, 775, 1523, 1898, 1418, 669, 188, 456, 673, 413, -46, -243, 267, 624, 678, 627, 708, 954, 913, 618, 380, 429, 422, 385, 319, 55, 130, 502, 467, -133, -564, -612, -403, -439, -701, -741, -1199, -1461, -1161, -806, -653, -858, -872, -456, -315, -549, -765, -558, -11, 341, 278, 99, 290, 454, 394, 506, 705, 949, 989, 863, 878, 917, 903, 689, 474, 463, 442, 480, 401, 188, -94, -183, -61, -200, -523, -693, -434, -233, -439, -574, -510, -241, -82, -268, -553, -647, -561, -401, -301, -323, -389, -408, -64, 311, 460, 370, -8, -114, 213, 635, 834, 624, 342, 206, 180, 144, 164, 224, 82, -224, -433, -358, -156, 20, 103, -35, -274, -501, -545, -326, -99, 23, 35, -31, -42, 53, 215, 343, 295, 84, -54, -104, -159, -192, -175, -25, 41, -126, -349, -296, -33, 93, 135, 105, 161, 203, 14, 52, 158, 215, 232, 85, 18, 42, 106, 157, 200, 241, 81, -34, -36, -24, -38, -91, -126, -146, -95, -68, -127, -194, -238, -122, -34, -60, -53, -90, -166, -185, -139, -21, 105, 153, 111, 58, 93, 162, 204, 188, 117, 82, 73, 83, 141, 131, 99, 49, -85, -163, -78, 100, 162, 44, -75, -113, -99, -75, -43, 33, 70, 8, -86, -152, -106, 17, 50, -5, -53, -70, -60, -46, -21, 18, 49, 37, -4, -15, -2, 33, 58, 30, 9, 21, 33, 39, 20, 0, 10, 14, 5, 0, -5, -1, 5, 5, 4, -3, -14, -24, -24, -7, 9, 8, -1, -5, -2, 1, 1, 0, 0
    };

    static double SampleKernel(std::span<const int16_t> kernel, double pos) noexcept
    {
        int32_t len = static_cast<int32_t>(kernel.size());
        if (pos <= 0.0 || pos >= static_cast<double>(len - 1))
        {
            return 0.0;
        }
        int32_t idx = static_cast<int32_t>(pos);
        double frac = pos - static_cast<double>(idx);
        double y0 = static_cast<double>(kernel[static_cast<size_t>(std::max(0, idx - 1))]);
        double y1 = static_cast<double>(kernel[static_cast<size_t>(idx)]);
        double y2 = static_cast<double>(kernel[static_cast<size_t>(idx + 1)]);
        double y3 = static_cast<double>(kernel[static_cast<size_t>(std::min(len - 1, idx + 2))]);
        double c0 = y1;
        double c1 = 0.5 * (y2 - y0);
        double c2 = y0 - 2.5 * y1 + 2.0 * y2 - 0.5 * y3;
        double c3 = 0.5 * (y3 - y0) + 1.5 * (y1 - y2);
        return ((c3 * frac + c2) * frac + c1) * frac + c0;
    }

    static void WriteU32LE(uint8_t* dst, uint32_t val) noexcept
    {
        std::memcpy(dst, &val, sizeof(val));
    }

    static void WriteU16LE(uint8_t* dst, uint16_t val) noexcept
    {
        std::memcpy(dst, &val, sizeof(val));
    }

    static void WriteI16LE(uint8_t* dst, int16_t val) noexcept
    {
        std::memcpy(dst, &val, sizeof(val));
    }

    static int16_t ReadI16LE(const uint8_t* src) noexcept
    {
        int16_t val = 0;
        std::memcpy(&val, src, sizeof(val));
        return val;
    }

public:
    static void GetCueParams(int32_t cue, double& dur, double& cut) noexcept
    {
        switch (cue)
        {
        case 0: dur = 0.022; cut = 0.085; break; // Click
        case 1: dur = 0.032; cut = 0.088; break; // ToggleOn
        case 2: dur = 0.031; cut = 0.080; break; // ToggleOff
        case 3: dur = 0.029; cut = 0.085; break; // Navigate
        case 4: dur = 0.021; cut = 0.090; break; // Select
        case 5: dur = 0.017; cut = 0.092; break; // Slider
        case 6: dur = 0.026; cut = 0.082; break; // Menu
        case 7: dur = 0.041; cut = 0.088; break; // Confirm
        case 8: dur = 0.030; cut = 0.076; break; // Delete
        case 9: dur = 0.026; cut = 0.080; break; // Transport
        default: dur = 0.022; cut = 0.085; break;
        }
    }

    static int32_t CreateWave(int32_t cue, int32_t variant, uint8_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
    {
        if (out_buf == nullptr || out_len == nullptr || variant < 0 || variant >= kVariantCount)
        {
            return VN_ERR_INVALID_ARG;
        }

        double dur = 0.022;
        double cut = 0.085;
        GetCueParams(cue, dur, cut);

        int32_t samples = static_cast<int32_t>(kRate * dur);
        int32_t byte_len = 44 + samples * 2;
        if (out_cap < byte_len)
        {
            return VN_ERR_BUFFER_TOO_SMALL;
        }

        double v_pitch = 1.0 + (static_cast<double>(variant) - 2.5) * 0.018;
        double v_time = 1.0 + (static_cast<double>(variant) - 2.5) * 0.010;
        std::span<const int16_t> k2(kSplReleaseKernel);

        double float_buf[4096]{};

        switch (cue)
        {
        case 0: // Click
        {
            int32_t start = static_cast<int32_t>(kRate * 0.0006);
            int32_t end = static_cast<int32_t>(kRate * 0.0136);
            for (int32_t i = start; i < end; ++i)
            {
                double dt = static_cast<double>(i - start) / kRate;
                double att = 0.5 * (1.0 - std::cos(kPi * std::min(1.0, dt / 0.0022)));
                double pos = (i - start) * 0.48 * v_pitch;
                double fade = (i > end - 110) ? 0.5 * (1.0 - std::cos(kPi * (end - 1 - i) / 110.0)) : 1.0;
                double warm_sub = 4600.0 * std::sin(2.0 * kPi * 165.0 * v_pitch * dt) * att * std::exp(-dt * 230.0);
                float_buf[static_cast<size_t>(i)] = fade * (0.55 * att * SampleKernel(k2, pos) + warm_sub);
            }
            break;
        }
        case 4: // Select
        {
            int32_t start = static_cast<int32_t>(kRate * 0.0006);
            for (int32_t i = start; i < samples - 1; ++i)
            {
                double dt = static_cast<double>(i - start) / kRate;
                double att = 0.5 * (1.0 - std::cos(kPi * std::min(1.0, dt / 0.0024)));
                double env = att * std::exp(-dt * 260.0);
                double wood = 5000.0 * (0.84 * std::sin(2.0 * kPi * 215.0 * v_pitch * dt) + 0.16 * std::sin(2.0 * kPi * 322.0 * v_pitch * dt)) * env;
                double tap = 0.28 * att * SampleKernel(k2, (i - start) * 0.54 * v_pitch);
                float_buf[static_cast<size_t>(i)] = wood + tap;
            }
            break;
        }
        case 5: // Slider
        {
            int32_t start = static_cast<int32_t>(kRate * 0.0005);
            for (int32_t i = start; i < samples - 1; ++i)
            {
                double dt = static_cast<double>(i - start) / kRate;
                double att = 0.5 * (1.0 - std::cos(kPi * std::min(1.0, dt / 0.0020)));
                double env = att * std::exp(-dt * 320.0);
                double detent = 5200.0 * std::sin(2.0 * kPi * 195.0 * v_pitch * dt) * env;
                double tick = 0.25 * att * SampleKernel(k2, (i - start) * 0.58 * v_pitch);
                float_buf[static_cast<size_t>(i)] = detent + tick;
            }
            break;
        }
        case 6: // Menu
        {
            int32_t start = static_cast<int32_t>(kRate * 0.0006);
            for (int32_t i = start; i < samples - 1; ++i)
            {
                double dt = static_cast<double>(i - start) / kRate;
                double att = 0.5 * (1.0 - std::cos(kPi * std::min(1.0, dt / 0.0026)));
                double env = att * std::exp(-dt * 195.0);
                double phase = 2.0 * kPi * v_pitch * (225.0 * dt + (90.0 / 150.0) * (std::exp(-dt * 150.0) - 1.0));
                double pop = 5800.0 * std::sin(phase) * env;
                double lip = 0.24 * att * SampleKernel(k2, (i - start) * 0.50 * v_pitch);
                float_buf[static_cast<size_t>(i)] = pop + lip;
            }
            break;
        }
        case 9: // Transport
        {
            int32_t start = static_cast<int32_t>(kRate * 0.0006);
            int32_t lock_start = static_cast<int32_t>(kRate * (0.0058 * v_time));
            for (int32_t i = start; i < samples - 1; ++i)
            {
                double dt = static_cast<double>(i - start) / kRate;
                double att = 0.5 * (1.0 - std::cos(kPi * std::min(1.0, dt / 0.0024)));
                double sub_env = att * std::exp(-dt * 200.0);
                double sub = 5600.0 * std::sin(2.0 * kPi * 145.0 * v_pitch * dt) * sub_env;
                double chug = 0.42 * att * SampleKernel(k2, (i - start) * 0.48 * v_pitch);
                double lock_click = (i >= lock_start) ? 0.38 * SampleKernel(k2, (i - lock_start) * 0.56 * v_pitch) : 0.0;
                float_buf[static_cast<size_t>(i)] = sub + chug + lock_click;
            }
            break;
        }
        case 1: // ToggleOn
        {
            int32_t s1 = static_cast<int32_t>(kRate * 0.0006);
            int32_t s2 = static_cast<int32_t>(kRate * (0.0108 * v_time));
            for (int32_t i = s1; i < samples - 1; ++i)
            {
                double dt1 = static_cast<double>(i - s1) / kRate;
                double att1 = 0.5 * (1.0 - std::cos(kPi * std::min(1.0, dt1 / 0.0024)));
                double env1 = att1 * std::exp(-dt1 * 240.0);
                float_buf[static_cast<size_t>(i)] += 0.28 * att1 * SampleKernel(k2, (i - s1) * 0.50 * v_pitch) +
                                                      4800.0 * std::sin(2.0 * kPi * 164.81 * v_pitch * dt1) * env1;
                if (i >= s2)
                {
                    double dt2 = static_cast<double>(i - s2) / kRate;
                    double att2 = 0.5 * (1.0 - std::cos(kPi * std::min(1.0, dt2 / 0.0024)));
                    double env2 = att2 * std::exp(-dt2 * 210.0);
                    float_buf[static_cast<size_t>(i)] += 0.30 * att2 * SampleKernel(k2, (i - s2) * 0.58 * v_pitch) +
                                                          5200.0 * (0.88 * std::sin(2.0 * kPi * 220.00 * v_pitch * dt2) +
                                                                    0.12 * std::sin(2.0 * kPi * 329.63 * v_pitch * dt2)) * env2;
                }
            }
            break;
        }
        case 2: // ToggleOff
        {
            int32_t s1 = static_cast<int32_t>(kRate * 0.0006);
            int32_t s2 = static_cast<int32_t>(kRate * (0.0105 * v_time));
            for (int32_t i = s1; i < samples - 1; ++i)
            {
                double dt1 = static_cast<double>(i - s1) / kRate;
                double att1 = 0.5 * (1.0 - std::cos(kPi * std::min(1.0, dt1 / 0.0024)));
                double env1 = att1 * std::exp(-dt1 * 250.0);
                float_buf[static_cast<size_t>(i)] += 0.28 * att1 * SampleKernel(k2, (i - s1) * 0.56 * v_pitch) +
                                                      4800.0 * std::sin(2.0 * kPi * 207.65 * v_pitch * dt1) * env1;
                if (i >= s2)
                {
                    double dt2 = static_cast<double>(i - s2) / kRate;
                    double att2 = 0.5 * (1.0 - std::cos(kPi * std::min(1.0, dt2 / 0.0024)));
                    double env2 = att2 * std::exp(-dt2 * 210.0);
                    float_buf[static_cast<size_t>(i)] += 0.34 * att2 * SampleKernel(k2, (i - s2) * 0.46 * v_pitch) +
                                                          5200.0 * std::sin(2.0 * kPi * 146.83 * v_pitch * dt2) * env2;
                }
            }
            break;
        }
        case 3: // Navigate
        {
            int32_t s1 = static_cast<int32_t>(kRate * 0.0006);
            int32_t s2 = static_cast<int32_t>(kRate * (0.0078 * v_time));
            for (int32_t i = s1; i < samples - 1; ++i)
            {
                double dt = static_cast<double>(i - s1) / kRate;
                double f_glide = (155.0 + 90.0 * std::min(1.0, dt / 0.022)) * v_pitch;
                double swoosh_env = std::sin(kPi * std::min(1.0, dt / 0.025)) * std::exp(-dt * 135.0);
                double tap = 0.30 * SampleKernel(k2, (i - s1) * 0.50 * v_pitch);
                if (i >= s2) tap += 0.35 * SampleKernel(k2, (i - s2) * 0.58 * v_pitch);
                float_buf[static_cast<size_t>(i)] = tap + 4600.0 * std::sin(2.0 * kPi * f_glide * dt) * swoosh_env;
            }
            break;
        }
        case 7: // Confirm
        {
            struct Note { double delay; double freq; double amp; double k_mix; double k_rate; };
            static constexpr Note notes[3] = {
                { 0.0006, 164.81, 4400.0, 0.22, 0.48 },
                { 0.0092, 207.65, 4800.0, 0.22, 0.54 },
                { 0.0182, 246.94, 5400.0, 0.26, 0.60 }
            };
            for (const auto& n : notes)
            {
                int32_t st = static_cast<int32_t>(kRate * n.delay * v_time);
                for (int32_t i = st; i < samples - 1; ++i)
                {
                    double dt = static_cast<double>(i - st) / kRate;
                    double att = 0.5 * (1.0 - std::cos(kPi * std::min(1.0, dt / 0.0024)));
                    double env = att * std::exp(-dt * 215.0);
                    double chime = n.amp * (0.90 * std::sin(2.0 * kPi * n.freq * v_pitch * dt) +
                                            0.10 * std::sin(2.0 * kPi * n.freq * 1.5 * v_pitch * dt)) * env;
                    double sp = n.k_mix * att * SampleKernel(k2, (i - st) * n.k_rate * v_pitch);
                    float_buf[static_cast<size_t>(i)] += chime + sp;
                }
            }
            break;
        }
        case 8: // Delete
        {
            int32_t s1 = static_cast<int32_t>(kRate * 0.0006);
            int32_t s2 = static_cast<int32_t>(kRate * (0.0086 * v_time));
            for (int32_t i = s1; i < samples - 1; ++i)
            {
                double dt = static_cast<double>(i - s1) / kRate;
                double att = 0.5 * (1.0 - std::cos(kPi * std::min(1.0, dt / 0.0024)));
                double env = att * std::exp(-dt * 195.0);
                double phase = 2.0 * kPi * v_pitch * (118.0 * dt + (77.0 / 140.0) * (1.0 - std::exp(-dt * 140.0)));
                double drop = 5400.0 * std::sin(phase) * env;
                double thud = 0.30 * att * SampleKernel(k2, (i - s1) * 0.50 * v_pitch);
                if (i >= s2) thud += 0.38 * SampleKernel(k2, (i - s2) * 0.42 * v_pitch);
                float_buf[static_cast<size_t>(i)] = drop + thud;
            }
            break;
        }
        default:
            break;
        }

        // Write 44-byte RIFF WAVE header
        std::memcpy(out_buf + 0, "RIFF", 4);
        WriteU32LE(out_buf + 4, static_cast<uint32_t>(36 + samples * 2));
        std::memcpy(out_buf + 8, "WAVEfmt ", 8);
        WriteU32LE(out_buf + 16, 16u);
        WriteU16LE(out_buf + 20, 1u);
        WriteU16LE(out_buf + 22, 1u);
        WriteU32LE(out_buf + 24, static_cast<uint32_t>(kRate));
        WriteU32LE(out_buf + 28, static_cast<uint32_t>(kRate * 2));
        WriteU16LE(out_buf + 32, 2u);
        WriteU16LE(out_buf + 34, 16u);
        std::memcpy(out_buf + 36, "data", 4);
        WriteU32LE(out_buf + 40, static_cast<uint32_t>(samples * 2));

        double lp1 = 0.0, lp2 = 0.0, lp3 = 0.0, lp4 = 0.0;
        double c = cut * (1.0 + (static_cast<double>(variant) - 2.5) * 0.012);
        int32_t click_end = (cue == 0) ? static_cast<int32_t>(kRate * 0.014) : samples;

        for (int32_t i = 0; i < samples; ++i)
        {
            uint8_t* sample_ptr = out_buf + 44 + i * 2;
            if (i == 0 || i == samples - 1 || i >= click_end)
            {
                WriteI16LE(sample_ptr, 0);
                continue;
            }
            double w = 1.0;
            if (i < 64)
            {
                w = 0.5 * (1.0 - std::cos(kPi * i / 64.0));
            }
            else if (i > click_end - 112)
            {
                w = 0.5 * (1.0 - std::cos(kPi * (click_end - 1 - i) / 112.0));
            }
            double v = float_buf[static_cast<size_t>(i)] * w;
            lp1 += c * (v - lp1);
            lp2 += c * (lp1 - lp2);
            lp3 += c * (lp2 - lp3);
            lp4 += c * (lp3 - lp4);
            WriteI16LE(sample_ptr, static_cast<int16_t>(std::nearbyint(lp4)));
        }

        NormalizeLoudness(out_buf, byte_len);
        *out_len = byte_len;
        return VN_OK;
    }

    static void NormalizeLoudness(uint8_t* wave, int32_t wave_len) noexcept
    {
        double energy = 0.0;
        int32_t peak = 0;
        int32_t count = (wave_len - 44) / 2;
        if (count <= 0) return;

        for (int32_t offset = 44; offset < wave_len; offset += 2)
        {
            int32_t sample = static_cast<int32_t>(ReadI16LE(wave + offset));
            energy += static_cast<double>(sample) * static_cast<double>(sample);
            peak = std::max(peak, std::abs(sample));
        }
        if (peak == 0) return;

        constexpr double kTargetRms = 1304.52;
        double gain = std::min(kTargetRms / std::sqrt(energy / static_cast<double>(count)), 6553.0 / static_cast<double>(peak));
        for (int32_t offset = 44; offset < wave_len; offset += 2)
        {
            int16_t val = ReadI16LE(wave + offset);
            WriteI16LE(wave + offset, static_cast<int16_t>(std::nearbyint(static_cast<double>(val) * gain)));
        }
    }

    static int32_t ClassifyActionName(std::u16string_view name, bool is_tab_item) noexcept
    {
        if (vnotch::contains_ordinal(name, u"Cancel") || vnotch::contains_ordinal(name, u"Close") || vnotch::contains_ordinal(name, u"Dismiss"))
            return 0; // Click
        if (vnotch::contains_ordinal(name, u"ClearSearch") || vnotch::contains_ordinal(name, u"SearchingClear") || vnotch::contains_ordinal(name, u"Pill") || vnotch::contains_ordinal(name, u"Reveal"))
            return 4; // Select
        if (is_tab_item || vnotch::starts_with_ordinal(name, u"Nav") || vnotch::contains_ordinal(name, u"Tab") || vnotch::contains_ordinal(name, u"IconButton") || vnotch::contains_ordinal(name, u"BackButton") || vnotch::contains_ordinal(name, u"Header"))
            return 3; // Navigate
        if (vnotch::contains_ordinal(name, u"Delete") || vnotch::contains_ordinal(name, u"Remove") || vnotch::contains_ordinal(name, u"Clear") || vnotch::contains_ordinal(name, u"Reset") || vnotch::contains_ordinal(name, u"Empty") || vnotch::contains_ordinal(name, u"Disconnect"))
            return 8; // Delete
        if (vnotch::contains_ordinal(name, u"Save") || vnotch::contains_ordinal(name, u"Apply") || vnotch::contains_ordinal(name, u"Copy") || vnotch::contains_ordinal(name, u"Confirm") || vnotch::contains_ordinal(name, u"Approve") || vnotch::contains_ordinal(name, u"Unlock") || vnotch::contains_ordinal(name, u"Connect") || vnotch::contains_ordinal(name, u"Export") || vnotch::contains_ordinal(name, u"Import") || vnotch::contains_ordinal(name, u"Download") || vnotch::contains_ordinal(name, u"Replace") || vnotch::contains_ordinal(name, u"TranslateButton"))
            return 7; // Confirm
        if (vnotch::contains_ordinal(name, u"Play") || vnotch::contains_ordinal(name, u"Pause") || vnotch::contains_ordinal(name, u"Next") || vnotch::contains_ordinal(name, u"Prev") || vnotch::contains_ordinal(name, u"CountdownStart") || vnotch::contains_ordinal(name, u"CountdownPlus") || vnotch::contains_ordinal(name, u"CountdownMinus") || vnotch::contains_ordinal(name, u"StopButton") || vnotch::contains_ordinal(name, u"MuteButton"))
            return 9; // Transport
        if (vnotch::starts_with_ordinal(name, u"Menu") || vnotch::contains_ordinal(name, u"More") || vnotch::contains_ordinal(name, u"SettingsButton"))
            return 6; // Menu
        return 0; // Click
    }
};

} // namespace vnotch::services

VN_EXPORT int32_t vn_interaction_sounds_create_wave(
    int32_t cue,
    int32_t variant,
    uint8_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
{
    return vnotch::services::InteractionSounds::CreateWave(cue, variant, out_buf, out_cap, out_len);
}

VN_EXPORT int32_t vn_interaction_sounds_classify_action(
    const char16_t* name_ptr,
    int32_t name_len,
    uint8_t is_tab_item,
    int32_t* out_cue) noexcept
{
    if (out_cue == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_cue = vnotch::services::InteractionSounds::ClassifyActionName(
        vnotch::make_u16_view(name_ptr, name_len),
        is_tab_item != 0u);
    return VN_OK;
}
