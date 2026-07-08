using System;
using System.Collections.Generic;

namespace Lookout.Services;

/// <summary>A provider-neutral tool definition. The schema is plain JSON Schema,
/// which both the Anthropic (<c>input_schema</c>) and OpenAI
/// (<c>function.parameters</c>) formats accept verbatim.</summary>
public sealed record ToolSpec(string Name, string Description, object Schema);

/// <summary>
/// The shared system prompt and tool set, defined once and serialized to each
/// provider's own wire format. Keeps Anthropic and OpenAI paths in lockstep.
/// </summary>
public static class ToolCatalog
{
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

    private static readonly object EmptyObjectSchema =
        new { type = "object", properties = new { }, required = Array.Empty<string>() };

    public static readonly IReadOnlyList<ToolSpec> Tools = new[]
    {
        new ToolSpec("capture_screen",
            "Take a fresh screenshot of all the user's displays so you can see what "
            + "is currently on screen.",
            EmptyObjectSchema),

        new ToolSpec("highlight_element",
            "Draw a pulsing highlight on screen pointing at a UI element (button, "
            + "link, menu item, or text). Use it whenever you tell the user to click "
            + "or look at something.",
            new
            {
                type = "object",
                properties = new
                {
                    text = new { type = "string", description = "The visible text of the element to point at, e.g. \"Save\" or \"File\"." },
                },
                required = new[] { "text" },
            }),

        new ToolSpec("list_applications",
            "List the applications installed on the user's computer (from the Start Menu).",
            EmptyObjectSchema),

        new ToolSpec("search_files",
            "Search for files and folders by name on the user's computer.",
            new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "Text to look for in file and folder names." },
                    directory = new { type = "string", description = "Optional folder to search within. Defaults to the user's profile folder." },
                },
                required = new[] { "query" },
            }),

        new ToolSpec("open_item",
            "Open an application, file, folder, or URL for the user.",
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "App name, file path, folder path, or URL to open." },
                },
                required = new[] { "path" },
            }),

        new ToolSpec("save_note",
            "Save a concise note about the user to remember in future conversations "
            + "(skill level, preferences, projects, what they struggle with).",
            new
            {
                type = "object",
                properties = new
                {
                    note = new { type = "string", description = "The note to remember. Keep it short and useful." },
                },
                required = new[] { "note" },
            }),

        new ToolSpec("read_notes",
            "Recall the notes you've saved about this user in past sessions.",
            EmptyObjectSchema),
    };

    /// <summary>The base prompt plus the user's custom context, if any.</summary>
    public static string BuildSystemPrompt()
    {
        var custom = new CustomContextService().LoadContext();
        return string.IsNullOrEmpty(custom)
            ? BaseSystemPrompt
            : BaseSystemPrompt + "\n\n[User Context]\n" + custom;
    }
}
