// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.AutoSteer;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// A steering setting changed goes into the log with the value before and after (fork): on a tractor
/// Ackermann went 90 → 0 and Max Steer Angle 24 → 61, and the bug report could not say when or how.
/// </summary>
[TestFixture]
public class SteerSettingsLogTests
{
    private static (ConfigurationStore Store, List<string> Lines) Watched()
    {
        var store = new ConfigurationStore();
        store.AutoSteer.Ackermann = 90;
        store.Vehicle.MaxSteerAngle = 24;
        var lines = new List<string>();
        _ = new SteerSettingsLog(store, lines.Add);
        return (store, lines);
    }

    [Test]
    public void Ackermann_and_max_steer_angle_changes_are_written_before_and_after()
    {
        var (store, lines) = Watched();
        store.AutoSteer.Ackermann = 0;
        store.Vehicle.MaxSteerAngle = 61;
        Assert.That(lines, Is.EqualTo(new[] { "Ackermann 90 -> 0", "MaxSteerAngle 24 -> 61" }));
    }

    [Test]
    public void The_module_settings_are_written()
    {
        var (store, lines) = Watched();
        store.AutoSteer.WasOffset = 256;
        store.AutoSteer.CountsPerDegree = 138.5;
        store.AutoSteer.ProportionalGain = 10;
        store.AutoSteer.PressureSensorEnabled = !store.AutoSteer.PressureSensorEnabled;
        Assert.That(lines, Has.Count.EqualTo(4));
        Assert.That(lines[1], Does.StartWith("CountsPerDegree ").And.EndWith(" -> 138.5"));
    }

    [Test]
    public void A_value_set_to_what_it_was_and_other_settings_write_nothing()
    {
        var (store, lines) = Watched();
        store.AutoSteer.Ackermann = 90;
        store.Vehicle.MaxSteerAngle = 24;
        store.AutoSteer.LineWidth = store.AutoSteer.LineWidth + 1; // display, not steering
        store.Vehicle.Wheelbase = store.Vehicle.Wheelbase + 0.1;  // watched elsewhere
        Assert.That(lines, Is.Empty);
    }
}
