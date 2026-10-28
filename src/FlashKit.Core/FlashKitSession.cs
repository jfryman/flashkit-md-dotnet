namespace FlashKit.Core;

public enum OperationPhase { Read, Erase, Write, Verify }

/// <summary>Progress report for long-running cart operations. Each phase
/// starts with a Done=0 report so front-ends can render phase transitions.</summary>
public readonly record struct OperationProgress(OperationPhase Phase, long Done, long Total);

/// <summary>Read-back verification mismatch at <see cref="Offset"/>.</summary>
public sealed class VerifyException : Exception
{
    public long Offset { get; }

    public VerifyException(long offset) : base("Verify error at " + offset)
    {
        Offset = offset;
    }
}

/// <summary>The cart reports no save RAM, so there is nothing to read or
/// write at the save window.</summary>
public sealed class SaveRamNotFoundException : Exception
{
    public SaveRamNotFoundException() : base("RAM is not detected")
    {
    }
}

/// <summary>No CFI-capable flash chip answered on the cart bus — the cart
/// is not flashable (e.g. a mask ROM game cart) or is not seated.</summary>
public sealed class FlashChipNotFoundException : Exception
{
    public FlashChipNotFoundException(byte[] cfiResponse)
        : base("No flash chip detected: CFI query returned "
               + BitConverter.ToString(cfiResponse)
               + " instead of QRY. Not a flash cart, or the cart is unseated.")
    {
    }
}

/// <summary><paramref name="HeaderRomBytes"/> is the ROM size the cart's
/// header declares (end address at 0x1A4 plus one), or null when the header
/// holds no plausible size — e.g. blank or partially programmed flash.
/// <paramref name="RomBytes"/> is the mirror-probed size. <paramref name="Is32X"/>
/// is true when the cart is a Sega 32X title (see <see cref="FlashKitSession"/>).
/// <paramref name="HeaderValid"/> is true when "SEGA" appears in the system
/// field at 0x100; <paramref name="HeaderBlank"/> when the first 512 bytes all
/// read 0xFF. <paramref name="Flash"/> is the flash identification, probed
/// only when the header is invalid (or when asked for).</summary>
public sealed record CartInfo(string RomName, int RomBytes, int RamBytes, int? HeaderRomBytes = null,
    bool Is32X = false, string Region = "Unknown", bool HeaderValid = true, bool HeaderBlank = false,
    FlashId? Flash = null)
{
    /// <summary>Shown instead of cart details when nothing answered: no
    /// valid header and no flash chip.</summary>
    public const string NoCartMessage = "No cart detected (or non-flash cart with no valid header)";

    /// <summary>False when the header was unreadable, mirror probing found
    /// nothing and no flash chip answered — the "Unknown (X) / 0K" signature
    /// an empty or unseated cart slot produces when the bus floats (see
    /// docs/hardware-validation.md). A blank flash cart reads the same 0xFF
    /// but answers the flash probe, so it counts as detected.</summary>
    public bool CartDetected =>
        Flash != null || RomBytes != 0 || !RomName.StartsWith("Unknown", StringComparison.Ordinal);

    /// <summary>"Flash cart, blank (reads 0xFF)" or "Flash cart, unrecognised
    /// header" when a flash chip answered but the header is invalid; null
    /// otherwise (the normal header-driven details apply).</summary>
    public string? HeaderlessFlashStatus => Flash == null || HeaderValid ? null
        : HeaderBlank ? "Flash cart, blank (reads 0xFF)" : "Flash cart, unrecognised header";

    /// <summary>Human-readable target system for display.</summary>
    public string SystemName => Is32X ? "Sega 32X" : "Mega Drive / Genesis";
}

/// <summary>
/// High-level cartridge workflows over a connected programmer — the API
/// surface for front-ends (CLI, TUI, GUI). Operations are synchronous and
/// report progress through a plain callback; they do no console or file
/// I/O. The session owns the serial port and closes it on Dispose.
///
/// The workflow logic (delays, bank-register writes, block sizes, erase/
/// program/verify sequences) is ported from the original client's Form1
/// button handlers.
/// </summary>
public sealed class FlashKitSession : IDisposable
{
    const int SaveWindow = 0x200000;
    // Flash size assumed when CFI gives none (or the check is skipped): the
    // original client's 4 MB chip.
    const int DefaultChipBytes = 0x400000;
    // Cart address space: the standard 4 MB window plus the Mega-CD region a
    // flat-mapped cart (MegaWiFi's ~CART_IN/A22 hack) answers in.
    const int MaxCartBytes = 0x800000;

    readonly ISerialPort port;

    public Device Device { get; }
    public Cart Cart { get; }

    FlashKitSession(Device device, ISerialPort port)
    {
        Device = device;
        this.port = port;
        Cart = new Cart(device);
    }

    public static FlashKitSession Connect(DeviceConnector? connector = null, string? portName = null)
    {
        var (device, p) = (connector ?? new DeviceConnector()).connect(portName);
        return new FlashKitSession(device, p);
    }

    public string PortName => Device.getPortName();

    /// <summary>Cart name for use in suggested filenames. Headers pad the
    /// 48-char name field with spaces ("SONIC THE          HEDGEHOG 3"), so
    /// runs of spaces are collapsed; the raw name stays available via
    /// <see cref="GetInfo"/>.</summary>
    public string GetRomName()
    {
        Device.setDelay(1);
        return string.Join(' ', Cart.getRomName().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Suggested dump filename: the collapsed ROM name plus the
    /// system-appropriate extension (.32x for 32X carts, .bin otherwise).</summary>
    public string SuggestedRomFileName() => GetRomName() + (Is32X() ? ".32x" : ".bin");

    /// <summary>Reads the cart header and sizes. When the header has no
    /// "SEGA" signature (blank or foreign flash, empty slot) it also runs the
    /// read-only <see cref="TryIdentifyFlash"/> probe, so a blank flash cart
    /// is told apart from an empty slot. A valid header skips the probe:
    /// mask-ROM and mapper carts are never sent flash commands unless
    /// <paramref name="probeFlash"/> forces it.</summary>
    public CartInfo GetInfo(bool probeFlash = false)
    {
        Device.setDelay(1);
        var hdr = ReadHeader();
        var info = new CartInfo(Cart.getRomName(), ProbeRomSize(hdr), Cart.getRamSize(),
            HeaderRomSize(hdr), IsThirtyTwoX(hdr), RegionName(hdr),
            HeaderValid: HasSegaSignature(hdr), HeaderBlank: Array.TrueForAll(hdr, b => b == 0xFF));
        return info.HeaderValid && !probeFlash ? info : info with { Flash = TryIdentifyFlash() };
    }

    /// <summary>Standard headers start the 0x100 system field with "SEGA"
    /// ("SEGA MEGA DRIVE", "SEGA GENESIS", "SEGA 32X"); some pad it with a
    /// leading space, so search the whole field.</summary>
    internal static bool HasSegaSignature(byte[] header) =>
        System.Text.Encoding.ASCII.GetString(header, 0x100, 16).Contains("SEGA", StringComparison.Ordinal);

    /// <summary>Friendly region from the header's 0x1F0 field. Mirrors the
    /// letter codes Cart.getRomRegion appends to the ROM name (W/U/J/E/X):
    /// a first char differing from a meaningful second means multi-region.</summary>
    internal static string RegionName(byte[] header)
    {
        byte val = header[0x1F0];
        if (val != header[0x1F1] && header[0x1F1] != 0x20 && header[0x1F1] != 0)
            return "World";
        return val switch
        {
            (byte)'F' or (byte)'C' => "World",
            (byte)'U' or (byte)'W' or (byte)'4' or 4 => "USA",
            (byte)'J' or (byte)'B' or (byte)'1' or 1 => "Japan",
            (byte)'E' or (byte)'A' or (byte)'8' or 8 => "Europe",
            _ => "Unknown",
        };
    }

    /// <summary>ROM size declared in the cart header, or null when the header
    /// value is implausible. Unlike mirror probing this reports the intended
    /// image extent even on flash carts, where partially programmed flash has
    /// no mirrors to probe.</summary>
    public int? ReadHeaderRomSize() => HeaderRomSize(ReadHeader());

    /// <summary>True for a Sega 32X cart: the 16-byte system field at 0x100
    /// contains "32X" ("SEGA 32X …"). Standard carts read "SEGA MEGA DRIVE"
    /// or "SEGA GENESIS". The 32X hardware is a Genesis add-on, so a 32X cart
    /// is physically a Mega Drive cartridge and only the header tells them
    /// apart. Verified on real DOOM and Kolibri carts vs. five MD titles.</summary>
    public bool Is32X() => IsThirtyTwoX(ReadHeader());

    byte[] ReadHeader()
    {
        Device.setDelay(1);
        var hdr = new byte[512];
        Device.writeWord(0xA13000, 0x0000);
        Device.setAddr(0);
        Device.read(hdr, 0, 512);
        return hdr;
    }

    internal static bool IsThirtyTwoX(byte[] header) =>
        System.Text.Encoding.ASCII.GetString(header, 0x100, 16).Contains("32X", StringComparison.Ordinal);

    internal static int? HeaderRomSize(byte[] header)
    {
        // Sega header: ROM start address at 0x1A0, end address at 0x1A4,
        // both big-endian. A plausible header has start 0 and an even size
        // within the chip; anything else (blank flash reads 0xFF..) is null.
        long start = (uint)((header[0x1A0] << 24) | (header[0x1A1] << 16) | (header[0x1A2] << 8) | header[0x1A3]);
        long size = (uint)((header[0x1A4] << 24) | (header[0x1A5] << 16) | (header[0x1A6] << 8) | header[0x1A7]) + 1L;
        if (start != 0 || size < 0x200 || size > MaxCartBytes || size % 2 != 0) return null;
        return (int)size;
    }

    /// <summary>Dumps the cart ROM. <paramref name="size"/> overrides the
    /// mirror-probed size (e.g. with <see cref="ReadHeaderRomSize"/>).</summary>
    public byte[] ReadRom(Action<OperationProgress>? progress = null, int? size = null)
    {
        if (size is int s && (s <= 0 || s % 2 != 0 || s > MaxCartBytes))
            throw new ArgumentException("invalid ROM size override: " + s);
        Device.setDelay(1);
        int romSize = size ?? ProbeRomSize(ReadHeader());
        var rom = new byte[romSize];
        progress?.Invoke(new(OperationPhase.Read, 0, romSize));
        Device.writeWord(0xA13000, 0x0000);
        Device.setAddr(0);
        for (int i = 0; i < romSize; i += 32768)
        {
            int block = Math.Min(32768, romSize - i);
            Device.read(rom, i, block);
            progress?.Invoke(new(OperationPhase.Read, i + block, romSize));
        }
        return rom;
    }

    /// <summary>Mirror-probed ROM size. The ported probe stops at 4 MB; a
    /// flat 8 MiB flash cart (MegaWiFi) is recognised only when the probe hits
    /// that limit, the header declares more, and the upper half
    /// (0x400000+) differs from the lower half. The header gate keeps A22-high
    /// reads off ordinary carts; mapper carts (SSF2's header says 5 MB) mirror
    /// there and stay at 4 MB.</summary>
    int ProbeRomSize(byte[] header)
    {
        int len = Cart.getRomSize();
        if (len != DefaultChipBytes || HeaderRomSize(header) is not > DefaultChipBytes) return len;
        var lower = new byte[512];
        var upper = new byte[512];
        Device.writeWord(0xA13000, 0x0000);
        for (int off = 0; off < DefaultChipBytes; off += 0x80000)
        {
            Device.setAddr(off);
            Device.read(lower, 0, lower.Length);
            Device.setAddr(DefaultChipBytes + off);
            Device.read(upper, 0, upper.Length);
            if (!lower.AsSpan().SequenceEqual(upper)) return MaxCartBytes;
        }
        return len;
    }

    static readonly byte[] CfiQry = { 0x00, 0x51, 0x00, 0x52, 0x00, 0x59 };

    /// <summary>Verifies a CFI-capable flash chip answers on the cart bus
    /// before destructive writes — fails fast instead of "erasing" a mask ROM
    /// cart and finding out at verify time. Not part of the original client;
    /// the check is the standard CFI probe, as flashkit-md-py does it.
    /// Returns the chip's identification: its CFI size bounds writes and
    /// full erase.</summary>
    public FlashId CheckFlash()
    {
        var cfi = new byte[6];
        var id = ProbeFlash(cfi);
        if (id?.CfiOk != true) throw new FlashChipNotFoundException(cfi);
        return id;
    }

    /// <summary>
    /// Read-only flash identification: CFI query (0x98, "QRY" at word 0x10,
    /// device size 2^n at word 0x27) and AMD/Spansion autoselect (AA/55/90;
    /// manufacturer at word 0, device ID at words 1/0x0E/0x0F; 8-bit chip
    /// pairs answer on the low byte only). Sends only
    /// query, autoselect and reset (0xF0) commands — never erase or program —
    /// and always returns the chip to read-array mode. Returns null when no
    /// flash chip answered (empty slot, mask ROM).
    /// </summary>
    public FlashId? TryIdentifyFlash() => ProbeFlash(new byte[6]);

    FlashId? ProbeFlash(byte[] cfi)
    {
        // Baseline read-array contents at the autoselect ID addresses: a
        // chip that ignores autoselect (mask ROM, floating bus) reads the
        // same before and after, so differences mean a real ID.
        var array = new byte[0x20];
        var ids = new byte[0x20];
        var size = new byte[2];
        Device.setAddr(0);
        Device.read(array, 0, array.Length);
        bool cfiOk;
        try
        {
            Device.writeByte(0x55 * 2, 0x98);
            Device.setAddr(0x20);
            Device.read(cfi, 0, 6);
            cfiOk = cfi.AsSpan().SequenceEqual(CfiQry);
            if (cfiOk)
            {
                Device.setAddr(0x4E); // CFI word 0x27: device size, low byte
                Device.read(size, 0, 2);
            }
            Device.writeWord(0, 0xF0);

            Device.writeWord(0x555 * 2, 0xAA);
            Device.writeWord(0x2AA * 2, 0x55);
            Device.writeWord(0x555 * 2, 0x90);
            Device.setAddr(0);
            Device.read(ids, 0, ids.Length);
            Device.writeWord(0, 0xF0);
        }
        catch (Exception)
        {
            try { Device.writeWord(0, 0xF0); }
            catch (Exception) { }
            throw;
        }

        int[] idAddrs = { 0x00, 0x02, 0x1C, 0x1E };
        bool idOk = Array.Exists(idAddrs, a => Word(ids, a) != Word(array, a));
        if (!cfiOk && !idOk) return null;
        int sizeBytes = cfiOk && size[1] is >= 10 and <= 30 ? 1 << size[1] : 0;
        if (!idOk) return new FlashId(cfiOk, sizeBytes, 0, Array.Empty<ushort>());

        // Repro carts often pair two 8-bit chips (high and low data byte).
        // The commands go out as words with a 0x00 high byte, so only the
        // low-byte chip enters autoselect: its ID is in the low bytes while
        // the high bytes still read array data.
        bool byteWide = Array.TrueForAll(idAddrs, a => ids[a] == array[a]);
        ushort Id(int byteAddr) => byteWide ? ids[byteAddr + 1] : Word(ids, byteAddr);
        // Device ID words 2/3 are meaningful only for the AMD extended-ID
        // scheme (word 1 low byte 0x7E); otherwise they hold unrelated
        // autoselect data such as sector protection.
        ushort dev1 = Id(0x02);
        ushort[] devices = (dev1 & 0xFF) == 0x7E ? new[] { dev1, Id(0x1C), Id(0x1E) } : new[] { dev1 };
        return new FlashId(cfiOk, sizeBytes, Id(0x00), devices, byteWide);
    }

    static ushort Word(byte[] b, int byteAddr) => (ushort)((b[byteAddr] << 8) | b[byteAddr + 1]);

    /// <summary>
    /// Erases, programs, and verifies a ROM image (padded to 64 KB). The
    /// chip size comes from CFI, capped at the 8 MiB cart space; it is 4 MB
    /// when CFI gives none or <paramref name="skipFlashCheck"/> skips the
    /// probe. An image larger than the chip is refused before anything is
    /// erased. <paramref name="fullErase"/> wipes exactly that chip size
    /// first, so smaller (mirrored) chips are no longer over-erased.
    /// </summary>
    public void WriteRom(byte[] image, bool fullErase = false, Action<OperationProgress>? progress = null, bool skipFlashCheck = false)
    {
        Device.setDelay(0);
        int chipBytes = DefaultChipBytes;
        if (!skipFlashCheck && CheckFlash().SizeBytes is > 0 and var cfiBytes)
            chipBytes = Math.Min(cfiBytes, MaxCartBytes);
        if (image.Length > chipBytes)
            throw new ArgumentException($"image is {image.Length} bytes but the flash chip holds {chipBytes} bytes");
        int romSize = image.Length;
        if (romSize % 65536 != 0) romSize = romSize / 65536 * 65536 + 65536;
        if (romSize > chipBytes) romSize = chipBytes;
        var rom = new byte[romSize];
        Array.Copy(image, rom, image.Length);

        try
        {
            int eraseLen = fullErase ? chipBytes : romSize;
            progress?.Invoke(new(OperationPhase.Erase, 0, eraseLen));
            Device.flashResetByPass();
            for (int i = 0; i < eraseLen; i += 65536)
            {
                Device.flashErase(i);
                progress?.Invoke(new(OperationPhase.Erase, Math.Min(i + 65536, eraseLen), eraseLen));
            }

            progress?.Invoke(new(OperationPhase.Write, 0, romSize));
            Device.flashUnlockBypass();
            Device.setAddr(0);
            for (int i = 0; i < romSize; i += 4096)
            {
                Device.flashWrite(rom, i, 4096);
                progress?.Invoke(new(OperationPhase.Write, i + 4096, romSize));
            }
            Device.flashResetByPass();

            progress?.Invoke(new(OperationPhase.Verify, 0, romSize));
            var rom2 = new byte[romSize];
            Device.setAddr(0);
            for (int i = 0; i < romSize; i += 4096)
            {
                Device.read(rom2, i, 4096);
                progress?.Invoke(new(OperationPhase.Verify, i + 4096, romSize));
            }
            for (int i = 0; i < romSize; i++)
            {
                if (rom[i] != rom2[i]) throw new VerifyException(i);
            }
        }
        catch (Exception)
        {
            try { Device.flashResetByPass(); }
            catch (Exception) { }
            throw;
        }
    }

    /// <summary>Dumps save RAM as a word stream (data on odd bytes).</summary>
    public byte[] ReadRam()
    {
        Device.setDelay(1);
        int ramSize = Cart.getRamSize();
        if (ramSize == 0) throw new SaveRamNotFoundException();
        Device.writeWord(0xA13000, 0xffff);
        Device.setAddr(SaveWindow);
        var ram = new byte[ramSize * 2];
        Device.read(ram, 0, ram.Length);
        return ram;
    }

    /// <summary>Writes save RAM (odd bytes of the word stream) and verifies.
    /// Returns the number of words sent.</summary>
    public int WriteRam(byte[] ram, Action<OperationProgress>? progress = null)
    {
        Device.setDelay(1);
        int ramSize = Cart.getRamSize();
        if (ramSize == 0) throw new SaveRamNotFoundException();

        ramSize *= 2;
        int copyLen = ram.Length;
        if (ramSize < copyLen) copyLen = ramSize;
        if (copyLen % 2 != 0) copyLen--;
        progress?.Invoke(new(OperationPhase.Write, 0, copyLen));
        Device.writeWord(0xA13000, 0xffff);
        Device.setAddr(SaveWindow);
        Device.write(ram, 0, copyLen);
        progress?.Invoke(new(OperationPhase.Write, copyLen, copyLen));

        progress?.Invoke(new(OperationPhase.Verify, 0, copyLen));
        var ram2 = new byte[copyLen];
        Device.setAddr(SaveWindow);
        Device.read(ram2, 0, copyLen);
        for (int i = 0; i < copyLen; i++)
        {
            if (i % 2 == 0) continue; // save RAM is 8-bit, on odd bytes
            if (ram[i] != ram2[i]) throw new VerifyException(i);
        }
        progress?.Invoke(new(OperationPhase.Verify, copyLen, copyLen));

        return copyLen / 2;
    }

    /// <summary>
    /// Programs a save image into flash at the save window (0x200000) of an
    /// SRAM-less flash cart. Games see the saves read-only: loadable and
    /// persistent across power cycles, but not overwritable in-game.
    /// Needs a full-size 4 MB chip, like <see cref="WriteRom"/> with
    /// fullErase.
    /// </summary>
    public void BakeSave(byte[] srm, Action<OperationProgress>? progress = null, bool skipFlashCheck = false)
    {
        if (srm.Length == 0) throw new ArgumentException("save image is empty");
        if (srm.Length % 2 != 0) throw new ArgumentException("save image must have an even length (word stream)");
        if (srm.Length > 0x100000) throw new ArgumentException("save image too large (max 1 MB)");

        Device.setDelay(0);
        if (!skipFlashCheck) CheckFlash();
        try
        {
            int span = (srm.Length + 65535) / 65536 * 65536;
            progress?.Invoke(new(OperationPhase.Erase, 0, span));
            Device.flashResetByPass();
            for (int i = 0; i < span; i += 65536)
            {
                Device.flashErase(SaveWindow + i);
                progress?.Invoke(new(OperationPhase.Erase, i + 65536, span));
            }

            progress?.Invoke(new(OperationPhase.Write, 0, srm.Length));
            Device.flashUnlockBypass();
            Device.setAddr(SaveWindow);
            for (int i = 0; i < srm.Length; i += 4096)
            {
                int block = Math.Min(4096, srm.Length - i);
                Device.flashWrite(srm, i, block);
                progress?.Invoke(new(OperationPhase.Write, i + block, srm.Length));
            }
            Device.flashResetByPass();

            progress?.Invoke(new(OperationPhase.Verify, 0, srm.Length));
            var readback = new byte[srm.Length];
            Device.setAddr(SaveWindow);
            Device.read(readback, 0, readback.Length);
            for (int i = 0; i < srm.Length; i++)
            {
                if (srm[i] != readback[i]) throw new VerifyException(i);
            }
            progress?.Invoke(new(OperationPhase.Verify, srm.Length, srm.Length));
        }
        catch (Exception)
        {
            try { Device.flashResetByPass(); }
            catch (Exception) { }
            throw;
        }
    }

    public void Dispose()
    {
        try { port.Close(); }
        catch (Exception) { }
    }
}
