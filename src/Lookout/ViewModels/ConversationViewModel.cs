using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Lookout.Models;
using Lookout.Platform;
using Lookout.Services;

namespace Lookout.ViewModels;

/// <summary>
/// Orchestrates the chat: holds the message list, sends user input to the
/// active AI provider, and streams the reply back into the UI.
/// </summary>
public sealed class ConversationViewModel : ObservableObject
{
    /// <summary>Re-capture the screen if this long has passed since the last turn.</summary>
    private static readonly TimeSpan InactivityThreshold = TimeSpan.FromMinutes(3);

    private readonly ScreenCaptureService _capture = new();
    private readonly SystemContextService _context = new();
    private readonly OcrService _ocr = new();
    private readonly MemoryService _memory = new();
    private readonly OverlayService _overlay;
    private readonly ActionService _actions;

    private AppSettings _settings;
    private string _inputText = string.Empty;
    private bool _isBusy;
    private bool _needsApiKey;
    private string? _statusMessage;
    private CancellationTokenSource? _cts;
    private DateTimeOffset _lastTurnAt = DateTimeOffset.MinValue;

    public ConversationViewModel()
    {
        // Created on the UI thread, so the overlay can capture this dispatcher.
        _overlay = new OverlayService(DispatcherQueue.GetForCurrentThread());
        _actions = new ActionService(_capture, _ocr, _overlay, _memory);
        _settings = AppSettings.Load();

        SendCommand = new RelayCommand(
            _ => _ = SendAsync(),
            _ => !_isBusy && !string.IsNullOrWhiteSpace(_inputText));
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => _isBusy);

        _needsApiKey = !ChatProviderFactory.HasActiveKey(_settings);
    }

    /// <summary>Prompt shown in the API-key bar (provider-specific).</summary>
    public string KeyPrompt => ChatProviderFactory.KeyPrompt(_settings);

    /// <summary>Placeholder for the API-key input (provider-specific).</summary>
    public string KeyPlaceholder => ChatProviderFactory.KeyPlaceholder(_settings);

    /// <summary>Re-reads settings (e.g. after the Settings window changed provider)
    /// and refreshes the API-key bar to match the active provider.</summary>
    public void ReloadSettings()
    {
        _settings = AppSettings.Load();
        NeedsApiKey = !ChatProviderFactory.HasActiveKey(_settings);
        OnPropertyChanged(nameof(KeyPrompt));
        OnPropertyChanged(nameof(KeyPlaceholder));
    }

    public ObservableCollection<MessageViewModel> Messages { get; } = new();

    public RelayCommand SendCommand { get; }

    public RelayCommand CancelCommand { get; }

    public string InputText
    {
        get => _inputText;
        set
        {
            if (SetProperty(ref _inputText, value))
                SendCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                SendCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool NeedsApiKey
    {
        get => _needsApiKey;
        private set => SetProperty(ref _needsApiKey, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public void SaveApiKey(string apiKey)
    {
        SecureStore.Save(_settings.ActiveKeyAccount, apiKey);
        NeedsApiKey = false;
        StatusMessage = "API key saved.";
    }

    private async Task SendAsync()
    {
        if (_isBusy)
            return;

        var text = InputText.Trim();
        if (text.Length == 0)
            return;

        // Pick up any provider/model changes made in Settings since the last turn.
        _settings = AppSettings.Load();

        if (!ChatProviderFactory.HasActiveKey(_settings))
        {
            NeedsApiKey = true;
            OnPropertyChanged(nameof(KeyPrompt));
            OnPropertyChanged(nameof(KeyPlaceholder));
            return;
        }

        InputText = string.Empty;
        StatusMessage = null;
        IsBusy = true;

        // Auto-capture on the first turn, or after a stretch of inactivity.
        var shouldCapture = Messages.Count == 0
            || DateTimeOffset.Now - _lastTurnAt > InactivityThreshold;

        IReadOnlyList<byte[]>? images = null;
        if (shouldCapture)
        {
            StatusMessage = "Looking at your screen…";
            try
            {
                images = await Task.Run(() => _capture.CaptureAllDisplays());
            }
            catch
            {
                images = null; // proceed without vision rather than failing the turn
            }
            StatusMessage = null;
            if (images is { Count: 0 })
                images = null;
        }

        // Gather what's running/visible so Claude has context for the request.
        string? contextBlock = null;
        try
        {
            contextBlock = await Task.Run(() => _context.Gather().ToContextBlock());
        }
        catch
        {
            contextBlock = null; // context is a nice-to-have, never block the turn
        }

        Messages.Add(new MessageViewModel(
            MessageRole.User, text, attachedImageCount: images?.Count ?? 0));

        var assistant = new MessageViewModel(MessageRole.Assistant, string.Empty, isStreaming: true);
        Messages.Add(assistant);

        _cts = new CancellationTokenSource();
        try
        {
            var provider = ChatProviderFactory.Create(_settings);
            var history = BuildHistory(contextBlock);
            await foreach (var evt in provider.StreamConversationAsync(
                history, images, _actions, _cts.Token))
            {
                switch (evt)
                {
                    case AssistantTextDelta textDelta:
                        StatusMessage = null;
                        assistant.Append(textDelta.Text);
                        break;
                    case AssistantToolActivity activity:
                        StatusMessage = activity.Description;
                        break;
                }
            }

            if (assistant.Text.Length == 0)
                assistant.Text = "(no response)";
        }
        catch (OperationCanceledException)
        {
            assistant.Text = assistant.Text.Length == 0
                ? "(canceled)"
                : assistant.Text + "  …(canceled)";
        }
        catch (ChatApiException ex)
        {
            assistant.Text = "⚠ " + ex.Message;
            if (ex.Message.Contains("API key", StringComparison.OrdinalIgnoreCase))
                NeedsApiKey = true;
        }
        catch (Exception ex)
        {
            assistant.Text = "⚠ Something went wrong: " + ex.Message;
        }
        finally
        {
            assistant.IsStreaming = false;
            IsBusy = false;
            StatusMessage = null;
            _lastTurnAt = DateTimeOffset.Now;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Builds the API history from the message list, skipping the in-progress
    /// streaming placeholder and any empty turns. The system-context block is
    /// appended to the latest user turn so it reaches Claude without cluttering
    /// the on-screen message bubble.
    /// </summary>
    private IReadOnlyList<Message> BuildHistory(string? systemContext)
    {
        var history = new List<Message>(Messages.Count);
        foreach (var m in Messages)
        {
            if (m.IsStreaming || m.Text.Length == 0)
                continue;
            history.Add(m.ToModel());
        }

        if (!string.IsNullOrEmpty(systemContext)
            && history.Count > 0
            && history[^1].Role == MessageRole.User)
        {
            var last = history[^1];
            history[^1] = last with { Text = last.Text + "\n\n" + systemContext };
        }

        return history;
    }
}
