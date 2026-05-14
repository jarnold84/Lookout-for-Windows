using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace Lookout.Services;

/// <summary>
/// Draws a pulsing highlight on screen to point the user at a UI element.
/// Implemented as a pure Win32 layered, click-through, topmost window — it
/// floats above everything (including the user's apps) without stealing focus
/// or blocking clicks, and disposes itself after a few seconds.
/// </summary>
public sealed class OverlayService
{
    private const int FrameIntervalMs = 33;
    private const string ClassName = "LookoutOverlayWindow";

    // --- Win32 interop ---------------------------------------------------

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const int ULW_ALPHA = 0x00000002;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const int SW_SHOWNOACTIVATE = 4;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int Cx; public int Cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int CbSize;
        public uint Style;
        public IntPtr LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public IntPtr HInstance;
        public IntPtr HIcon;
        public IntPtr HCursor;
        public IntPtr HbrBackground;
        public string? LpszMenuName;
        public string LpszClassName;
        public IntPtr HIconSm;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hWnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE pSize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pBlend, int flags);

    // Keep the wndproc delegate rooted so it isn't collected while registered.
    private static readonly WndProcDelegate WndProc = (h, m, w, l) => DefWindowProc(h, m, w, l);
    private static bool _classRegistered;
    private static readonly object ClassLock = new();

    // --- instance state --------------------------------------------------

    private readonly DispatcherQueue _dispatcher;
    private IntPtr _hwnd;
    private int _winX;
    private int _winY;
    private long _startTicks;
    private DispatcherQueueTimer? _timer;

    public OverlayService(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Shows a pulsing highlight centered on the given screen coordinate.
    /// Safe to call from any thread; the work marshals to the UI thread.
    /// Completes once the highlight is on screen (it then animates and
    /// dismisses itself).
    /// </summary>
    public Task ShowHighlightAsync(int screenX, int screenY)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var enqueued = _dispatcher.TryEnqueue(() =>
        {
            try
            {
                ShowOnUiThread(screenX, screenY);
            }
            catch
            {
                // The highlight is best-effort — never let it break a tool call.
            }
            finally
            {
                tcs.TrySetResult();
            }
        });

        if (!enqueued)
            tcs.TrySetResult();

        return tcs.Task;
    }

    private void ShowOnUiThread(int screenX, int screenY)
    {
        Dismiss();
        EnsureClassRegistered();

        _winX = screenX - OverlayRenderer.OverlaySize / 2;
        _winY = screenY - OverlayRenderer.OverlaySize / 2;

        _hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            ClassName, string.Empty, WS_POPUP,
            _winX, _winY, OverlayRenderer.OverlaySize, OverlayRenderer.OverlaySize,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            return;

        _startTicks = Environment.TickCount64;
        PushFrame(0);
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(FrameIntervalMs);
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        var elapsed = (int)(Environment.TickCount64 - _startTicks);
        if (_hwnd == IntPtr.Zero || elapsed >= OverlayRenderer.DurationMs)
        {
            Dismiss();
            return;
        }
        PushFrame(elapsed);
    }

    private void Dismiss()
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private void PushFrame(int elapsedMs)
    {
        if (_hwnd == IntPtr.Zero)
            return;

        using var bitmap = OverlayRenderer.RenderFrame(elapsedMs);

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        var oldBitmap = SelectObject(memDc, hBitmap);
        try
        {
            var dst = new POINT { X = _winX, Y = _winY };
            var size = new SIZE { Cx = OverlayRenderer.OverlaySize, Cy = OverlayRenderer.OverlaySize };
            var src = new POINT { X = 0, Y = 0 };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA,
            };
            UpdateLayeredWindow(_hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memDc, oldBitmap);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static void EnsureClassRegistered()
    {
        lock (ClassLock)
        {
            if (_classRegistered)
                return;

            var wc = new WNDCLASSEX
            {
                CbSize = Marshal.SizeOf<WNDCLASSEX>(),
                Style = 0,
                LpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProc),
                HInstance = GetModuleHandle(null),
                LpszClassName = ClassName,
            };

            // A zero return may simply mean the class is already registered from
            // an earlier call in this process — treat it as success either way.
            RegisterClassEx(ref wc);
            _classRegistered = true;
        }
    }
}
