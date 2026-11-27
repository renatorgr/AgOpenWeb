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
/// A pass that runs by the tip of a bay of a hard fence, nearer than the implement is wide, with
/// field beyond it (fork only). The pass's line stays inside the fence, so the stretch outside the
/// turn line was "driven through" (<see cref="PassEnds.Find"/>): the U-turn was planned at the far
/// end of the field, and the hard fence guard took AutoSteer off 2 s before the implement's end got
/// to the fence. On a tester's field (Tapada da Casa, a 3 m no-till drill, 27 Nov 2026: the tip
/// 0.7 m from the pass, 60 m of field beyond) every job with the drill ended there. The pass now
/// ends where the rig does not fit (<see cref="PassEnds.RigFits"/>), and the U-turn is made before
/// the tip, as on the passes beside it that the bay cuts.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class PassEndAtFenceTipPipelineTests
{
    private const double Wheelbase = 2.9;
    private const double SpeedKmh = 8;
    private const double Hitch = 1.0;        // the implement's working line behind the pivot
    private const double Reach = 1.5;        // half the 3 m implement
    private const double FromBoundary = 2.8; // the U-turn distance from boundary
    private const double PassEasting = Reach; // pass 0 runs half the implement east of the AB line
    private const double TipClear = 0.7;     // the tip of the bay, from the pass

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
        config.Tool.Width = 2 * Reach;
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, (int)(2 * Reach * 100));
        config.Tool.Overlap = 0;
        config.Tool.IsToolFrontFixed = false;
        config.Tool.IsToolRearFixed = true;
        config.Tool.IsToolTrailing = false;
        config.Tool.IsToolTBT = false;
        config.Vehicle.ThreePointHitchLength = config.Vehicle.FrontHitchLength = Hitch;
        config.Vehicle.TrackWidth = 1.9;
        config.Tool.WorkingPointFromHitch = 0;
        config.Guidance.UTurnRadius = 6;
        config.Guidance.UTurnDistanceFromBoundary = FromBoundary;

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

    // A hard field from x -60 to 40 and y -150 to 150, with a bay from the east side whose tip is
    // TipClear m east of pass 0 at y 0; U-turns on, turning west, AutoSteer on pass 0 going north.
    private void Open(bool hard)
    {
        var fence = new List<Vec3>
        {
            new(-60, -150, 0), new(40, -150, 0), new(40, -40, 0), new(PassEasting + TipClear, 0, 0),
            new(40, 40, 0), new(40, 150, 0), new(-60, 150, 0),
        };
        _pipeline.SetHasActiveField(true);
        _pipeline.SetBoundary(new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                IsHard = hard,
            },
        });
        _pipeline.SetActiveTrack(Models.Track.Track.FromABLine("AB", new Vec3(0, -150, 0), new Vec3(0, 150, 0)), 0, 0);
        _pipeline.SetYouTurnConfig(uTurnSkipRows: 0, isSkipWorkedMode: false,
            headlandCalculatedWidth: 10, headlandDistance: 10, isAlternateSkipMode: false);
        _pipeline.SetYouTurnEnabled(true);
        _pipeline.SetNextUTurnDirectionLeftOverride(true); // the next pass is to the west, clear of the bay
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

    // Drives pass 0 north from y -100, steered by the line, until AutoSteer goes off, a turn starts or
    // y 140. Returns the cycle AutoSteer went off on (null if it stayed on), where the drive ended and
    // whether a turn started there.
    private (GpsCycleResult? Off, double Northing, bool TurnStarted) DrivePass()
    {
        double e = PassEasting, n = -100, h = 0, step = SpeedKmh / 3.6 * 0.1;
        while (n < 140)
        {
            var r = Cycle(e, n, h);
            if (r.AutoSteerDisengagedThisCycle) return (r, n, false);
            if (r.YouTurn?.IsExecuting == true) return (null, n, true);
            double steer = (r.Guidance?.SteerAngle ?? 0) * Math.PI / 180.0;
            h += step / Wheelbase * Math.Tan(steer);
            e += Math.Sin(h) * step;
            n += Math.Cos(h) * step;
        }
        return (null, n, false);
    }

    [Test]
    public void The_U_turn_is_made_before_the_tip_of_a_hard_fence_the_implement_does_not_fit_past()
    {
        Open(hard: true);
        var (off, northing, started) = DrivePass();

        Assert.That(off, Is.Null, $"AutoSteer went off at y {northing:F1}: {off?.DisengageReason}");
        Assert.That(started, Is.True, $"a U-turn started (the tractor got to y {northing:F1})");
        Assert.That(northing, Is.LessThan(-FromBoundary), "before the tip of the bay, not at the far end of the field");
    }

    // A soft fence is not the implement's limit: the pass is driven through as before, and the
    // U-turn is made at the north end.
    [Test]
    public void Beside_a_soft_fence_the_pass_goes_on_past_the_tip()
    {
        Open(hard: false);
        var (off, northing, started) = DrivePass();

        Assert.That(off, Is.Null, $"AutoSteer went off at y {northing:F1}: {off?.DisengageReason}");
        Assert.That(started, Is.True, $"a U-turn started (the tractor got to y {northing:F1})");
        Assert.That(northing, Is.GreaterThan(100), "at the north end of the field");
    }
}
