// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

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
/// Regression: a manual U-turn must not plot the implement through a HARD outer
/// boundary. The manual arc generator (CreateManualArcPath) originally had no
/// implement clearance — only an arc-endpoint-inside check — so a wide tool
/// would swing past the fence even with the boundary marked hard.
/// </summary>
[TestFixture]
public class ManualTurnHardBoundaryTests
{
    private YouTurnCreationService _creation = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.Tool.Width = 16;          // stored width (fallback only)
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, 1600); // 16 m via sections -> ActualToolWidth = 16
        config.Tool.Overlap = 0;
        config.Tool.HitchLength = 2;
        config.Tool.IsToolRearFixed = true;
        config.Tool.IsToolTrailing = false;
        config.Tool.IsToolTBT = false;
        config.Tool.IsToolFrontFixed = false;
        config.Guidance.UTurnDistanceFromBoundary = 1.0; // clearance margin

        _creation = new YouTurnCreationService(
            NullLogger<YouTurnCreationService>.Instance, new PolygonOffsetService(),
            config);
    }

    // 100×100 m square boundary centred on origin (edges at ±50).
    private static Boundary Square(bool hard)
    {
        var pts = new List<Vec2> { new(-50, -50), new(50, -50), new(50, 50), new(-50, 50) };
        return new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = pts.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                IsHard = hard
            }
        };
    }

    private List<Vec3> ManualTurn(Boundary boundary, double startN)
    {
        var pos = new Position { Easting = 0, Northing = startN };
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = true };
        // abHeading 0 = north; left turn. Radius = (16/2) = 8 m, so an arc started
        // at N=45 sweeps the pivot to ~N=53 — past the +50 edge.
        return _creation.CreateManualArcPath(pos, abHeading: 0, turnLeft: true,
            boundary, guidance, uTurnSkipRows: 0);
    }

    [Test]
    public void SoftBoundary_ImplementSwingsPast_TractorInside_PlotsTurn()
    {
        // Started at N=36 the tractor stays inside (its arc tops out short of +50) while the 16 m
        // implement's end swings past: a soft boundary allows that, as AgOpenGPS does.
        var path = ManualTurn(Square(hard: false), startN: 36);
        Assert.That(path.Count, Is.GreaterThan(2), "soft boundary should still plot the manual turn");
        Assert.That(path.Max(p => p.Northing), Is.LessThan(50), "the tractor itself stays inside");
    }

    [Test]
    public void SoftBoundary_TractorWouldLeaveTheField_RefusesTurn()
    {
        // Started at N=45 the tractor's own arc goes past the +50 edge: refused, as an automatic
        // turn is (#698). AgOpenGPS's manual turn has no such check (Turn intervention T11 went
        // 7 m out of the field).
        var path = ManualTurn(Square(hard: false), startN: 45);
        Assert.That(path.Count, Is.LessThanOrEqualTo(2), "a manual turn that leaves the field is refused");
    }

    [Test]
    public void HardBoundary_NearEdge_RefusesTurn()
    {
        var path = ManualTurn(Square(hard: true), startN: 45);
        Assert.That(path.Count, Is.LessThanOrEqualTo(2),
            "hard boundary must refuse a manual turn that swings the implement past the fence");
    }

    [Test]
    public void HardBoundary_WellInside_StillPlotsTurn()
    {
        // Centred at origin: the whole turn + implement stays well inside ±50.
        var path = ManualTurn(Square(hard: true), startN: 0);
        Assert.That(path.Count, Is.GreaterThan(2),
            "a clear turn far from the hard boundary must not be falsely refused");
    }

    // ── The implement already nearer the fence than the U-turn distance at the press ──────
    //
    // Bug report "manual U-turn does nothing" (2026-11-11): an 8 m boom on the front linkage,
    // the pass along a hard fence, the boom's end 1.5 m from it with a U-turn distance of 2 m.
    // The turn away from the fence was refused for where the boom already stood, and a pass
    // further on so was every other one: the button did nothing. Below, the tractor heads
    // north beside the east fence (x = 50) and turns left, away from it, onto the pass 16 m
    // over (radius 8, every second pass: a plain half circle).

    /// <summary>An 8 m implement, a 2 m U-turn distance, 8 m radius.</summary>
    private static void EightMetreTool(bool front, double hitch)
    {
        var c = ConfigurationStore.Instance;
        c.Tool.Width = 8;
        c.Tool.SetSectionWidth(0, 800);
        c.Tool.HitchLength = hitch;
        c.Tool.IsToolFrontFixed = front;
        c.Tool.IsToolRearFixed = !front;
        c.Guidance.UTurnRadius = 8;
        c.Guidance.UTurnDistanceFromBoundary = 2.0;
    }

    /// <summary>A left turn from 20 m south of the middle with the implement's right end
    /// <paramref name="endFromFence"/> metres inside the east fence (negative: across it).</summary>
    private List<Vec3> TurnAwayFromTheEastFence(double endFromFence, bool startedByTheApp = false) =>
        _creation.CreateManualArcPath(
            new Position { Easting = 50 - endFromFence - 4, Northing = -20 }, abHeading: 0, turnLeft: true,
            Square(hard: true), new GuidanceWorkingState { IsHeadingSameWay = true }, uTurnSkipRows: 1,
            startedByTheApp: startedByTheApp);

    [Test]
    public void FrontBoomInsideTheDistance_TurningAwayFromTheFence_PlotsTheTurn()
    {
        // A front boom's outer end only moves away from the fence in a turn away from it.
        EightMetreTool(front: true, hitch: 4.5);
        var path = TurnAwayFromTheEastFence(endFromFence: 1.5);
        Assert.That(path.Count, Is.GreaterThan(2), "the boom never comes nearer the fence than it is at the press");
        Assert.That(path[^1].Easting, Is.EqualTo(44.5 - 16).Within(0.5), "onto the pass 16 m over");
    }

    [Test]
    public void ToolInsideTheDistance_SwingingNearerButNotAcross_PlotsTheTurn()
    {
        // 5 m behind the pivot the outer corner swings 1 m out (13 m from the turn's centre,
        // 12 m at the press): from 1.5 m to 0.5 m inside the fence.
        EightMetreTool(front: false, hitch: 5);
        Assert.That(TurnAwayFromTheEastFence(endFromFence: 1.5).Count, Is.GreaterThan(2));
    }

    [Test]
    public void ToolInsideTheDistance_SwingingAcrossTheFence_IsRefused()
    {
        // The same swing from 0.6 m: 0.4 m across the fence.
        EightMetreTool(front: false, hitch: 5);
        Assert.That(TurnAwayFromTheEastFence(endFromFence: 0.6), Is.Empty);
    }

    [Test]
    public void ToolWithTheDistanceAtThePress_KeepsItThroughTheTurn()
    {
        // 2.5 m from the fence at the press, 1.5 m in the swing: the turn would take the
        // distance the implement still has, so it is refused as before.
        EightMetreTool(front: false, hitch: 5);
        Assert.That(TurnAwayFromTheEastFence(endFromFence: 2.5), Is.Empty);
        Assert.That(TurnAwayFromTheEastFence(endFromFence: 3.5).Count, Is.GreaterThan(2), "3.5 m: 2.5 m in the swing");
    }

    [Test]
    public void ToolAlreadyAcrossTheFence_MayTurnAway_ButNoFurtherAcross()
    {
        // A boom end over the fence line (a boom over a ditch; the report had 0.08 m).
        EightMetreTool(front: true, hitch: 4.5);
        Assert.That(TurnAwayFromTheEastFence(endFromFence: -0.2).Count, Is.GreaterThan(2),
            "the front boom's end comes back inside from the first metre of the turn");

        EightMetreTool(front: false, hitch: 5);
        Assert.That(TurnAwayFromTheEastFence(endFromFence: -0.2), Is.Empty,
            "the rear corner would swing 1 m further across");
    }

    [Test]
    public void ATurnTheAppStartsByItself_KeepsTheWholeDistance()
    {
        // "Turn from here" for a planned arc already behind the tractor: nobody asked for it.
        EightMetreTool(front: true, hitch: 4.5);
        Assert.That(TurnAwayFromTheEastFence(endFromFence: 1.5, startedByTheApp: true), Is.Empty);
        Assert.That(TurnAwayFromTheEastFence(endFromFence: 2.5, startedByTheApp: true).Count, Is.GreaterThan(2));
    }
}
