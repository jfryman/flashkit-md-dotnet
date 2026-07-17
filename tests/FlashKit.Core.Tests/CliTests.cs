using flashkit_md;

namespace FlashKit.Core.Tests;

/// <summary>
/// In-process end-to-end tests: each CLI command runs against the fake
/// firmware through a DeviceConnector, with temp files for ROM/RAM images.
/// </summary>
public class CliTests : IDisposable
{
    readonly string dir;
    readonly StringWriter stdout = new();
    readonly StringWriter stderr = new();

    public CliTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "flashkit-cli-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    public void Dispose() => Directory.Delete(dir, recursive: true);

    string TempFile(string name) => Path.Combine(dir, name);

    CliApp App(FakeFlashKitDevice fake)
    {
        var connector = new DeviceConnector(
            () => new[] { "/dev/ttyUSB0" }, _ => fake, HostOs.Linux);
        return new CliApp(connector, stdout, stderr);
    }

    CliApp AppWithoutDevice()
    {
        var connector = new DeviceConnector(
            () => Array.Empty<string>(),
            _ => throw new IOException("must not open"),
            HostOs.Linux);
        return new CliApp(connector, stdout, stderr);
    }

    [Fact]
    public void info_prints_cart_details()
    {
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000), sramBytes: 8192);
        int exit = App(fake).Run(new[] { "info" });

        Assert.Equal(0, exit);
        string outText = stdout.ToString();
        Assert.Contains("Connected to: FAKE", outText);
        Assert.Contains("ROM name : TEST GAME (U)", outText);
        Assert.Contains("ROM size : 512K", outText);
        Assert.Contains("RAM size : 8K", outText);
    }

    [Fact]
    public void read_rom_dumps_cart_contents_with_md5()
    {
        var rom = TestRoms.MakeRom(0x80000);
        var fake = new FakeFlashKitDevice(rom);
        string file = TempFile("dump.bin");

        int exit = App(fake).Run(new[] { "read-rom", file });

        Assert.Equal(0, exit);
        Assert.Equal(rom, File.ReadAllBytes(file));
        Assert.Contains("MD5: ", stdout.ToString());
        Assert.Contains("OK", stdout.ToString());
    }

    [Fact]
    public void read_rom_truncates_a_larger_existing_file()
    {
        // The original client used File.OpenWrite, which left stale bytes
        // when a smaller dump overwrote a larger file.
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000));
        string file = TempFile("dump.bin");
        File.WriteAllBytes(file, new byte[0x100000]);

        int exit = App(fake).Run(new[] { "read-rom", file });

        Assert.Equal(0, exit);
        Assert.Equal(0x80000, new FileInfo(file).Length);
    }

    [Fact]
    public void write_rom_erases_programs_and_verifies()
    {
        var fake = new FakeFlashKitDevice(new byte[0x400000]) { FlashWritable = true };
        var data = new byte[0x20000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 31 + 7);
        string file = TempFile("game.bin");
        File.WriteAllBytes(file, data);

        int exit = App(fake).Run(new[] { "write-rom", file });

        Assert.Equal(0, exit);
        string outText = stdout.ToString();
        Assert.Contains("Flash erase...", outText);
        Assert.Contains("Flash write...", outText);
        Assert.Contains("Flash verify...", outText);
        Assert.Contains("OK", outText);
        Assert.Equal(data, fake.Rom.Take(data.Length).ToArray());
    }

    [Fact]
    public void write_rom_pads_odd_sized_images_to_64K()
    {
        var fake = new FakeFlashKitDevice(new byte[0x400000]) { FlashWritable = true };
        var data = new byte[10000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i + 1);
        string file = TempFile("small.bin");
        File.WriteAllBytes(file, data);

        int exit = App(fake).Run(new[] { "write-rom", file });

        Assert.Equal(0, exit);
        Assert.Equal(data, fake.Rom.Take(data.Length).ToArray());
        // padding written as zeros up to the 64K boundary, like the original
        Assert.All(fake.Rom.Skip(data.Length).Take(65536 - data.Length), b => Assert.Equal(0, b));
    }

    [Fact]
    public void write_rom_without_full_erase_leaves_stale_data_above_image()
    {
        // The ghost-save scenario: a smaller game flashed over a larger one
        // leaves the old data at 0x200000+, where saves-capable games look
        // for SRAM. This is the original client's behavior, kept as default.
        var fake = new FakeFlashKitDevice(new byte[0x400000]) { FlashWritable = true };
        FlashFilled(fake, 0x400000, 0xAB);
        string file = TempFile("game.bin");
        File.WriteAllBytes(file, new byte[0x20000]);

        int exit = App(fake).Run(new[] { "write-rom", file });

        Assert.Equal(0, exit);
        Assert.Equal(0xAB, fake.Rom[0x200000]);
    }

    [Fact]
    public void write_rom_full_erase_wipes_the_whole_chip()
    {
        var fake = new FakeFlashKitDevice(new byte[0x400000]) { FlashWritable = true };
        FlashFilled(fake, 0x400000, 0xAB);
        var data = new byte[0x20000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 31 + 7);
        string file = TempFile("game.bin");
        File.WriteAllBytes(file, data);

        int exit = App(fake).Run(new[] { "write-rom", "--full-erase", file });

        Assert.Equal(0, exit);
        Assert.Contains("Flash erase (full chip)...", stdout.ToString());
        Assert.Equal(data, fake.Rom.Take(data.Length).ToArray());
        Assert.Equal(0xFF, fake.Rom[0x200000]);
        Assert.Equal(0xFF, fake.Rom[0x3FFFFF]);
    }

    [Fact]
    public void full_erase_is_rejected_outside_write_rom()
    {
        int exit = AppWithoutDevice().Run(new[] { "read-rom", "--full-erase", TempFile("x.bin") });

        Assert.Equal(2, exit);
        Assert.Contains("--full-erase only applies to write-rom", stderr.ToString());
    }

    /// <summary>Programs a fill pattern into fake flash via the real command path.</summary>
    static void FlashFilled(FakeFlashKitDevice fake, int len, byte fill)
    {
        var device = new Device(fake);
        var pattern = new byte[len];
        Array.Fill(pattern, fill);
        device.flashResetByPass();
        for (int i = 0; i < len; i += 65536) device.flashErase(i);
        device.flashUnlockBypass();
        device.setAddr(0);
        for (int i = 0; i < len; i += 4096) device.flashWrite(pattern, i, 4096);
        device.flashResetByPass();
    }

    [Fact]
    public void read_ram_dumps_save_ram()
    {
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x100000), sramBytes: 8192);
        for (int i = 0; i < 8192; i++) fake.Sram[i] = (byte)(i * 7 + 3);
        string file = TempFile("save.srm");

        int exit = App(fake).Run(new[] { "read-ram", file });

        Assert.Equal(0, exit);
        byte[] dump = File.ReadAllBytes(file);
        Assert.Equal(2 * 8192, dump.Length);
        for (int i = 0; i < 8192; i++)
            Assert.Equal((byte)(i * 7 + 3), dump[i * 2 + 1]);
    }

    [Fact]
    public void write_ram_stores_and_verifies_save_ram()
    {
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x100000), sramBytes: 8192);
        var data = new byte[2 * 8192];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 11 + 5);
        string file = TempFile("save.srm");
        File.WriteAllBytes(file, data);

        int exit = App(fake).Run(new[] { "write-ram", file });

        Assert.Equal(0, exit);
        Assert.Contains("8192 words sent", stdout.ToString());
        Assert.Contains("OK", stdout.ToString());
        for (int i = 0; i < 8192; i++)
            Assert.Equal(data[i * 2 + 1], fake.Sram[i]);
    }

    [Fact]
    public void ram_commands_fail_cleanly_without_save_ram()
    {
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000));
        int exit = App(fake).Run(new[] { "read-ram", TempFile("save.srm") });

        Assert.Equal(1, exit);
        Assert.Contains("RAM is not detected", stderr.ToString());
    }

    [Fact]
    public void missing_device_reports_error_and_exits_nonzero()
    {
        int exit = AppWithoutDevice().Run(new[] { "info" });

        Assert.Equal(1, exit);
        Assert.Contains("Device is not detected", stderr.ToString());
    }

    [Fact]
    public void no_command_prints_usage()
    {
        int exit = AppWithoutDevice().Run(Array.Empty<string>());

        Assert.Equal(2, exit);
        Assert.Contains("usage:", stderr.ToString());
    }

    [Fact]
    public void write_commands_require_a_file_argument()
    {
        int exit = AppWithoutDevice().Run(new[] { "write-rom" });

        Assert.Equal(2, exit);
        Assert.Contains("write-rom requires a file", stderr.ToString());
    }
}
