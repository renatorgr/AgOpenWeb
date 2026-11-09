// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.YouTurn;

using Microsoft.Extensions.Logging.Abstractions;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// Skip rows (alternative) switched on after a few turns: the pattern sets off the way the turns
/// have been stepping across the field, as the plain turns do (#523) and as AgOpenGPS does, whose
/// isTurnLeft carries over from the last turn. It took the pass number's sign instead, so a job
/// started at the field's edge on the negative side turned back over the passes just worked
/// (SKIP-TURNS-BACK: T4, skip rows set during the second turn, 75 % of the field left).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class AlternateSkipStartDirectionTests
{
    // 6 m passes on an AB line north along x = 0: pass n's centre is at x = 6n + 3.
    // Fence at ±60, turn line 16 m inside it: passes -7..6 lie inside the cultivated area.
    private const double Width = 6;
    private static double PassX(int n) => n * Width + Width / 2;

    [Test]
    public void SkipRowsSwitchedOnAfterTwoTurns_TheFirstSkipGoesOnTheWayTheTurnsWereStepping()
    {
        // From the west edge: -7 -> -6 -> -5, two plain turns stepping east.
        var bench = new Bench();
        var (guidance, turn) = bench.OnPass(-7);
        Assert.That(bench.TurnAndComplete(guidance, turn), Is.EqualTo(-6), "the scenario: the only pass to turn onto");
        Assert.That(bench.TurnAndComplete(guidance, turn), Is.EqualTo(-5), "and on east");

        // Skip rows on (alternative, 1 row): the next turn skips a pass EAST, onto -3.
        bench.Alternate = true;
        bench.SkipRows = 1;
        bench.Tick(guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(-3), "not back west onto -7, worked two turns ago");
            Assert.That(turn.NextTrack!.Points[0].Easting, Is.EqualTo(PassX(-3)).Within(0.01));
            Assert.That(turn.IsTurnLeft, Is.False, "heading north, east is to the right");
        });
    }

    [Test]
    public void SkipRowsSwitchedOnBeforeTheFirstTurn_StillGoesAwayFromTheReferenceLine()
    {
        var bench = new Bench { Alternate = true, SkipRows = 1 };
        var (guidance, turn) = bench.OnPass(0);
        bench.Tick(guidance, turn);
        Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(-2), "before any turn, as before: away from the reference line");
    }

    private sealed class Bench
    {
        private readonly YouTurnStateMachine _sm;
        private readonly Boundary _boundary;
        private readonly List<Vec3> _turnLine;
        public bool Alternate { get; set; }
        public int SkipRows { get; set; }
        private double _x;
        public Models.Track.Track Track { get; }

        public Bench()
        {
            ConfigurationStore.SetInstance(new ConfigurationStore());
            var c = ConfigurationStore.Instance;
            c.Vehicle.Wheelbase = 2.5;
            c.Vehicle.MaxSteerAngle = 35;
            c.Tool.Overlap = 0;
            c.NumSections = 1;
            c.Tool.SetSectionWidth(0, (int)(Width * 100));
            c.Guidance.UTurnRadius = 8.0;
            c.Guidance.UTurnExtension = 16.0;
            c.Guidance.UTurnDistanceFromBoundary = 2.0;
            c.Guidance.UTurnStyle = 0;

            var offset = new PolygonOffsetService();
            var creation = new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, offset, c);
            var pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);
            _sm = new YouTurnStateMachine(creation, pathing, NullLogger<YouTurnStateMachine>.Instance, c);

            var outer = new List<Vec2> { new(-60, -60), new(60, -60), new(60, 60), new(-60, 60) };
            _boundary = new Boundary
            {
                OuterBoundary = new BoundaryPolygon
                {
                    Points = outer.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                },
            };
            _turnLine = offset.CalculatePointHeadings(offset.CreateInwardOffset(outer, 16.0)!);
            Track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
        }

        /// <summary>The tractor on pass <paramref name="pass"/>, heading north, 64 m before the turn line.</summary>
        public (GuidanceWorkingState, YouTurnWorkingState) OnPass(int pass)
        {
            _x = PassX(pass);
            return (new GuidanceWorkingState { HowManyPathsAway = pass }, new YouTurnWorkingState { IsEnabled = true });
        }

        private YouTurnStateMachine.TickContext Ctx() => new(
            new Position { Easting = _x, Northing = -20, Heading = 0, Speed = 2.78 },
            Track, _boundary, _turnLine,
            UTurnSkipRows: SkipRows, IsSkipWorkedMode: false,
            HeadlandCalculatedWidth: 16.0, HeadlandDistance: 16.0,
            IsAlternateSkipMode: Alternate);

        public YouTurnEffects Tick(GuidanceWorkingState guidance, YouTurnWorkingState turn)
        {
            var effects = _sm.Tick(Ctx(), guidance, turn);
            Assert.That(turn.TurnPath, Is.Not.Null, "a turn is planned: " + effects.StatusMessage);
            return effects;
        }

        /// <summary>Plan a turn, drive it, and stand on the pass it lands on, 64 m before the turn line again.</summary>
        public int TurnAndComplete(GuidanceWorkingState guidance, YouTurnWorkingState turn)
        {
            Tick(guidance, turn);
            turn.IsTriggered = turn.IsExecuting = true;
            _sm.CompleteFromGuidance(Ctx(), guidance, turn);
            _x = PassX(guidance.HowManyPathsAway);
            return guidance.HowManyPathsAway;
        }
    }
}
