using System;
using Microsoft.Win32;

namespace ClaudeUsageWidget;

public static class StartupHelper
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClaudeUsageWidget";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>
    /// If auto-start is on but registered for a different exe (rebuilt, moved,
    /// Debug vs Release), point it at the one running now.
    /// </summary>
    public static void RepairPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is not string current) return;
        var expected = $"\"{Environment.ProcessPath}\"";
        if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
            key.SetValue(ValueName, expected);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(ValueName, false);
    }
}
