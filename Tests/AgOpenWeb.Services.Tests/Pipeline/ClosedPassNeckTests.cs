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
/// A boundary curve's lap across a neck of the field narrower than twice its offset: the offset
/// joins it along the fence there. On a tester's field (simulator, 8 m boom) AutoSteer followed
/// the second lap to 0.1 m from the fence of a bump and only the fence guard stopped it. Now
/// AutoSteer goes off 10 m plus 2 s of travel before that stretch, with the alarm, and the lap is
/// shown without it. A lap that fits through drives on. Fork only.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class ClosedPassNeckTests
{
    private const double Wheelbase = 2.5;
    private const double SpeedKmh = 8;

    private readonly Wgs84 _origin = new(43.7128, -74.006);
    private LocalPlane _plane = null!;
    private IAudioService _audio = null!;
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
        config.Vehicle.Wheelbase = Wheelbase;
        config.Tool.Width = 8;
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, 800);
        config.Tool.Overlap = 0;
        config.Guidance.UTurnRadius = 6;

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

    // A field 100 x 200 m with a notch cut in from the east between N 20 and N 40, to 17 m from
    // the west fence: two blocks joined by a 17 m neck on the west side. Clockwise, so the laps
    // are to the right: lap 1 (pass 0) 4 m in, lap 2 (pass 1) 12 m in.
    private static readonly List<Vec3> Fence = new()
    {
        new(-50, -100, 0), new(-50, 100, 0), new(50, 100, 0), new(50, 40, 0),
        new(-33, 40, 0), new(-33, 20, 0), new(50, 20, 0), new(50, -100, 0),
    };

    // The same field size with a 4 m dirt track cut in from the east between N 0 and N 4, its end
    // 2 m from the west fence: every lap from the second on crosses it (a tester's field).
    private static readonly List<Vec3> DirtTrack = new()
    {
        new(-50, -100, 0), new(-50, 100, 0), new(50, 100, 0), new(50, 4, 0),
        new(-48, 4, 0), new(-48, 0, 0), new(50, 0, 0), new(50, -100, 0),
    };

    private static Models.Track.Track BoundaryCurve(List<Vec3>? fence = null) => new()
    {
        Name = "Boundary Curve",
        Points = CurveProcessing.DensifyLoop(fence ?? Fence),
        Type = Models.Track.TrackType.Curve,
        IsClosed = true,
    };

    private void Open(int pass, bool hard = true, List<Vec3>? fence = null)
    {
        _pipeline.SetBoundary(new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = (fence ?? Fence).Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                IsHard = hard,
            },
        });
        _pipeline.SetActiveTrack(BoundaryCurve(fence), passNumber: pass, nudgeOffset: 0);
        _pipeline.SetAutoSteerEngaged(true);
    }

    private GpsCycleResult Cycle(double e, double n, double headingRad)
    {
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

    /// <summary>Drive a bicycle north on the cycle's steer angle from (<paramref name="e"/>, -40)
    /// until <paramref name="untilN"/> or AutoSteer goes off; where it went off, if it did.</summary>
    private (Vec3? OffAt, GpsCycleResult Last) DriveNorth(double e, double untilN)
    {
        double n = -40, h = 0, step = SpeedKmh / 3.6 * 0.1;
        GpsCycleResult r = null!;
        for (int i = 0; i < 2000 && n < untilN; i++)
        {
            r = Cycle(e, n, h);
            if (r.AutoSteerDisengagedThisCycle) return (new Vec3(e, n, h), r);
            double steer = r.Guidance!.SteerAngle * Math.PI / 180.0;
            h += step / Wheelbase * Math.Tan(steer);
            e += Math.Sin(h) * step;
            n += Math.Cos(h) * step;
        }
        return (null, r);
    }

    [Test]
    public void AutoSteer_goes_off_before_the_lap_runs_along_the_fence_across_the_neck()
    {
        // Lap 2, 12 m in: joined across the neck 5 m from the notch from about N 14 (where it is
        // within 8 m, half a pass, of the fence). 10 m plus 2 s at 8 km/h before that is N -0.4.
        Open(pass: 1);
        var (offAt, last) = DriveNorth(-38, untilN: 40);

        Assert.That(offAt, Is.Not.Null, "AutoSteer went off");
        Assert.Multiple(() =>
        {
            Assert.That(offAt!.Value.Northing, Is.InRange(-5.0, 4.0), "10 m plus 2 s of travel before the stretch");
            Assert.That(last.DisengageReason, Is.EqualTo(GpsPipelineService.PassDoesNotFitReason));
            _audio.Received().Play(SoundEffect.UTurnTooClose);
        });
    }

    [Test]
    public void AutoSteer_put_back_on_before_the_neck_goes_off_again()
    {
        Open(pass: 1);
        Cycle(-38, 5, 0);
        Assert.That(_results[^1].DisengageReason, Is.EqualTo(GpsPipelineService.PassDoesNotFitReason));
        _pipeline.SetAutoSteerEngaged(true);
        var again = Cycle(-38, 5.2, 0);
        Assert.That(again.DisengageReason, Is.EqualTo(GpsPipelineService.PassDoesNotFitReason));
    }

    [Test]
    public void The_lap_shown_leaves_out_the_stretch_across_the_neck()
    {
        Open(pass: 1);
        var r = Cycle(-38, -40, 0);
        Assert.That(r.AutoSteerDisengagedThisCycle, Is.False);
        var shown = r.Guidance!.DisplayTrack!.Points;
        var band = r.Guidance.ToolPass!;
        static bool InNeck(Vec3 p) => p.Easting < -30 && p.Northing > 16 && p.Northing < 44;
        Assert.Multiple(() =>
        {
            Assert.That(shown.Any(p => p.Northing < 15), "the lap round the south block");
            Assert.That(shown.Any(InNeck), Is.False, "not across the neck");
            Assert.That(shown.Any(p => p.Northing > 45), Is.False, "nor the north block's, on the other side of it");
            Assert.That(band.Any(InNeck), Is.False, "the implement's band neither");
        });

        // AutoSteer off: the lap nearest the tractor, the same.
        _pipeline.SetAutoSteerEngaged(false);
        var free = Cycle(-38, -40, 0).Guidance!.DisplayTrack!.Points;
        Assert.That(free.Any(InNeck), Is.False, "AutoSteer off, not across the neck either");
    }

    [Test]
    public void Guidelines_leave_out_the_stretch_across_the_neck()
    {
        ConfigurationStore.Instance.Display.ExtraGuidelines = true;
        ConfigurationStore.Instance.Display.ExtraGuidelinesCount = 2;
        Open(pass: 0);
        var lines = Cycle(-46, -40, 0).Guidance!.GuideLines!;
        // Lap 2 (12 m) and lap 3 (20 m) are guidelines beside lap 1: neither across the neck at its
        // offset, so neither is drawn there; lap 1's own neighbour outside the field is whole.
        Assert.Multiple(() =>
        {
            Assert.That(lines.Any(l => l.Any(p => p.Easting < -29 && p.Northing > 16 && p.Northing < 44 && p.Easting > -44)), Is.False,
                "no guideline inside the neck but lap 1's");
            Assert.That(lines.Count, Is.GreaterThan(4), "the laps cut into their pieces");
        });
    }

    // A soft fence may be crossed: the lap across a dirt track cut into the field is driven, as
    // the second lap on a tester's field; with a hard fence AutoSteer goes off before it.
    [Test]
    public void With_a_soft_fence_the_lap_across_a_dirt_track_is_driven([Values(false, true)] bool hard)
    {
        Open(pass: 1, hard: hard, fence: DirtTrack);
        var (offAt, last) = DriveNorth(-38, untilN: 30);
        if (hard)
        {
            Assert.That(offAt, Is.Not.Null, "AutoSteer went off before the track");
            Assert.That(last.DisengageReason, Is.EqualTo(GpsPipelineService.PassDoesNotFitReason));
            return;
        }
        Assert.Multiple(() =>
        {
            Assert.That(offAt, Is.Null, last.DisengageReason);
            Assert.That(Math.Abs(last.Guidance!.CrossTrackError), Is.LessThan(0.5), "on the lap, past the track");
            _audio.DidNotReceive().Play(SoundEffect.UTurnTooClose);
        });
    }

    // Across the neck the lap runs along the fence inside the field: soft or hard, that does not fit.
    [Test]
    public void With_a_soft_fence_AutoSteer_still_goes_off_before_the_lap_runs_along_the_fence()
    {
        Open(pass: 1, hard: false);
        var (offAt, last) = DriveNorth(-38, untilN: 40);
        Assert.That(offAt, Is.Not.Null, "AutoSteer went off");
        Assert.That(last.DisengageReason, Is.EqualTo(GpsPipelineService.PassDoesNotFitReason));
    }

    [Test]
    public void A_lap_that_fits_through_the_neck_drives_on()
    {
        // Lap 1, 4 m in: the neck leaves it 9 m.
        Open(pass: 0);
        var (offAt, last) = DriveNorth(-46, untilN: 60);
        Assert.Multiple(() =>
        {
            Assert.That(offAt, Is.Null, last.DisengageReason);
            _audio.DidNotReceive().Play(SoundEffect.UTurnTooClose);
        });
    }
}
