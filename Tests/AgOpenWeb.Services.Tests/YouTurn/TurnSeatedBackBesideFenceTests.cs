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
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.Tool;
using AgOpenWeb.Models.YouTurn;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// A U-turn seated back from the end of a pass that runs beside a side fence (fork only). A turn
/// that does not fit at the end of its pass (the implement over a hard fence, an obstacle in its
/// exit) is tried again further back by moving every turn line in, the side fence's too: on a pass
/// nearer the side fence than the setback the line moved in took the whole pass, no turn was built
/// and the operator read "Too late for a U-turn at this end" with the end still 40 m ahead (a
/// tester's field, the farm job harness, 28 Nov 2026: a pass 10 m from the side fence, an obstacle
/// on the passes the turn lands on). Such a try is now made along the pass (<see cref="PassSeatWall"/>).
/// </summary>
[TestFixture, NonParallelizable] // ConfigurationStore is a singleton.
public class TurnSeatedBackBesideFenceTests
{
    private static bool Verbose;

    private sealed class OutLogger : Microsoft.Extensions.Logging.ILogger<YouTurnCreationService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => TestContext.Out.WriteLine("    " + formatter(state, exception));
    }

    // An 8 m boom 4.5 m ahead of the rear axle, as the tester's sprayer.
    private static YouTurnCreationService NewCreation()
    {
        Verbose = Environment.GetEnvironmentVariable("TURN_VERBOSE") == "1";
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        const double width = 8;
        c.Vehicle.Wheelbase = 2.9;
        c.Tool.Width = width;
        c.Tool.Overlap = 0;
        c.Tool.Offset = 0;
        c.Vehicle.ThreePointHitchLength = c.Vehicle.FrontHitchLength = 4.5;
        c.Tool.WorkingPointFromHitch = 0;
        c.Tool.IsToolFrontFixed = true;
        c.Tool.IsToolRearFixed = false;
        c.Tool.IsToolTrailing = false;
        c.Tool.IsToolTBT = false;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, (int)(width * 100));
        c.Guidance.UTurnDistanceFromBoundary = 2.0;
        c.Guidance.UTurnRadius = 8.0;
        c.Guidance.UTurnExtension = 20.0;
        c.Guidance.UTurnStyle = 0;
        return new YouTurnCreationService(Verbose ? new OutLogger() : NullLogger<YouTurnCreationService>.Instance, new PolygonOffsetService(), c);
    }

    // The side fence: 7 m east of the pass driven (x = 4), so its turn line is 5 m from the pass and
    // the boom's end 3 m from the fence.
    private const double EastFence = 11;
    // The pass the turn lands on, three passes over (two rows skipped): 24 m west.
    private const double NextPass = -20;
    // The obstacle: beside the pass the turn lands on, 28 m before the north fence. Its turn area (the
    // 2 m from boundary and half the implement more) takes that pass from y = 62 to 78.
    private const double ObstacleE = -24, ObstacleN = 70, ObstacleRadius = 3;
    private const double ObstacleTurnArea = ObstacleRadius + 2.0 + 4.0;

    // A field from x = -100 to the east fence, y = -150 to 100; a hard obstacle a U-turn is made at.
    private static Boundary Field(bool hardFence)
    {
        var fence = new List<Vec2> { new(-100, -150), new(EastFence, -150), new(EastFence, 100), new(-100, 100) };
        var b = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                IsHard = hardFence,
            },
        };
        var obstacle = ObstacleTestField.Circle(ObstacleE, ObstacleN, ObstacleRadius);
        obstacle.AtObstacle = ObstacleAction.Turn;
        obstacle.IsHard = true;
        b.InnerBoundaries.Add(obstacle);
        return b;
    }

    private static List<Vec3> TurnLine(Boundary field) =>
        new PolygonOffsetService().CreateInwardOffset(
            field.OuterBoundary!.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList(), 2.0)!
            .Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList();

    // Pass 0 of a line at x = 0 with an 8 m implement is x = 4, driven north from y; the turn goes
    // left, two rows skipped, onto x = -20.
    private static YouTurnCreationService.TurnPathResult TurnLeft(YouTurnCreationService creation, Boundary field, double y)
    {
        var track = AgOpenWeb.Models.Track.Track.FromABLine("AB", new Vec3(0, -200, 0), new Vec3(0, 200, 0));
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = true, HowManyPathsAway = 0 };
        var turn = new YouTurnWorkingState { IsTurnLeft = true, DistanceToHeadland = 98 - y };
        return creation.CreateTurnPath(new Position { Easting = 0, Northing = y, Heading = 0 }, track,
            headingRadians: 0, abHeading: 0, field, TurnLine(field), guidance, turn,
            uTurnSkipRows: 2, headlandCalculatedWidth: 12, headlandDistance: 98 - y);
    }

    private static double NearestToObstacle(IEnumerable<Vec3> path) =>
        path.Min(p => Math.Sqrt((p.Easting - ObstacleE) * (p.Easting - ObstacleE) + (p.Northing - ObstacleN) * (p.Northing - ObstacleN)));

    // A turn at the north fence would end 8 m before the obstacle, so it is made further back. With
    // the turn lines moved in 4 m the side fence's line is west of the pass: no turn was built, and it
    // read "Too late for a U-turn at this end" 200 m before the end. A hard fence (the boom's own
    // clearance from the north fence moves the lines in first) and a soft one.
    [TestCase(true)]
    [TestCase(false)]
    public void A_turn_seated_back_on_a_pass_beside_a_side_fence_is_made(bool hardFence)
    {
        var r = TurnLeft(NewCreation(), Field(hardFence), y: -120);

        Assert.That(r.Path, Is.Not.Null,
            $"no turn (blocked {r.ClearanceBlocked}, at an obstacle {r.AtObstacle}, failure {r.Failure}, fallback {r.UsedFallback})");
        Assert.That(r.UsedFallback, Is.False);
        Assert.That(r.ObstacleSetback, Is.GreaterThan(20).And.LessThanOrEqualTo(60), "seated back before the obstacle");
        Assert.That(r.SetbackObstacleIndex, Is.EqualTo(0), "the obstacle seated it back");
        // The tractor stays inside the turn lines where they are, the side fence's too, and out of
        // the obstacle's turn area.
        Assert.That(r.Path!.Max(p => p.Easting), Is.LessThan(EastFence - 2.0 + 0.05), "the turn leaves the turn line at the side fence");
        Assert.That(r.Path!.Max(p => p.Northing), Is.LessThan(ObstacleN), "the turn is made before the obstacle");
        Assert.That(NearestToObstacle(r.Path!), Is.GreaterThan(ObstacleTurnArea - 0.1), "the turn runs into the obstacle's turn area");
        // It lands on the next pass heading south, away from the obstacle.
        var a = r.Path![^2];
        var b = r.Path![^1];
        Assert.That(b.Easting, Is.EqualTo(NextPass).Within(0.3), "the turn ends beside the next pass");
        Assert.That(b.Northing, Is.LessThan(ObstacleN - ObstacleTurnArea), "the turn ends past the obstacle's turn area");
        Assert.That(Math.IEEERemainder(Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing) - Math.PI, 2 * Math.PI) * 180 / Math.PI,
            Is.EqualTo(0).Within(3), "the turn ends at the next pass's heading");
        // And it starts ahead of the tractor.
        Assert.That(r.ArcStart!.Value.Northing, Is.GreaterThan(-118));
    }

    // The same pass with the tractor past where the turn before the obstacle would start: no turn, and
    // the reason is the obstacle, not "Too late for a U-turn at this end" 36 m before the end.
    [Test]
    public void No_turn_before_the_obstacle_is_told_as_the_obstacle()
    {
        var r = TurnLeft(NewCreation(), Field(hardFence: true), y: 62);

        Assert.That(r.Path, Is.Null);
        Assert.That(r.AtObstacle, Is.True, "the obstacle stops the turn");
        Assert.That(r.Failure, Is.Not.EqualTo(TurnFailure.TooLate), "not told as too late: the end of the pass is 36 m ahead");
    }

    // A 6 m implement behind the tractor, a 6 m radius, 1 m from boundary: the turn at the north fence
    // has the implement 3.2 m over the fence's margin and is seated back that far. The pass it lands on
    // is 4 m from the west fence, 3 m inside that fence's turn line, so with every line moved in 3.2 m
    // it was outside them: "No U-turn fits inside the boundary at this end". Along the pass the turn is
    // made, the implement's end the metre from the west fence.
    [Test]
    public void A_turn_onto_the_pass_beside_a_side_fence_is_made()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 2.5;
        c.Tool.Width = 6;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, 600);
        c.Guidance.UTurnRadius = 6;
        c.Guidance.UTurnDistanceFromBoundary = 1;
        var creation = new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, new PolygonOffsetService(), c);
        var fence = new List<Vec2> { new(-100, -10), new(100, -10), new(100, 290), new(-100, 290) };
        var field = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                IsHard = true,
            },
        };
        var turnLine = new PolygonOffsetService().CreateInwardOffset(fence, 1.0)!
            .Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList();
        // Pass 0 of a line at x = -93 is x = -90; the turn goes left onto x = -96.
        var track = AgOpenWeb.Models.Track.Track.FromABLine("AB", new Vec3(-93, -10, 0), new Vec3(-93, 290, 0));
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = true, HowManyPathsAway = 0 };
        var turn = new YouTurnWorkingState { IsTurnLeft = true, DistanceToHeadland = 189 };

        var r = creation.CreateTurnPath(new Position { Easting = -90, Northing = 100, Heading = 0 }, track,
            headingRadians: 0, abHeading: 0, field, turnLine, guidance, turn,
            uTurnSkipRows: 0, headlandCalculatedWidth: 20, headlandDistance: 189);

        Assert.That(r.Path, Is.Not.Null,
            $"no turn (blocked {r.ClearanceBlocked}, no turn built {r.NoTurnBuilt}, failure {r.Failure}, fallback {r.UsedFallback})");
        Assert.That(r.Path!.Min(p => p.Easting), Is.GreaterThan(-99.05), "the tractor leaves the west turn line");
        Assert.That(r.Path!.Max(p => p.Northing), Is.LessThan(289.05), "the tractor leaves the north turn line");
        // The implement's ends, 3 m either side of the path, keep the metre from the fences.
        var swept = ImplementSweptPath.Compute(r.Path!, RigGeometry.Geometry(c.Vehicle, c.Tool, c.ActualToolWidth));
        var clearance = TurnClearance.Evaluate(swept, fence, TurnClearance.KeepSide.Inside, 1.0);
        Assert.That(clearance.IsClear, Is.True, $"the implement is {clearance.MaxIntrusion:F2} m inside the fences' margin");
        var a = r.Path![^2];
        var b = r.Path![^1];
        Assert.That(b.Easting, Is.EqualTo(-96).Within(0.3), "the turn ends beside the next pass");
        Assert.That(Math.IEEERemainder(Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing) - Math.PI, 2 * Math.PI) * 180 / Math.PI,
            Is.EqualTo(0).Within(3), "the turn ends at the next pass's heading");
    }

    // The next pass half a metre nearer the west fence: its implement's end is half a metre inside the
    // fence's margin wherever the turn is made, so the second try along the pass is the last.
    [Test]
    public void A_fence_beside_the_pass_is_not_cleared_by_seating_the_turn_back()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 2.5;
        c.Tool.Width = 6;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, 600);
        c.Guidance.UTurnRadius = 6;
        c.Guidance.UTurnDistanceFromBoundary = 1;
        var log = new CountingLogger();
        var creation = new YouTurnCreationService(log, new PolygonOffsetService(), c);
        var fence = new List<Vec2> { new(-100, -10), new(100, -10), new(100, 290), new(-100, 290) };
        var field = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                IsHard = true,
            },
        };
        var turnLine = new PolygonOffsetService().CreateInwardOffset(fence, 1.0)!
            .Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList();
        var track = AgOpenWeb.Models.Track.Track.FromABLine("AB", new Vec3(-93.5, -10, 0), new Vec3(-93.5, 290, 0));
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = true, HowManyPathsAway = 0 };
        var turn = new YouTurnWorkingState { IsTurnLeft = true, DistanceToHeadland = 189 };

        var r = creation.CreateTurnPath(new Position { Easting = -90.5, Northing = 100, Heading = 0 }, track,
            headingRadians: 0, abHeading: 0, field, turnLine, guidance, turn,
            uTurnSkipRows: 0, headlandCalculatedWidth: 20, headlandDistance: 189);

        Assert.That(r.Path, Is.Null);
        Assert.That(r.ClearanceBlocked, Is.True);
        Assert.That(log.Lines.Count(l => l.Contains("End of pass 0 ahead")), Is.LessThanOrEqualTo(5), "tries: " + string.Join(" | ", log.Lines.Where(l => l.Contains("ntrud") || l.Contains("along the pass"))));
    }

    private sealed class CountingLogger : Microsoft.Extensions.Logging.ILogger<YouTurnCreationService>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    [Test]
    public void The_wall_stands_the_setback_before_the_end_of_the_pass()
    {
        // A pass north along x = 4, a point every 2 m from y = -150; its end at point 124 (y = 98).
        var pass = Enumerable.Range(0, 130).Select(i => new Vec3(4, -150 + 2.0 * i, 0)).ToList();

        var wall = PassSeatWall.At(pass, endIndex: 124, step: 1, tractorIndex: 15, setback: 13.0);
        Assert.That(wall, Is.Not.Null);
        Assert.That(wall!.Value.Easting, Is.EqualTo(4).Within(1e-9));
        Assert.That(wall.Value.Northing, Is.EqualTo(85).Within(1e-9));
        Assert.That((wall.Value.DirE, wall.Value.DirN), Is.EqualTo((0.0, 1.0)), "the way the tractor goes");

        // Driven the other way (the points run against the tractor), the end at point 1.
        var south = PassSeatWall.At(pass, endIndex: 1, step: -1, tractorIndex: 100, setback: 13.0);
        Assert.That(south!.Value.Northing, Is.EqualTo(-135).Within(1e-9));
        Assert.That(south.Value.DirN, Is.EqualTo(-1.0));

        // At the tractor or behind it: no wall.
        Assert.That(PassSeatWall.At(pass, endIndex: 124, step: 1, tractorIndex: 120, setback: 13.0), Is.Null);
    }

    [Test]
    public void The_turn_area_is_cut_off_at_the_wall_and_keeps_its_sides()
    {
        // The turn line of a field 30 m wide, and a wall across the pass x = 4 at y = 60.
        var turnLine = new List<Vec2> { new(-12, -148), new(14, -148), new(14, 98), new(-12, 98) };
        var wall = new PassSeatWall.Wall(4, 60, 0, 1);

        var cut = PassSeatWall.Cut(turnLine, wall);

        Assert.That(cut, Is.Not.Null);
        Assert.That(cut!.Max(p => p.Northing), Is.EqualTo(60).Within(0.002), "nothing past the wall");
        Assert.That(cut.Min(p => p.Northing), Is.EqualTo(-148).Within(0.002));
        Assert.That(cut.Min(p => p.Easting), Is.EqualTo(-12).Within(0.002), "the sides are where they were");
        Assert.That(cut.Max(p => p.Easting), Is.EqualTo(14).Within(0.002));

        // The ring's points on the wall head along it.
        var ring = new PolygonOffsetService().CalculatePointHeadings(cut);
        PassSeatWall.HeadingsAlongWall(ring, wall);
        var onWall = Enumerable.Range(0, ring.Count)
            .Where(i => Math.Abs(ring[i].Northing - 60) < 0.01 && Math.Abs(ring[(i + 1) % ring.Count].Northing - 60) < 0.01)
            .Select(i => ring[i]).ToList();
        Assert.That(onWall, Is.Not.Empty);
        foreach (var p in onWall)
            Assert.That(Math.Abs(Math.Sin(p.Heading)), Is.EqualTo(1).Within(1e-6), "east or west, along the wall");

        // A pass outside the turn line just before the wall has no piece.
        Assert.That(PassSeatWall.Cut(turnLine, new PassSeatWall.Wall(20, 60, 0, 1)), Is.Null);
    }
}
