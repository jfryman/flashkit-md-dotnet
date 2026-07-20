using Terminal.Gui.App;

namespace FlashKit.Tui;

static class Program
{
    static void Main()
    {
        using IApplication app = Application.Create();
        app.Init(null);
        var window = new ProgrammerTuiWindow(null, app);
        app.AddTimeout(TimeSpan.FromSeconds(2), () =>
        {
            _ = window.Model.RefreshAsync();
            return true;
        });
        _ = window.Model.RefreshAsync();
        app.Run(window, null);
        window.Dispose();
    }
}
