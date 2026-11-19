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
/// <para>The same look-ahead for a hard obstacle (<see cref="HeadingIn"/>), used while AutoSteer is
/// bringing the tractor onto a line from the side: on the line the pass itself keeps the implement
/// out of a hard obstacle (its turn line, or the bend round it), off it nothing did. The line moved a
/// pass over beside a hard obstacle took the end of a 12 m boom, 4.5 m ahead of the pivot, 1.2 m into
/// it, the tractor crossing 50° off the passes (the beta robot, simulator).</para>
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

    /// <summary>
    /// How far <paramref name="point"/> lies ahead of (<paramref name="easting"/>, <paramref name="northing"/>)
    /// along <paramref name="heading"/> (radians, 0 = north); negative behind. The look-ahead stops at the
    /// start of a U-turn planned ahead, which keeps the implement off the fence from there.
    /// </summary>
    public static double AheadOf(Vec3 point, double easting, double northing, double heading)
        => (point.Easting - easting) * Math.Sin(heading) + (point.Northing - northing) * Math.Cos(heading);

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
        var sweep = new Sweep(toolHeading, pivot, curvature);
        double worst = 0;
        foreach (int side in new[] { -1, 1 })
        {
            double e0 = toolCentre.Easting + side * reach * sweep.RightE, n0 = toolCentre.Northing + side * reach * sweep.RightN;
            double now = OutsideBy(outer, e0, n0);
            for (int k = 1; k <= Samples; k++)
            {
                var (e, n) = sweep.At(e0, n0, lookAhead * k / Samples);
                double ahead = OutsideBy(outer, e, n);
                if (ahead > Margin && ahead > now + Rising) worst = Math.Max(worst, ahead);
            }
        }
        return worst;
    }

    /// <summary>Along the implement and along the look-ahead, a point at most this far from the next is
    /// looked at for an obstacle: a tree is narrower than the implement, and between its two ends it
    /// was not seen.</summary>
    private const double ObstacleStep = 1.0;

    /// <summary>How far inside an obstacle a point of the implement must be going for it to count: less
    /// than the fence's <see cref="Margin"/>, a tree being a circle a metre or two across.</summary>
    public const double ObstacleMargin = 0.3;

    /// <summary>
    /// How far inside <paramref name="obstacle"/> a point of the implement will be within
    /// <paramref name="lookAhead"/> metres along the curve the tractor is steering, when it is going
    /// into it: further in there than <see cref="ObstacleMargin"/> and further than it is now. 0 otherwise.
    /// The implement is the line <paramref name="reach"/> either side of <paramref name="toolCentre"/>,
    /// across <paramref name="toolHeading"/>, looked at every metre along it; the curve as in
    /// <see cref="HeadingOut(BoundaryPolygon, Vec3, double, double, double, Vec3, double)"/>.
    /// </summary>
    public static double HeadingIn(BoundaryPolygon obstacle, Vec3 toolCentre, double toolHeading, double reach, double lookAhead,
        Vec3 pivot, double curvature)
    {
        if (obstacle.Points.Count < 3 || lookAhead < MinLookAhead) return 0;
        var sweep = new Sweep(toolHeading, pivot, curvature);
        int parts = Math.Max(1, (int)Math.Ceiling(2 * reach / ObstacleStep));
        int steps = Math.Max(Samples, (int)Math.Ceiling(lookAhead / ObstacleStep));
        double worst = 0;
        for (int i = 0; i <= parts; i++)
        {
            double across = -reach + 2 * reach * i / parts;
            double e0 = toolCentre.Easting + across * sweep.RightE, n0 = toolCentre.Northing + across * sweep.RightN;
            double now = InsideBy(obstacle, e0, n0);
            for (int k = 1; k <= steps; k++)
            {
                var (e, n) = sweep.At(e0, n0, lookAhead * k / steps);
                double ahead = InsideBy(obstacle, e, n);
                if (ahead > ObstacleMargin && ahead > now + Rising) worst = Math.Max(worst, ahead);
            }
        }
        return worst;
    }

    /// <summary>Where a point of the implement goes over the look-ahead: along the tool's heading going
    /// straight, else turned with the tractor about its turning centre.</summary>
    private readonly struct Sweep
    {
        private readonly bool _straight;
        private readonly double _fe, _fn, _ce, _cn, _curvature;

        /// <summary>Right of the tool's heading: (cos h, -sin h).</summary>
        public double RightE => _fn;
        public double RightN => -_fe;

        public Sweep(double toolHeading, Vec3 pivot, double curvature)
        {
            _straight = !double.IsFinite(curvature) || Math.Abs(curvature) < Straight;
            _curvature = curvature;
            _fe = Math.Sin(toolHeading);
            _fn = Math.Cos(toolHeading);
            // The turning centre, on the pivot's right for a right turn (curvature > 0), its left for a left one.
            double radius = _straight ? 0 : 1 / curvature;
            _ce = pivot.Easting + radius * Math.Cos(pivot.Heading);
            _cn = pivot.Northing - radius * Math.Sin(pivot.Heading);
        }

        public (double E, double N) At(double e0, double n0, double d)
        {
            if (_straight) return (e0 + _fe * d, n0 + _fn * d);
            // Turned clockwise (a right turn) by d × curvature about the centre.
            double a = d * _curvature, c = Math.Cos(a), s = Math.Sin(a);
            double de = e0 - _ce, dn = n0 - _cn;
            return (_ce + de * c + dn * s, _cn - de * s + dn * c);
        }
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
        => outer.IsPointInside(e, n) ? 0 : ToOutline(outer, e, n);

    /// <summary>Metres inside the polygon, 0 outside.</summary>
    internal static double InsideBy(BoundaryPolygon polygon, double e, double n)
        => polygon.IsPointInside(e, n) ? ToOutline(polygon, e, n) : 0;

    private static double ToOutline(BoundaryPolygon polygon, double e, double n)
    {
        var pts = polygon.Points;
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
