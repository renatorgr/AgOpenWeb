// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.Track;

namespace AgOpenWeb.Services.Tests.Track;

/// <summary>
/// Going round obstacles chosen as Go around: the pass is bent round each one it runs into,
/// tractor and implement kept outside, never tighter than the turn radius, on the side with
/// room, and not at all when there is no room.
/// </summary>
[TestFixture]
public class ObstacleDetourTests
{
    private const double KeepOut = 4.0;  // a 6 m implement and 1 m margin
    private const double Radius = 8.0;

    private static readonly ObstacleDetour Detour = new(new PolygonOffsetService());

    private static BoundaryPolygon Rect(double x0, double y0, double x1, double y1)
    {
        var poly = new BoundaryPolygon();
        poly.Points.Add(new BoundaryPoint(x0, y0, 0));
        poly.Points.Add(new BoundaryPoint(x1, y0, 0));
        poly.Points.Add(new BoundaryPoint(x1, y1, 0));
        poly.Points.Add(new BoundaryPoint(x0, y1, 0));
        poly.UpdateBounds();
        return poly;
    }

    private static BoundaryPolygon Tree(double e, double n, double d, ObstacleAction action = ObstacleAction.GoRound)
    {
        var poly = new BoundaryPolygon { AtObstacle = action };
        poly.SetCircle(new ObstacleCircle(e, n, d));
        return poly;
    }

    private static Boundary Field(double x0, double x1, params BoundaryPolygon[] obstacles)
    {
        var b = new Boundary { OuterBoundary = Rect(x0, -100, x1, 300) };
        b.InnerBoundaries.AddRange(obstacles);
        return b;
    }

    // A pass running north along easting 0.
    private static readonly List<Vec3> North = new() { new Vec3(0, 0, 0), new Vec3(0, 200, 0) };

    private static double ClearanceFrom(IReadOnlyList<Vec3> line, double e, double n) =>
        Segments(line).Min(s => DistanceToSegment(s.A, s.B, e, n));

    private static IEnumerable<(Vec3 A, Vec3 B)> Segments(IReadOnlyList<Vec3> line)
    {
        for (int i = 0; i + 1 < line.Count; i++) yield return (line[i], line[i + 1]);
    }

    private static double DistanceToSegment(Vec3 a, Vec3 b, double e, double n)
    {
        double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing, l2 = dx * dx + dy * dy;
        double t = l2 > 0 ? Math.Clamp(((e - a.Easting) * dx + (n - a.Northing) * dy) / l2, 0, 1) : 0;
        double qx = a.Easting + t * dx - e, qy = a.Northing + t * dy - n;
        return Math.Sqrt(qx * qx + qy * qy);
    }

    // The tightest radius along the line, from circles through points about 1 m apart.
    private static double TightestRadius(IReadOnlyList<Vec3> line)
    {
        double best = double.MaxValue;
        for (int i = 4; i + 4 < line.Count; i++)
        {
            Vec3 a = line[i - 4], b = line[i], c = line[i + 4];
            double ab = Dist(a, b), bc = Dist(b, c), ca = Dist(c, a);
            if (ab > 5 || bc > 5) continue; // the long straight ends
            double cross = Math.Abs((b.Easting - a.Easting) * (c.Northing - a.Northing) - (b.Northing - a.Northing) * (c.Easting - a.Easting));
            if (cross < 1e-9) continue;
            best = Math.Min(best, ab * bc * ca / (2 * cross));
        }
        return best;
    }

    private static double Dist(Vec3 a, Vec3 b) => Math.Sqrt(Math.Pow(a.Easting - b.Easting, 2) + Math.Pow(a.Northing - b.Northing, 2));

    [Test]
    public void A_tree_on_the_pass_is_gone_round_with_the_implement_kept_out()
    {
        var bend = Detour.Compute(North, Field(-100, 100, Tree(0, 100, 6)), KeepOut, Radius);

        Assert.That(bend, Is.Not.Null);
        Assert.That(bend!.GoneRound, Is.EqualTo(new[] { 0 }));
        Assert.That(bend.Blocked, Is.Empty);
        // 3 m of tree and 4 m of keep-out from its centre (the 24-gon's corners reach 3.03 m)
        Assert.That(ClearanceFrom(bend.Points, 0, 100), Is.GreaterThanOrEqualTo(3.0 + KeepOut - 0.05));
        // back on the pass well before and after it
        foreach (var p in bend.Points.Where(p => Math.Abs(p.Northing - 100) > 40))
            Assert.That(Math.Abs(p.Easting), Is.LessThan(1e-6), $"off the pass at {p.Northing:F1}");
        // it runs the same way as the pass, with headings to match
        Assert.That(bend.Points[0].Northing, Is.LessThan(bend.Points[^1].Northing));
        Assert.That(bend.Points.Skip(1).Zip(bend.Points, (b, a) => b.Northing - a.Northing).All(d => d > 0));
    }

    [Test]
    public void The_bend_is_never_tighter_than_the_turn_radius()
    {
        var bend = Detour.Compute(North, Field(-100, 100, Tree(0, 100, 6)), KeepOut, Radius)!;
        Assert.That(TightestRadius(bend.Points), Is.GreaterThanOrEqualTo(Radius * 0.9));
    }

    // Where the line leaves the pass before the tree and rejoins it after, as distances from
    // the tree's centre along the pass.
    private static (double Before, double After) BendEnds(IReadOnlyList<Vec3> line, double treeN)
    {
        var off = line.Where(p => Math.Abs(p.Easting) > 0.01).ToList();
        return (treeN - off.Min(p => p.Northing), off.Max(p => p.Northing) - treeN);
    }

    [TestCase(4.0)]
    [TestCase(6.0)]
    [TestCase(10.0)]
    public void The_bend_is_as_short_as_its_radius_allows(double radius)
    {
        // A 6 m tree and 4 m kept: the line moves 7 m aside. Two arcs of the radius need about
        // 2 x sqrt(7 R) along the pass, plus the 7 m the grown tree reaches before its centre.
        var bend = Detour.Compute(North, Field(-100, 100, Tree(0, 100, 6)), KeepOut, radius)!;
        var (before, after) = BendEnds(bend.Points, 100);
        double ideal = 7.0 + 2 * Math.Sqrt(7.0 * radius);
        Assert.That(before, Is.LessThanOrEqualTo(ideal * 1.3), "starts near the tree");
        Assert.That(after, Is.LessThanOrEqualTo(ideal * 1.3), "ends near the tree");
        Assert.That(TightestRadius(bend.Points), Is.GreaterThanOrEqualTo(radius * 0.9));
        Assert.That(ClearanceFrom(bend.Points, 0, 100), Is.GreaterThanOrEqualTo(3.0 + KeepOut - 0.05));
        TestContext.Out.WriteLine($"R {radius}: leaves {before:F1} m before the centre, back {after:F1} m after");
    }

    [Test]
    public void A_tighter_radius_starts_the_bend_nearer_the_obstacle()
    {
        var field = Field(-100, 100, Tree(0, 100, 6));
        var wide = BendEnds(Detour.Compute(North, field, KeepOut, 10.0)!.Points, 100);
        var tight = BendEnds(Detour.Compute(North, field, KeepOut, 4.0)!.Points, 100);
        Assert.That(tight.Before, Is.LessThan(wide.Before - 2));
    }

    [Test]
    public void Near_the_edge_only_half_the_implement_must_stay_inside_the_field()
    {
        // 3 m of half implement and a 2 m margin; the tree 1 m right of the line, the field's
        // edge 10 m left. Going left (the shorter way) puts the line 6 m left, 4 m from the edge:
        // the implement still fits in the field, though not with the margin, and that is allowed.
        var field = Field(-10, 100, Tree(1, 100, 4));
        var bend = Detour.Compute(North, field, 3.0 + 2.0, Radius, margin: 2.0)!;
        Assert.That(bend.GoneRound, Is.EqualTo(new[] { 0 }));
        Assert.That(bend.Points.Min(p => p.Easting), Is.LessThan(-5.5));
        Assert.That(bend.Points.Min(p => p.Easting), Is.GreaterThanOrEqualTo(-10 + 3.0));
    }

    [Test]
    public void It_goes_round_on_the_side_that_needs_the_smaller_move()
    {
        // The tree's centre 1 m right (east) of a pass heading north: left is shorter.
        var bend = Detour.Compute(North, Field(-100, 100, Tree(1, 100, 6)), KeepOut, Radius)!;
        Assert.That(bend.Points.Min(p => p.Easting), Is.LessThan(-5.5));
        Assert.That(bend.Points.Max(p => p.Easting), Is.LessThan(1e-6));
    }

    [Test]
    public void It_goes_round_on_the_field_side_when_the_edge_is_near()
    {
        // The field's edge 8 m left of the pass: going left would take the line out.
        var bend = Detour.Compute(North, Field(-8, 100, Tree(-1, 100, 6)), KeepOut, Radius)!;
        Assert.That(bend.GoneRound, Is.EqualTo(new[] { 0 }));
        Assert.That(bend.Points.Min(p => p.Easting), Is.GreaterThan(-1e-6));
        Assert.That(ClearanceFrom(bend.Points, -1, 100), Is.GreaterThanOrEqualTo(3.0 + KeepOut - 0.05));
    }

    [Test]
    public void With_no_room_either_side_it_is_blocked_and_the_pass_stays_straight()
    {
        var bend = Detour.Compute(North, Field(-6, 6, Tree(0, 100, 6)), KeepOut, Radius)!;
        Assert.That(bend.GoneRound, Is.Empty);
        Assert.That(bend.Blocked, Is.EqualTo(new[] { 0 }));
        Assert.That(bend.Points.All(p => Math.Abs(p.Easting) < 1e-6));
    }

    [Test]
    public void A_tree_clear_of_the_implement_is_left_alone()
    {
        Assert.That(Detour.Compute(North, Field(-100, 100, Tree(7.5, 100, 6)), KeepOut, Radius), Is.Null);
    }

    [TestCase(ObstacleAction.Turn)]
    [TestCase(ObstacleAction.DriveThrough)]
    public void Only_obstacles_chosen_as_go_round_are_gone_round(ObstacleAction action)
    {
        Assert.That(Detour.Compute(North, Field(-100, 100, Tree(0, 100, 6, action)), KeepOut, Radius), Is.Null);
    }

    [Test]
    public void The_way_round_one_tree_also_goes_round_the_tree_beside_it()
    {
        // A tree on the pass and one 6 m to its left, the field's edge close on the right:
        // the line goes left of both.
        var field = Field(-100, 8, Tree(0, 100, 4), Tree(-6, 100, 4));
        var bend = Detour.Compute(North, field, KeepOut, Radius)!;
        Assert.That(bend.GoneRound, Is.EquivalentTo(new[] { 0, 1 }));
        Assert.That(ClearanceFrom(bend.Points, 0, 100), Is.GreaterThanOrEqualTo(2.0 + KeepOut - 0.05));
        Assert.That(ClearanceFrom(bend.Points, -6, 100), Is.GreaterThanOrEqualTo(2.0 + KeepOut - 0.05));
    }

    [Test]
    public void A_row_of_trees_on_the_pass_is_gone_round_in_one_bend()
    {
        var field = Field(-100, 100, Tree(0, 60, 5), Tree(0.5, 70, 5), Tree(-0.5, 80, 5));
        var bend = Detour.Compute(North, field, KeepOut, Radius)!;
        Assert.That(bend.GoneRound, Has.Count.EqualTo(3));
        foreach (var (e, n) in new[] { (0.0, 60.0), (0.5, 70.0), (-0.5, 80.0) })
            Assert.That(ClearanceFrom(bend.Points, e, n), Is.GreaterThanOrEqualTo(2.5 + KeepOut - 0.05));
        Assert.That(TightestRadius(bend.Points), Is.GreaterThanOrEqualTo(Radius * 0.9));
        // One bend, on one side: off the pass all the way from the first tree to the last, never
        // back on it between two trees and never across it.
        var beside = bend.Points.Where(p => p.Northing >= 60 && p.Northing <= 80).ToList();
        Assert.That(beside, Is.Not.Empty);
        Assert.That(beside.All(p => p.Easting < -2) || beside.All(p => p.Easting > 2), Is.True,
            "the same side of the pass past all three trees");
        Assert.That(bend.Rounds.Select(r => r.Side).Distinct().Count(), Is.EqualTo(1), "one side for the row");
    }

    [Test]
    public void A_row_of_trees_that_would_take_the_line_too_far_off_the_pass_is_not_gone_round()
    {
        // Two 12 m trees side by side west of the pass, the field's edge close on the east: going
        // round both would move the line 24 m, more than a bend may (MaxOffset, 20 m here), so the
        // tree on the pass is not gone round and the U-turn planner turns before it. On a real
        // field a 12.5 m rake was taken 47 m off its pass, nearly four passes, round two trees.
        var field = Field(-100, 6, Tree(-2, 100, 12), Tree(-14, 100, 12));
        var bend = Detour.Compute(North, field, KeepOut, Radius)!;
        Assert.That(bend.GoneRound, Is.Empty);
        Assert.That(bend.Blocked, Is.EqualTo(new[] { 0 }));
        Assert.That(bend.Points.All(p => Math.Abs(p.Easting) < 1e-6), "the pass stays straight");

        // A smaller neighbour (6 m: 17 m needed) is still gone round with the first.
        field = Field(-100, 6, Tree(-2, 100, 12), Tree(-10, 100, 6));
        bend = Detour.Compute(North, field, KeepOut, Radius)!;
        Assert.That(bend.GoneRound, Is.EquivalentTo(new[] { 0, 1 }));
        Assert.That(bend.Blocked, Is.Empty);
        Assert.That(ClearanceFrom(bend.Points, -10, 100), Is.GreaterThanOrEqualTo(3.0 + KeepOut - 0.05));
    }

    [Test]
    public void One_obstacle_that_would_take_the_line_too_far_is_not_gone_round_either()
    {
        // A 40 m copse chosen as Go around: 24 m off the pass either way is more than a bend may move it.
        var bend = Detour.Compute(North, Field(-100, 100, Tree(0, 100, 40)), KeepOut, Radius)!;
        Assert.That(bend.GoneRound, Is.Empty);
        Assert.That(bend.Blocked, Is.EqualTo(new[] { 0 }));
        Assert.That(ObstacleDetour.MaxOffset(KeepOut, 1.0), Is.EqualTo(20.0));
        Assert.That(ObstacleDetour.MaxOffset(6.25 + 7.0, 7.0), Is.EqualTo(25.0), "a 12.5 m rake: its margin and 6 m cut-in widen nothing");
    }

    [Test]
    public void A_curved_pass_is_bent_too()
    {
        // A gentle arc from (0,0) bending east; the tree on it at about 100 m.
        var arc = new List<Vec3>();
        for (int i = 0; i <= 100; i++)
        {
            double a = i * 0.004;   // 0.4 rad over 200 m: radius 500 m
            arc.Add(new Vec3(500 - 500 * Math.Cos(a), 500 * Math.Sin(a), 0));
        }
        var onArc = arc[50];
        var bend = Detour.Compute(arc, Field(-100, 200, Tree(onArc.Easting, onArc.Northing, 6)), KeepOut, Radius)!;
        Assert.That(bend.GoneRound, Is.EqualTo(new[] { 0 }));
        Assert.That(ClearanceFrom(bend.Points, onArc.Easting, onArc.Northing), Is.GreaterThanOrEqualTo(3.0 + KeepOut - 0.05));
        Assert.That(bend.Points[0].Easting, Is.EqualTo(arc[0].Easting).Within(1e-9));
        Assert.That(bend.Points[0].Northing, Is.EqualTo(arc[0].Northing).Within(1e-9));
        Assert.That(bend.Points[^1].Easting, Is.EqualTo(arc[^1].Easting).Within(1e-9));
        Assert.That(bend.Points[^1].Northing, Is.EqualTo(arc[^1].Northing).Within(1e-9));
    }

    [Test]
    public void The_same_pass_gets_the_same_bend_back()
    {
        var detour = new ObstacleDetour(new PolygonOffsetService());
        var track = new AgOpenWeb.Models.Track.Track { Points = new List<Vec3>(North) };
        var field = Field(-100, 100, Tree(0, 100, 6));
        var first = detour.For(track, 0, North, field, KeepOut, Radius, 1.0);
        Assert.That(first, Is.Not.Null, "the scenario: the pass is bent");
        Assert.That(ClearanceFrom(first!.Points, 0, 100), Is.GreaterThanOrEqualTo(3.0 + KeepOut - 0.05));
        Assert.That(detour.For(track, 0, North, field, KeepOut, Radius, 1.0), Is.SameAs(first));

        field.InnerBoundaries[0].SetCircle(new ObstacleCircle(0, 120, 6)); // moved
        var moved = detour.For(track, 0, North, field, KeepOut, Radius, 1.0);
        Assert.That(moved, Is.Not.SameAs(first));
        Assert.That(ClearanceFrom(moved!.Points, 0, 120), Is.GreaterThanOrEqualTo(3.0 + KeepOut - 0.05));
    }

    [Test]
    public void A_corner_moved_anywhere_on_an_obstacle_or_the_fields_edge_gets_a_new_bend()
    {
        // A pond of six corners beside the pass, clear of the implement. Its corner 4, neither the
        // first nor the middle one (the two the key read before), is pulled out across the pass: the
        // bend kept for the old outline was null, and the pass ran straight through the pond.
        var detour = new ObstacleDetour(new PolygonOffsetService());
        var track = new AgOpenWeb.Models.Track.Track { Points = new List<Vec3>(North) };
        var pond = new BoundaryPolygon { AtObstacle = ObstacleAction.GoRound };
        foreach (var (e, n) in new[] { (12.0, 90.0), (28.0, 90.0), (28.0, 110.0), (12.0, 110.0), (12.0, 104.0), (12.0, 96.0) })
            pond.Points.Add(new BoundaryPoint(e, n, 0));
        pond.UpdateBounds();
        var field = Field(-100, 100, pond);
        Assert.That(detour.For(track, 0, North, field, KeepOut, Radius, 1.0), Is.Null, "the scenario: clear of the pond");

        var corners = new List<BoundaryPoint>(pond.Points);
        corners[4] = new BoundaryPoint(-1, 100, 0);
        pond.Points = corners;
        var bent = detour.For(track, 0, North, field, KeepOut, Radius, 1.0);
        Assert.That(bent, Is.Not.Null, "the moved corner is seen");
        Assert.That(bent!.GoneRound, Is.EqualTo(new[] { 0 }));
        Assert.That(ClearanceFrom(bent.Points, -1, 100), Is.GreaterThanOrEqualTo(KeepOut - 0.05));

        // The field's edge moved in beside the bend (the same Boundary, its outline replaced): the
        // bend to the west no longer fits, and the one kept is not given back.
        var west = bent.Points.Min(p => p.Easting);
        Assert.That(west, Is.LessThan(-4), "the scenario: the bend goes west");
        field.OuterBoundary!.Points = Rect(-4, -100, 100, 300).Points;
        var after = detour.For(track, 0, North, field, KeepOut, Radius, 1.0);
        Assert.That(after, Is.Not.SameAs(bent));
        Assert.That(after!.Points.Min(p => p.Easting), Is.GreaterThan(-4), "inside the new edge");
    }

    [Test]
    public void The_bend_keeps_the_implement_out_of_a_U_turn_obstacle_beside_it()
    {
        // A tree on the pass to go round, to the west (the smaller move). A pond chosen as U-turn
        // lies 1 m west of where that bend runs: the line stays outside the pond, so the bend was
        // made, with half the 6 m implement through the pond. It goes round to the east.
        var alone = Detour.Compute(North, Field(-100, 100, Tree(1, 100, 4)), KeepOut, Radius)!;
        double west = alone.Points.Min(p => p.Easting);
        Assert.That(west, Is.LessThan(-4), "the scenario: alone, the bend goes west");

        var pond = Rect(west - 11, 90, west - 1, 110);
        pond.AtObstacle = ObstacleAction.Turn;
        var bend = Detour.Compute(North, Field(-100, 100, Tree(1, 100, 4), pond), KeepOut, Radius)!;
        Assert.That(bend.GoneRound, Is.EqualTo(new[] { 0 }));
        Assert.That(bend.Points.Min(p => p.Easting), Is.GreaterThan(-1e-6), "not towards the pond");
        Assert.That(bend.Points.Max(p => p.Easting), Is.GreaterThan(4), "round the tree on the other side");
    }

    [Test]
    public void A_U_turn_obstacle_the_pass_already_runs_beside_does_not_stop_a_bend_away_from_it()
    {
        // The pond 2 m west of the pass all along: the pass itself runs nearer than half the
        // implement. A bend to the east moves away from it and is made.
        var pond = Rect(-12, 50, -2, 150);
        pond.AtObstacle = ObstacleAction.Turn;
        var bend = Detour.Compute(North, Field(-100, 100, Tree(1, 100, 4), pond), KeepOut, Radius)!;
        Assert.That(bend.GoneRound, Is.EqualTo(new[] { 0 }));
        Assert.That(bend.Points.Max(p => p.Easting), Is.GreaterThan(4));
    }

    [Test]
    public void An_obstacle_across_the_pass_that_cannot_be_grown_is_told_as_one_with_no_room()
    {
        // The offset of its outline fails: there is no bend for it. Left out, the pass counted as
        // clear of it and nothing was said.
        var noGrowth = NSubstitute.Substitute.For<IPolygonOffsetService>();
        var detour = new ObstacleDetour(noGrowth);
        var bend = detour.Compute(North, Field(-100, 100, Tree(0, 100, 6)), KeepOut, Radius);
        Assert.That(bend, Is.Not.Null);
        Assert.That(bend!.GoneRound, Is.Empty);
        Assert.That(bend.Blocked, Is.EqualTo(new[] { 0 }));
        Assert.That(bend.Points.All(p => Math.Abs(p.Easting) < 1e-6), "the pass stays straight");

        // Beside the pass, clear of it: nothing to tell.
        Assert.That(detour.Compute(North, Field(-100, 100, Tree(20, 100, 6)), KeepOut, Radius), Is.Null);
    }

    private static ToolConfig Trailed(string type, double hitch, double toPivot = 0, double tank = 3.0)
    {
        var tool = new ToolConfig { TrailingHitchLength = hitch, TrailingToolToPivotLength = toPivot, TankTrailingHitchLength = tank };
        tool.SetToolType(type);
        return tool;
    }

    [TestCase("rear")]
    [TestCase("front")]
    public void A_fixed_tool_does_not_cut_inside_the_bend(string type)
    {
        Assert.That(ObstacleDetour.TrailedCutIn(Trailed(type, 2.5), 1.8, 6.0), Is.EqualTo(0));
    }

    [Test]
    public void A_trailed_tool_cuts_in_as_far_as_its_wheels_settle_inside_the_curve()
    {
        // Hitch 1.8 m behind the axle on a 6 m curve: sqrt(36 + 3.24) from the centre. The
        // wheels 2.5 m behind it: sqrt(39.24 - 6.25) = 5.744, so 0.256 m inside the line.
        Assert.That(ObstacleDetour.TrailedCutIn(Trailed("trailing", 2.5), 1.8, 6.0), Is.EqualTo(0.256).Within(0.001));
        // A radius of 0 or less is the 1 m bend the detour builds then, not "no cut-in".
        Assert.That(ObstacleDetour.TrailedCutIn(Trailed("trailing", 0.5), 0, 0), Is.EqualTo(ObstacleDetour.TrailedCutIn(Trailed("trailing", 0.5), 0, 1.0)));
        Assert.That(ObstacleDetour.TrailedCutIn(Trailed("trailing", 0.5), 0, -3), Is.GreaterThan(0.1));
        // A longer drawbar cuts in further, a wider curve less.
        Assert.That(ObstacleDetour.TrailedCutIn(Trailed("trailing", 5.0), 1.8, 6.0), Is.GreaterThan(2.0));
        Assert.That(ObstacleDetour.TrailedCutIn(Trailed("trailing", 2.5), 1.8, 12.0), Is.LessThan(0.15));
        // A working centre behind the wheels swings back out: as far behind as the drawbar is
        // long, it runs on the hitch's own circle, outside the line.
        Assert.That(ObstacleDetour.TrailedCutIn(Trailed("trailing", 2.5, toPivot: 2.5), 1.8, 6.0), Is.EqualTo(0));
    }

    [Test]
    public void A_tow_between_tank_cuts_in_further_than_the_tool_alone()
    {
        double alone = ObstacleDetour.TrailedCutIn(Trailed("trailing", 2.5), 1.8, 6.0);
        double behindTank = ObstacleDetour.TrailedCutIn(Trailed("tbt", 2.5, tank: 3.0), 1.8, 6.0);
        Assert.That(behindTank, Is.GreaterThan(alone + 0.5));
    }

    // Drives the tractor's pivot along the line and trails the tool behind it as
    // ToolPositionService does (the wheels follow the hitch, Torriem), and returns how close its
    // working bar comes to a circle.
    private static double TrailedBarClearance(IReadOnlyList<Vec3> line, ToolConfig tool, double vehicleHitch,
        double width, double e, double n, double r)
    {
        var path = new List<(double E, double N)>();
        for (int i = 0; i + 1 < line.Count; i++)
        {
            double d = Dist(line[i], line[i + 1]);
            int steps = Math.Max(1, (int)Math.Ceiling(d / 0.05));
            for (int k = 0; k < steps; k++)
                path.Add((line[i].Easting + (line[i + 1].Easting - line[i].Easting) * k / steps,
                          line[i].Northing + (line[i + 1].Northing - line[i].Northing) * k / steps));
        }
        double tank = tool.IsToolTBT ? tool.TankTrailingHitchLength : 0;
        double l = tool.TrailingHitchLength, p = tool.TrailingToolToPivotLength;
        // Settled straight behind on the pass to begin with.
        (double E, double N) tankPos = (path[0].E, path[0].N - vehicleHitch - tank);
        (double E, double N) wheels = (tankPos.E, tankPos.N - l);
        double best = double.MaxValue;
        for (int i = 0; i + 1 < path.Count; i++)
        {
            double th = Math.Atan2(path[i + 1].E - path[i].E, path[i + 1].N - path[i].N);
            (double E, double N) hitch = (path[i].E - Math.Sin(th) * vehicleHitch, path[i].N - Math.Cos(th) * vehicleHitch);
            if (tool.IsToolTBT)
            {
                double tt = Math.Atan2(hitch.E - tankPos.E, hitch.N - tankPos.N);
                tankPos = (hitch.E - Math.Sin(tt) * tank, hitch.N - Math.Cos(tt) * tank);
            }
            else tankPos = hitch;
            double ph = Math.Atan2(tankPos.E - wheels.E, tankPos.N - wheels.N);
            wheels = (tankPos.E - Math.Sin(ph) * l, tankPos.N - Math.Cos(ph) * l);
            double ce = tankPos.E - Math.Sin(ph) * (l + p), cn = tankPos.N - Math.Cos(ph) * (l + p);
            double half = width / 2, ce0 = Math.Cos(ph) * half, cn0 = -Math.Sin(ph) * half;
            var a = new Vec3(ce - ce0, cn - cn0, 0);
            var b = new Vec3(ce + ce0, cn + cn0, 0);
            best = Math.Min(best, DistanceToSegment(a, b, e, n) - r);
        }
        return best;
    }

    [TestCase("trailing", 5.0)]
    [TestCase("tbt", 3.0)]
    public void A_trailed_implement_keeps_the_margin_round_the_tree(string type, double drawbar)
    {
        // A 6 m trailed implement round a 6 m tree with a 1 m margin and a 6 m curve. Without the
        // cut-in its wheels follow the hitch inside the bend and the bar comes nearer the tree
        // than the margin; with it the bar keeps the margin.
        var tool = Trailed(type, drawbar);
        const double hitch = 1.8, width = 6.0, margin = 1.0, radius = 6.0, treeR = 3.03;
        var field = Field(-100, 100, Tree(0, 100, 6));
        double cutIn = ObstacleDetour.TrailedCutIn(tool, hitch, radius);
        double Clearance(double m) => TrailedBarClearance(
            Detour.Compute(North, field, ObstacleDetour.KeepOutFor(width, 0, 2.0, m), radius, m)!.Points,
            tool, hitch, width, 0, 100, treeR);

        double without = Clearance(margin), with = Clearance(margin + cutIn);
        TestContext.Out.WriteLine($"{type} {drawbar} m: cut-in {cutIn:F2} m, bar {without:F2} m from the tree without it, {with:F2} m with it");
        Assert.That(without, Is.LessThan(margin - 0.2));
        Assert.That(with, Is.GreaterThanOrEqualTo(margin - 0.1));
    }

    // A lap 60 x 60 m, clockwise, a point every metre, starting on its west side at N 0 (its seam)
    // and back there, the first point repeated at the end as a boundary curve's is.
    private static List<Vec3> LapFromWestSide()
    {
        var corners = new[] { (-30.0, 0.0), (-30.0, 30.0), (30.0, 30.0), (30.0, -30.0), (-30.0, -30.0), (-30.0, 0.0) };
        var lap = new List<Vec3>();
        for (int c = 0; c + 1 < corners.Length; c++)
        {
            var (e0, n0) = corners[c];
            var (e1, n1) = corners[c + 1];
            int steps = (int)Math.Round(Math.Abs(e1 - e0) + Math.Abs(n1 - n0));
            for (int k = 0; k < steps; k++)
                lap.Add(new Vec3(e0 + (e1 - e0) * k / steps, n0 + (n1 - n0) * k / steps, 0));
        }
        lap.Add(lap[0]);
        return lap;
    }

    [Test]
    public void A_lap_is_bent_as_a_loop_even_with_its_seam_beside_the_tree()
    {
        // A boundary curve's lap is a loop: walked from where it was drawn, a bend round a tree at
        // its seam was cut in two. Walked from its point furthest from the tree, the bend is whole
        // and the lap comes back joined (fork only).
        var field = Field(-100, 100, Tree(-30, 0, 4));
        var lap = LapFromWestSide();
        var bend = Detour.Compute(lap, field, KeepOut, Radius, loop: true);

        Assert.That(bend, Is.Not.Null);
        Assert.That(bend!.GoneRound, Is.EqualTo(new[] { 0 }));
        var pts = bend.Points;
        Assert.Multiple(() =>
        {
            // The tree's 2 m and the keep-out, less a few centimetres of the sampling.
            Assert.That(pts.Min(p => Math.Sqrt((p.Easting + 30) * (p.Easting + 30) + p.Northing * p.Northing)),
                Is.GreaterThan(2 + KeepOut - 0.06), "clear of the tree");
            Assert.That(Dist(pts[0], pts[^1]), Is.GreaterThan(1e-3), "the first point is not repeated");
            Assert.That(Gaps(pts).Max(), Is.LessThan(1.5), "no jump anywhere, the seam (last to first) too");
            Assert.That(pts.Any(p => Math.Abs(p.Easting - 30) < 1e-6 && Math.Abs(p.Northing) < 1), "the far side as it was");
            Assert.That(pts.Count(p => p.Northing > -15 && p.Northing < 15 && p.Easting < -20), Is.GreaterThan(30),
                "the whole bend on the west side, either side of the old seam");
        });
    }

    [Test]
    public void A_lap_the_bends_would_reach_round_is_not_bent()
    {
        // A lap 12 x 12 m with a tree on it: the bend in and out reaches further than the lap goes
        // from the tree, all the way round to its seam. No bend: the tree is blocked.
        var field = Field(-100, 100, Tree(-6, 0, 4));
        var small = new List<Vec3>();
        foreach (var p in LapFromWestSide()) small.Add(new Vec3(p.Easting / 5, p.Northing / 5, 0));
        var bend = Detour.Compute(small, field, KeepOut, Radius, loop: true);

        Assert.That(bend, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(bend!.GoneRound, Is.Empty);
            Assert.That(bend.Blocked, Is.EqualTo(new[] { 0 }));
        });
    }

    private static IEnumerable<double> Gaps(IReadOnlyList<Vec3> loop)
    {
        for (int i = 0; i < loop.Count; i++) yield return Dist(loop[i], loop[(i + 1) % loop.Count]);
    }
}
