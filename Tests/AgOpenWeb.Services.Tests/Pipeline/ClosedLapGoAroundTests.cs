// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
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
/// A boundary curve's lap across a hard obstacle chosen as Go around, through the whole cycle
/// (fork only). Laps were never bent round Go around obstacles: on a tester's field (Eucaliptal,
/// 22 Nov 2026, a front sprayer 8 m wide 4.9 m ahead) the second lap was shown and steered across
/// hard Go around obstacles with AutoSteer on. The lap is now bent round them as an open pass is,
/// shown and steered as a loop, and with no room either side AutoSteer goes off before it.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class ClosedLapGoAroundTests
{
    private const double Wheelbase = 2.9;
    private const double SpeedKmh = 8;
    private const double Hitch = 4.9;  // the boom ahead of the pivot
    private const double Width = 8;
    private const double Reach = Width / 2;

    private readonly Wgs84 _origin = new(43.7128, -74.006);
    private LocalPlane _plane = null!;
    private IAudioService _audio = null!;
    private GpsService _gpsService = null!;
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
        config.Vehicle.MaxSteerAngle = 31;
        config.Tool.Width = Width;
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, (int)(Width * 100));
        config.Tool.Overlap = 0;
        config.Tool.IsToolFrontFixed = true;
        config.Tool.IsToolRearFixed = false;
        config.Tool.IsToolTrailing = false;
        config.Tool.IsToolTBT = false;
        config.Vehicle.ThreePointHitchLength = config.Vehicle.FrontHitchLength = Hitch;
        config.Tool.WorkingPointFromHitch = 0;
        config.Guidance.UTurnRadius = 8;
        config.Guidance.UTurnDistanceFromBoundary = 2;
        config.Guidance.GoAroundRadius = 6;
        config.Guidance.GoAroundMargin = 1;

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
            new PipelineIntents(),
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

    // A field 100 x 200 m, clockwise, so the laps are to the right of the fence: lap 1 (pass 0)
    // 4 m in, lap 2 (pass 1) 12 m in, up the west side at E -38.
    private static readonly List<Vec3> Fence = new()
    {
        new(-50, -100, 0), new(-50, 100, 0), new(50, 100, 0), new(50, -100, 0),
    };

    // A clump of trees 8 x 10 m with lap 2 along its west edge: grown by half the boom and the
    // 1 m margin it needs the lap 5 m to the west (7 m from the fence) or 13 m to the east.
    private static readonly (double E, double N)[] Clump = { (-38, 0), (-30, 0), (-30, 10), (-38, 10) };

    // From the fence to 40 m in: no room either side of lap 2 (the fence on one, a bend of more
    // than 20 m, two boom widths, on the other).
    private static readonly (double E, double N)[] Wall = { (-50, 0), (-10, 0), (-10, 6), (-50, 6) };

    private (double E, double N)[] _obstacle = Clump;

    private static Models.Track.Track BoundaryCurve() => new()
    {
        Name = "Boundary Curve",
        Points = CurveProcessing.DensifyLoop(Fence),
        Type = Models.Track.TrackType.Curve,
        IsClosed = true,
    };

    private void Open(int pass, (double E, double N)[] obstacle, bool hard = true)
    {
        _obstacle = obstacle;
        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = Fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                IsHard = true,
            },
        };
        boundary.InnerBoundaries.Add(new BoundaryPolygon
        {
            Points = obstacle.Select(p => new BoundaryPoint(p.E, p.N, 0)).ToList(),
            IsHard = hard,
            AtObstacle = ObstacleAction.GoRound,
        });
        _pipeline.SetBoundary(boundary);
        _pipeline.SetActiveTrack(BoundaryCurve(), passNumber: pass, nudgeOffset: 0);
        _pipeline.SetHasActiveField(true);
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

    // How far inside the obstacle (a box square to the field) a point is, 0 outside.
    private double InObstacle(double e, double n)
    {
        double e0 = _obstacle.Min(p => p.E), e1 = _obstacle.Max(p => p.E), n0 = _obstacle.Min(p => p.N), n1 = _obstacle.Max(p => p.N);
        return Math.Max(0, Math.Min(Math.Min(e - e0, e1 - e), Math.Min(n - n0, n1 - n)));
    }

    private sealed class Drive
    {
        public double E, N, H;
        public double WorstIn;      // the deepest a point of the boom got into the obstacle, m
        public double Nearest = double.MaxValue; // the nearest a point of the boom came to it, m
        public double FurthestOff;  // the furthest the pivot went from E -38 (lap 2), m
        public GpsCycleResult Last = null!;
        public double? OffAtN;
    }

    /// <summary>Drive a bicycle on the cycle's steer angle, from (<paramref name="e"/>, -60) north,
    /// until <paramref name="untilN"/> or AutoSteer goes off.</summary>
    private Drive DriveNorth(double e, double untilN)
    {
        var d = new Drive { E = e, N = -60 };
        double step = SpeedKmh / 3.6 * 0.1;
        for (int i = 0; i < 3000 && d.N < untilN; i++)
        {
            var r = Cycle(d.E, d.N, d.H);
            d.Last = r;
            for (double a = -Reach; a <= Reach; a += 0.25)
            {
                double be = d.E + Math.Sin(d.H) * Hitch + Math.Cos(d.H) * a, bn = d.N + Math.Cos(d.H) * Hitch - Math.Sin(d.H) * a;
                d.WorstIn = Math.Max(d.WorstIn, InObstacle(be, bn));
                d.Nearest = Math.Min(d.Nearest, DistanceToBox(be, bn));
            }
            d.FurthestOff = Math.Max(d.FurthestOff, Math.Abs(d.E + 38));
            if (r.AutoSteerDisengagedThisCycle) { d.OffAtN = d.N; return d; }
            double steer = r.Guidance!.SteerAngle * Math.PI / 180.0;
            d.H += step / Wheelbase * Math.Tan(steer);
            d.E += Math.Sin(d.H) * step;
            d.N += Math.Cos(d.H) * step;
        }
        return d;
    }

    [Test]
    public void The_lap_steered_goes_round_a_hard_go_around_obstacle_and_the_boom_stays_out_of_it()
    {
        // Before: the lap ran along the clump's edge, the boom's half of it over the clump, and
        // AutoSteer drove on until the implement was in it.
        Open(pass: 1, Clump);
        var d = DriveNorth(-38, untilN: 50);

        Assert.Multiple(() =>
        {
            Assert.That(d.OffAtN, Is.Null, d.Last.DisengageReason);
            Assert.That(d.WorstIn, Is.EqualTo(0), "no point of the boom in the clump");
            // The boom 4.9 m ahead keeps nearly the 1 m margin: the lap is moved for it by the
            // pull, as an open pass is. Moved one for one by the window it kept 0.56 m.
            Assert.That(d.Nearest, Is.GreaterThan(0.8), "the boom's nearest to the clump");
            Assert.That(d.FurthestOff, Is.GreaterThan(4.0), "the tractor went round it");
            Assert.That(d.E, Is.EqualTo(-38).Within(0.3), "and back onto the lap after it");
            _audio.DidNotReceive().Play(SoundEffect.UTurnTooClose);
        });
    }

    [Test]
    public void The_lap_shown_goes_round_the_obstacle_and_is_joined_at_its_seam()
    {
        Open(pass: 1, Clump);
        var shown = Cycle(-38, -60, 0).Guidance!.DisplayTrack!.Points;

        Assert.Multiple(() =>
        {
            // The line keeps half the boom and the margin (5 m) from the clump, less the rounding.
            Assert.That(shown.Min(p => DistanceToBox(p.Easting, p.Northing)), Is.GreaterThan(4.9));
            Assert.That(shown.Any(p => p.Northing > 0 && p.Northing < 10 && p.Easting < -42.5), "round its west side");
            Assert.That(shown.Any(p => p.Easting > 37 && p.Easting < 39), "the whole lap, the east side too");
            Assert.That(Math.Abs(shown[0].Easting - shown[^1].Easting) + Math.Abs(shown[0].Northing - shown[^1].Northing),
                Is.LessThan(1e-6), "the lap drawn ends where it starts");
        });

        // AutoSteer off: the lap nearest the tractor, bent the same.
        _pipeline.SetAutoSteerEngaged(false);
        var free = Cycle(-38, -60, 0).Guidance!.DisplayTrack!.Points;
        Assert.That(free.Min(p => DistanceToBox(p.Easting, p.Northing)), Is.GreaterThan(4.9), "AutoSteer off, bent too");
    }

    [Test]
    public void With_no_room_either_side_AutoSteer_goes_off_before_it()
    {
        Open(pass: 1, Wall);
        var d = DriveNorth(-38, untilN: 30);

        Assert.That(d.OffAtN, Is.Not.Null, "AutoSteer went off");
        Assert.Multiple(() =>
        {
            Assert.That(d.WorstIn, Is.EqualTo(0), "before the boom was in it");
            Assert.That(d.Last.DisengageReason, Does.Contain("Inner 1 ahead"));
            Assert.That(_results.Select(r => r.ObstacleNotice).FirstOrDefault(n => n != null),
                Is.EqualTo("No room to go around Inner 1 on this lap: steer round by hand"));
            _audio.Received().Play(SoundEffect.UTurnTooClose);
        });
    }

    [Test]
    public void A_soft_go_around_obstacle_is_gone_round_too()
    {
        Open(pass: 1, Clump, hard: false);
        var d = DriveNorth(-38, untilN: 50);
        Assert.Multiple(() =>
        {
            Assert.That(d.OffAtN, Is.Null, d.Last.DisengageReason);
            Assert.That(d.WorstIn, Is.EqualTo(0));
        });
    }

    private double DistanceToBox(double e, double n)
    {
        double e0 = _obstacle.Min(p => p.E), e1 = _obstacle.Max(p => p.E), n0 = _obstacle.Min(p => p.N), n1 = _obstacle.Max(p => p.N);
        double de = Math.Max(Math.Max(e0 - e, 0), e - e1), dn = Math.Max(Math.Max(n0 - n, 0), n - n1);
        return Math.Sqrt(de * de + dn * dn);
    }
}
