using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Lookout.Services;

/// <summary>A snapshot of what's running and visible on the user's desktop.</summary>
public sealed class SystemContext
{
    public required string? ForegroundApp { get; init; }
    public required IReadOnlyList<string> RunningApps { get; init; }
    public required IReadOnlyList<string> WindowTitles { get; init; }

    /// <summary>Formats the snapshot as the [System Context] block sent to Claude.</summary>
    public string ToContextBlock()
    {
        var sb = new StringBuilder();
        sb.Append("[System Context]");

        if (!string.IsNullOrEmpty(ForegroundApp))
            sb.Append("\nForeground app: ").Append(ForegroundApp);

        if (RunningApps.Count > 0)
            sb.Append("\nRunning apps: ").Append(string.Join(", ", RunningApps));

        if (WindowTitles.Count > 0)
        {
            sb.Append("\nVisible windows:");
            foreach (var title in WindowTitles)
                sb.Append("\n  - ").Append(title);
        }

        return sb.ToString();
    }
}

/// <summary>
/// Gathers running apps and visible window titles via Win32 enumeration —
/// the Windows counterpart of the macOS NSWorkspace context.
/// </summary>
public sealed class SystemContextService
{
    private const int MaxWindows = 40;
    private const int MaxApps = 30;
    private const int MaxTitleLength = 120;

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_APPWINDOW = 0x00040000;
    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hWnd, int attribute, out int value, int size);

    public SystemContext Gather()
    {
        var currentPid = (uint)Environment.ProcessId;
        var titles = new List<string>();
        var apps = new List<string>();
        var seenApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        EnumWindows((hWnd, _) =>
        {
            if (!IsAppWindow(hWnd, currentPid))
                return true;

            var title = GetTitle(hWnd);
            if (title.Length == 0)
                return true;

            if (titles.Count < MaxWindows)
                titles.Add(title);

            GetWindowThreadProcessId(hWnd, out var pid);
            var appName = GetProcessName(pid);
            if (appName != null && seenApps.Add(appName) && apps.Count < MaxApps)
                apps.Add(appName);

            return true;
        }, IntPtr.Zero);

        return new SystemContext
        {
            ForegroundApp = GetForegroundAppName(currentPid),
            RunningApps = apps,
            WindowTitles = titles,
        };
    }

    private static bool IsAppWindow(IntPtr hWnd, uint currentPid)
    {
        if (!IsWindowVisible(hWnd))
            return false;

        GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == currentPid || pid == 0)
            return false; // skip our own windows

        if (IsCloaked(hWnd))
            return false; // suspended UWP / window on another virtual desktop

        var exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
        var isToolWindow = (exStyle & WS_EX_TOOLWINDOW) != 0;
        var isAppWindow = (exStyle & WS_EX_APPWINDOW) != 0;
        var hasOwner = GetWindow(hWnd, GW_OWNER) != IntPtr.Zero;

        // A real taskbar-style window: not a tool window, and either a top-level
        // unowned window or one explicitly flagged as an app window.
        if (isToolWindow)
            return false;
        return isAppWindow || !hasOwner;
    }

    private static bool IsCloaked(IntPtr hWnd)
    {
        if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) != 0)
            return false; // attribute unavailable — assume not cloaked
        return cloaked != 0;
    }

    private static string GetTitle(IntPtr hWnd)
    {
        var length = GetWindowTextLength(hWnd);
        if (length <= 0)
            return string.Empty;

        var buffer = new StringBuilder(length + 1);
        GetWindowText(hWnd, buffer, buffer.Capacity);
        var title = buffer.ToString().Trim();
        if (title.Length > MaxTitleLength)
            title = title[..MaxTitleLength] + "…";
        return title;
    }

    private static string? GetProcessName(uint pid)
    {
        if (pid == 0)
            return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return null; // process exited between enumeration and lookup
        }
    }

    private static string? GetForegroundAppName(uint currentPid)
    {
        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero)
            return null;

        GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == currentPid || pid == 0)
            return null;
        return GetProcessName(pid);
    }
}
