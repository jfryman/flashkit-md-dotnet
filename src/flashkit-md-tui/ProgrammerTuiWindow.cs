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
    // Per-transaction cards (newest first) in a scrollable host, mirroring
    // the GUI's log: each entry carries its own progress bar and full
    // status text instead of one truncated line and a global bar.
    internal readonly List<TransactionCard> Cards = new();
    internal readonly View CardsHost = new()
    {
        X = 0,
        Y = 0,
        Width = Dim.Fill(),
        Height = Dim.Fill(),
        CanFocus = true,
    };

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

        var transFrame = new FrameView { Title = "Transactions", X = LeftWidth, Y = 6, Width = Dim.Fill(), Height = Dim.Fill(3) };
        CardsHost.VerticalScrollBar.VisibilityMode = ScrollBarVisibilityMode.Auto;
        CardsHost.ViewportChanged += (_, _) => UpdateCardsLayout();
        CardsHost.KeyDown += OnCardsHostKey;
        transFrame.Add(CardsHost);

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
            statusFrame);

        // Plain Tab walks every interactive element across all panels, like
        // the GUI: FrameViews default to TabStop=TabGroup, which traps Tab
        // inside the current frame (F6 moves between groups — undiscoverable).
        // Display-only frames opt out of focus entirely, or they'd become
        // empty Tab stops themselves.
        foreach (var frame in new[] { romFrame, ramFrame, autoDumpFrame, autoWriteFrame, transFrame })
            frame.TabStop = TabBehavior.TabStop;
        infoFrame.CanFocus = false;
        statusFrame.CanFocus = false;

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
        IndicatorColors.Tint(DeviceDot, model.DevicePresent ? IndicatorColors.Success : IndicatorColors.Neutral);
        DeviceStatusLabel.Text = model.DeviceStatus;
        CartDot.Text = model.CartPresent ? "●" : "○";
        IndicatorColors.Tint(CartDot, model.CartPresent ? IndicatorColors.Success : IndicatorColors.Neutral);
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

    /// <summary>Entries are only ever inserted at the top of the model's
    /// log; each gets a card at Y=0 and existing cards shift down. A new
    /// entry snaps the view back to the top so the active operation is
    /// visible, like the GUI (which always shows the newest entry).</summary>
    void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (TransactionEntry entry in e.NewItems)
            {
                var card = new TransactionCard(entry);
                Cards.Insert(0, card);
                CardsHost.Add(card);
            }
        for (int i = 0; i < Cards.Count; i++) Cards[i].Y = i * TransactionCard.CardHeight;
        UpdateCardsLayout();
        CardsHost.Viewport = CardsHost.Viewport with { Y = 0 };
    }

    /// <summary>Content height tracks the card stack; content width tracks
    /// the viewport (Dim.Fill sizes against the content area, so a stale
    /// width would collapse or clip the cards).</summary>
    void UpdateCardsLayout()
    {
        var size = new System.Drawing.Size(
            Math.Max(1, CardsHost.Viewport.Width),
            Math.Max(1, Cards.Count * TransactionCard.CardHeight));
        if (CardsHost.GetContentSize() != size) CardsHost.SetContentSize(size);
    }

    void OnCardsHostKey(object? sender, Terminal.Gui.Input.Key key)
    {
        int page = Math.Max(1, CardsHost.Viewport.Height - 1);
        int delta;
        if (key == Terminal.Gui.Input.Key.CursorDown) delta = 1;
        else if (key == Terminal.Gui.Input.Key.CursorUp) delta = -1;
        else if (key == Terminal.Gui.Input.Key.PageDown) delta = page;
        else if (key == Terminal.Gui.Input.Key.PageUp) delta = -page;
        else return;

        int maxY = Math.Max(0, CardsHost.GetContentSize().Height - CardsHost.Viewport.Height);
        CardsHost.Viewport = CardsHost.Viewport with
        {
            Y = Math.Clamp(CardsHost.Viewport.Y + delta, 0, maxY),
        };
        key.Handled = true;
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

    /// <summary>
    /// Runs a modal dialog on the main-loop thread and returns its result.
    /// The prompts are invoked from ProgrammerModel operations that reach
    /// this point through `await Task.Run(...)` continuations, which do NOT
    /// reliably resume on the UI thread — running Application.Run (a nested
    /// event loop, drawing views) off that thread corrupts the display or
    /// throws in layout. Application.Invoke marshals the dialog back onto
    /// the main loop; the awaiting caller yields, so this never deadlocks.
    /// </summary>
    Task<T> OnMainLoop<T>(Func<T> runModal)
    {
        var tcs = new TaskCompletionSource<T>();
        RunningApp.Invoke(() =>
        {
            try { tcs.SetResult(runModal()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    Task<string?> DefaultPickSavePath(string suggestedName, PromptFileKind kind) =>
        OnMainLoop<string?>(() =>
        {
            // Seed a full path (dir + name), not a bare filename, so the
            // dialog opens with a real directory context and a sensible
            // suggestion.
            using var d = new SaveDialog { Title = "Save " + Describe(kind) };
            d.Path = Path.Combine(Directory.GetCurrentDirectory(), suggestedName);
            RunningApp.Run(d, null);
            return ResolveSavePath(DialogResult(d), suggestedName);
        });

    /// <summary>
    /// SaveDialog.Path/FileName is just the dialog's path-box text.
    /// Navigating into a folder to pick a save location leaves a directory
    /// there, and writing to a directory fails with "Access to the path …
    /// is denied" — so when the chosen path is an existing directory, save
    /// the suggested name into it instead.
    /// </summary>
    internal static string? ResolveSavePath(string? dialogPath, string suggestedName)
    {
        if (dialogPath == null) return null;
        if (Directory.Exists(dialogPath)) return Path.Combine(dialogPath, suggestedName);
        return dialogPath;
    }

    Task<string?> DefaultPickOpenPath(PromptFileKind kind) =>
        OnMainLoop<string?>(() =>
        {
            using var d = new FileDialog { Title = "Open " + Describe(kind), OpenMode = OpenMode.File, MustExist = true };
            RunningApp.Run(d, null);
            return DialogResult(d);
        });

    Task<string?> DefaultPickFolder() =>
        OnMainLoop<string?>(() =>
        {
            using var d = new FileDialog { Title = "Auto-dump folder", OpenMode = OpenMode.Directory, MustExist = true };
            RunningApp.Run(d, null);
            return DialogResult(d);
        });

    static string Describe(PromptFileKind kind) =>
        kind == PromptFileKind.RomImage ? "ROM image (.bin)" : "save RAM (.srm)";

    static string? DialogResult(FileDialog d) =>
        d.Canceled || string.IsNullOrWhiteSpace(d.Path) ? null : d.Path;

    Task<bool> DefaultConfirmAutoWrite()
    {
        if (AutoWriteWarning.Suppressed) return Task.FromResult(true);
        return OnMainLoop(() =>
        {
            int? choice = MessageBox.Query(RunningApp, AutoWriteWarning.Title, AutoWriteWarning.Text,
                "Enable auto-write", "Enable, don't ask again", "Cancel");
            if (choice == 1) AutoWriteWarning.Suppress();
            return choice is 0 or 1;
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) model.Dispose();
        base.Dispose(disposing);
    }
}
