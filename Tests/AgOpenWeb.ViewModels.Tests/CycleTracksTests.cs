// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;
using NUnit.Framework;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Cycle Lines forward and back (#336), as AgOpenGPS btnCycleLines / btnCycleLinesBk:
/// hidden tracks are skipped, the ends wrap, auto track goes off, and a track change
/// stops AutoSteer and auto U-turn.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class CycleTracksTests
{
    private static Track Line(string name, double e, bool visible = true) => new()
    {
        Name = name, Type = TrackType.ABLine, IsVisible = visible,
        Points = new() { new Vec3(e, 0, 0), new Vec3(e, 100, 0) },
    };

    private static MainViewModel WithTracks(params Track[] tracks)
    {
        var vm = new MainViewModelBuilder().Build();
        foreach (var t in tracks) vm.SavedTracks.Add(t);
        return vm;
    }

    [Test]
    public void Forward_SkipsHiddenAndWraps()
    {
        var vm = WithTracks(Line("A", 0), Line("B", 20, visible: false), Line("C", 40));
        vm.ActivateTrackAt(0);

        vm.CycleABLinesCommand!.Execute(null);
        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("C"));

        vm.CycleABLinesCommand!.Execute(null);
        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("A"));
    }

    [Test]
    public void Back_SkipsHiddenAndWraps()
    {
        var vm = WithTracks(Line("A", 0), Line("B", 20, visible: false), Line("C", 40));
        vm.ActivateTrackAt(0);

        vm.CycleABLinesBackCommand!.Execute(null);
        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("C"));

        vm.CycleABLinesBackCommand!.Execute(null);
        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("A"));
    }

    [Test]
    public void Cycle_TurnsAutoTrackOff()
    {
        var vm = WithTracks(Line("A", 0), Line("B", 20));
        vm.ActivateTrackAt(0);
        vm.IsAutoTrackEnabled = true;

        vm.CycleABLinesBackCommand!.Execute(null);

        Assert.That(vm.IsAutoTrackEnabled, Is.False);
    }

    [Test]
    public void TrackChange_StopsAutoSteerAndUTurn()
    {
        var vm = WithTracks(Line("A", 0), Line("B", 20));
        vm.ActivateTrackAt(0);
        vm.IsAutoSteerEngaged = true;
        vm.IsYouTurnEnabled = true;

        vm.CycleABLinesCommand!.Execute(null);

        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("B"));
        Assert.That(vm.IsAutoSteerEngaged, Is.False);
        Assert.That(vm.IsYouTurnEnabled, Is.False);
    }

    private static Track Loop(string name) => new()
    {
        Name = name, Type = TrackType.Curve, IsClosed = true,
        Points = new() { new Vec3(0, 0, 0), new Vec3(100, 0, 0), new Vec3(100, 100, 0), new Vec3(0, 100, 0), new Vec3(0, 0, 0) },
    };

    // Eucaliptal in the simulator (25 Nov 2026, hand test 55): U-turns on, the AB line, the
    // Boundary Curve and the AB line again by the cycle button, and U-turns stayed off. The
    // button turned them off before the closed track was chosen, so the closed track never
    // knew they were on.
    [Test]
    public void Cycle_OntoAClosedTrackAndBack_GivesUTurnsBack()
    {
        var vm = WithTracks(Line("A+ 15:45", 0), Loop("Boundary Curve"));
        vm.ActivateTrackAt(0);
        vm.IsYouTurnEnabled = true;
        var toasts = new List<string>();
        vm.FailureReported += toasts.Add;

        vm.CycleABLinesCommand!.Execute(null);
        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("Boundary Curve"));
        Assert.That(vm.IsYouTurnEnabled, Is.False, "no U-turns on a closed track (#421)");

        vm.CycleABLinesCommand!.Execute(null);
        Assert.Multiple(() =>
        {
            Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("A+ 15:45"));
            Assert.That(vm.IsYouTurnEnabled, Is.True);
            Assert.That(toasts, Has.Some.StartsWith("Automatic U-turns on again"));
        });

        // Back and forth again, as in the test: every time back on the AB line they are on.
        vm.CycleABLinesBackCommand!.Execute(null);
        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("Boundary Curve"));
        Assert.That(vm.IsYouTurnEnabled, Is.False);
        vm.CycleABLinesBackCommand!.Execute(null);
        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("A+ 15:45"));
        Assert.That(vm.IsYouTurnEnabled, Is.True);
    }

    [Test]
    public void Cycle_OntoAClosedTrack_WithUTurnsOff_LeavesThemOff()
    {
        var vm = WithTracks(Line("A", 0), Loop("Boundary Curve"));
        vm.ActivateTrackAt(0);

        vm.CycleABLinesCommand!.Execute(null);
        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("Boundary Curve"));
        vm.CycleABLinesCommand!.Execute(null);
        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("A"));

        Assert.That(vm.IsYouTurnEnabled, Is.False);
    }

    [Test]
    public void Cycle_OntoAnotherOpenTrack_StillTurnsUTurnsOff()
    {
        var vm = WithTracks(Line("A", 0), Loop("Boundary Curve"), Line("B", 20));
        vm.ActivateTrackAt(0);
        vm.IsYouTurnEnabled = true;

        vm.CycleABLinesCommand!.Execute(null);
        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("Boundary Curve"));
        vm.CycleABLinesCommand!.Execute(null);
        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("B"));
        Assert.That(vm.IsYouTurnEnabled, Is.True, "back, as after a pick from the list");

        vm.CycleABLinesCommand!.Execute(null);
        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("A"));
        Assert.That(vm.IsYouTurnEnabled, Is.False, "open to open: off, as AgOpenGPS");
    }

    [Test]
    public void OnlyOneVisibleTrack_NothingChanges()
    {
        var vm = WithTracks(Line("A", 0), Line("B", 20, visible: false));
        vm.ActivateTrackAt(0);
        vm.IsAutoSteerEngaged = true;

        vm.CycleABLinesCommand!.Execute(null);

        Assert.That(vm.SelectedTrack?.Name, Is.EqualTo("A"));
        Assert.That(vm.IsAutoSteerEngaged, Is.True, "no track change, so steering carries on");
    }
}
