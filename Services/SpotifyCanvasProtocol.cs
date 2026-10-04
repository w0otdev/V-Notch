using System.Text;

namespace VNotch.Services;

internal static class SpotifyCanvasProtocol
{
    internal static byte[] BuildCanvasRequest(string trackId)
    {
        ReadOnlySpan<byte> prefix = "spotify:track:"u8;
        int uriLength = checked(prefix.Length + Encoding.UTF8.GetByteCount(trackId));
        int trackLength = checked(1 + GetVarintLength((uint)uriLength) + uriLength);
        byte[] request = new byte[checked(1 + GetVarintLength((uint)trackLength) + trackLength)];
        int offset = 0;
        request[offset++] = 0x0A; // CanvasRequest.tracks, field 1, length-delimited.
        WriteVarint(request, ref offset, (uint)trackLength);
        request[offset++] = 0x0A; // Track.track_uri, field 1, length-delimited.
        WriteVarint(request, ref offset, (uint)uriLength);
        prefix.CopyTo(request.AsSpan(offset));
        offset += prefix.Length;
        Encoding.UTF8.GetBytes(trackId.AsSpan(), request.AsSpan(offset));
        return request;
    }

    private static int GetVarintLength(uint value)
    {
        int length = 1;
        while (value >= 0x80)
        {
            length++;
            value >>= 7;
        }
        return length;
    }

    private static void WriteVarint(Span<byte> destination, ref int offset, uint value)
    {
        while (value >= 0x80)
        {
            destination[offset++] = (byte)(value | 0x80);
            value >>= 7;
        }
        destination[offset++] = (byte)value;
    }

    internal static Uri? ParseCanvasResponse(ReadOnlySpan<byte> protobuf)
    {
        int offset = 0;
        while (offset < protobuf.Length)
        {
            if (!TryReadVarint(protobuf, ref offset, out ulong tag))
                return null;

            int fieldNumber = (int)(tag >> 3);
            int wireType = (int)(tag & 0x07);
            if (fieldNumber == 1 && wireType == 2)
            {
                if (!TryReadLengthDelimited(protobuf, ref offset, out var canvas))
                    return null;

                Uri? uri = ParseCanvasMessage(canvas);
                if (uri != null)
                    return uri;
            }
            else if (!TrySkipField(protobuf, ref offset, wireType))
            {
                return null;
            }
        }

        return null;
    }

    private static Uri? ParseCanvasMessage(ReadOnlySpan<byte> canvas)
    {
        int offset = 0;
        while (offset < canvas.Length)
        {
            if (!TryReadVarint(canvas, ref offset, out ulong tag))
                return null;

            int fieldNumber = (int)(tag >> 3);
            int wireType = (int)(tag & 0x07);
            if (fieldNumber == 2 && wireType == 2)
            {
                if (!TryReadLengthDelimited(canvas, ref offset, out var value))
                    return null;

                if (TryCreateCanvasUri(Encoding.UTF8.GetString(value), out var uri))
                    return uri;
            }
            else if (!TrySkipField(canvas, ref offset, wireType))
            {
                return null;
            }
        }

        return null;
    }

    private static bool TryReadLengthDelimited(
        ReadOnlySpan<byte> data,
        ref int offset,
        out ReadOnlySpan<byte> value)
    {
        value = default;
        if (!TryReadVarint(data, ref offset, out ulong length) || length > int.MaxValue)
            return false;

        int intLength = (int)length;
        if (offset < 0 || intLength < 0 || offset > data.Length - intLength)
            return false;

        value = data.Slice(offset, intLength);
        offset += intLength;
        return true;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int offset, out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 64 && offset < data.Length; shift += 7)
        {
            byte current = data[offset++];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
                return true;
        }
        return false;
    }

    private static bool TrySkipField(ReadOnlySpan<byte> data, ref int offset, int wireType)
    {
        switch (wireType)
        {
            case 0:
                return TryReadVarint(data, ref offset, out _);
            case 1:
                if (offset > data.Length - 8) return false;
                offset += 8;
                return true;
            case 2:
                return TryReadLengthDelimited(data, ref offset, out _);
            case 5:
                if (offset > data.Length - 4) return false;
                offset += 4;
                return true;
            default:
                return false;
        }
    }

    internal static bool TryCreateCanvasUri(string? value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) || candidate.Scheme != Uri.UriSchemeHttps)
            return false;

        bool trustedHost = candidate.Host.Equals("scdn.co", StringComparison.OrdinalIgnoreCase) ||
                           candidate.Host.EndsWith(".scdn.co", StringComparison.OrdinalIgnoreCase);
        bool isMp4 = candidate.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
        if (!trustedHost || !isMp4)
            return false;

        uri = candidate;
        return true;
    }

}
