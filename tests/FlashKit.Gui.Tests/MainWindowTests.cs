using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Avalonia.Media;
using Avalonia.VisualTree;
using FlashKit.Core;
using FlashKit.Core.Tests;
using FlashKit.Gui;
using FlashKit.Presentation;

namespace FlashKit.Gui.Tests;

/// <summary>
/// Headless end-to-end tests for the adapter layer the GUI adds on top of
/// FlashKitSession: status bar + info panel refresh, transaction logging,
/// busy state, and the error path. Cart behavior itself is covered by
/// FlashKit.Core.Tests; the fake device stands in for the programmer, temp
/// files for the pickers (CliTests pattern). Auto-refresh is driven by
/// calling RefreshAsync directly — the poll timer only exists in the
/// production constructor, so no timing here.
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

    [AvaloniaFact]
    public void left_column_scroll_area_stays_above_the_status_bar()
    {
        // The left column (five panels including IPS) lives in a ScrollViewer
        // confined to the content row, so its contents are clipped/scrolled
        // and can never overflow onto the bottom status bar — whatever the
        // platform's control sizes. A scroll bar appears if they don't fit.
        var window = Window(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)));
        window.UpdateLayout();

        var scroll = window.FindControl<Button>("BtnCreatePatch")!
            .GetVisualAncestors().OfType<ScrollViewer>().First();
        var statusText = window.FindControl<TextBlock>("DeviceStatusText")!;

        double scrollBottom = scroll.TranslatePoint(new Point(0, scroll.Bounds.Height), window)!.Value.Y;
        double statusTop = statusText.TranslatePoint(new Point(0, 0), window)!.Value.Y;

        Assert.True(scrollBottom <= statusTop,
            $"left column scroll area ({scrollBottom}) overlaps the status bar ({statusTop})");
    }

    [AvaloniaFact]
    public void status_indicators_are_accessible()
    {
        var window = Window(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)));

        // The colour-only dots are redundant with the adjacent text, so they
        // are hidden from assistive tech; the status text is a live region so
        // connect/disconnect is announced without the user going looking.
        foreach (var dot in new[] { "DeviceDot", "CartDot" })
            Assert.Equal(AccessibilityView.Raw,
                AutomationProperties.GetAccessibilityView(window.FindControl<Ellipse>(dot)!));
        foreach (var text in new[] { "DeviceStatusText", "CartStatusText" })
            Assert.Equal(AutomationLiveSetting.Polite,
                AutomationProperties.GetLiveSetting(window.FindControl<TextBlock>(text)!));

        // Primary actions carry an access-key mnemonic (the '_' before the key).
        Assert.Contains('_', Btn(window, "BtnReadRom").Content as string ?? "");
        Assert.Contains('_', Btn(window, "BtnWriteRom").Content as string ?? "");
    }

    static string Text(MainWindow window, string name) =>
        window.FindControl<TextBlock>(name)!.Text ?? "";

    static Button Btn(MainWindow window, string name) =>
        window.FindControl<Button>(name)!;

    [AvaloniaFact]
    public async Task refresh_shows_device_and_cart_info()
    {
        var window = Window(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000), sramBytes: 8192));

        await window.RefreshAsync();

        Assert.Equal("Programmer connected on FAKE", Text(window, "DeviceStatusText"));
        Assert.Equal("Cartridge inserted", Text(window, "CartStatusText"));
        Assert.Equal("TEST GAME (U)", Text(window, "InfoName"));
        Assert.Equal("Mega Drive / Genesis", Text(window, "InfoSystem"));
        Assert.Equal("USA", Text(window, "InfoRegion"));
        Assert.Equal("512K", Text(window, "InfoRomSize"));
        Assert.Equal("8K", Text(window, "InfoRamSize"));
        Assert.Equal("512K", Text(window, "InfoHeaderSize"));
    }

    [AvaloniaFact]
    public async Task refresh_tracks_cartridge_insertion()
    {
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)) { CartInserted = false };
        var window = Window(fake);

        await window.RefreshAsync();
        Assert.Equal("Programmer connected on FAKE", Text(window, "DeviceStatusText"));
        Assert.Equal("No cartridge", Text(window, "CartStatusText"));
        Assert.Equal("—", Text(window, "InfoName"));
        Assert.Equal("—", Text(window, "InfoRomSize"));

        fake.CartInserted = true;
        await window.RefreshAsync();
        Assert.Equal("Cartridge inserted", Text(window, "CartStatusText"));
        Assert.Equal("TEST GAME (U)", Text(window, "InfoName"));
        Assert.Equal("512K", Text(window, "InfoRomSize"));
    }

    [AvaloniaFact]
    public async Task session_is_held_across_polls_and_operations()
    {
        // Regression guard for the macOS FTDI wedge: closing the port after
        // a flash write can hang and leave the descriptor abandoned in this
        // long-lived process, so the GUI must reuse one session instead of
        // reconnecting per poll/operation.
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000));
        var window = Window(fake);
        window.PickSavePath = (_, _) => Task.FromResult<string?>(TempFile("dump.bin"));

        await window.RefreshAsync();
        await window.RefreshAsync();
        await window.ReadRomAsync();
        await window.RefreshAsync();

        Assert.Equal(1, fake.OpenCount);
    }

    [AvaloniaFact]
    public async Task unplugged_programmer_is_reported_and_reconnected()
    {
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000));
        var window = Window(fake);

        await window.RefreshAsync();
        Assert.Equal("Programmer connected on FAKE", Text(window, "DeviceStatusText"));

        fake.Disconnected = true;
        await window.RefreshAsync();
        Assert.Equal("No programmer detected", Text(window, "DeviceStatusText"));
        Assert.Equal("—", Text(window, "CartStatusText"));

        fake.Disconnected = false;
        await window.RefreshAsync();
        Assert.Equal("Programmer connected on FAKE", Text(window, "DeviceStatusText"));
        Assert.Equal(2, fake.OpenCount);
    }

    [AvaloniaFact]
    public async Task refresh_reports_missing_programmer()
    {
        var window = new MainWindow(new DeviceConnector(
            () => Array.Empty<string>(),
            _ => throw new InvalidOperationException("no ports to open"),
            HostOs.Linux));
        window.Show();

        await window.RefreshAsync();

        Assert.Equal("No programmer detected", Text(window, "DeviceStatusText"));
        Assert.Equal("—", Text(window, "CartStatusText"));
        Assert.Equal("—", Text(window, "InfoName"));
    }

    [AvaloniaFact]
    public async Task permission_denied_is_surfaced_not_masked_as_missing()
    {
        // Regression: a denied serial port used to render as the same
        // "No programmer detected" as an unplugged programmer.
        var window = new MainWindow(new DeviceConnector(
            () => new[] { "/dev/ttyUSB0" },
            _ => throw new UnauthorizedAccessException(
                "Access to the port '/dev/ttyUSB0' is denied."),
            HostOs.Linux));
        window.Show();

        await window.RefreshAsync();

        Assert.Contains("permission denied on /dev/ttyUSB0", Text(window, "DeviceStatusText"));
        Assert.Contains("serial group", Text(window, "DeviceStatusText"));
    }

    [AvaloniaFact]
    public async Task read_rom_dumps_cart_and_logs_transaction()
    {
        var rom = TestRoms.MakeRom(0x80000);
        var window = Window(new FakeFlashKitDevice(rom));
        string file = TempFile("dump.bin");
        string? suggested = null;
        window.PickSavePath = (name, _) => { suggested = name; return Task.FromResult<string?>(file); };

        await window.ReadRomAsync();

        Assert.Equal("TEST GAME (U).bin", suggested);
        Assert.Equal(rom, File.ReadAllBytes(file));
        var entry = Assert.Single(window.Log);
        Assert.Equal("Read ROM", entry.Title);
        Assert.Equal(file, entry.Detail);
        Assert.StartsWith("OK — 512K", entry.Status);
        Assert.Contains("CRC32 ", entry.Status);
        Assert.Contains("MD5 ", entry.Status);
        Assert.Contains("SHA-1 ", entry.Status);
        Assert.True(entry.Succeeded);
        Assert.False(entry.Failed);
        Assert.False(entry.Running);
        Assert.Equal(entry.ProgressMax, entry.ProgressValue);

        // Force a layout pass so the log row's DataTemplate instantiates —
        // this is the only place its bindings are exercised (they fail
        // silently at runtime if a property name drifts).
        window.UpdateLayout();
        var list = window.FindControl<ItemsControl>("LogList")!;
        var bar = list.GetVisualDescendants().OfType<ProgressBar>().Single();
        Assert.Equal(entry.ProgressMax, bar.Maximum);
        Assert.Equal(entry.ProgressValue, bar.Value);
        Assert.Contains(list.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == entry.Status);
        var bubble = list.GetVisualDescendants().OfType<Ellipse>().Single();
        Assert.Equal(Color.Parse("#3FB950"), ((ISolidColorBrush)bubble.Fill!).Color);
    }

    [AvaloniaFact]
    public async Task read_ram_dumps_save_and_logs_transaction()
    {
        var window = Window(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000), sramBytes: 8192));
        string file = TempFile("save.srm");
        string? suggested = null;
        window.PickSavePath = (name, _) => { suggested = name; return Task.FromResult<string?>(file); };

        await window.ReadRamAsync();

        Assert.Equal("TEST GAME (U).srm", suggested);
        Assert.Equal(16384, new FileInfo(file).Length);
        var entry = Assert.Single(window.Log);
        Assert.Equal("Read RAM", entry.Title);
        Assert.StartsWith("OK — 8K", entry.Status);
        Assert.Contains("CRC32 ", entry.Status);
        Assert.Contains("MD5 ", entry.Status);
        Assert.Contains("SHA-1 ", entry.Status);
    }

    [AvaloniaFact]
    public async Task cancelled_picker_logs_cancelled_transaction()
    {
        var window = Window(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)));
        window.PickSavePath = (_, _) => Task.FromResult<string?>(null);

        await window.ReadRomAsync();

        var entry = Assert.Single(window.Log);
        Assert.Equal("Cancelled", entry.Status);
        Assert.False(entry.Failed);
        Assert.False(entry.Succeeded);
        Assert.False(entry.Running);
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
        var statuses = new List<string>();
        window.PickOpenPath = _ =>
        {
            // The entry exists by picker time; record every status it goes
            // through so the transient phase labels are observable.
            var entry = window.Log[0];
            entry.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TransactionEntry.Status)) statuses.Add(entry.Status);
            };
            return Task.FromResult<string?>(file);
        };

        await window.WriteRomAsync();

        Assert.Contains("Flash erase...", statuses);
        Assert.Contains("Flash write...", statuses);
        Assert.Contains("Flash verify...", statuses);
        Assert.StartsWith("OK — 128K written", window.Log[0].Status);
        Assert.Contains("CRC32 ", window.Log[0].Status);
        Assert.Contains("MD5 ", window.Log[0].Status);
        Assert.Contains("SHA-1 ", window.Log[0].Status);
        Assert.Equal(image, fake.Rom.Take(image.Length));
    }

    [AvaloniaFact]
    public async Task apply_patch_flashes_the_patched_image()
    {
        var fake = new FakeFlashKitDevice(new byte[0x400000]) { FlashWritable = true };
        var window = Window(fake);
        var baseImg = new byte[0x20000];
        for (int i = 0; i < baseImg.Length; i++) baseImg[i] = (byte)(i * 5 + 2);
        var patched = (byte[])baseImg.Clone();
        patched[0x40] ^= 0xFF;
        string imgFile = TempFile("img.bin");
        string patchFile = TempFile("hack.ips");
        File.WriteAllBytes(imgFile, baseImg);
        File.WriteAllBytes(patchFile, FlashKit.Core.IpsPatch.Create(baseImg, patched));

        window.PickOpenPath = _ => Task.FromResult<string?>(patchFile);
        Assert.True(await window.Model.RequestApplyPatchAsync(true));
        window.PickOpenPath = _ => Task.FromResult<string?>(imgFile);
        await window.WriteRomAsync();

        Assert.Equal(patched, fake.Rom.Take(patched.Length).ToArray());
    }

    [AvaloniaFact]
    public async Task create_patch_writes_an_ips_diff()
    {
        var cart = TestRoms.MakeRom(0x80000);
        var window = Window(new FakeFlashKitDevice(cart));
        var baseRom = (byte[])cart.Clone();
        baseRom[0x300] ^= 0xFF;
        string baseFile = TempFile("base.bin");
        string outIps = TempFile("out.ips");
        File.WriteAllBytes(baseFile, baseRom);
        window.PickOpenPath = _ => Task.FromResult<string?>(baseFile);
        window.PickSavePath = (_, _) => Task.FromResult<string?>(outIps);

        await window.Model.CreatePatchAsync();

        Assert.Equal(cart, FlashKit.Core.IpsPatch.Apply(baseRom, File.ReadAllBytes(outIps)));
    }

    [AvaloniaFact]
    public async Task write_rom_without_flash_chip_fails_entry_and_reenables_buttons()
    {
        var window = Window(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)));
        string file = TempFile("image.bin");
        File.WriteAllBytes(file, TestRoms.MakeRom(0x20000));
        window.PickOpenPath = _ => Task.FromResult<string?>(file);

        await window.WriteRomAsync();

        var entry = Assert.Single(window.Log);
        Assert.True(entry.Failed);
        Assert.False(entry.Succeeded);
        Assert.False(entry.Running);
        Assert.Contains("No flash chip detected", entry.Status);
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

        var entry = Assert.Single(window.Log);
        Assert.StartsWith("OK — 8192 words sent", entry.Status);
        Assert.Contains("CRC32 ", entry.Status);
        Assert.Contains("MD5 ", entry.Status);
        Assert.Contains("SHA-1 ", entry.Status);
        for (int i = 0; i < 8192; i++) Assert.Equal(srm[i * 2 + 1], fake.Sram[i]);
    }

    [AvaloniaFact]
    public async Task auto_dump_dumps_newly_inserted_cart_once()
    {
        var rom = TestRoms.MakeRom(0x80000);
        var fake = new FakeFlashKitDevice(rom, sramBytes: 8192) { CartInserted = false };
        var window = Window(fake);
        window.PickFolder = () => Task.FromResult<string?>(dir);
        window.FindControl<CheckBox>("ChkAutoRom")!.IsChecked = true;
        window.FindControl<CheckBox>("ChkAutoRam")!.IsChecked = true;

        await window.RefreshAsync();
        Assert.Empty(window.Log); // no cart yet, nothing to dump

        fake.CartInserted = true;
        await window.RefreshAsync();

        Assert.Equal(rom, File.ReadAllBytes(Path.Combine(dir, "TEST GAME (U).bin")));
        Assert.Equal(16384, new FileInfo(Path.Combine(dir, "TEST GAME (U).srm")).Length);
        Assert.Equal(2, window.Log.Count); // newest first: RAM then ROM
        Assert.Equal("Auto-dump RAM", window.Log[0].Title);
        Assert.Equal("Auto-dump ROM", window.Log[1].Title);
        Assert.All(window.Log, e => Assert.True(e.Succeeded));

        await window.RefreshAsync();
        Assert.Equal(2, window.Log.Count); // same cart: no re-dump
    }

    [AvaloniaFact]
    public async Task auto_dump_on_reinsertion_does_not_overwrite_earlier_dump()
    {
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000));
        var window = Window(fake);
        window.PickFolder = () => Task.FromResult<string?>(dir);
        window.FindControl<CheckBox>("ChkAutoRom")!.IsChecked = true;

        await window.RefreshAsync(); // cart present from the start: dumps
        fake.CartInserted = false;
        await window.RefreshAsync(); // removal re-arms
        fake.CartInserted = true;
        await window.RefreshAsync(); // reinsertion dumps again

        Assert.True(File.Exists(Path.Combine(dir, "TEST GAME (U).bin")));
        Assert.True(File.Exists(Path.Combine(dir, "TEST GAME (U) (2).bin")));
        Assert.Equal(2, window.Log.Count);
    }

    [AvaloniaFact]
    public async Task cancelling_folder_picker_unchecks_auto_dump()
    {
        var window = Window(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)));
        window.PickFolder = () => Task.FromResult<string?>(null);
        var box = window.FindControl<CheckBox>("ChkAutoRom")!;

        box.IsChecked = true;

        Assert.False(box.IsChecked);
        await window.RefreshAsync();
        Assert.Empty(window.Log);
    }

    [AvaloniaFact]
    public async Task auto_write_flashes_newly_inserted_cart_once()
    {
        var image = TestRoms.MakeRom(0x20000, name: "NEW GAME");
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)) { FlashWritable = true, CartInserted = false };
        var window = Window(fake);
        string file = TempFile("image.bin");
        File.WriteAllBytes(file, image);
        window.ConfirmAutoWrite = () => Task.FromResult(true);
        window.PickOpenPath = _ => Task.FromResult<string?>(file);
        window.FindControl<CheckBox>("ChkAutoWrite")!.IsChecked = true;

        await window.RefreshAsync();
        Assert.Empty(window.Log); // no cart yet

        fake.CartInserted = true;
        await window.RefreshAsync();

        var entry = Assert.Single(window.Log);
        Assert.Equal("Auto-write ROM", entry.Title);
        Assert.True(entry.Succeeded);
        Assert.Equal(image, fake.Rom.Take(image.Length));

        // The write changed the cart's contents; the cart must still count
        // as processed or auto-write would re-fire in an erase loop.
        await window.RefreshAsync();
        Assert.Single(window.Log);
    }

    [AvaloniaFact]
    public async Task declined_warning_keeps_auto_write_off()
    {
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)) { FlashWritable = true };
        var window = Window(fake);
        window.ConfirmAutoWrite = () => Task.FromResult(false);
        window.PickOpenPath = _ => Task.FromResult<string?>(TempFile("image.bin"));
        var box = window.FindControl<CheckBox>("ChkAutoWrite")!;

        box.IsChecked = true;

        Assert.False(box.IsChecked);
        await window.RefreshAsync();
        Assert.Empty(window.Log);
    }

    [AvaloniaFact]
    public void auto_dump_and_auto_write_are_mutually_exclusive()
    {
        var window = Window(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)));
        window.PickFolder = () => Task.FromResult<string?>(dir);
        window.ConfirmAutoWrite = () => Task.FromResult(true);
        window.PickOpenPath = _ => Task.FromResult<string?>(TempFile("image.bin"));
        var dumpRom = window.FindControl<CheckBox>("ChkAutoRom")!;
        var dumpRam = window.FindControl<CheckBox>("ChkAutoRam")!;
        var write = window.FindControl<CheckBox>("ChkAutoWrite")!;

        dumpRom.IsChecked = true;
        Assert.False(write.IsEnabled);

        dumpRom.IsChecked = false;
        Assert.True(write.IsEnabled);

        write.IsChecked = true;
        Assert.False(dumpRom.IsEnabled);
        Assert.False(dumpRam.IsEnabled);

        write.IsChecked = false;
        Assert.True(dumpRom.IsEnabled);
        Assert.True(dumpRam.IsEnabled);
    }

    [AvaloniaFact]
    public async Task buttons_are_disabled_while_an_operation_runs()
    {
        var window = Window(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)));
        window.PickSavePath = (_, _) => Task.FromResult<string?>(TempFile("dump.bin"));

        var running = window.ReadRomAsync();
        foreach (var name in new[] { "BtnReadRom", "BtnWriteRom", "BtnReadRam", "BtnWriteRam" })
            Assert.False(Btn(window, name).IsEnabled);

        await running;
        foreach (var name in new[] { "BtnReadRom", "BtnWriteRom", "BtnReadRam", "BtnWriteRam" })
            Assert.True(Btn(window, name).IsEnabled);
    }
}
