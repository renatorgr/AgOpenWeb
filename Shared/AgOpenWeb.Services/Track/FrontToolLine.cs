// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services.Track;

/// <summary>
/// The line the pivot steers so that a front-mounted implement runs on the pass round a bend
/// (fork only). AutoSteer steers the tractor's pivot on the line; an implement fixed
/// <c>h</c> metres ahead of it, on a bend of radius R, runs outside it by √(R² + h²) − R
/// (4.5 m ahead: 0.2 m on a 50 m bend, 0.5 m on 20 m, 1.3 m on 8 m), so the work leaves gaps on
/// the inside of a curve and overlaps on the outside, and a lap along a fence takes the boom
/// across it. Steered on a bend of radius √(R² − h²) instead, the pivot keeps the implement on
/// the pass: each point of the line is moved towards the bend's centre by R − √(R² − h²), with
/// R the bend where the implement is (half way between the pivot and it, over a few metres
/// either side), never tighter than the tractor turns.
/// </summary>
public static class FrontToolLine
{
    /// <summary>A line moved less than this nowhere is steered as it is, m.</summary>
    public const double MinShift = 0.01;

    /// <summary>The bend is measured over at least this either side, m.</summary>
    public const double MinWindow = 3.0;

    /// <summary>The pass's heading at a point is taken over this either side of it, m.</summary>
    private const double Chord = 2.0;

    /// <summary>
    /// The pass moved in round its bends for an implement <paramref name="reach"/> metres ahead
    /// of the pivot; null when it would move less than <see cref="MinShift"/> anywhere (straight).
    /// A closed pass's points are the pass's, one for one (PassUnfit lines up with them); an open
    /// pass gets points every <see cref="StretchStep"/> near the ends of a long straight stretch
    /// (<see cref="WithPointsNearStretchEnds"/>).
    /// </summary>
    /// <param name="pass">The pass steered, in its own order.</param>
    /// <param name="closed">The pass is a loop.</param>
    /// <param name="reach">How far ahead of the pivot the implement is, m (positive).</param>
    /// <param name="minRadius">The tightest the tractor turns, m.</param>
    public static List<Vec3>? Steered(IReadOnlyList<Vec3> pass, bool closed, double reach, double minRadius)
    {
        if (pass.Count < 3 || reach <= 0.05) return null;
        if (!closed) pass = WithPointsNearStretchEnds(pass, Math.Max(reach, MinWindow) + reach / 2 + Chord + StretchStep);
        int n = pass.Count;

        // Along the pass, and its heading on each stretch.
        var cum = new double[n];
        for (int i = 1; i < n; i++) cum[i] = cum[i - 1] + GeometryMath.Distance(pass[i - 1], pass[i]);
        double length = cum[n - 1];
        double loop = closed ? length + GeometryMath.Distance(pass[n - 1], pass[0]) : length;
        if (loop < 1e-6) return null;

        double window = Math.Max(reach, MinWindow);
        double lead = reach / 2;
        var shifts = new double[n];
        double most = 0;
        for (int i = 0; i < n; i++)
        {
            double s = cum[i] + lead;
            double turn = AngleDiff(HeadingAt(s + window), HeadingAt(s - window));
            double k = turn / (2 * window); // + turning right (clockwise)
            if (Math.Abs(k) < 1e-6) continue;
            double r = 1 / Math.Abs(k);
            double pivotR = Math.Sqrt(Math.Max(r * r - reach * reach, 0));
            pivotR = Math.Max(pivotR, minRadius);
            double d = r - pivotR;
            if (d <= 0) continue;
            shifts[i] = Math.Sign(k) * d;
            most = Math.Max(most, d);
        }
        if (most < MinShift) return null;

        var line = new List<Vec3>(n);
        for (int i = 0; i < n; i++)
        {
            var p = pass[i];
            double h = HeadingAt(cum[i]);
            // Right of a heading h is (cos h, -sin h).
            line.Add(new Vec3(p.Easting + shifts[i] * Math.Cos(h), p.Northing - shifts[i] * Math.Sin(h), p.Heading));
        }
        return line;

        // The way the pass runs at s, over a chord either side: a pass that doubles back a little
        // where a loop's ends overlap still has its own heading there.
        double HeadingAt(double s)
        {
            var a = PointAt(s - Chord);
            var b = PointAt(s + Chord);
            return Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing);
        }

        Vec2 PointAt(double s)
        {
            if (closed) s = ((s % loop) + loop) % loop;
            else s = Math.Clamp(s, 0, length);
            int i = Array.BinarySearch(cum, s);
            if (i < 0) i = ~i - 1;
            i = Math.Clamp(i, 0, n - 1);
            var a = pass[i];
            var b = i < n - 1 ? pass[i + 1] : closed ? pass[0] : pass[i];
            double segLen = (i < n - 1 ? cum[i + 1] : loop) - cum[i];
            double t = segLen > 1e-9 ? Math.Clamp((s - cum[i]) / segLen, 0, 1) : 0;
            return new Vec2(a.Easting + (b.Easting - a.Easting) * t, a.Northing + (b.Northing - a.Northing) * t);
        }
    }

    /// <summary>The spacing of the points added near the ends of a long straight stretch, m.</summary>
    public const double StretchStep = 1.0;

    /// <summary>
    /// The pass with a point every <see cref="StretchStep"/> along the first and last
    /// <paramref name="span"/> metres of every stretch longer than the span. A pass bent round Go
    /// around obstacles is straight between its bends, one stretch often hundreds of metres long:
    /// the point that starts it moved with the bend and the far one did not, so the line steered
    /// ran slanted along the whole stretch (on a tester's field, simulator, the tractor and its
    /// front boom 0.40 m beside the pass for 100 m after the last tree, to the end of the pass).
    /// The span is as far as a bend moves the line past its end.
    /// </summary>
    internal static IReadOnlyList<Vec3> WithPointsNearStretchEnds(IReadOnlyList<Vec3> pass, double span)
    {
        List<Vec3>? result = null;
        for (int i = 0; i < pass.Count - 1; i++)
        {
            var a = pass[i];
            var b = pass[i + 1];
            double len = GeometryMath.Distance(a, b);
            if (len <= Math.Max(span, 2 * StretchStep))
            {
                result?.Add(a);
                continue;
            }
            result ??= new List<Vec3>(pass.Take(i));
            result.Add(a);
            for (double d = StretchStep; d < len - StretchStep / 2; d += StretchStep)
            {
                if (d > span && d < len - span) d = Math.Floor((len - span) / StretchStep) * StretchStep;
                if (d <= 0 || d >= len - StretchStep / 2) continue;
                double t = d / len;
                result.Add(new Vec3(a.Easting + (b.Easting - a.Easting) * t, a.Northing + (b.Northing - a.Northing) * t, a.Heading));
            }
        }
        if (result == null) return pass;
        result.Add(pass[^1]);
        return result;
    }

    private static double AngleDiff(double a, double b)
    {
        double d = a - b;
        while (d > Math.PI) d -= 2 * Math.PI;
        while (d < -Math.PI) d += 2 * Math.PI;
        return d;
    }
}
