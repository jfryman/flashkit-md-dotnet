namespace FlashKit.Core.Tests;

// Covers the guarded-close workaround for the macOS FTDI hang: on that
// driver tcdrain (run inside SerialPort.Close) can block forever after
// large writes, so the adapter discards the output queue first and
// abandons a close that still wedges.
public class SystemSerialPortTests
{
    [Fact]
    public void guarded_close_discards_output_then_closes()
    {
        var calls = new List<string>();
        SystemSerialPort.CloseGuarded(
            discardOutput: () => calls.Add("discard"),
            close: () => calls.Add("close"),
            abandonAfter: TimeSpan.FromSeconds(30));
        Assert.Equal(new[] { "discard", "close" }, calls);
    }

    [Fact]
    public void guarded_close_returns_even_if_close_hangs()
    {
        using var hang = new ManualResetEventSlim(false);
        SystemSerialPort.CloseGuarded(
            discardOutput: () => { },
            close: () => hang.Wait(),
            abandonAfter: TimeSpan.Zero);
        hang.Set(); // release the abandoned worker
    }

    [Fact]
    public void guarded_close_still_closes_when_discard_throws()
    {
        bool closed = false;
        SystemSerialPort.CloseGuarded(
            discardOutput: () => throw new IOException("port gone"),
            close: () => closed = true,
            abandonAfter: TimeSpan.FromSeconds(30));
        Assert.True(closed);
    }
}
