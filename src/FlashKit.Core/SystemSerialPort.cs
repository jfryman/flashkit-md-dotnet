using System.IO.Ports;

namespace FlashKit.Core;

/// <summary>Adapter from <see cref="ISerialPort"/> to <c>System.IO.Ports.SerialPort</c>.</summary>
public sealed class SystemSerialPort : ISerialPort, IDisposable
{
    readonly SerialPort inner;

    public SystemSerialPort(string portName)
    {
        inner = new SerialPort(portName);
    }

    public string PortName => inner.PortName;

    public int ReadTimeout
    {
        get => inner.ReadTimeout;
        set => inner.ReadTimeout = value;
    }

    public int WriteTimeout
    {
        get => inner.WriteTimeout;
        set => inner.WriteTimeout = value;
    }

    public void Open() => inner.Open();
    public void Close() => CloseGuarded();
    public void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public int ReadByte() => inner.ReadByte();
    public void Dispose() => CloseGuarded();

    // SerialPort.Close() drains the output queue (tcdrain) before closing,
    // and on the macOS FTDI driver that drain can block forever after the
    // multi-megabyte writes of a flash operation, hanging the process on
    // exit. Every FlashKit command is acked by the device before we close,
    // so the output queue is logically empty: discard it, then run the
    // close on a worker thread and abandon it if the drain still wedges —
    // process exit reclaims the descriptor.
    void CloseGuarded() => CloseGuarded(
        discardOutput: () => { if (inner.IsOpen) inner.DiscardOutBuffer(); },
        close: inner.Dispose,
        abandonAfter: TimeSpan.FromSeconds(2));

    internal static void CloseGuarded(Action discardOutput, Action close, TimeSpan abandonAfter)
    {
        try { discardOutput(); }
        catch (Exception) { }
        Task.Run(() =>
        {
            try { close(); }
            catch (Exception) { }
        }).Wait(abandonAfter);
    }
}
