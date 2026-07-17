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

public sealed record CartInfo(string RomName, int RomBytes, int RamBytes);

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

    public string GetRomName()
    {
        Device.setDelay(1);
        return Cart.getRomName();
    }

    public CartInfo GetInfo()
    {
        Device.setDelay(1);
        return new CartInfo(Cart.getRomName(), Cart.getRomSize(), Cart.getRamSize());
    }

    /// <summary>Dumps the cart ROM (size auto-detected via mirror probing).</summary>
    public byte[] ReadRom(Action<OperationProgress>? progress = null)
    {
        Device.setDelay(1);
        int rom_size = Cart.getRomSize();
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

    /// <summary>
    /// Erases, programs, and verifies a ROM image (padded to 64 KB, capped
    /// at the 4 MB chip). <paramref name="fullErase"/> wipes the whole chip
    /// first — only safe on carts with a full-size chip, since smaller chips
    /// mirror the ROM into the upper address space.
    /// </summary>
    public void WriteRom(byte[] image, bool fullErase = false, Action<OperationProgress>? progress = null)
    {
        Device.setDelay(0);
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
    public void BakeSave(byte[] srm, Action<OperationProgress>? progress = null)
    {
        if (srm.Length == 0) throw new ArgumentException("save image is empty");
        if (srm.Length % 2 != 0) throw new ArgumentException("save image must have an even length (word stream)");
        if (srm.Length > 0x100000) throw new ArgumentException("save image too large (max 1 MB)");

        Device.setDelay(0);
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
