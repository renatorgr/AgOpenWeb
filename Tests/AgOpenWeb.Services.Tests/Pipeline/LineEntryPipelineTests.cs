// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
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
/// AutoSteer joining a new line with the tractor in the headland (U-turn settings › Joining a new
/// line in the headland; fork only), through the real GPS cycle: Start of the pass drives the way
/// through the headland and puts the tractor on the pass before the crop, Ask offers it with the
/// approach sound and two buttons, Nearest point (the default) does what it always did. The field
/// is 200 × 300 m with a 20 m headland; the tractor starts in the south headland heading east,
/// the pass 40 m east of it runs north.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class LineEntryPipelineTests
{
    private const double Wheelbase = 2.5;
    private const double SpeedKmh = 8;
    private const double PassE = 40;

    /// <summary>Where pass 0 (1R) runs, as the pass model puts it beside the AB line at
    /// <see cref="PassE"/>.</summary>
    private double _passLineE;

    private readonly Wgs84 _origin = new(43.7128, -74.006);
    private LocalPlane _plane = null!;
    private PipelineIntents _intents = null!;
    private IAudioService _audio = null!;
    private GpsService _gpsService = null!;
    private GpsPipelineService _pipeline = null!;
    private List<GpsCycleResult> _results = null!;
    private ConfigurationStore _config = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _config = ConfigurationStore.Instance;
        _config.Vehicle.AntennaPivot = 0;
        _config.Vehicle.AntennaOffset = 0;
        _config.Vehicle.AntennaHeight = 0;
        _config.Vehicle.Wheelbase = Wheelbase;
        _config.Tool.Width = 6;
        _config.NumSections = 1;
        _config.Tool.SetSectionWidth(0, 600);
        _config.Tool.Overlap = 0;
        _config.Guidance.UTurnRadius = 6;
        _config.Guidance.UTurnDistanceFromBoundary = 1;

        _gpsService = new GpsService();
        _gpsService.Start();
        var toolPosition = new ToolPositionService(_config);
        var coverage = new CoverageMapService(_config);
        var sectionControl = new SectionControlService(toolPosition, coverage, _config);
        var autoSteer = new AutoSteerService(new TrackGuidanceService(),
            Substitute.For<IUdpCommunicationService>(), _gpsService, _config);
        var headingFusion = Substitute.For<IGpsHeadingFusionService>();
        headingFusion.FuseHeading(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>(),
                Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>())
            .Returns(ci => ci.ArgAt<double>(0));
        _intents = new PipelineIntents();
        _audio = Substitute.For<IAudioService>();
        _pipeline = new GpsPipelineService(
            _gpsService, toolPosition, new TrackGuidanceService(),
            sectionControl, coverage, autoSteer,
            new YouTurnGuidanceService(),
            new YouTurnStateMachine(
                new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance,
                    new Geometry.PolygonOffsetService(), _config),
                new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, _config),
                NullLogger<YouTurnStateMachine>.Instance, _config),
            _audio,
            _intents,
            headingFusion,
            NullLogger<GpsPipelineService>.Instance,
            _config,
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

    /// <summary>The field, the headland line <paramref name="headland"/> m in, and the track at
    /// <see cref="PassE"/> running north; AutoSteer engaged.</summary>
    private void OpenField(LineEntryMode mode, double headland = 20, bool hard = false)
    {
        _config.Guidance.LineEntryMode = mode;
        var outer = new BoundaryPolygon
        {
            Points = new List<BoundaryPoint> { new(-100, -10, 0), new(100, -10, 0), new(100, 290, 0), new(-100, 290, 0) },
            IsHard = hard,
        };
        _pipeline.SetBoundary(new Boundary { OuterBoundary = outer });
        _pipeline.SetHeadlandLine(new List<Vec3>
        {
            new(-100 + headland, -10 + headland, 0), new(100 - headland, -10 + headland, 0),
            new(100 - headland, 290 - headland, 0), new(-100 + headland, 290 - headland, 0),
        });
        var track = Models.Track.Track.FromABLine("AB", new Vec3(PassE, -10, 0), new Vec3(PassE, 290, 0));
        _passLineE = PassE + Models.Track.PassGeometry.Offset(track, 0, 6);
        _pipeline.SetActiveTrack(track, passNumber: 0, nudgeOffset: 0);
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

    /// <summary>Drive a bicycle on the cycle's steer angle from (0, 0) until the pivot is
    /// <paramref name="untilN"/> north (or has left the field); the pivot's track.</summary>
    private List<Vec3> Drive(double headingDeg, double untilN = 40)
    {
        double e = 0, n = 0, h = headingDeg * Math.PI / 180.0, step = SpeedKmh / 3.6 * 0.1;
        var track = new List<Vec3>();
        for (int i = 0; i < 1000 && n < untilN && n > -15; i++)
        {
            var r = Cycle(e, n, h);
            track.Add(new Vec3(e, n, h));
            double steer = r.Guidance!.SteerAngle * Math.PI / 180.0;
            h += step / Wheelbase * Math.Tan(steer);
            e += Math.Sin(h) * step;
            n += Math.Cos(h) * step;
        }
        return track;
    }

    private static bool InCrop(Vec3 p) => Math.Abs(p.Easting) < 80 && p.Northing > 10 && p.Northing < 270;

    [Test]
    public void Start_of_the_pass_drives_through_the_headland_onto_the_pass()
    {
        OpenField(LineEntryMode.PassStart);
        var first = Cycle(0, 0, 70 * Math.PI / 180);
        Assert.That(first.LineEntryPath, Is.Not.Null, "the way is planned when AutoSteer is engaged");
        Assert.That(first.LineEntryOfferPass, Is.Null, "driven, not offered");
        Assert.That(first.LineEntryPath![^1].Easting, Is.EqualTo(_passLineE).Within(0.05));

        var track = Drive(70);

        var inCrop = track.Where(InCrop).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(inCrop, Is.Not.Empty);
            Assert.That(inCrop.Max(p => Math.Abs(p.Easting - _passLineE)), Is.LessThan(0.5),
                "in the crop only on the pass");
            var atLine = inCrop[0];
            Assert.That(Math.Abs(atLine.Heading) * 180 / Math.PI, Is.LessThan(8).Or.GreaterThan(352),
                "heading along the pass at the headland line");
            Assert.That(track.Min(p => p.Northing), Is.GreaterThan(-10), "inside the field");
            Assert.That(_results[^1].LineEntryPath, Is.Null, "the line steers once on the pass");
        });
    }

    [Test]
    public void Nearest_point_joins_the_line_as_before()
    {
        OpenField(LineEntryMode.Nearest);
        var first = Cycle(0, 0, 70 * Math.PI / 180);
        Assert.That(first.LineEntryPath, Is.Null);

        var track = Drive(70);

        Assert.That(track.Where(InCrop).Max(p => Math.Abs(p.Easting - _passLineE)), Is.GreaterThan(0.5),
            "the default joins at the nearest point, into the crop off the pass (why the option exists)");
        _audio.DidNotReceive().Play(SoundEffect.YouTurnApproach);
    }

    [Test]
    public void Ask_offers_the_way_and_the_operator_takes_it()
    {
        OpenField(LineEntryMode.Ask);
        var first = Cycle(0, 0, Math.PI / 2);
        Assert.Multiple(() =>
        {
            Assert.That(first.LineEntryOfferPass, Is.EqualTo("1R"), "offered for pass 1R");
            Assert.That(first.LineEntryPath, Is.Not.Null, "the way offered is drawn");
        });
        _audio.Received(1).Play(SoundEffect.YouTurnApproach);

        _intents.RequestLineEntryAnswer(take: true);
        var taken = Cycle(0.2, 0, Math.PI / 2);
        Assert.Multiple(() =>
        {
            Assert.That(taken.LineEntryOfferPass, Is.Null, "answered");
            Assert.That(taken.LineEntryPath, Is.Not.Null, "and driven");
        });
    }

    [Test]
    public void Ask_keeps_the_nearest_point_when_the_operator_says_so()
    {
        OpenField(LineEntryMode.Ask);
        Cycle(0, 0, Math.PI / 2);
        _intents.RequestLineEntryAnswer(take: false);
        var kept = Cycle(0.2, 0, Math.PI / 2);
        Assert.Multiple(() =>
        {
            Assert.That(kept.LineEntryOfferPass, Is.Null);
            Assert.That(kept.LineEntryPath, Is.Null);
        });
    }

    [Test]
    public void No_room_says_so_once_and_joins_at_the_nearest_point()
    {
        OpenField(LineEntryMode.PassStart, headland: 4);
        var first = Cycle(0, -8, Math.PI / 2);
        var second = Cycle(0.2, -8, Math.PI / 2);
        Assert.Multiple(() =>
        {
            Assert.That(first.LineEntryPath, Is.Null);
            Assert.That(first.LineEntryNotice, Does.Contain("No way through the headland to the start of pass 1R"));
            Assert.That(second.LineEntryNotice, Is.Null, "said once");
        });
    }

    [Test]
    public void Moving_the_line_a_pass_plans_the_way_again()
    {
        OpenField(LineEntryMode.PassStart);
        var first = Cycle(0, 0, Math.PI / 2);
        Assert.That(first.LineEntryPath![^1].Easting, Is.EqualTo(_passLineE).Within(0.05));

        _intents.RequestGuidanceSnap(left: false);
        var moved = Cycle(0.2, 0, Math.PI / 2);
        Assert.That(moved.LineEntryPath, Is.Not.Null);
        Assert.That(moved.LineEntryPath![^1].Easting, Is.EqualTo(_passLineE + 6).Within(0.05), "to the start of the next pass");
    }

    [Test]
    public void A_manual_U_turn_drops_the_way()
    {
        OpenField(LineEntryMode.PassStart);
        Assert.That(Cycle(0, 0, Math.PI / 2).LineEntryPath, Is.Not.Null);
        _intents.RequestManualYouTurn(turnLeft: true);
        Assert.That(Cycle(0.2, 0, Math.PI / 2).LineEntryPath, Is.Null);
    }

    [Test]
    public void AutoSteer_off_drops_the_way()
    {
        OpenField(LineEntryMode.PassStart);
        Assert.That(Cycle(0, 0, Math.PI / 2).LineEntryPath, Is.Not.Null);
        _pipeline.SetAutoSteerEngaged(false);
        Assert.That(Cycle(0.2, 0, Math.PI / 2).LineEntryPath, Is.Null);
    }

    // ── Facing out of the field (Tapada da Casa: AutoSteer engaged in the headland facing the
    // fence took the line out to it) ──

    private const string FaceTheField = "AutoSteer off: the line ahead runs out of the field. Turn the tractor to face the field.";

    [Test]
    public void Engaged_facing_a_hard_fence_AutoSteer_goes_off_and_says_to_face_the_field()
    {
        OpenField(LineEntryMode.Nearest, hard: true);
        var first = Cycle(PassE - 6, 0, Math.PI); // in the south headland, facing south

        Assert.Multiple(() =>
        {
            Assert.That(first.AutoSteerDisengagedThisCycle, Is.True);
            Assert.That(first.DisengageReason, Is.EqualTo(FaceTheField));
        });
        _audio.Received(1).Play(SoundEffect.UTurnTooClose);
    }

    [Test]
    public void Engaged_facing_the_field_AutoSteer_stays_on()
    {
        OpenField(LineEntryMode.Nearest, hard: true);
        var first = Cycle(PassE - 6, 0, 30 * Math.PI / 180);

        Assert.That(first.AutoSteerDisengagedThisCycle, Is.False);
        _audio.DidNotReceive().Play(SoundEffect.UTurnTooClose);
    }

    [Test]
    public void Facing_a_soft_boundary_AutoSteer_stays_on()
    {
        OpenField(LineEntryMode.Nearest, hard: false);
        var first = Cycle(PassE - 6, 0, Math.PI);

        Assert.That(first.AutoSteerDisengagedThisCycle, Is.False);
    }

    [Test]
    public void Start_of_the_pass_facing_out_turns_round_through_the_headland_when_it_fits()
    {
        // 15 m from the fence, facing it: room for a loop at radius 6 in the 20 m headland.
        OpenField(LineEntryMode.PassStart, hard: true);
        var first = Cycle(0, 5, Math.PI);
        Assert.Multiple(() =>
        {
            Assert.That(first.AutoSteerDisengagedThisCycle, Is.False, first.DisengageReason);
            Assert.That(first.LineEntryPath, Is.Not.Null, "the way round to the start of the pass");
            Assert.That(first.LineEntryPath![^1].Easting, Is.EqualTo(_passLineE).Within(0.05));
        });

        var track = DriveFrom(0, 5, 180, untilN: 40);

        var inCrop = track.Where(InCrop).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(inCrop, Is.Not.Empty, "into the field");
            Assert.That(inCrop.Max(p => Math.Abs(p.Easting - _passLineE)), Is.LessThan(0.5), "in the crop only on the pass");
            Assert.That(track.Min(p => p.Northing), Is.GreaterThan(-9), "the fence not reached");
        });
    }

    [Test]
    public void Start_of_the_pass_facing_out_with_no_room_to_turn_goes_off_and_says_to_face_the_field()
    {
        // 3 m from a hard fence, facing it: no way round.
        OpenField(LineEntryMode.PassStart, hard: true);
        var first = Cycle(0, -7, Math.PI);

        Assert.Multiple(() =>
        {
            Assert.That(first.LineEntryPath, Is.Null);
            Assert.That(first.DisengageReason, Is.EqualTo(FaceTheField));
            Assert.That(first.LineEntryNotice, Is.Null, "not \"joining it at the nearest point\"");
        });
    }

    /// <summary><see cref="Drive"/> from (<paramref name="e0"/>, <paramref name="n0"/>).</summary>
    private List<Vec3> DriveFrom(double e0, double n0, double headingDeg, double untilN)
    {
        double e = e0, n = n0, h = headingDeg * Math.PI / 180.0, step = SpeedKmh / 3.6 * 0.1;
        var track = new List<Vec3>();
        for (int i = 0; i < 2000 && n < untilN && n > -15; i++)
        {
            var r = Cycle(e, n, h);
            track.Add(new Vec3(e, n, h));
            double steer = r.Guidance!.SteerAngle * Math.PI / 180.0;
            h += step / Wheelbase * Math.Tan(steer);
            e += Math.Sin(h) * step;
            n += Math.Cos(h) * step;
        }
        return track;
    }
}
