// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.YouTurn;

namespace AgOpenWeb.Services.YouTurn;

/// <summary>
/// A U-turn seated on a Go around bend of the current pass (fork only). Near the end of a pass a
/// tree the pass goes round can leave no straight stretch for the turn, above all when the turn is
/// seated back to keep a front boom off a hard fence: on a tester's field a tree 33 m before a
/// slanted fence, an 8 m boom 4.5 m ahead, and the arc was seated on the bend's way back to the
/// pass. Built at the bend's heading, the sagitta arc turned 180° from there and ended up to 45° off
/// the next pass, joined it with a kink, and the turn was refused as malformed: every job on that
/// field stopped at its first turn. On a bend the arc is the sagitta from that heading onto the next
/// pass at its own (<see cref="SagittaTurnGeometry.BuildToLine"/>), seated as far on as it fits.
/// </summary>
public partial class YouTurnCreationService
{
    /// <summary>Where an arc starts on the current pass: the point and the heading it is driven at.</summary>
    private readonly record struct BendSeat(Vec3 Point, double Heading);

    /// <summary>A seat this far off the straight pass, or this far off its heading, is on a bend.</summary>
    private const double BendOffPass = 0.05;
    private const double BendOffHeading = Math.PI / 180;

    /// <summary>The step a turn seated on a bend is moved on by, to touch the turn line, m.</summary>
    private const double BendSeatStep = 0.1;

    /// <summary>The pass's heading at a seat is taken over this either side of it, m.</summary>
    private const double BendSeatChord = 1.0;

    /// <summary>The next pass, for the arcs seated on a bend of this plan (<see cref="ArcFromBend"/>).</summary>
    private List<Vec3>? _bendNextPass;

    /// <summary>
    /// Whether the current pass is bent round a Go around obstacle at point <paramref name="index"/>:
    /// off the pass as it is without its bends (<see cref="YouTurnCreationInput.StraightGuidancePoints"/>),
    /// or at an angle to it. <paramref name="back"/> is the step that walks the pass back from the
    /// end (against the way it is driven).
    /// </summary>
    private static bool OnGoAroundBend(YouTurnCreationInput input, int index, int back, out BendSeat seat)
    {
        seat = default;
        var straight = input.StraightGuidancePoints;
        if (straight == null || straight.Count < 2) return false;
        seat = SeatAt(input.GuidancePoints, index, -back, 0);
        double best = double.MaxValue, heading = 0;
        for (int i = 1; i < straight.Count; i++)
        {
            var a = straight[i - 1];
            var b = straight[i];
            double d = GeometryMath.PointToSegmentDistance(seat.Point.Easting, seat.Point.Northing, a.Easting, a.Northing, b.Easting, b.Northing);
            if (d >= best) continue;
            best = d;
            heading = Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing);
        }
        return best > BendOffPass || Math.Abs(LineAngle(seat.Heading - heading)) > BendOffHeading;
    }

    /// <summary>
    /// The arc from <paramref name="seat"/> onto the next pass, at its heading where it lands;
    /// empty when none lands there.
    /// </summary>
    private List<Vec3> ArcFromBend(YouTurnCreationInput input, BendSeat seat, bool isTurningRight)
    {
        _bendNextPass ??= BuildNewOffsetCurveList(input, NextPassDistAway(input));
        var next = _bendNextPass;
        if (next.Count < 2) return new List<Vec3>();
        // Aimed at the next pass abreast of the seat, then again where that turn lands (a next pass
        // that curves).
        var arc = new List<Vec3>();
        var aim = seat.Point;
        for (int k = 0; k < 2; k++)
        {
            var (point, heading) = LineAt(next, NearestIndex(next, aim), seat.Heading + Math.PI);
            arc = SagittaTurnGeometry.BuildToLine(seat.Point, seat.Heading, isTurningRight, input.TurnRadius, point, heading);
            if (arc.Count == 0) return arc;
            aim = arc[^1];
        }
        return arc;
    }

    /// <summary>
    /// The turn seated on a bend at point <paramref name="index"/> (<paramref name="arc"/>, inside the
    /// turn line), moved on along the pass in <see cref="BendSeatStep"/> steps, each built again from
    /// its own seat, for as long as it stays clear: the turn as far on as it fits. Moved along its
    /// start heading as an arc on a straight pass is (<see cref="MoveTurnInsideTurnLine"/>), it would
    /// leave the bend and land beside the next pass. At most <paramref name="maxSlide"/> on.
    /// </summary>
    private List<Vec3> SeatForwardOnBend(YouTurnCreationInput input, int index, int back, bool isTurningRight,
        List<Vec3> arc, double maxSlide)
    {
        var best = arc;
        for (double s = BendSeatStep; s <= maxSlide; s += BendSeatStep)
        {
            var moved = ArcFromBend(input, SeatAt(input.GuidancePoints, index, -back, s), isTurningRight);
            if (moved.Count == 0 || moved.Exists(p => Blocked(input, p))) break;
            best = moved;
        }
        return best;
    }

    /// <summary>The point <paramref name="along"/> metres on from point <paramref name="index"/> of the
    /// pass (in the direction of <paramref name="step"/>), and the pass's heading there that way.</summary>
    private static BendSeat SeatAt(List<Vec3> pass, int index, int step, double along)
    {
        var at = PointAlong(pass, index, step, along);
        var behind = PointAlong(pass, index, step, along - BendSeatChord);
        var ahead = PointAlong(pass, index, step, along + BendSeatChord);
        double heading = Math.Atan2(ahead.Easting - behind.Easting, ahead.Northing - behind.Northing);
        if (heading < 0) heading += TWO_PI;
        return new BendSeat(new Vec3(at.Easting, at.Northing, heading), heading);
    }

    /// <summary>The point <paramref name="along"/> metres from point <paramref name="index"/> of the pass
    /// in the direction of <paramref name="step"/> (back the other way when negative), held at its ends.</summary>
    private static Vec2 PointAlong(List<Vec3> pass, int index, int step, double along)
    {
        if (along < 0) { step = -step; along = -along; }
        int i = index;
        while (true)
        {
            int j = i + step;
            if (j < 0 || j >= pass.Count) return new Vec2(pass[i].Easting, pass[i].Northing);
            double de = pass[j].Easting - pass[i].Easting, dn = pass[j].Northing - pass[i].Northing;
            double seg = Math.Sqrt(de * de + dn * dn);
            if (seg >= along && seg > 1e-9)
            {
                double f = along / seg;
                return new Vec2(pass[i].Easting + de * f, pass[i].Northing + dn * f);
            }
            along -= seg;
            i = j;
        }
    }

    /// <summary>The line through point <paramref name="k"/> of a pass, at its heading there, turned to
    /// run within 90° of <paramref name="towards"/>.</summary>
    private static (Vec2 Point, double Heading) LineAt(List<Vec3> pass, int k, double towards)
    {
        int a = Math.Max(0, k - 1), b = Math.Min(pass.Count - 1, k + 1);
        if (a == b) return (new Vec2(pass[k].Easting, pass[k].Northing), towards);
        double heading = Math.Atan2(pass[b].Easting - pass[a].Easting, pass[b].Northing - pass[a].Northing);
        if (Math.Cos(heading - towards) < 0) heading += Math.PI;
        return (new Vec2(pass[k].Easting, pass[k].Northing), heading);
    }

    /// <summary>The angle between two lines (headings without their sense), in (−π/2, π/2].</summary>
    private static double LineAngle(double angle)
    {
        angle %= Math.PI;
        if (angle > Math.PI / 2) angle -= Math.PI;
        else if (angle <= -Math.PI / 2) angle += Math.PI;
        return angle;
    }

    /// <summary>
    /// How far, in radians, the line a turn path starts on (<paramref name="startHeading"/> at
    /// <paramref name="start"/>) turns from the pass without its Go around bends: 0 on a straight
    /// pass. A turn starting on a bend turns that much less, or more, than 180°.
    /// </summary>
    internal static double DeviationFromStraight(IReadOnlyList<Vec3>? straight, Vec3 start, double startHeading)
    {
        if (straight == null || straight.Count < 2) return 0;
        double best = double.MaxValue, heading = 0;
        for (int i = 1; i < straight.Count; i++)
        {
            var a = straight[i - 1];
            var b = straight[i];
            double d = GeometryMath.PointToSegmentDistance(start.Easting, start.Northing, a.Easting, a.Northing, b.Easting, b.Northing);
            if (d >= best) continue;
            best = d;
            heading = Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing);
        }
        return LineAngle(startHeading - heading);
    }
}
