// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.YouTurn;
using AgOpenWeb.Services.Profile;
using AgOpenWeb.Services.Tram;

namespace AgOpenWeb.Services.Tests;

/// <summary>The U-Turn and Tram tabs of the tool configuration are saved with the tool
/// profile (#330). They were saved with the vehicle (tram width with the field), so
/// swapping a drill for a sprayer on one tractor kept the drill's turn and tram
/// settings.</summary>
[TestFixture]
public class ToolProfileUTurnTramTests
{
    private string _tools = null!;
    private string _vehicles = null!;
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aow-tooluturn-" + Guid.NewGuid().ToString("N"));
        _tools = Path.Combine(_root, "Tools");
        _vehicles = Path.Combine(_root, "Vehicles");
        Directory.CreateDirectory(_tools);
        Directory.CreateDirectory(_vehicles);
    }

    [TearDown]
    public void TearDown() { try { Directory.Delete(_root, true); } catch { } }

    private static ConfigurationStore Sprayer()
    {
        var s = new ConfigurationStore();
        s.Guidance.UTurnRadius = 11.5;
        s.Guidance.UTurnExtension = 32.0;
        s.Guidance.UTurnDistanceFromBoundary = 6.5;
        s.Guidance.UTurnStyle = (int)YouTurnType.KStyle;
        s.Guidance.UTurnSmoothing = 22;
        s.Tram.TramWidth = 21.0;
        s.Guidance.TramPasses = 7;
        s.Guidance.TramDisplay = false;
        s.Guidance.TramLine = 4;
        s.Tram.IsOuterInverted = true;
        return s;
    }

    private static void AssertSprayer(ConfigurationStore d) => Assert.Multiple(() =>
    {
        Assert.That(d.Guidance.UTurnRadius, Is.EqualTo(11.5));
        Assert.That(d.Guidance.UTurnExtension, Is.EqualTo(32.0));
        Assert.That(d.Guidance.UTurnDistanceFromBoundary, Is.EqualTo(6.5));
        Assert.That(d.Guidance.UTurnStyle, Is.EqualTo((int)YouTurnType.KStyle));
        Assert.That(d.Guidance.UTurnSmoothing, Is.EqualTo(22));
        Assert.That(d.Tram.TramWidth, Is.EqualTo(21.0));
        Assert.That(d.Tram.IsOuterInverted, Is.True);
        Assert.That(d.Guidance.TramPasses, Is.EqualTo(7));
        Assert.That(d.Guidance.TramDisplay, Is.False);
        Assert.That(d.Guidance.TramLine, Is.EqualTo(4));
    });

    [Test]
    public void UTurnAndTramSettings_RoundTripThroughTheToolProfile()
    {
        ToolProfileJsonService.Save(_tools, "Sprayer", Sprayer());
        var dst = new ConfigurationStore();
        Assert.That(ToolProfileJsonService.Load(_tools, "Sprayer", dst), Is.True);
        AssertSprayer(dst);
    }

    [Test]
    public void LoadingAVehicle_LeavesTheToolsUTurnAndTramSettingsAlone()
    {
        // Another tractor's profile, saved while a drill with other settings was on it.
        VehicleProfileJsonService.Save(_vehicles, "Other tractor", new ConfigurationStore());

        var live = Sprayer();
        Assert.That(VehicleProfileJsonService.Load(_vehicles, "Other tractor", live), Is.True);
        AssertSprayer(live);
    }

    [Test]
    public void SwitchingTools_OnOneTractor_BringsEachToolsSettings()
    {
        var store = Sprayer();
        ToolProfileJsonService.Save(_tools, "Sprayer", store);
        ToolProfileJsonService.Save(_tools, "Drill", new ConfigurationStore());

        Assert.That(ToolProfileJsonService.Load(_tools, "Drill", store), Is.True);
        var def = new GuidanceConfig();
        Assert.Multiple(() =>
        {
            Assert.That(store.Guidance.UTurnRadius, Is.EqualTo(def.UTurnRadius));
            Assert.That(store.Guidance.UTurnDistanceFromBoundary, Is.EqualTo(def.UTurnDistanceFromBoundary));
            Assert.That(store.Tram.TramWidth, Is.EqualTo(new TramConfig().TramWidth));
            Assert.That(store.Guidance.TramPasses, Is.EqualTo(def.TramPasses));
        });

        Assert.That(ToolProfileJsonService.Load(_tools, "Sprayer", store), Is.True);
        AssertSprayer(store);
    }

    [Test]
    public void AToolFileWithoutTheSections_LoadsTheDefaults_NotThePreviousTool()
    {
        ToolProfileJsonService.Save(_tools, "Plain", new ConfigurationStore());
        var path = Path.Combine(_tools, "Plain.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.That(node.Remove("youTurn"), Is.True, "the U-turn section is written");
        Assert.That(node.Remove("tram"), Is.True, "the tram section is written");
        File.WriteAllText(path, node.ToJsonString());

        var dst = Sprayer();   // as left by the tool that was active before
        Assert.That(ToolProfileJsonService.Load(_tools, "Plain", dst), Is.True);

        var def = new GuidanceConfig();
        Assert.Multiple(() =>
        {
            Assert.That(dst.Guidance.UTurnRadius, Is.EqualTo(def.UTurnRadius));
            Assert.That(dst.Guidance.UTurnExtension, Is.EqualTo(def.UTurnExtension));
            Assert.That(dst.Guidance.UTurnDistanceFromBoundary, Is.EqualTo(def.UTurnDistanceFromBoundary));
            Assert.That(dst.Guidance.UTurnStyle, Is.EqualTo(def.UTurnStyle));
            Assert.That(dst.Guidance.UTurnSmoothing, Is.EqualTo(def.UTurnSmoothing));
            Assert.That(dst.Tram.TramWidth, Is.EqualTo(new TramConfig().TramWidth));
            Assert.That(dst.Guidance.TramPasses, Is.EqualTo(def.TramPasses));
            Assert.That(dst.Guidance.TramDisplay, Is.EqualTo(def.TramDisplay));
            Assert.That(dst.Guidance.TramLine, Is.EqualTo(def.TramLine));
            Assert.That(dst.Tram.IsOuterInverted, Is.False);
        });
    }

    [Test]
    public void OpeningAField_LeavesTheToolsTramSettingsAlone()
    {
        string field = Path.Combine(_root, "Field");
        Directory.CreateDirectory(field);

        // A field saved while another tool's tram settings were live...
        var other = new TramConfig { TramWidth = 36.0, StartPass = 5, IsOuterInverted = false };
        TramConfigFileService.Save(field, other);

        // ...opened with the sprayer on: its width, first pass and outer/inner stay.
        var tram = new TramConfig { TramWidth = 21.0, StartPass = 3, IsOuterInverted = true };
        TramConfigFileService.Load(field, tram);

        Assert.Multiple(() =>
        {
            Assert.That(tram.TramWidth, Is.EqualTo(21.0));
            Assert.That(tram.StartPass, Is.EqualTo(3));
            Assert.That(tram.IsOuterInverted, Is.True);
        });
    }

    [Test]
    public void GoAroundAndLineEntrySettings_OutOfRangeInAFile_AreReadInsideTheirLimits()
    {
        // A profile edited by hand, or written by a build with other limits: a Go around radius of
        // 0 bent the pass at a corner, a margin of 0 took the implement along the tree's bark, and
        // 500 m of straight before the crop found no way onto any pass.
        var odd = new ConfigurationStore();
        odd.Guidance.GoAroundRadius = 0;
        odd.Guidance.GoAroundMargin = -2;
        odd.Guidance.LineEntryStraight = 500;
        ToolProfileJsonService.Save(_tools, "Odd", odd);

        var d = new ConfigurationStore();
        Assert.That(ToolProfileJsonService.Load(_tools, "Odd", d), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(d.Guidance.GoAroundRadius, Is.EqualTo(GuidanceConfig.MinGoAroundRadius));
            Assert.That(d.Guidance.GoAroundMargin, Is.EqualTo(GuidanceConfig.MinGoAroundMargin));
            Assert.That(d.Guidance.LineEntryStraight, Is.EqualTo(GuidanceConfig.MaxLineEntryStraight));
        });

        odd.Guidance.GoAroundRadius = 900;
        odd.Guidance.GoAroundMargin = 90;
        odd.Guidance.LineEntryStraight = -3;
        ToolProfileJsonService.Save(_tools, "Odd", odd);
        Assert.That(ToolProfileJsonService.Load(_tools, "Odd", d), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(d.Guidance.GoAroundRadius, Is.EqualTo(GuidanceConfig.MaxGoAroundRadius));
            Assert.That(d.Guidance.GoAroundMargin, Is.EqualTo(GuidanceConfig.MaxGoAroundMargin));
            Assert.That(d.Guidance.LineEntryStraight, Is.EqualTo(0));
        });
    }

    [Test]
    public void GoAroundAndLineEntrySettings_ThatAreNoNumberInAFile_AreReadAsTheDefaults()
    {
        // The profile files may hold NaN and the infinities, and a clamp lets NaN through: the
        // bend round an obstacle and the way onto a pass were then built from no number at all.
        var odd = new ConfigurationStore();
        odd.Guidance.GoAroundRadius = double.NaN;
        odd.Guidance.GoAroundMargin = double.NaN;
        odd.Guidance.LineEntryStraight = double.NaN;
        ToolProfileJsonService.Save(_tools, "Odd", odd);
        Assert.That(File.ReadAllText(Path.Combine(_tools, "Odd.json")), Does.Contain("NaN"), "the file holds them");

        var defaults = new GuidanceConfig();
        var d = new ConfigurationStore();
        d.Guidance.GoAroundRadius = 9;
        d.Guidance.GoAroundMargin = 2;
        d.Guidance.LineEntryStraight = 7;
        Assert.That(ToolProfileJsonService.Load(_tools, "Odd", d), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(d.Guidance.GoAroundRadius, Is.EqualTo(defaults.GoAroundRadius));
            Assert.That(d.Guidance.GoAroundMargin, Is.EqualTo(defaults.GoAroundMargin));
            Assert.That(d.Guidance.LineEntryStraight, Is.EqualTo(defaults.LineEntryStraight));
        });

        odd.Guidance.GoAroundRadius = double.PositiveInfinity;
        odd.Guidance.LineEntryStraight = double.PositiveInfinity;
        ToolProfileJsonService.Save(_tools, "Odd", odd);
        Assert.That(ToolProfileJsonService.Load(_tools, "Odd", d), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(d.Guidance.GoAroundRadius, Is.EqualTo(defaults.GoAroundRadius));
            Assert.That(d.Guidance.LineEntryStraight, Is.EqualTo(defaults.LineEntryStraight));
        });
    }

    [Test]
    public void UTurnsPlannedForTheFrontImplement_IsSavedWithTheToolProfile_AndOffInOneSavedBeforeIt()
    {
        // Fork only: the choice is the implement's (a front boom, a combine's header), and a
        // profile saved before it, or loaded after one that had it on, plans for the pivot.
        var boom = new ConfigurationStore();
        boom.Guidance.UTurnForFrontTool = true;
        ToolProfileJsonService.Save(_tools, "Boom", boom);
        ToolProfileJsonService.Save(_tools, "Drill", new ConfigurationStore());

        var d = new ConfigurationStore();
        Assert.That(ToolProfileJsonService.Load(_tools, "Boom", d), Is.True);
        Assert.That(d.Guidance.UTurnForFrontTool, Is.True, "saved with the profile");
        Assert.That(ToolProfileJsonService.Load(_tools, "Drill", d), Is.True);
        Assert.That(d.Guidance.UTurnForFrontTool, Is.False, "another implement's profile turns it off");

        // A profile from before the setting: the key is not in the file.
        string file = Path.Combine(_tools, "Boom.json");
        string text = File.ReadAllText(file);
        Assert.That(text, Does.Contain("\"forFrontTool\": true"));
        File.WriteAllText(file, System.Text.RegularExpressions.Regex.Replace(text, "\\s*\"forFrontTool\": true,?", ""));
        d.Guidance.UTurnForFrontTool = true;
        Assert.That(ToolProfileJsonService.Load(_tools, "Boom", d), Is.True);
        Assert.That(d.Guidance.UTurnForFrontTool, Is.False, "off unless the profile says on");

        // An AgOpenGPS profile: AgOpenGPS plans for the tractor, whatever the implement open before had.
        d.Guidance.UTurnForFrontTool = true;
        AgOpenWeb.Services.Profile.AgOpenGpsSettings.ApplyToStore(new System.Collections.Generic.Dictionary<string, string>(), "Imported", "Imported.xml", d);
        Assert.That(d.Guidance.UTurnForFrontTool, Is.False, "an AgOpenGPS profile plans for the tractor");
    }

    [Test]
    public void UTurnRadiusAndExtension_OutOfRangeOrNoNumberInAFile_AreReadInsideTheirLimits()
    {
        // As the Go around radius above: the settings page holds the radius to 2-100 m and the
        // extension to 3-50 m (AgOpenGPS's boxes), a profile read from a file did not.
        var odd = new ConfigurationStore();
        odd.Guidance.UTurnRadius = 0.5;
        odd.Guidance.UTurnExtension = 900;
        ToolProfileJsonService.Save(_tools, "Odd", odd);

        var d = new ConfigurationStore();
        Assert.That(ToolProfileJsonService.Load(_tools, "Odd", d), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(d.Guidance.UTurnRadius, Is.EqualTo(2.0));
            Assert.That(d.Guidance.UTurnExtension, Is.EqualTo(50.0));
        });

        odd.Guidance.UTurnRadius = double.PositiveInfinity;
        odd.Guidance.UTurnExtension = double.NaN;
        ToolProfileJsonService.Save(_tools, "Odd", odd);
        var defaults = new GuidanceConfig();
        Assert.That(ToolProfileJsonService.Load(_tools, "Odd", d), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(d.Guidance.UTurnRadius, Is.EqualTo(defaults.UTurnRadius));
            Assert.That(d.Guidance.UTurnExtension, Is.EqualTo(defaults.UTurnExtension));
        });
    }
}
