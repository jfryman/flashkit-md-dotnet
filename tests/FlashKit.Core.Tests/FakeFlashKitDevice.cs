namespace FlashKit.Core.Tests;

/// <summary>
/// In-memory emulation of the FlashKit MD programmer firmware plus an attached
/// cartridge, presented as an <see cref="ISerialPort"/>.
///
/// Protocol (inferred from the original Device.cs): each command byte has the
/// opcode in the low nibble (ADDR/LEN/RD/WR/RY/DELAY) and flags in the high
/// bits (INC=128, SINGLE=64, DEV_ID=32, MODE8=16). ADDR/LEN/DELAY consume one
/// argument byte each, shifting into 24-bit word-address / 16-bit word-count
/// registers. Reads reply big-endian words.
///
/// Cartridge model:
///  - ROM: power-of-two image, mirrored across the 4 MB cart space.
///  - SRAM: one 8-bit byte per 16-bit word (low byte; high byte reads 0xFF),
///    mapped at 0x200000 while the 0xA13000 bank register is non-zero.
///  - Flash (when <see cref="FlashWritable"/>): AMD command set as used by the
///    client — sector erase (AA/55/80/AA/55 then 0x30 per sector, 8 KB each),
///    unlock bypass (AA/55/20), bypass program (A0 + data, AND semantics),
///    reset (F0, 90/00), CFI query (98 → 'QRY' at word 0x10, F0 exits).
/// </summary>
sealed class FakeFlashKitDevice : ISerialPort
{
    public const int SramBase = 0x200000;
    public const int SramWindowEnd = 0x400000;
    const int EraseSectorBytes = 8192;

    readonly byte[] rom;
    readonly byte[]? sram;
    readonly Queue<byte> output = new();

    public bool FlashWritable { get; init; }
    public byte DeviceId { get; init; } = 0x55;

    /// <summary>False simulates an empty cart slot: the bus floats, so reads
    /// return open-bus 0xFFFF and writes (including the 0xA13000 bank
    /// register, which lives on the cart) go nowhere. The programmer itself
    /// still answers the device-ID handshake. Settable mid-test to emulate
    /// inserting or removing a cartridge.</summary>
    public bool CartInserted { get; set; } = true;

    /// <summary>True simulates yanking the USB cable: every serial call
    /// (including reopening) throws IOException until cleared.</summary>
    public bool Disconnected { get; set; }

    /// <summary>How many times the port was opened — lets tests assert a
    /// session is being held rather than reopened per operation.</summary>
    public int OpenCount { get; private set; }
    public byte Delay { get; private set; }

    // firmware registers
    int addr;            // 24-bit word address
    int len;             // 16-bit word count
    // parser state
    enum State { Cmd, AddrArg, LenArg, DelayArg, Wr8, Wr16, WrBlock }
    State state = State.Cmd;
    byte pendingCmd;
    int pendingHi = -1;
    int blockRemaining;
    // cartridge state
    int bankReg;
    int flashSeq;
    bool bypass;
    bool programArmed;
    bool eraseArmed;
    bool cfiMode;

    public FakeFlashKitDevice(byte[] rom, int sramBytes = 0)
    {
        if ((rom.Length & (rom.Length - 1)) != 0) throw new ArgumentException("ROM size must be a power of two");
        this.rom = rom;
        if (sramBytes > 0) sram = new byte[sramBytes];
    }

    public byte[] Rom => rom;
    public byte[] Sram => sram ?? throw new InvalidOperationException("no SRAM configured");

    public string PortName { get; init; } = "FAKE";
    public int ReadTimeout { get; set; }
    public int WriteTimeout { get; set; }
    public void Open()
    {
        ThrowIfDisconnected();
        OpenCount++;
    }

    public void Close() { }

    public void Write(byte[] buffer, int offset, int count)
    {
        ThrowIfDisconnected();
        for (int i = 0; i < count; i++) Feed(buffer[offset + i]);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        ThrowIfDisconnected();
        if (output.Count == 0) throw new TimeoutException();
        int n = 0;
        while (n < count && output.Count > 0) buffer[offset + n++] = output.Dequeue();
        return n;
    }

    public int ReadByte()
    {
        ThrowIfDisconnected();
        if (output.Count == 0) throw new TimeoutException();
        return output.Dequeue();
    }

    void ThrowIfDisconnected()
    {
        if (Disconnected) throw new IOException("device disconnected");
    }

    void Feed(byte b)
    {
        switch (state)
        {
            case State.Cmd:
                OnCommand(b);
                break;
            case State.AddrArg:
                addr = ((addr << 8) | b) & 0xFFFFFF;
                state = State.Cmd;
                break;
            case State.LenArg:
                len = ((len << 8) | b) & 0xFFFF;
                state = State.Cmd;
                break;
            case State.DelayArg:
                Delay = b;
                state = State.Cmd;
                break;
            case State.Wr8:
                BusWrite8(addr, b);
                state = State.Cmd;
                break;
            case State.Wr16:
                if (pendingHi < 0)
                {
                    pendingHi = b;
                }
                else
                {
                    BusWrite16(addr, (ushort)((pendingHi << 8) | b));
                    if ((pendingCmd & 128) != 0) addr = (addr + 1) & 0xFFFFFF;
                    pendingHi = -1;
                    state = State.Cmd;
                }
                break;
            case State.WrBlock:
                if (pendingHi < 0)
                {
                    pendingHi = b;
                }
                else
                {
                    BusWrite16(addr, (ushort)((pendingHi << 8) | b));
                    addr = (addr + 1) & 0xFFFFFF;
                    pendingHi = -1;
                    if (--blockRemaining == 0) state = State.Cmd;
                }
                break;
        }
    }

    void OnCommand(byte b)
    {
        const int INC = 128, SINGLE = 64, DEV_ID = 32, MODE8 = 16;
        switch (b & 0x0F)
        {
            case 0: // CMD_ADDR
                state = State.AddrArg;
                break;
            case 1: // CMD_LEN
                state = State.LenArg;
                break;
            case 5: // CMD_DELAY
                state = State.DelayArg;
                break;
            case 4: // CMD_RY: flash is always instantly ready in the fake
                break;
            case 2: // CMD_RD
                if ((b & DEV_ID) != 0)
                {
                    output.Enqueue(DeviceId);
                    output.Enqueue(DeviceId);
                }
                else if ((b & SINGLE) != 0)
                {
                    ushort v = BusRead16(addr);
                    output.Enqueue((byte)(v >> 8));
                    output.Enqueue((byte)v);
                }
                else if ((b & INC) != 0)
                {
                    for (int i = 0; i < len; i++)
                    {
                        ushort v = BusRead16(addr);
                        output.Enqueue((byte)(v >> 8));
                        output.Enqueue((byte)v);
                        addr = (addr + 1) & 0xFFFFFF;
                    }
                }
                break;
            case 3: // CMD_WR
                if ((b & MODE8) != 0)
                {
                    pendingCmd = b;
                    state = State.Wr8;
                }
                else if ((b & SINGLE) != 0)
                {
                    pendingCmd = b;
                    state = State.Wr16;
                }
                else if ((b & INC) != 0)
                {
                    pendingCmd = b;
                    blockRemaining = len;
                    pendingHi = -1;
                    state = blockRemaining > 0 ? State.WrBlock : State.Cmd;
                }
                break;
        }
    }

    bool SramActive(int byteAddr) =>
        sram != null && bankReg != 0 && byteAddr >= SramBase && byteAddr < SramWindowEnd;

    int SramIndex(int wordAddr) => (wordAddr - SramBase / 2) % sram!.Length;

    ushort BusRead16(int wordAddr)
    {
        if (!CartInserted) return 0xFFFF;
        int byteAddr = wordAddr * 2;
        if (cfiMode) return wordAddr switch { 0x10 => 0x0051, 0x11 => 0x0052, 0x12 => 0x0059, _ => (ushort)0 };
        if (SramActive(byteAddr)) return (ushort)(0xFF00 | sram![SramIndex(wordAddr)]);
        return (ushort)((rom[byteAddr % rom.Length] << 8) | rom[(byteAddr + 1) % rom.Length]);
    }

    void BusWrite16(int wordAddr, ushort val)
    {
        if (!CartInserted) return;
        int byteAddr = wordAddr * 2;
        if (byteAddr == 0xA13000)
        {
            bankReg = val;
            return;
        }
        if (SramActive(byteAddr))
        {
            sram![SramIndex(wordAddr)] = (byte)val;
            return;
        }
        FlashCommand(wordAddr, val);
    }

    void BusWrite8(int wordAddr, byte val)
    {
        if (!CartInserted) return;
        int byteAddr = wordAddr * 2;
        if (byteAddr == 0xA13000)
        {
            bankReg = val;
            return;
        }
        if (SramActive(byteAddr))
        {
            sram![SramIndex(wordAddr)] = val;
            return;
        }
        FlashCommand(wordAddr, val);
    }

    void FlashCommand(int wordAddr, ushort val)
    {
        if (programArmed)
        {
            programArmed = false;
            if (FlashWritable)
            {
                int b = (wordAddr * 2) % rom.Length;
                rom[b] &= (byte)(val >> 8);
                rom[(b + 1) % rom.Length] &= (byte)val;
            }
            return;
        }

        byte v = (byte)val;

        if (v == 0xF0)
        {
            flashSeq = 0;
            eraseArmed = false;
            cfiMode = false;
            return;
        }
        if (bypass)
        {
            if (v == 0xA0) programArmed = true;
            else if (v == 0x90) bypass = false;
            return;
        }
        if (v == 0x98)
        {
            // CFI query mode; a mask ROM cart (FlashWritable=false) ignores it
            cfiMode = FlashWritable;
            return;
        }
        if (eraseArmed && v == 0x30)
        {
            if (FlashWritable)
            {
                int start = (wordAddr * 2) % rom.Length;
                for (int i = 0; i < EraseSectorBytes; i++) rom[(start + i) % rom.Length] = 0xFF;
            }
            return;
        }

        switch (flashSeq)
        {
            case 0:
                if (wordAddr == 0x555 && v == 0xAA) flashSeq = 1;
                break;
            case 1:
                if (wordAddr == 0x2AA && v == 0x55) flashSeq = 2;
                else flashSeq = 0;
                break;
            case 2:
                if (wordAddr == 0x555 && v == 0x80) flashSeq = 3;
                else if (wordAddr == 0x555 && v == 0x20) { bypass = true; flashSeq = 0; }
                else flashSeq = 0;
                break;
            case 3:
                if (wordAddr == 0x555 && v == 0xAA) flashSeq = 4;
                else flashSeq = 0;
                break;
            case 4:
                if (wordAddr == 0x2AA && v == 0x55) eraseArmed = true;
                flashSeq = 0;
                break;
        }
    }
}
