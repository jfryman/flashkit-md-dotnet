using System.Security.Cryptography;
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
        commands:
          info               print cart ROM name/size and save-RAM size
          read-rom [file]    dump cart ROM (default file: <ROM name>.bin)
          write-rom <file>   erase flash cart and write ROM image
              --full-erase   erase the entire 4 MB chip first, so no stale
                             data above the image shows up as ghost saves
                             (only for carts with a full-size 4 MB chip)
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
        bool fullErase = false;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port")
            {
                if (i + 1 >= args.Length)
                {
                    err.WriteLine("--port requires a value");
                    return 2;
                }
                portName = args[++i];
            }
            else if (args[i] == "--full-erase") fullErase = true;
            else if (command == null) command = args[i];
            else if (file == null) file = args[i];
            else
            {
                err.WriteLine("unexpected argument: " + args[i]);
                return 2;
            }
        }

        if (fullErase && command != "write-rom")
        {
            err.WriteLine("--full-erase only applies to write-rom");
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
                    return WithSession(portName, session => ReadRom(session, file));
                case "write-rom":
                    return WithSession(portName, session => WriteRom(session, file!, fullErase));
                case "read-ram":
                    return WithSession(portName, session => ReadRam(session, file));
                case "write-ram":
                    return WithSession(portName, session => WriteRam(session, file!));
                case "bake-save":
                    return WithSession(portName, session => BakeSave(session, file!));
                default:
                    err.WriteLine(Usage);
                    return 2;
            }
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
        con.WriteLine("ROM size : " + info.RomBytes / 1024 + "K");
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

    void ReadRom(FlashKitSession session, string? file)
    {
        string path = file ?? session.GetRomName() + ".bin";
        con.WriteLine("Read ROM to " + path);
        byte[] rom = session.ReadRom(RenderProgress());
        con.WriteLine("ROM size : " + rom.Length / 1024 + "K");
        File.WriteAllBytes(path, rom);
        PrintMD5(rom);
        con.WriteLine("OK");
    }

    void WriteRom(FlashKitSession session, string file, bool fullErase)
    {
        byte[] image = File.ReadAllBytes(file);
        session.WriteRom(image, fullErase, RenderProgress(phase => phase switch
        {
            OperationPhase.Erase => fullErase ? "Flash erase (full chip)..." : "Flash erase...",
            OperationPhase.Write => "Flash write...",
            _ => "Flash verify...",
        }));
        con.WriteLine("OK");
    }

    void ReadRam(FlashKitSession session, string? file)
    {
        string path = file ?? session.GetRomName() + ".srm";
        byte[] ram = session.ReadRam();
        con.WriteLine("Read RAM to " + path);
        PrintRamSize(ram.Length / 2);
        File.WriteAllBytes(path, ram);
        PrintMD5(ram);
        con.WriteLine("OK");
    }

    void WriteRam(FlashKitSession session, string file)
    {
        con.WriteLine("Write RAM...");
        byte[] ram = File.ReadAllBytes(file);
        int words = session.WriteRam(ram, RenderProgress(phase =>
            phase == OperationPhase.Verify ? "Verify..." : null, percentages: false));
        con.WriteLine("" + words + " words sent");
        PrintMD5(ram);
        con.WriteLine("OK");
    }

    void BakeSave(FlashKitSession session, string file)
    {
        byte[] srm = File.ReadAllBytes(file);
        con.WriteLine("Bake save into flash at 0x200000...");
        session.BakeSave(srm, RenderProgress(phase =>
            phase == OperationPhase.Verify ? "Verify..." : null));
        PrintMD5(srm);
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

    void PrintMD5(byte[] buff)
    {
        con.WriteLine("MD5: " + BitConverter.ToString(MD5.HashData(buff)));
    }
}
