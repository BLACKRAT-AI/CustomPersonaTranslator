using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CPT.Shell;

public sealed class HotkeyManager : IDisposable
{
    public const uint ModAlt = 0x1;
    public const uint ModControl = 0x2;
    public const uint ModShift = 0x4;
    public const uint ModWin = 0x8;

    private const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, Action> _callbacks = new();
    private int _nextId = 1;
    private bool _disposed;

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("CPT.Hotkeys")
        {
            HwndSourceHook = WndProc,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE: message-only window
        };
        _source = new HwndSource(parameters);
        _hwnd = _source.Handle;
    }

    public int Register(uint modifiers, uint virtualKey, Action onPressed)
    {
        int id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, modifiers, virtualKey))
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"RegisterHotKey failed (id={id}, win32={err})");
        }
        _callbacks[id] = onPressed;
        return id;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_callbacks.TryGetValue(id, out var cb))
            {
                Application.Current.Dispatcher.BeginInvoke(cb);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var id in _callbacks.Keys) UnregisterHotKey(_hwnd, id);
        _callbacks.Clear();
        _source.Dispose();
    }
}
