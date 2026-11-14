// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The obstacles ticked in the boundary list are deleted together, after one confirmation in the
/// web client (a tester redrawing a field's trees had over 40 to delete one by one). The outer
/// boundary is never deleted this way, and a list that changed since the ticks were made (another
/// length) or a row that is not an obstacle deletes nothing, so a row number never names another
/// obstacle.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class DeleteTickedObstaclesTests
{
    private string _root = null!;
    private string _dir = null!;
    private MainViewModelBuilder _b = null!;

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
        _root = Path.Combine(Path.GetTempPath(), "aow-delticked-" + Guid.NewGuid().ToString("N"));
        _dir = Path.Combine(_root, "F");
        Directory.CreateDirectory(_dir);
        var b = new Boundary { OuterBoundary = Square(0, 0, 200) };
        b.InnerBoundaries.Add(Square(20, 20, 4));  // Inner 1, list row 1
        b.InnerBoundaries.Add(Square(60, 20, 5));  // Inner 2, row 2
        b.InnerBoundaries.Add(Square(100, 20, 6)); // Inner 3, row 3
        b.InnerBoundaries.Add(Square(140, 20, 30)); // Inner 4, row 4: a big patch that stays
        GeoJsonFieldService.Save(new Field
        {
            Name = "F", DirectoryPath = _dir, Origin = new Position { Latitude = 39.42, Longitude = -7.53 }, Boundary = b,
        }, tracks: null);
        _b = new MainViewModelBuilder { FieldService = new FieldService() };
        _b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = _root });
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { }
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    private static Boundary OnDisk(string dir) => new FieldService().LoadField(dir).Boundary!;

    private async Task<MainViewModel> Open()
    {
        var vm = _b.Build();
        await vm.OpenFieldAsync(_dir, "F");
        vm.RefreshBoundaryList();
        return vm;
    }

    private static double[] Easting(Boundary b) => b.InnerBoundaries.Select(i => i.Points.Min(p => p.Easting)).ToArray();

    [Test]
    public async Task The_ticked_obstacles_go_together_and_the_rest_stay()
    {
        var vm = await Open();
        vm.SelectedBoundaryIndex = 2;

        vm.RemoteDeleteObstacles(5, new[] { 3, 1 });

        var disk = OnDisk(_dir);
        Assert.Multiple(() =>
        {
            Assert.That(Easting(disk), Is.EqualTo(new double[] { 60, 140 }).Within(0.01), "Inner 1 and 3 gone, 2 and 4 kept, in order");
            Assert.That(disk.OuterBoundary, Is.Not.Null.And.Property(nameof(BoundaryPolygon.IsValid)).True, "the outer boundary stays");
            Assert.That(vm.State.Field.CurrentBoundary!.InnerBoundaries, Has.Count.EqualTo(2), "in the live boundary");
            Assert.That(vm.BoundaryItems.Select(i => i.BoundaryType), Is.EqualTo(new[] { "Outer", "Inner 1", "Inner 2" }));
            Assert.That(vm.SelectedBoundaryIndex, Is.EqualTo(-1), "the rows moved: nothing stays selected");
            Assert.That(vm.StatusMessage, Is.EqualTo("2 obstacles deleted"));
        });
    }

    [Test]
    public async Task Every_obstacle_ticked_leaves_the_outer_boundary_alone()
    {
        var vm = await Open();

        vm.RemoteDeleteObstacles(5, new[] { 1, 2, 3, 4 });

        var disk = OnDisk(_dir);
        Assert.That(disk.InnerBoundaries, Is.Empty);
        Assert.That(disk.OuterBoundary!.IsValid, Is.True);
        Assert.That(vm.BoundaryItems.Select(i => i.BoundaryType), Is.EqualTo(new[] { "Outer" }));
        Assert.That(vm.StatusMessage, Is.EqualTo("4 obstacles deleted"));
    }

    [Test]
    public async Task The_outer_boundary_among_the_rows_deletes_nothing()
    {
        var vm = await Open();

        vm.RemoteDeleteObstacles(5, new[] { 0, 1 });

        Assert.That(OnDisk(_dir).InnerBoundaries, Has.Count.EqualTo(4));
        Assert.That(OnDisk(_dir).OuterBoundary!.IsValid, Is.True);
        Assert.That(vm.StatusMessage, Does.Contain("Only obstacles"));
    }

    [Test]
    public async Task A_list_of_another_length_deletes_nothing()
    {
        var vm = await Open();

        vm.RemoteDeleteObstacles(4, new[] { 1 }); // ticked before a row was added or deleted

        Assert.That(OnDisk(_dir).InnerBoundaries, Has.Count.EqualTo(4));
        Assert.That(vm.StatusMessage, Does.Contain("list changed"));
    }

    [Test]
    public async Task A_row_past_the_end_or_no_row_deletes_nothing()
    {
        var vm = await Open();

        vm.RemoteDeleteObstacles(5, new[] { 1, 5 });
        Assert.That(OnDisk(_dir).InnerBoundaries, Has.Count.EqualTo(4));

        vm.RemoteDeleteObstacles(5, Array.Empty<int>());
        Assert.That(OnDisk(_dir).InnerBoundaries, Has.Count.EqualTo(4));
        Assert.That(vm.StatusMessage, Does.Contain("Tick the obstacles"));
    }

    [Test]
    public async Task A_row_ticked_twice_is_one_obstacle()
    {
        var vm = await Open();

        vm.RemoteDeleteObstacles(5, new[] { 2, 2 });

        Assert.That(Easting(OnDisk(_dir)), Is.EqualTo(new double[] { 20, 100, 140 }).Within(0.01));
        Assert.That(vm.StatusMessage, Is.EqualTo("1 obstacle deleted"));
    }
}
