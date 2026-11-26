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
using AgOpenWeb.Models.Tool;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.Track;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// A U-turn planned for a front implement's centre (<see cref="GuidanceConfig.UTurnForFrontTool"/>,
/// <c>YouTurnCreationService.FrontTool.cs</c>): the implement leaves one pass and joins the next
/// along them and stays inside the turn line, the tractor behind it never turning tighter than the
/// U-turn radius. Planned for the pivot (the setting off), the implement's centre runs past the
/// turn line and comes onto the next pass from beyond it. A tester's rig: an 8 m boom 4.7 m ahead
/// of the rear axle, a 5.4 m U-turn radius (26 Nov 2026). Fork only.
/// </summary>
[TestFixture, NonParallelizable] // ConfigurationStore is a singleton.
public class FrontToolTurnPlanTests
{
    private const double Width = 8.0;
    private const double Reach = 4.7;        // front hitch 4.5 m + working point 0.2 m
    private const double Radius = 5.4;
    private const double FromBoundary = 1.5;
    private const double Extension = 11.0;
    private const double HalfE = 150, HalfN = 150;
    private const double PassX = Width / 2;  // pass 0 of an AB line along x = 0
    private static readonly double ToolRadius = Math.Sqrt(Radius * Radius + Reach * Reach);

    private static YouTurnCreationService NewCreation(bool forTool, double reach = Reach, ListLogger? log = null)
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 2.9;
        c.Vehicle.FrontHitchLength = reach - 0.2;
        c.Tool.SetToolType("front");
        c.Tool.WorkingPointFromHitch = 0.2;
        c.Tool.Width = Width;
        c.Tool.Overlap = 0;
        c.Tool.Offset = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, (int)(Width * 100));
        c.Guidance.UTurnDistanceFromBoundary = FromBoundary;
        c.Guidance.UTurnRadius = Radius;
        c.Guidance.UTurnExtension = Extension;
        c.Guidance.UTurnStyle = 0;
        c.Guidance.UTurnForFrontTool = forTool;
        return new YouTurnCreationService(log ?? new ListLogger(), new PolygonOffsetService(), c);
    }

    private static List<Vec2> Fence() => new()
    {
        new(-HalfE, -HalfN), new(-HalfE, HalfN), new(HalfE, HalfN), new(HalfE, -HalfN),
    };

    /// <summary>
    /// The field with its part east of x = 12 ending at y = <paramref name="south"/>: the passes
    /// there (x = 20, 28, ...) are short, the ones west of it the field's whole length.
    /// </summary>
    private static List<Vec2> FenceWithShortPasses(double south) => new()
    {
        new(-HalfE, -HalfN), new(-HalfE, HalfN), new(HalfE, HalfN), new(HalfE, south), new(12, south), new(12, -HalfN),
    };

    private static Boundary Field(List<Vec2> fence, bool hard) => new()
    {
        OuterBoundary = new BoundaryPolygon { Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(), IsHard = hard },
    };

    private static List<Vec3> TurnLine(List<Vec2> fence) => new PolygonOffsetService().CreateInwardOffset(fence, FromBoundary)!
        .Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList();

    private static readonly AgOpenWeb.Models.Track.Track Line =
        AgOpenWeb.Models.Track.Track.FromABLine("AB", new Vec3(0, -300, 0), new Vec3(0, 300, 0));

    /// <summary>
    /// The turn at the south end of pass 2 (x = 20, driven south) of <see cref="FenceWithShortPasses"/>,
    /// to the left onto pass 4, planned with the tractor at <paramref name="at"/> on the pass.
    /// </summary>
    private static YouTurnCreationService.TurnPathResult TurnAtTheOtherEnd(YouTurnCreationService creation, double south, Vec3 at)
    {
        var fence = FenceWithShortPasses(south);
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = false, HowManyPathsAway = 2 };
        double toEnd = at.Northing - (south + FromBoundary);
        var turn = new YouTurnWorkingState { IsTurnLeft = true, DistanceToHeadland = toEnd, NextTrackTurnOffset = Width * 2 };
        return creation.CreateTurnPath(new Position { Easting = NextPassX(1), Northing = at.Northing, Heading = Math.PI }, Line,
            headingRadians: Math.PI, abHeading: 0, Field(fence, hard: true), TurnLine(fence), guidance, turn,
            uTurnSkipRows: 1, headlandCalculatedWidth: 24, headlandDistance: toEnd);
    }

    /// <summary>Where the tractor is on a turn's path when the line takes over: 4 m before its end.</summary>
    private static Vec3 LineTakesOver(IReadOnlyList<Vec3> path)
    {
        double left = 4.0;
        for (int i = path.Count - 1; i > 0; i--)
        {
            double d = GeometryMath.Distance(path[i - 1], path[i]);
            if (d >= left)
            {
                double f = left / d;
                return new Vec3(path[i].Easting + (path[i - 1].Easting - path[i].Easting) * f,
                    path[i].Northing + (path[i - 1].Northing - path[i].Northing) * f, path[i].Heading);
            }
            left -= d;
        }
        return path[0];
    }

    /// <summary>Pass 0 (x = 4) north from y = 60, the turn to the right with <paramref name="skip"/> rows skipped.</summary>
    private static YouTurnCreationService.TurnPathResult Turn(YouTurnCreationService creation, int skip, bool hard = false, double fromY = 60,
        List<Vec2>? fence = null)
    {
        fence ??= Fence();
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = true, HowManyPathsAway = 0 };
        double toEnd = HalfN - FromBoundary - fromY;
        var turn = new YouTurnWorkingState { IsTurnLeft = false, DistanceToHeadland = toEnd, NextTrackTurnOffset = Width * (skip + 1) };
        return creation.CreateTurnPath(new Position { Easting = PassX, Northing = fromY, Heading = 0 }, Line,
            headingRadians: 0, abHeading: 0, Field(fence, hard), TurnLine(fence), guidance, turn,
            uTurnSkipRows: skip, headlandCalculatedWidth: 24, headlandDistance: toEnd);
    }

    private static double NextPassX(int skip) => PassX + Width * (skip + 1);

    [TestCase(0, TestName = "Planned for the front implement, its centre keeps to the passes and the turn line (the next pass)")]
    [TestCase(1, TestName = "Planned for the front implement, its centre keeps to the passes and the turn line (one row skipped)")]
    [TestCase(3, TestName = "Planned for the front implement, its centre keeps to the passes and the turn line (three rows skipped)")]
    public void The_implement_s_centre_keeps_to_the_passes_and_the_turn_line(int skip)
    {
        var result = Turn(NewCreation(forTool: true), skip);
        Assert.That(result.Path, Is.Not.Null, "a turn");
        var pivot = result.Path!;
        var tool = RigGeometry.PathOfPointAhead(pivot, Reach);
        double next = NextPassX(skip);
        TestContext.Out.WriteLine(Describe(pivot, tool, next));

        Assert.Multiple(() =>
        {
            // Never past the next pass nor the turn line. Rows wider than the implement's circle:
            // between the two passes all the way; nearer than that, the circle's bulb is on this side.
            double bulb = Math.Max(0, 2 * ToolRadius - (next - PassX));
            Assert.That(tool.Min(p => p.Easting), Is.GreaterThan(PassX - bulb - 0.10), "the implement's centre left of its pass (or of its circle's bulb), m");
            if (bulb > 0)
                Assert.That(tool.Min(p => p.Easting), Is.LessThan(PassX - bulb + 0.5), "the bulb is the implement's circle and no wider");
            Assert.That(tool.Max(p => p.Easting), Is.LessThan(next + 0.10), "the implement's centre past the next pass, m");
            Assert.That(tool.Max(p => p.Northing), Is.LessThan(HalfN - FromBoundary + 0.10), "the implement's centre past the turn line, m");
            Assert.That(tool.Max(p => p.Northing), Is.GreaterThan(HalfN - FromBoundary - 0.6), "the turn is made at the turn line");
            // The tractor: on its pass until the turn, never tighter than the U-turn radius, and on
            // the next pass, straight, where the path ends.
            Assert.That(pivot[0].Easting, Is.EqualTo(PassX).Within(0.02), "the path starts on the pass");
            Assert.That(pivot.Max(p => p.Northing), Is.LessThan(tool.Max(p => p.Northing)), "the tractor stays inside the implement's arc");
            Assert.That(FrontToolLine.TightestBend(pivot), Is.GreaterThan(Radius * 0.95), "the tractor's tightest bend, m");
            Assert.That(pivot[^1].Easting, Is.EqualTo(next).Within(0.15), "the tractor is on the next pass where the path ends");
            Assert.That(Math.Abs(Math.IEEERemainder(pivot[^1].Heading - Math.PI, 2 * Math.PI)) * 180 / Math.PI, Is.LessThan(2.0),
                "the tractor's angle to the next pass where the path ends, degrees");
            // Where each leaves the pass: the tractor a reach before the implement.
            Assert.That(result.ToolLeavesPassAt, Is.Not.Null);
            Assert.That(result.ArcStart, Is.Not.Null);
            Assert.That(result.ToolLeavesPassAt!.Value.Northing - result.ArcStart!.Value.Northing, Is.EqualTo(Reach).Within(0.10));
            Assert.That(result.ArcStart!.Value.Easting, Is.EqualTo(PassX).Within(0.10));
        });
    }

    [TestCase(0)]
    [TestCase(1)]
    public void Planned_for_the_pivot_the_implement_s_centre_runs_past_the_turn_line_and_the_next_pass(int skip)
    {
        // The setting off: today's turn. What the setting is for.
        var result = Turn(NewCreation(forTool: false), skip);
        Assert.That(result.Path, Is.Not.Null, "a turn");
        var pivot = result.Path!;
        var tool = RigGeometry.PathOfPointAhead(pivot, Reach);
        double next = NextPassX(skip);
        TestContext.Out.WriteLine(Describe(pivot, tool, next));

        Assert.Multiple(() =>
        {
            Assert.That(pivot.Max(p => p.Northing), Is.LessThan(HalfN - FromBoundary + 0.10), "the pivot keeps inside the turn line");
            Assert.That(tool.Max(p => p.Northing), Is.GreaterThan(HalfN - FromBoundary + 1.0), "the implement's centre past the turn line, m");
            Assert.That(tool.Max(p => p.Easting), Is.GreaterThan(next + 1.0), "the implement's centre past the next pass, m");
            Assert.That(result.ToolLeavesPassAt, Is.Null);
        });
    }

    [Test]
    public void The_setting_off_leaves_the_turn_as_it_was()
    {
        var off = Turn(NewCreation(forTool: false), 1).Path!;
        // A rear implement with the setting on is planned for the pivot too.
        var creation = NewCreation(forTool: true);
        ConfigurationStore.Instance.Tool.SetToolType("rear");
        var rear = Turn(creation, 1).Path!;

        Assert.That(rear, Has.Count.EqualTo(off.Count));
        for (int i = 0; i < off.Count; i++)
        {
            Assert.That(GeometryMath.Distance(rear[i], off[i]), Is.LessThan(1e-9), $"point {i}");
            Assert.That(rear[i].Heading, Is.EqualTo(off[i].Heading).Within(1e-9), $"heading at point {i}");
        }
    }

    [Test]
    public void A_k_turn_is_planned_for_the_pivot()
    {
        var c = NewCreation(forTool: true);
        ConfigurationStore.Instance.Guidance.UTurnStyle = 1;
        Assert.That(YouTurnCreationService.PlannedTurnRadius(ConfigurationStore.Instance), Is.EqualTo(Radius));
        ConfigurationStore.Instance.Guidance.UTurnStyle = 0;
        Assert.That(YouTurnCreationService.PlannedTurnRadius(ConfigurationStore.Instance), Is.EqualTo(ToolRadius).Within(1e-9));
        Assert.That(c, Is.Not.Null);
    }

    [Test]
    public void Beside_a_hard_fence_the_whole_implement_keeps_the_distance_from_it()
    {
        var log = new ListLogger();
        var result = Turn(NewCreation(forTool: true, log: log), 1, hard: true);
        Assert.That(result.Path, Is.Not.Null, "a turn");
        var pivot = result.Path!;

        // The boom's two ends, from the tractor's heading along the path that is steered.
        double furthest = double.MinValue;
        foreach (var p in pivot)
            foreach (double side in new[] { -Width / 2, Width / 2 })
                furthest = Math.Max(furthest, p.Northing + Math.Cos(p.Heading) * Reach - Math.Sin(p.Heading) * side);
        TestContext.Out.WriteLine($"the boom's ends reach y {furthest:F2} (fence {HalfN}, distance kept {HalfN - furthest:F2} m)");
        Assert.That(HalfN - furthest, Is.GreaterThan(FromBoundary - 0.05), "the boom's ends from the fence, m");
        Assert.That(HalfN - furthest, Is.LessThan(FromBoundary + 1.5), "and the turn is not made further in than that needs");
    }

    [Test]
    public void Too_late_for_the_implement_s_turn_the_turn_is_planned_for_the_pivot()
    {
        // Beside a hard fence the turn is seated back until the boom's ends keep the distance from
        // it, and a turn seated back must start 2 m ahead of the tractor. The tractor turns off the
        // pass a reach before the implement does, so the last place the implement's turn can be
        // planned from is a reach earlier than for a turn planned for the pivot. From later than
        // that the turn is the one made without the choice: never no turn where there was one.
        var planned = Turn(NewCreation(forTool: true), 1, hard: true);
        Assert.That(planned.Path, Is.Not.Null, "the scenario: a turn planned from far back");
        Assert.That(planned.ToolLeavesPassAt, Is.Not.Null, "planned for the implement");
        double turnsOffAt = planned.ArcStart!.Value.Northing;

        var early = Turn(NewCreation(forTool: true), 1, hard: true, fromY: turnsOffAt - 8);
        Assert.That(early.Path, Is.Not.Null, "8 m before the tractor turns off the pass: a turn");
        Assert.That(early.ToolLeavesPassAt, Is.Not.Null, "for the implement");

        var log = new ListLogger();
        var late = Turn(NewCreation(forTool: true, log: log), 1, hard: true, fromY: turnsOffAt - 1);
        var asToday = Turn(NewCreation(forTool: false), 1, hard: true, fromY: turnsOffAt - 1);
        Assert.That(asToday.Path, Is.Not.Null, "the scenario: planned for the pivot there is still a turn from there");
        Assert.That(late.Path, Is.Not.Null, "1 m before it: a turn all the same");
        Assert.That(late.ToolLeavesPassAt, Is.Null, "planned for the pivot");
        Assert.That(late.Path!.Select(p => (p.Easting, p.Northing, p.Heading)), Is.EqualTo(asToday.Path!.Select(p => (p.Easting, p.Northing, p.Heading))),
            "the turn made without the choice, point for point");
        Assert.That(log.Lines, Has.Some.Contains("planning for the pivot"), "and the log says so");
    }

    [Test]
    public void Onto_a_short_pass_the_turn_is_planned_for_the_pivot_and_the_turn_at_its_other_end_is_still_made()
    {
        // Planned for the implement, the turn's path goes on along the next pass 7 m further than
        // the pivot's turn (the tractor still has to come onto the pass). On a short pass that left
        // the turn at its other end no room: beside a hard fence a turn must start 2 m ahead of the
        // tractor, and it was refused ("U-turn blocked") where the pivot's turn before it left room
        // (a tester's field, the farm job harness, 26 Nov 2026).
        // The next pass (x = 20) ends at a hard fence at y = 115: 35 m of it between the two turn lines.
        const double south = 115;
        var shortPass = FenceWithShortPasses(south);

        // The scenario, from the same turn on a long pass: where the line takes over from each turn.
        var forToolOnALongPass = Turn(NewCreation(forTool: true), 1, hard: true, fence: FenceWithShortPasses(-100));
        Assert.That(forToolOnALongPass.ToolLeavesPassAt, Is.Not.Null, "the scenario: onto a long pass the turn is the implement's");
        var asToday = Turn(NewCreation(forTool: false), 1, hard: true, fence: shortPass);
        Assert.That(asToday.Path, Is.Not.Null, "the scenario: a turn planned for the pivot");
        Vec3 afterTheImplementsTurn = LineTakesOver(forToolOnALongPass.Path!), afterThePivotsTurn = LineTakesOver(asToday.Path!);
        TestContext.Out.WriteLine($"the line takes over at y {afterTheImplementsTurn.Northing:F1} after the implement's turn, "
            + $"y {afterThePivotsTurn.Northing:F1} after the pivot's; the pass ends at y {south + FromBoundary:F1}");
        Assert.That(TurnAtTheOtherEnd(NewCreation(forTool: true), south, afterTheImplementsTurn).Path, Is.Null,
            "the scenario: after the implement's turn there is no turn at the other end of the pass");
        Assert.That(TurnAtTheOtherEnd(NewCreation(forTool: true), south, afterThePivotsTurn).Path, Is.Not.Null,
            "the scenario: after the pivot's turn there is one");

        var log = new ListLogger();
        var turn = Turn(NewCreation(forTool: true, log: log), 1, hard: true, fence: shortPass);
        Assert.That(turn.Path, Is.Not.Null, "a turn");
        Assert.That(turn.ToolLeavesPassAt, Is.Null, "planned for the pivot");
        Assert.That(turn.Path!.Select(p => (p.Easting, p.Northing, p.Heading)), Is.EqualTo(asToday.Path!.Select(p => (p.Easting, p.Northing, p.Heading))),
            "the turn made without the choice, point for point");
        Assert.That(log.Lines, Has.Some.Contains("of the next pass left").And.Contains("planning for the pivot"), "and the log says why");
        Assert.That(TurnAtTheOtherEnd(NewCreation(forTool: true), south, LineTakesOver(turn.Path!)).Path, Is.Not.Null,
            "the turn at the other end of the pass is made");
    }

    [TestCase(80.0, true, TestName = "With 41 m of the next pass left after it, the turn is the implement's")]
    [TestCase(90.0, false, TestName = "With 31 m of the next pass left after it, the turn is planned for the pivot")]
    public void The_implement_s_turn_needs_room_for_the_next_turn_on_the_pass_it_lands_on(double south, bool forTool)
    {
        // The U-turn radius and the 30 m a turn may be seated back from a hard fence: 35.4 m of the
        // next pass inside the turn line past the end of the turn's path (the tractor at y 122.7).
        var turn = Turn(NewCreation(forTool: true), 1, hard: true, fence: FenceWithShortPasses(south));
        Assert.That(turn.Path, Is.Not.Null, "a turn");
        TestContext.Out.WriteLine($"the path ends at y {turn.Path![^1].Northing:F1}, the pass at y {south + FromBoundary:F1}");
        Assert.That(turn.ToolLeavesPassAt != null, Is.EqualTo(forTool));
    }

    private static string Describe(IReadOnlyList<Vec3> pivot, IReadOnlyList<Vec3> tool, double next) =>
        $"{pivot.Count} points; pivot: x {pivot.Min(p => p.Easting):F2}..{pivot.Max(p => p.Easting):F2}, top y {pivot.Max(p => p.Northing):F2}, "
        + $"tightest {FrontToolLine.TightestBend(pivot):F2} m, ends ({pivot[^1].Easting:F2},{pivot[^1].Northing:F1}) heading {pivot[^1].Heading * 180 / Math.PI:F1}; "
        + $"implement: x {tool.Min(p => p.Easting):F2}..{tool.Max(p => p.Easting):F2}, top y {tool.Max(p => p.Northing):F2}; next pass x {next:F1}";

    private sealed class ListLogger : ILogger<YouTurnCreationService>
    {
        public readonly List<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }
}
