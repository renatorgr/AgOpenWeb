// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.State;
using AgOpenWeb.Models.Track;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The automatic U-turn switch is in the log, with its cause, and in a bug report's runtime
/// state. A tester's bug report had a turn planned before an obstacle that was never driven and
/// no turn planned at three engages after it, with nothing in the log or the dump to show that
/// U-turns had been switched off (the U-turn button sits beside AutoSteer and acts on touch).
/// </summary>
[TestFixture]
public class YouTurnSwitchLogTests
{
    private static Track AbLine() => new() { Name = "AB", Points = new List<Vec3> { new(0, 0, 0), new(0, 100, 0) } };

    private static (MainViewModel Vm, ListLogger Log) Build()
    {
        var log = new ListLogger();
        var vm = new MainViewModelBuilder { Logger = log }.Build();
        vm.SelectedTrack = AbLine();
        return (vm, log);
    }

    [Test]
    public void The_U_turn_button_on_and_off_is_logged_with_its_cause()
    {
        var (vm, log) = Build();

        vm.ToggleYouTurnCommand!.Execute(null);
        vm.ToggleYouTurnCommand!.Execute(null);

        Assert.That(log.Lines.Where(l => l.StartsWith("[YouTurn] Automatic U-turns", StringComparison.Ordinal)), Is.EqualTo(new[]
        {
            "[YouTurn] Automatic U-turns on: U-turn button",
            "[YouTurn] Automatic U-turns off: U-turn button",
        }));
    }

    [Test]
    public void A_closed_track_chosen_turns_U_turns_off_with_its_reason_in_the_log()
    {
        var (vm, log) = Build();
        vm.IsYouTurnEnabled = true;

        vm.SelectedTrack = new Track
        {
            Name = "Polygon", Type = TrackType.Curve, IsClosed = true,
            Points = new List<Vec3> { new(0, 0, 0), new(100, 0, 0), new(100, 100, 0), new(0, 100, 0), new(0, 0, 0) },
        };

        Assert.That(vm.IsYouTurnEnabled, Is.False);
        Assert.That(log.Lines, Has.Some.EqualTo("[YouTurn] Automatic U-turns off: closed track chosen"));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void The_bug_report_runtime_state_has_the_U_turn_switch(bool on)
    {
        var (vm, _) = Build();
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
