// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

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
/// A U-turn and the way onto a pass are planned and driven for the pivot. With a front implement
/// (a combine's header too) the map draws them where the implement's working centre goes: drawn
/// for the pivot, the green line ran through the rear axle and an 8 m front boom 4.7 m ahead was
/// beside it all the way round (a tester's simulator, 26 Nov 2026). Fork only.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class FrontToolPathOnMapTests
{
    private const double Reach = 4.7; // front hitch 4.5 m and the boom's working point 0.2 m from it

    private static (SceneProjector Projector, ApplicationState State, ConfigurationStore Config) Make(string toolType)
    {
        var state = new ApplicationState();
        var config = new ConfigurationStore();
        config.Vehicle.FrontHitchLength = 4.5;
        config.Tool.WorkingPointFromHitch = 0.2;
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

    [Test]
    public void With_a_front_implement_the_U_turn_is_drawn_where_the_implement_goes()
    {
        var (projector, state, _) = Make("front");
        state.YouTurn.TurnPath = North();

        var drawn = projector.BuildScene(1).UTurnPath!;
        Assert.That(drawn, Has.Count.EqualTo(21));
        Assert.That(drawn.All(p => System.Math.Abs(p.E - 2) < 1e-9));
        Assert.That(drawn[0].N, Is.EqualTo(Reach).Within(1e-9), "it starts at the implement, not at the rear axle");
        Assert.That(drawn[^1].N, Is.EqualTo(10 + Reach).Within(1e-9));
    }

    [Test]
    public void With_a_front_implement_the_way_onto_a_pass_is_drawn_where_the_implement_goes()
    {
        var (projector, state, _) = Make("front");
        state.Operation.LineEntryPath = North();

        var drawn = projector.BuildScene(1).EntryPath!;
        Assert.That(drawn[0].N, Is.EqualTo(Reach).Within(1e-9));
        Assert.That(drawn[^1].N, Is.EqualTo(10 + Reach).Within(1e-9));
    }

    [Test]
    public void A_front_implement_set_to_one_side_is_drawn_on_that_side()
    {
        var (projector, state, config) = Make("front");
        config.Tool.Offset = 0.5; // to the right
        state.YouTurn.TurnPath = North();

        var drawn = projector.BuildScene(1).UTurnPath!;
        Assert.That(drawn.All(p => System.Math.Abs(p.E - 2.5) < 1e-9), "0.5 m east of a path going north");
        Assert.That(drawn[0].N, Is.EqualTo(Reach).Within(1e-9));
    }

    // A trailed implement's working centre is drawn too: TrailedToolPathOnMapTests.
    [Test]
    public void With_an_implement_fixed_behind_the_tractor_the_paths_are_drawn_as_planned()
    {
        var (projector, state, _) = Make("rear");
        state.YouTurn.TurnPath = North();
        state.Operation.LineEntryPath = North();

        var scene = projector.BuildScene(1);
        Assert.That(scene.UTurnPath!.Select(p => (p.E, p.N)), Is.EqualTo(North().Select(p => (p.Easting, p.Northing))));
        Assert.That(scene.EntryPath!.Select(p => (p.E, p.N)), Is.EqualTo(North().Select(p => (p.Easting, p.Northing))));
    }

    [Test]
    public void The_Scene_is_sent_again_when_the_implement_s_place_changes()
    {
        var (projector, state, config) = Make("front");
        state.YouTurn.TurnPath = North();
        long before = projector.SceneFingerprint();

        config.Vehicle.FrontHitchLength = 5.0;
        Assert.That(projector.SceneFingerprint(), Is.Not.EqualTo(before), "the front hitch moved");

        before = projector.SceneFingerprint();
        config.Vehicle.FrontHitchLength = 5.001;
        Assert.That(projector.SceneFingerprint(), Is.Not.EqualTo(before), "a millimetre counts");

        before = projector.SceneFingerprint();
        config.Tool.Offset = 0.3;
        Assert.That(projector.SceneFingerprint(), Is.Not.EqualTo(before), "the implement moved to one side");

        before = projector.SceneFingerprint();
        config.Tool.SetToolType("rear");
        Assert.That(projector.SceneFingerprint(), Is.Not.EqualTo(before), "no longer a front implement");
    }

    [Test]
    public void The_way_to_a_recorded_path_is_drawn_as_it_is()
    {
        // The recorded path is where the tractor drove: its approach is not a turn of the implement's.
        var (projector, state, _) = Make("front");
        var approach = North();
        state.RecordedPath.RecordedPoints = Enumerable.Range(0, 50).Select(i => new RecPathPoint(2, 10 + i * 0.5, 0, 8, true)).ToList();
        state.RecordedPath.DubinsApproachPath = approach;
        state.RecordedPath.IsFollowingDubinsToPath = true;
        state.RecordedPath.IsDrivingRecordedPath = true;

        var drawn = projector.BuildScene(1).UTurnPath!;
        Assert.That(drawn.Select(p => (p.E, p.N)), Is.EqualTo(approach.Select(p => (p.Easting, p.Northing))));
    }
}
