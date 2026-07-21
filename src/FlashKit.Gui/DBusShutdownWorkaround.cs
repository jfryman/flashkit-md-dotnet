using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace FlashKit.Gui;

/// <summary>
/// Works around AvaloniaUI/Avalonia#19523: on Linux, Avalonia's FreeDesktop
/// backend keeps a shared DBus connection (portal file picker, theme
/// settings) whose read loop marshals disconnect callbacks onto the UI
/// thread. At process exit that loop can observe the socket closing after
/// the dispatcher has already shut down; the resulting Dispatcher.Send then
/// throws an unhandled TaskCanceledException on a thread-pool thread and
/// crashes an otherwise clean close. Disposing the connection from Main
/// right after the lifetime returns — still the UI thread, so the disconnect
/// callbacks run inline instead of racing a dead dispatcher queue — removes
/// the race. Remove once the upstream issue is fixed.
/// </summary>
internal static class DBusShutdownWorkaround
{
    internal const string HelperTypeName = "Avalonia.FreeDesktop.DBusHelper, Avalonia.FreeDesktop";
    internal const string ConnectionFieldName = "s_defaultConntection"; // upstream typo, verbatim

    // DBusHelper is internal to Avalonia, so this has to be reflection; the
    // DynamicDependency keeps the field alive under PublishTrimmed, and
    // DBusShutdownWorkaroundTests pins the reflection contract so an
    // Avalonia upgrade that renames it fails tests instead of silently
    // turning this into a no-op.
    [DynamicDependency(ConnectionFieldName, "Avalonia.FreeDesktop.DBusHelper", "Avalonia.FreeDesktop")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "The DynamicDependency above preserves the field this looks up.")]
    public static void DisposeSharedConnection()
    {
        if (!OperatingSystem.IsLinux())
            return;

        try
        {
            var field = Type.GetType(HelperTypeName)
                ?.GetField(ConnectionFieldName, BindingFlags.NonPublic | BindingFlags.Static);
            if (field?.GetValue(null) is IDisposable connection)
            {
                field.SetValue(null, null);
                connection.Dispose();
            }
        }
        catch
        {
            // Best-effort mitigation only — a failure here must never turn a
            // clean shutdown into the very crash it exists to prevent.
        }
    }
}
