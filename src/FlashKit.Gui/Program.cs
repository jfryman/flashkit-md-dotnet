using Avalonia;

namespace FlashKit.Gui;

class Program
{
    // Avalonia requires the app to start on the main thread; the framework
    // configuration must stay in BuildAvaloniaApp for the visual designer.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
