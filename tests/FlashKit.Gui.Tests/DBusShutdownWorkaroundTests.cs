using System.Reflection;
using FlashKit.Gui;

namespace FlashKit.Gui.Tests;

/// <summary>
/// Pins the reflection contract DBusShutdownWorkaround depends on, so an
/// Avalonia upgrade that moves or renames the internal DBus connection
/// field fails here instead of silently reviving the crash-on-exit
/// (AvaloniaUI/Avalonia#19523). Plain [Fact]s: nothing here needs the
/// headless dispatcher, and no DBus connection is ever opened.
/// </summary>
public class DBusShutdownWorkaroundTests
{
    static FieldInfo ConnectionField()
    {
        var type = Type.GetType(DBusShutdownWorkaround.HelperTypeName);
        Assert.NotNull(type);
        var field = type.GetField(
            DBusShutdownWorkaround.ConnectionFieldName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return field;
    }

    [Fact]
    public void avalonia_still_has_the_internal_dbus_connection_field()
    {
        var field = ConnectionField();
        // The workaround pattern-matches the value to IDisposable.
        Assert.True(typeof(IDisposable).IsAssignableFrom(field.FieldType),
            $"{field.FieldType} is no longer IDisposable");
    }

    [Fact]
    public void dispose_clears_a_cached_connection_without_connecting()
    {
        if (!OperatingSystem.IsLinux())
            return; // the workaround (correctly) no-ops off Linux

        var field = ConnectionField();
        try
        {
            // An unconnected Tmds connection: the address is only parsed on
            // ConnectAsync, which never runs here.
            field.SetValue(null, Activator.CreateInstance(
                field.FieldType, "unix:path=/nonexistent"));

            DBusShutdownWorkaround.DisposeSharedConnection();

            Assert.Null(field.GetValue(null));
        }
        finally
        {
            field.SetValue(null, null);
        }
    }

    [Fact]
    public void dispose_is_a_safe_no_op_when_nothing_is_cached()
    {
        DBusShutdownWorkaround.DisposeSharedConnection();
        Assert.Null(ConnectionField().GetValue(null));
    }
}
