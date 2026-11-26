// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.Tool;
using AgOpenWeb.Models.YouTurn;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.Services.YouTurn;

/// <summary>
/// A U-turn whose exit is swung wide for a trailed implement (fork only; the U-turn setting
/// <see cref="GuidanceConfig.UTurnForTrailedTool"/>, off unless the operator turns it on).
///
/// A turn is planned and driven for the tractor's pivot, and it ends where the tractor is on the
/// next pass. A trailed implement cuts inside the turn and is then still at an angle to the pass:
/// it comes onto it only over three times its length or so (a drill with its wheels 4 m behind
/// the pin: 15 to 20 m past the top of the turn), so the start of every pass is worked crooked.
///
/// With the choice on, the turn is found exactly as before (the same seat, the same checks), and
/// then its exit is replaced by the one a driver makes with a trailer
/// (<see cref="TrailedExitSwing"/>): the tractor goes past the next pass and comes back onto it,
/// never tighter than the U-turn radius, sized so that the implement is square on the pass where
/// the tractor is back on it (6 m past the top of the turn with that drill). The new exit is held
/// to the same rules as the turn: the tractor inside the turn line and out of every obstacle's
/// turn area, the implement the distance from a hard boundary and a hard obstacle, the exit clear
/// of obstacles, and on the pass as it is steered (a straight stretch of it). Where it breaks one,
/// the turn stays as planned for the tractor: never no turn where there was one, and never a turn
/// seated elsewhere because of the swing.
///
/// A K-turn (it reverses), the simple fallback, a manual U-turn and the way to the start of a pass
/// are planned for the pivot.
/// </summary>
public partial class YouTurnCreationService
{
    /// <summary>The most a point of the swung exit's leg may be beside the next pass as steered, m.</summary>
    private const double SwingOffPass = 0.3;

    /// <summary>
    /// Whether a turn's exit is swung wide with these settings: the operator's choice, a trailed
    /// or TBT implement and a turn that does not reverse.
    /// </summary>
    internal static bool SwingsExitForTrailedTool(ConfigurationStore config, bool kTurn = false)
    {
        if (!config.Guidance.UTurnForTrailedTool || kTurn) return false;
        double radius = config.Guidance.UTurnRadius;
        return RigGeometry.Mount(config.Tool) is ToolMount.Trailing or ToolMount.TBT
            && double.IsFinite(radius) && radius > 0;
    }

    /// <summary>
    /// 0 with no swung exit; else a value that changes when a turn planned with one would come
    /// out otherwise (the implement's lengths, its hitch, the choice itself): a turn planned and
    /// not yet driven is planned again then.
    /// </summary>
    internal static int TrailedSwingStamp(ConfigurationStore config) =>
        SwingsExitForTrailedTool(config, MapTurnStyle(config.Guidance.UTurnStyle) == YouTurnType.KStyle)
            ? HashCode.Combine(RigGeometry.Stamp(config.Vehicle, config.Tool), 1) | 1
            : 0;

    /// <summary>
    /// <paramref name="path"/>, a turn that passed every check as planned for the pivot, with its
    /// exit swung wide for the trailed implement; null when the swing gains nothing or breaks a
    /// rule the turn is held to (the turn then stays as planned).
    /// </summary>
    /// <param name="input">The plan's input: its turn area and the room round Go around obstacles.</param>
    /// <param name="hardOuter">The hard outer boundary, or null with a soft one.</param>
    /// <param name="turnedAt">The obstacle the turn is made at, or null at the field's edge.</param>
    /// <param name="arcFrom">The path's first point that is not the current pass.</param>
    private TrailedExitSwing.Result? ExitSwingFor(
        List<Vec3> path, YouTurnCreationInput input, Boundary? boundary, ConfigurationStore config,
        List<TurnObstacle> obstacles, TurnObstacle? turnedAt, List<Vec2>? hardOuter, double clearanceMargin,
        int arcFrom, Models.Track.Track? nextTrack)
    {
        var tool = BuildToolGeometry(config);
        var swing = TrailedExitSwing.Apply(path, tool, config.Guidance.UTurnRadius,
            p => input.IsPointInsideTurnArea(p) == 0 && input.IsPointBlockedForTurn?.Invoke(p) != true);
        if (swing == null)
        {
            _logger.LogDebug("[YouTurn] No swung exit for the trailed implement here (square already, or no room inside the turn line); the turn as planned for the tractor");
            return null;
        }

        string? broken = null;
        // The leg after the counter-arc lies on the next pass as it is steered: a pass that bends
        // there (a curve, a bend round a Go around obstacle) is joined as planned.
        var pass = _bentDestination;
        if (pass != null && pass.Count >= 2)
        {
            for (int i = swing.OnPassIndex; i < swing.Path.Count && broken == null; i++)
                if (OffLine(swing.Path[i], pass) > SwingOffPass) broken = "the next pass bends where the exit joins it";
        }
        if (broken == null && TurnLanding.PastLineEnd(swing.Path, nextTrack))
            broken = "it would hand over past the end of the next pass's line";

        // The implement against the hard outer boundary and a hard obstacle the turn is made at.
        if (broken == null && (hardOuter != null || turnedAt?.IsHard == true))
        {
            var swept = ImplementSweptPath.Compute(swing.Path, tool);
            if (hardOuter != null
                && !TurnClearance.Evaluate(swept, hardOuter, TurnClearance.KeepSide.Inside, clearanceMargin).IsClear)
                broken = "the implement would come too near the hard boundary";
            else if (turnedAt?.IsHard == true
                && !TurnClearance.Evaluate(swept, turnedAt.Fence, TurnClearance.KeepSide.Outside, clearanceMargin).IsClear)
                broken = "the implement would come too near the hard obstacle";
        }

        // The path and its exit against every obstacle, as the planned turn was. The swung path
        // may run on further along the next pass: the same place on it is that much less past its end.
        if (broken == null && boundary != null
            && !ClearsTurnObstacles(swing.Path, boundary, config, out int crossed, SwingExitReach(swing),
                turnedAt?.InnerIndex ?? -1, obstacles, arcFrom: arcFrom))
            broken = $"it would cross obstacle {crossed + 1}, or end right before it";

        if (broken != null)
        {
            _logger.LogDebug("[YouTurn] No swung exit for the trailed implement here ({Why}); the turn as planned for the tractor", broken);
            return null;
        }
        _logger.LogDebug("[YouTurn] Exit swung wide by {A:F0}° for the trailed implement: the tractor goes {P:F1} m past the next pass; the implement, {B:F0}° off the pass where the planned turn ended, is square where the tractor is back on it",
            swing.Swing * 180 / Math.PI, swing.PastPass, Math.Abs(swing.ToolAngleBefore) * 180 / Math.PI);
        return swing;
    }

    /// <summary>How far past the end of a swung path its exit is looked at (<see cref="ExitReach"/>
    /// past the planned one's end; never less than the stretch a rigid implement settles over).</summary>
    private static double SwingExitReach(TrailedExitSwing.Result swing) =>
        Math.Max(8.0, ExitReach - Math.Max(0.0, swing.RunsOnBy));

    /// <summary>The distance from <paramref name="p"/> to <paramref name="line"/>, its first and last
    /// stretches carried on past its ends (a pass is steered on past them).</summary>
    private static double OffLine(Vec3 p, IReadOnlyList<Vec3> line)
    {
        double best = double.MaxValue;
        for (int i = 1; i < line.Count; i++)
        {
            var a = line[i - 1];
            var b = line[i];
            double sE = b.Easting - a.Easting, sN = b.Northing - a.Northing, l2 = sE * sE + sN * sN;
            if (l2 < 1e-12) continue;
            double t = ((p.Easting - a.Easting) * sE + (p.Northing - a.Northing) * sN) / l2;
            if (i > 1) t = Math.Max(t, 0);
            if (i < line.Count - 1) t = Math.Min(t, 1);
            double dE = a.Easting + sE * t - p.Easting, dN = a.Northing + sN * t - p.Northing;
            best = Math.Min(best, Math.Sqrt(dE * dE + dN * dN));
        }
        return best;
    }
}
