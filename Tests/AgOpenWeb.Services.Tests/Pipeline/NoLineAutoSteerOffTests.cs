// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Coverage;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Pipeline;
using AgOpenWeb.Services.Section;
using AgOpenWeb.Services.Tool;
using AgOpenWeb.Services.Track;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using TrackModel = AgOpenWeb.Models.Track.Track;

namespace AgOpenWeb.Services.Tests.Pipeline;

/// <summary>
/// AutoSteer on a pass with no line (the offset left nothing of it) goes off with the alarm and
/// the reason. It used to stay on, hold the wheels straight and say nothing, and the hard fence
/// guard, which needs a line, never fired: on Couto da Ribeira (the beta robot) an AB line from
/// AgOpenGPS with A and B 0.96 m apart had no passes, and the tractor drove straight through the
/// hard fence 24 times. Fork only.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class NoLineAutoSteerOffTests
{
    private const double SpeedKmh = 8;

    private readonly Wgs84 _origin = new(39.4194605, -7.529441833);
    private LocalPlane _plane = null!;
    private IAudioService _audio = null!;
    private GpsService _gpsService = null!;
    private GpsPipelineService _pipeline = null!;
    private List<GpsCycleResult> _results = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.Vehicle.AntennaPivot = 0;
        config.Vehicle.AntennaOffset = 0;
        config.Vehicle.AntennaHeight = 0;
        config.Vehicle.Wheelbase = 2.5;
        config.Tool.Width = 8;
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, 800);
        config.Tool.Overlap = 0;

        _gpsService = new GpsService();
        _gpsService.Start();
        var toolPosition = new ToolPositionService(config);
        var coverage = new CoverageMapService(config);
        var sectionControl = new SectionControlService(toolPosition, coverage, config);
        var autoSteer = new AutoSteerService(new TrackGuidanceService(),
            Substitute.For<IUdpCommunicationService>(), _gpsService, config);
        var headingFusion = Substitute.For<IGpsHeadingFusionService>();
        headingFusion.FuseHeading(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>(),
                Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>())
            .Returns(ci => ci.ArgAt<double>(0));
        _audio = Substitute.For<IAudioService>();
        _pipeline = new GpsPipelineService(
            _gpsService, toolPosition, new TrackGuidanceService(),
            sectionControl, coverage, autoSteer,
            new YouTurnGuidanceService(),
            new YouTurnStateMachine(
                new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance,
                    new Geometry.PolygonOffsetService(), config),
                new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, config),
                NullLogger<YouTurnStateMachine>.Instance, config),
            _audio,
            new PipelineIntents(),
            headingFusion,
            NullLogger<GpsPipelineService>.Instance,
            config,
            new PositionEstimator());

        _plane = new LocalPlane(_origin, new SharedFieldProperties());
        _pipeline.SetLocalPlane(_plane);
        _pipeline.SynchronousMode = true;
        _pipeline.Start();
        _results = new List<GpsCycleResult>();
        _pipeline.CycleCompleted += r => _results.Add(r);
    }

    [TearDown]
    public void TearDown()
    {
        _pipeline.Stop();
        _gpsService.Stop();
    }

    // A line running north through (0, 0), A and B 0.96 m apart as AgOpenGPS's A+ line keeps them.
    private static TrackModel OneMetreLine() => new()
    {
        Name = "A+0°",
        Type = AgOpenWeb.Models.Track.TrackType.ABLine,
        Points = new List<Vec3> { new(0, 0, 0), new(0, 0.96, 0) },
    };

    private GpsCycleResult Cycle(double e, double n)
    {
        var w = _plane.ConvertGeoCoordToWgs84(new GeoCoord(n, e));
        _gpsService.UpdateGpsData(new GpsData
        {
            CurrentPosition = new Position
            {
                Latitude = w.Latitude, Longitude = w.Longitude, Heading = 0, Speed = SpeedKmh / 3.6,
            },
            FixQuality = 4,
            IsValid = true,
        });
        return _results[^1];
    }

    [Test]
    public void AutoSteer_on_a_pass_with_no_line_goes_off_with_the_alarm()
    {
        // Pass 5 (44 m east): the offset keeps A and drops B, 0.96 m on, so it has no line.
        _pipeline.SetActiveTrack(OneMetreLine(), passNumber: 5, nudgeOffset: 0);
        _pipeline.SetAutoSteerEngaged(true);

        var r = Cycle(44, 50);

        Assert.Multiple(() =>
        {
            Assert.That(r.AutoSteerDisengagedThisCycle, Is.True, "AutoSteer off");
            Assert.That(r.DisengageReason, Is.EqualTo(GpsPipelineService.NoLineReason));
            _audio.Received().Play(SoundEffect.UTurnTooClose);
        });

        // Put back on there: off again.
        _pipeline.SetAutoSteerEngaged(true);
        Assert.That(Cycle(44, 50.2).DisengageReason, Is.EqualTo(GpsPipelineService.NoLineReason));
    }

    [Test]
    public void AutoSteer_off_on_such_a_pass_says_nothing()
    {
        _pipeline.SetActiveTrack(OneMetreLine(), passNumber: 5, nudgeOffset: 0);
        var r = Cycle(44, 50);
        Assert.Multiple(() =>
        {
            Assert.That(r.AutoSteerDisengagedThisCycle, Is.False);
            _audio.DidNotReceive().Play(SoundEffect.UTurnTooClose);
        });
    }

    [Test]
    public void The_same_line_read_from_a_file_has_its_pass_and_AutoSteer_stays_on()
    {
        var line = OneMetreLine();
        line.Points = TrackModel.LengthenShortABLine(line.Points, 0);
        _pipeline.SetActiveTrack(line, passNumber: 5, nudgeOffset: 0);
        _pipeline.SetAutoSteerEngaged(true);

        var results = Enumerable.Range(0, 20).Select(i => Cycle(44, 50 + i * 0.2)).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(results.Any(r => r.AutoSteerDisengagedThisCycle), Is.False, "AutoSteer stays on");
            Assert.That(results[^1].Guidance!.CrossTrackError, Is.EqualTo(0).Within(0.5), "on pass 5, 44 m east");
        });
    }
}
