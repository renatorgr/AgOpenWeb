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
/// What the operator is told when no turn is made at the end of a pass while the turns step one
/// way across the field (#523). The next pass on that side inside the field somewhere, but with
/// nothing to land on at this end (the headland cuts across it: a notch in the fence, an
/// hourglass's neck): "End of field reached - no room on the next pass at this end" (#712), so
/// the operator knows to drive on and let the turns resume. It said "End of field reached" with
/// a fifth of the field still to do (J14, J16, J19 of the rig jobs). With that pass outside the
/// field, the field is done and the plain line stays.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class EndOfFieldMessageTests
{
    // 6 m passes on an AB line north along x = 0: pass n's centre is at x = 6n + 3. Fence at ±60
    // with a bay in its east side (east of x = 36 the field only reaches from y = 16 up to the
    // north fence), turn line 16 m inside it: the passes up to x = 20 run the field's full
    // height, those east of it have only 12 m inside, at the north end.
    private const double Width = 6;
    private static double PassX(int n) => n * Width + Width / 2;

    [Test]
    public void NextPassCutShortAtThisEnd_SaysNoRoomOnTheNextPass()
    {
        var bench = new Bench();
        var (guidance, turn) = bench.OnPass(1);
        Assert.That(bench.TurnAndComplete(guidance, turn), Is.EqualTo(2), "the scenario: the first turn steps east, onto pass 2");

        // Pass 3 has 12 m inside the field at the north end, where the tractor is heading: too
        // short to land a turn on (#712), inside the field all the same. Pass 1 was just worked.
        var effects = bench.Tick(guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Null, "no turn");
            Assert.That(effects.StatusMessage, Is.EqualTo("End of field reached - no room on the next pass at this end"));
        });
    }

    [Test]
    public void NextPassOutsideTheField_SaysEndOfField()
    {
        var bench = new Bench();
        var (guidance, turn) = bench.OnPass(-6);
        Assert.That(bench.TurnAndComplete(guidance, turn), Is.EqualTo(-7), "the scenario: the first turn steps west, onto the last pass");

        // Pass -8 is outside the cultivated area; pass -6 was just worked.
        var effects = bench.Tick(guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Null, "no turn");
            Assert.That(effects.StatusMessage, Is.EqualTo("End of field reached"));
        });
    }

    private sealed class Bench
    {
        private readonly YouTurnStateMachine _sm;
        private readonly Boundary _boundary;
        private readonly List<Vec3> _turnLine;
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

            var outer = new List<Vec2> { new(-60, -60), new(36, -60), new(36, 16), new(60, 16), new(60, 60), new(-60, 60) };
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

        /// <summary>The tractor on pass <paramref name="pass"/>, heading north, 64 m before the north turn line.</summary>
        public (GuidanceWorkingState, YouTurnWorkingState) OnPass(int pass)
        {
            _x = PassX(pass);
            return (new GuidanceWorkingState { HowManyPathsAway = pass }, new YouTurnWorkingState { IsEnabled = true });
        }

        private YouTurnStateMachine.TickContext Ctx() => new(
            new Position { Easting = _x, Northing = -20, Heading = 0, Speed = 2.78 },
            Track, _boundary, _turnLine,
            UTurnSkipRows: 0, IsSkipWorkedMode: false,
            HeadlandCalculatedWidth: 16.0, HeadlandDistance: 16.0);

        public YouTurnEffects Tick(GuidanceWorkingState guidance, YouTurnWorkingState turn) => _sm.Tick(Ctx(), guidance, turn);

        /// <summary>Plan a turn, drive it, and stand on the pass it lands on, 64 m before the turn line again.</summary>
        public int TurnAndComplete(GuidanceWorkingState guidance, YouTurnWorkingState turn)
        {
            var effects = Tick(guidance, turn);
            Assert.That(turn.TurnPath, Is.Not.Null, "a turn is planned: " + effects.StatusMessage);
            turn.IsTriggered = turn.IsExecuting = true;
            _sm.CompleteFromGuidance(Ctx(), guidance, turn);
            _x = PassX(guidance.HowManyPathsAway);
            return guidance.HowManyPathsAway;
        }
    }
}
