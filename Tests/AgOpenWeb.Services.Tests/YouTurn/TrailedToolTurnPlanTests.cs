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
/// A U-turn whose exit is swung wide for a trailed implement
/// (<see cref="GuidanceConfig.UTurnForTrailedTool"/>, <c>YouTurnCreationService.TrailedTool.cs</c>):
/// the turn is found as before, then the tractor goes past the next pass and comes back onto it,
/// so the implement is square on the pass when the tractor is. With the setting off the turn is
/// as it was. A tester's rig: a 3 m semi-mounted drill, its wheels 4.15 m behind the drawbar pin
/// and its discs 1.6 m ahead of them, the pin 1.54 m behind the rear axle, a 4.3 m U-turn radius,
/// 2.8 m from the boundary (26 Nov 2026). Fork only.
/// </summary>
[TestFixture, NonParallelizable] // ConfigurationStore is a singleton.
public class TrailedToolTurnPlanTests
{
    private const double Width = 3.0;
    private const double Radius = 4.3;
    private const double FromBoundary = 2.8;
    private const double Extension = 12.5;
    private const double HalfN = 150;
    private const double West = -150;
    private const double PassX = Width / 2;  // pass 0 of an AB line along x = 0
    private const int Keyhole = 2, Sagitta = 0, KTurn = 1;

    private static YouTurnCreationService NewCreation(bool forTool, int style = Keyhole, ListLogger? log = null)
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 2.7;
        c.Vehicle.TrackWidth = 1.81;
        c.Vehicle.HitchLength = 1.54;
        c.Tool.SetToolType("trailing");
        c.Tool.TrailingHitchLength = 4.15;
        c.Tool.TrailingToolToPivotLength = -1.6;
        c.Tool.Width = Width;
        c.Tool.Overlap = 0;
        c.Tool.Offset = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, (int)(Width * 100));
        c.Guidance.UTurnDistanceFromBoundary = FromBoundary;
        c.Guidance.UTurnRadius = Radius;
        c.Guidance.UTurnExtension = Extension;
        c.Guidance.UTurnStyle = style;
        c.Guidance.UTurnForTrailedTool = forTool;
        return new YouTurnCreationService(log ?? new ListLogger(), new PolygonOffsetService(), c);
    }

    private static List<Vec2> Fence(double east) => new()
    {
        new(West, -HalfN), new(West, HalfN), new(east, HalfN), new(east, -HalfN),
    };

    /// <summary>Pass 0 (x = 1.5) north from y = 60, the turn to the right with <paramref name="skip"/>
    /// rows skipped, in a field whose east fence is at <paramref name="east"/>.</summary>
    private static YouTurnCreationService.TurnPathResult Turn(YouTurnCreationService creation, int skip, bool hard = false, double east = 150)
    {
        var fence = Fence(east);
        var field = new Boundary
        {
            OuterBoundary = new BoundaryPolygon { Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(), IsHard = hard },
        };
        var turnLine = new PolygonOffsetService().CreateInwardOffset(fence, FromBoundary)!
            .Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList();
        var track = AgOpenWeb.Models.Track.Track.FromABLine("AB", new Vec3(0, -300, 0), new Vec3(0, 300, 0));
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = true, HowManyPathsAway = 0 };
        const double fromY = 60;
        double toEnd = HalfN - FromBoundary - fromY;
        var turn = new YouTurnWorkingState { IsTurnLeft = false, DistanceToHeadland = toEnd, NextTrackTurnOffset = Width * (skip + 1) };
        return creation.CreateTurnPath(new Position { Easting = PassX, Northing = fromY, Heading = 0 }, track,
            headingRadians: 0, abHeading: 0, field, turnLine, guidance, turn,
            uTurnSkipRows: skip, headlandCalculatedWidth: 18, headlandDistance: toEnd);
    }

    private static double NextPassX(int skip) => PassX + Width * (skip + 1);

    private static List<Vec3> Drill(IReadOnlyList<Vec3> pivot)
    {
        var c = ConfigurationStore.Instance;
        return ImplementSweptPath.Compute(pivot, RigGeometry.Geometry(c.Vehicle, c.Tool, c.ActualToolWidth)).ToolCenter;
    }

    private static double OffSouth(double heading) => Math.Abs(Math.IEEERemainder(heading - Math.PI, 2 * Math.PI)) * 180 / Math.PI;

    /// <summary>How far below the top of the turn the drill's working line is within
    /// <paramref name="within"/> m of the next pass for good, the drill followed at 5 cm steps.</summary>
    private static double DrillOnPassBelowTop(IReadOnlyList<Vec3> pivot, double next, double within = 0.05)
    {
        // The line takes over where the path ends: the tractor goes on along the pass.
        var onwards = pivot.ToList();
        for (double s = 0.5; s <= 40; s += 0.5) onwards.Add(new Vec3(pivot[^1].Easting, pivot[^1].Northing - s, Math.PI));
        var fine = Densify(onwards, 0.05);
        var drill = Drill(fine);
        double top = fine.Max(p => p.Northing);
        int last = drill.Count - 1;
        while (last > 0 && Math.Abs(drill[last].Easting - next) < within) last--;
        return top - drill[Math.Min(last + 1, drill.Count - 1)].Northing;
    }

    /// <summary>The drill's working centre where the pivot is at point <paramref name="index"/>, followed at 5 cm steps.</summary>
    private static Vec3 DrillWhenPivotAt(IReadOnlyList<Vec3> pivot, int index)
    {
        var upTo = pivot.Take(Math.Min(index + 2, pivot.Count)).ToList();
        int at = Densify(upTo.Take(index + 1).ToList(), 0.05).Count - 1;
        return Drill(Densify(upTo, 0.05))[at];
    }

    private static List<Vec3> Densify(IReadOnlyList<Vec3> path, double step)
    {
        var fine = new List<Vec3> { path[0] };
        for (int i = 1; i < path.Count; i++)
        {
            double d = GeometryMath.Distance(path[i - 1], path[i]);
            int n = Math.Max(1, (int)Math.Ceiling(d / step));
            for (int k = 1; k <= n; k++)
            {
                double t = (double)k / n;
                fine.Add(new Vec3(path[i - 1].Easting + (path[i].Easting - path[i - 1].Easting) * t,
                    path[i - 1].Northing + (path[i].Northing - path[i - 1].Northing) * t, path[i].Heading));
            }
        }
        return fine;
    }

    [TestCase(Keyhole, 1, TestName = "The exit is swung for the drill (Keyhole, one row skipped)")]
    [TestCase(Keyhole, 3, TestName = "The exit is swung for the drill (Keyhole, three rows skipped: a wide turn)")]
    [TestCase(Sagitta, 1, TestName = "The exit is swung for the drill (Sagitta, one row skipped)")]
    [TestCase(Sagitta, 3, TestName = "The exit is swung for the drill (Sagitta, three rows skipped: a wide turn)")]
    public void With_the_choice_on_the_drill_is_square_when_the_tractor_is_back_on_the_pass(int style, int skip)
    {
        var planned = Turn(NewCreation(forTool: false, style), skip).Path;
        Assert.That(planned, Is.Not.Null, "a turn with the setting off");
        double next = NextPassX(skip);
        double before = DrillOnPassBelowTop(planned!, next), before2 = DrillOnPassBelowTop(planned!, next, 0.02);

        var result = Turn(NewCreation(forTool: true, style), skip);
        Assert.That(result.Path, Is.Not.Null, "a turn");
        var swing = result.ExitSwing;
        Assert.That(swing, Is.Not.Null, "the exit was swung");
        var pivot = result.Path!;
        var drill = DrillWhenPivotAt(pivot, swing!.OnPassIndex);
        double after = DrillOnPassBelowTop(pivot, next), after2 = DrillOnPassBelowTop(pivot, next, 0.02);
        TestContext.Out.WriteLine($"style {style}, skip {skip}: swing {swing.Swing * 180 / Math.PI:F1} deg, the tractor {swing.PastPass:F2} m past the next pass, " +
            $"path {swing.RunsOnBy:F1} m longer; the drill within 5 cm of the pass {before:F1} m below the top as planned, {after:F1} m swung; " +
            $"within 2 cm {before2:F1} m and {after2:F1} m");

        Assert.Multiple(() =>
        {
            // What it is for: the drill on the pass and square where the tractor is back on it,
            // and metres sooner than with the turn as planned.
            Assert.That(drill.Easting, Is.EqualTo(next).Within(0.05), "the drill beside the next pass where the tractor is back on it, m");
            Assert.That(OffSouth(drill.Heading), Is.LessThan(0.5), "the drill's angle to the pass there, degrees");
            Assert.That(after, Is.LessThan(before - 5.0), "the drill is on the pass sooner, m below the top of the turn");
            Assert.That(after2, Is.LessThan(before2 - 8.0), "and within 2 cm of it much sooner");
            // The tractor: the same turn up to its last arc, past the next pass and back, never
            // tighter than the U-turn radius, inside the turn line all the way.
            for (int i = 0; i <= swing.FromIndex; i++)
                Assert.That(GeometryMath.Distance(pivot[i], planned![i]), Is.LessThan(1e-9), $"point {i} of the planned turn is kept");
            Assert.That(swing.PastPass, Is.InRange(0.5, 3.5), "the tractor past the next pass, m");
            Assert.That(pivot.Max(p => p.Easting), Is.EqualTo(next + swing.PastPass).Within(0.15));
            Assert.That(pivot.Max(p => p.Northing), Is.LessThan(HalfN - FromBoundary + 0.05), "the tractor keeps inside the turn line");
            Assert.That(FrontToolLine.TightestBend(pivot), Is.GreaterThan(Radius * 0.9), "the tractor's tightest bend, m");
            Assert.That(pivot[^1].Easting, Is.EqualTo(next).Within(0.05), "the tractor is on the next pass where the path ends");
            Assert.That(OffSouth(Math.Atan2(pivot[^1].Easting - pivot[^2].Easting, pivot[^1].Northing - pivot[^2].Northing)), Is.LessThan(1.0),
                "the tractor's angle to the next pass where the path ends, degrees");
            Assert.That(pivot[^1].Northing, Is.LessThanOrEqualTo(planned![^1].Northing + 0.01), "the path ends no sooner than the planned one");
            Assert.That(pivot[swing.OnPassIndex].Northing - pivot[^1].Northing, Is.GreaterThanOrEqualTo(TrailedExitSwing.LegAfter - 0.01),
                "the straight on the pass before the line takes over, m");
        });
    }

    [Test]
    public void A_keyhole_onto_the_next_pass_already_swings_as_much_and_is_left_alone()
    {
        // Rows 3 m apart at a 4.3 m radius: the keyhole's own way back onto the pass swings 48°,
        // more than the drill needs, so there is nothing to add.
        var planned = Turn(NewCreation(forTool: false), 0);
        var result = Turn(NewCreation(forTool: true), 0);
        Assert.That(result.Path, Is.Not.Null);
        TestContext.Out.WriteLine($"the drill within 5 cm of the pass {DrillOnPassBelowTop(planned.Path!, NextPassX(0)):F1} m below the top");
        Assert.That(result.ExitSwing, Is.Null);
        Assert.That(result.Path, Has.Count.EqualTo(planned.Path!.Count));
    }

    [Test]
    public void The_choice_off_leaves_the_turn_as_it_was()
    {
        var off = Turn(NewCreation(forTool: false), 1);
        Assert.That(off.ExitSwing, Is.Null);

        // A rear implement with the setting on is planned for the pivot too.
        var creation = NewCreation(forTool: true);
        ConfigurationStore.Instance.Tool.SetToolType("rear");
        var rear = Turn(creation, 1);

        Assert.That(rear.ExitSwing, Is.Null);
        Assert.That(rear.Path, Has.Count.EqualTo(off.Path!.Count));
        for (int i = 0; i < off.Path.Count; i++)
            Assert.That(GeometryMath.Distance(rear.Path![i], off.Path[i]), Is.LessThan(1e-9), $"point {i}");
    }

    [Test]
    public void A_k_turn_is_planned_for_the_tractor()
    {
        var result = Turn(NewCreation(forTool: true, KTurn), 0);
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitSwing, Is.Null);
            Assert.That(YouTurnCreationService.SwingsExitForTrailedTool(ConfigurationStore.Instance, kTurn: true), Is.False);
            Assert.That(YouTurnCreationService.SwingsExitForTrailedTool(ConfigurationStore.Instance), Is.True);
            Assert.That(YouTurnCreationService.TrailedSwingStamp(ConfigurationStore.Instance), Is.EqualTo(0), "a K-turn: no swing to plan again for");
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void With_no_room_past_the_next_pass_the_turn_is_never_taken_out_of_the_turn_line(bool hard)
    {
        // The east fence 3.6 m from the next pass: the turn line 0.8 m past it, where the swing
        // wants 2 m. The exit is swung less far from further round the arc, or the turn stays as
        // planned; either way the tractor stays inside the turn line and there is a turn.
        // (The Sagitta turn: a keyhole bulges past the next pass by itself and does not fit here at all.)
        const int skip = 1;
        double next = NextPassX(skip), east = next + 0.8 + FromBoundary;
        var planned = Turn(NewCreation(forTool: false, Sagitta), skip, hard, east);
        Assert.That(planned.Path, Is.Not.Null, "a turn with the setting off");

        var log = new ListLogger();
        var result = Turn(NewCreation(forTool: true, Sagitta, log), skip, hard, east);
        Assert.That(result.Path, Is.Not.Null, "never no turn where there was one");
        TestContext.Out.WriteLine(result.ExitSwing is { } s
            ? $"swung {s.Swing * 180 / Math.PI:F1} deg from point {s.FromIndex}, {s.PastPass:F2} m past the pass"
            : "as planned: " + string.Join(" | ", log.Lines.Where(l => l.Contains("swung exit"))));

        Assert.Multiple(() =>
        {
            Assert.That(result.Path!.Max(p => p.Easting), Is.LessThan(east - FromBoundary + 0.05), "the tractor keeps inside the turn line, m");
            if (result.ExitSwing == null)
            {
                Assert.That(result.Path, Has.Count.EqualTo(planned.Path!.Count), "the turn as planned");
                Assert.That(log.Lines.Any(l => l.Contains("No swung exit for the trailed implement")), Is.True, "the log says why");
            }
        });
    }

    [TestCase(Keyhole)]
    [TestCase(Sagitta)]
    public void At_a_hard_fence_the_swung_exit_keeps_the_drill_the_distance_from_it(int style)
    {
        // A hard fence all round, the drill 3 m wide and the distance from boundary 2.8 m: the
        // tractor's straight at the top of the turn brings the drill's outer end up towards the
        // fence, so the exit is swung from further round the arc where it has to be. Either way
        // the drill keeps the distance, as in the turn planned for the tractor.
        const int skip = 1;
        var planned = Turn(NewCreation(forTool: false, style), skip, hard: true);
        Assert.That(planned.Path, Is.Not.Null, "a turn with the setting off");

        var result = Turn(NewCreation(forTool: true, style), skip, hard: true);
        Assert.That(result.Path, Is.Not.Null, "never no turn where there was one");
        Assert.That(result.ExitSwing, Is.Not.Null, "the exit is swung");
        var c = ConfigurationStore.Instance;
        var swept = ImplementSweptPath.Compute(result.Path!, RigGeometry.Geometry(c.Vehicle, c.Tool, c.ActualToolWidth));
        var clearance = AgOpenWeb.Models.Guidance.TurnClearance.Evaluate(swept, Fence(150),
            AgOpenWeb.Models.Guidance.TurnClearance.KeepSide.Inside, FromBoundary);
        double next = NextPassX(skip);
        TestContext.Out.WriteLine($"style {style}: swung {result.ExitSwing!.Swing * 180 / Math.PI:F1} deg from point {result.ExitSwing.FromIndex}, " +
            $"{result.ExitSwing.PastPass:F2} m past the pass; the drill within 5 cm of the pass {DrillOnPassBelowTop(planned.Path!, next):F1} m below the top as planned, " +
            $"{DrillOnPassBelowTop(result.Path!, next):F1} m swung; nearest the fence {FromBoundary - clearance.MaxIntrusion:F2} m");

        Assert.Multiple(() =>
        {
            Assert.That(clearance.IsClear, Is.True, "the drill keeps the distance from the hard fence");
            Assert.That(DrillOnPassBelowTop(result.Path!, next), Is.LessThan(DrillOnPassBelowTop(planned.Path!, next) - 5.0),
                "the drill is on the pass sooner, m below the top of the turn");
        });
    }

    [Test]
    public void With_the_next_pass_at_the_turn_line_the_turn_stays_as_planned()
    {
        // No room at all beside the next pass (the turn line 5 cm past it): the turn as planned.
        const int skip = 1;
        double east = NextPassX(skip) + 0.05 + FromBoundary;
        var planned = Turn(NewCreation(forTool: false, Sagitta), skip, hard: false, east);
        Assert.That(planned.Path, Is.Not.Null, "a turn with the setting off");

        var result = Turn(NewCreation(forTool: true, Sagitta), skip, hard: false, east);
        Assert.That(result.Path, Is.Not.Null, "never no turn where there was one");
        Assert.That(result.ExitSwing, Is.Null);
        Assert.That(result.Path, Has.Count.EqualTo(planned.Path!.Count));
        for (int i = 0; i < planned.Path.Count; i++)
            Assert.That(GeometryMath.Distance(result.Path![i], planned.Path[i]), Is.LessThan(1e-9), $"point {i}");
    }

    [Test]
    public void The_swing_is_planned_again_for_when_the_choice_or_the_drill_changes()
    {
        NewCreation(forTool: false);
        var c = ConfigurationStore.Instance;
        Assert.That(YouTurnCreationService.TrailedSwingStamp(c), Is.EqualTo(0), "off: nothing to plan again for");

        c.Guidance.UTurnForTrailedTool = true;
        int on = YouTurnCreationService.TrailedSwingStamp(c);
        Assert.That(on, Is.Not.EqualTo(0));

        c.Tool.TrailingHitchLength = 5.0;
        Assert.That(YouTurnCreationService.TrailedSwingStamp(c), Is.Not.EqualTo(on).And.Not.EqualTo(0), "a longer drill swings further");

        c.Tool.SetToolType("rear");
        Assert.That(YouTurnCreationService.TrailedSwingStamp(c), Is.EqualTo(0), "a fixed implement is not swung for");
    }

    private sealed class ListLogger : ILogger<YouTurnCreationService>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }
}
