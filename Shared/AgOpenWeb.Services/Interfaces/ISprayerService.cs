// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;

namespace AgOpenWeb.Services.Interfaces;

/// <summary>Sprayer alarms, one bit each.</summary>
[Flags]
public enum SprayerAlarms
{
    None = 0,
    /// <summary>A section sprays and the pressure is below the minimum.</summary>
    PressureLow = 1,
    /// <summary>The pressure is above the maximum.</summary>
    PressureHigh = 2,
    /// <summary>Less flow than the open nozzles should spray at this pressure.</summary>
    FlowLow = 4,
    /// <summary>More flow than the open nozzles should spray at this pressure.</summary>
    FlowHigh = 8,
    /// <summary>Flow while the module has every section closed (valve stuck open, or a
    /// section switched to manual).</summary>
    FlowSectionsOff = 16,
    /// <summary>The module stopped sending.</summary>
    NoData = 32,
}

/// <summary>
/// What the sprayer's machine module last reported in PGN 160, and what the app makes of it.
/// </summary>
/// <param name="PressureBar">Boom pressure, bar.</param>
/// <param name="FlowLpm">Measured flow, L/min.</param>
/// <param name="TotalLitres">Litres counted since the module started.</param>
/// <param name="MasterOn">The master switch is on.</param>
/// <param name="Sections">The sections the module has open, section 1 in bit 0.</param>
/// <param name="OpenNozzles">Nozzles on the open sections.</param>
/// <param name="OpenWidthM">Width of the open sections, m.</param>
/// <param name="ExpectedFlowLpm">The flow the open nozzles should spray at this pressure,
/// L/min; NaN with the flow check off.</param>
/// <param name="Alarms">The alarms on now.</param>
/// <param name="ModuleChecks">The module checks the sprayer itself (PGN 161 with its alarms):
/// the rate, target, pressure for the target, limits and alarms below are the module's, and
/// the Machine tab's sprayer settings are not used.</param>
/// <param name="RateLha">The module's actual rate, L/ha; NaN = none (stopped, or not sent).</param>
/// <param name="TargetLha">The module's target rate, L/ha; NaN = none.</param>
/// <param name="PressureNeededBar">The pressure that gives the target at this speed, from the
/// module; NaN = none.</param>
/// <param name="MinPressureBar">The module's minimum pressure, bar; NaN = off.</param>
/// <param name="MaxPressureBar">The module's maximum pressure, bar; NaN = off.</param>
public readonly record struct SprayerStatus(
    double PressureBar,
    double FlowLpm,
    double TotalLitres,
    bool MasterOn,
    int Sections,
    int OpenNozzles,
    double OpenWidthM,
    double ExpectedFlowLpm,
    SprayerAlarms Alarms,
    bool ModuleChecks = false,
    double RateLha = double.NaN,
    double TargetLha = double.NaN,
    double PressureNeededBar = double.NaN,
    double MinPressureBar = double.NaN,
    double MaxPressureBar = double.NaN);

/// <summary>
/// The values of one PGN 161 (ISOBUS process data from the sprayer's machine module); NaN or
/// null = not in the message.
/// </summary>
/// <param name="PressureBar">DDI 194, actual pressure.</param>
/// <param name="FlowLpm">DDI 37, actual flow.</param>
/// <param name="TotalLitres">DDI 351, total volume.</param>
/// <param name="MasterOn">DDI 141, actual work state (the master switch).</param>
/// <param name="Sections">DDI 161, actual condensed work state 1–16, as section bits (section 1 in bit 0).</param>
/// <param name="RateLha">DDI 2, actual volume per area.</param>
/// <param name="TargetLha">DDI 1, setpoint volume per area.</param>
/// <param name="PressureNeededBar">DDI 193, setpoint pressure: what gives the target now.</param>
/// <param name="MinPressureBar">DDI 195, minimum pressure.</param>
/// <param name="MaxPressureBar">DDI 196, maximum pressure.</param>
/// <param name="Alarms">DDI 57344 (proprietary), the module's sprayer alarms; null = the module
/// does not check (then the app checks with the Machine tab's settings).</param>
public readonly record struct SprayerProcessData(
    double PressureBar,
    double FlowLpm,
    double TotalLitres,
    bool? MasterOn,
    int? Sections,
    double RateLha,
    double TargetLha,
    double PressureNeededBar,
    double MinPressureBar,
    double MaxPressureBar,
    SprayerAlarms? Alarms);

/// <summary>
/// Reads the sprayer data the machine module sends once a second (PGN 160: pressure, flow,
/// litres, master switch, open sections), checks it against the nozzles of the open
/// sections, and sounds the alarm when something is wrong. A module that checks the sprayer
/// itself also sends PGN 161 (ISOBUS process data: rate, target, pressure for the target,
/// limits, alarms); then its values and alarms are used instead.
/// </summary>
public interface ISprayerService
{
    /// <summary>The latest data; null until the module has been heard.</summary>
    SprayerStatus? Status { get; }
}
