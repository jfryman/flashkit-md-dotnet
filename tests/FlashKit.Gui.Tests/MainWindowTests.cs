using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FlashKit.Core;
using FlashKit.Core.Tests;
using FlashKit.Gui;

namespace FlashKit.Gui.Tests;

/// <summary>
/// Headless end-to-end tests for the adapter layer the GUI adds on top of
/// FlashKitSession: button wiring, busy state, console messages, and the
/// error path. Cart behavior itself is covered by FlashKit.Core.Tests; the
/// fake device stands in for the programmer, temp files for the pickers
/// (CliTests pattern).
/// </summary>
public class MainWindowTests : IDisposable
{
    readonly string dir;

    public MainWindowTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "flashkit-gui-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    public void Dispose() => Directory.Delete(dir, recursive: true);

    string TempFile(string name) => Path.Combine(dir, name);

    static MainWindow Window(FakeFlashKitDevice fake)
    {
        var window = new MainWindow(new DeviceConnector(
            () => new[] { "/dev/ttyUSB0" }, _ => fake, HostOs.Linux));
        window.Show();
        return window;
    }

    static string Console(MainWindow window) =>
        window.FindControl<TextBox>("ConsoleBox")!.Text ?? "";

    static Button Btn(MainWindow window, string name) =>
        window.FindControl<Button>(name)!;

    [AvaloniaFact]
    public async Task cart_info_prints_cart_details()
    {
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000), sramBytes: 8192);
        var window = Window(fake);

        await window.CartInfoAsync();

        string text = Console(window);
        Assert.Contains("Connected to: FAKE", text);
        Assert.Contains("ROM name : TEST GAME (U)", text);
        Assert.Contains("ROM size : 512K", text);
        Assert.Contains("RAM size : 8K", text);
    }

    [AvaloniaFact]
    public async Task read_rom_dumps_cart_to_chosen_file_with_md5()
    {
        var rom = TestRoms.MakeRom(0x80000);
        var window = Window(new FakeFlashKitDevice(rom));
        string file = TempFile("dump.bin");
        string? suggested = null;
        window.PickSavePath = (name, _) => { suggested = name; return Task.FromResult<string?>(file); };

        await window.ReadRomAsync();

        Assert.Equal("TEST GAME (U).bin", suggested);
        Assert.Equal(rom, File.ReadAllBytes(file));
        Assert.Contains("MD5: ", Console(window));
        Assert.Contains("OK", Console(window));
    }

    [AvaloniaFact]
    public async Task cancelled_picker_does_nothing()
    {
        var window = Window(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)));
        window.PickSavePath = (_, _) => Task.FromResult<string?>(null);

        await window.ReadRomAsync();

        Assert.Equal("", Console(window));
        Assert.True(Btn(window, "BtnReadRom").IsEnabled);
    }

    [AvaloniaFact]
    public async Task write_rom_programs_flash_and_reports_phases()
    {
        var image = TestRoms.MakeRom(0x20000, name: "NEW GAME");
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)) { FlashWritable = true };
        var window = Window(fake);
        string file = TempFile("image.bin");
        File.WriteAllBytes(file, image);
        window.PickOpenPath = _ => Task.FromResult<string?>(file);

        await window.WriteRomAsync();

        string text = Console(window);
        Assert.Contains("Flash erase...", text);
        Assert.Contains("Flash write...", text);
        Assert.Contains("Flash verify...", text);
        Assert.Contains("OK", text);
        Assert.Equal(image, fake.Rom.Take(image.Length));
    }

    [AvaloniaFact]
    public async Task write_rom_without_flash_chip_logs_error_and_reenables_buttons()
    {
        var window = Window(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)));
        string file = TempFile("image.bin");
        File.WriteAllBytes(file, TestRoms.MakeRom(0x20000));
        window.PickOpenPath = _ => Task.FromResult<string?>(file);

        await window.WriteRomAsync();

        Assert.Contains("No flash chip detected", Console(window));
        Assert.DoesNotContain("Flash erase...", Console(window));
        Assert.True(Btn(window, "BtnWriteRom").IsEnabled);
    }

    [AvaloniaFact]
    public async Task write_ram_stores_odd_bytes_and_reports_words_sent()
    {
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000), sramBytes: 8192);
        var window = Window(fake);
        var srm = new byte[16384];
        for (int i = 0; i < srm.Length; i++) srm[i] = (byte)(i * 7);
        string file = TempFile("save.srm");
        File.WriteAllBytes(file, srm);
        window.PickOpenPath = _ => Task.FromResult<string?>(file);

        await window.WriteRamAsync();

        string text = Console(window);
        Assert.Contains("Write RAM...", text);
        Assert.Contains("Verify...", text);
        Assert.Contains("8192 words sent", text);
        Assert.Contains("OK", text);
        for (int i = 0; i < 8192; i++) Assert.Equal(srm[i * 2 + 1], fake.Sram[i]);
    }

    [AvaloniaFact]
    public async Task buttons_are_disabled_while_an_operation_runs()
    {
        var window = Window(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)));

        var running = window.CartInfoAsync();
        foreach (var name in new[] { "BtnReadRom", "BtnWriteRom", "BtnReadRam", "BtnWriteRam", "BtnCartInfo" })
            Assert.False(Btn(window, name).IsEnabled);

        await running;
        foreach (var name in new[] { "BtnReadRom", "BtnWriteRom", "BtnReadRam", "BtnWriteRam", "BtnCartInfo" })
            Assert.True(Btn(window, name).IsEnabled);
    }
}
