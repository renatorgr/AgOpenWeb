// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.YouTurn;

using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// The way through the headland to the start of the pass, for AutoSteer joining a new line
/// (U-turn settings › Joining a new line in the headland; fork only). A 200 × 300 m field with a
/// 20 m headland, the passes running north at the U-turn radius of 6 m: the way stays in the
/// headland, puts the tractor straight on the pass before the headland line, and is not planned
/// in the crop, on the line already, or facing out of the field.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class LineEntryPlannerTests
{
    private const double HalfE = 100, HalfN = 150, Headland = 20;
    private YouTurnCreationService _creation = null!;
    private ConfigurationStore _config = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _config = ConfigurationStore.Instance;
        _config.Tool.Width = 6;
        _config.NumSections = 1;
        _config.Tool.SetSectionWidth(0, 600);
        _config.Tool.Overlap = 0;
        _config.Tool.HitchLength = 2;
        _config.Tool.IsToolRearFixed = true;
        _config.Tool.IsToolTrailing = false;
        _config.Tool.IsToolTBT = false;
        _config.Tool.IsToolFrontFixed = false;
        _config.Guidance.UTurnRadius = 6;
        _config.Guidance.UTurnDistanceFromBoundary = 1.0;
        _creation = new YouTurnCreationService(
            NullLogger<YouTurnCreationService>.Instance, new PolygonOffsetService(), _config);
    }

    private static Boundary Field(bool hard = false) => new()
    {
        OuterBoundary = new BoundaryPolygon
        {
            Points = Rect(HalfE, HalfN).Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            IsHard = hard,
        },
    };

    private static List<Vec3> HeadlandLine(double width = Headland) =>
        Rect(HalfE - width, HalfN - width).Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList();

    private static List<Vec2> Rect(double e, double n) =>
        new() { new(-e, -n), new(e, -n), new(e, n), new(-e, n) };

    /// <summary>The pass at <paramref name="e"/>, running north, run on far past the field.</summary>
    private static Func<bool, IReadOnlyList<Vec3>?> PassAt(double e) =>
        _ => new List<Vec3> { new(e, -650, 0), new(e, 650, 0) };

    private LineEntryPlan Plan(double e, double n, double headingDeg, Boundary? field = null,
        List<Vec3>? headlandLine = null, double passE = 0) =>
        _creation.PlanLineEntry(new Vec3(e, n, headingDeg * Math.PI / 180.0), PassAt(passE),
            headlandLine ?? HeadlandLine(), field ?? Field(), onLineWithin: 3);

    private static bool InCrop(Vec3 p, double width = Headland) =>
        Math.Abs(p.Easting) < HalfE - width && Math.Abs(p.Northing) < HalfN - width;

    [Test]
    public void From_the_headland_the_way_goes_to_the_start_of_the_pass()
    {
        var plan = Plan(-40, -140, 90); // in the south headland, heading east

        Assert.That(plan.Outcome, Is.EqualTo(LineEntryOutcome.Planned));
        var path = plan.Path!;
        Assert.Multiple(() =>
        {
            Assert.That(plan.HeadingSameWay, Is.True, "driven north, the track's own way");
            Assert.That(path[0].Easting, Is.EqualTo(-40).Within(1e-9), "starts at the tractor");
            Assert.That(path[0].Northing, Is.EqualTo(-140).Within(1e-9));
            Assert.That(path[^1].Easting, Is.EqualTo(0).Within(0.05), "ends on the pass");
            Assert.That(path[^1].Northing, Is.EqualTo(-HalfN + Headland + YouTurnCreationService.LineEntryRunOn).Within(0.3),
                "a little past the headland line");
            Assert.That(path.All(p => !InCrop(p) || Math.Abs(p.Easting) < 0.05), Is.True,
                "the way never cuts across the crop: in it only on the pass");
            Assert.That(path.All(p => p.Northing > -HalfN), Is.True, "inside the field");
            var straight = path.Where(p => p.Northing >= -HalfN + Headland - _config.Guidance.LineEntryStraight + 0.3);
            Assert.That(straight.All(p => Math.Abs(p.Easting) < 0.05), Is.True,
                "straight on the pass the set distance before the headland line");
        });
    }

    [Test]
    public void From_the_other_side_the_way_turns_the_other_way()
    {
        var plan = Plan(40, -140, 270); // heading west

        Assert.That(plan.Outcome, Is.EqualTo(LineEntryOutcome.Planned));
        Assert.That(plan.Path![^1].Easting, Is.EqualTo(0).Within(0.05));
        Assert.That(plan.Path.All(p => !InCrop(p) || Math.Abs(p.Easting) < 0.05), Is.True);
    }

    [Test]
    public void At_the_far_end_the_pass_is_driven_against_the_track()
    {
        var plan = Plan(-40, 140, 90); // the north headland: the pass starts there, driven south

        Assert.That(plan.Outcome, Is.EqualTo(LineEntryOutcome.Planned));
        Assert.Multiple(() =>
        {
            Assert.That(plan.HeadingSameWay, Is.False);
            Assert.That(plan.Path![^1].Northing, Is.EqualTo(HalfN - Headland - YouTurnCreationService.LineEntryRunOn).Within(0.3));
            Assert.That(plan.Path.All(p => !InCrop(p) || Math.Abs(p.Easting) < 0.05), Is.True);
        });
    }

    [Test]
    public void A_longer_straight_puts_the_tractor_on_the_pass_sooner()
    {
        _config.Guidance.LineEntryStraight = 10;
        var plan = Plan(-40, -146, 90);

        Assert.That(plan.Outcome, Is.EqualTo(LineEntryOutcome.Planned));
        var straight = plan.Path!.Where(p => p.Northing >= -HalfN + Headland - 10 + 0.3);
        Assert.That(straight.All(p => Math.Abs(p.Easting) < 0.05), Is.True);
    }

    [Test]
    public void A_front_implement_adds_its_reach_to_the_straight()
    {
        _config.Tool.IsToolRearFixed = false;
        _config.Tool.IsToolFrontFixed = true;
        _config.Tool.HitchLength = 3;
        _config.Guidance.LineEntryStraight = 3;
        var plan = Plan(-40, -146, 90);

        Assert.That(plan.Outcome, Is.EqualTo(LineEntryOutcome.Planned));
        var straight = plan.Path!.Where(p => p.Northing >= -HalfN + Headland - 6 + 0.3);
        Assert.That(straight.All(p => Math.Abs(p.Easting) < 0.05), Is.True);
    }

    [TestCase(-40, 0, 90, TestName = "In the crop")]
    [TestCase(-40, -160, 90, TestName = "Outside the field")]
    public void Not_in_the_headland_nothing_is_planned(double e, double n, double headingDeg)
    {
        Assert.That(Plan(e, n, headingDeg).Outcome, Is.EqualTo(LineEntryOutcome.NotInHeadland));
    }

    [Test]
    public void On_the_line_already_nothing_is_needed()
    {
        Assert.That(Plan(0.5, -140, 2).Outcome, Is.EqualTo(LineEntryOutcome.NotNeeded));
    }

    [Test]
    public void Facing_out_of_the_field_is_left_to_the_U_turn()
    {
        Assert.That(Plan(-40, -140, 180).Outcome, Is.EqualTo(LineEntryOutcome.NoStart));
    }

    [Test]
    public void A_start_too_far_away_is_not_driven_to()
    {
        // The pass 160 m along the headland: further than the planner looks.
        var plan = _creation.PlanLineEntry(new Vec3(-90, -140, 0), PassAt(75), HeadlandLine(), Field(), 3);
        Assert.That(plan.Outcome, Is.Not.EqualTo(LineEntryOutcome.Planned));
    }

    [Test]
    public void A_headland_narrower_than_the_turn_has_no_room()
    {
        var plan = Plan(-40, -147.5, 90, headlandLine: HeadlandLine(5));
        Assert.That(plan.Outcome, Is.EqualTo(LineEntryOutcome.NoRoom));
    }

    [Test]
    public void A_hard_fence_keeps_the_implement_off_it()
    {
        // Heading west, away from the pass: the way loops round towards the south fence. The
        // tractor stays in the field, but a 6 m implement goes over the fence on the loop.
        var soft = Plan(-40, -136, 270, Field(hard: false));
        var hard = Plan(-40, -136, 270, Field(hard: true));

        Assert.That(soft.Outcome, Is.EqualTo(LineEntryOutcome.Planned), "a soft fence allows it, as a manual turn");
        Assert.That(soft.Path!.Min(p => p.Northing), Is.LessThan(-HalfN + 3), "the implement's end goes past the fence");
        Assert.That(hard.Outcome, Is.EqualTo(LineEntryOutcome.NoRoom),
            "with a hard fence the implement keeps the U-turn distance from it: no way, the nearest point");
    }

    [Test]
    public void An_obstacle_in_the_way_is_not_driven_through()
    {
        var field = Field();
        var pond = new BoundaryPolygon
        {
            Points = new List<BoundaryPoint>
            {
                new(-25, -146, 0), new(-15, -146, 0), new(-15, -134, 0), new(-25, -134, 0),
            },
            AtObstacle = ObstacleAction.Turn,
            IsHard = true,
        };
        field.InnerBoundaries.Add(pond);

        // The pond sits across the headland between the tractor and the pass.
        Assert.That(Plan(-40, -140, 90, field).Outcome, Is.EqualTo(LineEntryOutcome.NoRoom));
        // Past it, the way is free again.
        Assert.That(Plan(-10, -140, 90, field).Outcome, Is.EqualTo(LineEntryOutcome.Planned));
    }
}
