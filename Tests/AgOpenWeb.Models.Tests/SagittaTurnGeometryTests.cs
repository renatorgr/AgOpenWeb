// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.YouTurn;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

[TestFixture]
public class SagittaTurnGeometryTests
{
    private const double R = 5.0;

    private static double MaxStep(IReadOnlyList<Vec3> path)
    {
        double max = 0;
        for (int i = 1; i < path.Count; i++)
        {
            double d = GeometryMath.Distance(path[i - 1], path[i]);
            if (d > max) max = d;
        }
        return max;
    }

    [Test]
    public void PlainSemicircle_NoSagitta_LandsTwoRadiiAcross()
    {
        // Heading north (theta = 0), turning right, no sagitta -> a clean semicircle.
        var start = new Vec3(0, 0, 0);
        var path = SagittaTurnGeometry.BuildOffsetArc(start, 0.0, isTurningRight: true,
            turningRadius: R, offsetDistance: 0.0, angle: Math.PI);

        Assert.That(path.Count, Is.GreaterThan(2));

        var end = path[^1];
        // A 180° turn of radius R offsets laterally by 2R and returns to the start's along-track line.
        Assert.That(end.Easting, Is.EqualTo(2 * R).Within(0.05), "lateral offset should be 2R");
        Assert.That(end.Northing, Is.EqualTo(0.0).Within(0.05), "should return to the start along-track line");
    }

    [Test]
    public void PlainSemicircle_ReversesHeadingByPi()
    {
        var start = new Vec3(0, 0, 0);
        var path = SagittaTurnGeometry.BuildOffsetArc(start, 0.0, isTurningRight: true,
            turningRadius: R, offsetDistance: 0.0, angle: Math.PI);

        double endHeading = path[^1].Heading;
        // Heading should have advanced ~PI (mod 2π).
        double delta = Math.Abs(endHeading - Math.PI);
        Assert.That(delta, Is.LessThan(0.02), $"end heading {endHeading} should be ~PI");
    }

    [TestCase(3.0)]
    [TestCase(5.0)]
    [TestCase(8.0)]
    [TestCase(10.0)]
    public void Sagitta_LandsExactlyOnTargetRow(double targetLateral)
    {
        // To land at lateral L, pass pullback = 2R - L. The arc must end at E≈L
        // with heading reversed by π so the exit leg lies on the next track.
        var start = new Vec3(0, 0, 0);
        double pullback = 2 * R - targetLateral;
        var path = SagittaTurnGeometry.BuildOffsetArc(start, 0.0, isTurningRight: true,
            turningRadius: R, offsetDistance: pullback, angle: Math.PI);

        var end = path[^1];
        Assert.That(end.Easting, Is.EqualTo(targetLateral).Within(0.05),
            "landing lateral should equal the target row offset");
        Assert.That(end.Heading, Is.EqualTo(Math.PI).Within(0.02),
            "exit heading should be reversed (parallel to the track)");
    }

    [Test]
    public void Sagitta_AddsCounterArc_StartingTheOppositeWay()
    {
        // With a sagitta, the path must begin with a short counter-arc: for a
        // right turn heading north, the very first move bends LEFT (easting < 0)
        // before the main arc sweeps right.
        var start = new Vec3(0, 0, 0);
        double offset = R; // tight row -> meaningful sagitta
        var path = SagittaTurnGeometry.BuildOffsetArc(start, 0.0, isTurningRight: true,
            turningRadius: R, offsetDistance: offset, angle: Math.PI);

        Assert.That(path.Count, Is.GreaterThan(4));
        Assert.That(path[1].Easting, Is.LessThan(0.0),
            "first point of a sagitta right-turn should bend left (counter-arc lead-in)");
    }

    [Test]
    public void Sagitta_NoSagitta_HaveDifferentPaths()
    {
        var start = new Vec3(0, 0, 0);
        var plain = SagittaTurnGeometry.BuildOffsetArc(start, 0.0, true, R, 0.0, Math.PI);
        var sagitta = SagittaTurnGeometry.BuildOffsetArc(start, 0.0, true, R, R, Math.PI);

        Assert.That(sagitta.Count, Is.GreaterThan(plain.Count),
            "the counter-arc lead-in should add points");
    }

    [Test]
    public void LeftAndRight_AreMirrored()
    {
        var start = new Vec3(0, 0, 0);
        var right = SagittaTurnGeometry.BuildOffsetArc(start, 0.0, isTurningRight: true, R, 0.0, Math.PI);
        var left = SagittaTurnGeometry.BuildOffsetArc(start, 0.0, isTurningRight: false, R, 0.0, Math.PI);

        Assert.That(right[^1].Easting, Is.EqualTo(+2 * R).Within(0.05));
        Assert.That(left[^1].Easting, Is.EqualTo(-2 * R).Within(0.05));
    }

    [Test]
    public void Points_AreFinelySpaced_NoLargeGaps()
    {
        var start = new Vec3(0, 0, 0);
        var path = SagittaTurnGeometry.BuildOffsetArc(start, 0.0, true, R, R, Math.PI);

        // Segment sizing is radius * 0.1, so steps should stay well under ~0.6 m for R=5.
        Assert.That(MaxStep(path), Is.LessThan(R * 0.15));
    }

    [Test]
    public void ZeroRadius_DoesNotThrow_ReturnsStartOnly()
    {
        var start = new Vec3(1, 2, 0);
        var path = SagittaTurnGeometry.BuildOffsetArc(start, 0.0, true, 0.0, 0.0, Math.PI);
        Assert.That(path.Count, Is.EqualTo(1));
    }
}

/// <summary>The keyhole: the sagitta lead-in mirrored at the end (#418).</summary>
[TestFixture]
public class KeyholeTurnGeometryTests
{
    private const double R = 5.0;

    private static double MaxStep(IReadOnlyList<Vec3> path)
    {
        double max = 0;
        for (int i = 1; i < path.Count; i++)
        {
            double d = GeometryMath.Distance(path[i - 1], path[i]);
            if (d > max) max = d;
        }
        return max;
    }

    private static List<Vec3> Keyhole(double lateral, bool right = true, double r = R) =>
        SagittaTurnGeometry.BuildKeyhole(new Vec3(0, 0, 0), 0.0, right, r, lateral);

    [Test]
    public void FullWidth_IsAPlainSemicircle()
    {
        // Heading north, turning right onto a pass 2R over: no counter-arcs, depth R.
        var path = Keyhole(2 * R);
        Assert.Multiple(() =>
        {
            Assert.That(path[^1].Easting, Is.EqualTo(2 * R).Within(0.05));
            Assert.That(path[^1].Northing, Is.EqualTo(0.0).Within(0.05));
            Assert.That(path.Min(p => p.Easting), Is.GreaterThanOrEqualTo(-0.01), "no swing the other way");
            Assert.That(path.Max(p => p.Northing), Is.EqualTo(R).Within(0.05));
        });
    }

    [TestCase(0.5)]
    [TestCase(3.0)]
    [TestCase(5.0)]
    [TestCase(8.0)]
    [TestCase(10.0)]
    public void LandsOnTheTargetPass_LevelWithTheStart_HeadingBack(double lateral)
    {
        var end = Keyhole(lateral)[^1];
        Assert.Multiple(() =>
        {
            Assert.That(end.Easting, Is.EqualTo(lateral).Within(0.05));
            Assert.That(end.Northing, Is.EqualTo(0.0).Within(0.05));
            Assert.That(Math.Abs(Math.IEEERemainder(end.Heading - Math.PI, 2 * Math.PI)), Is.LessThan(0.02));
        });
    }

    [TestCase(3.0)]
    [TestCase(6.0)]
    public void IsSymmetric_AboutTheMidline(double lateral)
    {
        // #418: the loop is centred between the passes, so it swings as far past the
        // start pass as past the target.
        var path = Keyhole(lateral);
        double mid = lateral / 2;
        Assert.That(mid - path.Min(p => p.Easting), Is.EqualTo(path.Max(p => p.Easting) - mid).Within(0.05));
    }

    [Test]
    public void Report418_Rig_IsAbout20mDeep()
    {
        // 6 m passes, 8 m radius: the one-sided turn was 22.8 m deep, 10 m to the far side.
        var path = Keyhole(6, r: 8);
        Assert.Multiple(() =>
        {
            Assert.That(path.Max(p => p.Northing), Is.EqualTo(19.62).Within(0.05));
            Assert.That(SagittaTurnGeometry.KeyholeDepth(8, 6), Is.EqualTo(19.62).Within(0.01));
            Assert.That(path.Min(p => p.Easting), Is.EqualTo(-5).Within(0.05));
            Assert.That(path.Max(p => p.Easting), Is.EqualTo(11).Within(0.05));
        });
    }

    [Test]
    public void StartsTheOppositeWay()
    {
        // A right turn onto a near pass first bends left (the counter-arc).
        var path = Keyhole(R);
        Assert.That(path[1].Easting, Is.LessThan(0.0));
    }

    [Test]
    public void HeadingFollowsThePath()
    {
        var path = Keyhole(4);
        for (int i = 1; i < path.Count - 1; i++)
        {
            double h = Math.Atan2(path[i + 1].Easting - path[i - 1].Easting, path[i + 1].Northing - path[i - 1].Northing);
            Assert.That(Math.Abs(Math.IEEERemainder(path[i].Heading - h, 2 * Math.PI)), Is.LessThan(0.12), $"point {i}");
        }
    }

    [Test]
    public void LeftAndRight_AreMirrored()
    {
        Assert.That(Keyhole(2 * R, right: true)[^1].Easting, Is.EqualTo(+2 * R).Within(0.05));
        Assert.That(Keyhole(2 * R, right: false)[^1].Easting, Is.EqualTo(-2 * R).Within(0.05));
        Assert.That(Keyhole(R, right: false)[^1].Easting, Is.EqualTo(-R).Within(0.05));
    }

    [Test]
    public void Points_AreFinelySpaced_NoLargeGaps()
    {
        // Segment sizing is radius * 0.1, so steps stay well under ~0.6 m for R=5.
        Assert.That(MaxStep(Keyhole(R)), Is.LessThan(R * 0.15));
    }

    [Test]
    public void ZeroRadius_DoesNotThrow_ReturnsStartOnly()
    {
        var path = SagittaTurnGeometry.BuildKeyhole(new Vec3(1, 2, 0), 0.0, true, 0.0, 3.0);
        Assert.That(path.Count, Is.EqualTo(1));
    }
}

/// <summary>
/// The sagitta turn from any heading onto a line (fork only): a turn seated on a Go around bend of
/// the pass starts off the pass and at an angle to it, and must still land on the next pass at its
/// heading.
/// </summary>
[TestFixture]
public class SagittaTurnToLineTests
{
    private const double R = 8.0;

    // Across the line through (x, 0) heading south, and the heading's difference from south.
    private static (double Across, double HeadingOff) Landing(List<Vec3> path, double x)
    {
        var end = path[^1];
        double off = Math.IEEERemainder(end.Heading - Math.PI, 2 * Math.PI);
        return (end.Easting - x, off);
    }

    private static double MaxStep(IReadOnlyList<Vec3> path)
    {
        double max = 0;
        for (int i = 1; i < path.Count; i++)
            max = Math.Max(max, GeometryMath.Distance(path[i - 1], path[i]));
        return max;
    }

    [TestCase(4.0)]
    [TestCase(10.0)]
    [TestCase(16.0)]
    public void From_the_pass_at_its_heading_it_is_the_offset_arc(double lateral)
    {
        var start = new Vec3(0, 0, 0);
        var toLine = SagittaTurnGeometry.BuildToLine(start, 0, true, R, new Vec2(lateral, 0), Math.PI);
        var offsetArc = SagittaTurnGeometry.BuildOffsetArc(start, 0, true, R, 2 * R - lateral, Math.PI);

        Assert.That(toLine, Is.Not.Empty);
        Assert.That(toLine[^1].Easting, Is.EqualTo(offsetArc[^1].Easting).Within(0.01));
        Assert.That(toLine[^1].Northing, Is.EqualTo(offsetArc[^1].Northing).Within(0.01));
        // The same way out: as far to the outside of the turn first.
        Assert.That(toLine.Min(p => p.Easting), Is.EqualTo(offsetArc.Min(p => p.Easting)).Within(0.05));
    }

    [TestCase(20.0, true, 12.0)]
    [TestCase(-20.0, true, 12.0)]
    [TestCase(40.0, true, 14.0)]
    [TestCase(-35.0, true, 6.0)]
    [TestCase(20.0, false, -12.0)]
    [TestCase(-40.0, false, -13.0)]
    public void From_a_bend_it_lands_on_the_line_at_its_heading(double startDeg, bool right, double lineX)
    {
        // On a bend: heading 20-40° off the pass. The offset arc from there turned 180° and ended
        // that far off the next pass.
        var path = SagittaTurnGeometry.BuildToLine(new Vec3(0, 0, 0), startDeg * Math.PI / 180, right, R,
            new Vec2(lineX, 0), Math.PI);

        Assert.That(path, Is.Not.Empty, "no turn built");
        var (across, headingOff) = Landing(path, lineX);
        Assert.That(across, Is.EqualTo(0).Within(0.01), "lands beside the line");
        Assert.That(headingOff, Is.EqualTo(0).Within(0.01), "lands at the line's heading");
        Assert.That(MaxStep(path), Is.LessThan(R * 0.15), "a gap in the path");
    }

    [TestCase(true, 20.0)]
    [TestCase(false, -24.0)]
    public void Further_over_than_two_radii_it_drives_straight_between_the_arcs(bool right, double lineX)
    {
        // A bend that took the pass away from the next one: further than the arcs reach.
        var path = SagittaTurnGeometry.BuildToLine(new Vec3(0, 0, 0), 0, right, R, new Vec2(lineX, 0), Math.PI);

        Assert.That(path, Is.Not.Empty, "no turn built");
        var (across, headingOff) = Landing(path, lineX);
        Assert.That(across, Is.EqualTo(0).Within(0.01));
        Assert.That(headingOff, Is.EqualTo(0).Within(0.01));
        Assert.That(path.Max(p => p.Northing), Is.EqualTo(R).Within(0.05), "a quarter circle each side");
        Assert.That(MaxStep(path), Is.LessThan(R * 0.15));
    }

    [Test]
    public void A_line_the_turn_cannot_reach_gives_no_turn()
    {
        // Heading the way the start is (no U-turn onto it), and a line on the other side of the start.
        Assert.That(SagittaTurnGeometry.BuildToLine(new Vec3(0, 0, 0), 0, true, R, new Vec2(10, 0), 0), Is.Empty);
        Assert.That(SagittaTurnGeometry.BuildToLine(new Vec3(0, 0, 0), 0, true, R, new Vec2(-5, 0), Math.PI), Is.Empty);
    }
}
