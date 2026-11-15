// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Headland;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using AgOpenWeb.Services.Headland;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The Field Builder's *Along the Passes* headland (fork only): wide where the active line's
/// passes end, narrow at the sides (<see cref="HeadlandAlongPasses"/>). Made, changed, kept when
/// the boundary is redrawn, saved with the field.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class HeadlandAlongPassesVmTests
{
    private string _root = null!;
    private string _dir = null!;
    private MainViewModel _vm = null!;
    private readonly List<string> _notices = new();

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _root = Path.Combine(Path.GetTempPath(), "aow-hlpasses-" + Guid.NewGuid().ToString("N"));
        _dir = Path.Combine(_root, "F");
        Directory.CreateDirectory(_dir);
        var field = new Field { Name = "F", DirectoryPath = _dir, Origin = new Position { Latitude = 52, Longitude = 5 } };
        GeoJsonFieldService.Save(field, tracks: null);

        var b = new MainViewModelBuilder { FieldService = new FieldService() };
        b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = _root });
        _vm = b.Build();
        _vm.ActiveField = field;
        _vm.IsFieldOpen = true;
        _notices.Clear();
        _vm.FailureReported += _notices.Add;

        // 100 m across the passes, 200 m along them.
        Field((0, 0), (100, 0), (100, 200), (0, 200));
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { }
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    private void Field(params (double e, double n)[] pts) => _vm.RemoteCreateBoundaryFromMapPoints(pts);

    private static Track NorthLine(string name = "AB 1") => new()
    {
        Name = name, Type = TrackType.ABLine, IsVisible = true,
        Points = new() { new Vec3(50, 0, 0), new Vec3(50, 100, 0) },
    };

    private static double Area(IReadOnlyList<Vec3> ring)
    {
        double sum = 0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            sum += ring[j].Easting * ring[i].Northing - ring[i].Easting * ring[j].Northing;
        return Math.Abs(sum) / 2;
    }

    private void ChooseNorthLine()
    {
        var t = NorthLine();
        _vm.SavedTracks.Add(t);
        _vm.SelectedTrack = t;
    }

    [Test]
    public void Makes_the_headland_wide_where_the_passes_end_and_narrow_beside_them()
    {
        ChooseNorthLine();

        _vm.RemoteCreateHeadlandAlongPasses(24, 8);

        var seg = _vm.HeadlandSegments.Single();
        Assert.Multiple(() =>
        {
            Assert.That(seg.Type, Is.EqualTo(HeadlandSegmentType.AlongPasses));
            Assert.That(seg.Offset, Is.EqualTo(24));
            Assert.That(seg.SideOffset, Is.EqualTo(8));
            Assert.That(seg.IsEffective, Is.True);
            Assert.That(_vm.HasHeadland, Is.True);
            Assert.That(Area(_vm.CurrentHeadlandLine!), Is.EqualTo(84 * 152).Within(1).Percent, "24 m at the ends, 8 m at the sides");
            Assert.That(_vm.CurrentHeadlandLine!.Min(p => p.Northing), Is.EqualTo(24).Within(0.3));
            Assert.That(_vm.CurrentHeadlandLine!.Min(p => p.Easting), Is.EqualTo(8).Within(0.3));
            Assert.That(_notices, Is.Empty);
        });
    }

    [Test]
    public void With_no_line_active_the_fields_only_line_is_used()
    {
        _vm.SavedTracks.Add(NorthLine());
        _vm.SelectedTrack = null;

        _vm.RemoteCreateHeadlandAlongPasses(24, 8);

        Assert.That(_vm.HeadlandSegments.Single().Type, Is.EqualTo(HeadlandSegmentType.AlongPasses));
    }

    [Test]
    public void With_no_line_the_operator_is_asked_to_choose_one()
    {
        _vm.RemoteCreateHeadlandAlongPasses(24, 8);

        Assert.That(_vm.HeadlandSegments, Is.Empty);
        Assert.That(_notices, Has.Some.Contains("Choose the AB line first"));
    }

    [Test]
    public void Ends_too_wide_for_the_field_are_refused()
    {
        ChooseNorthLine();

        _vm.RemoteCreateHeadlandAlongPasses(120, 8);

        Assert.That(_vm.HeadlandSegments, Is.Empty);
        Assert.That(_notices, Has.Some.Contains("leaves nothing of this field"));
    }

    [Test]
    public void It_takes_the_place_of_a_whole_boundary_headland_and_keeps_the_lines_drawn()
    {
        ChooseNorthLine();
        _vm.RemoteCreateHeadlandWholeBoundary(20);
        _vm.RemoteCreateHeadlandFromMapPoints(false, 30, 0, 0, 100, 0); // a line along the south edge

        _vm.RemoteCreateHeadlandAlongPasses(24, 8);

        Assert.That(_vm.HeadlandSegments.Select(s => s.Type),
            Is.EqualTo(new[] { HeadlandSegmentType.Line, HeadlandSegmentType.AlongPasses }));
    }

    [Test]
    public void Redrawing_the_boundary_rebuilds_it_on_the_new_one()
    {
        ChooseNorthLine();
        _vm.RemoteCreateHeadlandAlongPasses(24, 8);

        Field((0, 0), (100, 0), (100, 150), (0, 150));

        var seg = _vm.HeadlandSegments.Single();
        Assert.Multiple(() =>
        {
            Assert.That(seg.Type, Is.EqualTo(HeadlandSegmentType.AlongPasses), "kept, not dropped like a drawn line");
            Assert.That(Area(_vm.CurrentHeadlandLine!), Is.EqualTo(84 * 102).Within(1).Percent);
            Assert.That(_vm.CurrentHeadlandLine!.Max(p => p.Northing), Is.EqualTo(126).Within(0.3));
        });
    }

    [Test]
    public void The_ends_and_the_sides_can_be_changed_and_a_width_that_leaves_nothing_is_refused()
    {
        ChooseNorthLine();
        _vm.RemoteCreateHeadlandAlongPasses(24, 8);

        _vm.RemoteSetHeadlandSidesAt(0, 12);
        Assert.That(Area(_vm.CurrentHeadlandLine!), Is.EqualTo(76 * 152).Within(1).Percent, "sides 12 m");
        _vm.RemoteSetHeadlandOffsetAt(0, 30);
        Assert.That(Area(_vm.CurrentHeadlandLine!), Is.EqualTo(76 * 140).Within(1).Percent, "ends 30 m");

        _vm.RemoteSetHeadlandSidesAt(0, 60);

        Assert.Multiple(() =>
        {
            Assert.That(_notices, Has.Some.Contains("leaves nothing of this field"));
            Assert.That(_vm.HeadlandSegments[0].SideOffset, Is.EqualTo(12), "the headland stays as it was");
            Assert.That(Area(_vm.CurrentHeadlandLine!), Is.EqualTo(76 * 140).Within(1).Percent);
        });
    }

    [Test]
    public void It_is_not_moved_by_dragging_its_ends()
    {
        ChooseNorthLine();
        _vm.RemoteCreateHeadlandAlongPasses(24, 8);
        var before = _vm.HeadlandSegments[0].BoundaryPoints.Count;

        _vm.RemoteSaveHeadlandEdit(0, 0, 0, 100, 0);

        Assert.That(_vm.HeadlandSegments[0].BoundaryPoints, Has.Count.EqualTo(before));
        Assert.That(_vm.HeadlandSegments[0].Type, Is.EqualTo(HeadlandSegmentType.AlongPasses));
    }

    [Test]
    public void It_is_saved_with_the_field_and_read_back()
    {
        ChooseNorthLine();
        _vm.RemoteCreateHeadlandAlongPasses(24, 8);
        _vm.RemoteCreateHeadlandFromMapPoints(false, 30, 0, 0, 100, 0);
        HeadlandSegmentFileService.Save(_dir, _vm.HeadlandSegments);

        var json = File.ReadAllText(Path.Combine(_dir, "HeadlandSegments.json"));
        var read = HeadlandSegmentFileService.Load(_dir);

        Assert.Multiple(() =>
        {
            Assert.That(read.Select(s => s.Type), Is.EqualTo(new[] { HeadlandSegmentType.AlongPasses, HeadlandSegmentType.Line }));
            Assert.That(read[0].Offset, Is.EqualTo(24));
            Assert.That(read[0].SideOffset, Is.EqualTo(8));
            Assert.That(read[0].PassHeading, Is.EqualTo(0).Within(1e-9));
            Assert.That(json.Split("SideOffset").Length - 1, Is.EqualTo(1), "only the segment along the passes carries a side distance");
        });
    }
}
