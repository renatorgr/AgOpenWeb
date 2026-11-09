// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
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

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// A tool offset over half the pass spacing (a 3 m mower shifted 2.5 m to one side): the tractor
/// steers 2.5 m beside the pass, on the other side after every turn, so its own lines are 8 m
/// apart one way and 2 m the other way, and that 2 m is BACK across the turn's direction. The
/// turn offset carries that sign, as AgOpenGPS's turnOffset does (CYouTurn: rowSkipsWidth ±
/// tool.offset * 2). Unsigned, the -2 m turn was planned as +2 m: its arc swung to the wrong
/// side and its exit leg, two passes over, landed a whole pass from the pass worked next
/// (TURN-EXIT-OFF-PASS, 19 of 37 turns on the rig jobs J8 and J9).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class ToolOffsetTurnSideTests
{
    // 3 m passes on an AB line north along x = 0: pass n's centre is at x = 3n + 1.5. The tool is
    // 2.5 m right of the tractor, so going north the tractor steers 2.5 m west of the pass and
    // coming back 2.5 m east of it.
    private const double Width = 3;
    private const double ToolOffset = 2.5;
    private static double PassX(int n) => n * Width + Width / 2;
    private static double LineX(int n, bool north) => PassX(n) + (north ? -ToolOffset : ToolOffset);

    [TestCase(0, TestName = "Sagitta")]
    [TestCase(2, TestName = "Keyhole")]
    [TestCase(1, TestName = "KStyle")]
    public void NarrowWay_TheTurnEndsOnTheLineSteeredNext_TwoMetresBackAcrossItsOwnDirection(int style)
    {
        // North on pass 0 the tractor is at x = -1; the turn goes to pass -1 (west, a left turn),
        // where it steers x = +1 coming back: 2 m EAST, against the turn's direction.
        var b = new Bench(style);
        var (g, t) = b.OnPass(0, north: true);

        var e = b.Tick(g, t);
        Assert.That(t.TurnPath, Is.Not.Null, "a turn is planned: " + e.StatusMessage);
        Assert.Multiple(() =>
        {
            Assert.That(t.IsTurnLeft, Is.True, "the scenario: from pass 0 the turn goes west, onto pass -1");
            Assert.That(t.NextTrackTurnOffset, Is.EqualTo(-2.0).Within(0.01), "signed: the next line is on the other side of the turn");
            Assert.That(t.NextTrack!.Points[0].Easting, Is.EqualTo(LineX(-1, north: false)).Within(0.01), "the cyan line is the line steered on pass -1");
            if (style == 1)
                // The K turn's path is its forward arc and tail; the tractor reverses and takes the
                // next line from there. The arc swings to the side that line is on (east), as
                // AgOpenGPS's does for the side of its signed turnOffset.
                Assert.That(t.TurnPath![^1].Easting, Is.GreaterThan(LineX(-1, north: false)), "the K turn swings east, where the next line is");
            else
                Assert.That(t.TurnPath![^1].Easting, Is.EqualTo(LineX(-1, north: false)).Within(0.5), "the exit leg ends on it");
        });

        t.IsTriggered = t.IsExecuting = true;
        b.Complete(g, t);
        Assert.That(g.HowManyPathsAway, Is.EqualTo(-1), "guidance moves to the pass the turn was built for");
    }

    [Test]
    public void WideWay_TheTurnIsEightMetresOver_AsBefore()
    {
        // South on pass -1 the tractor is at x = +1; the turn goes on west to pass -2, where it
        // steers x = -7 going north: 8 m, the passes apart plus twice the offset.
        var b = new Bench(style: 0);
        var (g, t) = b.OnPass(-1, north: false);

        var e = b.Tick(g, t);
        Assert.That(t.TurnPath, Is.Not.Null, "a turn is planned: " + e.StatusMessage);
        Assert.Multiple(() =>
        {
            Assert.That(t.IsTurnLeft, Is.False, "heading south, west is to the right");
            Assert.That(t.NextTrackTurnOffset, Is.EqualTo(8.0).Within(0.01));
            Assert.That(t.NextTrack!.Points[0].Easting, Is.EqualTo(LineX(-2, north: true)).Within(0.01));
            Assert.That(t.TurnPath![^1].Easting, Is.EqualTo(LineX(-2, north: true)).Within(0.5));
        });
    }

    [Test]
    public void ManualTurn_NarrowWay_IsMadeAndEndsOnTheLineSteeredNext()
    {
        // The manual turn computed the same signed offset and refused it as "tool width is zero".
        var b = new Bench(style: 0);
        var (g, _) = b.OnPass(0, north: true);

        var path = b.Creation.CreateManualArcPath(b.Ctx().CurrentPosition, 0, turnLeft: true, b.Boundary, g, uTurnSkipRows: 0);

        Assert.That(path, Is.Not.Empty, "a left turn onto pass -1 is made");
        Assert.That(path[^1].Easting, Is.EqualTo(LineX(-1, north: false)).Within(0.5), "and ends 2 m east, on the line steered on pass -1");
    }

    /// <summary>The real planner on a 76 x 120 m field (3 m passes, 8 m radius, 16 m headland).</summary>
    private sealed class Bench
    {
        public readonly YouTurnStateMachine Sm;
        public readonly YouTurnCreationService Creation;
        public readonly Boundary Boundary;
        private readonly List<Vec3> _turnLine;
        public readonly Models.Track.Track Track;
        private double _x, _y, _heading;

        public Bench(int style)
        {
            ConfigurationStore.SetInstance(new ConfigurationStore());
            var c = ConfigurationStore.Instance;
            c.Vehicle.Wheelbase = 2.5;
            c.Vehicle.MaxSteerAngle = 35;
            c.Tool.Overlap = 0;
            c.Tool.Offset = ToolOffset;
            c.NumSections = 1;
            c.Tool.SetSectionWidth(0, (int)(Width * 100));
            c.Guidance.UTurnRadius = 8.0;
            c.Guidance.UTurnExtension = 16.0;
            c.Guidance.UTurnDistanceFromBoundary = 2.0;
            c.Guidance.UTurnStyle = style;

            var offset = new PolygonOffsetService();
            Creation = new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, offset, c);
            var pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);
            Sm = new YouTurnStateMachine(Creation, pathing, NullLogger<YouTurnStateMachine>.Instance, c);

            var outer = new List<Vec2> { new(-38, -60), new(38, -60), new(38, 60), new(-38, 60) };
            Boundary = new Boundary
            {
                OuterBoundary = new BoundaryPolygon
                {
                    Points = outer.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                },
            };
            _turnLine = offset.CalculatePointHeadings(offset.CreateInwardOffset(outer, 16.0)!);
            Track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
        }

        /// <summary>The tractor on the line it steers on pass <paramref name="pass"/>, 20 m before
        /// the middle of the field, heading north (along the line) or south (against it).</summary>
        public (GuidanceWorkingState, YouTurnWorkingState) OnPass(int pass, bool north)
        {
            _x = LineX(pass, north);
            _y = north ? -20 : 20;
            _heading = north ? 0 : 180;
            return (new GuidanceWorkingState { HowManyPathsAway = pass, IsHeadingSameWay = north },
                new YouTurnWorkingState { IsEnabled = true });
        }

        public YouTurnStateMachine.TickContext Ctx() => new(
            new Position { Easting = _x, Northing = _y, Heading = _heading, Speed = 2.78 },
            Track, Boundary, _turnLine,
            UTurnSkipRows: 0, IsSkipWorkedMode: false,
            HeadlandCalculatedWidth: 16.0, HeadlandDistance: 16.0);

        public YouTurnEffects Tick(GuidanceWorkingState g, YouTurnWorkingState t) => Sm.Tick(Ctx(), g, t);

        public YouTurnEffects Complete(GuidanceWorkingState g, YouTurnWorkingState t) => Sm.CompleteFromGuidance(Ctx(), g, t);
    }
}
