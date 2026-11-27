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
/// A U-turn sized for a trailed implement (<see cref="TrailedOpenTurn"/>): the tractor turns on
/// the wider circle that keeps the implement's working centre on the U-turn radius, and ends with
/// a swing that leaves the implement square on the next pass. A tester's semi-mounted drill: the
/// pin 1.54 m behind the rear axle, the wheels 4.15 m behind the pin, the discs 1.6 m ahead of
/// the wheels, a U-turn radius of 4.3 m, 3 m passes. Fork only.
/// </summary>
[TestFixture]
public class TrailedOpenTurnTests
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

    /// <summary>The shape as a whole turn: 10 m north along E = 0 into it, 15 m south along the next pass out of it, a point every 5 cm.</summary>
    private static (List<Vec3> Path, int ShapeEnd) Turn(TrailedOpenTurn.Shape shape, bool right = true)
    {
        var path = new List<Vec3>();
        for (double back = 10.0; back > 1e-9; back -= 0.05) path.Add(new Vec3(0, -back, 0));
        path.AddRange(TrailedOpenTurn.Build(new Vec3(0, 0, 0), 0, right, shape, 0.05));
        int end = path.Count - 1;
        for (double on = 0.05; on <= 15.0; on += 0.05) path.Add(new Vec3(path[end].Easting, path[end].Northing - on, Math.PI));
        return (path, end);
    }

    /// <summary>The turn as it is planned for the tractor at the U-turn radius: a quarter circle, a straight and a quarter circle.</summary>
    private static List<Vec3> PlannedForTractor(double offset)
    {
        var shape = new TrailedOpenTurn.Shape(offset, Radius, offset - 2 * Radius, 0, Radius);
        return Turn(shape).Path;
    }

    /// <summary>The largest angle between the tractor and the implement along the path, rad.</summary>
    private static double LargestAngle(List<Vec3> path, ToolGeometry tool)
    {
        var swept = ImplementSweptPath.Compute(path, tool);
        double worst = 0;
        for (int i = 0; i < path.Count - 1; i++)
        {
            double tractor = Math.Atan2(path[i + 1].Easting - path[i].Easting, path[i + 1].Northing - path[i].Northing);
            worst = Math.Max(worst, Math.Abs(Wrap(tractor - swept.ToolCenter[i].Heading)));
        }
        return worst;
    }

    [Test]
    public void The_tractor_s_radius_keeps_the_drill_s_discs_on_the_u_turn_radius()
    {
        // √(4.3² − 1.6² + 4.15² − 1.54²)
        Assert.That(TrailedOpenTurn.OpenRadius(Drill, Radius), Is.EqualTo(5.548).Within(0.005));

        // Checked the other way: the tractor on that circle, the discs settle on the U-turn radius.
        double open = TrailedOpenTurn.OpenRadius(Drill, Radius);
        var circle = new List<Vec3>();
        for (double a = 0; a < 4 * Math.PI; a += 0.01)
            circle.Add(new Vec3(open - open * Math.Cos(a), open * Math.Sin(a), a));
        var discs = ImplementSweptPath.Compute(circle, Drill).ToolCenter[^1];
        Assert.That(Math.Sqrt(Math.Pow(discs.Easting - open, 2) + Math.Pow(discs.Northing, 2)), Is.EqualTo(Radius).Within(0.02));
    }

    [Test]
    public void No_wider_radius_for_a_fixed_implement_or_one_that_already_runs_outside()
    {
        var mounted = new ToolGeometry(ToolMount.RearFixed, 3.0, 0, -1.2, 0, 0, 0);
        Assert.That(TrailedOpenTurn.OpenRadius(mounted, Radius), Is.EqualTo(Radius));

        // A short trailer on a long drawbar pin: it runs outside the tractor's circle already.
        var shortTrailer = new ToolGeometry(ToolMount.Trailing, 3.0, 0, -2.5, 1.5, 0, 0);
        Assert.That(TrailedOpenTurn.OpenRadius(shortTrailer, Radius), Is.EqualTo(Radius));
        Assert.That(TrailedOpenTurn.Solve(shortTrailer, Radius, 9.0), Is.Null);
    }

    [Test]
    public void A_very_long_implement_opens_the_turn_by_half_at_most()
    {
        var longTool = new ToolGeometry(ToolMount.Trailing, 6.0, 0, -1.0, 9.0, 0, 0);
        Assert.That(TrailedOpenTurn.OpenRadius(longTool, Radius), Is.EqualTo(Radius * TrailedOpenTurn.MaxOpenShare).Within(1e-9));
    }

    [Test]
    public void A_tbt_implement_counts_its_tank_s_drawbar_too()
    {
        var tbt = new ToolGeometry(ToolMount.TBT, 6.0, 0, -1.0, 2.0, 0, 2.5);
        Assert.That(TrailedOpenTurn.OpenRadius(tbt, 6.0), Is.EqualTo(Math.Sqrt(36 + 4 + 6.25 - 1)).Within(1e-9));
    }

    [Test]
    public void Two_passes_skipped_the_drill_is_square_where_the_tractor_is_back_on_the_pass()
    {
        var shape = TrailedOpenTurn.Solve(Drill, Radius, 9.0);
        Assert.That(shape, Is.Not.Null);
        TestContext.Out.WriteLine($"radius {shape!.Radius:F2} m, swing {Deg(shape.Swing):F1}°, top straight {shape.TopStraight:F2} m, {shape.PastPass:F2} m past the pass, runs on {shape.RunsOn:F2} m");

        Assert.Multiple(() =>
        {
            Assert.That(shape.Radius, Is.EqualTo(5.38).Within(0.05), "the main arc");
            Assert.That(shape.Radius, Is.GreaterThanOrEqualTo(Radius), "never tighter than the U-turn radius");
            Assert.That(shape.CounterRadius, Is.EqualTo(Radius), "the counter-arc at the U-turn radius");
            Assert.That(shape.TopStraight, Is.EqualTo(0).Within(1e-9));
            Assert.That(Deg(shape.Swing), Is.EqualTo(35.0).Within(1.5));
            Assert.That(shape.PastPass, Is.EqualTo(1.75).Within(0.1), "the tractor past the next pass");
        });

        var (path, end) = Turn(shape);
        var swept = ImplementSweptPath.Compute(path, Drill);
        Assert.Multiple(() =>
        {
            Assert.That(path[end].Easting, Is.EqualTo(9.0).Within(1e-6), "the counter-arc ends on the next pass");
            Assert.That(Math.Abs(Wrap(path[end].Heading - Math.PI)), Is.LessThan(1e-6), "along it");
            Assert.That(swept.ToolCenter[end].Easting, Is.EqualTo(9.0).Within(0.03), "the discs on the pass there");
            Assert.That(Deg(Math.Abs(Wrap(swept.ToolCenter[end].Heading - Math.PI))), Is.LessThan(0.5), "and square");
            // And they stay there.
            for (int i = end; i < path.Count; i++)
                Assert.That(swept.ToolCenter[i].Easting, Is.EqualTo(9.0).Within(0.03));
        });
    }

    [Test]
    public void The_drawbar_stays_further_from_the_tyre_than_in_the_turn_planned_for_the_tractor()
    {
        var shape = TrailedOpenTurn.Solve(Drill, Radius, 9.0)!;
        double open = Deg(LargestAngle(Turn(shape).Path, Drill));
        double planned = Deg(LargestAngle(PlannedForTractor(9.0), Drill));
        TestContext.Out.WriteLine($"tractor to drill: {open:F0}° in the open turn, {planned:F0}° planned for the tractor");
        Assert.Multiple(() =>
        {
            Assert.That(open, Is.LessThan(65.0));
            Assert.That(planned, Is.GreaterThan(open + 8.0));
        });
    }

    [Test]
    public void The_discs_turn_at_about_the_u_turn_radius()
    {
        var shape = TrailedOpenTurn.Solve(Drill, Radius, 9.0)!;
        var (path, _) = Turn(shape);
        var swept = ImplementSweptPath.Compute(path, Drill);
        // How far the discs go past the place the tractor leaves its pass: the tractor's arc is 5.4 m deep.
        double top = swept.ToolCenter.Max(p => p.Northing);
        Assert.That(top, Is.EqualTo(Radius).Within(0.3));
    }

    [Test]
    public void Passes_too_near_for_the_wider_arc_have_no_open_turn()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TrailedOpenTurn.Solve(Drill, Radius, 3.0), Is.Null, "the neighbouring pass");
            Assert.That(TrailedOpenTurn.Solve(Drill, Radius, 6.0), Is.Null, "one pass skipped");
        });
    }

    /// <summary>
    /// The swing is looked for from the least one that fits, not from the first whole degree
    /// above it. The farm's Puma (U-turn radius 5.4 m, the pin 1.4 m behind the rear axle) with
    /// this drill and two rows skipped (9.15 m): the implement is square 0.8 of a degree above
    /// the least swing, and started at the next whole degree the search said "no shape" there,
    /// with one at 9.10 m and one at 9.20 m (the farm job harness on the mini PC, 27 Nov 2026).
    /// </summary>
    [Test]
    public void A_swing_between_the_least_one_and_the_next_whole_degree_is_found()
    {
        const double pumaRadius = 5.4;
        var onThePuma = Drill with { AttachX = -1.4 };
        var shape = TrailedOpenTurn.Solve(onThePuma, pumaRadius, 9.15);
        Assert.That(shape, Is.Not.Null, "passes 9.15 m apart have an open turn, as 9.10 m and 9.20 m have");
        Assert.Multiple(() =>
        {
            double least = Math.Acos(9.15 / (2 * pumaRadius));
            Assert.That(shape!.Swing, Is.GreaterThan(least).And.LessThan(Math.Ceiling(Deg(least)) * Math.PI / 180.0),
                "the case: the swing lies between the least one and the next whole degree");
            Assert.That(shape.Radius, Is.GreaterThanOrEqualTo(pumaRadius), "the tractor never turns tighter than the U-turn radius");
            Assert.That(Deg(Math.Abs(TrailedOpenTurn.ToolLagAtEnd(onThePuma, shape))), Is.LessThan(0.01), "the implement is square where the counter-arc ends");
            var before = TrailedOpenTurn.Solve(onThePuma, pumaRadius, 9.10);
            var after = TrailedOpenTurn.Solve(onThePuma, pumaRadius, 9.20);
            Assert.That(before, Is.Not.Null);
            Assert.That(after, Is.Not.Null);
            Assert.That(shape.Swing, Is.LessThan(before!.Swing).And.GreaterThan(after!.Swing), "between its neighbours' swings");
        });
    }

    /// <summary>
    /// From the nearest passes that have an open turn up to twice the open radius, every spacing
    /// has one: no spacing in between is left out. Both of the farm's tractors with this drill.
    /// </summary>
    [TestCase(4.3, 1.54)]
    [TestCase(5.4, 1.4)]
    public void Every_spacing_from_the_nearest_with_an_open_turn_to_twice_the_open_radius_has_one(double radius, double pin)
    {
        var tool = Drill with { AttachX = -pin };
        double open = TrailedOpenTurn.OpenRadius(tool, radius);
        double first = double.NaN;
        var missing = new List<double>();
        for (int cm = 300; cm <= (int)Math.Floor(2 * open * 100); cm += 2)
        {
            double offset = cm / 100.0;
            if (TrailedOpenTurn.Solve(tool, radius, offset) != null)
            {
                if (double.IsNaN(first)) first = offset;
            }
            else if (!double.IsNaN(first)) missing.Add(offset);
        }
        Assert.Multiple(() =>
        {
            Assert.That(first, Is.GreaterThan(radius).And.LessThan(2 * open), "some spacing has an open turn");
            Assert.That(missing, Is.Empty, $"spacings past {first:F2} m with no open turn");
        });
    }

    [Test]
    public void Passes_past_what_the_arcs_reach_get_a_straight_along_the_top()
    {
        var shape = TrailedOpenTurn.Solve(Drill, Radius, 10.5);
        Assert.That(shape, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(shape!.Radius, Is.EqualTo(TrailedOpenTurn.OpenRadius(Drill, Radius)).Within(1e-9), "opened no further than the open radius");
            Assert.That(shape.TopStraight, Is.GreaterThan(0.5));
            var (path, end) = Turn(shape);
            var swept = ImplementSweptPath.Compute(path, Drill);
            Assert.That(path[end].Easting, Is.EqualTo(10.5).Within(1e-6));
            Assert.That(swept.ToolCenter[end].Easting, Is.EqualTo(10.5).Within(0.03));
            Assert.That(Deg(Math.Abs(Wrap(swept.ToolCenter[end].Heading - Math.PI))), Is.LessThan(0.5));
        });
    }

    [Test]
    public void Passes_further_apart_than_twice_the_open_radius_are_the_wide_turn_s()
    {
        Assert.That(TrailedOpenTurn.Solve(Drill, Radius, 12.0), Is.Null);
    }

    [Test]
    public void A_turn_to_the_left_is_the_mirror_of_one_to_the_right()
    {
        var shape = TrailedOpenTurn.Solve(Drill, Radius, 9.0)!;
        var right = TrailedOpenTurn.Build(new Vec3(0, 0, 0), 0, true, shape, 0.5);
        var left = TrailedOpenTurn.Build(new Vec3(0, 0, 0), 0, false, shape, 0.5);
        Assert.That(left.Count, Is.EqualTo(right.Count));
        Assert.Multiple(() =>
        {
            for (int i = 0; i < right.Count; i++)
            {
                Assert.That(left[i].Easting, Is.EqualTo(-right[i].Easting).Within(1e-9));
                Assert.That(left[i].Northing, Is.EqualTo(right[i].Northing).Within(1e-9));
            }
            Assert.That(left[^1].Easting, Is.EqualTo(-9.0).Within(1e-6));
        });
    }

    [Test]
    public void The_shape_follows_the_pass_s_own_heading_and_place()
    {
        var shape = TrailedOpenTurn.Solve(Drill, Radius, 9.0)!;
        double heading = 40 * Math.PI / 180;
        var start = new Vec3(120, -35, heading);
        var points = TrailedOpenTurn.Build(start, heading, true, shape, 0.5);
        // The end: 9 m to the right of the pass, the counter-arc's run back along it.
        double dE = points[^1].Easting - start.Easting, dN = points[^1].Northing - start.Northing;
        double across = dE * Math.Cos(heading) - dN * Math.Sin(heading);
        double along = dE * Math.Sin(heading) + dN * Math.Cos(heading);
        Assert.Multiple(() =>
        {
            Assert.That(points[0].Easting, Is.EqualTo(start.Easting));
            Assert.That(across, Is.EqualTo(9.0).Within(1e-6));
            Assert.That(along, Is.EqualTo(-shape.RunsOn).Within(1e-6));
            Assert.That(Math.Abs(Wrap(points[^1].Heading - (heading + Math.PI))), Is.LessThan(1e-6));
        });
    }
}
