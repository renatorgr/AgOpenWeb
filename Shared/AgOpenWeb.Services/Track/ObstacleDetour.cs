// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Services.Geometry;

namespace AgOpenWeb.Services.Track;

/// <summary>
/// Going round obstacles on a pass: the line the tractor steers along is bent round every
/// obstacle chosen as <see cref="ObstacleAction.GoRound"/> that the pass runs into, far enough
/// that the tractor and the implement stay outside it, and back onto the pass after it.
/// <para>
/// Each obstacle is grown by the keep-out distance (half the implement's width plus a margin),
/// and the pass is moved sideways by an offset that changes along it: as far as the grown
/// obstacle reaches across the pass at that point, on the side that needs the smaller move.
/// The offset then rises and falls no steeper than <see cref="MaxSlope"/> and is rounded so
/// that its curves are no tighter than the given radius, as short as that allows; it is never
/// less than what the obstacle needs. A bent line is checked against every obstacle and the
/// field's edge: an obstacle with no room on either side is not gone round
/// (<see cref="Bend.Blocked"/>), and the U-turn planner makes a turn before it on this pass.
/// </para>
/// <para>
/// The side chosen for an obstacle can be pinned (<see cref="GoAroundPasses"/> pins it while the
/// tractor is beside the obstacle): a nudge, a snap or a changed setting then bends the new pass
/// round the same side, never across the obstacle in front of the tractor; with no room on the
/// pinned side the obstacle is blocked, not flipped.
/// </para>
/// <para>
/// A lap of a closed track (a boundary curve) is bent the same way (<c>loop</c>): the lap is
/// walked from a point of it far from every Go around obstacle, so no bend is cut at its seam, and
/// the bent lap is a loop again, its last point joining its first. Laps were not bent before, and
/// a tester's boundary curve (Eucaliptal, 22 Nov 2026, a front sprayer) ran its second lap
/// across hard Go around obstacles with AutoSteer on. Fork only.
/// </para>
/// Built once per pass and kept while the pass, the boundary, the pins and the settings are the
/// same. Pure geometry: the one object the steered line, the shown line and the U-turn planner
/// share (<see cref="GoAroundPasses"/>), so they see one and the same bent pass.
/// </summary>
public sealed class ObstacleDetour
{
    /// <summary>The distance between the bent line's points, metres.</summary>
    public const double SampleStep = 0.25;

    /// <summary>The steepest the line leaves or rejoins the pass: 1 m sideways in 1 m (45°).</summary>
    public const double MaxSlope = 1.0;

    /// <summary>A straight pass (an AB line, two points) is taken this far past its ends, metres.</summary>
    private const double LineExtension = 2000.0;

    /// <summary>A bent point nearer its place on the pass than this is on the pass, for the
    /// field's edge: the last of a bend's rounding, m.</summary>
    private const double EdgeTail = 0.05;

    /// <summary>
    /// The farthest a bend moves the line off its pass, metres: two implement widths (four times
    /// the keep-out less its margin, so a trailed tool's cut-in widens nothing), never under 20 m.
    /// An obstacle, or a row of them gone round together, that needs more on the side with room
    /// is not gone round (<see cref="Bend.Blocked"/>) and the U-turn planner turns before it. On
    /// a real field a 12.5 m rake was taken 47 m off its pass, nearly four passes, round two trees
    /// in a row.
    /// </summary>
    public static double MaxOffset(double keepOut, double margin) => Math.Max(4 * (keepOut - Math.Max(0, margin)), 20.0);

    /// <param name="Points">The bent line, with headings.</param>
    /// <param name="GoneRound">The obstacles gone round, as indices in <see cref="Boundary.InnerBoundaries"/>.</param>
    /// <param name="Blocked">The obstacles the pass runs into with no room to go round.</param>
    /// <param name="Rounds">Each obstacle gone round: which side, and where the tractor counts as beside it.</param>
    public sealed record Bend(List<Vec3> Points, IReadOnlyList<int> GoneRound, IReadOnlyList<int> Blocked,
        IReadOnlyList<RoundedObstacle> Rounds);

    /// <param name="InnerIndex">The obstacle's index in <see cref="Boundary.InnerBoundaries"/>.</param>
    /// <param name="Side">+1 bent to the left of the pass (the way its points run), -1 to the right.</param>
    /// <param name="Fence">The obstacle grown by the keep-out distance.</param>
    /// <param name="Reach">How far from the grown obstacle the bend for it starts and ends, metres: a
    /// tractor nearer than that is beside the obstacle.</param>
    public sealed record RoundedObstacle(int InnerIndex, int Side, IReadOnlyList<Vec2> Fence, double Reach)
    {
        /// <summary>Whether a tractor at (<paramref name="e"/>, <paramref name="n"/>) is beside the obstacle.</summary>
        public bool IsBeside(double e, double n) => DistanceToRing(Fence, e, n) <= Reach;
    }

    private readonly IPolygonOffsetService _offset;

    public ObstacleDetour(IPolygonOffsetService offset) => _offset = offset;

    private readonly record struct Entry(object Track, object Points, double DistAway, Boundary Boundary, long Key,
        double KeepOut, double Radius, double Margin, long Pins, int PassCount, Vec3 PassStart, Vec3 PassEnd, bool Loop, Bend? Result);

    // The last few passes asked for: the line steered and the line shown can be two passes for
    // a moment, and each would otherwise throw the other's bend away.
    private readonly Entry?[] _cache = new Entry?[8];
    private int _next;

    /// <summary>
    /// The pass <paramref name="pass"/> (offset <paramref name="distAway"/> from
    /// <paramref name="track"/>) bent round its Go around obstacles, or null when it runs into
    /// none: the same result while the inputs are the same, so a cycle costs a comparison.
    /// <paramref name="pinnedSides"/> holds the side (+1 left, -1 right) each pinned obstacle keeps.
    /// <paramref name="loop"/>: the pass is a whole lap of a closed track (<see cref="Compute"/>).
    /// </summary>
    public Bend? For(Models.Track.Track track, double distAway, IReadOnlyList<Vec3> pass, Boundary? boundary,
        double keepOut, double radius, double margin, IReadOnlyDictionary<int, int>? pinnedSides = null, bool loop = false)
    {
        if (boundary == null || boundary.InnerBoundaries.Count == 0) return null;
        if (pass.Count < 2) return null;
        // Every corner of every obstacle and of the field's edge (the bend keeps the implement
        // inside it): a corner moved anywhere gets a bend of its own.
        long key = YouTurn.YouTurnCreationService.ObstacleStamp(boundary) * 31 + (boundary.OuterBoundary?.OutlineStamp ?? 0);
        long pins = PinsKey(pinnedSides);
        // The pass is part of the key by its shape: the pipeline's two-point AB pass and the
        // planner's densified one get bends of their own (the same geometry, on different points),
        // and the planner never walks the sparse one.
        Vec3 start = pass[0], end = pass[^1];
        foreach (var e in _cache)
            if (e is { } c && ReferenceEquals(c.Track, track) && ReferenceEquals(c.Points, track.Points)
                && c.DistAway == distAway && ReferenceEquals(c.Boundary, boundary) && c.Key == key
                && c.KeepOut == keepOut && c.Radius == radius && c.Margin == margin && c.Pins == pins
                && c.PassCount == pass.Count && SamePlace(c.PassStart, start) && SamePlace(c.PassEnd, end)
                && c.Loop == loop)
                return c.Result;

        var result = Compute(pass, boundary, keepOut, radius, margin, pinnedSides, loop);
        _cache[_next] = new Entry(track, track.Points, distAway, boundary, key, keepOut, radius, margin, pins,
            pass.Count, start, end, loop, result);
        _next = (_next + 1) % _cache.Length;
        return result;
    }

    private static bool SamePlace(Vec3 a, Vec3 b) => a.Easting == b.Easting && a.Northing == b.Northing;

    private static long PinsKey(IReadOnlyDictionary<int, int>? pins)
    {
        if (pins == null || pins.Count == 0) return 0;
        long h = pins.Count;
        foreach (var (inner, side) in pins.OrderBy(p => p.Key)) h = (h * 31 + inner) * 31 + side;
        return h;
    }

    // A Go around obstacle grown by the keep-out distance: the line stays outside it.
    private sealed class Fence
    {
        public Fence(int innerIndex, List<Vec2> ring)
        {
            InnerIndex = innerIndex;
            Ring = ring;
            MinE = ring.Min(p => p.Easting); MaxE = ring.Max(p => p.Easting);
            MinN = ring.Min(p => p.Northing); MaxN = ring.Max(p => p.Northing);
        }

        public int InnerIndex { get; }
        public List<Vec2> Ring { get; }
        public double MinE { get; }
        public double MaxE { get; }
        public double MinN { get; }
        public double MaxN { get; }

        public bool Holds(double e, double n) =>
            e >= MinE && e <= MaxE && n >= MinN && n <= MaxN && GeometryMath.IsPointInPolygon(Ring, e, n);
    }

    // An obstacle to go round: its grown outline across the pass, station by station.
    private sealed class Hit
    {
        public required Fence Fence { get; init; }
        public required int First { get; init; }      // station index of MinL[0]
        public required double[] MinL { get; init; }  // NaN where the outline is not
        public required double[] MaxL { get; init; }
        public required double NeedLeft { get; init; }
        public required double NeedRight { get; init; }
        public int Side;                               // +1 left of the pass (the way its points run), -1 right
        public bool Flipped;
        public int Group;                              // obstacles close along the pass go round together
        public int Last => First + MinL.Length - 1;
    }

    /// <summary>
    /// How far a trailed tool's working centre runs inside the tractor's line on a curve of this
    /// radius, once it has settled on it: the hitch is behind the tractor's pivot, the tool's
    /// wheels (and a TBT tank's) cut the corner, and a working centre ahead of or behind its
    /// wheels swings back out. Around a Go around obstacle the inside of the curve is the
    /// obstacle's side, so this is added to the safety margin. Zero for a front or rear fixed
    /// tool, which never runs inside the line. The same geometry as <c>ToolPositionService</c>:
    /// the hitch <paramref name="vehicleHitch"/> behind the pivot, the wheels the trailing hitch
    /// length behind it (a TBT tank's axle first, its own length behind the hitch), the working
    /// centre the pivot distance from the wheels.
    /// </summary>
    public static double TrailedCutIn(Models.Configuration.ToolConfig tool, double vehicleHitch, double radius)
    {
        if (!double.IsFinite(radius) || !(tool.IsToolTrailing || tool.IsToolTBT) || tool.IsToolFrontFixed || tool.IsToolRearFixed)
            return 0;
        radius = Math.Max(radius, 1.0); // the tightest bend Compute builds, whatever the setting says
        double h = Math.Abs(vehicleHitch), l = Math.Abs(tool.TrailingHitchLength), p = tool.TrailingToolToPivotLength;
        double r2 = radius * radius + h * h - l * l + p * p;
        if (tool.IsToolTBT) r2 -= tool.TankTrailingHitchLength * tool.TankTrailingHitchLength;
        return Math.Max(0, radius - Math.Sqrt(Math.Max(0, r2)));
    }

    /// <summary>
    /// How far the line keeps from a Go around obstacle: half the implement (and its offset), or
    /// half the tractor's track if that is wider, plus the safety margin.
    /// </summary>
    public static double KeepOutFor(double toolWidth, double toolOffset, double trackWidth, double margin) =>
        Math.Max(toolWidth / 2 + Math.Abs(toolOffset), trackWidth / 2 + 0.3) + Math.Max(0, margin);

    /// <summary>The bend, computed (no cache). Null when the pass runs into no Go around obstacle.</summary>
    /// <param name="keepOut">How far the line keeps from a Go around obstacle: half the implement and the margin.</param>
    /// <param name="radius">The tightest the line may bend.</param>
    /// <param name="margin">The safety margin in <paramref name="keepOut"/>: the bend keeps half the
    /// implement (keepOut less the margin) inside the field's edge.</param>
    /// <param name="pinnedSides">The side (+1 left, -1 right) each pinned obstacle keeps; one with no
    /// room on that side is blocked, not gone round the other way.</param>
    /// <param name="loop">The pass is a whole lap of a closed track, its last point joined to its
    /// first (repeated or not). It is walked from its point furthest from the Go around obstacles,
    /// so a bend never runs across its seam, and the bent lap comes back as a loop starting there,
    /// its first point not repeated at the end. A bend that would still reach the seam (a lap too
    /// short for its bends) is not made: those obstacles are blocked.</param>
    public Bend? Compute(IReadOnlyList<Vec3> pass, Boundary boundary, double keepOut, double radius, double margin = 1.0,
        IReadOnlyDictionary<int, int>? pinnedSides = null, bool loop = false)
    {
        if (pass.Count < 2 || keepOut <= 0) return null;
        loop = loop && pass.Count >= 3;
        var line = loop ? FromSeam(pass, boundary) : pass.Count == 2 ? ExtendLine(pass[0], pass[1]) : pass;
        var frame = new Frame(line);

        var fences = new List<Fence>();      // Go around obstacles, grown by the keep-out distance
        var others = new List<Fence>();      // obstacles chosen as U-turn: the bent line stays out of them
        var ungrown = new List<int>();       // Go around obstacles across the pass whose outline could not be grown
        var hits = new List<Hit>();
        for (int i = 0; i < boundary.InnerBoundaries.Count; i++)
        {
            var inner = boundary.InnerBoundaries[i];
            if (!inner.IsValid) continue;
            var outline = inner.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList();
            if (inner.AtObstacle == ObstacleAction.Turn) { others.Add(new Fence(i, outline)); continue; }
            if (inner.AtObstacle != ObstacleAction.GoRound) continue;
            var grown = _offset.CreateOutwardOffset(outline, keepOut);
            if (grown == null || grown.Count < 3)
            {
                // No grown outline, no bend. Across the pass it is told as one with no room (a
                // U-turn before it on this pass), not left out as if the pass were clear of it.
                if (Across(frame, new Fence(i, outline), force: false) != null) ungrown.Add(i);
                continue;
            }
            var fence = new Fence(i, grown);
            fences.Add(fence);
            if (Across(frame, fence, force: false) is { } hit) hits.Add(hit);
        }
        if (hits.Count == 0)
            return ungrown.Count == 0 ? null : new Bend(new List<Vec3>(pass), Array.Empty<int>(), ungrown, Array.Empty<RoundedObstacle>());
        radius = Math.Max(radius, 1.0);
        Group(hits, radius);
        Pin(hits, pinnedSides);

        var outer = boundary.OuterBoundary is { IsValid: true } ob
            ? ob.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList() : null;
        double edge = Math.Max(0, keepOut - Math.Max(0, margin));
        double most = MaxOffset(keepOut, margin);

        var blocked = new List<int>(ungrown);
        for (int attempt = 0; attempt < 3 * fences.Count + 3 && hits.Count > 0; attempt++)
        {
            // An obstacle whose bend would move the line further than a bend may is not gone round:
            // its side is the one with the smaller need, the other one already tried, or pinned.
            for (int i = hits.Count - 1; i >= 0; i--)
            {
                var h = hits[i];
                if ((h.Side > 0 ? h.NeedLeft : h.NeedRight) <= most) continue;
                blocked.Add(h.Fence.InnerIndex);
                hits.RemoveAt(i);
            }
            if (hits.Count == 0) break;

            var (first, offsets) = Profile(hits, radius);
            if (loop && (first < 0 || (first + offsets.Length - 1) * SampleStep > frame.Length))
            {
                // The bends reach round to the lap's seam, as far as the lap goes from every Go
                // around obstacle: a lap too short for them. Not gone round.
                foreach (var h in hits) if (!blocked.Contains(h.Fence.InnerIndex)) blocked.Add(h.Fence.InnerIndex);
                hits.Clear();
                break;
            }
            var (points, bases) = BentLine(frame, first, offsets);
            var conflict = FirstConflict(points, bases, fences, others, outer, edge);
            if (conflict == null)
            {
                // A lap: its last point is the seam again, which closes it.
                if (loop && points.Count > 2 && SamePlace(points[^1], points[0])) points.RemoveAt(points.Count - 1);
                CurveProcessing.CalculateHeadings(points);
                var rounds = hits.Select(h => new RoundedObstacle(h.Fence.InnerIndex, h.Side, h.Fence.Ring,
                    Lead(Math.Max(h.NeedLeft, h.NeedRight), radius) + BesideSlack)).ToList();
                return new Bend(points, hits.Select(h => h.Fence.InnerIndex).ToList(), blocked, rounds);
            }

            var (fenceHit, at, sideThere) = conflict.Value;
            Hit? culprit = fenceHit == null ? null : hits.FirstOrDefault(h => h.Fence == fenceHit);
            if (culprit == null && fenceHit != null && !blocked.Contains(fenceHit.InnerIndex)
                && Across(frame, fenceHit, force: true) is { } more
                && (sideThere > 0 ? more.NeedLeft : more.NeedRight) <= most)
            {
                // The way round one obstacle runs into the next: go round both, the same side
                // (a next one that would take the line too far is a conflict like any other).
                var by = Nearest(hits, at);
                more.Side = sideThere;
                more.Group = by.Group;
                more.Flipped = by.Flipped;
                hits.Add(more);
                continue;
            }
            culprit ??= Nearest(hits, at);
            // An obstacle with no way round on its other side either is not gone round, and it
            // alone: the obstacles going round with it keep their side. Sent to the other side
            // with it, each was "too far" there and none was gone round: an obstacle the pass
            // clips by 3 m, a few metres before one at the fence with no room on this pass, was
            // told as one with no room too (a tester's field, 26 Nov 2026). Fork only.
            bool roomOtherSide = (culprit.Side > 0 ? culprit.NeedRight : culprit.NeedLeft) <= most;
            if (!culprit.Flipped && roomOtherSide)
            {
                // The other side, for the obstacle and those going round with it.
                foreach (var h in hits.Where(h => h.Group == culprit.Group)) { h.Side = -h.Side; h.Flipped = true; }
            }
            else { blocked.Add(culprit.Fence.InnerIndex); hits.Remove(culprit); }
        }
        foreach (var h in hits) if (!blocked.Contains(h.Fence.InnerIndex)) blocked.Add(h.Fence.InnerIndex);
        return new Bend(new List<Vec3>(pass), Array.Empty<int>(), blocked, Array.Empty<RoundedObstacle>());
    }

    /// <summary>
    /// A lap walked from its point furthest from every Go around obstacle (its own points, the
    /// first not repeated) and back to it: the seam where the line starts and ends is where no
    /// bend is. Walked from where it was drawn, a lap whose seam lies beside an obstacle had its
    /// bend cut in two there.
    /// </summary>
    private static List<Vec3> FromSeam(IReadOnlyList<Vec3> lap, Boundary boundary)
    {
        int n = lap.Count;
        if (n > 2 && SamePlace(lap[n - 1], lap[0])) n--;
        var rings = new List<List<Vec2>>();
        foreach (var inner in boundary.InnerBoundaries)
            if (inner.IsValid && inner.AtObstacle == ObstacleAction.GoRound)
                rings.Add(inner.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList());
        int seam = 0;
        double furthest = double.NegativeInfinity;
        for (int i = 0; i < n && rings.Count > 0; i++)
        {
            double nearest = double.PositiveInfinity;
            foreach (var ring in rings)
            {
                nearest = Math.Min(nearest, DistanceToRing(ring, lap[i].Easting, lap[i].Northing));
                if (nearest <= furthest) break;
            }
            if (nearest > furthest) { furthest = nearest; seam = i; }
        }
        var line = new List<Vec3>(n + 1);
        for (int k = 0; k <= n; k++) line.Add(lap[(seam + k) % n]);
        return line;
    }

    /// <summary>Beyond the bend's lead, how much nearer than the start of the bend still counts as beside the obstacle.</summary>
    private const double BesideSlack = 2.0;

    // A pinned obstacle keeps its side, and so do the obstacles gone round with it; none of them
    // is tried the other way (Flipped): no room there means blocked.
    private static void Pin(List<Hit> hits, IReadOnlyDictionary<int, int>? pins)
    {
        if (pins == null || pins.Count == 0) return;
        foreach (var h in hits)
        {
            if (!pins.TryGetValue(h.Fence.InnerIndex, out int side)) continue;
            foreach (var g in hits.Where(x => x.Group == h.Group)) { g.Side = side; g.Flipped = true; }
        }
    }

    // Obstacles close together along the pass are gone round in one bend, on one side: the side
    // where the farthest any of them reaches across the pass is the nearer.
    private static void Group(List<Hit> hits, double radius)
    {
        hits.Sort((a, b) => a.First.CompareTo(b.First));
        int group = 0, reach = int.MinValue;
        var groups = new List<List<Hit>>();
        foreach (var h in hits)
        {
            // The bend for an obstacle starts and ends this far (in stations) before and after it.
            int lead = (int)Math.Ceiling(Lead(Math.Max(h.NeedLeft, h.NeedRight), radius) / SampleStep);
            if (groups.Count == 0 || h.First - lead > reach) { groups.Add(new List<Hit>()); group = groups.Count - 1; }
            h.Group = group;
            groups[group].Add(h);
            reach = Math.Max(reach, h.Last + lead);
        }
        foreach (var g in groups)
        {
            int side = g.Max(h => h.NeedLeft) <= g.Max(h => h.NeedRight) ? 1 : -1;
            foreach (var h in g) h.Side = side;
        }
    }

    // An AB line runs on past A and B: the bent line is a curve, so it gets real ends far off.
    private static List<Vec3> ExtendLine(Vec3 a, Vec3 b)
    {
        double de = b.Easting - a.Easting, dn = b.Northing - a.Northing, len = Math.Sqrt(de * de + dn * dn);
        if (len < 1e-6) return new List<Vec3> { a, b };
        double ue = de / len, un = dn / len;
        return new List<Vec3>
        {
            new(a.Easting - ue * LineExtension, a.Northing - un * LineExtension, a.Heading),
            new(b.Easting + ue * LineExtension, b.Northing + un * LineExtension, b.Heading),
        };
    }

    // The grown obstacle across the pass: for every station its nearest and farthest reach
    // sideways. Null when the pass doesn't run into it (it clears the implement already),
    // unless `force`: the way round another obstacle runs into this one.
    private static Hit? Across(Frame frame, Fence fence, bool force)
    {
        var grown = fence.Ring;
        var sl = new List<(double S, double L)>();
        for (int k = 0; k < grown.Count; k++)
        {
            var a = grown[k]; var b = grown[(k + 1) % grown.Count];
            double de = b.Easting - a.Easting, dn = b.Northing - a.Northing;
            int n = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(de * de + dn * dn) / (2 * SampleStep)));
            for (int j = 0; j < n; j++)
                sl.Add(frame.Project(a.Easting + de * j / n, a.Northing + dn * j / n));
        }
        double minS = sl.Min(p => p.S), maxS = sl.Max(p => p.S);
        if (!force && (sl.All(p => p.L > 0) || sl.All(p => p.L < 0))) return null;
        if (maxS < frame.Start || minS > frame.Length) return null;

        int first = (int)Math.Floor(minS / SampleStep), last = (int)Math.Ceiling(maxS / SampleStep);
        int count = last - first + 1;
        var minL = new double[count]; var maxL = new double[count];
        bool runsInto = false;
        double needLeft = 0, needRight = 0;
        for (int i = 0; i < count; i++)
        {
            double s = (first + i) * SampleStep, lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            for (int k = 0; k < sl.Count; k++)
            {
                var p = sl[k]; var q = sl[(k + 1) % sl.Count];
                if ((p.S - s) * (q.S - s) > 0 || p.S == q.S) continue;
                double l = p.L + (s - p.S) / (q.S - p.S) * (q.L - p.L);
                if (l < lo) lo = l;
                if (l > hi) hi = l;
            }
            if (lo > hi) { minL[i] = maxL[i] = double.NaN; continue; }
            minL[i] = lo; maxL[i] = hi;
            if (lo < 0 && hi > 0 && s >= frame.Start && s <= frame.Length) runsInto = true;
            needLeft = Math.Max(needLeft, hi);
            needRight = Math.Max(needRight, -lo);
        }
        if (!runsInto && !force) return null;
        return new Hit
        {
            Fence = fence, First = first, MinL = minL, MaxL = maxL,
            NeedLeft = needLeft, NeedRight = needRight, Side = needLeft <= needRight ? 1 : -1,
        };
    }

    // How far before an obstacle (and after it) the bend for a sideways move of `need` metres
    // can start, at most.
    private static double Lead(double need, double radius) =>
        need / MaxSlope + Math.Sqrt(4 * radius * need) + Math.Sqrt(2 * radius * need) + 1.0;

    // The sideways offset along the pass, from station index `first`: what every obstacle needs
    // on its side, rising and falling no steeper than MaxSlope, rounded to `radius`.
    private static (int First, double[] Offsets) Profile(List<Hit> hits, double radius)
    {
        double most = hits.Max(h => h.Side > 0 ? h.NeedLeft : h.NeedRight);
        int pad = (int)Math.Ceiling(Lead(most, radius) / SampleStep);
        int first = hits.Min(h => h.First) - pad;
        int last = hits.Max(h => h.First + h.MinL.Length - 1) + pad;
        int n = last - first + 1;
        var left = new double[n]; var right = new double[n];
        foreach (var h in hits)
            for (int i = 0; i < h.MinL.Length; i++)
            {
                if (double.IsNaN(h.MinL[i])) continue;
                int j = h.First + i - first;
                if (h.Side > 0) left[j] = Math.Max(left[j], Math.Max(0, h.MaxL[i]));
                else right[j] = Math.Max(right[j], Math.Max(0, -h.MinL[i]));
            }
        left = Round(left, radius); right = Round(right, radius);
        var offsets = new double[n];
        for (int i = 0; i < n; i++) offsets[i] = left[i] - right[i];
        return (first, offsets);
    }

    // The smallest smooth offset over what is needed, near enough: no steeper than MaxSlope, and
    // bending no tighter than `radius` either way. The need is limited in slope, then widened by
    // parabolas of radius 2R (each value spread as a cap: the tops get rounded) and narrowed again
    // by parabolas of radius R (each value lifted as a cup: the feet get rounded). That is the
    // Lasry-Lions smoothing: the result is never below the need (the cups are narrower than the
    // caps), curves no tighter than R on either side and keeps the slope limit.
    private static double[] Round(double[] need, double radius)
    {
        int n = need.Length;
        double drop = MaxSlope * SampleStep, most = 0;
        for (int i = 1; i < n; i++) need[i] = Math.Max(need[i], need[i - 1] - drop);
        for (int i = n - 2; i >= 0; i--) need[i] = Math.Max(need[i], need[i + 1] - drop);
        for (int i = 0; i < n; i++) most = Math.Max(most, need[i]);
        if (most <= 0) return need;

        // Caps: wide[i] = max over j of need[j] - d^2 / (4R).
        var wide = new double[n];
        int reach = (int)Math.Ceiling(Math.Sqrt(4 * radius * most) / SampleStep);
        double capK = SampleStep * SampleStep / (4 * radius);
        for (int i = 0; i < n; i++)
        {
            double m = need[i];
            for (int j = Math.Max(0, i - reach); j <= Math.Min(n - 1, i + reach); j++)
            {
                int d = j - i;
                double v = need[j] - d * d * capK;
                if (v > m) m = v;
            }
            wide[i] = m;
        }

        // Cups: out[i] = min over j of wide[j] + d^2 / (2R); past the ends the offset is 0.
        var result = new double[n];
        reach = (int)Math.Ceiling(Math.Sqrt(2 * radius * most) / SampleStep);
        double cupK = SampleStep * SampleStep / (2 * radius);
        for (int i = 0; i < n; i++)
        {
            double m = wide[i];
            for (int j = i - reach; j <= i + reach; j++)
            {
                int d = j - i;
                double v = (j >= 0 && j < n ? wide[j] : 0) + d * d * cupK;
                if (v < m) m = v;
            }
            result[i] = m;
        }
        return result;
    }

    // The pass with every stretch that has an offset replaced by its offset points; `bases`
    // holds, for each point, where on the pass it was moved from (null for a pass vertex).
    private static (List<Vec3> Points, List<(double E, double N)?> Bases) BentLine(Frame frame, int first, double[] offsets)
    {
        var result = new List<Vec3>();
        var bases = new List<(double E, double N)?>();
        int i = 0, n = offsets.Length, v = 0; // v: the next pass vertex to copy
        while (i < n)
        {
            if (Math.Abs(offsets[i]) < 1e-4) { i++; continue; }
            int start = Math.Max(0, i - 1), end = i;
            while (end < n - 1 && Math.Abs(offsets[end + 1]) >= 1e-4) end++;
            end = Math.Min(n - 1, end + 1);
            double s0 = (first + start) * SampleStep, s1 = (first + end) * SampleStep;
            while (v < frame.Count && frame.Station(v) < s0) { result.Add(frame.Vertex(v++)); bases.Add(null); }
            for (int k = start; k <= end; k++)
            {
                double s = (first + k) * SampleStep;
                // Only where the pass is: a station before its first point (the first point's
                // own station is not 0 on a straight pass) is no place on it.
                if (s < frame.Start || s > frame.Length) continue;
                var (e, nn, ue, un) = frame.At(s);
                result.Add(new Vec3(e - un * offsets[k], nn + ue * offsets[k], 0));
                bases.Add((e, nn));
            }
            while (v < frame.Count && frame.Station(v) <= s1) v++;
            i = end + 1;
        }
        while (v < frame.Count) { result.Add(frame.Vertex(v++)); bases.Add(null); }
        return (result, bases);
    }

    // Where the bent line first goes wrong, among the points it moved: into a grown Go around
    // obstacle (which, and the side the line is on there), into a U-turn obstacle or closer to it
    // than `edge` (half the implement; unless it moved away from it), or closer to the field's
    // edge than `edge` (unless it moved away from the edge). Null when it is clear.
    // The pass's own points are not tested: a pass through an obstacle with no room (blocked) is
    // the U-turn planner's business, not the bend's, and a dense pass has such points where a
    // two-point line has none.
    private static (Fence? Fence, Vec3 At, int Side)? FirstConflict(List<Vec3> points,
        List<(double E, double N)?> bases, List<Fence> fences, List<Fence> others, List<Vec2>? outer, double edge)
    {
        for (int k = 0; k < points.Count; k++)
        {
            if (bases[k] is not { } b) continue;
            if (Math.Abs(points[k].Easting - b.E) + Math.Abs(points[k].Northing - b.N) < 1e-3) continue;
            var p = points[k];
            int side = 1;
            if (k + 1 < points.Count)
            {
                // Which side of the pass this point was moved to.
                var q = points[k + 1];
                double ue = q.Easting - p.Easting, un = q.Northing - p.Northing;
                side = ue * (p.Northing - b.N) - un * (p.Easting - b.E) > 0 ? 1 : -1;
            }
            foreach (var f in fences)
                // The rounding keeps the line outside each grown outline; a few centimetres of
                // sampling are let through.
                if (f.Holds(p.Easting, p.Northing) && DistanceToRing(f.Ring, p.Easting, p.Northing) > 0.05)
                    return (f, p, side);
            foreach (var o in others)
            {
                if (p.Easting < o.MinE - edge || p.Easting > o.MaxE + edge
                    || p.Northing < o.MinN - edge || p.Northing > o.MaxN + edge) continue;
                if (o.Holds(p.Easting, p.Northing)) return (null, p, side);
                // The line stayed out of the obstacle but the bend took the implement's end into
                // it: nearer than half the implement, and nearer than the pass itself runs.
                double near = DistanceToRing(o.Ring, p.Easting, p.Northing);
                if (near < edge && near < DistanceToRing(o.Ring, b.E, b.N) - 0.01) return (null, p, side);
            }
            if (outer == null) continue;
            // The end of a bend dies away over its last metres. Where the pass leaves the field
            // just after an obstacle, the bend is back on the pass at the fence but for a few
            // millimetres, and such a point outside the fence (as the pass is there) is not a
            // bend out of the field: 4.7 mm of one took the bend round two obstacles away on a
            // tester's field (26 Nov 2026). Fork only.
            double de = p.Easting - b.E, dn = p.Northing - b.N;
            if (de * de + dn * dn < EdgeTail * EdgeTail) continue;
            if (!GeometryMath.IsPointInPolygon(outer, p.Easting, p.Northing)) return (null, p, side);
            double d = DistanceToRing(outer, p.Easting, p.Northing);
            if (d < edge && d < DistanceToRing(outer, b.E, b.N) - 0.01) return (null, p, side);
        }
        return null;
    }

    private static Hit Nearest(List<Hit> hits, Vec3 p)
    {
        Hit best = hits[0]; double bestD = double.MaxValue;
        foreach (var h in hits)
        {
            double d = DistanceToRing(h.Fence.Ring, p.Easting, p.Northing);
            if (d < bestD) { bestD = d; best = h; }
        }
        return best;
    }

    /// <summary>The distance from (<paramref name="e"/>, <paramref name="n"/>) to the nearest edge of a ring.</summary>
    public static double DistanceToRing(IReadOnlyList<Vec2> ring, double e, double n)
    {
        double best = double.MaxValue;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            double ax = ring[j].Easting, ay = ring[j].Northing, dx = ring[i].Easting - ax, dy = ring[i].Northing - ay;
            double l2 = dx * dx + dy * dy;
            double t = l2 > 0 ? Math.Clamp(((e - ax) * dx + (n - ay) * dy) / l2, 0, 1) : 0;
            double qx = ax + t * dx - e, qy = ay + t * dy - n;
            best = Math.Min(best, Math.Sqrt(qx * qx + qy * qy));
        }
        return best;
    }

    // Stations along the pass and the side offset: S from the first point, L to the left
    // (positive) of the way the points run.
    private sealed class Frame
    {
        private readonly IReadOnlyList<Vec3> _p;
        private readonly double[] _cum;

        public Frame(IReadOnlyList<Vec3> points)
        {
            _p = points;
            _cum = new double[points.Count];
            _cum[0] = StraightStart(points);
            for (int i = 1; i < points.Count; i++)
            {
                double de = points[i].Easting - points[i - 1].Easting, dn = points[i].Northing - points[i - 1].Northing;
                _cum[i] = _cum[i - 1] + Math.Sqrt(de * de + dn * dn);
            }
        }

        // A straight pass is measured from a fixed place on its own line, not from its first
        // point, so the stations (every SampleStep) fall on the same places whatever points the
        // pass is given by. The pipeline asks with an AB pass's two points and the U-turn planner
        // with a point every 2 m across the field: measured each from its first point, their
        // stations lay up to a SampleStep apart, and near a limit (a bend ending at the fence)
        // the two decided differently for the same pass: no room for the one, which then steered
        // the pass straight, and a bend for the other, which planned no U-turn before the
        // obstacle (a tester's field, 26 Nov 2026). A curve is given by the same points to both.
        // Fork only.
        private static double StraightStart(IReadOnlyList<Vec3> p)
        {
            Vec3 a = p[0], b = p[^1];
            double de = b.Easting - a.Easting, dn = b.Northing - a.Northing, len = Math.Sqrt(de * de + dn * dn);
            if (len < 1e-6) return 0;
            double ue = de / len, un = dn / len;
            for (int i = 1; i < p.Count - 1; i++)
                if (Math.Abs(ue * (p[i].Northing - a.Northing) - un * (p[i].Easting - a.Easting)) > 1e-3) return 0;
            double along = a.Easting * ue + a.Northing * un;
            return along - Math.Floor(along / SampleStep) * SampleStep;
        }

        public int Count => _p.Count;
        /// <summary>The station of the first point: 0, or under a SampleStep on a straight pass.</summary>
        public double Start => _cum[0];
        /// <summary>The station of the last point.</summary>
        public double Length => _cum[^1];
        public double Station(int i) => _cum[i];
        public Vec3 Vertex(int i) => _p[i];

        public (double S, double L) Project(double e, double n)
        {
            double bestD = double.MaxValue, bestS = 0, bestL = 0;
            for (int i = 0; i < _p.Count - 1; i++)
            {
                double ax = _p[i].Easting, ay = _p[i].Northing;
                double dx = _p[i + 1].Easting - ax, dy = _p[i + 1].Northing - ay;
                double len = _cum[i + 1] - _cum[i];
                if (len < 1e-9) continue;
                double t = ((e - ax) * dx + (n - ay) * dy) / (len * len);
                double tc = Math.Clamp(t, 0, 1);
                double qx = ax + tc * dx - e, qy = ay + tc * dy - n;
                double d = qx * qx + qy * qy;
                if (d < bestD)
                {
                    bestD = d;
                    bestS = _cum[i] + tc * len;
                    bestL = (dx * (n - ay) - dy * (e - ax)) / len; // cross: positive on the left
                }
            }
            return (bestS, bestL);
        }

        /// <summary>The point at station s and the unit direction there.</summary>
        public (double E, double N, double Ue, double Un) At(double s)
        {
            int i = Array.BinarySearch(_cum, s);
            if (i < 0) i = ~i - 1;
            i = Math.Clamp(i, 0, _p.Count - 2);
            double len = _cum[i + 1] - _cum[i];
            double de = _p[i + 1].Easting - _p[i].Easting, dn = _p[i + 1].Northing - _p[i].Northing;
            double t = len > 1e-9 ? (s - _cum[i]) / len : 0;
            return (_p[i].Easting + de * t, _p[i].Northing + dn * t, len > 1e-9 ? de / len : 0, len > 1e-9 ? dn / len : 1);
        }
    }
}
