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
    public void version_prints_client_version_without_touching_a_device()
    {
        int exit = AppWithoutDevice().Run(new[] { "--version" });

        Assert.Equal(0, exit);
        Assert.Equal("flashkit-md " + VersionInfo.ClientVersion + Environment.NewLine, stdout.ToString());
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void help_prints_usage_to_stdout_and_exits_zero(string flag)
    {
        int exit = AppWithoutDevice().Run(new[] { flag });

        Assert.Equal(0, exit);
        Assert.Contains("usage: flashkit-md", stdout.ToString());
        Assert.Equal("", stderr.ToString());
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
        Assert.Contains("System   : Mega Drive / Genesis", outText);
        Assert.Contains("Region   : USA", outText);
        Assert.Contains("ROM size : 512K", outText);
        Assert.Contains("RAM size : 8K", outText);
    }

    [Fact]
    public void info_reports_32X_system()
    {
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000, "DOOM", system: "SEGA 32X"));
        int exit = App(fake).Run(new[] { "info" });

        Assert.Equal(0, exit);
        Assert.Contains("System   : Sega 32X", stdout.ToString());
    }

    [Fact]
    public void read_rom_default_filename_uses_32x_extension_for_32X_carts()
    {
        // Default (no file arg): the dump lands next to the CWD; run in the
        // temp dir so we can assert the chosen name without polluting.
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000, "DOOM", system: "SEGA 32X"));
        string prevCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(dir);
        try
        {
            int exit = App(fake).Run(new[] { "read-rom" });
            Assert.Equal(0, exit);
            Assert.Contains("Read ROM to DOOM (U).32x", stdout.ToString());
            Assert.True(File.Exists(Path.Combine(dir, "DOOM (U).32x")));
        }
        finally { Directory.SetCurrentDirectory(prevCwd); }
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
        Assert.Contains("CRC32: ", stdout.ToString());
        Assert.Contains("MD5: ", stdout.ToString());
        Assert.Contains("SHA-1: ", stdout.ToString());
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
        Assert.Contains("CRC32: " + RomHash.Crc32(data), outText); // flashed image is checksummed
        Assert.Contains("OK", outText);
        Assert.Equal(data, fake.Rom.Take(data.Length).ToArray());
    }

    [Fact]
    public void write_rom_patch_flashes_the_patched_image()
    {
        var fake = new FakeFlashKitDevice(new byte[0x400000]) { FlashWritable = true };
        var baseImg = new byte[0x20000];
        for (int i = 0; i < baseImg.Length; i++) baseImg[i] = (byte)(i * 7 + 1);
        var patched = (byte[])baseImg.Clone();
        patched[0x100] ^= 0xFF;
        patched[0x1FFFF] ^= 0xAA;
        string imgFile = TempFile("base.bin");
        string patchFile = TempFile("hack.ips");
        File.WriteAllBytes(imgFile, baseImg);
        File.WriteAllBytes(patchFile, IpsPatch.Create(baseImg, patched));

        int exit = App(fake).Run(new[] { "write-rom", imgFile, "--patch", patchFile });

        Assert.Equal(0, exit);
        Assert.Contains("Applied IPS patch", stdout.ToString());
        Assert.Equal(patched, fake.Rom.Take(patched.Length).ToArray());
    }

    [Fact]
    public void read_rom_apply_patch_saves_the_patched_dump()
    {
        var rom = TestRoms.MakeRom(0x80000);
        var fake = new FakeFlashKitDevice(rom);
        var patched = (byte[])rom.Clone();
        patched[5] ^= 0xFF;
        string outFile = TempFile("dump.bin");
        string patchFile = TempFile("fix.ips");
        File.WriteAllBytes(patchFile, IpsPatch.Create(rom, patched));

        int exit = App(fake).Run(new[] { "read-rom", outFile, "--apply-patch", patchFile });

        Assert.Equal(0, exit);
        Assert.Contains("Applied IPS patch", stdout.ToString());
        Assert.Equal(patched, File.ReadAllBytes(outFile));
    }

    [Fact]
    public void read_rom_create_patch_writes_a_diff_against_the_base()
    {
        var cartRom = TestRoms.MakeRom(0x80000);   // what the cart holds
        var fake = new FakeFlashKitDevice(cartRom);
        var baseRom = (byte[])cartRom.Clone();
        baseRom[0x200] ^= 0xFF;                     // base differs from the cart
        string baseFile = TempFile("base.bin");
        string outIps = TempFile("out.ips");
        File.WriteAllBytes(baseFile, baseRom);

        int exit = App(fake).Run(new[] { "read-rom", outIps, "--create-patch", baseFile });

        Assert.Equal(0, exit);
        Assert.Contains("Wrote IPS patch", stdout.ToString());
        // The patch turns the base back into the cart dump.
        Assert.Equal(cartRom, IpsPatch.Apply(baseRom, File.ReadAllBytes(outIps)));
    }

    [Theory]
    [InlineData(new[] { "write-rom", "f", "--apply-patch", "p" }, "only apply to read-rom")]
    [InlineData(new[] { "read-rom", "f", "--patch", "p" }, "only applies to write-rom")]
    [InlineData(new[] { "read-rom", "f", "--apply-patch", "a", "--create-patch", "b" }, "mutually exclusive")]
    [InlineData(new[] { "read-rom", "--apply-patch" }, "--apply-patch requires a value")]
    public void patch_flag_misuse_is_rejected(string[] args, string expected)
    {
        int exit = AppWithoutDevice().Run(args);

        Assert.Equal(2, exit);
        Assert.Contains(expected, stderr.ToString());
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

    [Fact]
    public void info_shows_header_size_only_when_it_differs_from_probe()
    {
        // Probed 512K, header claims 256K — the flash-cart situation where
        // stale data above the image confuses mirror probing.
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000, headerRomSize: 0x40000));
        int exit = App(fake).Run(new[] { "info" });

        Assert.Equal(0, exit);
        Assert.Contains("ROM size : 512K", stdout.ToString());
        Assert.Contains("Header ROM size : 256K", stdout.ToString());
    }

    [Fact]
    public void info_omits_header_size_when_it_matches_probe()
    {
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000));
        int exit = App(fake).Run(new[] { "info" });

        Assert.Equal(0, exit);
        Assert.DoesNotContain("Header ROM size", stdout.ToString());
    }

    [Fact]
    public void read_rom_trust_header_dumps_the_header_extent()
    {
        var rom = TestRoms.MakeRom(0x80000, headerRomSize: 0x40000);
        var fake = new FakeFlashKitDevice(rom);
        string file = TempFile("dump.bin");

        int exit = App(fake).Run(new[] { "read-rom", "--trust-header", file });

        Assert.Equal(0, exit);
        Assert.Equal(rom.Take(0x40000).ToArray(), File.ReadAllBytes(file));
        Assert.Contains("ROM size : 256K (from header)", stdout.ToString());
    }

    [Fact]
    public void read_rom_trust_header_falls_back_when_header_is_blank()
    {
        var blank = new byte[0x400000];
        Array.Fill(blank, (byte)0xFF);
        // Give probing something to measure: make the first 512K distinct.
        var withRom = TestRoms.MakeRom(0x80000);
        Array.Copy(withRom, blank, withRom.Length);
        blank[0x1A4] = blank[0x1A5] = blank[0x1A6] = blank[0x1A7] = 0xFF;
        var fake = new FakeFlashKitDevice(blank);
        string file = TempFile("dump.bin");

        int exit = App(fake).Run(new[] { "read-rom", "--trust-header", file });

        Assert.Equal(0, exit);
        Assert.Contains("Header declares no plausible ROM size", stdout.ToString());
    }

    [Fact]
    public void trust_header_is_rejected_outside_read_rom()
    {
        int exit = AppWithoutDevice().Run(new[] { "info", "--trust-header" });

        Assert.Equal(2, exit);
        Assert.Contains("--trust-header only applies to read-rom", stderr.ToString());
    }

    [Fact]
    public void write_rom_refuses_a_cart_with_no_flash_chip()
    {
        // A real game cart's mask ROM ignores the CFI query, so write-rom
        // must fail fast instead of "erasing" and failing at verify.
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000));
        string file = TempFile("game.bin");
        File.WriteAllBytes(file, new byte[0x10000]);

        int exit = App(fake).Run(new[] { "write-rom", file });

        Assert.Equal(1, exit);
        Assert.Contains("No flash chip detected", stderr.ToString());
        Assert.Contains("--no-flash-check", stderr.ToString());
    }

    [Fact]
    public void write_rom_no_flash_check_skips_the_cfi_probe()
    {
        // Same non-flash cart, but with the check skipped the flow reaches
        // the verify stage (which then fails, as nothing was programmed).
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000));
        string file = TempFile("game.bin");
        File.WriteAllBytes(file, new byte[0x10000]);

        int exit = App(fake).Run(new[] { "write-rom", "--no-flash-check", file });

        Assert.Equal(1, exit);
        Assert.Contains("Verify error", stderr.ToString());
    }

    [Fact]
    public void no_flash_check_is_rejected_outside_write_commands()
    {
        int exit = AppWithoutDevice().Run(new[] { "read-rom", "--no-flash-check", TempFile("x.bin") });

        Assert.Equal(2, exit);
        Assert.Contains("--no-flash-check only applies to write-rom and bake-save", stderr.ToString());
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
    public void bake_save_programs_save_window_and_verifies()
    {
        var fake = new FakeFlashKitDevice(new byte[0x400000]) { FlashWritable = true };
        FlashFilled(fake, 0x400000, 0xAB); // stale data everywhere, incl. save window
        var srm = new byte[16384];
        for (int i = 0; i < srm.Length; i += 2) { srm[i] = 0xFF; srm[i + 1] = (byte)(i * 7 + 3); }
        string file = TempFile("save.srm");
        File.WriteAllBytes(file, srm);

        int exit = App(fake).Run(new[] { "bake-save", file });

        Assert.Equal(0, exit);
        Assert.Contains("read-only snapshot", stdout.ToString());
        Assert.Contains("OK", stdout.ToString());
        Assert.Equal(srm, fake.Rom.Skip(0x200000).Take(srm.Length).ToArray());
        // rest of the erased 64K block is clean, the ROM area untouched
        Assert.Equal(0xFF, fake.Rom[0x200000 + srm.Length]);
        Assert.Equal(0xAB, fake.Rom[0x1FFFFF]);
        Assert.Equal(0xAB, fake.Rom[0x210000]);
    }

    [Fact]
    public void bake_save_requires_a_file()
    {
        int exit = AppWithoutDevice().Run(new[] { "bake-save" });

        Assert.Equal(2, exit);
        Assert.Contains("bake-save requires a file", stderr.ToString());
    }

    [Fact]
    public void bake_save_rejects_odd_sized_images()
    {
        var fake = new FakeFlashKitDevice(new byte[0x400000]) { FlashWritable = true };
        string file = TempFile("bad.srm");
        File.WriteAllBytes(file, new byte[1023]);

        int exit = App(fake).Run(new[] { "bake-save", file });

        Assert.Equal(1, exit);
        Assert.Contains("even length", stderr.ToString());
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
