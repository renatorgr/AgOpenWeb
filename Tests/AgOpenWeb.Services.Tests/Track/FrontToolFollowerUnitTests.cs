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

/// <summary>The steer angle <see cref="FrontToolFollower"/> asks for on a line, and when it
/// leaves the steering to Pure Pursuit (fork only).</summary>
[TestFixture]
public class FrontToolFollowerUnitTests
{
    private const double Wheelbase = 2.9;
    private const double LookAhead = 4.5;

    // A circle of radius r round (0, 0), clockwise seen from above (a right-hand bend), a point every metre.
    private static List<Vec3> Circle(double r)
    {
        int n = (int)Math.Round(2 * Math.PI * r);
        return Enumerable.Range(0, n).Select(i =>
        {
            double a = 2 * Math.PI * i / n; // heading of the travel at this point
            return new Vec3(-r * Math.Cos(a), r * Math.Sin(a), a);
        }).ToList();
    }

    // On the circle at the point a quarter round, heading the way the points go (east).
    private static (double E, double N, double H) OnIt(double r) => (0, r, Math.PI / 2);

    [Test]
    public void On_a_bend_it_asks_for_the_bend()
    {
        const double r = 20;
        var line = Circle(r);
        var (e, n, h) = OnIt(r);
        var f = FrontToolFollower.Steer(line, true, line.Count / 4, true, e, n, h, LookAhead, 0, Wheelbase);
        Assert.That(f, Is.Not.Null);
        Assert.That(f!.Value.Weight, Is.EqualTo(1));
        Assert.That(f.Value.SteerDeg, Is.EqualTo(Math.Atan(Wheelbase / r) * 180 / Math.PI).Within(0.3), "right, as the bend");
    }

    [Test]
    public void Driven_the_other_way_the_same_bend_is_to_the_left()
    {
        const double r = 20;
        var line = Circle(r);
        var (e, n, _) = OnIt(r);
        var f = FrontToolFollower.Steer(line, true, line.Count / 4, false, e, n, -Math.PI / 2, LookAhead, 0, Wheelbase);
        Assert.That(f!.Value.SteerDeg, Is.EqualTo(-Math.Atan(Wheelbase / r) * 180 / Math.PI).Within(0.3));
    }

    [Test]
    public void Beside_the_line_it_steers_back_towards_it()
    {
        // A straight north; the pivot 0.3 m right of it, heading north: steer left.
        var line = Enumerable.Range(0, 50).Select(i => new Vec3(0, i, 0)).ToList();
        var f = FrontToolFollower.Steer(line, false, 20, true, 0.3, 20.5, 0, LookAhead, 2, Wheelbase);
        Assert.That(f!.Value.Weight, Is.EqualTo(1));
        Assert.That(f.Value.SteerDeg, Is.LessThan(0));
        // Heading 5° right of it, on it: steer left too.
        var g = FrontToolFollower.Steer(line, false, 20, true, 0, 20.5, 5 * Math.PI / 180, LookAhead, 2, Wheelbase);
        Assert.That(g!.Value.SteerDeg, Is.LessThan(0));
    }

    [Test]
    public void Away_from_the_line_Pure_Pursuit_steers_alone()
    {
        var line = Enumerable.Range(0, 50).Select(i => new Vec3(0, i, 0)).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(FrontToolFollower.Steer(line, false, 20, true, 2.0, 20.5, 0, LookAhead, 2, Wheelbase)!.Value.Weight,
                Is.EqualTo(0), "2 m off");
            Assert.That(FrontToolFollower.Steer(line, false, 20, true, 0, 20.5, 30 * Math.PI / 180, LookAhead, 2, Wheelbase)!.Value.Weight,
                Is.EqualTo(0), "30° off");
            Assert.That(FrontToolFollower.Steer(line, false, 20, true, 0.75, 20.5, 0, LookAhead, 2, Wheelbase)!.Value.Weight,
                Is.EqualTo(0.5).Within(1e-9), "half way in");
        });
    }

    [Test]
    public void A_bend_ahead_is_taken_as_the_wheels_answer_late()
    {
        // North 30 m, then a right bend of 20 m: 1 m before it, at 3 m/s, the bend comes 0.6 m nearer.
        var line = Enumerable.Range(0, 31).Select(i => new Vec3(0, i, 0)).ToList();
        for (int i = 1; i <= 20; i++)
        {
            double a = i / 20.0;
            line.Add(new Vec3(20 - 20 * Math.Cos(a), 30 + 20 * Math.Sin(a), a));
        }
        var standing = FrontToolFollower.Steer(line, false, 28, true, 0, 29.0, 0, LookAhead, 0, Wheelbase)!.Value;
        var moving = FrontToolFollower.Steer(line, false, 28, true, 0, 29.0, 0, LookAhead, 3, Wheelbase)!.Value;
        Assert.That(moving.SteerDeg, Is.GreaterThan(standing.SteerDeg));
    }

    [Test]
    public void No_line_no_answer()
    {
        Assert.That(FrontToolFollower.Steer(new List<Vec3> { new(0, 0, 0) }, false, 0, true, 0, 0, 0, LookAhead, 2, Wheelbase), Is.Null);
        var same = new List<Vec3> { new(0, 0, 0), new(0, 0, 0), new(0, 0, 0) };
        Assert.That(FrontToolFollower.Steer(same, false, 0, true, 0, 0, 0, LookAhead, 2, Wheelbase), Is.Null);
    }
}
