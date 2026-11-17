// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.YouTurn;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// A U-turn near a Go around tree at the end of a pass (fork only). Found by the farm job harness
/// on a tester's field: a tree 33 m before a slanted hard fence on the pass, an 8 m front boom.
/// Kept off the fence, the turn was seated back onto the bend round the tree; the arc, built at
/// the bend's heading, turned 180° from there, ended 40° off the next pass and joined it with a
/// kink, and the turn was refused as malformed: "U-turn blocked: implement would swing into a
/// hard boundary", and every job on that field stopped at its first turn.
/// </summary>
[TestFixture, NonParallelizable] // ConfigurationStore is a singleton.
public class TurnAtGoAroundBendTests
{
    private static YouTurnCreationService NewCreation(double reach)
    {
        Verbose = Environment.GetEnvironmentVariable("TURN_VERBOSE") == "1";
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        const double width = 8;
        c.Vehicle.Wheelbase = 2.9;
        c.Tool.Width = width;
        c.Tool.Overlap = 0;
        c.Tool.Offset = 0;
        c.Vehicle.ThreePointHitchLength = c.Vehicle.FrontHitchLength = reach;
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

    private static bool Verbose;

    private sealed class OutLogger : Microsoft.Extensions.Logging.ILogger<YouTurnCreationService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => TestContext.Out.WriteLine("    " + formatter(state, exception));
    }

    // A field 200 m wide; the north fence slants down to the east by `slant` over the field.
    private static Boundary Field(double treeE, double treeN, double slantDeg)
    {
        double t = Math.Tan(slantDeg * Math.PI / 180);
        var fence = new List<Vec2> { new(-100, -150), new(100, -150), new(100, 100 - 100 * t), new(-100, 100 + 100 * t) };
        var b = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                IsHard = true,
            },
        };
        var tree = ObstacleTestField.Circle(treeE, treeN, 1.6);
        tree.AtObstacle = ObstacleAction.GoRound;
        tree.IsHard = true;
        b.InnerBoundaries.Add(tree);
        return b;
    }

    private static List<Vec3> TurnLine(Boundary field) =>
        new PolygonOffsetService().CreateInwardOffset(
            field.OuterBoundary!.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList(), 2.0)!
            .Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList();

    private static YouTurnCreationService.TurnPathResult Turn(YouTurnCreationService creation, Boundary field, int skip, bool left,
        double y = -60, bool againstTheLine = false)
    {
        // Pass 0 north from y; the next pass `skip + 1` passes to the side. Against the line: the AB
        // line runs south and the tractor drives it the other way.
        var track = againstTheLine
            ? AgOpenWeb.Models.Track.Track.FromABLine("AB", new Vec3(0, 200, Math.PI), new Vec3(0, -200, Math.PI))
            : AgOpenWeb.Models.Track.Track.FromABLine("AB", new Vec3(0, -200, 0), new Vec3(0, 200, 0));
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = !againstTheLine, HowManyPathsAway = 0 };
        var turn = new YouTurnWorkingState { IsTurnLeft = left, DistanceToHeadland = 98 - y };
        return creation.CreateTurnPath(new Position { Easting = 0, Northing = y, Heading = 0 }, track,
            headingRadians: 0, abHeading: againstTheLine ? Math.PI : 0, field, TurnLine(field), guidance, turn,
            uTurnSkipRows: skip, headlandCalculatedWidth: 12, headlandDistance: 98 - y);
    }

    // Where a turn's path ends: across from the next pass (x = nextX, heading south), and its last
    // stretch's heading off south.
    private static (double Across, double HeadingOffDeg) Landing(IReadOnlyList<Vec3> path, double nextX)
    {
        var a = path[^2];
        var b = path[^1];
        double heading = Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing);
        return (b.Easting - nextX, Math.IEEERemainder(heading - Math.PI, 2 * Math.PI) * 180 / Math.PI);
    }

    private static double NearestToTree(IEnumerable<Vec3> path, double treeE, double treeN) =>
        path.Min(p => Math.Sqrt((p.Easting - treeE) * (p.Easting - treeE) + (p.Northing - treeN) * (p.Northing - treeN)));

    // The path from the arc's start on (the entry leg before it is the pass, bent round the tree).
    private static List<Vec3> FromArc(YouTurnCreationService.TurnPathResult r)
    {
        var path = r.Path!;
        if (r.ArcStart is not { } at) return path;
        int k = 0;
        double best = double.MaxValue;
        for (int i = 0; i < path.Count; i++)
        {
            double d = GeometryMath.Distance(path[i], at);
            if (d < best) { best = d; k = i; }
        }
        return path.Skip(k).ToList();
    }

    // How far the tree's 24-sided outline (vertices on the circle) can lie inside its circle, and the
    // polygon offset's own rounding: the outlines the planner grows are not exact circles.
    private const double OutlineSlack = 0.1;

    // The pass steered is x = 4 (pass 0 of a line at x = 0, an 8 m implement); the turn goes onto the
    // pass beside it (skip 0, 8 m to the side) or two over (skip 1, 16 m).
    [TestCase(20.0, -1.5, false, 1, 24.0, 20.0)] // the tree on the far side: the bend leans towards the next pass
    [TestCase(0.0, 1.5, true, 1, 24.0, -12.0)]   // the tree on the near side: the bend leans away from it
    [TestCase(0.0, 1.5, true, 1, 28.0, -12.0)]
    [TestCase(20.0, 1.5, true, 1, 20.0, -12.0)]
    [TestCase(-20.0, 1.5, true, 1, 24.0, -12.0)]
    [TestCase(0.0, 0.0, false, 0, 28.0, 12.0)]   // seated on the bend, onto the pass beside it
    [TestCase(0.0, 0.0, true, 0, 18.0, -4.0)]    // a loop from the bend: seated further back
    public void A_turn_near_a_go_around_tree_lands_on_the_next_pass(double slant, double treeE, bool left, int skip, double gap, double nextX)
    {
        double treeN = 98 - gap;
        var r = Turn(NewCreation(4.5), Field(treeE, treeN, slant), skip, left);

        Assert.That(r.Path, Is.Not.Null, $"no turn (blocked {r.ClearanceBlocked}, at an obstacle {r.AtObstacle})");
        var (across, headingOff) = Landing(r.Path!, nextX);
        Assert.That(across, Is.EqualTo(0).Within(0.3), "the turn ends beside the next pass");
        Assert.That(headingOff, Is.EqualTo(0).Within(3), "the turn ends at the next pass's heading");
        // From the arc on, the tree's room in a turn (GoAroundRoom: half the track plus the larger of
        // the Go around margin and the U-turn distance) past its edge.
        var creation = NewCreation(4.5);
        double room = creation.GoAroundRoom(ConfigurationStore.Instance);
        Assert.That(NearestToTree(FromArc(r), treeE, treeN), Is.GreaterThan(1.6 + room - OutlineSlack), "the turn runs into the tree's room");
        // The entry leg is the pass bent round the tree: half the 8 m implement plus the margin.
        Assert.That(NearestToTree(r.Path!, treeE, treeN), Is.GreaterThan(1.6 + 4.0 + 0.3 - OutlineSlack), "the entry leg runs into the tree's room");
    }

    [Test]
    public void A_turn_seated_on_the_bend_starts_on_it()
    {
        // The tree on the far side 24 m before a slanted fence: the turn is seated on the bend's way
        // back to the pass, off the straight pass (x = 4) and at an angle to it.
        var r = Turn(NewCreation(4.5), Field(-1.5, 74, 20), skip: 1, left: false);

        Assert.That(r.Path, Is.Not.Null);
        Assert.That(r.ArcStart, Is.Not.Null);
        Assert.That(r.ArcStart!.Value.Easting, Is.GreaterThan(4.5), "the arc starts on the bend");
    }

    [Test]
    public void No_room_after_the_tree_says_it_is_the_obstacle_not_the_fence()
    {
        // Onto the pass beside it (skip 0), the next pass goes round the tree as well and has no straight
        // stretch to land on after it: no turn, and the operator is told it is the obstacle. It was
        // "implement would swing into a hard boundary", which sent the operator to the fence.
        var r = Turn(NewCreation(4.5), Field(1.5, 70, 0), skip: 0, left: true);

        Assert.That(r.Path, Is.Null);
        Assert.That(r.AtObstacle, Is.True);
        Assert.That(r.ClearanceBlocked, Is.False);
    }

    [TestCase(-60.0)]
    [TestCase(-149.0)] // at the start of a job: between the fence and the turn line, driving in
    public void A_turn_obstacle_ahead_is_seen_from_outside_the_turn_line(double y)
    {
        // A U-turn obstacle on the pass 40 m before the fence. From outside the turn line the stretch
        // under the tractor was taken for the end of the pass, the obstacle was not seen, and the
        // turn was planned at the fence through it.
        var creation = NewCreation(0);
        var field = Field(30, 0, 0);
        field.InnerBoundaries.Clear();
        var post = ObstacleTestField.Circle(4, 58, 1.5);
        post.AtObstacle = ObstacleAction.Turn;
        post.IsHard = true;
        field.InnerBoundaries.Add(post);

        var r = Turn(creation, field, skip: 1, left: true, y: y);

        Assert.That(r.AtObstacle, Is.True, "the turn is at the obstacle");
        if (r.Path != null)
            // Outside the post's turn line: the post grown by the U-turn distance (2 m), and by the
            // implement's reach (half its 8 m) for a hard one, less the arc's tolerance at the obstacle
            // turned at (0.3 m).
            Assert.That(NearestToTree(r.Path, 4, 58), Is.GreaterThan(1.5 + 2.0 + 4.0 - 0.3 - OutlineSlack), "a turn outside the post's turn line");
    }

    [Test]
    public void A_turn_obstacle_the_tractor_is_already_in_the_room_of_is_seen()
    {
        // The tractor inside the post's turn line, 6 m before it (the turn there missed): the post is
        // still what the pass runs into, not the fence 40 m on.
        var creation = NewCreation(0);
        var field = Field(30, 0, 0);
        field.InnerBoundaries.Clear();
        var post = ObstacleTestField.Circle(4, 58, 1.5);
        post.AtObstacle = ObstacleAction.Turn;
        post.IsHard = true;
        field.InnerBoundaries.Add(post);

        var r = Turn(creation, field, skip: 1, left: true, y: 52);

        Assert.That(r.AtObstacle, Is.True, "the post is what the pass runs into");
    }

    [Test]
    public void A_go_around_tree_put_on_the_entry_leg_under_a_turn_stops_it()
    {
        // A turn seated on the bend round a tree: its entry leg is the bent pass. A second tree put on
        // that leg while the turn is driven is not gone round: the rest of the turn is blocked by it.
        // The leg bent round the first tree is not (no alarm for an obstacle changed elsewhere).
        var creation = NewCreation(4.5);
        var field = Field(-1.5, 74, 20);
        var r = Turn(creation, field, skip: 1, left: false);
        Assert.That(r.Path, Is.Not.Null);
        Assert.That(r.ArcStart, Is.Not.Null);
        var path = r.Path!;
        var tractor = new Position { Easting = path[0].Easting, Northing = path[0].Northing };

        Assert.That(creation.RestOfTurnBlockedBy(path, tractor, field, -1, null, r.ArcStart), Is.EqualTo(-1),
            "the leg bent round the tree is clear");

        // On the leg, 5 m before the arc's start.
        var arc = r.ArcStart!.Value;
        int k = path.FindIndex(p => GeometryMath.Distance(p, arc) < 0.5);
        var onLeg = path.Take(k + 1).Last(p => GeometryMath.Distance(p, arc) >= 5.0);
        var second = ObstacleTestField.Circle(onLeg.Easting, onLeg.Northing, 0.5);
        second.AtObstacle = ObstacleAction.GoRound;
        field.InnerBoundaries.Add(second);

        Assert.That(creation.RestOfTurnBlockedBy(path, tractor, field, -1, null, r.ArcStart), Is.EqualTo(1),
            "the new tree on the entry leg");
    }

    [TestCase(97.5)]
    [TestCase(97.9)]
    public void Too_late_for_a_turn_is_not_said_as_no_turn_fits(double y)
    {
        // At the turn line of a hard fence (half a metre before it or less): too late to turn there,
        // and said so, not as a turn that does not fit at this end.
        var creation = NewCreation(0);
        var field = Field(30, 0, 0);
        field.InnerBoundaries.Clear();

        var r = Turn(creation, field, skip: 1, left: true, y: y);

        Assert.That(r.Path, Is.Null);
        Assert.That(r.Failure, Is.EqualTo(TurnFailure.TooLate));
        Assert.That(r.NoTurnBuilt, Is.False);
    }
}
