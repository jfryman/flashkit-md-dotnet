using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FlashKit.Core;

namespace FlashKit.Gui;

/// <summary>
/// Port of the original client's Form1: same controls, same console
/// messages, same per-click connect/disconnect. Cart operations run on a
/// worker thread (the WinForms original blocked the UI thread and pumped
/// with this.Update()); buttons are disabled while one is in flight.
/// </summary>
public partial class MainWindow : Window
{
    static readonly FilePickerFileType RomFiles = new("ROM image") { Patterns = new[] { "*.bin" } };
    static readonly FilePickerFileType SaveFiles = new("Save RAM") { Patterns = new[] { "*.srm" } };

    readonly DeviceConnector? connector;

    // Test seams: headless tests inject a fake-device connector and replace
    // the pickers (the headless platform's StorageProvider never returns a
    // file). Production code uses the defaults.
    internal Func<string, FilePickerFileType, Task<string?>> PickSavePath;
    internal Func<FilePickerFileType, Task<string?>> PickOpenPath;

    public MainWindow() : this(null) { }

    internal MainWindow(DeviceConnector? connector)
    {
        this.connector = connector;
        InitializeComponent();
        Title += " " + (typeof(FlashKitSession).Assembly.GetName().Version?.ToString(3) ?? "");
        PickSavePath = DefaultPickSavePath;
        PickOpenPath = DefaultPickOpenPath;
    }

    async Task<string?> DefaultPickSavePath(string suggestedName, FilePickerFileType type)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            FileTypeChoices = new[] { type, FilePickerFileTypes.All },
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

    void ConsWriteLine(string str)
    {
        ConsoleBox.Text += str + Environment.NewLine;
        ConsoleBox.CaretIndex = ConsoleBox.Text?.Length ?? 0;
    }

    void Separator() => ConsWriteLine("-----------------------------------------------------");

    void PrintMD5(byte[] buff) => ConsWriteLine("MD5: " + BitConverter.ToString(MD5.HashData(buff)));

    void SetBusy(bool busy)
    {
        foreach (var btn in new[] { BtnReadRom, BtnWriteRom, BtnReadRam, BtnWriteRam, BtnCartInfo })
            btn.IsEnabled = !busy;
    }

    /// <summary>Connects, runs <paramref name="body"/>, logs any failure to
    /// the console, and always disconnects — the original's per-click
    /// try/catch/disconnect shape. Connect and Dispose run off the UI thread;
    /// Dispose can stall in the guarded serial close.</summary>
    async Task WithSession(Func<FlashKitSession, Task> body)
    {
        SetBusy(true);
        FlashKitSession? session = null;
        try
        {
            session = await Task.Run(() => FlashKitSession.Connect(connector));
            await body(session);
        }
        catch (Exception x)
        {
            ConsWriteLine(x.Message);
        }
        finally
        {
            if (session != null) await Task.Run(session.Dispose);
            SetBusy(false);
        }
    }

    /// <summary>Progress sink that drives the bar and logs a line when the
    /// operation enters a new phase. Constructed on the UI thread so reports
    /// from the worker marshal back automatically.</summary>
    IProgress<OperationProgress> TrackProgress(Func<OperationPhase, string?>? phaseLabel = null)
    {
        OperationPhase? current = null;
        Progress.Value = 0;
        return new Progress<OperationProgress>(p =>
        {
            if (current != p.Phase)
            {
                current = p.Phase;
                if (phaseLabel?.Invoke(p.Phase) is string label) ConsWriteLine(label);
            }
            Progress.Maximum = Math.Max(1, p.Total);
            Progress.Value = p.Done;
        });
    }

    void OnCartInfo(object? sender, RoutedEventArgs e) => _ = CartInfoAsync();
    void OnReadRom(object? sender, RoutedEventArgs e) => _ = ReadRomAsync();
    void OnWriteRom(object? sender, RoutedEventArgs e) => _ = WriteRomAsync();
    void OnReadRam(object? sender, RoutedEventArgs e) => _ = ReadRamAsync();
    void OnWriteRam(object? sender, RoutedEventArgs e) => _ = WriteRamAsync();

    internal Task CartInfoAsync()
    {
        Separator();
        return WithSession(async session =>
        {
            var info = await Task.Run(session.GetInfo);
            ConsWriteLine("Connected to: " + session.PortName);
            ConsWriteLine("ROM name : " + info.RomName);
            ConsWriteLine("ROM size : " + info.RomBytes / 1024 + "K");
            ConsWriteLine(info.RamBytes < 1024
                ? "RAM size : " + info.RamBytes + "B"
                : "RAM size : " + info.RamBytes / 1024 + "K");
            if (info.HeaderRomBytes is int hdr && hdr != info.RomBytes)
                ConsWriteLine("Header ROM size : " + hdr / 1024 + "K");
        });
    }

    internal Task ReadRomAsync() => WithSession(async session =>
    {
        var romName = await Task.Run(session.GetRomName);
        if (await PickSavePath(romName + ".bin", RomFiles) is not string path) return;

        Separator();
        ConsWriteLine("Read ROM to " + path);
        var progress = TrackProgress();
        var rom = await Task.Run(() => session.ReadRom(p => progress.Report(p)));
        ConsWriteLine("ROM size : " + rom.Length / 1024 + "K");
        await File.WriteAllBytesAsync(path, rom);
        PrintMD5(rom);
        ConsWriteLine("OK");
    });

    internal Task WriteRomAsync() => WithSession(async session =>
    {
        if (await PickOpenPath(RomFiles) is not string path) return;

        Separator();
        var rom = await File.ReadAllBytesAsync(path);
        var progress = TrackProgress(phase => phase switch
        {
            OperationPhase.Erase => "Flash erase...",
            OperationPhase.Write => "Flash write...",
            OperationPhase.Verify => "Flash verify...",
            _ => null,
        });
        await Task.Run(() => session.WriteRom(rom, progress: p => progress.Report(p)));
        ConsWriteLine("OK");
    });

    internal Task ReadRamAsync() => WithSession(async session =>
    {
        var romName = await Task.Run(session.GetRomName);
        if (await PickSavePath(romName + ".srm", SaveFiles) is not string path) return;

        Separator();
        var ram = await Task.Run(session.ReadRam);
        ConsWriteLine("Read RAM to " + path);
        ConsWriteLine(ram.Length / 2 < 1024
            ? "RAM size : " + ram.Length / 2 + "B"
            : "RAM size : " + ram.Length / 2 / 1024 + "K");
        await File.WriteAllBytesAsync(path, ram);
        PrintMD5(ram);
        ConsWriteLine("OK");
    });

    internal Task WriteRamAsync() => WithSession(async session =>
    {
        if (await PickOpenPath(SaveFiles) is not string path) return;

        Separator();
        var ram = await File.ReadAllBytesAsync(path);
        var progress = TrackProgress(phase => phase switch
        {
            OperationPhase.Write => "Write RAM...",
            OperationPhase.Verify => "Verify...",
            _ => null,
        });
        int words = await Task.Run(() => session.WriteRam(ram, p => progress.Report(p)));
        ConsWriteLine(words + " words sent");
        PrintMD5(ram);
        ConsWriteLine("OK");
    });
}
