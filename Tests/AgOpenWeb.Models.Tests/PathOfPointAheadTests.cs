// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Tool;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// Where a front implement's working centre goes while the pivot drives a U-turn or the way onto
/// a pass (<see cref="RigGeometry.PathOfPointAhead"/>): the line the map draws for it. Fork only.
/// </summary>
[TestFixture]
public class PathOfPointAheadTests
{
    private const double Reach = 4.7;   // an 8 m front boom on a tester's tractor
    private const double Radius = 5.4;  // his U-turn radius

    [Test]
    public void On_a_straight_path_the_point_is_the_same_path_further_on()
    {
        var path = Enumerable.Range(0, 20).Select(i => new Vec3(3, i * 0.5, 0)).ToList();
        var ahead = RigGeometry.PathOfPointAhead(path, Reach);

        Assert.That(ahead, Has.Count.EqualTo(path.Count));
        for (int i = 0; i < path.Count; i++)
        {
            Assert.That(ahead[i].Easting, Is.EqualTo(3).Within(1e-9));
            Assert.That(ahead[i].Northing, Is.EqualTo(path[i].Northing + Reach).Within(1e-9));
        }
    }

    [Test]
    public void Round_a_turn_the_point_runs_outside_the_pivot_s_circle()
    {
        // Half a circle to the right about (Radius, 0), a point every 2 degrees.
        var path = new List<Vec3>();
        for (int d = 0; d <= 180; d += 2)
        {
            double a = d * Math.PI / 180;
            path.Add(new Vec3(Radius - Radius * Math.Cos(a), Radius * Math.Sin(a), 0));
        }
        var ahead = RigGeometry.PathOfPointAhead(path, Reach);

        // The pivot on a 5.4 m circle, the point 4.7 m ahead of it on a 7.16 m one: 1.76 m outside.
        double outside = Math.Sqrt(Radius * Radius + Reach * Reach);
        Assert.That(outside - Radius, Is.EqualTo(1.76).Within(0.01));
        foreach (var p in ahead.Skip(1).Take(ahead.Count - 2))
            Assert.That(Math.Sqrt(Math.Pow(p.Easting - Radius, 2) + p.Northing * p.Northing), Is.EqualTo(outside).Within(0.01));
        // Half way round the pivot heads east at the top of its circle; the point is ahead of it.
        var top = ahead[45];
        Assert.That(top.Easting, Is.EqualTo(Radius + Reach).Within(0.01));
        Assert.That(top.Northing, Is.EqualTo(Radius).Within(0.01));
    }

    [Test]
    public void A_point_given_twice_keeps_the_path_s_direction()
    {
        var path = new List<Vec3> { new(0, 0, 0), new(0, 1, 0), new(0, 1, 0), new(0, 2, 0) };
        var ahead = RigGeometry.PathOfPointAhead(path, Reach);
        Assert.That(ahead.Select(p => p.Northing), Is.EqualTo(new[] { 0 + Reach, 1 + Reach, 1 + Reach, 2 + Reach }).Within(1e-9));
        Assert.That(ahead.All(p => Math.Abs(p.Easting) < 1e-9));
    }

    [Test]
    public void Only_a_front_implement_has_a_reach_ahead()
    {
        var vehicle = new VehicleConfig { FrontHitchLength = 4.5, ThreePointHitchLength = 0.9, HitchLength = 0.9 };
        var tool = new ToolConfig { WorkingPointFromHitch = 0.2 };

        tool.SetToolType("front");
        Assert.That(RigGeometry.FrontToolReach(vehicle, tool), Is.EqualTo(4.7).Within(1e-9));
        foreach (string type in new[] { "rear", "trailing", "tbt" })
        {
            tool.SetToolType(type);
            Assert.That(RigGeometry.FrontToolReach(vehicle, tool), Is.Zero, type);
        }
    }
}
