// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Services.Pipeline;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.Pipeline;

/// <summary>
/// The S onto a line beside a hard fence (<see cref="SideStep"/>; fork only). On a tester's field
/// (simulator) a lateral move onto the lap 4 m from the fence, with an 8 m boom 4.5 m ahead of the
/// pivot, took the boom's end across the fence until the fence guard stopped AutoSteer.
/// </summary>
[TestFixture]
public class SideStepTests
{
    // A field 100 m wide and 400 m long; the west fence at E -50.
    private static BoundaryPolygon Field(double northEnd = 200) => new()
    {
        Points = new List<BoundaryPoint>
        {
            new(-50, -200, 0), new(-50, northEnd, 0), new(50, northEnd, 0), new(50, -200, 0),
        },
        IsHard = true,
    };

    // The lap 4 m in from the west fence, driven north: an 8 m boom's west end runs on the fence.
    private static List<Vec3> Line(double e = -46) => new() { new(e, -700, 0), new(e, 700, 0) };

    private static readonly SideStepTool FrontBoom = new(4.5, 4);
    private static readonly SideStepTool RearBoom = new(-1.5, 4);
    private const double Speed = 8 / 3.6;
    private const double MinRadius = 8;

    private static Vec3 Tractor(double e = -38, double n = -100, double headingDeg = 0) =>
        new(e, n, headingDeg * Math.PI / 180);

    private static double LengthAlong(List<Vec3> path) => path[^1].Northing - path[0].Northing;

    /// <summary>The ends of the implement for the tractor at each point of the path.</summary>
    private static IEnumerable<(Vec3 Pose, Vec2 Left, Vec2 Right, Vec3 Centre)> Ends(List<Vec3> path, SideStepTool tool)
    {
        foreach (var p in path)
        {
            double fe = Math.Sin(p.Heading), fn = Math.Cos(p.Heading);
            double ce = p.Easting + fe * tool.Along, cn = p.Northing + fn * tool.Along;
            yield return (p, new Vec2(ce - fn * tool.Reach, cn + fe * tool.Reach),
                new Vec2(ce + fn * tool.Reach, cn - fe * tool.Reach), new Vec3(ce, cn, p.Heading));
        }
    }

    [Test]
    public void A_front_boom_steps_onto_the_lap_by_the_fence_without_crossing_it()
    {
        var fence = Field();
        var plan = SideStep.Plan(Tractor(), Speed, Line(), closed: false, fence, null, FrontBoom, MinRadius);

        Assert.That(plan.Outcome, Is.EqualTo(SideStepOutcome.Planned));
        var path = plan.Path!;
        double worst = Ends(path, FrontBoom).Max(x => Math.Max(
            HardFenceGuard.OutsideBy(fence, x.Left.Easting, x.Left.Northing),
            HardFenceGuard.OutsideBy(fence, x.Right.Easting, x.Right.Northing)));
        double guard = Ends(path, FrontBoom).Max(x =>
            HardFenceGuard.HeadingOut(fence, x.Centre, x.Pose.Heading, FrontBoom.Reach, HardFenceGuard.LookAheadSeconds * Speed));
        Assert.Multiple(() =>
        {
            Assert.That(path[0].Easting, Is.EqualTo(-38).Within(1e-9), "from the tractor");
            Assert.That(path[^1].Easting, Is.EqualTo(-46).Within(1e-6), "onto the lap");
            Assert.That(worst, Is.LessThanOrEqualTo(SideStep.Tolerance + 1e-9), "the boom's end keeps inside the fence");
            Assert.That(guard, Is.EqualTo(0), "the fence guard does not see it heading out across the fence");
            Assert.That(LengthAlong(path), Is.InRange(40.0, 90.0), "a long S: the boom swings out ahead of the tractor");
        });
    }

    [Test]
    public void A_rear_implement_takes_a_shorter_S()
    {
        var front = SideStep.Plan(Tractor(), Speed, Line(), false, Field(), null, FrontBoom, MinRadius);
        var rear = SideStep.Plan(Tractor(), Speed, Line(), false, Field(), null, RearBoom, MinRadius);
        Assert.That(rear.Outcome, Is.EqualTo(SideStepOutcome.Planned));
        TestContext.Out.WriteLine($"S front {LengthAlong(front.Path!):F0} m, rear {LengthAlong(rear.Path!):F0} m");
        Assert.That(LengthAlong(rear.Path!), Is.LessThan(LengthAlong(front.Path!)));
    }

    [Test]
    public void The_S_is_longer_at_speed()
    {
        var slow = SideStep.Plan(Tractor(), 8 / 3.6, Line(), false, Field(), null, FrontBoom, MinRadius);
        var fast = SideStep.Plan(Tractor(), 14 / 3.6, Line(), false, Field(), null, FrontBoom, MinRadius);
        TestContext.Out.WriteLine($"S at 8 km/h {LengthAlong(slow.Path!):F0} m, at 14 km/h {LengthAlong(fast.Path!):F0} m");
        Assert.That(LengthAlong(fast.Path!), Is.GreaterThan(LengthAlong(slow.Path!)));
    }

    [Test]
    public void A_line_away_from_the_fence_is_joined_as_before()
    {
        // From 16 m to 24 m in: the boom's ends stay 20 m and more from the fence.
        var plan = SideStep.Plan(Tractor(-34), Speed, Line(-26), false, Field(), null, FrontBoom, MinRadius);
        Assert.That(plan.Outcome, Is.EqualTo(SideStepOutcome.NotNeeded));
    }

    [Test]
    public void On_the_line_or_heading_across_it_nothing_is_planned()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SideStep.Plan(Tractor(-45.5), Speed, Line(), false, Field(), null, FrontBoom, MinRadius).Outcome,
                Is.EqualTo(SideStepOutcome.NotNeeded), "on the line");
            Assert.That(SideStep.Plan(Tractor(headingDeg: -80), Speed, Line(), false, Field(), null, FrontBoom, MinRadius).Outcome,
                Is.EqualTo(SideStepOutcome.NotNeeded), "heading across it");
        });
    }

    [Test]
    public void No_room_before_the_end_of_the_field_is_said()
    {
        // The north fence 25 m ahead: no S of the front boom fits before it.
        var plan = SideStep.Plan(Tractor(n: -100), Speed, Line(), false, Field(northEnd: -75), null, FrontBoom, MinRadius);
        Assert.That(plan.Outcome, Is.EqualTo(SideStepOutcome.NoRoom));
    }

    [Test]
    public void Near_an_obstacle_nothing_is_planned()
    {
        // A tree 30 m ahead, where the pass may bend round it.
        var tree = new BoundaryPolygon
        {
            Points = new List<BoundaryPoint> { new(-42, -72, 0), new(-42, -68, 0), new(-38, -68, 0), new(-38, -72, 0) },
        };
        var plan = SideStep.Plan(Tractor(), Speed, Line(), false, Field(), new[] { tree }, FrontBoom, MinRadius);
        Assert.That(plan.Outcome, Is.EqualTo(SideStepOutcome.NotNeeded));
    }

    [Test]
    public void A_steep_approach_is_turned_along_the_line_first()
    {
        // As on the tester's field: 9.5 m from the lap, heading 46 degrees towards it (and the fence).
        var fence = Field();
        var plan = SideStep.Plan(Tractor(-36.5, headingDeg: -46), Speed, Line(), false, fence, null, FrontBoom, MinRadius);
        Assert.That(plan.Outcome, Is.EqualTo(SideStepOutcome.Planned));
        var path = plan.Path!;
        double worst = Ends(path, FrontBoom).Max(x => Math.Max(
            HardFenceGuard.OutsideBy(fence, x.Left.Easting, x.Left.Northing),
            HardFenceGuard.OutsideBy(fence, x.Right.Easting, x.Right.Northing)));
        Assert.Multiple(() =>
        {
            Assert.That(worst, Is.LessThanOrEqualTo(SideStep.Tolerance + 1e-9), "the boom's end keeps inside the fence");
            Assert.That(path.Min(p => p.Easting), Is.GreaterThan(-46.3), "the tractor does not run past the lap");
            Assert.That(path[^1].Easting, Is.EqualTo(-46).Within(1e-6));
        });
    }

    [Test]
    public void The_S_starts_on_the_tractors_heading()
    {
        // Already turning 10 degrees towards the line: the S leaves on that heading.
        var plan = SideStep.Plan(Tractor(headingDeg: -10), Speed, Line(), false, Field(), null, FrontBoom, MinRadius);
        Assert.That(plan.Outcome, Is.EqualTo(SideStepOutcome.Planned));
        var p = plan.Path!;
        double h = Math.Atan2(p[2].Easting - p[1].Easting, p[2].Northing - p[1].Northing) * 180 / Math.PI;
        Assert.That(h, Is.EqualTo(-10).Within(1.5));
    }

    [Test]
    public void On_a_loop_the_S_runs_on_past_where_the_loop_was_started()
    {
        // The lap 4 m in, clockwise from its south-west corner; the tractor near its end, 8 m in.
        var lap = new List<Vec3>();
        void Edge(double e0, double n0, double e1, double n1)
        {
            int k = (int)(Math.Max(Math.Abs(e1 - e0), Math.Abs(n1 - n0)) / 2);
            for (int i = 0; i < k; i++) lap.Add(new Vec3(e0 + (e1 - e0) * i / k, n0 + (n1 - n0) * i / k, 0));
        }
        Edge(-46, -150, -46, 196); Edge(-46, 196, 46, 196); Edge(46, 196, 46, -196); Edge(46, -196, -46, -196); Edge(-46, -196, -46, -150);
        // On the west side going north, 20 m before the lap's first point.
        var plan = SideStep.Plan(Tractor(-38, -170), Speed, lap, closed: true, Field(), null, FrontBoom, MinRadius);
        Assert.That(plan.Outcome, Is.EqualTo(SideStepOutcome.Planned));
        var path = plan.Path!;
        Assert.Multiple(() =>
        {
            Assert.That(path[^1].Northing, Is.GreaterThan(-140), "on past the lap's first point");
            Assert.That(path[^1].Easting, Is.EqualTo(-46).Within(1e-6));
            for (int i = 1; i < path.Count; i++)
                Assert.That(GeometryMath.Distance(path[i - 1], path[i]), Is.LessThan(1.0), "no jump");
        });
    }
}
