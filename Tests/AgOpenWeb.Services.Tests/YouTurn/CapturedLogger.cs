// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>A logger that writes every line, as "Category: text", into a list shared by several loggers.</summary>
internal sealed class CapturedLogger<T>(List<string> lines) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => lines.Add(typeof(T).Name + ": " + formatter(state, exception));
}
