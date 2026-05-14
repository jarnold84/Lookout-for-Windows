using System;
using System.IO;

namespace Lookout.Services;

/// <summary>
/// Manages %USERPROFILE%\.lookout\notes.md — Claude's cross-session memory.
/// Notes are saved via the save_note tool and recalled via read_notes.
/// </summary>
public sealed class MemoryService
{
    /// <summary>Returns all saved notes, or an empty string if there are none.</summary>
    public string ReadNotes()
    {
        try
        {
            if (!File.Exists(LookoutPaths.NotesFile))
                return string.Empty;
            return File.ReadAllText(LookoutPaths.NotesFile).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Appends a dated note. Empty notes are ignored.</summary>
    public void SaveNote(string? note)
    {
        var trimmed = note?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return;

        // Keep each note on a single line so the file stays a tidy bullet list.
        trimmed = trimmed.ReplaceLineEndings(" ");

        LookoutPaths.EnsureRoot();
        File.AppendAllText(
            LookoutPaths.NotesFile, $"- [{DateTime.Now:yyyy-MM-dd}] {trimmed}{Environment.NewLine}");
    }

    /// <summary>Deletes all saved notes.</summary>
    public void ClearNotes()
    {
        try
        {
            if (File.Exists(LookoutPaths.NotesFile))
                File.Delete(LookoutPaths.NotesFile);
        }
        catch
        {
            // Non-fatal.
        }
    }
}
