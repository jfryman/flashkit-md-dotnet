using System.IO.Ports;

namespace FlashKit.Core;

/// <summary>
/// Thrown when no FlashKit programmer answers on any candidate port.
/// Carries the per-port failure reasons the original client swallowed —
/// on Linux a permission error must not masquerade as "device not detected".
/// </summary>
public sealed class DeviceNotFoundException : Exception
{
    public IReadOnlyList<string> PortErrors { get; }

    public DeviceNotFoundException(string message, IReadOnlyList<string> portErrors)
        : base(message)
    {
        PortErrors = portErrors;
    }
}

/// <summary>
/// Finds and connects to the FlashKit programmer. Probe logic (ID handshake,
/// timeouts, delay reset) is ported from the original Device.connect(); port
/// enumeration and opening are injectable so tests run without hardware.
/// </summary>
public sealed class DeviceConnector
{
    readonly Func<string[]> enumeratePorts;
    readonly Func<string, ISerialPort> openPort;
    readonly HostOs os;

    public DeviceConnector(
        Func<string[]>? enumeratePorts = null,
        Func<string, ISerialPort>? portFactory = null,
        HostOs? os = null)
    {
        this.enumeratePorts = enumeratePorts ?? SerialPort.GetPortNames;
        this.openPort = portFactory ?? (name => new SystemSerialPort(name));
        this.os = os ?? PortDiscovery.CurrentOs();
    }

    /// <summary>
    /// Connects to the programmer on <paramref name="portName"/>, or scans
    /// the OS-appropriate candidates when null. The caller owns the returned
    /// port and must Close() it.
    /// </summary>
    public (Device device, ISerialPort port) connect(string? portName = null)
    {
        string[] candidates = portName != null
            ? new[] { portName }
            : PortDiscovery.FilterCandidates(enumeratePorts(), os);

        var errors = new List<string>();

        foreach (string name in candidates)
        {
            ISerialPort? port = null;
            try
            {
                port = openPort(name);
                port.Open();
                port.ReadTimeout = 200;
                port.WriteTimeout = 200;
                var device = new Device(port);
                int id = device.getID();
                if ((id & 0xff) == (id >> 8) && id != 0)
                {
                    device.setDelay(0);
                    port.WriteTimeout = 2000;
                    port.ReadTimeout = 2000;
                    return (device, port);
                }
                errors.Add($"{name}: not a FlashKit device (id 0x{id:X4})");
                port.Close();
            }
            catch (Exception x)
            {
                errors.Add($"{name}: {x.Message}");
                try { port?.Close(); }
                catch (Exception) { }
            }
        }

        if (candidates.Length == 0)
        {
            errors.Add("no candidate serial ports found (" + os switch
            {
                HostOs.Linux => "expected /dev/ttyUSB*; is the programmer plugged in?",
                HostOs.MacOs => "expected /dev/cu.usbserial*; is the programmer plugged in?",
                _ => "is the programmer plugged in?",
            } + ")");
        }

        string message = "Device is not detected:\n  " + string.Join("\n  ", errors);
        if (os == HostOs.Linux && errors.Any(e =>
                e.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
                e.Contains("permission", StringComparison.OrdinalIgnoreCase)))
        {
            message += "\nHint: add your user to the serial group (usually 'dialout', 'uucp' on Arch) or install a udev rule.";
        }

        throw new DeviceNotFoundException(message, errors);
    }
}
