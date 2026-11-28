// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;
using Clipper2Lib;

namespace AgOpenWeb.Services.YouTurn;

/// <summary>
/// A U-turn seated back along its pass: the turn area cut off at a line square to the pass (the
/// wall), so the turn is made there as at the end of a field, every fence and obstacle beside the
/// pass where it is. Fork only.
/// <para>
/// A turn that does not fit at the end of its pass (the implement over a hard fence, a landing on
/// a bend, an obstacle in its legs or exit) is tried again further back. That is done by moving
/// every turn line in (<see cref="YouTurnCreationService.CreateTurnPath"/>'s setback), which also
/// moves in the fences beside the pass. On a pass nearer a side fence than the setback the line
/// moved in took the whole pass: no end of the pass was left to turn at, and the operator read
/// "Too late for a U-turn at this end" 40 m before the end (a tester's fields, the farm job harness,
/// 28 Nov 2026). The same happened to the pass the turn lands on when that one runs beside a side
/// fence: "No U-turn fits inside the boundary at this end". When a try with the lines moved in
/// builds no turn, the same setback is tried with the wall, and the tries after it too.
/// </para>
/// </summary>
internal static class PassSeatWall
{
    private const double Scale = 1000.0;
    // Far past any field, either way along the wall and back from it.
    private const double Far = 20000.0;

    /// <summary>The wall: a point of the pass and the way the tractor drives there (a unit vector).</summary>
    public readonly record struct Wall(double Easting, double Northing, double DirE, double DirN);

    /// <summary>
    /// Where the wall stands: <paramref name="setback"/> m back along <paramref name="pass"/> from its
    /// end (<paramref name="endIndex"/>, the first point outside the turn area on the first try)
    /// towards the tractor (<paramref name="tractorIndex"/>, the point of the pass nearest
    /// <paramref name="tractor"/>; <paramref name="step"/> is the way the tractor goes through the
    /// points, 1 or -1). Null when that is behind the tractor's point, or on the stretch that starts at
    /// that point and not more than <paramref name="minAhead"/> m ahead of the tractor itself. That
    /// stretch is judged by the tractor's place, not by its point: on a pass with points far apart (a
    /// curve drawn by taps has one every 20 m) it is most of the way to the end.
    /// </summary>
    public static Wall? At(IReadOnlyList<Vec3> pass, int endIndex, int step, int tractorIndex, double setback,
        Vec3 tractor, double minAhead)
    {
        if (endIndex < 0 || endIndex >= pass.Count || step == 0) return null;
        double left = Math.Max(0.0, setback);
        for (int j = endIndex; ; j -= step)
        {
            int k = j - step;
            if (k < 0 || k >= pass.Count || (k - tractorIndex) * step < 0) return null;
            double de = pass[j].Easting - pass[k].Easting, dn = pass[j].Northing - pass[k].Northing;
            double seg = Math.Sqrt(de * de + dn * dn);
            if (seg < 1e-6) continue;
            if (left <= seg)
            {
                double t = left / seg;
                var wall = new Wall(pass[j].Easting - de * t, pass[j].Northing - dn * t, de / seg, dn / seg);
                if (k == tractorIndex
                    && (wall.Easting - tractor.Easting) * wall.DirE + (wall.Northing - tractor.Northing) * wall.DirN <= minAhead)
                    return null;
                return wall;
            }
            left -= seg;
        }
    }

    /// <summary>
    /// Where the pass must be inside the turn line for a turn to be seated at <paramref name="wall"/>:
    /// half a metre before it.
    /// </summary>
    public static Vec2 Probe(Wall wall) => new(wall.Easting - wall.DirE * 0.5, wall.Northing - wall.DirN * 0.5);

    /// <summary>
    /// <paramref name="turnLine"/> (the outer turn line, a ring) cut off at <paramref name="wall"/>:
    /// the piece of it before the wall that the pass is in. Null when the pass is not inside the turn
    /// line just before the wall (a stretch outside it the pass is driven through), or nothing is left.
    /// </summary>
    public static List<Vec2>? Cut(IReadOnlyList<Vec2> turnLine, Wall wall)
    {
        if (turnLine.Count < 3) return null;
        var ring = new Path64(turnLine.Count);
        foreach (var p in turnLine) ring.Add(P(p.Easting, p.Northing));
        // Everything before the wall: a rectangle from the wall back, the way the tractor came.
        double nE = -wall.DirN, nN = wall.DirE;
        var before = new Path64
        {
            P(wall.Easting + nE * Far, wall.Northing + nN * Far),
            P(wall.Easting - nE * Far, wall.Northing - nN * Far),
            P(wall.Easting - nE * Far - wall.DirE * Far, wall.Northing - nN * Far - wall.DirN * Far),
            P(wall.Easting + nE * Far - wall.DirE * Far, wall.Northing + nN * Far - wall.DirN * Far),
        };
        var pieces = Clipper.Intersect(new Paths64 { ring }, new Paths64 { before }, FillRule.NonZero);
        // The piece the pass is in, half a metre before the wall.
        var at = Probe(wall);
        var probe = P(at.Easting, at.Northing);
        foreach (var piece in pieces)
        {
            if (piece.Count < 3 || Clipper.PointInPolygon(probe, piece) == PointInPolygonResult.IsOutside) continue;
            var cut = new List<Vec2>(piece.Count);
            foreach (var p in piece) cut.Add(new Vec2(p.X / Scale, p.Y / Scale));
            return cut;
        }
        return null;
    }

    /// <summary>
    /// The headings of a cut ring's points on the wall: along the wall, the way the ring goes round.
    /// A ring's headings are taken from the point before to the point after, which at the two corners
    /// of the wall is neither the wall's way nor the fence's.
    /// </summary>
    public static void HeadingsAlongWall(List<Vec3> ring, Wall wall)
    {
        bool OnWall(Vec3 p) =>
            Math.Abs((p.Easting - wall.Easting) * wall.DirE + (p.Northing - wall.Northing) * wall.DirN) < 0.01;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            if (!OnWall(a) || !OnWall(b)) continue;
            double de = b.Easting - a.Easting, dn = b.Northing - a.Northing;
            if (de * de + dn * dn < 1e-6) continue;
            ring[i] = new Vec3(a.Easting, a.Northing, Math.Atan2(de, dn));
        }
    }

    private static Point64 P(double e, double n) => new((long)Math.Round(e * Scale), (long)Math.Round(n * Scale));
}
