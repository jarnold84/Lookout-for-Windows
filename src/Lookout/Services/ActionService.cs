using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lookout.Services;

/// <summary>
/// Executes the non-overlay tools Claude can call: capture the screen, list
/// installed apps, search for files, and open apps/files/folders/URLs.
/// </summary>
public sealed class ActionService : IToolExecutor
{
    private const int MaxSearchResults = 50;
    private static readonly TimeSpan SearchBudget = TimeSpan.FromSeconds(8);

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "AppData", "$Recycle.Bin",
    };

    private readonly ScreenCaptureService _capture;
    private readonly OcrService _ocr;
    private readonly OverlayService _overlay;
    private readonly MemoryService _memory;

    public ActionService(
        ScreenCaptureService capture, OcrService ocr, OverlayService overlay, MemoryService memory)
    {
        _capture = capture;
        _ocr = ocr;
        _overlay = overlay;
        _memory = memory;
    }

    public string DescribeActivity(string toolName, string inputJson) => toolName switch
    {
        "capture_screen" => "Taking a screenshot…",
        "list_applications" => "Checking installed apps…",
        "search_files" => $"Searching for \"{GetString(inputJson, "query") ?? "files"}\"…",
        "open_item" => $"Opening {GetString(inputJson, "path") ?? "an item"}…",
        "highlight_element" => $"Pointing at \"{GetString(inputJson, "text") ?? "an element"}\"…",
        "save_note" => "Making a note…",
        "read_notes" => "Recalling what I know…",
        _ => "Working…",
    };

    public async Task<ToolResult> ExecuteAsync(string toolName, string inputJson, CancellationToken ct)
    {
        try
        {
            return toolName switch
            {
                "capture_screen" => await Task.Run(CaptureScreen, ct),
                "list_applications" => await Task.Run(ListApplications, ct),
                "search_files" => await Task.Run(
                    () => SearchFiles(GetString(inputJson, "query"), GetString(inputJson, "directory"), ct), ct),
                "open_item" => await Task.Run(() => OpenItem(GetString(inputJson, "path")), ct),
                "highlight_element" => await HighlightElementAsync(GetString(inputJson, "text"), ct),
                "save_note" => await Task.Run(() => SaveNote(GetString(inputJson, "note")), ct),
                "read_notes" => await Task.Run(ReadNotes, ct),
                _ => new ToolResult($"Unknown tool: {toolName}", IsError: true),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolResult($"The {toolName} tool failed: {ex.Message}", IsError: true);
        }
    }

    private async Task<ToolResult> HighlightElementAsync(string? targetText, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetText))
            return new ToolResult("No element text was provided to highlight.", IsError: true);

        var displays = await Task.Run(() => _capture.CaptureDisplays(), ct);
        if (displays.Count == 0)
            return new ToolResult(
                "Couldn't capture the screen to locate the element.", IsError: true);

        foreach (var display in displays)
        {
            ct.ThrowIfCancellationRequested();
            var candidates = await _ocr.RecognizeAsync(display.Jpeg, ct);
            var match = OcrService.FindBestMatch(candidates, targetText);
            if (match == null)
                continue;

            var screenX = display.OriginX + (int)Math.Round(match.CenterX);
            var screenY = display.OriginY + (int)Math.Round(match.CenterY);
            await _overlay.ShowHighlightAsync(screenX, screenY);
            return new ToolResult($"Highlighted \"{match.Text}\" on screen for the user.");
        }

        return new ToolResult(
            $"Couldn't find \"{targetText}\" on screen with OCR. It may be an icon without a "
            + "text label, or the text may be styled in a way OCR can't read — describe its "
            + "location to the user in words instead.");
    }

    private ToolResult CaptureScreen()
    {
        var shots = _capture.CaptureAllDisplays();
        if (shots.Count == 0)
            return new ToolResult(
                "Couldn't capture the screen — no displays returned an image.", IsError: true);

        var label = shots.Count == 1 ? "Captured 1 display." : $"Captured {shots.Count} displays.";
        return new ToolResult(label, shots);
    }

    private static ToolResult ListApplications()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in StartMenuRoots())
        {
            try
            {
                foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                    names.Add(Path.GetFileNameWithoutExtension(lnk));
            }
            catch
            {
                // A Start Menu folder we couldn't read — skip it.
            }
        }

        return names.Count == 0
            ? new ToolResult("No installed applications were found in the Start Menu.")
            : new ToolResult(string.Join("\n", names));
    }

    private static ToolResult SearchFiles(string? query, string? directory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new ToolResult("No search query was provided.", IsError: true);

        var root = directory;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var matches = new List<string>();
        var deadline = DateTime.UtcNow + SearchBudget;
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0 && matches.Count < MaxSearchResults && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (Path.GetFileName(file).Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(file);
                        if (matches.Count >= MaxSearchResults)
                            break;
                    }
                }
            }
            catch
            {
                // Access denied / transient error — skip this directory's files.
            }

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (SkipDirectories.Contains(name) || name.StartsWith('.'))
                        continue;
                    if (name.Contains(query, StringComparison.OrdinalIgnoreCase)
                        && matches.Count < MaxSearchResults)
                        matches.Add(sub);
                    pending.Push(sub);
                }
            }
            catch
            {
                // Access denied / transient error — skip this directory's children.
            }
        }

        if (matches.Count == 0)
            return new ToolResult($"No files or folders matching \"{query}\" were found under {root}.");

        var header = matches.Count >= MaxSearchResults
            ? $"First {MaxSearchResults} matches for \"{query}\":"
            : $"{matches.Count} match(es) for \"{query}\":";
        return new ToolResult(header + "\n" + string.Join("\n", matches));
    }

    private ToolResult SaveNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
            return new ToolResult("No note text was provided.", IsError: true);
        _memory.SaveNote(note);
        return new ToolResult("Noted — I'll remember that.");
    }

    private ToolResult ReadNotes()
    {
        var notes = _memory.ReadNotes();
        return notes.Length == 0
            ? new ToolResult("No notes saved about this user yet.")
            : new ToolResult("Notes from past sessions:\n" + notes);
    }

    private static ToolResult OpenItem(string? path)
    {
        path = path?.Trim();
        if (string.IsNullOrEmpty(path))
            return new ToolResult("No app, file, or folder was specified to open.", IsError: true);

        if (TryStart(path, out var error))
            return new ToolResult($"Opened \"{path}\".");

        // The path wasn't directly launchable — try resolving it as an app name
        // against the Start Menu shortcuts.
        var shortcut = FindStartMenuShortcut(path);
        if (shortcut != null && TryStart(shortcut, out _))
            return new ToolResult($"Opened \"{path}\".");

        return new ToolResult($"Couldn't open \"{path}\": {error}", IsError: true);
    }

    private static bool TryStart(string target, out string error)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? FindStartMenuShortcut(string appName)
    {
        var candidates = new List<string>();
        foreach (var root in StartMenuRoots())
        {
            try
            {
                candidates.AddRange(Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories));
            }
            catch
            {
                // Skip unreadable Start Menu folder.
            }
        }

        // Prefer an exact name match, then a contains match.
        var exact = candidates.FirstOrDefault(c =>
            string.Equals(Path.GetFileNameWithoutExtension(c), appName, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;

        return candidates.FirstOrDefault(c =>
            Path.GetFileNameWithoutExtension(c).Contains(appName, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> StartMenuRoots()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.StartMenu })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                yield return path;
        }
    }

    /// <summary>Reads a string property from a tool-input JSON object. Returns null if absent.</summary>
    public static string? GetString(string json, string property)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(property, out var element)
                && element.ValueKind == JsonValueKind.String)
            {
                return element.GetString();
            }
        }
        catch (JsonException)
        {
            // Malformed tool input — treat as missing.
        }
        return null;
    }
}
