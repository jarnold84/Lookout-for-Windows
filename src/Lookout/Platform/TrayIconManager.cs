using System;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Lookout.Platform;

/// <summary>
/// Owns the system tray icon and its context menu. Left-click toggles the
/// chat window; the menu offers explicit Show and Quit actions.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly Action _onToggle;
    private readonly Action _onSettings;
    private readonly Action _onQuit;
    private TaskbarIcon? _trayIcon;

    public TrayIconManager(Action onToggle, Action onSettings, Action onQuit)
    {
        _onToggle = onToggle;
        _onSettings = onSettings;
        _onQuit = onQuit;
    }

    public void Initialize()
    {
        var menu = new MenuFlyout();

        var showItem = new MenuFlyoutItem { Text = "Show Lookout" };
        showItem.Click += (_, _) => _onToggle();

        var settingsItem = new MenuFlyoutItem { Text = "Settings" };
        settingsItem.Click += (_, _) => _onSettings();

        var quitItem = new MenuFlyoutItem { Text = "Quit Lookout" };
        quitItem.Click += (_, _) => _onQuit();

        menu.Items.Add(showItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(quitItem);

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Lookout — AI screen assistant",
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/lookout.ico")),
            ContextMenuMode = ContextMenuMode.SecondWindow,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(() => _onToggle()),
            ContextFlyout = menu,
        };

        _trayIcon.ForceCreate();
    }

    public void Dispose()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}
