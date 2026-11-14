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
            Assert.That(Math.Abs(EastingAt(line, 95)), Is.LessThan(0.01), "15 m past the bend, back on the pass");
            Assert.That(Math.Abs(EastingAt(line, 180)), Is.LessThan(1e-9), "and on it along the stretch");
            Assert.That(line[^1].Northing, Is.EqualTo(280), "to the pass's end");
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
