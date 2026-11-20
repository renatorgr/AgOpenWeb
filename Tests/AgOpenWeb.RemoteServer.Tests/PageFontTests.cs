// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using NUnit.Framework;

namespace AgOpenWeb.RemoteServer.Tests;

/// <summary>
/// The web client has one typeface. The body set none, so every rule named its own, and text a rule
/// missed was in the browser's default serif (Times New Roman: Roll Correction, GPS Offset Fix,
/// Recorded Path, the chart and VT card titles), buttons, boxes and lists in the browser's Arial
/// 13.3 px (20 Nov 2026, measured on the 1280x800 tablet layout).
/// </summary>
[TestFixture]
public class PageFontTests
{
    [Test]
    public void The_body_sets_the_typeface_and_form_controls_take_it()
    {
        var css = Css("base.css");
        Assert.That(css, Does.Match(@"\bbody\s*\{[^}]*font-family:\s*system-ui,\s*sans-serif"), "the body's typeface");
        Assert.That(css, Does.Match(@"button,\s*input,\s*select,\s*textarea\s*\{[^}]*font-family:\s*inherit"),
            "buttons and boxes inherit it instead of the browser's Arial");
    }

    [Test]
    public void No_stylesheet_names_another_typeface()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(WwwRoot(), "css"), "*.css"))
        {
            var css = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(css, @"font(?:-family)?\s*:\s*([^;}]+)"))
            {
                var value = m.Groups[1].Value.Trim();
                bool ok = value == "inherit" || value.Contains("system-ui", StringComparison.Ordinal)
                    || value.Contains("monospace", StringComparison.Ordinal);
                Assert.That(ok, Is.True, Path.GetFileName(file) + ": " + m.Value);
            }
        }
    }

    [Test]
    public void Number_boxes_are_all_in_the_mono_face()
    {
        var css = string.Concat(Directory.GetFiles(Path.Combine(WwwRoot(), "css"), "*.css").Select(File.ReadAllText));
        foreach (var box in new[] { ".cfg-num", ".dlg-input", ".of-input", ".bm-circin", ".bp-offin" })
            Assert.That(Rule(css, box), Does.Contain("monospace"), box);
    }

    [Test]
    public void Words_under_an_icon_are_one_size()
    {
        var css = Css("tool-panels.css");
        foreach (var words in new[] { ".bm-tlbl", ".bp-lbl", ".rp-iconbtn" })
            Assert.That(Rule(css, words), Does.Contain("font-size: 12px"), words);
    }

    [Test]
    public void Recorded_path_tabs_are_the_segmented_buttons_of_the_other_panels()
    {
        var html = File.ReadAllText(Path.Combine(WwwRoot(), "index.html"));
        foreach (var id in new[] { "rp-tab-rec", "rp-tab-play" })
            Assert.That(html, Does.Match("<button id=\"" + id + "\" class=\"ln-segbtn rp-tab\">"), id);
    }

    /// <summary>The first rule whose selector list starts with <paramref name="selector"/>.</summary>
    private static string Rule(string css, string selector)
    {
        var m = Regex.Match(css, @"(?m)^" + Regex.Escape(selector) + @"\s*\{([^}]*)\}");
        Assert.That(m.Success, Is.True, selector + " rule");
        return m.Groups[1].Value;
    }

    private static string Css(string name) => File.ReadAllText(Path.Combine(WwwRoot(), "css", name));

    private static string WwwRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AgOpenWeb.sln"))) dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "Could not locate repo root (AgOpenWeb.sln) from test base dir.");
        return Path.Combine(dir!.FullName, "Shared", "AgOpenWeb.RemoteServer", "wwwroot");
    }
}
