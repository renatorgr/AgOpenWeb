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
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// The snake's search for a turn (skip worked passes) builds a turn onto each pass it tries, inside the
/// GPS cycle. On a tester's tablet near obstacles and a hard fence one search built eight, each 0.1 to
/// 0.5 s, and the cycles of 1.0 to 1.24 s took AutoSteer off with "no GPS/guidance update for over 1 s"
/// (#169; Couto da Ribeira, the Puma, 23 Nov 2026). With a time for the search in the cycle
/// (<see cref="YouTurnStateMachine.TickContext.PlanningBudget"/>, given to live cycles) the search stops
/// before its next build once the time is up and goes on in the next cycle where it stopped, with the
/// same answer in the end. A budget of zero makes it stop after every build, so each cycle here builds
/// one turn. Fork only.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class SnakeSearchOverCyclesTests
{
    private const double ToolWidth = 4.0;

    // Pass k is x = 4k + 2.
    private static double PassX(int k) => 4 * k + 2;

    private YouTurnStateMachine _sm = null!;
    private YouTurnCreationService _creation = null!;
    private Boundary _boundary = null!;
    private Models.Track.Track _track = null!;
    private List<Vec3> _turnLine = null!;

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
        // A 32 m loop and a hard fence 2 m past the turn line: a turn onto a pass 8 or 12 m over does
        // not clear the fence, even moved back towards the tractor; onto a pass 32 m over it is a plain U.
        c.Guidance.UTurnRadius = 16.0;
        c.Guidance.UTurnExtension = 16.0;
        c.Guidance.UTurnDistanceFromBoundary = 2.0;
        c.Guidance.UTurnStyle = 0;

        var offset = new PolygonOffsetService();
        _creation = new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, offset, c);
        _sm = new YouTurnStateMachine(
            _creation,
            new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c),
            NullLogger<YouTurnStateMachine>.Instance, c);

        var outer = new List<Vec2> { new(-50, -50), new(50, -50), new(50, 50), new(-50, 50) };
        _boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = outer.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                IsHard = true,
            },
        };
        _track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
        _turnLine = offset.CalculatePointHeadings(new List<Vec2> { new(-48, -48), new(48, -48), new(48, 48), new(-48, 48) });
    }

    private YouTurnStateMachine.TickContext Ctx(double n, TimeSpan? budget, double headingDeg = 180) => new(
        new Position { Easting = PassX(0), Northing = n, Heading = headingDeg, Speed = 2.78 },
        _track, _boundary, _turnLine,
        UTurnSkipRows: 1, IsSkipWorkedMode: true,
        HeadlandCalculatedWidth: 10.0, HeadlandDistance: 10.0,
        PlanningBudget: budget);

    // On pass 0 heading south, passes 0, 2, 4 and 6 worked: the order's next pass is 8, far along the
    // headland, so the nearest passes still to do are tried first, 3 then -2, and neither takes a turn
    // here; the order's pass 8, 32 m over, does (SnakeOpenPassTests: three builds, one cycle).
    private (GuidanceWorkingState Guidance, YouTurnWorkingState Turn) OrderPassFar()
    {
        foreach (int k in new[] { 0, 2, 4, 6 }) _track.MarkPathWorked(k);
        return (new GuidanceWorkingState { HowManyPathsAway = 0, IsHeadingSameWay = false },
            new YouTurnWorkingState { IsEnabled = true });
    }

    // As above with passes 7 and 8 worked too: the order goes on to pass 10, and no pass tried takes a
    // turn here (3 and -2 beside the tractor, then 10 at the far side).
    private (GuidanceWorkingState Guidance, YouTurnWorkingState Turn) NoTurnAnywhere()
    {
        var setup = OrderPassFar();
        ConfigurationStore.Instance.Guidance.UTurnRadius = 24.0;
        return setup;
    }

    [Test]
    public void The_search_goes_on_over_cycles_one_build_each_and_ends_with_the_same_turn()
    {
        var (guidance, turn) = OrderPassFar();
        var builds = new List<int>();
        var said = new List<string?>();
        double n = -18;
        for (int cycle = 0; cycle < 6 && turn.TurnPath == null; cycle++, n -= 0.3)
        {
            int before = _creation.TurnPathBuilds;
            var effects = _sm.Tick(Ctx(n, TimeSpan.Zero), guidance, turn);
            builds.Add(_creation.TurnPathBuilds - before);
            Assert.That(effects.TurnCreationFailedSound, Is.False, $"cycle {cycle}: nothing sounded while the search goes on");
            if (turn.TurnPath == null)
            {
                said.Add(effects.StatusMessage);
                Assert.That(turn.NextTrack, Is.Null, $"cycle {cycle}: no next line drawn before there is a turn to it");
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Not.Null, "the turn, in the end");
            Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(8), "the same pass as in one cycle");
            Assert.That(builds, Is.EqualTo(new[] { 1, 1, 1 }), "pass 3, pass -2, then the order's pass 8: one build a cycle");
            Assert.That(said.Where(m => m != null && m.Contains("no room", StringComparison.Ordinal)), Is.Empty,
                "no 'no room' while the search goes on");
        });
    }

    [Test]
    public void With_no_limit_the_whole_search_is_one_cycle_as_before()
    {
        var (guidance, turn) = OrderPassFar();

        _sm.Tick(Ctx(-18, budget: null), guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Not.Null);
            Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(8));
            Assert.That(_creation.TurnPathBuilds, Is.EqualTo(3));
        });
    }

    [Test]
    public void An_end_with_no_turn_is_told_once_when_the_search_ends_and_tried_again_after_1_5_s()
    {
        var (guidance, turn) = NoTurnAnywhere();
        // The same end in one cycle, with no limit, for what is said.
        var reference = new YouTurnStateMachine(_creation,
            new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, ConfigurationStore.Instance),
            NullLogger<YouTurnStateMachine>.Instance, ConfigurationStore.Instance);
        var refTurn = new YouTurnWorkingState { IsEnabled = true };
        var refGuidance = new GuidanceWorkingState { HowManyPathsAway = 0, IsHeadingSameWay = false };
        var told = reference.Tick(Ctx(-18, budget: null), refGuidance, refTurn);
        int inOneCycle = _creation.TurnPathBuilds;
        Assert.That(refTurn.TurnPath, Is.Null, "the scenario: no turn here");
        Assert.That(told.TurnCreationFailedSound, Is.True, "the scenario: told in one cycle");
        Assert.That(inOneCycle, Is.GreaterThan(1), "the scenario: more than one build");

        int start = _creation.TurnPathBuilds;
        double n = -18;
        YouTurnEffects effects;
        int cycles = 0;
        do
        {
            int before = _creation.TurnPathBuilds;
            effects = _sm.Tick(Ctx(n, TimeSpan.Zero), guidance, turn);
            Assert.That(_creation.TurnPathBuilds - before, Is.LessThanOrEqualTo(1), $"cycle {cycles}: one build at most");
            cycles++;
            n -= 0.3;
        } while (!effects.TurnCreationFailedSound && cycles < 10);

        Assert.Multiple(() =>
        {
            Assert.That(effects.TurnCreationFailedSound, Is.True, "told when the search has ended");
            Assert.That(effects.StatusMessage, Is.EqualTo(told.StatusMessage), "the same words as in one cycle");
            Assert.That(cycles, Is.EqualTo(inOneCycle), "one cycle per build");
            Assert.That(_creation.TurnPathBuilds - start, Is.EqualTo(inOneCycle), "no pass built twice");
            Assert.That(turn.TurnPath, Is.Null);
        });

        // Then, as before, nothing for 1.5 s, and tried again silently.
        int after = _creation.TurnPathBuilds;
        for (int i = 0; i < 14; i++, n -= 0.3)
            Assert.That(_sm.Tick(Ctx(n, TimeSpan.Zero), guidance, turn).TurnCreationFailedSound, Is.False);
        Assert.That(_creation.TurnPathBuilds, Is.EqualTo(after), "not built again before 1.5 s");
        var retry = _sm.Tick(Ctx(n, TimeSpan.Zero), guidance, turn);
        Assert.Multiple(() =>
        {
            Assert.That(_creation.TurnPathBuilds, Is.EqualTo(after + 1), "tried again after 1.5 s, one build a cycle");
            Assert.That(retry.TurnCreationFailedSound, Is.False, "silently");
        });
    }

    [Test]
    public void A_search_not_gone_on_with_in_the_next_cycle_starts_again()
    {
        var (guidance, turn) = OrderPassFar();

        _sm.Tick(Ctx(-18, TimeSpan.Zero), guidance, turn);
        Assert.That(_creation.TurnPathBuilds, Is.EqualTo(1), "the scenario: stopped after pass 3");

        // A cycle off the line's heading (out of the gate): no search. The tractor's place and the
        // field may have moved on since, so what was found is not used again.
        _sm.Tick(Ctx(-18.3, TimeSpan.Zero, headingDeg: 120), guidance, turn);
        Assert.That(_creation.TurnPathBuilds, Is.EqualTo(1), "the scenario: no build off the heading");

        _sm.Tick(Ctx(-18.6, TimeSpan.Zero), guidance, turn);
        Assert.Multiple(() =>
        {
            Assert.That(_creation.TurnPathBuilds, Is.EqualTo(2), "one build");
            Assert.That(turn.TurnPath, Is.Null, "pass 3 tried again first, not pass -2 after it");
        });
    }

    [Test]
    public void The_live_budget_with_one_slow_build_keeps_the_cycle_under_the_AutoSteer_guard()
    {
        // The slowest build on the tester's tablet was 0.5 s; the search stops before another one.
        Assert.That(YouTurnStateMachine.LiveCyclePlanningBudget + TimeSpan.FromSeconds(0.5),
            Is.LessThan(AutoSteerService.GuidanceStaleLimit));
    }
}
