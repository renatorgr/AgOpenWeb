// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Timing;

namespace AgOpenWeb.Models.Tests.Timing;

/// <summary>The motion clock of the simulator's time-lapse (the sim bar's 10x). Fork only.</summary>
[TestFixture]
public class MotionClockTests
{
    private TestClock _clock = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new TestClock();
        _clock.AdvanceSeconds(100);
        Clock.Set(_clock);
        MotionClock.Reset();
    }

    [TearDown]
    public void TearDown()
    {
        MotionClock.Reset();
        Clock.Reset();
    }

    [Test]
    public void At_scale_1_it_is_the_real_clock()
    {
        Assert.That(MotionClock.Scale, Is.EqualTo(1.0));
        Assert.That(MotionClock.GetTimestamp(), Is.EqualTo(_clock.GetTimestamp()));
        _clock.AdvanceMs(37);
        Assert.That(MotionClock.GetTimestamp(), Is.EqualTo(_clock.GetTimestamp()));
    }

    [Test]
    public void At_scale_10_ten_seconds_pass_in_each_real_second()
    {
        MotionClock.SetScale(10);
        long start = MotionClock.GetTimestamp();
        _clock.AdvanceSeconds(1.5);
        double motionSeconds = (MotionClock.GetTimestamp() - start) / (double)_clock.Frequency;
        Assert.That(motionSeconds, Is.EqualTo(15.0).Within(1e-6));
    }

    [Test]
    public void A_change_of_scale_never_jumps()
    {
        long before = MotionClock.GetTimestamp();
        MotionClock.SetScale(10);
        Assert.That(MotionClock.GetTimestamp(), Is.EqualTo(before), "on to 10x");

        _clock.AdvanceSeconds(2);   // 20 s of motion
        long reached = MotionClock.GetTimestamp();
        MotionClock.SetScale(1);
        Assert.That(MotionClock.GetTimestamp(), Is.EqualTo(reached), "back to 1x");

        _clock.AdvanceSeconds(1);   // then real time again
        double seconds = (MotionClock.GetTimestamp() - reached) / (double)_clock.Frequency;
        Assert.That(seconds, Is.EqualTo(1.0).Within(1e-6));
    }

    [Test]
    public void It_never_runs_backwards_across_changes()
    {
        long last = MotionClock.GetTimestamp();
        foreach (double scale in new[] { 10.0, 1.0, 10.0, 1.0 })
        {
            MotionClock.SetScale(scale);
            for (int i = 0; i < 5; i++)
            {
                _clock.AdvanceMs(33);
                long now = MotionClock.GetTimestamp();
                Assert.That(now, Is.GreaterThan(last));
                last = now;
            }
        }
    }

    [Test]
    public void FromReal_maps_a_real_instant_onto_motion_time()
    {
        MotionClock.SetScale(10);
        long real = _clock.GetTimestamp();
        Assert.That(MotionClock.FromReal(real), Is.EqualTo(MotionClock.GetTimestamp()));
        long later = real + _clock.Frequency / 10;   // 0.1 s real
        Assert.That((MotionClock.FromReal(later) - MotionClock.FromReal(real)) / (double)_clock.Frequency,
            Is.EqualTo(1.0).Within(1e-6));
    }

    [TestCase(0.0)]
    [TestCase(-1.0)]
    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    public void A_scale_that_is_not_positive_is_refused(double scale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MotionClock.SetScale(scale));
        Assert.That(MotionClock.Scale, Is.EqualTo(1.0));
    }
}
