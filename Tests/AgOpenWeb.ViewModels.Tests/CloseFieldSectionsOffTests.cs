// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Closing a field switches the master Auto and Manual buttons and every section off, as
/// AgOpenGPS JobClose does. Left on, sections switched on by hand kept the module's section
/// bits set after the close, spraying on with no field open, while the section bar and the
/// Manual button are hidden without a field, so the operator had nothing to switch them off
/// with. Opening another field closes the open one first, so the same code covers that.
/// </summary>
[TestFixture]
public class CloseFieldSectionsOffTests
{
    private string _root = null!;
    private MainViewModelBuilder _b = null!;
    private MainViewModel _vm = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _root = Path.Combine(Path.GetTempPath(), "aow-closesec-" + Guid.NewGuid().ToString("N"));
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

    private void Sections(params SectionButtonState[] buttons)
    {
        var states = new SectionControlState[buttons.Length];
        for (int i = 0; i < buttons.Length; i++)
            states[i] = new SectionControlState { Index = i, ButtonState = buttons[i], IsOn = buttons[i] == SectionButtonState.On };
        _b.SectionControlService.SectionStates.Returns(states);
    }

    [Test]
    public async Task ClosingTheField_WithManualOn_SwitchesTheMastersAndEverySectionOff()
    {
        _vm.IsManualSectionMode = true;
        Sections(SectionButtonState.On, SectionButtonState.On, SectionButtonState.On);

        await _vm.CloseFieldAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_vm.IsManualSectionMode, Is.False);
            Assert.That(_vm.IsSectionMasterOn, Is.False);
            Assert.That(_vm.State.Operation.IsSectionManualAll, Is.False);
            Assert.That(_vm.State.Operation.IsSectionAutoMaster, Is.False);
            Assert.That(_vm.IsFieldOpen, Is.False);
        });
        _b.SectionControlService.Received(1).SetAllSections(SectionButtonState.Off);
    }

    [Test]
    public async Task ClosingTheField_WithTheAutoMasterOn_SwitchesItOff()
    {
        _vm.IsSectionMasterOn = true;
        Sections(SectionButtonState.Auto, SectionButtonState.Auto);

        await _vm.CloseFieldAsync();

        Assert.That(_vm.IsSectionMasterOn, Is.False);
        _b.SectionControlService.Received(1).SetAllSections(SectionButtonState.Off);
    }

    // #316: a section switched on by its own button or hotkey, with neither master on.
    [Test]
    public async Task ClosingTheField_SwitchesOffASectionOnByItsOwnButton()
    {
        Sections(SectionButtonState.Off, SectionButtonState.On);
        Assume.That(_vm.IsSectionMasterOn || _vm.IsManualSectionMode, Is.False);

        await _vm.CloseFieldAsync();

        _b.SectionControlService.Received(1).SetAllSections(SectionButtonState.Off);
    }

    [Test]
    public async Task ClosingTheField_WithNothingOn_LeavesTheSectionsAlone()
    {
        Sections(SectionButtonState.Off, SectionButtonState.Off);

        await _vm.CloseFieldAsync();

        _b.SectionControlService.DidNotReceive().SetAllSections(Arg.Any<SectionButtonState>());
    }

    [Test]
    public async Task ClosingWithNoFieldOpen_WithNothingOn_LeavesTheSectionsAlone()
    {
        _vm.ActiveField = null;
        _vm.IsFieldOpen = false;
        Sections(SectionButtonState.Off);

        await _vm.CloseFieldAsync();

        _b.SectionControlService.DidNotReceive().SetAllSections(Arg.Any<SectionButtonState>());
    }
}
