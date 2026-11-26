// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Tool;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// A turn's exit swung wide for a trailed implement (<see cref="TrailedExitSwing"/>): the tractor
/// goes past the next pass and comes back onto it, so the implement is square on the pass when
/// the tractor is. A tester's semi-mounted drill: the pin 1.54 m behind the rear axle, the wheels
/// 4.15 m behind the pin, the discs 1.6 m ahead of the wheels, turns of 4.3 m. Fork only.
/// </summary>
[TestFixture]
public class TrailedExitSwingTests
{
    private const double Radius = 4.3;
    private static readonly ToolGeometry Drill = new(ToolMount.Trailing, 3.0, 0, -1.54, 4.15, -1.6, 0);

    private static double Deg(double rad) => rad * 180.0 / Math.PI;

    private static double Wrap(double a)
    {
        while (a > Math.PI) a -= 2 * Math.PI;
        while (a < -Math.PI) a += 2 * Math.PI;
        return a;
    }

    private static void Line(List<Vec3> path, double heading, double length, double step)
    {
        var from = path[^1];
        int pieces = Math.Max(1, (int)Math.Round(length / step));
        for (int i = 1; i <= pieces; i++)
        {
            double d = length * i / pieces;
            path.Add(new Vec3(from.Easting + Math.Sin(heading) * d, from.Northing + Math.Cos(heading) * d, heading));
        }
    }

    /// <summary>An arc from the path's last point heading <paramref name="heading"/>; +1 turns clockwise. Returns the new heading.</summary>
    private static double Arc(List<Vec3> path, double heading, double radius, double angle, int dir, double step)
    {
        var from = path[^1];
        double cE = from.Easting + radius * dir * Math.Cos(heading), cN = from.Northing - radius * dir * Math.Sin(heading);
        int pieces = Math.Max(1, (int)Math.Ceiling(angle * radius / step));
        for (int i = 1; i <= pieces; i++)
        {
            double h = heading + dir * angle * i / pieces;
            path.Add(new Vec3(cE - radius * dir * Math.Cos(h), cN + radius * dir * Math.Sin(h), h));
        }
        return heading + dir * angle;
    }

    /// <summary>
    /// A turn as it is planned for the tractor: north along E = 0, round to the left onto the pass
    /// <paramref name="rows"/> m to the west, and south along it. Rows nearer than twice the radius
    /// get an omega (out to the right, round, back to the right), wider ones a quarter circle, a
    /// straight and a quarter circle. <paramref name="mirror"/>: the same turn to the right.
    /// </summary>
    private static List<Vec3> Planned(double rows, double radius = Radius, bool mirror = false, double step = 0.5,
        double entry = 20, double exit = 12.5)
    {
        var path = new List<Vec3> { new(0, -entry, 0) };
        Line(path, 0, entry, step);
        double h = 0;
        if (rows < 2 * radius - 1e-9)
        {
            double out_ = Math.Acos(0.5 + rows / (4 * radius));
            h = Arc(path, h, radius, out_, +1, step);
            h = Arc(path, h, radius, Math.PI + 2 * out_, -1, step);
            h = Arc(path, h, radius, out_, +1, step);
        }
        else
        {
            h = Arc(path, h, radius, Math.PI / 2, -1, step);
            if (rows > 2 * radius + 1e-9) Line(path, h, rows - 2 * radius, step);
            h = Arc(path, h, radius, Math.PI / 2, -1, step);
        }
        Line(path, h, exit, step);
        if (mirror)
            for (int i = 0; i < path.Count; i++) path[i] = new Vec3(-path[i].Easting, path[i].Northing, -path[i].Heading);
        return path;
    }

    /// <summary>The path with a point every 5 cm: for following the implement more finely than the path's own points allow.</summary>
    private static List<Vec3> Fine(IReadOnlyList<Vec3> path)
    {
        var fine = new List<Vec3> { path[0] };
        for (int i = 1; i < path.Count; i++)
        {
            double len = Math.Sqrt(Math.Pow(path[i].Easting - path[i - 1].Easting, 2) + Math.Pow(path[i].Northing - path[i - 1].Northing, 2));
            int pieces = Math.Max(1, (int)Math.Ceiling(len / 0.05));
            for (int k = 1; k <= pieces; k++)
            {
                double f = (double)k / pieces;
                fine.Add(new Vec3(path[i - 1].Easting + (path[i].Easting - path[i - 1].Easting) * f,
                    path[i - 1].Northing + (path[i].Northing - path[i - 1].Northing) * f, path[i].Heading));
            }
        }
        return fine;
    }

    /// <summary>The implement's centre where the pivot is at point <paramref name="index"/> of the path, followed finely.</summary>
    private static Vec3 ToolWhenPivotAt(IReadOnlyList<Vec3> path, int index, ToolGeometry tool)
    {
        var upTo = path.Take(Math.Min(index + 2, path.Count)).ToList();
        var fine = Fine(upTo);
        int at = Fine(upTo.Take(index + 1).ToList()).Count - 1;
        return ImplementSweptPath.Compute(fine, tool).ToolCenter[at];
    }

    /// <summary>How far south of the turn's top the implement is on the pass for good (within 5 cm of it).</summary>
    private static double SquareBelowTop(IReadOnlyList<Vec3> path, double passEasting, ToolGeometry tool)
    {
        var fine = Fine(path);
        var centre = ImplementSweptPath.Compute(fine, tool).ToolCenter;
        double top = path.Max(p => p.Northing);
        int last = -1;
        for (int i = 0; i < centre.Count; i++)
            if (Math.Abs(centre[i].Easting - passEasting) > 0.05) last = i;
        return top - centre[Math.Min(last + 1, centre.Count - 1)].Northing;
    }

    [Test]
    public void A_wide_turn_is_swung_and_the_drill_is_square_when_the_tractor_is()
    {
        var planned = Planned(rows: 9);
        var swung = TrailedExitSwing.Apply(planned, Drill, Radius);

        Assert.That(swung, Is.Not.Null);
        Assert.That(Deg(swung!.Swing), Is.InRange(30, 50), "the swing");
        Assert.That(Deg(swung.ToolAngleBefore), Is.GreaterThan(20), "planned for the tractor, the drill is still at an angle where the tractor is straight");

        var drill = ToolWhenPivotAt(swung.Path, swung.OnPassIndex, Drill);
        Assert.That(Deg(Wrap(drill.Heading - Math.PI)), Is.EqualTo(0).Within(0.5), "the drill heads along the pass where the tractor comes straight");
        Assert.That(drill.Easting, Is.EqualTo(-9).Within(0.03), "and is on the pass");

        // Followed finely: on the pass about 6 m below the top of the turn, where planned for the tractor it took 20 m.
        double before = SquareBelowTop(planned, -9, Drill), after = SquareBelowTop(swung.Path, -9, Drill);
        Assert.That(before, Is.GreaterThan(12.5 - 0.5), "planned for the tractor, the drill is not on the pass by the end of the path");
        Assert.That(after, Is.LessThan(8), $"swung, the drill is on the pass {after:F1} m below the top");
    }

    [Test]
    public void The_planned_path_is_kept_up_to_the_top_of_its_last_arc()
    {
        var planned = Planned(rows: 9);
        var swung = TrailedExitSwing.Apply(planned, Drill, Radius)!;

        for (int i = 0; i <= swung.FromIndex; i++)
        {
            Assert.That(swung.Path[i].Easting, Is.EqualTo(planned[i].Easting));
            Assert.That(swung.Path[i].Northing, Is.EqualTo(planned[i].Northing));
        }
        double top = planned.Max(p => p.Northing);
        Assert.That(swung.Path.Max(p => p.Northing), Is.EqualTo(top).Within(0.02), "the swung turn goes no deeper into the headland");
        // Kept to the end of the straight between the two quarter circles: the last arc's start.
        Assert.That(planned[swung.FromIndex].Northing, Is.EqualTo(top).Within(0.02));
        Assert.That(planned[swung.FromIndex].Easting, Is.EqualTo(-9 + Radius).Within(0.3));
    }

    [Test]
    public void The_tractor_goes_past_the_next_pass_and_comes_back_onto_it()
    {
        var swung = TrailedExitSwing.Apply(Planned(rows: 9), Drill, Radius)!;

        Assert.That(swung.PastPass, Is.InRange(1.0, 3.0));
        Assert.That(swung.Path.Min(p => p.Easting), Is.EqualTo(-9 - swung.PastPass).Within(0.05));
        Assert.That(swung.Straight, Is.EqualTo(2 * Radius * (1 - Math.Cos(swung.Swing))).Within(0.05),
            "the straight added before the last arc is what the counter-arc needs to end on the pass");

        // From where the tractor comes straight: on the pass, heading along it, for 6 m or more.
        var onPass = swung.Path[swung.OnPassIndex];
        Assert.That(onPass.Easting, Is.EqualTo(-9).Within(0.01));
        for (int i = swung.OnPassIndex; i < swung.Path.Count; i++)
        {
            Assert.That(swung.Path[i].Easting, Is.EqualTo(-9).Within(0.01));
            Assert.That(Deg(Wrap(swung.Path[i].Heading - Math.PI)), Is.EqualTo(0).Within(0.01));
        }
        Assert.That(onPass.Northing - swung.Path[^1].Northing, Is.GreaterThanOrEqualTo(TrailedExitSwing.LegAfter - 0.01));
    }

    [Test]
    public void The_tractor_never_turns_tighter_than_the_turn_radius()
    {
        var swung = TrailedExitSwing.Apply(Planned(rows: 9), Drill, Radius)!;
        var p = swung.Path;
        for (int i = swung.FromIndex + 1; i < p.Count - 1; i++)
        {
            // The circle through three points in a row.
            double a = Dist(p[i - 1], p[i]), b = Dist(p[i], p[i + 1]), c = Dist(p[i - 1], p[i + 1]);
            double area2 = Math.Abs((p[i].Easting - p[i - 1].Easting) * (p[i + 1].Northing - p[i - 1].Northing)
                                  - (p[i + 1].Easting - p[i - 1].Easting) * (p[i].Northing - p[i - 1].Northing));
            if (area2 < 1e-9) continue;
            double r = a * b * c / (2 * area2);
            // Where the arc hands over to the counter-arc the three points span both: no bend there.
            Assert.That(r, Is.GreaterThan(Radius * 0.98), $"point {i}");
        }
    }

    private static double Dist(Vec3 a, Vec3 b) => Math.Sqrt(Math.Pow(a.Easting - b.Easting, 2) + Math.Pow(a.Northing - b.Northing, 2));

    [Test]
    public void A_plain_U_turn_gets_a_straight_at_its_top()
    {
        var planned = Planned(rows: 2 * Radius);
        var swung = TrailedExitSwing.Apply(planned, Drill, Radius);

        Assert.That(swung, Is.Not.Null);
        Assert.That(swung!.Straight, Is.EqualTo(2 * Radius * (1 - Math.Cos(swung.Swing))).Within(0.35),
            "kept to the top of the half circle (a point of it, so a little either way), then straight across");
        Assert.That(SquareBelowTop(swung.Path, -2 * Radius, Drill), Is.LessThan(8));
    }

    [Test]
    public void An_omega_that_swings_too_little_is_swung_more()
    {
        // Rows 6 m apart: the omega comes back onto the pass through 32°, and the drill needs 43°.
        var planned = Planned(rows: 6);
        var swung = TrailedExitSwing.Apply(planned, Drill, Radius);

        Assert.That(swung, Is.Not.Null);
        Assert.That(Deg(swung!.Swing), Is.InRange(36, 50));
        var drill = ToolWhenPivotAt(swung.Path, swung.OnPassIndex, Drill);
        Assert.That(Deg(Wrap(drill.Heading - Math.PI)), Is.EqualTo(0).Within(0.5));
        Assert.That(SquareBelowTop(swung.Path, -6, Drill), Is.LessThan(SquareBelowTop(planned, -6, Drill) - 3));
    }

    [Test]
    public void An_omega_that_already_swings_enough_is_left_alone()
    {
        // A 6 m turn onto the next row 3 m away: the omega's own way back is 51°, more than the drill needs.
        Assert.That(TrailedExitSwing.Apply(Planned(rows: 3, radius: 6), Drill, 6), Is.Null);
    }

    [Test]
    public void A_turn_to_the_right_is_the_mirror_of_one_to_the_left()
    {
        var left = TrailedExitSwing.Apply(Planned(rows: 9), Drill, Radius)!;
        var right = TrailedExitSwing.Apply(Planned(rows: 9, mirror: true), Drill, Radius);

        Assert.That(right, Is.Not.Null);
        Assert.That(right!.Swing, Is.EqualTo(left.Swing).Within(1e-6));
        Assert.That(right.Path.Count, Is.EqualTo(left.Path.Count));
        for (int i = 0; i < left.Path.Count; i++)
        {
            Assert.That(right.Path[i].Easting, Is.EqualTo(-left.Path[i].Easting).Within(1e-6));
            Assert.That(right.Path[i].Northing, Is.EqualTo(left.Path[i].Northing).Within(1e-6));
        }
    }

    [Test]
    public void A_tail_that_would_leave_the_turn_area_is_not_made()
    {
        var planned = Planned(rows: 9);
        // No more than half a metre past the next pass (a fence beside it): the swing needs 2 m.
        Assert.That(TrailedExitSwing.Apply(planned, Drill, Radius, p => p.Easting >= -9.5), Is.Null);
        // The whole headland free, down to the top of the planned turn: made.
        double top = planned.Max(p => p.Northing);
        Assert.That(TrailedExitSwing.Apply(planned, Drill, Radius, p => p.Northing <= top + 0.01), Is.Not.Null);
    }

    [Test]
    public void Where_the_turn_line_slants_the_tail_starts_further_round_the_arc()
    {
        // The limit falls away to the west past the top of the last arc: straight across from the
        // top would cross it, so the straight is laid from a point further round, heading back into the field.
        var planned = Planned(rows: 9);
        double top = planned.Max(p => p.Northing);
        bool Allowed(Vec3 p) => p.Northing <= top + 0.01 - Math.Max(0, -9 + Radius - p.Easting) * 0.25;
        var swung = TrailedExitSwing.Apply(planned, Drill, Radius, Allowed);

        Assert.That(swung, Is.Not.Null);
        Assert.That(planned[swung!.FromIndex].Northing, Is.LessThan(top - 0.05), "not from the top");
        for (int i = swung.FromIndex + 1; i <= swung.OnPassIndex; i++) Assert.That(Allowed(swung.Path[i]), $"point {i}");
        var drill = ToolWhenPivotAt(swung.Path, swung.OnPassIndex, Drill);
        Assert.That(Deg(Wrap(drill.Heading - Math.PI)), Is.EqualTo(0).Within(0.5));
    }

    [Test]
    public void A_tool_behind_a_tank_is_swung_too()
    {
        var tbt = new ToolGeometry(ToolMount.TBT, 6.0, 0, -1.2, 3.0, 0.5, 3.0);
        var planned = Planned(rows: 12, radius: 6);
        var swung = TrailedExitSwing.Apply(planned, tbt, 6);

        Assert.That(swung, Is.Not.Null);
        var tool = ToolWhenPivotAt(swung!.Path, swung.OnPassIndex, tbt);
        Assert.That(Deg(Wrap(tool.Heading - Math.PI)), Is.EqualTo(0).Within(0.5));
    }

    [Test]
    public void A_fixed_implement_and_a_path_that_is_no_turn_are_left_alone()
    {
        var planned = Planned(rows: 9);
        Assert.That(TrailedExitSwing.Apply(planned, new ToolGeometry(ToolMount.RearFixed, 3.0, 0, -1.5, 0, 0, 0), Radius), Is.Null);
        Assert.That(TrailedExitSwing.Apply(planned, new ToolGeometry(ToolMount.FrontFixed, 8.0, 0, 4.7, 0, 0, 0), Radius), Is.Null);

        var straight = new List<Vec3> { new(0, 0, 0) };
        Line(straight, 0, 30, 0.5);
        Assert.That(TrailedExitSwing.Apply(straight, Drill, Radius), Is.Null);
        Assert.That(TrailedExitSwing.Apply(planned.Take(5).ToList(), Drill, Radius), Is.Null);
        Assert.That(TrailedExitSwing.Apply(planned, Drill, 0), Is.Null);
    }

    [Test]
    public void A_short_implement_that_follows_the_tractor_closely_is_left_alone()
    {
        // 0.3 m behind its pin: 9° off the pass where the tractor comes straight, 5 cm, and on it a metre on.
        var shortTool = new ToolGeometry(ToolMount.Trailing, 3.0, 0, -0.2, 0.3, 0, 0);
        Assert.That(TrailedExitSwing.Apply(Planned(rows: 9), shortTool, Radius), Is.Null);
    }
}
