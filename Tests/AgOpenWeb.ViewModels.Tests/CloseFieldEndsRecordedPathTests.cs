// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Closing a field ends the recorded path with it, as AgOpenGPS JobClose stops the replay
/// and drops the recording. A replay left driving put the Auto master back on at the next
/// fix, right after the close had switched the sections off; a loaded path would have been
/// driven in the next field's coordinates when that field had no path of its own.
/// </summary>
[TestFixture]
public class CloseFieldEndsRecordedPathTests
{
    private string _root = null!;
    private MainViewModelBuilder _b = null!;
    private MainViewModel _vm = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _root = Path.Combine(Path.GetTempPath(), "aow-closerec-path-" + Guid.NewGuid().ToString("N"));
        _b = new MainViewModelBuilder { FieldService = new FieldService() };
        _b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = _root });
        _vm = _b.Build();

        var dir = Path.Combine(_root, "F");
        Directory.CreateDirectory(dir);
        var field = new Field { Name = "F", DirectoryPath = dir, Origin = new Position { Latitude = 52, Longitude = 5 } };
        GeoJsonFieldService.Save(field, tracks: null);
        _vm.ActiveField = field;
        _vm.IsFieldOpen = true;
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { }
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    /// <summary>A straight path north, 30 m ahead of the tractor, recorded with the Auto master on.</summary>
    private static List<RecPathPoint> PathRecordedWithAutoOn()
    {
        var points = new List<RecPathPoint>();
        for (int i = 0; i < 12; i++)
            points.Add(new RecPathPoint(0, 30 + 5 * i, 0, 8, autoBtnState: true));
        return points;
    }

    [Test]
    public async Task ClosingTheField_StopsTheReplay_SoTheNextFixLeavesTheMasterOff()
    {
        _vm.State.RecordedPath.RecordedPoints = PathRecordedWithAutoOn();
        _vm.PlayRecordedPathCommand!.Execute(null);
        Assume.That(_vm.State.RecordedPath.IsDrivingRecordedPath, Is.True, "the replay was running before the close");
        _b.SectionControlService.SectionStates.Returns(new[] { new SectionControlState { Index = 0 } });

        await _vm.CloseFieldAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_vm.State.RecordedPath.IsDrivingRecordedPath, Is.False);
            Assert.That(_vm.State.RecordedPath.RecordedPoints, Is.Empty, "the closed field's path stayed loaded for the next field");
            Assert.That(_vm.HasUnsavedRecordedPath, Is.False);
        });

        // The next GPS fix after the close: with the replay still running it matched the
        // recorded Auto state and switched the master back on, with no field open.
        _vm.UpdateRecordedPathPlayback();

        Assert.That(_vm.IsSectionMasterOn, Is.False, "the replay put the Auto master back on after the close");
        _b.SectionControlService.DidNotReceive().SetAllSections(SectionButtonState.Auto);
    }

    [Test]
    public async Task ClosingTheField_DropsARecordingInProgress()
    {
        _vm.StartRecordedPathCommand!.Execute(null);
        Assume.That(_vm.IsRecordingPath, Is.True);

        await _vm.CloseFieldAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_vm.IsRecordingPath, Is.False);
            Assert.That(_vm.LiveRecordingPoints, Is.Empty);
        });
    }

    [Test]
    public async Task ClosingTheField_WithNoPathLoaded_ChangesNothingElse()
    {
        await _vm.CloseFieldAsync();

        Assert.That(_vm.State.RecordedPath.IsDrivingRecordedPath, Is.False);
        _b.SectionControlService.DidNotReceive().SetAllSections(Arg.Any<SectionButtonState>());
    }
}
