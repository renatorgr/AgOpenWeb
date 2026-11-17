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
/// The fence guard (<see cref="HardFenceGuard"/>) with a U-turn planned at the end ahead (fork only). It
/// ran only with no turn planned, so on a pass with its turn planned at the far end it was off the whole
/// way, and a spike of a hard fence beside the pass took a 12 m boom's end 1.1 m over it (the beta robot
/// on Quinta Torta, simulator). Now it runs up to the start of the planned turn, which keeps the
/// implement off the fence from there.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class HardFenceGuardTurnAheadTests
{
    private const double Wheelbase = 2.9;
    private const double SpeedKmh = 8;
    private const double Hitch = 4.5;   // the boom's working line ahead of the pivot
    private const double Reach = 4;     // half the 8 m boom
    private const double PassEasting = Reach; // pass 0 runs half the implement east of the AB line

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
        config.Tool.IsToolFrontFixed = true;
        config.Tool.IsToolRearFixed = false;
        config.Tool.IsToolTrailing = false;
        config.Tool.IsToolTBT = false;
        config.Vehicle.ThreePointHitchLength = config.Vehicle.FrontHitchLength = Hitch;
        config.Tool.WorkingPointFromHitch = 0;
        config.Guidance.UTurnRadius = 6;
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

    // A hard field from x -60 to 20, its north edge at y 100, or slanting down to y 85 at the east side; U-turns
    // on, turning west, AutoSteer on pass 0 (x 4, the boom's east end at x 8) going north.
    private void Open(IEnumerable<Vec3> eastSpike, bool slantedNorth)
    {
        var fence = new List<Vec3> { new(-60, -100, 0), new(20, -100, 0) };
        fence.AddRange(eastSpike);
        fence.Add(new Vec3(20, slantedNorth ? 85 : 100, 0));
        fence.Add(new Vec3(-60, 100, 0));
        _pipeline.SetHasActiveField(true);
        _pipeline.SetBoundary(new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                IsHard = true,
            },
        });
        // The headland across the ends only, 10 m deep.
        _pipeline.SetHeadlandLine(new List<Vec3>
        {
            new(-60, -90, 0), new(20, -90, 0), new(20, slantedNorth ? 75 : 90, 0), new(-60, 90, 0),
        });
        _pipeline.SetHeadlandOn(true);
        _pipeline.SetActiveTrack(Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0)), 0, 0);
        _pipeline.SetYouTurnConfig(uTurnSkipRows: 0, isSkipWorkedMode: false,
            headlandCalculatedWidth: 10, headlandDistance: 10, isAlternateSkipMode: false);
        _pipeline.SetYouTurnEnabled(true);
        _pipeline.SetNextUTurnDirectionLeftOverride(true); // the next pass is to the west, inside the field
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

    // Drives pass 0 north from y -60, steered by the line, until AutoSteer goes off, a turn starts or y 95.
    // Returns the cycle AutoSteer went off on (null if it stayed on), where, whether a turn was planned on
    // the way and whether it started.
    private (GpsCycleResult? Off, double OffNorthing, bool TurnPlannedOnTheWay, bool TurnStarted) DrivePass()
    {
        double e = PassEasting, n = -60, h = 0, step = SpeedKmh / 3.6 * 0.1;
        bool planned = false;
        while (n < 95)
        {
            var r = Cycle(e, n, h);
            if (r.AutoSteerDisengagedThisCycle) return (r, n, planned, false);
            if (r.YouTurn?.IsExecuting == true) return (null, n, planned, true);
            planned |= r.YouTurn?.TurnPath != null;
            double steer = (r.Guidance?.SteerAngle ?? 0) * Math.PI / 180.0;
            h += step / Wheelbase * Math.Tan(steer);
            e += Math.Sin(h) * step;
            n += Math.Cos(h) * step;
        }
        return (null, n, planned, false);
    }

    // A spike of the east fence reaching 1.5 m past the boom's end at y 0 (the pivot stays 2.5 m clear of it):
    // AutoSteer goes off with the alarm before the boom gets to it, though a turn is planned at the north end.
    [Test]
    public void A_spike_of_the_fence_beside_the_pass_takes_AutoSteer_off_with_a_turn_planned_ahead()
    {
        Open(new[] { new Vec3(20, -12, 0), new Vec3(6.5, 0, 0), new Vec3(20, 6, 0) }, slantedNorth: false);
        var (off, offNorthing, planned, _) = DrivePass();

        Assert.That(planned, Is.True, "a turn was planned at the north end on the way");
        Assert.That(off, Is.Not.Null, "AutoSteer drove the boom over the spike");
        Assert.That(off!.DisengageReason, Does.Contain("heading across the hard boundary"));
        // The boom's east end (x 8) reaches the spike's south side at y -1.3 and is the guard's 0.5 m out at
        // y -0.66; 2 s ahead at 8 km/h (4.4 m) with the pivot 4.5 m behind, the stop is at y -9.6, give or
        // take a 0.22 m cycle. Looking 1.5 s or 2.5 s ahead would stop at -8.5 or -10.7.
        Assert.That(offNorthing, Is.InRange(-10.1, -9.1), "2 s before the boom gets 0.5 m over the spike");
    }

    // The same pass with no spike, its north edge slanting down at the east side, so the boom's east end
    // nears the fence before the pivot gets to the turn: the guard looks only up to the turn's start, which
    // the turn keeps clear, and the turn starts with AutoSteer on.
    [Test]
    public void At_a_slanted_end_the_planned_turn_starts_with_AutoSteer_on()
    {
        Open(Array.Empty<Vec3>(), slantedNorth: true);
        var (off, offNorthing, planned, started) = DrivePass();

        Assert.That(planned, Is.True, "a turn was planned at the north end");
        Assert.That(off, Is.Null, $"AutoSteer went off at y {offNorthing:F1}: {off?.DisengageReason}");
        Assert.That(started, Is.True, $"the turn started (the tractor got to y {offNorthing:F1})");
    }
}
