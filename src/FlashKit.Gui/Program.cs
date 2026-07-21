using Avalonia;

namespace FlashKit.Gui;

sealed class Program
{
    // Avalonia requires the app to start on the main thread; the framework
    // configuration must stay in BuildAvaloniaApp for the visual designer.
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // Must run after the lifetime returns: any earlier (e.g. the
            // lifetime's Exit event) and X11Window.Cleanup's DBusMenuExporter
            // still needs the connection, crashing with ObjectDisposedException.
            DBusShutdownWorkaround.DisposeSharedConnection();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
