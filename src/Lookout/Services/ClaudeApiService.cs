using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Lookout.Models;

namespace Lookout.Services;

/// <summary>Raised for any non-success outcome from the Claude API.</summary>
public sealed class ClaudeApiException : Exception
{
    public ClaudeApiException(string message) : base(message) { }
}

/// <summary>An event emitted while streaming an assistant turn.</summary>
public abstract record AssistantEvent;

/// <summary>A fragment of assistant text as it streams in.</summary>
public sealed record AssistantTextDelta(string Text) : AssistantEvent;

/// <summary>A human-readable note that a tool is running (e.g. "Opening Notepad…").</summary>
public sealed record AssistantToolActivity(string Description) : AssistantEvent;

/// <summary>Kinds of server-sent events from the Anthropic streaming API.</summary>
public enum SseEventKind
{
    Ignore,
    TextDelta,
    ContentBlockStart,
    InputJsonDelta,
    ContentBlockStop,
    MessageDelta,
    Error,
}

/// <summary>A parsed Anthropic SSE event.</summary>
public readonly record struct SseEvent
{
    public SseEventKind Kind { get; init; }
    public int Index { get; init; }
    public string? Text { get; init; }
    public string? ToolId { get; init; }
    public string? ToolName { get; init; }
    public string? PartialJson { get; init; }
    public string? StopReason { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Talks to the Anthropic Messages API. Streams assistant replies token by
/// token and runs an agentic loop: when Claude requests tools, they're executed
/// via <see cref="IToolExecutor"/> and the results are fed back automatically.
/// </summary>
public sealed class ClaudeApiService
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";
    private const string Model = "claude-sonnet-4-6";
    private const int MaxTokens = 2048;
    private const int MaxAgenticIterations = 8;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly CustomContextService _customContext = new();

    public const string BaseSystemPrompt =
        "You are Lookout, a friendly AI screen assistant for Windows. You help " +
        "people navigate their computer — the kind of help someone might phone a " +
        "tech-savvy friend or family member for.\n\n" +
        "Screenshots of the user's screen are attached automatically at the start " +
        "of a conversation and after the user has been away for a while. Use what " +
        "you can see to give specific, grounded guidance.\n\n" +
        "You have tools to take action on the user's computer:\n" +
        "- capture_screen: take a fresh screenshot of all displays — use this when " +
        "you need to see what's currently on screen, e.g. after the user says they " +
        "did something. Don't capture unless you actually need to look.\n" +
        "- highlight_element: draw a pulsing highlight on screen pointing at a "
        + "specific button, link, or piece of text. Use this whenever you tell the "
        + "user to click something — pass the element's visible text so they can "
        + "see exactly where it is.\n" +
        "- list_applications: see what apps are installed.\n" +
        "- search_files: find files and folders by name.\n" +
        "- open_item: open an app, file, folder, or URL.\n" +
        "- save_note: remember something useful about the user for future "
        + "conversations.\n" +
        "- read_notes: recall what you've learned about the user in past sessions.\n\n" +
        "At the start of a new conversation, use read_notes to recall what you know "
        + "about this user. As you help them, use save_note to remember useful things — "
        + "what they struggle with, apps they use, their comfort level, projects they're "
        + "working on, preferences. Keep notes concise; don't save every interaction.\n\n" +
        "Guidelines:\n" +
        "- Reference what you actually see: app names, button labels, menu items, " +
        "window titles, visible text.\n" +
        "- Be specific about locations: \"the blue Save button in the top-right " +
        "corner\" not just \"the button\".\n" +
        "- Give 1-2 clear next steps at a time, not long tutorials.\n" +
        "- Be conversational, encouraging, and patient.\n" +
        "- Keep replies concise — this is a small floating window, not a document.\n" +
        "- If you can't see something clearly in the screenshot, say so.\n" +
        "- Ignore the Lookout chat window itself if it appears in a screenshot.\n" +
        "- Be proactive: if the user needs something opened or found, just do it.\n" +
        "- Each message includes a [System Context] block with running apps and " +
        "visible window titles.";

    private static readonly object[] ToolDefinitions =
    {
        new
        {
            name = "capture_screen",
            description = "Take a fresh screenshot of all the user's displays so you "
                + "can see what is currently on screen.",
            input_schema = new { type = "object", properties = new { }, required = Array.Empty<string>() },
        },
        new
        {
            name = "highlight_element",
            description = "Draw a pulsing highlight on screen pointing at a UI element "
                + "(button, link, menu item, or text). Use it whenever you tell the user "
                + "to click or look at something.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    text = new { type = "string", description = "The visible text of the element to point at, e.g. \"Save\" or \"File\"." },
                },
                required = new[] { "text" },
            },
        },
        new
        {
            name = "list_applications",
            description = "List the applications installed on the user's computer "
                + "(from the Start Menu).",
            input_schema = new { type = "object", properties = new { }, required = Array.Empty<string>() },
        },
        new
        {
            name = "search_files",
            description = "Search for files and folders by name on the user's computer.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "Text to look for in file and folder names." },
                    directory = new { type = "string", description = "Optional folder to search within. Defaults to the user's profile folder." },
                },
                required = new[] { "query" },
            },
        },
        new
        {
            name = "open_item",
            description = "Open an application, file, folder, or URL for the user.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "App name, file path, folder path, or URL to open." },
                },
                required = new[] { "path" },
            },
        },
        new
        {
            name = "save_note",
            description = "Save a concise note about the user to remember in future "
                + "conversations (skill level, preferences, projects, what they struggle with).",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    note = new { type = "string", description = "The note to remember. Keep it short and useful." },
                },
                required = new[] { "note" },
            },
        },
        new
        {
            name = "read_notes",
            description = "Recall the notes you've saved about this user in past sessions.",
            input_schema = new { type = "object", properties = new { }, required = Array.Empty<string>() },
        },
    };

    /// <summary>
    /// Streams an assistant turn for the given history. Runs the agentic loop:
    /// any tools Claude requests are executed via <paramref name="toolExecutor"/>
    /// and the results fed back until Claude produces a final answer. Screenshots
    /// in <paramref name="images"/> are attached to the latest user turn.
    /// </summary>
    public async IAsyncEnumerable<AssistantEvent> StreamConversationAsync(
        IReadOnlyList<Message> history,
        IReadOnlyList<byte[]>? images,
        IToolExecutor toolExecutor,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var apiKey = SecureStore.LoadApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ClaudeApiException("No Anthropic API key is set.");

        var messages = BuildMessages(history, images);
        var systemPrompt = BuildSystemPrompt();

        for (var iteration = 0; iteration < MaxAgenticIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            var text = new StringBuilder();
            var toolAccumulators = new Dictionary<int, ToolUseAccumulator>();
            string? stopReason = null;

            using (var response = await SendRequestAsync(apiKey, systemPrompt, messages, ct))
            {
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    throw new ClaudeApiException(DescribeError(response.StatusCode, body));
                }

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                while (true)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line == null)
                        break;
                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                        continue;

                    var data = line[5..].Trim();
                    if (data.Length == 0)
                        continue;

                    var evt = ParseSseEvent(data);
                    switch (evt.Kind)
                    {
                        case SseEventKind.TextDelta:
                            text.Append(evt.Text);
                            yield return new AssistantTextDelta(evt.Text!);
                            break;
                        case SseEventKind.ContentBlockStart when evt.ToolName != null:
                            toolAccumulators[evt.Index] =
                                new ToolUseAccumulator(evt.ToolId!, evt.ToolName);
                            break;
                        case SseEventKind.InputJsonDelta:
                            if (toolAccumulators.TryGetValue(evt.Index, out var acc))
                                acc.Json.Append(evt.PartialJson);
                            break;
                        case SseEventKind.MessageDelta:
                            if (evt.StopReason != null)
                                stopReason = evt.StopReason;
                            break;
                        case SseEventKind.Error:
                            throw new ClaudeApiException(evt.Error!);
                    }
                }
            }

            // No tools requested — this was the final answer.
            if (stopReason != "tool_use" || toolAccumulators.Count == 0)
                yield break;

            // Reconstruct the assistant turn (text + tool_use blocks) for the next request.
            var assistantBlocks = new List<object>();
            if (text.Length > 0)
                assistantBlocks.Add(new TextBlock { Text = text.ToString() });
            foreach (var entry in toolAccumulators.OrderBy(e => e.Key))
            {
                assistantBlocks.Add(new ToolUseBlock
                {
                    Id = entry.Value.Id,
                    Name = entry.Value.Name,
                    Input = ParseInputJson(entry.Value.Json.ToString()),
                });
            }
            messages.Add(new ApiMessage { Role = "assistant", Content = assistantBlocks });

            // Execute each tool and feed the results back as a user turn.
            var resultBlocks = new List<object>();
            foreach (var entry in toolAccumulators.OrderBy(e => e.Key))
            {
                ct.ThrowIfCancellationRequested();
                var accumulator = entry.Value;
                var inputJson = accumulator.Json.ToString();

                yield return new AssistantToolActivity(
                    toolExecutor.DescribeActivity(accumulator.Name, inputJson));

                var result = await toolExecutor.ExecuteAsync(accumulator.Name, inputJson, ct);
                resultBlocks.Add(BuildToolResultBlock(accumulator.Id, result));
            }
            messages.Add(new ApiMessage { Role = "user", Content = resultBlocks });
        }

        throw new ClaudeApiException(
            "The assistant kept requesting tools without finishing. Stopped to avoid a loop.");
    }

    /// <summary>Combines the base prompt with the user's custom context, if any.</summary>
    private string BuildSystemPrompt()
    {
        var custom = _customContext.LoadContext();
        return string.IsNullOrEmpty(custom)
            ? BaseSystemPrompt
            : BaseSystemPrompt + "\n\n[User Context]\n" + custom;
    }

    private static async Task<HttpResponseMessage> SendRequestAsync(
        string apiKey, string systemPrompt, List<ApiMessage> messages, CancellationToken ct)
    {
        var payload = new RequestBody
        {
            Model = Model,
            MaxTokens = MaxTokens,
            System = systemPrompt,
            Stream = true,
            Tools = ToolDefinitions,
            Messages = messages,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);

        return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>Parses one SSE <c>data:</c> payload into a structured event.</summary>
    public static SseEvent ParseSseEvent(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeEl))
                return new SseEvent { Kind = SseEventKind.Ignore };

            switch (typeEl.GetString())
            {
                case "content_block_start":
                {
                    var index = root.GetProperty("index").GetInt32();
                    if (root.TryGetProperty("content_block", out var block)
                        && block.TryGetProperty("type", out var blockType)
                        && blockType.GetString() == "tool_use")
                    {
                        return new SseEvent
                        {
                            Kind = SseEventKind.ContentBlockStart,
                            Index = index,
                            ToolId = block.GetProperty("id").GetString(),
                            ToolName = block.GetProperty("name").GetString(),
                        };
                    }
                    return new SseEvent { Kind = SseEventKind.ContentBlockStart, Index = index };
                }

                case "content_block_delta":
                {
                    var index = root.GetProperty("index").GetInt32();
                    if (root.TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("type", out var deltaType))
                    {
                        switch (deltaType.GetString())
                        {
                            case "text_delta":
                                return new SseEvent
                                {
                                    Kind = SseEventKind.TextDelta,
                                    Index = index,
                                    Text = delta.GetProperty("text").GetString(),
                                };
                            case "input_json_delta":
                                return new SseEvent
                                {
                                    Kind = SseEventKind.InputJsonDelta,
                                    Index = index,
                                    PartialJson = delta.GetProperty("partial_json").GetString(),
                                };
                        }
                    }
                    return new SseEvent { Kind = SseEventKind.Ignore };
                }

                case "content_block_stop":
                    return new SseEvent
                    {
                        Kind = SseEventKind.ContentBlockStop,
                        Index = root.GetProperty("index").GetInt32(),
                    };

                case "message_delta":
                {
                    string? stop = null;
                    if (root.TryGetProperty("delta", out var messageDelta)
                        && messageDelta.TryGetProperty("stop_reason", out var sr)
                        && sr.ValueKind == JsonValueKind.String)
                    {
                        stop = sr.GetString();
                    }
                    return new SseEvent { Kind = SseEventKind.MessageDelta, StopReason = stop };
                }

                case "error":
                {
                    var message = root.TryGetProperty("error", out var err)
                                  && err.TryGetProperty("message", out var m)
                        ? m.GetString()
                        : "Unknown API error.";
                    return new SseEvent { Kind = SseEventKind.Error, Error = message };
                }

                default:
                    return new SseEvent { Kind = SseEventKind.Ignore };
            }
        }
        catch (JsonException)
        {
            // Keep-alive pings and partial fragments are expected — ignore them.
            return new SseEvent { Kind = SseEventKind.Ignore };
        }
    }

    /// <summary>
    /// Maps conversation history to API messages. Screenshots are attached only
    /// to the latest user turn — historical turns stay text-only so old images
    /// don't bloat the context window.
    /// </summary>
    public static List<ApiMessage> BuildMessages(
        IReadOnlyList<Message> history, IReadOnlyList<byte[]>? images)
    {
        var messages = new List<ApiMessage>(history.Count);
        for (var i = 0; i < history.Count; i++)
        {
            var m = history[i];
            var role = m.Role == MessageRole.User ? "user" : "assistant";
            var isLatest = i == history.Count - 1;

            if (isLatest && m.Role == MessageRole.User && images is { Count: > 0 })
            {
                var blocks = new List<object>(images.Count + 1);
                foreach (var jpeg in images)
                {
                    blocks.Add(new ImageBlock
                    {
                        Source = new ImageSourceBlock { Data = Convert.ToBase64String(jpeg) },
                    });
                }
                blocks.Add(new TextBlock { Text = m.Text });
                messages.Add(new ApiMessage { Role = role, Content = blocks });
            }
            else
            {
                messages.Add(new ApiMessage { Role = role, Content = m.Text });
            }
        }
        return messages;
    }

    /// <summary>Parses accumulated tool-input JSON, falling back to an empty object.</summary>
    public static JsonElement ParseInputJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            json = "{}";
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var fallback = JsonDocument.Parse("{}");
            return fallback.RootElement.Clone();
        }
    }

    /// <summary>Builds the tool_result content block sent back to Claude after a tool runs.</summary>
    public static ToolResultBlock BuildToolResultBlock(string toolUseId, ToolResult result)
    {
        object content;
        if (result.Images is { Count: > 0 })
        {
            var blocks = new List<object> { new TextBlock { Text = result.Text } };
            foreach (var jpeg in result.Images)
            {
                blocks.Add(new ImageBlock
                {
                    Source = new ImageSourceBlock { Data = Convert.ToBase64String(jpeg) },
                });
            }
            content = blocks;
        }
        else
        {
            content = result.Text;
        }

        return new ToolResultBlock
        {
            ToolUseId = toolUseId,
            Content = content,
            IsError = result.IsError ? true : null,
        };
    }

    private static string DescribeError(HttpStatusCode status, string body)
    {
        var snippet = body.Length > 300 ? body[..300] + "…" : body;
        return status switch
        {
            HttpStatusCode.Unauthorized =>
                "The Anthropic API key was rejected. Check that it's correct and active.",
            HttpStatusCode.TooManyRequests =>
                "Rate limited by the Anthropic API. Wait a moment and try again.",
            (HttpStatusCode)529 =>
                "The Anthropic API is temporarily overloaded. Try again shortly.",
            _ => $"Claude API error ({(int)status} {status}): {snippet}",
        };
    }

    private sealed class ToolUseAccumulator
    {
        public ToolUseAccumulator(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; }
        public string Name { get; }
        public StringBuilder Json { get; } = new();
    }

    private sealed class RequestBody
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
        [JsonPropertyName("system")] public string System { get; set; } = "";
        [JsonPropertyName("stream")] public bool Stream { get; set; }
        [JsonPropertyName("tools")] public object[]? Tools { get; set; }
        [JsonPropertyName("messages")] public List<ApiMessage> Messages { get; set; } = new();
    }

    /// <summary>
    /// One API message. <see cref="Content"/> is either a plain string (text-only
    /// turn) or a list of content blocks (text, images, tool_use, tool_result).
    /// </summary>
    public sealed class ApiMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("content")] public object Content { get; set; } = "";
    }

    private sealed class TextBlock
    {
        [JsonPropertyName("type")] public string Type => "text";
        [JsonPropertyName("text")] public string Text { get; set; } = "";
    }

    private sealed class ImageBlock
    {
        [JsonPropertyName("type")] public string Type => "image";
        [JsonPropertyName("source")] public ImageSourceBlock Source { get; set; } = new();
    }

    private sealed class ImageSourceBlock
    {
        [JsonPropertyName("type")] public string Type => "base64";
        [JsonPropertyName("media_type")] public string MediaType { get; set; } = "image/jpeg";
        [JsonPropertyName("data")] public string Data { get; set; } = "";
    }

    private sealed class ToolUseBlock
    {
        [JsonPropertyName("type")] public string Type => "tool_use";
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("input")] public JsonElement Input { get; set; }
    }

    public sealed class ToolResultBlock
    {
        [JsonPropertyName("type")] public string Type => "tool_result";
        [JsonPropertyName("tool_use_id")] public string ToolUseId { get; set; } = "";
        [JsonPropertyName("content")] public object Content { get; set; } = "";
        [JsonPropertyName("is_error")] public bool? IsError { get; set; }
    }
}
