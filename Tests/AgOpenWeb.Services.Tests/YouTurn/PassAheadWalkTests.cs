// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using TrackModel = AgOpenWeb.Models.Track.Track;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// The walks along the line ahead of the tractor that the fence guards ask (fork only): does it
/// come into the field within the reach, from outside a hard fence
/// (<see cref="YouTurnPathingService.PassComesIntoFieldAhead"/>), and does it leave the field
/// before any more crop, from the headland (<see cref="YouTurnPathingService.PassLeavesFieldAhead"/>)?
/// The field is E 0..100, N 0..400; the tractor heads north.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class PassAheadWalkTests
{
    private YouTurnPathingService _pathing = null!;

    private static readonly Boundary Field = new()
    {
        OuterBoundary = new BoundaryPolygon
        {
            Points = new List<BoundaryPoint> { new(0, 0, 0), new(0, 400, 0), new(100, 400, 0), new(100, 0, 0) },
            IsHard = true,
        },
    };

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, ConfigurationStore.Instance);
    }

    private static Position At(double e, double n) => new() { Easting = e, Northing = n };

    [Test]
    public void A_curve_with_long_stretches_is_walked_no_further_than_the_reach()
    {
        // A curve with a point 40 m before the fence and the next 5 m inside it: the field is 45 m
        // ahead, past the 30 m reach. Walked point to point, the first step went the whole 45 m.
        var curve = TrackModel.FromCurve("C", new List<Vec3>
        {
            new(50, -200, 0), new(50, -40, 0), new(50, 5, 0), new(50, 600, 0),
        });
        curve.NoPassOffset = true;
        Assert.Multiple(() =>
        {
            Assert.That(_pathing.PassComesIntoFieldAhead(curve, 0, 0, Field, At(50, -39.5), true, 30), Is.False,
                "45 m ahead");
            Assert.That(_pathing.PassComesIntoFieldAhead(curve, 0, 0, Field, At(50, -25), true, 30), Is.True,
                "25 m ahead");
        });
    }

    [Test]
    public void The_nudge_and_the_implements_shift_move_the_line_walked()
    {
        // An AB line along E -10, west of the field: its pass 0 never comes in. Nudged 20 m east, or
        // with the tractor 20 m east of an implement on the pass, it runs at E 10, in the field
        // 20 m ahead.
        var ab = TrackModel.FromABLine("AB", new Vec3(-10, -100, 0), new Vec3(-10, 500, 0));
        ab.NoPassOffset = true;
        Assert.Multiple(() =>
        {
            Assert.That(_pathing.PassComesIntoFieldAhead(ab, 0, 0, Field, At(10, -20), true, 30), Is.False,
                "the pass itself");
            Assert.That(_pathing.PassComesIntoFieldAhead(ab, 0, 0, Field, At(10, -20), true, 30, nudge: 20), Is.True,
                "nudged");
            Assert.That(_pathing.PassComesIntoFieldAhead(ab, 0, 0, Field, At(10, -20), true, 30, toolShift: -20), Is.True,
                "the implement 20 m left of the tractor");
        });
    }

    [Test]
    public void A_bent_line_steered_is_walked_instead_of_the_pass()
    {
        // The pass at E -10 stays outside; the line steered (bent round an obstacle) comes in 26 m ahead.
        var ab = TrackModel.FromABLine("AB", new Vec3(-10, -100, 0), new Vec3(-10, 500, 0));
        ab.NoPassOffset = true;
        var bent = new List<Vec3> { new(-10, -100, 0), new(-10, -15, 0), new(10, 5, 0), new(10, 500, 0) };
        Assert.Multiple(() =>
        {
            Assert.That(_pathing.PassComesIntoFieldAhead(ab, 0, 0, Field, At(-10, -20), true, 30), Is.False, "the pass");
            Assert.That(_pathing.PassComesIntoFieldAhead(ab, 0, 0, Field, At(-10, -20), true, 30, bent), Is.True, "the bend");
        });
    }

    [Test]
    public void The_end_of_the_pass_is_walked_along_the_bend_steered()
    {
        // The crop 10 m in from the fence, with a headland strip across its east half at N 200..210.
        // The pass at E 60 crosses the strip and runs on in the crop to N 390; the line steered, bent
        // round a tree, leaves it at the strip and runs on in the headland to the north fence.
        var crop = new List<Vec3>
        {
            new(10, 10, 0), new(90, 10, 0), new(90, 200, 0), new(40, 200, 0),
            new(40, 210, 0), new(90, 210, 0), new(90, 390, 0), new(10, 390, 0),
        };
        var ab = TrackModel.FromABLine("AB", new Vec3(60, -100, 0), new Vec3(60, 500, 0));
        ab.NoPassOffset = true;
        var bent = new List<Vec3> { new(60, 100, 0), new(60, 199, 0), new(95, 209, 0), new(95, 420, 0) };
        Assert.Multiple(() =>
        {
            Assert.That(_pathing.PassLeavesFieldAhead(ab, 0, 0, Field, crop, At(60, 195), true), Is.False,
                "the pass: crop past the strip");
            Assert.That(_pathing.PassLeavesFieldAhead(ab, 0, 0, Field, crop, At(60, 195), true, bent), Is.True,
                "the bend: out through the headland");
        });
    }
}
