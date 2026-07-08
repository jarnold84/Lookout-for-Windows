using System;
using System.Diagnostics;
using Lookout.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace Lookout;

public sealed partial class SettingsWindow : Window
{
    private readonly CustomContextService _customContext = new();
    private readonly MemoryService _memory = new();

    private AppSettings _settings;
    private ProviderKind _current;
    private bool _loading;

    public SettingsWindow()
    {
        InitializeComponent();
        Title = "Lookout Settings";

        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        appWindow.Resize(new SizeInt32(560, 760));
        if (appWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsMaximizable = false;

        _settings = AppSettings.Load();
        _current = _settings.Provider;

        _loading = true;
        ProviderBox.SelectedIndex = _current switch
        {
            ProviderKind.Google => 1,
            ProviderKind.OpenRouter => 2,
            _ => 0,
        };
        _loading = false;
        SyncProviderDependentUi();

        FolderPath.Text = "Files are stored in " + LookoutPaths.Root;
        RefreshMemoryStatus();

        // Persist any model / base-URL edits when the window closes.
        Closed += (_, _) => PersistAndSave();
    }

    // --- provider / model -------------------------------------------------

    private ProviderKind SelectedProvider()
    {
        if (ProviderBox.SelectedItem is ComboBoxItem item
            && item.Tag is string tag
            && Enum.TryParse<ProviderKind>(tag, out var kind))
        {
            return kind;
        }
        return ProviderKind.Anthropic;
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;

        // Capture edits for the provider we're leaving, then switch.
        PersistModelFields(_current);
        _current = SelectedProvider();
        _settings.Provider = _current;
        _settings.Save();
        SyncProviderDependentUi();
    }

    private void SyncProviderDependentUi()
    {
        switch (_current)
        {
            case ProviderKind.Google:
                ModelBox.Text = _settings.GoogleModel;
                ModelHint.Text = "e.g. gemini-2.5-flash-lite (cheap + great at screens). "
                    + "Must support vision and tool calling.";
                BaseUrlPanel.Visibility = Visibility.Collapsed;
                KeyHeader.Text = "Google Gemini API key";
                ApiKeyBox.PlaceholderText = "AIza… or AQ.…";
                break;

            case ProviderKind.OpenRouter:
                ModelBox.Text = _settings.OpenRouterModel;
                ModelHint.Text = "e.g. google/gemini-2.5-flash-lite (cheap + great at screens). "
                    + "Must support BOTH vision and tool calling, or screenshots and actions won't work.";
                BaseUrlPanel.Visibility = Visibility.Visible;
                BaseUrlBox.Text = _settings.OpenRouterBaseUrl;
                KeyHeader.Text = "OpenRouter API key";
                ApiKeyBox.PlaceholderText = "sk-or-...";
                break;

            default: // Anthropic
                ModelBox.Text = _settings.AnthropicModel;
                ModelHint.Text = "e.g. claude-sonnet-4-6";
                BaseUrlPanel.Visibility = Visibility.Collapsed;
                KeyHeader.Text = "Anthropic API key";
                ApiKeyBox.PlaceholderText = "sk-ant-...";
                break;
        }

        RefreshKeyStatus();
    }

    private void PersistModelFields(ProviderKind provider)
    {
        var model = ModelBox.Text?.Trim() ?? string.Empty;
        switch (provider)
        {
            case ProviderKind.Google:
                _settings.GoogleModel = model.Length > 0 ? model : OpenAiCompatibleProvider.GoogleDefaultModel;
                break;
            case ProviderKind.OpenRouter:
                _settings.OpenRouterModel = model.Length > 0 ? model : OpenAiCompatibleProvider.DefaultModel;
                _settings.OpenRouterBaseUrl = OpenAiCompatibleProvider.NormalizeBaseUrl(BaseUrlBox.Text?.Trim());
                break;
            default:
                _settings.AnthropicModel = model.Length > 0 ? model : ClaudeApiService.DefaultModel;
                break;
        }
    }

    private void PersistAndSave()
    {
        PersistModelFields(_current);
        _settings.Save();
    }

    // --- API key ----------------------------------------------------------

    private string ActiveAccount => _current switch
    {
        ProviderKind.Google => SecureStore.GoogleAccount,
        ProviderKind.OpenRouter => SecureStore.OpenRouterAccount,
        _ => SecureStore.AnthropicAccount,
    };

    private void RefreshKeyStatus()
    {
        var hasKey = SecureStore.Has(ActiveAccount);
        var name = _current switch
        {
            ProviderKind.Google => "Google Gemini",
            ProviderKind.OpenRouter => "OpenRouter",
            _ => "Anthropic",
        };
        KeyStatus.Text = hasKey
            ? $"A {name} key is saved. Enter a new one below to replace it."
            : $"No {name} key saved yet. Lookout needs one to chat.";
        RemoveKeyButton.IsEnabled = hasKey;
    }

    private void OnSaveKey(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            KeyStatus.Text = "Enter a key first.";
            return;
        }

        try
        {
            PersistAndSave();
            SecureStore.Save(ActiveAccount, key);
            ApiKeyBox.Password = string.Empty;
            RefreshKeyStatus();
            KeyStatus.Text = "Key saved.";
        }
        catch (Exception ex)
        {
            KeyStatus.Text = "Couldn't save the key: " + ex.Message;
        }
    }

    private void OnRemoveKey(object sender, RoutedEventArgs e)
    {
        try
        {
            SecureStore.Delete(ActiveAccount);
        }
        catch
        {
            // Non-fatal — fall through to refresh.
        }
        RefreshKeyStatus();
        KeyStatus.Text = "Key removed.";
    }

    // --- personalization / memory ----------------------------------------

    private void RefreshMemoryStatus()
    {
        var notes = _memory.ReadNotes();
        MemoryStatus.Text = notes.Length == 0
            ? "No notes saved yet."
            : $"{notes.Split('\n').Length} note(s) saved.";
    }

    private void OnOpenContext(object sender, RoutedEventArgs e)
    {
        _customContext.EnsureContextFile();
        OpenInShell(LookoutPaths.ContextFile);
    }

    private void OnOpenNotes(object sender, RoutedEventArgs e)
    {
        if (!System.IO.File.Exists(LookoutPaths.NotesFile))
        {
            LookoutPaths.EnsureRoot();
            System.IO.File.WriteAllText(LookoutPaths.NotesFile, string.Empty);
        }
        OpenInShell(LookoutPaths.NotesFile);
    }

    private void OnClearNotes(object sender, RoutedEventArgs e)
    {
        _memory.ClearNotes();
        RefreshMemoryStatus();
        MemoryStatus.Text = "Saved notes cleared.";
    }

    private static void OpenInShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch
        {
            // Best effort — nothing actionable if the shell can't open it.
        }
    }
}
