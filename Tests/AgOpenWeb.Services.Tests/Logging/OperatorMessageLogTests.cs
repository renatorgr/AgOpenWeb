// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Logging;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.Logging;

/// <summary>What the operator was told reaches the log, for a bug report (fork only): a tester's
/// report of an RTK drop could not say whether "RTK lost" was shown or the alarm heard.</summary>
[TestFixture]
public class OperatorMessageLogTests
{
    private sealed class LinesLogger : ILogger
    {
        public readonly List<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    private static (OperatorMessageLog log, LinesLogger lines, Action<long> advance) Make()
    {
        var lines = new LinesLogger();
        long now = 1_000_000;
        var log = new OperatorMessageLog(lines) { NowMs = () => now };
        return (log, lines, ms => now += ms);
    }

    [Test]
    public void A_toast_an_alarm_and_a_module_message_are_written()
    {
        var (log, lines, _) = Make();

        log.Toast("RTK lost: the 0.3 m jump of the position is taken out and the line held for up to 3 min");
        log.Sound(SoundEffect.RtkLost);
        log.ModuleMessage("Keya current kickout (9.1 A, limit 8.0 A)", warning: true);

        Assert.That(lines.Lines, Is.EqualTo(new[]
        {
            "[Screen] Toast: RTK lost: the 0.3 m jump of the position is taken out and the line held for up to 3 min",
            "[Screen] Sound: RtkLost",
            "[Screen] Module warning: Keya current kickout (9.1 A, limit 8.0 A)",
        }));
    }

    [Test]
    public void Routine_clicks_are_left_out()
    {
        var (log, lines, _) = Make();

        foreach (var routine in new[] { SoundEffect.SectionOn, SoundEffect.SectionOff, SoundEffect.HydraulicLiftUp,
                     SoundEffect.HydraulicLiftDown, SoundEffect.AutoSteerOn, SoundEffect.AutoSteerOff })
            log.Sound(routine);

        Assert.That(lines.Lines, Is.Empty);
    }

    [Test]
    public void The_same_line_again_within_the_window_is_written_once()
    {
        var (log, lines, advance) = Make();

        for (int i = 0; i < 20; i++) { log.ModuleMessage("XIAO silent", warning: false); advance(1000); }
        Assert.That(lines.Lines, Has.Count.EqualTo(1), "a message every second for 20 s");

        advance(OperatorMessageLog.RepeatWindowMs);
        log.ModuleMessage("XIAO silent", warning: false);
        Assert.That(lines.Lines, Has.Count.EqualTo(2), "after the window it is written again");

        log.Toast("Another message");
        Assert.That(lines.Lines, Has.Count.EqualTo(3), "another line is not held back");
    }

    [Test]
    public void A_line_break_in_a_message_stays_on_one_line_of_the_log()
    {
        var (log, lines, _) = Make();

        log.Toast("first\n[12:00:00.000] [Warning] Fake: second");

        Assert.That(lines.Lines, Has.Count.EqualTo(1));
        Assert.That(lines.Lines[0], Does.Not.Contain("\n"));
        Assert.That(lines.Lines[0], Does.Contain("first\\n[12:00:00.000]"));
    }

    [Test]
    public void Without_a_logger_nothing_happens()
    {
        var log = new OperatorMessageLog(null);
        Assert.DoesNotThrow(() => { log.Toast("x"); log.Sound(SoundEffect.RtkLost); log.Dialog("t", "m"); });
    }
}
