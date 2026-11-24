// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// The normal and alternative modes: an end where no turn was made is tried again every 1.5 s once
/// the tractor has moved on a little, or at once when something that can change the answer does, as the snake order's ends
/// (<see cref="SnakeOpenPassTests"/>, #852). Built again on every GPS cycle, a turn that
/// could not be made filled the 2000 lines of a bug report's log in 13 s (the tractor parked
/// with AutoSteer on, 95 builds, each moving the turn back 0.5 m at a time up to 30 m).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class FailedEndRetryTests
{
    private const double ToolWidth = 4.0;

    // Pass k is x = 4k + 2.
    private static double PassX(int k) => 4 * k + 2;

    private YouTurnStateMachine _sm = null!;
    private YouTurnCreationService _creation = null!;
    private Boundary _boundary = null!;
    private Models.Track.Track _track = null!;
    private PolygonOffsetService _offset = null!;
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
        // A 32 m loop onto a pass 8 m over, the hard fence 2 m past the turn line: no turn fits,
        // even moved back towards the tractor.
        c.Guidance.UTurnRadius = 16.0;
        c.Guidance.UTurnExtension = 16.0;
        c.Guidance.UTurnDistanceFromBoundary = 2.0;
        c.Guidance.UTurnStyle = 0;

        _offset = new PolygonOffsetService();
        _creation = new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, _offset, c);
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
        _turnLine = _offset.CalculatePointHeadings(new List<Vec2> { new(-48, -48), new(48, -48), new(48, 48), new(-48, 48) });
    }

    private YouTurnStateMachine.TickContext Ctx(double e, double n, double headingDeg, bool alternate) => new(
        new Position { Easting = e, Northing = n, Heading = headingDeg, Speed = 2.78 },
        _track, _boundary, _turnLine,
        UTurnSkipRows: 1, IsSkipWorkedMode: false,
        HeadlandCalculatedWidth: 10.0, HeadlandDistance: 10.0,
        IsAlternateSkipMode: alternate);

    // On pass 0 heading south, 30 m from the turn line.
    private static (GuidanceWorkingState Guidance, YouTurnWorkingState Turn) OnPass0() =>
        (new GuidanceWorkingState { HowManyPathsAway = 0, IsHeadingSameWay = false },
            new YouTurnWorkingState { IsEnabled = true });

    [TestCase(false, TestName = "Normal mode: an end where no turn builds is tried again after 1.5 s and a few metres, not on every cycle")]
    [TestCase(true, TestName = "Alternative mode: an end where no turn builds is tried again after 1.5 s and a few metres, not on every cycle")]
    public void Tried_again_every_second_and_a_half(bool alternate)
    {
        var (guidance, turn) = OnPass0();

        var first = _sm.Tick(Ctx(PassX(0), -18, 180, alternate), guidance, turn);
        int builds = _creation.TurnPathBuilds;
        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Null, $"the scenario: no turn fits (\"{first.StatusMessage}\")");
            Assert.That(builds, Is.GreaterThan(0), "the scenario: a turn was built and did not fit");
            Assert.That(first.TurnCreationFailedSound, Is.True, "the operator is told once");
        });

        // Parked, or creeping (the bug report: a tractor parked with AutoSteer on): 4 s, 0.4 m, nothing
        // built. It was tried every 1.5 s: nothing that can change the answer changes standing there.
        for (int i = 1; i <= 40; i++)
            _sm.Tick(Ctx(PassX(0), -18 - 0.01 * i, 180, alternate), guidance, turn);
        Assert.That(_creation.TurnPathBuilds, Is.EqualTo(builds), "not built again parked");

        // Driving on, 0.3 m a cycle: not before the tractor is 2 m on, and by a tenth of the 30 m to the end.
        int retriedAt = 0;
        YouTurnEffects? retry = null;
        for (int i = 1; i <= 12 && retriedAt == 0; i++)
        {
            var effects = _sm.Tick(Ctx(PassX(0), -18.4 - 0.3 * i, 180, alternate), guidance, turn);
            if (_creation.TurnPathBuilds > builds) { retriedAt = i; retry = effects; }
        }
        Assert.Multiple(() =>
        {
            Assert.That(retriedAt, Is.GreaterThan(5), "not before the tractor is 2 m on");
            Assert.That(retriedAt, Is.LessThanOrEqualTo(9), "tried again by the time it is 3 m on");
            Assert.That(_creation.TurnPathBuilds, Is.EqualTo(2 * builds), "tried again once");
            Assert.That(retry?.TurnCreationFailedSound, Is.False, "silently");
            Assert.That(retry?.TellOperator, Is.False, "the operator was told once");
        });
    }

    [TestCase(false, TestName = "Normal mode: in the simulator's time-lapse an end with no turn is tried again ten times fewer cycles on")]
    [TestCase(true, TestName = "Alternative mode: in the simulator's time-lapse an end with no turn is tried again ten times fewer cycles on")]
    public void In_the_time_lapse_tried_again_as_often_in_real_time(bool alternate)
    {
        // 10x: the cycles come ten times as fast, and each try where no turn fits near obstacles took
        // 0.33 s on the mini PC: tried every 15 cycles, the 10x ran at 2.6x (Couto da Ribeira, 24 Nov 2026).
        int RetriedAt(double timeLapse)
        {
            SetUp();
            var (guidance, turn) = OnPass0();
            _sm.Tick(Ctx(PassX(0), -18, 180, alternate) with { TimeLapse = timeLapse }, guidance, turn);
            Assert.That(turn.TurnPath, Is.Null, "the scenario: no turn fits");
            int builds = _creation.TurnPathBuilds;
            for (int i = 1; i <= 160; i++) // 0.12 m a cycle: 18 m in 150, still 12 m before the end
            {
                _sm.Tick(Ctx(PassX(0), -18 - 0.12 * i, 180, alternate) with { TimeLapse = timeLapse }, guidance, turn);
                if (_creation.TurnPathBuilds > builds) return i;
            }
            return 0;
        }

        Assert.Multiple(() =>
        {
            Assert.That(RetriedAt(1.0), Is.InRange(15, 25), "at 1x: 15 cycles, and 2.7 m on");
            Assert.That(RetriedAt(SimulatorTimeLapse.Scale), Is.EqualTo(150), "at 10x: 150 cycles, 1.5 s as at 1x");
        });
    }

    [TestCase(false, TestName = "Normal mode: the arrow refused and no turn built says there is no turn")]
    [TestCase(true, TestName = "Alternative mode: the arrow refused and no turn built says there is no turn")]
    public void The_arrow_refused_in_the_cycle_no_turn_builds_does_not_hide_that_there_is_no_turn(bool alternate)
    {
        // Pass 10 (x = 42), two passes from the east fence: the passes two over to the east are
        // outside the field, so the arrow pointing east is refused, and the turn west does not
        // build (the fixture's 32 m loop). The refusal's text took the place of the reason, with
        // the failure sound, and the end is then tried again silently.
        (string Message, bool ArrowRefused) Told(bool? arrow)
        {
            SetUp();
            var guidance = new GuidanceWorkingState { HowManyPathsAway = 10, IsHeadingSameWay = false };
            var turn = new YouTurnWorkingState { IsEnabled = true, NextUTurnDirectionLeftOverride = arrow };
            var effects = _sm.Tick(Ctx(PassX(10), -18, 180, alternate), guidance, turn);
            Assert.That(turn.TurnPath, Is.Null, $"the scenario: no turn fits (\"{effects.StatusMessage}\")");
            Assert.That(effects.TurnCreationFailedSound, Is.True, "the scenario: the failure is sounded");
            return (effects.StatusMessage ?? "", effects.DirectionSwapRefused);
        }

        var none = Told(null);
        Assert.That(none.Message, Is.Not.Empty.And.Not.EqualTo(YouTurnStateMachine.DirectionNotChangedMessage));
        Assert.That(none.ArrowRefused, Is.False);
        // Whichever way the arrow points, the operator reads why there is no turn.
        var left = Told(true);
        var right = Told(false);
        Assert.That(left.ArrowRefused || right.ArrowRefused, Is.True, "the scenario: one way of the arrow is refused");
        Assert.That(left.Message, Is.EqualTo(none.Message), "arrow left");
        Assert.That(right.Message, Is.EqualTo(none.Message), "arrow right");
    }

    [TestCase(false, TestName = "Normal mode: the end tried again 1.5 s later turns the way the U-turn arrow points")]
    [TestCase(true, TestName = "Alternative mode: the end tried again 1.5 s later turns the way the U-turn arrow points")]
    public void The_retry_keeps_the_arrow_s_side(bool alternate)
    {
        // The side the turn goes with no arrow.
        var (probeGuidance, probeTurn) = OnPass0();
        _sm.Tick(Ctx(PassX(0), -18, 180, alternate), probeGuidance, probeTurn);
        bool plannedLeft = probeTurn.IsTurnLeft;
        _sm.OnYouTurnSwitchedOff();

        // The operator points the arrow the other way; no turn fits on that side either.
        var (guidance, turn) = OnPass0();
        turn.NextUTurnDirectionLeftOverride = !plannedLeft;
        _sm.Tick(Ctx(PassX(0), -18, 180, alternate), guidance, turn);
        int builds = _creation.TurnPathBuilds;
        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Null, "the scenario: no turn fits");
            Assert.That(turn.IsTurnLeft, Is.EqualTo(!plannedLeft), "the first try turns the arrow's way");
        });

        for (int i = 1; i <= 14; i++)
            _sm.Tick(Ctx(PassX(0), -18 - 0.3 * i, 180, alternate), guidance, turn);
        Assert.That(_creation.TurnPathBuilds, Is.EqualTo(builds), "the scenario: waiting 1.5 s");

        // Tried again: the try that made no turn took the arrow, and the retry turned the other way.
        _sm.Tick(Ctx(PassX(0), -22.5, 180, alternate), guidance, turn);
        Assert.Multiple(() =>
        {
            Assert.That(_creation.TurnPathBuilds, Is.GreaterThan(builds), "the scenario: tried again after 1.5 s");
            Assert.That(turn.IsTurnLeft, Is.EqualTo(!plannedLeft), "the retry turns the arrow's way too");
        });
    }

    [Test]
    public void Normal_mode_an_end_where_no_turn_built_is_tried_again_at_once_when_something_changes()
    {
        var (guidance, turn) = OnPass0();
        _sm.Tick(Ctx(PassX(0), -18, 180, false), guidance, turn);
        int builds = _creation.TurnPathBuilds;

        void TriedAgain(string what, YouTurnStateMachine.TickContext ctx)
        {
            _sm.Tick(ctx, guidance, turn);
            Assert.That(_creation.TurnPathBuilds, Is.GreaterThan(builds), what);
            builds = _creation.TurnPathBuilds;
        }

        // The U-turn arrow: the operator picks a side.
        turn.NextUTurnDirectionLeftOverride = true;
        TriedAgain("the U-turn arrow", Ctx(PassX(0), -18, 180, false));

        // Another pass (a lateral move): its end is another end.
        guidance.HowManyPathsAway = 1;
        TriedAgain("another pass", Ctx(PassX(1), -18, 180, false));

        // The alternative mode chosen.
        TriedAgain("the alternative mode", Ctx(PassX(1), -18, 180, true));

        // The fence made soft, still in the alternative mode: the turn may now swing over it.
        _boundary.OuterBoundary!.IsHard = false;
        TriedAgain("the fence made soft", Ctx(PassX(1), -18, 180, true));
    }

    [Test]
    public void Normal_mode_a_heading_flickering_off_the_line_does_not_build_the_turn_again()
    {
        // Parked with AutoSteer on, a single antenna's heading swings past the 20 degrees the turn is
        // looked for within, and back: the end is not tried again until the 1.5 s are up.
        var (guidance, turn) = OnPass0();
        _sm.Tick(Ctx(PassX(0), -18, 180, false), guidance, turn);
        int builds = _creation.TurnPathBuilds;
        Assert.That(builds, Is.GreaterThan(0), "the scenario: a turn was built and did not fit");

        for (int i = 1; i <= 6; i++)
        {
            _sm.Tick(Ctx(PassX(0), -18, 180 + 30, false), guidance, turn); // off the line
            _sm.Tick(Ctx(PassX(0), -18, 180, false), guidance, turn);
        }
        Assert.That(_creation.TurnPathBuilds, Is.EqualTo(builds), "not built again at each flicker");

        // Off the line for 1.5 s (turned away, and back): tried again at once.
        for (int i = 0; i < 15; i++) _sm.Tick(Ctx(PassX(0), -18, 180 + 30, false), guidance, turn);
        _sm.Tick(Ctx(PassX(0), -18, 180, false), guidance, turn);
        Assert.That(_creation.TurnPathBuilds, Is.GreaterThan(builds), "tried again after 1.5 s off the line");
    }

    [Test]
    public void Normal_mode_a_turn_radius_lowered_to_make_it_fit_is_built_at_once()
    {
        var (guidance, turn) = OnPass0();
        _sm.Tick(Ctx(PassX(0), -18, 180, false), guidance, turn);
        Assert.That(turn.TurnPath, Is.Null, "the scenario: no turn at 16 m");

        ConfigurationStore.Instance.Guidance.UTurnRadius = 4.0;
        _sm.Tick(Ctx(PassX(0), -18.01, 180, false), guidance, turn);
        Assert.That(turn.TurnPath, Is.Not.Null, "the radius changed: tried again at once, and it fits");
    }

    // ── The log (fork only) ──────────────────────────────────────────────

    /// <summary>The planner as in <see cref="SetUp"/>, writing its log into <paramref name="lines"/>.</summary>
    private void LogInto(List<string> lines)
    {
        var c = ConfigurationStore.Instance;
        _creation = new YouTurnCreationService(new CapturedLogger<YouTurnCreationService>(lines), _offset, c);
        _sm = new YouTurnStateMachine(
            _creation,
            new YouTurnPathingService(new CapturedLogger<YouTurnPathingService>(lines), c),
            new CapturedLogger<YouTurnStateMachine>(lines), c);
    }

    [TestCase(false, TestName = "Normal mode: a try again that makes no turn writes one line, not all it tried again")]
    [TestCase(true, TestName = "Alternative mode: a try again that makes no turn writes one line, not all it tried again")]
    public void A_try_again_with_no_turn_writes_one_line(bool alternate)
    {
        // Each try wrote what it tried, line for line: 140 lines a try at a Go around obstacle no turn
        // clears (Couto da Ribeira, 24 Nov 2026), so the bug report's 2000 lines reached back 11 s.
        var lines = new List<string>();
        LogInto(lines);
        var (guidance, turn) = OnPass0();

        _sm.Tick(Ctx(PassX(0), -18, 180, alternate), guidance, turn);
        int builds = _creation.TurnPathBuilds;
        Assert.That(lines.Count(l => l.StartsWith("YouTurnCreationService", StringComparison.Ordinal)), Is.GreaterThan(1),
            "the scenario: the first try writes what it tried");

        lines.Clear();
        for (int i = 1; i <= 15; i++)
            _sm.Tick(Ctx(PassX(0), -18 - 0.3 * i, 180, alternate), guidance, turn);
        Assert.That(_creation.TurnPathBuilds, Is.GreaterThan(builds), "the scenario: tried again");
        Assert.Multiple(() =>
        {
            Assert.That(lines.Where(l => l.StartsWith("YouTurnCreationService", StringComparison.Ordinal)), Is.Empty,
                "what the try again built is not written again");
            Assert.That(lines.Where(l => l.Contains("Trying the end again", StringComparison.Ordinal)), Is.Empty);
            Assert.That(lines.Count(l => l.Contains("Tried the end again", StringComparison.Ordinal)
                    && l.Contains("still no turn", StringComparison.Ordinal)), Is.EqualTo(1),
                "one line says it was tried again: " + string.Join(" | ", lines));
        });
    }

    [Test]
    public void Near_an_end_with_no_turn_the_zone_line_is_written_when_it_changes_or_every_10_m()
    {
        // Written on every cycle while no turn was planned within 100 m of the end: 450 of a bug
        // report's 2000 lines for the last 100 m at 8 km/h.
        var lines = new List<string>();
        LogInto(lines);
        var (guidance, turn) = OnPass0();

        for (int i = 0; i <= 40; i++) // 12 m on, 0.3 m a cycle
            _sm.Tick(Ctx(PassX(0), -18 - 0.3 * i, 180, false), guidance, turn);
        Assert.That(lines.Count(l => l.Contains("Zone=", StringComparison.Ordinal)), Is.InRange(1, 3),
            "at the start and every 10 m, not on each of the 41 cycles");
    }

    [Test]
    public void Lines_held_are_written_in_order_to_their_own_logger_and_errors_never_held()
    {
        var a = new List<string>();
        var b = new List<string>();
        var logA = YouTurnLog.Holdable(new CapturedLogger<YouTurnCreationService>(a));
        var logB = YouTurnLog.Holdable(new CapturedLogger<YouTurnPathingService>(b));
        var held = new List<YouTurnLog.Line>();
        try
        {
            YouTurnLog.HoldInto(held);
            logA.LogDebug("one {N}", 1);
            logB.LogWarning("two");
            logA.LogError("now");
            string? otherThread = null;
            var t = new Thread(() => { logB.LogDebug("other thread"); otherThread = string.Join(",", b); });
            t.Start();
            t.Join();
            Assert.Multiple(() =>
            {
                Assert.That(a, Is.EqualTo(new[] { "YouTurnCreationService: now" }), "an error is written at once");
                Assert.That(otherThread, Is.EqualTo("YouTurnPathingService: other thread"), "another thread writes as usual");
                Assert.That(held.Select(l => l.Text), Is.EqualTo(new[] { "one 1", "two" }));
            });
        }
        finally
        {
            YouTurnLog.HoldInto(null);
        }

        logA.LogDebug("not held");
        YouTurnLog.Write(held);
        Assert.Multiple(() =>
        {
            Assert.That(a, Is.EqualTo(new[] { "YouTurnCreationService: now", "YouTurnCreationService: not held", "YouTurnCreationService: one 1" }));
            Assert.That(b, Is.EqualTo(new[] { "YouTurnPathingService: other thread", "YouTurnPathingService: two" }));
        });
    }
}
