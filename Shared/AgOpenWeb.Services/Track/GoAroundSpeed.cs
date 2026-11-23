// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services.Track;

/// <summary>
/// How far a tractor steered by Pure Pursuit cuts a bend round a Go around obstacle at a given
/// speed, and the fastest speed that keeps the cut inside the safety margin. The answer comes
/// from driving the bend in a small model of the app's own steering: the goal point the look-ahead
/// distance along the line steered (<c>GpsPipelineService.GoalLookAheadFor</c>, on the line), the
/// Pure Pursuit angle (<c>TrackGuidanceService</c>), the wheels answering late and no faster than
/// a steering valve turns them, and the tractor as a bicycle of its wheelbase. Fork only.
/// <para>
/// Pure Pursuit aims at a point the look-ahead ahead, and the look-ahead grows with the speed
/// (3 m standing, 5.3 m at 10 km/h with the default Steer Response): the tractor starts each
/// curve of the bend that much early and cuts it, and the curve where the bend comes level beside
/// the obstacle is cut toward the obstacle. On the closed-loop obstacle rig (the real GPS cycle
/// steering a bicycle model) a tree's 5 m bend lost 0.2 m of its margin from 4 to 12 km/h with
/// Steer Response 3, and twice that with 4; how fast the wheels turn (15, 25 or 35°/s, answering
/// 0.2 s late) changed it by less than a centimetre, since the look-ahead spreads the bend over
/// metres the wheels have time for.
/// </para>
/// </summary>
public static class GoAroundSpeed
{
    /// <summary>How fast the model's wheels turn, degrees per second: the slow end of the Steer Wizard's choices.</summary>
    public const double WheelRateDegS = 15;

    /// <summary>How late the model's wheels answer, seconds: the steering the Steer Wizard measured on a tractor (0.19 s).</summary>
    public const double SteerDelayS = 0.2;

    /// <summary>The model's time step, seconds.</summary>
    public const double StepS = 0.05;

    /// <summary>
    /// The margin left for what the model leaves out (the position's own noise, slack in the
    /// steering: a tractor rests 4 to 9 cm from where it was asked to), metres.
    /// </summary>
    public const double Reserve = 0.1;

    /// <summary>The least cut allowed, metres, whatever the margin.</summary>
    public const double MinAllowed = 0.05;

    /// <summary>How the tractor is steered: its wheelbase and steering limit, and the Pure Pursuit look-ahead settings.</summary>
    /// <param name="Wheelbase">Metres.</param>
    /// <param name="MaxSteerDeg">The steering's limit, degrees.</param>
    /// <param name="Hold">The look-ahead hold (Steer Response), metres.</param>
    /// <param name="Mult">The look-ahead's speed multiplier.</param>
    /// <param name="MinLookAhead">The least look-ahead, metres.</param>
    public readonly record struct Follower(double Wheelbase, double MaxSteerDeg, double Hold, double Mult, double MinLookAhead)
    {
        /// <summary>The look-ahead on the line at <paramref name="kmh"/>, as the pipeline's (AgOpenGPS CVehicle.UpdateGoalPointDistance).</summary>
        public double LookAhead(double kmh) => Math.Max(MinLookAhead, Math.Abs(kmh) * 0.05 * Mult * Hold + Hold);
    }

    /// <summary>The cut allowed with a safety margin of <paramref name="margin"/> metres.</summary>
    public static double Allowed(double margin) => Math.Max(MinAllowed, margin - Reserve);

    /// <summary>
    /// How far the tractor's pivot gets into <paramref name="fence"/> (the obstacle grown by the
    /// keep-out: half the implement and the safety margin) beyond where the line steered itself
    /// goes (a front implement's line runs inside the bend by design), driving
    /// <paramref name="line"/> at <paramref name="kmh"/> from on it, straight, well before the
    /// bend (the points <paramref name="bendFrom"/> to <paramref name="bendTo"/>, entered from
    /// the first forward or the last backward) until past it. Metres of the safety margin used;
    /// 0 or less: none.
    /// </summary>
    public static double Cut(IReadOnlyList<Vec3> line, double[] stations, int bendFrom, int bendTo, IReadOnlyList<Vec2> fence,
        bool forward, double kmh, Follower f)
    {
        if (line.Count < 2 || kmh <= 0 || fence.Count < 3) return 0;
        double v = kmh / 3.6, ld = f.LookAhead(kmh), dir = forward ? 1 : -1;
        double sStart = forward ? stations[bendFrom] - ld - 10 : stations[bendTo] + ld + 10;
        sStart = Math.Clamp(sStart, 0, stations[^1]);
        double sEnd = forward ? stations[bendTo] + 2 : stations[bendFrom] - 2;

        var (e, n, hd) = At(line, stations, sStart);
        double h = forward ? hd : hd + Math.PI; // heading, radians clockwise from north
        double steer = 0;
        int delaySteps = (int)Math.Round(SteerDelayS / StepS);
        var asked = new Queue<double>(delaySteps + 1);
        int seg = SegmentAt(stations, sStart);
        double worst = double.NegativeInfinity;
        double maxSteer = f.MaxSteerDeg > 0 ? f.MaxSteerDeg : 45;
        double rate = WheelRateDegS * StepS;
        var (minE, maxE, minN, maxN) = Box(fence);
        // Long enough to get from the start to past the obstacle at the speed, twice over.
        int steps = (int)Math.Ceiling(2 * (Math.Abs(sEnd - sStart) + 1) / (v * StepS)) + 10;
        for (int k = 0; k < steps; k++)
        {
            seg = Nearest(line, seg, e, n);
            var (s, _) = Project(line, stations, seg, e, n);
            if (forward ? s > sEnd : s < sEnd) break;
            if (e > minE - 2 && e < maxE + 2 && n > minN - 2 && n < maxN + 2)
            {
                var (le, ln, _) = At(line, stations, Math.Clamp(s, 0, stations[^1]));
                worst = Math.Max(worst, Depth(fence, e, n) - Math.Max(0, Depth(fence, le, ln)));
            }

            // Pure Pursuit: the goal point the look-ahead along the line from the nearest point.
            var (ge, gn, _) = At(line, stations, Math.Clamp(s + dir * ld, 0, stations[^1]));
            double dx = ge - e, dy = gn - n, d2 = dx * dx + dy * dy;
            double latRight = dx * Math.Cos(h) - dy * Math.Sin(h);
            double want = d2 > 1e-6 ? Math.Atan(2 * latRight * f.Wheelbase / d2) * 180 / Math.PI : 0;
            want = Math.Clamp(want, -maxSteer, maxSteer);
            asked.Enqueue(want);
            double now = asked.Count > delaySteps ? asked.Dequeue() : 0;
            steer += Math.Clamp(now - steer, -rate, rate);

            h += v / f.Wheelbase * Math.Tan(steer * Math.PI / 180) * StepS;
            e += Math.Sin(h) * v * StepS;
            n += Math.Cos(h) * v * StepS;
        }
        return double.IsNegativeInfinity(worst) ? 0 : worst;
    }

    // How far (e, n) is inside the ring, negative outside.
    private static double Depth(IReadOnlyList<Vec2> ring, double e, double n)
    {
        double d = ObstacleDetour.DistanceToRing(ring, e, n);
        return GeometryMath.IsPointInPolygon(ring, e, n) ? d : -d;
    }

    private static (double MinE, double MaxE, double MinN, double MaxN) Box(IReadOnlyList<Vec2> ring)
    {
        double minE = double.MaxValue, maxE = double.MinValue, minN = double.MaxValue, maxN = double.MinValue;
        foreach (var p in ring)
        {
            minE = Math.Min(minE, p.Easting); maxE = Math.Max(maxE, p.Easting);
            minN = Math.Min(minN, p.Northing); maxN = Math.Max(maxN, p.Northing);
        }
        return (minE, maxE, minN, maxN);
    }

    /// <summary>
    /// The fastest speed, in whole km/h from <paramref name="belowKmh"/> down to
    /// <paramref name="lowestKmh"/>, at which <paramref name="cut"/> stays within
    /// <paramref name="allowed"/>; 0 when none does.
    /// </summary>
    public static int SafeKmh(Func<double, double> cut, double allowed, double belowKmh, int lowestKmh = 2)
    {
        for (int kmh = (int)Math.Floor(belowKmh); kmh >= lowestKmh; kmh--)
            if (cut(kmh) <= allowed) return kmh;
        return 0;
    }

    /// <summary>The distance along <paramref name="line"/> to each of its points.</summary>
    public static double[] Stations(IReadOnlyList<Vec3> line)
    {
        var s = new double[line.Count];
        for (int i = 1; i < line.Count; i++)
        {
            double de = line[i].Easting - line[i - 1].Easting, dn = line[i].Northing - line[i - 1].Northing;
            s[i] = s[i - 1] + Math.Sqrt(de * de + dn * dn);
        }
        return s;
    }

    // The point at station s and the line's heading there (radians clockwise from north).
    private static (double E, double N, double H) At(IReadOnlyList<Vec3> line, double[] stations, double s)
    {
        int i = SegmentAt(stations, s);
        var a = line[i];
        var b = line[i + 1];
        double len = stations[i + 1] - stations[i];
        double t = len > 1e-9 ? Math.Clamp((s - stations[i]) / len, 0, 1) : 0;
        return (a.Easting + t * (b.Easting - a.Easting), a.Northing + t * (b.Northing - a.Northing),
            Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing));
    }

    // The segment (its first point) holding station s.
    private static int SegmentAt(double[] stations, double s)
    {
        int i = Array.BinarySearch(stations, s);
        if (i < 0) i = ~i - 1;
        return Math.Clamp(i, 0, stations.Length - 2);
    }

    /// <summary>The segment of <paramref name="line"/> nearest (<paramref name="e"/>, <paramref name="n"/>), looked for along the whole line.</summary>
    public static int NearestSegment(IReadOnlyList<Vec3> line, double e, double n)
    {
        int best = 0;
        double bestD = double.MaxValue;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            double d = SegmentDistance2(line[i], line[i + 1], e, n);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>
    /// Where (<paramref name="e"/>, <paramref name="n"/>) is along <paramref name="line"/>: the
    /// nearest segment walked from <paramref name="seg"/> (updated), its station, and the line's
    /// heading on that segment (radians clockwise from north).
    /// </summary>
    public static (double S, double Heading) Locate(IReadOnlyList<Vec3> line, double[] stations, ref int seg, double e, double n)
    {
        seg = Nearest(line, Math.Clamp(seg, 0, line.Count - 2), e, n);
        var a = line[seg];
        var b = line[seg + 1];
        return (Project(line, stations, seg, e, n).S, Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing));
    }

    // The segment nearest (e, n), walked from seg while that gets nearer.
    private static int Nearest(IReadOnlyList<Vec3> line, int seg, double e, double n)
    {
        double D(int i) => SegmentDistance2(line[i], line[i + 1], e, n);
        double here = D(seg);
        while (seg + 1 < line.Count - 1 && D(seg + 1) < here) here = D(++seg);
        while (seg > 0 && D(seg - 1) < here) here = D(--seg);
        return seg;
    }

    private static double SegmentDistance2(Vec3 a, Vec3 b, double e, double n)
    {
        double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing, len2 = dx * dx + dy * dy;
        double t = len2 > 0 ? Math.Clamp(((e - a.Easting) * dx + (n - a.Northing) * dy) / len2, 0, 1) : 0;
        double px = a.Easting + t * dx - e, py = a.Northing + t * dy - n;
        return px * px + py * py;
    }

    // The station of (e, n) on segment seg and how far it is to the left of the line, the way its points run.
    private static (double S, double LatLeft) Project(IReadOnlyList<Vec3> line, double[] stations, int seg, double e, double n)
    {
        var a = line[seg];
        var b = line[seg + 1];
        double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return (stations[seg], 0);
        double ux = dx / len, uy = dy / len, re = e - a.Easting, rn = n - a.Northing;
        double along = re * ux + rn * uy;
        // Left of a heading clockwise from north: (-cos h, sin h) = (-uy, ux).
        double left = -re * uy + rn * ux;
        return (stations[seg] + along, left);
    }
}
