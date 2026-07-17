using Avalonia;
using Avalonia.Headless;
using FlashKit.Gui;
using FlashKit.Gui.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace FlashKit.Gui.Tests;

/// <summary>Boots the real App class on the headless platform: no display
/// server, [AvaloniaFact] tests run on a real dispatcher loop.</summary>
public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
