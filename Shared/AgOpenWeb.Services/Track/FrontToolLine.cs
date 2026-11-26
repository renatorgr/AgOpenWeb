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
/// either side), never tighter than the tractor turns. That is how a closed pass is moved. An
/// open pass is moved to the line the implement's pull draws (<see cref="TowedShifts"/>): the pivot
/// follows the implement as a trailer follows its hitch, which is the same √(R² − h²) on a steady
/// bend and also holds through a short S, such as a Go around bend round a tree; it depends on
/// the way the pass is driven. A closed pass bent round Go around obstacles is moved the same way,
/// as a loop (<see cref="TowedLoop"/>): round its bends' S the window fails as on an open pass.
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
    /// <param name="forward">The tractor drives the pass in its own order (false: against it). An open
    /// pass's line depends on it (<see cref="TowedShifts"/>), and a towed loop's.</param>
    /// <param name="towed">A closed pass moved by the implement's pull all round, as a loop
    /// (<see cref="TowedLoop"/>), not one for one by the window: a lap bent round Go around
    /// obstacles. Open passes are always towed.</param>
    public static List<Vec3>? Steered(IReadOnlyList<Vec3> pass, bool closed, double reach, double minRadius, bool forward = true,
        bool towed = false)
    {
        if (pass.Count < 3 || reach <= 0.05) return null;
        if (closed && towed) return TowedLoop(pass, reach, minRadius, forward);
        // The pull of a bend dies away over a few reaches past it (TowedShifts), so the points added
        // near a stretch's ends reach that far.
        if (!closed) pass = WithPointsNearStretchEnds(pass, Math.Max(Math.Max(reach, MinWindow) + reach / 2 + Chord, TowSettle * reach) + StretchStep);
        int n = pass.Count;

        // Along the pass, and its heading on each stretch.
        var cum = new double[n];
        for (int i = 1; i < n; i++) cum[i] = cum[i - 1] + GeometryMath.Distance(pass[i - 1], pass[i]);
        double length = cum[n - 1];
        double loop = closed ? length + GeometryMath.Distance(pass[n - 1], pass[0]) : length;
        if (loop < 1e-6) return null;

        double window = Math.Max(reach, MinWindow);
        // The implement is ahead in the way the tractor drives.
        double lead = (closed || forward ? 1 : -1) * reach / 2;
        double[] shifts;
        if (closed)
        {
            shifts = new double[n];
            for (int i = 0; i < n; i++)
            {
                double s = cum[i] + lead;
                double turn = AngleDiff(HeadingAt(s + window), HeadingAt(s - window));
                double k = turn / (2 * window); // + turning right (clockwise)
                if (Math.Abs(k) < 1e-6) continue;
                double r = 1 / Math.Abs(k);
                double pivotR = Math.Max(Math.Sqrt(Math.Max(r * r - reach * reach, 0)), minRadius);
                double d = r - pivotR;
                if (d <= 0) continue;
                shifts[i] = Math.Sign(k) * d;
            }
        }
        else
        {
            // An open pass: the line the implement's pull draws (a tractrix), which holds through a
            // short S as well as round a long bend. Measured over a window, a Go around bend's S
            // (out of the pass and back at the curve radius, a few metres each way) averaged out
            // to nearly straight and was hardly moved: the Maxxum's boom 4.8 m ahead cut the first
            // bend by 1.5 m and ran 0.85 m over a 3 m tree (a tester's field, the farm job harness).
            // The pull turns the pivot no tighter than the tractor turns (TowedShifts).
            shifts = TowedShifts(pass, cum, reach, forward, minRadius);
        }
        double most = 0;
        for (int i = 0; i < n; i++) most = Math.Max(most, Math.Abs(shifts[i]));
        if (most < MinShift) return null;

        var line = new List<Vec3>(n);
        for (int i = 0; i < n; i++)
        {
            var p = pass[i];
            double h = HeadingAt(cum[i]);
            // Right of a heading h is (cos h, -sin h).
            line.Add(new Vec3(p.Easting + shifts[i] * Math.Cos(h), p.Northing - shifts[i] * Math.Sin(h), p.Heading));
        }
        // A pass that doubles back tighter than the tractor turns (a hairpin at its turning radius)
        // leaves a pivot line that loops, which a shift beside each point of the pass can't follow:
        // it came out with half-metre kinks. Steered as it is, the pass can at least be driven.
        if (!closed && TightestBend(line) < minRadius * TightestBendShare) return null;
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

    /// <summary>
    /// A closed pass moved by the implement's pull (<see cref="TowedShifts"/>) all round: a lap bent
    /// round Go around obstacles has their S on it, which the window (one shift per point from the
    /// bend over a few metres) averages out to nearly straight, so a front boom cut the bend into
    /// the obstacle as on an open pass. The pull is walked round the lap three times, and the
    /// middle time round is the line: whichever way the lap is driven, a pivot started straight
    /// behind the implement has settled by then, and the headings there are the lap's own. Points every
    /// <see cref="StretchStep"/> near the ends of a long stretch, as an open pass gets
    /// (<see cref="WithPointsNearStretchEnds"/>); the lap's points once each, the first not
    /// repeated at the end. Null when it would move less than <see cref="MinShift"/> anywhere, or
    /// bends tighter than the tractor turns (<see cref="TightestBend"/>). Fork only.
    /// </summary>
    internal static List<Vec3>? TowedLoop(IReadOnlyList<Vec3> lap, double reach, double minRadius, bool forward)
    {
        int count = lap.Count;
        if (count > 2 && lap[count - 1].Easting == lap[0].Easting && lap[count - 1].Northing == lap[0].Northing) count--;
        if (count < 3 || reach <= 0.05) return null;
        // The lap joined at its seam, the points near its stretches' ends added, the seam left once.
        var joined = new List<Vec3>(count + 1);
        for (int i = 0; i < count; i++) joined.Add(lap[i]);
        joined.Add(lap[0]);
        var dense = WithPointsNearStretchEnds(joined, Math.Max(Math.Max(reach, MinWindow) + reach / 2 + Chord, TowSettle * reach) + StretchStep);
        int n = dense.Count - 1;
        if (n < 3) return null;

        // Round three times and back to the start.
        var thrice = new List<Vec3>(3 * n + 1);
        for (int k = 0; k < 3; k++)
            for (int i = 0; i < n; i++) thrice.Add(dense[i]);
        thrice.Add(dense[0]);
        var cum = new double[thrice.Count];
        for (int i = 1; i < thrice.Count; i++) cum[i] = cum[i - 1] + GeometryMath.Distance(thrice[i - 1], thrice[i]);
        if (cum[^1] < 1e-6) return null;
        var shifts = TowedShifts(thrice, cum, reach, forward, minRadius);

        double most = 0;
        var line = new List<Vec3>(n);
        for (int i = 0; i < n; i++)
        {
            int j = n + i; // the middle time round
            double shift = shifts[j];
            most = Math.Max(most, Math.Abs(shift));
            double h = HeadingOn(thrice, cum, cum[j]);
            var p = thrice[j];
            // Right of a heading h is (cos h, -sin h).
            line.Add(new Vec3(p.Easting + shift * Math.Cos(h), p.Northing - shift * Math.Sin(h), p.Heading));
        }
        if (most < MinShift) return null;
        var closedLine = new List<Vec3>(line) { line[0] };
        if (TightestBend(closedLine) < minRadius * TightestBendShare) return null;
        return line;
    }

    /// <summary>An open pass's line bending tighter than this share of the tractor's turning radius
    /// anywhere is not steered (<see cref="TightestBend"/>), nor is the pivot's path of a turn planned
    /// for a front implement bending tighter than this share of the U-turn radius. Not 1: the bend
    /// is read over 2 m chords of a sampled, smoothed path, and reads a few per cent either way.</summary>
    internal const double TightestBendShare = 0.9;

    /// <summary>The chord <see cref="TightestBend"/> measures a bend over, either side, m.</summary>
    private const double BendChord = 2.0;

    /// <summary>
    /// The tightest radius a line bends at: the circle through each point and the points
    /// <see cref="BendChord"/> before and after it along the line.
    /// </summary>
    internal static double TightestBend(IReadOnlyList<Vec3> line)
    {
        int n = line.Count;
        if (n < 3) return double.PositiveInfinity;
        var cum = new double[n];
        for (int i = 1; i < n; i++) cum[i] = cum[i - 1] + GeometryMath.Distance(line[i - 1], line[i]);
        double tightest = double.PositiveInfinity;
        for (int i = 0; i < n; i++)
        {
            if (cum[i] < BendChord || cum[i] > cum[n - 1] - BendChord) continue;
            var a = PointOn(line, cum, cum[i] - BendChord);
            var b = new Vec2(line[i].Easting, line[i].Northing);
            var c = PointOn(line, cum, cum[i] + BendChord);
            double cross = Math.Abs((b.Easting - a.Easting) * (c.Northing - a.Northing) - (b.Northing - a.Northing) * (c.Easting - a.Easting));
            if (cross < 1e-9) continue;
            tightest = Math.Min(tightest, GeometryMath.Distance(a, b) * GeometryMath.Distance(b, c) * GeometryMath.Distance(c, a) / (2 * cross));
        }
        return tightest;
    }

    /// <summary>The step the implement's line is walked in for <see cref="TowedShifts"/>, m.</summary>
    private const double TowStep = 0.05;

    /// <summary>A move of the pivot's line smaller than this is none, m: the pull dies away along a
    /// straight stretch without ever reaching 0.</summary>
    private const double TowNoise = 0.001;

    /// <summary>Past a bend the pivot comes back onto a straight pass as e^(−s/reach): within
    /// <see cref="TowNoise"/> of it after this many reaches from 2 m off.</summary>
    private const double TowSettle = 8.0;

    /// <summary>
    /// How far, right of the pass in its own order (+), the pivot is beside each point of the pass
    /// when the implement <paramref name="reach"/> ahead of it runs on the pass. The pivot follows
    /// the implement as a trailer follows its hitch: each step, it moves towards where the
    /// implement now is until it is <paramref name="reach"/> behind it. Round a bend of radius R
    /// that settles on √(R² − h²), as <see cref="Steered"/> has it; into and out of a bend, and
    /// through an S, it is where the pivot must be, which no measure of the bend at one place gives.
    /// The pivot's own turn is held to <paramref name="minRadius"/>: on a bend tighter than
    /// √(minRadius² + h²) the implement can't stay on the pass, and the pull would have the pivot
    /// turn on the spot (the line steered went down to a 1 m radius on a 6 m S with a 4.8 m boom);
    /// held, the pivot turns as tight as the tractor can and the implement leaves the pass as
    /// little as that allows. Bends that wide or wider, as Go around bends are made for a front
    /// implement (<see cref="GoAroundPasses.Radius"/>), are not changed by it.
    /// </summary>
    internal static double[] TowedShifts(IReadOnlyList<Vec3> pass, double[] cum, double reach, bool forward,
        double minRadius = 0)
    {
        int n = pass.Count;
        double length = cum[n - 1];
        var shifts = new double[n];
        int steps = (int)Math.Ceiling(length / TowStep);
        if (steps < 1) return shifts;

        // The implement along the pass the way the tractor drives, and the pivot behind it.
        var tow = new List<Vec2>(steps + 1);
        var towAt = new List<double>(steps + 1);  // where on the pass the implement was
        var towLag = new List<double>(steps + 1); // how far the pivot was from it (the reach, unless held)
        double mostLag = reach;
        Vec2 Implement(int k) => PointOn(pass, cum, forward ? Math.Min(k * TowStep, length) : Math.Max(length - k * TowStep, 0));
        var q0 = Implement(0);
        var q1 = Implement(1);
        double dl = GeometryMath.Distance(q0, q1);
        if (dl < 1e-9) return shifts;
        double dx = q1.Easting - q0.Easting, dy = q1.Northing - q0.Northing;
        var pivot = new Vec2(q0.Easting - dx / dl * reach, q0.Northing - dy / dl * reach);
        double heading = Math.Atan2(dx, dy); // the way the pivot moves
        for (int k = 0; k <= steps; k++)
        {
            var q = Implement(k);
            double el = GeometryMath.Distance(pivot, q);
            if (el > 1e-9)
            {
                var pulled = new Vec2(q.Easting - (q.Easting - pivot.Easting) / el * reach, q.Northing - (q.Northing - pivot.Northing) / el * reach);
                double moved = GeometryMath.Distance(pivot, pulled);
                if (moved > 1e-9)
                {
                    double way = Math.Atan2(pulled.Easting - pivot.Easting, pulled.Northing - pivot.Northing);
                    double turn = AngleDiff(way, heading), most = minRadius > 0 ? moved / minRadius : double.PositiveInfinity;
                    if (Math.Abs(turn) <= most)
                    {
                        pivot = pulled;
                        heading = way;
                    }
                    else
                    {
                        // Turned as tight as the tractor can, on as far as keeps the implement the
                        // reach ahead (or as near it as that way allows).
                        heading += Math.Sign(turn) * most;
                        double ue = Math.Sin(heading), un = Math.Cos(heading);
                        double we = q.Easting - pivot.Easting, wn = q.Northing - pivot.Northing;
                        double along = we * ue + wn * un;
                        double disc = along * along - (we * we + wn * wn) + reach * reach;
                        double d = Math.Max(0, disc >= 0 ? along - Math.Sqrt(disc) : along);
                        pivot = new Vec2(pivot.Easting + ue * d, pivot.Northing + un * d);
                    }
                }
            }
            tow.Add(pivot);
            towAt.Add(forward ? Math.Min(k * TowStep, length) : Math.Max(length - k * TowStep, 0));
            double lag = GeometryMath.Distance(pivot, q);
            towLag.Add(lag);
            mostLag = Math.Max(mostLag, lag);
        }

        // Beside each point of the pass: where the pivot's line crosses the pass's square there,
        // the pivot then being about the reach behind the implement (further where it was held).
        int span = (int)Math.Ceiling((reach + mostLag) / TowStep) + 2;
        for (int i = 0; i < n; i++)
        {
            double h = HeadingOn(pass, cum, cum[i]);
            double ae = Math.Sin(h), an = Math.Cos(h);   // along the pass's own order
            double re = Math.Cos(h), rn = -Math.Sin(h);  // right of it
            double target = forward ? cum[i] + reach : cum[i] - reach;
            int k0 = (int)Math.Round((forward ? target : length - target) / TowStep);
            int from = Math.Max(0, k0 - span), to = Math.Min(tow.Count - 2, k0 + span);
            double best = double.PositiveInfinity, shift = 0;
            for (int k = from; k <= to; k++)
            {
                double a0 = (tow[k].Easting - pass[i].Easting) * ae + (tow[k].Northing - pass[i].Northing) * an;
                double a1 = (tow[k + 1].Easting - pass[i].Easting) * ae + (tow[k + 1].Northing - pass[i].Northing) * an;
                if ((a0 > 0 && a1 > 0) || (a0 < 0 && a1 < 0)) continue;
                double t = Math.Abs(a1 - a0) > 1e-12 ? a0 / (a0 - a1) : 0;
                double pe = tow[k].Easting + (tow[k + 1].Easting - tow[k].Easting) * t;
                double pn = tow[k].Northing + (tow[k + 1].Northing - tow[k].Northing) * t;
                double lateral = (pe - pass[i].Easting) * re + (pn - pass[i].Northing) * rn;
                // A pivot line that loops near a tight bend crosses a square more than once: the
                // crossing with the implement as far ahead as the pivot was from it.
                double off = Math.Abs(towAt[k] - (forward ? cum[i] + towLag[k] : cum[i] - towLag[k]));
                if (off < best) { best = off; shift = lateral; }
            }
            shifts[i] = Math.Abs(shift) < TowNoise ? 0 : shift;
        }
        return shifts;
    }

    private static Vec2 PointOn(IReadOnlyList<Vec3> pass, double[] cum, double s)
    {
        int n = pass.Count;
        s = Math.Clamp(s, 0, cum[n - 1]);
        int i = Array.BinarySearch(cum, s);
        if (i < 0) i = ~i - 1;
        i = Math.Clamp(i, 0, n - 2);
        var a = pass[i];
        var b = pass[i + 1];
        double segLen = cum[i + 1] - cum[i];
        double t = segLen > 1e-9 ? Math.Clamp((s - cum[i]) / segLen, 0, 1) : 0;
        return new Vec2(a.Easting + (b.Easting - a.Easting) * t, a.Northing + (b.Northing - a.Northing) * t);
    }

    private static double HeadingOn(IReadOnlyList<Vec3> pass, double[] cum, double s)
    {
        var a = PointOn(pass, cum, s - Chord);
        var b = PointOn(pass, cum, s + Chord);
        return Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing);
    }

    /// <summary>The spacing of the points added near the ends of a long straight stretch, m.</summary>
    public const double StretchStep = 1.0;

    /// <summary>
    /// The pass with a point every <see cref="StretchStep"/> along the first and last
    /// <paramref name="span"/> metres of every stretch (all along one shorter than twice the span;
    /// a 25 m stretch beside a tree, joined straight between its ends, took the boom 0.58 m off the
    /// pass while the pull died away along it: review of fork #264). A pass bent round Go
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
            if (len <= 2 * StretchStep)
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
