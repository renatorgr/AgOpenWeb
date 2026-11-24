// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

using Microsoft.Extensions.Logging;

namespace AgOpenWeb.Services.YouTurn;

/// <summary>
/// The U-turn planner's log lines, which can be held back for a while. An end where no turn was built
/// is tried again as the tractor goes on, and each try wrote what it tried again, line for line: 140
/// lines a try at a Go around obstacle no turn clears (Couto da Ribeira, the mini PC, 24 Nov 2026), so a
/// bug report's 2000 lines reached back 11 s. While a try is held (<see cref="HoldInto"/>) the lines go
/// into a list instead; <see cref="YouTurnStateMachine"/> writes them when the try made a turn and
/// leaves them out, with one line saying so, when it made none again. Errors are never held. Fork only.
/// </summary>
internal static class YouTurnLog
{
    /// <summary>A line held back, with the logger (its category) it was written to.</summary>
    internal readonly record struct Line(ILogger Logger, LogLevel Level, EventId Id, string Text, Exception? Error);

    // Set and cleared within one Tick on the thread that runs it: other threads write as usual.
    [ThreadStatic] private static List<Line>? _held;

    /// <summary>The lines written on this thread from now on go into <paramref name="into"/>; null writes them.</summary>
    internal static void HoldInto(List<Line>? into) => _held = into;

    /// <summary>A logger that writes through <paramref name="logger"/>, or into the list held on this thread.</summary>
    internal static ILogger Holdable(ILogger logger) => new HoldableLogger(logger);

    /// <summary>Writes the lines held, in order, each to its own logger.</summary>
    internal static void Write(IEnumerable<Line> lines)
    {
        foreach (var line in lines)
            line.Logger.Log(line.Level, line.Id, line.Text, line.Error, static (text, _) => text);
    }

    private sealed class HoldableLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (_held is { } held && logLevel < LogLevel.Error)
            {
                if (inner.IsEnabled(logLevel))
                    held.Add(new Line(inner, logLevel, eventId, formatter(state, exception), exception));
                return;
            }
            inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
