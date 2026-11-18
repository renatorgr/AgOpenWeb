// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Threading.Tasks;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.ViewModels.Wizards.SteerWizard;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Pins the new max-steering-angle wizard step. The split-out step
/// must:
///   1. Detect a WAS plateau (consecutive samples within
///      PlateauThresholdDeg) and capture that angle, rather than read
///      a fixed-time snapshot mid-motion.
///   2. Capture both sides of the lock and persist
///      <c>min(left, right) * 0.9</c> as MaxSteerAngle on completion.
///   3. Honour cooperative cancellation by exiting the loop and
///      restoring free-drive state.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore singleton.
public class MaxSteeringAngleStepTests
{
    private IConfigurationService _configService = null!;
    private ConfigurationStore _store = null!;
    private IAutoSteerService _autoSteer = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new ConfigurationStore();
        ConfigurationStore.SetInstance(_store);

        _configService = Substitute.For<IConfigurationService>();
        _configService.Store.Returns(_store);

        _autoSteer = Substitute.For<IAutoSteerService>();
        _autoSteer.LastSteerData.Returns(SteerModuleData.Empty);
    }

    [Test]
    public async Task PlateauDetector_FiresOnStableSamples_AndPersistsMaxAngle()
    {
        // Simulator-style WAS: ramp up to ±35° then plateau. The
        // detector should fire once it sees PlateauStableSamples
        // consecutive samples within PlateauThresholdDeg of each other.
        var step = new MaxSteeringAngleStepViewModel(_configService, new AgOpenWeb.Services.Threading.InlineUiDispatcher(), _autoSteer);
        step.DelayFunc = (_, _) => Task.CompletedTask;

        // Right-lock sequence: 10, 20, 30, 35, 35, 35, 35, 35, 35, 35
        // then center, then left: -10, -25, -30, -30, -30, -30, -30, -30
        var rightSequence = new[] { 10.0, 20.0, 30.0, 35.0, 35.0, 35.0, 35.0, 35.0, 35.0, 35.0 };
        var leftSequence = new[] { -10.0, -25.0, -30.0, -30.0, -30.0, -30.0, -30.0, -30.0 };

        int call = 0;
        bool measuringLeft = false;
        step.ReadWasAngle = () =>
        {
            // Transition to "left" sequence the first time we hit
            // Phase.MeasuringLeft. (See the SetFreeDriveAngle stub below.)
            if (measuringLeft)
            {
                var v = leftSequence[System.Math.Min(call - rightSequence.Length, leftSequence.Length - 1)];
                call++;
                return v;
            }
            else
            {
                var v = rightSequence[System.Math.Min(call, rightSequence.Length - 1)];
                call++;
                if (call >= rightSequence.Length)
                {
                    // Reset call counter for the left-side polling.
                    measuringLeft = true;
                    call = rightSequence.Length;
                }
                return v;
            }
        };

        await step.RunMaxAngleMeasurementAsync();

        Assert.That(step.DetectedMaxAngleRight, Is.EqualTo(35.0).Within(0.5));
        Assert.That(step.DetectedMaxAngleLeft, Is.EqualTo(30.0).Within(0.5));
        // min(35, 30) * 0.9 = 27 (truncated).
        Assert.That(step.MaxSteerAngle, Is.EqualTo(27));
        Assert.That(step.CalibrationCompleted, Is.True);
        Assert.That(step.Phase, Is.EqualTo(MaxSteeringAnglePhase.Complete));
        Assert.That(step.PhaseResult, Does.Not.Contain("What to check"));
    }

    [Test]
    public async Task NoPlateau_TimesOut_WithoutHanging_AndIsNoLock()
    {
        // If the WAS never settles (e.g. simulator with no clamp, runaway
        // hydraulics), the detector must time out rather than hang forever.
        // The wizard caps the poll loop at PlateauTimeoutMs internally.
        var step = new MaxSteeringAngleStepViewModel(_configService, new AgOpenWeb.Services.Threading.InlineUiDispatcher(), _autoSteer);
        step.DelayFunc = (_, _) => Task.CompletedTask;

        // Drifts the way it is commanded and never plateaus (a wheel drifting the
        // other way stops the test: the motor direction is wrong).
        double angle = 0, commanded = 0;
        _autoSteer.When(a => a.SetFreeDriveAngle(Arg.Any<double>())).Do(c =>
        {
            if (System.Math.Sign(c.Arg<double>()) != System.Math.Sign(commanded)) angle = 0;
            commanded = c.Arg<double>();
        });
        step.ReadWasAngle = () =>
        {
            angle += 0.5 * System.Math.Sign(commanded);
            return angle;
        };

        await step.RunMaxAngleMeasurementAsync();

        // The point: it returned without hanging. Wheels still turning when the time ran
        // out read no lock, so nothing is saved (30° each way here, and 27 was saved before).
        Assert.That(step.Phase, Is.EqualTo(MaxSteeringAnglePhase.WaitingToStart));
        Assert.That(step.CalibrationCompleted, Is.False);
        Assert.That(step.PhaseResult, Does.Contain("They were still turning when the 6 s for each side ran out."));
    }

    // ── #170: the wheels must actually move ───────────────────────────────

    [Test]
    public async Task Wheels_that_do_not_move_fail_and_nothing_is_saved()
    {
        // Reporter's case: free drive didn't move the wheels (module not steering), the WAS
        // sat at 7.2° and the old code saved it as both the right and left lock.
        _store.Vehicle.MaxSteerAngle = 34;
        var step = new MaxSteeringAngleStepViewModel(_configService, new AgOpenWeb.Services.Threading.InlineUiDispatcher(), _autoSteer);
        step.DelayFunc = (_, _) => Task.CompletedTask;
        step.ReadWasAngle = () => 7.2;
        SetActive(step, true);

        await step.RunMaxAngleMeasurementAsync();

        Assert.Multiple(() =>
        {
            Assert.That(step.CalibrationCompleted, Is.False);
            Assert.That(step.Phase, Is.EqualTo(MaxSteeringAnglePhase.WaitingToStart));
            Assert.That(step.PhaseResult, Does.Contain("didn't move").And.Contain("not changed"));
        });
        SetActive(step, false);
        Assert.That(_store.Vehicle.MaxSteerAngle, Is.EqualTo(34), "a failed test must not overwrite the max steer angle");
    }

    [TestCase(0, 35, -30, null)]
    [TestCase(0, 35, -3, "turned right but hardly left")]
    [TestCase(0, 2, -25, "turned left but hardly right")]
    [TestCase(0, 30, 28, "turned right but hardly left", Description = "went right and stayed there")]
    // Too little push either way: the start is somewhere between the two stops, so one reading
    // lands beside it. Not a fault on one side.
    [TestCase(1.9, 7.7, 0.7, "moved only 7.0° in all: steered right they stopped at 7.7°, steered left at 0.7° (start 1.9°)",
        Description = "a tester's load-sensing block, 18 Nov 2026")]
    [TestCase(0, 12.4, -3.6, "moved only 16.0° in all", Description = "the same block the day before")]
    [TestCase(7, 7.7, 0.7, "moved only 7.0° in all", Description = "the same stops, started beside the right one")]
    [TestCase(0, 3, -3, "moved only 6.0° in all")]
    [TestCase(7.2, 7.2, 7.2, "didn't move")]
    [TestCase(0, 2, -2, "didn't move")]
    [TestCase(0, -30, 30, "opposite way")]
    public void LockMovementError_cases(double start, double right, double left, string? expected)
    {
        var err = MaxSteeringAngleStepViewModel.LockMovementError(start, right, left);
        if (expected == null) Assert.That(err, Is.Null);
        else Assert.That(err, Does.Contain(expected));
    }

    // ── Wheels that stop short of a lock (a hydraulic block's relief set low) ──

    [Test]
    public async Task Wheels_that_stop_short_of_a_lock_say_what_to_check_and_nothing_is_saved()
    {
        // A load-sensing block with its relief valve still at the lowest: standing, the wheels
        // stopped at 14° right and 12° left; turned by hand they reach 36°.
        _store.Vehicle.MaxSteerAngle = 33;
        var step = StepWithLocks(right: 14, left: 12);
        SetActive(step, true);

        await step.RunMaxAngleMeasurementAsync();

        Assert.Multiple(() =>
        {
            Assert.That(step.CalibrationCompleted, Is.False);
            Assert.That(step.Phase, Is.EqualTo(MaxSteeringAnglePhase.WaitingToStart));
            Assert.That(step.PhaseResult, Does.StartWith("Right max: 14.0\nLeft max: 12.0\n"));
            Assert.That(step.PhaseResult, Does.Contain("\n⚠ The wheels stopped short of the lock").And.Contain("not changed"),
                "the warning is a line of its own that the page shows in a box");
            Assert.That(step.PhaseResult, Does.Contain(MaxSteeringAngleStepViewModel.WhatToCheckHeader + "Run the test again"));
            Assert.That(step.PhaseResult, Does.Contain("relief valve").And.Contain("never the tractor's own"));
            Assert.That(step.PhaseResult, Does.Contain("(a Keya)").And.Contain("current trip"), "a motor on the steering wheel stops short too");
            Assert.That(step.PhaseResult, Does.Contain("\n– Hydraulic valve block").And.Contain("\n– Motor on the steering wheel").And.Contain("\n1. "),
                "one point a line, each kind of steering its own, so the page can set it out as a list");
        });
        SetActive(step, false);
        Assert.That(_store.Vehicle.MaxSteerAngle, Is.EqualTo(33), "a short reading must not become the max steer angle");
    }

    [Test]
    public async Task Wheels_that_turn_a_little_each_way_are_not_called_one_sided()
    {
        // The tester's first run: 12.4° right, 3.6° left (a 3 would have been saved before #170's check).
        // Too little push both ways, and read as "turned right but hardly left".
        _store.Vehicle.MaxSteerAngle = 33;
        var step = StepWithLocks(right: 12.4, left: 3.6);
        SetActive(step, true);

        await step.RunMaxAngleMeasurementAsync();

        Assert.That(step.PhaseResult, Does.StartWith("⚠ The wheels moved only 16.0° in all").And.Contain("not changed"));
        Assert.That(step.PhaseResult, Does.Not.Contain("hardly"));
        Assert.That(step.PhaseResult, Does.Contain(MaxSteeringAngleStepViewModel.WhatToCheckHeader));
        SetActive(step, false);
        Assert.That(_store.Vehicle.MaxSteerAngle, Is.EqualTo(33));
    }

    [Test]
    public async Task Wheels_that_turn_one_way_only_say_what_to_check()
    {
        // A lock on the right and nothing past the start on the left: one side.
        _store.Vehicle.MaxSteerAngle = 33;
        var step = StepWithLocks(right: 32, left: 3.6);
        SetActive(step, true);

        await step.RunMaxAngleMeasurementAsync();

        Assert.That(step.PhaseResult, Does.StartWith("⚠ The wheels turned right but hardly left").And.Contain("not changed"));
        Assert.That(step.PhaseResult, Does.Contain(MaxSteeringAngleStepViewModel.WhatToCheckHeader));
        SetActive(step, false);
        Assert.That(_store.Vehicle.MaxSteerAngle, Is.EqualTo(33));
    }

    [Test]
    public async Task Slow_wheels_cut_off_on_the_way_back_are_told_so()
    {
        // The tester's block with its relief valve half a turn up (18 Nov 2026): start 2.3°,
        // right 33.1°, left -0.1°. At 5°/s the right push ran out of time 30° out and the left
        // one on the way back, at the start: "hardly left" alone reads as a fault on the left.
        _store.Vehicle.MaxSteerAngle = 31;
        var step = StepWithLocks(right: 36, left: 36, perReading: 0.5);
        SetActive(step, true);

        await step.RunMaxAngleMeasurementAsync();

        Assert.That(step.PhaseResult, Does.StartWith(
            "⚠ The wheels turned right but hardly left (start 0.0°, right 30.0°, left -0.5°). " +
            "They were still turning when the 6 s for each side ran out. Max steer angle not changed."));
        Assert.That(step.PhaseResult, Does.Contain("They keep turning, slowly"));
        SetActive(step, false);
        Assert.That(_store.Vehicle.MaxSteerAngle, Is.EqualTo(31));
    }

    [Test]
    public async Task A_side_cut_off_by_the_time_is_not_taken_as_a_lock()
    {
        // 10°/s: the right lock (36°) is reached, the left push starts from it and its 6 s end
        // at -25°, the wheels still turning. 25 read as the left lock, and 22 was saved.
        _store.Vehicle.MaxSteerAngle = 33;
        var step = StepWithLocks(right: 36, left: 36, perReading: 1);
        SetActive(step, true);

        await step.RunMaxAngleMeasurementAsync();

        Assert.Multiple(() =>
        {
            Assert.That(step.CalibrationCompleted, Is.False);
            Assert.That(step.Phase, Is.EqualTo(MaxSteeringAnglePhase.WaitingToStart));
            Assert.That(step.PhaseResult, Does.StartWith(
                "Right max: 36.0\nLeft max: 25.0\n" +
                "⚠ The wheels did not reach the lock in the test's time. " +
                "They were still turning left when the 6 s for that side ran out. Max steer angle not changed.\n"));
            Assert.That(step.PhaseResult, Does.Not.Contain("stopped short"));
            Assert.That(step.PhaseResult, Does.Contain(MaxSteeringAngleStepViewModel.WhatToCheckHeader));
        });
        SetActive(step, false);
        Assert.That(_store.Vehicle.MaxSteerAngle, Is.EqualTo(33), "a reading cut off on the way must not become the max steer angle");
    }

    [TestCase(false, false, "")]
    [TestCase(true, false, " They were still turning right when the 6 s for that side ran out.")]
    [TestCase(false, true, " They were still turning left when the 6 s for that side ran out.")]
    [TestCase(true, true, " They were still turning when the 6 s for each side ran out.")]
    public void StillTurningNote_cases(bool right, bool left, string expected) =>
        Assert.That(MaxSteeringAngleStepViewModel.StillTurningNote(right, left), Is.EqualTo(expected));

    [Test]
    public async Task Locks_at_twenty_degrees_or_more_are_taken()
    {
        var step = StepWithLocks(right: 21, left: 20);
        SetActive(step, true);

        await step.RunMaxAngleMeasurementAsync();

        Assert.That(step.Phase, Is.EqualTo(MaxSteeringAnglePhase.Complete));
        Assert.That(step.MaxSteerAngle, Is.EqualTo(18));
        Assert.That(step.PhaseResult, Does.Not.Contain("What to check"));
        SetActive(step, false);
        Assert.That(_store.Vehicle.MaxSteerAngle, Is.EqualTo(18));
    }

    [Test]
    public async Task The_log_gets_the_three_readings_and_the_result()
    {
        // The page's result names right and left only. A bug report holds the log and no
        // screenshot, and reading a run needs the start as well.
        var log = new System.Collections.Generic.List<string>();
        var step = StepWithLocks(right: 36, left: 34);
        step.Log = log.Add;
        SetActive(step, true);

        await step.RunMaxAngleMeasurementAsync();

        Assert.That(log, Is.EqualTo(new[]
        {
            "Maximum Steering Angle: Readings: start 0.0°, pushed right to 36.0°, pushed left to -34.0°.",
            $"Maximum Steering Angle: Right max: {36.0:F1}",
            $"Maximum Steering Angle: Left max: {34.0:F1}",
            "Maximum Steering Angle: Max steer angle (raw WAS): 30",
        }));
        SetActive(step, false);
    }

    [Test]
    public async Task The_log_gets_a_warning_with_its_list_a_line_at_a_time()
    {
        var log = new System.Collections.Generic.List<string>();
        var step = StepWithLocks(right: 12.4, left: 3.6);
        step.Log = log.Add;

        await step.RunMaxAngleMeasurementAsync();

        Assert.That(log[0], Is.EqualTo("Maximum Steering Angle: Readings: start 0.0°, pushed right to 12.4°, pushed left to -3.6°."));
        Assert.That(log, Has.Some.Contains("Max steer angle not changed."));
        Assert.That(log, Has.Some.Contains("What to check"));
        Assert.That(log, Has.All.StartsWith("Maximum Steering Angle: "));
        Assert.That(log, Has.None.Contains("\n"));
        Assert.That(log.Count, Is.GreaterThan(4), "the list's lines are log lines of their own");
    }

    [Test]
    public async Task Wheels_that_turn_the_wrong_way_get_no_hydraulic_checks()
    {
        // The sensor or the motor wiring, not the push: the motor test is what to run.
        var step = StepWithLocks(right: -30, left: -30);

        await step.RunMaxAngleMeasurementAsync();

        Assert.That(step.PhaseResult, Is.EqualTo(MaxSteeringAngleStepViewModel.WrongWayText));
    }

    /// <summary>
    /// Wheels that run to <paramref name="right"/>° when steered right and to −<paramref name="left"/>°
    /// when steered left, <paramref name="perReading"/>° a reading (ten readings a second), then stay there.
    /// </summary>
    private MaxSteeringAngleStepViewModel StepWithLocks(double right, double left, double perReading = 2)
    {
        var step = new MaxSteeringAngleStepViewModel(_configService, new AgOpenWeb.Services.Threading.InlineUiDispatcher(), _autoSteer);
        step.DelayFunc = (_, _) => Task.CompletedTask;
        double angle = 0, target = 0;
        _autoSteer.When(a => a.SetFreeDriveAngle(Arg.Any<double>())).Do(c =>
        {
            double cmd = c.Arg<double>();
            target = cmd > 0 ? right : cmd < 0 ? -left : 0;
        });
        step.ReadWasAngle = () =>
        {
            // So far a reading towards where the wheels stop.
            angle += System.Math.Clamp(target - angle, -perReading, perReading);
            return angle;
        };
        return step;
    }

    [Test]
    public void Hint_is_quiet_before_Start_even_though_the_module_reports_not_steering()
    {
        // AIO v4 reports "not steering" whenever the app sends status 0, so before Free Drive
        // the bit says nothing about the steer switch (#154).
        _autoSteer.LastSteerData.Returns(SteerModuleData.Empty with { SteerSwitchActive = true });
        var step = new MaxSteeringAngleStepViewModel(_configService, new AgOpenWeb.Services.Threading.InlineUiDispatcher(), _autoSteer);
        SetActive(step, true);
        Assert.That(step.RecordHint, Is.Empty);
        Assert.That(step.CanStartTest, Is.True);
    }

    private static void SetActive(AgOpenWeb.ViewModels.Wizards.WizardStepViewModel step, bool active) =>
        typeof(AgOpenWeb.ViewModels.Wizards.WizardStepViewModel)
            .GetProperty(nameof(AgOpenWeb.ViewModels.Wizards.WizardStepViewModel.IsActive))!.SetValue(step, active);
}
