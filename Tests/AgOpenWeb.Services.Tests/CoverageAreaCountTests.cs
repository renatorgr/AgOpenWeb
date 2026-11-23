// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Coverage;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// The ground inside an area and how much of it is painted, counted on the coverage map
/// (<see cref="CoverageMapService.MeasureCoverage"/>): what the field still has to paint (the status bar's
/// "Left", the card's Remaining and Time left) and what a selection drawn on the map has. A 100 x 100 m field
/// with a 20 x 20 m obstacle in it. Paint outside the fence, in the obstacle and painted twice is not field
/// painted: the status bar took every pass painted from the headland's area and read "Left -14%" (Tapada da
/// Casa, simulator, 23 Nov 2026). Fork only.
/// </summary>
[TestFixture]
public class CoverageAreaCountTests
{
    private CoverageMapService _coverage = null!;

    private static readonly Vec2[] Fence = Rect(0, 100, 0, 100);
    private static readonly Vec2[] Obstacle = Rect(40, 60, 40, 60);
    private static readonly IReadOnlyList<IReadOnlyList<Vec2>> Field = new[] { Fence, Obstacle };

    [SetUp]
    public void SetUp()
    {
        _coverage = new CoverageMapService(new ConfigurationStore());
        _coverage.SetFieldBounds(-50, 150, -50, 150);
    }

    private static Vec2[] Rect(double minE, double maxE, double minN, double maxN) => new[]
    {
        new Vec2(minE, minN), new Vec2(maxE, minN), new Vec2(maxE, maxN), new Vec2(minE, maxN),
    };

    /// <summary>Paint the cells of [minE, maxE) x [minN, maxN) (whole decimetres).</summary>
    private void Paint(double minE, double maxE, double minN, double maxN) =>
        _coverage.MarkRectangleCovered(minE + 0.01, maxE - 0.15, minN + 0.01, maxN - 0.15);

    [Test]
    public void Nothing_painted_the_whole_field_less_its_obstacle_is_left()
    {
        var (inside, painted) = _coverage.MeasureCoverage(Field);

        Assert.Multiple(() =>
        {
            Assert.That(inside, Is.EqualTo(10000 - 400).Within(0.5), "the boundary less the obstacle, as the card's Total");
            Assert.That(painted, Is.Zero);
        });
    }

    [Test]
    public void Paint_outside_the_fence_in_the_obstacle_and_twice_over_is_not_counted()
    {
        Paint(-10, 30, 0, 100);   // a pass half over the fence: 30 m of it inside
        Paint(20, 30, 0, 100);    // the same ground again (overlap)
        Paint(40, 60, 40, 60);    // the obstacle, sprayed over

        var (inside, painted) = _coverage.MeasureCoverage(Field);

        Assert.Multiple(() =>
        {
            Assert.That(painted, Is.EqualTo(30 * 100).Within(0.5), "the 30 x 100 m inside the fence, once");
            Assert.That(inside - painted, Is.EqualTo(9600 - 3000).Within(0.5));
            Assert.That(_coverage.ActualAreaCovered, Is.GreaterThan(inside - (inside - painted) + 400),
                "the map's own count has the paint outside and in the obstacle too");
        });
    }

    [Test]
    public void A_field_painted_all_over_and_past_its_edges_has_nothing_left_never_less()
    {
        Paint(-5, 105, -5, 105);

        var (inside, painted) = _coverage.MeasureCoverage(Field);

        Assert.That(inside - painted, Is.EqualTo(0).Within(1e-6),
            "everything inside painted: 0 left, where the worked area (12100 m²) against the field read -26 %");
    }

    [Test]
    public void A_selection_counts_only_its_ground_inside_the_field()
    {
        Paint(0, 50, 0, 100);
        // Half outside the fence to the west, over half the obstacle.
        var selection = new[] { Rect(-50, 50, 0, 100) };

        var (inside, painted) = _coverage.MeasureCoverage(selection, Field);

        Assert.Multiple(() =>
        {
            Assert.That(inside, Is.EqualTo(50 * 100 - 10 * 20).Within(0.5), "0-50 m inside the fence, less the obstacle's half");
            Assert.That(painted, Is.EqualTo(inside).Within(0.5), "all of it painted");
        });
    }

    [Test]
    public void A_selection_inside_the_field_with_part_painted()
    {
        Paint(0, 30, 0, 100);
        var selection = new[] { Rect(20, 80, 20, 80) };

        var (inside, painted) = _coverage.MeasureCoverage(selection, Field);

        Assert.Multiple(() =>
        {
            Assert.That(inside, Is.EqualTo(60 * 60 - 400).Within(0.5));
            Assert.That(painted, Is.EqualTo(10 * 60).Within(0.5), "the strip 20-30 m painted");
        });
    }

    [Test]
    public void A_slanted_edge_is_counted_to_its_area()
    {
        var triangle = new[] { new[] { new Vec2(0, 0), new Vec2(100, 0), new Vec2(0, 73) } };

        var (inside, _) = _coverage.MeasureCoverage(triangle);

        Assert.That(inside, Is.EqualTo(100 * 73 / 2.0).Within(100 * 73 / 2.0 * 0.002));
    }

    [Test]
    public void A_ring_closed_on_its_first_point_counts_the_same()
    {
        var closed = new[] { Fence.Append(Fence[0]).ToArray(), Obstacle.Append(Obstacle[0]).ToArray() };

        Assert.That(_coverage.MeasureCoverage(closed).InsideSqM,
            Is.EqualTo(_coverage.MeasureCoverage(Field).InsideSqM));
    }

    [Test]
    public void No_grid_yet_the_ground_is_counted_and_nothing_painted()
    {
        var empty = new CoverageMapService(new ConfigurationStore());

        var (inside, painted) = empty.MeasureCoverage(Field);

        Assert.Multiple(() =>
        {
            Assert.That(inside, Is.EqualTo(9600).Within(0.5));
            Assert.That(painted, Is.Zero);
        });
    }

    [Test]
    public void Bits_are_counted_as_one_by_one()
    {
        var rng = new Random(7);
        var bits = new byte[200];
        rng.NextBytes(bits);
        for (int k = 0; k < 500; k++)
        {
            long from = rng.Next(0, 1600), to = rng.Next((int)from, 1601);
            long naive = 0;
            for (long b = from; b < to; b++) if ((bits[b >> 3] & (1 << (int)(b & 7))) != 0) naive++;
            Assert.That(CoverageAreaCount.CountBits(bits, from, to), Is.EqualTo(naive), $"bits {from}-{to}");
        }
    }
}
