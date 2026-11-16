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
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// The snake (skip worked passes) never makes a turn at the field's edge that drives across the
/// crop of the passes between the two it joins. A pass that ends in a pocket of the field (under a
/// spike of the fence reaching into it) had its next pass still to do far over; the wide turn did
/// not build there (that pass meets a U-turn obstacle's turn line first), and the omega, one shape
/// square to the passes touching the turn line at one point, ran across 40-57 m of the worked crop
/// beyond the spike's tip and landed in the middle of the pass (the beta robot on a tester's field,
/// simulator). No turn is made: the end of the pass is said and the operator turns by hand. Fork only.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class SnakeTurnAcrossCropTests
{
    private const double ToolWidth = 4.0;
    private const double Headland = 10.0;

    // Pass k is x = 4k + 2.
    private static double PassX(int k) => 4 * k + 2;

    private YouTurnStateMachine _sm = null!;
    private PolygonOffsetService _offset = null!;
    private Boundary _boundary = null!;
    private Models.Track.Track _track = null!;
    private List<Vec3> _turnLine = null!;
    private List<Vec3> _headland = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 2.5;
        c.Vehicle.MaxSteerAngle = 35;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, (int)(ToolWidth * 100));
        c.Guidance.UTurnRadius = 4.0;
        c.Guidance.UTurnExtension = 16.0;
        c.Guidance.UTurnDistanceFromBoundary = 2.0;
        c.Guidance.UTurnStyle = 0;

        _offset = new PolygonOffsetService();
        var pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);
        _sm = new YouTurnStateMachine(
            new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, _offset, c),
            pathing, NullLogger<YouTurnStateMachine>.Instance, c);
        _track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
    }

    /// <summary>
    /// A field 160 x 120 m; with <paramref name="spike"/>, a spike of the fence from the west side
    /// to a tip at (12, 2), so the passes west of it end under it in a pocket, and a U-turn obstacle
    /// on pass 14 north of the middle, so the wide turn onto pass 14 from the pocket does not build.
    /// </summary>
    private void Field(bool spike)
    {
        var outer = new List<Vec2> { new(-60, -60), new(100, -60), new(100, 60), new(-60, 60) };
        if (spike) outer.AddRange(new Vec2[] { new(-60, 10), new(12, 2), new(-60, -6) });
        _boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = outer.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            },
        };
        if (spike)
        {
            var obstacle = new List<Vec2> { new(54, 25), new(62, 25), new(62, 33), new(54, 33) };
            _boundary.InnerBoundaries.Add(new BoundaryPolygon
            {
                Points = obstacle.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                AtObstacle = ObstacleAction.Turn,
            });
        }
        _turnLine = _offset.CalculatePointHeadings(_offset.CreateInwardOffset(outer, 2.0)!);
        _headland = _offset.CalculatePointHeadings(_offset.CreateInwardOffset(outer, Headland)!);
    }

    // Every pass worked but 14 and those east of it.
    private static bool Covered(double e, double n) => e < PassX(14) - ToolWidth / 2;

    private (string? Said, YouTurnWorkingState Turn) Tick(int pass, double n)
    {
        var ctx = new YouTurnStateMachine.TickContext(
            new Position { Easting = PassX(pass), Northing = n, Heading = 0, Speed = 2.78 },
            _track, _boundary, _turnLine,
            UTurnSkipRows: 1, IsSkipWorkedMode: true,
            HeadlandCalculatedWidth: Headland, HeadlandDistance: Headland,
            IsPointCovered: Covered, WorkHeadlandLine: _headland, PassEndLine: _headland);
        var guidance = new GuidanceWorkingState { HowManyPathsAway = pass, IsHeadingSameWay = true };
        var turn = new YouTurnWorkingState { IsEnabled = true };
        var effects = _sm.Tick(ctx, guidance, turn);
        string? said = effects.StatusMessage;
        if (turn.TurnPath == null) effects = _sm.Tick(ctx, guidance, turn); // a turn dropped is looked for again
        return (effects.StatusMessage ?? said, turn);
    }

    [Test]
    public void A_turn_out_of_a_pocket_across_the_crop_to_a_far_pass_is_not_made()
    {
        Field(spike: true);

        // Pass -10 heading north in the pocket, 16 m short of the turn line under the spike.
        var (said, turn) = Tick(-10, -22);

        Assert.That(turn.TurnPath, Is.Null, $"no turn across the crop onto pass {turn.ReturnPassTargetPath}");
        Assert.That(said, Does.Contain("no room on the next pass at this end"));
    }

    [Test]
    public void A_far_turn_along_the_headland_is_made()
    {
        Field(spike: false);

        // Pass 6 heading north, 16 m short of the north turn line: the turn onto pass 14 keeps to the
        // headland band.
        var (said, turn) = Tick(6, 42);

        Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(14), $"a turn onto pass 14, not \"{said}\"");
        Assert.That(turn.TurnPath, Is.Not.Null);
    }
}
