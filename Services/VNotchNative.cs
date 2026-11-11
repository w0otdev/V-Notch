using System;
using System.Runtime.InteropServices;

namespace VNotch.Services;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VnMarkdownSlice
{
    public int Start;
    public int Len;
}

public static partial class VNotchNative
{
    private const string DllName = "VNotch.Native.dll";

    public const int VN_OK = 0;
    public const int VN_ERR_INVALID_ARG = -1;
    public const int VN_ERR_BUFFER_TOO_SMALL = -2;
    public const int VN_ERR_FAIL = -3;

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int vn_settings_search_normalize(
        ReadOnlySpan<char> inputPtr,
        int inputLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int vn_settings_search_score_row(
        ReadOnlySpan<char> normTitlePtr,
        int normTitleLen,
        ReadOnlySpan<char> normContentPtr,
        int normContentLen,
        ReadOnlySpan<char> normCombinedPtr,
        int normCombinedLen,
        ReadOnlySpan<char> normQueryPtr,
        int normQueryLen,
        byte allowFuzzy,
        out int outScore);

    [LibraryImport(DllName)]
    internal static partial int vn_nav_tabs_calculate_target_slot_hysteresis(
        int current_target,
        double visual_pos,
        double pitch,
        int total_slots,
        out int out_slot);

    [LibraryImport(DllName)]
    internal static partial int vn_nav_tabs_calculate_neighbor_offset(
        int index,
        int initial_slot,
        int target_slot,
        double pitch,
        out double out_offset);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int vn_media_detection_build_session_instance_key(
        nint unkPtr,
        ReadOnlySpan<char> sourceApp,
        int sourceAppLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);
}
