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
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// The wide turn (rows wider than twice the turn radius) runs along the turn line from the end of
/// the tractor's pass to the end of the next pass, so where the next pass "ends" decides the turn.
/// <para>
/// With a large distance from boundary the next pass runs outside the turn line, and inside the
/// fence, wherever the fence comes in beside it. The tractor's own pass is driven through such a
/// stretch (<see cref="PassEnds.Find"/>); the next pass was taken to end at the first one, walking
/// from abreast of the tractor, and the turn ran along the turn line all the way back to it: on a
/// tester's field (an 8 m front boom, radius 5.4 m, 6.3 m from the boundary, one row skipped) 300 m
/// round a corner, refused as malformed with the next pass's real end 16 m from the turn's start.
/// </para>
/// <para>
/// And where the next pass really does run on far past the end of the tractor's (this pass ends at
/// a notch of the fence the next one runs by), the wide turn along the turn line is no U-turn: the
/// omega turn, abreast, is tried before the turn is refused.
/// </para>
/// </summary>
[TestFixture, NonParallelizable] // ConfigurationStore is a singleton.
public class WideTurnNextPassEndTests
{
    private const double FenceNorth = 100;
    private const double FromBoundary = 6.3;

    // The west fence comes in to x = -17 from y = -15 to 15, with 45° sides: the next pass
    // (x = -12) runs past it 5 m from the fence, outside the turn line and inside the field.
    private static List<Vec2> FenceWithBulge(bool bulge) => bulge
        ? new() { new(-60, -100), new(60, -100), new(60, FenceNorth), new(-60, FenceNorth), new(-60, 58), new(-17, 15), new(-17, -15), new(-60, -58) }
        : new() { new(-60, -100), new(60, -100), new(60, FenceNorth), new(-60, FenceNorth) };

    // A 15 m notch of the fence comes in from the east at y = 60 as far as x = -2: the tractor's
    // pass (x = 4) ends at it, the next pass (x = -12) runs by it and on to y = 300. The turn line
    // between the two ends goes round the notch, up the east side and along the north end.
    private static List<Vec2> FenceWithNotch() => new()
    {
        new(-60, -100), new(60, -100), new(60, 60), new(-2, 60), new(-2, 75), new(60, 75), new(60, 300), new(-60, 300),
    };

    private static YouTurnCreationService NewCreation(ListLogger log)
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        // A front sprayer: an 8 m boom fixed 4.7 m ahead of the pivot.
        const double width = 8;
        c.Vehicle.Wheelbase = 2.9;
        c.Tool.Width = width;
        c.Tool.Overlap = 0;
        c.Tool.Offset = 0;
        c.Vehicle.ThreePointHitchLength = c.Vehicle.FrontHitchLength = 4.7;
        c.Tool.WorkingPointFromHitch = 0;
        c.Tool.IsToolFrontFixed = true;
        c.Tool.IsToolRearFixed = false;
        c.Tool.IsToolTrailing = false;
        c.Tool.IsToolTBT = false;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, (int)(width * 100));
        c.Guidance.UTurnDistanceFromBoundary = FromBoundary;
        c.Guidance.UTurnRadius = 5.4;
        c.Guidance.UTurnExtension = 11.0;
        c.Guidance.UTurnStyle = 0;
        return new YouTurnCreationService(log, new PolygonOffsetService(), c);
    }

    private static Boundary Field(List<Vec2> fence, bool hard) => new()
    {
        OuterBoundary = new BoundaryPolygon
        {
            Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            IsHard = hard,
        },
    };

    /// <summary>Pass 0 (x = 4) north from y = -70, one row skipped: the next pass is x = -12, to the left.</summary>
    private static YouTurnCreationService.TurnPathResult TurnFromTheSouth(Boundary field, double passEndNorth, ListLogger? log = null)
    {
        const double y = -70;
        var creation = NewCreation(log ?? new ListLogger());
        var turnLine = new PolygonOffsetService().CreateInwardOffset(
            field.OuterBoundary!.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList(), FromBoundary)!
            .Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList();
        var track = AgOpenWeb.Models.Track.Track.FromABLine("AB", new Vec3(0, -200, 0), new Vec3(0, 400, 0));
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = true, HowManyPathsAway = 0 };
        var turn = new YouTurnWorkingState { IsTurnLeft = true, DistanceToHeadland = passEndNorth - FromBoundary - y };
        return creation.CreateTurnPath(new Position { Easting = 4, Northing = y, Heading = 0 }, track,
            headingRadians: 0, abHeading: 0, field, turnLine, guidance, turn,
            uTurnSkipRows: 1, headlandCalculatedWidth: 12, headlandDistance: passEndNorth - FromBoundary - y);
    }

    private static double Length(IReadOnlyList<Vec3> path)
    {
        double length = 0;
        for (int i = 1; i < path.Count; i++)
            length += Math.Sqrt(GeometryMath.DistanceSquared(path[i - 1], path[i]));
        return length;
    }

    [TestCase(true, TestName = "The next pass is not ended where it runs past a bulge of the fence (hard fence)")]
    [TestCase(false, TestName = "The next pass is not ended where it runs past a bulge of the fence (soft boundary)")]
    public void The_next_pass_is_driven_past_a_bulge_of_the_fence(bool hard)
    {
        var turnLine = new PolygonOffsetService().CreateInwardOffset(FenceWithBulge(bulge: true), FromBoundary)!;
        Assert.Multiple(() =>
        {
            Assert.That(GeometryMath.IsPointInPolygon(turnLine, new Vec2(-12, 0)), Is.False, "the scenario: beside the bulge the next pass is outside the turn line");
            Assert.That(GeometryMath.IsPointInPolygon(FenceWithBulge(bulge: true), new Vec2(-16, 0)), Is.True, "the scenario: and the implement's end is inside the fence");
            Assert.That(GeometryMath.IsPointInPolygon(turnLine, new Vec2(-12, -40)), Is.True, "the scenario: abreast of the tractor it is inside");
        });

        var plain = TurnFromTheSouth(Field(FenceWithBulge(bulge: false), hard), FenceNorth);
        var turn = TurnFromTheSouth(Field(FenceWithBulge(bulge: true), hard), FenceNorth);

        Assert.That(plain.Path, Is.Not.Null, "the scenario: a turn without the bulge");
        Assert.That(turn.Path, Is.Not.Null, "a turn");
        TestContext.Out.WriteLine($"without the bulge: {Length(plain.Path!):F0} m, top {plain.Path!.Max(p => p.Northing):F1}, lowest {plain.Path!.Min(p => p.Northing):F1}; "
            + $"with it: {Length(turn.Path!):F0} m, top {turn.Path!.Max(p => p.Northing):F1}, lowest {turn.Path!.Min(p => p.Northing):F1}");
        Assert.Multiple(() =>
        {
            Assert.That(turn.Path!.Min(p => p.Northing), Is.GreaterThan(30), "the turn stays at the end of the passes, not down the west side to the bulge");
            Assert.That(Length(turn.Path!), Is.EqualTo(Length(plain.Path!)).Within(2.0), "the same turn as without the bulge");
            Assert.That(turn.Path![^1].Easting, Is.EqualTo(-12).Within(0.5), "it lands on the next pass");
        });
    }

    [Test(Description = "A wide turn that runs round the field along the turn line to the far end of the next pass is not refused: the omega turn, abreast, is made")]
    public void A_wide_turn_that_is_no_U_turn_gives_way_to_the_omega_turn()
    {
        var log = new ListLogger();
        var turn = TurnFromTheSouth(Field(FenceWithNotch(), hard: true), passEndNorth: 60, log);

        Assert.That(turn.Path, Is.Not.Null, "a turn, not 'No U-turn fits inside the boundary at this end'");
        TestContext.Out.WriteLine($"{Length(turn.Path!):F0} m, top {turn.Path!.Max(p => p.Northing):F1}, ends ({turn.Path![^1].Easting:F1},{turn.Path![^1].Northing:F1})");
        Assert.Multiple(() =>
        {
            Assert.That(log.Lines, Has.Some.Contains("Wide turn malformed"), "the scenario: the wide turn was built first, and was no U-turn");
            Assert.That(turn.Path!.Max(p => p.Northing), Is.LessThan(60), "abreast, below the notch");
            Assert.That(turn.Path![^1].Easting, Is.EqualTo(-12).Within(0.5), "it lands on the next pass");
            Assert.That(Length(turn.Path!), Is.LessThan(120), "one U-turn, not a drive along the turn line");
        });
    }

    private sealed class ListLogger : ILogger<YouTurnCreationService>
    {
        public readonly List<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }
}
