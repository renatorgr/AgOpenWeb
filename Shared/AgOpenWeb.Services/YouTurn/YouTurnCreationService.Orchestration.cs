// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
using AgOpenWeb.Models.Tool;
using AgOpenWeb.Models.YouTurn;

using Microsoft.Extensions.Logging;

namespace AgOpenWeb.Services.YouTurn;

/// <summary>
/// High-level orchestration around <see cref="CreateTurn(YouTurnCreationInput)"/>:
/// builds the input, validates the generated path, and drops to a simple geometric
/// fallback when the primary algorithm produces a spiral or fails outright. This
/// used to live inline in MainViewModel.YouTurn.cs.
/// </summary>
public partial class YouTurnCreationService
{
    /// <summary>
    /// Result of <see cref="CreateTurnPath"/>. <see cref="Path"/> is null when preconditions
    /// aren't met (no headland line, no boundary, invalid input). <see cref="UsedFallback"/>
    /// is true when the primary Dubins-based algorithm returned a spiral or failed outright
    /// and the simple geometric fallback was substituted.
    /// </summary>
    public readonly record struct TurnPathResult(List<Vec3>? Path, bool UsedFallback)
    {
        /// <summary>
        /// True when no forward turn could keep the implement clear of a hard
        /// boundary, so <see cref="Path"/> is null and the turn must not be
        /// auto-engaged — the operator should be warned to take over.
        /// </summary>
        public bool ClearanceBlocked { get; init; }

        /// <summary>
        /// Near a hard boundary, no turn of the right shape was built (the generator found none, or
        /// the one it built was malformed): <see cref="ClearanceBlocked"/> too, but the operator is
        /// told no turn fits here (with <see cref="OutsideTurnLineBy"/> when known), not that the
        /// implement would swing into the boundary, which sent them looking at the fence. Fork only.
        /// </summary>
        public bool NoTurnBuilt { get; init; }

        /// <summary>
        /// With no <see cref="Path"/> after the generator failed: why (<see cref="YouTurnCreationOutput.Failure"/>).
        /// The tractor at the turn line or past it (<see cref="TurnFailure.TooLate"/>) is said as such,
        /// not as a turn that does not fit: <see cref="NoTurnBuilt"/> is set only for
        /// <see cref="TurnFailure.DoesNotFit"/>. Fork only.
        /// </summary>
        public TurnFailure Failure { get; init; }

        /// <summary>
        /// True when the turn built would hand over past the end of the next pass's line
        /// (<see cref="TurnLanding.PastLineEnd"/>), so it was not taken: <see cref="Path"/> is null.
        /// </summary>
        public bool LandsPastLineEnd { get; init; }

        /// <summary>Where the path leaves the current pass (see <see cref="YouTurnCreationOutput.ArcStart"/>);
        /// null for a fallback path, which has no entry leg.</summary>
        public Vec3? ArcStart { get; init; }

        /// <summary>
        /// With a turn planned for a front implement's centre (<see cref="FrontToolPlan"/>): where the
        /// implement leaves the current pass, a reach ahead of <see cref="ArcStart"/>, where the pivot
        /// does. What is left of the pass before the turn is measured from here. Null otherwise.
        /// </summary>
        public Vec3? ToolLeavesPassAt { get; init; }

        /// <summary>
        /// The pass runs into an obstacle whose choice is a U-turn before it reaches the outer
        /// turn line: the turn is made there. With no <see cref="Path"/>, no turn fits before the
        /// obstacle and none is planned elsewhere; the operator has to take over.
        /// </summary>
        public bool AtObstacle { get; init; }

        /// <summary>With <see cref="AtObstacle"/> and a path, the obstacle's index in the inner boundaries; else -1.</summary>
        public int AtObstacleIndex { get; init; } = -1;

        /// <summary>
        /// No turn: no arc of this radius fitted inside the turn line anywhere along the pass, and
        /// this is how far the best try fell outside it (<see cref="YouTurnCreationOutput.OutsideTurnLineBy"/>).
        /// The geometry refuses the turn, not the planner: the operator can lower the U-turn
        /// distance from boundary by this much, or turn by hand.
        /// </summary>
        public double? OutsideTurnLineBy { get; init; }

        /// <summary>
        /// With a <see cref="Path"/>: how far the turn was seated back because an obstacle stopped it
        /// nearer the end (it landed on a bend round a Go around obstacle, or its legs or exit met an
        /// obstacle); 0 when none did. The end of the pass before the turn is left to do, and the
        /// operator is told. Fork only.
        /// </summary>
        public double ObstacleSetback { get; init; }

        /// <summary>With <see cref="ObstacleSetback"/>: the inner boundary whose crossing seated the
        /// turn back last, or -1 (a bend of the next pass, whose obstacle is not known here).</summary>
        public int SetbackObstacleIndex { get; init; } = -1;
    }

    /// <summary>
    /// Maps the persisted <see cref="GuidanceConfig.UTurnStyle"/> integer onto the
    /// creation <see cref="YouTurnType"/>. 0 = Sagitta (default), 1 = K-style, 2 = Keyhole.
    /// Unknown values — including the retired Albin/Dubins style and any old
    /// numbering — fall back to Sagitta.
    /// </summary>
    private static YouTurnType MapTurnStyle(int uTurnStyle) => uTurnStyle switch
    {
        (int)YouTurnType.KStyle => YouTurnType.KStyle,
        (int)YouTurnType.KeyholeStyle => YouTurnType.KeyholeStyle,
        _ => YouTurnType.SagittaStyle,
    };

    /// <summary>How far a turn may be seated back from the end of its pass to keep the implement off a
    /// hard fence, m (an obstacle may seat it further back).</summary>
    private const double MaxExtraSetback = 30.0;

    /// <summary>How many times <see cref="CreateTurnPath"/> was called: the tests that hold how often a turn is built.</summary>
    internal int TurnPathBuilds { get; private set; }

    /// <summary>
    /// Build a complete U-turn path for the current vehicle state.
    /// The turn is smoothed as AgOpenGPS (<see cref="TurnPathSmoothing.SmoothYouTurn"/>, 6 points,
    /// not a K-turn); <see cref="GuidanceConfig.UTurnSmoothing"/> smooths only the simple fallback.
    /// </summary>
    public TurnPathResult CreateTurnPath(
        Position currentPosition,
        Models.Track.Track selectedTrack,
        double headingRadians,
        double abHeading,
        Boundary? boundary,
        IReadOnlyList<Vec3>? headlandLine,
        GuidanceWorkingState guidance,
        YouTurnWorkingState turn,
        int uTurnSkipRows,
        double headlandCalculatedWidth,
        double headlandDistance)
    {
        TurnPathBuilds++;
        // For a front implement's centre when the operator chose that (FrontToolPlan); where no
        // such turn is made (it does not fit, an obstacle or a hard fence stops it, the tractor
        // could not drive it), for the pivot, as without the choice. Fork only.
        var plan = FrontToolPlanFor(_configStore, MapTurnStyle(_configStore.Guidance.UTurnStyle) == YouTurnType.KStyle);
        if (plan.On)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            var forTool = CreateTurnPathFor(plan, currentPosition, selectedTrack, headingRadians, abHeading, boundary,
                headlandLine, guidance, turn, uTurnSkipRows, headlandCalculatedWidth, headlandDistance);
            if (forTool.Path != null && !forTool.UsedFallback)
            {
                _logger.LogDebug("[YouTurn] Planned for the front implement's centre ({Ms:F0} ms): the implement leaves the pass {D:F1} m after the tractor",
                    System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    forTool.ToolLeavesPassAt is { } tool && forTool.ArcStart is { } pivot ? GeometryMath.Distance(tool, pivot) : 0.0);
                return forTool;
            }
            if (headlandLine != null)
                _logger.LogDebug("[YouTurn] No turn planned for the front implement's centre here ({Ms:F0} ms); planning for the pivot",
                    System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        return CreateTurnPathFor(default, currentPosition, selectedTrack, headingRadians, abHeading, boundary,
            headlandLine, guidance, turn, uTurnSkipRows, headlandCalculatedWidth, headlandDistance);
    }

    /// <summary><see cref="CreateTurnPath"/> for the pivot, or for a front implement's centre
    /// (<paramref name="plan"/>).</summary>
    private TurnPathResult CreateTurnPathFor(
        FrontToolPlan plan,
        Position currentPosition,
        Models.Track.Track selectedTrack,
        double headingRadians,
        double abHeading,
        Boundary? boundary,
        IReadOnlyList<Vec3>? headlandLine,
        GuidanceWorkingState guidance,
        YouTurnWorkingState turn,
        int uTurnSkipRows,
        double headlandCalculatedWidth,
        double headlandDistance)
    {
        if (headlandLine == null)
            return new TurnPathResult(null, false);

        bool turnLeft = turn.IsTurnLeft;

        _logger.LogDebug("[YouTurn] Creating turn: direction={Dir}, sameWay={SameWay}, pathsAway={Away}",
            turnLeft ? "LEFT" : "RIGHT", guidance.IsHeadingSameWay, guidance.HowManyPathsAway);

        var config = _configStore;

        // Hard outer boundary: the implement's swept path (including a long
        // mounted tool's rear corners) must stay clear of the fence/obstacle
        // edge. When set, we shift the turn inward and regenerate until the
        // implement clears, or give up and refuse to auto-engage. A hard obstacle
        // the turn is made at gets the same check, from its outside.
        bool hardOuter = boundary?.OuterBoundary?.IsHard == true && boundary.OuterBoundary.IsValid;
        var outerPolygon = hardOuter
            ? boundary!.OuterBoundary!.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList()
            : null;
        double clearanceMargin = Math.Max(0.0, config.Guidance.UTurnDistanceFromBoundary);

        // Diagnostic: one line per turn build to see whether the clearance path is
        // armed at runtime and with what geometry (note actualWidth vs storedWidth).
        _logger.LogDebug(
            "[YouTurn][HardBoundary] hardOuter={Hard} isHard={IsHard} validOuter={Valid} actualWidth={AW:F1} storedWidth={W:F1} mount={Mount} length={L:F1} margin={M:F1}",
            hardOuter, boundary?.OuterBoundary?.IsHard, boundary?.OuterBoundary?.IsValid,
            config.ActualToolWidth, config.Tool.Width, RigGeometry.Mount(config.Tool),
            config.Tool.Length, clearanceMargin);

        const double SetbackStep = 0.5;
        // A turn seated back to clear a hard boundary must still start this far ahead of the tractor.
        const double MinArcStartAhead = 2.0;
        // The step a turn is seated further back by when it lands on a bend of the next pass or
        // its legs or exit meet an obstacle (a coarser step than the implement's: a bend is metres long,
        // and the plan is tried again every cycle until a turn is found).
        const double ObstacleSetbackStep = 4.0;
        // How far back a turn an obstacle stops may be seated (it lands on a bend round a Go around
        // obstacle, or its legs or exit meet an obstacle). A Go around tree 47 m from a hard fence
        // stopped every turn on the passes beside it within 30 m, and with no turn there the job
        // stopped (a tester's field, the farm job harness: 9.5 % sprayed). Seated back that far the
        // end of the pass is left to do, and the operator is
        // told (TurnPathResult.ObstacleSetback). The hard fence's own clearance keeps 30 m unless an
        // obstacle already seated the turn back. Fork only.
        const double MaxObstacleSetback = 60.0;
        double extraSetback = 0.0;
        // An obstacle seated the turn back (MaxObstacleSetback), the last one known by its index.
        bool obstacleSetback = false;
        int setbackObstacle = -1;
        // Where the pass leaves the turn area on the first try: a try with the turn lines moved in
        // seats the turn back from there (YouTurnCreationInput.TurnLineEndIndex).
        int? turnLineEnd = null;
        // The second try after a wide turn of no U-turn's shape: the omega turn (see below).
        bool omegaOnly = false;
        // The turn area of the first try, the turn lines where they are: a try with them moved in
        // moves the other end of the next pass in too, which is not where that pass ends
        // (FrontToolPlan.NextTurnRoom).
        Func<Vec3, int>? turnAreaAsItIs = null;

        while (true)
        {
            _straightDestination = _bentDestination = null;
            var input = BuildCreationInput(
                currentPosition, selectedTrack, headingRadians, abHeading, turnLeft,
                boundary, headlandLine, guidance, turn, uTurnSkipRows, headlandCalculatedWidth,
                out var obstacles, extraSetback, plan);

            if (input == null)
            {
                _logger.LogWarning("[YouTurn] Failed to build creation input - no boundary available?");
                return new TurnPathResult(null, false);
            }
            if (extraSetback > 0) input.TurnLineEndIndex = turnLineEnd;
            input.NoWideTurn = omegaOnly;
            turnAreaAsItIs ??= input.IsPointInsideTurnArea;

            var output = CreateTurn(input);
            if (turnLineEnd == null && output.Success && output.TurnLineEndIndex >= 0)
                turnLineEnd = output.TurnLineEndIndex;

            // A pass that runs into an obstacle turns before it. When no turn fits there, the
            // fallback (laid out from the outer boundary) would drive the tractor into the
            // obstacle, so there is no turn and the operator is told.
            var obstacle = ObstacleAhead(input, obstacles);
            bool atObstacle = obstacle != null;
            bool hardObstacle = obstacle?.IsHard == true;

            if (output.Success && output.TurnPath != null && output.TurnPath.Count > 10)
            {
                var path = output.TurnPath;

                // Smooth FIRST, then validate: the spiral/pretzel guard judges the path that is
                // driven. The smoothing is AgOpenGPS's (SmoothYouTurn with 6 points, one pass, a
                // U turn only): a wider or repeated average pulls a tight arc inward, so the turn
                // ended a metre short of the turn line and bent tighter than the set radius at a
                // 5 m radius. The legs meet the arc without a fold (WalkLegByDistance), so there
                // is nothing for a heavier smoothing to hide.
                if (input.TurnType != YouTurnType.KStyle)
                    TurnPathSmoothing.SmoothYouTurn(path, 6);

                // A turn lands on a straight stretch of the next pass. Where that pass bends round
                // a Go around obstacle at the landing, the generator joins the bend's slope (a net
                // turn of 150°, loops of 500°), the tractor comes out of the arc metres off the
                // bent line, and a trailed implement sweeps through the tree (a 10 m sprayer by
                // 1.7 m, hay rakes by 0.5 m, on two real fields): the turn is seated further back
                // until it lands before the bend, else there is no turn. Asked before the turn's
                // shape is judged: such a turn is also 30° or more off a U-turn, and near a hard
                // fence that refused it at once, without the try further back (a drill on a
                // tester's field, the farm job harness).
                if (LandsOnBend(path, out double offStraight))
                {
                    extraSetback += ObstacleSetbackStep;
                    obstacleSetback = true;
                    setbackObstacle = -1;   // a bend, not an obstacle crossed: named by the last cause
                    if (extraSetback <= MaxObstacleSetback)
                    {
                        _logger.LogDebug("[YouTurn] The turn lands {D:F1} m off the straight next pass, on a bend; seating it further back (setback {S:F1} m)",
                            offStraight, extraSetback);
                        continue;
                    }
                    _logger.LogWarning("[YouTurn] No turn within {S:F0} m lands on a straight stretch of the next pass; no turn.", MaxObstacleSetback);
                    return new TurnPathResult(null, UsedFallback: false) { AtObstacle = true };
                }

                // Measure turning from SEGMENT DIRECTIONS (positions), not the stored per-point
                // headings — smoothing only moves positions (it preserves the stale headings),
                // and the generator's recomputed headings are exactly what's noisy.
                //   - Primary check: net start→end direction change should be ~π (180°).
                //   - Secondary safety: cumulative |Δdir| capped at 400° (an omega turn is 360°) to
                //     catch loops and spirals the tractor would not follow.
                var dirs = new List<double>(path.Count);
                var dirAt = new List<int>(path.Count);
                for (int i = 1; i < path.Count; i++)
                {
                    double de = path[i].Easting - path[i - 1].Easting;
                    double dn = path[i].Northing - path[i - 1].Northing;
                    if ((de * de + dn * dn) < 1e-4) continue; // skip near-duplicate points
                    dirs.Add(Math.Atan2(de, dn));
                    dirAt.Add(i);
                }

                // A turn that starts on a Go around bend of the pass (the entry leg on the bend, the
                // arc from its heading: ArcFromBend) turns 180° less that bend's angle to the pass,
                // and the bend's own turning is the pass's, not the turn's. Fork only.
                double startDeviation = 0;
                int arcFrom = 0;
                if (input.StraightGuidancePoints != null && dirs.Count >= 2)
                {
                    startDeviation = DeviationFromStraight(input.StraightGuidancePoints, path[0], dirs[0]);
                    if (output.ArcStart is { } arcAt)
                    {
                        int m = PassEnds.NearestIndex(path, arcAt.Easting, arcAt.Northing);
                        arcFrom = Math.Max(0, dirAt.FindIndex(i => i >= m));
                    }
                }

                double netHeadingChange = 0, totalHeadingChange = 0;
                if (dirs.Count >= 2)
                {
                    netHeadingChange = dirs[^1] - dirs[0];
                    while (netHeadingChange > Math.PI) netHeadingChange -= 2 * Math.PI;
                    while (netHeadingChange < -Math.PI) netHeadingChange += 2 * Math.PI;

                    // From the entry leg's last stretch into the arc on: dirs[arcFrom] is that last
                    // stretch, so the bend's own turning before it is left out.
                    for (int i = arcFrom > 0 ? arcFrom + 1 : 1; i < dirs.Count; i++)
                    {
                        double delta = dirs[i] - dirs[i - 1];
                        while (delta > Math.PI) delta -= 2 * Math.PI;
                        while (delta < -Math.PI) delta += 2 * Math.PI;
                        totalHeadingChange += Math.Abs(delta);
                    }
                }

                // ~π expected for a U-turn; tolerate ±30° slack to account for approach angle.
                // A K-turn's path is AgOpenGPS's KStyleTurn: an arc of 2.2 rad and a straight tail,
                // the rest of the turn made backing up, so its net turn is 2.2 rad (126°), not π.
                // From a bend, π less the bend's angle to the pass, the way the arc turns.
                bool netIsUTurnLike;
                if (input.TurnType == YouTurnType.KStyle)
                    netIsUTurnLike = Math.Abs(Math.Abs(netHeadingChange) - 2.2) < Math.PI / 6.0;
                else
                {
                    double expected = (input.ArcTurnLeft ? -Math.PI : Math.PI) - startDeviation;
                    double off = netHeadingChange - expected;
                    while (off > Math.PI) off -= 2 * Math.PI;
                    while (off < -Math.PI) off += 2 * Math.PI;
                    netIsUTurnLike = Math.Abs(off) < Math.PI / 6.0;
                }
                // 400°: an omega turn is 360°; more is a loop the tractor does not follow (a 12.5 m
                // rake came out of a 500° loop along a wall's turn line 8 m off its line, pointing at
                // the wall 5 m away; a 4 m rake out of a 518° loop in the headland 1.3 m off, into a tree).
                bool cumulativeRunaway = totalHeadingChange > Math.PI * 20.0 / 9.0;

                if (!netIsUTurnLike || cumulativeRunaway)
                {
                    // A wide turn runs along the turn line from the end of this pass to the end
                    // of the next. Where the next pass runs on far past this one's end (this pass
                    // ends at a bump of the fence, the next one 150 m on round a corner of the
                    // field) that is no U-turn, and it was refused with a turn onto the next pass
                    // abreast of the tractor there to be made: the omega turn, one shape square to
                    // the track, is tried first, seated from the start. Fork only.
                    if (output.IsWideTurn && !omegaOnly)
                    {
                        _logger.LogDebug("[YouTurn] Wide turn malformed (net={Net:F0}° cumulative={Cum:F0}°); trying the omega turn",
                            netHeadingChange * 180 / Math.PI, totalHeadingChange * 180 / Math.PI);
                        omegaOnly = true;
                        extraSetback = 0.0;
                        obstacleSetback = false;
                        setbackObstacle = -1;
                        continue;
                    }
                    if (atObstacle)
                    {
                        _logger.LogWarning("[YouTurn] Turn before obstacle {I} was malformed (net={Net:F0}° cumulative={Cum:F0}°); no turn.",
                            obstacle!.InnerIndex + 1, netHeadingChange * 180 / Math.PI, totalHeadingChange * 180 / Math.PI);
                        return new TurnPathResult(null, UsedFallback: false) { AtObstacle = true };
                    }
                    // On a pass bent round Go around obstacles a turn seated on a bend can loop (a
                    // counter-arc onto a pass the bend took nearer); seated further back it starts
                    // on the straight. Fork only.
                    if (input.StraightGuidancePoints != null && extraSetback + ObstacleSetbackStep <= MaxObstacleSetback)
                    {
                        extraSetback += ObstacleSetbackStep;
                        obstacleSetback = true;
                        setbackObstacle = -1;
                        _logger.LogDebug("[YouTurn] Turn malformed (net={Net:F0}° cumulative={Cum:F0}°) on a pass bent round an obstacle; seating it further back (setback {S:F1} m)",
                            netHeadingChange * 180 / Math.PI, totalHeadingChange * 180 / Math.PI, extraSetback);
                        continue;
                    }
                    if (hardOuter)
                    {
                        _logger.LogWarning("[YouTurn] Turn near hard boundary was malformed (net={Net:F0}° cumulative={Cum:F0}°); not engaging.",
                            netHeadingChange * 180 / Math.PI, totalHeadingChange * 180 / Math.PI);
                        return new TurnPathResult(null, UsedFallback: false) { ClearanceBlocked = true, NoTurnBuilt = true };
                    }
                    _logger.LogWarning("[YouTurn] Service path rejected: net={Net:F0}° cumulative={Cum:F0}° - using simple fallback",
                        netHeadingChange * 180 / Math.PI, totalHeadingChange * 180 / Math.PI);
                    var fallback = SimpleFallback(currentPosition, abHeading, turnLeft, boundary,
                        guidance, turn, uTurnSkipRows, headlandDistance, selectedTrack);
                    return FallbackResult(fallback, boundary, config);
                }

                // Planned for a front implement's centre: so far the path is the implement's. From
                // here on it is the pivot's, the one that takes the implement along it, a point for
                // a point: the path steered, checked below and kept as the turn's. Where the pivot
                // leaves the pass is a reach before where the implement does. Fork only.
                Vec3? leavesPass = output.ArcStart, toolLeavesPass = null;
                if (plan.On)
                {
                    var pivotPath = PivotPathFor(path, plan);
                    if (pivotPath == null) return new TurnPathResult(null, UsedFallback: false);
                    // The turn at the other end of the next pass must still have its room
                    // (FrontToolPlan.NextTurnRoom): on a short pass, for the pivot.
                    double passLeft = PassLeftAfter(pivotPath, _bentDestination, p => turnAreaAsItIs(p) == 0, plan.NextTurnRoom);
                    if (passLeft < plan.NextTurnRoom)
                    {
                        _logger.LogDebug("[YouTurn] Planned for the front implement, the turn would end with {L:F1} m of the next pass left ({N:F1} m needed for the turn at its other end); planning for the pivot",
                            passLeft, plan.NextTurnRoom);
                        return new TurnPathResult(null, UsedFallback: false);
                    }
                    if (output.ArcStart is { } toolArc)
                    {
                        toolLeavesPass = toolArc;
                        leavesPass = PivotWhenToolIsAt(path, pivotPath, toolArc);
                    }
                    path = pivotPath;
                }

                // Implement clearance against the hard outer boundary and a hard obstacle the
                // turn is made at, checked on the final (smoothed) path that will actually be driven.
                if (hardOuter || hardObstacle)
                {
                    var swept = ImplementSweptPath.Compute(path, BuildToolGeometry(config));
                    var clearance = new TurnClearance.ClearanceResult(true, double.NegativeInfinity);
                    if (hardOuter)
                        clearance = TurnClearance.Evaluate(swept, outerPolygon!, TurnClearance.KeepSide.Inside, clearanceMargin);
                    if (hardObstacle)
                    {
                        var round = TurnClearance.Evaluate(swept, obstacle!.Fence, TurnClearance.KeepSide.Outside, clearanceMargin);
                        if (round.MaxIntrusion > clearance.MaxIntrusion) clearance = round;
                    }

                    if (!clearance.IsClear)
                    {
                        extraSetback += Math.Max(SetbackStep, clearance.MaxIntrusion);
                        if (extraSetback <= (obstacleSetback ? MaxObstacleSetback : MaxExtraSetback))
                        {
                            _logger.LogDebug("[YouTurn] Implement intrudes hard boundary by {I:F2}m; shifting turn away (setback {S:F2}m) and retrying",
                                clearance.MaxIntrusion, extraSetback);
                            continue;
                        }

                        _logger.LogWarning("[YouTurn] No forward turn keeps the implement clear of the hard boundary (intrusion {I:F2}m); not engaging.",
                            clearance.MaxIntrusion);
                        return new TurnPathResult(null, UsedFallback: false) { ClearanceBlocked = true, AtObstacle = atObstacle };
                    }

                    // Cleared. MaxIntrusion <= 0; the worst implement point sits
                    // (margin - MaxIntrusion) m clear of the fence — log the achieved gap.
                    _logger.LogDebug("[YouTurn][HardBoundary] CLEARED: nearest implement point {C:F2}m from the fence (margin {M:F2}m, setback {S:F2}m)",
                        clearanceMargin - clearance.MaxIntrusion, clearanceMargin, extraSetback);

                    // Seated back under or behind the tractor, the turn would start at once, not
                    // at the headland (built from a crossing behind the tractor at a field's
                    // entry, it turned the tractor round there and out of the field): not engaging.
                    // At an obstacle the result says so, and the operator is told it is the
                    // obstacle, not the field's edge.
                    if (leavesPass is { } arcStart)
                    {
                        double arcAhead = (arcStart.Easting - currentPosition.Easting) * Math.Sin(headingRadians)
                                        + (arcStart.Northing - currentPosition.Northing) * Math.Cos(headingRadians);
                        if (arcAhead < MinArcStartAhead)
                        {
                            if (atObstacle)
                                _logger.LogWarning("[YouTurn] The turn before obstacle {I}, seated back {S:F1}m, would start {D:F1}m behind the tractor; no turn.",
                                    obstacle!.InnerIndex + 1, extraSetback, MinArcStartAhead - arcAhead);
                            else
                                _logger.LogWarning("[YouTurn] The turn seated back {S:F1}m to clear the hard boundary would start {D:F1}m behind the tractor; not engaging.",
                                    extraSetback, MinArcStartAhead - arcAhead);
                            return new TurnPathResult(null, UsedFallback: false) { ClearanceBlocked = true, AtObstacle = atObstacle };
                        }
                    }
                }

                // The whole path, and the first metres of the next pass after it, against every
                // obstacle chosen as U-turn. The generator keeps the arc outside the turn line it
                // turns at, not the legs outside the others: at a lone tree the path crossed the
                // next tree's turn line 8 m away, and a turn whose exit landed just before another
                // obstacle ended in the AutoSteer-off alarm with no room. No such turn is made;
                // the operator is told and takes over before the obstacle.
                int arcIndex = leavesPass is { } seated ? PassEnds.NearestIndex(path, seated.Easting, seated.Northing) : 0;
                // Planned for the implement, the path goes on along the next pass further than the
                // pivot's turn would: its arc ends that much further back, and its exit leg is longer.
                double extension = Math.Max(0.0, config.Guidance.UTurnExtension);
                double exitReach = ExitReachFor(plan,
                    plan.Radius - plan.PivotRadius + Math.Max(plan.MinExitLeg, extension) - plan.Reach - extension);
                if (boundary != null && !ClearsTurnObstacles(path, boundary, config, out int crossed, exitReach, obstacle?.InnerIndex ?? -1,
                        obstacles, arcFrom: arcIndex))
                {
                    // Seated further back the legs may clear the obstacle, or the exit land with
                    // room before it; then there is no turn.
                    extraSetback += ObstacleSetbackStep;
                    obstacleSetback = true;
                    setbackObstacle = crossed;
                    if (extraSetback <= MaxObstacleSetback)
                    {
                        _logger.LogDebug("[YouTurn] The turn would cross obstacle {I}, or end right before it; seating it further back (setback {S:F1} m)",
                            crossed + 1, extraSetback);
                        continue;
                    }
                    _logger.LogWarning("[YouTurn] The turn would cross obstacle {I}, or end right before it; no turn.", crossed + 1);
                    return new TurnPathResult(null, UsedFallback: false) { AtObstacle = true };
                }

                // Seated back further than the fence's clearance ever seats a turn, it must still
                // start ahead of the tractor, a soft fence too (the hard one is checked above).
                if (obstacleSetback && extraSetback > MaxExtraSetback && leavesPass is { } farStart)
                {
                    double ahead = (farStart.Easting - currentPosition.Easting) * Math.Sin(headingRadians)
                                 + (farStart.Northing - currentPosition.Northing) * Math.Cos(headingRadians);
                    if (ahead < MinArcStartAhead)
                    {
                        _logger.LogWarning("[YouTurn] The turn seated back {S:F1} m for an obstacle would start {D:F1} m behind the tractor; no turn.",
                            extraSetback, MinArcStartAhead - ahead);
                        return new TurnPathResult(null, UsedFallback: false) { AtObstacle = true };
                    }
                }

                _logger.LogDebug("[YouTurn] Path created with {Count} points, net={Net:F0}° cumulative={Cum:F0}° (setback {S:F2}m){At}",
                    path.Count, netHeadingChange * 180 / Math.PI, totalHeadingChange * 180 / Math.PI, extraSetback,
                    atObstacle ? $", before obstacle {obstacle!.InnerIndex + 1}" : "");
                return new TurnPathResult(path, UsedFallback: false)
                {
                    ArcStart = leavesPass, ToolLeavesPassAt = toolLeavesPass,
                    AtObstacle = atObstacle, AtObstacleIndex = atObstacle ? obstacle!.InnerIndex : -1,
                    ObstacleSetback = obstacleSetback ? extraSetback : 0, SetbackObstacleIndex = obstacleSetback ? setbackObstacle : -1,
                };
            }

            // Creation failed outright.
            if (atObstacle)
            {
                _logger.LogWarning("[YouTurn] No turn fits before obstacle {I} ({Reason}); no turn.",
                    obstacle!.InnerIndex + 1, output.FailureReason ?? "unknown");
                return new TurnPathResult(null, UsedFallback: false) { AtObstacle = true };
            }
            if (hardOuter)
            {
                _logger.LogWarning("[YouTurn] Turn creation failed near a hard boundary ({Reason}); not engaging.",
                    output.FailureReason ?? "unknown");
                return new TurnPathResult(null, UsedFallback: false)
                {
                    ClearanceBlocked = true, NoTurnBuilt = output.Failure == TurnFailure.DoesNotFit,
                    Failure = output.Failure, OutsideTurnLineBy = output.OutsideTurnLineBy,
                };
            }

            // The simple fallback is laid out for the pivot: with none made for the implement,
            // the turn is planned for the pivot first (CreateTurnPath).
            if (plan.On) return new TurnPathResult(null, UsedFallback: false) { Failure = output.Failure };

            _logger.LogWarning("[YouTurn] Service failed: {Reason} - using simple fallback",
                output.FailureReason ?? "unknown");
            var fallbackPath = SimpleFallback(currentPosition, abHeading, turnLeft, boundary,
                guidance, turn, uTurnSkipRows, headlandDistance, selectedTrack);
            if (fallbackPath.Count <= 10)
                return new TurnPathResult(null, UsedFallback: true)
                {
                    OutsideTurnLineBy = output.OutsideTurnLineBy,
                    Failure = output.Failure,
                };
            return FallbackResult(fallbackPath, boundary, config);
        }
    }

    /// <summary>
    /// The simple fallback, held to the obstacles chosen as U-turn as the generator's path is
    /// (<see cref="ClearsTurnObstacles"/>, with the exit, <see cref="ExitReach"/>): it is laid out
    /// from the tractor and <see cref="LeavesField"/> keeps it out of the obstacles themselves only,
    /// so it could run through an obstacle's turn line beside the pass, swing the implement into a
    /// hard one, or end just before one, where the AutoSteer-off alarm then leaves no room to stop.
    /// No such turn is made; the operator is told ("No room for a U-turn before the obstacle").
    /// As official #613 (Sourcery's review, 16 Nov 2026).
    /// </summary>
    internal TurnPathResult FallbackResult(List<Vec3> fallback, Boundary? boundary, ConfigurationStore config)
    {
        if (fallback.Count <= 10) return new TurnPathResult(null, UsedFallback: true);
        if (boundary != null && !ClearsTurnObstacles(fallback, boundary, config, out int crossed, ExitReach,
                promoted: _goAround.CurrentBlocked))
        {
            _logger.LogWarning("[YouTurn] The fallback turn would cross obstacle {I}, or end right before it; no turn.", crossed + 1);
            return new TurnPathResult(null, UsedFallback: true) { AtObstacle = true };
        }
        return new TurnPathResult(fallback, UsedFallback: true);
    }

    /// <summary>
    /// Maps the runtime tool/vehicle config onto the pure <see cref="ToolGeometry"/>
    /// used by the implement swept-path clearance check.
    /// </summary>
    // Real implement width comes from the active sections (ActualToolWidth), not Tool.Width:
    // the latter is a stored fallback (often the 6 m default).
    private static ToolGeometry BuildToolGeometry(ConfigurationStore config) =>
        RigGeometry.Geometry(config.Vehicle, config.Tool, config.ActualToolWidth);

    /// <summary>
    /// True when the implement swept along <paramref name="path"/> stays clear of a
    /// hard outer boundary by the configured margin — or when there is no hard
    /// boundary (in which case it is always "clear"). <paramref name="intrusion"/>
    /// is how far the worst implement point pushes past the margin (&gt; 0 = violated).
    /// </summary>
    private static bool ImplementClearsHardBoundary(
        IReadOnlyList<Vec3> path, Boundary? boundary, ConfigurationStore config, out double intrusion)
    {
        intrusion = 0.0;
        if (boundary?.OuterBoundary == null || !boundary.OuterBoundary.IsValid
            || !boundary.OuterBoundary.IsHard || path == null || path.Count < 2)
            return true;

        var outerPolygon = boundary.OuterBoundary.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList();
        var swept = ImplementSweptPath.Compute(path, BuildToolGeometry(config));
        double margin = Math.Max(0.0, config.Guidance.UTurnDistanceFromBoundary);
        var clearance = TurnClearance.Evaluate(swept, outerPolygon, TurnClearance.KeepSide.Inside, margin);
        intrusion = clearance.MaxIntrusion;
        return clearance.IsClear;
    }

    /// <summary>
    /// The hard-boundary check for a turn the operator asks for where the tractor is. The implement
    /// keeps the U-turn distance from the fence along the whole turn, as in an automatic turn,
    /// unless it is already nearer than that at the press (the pass along a fence, a pass that
    /// meets the fence at a shallow angle): that distance is gone before the turn starts, so the
    /// turn only has to keep the implement inside the fence, and an implement already across it
    /// no further across. Judged against the full distance, such a turn was refused for where the
    /// implement already stood, also when it turned away from the fence: an 8 m front boom
    /// 1.5 m from the fence on its right could not turn left, and nothing but driving on into the
    /// corner was left. <paramref name="intrusion"/> is how far the worst implement point is
    /// inside the U-turn distance (over the distance itself: across the fence).
    /// </summary>
    private static bool ManualTurnClearsHardBoundary(
        IReadOnlyList<Vec3> path, Boundary? boundary, ConfigurationStore config, out double intrusion)
    {
        intrusion = 0.0;
        if (boundary?.OuterBoundary == null || !boundary.OuterBoundary.IsValid
            || !boundary.OuterBoundary.IsHard || path == null || path.Count < 2)
            return true;

        var outerPolygon = boundary.OuterBoundary.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList();
        var swept = ImplementSweptPath.Compute(path, BuildToolGeometry(config));
        double margin = Math.Max(0.0, config.Guidance.UTurnDistanceFromBoundary);
        var clearance = TurnClearance.Evaluate(swept, outerPolygon, TurnClearance.KeepSide.Inside, margin);
        intrusion = clearance.MaxIntrusion;
        if (clearance.IsClear) return true;

        return SweepKeepsToFence(swept, outerPolygon, margin);
    }

    /// <summary>
    /// The second half of <see cref="ManualTurnClearsHardBoundary"/>, for an implement that does
    /// not keep <paramref name="margin"/> from the fence along the whole turn. Refused when it has
    /// the distance at the press (the turn would take it). Else each corner is held to its own
    /// place at the press: one inside the fence stays inside it, one across it goes no further
    /// across. Judged by the worst corner alone, a corner 0.5 m across at the press let another
    /// one, inside then, cross by as much during the turn.
    /// </summary>
    internal static bool SweepKeepsToFence(ImplementSweptPathResult swept, IReadOnlyList<Vec2> outerPolygon, double margin)
    {
        var corners = new[] { swept.FrontLeft, swept.FrontRight, swept.RearLeft, swept.RearRight };
        // Where the implement is at the press: the path's first point is the tractor.
        var atPress = new double[corners.Length];
        bool clearAtPress = true;
        for (int i = 0; i < corners.Length; i++)
        {
            if (corners[i].Count == 0) { atPress[i] = double.NegativeInfinity; continue; }
            atPress[i] = TurnClearance.Evaluate(new[] { corners[i][0] }, outerPolygon, TurnClearance.KeepSide.Inside, margin).MaxIntrusion;
            if (atPress[i] > 0.0) clearAtPress = false;
        }
        if (clearAtPress) return false; // it has the distance now and the turn would take it

        // Inside the distance already: up to the fence. Across it already: no further.
        for (int i = 0; i < corners.Length; i++)
        {
            double along = TurnClearance.Evaluate(corners[i], outerPolygon, TurnClearance.KeepSide.Inside, margin).MaxIntrusion;
            if (along > Math.Max(margin, atPress[i] + ManualTurnFenceSlack)) return false;
        }
        return true;
    }

    /// <summary>What an implement already across a hard fence at the press may move in a manual
    /// turn before it counts as going further across (m): the turn's first point is the press itself.</summary>
    private const double ManualTurnFenceSlack = 0.01;

    // ── Input builder ───────────────────────────────────────────────────

    private YouTurnCreationInput? BuildCreationInput(
        Position currentPosition,
        Models.Track.Track track,
        double headingRadians,
        double abHeading,
        bool turnLeft,
        Boundary? boundary,
        IReadOnlyList<Vec3> headlandLine,
        GuidanceWorkingState guidance,
        YouTurnWorkingState turn,
        int uTurnSkipRows,
        double headlandCalculatedWidth,
        out List<TurnObstacle> obstacles,
        double extraInwardSetback = 0.0,
        FrontToolPlan plan = default)
    {
        obstacles = EmptyObstacles;
        if (boundary?.OuterBoundary == null || !boundary.OuterBoundary.IsValid)
        {
            _logger.LogDebug("[YouTurn] No valid outer boundary available");
            return null;
        }

        var config = _configStore;
        double toolWidth = config.ActualToolWidth;
        double totalHeadlandWidth = headlandCalculatedWidth;

        var outerPoints = boundary.OuterBoundary.Points
            .Select(p => new Vec2(p.Easting, p.Northing))
            .ToList();

        // Turn boundary controls where the outermost point of the turn can reach.
        //  > 0: turn stays that far inside the field
        //  = 0: turn may touch the outer boundary
        //  < 0: turn may extend past the outer boundary
        // Base setback, plus any extra the clearance loop asked for to pull the
        // turn further inside a hard boundary.
        double distanceFromBoundary = config.Guidance.UTurnDistanceFromBoundary + extraInwardSetback;
        List<Vec2>? turnBoundaryVec2;
        if (distanceFromBoundary > 0.1)
            turnBoundaryVec2 = _polygonOffsetService.CreateInwardOffset(outerPoints, distanceFromBoundary);
        else if (distanceFromBoundary < -0.1)
            turnBoundaryVec2 = _polygonOffsetService.CreateOutwardOffset(outerPoints, -distanceFromBoundary);
        else
            turnBoundaryVec2 = outerPoints;

        if (turnBoundaryVec2 == null || turnBoundaryVec2.Count < 3)
        {
            _logger.LogDebug("[YouTurn] Offset failed, using outer boundary directly");
            turnBoundaryVec2 = outerPoints;
        }
        var turnBoundaryVec3 = _polygonOffsetService.CalculatePointHeadings(turnBoundaryVec2);

        var headlandBoundaryVec2 = _polygonOffsetService.CreateInwardOffset(outerPoints, totalHeadlandWidth);
        if (headlandBoundaryVec2 == null || headlandBoundaryVec2.Count < 3)
        {
            _logger.LogDebug("[YouTurn] Failed to create headland boundary");
            return null;
        }
        var headlandBoundaryVec3 = _polygonOffsetService.CalculatePointHeadings(headlandBoundaryVec2);

        var boundaryTurnLines = new List<BoundaryTurnLine>
        {
            new BoundaryTurnLine { Points = turnBoundaryVec3, BoundaryIndex = 0 }
        };
        // The current pass as the pipeline steers it: offset, then bent round the Go around
        // obstacles it runs into. The ones it has no room to go round get a U-turn before them
        // on this pass (promoted), with the obstacles chosen as U-turn.
        List<Vec3> basePoints = track.Points.Count > 2
            ? new List<Vec3>(track.Points)
            : DensifyAbLine(AbLineAcrossField(track.Points, outerPoints));

        double widthMinusOverlap = toolWidth - config.Tool.Overlap;
        double offsetDistance = Models.Track.PassGeometry.Offset(track, guidance.HowManyPathsAway, widthMinusOverlap, guidance.NudgeOffset,
            Models.Track.PassGeometry.ToolShift(config.Tool.Offset, guidance.IsHeadingSameWay));
        // The pass as the shared routine builds it (CurveProcessing.CurvePass / ExtendPass), with
        // the U-turn's own geometry: no turn-radius limit, the loop not joined at its seam.
        var pass = CurveProcessing.CurvePass(basePoints, offsetDistance);
        if (pass.Points.Count < 2)
        {
            // No pass here: the offset removed all of it (#694); nothing to turn from.
            _logger.LogDebug("[YouTurn] Pass {Path} has no line (curve too tight)", guidance.HowManyPathsAway);
            return null;
        }
        List<Vec3> offsetCurve = pass.Points;
        var bend = _goAround.For(track, offsetDistance, offsetCurve, boundary);
        if (bend != null && bend.GoneRound.Count > 0) offsetCurve = bend.Points;
        IReadOnlyList<int> promoted = bend?.Blocked ?? Array.Empty<int>();

        // The obstacles a U-turn is made at, turn lines 1..n after the outer one. The extra
        // setback the clearance loop asks for moves them out from the obstacle the same way.
        obstacles = TurnObstacles(boundary, distanceFromBoundary, promoted, HardReach(config));
        foreach (var o in obstacles)
            boundaryTurnLines.Add(new BoundaryTurnLine { Points = o.TurnLine, BoundaryIndex = o.InnerIndex + 1 });

        double headlandWidthForTurn = Math.Max(totalHeadlandWidth - toolWidth, toolWidth);

        // Asked for every candidate path point: the turn line's strip index, built once per plan.
        var turnArea = PolygonSlabIndex.Build(turnBoundaryVec3);
        Func<Vec3, int> isPointInsideTurnArea = TurnAreaTest(turnArea, obstacles);

        // A turn keeps out of the room round every Go around obstacle too (ClearsTurnObstacles
        // holds the path to it), so the generator seats its arcs and slides them to the turn
        // line by that test as well: at a U-turn obstacle just past a tree the pass goes round,
        // the arc then stops before the tree, where it was seated inside the bend and slid along
        // the bend's heading, away from the pass.
        var keepOut = boundary != null ? RoundObstacles(boundary, GoAroundRoom(config)) : EmptyRound;
        Func<Vec3, bool>? blockedForTurn = keepOut.Count == 0 ? null : p =>
        {
            if (isPointInsideTurnArea(p) != 0) return true;
            foreach (var r in keepOut)
                if (r.Clear.Contains(p.Easting, p.Northing)) return true;
            return false;
        };

        // The strip a pass may be driven across with the sections off instead of turned at
        // (GuidanceConfig.UTurnCrossGap): inside the field, outside the headland line and outside
        // every obstacle that is not driven through. Built once per plan, asked per sample.
        var headlandArea = PolygonSlabIndex.Build(headlandLine);
        var stripObstacles = boundary.InnerBoundaries.Where(b => b.IsValid && !b.IsDriveThrough).ToList();
        var outerBoundary = boundary.OuterBoundary;
        Func<Vec3, bool> isPointInHeadlandStrip = point =>
            outerBoundary.IsPointInside(point.Easting, point.Northing)
            && !(headlandArea?.Contains(point.Easting, point.Northing) ?? false)
            && stripObstacles.All(b => !b.IsPointInside(point.Easting, point.Northing));

        var pivotPosition = new Vec3(currentPosition.Easting, currentPosition.Northing, headingRadians);
        // What the turn is planned for: the pivot, or the front implement's centre ahead of it
        // (FrontToolPlan). The end of the pass is found from the pivot either way, as the state
        // machine finds it.
        var planPosition = plan.On ? RigGeometry.AlongTractor(pivotPosition, headingRadians, plan.Reach) : pivotPosition;

        // ONE turn generator. "An AB line is just a curve with 2 points": a 2-point AB
        // line is densified into a straight poly-curve so it flows through the SAME
        // curve-aware generator as recorded curves — there is no separate analytic AB path.
        //
        // CRITICAL: offset FIRST, then extend the OFFSET RESULT. The in-field portion of the
        // U-turn's current pass must be byte-for-byte identical to the guidance line the
        // tractor steers to (GpsPipelineService offsets the NON-extended track), so the
        // track→turn handoff has zero lateral step. Extending the base BEFORE offsetting
        // shifts the in-field offset points by up to ~1 m (different point set → different
        // pruning/spacing) — that step is what made the tractor "get lost" entering the turn.
        // The tangent extension is added on TOP only so FindCurveTurnPoint can see the curve
        // cross the boundary (a bare offset curve stops at the field edge → generator fails →
        // straight fallback). The offset pass (bent round its Go around obstacles) is built
        // above, before the turn lines; it runs on only past the ends the offset kept (#694).
        List<Vec3> currentCurvePoints = CurveProcessing.ExtendPassForTurn((offsetCurve, pass.PercentRemoved, pass.StartKept, pass.EndKept, pass.StartIsEnd, pass.EndIsEnd));

        int currentLocationIndex = PassEnds.NearestIndex(currentCurvePoints, pivotPosition.Easting, pivotPosition.Northing);
        // The end of the pass, found the one way (PassEnds): the turn builder starts its arc from
        // this point, and the state machine measured its distance to the turn to the same end.
        // A stretch outside the turn line that stays inside the fence is driven through, but never
        // one into the turn area of a U-turn obstacle (in the field or in the headland): a point
        // there is an end, as at the fence, and the turn is made before the obstacle, as the gap
        // crossing (GapCrossingEnd) never crosses one. Fork only (the obstacles' turn lines are).
        var obstacleAreas = obstacles;
        Func<Vec3, bool> isPointDrivenThrough = p => !obstacleAreas.Exists(o => o.Area.Contains(p.Easting, p.Northing))
            && boundary.OuterBoundary.IsPointInside(p.Easting, p.Northing);
        var passEnd = PassEnds.Find(currentCurvePoints, currentLocationIndex, guidance.IsHeadingSameWay,
            p => isPointInsideTurnArea(p) == 0, turnBoundaryVec3,
            isPointDrivenThrough,
            m => _logger.LogDebug("[YouTurn] Pass {Path}: {What}", guidance.HowManyPathsAway, m));
        if (passEnd is { } end)
            _logger.LogDebug("[YouTurn] End of pass {Path} ahead at ({E:F1},{N:F1}), point {Idx} of {Count}",
                guidance.HowManyPathsAway, end.Crossing.Easting, end.Crossing.Northing, end.OutsideIndex, currentCurvePoints.Count);
        else
            _logger.LogDebug("[YouTurn] Pass {Path} does not leave the turn area ahead of the tractor", guidance.HowManyPathsAway);

        var input = new YouTurnCreationInput
        {
            TurnType = MapTurnStyle(config.Guidance.UTurnStyle),
            IsTurnLeft = turnLeft,
            // Always the unified curve generator (AB lines are densified 2-point curves).
            GuidanceType = GuidanceLineType.Curve,
            BoundaryTurnLines = boundaryTurnLines,
            IsPointInsideTurnArea = isPointInsideTurnArea,
            IsPointBlockedForTurn = blockedForTurn,
            MaxGapCrossing = config.Guidance.UTurnCrossGap,
            IsPointInHeadlandStrip = isPointInHeadlandStrip,
            IsPointDrivenThrough = isPointDrivenThrough,

            // The single generator walks these points (the current offset pass).
            GuidancePoints = currentCurvePoints,
            CurrentLocationIndex = currentLocationIndex,
            // Where the pass leaves the turn area ahead: the end the turn is made at. A try with
            // the turn lines moved in gets the first try's end instead (CreateTurnPath).
            TurnLineEndIndex = passEnd?.OutsideIndex,
            // Non-extended base; the destination (exit) curve is offset+extended from this in
            // BuildNewOffsetCurveList so its in-field portion matches the next guidance line,
            // bent round the Go around obstacles as that line is.
            ReferenceCurvePoints = basePoints,
            BendPass = (distAway, pass) => _goAround.Bent(track, distAway, pass, boundary),
            StraightGuidancePoints = ReferenceEquals(offsetCurve, pass.Points) ? null
                : CurveProcessing.ExtendPassForTurn((pass.Points, pass.PercentRemoved, pass.StartKept, pass.EndKept, pass.StartIsEnd, pass.EndIsEnd)),

            // Diagnostics; the curve generator anchors on GuidancePoints, not these.
            ABHeading = passEnd?.Heading ?? abHeading,
            ABReferencePoint = passEnd?.Crossing ?? new Vec2(pivotPosition.Easting, pivotPosition.Northing),
            IsHeadingSameWay = guidance.IsHeadingSameWay,

            PivotPosition = planPosition,
            ToolWidth = toolWidth,
            ToolOverlap = config.Tool.Overlap,
            ToolOffset = config.Tool.Offset,
            TurnRadius = plan.On ? plan.Radius : config.Guidance.UTurnRadius,

            // Matches the cyan next-track line exactly.
            TurnOffset = turn.NextTrackTurnOffset,
            RowSkipsWidth = uTurnSkipRows,
            TurnStartOffset = 0,
            HowManyPathsAway = guidance.HowManyPathsAway,
            // Carry the nudge so the destination offset curve (exit leg) lands on the
            // nudged next pass, matching the current pass built above (and AgOpen, whose
            // BuildNewOffsetList distAway includes track.nudgeDistance).
            NudgeDistance = guidance.NudgeOffset,
            PassEdgeOffset = Models.Track.PassGeometry.EdgeOffset(track, toolWidth - config.Tool.Overlap),
            TrackMode = 0,

            MakeUTurnCounter = turn.YouTurnCounter + 10,

            LegLength = config.Guidance.UTurnExtension,
            MinExitLegLength = plan.On ? plan.MinExitLeg : 0,
            YouTurnLegExtensionMultiplier = 2.5,
            HeadlandWidth = headlandWidthForTurn,
        };

        _logger.LogDebug("[YouTurn] Input built: toolWidth={W:F1}m, totalHeadland={TH:F1}m, headlandWidthForTurn={HWT:F1}m, turnBoundaryPts={TB}, headlandPts={HP}",
            toolWidth, totalHeadlandWidth, headlandWidthForTurn, turnBoundaryVec3.Count, headlandBoundaryVec3.Count);

        return input;
    }

    /// <summary>
    /// Densifies a 2-point AB line into an evenly-spaced poly-curve (heading = A→B) so it
    /// can flow through the unified curve turn generator. Without dense points,
    /// FindCurveTurnPoint (which tests boundary crossing per point) could step over the
    /// turn boundary in one long segment.
    /// </summary>
    /// <summary>
    /// An AB line's A and B moved out along the line to past the whole field: the line is
    /// endless (guidance steers to it anywhere), but its two points can sit anywhere in the
    /// field. An A+ or Lat/Lon line has B 100 m from A, and the turn generator follows the
    /// densified points plus 200 m of extension, so in a field longer than that the pass never
    /// reached the turn line at the far end: no turn could be built there, and after #698 not
    /// even the fallback (the olive grove in the field job harness: every turn at its south end).
    /// The line itself is unchanged: same heading, same points on it.
    /// </summary>
    internal static IReadOnlyList<Vec3> AbLineAcrossField<T>(IReadOnlyList<Vec3> ab, IReadOnlyList<T> outer) where T : IPlanarPoint
    {
        if (ab.Count != 2 || outer.Count < 3) return ab;
        var a = ab[0];
        var b = ab[1];
        double dE = b.Easting - a.Easting, dN = b.Northing - a.Northing;
        double len = Math.Sqrt(dE * dE + dN * dN);
        if (len < 0.01) return ab;
        double uE = dE / len, uN = dN / len;
        // The field's extent along the line, from A.
        double tMin = 0, tMax = len;
        foreach (var p in outer)
        {
            double t = (p.Easting - a.Easting) * uE + (p.Northing - a.Northing) * uN;
            if (t < tMin) tMin = t;
            if (t > tMax) tMax = t;
        }
        const double Margin = 10.0;
        tMin -= Margin;
        tMax += Margin;
        return new List<Vec3>
        {
            new(a.Easting + uE * tMin, a.Northing + uN * tMin, a.Heading),
            new(a.Easting + uE * tMax, a.Northing + uN * tMax, b.Heading),
        };
    }

    private static List<Vec3> DensifyAbLine(IReadOnlyList<Vec3> ab)
    {
        var a = ab[0];
        var b = ab[1];
        double dE = b.Easting - a.Easting;
        double dN = b.Northing - a.Northing;
        double len = Math.Sqrt(dE * dE + dN * dN);
        if (len < 0.01)
            return new List<Vec3> { a, b };

        double head = Math.Atan2(dE, dN);
        if (head < 0) head += Math.PI * 2;

        int n = Math.Max(2, (int)(len / 2.0)); // ~2 m spacing
        var pts = new List<Vec3>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            double t = (double)i / n;
            pts.Add(new Vec3(a.Easting + dE * t, a.Northing + dN * t, head));
        }
        return pts;
    }

    // ── The pass and its end ─────────────────────────────────────────────

    /// <summary>
    /// The pass the turn is planned on, as the turn generator walks it: the track (an AB line
    /// first run across the whole field, <see cref="AbLineAcrossField{T}"/>, and densified: "an AB
    /// line is just a curve with 2 points") offset by <paramref name="offsetDistance"/> and then
    /// extended past its ends (<see cref="CurveProcessing.ExtendPassForTurn"/>). Offset first, then
    /// extend the offset result: the in-field part must be the guidance line the tractor steers to
    /// (the pipeline offsets the non-extended track), so the track→turn handoff has no lateral
    /// step; the extension is on top, so the pass is seen to cross the turn line. The U-turn's own
    /// geometry: no turn-radius limit, a loop not joined at its seam. The base points (not
    /// extended) come back too: the exit pass is offset and extended from them. The pass has no
    /// points when the offset removed all of it (#694).
    /// </summary>
    internal static (List<Vec3> Base, List<Vec3> Pass) PassForTurn<T>(Models.Track.Track track, IReadOnlyList<T> outer, double offsetDistance)
        where T : IPlanarPoint
    {
        List<Vec3> basePoints = track.Points.Count > 2
            ? new List<Vec3>(track.Points)
            : DensifyAbLine(AbLineAcrossField(track.Points, outer));
        var offsetCurve = CurveProcessing.CurvePass(basePoints, offsetDistance);
        if (offsetCurve.Points.Count < 2) return (basePoints, new List<Vec3>());
        return (basePoints, CurveProcessing.ExtendPassForTurn(offsetCurve));
    }

    /// <summary>
    /// The end of the track's pass <paramref name="offsetDistance"/> over, ahead of
    /// <paramref name="vehiclePos"/> (<see cref="PassEnds.Find"/> on <see cref="PassForTurn{T}"/>,
    /// the pass run across the turn line's extent). Null past the last crossing.
    /// </summary>
    internal static PassEnd? PassEndAhead(Models.Track.Track track, Vec3 vehiclePos, IReadOnlyList<Vec3> turnLine,
        bool headingSameWay, double offsetDistance = 0, IReadOnlyList<Vec3>? fence = null)
    {
        if (turnLine.Count < 3 || track.Points.Count < 2) return null;
        var (_, pass) = PassForTurn(track, fence ?? turnLine, offsetDistance);
        if (pass.Count < 2) return null;
        int near = PassEnds.NearestIndex(pass, vehiclePos.Easting, vehiclePos.Northing);
        return PassEnds.Find(pass, near, headingSameWay, p => GeometryMath.IsPointInPolygon(turnLine, p.Easting, p.Northing), turnLine,
            fence == null ? null : p => GeometryMath.IsPointInPolygon(fence, p.Easting, p.Northing));
    }

    // ── Simple geometric fallback ───────────────────────────────────────

    /// <summary>
    /// The fallback builds the whole turn from one anchor point. Using the tractor's raw
    /// position carried its cross-track error into the path — a turn created right after
    /// engaging, before the tractor had converged, came out shifted by that error (#174:
    /// 2 m). Move the anchor sideways onto the pass being followed (the offset
    /// <c>PassGeometry.Offset</c>, positive right of the AB heading, as
    /// guidance uses). AB lines only: the fallback's geometry is straight along abHeading.
    /// </summary>
    internal Position OntoPassLine(Position pos, Models.Track.Track? track, double abHeading, GuidanceWorkingState guidance)
    {
        if (track == null || track.Points.Count != 2) return pos;
        var a = track.Points[0];
        double width = _configStore.ActualToolWidth - _configStore.Tool.Overlap;
        double passOffset = Models.Track.PassGeometry.Offset(track, guidance.HowManyPathsAway, width, guidance.NudgeOffset,
            Models.Track.PassGeometry.ToolShift(_configStore.Tool.Offset, guidance.IsHeadingSameWay));
        double rightE = Math.Cos(abHeading), rightN = -Math.Sin(abHeading);
        double perp = (pos.Easting - a.Easting) * rightE + (pos.Northing - a.Northing) * rightN;
        double shift = passOffset - perp;
        return pos with { Easting = pos.Easting + shift * rightE, Northing = pos.Northing + shift * rightN };
    }

    /// <summary>
    /// Straight-in, semicircle, straight-out U-turn. Used when the primary Dubins-based creation
    /// returns a spiral or fails entirely.
    /// </summary>
    internal List<Vec3> SimpleFallback(
        Position currentPosition,
        double abHeading,
        bool turnLeft,
        Boundary? boundary,
        GuidanceWorkingState guidance,
        YouTurnWorkingState turn,
        int uTurnSkipRows,
        double headlandDistance,
        Models.Track.Track? selectedTrack = null)
    {
        var path = new List<Vec3>();
        var config = _configStore;

        currentPosition = OntoPassLine(currentPosition, selectedTrack, abHeading, guidance);

        const double pointSpacing = 0.5;
        // Signed in the turn direction: negative, the arc goes round the other way.
        double turnOffset = Math.Abs(turn.NextTrackTurnOffset);
        if (turn.NextTrackTurnOffset < 0) turnLeft = !turnLeft;

        if (turnOffset < 0.1)
        {
            double trackWidth = config.ActualToolWidth - config.Tool.Overlap;
            turnOffset = trackWidth * (uTurnSkipRows + 1);
            _logger.LogDebug("[YouTurn] Using fallback turnOffset calculation: {Off:F2}m", turnOffset);
        }

        double turnRadius = config.Guidance.UTurnRadius;
        double geometricMinRadius = turnOffset / 2.0;
        if (turnRadius < geometricMinRadius) turnRadius = geometricMinRadius;
        const double minTurnRadius = 4.0;
        if (turnRadius < minTurnRadius) turnRadius = minTurnRadius;

        double travelHeading = abHeading;
        if (!guidance.IsHeadingSameWay)
        {
            travelHeading += Math.PI;
            if (travelHeading >= Math.PI * 2) travelHeading -= Math.PI * 2;
        }

        double exitHeading = travelHeading + Math.PI;
        if (exitHeading >= Math.PI * 2) exitHeading -= Math.PI * 2;

        double perpAngle = turnLeft ? (travelHeading - Math.PI / 2) : (travelHeading + Math.PI / 2);

        double distToHeadland = turn.DistanceToHeadland;
        double headlandBoundaryEasting = currentPosition.Easting + Math.Sin(travelHeading) * distToHeadland;
        double headlandBoundaryNorthing = currentPosition.Northing + Math.Cos(travelHeading) * distToHeadland;

        double distanceFromBoundary = config.Guidance.UTurnDistanceFromBoundary;

        // #289 F4: Compute the actual distance from the headland crossing to the outer
        // boundary along the travel heading, instead of assuming headlandDistance (the
        // scalar headland-zone width). On non-rectangular fields the available forward
        // room varies along the headland — near a corner it's tighter than the zone
        // width, along a long straight edge it's closer to the zone width. The previous
        // formula `headlandLegLength = headlandDistance - turnRadius - distanceFromBoundary`
        // produced a tight arc when used as a fallback for a turn that the Dubins service
        // would have placed deeper. Using the actual ray distance makes the fallback's
        // apex placement match the service's intent regardless of field shape.
        double rayToOuter = double.MaxValue;
        if (boundary?.OuterBoundary != null && boundary.OuterBoundary.IsValid)
        {
            rayToOuter = RaycastDistanceToPolygon(
                headlandBoundaryEasting, headlandBoundaryNorthing,
                travelHeading, boundary.OuterBoundary.Points);
        }
        double availableHeadlandSpan = rayToOuter < double.MaxValue
            ? rayToOuter
            : headlandDistance; // fallback to legacy scalar when raycast fails

        double headlandLegLength = availableHeadlandSpan - turnRadius - distanceFromBoundary;
        if (headlandLegLength < 0) headlandLegLength = 0;
        double fieldLegLength = config.Guidance.UTurnExtension;

        _logger.LogDebug("[YouTurn] Simple path: turnOffset={Off:F1}m, turnRadius={Rad:F1}m", turnOffset, turnRadius);
        _logger.LogDebug("[YouTurn] headlandDist(scalar)={HD:F1}m rayToOuter={RO:F1}m, headlandLegLength={HLL:F1}m",
            headlandDistance, rayToOuter, headlandLegLength);

        // The whole arc and the leg back from it are checked against the fence, not only the
        // ray ahead and the exit end: on a slanted boundary the arc's far side crossed it (the
        // tractor 12 m out of a field with a 21 m cultivator, 11 m with an 8 m sprayer). The
        // arc is seated further back along the pass until the path keeps the set distance from
        // the fence: back from where the ray put it (143 m past the headland crossing along an
        // edge the ray ran beside), to the crossing, and up to 30 m into the field past it when
        // the headland has no room at all; further in there is no turn. The exit leg's end is
        // part of the check (it used to be the only point checked, where the ray put it). An
        // arc seated to start behind the tractor is no turn either: the tractor would join it
        // mid-way and cut across.
        var outerPolygon = boundary?.OuterBoundary != null && boundary.OuterBoundary.IsValid
            ? boundary.OuterBoundary.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList()
            : null;
        const double SetbackStep = 0.5;
        const double MaxIntoField = 30.0;
        const double MinLeg = 2.0;
        const double MinArcAhead = 2.0;
        double maxSetback = Math.Min(headlandLegLength + MaxIntoField, distToHeadland + headlandLegLength - MinArcAhead);
        // Seated back only on a straight pass. The path runs straight from the tractor along the
        // pass's heading where the tractor is; on a curve that line leaves the pass the further
        // ahead the turn is. Seated to fit while the tractor was still far off (190 m on a
        // boundary curve's inner pass once U-turns turn at the turn line, official #816), the
        // turn was never joined: the tractor passed beside its start and the turn was made "from
        // here", onto a pass whose line had already ended. On a curve the fallback is the
        // official one: built where the ray puts it, used only if it stays in the field (below),
        // and built again as the tractor comes closer.
        bool seatBack = selectedTrack == null || selectedTrack.Points.Count <= 2;
        double setback = 0;
        while (true)
        {
            path.Clear();
            int arcFrom = BuildSimplePath(path, setback);
            // Smoothed before it is checked, so the path checked is the path driven: smoothing
            // pulls the arc in towards its centre (0.2 m at the default 14 passes on an 8 m
            // radius, 0.4 m on a 4 m one, more with more passes), which is nearer a fence that
            // lies inside the bend (the tip of a bay between the two passes). It moves no point
            // along the list, so the arc still starts at arcFrom.
            TurnPathSmoothing.Smooth(path, config.Guidance.UTurnSmoothing);
            if (outerPolygon == null || !seatBack) break;

            var clearance = TurnClearance.Evaluate(path.Skip(arcFrom), outerPolygon, TurnClearance.KeepSide.Inside, distanceFromBoundary);
            if (clearance.IsClear)
            {
                if (setback > 0)
                    _logger.LogDebug("[YouTurn] Simple path seated {S:F1}m further back to keep {M:F1}m from the boundary", setback, distanceFromBoundary);
                break;
            }
            double next = setback + Math.Max(SetbackStep, clearance.MaxIntrusion);
            // The last step stops at the limit itself: a turn that fits only with its arc seated
            // within that step of the limit was refused untried.
            if (next > maxSetback && setback < maxSetback - 1e-9) next = maxSetback;
            if (next > maxSetback)
            {
                _logger.LogWarning("[YouTurn] No simple turn seated up to {S:F0}m back (up to {F:F0}m into the field, starting ahead of the tractor) keeps the tractor {M:F1}m inside the boundary (intrusion {I:F1}m); no turn",
                    maxSetback, MaxIntoField, distanceFromBoundary, clearance.MaxIntrusion);
                path.Clear();
                return path;
            }
            setback = next;
        }

        _logger.LogDebug("[YouTurn] Simple fallback path has {Count} points", path.Count);

        // A last check, after the seating above (which keeps clear of the outer boundary only):
        // no point outside the field or in an obstacle a turn may not drive through (#692).
        // AgOpenGPS has no fallback; it makes no turn and sounds the failure.
        if (LeavesField(path, boundary))
        {
            _logger.LogDebug("[YouTurn] Simple fallback leaves the field - not creating U-turn");
            path.Clear();
        }

        return path;

        // The path with its arc seated `back` m before where the ray put it: the entry leg, the
        // arc, the connector back to the exit leg's start, the exit leg. Seated back past the
        // entry start, the legs keep MinLeg m of straight. Returns the index of the first arc point.
        int BuildSimplePath(List<Vec3> into, double back)
        {
            double legLength = headlandLegLength - back;   // negative: the arc before the headland crossing
            double arcStartE = headlandBoundaryEasting + Math.Sin(travelHeading) * legLength;
            double arcStartN = headlandBoundaryNorthing + Math.Cos(travelHeading) * legLength;
            double totalEntryLength = Math.Max(fieldLegLength + legLength, MinLeg);
            double startE = arcStartE - Math.Sin(travelHeading) * totalEntryLength;
            double startN = arcStartN - Math.Cos(travelHeading) * totalEntryLength;
            int totalEntryPoints = (int)(totalEntryLength / pointSpacing);

            for (int i = 0; i <= totalEntryPoints; i++)
            {
                double dist = i * pointSpacing;
                into.Add(new Vec3
                {
                    Easting = startE + Math.Sin(travelHeading) * dist,
                    Northing = startN + Math.Cos(travelHeading) * dist,
                    Heading = travelHeading,
                });
            }
            int arcIndex = into.Count;

            double arcCenterE = arcStartE + Math.Sin(perpAngle) * turnRadius;
            double arcCenterN = arcStartN + Math.Cos(perpAngle) * turnRadius;

            int arcPoints = Math.Max((int)(Math.PI * turnRadius / pointSpacing), 20);
            double startAngle = Math.Atan2(arcStartE - arcCenterE, arcStartN - arcCenterN);

            for (int i = 1; i <= arcPoints; i++)
            {
                double t = (double)i / arcPoints;
                double sweepAngle = turnLeft ? (-Math.PI * t) : (Math.PI * t);
                double currentAngle = startAngle + sweepAngle;

                double tangentHeading = currentAngle + (turnLeft ? -Math.PI / 2 : Math.PI / 2);
                if (tangentHeading < 0) tangentHeading += Math.PI * 2;
                if (tangentHeading >= Math.PI * 2) tangentHeading -= Math.PI * 2;

                into.Add(new Vec3
                {
                    Easting = arcCenterE + Math.Sin(currentAngle) * turnRadius,
                    Northing = arcCenterN + Math.Cos(currentAngle) * turnRadius,
                    Heading = tangentHeading,
                });
            }

            // Connect the arc exit to the start of the outbound straight leg, then build the leg itself.
            var lastArcPoint = into[into.Count - 1];
            double actualArcEndE = lastArcPoint.Easting;
            double actualArcEndN = lastArcPoint.Northing;

            double exitStartE = arcStartE + Math.Sin(perpAngle) * turnOffset;
            double exitStartN = arcStartN + Math.Cos(perpAngle) * turnOffset;

            double arcToExitDist = Math.Sqrt(
                (exitStartE - actualArcEndE) * (exitStartE - actualArcEndE) +
                (exitStartN - actualArcEndN) * (exitStartN - actualArcEndN));
            if (arcToExitDist > pointSpacing)
            {
                int connectPoints = (int)(arcToExitDist / pointSpacing);
                for (int i = 1; i <= connectPoints; i++)
                {
                    double t = (double)i / (connectPoints + 1);
                    into.Add(new Vec3
                    {
                        Easting = actualArcEndE + (exitStartE - actualArcEndE) * t,
                        Northing = actualArcEndN + (exitStartN - actualArcEndN) * t,
                        Heading = exitHeading,
                    });
                }
            }

            int totalExitPoints = (int)(totalEntryLength / pointSpacing);
            for (int i = 1; i <= totalExitPoints; i++)
            {
                double dist = i * pointSpacing;
                into.Add(new Vec3
                {
                    Easting = exitStartE + Math.Sin(exitHeading) * dist,
                    Northing = exitStartN + Math.Cos(exitHeading) * dist,
                    Heading = exitHeading,
                });
            }
            return arcIndex;
        }
    }

    /// <summary>
    /// True when a point of <paramref name="path"/> is outside the outer boundary or inside an
    /// inner boundary a turn may not drive through.
    /// </summary>
    internal static bool LeavesField(IReadOnlyList<Vec3> path, Boundary? boundary)
    {
        var outer = boundary?.OuterBoundary;
        if (outer == null || !outer.IsValid) return false;
        foreach (var p in path)
        {
            if (!outer.IsPointInside(p.Easting, p.Northing)) return true;
            foreach (var inner in boundary!.InnerBoundaries)
                if (!inner.IsDriveThrough && inner.IsValid && inner.IsPointInside(p.Easting, p.Northing)) return true;
        }
        return false;
    }

    /// <summary>
    /// Build an immediate U-turn path from the tractor's current position to the target
    /// pass — no headland traversal, no boundary-anchored entry/exit legs (#260). Like
    /// AgOpenGPS <c>BuildManualYouTurn</c>: a Dubins path at the configured
    /// <c>Guidance.UTurnRadius</c> from a start 4 m ahead of the tractor along the track to
    /// the point abreast of it on the target pass, heading reversed. With a radius larger
    /// than half the pass offset that is an omega (bulb) turn; smaller, an arc-straight-arc.
    ///
    /// The radius used to be fixed at offset / 2 so a plain semicircle landed on the next
    /// pass, ignoring the configured radius. With a 6 m tool that is a 3 m radius, which no
    /// tractor can drive: it fell more than 4 m outside the arc and the turn was abandoned
    /// halfway, heading square to the next pass (#156).
    /// </summary>
    public List<Vec3> CreateManualArcPath(
        Position currentPosition,
        double abHeading,
        bool turnLeft,
        Boundary? boundary,
        GuidanceWorkingState guidance,
        int uTurnSkipRows,
        Models.Track.Track? targetPass = null,
        bool startedByTheApp = false) =>
        CreateManualArcPath(currentPosition, abHeading, turnLeft, boundary, guidance, uTurnSkipRows,
            targetPass, out _, startedByTheApp);

    /// <param name="obstacleInTheWay">True when the turn was refused because it would cross an
    /// obstacle chosen as U-turn.</param>
    /// <param name="startedByTheApp">True for a turn the app starts by itself from where the tractor
    /// is (a planned arc already behind it): the implement keeps the whole U-turn distance from a
    /// hard boundary. A turn the operator asks for is judged from where the implement is at the
    /// press (<see cref="ManualTurnClearsHardBoundary"/>).</param>
    public List<Vec3> CreateManualArcPath(
        Position currentPosition,
        double abHeading,
        bool turnLeft,
        Boundary? boundary,
        GuidanceWorkingState guidance,
        int uTurnSkipRows,
        Models.Track.Track? targetPass,
        out bool obstacleInTheWay,
        bool startedByTheApp = false)
    {
        // For a front implement's centre when the operator chose that (FrontToolPlan); where no
        // such turn is made from here, for the pivot, as without the choice. Fork only.
        var plan = FrontToolPlanFor(_configStore);
        if (plan.On)
        {
            var forTool = ManualArcPathFor(plan, currentPosition, abHeading, turnLeft, boundary, guidance,
                uTurnSkipRows, targetPass, out _, startedByTheApp);
            if (forTool.Count > 0)
            {
                obstacleInTheWay = false;
                return forTool;
            }
            _logger.LogDebug("[ManualYouTurn] No turn planned for the front implement's centre from here; planning for the pivot");
        }
        return ManualArcPathFor(default, currentPosition, abHeading, turnLeft, boundary, guidance,
            uTurnSkipRows, targetPass, out obstacleInTheWay, startedByTheApp);
    }

    /// <summary>
    /// <see cref="CreateManualArcPath(Position, double, bool, Boundary?, GuidanceWorkingState, int, Models.Track.Track?, out bool, bool)"/>
    /// for the pivot, or for a front implement's centre (<paramref name="plan"/>): the Dubins path
    /// is then the implement's, at the radius of its circle, from where the tractor's would start
    /// (or half a metre ahead of the implement, if that is further on) to the next pass, and on
    /// along that pass while the tractor comes onto it; the path returned is the pivot's
    /// (<see cref="PivotPathFor"/>), which every check below reads.
    /// </summary>
    private List<Vec3> ManualArcPathFor(
        FrontToolPlan plan,
        Position currentPosition,
        double abHeading,
        bool turnLeft,
        Boundary? boundary,
        GuidanceWorkingState guidance,
        int uTurnSkipRows,
        Models.Track.Track? targetPass,
        out bool obstacleInTheWay,
        bool startedByTheApp)
    {
        obstacleInTheWay = false;
        var path = new List<Vec3>();
        var config = _configStore;
        // A turn not made for the implement is planned for the pivot next: its refusal is no warning.
        var refused = plan.On ? LogLevel.Debug : LogLevel.Warning;

        double trackWidth = config.ActualToolWidth - config.Tool.Overlap;
        // From the line steered now to the one steered on the next pass: the passes apart, and
        // with a tool offset twice the offset more or less, since the next pass is driven the
        // other way (AgOpenGPS BuildManualYouTurn: rowSkipsWidth ± tool.offset * 2).
        int side = (turnLeft ^ guidance.IsHeadingSameWay) ? 1 : -1;
        double toolShift = Models.Track.PassGeometry.ToolShift(config.Tool.Offset, guidance.IsHeadingSameWay);
        // A tool offset over half the pass makes it negative turning away from the tool: the line
        // steered on the next pass is on the other side of the tractor, and the turn loops back
        // across to it, as in AgOpenGPS (J8: a 3 m mower 2.5 m to one side).
        double nearestPassOffset = trackWidth + 2 * toolShift * side;
        double turnOffset = trackWidth * uTurnSkipRows + nearestPassOffset;
        if (trackWidth < 0.1)
        {
            _logger.LogWarning("[ManualYouTurn] Tool width is zero — cannot compute offset");
            return path;
        }

        double turnRadius = plan.On ? plan.Radius : Math.Max(config.Guidance.UTurnRadius, MinManualTurnRadius);
        double reach = plan.On ? plan.Reach : 0.0;
        double startLead = plan.On ? Math.Max(ManualTurnStartLead, reach + ManualToolLead) : ManualTurnStartLead;

        // Travel direction as the tractor is currently heading along the track.
        double lineHeading = abHeading;
        if (!guidance.IsHeadingSameWay) lineHeading += Math.PI;
        lineHeading = NormalizeHeading(lineHeading);

        // The start heading is rotated by 0.01 rad like AgOpenGPS, so start and goal are
        // never exactly antiparallel: Dubins degenerates into loops when the two turning
        // circles coincide (offset = 2R).
        double travelHeading = NormalizeHeading(lineHeading - ManualTurnHeadingTweak);

        // The curve starts ahead of the tractor: the steering needs the lead to reach lock,
        // and the arc must not begin behind the pivot by the time it renders (AgOpenGPS: 4 m).
        var start = new Vec3
        {
            Easting = currentPosition.Easting + Math.Sin(lineHeading) * startLead,
            Northing = currentPosition.Northing + Math.Cos(lineHeading) * startLead,
            Heading = travelHeading,
        };

        // The goal is on the target pass abreast of the start, so the turn ends on the pass
        // even when the tractor is off its own line at the press (AgOpenGPS anchors on the
        // line, not the tractor). Seen on #156: 0.9 m off at the press, the turn ended 0.9 m
        // beside the next pass. The planned pass decides where the turn ends: a skip-worked,
        // snake or alternative plan can be another number of passes over than the skip, and
        // the turn completes onto it. Without a usable target pass, go one offset over.
        double perpAngle = turnLeft ? (lineHeading - Math.PI / 2) : (lineHeading + Math.PI / 2);
        var goal = new Vec3
        {
            Easting = start.Easting + Math.Sin(perpAngle) * turnOffset,
            Northing = start.Northing + Math.Cos(perpAngle) * turnOffset,
            Heading = NormalizeHeading(lineHeading + Math.PI),
        };
        if (TryProjectOntoPass(targetPass, start, out double passE, out double passN))
        {
            double across = (passE - start.Easting) * Math.Sin(perpAngle) + (passN - start.Northing) * Math.Cos(perpAngle);
            // Only a pass on the turn side: at least the nearest pass there, less half a pass (with
            // no tool offset, half a pass over; the pass the tractor is on never qualifies).
            if (across >= nearestPassOffset - trackWidth / 2)
            {
                goal.Easting = passE;
                goal.Northing = passN;
            }
        }

        // Refuse to plot a turn whose endpoint lands outside the field.
        if (boundary?.OuterBoundary != null && boundary.OuterBoundary.IsValid
            && !boundary.OuterBoundary.IsPointInside(goal.Easting, goal.Northing))
        {
            _logger.LogDebug("[ManualYouTurn] Turn end ({E:F1},{N:F1}) outside boundary — refusing",
                goal.Easting, goal.Northing);
            return path;
        }

        _dubinsService.TurningRadius = turnRadius;
        path = _dubinsService.GeneratePath(start, goal);
        if (path.Count < 3)
        {
            _logger.Log(refused, "[ManualYouTurn] Dubins found no path: radius={Rad:F1}m, offset={Off:F1}m",
                turnRadius, turnOffset);
            path.Clear();
            return path;
        }

        // The path begins at the tractor with a straight lead into the Dubins start, so
        // guidance has its t=0 sample at the pivot: it abandons a turn whose nearest path
        // point is over 4 m away, which the lead alone would be the moment it renders.
        // (Planned for the implement: at the implement, which puts the pivot's path at the tractor.)
        path.Insert(0, new Vec3
        {
            Easting = currentPosition.Easting + Math.Sin(lineHeading) * reach,
            Northing = currentPosition.Northing + Math.Cos(lineHeading) * reach,
            Heading = lineHeading,
        });
        // The implement is on the next pass at the goal, the tractor still at an angle to it:
        // on along the pass until the tractor is on it too.
        if (plan.On) RunOnAlongPass(path, targetPass, plan.SettleLeg);

        TurnPathSmoothing.Smooth(path, config.Guidance.UTurnSmoothing);

        if (plan.On)
        {
            var inField = boundary?.OuterBoundary;
            if (inField != null && inField.IsValid && inField.IsPointInside(currentPosition.Easting, currentPosition.Northing)
                && LeavesField(path, boundary))
            {
                _logger.LogDebug("[ManualYouTurn] The front implement's centre would leave the field");
                path.Clear();
                return path;
            }
            var pivotPath = PivotPathFor(path, plan);
            if (pivotPath == null)
            {
                path.Clear();
                return path;
            }
            // As the automatic turn: the turn at the other end of the next pass must still have
            // its room (FrontToolPlan.NextTurnRoom), here from the fence and the obstacles a turn
            // may not drive through, the turn line being the U-turn distance inside them.
            if (inField != null && inField.IsValid)
            {
                double room = plan.NextTurnRoom + Math.Max(0.0, config.Guidance.UTurnDistanceFromBoundary);
                double passLeft = PassLeftAfter(pivotPath, targetPass?.Points,
                    p => inField.IsPointInside(p.Easting, p.Northing)
                        && !boundary!.InnerBoundaries.Any(b => b.IsValid && !b.IsDriveThrough && b.IsPointInside(p.Easting, p.Northing)),
                    room);
                if (passLeft < room)
                {
                    _logger.LogDebug("[ManualYouTurn] Planned for the front implement, the turn would end with {L:F1} m of the next pass left before the fence or an obstacle ({N:F1} m needed for the turn at its other end)",
                        passLeft, room);
                    path.Clear();
                    return path;
                }
            }
            path = pivotPath;
        }

        // Hard boundary: a manual turn is anchored at the tractor, so it can't be
        // shifted inward — if the implement would swing into the fence, refuse it.
        double intrusion;
        bool clear = startedByTheApp
            ? ImplementClearsHardBoundary(path, boundary, config, out intrusion)
            : ManualTurnClearsHardBoundary(path, boundary, config, out intrusion);
        if (!clear)
        {
            _logger.Log(refused, "[ManualYouTurn] Implement would swing into hard boundary (intrusion {I:F2}m) — refusing",
                intrusion);
            path.Clear();
            return path;
        }

        // Obstacles chosen as U-turn: a manual turn is anchored at the tractor, so one that
        // crosses an obstacle's turn line, or swings the implement into a hard obstacle, is
        // refused, as the automatic turn keeps out of them. Its exit too, as the automatic
        // turn's (ExitReach): one that lands just before an obstacle ends in the AutoSteer-off
        // alarm once the turn is driven, with no room left to stop (official #613, Sourcery).
        if (boundary != null && !ClearsTurnObstacles(path, boundary, config, out int inner,
                ExitReachFor(plan, plan.SettleLeg - plan.Reach), promoted: _goAround.CurrentBlocked))
        {
            _logger.Log(refused, "[ManualYouTurn] The turn would cross obstacle {I} - refusing", inner + 1);
            obstacleInTheWay = true;
            path.Clear();
            return path;
        }

        // A manual turn that would take the tractor out of the field (past the outer boundary,
        // or into an obstacle a turn may not drive through) is refused, as an automatic one is
        // (#698): tapped at the headland line with a headland narrower than the turn, it went up
        // to 7 m out of the field (Turn intervention scenarios T11, TURN-OUTSIDE-BOUNDARY).
        // AgOpenGPS's manual turn has no such check. Judged only with the tractor inside the field:
        // from outside, every path starts outside.
        var outer = boundary?.OuterBoundary;
        if (outer != null && outer.IsValid && outer.IsPointInside(currentPosition.Easting, currentPosition.Northing)
            && LeavesField(path, boundary))
        {
            _logger.LogDebug("[ManualYouTurn] The turn would leave the field — refusing");
            path.Clear();
            return path;
        }

        _logger.LogDebug("[ManualYouTurn] Dubins path: {Count} points, radius={Rad:F1}m, offset={Off:F1}m, turnLeft={Left}{For}",
            path.Count, turnRadius, turnOffset, turnLeft, plan.On ? ", planned for the front implement's centre" : "");

        return path;
    }

    /// <summary>Manual turn starts this far ahead of the tractor (AgOpenGPS: 4 m).</summary>
    private const double ManualTurnStartLead = 4.0;

    /// <summary>A turn planned for a front implement's centre starts at least this far ahead of the
    /// implement: a straight stretch first, so the pivot's path starts at the tractor, m.</summary>
    private const double ManualToolLead = 0.5;

    /// <summary>Heading rotation that keeps a manual turn's start and goal from being
    /// exactly abreast (AgOpenGPS: 0.01 rad).</summary>
    private const double ManualTurnHeadingTweak = 0.01;

    /// <summary>Floor for a nonsense configured U-turn radius (0 or negative).</summary>
    private const double MinManualTurnRadius = 1.0;

    /// <summary>
    /// Closest point on <paramref name="pass"/> to <paramref name="point"/>: on the infinite
    /// line for an AB pass (two points), on the polyline for a curve.
    /// </summary>
    private static bool TryProjectOntoPass(Models.Track.Track? pass, Vec3 point, out double e, out double n)
    {
        e = n = 0;
        if (pass == null || pass.Points.Count < 2) return false;
        bool infinite = pass.Points.Count == 2;
        double best = double.MaxValue;
        for (int i = 0; i < pass.Points.Count - 1; i++)
        {
            var a = pass.Points[i];
            var b = pass.Points[i + 1];
            double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing;
            double len2 = dx * dx + dy * dy;
            if (len2 < 1e-9) continue;
            double t = ((point.Easting - a.Easting) * dx + (point.Northing - a.Northing) * dy) / len2;
            if (!infinite) t = Math.Clamp(t, 0, 1);
            double pe = a.Easting + t * dx, pn = a.Northing + t * dy;
            double d2 = (pe - point.Easting) * (pe - point.Easting) + (pn - point.Northing) * (pn - point.Northing);
            if (d2 < best) { best = d2; e = pe; n = pn; }
        }
        return best < double.MaxValue;
    }

    private static double NormalizeHeading(double heading)
    {
        heading %= Math.PI * 2;
        return heading < 0 ? heading + Math.PI * 2 : heading;
    }

    /// <summary>
    /// Distance from (startE, startN) along direction `heading` to the first intersection
    /// with a closed polygon. Returns double.MaxValue if the ray never hits. Used by
    /// SimpleFallback to measure the actual forward distance from the headland crossing to
    /// the outer boundary on non-rectangular fields (#289 F4).
    /// </summary>
    private static double RaycastDistanceToPolygon(
        double startE, double startN, double heading, IReadOnlyList<BoundaryPoint> polygon)
    {
        if (polygon.Count < 3) return double.MaxValue;

        double dirE = Math.Sin(heading);
        double dirN = Math.Cos(heading);
        double minDist = double.MaxValue;
        int n = polygon.Count;

        for (int i = 0; i < n; i++)
        {
            var p1 = polygon[i];
            var p2 = polygon[(i + 1) % n];

            double edgeE = p2.Easting - p1.Easting;
            double edgeN = p2.Northing - p1.Northing;
            double toP1E = p1.Easting - startE;
            double toP1N = p1.Northing - startN;

            double cross = dirE * edgeN - dirN * edgeE;
            if (Math.Abs(cross) < 1e-10) continue;

            double t = (toP1E * edgeN - toP1N * edgeE) / cross;
            double u = (toP1E * dirN - toP1N * dirE) / cross;

            if (t > 0 && u >= 0 && u <= 1 && t < minDist)
                minDist = t;
        }

        return minDist;
    }
}
