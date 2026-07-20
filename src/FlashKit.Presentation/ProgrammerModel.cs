using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FlashKit.Core;

namespace FlashKit.Presentation;

/// <summary>
/// Toolkit-agnostic presentation model shared by the GUI and TUI front-ends:
/// device/cart status display strings, the transaction log, auto-dump/
/// auto-write state, and the cart operations. Front-ends render its
/// INotifyPropertyChanged properties and call its methods; user decisions
/// go through the injected <see cref="IUserPrompts"/>.
///
/// Threading contract: all public members must be called on the UI thread
/// (the front-end's main/dispatcher thread). Device I/O runs on worker
/// threads internally; progress and completion marshal back via async
/// continuations and <see cref="Progress{T}"/>, so property-change events
/// always fire on the calling thread. The host drives polling by calling
/// <see cref="RefreshAsync"/> on its own timer.
///
/// One session is held for as long as the programmer stays reachable, NOT
/// one per operation like the original client: on macOS, closing the port
/// after a multi-MB flash write wedges in tcdrain and the guarded close
/// abandons it (see SystemSerialPort). In a long-lived process an abandoned
/// close keeps the descriptor held, making the port unopenable until the
/// adapter is replugged — so the close is deferred to Dispose / device
/// loss, where it is harmless.
/// </summary>
public sealed class ProgrammerModel : INotifyPropertyChanged, IDisposable
{
    readonly IUserPrompts prompts;
    readonly DeviceConnector? connector;
    readonly SemaphoreSlim deviceGate = new(1, 1);
    FlashKitSession? session;

    // Auto actions fire on cart insertion (the poll transition from no cart
    // to cart). Deliberately NOT keyed on cart identity: auto-write changes
    // the cart's contents, so identity-keying would see the just-written
    // cart as new and re-write it in a loop. cartProcessed clears on
    // removal, device loss, or any auto-setting change, so the seated cart
    // then counts as newly inserted; a failed action is not retried every
    // poll. Auto-dump and auto-write are mutually exclusive.
    string? autoDumpFolder;
    string? autoWriteFile;
    bool cartProcessed;

    public ProgrammerModel(IUserPrompts prompts, DeviceConnector? connector = null)
    {
        this.prompts = prompts;
        this.connector = connector;
    }

    public ObservableCollection<TransactionEntry> Log { get; } = new();

    // --- status display -------------------------------------------------

    bool devicePresent;
    string deviceStatus = "Looking for programmer...";
    bool cartPresent;
    string cartStatus = "—";
    string cartName = "—";
    string cartSystem = "—";
    string cartRomSize = "—";
    string cartRamSize = "—";
    string cartHeaderSize = "—";
    bool isBusy;

    public bool DevicePresent { get => devicePresent; private set => Set(ref devicePresent, value); }
    public string DeviceStatus { get => deviceStatus; private set => Set(ref deviceStatus, value); }
    public bool CartPresent { get => cartPresent; private set => Set(ref cartPresent, value); }
    public string CartStatus { get => cartStatus; private set => Set(ref cartStatus, value); }
    public string CartName { get => cartName; private set => Set(ref cartName, value); }
    public string CartSystem { get => cartSystem; private set => Set(ref cartSystem, value); }
    public string CartRomSize { get => cartRomSize; private set => Set(ref cartRomSize, value); }
    public string CartRamSize { get => cartRamSize; private set => Set(ref cartRamSize, value); }
    public string CartHeaderSize { get => cartHeaderSize; private set => Set(ref cartHeaderSize, value); }
    public bool IsBusy { get => isBusy; private set => Set(ref isBusy, value); }

    // --- auto-dump / auto-write ----------------------------------------

    bool autoDumpRomOn;
    bool autoDumpRamOn;
    bool autoWriteOn;
    string autoDumpFolderDisplay = "No folder chosen";
    string autoWriteFileDisplay = "No file chosen";

    public bool AutoDumpRomOn
    {
        get => autoDumpRomOn;
        private set { Set(ref autoDumpRomOn, value); Raise(nameof(CanToggleAutoWrite)); }
    }

    public bool AutoDumpRamOn
    {
        get => autoDumpRamOn;
        private set { Set(ref autoDumpRamOn, value); Raise(nameof(CanToggleAutoWrite)); }
    }

    public bool AutoWriteOn
    {
        get => autoWriteOn;
        private set { Set(ref autoWriteOn, value); Raise(nameof(CanToggleAutoDump)); }
    }

    public string AutoDumpFolderDisplay { get => autoDumpFolderDisplay; private set => Set(ref autoDumpFolderDisplay, value); }
    public string AutoWriteFileDisplay { get => autoWriteFileDisplay; private set => Set(ref autoWriteFileDisplay, value); }

    /// <summary>Auto-dump and auto-write must not run together (dumping a
    /// cart that is about to be erased, or writing one being archived, is
    /// never what the user meant).</summary>
    public bool CanToggleAutoDump => !AutoWriteOn;
    public bool CanToggleAutoWrite => !(AutoDumpRomOn || AutoDumpRamOn);

    /// <summary>Turning a toggle on (or off) re-arms the auto actions, so
    /// the currently seated cart counts as newly inserted on the next poll.
    /// Turning auto-dump on prompts for a folder if none is chosen yet;
    /// cancelling the prompt leaves the toggle off. Returns the effective
    /// state for the front-end to reflect.</summary>
    public async Task<bool> RequestAutoDumpRomAsync(bool on)
    {
        cartProcessed = false;
        on = on && await EnsureDumpFolder();
        AutoDumpRomOn = on;
        return on;
    }

    public async Task<bool> RequestAutoDumpRamAsync(bool on)
    {
        cartProcessed = false;
        on = on && await EnsureDumpFolder();
        AutoDumpRamOn = on;
        return on;
    }

    async Task<bool> EnsureDumpFolder()
    {
        if (autoDumpFolder != null) return true;
        autoDumpFolder = await prompts.PickFolder();
        AutoDumpFolderDisplay = autoDumpFolder ?? "No folder chosen";
        return autoDumpFolder != null;
    }

    /// <summary>Turning auto-write on first requires the destructive-action
    /// warning to be acknowledged, then a ROM file if none is chosen —
    /// declining either leaves the toggle off.</summary>
    public async Task<bool> RequestAutoWriteAsync(bool on)
    {
        cartProcessed = false;
        if (on)
        {
            if (!await prompts.ConfirmAutoWrite()) on = false;
            else if (autoWriteFile == null)
            {
                autoWriteFile = await prompts.PickOpenPath(PromptFileKind.RomImage);
                AutoWriteFileDisplay = autoWriteFile is string f ? Path.GetFileName(f) : "No file chosen";
                if (autoWriteFile == null) on = false;
            }
        }
        AutoWriteOn = on;
        return on;
    }

    public async Task ChooseAutoDumpFolderAsync()
    {
        if (await prompts.PickFolder() is not string folder) return;
        autoDumpFolder = folder;
        AutoDumpFolderDisplay = folder;
        cartProcessed = false;
    }

    public async Task ChooseAutoWriteFileAsync()
    {
        if (await prompts.PickOpenPath(PromptFileKind.RomImage) is not string file) return;
        autoWriteFile = file;
        AutoWriteFileDisplay = Path.GetFileName(file);
        cartProcessed = false;
    }

    bool AutoDumpEnabled => autoDumpFolder != null && (AutoDumpRomOn || AutoDumpRamOn);
    bool AutoWriteEnabled => autoWriteFile != null && AutoWriteOn;

    // --- polling ---------------------------------------------------------

    FlashKitSession EnsureSession() => session ??= FlashKitSession.Connect(connector);

    void DisposeSession()
    {
        var s = session;
        session = null;
        try { s?.Dispose(); }
        catch (Exception) { }
    }

    /// <summary>One poll: connect if needed, read cart info on the held
    /// session, update the status properties. Skips silently when an
    /// operation (or a previous poll) holds the device.</summary>
    public async Task RefreshAsync()
    {
        if (!await deviceGate.WaitAsync(0)) return;
        CartInfo? insertedCart = null;
        try
        {
            var (port, info) = await Task.Run(() =>
            {
                var s = EnsureSession();
                return (s.PortName, s.GetInfo());
            });
            ShowDeviceState(port);
            ShowCartState(info);
            if (!info.CartDetected)
            {
                cartProcessed = false;
            }
            else if (!cartProcessed)
            {
                cartProcessed = true;
                if (AutoDumpEnabled || AutoWriteEnabled) insertedCart = info;
            }
        }
        catch (DeviceNotFoundException x)
        {
            ShowDeviceState(null);
            DeviceStatus = DescribeDeviceError(x);
            ShowCartState(null);
            cartProcessed = false;
        }
        catch (Exception)
        {
            // The held session died mid-poll (cable pulled): drop it and
            // report the device gone; the next poll reconnects if it's back.
            await Task.Run(DisposeSession);
            ShowDeviceState(null);
            ShowCartState(null);
            cartProcessed = false;
        }
        finally
        {
            deviceGate.Release();
        }
        // Outside the gate: the operations take it themselves via RunOperation.
        if (insertedCart != null) await AutoProcessAsync(insertedCart);
    }

    async Task AutoProcessAsync(CartInfo info)
    {
        if (AutoDumpEnabled && autoDumpFolder is string folder)
        {
            string name = DisplayName(info.RomName);
            string romExt = info.Is32X ? ".32x" : ".bin";
            if (AutoDumpRomOn)
                await RunOperation("Auto-dump ROM", (s, entry) => DumpRomTo(s, entry, UniquePath(folder, name + romExt)));
            if (AutoDumpRamOn && info.RamBytes > 0)
                await RunOperation("Auto-dump RAM", (s, entry) => DumpRamTo(s, entry, UniquePath(folder, name + ".srm")));
        }
        if (AutoWriteEnabled && autoWriteFile is string file)
        {
            await RunOperation("Auto-write ROM", (s, entry) => WriteRomFrom(s, entry, file));
        }
    }

    /// <summary>Never overwrite an earlier dump: append " (2)", " (3)", …
    /// until the name is free.</summary>
    static string UniquePath(string folder, string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        string path = Path.Combine(folder, fileName);
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{stem} ({n}){ext}");
        return path;
    }

    /// <summary>A permission-denied port must not masquerade as an absent
    /// programmer — that cost a real debugging session. Everything else
    /// stays the plain "not detected" (genuinely unplugged, or ports that
    /// answered with the wrong ID).</summary>
    static string DescribeDeviceError(DeviceNotFoundException x)
    {
        string? denied = x.PortErrors.FirstOrDefault(e =>
            e.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
            e.Contains("permission", StringComparison.OrdinalIgnoreCase));
        if (denied == null) return "No programmer detected";
        string port = denied.Split(':')[0];
        return $"No programmer: permission denied on {port} — add your user "
             + "to the serial group ('dialout', 'uucp' on Arch) and log in again";
    }

    void ShowDeviceState(string? port)
    {
        DevicePresent = port != null;
        DeviceStatus = port != null
            ? "Programmer connected on " + port
            : "No programmer detected";
    }

    void ShowCartState(CartInfo? info)
    {
        bool present = info?.CartDetected == true;
        CartPresent = present;
        CartStatus = present ? "Cartridge inserted"
            : info != null ? "No cartridge" : "—";
        CartName = present ? DisplayName(info!.RomName) : "—";
        CartSystem = present ? info!.SystemName : "—";
        CartRomSize = present ? FormatKb(info!.RomBytes) : "—";
        CartRamSize = present ? FormatSize(info!.RamBytes) : "—";
        CartHeaderSize = present && info!.HeaderRomBytes is int hdr ? FormatKb(hdr) : "—";
    }

    // Header names are space-padded ("SONIC THE          HEDGEHOG 3");
    // collapse the runs for display, like GetRomName does for filenames.
    static string DisplayName(string romName) =>
        string.Join(' ', romName.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    static string FormatKb(int bytes) => bytes / 1024 + "K";
    static string FormatSize(int bytes) => bytes < 1024 ? bytes + "B" : bytes / 1024 + "K";
    static string Md5(byte[] buff) => BitConverter.ToString(MD5.HashData(buff));

    // --- operations ------------------------------------------------------

    /// <summary>Runs one cart operation as a transaction-log entry on the
    /// held session (connecting first if needed) and reports any failure on
    /// the entry. The session stays open afterwards — no per-operation close
    /// (see the class comment) — unless the failure looks like a dead serial
    /// link, in which case it is dropped for the poller to re-establish.
    /// Waits out an in-flight poll before touching the port; connecting and
    /// disposing run off the UI thread (a dispose can stall in the guarded
    /// serial close).</summary>
    async Task RunOperation(string title, Func<FlashKitSession, TransactionEntry, Task> body)
    {
        var entry = new TransactionEntry(title);
        Log.Insert(0, entry);
        IsBusy = true;
        await deviceGate.WaitAsync();
        try
        {
            entry.Status = "Connecting...";
            var s = await Task.Run(EnsureSession);
            await body(s, entry);
        }
        catch (Exception x)
        {
            entry.Fail(x.Message);
            if (x is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
                await Task.Run(DisposeSession);
        }
        finally
        {
            deviceGate.Release();
            IsBusy = false;
        }
    }

    /// <summary>Progress sink that drives the entry's progress bar and sets
    /// its status when the operation enters a new phase. Constructed on the
    /// UI thread so reports from the worker marshal back automatically.</summary>
    static IProgress<OperationProgress> TrackProgress(TransactionEntry entry,
        Func<OperationPhase, string?>? phaseLabel = null)
    {
        OperationPhase? current = null;
        return new Progress<OperationProgress>(p =>
        {
            if (current != p.Phase)
            {
                current = p.Phase;
                if (phaseLabel?.Invoke(p.Phase) is string label) entry.Status = label;
            }
            entry.ProgressMax = Math.Max(1, p.Total);
            entry.ProgressValue = p.Done;
        });
    }

    public Task ReadRomAsync() => RunOperation("Read ROM", async (session, entry) =>
    {
        var suggested = await Task.Run(session.SuggestedRomFileName);
        if (await prompts.PickSavePath(suggested, PromptFileKind.RomImage) is not string path)
        {
            entry.Cancel();
            return;
        }
        await DumpRomTo(session, entry, FixAppendedExtension(path, suggested));
    });

    /// <summary>
    /// Some native save pickers append the file-type's default extension on
    /// top of a suggested name that already has one (a 32X dump suggested as
    /// "GAME.32x" comes back "GAME.32x.bin"). When the returned path is the
    /// intended extension followed by one extra, drop the extra; a genuinely
    /// different name the user typed is left untouched.
    /// </summary>
    internal static string FixAppendedExtension(string path, string suggestedName)
    {
        string want = Path.GetExtension(suggestedName);          // ".32x"
        string appended = Path.GetExtension(path);               // ".bin" if appended
        if (want.Length == 0 || appended.Length == 0) return path;
        if (!path.EndsWith(want, StringComparison.OrdinalIgnoreCase)
            && path.EndsWith(want + appended, StringComparison.OrdinalIgnoreCase))
            return path[..^appended.Length];
        return path;
    }

    async Task DumpRomTo(FlashKitSession session, TransactionEntry entry, string path)
    {
        entry.Detail = path;
        entry.Status = "Reading ROM...";
        var progress = TrackProgress(entry);
        var rom = await Task.Run(() => session.ReadRom(p => progress.Report(p)));
        await File.WriteAllBytesAsync(path, rom);
        entry.Succeed($"OK — {rom.Length / 1024}K, MD5 {Md5(rom)}");
    }

    public Task WriteRomAsync() => RunOperation("Write ROM", async (session, entry) =>
    {
        if (await prompts.PickOpenPath(PromptFileKind.RomImage) is not string path)
        {
            entry.Cancel();
            return;
        }
        await WriteRomFrom(session, entry, path);
    });

    async Task WriteRomFrom(FlashKitSession session, TransactionEntry entry, string path)
    {
        entry.Detail = path;
        var rom = await File.ReadAllBytesAsync(path);
        var progress = TrackProgress(entry, phase => phase switch
        {
            OperationPhase.Erase => "Flash erase...",
            OperationPhase.Write => "Flash write...",
            OperationPhase.Verify => "Flash verify...",
            _ => null,
        });
        await Task.Run(() => session.WriteRom(rom, progress: p => progress.Report(p)));
        entry.Succeed($"OK — {rom.Length / 1024}K written, MD5 {Md5(rom)}");
    }

    public Task ReadRamAsync() => RunOperation("Read RAM", async (session, entry) =>
    {
        var romName = await Task.Run(session.GetRomName);
        string suggested = romName + ".srm";
        if (await prompts.PickSavePath(suggested, PromptFileKind.SaveRam) is not string path)
        {
            entry.Cancel();
            return;
        }
        await DumpRamTo(session, entry, FixAppendedExtension(path, suggested));
    });

    async Task DumpRamTo(FlashKitSession session, TransactionEntry entry, string path)
    {
        entry.Detail = path;
        entry.Status = "Reading RAM...";
        var ram = await Task.Run(session.ReadRam);
        await File.WriteAllBytesAsync(path, ram);
        entry.Succeed($"OK — {FormatSize(ram.Length / 2)}, MD5 {Md5(ram)}");
    }

    public Task WriteRamAsync() => RunOperation("Write RAM", async (session, entry) =>
    {
        if (await prompts.PickOpenPath(PromptFileKind.SaveRam) is not string path)
        {
            entry.Cancel();
            return;
        }
        entry.Detail = path;
        var ram = await File.ReadAllBytesAsync(path);
        var progress = TrackProgress(entry, phase => phase switch
        {
            OperationPhase.Write => "Write RAM...",
            OperationPhase.Verify => "Verify...",
            _ => null,
        });
        int words = await Task.Run(() => session.WriteRam(ram, p => progress.Report(p)));
        entry.Succeed($"OK — {words} words sent, MD5 {Md5(ram)}");
    });

    public void Dispose() => DisposeSession();

    // --- INotifyPropertyChanged ------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }

    void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
