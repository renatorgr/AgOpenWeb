// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
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
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.Pipeline;

/// <summary>
/// AutoSteer beside a hard obstacle chosen as U-turn, through the whole cycle (fork only). On the
/// pass the U-turn before the obstacle keeps the implement out of it; two ways round that turn:
/// <para>The line moved one pass over beside the obstacle. AutoSteer steers the shortest way across,
/// about 50° off the passes, and a front boom swings ahead of the pivot: the beta robot (simulator)
/// took the end of a 12 m boom, 4.5 m ahead, 1.2 m into the obstacle with AutoSteer on. The tractor
/// itself went by the obstacle, so nothing was ahead of it.</para>
/// <para>AutoSteer engaged on the pass inside the obstacle's turn line, the obstacle straight ahead.
/// The planner had a turn at the far end of the pass, and with a turn planned the obstacle ahead
/// was not looked at: AutoSteer drove the tractor through it.</para>
/// Both now take AutoSteer off with the alarm before the implement is in the obstacle.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class HardObstacleGuardPipelineTests
{
    private const double Wheelbase = 2.5;
    private const double SpeedKmh = 8;
    private const double Hitch = 4.5;
    private const double Width = 12;
    private const double Reach = Width / 2;

    private readonly Wgs84 _origin = new(43.7128, -74.006);
    private LocalPlane _plane = null!;
    private IAudioService _audio = null!;
    private GpsService _gpsService = null!;
    private PipelineIntents _intents = null!;
    private ToolPositionService _toolPosition = null!;
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
        config.Vehicle.Wheelbase = Wheelbase;
        config.Tool.Width = Width;
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, (int)(Width * 100));
        config.Tool.Overlap = 0;
        config.Tool.IsToolFrontFixed = true;
        config.Tool.IsToolRearFixed = false;
        config.Tool.IsToolTrailing = false;
        config.Tool.IsToolTBT = false;
        // A fixed tool's working point Hitch m from the rear axle: the tractor's hitch there, the tool on it (#547).
        config.Vehicle.ThreePointHitchLength = config.Vehicle.FrontHitchLength = System.Math.Abs(Hitch);
        config.Tool.WorkingPointFromHitch = 0;
        config.Guidance.UTurnRadius = 8;
        config.Guidance.UTurnExtension = 2;
        config.Guidance.UTurnDistanceFromBoundary = 2;

        _gpsService = new GpsService();
        _gpsService.Start();
        var toolPosition = _toolPosition = new ToolPositionService(config);
        var coverage = new CoverageMapService(config);
        var sectionControl = new SectionControlService(toolPosition, coverage, config);
        var autoSteer = new AutoSteerService(new TrackGuidanceService(),
            Substitute.For<IUdpCommunicationService>(), _gpsService, config);
        var headingFusion = Substitute.For<IGpsHeadingFusionService>();
        headingFusion.FuseHeading(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>(),
                Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>())
            .Returns(ci => ci.ArgAt<double>(0));
        _audio = Substitute.For<IAudioService>();
        _intents = new PipelineIntents();
        _pipeline = new GpsPipelineService(
            _gpsService, toolPosition, new TrackGuidanceService(),
            sectionControl, coverage, autoSteer,
            new YouTurnGuidanceService(),
            new YouTurnStateMachine(
                new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance,
                    new Geometry.PolygonOffsetService(), config),
                new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, config),
                NullLogger<YouTurnStateMachine>.Instance, config),
            _audio,
            _intents,
            headingFusion,
            NullLogger<GpsPipelineService>.Instance,
            config,
            new PositionEstimator());

        _plane = new LocalPlane(_origin, new SharedFieldProperties());
        _pipeline.SetLocalPlane(_plane);
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

    // A 4 m square, U-turn before it, on pass 2 (E -20): the passes are 12 m apart from the AB line
    // on E -50, so pass 3 (E -8) goes by 10 m from its edge, outside its turn line (8 m: the U-turn
    // distance and half the boom).
    private static readonly (double E, double N)[] Square = { (-22, -60), (-18, -60), (-18, -56), (-22, -56) };

    private void Open(int pass, bool hard = true)
    {
        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = new[] { (-50.0, -200.0), (-50.0, 200.0), (50.0, 200.0), (50.0, -200.0) }
                    .Select(p => new BoundaryPoint(p.Item1, p.Item2, 0)).ToList(),
            },
        };
        boundary.InnerBoundaries.Add(new BoundaryPolygon
        {
            Points = Square.Select(p => new BoundaryPoint(p.E, p.N, 0)).ToList(),
            IsHard = hard,
            AtObstacle = ObstacleAction.Turn,
        });
        _pipeline.SetBoundary(boundary);
        _pipeline.SetActiveTrack(new Models.Track.Track
        {
            Name = "AB",
            Points = new List<Vec3> { new(-50, -150, 0), new(-50, 150, 0) },
            Type = Models.Track.TrackType.ABLine,
        }, passNumber: pass, nudgeOffset: 0);
        _pipeline.SetHasActiveField(true);
        _pipeline.SetYouTurnConfig(uTurnSkipRows: 0, isSkipWorkedMode: false, headlandCalculatedWidth: 0, headlandDistance: 0, isAlternateSkipMode: false);
        _pipeline.SetYouTurnEnabled(true);
        _pipeline.SetAutoSteerEngaged(true);
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
                Heading = headingRad * 180.0 / Math.PI, Speed = SpeedKmh / 3.6, // m/s
            },
            FixQuality = 4,
            IsValid = true,
        });
        return _results[^1];
    }

    private sealed class Drive
    {
        public double E, N, H;
        public double WorstIn; // the deepest a point of the boom got into the square, m
        public GpsCycleResult Last = null!;
        public double? OffAtN;
    }

    // How far inside the square a point is, 0 outside.
    private static double InSquare(double e, double n)
    {
        double d = Math.Min(Math.Min(e - -22, -18 - e), Math.Min(n - -60, -56 - n));
        return Math.Max(0, d);
    }

    /// <summary>Drive a bicycle north on the cycle's steer angle, <paramref name="cycles"/> tenths of a
    /// second, until AutoSteer goes off; <paramref name="before"/> runs ahead of each cycle.</summary>
    private Drive DriveNorth(double e, double n, int cycles, Action<int>? before = null)
    {
        var d = new Drive { E = e, N = n };
        double step = SpeedKmh / 3.6 * 0.1;
        for (int i = 0; i < cycles; i++)
        {
            before?.Invoke(i);
            var r = Cycle(d.E, d.N, d.H);
            d.Last = r;
            // The front boom: Hitch ahead of the pivot, Reach either side, a point every 0.25 m.
            for (double a = -Reach; a <= Reach; a += 0.25)
                d.WorstIn = Math.Max(d.WorstIn, InSquare(
                    d.E + Math.Sin(d.H) * Hitch + Math.Cos(d.H) * a,
                    d.N + Math.Cos(d.H) * Hitch - Math.Sin(d.H) * a));
            if (r.AutoSteerDisengagedThisCycle) { d.OffAtN = d.N; return d; }
            double steer = r.Guidance!.SteerAngle * Math.PI / 180.0;
            d.H += step / Wheelbase * Math.Tan(steer);
            d.E += Math.Sin(d.H) * step;
            d.N += Math.Cos(d.H) * step;
        }
        return d;
    }

    // Cycle 245 of a drive north from N -120: the pivot 5.6 m short of the square, the boom 1 m short.
    private const int BesideTheObstacle = 245;
    private const double StartN = -120;

    private Drive MoveLeftFromPass3(int atCycle)
        => DriveNorth(-8, StartN, 500, i => { if (i == atCycle) _intents.RequestGuidanceSnap(left: true); });

    [Test]
    public void TheLineMovedOverBesideAHardObstacle_AutoSteerGoesOffBeforeTheBoomIsInIt()
    {
        Open(pass: 3);
        var d = MoveLeftFromPass3(BesideTheObstacle);

        Assert.Multiple(() =>
        {
            Assert.That(d.OffAtN, Is.Not.Null, "AutoSteer went off");
            Assert.That(d.Last.DisengageReason, Is.EqualTo(GpsPipelineService.HardObstacleReason(0)));
            Assert.That(d.WorstIn, Is.EqualTo(0), "no point of the boom in the obstacle (1.9 m in before)");
            Assert.That(d.N, Is.LessThan(-60), "while the tractor is still short of it");
        });
        _audio.Received().Play(SoundEffect.UTurnTooClose);
    }

    [Test]
    public void TheLineMovedOverPastTheObstacle_AutoSteerStaysOn()
    {
        Open(pass: 3);
        // Cycle 300: the pivot at N -53, the boom past the square's north edge.
        var d = MoveLeftFromPass3(300);

        Assert.Multiple(() =>
        {
            Assert.That(d.OffAtN, Is.Null, d.Last.DisengageReason);
            Assert.That(d.E, Is.EqualTo(-20).Within(0.2), "on the pass it was moved to");
            Assert.That(d.WorstIn, Is.EqualTo(0));
        });
    }

    [Test]
    public void AnObstacleThatIsNotHard_IsLeftToTheTractorsOwnNet()
    {
        // Only the tractor keeps out of a soft obstacle: the boom goes over it, and AutoSteer goes
        // off when the tractor itself heads into its turn line with no turn before it, as before.
        Open(pass: 3, hard: false);
        var d = MoveLeftFromPass3(BesideTheObstacle);

        Assert.Multiple(() =>
        {
            Assert.That(d.WorstIn, Is.GreaterThan(0.5), "the boom was over it with AutoSteer on");
            Assert.That(d.Last.DisengageReason, Is.Not.EqualTo(GpsPipelineService.HardObstacleReason(0)));
        });
    }

    [Test]
    public void EngagedInsideTheTurnLineWithTheObstacleAhead_AutoSteerGoesOffThoughATurnIsPlanned()
    {
        // On the obstacle's pass 6.7 m short of it, inside its turn line (8 m): the turn planned
        // is the one at the north fence, 240 m on. AutoSteer drove through the obstacle.
        Open(pass: 2);
        var d = DriveNorth(-20, -66.7, 200);

        Assert.Multiple(() =>
        {
            Assert.That(d.OffAtN, Is.Not.Null.And.LessThan(-65.5), "AutoSteer went off within a metre");
            Assert.That(d.Last.DisengageReason, Does.Contain("Inner 1 ahead and no U-turn before it"));
            Assert.That(d.WorstIn, Is.EqualTo(0));
        });
        _audio.Received().Play(SoundEffect.UTurnTooClose);
    }

    [Test]
    public void EngagedInsideTheTurnLinePastTheObstacle_AutoSteerStaysOn()
    {
        // Still inside the turn line, the obstacle behind: nothing ahead but the pass.
        Open(pass: 2);
        var d = DriveNorth(-20, -54, 200);

        Assert.That(d.OffAtN, Is.Null, d.Last.DisengageReason);
    }
}
