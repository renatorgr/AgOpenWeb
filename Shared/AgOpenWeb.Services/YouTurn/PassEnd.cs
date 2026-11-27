// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Pipeline;

namespace AgOpenWeb.Services.YouTurn;

/// <summary>
/// The end of a pass: where it leaves the turn area ahead of the tractor, found once and the same
/// way by everything that needs it (<see cref="PassEnds.Find"/>). <see cref="OutsideIndex"/> is the
/// pass's first point outside the turn area after it was inside, <see cref="InsideIndex"/> the point
/// before it, <see cref="Crossing"/> where the segment between them crosses the turn line, and
/// <see cref="Heading"/> the pass's heading there, in the order of its points.
/// </summary>
public readonly record struct PassEnd(int OutsideIndex, int InsideIndex, Vec2 Crossing, double Heading);

/// <summary>
/// The one definition of where a pass ends, as AgOpenGPS finds it (CYouTurn.FindCurveTurnPoint:
/// walking the pass from the tractor's point, the first point outside the turn line). Walking the
/// pass itself, not a straight line along the heading: on the outside of a bend a straight line
/// leaves the field while the pass runs on (field job J10). The pass must have been inside first:
/// the tractor driving in over the turn line at the start of a job is outside at first, and the
/// crossing under it is where the pass begins, not where it ends (C11, C15, J12, J20). The state
/// machine's distance to the turn, the turn builder's arc start and the fallback turn are all
/// measured from this end.
/// <para>
/// A stretch outside the turn line that stays inside the fence, with the pass back inside the turn
/// line after it, is driven through, not turned at: the end of a pass is the turn line's crossing
/// that the fence's crossing follows. The turn line is the fence moved in by the U-turn distance,
/// a margin for turning, not for driving along an edge: on Olival (field job J19) pass -18 runs
/// along a slanted edge 1.2–2.1 m inside the fence for 26 m, with 180 m of field beyond, and every
/// search took the first point outside the turn line for its end, 49 m into the pass; the turn
/// there landed on a pass 11 m short of its own end, and the job stopped. AgOpenGPS turns at the
/// first point outside the turn line. A stretch that leaves the fence is an end, at the turn
/// line's crossing before it, as at any ordinary end of the field; so is one after which the pass
/// is back inside the turn line for less than <see cref="DriveOnLength"/> (J14).
/// </para>
/// <para>
/// Beside a hard fence the stretch is driven with the whole rig, not with a line: where the
/// implement or the tractor's wheels would be across the fence (<see cref="RigFits"/>), the pass
/// ends, as where it leaves the fence itself. On a tester's field (Tapada da Casa, a 3 m drill,
/// 27 Nov 2026) a pass runs 0.7 m from the tip of a bay of the fence, 60 m from one end of the
/// pass and 500 m from the other:
/// the line stays inside, so it was driven through, and the hard fence guard took AutoSteer off
/// with the drill's end 0.8 m over the fence ahead; every job with the drill ended on that pass.
/// Fork only.
/// </para>
/// </summary>
public static class PassEnds
{
    /// <summary>
    /// The end of <paramref name="pass"/> ahead of index <paramref name="fromIndex"/>, walking the
    /// points in their order (<paramref name="forward"/>) or against it. Null when the pass does not
    /// leave the turn area ahead (past its last crossing, or never inside). With
    /// <paramref name="insideField"/> (the fence) a stretch outside the turn line but inside the
    /// fence is driven through; without it every point outside the turn line is an end. With
    /// <paramref name="rigFits"/> (<see cref="RigFits"/>: a hard fence) such a stretch is driven
    /// through only where the rig fits beside the fence.
    /// </summary>
    public static PassEnd? Find(IReadOnlyList<Vec3> pass, int fromIndex, bool forward,
        Func<Vec3, bool> insideTurnArea, IReadOnlyList<Vec3>? turnLine = null, Func<Vec3, bool>? insideField = null,
        Action<string>? debug = null, Func<Vec3, Vec3, bool>? rigFits = null)
    {
        int step = forward ? 1 : -1;
        bool seenInside = false;
        for (int j = fromIndex; j >= 0 && j < pass.Count; j += step)
        {
            if (insideTurnArea(pass[j])) { seenInside = true; continue; }
            if (!seenInside) continue;
            if (insideField != null)
            {
                var why = IsClip(pass, j, step, insideTurnArea, insideField, rigFits, out int back);
                if (back != j && RunInside(pass, back, step, insideTurnArea) is var run && run < DriveOnLength)
                {
                    why += FormattableString.Invariant($", but inside it for only {run:F1} m: the end of the pass");
                    back = j;
                }
                debug?.Invoke(FormattableString.Invariant($"point {j} ({pass[j].Easting:F1},{pass[j].Northing:F1}) is outside the turn line: {why}"));
                if (back != j)
                {
                    j = back; // back inside the turn line; the walk goes on from there
                    continue;
                }
            }
            int i = j - step;
            var a = pass[i];
            var b = pass[j];
            var crossing = turnLine != null ? CrossingOf(a, b, turnLine) : null;
            // The pass's heading at the end, in the order of its points.
            var (p, q) = forward ? (a, b) : (b, a);
            double heading = Math.Atan2(q.Easting - p.Easting, q.Northing - p.Northing);
            if (heading < 0) heading += 2 * Math.PI;
            return new PassEnd(j, i, crossing ?? new Vec2((a.Easting + b.Easting) / 2, (a.Northing + b.Northing) / 2), heading);
        }
        return null;
    }

    /// <summary>
    /// Whether the stretch of <paramref name="pass"/> outside the turn line that starts at
    /// <paramref name="first"/> (its first point outside, walking by <paramref name="step"/>) is
    /// driven through, as <see cref="Find"/> decides it: no point of it outside
    /// <paramref name="insideField"/>, the rig fitting beside a hard fence all along it
    /// (<paramref name="rigFits"/>), and the pass back inside the turn line at
    /// <paramref name="back"/> for at least <see cref="DriveOnLength"/>. The next pass of a turn is
    /// asked the same question as the tractor's own (<c>FindCurveOutTurnPoint</c>), so both ends of
    /// a turn are ends the pass is turned at when it is driven.
    /// </summary>
    public static bool IsDrivenThrough(IReadOnlyList<Vec3> pass, int first, int step,
        Func<Vec3, bool> insideTurnArea, Func<Vec3, bool> insideField, out int back, Func<Vec3, Vec3, bool>? rigFits = null)
    {
        IsClip(pass, first, step, insideTurnArea, insideField, rigFits, out back);
        if (back == first) return false;
        if (RunInside(pass, back, step, insideTurnArea) >= DriveOnLength) return true;
        back = first;
        return false;
    }

    /// <summary>
    /// How much of the pass must lie inside the turn line after a stretch outside it for the stretch
    /// to be driven through: the landing length (<see cref="YouTurnPathingService.MinLandingLength"/>),
    /// room to work. A pass that dips out over a wiggle of the edge and is back inside for a few
    /// metres before it leaves for good ends at the first crossing: driven through, the end moved
    /// 10 m on into a corner, where the next pass had no room, and the job stopped at its first
    /// turn (field job J14 on Baetas, pass -23: back inside for 4 m, 2026-11-16).
    /// </summary>
    public const double DriveOnLength = YouTurnPathingService.MinLandingLength;

    /// <summary>
    /// Metres of the pass inside the turn area from <paramref name="from"/> on, up to
    /// <see cref="DriveOnLength"/>; a pass whose points end while inside counts as long enough.
    /// </summary>
    private static double RunInside(IReadOnlyList<Vec3> pass, int from, int step, Func<Vec3, bool> insideTurnArea)
    {
        double length = 0;
        for (int k = from; k + step >= 0 && k + step < pass.Count; k += step)
        {
            if (!insideTurnArea(pass[k + step])) return length;
            length += Distance(pass[k], pass[k + step]);
            if (length >= DriveOnLength) return length;
        }
        return DriveOnLength;
    }

    /// <summary>
    /// Whether the stretch outside the turn line from <paramref name="first"/> (the first point
    /// outside) is driven through: back inside the turn line at <paramref name="back"/> with no
    /// point of it outside the fence, and none where the rig does not fit beside a hard fence
    /// (<paramref name="rigFits"/>, asked for each piece of the stretch, from the last point
    /// inside the turn line before it to the first one after it). Says what it decided, for the
    /// debug log.
    /// </summary>
    private static string IsClip(IReadOnlyList<Vec3> pass, int first, int step,
        Func<Vec3, bool> insideTurnArea, Func<Vec3, bool> insideField, Func<Vec3, Vec3, bool>? rigFits, out int back)
    {
        back = first;
        double length = 0;
        for (int k = first; k >= 0 && k < pass.Count; k += step)
        {
            if (!insideField(pass[k]))
                return FormattableString.Invariant($"the end of the pass, outside the fence at point {k} ({pass[k].Easting:F1},{pass[k].Northing:F1}), {length:F1} m on");
            if (k != first) length += Distance(pass[k - step], pass[k]);
            int before = k - step;
            if (rigFits != null && before >= 0 && before < pass.Count && !rigFits(pass[before], pass[k]))
                return FormattableString.Invariant($"the end of the pass, the rig does not fit beside the hard fence before point {k} ({pass[k].Easting:F1},{pass[k].Northing:F1}), {length:F1} m on");
            if (insideTurnArea(pass[k]))
            {
                back = k;
                return FormattableString.Invariant($"inside the fence, driven through: back inside the turn line at point {k} ({pass[k].Easting:F1},{pass[k].Northing:F1}), {length:F1} m on");
            }
        }
        return "the end of the pass, the pass ends outside the turn line";
    }

    /// <summary>
    /// How far either side of a pass the rig needs inside a hard fence to be driven along it: half
    /// the implement and its offset (the reach the hard fence guard looks at) less what the guard
    /// lets an end go over a fence (<see cref="HardFenceGuard.Margin"/>), or half the tractor's
    /// track when that is more.
    /// </summary>
    public static double RigHalfWidth(ConfigurationStore config) =>
        Math.Max(config.ActualToolWidth / 2 + Math.Abs(config.Tool.Offset) - HardFenceGuard.Margin, config.Vehicle.TrackWidth / 2);

    /// <summary>
    /// The test a stretch outside the turn line must pass, piece by piece, to be driven through
    /// beside <paramref name="fence"/>: from one point of the pass to the next, the band
    /// <paramref name="halfWidth"/> either side of it lies inside the fence. Null for a soft
    /// fence (an implement may hang over it) or no width: the stretch is then judged by the
    /// pass's line alone, as before.
    /// </summary>
    public static Func<Vec3, Vec3, bool>? RigFits(BoundaryPolygon? fence, double halfWidth)
    {
        if (fence is not { IsHard: true, IsValid: true } || !(halfWidth > 0) || !double.IsFinite(halfWidth)) return null;
        return (a, b) => FitsBetween(fence, a, b, halfWidth);
    }

    /// <summary>
    /// Whether the band <paramref name="half"/> either side of a–b is inside the fence: its two
    /// edges and the bar across each end. The fence is one ring and the pass's points are inside
    /// it, so the fence gets into the band only across one of those four lines.
    /// </summary>
    private static bool FitsBetween(BoundaryPolygon fence, Vec3 a, Vec3 b, double half)
    {
        double de = b.Easting - a.Easting, dn = b.Northing - a.Northing;
        double len = Math.Sqrt(de * de + dn * dn);
        if (len < 1e-6) return true;
        // To the right of a -> b, the band's half width long.
        double re = dn / len * half, rn = -de / len * half;
        return LineInside(fence, a.Easting - re, a.Northing - rn, a.Easting + re, a.Northing + rn)
            && LineInside(fence, b.Easting - re, b.Northing - rn, b.Easting + re, b.Northing + rn)
            && LineInside(fence, a.Easting - re, a.Northing - rn, b.Easting - re, b.Northing - rn)
            && LineInside(fence, a.Easting + re, a.Northing + rn, b.Easting + re, b.Northing + rn);
    }

    /// <summary>Whether the line from (e1, n1) to (e2, n2) is wholly inside the fence.</summary>
    private static bool LineInside(BoundaryPolygon fence, double e1, double n1, double e2, double n2)
    {
        double de = e2 - e1, dn = n2 - n1;
        double half = Math.Sqrt(de * de + dn * dn) / 2;
        if (half < 1e-6) return fence.IsPointInside(e1, n1);
        // The polygon's test takes a section: a line across a heading, half its length either side of its middle.
        double heading = Math.Atan2(de, dn) - Math.PI / 2;
        var status = fence.GetSegmentBoundaryStatus(new Vec2((e1 + e2) / 2, (n1 + n2) / 2), heading, half);
        return status.IsFullyInside && !status.CrossesBoundary;
    }

    /// <summary>Where the segment a–b crosses <paramref name="turnLine"/> (its closing segment included); null when it doesn't.</summary>
    private static Vec2? CrossingOf(Vec3 a, Vec3 b, IReadOnlyList<Vec3> turnLine)
    {
        int n = turnLine.Count;
        for (int k = 0; k < n; k++)
        {
            var h1 = turnLine[k];
            var h2 = turnLine[(k + 1) % n];
            var at = GeometryMath.TryGetSegmentIntersection(a.Easting, a.Northing, b.Easting, b.Northing,
                h1.Easting, h1.Northing, h2.Easting, h2.Northing);
            if (at.HasValue) return at;
        }
        return null;
    }

    /// <summary>The index of the pass point nearest <paramref name="e"/>, <paramref name="n"/>.</summary>
    public static int NearestIndex(IReadOnlyList<Vec3> pass, double e, double n)
    {
        int near = 0;
        double best = double.MaxValue;
        for (int i = 0; i < pass.Count; i++)
        {
            double de = pass[i].Easting - e, dn = pass[i].Northing - n;
            double d = de * de + dn * dn;
            if (d < best) { best = d; near = i; }
        }
        return near;
    }

    /// <summary>
    /// Metres along the pass from a point abreast of (<paramref name="e"/>, <paramref name="n"/>),
    /// whose nearest pass point is <paramref name="fromIndex"/>, to <paramref name="end"/>'s crossing;
    /// negative past it.
    /// </summary>
    public static double DistanceAlong(IReadOnlyList<Vec3> pass, int fromIndex, bool forward, double e, double n, PassEnd end)
    {
        int step = forward ? 1 : -1;
        double d = 0;
        for (int k = fromIndex; k != end.InsideIndex && k + step >= 0 && k + step < pass.Count; k += step)
            d += Distance(pass[k], pass[k + step]);
        var inside = pass[end.InsideIndex];
        double ce = end.Crossing.Easting - inside.Easting, cn = end.Crossing.Northing - inside.Northing;
        d += Math.Sqrt(ce * ce + cn * cn);
        // Less how far the point is along the pass past its nearest pass point.
        int next = fromIndex + step >= 0 && fromIndex + step < pass.Count ? fromIndex + step : fromIndex - step;
        if (next >= 0 && next < pass.Count && next != fromIndex)
        {
            var p = pass[fromIndex];
            double ue = pass[next].Easting - p.Easting, un = pass[next].Northing - p.Northing;
            double len = Math.Sqrt(ue * ue + un * un);
            if (len > 1e-9)
            {
                double along = ((e - p.Easting) * ue + (n - p.Northing) * un) / len;
                d -= next == fromIndex + step ? along : -along;
            }
        }
        return d;
    }

    private static double Distance(Vec3 a, Vec3 b)
    {
        double de = a.Easting - b.Easting, dn = a.Northing - b.Northing;
        return Math.Sqrt(de * de + dn * dn);
    }
}
