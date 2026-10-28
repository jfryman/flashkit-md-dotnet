namespace FlashKit.Core.Tests;

/// <summary>
/// Flash writes and ROM sizing beyond the original 4 MB limit: the chip size
/// comes from CFI (MegaWiFi Rev.C: 8 MiB flat, docs/hardware-validation.md),
/// full erase covers exactly that size, and read-rom only looks above 4 MB
/// when the header asks for it.
/// </summary>
public class EightMiBFlashTests
{
    const int MiB = 0x100000;

    static FlashKitSession Connect(FakeFlashKitDevice fake) =>
        FlashKitSession.Connect(new DeviceConnector(
            () => new[] { "/dev/ttyUSB0" }, _ => fake, HostOs.Linux));

    static byte[] Filled(int size, byte value)
    {
        var b = new byte[size];
        Array.Fill(b, value);
        return b;
    }

    [Fact]
    public void WriteRom_programs_and_reads_back_a_full_8_MiB_image()
    {
        var image = TestRoms.MakeRom(8 * MiB);
        var fake = new FakeFlashKitDevice(Filled(8 * MiB, 0xFF)) { FlashWritable = true };
        using var session = Connect(fake);

        session.WriteRom(image);

        Assert.Equal(image, fake.Rom);
        Assert.Equal(8 * MiB, session.GetInfo().RomBytes);
        Assert.Equal(image, session.ReadRom());
    }

    [Fact]
    public void WriteRom_refuses_an_image_larger_than_the_chip_before_erasing()
    {
        var fake = new FakeFlashKitDevice(Filled(2 * MiB, 0xAB)) { FlashWritable = true };
        using var session = Connect(fake);

        var x = Assert.Throws<ArgumentException>(() => session.WriteRom(new byte[2 * MiB + 2]));

        Assert.Contains("flash chip holds 2097152 bytes", x.Message);
        Assert.Equal(0, fake.FlashModifyCount);
    }

    [Fact]
    public void WriteRom_without_the_flash_check_assumes_a_4_MB_chip()
    {
        var fake = new FakeFlashKitDevice(Filled(8 * MiB, 0xFF)) { FlashWritable = true };
        using var session = Connect(fake);

        Assert.Throws<ArgumentException>(() => session.WriteRom(new byte[4 * MiB + 2], skipFlashCheck: true));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void full_erase_covers_exactly_the_CFI_chip_size(int chipMiB)
    {
        var fake = new FakeFlashKitDevice(Filled(chipMiB * MiB, 0xAB)) { FlashWritable = true };
        using var session = Connect(fake);
        long eraseTotal = 0;

        session.WriteRom(new byte[0x10000], fullErase: true,
            progress: p => { if (p.Phase == OperationPhase.Erase) eraseTotal = p.Total; });

        Assert.Equal(chipMiB * MiB, eraseTotal);
        Assert.All(fake.Rom.Skip(0x10000), b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public void ReadRom_stays_at_4_MB_when_the_header_claims_more_but_the_upper_half_mirrors()
    {
        // A mapper cart (SSF2's header declares 5 MB) is not wired for A22:
        // 0x400000+ mirrors the bottom half.
        var rom = TestRoms.MakeRom(4 * MiB, headerRomSize: 5 * MiB);
        using var session = Connect(new FakeFlashKitDevice(rom));

        Assert.Equal(4 * MiB, session.GetInfo().RomBytes);
        Assert.Equal(4 * MiB, session.ReadRom().Length);
    }

    [Fact]
    public void ReadRom_ignores_the_upper_half_when_the_header_declares_4_MB()
    {
        // 4 MB image on an 8 MiB chip: the blank upper half is not ROM.
        var flash = Filled(8 * MiB, 0xFF);
        TestRoms.MakeRom(4 * MiB).CopyTo(flash, 0);
        using var session = Connect(new FakeFlashKitDevice(flash));

        Assert.Equal(4 * MiB, session.GetInfo().RomBytes);
    }

    [Fact]
    public void ReadHeaderRomSize_accepts_up_to_8_MiB()
    {
        using var session = Connect(new FakeFlashKitDevice(TestRoms.MakeRom(8 * MiB)));

        Assert.Equal(8 * MiB, session.ReadHeaderRomSize());
    }
}
