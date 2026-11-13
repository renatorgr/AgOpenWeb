// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;

namespace AgOpenWeb.Services.Pipeline;

/// <summary>
/// Passes lying wholly outside a hard outer boundary (fork only). A hard boundary is not crossed,
/// so such a pass is never worked. With AutoSteer off the pass nearest the tractor is shown: a
/// tractor that had gone out of the field (the simulator driving on straight after AutoSteer went
/// off) was shown the lap outside the fence of a boundary curve, on a tester's field. The pass
/// shown is now the nearest one not wholly outside, and a lateral move onto one is refused.
/// </summary>
public static class HardFencePasses
{
    /// <summary>How far apart (m) the points looked at along a curve's pass are.</summary>
    private const double Step = 2.0;

    /// <summary>How many passes either side of the nearest are looked at for one inside the fence.</summary>
    public const int MaxSearch = 200;

    /// <summary>
    /// Whether the pass <paramref name="distAway"/> (m, + = right of A→B) from the reference of
    /// <paramref name="track"/> has no point inside <paramref name="fence"/>. An AB line's pass runs on
    /// without end; an open curve's runs on straight past its ends, as it is steered
    /// (<see cref="CurveProcessing.DefaultExtension"/>). A curve's pass is looked at as its reference
    /// moved sideways, without the offset's clean-up: where that crosses a corner of the fence into
    /// the field, the pass counts as inside. A water pivot's passes are never outside.
    /// </summary>
    public static bool WhollyOutside(Models.Track.Track track, double distAway, BoundaryPolygon fence)
    {
        var pts = track.Points;
        if (!fence.IsValid || track.IsPivot || pts.Count < 2) return false;

        if (pts.Count == 2 && !track.IsClosed)
        {
            // A line without end misses the field when every corner of the fence is on one side of it.
            double dx = pts[1].Easting - pts[0].Easting, dy = pts[1].Northing - pts[0].Northing;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) return false;
            double re = dy / len, rn = -dx / len; // right of A→B
            bool left = false, right = false;
            foreach (var p in fence.Points)
            {
                double s = (p.Easting - pts[0].Easting) * re + (p.Northing - pts[0].Northing) * rn - distAway;
                if (s > 0) right = true;
                else if (s < 0) left = true;
                if (left && right) return false;
            }
            return true;
        }

        foreach (var (e, n) in Samples(pts, track.IsClosed, distAway))
            if (fence.IsPointInside(e, n)) return false;
        return true;
    }

    /// <summary>
    /// The pass nearest <paramref name="nearest"/> that is not wholly outside the hard fence
    /// (<paramref name="outside"/>); between two as near, the one whose centre is nearer
    /// <paramref name="distFromRef"/> (its distance from <paramref name="offset"/>). <paramref name="nearest"/>
    /// itself when it is not outside, or when none is found within <see cref="MaxSearch"/> passes.
    /// </summary>
    public static int NearestInside(int nearest, double distFromRef, Func<int, bool> outside, Func<int, double> offset)
    {
        if (!outside(nearest)) return nearest;
        for (int k = 1; k <= MaxSearch; k++)
        {
            bool lowIn = !outside(nearest - k), highIn = !outside(nearest + k);
            if (lowIn && highIn)
                return Math.Abs(offset(nearest - k) - distFromRef) <= Math.Abs(offset(nearest + k) - distFromRef)
                    ? nearest - k : nearest + k;
            if (lowIn) return nearest - k;
            if (highIn) return nearest + k;
        }
        return nearest;
    }

    // Points about Step apart along the reference moved distAway to its right; an open curve's run on
    // straight past each end.
    private static IEnumerable<(double E, double N)> Samples(IReadOnlyList<Vec3> pts, bool closed, double distAway)
    {
        int count = pts.Count;
        (double E, double N) Right(int i)
        {
            int a = closed ? (i - 1 + count) % count : Math.Max(i - 1, 0);
            int b = closed ? (i + 1) % count : Math.Min(i + 1, count - 1);
            double dx = pts[b].Easting - pts[a].Easting, dy = pts[b].Northing - pts[a].Northing;
            double len = Math.Sqrt(dx * dx + dy * dy);
            return len < 1e-9 ? (0, 0) : (dy / len, -dx / len);
        }
        (double E, double N) At(int i)
        {
            var (re, rn) = Right(i);
            return (pts[i].Easting + re * distAway, pts[i].Northing + rn * distAway);
        }

        if (!closed)
        {
            // Straight on before the start, along the first stretch.
            var (e0, n0) = At(0);
            double dx = pts[1].Easting - pts[0].Easting, dy = pts[1].Northing - pts[0].Northing;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len > 1e-9)
                for (double s = CurveProcessing.DefaultExtension; s > 0; s -= Step)
                    yield return (e0 - dx / len * s, n0 - dy / len * s);
        }

        double since = Step;
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                double dx = pts[i].Easting - pts[i - 1].Easting, dy = pts[i].Northing - pts[i - 1].Northing;
                since += Math.Sqrt(dx * dx + dy * dy);
            }
            if (since >= Step || i == count - 1)
            {
                since = 0;
                yield return At(i);
            }
        }

        if (!closed)
        {
            // Straight on past the end, along the last stretch.
            var (e1, n1) = At(count - 1);
            double dx = pts[count - 1].Easting - pts[count - 2].Easting, dy = pts[count - 1].Northing - pts[count - 2].Northing;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len > 1e-9)
                for (double s = Step; s <= CurveProcessing.DefaultExtension; s += Step)
                    yield return (e1 + dx / len * s, n1 + dy / len * s);
        }
    }
}
