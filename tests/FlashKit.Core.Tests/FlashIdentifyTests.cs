namespace FlashKit.Core.Tests;

/// <summary>
/// The read-only flash ID probe and how GetInfo uses it to tell a blank
/// flash cart (reads 0xFF, but answers CFI/autoselect) from an empty slot
/// (reads 0xFF, nothing answers). Reference values are from a real
/// MegaWiFi Rev.C (Spansion S29GL064N, 8 MiB) — docs/hardware-validation.md.
/// </summary>
public class FlashIdentifyTests
{
    static FlashKitSession Connect(ISerialPort port) =>
        FlashKitSession.Connect(new DeviceConnector(
            () => new[] { "/dev/ttyUSB0" }, _ => port, HostOs.Linux));

    static byte[] Blank(int size)
    {
        var b = new byte[size];
        Array.Fill(b, (byte)0xFF);
        return b;
    }

    [Fact]
    public void GetInfo_identifies_a_blank_flash_cart()
    {
        var fake = new FakeFlashKitDevice(Blank(0x800000)) { FlashWritable = true };
        using var session = Connect(fake);

        var info = session.GetInfo();

        Assert.True(info.CartDetected);
        Assert.False(info.HeaderValid);
        Assert.True(info.HeaderBlank);
        Assert.Equal("Flash cart, blank (reads 0xFF)", info.HeaderlessFlashStatus);
        var flash = Assert.IsType<FlashId>(info.Flash);
        Assert.True(flash.CfiOk);
        Assert.Equal(8 * 1024 * 1024, flash.SizeBytes);
        Assert.Equal(0x0001, flash.Manufacturer);
        Assert.Equal(new ushort[] { 0x227E, 0x2210, 0x2200 }, flash.DeviceId);
        Assert.Equal("CFI OK, 8 MiB, mfr 0x0001 dev 227E/2210/2200 (Spansion S29GL064N family)", flash.Describe());
        Assert.Equal(0, fake.FlashModifyCount);
        Assert.All(session.ReadRom(size: 0x10000), b => Assert.Equal(0xFF, b)); // back in read-array mode
    }

    [Fact]
    public void GetInfo_reports_unrecognised_header_on_flash_without_SEGA_signature()
    {
        var fake = new FakeFlashKitDevice(new byte[0x400000]) { FlashWritable = true };
        using var session = Connect(fake);

        var info = session.GetInfo();

        Assert.Equal("Flash cart, unrecognised header", info.HeaderlessFlashStatus);
    }

    [Fact]
    public void GetInfo_on_empty_slot_probes_but_finds_nothing()
    {
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)) { FlashWritable = true, CartInserted = false };
        using var session = Connect(fake);

        var info = session.GetInfo();

        Assert.False(info.CartDetected);
        Assert.Null(info.Flash);
        Assert.Null(info.HeaderlessFlashStatus);
    }

    [Fact]
    public void GetInfo_skips_the_probe_for_a_valid_header()
    {
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)) { FlashWritable = true };
        using var session = Connect(fake);

        var info = session.GetInfo();

        Assert.True(info.HeaderValid);
        Assert.Null(info.Flash);
        Assert.Equal(0, fake.FlashQueryCount);
    }

    [Fact]
    public void GetInfo_probes_a_valid_header_when_forced()
    {
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)) { FlashWritable = true, CfiSizeExponent = 0x16 };
        using var session = Connect(fake);

        var info = session.GetInfo(probeFlash: true);

        Assert.Equal("TEST GAME (U)", info.RomName);
        Assert.Null(info.HeaderlessFlashStatus);
        Assert.Equal(4 * 1024 * 1024, info.Flash?.SizeBytes);
    }

    [Fact]
    public void TryIdentifyFlash_returns_null_for_mask_rom()
    {
        var rom = TestRoms.MakeRom(0x80000);
        var fake = new FakeFlashKitDevice(rom.ToArray());
        using var session = Connect(fake);

        Assert.Null(session.TryIdentifyFlash());
        Assert.Equal(rom, session.ReadRom());
    }

    [Fact]
    public void Describe_falls_back_to_raw_hex_for_unknown_chips()
    {
        var known = new FlashId(true, 0x400000, 0x00C2, new ushort[] { 0x22C9 });
        var unknown = new FlashId(true, 0x400000, 0x1234, new ushort[] { 0xAB7E, 0x0001, 0x0002 });
        var noId = new FlashId(true, 0, 0, Array.Empty<ushort>());

        Assert.Equal("CFI OK, 4 MiB, mfr 0x00C2 dev 22C9 (Macronix)", known.Describe());
        Assert.Equal("CFI OK, 4 MiB, mfr 0x1234 dev AB7E/0001/0002", unknown.Describe());
        Assert.Equal("CFI OK, no autoselect ID", noId.Describe());
        Assert.Equal("4 MiB flash", unknown.ShortDescription);
    }

    [Fact]
    public void probe_reports_only_the_first_device_word_without_the_extended_ID_scheme()
    {
        // Real reads from an ST/Micron repro cart (2026-10-28): words 2/3
        // are other autoselect data, not part of the device ID.
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000))
        {
            FlashWritable = true, CfiSizeExponent = 0x16,
            FlashManufacturer = 0x0020, FlashDeviceIds = new ushort[] { 0x2256, 0x0000, 0x0001 },
        };
        using var session = Connect(fake);

        var flash = session.TryIdentifyFlash();

        Assert.Equal(new ushort[] { 0x2256 }, flash?.DeviceId);
        Assert.Equal("CFI OK, 4 MiB, mfr 0x0020 dev 2256 (ST/Micron)", flash?.Describe());
    }

    static FakeFlashKitDevice BytePairRepro(byte[] rom) => new(rom)
    {
        FlashWritable = true, ByteWidePair = true,
        FlashManufacturer = 0xC2, FlashDeviceIds = new ushort[] { 0xA4 },
    };

    [Fact]
    public void GetInfo_identifies_a_blank_8_bit_pair_repro_cart()
    {
        using var session = Connect(BytePairRepro(Blank(0x100000)));

        var info = session.GetInfo();

        Assert.Equal("Flash cart, blank (reads 0xFF)", info.HeaderlessFlashStatus);
        var flash = Assert.IsType<FlashId>(info.Flash);
        Assert.True(flash.ByteWide);
        Assert.False(flash.CfiOk);
        Assert.Equal("no CFI, 8-bit pair, mfr 0xC2 dev A4 (Macronix MX29F040)", flash.Describe());
        Assert.Equal("flash, Macronix MX29F040", flash.ShortDescription);
    }

    [Fact]
    public void WriteRom_refuses_an_autoselect_only_chip_before_touching_it()
    {
        // No CFI means the pre-write check fails even though autoselect
        // answers: the programmer's word-wide commands only reach the
        // low-byte chip of a pair, so a write could never fully succeed.
        var rom = TestRoms.MakeRom(0x100000);
        var pristine = rom.ToArray();
        var fake = BytePairRepro(rom);
        using var session = Connect(fake);

        Assert.Throws<FlashChipNotFoundException>(() => session.WriteRom(new byte[0x10000]));
        Assert.Equal(0, fake.FlashModifyCount);
        Assert.Equal(pristine, fake.Rom);
    }

    [Fact]
    public void probe_decodes_real_8_bit_pair_repro_replies()
    {
        // Real reads from a Sonic 2 repro (2026-10-28): array data at 0, then
        // the same high bytes with the Macronix ID in the low bytes.
        static byte[] Hex(string s) => s.Split('-').Select(h => Convert.ToByte(h, 16)).ToArray();
        var port = new ScriptedPort();
        port.EnqueueReply(0x55, 0x55);
        port.EnqueueReply(Hex("FF-FF-FE-00-00-00-02-06-00-00-02-00-00-00-02-00-00-00-02-00-00-00-02-00-00-00-02-00-00-00-02-00"));
        port.EnqueueReply(0, 0, 0, 0, 0, 0); // no QRY (array data at 0x20)
        port.EnqueueReply(Hex("FF-C2-FE-A4-00-00-02-00-00-C2-02-A4-00-00-02-00-00-C2-02-A4-00-00-02-00-00-C2-02-A4-00-00-02-00"));
        using var session = Connect(port);

        var flash = session.TryIdentifyFlash();

        Assert.Equal("no CFI, 8-bit pair, mfr 0xC2 dev A4 (Macronix MX29F040)", flash?.Describe());
    }

    // Wire-level reply script for the probe, as the MegaWiFi Rev.C answered.
    static ScriptedPort MegaWiFiPort()
    {
        var port = new ScriptedPort();
        port.EnqueueReply(0x55, 0x55);                          // device ID handshake
        port.EnqueueReply(Blank(0x20));                         // read-array baseline @0
        port.EnqueueReply(0x00, 0x51, 0x00, 0x52, 0x00, 0x59);  // CFI @0x20: QRY
        port.EnqueueReply(0x00, 0x17);                          // CFI word 0x27: 2^23
        var ids = new byte[0x20];
        new byte[] { 0x00, 0x01, 0x22, 0x7E }.CopyTo(ids, 0x00);
        new byte[] { 0x22, 0x10, 0x22, 0x00 }.CopyTo(ids, 0x1C);
        port.EnqueueReply(ids);                                 // autoselect @0
        return port;
    }

    [Fact]
    public void probe_decodes_real_hardware_replies_and_sends_only_query_commands()
    {
        var port = MegaWiFiPort();
        using var session = Connect(port);
        port.Written.Clear();

        var flash = session.TryIdentifyFlash();

        Assert.Equal("CFI OK, 8 MiB, mfr 0x0001 dev 227E/2210/2200 (Spansion S29GL064N family)", flash?.Describe());
        // Exact sequence: baseline read, CFI query + reads, reset, autoselect + read, reset.
        byte[] Addr(int byteAddr) { int a = byteAddr / 2; return new byte[] { 0, (byte)(a >> 16), 0, (byte)(a >> 8), 0, (byte)a }; }
        byte[] Read(int bytes) => new byte[] { 1, (byte)(bytes / 2 >> 8), 1, (byte)(bytes / 2), 2 | 128 };
        byte[] W8(int byteAddr, byte v) => Addr(byteAddr).Concat(new byte[] { 3 | 64 | 16, v }).ToArray();
        byte[] W16(int byteAddr, ushort v) => Addr(byteAddr).Concat(new byte[] { 3 | 64, (byte)(v >> 8), (byte)v }).ToArray();
        var expected = new[]
        {
            Addr(0), Read(0x20),
            W8(0x55 * 2, 0x98), Addr(0x20), Read(6), Addr(0x4E), Read(2), W16(0, 0xF0),
            W16(0x555 * 2, 0xAA), W16(0x2AA * 2, 0x55), W16(0x555 * 2, 0x90), Addr(0), Read(0x20), W16(0, 0xF0),
        }.SelectMany(b => b).ToArray();
        Assert.Equal(expected, port.Written);
    }

    [Fact]
    public void probe_resets_to_read_array_when_the_link_fails_mid_probe()
    {
        var port = new ScriptedPort();
        port.EnqueueReply(0x55, 0x55);
        port.EnqueueReply(Blank(0x20)); // baseline, then the CFI read times out
        using var session = Connect(port);
        port.Written.Clear();

        Assert.Throws<TimeoutException>(() => session.TryIdentifyFlash());

        byte[] reset = { 0, 0, 0, 0, 0, 0, 3 | 64, 0x00, 0xF0 };
        Assert.Equal(reset, port.Written.TakeLast(reset.Length));
    }

    [Fact]
    public void CheckFlash_reports_the_raw_CFI_response_on_failure()
    {
        using var session = Connect(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)));

        var x = Assert.Throws<FlashChipNotFoundException>(() => session.CheckFlash());

        Assert.Contains("instead of QRY", x.Message);
    }
}
