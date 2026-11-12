// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// A closed track's pass where the field is narrower than twice its offset: the offset leaves
/// out the points too near the reference and joins what is left, so across a neck of the field
/// the pass runs along the fence. On a tester's field (a boundary curve, 8 m boom) the second
/// lap ran 0.1-0.5 m from the fence of a bump across a 17.5 m neck. <see cref="CurveProcessing.PassUnfit"/>
/// finds that stretch; the pipeline shows the pass without it and stops AutoSteer before it.
/// Fork only.
/// </summary>
[TestFixture]
public class ClosedPassFitTests
{
    // A field 100 x 200 m with a notch cut in from the east between N 20 and N 40, to 17 m from
    // the west fence: two blocks joined by a 17 m neck on the west side. Clockwise, so the
    // passes inside are to the right (+).
    private static readonly List<Vec3> Fence = new()
    {
        new(-50, -100, 0), new(-50, 100, 0), new(50, 100, 0), new(50, 40, 0),
        new(-33, 40, 0), new(-33, 20, 0), new(50, 20, 0), new(50, -100, 0),
    };

    private static List<Vec3> Reference() => CurveProcessing.DensifyLoop(Fence);

    private const double TurnRadius = 6;
    // Half an 8 m pass, as the pipeline asks it.
    private const double Tolerance = 4;

    private static List<Vec3> Pass(double distAway) =>
        CurveProcessing.CurvePass(Reference(), distAway, closed: true, minTurnRadius: TurnRadius).Points;

    // As the pipeline's pass cache asks it: the pass as offset, before its bends were opened to
    // the turn radius, lets off the corners that rounding brought nearer.
    private static bool[]? Unfit(IReadOnlyList<Vec3> reference, IReadOnlyList<Vec3> pass, double distAway,
        double tolerance = Tolerance) =>
        CurveProcessing.PassUnfit(reference, pass, distAway, tolerance,
            CurveProcessing.CreateOffsetCurveWithInfo(reference, distAway, closed: true).Points);

    private static double ToFence(Vec3 p)
    {
        double best = double.MaxValue;
        for (int i = 0; i < Fence.Count; i++)
        {
            var a = Fence[i];
            var b = Fence[(i + 1) % Fence.Count];
            double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing;
            double t = Math.Clamp(((p.Easting - a.Easting) * dx + (p.Northing - a.Northing) * dy) / (dx * dx + dy * dy), 0, 1);
            double ex = a.Easting + t * dx - p.Easting, ey = a.Northing + t * dy - p.Northing;
            best = Math.Min(best, Math.Sqrt(ex * ex + ey * ey));
        }
        return best;
    }

    [Test]
    public void A_pass_across_a_neck_narrower_than_twice_its_offset_does_not_fit_there()
    {
        var pass = Pass(12);
        var unfit = Unfit(Reference(), pass, 12);
        Assert.That(unfit, Is.Not.Null, "the 12 m pass is joined across the 17 m neck");

        var bad = pass.Where((p, i) => unfit![i]).ToList();
        var pieces = CurveProcessing.FitPieces(pass, unfit!);
        Assert.Multiple(() =>
        {
            Assert.That(bad, Is.Not.Empty);
            Assert.That(bad.All(p => p.Easting < -30 && p.Northing > 5 && p.Northing < 55), "only in the neck");
            Assert.That(bad.Min(ToFence), Is.LessThan(6), "the joined stretch runs along the notch's fence");
            Assert.That(pieces, Has.Count.EqualTo(2), "the south block's lap and the north block's");
            // What is left is nearer its own place than the next pass out's: the ends of the
            // stretch joined across, where it is less than half a pass too near, are kept.
            foreach (var piece in pieces)
                Assert.That(piece.Min(ToFence), Is.GreaterThan(12 - Tolerance), "what is left is on its pass");
            Assert.That(pieces.Sum(p => p.Count) + bad.Count, Is.EqualTo(pass.Count - 1), "every point once round (the last repeats the first)");
        });
    }

    [TestCase(4)]
    [TestCase(-4)]
    public void A_pass_that_fits_through_the_neck_fits_everywhere(double distAway)
    {
        // Inside, 4 m off the fence: the neck leaves it 9 m. Outside: nothing near. Judged to
        // 0.5 m: the 6 m turn radius rounds the outside of each corner nearer the fence than 4 m,
        // which is not a pass that does not fit, and counts only judged without the pass as offset.
        Assert.Multiple(() =>
        {
            Assert.That(Unfit(Reference(), Pass(distAway), distAway, tolerance: 0.5), Is.Null);
            Assert.That(CurveProcessing.PassUnfit(Reference(), Pass(distAway), distAway, 0.5), Is.Not.Null,
                "the rounded corners alone");
        });
    }

    [Test]
    public void A_pass_a_little_too_far_in_for_the_neck_is_driven_through()
    {
        // 10 m in: joined across the 17 m neck 7 m from the notch, 3 m nearer than its offset but
        // nearer its own place than the next pass out's (half a pass, 4 m).
        var pass = Pass(10);
        Assert.Multiple(() =>
        {
            Assert.That(pass.Where(p => p.Northing > 25 && p.Northing < 35).Min(ToFence), Is.LessThan(7.5), "joined across");
            Assert.That(Unfit(Reference(), pass, 10), Is.Null);
        });
    }

    [TestCase(4)]
    [TestCase(12)]
    [TestCase(28)]
    [TestCase(-4)]
    [TestCase(-12)]
    public void Every_pass_of_a_plain_field_fits(double distAway)
    {
        var square = CurveProcessing.DensifyLoop(new List<Vec3> { new(-50, -50, 0), new(-50, 50, 0), new(50, 50, 0), new(50, -50, 0) });
        var pass = CurveProcessing.CurvePass(square, distAway, closed: true, minTurnRadius: TurnRadius).Points;
        Assert.That(Unfit(square, pass, distAway), Is.Null);
    }

    [Test]
    public void The_stretch_shown_is_the_one_the_tractor_is_on()
    {
        var pass = Pass(12);
        var unfit = Unfit(Reference(), pass, 12)!;

        var south = CurveProcessing.FitPieceFrom(pass, unfit, CurveProcessing.FitPieceStart(pass, unfit, -38, -40));
        var north = CurveProcessing.FitPieceFrom(pass, unfit, CurveProcessing.FitPieceStart(pass, unfit, -38, 70));
        Assert.Multiple(() =>
        {
            Assert.That(south.All(p => p.Northing < 15), "the south block's lap");
            Assert.That(south.Count, Is.GreaterThan(50));
            Assert.That(north.All(p => p.Northing > 45), "the north block's lap");
            Assert.That(north.Count, Is.GreaterThan(50));
        });
    }

    [Test]
    public void The_stretch_that_does_not_fit_is_seen_ahead_only_the_way_the_tractor_goes()
    {
        var pass = Pass(12);
        var unfit = Unfit(Reference(), pass, 12)!;
        int at = CurveProcessing.NearestIndex(pass, -38, -10);
        double firstBad = pass.Where((p, i) => unfit[i] && p.Northing < 30).Min(p => p.Northing);

        Assert.Multiple(() =>
        {
            Assert.That(firstBad, Is.GreaterThan(5).And.LessThan(15));
            double gap = firstBad + 10; // from N -10 north to it
            Assert.That(CurveProcessing.UnfitAhead(pass, unfit, at, 0, gap - 2), Is.False, "heading north, not yet");
            Assert.That(CurveProcessing.UnfitAhead(pass, unfit, at, 0, gap + 2), Is.True, "heading north, within reach");
            Assert.That(CurveProcessing.UnfitAhead(pass, unfit, at, Math.PI, 100), Is.False, "heading south, the lap goes round the south block first");
            int inNeck = CurveProcessing.NearestIndex(pass, -38, 30);
            Assert.That(unfit[inNeck], Is.True);
            Assert.That(CurveProcessing.UnfitAhead(pass, unfit, inNeck, 0, 1), Is.True, "on it already");
        });
    }

    // The same field size with a 4 m track cut in from the east between N 0 and N 4, its end 2 m
    // from the west fence: every lap from the second on crosses the track there (twice, the lap
    // round each block joined across it), as on a tester's field with a dirt track.
    private static readonly List<Vec3> Track = new()
    {
        new(-50, -100, 0), new(-50, 100, 0), new(50, 100, 0), new(50, 4, 0),
        new(-48, 4, 0), new(-48, 0, 0), new(50, 0, 0), new(50, -100, 0),
    };

    [Test]
    public void With_a_soft_fence_a_lap_that_crosses_a_track_cut_into_the_field_fits()
    {
        var reference = CurveProcessing.DensifyLoop(Track);
        var pass = CurveProcessing.CurvePass(reference, 12, closed: true, minTurnRadius: TurnRadius).Points;
        var unfit = Unfit(reference, pass, 12);
        Assert.That(unfit, Is.Not.Null, "across the track the lap is nearer the fence than 8 m");
        Assert.That(CurveProcessing.UnfitInside(pass, unfit, (e, n) => GeometryMath.IsPointInPolygon(Track, e, n)), Is.Null,
            "each stretch that does not fit goes out of the field, across the track");
    }

    [Test]
    public void With_a_soft_fence_a_lap_across_a_neck_inside_the_field_still_does_not_fit()
    {
        var pass = Pass(12);
        var unfit = Unfit(Reference(), pass, 12)!;
        var left = CurveProcessing.UnfitInside(pass, unfit, (e, n) => GeometryMath.IsPointInPolygon(Fence, e, n));
        Assert.That(left, Is.EqualTo(unfit), "the stretch along the fence across the neck stays inside");
    }

    [Test]
    public void Only_the_stretch_that_goes_out_is_let_off()
    {
        // A square lap whose points 2-3 and 6-7 do not fit; the step from 6 to 7 goes out.
        var pass = new List<Vec3> { new(0, 0, 0), new(0, 10, 0), new(0, 20, 0), new(0, 30, 0), new(10, 30, 0),
            new(20, 30, 0), new(30, 30, 0), new(30, 20, 0), new(30, 10, 0), new(30, 0, 0), new(0, 0, 0) };
        var unfit = new bool[pass.Count];
        unfit[2] = unfit[3] = unfit[6] = unfit[7] = true;
        var left = CurveProcessing.UnfitInside(pass, unfit, (e, n) => !(e > 30.0 - 1e-9 && n > 24 && n < 26))!;
        Assert.That(left.Select((u, i) => u ? i : -1).Where(i => i >= 0), Is.EqualTo(new[] { 2, 3 }));
    }
}
