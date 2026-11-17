// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
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
/// AutoSteer on with the tractor and its implement outside a hard outer boundary (fork only). Every
/// fence guard works with the tractor inside, so the line steered it on along a pass that lies
/// outside the field: the beta robot on Baetas (simulator) engaged just past the east fence, where
/// the field's edge slants across the passes, and AutoSteer drove the tractor 250 m up pass 12R,
/// outside the field all the way, until it was 34 m from the fence. The field here is 200 m wide
/// with its east fence slanting from (100, -10) to (40, 290); the passes run north, 6 m apart, so
/// pass 8 (E 91) is inside south of N 35 and outside north of it.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class AutoSteerOutsideHardFenceTests
{
    private const double Wheelbase = 2.9;
    private const double SpeedKmh = 8;
    private const double Hitch = 4.5;

    private readonly Wgs84 _origin = new(43.7128, -74.006);
    private LocalPlane _plane = null!;
    private GpsService _gpsService = null!;
    private ToolPositionService _toolPosition = null!;
    private GpsPipelineService _pipeline = null!;
    private IAudioService _audio = null!;
    private List<GpsCycleResult> _results = null!;
    private ConfigurationStore _config = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = _config = ConfigurationStore.Instance;
        config.Vehicle.AntennaPivot = 0;
        config.Vehicle.AntennaOffset = 0;
        config.Vehicle.AntennaHeight = 0;
        config.Vehicle.Wheelbase = Wheelbase;
        config.Tool.Width = 6;
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, 600);
        config.Tool.Overlap = 0;
        // A front boom 4.5 m ahead of the pivot, as the robot's sprayer.
        config.Tool.IsToolFrontFixed = true;
        config.Tool.IsToolRearFixed = false;
        config.Tool.IsToolTrailing = false;
        config.Tool.IsToolTBT = false;
        config.Vehicle.ThreePointHitchLength = config.Vehicle.FrontHitchLength = Hitch;
        config.Tool.WorkingPointFromHitch = 0;
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

    /// <summary>The field with its slanting east fence, the AB line at E 40 running north, on
    /// <paramref name="pass"/>; AutoSteer engaged.</summary>
    private void Open(int pass, bool hard = true, double nudge = 0)
    {
        _pipeline.SetBoundary(new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = new List<BoundaryPoint> { new(-100, -10, 0), new(100, -10, 0), new(40, 290, 0), new(-100, 290, 0) },
                IsHard = hard,
            },
        });
        var track = Models.Track.Track.FromABLine("AB", new Vec3(40, -10, 0), new Vec3(40, 290, 0));
        _pipeline.SetActiveTrack(track, passNumber: pass, nudgeOffset: nudge);
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

    /// <summary>Drive a bicycle on the cycle's steer angle from (<paramref name="e0"/>,
    /// <paramref name="n0"/>) for <paramref name="cycles"/> cycles; the cycles' results.</summary>
    private List<GpsCycleResult> Drive(double e0, double n0, double headingRad, int cycles)
    {
        double e = e0, n = n0, h = headingRad, step = SpeedKmh / 3.6 * 0.1;
        var results = new List<GpsCycleResult>();
        for (int i = 0; i < cycles; i++)
        {
            var r = Cycle(e, n, h);
            results.Add(r);
            double steer = (r.Guidance?.SteerAngle ?? 0) * Math.PI / 180.0;
            h += step / Wheelbase * Math.Tan(steer);
            e += Math.Sin(h) * step;
            n += Math.Cos(h) * step;
        }
        return results;
    }

    [Test]
    public void Engaged_outside_on_a_pass_that_stays_outside_AutoSteer_goes_off_with_the_alarm()
    {
        // 23 m east of the fence on pass 8, heading north: the pass ahead never comes into the field.
        Open(pass: 8);
        var results = Drive(91, 150, 0, 5);

        var off = results.Find(r => r.AutoSteerDisengagedThisCycle);
        Assert.Multiple(() =>
        {
            Assert.That(off, Is.Not.Null, "AutoSteer went off");
            Assert.That(off?.DisengageReason, Is.EqualTo(GpsPipelineService.OutsideFenceReason));
            Assert.That(results.IndexOf(off!), Is.LessThanOrEqualTo(1), "at once");
        });
        _audio.Received().Play(SoundEffect.UTurnTooClose);
    }

    [Test]
    public void Engaged_outside_on_a_pass_that_comes_in_only_far_ahead_AutoSteer_goes_off()
    {
        // Heading south on pass 8 from N 150: the field is 115 m ahead along the pass, all of it
        // outside the fence.
        Open(pass: 8);
        var results = Drive(91, 150, Math.PI, 5);

        Assert.That(results.Find(r => r.AutoSteerDisengagedThisCycle)?.DisengageReason,
            Is.EqualTo(GpsPipelineService.OutsideFenceReason));
    }

    [Test]
    public void Engaged_just_outside_heading_into_the_field_AutoSteer_steers_in()
    {
        // 15 m before the fence on pass 8, heading south into the field (as the farm job harness
        // starts, 12 m before the fence): steered in, AutoSteer stays on.
        Open(pass: 8);
        var results = Drive(91, 50, Math.PI, 150); // 33 m at 8 km/h, to N 17

        Assert.Multiple(() =>
        {
            Assert.That(results.Find(r => r.AutoSteerDisengagedThisCycle)?.DisengageReason, Is.Null);
            Assert.That(results[^1].Guidance?.HasGuidance, Is.True);
        });
    }

    [Test]
    public void Engaged_just_outside_on_a_nudged_line_heading_into_the_field_AutoSteer_steers_in()
    {
        // Pass 8 nudged 12 m west runs at E 79: heading south from N 110 (3 m east of the fence) it
        // comes into the field 15 m ahead. Pass 8 as it is (E 91) comes in only 75 m ahead, so the
        // guard walking the pass without the nudge took AutoSteer off.
        Open(pass: 8, nudge: -12);
        var results = Drive(79, 110, Math.PI, 150);

        Assert.Multiple(() =>
        {
            Assert.That(results.Find(r => r.AutoSteerDisengagedThisCycle)?.DisengageReason, Is.Null);
            Assert.That(results[^1].Guidance?.HasGuidance, Is.True);
        });
    }

    [Test]
    public void Outside_a_soft_boundary_AutoSteer_stays_on()
    {
        Open(pass: 8, hard: false);
        var results = Drive(91, 150, 0, 20);

        Assert.That(results.Find(r => r.AutoSteerDisengagedThisCycle)?.DisengageReason, Is.Null);
    }

    [Test]
    public void A_side_shifted_implement_inside_the_fence_with_the_tractor_beyond_it_AutoSteer_stays_on()
    {
        // The implement 5 m to the left of the tractor: on pass 4 the tractor runs at E 72, 4 m
        // east of the fence at N 150 and outside it all the way north, the implement 1 m inside.
        _config.Tool.Offset = -5;
        Open(pass: 4);
        var results = Drive(72, 150, 0, 20);

        Assert.That(results.Find(r => r.AutoSteerDisengagedThisCycle)?.DisengageReason, Is.Null);
    }
}
