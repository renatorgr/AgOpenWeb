// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// The two walks along a pass ahead of the tractor that decide what no U-turn at an end means:
/// a gap to drive on across (<see cref="YouTurnPathingService.GapAhead"/>) or the end of the pass
/// (<see cref="YouTurnPathingService.PassLeavesFieldAhead"/>). Both looked at the pass's line
/// alone, and by the tip of a bay of a hard fence the line stays inside where the implement does
/// not (Tapada da Casa, a 3 m no-till drill: the tip 0.7 m from the pass). With the rig's own test
/// (<see cref="PassEnds.RigFits"/>) such a stretch is the fence. Fork only.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class GapPastFenceTipTests
{
    private const double ToolWidth = 4.0;
    private const double Pass6 = 26.0; // pass k is the line x = 4k + 2

    private YouTurnPathingService _pathing = null!;
    private List<Vec3> _area = null!;
    private Models.Track.Track _track = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Tool.Overlap = 0;
        c.Tool.Offset = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, (int)(ToolWidth * 100));
        c.Vehicle.TrackWidth = 1.9;
        _pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);

        // The area a pass is "in the field" in (the turn line for the gap, the crop for the end of
        // the pass): a square ±40 cut back round the bay, so pass 6 leaves it at y -11.7 and is in
        // it again from y 11.7, 23.3 m on.
        _area = new List<Vec3>
        {
            new(-40, -40, 0), new(40, -40, 0), new(40, -28, 0), new(16, 0, 0), new(40, 28, 0), new(40, 40, 0), new(-40, 40, 0),
        };
        _track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
    }

    /// <summary>A fence at ±50 with a bay from the east side, its tip <paramref name="tipClear"/> m east of pass 6 at y 0.</summary>
    private static Boundary Field(bool hard, double tipClear)
    {
        var fence = new List<Vec2>
        {
            new(-50, -50), new(50, -50), new(50, -20), new(Pass6 + tipClear, 0), new(50, 20), new(50, 50), new(-50, 50),
        };
        return new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                IsHard = hard,
            },
        };
    }

    /// <summary>The rig's test beside the field's fence: null beside a soft one.</summary>
    private static Func<Vec3, Vec3, bool>? RigFits(Boundary field) =>
        PassEnds.RigFits(field.OuterBoundary, PassEnds.RigHalfWidth(ConfigurationStore.Instance));

    private static Position At(double e, double n) => new() { Easting = e, Northing = n };

    private static Models.Track.Track Curve()
    {
        var points = new List<Vec3>();
        for (double n = -110; n <= 110; n += 20) points.Add(new Vec3(0, n, 0));
        return Models.Track.Track.FromCurve("drawn", points);
    }

    // Heading north on pass 6 at y -35, the area's edge 23.3 m ahead.
    private double? Gap(Models.Track.Track track, Boundary field, Func<Vec3, Vec3, bool>? rigFits) =>
        _pathing.GapAhead(track, 0, 6, field, _area, At(Pass6, -35), true, 23.3, rigFits);

    [Test]
    public void The_scenario_the_implement_does_not_fit_past_the_tip()
    {
        // Half the 4 m implement less the 0.5 m the fence guard allows: 1.5 m either side of the pass.
        Assert.That(PassEnds.RigHalfWidth(ConfigurationStore.Instance), Is.EqualTo(1.5).Within(1e-9));
        var fits = RigFits(Field(hard: true, tipClear: 0.7))!;
        Assert.That(fits(new Vec3(Pass6, -1, 0), new Vec3(Pass6, 0, 0)), Is.False, "the tip is inside the rig's band");
        Assert.That(fits(new Vec3(Pass6, -12, 0), new Vec3(Pass6, -11, 0)), Is.True, "before the bay the rig fits");
    }

    [Test]
    public void No_gap_to_drive_across_where_the_rig_does_not_fit_beside_a_hard_fence()
    {
        var field = Field(hard: true, tipClear: 0.7);
        Assert.Multiple(() =>
        {
            Assert.That(Gap(_track, field, null), Is.EqualTo(23.3).Within(1.5), "the scenario: the line alone stays inside the fence");
            Assert.That(Gap(_track, field, RigFits(field)), Is.Null, "an AB line's pass");
            Assert.That(Gap(Curve(), field, RigFits(field)), Is.Null, "a curve's pass");
        });
    }

    [Test]
    public void A_gap_the_rig_fits_along_is_driven_across_as_before()
    {
        // The tip 3 m from the pass: 1.5 m clear of the rig's band.
        var field = Field(hard: true, tipClear: 3.0);
        Assert.Multiple(() =>
        {
            Assert.That(Gap(_track, field, RigFits(field)), Is.EqualTo(23.3).Within(1.5));
            Assert.That(Gap(Curve(), field, RigFits(field)), Is.EqualTo(23.3).Within(2.5));
        });
    }

    [Test]
    public void A_soft_fence_is_judged_by_the_line_alone()
    {
        var field = Field(hard: false, tipClear: 0.7);
        Assert.That(RigFits(field), Is.Null, "no rig test beside a soft fence");
        Assert.That(Gap(_track, field, RigFits(field)), Is.EqualTo(23.3).Within(1.5));
    }

    // A metre into the headland on pass 6, heading north for the tip.
    private bool Leaves(Boundary field, Func<Vec3, Vec3, bool>? rigFits) =>
        _pathing.PassLeavesFieldAhead(_track, 0, 6, field, _area, At(Pass6, -10.7), true, rigFits: rigFits);

    [Test]
    public void The_pass_ends_where_the_rig_does_not_fit_beside_a_hard_fence()
    {
        var field = Field(hard: true, tipClear: 0.7);
        Assert.Multiple(() =>
        {
            Assert.That(Leaves(field, null), Is.False, "the scenario: the line stays inside and is in the crop again past the tip");
            Assert.That(Leaves(field, RigFits(field)), Is.True);
        });
    }

    [Test]
    public void The_pass_goes_on_where_the_rig_fits()
    {
        var wide = Field(hard: true, tipClear: 3.0);
        var soft = Field(hard: false, tipClear: 0.7);
        Assert.Multiple(() =>
        {
            Assert.That(Leaves(wide, RigFits(wide)), Is.False, "a tip the rig fits by");
            Assert.That(Leaves(soft, RigFits(soft)), Is.False, "a soft fence");
        });
    }
}
