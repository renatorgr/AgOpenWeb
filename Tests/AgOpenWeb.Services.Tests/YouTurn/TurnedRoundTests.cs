// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
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
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// A turn planned for the end the tractor was heading to, and the tractor turned round on the
/// pass (by hand, or the simulator's turn-around): the turn is dropped and the one at the end
/// ahead planned. On Tapada da Casa (simulator) the turn planned at the south-west end stayed
/// after the turn-around; at the north-east end the headland stop and the fence guard took it for
/// a turn planned there and left AutoSteer on in the headland, heading for the fence. Fork only.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class TurnedRoundTests
{
    private const double ToolWidth = 4.0;
    private const double PassEasting = 2.0; // pass 0

    private YouTurnStateMachine _sm = null!;
    private PolygonOffsetService _offset = null!;
    private Models.Track.Track _track = null!;

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
        c.Guidance.UTurnRadius = 8.0;
        c.Guidance.UTurnExtension = 16.0;
        c.Guidance.UTurnDistanceFromBoundary = 2.0;
        c.Guidance.UTurnStyle = 0;

        _offset = new PolygonOffsetService();
        _sm = new YouTurnStateMachine(
            new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, _offset, c),
            new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c),
            NullLogger<YouTurnStateMachine>.Instance, c);
        _track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
    }

    private static Boundary Fence(bool hard)
    {
        var outer = new List<Vec2> { new(-50, -50), new(50, -50), new(50, 50), new(-50, 50) };
        return new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = outer.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                IsHard = hard,
            },
        };
    }

    private List<Vec3> Cultivated(double south, double north) =>
        _offset.CalculatePointHeadings(new List<Vec2> { new(-48, south), new(48, south), new(48, north), new(-48, north) });

    private YouTurnStateMachine.TickContext Ctx(Boundary boundary, List<Vec3> cultivated, double northing, double headingDeg) => new(
        new Position { Easting = PassEasting, Northing = northing, Heading = headingDeg, Speed = 2.78 },
        _track, boundary, cultivated,
        UTurnSkipRows: 1, IsSkipWorkedMode: false,
        HeadlandCalculatedWidth: 10.0, HeadlandDistance: 10.0);

    [Test]
    public void Turned_round_the_turn_at_the_end_behind_is_dropped_and_the_one_ahead_planned()
    {
        var boundary = Fence(hard: false);
        var cultivated = Cultivated(-40, 40);
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 0, IsHeadingSameWay = false };
        var turn = new YouTurnWorkingState { IsEnabled = true };

        _sm.Tick(Ctx(boundary, cultivated, -10, 180), guidance, turn);
        Assert.That(turn.ArcStart?.Northing, Is.LessThan(-20), "the scenario: a turn planned at the south end");

        // Turned round, heading north.
        _sm.Tick(Ctx(boundary, cultivated, -10, 0), guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Not.Null);
            Assert.That(turn.ArcStart?.Northing, Is.GreaterThan(20), "the turn at the north end, ahead");
        });
    }

    [Test]
    public void Turned_round_towards_an_end_with_no_turn_the_headland_stop_sees_no_turn()
    {
        // A hard fence 2 m past the north headland line, too close for a 16 m loop, and a 20 m
        // headland at the south end, where the turn fits.
        var boundary = Fence(hard: true);
        var cultivated = Cultivated(-30, 48);
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 0, IsHeadingSameWay = false };
        var turn = new YouTurnWorkingState { IsEnabled = true };

        _sm.Tick(Ctx(boundary, cultivated, 0, 180), guidance, turn);
        Assert.That(turn.ArcStart?.Northing, Is.LessThan(-20), "the scenario: a turn planned at the south end");

        // Turned round: no turn fits at the north end.
        _sm.Tick(Ctx(boundary, cultivated, 36, 0), guidance, turn);
        Assert.That(turn.TurnPath, Is.Null, "the south turn is not kept");

        // A metre into the north headland: the end of the pass with no turn, so AutoSteer goes off.
        var inHeadland = Ctx(boundary, cultivated, 49, 0);
        _sm.Tick(inHeadland, guidance, turn);
        Assert.That(_sm.EndOfPassWithoutTurn(in inHeadland, guidance, turn), Is.True);
    }

    [Test]
    public void Going_on_the_same_way_keeps_the_planned_turn()
    {
        var boundary = Fence(hard: false);
        var cultivated = Cultivated(-40, 40);
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 0, IsHeadingSameWay = false };
        var turn = new YouTurnWorkingState { IsEnabled = true };

        _sm.Tick(Ctx(boundary, cultivated, -10, 180), guidance, turn);
        var planned = turn.TurnPath;
        Assert.That(planned, Is.Not.Null);

        for (int i = 1; i <= 10; i++)
            _sm.Tick(Ctx(boundary, cultivated, -10 - i, 180 + (i % 2 == 0 ? 3 : -3)), guidance, turn);

        Assert.That(turn.TurnPath, Is.SameAs(planned), "not dropped or built again on the same way");
    }
}
