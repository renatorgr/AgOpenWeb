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
/// The snake (skip worked passes) makes no turn onto a piece of a pass that is worked as far as
/// that pass is next turned at. A ditch drawn as a Go around obstacle runs across every pass of
/// the field, too long to go round, so each pass is turned at it; the piece of crop between the
/// ditch and the north headland is worked, and south of the ditch nothing is. Every pass is still
/// to do on the whole, and from its end at the north headland the end tests judge it through the
/// ditch (they cut a pass only at the obstacles chosen as U-turn), so from inside the worked piece
/// every turn went onto a pass still to do and landed inside the piece again: on a tester's field
/// the farm job harness drove 988 turns there until its time ran out (28 Nov 2026). Fork only.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class SnakeWorkedPieceTests
{
    private const double ToolWidth = 4.0;
    private const double Headland = 10.0;

    // The ditch, from 4 m inside the west fence to 4 m inside the east one.
    private const double DitchSouth = 30.0, DitchNorth = 36.0;

    // Pass k is x = 4k + 2.
    private static double PassX(int k) => 4 * k + 2;

    private YouTurnStateMachine _sm = null!;
    private YouTurnPathingService _pathing = null!;
    private Boundary _boundary = null!;
    private Models.Track.Track _track = null!;
    private List<Vec3> _turnLine = null!;
    private List<Vec3> _crop = null!;

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
        c.Tool.IsHeadlandSectionControl = true;
        c.Guidance.UTurnRadius = 4.0;
        c.Guidance.UTurnExtension = 16.0;
        c.Guidance.UTurnDistanceFromBoundary = 2.0;
        c.Guidance.UTurnStyle = 0;

        var offset = new PolygonOffsetService();
        _pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);
        _sm = new YouTurnStateMachine(
            new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, offset, c),
            _pathing, NullLogger<YouTurnStateMachine>.Instance, c);
        _track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));

        // 160 x 200 m, the crop 10 m inside the fence.
        var outer = new List<Vec2> { new(-60, -100), new(100, -100), new(100, 100), new(-60, 100) };
        _boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = outer.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            },
        };
        _turnLine = offset.CalculatePointHeadings(offset.CreateInwardOffset(outer, 2.0)!);
        _crop = offset.CalculatePointHeadings(new List<Vec2> { new(-50, -90), new(90, -90), new(90, 90), new(-50, 90) });
    }

    private void AddDitch(ObstacleAction atObstacle, bool hard = false)
    {
        var pts = new List<Vec2> { new(-56, DitchSouth), new(96, DitchSouth), new(96, DitchNorth), new(-56, DitchNorth) };
        _boundary.InnerBoundaries.Add(new BoundaryPolygon
        {
            Points = pts.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            AtObstacle = atObstacle,
            IsHard = hard,
        });
    }

    // Everything north of the ditch is worked.
    private static bool NorthOfTheDitch(double e, double n) => n > DitchNorth;

    // Pass 3 heading north in the piece north of the ditch, the north headland 30 m ahead. A turn
    // dropped is looked for again on the next cycle, so the cycles go on until a turn is planned
    // or the snake says what it found.
    private (string? Said, YouTurnWorkingState Turn) Tick(Func<double, double, bool> covered, Models.Track.Track? track = null)
    {
        var ctx = new YouTurnStateMachine.TickContext(
            new Position { Easting = PassX(3), Northing = 60, Heading = 0, Speed = 2.78 },
            track ?? _track, _boundary, _turnLine,
            UTurnSkipRows: 2, IsSkipWorkedMode: true,
            HeadlandCalculatedWidth: Headland, HeadlandDistance: Headland,
            IsPointCovered: covered, WorkHeadlandLine: _crop, PassEndLine: _crop);
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 3, IsHeadingSameWay = true };
        var turn = new YouTurnWorkingState { IsEnabled = true };
        string? said = null;
        for (int cycle = 0; cycle < 40 && turn.TurnPath == null; cycle++)
        {
            var effects = _sm.Tick(ctx, guidance, turn);
            said = effects.StatusMessage ?? said;
            if (said != null && said.Contains("still to do")) break;
        }
        return (said, turn);
    }

    [Test]
    public void The_scenario_every_pass_is_still_to_do_judged_from_its_end_at_the_headland()
    {
        AddDitch(ObstacleAction.GoRound);
        var at = new Position { Easting = PassX(3), Northing = 60 };
        Assert.Multiple(() =>
        {
            Assert.That(_pathing.IsPassWorked(_track, 6, 0, _boundary, _crop, NorthOfTheDitch), Is.False,
                "pass 6 is still to do on the whole: 54 of its 180 m are worked");
            Assert.That(_pathing.IsPieceDoneAtThisEnd(_track, 0, 6, 0, _boundary, _crop, at, true, 30, NorthOfTheDitch), Is.False,
                "from the north headland the end test walks pass 6 through the ditch, which is no U-turn obstacle");
        });
    }

    [Test]
    public void No_turn_onto_a_piece_worked_as_far_as_the_ditch_the_pass_is_turned_at()
    {
        AddDitch(ObstacleAction.GoRound);

        var (said, turn) = Tick(NorthOfTheDitch);

        Assert.That(turn.TurnPath, Is.Null,
            $"no turn onto pass {turn.ReturnPassTargetPath}: it lands north of the ditch, where that pass is worked");
        Assert.That(said, Does.Contain("no room on the next pass at this end"));
    }

    [Test]
    public void A_piece_north_of_the_ditch_still_to_do_is_turned_onto()
    {
        AddDitch(ObstacleAction.GoRound);

        // Passes 4 and up are not worked north of the ditch either.
        var (said, turn) = Tick((e, n) => NorthOfTheDitch(e, n) && e < PassX(3) + ToolWidth / 2);

        Assert.That(turn.TurnPath, Is.Not.Null, $"a turn, not \"{said}\"");
        Assert.That(turn.ReturnPassTargetPath, Is.GreaterThan(3));
    }

    [Test]
    public void A_ditch_driven_through_does_not_end_the_piece()
    {
        AddDitch(ObstacleAction.DriveThrough);

        // The pass runs on through the ditch to its 120 m south of it, still to do: the turn that
        // lands on the worked 54 m north of it is made, as before.
        var (said, turn) = Tick(NorthOfTheDitch);

        Assert.That(turn.TurnPath, Is.Not.Null, $"a turn, not \"{said}\"");
    }

    // Pass 6 as a line across the field, a point every 2 m, and a turn that ends on it going south
    // at 80 m north.
    private static List<Vec3> Pass6 =>
        Enumerable.Range(0, 101).Select(i => new Vec3(PassX(6), -100 + 2.0 * i, 0)).ToList();

    private static List<Vec3> TurnEndingAt(double e, double n) => new()
    {
        new Vec3(e - 8, n + 4, 0), new Vec3(e - 4, n + 8, Math.PI / 2), new Vec3(e, n + 4, Math.PI), new Vec3(e, n, Math.PI),
    };

    [Test]
    public void The_piece_ahead_of_a_turn_ends_at_a_go_around_obstacle_the_pass_runs_into()
    {
        AddDitch(ObstacleAction.GoRound);

        var piece = _pathing.PieceAheadOfTurn(TurnEndingAt(PassX(6), 80), Pass6, _boundary, _crop, NorthOfTheDitch);

        Assert.Multiple(() =>
        {
            // From 80 m north down to where the pass is turned at before the ditch: the turn line a
            // U-turn obstacle has, the distance from boundary (2 m) out from its north edge at 36.
            Assert.That(piece.Crop, Is.InRange(42, 43));
            Assert.That(piece.Setback, Is.InRange(1, 2), "the crop between that line and the ditch");
            Assert.That(piece.Covered, Is.EqualTo(piece.Crop), "all of it is worked");
            Assert.That(piece.IsWorked, Is.True);
            Assert.That(piece.IsOpenAtLanding, Is.False);
        });
    }

    // A turn that ends on pass 6 going south at the given northing.
    private YouTurnPathingService.PieceCover SouthFrom(double n, Func<double, double, bool> covered) =>
        _pathing.PieceAheadOfTurn(TurnEndingAt(PassX(6), n), Pass6, _boundary, _crop, covered);

    [Test]
    public void The_crop_before_a_ditch_the_pass_is_turned_at_is_not_part_of_the_piece()
    {
        // A hard ditch is turned at 4 m before it (the distance from boundary and half the 4 m
        // implement): at 40 m north. A turn that ends at 46: 7 m of crop to drive, 43 to 46 worked.
        // The 3 m from the turn line to the ditch are never driven on this pass: counted, 4 of 10
        // m covered made the piece one still to do, and the turn onto it was made.
        AddDitch(ObstacleAction.GoRound, hard: true);

        var piece = SouthFrom(46, (e, n) => n > 42.5);

        Assert.Multiple(() =>
        {
            Assert.That(piece.Crop, Is.InRange(6, 7));
            Assert.That(piece.Covered, Is.EqualTo(4));
            Assert.That(piece.Setback, Is.InRange(3, 4));
            Assert.That(piece.IsWorked, Is.True);
            Assert.That(piece.IsOpenAtLanding, Is.False);
        });
    }

    [Test]
    public void A_turn_that_ends_in_the_room_before_the_ditch_has_nothing_to_work()
    {
        AddDitch(ObstacleAction.GoRound, hard: true);

        // Ends 3 m from the ditch, inside the 4 m the pass is turned at before it.
        var piece = SouthFrom(39, (e, n) => false);

        Assert.Multiple(() =>
        {
            Assert.That(piece.Crop, Is.Zero);
            Assert.That(piece.Setback, Is.InRange(3, 4));
            Assert.That(piece.IsWorked, Is.True, "nothing of it is driven: not a piece to turn onto");
            Assert.That(piece.IsOpenAtLanding, Is.False);
        });
    }

    [Test]
    public void The_end_the_turn_lands_at_stops_where_the_pass_leaves_the_crop()
    {
        AddDitch(ObstacleAction.GoRound);
        // The crop with a bay 10 m deep across pass 6, from 60 to 70 m north. A turn that ends at
        // 78: 8 m of crop, the bay, then 22 m down to the ditch's turn line. The 8 m are worked, the
        // first 12 m after the bay are not, the rest is: the end the turn lands at is the 8 m, all
        // worked. Counted on through the bay, 8 of its "first 20 m" made it an end left to do.
        var crop = new PolygonOffsetService().CalculatePointHeadings(new List<Vec2>
        {
            new(-50, -90), new(90, -90), new(90, 60), new(10, 60), new(10, 70), new(90, 70), new(90, 90), new(-50, 90),
        });

        var piece = _pathing.PieceAheadOfTurn(TurnEndingAt(PassX(6), 78), Pass6, _boundary, crop, (e, n) => n > 70 || n < 47.5);

        Assert.Multiple(() =>
        {
            Assert.That(piece.EndCrop, Is.InRange(7, 9));
            Assert.That(piece.EndCovered, Is.EqualTo(piece.EndCrop));
            Assert.That(piece.Crop, Is.InRange(28, 32));
            Assert.That(piece.IsWorked, Is.True);
            Assert.That(piece.IsOpenAtLanding, Is.False);
        });
    }

    [Test]
    public void On_a_curve_that_stops_in_the_field_the_piece_is_walked_along_its_run_on()
    {
        // A curve up the field that stops 30 m short of the fence at each end: it is driven on
        // straight past its ends, and a turn at the north headland ends on that run-on, 20 m past
        // the curve's last point. Walked along the curve's own points only, that turn did not end
        // on the pass, nothing was judged, and the turn onto the worked piece was made.
        AddDitch(ObstacleAction.GoRound);
        var curve = Models.Track.Track.FromCurve("Short",
            new List<Vec3> { new(0, -70, 0), new(0, 0, 0), new(0, 70, 0) }, isClosed: false);

        var (said, turn) = Tick(NorthOfTheDitch, curve);

        Assert.That(turn.TurnPath, Is.Null, "no turn onto a piece already worked");
        Assert.That(said, Does.Contain("no room on the next pass at this end"));
    }

    [Test]
    public void The_piece_ahead_of_a_turn_ends_at_a_u_turn_obstacle_s_turn_line()
    {
        AddDitch(ObstacleAction.Turn);

        var piece = _pathing.PieceAheadOfTurn(TurnEndingAt(PassX(6), 80), Pass6, _boundary, _crop, NorthOfTheDitch);

        // The turn line is the distance from boundary, 2 m, out from the ditch: 80 down to 38.
        Assert.That(piece.Crop, Is.InRange(42, 43));
    }

    [Test]
    public void The_piece_ahead_of_a_turn_runs_on_through_an_obstacle_driven_through()
    {
        AddDitch(ObstacleAction.DriveThrough);

        var piece = _pathing.PieceAheadOfTurn(TurnEndingAt(PassX(6), 80), Pass6, _boundary, _crop, NorthOfTheDitch);

        Assert.Multiple(() =>
        {
            // 80 m north down to the crop's south edge at -90, less the ditch's own 6 m.
            Assert.That(piece.Crop, Is.InRange(162, 166));
            Assert.That(piece.Covered, Is.InRange(44, 45), "only the piece north of the ditch is worked");
            Assert.That(piece.IsWorked, Is.False, "the pass runs on to its 120 m south of the ditch");
        });
    }

    [Test]
    public void The_piece_ahead_is_walked_the_way_the_turn_ends()
    {
        AddDitch(ObstacleAction.GoRound);
        // A turn that ends going north at 50 m north: 40 m of crop to the headland line at 90.
        var north = new List<Vec3>
        {
            new(PassX(6) + 8, 46, Math.PI), new(PassX(6) + 4, 42, -Math.PI / 2), new(PassX(6), 46, 0), new(PassX(6), 50, 0),
        };

        var piece = _pathing.PieceAheadOfTurn(north, Pass6, _boundary, _crop, NorthOfTheDitch);

        Assert.That(piece.Crop, Is.InRange(40, 41));
    }

    [Test]
    public void A_turn_that_does_not_end_on_the_pass_has_no_piece_ahead()
    {
        AddDitch(ObstacleAction.GoRound);

        var piece = _pathing.PieceAheadOfTurn(TurnEndingAt(PassX(6) + 3, 80), Pass6, _boundary, _crop, NorthOfTheDitch);

        Assert.That(piece, Is.EqualTo(default(YouTurnPathingService.PieceCover)));
    }

    [Test]
    public void A_worked_piece_left_to_do_where_the_turn_lands_is_open_there()
    {
        AddDitch(ObstacleAction.GoRound);
        // Pass 6 north of the ditch: its 14 m next to the headland left, the 30 m by the ditch worked.
        var piece = _pathing.PieceAheadOfTurn(TurnEndingAt(PassX(6), 80), Pass6, _boundary, _crop, (e, n) => n > DitchNorth && n < 66);

        Assert.Multiple(() =>
        {
            Assert.That(piece.IsWorked, Is.True, "30 of its 44 m are covered");
            Assert.That(piece.IsOpenAtLanding, Is.True, "14 of the 20 m where the turn lands are left");
        });
    }

    [Test]
    public void A_piece_left_to_do_where_the_turn_lands_is_turned_onto()
    {
        AddDitch(ObstacleAction.GoRound);

        // North of the ditch the passes east of the tractor's are worked by the ditch but not for
        // their 30 m next to the north headland.
        var (said, turn) = Tick((e, n) => NorthOfTheDitch(e, n) && (e < PassX(3) + ToolWidth / 2 || n < 60));

        Assert.That(turn.TurnPath, Is.Not.Null, $"a turn, not \"{said}\"");
        Assert.That(turn.ReturnPassTargetPath, Is.GreaterThan(3));
    }
}
