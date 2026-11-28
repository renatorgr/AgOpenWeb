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
/// The snake (skip worked passes) makes no turn that lands on a piece of a pass with no crop to
/// drive to. A field of two parts joined by a neck: four passes run on through the neck into a
/// plot the headland line leaves out, and the passes beside them have a second piece there, cut
/// off from their first by ground outside the fence. The room on the order's next pass, more than
/// twice the turn radius over, is asked where a wide turn lands (where that pass first leaves the
/// crop ahead of the tractor: its first piece), the wide turn round the plot and the neck has no
/// U-turn's shape, and the omega that takes its place lands abreast of the end, on the piece in
/// the plot (a tester's field, 28 Nov 2026: "end of the pass and no U-turn here" on landing, the
/// job over at 18 %). Fork only.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class SnakeTurnLandsOffCropTests
{
    private const double ToolWidth = 4.0;
    private const double Headland = 10.0;

    // Pass k is x = 4k + 2.
    private static double PassX(int k) => 4 * k + 2;

    private YouTurnStateMachine _sm = null!;
    private YouTurnPathingService _pathing = null!;
    private Boundary _boundary = null!;
    private Models.Track.Track _track = null!;
    private List<Vec3> _turnLine = null!;
    private List<Vec3> _crop = null!;

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
        c.Tool.IsHeadlandSectionControl = true;
        c.Guidance.UTurnRadius = 4.0;
        c.Guidance.UTurnExtension = 16.0;
        c.Guidance.UTurnDistanceFromBoundary = 2.0;
        c.Guidance.UTurnStyle = 0;

        var offset = new PolygonOffsetService();
        _pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);
        _sm = new YouTurnStateMachine(
            new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, offset, c),
            _pathing, NullLogger<YouTurnStateMachine>.Instance, c);
        _track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));

        // The main part, 160 x 120 m; a neck 18 m wide going north from it (passes 0 to 3 run through
        // it); a plot 60 x 40 m at its top, where passes 4 to 11 have a second piece.
        var outer = new List<Vec2>
        {
            new(-60, -60), new(100, -60), new(100, 60), new(17, 60), new(17, 100), new(50, 100),
            new(50, 140), new(-10, 140), new(-10, 100), new(-1, 100), new(-1, 60), new(-60, 60),
        };
        _boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = outer.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            },
        };
        _turnLine = offset.CalculatePointHeadings(offset.CreateInwardOffset(outer, 2.0)!);
        // The headland line is one ring: the main part's. The neck and the plot are headland band.
        var crop = new List<Vec2> { new(-50, -50), new(90, -50), new(90, 50), new(-50, 50) };
        _crop = offset.CalculatePointHeadings(crop);
    }

    // Pass 3 heading north in the main part's crop, the end of the pass 108 m ahead in the plot.
    private (string? Said, YouTurnWorkingState Turn) Tick(Func<double, double, bool> covered)
    {
        var ctx = new YouTurnStateMachine.TickContext(
            new Position { Easting = PassX(3), Northing = 30, Heading = 0, Speed = 2.78 },
            _track, _boundary, _turnLine,
            UTurnSkipRows: 2, IsSkipWorkedMode: true,
            HeadlandCalculatedWidth: Headland, HeadlandDistance: Headland,
            IsPointCovered: covered, WorkHeadlandLine: _crop, PassEndLine: _crop);
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 3, IsHeadingSameWay = true };
        var turn = new YouTurnWorkingState { IsEnabled = true };
        var effects = _sm.Tick(ctx, guidance, turn);
        string? said = effects.StatusMessage;
        if (turn.TurnPath == null) effects = _sm.Tick(ctx, guidance, turn); // a turn dropped is looked for again
        return (effects.StatusMessage ?? said, turn);
    }

    [Test]
    public void The_scenario_a_pass_beside_the_neck_has_a_piece_in_the_plot_with_no_way_to_the_crop()
    {
        var at = new Position { Easting = PassX(3), Northing = 30 };
        Assert.Multiple(() =>
        {
            // Before a turn is built: a step of 12 m at a 4 m radius is a wide turn's, asked where
            // pass 6 first leaves the crop ahead of the tractor.
            Assert.That(_pathing.IsPathLandable(_track, 0, 6, _boundary, _crop, at, true, 108, 3), Is.True,
                "pass 6 has room where a wide turn would land, on its first piece");
            // From where a turn ends: in the plot pass 6 leaves the fence before any crop; pass 0
            // runs back through the neck to it.
            Assert.That(_pathing.LandingLength(_track, 0, 6, _boundary, _crop, at, true, 108, landsAt: new Vec3(PassX(6), 125, Math.PI)),
                Is.Zero, "no crop to drive to from pass 6's piece in the plot");
            Assert.That(_pathing.LandingLength(_track, 0, 0, _boundary, _crop, at, true, 108, landsAt: new Vec3(PassX(0), 125, Math.PI)),
                Is.GreaterThan(20), "pass 0 runs back through the neck to the crop");
        });
    }

    [Test]
    public void No_turn_onto_a_piece_of_a_pass_with_no_crop_to_drive_to()
    {
        // Every pass through the neck is worked; the passes east of it are still to do.
        var (said, turn) = Tick((e, n) => e < PassX(3) + ToolWidth / 2);

        Assert.That(turn.TurnPath, Is.Null, $"no turn onto pass {turn.ReturnPassTargetPath}'s piece in the plot");
        Assert.That(said, Does.Contain("no room on the next pass at this end"));
    }

    [Test]
    public void The_turn_goes_onto_the_pass_still_to_do_that_leads_back_to_the_crop()
    {
        // Pass 1, through the neck, is still to do, as are the passes east of the neck: a turn onto
        // it in the plot leads back through the neck to the crop, and is made.
        var (said, turn) = Tick((e, n) => e < PassX(3) + ToolWidth / 2 && Math.Abs(e - PassX(1)) > ToolWidth / 2);

        Assert.That(turn.TurnPath, Is.Not.Null, $"a turn, not \"{said}\"");
        Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(1));
    }

    [Test]
    public void With_the_sections_working_in_the_headland_the_plot_is_ground_to_work()
    {
        ConfigurationStore.Instance.Tool.IsHeadlandSectionControl = false;

        var (said, turn) = Tick((e, n) => e < PassX(3) + ToolWidth / 2);

        Assert.That(turn.TurnPath, Is.Not.Null, $"a turn, not \"{said}\"");
        Assert.That(turn.ReturnPassTargetPath, Is.GreaterThan(3), "a pass east of the neck, on its piece in the plot");
    }
}
