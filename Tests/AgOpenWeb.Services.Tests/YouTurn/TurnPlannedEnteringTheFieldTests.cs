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
/// AutoSteer engaged in the headland facing the field, on an AB line: the first turn is planned as
/// the pivot crosses the turn line into the field, for the far end of the pass. The search for the
/// end of the pass started at the pass point nearest the pivot, and an AB line's points are about
/// 2 m apart: in the first metre past the turn line that point was still behind the tractor,
/// outside the turn area, and the crossing the tractor had just driven over was taken for the end
/// of the pass. The turn built there failed, so beside a hard fence the operator was told "U-turn
/// blocked: implement would swing into a hard boundary - take over manually", with the creation
/// failed sound, at the start of the first pass; beside a soft one the simple fallback was planned
/// instead of the turn. Driven on, the turn at the far end was planned and made. Seen on testers'
/// fields with an 8 m front boom and a 20 m spreader (the farm job harness, TAKE-OVER-AT-START);
/// this field is made up. A turn that really can't keep the implement off the fence still warns.
/// Fork only.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class TurnPlannedEnteringTheFieldTests
{
    // A 200 x 300 m field, the AB line x = 0 running north through it.
    private const double FenceSouth = -150, FenceNorth = 150, FenceHalfWidth = 100;
    private const double TurnDistance = 2.0;
    private const double HeadlandWidth = 12.0;

    private PolygonOffsetService _offset = null!;
    private YouTurnStateMachine _sm = null!;
    private Models.Track.Track _track = null!;
    private double _width;

    public enum Rig { FrontBoom8m, RearSpreader20m }

    private void Setup(Rig rig)
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 2.9;
        c.Vehicle.MaxSteerAngle = 32;
        c.Tool.Overlap = 0;
        c.Tool.Offset = 0;
        c.Tool.WorkingPointFromHitch = 0;
        c.Tool.IsToolTrailing = false;
        c.Tool.IsToolTBT = false;
        if (rig == Rig.FrontBoom8m)
        {
            // An 8 m boom fixed 4.5 m ahead of the pivot, in five sections.
            _width = 8;
            c.Vehicle.ThreePointHitchLength = c.Vehicle.FrontHitchLength = 4.5;
            c.Tool.IsToolFrontFixed = true;
            c.Tool.IsToolRearFixed = false;
            c.NumSections = 5;
            int[] cm = { 200, 100, 200, 100, 200 };
            for (int i = 0; i < cm.Length; i++) c.Tool.SetSectionWidth(i, cm[i]);
        }
        else
        {
            // A mounted spreader working 20 m in two halves: passes wider than twice the radius
            // (the wide turn).
            _width = 20;
            c.Vehicle.ThreePointHitchLength = c.Vehicle.FrontHitchLength = 1.5;
            c.Tool.IsToolFrontFixed = false;
            c.Tool.IsToolRearFixed = true;
            c.NumSections = 2;
            c.Tool.SetSectionWidth(0, 1000);
            c.Tool.SetSectionWidth(1, 1000);
        }
        c.Tool.Width = _width;
        c.Guidance.UTurnRadius = 8.0;
        c.Guidance.UTurnExtension = 20.0;
        c.Guidance.UTurnDistanceFromBoundary = TurnDistance;
        c.Guidance.UTurnStyle = 0;

        _offset = new PolygonOffsetService();
        _sm = new YouTurnStateMachine(
            new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, _offset, c),
            new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c),
            NullLogger<YouTurnStateMachine>.Instance, c);
        _track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
    }

    private static List<Vec2> FencePoints() => new()
    {
        new(-FenceHalfWidth, FenceSouth), new(FenceHalfWidth, FenceSouth),
        new(FenceHalfWidth, FenceNorth), new(-FenceHalfWidth, FenceNorth),
    };

    private static Boundary Field(bool hard) => new()
    {
        OuterBoundary = new BoundaryPolygon
        {
            Points = FencePoints().Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            IsHard = hard,
        },
    };

    private List<Vec3> Inset(double d) => _offset.CalculatePointHeadings(_offset.CreateInwardOffset(FencePoints(), d)!);

    /// <summary>Pass 0, half a pass right of the AB line, heading north.</summary>
    private YouTurnStateMachine.TickContext Ctx(Boundary field, List<Vec3> turnLine, List<Vec3> headland, double northing) => new(
        new Position { Easting = _width / 2, Northing = northing, Heading = 0, Speed = 2.2 },
        _track, field, turnLine,
        UTurnSkipRows: 0, IsSkipWorkedMode: false,
        HeadlandCalculatedWidth: HeadlandWidth, HeadlandDistance: HeadlandWidth,
        WorkHeadlandLine: headland, PassEndLine: headland);

    /// <summary>
    /// Drives pass 0 north from <paramref name="from"/> to <paramref name="to"/> in 0.2 m steps (a
    /// 10 Hz fix at 8 km/h), and returns every "take over" said and every creation failed sound.
    /// </summary>
    private List<string> Drive(Boundary field, GuidanceWorkingState guidance, YouTurnWorkingState turn, double from, double to)
    {
        var turnLine = Inset(TurnDistance);
        var headland = Inset(HeadlandWidth);
        var told = new List<string>();
        for (double y = from; y <= to; y += 0.2)
        {
            var effects = _sm.Tick(Ctx(field, turnLine, headland, y), guidance, turn);
            if (effects.TurnCreationFailedSound)
                told.Add($"y = {y:F1}: creation failed sound ({effects.StatusMessage})");
            else if (effects.StatusMessage?.Contains("take over", StringComparison.OrdinalIgnoreCase) == true)
                told.Add($"y = {y:F1}: {effects.StatusMessage}");
        }
        return told;
    }

    [TestCase(Rig.FrontBoom8m, true, TestName = "Entering the field the turn at the far end is planned, no blocked warning (front 8 m boom, hard fence)")]
    [TestCase(Rig.RearSpreader20m, true, TestName = "Entering the field the turn at the far end is planned, no blocked warning (20 m spreader, hard fence)")]
    [TestCase(Rig.FrontBoom8m, false, TestName = "Entering the field the turn at the far end is planned, not the fallback (front 8 m boom, soft fence)")]
    public void Entering_the_field_the_turn_at_the_far_end_is_planned(Rig rig, bool hard)
    {
        Setup(rig);
        var field = Field(hard);
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 0, IsHeadingSameWay = true };
        var turn = new YouTurnWorkingState { IsEnabled = true };

        // From 6 m outside the fence, across it and the turn line, 50 m into the field.
        var told = Drive(field, guidance, turn, FenceSouth - 6, FenceSouth + 50);

        Assert.That(told, Is.Empty, "nothing to take over at the start of the pass");
        Assert.That(turn.TurnPath, Is.Not.Null, "a turn is planned");
        Assert.Multiple(() =>
        {
            Assert.That(turn.ArcStart, Is.Not.Null, "the turn, not the simple fallback");
            Assert.That(turn.ArcStart!.Value.Northing, Is.GreaterThan(FenceNorth - 50), "at the far end of the pass");
            Assert.That(turn.TurnPath!.Min(p => p.Northing), Is.GreaterThan(FenceSouth + 50), "the whole turn at the far end");
        });
    }

    [Test]
    public void A_turn_at_the_far_end_planned_too_late_to_keep_the_boom_off_a_hard_fence_still_warns()
    {
        // AutoSteer engaged 15 m before the far turn line: the turn that keeps the 8 m front boom
        // off the fence starts behind the tractor, so there is none and the operator is told.
        Setup(Rig.FrontBoom8m);
        var field = Field(hard: true);
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 0, IsHeadingSameWay = true };
        var turn = new YouTurnWorkingState { IsEnabled = true };

        double turnLineNorth = FenceNorth - TurnDistance;
        var told = Drive(field, guidance, turn, turnLineNorth - 15, turnLineNorth - 14);

        Assert.That(turn.TurnPath, Is.Null, "no turn");
        Assert.That(told, Has.Some.Contains("creation failed sound").And.Some.Contains("implement would swing into a hard boundary"),
            "the operator is told, with the sound");
    }
}
