using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Lookout.Platform;
using Lookout.Services;

namespace Lookout;

public partial class App : Application
{
    public static new App? Current => Application.Current as App;

    private ChatWindow? _chatWindow;
    private SettingsWindow? _settingsWindow;
    private TrayIconManager? _tray;
    private GlobalHotkey? _hotkey;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Make sure ~/.lookout/context.md exists so the user can personalize Lookout.
        new CustomContextService().EnsureContextFile();

        // Lookout is tray-resident: the window is created up front (so its HWND
        // exists for the hotkey) but stays hidden until the user summons it.
        _chatWindow = new ChatWindow();

        _tray = new TrayIconManager(
            onToggle: () => _chatWindow?.ToggleVisibility(),
            onSettings: ShowSettings,
            onQuit: Quit);
        _tray.Initialize();

        _hotkey = new GlobalHotkey(_chatWindow.Hwnd, () => _chatWindow?.ToggleVisibility());
        _chatWindow.SetHotkeyStatus(_hotkey.IsRegistered);

        // Show the window on a normal launch (desktop icon, Start Menu, "Launch
        // now"). Stay hidden in the tray only when Windows auto-starts us at
        // sign-in — the autostart shortcut passes --autostart.
        var launchedByAutostart = Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase));
        if (!launchedByAutostart)
            _chatWindow.ShowAndActivate();
    }

    /// <summary>Invoked when a second instance was launched and redirected here.</summary>
    public void HandleRedirectedActivation()
    {
        _chatWindow?.DispatcherQueue.TryEnqueue(() => _chatWindow.ShowAndActivate());
    }

    /// <summary>Opens (or focuses) the Settings window. Called from the tray menu
    /// and the in-window Settings button.</summary>
    public void ShowSettings()
    {
        _chatWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow();
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            }
            _settingsWindow.Activate();
        });
    }

    private void Quit()
    {
        _hotkey?.Dispose();
        _tray?.Dispose();
        _chatWindow?.PrepareForExit();
        Exit();
    }
}
