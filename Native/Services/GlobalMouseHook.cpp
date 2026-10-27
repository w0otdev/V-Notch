#include "vnotch_common.h"
#include <cstdint>
#include <cstring>

namespace vnotch::services
{

class InputMonitorService final
{
public:
    static bool IsLeftButtonDown(const uint8_t* packet, int32_t length, int32_t ptr_size) noexcept
    {
        if (packet == nullptr || (ptr_size != 4 && ptr_size != 8))
        {
            return false;
        }
        int32_t header_size = 8 + 2 * ptr_size;
        if (length < header_size + 24)
        {
            return false;
        }
        uint32_t dw_type = 0;
        uint32_t declared_size = 0;
        uint16_t button_flags = 0;
        std::memcpy(&dw_type, packet, sizeof(uint32_t));
        if (dw_type != 0u)
        {
            return false;
        }
        std::memcpy(&declared_size, packet + 4, sizeof(uint32_t));
        if (declared_size < static_cast<uint32_t>(header_size + 24) ||
            declared_size > static_cast<uint32_t>(length))
        {
            return false;
        }
        std::memcpy(&button_flags, packet + header_size + 4, sizeof(uint16_t));
        return (button_flags & 1u) != 0u;
    }

    static constexpr void DecodeMessagePosition(uint32_t packed, int32_t& out_x, int32_t& out_y) noexcept
    {
        out_x = static_cast<int32_t>(static_cast<int16_t>(packed & 0xFFFFu));
        out_y = static_cast<int32_t>(static_cast<int16_t>((packed >> 16) & 0xFFFFu));
    }
};

} // namespace vnotch::services

extern "C"
{

VN_EXPORT int32_t vn_input_monitor_is_left_button_down(
    const uint8_t* packet_ptr,
    int32_t packet_len,
    int32_t ptr_size,
    uint8_t* out_is_down) noexcept
{
    if (out_is_down == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_is_down = vnotch::services::InputMonitorService::IsLeftButtonDown(packet_ptr, packet_len, ptr_size) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_input_monitor_decode_message_position(
    uint32_t packed,
    int32_t* out_x,
    int32_t* out_y) noexcept
{
    if (out_x == nullptr || out_y == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    vnotch::services::InputMonitorService::DecodeMessagePosition(packed, *out_x, *out_y);
    return VN_OK;
}

} // extern "C"
extern "C"
{

// Registers (or removes) raw mouse input for the window. Input is delivered even when unfocused.
VN_EXPORT int32_t __stdcall vn_input_register_raw_mouse(int64_t hwnd, uint8_t remove, int32_t* out_last_error) noexcept
{
    if (out_last_error != nullptr) *out_last_error = 0;
    RAWINPUTDEVICE device{};
    device.usUsagePage = 1;
    device.usUsage = 2;
    device.dwFlags = remove != 0 ? RIDEV_REMOVE : RIDEV_INPUTSINK;
    device.hwndTarget = remove != 0 ? nullptr : reinterpret_cast<HWND>(static_cast<intptr_t>(hwnd));
    SetLastError(0);
    if (RegisterRawInputDevices(&device, 1, sizeof(device)))
    {
        return VN_OK;
    }
    if (out_last_error != nullptr)
    {
        *out_last_error = static_cast<int32_t>(GetLastError());
    }
    return VN_ERR_FAIL;
}

// Decodes a WM_INPUT lParam. out_left_down is set when the packet is a mouse event with the left button down;
// out_x/out_y hold the cursor position of the message being processed.
VN_EXPORT int32_t __stdcall vn_input_read_left_click(
    int64_t raw_input_handle, uint8_t* out_left_down, int32_t* out_x, int32_t* out_y) noexcept
{
    if (out_left_down == nullptr || out_x == nullptr || out_y == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_left_down = 0;
    *out_x = 0;
    *out_y = 0;
    alignas(8) uint8_t buffer[64];
    UINT size = sizeof(buffer);
    const UINT read = GetRawInputData(
        reinterpret_cast<HRAWINPUT>(static_cast<intptr_t>(raw_input_handle)),
        RID_INPUT, buffer, &size, sizeof(RAWINPUTHEADER));
    if (read == static_cast<UINT>(-1) || read > sizeof(buffer))
    {
        return VN_ERR_FAIL;
    }
    if (!vnotch::services::InputMonitorService::IsLeftButtonDown(buffer, static_cast<int32_t>(read), static_cast<int32_t>(sizeof(void*))))
    {
        return VN_OK;
    }
    vnotch::services::InputMonitorService::DecodeMessagePosition(GetMessagePos(), *out_x, *out_y);
    *out_left_down = 1;
    return VN_OK;
}

} // extern "C"