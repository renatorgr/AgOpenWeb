// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// #99: the AutoSteer panel edits GuidanceConfig's steering-tuning values directly.
/// </summary>
[TestFixture]
public class GuidanceConfigTests
{
    [TestCase(1.0, 0.0)]
    [TestCase(0.2, -80.0)]  // AgOpenGPS slider minimum
    [TestCase(2.0, 100.0)]  // AgOpenGPS slider maximum
    [TestCase(1.25, 25.0)]
    public void UTurnCompensation_PercentRoundTrips(double multiplier, double percent)
    {
        Assert.That(GuidanceConfig.UTurnCompensationToPercent(multiplier), Is.EqualTo(percent).Within(1e-9));
        Assert.That(GuidanceConfig.UTurnCompensationFromPercent(percent), Is.EqualTo(multiplier).Within(1e-9));
    }

    [Test]
    public void The_distance_from_the_boundary_is_never_negative_and_always_a_number()
    {
        // A negative distance (the settings box takes one) put the turn line outside the fence.
        var g = new GuidanceConfig { UTurnDistanceFromBoundary = -3 };
        Assert.That(g.UTurnDistanceFromBoundary, Is.EqualTo(0));

        g.UTurnDistanceFromBoundary = 4.5;
        Assert.That(g.UTurnDistanceFromBoundary, Is.EqualTo(4.5));

        g.UTurnDistanceFromBoundary = double.NaN;
        Assert.That(g.UTurnDistanceFromBoundary, Is.EqualTo(GuidanceConfig.DefaultUTurnDistanceFromBoundary));
        g.UTurnDistanceFromBoundary = double.NegativeInfinity;
        Assert.That(g.UTurnDistanceFromBoundary, Is.EqualTo(GuidanceConfig.DefaultUTurnDistanceFromBoundary));
    }

    [Test]
    public void ResetSteeringTuning_RestoresNewProfileDefaults_AndLeavesUTurnGeometry()
    {
        var g = new GuidanceConfig
        {
            IsPurePursuit = false,
            GoalPointLookAheadHold = 9,
            GoalPointLookAheadMult = 2.5,
            PurePursuitIntegralGain = 0.4,
            StanleyDistanceErrorGain = 3,
            StanleyHeadingErrorGain = 3,
            UTurnCompensation = 1.8,
            UTurnRadius = 12,
        };

        g.ResetSteeringTuning();

        Assert.Multiple(() =>
        {
            Assert.That(g.IsPurePursuit, Is.True);
            Assert.That(g.GoalPointLookAheadHold, Is.EqualTo(3.0)); // AgOpenGPS's defaults (checklist 10.7)
            Assert.That(g.GoalPointLookAheadMult, Is.EqualTo(1.5));
            Assert.That(g.PurePursuitIntegralGain, Is.EqualTo(0.0));
            Assert.That(g.StanleyDistanceErrorGain, Is.EqualTo(1.0));
            Assert.That(g.StanleyHeadingErrorGain, Is.EqualTo(1.0));
            Assert.That(g.UTurnCompensation, Is.EqualTo(1.0));
            Assert.That(g.UTurnRadius, Is.EqualTo(12), "Not a steering-tuning value — must be left alone");
        });
    }

    // "NaN" parses as a number and passes Math.Max(0, x); every "gap longer than the setting"
    // test is then false, so a headland gap of any length was driven across.
    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    [TestCase(double.NegativeInfinity)]
    [TestCase(-5.0)]
    public void A_gap_setting_that_is_no_length_is_off(double value)
    {
        var g = new GuidanceConfig { UTurnCrossGap = 12 };
        g.UTurnCrossGap = value;
        Assert.That(g.UTurnCrossGap, Is.EqualTo(0.0));
    }

    [Test]
    public void A_gap_setting_keeps_a_length()
    {
        var g = new GuidanceConfig { UTurnCrossGap = 12.5 };
        Assert.That(g.UTurnCrossGap, Is.EqualTo(12.5));
    }
}
