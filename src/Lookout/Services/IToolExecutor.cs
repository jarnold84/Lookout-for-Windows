using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lookout.Services;

/// <summary>The outcome of running a tool — text, optional images, and an error flag.</summary>
public sealed record ToolResult(
    string Text,
    IReadOnlyList<byte[]>? Images = null,
    bool IsError = false);

/// <summary>
/// Runs a tool call requested by Claude. Implemented by ActionService.
/// Kept as an interface so ClaudeApiService's agentic loop has no dependency
/// on the concrete action implementations.
/// </summary>
public interface IToolExecutor
{
    /// <summary>Human-readable label for a tool call, shown while it runs.</summary>
    string DescribeActivity(string toolName, string inputJson);

    Task<ToolResult> ExecuteAsync(string toolName, string inputJson, CancellationToken ct);
}
