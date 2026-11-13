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
/// Passes lying wholly outside a hard outer boundary are never shown or moved onto (fork only,
/// <see cref="HardFencePasses"/>). On a tester's field the simulator drove on straight out of the
/// field after AutoSteer went off, and with AutoSteer off the lap outside the fence of the boundary
/// curve was shown, the one nearest the tractor.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class HardFencePassesTests
{
    private static BoundaryPolygon Polygon(IEnumerable<(double E, double N)> pts, bool hard = true) => new()
    {
        Points = pts.Select(p => new BoundaryPoint(p.E, p.N, 0)).ToList(),
        IsHard = hard,
    };

    // A square field 100 m across round the origin.
    private static readonly BoundaryPolygon Square = Polygon(new[] { (-50.0, -50.0), (50.0, -50.0), (50.0, 50.0), (-50.0, 50.0) });

    // A round field, its fence clockwise from the west point: the field is on the right going along it.
    private static List<Vec3> Round(double radius, int n = 180) => Enumerable.Range(0, n).Select(i =>
    {
        double a = 2 * Math.PI * i / n;
        return new Vec3(-radius * Math.Cos(a), radius * Math.Sin(a), 0);
    }).ToList();

    [Test]
    public void An_AB_line_pass_is_outside_when_the_whole_field_is_on_one_side_of_it()
    {
        // Along the west fence, northwards: + is east, into the field.
        var line = Models.Track.Track.FromABLine("AB", new Vec3(-50, -10, 0), new Vec3(-50, 10, 0));
        Assert.Multiple(() =>
        {
            Assert.That(HardFencePasses.WhollyOutside(line, 4, Square), Is.False, "inside");
            Assert.That(HardFencePasses.WhollyOutside(line, 99, Square), Is.False, "by the east fence");
            Assert.That(HardFencePasses.WhollyOutside(line, -4, Square), Is.True, "west of the fence");
            Assert.That(HardFencePasses.WhollyOutside(line, 104, Square), Is.True, "east of the field");
        });
    }

    [Test]
    public void A_lap_of_a_boundary_curve_outside_the_fence_is_outside()
    {
        var fence = Round(30);
        var lap = new Models.Track.Track
        {
            Name = "Boundary Curve", Points = CurveProcessing.DensifyLoop(fence),
            Type = Models.Track.TrackType.Curve, IsClosed = true,
        };
        var polygon = Polygon(fence.Select(p => (p.Easting, p.Northing)));
        Assert.Multiple(() =>
        {
            Assert.That(HardFencePasses.WhollyOutside(lap, 4, polygon), Is.False, "the first lap, 4 m in");
            Assert.That(HardFencePasses.WhollyOutside(lap, 20, polygon), Is.False);
            Assert.That(HardFencePasses.WhollyOutside(lap, -4, polygon), Is.True, "4 m out");
            Assert.That(HardFencePasses.WhollyOutside(lap, -12, polygon), Is.True);
        });
    }

    [Test]
    public void An_open_curve_that_runs_on_into_the_field_is_not_outside()
    {
        // A curve south of the field, heading north at its end: the pass runs on straight past it,
        // into the field.
        var curve = new Models.Track.Track
        {
            Name = "Curve",
            Points = Enumerable.Range(0, 21).Select(i => new Vec3(10 + 0.02 * (i - 20) * (i - 20), -120 + i, 0)).ToList(),
            Type = Models.Track.TrackType.Curve,
        };
        Assert.That(HardFencePasses.WhollyOutside(curve, 0, Square), Is.False);
        Assert.That(HardFencePasses.WhollyOutside(curve, -70, Square), Is.True, "70 m to the west of it, it runs past the field");
    }

    [Test]
    public void The_nearest_pass_inside_is_taken()
    {
        // Passes -3 and below are outside; 6 m passes, the reference at 0.
        Func<int, bool> outside = p => p <= -3;
        Func<int, double> offset = p => 6 * p + 3;
        Assert.Multiple(() =>
        {
            Assert.That(HardFencePasses.NearestInside(1, 9, outside, offset), Is.EqualTo(1), "inside: kept");
            Assert.That(HardFencePasses.NearestInside(-5, -27, outside, offset), Is.EqualTo(-2));
            Assert.That(HardFencePasses.NearestInside(-3, -15, p => p == -3, offset), Is.EqualTo(-4), "both as near: the one nearer the tractor");
            Assert.That(HardFencePasses.NearestInside(-3, -15, _ => true, offset), Is.EqualTo(-3), "none inside: as before");
        });
    }

    // ── Through the whole cycle ──────────────────────────────────────────

    private readonly PipelineIntents _intents = new();
    private LocalPlane _plane = null!;
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
        config.Vehicle.Wheelbase = 2.9;
        config.Tool.Width = 8;
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, 800);
        config.Tool.Overlap = 0;
        config.Guidance.UTurnRadius = 8;

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
                    new Geometry.PolygonOffsetService(), config),
                new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, config),
                NullLogger<YouTurnStateMachine>.Instance, config),
            Substitute.For<IAudioService>(),
            _intents,
            headingFusion,
            NullLogger<GpsPipelineService>.Instance,
            config,
            new PositionEstimator());

        _plane = new LocalPlane(new Wgs84(43.7128, -74.006), new SharedFieldProperties());
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

    // A round field of 30 m radius and its boundary curve: pass 0 is the first lap, 4 m in; pass -1
    // runs 4 m outside the fence.
    private void Open(bool hard, int pass)
    {
        var fence = Round(30);
        _pipeline.SetBoundary(new Boundary { OuterBoundary = Polygon(fence.Select(p => (p.Easting, p.Northing)), hard) });
        _pipeline.SetActiveTrack(new Models.Track.Track
        {
            Name = "Boundary Curve", Points = CurveProcessing.DensifyLoop(fence),
            Type = Models.Track.TrackType.Curve, IsClosed = true,
        }, passNumber: pass, nudgeOffset: 0);
    }

    private GpsCycleResult Cycle(double e, double n, double headingRad)
    {
        var w = _plane.ConvertGeoCoordToWgs84(new GeoCoord(n, e));
        _gpsService.UpdateGpsData(new GpsData
        {
            CurrentPosition = new Position
            {
                Latitude = w.Latitude, Longitude = w.Longitude,
                Heading = headingRad * 180.0 / Math.PI, Speed = 2,
            },
            FixQuality = 4,
            IsValid = true,
        });
        return _results[^1];
    }

    [Test]
    public void With_AutoSteer_off_outside_a_hard_fence_the_first_lap_is_shown([Values] bool hard)
    {
        // The tractor 12 m outside the west fence, heading north: the lap nearest it is 2 out
        // (pass -2). With a soft fence that one is shown, as before.
        Open(hard, pass: 0);
        var r = Cycle(-42, 0, 0);
        Assert.That(r.Guidance!.HowManyPathsAway, Is.EqualTo(hard ? 0 : -2));
    }

    [Test]
    public void A_lateral_move_out_across_a_hard_fence_is_refused()
    {
        // On the first lap, heading north at the west point (the field on the right): a move to the
        // left is out across the fence.
        Open(hard: true, pass: 0);
        _pipeline.SetAutoSteerEngaged(true);
        Cycle(-26, 0, 0);
        _intents.RequestGuidanceSnap(left: true);
        var refused = Cycle(-26, 0.2, 0);
        _intents.RequestGuidanceSnap(left: false);
        var moved = Cycle(-26, 0.4, 0);
        Assert.Multiple(() =>
        {
            Assert.That(refused.Guidance!.HowManyPathsAway, Is.EqualTo(0));
            Assert.That(refused.PassNotice, Is.EqualTo("Pass 1L is outside the hard boundary: the line stays on pass 1R"));
            Assert.That(moved.Guidance!.HowManyPathsAway, Is.EqualTo(1), "into the field");
            Assert.That(moved.PassNotice, Is.Null);
        });
    }
}
