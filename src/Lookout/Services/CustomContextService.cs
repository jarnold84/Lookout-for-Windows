using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Lookout.Services;

/// <summary>
/// Manages %USERPROFILE%\.lookout\context.md — user-authored context that gets
/// folded into Claude's system prompt so it can give more personalized help.
/// </summary>
public sealed class CustomContextService
{
    private const string Template =
        "<!--\n" +
        "This is your Lookout context file. Anything you write here (outside these\n" +
        "comment markers) is shared with Lookout at the start of every conversation,\n" +
        "so it can help you better.\n" +
        "\n" +
        "For example: your name, what you use this computer for, your comfort level\n" +
        "with technology, apps you use often, or anything you'd want a helpful friend\n" +
        "looking over your shoulder to know.\n" +
        "-->\n";

    private static readonly Regex HtmlComment =
        new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Creates the .lookout folder and a starter context.md if missing.</summary>
    public void EnsureContextFile()
    {
        try
        {
            LookoutPaths.EnsureRoot();
            if (!File.Exists(LookoutPaths.ContextFile))
                File.WriteAllText(LookoutPaths.ContextFile, Template);
        }
        catch
        {
            // Non-fatal — the app still works without a context file.
        }
    }

    /// <summary>
    /// Returns the user's custom context, or null if the file is missing/empty
    /// or still contains only the template comment.
    /// </summary>
    public string? LoadContext()
    {
        try
        {
            if (!File.Exists(LookoutPaths.ContextFile))
                return null;

            var raw = File.ReadAllText(LookoutPaths.ContextFile);
            var withoutComments = HtmlComment.Replace(raw, string.Empty).Trim();
            return withoutComments.Length == 0 ? null : withoutComments;
        }
        catch
        {
            return null;
        }
    }
}
