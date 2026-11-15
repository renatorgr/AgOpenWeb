// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Models.Headland;

/// <summary>
/// A headland wide only where the passes end (fork only). The crop is every point of the field
/// with at least <c>ends</c> metres of field ahead of it and behind it along the passes, and at
/// least <c>sides</c> metres from the boundary in any direction. So a stretch of the boundary
/// square to the passes, where they end and the U-turns are made, gets the ends' width; one
/// along the passes gets the sides' width; a slanted one gets the ends' width times the sine
/// of its angle to the passes (never under the sides'); and a spike of the field too narrow
/// for a turn is all headland. One width all round (Whole Boundary) is the ends' width at the
/// sides too, crop lost; lines drawn per edge run straight on past their ends and cut pieces
/// off an irregular field at a step in its boundary.
/// <para>
/// Worked out on a grid in the passes' own frame (x along them): each row's crossings of the
/// boundary give the room along the passes, the boundary's edges by tiles the distance to it,
/// and marching squares the crop's edge; the largest ring is kept (the headland is one ring),
/// thinned to 5 cm. About 0.3 s on a 2 km boundary.
/// </para>
/// </summary>
public static class HeadlandAlongPasses
{
    /// <summary>The grid's cell (m); a large field gets a coarser one, to keep under <see cref="MaxCells"/>.</summary>
    public const double Cell = 0.25;

    /// <summary>The most cells the grid may have (a float each).</summary>
    public const int MaxCells = 8_000_000;

    /// <summary>How far the thinned line may stray from the grid's (m).</summary>
    public const double Simplify = 0.05;

    /// <summary>
    /// The crop's edge, the headland line: a ring (open, its first point not repeated) inside
    /// <paramref name="boundary"/> and turning the same way, or an empty list when nothing is
    /// left of the field. <paramref name="passHeading"/> is the passes' heading in radians
    /// (clockwise from north: east = sin, north = cos); either way along them gives the same line.
    /// </summary>
    public static List<Vec3> Build(IReadOnlyList<Vec3> boundary, double passHeading, double ends, double sides)
    {
        var none = new List<Vec3>();
        if (boundary == null || !double.IsFinite(passHeading) || !double.IsFinite(ends) || !double.IsFinite(sides)
            || ends < 0 || sides < 0)
            return none;
        int n = boundary.Count;
        if (n >= 2 && Math.Abs(boundary[0].Easting - boundary[n - 1].Easting) < 1e-9
                   && Math.Abs(boundary[0].Northing - boundary[n - 1].Northing) < 1e-9)
            n--;
        if (n < 3) return none;

        // The passes' frame: x along them, y to their right.
        double ue = Math.Sin(passHeading), un = Math.Cos(passHeading);
        double re = un, rn = -ue;
        var qx = new double[n];
        var qy = new double[n];
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            double e = boundary[i].Easting, no = boundary[i].Northing;
            qx[i] = e * ue + no * un;
            qy[i] = e * re + no * rn;
            minX = Math.Min(minX, qx[i]); maxX = Math.Max(maxX, qx[i]);
            minY = Math.Min(minY, qy[i]); maxY = Math.Max(maxY, qy[i]);
        }
        // Two metres round it, so the crop's edge never touches the grid's.
        minX -= 2; maxX += 2; minY -= 2; maxY += 2;
        double cell = Math.Max(Cell, Math.Sqrt((maxX - minX) * (maxY - minY) / MaxCells));
        int nx = (int)Math.Ceiling((maxX - minX) / cell) + 1, ny = (int)Math.Ceiling((maxY - minY) / cell) + 1;

        // The boundary's edges by tiles, each in every tile within R of it: the distance to the
        // boundary is wanted only up to the sides' width and a little more.
        double reach = sides + 2;
        const double T = 20;
        int tx = (int)Math.Ceiling((maxX - minX) / T) + 1, ty = (int)Math.Ceiling((maxY - minY) / T) + 1;
        var tiles = new List<int>?[tx * ty];
        for (int i = 0; i < n; i++)
        {
            int k = (i + 1) % n;
            int i0 = Math.Max(0, (int)Math.Floor((Math.Min(qx[i], qx[k]) - reach - minX) / T));
            int i1 = Math.Min(tx - 1, (int)Math.Floor((Math.Max(qx[i], qx[k]) + reach - minX) / T));
            int j0 = Math.Max(0, (int)Math.Floor((Math.Min(qy[i], qy[k]) - reach - minY) / T));
            int j1 = Math.Min(ty - 1, (int)Math.Floor((Math.Max(qy[i], qy[k]) + reach - minY) / T));
            for (int ti = i0; ti <= i1; ti++)
                for (int tj = j0; tj <= j1; tj++)
                    (tiles[tj * tx + ti] ??= new List<int>()).Add(i);
        }

        // Each cell's value: how much it is inside the crop (> 0) or out of it (<= 0), by the
        // smaller of its room along the passes less the ends' width and its distance to the
        // boundary less the sides'. Outside the field: -1.
        var v = new float[nx * ny];
        Array.Fill(v, -1f);
        var xs = new List<double>();
        for (int j = 0; j < ny; j++)
        {
            double y = minY + j * cell;
            xs.Clear();
            for (int i = 0, k = n - 1; i < n; k = i++)
                if ((qy[i] > y) != (qy[k] > y))
                    xs.Add(qx[i] + (y - qy[i]) * (qx[k] - qx[i]) / (qy[k] - qy[i]));
            xs.Sort();
            for (int q = 0; q + 1 < xs.Count; q += 2)
            {
                double x0 = xs[q], x1 = xs[q + 1];
                for (int i = Math.Max(0, (int)Math.Ceiling((x0 - minX) / cell)); i < nx && minX + i * cell <= x1; i++)
                {
                    double x = minX + i * cell;
                    double f = Math.Min(x - x0, x1 - x) - ends;
                    if (f > -2)
                    {
                        double d = reach;
                        var edges = tiles[(int)((y - minY) / T) * tx + (int)((x - minX) / T)];
                        if (edges != null)
                            foreach (int a in edges)
                            {
                                int b = (a + 1) % n;
                                d = Math.Min(d, DistToSegment(x, y, qx[a], qy[a], qx[b], qy[b]));
                            }
                        f = Math.Min(f, d - sides);
                    }
                    v[j * nx + i] = f == 0 ? 1e-6f : (float)f;
                }
            }
        }

        var rings = Contours(v, nx, ny);
        if (rings.Count == 0) return none;

        // The largest ring, as points in the field's plane.
        List<long>? best = null;
        double bestArea = 0;
        foreach (var ring in rings)
        {
            double a = Math.Abs(SignedArea(ring, v, nx, minX, minY, cell));
            if (a > bestArea) { bestArea = a; best = ring; }
        }
        if (best == null || bestArea <= 0) return none;
        var px = new List<double>(best.Count);
        var py = new List<double>(best.Count);
        foreach (long key in best)
        {
            var (x, y) = Position(key, v, nx, minX, minY, cell);
            px.Add(ue * x + re * y);
            py.Add(un * x + rn * y);
        }

        // Turning the way the boundary turns.
        double sb = 0, sl = 0;
        for (int i = 0, k = n - 1; i < n; k = i++)
            sb += boundary[k].Easting * boundary[i].Northing - boundary[i].Easting * boundary[k].Northing;
        for (int i = 0, k = px.Count - 1; i < px.Count; k = i++)
            sl += px[k] * py[i] - px[i] * py[k];
        if (Math.Sign(sb) != Math.Sign(sl)) { px.Reverse(); py.Reverse(); }

        // Thinned: Douglas-Peucker on the two halves, split at the point furthest from the first.
        int far = 0;
        double farD = 0;
        for (int i = 1; i < px.Count; i++)
        {
            double d = (px[i] - px[0]) * (px[i] - px[0]) + (py[i] - py[0]) * (py[i] - py[0]);
            if (d > farD) { farD = d; far = i; }
        }
        var keep = new bool[px.Count + 1];
        keep[0] = keep[far] = true;
        Thin(px, py, 0, far, keep);
        Thin(px, py, far, px.Count, keep);
        var line = new List<Vec3>();
        for (int i = 0; i < px.Count; i++)
            if (keep[i]) line.Add(new Vec3(px[i], py[i], 0));
        return line.Count >= 3 ? line : none;
    }

    private static double DistToSegment(double x, double y, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
        double t = l2 > 0 ? Math.Clamp(((x - ax) * dx + (y - ay) * dy) / l2, 0, 1) : 0;
        double ex = ax + t * dx - x, ey = ay + t * dy - y;
        return Math.Sqrt(ex * ex + ey * ey);
    }

    // A crossing of the zero level on the grid: on the edge from cell (i, j) to (i + 1, j)
    // (even keys) or to (i, j + 1) (odd keys).
    private static long KeyH(int i, int j, int nx) => 2L * ((long)j * nx + i);
    private static long KeyV(int i, int j, int nx) => 2L * ((long)j * nx + i) + 1;

    private static (double x, double y) Position(long key, float[] v, int nx, double minX, double minY, double cell)
    {
        long c = key >> 1;
        int i = (int)(c % nx), j = (int)(c / nx);
        double a = v[j * nx + i];
        if ((key & 1) == 0)
        {
            double b = v[j * nx + i + 1], t = a / (a - b);
            return (minX + (i + t) * cell, minY + j * cell);
        }
        else
        {
            double b = v[(j + 1) * nx + i], t = a / (a - b);
            return (minX + i * cell, minY + (j + t) * cell);
        }
    }

    private static double SignedArea(List<long> ring, float[] v, int nx, double minX, double minY, double cell)
    {
        double s = 0;
        var (lx, ly) = Position(ring[^1], v, nx, minX, minY, cell);
        foreach (long key in ring)
        {
            var (x, y) = Position(key, v, nx, minX, minY, cell);
            s += lx * y - x * ly;
            lx = x; ly = y;
        }
        return s / 2;
    }

    /// <summary>The closed rings where the values cross zero (marching squares), as keys of the crossings.</summary>
    private static List<List<long>> Contours(float[] v, int nx, int ny)
    {
        var segs = new List<(long a, long b)>();
        var at = new Dictionary<long, (int s1, int s2)>();
        void Add(long k1, long k2)
        {
            int id = segs.Count;
            segs.Add((k1, k2));
            foreach (long k in new[] { k1, k2 })
                at[k] = at.TryGetValue(k, out var p) ? (p.s1, id) : (id, -1);
        }
        for (int j = 0; j + 1 < ny; j++)
            for (int i = 0; i + 1 < nx; i++)
            {
                float va = v[j * nx + i], vb = v[j * nx + i + 1], vc = v[(j + 1) * nx + i + 1], vd = v[(j + 1) * nx + i];
                int k = (va > 0 ? 1 : 0) | (vb > 0 ? 2 : 0) | (vc > 0 ? 4 : 0) | (vd > 0 ? 8 : 0);
                if (k == 0 || k == 15) continue;
                long bot = KeyH(i, j, nx), right = KeyV(i + 1, j, nx), top = KeyH(i, j + 1, nx), left = KeyV(i, j, nx);
                bool mid = (va + vb + vc + vd) / 4 > 0;
                switch (k)
                {
                    case 1: case 14: Add(left, bot); break;
                    case 2: case 13: Add(bot, right); break;
                    case 3: case 12: Add(left, right); break;
                    case 4: case 11: Add(right, top); break;
                    case 6: case 9: Add(bot, top); break;
                    case 7: case 8: Add(left, top); break;
                    case 5: if (mid) { Add(bot, right); Add(left, top); } else { Add(left, bot); Add(right, top); } break;
                    case 10: if (mid) { Add(left, bot); Add(right, top); } else { Add(bot, right); Add(left, top); } break;
                }
            }

        var used = new bool[segs.Count];
        var rings = new List<List<long>>();
        for (int s0 = 0; s0 < segs.Count; s0++)
        {
            if (used[s0]) continue;
            used[s0] = true;
            long start = segs[s0].a, cur = segs[s0].b;
            var ring = new List<long> { start };
            for (int guard = 0; cur != start && guard < segs.Count; guard++)
            {
                ring.Add(cur);
                var (p1, p2) = at[cur];
                int next = p1 >= 0 && !used[p1] ? p1 : p2 >= 0 && !used[p2] ? p2 : -1;
                if (next < 0) break;
                used[next] = true;
                cur = segs[next].a == cur ? segs[next].b : segs[next].a;
            }
            if (cur == start && ring.Count > 3) rings.Add(ring);
        }
        return rings;
    }

    /// <summary>Douglas-Peucker between <paramref name="i0"/> and <paramref name="i1"/> (i1 may be
    /// the ring's length: the first point again), marking the points kept.</summary>
    private static void Thin(List<double> px, List<double> py, int i0, int i1, bool[] keep)
    {
        int count = px.Count;
        var stack = new Stack<(int, int)>();
        stack.Push((i0, i1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            double ax = px[a % count], ay = py[a % count], bx = px[b % count], by = py[b % count];
            double dmax = 0;
            int im = -1;
            for (int i = a + 1; i < b; i++)
            {
                double d = DistToSegment(px[i], py[i], ax, ay, bx, by);
                if (d > dmax) { dmax = d; im = i; }
            }
            if (dmax > Simplify) { keep[im] = true; stack.Push((a, im)); stack.Push((im, b)); }
        }
    }
}
