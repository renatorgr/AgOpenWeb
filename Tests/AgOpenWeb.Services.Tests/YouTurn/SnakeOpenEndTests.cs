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
/// The snake order (skip worked passes) counts a pass worked when at least half of it is covered
/// on the job's map. A pass left short of the end ahead is still to do there: the turn goes onto
/// it. On Tapada da Casa 27R was 71 % covered with its last 100 m at the north-east end left, and
/// the turn from 29R at that end found no pass to go to. Fork only.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class SnakeOpenEndTests
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

    private YouTurnStateMachine.TickContext Ctx(double e, double n, double headingDeg, Func<double, double, bool> covered) => new(
        new Position { Easting = e, Northing = n, Heading = headingDeg, Speed = 2.78 },
        _track, _boundary, _square,
        UTurnSkipRows: 1, IsSkipWorkedMode: true,
        HeadlandCalculatedWidth: 10.0, HeadlandDistance: 10.0,
        IsPointCovered: covered);

    private (YouTurnEffects Effects, YouTurnWorkingState Turn) DriveSouthOnPass6(Func<double, double, bool> covered)
    {
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 6, IsHeadingSameWay = false };
        var turn = new YouTurnWorkingState { IsEnabled = true };
        var effects = _sm.Tick(Ctx(PassX(6), -25, 180, covered), guidance, turn);
        return (effects, turn);
    }

    [Test]
    public void A_pass_worked_on_the_whole_but_left_short_of_this_end_gets_the_turn()
    {
        // Pass 4: covered from 10 m south of the middle to the north edge (62 %), its south 30 m left.
        var covered = AllCoveredBut(n => n < -10);
        Assert.That(_pathing.IsPassWorked(_track, 4, 0, _boundary, _square, covered), Is.True,
            "the scenario: pass 4 counts as worked on the whole");

        var (effects, turn) = DriveSouthOnPass6(covered);

        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Not.Null, $"a turn, not \"{effects.StatusMessage}\"");
            Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(4));
            Assert.That(effects.TurnCreationFailedSound, Is.False);
        });
    }

    [Test]
    public void Sections_off_a_little_early_at_the_headland_leave_the_pass_worked()
    {
        // Pass 4 covered but its last 2 m at the south edge.
        var covered = AllCoveredBut(n => n < -38);

        var (effects, turn) = DriveSouthOnPass6(covered);

        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Null);
            Assert.That(effects.StatusMessage, Is.EqualTo("Field complete — all tracks worked"));
        });
    }

    [Test]
    public void A_pass_left_short_of_the_other_end_is_not_turned_onto_at_this_one()
    {
        // Pass 4's north 30 m left: still worked at the south end, where the tractor turns.
        var covered = AllCoveredBut(n => n > 10);

        var (effects, turn) = DriveSouthOnPass6(covered);

        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Null);
            Assert.That(effects.StatusMessage, Is.EqualTo("Field complete — all tracks worked"));
        });
    }

    [Test]
    public void With_the_turn_line_the_open_end_is_measured_from_the_headland_line()
    {
        // Official #816: the U-turn gets the turn line (here 2 m in from the fence, ±48) and the
        // headland (±30, 20 m wide) is left to the sections, so every pass is uncovered for the
        // 18 m between them. Measured from the turn line, the 20 m next to the end were that band,
        // and every worked pass looked open at this end; measured from the headland line they are
        // the crop. The pipeline gives the headland as PassEndLine.
        var offset = new PolygonOffsetService();
        var turnLine = offset.CalculatePointHeadings(new List<Vec2> { new(-48, -48), new(48, -48), new(48, 48), new(-48, 48) });
        var headland = offset.CalculatePointHeadings(new List<Vec2> { new(-30, -30), new(30, -30), new(30, 30), new(-30, 30) });
        Func<double, double, bool> cropWorked = (e, n) => Math.Abs(n) < 30;
        var position = new Position { Easting = PassX(6), Northing = -25, Heading = 180, Speed = 2.78 };
        Assert.That(_pathing.IsPassOpenAtThisEnd(_track, 0, 4, 0, _boundary, turnLine, position,
            headingSameWay: false, headlandAhead: 23, cropWorked), Is.True, "the scenario: from the turn line pass 4 looks open");

        var ctx = new YouTurnStateMachine.TickContext(position, _track, _boundary, turnLine,
            UTurnSkipRows: 1, IsSkipWorkedMode: true, HeadlandCalculatedWidth: 20.0, HeadlandDistance: 20.0,
            IsPointCovered: cropWorked, WorkHeadlandLine: headland, PassEndLine: headland);
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 6, IsHeadingSameWay = false };
        var turn = new YouTurnWorkingState { IsEnabled = true };
        var effects = _sm.Tick(ctx, guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Null, $"no turn onto a worked pass (to {turn.ReturnPassTargetPath})");
            Assert.That(effects.StatusMessage, Is.EqualTo("Field complete — all tracks worked"));
        });
    }

    [Test]
    public void A_pass_left_to_do_beyond_where_the_turn_lands_on_it_is_worked_at_this_end()
    {
        // The fence reaches 20 m further south west of x = 22 (a corner of the field), so pass 4's
        // own end is 20 m past the turn line ahead of the tractor on pass 6. Pass 4 is covered down
        // to n = -52 and left below that, at its end. A turn here lands on pass 4 abreast of the
        // turn line at -48 and drives north over the covered part: the end stays as it was, and the
        // whole pass would be driven again for nothing (a tester's field, simulator: 2R driven twice).
        var fence = new List<Vec2> { new(-50, -70), new(22, -70), new(22, -50), new(50, -50), new(50, 50), new(-50, 50) };
        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            },
        };
        // The turn line: the fence 2 m in.
        var turnLine = new PolygonOffsetService().CalculatePointHeadings(new List<Vec2>
            { new(-48, -68), new(20, -68), new(20, -48), new(48, -48), new(48, 48), new(-48, 48) });
        var covered = AllCoveredBut(n => n < -52);
        var position = new Position { Easting = PassX(6), Northing = -20, Heading = 180, Speed = 2.78 };
        Assert.That(_pathing.IsPassOpenAtThisEnd(_track, 0, 4, 0, boundary, turnLine, position,
            headingSameWay: false, headlandAhead: 28, covered), Is.True, "the scenario: pass 4 is left to do at its south end");

        var ctx = new YouTurnStateMachine.TickContext(position, _track, boundary, turnLine,
            UTurnSkipRows: 1, IsSkipWorkedMode: true, HeadlandCalculatedWidth: 10.0, HeadlandDistance: 10.0,
            IsPointCovered: covered);
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 6, IsHeadingSameWay = false };
        var turn = new YouTurnWorkingState { IsEnabled = true };
        _sm.Tick(ctx, guidance, turn);
        var effects = _sm.Tick(ctx, guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Null, $"no turn onto pass {turn.ReturnPassTargetPath}");
            Assert.That(effects.StatusMessage, Is.EqualTo("Field complete — all tracks worked"));
        });
    }

    [Test]
    public void A_turn_that_lands_on_the_part_left_to_do_reaches_it()
    {
        var turnPath = Enumerable.Range(0, 30).Select(i => new Vec3(PassX(4), -38 + i, 0)).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(_pathing.TurnReachesUnworkedGround(turnPath, _boundary, _square, AllCoveredBut(n => n < -10)),
                Is.True, "left below -10: the turn's last 20 m are on it");
            Assert.That(_pathing.TurnReachesUnworkedGround(turnPath, _boundary, _square, AllCoveredBut(n => n < -40)),
                Is.False, "left below the field's edge here: nothing of it is reached");
            Assert.That(_pathing.TurnReachesUnworkedGround(turnPath, _boundary, _square, null), Is.True, "no map");
        });
    }

    [Test]
    public void The_stretch_at_this_end_is_measured_from_where_the_pass_leaves_the_field()
    {
        var position = new Position { Easting = PassX(6), Northing = -25, Heading = 180 };
        var leftSouth = AllCoveredBut(n => n < -10);
        var leftNorth = AllCoveredBut(n => n > 10);

        Assert.Multiple(() =>
        {
            Assert.That(_pathing.IsPassOpenAtThisEnd(_track, 0, 4, 0, _boundary, _square, position,
                headingSameWay: false, headlandAhead: 15, leftSouth), Is.True, "south end, left there");
            Assert.That(_pathing.IsPassOpenAtThisEnd(_track, 0, 4, 0, _boundary, _square, position,
                headingSameWay: false, headlandAhead: 15, leftNorth), Is.False, "south end, left at the north");
            // Heading north, the end ahead is the north one.
            var north = new Position { Easting = PassX(6), Northing = 25, Heading = 0 };
            Assert.That(_pathing.IsPassOpenAtThisEnd(_track, 0, 4, 0, _boundary, _square, north,
                headingSameWay: true, headlandAhead: 15, leftNorth), Is.True, "north end, left there");
            // The point abreast of the end is inside (the headland is still 40 m ahead): the edge is found.
            Assert.That(_pathing.IsPassOpenAtThisEnd(_track, 0, 4, 0, _boundary, _square, north,
                headingSameWay: true, headlandAhead: 0, leftNorth), Is.True, "found forward from inside");
            Assert.That(_pathing.IsPassOpenAtThisEnd(_track, 0, 4, 0, _boundary, _square, position,
                headingSameWay: false, headlandAhead: 15, null), Is.False, "no map");
        });
    }
}
