using System;
using System.Collections.Generic;
using System.Threading;
using Lookout.Models;

namespace Lookout.Services;

/// <summary>Which backend Lookout talks to for chat completions.</summary>
public enum ProviderKind
{
    /// <summary>Anthropic Messages API, spoken natively.</summary>
    Anthropic,

    /// <summary>OpenRouter / any OpenAI-compatible chat-completions endpoint.</summary>
    OpenRouter,
}

/// <summary>Raised for any non-success outcome from a chat provider.</summary>
public sealed class ChatApiException : Exception
{
    public ChatApiException(string message) : base(message) { }
}

/// <summary>An event emitted while streaming an assistant turn.</summary>
public abstract record AssistantEvent;

/// <summary>A fragment of assistant text as it streams in.</summary>
public sealed record AssistantTextDelta(string Text) : AssistantEvent;

/// <summary>A human-readable note that a tool is running (e.g. "Opening Notepad…").</summary>
public sealed record AssistantToolActivity(string Description) : AssistantEvent;

/// <summary>
/// A chat backend that streams an assistant turn and runs an agentic tool loop.
/// Implemented once per API format (Anthropic native, OpenAI-compatible).
/// </summary>
public interface IChatProvider
{
    /// <summary>
    /// Streams an assistant turn for the given history. Any tools the model
    /// requests are executed via <paramref name="toolExecutor"/> and the results
    /// fed back until it produces a final answer. Screenshots in
    /// <paramref name="images"/> are attached to the latest user turn.
    /// </summary>
    IAsyncEnumerable<AssistantEvent> StreamConversationAsync(
        IReadOnlyList<Message> history,
        IReadOnlyList<byte[]>? images,
        IToolExecutor toolExecutor,
        CancellationToken ct = default);
}
