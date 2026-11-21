// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Services.Pipeline;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.Pipeline;

/// <summary>
/// With no U-turn planned or driven, an end of the implement heading out across a hard outer
/// boundary takes AutoSteer off (<see cref="HardFenceGuard"/>): AutoSteer joining a line in a
/// corner of the field swung an 8 m front boom 0.9 m over the fence. Driving along the fence,
/// back into the field or standing still is not heading out.
/// </summary>
[TestFixture]
public class HardFenceGuardTests
{
    // A fence 100 x 100 m round the origin; an 8 m implement (4 m either side of its centre).
    private static readonly BoundaryPolygon Fence = new()
    {
        Points = new[] { (-50.0, -50.0), (50.0, -50.0), (50.0, 50.0), (-50.0, 50.0) }
            .Select(p => new BoundaryPoint(p.Item1, p.Item2, 0)).ToList(),
        IsHard = true,
    };

    private const double Reach = 4.0;
    private static double Rad(double deg) => deg * Math.PI / 180;

    [Test]
    public void An_end_going_out_across_the_fence_counts()
    {
        // Heading north-east at 45° near the east fence: the right end is 0.8 m inside it now and
        // 1.8 m out after 4 m of travel.
        double h = Rad(45);
        var centre = new Vec3(50 - 0.8 - Reach * Math.Cos(h), 0, h);
        double outBy = HardFenceGuard.HeadingOut(Fence, centre, h, Reach, 4.0);
        Assert.That(outBy, Is.EqualTo(4.0 * Math.Sin(h) - 0.8).Within(0.05));
    }

    [Test]
    public void Driving_along_the_fence_a_little_over_it_does_not_count()
    {
        // Heading north along the east fence with the right end 0.3 m over it (a fence drawn on a
        // photo is a little off the ground) or 0.7 m over it: no further out ahead.
        foreach (double over in new[] { 0.3, 0.7 })
        {
            var centre = new Vec3(50 + over - Reach, 0, 0);
            Assert.That(HardFenceGuard.HeadingOut(Fence, centre, 0, Reach, 4.0), Is.EqualTo(0), $"{over} m over");
        }
    }

    [Test]
    public void Going_back_into_the_field_does_not_count()
    {
        // The right end 2 m outside the east fence, heading north-west back in.
        double h = Rad(-45);
        var centre = new Vec3(52 - Reach * Math.Cos(h), 0, h);
        Assert.That(HardFenceGuard.HeadingOut(Fence, centre, h, Reach, 4.0), Is.EqualTo(0));
    }

    [Test]
    public void Less_than_the_margin_out_does_not_count()
    {
        // Heading at 10° to the east fence, the right end just inside: 4 m on it is 0.4 m out.
        double h = Rad(10);
        var centre = new Vec3(50 - 0.3 - Reach * Math.Cos(h), 0, h);
        Assert.That(HardFenceGuard.HeadingOut(Fence, centre, h, Reach, 4.0), Is.EqualTo(0));
    }

    [Test]
    public void Standing_still_or_creeping_nothing_is_looked_at()
    {
        // Heading straight at the east fence with the right end on it: below walking pace the
        // look-ahead is shorter than MinLookAhead.
        double h = Rad(90);
        var centre = new Vec3(49.9, 0, h);
        Assert.That(HardFenceGuard.HeadingOut(Fence, centre, h, Reach, 0.5), Is.EqualTo(0));
        Assert.That(HardFenceGuard.HeadingOut(Fence, centre, h, Reach, 2.0), Is.GreaterThan(HardFenceGuard.Margin));
    }

    [Test]
    public void Far_inside_the_field_nothing_counts()
    {
        double h = Rad(90);
        Assert.That(HardFenceGuard.HeadingOut(Fence, new Vec3(0, 0, h), h, Reach, 10.0), Is.EqualTo(0));
    }

    // A round fence of 40 m radius round the origin.
    private static readonly BoundaryPolygon RoundFence = new()
    {
        Points = Enumerable.Range(0, 360)
            .Select(i => new BoundaryPoint(40 * Math.Sin(Rad(i)), 40 * Math.Cos(Rad(i)), 0)).ToList(),
        IsHard = true,
    };

    [Test]
    public void Round_a_bend_the_ends_follow_the_curve_steered()
    {
        // The first lap of a boundary curve, turning right round the field with the fence on the left:
        // the pivot heads north 35.6 m west of the centre, a front boom 4.5 m ahead, its left end 0.1 m
        // inside the fence. 4.4 m straight ahead (2 s at 8 km/h) that end is 0.7 m out, but the tractor
        // steering round the bend keeps it where it is (a tester's field: AutoSteer on the line was
        // taken off at a bend).
        double rp = Math.Sqrt(39.9 * 39.9 - 4.5 * 4.5) - Reach;
        var pivot = new Vec3(-rp, 0, 0);
        var centre = new Vec3(-rp, 4.5, 0);
        Assume.That(HardFenceGuard.HeadingOut(RoundFence, centre, 0, Reach, 4.4), Is.GreaterThan(HardFenceGuard.Margin),
            "straight ahead leaves the bend");
        Assert.That(HardFenceGuard.HeadingOut(RoundFence, centre, 0, Reach, 4.4, pivot, 1 / rp), Is.EqualTo(0));
    }

    [Test]
    public void Steering_out_across_the_fence_counts_before_the_heading_does()
    {
        // Heading north along the east fence, a front boom 4.5 m ahead with its right end 0.6 m inside
        // it, the tractor steering 15° right: straight ahead stays inside, the curve steered takes the
        // end 1.8 m out.
        const double wheelbase = 2.9;
        var pivot = new Vec3(50 - 0.6 - Reach, 0, 0);
        var centre = new Vec3(pivot.Easting, 4.5, 0);
        double k = HardFenceGuard.Curvature(15, wheelbase, 35);
        Assert.Multiple(() =>
        {
            Assert.That(HardFenceGuard.HeadingOut(Fence, centre, 0, Reach, 4.4), Is.EqualTo(0));
            Assert.That(HardFenceGuard.HeadingOut(Fence, centre, 0, Reach, 4.4, pivot, k), Is.EqualTo(1.8).Within(0.1));
            Assert.That(HardFenceGuard.HeadingOut(Fence, centre, 0, Reach, 4.4, pivot, -k), Is.EqualTo(0), "steering away");
        });
    }

    [Test]
    public void The_curvature_is_the_steer_angle_over_the_wheelbase()
    {
        Assert.Multiple(() =>
        {
            Assert.That(HardFenceGuard.Curvature(15, 2.9, 35), Is.EqualTo(Math.Tan(Rad(15)) / 2.9).Within(1e-12));
            Assert.That(HardFenceGuard.Curvature(-15, 2.9, 35), Is.EqualTo(-Math.Tan(Rad(15)) / 2.9).Within(1e-12), "left");
            Assert.That(HardFenceGuard.Curvature(80, 2.9, 35), Is.EqualTo(Math.Tan(Rad(35)) / 2.9).Within(1e-12), "within the maximum");
            Assert.That(HardFenceGuard.Curvature(15, 0, 35), Is.EqualTo(0), "no wheelbase");
            Assert.That(HardFenceGuard.Curvature(double.NaN, 2.9, 35), Is.EqualTo(0));
        });
    }

    // A tree 2 m square, 10 m north of the origin.
    private static BoundaryPolygon Tree(double e0, double n0, double e1, double n1) => new()
    {
        Points = new[] { (e0, n0), (e1, n0), (e1, n1), (e0, n1) }
            .Select(p => new BoundaryPoint(p.Item1, p.Item2, 0)).ToList(),
        IsHard = true,
    };

    [Test]
    public void A_tree_narrower_than_the_implement_between_its_ends_counts()
    {
        // The ends go by 3 m either side of it; the middle of the implement is 1 m in after 5 m.
        var tree = Tree(-1, 10, 1, 12);
        Assert.That(HardFenceGuard.HeadingIn(tree, new Vec3(0, 6, 0), 0, Reach, 5.0, default, 0), Is.EqualTo(1.0).Within(0.05));
    }

    [Test]
    public void Going_by_beside_an_obstacle_or_brushing_it_does_not_count()
    {
        var tree = Tree(-1, 10, 1, 12);
        Assert.Multiple(() =>
        {
            Assert.That(HardFenceGuard.HeadingIn(tree, new Vec3(6, 6, 0), 0, Reach, 5.0, default, 0), Is.EqualTo(0), "1 m beside it");
            // The right end 0.2 m inside its west edge: less than the margin.
            Assert.That(HardFenceGuard.HeadingIn(tree, new Vec3(-0.8 - Reach, 6, 0), 0, Reach, 5.0, default, 0), Is.EqualTo(0), "brushing it");
        });
    }

    [Test]
    public void Already_in_an_obstacle_and_going_no_further_in_does_not_count()
    {
        // The middle of the implement at the tree's centre, driving out of it.
        var tree = Tree(-1, 10, 1, 12);
        Assert.That(HardFenceGuard.HeadingIn(tree, new Vec3(0, 11, 0), 0, Reach, 5.0, default, 0), Is.EqualTo(0));
    }

    [Test]
    public void Creeping_no_obstacle_is_looked_at()
    {
        var tree = Tree(-1, 10, 1, 12);
        Assert.That(HardFenceGuard.HeadingIn(tree, new Vec3(0, 9.5, 0), 0, Reach, 0.5, default, 0), Is.EqualTo(0));
    }

    [Test]
    public void Steering_into_an_obstacle_counts_and_steering_away_does_not()
    {
        // A tree half a metre right of the implement's right end and 3 m on: straight ahead the end
        // goes by it; turning right on a 10 m radius the end is 0.9 m in after 7 m.
        var tree = Tree(4.5, 3, 6.5, 5);
        var pivot = new Vec3(0, 0, 0);
        Assert.Multiple(() =>
        {
            Assert.That(HardFenceGuard.HeadingIn(tree, pivot, 0, Reach, 8.0, pivot, 0), Is.EqualTo(0), "straight");
            Assert.That(HardFenceGuard.HeadingIn(tree, pivot, 0, Reach, 8.0, pivot, 0.1), Is.EqualTo(0.87).Within(0.05), "right");
            Assert.That(HardFenceGuard.HeadingIn(tree, pivot, 0, Reach, 8.0, pivot, -0.1), Is.EqualTo(0), "left");
        });
    }

    [Test]
    public void In_is_how_deep_the_implement_is_in_an_obstacle_now()
    {
        // A strip along the passes, its east edge 2 m inside the implement's left end.
        var strip = Tree(-10, 0, 2 - Reach, 40);
        Assert.Multiple(() =>
        {
            Assert.That(HardFenceGuard.In(strip, new Vec3(0, 20, 0), 0, Reach), Is.EqualTo(2.0).Within(0.05), "going along it");
            Assert.That(HardFenceGuard.In(strip, new Vec3(0, -3, 0), 0, Reach), Is.EqualTo(0), "short of it");
            Assert.That(HardFenceGuard.In(strip, new Vec3(2.5, 20, 0), 0, Reach), Is.EqualTo(0), "half a metre beside it");
            // Turned square to the strip, the implement lies along the passes and its end is out of it.
            Assert.That(HardFenceGuard.In(strip, new Vec3(0, 20, Math.PI / 2), Math.PI / 2, Reach), Is.EqualTo(0), "turned away");
        });
    }

    [Test]
    public void In_sees_a_tree_between_the_implements_ends()
    {
        var tree = Tree(-1, 10, 1, 12);
        Assert.That(HardFenceGuard.In(tree, new Vec3(0, 11, 0), 0, Reach), Is.EqualTo(1.0).Within(0.05));
    }
}
