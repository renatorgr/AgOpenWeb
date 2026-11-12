// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using NSubstitute;
using TrackModel = AgOpenWeb.Models.Track.Track;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Offset Fix › Move the field instead (fork only): the open field's drawing moves by the
/// opposite of the offset, the offset goes back to 0, and the field comes back open with the
/// drawing where the tractor saw it.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class MoveFieldTests
{
    private string _root = null!;
    private string _dir = null!;
    private MainViewModelBuilder _b = null!;
    private readonly List<string> _said = new();

    private static BoundaryPolygon Square(double e0, double n0, double side)
    {
        var p = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (e0, n0), (e0 + side, n0), (e0 + side, n0 + side), (e0, n0 + side) })
            p.Points.Add(new BoundaryPoint(e, n, 0));
        p.UpdateBounds();
        return p;
    }

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _root = Path.Combine(Path.GetTempPath(), "aow-move-" + Guid.NewGuid().ToString("N"));
        _dir = Path.Combine(_root, "F");
        Directory.CreateDirectory(_dir);
        var b = new Boundary { OuterBoundary = Square(0, 0, 200) };
        var tree = new BoundaryPolygon { AtObstacle = ObstacleAction.GoRound };
        tree.SetCircle(new ObstacleCircle(100, 100, 10));
        b.InnerBoundaries.Add(tree);
        GeoJsonFieldService.Save(new Field
        {
            Name = "F", DirectoryPath = _dir, Origin = new Position { Latitude = 39.42, Longitude = -7.53 }, Boundary = b,
        }, tracks: null);
        GeoJsonFieldService.SaveTracks(_dir, new[] { TrackModel.FromABLine("AB", new Vec3(20, 10, 0), new Vec3(20, 190, 0)) });
        _b = new MainViewModelBuilder { FieldService = new FieldService() };
        _b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = _root });
        _said.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { }
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    private async Task<MainViewModel> Open()
    {
        var vm = _b.Build();
        vm.FailureReported += _said.Add;
        await vm.OpenFieldAsync(_dir, "F");
        return vm;
    }

    [Test]
    public async Task The_offset_goes_into_the_field_and_back_to_zero()
    {
        var vm = await Open();
        vm.OffsetFixSet(-1.2, 0.4); // the tractor lined up with the drawing: 1.2 m west, 0.4 m north

        await vm.RemoteMoveFieldAsync(1.2, -0.4, moveTracks: true);

        var outer = vm.CurrentBoundary!.OuterBoundary!.Points[0];
        var tree = vm.CurrentBoundary.InnerBoundaries[0].Circle!.Value;
        var ab = vm.SavedTracks.Single(t => t.Name == "AB");
        var onDisk = new FieldService().LoadField(_dir).Boundary!.OuterBoundary!.Points[0];
        Assert.Multiple(() =>
        {
            Assert.That(vm.IsFieldOpen, Is.True, "the field is open again");
            Assert.That(vm.CurrentFieldName, Is.EqualTo("F"));
            Assert.That(vm.State.Field.DriftEasting, Is.EqualTo(0).Within(1e-9), "the offset is back to 0");
            Assert.That(vm.State.Field.DriftNorthing, Is.EqualTo(0).Within(1e-9));
            Assert.That((outer.Easting, outer.Northing), Is.EqualTo((1.2, -0.4)).Using<(double, double)>(Near), "the boundary moved");
            Assert.That((onDisk.Easting, onDisk.Northing), Is.EqualTo((1.2, -0.4)).Using<(double, double)>(Near), "and is saved moved");
            Assert.That((tree.CenterEasting, tree.CenterNorthing), Is.EqualTo((101.2, 99.6)).Using<(double, double)>(Near), "the tree moved");
            Assert.That((ab.Points[0].Easting, ab.Points[0].Northing), Is.EqualTo((21.2, 9.6)).Using<(double, double)>(Near), "the track moved");
            Assert.That(_said, Has.Some.EqualTo("Field moved 1.20 m east, 0.40 m south"));
        });
    }

    [Test]
    public async Task The_tracks_can_stay()
    {
        var vm = await Open();

        await vm.RemoteMoveFieldAsync(-2, 0, moveTracks: false);

        var ab = vm.SavedTracks.Single(t => t.Name == "AB");
        Assert.Multiple(() =>
        {
            Assert.That((ab.Points[0].Easting, ab.Points[0].Northing), Is.EqualTo((20.0, 10.0)).Using<(double, double)>(Near));
            Assert.That(vm.CurrentBoundary!.OuterBoundary!.Points[0].Easting, Is.EqualTo(-2).Within(0.001));
            Assert.That(vm.State.Field.DriftEasting, Is.EqualTo(-2).Within(1e-9), "with no offset before, the tractor keeps its place by an offset");
            Assert.That(_said, Has.Some.EqualTo("Field moved 2.00 m west; its tracks stay where they were"));
        });
    }

    [Test]
    public async Task Refused_with_AutoSteer_on_or_nothing_to_move()
    {
        var vm = await Open();

        await vm.RemoteMoveFieldAsync(0.001, 0, moveTracks: true);
        await vm.RemoteMoveFieldAsync(80, 0, moveTracks: true);
        vm.IsAutoSteerEngaged = true;
        await vm.RemoteMoveFieldAsync(1, 0, moveTracks: true);

        Assert.Multiple(() =>
        {
            Assert.That(_said, Is.EqualTo(new[]
            {
                "Nothing to move: the offset is 0",
                "A field can be moved 50 m at most",
                "Turn AutoSteer off before moving the field",
            }));
            Assert.That(new FieldService().LoadField(_dir).Boundary!.OuterBoundary!.Points[0].Easting, Is.EqualTo(0).Within(0.001), "nothing moved");
        });
    }

    private static bool Near((double, double) a, (double, double) b) =>
        Math.Abs(a.Item1 - b.Item1) < 0.001 && Math.Abs(a.Item2 - b.Item2) < 0.001;
}
