// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.IO.Compression;
using System.Text;

namespace AgOpenWeb.Services.BugReport;

/// <summary>
/// The title and description in a bug report dump (<c>user_notes.txt</c>), brought up to what
/// the panel holds when the report is saved to be sent by hand (fork only: the report sent to
/// Renato in a Telegram chat). Create Bug Report writes what was typed then, and the panel asks
/// for the report to be made the moment a problem shows and described after; Send Report takes
/// the text at send time, so only the report saved by hand needs this.
/// </summary>
public static class BugReportNotes
{
    public const string Entry = "user_notes.txt";

    /// <summary>The most the notes may be, in bytes (a description is a few hundred).</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>The notes as Create Bug Report writes them: the title as a heading, then the description.</summary>
    public static string Compose(string title, string description)
    {
        title = title.Trim();
        description = description.Trim();
        return title.Length == 0 ? description : "# " + title + "\n\n" + description;
    }

    /// <summary>Replace the dump's notes. Nothing typed changes nothing: the dump keeps the notes
    /// it was made with, if any.</summary>
    public static void Set(string zipPath, string notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return;
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update);
        zip.GetEntry(Entry)?.Delete();
        using var w = new StreamWriter(zip.CreateEntry(Entry, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        w.Write(notes);
    }

    /// <summary>The dump's notes, or null when it has none.</summary>
    public static string? Read(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var e = zip.GetEntry(Entry);
        if (e == null) return null;
        using var r = new StreamReader(e.Open(), Encoding.UTF8);
        return r.ReadToEnd();
    }
}
