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
    public async Task NoPlateau_FallsBackToLastSample_WithoutHanging()
    {
        // If the WAS never settles (e.g. simulator with no clamp, runaway
        // hydraulics), the detector must time out and return the last
        // sample rather than hang forever. The wizard caps the poll loop
        // at PlateauTimeoutMs internally.
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

        // We don't assert exact values — the timeout-fallback path is
        // operational, not metric. The point: it returned without
        // hanging, and CalibrationCompleted is true so OnLeaving will
        // still persist *something*.
        Assert.That(step.Phase, Is.EqualTo(MaxSteeringAnglePhase.Complete));
        Assert.That(step.CalibrationCompleted, Is.True);
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
    [TestCase(0, 12.4, -3.6, "turned right but hardly left")]
    [TestCase(0, 2, -25, "turned left but hardly right")]
    [TestCase(7.2, 7.2, 7.2, "didn't move")]
    [TestCase(0, 3, -3, "didn't move")]
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
            Assert.That(step.PhaseResult, Does.Contain("stopped short of the lock").And.Contain("not changed"));
            Assert.That(step.PhaseResult, Does.Contain(MaxSteeringAngleStepViewModel.WhatToCheckHeader + "Run the test again"));
            Assert.That(step.PhaseResult, Does.Contain("relief valve").And.Contain("never the tractor's own"));
        });
        SetActive(step, false);
        Assert.That(_store.Vehicle.MaxSteerAngle, Is.EqualTo(33), "a short reading must not become the max steer angle");
    }

    [Test]
    public async Task Wheels_that_turn_one_way_only_say_what_to_check()
    {
        // The tester's first run: 12.4° right, 3.6° left (a 3 would have been saved before #170's check).
        _store.Vehicle.MaxSteerAngle = 33;
        var step = StepWithLocks(right: 12.4, left: 3.6);
        SetActive(step, true);

        await step.RunMaxAngleMeasurementAsync();

        Assert.That(step.PhaseResult, Does.Contain("turned right but hardly left").And.Contain("not changed"));
        Assert.That(step.PhaseResult, Does.Contain(MaxSteeringAngleStepViewModel.WhatToCheckHeader));
        SetActive(step, false);
        Assert.That(_store.Vehicle.MaxSteerAngle, Is.EqualTo(33));
    }

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
    public async Task Wheels_that_turn_the_wrong_way_get_no_hydraulic_checks()
    {
        // The sensor or the motor wiring, not the push: the motor test is what to run.
        var step = StepWithLocks(right: -30, left: -30);

        await step.RunMaxAngleMeasurementAsync();

        Assert.That(step.PhaseResult, Is.EqualTo(MaxSteeringAngleStepViewModel.WrongWayText));
    }

    /// <summary>
    /// Wheels that run to <paramref name="right"/>° when steered right and to −<paramref name="left"/>°
    /// when steered left, then stay there.
    /// </summary>
    private MaxSteeringAngleStepViewModel StepWithLocks(double right, double left)
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
            // 2° a reading towards where the wheels stop.
            angle += System.Math.Clamp(target - angle, -2, 2);
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
