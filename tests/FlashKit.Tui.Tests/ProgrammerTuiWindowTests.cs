using FlashKit.Core;
using FlashKit.Core.Tests;
using FlashKit.Presentation;
using FlashKit.Tui;
using Terminal.Gui.Views;

namespace FlashKit.Tui.Tests;

/// <summary>
/// End-to-end tests for the Terminal.Gui adapter over ProgrammerModel,
/// mirroring the GUI's MainWindowTests: status labels, transaction lines,
/// busy state, auto flows, and error paths. Terminal.Gui views work without
/// Application.Init, so no driver or main loop is involved — the fake
/// device stands in for the programmer and the prompt seams for dialogs.
/// </summary>
public class ProgrammerTuiWindowTests : IDisposable
{
    readonly string dir;

    public ProgrammerTuiWindowTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "flashkit-tui-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    public void Dispose() => Directory.Delete(dir, recursive: true);

    string TempFile(string name) => Path.Combine(dir, name);

    static ProgrammerTuiWindow Window(FakeFlashKitDevice fake) =>
        new(new DeviceConnector(() => new[] { "/dev/ttyUSB0" }, _ => fake, HostOs.Linux));

    [Fact]
    public async Task refresh_shows_device_and_cart_info()
    {
        var window = Window(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000), sramBytes: 8192));

        await window.RefreshAsync();

        Assert.Equal("●", window.DeviceDot.Text);
        Assert.Equal("Programmer connected on FAKE", window.DeviceStatusLabel.Text);
        Assert.Equal("Cartridge inserted", window.CartStatusLabel.Text);
        Assert.Equal("TEST GAME (U)", window.InfoName.Text);
        Assert.Equal("512K", window.InfoRomSize.Text);
        Assert.Equal("8K", window.InfoRamSize.Text);
        Assert.Equal("512K", window.InfoHeaderSize.Text);
    }

    [Fact]
    public async Task refresh_tracks_cartridge_insertion()
    {
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)) { CartInserted = false };
        var window = Window(fake);

        await window.RefreshAsync();
        Assert.Equal("No cartridge", window.CartStatusLabel.Text);
        Assert.Equal("○", window.CartDot.Text);
        Assert.Equal("—", window.InfoName.Text);

        fake.CartInserted = true;
        await window.RefreshAsync();
        Assert.Equal("Cartridge inserted", window.CartStatusLabel.Text);
        Assert.Equal("●", window.CartDot.Text);
        Assert.Equal("TEST GAME (U)", window.InfoName.Text);
    }

    [Fact]
    public async Task refresh_reports_missing_programmer()
    {
        var window = new ProgrammerTuiWindow(new DeviceConnector(
            () => Array.Empty<string>(),
            _ => throw new InvalidOperationException("no ports to open"),
            HostOs.Linux));

        await window.RefreshAsync();

        Assert.Equal("○", window.DeviceDot.Text);
        Assert.Equal("No programmer detected", window.DeviceStatusLabel.Text);
        Assert.Equal("—", window.CartStatusLabel.Text);
    }

    [Fact]
    public async Task bottom_status_bullets_never_overlap_on_long_port_names()
    {
        // macOS port names push the device text past column 46, where the
        // cart bullet used to sit at a fixed offset — they overlapped.
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000))
        {
            PortName = "/dev/cu.usbserial-A50285BI",
        };
        var window = Window(fake);

        await window.RefreshAsync();
        window.Frame = new System.Drawing.Rectangle(0, 0, 120, 30);
        window.Layout();

        Assert.Equal("Programmer connected on /dev/cu.usbserial-A50285BI",
            window.DeviceStatusLabel.Text);
        Assert.True(window.CartDot.Frame.X >= window.DeviceStatusLabel.Frame.Right + 4,
            $"cart dot at {window.CartDot.Frame.X}, device text ends at {window.DeviceStatusLabel.Frame.Right}");
        Assert.True(window.CartStatusLabel.Frame.X >= window.CartDot.Frame.Right + 2);
    }

    [Fact]
    public void action_buttons_are_uniform_and_shadowless()
    {
        // Padded labels keep the bracket spans identical within each pair;
        // ShadowStyles.None because the default drop shadow rendered as
        // stray black cells after every button.
        var window = Window(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)));
        window.Frame = new System.Drawing.Rectangle(0, 0, 100, 28);
        window.Layout();

        var actionButtons = new[]
        {
            window.BtnReadRom, window.BtnWriteRom, window.BtnReadRam, window.BtnWriteRam,
        };
        Assert.Single(actionButtons.Select(b => b.Frame.Width).Distinct());

        foreach (var b in actionButtons.Concat(new[] { window.BtnDumpFolder, window.BtnWriteFile }))
            Assert.Equal(Terminal.Gui.ViewBase.ShadowStyles.None, b.ShadowStyle);
    }

    [Fact]
    public void tab_reaches_every_interactive_element_including_the_log()
    {
        // FrameViews default to TabStop=TabGroup, which traps Tab inside the
        // current frame; the transaction list on the right was unreachable.
        var window = Window(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)));
        window.Frame = new System.Drawing.Rectangle(0, 0, 100, 28);
        window.Layout();

        window.FocusDeepest(Terminal.Gui.ViewBase.NavigationDirection.Forward,
            Terminal.Gui.ViewBase.TabBehavior.TabStop);
        var focused = new List<Terminal.Gui.ViewBase.View?>();
        for (int i = 0; i < 24; i++)
        {
            focused.Add(window.MostFocused);
            window.AdvanceFocus(Terminal.Gui.ViewBase.NavigationDirection.Forward,
                Terminal.Gui.ViewBase.TabBehavior.TabStop);
        }

        var expected = new Terminal.Gui.ViewBase.View[]
        {
            window.BtnReadRom, window.BtnWriteRom, window.BtnReadRam, window.BtnWriteRam,
            window.ChkAutoRom, window.ChkAutoRam, window.BtnDumpFolder,
            window.ChkAutoWrite, window.BtnWriteFile, window.CardsHost,
        };
        foreach (var view in expected)
            Assert.Contains(view, focused);
        // no display-only frame becomes an empty Tab stop (cards are
        // FrameViews but CanFocus=false, so they never appear either)
        Assert.DoesNotContain(focused, v => v is FrameView);
    }

    [Fact]
    public void resolve_save_path_appends_suggested_name_to_a_directory()
    {
        // The save dialog returns its path-box text; navigating into a
        // folder leaves a directory there, and writing to a directory
        // fails with "Access to the path … is denied".
        string subdir = Path.Combine(dir, "target");
        Directory.CreateDirectory(subdir);

        Assert.Equal(
            Path.Combine(subdir, "SHINING FORCE 2 (U).bin"),
            ProgrammerTuiWindow.ResolveSavePath(subdir, "SHINING FORCE 2 (U).bin"));
    }

    [Fact]
    public void resolve_save_path_keeps_a_file_path_as_is()
    {
        string file = Path.Combine(dir, "my dump.bin");
        Assert.Equal(file, ProgrammerTuiWindow.ResolveSavePath(file, "SHINING FORCE 2 (U).bin"));
    }

    [Fact]
    public void resolve_save_path_passes_through_cancellation()
    {
        Assert.Null(ProgrammerTuiWindow.ResolveSavePath(null, "whatever.bin"));
    }

    sealed class UnopenablePort : ISerialPort
    {
        readonly Exception error;
        public UnopenablePort(Exception error) => this.error = error;
        public string PortName => "BROKEN";
        public int ReadTimeout { get; set; }
        public int WriteTimeout { get; set; }
        public void Open() => throw error;
        public void Close() { }
        public void Write(byte[] buffer, int offset, int count) => throw error;
        public int Read(byte[] buffer, int offset, int count) => throw error;
        public int ReadByte() => throw error;
    }

    [Fact]
    public async Task permission_denied_is_surfaced_not_masked_as_missing()
    {
        // Regression: a denied serial port used to render as the same
        // "No programmer detected" as an unplugged programmer.
        var window = new ProgrammerTuiWindow(new DeviceConnector(
            () => new[] { "/dev/ttyUSB0" },
            _ => new UnopenablePort(new UnauthorizedAccessException(
                "Access to the port '/dev/ttyUSB0' is denied.")),
            HostOs.Linux));

        await window.RefreshAsync();

        Assert.Equal("○", window.DeviceDot.Text);
        Assert.Contains("permission denied on /dev/ttyUSB0", window.DeviceStatusLabel.Text);
        Assert.Contains("serial group", window.DeviceStatusLabel.Text);
    }

    [Fact]
    public async Task session_is_held_across_polls_and_operations()
    {
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000));
        var window = Window(fake);
        window.PickSavePath = (_, _) => Task.FromResult<string?>(TempFile("dump.bin"));

        await window.RefreshAsync();
        await window.RefreshAsync();
        await window.ReadRomAsync();
        await window.RefreshAsync();

        Assert.Equal(1, fake.OpenCount);
    }

    [Fact]
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
        var card = Assert.Single(window.Cards);
        Assert.Contains("Read ROM", card.Title);
        Assert.Equal("✔", card.Bubble.Text);
        Assert.Equal(IndicatorColors.Success, card.Bubble.GetScheme().Normal.Foreground);
        Assert.Equal(file, card.DetailLabel.Text);
        Assert.StartsWith("OK — 512K, MD5 ", card.StatusLabel.Text);
        Assert.Equal(1f, card.Progress.Fraction); // own bar, filled on success
    }

    [Fact]
    public async Task cancelled_picker_logs_cancelled_transaction()
    {
        var window = Window(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)));
        window.PickSavePath = (_, _) => Task.FromResult<string?>(null);

        await window.ReadRomAsync();

        var card = Assert.Single(window.Cards);
        Assert.Contains("Read ROM", card.Title);
        Assert.Equal("○", card.Bubble.Text);
        Assert.Equal(IndicatorColors.Neutral, card.Bubble.GetScheme().Normal.Foreground);
        Assert.Equal("Cancelled", card.StatusLabel.Text);
        Assert.True(window.BtnReadRom.Enabled);
    }

    [Fact]
    public async Task write_rom_without_flash_chip_fails_entry_and_reenables_buttons()
    {
        var window = Window(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)));
        string file = TempFile("image.bin");
        File.WriteAllBytes(file, TestRoms.MakeRom(0x20000));
        window.PickOpenPath = _ => Task.FromResult<string?>(file);

        await window.WriteRomAsync();

        var card = Assert.Single(window.Cards);
        Assert.Contains("Write ROM", card.Title);
        Assert.Equal("✖", card.Bubble.Text);
        Assert.Equal(IndicatorColors.Failure, card.Bubble.GetScheme().Normal.Foreground);
        Assert.Equal(IndicatorColors.Failure, card.StatusLabel.GetScheme().Normal.Foreground);
        Assert.Contains("No flash chip detected", card.StatusLabel.Text);
        Assert.True(window.BtnWriteRom.Enabled);
    }

    [Fact]
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

        var card = Assert.Single(window.Cards);
        Assert.Contains("Write RAM", card.Title);
        Assert.Equal("✔", card.Bubble.Text);
        Assert.Contains("8192 words sent", card.StatusLabel.Text);
        for (int i = 0; i < 8192; i++) Assert.Equal(srm[i * 2 + 1], fake.Sram[i]);
    }

    [Fact]
    public async Task auto_dump_dumps_newly_inserted_cart_once()
    {
        var rom = TestRoms.MakeRom(0x80000);
        var fake = new FakeFlashKitDevice(rom, sramBytes: 8192) { CartInserted = false };
        var window = Window(fake);
        window.PickFolder = () => Task.FromResult<string?>(dir);
        window.ChkAutoRom.Value = CheckState.Checked;
        window.ChkAutoRam.Value = CheckState.Checked;

        await window.RefreshAsync();
        Assert.Empty(window.Cards); // no cart yet, nothing to dump

        fake.CartInserted = true;
        await window.RefreshAsync();

        Assert.Equal(rom, File.ReadAllBytes(Path.Combine(dir, "TEST GAME (U).bin")));
        Assert.Equal(16384, new FileInfo(Path.Combine(dir, "TEST GAME (U).srm")).Length);
        Assert.Equal(2, window.Cards.Count); // newest first: RAM then ROM
        Assert.Contains("Auto-dump RAM", window.Cards[0].Title);
        Assert.Contains("Auto-dump ROM", window.Cards[1].Title);
        Assert.All(window.Cards, c => Assert.Equal("✔", c.Bubble.Text));

        await window.RefreshAsync();
        Assert.Equal(2, window.Cards.Count); // same cart: no re-dump
    }

    [Fact]
    public async Task cancelling_folder_picker_unchecks_auto_dump()
    {
        var window = Window(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)));
        window.PickFolder = () => Task.FromResult<string?>(null);

        window.ChkAutoRom.Value = CheckState.Checked;

        Assert.Equal(CheckState.UnChecked, window.ChkAutoRom.Value);
        await window.RefreshAsync();
        Assert.Empty(window.Cards);
    }

    [Fact]
    public async Task declined_warning_keeps_auto_write_off()
    {
        var fake = new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)) { FlashWritable = true };
        var window = Window(fake);
        window.ConfirmAutoWrite = () => Task.FromResult(false);
        window.PickOpenPath = _ => Task.FromResult<string?>(TempFile("image.bin"));

        window.ChkAutoWrite.Value = CheckState.Checked;

        Assert.Equal(CheckState.UnChecked, window.ChkAutoWrite.Value);
        await window.RefreshAsync();
        Assert.Empty(window.Cards);
    }

    [Fact]
    public void auto_dump_and_auto_write_are_mutually_exclusive()
    {
        var window = Window(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)));
        window.PickFolder = () => Task.FromResult<string?>(dir);
        window.ConfirmAutoWrite = () => Task.FromResult(true);
        window.PickOpenPath = _ => Task.FromResult<string?>(TempFile("image.bin"));

        window.ChkAutoRom.Value = CheckState.Checked;
        Assert.False(window.ChkAutoWrite.Enabled);

        window.ChkAutoRom.Value = CheckState.UnChecked;
        Assert.True(window.ChkAutoWrite.Enabled);

        window.ChkAutoWrite.Value = CheckState.Checked;
        Assert.False(window.ChkAutoRom.Enabled);
        Assert.False(window.ChkAutoRam.Enabled);

        window.ChkAutoWrite.Value = CheckState.UnChecked;
        Assert.True(window.ChkAutoRom.Enabled);
        Assert.True(window.ChkAutoRam.Enabled);
    }

    [Fact]
    public async Task buttons_are_disabled_while_an_operation_runs()
    {
        var window = Window(new FakeFlashKitDevice(TestRoms.MakeRom(0x80000)));
        window.PickSavePath = (_, _) => Task.FromResult<string?>(TempFile("dump.bin"));

        var running = window.ReadRomAsync();
        foreach (var btn in new[] { window.BtnReadRom, window.BtnWriteRom, window.BtnReadRam, window.BtnWriteRam })
            Assert.False(btn.Enabled);

        await running;
        foreach (var btn in new[] { window.BtnReadRom, window.BtnWriteRom, window.BtnReadRam, window.BtnWriteRam })
            Assert.True(btn.Enabled);
    }

    [Fact]
    public void card_tracks_entry_through_its_lifecycle()
    {
        var entry = new TransactionEntry("Read ROM");
        var card = new TransactionCard(entry);
        Assert.Equal(entry.Time + " Read ROM", card.Title);
        Assert.Equal("▶", card.Bubble.Text);
        Assert.Equal(IndicatorColors.Running, card.Bubble.GetScheme().Normal.Foreground);

        entry.Detail = "/tmp/x/dump.bin";
        entry.ProgressMax = 100;
        entry.ProgressValue = 25;
        Assert.Equal("/tmp/x/dump.bin", card.DetailLabel.Text);
        Assert.Equal(0.25f, card.Progress.Fraction);

        entry.Succeed("OK — 512K, MD5 AB-CD");
        Assert.Equal("✔", card.Bubble.Text);
        Assert.Equal(IndicatorColors.Success, card.Bubble.GetScheme().Normal.Foreground);
        Assert.Equal("OK — 512K, MD5 AB-CD", card.StatusLabel.Text);
        Assert.Equal(1f, card.Progress.Fraction);

        var failed = new TransactionEntry("Write ROM");
        var failedCard = new TransactionCard(failed);
        failed.Fail("Verify error at 0");
        Assert.Equal("✖", failedCard.Bubble.Text);
        Assert.Equal(IndicatorColors.Failure, failedCard.Bubble.GetScheme().Normal.Foreground);
        Assert.Equal(IndicatorColors.Failure, failedCard.StatusLabel.GetScheme().Normal.Foreground);
        Assert.Equal("Verify error at 0", failedCard.StatusLabel.Text);
    }
}
