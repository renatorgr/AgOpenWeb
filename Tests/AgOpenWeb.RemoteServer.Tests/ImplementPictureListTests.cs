// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using AgOpenWeb.Models.Configuration;
using NUnit.Framework;

namespace AgOpenWeb.RemoteServer.Tests;

/// <summary>
/// Tool > Type > Picture on the map lists the rows of IMPLEMENT_PICTURES (js/map-draw.js), and the host keeps
/// only a ToolPicture it knows (fork only). A value with no row could be saved and never drawn or chosen; a row
/// with no value would be refused by the host when picked.
/// </summary>
[TestFixture]
public class ImplementPictureListTests
{
    [Test]
    public void Every_picture_the_host_keeps_has_one_row_in_the_page_s_list_and_no_more()
    {
        var js = File.ReadAllText(Path.Combine(WwwRoot(), "js", "map-draw.js"));
        var rows = Rows(js).Select(r => (r.Id, r.Draw)).ToList();
        var values = Enum.GetValues<ToolPicture>().Where(p => p != ToolPicture.None).Select(p => (int)p).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(rows.Select(r => r.Id), Is.EquivalentTo(values), "one row per ToolPicture value but None");
            foreach (var (id, draw) in rows)
                Assert.That(js, Does.Contain("function " + draw + "(canvas, tl, veh)"), "row " + id + " draws with " + draw);
        });
    }

    /// <summary>The painted area's texture comes from the picture's work (<c>work</c> in its row, fork only): every work
    /// a row names is a texture of js/work-textures.js with a name in the Painted area hint, and every texture is the
    /// work of some picture, so none is made and never shown.</summary>
    [Test]
    public void Every_work_a_picture_names_has_a_texture_and_every_texture_a_picture()
    {
        var js = File.ReadAllText(Path.Combine(WwwRoot(), "js", "map-draw.js"));
        var tex = File.ReadAllText(Path.Combine(WwwRoot(), "js", "work-textures.js"));
        var table = tex[tex.IndexOf("const WORK_TEXTURES = {", StringComparison.Ordinal)..];
        table = table[..table.IndexOf("\n};", StringComparison.Ordinal)];
        var textures = Regex.Matches(table, @"^  (\w+): \{ passes: [12], rgb\(", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        var names = tex[tex.IndexOf("function workName(work)", StringComparison.Ordinal)..];
        names = names[..names.IndexOf("}[work];", StringComparison.Ordinal)];
        var works = Rows(js).Where(r => r.Work != null).Select(r => r.Work!).Distinct().ToList();
        Assert.Multiple(() =>
        {
            Assert.That(textures, Has.Count.EqualTo(10), "the ten textures of the mock-up");
            Assert.That(works, Is.EquivalentTo(textures), "each texture is a picture's work, each work a texture");
            foreach (var w in textures)
                Assert.That(names, Does.Contain(w + ": () => tr('"), w + " has a name");
        });
    }

    // The rows of IMPLEMENT_PICTURES: id, its work (null for none) and its draw function.
    private static List<(int Id, string? Work, string Draw)> Rows(string js)
    {
        var list = js[js.IndexOf("const IMPLEMENT_PICTURES = [", StringComparison.Ordinal)..];
        list = list[..list.IndexOf("];", StringComparison.Ordinal)];
        return Regex.Matches(list, @"\{ id: (\d+), type: ([0-3]), vehicles: \[([0-2](?:, [0-2])*)\], work: (?:null|'(\w+)'), draw: (\w+),")
            .Select(m => (int.Parse(m.Groups[1].Value), m.Groups[4].Success ? m.Groups[4].Value : null, m.Groups[5].Value)).ToList();
    }

    private static string WwwRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AgOpenWeb.sln"))) dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "Could not locate repo root (AgOpenWeb.sln) from test base dir.");
        return Path.Combine(dir!.FullName, "Shared", "AgOpenWeb.RemoteServer", "wwwroot");
    }
}
