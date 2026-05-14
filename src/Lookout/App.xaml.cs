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
    }

    /// <summary>Invoked when a second instance was launched and redirected here.</summary>
    public void HandleRedirectedActivation()
    {
        _chatWindow?.DispatcherQueue.TryEnqueue(() => _chatWindow.ShowAndActivate());
    }

    private void ShowSettings()
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
