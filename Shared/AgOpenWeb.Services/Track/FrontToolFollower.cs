// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services.Track;

/// <summary>
/// How AutoSteer follows the line a front implement's pivot steers (<see cref="FrontToolLine"/>)
/// once the tractor is on it (fork only). Pure Pursuit aims at a point the look-ahead ahead, so it
/// starts each bend early and comes out of it early; with the pivot steered that is a few
/// centimetres, but an implement <c>h</c> ahead swings out by the heading the tractor took early
/// times h. On a curve in the simulator (a straight, a quarter bend, a straight, a quarter bend
/// back) an 8 m front boom 4.9 m ahead ran 12 cm off the pass where a 20 m bend starts and ends,
/// a 35 ft combine header 3.56 m ahead the same, with the middle of the bend on the pass.
/// <para>
/// Here the wheels are set for the line's own bend where the pivot is (taken
/// <see cref="LeadSeconds"/> of travel on, as the wheels answer late), plus a correction for the
/// pivot's distance and heading off the line, critically damped over the look-ahead the operator
/// tuned (Steer Response): curvature = line's − distance ÷ L² − 2 sin(heading off) ÷ L. On the
/// same curve: 2 to 3 cm, with a receiver's noise moving the wheels 5 to 10 % more than Pure
/// Pursuit from one fix to the next. Off the line (joining it, a pass moved) Pure Pursuit steers
/// as before: the follower takes over between <see cref="NoBlendDistance"/> and
/// <see cref="FullBlendDistance"/> from the line and between <see cref="NoBlendHeadingDeg"/> and
/// <see cref="FullBlendHeadingDeg"/> off its heading.
/// </para>
/// </summary>
public static class FrontToolFollower
{
    /// <summary>Within this of the line (m) the follower steers alone.</summary>
    public const double FullBlendDistance = 0.5;

    /// <summary>Further than this from the line (m) Pure Pursuit steers alone.</summary>
    public const double NoBlendDistance = 1.0;

    /// <summary>Within this of the line's heading (degrees) the follower steers alone.</summary>
    public const double FullBlendHeadingDeg = 10;

    /// <summary>Further than this off the line's heading (degrees) Pure Pursuit steers alone.</summary>
    public const double NoBlendHeadingDeg = 20;

    /// <summary>The line's bend is taken this much travel ahead of the pivot, s: the wheels answer
    /// about this late (0.19 s measured by the Steer Wizard on a tractor).</summary>
    public const double LeadSeconds = 0.2;

    /// <summary>The bend is measured over at least this either side, m.</summary>
    private const double MinHalfChord = 1.0;

    /// <summary>The nearest stretch is looked for this many points either side of the one given.</summary>
    private const int SearchWindow = 12;

    /// <summary>
    /// The steer angle (degrees, + right; before the side-hill term and the limit) that keeps the
    /// pivot on <paramref name="line"/>, and how much of it to take (1 on the line, 0 away from
    /// it: Pure Pursuit's then); null when the line has no stretch to follow.
    /// </summary>
    /// <param name="line">The line steered, in its own order.</param>
    /// <param name="closed">The line is a loop.</param>
    /// <param name="near">A point near the pivot's stretch (Pure Pursuit's nearest).</param>
    /// <param name="sameWay">The tractor drives the line in its own order.</param>
    /// <param name="pivotE">The pivot, easting (m).</param>
    /// <param name="pivotN">The pivot, northing (m).</param>
    /// <param name="heading">The tractor's heading (rad, clockwise from north).</param>
    /// <param name="lookAhead">The look-ahead the operator tuned, m: the correction is damped over it.</param>
    /// <param name="speed">The speed, m/s.</param>
    /// <param name="wheelbase">The wheelbase, m.</param>
    public static (double SteerDeg, double Weight)? Steer(IReadOnlyList<Vec3> line, bool closed, int near, bool sameWay,
        double pivotE, double pivotN, double heading, double lookAhead, double speed, double wheelbase)
    {
        int n = line.Count;
        int segments = closed ? n : n - 1;
        if (n < 2 || segments < 1 || lookAhead <= 0.1 || wheelbase <= 0) return null;

        // The nearest stretch, near the one Pure Pursuit found.
        int seg = -1;
        double best = double.MaxValue, t = 0;
        for (int k = -SearchWindow; k <= SearchWindow; k++)
        {
            int i = near + k;
            if (closed) i = ((i % n) + n) % n;
            else if (i < 0 || i >= segments) continue;
            var a = line[i];
            var b = line[(i + 1) % n];
            double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing, l2 = dx * dx + dy * dy;
            if (l2 < 1e-12) continue;
            double u = Math.Clamp(((pivotE - a.Easting) * dx + (pivotN - a.Northing) * dy) / l2, 0, 1);
            double ex = a.Easting + u * dx - pivotE, ey = a.Northing + u * dy - pivotN;
            double d2 = ex * ex + ey * ey;
            if (d2 < best) { best = d2; seg = i; t = u; }
        }
        if (seg < 0) return null;

        var p = line[seg];
        var q = line[(seg + 1) % n];
        double sx = q.Easting - p.Easting, sy = q.Northing - p.Northing, len = Math.Sqrt(sx * sx + sy * sy);
        double dir = sameWay ? 1 : -1;
        // Off the line, + right of the way driven; the line's heading the way driven.
        double xte = -dir * (sx * (pivotN - p.Northing) - sy * (pivotE - p.Easting)) / len;
        double lineHeading = Math.Atan2(sx, sy) + (sameWay ? 0 : Math.PI);
        double off = Math.IEEERemainder(heading - lineHeading, 2 * Math.PI);

        double weight = Math.Min(
            Ramp(Math.Abs(xte), FullBlendDistance, NoBlendDistance),
            Ramp(Math.Abs(off) * 180 / Math.PI, FullBlendHeadingDeg, NoBlendHeadingDeg));
        if (weight <= 0) return (0, 0);

        // The line's bend the way driven, LeadSeconds of travel on.
        double half = Math.Max(MinHalfChord, len);
        double at = t * len + dir * LeadSeconds * Math.Max(0, speed);
        double behind = HeadingAt(line, closed, seg, at - half);
        double ahead = HeadingAt(line, closed, seg, at + half);
        double bend = dir * Math.IEEERemainder(ahead - behind, 2 * Math.PI) / (2 * half);

        double curvature = bend - xte / (lookAhead * lookAhead) - 2 * Math.Sin(off) / lookAhead;
        return (Math.Atan(curvature * wheelbase) * 180 / Math.PI, weight);
    }

    // 1 up to full, 0 from none on, a straight line between.
    private static double Ramp(double x, double full, double none) =>
        x <= full ? 1 : x >= none ? 0 : (none - x) / (none - full);

    /// <summary>The heading (rad) of the stretch <paramref name="along"/> m from the start of
    /// stretch <paramref name="seg"/> in the line's own order (negative: back), the end stretch
    /// past an open line's end.</summary>
    private static double HeadingAt(IReadOnlyList<Vec3> line, bool closed, int seg, double along)
    {
        int n = line.Count;
        int segments = closed ? n : n - 1;
        int i = seg;
        double s = along;
        for (int guard = 0; guard < 4 * n; guard++)
        {
            var a = line[i];
            var b = line[(i + 1) % n];
            double l = Math.Sqrt((b.Easting - a.Easting) * (b.Easting - a.Easting) + (b.Northing - a.Northing) * (b.Northing - a.Northing));
            bool openEnd = !closed && (s < 0 && i == 0 || s > l && i == segments - 1);
            if (l > 1e-9 && (s >= 0 && s <= l || openEnd))
                return Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing);
            if (openEnd) break;
            if (s >= 0)
            {
                s -= l;
                i = closed ? (i + 1) % n : i + 1;
            }
            else
            {
                i = closed ? (i - 1 + n) % n : i - 1;
                var pa = line[i];
                var pb = line[(i + 1) % n];
                s += Math.Sqrt((pb.Easting - pa.Easting) * (pb.Easting - pa.Easting) + (pb.Northing - pa.Northing) * (pb.Northing - pa.Northing));
            }
        }
        var x = line[seg];
        var y = line[(seg + 1) % n];
        return Math.Atan2(y.Easting - x.Easting, y.Northing - x.Northing);
    }
}
