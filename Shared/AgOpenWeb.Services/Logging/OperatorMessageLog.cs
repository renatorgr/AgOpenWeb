// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.Services.Logging;

/// <summary>
/// What the operator was told, written to the log so a bug report's <c>logs.txt</c> has it:
/// each notification on the screen (a toast), each alarm sound, each message from a module
/// (PGN 221) and each dialog with the answer given, as a line starting <c>[Screen]</c>.
/// Without it, whoever reads a report can't tell whether the operator saw "RTK lost" or
/// heard the alarm. The same line again within <see cref="RepeatWindowMs"/> is left out, so a
/// message repeated every second does not push the rest of the log out of its 2000 lines.
/// Fork only.
/// </summary>
public sealed class OperatorMessageLog
{
    /// <summary>The same line again within this time is not written again.</summary>
    public const long RepeatWindowMs = 30_000;

    private readonly ILogger? _logger;
    private readonly Dictionary<string, long> _lastWritten = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public OperatorMessageLog(ILogger? logger) => _logger = logger;

    /// <summary>The clock for the repeat window; tests set it.</summary>
    internal Func<long> NowMs { get; set; } = () => Environment.TickCount64;

    /// <summary>A notification shown on the screen.</summary>
    public void Toast(string message) => Write("Toast: " + OneLine(message));

    /// <summary>A sound played on the operator's screen. The section and hydraulic lift clicks
    /// and AutoSteer's on and off sounds are left out: they come many times a minute and the log
    /// already says what made them.</summary>
    public void Sound(SoundEffect effect)
    {
        if (!IsAlarm(effect)) return;
        Write("Sound: " + effect);
    }

    /// <summary>A module's own message shown on the screen (PGN 221).</summary>
    public void ModuleMessage(string text, bool warning) =>
        Write((warning ? "Module warning: " : "Module message: ") + OneLine(text));

    /// <summary>A dialog put in front of the operator.</summary>
    public void Dialog(string title, string message) =>
        Write("Dialog: " + OneLine(title) + " - " + OneLine(message));

    /// <summary>The operator's answer to a dialog (the button's caption).</summary>
    public void DialogAnswer(string answer) => Write("Dialog answered: " + OneLine(answer));

    /// <summary>The sounds worth a line: alarms and warnings, not routine clicks.</summary>
    public static bool IsAlarm(SoundEffect effect) => effect switch
    {
        SoundEffect.SectionOn or SoundEffect.SectionOff => false,
        SoundEffect.HydraulicLiftUp or SoundEffect.HydraulicLiftDown => false,
        SoundEffect.AutoSteerOn or SoundEffect.AutoSteerOff => false,
        _ => true,
    };

    /// <summary>
    /// A text as one line of the log: a line break in it would start a line of its own in a bug
    /// report's logs.txt, which then reads as something the app wrote.
    /// </summary>
    public static string OneLine(string text) =>
        text.IndexOfAny(new[] { '\r', '\n' }) < 0 ? text : text.Replace("\r", "\\r").Replace("\n", "\\n");

    private void Write(string line)
    {
        if (_logger is null) return;
        long now = NowMs();
        lock (_gate)
        {
            if (_lastWritten.TryGetValue(line, out long last) && now - last < RepeatWindowMs) return;
            _lastWritten[line] = now;
            if (_lastWritten.Count > 200) Forget(now);
        }
        _logger.LogInformation("[Screen] {Line}", line);
    }

    /// <summary>Lets go of lines older than the window, so the table stays small.</summary>
    private void Forget(long now)
    {
        var old = new List<string>();
        foreach (var (line, at) in _lastWritten)
            if (now - at >= RepeatWindowMs) old.Add(line);
        foreach (var line in old) _lastWritten.Remove(line);
    }
}
