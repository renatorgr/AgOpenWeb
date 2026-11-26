// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Coverage;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Pipeline;
using AgOpenWeb.Services.Section;
using AgOpenWeb.Services.Tests.Track;
using AgOpenWeb.Services.Tests.YouTurn;
using AgOpenWeb.Services.Tool;
using AgOpenWeb.Services.Track;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.Pipeline;

/// <summary>
/// The side a Go around obstacle was gone round on one pass, and the next pass through the same
/// obstacle, through the whole cycle (fork only). On a tester's field (Couto da Ribeira,
/// 26 Nov 2026, simulator) the side pinned on pass 19 was still held after the U-turn at the end
/// of the pass, 67 m past the obstacle: pass 17 has room only on the other side, so the operator
/// read "No room to go around Inner 5 on this pass: U-turn before it", the turn was made before
/// the obstacle and 319 m of the pass were left to do. The field is that of
/// <see cref="GoAroundPassesTests.SpikeBoundary"/>.
/// <para>Also: the pipeline and the U-turn planner use one <see cref="GoAroundPasses"/>. A host that
/// gave none (every scenario rig) had two, so the planner never saw a pin and planned from a
/// pass the pipeline did not steer.</para>
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class GoAroundPinAfterObstaclePipelineTests
{
    private const double SpeedKmh = 8;

    private readonly Wgs84 _origin = new(39.4194605, -7.529441833);
    private LocalPlane _plane = null!;
    private GpsService _gpsService = null!;
    private ToolPositionService _toolPosition = null!;
    private YouTurnStateMachine _stateMachine = null!;
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
        config.Vehicle.Wheelbase = 2.5;
        config.Tool.Width = 6;
        config.Tool.Offset = 0;
        config.Tool.Overlap = 0;
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, 600);
        config.Guidance.GoAroundRadius = 6;
        config.Guidance.GoAroundMargin = 1;

        _gpsService = new GpsService();
        _gpsService.Start();
        _pipeline = NewPipeline(goAround: null);
        _plane = new LocalPlane(_origin, new SharedFieldProperties());
        _pipeline.SetLocalPlane(_plane);
        _pipeline.SynchronousMode = true;
        _pipeline.Start();
        _results = new List<GpsCycleResult>();
        _pipeline.CycleCompleted += r => _results.Add(r);
    }

    private GpsPipelineService NewPipeline(GoAroundPasses? goAround)
    {
        var config = ConfigurationStore.Instance;
        _toolPosition = new ToolPositionService(config);
        var coverage = new CoverageMapService(config);
        var sectionControl = new SectionControlService(_toolPosition, coverage, config);
        var autoSteer = new AutoSteerService(new TrackGuidanceService(),
            Substitute.For<IUdpCommunicationService>(), _gpsService, config);
        var headingFusion = Substitute.For<IGpsHeadingFusionService>();
        headingFusion.FuseHeading(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>(),
                Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>())
            .Returns(ci => ci.ArgAt<double>(0));
        _stateMachine = new YouTurnStateMachine(
            new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, new PolygonOffsetService(), config),
            new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, config),
            NullLogger<YouTurnStateMachine>.Instance, config);
        return new GpsPipelineService(
            _gpsService, _toolPosition, new TrackGuidanceService(),
            sectionControl, coverage, autoSteer,
            new YouTurnGuidanceService(),
            _stateMachine,
            Substitute.For<IAudioService>(),
            new PipelineIntents(),
            headingFusion,
            NullLogger<GpsPipelineService>.Instance,
            config,
            new PositionEstimator(),
            goAround);
    }

    [TearDown]
    public void TearDown()
    {
        _pipeline.Stop();
        _gpsService.Stop();
    }

    private GpsCycleResult Cycle(double e, double n, double headingRad)
    {
        // The control loop keeps the tool's position; here the test does.
        _toolPosition.Update(new Vec3(e, n, headingRad), headingRad);
        var w = _plane.ConvertGeoCoordToWgs84(new GeoCoord(n, e));
        _gpsService.UpdateGpsData(new GpsData
        {
            CurrentPosition = new Position
            {
                Latitude = w.Latitude, Longitude = w.Longitude,
                Heading = headingRad * 180.0 / Math.PI, Speed = SpeedKmh / 3.6,
            },
            FixQuality = 4,
            IsValid = true,
        });
        return _results[^1];
    }

    [Test]
    public void The_pipeline_steers_by_the_planners_passes_when_the_host_gives_none()
    {
        Assert.That(_pipeline.GoAround, Is.SameAs(_stateMachine.GoAround));

        var given = new GoAroundPasses(ConfigurationStore.Instance, new PolygonOffsetService());
        var other = NewPipeline(given);
        Assert.That(other.GoAround, Is.SameAs(given), "the host's own, when it gives one");
    }

    [Test]
    public void The_pass_after_the_U_turn_goes_round_the_obstacle_on_its_own_side()
    {
        var field = GoAroundPassesTests.SpikeBoundary();
        var track = ObstacleTestField.Track("AB 0°");
        _pipeline.SetBoundary(field);
        _pipeline.SetHasActiveField(true);
        _pipeline.SetYouTurnEnabled(false);
        _pipeline.SetActiveTrack(track, passNumber: 6, nudgeOffset: 0);
        _pipeline.SetAutoSteerEngaged(true);

        // Pass 6 (39 m east) north along its bend round the spike's tip, from 60 m before the
        // obstacle to 60 m past it. The bend itself, from passes of the test's own.
        var bends = new GoAroundPasses(ConfigurationStore.Instance, new PolygonOffsetService());
        var bend6 = bends.For(track, 39, CurveProcessing.CreateOffsetCurve(track.Points, 39), field)!.Points;
        var notices = new List<string>();
        for (double n = -60; n <= 60; n += 0.5)
        {
            var at = bend6.OrderBy(p => Math.Abs(p.Northing - n)).First();
            if (Cycle(at.Easting, n, at.Heading).ObstacleNotice is { } told) notices.Add(told);
        }
        Assert.That(_pipeline.GoAround.PinnedSide(0), Is.EqualTo(-1), "the tip's side, still pinned 60 m on");
        Assert.That(_pipeline.GoAround.PinIsPassed(0), Is.True);
        Assert.That(notices, Is.Empty, "pass 6");

        // The U-turn onto pass 2 (15 m east), driven south from 60 m north of the obstacle.
        _pipeline.SetActiveTrack(track, passNumber: 2, nudgeOffset: 0);
        _pipeline.SetAutoSteerEngaged(true);
        for (double n = 60; n >= 55; n -= 0.5)
            if (Cycle(15, n, Math.PI).ObstacleNotice is { } told) notices.Add(told);

        Assert.Multiple(() =>
        {
            Assert.That(notices, Is.Empty, "no \"No room to go around Inner 1 on this pass\"");
            var steered = _pipeline.GoAround.Current;
            Assert.That(steered?.Blocked, Is.Empty);
            Assert.That(steered?.GoneRound, Is.EqualTo(new[] { 0 }), "pass 2 is bent round the obstacle");
            Assert.That(steered?.Rounds.Single().Side, Is.EqualTo(1), "round the body, west");
        });
    }
}
