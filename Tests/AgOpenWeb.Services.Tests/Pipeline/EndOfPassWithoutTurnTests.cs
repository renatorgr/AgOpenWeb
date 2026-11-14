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
    private Boundary? _boundary;

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
    private void Field(bool hard, List<Vec3>? headlandLine = null, double fieldHalf = 50, params BoundaryPolygon[] obstacles)
    {
        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = Square(fieldHalf).Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                IsHard = hard,
            },
        };
        boundary.InnerBoundaries.AddRange(obstacles);
        _boundary = boundary;
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

        // Engaged again in the headland, heading out: the end of the pass is not said again on
        // every fix. Joining a line that runs out to the fence goes off once, at once, and says
        // to face the field.
        _pipeline.SetAutoSteerEngaged(true);
        Drive(41.7, 43);
        Assert.Multiple(() =>
        {
            Assert.That(_cycles.Count(c => c.Result.AutoSteerDisengagedThisCycle), Is.EqualTo(1));
            Assert.That(WentOff()?.Northing, Is.EqualTo(41.7).Within(0.01), "at the press");
            Assert.That(WentOff()?.Result.DisengageReason, Does.Contain("Turn the tractor to face the field"));
        });
    }

    // Official #816 turns at the turn line (the fence moved in by the U-turn distance, 2 m here:
    // ±48) and keeps the headland for the sections. The fork's stops measure from the headland
    // line, where the crop ends: measured from the turn line, a headland 20 m wide left 17 m of
    // "field" ahead of the tractor and AutoSteer steered on towards the fence.

    [Test]
    public void A_wide_headland_still_stops_a_metre_past_its_line()
    {
        Field(hard: true, Square(30));
        Drive(10, 48);

        var off = WentOff();
        Assert.That(off, Is.Not.Null, "AutoSteer steered on towards the fence");
        Assert.That(off!.Value.Result.DisengageReason, Does.Contain("end of the pass and no U-turn"));
        Assert.That(off.Value.Northing, Is.InRange(30.9, 31.6), "a metre past the headland line");
    }

    [Test]
    public void A_wide_headland_joining_the_line_facing_out_goes_off_at_once()
    {
        Field(hard: true, Square(30));
        _pipeline.SetAutoSteerEngaged(false);
        Drive(33, 34);
        _cycles.Clear();

        _pipeline.SetAutoSteerEngaged(true);
        Drive(34.2, 36);
        Assert.Multiple(() =>
        {
            Assert.That(WentOff()?.Northing, Is.EqualTo(34.2).Within(0.01), "at the press");
            Assert.That(WentOff()?.Result.DisengageReason, Does.Contain("Turn the tractor to face the field"));
        });
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
    /// <summary>A tree chosen as Go around: a hard circle of <paramref name="radius"/> m.</summary>
    private static BoundaryPolygon Tree(double e, double n, double radius)
    {
        var tree = new BoundaryPolygon { AtObstacle = ObstacleAction.GoRound, IsHard = true };
        for (int i = 0; i < 24; i++)
        {
            double a = i * 2 * System.Math.PI / 24;
            tree.Points.Add(new BoundaryPoint(e + radius * System.Math.Sin(a), n + radius * System.Math.Cos(a), 0));
        }
        return tree;
    }

    /// <summary>
    /// Drive along <paramref name="line"/> (the tractor following it, heading along each piece)
    /// from northing <paramref name="from"/> to <paramref name="to"/>, 0.2 m a fix, northwards.
    /// </summary>
    private void DriveAlong(List<Vec3> line, double from, double to)
    {
        var pts = line.Where(p => p.Northing >= from - 1 && p.Northing <= to + 1).OrderBy(p => p.Northing).ToList();
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var a = pts[i]; var b = pts[i + 1];
            double len = System.Math.Sqrt((b.Easting - a.Easting) * (b.Easting - a.Easting) + (b.Northing - a.Northing) * (b.Northing - a.Northing));
            if (len < 1e-6) continue;
            double heading = System.Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing) * 180 / System.Math.PI;
            for (double t = 0; t < len; t += 0.2)
            {
                double e = a.Easting + (b.Easting - a.Easting) * t / len;
                _northing = a.Northing + (b.Northing - a.Northing) * t / len;
                if (_northing < from || _northing > to) continue;
                var ll = _plane.ConvertGeoCoordToWgs84(new GeoCoord(_northing, e));
                _gps.UpdateGpsData(new GpsData
                {
                    CurrentPosition = new Position { Latitude = ll.Latitude, Longitude = ll.Longitude, Heading = (heading + 360) % 360, Speed = 2.0 },
                    FixQuality = 4,
                    SatellitesInUse = 14,
                    Hdop = 0.7,
                    IsValid = true,
                });
            }
        }
    }

    /// <summary>Pass 0 bent round <paramref name="tree"/> as the pipeline bends it.</summary>
    private static List<Vec3> BentPass(Boundary boundary)
    {
        var config = ConfigurationStore.Instance;
        var goAround = new GoAroundPasses(config, new AgOpenWeb.Services.Geometry.PolygonOffsetService());
        var track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
        var pass = new List<Vec3>();
        for (double n = -200; n <= 200; n += 1) pass.Add(new Vec3(PassEasting, n, 0));
        var bend = goAround.For(track, PassEasting, pass, boundary);
        Assert.That(bend?.GoneRound, Is.Not.Empty, "the pass goes round the tree");
        return bend!.Points;
    }

    // Renato's report of 14 Nov (Tapada da Casa, simulator, 8 m front boom): 3R, bent round two
    // trees in the headland, left the crop 36 degrees off the pass. Measured against the pass the
    // tractor looked like one turning by hand, the end of the pass was not seen, and AutoSteer
    // steered on along the bend for 40 m until the fence guard stopped it with the boom over the fence.

    [Test]
    public void On_a_bend_round_a_tree_in_the_headland_AutoSteer_still_goes_off_a_metre_past_the_line()
    {
        // A wide headland (the line at ±36, the fence at ±80) and a tree in it, its keep-out
        // reaching back to 39: the pass runs on 44 m through the headland, as 3R did.
        const double line = 36;
        Field(hard: true, Square(line), 80, Tree(PassEasting + 0.5, 44, 1.0));
        var bent = BentPass(_boundary!);

        // Where the bend leaves the crop, and how far off the pass it runs there.
        int i = bent.FindIndex(p => p.Northing > line);
        var a = bent[i - 1]; var b = bent[i + 1];
        double off = System.Math.Abs(System.Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing)) * 180 / System.Math.PI;
        TestContext.Out.WriteLine($"bend leaves the crop at ({bent[i].Easting:F2},{bent[i].Northing:F2}), {off:F0} degrees off the pass");
        Assert.That(off, Is.GreaterThan(25), "the bend leaves the crop well off the pass's direction");

        DriveAlong(bent, 20, 60);

        var offAt = WentOff();
        Assert.That(offAt, Is.Not.Null, "AutoSteer steered on along the bend towards the fence");
        Assert.That(offAt!.Value.Result.DisengageReason, Does.Contain("end of the pass and no U-turn"));
        Assert.That(offAt.Value.Northing, Is.InRange(line + 0.5, line + 1.6), "a metre past the headland line");
    }

    [Test]
    public void A_bend_through_the_headland_back_into_the_crop_is_not_the_end()
    {
        // A tree on the pass in a headland strip across the field (y 10 to 16, the notch of the
        // test above), with 24 m of crop past it: the bend dips into the strip and comes back.
        var notched = new List<Vec3>
        {
            new(-40, -40, 0), new(40, -40, 0), new(40, 10, 0), new(-10, 10, 0),
            new(-10, 16, 0), new(40, 16, 0), new(40, 40, 0), new(-40, 40, 0),
        };
        Field(hard: true, notched, 50, Tree(PassEasting + 0.5, 13, 1.5));
        var bent = BentPass(_boundary!);
        DriveAlong(bent, -10, 30);
        Assert.That(WentOff(), Is.Null, "the strip is crossed on the bend");
    }
}
