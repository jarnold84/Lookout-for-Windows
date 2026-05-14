namespace Lookout.Models;

public enum MessageRole
{
    User,
    Assistant,
}

/// <summary>
/// A single turn in the conversation. Plain data — used to build the
/// history sent to the Claude API. The bindable counterpart is MessageViewModel.
/// </summary>
public sealed record Message(MessageRole Role, string Text);
