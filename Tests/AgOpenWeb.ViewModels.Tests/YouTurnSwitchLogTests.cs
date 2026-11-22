// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.State;
using AgOpenWeb.Models.Track;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The automatic U-turn switch is in the log and in a bug report's runtime state, and switched
/// off with AutoSteer on the operator is told. A bug report from Couto da Ribeira (20 Nov 2026)
/// had a turn planned before an obstacle that was never driven and AutoSteer off four times "no
/// U-turn before it" / "end of the pass and no U-turn here", with nothing to show that U-turns
/// had been switched off (the U-turn button sits beside AutoSteer and acts on touch). Fork only.
/// </summary>
[TestFixture]
public class YouTurnSwitchLogTests
{
    private static Track AbLine() => new() { Name = "AB", Points = new List<Vec3> { new(0, 0, 0), new(0, 100, 0) } };

    private static (MainViewModel Vm, ListLogger Log, List<string> Toasts) Build()
    {
        var log = new ListLogger();
        var vm = new MainViewModelBuilder { Logger = log }.Build();
        vm.SelectedTrack = AbLine();
        var toasts = new List<string>();
        vm.FailureReported += toasts.Add;
        return (vm, log, toasts);
    }

    [Test]
    public void The_U_turn_button_off_with_AutoSteer_on_is_logged_and_told()
    {
        var (vm, log, toasts) = Build();
        vm.IsYouTurnEnabled = true;
        vm.IsAutoSteerEngaged = true;

        vm.ToggleYouTurnCommand!.Execute(null);

        Assert.Multiple(() =>
        {
            Assert.That(vm.IsYouTurnEnabled, Is.False);
            Assert.That(log.Lines, Has.Some.EqualTo("[YouTurn] Automatic U-turns off: U-turn button"));
            Assert.That(toasts, Has.Some.StartsWith("Automatic U-turns off"), "a touch on it by mistake is seen at once");
        });
    }

    [Test]
    public void The_U_turn_button_on_or_off_with_AutoSteer_off_is_logged_only()
    {
        var (vm, log, toasts) = Build();

        vm.ToggleYouTurnCommand!.Execute(null);
        vm.ToggleYouTurnCommand!.Execute(null);

        Assert.Multiple(() =>
        {
            Assert.That(log.Lines, Is.SupersetOf(new[]
            {
                "[YouTurn] Automatic U-turns on: U-turn button",
                "[YouTurn] Automatic U-turns off: U-turn button",
            }));
            Assert.That(toasts, Has.None.StartsWith("Automatic U-turns"));
        });
    }

    [Test]
    public void A_closed_track_chosen_turns_U_turns_off_with_its_reason_in_the_log()
    {
        var (vm, log, _) = Build();
        vm.IsYouTurnEnabled = true;

        vm.SelectedTrack = new Track
        {
            Name = "Polygon", Type = TrackType.Curve, IsClosed = true,
            Points = new List<Vec3> { new(0, 0, 0), new(100, 0, 0), new(100, 100, 0), new(0, 100, 0), new(0, 0, 0) },
        };

        Assert.That(vm.IsYouTurnEnabled, Is.False);
        Assert.That(log.Lines, Has.Some.EqualTo("[YouTurn] Automatic U-turns off: closed track chosen"));
    }

    private static Track Closed() => new()
    {
        Name = "Boundary Curve", Type = TrackType.Curve, IsClosed = true,
        Points = new List<Vec3> { new(0, 0, 0), new(100, 0, 0), new(100, 100, 0), new(0, 100, 0), new(0, 0, 0) },
    };

    // Eucaliptal (22 Nov 2026): U-turns on, the Boundary Curve driven, the AB line chosen again,
    // and AutoSteer off twice at the end of the pass with U-turns still off.
    [Test]
    public void An_open_track_after_the_closed_one_gets_U_turns_back_on()
    {
        var (vm, log, toasts) = Build();
        var ab = vm.SelectedTrack;
        vm.IsYouTurnEnabled = true;

        vm.SelectedTrack = Closed();
        vm.SelectedTrack = ab;

        Assert.Multiple(() =>
        {
            Assert.That(vm.IsYouTurnEnabled, Is.True);
            Assert.That(log.Lines, Has.Some.EqualTo(
                "[YouTurn] Automatic U-turns on: open track chosen after a closed one; they were on before it"));
            Assert.That(toasts, Has.Some.StartsWith("Automatic U-turns on again"));
        });
    }

    [Test]
    public void Off_before_the_closed_track_they_stay_off()
    {
        var (vm, log, toasts) = Build();
        var ab = vm.SelectedTrack;

        vm.SelectedTrack = Closed();
        vm.SelectedTrack = ab;

        Assert.Multiple(() =>
        {
            Assert.That(vm.IsYouTurnEnabled, Is.False);
            Assert.That(log.Lines, Has.None.StartsWith("[YouTurn] Automatic U-turns on"));
            Assert.That(toasts, Has.None.StartsWith("Automatic U-turns on"));
        });
    }

    [Test]
    public void Two_closed_tracks_in_a_row_still_give_them_back()
    {
        var (vm, _, _) = Build();
        var ab = vm.SelectedTrack;
        vm.IsYouTurnEnabled = true;

        vm.SelectedTrack = Closed();
        vm.SelectedTrack = Closed();
        vm.SelectedTrack = ab;

        Assert.That(vm.IsYouTurnEnabled, Is.True);
    }

    [Test]
    public void The_U_turn_button_between_forgets_them()
    {
        var (vm, _, _) = Build();
        var ab = vm.SelectedTrack;
        vm.IsYouTurnEnabled = true;
        vm.SelectedTrack = Closed();

        vm.ToggleYouTurnCommand!.Execute(null); // refused on a closed track: changes nothing
        vm.SelectedTrack = ab;
        Assert.That(vm.IsYouTurnEnabled, Is.True, "a press refused on the closed track is no choice");

        vm.SelectedTrack = Closed();
        vm.SelectedTrack = ab;
        vm.ToggleYouTurnCommand!.Execute(null);
        vm.SelectedTrack = Closed();
        vm.SelectedTrack = ab;
        Assert.That(vm.IsYouTurnEnabled, Is.False, "switched off by hand, then a closed track: they stay off");
    }

    [Test]
    public void Guidance_stopped_between_forgets_them()
    {
        var (vm, _, _) = Build();
        var ab = vm.SelectedTrack;
        vm.IsYouTurnEnabled = true;

        vm.SelectedTrack = Closed();
        vm.SelectedTrack = null;
        vm.SelectedTrack = ab;

        Assert.That(vm.IsYouTurnEnabled, Is.False);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void The_bug_report_runtime_state_has_the_U_turn_switch(bool on)
    {
        var (vm, _, _) = Build();
        vm.IsYouTurnEnabled = on;

        string json = RuntimeStateCapture.From(vm.State).Json;

        Assert.That(json, Does.Contain($"\"IsYouTurnEnabled\": {(on ? "true" : "false")}"));
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
