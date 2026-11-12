// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services.Pipeline;

/// <summary>What <see cref="SideStep.Plan"/> found.</summary>
public enum SideStepOutcome
{
    /// <summary>Nothing to plan: on the line already, not heading along it, no hard fence near, an
    /// obstacle near (the pass may bend round it there). AutoSteer steers onto the line as before.</summary>
    NotNeeded,
    /// <summary>An S onto the line that keeps the implement inside the fence: <see cref="SideStepPlan.Path"/>.</summary>
    Planned,
    /// <summary>No S up to <see cref="SideStep.MaxLength"/> keeps the implement inside the fence.</summary>
    NoRoom,
}

/// <summary>The S from the tractor onto the line, beginning at the pivot and ending on the line.</summary>
public sealed record SideStepPlan(SideStepOutcome Outcome, List<Vec3>? Path = null)
{
    public static readonly SideStepPlan NotNeeded = new(SideStepOutcome.NotNeeded);
    public static readonly SideStepPlan NoRoom = new(SideStepOutcome.NoRoom);
}

/// <summary>
/// Where the implement is, as the S is checked: its working centre <see cref="Along"/> metres
/// ahead of the pivot (behind, negative) on the tractor's heading, its ends <see cref="Reach"/>
/// either side. A trailed implement is taken at the hitch: it follows inside the tractor's way
/// onto a line and never further out, and its heading is never steeper than the tractor's.
/// </summary>
public readonly record struct SideStepTool(double Along, double Reach);

/// <summary>
/// AutoSteer joining a line beside a hard fence, sideways (engaged off it, moved a pass, another
/// track; fork only). Steered the shortest way onto it, a front implement swings out ahead of the
/// tractor as it straightens: on a tester's field (simulator) a lateral move onto the lap 4 m from
/// the fence, with an 8 m boom 4.5 m ahead of the pivot, took the boom's end at the fence at 26°
/// until the fence guard stopped AutoSteer. A point h ahead of the pivot on a curve of radius R
/// straightening onto the line goes past it by about h²/2R. Now the way onto the line is a smooth
/// S (a quintic in the offset from the line, along it, so it leaves the tractor's heading and
/// joins the line without a kink), the shortest from 10 to <see cref="MaxLength"/> m whose
/// tightest bend is no tighter than the tractor turns and along which the implement's ends
/// stay inside the fence (or no further out than the line itself puts them, plus
/// <see cref="Tolerance"/>) and the fence guard (<see cref="HardFenceGuard"/>) would not see
/// them heading out across it. None fits: AutoSteer off, the operator moves over by hand.
/// </summary>
public static class SideStep
{
    /// <summary>How much further out than the line itself an end of the implement may go on the S, m.</summary>
    public const double Tolerance = 0.1;

    /// <summary>Nearer the line than this, the tractor steers onto it as before, m.</summary>
    public const double MinOffset = 1.0;

    /// <summary>Heading across the line more than this, the tractor is not joining it along its way.</summary>
    public const double MaxHeadingRad = 75.0 * Math.PI / 180.0;

    /// <summary>The longest S, m along the line.</summary>
    public const double MaxLength = 150.0;

    /// <summary>
    /// The fence counts when an end of the implement, on the S or on the line, comes this near it
    /// (plus how far the implement is ahead of or behind the pivot), m.
    /// </summary>
    public const double NearFence = 2.0;

    /// <summary>The S goes on along the line this far, so the line takes over on it, m.</summary>
    public const double RunOn = 2.0;

    /// <summary>The S is checked for the fence guard at this speed at least, m/s (about 7 km/h).</summary>
    public const double MinCheckSpeed = 2.0;

    /// <summary>The way into the cultivated area stays in it this far past the S's end (the U-turn is
    /// planned again only after it), m.</summary>
    public const double CultivatedAfter = 20.0;

    private static readonly double[] Lengths = { 10, 15, 20, 25, 30, 40, 50, 60, 70, 80, 90, 100, 120, MaxLength };


    private const double PathSpacing = 0.5;
    private const double CheckSpacing = 1.0;
    // The fence guard's look-ahead seeing a little more than the line's own is not the S's doing.
    private const double GuardSlack = 0.05;

    /// <summary>
    /// Plan the S from <paramref name="pivot"/> onto <paramref name="line"/>.
    /// </summary>
    /// <param name="pivot">The tractor's pivot, heading in compass radians.</param>
    /// <param name="speed">The tractor's speed, m/s.</param>
    /// <param name="line">The line steered, in the order it is driven, run on past the ends of an
    /// open pass; a closed pass once round (it is joined end to start).</param>
    /// <param name="closed">The line is a loop.</param>
    /// <param name="outer">The hard outer boundary.</param>
    /// <param name="obstacles">The field's inner boundaries: near one, nothing is planned.</param>
    /// <param name="tool">Where the implement is.</param>
    /// <param name="minRadius">The tightest bend the S may have, m.</param>
    /// <param name="cultivated">The edge of the cultivated area, when the line's U-turns are planned
    /// at it: once in it the S and <see cref="CultivatedAfter"/> m after stay in it. Null: not looked at.</param>
    public static SideStepPlan Plan(Vec3 pivot, double speed, IReadOnlyList<Vec3> line, bool closed,
        BoundaryPolygon outer, IReadOnlyList<BoundaryPolygon>? obstacles, SideStepTool tool, double minRadius,
        IReadOnlyList<Vec3>? cultivated = null)
    {
        if (outer == null || !outer.IsValid || line == null || line.Count < 2) return SideStepPlan.NotNeeded;
        var frame = Frame.Build(line, closed);
        if (frame == null) return SideStepPlan.NotNeeded;

        frame.Project(pivot.Easting, pivot.Northing, out double s0, out double offset, out double lineHeading);
        if (Math.Abs(offset) < MinOffset) return SideStepPlan.NotNeeded;
        double phi0 = AngleDiff(pivot.Heading, lineHeading); // + heading to the right of the line
        if (Math.Abs(phi0) > MaxHeadingRad) return SideStepPlan.NotNeeded;
        minRadius = Math.Max(minRadius, 1.0);

        // The shapes, shortest along the line first: an arc at the tightest bend that brings the
        // tractor's heading round towards the line's (none when it is near it already), then the S.
        var shapes = new List<Shape>();
        var turnTo = new List<double> { phi0 };
        foreach (double target in ArcTargets)
            if (Math.Abs(phi0) > target + ArcTargetSlack) turnTo.Add(Math.Sign(phi0) * target);
        foreach (double phi1 in turnTo)
        {
            var arc = Shape.Arc(phi0, phi1, offset, minRadius);
            foreach (double length in Lengths)
                shapes.Add(arc with { Length = length });
        }
        shapes.Sort((a, b) => a.Along.CompareTo(b.Along));

        double look = HardFenceGuard.LookAheadSeconds * Math.Max(Math.Abs(speed), MinCheckSpeed);
        double near = NearFence + Math.Abs(tool.Along);
        bool first = true;
        foreach (var shape in shapes)
        {
            if (s0 + shape.Along + RunOn > frame.Length) continue; // the line ends first
            if (TightestRadius(shape.Y1, Math.Tan(shape.Phi1), shape.Length) < minRadius) continue;

            var samples = Sample(frame, s0, shape, CheckSpacing);
            if (first)
            {
                // The first way the tractor can drive: no hard fence near it or the line, nothing to do.
                first = false;
                if (!FenceNear(samples, frame, outer, tool, near)) return SideStepPlan.NotNeeded;
            }
            if (ObstacleNear(samples, obstacles, near)) return SideStepPlan.NotNeeded;
            if (!Fits(samples, frame, outer, tool, look, cultivated)) continue;

            var path = new List<Vec3>();
            foreach (var (pose, _) in Sample(frame, s0, shape, PathSpacing)) path.Add(pose);
            for (double s = PathSpacing; s <= RunOn + 1e-9; s += PathSpacing)
            {
                var p = frame.PointAt(s0 + shape.Along + s);
                path.Add(new Vec3(p.Easting, p.Northing, frame.HeadingAt(s0 + shape.Along + s)));
            }
            // The path begins at the pivot itself, on its own heading.
            path[0] = pivot;
            return new SideStepPlan(SideStepOutcome.Planned, path);
        }
        return first ? SideStepPlan.NotNeeded : SideStepPlan.NoRoom;
    }

    /// <summary>Headings to the line (radians) a steep approach is first brought round to by an arc.</summary>
    private static readonly double[] ArcTargets = { 20.0 * Math.PI / 180.0, 10.0 * Math.PI / 180.0, 0.0 };
    private const double ArcTargetSlack = 2.0 * Math.PI / 180.0;

    /// <summary>
    /// One way onto the line, in the line's own measure (along it from the tractor, and to its
    /// right): an arc of radius <see cref="Radius"/> from the tractor's heading to the line
    /// <see cref="Phi0"/> round to <see cref="Phi1"/> (none when they are the same), ending
    /// <see cref="ArcAlong"/> along and <see cref="Y1"/> to the right; then the S over
    /// <see cref="Length"/> m onto the line.
    /// </summary>
    private readonly record struct Shape(double Phi0, double Phi1, double Radius, double ArcAlong, double Y0, double Y1, double Length)
    {
        public double Along => ArcAlong + Length;

        public static Shape Arc(double phi0, double phi1, double y0, double radius)
        {
            if (Math.Abs(phi1 - phi0) < 1e-9) return new Shape(phi0, phi0, radius, 0, y0, y0, 0);
            double k = Math.Sign(phi1 - phi0) / radius; // heading change per metre driven
            return new Shape(phi0, phi1, radius, (Math.Sin(phi1) - Math.Sin(phi0)) / k, y0,
                y0 - (Math.Cos(phi1) - Math.Cos(phi0)) / k, 0);
        }

        /// <summary>The point <paramref name="x"/> m along the line: its offset and heading to the line.</summary>
        public (double y, double phi) At(double x)
        {
            if (x < ArcAlong)
            {
                double k = Math.Sign(Phi1 - Phi0) / Radius;
                double phi = Math.Asin(Math.Clamp(Math.Sin(Phi0) + x * k, -1, 1));
                return (Y0 - (Math.Cos(phi) - Math.Cos(Phi0)) / k, phi);
            }
            Offset((x - ArcAlong) / Length, Y1, Math.Tan(Phi1), Length, out double y, out double dy, out _);
            return (y, Math.Atan(dy));
        }
    }

    /// <summary>
    /// The offset from the line, as a share of the S's length: leaves the tractor's offset and
    /// heading and joins the line with the line's heading, with no sudden change of steering at
    /// either end (a quintic Hermite: value, slope and bend matched at both ends).
    /// </summary>
    private static void Offset(double t, double offset, double slope, double length, out double y, out double dy, out double d2y)
    {
        double t2 = t * t, t3 = t2 * t, t4 = t3 * t, t5 = t4 * t;
        double h0 = 1 - 10 * t3 + 15 * t4 - 6 * t5;
        double h0d = -30 * t2 + 60 * t3 - 30 * t4;
        double h0dd = -60 * t + 180 * t2 - 120 * t3;
        double h1 = t - 6 * t3 + 8 * t4 - 3 * t5;
        double h1d = 1 - 18 * t2 + 32 * t3 - 15 * t4;
        double h1dd = -36 * t + 96 * t2 - 60 * t3;
        double m = slope * length;
        y = offset * h0 + m * h1;
        dy = (offset * h0d + m * h1d) / length;
        d2y = (offset * h0dd + m * h1dd) / (length * length);
    }

    /// <summary>The S's tightest bend on a straight line, m.</summary>
    private static double TightestRadius(double offset, double slope, double length)
    {
        double best = double.MaxValue;
        for (int i = 0; i <= 100; i++)
        {
            Offset(i / 100.0, offset, slope, length, out _, out double dy, out double d2y);
            double k = Math.Abs(d2y) / Math.Pow(1 + dy * dy, 1.5);
            if (k > 1e-12) best = Math.Min(best, 1 / k);
        }
        return best;
    }

    /// <summary>The way's points about every <paramref name="spacing"/> m along the line, with how
    /// far along the line each is.</summary>
    private static List<(Vec3 Pose, double S)> Sample(Frame frame, double s0, Shape shape, double spacing)
    {
        var pts = new List<(Vec3, double)>();
        int arcCount = shape.ArcAlong > 0 ? Math.Max(1, (int)Math.Ceiling(shape.ArcAlong / spacing)) : 0;
        int sCount = Math.Max(1, (int)Math.Ceiling(shape.Length / spacing));
        for (int i = 0; i < arcCount; i++) pts.Add(Point(shape.ArcAlong * i / arcCount));
        for (int i = 0; i <= sCount; i++) pts.Add(Point(shape.ArcAlong + shape.Length * i / sCount));
        return pts;

        (Vec3, double) Point(double x)
        {
            var (y, phi) = shape.At(x);
            double s = s0 + x;
            var p = frame.PointAt(s);
            double h = frame.HeadingAt(s);
            // Right of a heading h is (cos h, -sin h); going right turns the heading clockwise.
            return (new Vec3(p.Easting + y * Math.Cos(h), p.Northing - y * Math.Sin(h), h + phi), s);
        }
    }

    private static Vec3 OnLine(Frame frame, double s)
    {
        var p = frame.PointAt(s);
        return new Vec3(p.Easting, p.Northing, frame.HeadingAt(s));
    }

    private static bool FenceNear(List<(Vec3 Pose, double S)> samples, Frame frame, BoundaryPolygon outer,
        SideStepTool tool, double near)
    {
        foreach (var (pose, s) in samples)
            if (EndsWithin(outer, pose, tool, near) || EndsWithin(outer, OnLine(frame, s), tool, near)) return true;
        return false;
    }

    private static bool EndsWithin(BoundaryPolygon outer, Vec3 pose, SideStepTool tool, double dist)
    {
        Ends(pose, tool, out _, out var a, out var b);
        return DistanceToEdges(outer, a.Easting, a.Northing) < dist || DistanceToEdges(outer, b.Easting, b.Northing) < dist
            || !outer.IsPointInside(a.Easting, a.Northing) || !outer.IsPointInside(b.Easting, b.Northing);
    }

    private static bool ObstacleNear(List<(Vec3 Pose, double S)> samples, IReadOnlyList<BoundaryPolygon>? obstacles, double near)
    {
        if (obstacles == null || obstacles.Count == 0) return false;
        double minE = double.MaxValue, minN = double.MaxValue, maxE = double.MinValue, maxN = double.MinValue;
        foreach (var (p, _) in samples)
        {
            minE = Math.Min(minE, p.Easting); maxE = Math.Max(maxE, p.Easting);
            minN = Math.Min(minN, p.Northing); maxN = Math.Max(maxN, p.Northing);
        }
        foreach (var o in obstacles)
        {
            if (o == null || !o.IsValid) continue;
            foreach (var q in o.Points)
                if (q.Easting > minE - near && q.Easting < maxE + near && q.Northing > minN - near && q.Northing < maxN + near)
                    return true;
        }
        return false;
    }

    private static bool Fits(List<(Vec3 Pose, double S)> samples, Frame frame, BoundaryPolygon outer,
        SideStepTool tool, double look, IReadOnlyList<Vec3>? cultivated)
    {
        // Already out at the start (the tractor is where it is): no further out than that.
        Ends(samples[0].Pose, tool, out _, out var a0, out var b0);
        double outAtStart = Math.Max(HardFenceGuard.OutsideBy(outer, a0.Easting, a0.Northing),
            HardFenceGuard.OutsideBy(outer, b0.Easting, b0.Northing));
        // Further from the fence than this, the fence guard cannot see an end going out across it.
        double guardReach = look + HardFenceGuard.Margin + 1.0;
        bool inCultivated = false;
        foreach (var (p, s) in samples)
        {
            if (!outer.IsPointInside(p.Easting, p.Northing)) return false;
            var onLine = OnLine(frame, s);

            Ends(p, tool, out var c, out var a, out var b);
            Ends(onLine, tool, out var lc, out var la, out var lb);
            double outS = Math.Max(HardFenceGuard.OutsideBy(outer, a.Easting, a.Northing), HardFenceGuard.OutsideBy(outer, b.Easting, b.Northing));
            double outL = Math.Max(HardFenceGuard.OutsideBy(outer, la.Easting, la.Northing), HardFenceGuard.OutsideBy(outer, lb.Easting, lb.Northing));
            if (outS > Math.Max(outL, outAtStart) + Tolerance) return false;

            if (outS > 0 || Math.Min(DistanceToEdges(outer, a.Easting, a.Northing), DistanceToEdges(outer, b.Easting, b.Northing)) < guardReach)
            {
                double guardS = HardFenceGuard.HeadingOut(outer, c, p.Heading, tool.Reach, look);
                if (guardS > 0)
                {
                    double guardL = HardFenceGuard.HeadingOut(outer, lc, onLine.Heading, tool.Reach, look);
                    if (guardS > guardL + GuardSlack) return false;
                }
            }

            if (cultivated != null)
            {
                bool inside = GeometryMath.IsPointInPolygon(cultivated, p.Easting, p.Northing);
                if (inCultivated && !inside) return false;
                inCultivated |= inside;
            }
        }
        if (cultivated != null && inCultivated)
        {
            double end = samples[^1].S;
            for (double s = end; s <= end + CultivatedAfter; s += CheckSpacing)
            {
                if (s > frame.Length) return false;
                var lp = frame.PointAt(s);
                if (!GeometryMath.IsPointInPolygon(cultivated, lp.Easting, lp.Northing)) return false;
            }
        }
        return true;
    }

    /// <summary>The implement's working centre and its two ends for the tractor at <paramref name="pose"/>.</summary>
    private static void Ends(Vec3 pose, SideStepTool tool, out Vec3 centre, out Vec2 left, out Vec2 right)
    {
        double fe = Math.Sin(pose.Heading), fn = Math.Cos(pose.Heading);
        double ce = pose.Easting + fe * tool.Along, cn = pose.Northing + fn * tool.Along;
        centre = new Vec3(ce, cn, pose.Heading);
        // Right of the heading is (cos h, -sin h) = (fn, -fe).
        right = new Vec2(ce + fn * tool.Reach, cn - fe * tool.Reach);
        left = new Vec2(ce - fn * tool.Reach, cn + fe * tool.Reach);
    }

    /// <summary>Metres to the nearest edge of the polygon, inside or out.</summary>
    private static double DistanceToEdges(BoundaryPolygon poly, double e, double n)
    {
        var pts = poly.Points;
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

    private static double AngleDiff(double a, double b)
    {
        double d = a - b;
        while (d > Math.PI) d -= 2 * Math.PI;
        while (d < -Math.PI) d += 2 * Math.PI;
        return d;
    }

    /// <summary>The line measured along its length; a loop is gone round twice, so an S may run on past its start.</summary>
    private sealed class Frame
    {
        private readonly Vec2[] _pts;
        private readonly double[] _cum;

        private Frame(Vec2[] pts, double[] cum) { _pts = pts; _cum = cum; }

        public double Length => _cum[^1];

        public static Frame? Build(IReadOnlyList<Vec3> line, bool closed)
        {
            var pts = new List<Vec2>(line.Count * (closed ? 2 : 1) + 1);
            int rounds = closed ? 2 : 1;
            for (int r = 0; r < rounds; r++)
                for (int i = 0; i < line.Count; i++)
                {
                    var v = new Vec2(line[i].Easting, line[i].Northing);
                    if (pts.Count == 0 || GeometryMath.Distance(pts[^1], v) > 1e-6) pts.Add(v);
                }
            if (closed && pts.Count > 1 && GeometryMath.Distance(pts[^1], pts[0]) > 1e-6) pts.Add(pts[0]);
            if (pts.Count < 2) return null;
            var cum = new double[pts.Count];
            for (int i = 1; i < pts.Count; i++) cum[i] = cum[i - 1] + GeometryMath.Distance(pts[i - 1], pts[i]);
            return new Frame(pts.ToArray(), cum);
        }

        public Vec2 PointAt(double s)
        {
            int i = Segment(s);
            var a = _pts[i]; var b = _pts[i + 1];
            double len = _cum[i + 1] - _cum[i];
            double t = len > 1e-9 ? (s - _cum[i]) / len : 0;
            return new Vec2(a.Easting + (b.Easting - a.Easting) * t, a.Northing + (b.Northing - a.Northing) * t);
        }

        /// <summary>The way the line runs at <paramref name="s"/>, compass radians.</summary>
        public double HeadingAt(double s)
        {
            int i = Segment(s);
            return Math.Atan2(_pts[i + 1].Easting - _pts[i].Easting, _pts[i + 1].Northing - _pts[i].Northing);
        }

        private int Segment(double s)
        {
            if (s <= 0) return 0;
            if (s >= Length) return _pts.Length - 2;
            int i = Array.BinarySearch(_cum, s);
            if (i < 0) i = ~i - 1;
            return Math.Clamp(i, 0, _pts.Length - 2);
        }

        /// <summary>The nearest point of the line (on a loop, its first time round): how far along,
        /// the distance off it (+ right of the way it runs) and its heading there.</summary>
        public void Project(double e, double n, out double s, out double offset, out double heading)
        {
            double best = double.MaxValue; s = 0; offset = 0; heading = 0;
            for (int i = 0; i < _pts.Length - 1; i++)
            {
                var a = _pts[i]; var b = _pts[i + 1];
                double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing;
                double len2 = dx * dx + dy * dy;
                if (len2 < 1e-12) continue;
                double t = Math.Clamp(((e - a.Easting) * dx + (n - a.Northing) * dy) / len2, 0, 1);
                double px = a.Easting + dx * t, py = a.Northing + dy * t;
                double d = (e - px) * (e - px) + (n - py) * (n - py);
                if (d >= best - 1e-9) continue; // the first time round wins a tie
                best = d;
                double len = Math.Sqrt(len2);
                s = _cum[i] + t * len;
                offset = ((e - a.Easting) * dy - (n - a.Northing) * dx) / len;
                heading = Math.Atan2(dx, dy);
            }
        }
    }
}
