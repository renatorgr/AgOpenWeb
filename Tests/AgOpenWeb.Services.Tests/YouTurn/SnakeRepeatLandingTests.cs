// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// The snake (skip worked passes) never makes a turn that lands where a turn landed before in the
/// job, going the same way, on ground already worked: the tractor drove that piece from there and
/// would drive it again to the same turn. The worked-pass tests judge a pass from its end back to
/// the headland line, through an obstacle the pass turns at, so a pass whose piece before the
/// obstacle was worked still looked to do, and the snake went back and forth between two passes
/// for the rest of the job (the beta robot, simulator: 280 turns between 4R and 6R on one field,
/// 689 between 8R and 10R on another). Fork only.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class SnakeRepeatLandingTests
{
    private const double ToolWidth = 4.0;

    // Pass k is x = 4k + 2; the cultivated square -40..40 holds passes -10..9.
    private static double PassX(int k) => 4 * k + 2;

    private YouTurnStateMachine _sm = null!;
    private YouTurnPathingService _pathing = null!;
    private Boundary _boundary = null!;
    private Models.Track.Track _track = null!;
    private List<Vec3> _square = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 2.5;
        c.Vehicle.MaxSteerAngle = 35;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, (int)(ToolWidth * 100));
        c.Guidance.UTurnRadius = 4.0;
        c.Guidance.UTurnExtension = 16.0;
        c.Guidance.UTurnDistanceFromBoundary = 2.0;
        c.Guidance.UTurnStyle = 0;

        var offset = new PolygonOffsetService();
        _pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);
        _sm = new YouTurnStateMachine(
            new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, offset, c),
            _pathing, NullLogger<YouTurnStateMachine>.Instance, c);

        var outer = new List<Vec2> { new(-50, -50), new(50, -50), new(50, 50), new(-50, 50) };
        _boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = outer.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            },
        };
        _track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
        _square = offset.CalculatePointHeadings(new List<Vec2> { new(-40, -40), new(40, -40), new(40, 40), new(-40, 40) });
    }

    /// <summary>Every pass covered but pass 4 where <paramref name="left"/> says (north, metres).</summary>
    private static Func<double, double, bool> AllCoveredBut(Func<double, bool> left) =>
        (e, n) => !(Math.Abs(e - PassX(4)) < ToolWidth / 2 && left(n));

    // Pass 4 worked from its south end to 5 m south of the middle (44 %): still to do on the whole,
    // and from the south end back to the north edge it is one piece, so it looks to do there too.
    // That is the shuttle: the pass turns at an obstacle in the middle, its south piece is done.
    private static readonly Func<double, double, bool> SouthPieceDone = AllCoveredBut(n => n > -5);

    private YouTurnStateMachine.TickContext Ctx(double n, double headingDeg, Func<double, double, bool>? covered) => new(
        new Position { Easting = PassX(6), Northing = n, Heading = headingDeg, Speed = 2.78 },
        _track, _boundary, _square,
        UTurnSkipRows: 1, IsSkipWorkedMode: true,
        HeadlandCalculatedWidth: 10.0, HeadlandDistance: 10.0,
        IsPointCovered: covered);

    /// <summary>On pass 6 heading south 25 m before the south end: the turn the snake plans there.</summary>
    private (YouTurnEffects Effects, YouTurnWorkingState Turn, GuidanceWorkingState Guidance) SouthEndOfPass6(
        Func<double, double, bool>? covered)
    {
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 6, IsHeadingSameWay = false };
        var turn = new YouTurnWorkingState { IsEnabled = true };
        var ctx = Ctx(-25, 180, covered);
        var effects = _sm.Tick(ctx, guidance, turn);
        if (turn.TurnPath == null) effects = _sm.Tick(ctx, guidance, turn); // a turn dropped is looked for again
        return (effects, turn, guidance);
    }

    /// <summary>The turn onto pass 4 at the south end, driven to its end.</summary>
    private void DriveTheTurnOntoPass4(Func<double, double, bool>? covered)
    {
        var (effects, turn, guidance) = SouthEndOfPass6(covered);
        Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(4), $"the scenario: a turn onto pass 4, not \"{effects.StatusMessage}\"");
        turn.IsExecuting = true;
        _sm.CompleteFromGuidance(Ctx(-25, 180, covered), guidance, turn);
        Assert.That(guidance.HowManyPathsAway, Is.EqualTo(4), "the scenario: the turn landed on pass 4");
    }

    [Test]
    public void A_turn_that_lands_where_one_landed_before_on_worked_ground_is_not_made()
    {
        DriveTheTurnOntoPass4(SouthPieceDone);

        // Back on pass 6 at the same end: pass 4 still looks to do, and a turn onto it lands where
        // the last one did, on the piece now worked.
        var (_, turn, _) = SouthEndOfPass6(SouthPieceDone);

        Assert.That(turn.ReturnPassTargetPath, Is.Not.EqualTo(4), "no turn onto pass 4's worked piece again");
    }

    [Test]
    public void The_same_pass_is_turned_onto_at_its_other_end()
    {
        DriveTheTurnOntoPass4(SouthPieceDone);

        // On pass 6 heading north: a turn onto pass 4 there lands at its north end, left to do.
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 6, IsHeadingSameWay = true };
        var turn = new YouTurnWorkingState { IsEnabled = true };
        var effects = _sm.Tick(Ctx(25, 0, SouthPieceDone), guidance, turn);

        Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(4), $"the turn onto pass 4's north end, not \"{effects.StatusMessage}\"");
    }

    [Test]
    public void Ground_the_sections_left_unworked_gets_the_turn_again()
    {
        // Driven onto pass 4 with the sections off: nothing painted, so it is not a repeat.
        DriveTheTurnOntoPass4(SouthPieceDone);
        var nothingOnPass4 = AllCoveredBut(_ => true);

        var (effects, turn, _) = SouthEndOfPass6(nothingOnPass4);

        Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(4), $"a turn onto pass 4, not \"{effects.StatusMessage}\"");
    }

    // Pass 4 worked but its last 15 m at the south end: a turn onto it there reaches unworked crop.
    // The piece is left as it was each time (a turn at its other end made early for an obstacle).
    private static readonly Func<double, double, bool> SouthEndLeft = AllCoveredBut(n => n < -25);

    [Test]
    public void A_turn_that_lands_again_and_again_on_the_same_unworked_ground_is_not_made_a_third_time()
    {
        // The beta robot's farm job on a tester's field (simulator, 27 Nov 2026): the turn at the far
        // end of each piece was made early for an obstacle, about 15 m before it, so that ground was
        // never worked, and a turn onto the piece always reached it: 1080 turns between two passes.
        DriveTheTurnOntoPass4(SouthEndLeft);
        DriveTheTurnOntoPass4(SouthEndLeft); // made a second time: a piece driven with the sections off by hand

        var (_, turn, _) = SouthEndOfPass6(SouthEndLeft);

        Assert.That(turn.ReturnPassTargetPath, Is.Not.EqualTo(4), "no third turn onto ground two landings left unworked");
    }

    [Test]
    public void Another_track_forgets_where_turns_landed()
    {
        DriveTheTurnOntoPass4(SouthPieceDone);
        _sm.ForgetTrack(new YouTurnWorkingState());

        var (effects, turn, _) = SouthEndOfPass6(SouthPieceDone);

        Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(4), $"a turn onto pass 4, not \"{effects.StatusMessage}\"");
    }
}
