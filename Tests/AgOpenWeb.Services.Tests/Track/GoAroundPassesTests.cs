// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.Tests.YouTurn;
using AgOpenWeb.Services.Track;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.Track;

/// <summary>
/// The pins of <see cref="GoAroundPasses"/> on the scenario field: Tree circle 18 sits between
/// passes 12 and 13 of the AB 0° line, so pass 12 bends west round it and pass 13 east. A U-turn
/// next to the tree takes the tractor from one pass to the other, and the side pinned on pass 12
/// must not bend pass 13 west across the tree in front of the tractor.
/// </summary>
[TestFixture]
public class GoAroundPassesTests
{
    private const int Tree18 = 26; // inner index of Tree circle 18 at (77.4, 74.5)

    private static (GoAroundPasses Passes, ObstacleTestField.Built Field, Models.Track.Track Track) Setup()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Tool.Width = 6;
        c.Tool.Offset = 0;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, 600);
        c.Guidance.GoAroundRadius = 6;
        c.Guidance.GoAroundMargin = 1;
        var field = ObstacleTestField.Build(treesGoRound: true);
        var track = ObstacleTestField.Track("AB 0°");
        Assert.That(field.Boundary.InnerBoundaries[Tree18].Points.Average(p => p.Easting), Is.EqualTo(77.4).Within(0.1));
        return (new GoAroundPasses(c, new PolygonOffsetService()), field, track);
    }

    private static (double Dist, List<Vec3> Pass) PassOf(Models.Track.Track track, int pass)
    {
        double dist = Models.Track.PassGeometry.Offset(track, pass, 6, 0, 0);
        return (dist, CurveProcessing.CreateOffsetCurve(track.Points, dist));
    }

    private static double EastingAt(List<Vec3> line, double northing)
    {
        var p = line.OrderBy(v => System.Math.Abs(v.Northing - northing)).First();
        return p.Easting;
    }

    [Test]
    public void The_bend_radius_never_goes_under_the_tractors_minimum_turning_radius()
    {
        var (passes, _, _) = Setup();
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 3;
        c.Vehicle.MaxSteerAngle = 35; // 4.28 m
        c.Guidance.GoAroundRadius = 2;
        Assert.That(passes.Radius, Is.EqualTo(3 / System.Math.Tan(35 * System.Math.PI / 180)).Within(0.01));
        c.Guidance.GoAroundRadius = 10;
        Assert.That(passes.Radius, Is.EqualTo(10));
    }

    [Test]
    public void With_a_front_implement_the_bend_is_one_its_pivot_can_keep_the_implement_on()
    {
        // The pivot steers √(R² − h²) to keep an implement h ahead on a bend of R (FrontToolLine):
        // the bend is never under √(r² + h²), r the tractor's turning radius.
        var (passes, _, _) = Setup();
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 3;
        c.Vehicle.MaxSteerAngle = 35; // 4.28 m
        c.Vehicle.FrontHitchLength = 4.5;
        c.Tool.WorkingPointFromHitch = 0;
        c.Tool.IsToolFrontFixed = true;
        c.Tool.IsToolRearFixed = false;
        c.Tool.IsToolTrailing = false;
        c.Tool.IsToolTBT = false;
        double r = 3 / System.Math.Tan(35 * System.Math.PI / 180);
        c.Guidance.GoAroundRadius = 5;
        Assert.That(passes.Radius, Is.EqualTo(System.Math.Sqrt(r * r + 4.5 * 4.5)).Within(0.01));
        c.Guidance.GoAroundRadius = 10;
        Assert.That(passes.Radius, Is.EqualTo(10));

        c.Tool.IsToolFrontFixed = false;
        c.Tool.IsToolRearFixed = true;
        c.Guidance.GoAroundRadius = 5;
        Assert.That(passes.Radius, Is.EqualTo(5), "a rear implement: the tractor's own radius only");
    }

    [Test]
    public void The_least_margin_grows_as_the_bend_tightens()
    {
        Assert.That(GoAroundPasses.MinMarginFor(6), Is.EqualTo(0.3).Within(1e-9));
        Assert.That(GoAroundPasses.MinMarginFor(10), Is.EqualTo(0.3).Within(1e-9), "no less than on a 6 m bend");
        Assert.That(GoAroundPasses.MinMarginFor(4), Is.EqualTo(0.3 * System.Math.Pow(1.5, 1.5)).Within(1e-9));
        Assert.That(GoAroundPasses.MinMarginFor(4), Is.GreaterThan(0.4), "the scenario tractor cuts a 4 m bend by 0.4 m");

        var (passes, _, _) = Setup();
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 3;
        c.Vehicle.MaxSteerAngle = 35;
        c.Guidance.GoAroundRadius = 2;
        c.Guidance.GoAroundMargin = 0;
        Assert.That(passes.SafetyMargin, Is.EqualTo(GoAroundPasses.MinMarginFor(passes.Radius)).Within(1e-9), "the floor for the radius in force");
        c.Guidance.GoAroundMargin = 1;
        Assert.That(passes.SafetyMargin, Is.EqualTo(1));
    }

    [Test]
    public void A_trailed_implement_gets_its_cut_in_added_to_the_margin()
    {
        // Pass 12 bends west round the tree. Trailed on a 3.5 m drawbar the implement settles
        // inside a 6 m curve, the tree's side, so the bend keeps that much further off.
        double Westmost(bool trailed)
        {
            var (passes, field, track) = Setup();
            var c = ConfigurationStore.Instance;
            c.Vehicle.HitchLength = -1.8;
            c.Tool.TrailingHitchLength = -3.5;
            c.Tool.TrailingToolToPivotLength = 0;
            c.Tool.SetToolType(trailed ? "trailing" : "rear");
            var (dist, pass) = PassOf(track, 12);
            return passes.For(track, dist, pass, field.Boundary)!.Points.Min(p => p.Easting);
        }

        double withRear = Westmost(trailed: false), withTrailed = Westmost(trailed: true);
        double cutIn = ObstacleDetour.TrailedCutIn(ConfigurationStore.Instance.Tool, 1.8, 6.0);
        Assert.That(cutIn, Is.EqualTo(0.80).Within(0.01), "sqrt(36 + 3.24 - 12.25) = 5.195 from the centre");
        Assert.That(withRear - withTrailed, Is.EqualTo(cutIn).Within(0.1));
    }

    [Test]
    public void The_two_passes_beside_the_tree_bend_away_from_each_other()
    {
        var (passes, field, track) = Setup();
        var (d12, p12) = PassOf(track, 12);
        var (d13, p13) = PassOf(track, 13);
        Assert.That(d12, Is.EqualTo(75).Within(0.01));
        Assert.That(d13, Is.EqualTo(81).Within(0.01));
        var b12 = passes.For(track, d12, p12, field.Boundary)!;
        var b13 = passes.For(track, d13, p13, field.Boundary)!;
        Assert.That(b12.GoneRound, Does.Contain(Tree18));
        Assert.That(b13.GoneRound, Does.Contain(Tree18));
        Assert.That(EastingAt(b12.Points, 74.5), Is.LessThan(75 - 1), "pass 12 bends west round the tree");
        Assert.That(EastingAt(b13.Points, 74.5), Is.GreaterThan(81 + 1), "pass 13 bends east round it");
    }

    [Test]
    public void A_pin_is_taken_beside_the_tree_on_the_bends_side_and_let_go_on_the_other_side()
    {
        var (passes, field, track) = Setup();
        var (d12, p12) = PassOf(track, 12);
        var (d13, p13) = PassOf(track, 13);
        var b12 = passes.For(track, d12, p12, field.Boundary)!;

        // In the lead-in, 20 m before the tree and not yet over on the bend's side of its centre:
        // nothing pinned, a flip is harmless there.
        passes.Steered(track, b12, 78, 97);
        Assert.That(passes.PinnedSide(Tree18), Is.EqualTo(0));

        // Alongside the tree on the bend's (west) side: pinned.
        passes.Steered(track, b12, EastingAt(b12.Points, 74.5), 74.5);
        int pinned = passes.PinnedSide(Tree18);
        Assert.That(pinned, Is.Not.EqualTo(0));
        var pinned13 = passes.For(track, d13, p13, field.Boundary)!;
        Assert.That(EastingAt(pinned13.Points, 74.5), Is.LessThan(81), "while the tractor is west of the tree, pass 13 is bent to its side too");

        // After the U-turn next to the tree, the tractor lands on pass 13 east of it.
        passes.Steered(track, pinned13, 82, 66);
        Assert.That(passes.PinnedSide(Tree18), Is.EqualTo(0), "over on the other side, the pin is let go");
        var free13 = passes.For(track, d13, p13, field.Boundary)!;
        Assert.That(EastingAt(free13.Points, 74.5), Is.GreaterThan(81 + 1), "and pass 13 bends east, away from the tractor's tree");
    }

    [Test]
    public void A_pin_holds_while_the_tractor_is_beside_the_tree_on_its_side()
    {
        var (passes, field, track) = Setup();
        var (d12, p12) = PassOf(track, 12);
        var b12 = passes.For(track, d12, p12, field.Boundary)!;
        passes.Steered(track, b12, EastingAt(b12.Points, 74.5), 74.5);
        int side = passes.PinnedSide(Tree18);
        Assert.That(side, Is.Not.EqualTo(0));

        // A snap moves the line 4 m east, through the tree's centre: the bend keeps the tractor's side.
        var (dSnap, pSnap) = (77.4, CurveProcessing.CreateOffsetCurve(track.Points, 77.4));
        var snapped = passes.For(track, dSnap, pSnap, field.Boundary)!;
        Assert.That(snapped.Rounds.Single(r => r.InnerIndex == Tree18).Side, Is.EqualTo(side));
        Assert.That(EastingAt(snapped.Points, 74.5), Is.LessThan(75), "west, where the tractor is");
    }

    [Test]
    public void A_closed_tracks_lap_is_bent_only_when_asked_as_a_lap()
    {
        // A boundary curve's lap through a tree (fork only). The pipeline asks for the whole lap
        // (steered, shown) or the piece of it shown, and gets it bent; the U-turn planner walks a
        // lap as an open line not joined at its seam and asks without saying: no bend, as before.
        var (passes, _, _) = Setup();
        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = new[] { (-100.0, -100.0), (-100.0, 100.0), (100.0, 100.0), (100.0, -100.0) }
                    .Select(p => new BoundaryPoint(p.Item1, p.Item2, 0)).ToList(),
            },
        };
        var tree = new BoundaryPolygon { AtObstacle = ObstacleAction.GoRound };
        tree.SetCircle(new ObstacleCircle(-50, 0, 4));
        boundary.InnerBoundaries.Add(tree);
        var ring = new List<Vec3> { new(-50, -50, 0), new(-50, 50, 0), new(50, 50, 0), new(50, -50, 0) };
        var track = new Models.Track.Track
        {
            Name = "Boundary Curve", Points = CurveProcessing.DensifyLoop(ring), Type = Models.Track.TrackType.Curve, IsClosed = true,
        };

        var lap = passes.For(track, 0, track.Points, boundary, GoAroundPasses.Lap.Whole);
        Assert.Multiple(() =>
        {
            Assert.That(passes.For(track, 0, track.Points, boundary), Is.Null, "the planner's ask");
            Assert.That(passes.Bent(track, 0, track.Points, boundary), Is.SameAs(track.Points), "the planner walks the lap as it is");
            Assert.That(lap?.GoneRound, Is.EqualTo(new[] { 0 }), "the whole lap");
            Assert.That(passes.For(track, 0, track.Points.Take(120).ToList(), boundary, GoAroundPasses.Lap.Piece)?.GoneRound,
                Is.EqualTo(new[] { 0 }), "a piece of it, as an open line");
        });
    }

    // ── A pin after its obstacle is passed (fork only) ──────────────────────────────────────────

    private const int East = -1, West = 1; // of a pass driven north: right and left

    /// <summary>
    /// A long obstacle across the passes of the AB 0° line (6 m apart, pass k at 6k + 3 east): a
    /// round body in the west, 2 to 8 m east, with nearly all the outline's corners, and a thin
    /// spike out to 42 m east. Pass 6 (39 m) crosses the spike 3 m from its tip and goes round the
    /// tip, east. Pass 2 (15 m) has 17 m to go round the body, west, and 31 m round the tip, more
    /// than a bend may move a pass (<see cref="ObstacleDetour.MaxOffset"/>, 20 m), and it runs
    /// east of the middle of the outline's corners, where a tractor is on the east side of the
    /// obstacle for the pins.
    /// </summary>
    private static (GoAroundPasses Passes, Boundary Field, Models.Track.Track Track) SpikeField()
    {
        var (passes, _, track) = Setup();
        return (passes, SpikeBoundary(), track);
    }

    /// <summary>The field of <see cref="SpikeField"/>: 600 m square, soft, the one obstacle in it.</summary>
    internal static Boundary SpikeBoundary()
    {
        var ring = new List<(double E, double N)>();
        // The body, from just above the spike round by the west to just below it.
        for (int i = 0; i <= 120; i++)
        {
            double a = (10 + i * 340.0 / 120) * System.Math.PI / 180; // anticlockwise from east
            ring.Add((5 + 3 * System.Math.Cos(a), 3 * System.Math.Sin(a)));
        }
        ring.Add((42, -0.5));
        ring.Add((42, 0.5));
        var obstacle = ObstacleTestField.Polygon(ring.ToArray());
        obstacle.AtObstacle = ObstacleAction.GoRound;
        var field = new Boundary
        {
            OuterBoundary = ObstacleTestField.Polygon(new[] { (-300.0, -300.0), (-300.0, 300.0), (300.0, 300.0), (300.0, -300.0) }),
        };
        field.InnerBoundaries.Add(obstacle);
        return field;
    }

    [Test]
    public void A_pin_of_an_obstacle_driven_past_holds_the_pass_driven_and_no_other()
    {
        // The tester's case (Couto da Ribeira, 26 Nov 2026, simulator): the side pinned going round
        // an obstacle on one pass was held until the bend's own reach was behind (84 m there). The
        // U-turn at the end of the pass, 67 m on, landed on a pass through the middle of the same
        // obstacle with room only on the other side: "No room to go around Inner 5 on this pass", a
        // U-turn before it and 319 m of the pass left to do.
        var (passes, field, track) = SpikeField();
        var (d6, p6) = PassOf(track, 6);
        var (d2, p2) = PassOf(track, 2);
        Assert.That((d6, d2), Is.EqualTo((39.0, 15.0)));
        var own2 = passes.For(track, d2, p2, field)!;
        Assert.That(own2.Rounds.Single().Side, Is.EqualTo(West), "pass 2 by itself goes round the body");
        Assert.That(own2.Blocked, Is.Empty);

        // North along pass 6, beside the spike: the tip's side is pinned, and it holds pass 2 too.
        var b6 = passes.For(track, d6, p6, field)!;
        Assert.That(b6.Rounds.Single().Side, Is.EqualTo(East));
        passes.LetGo(track, d6, EastingAt(b6.Points, 0), 0, travelHeading: 0);
        passes.Steered(track, d6, b6, EastingAt(b6.Points, 0), 0, travelHeading: 0);
        Assert.That(passes.PinnedSide(0), Is.EqualTo(East));
        Assert.That(passes.PinIsPassed(0), Is.False);
        Assert.That(passes.For(track, d2, p2, field)!.Blocked, Is.EqualTo(new[] { 0 }),
            "beside the obstacle the tractor's side holds every pass: pass 2 has no room on it");

        // 40 m on, the obstacle behind the rig, still inside the bend's reach: the pin is kept for
        // the pass driven (a snap 2 m over is that pass) and no longer holds pass 2.
        passes.LetGo(track, d6, EastingAt(b6.Points, 40), 40, travelHeading: 0);
        passes.Steered(track, d6, b6, EastingAt(b6.Points, 40), 40, travelHeading: 0);
        Assert.That(passes.PinnedSide(0), Is.EqualTo(East), "still pinned");
        Assert.That(passes.PinIsPassed(0), Is.True);
        var snapped = passes.For(track, d6 - 2, CurveProcessing.CreateOffsetCurve(track.Points, d6 - 2), field)!;
        Assert.That(snapped.Rounds.Single().Side, Is.EqualTo(East));
        var planned2 = passes.For(track, d2, p2, field)!;
        Assert.That(planned2.Blocked, Is.Empty, "the planner's pass 2, with the obstacle behind the tractor");
        Assert.That(planned2.Rounds.Single().Side, Is.EqualTo(West));

        // After the U-turn, south on pass 2 at 60 m: the pin is let go before the bend is asked for.
        passes.LetGo(track, d2, 15, 60, travelHeading: System.Math.PI);
        Assert.That(passes.PinnedSide(0), Is.EqualTo(0), "let go on another pass");
        var b2 = passes.For(track, d2, p2, field)!;
        passes.Steered(track, d2, b2, 15, 60, travelHeading: System.Math.PI);
        Assert.Multiple(() =>
        {
            Assert.That(b2.Blocked, Is.Empty);
            Assert.That(b2.GoneRound, Is.EqualTo(new[] { 0 }));
            Assert.That(EastingAt(b2.Points, 0), Is.LessThan(-2 + 0.5), "round the body, west of it");
            Assert.That(passes.PinnedSide(0), Is.Not.EqualTo(East));
        });
    }

    [Test]
    public void Without_the_way_the_tractor_moves_no_obstacle_is_passed_and_the_pin_holds_the_next_pass()
    {
        // The same drive with the way the tractor moves not known: as it was before. It also shows
        // the field of these tests is the tester's case: on pass 2 the tractor is still on the
        // pinned side of the obstacle for the pins, and inside the bend's reach.
        var (passes, field, track) = SpikeField();
        var (d6, p6) = PassOf(track, 6);
        var (d2, p2) = PassOf(track, 2);
        var b6 = passes.For(track, d6, p6, field)!;
        passes.Steered(track, d6, b6, EastingAt(b6.Points, 0), 0, travelHeading: null);
        passes.Steered(track, d6, b6, EastingAt(b6.Points, 40), 40, travelHeading: null);
        Assert.That(passes.PinIsPassed(0), Is.False);

        passes.LetGo(track, d2, 15, 60, travelHeading: null);
        Assert.That(passes.PinnedSide(0), Is.EqualTo(East));
        Assert.That(passes.For(track, d2, p2, field)!.Blocked, Is.EqualTo(new[] { 0 }));
    }

    [Test]
    public void An_obstacle_is_not_passed_while_the_rig_is_still_beside_it()
    {
        var (passes, field, track) = SpikeField();
        var c = ConfigurationStore.Instance;
        var (d6, p6) = PassOf(track, 6);
        var b6 = passes.For(track, d6, p6, field)!;
        passes.Steered(track, d6, b6, EastingAt(b6.Points, 0), 0, travelHeading: 0);

        // The grown obstacle ends 7 m north of its middle (the body, 3 m, and the 4 m kept from
        // it). A drill on wheels 4 m behind the drawbar pin, the pin 1.5 m behind the axle: the rig
        // reaches 5.5 m back, so the pivot must be 7 + 2 + 5.5 m on. (The pin keeps the outline it
        // was taken with, so the line driven here is the one bent before the drill was hitched.)
        c.Tool.SetToolType("trailing");
        c.Vehicle.HitchLength = 1.5;
        c.Tool.TrailingHitchLength = -4;
        c.Tool.TrailingToolToPivotLength = 0;
        Assert.That(passes.RigBehind, Is.EqualTo(5.5).Within(0.01));
        passes.Steered(track, d6, b6, EastingAt(b6.Points, 13), 13, travelHeading: 0);
        Assert.That(passes.PinIsPassed(0), Is.False, "13 m on: the drill is still beside it");
        passes.Steered(track, d6, b6, EastingAt(b6.Points, 17), 17, travelHeading: 0);
        Assert.That(passes.PinIsPassed(0), Is.True, "17 m on: all of the rig is past it");

        // Backing up (no way forward is given then): passed while the drill is still clear of it,
        // not any more once the drill is beside it again, the pivot still 3 m past it.
        passes.Steered(track, d6, b6, EastingAt(b6.Points, 14), 14, travelHeading: null);
        Assert.That(passes.PinIsPassed(0), Is.True, "backed up to 14 m: the drill is still past it");
        passes.Steered(track, d6, b6, EastingAt(b6.Points, 10), 10, travelHeading: null);
        Assert.That(passes.PinIsPassed(0), Is.False, "backed up to 10 m: the drill is beside it again");
        Assert.That(passes.PinnedSide(0), Is.EqualTo(East));

        // And backing up on past it the other way, nothing is passed: what trails then is the
        // tractor's front (and a front implement), which the rig's reach behind does not hold.
        passes.Steered(track, d6, b6, EastingAt(b6.Points, -30), -30, travelHeading: null);
        Assert.That(passes.PinIsPassed(0), Is.False);
        Assert.That(passes.PinnedSide(0), Is.EqualTo(East));
    }

    [Test]
    public void A_pin_of_an_obstacle_driven_past_still_holds_a_snap_of_the_pass_driven()
    {
        // The tree between passes 12 and 13: pass 12 goes round it west. With the tree behind the
        // rig, a snap of the line 2.9 m east (under half a pass: the pass driven) puts it east of
        // the tree's middle, where a line by itself goes round the tree east; the way back from
        // the bend is still under the tractor, so the pin keeps it west.
        var (passes, field, track) = Setup();
        var (d12, p12) = PassOf(track, 12);
        var b12 = passes.For(track, d12, p12, field.Boundary)!;
        passes.Steered(track, d12, b12, EastingAt(b12.Points, 74.5), 74.5, travelHeading: 0);
        int side = passes.PinnedSide(Tree18);
        Assert.That(side, Is.Not.EqualTo(0), "the scenario: pinned alongside the tree");

        double north = 74.5;
        while (!passes.PinIsPassed(Tree18) && passes.PinnedSide(Tree18) != 0 && north < 120)
        {
            north += 1;
            passes.LetGo(track, d12, EastingAt(b12.Points, north), north, travelHeading: 0);
            passes.Steered(track, d12, b12, EastingAt(b12.Points, north), north, travelHeading: 0);
        }
        Assert.That(passes.PinIsPassed(Tree18), Is.True, "the scenario: the tree is passed inside the bend's reach");
        TestContext.Out.WriteLine($"the tree at 74.5 m north is passed at {north:F1} m");

        double dSnap = d12 + 2.9;
        var pSnap = CurveProcessing.CreateOffsetCurve(track.Points, dSnap);
        var byItself = new GoAroundPasses(ConfigurationStore.Instance, new PolygonOffsetService()).For(track, dSnap, pSnap, field.Boundary)!;
        Assert.That(byItself.Rounds.Single(r => r.InnerIndex == Tree18).Side, Is.EqualTo(-side), "the scenario: by itself the snapped line goes round the other side");

        var snapped = passes.For(track, dSnap, pSnap, field.Boundary)!;
        Assert.That(snapped.Rounds.Single(r => r.InnerIndex == Tree18).Side, Is.EqualTo(side), "the pin holds the line under the tractor");
        var (d13, p13) = PassOf(track, 13);
        Assert.That(EastingAt(passes.For(track, d13, p13, field.Boundary)!.Points, 74.5), Is.GreaterThan(81 + 1), "and not the next pass");
    }

    [Test]
    public void A_pin_is_let_go_when_another_obstacle_takes_its_place_in_the_field_s_list()
    {
        // A pin is kept by its obstacle's place in the field's list. An obstacle added before it
        // (or one deleted) puts another obstacle at that place: the side held is not given to it.
        var (passes, field, track) = SpikeField();
        var (d6, p6) = PassOf(track, 6);
        var b6 = passes.For(track, d6, p6, field)!;
        passes.Steered(track, d6, b6, EastingAt(b6.Points, 0), 0, travelHeading: 0);
        Assert.That(passes.PinnedSide(0), Is.EqualTo(East));

        passes.For(track, d6, p6, field);
        Assert.That(passes.PinnedSide(0), Is.EqualTo(East), "the field as it was: the pin holds");

        var tree = new BoundaryPolygon { AtObstacle = ObstacleAction.GoRound };
        tree.SetCircle(new ObstacleCircle(-150, 150, 3));
        field.InnerBoundaries.Insert(0, tree);
        var after = passes.For(track, d6, p6, field)!;
        Assert.That(passes.PinnedSide(0), Is.EqualTo(0), "the tree now at that place is not pinned");
        Assert.That(after.Rounds.Single().InnerIndex, Is.EqualTo(1), "the long obstacle is the second of the list now");

        // The tractor is still beside the long obstacle: pinned again, at its new place.
        passes.Steered(track, d6, after, EastingAt(after.Points, 0), 0, travelHeading: 0);
        Assert.That((passes.PinnedSide(0), passes.PinnedSide(1)), Is.EqualTo((0, East)));
    }

    [Test]
    public void An_obstacle_already_behind_the_tractor_is_not_pinned()
    {
        // Engaged, or moved onto the pass, in the bend's way back to the pass: nothing to hold.
        var (passes, field, track) = SpikeField();
        var (d6, p6) = PassOf(track, 6);
        var b6 = passes.For(track, d6, p6, field)!;
        double e = EastingAt(b6.Points, 12);
        Assert.That(e, Is.GreaterThan(39 + 0.5), "still off the pass, on the bend");

        passes.Steered(track, d6, b6, e, 12, travelHeading: 0);
        Assert.That(passes.PinnedSide(0), Is.EqualTo(0), "going away from it");

        passes.Steered(track, d6, b6, e, 12, travelHeading: System.Math.PI);
        Assert.That(passes.PinnedSide(0), Is.EqualTo(East), "going towards it");
    }
}
