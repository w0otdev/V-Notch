#include "vnotch_common.h"

namespace vnotch::services
{

class BluetoothMonitorService final
{
    static bool ContainsAny(std::u16string_view name, std::span<const std::u16string_view> needles) noexcept
    {
        for (std::u16string_view n : needles)
        {
            if (vnotch::contains_icase_win(name, n))
            {
                return true;
            }
        }
        return false;
    }

public:
    static int32_t DetectDeviceType(std::u16string_view name) noexcept
    {
        static constexpr std::u16string_view kHeadphones[] = {
            u"airpods", u"buds", u"earbuds", u"earpods", u"wf-", u"wh-",
            u"headphone", u"earphone", u"airdots", u"freebuds", u"galaxy buds",
            u"jabra", u"beats", u"bose", u"sennheiser", u"sony wf", u"sony wh"
        };
        if (ContainsAny(name, kHeadphones)) return 1; // Headphones

        static constexpr std::u16string_view kSpeakers[] = {
            u"speaker", u"soundbar", u"jbl", u"marshall", u"harman",
            u"sonos", u"ue boom", u"flip"
        };
        if (ContainsAny(name, kSpeakers)) return 2; // Speaker

        static constexpr std::u16string_view kKeyboards[] = {
            u"keyboard", u"keychron", u"k380", u"mx keys"
        };
        if (ContainsAny(name, kKeyboards)) return 3; // Keyboard

        static constexpr std::u16string_view kMice[] = {
            u"mouse", u"mx master", u"mx anywhere", u"trackpad", u"magic mouse"
        };
        if (ContainsAny(name, kMice)) return 4; // Mouse

        static constexpr std::u16string_view kControllers[] = {
            u"controller", u"gamepad", u"xbox", u"dualsense",
            u"dualshock", u"pro controller", u"joy-con"
        };
        if (ContainsAny(name, kControllers)) return 5; // GameController

        static constexpr std::u16string_view kPhones[] = {
            u"iphone", u"galaxy", u"pixel", u"phone", u"oneplus", u"xiaomi"
        };
        if (ContainsAny(name, kPhones)) return 6; // Phone

        return 0; // Unknown
    }

    static int32_t ExtractNameStartIndex(std::u16string_view id) noexcept
    {
        for (size_t i = id.size(); i > 0; --i)
        {
            char16_t ch = id[i - 1];
            if (ch == u'#' || ch == u'\\')
            {
                return static_cast<int32_t>(i);
            }
        }
        return 0;
    }
};

} // namespace vnotch::services

VN_EXPORT int32_t vn_bluetooth_monitor_detect_device_type(
    const char16_t* name_ptr,
    int32_t name_len,
    int32_t* out_type) noexcept
{
    if (out_type == nullptr) return VN_ERR_INVALID_ARG;
    *out_type = vnotch::services::BluetoothMonitorService::DetectDeviceType(
        vnotch::make_u16_view(name_ptr, name_len));
    return VN_OK;
}

VN_EXPORT int32_t vn_bluetooth_monitor_extract_name_start(
    const char16_t* id_ptr,
    int32_t id_len,
    int32_t* out_start) noexcept
{
    if (out_start == nullptr) return VN_ERR_INVALID_ARG;
    *out_start = vnotch::services::BluetoothMonitorService::ExtractNameStartIndex(
        vnotch::make_u16_view(id_ptr, id_len));
    return VN_OK;
}
