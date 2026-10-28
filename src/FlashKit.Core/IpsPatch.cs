namespace FlashKit.Core;

/// <summary>Thrown when an IPS patch is malformed or cannot represent a diff.</summary>
public sealed class IpsFormatException : Exception
{
    public IpsFormatException(string message) : base(message) { }
}

/// <summary>
/// IPS (International Patching System) apply and create, operating purely on
/// byte arrays — front-ends wire the file I/O. Supports the RLE record form
/// and the Lunar IPS truncation extension.
///
/// Format: "PATCH", then records until an "EOF" (0x454F46) offset marker.
/// Each record is a 3-byte big-endian offset then a 2-byte big-endian size;
/// size &gt; 0 is followed by that many literal bytes, size == 0 is an RLE
/// record (2-byte run length + one value byte). An optional 3-byte length
/// after "EOF" truncates the output.
/// </summary>
public static class IpsPatch
{
    const int EofMarker = 0x454F46; // "EOF"
    const int MaxSize = 0x1000000;  // offsets are 3 bytes -> 16 MB ceiling
    const int MaxRecord = 0xFFFF;   // size/run fields are 2 bytes

    static readonly byte[] Magic = "PATCH"u8.ToArray();

    /// <summary>Applies <paramref name="patch"/> to <paramref name="rom"/>,
    /// returning the patched bytes (records past the end extend it; the
    /// truncation extension shrinks it).</summary>
    public static byte[] Apply(byte[] rom, byte[] patch)
    {
        if (patch.Length < 5 || !patch.AsSpan(0, 5).SequenceEqual(Magic))
            throw new IpsFormatException("not an IPS patch (missing PATCH header)");

        using var ms = new MemoryStream();
        ms.Write(rom, 0, rom.Length);

        int p = 5;
        while (true)
        {
            if (p + 3 > patch.Length) throw new IpsFormatException("truncated patch: expected an offset or EOF");
            int offset = (patch[p] << 16) | (patch[p + 1] << 8) | patch[p + 2];
            p += 3;

            if (offset == EofMarker)
            {
                if (p + 3 <= patch.Length) // optional truncation length
                {
                    long trunc = (patch[p] << 16) | (patch[p + 1] << 8) | patch[p + 2];
                    ms.SetLength(trunc);
                }
                break;
            }

            if (p + 2 > patch.Length) throw new IpsFormatException("truncated patch: expected a record size");
            int size = (patch[p] << 8) | patch[p + 1];
            p += 2;

            if (size == 0) // RLE
            {
                if (p + 3 > patch.Length) throw new IpsFormatException("truncated patch: expected an RLE run");
                int runLen = (patch[p] << 8) | patch[p + 1];
                byte value = patch[p + 2];
                p += 3;
                ms.Position = offset;
                for (int i = 0; i < runLen; i++) ms.WriteByte(value);
            }
            else
            {
                if (p + size > patch.Length) throw new IpsFormatException("truncated patch: expected record data");
                ms.Position = offset;
                ms.Write(patch, p, size);
                p += size;
            }
        }
        return ms.ToArray();
    }

    /// <summary>Builds an IPS patch turning <paramref name="original"/> into
    /// <paramref name="modified"/>. Round-trips: Apply(original, Create(a, b))
    /// == b. Uses RLE for long single-value runs (so an extended ROM's blank
    /// tail stays tiny) and the truncation extension when the target is
    /// shorter.</summary>
    public static byte[] Create(byte[] original, byte[] modified)
    {
        if (modified.Length > MaxSize)
            throw new IpsFormatException("target too large for IPS (over 16 MB)");

        using var ms = new MemoryStream();
        ms.Write(Magic, 0, Magic.Length);

        int i = 0, n = modified.Length;
        while (i < n)
        {
            if (!Differs(original, modified, i)) { i++; continue; }
            int start = i;
            while (i < n && Differs(original, modified, i)) i++;
            EmitRange(ms, modified, start, i);
        }

        WriteOffset(ms, EofMarker);
        if (modified.Length < original.Length)
            Write3(ms, modified.Length); // Lunar IPS truncation extension
        return ms.ToArray();
    }

    static bool Differs(byte[] original, byte[] modified, int i) =>
        i >= original.Length || original[i] != modified[i];

    /// <summary>Emits records covering data[start, end): RLE for repeated-value
    /// runs of 4+ (RLE's fixed 8 bytes beats a literal's 5+len there), literal
    /// records otherwise, each split to the 0xFFFF field limit.</summary>
    static void EmitRange(MemoryStream ms, byte[] data, int start, int end)
    {
        int i = start;
        while (i < end)
        {
            int run = 1;
            while (i + run < end && data[i + run] == data[i]) run++;

            if (run >= 4)
            {
                for (int off = i, left = run; left > 0;)
                {
                    int chunk = Math.Min(left, MaxRecord);
                    EmitRle(ms, off, chunk, data[i]);
                    off += chunk;
                    left -= chunk;
                }
                i += run;
            }
            else
            {
                int litStart = i;
                while (i < end && i - litStart < MaxRecord)
                {
                    int r = 1;
                    while (i + r < end && data[i + r] == data[i]) r++;
                    if (r >= 4) break; // leave the long run for the RLE branch
                    i++;
                }
                EmitLiteral(ms, data, litStart, i - litStart);
            }
        }
    }

    static void EmitLiteral(MemoryStream ms, byte[] data, int offset, int len)
    {
        GuardOffset(offset);
        WriteOffset(ms, offset);
        ms.WriteByte((byte)(len >> 8));
        ms.WriteByte((byte)len);
        ms.Write(data, offset, len);
    }

    static void EmitRle(MemoryStream ms, int offset, int len, byte value)
    {
        GuardOffset(offset);
        WriteOffset(ms, offset);
        ms.WriteByte(0); // size 0 -> RLE
        ms.WriteByte(0);
        ms.WriteByte((byte)(len >> 8));
        ms.WriteByte((byte)len);
        ms.WriteByte(value);
    }

    // A record offset of 0x454F46 would be read back as the EOF marker.
    // Only reachable for targets over 4 MB (flat 8 MiB carts such as the
    // MegaWiFi); this is a guard rather than the (byte-shifting) workaround,
    // since xdelta already covers large targets.
    static void GuardOffset(int offset)
    {
        if (offset == EofMarker)
            throw new IpsFormatException("change at the reserved EOF offset 0x454F46 cannot be encoded");
    }

    static void WriteOffset(MemoryStream ms, int offset) => Write3(ms, offset);

    static void Write3(MemoryStream ms, int value)
    {
        ms.WriteByte((byte)(value >> 16));
        ms.WriteByte((byte)(value >> 8));
        ms.WriteByte((byte)value);
    }
}
