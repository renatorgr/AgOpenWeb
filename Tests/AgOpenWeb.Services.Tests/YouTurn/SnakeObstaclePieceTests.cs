// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
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
/// A U-turn obstacle cuts a pass as the headland line does: the pass is turned at its turn line, so
/// the snake order judges the piece on this side of it. Judged through the obstacle, a pass whose
/// piece between the obstacle and the headland was done still looked to do at the obstacle, and on
/// the beta robot's field (simulator) the snake turned round six such pieces for the rest of the job.
/// Fork only.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class SnakeObstaclePieceTests
{
    private const double ToolWidth = 4.0;

    // Pass k is x = 4k + 2; the cultivated square -40..40 holds passes -10..9.
    private static double PassX(int k) => 4 * k + 2;

    private YouTurnPathingService _pathing = null!;
    private Boundary _boundary = null!;
    private Models.Track.Track _track = null!;
    private List<Vec3> _square = null!;

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

        var offset = new PolygonOffsetService();
        _pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);

        var outer = new List<Vec2> { new(-50, -50), new(50, -50), new(50, 50), new(-50, 50) };
        _boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = outer.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            },
        };
        _track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
        _square = offset.CalculatePointHeadings(new List<Vec2> { new(-40, -40), new(40, -40), new(40, 40), new(-40, 40) });
    }

    /// <summary>
    /// An obstacle across passes 3-6 (x 13..27) from 15 m to 5 m south of the middle; its turn line
    /// is 2 m out (the distance from boundary), so passes 3-6 are cut from -17 to -3.
    /// </summary>
    private void AddObstacle(ObstacleAction atObstacle)
    {
        var pts = new List<Vec2> { new(13, -15), new(27, -15), new(27, -5), new(13, -5) };
        _boundary.InnerBoundaries.Add(new BoundaryPolygon
        {
            Points = pts.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            AtObstacle = atObstacle,
        });
    }

    /// <summary>Pass 4 covered where <paramref name="done"/> says (north, metres).</summary>
    private static Func<double, double, bool> Pass4Covered(Func<double, bool> done) =>
        (e, n) => Math.Abs(e - PassX(4)) < ToolWidth / 2 && done(n);

    // The tractor on pass 6 heading north, 12 m short of the obstacle's turn line.
    private static Position TowardsTheObstacle => new() { Easting = PassX(6), Northing = -30, Heading = 0, Speed = 2.78 };

    private bool PieceDone(Func<double, double, bool> covered) => _pathing.IsPieceDoneAtThisEnd(
        _track, 0, 4, 0, _boundary, _square, TowardsTheObstacle, headingSameWay: true, headlandAhead: 12, covered);

    [Test]
    public void The_piece_before_a_u_turn_obstacle_is_done_when_it_is_covered()
    {
        AddObstacle(ObstacleAction.Turn);
        // Pass 4's 23 m between the headland and the obstacle done, its 43 m beyond it not.
        var covered = Pass4Covered(n => n < -17);
        Assert.That(_pathing.IsPassWorked(_track, 4, 0, _boundary, _square, covered), Is.False,
            "the scenario: pass 4 is still to do on the whole");

        Assert.That(PieceDone(covered), Is.True, "a turn onto pass 4 here would drive its done piece again");
    }

    [Test]
    public void The_piece_before_a_u_turn_obstacle_left_to_do_is_not_done()
    {
        AddObstacle(ObstacleAction.Turn);
        // Pass 4 done beyond the obstacle, its piece between the headland and the obstacle not.
        var covered = Pass4Covered(n => n > -3);
        Assert.That(_pathing.IsPassWorked(_track, 4, 0, _boundary, _square, covered), Is.True,
            "the scenario: pass 4 counts as worked on the whole");

        Assert.That(PieceDone(covered), Is.False, "the piece a turn here lands on is still to do");
    }

    [Test]
    public void The_stretch_at_a_u_turn_obstacle_is_the_one_before_it()
    {
        AddObstacle(ObstacleAction.Turn);
        // Pass 4 covered but for its last 20 m before the obstacle's turn line.
        var covered = Pass4Covered(n => n < -37 || n > -3);

        Assert.That(_pathing.IsPassOpenAtThisEnd(_track, 0, 4, 0, _boundary, _square, TowardsTheObstacle,
            headingSameWay: true, headlandAhead: 12, covered), Is.True, "its end at the obstacle is still to do");
    }

    [TestCase(ObstacleAction.DriveThrough)]
    [TestCase(ObstacleAction.GoRound)]
    public void An_obstacle_the_pass_is_not_turned_at_does_not_cut_it(ObstacleAction atObstacle)
    {
        AddObstacle(atObstacle);
        // Only the piece south of the obstacle done: the pass runs on past it, so it is one piece.
        var covered = Pass4Covered(n => n < -17);

        Assert.That(PieceDone(covered), Is.False, "the whole pass is judged, 30 % done");
    }

    [Test]
    public void A_hard_u_turn_obstacle_is_cut_with_the_implement_kept_off_it()
    {
        // Hard: the turn line is the implement's reach (half of 4 m) further out, so -19 to -1.
        AddObstacle(ObstacleAction.Turn);
        _boundary.InnerBoundaries[0].IsHard = true;
        var covered = Pass4Covered(n => n < -19);

        Assert.That(_pathing.IsPieceDoneAtThisEnd(_track, 0, 4, 0, _boundary, _square, TowardsTheObstacle,
            headingSameWay: true, headlandAhead: 10, covered), Is.True, "the piece ends at the hard obstacle's turn line");
    }
}
