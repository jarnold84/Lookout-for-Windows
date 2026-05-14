using System;
using System.Runtime.InteropServices;

namespace Lookout.Platform;

/// <summary>
/// Registers a system-wide hotkey (Ctrl+Alt+L) to show/hide Lookout.
/// WM_HOTKEY is delivered to the owning window's thread, so the callback
/// runs on the UI thread and can touch the window directly.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0xB001;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_L = 0x4C;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private delegate IntPtr SubclassProc(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(
        IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    private readonly IntPtr _hwnd;
    private readonly Action _onPressed;
    private readonly SubclassProc _subclassProc; // keep alive: passed to native code
    private bool _disposed;

    public bool IsRegistered { get; }

    public GlobalHotkey(IntPtr hwnd, Action onPressed)
    {
        _hwnd = hwnd;
        _onPressed = onPressed;
        _subclassProc = HandleMessage;

        SetWindowSubclass(_hwnd, _subclassProc, HotkeyId, IntPtr.Zero);
        IsRegistered = RegisterHotKey(_hwnd, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_L);
    }

    private IntPtr HandleMessage(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            _onPressed();
            return IntPtr.Zero;
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (IsRegistered)
            UnregisterHotKey(_hwnd, HotkeyId);
        RemoveWindowSubclass(_hwnd, _subclassProc, HotkeyId);
    }
}
