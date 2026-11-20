// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Coverage;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Section;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The field data card's Rate and Trip area count only while sections are on: with the
/// sections off nothing is worked. An 8 m boom of 8 sections of 1 m (a tester's sprayer),
/// driven north at 8 km/h by the real <see cref="SectionControlService"/>, which paints the
/// real <see cref="CoverageMapService"/> (the Trip area is its <c>TotalWorkedAreaUser</c>).
/// Fork only: AgOpenGPS's rate (<c>CFieldData.WorkRateHectares</c>) is the whole implement.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class FieldCardRateTests
{
    private const int Sections = 8;
    private const double Kmh = 8;

    private ConfigurationStore _config = null!;
    private CoverageMapService _coverage = null!;
    private SectionControlService _sections = null!;
    private MainViewModel _vm = null!;
    private double _northing;

    [SetUp]
    public void SetUp()
    {
        _config = new ConfigurationStore();
        ConfigurationStore.SetInstance(_config);
        _config.NumSections = Sections;
        for (int i = 0; i < Sections; i++) _config.Tool.SetSectionWidth(i, 100);
        _config.Tool.Offset = 0;
        _config.Tool.IsSectionsNotZones = true;
        _coverage = new CoverageMapService(_config);
        _coverage.SetFieldBounds(-200, 200, -200, 200);
        _sections = new SectionControlService(Substitute.For<IToolPositionService>(), _coverage, _config);
        var b = new MainViewModelBuilder { SectionControlService = _sections };
        _vm = b.Build();
        _northing = 0;
    }

    [TearDown]
    public void TearDown() => ConfigurationStore.SetInstance(new ConfigurationStore());

    /// <summary>Drive north at <see cref="Kmh"/>, a fix every 0.1 s.</summary>
    private void Drive(double metres)
    {
        double speed = Kmh / 3.6, dt = 0.1;
        int steps = (int)System.Math.Round(metres / (speed * dt));
        for (int k = 0; k < steps; k++)
        {
            _northing += speed * dt;
            _vm.State.Vehicle.Speed = speed;
            _sections.Update(new Vec3(0, _northing, 0), 0, 0, speed);
        }
    }

    [Test]
    public void SectionsOff_RateAndTripAreaStayAtZero()
    {
        _sections.SetAllSections(SectionButtonState.Off);

        Drive(40);

        Assert.Multiple(() =>
        {
            Assert.That(_vm.WorkRateHaPerHour, Is.Zero, "driving with every section off works nothing");
            Assert.That(_coverage.TotalWorkedAreaUser, Is.Zero, "the trip area counts only painted ground");
        });
    }

    [Test]
    public void HalfTheBoomOn_CountsHalf_AndOffAgainStopsCounting()
    {
        _sections.SetAllSections(SectionButtonState.Off);
        for (int i = 0; i < 4; i++) _sections.SetSectionState(i, SectionButtonState.On);

        Drive(40);
        double rateOn = _vm.WorkRateHaPerHour, tripOn = _coverage.TotalWorkedAreaUser;

        _sections.SetAllSections(SectionButtonState.Off);
        Drive(40);

        Assert.Multiple(() =>
        {
            Assert.That(rateOn, Is.EqualTo(4 * Kmh * 0.1).Within(1e-9), "4 m on at 8 km/h: 3.2 ha/hr, not the 8 m boom's 6.4");
            Assert.That(tripOn, Is.EqualTo(4 * 40).Within(4 * 40 * 0.05), "4 m over 40 m");
            Assert.That(_vm.WorkRateHaPerHour, Is.Zero, "all off again");
            Assert.That(_coverage.TotalWorkedAreaUser, Is.EqualTo(tripOn).Within(1e-9), "nothing more with the sections off");
        });
    }

    [Test]
    public void Reversing_WithSectionsOnByHand_CountsTheSpeedNotItsSign()
    {
        _sections.SetAllSections(SectionButtonState.On);
        _vm.State.Vehicle.Speed = -Kmh / 3.6;

        Assert.That(_vm.WorkRateHaPerHour, Is.EqualTo(Sections * Kmh * 0.1).Within(1e-9));
    }

    [Test]
    public void ASectionLeftOnPastTheConfiguredCount_IsNotCounted()
    {
        _sections.SetAllSections(SectionButtonState.On);
        _config.NumSections = 2;
        _sections.SectionStates[5].IsOn = true; // as if the count was cut with it on
        _vm.State.Vehicle.Speed = Kmh / 3.6;

        Assert.That(_vm.WorkRateHaPerHour, Is.EqualTo(2 * Kmh * 0.1).Within(1e-9), "the two configured sections only");
    }
}
