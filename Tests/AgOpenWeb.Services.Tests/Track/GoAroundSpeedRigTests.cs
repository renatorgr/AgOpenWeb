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
using AgOpenWeb.Services.Tests.YouTurn;

namespace AgOpenWeb.Services.Tests.Track;

/// <summary>
/// The Go around speed warning driven on the closed-loop obstacle rig (<see cref="ObstacleScenarioRig"/>:
/// the real GPS cycle steering a bicycle model whose wheels turn at most 25°/s, answering 0.2 s late)
/// past one hard Go around tree on a straight pass, an 8 m implement. The explicit table compares, for
/// each radius, margin, tree, Steer Response and implement, the nearest the implement came to the tree
/// at each speed with the speed the host told the operator to slow down to; the tests hold the rules.
/// Fork only.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class GoAroundSpeedRigTests
{
    internal static ObstacleTestField.Built OneTree(double treeE, double treeRadius)
    {
        var outer = ObstacleTestField.Polygon(new[] { (-100.0, -300.0), (100, -300), (100, 300), (-100, 300) });
        outer.IsHard = true;
        var boundary = new Boundary { OuterBoundary = outer };
        var tree = ObstacleTestField.Circle(treeE, 0, treeRadius);
        tree.AtObstacle = ObstacleAction.GoRound;
        tree.IsHard = true;
        boundary.InnerBoundaries.Add(tree);
        var headland = new PolygonOffsetService()
            .CreateInwardOffset(outer.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList(), 20)!;
        boundary.HeadlandPolygon = ObstacleTestField.Polygon(headland.Select(p => (p.Easting, p.Northing)).ToArray());
        return new ObstacleTestField.Built
        {
            Boundary = boundary,
            HeadlandLine = headland.Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList(),
            Obstacles = new[] { new ObstacleTestField.Obstacle("Tree", tree, treeE, 0) },
        };
    }

    /// <summary>One run north along the pass through E 0, past a tree 3 m across at (treeE, 0).</summary>
    internal static (double Clearance, List<(string Text, double E, double N)> Notices) Drive(double treeE, double radius,
        double margin, double kmh, string tool = "rear", double hold = 3.0, bool stanley = false)
    {
        using var rig = new ObstacleScenarioRig(OneTree(treeE, 1.5), tool, 8);
        var c = ConfigurationStore.Instance;
        c.Guidance.GoAroundRadius = radius;
        c.Guidance.GoAroundMargin = margin;
        c.Guidance.GoalPointLookAheadHold = hold;
        c.Guidance.IsPurePursuit = !stanley;
        c.Vehicle.MaxSteerAngle = ObstacleScenarioRig.MaxSteerDeg;
        rig.SteerRate = 25;
        rig.SteerDelay = 0.2;
        rig.Speed = kmh / 3.6;
        rig.UseTrack(Models.Track.Track.FromABLine("AB", new Vec3(0, -300, 0), new Vec3(0, 300, 0)), 0);
        rig.Place(0, -150, 0);
        rig.Engage();
        rig.DriveUntil(() => rig.N > 60 || !rig.Engaged, 200);
        return (rig.WorstClearanceGoRound, rig.SpeedNotices);
    }

    private static int? SpeedTold(string text) =>
        text.StartsWith("Slow down to ", StringComparison.Ordinal) ? int.Parse(text.Substring(13, text.IndexOf(' ', 13) - 13)) : null;

    [Test]
    public void Too_fast_for_the_bend_is_told_once_before_it_with_a_speed_that_keeps_the_implement_out()
    {
        // Margin 0.3 m (0.39 m in force on a 5 m bend), Steer Response 3, an 8 m front implement:
        // at 12 km/h the bend is cut by more than the margin less its reserve.
        var (fast, notices) = Drive(0, 5, 0.3, 12, "front");
        Assert.That(notices, Has.Count.EqualTo(1), "told once on the pass");
        var (text, _, n) = notices[0];
        Assert.That(text, Does.EndWith("for the bend round Inner 1"));
        int? told = SpeedTold(text);
        Assert.That(told, Is.Not.Null.And.InRange(3, 11), text);
        // The tree's bend starts about 10 m before it; the warning comes seconds of travel before that.
        Assert.That(n, Is.LessThan(-10 - 3 * 12 / 3.6), "told a few seconds before the bend");

        // At the speed told the implement keeps further from the tree, and nothing is said.
        var (slowed, quiet) = Drive(0, 5, 0.3, told!.Value, "front");
        Assert.That(quiet, Is.Empty, $"not told again at {told} km/h");
        Assert.That(slowed, Is.GreaterThan(fast + 0.02).And.GreaterThan(0.05),
            $"at {told} km/h the implement keeps {slowed:F2} m from the tree, at 12 km/h {fast:F2} m");
    }

    [Test]
    public void A_bend_taken_slowly_or_with_room_to_spare_is_not_told()
    {
        Assert.That(Drive(0, 5, 0.3, 6, "front").Notices, Is.Empty, "6 km/h on a 5 m bend, margin 0.3 m");
        Assert.That(Drive(0, 5, 1.0, 12, "front").Notices, Is.Empty, "12 km/h with a 1 m margin");
    }

    [Test]
    public void Where_the_implement_would_go_into_the_tree_the_operator_was_told()
    {
        // Steer Response 4 cuts the bends more: at 10-12 km/h the front implement goes into the tree.
        foreach (var (treeE, margin, kmh) in new[] { (3.0, 0.3, 10.0), (0.0, 0.5, 12.0), (3.0, 0.5, 12.0) })
        {
            var (clear, notices) = Drive(treeE, 5, margin, kmh, "front", hold: 4.0);
            Assert.That(clear, Is.LessThan(0.02), $"tree {treeE}, margin {margin}, {kmh} km/h: the rig goes into it");
            Assert.That(notices, Has.Count.EqualTo(1), $"tree {treeE}, margin {margin}, {kmh} km/h: told");
        }
    }

    [Test]
    public void Stanley_is_not_told()
    {
        Assert.That(Drive(0, 5, 0.3, 12, "front", stanley: true).Notices, Is.Empty);
    }

    /// <summary>The calibration table: the implement's nearest to the tree (m) and the speed told, by speed.</summary>
    [Test]
    [Explicit("calibration table, about 30 s")]
    public void Table()
    {
        foreach (double radius in new[] { 5.0, 8.0 })
            foreach (double margin in new[] { 0.3, 0.5, 1.0 })
                foreach (double treeE in new[] { 0.0, 3.0 })
                    foreach (double hold in new[] { 3.0, 4.0 })
                        foreach (var tool in new[] { "rear", "front" })
                        {
                            var row = new List<string>();
                            foreach (double kmh in new[] { 4.0, 6, 8, 10, 12 })
                            {
                                var (clear, notices) = Drive(treeE, radius, margin, kmh, tool, hold);
                                string told = notices.Count == 0 ? "-" : SpeedTold(notices[0].Text)?.ToString() ?? "!!";
                                row.Add($"{kmh,2:F0}:{clear,6:F2}({told})");
                            }
                            TestContext.Out.WriteLine($"R {radius} m {margin} tree {treeE} hold {hold} {tool,-5}: " + string.Join(" ", row));
                        }
    }
}
