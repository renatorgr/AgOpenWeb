// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using AgOpenWeb.Services.BugReport;

namespace AgOpenWeb.Services.Tests;

/// <summary>The title and description typed in the panel are written into a dump saved to be sent
/// by hand (fork only: the report sent to Renato), in place of what Create wrote.</summary>
[TestFixture]
public class BugReportNotesTests
{
    private string _zip = null!;

    [SetUp]
    public void SetUp()
    {
        _zip = Path.Combine(Path.GetTempPath(), "brnotes_" + Guid.NewGuid().ToString("N") + ".zip");
        using var z = ZipFile.Open(_zip, ZipArchiveMode.Create);
        using (var w = new StreamWriter(z.CreateEntry("system_info.txt").Open())) w.Write("App Version: 1.0.0.0\n");
        using (var w = new StreamWriter(z.CreateEntry(BugReportNotes.Entry).Open())) w.Write("# old\n\nwritten at Create");
    }

    [TearDown] public void TearDown() => File.Delete(_zip);

    [Test]
    public void Compose_IsWhatCreateWrites()
    {
        Assert.That(BugReportNotes.Compose("Boom off the line", "On the 20 m bend."),
            Is.EqualTo("# Boom off the line\n\nOn the 20 m bend."));
        Assert.That(BugReportNotes.Compose("  ", " Only a description "), Is.EqualTo("Only a description"));
    }

    [Test]
    public void Set_ReplacesTheNotes_AndKeepsTheDump()
    {
        BugReportNotes.Set(_zip, "# Boom off the line\n\nÁgua na curva, 20 m.");
        Assert.That(BugReportNotes.Read(_zip), Is.EqualTo("# Boom off the line\n\nÁgua na curva, 20 m."));
        using var z = ZipFile.OpenRead(_zip);
        Assert.That(z.Entries.Count(e => e.FullName == BugReportNotes.Entry), Is.EqualTo(1));
        Assert.That(z.GetEntry("system_info.txt"), Is.Not.Null);
    }

    [Test]
    public void NothingTyped_KeepsWhatCreateWrote()
    {
        BugReportNotes.Set(_zip, "  ");
        Assert.That(BugReportNotes.Read(_zip), Is.EqualTo("# old\n\nwritten at Create"));
    }

    [Test]
    public void A_dump_with_no_notes_gets_them()
    {
        using (var z = ZipFile.Open(_zip, ZipArchiveMode.Update)) z.GetEntry(BugReportNotes.Entry)!.Delete();
        Assert.That(BugReportNotes.Read(_zip), Is.Null);
        BugReportNotes.Set(_zip, "Description only");
        Assert.That(BugReportNotes.Read(_zip), Is.EqualTo("Description only"));
    }
}
