using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using FlashKit.Core;
using FlashKit.Presentation;

namespace FlashKit.Tui;

/// <summary>
/// Terminal.Gui adapter over <see cref="ProgrammerModel"/> — the terminal
/// sibling of the Avalonia MainWindow, with the same panel roles: action
/// groups and auto panels on the left, cart info and the transaction log on
/// the right, device/cart status along the bottom. All behavior lives in
/// the model; this class renders its properties into views and implements
/// <see cref="IUserPrompts"/> with Terminal.Gui dialogs.
///
/// Threading mirrors the GUI: Application.Init installs a main-loop
/// SynchronizationContext, so model callbacks land on the UI thread in
/// production. Tests construct this window without Init (Terminal.Gui
/// views work driverless) and replace the prompt seams.
/// </summary>
public class ProgrammerTuiWindow : Window
{
    const int LeftWidth = 26;

    readonly ProgrammerModel model;
    readonly IApplication? app;
    bool syncingToggles;

    internal ProgrammerModel Model => model;
    internal ObservableCollection<TransactionEntry> Log => model.Log;

    // Test seams, same shape as the GUI's MainWindow: the model consumes
    // these through the Prompts adapter, which resolves them at call time.
    internal Func<string, PromptFileKind, Task<string?>> PickSavePath;
    internal Func<PromptFileKind, Task<string?>> PickOpenPath;
    internal Func<Task<string?>> PickFolder;
    internal Func<Task<bool>> ConfirmAutoWrite;

    // Labels are padded to a common length so the button brackets span the
    // same columns; ShadowStyle.None because the default drop shadow reads
    // as stray black cells after every button (and bleeds onto the label
    // row beneath the chooser buttons).
    internal readonly Button BtnReadRom = MakeButton("Read ROM ");
    internal readonly Button BtnWriteRom = MakeButton("Write ROM");
    internal readonly Button BtnReadRam = MakeButton("Read RAM ");
    internal readonly Button BtnWriteRam = MakeButton("Write RAM");
    internal readonly Button BtnDumpFolder = MakeButton("Choose folder...");
    internal readonly Button BtnWriteFile = MakeButton("Choose file...");

    static Button MakeButton(string text) => new()
    {
        Text = text,
        ShadowStyle = ShadowStyles.None,
    };
    internal readonly CheckBox ChkAutoRom = new() { Text = "Dump ROM" };
    internal readonly CheckBox ChkAutoRam = new() { Text = "Dump RAM" };
    internal readonly CheckBox ChkAutoWrite = new() { Text = "Write ROM" };
    internal readonly Label DeviceDot = new() { Text = "○" };
    internal readonly Label CartDot = new() { Text = "○" };
    internal readonly Label DeviceStatusLabel = new() { Text = "" };
    internal readonly Label CartStatusLabel = new() { Text = "" };
    internal readonly Label InfoName = new() { Text = "—" };
    internal readonly Label InfoRomSize = new() { Text = "—" };
    internal readonly Label InfoRamSize = new() { Text = "—" };
    internal readonly Label InfoHeaderSize = new() { Text = "—" };
    internal readonly Label AutoDumpFolderLabel = new() { Text = "No folder chosen" };
    internal readonly Label AutoWriteFileLabel = new() { Text = "No file chosen" };
    internal readonly ProgressBar OperationProgress = new();
    internal readonly ObservableCollection<string> TransactionLines = new();
    readonly ListView logList = new();

    public ProgrammerTuiWindow(DeviceConnector? connector = null, IApplication? app = null)
    {
        this.app = app;
        Title = $"Flashkit-md {VersionInfo.ClientVersion}";
        PickSavePath = DefaultPickSavePath;
        PickOpenPath = DefaultPickOpenPath;
        PickFolder = DefaultPickFolder;
        ConfirmAutoWrite = DefaultConfirmAutoWrite;
        model = new ProgrammerModel(new Prompts(this), connector);
        model.PropertyChanged += (_, _) => SyncFromModel();
        model.Log.CollectionChanged += OnLogChanged;

        var romFrame = new FrameView { Title = "ROM", X = 0, Y = 0, Width = LeftWidth, Height = 4 };
        Place(BtnReadRom, 0);
        Place(BtnWriteRom, 1);
        romFrame.Add(BtnReadRom, BtnWriteRom);

        var ramFrame = new FrameView { Title = "RAM", X = 0, Y = 4, Width = LeftWidth, Height = 4 };
        Place(BtnReadRam, 0);
        Place(BtnWriteRam, 1);
        ramFrame.Add(BtnReadRam, BtnWriteRam);

        var autoDumpFrame = new FrameView { Title = "Auto-dump", X = 0, Y = 8, Width = LeftWidth, Height = 6 };
        Place(ChkAutoRom, 0);
        Place(ChkAutoRam, 1);
        Place(BtnDumpFolder, 2);
        Place(AutoDumpFolderLabel, 3);
        autoDumpFrame.Add(ChkAutoRom, ChkAutoRam, BtnDumpFolder, AutoDumpFolderLabel);

        var autoWriteFrame = new FrameView { Title = "Auto-write", X = 0, Y = 14, Width = LeftWidth, Height = 5 };
        Place(ChkAutoWrite, 0);
        Place(BtnWriteFile, 1);
        Place(AutoWriteFileLabel, 2);
        autoWriteFrame.Add(ChkAutoWrite, BtnWriteFile, AutoWriteFileLabel);

        var infoFrame = new FrameView { Title = "Cartridge", X = LeftWidth, Y = 0, Width = Dim.Fill(), Height = 6 };
        infoFrame.Add(
            Caption("Cartridge", 0), At(InfoName, 0),
            Caption("ROM size", 1), At(InfoRomSize, 1),
            Caption("RAM size", 2), At(InfoRamSize, 2),
            Caption("Header ROM size", 3), At(InfoHeaderSize, 3));

        var transFrame = new FrameView { Title = "Transactions", X = LeftWidth, Y = 6, Width = Dim.Fill(), Height = Dim.Fill(4) };
        logList.X = 0;
        logList.Y = 0;
        logList.Width = Dim.Fill();
        logList.Height = Dim.Fill();
        logList.SetSource(TransactionLines);
        transFrame.Add(logList);

        OperationProgress.X = 0;
        OperationProgress.Y = Pos.AnchorEnd(4);
        OperationProgress.Width = Dim.Fill();

        // Bordered status bar along the bottom, like the GUI's. The cart
        // bullet chains off the device label rather than sitting at a fixed
        // column: macOS port names ("/dev/cu.usbserial-…") push the device
        // text past any fixed offset and the two would overlap. Mirrors the
        // GUI status bar's padding and StackPanel spacing.
        var statusFrame = new FrameView { X = 0, Y = Pos.AnchorEnd(3), Width = Dim.Fill(), Height = 3 };
        DeviceDot.X = 1;
        DeviceDot.Y = 0;
        DeviceStatusLabel.X = 3;
        DeviceStatusLabel.Y = 0;
        CartDot.X = Pos.Right(DeviceStatusLabel) + 4;
        CartDot.Y = 0;
        CartStatusLabel.X = Pos.Right(CartDot) + 2;
        CartStatusLabel.Y = 0;
        statusFrame.Add(DeviceDot, DeviceStatusLabel, CartDot, CartStatusLabel);

        Add(romFrame, ramFrame, autoDumpFrame, autoWriteFrame, infoFrame, transFrame,
            OperationProgress, statusFrame);

        BtnReadRom.Accepting += (_, e) => { e.Handled = true; _ = model.ReadRomAsync(); };
        BtnWriteRom.Accepting += (_, e) => { e.Handled = true; _ = model.WriteRomAsync(); };
        BtnReadRam.Accepting += (_, e) => { e.Handled = true; _ = model.ReadRamAsync(); };
        BtnWriteRam.Accepting += (_, e) => { e.Handled = true; _ = model.WriteRamAsync(); };
        BtnDumpFolder.Accepting += (_, e) => { e.Handled = true; _ = model.ChooseAutoDumpFolderAsync(); };
        BtnWriteFile.Accepting += (_, e) => { e.Handled = true; _ = model.ChooseAutoWriteFileAsync(); };
        ChkAutoRom.ValueChanged += (_, _) => _ = OnToggleAsync(ChkAutoRom, model.RequestAutoDumpRomAsync);
        ChkAutoRam.ValueChanged += (_, _) => _ = OnToggleAsync(ChkAutoRam, model.RequestAutoDumpRamAsync);
        ChkAutoWrite.ValueChanged += (_, _) => _ = OnToggleAsync(ChkAutoWrite, model.RequestAutoWriteAsync);

        SyncFromModel();
    }

    static void Place(View v, int row)
    {
        v.X = 0;
        v.Y = row;
    }

    static Label Caption(string text, int row) => new() { Text = text, X = 0, Y = row };

    static Label At(Label label, int row)
    {
        label.X = 18;
        label.Y = row;
        return label;
    }

    internal Task RefreshAsync() => model.RefreshAsync();
    internal Task ReadRomAsync() => model.ReadRomAsync();
    internal Task WriteRomAsync() => model.WriteRomAsync();
    internal Task ReadRamAsync() => model.ReadRamAsync();
    internal Task WriteRamAsync() => model.WriteRamAsync();

    async Task OnToggleAsync(CheckBox box, Func<bool, Task<bool>> request)
    {
        if (syncingToggles) return;
        bool result = await request(box.Value == CheckState.Checked);
        syncingToggles = true;
        box.Value = result ? CheckState.Checked : CheckState.UnChecked;
        syncingToggles = false;
    }

    void SyncFromModel()
    {
        DeviceDot.Text = model.DevicePresent ? "●" : "○";
        DeviceStatusLabel.Text = model.DeviceStatus;
        CartDot.Text = model.CartPresent ? "●" : "○";
        CartStatusLabel.Text = model.CartStatus;
        InfoName.Text = model.CartName;
        InfoRomSize.Text = model.CartRomSize;
        InfoRamSize.Text = model.CartRamSize;
        InfoHeaderSize.Text = model.CartHeaderSize;
        AutoDumpFolderLabel.Text = model.AutoDumpFolderDisplay;
        AutoWriteFileLabel.Text = model.AutoWriteFileDisplay;
        foreach (var btn in new[] { BtnReadRom, BtnWriteRom, BtnReadRam, BtnWriteRam })
            btn.Enabled = !model.IsBusy;
        ChkAutoRom.Enabled = model.CanToggleAutoDump;
        ChkAutoRam.Enabled = model.CanToggleAutoDump;
        ChkAutoWrite.Enabled = model.CanToggleAutoWrite;
    }

    void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (TransactionEntry entry in e.NewItems)
                entry.PropertyChanged += (_, _) => RebuildLines();
        RebuildLines();
    }

    void RebuildLines()
    {
        TransactionLines.Clear();
        foreach (var entry in model.Log) TransactionLines.Add(FormatLine(entry));

        var newest = model.Log.Count > 0 ? model.Log[0] : null;
        OperationProgress.Fraction = newest?.Running == true
            ? (float)(newest.ProgressValue / Math.Max(1, newest.ProgressMax))
            : 0f;
    }

    /// <summary>One transaction as a log line: time, outcome glyph
    /// (▶ running, ✔ ok, ✖ failed, ○ cancelled), title, file, status.</summary>
    internal static string FormatLine(TransactionEntry e)
    {
        string icon = e.Running ? "▶" : e.Failed ? "✖" : e.Succeeded ? "✔" : "○";
        string detail = e.HasDetail ? $" [{Path.GetFileName(e.Detail)}]" : "";
        string status = e.Status.Length != 0 ? " — " + e.Status : "";
        return $"{e.Time} {icon} {e.Title}{detail}{status}";
    }

    sealed class Prompts : IUserPrompts
    {
        readonly ProgrammerTuiWindow w;
        public Prompts(ProgrammerTuiWindow w) => this.w = w;

        public Task<string?> PickSavePath(string suggestedName, PromptFileKind kind) => w.PickSavePath(suggestedName, kind);
        public Task<string?> PickOpenPath(PromptFileKind kind) => w.PickOpenPath(kind);
        public Task<string?> PickFolder() => w.PickFolder();
        public Task<bool> ConfirmAutoWrite() => w.ConfirmAutoWrite();
    }

    IApplication RunningApp => app ?? throw new InvalidOperationException(
        "default prompts need the IApplication passed to the constructor");

    Task<string?> DefaultPickSavePath(string suggestedName, PromptFileKind kind)
    {
        using var d = new SaveDialog { Title = "Save " + Describe(kind) };
        d.Path = suggestedName;
        RunningApp.Run(d, null);
        return Task.FromResult(DialogResult(d));
    }

    Task<string?> DefaultPickOpenPath(PromptFileKind kind)
    {
        using var d = new FileDialog { Title = "Open " + Describe(kind), OpenMode = OpenMode.File, MustExist = true };
        RunningApp.Run(d, null);
        return Task.FromResult(DialogResult(d));
    }

    Task<string?> DefaultPickFolder()
    {
        using var d = new FileDialog { Title = "Auto-dump folder", OpenMode = OpenMode.Directory, MustExist = true };
        RunningApp.Run(d, null);
        return Task.FromResult(DialogResult(d));
    }

    static string Describe(PromptFileKind kind) =>
        kind == PromptFileKind.RomImage ? "ROM image (.bin)" : "save RAM (.srm)";

    static string? DialogResult(FileDialog d) =>
        d.Canceled || string.IsNullOrWhiteSpace(d.Path) ? null : d.Path;

    Task<bool> DefaultConfirmAutoWrite()
    {
        if (AutoWriteWarning.Suppressed) return Task.FromResult(true);
        int? choice = MessageBox.Query(RunningApp, AutoWriteWarning.Title, AutoWriteWarning.Text,
            "Enable auto-write", "Enable, don't ask again", "Cancel");
        if (choice == 1) AutoWriteWarning.Suppress();
        return Task.FromResult(choice is 0 or 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) model.Dispose();
        base.Dispose(disposing);
    }
}
