// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using AgOpenWeb.Models.Configuration;

namespace AgOpenWeb.Services.AutoSteer;

/// <summary>
/// Writes each change of a setting the steering runs by, with the value before and after, for the
/// log (fork, Renato 2026-11-13): on a tractor Ackermann went 90 → 0 (every left angle the module
/// read became 0°) and Max Steer Angle 24 → 61, and nothing in the log said when, so a bug report
/// could not tell how. Whatever changed it (the web screen, the steer wizard, a profile loaded,
/// Smart WAS) shows here; the web screen's own line says when it was the screen.
/// Covers what goes to the steer module (PGN 251 and 252) and the angle guidance is held to.
/// </summary>
public sealed class SteerSettingsLog
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly (string Name, Func<ConfigurationStore, string> Read)[] AutoSteerSettings =
    {
        (nameof(AutoSteerConfig.WasOffset), s => s.AutoSteer.WasOffset.ToString(Inv)),
        (nameof(AutoSteerConfig.CountsPerDegree), s => s.AutoSteer.CountsPerDegree.ToString("0.##", Inv)),
        (nameof(AutoSteerConfig.Ackermann), s => s.AutoSteer.Ackermann.ToString(Inv)),
        (nameof(AutoSteerConfig.ProportionalGain), s => s.AutoSteer.ProportionalGain.ToString(Inv)),
        (nameof(AutoSteerConfig.MaxPwm), s => s.AutoSteer.MaxPwm.ToString(Inv)),
        (nameof(AutoSteerConfig.MinPwm), s => s.AutoSteer.MinPwm.ToString(Inv)),
        (nameof(AutoSteerConfig.InvertWas), s => OnOff(s.AutoSteer.InvertWas)),
        (nameof(AutoSteerConfig.InvertMotor), s => OnOff(s.AutoSteer.InvertMotor)),
        (nameof(AutoSteerConfig.MotorDriver), s => s.AutoSteer.MotorDriver.ToString(Inv)),
        (nameof(AutoSteerConfig.AdConverter), s => s.AutoSteer.AdConverter.ToString(Inv)),
        (nameof(AutoSteerConfig.ExternalEnable), s => s.AutoSteer.ExternalEnable.ToString(Inv)),
        (nameof(AutoSteerConfig.DanfossEnabled), s => OnOff(s.AutoSteer.DanfossEnabled)),
        (nameof(AutoSteerConfig.TurnSensorEnabled), s => OnOff(s.AutoSteer.TurnSensorEnabled)),
        (nameof(AutoSteerConfig.TurnSensorCounts), s => s.AutoSteer.TurnSensorCounts.ToString(Inv)),
        (nameof(AutoSteerConfig.PressureSensorEnabled), s => OnOff(s.AutoSteer.PressureSensorEnabled)),
        (nameof(AutoSteerConfig.PressureTripPoint), s => s.AutoSteer.PressureTripPoint.ToString(Inv)),
        (nameof(AutoSteerConfig.CurrentSensorEnabled), s => OnOff(s.AutoSteer.CurrentSensorEnabled)),
        (nameof(AutoSteerConfig.CurrentTripPoint), s => s.AutoSteer.CurrentTripPoint.ToString(Inv)),
    };

    private static readonly (string Name, Func<ConfigurationStore, string> Read)[] VehicleSettings =
    {
        (nameof(VehicleConfig.MaxSteerAngle), s => s.Vehicle.MaxSteerAngle.ToString("0.#", Inv)),
    };

    private readonly ConfigurationStore _store;
    private readonly Action<string> _write;
    private readonly Dictionary<string, string> _seen = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>Starts watching: <paramref name="write"/> gets one line per change, e.g. "Ackermann 90 -> 0".</summary>
    public SteerSettingsLog(ConfigurationStore store, Action<string> write)
    {
        _store = store;
        _write = write;
        foreach (var (name, read) in AutoSteerSettings) _seen[name] = read(store);
        foreach (var (name, read) in VehicleSettings) _seen[name] = read(store);
        store.AutoSteer.PropertyChanged += (_, e) => Changed(AutoSteerSettings, e);
        store.Vehicle.PropertyChanged += (_, e) => Changed(VehicleSettings, e);
    }

    private void Changed((string Name, Func<ConfigurationStore, string> Read)[] settings, PropertyChangedEventArgs e)
    {
        foreach (var (name, read) in settings)
        {
            if (name != e.PropertyName) continue;
            var now = read(_store);
            string? line = null;
            lock (_lock)
            {
                // Some setters announce a change without one: only a new value is written.
                if (_seen.TryGetValue(name, out var was) && was == now) return;
                _seen[name] = now;
                line = name + " " + (was ?? "?") + " -> " + now;
            }
            _write(line);
            return;
        }
    }

    private static string OnOff(bool on) => on ? "on" : "off";
}
