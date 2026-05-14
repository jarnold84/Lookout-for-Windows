using Lookout.Models;
using Lookout.Platform;

namespace Lookout.ViewModels;

/// <summary>Bindable wrapper around a chat message. Text mutates while streaming.</summary>
public sealed class MessageViewModel : ObservableObject
{
    private string _text;
    private bool _isStreaming;

    public MessageViewModel(MessageRole role, string text, bool isStreaming = false,
        int attachedImageCount = 0)
    {
        Role = role;
        _text = text;
        _isStreaming = isStreaming;
        AttachedImageCount = attachedImageCount;
    }

    public MessageRole Role { get; }

    public bool IsUser => Role == MessageRole.User;

    public string RoleLabel => IsUser ? "You" : "Lookout";

    /// <summary>How many screenshots were sent with this turn (0 if none).</summary>
    public int AttachedImageCount { get; }

    public bool HasScreenshot => AttachedImageCount > 0;

    public string ScreenshotLabel => AttachedImageCount == 1
        ? "screen attached"
        : $"{AttachedImageCount} screens attached";

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set => SetProperty(ref _isStreaming, value);
    }

    public void Append(string delta)
    {
        if (string.IsNullOrEmpty(delta))
            return;
        Text += delta;
    }

    public Message ToModel() => new(Role, Text);
}
