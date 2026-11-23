// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Numerics;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services.Coverage;

/// <summary>
/// Counts the coverage map's detection cells (0.1 m) inside an area: how much ground the area holds and how
/// much of it is painted. A cell counts when its centre is inside the area's rings (even-odd: an outer ring and
/// the holes in it, such as a field's boundary and its obstacles) and, with a clip, inside the clip's rings too
/// (a selection drawn on the map, kept to the field). Worked a row of cells at a time: the rings' crossings of
/// the row's centre line give the stretches inside, and the covered bits of each stretch are counted a word at
/// a time. A 15 ha field is about 15 million cells and a few milliseconds. Cells outside the detection grid are
/// inside but not covered. Fork only.
/// </summary>
public static class CoverageAreaCount
{
    /// <summary>The cells inside and the covered ones among them.</summary>
    public readonly record struct Result(long InsideCells, long CoveredCells);

    /// <summary>A ring's edge, lowest end first, with the easting's change per metre of northing.</summary>
    private readonly record struct Edge(double Y0, double Y1, double X0, double Slope);

    /// <summary>The detection bits as the coverage map keeps them: row-major, a bit per cell, the lowest
    /// bit of a byte first; <paramref name="originE"/>/<paramref name="originN"/> the cell coordinates of
    /// bit 0. <paramref name="bits"/> null is a map with nothing painted.</summary>
    public static Result Count(byte[]? bits, int originE, int originN, int width, int height, double cellSize,
        IReadOnlyList<IReadOnlyList<Vec2>> area, IReadOnlyList<IReadOnlyList<Vec2>>? clip = null)
    {
        var areaEdges = EdgesOf(area, out double minY, out double maxY);
        if (areaEdges.Length == 0) return default;
        Edge[]? clipEdges = null;
        if (clip != null)
        {
            clipEdges = EdgesOf(clip, out double cMin, out double cMax);
            if (clipEdges.Length == 0) return default;
            minY = Math.Max(minY, cMin);
            maxY = Math.Min(maxY, cMax);
            if (!(maxY > minY)) return default;
        }

        long firstRow = (long)Math.Ceiling(minY / cellSize - 0.5);
        long endRow = (long)Math.Ceiling(maxY / cellSize - 0.5);
        var areaRuns = new List<(long From, long To)>();
        var clipRuns = new List<(long From, long To)>();
        var runs = new List<(long From, long To)>();
        var xs = new List<double>();
        var areaActive = new ActiveEdges(areaEdges);
        var clipActive = clipEdges != null ? new ActiveEdges(clipEdges) : null;
        long inside = 0, covered = 0;
        bool haveBits = bits != null && width > 0 && height > 0;

        for (long row = firstRow; row < endRow; row++)
        {
            double y = (row + 0.5) * cellSize;
            RowRuns(areaActive, y, cellSize, xs, areaRuns);
            if (areaRuns.Count == 0) continue;
            List<(long From, long To)> rowRuns = areaRuns;
            if (clipActive != null)
            {
                RowRuns(clipActive, y, cellSize, xs, clipRuns);
                Intersect(areaRuns, clipRuns, runs);
                rowRuns = runs;
            }
            long localRow = row - originN;
            bool rowOnGrid = haveBits && localRow >= 0 && localRow < height;
            foreach (var (from, to) in rowRuns)
            {
                inside += to - from;
                if (!rowOnGrid) continue;
                long a = Math.Max(from - originE, 0), b = Math.Min(to - originE, width);
                if (b > a) covered += CountBits(bits!, localRow * width + a, localRow * width + b);
            }
        }
        return new Result(inside, covered);
    }

    /// <summary>The edges of every ring with three corners or more, the closing edge too; flat edges left
    /// out (a row's centre line never crosses one).</summary>
    private static Edge[] EdgesOf(IReadOnlyList<IReadOnlyList<Vec2>> rings, out double minY, out double maxY)
    {
        var edges = new List<Edge>();
        minY = double.PositiveInfinity; maxY = double.NegativeInfinity;
        foreach (var ring in rings)
        {
            if (ring == null || ring.Count < 3) continue;
            for (int i = 0; i < ring.Count; i++)
            {
                var p = ring[i];
                var q = ring[(i + 1) % ring.Count];
                if (!double.IsFinite(p.Easting) || !double.IsFinite(p.Northing)
                    || !double.IsFinite(q.Easting) || !double.IsFinite(q.Northing)) continue;
                if (p.Northing == q.Northing) continue;
                var (lo, hi) = p.Northing < q.Northing ? (p, q) : (q, p);
                edges.Add(new Edge(lo.Northing, hi.Northing, lo.Easting,
                    (hi.Easting - lo.Easting) / (hi.Northing - lo.Northing)));
                minY = Math.Min(minY, lo.Northing);
                maxY = Math.Max(maxY, hi.Northing);
            }
        }
        edges.Sort((a, b) => a.Y0.CompareTo(b.Y0));
        return edges.ToArray();
    }

    /// <summary>The edges a row's centre line crosses, walked up the rows in order: an edge joins when the
    /// rows reach its lower end and leaves past its upper end, so a row looks at the edges beside it only.</summary>
    private sealed class ActiveEdges
    {
        private readonly Edge[] _edges;
        private int _next;
        public readonly List<Edge> Now = new();
        public ActiveEdges(Edge[] edges) => _edges = edges;

        public void MoveTo(double y)
        {
            while (_next < _edges.Length && _edges[_next].Y0 <= y) Now.Add(_edges[_next++]);
            Now.RemoveAll(e => e.Y1 <= y);
        }
    }

    /// <summary>The stretches of cells (from, to: cell columns, to not included) whose centres lie inside the
    /// rings on the row at <paramref name="y"/>: the crossings sorted and taken in pairs (even-odd).</summary>
    private static void RowRuns(ActiveEdges active, double y, double cellSize, List<double> xs, List<(long, long)> runs)
    {
        runs.Clear();
        xs.Clear();
        active.MoveTo(y);
        foreach (var e in active.Now)
            if (e.Y0 <= y && y < e.Y1) xs.Add(e.X0 + (y - e.Y0) * e.Slope);
        if (xs.Count < 2) return;
        xs.Sort();
        for (int i = 0; i + 1 < xs.Count; i += 2)
        {
            long from = (long)Math.Ceiling(xs[i] / cellSize - 0.5);
            long to = (long)Math.Ceiling(xs[i + 1] / cellSize - 0.5);
            if (to > from) runs.Add((from, to));
        }
    }

    /// <summary>The cells in a stretch of both lists (each sorted, its stretches apart).</summary>
    private static void Intersect(List<(long From, long To)> a, List<(long From, long To)> b, List<(long, long)> result)
    {
        result.Clear();
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
        {
            long from = Math.Max(a[i].From, b[j].From), to = Math.Min(a[i].To, b[j].To);
            if (to > from) result.Add((from, to));
            if (a[i].To < b[j].To) i++; else j++;
        }
    }

    /// <summary>Set bits from bit <paramref name="from"/> up to <paramref name="to"/> (not included).</summary>
    internal static long CountBits(byte[] bits, long from, long to)
    {
        long count = 0;
        to = Math.Min(to, (long)bits.Length * 8);
        while (from < to && (from & 7) != 0)
        {
            if ((bits[from >> 3] & (1 << (int)(from & 7))) != 0) count++;
            from++;
        }
        while (to - from >= 64)
        {
            count += BitOperations.PopCount(BitConverter.ToUInt64(bits, (int)(from >> 3)));
            from += 64;
        }
        while (to - from >= 8)
        {
            count += BitOperations.PopCount((uint)bits[from >> 3]);
            from += 8;
        }
        while (from < to)
        {
            if ((bits[from >> 3] & (1 << (int)(from & 7))) != 0) count++;
            from++;
        }
        return count;
    }
}
