// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
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

namespace AgOpenWeb.Services.Tests.Pipeline;

/// <summary>
/// At the end of a pass with no U-turn there (no pass with room at this end, no turn that fits,
/// U-turns off), AutoSteer steered on along the line and through a hard fence: on Tapada da Casa
/// (simulator) the tractor left the field and drove on 2.8 km. With a hard outer boundary it now
/// goes off with the alarm a metre into the headland, heading out, so the operator has the
/// headland to turn by hand. Fork only.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class EndOfPassWithoutTurnTests
{
    private const double ToolWidth = 6.0;
    private const double PassEasting = ToolWidth / 2; // pass 0 runs half a tool off the AB line
    private const double Headland = 40.0;             // the headland line is the square ±40

    private GpsService _gps = null!;
    private GpsPipelineService _pipeline = null!;
    private LocalPlane _plane = null!;
    private readonly List<(double Northing, GpsCycleResult Result)> _cycles = new();
    private double _northing;

    [SetUp]
    public void SetUp()
    {
        _cycles.Clear();
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.Vehicle.AntennaPivot = 0;
        config.Vehicle.AntennaOffset = 0;
        config.Vehicle.AntennaHeight = 0;
        config.Tool.Width = ToolWidth;
        config.Tool.Overlap = 0;
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, (int)(ToolWidth * 100));

        _gps = new GpsService();
        _gps.Start();
        var toolPosition = new ToolPositionService(config);
        var coverage = new CoverageMapService(config);
        var sections = new SectionControlService(toolPosition, coverage, config);
        var autoSteer = new AutoSteerService(new TrackGuidanceService(),
            Substitute.For<IUdpCommunicationService>(), _gps, config);
        var headingFusion = Substitute.For<IGpsHeadingFusionService>();
        headingFusion.FuseHeading(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>(),
                Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>())
            .Returns(ci => ci.ArgAt<double>(0));

        _pipeline = new GpsPipelineService(
            _gps, toolPosition, new TrackGuidanceService(),
            sections, coverage, autoSteer,
            new YouTurnGuidanceService(),
            new YouTurnStateMachine(
                new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance,
                    Substitute.For<AgOpenWeb.Services.Geometry.IPolygonOffsetService>(), config),
                new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, config),
                NullLogger<YouTurnStateMachine>.Instance, config),
            Substitute.For<IAudioService>(),
            new PipelineIntents(),
            headingFusion,
            NullLogger<GpsPipelineService>.Instance,
            config,
            new PositionEstimator());
        _plane = new LocalPlane(new Wgs84(43.7128, -74.006), new SharedFieldProperties());
        _pipeline.SetLocalPlane(_plane);
        _pipeline.SynchronousMode = true;
        _pipeline.Start();
        _pipeline.CycleCompleted += r => _cycles.Add((_northing, r));
    }

    [TearDown]
    public void TearDown()
    {
        _pipeline.Stop();
        _gps.Stop();
    }

    private static List<Vec3> Square(double half) => new()
    {
        new(-half, -half, 0), new(half, -half, 0), new(half, half, 0), new(-half, half, 0),
    };

    /// <summary>A 100 x 100 m field, the headland line at ±40 unless given, AutoSteer on pass 0 of a north AB line, U-turns off.</summary>
    private void Field(bool hard, List<Vec3>? headlandLine = null)
    {
        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = Square(50).Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                IsHard = hard,
            },
        };
        _pipeline.SetHasActiveField(true);
        _pipeline.SetBoundary(boundary);
        _pipeline.SetHeadlandLine(headlandLine ?? Square(Headland));
        _pipeline.SetHeadlandOn(true);
        _pipeline.SetActiveTrack(Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0)), 0, 0);
        _pipeline.SetYouTurnConfig(uTurnSkipRows: 0, isSkipWorkedMode: false,
            headlandCalculatedWidth: 10, headlandDistance: 10, isAlternateSkipMode: false);
        _pipeline.SetYouTurnEnabled(false);
        _pipeline.SetAutoSteerEngaged(true);
    }

    /// <summary>Drive pass 0 from <paramref name="from"/> to <paramref name="to"/> (north or south), 0.2 m a fix.</summary>
    private void Drive(double from, double to)
    {
        double step = to > from ? 0.2 : -0.2;
        double heading = to > from ? 0 : 180;
        for (_northing = from; step > 0 ? _northing <= to : _northing >= to; _northing += step)
        {
            var ll = _plane.ConvertGeoCoordToWgs84(new GeoCoord(_northing, PassEasting));
            _gps.UpdateGpsData(new GpsData
            {
                CurrentPosition = new Position { Latitude = ll.Latitude, Longitude = ll.Longitude, Heading = heading, Speed = 2.0 },
                FixQuality = 4,
                SatellitesInUse = 14,
                Hdop = 0.7,
                IsValid = true,
            });
        }
    }

    private (double Northing, GpsCycleResult Result)? WentOff() =>
        _cycles.Where(c => c.Result.AutoSteerDisengagedThisCycle).Select(c => ((double, GpsCycleResult)?)c).FirstOrDefault();

    [Test]
    public void At_a_hard_fence_AutoSteer_goes_off_a_metre_into_the_headland()
    {
        Field(hard: true);
        Drive(20, 48);

        var off = WentOff();
        Assert.That(off, Is.Not.Null, "AutoSteer steered on towards the fence");
        Assert.That(off!.Value.Result.DisengageReason, Does.Contain("end of the pass and no U-turn"));
        Assert.That(off.Value.Northing, Is.InRange(Headland + 0.9, Headland + 1.6), "a metre past the headland line");
    }

    [Test]
    public void At_a_soft_boundary_AutoSteer_stays_on_as_AgOpenGPS()
    {
        Field(hard: false);
        Drive(20, 48);
        Assert.That(WentOff(), Is.Null);
    }

    [Test]
    public void Heading_into_the_field_from_the_headland_is_not_the_end()
    {
        Field(hard: true);
        Drive(46, 20);
        Assert.That(WentOff(), Is.Null);
    }

    [Test]
    public void A_headland_strip_across_the_pass_with_field_past_it_is_not_the_end()
    {
        // A notch of headland from the east side across the pass, y 10 to 16, with 24 m of field past it.
        var notched = new List<Vec3>
        {
            new(-40, -40, 0), new(40, -40, 0), new(40, 10, 0), new(-10, 10, 0),
            new(-10, 16, 0), new(40, 16, 0), new(40, 40, 0), new(-40, 40, 0),
        };
        Field(hard: true, notched);
        Drive(0, 30);
        Assert.That(WentOff(), Is.Null, "the notch is crossed");

        Drive(30.2, 48);
        Assert.That(WentOff()?.Northing, Is.InRange(Headland + 0.9, Headland + 1.6), "the end of the pass still counts");
    }

    [Test]
    public void Once_per_headland_entry()
    {
        Field(hard: true);
        Drive(20, 41.5);
        Assert.That(WentOff(), Is.Not.Null);
        _cycles.Clear();

        // Engaged again in the headland, heading out: no second alarm on every fix (the fence guard is the net).
        _pipeline.SetAutoSteerEngaged(true);
        Drive(41.7, 43);
        Assert.That(WentOff(), Is.Null);
    }

    [Test]
    public void A_planned_or_driven_turn_is_not_the_end_of_the_pass()
    {
        var config = ConfigurationStore.Instance;
        var sm = new YouTurnStateMachine(
            new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance,
                Substitute.For<AgOpenWeb.Services.Geometry.IPolygonOffsetService>(), config),
            new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, config),
            NullLogger<YouTurnStateMachine>.Instance, config);
        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = Square(50).Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                IsHard = true,
            },
        };
        var track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
        var ctx = new YouTurnStateMachine.TickContext(
            new Position { Easting = PassEasting, Northing = 41, Heading = 0, Speed = 2 },
            track, boundary, Square(Headland), UTurnSkipRows: 0, IsSkipWorkedMode: false,
            HeadlandCalculatedWidth: 10, HeadlandDistance: 10, IsAlternateSkipMode: false, IsPointCovered: null);
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 0, IsHeadingSameWay = true };

        Assert.That(sm.EndOfPassWithoutTurn(in ctx, guidance, new YouTurnWorkingState()), Is.True, "no turn");
        Assert.That(sm.EndOfPassWithoutTurn(in ctx, guidance,
            new YouTurnWorkingState { TurnPath = new List<Vec3> { new(3, 41, 0), new(9, 41, 0) } }), Is.False, "a turn planned");
        Assert.That(sm.EndOfPassWithoutTurn(in ctx, guidance, new YouTurnWorkingState { IsExecuting = true }), Is.False, "a turn driven");

        var across = ctx with { CurrentPosition = new Position { Easting = PassEasting, Northing = 41, Heading = 90, Speed = 2 } };
        Assert.That(sm.EndOfPassWithoutTurn(in across, guidance, new YouTurnWorkingState()), Is.False, "turning by hand, across the line");
    }
}
