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
/// <para>AutoSteer engaged inside the turn line on a pass that goes by beside the obstacle, the
/// implement reaching into it. No turn is before it there and nothing is ahead of the tractor: the
/// beta robot (Quinta Torta, simulator) had a 6 m implement driven 50 m with its end 2 m inside a
/// hard obstacle a metre beside the pass.</para>
/// All three now take AutoSteer off with the alarm before the implement is in the obstacle.
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

    private void Open(int pass, bool hard = true, (double E, double N)[]? obstacle = null, ObstacleAction at = ObstacleAction.Turn)
    {
        _obstacle = obstacle ?? Square;
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
            Points = _obstacle.Select(p => new BoundaryPoint(p.E, p.N, 0)).ToList(),
            IsHard = hard,
            AtObstacle = at,
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

    private GpsCycleResult Cycle(double e, double n, double headingRad, double speedKmh = SpeedKmh)
    {
        // The control loop keeps the tool's position; here the test does.
        _toolPosition.Update(new Vec3(e, n, headingRad), headingRad);
        var w = _plane.ConvertGeoCoordToWgs84(new GeoCoord(n, e));
        _gpsService.UpdateGpsData(new GpsData
        {
            CurrentPosition = new Position
            {
                Latitude = w.Latitude, Longitude = w.Longitude,
                Heading = headingRad * 180.0 / Math.PI, Speed = speedKmh / 3.6, // m/s
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

    private (double E, double N)[] _obstacle = Square;

    // How far inside the obstacle (a box square to the passes) a point is, 0 outside.
    private double InSquare(double e, double n)
    {
        double e0 = _obstacle.Min(p => p.E), e1 = _obstacle.Max(p => p.E), n0 = _obstacle.Min(p => p.N), n1 = _obstacle.Max(p => p.N);
        double d = Math.Min(Math.Min(e - e0, e1 - e), Math.Min(n - n0, n1 - n));
        return Math.Max(0, d);
    }

    /// <summary>Drive a bicycle north on the cycle's steer angle, <paramref name="cycles"/> tenths of a
    /// second, until AutoSteer goes off; <paramref name="before"/> runs ahead of each cycle.</summary>
    private Drive DriveNorth(double e, double n, int cycles, Action<int>? before = null, double speedKmh = SpeedKmh)
    {
        var d = new Drive { E = e, N = n };
        double step = speedKmh / 3.6 * 0.1;
        for (int i = 0; i < cycles; i++)
        {
            before?.Invoke(i);
            var r = Cycle(d.E, d.N, d.H, speedKmh);
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

    // A hard strip 40 m long beside pass 3 (E -8), its east edge `gap` m left of the pass: a wall or a
    // ditch along the passes. Its turn line is 8 m out (the U-turn distance and half the boom), so
    // pass 3 runs inside it; the boom reaches 6 m.
    private static (double E, double N)[] StripBesidePass3(double gap)
        => new[] { (-8 - gap - 5, -60.0), (-8 - gap, -60.0), (-8 - gap, -20.0), (-8 - gap - 5, -20.0) };

    [Test]
    public void EngagedInsideTheTurnLineOnAPassBesideTheObstacle_AutoSteerGoesOffBeforeTheBoomIsInIt()
    {
        // The pivot 6 m short of the strip and 4 m beside its edge line: inside the turn line, so no
        // turn is before it, and nothing is straight ahead of the tractor. The boom's end would run
        // 2 m inside the strip for all its 40 m.
        Open(pass: 3, obstacle: StripBesidePass3(gap: 4));
        var d = DriveNorth(-8, -66, 400);

        Assert.Multiple(() =>
        {
            Assert.That(d.OffAtN, Is.Not.Null, "AutoSteer went off");
            Assert.That(d.Last.DisengageReason, Is.EqualTo(GpsPipelineService.HardObstacleReason(0)));
            Assert.That(d.WorstIn, Is.EqualTo(0), "no point of the boom in the obstacle (2 m in for 40 m before)");
        });
        _audio.Received().Play(SoundEffect.UTurnTooClose);
    }

    [Test]
    public void EngagedWithTheBoomAlreadyInTheObstacle_AutoSteerGoesOffAtOnce()
    {
        // Half way along the strip, the boom's end 2 m in it and going along it, no deeper: nothing
        // is heading in, and AutoSteer stayed on to the strip's end.
        Open(pass: 3, obstacle: StripBesidePass3(gap: 4));
        var d = DriveNorth(-8, -45, 400);

        Assert.Multiple(() =>
        {
            Assert.That(d.OffAtN, Is.Not.Null.And.LessThan(-44), "AutoSteer went off within a metre");
            Assert.That(d.Last.DisengageReason, Is.EqualTo(GpsPipelineService.HardObstacleInReason(0)));
        });
        _audio.Received().Play(SoundEffect.UTurnTooClose);
    }

    [Test]
    public void EngagedInsideTheTurnLineOnAPassThatClearsTheObstacle_AutoSteerStaysOn()
    {
        // The strip's edge 7 m from the pass, the boom's end a metre clear of it; engaged 2 m short
        // of the strip, 7.3 m from its corner: inside the turn line (8 m).
        Open(pass: 3, obstacle: StripBesidePass3(gap: 7));
        var d = DriveNorth(-8, -62, 400);

        Assert.Multiple(() =>
        {
            Assert.That(d.OffAtN, Is.Null, d.Last.DisengageReason);
            Assert.That(d.WorstIn, Is.EqualTo(0));
            Assert.That(d.N, Is.GreaterThan(-20), "driven past the strip");
        });
    }

    [Test]
    public void ASoftObstacleBesideThePass_IsLeftAlone()
    {
        // Not hard: the implement may pass over it, as before.
        Open(pass: 3, hard: false, obstacle: StripBesidePass3(gap: 4));
        var d = DriveNorth(-8, -66, 400);

        Assert.Multiple(() =>
        {
            Assert.That(d.OffAtN, Is.Null, d.Last.DisengageReason);
            Assert.That(d.WorstIn, Is.GreaterThan(1.5), "the boom was over it with AutoSteer on");
        });
    }

    [Test]
    public void OnTheObstaclesOwnPass_TheTurnBeforeItIsNotStoppedByTheGuard()
    {
        // From far off on the obstacle's pass: the U-turn before it is planned and driven, and the
        // guard's look-ahead stops where that turn starts.
        Open(pass: 2);
        var d = DriveNorth(-20, StartN, 400);

        Assert.Multiple(() =>
        {
            Assert.That(_results.Any(r => r.YouTurn is { IsExecuting: true }), Is.True, "the turn before the obstacle was driven");
            Assert.That(_results.Select(r => r.DisengageReason), Has.None.EqualTo(GpsPipelineService.HardObstacleReason(0))
                .And.None.EqualTo(GpsPipelineService.HardObstacleInReason(0)));
            Assert.That(d.WorstIn, Is.EqualTo(0));
        });
    }

    // A hard obstacle chosen as Go around, 9 m right of pass 3 (E -8): too far for the pass to bend
    // round it (half the boom and the margin are 7 m), so on the pass the boom's end goes by 3 m
    // clear of it. Off the pass nothing kept the boom out of it but the look-ahead.
    private static (double E, double N)[] GoAroundRightOfPass3(double south, double north)
        => new[] { (1.0, south), (5.0, south), (5.0, north), (1.0, north) };

    [Test]
    public void EngagedAtACrawlOffTheLineBesideAHardObstacle_AutoSteerGoesOffBeforeTheBoomIsInIt()
    {
        // 4.5 m right of the pass, pulling away at 1 km/h, the boom 0.6 m short of the obstacle and its
        // right end 1.5 m inside the obstacle's width: AutoSteer steers left for the line and the right
        // end swings forward into it. Two seconds of travel at 1 km/h are 0.56 m, less than the least
        // the look-ahead looked at, so nothing was looked at (the beta robot on Casa, simulator: put
        // by hand beside a hard Go around obstacle, 4.5 m off the line, engaged at 0.28 m/s, a 12 m
        // implement 1.2 m in it with AutoSteer on for 7 s).
        Open(pass: 3, obstacle: GoAroundRightOfPass3(south: -64.9, north: -60.9), at: ObstacleAction.GoRound);
        var d = DriveNorth(-3.5, -70, 300, speedKmh: 1);

        Assert.Multiple(() =>
        {
            Assert.That(d.OffAtN, Is.Not.Null, "AutoSteer went off");
            Assert.That(d.Last.DisengageReason, Is.EqualTo(GpsPipelineService.HardObstacleReason(0)));
            Assert.That(d.WorstIn, Is.EqualTo(0), "no point of the boom in the obstacle");
        });
        _audio.Received().Play(SoundEffect.UTurnTooClose);
    }

    [Test]
    public void EngagedOffTheLineWithTheBoomAlreadyInAHardGoAroundObstacle_AutoSteerGoesOffAtOnce()
    {
        // 4.5 m right of the pass beside a hard strip, the boom's right end 1.5 m in it. AutoSteer
        // steers left, out of it: nothing is heading in, and only an obstacle the pass turns at was
        // asked whether the boom is in it. Off the line no bend keeps the boom out of this one either.
        Open(pass: 3, obstacle: GoAroundRightOfPass3(south: -80, north: -20), at: ObstacleAction.GoRound);
        var d = DriveNorth(-3.5, -50, 100);

        Assert.Multiple(() =>
        {
            Assert.That(d.OffAtN, Is.Not.Null.And.LessThan(-49), "AutoSteer went off within a metre");
            Assert.That(d.Last.DisengageReason, Is.EqualTo(GpsPipelineService.HardObstacleInReason(0)));
        });
        _audio.Received().Play(SoundEffect.UTurnTooClose);
    }

    [Test]
    public void StandingStillOffTheLineBesideAHardObstacle_NothingIsLookedAt()
    {
        // Not moving, the boom 0.6 m short of the obstacle: a parked tractor is not heading anywhere.
        Open(pass: 3, obstacle: GoAroundRightOfPass3(south: -64.9, north: -60.9), at: ObstacleAction.GoRound);
        var d = DriveNorth(-3.5, -70, 30, speedKmh: 0);

        Assert.That(d.OffAtN, Is.Null, d.Last.DisengageReason);
    }

    [Test]
    public void OnThePassBesideAHardGoAroundObstacleItClears_AutoSteerStaysOn()
    {
        // On pass 3 the boom's right end goes by 3 m from the strip, at a crawl and at speed.
        Open(pass: 3, obstacle: GoAroundRightOfPass3(south: -80, north: -20), at: ObstacleAction.GoRound);
        var slow = DriveNorth(-8, -90, 100, speedKmh: 1);
        var fast = DriveNorth(-8, -90, 400);

        Assert.Multiple(() =>
        {
            Assert.That(slow.OffAtN, Is.Null, slow.Last.DisengageReason);
            Assert.That(fast.OffAtN, Is.Null, fast.Last.DisengageReason);
            Assert.That(fast.WorstIn, Is.EqualTo(0));
            Assert.That(fast.N, Is.GreaterThan(-20), "driven past the strip");
        });
    }

    // A hard strip chosen as Go around, 6.6 m right of pass 3 (E -8), with a safety margin of 0.5 m:
    // the pass is not bent (half the boom and the margin are 6.5 m) and on it the boom's right end
    // goes by 0.6 m from the strip.
    private static (double E, double N)[] GoAroundCloseRightOfPass3(double south, double north)
        => new[] { (-1.4, south), (3.0, south), (3.0, north), (-1.4, north) };

    [Test]
    public void EngagedNearTheLineWithTheBoomInAHardGoAroundObstacle_AutoSteerGoesOffAtOnce()
    {
        // 1.3 m right of the pass, under the 1.5 m that count as off the line, the boom's right end
        // 0.7 m in the strip. Near the line a Go around obstacle was not looked at at all: the bend
        // keeps the boom the margin out of it, and a tractor more than the margin off the pass had
        // spent that (the beta robot on Olival, simulator: engaged 0.45 m off the pass at 1 km/h,
        // heading away from it, 1.3 m off before it came back, a 12 m implement 0.4 m in a hard Go
        // around obstacle with AutoSteer on).
        ConfigurationStore.Instance.Guidance.GoAroundMargin = 0.5;
        Open(pass: 3, obstacle: GoAroundCloseRightOfPass3(south: -80, north: -20), at: ObstacleAction.GoRound);
        var d = DriveNorth(-6.7, -50, 100);

        Assert.Multiple(() =>
        {
            Assert.That(d.OffAtN, Is.Not.Null.And.LessThan(-49), "AutoSteer went off within a metre");
            Assert.That(d.Last.DisengageReason, Is.EqualTo(GpsPipelineService.HardObstacleInReason(0)));
        });
        _audio.Received().Play(SoundEffect.UTurnTooClose);
    }

    [Test]
    public void NearTheLineWithTheBoomShortOfAHardGoAroundObstacle_AutoSteerStaysOn()
    {
        // 0.3 m right of the pass the boom's right end is 0.3 m short of the strip: AutoSteer steers
        // back to the line and nothing is in the obstacle.
        ConfigurationStore.Instance.Guidance.GoAroundMargin = 0.5;
        Open(pass: 3, obstacle: GoAroundCloseRightOfPass3(south: -80, north: -20), at: ObstacleAction.GoRound);
        var d = DriveNorth(-7.7, -50, 300);

        Assert.Multiple(() =>
        {
            Assert.That(d.OffAtN, Is.Null, d.Last.DisengageReason);
            Assert.That(d.WorstIn, Is.EqualTo(0), "no point of the boom in the strip");
            Assert.That(d.N, Is.GreaterThan(-20), "driven past the strip");
        });
    }

    [Test]
    public void OnThePassBentRoundAHardGoAroundObstacle_AutoSteerStaysOn()
    {
        // A hard box 5 m right of pass 3: the pass bends 2 m left round it, and the boom follows the
        // bend the margin clear of the box, at a crawl and at speed.
        var box = new[] { (-3.0, -62.0), (1.0, -62.0), (1.0, -58.0), (-3.0, -58.0) };
        Open(pass: 3, obstacle: box, at: ObstacleAction.GoRound);
        var fast = DriveNorth(-8, StartN, 600);

        Assert.Multiple(() =>
        {
            Assert.That(fast.OffAtN, Is.Null, fast.Last.DisengageReason);
            Assert.That(fast.WorstIn, Is.EqualTo(0), "no point of the boom in the box");
            Assert.That(fast.N, Is.GreaterThan(-40), "driven past the box");
        });
    }
}
