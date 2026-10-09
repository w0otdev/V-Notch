using System.IO;

namespace VNotch.Services.Translation;

// Exact affine change for the pinned dynamic-quantized MADLAD graphs. U8S8
// saturates on AVX2/non-VNNI; changing both q and zero-point by +128 uses U8U8.
// Scan protobuf containers and stream tensor bytes; never hold a GB model in RAM.
internal static class TranslationUnsignedWeights
{
    private sealed record Patch(long Offset, long Length, byte[]? Bytes);
    private enum Container { Model, Graph, Node, Attribute, Tensor }

    internal static async Task ConvertAsync(string source, string destination, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        var patches = new List<Patch>();
        Scan(input, input.Length, Container.Model, patches, token);
        patches.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        input.Position = 0;
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
        byte[] buffer = new byte[131072];
        foreach (var patch in patches)
        {
            await CopyAsync(patch.Offset - input.Position, false);
            if (patch.Bytes != null)
            {
                input.Position += patch.Length;
                await output.WriteAsync(patch.Bytes, token).ConfigureAwait(false);
            }
            else await CopyAsync(patch.Length, true);
        }
        await CopyAsync(input.Length - input.Position, false);

        async Task CopyAsync(long remaining, bool shift)
        {
            if (remaining < 0) throw new TranslationException("translation.modelInvalid");
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                int count = (int)Math.Min(remaining, buffer.Length);
                await input.ReadExactlyAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                if (shift) for (int i = 0; i < count; i++) buffer[i] ^= 128;
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                remaining -= count;
            }
        }
    }

    private static long Scan(Stream stream, long end, Container container, List<Patch> patches, CancellationToken token)
    {
        long delta = 0, typeOffset = 0, typeLength = 0;
        ulong type = 0;
        var tensorData = new List<Patch>();
        while (stream.Position < end)
        {
            token.ThrowIfCancellationRequested();
            ulong tag = ReadVarint(stream);
            int field = (int)(tag >> 3), wire = (int)(tag & 7);
            if (field == 0) throw new TranslationException("translation.modelInvalid");
            if (wire == 0)
            {
                long offset = stream.Position;
                ulong value = ReadVarint(stream);
                if (container == Container.Tensor && field == 2) { type = value; typeOffset = offset; typeLength = stream.Position - offset; }
                else if (container == Container.Tensor && field == 5)
                    tensorData.Add(new(offset, stream.Position - offset, EncodeVarint(Shift(value))));
            }
            else if (wire == 2)
            {
                long lengthOffset = stream.Position;
                long length = checked((long)ReadVarint(stream));
                long prefixLength = stream.Position - lengthOffset, payload = stream.Position, next = checked(payload + length);
                if (next > end) throw new TranslationException("translation.modelInvalid");
                Container? child = (container, field) switch
                {
                    (Container.Model, 7) => Container.Graph,
                    (Container.Graph, 1) => Container.Node,
                    (Container.Graph, 5) => Container.Tensor,
                    (Container.Node, 5) => Container.Attribute,
                    (Container.Attribute, 6 or 11) => Container.Graph,
                    _ => null
                };
                long childDelta = 0;
                if (child != null) childDelta = Scan(stream, next, child.Value, patches, token);
                else if (container == Container.Tensor && field == 9) tensorData.Add(new(payload, length, null));
                else if (container == Container.Tensor && field == 5)
                {
                    // Packed int32_data (zero-points in these graphs).
                    using var converted = new MemoryStream();
                    while (stream.Position < next) converted.Write(EncodeVarint(Shift(ReadVarint(stream))));
                    tensorData.Add(new(payload, length, converted.ToArray()));
                    // Its prefix is updated only if the tensor turns out to be INT8.
                    tensorData.Add(new(lengthOffset, prefixLength, EncodeVarint((ulong)converted.Length)));
                }
                if (childDelta != 0)
                {
                    byte[] newLength = EncodeVarint(checked((ulong)(length + childDelta)));
                    patches.Add(new(lengthOffset, prefixLength, newLength));
                    delta += childDelta + newLength.Length - prefixLength;
                }
                stream.Position = next;
            }
            else if (wire is 1 or 5) stream.Position += wire == 1 ? 8 : 4;
            else throw new TranslationException("translation.modelInvalid");
            if (stream.Position > end) throw new TranslationException("translation.modelInvalid");
        }
        if (container == Container.Tensor && type == 3)
        {
            patches.Add(new(typeOffset, typeLength, EncodeVarint(2)));
            foreach (var patch in tensorData)
            {
                patches.Add(patch);
                if (patch.Bytes != null) delta += patch.Bytes.Length - patch.Length;
            }
        }
        return delta;
    }

    private static ulong Shift(ulong signed) => unchecked((byte)(unchecked((int)signed) + 128));
    private static ulong ReadVarint(Stream stream)
    {
        ulong value = 0;
        for (int shift = 0; shift < 70; shift += 7)
        {
            int next = stream.ReadByte();
            if (next < 0 || (shift == 63 && next > 1)) throw new TranslationException("translation.modelInvalid");
            value |= (ulong)(next & 127) << shift;
            if ((next & 128) == 0) return value;
        }
        throw new TranslationException("translation.modelInvalid");
    }
    private static byte[] EncodeVarint(ulong value)
    {
        var bytes = new List<byte>(10);
        do { byte next = (byte)(value & 127); value >>= 7; bytes.Add((byte)(next | (value != 0 ? 128 : 0))); } while (value != 0);
        return bytes.ToArray();
    }
}
