// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Reflection;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
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

namespace AgOpenWeb.Services.Tests.Pipeline;

/// <summary>
/// Holding the line when RTK is lost (fork only), through the whole GPS cycle: the step the
/// receiver's position makes when it leaves RTK fixed is taken out of the position everything
/// after it uses, and the operator is told.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class RtkHoldPipelineTests
{
    private GpsService _gpsService = null!;
    private GpsPipelineService _pipeline = null!;
    private List<GpsCycleResult> _results = null!;
    private IAudioService _audio = null!;
    private LocalPlane _plane = null!;
    private int _epoch;

    [SetUp]
    public void SetUp()
    {
        _audio = Substitute.For<IAudioService>();
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.Vehicle.AntennaPivot = 0;
        config.Vehicle.AntennaOffset = 0;
        config.Vehicle.AntennaHeight = 0;
        config.Tool.Width = 6;
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, 600);

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

        _pipeline = new GpsPipelineService(
            _gpsService, toolPosition, new TrackGuidanceService(),
            sectionControl, coverage, autoSteer,
            new YouTurnGuidanceService(),
            new YouTurnStateMachine(
                new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance,
                    Substitute.For<AgOpenWeb.Services.Geometry.IPolygonOffsetService>(), config),
                new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, config),
                NullLogger<YouTurnStateMachine>.Instance, config),
            _audio,
            new PipelineIntents(),
            headingFusion,
            NullLogger<GpsPipelineService>.Instance,
            config,
            new PositionEstimator());

        _plane = new LocalPlane(new Wgs84(43.7128, -74.006), new SharedFieldProperties());
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

    private GpsCycleResult Last => _results[^1];

    /// <summary>The next fix, driving north up the line E = 0 at 0.25 m a fix, the receiver
    /// <paramref name="error"/> m east of the truth.</summary>
    private void Fix(int quality, double error = 0)
    {
        _epoch++;
        var w = _plane.ConvertGeoCoordToWgs84(new GeoCoord(10 + _epoch * 0.25, error));
        _gpsService.UpdateGpsData(new GpsData
        {
            CurrentPosition = new Position { Latitude = w.Latitude, Longitude = w.Longitude, Speed = 9 },
            FixQuality = quality,
            SatellitesInUse = 12,
            IsValid = true,
        });
    }

    private void DriveOnRtk()
    {
        _epoch = 0;
        for (int i = 0; i < 30; i++) Fix(4);
    }

    [Test]
    public void The_jump_is_taken_out_of_the_position_steered()
    {
        DriveOnRtk();

        Fix(1, error: 0.9);

        Assert.Multiple(() =>
        {
            Assert.That(Last.Easting, Is.EqualTo(0).Within(0.02), "the tractor stays on the line");
            Assert.That(Last.RtkHoldM, Is.EqualTo(0.9).Within(0.02));
            Assert.That(Last.RtkHoldNotice, Is.EqualTo("RTK lost: the 0.9 m jump of the position is taken out and the line held for up to 3 min"));
        });
        Fix(1, error: 0.9);
        Assert.That(Last.RtkHoldNotice, Is.Null, "told once");
    }

    [Test]
    public void When_RTK_is_back_there_is_no_jump_either()
    {
        DriveOnRtk();
        Fix(1, error: 0.9);
        for (int i = 0; i < 20; i++) Fix(1, error: 1.2);

        Fix(4);

        Assert.Multiple(() =>
        {
            Assert.That(Last.Easting, Is.EqualTo(0.3).Within(0.02), "where it was a fix ago");
            Assert.That(Last.RtkHoldNotice, Is.EqualTo("RTK back: the line moves the 0.3 m it was held to the true position, slowly"));
        });
    }

    [Test]
    public void Off_the_position_jumps_as_before()
    {
        ConfigurationStore.Instance.Connections.RtkHold = false;
        DriveOnRtk();

        Fix(1, error: 0.9);

        Assert.Multiple(() =>
        {
            Assert.That(Last.Easting, Is.EqualTo(0.9).Within(0.02));
            Assert.That(Last.RtkHoldM, Is.Zero);
            Assert.That(Last.RtkHoldNotice, Is.Null);
        });
    }

    [Test]
    public void On_by_default()
    {
        Assert.That(new ConnectionConfig().RtkHold, Is.True);
    }
}
