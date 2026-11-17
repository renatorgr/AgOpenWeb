// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// The obstacle shapes' sums that run on the host while the operator waits or every cycle: an
/// obstacle's size in the list (<see cref="BoundaryPolygon.LongestSpan"/>), its outline's
/// fingerprint (<see cref="BoundaryPolygon.OutlineStamp"/>).
/// </summary>
[TestFixture]
public class ObstacleShapeReviewTests
{
    private static BoundaryPolygon Polygon(IEnumerable<(double E, double N)> points)
    {
        var p = new BoundaryPolygon();
        foreach (var (e, n) in points) p.Points.Add(new BoundaryPoint(e, n, 0));
        p.UpdateBounds();
        return p;
    }

    private static double EveryPair(BoundaryPolygon p)
    {
        double best = 0;
        foreach (var a in p.Points)
            foreach (var b in p.Points)
                best = Math.Max(best, Math.Sqrt((a.Easting - b.Easting) * (a.Easting - b.Easting) + (a.Northing - b.Northing) * (a.Northing - b.Northing)));
        return best;
    }

    [Test]
    public void LongestSpan_IsTheDistanceBetweenTheTwoFarthestPoints()
    {
        var random = new Random(7);
        for (int run = 0; run < 200; run++)
        {
            int n = 3 + random.Next(60);
            var p = Polygon(Enumerable.Range(0, n).Select(_ => (random.NextDouble() * 40 - 20, random.NextDouble() * 15)));
            Assert.That(p.LongestSpan(), Is.EqualTo(EveryPair(p)).Within(1e-9), $"run {run}");
        }
        Assert.That(Polygon(new[] { (0.0, 0.0), (30.0, 0.0), (30.0, 30.0), (0.0, 30.0) }).LongestSpan(), Is.EqualTo(30 * Math.Sqrt(2)).Within(1e-9));
        Assert.That(Polygon(new[] { (0.0, 0.0), (1.0, 1.0), (2.0, 2.0), (2.0, 2.0) }).LongestSpan(), Is.EqualTo(2 * Math.Sqrt(2)).Within(1e-9), "in a line");
        Assert.That(Polygon(new[] { (5.0, 5.0), (5.0, 5.0) }).LongestSpan(), Is.EqualTo(0));
        var circle = new BoundaryPolygon();
        circle.SetCircle(new ObstacleCircle(3, 4, 6));
        Assert.That(circle.LongestSpan(), Is.EqualTo(EveryPair(circle)).Within(1e-9));
    }

    [Test]
    public void LongestSpan_OfARecordedPond_IsQuick()
    {
        // 40 000 points a few centimetres apart round a 400 m pond: every pair was 800 million sums.
        var pond = Polygon(Enumerable.Range(0, 40000).Select(i =>
        {
            double a = 2 * Math.PI * i / 40000;
            return (200 * Math.Cos(a) + 3 * Math.Sin(7 * a), 120 * Math.Sin(a));
        }));
        var clock = Stopwatch.StartNew();
        double span = pond.LongestSpan();
        Assert.That(clock.Elapsed.TotalSeconds, Is.LessThan(2));
        Assert.That(span, Is.GreaterThan(399).And.LessThan(407));
    }

    [Test]
    public void OutlineStamp_SeesEveryCorner_AndOnlyTheOutline()
    {
        var a = Polygon(new[] { (30.0, 40.0), (40.0, 40.0), (50.0, 40.0), (50.0, 60.0), (40.0, 60.0), (30.0, 60.0) });
        var same = Polygon(new[] { (30.0, 40.0), (40.0, 40.0), (50.0, 40.0), (50.0, 60.0), (40.0, 60.0), (30.0, 60.0) });
        Assert.That(same.OutlineStamp, Is.EqualTo(a.OutlineStamp));
        long before = a.OutlineStamp;

        // Corner 1: neither the first nor the middle one, the two read before.
        a.Points[1] = new BoundaryPoint(40, 38, 0);
        a.UpdateBounds();
        Assert.That(a.OutlineStamp, Is.Not.EqualTo(before));

        a.Points[1] = new BoundaryPoint(40, 40, 0);
        a.UpdateBounds();
        Assert.That(a.OutlineStamp, Is.EqualTo(before), "back where it was");

        a.Points = a.Points.Take(5).ToList(); // a new list: no UpdateBounds needed
        Assert.That(a.OutlineStamp, Is.Not.EqualTo(before));
    }
}
