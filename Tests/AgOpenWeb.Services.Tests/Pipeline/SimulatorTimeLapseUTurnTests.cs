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
using AgOpenWeb.Services.Geometry;
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
/// The simulator's 10x (fork only, official issue #726). It used to multiply the speed: at
/// 10 km/h on the slider the guidance, the U-turn and the sections worked at 100 km/h, and the
/// turns were not made. Now it is time-lapse (<see cref="SimulatorTimeLapse"/>): the speed stays
/// 10 km/h and ten seconds of driving pass in each real second. Here the real simulator drives
/// the real GPS cycle with AutoSteer and U-turns on, as the app wires them, to the headland,
/// round the U-turn and up the next pass; the time-lapse must turn as 1x does.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class SimulatorTimeLapseUTurnTests
{
    private const double ToolWidth = 6.0;
    private const double Half = 100.0;      // a 200 x 200 m field
    private const double HeadlandLine = 70.0;
    private const double SliderKph = 10.0;

    private GpsService _gps = null!;
    private GpsPipelineService _pipeline = null!;
    private GpsSimulationService _sim = null!;
    private LocalPlane _plane = null!;
    private GpsCycleResult? _last;
    private readonly List<GpsCycleResult> _cycles = new();
    private Func<long> _fixStamp = () => 0;
    private Func<bool> _isFix = () => true;

    [SetUp]
    public void SetUp()
    {
        _cycles.Clear();
        _last = null;
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.Vehicle.Wheelbase = 3;
        config.Vehicle.AntennaPivot = 0;
        config.Vehicle.AntennaOffset = 0;
        config.Vehicle.AntennaHeight = 0;
        config.Tool.Width = ToolWidth;
        config.Tool.Overlap = 0;
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, (int)(ToolWidth * 100));
        config.Guidance.UTurnRadius = 8;
        config.Guidance.UTurnDistanceFromBoundary = 2;

        _gps = new GpsService();
        _gps.Start();
        var toolPosition = new ToolPositionService(config);
        var coverage = new CoverageMapService(config);
        var sections = new SectionControlService(toolPosition, coverage, config);
        var autoSteer = new AutoSteerService(new TrackGuidanceService(),
            Substitute.For<IUdpCommunicationService>(), _gps, config);
        var headingFusion = Substitute.For<IGpsHeadingFusionService>();
        headingFusion.FuseHeading(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>(),
                Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>())
            .Returns(ci => ci.ArgAt<double>(0));

        _pipeline = new GpsPipelineService(
            _gps, toolPosition, new TrackGuidanceService(),
            sections, coverage, autoSteer,
            new YouTurnGuidanceService(),
            new YouTurnStateMachine(
                new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance,
                    new PolygonOffsetService(), config),
                new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, config),
                NullLogger<YouTurnStateMachine>.Instance, config),
            Substitute.For<IAudioService>(),
            new PipelineIntents(),
            headingFusion,
            NullLogger<GpsPipelineService>.Instance,
            config,
            new PositionEstimator());
        _plane = new LocalPlane(new Wgs84(43.7128, -74.006), new SharedFieldProperties());
        _pipeline.SetLocalPlane(_plane);
        _pipeline.Start();
        _pipeline.CycleCompleted += r => { _last = r; _cycles.Add(r); };

        // The simulator, wired as MainViewModel wires it: each fix becomes the GPS data the
        // cycle gets; a time-lapse step between two fixes only moves the tractor.
        _sim = new GpsSimulationService();
        _sim.GpsDataUpdated += (_, e) =>
        {
            if (!_isFix()) return;
            var d = e.Data;
            var local = _plane.ConvertWgs84ToGeoCoord(d.Position);
            _gps.UpdateGpsData(new GpsData
            {
                CurrentPosition = new Position
                {
                    Latitude = d.Position.Latitude, Longitude = d.Position.Longitude, Altitude = d.Altitude,
                    Easting = local.Easting, Northing = local.Northing,
                    Heading = d.HeadingDegrees, Speed = d.SpeedKmh / 3.6,
                },
                FixQuality = 4,
                SatellitesInUse = d.SatellitesTracked,
                Hdop = d.Hdop,
                HasDualHeading = true,
                ImuHeading = d.HeadingDegrees,
                ImuValid = true,
                SentenceType = GpsSentenceType.Simulator,
                MotionTimestamp = _fixStamp(),
            });
        };
    }

    [TearDown]
    public void TearDown()
    {
        _pipeline.Stop();
        _gps.Stop();
    }

    // A square, a point every metre.
    private static List<Vec3> Square(double half)
    {
        var r = new List<Vec3>();
        for (double x = -half; x < half; x++) r.Add(new(x, -half, 0));
        for (double y = -half; y < half; y++) r.Add(new(half, y, 0));
        for (double x = half; x > -half; x--) r.Add(new(x, half, 0));
        for (double y = half; y > -half; y--) r.Add(new(-half, y, 0));
        return r;
    }

    /// <summary>The field, AutoSteer on pass 0 of a north AB line, U-turns on; the simulated tractor on pass 0 heading north.</summary>
    private void Field()
    {
        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = Square(Half).Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            },
        };
        _pipeline.SetSimulatorEnabled(true);
        _pipeline.SetHasActiveField(true);
        _pipeline.SetBoundary(boundary);
        _pipeline.SetHeadlandLine(Square(HeadlandLine));
        _pipeline.SetHeadlandOn(true);
        _pipeline.SetActiveTrack(Models.Track.Track.FromABLine("AB", new Vec3(0, -Half, 0), new Vec3(0, Half, 0)), 0, 0);
        _pipeline.SetYouTurnConfig(uTurnSkipRows: 0, isSkipWorkedMode: false,
            headlandCalculatedWidth: Half - HeadlandLine, headlandDistance: Half - HeadlandLine);
        _pipeline.SetYouTurnEnabled(true);
        _pipeline.SetAutoSteerEngaged(true);

        var start = _plane.ConvertGeoCoordToWgs84(new GeoCoord(20, ToolWidth / 2));
        _sim.Initialize(new Wgs84(start.Latitude, start.Longitude));
        _sim.SetHeading(0);
    }

    /// <summary>The simulator's steer: the guidance angle while AutoSteer has a line (MainViewModel.ApplyGpsCycleResult).</summary>
    private double Steer() => _last?.Guidance is { HasGuidance: true } g ? g.SteerAngle : 0;

    private sealed record Run(bool Completed, int PassAfter, double WorstXteAfter, double SecondsDriven, string? Notice, bool Disengaged);

    /// <summary>Drives until 40 m up the next pass after the turn, or <paramref name="maxSeconds"/> of driving.</summary>
    private Run Drive(Func<double> advance, double maxSeconds)
    {
        double driven = 0;
        bool completed = false;
        double? northingAtCompletion = null;
        double worstXte = 0;
        while (driven < maxSeconds)
        {
            driven += advance();
            if (_cycles.Any(c => c.AutoSteerDisengagedThisCycle)) break;
            if (!completed && _cycles.Any(c => c.YouTurn is { JustCompleted: true }))
            {
                completed = true;
                northingAtCompletion = _last!.Northing;
            }
            if (completed && _last?.Guidance is { HasGuidance: true } g
                && northingAtCompletion - _last.Northing > 15)  // settled on the new pass (heading south)
                worstXte = Math.Max(worstXte, Math.Abs(g.CrossTrackError));
            if (completed && northingAtCompletion - _last!.Northing > 40) break;
        }
        return new Run(completed, _last?.Guidance?.HowManyPathsAway ?? 0, worstXte, driven,
            _cycles.Select(c => c.YouTurnNotice).FirstOrDefault(n => n != null),
            _cycles.Any(c => c.AutoSteerDisengagedThisCycle));
    }

    /// <summary>1x: the simulator's 33 ms step, a fix every step, at the speed shown.</summary>
    private Run DriveAt1x()
    {
        _pipeline.SynchronousMode = true;
        _sim.StepDistance = SliderKph / 40.0;
        return Drive(() => { _sim.Tick(Steer(), 1.0 / 30); return 1.0 / 30; }, 120);
    }

    /// <summary>The time-lapse as MainViewModel runs it: each timer tick drives ten times the
    /// tick's 33 ms of motion time; the GPS cycle processes each fix inline.</summary>
    private Run DriveInTimeLapse()
    {
        _pipeline.SetSimulatorTimeLapse(true);
        _sim.StepDistance = SliderKph / 40.0;
        var lapse = new SimulatorTimeLapse(_sim);
        long frequency = System.Diagnostics.Stopwatch.Frequency;
        long motion = 1_000_000_000;
        lapse.Start(motion);
        _isFix = () => !lapse.IsStepBetweenFixes;
        _fixStamp = () => lapse.FixTimestamp;
        long tick = (long)(SimulatorTimeLapse.Scale * frequency / 30);
        return Drive(() =>
        {
            motion += tick;
            int fixes = lapse.Advance(motion, frequency, Steer, () => true);
            return fixes * SimulatorTimeLapse.FixSeconds;
        }, 120);
    }

    [Test]
    public void At_1x_the_U_turn_is_made_and_lands_on_the_next_pass()
    {
        Field();
        var run = DriveAt1x();
        Assert.Multiple(() =>
        {
            Assert.That(run.Disengaged, Is.False, "AutoSteer went off: " + run.Notice);
            Assert.That(run.Completed, Is.True, "no U-turn was made: " + run.Notice);
            Assert.That(Math.Abs(run.PassAfter), Is.EqualTo(1), "the next pass");
            Assert.That(run.WorstXteAfter, Is.LessThan(0.3), "on the next pass");
        });
    }

    [Test]
    public void In_time_lapse_the_U_turn_is_made_as_at_1x()
    {
        Field();
        DriveAt1x();
        var at1x = Path();

        TearDown();
        SetUp();
        Field();
        var run = DriveInTimeLapse();
        var lapse = Path();

        Assert.Multiple(() =>
        {
            Assert.That(run.Disengaged, Is.False, "AutoSteer went off: " + run.Notice);
            Assert.That(run.Completed, Is.True, "no U-turn was made: " + run.Notice);
            Assert.That(Math.Abs(run.PassAfter), Is.EqualTo(1), "the next pass");
            Assert.That(run.WorstXteAfter, Is.LessThan(0.3), "on the next pass");
            Assert.That(lapse.Max(p => p.Northing), Is.LessThan(Half), "inside the field");
            // The same drive: every time-lapse fix lies on the 1x path (a fix every 9 cm), as
            // far down the next pass as the 1x run went (each stops 40 m past its own end of turn).
            double end1x = at1x.Where(q => q.Easting < 0).Min(q => q.Northing);
            double worst = lapse.Where(p => p.Easting >= 0 || p.Northing >= end1x).Max(p => at1x.Min(q => Math.Sqrt(
                (p.Easting - q.Easting) * (p.Easting - q.Easting) + (p.Northing - q.Northing) * (p.Northing - q.Northing))));
            Assert.That(worst, Is.LessThan(0.2), "off the 1x path");
        });
    }

    /// <summary>
    /// What the old 10x did, kept to show why it went (and for official issue #726): at 10 km/h
    /// on the slider it drove and reported 100 km/h, a fix every 0.9 m, and chased a goal point
    /// 25 m ahead. The turn was "completed" but not driven: the tractor went on north through the
    /// fence. Explicit: it measures a way of driving the app no longer has.
    /// </summary>
    [Test, Explicit]
    public void The_old_10x_speed_drove_through_the_fence()
    {
        Field();
        _pipeline.SynchronousMode = true;
        _sim.StepDistance = SliderKph * 10 / 40.0;
        Drive(() => { _sim.Tick(Steer(), 1.0 / 30); return 1.0 / 30; }, 60);
        double furthest = Path().Max(p => p.Northing);
        TestContext.Out.WriteLine($"old 10x: furthest north {furthest:F0} m (the fence is at {Half:F0} m)");
        Assert.That(furthest, Is.GreaterThan(Half));
    }

    private List<(double Easting, double Northing)> Path() => _cycles.Select(c => (c.Easting, c.Northing)).ToList();

    [Test]
    public void In_time_lapse_the_GPS_cycle_works_at_the_speed_shown()
    {
        Field();
        DriveInTimeLapse();
        // Never the old 10x's 100 km/h (the cycle's speed is in m/s).
        double fastest = _cycles.Max(c => c.Speed) * 3.6;
        Assert.That(fastest, Is.EqualTo(SliderKph).Within(0.1));
    }
}
