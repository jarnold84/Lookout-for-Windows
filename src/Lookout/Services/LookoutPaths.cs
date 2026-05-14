using System;
using System.IO;

namespace Lookout.Services;

/// <summary>Filesystem locations Lookout uses, all under %USERPROFILE%\.lookout.</summary>
public static class LookoutPaths
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lookout");

    public static string ContextFile => Path.Combine(Root, "context.md");

    public static string NotesFile => Path.Combine(Root, "notes.md");

    public static void EnsureRoot() => Directory.CreateDirectory(Root);
}
