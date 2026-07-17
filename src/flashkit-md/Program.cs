using FlashKit.Core;

namespace flashkit_md;

static class Program
{
    static int Main(string[] args)
        => new CliApp(new DeviceConnector(), Console.Out, Console.Error).Run(args);
}
