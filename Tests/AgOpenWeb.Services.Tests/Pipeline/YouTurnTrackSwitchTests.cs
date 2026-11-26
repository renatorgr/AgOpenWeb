// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Reflection;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Coverage;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Pipeline;
using AgOpenWeb.Services.Section;
using AgOpenWeb.Services.Tool;
using AgOpenWeb.Services.Track;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgOpenWeb.Services.Tests.Pipeline;

/// <summary>
/// Fable review (2026-11-03): a turn planned on one track survived a switch to another. It
/// stayed on the map, started "from here" mid-field on the new track once the tractor was
/// level with the old arc, and completed onto the old track's pass number. AgOpenGPS resets
/// the U-turn on a track change (FormBuildTracks: yt.ResetYouTurn).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class YouTurnTrackSwitchTests
{
    private GpsService _gpsService = null!;
    private GpsPipelineService _pipeline = null!;
    private List<GpsCycleResult> _results = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.Vehicle.AntennaPivot = 0;
        config.Vehicle.AntennaOffset = 0;
        config.Vehicle.AntennaHeight = 0;
        config.Tool.Width = 6;
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, 600);

        _gpsService = new GpsService();
        _gpsService.Start();

        var toolPosition = new ToolPositionService(config);
        var coverage = new CoverageMapService(config);
        var sectionControl = new SectionControlService(toolPosition, coverage, config);
        var autoSteer = new AutoSteerService(new TrackGuidanceService(),
            Substitute.For<IUdpCommunicationService>(), _gpsService, config);

        var headingFusion = Substitute.For<IGpsHeadingFusionService>();
        headingFusion.FuseHeading(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>(),
                Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>())
            .Returns(ci => ci.ArgAt<double>(0));

        _pipeline = new GpsPipelineService(
            _gpsService, toolPosition, new TrackGuidanceService(),
            sectionControl, coverage, autoSteer,
            new YouTurnGuidanceService(),
            new YouTurnStateMachine(
                new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance,
                    Substitute.For<AgOpenWeb.Services.Geometry.IPolygonOffsetService>(), config),
                new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, config),
                NullLogger<YouTurnStateMachine>.Instance, config),
            Substitute.For<IAudioService>(),
            new PipelineIntents(),
            headingFusion,
            NullLogger<GpsPipelineService>.Instance,
            config,
            new PositionEstimator());

        _pipeline.SetLocalPlane(new LocalPlane( new Wgs84(43.7128, -74.006), new SharedFieldProperties()));
        _pipeline.SynchronousMode = true;
        _pipeline.Start();

        _results = new List<GpsCycleResult>();
        _pipeline.CycleCompleted += r => _results.Add(r);
    }

    [TearDown]
    public void TearDown()
    {
        _pipeline.Stop();
        _gpsService.Stop();
    }

    private GpsCycleResult Last => _results[^1];

    /// <summary>
    /// Reach into the cycle-owned <see cref="YouTurnWorkingState"/> via reflection
    /// to simulate a turn already in progress. Production code mutates this only
    /// from the cycle thread (state-machine ticks, manual triggers, ClearState);
    /// the test reaches in directly to skip the state-machine geometry that this
    /// test isn't trying to exercise.
    /// </summary>
    private YouTurnWorkingState GetCycleYouTurn()
    {
        var fld = typeof(GpsPipelineService).GetField("_youTurn",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(fld, Is.Not.Null, "Reflection target _youTurn missing — pipeline internals changed");
        return (YouTurnWorkingState)fld!.GetValue(_pipeline)!;
    }

    private static GpsData ValidFix() => new()
    {
        CurrentPosition = new Position { Latitude = 43.7128, Longitude = -74.006 },
        FixQuality = 4,
        IsValid = true,
    };

    private static readonly Models.Track.Track TrackA =
        Models.Track.Track.FromABLine("AB 1", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
    private static readonly Models.Track.Track TrackB =
        Models.Track.Track.FromABLine("AB 2", new Vec3(30, -100, 0), new Vec3(30, 100, 0));

    // On track A, guiding, with a turn planned onto pass -1 and a snake order in hand.
    private YouTurnWorkingState PlannedOnTrackA()
    {
        _pipeline.SetActiveTrack(TrackA, passNumber: 0, nudgeOffset: 0);
        _pipeline.SetAutoSteerEngaged(true);
        _pipeline.SetYouTurnEnabled(true);
        _gpsService.UpdateGpsData(ValidFix());

        var turn = GetCycleYouTurn();
        turn.TurnPath = new List<Vec3> { new(3, 40, 0), new(0, 50, 0), new(-3, 40, Math.PI) };
        turn.PlannedNextPath = -1;
        turn.ReturnPassTargetPath = -1;
        turn.SnakeSequence = new List<int> { 0, -1, 1 };
        turn.SnakeIndex = 0;
        turn.AltSign = 1;
        return turn;
    }

    [Test]
    public void AnotherTrack_DropsTheTurnPlannedOnTheOldOne()
    {
        var turn = PlannedOnTrackA();

        _pipeline.SetActiveTrack(TrackB, passNumber: 0, nudgeOffset: 0);
        _gpsService.UpdateGpsData(ValidFix());

        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Null);
            Assert.That(turn.PlannedNextPath, Is.Null, "it would land on track A's pass number on track B");
            Assert.That(turn.ReturnPassTargetPath, Is.Null);
            Assert.That(turn.SnakeSequence, Is.Null, "track A's order");
            Assert.That(turn.SnakeIndex, Is.EqualTo(-1));
            Assert.That(turn.AltSign, Is.EqualTo(0));
            Assert.That(Last.YouTurn!.TurnPath, Is.Null, "the map drops it too");
        });
    }

    [Test]
    public void AnotherTrack_EndsATurnBeingDriven()
    {
        var turn = PlannedOnTrackA();
        turn.IsTriggered = turn.IsExecuting = true;

        _pipeline.SetActiveTrack(TrackB, passNumber: 0, nudgeOffset: 0);
        _gpsService.UpdateGpsData(ValidFix());

        Assert.Multiple(() =>
        {
            Assert.That(turn.IsExecuting, Is.False);
            Assert.That(Last.Guidance!.HowManyPathsAway, Is.EqualTo(0), "not completed onto track A's pass -1");
            Assert.That(Last.YouTurn!.IsExecuting, Is.False);
        });
    }

    [Test]
    public void AnotherImplementWidth_ForgetsTheOrderMadeForTheOldPasses()
    {
        // The passes are laid a width apart: with another width pass 1 is another strip of ground,
        // and the snake's order (and what it found worked or open on each pass) was made for the
        // old strips.
        var turn = PlannedOnTrackA();
        var tool = ConfigurationStore.Instance.Tool;
        tool.SetSectionWidth(0, 800); // 6 m before
        _gpsService.UpdateGpsData(ValidFix());

        Assert.Multiple(() =>
        {
            Assert.That(turn.SnakeSequence, Is.Null, "the order for the old spacing");
            Assert.That(turn.SnakeIndex, Is.EqualTo(-1));
            Assert.That(turn.TurnPath, Is.Null, "the turn planned onto the old pass");
            Assert.That(turn.ReturnPassTargetPath, Is.Null);
        });

        // The same width again: nothing forgotten, the same order where it had got to.
        var order = new List<int> { 0, -1, 1 };
        var planned = new List<Vec3> { new(3, 40, 0), new(0, 50, 0), new(-3, 40, Math.PI) };
        turn.SnakeSequence = order;
        turn.SnakeIndex = 1;
        turn.TurnPath = planned;
        _gpsService.UpdateGpsData(ValidFix());
        Assert.Multiple(() =>
        {
            Assert.That(turn.SnakeSequence, Is.SameAs(order));
            Assert.That(turn.SnakeIndex, Is.EqualTo(1));
            Assert.That(turn.TurnPath, Is.SameAs(planned));
        });
    }

    [Test]
    public void AnotherImplementOffset_PlansTheTurnAgain_AndKeepsTheOrder()
    {
        // The passes stay where they are, but the implement now reaches further to one side: a
        // turn planned beside a hard fence or a hard obstacle was kept clear of it by the old reach.
        var turn = PlannedOnTrackA();
        var order = turn.SnakeSequence;
        int at = turn.SnakeIndex;
        ConfigurationStore.Instance.Tool.Offset = 1.5;
        _gpsService.UpdateGpsData(ValidFix());

        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Null, "the turn planned for the old reach");
            Assert.That(turn.ReturnPassTargetPath, Is.Null);
            Assert.That(turn.SnakeSequence, Is.SameAs(order), "the passes did not move: the order holds");
            Assert.That(turn.SnakeIndex, Is.EqualTo(at));
        });

        // The same offset again: a turn planned since is kept.
        var planned = new List<Vec3> { new(3, 40, 0), new(0, 50, 0), new(-3, 40, Math.PI) };
        turn.TurnPath = planned;
        _gpsService.UpdateGpsData(ValidFix());
        Assert.That(turn.TurnPath, Is.SameAs(planned));
    }

    [Test]
    public void AnotherImplementOffset_LeavesATurnBeingDrivenAlone()
    {
        // A turn being driven is not planned again (as for a changed obstacle). In this fixture
        // the tractor is past the three-point path, so the turn is driven to its end in the
        // cycle: onto pass -1. Dropped, it stayed "being driven" with no path, on pass 0.
        var turn = PlannedOnTrackA();
        turn.IsTriggered = turn.IsExecuting = true;
        ConfigurationStore.Instance.Tool.Offset = 1.5;
        _gpsService.UpdateGpsData(ValidFix());

        Assert.Multiple(() =>
        {
            Assert.That(turn.IsExecuting, Is.False, "the turn ended by being driven");
            Assert.That(Last.Guidance!.HowManyPathsAway, Is.EqualTo(-1), "onto the pass it was made for");
        });
    }

    [Test]
    public void PlanningForTheFrontImplement_PlansTheTurnAgain_AndBackToTheTractorToo()
    {
        // The operator's choice alone (U-turns planned for the tractor / the front implement), with
        // nothing else changed: a turn planned and not yet driven was made for the other point of
        // the rig, so it is planned again; the order holds.
        var config = ConfigurationStore.Instance;
        config.Tool.SetToolType("front");
        config.Vehicle.FrontHitchLength = 4.5;
        config.Tool.WorkingPointFromHitch = 0.2;
        var turn = PlannedOnTrackA();
        var order = turn.SnakeSequence;

        config.Guidance.UTurnForFrontTool = true;
        _gpsService.UpdateGpsData(ValidFix());
        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Null, "the turn planned for the tractor");
            Assert.That(turn.SnakeSequence, Is.SameAs(order), "the passes did not move: the order holds");
        });

        // The same choice again: a turn planned since is kept.
        var planned = new List<Vec3> { new(3, 40, 0), new(0, 50, 0), new(-3, 40, Math.PI) };
        turn.TurnPath = planned;
        _gpsService.UpdateGpsData(ValidFix());
        Assert.That(turn.TurnPath, Is.SameAs(planned));

        config.Guidance.UTurnForFrontTool = false;
        _gpsService.UpdateGpsData(ValidFix());
        Assert.That(turn.TurnPath, Is.Null, "the turn planned for the front implement");
    }

    [Test]
    public void SwingingTheExitForTheTrailedImplement_PlansTheTurnAgain_AndBackToTheTractorToo()
    {
        // The operator's choice alone (U-turns planned for the tractor / the trailed implement):
        // a turn planned and not yet driven has the other exit, so it is planned again; the order
        // holds. The same when the drill's length changes with the choice on.
        var config = ConfigurationStore.Instance;
        config.Tool.SetToolType("trailing");
        config.Tool.TrailingHitchLength = 4.15;
        var turn = PlannedOnTrackA();
        var order = turn.SnakeSequence;

        config.Guidance.UTurnForTrailedTool = true;
        _gpsService.UpdateGpsData(ValidFix());
        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Null, "the turn planned for the tractor");
            Assert.That(turn.SnakeSequence, Is.SameAs(order), "the passes did not move: the order holds");
        });

        // The same choice again: a turn planned since is kept.
        var planned = new List<Vec3> { new(3, 40, 0), new(0, 50, 0), new(-3, 40, Math.PI) };
        turn.TurnPath = planned;
        _gpsService.UpdateGpsData(ValidFix());
        Assert.That(turn.TurnPath, Is.SameAs(planned));

        config.Tool.TrailingHitchLength = 5.0;
        _gpsService.UpdateGpsData(ValidFix());
        Assert.That(turn.TurnPath, Is.Null, "the exit swung for a shorter drill");

        turn.TurnPath = planned;
        _gpsService.UpdateGpsData(ValidFix());
        Assert.That(turn.TurnPath, Is.SameAs(planned));
        config.Guidance.UTurnForTrailedTool = false;
        _gpsService.UpdateGpsData(ValidFix());
        Assert.That(turn.TurnPath, Is.Null, "the turn with its exit swung");
    }

    [Test]
    public void PlanningForTheFrontImplement_LeavesATurnBeingDrivenAlone()
    {
        var config = ConfigurationStore.Instance;
        config.Tool.SetToolType("front");
        config.Vehicle.FrontHitchLength = 4.5;
        config.Tool.WorkingPointFromHitch = 0.2;
        var turn = PlannedOnTrackA();
        turn.IsTriggered = turn.IsExecuting = true;
        config.Guidance.UTurnForFrontTool = true;
        _gpsService.UpdateGpsData(ValidFix());

        Assert.Multiple(() =>
        {
            Assert.That(turn.IsExecuting, Is.False, "the turn ended by being driven");
            Assert.That(Last.Guidance!.HowManyPathsAway, Is.EqualTo(-1), "onto the pass it was made for");
        });
    }

    [Test]
    public void TheSameTrackAgain_KeepsThePlannedTurn()
    {
        var turn = PlannedOnTrackA();

        _pipeline.SetActiveTrack(TrackA, passNumber: 0, nudgeOffset: 0); // a resync, not a switch
        _gpsService.UpdateGpsData(ValidFix());

        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Not.Null);
            Assert.That(turn.PlannedNextPath, Is.EqualTo(-1));
            Assert.That(turn.SnakeSequence, Is.Not.Null);
        });
    }
}
