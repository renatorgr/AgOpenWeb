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
/// A front-mounted implement round a bend, through the whole cycle (fork only): the pivot steered
/// on the pass, an 8 m boom 4.5 m ahead ran outside it by √(R² + h²) − R (0.4 m round a 26 m lap).
/// Now the pivot steers inside the bend and the boom runs on the pass.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class FrontToolCurveTests
{
    private const double Wheelbase = 2.5;
    private const double SpeedKmh = 8;
    private const double Hitch = 4.5;

    private readonly Wgs84 _origin = new(43.7128, -74.006);
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

    // A round field of 30 m radius, its fence clockwise: the boundary curve's first lap (pass 0)
    // runs 4 m in, round a 26 m circle.
    private const double FieldRadius = 30;
    private const double LapRadius = 26;

    private static List<Vec3> Fence()
    {
        const int n = 120;
        return Enumerable.Range(0, n).Select(i =>
        {
            double a = 2 * Math.PI * i / n;
            return new Vec3(-FieldRadius * Math.Cos(a), FieldRadius * Math.Sin(a), 0);
        }).ToList();
    }

    private void Open()
    {
        var fence = Fence();
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

    /// <summary>Drive a bicycle round the lap from its east point, heading south, at
    /// <paramref name="startRadius"/> from the centre; how far the boom's centre and the pivot ran
    /// off the lap (+ outside), once settled, over a whole lap (past where the loop starts).</summary>
    private (double BoomWorst, double BoomMean, double PivotMean) DriveRound(double startRadius)
    {
        double e = startRadius, n = 0, h = Math.PI, step = SpeedKmh / 3.6 * 0.1;
        double worst = 0, sum = 0, pivotSum = 0;
        int count = 0;
        int settle = 50, cycles = settle + (int)(2 * Math.PI * LapRadius / step);
        for (int i = 0; i < cycles; i++)
        {
            var r = Cycle(e, n, h);
            Assert.That(r.AutoSteerDisengagedThisCycle, Is.False, r.DisengageReason);
            double steer = r.Guidance!.SteerAngle * Math.PI / 180.0;
            h += step / Wheelbase * Math.Tan(steer);
            e += Math.Sin(h) * step;
            n += Math.Cos(h) * step;
            if (i < settle) continue;
            double ce = e + Math.Sin(h) * Hitch, cn = n + Math.Cos(h) * Hitch;
            double off = Math.Sqrt(ce * ce + cn * cn) - LapRadius;
            worst = Math.Max(worst, Math.Abs(off));
            sum += off;
            pivotSum += Math.Sqrt(e * e + n * n) - LapRadius;
            count++;
        }
        return (worst, sum / count, pivotSum / count);
    }

    [Test]
    public void A_front_boom_runs_on_the_lap_round_a_bend()
    {
        Open();
        double inside = LapRadius - Math.Sqrt(LapRadius * LapRadius - Hitch * Hitch);
        var (worst, mean, pivot) = DriveRound(LapRadius - inside);
        TestContext.Out.WriteLine($"boom off the lap: mean {mean:F2} m, worst {worst:F2} m; pivot {pivot:F2} m");
        Assert.Multiple(() =>
        {
            Assert.That(Math.Abs(mean), Is.LessThan(0.08), "the boom on the lap");
            Assert.That(worst, Is.LessThan(0.15));
            Assert.That(pivot, Is.EqualTo(-inside).Within(0.08), "the pivot inside it");
        });
    }

    [Test]
    public void Before_the_boom_ran_outside_the_lap()
    {
        // The same lap with the pivot on it (a rear implement's line): a front boom's centre is
        // √(R² + h²) − R outside, 0.39 m.
        ConfigurationStore.Instance.Tool.IsToolFrontFixed = false;
        ConfigurationStore.Instance.Tool.IsToolRearFixed = true;
        Open();
        var (_, mean, _) = DriveRound(LapRadius);
        Assert.That(mean, Is.EqualTo(Math.Sqrt(LapRadius * LapRadius + Hitch * Hitch) - LapRadius).Within(0.05));
    }

    [Test]
    public void A_rear_implement_is_steered_as_before()
    {
        ConfigurationStore.Instance.Tool.IsToolFrontFixed = false;
        ConfigurationStore.Instance.Tool.IsToolRearFixed = true;
        ConfigurationStore.Instance.Tool.HitchLength = -1.5;
        Open();
        var (_, _, pivot) = DriveRound(LapRadius);
        Assert.That(pivot, Is.EqualTo(0).Within(0.08), "the pivot on the lap");
    }
}
