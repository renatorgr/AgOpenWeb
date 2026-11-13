// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Every way AutoSteer goes off logs why ("[AutoSteer] Off: …"), so logs.txt in a bug report
/// tells an outside-boundary kickout from the button. The status line only showed the latest
/// message, and the steer module only sees PGN 254 stop asking it to steer.
/// </summary>
[TestFixture, NonParallelizable] // ConfigurationStore singleton.
public class AutoSteerOffReasonTests
{
    private MainViewModelBuilder _b = null!;
    private MainViewModel _vm = null!;
    private ListLogger _log = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _log = new ListLogger();
        _b = new MainViewModelBuilder
        {
            Logger = _log,
            ModuleCommunicationService = new ModuleCommunicationService(ConfigurationStore.Instance),
        };
        _vm = _b.Build();
        _vm.IsSimulatorEnabled = false;   // a receiver's fix: on the simulator the module is never asked to steer
        _vm.IsAutoSteerAvailable = true;
    }

    private string[] OffLines() => _log.Lines.Where(l => l.StartsWith("[AutoSteer] Off: ")).ToArray();

    private void Engage()
    {
        _vm.ToggleAutoSteerCommand!.Execute(null);
        Assume.That(_vm.IsAutoSteerEngaged, Is.True);
    }

    [Test]
    public void Button_logs_on_and_off()
    {
        Engage();
        _vm.ToggleAutoSteerCommand!.Execute(null);

        Assert.That(_log.Lines, Does.Contain("[AutoSteer] On: AutoSteer button"));
        Assert.That(OffLines(), Is.EqualTo(new[] { "[AutoSteer] Off: AutoSteer button" }));
    }

    [Test]
    public void Pipeline_kickout_logs_and_shows_its_reason()
    {
        Engage();
        string? msg = null; _vm.FailureReported += m => msg = m;

        _vm.ApplyGpsCycleResult(new GpsCycleResult
        {
            AutoSteerDisengagedThisCycle = true,
            DisengageReason = "AutoSteer disengaged - outside boundary",
        });

        Assert.That(_vm.IsAutoSteerEngaged, Is.False);
        Assert.That(OffLines(), Is.EqualTo(new[] { "[AutoSteer] Off: AutoSteer disengaged - outside boundary" }));
        Assert.That(msg, Is.EqualTo("AutoSteer disengaged - outside boundary"), "only the app knows why: say it on screen");
    }

    [Test]
    public void Button_shows_no_notice()
    {
        Engage();
        int notices = 0; _vm.FailureReported += _ => notices++;

        _vm.ToggleAutoSteerCommand!.Execute(null);

        Assert.That(notices, Is.Zero, "the operator knows why");
    }

    [Test]
    public void Sensor_kickout_turns_AutoSteer_off_and_says_why()
    {
        Engage();
        string? msg = null; _vm.FailureReported += m => msg = m;
        const string reason = "pressure sensor 41% reached the trip point (36%)";

        _b.AutoSteerService.SensorKickout += Raise.Event<EventHandler<string>>(_b.AutoSteerService, reason);

        Assert.That(_vm.IsAutoSteerEngaged, Is.False, "the button must not stay on while nothing steers");
        Assert.That(OffLines(), Is.EqualTo(new[] { "[AutoSteer] Off: " + reason }));
        Assert.That(msg, Is.EqualTo("AutoSteer off: " + reason));
    }

    [Test]
    public void Guidance_lost_logs_its_reason()
    {
        Engage();

        _b.AutoSteerService.GuidanceLost += Raise.Event<EventHandler>(_b.AutoSteerService, EventArgs.Empty);

        Assert.That(OffLines(), Is.EqualTo(new[] { "[AutoSteer] Off: no GPS/guidance update for over 1 s" }));
    }

    [Test]
    public void Gps_lost_logs_its_reason()
    {
        Engage();
        var t0 = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

        _vm.UpdateGpsLost(false, t0);
        _vm.UpdateGpsLost(false, t0.AddMilliseconds(MainViewModel.GpsLostAfterMs));

        Assert.That(OffLines(), Is.EqualTo(new[] { "[AutoSteer] Off: GPS data lost" }));
    }

    // PGN 253 byte 11 bit 1 is the module's steer-switch state: 1 = not steering.
    private void ModuleSteering(bool steering)
    {
        _b.AutoSteerService.LastSteerData.Returns(new SteerModuleData(
            ActualSteerAngle: 0, ImuHeading: 0, ImuRoll: 0, WorkSwitchActive: false,
            SteerSwitchActive: !steering, RemoteButtonPressed: false, VwasFusionActive: false, PwmDisplay: 0));
        typeof(MainViewModel).GetMethod("UpdateModuleSwitches", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_vm, null);
    }

    [Test]
    public void Module_stop_logs_the_sensor_reading()
    {
        // Steer switch / button: Button, with a pressure sensor. The module's own kickout
        // clears the same bit as its button, so the reading is what tells them apart.
        var a = ConfigurationStore.Instance.AutoSteer;
        a.ExternalEnable = 2;
        a.PressureSensorEnabled = true;
        a.PressureTripPoint = 36;
        _vm.State.Connections.IsAutoSteerDataOk = true;
        _b.AutoSteerService.SensorPercent.Returns(41.2);

        ModuleSteering(false);
        ModuleSteering(true);
        Assume.That(_vm.IsAutoSteerEngaged, Is.True);
        ModuleSteering(false);

        Assert.That(_vm.IsAutoSteerEngaged, Is.False);
        Assert.That(_log.Lines, Does.Contain("[AutoSteer] On: steer switch or button on the steer module"));
        Assert.That(OffLines(), Is.EqualTo(new[]
        {
            "[AutoSteer] Off: steer module stopped steering (switch, button or kickout), pressure sensor 41% (trip point 36%)",
        }));
    }

    [Test]
    public void Module_stop_with_switch_type_None_says_AutoSteer_stays_on()
    {
        _vm.State.Connections.IsAutoSteerDataOk = true;
        Engage();

        ModuleSteering(true);
        ModuleSteering(false);

        Assert.That(_vm.IsAutoSteerEngaged, Is.True, "with None the module stopping only shows, as in AgOpenGPS");
        Assert.That(OffLines(), Is.Empty);
        Assert.That(_log.Lines, Does.Contain(
            "[AutoSteer] steer module stopped steering (switch, button or kickout); AutoSteer stays on (steer switch / button: None)"));
    }

    [Test]
    public void The_module_answering_an_engage_the_app_took_back_does_not_turn_AutoSteer_on_again()
    {
        // Button, a module whose steer bit follows the engage. A guard takes AutoSteer off as it goes
        // on; the module's "steering" then arrives, and its "stopped" after it. Before, that turned
        // AutoSteer on again, the guard off again, and so on: 13 s of on and off (a tester's bench).
        var clock = new AgOpenWeb.Models.Timing.TestClock();
        AgOpenWeb.Models.Timing.Clock.Set(clock);
        try
        {
            ConfigurationStore.Instance.AutoSteer.ExternalEnable = 2;
            _vm.State.Connections.IsAutoSteerDataOk = true;
            ModuleSteering(false);
            Engage();
            _vm.ApplyGpsCycleResult(new GpsCycleResult
            {
                AutoSteerDisengagedThisCycle = true,
                DisengageReason = "AutoSteer off: the implement is heading across the hard boundary. Steer by hand.",
            });
            clock.AdvanceMs(30);
            ModuleSteering(true);
            Assert.That(_vm.IsAutoSteerEngaged, Is.False, "the module's answer is not the operator");
            clock.AdvanceMs(30);
            ModuleSteering(false);
            Assert.That(_vm.IsAutoSteerEngaged, Is.False);
            Assert.That(OffLines(), Has.Length.EqualTo(1));
            Assert.That(_log.Lines.Count(l => l.StartsWith("[AutoSteer] On: ")), Is.EqualTo(1));

            // The operator presses the module's button: AutoSteer goes on.
            clock.AdvanceSeconds(MainViewModel.ModuleAnswerSeconds);
            ModuleSteering(true);
            Assert.That(_vm.IsAutoSteerEngaged, Is.True);
            Assert.That(_log.Lines.Last(l => l.StartsWith("[AutoSteer] On: ")), Is.EqualTo("[AutoSteer] On: steer switch or button on the steer module"));
        }
        finally
        {
            AgOpenWeb.Models.Timing.Clock.Reset();
        }
    }

    [Test]
    public void The_module_button_pressed_again_right_after_it_stopped_steering_turns_AutoSteer_on()
    {
        // Only the app's own stop holds the module's "steering" back: after the module's (its button,
        // a kickout) the next press is the operator's.
        var clock = new AgOpenWeb.Models.Timing.TestClock();
        AgOpenWeb.Models.Timing.Clock.Set(clock);
        try
        {
            ConfigurationStore.Instance.AutoSteer.ExternalEnable = 2;
            _vm.State.Connections.IsAutoSteerDataOk = true;
            ModuleSteering(false);
            ModuleSteering(true);
            Assume.That(_vm.IsAutoSteerEngaged, Is.True);
            clock.AdvanceMs(100);
            ModuleSteering(false);
            Assume.That(_vm.IsAutoSteerEngaged, Is.False);
            clock.AdvanceMs(100);
            ModuleSteering(true);
            Assert.That(_vm.IsAutoSteerEngaged, Is.True);
        }
        finally
        {
            AgOpenWeb.Models.Timing.Clock.Reset();
        }
    }

    private sealed class ListLogger : ILogger<MainViewModel>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }
}
