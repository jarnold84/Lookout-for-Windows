using System;
using System.Diagnostics;
using Lookout.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace Lookout;

public sealed partial class SettingsWindow : Window
{
    private readonly CustomContextService _customContext = new();
    private readonly MemoryService _memory = new();

    public SettingsWindow()
    {
        InitializeComponent();
        Title = "Lookout Settings";

        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        appWindow.Resize(new SizeInt32(520, 600));
        if (appWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsMaximizable = false;

        FolderPath.Text = "Files are stored in " + LookoutPaths.Root;
        RefreshKeyStatus();
        RefreshMemoryStatus();
    }

    private void RefreshKeyStatus()
    {
        var hasKey = SecureStore.HasApiKey();
        KeyStatus.Text = hasKey
            ? "A key is saved. Enter a new one below to replace it."
            : "No key saved yet. Lookout needs one to talk to Claude.";
        RemoveKeyButton.IsEnabled = hasKey;
    }

    private void RefreshMemoryStatus()
    {
        var notes = _memory.ReadNotes();
        MemoryStatus.Text = notes.Length == 0
            ? "No notes saved yet."
            : $"{notes.Split('\n').Length} note(s) saved.";
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
            SecureStore.SaveApiKey(key);
            ApiKeyBox.Password = string.Empty;
            KeyStatus.Text = "Key saved.";
            RemoveKeyButton.IsEnabled = true;
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
            SecureStore.DeleteApiKey();
        }
        catch
        {
            // Non-fatal — fall through to refresh.
        }
        RefreshKeyStatus();
        KeyStatus.Text = "Key removed.";
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
