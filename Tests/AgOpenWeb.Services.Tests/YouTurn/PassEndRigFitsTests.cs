// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.YouTurn;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// A stretch of a pass outside the turn line is driven through when it stays inside the fence
/// (<see cref="PassEnds.Find"/>). Beside a hard fence that is asked of the whole rig, not of the
/// pass's line (<see cref="PassEnds.RigFits"/>; fork only).
/// <para>
/// On a tester's field (Tapada da Casa, a 3 m no-till drill, 27 Nov 2026) a pass runs 0.7 m from
/// the tip of a bay of the hard fence, 60 m from one end of the pass and 500 m from the other. The
/// line is inside the fence,
/// so the pass was driven through, and the hard fence guard took AutoSteer off 2 s before the tip,
/// where the drill's end would be 0.8 m over the fence: every job with the drill ended on that pass.
/// The fence here is that tip: a bay from the east side whose tip is 0.7 m from the pass.
/// </para>
/// </summary>
[TestFixture, NonParallelizable] // ConfigurationStore is a singleton.
public class PassEndRigFitsTests
{
    private const double FromBoundary = 2.8;  // the turn line: the fence moved in by this much
    private const double HalfDrill = 1.0;     // a 3 m drill: half of it less the guard's 0.5 m

    // A field from x -60 to 40 and y -100 to 100, with a bay from the east side to (tipX, tipY).
    private static BoundaryPolygon Fence(double tipX, bool hard = true, double tipY = 0, double baseHalf = 12) => new()
    {
        Points = new List<BoundaryPoint>
        {
            new(-60, -100, 0), new(40, -100, 0), new(40, tipY - baseHalf, 0), new(tipX, tipY, 0),
            new(40, tipY + baseHalf, 0), new(40, 100, 0), new(-60, 100, 0),
        },
        IsHard = hard,
    };

    // The pass along x = 0, a point every 2 m, run on past both ends of the field.
    private static readonly List<Vec3> Pass = Enumerable.Range(0, 111).Select(k => new Vec3(0, -110 + 2.0 * k, 0)).ToList();

    private static double Clearance(BoundaryPolygon fence, Vec3 p)
    {
        double best = double.MaxValue;
        for (int i = 0; i < fence.Points.Count; i++)
        {
            var a = fence.Points[i];
            var b = fence.Points[(i + 1) % fence.Points.Count];
            double de = b.Easting - a.Easting, dn = b.Northing - a.Northing;
            double t = Math.Clamp(((p.Easting - a.Easting) * de + (p.Northing - a.Northing) * dn) / (de * de + dn * dn), 0, 1);
            best = Math.Min(best, Math.Sqrt(Math.Pow(p.Easting - a.Easting - t * de, 2) + Math.Pow(p.Northing - a.Northing - t * dn, 2)));
        }
        return best;
    }

    // The end of the pass ahead of y = -60, going north.
    private static PassEnd? EndAhead(BoundaryPolygon fence, double halfWidth)
    {
        int from = PassEnds.NearestIndex(Pass, 0, -60);
        return PassEnds.Find(Pass, from, forward: true,
            p => fence.IsPointInside(p.Easting, p.Northing) && Clearance(fence, p) >= FromBoundary,
            turnLine: null,
            p => fence.IsPointInside(p.Easting, p.Northing),
            debug: null,
            PassEnds.RigFits(fence, halfWidth));
    }

    [Test]
    public void Beside_a_hard_fence_the_pass_ends_before_a_tip_the_rig_does_not_fit_past()
    {
        var end = EndAhead(Fence(tipX: 0.7), HalfDrill);
        Assert.That(end, Is.Not.Null);
        // The tip is 2.8 m from the pass's point at y -2.7: the first point outside the turn line is y -2.
        Assert.That(Pass[end!.Value.OutsideIndex].Northing, Is.EqualTo(-2).Within(1e-9),
            "the pass ends at the turn line before the tip, not at the north end of the field");
        Assert.That(Pass[end.Value.InsideIndex].Northing, Is.EqualTo(-4).Within(1e-9));
    }

    [Test]
    public void Beside_a_soft_fence_the_same_pass_is_driven_through()
    {
        var soft = Fence(tipX: 0.7, hard: false);
        Assert.That(PassEnds.RigFits(soft, HalfDrill), Is.Null, "an implement may hang over a soft fence: no test of the rig");
        var end = EndAhead(soft, HalfDrill);
        Assert.That(end, Is.Not.Null);
        Assert.That(Pass[end!.Value.OutsideIndex].Northing, Is.EqualTo(98).Within(1e-9), "the north end of the field, as before");
    }

    [Test]
    public void A_tip_that_leaves_the_rig_room_is_driven_through()
    {
        // 1.3 m from the pass: the drill's end is 0.2 m over the fence there, less than the hard
        // fence guard lets it brush (0.5 m), and the wheels are inside.
        var end = EndAhead(Fence(tipX: 1.3), HalfDrill);
        Assert.That(end, Is.Not.Null);
        Assert.That(Pass[end!.Value.OutsideIndex].Northing, Is.EqualTo(98).Within(1e-9), "the north end of the field");
    }

    [Test]
    public void A_wider_implement_ends_the_pass_at_the_tip_a_narrower_one_passes()
    {
        // The same tip, 1.3 m from the pass, and a 6 m implement (half of it less 0.5 m: 2.5 m).
        var end = EndAhead(Fence(tipX: 1.3), 2.5);
        Assert.That(end, Is.Not.Null);
        Assert.That(Pass[end!.Value.OutsideIndex].Northing, Is.LessThan(0), "before the tip");
    }

    [Test]
    public void A_thin_spike_between_two_points_of_the_pass_is_found()
    {
        // A spike 0.2 m wide at its base whose tip, 0.5 m from the pass, lies between the pass's
        // points at y 0 and y 2: no bar across the pass at a point touches it, the band's edge does.
        var fence = Fence(tipX: 0.5, tipY: 1.0, baseHalf: 0.1);
        var fits = PassEnds.RigFits(fence, HalfDrill);
        Assert.That(fits, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(fits!(new Vec3(0, 0, 0), new Vec3(0, 2, 0)), Is.False, "the spike is in the rig's way between the two points");
            Assert.That(fits(new Vec3(0, -4, 0), new Vec3(0, -2, 0)), Is.True, "before it the rig fits");
            Assert.That(fits(new Vec3(0, 4, 0), new Vec3(0, 6, 0)), Is.True, "and after it");
        });
        var end = EndAhead(fence, HalfDrill);
        Assert.That(end, Is.Not.Null);
        Assert.That(Pass[end!.Value.OutsideIndex].Northing, Is.LessThan(1), "the pass ends before the spike");
    }

    [Test]
    public void The_next_pass_of_a_turn_is_asked_the_same()
    {
        var fence = Fence(tipX: 0.7);
        Func<Vec3, bool> insideTurnLine = p => fence.IsPointInside(p.Easting, p.Northing) && Clearance(fence, p) >= FromBoundary;
        Func<Vec3, bool> insideFence = p => fence.IsPointInside(p.Easting, p.Northing);
        int first = Pass.FindIndex(p => p.Northing > -60 && !insideTurnLine(p));
        Assert.Multiple(() =>
        {
            Assert.That(PassEnds.IsDrivenThrough(Pass, first, 1, insideTurnLine, insideFence, out int on), Is.True,
                "by its line alone the stretch is driven through");
            Assert.That(Pass[on].Northing, Is.GreaterThan(0), "back inside the turn line past the tip");
            Assert.That(PassEnds.IsDrivenThrough(Pass, first, 1, insideTurnLine, insideFence, out int stays, PassEnds.RigFits(fence, HalfDrill)), Is.False,
                "with the rig it is the end of the pass");
            Assert.That(stays, Is.EqualTo(first));
        });
    }

    [Test]
    public void No_fence_no_width_no_test()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PassEnds.RigFits(null, HalfDrill), Is.Null);
            Assert.That(PassEnds.RigFits(Fence(tipX: 0.7), 0), Is.Null);
            Assert.That(PassEnds.RigFits(Fence(tipX: 0.7), double.NaN), Is.Null);
            Assert.That(PassEnds.RigFits(new BoundaryPolygon { IsHard = true }, HalfDrill), Is.Null, "a fence with no outline");
        });
    }

    // The implement's offset, plus half the implement less the 0.5 m the hard fence guard lets an
    // end go over a fence, or plus half the tractor's track when that is more.
    [TestCase(3.0, 0.0, 1.9, 1.0)]    // the drill
    [TestCase(8.0, 0.0, 1.9, 3.5)]    // the front boom
    [TestCase(3.0, 0.5, 1.9, 1.5)]    // shifted half a metre to one side
    [TestCase(3.0, -0.5, 1.9, 1.5)]   // or to the other
    [TestCase(1.0, 0.0, 2.0, 1.0)]    // narrower than the tractor: its wheels
    // Narrow and shifted a metre: on the tool's own line the tractor runs a metre beside it, so its
    // far wheels are 1.95 m from the line (the implement's end only 0.75 m).
    [TestCase(1.5, 1.0, 1.9, 1.95)]
    [TestCase(3.0, 0.0, double.NaN, 1.0)]   // a measure that is no number counts as 0
    [TestCase(3.0, double.NaN, 1.9, 1.0)]
    [TestCase(0.5, 0.0, -1.9, 0.0)]         // never negative
    public void The_room_the_rig_needs_either_side_of_the_pass(double toolWidth, double toolOffset, double trackWidth, double expected)
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, (int)Math.Round(toolWidth * 100));
        config.Tool.Width = toolWidth;
        config.Tool.Offset = toolOffset;
        config.Vehicle.TrackWidth = trackWidth;
        Assert.That(PassEnds.RigHalfWidth(config), Is.EqualTo(expected).Within(1e-9));
    }
}
