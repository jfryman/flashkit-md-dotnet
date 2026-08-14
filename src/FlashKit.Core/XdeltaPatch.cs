namespace FlashKit.Core;

/// <summary>Thrown when an xdelta/VCDIFF patch is malformed, uses an
/// unsupported feature, or fails its checksum.</summary>
public sealed class XdeltaFormatException : Exception
{
    public XdeltaFormatException(string message) : base(message) { }
}

/// <summary>
/// xdelta (VCDIFF, RFC 3284) apply and create, operating purely on byte
/// arrays — front-ends wire the file I/O, mirroring <see cref="IpsPatch"/>.
///
/// Apply decodes the standard format as xdelta3 produces it with default
/// settings: the RFC 3284 default code table, plus the two xdelta3 header
/// extensions — application headers (skipped) and per-window Adler-32
/// checksums (verified). Secondary compression (xdelta3 -S djw/lzma) and
/// application-defined code tables are rejected with a pointed message.
///
/// Create emits a conservative subset any VCDIFF decoder (xdelta3 included)
/// accepts: one window per 4 MB of target with the whole source as its copy
/// segment, plain ADD/RUN/COPY-mode-SELF opcodes, and an Adler-32 checksum
/// per window. Matching probes the same offset first (unchanged regions
/// dominate ROM hacks) and falls back to a 16-byte block index of the
/// source for relocated content.
/// </summary>
public static class XdeltaPatch
{
    // Hdr_Indicator bits.
    const int VcdDecompress = 0x01;
    const int VcdCodetable = 0x02;
    const int VcdAppHeader = 0x04;   // xdelta3 extension

    // Win_Indicator bits.
    const int VcdSource = 0x01;
    const int VcdTarget = 0x02;
    const int VcdAdler32 = 0x04;     // xdelta3/open-vcdiff extension

    const int MaxTarget = 1 << 30;   // sanity cap on decoded output
    const int NearSlots = 4;         // default code table s_near
    const int SameSlots = 3;         // default code table s_same

    const int WindowSize = 1 << 22;  // 4 MB of target per encoded window
    const int BlockSize = 16;        // source indexing granularity
    const int MinMatch = 8;          // shortest COPY/RUN worth its encoding

    // Instruction kinds (RFC 3284 section 5.4).
    const byte InstNoop = 0;
    const byte InstAdd = 1;
    const byte InstRun = 2;
    const byte InstCopy = 3;

    readonly record struct Op(byte Kind, byte Size, byte Mode);
    readonly record struct TableEntry(Op First, Op Second);

    static readonly TableEntry[] CodeTable = BuildDefaultCodeTable();

    /// <summary>Applies <paramref name="patch"/> to <paramref name="rom"/>,
    /// returning the patched bytes. The target size is whatever the patch
    /// declares; when the patch carries Adler-32 checksums (xdelta3 writes
    /// them by default) the output is verified against them.</summary>
    public static byte[] Apply(byte[] rom, byte[] patch)
    {
        var r = new Reader(patch, 0, patch.Length);
        if (patch.Length < 4 || r.ReadByte() != 0xD6 || r.ReadByte() != 0xC3 || r.ReadByte() != 0xC4)
            throw new XdeltaFormatException("not an xdelta patch (missing VCDIFF magic)");
        int version = r.ReadByte();
        if (version != 0)
            throw new XdeltaFormatException($"unsupported VCDIFF version 0x{version:X2}");

        int hdr = r.ReadByte();
        if ((hdr & VcdDecompress) != 0)
            throw new XdeltaFormatException("secondary compression is not supported (recreate the patch with xdelta3 -S none)");
        if ((hdr & VcdCodetable) != 0)
            throw new XdeltaFormatException("application-defined code tables are not supported");
        if ((hdr & ~(VcdDecompress | VcdCodetable | VcdAppHeader)) != 0)
            throw new XdeltaFormatException("unsupported header flags");
        if ((hdr & VcdAppHeader) != 0)
            r.Skip(r.ReadVarint()); // xdelta3 stores file names here; irrelevant to the bytes

        using var target = new MemoryStream();
        while (r.Remaining > 0)
            DecodeWindow(rom, target, r);
        return target.ToArray();
    }

    static void DecodeWindow(byte[] rom, MemoryStream target, Reader r)
    {
        int winInd = r.ReadByte();
        if ((winInd & VcdSource) != 0 && (winInd & VcdTarget) != 0)
            throw new XdeltaFormatException("window names both a source and a target segment");
        if ((winInd & ~(VcdSource | VcdTarget | VcdAdler32)) != 0)
            throw new XdeltaFormatException("unsupported window flags");

        // The copy segment: a slice of the ROM (VCD_SOURCE) or of the target
        // decoded so far (VCD_TARGET — legal, but xdelta3 never emits it).
        byte[] segFrom = (winInd & VcdTarget) != 0 ? target.ToArray() : rom;
        int segLen = 0, segPos = 0;
        if ((winInd & (VcdSource | VcdTarget)) != 0)
        {
            segLen = r.ReadVarint();
            segPos = r.ReadVarint();
            if ((long)segPos + segLen > segFrom.Length)
                throw new XdeltaFormatException((winInd & VcdSource) != 0
                    ? "window segment lies outside the ROM (patch made for a different ROM?)"
                    : "window segment lies outside the decoded target");
        }

        r.ReadVarint(); // delta encoding length; the section lengths below bound every read
        int winLen = r.ReadVarint();
        if (target.Length + winLen > MaxTarget)
            throw new XdeltaFormatException("patch output exceeds the 1 GB sanity cap");
        if (r.ReadByte() != 0)
            throw new XdeltaFormatException("secondary compression is not supported (recreate the patch with xdelta3 -S none)");

        int dataLen = r.ReadVarint();
        int instLen = r.ReadVarint();
        int addrLen = r.ReadVarint();
        uint checksum = (winInd & VcdAdler32) != 0 ? r.ReadUInt32() : 0;
        var data = r.ReadSection(dataLen);
        var inst = r.ReadSection(instLen);
        var addr = r.ReadSection(addrLen);

        var output = new byte[winLen];
        int outPos = 0;
        var near = new int[NearSlots];
        var same = new int[SameSlots * 256];
        int nextNear = 0;

        while (inst.Remaining > 0)
        {
            var entry = CodeTable[inst.ReadByte()];
            Execute(entry.First);
            if (entry.Second.Kind != InstNoop) Execute(entry.Second);
        }

        if (outPos != winLen)
            throw new XdeltaFormatException("window ended short of its declared length");
        if (data.Remaining != 0 || addr.Remaining != 0)
            throw new XdeltaFormatException("window has unconsumed section bytes");
        if ((winInd & VcdAdler32) != 0 && Adler32(output, 0, output.Length) != checksum)
            throw new XdeltaFormatException("Adler-32 mismatch (patch made for a different ROM, or corrupt)");
        target.Write(output, 0, output.Length);

        void Execute(Op op)
        {
            int size = op.Size != 0 ? op.Size : inst.ReadVarint();
            if (outPos + size > winLen)
                throw new XdeltaFormatException("window output overruns its declared length");
            switch (op.Kind)
            {
                case InstRun:
                    byte value = data.ReadByte();
                    for (int k = 0; k < size; k++) output[outPos++] = value;
                    break;
                case InstAdd:
                    data.ReadInto(output, outPos, size);
                    outPos += size;
                    break;
                default: // InstCopy; may overlap forward into itself, so copy byte by byte
                    int a = DecodeAddress(op.Mode);
                    for (int k = 0; k < size; k++, a++)
                        output[outPos++] = a < segLen ? segFrom[segPos + a] : output[a - segLen];
                    break;
            }
        }

        // RFC 3284 section 3: modes are SELF, HERE, s_near near slots, then
        // s_same same slots; every decoded address updates both caches.
        int DecodeAddress(int mode)
        {
            int here = segLen + outPos;
            int a = mode switch
            {
                0 => addr.ReadVarint(),
                1 => here - addr.ReadVarint(),
                < 2 + NearSlots => near[mode - 2] + addr.ReadVarint(),
                _ => same[(mode - 2 - NearSlots) * 256 + addr.ReadByte()],
            };
            if (a < 0 || a >= here)
                throw new XdeltaFormatException("copy address outside the decoded data");
            near[nextNear] = a;
            nextNear = (nextNear + 1) % NearSlots;
            same[a % (SameSlots * 256)] = a;
            return a;
        }
    }

    /// <summary>Builds an xdelta patch turning <paramref name="original"/>
    /// into <paramref name="modified"/>. Round-trips: Apply(original,
    /// Create(a, b)) == b, and xdelta3 decodes the output.</summary>
    public static byte[] Create(byte[] original, byte[] modified)
    {
        using var patch = new MemoryStream();
        patch.Write([0xD6, 0xC3, 0xC4, 0x00, 0x00]); // magic, version 0, no header extensions
        var index = BuildSourceIndex(original);
        for (int off = 0; off < modified.Length; off += WindowSize)
            EncodeWindow(patch, original, index, modified, off, Math.Min(WindowSize, modified.Length - off));
        return patch.ToArray();
    }

    /// <summary>Hash of every aligned 16-byte source block to its offset
    /// (first occurrence wins) — enough to find relocated content; the
    /// same-offset probe covers the dominant in-place-edit case.</summary>
    static Dictionary<ulong, int> BuildSourceIndex(byte[] source)
    {
        var map = new Dictionary<ulong, int>(source.Length / BlockSize + 1);
        for (int off = 0; off + BlockSize <= source.Length; off += BlockSize)
            map.TryAdd(BlockHash(source, off), off);
        return map;
    }

    static ulong BlockHash(byte[] buff, int off)
    {
        ulong h = 14695981039346656037; // FNV-1a
        for (int i = 0; i < BlockSize; i++) h = (h ^ buff[off + i]) * 1099511628211;
        return h;
    }

    static void EncodeWindow(MemoryStream patch, byte[] source, Dictionary<ulong, int> index,
        byte[] target, int start, int len)
    {
        using var data = new MemoryStream();
        using var inst = new MemoryStream();
        using var addrs = new MemoryStream();

        int end = start + len;
        int addFrom = start; // pending literal run [addFrom, t)
        for (int t = start; t < end;)
        {
            (int matchLen, int matchAddr) = FindCopy(source, index, target, t, end);
            int runLen = RunLength(target, t, end);
            if (matchLen < MinMatch && runLen < MinMatch) { t++; continue; }

            FlushAdd(data, inst, target, addFrom, t);
            if (runLen >= matchLen)
            {
                inst.WriteByte(0); // RUN, explicit size
                WriteVarint(inst, runLen);
                data.WriteByte(target[t]);
                t += runLen;
            }
            else
            {
                EmitCopy(inst, addrs, matchLen, matchAddr);
                t += matchLen;
            }
            addFrom = t;
        }
        FlushAdd(data, inst, target, addFrom, end);

        bool hasSource = source.Length > 0;
        patch.WriteByte((byte)((hasSource ? VcdSource : 0) | VcdAdler32));
        if (hasSource)
        {
            WriteVarint(patch, source.Length); // the segment is the whole source
            WriteVarint(patch, 0);
        }
        long encLen = VarintLength(len) + 1
            + VarintLength((int)data.Length) + VarintLength((int)inst.Length) + VarintLength((int)addrs.Length)
            + 4 + data.Length + inst.Length + addrs.Length;
        WriteVarint(patch, checked((int)encLen));
        WriteVarint(patch, len);
        patch.WriteByte(0); // Delta_Indicator: no secondary compression
        WriteVarint(patch, (int)data.Length);
        WriteVarint(patch, (int)inst.Length);
        WriteVarint(patch, (int)addrs.Length);
        uint checksum = Adler32(target, start, len);
        patch.Write([(byte)(checksum >> 24), (byte)(checksum >> 16), (byte)(checksum >> 8), (byte)checksum]);
        data.WriteTo(patch);
        inst.WriteTo(patch);
        addrs.WriteTo(patch);
    }

    /// <summary>Best source match at <paramref name="t"/>: the same offset
    /// first, then the block index. Returns length 0 when nothing bites.</summary>
    static (int Len, int Addr) FindCopy(byte[] source, Dictionary<ulong, int> index,
        byte[] target, int t, int end)
    {
        int bestLen = 0, bestAddr = 0;
        if (t < source.Length && source[t] == target[t])
        {
            bestLen = MatchLength(source, t, target, t, end);
            bestAddr = t;
        }
        if (t + BlockSize <= end && index.TryGetValue(BlockHash(target, t), out int off))
        {
            int len = MatchLength(source, off, target, t, end); // re-verifies, so a hash collision just scores 0
            if (len > bestLen) { bestLen = len; bestAddr = off; }
        }
        return (bestLen, bestAddr);
    }

    static int MatchLength(byte[] source, int s, byte[] target, int t, int end)
    {
        int len = 0;
        while (t + len < end && s + len < source.Length && source[s + len] == target[t + len]) len++;
        return len;
    }

    static int RunLength(byte[] target, int t, int end)
    {
        int len = 1;
        while (t + len < end && target[t + len] == target[t]) len++;
        return len;
    }

    static void FlushAdd(MemoryStream data, MemoryStream inst, byte[] target, int from, int to)
    {
        if (to == from) return;
        int len = to - from;
        if (len <= 17)
        {
            inst.WriteByte((byte)(1 + len)); // ADD with implicit size 1..17
        }
        else
        {
            inst.WriteByte(1); // ADD, explicit size
            WriteVarint(inst, len);
        }
        data.Write(target, from, len);
    }

    static void EmitCopy(MemoryStream inst, MemoryStream addrs, int len, int addr)
    {
        if (len is >= 4 and <= 18)
        {
            inst.WriteByte((byte)(19 + len - 3)); // COPY mode SELF with implicit size 4..18
        }
        else
        {
            inst.WriteByte(19); // COPY mode SELF, explicit size
            WriteVarint(inst, len);
        }
        WriteVarint(addrs, addr);
    }

    // Varints are big-endian base 128, continuation bit on all but the last
    // byte (RFC 3284 section 2); all values here are non-negative ints.
    static void WriteVarint(MemoryStream ms, int value)
    {
        Span<byte> tmp = stackalloc byte[5];
        int n = tmp.Length;
        tmp[--n] = (byte)(value & 0x7F);
        for (uint v = (uint)value >> 7; v != 0; v >>= 7)
            tmp[--n] = (byte)(v & 0x7F | 0x80);
        ms.Write(tmp[n..]);
    }

    static int VarintLength(int value) =>
        value < 1 << 7 ? 1 : value < 1 << 14 ? 2 : value < 1 << 21 ? 3 : value < 1 << 28 ? 4 : 5;

    static uint Adler32(byte[] buff, int offset, int count)
    {
        const uint Mod = 65521;
        uint a = 1, b = 0;
        int i = offset, end = offset + count;
        while (i < end)
        {
            // 5552 is the most bytes accumulable before a and b can overflow.
            for (int stop = Math.Min(end, i + 5552); i < stop; i++) { a += buff[i]; b += a; }
            a %= Mod;
            b %= Mod;
        }
        return b << 16 | a;
    }

    // RFC 3284 section 5.6, the default code table: RUN, 18 ADDs, 9x16
    // COPYs (one block per mode), then the paired ADD+COPY / COPY+ADD forms.
    static TableEntry[] BuildDefaultCodeTable()
    {
        var table = new TableEntry[256];
        var noop = new Op(InstNoop, 0, 0);
        int i = 0;
        table[i++] = new(new Op(InstRun, 0, 0), noop);
        for (int size = 0; size <= 17; size++)
            table[i++] = new(new Op(InstAdd, (byte)size, 0), noop);
        for (byte mode = 0; mode <= 8; mode++)
        {
            table[i++] = new(new Op(InstCopy, 0, mode), noop);
            for (int size = 4; size <= 18; size++)
                table[i++] = new(new Op(InstCopy, (byte)size, mode), noop);
        }
        for (byte mode = 0; mode <= 5; mode++)
            for (byte add = 1; add <= 4; add++)
                for (byte copy = 4; copy <= 6; copy++)
                    table[i++] = new(new Op(InstAdd, add, 0), new Op(InstCopy, copy, mode));
        for (byte mode = 6; mode <= 8; mode++)
            for (byte add = 1; add <= 4; add++)
                table[i++] = new(new Op(InstAdd, add, 0), new Op(InstCopy, 4, mode));
        for (byte mode = 0; mode <= 8; mode++)
            table[i++] = new(new Op(InstCopy, 4, mode), new Op(InstAdd, 1, 0));
        return table;
    }

    /// <summary>Bounded cursor over a byte range; every read throws
    /// <see cref="XdeltaFormatException"/> instead of running past the end.</summary>
    sealed class Reader
    {
        readonly byte[] buff;
        readonly int end;
        int pos;

        public Reader(byte[] buff, int start, int end)
        {
            this.buff = buff;
            pos = start;
            this.end = end;
        }

        public int Remaining => end - pos;

        public byte ReadByte()
        {
            if (pos >= end) throw new XdeltaFormatException("truncated patch");
            return buff[pos++];
        }

        public int ReadVarint()
        {
            long value = 0;
            while (true)
            {
                byte b = ReadByte();
                value = value << 7 | (uint)(b & 0x7F);
                if (value > int.MaxValue) throw new XdeltaFormatException("varint out of range");
                if ((b & 0x80) == 0) return (int)value;
            }
        }

        public uint ReadUInt32() =>
            (uint)(ReadByte() << 24 | ReadByte() << 16 | ReadByte() << 8 | ReadByte());

        public Reader ReadSection(int length)
        {
            if (length > Remaining) throw new XdeltaFormatException("truncated patch");
            var section = new Reader(buff, pos, pos + length);
            pos += length;
            return section;
        }

        public void ReadInto(byte[] dest, int offset, int count)
        {
            if (count > Remaining) throw new XdeltaFormatException("truncated patch");
            Array.Copy(buff, pos, dest, offset, count);
            pos += count;
        }

        public void Skip(int count)
        {
            if (count > Remaining) throw new XdeltaFormatException("truncated patch");
            pos += count;
        }
    }
}
