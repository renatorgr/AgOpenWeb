// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Headland;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// A headland wide only where the passes end (<see cref="HeadlandAlongPasses"/>, fork only): at
/// least the ends' width of field ahead and behind along the passes, at least the sides' width
/// from the boundary any way. A tester's fields are irregular: one width all round lost crop at
/// the sides, and lines drawn per edge cut off half a field at a step in its boundary.
/// </summary>
[TestFixture]
public class HeadlandAlongPassesTests
{
    private const double North = 0;
    private const double East = Math.PI / 2;

    private static List<Vec3> Ring(params (double e, double n)[] pts) =>
        pts.Select(p => new Vec3(p.e, p.n, 0)).ToList();

    private static double SignedArea(IReadOnlyList<Vec3> ring)
    {
        double s = 0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            s += ring[j].Easting * ring[i].Northing - ring[i].Easting * ring[j].Northing;
        return s / 2;
    }

    private static double Area(IReadOnlyList<Vec3> ring) => Math.Abs(SignedArea(ring));

    private static double DistToSegment(Vec3 p, Vec3 a, Vec3 b)
    {
        double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing, l2 = dx * dx + dy * dy;
        double t = l2 > 0 ? Math.Clamp(((p.Easting - a.Easting) * dx + (p.Northing - a.Northing) * dy) / l2, 0, 1) : 0;
        return Math.Sqrt(Math.Pow(a.Easting + t * dx - p.Easting, 2) + Math.Pow(a.Northing + t * dy - p.Northing, 2));
    }

    private static double DistToBoundary(Vec3 p, IReadOnlyList<Vec3> ring)
    {
        double d = double.MaxValue;
        for (int i = 0; i < ring.Count; i++) d = Math.Min(d, DistToSegment(p, ring[i], ring[(i + 1) % ring.Count]));
        return d;
    }

    /// <summary>How far from <paramref name="p"/> the boundary is, going one way along <paramref name="heading"/>.</summary>
    private static double Ray(Vec3 p, double heading, IReadOnlyList<Vec3> ring)
    {
        double de = Math.Sin(heading), dn = Math.Cos(heading), best = double.PositiveInfinity;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            double ee = b.Easting - a.Easting, en = b.Northing - a.Northing;
            double we = a.Easting - p.Easting, wn = a.Northing - p.Northing;
            double den = de * en - dn * ee;
            if (Math.Abs(den) < 1e-12) continue;
            double t = (we * en - wn * ee) / den, s = (we * dn - wn * de) / den;
            if (t > 0 && s >= 0 && s <= 1) best = Math.Min(best, t);
        }
        return best;
    }

    /// <summary>The line's points every half metre (its corners are few: straight stretches are thinned to their ends).</summary>
    private static List<Vec3> Samples(IReadOnlyList<Vec3> line)
    {
        var pts = new List<Vec3>();
        for (int i = 0; i < line.Count; i++)
        {
            var a = line[i];
            var b = line[(i + 1) % line.Count];
            double len = Math.Sqrt(Math.Pow(b.Easting - a.Easting, 2) + Math.Pow(b.Northing - a.Northing, 2));
            int steps = Math.Max(1, (int)Math.Ceiling(len / 0.5));
            for (int k = 0; k < steps; k++)
            {
                double t = (double)k / steps;
                pts.Add(new Vec3(a.Easting + t * (b.Easting - a.Easting), a.Northing + t * (b.Northing - a.Northing), 0));
            }
        }
        return pts;
    }

    /// <summary>The line walked every half metre: inside the field and the sides' width from the
    /// boundary everywhere; the room along the passes kept nearly everywhere (it jumps where a pass
    /// just grazes a corner of the boundary, and the grid's columns fall either side of it); and
    /// nearly all of it held by one of the two widths (a line further in than the rule asks is
    /// crop lost).</summary>
    private static void AssertOnTheRule(IReadOnlyList<Vec3> ring, IReadOnlyList<Vec3> line, double heading, double ends, double sides)
    {
        Assert.That(line, Has.Count.GreaterThanOrEqualTo(4), "a headland line");
        int samples = 0, held = 0, short_ = 0;
        foreach (var p in Samples(line))
        {
            samples++;
            Assert.That(GeometryMath.IsPointInPolygon(ring, p), Is.True, $"({p.Easting:0.0}, {p.Northing:0.0}) inside the field");
            double side = DistToBoundary(p, ring);
            Assert.That(side, Is.GreaterThanOrEqualTo(sides - 0.3), $"distance from the boundary at ({p.Easting:0.0}, {p.Northing:0.0})");
            double along = Math.Min(Ray(p, heading, ring), Ray(p, heading + Math.PI, ring));
            if (along < ends - 0.3) short_++;
            if (Math.Abs(along - ends) <= 0.5 || Math.Abs(side - sides) <= 0.5) held++;
        }
        Assert.That(short_, Is.LessThanOrEqualTo(0.03 * samples), "the room along the passes kept");
        Assert.That(held, Is.GreaterThanOrEqualTo(0.9 * samples), "nearly all of it on the ends' or the sides' width");
    }

    private static (double minE, double maxE, double minN, double maxN) Box(IReadOnlyList<Vec3> line) =>
        (line.Min(p => p.Easting), line.Max(p => p.Easting), line.Min(p => p.Northing), line.Max(p => p.Northing));

    [Test]
    public void A_rectangle_gets_the_ends_width_where_the_passes_end_and_the_sides_width_beside_them()
    {
        var field = Ring((0, 0), (100, 0), (100, 200), (0, 200)); // 100 m across the passes, 200 m along
        var line = HeadlandAlongPasses.Build(field, North, 24, 8);

        AssertOnTheRule(field, line, North, 24, 8);
        var (minE, maxE, minN, maxN) = Box(line);
        Assert.Multiple(() =>
        {
            Assert.That(minE, Is.EqualTo(8).Within(0.3), "west side 8 m");
            Assert.That(maxE, Is.EqualTo(92).Within(0.3), "east side 8 m");
            Assert.That(minN, Is.EqualTo(24).Within(0.3), "south end 24 m");
            Assert.That(maxN, Is.EqualTo(176).Within(0.3), "north end 24 m");
            Assert.That(Area(line), Is.EqualTo(84 * 152).Within(0.5).Percent);
        });
    }

    [Test]
    public void Passes_across_the_field_put_the_wide_headland_on_the_other_edges()
    {
        var field = Ring((0, 0), (100, 0), (100, 200), (0, 200));
        var line = HeadlandAlongPasses.Build(field, East, 24, 8);

        AssertOnTheRule(field, line, East, 24, 8);
        var (minE, maxE, minN, maxN) = Box(line);
        Assert.Multiple(() =>
        {
            Assert.That(minE, Is.EqualTo(24).Within(0.3));
            Assert.That(maxE, Is.EqualTo(76).Within(0.3));
            Assert.That(minN, Is.EqualTo(8).Within(0.3));
            Assert.That(maxN, Is.EqualTo(192).Within(0.3));
        });
    }

    [Test]
    public void Either_way_along_the_passes_gives_the_same_headland()
    {
        var field = Ring((0, 0), (120, 10), (150, 180), (20, 230), (-10, 90));
        double heading = 0.3;
        var one = HeadlandAlongPasses.Build(field, heading, 24, 8);
        var other = HeadlandAlongPasses.Build(field, heading + Math.PI, 24, 8);
        Assert.That(Area(other), Is.EqualTo(Area(one)).Within(0.3).Percent);
    }

    [Test]
    public void A_slanted_end_gets_the_ends_width_times_the_sine_of_its_angle_to_the_passes()
    {
        // The south edge runs at 30 deg to east, 60 deg to the passes (north): the room along the
        // passes is 24 m where the line is 24 sin 60 = 20.8 m square from the edge.
        double tan30 = Math.Tan(Math.PI / 6);
        var field = Ring((0, 0), (100, 100 * tan30), (100, 300), (0, 300));
        var line = HeadlandAlongPasses.Build(field, North, 24, 8);

        AssertOnTheRule(field, line, North, 24, 8);
        var a = field[0];
        var b = field[1];
        var nearSlant = Samples(line).Where(p => p.Easting > 20 && p.Easting < 80 && p.Northing < 100).ToList();
        Assert.That(nearSlant, Is.Not.Empty);
        foreach (var p in nearSlant)
            Assert.That(DistToSegment(p, a, b), Is.EqualTo(24 * Math.Sin(Math.PI / 3)).Within(0.3),
                $"({p.Easting:0.0}, {p.Northing:0.0}) square from the slanted end");
    }

    [Test]
    public void An_edge_nearly_along_the_passes_keeps_the_sides_width()
    {
        // The east edge leans 10 deg off the passes: 24 sin 10 = 4.2 m, under the sides' 8 m.
        double lean = Math.Tan(10 * Math.PI / 180) * 300;
        var field = Ring((0, 0), (100, 0), (100 + lean, 300), (0, 300));
        var line = HeadlandAlongPasses.Build(field, North, 24, 8);

        AssertOnTheRule(field, line, North, 24, 8);
        var a = field[1];
        var b = field[2];
        var nearLean = Samples(line).Where(p => p.Northing > 60 && p.Northing < 240 && p.Easting > 60).ToList();
        Assert.That(nearLean, Is.Not.Empty);
        foreach (var p in nearLean)
            Assert.That(DistToSegment(p, a, b), Is.EqualTo(8).Within(0.3));
    }

    [Test]
    public void A_step_in_the_boundary_cuts_no_piece_of_the_field_off()
    {
        // The east half ends 100 m short of the west half. Lines drawn per end run straight on
        // past the step and cut the west half's last 100 m off; along the passes it keeps them.
        var field = Ring((0, 0), (100, 0), (100, 200), (50, 200), (50, 300), (0, 300));
        var line = HeadlandAlongPasses.Build(field, North, 24, 8);

        AssertOnTheRule(field, line, North, 24, 8);
        Assert.Multiple(() =>
        {
            Assert.That(GeometryMath.IsPointInPolygon(line, new Vec2(25, 260)), Is.True, "the west half's far end is crop");
            Assert.That(GeometryMath.IsPointInPolygon(line, new Vec2(75, 170)), Is.True, "the east half up to 24 m from its end is crop");
            Assert.That(GeometryMath.IsPointInPolygon(line, new Vec2(75, 185)), Is.False, "the east half's end is headland");
            Assert.That(Area(line), Is.EqualTo(84 * 152 + 34 * 100).Within(1).Percent);
        });
    }

    [Test]
    public void A_spike_too_narrow_for_a_turn_is_all_headland()
    {
        // A 10 m spike sticking out 50 m east, square to the passes: 5 m of room along them.
        var field = Ring((0, 0), (100, 0), (100, 100), (150, 100), (150, 110), (100, 110), (100, 200), (0, 200));
        var line = HeadlandAlongPasses.Build(field, North, 24, 8);

        AssertOnTheRule(field, line, North, 24, 8);
        // Beside the spike's mouth the crop reaches 6.2 m from the field's edge (8 m from the corners).
        Assert.That(line.Max(p => p.Easting), Is.LessThan(94), "nothing of the spike is crop");
    }

    [Test]
    public void A_field_too_small_for_the_ends_leaves_nothing()
    {
        var field = Ring((0, 0), (40, 0), (40, 40), (0, 40));
        Assert.That(HeadlandAlongPasses.Build(field, North, 24, 8), Is.Empty);
    }

    [Test]
    public void The_line_turns_the_way_the_boundary_turns()
    {
        var anticlockwise = Ring((0, 0), (100, 0), (100, 200), (0, 200));
        var clockwise = Enumerable.Reverse(anticlockwise).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(SignedArea(HeadlandAlongPasses.Build(anticlockwise, North, 24, 8)), Is.GreaterThan(0));
            Assert.That(SignedArea(HeadlandAlongPasses.Build(clockwise, North, 24, 8)), Is.LessThan(0));
        });
    }

    [Test]
    public void The_first_point_repeated_at_the_end_changes_nothing()
    {
        var open = Ring((0, 0), (120, 10), (150, 180), (20, 230), (-10, 90));
        var closed = new List<Vec3>(open) { open[0] };
        var a = HeadlandAlongPasses.Build(open, 0.3, 24, 8);
        var b = HeadlandAlongPasses.Build(closed, 0.3, 24, 8);
        Assert.That(b, Has.Count.EqualTo(a.Count));
        Assert.That(Area(b), Is.EqualTo(Area(a)).Within(1e-6).Percent);
    }

    [Test]
    public void An_irregular_field_keeps_both_widths_everywhere()
    {
        var field = Ring((0, 0), (60, -15), (140, 5), (175, 70), (150, 160), (165, 240), (90, 260), (30, 210), (-20, 120));
        double heading = 20 * Math.PI / 180;
        var line = HeadlandAlongPasses.Build(field, heading, 24, 8);
        AssertOnTheRule(field, line, heading, 24, 8);
    }

    [Test]
    public void A_large_field_takes_a_coarser_grid_and_keeps_the_rule()
    {
        var field = Ring((0, 0), (3000, 0), (3000, 2000), (0, 2000));
        var line = HeadlandAlongPasses.Build(field, North, 36, 12);
        var (minE, maxE, minN, maxN) = Box(line);
        Assert.Multiple(() =>
        {
            Assert.That(minE, Is.EqualTo(12).Within(1));
            Assert.That(maxE, Is.EqualTo(2988).Within(1));
            Assert.That(minN, Is.EqualTo(36).Within(1));
            Assert.That(maxN, Is.EqualTo(1964).Within(1));
        });
    }

    [Test]
    public void Distances_that_are_not_numbers_or_below_zero_leave_nothing()
    {
        var field = Ring((0, 0), (100, 0), (100, 200), (0, 200));
        Assert.Multiple(() =>
        {
            Assert.That(HeadlandAlongPasses.Build(field, North, double.NaN, 8), Is.Empty);
            Assert.That(HeadlandAlongPasses.Build(field, North, 24, -1), Is.Empty);
            Assert.That(HeadlandAlongPasses.Build(field, double.PositiveInfinity, 24, 8), Is.Empty);
            Assert.That(HeadlandAlongPasses.Build(Ring((0, 0), (1, 1)), North, 24, 8), Is.Empty);
        });
    }
}
