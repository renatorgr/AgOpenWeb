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
/// A manual U-turn and the way to the start of a pass planned for a front implement's centre
/// (the U-turn setting <see cref="GuidanceConfig.UTurnForFrontTool"/>; fork only), as the
/// automatic turn is (<see cref="FrontToolTurnPlanTests"/>): the Dubins path is the implement's,
/// at the radius of its circle, the path steered the pivot's behind it. A tester's sprayer: an
/// 8 m boom 4.7 m ahead of the rear axle, the U-turn radius 5.4 m. Where no such path fits, the
/// one made without the choice.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class FrontToolManualTurnTests
{
    private const double Width = 8.0;
    private const double Reach = 4.7;
    private const double Radius = 5.4;
    private const double HalfE = 100, HalfN = 150;
    private static readonly double ToolRadius = Math.Sqrt(Radius * Radius + Reach * Reach);

    private ListLogger _log = null!;

    private YouTurnCreationService NewCreation(bool forTool)
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 2.9;
        c.Vehicle.FrontHitchLength = Reach - 0.2;
        c.Tool.SetToolType("front");
        c.Tool.WorkingPointFromHitch = 0.2;
        c.Tool.Width = Width;
        c.Tool.Overlap = 0;
        c.Tool.Offset = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, (int)(Width * 100));
        c.Guidance.UTurnDistanceFromBoundary = 1.5;
        c.Guidance.UTurnRadius = Radius;
        c.Guidance.LineEntryStraight = 3;
        c.Guidance.UTurnForFrontTool = forTool;
        _log = new ListLogger();
        return new YouTurnCreationService(_log, new PolygonOffsetService(), c);
    }

    private static Boundary Field(bool hard = false) => new()
    {
        OuterBoundary = new BoundaryPolygon
        {
            Points = Rect(HalfE, HalfN).Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            IsHard = hard,
        },
    };

    private static List<Vec2> Rect(double e, double n) =>
        new() { new(-e, -n), new(e, -n), new(e, n), new(-e, n) };

    /// <summary>Where the implement's centre is at each point of the pivot's path.</summary>
    private static List<Vec3> Implement(IReadOnlyList<Vec3> pivot) =>
        pivot.Select(p => RigGeometry.AlongTractor(p, p.Heading, Reach)).ToList();

    /// <summary>The log of the last path made says it was planned for the implement's centre.</summary>
    private bool PlannedForTheImplement => _log.Lines.Any(l => l.Contains(", planned for the front implement's centre"));

    private static double Deg(double rad) => Math.Abs(Math.IEEERemainder(rad, 2 * Math.PI)) * 180 / Math.PI;

    // ---- the manual U-turn: north on x = 0 from the origin, to the right ----

    private List<Vec3> ManualTurn(bool forTool, int skip, Boundary? field = null, double fromY = 0) =>
        NewCreation(forTool).CreateManualArcPath(new Position { Easting = 0, Northing = fromY }, abHeading: 0, turnLeft: false,
            field, new GuidanceWorkingState { IsHeadingSameWay = true }, uTurnSkipRows: skip);

    [TestCase(0, TestName = "A manual turn onto the next pass is the implement's")]
    [TestCase(1, TestName = "A manual turn with a row skipped is the implement's")]
    [TestCase(3, TestName = "A manual turn with three rows skipped is the implement's")]
    public void A_manual_turn_is_planned_for_the_implement_s_centre(int skip)
    {
        var pivot = ManualTurn(forTool: true, skip);
        Assert.That(pivot.Count, Is.GreaterThan(10), "a turn");
        Assert.That(PlannedForTheImplement, Is.True, "planned for the implement");
        var tool = Implement(pivot);
        double next = Width * (skip + 1);
        // Its arc starts half a metre ahead of the implement (further on than 4 m ahead of the tractor).
        double arcStart = Reach + 0.5;
        TestContext.Out.WriteLine($"skip {skip}: pivot top y {pivot.Max(p => p.Northing):F2}, tightest {FrontToolLine.TightestBend(pivot):F2} m, "
            + $"ends ({pivot[^1].Easting:F2},{pivot[^1].Northing:F1}) {Deg(pivot[^1].Heading - Math.PI):F1} deg off; "
            + $"implement x {tool.Min(p => p.Easting):F2}..{tool.Max(p => p.Easting):F2}, top y {tool.Max(p => p.Northing):F2}");

        Assert.Multiple(() =>
        {
            Assert.That((pivot[0].Easting, pivot[0].Northing), Is.EqualTo((0.0, 0.0)).Within(1e-6), "the path steered starts at the tractor");
            // Rows nearer than the implement's circle: the bulb is that circle, centred between the
            // two passes, so half of what the circle is wider than the rows lies past the next pass.
            Assert.That(tool.Max(p => p.Easting), Is.LessThan(Math.Max(next, next / 2 + ToolRadius) + 0.15),
                "the implement's centre goes no further across than the next pass (or its own circle, on rows nearer than that)");
            if (next >= 2 * ToolRadius)
                Assert.That(tool.Max(p => p.Easting), Is.LessThan(next + 0.10), "never past the next pass");
            // Rows nearer than the implement's circle take a bulb (a turn away, round, and back),
            // which reaches further on than a plain half circle.
            double bulb = next < 2 * ToolRadius
                ? Math.Sqrt(4 * ToolRadius * ToolRadius - Math.Pow(ToolRadius + next / 2, 2))
                : 0;
            Assert.That(tool.Max(p => p.Northing), Is.EqualTo(arcStart + bulb + ToolRadius).Within(0.25),
                "and as far on as its own circle from where its arc starts");
            Assert.That(FrontToolLine.TightestBend(pivot), Is.GreaterThan(Radius * 0.95), "the tractor turns no tighter than the U-turn radius");
            Assert.That(pivot[^1].Easting, Is.EqualTo(next).Within(0.15), "the tractor ends on the next pass");
            Assert.That(Deg(pivot[^1].Heading - Math.PI), Is.LessThan(2.0), "and along it");
        });
    }

    [Test]
    public void Planned_for_the_pivot_the_implement_runs_past_the_next_pass()
    {
        // The turn as it is made without the choice: the same test of the same path.
        var pivot = ManualTurn(forTool: false, skip: 1);
        Assert.That(pivot.Count, Is.GreaterThan(10), "a turn");
        var tool = RigGeometry.PathOfPointAhead(pivot, Reach);
        Assert.That(tool.Max(p => p.Easting), Is.GreaterThan(2 * Width + 1.0), "the implement's centre past the next pass, m");
        Assert.That(pivot[^1].Northing, Is.EqualTo(4.0).Within(0.3), "and the path ends abreast of where it started");
    }

    [Test]
    public void With_the_choice_off_or_a_rear_implement_a_manual_turn_is_as_it_was()
    {
        var off = ManualTurn(forTool: false, skip: 1);
        var creation = NewCreation(forTool: true);
        ConfigurationStore.Instance.Tool.SetToolType("rear");
        var rear = creation.CreateManualArcPath(new Position(), 0, false, null,
            new GuidanceWorkingState { IsHeadingSameWay = true }, 1);

        Assert.That(rear.Select(p => (p.Easting, p.Northing, p.Heading)), Is.EqualTo(off.Select(p => (p.Easting, p.Northing, p.Heading))));
    }

    [Test]
    public void Where_the_implement_s_turn_does_not_fit_the_turn_is_the_pivot_s()
    {
        // Onto the next pass, nearer than the implement's circle, towards a hard fence across the
        // passes: the implement's bulb reaches further on than the tractor's, so its turn needs
        // more room. From the last places the tractor's turn still fits, the turn made is that
        // one, point for point: never no turn where there was one.
        var field = Field(hard: true);
        double lastForTool = double.NaN, lastForPivot = double.NaN;
        for (double y = HalfN - 45; y < HalfN - 5; y += 0.25)
        {
            var path = ManualTurn(forTool: true, 0, field, y);
            bool forTool = PlannedForTheImplement;
            if (path.Count > 0 && forTool) lastForTool = y;
            if (path.Count > 0 && !forTool) lastForPivot = y;
        }
        TestContext.Out.WriteLine($"the implement's turn up to y {lastForTool:F2}, the pivot's up to y {lastForPivot:F2} (fence {HalfN})");
        Assert.That(lastForTool, Is.Not.NaN, "the scenario: the implement's turn fits from far back");
        Assert.That(lastForPivot, Is.GreaterThan(lastForTool), "nearer the fence the pivot's turn is made");
        Assert.That(lastForPivot - lastForTool, Is.InRange(1.0, 9.0), "over the last metres");

        var asToday = ManualTurn(forTool: false, 0, field, lastForPivot);
        var made = ManualTurn(forTool: true, 0, field, lastForPivot);
        Assert.That(_log.Lines, Has.Some.Contains("planning for the pivot"), "and the log says so");
        Assert.That(made.Select(p => (p.Easting, p.Northing, p.Heading)), Is.EqualTo(asToday.Select(p => (p.Easting, p.Northing, p.Heading))));
    }

    // ---- the way to the start of a pass: a 30 m headland, the pass north along x = 0 ----

    private const double Headland = 30;

    private static List<Vec3> HeadlandLine(double width = Headland) =>
        Rect(HalfE - width, HalfN - width).Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList();

    private LineEntryPlan Entry(bool forTool, double e, double n, double headingDeg, double headland = Headland, bool hard = false) =>
        NewCreation(forTool).PlanLineEntry(new Vec3(e, n, headingDeg * Math.PI / 180.0),
            _ => new List<Vec3> { new(0, -650, 0), new(0, 650, 0) },
            HeadlandLine(headland), Field(hard), onLineWithin: 3);

    [Test]
    public void The_way_to_the_start_of_a_pass_is_planned_for_the_implement_s_centre()
    {
        var plan = Entry(forTool: true, -40, -140, 90); // in the south headland, heading east
        Assert.That(plan.Outcome, Is.EqualTo(LineEntryOutcome.Planned));
        Assert.That(PlannedForTheImplement, Is.True, "planned for the implement");
        var pivot = plan.Path!;
        var tool = Implement(pivot);
        double crop = -HalfN + Headland;            // the headland line on the pass
        double joins = crop - (3 + Reach);          // where the tractor's way joins the pass without the choice
        int onPass = tool.FindLastIndex(p => Math.Abs(p.Easting) >= 0.05) + 1;
        TestContext.Out.WriteLine($"{pivot.Count} points; pivot tightest {FrontToolLine.TightestBend(pivot):F2} m, ends ({pivot[^1].Easting:F2},{pivot[^1].Northing:F1}); "
            + $"implement on the pass from y {tool[onPass].Northing:F1}, the tractor {Deg(pivot[onPass].Heading):F0} deg off it there "
            + $"and {Deg(pivot[tool.FindIndex(p => p.Northing >= crop)].Heading):F0} deg at the crop (from y {crop})");

        Assert.Multiple(() =>
        {
            Assert.That((pivot[0].Easting, pivot[0].Northing), Is.EqualTo((-40.0, -140.0)).Within(1e-6), "starts at the tractor");
            Assert.That((tool[0].Easting, tool[0].Northing), Is.EqualTo((-40.0 + Reach, -140.0)).Within(1e-6), "the implement where it is");
            Assert.That(tool.Where(p => p.Northing >= joins + 0.3).All(p => Math.Abs(p.Easting) < 0.05), Is.True,
                "the implement's centre is on the pass from where the tractor's way would join it: a reach more before the crop");
            Assert.That(tool.Where(p => p.Northing < joins - 0.3).All(p => p.Easting < 0.05), Is.True, "and comes onto it from the tractor's side, never across it");
            Assert.That(pivot.Concat(tool).All(p => p.Northing > crop ? Math.Abs(p.Easting) < 0.6 : true), Is.True,
                "neither is in the crop but along the pass");
            Assert.That(FrontToolLine.TightestBend(pivot), Is.GreaterThan(Radius * 0.95), "the tractor turns no tighter than the U-turn radius");
            Assert.That(Math.Abs(pivot[^1].Easting), Is.LessThan(0.15), "the tractor ends on the pass");
            Assert.That(Deg(pivot[^1].Heading), Is.LessThan(2.0), "and along it");
            Assert.That(pivot[^1].Northing, Is.GreaterThan(crop + YouTurnCreationService.LineEntryRunOn - 0.3), "a little inside the crop, where the line takes over");
        });
    }

    [Test]
    public void Planned_for_the_pivot_the_implement_comes_onto_the_pass_only_at_the_end_of_the_tractor_s_arc()
    {
        var plan = Entry(forTool: false, -40, -140, 90);
        Assert.That(plan.Outcome, Is.EqualTo(LineEntryOutcome.Planned));
        var tool = RigGeometry.PathOfPointAhead(plan.Path!, Reach);
        double crop = -HalfN + Headland, joins = crop - (3 + Reach);

        // The implement swings outside the tractor's arc: across the pass before it comes back to it.
        Assert.That(tool.Max(p => p.Easting), Is.GreaterThan(1.0), "the implement's centre past the pass, m");
        Assert.That(tool.Where(p => p.Northing < joins + Reach - 0.5).Any(p => Math.Abs(p.Easting) > 0.5), Is.True);
    }

    [Test]
    public void Where_the_implement_s_way_does_not_fit_the_way_is_the_pivot_s()
    {
        // The implement's circle is wider than the tractor's, so its way needs a deeper headland.
        // In the narrowest one the tractor's way still fits, the way made is that one, point for
        // point: never no way where there was one.
        double narrowest = double.NaN;
        // The tractor in the middle of the headland, facing the crop 40 m beside the pass.
        for (double headland = 30; headland >= 8; headland -= 0.5)
        {
            double n = -HalfN + headland / 2;
            var made = Entry(forTool: true, -40, n, 0, headland, hard: true);
            if (made.Outcome != LineEntryOutcome.Planned || PlannedForTheImplement) continue;
            narrowest = headland;
            var asToday = Entry(forTool: false, -40, n, 0, headland, hard: true);
            Assert.That(asToday.Outcome, Is.EqualTo(LineEntryOutcome.Planned));
            Assert.That(made.Path!.Select(p => (p.Easting, p.Northing, p.Heading)), Is.EqualTo(asToday.Path!.Select(p => (p.Easting, p.Northing, p.Heading))),
                $"a {headland} m headland");
        }
        TestContext.Out.WriteLine($"the pivot's way in headlands down to {narrowest} m");
        Assert.That(narrowest, Is.Not.NaN, "a headland where only the pivot's way fits");
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
