// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Job;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The field data card's Remaining and Time left, and the status bar's Done and Left (fork only): the ground
/// inside the field still to paint, from the coverage map, and the time to paint it at the job's pace
/// (<c>MainViewModel.FieldProgress</c>). The job keeps its pace, so it goes on from there when opened again;
/// a trip off the field is not work.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class FieldProgressTests
{
    private MainViewModelBuilder _b = null!;
    private MainViewModel _vm = null!;
    private Job _job = null!;
    private double _painted;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _b = new MainViewModelBuilder();
        _job = new Job { TaskName = "Spray", WorkSeconds = 0, WorkPaintedSqM = 0 };
        _b.JobService.ActiveJob.Returns(_job);
        _b.CoverageMapService.ActualAreaCovered.Returns(_ => _painted);
        _vm = _b.Build();
        _vm.State.Field.CurrentBoundary = new Boundary
        {
            OuterBoundary = Polygon(0, 200, 0, 500),
            InnerBoundaries = { Polygon(90, 110, 240, 260) },
        };
    }

    [TearDown]
    public void TearDown() => ConfigurationStore.SetInstance(new ConfigurationStore());

    private static BoundaryPolygon Polygon(double minE, double maxE, double minN, double maxN)
    {
        var p = new BoundaryPolygon();
        p.Points.Add(new BoundaryPoint(minE, minN, 0));
        p.Points.Add(new BoundaryPoint(maxE, minN, 0));
        p.Points.Add(new BoundaryPoint(maxE, maxN, 0));
        p.Points.Add(new BoundaryPoint(minE, maxN, 0));
        p.UpdateBounds();
        return p;
    }

    [Test]
    public void The_field_is_counted_as_its_boundary_and_its_obstacles()
    {
        IReadOnlyList<IReadOnlyList<Vec2>>? asked = null;
        _b.CoverageMapService.MeasureCoverage(Arg.Any<IReadOnlyList<IReadOnlyList<Vec2>>>(), null)
            .Returns(c => { asked = (IReadOnlyList<IReadOnlyList<Vec2>>)c[0]; return (99600.0, 30000.0); });

        _vm.CountFieldNow();

        Assert.Multiple(() =>
        {
            Assert.That(asked, Has.Count.EqualTo(2), "the outer boundary and the obstacle (a hole)");
            Assert.That(_vm.FieldUnpaintedSqM, Is.EqualTo(69600));
            Assert.That(_vm.FieldPaintedSqM, Is.EqualTo(30000));
        });
    }

    [Test]
    public void The_time_left_is_at_the_job_pace_once_it_has_one_and_the_job_keeps_it()
    {
        _b.CoverageMapService.MeasureCoverage(Arg.Any<IReadOnlyList<IReadOnlyList<Vec2>>>(), null).Returns((99600.0, 0.0));
        ConfigurationStore.Instance.NumSections = 1;
        ConfigurationStore.Instance.Tool.SetSectionWidth(0, 1200); // a 12 m boom
        _vm.State.Vehicle.Easting = 100; _vm.State.Vehicle.Northing = 100;
        _vm.State.Vehicle.Speed = 10 / 3.6;
        // Three minutes: 2 of them spraying at 10 km/h, 1 turning (10 x 12 m x 120 s / 3.6 = 4000 m²).
        double t = 1000;
        _vm.SampleJobPace(t);
        for (int s = 0; s < 180; s++)
        {
            t += 1;
            if (s < 120) _painted += 12 * 10 / 3.6;
            _vm.SampleJobPace(t);
        }
        _b.CoverageMapService.MeasureCoverage(Arg.Any<IReadOnlyList<IReadOnlyList<Vec2>>>(), null).Returns((99600.0, _painted));
        _vm.CountFieldNow();

        double pace = _painted / 180; // m² a second of work
        Assert.Multiple(() =>
        {
            Assert.That(_vm.JobPaceHaPerHour, Is.EqualTo(pace * 0.36).Within(1e-9), "2/3 of the time painting: 8 ha/hr");
            Assert.That(_vm.FieldTimeLeftSeconds, Is.EqualTo((99600 - _painted) / pace).Within(1e-6));
            Assert.That(_job.WorkSeconds, Is.EqualTo(180).Within(1e-9), "kept in the job (job.json)");
            Assert.That(_job.WorkPaintedSqM, Is.EqualTo(_painted).Within(1e-9));
        });

        // Standing (a refill): the time left stays what it was, not ∞.
        _vm.State.Vehicle.Speed = 0;
        double before = _vm.FieldTimeLeftSeconds;
        for (int s = 0; s < 60; s++) { t += 1; _vm.SampleJobPace(t); }
        Assert.That(_vm.FieldTimeLeftSeconds, Is.EqualTo(before), "a stop is not work");
    }

    [Test]
    public void Driving_off_the_field_is_not_work()
    {
        _vm.State.Vehicle.Easting = 100; _vm.State.Vehicle.Northing = -200; // 200 m south of the fence
        _vm.State.Vehicle.Speed = 20 / 3.6;
        _vm.SampleJobPace(0);
        for (int s = 1; s <= 60; s++) _vm.SampleJobPace(s);

        Assert.That(_job.WorkSeconds, Is.Zero);

        _vm.State.Vehicle.Northing = -20; // a turn just outside: on the field
        for (int s = 61; s <= 70; s++) _vm.SampleJobPace(s);

        Assert.That(_job.WorkSeconds, Is.EqualTo(10).Within(1e-9));
    }

    [Test]
    public void Before_the_first_count_nothing_is_known()
    {
        Assert.Multiple(() =>
        {
            Assert.That(_vm.FieldUnpaintedSqM, Is.NaN);
            Assert.That(_vm.FieldTimeLeftSeconds, Is.NaN);
            Assert.That(_vm.JobPaceHaPerHour, Is.NaN);
        });
    }
}
