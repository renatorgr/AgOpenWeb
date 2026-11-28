// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
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
/// The end of a pass at the tip of a bay of a hard fence the implement does not fit past, with no
/// pass to turn onto there and more of the same pass in the crop beyond the tip (fork only). The
/// pass ends before the tip (<see cref="PassEnds.RigFits"/>), but with no pass with room at that
/// end the planner asked whether the pass goes on in the field past the headland ahead
/// (<see cref="YouTurnPathingService.GapAhead"/>), and that looked at the pass's line alone: it
/// stays inside the fence, so "No room to turn at this headland - driving on across it", AutoSteer
/// left on, and the hard fence guard took it off 2 s before the implement's end got to the fence.
/// On a tester's field (Tapada da Casa, a 3 m no-till drill, the night of 27 Nov 2026: the tip
/// 0.7 m from the pass) every job with the drill still ended there, with 37 % of the field sown.
/// Where the rig does not fit there is no way across: no turn here is the end of the pass, and
/// AutoSteer goes off a metre into the headland, as at any other end with no turn.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class NoDriveOnPastFenceTipPipelineTests
{
    private const double Wheelbase = 2.9;
    private const double SpeedKmh = 8;
    private const double Hitch = 1.0;         // the implement's working line behind the pivot
    private const double Reach = 1.5;         // half the 3 m implement
    private const double FromBoundary = 2.0;  // the U-turn distance from boundary
    private const double PassEasting = Reach; // pass 0 runs half the implement east of the AB line
    private const double CropEnd = 9.6;       // pass 0 leaves the crop this far before the tip, and is in it again as far past it

    private const string DrivingOn = "driving on across it";
    private const string EndOfPass = "end of the pass and no U-turn here";

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

    // A strip of field 8.6 m wide, x -3 to 5.6 and y -150 to 150, with room for one pass: pass 0 at
    // x 1.5 (the passes either side lie in the headland band along the fences). A bay from the east
    // side has its tip <paramref name="tipClear"/> m east of pass 0 at y 0. The crop is the strip
    // x 0 to 3, cut back round the bay: pass 0 is in the headland from y -9.6 to 9.6 and in the crop
    // again beyond. U-turns on, AutoSteer on pass 0 going north.
    private void Open(bool hard, double tipClear = 0.7)
    {
        var fence = new List<Vec3>
        {
            new(-3, -150, 0), new(5.6, -150, 0), new(5.6, -4, 0), new(PassEasting + tipClear, 0, 0),
            new(5.6, 4, 0), new(5.6, 150, 0), new(-3, 150, 0),
        };
        var crop = new List<Vec3>
        {
            new(0, -140, 0), new(3, -140, 0), new(3, -12, 0), new(0.5, -8, 0),
            new(0.5, 8, 0), new(3, 12, 0), new(3, 140, 0), new(0, 140, 0),
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
        _pipeline.SetHeadlandLine(crop);
        _pipeline.SetHeadlandOn(true);
        _pipeline.SetActiveTrack(Models.Track.Track.FromABLine("AB", new Vec3(0, -150, 0), new Vec3(0, 150, 0)), 0, 0);
        _pipeline.SetYouTurnConfig(uTurnSkipRows: 0, isSkipWorkedMode: false,
            headlandCalculatedWidth: 3, headlandDistance: 3, isAlternateSkipMode: false);
        _pipeline.SetYouTurnEnabled(true);
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

    // Drives pass 0 north from y -60, steered by the line, until AutoSteer goes off, a turn starts or
    // y 60. Returns the cycle AutoSteer went off on (null if it stayed on), where the drive ended and
    // what the planner said on the way.
    private (GpsCycleResult? Off, double Northing, List<string> Said) DrivePass()
    {
        var said = new List<string>();
        double e = PassEasting, n = -60, h = 0, step = SpeedKmh / 3.6 * 0.1;
        while (n < 60)
        {
            var r = Cycle(e, n, h);
            if (r.StatusMessage is { Length: > 0 } status && (said.Count == 0 || said[^1] != status)) said.Add(status);
            if (r.AutoSteerDisengagedThisCycle) return (r, n, said);
            if (r.YouTurn?.IsExecuting == true) return (null, n, said);
            double steer = (r.Guidance?.SteerAngle ?? 0) * Math.PI / 180.0;
            h += step / Wheelbase * Math.Tan(steer);
            e += Math.Sin(h) * step;
            n += Math.Cos(h) * step;
        }
        return (null, n, said);
    }

    [Test]
    public void No_turn_at_a_tip_of_a_hard_fence_the_implement_does_not_fit_past_is_the_end_of_the_pass()
    {
        Open(hard: true);
        var (off, northing, said) = DrivePass();

        Assert.That(said, Has.None.Contains(DrivingOn), "the implement does not fit past the tip: there is no way across");
        Assert.That(off, Is.Not.Null, $"AutoSteer stayed on to y {northing:F1}");
        Assert.That(off!.DisengageReason, Does.Contain(EndOfPass), "the end of the pass with no U-turn, not the fence guard's stop");
        Assert.That(northing, Is.InRange(-CropEnd + 0.8, -CropEnd + 2.0), "a metre into the headland, with room to turn by hand");
    }

    // The tip 1.5 m from the pass: the implement's end just reaches it, so the rig fits past, the
    // stretch is part of the pass and AutoSteer stays on through it, as before.
    [Test]
    public void Past_a_tip_the_implement_fits_by_AutoSteer_stays_on()
    {
        Open(hard: true, tipClear: 1.5);
        var (off, northing, said) = DrivePass();

        Assert.That(off, Is.Null, $"AutoSteer went off at y {northing:F1}: {off?.DisengageReason}");
        Assert.That(northing, Is.GreaterThan(CropEnd), "in the crop again past the tip");
    }

    // A soft fence is not the implement's limit: the pass goes on past the tip, as before.
    [Test]
    public void Beside_a_soft_fence_AutoSteer_stays_on_past_the_tip()
    {
        Open(hard: false);
        var (off, northing, said) = DrivePass();

        Assert.That(off, Is.Null, $"AutoSteer went off at y {northing:F1}: {off?.DisengageReason}");
        Assert.That(northing, Is.GreaterThan(CropEnd), "in the crop again past the tip");
    }
}
