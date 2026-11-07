// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// The simple fallback turn checked only the ray ahead of the pass and the exit end against the
/// boundary. On a boundary slanting across the end of the pass the arc's far side crossed it:
/// the tractor was driven 2.6 m out of an olive grove with an 8 m sprayer, 12 m out of a field
/// with a 21 m cultivator. The whole arc and the leg back are checked now, and the arc is seated
/// further back along the pass until the path keeps the set distance from the fence.
/// </summary>
[TestFixture, NonParallelizable]
public class FallbackTurnInsideBoundaryTests
{
    private ConfigurationStore _config = null!;
    private YouTurnCreationService _creation = null!;

    [SetUp]
    public void SetUp()
    {
        _config = new ConfigurationStore();
        ConfigurationStore.SetInstance(_config);
        _config.NumSections = 2;
        _config.Tool.SetSectionWidth(0, 600);
        _config.Tool.SetSectionWidth(1, 600);           // 12 m passes
        _config.Tool.Overlap = 0;
        _config.Guidance.UTurnRadius = 8;
        _config.Guidance.UTurnDistanceFromBoundary = 1;
        _config.Guidance.UTurnSmoothing = 0;
        _creation = new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance,
            Substitute.For<AgOpenWeb.Services.Geometry.IPolygonOffsetService>(), _config);
    }

    // AB line due north on x = 0; pass 0 is at x = 6, pass 1 at x = 18 (PassGeometry).
    private static readonly AgOpenWeb.Models.Track.Track Track = AgOpenWeb.Models.Track.Track.FromABLine(
        "AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));

    // The north edge slants down to the east: from (-50, 60) to (60, 10), y = 10 + (60 - x) * 5 / 11.
    // At x = 6 it is at y = 34.5, at x = 22 (the arc's far side) at y = 27.3.
    private static readonly List<Vec2> Slanted = new() { new(-50, -50), new(60, -50), new(60, 10), new(-50, 60) };

    private static Boundary Field() => new()
    {
        OuterBoundary = new BoundaryPolygon
        {
            Points = Slanted.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
        },
    };

    private static double EdgeNorthing(double x) => 10 + (60 - x) * 5 / 11;

    private List<Vec3> TurnRightFromPass0(double tractorNorthing, double headlandAhead)
    {
        var g = new GuidanceWorkingState { HowManyPathsAway = 0, IsHeadingSameWay = true };
        var turn = new YouTurnWorkingState { DistanceToHeadland = headlandAhead, NextTrackTurnOffset = 12 };
        var tractor = new Position { Easting = 6, Northing = tractorNorthing, Heading = 0 };
        return _creation.SimpleFallback(tractor, abHeading: 0, turnLeft: false, Field(),
            g, turn, uTurnSkipRows: 0, headlandDistance: 10, selectedTrack: Track);
    }

    [Test]
    public void The_arc_is_seated_back_until_the_whole_path_keeps_its_distance_from_the_fence()
    {
        // Heading north on pass 0 at y = 10, the headland 10 m ahead (y = 20); the fence is 14.5 m
        // on at x = 6. The old fallback put the arc start 5.5 m into the headland and its apex at
        // y = 33.5 on x = 14, where the fence is at y = 30.9: 2.6 m out, the exit end inside.
        var path = TurnRightFromPass0(10, 10);

        Assert.That(path, Is.Not.Empty, "a fallback turn is still made");
        foreach (var p in path)
        {
            Assert.That(GeometryMath.IsPointInPolygon(Slanted, new Vec2(p.Easting, p.Northing)), Is.True,
                $"path point ({p.Easting:F1},{p.Northing:F1}) is outside the fence");
            Assert.That(EdgeNorthing(p.Easting) - p.Northing, Is.GreaterThanOrEqualTo(0.9),
                $"path point ({p.Easting:F1},{p.Northing:F1}) is within 1 m of the fence");
        }
        Assert.That(path.Max(p => p.Northing), Is.LessThan(33.5 - 1), "the arc was seated further back");
        Assert.That(path[0].Easting, Is.EqualTo(6).Within(0.05), "entry leg on pass 0");
        Assert.That(path[^1].Easting, Is.EqualTo(18).Within(0.05), "exit leg on pass 1");
    }

    [Test]
    public void A_square_end_keeps_the_old_turn()
    {
        // The same turn against a square fence 14.5 m past the headland is clear as built.
        var square = new List<Vec2> { new(-50, -50), new(60, -50), new(60, 34.5), new(-50, 34.5) };
        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = square.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            },
        };
        var g = new GuidanceWorkingState { HowManyPathsAway = 0, IsHeadingSameWay = true };
        var turn = new YouTurnWorkingState { DistanceToHeadland = 10, NextTrackTurnOffset = 12 };
        var path = _creation.SimpleFallback(new Position { Easting = 6, Northing = 10, Heading = 0 }, 0, false, boundary,
            g, turn, 0, 10, Track);

        Assert.That(path, Is.Not.Empty);
        Assert.That(path.Max(p => p.Northing), Is.EqualTo(33.5).Within(0.3), "the apex where the old fallback put it");
    }

    [Test]
    public void No_turn_when_no_seating_within_reach_keeps_the_path_inside()
    {
        // The fence 3 m past the headland on x = 6 and sloping away: no room for an 8 m arc at all.
        var tight = new List<Vec2> { new(-50, -50), new(60, -50), new(60, -1.5), new(-50, 48.5) };
        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = tight.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            },
        };
        _config.Guidance.UTurnExtension = 2;
        var g = new GuidanceWorkingState { HowManyPathsAway = 0, IsHeadingSameWay = true };
        var turn = new YouTurnWorkingState { DistanceToHeadland = 10, NextTrackTurnOffset = 12 };
        var path = _creation.SimpleFallback(new Position { Easting = 6, Northing = 10, Heading = 0 }, 0, false, boundary,
            g, turn, 0, 10, Track);

        Assert.That(path, Is.Empty);
    }
}
