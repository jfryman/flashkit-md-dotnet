namespace FlashKit.Core.Tests;

/// <summary>
/// Runs the ported Cart/Device logic against the fake firmware + synthetic
/// cartridges: header parsing, ROM size probing, SRAM detect/size/dump.
/// </summary>
public class CartBehaviorTests
{
    static byte[] MakeRom(int size, string name = "TEST GAME", string region = "U")
        => TestRoms.MakeRom(size, name, region);

    static Cart CartFor(FakeFlashKitDevice fake) => new(new Device(fake));

    [Fact]
    public void getRomName_reads_header_name_and_region()
    {
        var fake = new FakeFlashKitDevice(MakeRom(0x80000, "SONIC THE HEDGEHOG"));
        Assert.Equal("SONIC THE HEDGEHOG (U)", CartFor(fake).getRomName());
    }

    [Fact]
    public void getRomName_falls_back_to_overseas_name_offset()
    {
        var rom = MakeRom(0x80000, "");
        rom[0x120] = 1; // invalid char at domestic name offset
        const string name = "MEGA GAME";
        for (int i = 0; i < name.Length; i++) rom[0x150 + i] = (byte)name[i];
        Assert.Equal("MEGA GAME (U)", CartFor(new FakeFlashKitDevice(rom)).getRomName());
    }

    [Fact]
    public void getRomName_reports_unknown_for_garbage_header()
    {
        var rom = MakeRom(0x80000, "");
        rom[0x120] = 1;
        rom[0x150] = 1;
        Assert.Equal("Unknown (U)", CartFor(new FakeFlashKitDevice(rom)).getRomName());
    }

    [Theory]
    [InlineData("J", "(J)")]
    [InlineData("E", "(E)")]
    [InlineData("U", "(U)")]
    [InlineData("F", "(W)")]
    [InlineData("JUE", "(W)")] // multi-region reported as world by the original
    public void getRomName_maps_region_codes(string region, string expected)
    {
        var fake = new FakeFlashKitDevice(MakeRom(0x80000, "TEST GAME", region));
        Assert.EndsWith(expected, CartFor(fake).getRomName());
    }

    [Theory]
    [InlineData(0x080000)] // 512 KB
    [InlineData(0x100000)] // 1 MB
    [InlineData(0x400000)] // 4 MB
    public void getRomSize_detects_mirrored_rom_size(int size)
    {
        var fake = new FakeFlashKitDevice(MakeRom(size));
        Assert.Equal(size, CartFor(fake).getRomSize());
    }

    [Fact]
    public void getRomSize_detects_2M_rom_with_sram()
    {
        var fake = new FakeFlashKitDevice(MakeRom(0x200000), sramBytes: 8192);
        Assert.Equal(0x200000, CartFor(fake).getRomSize());
    }

    [Fact]
    public void getRamSize_returns_zero_without_sram()
    {
        var fake = new FakeFlashKitDevice(MakeRom(0x80000));
        Assert.Equal(0, CartFor(fake).getRamSize());
    }

    [Theory]
    [InlineData(8192)]
    [InlineData(32768)]
    public void getRamSize_detects_sram_bytes(int sramBytes)
    {
        var fake = new FakeFlashKitDevice(MakeRom(0x100000), sramBytes);
        Assert.Equal(sramBytes, CartFor(fake).getRamSize());
    }

    [Fact]
    public void getRam_dumps_sram_on_odd_bytes()
    {
        var fake = new FakeFlashKitDevice(MakeRom(0x100000), sramBytes: 8192);
        for (int i = 0; i < 8192; i++) fake.Sram[i] = (byte)(i * 7 + 3);

        byte[] dump = CartFor(fake).getRam();

        Assert.Equal(2 * 8192, dump.Length);
        for (int i = 0; i < 8192; i++)
        {
            Assert.Equal(0xFF, dump[i * 2]);            // open bus on even bytes
            Assert.Equal((byte)(i * 7 + 3), dump[i * 2 + 1]);
        }
    }

    [Fact]
    public void write_stores_odd_bytes_into_sram()
    {
        // Mirrors the original write-RAM flow: enable SRAM, block-write words.
        var fake = new FakeFlashKitDevice(MakeRom(0x100000), sramBytes: 8192);
        var device = new Device(fake);

        var data = new byte[2 * 8192];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 11 + 5);

        device.writeWord(0xA13000, 0xffff);
        device.setAddr(0x200000);
        device.write(data, 0, data.Length);

        for (int i = 0; i < 8192; i++)
            Assert.Equal(data[i * 2 + 1], fake.Sram[i]);
    }
}
