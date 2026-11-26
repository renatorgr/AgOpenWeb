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
using AgOpenWeb.Models.Tool;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.Track;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// A U-turn sized for a trailed implement (<see cref="GuidanceConfig.UTurnTrailedOpen"/>,
/// <c>YouTurnCreationService.TrailedOpen.cs</c>): the implement decides the turn. The tractor's
/// arc is opened to the radius that keeps the implement's working centre on the U-turn radius,
/// and the swing that leaves the implement square on the next pass is part of the shape. Where
/// the passes are too near for that, and where the open turn does not fit, the turn is the one
/// with its exit swung, as without the choice. A tester's rig: a 3 m semi-mounted drill, its
/// wheels 4.15 m behind the drawbar pin and its discs 1.6 m ahead of them, the pin 1.54 m behind
/// the rear axle, a 4.3 m U-turn radius, 2.8 m from the boundary (26 Nov 2026). Fork only.
/// </summary>
[TestFixture, NonParallelizable] // ConfigurationStore is a singleton.
public class TrailedOpenTurnPlanTests
{
    private const double Width = 3.0;
    private const double Radius = 4.3;
    private const double FromBoundary = 2.8;
    private const double Extension = 12.5;
    private const double HalfN = 150;
    private const double West = -150;
    private const double PassX = Width / 2;  // pass 0 of an AB line along x = 0
    private const int Keyhole = 2, Sagitta = 0, KTurn = 1;

    /// <summary>What the turns are planned for: the tractor, the exit swung, or the whole turn sized for the drill.</summary>
    private enum For { Tractor, Exit, WholeTurn }

    private static YouTurnCreationService NewCreation(For choice, int style = Keyhole, ListLogger? log = null)
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
        c.Guidance.UTurnForTrailedTool = choice != For.Tractor;
        c.Guidance.UTurnTrailedOpen = choice == For.WholeTurn;
        return new YouTurnCreationService(log ?? new ListLogger(), new PolygonOffsetService(), c);
    }

    private static List<Vec2> Fence(double east) => new()
    {
        new(West, -HalfN), new(West, HalfN), new(east, HalfN), new(east, -HalfN),
    };

    /// <summary>Pass 0 (x = 1.5) north from y = 60, the turn to the right with <paramref name="skip"/>
    /// rows skipped, in a field whose east fence is at <paramref name="east"/>.</summary>
    private static YouTurnCreationService.TurnPathResult Turn(YouTurnCreationService creation, int skip, bool hard = false, double east = 150,
        double south = -HalfN)
    {
        var fence = Fence(east);
        fence[0] = new Vec2(West, south);
        fence[3] = new Vec2(east, south);
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

    private static ToolGeometry Rig()
    {
        var c = ConfigurationStore.Instance;
        return RigGeometry.Geometry(c.Vehicle, c.Tool, c.ActualToolWidth);
    }

    private static double OffSouth(double heading) => Math.Abs(Math.IEEERemainder(heading - Math.PI, 2 * Math.PI)) * 180 / Math.PI;

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

    /// <summary>What the drill does along a turn's path, followed at 5 cm steps, the tractor going on along the pass after it.</summary>
    private sealed record Drive(double LargestAngle, double DrillOnPassBelowTop, double TractorOnPassBelowTop, double DrillOffWhenTractorOn,
        double DrillAngleWhenTractorOn, double DrillTopBelowTractorTop);

    private static Drive Follow(IReadOnlyList<Vec3> pivot, double next)
    {
        var onwards = pivot.ToList();
        for (double s = 0.5; s <= 40; s += 0.5) onwards.Add(new Vec3(pivot[^1].Easting, pivot[^1].Northing - s, Math.PI));
        var fine = Densify(onwards, 0.05);
        var drill = ImplementSweptPath.Compute(fine, Rig()).ToolCenter;
        double top = fine.Max(p => p.Northing);

        double largest = 0;
        for (int i = 0; i < fine.Count - 1; i++)
        {
            double tractor = Math.Atan2(fine[i + 1].Easting - fine[i].Easting, fine[i + 1].Northing - fine[i].Northing);
            largest = Math.Max(largest, Math.Abs(Math.IEEERemainder(tractor - drill[i].Heading, 2 * Math.PI)));
        }

        // From where on the drill stays within 5 cm of the next pass for good, and from where on
        // the tractor is on it and along it (the end of the turn's last arc).
        int drillOn = drill.Count - 1;
        while (drillOn > 0 && Math.Abs(drill[drillOn - 1].Easting - next) < 0.05) drillOn--;
        int tractorOn = fine.Count - 2;
        while (tractorOn > 0 && Math.Abs(fine[tractorOn - 1].Easting - next) < 0.02
               && OffSouth(Math.Atan2(fine[tractorOn].Easting - fine[tractorOn - 1].Easting, fine[tractorOn].Northing - fine[tractorOn - 1].Northing)) < 0.5)
            tractorOn--;

        return new Drive(largest * 180 / Math.PI, top - drill[drillOn].Northing, top - fine[tractorOn].Northing,
            Math.Abs(drill[tractorOn].Easting - next), OffSouth(drill[tractorOn].Heading), top - drill.Max(p => p.Northing));
    }

    private static string Say(string what, Drive d) =>
        $"{what}: tractor to drill at most {d.LargestAngle:F0}°; the tractor back on the pass {d.TractorOnPassBelowTop:F1} m below the top, " +
        $"the drill then {d.DrillOffWhenTractorOn * 100:F0} cm and {d.DrillAngleWhenTractorOn:F1}° off it, on it {d.DrillOnPassBelowTop:F1} m below the top";

    [TestCase(Keyhole, TestName = "Two rows skipped: the whole turn is sized for the drill (Keyhole)")]
    [TestCase(Sagitta, TestName = "Two rows skipped: the whole turn is sized for the drill (Sagitta)")]
    public void Two_rows_skipped_the_whole_turn_is_sized_for_the_drill(int style)
    {
        const int skip = 2;
        double next = NextPassX(skip);
        var tractor = Turn(NewCreation(For.Tractor, style), skip);
        var exit = Turn(NewCreation(For.Exit, style), skip);
        Assert.That(tractor.Path, Is.Not.Null, "a turn planned for the tractor");
        Assert.That(exit.Path, Is.Not.Null, "a turn with the exit swung");
        var asPlanned = Follow(tractor.Path!, next);
        var swung = Follow(exit.Path!, next);

        var log = new ListLogger();
        var result = Turn(NewCreation(For.WholeTurn, style, log), skip);
        Assert.That(result.Path, Is.Not.Null, "a turn");
        var shape = result.OpenShape;
        Assert.That(shape, Is.Not.Null, "the open shape: " + string.Join(" | ", log.Lines.Where(l => l.Contains("trailed implement"))));
        var pivot = result.Path!;
        var open = Follow(pivot, next);
        TestContext.Out.WriteLine($"radius {shape!.Radius:F2} m, swing {shape.Swing * 180 / Math.PI:F1}°, {shape.PastPass:F2} m past the pass");
        TestContext.Out.WriteLine(Say("planned for the tractor", asPlanned));
        TestContext.Out.WriteLine(Say("exit swung", swung));
        TestContext.Out.WriteLine(Say("whole turn", open));

        Assert.Multiple(() =>
        {
            Assert.That(result.OpenRadius, Is.EqualTo(shape.Radius).Within(1e-9));
            Assert.That(shape.Radius, Is.EqualTo(5.39).Within(0.05), "the tractor's arc, m");
            Assert.That(result.ExitSwing, Is.Null, "the swing is in the shape");

            // What it is for: the drawbar further from the tyre, and the drill square on the pass
            // where the tractor is back on it, as with the exit swung.
            Assert.That(open.LargestAngle, Is.LessThan(swung.LargestAngle - 6.0), "the largest angle between tractor and drill, degrees");
            Assert.That(open.LargestAngle, Is.LessThan(67.0));
            Assert.That(open.DrillOffWhenTractorOn, Is.LessThan(0.08), "the drill beside the pass where the tractor is back on it, m");
            Assert.That(open.DrillAngleWhenTractorOn, Is.LessThan(2.0), "and its angle to the pass there, degrees");
            Assert.That(open.DrillOnPassBelowTop, Is.LessThan(asPlanned.DrillOnPassBelowTop - 5.0), "the drill on the pass sooner than planned for the tractor, m below the top");
            // The deeper arc costs a little: the drill is on the pass a metre or two further into the field than with the exit alone swung.
            Assert.That(open.DrillOnPassBelowTop, Is.LessThan(swung.DrillOnPassBelowTop + 2.5), "and about where the swung exit puts it");

            // The tractor: inside the turn line, touching it, never tighter than the U-turn radius,
            // past the next pass and back, on it where the path ends.
            double top = pivot.Max(p => p.Northing);
            Assert.That(top, Is.LessThan(HalfN - FromBoundary + 0.05), "the tractor keeps inside the turn line");
            Assert.That(top, Is.GreaterThan(HalfN - FromBoundary - 0.6), "and turns at it");
            Assert.That(FrontToolLine.TightestBend(pivot), Is.GreaterThan(Radius * 0.9), "the tractor's tightest bend, m");
            Assert.That(pivot.Max(p => p.Easting) - next, Is.EqualTo(shape.PastPass).Within(0.25), "the tractor past the next pass, m");
            Assert.That(pivot[^1].Easting, Is.EqualTo(next).Within(0.05), "the tractor is on the next pass where the path ends");
            Assert.That(OffSouth(Math.Atan2(pivot[^1].Easting - pivot[^2].Easting, pivot[^1].Northing - pivot[^2].Northing)), Is.LessThan(1.0));
            Assert.That(pivot[^1].Northing, Is.EqualTo(tractor.Path![^1].Northing).Within(2.5), "the path ends about where the planned one does");
            // The drill's discs go about as deep as a turn at the U-turn radius would take the tractor.
            Assert.That(open.DrillTopBelowTractorTop, Is.EqualTo(shape.Radius - Radius).Within(0.5));
        });
    }

    [TestCase(Keyhole, TestName = "Three rows skipped: the wide turn at the open radius, its exit swung (Keyhole)")]
    [TestCase(Sagitta, TestName = "Three rows skipped: the wide turn at the open radius, its exit swung (Sagitta)")]
    public void Three_rows_skipped_the_wide_turn_is_made_at_the_open_radius(int style)
    {
        const int skip = 3;
        double next = NextPassX(skip);
        var exit = Turn(NewCreation(For.Exit, style), skip);
        Assert.That(exit.Path, Is.Not.Null, "a turn with the exit swung");
        var swung = Follow(exit.Path!, next);

        var log = new ListLogger();
        var result = Turn(NewCreation(For.WholeTurn, style, log), skip);
        Assert.That(result.Path, Is.Not.Null, "a turn");
        Assert.That(result.OpenRadius, Is.GreaterThan(0), "sized for the drill: " + string.Join(" | ", log.Lines.Where(l => l.Contains("trailed implement") || l.Contains("open radius"))));
        var pivot = result.Path!;
        var open = Follow(pivot, next);
        TestContext.Out.WriteLine(Say("exit swung", swung));
        TestContext.Out.WriteLine(Say("whole turn", open));
        TestContext.Out.WriteLine($"swing {result.ExitSwing?.Swing * 180 / Math.PI:F1}°, {result.ExitSwing?.PastPass:F2} m past the pass");

        Assert.Multiple(() =>
        {
            Assert.That(result.OpenRadius, Is.EqualTo(5.548).Within(0.01), "the tractor's arcs, m");
            Assert.That(result.OpenShape, Is.Null, "the wide turn, not the one shape");
            Assert.That(result.ExitSwing, Is.Not.Null, "its exit is swung");
            Assert.That(open.LargestAngle, Is.LessThan(swung.LargestAngle - 6.0), "the largest angle between tractor and drill, degrees");
            Assert.That(open.DrillOffWhenTractorOn, Is.LessThan(0.08), "the drill beside the pass where the tractor is back on it, m");
            Assert.That(open.DrillAngleWhenTractorOn, Is.LessThan(1.5), "and its angle to the pass there, degrees");
            Assert.That(pivot.Max(p => p.Northing), Is.LessThan(HalfN - FromBoundary + 0.05), "the tractor keeps inside the turn line");
            Assert.That(FrontToolLine.TightestBend(pivot), Is.GreaterThan(Radius * 0.9), "the tractor's tightest bend, m");
            Assert.That(pivot[^1].Easting, Is.EqualTo(next).Within(0.05), "the tractor is on the next pass where the path ends");
        });
    }

    [TestCase(Keyhole, 0)]
    [TestCase(Keyhole, 1)]
    [TestCase(Sagitta, 0)]
    [TestCase(Sagitta, 1)]
    public void Rows_too_near_for_the_wider_arc_get_the_turn_with_its_exit_swung(int style, int skip)
    {
        var exit = Turn(NewCreation(For.Exit, style), skip);
        var result = Turn(NewCreation(For.WholeTurn, style), skip);
        Assert.That(result.Path, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result.OpenRadius, Is.EqualTo(0));
            Assert.That(result.OpenShape, Is.Null);
            Assert.That(result.Path, Has.Count.EqualTo(exit.Path!.Count));
            for (int i = 0; i < Math.Min(result.Path!.Count, exit.Path.Count); i++)
                Assert.That(GeometryMath.Distance(result.Path[i], exit.Path[i]), Is.LessThan(1e-9), $"point {i}");
        });
    }

    [Test]
    public void Without_the_choice_and_for_another_implement_the_turn_is_as_it_was()
    {
        const int skip = 2;
        var tractor = Turn(NewCreation(For.Tractor), skip);

        // The whole-turn choice needs the trailed implement's own choice.
        var creation = NewCreation(For.Tractor);
        ConfigurationStore.Instance.Guidance.UTurnTrailedOpen = true;
        var alone = Turn(creation, skip);

        // A rear implement with both on is planned for the tractor.
        creation = NewCreation(For.WholeTurn);
        ConfigurationStore.Instance.Tool.SetToolType("rear");
        var rear = Turn(creation, skip);

        Assert.Multiple(() =>
        {
            foreach (var (other, what) in new[] { (alone, "the whole-turn choice alone"), (rear, "a rear implement") })
            {
                Assert.That(other.OpenRadius, Is.EqualTo(0), what);
                Assert.That(other.Path, Has.Count.EqualTo(tractor.Path!.Count), what);
                for (int i = 0; i < Math.Min(other.Path!.Count, tractor.Path.Count); i++)
                    Assert.That(GeometryMath.Distance(other.Path[i], tractor.Path[i]), Is.LessThan(1e-9), $"{what}, point {i}");
            }
        });
    }

    [Test]
    public void A_k_turn_is_planned_for_the_tractor()
    {
        var result = Turn(NewCreation(For.WholeTurn, KTurn), 2);
        Assert.Multiple(() =>
        {
            Assert.That(result.OpenRadius, Is.EqualTo(0));
            Assert.That(YouTurnCreationService.TrailedOpenRadius(ConfigurationStore.Instance, kTurn: true), Is.EqualTo(0));
            Assert.That(YouTurnCreationService.TrailedOpenRadius(ConfigurationStore.Instance), Is.EqualTo(5.548).Within(0.01));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void With_no_room_past_the_next_pass_there_is_still_a_turn_inside_the_turn_line(bool hard)
    {
        // The east fence 3.6 m from the next pass: the turn line 0.8 m past it, where the open
        // shape goes 1.8 m past. The turn is then the one with its exit swung (from further round
        // the arc), or as planned for the tractor: a turn either way, inside the turn line.
        const int skip = 2;
        double next = NextPassX(skip), east = next + 0.8 + FromBoundary;
        var tractor = Turn(NewCreation(For.Tractor, Sagitta), skip, hard, east);
        Assert.That(tractor.Path, Is.Not.Null, "a turn planned for the tractor");

        var log = new ListLogger();
        var result = Turn(NewCreation(For.WholeTurn, Sagitta, log), skip, hard, east);
        TestContext.Out.WriteLine(string.Join("\n", log.Lines.Where(l => l.Contains("trailed implement") || l.Contains("open turn"))));
        Assert.That(result.Path, Is.Not.Null, "never no turn where there was one");
        Assert.Multiple(() =>
        {
            Assert.That(result.Path!.Max(p => p.Easting), Is.LessThan(east - FromBoundary + 0.05), "the tractor keeps inside the turn line, m");
            Assert.That(result.Path!.Max(p => p.Northing), Is.LessThan(HalfN - FromBoundary + 0.05));
            if (result.OpenRadius == 0)
                Assert.That(log.Lines.Any(l => l.Contains("No turn sized for the trailed implement here")), Is.True, "the log says the open turn was not made");
        });
    }

    [TestCase(Keyhole)]
    [TestCase(Sagitta)]
    public void At_a_hard_fence_the_drill_keeps_the_distance_from_it(int style)
    {
        const int skip = 2;
        var log = new ListLogger();
        var result = Turn(NewCreation(For.WholeTurn, style, log), skip, hard: true);
        Assert.That(result.Path, Is.Not.Null, "a turn");
        var swept = ImplementSweptPath.Compute(result.Path!, Rig());
        var clearance = AgOpenWeb.Models.Guidance.TurnClearance.Evaluate(swept, Fence(150),
            AgOpenWeb.Models.Guidance.TurnClearance.KeepSide.Inside, FromBoundary);
        var open = Follow(result.Path!, NextPassX(skip));
        TestContext.Out.WriteLine($"style {style}: open radius {result.OpenRadius:F2} m; nearest the fence {FromBoundary - clearance.MaxIntrusion:F2} m; " + Say("whole turn", open));
        Assert.Multiple(() =>
        {
            Assert.That(clearance.IsClear, Is.True, "the drill keeps the distance from the hard fence");
            Assert.That(result.OpenRadius, Is.GreaterThan(0), "the turn is sized for the drill here too");
            Assert.That(open.DrillOffWhenTractorOn, Is.LessThan(0.08));
        });
    }

    [Test]
    public void With_no_extension_set_the_legs_are_the_headland_s_and_the_turn_is_still_made()
    {
        var creation = NewCreation(For.WholeTurn);
        ConfigurationStore.Instance.Guidance.UTurnExtension = 0;
        var result = Turn(creation, 2);
        Assert.That(result.Path, Is.Not.Null);
        Assert.That(result.OpenShape, Is.Not.Null);
        Assert.That(result.Path![^1].Easting, Is.EqualTo(NextPassX(2)).Within(0.05));
    }

    [Test]
    public void A_turn_planned_and_not_yet_driven_is_planned_again_when_the_choice_changes()
    {
        NewCreation(For.Exit);
        var c = ConfigurationStore.Instance;
        int exit = YouTurnCreationService.TrailedSwingStamp(c);
        Assert.That(exit, Is.Not.EqualTo(0));
        Assert.That(YouTurnCreationService.PlannedTurnRadius(c), Is.EqualTo(Radius), "the exit swung: the generator's radius is the U-turn radius");

        c.Guidance.UTurnTrailedOpen = true;
        Assert.That(YouTurnCreationService.TrailedSwingStamp(c), Is.Not.EqualTo(exit).And.Not.EqualTo(0));
        Assert.That(YouTurnCreationService.PlannedTurnRadius(c), Is.EqualTo(5.548).Within(0.01), "the whole turn: rows wider than twice the open radius get the wide turn");

        c.Guidance.UTurnForTrailedTool = false;
        Assert.That(YouTurnCreationService.TrailedSwingStamp(c), Is.EqualTo(0), "the whole-turn choice alone is no choice");
        Assert.That(YouTurnCreationService.PlannedTurnRadius(c), Is.EqualTo(Radius));
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
