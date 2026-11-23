using System.Diagnostics;
using Microsoft.Win32;

namespace LyricWallpaper.Platform.Windows;

internal static class DesktopShell
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "LyricWallpaper.Native";
    private static string StartupCommand => $"\"{Application.ExecutablePath}\"";

    public static void OpenDirectory(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static bool StartsAtLogin()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(RunName) as string, StartupCommand, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetStartsAtLogin(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunName, StartupCommand);
        else key.DeleteValue(RunName, throwOnMissingValue: false);
    }
}
