// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Services.Gps;
using static AgOpenWeb.Services.Gps.RtkLossHold;

namespace AgOpenWeb.Services.Tests.Gps;

/// <summary>
/// Holding the line when RTK is lost (fork only): the receiver's position steps sideways when it
/// leaves RTK fixed; the step is taken out, held for up to 3 minutes, and let go gently.
/// </summary>
[TestFixture]
public class RtkLossHoldTests
{
    private const double Speed = 2.5; // m/s, 9 km/h
    private const double Rate = 0.1;  // 10 Hz

    private RtkLossHold _hold = null!;
    private double _t;
    private int _epoch;

    [SetUp]
    public void SetUp()
    {
        _hold = new RtkLossHold();
        _t = 0;
        _epoch = 0;
    }

    /// <summary>Where the tractor really is at an epoch: driving north up the line E = 0.</summary>
    private static (double e, double n) Truth(int epoch) => (0, epoch * Rate * Speed);

    /// <summary>The next fix, <paramref name="error"/> being the receiver's error east, m.</summary>
    private Step Fix(int quality, double error = 0, bool enabled = true, double late = 0, int epochs = 1)
    {
        _epoch += epochs;
        _t += epochs * Rate + late;
        var (e, n) = Truth(_epoch);
        return _hold.Update(quality, e + error, n, _t, enabled);
    }

    private void Drive(int fixes, int quality, double error = 0, bool enabled = true)
    {
        for (int i = 0; i < fixes; i++) Fix(quality, error, enabled);
    }

    /// <summary>Where AgOpenWeb puts the tractor east of the line: the receiver's position plus the offset.</summary>
    private double ShownEast(double error) => error + _hold.OffsetE;

    [Test]
    public void The_jump_when_RTK_is_lost_is_taken_out()
    {
        Drive(30, 4);

        var step = Fix(1, error: 0.9);

        Assert.Multiple(() =>
        {
            Assert.That(step.What, Is.EqualTo(Change.Held));
            Assert.That(step.StepM, Is.EqualTo(0.9).Within(0.01));
            Assert.That(ShownEast(0.9), Is.EqualTo(0).Within(0.01), "the tractor stays on the line");
            Assert.That(_hold.OffsetN, Is.EqualTo(0).Within(0.01), "the travel north is not taken for a step");
            Assert.That(_hold.IsHolding, Is.True);
        });
    }

    [Test]
    public void While_held_the_offset_stays_and_the_receiver_wander_is_followed()
    {
        Drive(30, 4);
        Fix(1, error: 0.9);

        Drive(50, 1, error: 1.1); // the plain fix wanders 0.2 m further over 5 s

        Assert.Multiple(() =>
        {
            Assert.That(_hold.OffsetE, Is.EqualTo(-0.9).Within(0.01), "the offset is held, not eased out");
            Assert.That(ShownEast(1.1), Is.EqualTo(0.2).Within(0.01), "only the wander since the loss is left");
            Assert.That(_hold.HeldForS, Is.EqualTo(5).Within(0.01));
        });
    }

    [Test]
    public void Every_change_of_fix_while_held_is_taken_out_too()
    {
        Drive(30, 4);
        Fix(5, error: 0.3);  // float
        Drive(20, 5, error: 0.3);

        var step = Fix(2, error: 1.2); // then DGPS

        Assert.Multiple(() =>
        {
            Assert.That(step.StepM, Is.EqualTo(0.9).Within(0.01));
            Assert.That(ShownEast(1.2), Is.EqualTo(0).Within(0.01));
        });
    }

    [Test]
    public void When_RTK_is_back_its_step_is_taken_out_and_the_offset_goes_out_gently()
    {
        Drive(30, 4);
        Fix(1, error: 0.9);
        Drive(100, 1, error: 1.3); // 10 s later the fix is 0.4 m further off

        var back = Fix(4, error: 0);

        Assert.Multiple(() =>
        {
            Assert.That(back.What, Is.EqualTo(Change.Back));
            Assert.That(ShownEast(0), Is.EqualTo(0.4).Within(0.01), "no jump when RTK fixed is back");
            Assert.That(_hold.IsHolding, Is.False);
        });

        Drive(40, 4); // 4 s: 0.2 m let go
        Assert.That(ShownEast(0), Is.EqualTo(0.2).Within(0.01));
        Drive(60, 4);
        Assert.That(_hold.OffsetM, Is.Zero, "all let go after 8 s");
    }

    [Test]
    public void The_hold_ends_after_3_minutes()
    {
        Drive(30, 4);
        Fix(1, error: 0.9);

        Change last = Change.None;
        for (int i = 0; i < (int)(HoldLimitS / Rate) + 1 && last != Change.LimitReached; i++)
            last = Fix(1, error: 0.9).What;

        Assert.That(last, Is.EqualTo(Change.LimitReached));
        Assert.That(_hold.IsHolding, Is.False);
        Drive(100, 1, error: 0.9);
        Assert.That(_hold.OffsetE, Is.EqualTo(-0.4).Within(0.01), "then the offset goes out at 5 cm/s");
        var step = Fix(2, error: 0.3);
        Assert.That(step.StepM, Is.Zero, "and a change of fix after the limit is followed");
    }

    [Test]
    public void Off_the_position_is_followed_as_before()
    {
        Drive(30, 4, enabled: false);

        var step = Fix(1, error: 0.9, enabled: false);

        Assert.Multiple(() =>
        {
            Assert.That(step.What, Is.EqualTo(Change.None));
            Assert.That(_hold.OffsetM, Is.Zero);
        });
    }

    [Test]
    public void Turned_off_while_held_the_offset_goes_out_gently()
    {
        Drive(30, 4);
        Fix(1, error: 1.0);

        Drive(40, 1, error: 1.0, enabled: false);

        Assert.That(_hold.OffsetE, Is.EqualTo(-0.8).Within(0.01));
    }

    [Test]
    public void A_step_too_large_for_a_receiver_is_followed()
    {
        Drive(30, 4);

        var step = Fix(1, error: 8);

        Assert.That((step.StepM, _hold.OffsetM), Is.EqualTo((0.0, 0.0)));
    }

    [Test]
    public void After_a_gap_in_the_fixes_the_step_is_not_measured()
    {
        Drive(30, 4);

        var step = Fix(1, error: 0.9, epochs: 15); // 1.5 s without a fix

        Assert.Multiple(() =>
        {
            Assert.That(step.StepM, Is.Zero, "a step can't be told from 1.5 s of travel");
            Assert.That(_hold.IsHolding, Is.True, "a later change while held is still taken out");
        });
    }

    [Test]
    public void A_fix_handled_late_or_skipped_by_the_receiver_is_measured_by_its_epochs()
    {
        Drive(30, 4);
        Fix(4, late: 0.04);  // arrived late: the next one comes early
        Fix(4, late: -0.04);

        var early = Fix(1, error: 0.9, late: -0.098); // handled in the same burst as the last
        Assert.That(early.StepM, Is.EqualTo(0.9).Within(0.02), "a burst is one epoch of travel, not none");

        _hold.Reset();
        Drive(30, 4);
        var skipped = Fix(1, error: 0.9, epochs: 2); // the receiver skipped an epoch
        Assert.That(skipped.StepM, Is.EqualTo(0.9).Within(0.02), "a skipped epoch is two of travel");
    }

    [Test]
    public void In_a_turn_the_step_is_still_measured()
    {
        // A 12 m circle at 2.5 m/s: the travel from the last fix turns 1.2° a fix.
        double r = 12, w = Speed / r, t = 0;
        (double e, double n) At(double s) => (r * Math.Sin(w * s), r - r * Math.Cos(w * s));
        for (int i = 0; i < 30; i++) { t += Rate; var p = At(t); _hold.Update(4, p.e, p.n, t, true); }

        t += Rate;
        var q = At(t);
        var step = _hold.Update(1, q.e + 0.9, q.n, t, true);

        Assert.That(step.StepM, Is.EqualTo(0.9).Within(0.03));
        Assert.That(q.e + 0.9 + _hold.OffsetE, Is.EqualTo(q.e).Within(0.03));
    }

    [Test]
    public void Standing_still_the_whole_step_is_taken_out()
    {
        for (int i = 0; i < 20; i++) { _t += Rate; _hold.Update(4, 5, 5, _t, true); }

        _t += Rate;
        var step = _hold.Update(5, 5.4, 4.7, _t, true);

        Assert.That((_hold.OffsetE, _hold.OffsetN), Is.EqualTo((-0.4, 0.3)).Using<(double, double)>((a, b) =>
            Math.Abs(a.Item1 - b.Item1) < 1e-9 && Math.Abs(a.Item2 - b.Item2) < 1e-9));
        Assert.That(step.StepM, Is.EqualTo(0.5).Within(1e-9));
    }
}
