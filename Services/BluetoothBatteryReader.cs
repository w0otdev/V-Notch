using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using VNotch.Models;

namespace VNotch.Services;

internal static partial class BluetoothBatteryReader
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct VnCoreBtBatteryDevice
    {
        public int Percentage;
        public byte IsConnected;
        public byte Reserved;
        public ushort IdLen;
        public ushort NameLen;
        public fixed ushort Id[260];
        public fixed ushort Name[512];
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_bluetooth_battery_parse_percentage(
        ReadOnlySpan<byte> buf,
        int len,
        out int outPercentage);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_bluetooth_battery_read_devices(
        VnCoreBtBatteryDevice* outDevices,
        int cap,
        out int outTotalCount);

    private static readonly Comparison<BatteryDevice> s_deviceSortComparison = static (a, b) =>
    {
        int cmp = StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
        return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.Id, b.Id);
    };

    internal static unsafe IReadOnlyList<BatteryDevice> ReadDevices()
    {
        var buffer = new VnCoreBtBatteryDevice[16];
        int count;
        fixed (VnCoreBtBatteryDevice* pInitial = buffer)
        {
            int rc = vn_bluetooth_battery_read_devices(pInitial, buffer.Length, out count);
            if (rc == -2 && count > buffer.Length)
            {
                buffer = new VnCoreBtBatteryDevice[count];
                fixed (VnCoreBtBatteryDevice* pRetry = buffer)
                {
                    rc = vn_bluetooth_battery_read_devices(pRetry, buffer.Length, out count);
                }
            }

            if (rc != 0 || count <= 0)
                return Array.Empty<BatteryDevice>();
        }

        int finalCount = Math.Min(count, buffer.Length);
        var result = new BatteryDevice[finalCount];
        fixed (VnCoreBtBatteryDevice* pBuf = buffer)
        {
            for (int i = 0; i < finalCount; i++)
            {
                VnCoreBtBatteryDevice* item = pBuf + i;
                string id = item->IdLen > 0 ? new string((char*)item->Id, 0, item->IdLen) : string.Empty;
                string name = item->NameLen > 0 ? new string((char*)item->Name, 0, item->NameLen) : id;
                result[i] = new BatteryDevice(id, name, item->Percentage, item->IsConnected != 0);
            }
        }

        Array.Sort(result, s_deviceSortComparison);
        return result;
    }

    internal static int ParsePercentage(ReadOnlySpan<byte> value) =>
        vn_bluetooth_battery_parse_percentage(value, value.Length, out int percentage) == 0 ? percentage : -1;
}
