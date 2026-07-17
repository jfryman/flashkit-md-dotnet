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
        // so read-back cannot match and verification must fail.
        var fake = new FakeFlashKitDevice(new byte[0x400000]);
        using var session = Connect(fake);
        var data = new byte[0x10000];
        Array.Fill(data, (byte)0x42);

        var x = Assert.Throws<VerifyException>(() => session.WriteRom(data));

        Assert.Equal(0, x.Offset);
        Assert.Equal("Verify error at 0", x.Message);
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
