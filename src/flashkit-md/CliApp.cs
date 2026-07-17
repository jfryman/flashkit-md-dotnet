using System.Security.Cryptography;
using FlashKit.Core;

namespace flashkit_md;

/// <summary>
/// Command-line front-end replacing the original WinForms window. Each
/// command ports the corresponding Form1 button handler; file dialogs
/// become file arguments and the console textbox becomes stdout.
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
          read-ram [file]    dump save RAM (default file: <ROM name>.srm)
          write-ram <file>   write save RAM from file
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
            else if (command == null) command = args[i];
            else if (file == null) file = args[i];
            else
            {
                err.WriteLine("unexpected argument: " + args[i]);
                return 2;
            }
        }

        try
        {
            switch (command)
            {
                case "info":
                    return WithDevice(portName, delay: 1, (device, cart) => Info(cart));
                case "read-rom":
                    return WithDevice(portName, delay: 1, (device, cart) => ReadRom(device, cart, file));
                case "write-rom":
                    if (file == null) { err.WriteLine("write-rom requires a file"); return 2; }
                    return WithDevice(portName, delay: 0, (device, cart) => WriteRom(device, file));
                case "read-ram":
                    return WithDevice(portName, delay: 1, (device, cart) => ReadRam(device, cart, file));
                case "write-ram":
                    if (file == null) { err.WriteLine("write-ram requires a file"); return 2; }
                    return WithDevice(portName, delay: 1, (device, cart) => WriteRam(device, cart, file));
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

    int WithDevice(string? portName, int delay, Action<Device, Cart> body)
    {
        var (device, port) = connector.connect(portName);
        try
        {
            con.WriteLine("Connected to: " + device.getPortName());
            device.setDelay(delay);
            body(device, new Cart(device));
            return 0;
        }
        finally
        {
            try { port.Close(); }
            catch (Exception) { }
        }
    }

    void Info(Cart cart)
    {
        con.WriteLine("ROM name : " + cart.getRomName());
        con.WriteLine("ROM size : " + cart.getRomSize() / 1024 + "K");
        PrintRamSize(cart.getRamSize());
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

    void ReadRom(Device device, Cart cart, string? file)
    {
        string path = file ?? cart.getRomName() + ".bin";
        int rom_size = cart.getRomSize();
        con.WriteLine("Read ROM to " + path);
        con.WriteLine("ROM size : " + rom_size / 1024 + "K");

        var rom = new byte[rom_size];
        device.writeWord(0xA13000, 0x0000);
        device.setAddr(0);
        for (int i = 0; i < rom_size; i += 32768)
        {
            device.read(rom, i, Math.Min(32768, rom_size - i));
            Progress(i + 32768, rom_size);
        }

        // File.WriteAllBytes truncates; the original's File.OpenWrite left
        // stale bytes when overwriting a larger existing dump.
        File.WriteAllBytes(path, rom);
        PrintMD5(rom);
        con.WriteLine("OK");
    }

    void ReadRam(Device device, Cart cart, string? file)
    {
        string path = file ?? cart.getRomName() + ".srm";
        int ram_size = cart.getRamSize();
        if (ram_size == 0) throw new Exception("RAM is not detected");
        con.WriteLine("Read RAM to " + path);
        PrintRamSize(ram_size);

        device.writeWord(0xA13000, 0xffff);
        device.setAddr(0x200000);
        var ram = new byte[ram_size * 2];
        device.read(ram, 0, ram.Length);

        File.WriteAllBytes(path, ram);
        PrintMD5(ram);
        con.WriteLine("OK");
    }

    void WriteRam(Device device, Cart cart, string file)
    {
        con.WriteLine("Write RAM...");
        byte[] ram = File.ReadAllBytes(file);

        int ram_size = cart.getRamSize();
        if (ram_size == 0) throw new Exception("RAM is not detected");

        ram_size *= 2;
        int copy_len = ram.Length;
        if (ram_size < copy_len) copy_len = ram_size;
        if (copy_len % 2 != 0) copy_len--;
        device.writeWord(0xA13000, 0xffff);
        device.setAddr(0x200000);
        device.write(ram, 0, copy_len);

        con.WriteLine("Verify...");
        var ram2 = new byte[copy_len];
        device.setAddr(0x200000);
        device.read(ram2, 0, copy_len);
        for (int i = 0; i < copy_len; i++)
        {
            if (i % 2 == 0) continue; // save RAM is 8-bit, on odd bytes
            if (ram[i] != ram2[i]) throw new Exception("Verify error at " + i);
        }

        con.WriteLine("" + copy_len / 2 + " words sent");
        PrintMD5(ram);
        con.WriteLine("OK");
    }

    void WriteRom(Device device, string file)
    {
        try
        {
            byte[] src = File.ReadAllBytes(file);
            int rom_size = src.Length;
            if (rom_size % 65536 != 0) rom_size = rom_size / 65536 * 65536 + 65536;
            if (rom_size > 0x400000) rom_size = 0x400000;
            var rom = new byte[rom_size];
            Array.Copy(src, rom, Math.Min(src.Length, rom_size));

            con.WriteLine("Flash erase...");
            device.flashResetByPass();
            for (int i = 0; i < rom_size; i += 65536)
            {
                device.flashErase(i);
                Progress(i + 65536, rom_size);
            }

            con.WriteLine("Flash write...");
            device.flashUnlockBypass();
            device.setAddr(0);
            for (int i = 0; i < rom_size; i += 4096)
            {
                device.flashWrite(rom, i, 4096);
                Progress(i + 4096, rom_size);
            }
            device.flashResetByPass();

            con.WriteLine("Flash verify...");
            var rom2 = new byte[rom_size];
            device.setAddr(0);
            for (int i = 0; i < rom_size; i += 4096)
            {
                device.read(rom2, i, 4096);
                Progress(i + 4096, rom_size);
            }
            for (int i = 0; i < rom_size; i++)
            {
                if (rom[i] != rom2[i]) throw new Exception("Verify error at " + i);
            }

            con.WriteLine("OK");
        }
        catch (Exception)
        {
            try { device.flashResetByPass(); }
            catch (Exception) { }
            throw;
        }
    }

    void Progress(long done, long total)
    {
        if (done > total) done = total;
        err.Write($"\r{done * 100 / total}%");
        if (done >= total) err.Write("\r");
    }

    void PrintMD5(byte[] buff)
    {
        con.WriteLine("MD5: " + BitConverter.ToString(MD5.HashData(buff)));
    }
}
