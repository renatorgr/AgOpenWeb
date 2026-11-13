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
/// The fence guard (<see cref="HardFenceGuard"/>) on the first lap of a boundary curve, through the
/// whole cycle (fork only). The lap runs half the implement in from a hard fence, so an 8 m front
/// boom's end runs on it. The guard looked 2 s straight ahead, which leaves a bend: on a tester's
/// field (simulator, 8 km/h) it took AutoSteer off at a bend with the tractor 4 cm off the line, and
/// the tractor went on straight out across the fence.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class HardFenceGuardPipelineTests
{
    private const double Wheelbase = 2.9;
    private const double SpeedKmh = 8;
    private const double Hitch = 4.5;
    private const double Reach = 4;

    private readonly Wgs84 _origin = new(43.7128, -74.006);
    private LocalPlane _plane = null!;
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
        config.Tool.Width = 8;
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, 800);
        config.Tool.Overlap = 0;
        config.Tool.IsToolFrontFixed = true;
        config.Tool.IsToolRearFixed = false;
        config.Tool.IsToolTrailing = false;
        config.Tool.IsToolTBT = false;
        config.Tool.HitchLength = Hitch;
        config.Guidance.UTurnRadius = 8;

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

    // A round field, its hard fence clockwise from the west point (the seam), and its boundary curve:
    // the first lap (pass 0) runs 4 m in, the boom's outer end on the fence.
    private void Open(double radius)
    {
        const int n = 180;
        var fence = Enumerable.Range(0, n).Select(i =>
        {
            double a = 2 * Math.PI * i / n;
            return new Vec3(-radius * Math.Cos(a), radius * Math.Sin(a), 0);
        }).ToList();
        _pipeline.SetBoundary(new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                IsHard = true,
            },
        });
        _pipeline.SetActiveTrack(new Models.Track.Track
        {
            Name = "Boundary Curve",
            Points = CurveProcessing.DensifyLoop(fence),
            Type = Models.Track.TrackType.Curve,
            IsClosed = true,
        }, passNumber: 0, nudgeOffset: 0);
        _pipeline.SetAutoSteerEngaged(true);
    }

    private GpsCycleResult Cycle(double e, double n, double headingRad)
    {
        // The control loop places the implement between fixes; here at the fix.
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

    // Round fields of 30 and 45 m radius: AutoSteer engaged with the boom on the first lap, a quarter
    // of the way round from the seam, drives three quarters of the lap at 8 km/h without going off,
    // and the boom's outer end stays within the guard's margin of the fence.
    [Test]
    public void A_front_boom_on_the_first_lap_is_driven_round_the_bend([Values(30.0, 45.0)] double radius)
    {
        Open(radius);
        double lap = radius - Reach;                              // where the boom's centre runs
        double rp = Math.Sqrt(lap * lap - Hitch * Hitch);         // and the pivot, 4.5 m behind it
        double a = Math.PI / 2 - Math.Atan2(Hitch, rp);           // the boom a quarter of the way round
        double e = -rp * Math.Cos(a), n = rp * Math.Sin(a), h = a;
        double step = SpeedKmh / 3.6 * 0.1, worstOut = 0, gone = 0;
        int cycles = (int)(0.75 * 2 * Math.PI * rp / step);
        for (int i = 0; i < cycles; i++)
        {
            var r = Cycle(e, n, h);
            Assert.That(r.AutoSteerDisengagedThisCycle, Is.False,
                $"{gone:F0} m round: {r.DisengageReason}");
            double steer = r.Guidance!.SteerAngle * Math.PI / 180.0;
            h += step / Wheelbase * Math.Tan(steer);
            e += Math.Sin(h) * step;
            n += Math.Cos(h) * step;
            gone += step;
            // The boom's outer end: 4.5 m ahead of the pivot, 4 m to its left (the fence side).
            double oe = e + Math.Sin(h) * Hitch - Math.Cos(h) * Reach;
            double on = n + Math.Cos(h) * Hitch + Math.Sin(h) * Reach;
            worstOut = Math.Max(worstOut, Math.Sqrt(oe * oe + on * on) - radius);
        }
        TestContext.Out.WriteLine($"driven {gone:F0} m, the boom's end at most {worstOut:F2} m over the fence");
        Assert.That(worstOut, Is.LessThan(HardFenceGuard.Margin));
    }
}
