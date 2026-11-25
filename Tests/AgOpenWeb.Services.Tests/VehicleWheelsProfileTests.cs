// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Profile;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgOpenWeb.Services.Tests;

/// <summary>Dual wheels on the map (Vehicle > Wheels; fork only) are the vehicle profile's: a tractor with
/// dual rear wheels keeps them when its profile is opened again. A profile saved before the setting, or
/// with a value that is not a choice, has single wheels, as a new profile.</summary>
[TestFixture]
public class VehicleWheelsProfileTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aow-vehwheels-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() { try { Directory.Delete(_dir, true); } catch { } }

    [TestCase(0, Description = "single")]
    [TestCase(1, Description = "dual on the drive axle")]
    [TestCase(2, Description = "dual on both axles")]
    public void The_wheels_are_saved_with_the_vehicle_profile(int dualWheels)
    {
        var src = new ConfigurationStore();
        src.Vehicle.DualWheels = dualWheels;
        VehicleProfileJsonService.Save(_dir, "Tractor", src);

        var dst = new ConfigurationStore();
        dst.Vehicle.DualWheels = dualWheels == 1 ? 2 : 1; // the vehicle before
        Assert.That(VehicleProfileJsonService.Load(_dir, "Tractor", dst), Is.True);
        Assert.That(dst.Vehicle.DualWheels, Is.EqualTo(dualWheels));
    }

    [TestCase(null, Description = "saved before the setting")]
    [TestCase(3, Description = "not a choice")]
    [TestCase(-1)]
    public void A_profile_without_a_choice_has_single_wheels(int? stored)
    {
        VehicleProfileJsonService.Save(_dir, "Old", new ConfigurationStore());
        var path = Path.Combine(_dir, "Old.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var vehicle = node["vehicle"]!.AsObject();
        Assert.That(vehicle.ContainsKey("dualWheels"), Is.True, "the setting is written");
        vehicle.Remove("dualWheels");
        if (stored is int v) vehicle["dualWheels"] = v;
        File.WriteAllText(path, node.ToJsonString());

        var dst = new ConfigurationStore();
        dst.Vehicle.DualWheels = 2; // the vehicle that was active before
        Assert.That(VehicleProfileJsonService.Load(_dir, "Old", dst), Is.True);
        Assert.That(dst.Vehicle.DualWheels, Is.EqualTo(0));
    }

    [Test]
    public void A_new_vehicle_profile_has_single_wheels()
    {
        var store = new ConfigurationStore();
        store.Vehicle.DualWheels = 2;
        new TestVehicleProfileService(_dir).CreateDefaultProfile("New", store);
        Assert.That(store.Vehicle.DualWheels, Is.EqualTo(0));

        var saved = new ConfigurationStore();
        saved.Vehicle.DualWheels = 2;
        Assert.That(VehicleProfileJsonService.Load(_dir, "New", saved), Is.True, "the new profile is saved");
        Assert.That(saved.Vehicle.DualWheels, Is.EqualTo(0));
    }

    [Test]
    public void A_profile_in_the_older_format_has_single_wheels_and_the_picture_as_drawn()
    {
        ProfileJsonServiceV1.Save(_dir, "Older", new ConfigurationStore());
        var dst = new ConfigurationStore();
        dst.Vehicle.DualWheels = 2; // the vehicle that was active before
        dst.Vehicle.Color = 0xA6192E;
        Assert.That(ProfileJsonServiceV1.Load(_dir, "Older", dst), Is.True);
        Assert.That(dst.Vehicle.DualWheels, Is.EqualTo(0));
        Assert.That(dst.Vehicle.Color, Is.EqualTo(VehicleConfig.DefaultColor));
    }

    [Test]
    public void An_AgOpenGPS_vehicle_has_single_wheels_and_the_picture_as_drawn()
    {
        var dst = new ConfigurationStore();
        dst.Vehicle.DualWheels = 2; // the vehicle that was active before
        dst.Vehicle.Color = 0xA6192E;
        AgOpenGpsSettings.ApplyToStore(new System.Collections.Generic.Dictionary<string, string>(), "Imported", "Imported.xml", dst);
        Assert.That(dst.Vehicle.DualWheels, Is.EqualTo(0));
        Assert.That(dst.Vehicle.Color, Is.EqualTo(VehicleConfig.DefaultColor));
    }

    private sealed class TestVehicleProfileService : VehicleProfileService
    {
        public TestVehicleProfileService(string dir)
            : base(NullLogger<VehicleProfileService>.Instance, dir) { }
    }
}
