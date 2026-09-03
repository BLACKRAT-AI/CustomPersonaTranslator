using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CPT.Core.Diagnostics;

namespace CPT.Shell;

/// <summary>
/// Paints window title bars to match the app.
///
/// A WPF window's caption is drawn by the desktop window manager, not by WPF, so
/// no amount of styling reaches it — a dark app ends up wearing a white title
/// bar. Windows exposes one attribute that fixes this, and the class handler
/// below applies it to every window the app opens, so no individual window has
/// to remember to.
/// </summary>
internal static class DarkTitleBar
{
    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE, as of Windows 10 20H1 and later.</summary>
    private const int UseImmersiveDarkMode = 20;

    /// <summary>The attribute number used by Windows 10 builds before 20H1.</summary>
    private const int UseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Applies a dark caption to every window from now on. Call once at startup.
    /// </summary>
    public static void ApplyToAllWindows() =>
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Apply(sender as Window)));

    /// <summary>Applies a dark caption to one window. Safe to call more than once.</summary>
    public static void Apply(Window? window)
    {
        if (window is null) return;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var enabled = 1;

        // 20 is correct on current Windows; older builds used 19 and ignore 20,
        // so try the modern one first and fall back rather than checking builds.
        if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) == 0) return;

        if (DwmSetWindowAttribute(handle, UseImmersiveDarkModeLegacy, ref enabled, sizeof(int)) != 0)
            CptLog.Write("[ui] the window manager refused a dark title bar for " + window.GetType().Name);
    }
}
