// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services.Pipeline;

/// <summary>
/// The net under a hard outer boundary when no U-turn is planned or driven: an end of the
/// implement heading out across the fence within the look-ahead takes AutoSteer off with the
/// alarm, while the operator can still stop or steer away. A U-turn keeps the implement off a
/// hard fence, and so does the S AutoSteer drives onto a line beside it (<see cref="SideStep"/>);
/// otherwise AutoSteer steers the shortest way onto a line. Switched on in a corner of the field
/// with the line moved five passes over, it swung an 8 m front boom 0.9 m over the fence
/// (Tapada da Casa, simulator). The look-ahead follows the curve the tractor is steering
/// (<see cref="HeadingOut(BoundaryPolygon, Vec3, double, double, double, Vec3, double)"/>): straight
/// ahead leaves a bend, and on the first lap of a boundary curve, the boom's end on the fence, it took
/// AutoSteer off at a bend with the tractor on the line. Fork only.
/// </summary>
public static class HardFenceGuard
{
    /// <summary>Seconds of travel looked ahead.</summary>
    public const double LookAheadSeconds = 2.0;

    /// <summary>Shorter than this (slower than walking pace) nothing is looked at.</summary>
    public const double MinLookAhead = 1.0;

    /// <summary>
    /// How far outside the fence an implement end must be going for it to count: a fence drawn
    /// on a photo is a little off the ground, and an implement working along it brushes it.
    /// </summary>
    public const double Margin = 0.5;

    /// <summary>Going out by less than this over the look-ahead is driving along the fence, not out across it.</summary>
    private const double Rising = 0.05;

    /// <summary>Points looked at along the look-ahead: a fence that bulges in and out again between here and there counts.</summary>
    private const int Samples = 4;

    /// <summary>
    /// How far outside <paramref name="outer"/> an end of the implement will be within
    /// <paramref name="lookAhead"/> metres along <paramref name="toolHeading"/>, when it is going out
    /// across it: further out there than <see cref="Margin"/> and further than it is now. 0 otherwise.
    /// The ends are <paramref name="reach"/> either side of <paramref name="toolCentre"/>, across the heading.
    /// </summary>
    public static double HeadingOut(BoundaryPolygon outer, Vec3 toolCentre, double toolHeading, double reach, double lookAhead)
        => HeadingOut(outer, toolCentre, toolHeading, reach, lookAhead, default, 0);

    /// <summary>Below this curvature (1/m: a radius over 10 km) the tractor is going straight.</summary>
    private const double Straight = 1e-4;

    /// <summary>
    /// As <see cref="HeadingOut(BoundaryPolygon, Vec3, double, double, double)"/>, along the curve the
    /// tractor is steering: the ends turn with it about its turning centre, 1/<paramref name="curvature"/>
    /// (+ = to the right) to the side of <paramref name="pivot"/>, by the angle it turns over the
    /// look-ahead. A front or rear implement is part of the tractor and goes exactly there. A trailed one
    /// swings out less as the tractor turns away and cuts further in round a bend, so on the outside of a
    /// bend it stays inside this and on the inside it comes a little further in; straight ahead missed both.
    /// Going straight, the ends go along <paramref name="toolHeading"/>, as without a curve.
    /// </summary>
    public static double HeadingOut(BoundaryPolygon outer, Vec3 toolCentre, double toolHeading, double reach, double lookAhead,
        Vec3 pivot, double curvature)
    {
        if (outer.Points.Count < 3 || lookAhead < MinLookAhead) return 0;
        bool straight = !double.IsFinite(curvature) || Math.Abs(curvature) < Straight;
        double fe = Math.Sin(toolHeading), fn = Math.Cos(toolHeading);
        // Right of the heading is (cos h, -sin h).
        double re = fn, rn = -fe;
        // The turning centre, on the pivot's right for a right turn (curvature > 0), its left for a left one.
        double radius = straight ? 0 : 1 / curvature;
        double ce = pivot.Easting + radius * Math.Cos(pivot.Heading), cn = pivot.Northing - radius * Math.Sin(pivot.Heading);
        double worst = 0;
        foreach (int side in new[] { -1, 1 })
        {
            double e0 = toolCentre.Easting + side * reach * re, n0 = toolCentre.Northing + side * reach * rn;
            double now = OutsideBy(outer, e0, n0);
            for (int k = 1; k <= Samples; k++)
            {
                double d = lookAhead * k / Samples;
                double e, n;
                if (straight) { e = e0 + fe * d; n = n0 + fn * d; }
                else
                {
                    // Turned clockwise (a right turn) by d × curvature about the centre.
                    double a = d * curvature, c = Math.Cos(a), s = Math.Sin(a);
                    double de = e0 - ce, dn = n0 - cn;
                    e = ce + de * c + dn * s;
                    n = cn - de * s + dn * c;
                }
                double ahead = OutsideBy(outer, e, n);
                if (ahead > Margin && ahead > now + Rising) worst = Math.Max(worst, ahead);
            }
        }
        return worst;
    }

    /// <summary>
    /// The curvature (1/m, + = to the right) of a tractor steering <paramref name="steerAngleDeg"/>
    /// (+ = right) with <paramref name="wheelbase"/>, the angle held within <paramref name="maxSteerAngleDeg"/>.
    /// 0 without a wheelbase.
    /// </summary>
    public static double Curvature(double steerAngleDeg, double wheelbase, double maxSteerAngleDeg)
    {
        if (!(wheelbase > 0) || !double.IsFinite(steerAngleDeg)) return 0;
        double max = Math.Clamp(maxSteerAngleDeg > 0 ? maxSteerAngleDeg : 45, 1, 60);
        return Math.Tan(Math.Clamp(steerAngleDeg, -max, max) * Math.PI / 180) / wheelbase;
    }

    /// <summary>Metres outside the polygon, 0 inside.</summary>
    internal static double OutsideBy(BoundaryPolygon outer, double e, double n)
    {
        if (outer.IsPointInside(e, n)) return 0;
        var pts = outer.Points;
        double best = double.MaxValue;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
        {
            double ax = pts[j].Easting, ay = pts[j].Northing;
            double dx = pts[i].Easting - ax, dy = pts[i].Northing - ay;
            double len2 = dx * dx + dy * dy;
            double t = len2 > 0 ? Math.Clamp(((e - ax) * dx + (n - ay) * dy) / len2, 0, 1) : 0;
            double px = ax + t * dx - e, py = ay + t * dy - n;
            best = Math.Min(best, px * px + py * py);
        }
        return Math.Sqrt(best);
    }
}
