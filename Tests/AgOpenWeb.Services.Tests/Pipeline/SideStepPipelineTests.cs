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
/// A lateral move onto the pass beside a hard fence with a front boom, through the whole cycle
/// (fork only). On a tester's field (simulator) the move onto the lap 4 m from the fence, with an
/// 8 m boom 4.5 m ahead of the pivot, took the boom's end across the fence until the fence guard
/// stopped AutoSteer. Now AutoSteer drives an S that keeps the boom inside, or goes off at once
/// when none fits.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class SideStepPipelineTests
{
    private const double Wheelbase = 2.5;
    private const double SpeedKmh = 8;
    private const double Hitch = 4.5;
    private const double Reach = 4;

    private readonly Wgs84 _origin = new(43.7128, -74.006);
    private LocalPlane _plane = null!;
    private IAudioService _audio = null!;
    private GpsService _gpsService = null!;
    private PipelineIntents _intents = null!;
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
        config.Tool.IsToolFrontFixed = true;
        config.Tool.IsToolRearFixed = false;
        config.Tool.IsToolTrailing = false;
        config.Tool.IsToolTBT = false;
        // A fixed tool's working point Hitch m from the rear axle: the tractor's hitch there, the tool on it (#547).
        config.Vehicle.ThreePointHitchLength = config.Vehicle.FrontHitchLength = System.Math.Abs(Hitch);
        config.Tool.WorkingPointFromHitch = 0;
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

    private static List<Vec3> Fence(double northEnd) => new()
    {
        new(-50, -200, 0), new(-50, northEnd, 0), new(50, northEnd, 0), new(50, -200, 0),
    };

    // An AB line on the west fence: pass 0 runs 4 m in, the boom's west end on the fence; pass 1
    // is 8 m further in.
    private void Open(int pass, double northEnd = 200)
    {
        _pipeline.SetBoundary(new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = Fence(northEnd).Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                IsHard = true,
            },
        });
        _pipeline.SetActiveTrack(new Models.Track.Track
        {
            Name = "Along the fence",
            Points = new List<Vec3> { new(-50, -150, 0), new(-50, 150, 0) },
            Type = Models.Track.TrackType.ABLine,
        }, passNumber: pass, nudgeOffset: 0);
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

    private sealed class Drive
    {
        public double E, N, H;
        public double WorstOut; // the boom's west end west of the fence, m
        public bool SawS;
        public GpsCycleResult Last = null!;
        public double? OffAtN;
    }

    /// <summary>Drive a bicycle north on the cycle's steer angle, <paramref name="cycles"/> tenths of a
    /// second, until AutoSteer goes off; <paramref name="before"/> runs ahead of the first cycle.</summary>
    private Drive DriveNorth(double e, double n, int cycles, Action<int>? before = null)
    {
        var d = new Drive { E = e, N = n };
        double step = SpeedKmh / 3.6 * 0.1;
        for (int i = 0; i < cycles; i++)
        {
            before?.Invoke(i);
            var r = Cycle(d.E, d.N, d.H);
            d.Last = r;
            d.SawS |= r.LineEntryPath is { Count: > 0 };
            // The front boom's west end: 4.5 m ahead of the pivot, 4 m to its left.
            double westEnd = d.E + Math.Sin(d.H) * Hitch - Math.Cos(d.H) * Reach;
            d.WorstOut = Math.Max(d.WorstOut, -50 - westEnd);
            if (r.AutoSteerDisengagedThisCycle) { d.OffAtN = d.N; return d; }
            double steer = r.Guidance!.SteerAngle * Math.PI / 180.0;
            d.H += step / Wheelbase * Math.Tan(steer);
            d.E += Math.Sin(d.H) * step;
            d.N += Math.Cos(d.H) * step;
        }
        return d;
    }

    [Test]
    public void A_lateral_move_onto_the_lap_by_the_fence_keeps_the_front_boom_inside()
    {
        Open(pass: 1);
        // Settle on pass 1 (E -38), then move one pass left onto the lap by the fence.
        var d = DriveNorth(-38, -120, 900, i => { if (i == 50) _intents.RequestGuidanceSnap(left: true); });

        Assert.Multiple(() =>
        {
            Assert.That(d.OffAtN, Is.Null, d.Last.DisengageReason);
            Assert.That(d.SawS, "the S is shown");
            Assert.That(d.WorstOut, Is.LessThan(0.2), "the boom's end keeps inside the fence");
            Assert.That(d.E, Is.EqualTo(-46).Within(0.2), "on the lap by the fence");
            _audio.DidNotReceive().Play(SoundEffect.UTurnTooClose);
        });
    }

    [Test]
    public void No_room_for_the_S_takes_AutoSteer_off_at_once()
    {
        // The north fence 30 m past the move: no S fits before it.
        Open(pass: 1, northEnd: -45);
        var d = DriveNorth(-38, -120, 300, i => { if (i == 50) _intents.RequestGuidanceSnap(left: true); });

        Assert.Multiple(() =>
        {
            Assert.That(d.OffAtN, Is.Not.Null.And.LessThan(-75), "off at the move");
            Assert.That(d.Last.DisengageReason, Is.EqualTo(GpsPipelineService.SideStepNoRoomReason));
            Assert.That(d.WorstOut, Is.LessThan(0.01));
            _audio.Received().Play(SoundEffect.UTurnTooClose);
        });
    }

    [Test]
    public void A_lateral_move_away_from_the_fence_is_steered_as_before()
    {
        // From pass 2 (E -30) to pass 3 (E -22): the boom stays 20 m and more from the fence.
        Open(pass: 2);
        var d = DriveNorth(-30, -120, 600, i => { if (i == 50) _intents.RequestGuidanceSnap(left: false); });
        Assert.Multiple(() =>
        {
            Assert.That(d.OffAtN, Is.Null, d.Last.DisengageReason);
            Assert.That(d.SawS, Is.False, "no S");
            Assert.That(d.E, Is.EqualTo(-22).Within(0.2));
        });
    }
}
