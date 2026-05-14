using System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace Lookout;

public sealed partial class ChatWindow : Window
{
    private const int WindowWidth = 380;
    private const int WindowHeight = 560;
    private const int ScreenMargin = 20;

    private readonly AppWindow _appWindow;
    private bool _allowClose;

    /// <summary>Native Win32 handle — needed for the global hotkey and capture exclusion.</summary>
    public IntPtr Hwnd { get; }

    public ChatWindow()
    {
        InitializeComponent();

        Hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(Hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        Title = "Lookout";
        ConfigurePresenter();
        ResizeAndPositionTopRight();

        // Closing the window hides it to the tray rather than exiting the app.
        _appWindow.Closing += OnClosing;
    }

    /// <summary>Reflects whether the global hotkey registered in the window header.</summary>
    public void SetHotkeyStatus(bool registered)
    {
        HeaderText.Text = registered ? "Lookout" : "Lookout — hotkey unavailable";
    }

    /// <summary>Allows the next Close() to actually destroy the window (used on Quit).</summary>
    public void PrepareForExit() => _allowClose = true;

    private void ConfigurePresenter()
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = true;
        }
    }

    private void ResizeAndPositionTopRight()
    {
        var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        int x = work.X + work.Width - WindowWidth - ScreenMargin;
        int y = work.Y + ScreenMargin;
        _appWindow.MoveAndResize(new RectInt32(x, y, WindowWidth, WindowHeight));
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
            return;

        // Keep the process alive in the tray instead of terminating.
        args.Cancel = true;
        _appWindow.Hide();
    }

    public void ShowAndActivate()
    {
        _appWindow.Show();
        Activate();
        _appWindow.MoveInZOrderAtTop();
    }

    public void HideWindow() => _appWindow.Hide();

    public void ToggleVisibility()
    {
        if (_appWindow.IsVisible)
            HideWindow();
        else
            ShowAndActivate();
    }
}
