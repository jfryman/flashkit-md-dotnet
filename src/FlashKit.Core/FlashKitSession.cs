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
/// is true when the cart is a Sega 32X title (see <see cref="FlashKitSession"/>).</summary>
public sealed record CartInfo(string RomName, int RomBytes, int RamBytes, int? HeaderRomBytes = null, bool Is32X = false)
{
    /// <summary>False when the header was unreadable and mirror probing found
    /// nothing — the "Unknown (X) / 0K" signature an empty or unseated cart
    /// slot produces when the bus floats (see docs/hardware-validation.md).</summary>
    public bool CartDetected => RomBytes != 0 || !RomName.StartsWith("Unknown", StringComparison.Ordinal);

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
    const int FlashChipBytes = 0x400000;

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

    public CartInfo GetInfo()
    {
        Device.setDelay(1);
        var hdr = ReadHeader();
        return new CartInfo(Cart.getRomName(), Cart.getRomSize(), Cart.getRamSize(),
            HeaderRomSize(hdr), IsThirtyTwoX(hdr));
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
        if (start != 0 || size < 0x200 || size > FlashChipBytes || size % 2 != 0) return null;
        return (int)size;
    }

    /// <summary>Dumps the cart ROM. <paramref name="size"/> overrides the
    /// mirror-probed size (e.g. with <see cref="ReadHeaderRomSize"/>).</summary>
    public byte[] ReadRom(Action<OperationProgress>? progress = null, int? size = null)
    {
        if (size is int s && (s <= 0 || s % 2 != 0 || s > FlashChipBytes))
            throw new ArgumentException("invalid ROM size override: " + s);
        Device.setDelay(1);
        int rom_size = size ?? Cart.getRomSize();
        var rom = new byte[rom_size];
        progress?.Invoke(new(OperationPhase.Read, 0, rom_size));
        Device.writeWord(0xA13000, 0x0000);
        Device.setAddr(0);
        for (int i = 0; i < rom_size; i += 32768)
        {
            int block = Math.Min(32768, rom_size - i);
            Device.read(rom, i, block);
            progress?.Invoke(new(OperationPhase.Read, i + block, rom_size));
        }
        return rom;
    }

    static readonly byte[] CfiQry = { 0x00, 0x51, 0x00, 0x52, 0x00, 0x59 };

    /// <summary>Verifies a CFI-capable flash chip answers on the cart bus
    /// (query mode via 0x98, 'QRY' at word address 0x10) before destructive
    /// writes — fails fast instead of "erasing" a mask ROM cart and finding
    /// out at verify time. Not part of the original client; the check is the
    /// standard CFI probe, as flashkit-md-py does it.</summary>
    public void CheckFlash()
    {
        Device.writeByte(0x55 * 2, 0x98);
        var cfi = new byte[6];
        Device.setAddr(0x20);
        Device.read(cfi, 0, 6);
        Device.writeByte(0, 0xf0);
        if (!cfi.AsSpan().SequenceEqual(CfiQry)) throw new FlashChipNotFoundException(cfi);
    }

    /// <summary>
    /// Erases, programs, and verifies a ROM image (padded to 64 KB, capped
    /// at the 4 MB chip). <paramref name="fullErase"/> wipes the whole chip
    /// first — only safe on carts with a full-size chip, since smaller chips
    /// mirror the ROM into the upper address space.
    /// </summary>
    public void WriteRom(byte[] image, bool fullErase = false, Action<OperationProgress>? progress = null, bool skipFlashCheck = false)
    {
        Device.setDelay(0);
        if (!skipFlashCheck) CheckFlash();
        int rom_size = image.Length;
        if (rom_size % 65536 != 0) rom_size = rom_size / 65536 * 65536 + 65536;
        if (rom_size > FlashChipBytes) rom_size = FlashChipBytes;
        var rom = new byte[rom_size];
        Array.Copy(image, rom, Math.Min(image.Length, rom_size));

        try
        {
            int erase_len = fullErase ? FlashChipBytes : rom_size;
            progress?.Invoke(new(OperationPhase.Erase, 0, erase_len));
            Device.flashResetByPass();
            for (int i = 0; i < erase_len; i += 65536)
            {
                Device.flashErase(i);
                progress?.Invoke(new(OperationPhase.Erase, Math.Min(i + 65536, erase_len), erase_len));
            }

            progress?.Invoke(new(OperationPhase.Write, 0, rom_size));
            Device.flashUnlockBypass();
            Device.setAddr(0);
            for (int i = 0; i < rom_size; i += 4096)
            {
                Device.flashWrite(rom, i, 4096);
                progress?.Invoke(new(OperationPhase.Write, i + 4096, rom_size));
            }
            Device.flashResetByPass();

            progress?.Invoke(new(OperationPhase.Verify, 0, rom_size));
            var rom2 = new byte[rom_size];
            Device.setAddr(0);
            for (int i = 0; i < rom_size; i += 4096)
            {
                Device.read(rom2, i, 4096);
                progress?.Invoke(new(OperationPhase.Verify, i + 4096, rom_size));
            }
            for (int i = 0; i < rom_size; i++)
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
        int ram_size = Cart.getRamSize();
        if (ram_size == 0) throw new Exception("RAM is not detected");
        Device.writeWord(0xA13000, 0xffff);
        Device.setAddr(SaveWindow);
        var ram = new byte[ram_size * 2];
        Device.read(ram, 0, ram.Length);
        return ram;
    }

    /// <summary>Writes save RAM (odd bytes of the word stream) and verifies.
    /// Returns the number of words sent.</summary>
    public int WriteRam(byte[] ram, Action<OperationProgress>? progress = null)
    {
        Device.setDelay(1);
        int ram_size = Cart.getRamSize();
        if (ram_size == 0) throw new Exception("RAM is not detected");

        ram_size *= 2;
        int copy_len = ram.Length;
        if (ram_size < copy_len) copy_len = ram_size;
        if (copy_len % 2 != 0) copy_len--;
        progress?.Invoke(new(OperationPhase.Write, 0, copy_len));
        Device.writeWord(0xA13000, 0xffff);
        Device.setAddr(SaveWindow);
        Device.write(ram, 0, copy_len);
        progress?.Invoke(new(OperationPhase.Write, copy_len, copy_len));

        progress?.Invoke(new(OperationPhase.Verify, 0, copy_len));
        var ram2 = new byte[copy_len];
        Device.setAddr(SaveWindow);
        Device.read(ram2, 0, copy_len);
        for (int i = 0; i < copy_len; i++)
        {
            if (i % 2 == 0) continue; // save RAM is 8-bit, on odd bytes
            if (ram[i] != ram2[i]) throw new VerifyException(i);
        }
        progress?.Invoke(new(OperationPhase.Verify, copy_len, copy_len));

        return copy_len / 2;
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
