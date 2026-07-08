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

/// <summary>
/// Talks to any OpenAI-compatible chat-completions endpoint — used for
/// OpenRouter (so Lookout can run GLM, GPT, Gemini, etc. with the user's own
/// key). Same agentic tool loop as the Anthropic provider, translated to the
/// OpenAI wire format (Bearer auth, system-as-message, function tools,
/// image_url parts, tool-role results, and OpenAI SSE deltas).
/// </summary>
public sealed class OpenAiCompatibleProvider : IChatProvider
{
    private const int MaxTokens = 2048;
    private const int MaxAgenticIterations = 8;

    public const string DefaultBaseUrl = "https://openrouter.ai/api/v1";

    /// <summary>Default OpenRouter model — vision + tool calling (both required by
    /// Lookout), cheap, and strong at reading UI screenshots.</summary>
    public const string DefaultModel = "google/gemini-2.5-flash-lite";

    /// <summary>Google Gemini's OpenAI-compatible endpoint (Bearer auth with a
    /// Gemini API key).</summary>
    public const string GoogleBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai";

    /// <summary>Default Google model — cheap, vision + tool calling, strong at screens.</summary>
    public const string GoogleDefaultModel = "gemini-2.5-flash-lite";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Standard JSON escaping (\" not ") — we send to an API over HTTPS,
        // not into HTML, so relaxed escaping is correct and keeps payloads clean.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _baseUrl;

    public OpenAiCompatibleProvider(string apiKey, string? model = null, string? baseUrl = null)
    {
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
        _baseUrl = NormalizeBaseUrl(baseUrl);
    }

    /// <summary>OpenAI function-tool definitions, serialized from the shared catalog.</summary>
    private static readonly object[] ToolDefinitions = ToolCatalog.Tools
        .Select(t => (object)new
        {
            type = "function",
            function = new { name = t.Name, description = t.Description, parameters = t.Schema },
        })
        .ToArray();

    /// <inheritdoc />
    public async IAsyncEnumerable<AssistantEvent> StreamConversationAsync(
        IReadOnlyList<Message> history,
        IReadOnlyList<byte[]>? images,
        IToolExecutor toolExecutor,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new ChatApiException("No OpenRouter API key is set.");

        var messages = BuildMessages(ToolCatalog.BuildSystemPrompt(), history, images);

        for (var iteration = 0; iteration < MaxAgenticIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            var text = new StringBuilder();
            var toolCalls = new Dictionary<int, ToolCallAccumulator>();

            using (var response = await SendRequestAsync(messages, ct))
            {
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    throw new ChatApiException(DescribeError(response.StatusCode, body));
                }

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                var done = false;
                while (!done)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line == null)
                        break;
                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                        continue;

                    var data = line[5..].Trim();
                    if (data.Length == 0)
                        continue;

                    var chunk = ParseChunk(data);
                    if (chunk.IsDone)
                        break;
                    if (chunk.Error != null)
                        throw new ChatApiException(chunk.Error);

                    if (!string.IsNullOrEmpty(chunk.TextDelta))
                    {
                        text.Append(chunk.TextDelta);
                        yield return new AssistantTextDelta(chunk.TextDelta);
                    }

                    foreach (var tc in chunk.ToolCalls)
                    {
                        if (!toolCalls.TryGetValue(tc.Index, out var acc))
                        {
                            acc = new ToolCallAccumulator();
                            toolCalls[tc.Index] = acc;
                        }
                        if (!string.IsNullOrEmpty(tc.Id)) acc.Id = tc.Id!;
                        if (!string.IsNullOrEmpty(tc.Name)) acc.Name = tc.Name!;
                        acc.Arguments.Append(tc.ArgumentsFragment);
                    }
                }
            }

            // No tools requested — this was the final answer.
            if (toolCalls.Count == 0)
                yield break;

            // Echo the assistant's tool-call turn back verbatim.
            var ordered = toolCalls.OrderBy(e => e.Key).Select(e => e.Value).ToList();
            messages.Add(new ChatMessage
            {
                // Always emit content (empty string, not null) so stricter
                // OpenAI-compatible endpoints accept the tool-call turn.
                Role = "assistant",
                Content = text.ToString(),
                ToolCalls = ordered.Select(a => new ToolCallOut
                {
                    Id = a.Id,
                    Function = new FunctionCallOut { Name = a.Name, Arguments = a.Arguments.ToString() },
                }).ToList(),
            });

            // Execute each tool and feed results back as tool-role messages. Any
            // images a tool returns are attached as a follow-up user message,
            // since the OpenAI tool role only carries text.
            var pendingImages = new List<byte[]>();
            foreach (var acc in ordered)
            {
                ct.ThrowIfCancellationRequested();
                var inputJson = acc.Arguments.ToString();

                yield return new AssistantToolActivity(
                    toolExecutor.DescribeActivity(acc.Name, inputJson));

                var result = await toolExecutor.ExecuteAsync(acc.Name, inputJson, ct);
                messages.Add(new ChatMessage
                {
                    Role = "tool",
                    ToolCallId = acc.Id,
                    Content = result.Text,
                });
                if (result.Images is { Count: > 0 })
                    pendingImages.AddRange(result.Images);
            }

            if (pendingImages.Count > 0)
                messages.Add(BuildImageFollowUp(pendingImages));
        }

        throw new ChatApiException(
            "The assistant kept requesting tools without finishing. Stopped to avoid a loop.");
    }

    private async Task<HttpResponseMessage> SendRequestAsync(List<ChatMessage> messages, CancellationToken ct)
    {
        var payload = new RequestBody
        {
            Model = _model,
            MaxTokens = MaxTokens,
            Stream = true,
            Tools = ToolDefinitions,
            Messages = messages,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Authorization", "Bearer " + _apiKey);
        // OpenRouter-specific attribution headers — only send to OpenRouter so
        // other endpoints (e.g. Google) never see unexpected headers.
        if (_baseUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Add("HTTP-Referer", "https://github.com/jarnold84/Lookout-for-Windows");
            request.Headers.Add("X-Title", "Lookout");
        }

        return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    // ------------------------------------------------------------------ parsing

    /// <summary>A tool-call fragment parsed from one streaming delta.</summary>
    public readonly record struct ToolCallDelta(int Index, string? Id, string? Name, string? ArgumentsFragment);

    /// <summary>A parsed OpenAI streaming chunk.</summary>
    public readonly record struct OpenAiChunk
    {
        public bool IsDone { get; init; }
        public string? TextDelta { get; init; }
        public string? FinishReason { get; init; }
        public string? Error { get; init; }
        public IReadOnlyList<ToolCallDelta> ToolCalls { get; init; }
    }

    private static readonly IReadOnlyList<ToolCallDelta> NoToolCalls = Array.Empty<ToolCallDelta>();

    /// <summary>Parses one SSE <c>data:</c> payload from an OpenAI-compatible stream.</summary>
    public static OpenAiChunk ParseChunk(string data)
    {
        if (data == "[DONE]")
            return new OpenAiChunk { IsDone = true, ToolCalls = NoToolCalls };

        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new OpenAiChunk { ToolCalls = NoToolCalls };

            // Errors can arrive inline in the stream.
            if (root.TryGetProperty("error", out var errEl))
            {
                var msg = errEl.ValueKind == JsonValueKind.Object
                          && errEl.TryGetProperty("message", out var m)
                    ? m.GetString()
                    : errEl.GetRawText();
                return new OpenAiChunk { Error = msg ?? "Unknown API error.", ToolCalls = NoToolCalls };
            }

            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return new OpenAiChunk { ToolCalls = NoToolCalls };
            }

            var choice = choices[0];
            string? finish = null;
            if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                finish = fr.GetString();

            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                return new OpenAiChunk { FinishReason = finish, ToolCalls = NoToolCalls };

            string? textDelta = null;
            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                textDelta = content.GetString();

            var toolCalls = NoToolCalls;
            if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                var list = new List<ToolCallDelta>(tcs.GetArrayLength());
                foreach (var tc in tcs.EnumerateArray())
                {
                    var index = tc.TryGetProperty("index", out var idx) && idx.ValueKind == JsonValueKind.Number
                        ? idx.GetInt32() : 0;
                    string? id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    string? name = null, args = null;
                    if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                    {
                        if (fn.TryGetProperty("name", out var n)) name = n.GetString();
                        if (fn.TryGetProperty("arguments", out var a)) args = a.GetString();
                    }
                    list.Add(new ToolCallDelta(index, id, name, args));
                }
                toolCalls = list;
            }

            return new OpenAiChunk
            {
                TextDelta = textDelta,
                FinishReason = finish,
                ToolCalls = toolCalls,
            };
        }
        catch (JsonException)
        {
            // Keep-alive comments / partial fragments — ignore.
            return new OpenAiChunk { ToolCalls = NoToolCalls };
        }
    }

    // ------------------------------------------------------------- message build

    /// <summary>Maps history to OpenAI messages, prepending the system prompt and
    /// attaching screenshots to the latest user turn.</summary>
    public static List<ChatMessage> BuildMessages(
        string systemPrompt, IReadOnlyList<Message> history, IReadOnlyList<byte[]>? images)
    {
        var messages = new List<ChatMessage>(history.Count + 1)
        {
            new() { Role = "system", Content = systemPrompt },
        };

        for (var i = 0; i < history.Count; i++)
        {
            var m = history[i];
            var role = m.Role == MessageRole.User ? "user" : "assistant";
            var isLatest = i == history.Count - 1;

            if (isLatest && m.Role == MessageRole.User && images is { Count: > 0 })
            {
                var parts = new List<object> { new TextPart { Text = m.Text } };
                foreach (var jpeg in images)
                    parts.Add(new ImagePart { ImageUrl = new ImageUrl { Url = DataUri(jpeg) } });
                messages.Add(new ChatMessage { Role = role, Content = parts });
            }
            else
            {
                messages.Add(new ChatMessage { Role = role, Content = m.Text });
            }
        }
        return messages;
    }

    private static ChatMessage BuildImageFollowUp(IReadOnlyList<byte[]> images)
    {
        var parts = new List<object>
        {
            new TextPart { Text = "(Screenshot(s) from the tool call above:)" },
        };
        foreach (var jpeg in images)
            parts.Add(new ImagePart { ImageUrl = new ImageUrl { Url = DataUri(jpeg) } });
        return new ChatMessage { Role = "user", Content = parts };
    }

    private static string DataUri(byte[] jpeg) =>
        "data:image/jpeg;base64," + Convert.ToBase64String(jpeg);

    public static string NormalizeBaseUrl(string? baseUrl)
    {
        var url = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim();
        return url.TrimEnd('/');
    }

    private static string DescribeError(HttpStatusCode status, string body)
    {
        var snippet = body.Length > 300 ? body[..300] + "…" : body;
        return status switch
        {
            HttpStatusCode.Unauthorized =>
                "The OpenRouter API key was rejected. Check that it's correct and active.",
            HttpStatusCode.PaymentRequired =>
                "OpenRouter reports insufficient credits for this request.",
            HttpStatusCode.TooManyRequests =>
                "Rate limited by OpenRouter. Wait a moment and try again.",
            HttpStatusCode.NotFound =>
                $"Model not found. Check the model ID in Settings. ({snippet})",
            _ => $"OpenRouter API error ({(int)status} {status}): {snippet}",
        };
    }

    private sealed class ToolCallAccumulator
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public StringBuilder Arguments { get; } = new();
    }

    // ------------------------------------------------------------- wire models

    private sealed class RequestBody
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
        [JsonPropertyName("stream")] public bool Stream { get; set; }
        [JsonPropertyName("tools")] public object[]? Tools { get; set; }
        [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; } = new();
    }

    /// <summary>An OpenAI chat message. <see cref="Content"/> is a string, a list
    /// of content parts, or null (assistant tool-call turns).</summary>
    public sealed class ChatMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("content")] public object? Content { get; set; }
        [JsonPropertyName("tool_calls")] public List<ToolCallOut>? ToolCalls { get; set; }
        [JsonPropertyName("tool_call_id")] public string? ToolCallId { get; set; }
    }

    public sealed class ToolCallOut
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("type")] public string Type => "function";
        [JsonPropertyName("function")] public FunctionCallOut Function { get; set; } = new();
    }

    public sealed class FunctionCallOut
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("arguments")] public string Arguments { get; set; } = "";
    }

    private sealed class TextPart
    {
        [JsonPropertyName("type")] public string Type => "text";
        [JsonPropertyName("text")] public string Text { get; set; } = "";
    }

    private sealed class ImagePart
    {
        [JsonPropertyName("type")] public string Type => "image_url";
        [JsonPropertyName("image_url")] public ImageUrl ImageUrl { get; set; } = new();
    }

    private sealed class ImageUrl
    {
        [JsonPropertyName("url")] public string Url { get; set; } = "";
    }
}
