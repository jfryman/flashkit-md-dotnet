namespace FlashKit.Core.Tests;

/// <summary>
/// Tests the front-end-facing library API (the surface a CLI/TUI/GUI builds
/// on) against the fake firmware: workflows, progress reporting, and error
/// contracts — no console or file I/O involved.
/// </summary>
public class FlashKitSessionTests
{
    static FlashKitSession Connect(FakeFlashKitDevice fake) =>
        FlashKitSession.Connect(new DeviceConnector(
            () => new[] { "/dev/ttyUSB0" }, _ => fake, HostOs.Linux));

    [Fact]
    public void GetInfo_returns_name_and_sizes()
    {
        using var session = Connect(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000), sramBytes: 8192));

        var info = session.GetInfo();

        Assert.Equal("TEST GAME (U)", info.RomName);
        Assert.Equal(0x80000, info.RomBytes);
        Assert.Equal(8192, info.RamBytes);
        Assert.Equal(0x80000, info.HeaderRomBytes);
        Assert.True(info.CartDetected);
        Assert.False(info.Is32X);
        Assert.Equal("Mega Drive / Genesis", info.SystemName);
    }

    [Theory]
    [InlineData("SEGA 32X      JU")] // real DOOM cart: region packed into the tail
    [InlineData("SEGA 32X        ")] // real Kolibri cart: pure spaces
    public void GetInfo_detects_32X_from_the_system_field(string system)
    {
        using var session = Connect(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000, system: system)));

        var info = session.GetInfo();

        Assert.True(info.Is32X);
        Assert.Equal("Sega 32X", info.SystemName);
    }

    [Fact]
    public void SuggestedRomFileName_uses_32x_extension_for_32X_carts()
    {
        using var md = Connect(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000, "SONIC")));
        Assert.Equal("SONIC (U).bin", md.SuggestedRomFileName());

        using var x32 = Connect(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000, "DOOM", system: "SEGA 32X")));
        Assert.Equal("DOOM (U).32x", x32.SuggestedRomFileName());
    }

    [Fact]
    public void GetInfo_flags_missing_cartridge()
    {
        // An empty slot floats the bus: every read is 0xFFFF, giving the
        // "Unknown (X) / 0K" signature (docs/hardware-validation.md). The
        // programmer itself still answers, so this is cart-absent, not
        // device-absent.
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000), sramBytes: 8192) { CartInserted = false };
        using var session = Connect(fake);

        var info = session.GetInfo();

        Assert.False(info.CartDetected);
        Assert.StartsWith("Unknown", info.RomName);
        Assert.Equal(0, info.RomBytes);
        Assert.Equal(0, info.RamBytes);
        Assert.Null(info.HeaderRomBytes);
    }

    [Fact]
    public void GetRomName_collapses_header_space_padding()
    {
        // Real headers pad the name field internally (Sonic 3 is
        // "SONIC THE               HEDGEHOG 3"); the suggested-filename
        // name must collapse the runs.
        var rom = TestRoms.MakeRom(0x80000, "SONIC THE               HEDGEHOG 3");
        using var session = Connect(new FakeFlashKitDevice(rom));

        Assert.Equal("SONIC THE HEDGEHOG 3 (U)", session.GetRomName());
    }

    [Fact]
    public void GetInfo_reports_null_header_size_for_blank_header()
    {
        // All-0xFF flash: header end address wraps past the chip. All-zero
        // flash: end address 0 gives an implausible 1-byte size.
        var blank = new byte[0x400000];
        Array.Fill(blank, (byte)0xFF);
        using (var session = Connect(new FakeFlashKitDevice(blank)))
            Assert.Null(session.GetInfo().HeaderRomBytes);
        using (var session = Connect(new FakeFlashKitDevice(new byte[0x400000])))
            Assert.Null(session.GetInfo().HeaderRomBytes);
    }

    [Fact]
    public void ReadRom_size_override_wins_over_probing()
    {
        var rom = TestRoms.MakeRom(0x80000);
        using var session = Connect(new FakeFlashKitDevice(rom));

        byte[] dump = session.ReadRom(size: 0x40000);

        Assert.Equal(rom.Take(0x40000).ToArray(), dump);
    }

    [Fact]
    public void ReadRom_rejects_bad_size_override()
    {
        using var session = Connect(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)));

        Assert.Throws<ArgumentException>(() => session.ReadRom(size: 0x40001));
        Assert.Throws<ArgumentException>(() => session.ReadRom(size: 0));
        Assert.Throws<ArgumentException>(() => session.ReadRom(size: 0x400002));
    }

    [Fact]
    public void ReadRom_returns_image_and_reports_monotonic_progress()
    {
        var rom = TestRoms.MakeRom(0x80000);
        using var session = Connect(new FakeFlashKitDevice(rom));
        var reports = new List<OperationProgress>();

        byte[] dump = session.ReadRom(reports.Add);

        Assert.Equal(rom, dump);
        Assert.All(reports, p => Assert.Equal(OperationPhase.Read, p.Phase));
        Assert.All(reports, p => Assert.Equal(0x80000, p.Total));
        Assert.Equal(0, reports[0].Done);
        Assert.Equal(0x80000, reports[^1].Done);
        for (int i = 1; i < reports.Count; i++)
            Assert.True(reports[i].Done > reports[i - 1].Done);
    }

    [Fact]
    public void WriteRom_programs_and_verifies()
    {
        var fake = new FakeFlashKitDevice(new byte[0x400000]) { FlashWritable = true };
        using var session = Connect(fake);
        var data = new byte[0x20000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 31 + 7);
        var phases = new List<OperationPhase>();

        session.WriteRom(data, progress: p => phases.Add(p.Phase));

        Assert.Equal(data, fake.Rom.Take(data.Length).ToArray());
        Assert.Equal(
            new[] { OperationPhase.Erase, OperationPhase.Write, OperationPhase.Verify },
            phases.Distinct().ToArray()); // phases in order, no interleaving
    }

    [Fact]
    public void WriteRom_full_erase_wipes_whole_chip()
    {
        var fake = new FakeFlashKitDevice(new byte[0x400000]) { FlashWritable = true };
        using (var setup = Connect(fake))
        {
            var old = new byte[0x400000];
            Array.Fill(old, (byte)0xAB);
            setup.WriteRom(old);
        }
        using var session = Connect(fake);

        session.WriteRom(new byte[0x20000], fullErase: true);

        Assert.Equal(0xFF, fake.Rom[0x200000]);
        Assert.Equal(0xFF, fake.Rom[0x3FFFFF]);
    }

    [Fact]
    public void WriteRom_throws_VerifyException_when_flash_does_not_program()
    {
        // FlashWritable=false: erases and programs are silently ignored,
        // so read-back cannot match and verification must fail. The CFI
        // check is skipped to reach the verify path.
        var fake = new FakeFlashKitDevice(new byte[0x400000]);
        using var session = Connect(fake);
        var data = new byte[0x10000];
        Array.Fill(data, (byte)0x42);

        var x = Assert.Throws<VerifyException>(() => session.WriteRom(data, skipFlashCheck: true));

        Assert.Equal(0, x.Offset);
        Assert.Equal("Verify error at 0", x.Message);
    }

    [Fact]
    public void WriteRom_refuses_cart_without_flash_before_touching_it()
    {
        // A mask ROM cart ignores the CFI query, so the check must throw
        // before any erase command is issued.
        var rom = TestRoms.MakeRom(0x80000);
        var pristine = rom.ToArray();
        var fake = new FakeFlashKitDevice(rom);
        using var session = Connect(fake);

        Assert.Throws<FlashChipNotFoundException>(() => session.WriteRom(new byte[0x10000]));
        Assert.Equal(pristine, fake.Rom);
    }

    [Fact]
    public void CheckFlash_passes_on_flash_cart_and_leaves_it_readable()
    {
        var rom = TestRoms.MakeRom(0x80000);
        var fake = new FakeFlashKitDevice(rom.ToArray()) { FlashWritable = true };
        using var session = Connect(fake);

        session.CheckFlash();

        Assert.Equal(rom, session.ReadRom()); // reset returned chip to array mode
    }

    [Fact]
    public void ReadRam_requires_save_ram()
    {
        using var session = Connect(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)));

        var x = Assert.Throws<Exception>(() => session.ReadRam());

        Assert.Equal("RAM is not detected", x.Message);
    }

    [Fact]
    public void WriteRam_roundtrips_through_ReadRam()
    {
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x100000), sramBytes: 8192);
        using var session = Connect(fake);
        var data = new byte[2 * 8192];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 11 + 5);

        int words = session.WriteRam(data);
        byte[] dump = session.ReadRam();

        Assert.Equal(8192, words);
        Assert.Equal(data.Length, dump.Length);
        for (int i = 1; i < data.Length; i += 2)
            Assert.Equal(data[i], dump[i]); // odd bytes are the real 8-bit RAM
    }

    [Fact]
    public void BakeSave_programs_save_window_only()
    {
        var fake = new FakeFlashKitDevice(new byte[0x400000]) { FlashWritable = true };
        using (var setup = Connect(fake))
        {
            var old = new byte[0x400000];
            Array.Fill(old, (byte)0xAB);
            setup.WriteRom(old);
        }
        var srm = new byte[16384];
        for (int i = 0; i < srm.Length; i += 2) { srm[i] = 0xFF; srm[i + 1] = (byte)(i * 7 + 3); }
        using var session = Connect(fake);

        session.BakeSave(srm);

        Assert.Equal(srm, fake.Rom.Skip(0x200000).Take(srm.Length).ToArray());
        Assert.Equal(0xFF, fake.Rom[0x200000 + srm.Length]); // rest of erased block
        Assert.Equal(0xAB, fake.Rom[0x1FFFFF]);              // ROM area untouched
        Assert.Equal(0xAB, fake.Rom[0x210000]);              // beyond erased block untouched
    }

    [Theory]
    [InlineData(0, "save image is empty")]
    [InlineData(1023, "even length")]
    [InlineData(0x100002, "too large")]
    public void BakeSave_validates_image(int length, string expectedError)
    {
        using var session = Connect(new FakeFlashKitDevice(new byte[0x400000]) { FlashWritable = true });

        var x = Assert.Throws<ArgumentException>(() => session.BakeSave(new byte[length]));

        Assert.Contains(expectedError, x.Message);
    }
}
