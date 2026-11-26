// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Tool;
using AgOpenWeb.Models.YouTurn;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.Services.YouTurn;

/// <summary>
/// A U-turn sized for a trailed implement (fork only; the U-turn setting
/// <see cref="GuidanceConfig.UTurnTrailedOpen"/> with <see cref="GuidanceConfig.UTurnForTrailedTool"/>,
/// off unless the operator turns it on).
///
/// The swung exit (<c>YouTurnCreationService.TrailedTool.cs</c>) leaves the turn itself as it is
/// planned for the tractor: the tractor at the U-turn radius, the implement dragged round a much
/// tighter circle behind it, its drawbar beside the rear tyre. Here the implement decides the
/// turn and the tractor drives what that takes: the U-turn radius is read as the radius of the
/// implement's working centre, the tractor's arc is opened to the radius that gives it
/// (<see cref="TrailedOpenTurn.OpenRadius"/>), and the swing that leaves the implement square on
/// the next pass is part of the shape (<see cref="TrailedOpenTurn"/>).
///
/// By how far apart the passes are:
/// <list type="bullet">
/// <item>further than twice the open radius: the wide turn, made at the open radius, and its exit
/// swung with the counter-arc at the U-turn radius;</item>
/// <item>up to that, where the open shape exists (<see cref="TrailedOpenTurn.Solve"/>): that one
/// shape, seated and held to the turn line by the generator as the style's own arc is, and to
/// every check a turn is held to;</item>
/// <item>nearer (no open shape lands the implement square): the turn as planned for the tractor
/// with its exit swung, as without this choice.</item>
/// </list>
/// Never no turn where there was one: where the open turn is not made (it fits nowhere, a hard
/// fence or an obstacle stops it, the pass bends round an obstacle where it would start), the
/// turn is planned again without it, and the log says so.
/// </summary>
public partial class YouTurnCreationService
{
    /// <summary>
    /// What a turn is opened to for a trailed implement: nothing (<see cref="On"/> false), the
    /// one shape <see cref="Shape"/> with its main arc at <see cref="Radius"/>, or (no shape) the
    /// wide turn made at <see cref="Radius"/>.
    /// </summary>
    internal readonly record struct TrailedOpenPlan(double Radius, TrailedOpenTurn.Shape? Shape)
    {
        public bool On => Radius > 0;

        /// <summary>The passes are further apart than one shape reaches: the wide turn at the open radius.</summary>
        public bool Wide => On && Shape == null;
    }

    /// <summary>
    /// The radius the tractor turns at with these settings when its turns are sized for a trailed
    /// implement, or 0 when they are not: the choice is off, the implement is not a trailed one,
    /// a K-turn, or the implement follows the tractor closely enough
    /// (<see cref="TrailedOpenTurn.MinOpen"/>).
    /// </summary>
    internal static double TrailedOpenRadius(ConfigurationStore config, bool kTurn = false)
    {
        if (!config.Guidance.UTurnTrailedOpen || !SwingsExitForTrailedTool(config, kTurn)) return 0;
        double radius = config.Guidance.UTurnRadius;
        double open = TrailedOpenTurn.OpenRadius(BuildToolGeometry(config), radius);
        return open >= radius + TrailedOpenTurn.MinOpen ? open : 0;
    }

    /// <summary>
    /// The open turn onto a pass <paramref name="turnOffset"/> m to the side with these settings,
    /// or none (<see cref="TrailedOpenPlan.On"/> false).
    /// </summary>
    internal static TrailedOpenPlan TrailedOpenPlanFor(ConfigurationStore config, double turnOffset, bool kTurn = false)
    {
        double open = TrailedOpenRadius(config, kTurn);
        double offset = Math.Abs(turnOffset);
        if (!(open > 0) || !(offset > 0) || !double.IsFinite(offset)) return default;
        if (offset > 2 * open) return new TrailedOpenPlan(open, null);
        var shape = TrailedOpenTurn.Solve(BuildToolGeometry(config), config.Guidance.UTurnRadius, offset);
        return shape == null ? default : new TrailedOpenPlan(shape.Radius, shape);
    }

    /// <summary>
    /// Sets the generator's <paramref name="input"/> for <paramref name="open"/>: the radius it
    /// works with, and for the one shape the shape itself and its exit leg. That shape ends
    /// further along the next pass than a turn planned for the tractor
    /// (<see cref="TrailedOpenTurn.Shape.RunsOn"/>), so its leg is shorter by that much, never
    /// under the straight the line needs to take over on (<see cref="TrailedExitSwing.LegAfter"/>):
    /// the path ends where the planned turn's does.
    /// </summary>
    private static void ApplyOpenPlan(YouTurnCreationInput input, TrailedOpenPlan open)
    {
        input.TurnRadius = open.Radius;
        if (open.Shape is not { } shape) return;
        input.OpenShape = shape;
        if (input.LegLength > 0)
            input.ExitLegLength = Math.Max(TrailedExitSwing.LegAfter, input.LegLength - shape.RunsOn);
    }

    /// <summary>The side offset of the turn being planned, as the generator takes it
    /// (<see cref="CreateTurn"/>): the pre-calculated one, else the passes skipped times their spacing.</summary>
    private static double PlannedTurnOffset(ConfigurationStore config, double nextTrackTurnOffset, int skipRows) =>
        nextTrackTurnOffset != 0
            ? Math.Abs(nextTrackTurnOffset)
            : (config.ActualToolWidth - config.Tool.Overlap) * (skipRows + 1);

    private void LogOpenTurn(TurnPathResult result, TrailedOpenPlan open, double elapsedMs)
    {
        if (result.OpenShape is { } shape)
            _logger.LogDebug("[YouTurn] Turn sized for the trailed implement ({Ms:F0} ms): the tractor's arc at {R:F1} m (U-turn radius {U:F1} m){Top}, {P:F1} m past the next pass and back onto it by {S:F0}°",
                elapsedMs, shape.Radius, shape.CounterRadius,
                shape.TopStraight > 0.05 ? $", {shape.TopStraight:F1} m straight along the top" : "", shape.PastPass, shape.Swing * 180 / Math.PI);
        else
            _logger.LogDebug("[YouTurn] Wide turn sized for the trailed implement ({Ms:F0} ms): the tractor's arcs at {R:F1} m, its exit swung by {S:F0}°",
                elapsedMs, open.Radius, (result.ExitSwing?.Swing ?? 0) * 180 / Math.PI);
    }
}
