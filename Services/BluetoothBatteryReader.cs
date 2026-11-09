using System.Runtime.InteropServices;
using System.Text;
using VNotch.Models;

namespace VNotch.Services;

internal static class BluetoothBatteryReader
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid format, uint id)
    {
        public readonly Guid Format = format;
        public readonly uint Id = id;
    }

    private static readonly PropertyKey BatteryLevel = new(new("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
    private static readonly PropertyKey BatteryLife = new(new("49CD1F76-5626-4B17-A4E8-18B4AA1A2213"), 10);
    private static readonly PropertyKey FriendlyName = new(new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    private static readonly PropertyKey Description = new(new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 2);
    private static readonly PropertyKey ContainerId = new(new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
    private const uint EnumeratorFilter = 1;
    private const uint PresentFilter = 0x100;
    private const uint DeviceDisconnected = 0x02000000;
    private const uint DeviceHasProblem = 0x400;

    internal static IReadOnlyList<BatteryDevice> ReadDevices()
    {
        var devices = new List<BatteryDevice>();
        foreach (string enumerator in new[] { "BTHENUM", "BTHLE", "BTHLEDEVICE" })
        {
            var present = EnumerateIds(enumerator, presentOnly: true).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string instanceId in EnumerateIds(enumerator, presentOnly: false))
            {
                if (CM_Locate_DevNodeW(out uint node, instanceId, 1 /* CM_LOCATE_DEVNODE_PHANTOM */) != 0) continue;
                int percentage = ReadPercentage(node, BatteryLevel);
                if (percentage < 0) percentage = ReadPercentage(node, BatteryLife);
                if (percentage < 0) continue;
                var container = ReadProperty(node, ContainerId, 13 /* DEVPROP_TYPE_GUID */);
                Guid containerId = container is { Length: 16 } ? new Guid(container) : Guid.Empty;
                string id = containerId != Guid.Empty ? $"bluetooth:{containerId:D}" : instanceId;
                string name = ReadName(node, FriendlyName) ?? ReadName(node, Description) ?? instanceId;
                bool connected = present.Contains(instanceId)
                    && CM_Get_DevNode_Status(out uint status, out _, node, 0) == 0
                    && (status & (DeviceDisconnected | DeviceHasProblem)) == 0;
                devices.Add(new(id, name, percentage, connected));
            }
        }
        // A dual-mode device can expose several service nodes. Prefer a live node.
        return devices.GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(device => device.IsConnected).First())
            .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(device => device.Id, StringComparer.Ordinal).ToArray();
    }

    private static string[] EnumerateIds(string enumerator, bool presentOnly)
    {
        uint flags = EnumeratorFilter | (presentOnly ? PresentFilter : 0);
        // The list can grow between the size query and the read when a device arrives.
        for (int attempt = 0; attempt < 3; ++attempt)
        {
            if (CM_Get_Device_ID_List_SizeW(out uint size, enumerator, flags) != 0 || size is < 2 or > 1_048_576)
                return Array.Empty<string>();
            var buffer = new char[size];
            uint result = CM_Get_Device_ID_ListW(enumerator, buffer, size, flags);
            if (result == 0) return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (result != 0x1A /* CR_BUFFER_SMALL */) break;
        }
        return Array.Empty<string>();
    }

    private static int ReadPercentage(uint node, PropertyKey key)
    {
        var value = ReadProperty(node, key, 3 /* DEVPROP_TYPE_BYTE */);
        return ParsePercentage(value);
    }

    internal static int ParsePercentage(ReadOnlySpan<byte> value) =>
        value.Length == 1 && value[0] <= 100 ? value[0] : -1;

    private static string? ReadName(uint node, PropertyKey key)
    {
        var value = ReadProperty(node, key, 18 /* DEVPROP_TYPE_STRING */);
        if (value == null || (value.Length & 1) != 0) return null;
        string name = Encoding.Unicode.GetString(value).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static byte[]? ReadProperty(uint node, PropertyKey key, uint expectedType)
    {
        var buffer = new byte[4096];
        uint length = (uint)buffer.Length;
        if (CM_Get_DevNode_PropertyW(node, in key, out uint type, buffer, ref length, 0) != 0
            || type != expectedType || length > buffer.Length) return null;
        return buffer.AsSpan(0, (int)length).ToArray();
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint size, string filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Locate_DevNodeW(out uint node, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_PropertyW(uint node, in PropertyKey key, out uint type,
        [Out] byte[] buffer, ref uint size, uint flags);
}
