#include "vnotch_ext_core.h"

#include <tlhelp32.h>
#include <devpropdef.h>
#include <cfgmgr32.h>
#include <cwchar>

#pragma comment(lib, "cfgmgr32.lib")

#include "Parsing/WindowsText.h"
using namespace vnotch::windows_text;

VN_EXPORT int32_t __stdcall vn_bluetooth_battery_parse_percentage(
    const uint8_t* buf,
    int32_t len,
    int32_t* out_percentage) noexcept
{
    if (!out_percentage || len < 0 || (len > 0 && !buf))
        return VN_ERR_INVALID_ARG;

    *out_percentage = (len == 1 && buf[0] <= 100) ? static_cast<int32_t>(buf[0]) : -1;
    return VN_OK;
}

namespace
{
    constexpr DEVPROPKEY kPropBatteryLevel = {
        { 0x104EA319, 0x6EE2, 0x4701, { 0xBD, 0x47, 0x8D, 0xDB, 0xF4, 0x25, 0xBB, 0xE5 } }, 2
    };
    constexpr DEVPROPKEY kPropBatteryLife = {
        { 0x49CD1F76, 0x5626, 0x4B17, { 0xA4, 0xE8, 0x18, 0xB4, 0xAA, 0x1A, 0x22, 0x13 } }, 10
    };
    constexpr DEVPROPKEY kPropFriendlyName = {
        { 0xA45C254E, 0xDF1C, 0x4EFD, { 0x80, 0x20, 0x67, 0xD1, 0x46, 0xA8, 0x50, 0xE0 } }, 14
    };
    constexpr DEVPROPKEY kPropDescription = {
        { 0xA45C254E, 0xDF1C, 0x4EFD, { 0x80, 0x20, 0x67, 0xD1, 0x46, 0xA8, 0x50, 0xE0 } }, 2
    };
    constexpr DEVPROPKEY kPropContainerId = {
        { 0x8C7ED206, 0x3F8A, 0x4827, { 0xB3, 0xAB, 0xAE, 0x9E, 0x1F, 0xAE, 0xFC, 0x6C } }, 2
    };

    bool equals_ordinal_icase(std::wstring_view a, std::wstring_view b) noexcept
    {
        if (a.size() != b.size()) return false;
        if (a.empty()) return true;
        return ::CompareStringOrdinal(
            a.data(), static_cast<int>(a.size()),
            b.data(), static_cast<int>(b.size()),
            TRUE) == CSTR_EQUAL;
    }

    bool enumerate_id_multi_sz(const wchar_t* enumerator, bool present_only, std::vector<wchar_t>& out_buf)
    {
        out_buf.clear();
        const ULONG flags = CM_GETIDLIST_FILTER_ENUMERATOR | (present_only ? CM_GETIDLIST_FILTER_PRESENT : 0u);
        for (int attempt = 0; attempt < 3; ++attempt)
        {
            ULONG size = 0;
            if (::CM_Get_Device_ID_List_SizeW(&size, enumerator, flags) != CR_SUCCESS || size < 2 || size > 1048576u)
                return false;

            out_buf.assign(static_cast<size_t>(size) + 1u, L'\0');
            const CONFIGRET cr = ::CM_Get_Device_ID_ListW(enumerator, out_buf.data(), size, flags);
            if (cr == CR_SUCCESS)
                return true;
            if (cr != CR_BUFFER_SMALL)
                break;
        }
        out_buf.clear();
        return false;
    }

    void split_multi_sz(std::vector<wchar_t>& buf, std::vector<wchar_t*>& out_ptrs)
    {
        out_ptrs.clear();
        size_t i = 0;
        const size_t n = buf.size();
        while (i < n)
        {
            while (i < n && buf[i] == L'\0')
                ++i;
            if (i >= n)
                break;
            wchar_t* start = buf.data() + i;
            while (i < n && buf[i] != L'\0')
                ++i;
            if (i < n && start[0] != L'\0')
                out_ptrs.push_back(start);
        }
    }

    int32_t read_devnode_percentage(DEVINST node, const DEVPROPKEY& key) noexcept
    {
        uint8_t prop_buf[16]{};
        ULONG cb = sizeof(prop_buf);
        DEVPROPTYPE prop_type = 0;
        if (::CM_Get_DevNode_PropertyW(node, &key, &prop_type, prop_buf, &cb, 0) != CR_SUCCESS ||
            prop_type != DEVPROP_TYPE_BYTE || cb != 1)
        {
            return -1;
        }
        return prop_buf[0] <= 100 ? static_cast<int32_t>(prop_buf[0]) : -1;
    }

    bool read_devnode_string(DEVINST node, const DEVPROPKEY& key, wchar_t* out_str, size_t max_chars, uint16_t* out_len) noexcept
    {
        uint8_t prop_buf[4096]{};
        ULONG cb = sizeof(prop_buf);
        DEVPROPTYPE prop_type = 0;
        if (::CM_Get_DevNode_PropertyW(node, &key, &prop_type, prop_buf, &cb, 0) != CR_SUCCESS ||
            prop_type != DEVPROP_TYPE_STRING || cb > sizeof(prop_buf) || (cb & 1u) != 0)
        {
            return false;
        }

        const wchar_t* wptr = reinterpret_cast<const wchar_t*>(prop_buf);
        size_t wlen = static_cast<size_t>(cb / sizeof(wchar_t));
        while (wlen > 0 && wptr[wlen - 1] == L'\0')
            --wlen;

        bool any_non_ws = false;
        for (size_t i = 0; i < wlen; ++i)
        {
            if (!is_dotnet_whitespace(wptr[i]))
            {
                any_non_ws = true;
                break;
            }
        }
        if (!any_non_ws)
            return false;

        const size_t copy_len = std::min(wlen, max_chars - 1);
        std::memcpy(out_str, wptr, copy_len * sizeof(wchar_t));
        out_str[copy_len] = L'\0';
        *out_len = static_cast<uint16_t>(copy_len);
        return true;
    }

    bool is_empty_guid(const GUID& g) noexcept
    {
        static constexpr GUID kZero{};
        return std::memcmp(&g, &kZero, sizeof(GUID)) == 0;
    }
}

VN_EXPORT int32_t __stdcall vn_bluetooth_battery_read_devices(
    VnCoreBtBatteryDevice* out_devices,
    int32_t cap,
    int32_t* out_total_count) noexcept
{
    if (!out_total_count || cap < 0 || (cap > 0 && !out_devices))
        return VN_ERR_INVALID_ARG;

    *out_total_count = 0;

    try
    {
        static constexpr const wchar_t* kEnumerators[3] = {
            L"BTHENUM",
            L"BTHLE",
            L"BTHLEDEVICE"
        };

        std::vector<VnCoreBtBatteryDevice> collected;
        collected.reserve(16);

        std::vector<wchar_t> present_buf;
        std::vector<wchar_t*> present_ids;
        std::vector<wchar_t> all_buf;
        std::vector<wchar_t*> all_ids;

        for (const wchar_t* enumerator : kEnumerators)
        {
            present_ids.clear();
            if (enumerate_id_multi_sz(enumerator, true, present_buf))
                split_multi_sz(present_buf, present_ids);

            all_ids.clear();
            if (!enumerate_id_multi_sz(enumerator, false, all_buf))
                continue;
            split_multi_sz(all_buf, all_ids);

            for (wchar_t* instance_id : all_ids)
            {
                DEVINST node = 0;
                if (::CM_Locate_DevNodeW(&node, instance_id, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS)
                    continue;

                int32_t percentage = read_devnode_percentage(node, kPropBatteryLevel);
                if (percentage < 0)
                    percentage = read_devnode_percentage(node, kPropBatteryLife);
                if (percentage < 0)
                    continue;

                VnCoreBtBatteryDevice entry{};
                entry.percentage = percentage;

                GUID container_guid{};
                ULONG guid_cb = sizeof(container_guid);
                DEVPROPTYPE guid_type = 0;
                const std::wstring_view inst_sv(instance_id);

                if (::CM_Get_DevNode_PropertyW(
                        node, &kPropContainerId, &guid_type,
                        reinterpret_cast<PBYTE>(&container_guid), &guid_cb, 0) == CR_SUCCESS &&
                    guid_type == DEVPROP_TYPE_GUID && guid_cb == sizeof(GUID) && !is_empty_guid(container_guid))
                {
                    const int written = ::swprintf_s(
                        entry.id,
                        L"bluetooth:%08lx-%04x-%04x-%02x%02x-%02x%02x%02x%02x%02x%02x",
                        container_guid.Data1,
                        static_cast<unsigned int>(container_guid.Data2),
                        static_cast<unsigned int>(container_guid.Data3),
                        static_cast<unsigned int>(container_guid.Data4[0]),
                        static_cast<unsigned int>(container_guid.Data4[1]),
                        static_cast<unsigned int>(container_guid.Data4[2]),
                        static_cast<unsigned int>(container_guid.Data4[3]),
                        static_cast<unsigned int>(container_guid.Data4[4]),
                        static_cast<unsigned int>(container_guid.Data4[5]),
                        static_cast<unsigned int>(container_guid.Data4[6]),
                        static_cast<unsigned int>(container_guid.Data4[7]));
                    entry.id_len = written > 0 ? static_cast<uint16_t>(written) : 0;
                }
                else
                {
                    const size_t copy_len = std::min(inst_sv.size(), std::size(entry.id) - 1);
                    std::memcpy(entry.id, inst_sv.data(), copy_len * sizeof(wchar_t));
                    entry.id[copy_len] = L'\0';
                    entry.id_len = static_cast<uint16_t>(copy_len);
                }

                uint16_t name_len = 0;
                if (!read_devnode_string(node, kPropFriendlyName, entry.name, std::size(entry.name), &name_len) &&
                    !read_devnode_string(node, kPropDescription, entry.name, std::size(entry.name), &name_len))
                {
                    const size_t copy_len = std::min(inst_sv.size(), std::size(entry.name) - 1);
                    std::memcpy(entry.name, inst_sv.data(), copy_len * sizeof(wchar_t));
                    entry.name[copy_len] = L'\0';
                    name_len = static_cast<uint16_t>(copy_len);
                }
                entry.name_len = name_len;

                bool is_present = false;
                for (const wchar_t* pid : present_ids)
                {
                    if (equals_ordinal_icase(inst_sv, std::wstring_view(pid)))
                    {
                        is_present = true;
                        break;
                    }
                }

                ULONG dev_status = 0;
                ULONG dev_problem = 0;
                constexpr ULONG kDisconnectedOrProblem = 0x02000000u | 0x400u;
                const bool connected = is_present &&
                    ::CM_Get_DevNode_Status(&dev_status, &dev_problem, node, 0) == CR_SUCCESS &&
                    (dev_status & kDisconnectedOrProblem) == 0;
                entry.is_connected = connected ? 1 : 0;

                const std::wstring_view entry_id_sv(entry.id, entry.id_len);
                size_t existing_idx = collected.size();
                for (size_t k = 0; k < collected.size(); ++k)
                {
                    if (equals_ordinal_icase(std::wstring_view(collected[k].id, collected[k].id_len), entry_id_sv))
                    {
                        existing_idx = k;
                        break;
                    }
                }

                if (existing_idx == collected.size())
                {
                    collected.push_back(entry);
                }
                else if (collected[existing_idx].is_connected == 0 && connected)
                {
                    collected[existing_idx] = entry;
                }
            }
        }

        const int32_t total = static_cast<int32_t>(collected.size());
        *out_total_count = total;
        if (total == 0)
            return VN_OK;
        if (total > cap)
            return VN_ERR_BUFFER_TOO_SMALL;

        std::memcpy(out_devices, collected.data(), static_cast<size_t>(total) * sizeof(VnCoreBtBatteryDevice));
        return VN_OK;
    }
    catch (...)
    {
        *out_total_count = 0;
        return VN_ERR_FAIL;
    }
}
