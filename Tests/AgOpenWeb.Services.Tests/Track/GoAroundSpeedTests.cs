// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.Pipeline;
using AgOpenWeb.Services.Track;

namespace AgOpenWeb.Services.Tests.Track;

/// <summary>
/// <see cref="GoAroundSpeed"/>, the model of the app's own Pure Pursuit driving a Go around bend,
/// and <see cref="GoAroundSpeedWarning"/>, which tells the operator to slow down before one.
/// One hard tree 3 m across on the pass through E 0, an 8 m implement, a 5 m Curve radius. Fork only.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class GoAroundSpeedTests
{
    private static readonly GoAroundSpeed.Follower Steering = new(Wheelbase: 3, MaxSteerDeg: 35, Hold: 3, Mult: 1.5, MinLookAhead: 2);
    // Steer Response 4: at 12 km/h it cuts this bend by more than the 0.39 m margin less its reserve.
    private static readonly GoAroundSpeed.Follower Steering4 = Steering with { Hold = 4 };

    // The pass bent round the tree, as the pipeline gets it.
    private static (ObstacleDetour.Bend Bend, Models.Track.Track Track, double Margin) Bend(double margin = 0.3, double radius = 5)
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 3;
        c.Vehicle.MaxSteerAngle = 35;
        c.Tool.Width = 8;
        c.Tool.Offset = 0;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, 800);
        c.Guidance.GoAroundRadius = radius;
        c.Guidance.GoAroundMargin = margin;
        var field = GoAroundSpeedRigTests.OneTree(0, 1.5);
        var track = Models.Track.Track.FromABLine("AB", new Vec3(0, -300, 0), new Vec3(0, 300, 0));
        var passes = new GoAroundPasses(c, new PolygonOffsetService());
        var bend = passes.For(track, 0, track.Points, field.Boundary);
        Assert.That(bend, Is.Not.Null);
        Assert.That(bend!.GoneRound, Is.EqualTo(new[] { 0 }), "the pass goes round the tree");
        return (bend, track, passes.SafetyMargin);
    }

    // The bend's stretch of line round the tree: the points within its reach.
    private static (int From, int To) Stretch(ObstacleDetour.Bend bend)
    {
        var r = bend.Rounds[0];
        int from = -1, to = -1;
        for (int i = 0; i < bend.Points.Count; i++)
            if (ObstacleDetour.DistanceToRing(r.Fence, bend.Points[i].Easting, bend.Points[i].Northing) <= r.Reach)
            {
                if (from < 0) from = i;
                to = i;
            }
        return (from, to);
    }

    private static double CutAt(ObstacleDetour.Bend bend, double kmh, GoAroundSpeed.Follower f, bool forward = true)
    {
        var (from, to) = Stretch(bend);
        return GoAroundSpeed.Cut(bend.Points, GoAroundSpeed.Stations(bend.Points), from, to, bend.Rounds[0].Fence, forward, kmh, f);
    }

    [Test]
    public void The_cut_grows_with_the_speed_and_the_look_ahead()
    {
        var (bend, _, _) = Bend();
        double at4 = CutAt(bend, 4, Steering), at8 = CutAt(bend, 8, Steering), at12 = CutAt(bend, 12, Steering);
        TestContext.Out.WriteLine($"cut 4 km/h {at4:F3} m, 8 km/h {at8:F3} m, 12 km/h {at12:F3} m");
        Assert.That(at8, Is.GreaterThan(at4 + 0.02));
        Assert.That(at12, Is.GreaterThan(at8 + 0.02));
        Assert.That(CutAt(bend, 8, Steering with { Hold = 4 }), Is.GreaterThan(at8 + 0.05), "Steer Response 4 cuts more than 3");
        // Driven the other way the bend is the same shape: about the same cut.
        Assert.That(CutAt(bend, 8, Steering, forward: false), Is.EqualTo(at8).Within(0.05));
    }

    [Test]
    public void A_straight_line_beside_an_obstacle_is_not_cut()
    {
        var line = Enumerable.Range(0, 401).Select(i => new Vec3(0, -100 + i * 0.5, 0)).ToList();
        // A ring 1 m east of the line.
        var ring = Enumerable.Range(0, 24).Select(i => 2 * Math.PI * i / 24)
            .Select(a => new Vec2(3 + 2 * Math.Cos(a), 2 * Math.Sin(a))).ToList();
        double cut = GoAroundSpeed.Cut(line, GoAroundSpeed.Stations(line), 180, 220, ring, true, 12, Steering);
        Assert.That(cut, Is.LessThanOrEqualTo(-0.9), "the tractor stays on the line, 1 m from the ring");
    }

    [Test]
    public void The_safe_speed_is_the_fastest_whole_km_h_within_what_is_allowed()
    {
        Func<double, double> cut = kmh => 0.02 * kmh; // 0.02 m per km/h
        Assert.That(GoAroundSpeed.SafeKmh(cut, 0.2, 14.5), Is.EqualTo(10));
        Assert.That(GoAroundSpeed.SafeKmh(cut, 0.2, 9.2), Is.EqualTo(9));
        Assert.That(GoAroundSpeed.SafeKmh(cut, 0.01, 14.5), Is.EqualTo(0), "none from 2 km/h");
        Assert.That(GoAroundSpeed.Allowed(0.3), Is.EqualTo(0.2).Within(1e-9), "the margin less 10 cm");
        Assert.That(GoAroundSpeed.Allowed(0.1), Is.EqualTo(GoAroundSpeed.MinAllowed));
    }

    [Test]
    public void The_look_ahead_is_the_pipelines_on_the_line()
    {
        // speed × 0.05 × mult × hold + hold, never under the least: 3 m standing, 5.25 m at 10 km/h.
        Assert.That(Steering.LookAhead(0), Is.EqualTo(3).Within(1e-9));
        Assert.That(Steering.LookAhead(10), Is.EqualTo(5.25).Within(1e-9));
        Assert.That((Steering with { Hold = 1 }).LookAhead(0), Is.EqualTo(2).Within(1e-9));
    }

    // ── the warning ────────────────────────────────────────────────────────────────────────

    // Drives the warning along the bent pass north at kmh from N -150 to N 60, a step every 0.1 s;
    // returns each warning with the tractor's northing then.
    private static List<(GoAroundSpeedWarning.Warning W, double N)> Approach(GoAroundSpeedWarning warning,
        ObstacleDetour.Bend bend, Models.Track.Track track, double margin, double kmh, double from = -150, double to = 60,
        bool north = true)
    {
        var told = new List<(GoAroundSpeedWarning.Warning, double)>();
        double step = kmh / 3.6 * 0.1;
        for (double n = from; north ? n <= to : n >= to; n += north ? step : -step)
        {
            var p = bend.Points.MinBy(v => Math.Abs(v.Northing - n));
            if (warning.Check(track, 0, bend, bend.Points, p.Easting, n, north ? 0 : Math.PI, kmh, Steering4, margin) is { } w)
                told.Add((w, n));
        }
        return told;
    }

    [Test]
    public void Told_once_a_few_seconds_before_the_bend_and_again_the_next_time()
    {
        var (bend, track, margin) = Bend();
        var (from, _) = Stretch(bend);
        double bendStart = bend.Points[from].Northing;
        var warning = new GoAroundSpeedWarning();

        var told = Approach(warning, bend, track, margin, 12);
        Assert.That(told, Has.Count.EqualTo(1), "once");
        var (w, n) = told[0];
        Assert.That(w.Inner, Is.EqualTo(0));
        Assert.That(w.SafeKmh, Is.InRange(2, 11));
        Assert.That(w.Notice, Is.EqualTo($"Slow down to {w.SafeKmh} km/h for the bend round Inner 1"));
        Assert.That(w.Cut, Is.GreaterThan(w.Allowed));
        Assert.That(w.CutAtSafe, Is.LessThanOrEqualTo(w.Allowed));
        Assert.That(bendStart - n, Is.InRange(GoAroundSpeedWarning.LeadSeconds * 12 / 3.6 - 1, GoAroundSpeedWarning.LeadSeconds * 12 / 3.6 + 0.5),
            "told the lead's seconds before the bend starts");
        Assert.That(w.LogLine, Does.StartWith("Bend round Inner 1 ").And.Contain("km/h cuts it by"));

        // Back south past it on the same pass, then north again: told each time it is come to.
        Assert.That(Approach(warning, bend, track, margin, 12, 60, -150, north: false), Has.Count.EqualTo(1), "the other way");
        Assert.That(Approach(warning, bend, track, margin, 12), Has.Count.EqualTo(1), "the next time north");
    }

    [Test]
    public void Not_told_at_the_safe_speed_nor_with_room_to_spare()
    {
        var (bend, track, margin) = Bend();
        var w = Approach(new GoAroundSpeedWarning(), bend, track, margin, 12).Single().W;
        Assert.That(Approach(new GoAroundSpeedWarning(), bend, track, margin, w.SafeKmh), Is.Empty, $"{w.SafeKmh} km/h");
        Assert.That(Approach(new GoAroundSpeedWarning(), bend, track, margin, w.SafeKmh + GoAroundSpeedWarning.ToleranceKmh), Is.Empty,
            "within the tolerance");

        var (wide, wideTrack, wideMargin) = Bend(margin: 1.0);
        Assert.That(Approach(new GoAroundSpeedWarning(), wide, wideTrack, wideMargin, 12), Is.Empty, "a 1 m margin at 12 km/h");
    }

    [Test]
    public void Too_tight_even_slowly_says_so()
    {
        var w = new GoAroundSpeedWarning.Warning(2, 20, 9, 0.4, 0.2, 0, 0);
        Assert.That(w.Notice, Is.EqualTo(
            "Slow right down for the bend round Inner 3: the steering cuts it by more than the Safety margin even slowly (raise the margin)"));
        Assert.That(w.LogLine, Does.EndWith("no speed from 2 km/h keeps inside it"));
    }

    [Test]
    public void Each_speed_is_worked_out_once_for_the_line()
    {
        // The cycle at 10 Hz asks again and again: the model runs once per speed (half km/h) and way.
        var (bend, track, margin) = Bend();
        var warning = new GoAroundSpeedWarning();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Approach(warning, bend, track, margin, 12);
        double first = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        for (int k = 0; k < 20; k++) Approach(warning, bend, track, margin, 12, -150, -60);
        double again = sw.Elapsed.TotalMilliseconds / 20;
        TestContext.Out.WriteLine($"first approach {first:F1} ms, an approach after {again:F2} ms");
        Assert.That(again, Is.LessThan(first), "the cuts are kept");
    }
}
