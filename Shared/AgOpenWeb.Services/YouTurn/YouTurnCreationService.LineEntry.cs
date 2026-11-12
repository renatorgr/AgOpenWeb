// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Services.PathPlanning;

using Microsoft.Extensions.Logging;

namespace AgOpenWeb.Services.YouTurn;

/// <summary>What <see cref="YouTurnCreationService.PlanLineEntry"/> found.</summary>
public enum LineEntryOutcome
{
    /// <summary>A way to the start of the pass through the headland: <see cref="LineEntryPlan.Path"/>.</summary>
    Planned,
    /// <summary>The tractor is on the line already, heading along it.</summary>
    NotNeeded,
    /// <summary>The tractor is not in the headland (in the cultivated area, or outside the field).</summary>
    NotInHeadland,
    /// <summary>No start of the pass near the tractor: the pass does not enter the cultivated area
    /// ahead of it within reach.</summary>
    NoStart,
    /// <summary>A start, but no way to it that stays in the headland, keeps the implement off a hard
    /// fence and out of obstacles.</summary>
    NoRoom,
}

/// <summary>
/// The way from the tractor to the start of a pass through the headland (fork only): the path
/// begins at the pivot and ends a little past the headland line on the pass, driven the way
/// <see cref="HeadingSameWay"/> says (along the track's own direction or against it).
/// </summary>
public sealed record LineEntryPlan(LineEntryOutcome Outcome, List<Vec3>? Path = null, bool HeadingSameWay = true)
{
    public static readonly LineEntryPlan NotNeeded = new(LineEntryOutcome.NotNeeded);
    public static readonly LineEntryPlan NotInHeadland = new(LineEntryOutcome.NotInHeadland);
    public static readonly LineEntryPlan NoStart = new(LineEntryOutcome.NoStart);
    public static readonly LineEntryPlan NoRoom = new(LineEntryOutcome.NoRoom);
}

public partial class YouTurnCreationService
{
    /// <summary>The way starts this far ahead of the tractor, as a manual turn does (the steering
    /// needs the lead to reach lock), m.</summary>
    public const double LineEntryStartLead = 3.0;

    /// <summary>The path goes on along the pass this far past the headland line, so line guidance
    /// takes over inside the cultivated area, m.</summary>
    public const double LineEntryRunOn = 2.0;

    /// <summary>The start of the pass is looked for this far from the tractor at most, m.</summary>
    public const double LineEntryMaxReach = 150.0;

    /// <summary>A way longer than this is not driven (m): it would be a tour of the headland.</summary>
    public const double LineEntryMaxLength = 300.0;

    /// <summary>On the line already: within this angle of the pass's heading (and within the
    /// caller's distance of it).</summary>
    private const double LineEntryAlignedRad = 20.0 * Math.PI / 180.0;

    /// <summary>The start of the pass is driven in no further than this from the way the tractor
    /// faces, when there is one.</summary>
    private const double LineEntryMaxTurnRad = 100.0 * Math.PI / 180.0;

    private const double LineEntrySpacing = 0.25;

    /// <summary>
    /// Plan the way from the tractor to the start of a pass through the headland, for AutoSteer
    /// joining a new line (GuidanceConfig.LineEntryMode; fork only). The tractor must be in the
    /// headland: inside the field and outside <paramref name="headlandLine"/>, the edge of the
    /// cultivated area. The start of the pass is where it enters the cultivated area nearest the
    /// tractor, not behind it, driven in no more than 100 degrees from the way the tractor faces;
    /// with none there (the tractor faces out of the field: the pass ahead runs to the fence), the
    /// way the tractor would have to turn round for, which the checks below let through only when
    /// it fits in the headland (Tapada da Casa: AutoSteer engaged facing the fence took the
    /// line out to it). The tractor is put straight on the pass
    /// <c>LineEntryStraight</c> (plus a front implement's reach ahead of the pivot) before that,
    /// by a Dubins path at the U-turn radius from 3 m ahead of it, the shortest of the six that
    /// passes every check: the pivot stays in the headland until it is on the pass, and in the
    /// field out of obstacles a turn may not drive through; the implement keeps the U-turn
    /// distance from a hard fence (or, already nearer at the start, goes no further across), and
    /// clear of every turn obstacle, as a manual U-turn.
    /// </summary>
    /// <param name="pivot">The tractor's pivot, heading in radians.</param>
    /// <param name="passFor">The pass line to join, extended past both ends, in the track's
    /// own order: for driving along the track's direction (true) or against it (false), which
    /// differ by the tool offset.</param>
    /// <param name="headlandLine">The headland line (the cultivated area's edge).</param>
    /// <param name="boundary">The field.</param>
    /// <param name="onLineWithin">Nearer the line than this, heading along it, nothing is planned, m.</param>
    public LineEntryPlan PlanLineEntry(
        Vec3 pivot,
        Func<bool, IReadOnlyList<Vec3>?> passFor,
        IReadOnlyList<Vec3> headlandLine,
        Boundary boundary,
        double onLineWithin)
    {
        var outer = boundary.OuterBoundary;
        if (outer == null || !outer.IsValid || headlandLine == null || headlandLine.Count < 3)
            return LineEntryPlan.NoStart;
        if (!outer.IsPointInside(pivot.Easting, pivot.Northing)
            || GeometryMath.IsPointInPolygon(headlandLine, pivot.Easting, pivot.Northing))
            return LineEntryPlan.NotInHeadland;

        // The pass the way it would be driven, and where it enters the cultivated area nearest
        // the tractor, for either way along the track.
        EntryLine? best = null, turnRound = null;
        foreach (bool sameWay in new[] { true, false })
        {
            var pass = passFor(sameWay);
            if (pass == null || pass.Count < 2) continue;
            var line = EntryLine.Build(pass, reverse: !sameWay);
            if (line == null) continue;
            if (!line.FindStart(headlandLine, pivot)) continue;
            line.SameWay = sameWay;
            // Into a pass the tractor faces; a tractor facing out of the field turns round onto
            // the other way only when the way it faces has no start.
            if (Math.Abs(AngleDiff(pivot.Heading, line.HeadingAt(line.StartS))) > LineEntryMaxTurnRad)
            {
                if (turnRound == null || line.StartDistance < turnRound.StartDistance) turnRound = line;
                continue;
            }
            if (best == null || line.StartDistance < best.StartDistance) best = line;
        }
        best ??= turnRound;
        if (best == null || best.StartDistance > LineEntryMaxReach) return LineEntryPlan.NoStart;

        // On the line and heading along it: AutoSteer just follows it.
        best.Project(pivot.Easting, pivot.Northing, out _, out double xte, out double lineHeading);
        if (Math.Abs(xte) <= onLineWithin
            && Math.Abs(AngleDiff(pivot.Heading, lineHeading)) <= LineEntryAlignedRad)
            return LineEntryPlan.NotNeeded;

        var config = _configStore;
        double frontReach = config.Tool.IsToolFrontFixed ? Math.Abs(config.Tool.HitchLength) : 0.0;
        double lead = Math.Max(0.0, config.Guidance.LineEntryStraight) + frontReach;
        double goalS = best.StartS - lead;
        var goalPt = best.PointAt(goalS);
        var goal = new Vec3(goalPt.Easting, goalPt.Northing, best.HeadingAt(goalS));

        double headingRad = pivot.Heading;
        var start = new Vec3(
            pivot.Easting + Math.Sin(headingRad) * LineEntryStartLead,
            pivot.Northing + Math.Cos(headingRad) * LineEntryStartLead,
            headingRad);

        // The straight on the pass from the goal to a little past the headland line.
        var straight = new List<Vec3>();
        for (double s = goalS + LineEntrySpacing; s < best.StartS + LineEntryRunOn + 1e-9; s += LineEntrySpacing)
        {
            var p = best.PointAt(s);
            straight.Add(new Vec3(p.Easting, p.Northing, best.HeadingAt(s)));
        }

        double radius = Math.Max(config.Guidance.UTurnRadius, MinManualTurnRadius);
        var dubins = new DubinsPathService(radius);
        int tried = 0;
        foreach (var candidate in dubins.GeneratePaths(start, goal))
        {
            tried++;
            if (PathLength(candidate) + LineEntryStartLead > LineEntryMaxLength) continue;

            var path = new List<Vec3>(candidate.Count + straight.Count + 1)
            {
                new Vec3(pivot.Easting, pivot.Northing, headingRad),
            };
            path.AddRange(candidate);
            int onPassFrom = path.Count - 1; // the goal: from here on the tractor is on the pass
            path.AddRange(straight);

            // The pivot keeps to the headland until it is on the pass: the way never cuts across
            // the crop it is about to work.
            bool inHeadland = true;
            for (int i = 0; i < onPassFrom && inHeadland; i++)
                if (GeometryMath.IsPointInPolygon(headlandLine, path[i].Easting, path[i].Northing))
                    inHeadland = false;
            if (!inHeadland) continue;

            if (LeavesField(path, boundary)) continue;
            if (!ManualTurnClearsHardBoundary(path, boundary, config, out _)) continue;
            if (!ClearsTurnObstacles(path, boundary, config, out _, promoted: _goAround.CurrentBlocked)) continue;

            _logger.LogDebug("[LineEntry] Way to the start of the pass: {Count} points, {Len:F1} m, candidate {N}",
                path.Count, PathLength(path), tried);
            return new LineEntryPlan(LineEntryOutcome.Planned, path, best.SameWay);
        }

        _logger.LogDebug("[LineEntry] No way to the start of the pass fits ({N} tried)", tried);
        return LineEntryPlan.NoRoom;
    }

    private static double PathLength(IReadOnlyList<Vec3> path)
    {
        double len = 0;
        for (int i = 1; i < path.Count; i++)
            len += GeometryMath.Distance(path[i - 1], path[i]);
        return len;
    }

    private static double AngleDiff(double a, double b)
    {
        double d = a - b;
        while (d > Math.PI) d -= 2 * Math.PI;
        while (d < -Math.PI) d += 2 * Math.PI;
        return d;
    }

    /// <summary>A pass as a polyline in the order it is driven, measured along its length.</summary>
    private sealed class EntryLine
    {
        private readonly Vec2[] _pts;
        private readonly double[] _cum;

        public bool SameWay;
        /// <summary>Where the pass enters the cultivated area (m along the line), and how far that is from the tractor.</summary>
        public double StartS;
        public double StartDistance = double.MaxValue;

        private EntryLine(Vec2[] pts, double[] cum) { _pts = pts; _cum = cum; }

        public static EntryLine? Build(IReadOnlyList<Vec3> pass, bool reverse)
        {
            var pts = new List<Vec2>(pass.Count);
            for (int i = 0; i < pass.Count; i++)
            {
                var p = pass[reverse ? pass.Count - 1 - i : i];
                var v = new Vec2(p.Easting, p.Northing);
                if (pts.Count == 0 || GeometryMath.Distance(pts[^1], v) > 1e-6) pts.Add(v);
            }
            if (pts.Count < 2) return null;
            var cum = new double[pts.Count];
            for (int i = 1; i < pts.Count; i++) cum[i] = cum[i - 1] + GeometryMath.Distance(pts[i - 1], pts[i]);
            return new EntryLine(pts.ToArray(), cum);
        }

        private double Length => _cum[^1];

        /// <summary>The point <paramref name="s"/> m along the line, on its end directions past either end.</summary>
        public Vec2 PointAt(double s)
        {
            int i = Segment(s);
            var a = _pts[i]; var b = _pts[i + 1];
            double len = _cum[i + 1] - _cum[i];
            double t = len > 1e-9 ? (s - _cum[i]) / len : 0;
            return new Vec2(a.Easting + (b.Easting - a.Easting) * t, a.Northing + (b.Northing - a.Northing) * t);
        }

        /// <summary>The way the line runs at <paramref name="s"/> (compass radians, 0 to 2π).</summary>
        public double HeadingAt(double s)
        {
            int i = Segment(s);
            double h = Math.Atan2(_pts[i + 1].Easting - _pts[i].Easting, _pts[i + 1].Northing - _pts[i].Northing);
            return h < 0 ? h + 2 * Math.PI : h;
        }

        private int Segment(double s)
        {
            if (s <= 0) return 0;
            if (s >= Length) return _pts.Length - 2;
            int i = Array.BinarySearch(_cum, s);
            if (i < 0) i = ~i - 1;
            return Math.Clamp(i, 0, _pts.Length - 2);
        }

        /// <summary>The nearest point of the line to (e, n): how far along it, the distance off it
        /// (+ right of the way it runs) and its heading there.</summary>
        public void Project(double e, double n, out double s, out double xte, out double heading)
        {
            double best = double.MaxValue; s = 0; xte = 0; heading = 0;
            for (int i = 0; i < _pts.Length - 1; i++)
            {
                var a = _pts[i]; var b = _pts[i + 1];
                double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing;
                double len2 = dx * dx + dy * dy;
                if (len2 < 1e-12) continue;
                double t = Math.Clamp(((e - a.Easting) * dx + (n - a.Northing) * dy) / len2, 0, 1);
                double px = a.Easting + dx * t, py = a.Northing + dy * t;
                double d = (e - px) * (e - px) + (n - py) * (n - py);
                if (d >= best) continue;
                best = d;
                double len = Math.Sqrt(len2);
                s = _cum[i] + t * len;
                xte = ((e - a.Easting) * dy - (n - a.Northing) * dx) / len;
                heading = Math.Atan2(dx, dy);
            }
        }

        /// <summary>
        /// The start of the pass for a tractor at <paramref name="pivot"/>: the place nearest it
        /// where the line, run the way it is driven, goes from outside <paramref name="headlandLine"/>
        /// to inside, not behind the tractor (by more than a metre). False when there is none.
        /// </summary>
        public bool FindStart(IReadOnlyList<Vec3> headlandLine, Vec3 pivot)
        {
            Project(pivot.Easting, pivot.Northing, out double tractorS, out _, out _);
            int m = headlandLine.Count;
            bool found = false;
            for (int i = 0; i < _pts.Length - 1; i++)
            {
                var a = _pts[i]; var b = _pts[i + 1];
                double segLen = _cum[i + 1] - _cum[i];
                if (segLen < 1e-9) continue;
                for (int j = 0; j < m; j++)
                {
                    var c = headlandLine[j]; var d = headlandLine[(j + 1) % m];
                    if (!SegmentsCross(a.Easting, a.Northing, b.Easting, b.Northing,
                            c.Easting, c.Northing, d.Easting, d.Northing, out double t)) continue;
                    double s = _cum[i] + t * segLen;
                    if (s < tractorS - 1.0) continue; // behind the tractor
                    var before = PointAt(s - 0.05);
                    var after = PointAt(s + 0.05);
                    if (GeometryMath.IsPointInPolygon(headlandLine, before.Easting, before.Northing)
                        || !GeometryMath.IsPointInPolygon(headlandLine, after.Easting, after.Northing))
                        continue; // leaves the cultivated area there, or only grazes the line
                    var at = PointAt(s);
                    double dist = GeometryMath.Distance(at.Easting, at.Northing, pivot.Easting, pivot.Northing);
                    if (dist < StartDistance) { StartDistance = dist; StartS = s; found = true; }
                }
            }
            return found;
        }

        private static bool SegmentsCross(double ax, double ay, double bx, double by,
            double cx, double cy, double dx, double dy, out double t)
        {
            t = 0;
            double rx = bx - ax, ry = by - ay, sx = dx - cx, sy = dy - cy;
            double denom = rx * sy - ry * sx;
            if (Math.Abs(denom) < 1e-12) return false;
            double qx = cx - ax, qy = cy - ay;
            t = (qx * sy - qy * sx) / denom;
            double u = (qx * ry - qy * rx) / denom;
            return t >= 0 && t <= 1 && u >= 0 && u <= 1;
        }
    }
}
