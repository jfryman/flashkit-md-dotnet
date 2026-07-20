using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FlashKit.Core;
using FlashKit.Presentation;

namespace FlashKit.Gui;

/// <summary>
/// Avalonia adapter over <see cref="ProgrammerModel"/>, which owns all
/// behavior (session lifetime, device gate, poll state machine, auto
/// actions, transaction log). This class renders the model's properties
/// into the named controls, implements <see cref="IUserPrompts"/> with
/// StorageProvider pickers and a warning dialog, and drives the 2 s poll
/// timer. The TUI front-end mirrors this same adapter pattern.
/// </summary>
public partial class MainWindow : Window
{
    static readonly FilePickerFileType RomFiles = new("ROM image") { Patterns = new[] { "*.bin", "*.32x" } };
    static readonly FilePickerFileType SaveFiles = new("Save RAM") { Patterns = new[] { "*.srm" } };
    static readonly FilePickerFileType IpsFiles = new("IPS patch") { Patterns = new[] { "*.ips" } };
    static readonly IBrush PresentBrush = Brush.Parse(StatusPalette.Success);
    static readonly IBrush AbsentBrush = Brush.Parse(StatusPalette.Neutral);

    readonly ProgrammerModel model;

    internal ProgrammerModel Model => model;
    internal System.Collections.ObjectModel.ObservableCollection<TransactionEntry> Log => model.Log;

    // Test seams: headless tests inject a fake-device connector and replace
    // the pickers (the headless platform's StorageProvider never returns a
    // file). Production code uses the defaults. The model consumes these
    // through the Prompts adapter, which resolves them at call time.
    internal Func<string, FilePickerFileType, Task<string?>> PickSavePath;
    internal Func<FilePickerFileType, Task<string?>> PickOpenPath;
    internal Func<Task<string?>> PickFolder;
    internal Func<Task<bool>> ConfirmAutoWrite;

    public MainWindow() : this(null)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => _ = model.RefreshAsync();
        Opened += (_, _) => { _ = model.RefreshAsync(); timer.Start(); };
        Closed += (_, _) => timer.Stop();
    }

    internal MainWindow(DeviceConnector? connector)
    {
        InitializeComponent();
        Title += " " + VersionInfo.ClientVersion;
        PickSavePath = DefaultPickSavePath;
        PickOpenPath = DefaultPickOpenPath;
        PickFolder = DefaultPickFolder;
        ConfirmAutoWrite = DefaultConfirmAutoWrite;
        model = new ProgrammerModel(new Prompts(this), connector);
        model.PropertyChanged += (_, _) => SyncFromModel();
        LogList.ItemsSource = model.Log;
        Closed += (_, _) => model.Dispose();
        SyncFromModel();
    }

    internal Task RefreshAsync() => model.RefreshAsync();
    internal Task ReadRomAsync() => model.ReadRomAsync();
    internal Task WriteRomAsync() => model.WriteRomAsync();
    internal Task ReadRamAsync() => model.ReadRamAsync();
    internal Task WriteRamAsync() => model.WriteRamAsync();

    /// <summary>Maps the model's prompt requests onto the window's
    /// replaceable picker functions.</summary>
    sealed class Prompts : IUserPrompts
    {
        readonly MainWindow w;
        public Prompts(MainWindow w) => this.w = w;

        static FilePickerFileType Map(PromptFileKind kind) => kind switch
        {
            PromptFileKind.RomImage => RomFiles,
            PromptFileKind.IpsPatch => IpsFiles,
            _ => SaveFiles,
        };

        public Task<string?> PickSavePath(string suggestedName, PromptFileKind kind) => w.PickSavePath(suggestedName, Map(kind));
        public Task<string?> PickOpenPath(PromptFileKind kind) => w.PickOpenPath(Map(kind));
        public Task<string?> PickFolder() => w.PickFolder();
        public Task<bool> ConfirmAutoWrite() => w.ConfirmAutoWrite();
    }

    /// <summary>Renders every model property into the named controls. Cheap
    /// enough to run wholesale on any property change.</summary>
    void SyncFromModel()
    {
        DeviceDot.Fill = model.DevicePresent ? PresentBrush : AbsentBrush;
        DeviceStatusText.Text = model.DeviceStatus;
        CartDot.Fill = model.CartPresent ? PresentBrush : AbsentBrush;
        CartStatusText.Text = model.CartStatus;
        InfoName.Text = model.CartName;
        InfoSystem.Text = model.CartSystem;
        InfoRegion.Text = model.CartRegion;
        InfoRomSize.Text = model.CartRomSize;
        InfoRamSize.Text = model.CartRamSize;
        InfoHeaderSize.Text = model.CartHeaderSize;
        AutoDumpFolderText.Text = model.AutoDumpFolderDisplay;
        AutoWriteFileText.Text = model.AutoWriteFileDisplay;
        PatchFileText.Text = model.PatchFileDisplay;
        // These labels ellipsize a long path; surface the full value on hover
        // (and to assistive tech, which reads the tooltip) so it isn't lost.
        ToolTip.SetTip(AutoDumpFolderText, model.AutoDumpFolderDisplay);
        ToolTip.SetTip(AutoWriteFileText, model.AutoWriteFileDisplay);
        ToolTip.SetTip(PatchFileText, model.PatchFileDisplay);
        if (ChkApplyPatch.IsChecked != model.ApplyPatch) ChkApplyPatch.IsChecked = model.ApplyPatch;
        foreach (var btn in new[] { BtnReadRom, BtnWriteRom, BtnReadRam, BtnWriteRam, BtnCreatePatch })
            btn.IsEnabled = !model.IsBusy;
        ChkAutoRom.IsEnabled = model.CanToggleAutoDump;
        ChkAutoRam.IsEnabled = model.CanToggleAutoDump;
        ChkAutoWrite.IsEnabled = model.CanToggleAutoWrite;
    }

    void OnReadRom(object? sender, RoutedEventArgs e) => _ = model.ReadRomAsync();
    void OnWriteRom(object? sender, RoutedEventArgs e) => _ = model.WriteRomAsync();
    void OnReadRam(object? sender, RoutedEventArgs e) => _ = model.ReadRamAsync();
    void OnWriteRam(object? sender, RoutedEventArgs e) => _ = model.WriteRamAsync();

    async void OnAutoDumpToggled(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box) return;
        bool on = box.IsChecked == true;
        bool result = box == ChkAutoRom
            ? await model.RequestAutoDumpRomAsync(on)
            : await model.RequestAutoDumpRamAsync(on);
        box.IsChecked = result;
    }

    async void OnAutoWriteToggled(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box) return;
        box.IsChecked = await model.RequestAutoWriteAsync(box.IsChecked == true);
    }

    async void OnChooseFolder(object? sender, RoutedEventArgs e) =>
        await model.ChooseAutoDumpFolderAsync();

    async void OnChooseWriteFile(object? sender, RoutedEventArgs e) =>
        await model.ChooseAutoWriteFileAsync();

    async void OnApplyPatchToggled(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box) return;
        box.IsChecked = await model.RequestApplyPatchAsync(box.IsChecked == true);
    }

    async void OnChoosePatch(object? sender, RoutedEventArgs e) =>
        await model.ChoosePatchFileAsync();

    void OnCreatePatch(object? sender, RoutedEventArgs e) => _ = model.CreatePatchAsync();

    async Task<string?> DefaultPickSavePath(string suggestedName, FilePickerFileType type)
    {
        // Offer exactly the extension being saved (.32x vs .bin), so the
        // native picker doesn't append its file-type default on top of the
        // suggested name ("GAME.32x" -> "GAME.32x.bin"). ProgrammerModel
        // also normalizes the returned path as a backstop.
        string ext = System.IO.Path.GetExtension(suggestedName).TrimStart('.');
        var saveType = ext.Length > 0
            ? new FilePickerFileType(type.Name) { Patterns = new[] { "*." + ext } }
            : type;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = ext.Length > 0 ? ext : null,
            FileTypeChoices = new[] { saveType, FilePickerFileTypes.All },
        });
        return file?.TryGetLocalPath();
    }

    async Task<string?> DefaultPickOpenPath(FilePickerFileType type)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            FileTypeFilter = new[] { type, FilePickerFileTypes.All },
        });
        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }

    async Task<string?> DefaultPickFolder()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Auto-dump folder",
        });
        return folders.Count == 1 ? folders[0].TryGetLocalPath() : null;
    }

    async Task<bool> DefaultConfirmAutoWrite()
    {
        if (AutoWriteWarning.Suppressed) return true;

        var dontShow = new CheckBox { Content = "Don't show this warning again" };
        var enable = new Button { Content = "Enable auto-write" };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var dialog = new Window
        {
            Title = AutoWriteWarning.Title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = AutoWriteWarning.Text,
                    },
                    dontShow,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, enable },
                    },
                },
            },
        };
        enable.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);

        bool accepted = await dialog.ShowDialog<bool>(this);
        if (accepted && dontShow.IsChecked == true) AutoWriteWarning.Suppress();
        return accepted;
    }
}
