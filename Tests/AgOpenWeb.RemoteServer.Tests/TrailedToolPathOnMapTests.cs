// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Models.Track;
using AgOpenWeb.RemoteServer;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;
using NUnit.Framework;

namespace AgOpenWeb.RemoteServer.Tests;

/// <summary>
/// A U-turn and the way onto a pass are planned and driven for the pivot. With a trailed or TBT
/// implement the map draws them where the implement's working centre goes: behind the hitch,
/// inside every bend of the pivot's path and onto the next pass gradually. A tester's semi-mounted
/// drill (its wheels 4.15 m behind the pin, the discs 1.6 m ahead of the wheels) on a tractor with
/// the pin 1.54 m behind the rear axle; he asked for the line the implement works on, not the
/// tractor's (26 Nov 2026). Fork only.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class TrailedToolPathOnMapTests
{
    private const double Drawbar = 1.54;      // the pin behind the rear axle
    private const double ToWheels = 4.15;     // the pin to the drill's wheels
    private const double DiscsAhead = 1.6;    // the discs ahead of the wheels
    private const double Behind = Drawbar + ToWheels - DiscsAhead; // the discs behind the rear axle, in line

    private static (SceneProjector Projector, ApplicationState State, ConfigurationStore Config) Make(string toolType)
    {
        var state = new ApplicationState();
        var config = new ConfigurationStore();
        config.Vehicle.HitchLength = Drawbar;
        config.Tool.TrailingHitchLength = ToWheels;
        config.Tool.TrailingToolToPivotLength = -DiscsAhead;
        config.Tool.TankTrailingHitchLength = 3.0;
        config.Tool.Offset = 0;
        config.Tool.SetToolType(toolType);
        var settings = Substitute.For<ISettingsService>();
        settings.Settings.Returns(new AppSettings { FieldsDirectory = "" });
        var projector = new SceneProjector(state, Substitute.For<ISectionControlService>(),
            Substitute.For<IToolPositionService>(), config, Substitute.For<ICoverageMapService>(),
            Substitute.For<IJobService>(), Substitute.For<IConfigurationService>(), Substitute.For<IAutoSteerService>(),
            Substitute.For<ISmartWasCalibrationService>(), Substitute.For<IUdpCommunicationService>(),
            Substitute.For<INtripProfileService>(), Substitute.For<IFieldService>(), settings,
            Substitute.For<IVehicleProfileService>(), Substitute.For<IPersistentStateService>());
        return (projector, state, config);
    }

    // The pivot's path: 10 m north along easting 2, a point every half metre.
    private static List<Vec3> North() => Enumerable.Range(0, 21).Select(i => new Vec3(2, i * 0.5, 0)).ToList();

    // The pivot's path: 20 m north along easting 0, then round a circle of the radius to the right
    // (its centre at (radius, 20)) for the turns given, a point every 0.25 m.
    private static List<Vec3> NorthThenRound(double radius, double turns)
    {
        var path = new List<Vec3>();
        for (double n = 0; n < 20; n += 0.25) path.Add(new Vec3(0, n, 0));
        int steps = (int)(turns * 2 * Math.PI * radius / 0.25);
        for (int i = 0; i <= steps; i++)
        {
            double a = i * 0.25 / radius; // the angle turned, clockwise from the west of the centre
            path.Add(new Vec3(radius - radius * Math.Cos(a), 20 + radius * Math.Sin(a), a));
        }
        return path;
    }

    [Test]
    public void With_a_trailed_implement_the_U_turn_is_drawn_where_its_working_centre_goes()
    {
        var (projector, state, _) = Make("trailing");
        state.YouTurn.TurnPath = North();

        var drawn = projector.BuildScene(1).UTurnPath!;
        Assert.That(drawn, Has.Count.EqualTo(21));
        Assert.That(drawn.All(p => Math.Abs(p.E - 2) < 1e-9), "straight behind the tractor on a straight path");
        Assert.That(drawn[0].N, Is.EqualTo(-Behind).Within(1e-9), "it starts at the implement, not at the rear axle");
        Assert.That(drawn[^1].N, Is.EqualTo(10 - Behind).Within(1e-9));
    }

    [Test]
    public void With_a_trailed_implement_the_way_onto_a_pass_is_drawn_where_its_working_centre_goes()
    {
        var (projector, state, _) = Make("trailing");
        state.Operation.LineEntryPath = North();

        var drawn = projector.BuildScene(1).EntryPath!;
        Assert.That(drawn[0].N, Is.EqualTo(-Behind).Within(1e-9));
        Assert.That(drawn[^1].N, Is.EqualTo(10 - Behind).Within(1e-9));
    }

    [Test]
    public void Round_a_bend_the_trailed_implement_is_drawn_inside_the_tractor_s_path()
    {
        // The pin runs on a circle of sqrt(R² + drawbar²), the wheels on sqrt(pin² - toWheels²),
        // the discs ahead of the wheels on sqrt(wheels² + ahead²): 4.87 m for a 6 m turn.
        const double radius = 6;
        double pin2 = radius * radius + Drawbar * Drawbar;
        double wheels2 = pin2 - ToWheels * ToWheels;
        double expected = Math.Sqrt(wheels2 + DiscsAhead * DiscsAhead);
        Assert.That(expected, Is.EqualTo(4.87).Within(0.01));

        var (projector, state, _) = Make("trailing");
        state.YouTurn.TurnPath = NorthThenRound(radius, 1.5);

        var drawn = projector.BuildScene(1).UTurnPath!;
        // Settled, over the last half turn. Within 6 cm: the implement is followed from point to
        // point of the path, 0.25 m apart here, as the U-turn's own checks follow it.
        int half = (int)(Math.PI * radius / 0.25);
        foreach (var p in drawn.Skip(drawn.Count - half))
            Assert.That(Math.Sqrt((p.E - radius) * (p.E - radius) + (p.N - 20) * (p.N - 20)), Is.EqualTo(expected).Within(0.06),
                "the discs run 1.1 m inside the tractor's 6 m turn");
    }

    [Test]
    public void Coming_out_of_a_bend_the_trailed_implement_comes_onto_the_straight_gradually()
    {
        // A quarter turn to the right, then 30 m east: the tractor is on the straight at once,
        // the drill behind it only after a few of its lengths.
        const double radius = 6;
        var path = NorthThenRound(radius, 0.25);
        var last = path[^1];
        for (double e = 0.25; e <= 30; e += 0.25) path.Add(new Vec3(last.Easting + e, last.Northing, Math.PI / 2));

        var (projector, state, _) = Make("trailing");
        state.YouTurn.TurnPath = path;
        var drawn = projector.BuildScene(1).UTurnPath!;

        // The straight is northing 20 + radius. Where the discs are across it, by how far along it they are.
        double line = 20 + radius;
        double OffAt(double metresAlong) => Math.Abs(drawn.OrderBy(p => Math.Abs(p.E - (radius + metresAlong))).First().N - line);
        Assert.That(OffAt(0), Is.GreaterThan(0.5), "still inside the bend where the straight starts");
        Assert.That(OffAt(4), Is.LessThan(OffAt(0)));
        Assert.That(OffAt(8), Is.LessThan(OffAt(4)));
        Assert.That(OffAt(20), Is.LessThan(0.05), "on the straight in the end");
    }

    [Test]
    public void A_trailed_implement_set_to_one_side_is_drawn_on_that_side()
    {
        var (projector, state, config) = Make("trailing");
        config.Tool.Offset = 0.5; // to the right
        state.YouTurn.TurnPath = North();

        var drawn = projector.BuildScene(1).UTurnPath!;
        Assert.That(drawn.All(p => Math.Abs(p.E - 2.5) < 1e-9), "0.5 m east of a path going north");
        Assert.That(drawn[0].N, Is.EqualTo(-Behind).Within(1e-9));
    }

    [Test]
    public void With_a_tank_between_tractor_and_implement_the_implement_is_drawn_behind_the_tank()
    {
        var (projector, state, _) = Make("tbt");
        state.YouTurn.TurnPath = North();

        var drawn = projector.BuildScene(1).UTurnPath!;
        Assert.That(drawn.All(p => Math.Abs(p.E - 2) < 1e-9));
        Assert.That(drawn[0].N, Is.EqualTo(-(Behind + 3.0)).Within(1e-9), "the tank's 3 m further back");
    }

    [Test]
    public void The_Scene_is_sent_again_when_the_trailed_implement_s_place_changes()
    {
        var (projector, state, config) = Make("trailing");
        state.YouTurn.TurnPath = North();
        long before = projector.SceneFingerprint();

        config.Tool.TrailingHitchLength = 4.151;
        Assert.That(projector.SceneFingerprint(), Is.Not.EqualTo(before), "the wheels moved a millimetre");

        before = projector.SceneFingerprint();
        config.Tool.TrailingToolToPivotLength = -1.5;
        Assert.That(projector.SceneFingerprint(), Is.Not.EqualTo(before), "the discs moved");

        before = projector.SceneFingerprint();
        config.Vehicle.HitchLength = 1.6;
        Assert.That(projector.SceneFingerprint(), Is.Not.EqualTo(before), "the tractor's pin moved");

        before = projector.SceneFingerprint();
        config.Tool.Offset = 0.3;
        Assert.That(projector.SceneFingerprint(), Is.Not.EqualTo(before), "the implement moved to one side");

        before = projector.SceneFingerprint();
        config.Tool.SetToolType("tbt");
        Assert.That(projector.SceneFingerprint(), Is.Not.EqualTo(before), "a tank between them now");

        before = projector.SceneFingerprint();
        config.Tool.TankTrailingHitchLength = 3.5;
        Assert.That(projector.SceneFingerprint(), Is.Not.EqualTo(before), "the tank's hitch moved");

        before = projector.SceneFingerprint();
        config.Tool.SetToolType("rear");
        Assert.That(projector.SceneFingerprint(), Is.Not.EqualTo(before), "no longer a trailed implement");
    }

    [Test]
    public void The_way_to_a_recorded_path_is_drawn_as_it_is()
    {
        // The recorded path is where the tractor drove: its approach is not a turn of the implement's.
        var (projector, state, _) = Make("trailing");
        var approach = North();
        state.RecordedPath.RecordedPoints = Enumerable.Range(0, 50).Select(i => new RecPathPoint(2, 10 + i * 0.5, 0, 8, true)).ToList();
        state.RecordedPath.DubinsApproachPath = approach;
        state.RecordedPath.IsFollowingDubinsToPath = true;
        state.RecordedPath.IsDrivingRecordedPath = true;

        var drawn = projector.BuildScene(1).UTurnPath!;
        Assert.That(drawn.Select(p => (p.E, p.N)), Is.EqualTo(approach.Select(p => (p.Easting, p.Northing))));
    }
}
