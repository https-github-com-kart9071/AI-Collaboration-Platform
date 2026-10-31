using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AICollaborationPlatform.App.Views;

/// <summary>
/// Asks Windows to draw a window's title bar dark, to match the app's dark theme.
/// Windows 10 (20H1+) and 11 support it; on older versions the call fails quietly and the title bar stays light.
/// </summary>
public static class DarkTitleBar
{
    private const int DwmUseImmersiveDarkMode = 20;

    public static void Apply(Window window)
    {
        if (new WindowInteropHelper(window).Handle is var handle && handle != IntPtr.Zero)
        {
            Set(handle);
        }
        else
        {
            window.SourceInitialized += (_, _) => Set(new WindowInteropHelper(window).Handle);
        }
    }

    private static void Set(IntPtr handle)
    {
        var enabled = 1;
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
