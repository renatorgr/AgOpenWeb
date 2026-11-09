// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Communication;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The section buttons need an open field, as AgOpenGPS's button panel does (disabled with no
/// job, so the hotkeys' and the work switch's PerformClick do nothing). With no field the
/// section bar is hidden: a section switched on by the M, N or 1-8 keys or by lowering the
/// implement would have sprayed with nothing on the screen to turn it off.
/// </summary>
[TestFixture]
public class SectionButtonsNeedAFieldTests
{
    private MainViewModelBuilder _b = null!;
    private MainViewModel _vm = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _b = new MainViewModelBuilder();
        _vm = _b.Build();
        _b.SectionControlService.NumSections.Returns(8);
        var states = new SectionControlState[8];
        for (int i = 0; i < 8; i++) states[i] = new SectionControlState { Index = i };
        _b.SectionControlService.SectionStates.Returns(states);
        Assume.That(_vm.IsFieldOpen, Is.False);
    }

    [TearDown]
    public void TearDown() => ConfigurationStore.SetInstance(new ConfigurationStore());

    [Test]
    public void WithNoField_TheManualAndAutoMasters_StayOff()
    {
        _vm.ToggleManualModeCommand!.Execute(null);
        _vm.ToggleSectionMasterCommand!.Execute(null);

        Assert.Multiple(() =>
        {
            Assert.That(_vm.IsManualSectionMode, Is.False);
            Assert.That(_vm.IsSectionMasterOn, Is.False);
        });
        _b.SectionControlService.DidNotReceive().SetAllSections(Arg.Any<SectionButtonState>());
    }

    [Test]
    public void WithNoField_ASectionButtonAndAZoneButton_DoNothing()
    {
        _vm.ToggleSectionCommand!.Execute(0);
        _vm.ToggleSectionCommand.Execute("3");
        _vm.ToggleZone(1);

        _b.SectionControlService.DidNotReceive().SetSectionState(Arg.Any<int>(), Arg.Any<SectionButtonState>());
    }

    // The work switch (PGN 253) asks for the Manual or Auto button through the module
    // communication service, as AgOpenGPS CheckWorkAndSteerSwitch clicks the buttons.
    [Test]
    public void WithNoField_TheWorkSwitch_TurnsNothingOn()
    {
        _b.ModuleCommunicationService.SectionMasterToggleRequested += Raise.EventWith(_b.ModuleCommunicationService,
            new SectionMasterToggleEventArgs { Button = SectionMasterToggleEventArgs.SectionMasterButton.Manual });
        _b.ModuleCommunicationService.SectionMasterToggleRequested += Raise.EventWith(_b.ModuleCommunicationService,
            new SectionMasterToggleEventArgs { Button = SectionMasterToggleEventArgs.SectionMasterButton.Auto });

        Assert.Multiple(() =>
        {
            Assert.That(_vm.IsManualSectionMode, Is.False);
            Assert.That(_vm.IsSectionMasterOn, Is.False);
        });
        _b.SectionControlService.DidNotReceive().SetAllSections(Arg.Any<SectionButtonState>());
    }

    [Test]
    public void WithAField_TheButtonsWork()
    {
        _vm.IsFieldOpen = true;

        _vm.ToggleSectionMasterCommand!.Execute(null);
        Assert.That(_vm.IsSectionMasterOn, Is.True);
        _b.SectionControlService.Received(1).SetAllSections(SectionButtonState.Auto);

        _vm.ToggleManualModeCommand!.Execute(null);
        Assert.Multiple(() =>
        {
            Assert.That(_vm.IsManualSectionMode, Is.True);
            Assert.That(_vm.IsSectionMasterOn, Is.False, "Manual takes over from Auto");
        });
        _b.SectionControlService.Received(1).SetAllSections(SectionButtonState.On);

        _vm.ToggleSectionCommand!.Execute(2);
        _b.SectionControlService.Received(1).SetSectionState(2, SectionButtonState.Auto);

        _b.ModuleCommunicationService.SectionMasterToggleRequested += Raise.EventWith(_b.ModuleCommunicationService,
            new SectionMasterToggleEventArgs { Button = SectionMasterToggleEventArgs.SectionMasterButton.Manual });
        Assert.That(_vm.IsManualSectionMode, Is.False, "the work switch toggled Manual off");
    }
}
