using FlashKit.Core;

namespace flashkit_md;

/// <summary>
/// Command-line front-end. All device workflows live in
/// <see cref="FlashKitSession"/> (shared with future TUI/GUI front-ends);
/// this class only parses arguments, does file I/O, and renders output.
/// </summary>
public sealed class CliApp
{
    const string Usage = """
        FlashKit MD — Sega Mega Drive / Genesis cart programmer client
        usage: flashkit-md [--port <serial-port>] <command> [file]
               flashkit-md --version
        commands:
          info               print cart ROM name/size and save-RAM size
          read-rom [file]    dump cart ROM (default file: <ROM name>.bin)
              --trust-header dump the size the ROM header declares instead
                             of the mirror-probed size (useful on flash
                             carts, where probing can misjudge the extent)
              --apply-patch <ips>
                             apply an IPS patch to the dump before saving
              --create-patch <base>
                             diff the dump against <base> and write an IPS
                             patch instead of the ROM (default: <name>.ips)
          write-rom <file>   erase flash cart and write ROM image
              --full-erase   erase the entire 4 MB chip first, so no stale
                             data above the image shows up as ghost saves
                             (only for carts with a full-size 4 MB chip)
              --patch <ips>  apply an IPS patch to the image before flashing
              --no-flash-check
                             skip the CFI flash-presence check that write-rom
                             and bake-save run before erasing
          read-ram [file]    dump save RAM (default file: <ROM name>.srm)
          write-ram <file>   write save RAM from file
          bake-save <file>   program a save image into flash at the save
                             window (0x200000) of an SRAM-less flash cart:
                             the game sees the saves read-only, so they
                             survive every power cycle but cannot be
                             overwritten in-game (needs a 4 MB chip)
        """;

    readonly DeviceConnector connector;
    readonly TextWriter con;
    readonly TextWriter err;

    public CliApp(DeviceConnector connector, TextWriter stdout, TextWriter stderr)
    {
        this.connector = connector;
        con = stdout;
        err = stderr;
    }

    public int Run(string[] args)
    {
        string? portName = null;
        string? command = null;
        string? file = null;
        string? patchFile = null;
        string? applyPatchFile = null;
        string? createPatchBase = null;
        bool fullErase = false;
        bool trustHeader = false;
        bool noFlashCheck = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--version")
            {
                con.WriteLine("flashkit-md " + VersionInfo.ClientVersion);
                return 0;
            }

            if (arg is "--help" or "-h")
            {
                con.WriteLine(Usage);
                return 0;
            }

            // Value flags: consume the following argument.
            string? flag = arg is "--port" or "--patch" or "--apply-patch" or "--create-patch" ? arg : null;
            if (flag != null)
            {
                if (i + 1 >= args.Length)
                {
                    err.WriteLine(flag + " requires a value");
                    return 2;
                }
                string value = args[++i];
                switch (flag)
                {
                    case "--port": portName = value; break;
                    case "--patch": patchFile = value; break;
                    case "--apply-patch": applyPatchFile = value; break;
                    case "--create-patch": createPatchBase = value; break;
                }
            }
            else if (arg == "--full-erase") fullErase = true;
            else if (arg == "--trust-header") trustHeader = true;
            else if (arg == "--no-flash-check") noFlashCheck = true;
            else if (command == null) command = arg;
            else if (file == null) file = arg;
            else
            {
                err.WriteLine("unexpected argument: " + arg);
                return 2;
            }
        }

        if (fullErase && command != "write-rom")
        {
            err.WriteLine("--full-erase only applies to write-rom");
            return 2;
        }

        if (trustHeader && command != "read-rom")
        {
            err.WriteLine("--trust-header only applies to read-rom");
            return 2;
        }

        if (noFlashCheck && command is not ("write-rom" or "bake-save"))
        {
            err.WriteLine("--no-flash-check only applies to write-rom and bake-save");
            return 2;
        }

        if (patchFile != null && command != "write-rom")
        {
            err.WriteLine("--patch only applies to write-rom");
            return 2;
        }

        if ((applyPatchFile != null || createPatchBase != null) && command != "read-rom")
        {
            err.WriteLine("--apply-patch and --create-patch only apply to read-rom");
            return 2;
        }

        if (applyPatchFile != null && createPatchBase != null)
        {
            err.WriteLine("--apply-patch and --create-patch are mutually exclusive");
            return 2;
        }

        if (command is "write-rom" or "write-ram" or "bake-save" && file == null)
        {
            err.WriteLine(command + " requires a file");
            return 2;
        }

        try
        {
            switch (command)
            {
                case "info":
                    return WithSession(portName, Info);
                case "read-rom":
                    return WithSession(portName, session => ReadRom(session, file, trustHeader, applyPatchFile, createPatchBase));
                case "write-rom":
                    return WithSession(portName, session => WriteRom(session, file!, fullErase, noFlashCheck, patchFile));
                case "read-ram":
                    return WithSession(portName, session => ReadRam(session, file));
                case "write-ram":
                    return WithSession(portName, session => WriteRam(session, file!));
                case "bake-save":
                    return WithSession(portName, session => BakeSave(session, file!, noFlashCheck));
                default:
                    err.WriteLine(Usage);
                    return 2;
            }
        }
        catch (FlashChipNotFoundException x)
        {
            err.WriteLine(x.Message);
            err.WriteLine("Use --no-flash-check if you are sure the cart is writable.");
            return 1;
        }
        catch (Exception x)
        {
            err.WriteLine(x.Message);
            return 1;
        }
    }

    int WithSession(string? portName, Action<FlashKitSession> body)
    {
        using var session = FlashKitSession.Connect(connector, portName);
        con.WriteLine("Connected to: " + session.PortName);
        body(session);
        return 0;
    }

    void Info(FlashKitSession session)
    {
        var info = session.GetInfo();
        con.WriteLine("ROM name : " + info.RomName);
        con.WriteLine("System   : " + info.SystemName);
        con.WriteLine("Region   : " + info.Region);
        con.WriteLine("ROM size : " + info.RomBytes / 1024 + "K");
        if (info.HeaderRomBytes is int h && h != info.RomBytes)
            con.WriteLine("Header ROM size : " + h / 1024 + "K (read-rom --trust-header dumps this extent)");
        PrintRamSize(info.RamBytes);
    }

    void PrintRamSize(int ram_size)
    {
        if (ram_size < 1024)
        {
            con.WriteLine("RAM size : " + ram_size + "B");
        }
        else
        {
            con.WriteLine("RAM size : " + ram_size / 1024 + "K");
        }
    }

    void ReadRom(FlashKitSession session, string? file, bool trustHeader,
        string? applyPatchFile, string? createPatchBase)
    {
        int? size = null;
        if (trustHeader)
        {
            size = session.ReadHeaderRomSize();
            if (size == null) con.WriteLine("Header declares no plausible ROM size; using probed size");
        }

        if (createPatchBase != null)
        {
            // Dump the cart and write the diff against the base as an .ips;
            // the product is the patch, so the raw dump is not saved.
            string path = file ?? session.GetRomName() + ".ips";
            con.WriteLine("Read ROM and diff against " + createPatchBase);
            byte[] rom = session.ReadRom(RenderProgress(), size);
            byte[] baseRom = File.ReadAllBytes(createPatchBase);
            byte[] patch = IpsPatch.Create(baseRom, rom);
            File.WriteAllBytes(path, patch);
            con.WriteLine($"Wrote IPS patch {path} ({patch.Length} bytes)");
            con.WriteLine("OK");
            return;
        }

        string outPath = file ?? session.SuggestedRomFileName();
        con.WriteLine("Read ROM to " + outPath);
        byte[] dump = session.ReadRom(RenderProgress(), size);
        con.WriteLine("ROM size : " + dump.Length / 1024 + "K" + (size != null ? " (from header)" : ""));
        if (applyPatchFile != null)
        {
            dump = IpsPatch.Apply(dump, File.ReadAllBytes(applyPatchFile));
            con.WriteLine("Applied IPS patch " + applyPatchFile);
        }
        File.WriteAllBytes(outPath, dump);
        PrintHashes(dump);
        con.WriteLine("OK");
    }

    void WriteRom(FlashKitSession session, string file, bool fullErase, bool noFlashCheck, string? patchFile)
    {
        byte[] image = File.ReadAllBytes(file);
        if (patchFile != null)
        {
            image = IpsPatch.Apply(image, File.ReadAllBytes(patchFile));
            con.WriteLine("Applied IPS patch " + patchFile);
        }
        session.WriteRom(image, fullErase, skipFlashCheck: noFlashCheck, progress: RenderProgress(phase => phase switch
        {
            OperationPhase.Erase => fullErase ? "Flash erase (full chip)..." : "Flash erase...",
            OperationPhase.Write => "Flash write...",
            _ => "Flash verify...",
        }));
        PrintHashes(image);
        con.WriteLine("OK");
    }

    void ReadRam(FlashKitSession session, string? file)
    {
        string path = file ?? session.GetRomName() + ".srm";
        byte[] ram = session.ReadRam();
        con.WriteLine("Read RAM to " + path);
        PrintRamSize(ram.Length / 2);
        File.WriteAllBytes(path, ram);
        PrintHashes(ram);
        con.WriteLine("OK");
    }

    void WriteRam(FlashKitSession session, string file)
    {
        con.WriteLine("Write RAM...");
        byte[] ram = File.ReadAllBytes(file);
        int words = session.WriteRam(ram, RenderProgress(phase =>
            phase == OperationPhase.Verify ? "Verify..." : null, percentages: false));
        con.WriteLine("" + words + " words sent");
        PrintHashes(ram);
        con.WriteLine("OK");
    }

    void BakeSave(FlashKitSession session, string file, bool noFlashCheck)
    {
        byte[] srm = File.ReadAllBytes(file);
        con.WriteLine("Bake save into flash at 0x200000...");
        session.BakeSave(srm, skipFlashCheck: noFlashCheck, progress: RenderProgress(phase =>
            phase == OperationPhase.Verify ? "Verify..." : null));
        PrintHashes(srm);
        con.WriteLine("Note: baked saves are a read-only snapshot; in-game saving will not persist.");
        con.WriteLine("OK");
    }

    /// <summary>
    /// Builds a progress callback that prints a label on each phase
    /// transition (when <paramref name="phaseLabel"/> returns one) and a
    /// percentage stream on stderr.
    /// </summary>
    Action<OperationProgress> RenderProgress(
        Func<OperationPhase, string?>? phaseLabel = null, bool percentages = true)
    {
        OperationPhase? last = null;
        return p =>
        {
            if (p.Phase != last)
            {
                last = p.Phase;
                if (phaseLabel?.Invoke(p.Phase) is string label) con.WriteLine(label);
            }
            if (percentages && p.Total > 0 && p.Done > 0)
            {
                err.Write($"\r{Math.Min(p.Done, p.Total) * 100 / p.Total}%");
                if (p.Done >= p.Total) err.Write("\r");
            }
        };
    }

    void PrintHashes(byte[] buff)
    {
        con.WriteLine("CRC32: " + RomHash.Crc32(buff));
        con.WriteLine("MD5: " + RomHash.Md5(buff));
        con.WriteLine("SHA-1: " + RomHash.Sha1(buff));
    }
}
