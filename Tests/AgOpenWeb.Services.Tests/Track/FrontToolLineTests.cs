// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Services.Track;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.Track;

/// <summary>
/// The line a front-mounted implement's pivot steers round a bend (<see cref="FrontToolLine"/>;
/// fork only): an implement 4.5 m ahead of the pivot runs outside the pivot's line by
/// √(R² + h²) − R, 0.5 m on a 20 m bend.
/// </summary>
[TestFixture]
public class FrontToolLineTests
{
    private const double Reach = 4.5;
    private const double MinRadius = 4.6;

    // A circle driven clockwise (right turn), a point a metre.
    private static List<Vec3> Circle(double r)
    {
        int n = (int)(2 * Math.PI * r);
        return Enumerable.Range(0, n).Select(i =>
        {
            double a = 2 * Math.PI * i / n;
            return new Vec3(-r * Math.Cos(a), r * Math.Sin(a), 0); // from the west point, going north
        }).ToList();
    }

    [TestCase(20)]
    [TestCase(8)]
    public void Round_a_circle_the_pivot_steers_the_circle_that_puts_the_implement_on_it(double r)
    {
        var line = FrontToolLine.Steered(Circle(r), closed: true, Reach, MinRadius);
        Assert.That(line, Is.Not.Null);
        double expected = Math.Sqrt(r * r - Reach * Reach);
        Assert.Multiple(() =>
        {
            foreach (var p in line!)
                Assert.That(Math.Sqrt(p.Easting * p.Easting + p.Northing * p.Northing), Is.EqualTo(expected).Within(0.02));
        });
        // The implement, 4.5 m ahead on the tangent of that circle, is on the pass.
        Assert.That(Math.Sqrt(expected * expected + Reach * Reach), Is.EqualTo(r).Within(1e-9));
    }

    [Test]
    public void A_left_turn_moves_the_line_to_the_left()
    {
        var anticlockwise = Circle(20);
        anticlockwise.Reverse();
        var line = FrontToolLine.Steered(anticlockwise, closed: true, Reach, MinRadius)!;
        Assert.That(line.All(p => Math.Sqrt(p.Easting * p.Easting + p.Northing * p.Northing) < 19.6), "towards the centre");
    }

    [Test]
    public void A_straight_line_is_steered_as_it_is()
    {
        var straight = Enumerable.Range(0, 100).Select(i => new Vec3(0, i, 0)).ToList();
        Assert.That(FrontToolLine.Steered(straight, closed: false, Reach, MinRadius), Is.Null);
    }

    [Test]
    public void Only_the_bend_moves_and_it_starts_as_the_implement_reaches_it()
    {
        // North 100 m, a quarter circle of 20 m to the right, then east 100 m.
        var pts = new List<Vec3>();
        for (int i = 0; i < 100; i++) pts.Add(new Vec3(0, i, 0));
        for (int i = 0; i <= 31; i++)
        {
            double a = Math.PI / 2 * i / 31;
            pts.Add(new Vec3(20 - 20 * Math.Cos(a), 100 + 20 * Math.Sin(a), 0));
        }
        for (int i = 1; i <= 100; i++) pts.Add(new Vec3(20 + i, 120, 0));

        var line = FrontToolLine.Steered(pts, closed: false, Reach, MinRadius)!;
        double Moved(int i) => GeometryMath.Distance(pts[i], line[i]);
        Assert.Multiple(() =>
        {
            Assert.That(Moved(50), Is.EqualTo(0).Within(1e-9), "the straight well before the bend");
            Assert.That(Moved(115), Is.EqualTo(20 - Math.Sqrt(400 - Reach * Reach)).Within(0.02), "on the bend");
            Assert.That(Moved(97), Is.GreaterThan(0), "just before it: the implement is on the bend already");
            Assert.That(Moved(200), Is.EqualTo(0).Within(1e-9), "the straight after it");
            Assert.That(line[115].Easting, Is.GreaterThan(pts[115].Easting), "towards the bend's centre");
        });
    }

    // The line's easting at a northing, between its points (a line running north).
    private static double EastingAt(IReadOnlyList<Vec3> line, double northing)
    {
        for (int i = 0; i + 1 < line.Count; i++)
        {
            var a = line[i];
            var b = line[i + 1];
            if ((a.Northing - northing) * (b.Northing - northing) > 0 || Math.Abs(b.Northing - a.Northing) < 1e-9) continue;
            double t = (northing - a.Northing) / (b.Northing - a.Northing);
            return a.Easting + (b.Easting - a.Easting) * t;
        }
        throw new ArgumentOutOfRangeException(nameof(northing));
    }

    [Test]
    public void After_a_bend_the_line_is_back_on_a_long_straight_stretch_within_a_few_metres()
    {
        // As a pass bent round a Go around tree: north on points a metre apart, a sideways bump of
        // 3 m over 30 m, then one straight stretch of 200 m, two points. The stretch's first point
        // moved with the bend and its far one did not: steered one for one, the line ran slanted
        // the whole 200 m (0.40 m beside the pass 100 m on, on a tester's field).
        var pts = new List<Vec3>();
        for (int i = 0; i < 50; i++) pts.Add(new Vec3(0, i, 0));
        for (int i = 0; i <= 30; i++) pts.Add(new Vec3(3 * (1 - Math.Cos(2 * Math.PI * i / 30)) / 2, 50 + i, 0));
        pts.Add(new Vec3(0, 280, 0));

        var line = FrontToolLine.Steered(pts, closed: false, Reach, MinRadius)!;
        Assert.That(line, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(Math.Abs(EastingAt(line, 65)), Is.GreaterThan(0.05), "the bend itself moves");
            // The pivot follows the implement as a trailer its hitch (FrontToolLine.TowedShifts):
            // back on the pass as e^(−s/4.5 m), 4 cm off 15 m past the bump.
            Assert.That(Math.Abs(EastingAt(line, 95)), Is.LessThan(0.05), "15 m past the bend, nearly back on the pass");
            Assert.That(Math.Abs(EastingAt(line, 120)), Is.LessThan(0.005), "40 m past it, back on the pass");
            Assert.That(Math.Abs(EastingAt(line, 180)), Is.LessThan(1e-9), "and on it along the stretch");
            Assert.That(line[^1].Northing, Is.EqualTo(280), "to the pass's end");
        });
    }

    // A pass north that steps right round a tree as a Go around bend does: out at 45° and back,
    // each bend an arc of the curve radius, the step 4 m. Points every 0.25 m.
    private static List<Vec3> GoAroundS(double radius, double step = 4.0)
    {
        var pts = new List<Vec3>();
        double e = 0, n = 0, h = 0; // heading clockwise from north
        void Straight(double len)
        {
            for (double s = 0; s < len; s += 0.25)
            {
                pts.Add(new Vec3(e, n, h));
                e += 0.25 * Math.Sin(h);
                n += 0.25 * Math.Cos(h);
            }
        }
        void Arc(double turn) // + right
        {
            double len = Math.Abs(turn) * radius;
            for (double s = 0; s < len; s += 0.25)
            {
                pts.Add(new Vec3(e, n, h));
                h += Math.Sign(turn) * 0.25 / radius;
                e += 0.25 * Math.Sin(h);
                n += 0.25 * Math.Cos(h);
            }
        }
        double q = Math.PI / 4;
        double slant = Math.Max(0, (step - 2 * radius * (1 - Math.Cos(q))) / Math.Sin(q));
        Straight(60); Arc(q); Straight(slant); Arc(-q); Straight(10); Arc(-q); Straight(slant); Arc(q); Straight(120);
        pts.Add(new Vec3(e, n, h));
        return pts;
    }

    private static double DistanceToLine(IReadOnlyList<Vec3> line, double e, double n)
    {
        double best = double.MaxValue;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            var a = line[i];
            var b = line[i + 1];
            double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing, l2 = dx * dx + dy * dy;
            double t = l2 > 1e-12 ? Math.Clamp(((e - a.Easting) * dx + (n - a.Northing) * dy) / l2, 0, 1) : 0;
            best = Math.Min(best, Math.Sqrt(Math.Pow(a.Easting + dx * t - e, 2) + Math.Pow(a.Northing + dy * t - n, 2)));
        }
        return best;
    }

    // The implement, reach ahead of a pivot driven exactly along the line steered (on it, facing
    // along it): the furthest it gets from the pass, along the stretch from 10 m to 200 m.
    private static double ImplementOffPass(List<Vec3> pass, List<Vec3> steered, double reach, bool forward)
    {
        var line = forward ? steered : Enumerable.Reverse(steered).ToList();
        double worst = 0;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            var a = line[i];
            var b = line[i + 1];
            double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing, l = Math.Sqrt(dx * dx + dy * dy);
            if (l < 1e-9) continue;
            // The pivot half way along the stretch, where the stretch is the line's tangent.
            double ie = (a.Easting + b.Easting) / 2 + dx / l * reach, inn = (a.Northing + b.Northing) / 2 + dy / l * reach;
            if (inn < 10 || inn > 200) continue;
            worst = Math.Max(worst, DistanceToLine(pass, ie, inn));
        }
        return worst;
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Round_a_go_around_tree_the_implement_stays_on_the_bend(bool forward)
    {
        // The farm job harness on a tester's field: the Maxxum's boom 4.8 m ahead on a 6 m Go
        // around bend. Measured over a window either side, the S out of the pass and back
        // averaged to nearly straight and the line was hardly moved: the boom cut the first bend
        // by 1.5 m and ran 0.85 m over a 3 m tree. Steered as the boom's pull draws it, the boom
        // runs on the bend, whichever way the pass is driven.
        const double reach = 4.8, minRadius = 3.0, radius = 6.0;
        var pass = GoAroundS(radius);
        var steered = FrontToolLine.Steered(pass, closed: false, reach, minRadius, forward);
        Assert.That(steered, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(ImplementOffPass(pass, steered!, reach, forward), Is.LessThan(0.03), "the implement on the bend");
            Assert.That(ImplementOffPass(pass, pass, reach, forward), Is.GreaterThan(1.0), "steered on the pass itself, it cuts the bends");
        });
    }

    [Test]
    public void A_closed_pass_keeps_its_points_one_for_one()
    {
        // PassUnfit marks a closed pass's points by index.
        var square = new List<Vec3>();
        for (int i = 0; i < 4; i++)
        {
            double a = Math.PI / 2 * i;
            square.Add(new Vec3(50 * Math.Sin(a), 50 * Math.Cos(a), 0));
        }
        var circle = Circle(20);
        Assert.That(FrontToolLine.Steered(circle, closed: true, Reach, MinRadius)!.Count, Is.EqualTo(circle.Count));
        var steered = FrontToolLine.Steered(square, closed: true, Reach, MinRadius);
        Assert.That(steered == null || steered.Count == square.Count);
    }

    [Test]
    public void Never_tighter_than_the_tractor_turns()
    {
        // A 5 m circle: the pivot would need 0.6 m (√(25 − 20.25) = 2.2 m); it is held at its 4.6 m.
        var line = FrontToolLine.Steered(Circle(5), closed: true, Reach, MinRadius)!;
        foreach (var p in line)
            Assert.That(Math.Sqrt(p.Easting * p.Easting + p.Northing * p.Northing), Is.GreaterThanOrEqualTo(MinRadius - 0.05));
    }
}
