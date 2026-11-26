// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Tool;
using AgOpenWeb.Services.Track;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.Services.YouTurn;

/// <summary>
/// A U-turn planned for a front implement's centre (fork only; the U-turn setting
/// <see cref="GuidanceConfig.UTurnForFrontTool"/>, off unless the operator turns it on).
///
/// A turn is planned and driven for the tractor's pivot. An implement fixed <c>h</c> ahead of it
/// (a front boom, a combine's header) swings outside every bend of the pivot's path: on a turn of
/// radius R it runs on a circle of √(R² + h²), and where the pivot's arc starts and ends its
/// centre steps sideways at once, by the angle whose tangent is h / R (4.7 m ahead on a 5.4 m
/// turn: a 7.2 m circle, 41°). The wheels can't step, so there the implement is off the path
/// drawn for it by the steering's lag times h.
///
/// Planned for the implement instead, the turn's shape (the legs along the two passes, the arc,
/// a wide turn's run along the turn line) is the path of the implement's centre, at the radius
/// its circle has when the tractor turns at the U-turn radius; it is held to the turn line, and
/// it leaves one pass and joins the next along them, with no step. The pivot's path is then the
/// one that takes the implement along it (<see cref="RigGeometry.PivotPathBehind"/>): h behind,
/// as a trailer follows its hitch, never tighter than the U-turn radius, coming into the bend
/// and out of it gradually. That path is the one steered, checked (the implement's sweep against
/// a hard fence, the obstacles, the turn's start ahead of the tractor) and kept as the turn's
/// path, so the guards and the map (<see cref="RigGeometry.PathOfPointAhead"/>) read it as
/// before. After the implement's arc the tractor is still at an angle to the pass (41° above,
/// 16° a reach on, 6° two reaches on), so the exit leg is at least <see cref="FrontToolPlan.MinExitLeg"/>.
///
/// An implement shifted to one side is planned by the point ahead on the tractor's centre line:
/// the pass lines the turn joins are already the tractor's (the tool offset beside them).
/// A K-turn (it reverses) is planned for the pivot.
/// </summary>
public partial class YouTurnCreationService
{
    /// <summary>What a turn is planned for: the pivot (<see cref="On"/> false) or the point
    /// <see cref="Reach"/> ahead of it, with the tractor turning no tighter than <see cref="PivotRadius"/>.</summary>
    internal readonly record struct FrontToolPlan(double Reach, double PivotRadius)
    {
        public bool On => Reach > 0;

        /// <summary>The radius of the implement's circle when the pivot's is <see cref="PivotRadius"/>.</summary>
        public double Radius => Math.Sqrt(PivotRadius * PivotRadius + Reach * Reach);

        /// <summary>
        /// The least straight along the next pass after the implement's arc: the tractor comes onto
        /// the pass as e^(−s/reach), the turn hands over to the line 4 m before its end, and the
        /// generator drops the leg's last two points (up to 4 m on an AB line). Three reaches before
        /// the hand-over leave a twentieth of what the pivot was off the pass (0.15 m of 3 m, 2°).
        /// </summary>
        public double MinExitLeg => 3.5 * Reach + 6.0;

        /// <summary>
        /// The same for a path laid out point by point (a manual turn, the way to the start of a
        /// pass): three reaches along the pass before the line takes over, which it does 4 m
        /// before the path's end.
        /// </summary>
        public double SettleLeg => 3.0 * Reach + 4.0;
    }

    /// <summary>A front implement nearer the rear axle than this is planned for the pivot, m.</summary>
    internal const double MinFrontToolReach = 0.5;

    /// <summary>A pivot path bending tighter than this share of the U-turn radius is not used.</summary>
    private const double FrontToolTightestShare = 0.85;

    /// <summary>The most the pivot may still be beside the next pass where the turn's path ends, m.</summary>
    private const double FrontToolEndOffPass = 0.5;

    /// <summary>
    /// How far past the end of a turn's path its exit along the next pass is looked at
    /// (<see cref="ClearsTurnObstacles"/>): <see cref="ExitReach"/> past a turn planned for the
    /// pivot. One planned for the implement goes on along the next pass
    /// <paramref name="pathRunsOnBy"/> m further than that one (the tractor still has to come onto
    /// the pass), so the same place on the pass is that much less past its end; never less than
    /// the stretch a rigid implement settles over.
    /// </summary>
    private static double ExitReachFor(FrontToolPlan plan, double pathRunsOnBy) =>
        plan.On ? Math.Max(8.0, ExitReach - Math.Max(0.0, pathRunsOnBy)) : ExitReach;

    /// <summary>
    /// What turns are planned for with these settings: the front implement's centre when the
    /// operator chose that and the tool is a front one, else the pivot.
    /// </summary>
    internal static FrontToolPlan FrontToolPlanFor(ConfigurationStore config, bool kTurn = false)
    {
        if (!config.Guidance.UTurnForFrontTool || kTurn) return default;
        double reach = RigGeometry.FrontToolReach(config.Vehicle, config.Tool);
        double radius = config.Guidance.UTurnRadius;
        return double.IsFinite(reach) && reach >= MinFrontToolReach && double.IsFinite(radius) && radius > 0
            ? new FrontToolPlan(reach, radius)
            : default;
    }

    /// <summary>
    /// The radius the turn generator works with: the U-turn radius, or the radius of the front
    /// implement's circle when turns are planned for it. Rows wider than twice this get the wide turn.
    /// </summary>
    public static double PlannedTurnRadius(ConfigurationStore config)
    {
        var plan = FrontToolPlanFor(config, MapTurnStyle(config.Guidance.UTurnStyle) == Models.YouTurn.YouTurnType.KStyle);
        return plan.On ? plan.Radius : config.Guidance.UTurnRadius;
    }

    /// <summary>
    /// Adds the way on from the last point of <paramref name="path"/> (on <paramref name="pass"/>,
    /// heading the way it is driven) for <paramref name="length"/> m, a point every half metre:
    /// along the pass's own points when it is a curve, straight on past its end and along an AB pass.
    /// </summary>
    private static void RunOnAlongPass(List<Vec3> path, Models.Track.Track? pass, double length)
    {
        const double step = 0.5;
        var from = path[^1];
        var ahead = new List<Vec3> { from };
        var pts = pass?.Points;
        if (pts != null && pts.Count > 2)
        {
            // The stretch of the curve beside the point, and which way along it the point heads.
            int seg = -1;
            double best = double.MaxValue;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                double dE = pts[i + 1].Easting - pts[i].Easting, dN = pts[i + 1].Northing - pts[i].Northing;
                double len2 = dE * dE + dN * dN;
                if (len2 < 1e-9) continue;
                double t = Math.Clamp(((from.Easting - pts[i].Easting) * dE + (from.Northing - pts[i].Northing) * dN) / len2, 0, 1);
                double oE = pts[i].Easting + dE * t - from.Easting, oN = pts[i].Northing + dN * t - from.Northing;
                double d = oE * oE + oN * oN;
                if (d < best) { best = d; seg = i; }
            }
            if (seg >= 0)
            {
                double along = Math.Atan2(pts[seg + 1].Easting - pts[seg].Easting, pts[seg + 1].Northing - pts[seg].Northing);
                if (Math.Cos(along - from.Heading) >= 0)
                    for (int i = seg + 1; i < pts.Count; i++) ahead.Add(pts[i]);
                else
                    for (int i = seg; i >= 0; i--) ahead.Add(pts[i]);
            }
        }
        // Straight on past the last of them.
        double onward = from.Heading;
        for (int i = ahead.Count - 1; i > 0; i--)
        {
            if (GeometryMath.DistanceSquared(ahead[i], ahead[i - 1]) < 1e-9) continue;
            onward = Math.Atan2(ahead[i].Easting - ahead[i - 1].Easting, ahead[i].Northing - ahead[i - 1].Northing);
            break;
        }
        ahead.Add(RigGeometry.AlongTractor(ahead[^1], onward, length));

        double want = step, walked = 0;
        for (int i = 1; i < ahead.Count && want <= length + 1e-9; i++)
        {
            double dE = ahead[i].Easting - ahead[i - 1].Easting, dN = ahead[i].Northing - ahead[i - 1].Northing;
            double len = Math.Sqrt(dE * dE + dN * dN);
            if (len < 1e-9) continue;
            double heading = NormalizeHeading(Math.Atan2(dE, dN));
            for (; want <= walked + len + 1e-9 && want <= length + 1e-9; want += step)
            {
                double f = (want - walked) / len;
                path.Add(new Vec3(ahead[i - 1].Easting + dE * f, ahead[i - 1].Northing + dN * f, heading));
            }
            walked += len;
        }
    }

    /// <summary>
    /// Where the pivot is on <paramref name="pivotPath"/> when the implement is at
    /// <paramref name="at"/> on <paramref name="toolPath"/> (the two paths a point for a point).
    /// </summary>
    private static Vec3 PivotWhenToolIsAt(IReadOnlyList<Vec3> toolPath, IReadOnlyList<Vec3> pivotPath, Vec3 at)
    {
        int n = toolPath.Count;
        int k = PassEnds.NearestIndex(toolPath, at.Easting, at.Northing);
        // On the stretch before or after the nearest point, whichever it lies along.
        double bestT = 0;
        int bestA = k, bestB = k;
        double best = GeometryMath.DistanceSquared(toolPath[k], at);
        foreach (int j in new[] { k - 1, k + 1 })
        {
            if (j < 0 || j >= n) continue;
            double dE = toolPath[j].Easting - toolPath[k].Easting, dN = toolPath[j].Northing - toolPath[k].Northing;
            double len2 = dE * dE + dN * dN;
            if (len2 < 1e-12) continue;
            double t = Math.Clamp(((at.Easting - toolPath[k].Easting) * dE + (at.Northing - toolPath[k].Northing) * dN) / len2, 0, 1);
            double oE = toolPath[k].Easting + dE * t - at.Easting, oN = toolPath[k].Northing + dN * t - at.Northing;
            double d = oE * oE + oN * oN;
            if (d < best) { best = d; bestT = t; bestA = k; bestB = j; }
        }
        var a = pivotPath[bestA];
        var b = pivotPath[bestB];
        return new Vec3(a.Easting + (b.Easting - a.Easting) * bestT, a.Northing + (b.Northing - a.Northing) * bestT, a.Heading);
    }

    /// <summary>
    /// The pivot's path for a turn planned for the implement's centre, a point for each point of
    /// <paramref name="toolPath"/>; null when the tractor can't drive it (a bend tighter than it
    /// turns, as the path's own smoothing or a kink in it can leave) or is not yet on the next
    /// pass where the path ends (a short exit leg): the turn is then planned for the pivot.
    /// </summary>
    private List<Vec3>? PivotPathFor(List<Vec3> toolPath, FrontToolPlan plan)
    {
        var pivot = RigGeometry.PivotPathBehind(toolPath, plan.Reach);
        int n = pivot.Count;
        if (n < 3) return null;

        double tightest = FrontToolLine.TightestBend(pivot);
        if (tightest < plan.PivotRadius * FrontToolTightestShare)
        {
            _logger.LogDebug("[YouTurn] Planned for the front implement, the tractor would turn at {R:F1} m (U-turn radius {U:F1} m); planning for the pivot",
                tightest, plan.PivotRadius);
            return null;
        }

        // Beside the path's last stretch: the reach times the sine of the tractor's angle to it.
        double dE = toolPath[n - 1].Easting - toolPath[n - 2].Easting, dN = toolPath[n - 1].Northing - toolPath[n - 2].Northing;
        double len = Math.Sqrt(dE * dE + dN * dN);
        if (len > 1e-9)
        {
            double off = Math.Abs((pivot[n - 1].Easting - toolPath[n - 1].Easting) * dN - (pivot[n - 1].Northing - toolPath[n - 1].Northing) * dE) / len;
            if (off > FrontToolEndOffPass)
            {
                _logger.LogDebug("[YouTurn] Planned for the front implement, the tractor would end {D:F2} m beside the next pass; planning for the pivot", off);
                return null;
            }
        }
        return pivot;
    }
}
