namespace FlashKit.Core.Tests;

public class PortDiscoveryTests
{
    static readonly string[] LinuxPorts =
    {
        "/dev/ttyS0", "/dev/ttyS1", "/dev/ttyS31", "/dev/ttyUSB0", "/dev/ttyACM0", "/dev/ttyACM1",
    };

    static readonly string[] MacPorts =
    {
        "/dev/cu.Bluetooth-Incoming-Port", "/dev/tty.usbmodem14201", "/dev/cu.usbmodem14201",
        "/dev/cu.usbserial-A50285BI", "/dev/tty.usbserial-A50285BI",
    };

    [Fact]
    public void linux_keeps_only_usb_serial_converters_never_cdc_acm()
    {
        Assert.Equal(
            new[] { "/dev/ttyUSB0" },
            PortDiscovery.FilterCandidates(LinuxPorts, HostOs.Linux));
    }

    [Fact]
    public void macos_keeps_only_cu_usbserial_never_tty_or_usbmodem()
    {
        // usbmodem is CDC-ACM (e.g. an ESP32's native USB port): probing it
        // resets the board and holds the port away from esptool.
        Assert.Equal(
            new[] { "/dev/cu.usbserial-A50285BI" },
            PortDiscovery.FilterCandidates(MacPorts, HostOs.MacOs));
    }

    [Fact]
    public void windows_keeps_com_ports()
    {
        Assert.Equal(
            new[] { "COM1", "COM3" },
            PortDiscovery.FilterCandidates(new[] { "COM1", "COM3", "LPT1" }, HostOs.Windows));
    }
}

public class DeviceConnectorTests
{
    sealed class UnopenablePort : ISerialPort
    {
        readonly Exception error;
        public UnopenablePort(Exception error) { this.error = error; }
        public string PortName => "BROKEN";
        public int ReadTimeout { get; set; }
        public int WriteTimeout { get; set; }
        public void Open() => throw error;
        public void Close() { }
        public void Write(byte[] buffer, int offset, int count) => throw error;
        public int Read(byte[] buffer, int offset, int count) => throw error;
        public int ReadByte() => throw error;
    }

    static DeviceConnector Connector(
        Dictionary<string, ISerialPort> ports, HostOs os = HostOs.Linux)
        => new(() => ports.Keys.ToArray(), name => ports[name], os);

    [Fact]
    public void connect_scans_candidates_and_finds_the_programmer()
    {
        var fake = new FakeFlashKitDevice(new byte[0x8000]);
        var connector = Connector(new()
        {
            ["/dev/ttyS0"] = new UnopenablePort(new IOException("phantom port")),
            ["/dev/ttyUSB0"] = new UnopenablePort(new IOException("open failed")),
            ["/dev/ttyUSB1"] = fake,
        });

        var (device, port) = connector.connect();

        Assert.Same(fake, port);
        // Probe leaves the long operational timeouts set, like the original.
        Assert.Equal(2000, fake.ReadTimeout);
        Assert.Equal(2000, fake.WriteTimeout);
        Assert.NotNull(device);
    }

    [Fact]
    public void connect_skips_serial_devices_that_answer_with_a_bad_id()
    {
        var notFlashKit = new ScriptedPort();
        notFlashKit.EnqueueReply(0x12, 0x34); // id bytes differ -> not a FlashKit
        var fake = new FakeFlashKitDevice(new byte[0x8000]);
        var connector = Connector(new()
        {
            ["/dev/ttyUSB0"] = notFlashKit,
            ["/dev/ttyUSB1"] = fake,
        });

        var (_, port) = connector.connect();

        Assert.Same(fake, port);
    }

    [Fact]
    public void connect_never_opens_cdc_acm_ports_while_scanning()
    {
        // An ESP32 cart's native USB port must not be probed: opening it
        // resets the board and holds the port away from esptool.
        var fake = new FakeFlashKitDevice(new byte[0x8000]);
        var connector = Connector(new()
        {
            ["/dev/cu.usbmodem1101"] = new UnopenablePort(new InvalidOperationException("must not open")),
            ["/dev/cu.usbserial-A50285BI"] = fake,
        }, HostOs.MacOs);

        var (_, port) = connector.connect();

        Assert.Same(fake, port);
    }

    [Fact]
    public void connect_uses_explicit_port_without_scanning()
    {
        var fake = new FakeFlashKitDevice(new byte[0x8000]);
        var connector = new DeviceConnector(
            () => throw new InvalidOperationException("must not enumerate"),
            name => name == "/dev/ttyUSB7" ? fake : throw new IOException("wrong port"),
            HostOs.Linux);

        var (_, port) = connector.connect("/dev/ttyUSB7");

        Assert.Same(fake, port);
    }

    [Fact]
    public void connect_surfaces_per_port_errors_and_permission_hint()
    {
        var connector = Connector(new()
        {
            ["/dev/ttyUSB0"] = new UnopenablePort(
                new UnauthorizedAccessException("Access to the port '/dev/ttyUSB0' is denied.")),
        });

        var x = Assert.Throws<DeviceNotFoundException>(() => connector.connect());

        Assert.Contains("/dev/ttyUSB0", x.Message);
        Assert.Contains("denied", x.Message);
        Assert.Contains("udev", x.Message);
        Assert.Single(x.PortErrors);
    }

    [Fact]
    public void connect_reports_when_no_candidate_ports_exist()
    {
        var connector = new DeviceConnector(
            () => new[] { "/dev/ttyS0", "/dev/ttyACM0" }, // filtered out on Linux
            _ => throw new IOException("must not open"),
            HostOs.Linux);

        var x = Assert.Throws<DeviceNotFoundException>(() => connector.connect());

        Assert.Contains("no candidate serial ports", x.Message);
        Assert.Contains("/dev/ttyUSB*", x.Message);
    }
}
