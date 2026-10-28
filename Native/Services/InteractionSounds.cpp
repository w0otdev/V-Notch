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

    // PCM waveform from Assets/Sounds/click-01-ui-sound.wav (44100 Hz, 16-bit mono, 512 samples)
    static constexpr int16_t kClick01Kernel[] = {
        923, 9681, 19148, 24490, 27866, 27358, 24003, 18262, 10608, 2717, -4905, -11297, -15307, -16939, -16188, -12944, -8177, -2071, 4369, 10406, 15515, 18865, 20444, 20132, 17885, 14154, 9403, 4093, -1061, -5764, -9452, -11910,
        -12946, -12522, -10876, -8063, -4483, -425, 3679, 7548, 10852, 13273, 14698, 15088, 14329, 12566, 10061, 7000, 3693, 338, -2756, -5372, -7424, -8766, -9355, -9364, -8630, -7404, -5728, -3589, -1295, 1103, 3298, 5389,
        7190, 8577, 9362, 9698, 9618, 9075, 8339, 7030, 5503, 3780, 1994, 340, -1251, -2625, -3795, -4715, -5414, -5789, -5941, -5802, -5533, -5118, -4490, -3789, -2904, -1914, -909, 67, 1044, 2022, 2961, 3720,
        4388, 4951, 5373, 5588, 5631, 5635, 5448, 5195, 4743, 4159, 3582, 2904, 2221, 1508, 760, 46, -669, -1327, -1905, -2412, -2814, -3211, -3516, -3788, -3942, -4025, -4081, -4031, -3964, -3764, -3526, -3184,
        -2844, -2520, -2141, -1731, -1307, -902, -418, 29, 480, 888, 1358, 1777, 2121, 2497, 2787, 3056, 3216, 3396, 3545, 3612, 3667, 3611, 3471, 3301, 3089, 2834, 2504, 2068, 1663, 1239, 790, 338,
        -92, -489, -862, -1189, -1505, -1730, -1898, -2036, -2149, -2218, -2264, -2279, -2253, -2221, -2185, -2199, -2198, -2203, -2210, -2197, -2220, -2252, -2290, -2313, -2300, -2302, -2307, -2282, -2252, -2159, -2056, -1977,
        -1903, -1817, -1653, -1494, -1340, -1175, -1035, -871, -707, -568, -436, -340, -226, -115, -38, 34, 70, 107, 142, 179, 208, 216, 231, 243, 261, 287, 334, 375, 425, 486, 546, 618,
        685, 745, 794, 851, 882, 878, 881, 877, 860, 839, 790, 759, 738, 699, 692, 652, 611, 578, 551, 536, 482, 473, 449, 425, 398, 364, 344, 294, 259, 219, 195, 160,
        140, 129, 92, 48, -9, -44, -95, -129, -163, -193, -211, -242, -247, -264, -279, -305, -326, -335, -360, -374, -402, -426, -445, -476, -495, -518, -545, -560, -578, -594, -612, -627,
        -642, -661, -676, -696, -711, -728, -746, -761, -783, -797, -812, -830, -842, -857, -866, -875, -882, -884, -885, -886, -895, -904, -919, -938, -958, -982, -1000, -1019, -1030, -1037, -1036, -1033,
        -1031, -1025, -1024, -1025, -1030, -1034, -1042, -1050, -1058, -1064, -1068, -1072, -1070, -1067, -1062, -1057, -1051, -1043, -1038, -1033, -1031, -1031, -1032, -1036, -1041, -1046, -1052, -1055, -1058, -1058, -1057, -1055,
        -1053, -1052, -1053, -1056, -1061, -1068, -1075, -1081, -1086, -1087, -1086, -1084, -1080, -1076, -1073, -1072, -1073, -1074, -1075, -1074, -1072, -1067, -1060, -1051, -1043, -1036, -1031, -1030, -1030, -1032, -1034, -1036,
        -1036, -1033, -1030, -1026, -1021, -1017, -1014, -1011, -1008, -1005, -999, -992, -982, -971, -960, -948, -938, -930, -924, -919, -916, -914, -913, -913, -913, -915, -918, -921, -925, -927, -927, -925,
        -920, -913, -904, -895, -886, -879, -874, -871, -869, -868, -867, -866, -865, -866, -867, -869, -872, -874, -874, -871, -866, -860, -855, -854, -859, -870, -884, -896, -898, -881, -834, -751,
        -632, -479, -302, -115, 64, 215, 324, 379, 376, 319, 216, 81, -67, -212, -338, -434, -492, -509, -487, -432, -352, -257, -157, -62, 18, 76, 109, 115, 98, 61, 13, -39,
        -85, -121, -140, -143, -129, -102, -66, -27, 12, 45, 71, 88, 94, 90, 75, 52, 22, -12, -47, -78, -102, -117, -119, -110, -89, -62, -31, -2, 23, 38, 46, 49
    };

    // PCM waveform from Assets/Sounds/glass-tap-01-ui-sound.wav (44100 Hz, 16-bit mono, 1312 samples)
    static constexpr int16_t kGlassTap01Kernel[] = {
        545, -5680, -12284, -17435, -22754, -23198, -23198, -23198, -23198, -22494, -17490, -11802, -5207, 1128, 7264, 12257, 15895, 17930, 18129, 16599, 13274, 8567, 2799, -3460, -9844, -15701, -20637, -23198, -23198, -23198, -23198, -20714,
        -15792, -9852, -3340, 3189, 9217, 14315, 18045, 20139, 20394, 18734, 15353, 10485, 4626, -1806, -8307, -14332, -19431, -23088, -23198, -23198, -23198, -20436, -15650, -9876, -3481, 2922, 8951, 13971, 17755, 19876, 20169, 18732,
        15488, 11048, 5589, -439, -6504, -12194, -16902, -20426, -22301, -22467, -21018, -17959, -13656, -8368, -2670, 3000, 8277, 12676, 15889, 17606, 17806, 16398, 13467, 9315, 4332, -1006, -6325, -11186, -15182, -18012, -19496, -19458,
        -17901, -14986, -10957, -6161, -964, 4173, 8819, 12628, 15298, 16610, 16480, 14911, 12036, 8067, 3405, -1604, -6551, -10980, -14630, -17093, -18307, -18129, -16495, -13662, -9760, -5214, -350, 4469, 8832, 12397, 14879, 16115,
        15968, 14529, 11816, 8086, 3658, -1177, -5906, -10265, -13861, -16356, -17648, -17549, -16121, -13476, -9759, -5390, -653, 4076, 8404, 12055, 14643, 16019, 16039, 14708, 12189, 8604, 4323, -401, -5113, -9396, -13012, -15600,
        -17041, -17159, -15938, -13520, -10044, -5834, -1199, 3475, 7826, 11540, 14250, 15785, 16033, 14977, 12726, 9362, 5233, 678, -3960, -8296, -12029, -14789, -16418, -16765, -15832, -13667, -10415, -6387, -1812, 2815, 7193, 10941,
        13736, 15457, 15854, 15019, 12944, 9788, 5843, 1362, -3215, -7564, -11292, -14136, -15869, -16322, -15500, -13450, -10381, -6487, -2073, 2455, 6750, 10440, 13271, 15026, 15523, 14760, 12780, 9761, 5955, 1640, -2801, -7055,
        -10735, -13550, -15287, -15782, -15075, -13164, -10220, -6481, -2232, 2144, 6338, 9993, 12837, 14638, 15225, 14577, 12738, 9875, 6227, 2051, -2286, -6440, -10093, -12926, -14756, -15394, -14807, -13059, -10265, -6690, -2590, 1691,
        5820, 9457, 12318, 14190, 14894, 14412, 12752, 10074, 6602, 2593, -1608, -5698, -9318, -12188, -14094, -14872, -14470, -12918, -10350, -6978, -3073, 1052, 5068, 8660, 11542, 13483, 14329, 14024, 12600, 10166, 6927, 3149,
        -866, -4795, -8325, -11170, -13112, -14000, -13766, -12437, -10128, -7028, -3392, 493, 4311, 7756, 10553, 12478, 13389, 13212, 11973, 9774, 6796, 3292, -464, -4159, -7503, -10226, -12107, -13005, -12848, -11660, -9538, -6661,
        -3264, 375, 3959, 7203, 9840, 11667, 12535, 12380, 11224, 9162, 6368, 3071, -459, -3933, -7072, -9623, -11382, -12210, -12045, -10905, -8889, -6163, -2951, 486, 3868, 6922, 9402, 11111, 11915, 11752, 10640, 8673,
        6016, 2885, -464, -3759, -6735, -9152, -10818, -11601, -11443, -10361, -8445, -5855, -2802, 464, 3680, 6584, 8943, 10572, 11339, 11188, 10136, 8273, 5753, 2784, -391, -3517, -6337, -8626, -10202, -10942, -10789, -9762,
        -7950, -5501, -2622, 456, 3479, 6204, 8413, 9930, 10636, 10481, 9483, 7728, 5365, 2588, -375, -3281, -5897, -8014, -9462, -10130, -9971, -9002, -7308, -5031, -2361, 486, 3275, 5783, 7808, 9190, 9822, 9658,
        8716, 7079, 4883, 2309, -429, -3111, -5519, -7460, -8777, -9370, -9193, -8267, -6669, -4533, -2037, 617, 3209, 5532, 7396, 8652, 9202, 9003, 8076, 6500, 4406, 1966, -617, -3133, -5377, -7167, -8359, -8859,
        -8629, -7691, -6126, -4064, -1675, 843, 3286, 5452, 7168, 8296, 8746, 8487, 7543, 5996, 3974, 1645, -799, -3157, -5239, -6876, -7940, -8345, -8064, -7123, -5606, -3635, -1378, 983, 3254, 5252, 6816, 7823,
        8195, 7906, 6982, 5505, 3597, 1414, -863, -3050, -4971, -6473, -7435, -7784, -7494, -6595, -5161, -3312, -1202, 1000, 3111, 4965, 6410, 7334, 7664, 7376, 6499, 5106, 3315, 1272, -855, -2894, -4680, -6070,
        -6953, -7263, -6976, -6122, -4772, -3039, -1067, 984, 2946, 4664, 5998, 6844, 7137, 6857, 6033, 4733, 3068, 1173, -796, -2681, -4330, -5610, -6424, -6707, -6442, -5654, -4409, -2813, -995, 896, 2708, 4294,
        5529, 6317, 6596, 6348, 5598, 4408, 2878, 1134, -682, -2423, -3949, -5139, -5899, -6170, -5936, -5218, -4077, -2610, -936, 807, 2479, 3944, 5087, 5818, 6081, 5860, 5175, 4086, 2684, 1085, -582, -2180,
        -3584, -4682, -5387, -5649, -5449, -4808, -3783, -2459, -945, 636, 2157, 3498, 4553, 5240, 5511, 5348, 4768, 3823, 2592, 1175, -312, -1751, -3029, -4046, -4726, -5019, -4906, -4403, -3553, -2429, -1122, 262,
        1613, 2825, 3805, 4480, 4799, 4744, 4323, 3576, 2565, 1373, 96, -1162, -2306, -3246, -3911, -4254, -4251, -3909, -3259, -2357, -1276, -107, 1059, 2130, 3024, 3670, 4024, 4061, 3784, 3219, 2416, 1441,
        375, -697, -1690, -2525, -3140, -3489, -3550, -3322, -2828, -2111, -1234, -267, 711, 1622, 2395, 2972, 3310, 3389, 3206, 2780, 2151, 1371, 505, -377, -1206, -1917, -2459, -2791, -2893, -2760, -2410, -1873,
        -1195, -433, 355, 1105, 1761, 2275, 2611, 2746, 2676, 2410, 1973, 1403, 746, 55, -619, -1223, -1715, -2059, -2234, -2230, -2053, -1720, -1261, -712, -118, 478, 1029, 1497, 1848, 2062, 2125, 2037,
        1810, 1463, 1027, 534, 25, -466, -902, -1254, -1499, -1624, -1623, -1500, -1269, -949, -568, -152, 268, 662, 1004, 1271, 1450, 1529, 1508, 1392, 1190, 922, 605, 264, -79, -401, -680, -901,
        -1051, -1122, -1111, -1022, -863, -644, -383, -95, 199, 481, 734, 942, 1092, 1175, 1188, 1129, 1004, 820, 590, 328, 51, -222, -475, -691, -856, -958, -991, -951, -842, -668, -440, -174,
        114, 406, 680, 920, 1106, 1225, 1269, 1231, 1114, 921, 665, 365, 37, -294, -604, -871, -1075, -1198, -1230, -1165, -1005, -761, -447, -86, 297, 674, 1016, 1296, 1492, 1587, 1570, 1441,
        1208, 884, 493, 65, -370, -776, -1124, -1383, -1533, -1560, -1458, -1235, -905, -493, -28, 455, 916, 1322, 1638, 1840, 1909, 1839, 1633, 1305, 879, 386, -135, -645, -1104, -1476, -1731, -1848,
        -1814, -1634, -1317, -887, -375, 178, 732, 1243, 1671, 1982, 2150, 2161, 2013, 1715, 1289, 766, 186, -405, -964, -1445, -1810, -2030, -2087, -1974, -1700, -1284, -757, -160, 461, 1057, 1582, 1995,
        2261, 2360, 2284, 2036, 1636, 1114, 512, -125, -746, -1303, -1752, -2057, -2195, -2153, -1936, -1559, -1052, -456, 185, 819, 1397, 1874, 2212, 2385, 2380, 2196, 1848, 1364, 781, 144, -497, -1092,
        -1594, -1965, -2174, -2207, -2061, -1746, -1288, -721, -90, 556, 1165, 1693, 2096, 2343, 2415, 2306, 2023, 1591, 1040, 416, -234, -859, -1410, -1844, -2126, -2234, -2161, -1910, -1502, -971, -355, 296,
        930, 1498, 1955, 2265, 2404, 2362, 2141, 1759, 1247, 645, 1, -635, -1212, -1686, -2018, -2184, -2171, -1979, -1625, -1137, -554, 79, 711, 1293, 1778, 2131, 2322, 2338, 2177, 1853, 1393, 830,
        212, -416, -1002, -1501, -1875, -2095, -2144, -2018, -1727, -1296, -757, -152, 470, 1061, 1576, 1974, 2224, 2308, 2219, 1963, 1562, 1047, 457, -160, -757, -1288, -1710, -1992, -2113, -2061, -1842, -1474,
        -985, -414, 195, 795, 1338, 1783, 2094, 2248, 2234, 2053, 1718, 1258, 707, 108, -490, -1042, -1505, -1842, -2028, -2047, -1899, -1596, -1161, -629, -41, 556, 1115, 1593, 1952, 2164, 2212, 2092,
        1814, 1400, 882, 301, -296, -863, -1354, -1731, -1963, -2033, -1932, -1671, -1269, -759, -181, 421, 996, 1500, 1892, 2139, 2225, 2139, 1890, 1496, 988, 408, -198, -782, -1298, -1703, -1967, -2066,
        -1993, -1754, -1366, -862, -281, 331, 927, 1457, 1880, 2161, 2280, 2224, 2000, 1623, 1124, 541, -79, -688, -1237, -1684, -1992, -2137, -2106, -1902, -1540, -1048, -465, 165, 791, 1365, 1841, 2180,
        2356, 2353, 2172, 1825, 1339, 752, 110, -537, -1137, -1643, -2014, -2219, -2242, -2080, -1744, -1261, -668, -12, 656, 1280, 1812, 2209, 2437, 2479, 2329, 1999, 1515, 914, 245, -439, -1082, -1634,
        -2049, -2292, -2345, -2200, -1872, -1383, -774, -94, 603, 1262, 1828, 2255, 2509, 2568, 2428, 2098, 1604, 986, 295, -416, -1090, -1669, -2109, -2374, -2441, -2304, -1974, -1475, -849, -144, 581, 1271,
        1869, 2326, 2604, 2681, 2549, 2219, 1716, 1079, 360, -386, -1097, -1716, -2193, -2489, -2580, -2457, -2128, -1620, -973, -237, 528, 1262, 1904, 2403, 2719, 2825, 2712, 2387, 1877, 1220, 470, -314,
        -1069, -1734, -2256, -2592, -2714, -2612, -2294, -1783, -1121, -360, 438, 1211, 1897, 2439, 2793, 2932, 2842, 2531, 2023, 1358, 590, -220, -1008, -1710, -2268, -2639, -2791, -2712, -2408, -1903, -1237, -462,
        357, 1157, 1871, 2444, 2828, 2991, 2921, 2623, 2120, 1453, 674, -152, -961, -1686, -2269, -2663, -2835, -2772, -2479, -1979, -1311, -531, 300, 1114, 1846, 2437, 2839, 3019, 2963, 2677, 2181, 1517,
        738, -92, -909, -1645, -2241, -2650, -2839, -2792, -2514, -2027, -1371, -598, 229, 1044, 1781, 2382, 2797, 2995, 2959, 2692, 2217, 1572, 808, -12, -823, -1560, -2164, -2586, -2793, -2767, -2513, -2050,
        -1416, -662, 151, 957, 1693, 2298, 2725, 2939, 2924, 2681, 2230, 1608, 864, 61, -739, -1471, -2076, -2505, -2725, -2719, -2486, -2047, -1438, -708, 84, 874, 1599, 2200, 2629, 2852, 2853, 2631
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
        case 6: dur = 0.016; cut = 1.000; break; // Menu / Open Settings (click-01-ui-sound.wav)
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
        case 6: // Menu / Open Settings (click-01-ui-sound.wav)
        {
            std::span<const int16_t> click01(kClick01Kernel);
            constexpr int32_t start = 16;
            const double max_pos = static_cast<double>(click01.size() - 1);
            for (int32_t i = start; i < samples - 1; ++i)
            {
                double pos = static_cast<double>(i - start) * v_pitch;
                if (pos >= max_pos) break;
                double w = 1.0;
                if (i - start < 6)
                {
                    w = 0.5 * (1.0 - std::cos(kPi * static_cast<double>(i - start) / 6.0));
                }
                else if (pos > max_pos - 32.0)
                {
                    w = 0.5 * (1.0 - std::cos(kPi * (max_pos - pos) / 32.0));
                }
                float_buf[static_cast<size_t>(i)] = SampleKernel(click01, pos) * w;
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
        case 1: // ToggleOn (glass-tap-01-ui-sound.wav)
        case 2: // ToggleOff (glass-tap-01-ui-sound.wav)
        {
            std::span<const int16_t> glass(kGlassTap01Kernel);
            constexpr int32_t start = 16;
            const double rate = v_pitch * (cue == 1 ? 1.0 : 0.94);
            const double max_pos = static_cast<double>(glass.size() - 1);
            for (int32_t i = start; i < samples - 1; ++i)
            {
                double pos = static_cast<double>(i - start) * rate;
                if (pos >= max_pos) break;
                double w = 1.0;
                if (i - start < 6)
                {
                    w = 0.5 * (1.0 - std::cos(kPi * static_cast<double>(i - start) / 6.0));
                }
                else if (i > samples - 96)
                {
                    w = 0.5 * (1.0 - std::cos(kPi * static_cast<double>(samples - 1 - i) / 96.0));
                }
                float_buf[static_cast<size_t>(i)] = SampleKernel(glass, pos) * w;
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
            if (cue == 1 || cue == 2 || cue == 6)
            {
                WriteI16LE(sample_ptr, static_cast<int16_t>(std::nearbyint(float_buf[static_cast<size_t>(i)])));
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

        constexpr double kTargetRms = 5547.34;
        constexpr double kMaxPeak = 27866.0;
        double gain = std::min(kTargetRms / std::sqrt(energy / static_cast<double>(count)), kMaxPeak / static_cast<double>(peak));
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
