// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services.Threading;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The sim bar's 10x is a time-lapse (fork only): ten seconds of driving in each real second,
/// at the speed shown. It used to multiply the speed by 10, so guidance, U-turns and sections
/// worked at 100-250 km/h and the turns were not made (official issue #726).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore, Clock and MotionClock are static.
public class SimulatorTimeLapseTests
{
    private MainViewModelBuilder _b = null!;
    private MainViewModel _vm = null!;
    private TestClock _clock = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _clock = new TestClock();
        _clock.AdvanceSeconds(100);
        Clock.Set(_clock);
        MotionClock.Reset();
        _b = new MainViewModelBuilder();
        _vm = _b.Build();
    }

    [TearDown]
    public void TearDown()
    {
        MotionClock.Reset();
        Clock.Reset();
    }

    private ManualUiTimer SimulatorTimer =>
        _b.Timers.Single(t => t.Interval == TimeSpan.FromMilliseconds(33));

    [Test]
    public void Ten_x_runs_time_faster_not_the_tractor()
    {
        _vm.IsSimulatorEnabled = true;
        _vm.SimulatorSpeedKph = 10;
        _vm.IsSimulatorSpeed10x = true;

        Assert.That(_b.SimulatorService.StepDistance, Is.EqualTo(10 / 40.0), "the tractor drives at 10 km/h");
        Assert.That(_vm.SimulatorSpeedDisplay, Does.StartWith("10.0"), "the speed shown is the speed driven");
        Assert.That(MotionClock.Scale, Is.EqualTo(10.0), "ten seconds of driving per real second");
        _b.GpsPipelineService.Received(1).SetSimulatorTimeLapse(true);
    }

    [Test]
    public void Off_again_back_to_real_time()
    {
        _vm.IsSimulatorEnabled = true;
        _vm.IsSimulatorSpeed10x = true;

        _vm.IsSimulatorSpeed10x = false;

        Assert.That(MotionClock.Scale, Is.EqualTo(1.0));
        _b.GpsPipelineService.Received(1).SetSimulatorTimeLapse(false);
    }

    [Test]
    public void Only_with_the_simulator_on()
    {
        _vm.IsSimulatorEnabled = false;
        _vm.IsSimulatorSpeed10x = true;
        Assert.That(MotionClock.Scale, Is.EqualTo(1.0), "10x chosen, simulator off");
        _b.GpsPipelineService.DidNotReceive().SetSimulatorTimeLapse(true);

        _vm.IsSimulatorEnabled = true;
        Assert.That(MotionClock.Scale, Is.EqualTo(10.0), "simulator on");

        _vm.IsSimulatorEnabled = false;
        Assert.That(MotionClock.Scale, Is.EqualTo(1.0), "simulator off: a receiver's fix is never on 10x time");
        _b.GpsPipelineService.Received(1).SetSimulatorTimeLapse(false);
    }

    [Test]
    public void Each_timer_tick_drives_the_time_passed_in_the_simulators_own_steps()
    {
        _vm.IsSimulatorEnabled = true;
        _vm.IsSimulatorSpeed10x = true;
        _vm.SimulatorSteerAngle = 5;
        _b.SimulatorService.ClearReceivedCalls();

        _clock.AdvanceMs(33);       // 0.33 s of driving: three fixes of three 1/30 s steps
        SimulatorTimer.Fire();

        _b.SimulatorService.Received(9).Tick(5, Arg.Is<double>(dt => Math.Abs(dt - 1 / 30.0) < 1e-9));
        _b.SimulatorService.DidNotReceive().Tick(Arg.Any<double>());

        _clock.AdvanceMs(33);       // 0.03 s left over + 0.33 s: three more fixes
        SimulatorTimer.Fire();
        _b.SimulatorService.Received(18).Tick(5, Arg.Any<double>());
    }

    [Test]
    public void A_long_stall_is_let_go_not_driven_in_one_burst()
    {
        _vm.IsSimulatorEnabled = true;
        _vm.IsSimulatorSpeed10x = true;
        _b.SimulatorService.ClearReceivedCalls();

        _clock.AdvanceSeconds(5);   // 50 s of driving missed (the PC stalled)
        SimulatorTimer.Fire();

        _b.SimulatorService.Received(3).Tick(Arg.Any<double>(), Arg.Any<double>());
    }

    [Test]
    public void At_1x_the_simulator_steps_as_before()
    {
        _vm.IsSimulatorEnabled = true;
        _b.SimulatorService.ClearReceivedCalls();

        _clock.AdvanceMs(33);
        SimulatorTimer.Fire();

        _b.SimulatorService.Received(1).Tick(Arg.Any<double>());
        _b.SimulatorService.DidNotReceive().Tick(Arg.Any<double>(), Arg.Any<double>());
    }

    [Test]
    public void The_backend_stopping_ends_the_time_lapse()
    {
        _vm.IsSimulatorEnabled = true;
        _vm.IsSimulatorSpeed10x = true;

        _vm.StopBackgroundLoops();

        Assert.That(MotionClock.Scale, Is.EqualTo(1.0), "the process may outlive the backend (Android, iOS)");
    }
}
