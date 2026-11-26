// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Tool;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// The pivot's path while a front implement's centre drives a path planned for it
/// (<see cref="RigGeometry.PivotPathBehind"/>): the pivot follows the implement as a trailer
/// follows its hitch. Fork only.
/// </summary>
[TestFixture]
public class PivotPathBehindTests
{
    private const double Reach = 4.7;   // an 8 m front boom on a tester's tractor
    private const double Radius = 5.4;  // his U-turn radius
    private static readonly double ToolRadius = Math.Sqrt(Radius * Radius + Reach * Reach); // 7.16 m

    /// <summary>North along E = 0 for <paramref name="lead"/> m, half a circle to the right of the
    /// implement's radius, then south for <paramref name="tail"/> m: a U-turn as the implement drives it.</summary>
    private static List<Vec3> UTurn(double lead, double tail, double step = 0.25)
    {
        var path = new List<Vec3>();
        for (double s = -lead; s < 0; s += step) path.Add(new Vec3(0, s, 0));
        int arc = (int)Math.Ceiling(Math.PI * ToolRadius / step);
        for (int i = 0; i <= arc; i++)
        {
            double a = Math.PI * i / arc;
            path.Add(new Vec3(ToolRadius - ToolRadius * Math.Cos(a), ToolRadius * Math.Sin(a), a));
        }
        for (double s = step; s <= tail; s += step) path.Add(new Vec3(2 * ToolRadius, -s, Math.PI));
        return path;
    }

    [Test]
    public void On_a_straight_path_the_pivot_is_the_same_path_further_back()
    {
        var path = Enumerable.Range(0, 20).Select(i => new Vec3(3, i * 0.5, 0)).ToList();
        var pivot = RigGeometry.PivotPathBehind(path, Reach);

        Assert.That(pivot, Has.Count.EqualTo(path.Count));
        for (int i = 0; i < path.Count; i++)
        {
            Assert.That(pivot[i].Easting, Is.EqualTo(3).Within(1e-9));
            Assert.That(pivot[i].Northing, Is.EqualTo(path[i].Northing - Reach).Within(1e-9));
            Assert.That(pivot[i].Heading, Is.EqualTo(0).Within(1e-9));
        }
    }

    [Test]
    public void The_pivot_is_always_the_reach_behind_the_point_and_heads_for_it()
    {
        var path = UTurn(10, 20);
        var pivot = RigGeometry.PivotPathBehind(path, Reach);

        for (int i = 0; i < path.Count; i++)
        {
            Assert.That(GeometryMath.Distance(pivot[i], path[i]), Is.EqualTo(Reach).Within(1e-6));
            Assert.That(pivot[i].Easting + Math.Sin(pivot[i].Heading) * Reach, Is.EqualTo(path[i].Easting).Within(1e-6));
            Assert.That(pivot[i].Northing + Math.Cos(pivot[i].Heading) * Reach, Is.EqualTo(path[i].Northing).Within(1e-6));
        }
    }

    [Test]
    public void Round_a_long_bend_the_pivot_settles_on_the_tractor_s_own_circle()
    {
        // Twice round a circle of the implement's radius about (ToolRadius, 0).
        var path = new List<Vec3>();
        for (int d = 0; d <= 720; d++)
        {
            double a = d * Math.PI / 180;
            path.Add(new Vec3(ToolRadius - ToolRadius * Math.Cos(a), ToolRadius * Math.Sin(a), a));
        }
        var pivot = RigGeometry.PivotPathBehind(path, Reach);

        // The implement on a 7.16 m circle, the pivot 4.7 m behind it on a 5.4 m one.
        foreach (var p in pivot.Skip(360))
            Assert.That(Math.Sqrt(Math.Pow(p.Easting - ToolRadius, 2) + p.Northing * p.Northing), Is.EqualTo(Radius).Within(0.02));
    }

    [Test]
    public void The_pivot_never_turns_tighter_than_the_tractor_s_circle()
    {
        var pivot = RigGeometry.PivotPathBehind(UTurn(10, 20), Reach);

        // The turn between points 1 m apart along the pivot's path.
        double tightest = double.MaxValue;
        var at = new List<(double S, double H)>();
        double s = 0;
        for (int i = 0; i < pivot.Count; i++)
        {
            if (i > 0) s += GeometryMath.Distance(pivot[i - 1], pivot[i]);
            at.Add((s, pivot[i].Heading));
        }
        for (int i = 0, j = 0; i < at.Count; i++)
        {
            while (j < at.Count - 1 && at[j].S - at[i].S < 1.0) j++;
            double run = at[j].S - at[i].S;
            if (run < 1.0) break;
            double turn = Math.Abs(Math.IEEERemainder(at[j].H - at[i].H, 2 * Math.PI));
            if (turn > 1e-9) tightest = Math.Min(tightest, run / turn);
        }
        Assert.That(tightest, Is.GreaterThan(Radius - 0.05), "the pivot's tightest bend, m");
    }

    [Test]
    public void The_pivot_leaves_the_pass_as_the_point_starts_its_bend_and_inside_it()
    {
        var path = UTurn(10, 20);
        var pivot = RigGeometry.PivotPathBehind(path, Reach);

        // On the pass, the reach behind, up to where the implement's bend starts.
        int bend = path.FindIndex(p => p.Northing >= 0);
        for (int i = 0; i <= bend; i++)
        {
            Assert.That(pivot[i].Easting, Is.EqualTo(0).Within(1e-6));
            Assert.That(pivot[i].Northing, Is.EqualTo(path[i].Northing - Reach).Within(1e-6));
        }
        // Then inside the implement's half circle all the way round.
        foreach (var p in pivot.Skip(bend + 1).Where(p => p.Northing > 0.5))
            Assert.That(Math.Sqrt(Math.Pow(p.Easting - ToolRadius, 2) + p.Northing * p.Northing), Is.LessThan(ToolRadius));
    }

    [Test]
    public void After_the_bend_the_pivot_comes_onto_the_next_pass_within_a_few_reaches()
    {
        var path = UTurn(10, 25);
        var pivot = RigGeometry.PivotPathBehind(path, Reach);
        double nextPass = 2 * ToolRadius;

        // The implement is on the next pass from the end of its half circle; the pivot is then
        // still inside the turn, the tractor 41 degrees off the pass.
        int landed = path.FindLastIndex(p => p.Northing >= 0);
        Assert.That(nextPass - pivot[landed].Easting, Is.EqualTo(Reach * Reach / ToolRadius).Within(0.1));

        double OffAt(double metresOn) => nextPass - pivot[path.FindIndex(landed, p => p.Northing <= -metresOn)].Easting;
        double SkewAt(double metresOn) => Math.Abs(Math.IEEERemainder(
            pivot[path.FindIndex(landed, p => p.Northing <= -metresOn)].Heading - Math.PI, 2 * Math.PI)) * 180 / Math.PI;
        Assert.Multiple(() =>
        {
            Assert.That(SkewAt(Reach), Is.InRange(13, 18), "skew a reach on, degrees");
            Assert.That(SkewAt(2 * Reach), Is.InRange(4.5, 7), "skew two reaches on, degrees");
            Assert.That(SkewAt(3 * Reach), Is.LessThan(2.5), "skew three reaches on, degrees");
            Assert.That(OffAt(3 * Reach), Is.InRange(0, 0.20), "the pivot beside the pass three reaches on, m");
            Assert.That(OffAt(20), Is.InRange(0, 0.06), "the pivot beside the pass 20 m on, m");
        });
    }

    [Test]
    public void The_point_ahead_of_the_pivot_s_path_is_the_path_it_was_made_for()
    {
        var path = UTurn(10, 20);
        var back = RigGeometry.PathOfPointAhead(RigGeometry.PivotPathBehind(path, Reach), Reach);

        // PathOfPointAhead reads the tractor's heading from the pivot's own path, point to point.
        for (int i = 1; i < path.Count - 1; i++)
            Assert.That(GeometryMath.Distance(back[i], path[i]), Is.LessThan(0.05), $"point {i}");
    }

    [Test]
    public void No_reach_gives_the_path_back()
    {
        var path = UTurn(5, 5);
        Assert.That(RigGeometry.PivotPathBehind(path, 0), Is.EqualTo(path));
        Assert.That(RigGeometry.PivotPathBehind(new List<Vec3>(), Reach), Is.Empty);
    }
}
