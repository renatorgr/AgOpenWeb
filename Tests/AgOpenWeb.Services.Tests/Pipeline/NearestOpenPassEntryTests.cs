// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
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

namespace AgOpenWeb.Services.Tests.Pipeline;

/// <summary>
/// AutoSteer engaged in the headland with the U-turn skip mode "Ignore worked tracks" joins the
/// nearest pass still to do that the way through the headland reaches straight, not the pass under
/// the tractor (fork only). Renato on Tapada da Casa, with half the field worked one pass in two:
/// "go to the nearest clean pass you can get onto the line well; if it means going in very crooked,
/// avoid that one". Through the real GPS cycle: the field is 200 × 300 m with a 20 m headland, the
/// AB line runs north at <see cref="PassE"/>, the implement is 6 m, and the job's map is painted
/// pass by pass.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class NearestOpenPassEntryTests
{
    private const double Wheelbase = 2.5;
    private const double SpeedKmh = 8;
    private const double PassE = 40;
    private const double Width = 6;

    private readonly Wgs84 _origin = new(43.7128, -74.006);
    private LocalPlane _plane = null!;
    private PipelineIntents _intents = null!;
    private GpsService _gpsService = null!;
    private CoverageMapService _coverage = null!;
    private GpsPipelineService _pipeline = null!;
    private List<GpsCycleResult> _results = null!;
    private ConfigurationStore _config = null!;
    private Models.Track.Track _track = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _config = ConfigurationStore.Instance;
        _config.Vehicle.AntennaPivot = 0;
        _config.Vehicle.AntennaOffset = 0;
        _config.Vehicle.AntennaHeight = 0;
        _config.Vehicle.Wheelbase = Wheelbase;
        _config.Tool.Width = Width;
        _config.NumSections = 1;
        _config.Tool.SetSectionWidth(0, 600);
        _config.Tool.Overlap = 0;
        _config.Guidance.UTurnRadius = 6;
        _config.Guidance.UTurnDistanceFromBoundary = 1;
        _config.Guidance.LineEntryMode = LineEntryMode.Nearest; // Renato's: the clean pass does not depend on it

        _gpsService = new GpsService();
        _gpsService.Start();
        var toolPosition = new ToolPositionService(_config);
        _coverage = new CoverageMapService(_config);
        _coverage.SetFieldBounds(-120, 120, -30, 310);
        var sectionControl = new SectionControlService(toolPosition, _coverage, _config);
        var autoSteer = new AutoSteerService(new TrackGuidanceService(),
            Substitute.For<IUdpCommunicationService>(), _gpsService, _config);
        var headingFusion = Substitute.For<IGpsHeadingFusionService>();
        headingFusion.FuseHeading(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>(),
                Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>())
            .Returns(ci => ci.ArgAt<double>(0));
        _intents = new PipelineIntents();
        _pipeline = new GpsPipelineService(
            _gpsService, toolPosition, new TrackGuidanceService(),
            sectionControl, _coverage, autoSteer,
            new YouTurnGuidanceService(),
            new YouTurnStateMachine(
                new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance,
                    new Geometry.PolygonOffsetService(), _config),
                new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, _config),
                NullLogger<YouTurnStateMachine>.Instance, _config),
            Substitute.For<IAudioService>(),
            _intents,
            headingFusion,
            NullLogger<GpsPipelineService>.Instance,
            _config,
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

    /// <summary>The field, the headland line 20 m in, the AB line at <see cref="PassE"/> running
    /// north with the tractor on <paramref name="pass"/>; the skip mode; AutoSteer not engaged yet.</summary>
    private void OpenField(bool skipWorked, int pass = 0, bool hard = false)
    {
        var outer = new BoundaryPolygon
        {
            Points = new List<BoundaryPoint> { new(-100, -10, 0), new(100, -10, 0), new(100, 290, 0), new(-100, 290, 0) },
            IsHard = hard,
        };
        _pipeline.SetBoundary(new Boundary { OuterBoundary = outer });
        _pipeline.SetHeadlandLine(new List<Vec3>
        {
            new(-80, 10, 0), new(80, 10, 0), new(80, 270, 0), new(-80, 270, 0),
        });
        _track = Models.Track.Track.FromABLine("AB", new Vec3(PassE, -10, 0), new Vec3(PassE, 290, 0));
        _pipeline.SetActiveTrack(_track, passNumber: pass, nudgeOffset: 0);
        _pipeline.SetYouTurnConfig(uTurnSkipRows: 1, isSkipWorkedMode: skipWorked, headlandCalculatedWidth: 20, headlandDistance: 20);
    }

    /// <summary>The centre of pass <paramref name="pass"/> (east, m).</summary>
    private double PassCentre(int pass) => PassE + Models.Track.PassGeometry.Offset(_track, pass, Width);

    /// <summary>Paint the passes on the job's map across the cultivated area, as a job would.</summary>
    private void Paint(params int[] passes)
    {
        foreach (int pass in passes)
        {
            double c = PassCentre(pass);
            _coverage.StartMapping(0, new Vec2(c - Width / 2, 10), new Vec2(c + Width / 2, 10));
            for (double n = 11; n <= 270; n += 1)
                _coverage.AddCoveragePoint(0, new Vec2(c - Width / 2, n), new Vec2(c + Width / 2, n));
            _coverage.StopMapping(0);
        }
    }

    private GpsCycleResult Cycle(double e, double n, double headingRad)
    {
        var w = _plane.ConvertGeoCoordToWgs84(new GeoCoord(n, e));
        _gpsService.UpdateGpsData(new GpsData
        {
            CurrentPosition = new Position
            {
                Latitude = w.Latitude, Longitude = w.Longitude,
                Heading = headingRad * 180.0 / Math.PI, Speed = SpeedKmh / 3.6, // m/s
            },
            FixQuality = 4,
            IsValid = true,
        });
        return _results[^1];
    }

    /// <summary>The pass picked with AutoSteer off before it was engaged (at the look-ahead point,
    /// as AgOpenGPS).</summary>
    private int _passBefore;

    /// <summary>Engage AutoSteer with the tractor at (e, n) heading <paramref name="headingDeg"/>.</summary>
    private GpsCycleResult Engage(double e, double n, double headingDeg)
    {
        // AutoSteer off: the pass shown is picked.
        _passBefore = Cycle(e, n, headingDeg * Math.PI / 180).Guidance!.HowManyPathsAway;
        _pipeline.SetAutoSteerEngaged(true);
        return Cycle(e, n, headingDeg * Math.PI / 180);
    }

    /// <summary>Drive a bicycle on the cycle's steer angle until the pivot is <paramref name="untilN"/>
    /// north; the pivot's track.</summary>
    private List<Vec3> Drive(double e, double n, double headingDeg, double untilN = 60)
    {
        double h = headingDeg * Math.PI / 180.0, step = SpeedKmh / 3.6 * 0.1;
        var track = new List<Vec3>();
        for (int i = 0; i < 2000 && n < untilN && n > -15; i++)
        {
            var r = Cycle(e, n, h);
            track.Add(new Vec3(e, n, h));
            double steer = r.Guidance!.SteerAngle * Math.PI / 180.0;
            h += step / Wheelbase * Math.Tan(steer);
            e += Math.Sin(h) * step;
            n += Math.Cos(h) * step;
        }
        return track;
    }

    private static bool InCrop(Vec3 p) => Math.Abs(p.Easting) < 80 && p.Northing > 10 && p.Northing < 270;

    [Test]
    public void On_a_worked_pass_it_joins_the_nearest_pass_still_to_do()
    {
        OpenField(skipWorked: true);
        Paint(-1, 0, 1);
        double c0 = PassCentre(0);

        var first = Engage(c0, 0, 90); // in the south headland on pass 1R, heading east

        Assert.Multiple(() =>
        {
            Assert.That(first.Guidance!.HowManyPathsAway, Is.EqualTo(2), "3R, the nearest pass still to do ahead");
            Assert.That(first.PassNotice, Does.Contain("3R"));
            Assert.That(first.LineEntryPath, Is.Not.Null, "the way through the headland is driven");
            Assert.That(first.LineEntryPath![^1].Easting, Is.EqualTo(PassCentre(2)).Within(0.05));
            Assert.That(first.AutoSteerDisengagedThisCycle, Is.False);
        });

        var track = Drive(c0, 0, 90);
        var inCrop = track.Where(InCrop).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(inCrop, Is.Not.Empty);
            Assert.That(inCrop.Max(p => Math.Abs(p.Easting - PassCentre(2))), Is.LessThan(0.5),
                "in the crop only on 3R, straight");
            Assert.That(_results[^1].Guidance!.HowManyPathsAway, Is.EqualTo(2), "and stays on it");
        });
    }

    [Test]
    public void With_Ask_the_way_to_the_pass_chosen_is_offered()
    {
        _config.Guidance.LineEntryMode = LineEntryMode.Ask;
        OpenField(skipWorked: true);
        Paint(-1, 0, 1);

        var first = Engage(PassCentre(0), 0, 90);

        Assert.Multiple(() =>
        {
            Assert.That(first.Guidance!.HowManyPathsAway, Is.EqualTo(2));
            Assert.That(first.LineEntryOfferPass, Is.EqualTo("3R"), "offered, as the operator asked to be");
        });
        _intents.RequestLineEntryAnswer(take: true);
        var taken = Cycle(PassCentre(0) + 0.2, 0, Math.PI / 2);
        Assert.Multiple(() =>
        {
            Assert.That(taken.LineEntryOfferPass, Is.Null);
            Assert.That(taken.LineEntryPath![^1].Easting, Is.EqualTo(PassCentre(2)).Within(0.05), "and driven when taken");
        });
    }

    [Test]
    public void Without_ignore_worked_tracks_the_pass_under_the_tractor_is_kept()
    {
        OpenField(skipWorked: false);
        Paint(-1, 0, 1);

        var first = Engage(PassCentre(0), 0, 90);

        Assert.Multiple(() =>
        {
            Assert.That(_passBefore, Is.AnyOf(0, 1), "a worked pass");
            Assert.That(first.Guidance!.HowManyPathsAway, Is.EqualTo(_passBefore));
            Assert.That(first.LineEntryPath, Is.Null, "Nearest point, as before");
            Assert.That(first.PassNotice, Is.Null);
        });
    }

    [Test]
    public void On_the_line_of_a_pass_still_to_do_nothing_changes()
    {
        OpenField(skipWorked: true);
        Paint(-1, 1);

        var first = Engage(PassCentre(0), 0, 0); // on 1R, heading along it into the field

        Assert.Multiple(() =>
        {
            Assert.That(first.Guidance!.HowManyPathsAway, Is.EqualTo(0));
            Assert.That(first.LineEntryPath, Is.Null);
            Assert.That(first.PassNotice, Is.Null);
        });
    }

    [Test]
    public void In_the_cultivated_area_the_pass_under_the_tractor_is_kept()
    {
        OpenField(skipWorked: true);
        Paint(-1, 0, 1);

        var first = Engage(PassCentre(0), 50, 0); // mid-field on worked 1R

        Assert.Multiple(() =>
        {
            Assert.That(first.Guidance!.HowManyPathsAway, Is.EqualTo(0));
            Assert.That(first.LineEntryPath, Is.Null);
        });
    }

    [Test]
    public void A_pass_still_to_do_that_would_be_entered_crooked_is_passed_over()
    {
        // Heading east 4 m from the headland line, 3R is just ahead: no room to get straight on it
        // before the crop. The next one still to do further east is reached straight.
        OpenField(skipWorked: true);
        Paint(-1, 0, 1, 3);
        double c0 = PassCentre(0);

        var first = Engage(c0, 6, 90);

        Assert.Multiple(() =>
        {
            Assert.That(first.Guidance!.HowManyPathsAway, Is.Not.EqualTo(2), "3R would be entered crooked");
            Assert.That(first.Guidance!.HowManyPathsAway, Is.Not.AnyOf(-1, 0, 1, 3), "never onto a worked pass");
            Assert.That(first.LineEntryPath, Is.Not.Null);
        });
        int chosen = first.Guidance!.HowManyPathsAway;
        var track = Drive(c0, 6, 90);
        Assert.That(track.Where(InCrop).Max(p => Math.Abs(p.Easting - PassCentre(chosen))), Is.LessThan(0.5),
            "in the crop only on the pass chosen, straight");
    }

    [Test]
    public void With_no_pass_still_to_do_nearby_it_says_so_and_keeps_the_pass()
    {
        OpenField(skipWorked: true);
        Paint(Enumerable.Range(-9, 19).ToArray());

        var first = Engage(PassCentre(0), 0, 90);

        Assert.Multiple(() =>
        {
            Assert.That(first.Guidance!.HowManyPathsAway, Is.EqualTo(_passBefore));
            Assert.That(first.LineEntryNotice, Does.Contain("No pass still to do near here"));
        });
    }

    [Test]
    public void Facing_a_hard_fence_it_turns_round_onto_a_pass_still_to_do()
    {
        // Tapada da Casa: engaged in the headland facing the fence on a worked pass. The way turns
        // the tractor round to the nearest pass still to do instead of AutoSteer going off.
        OpenField(skipWorked: true, hard: true);
        Paint(-1, 0, 1);

        var first = Engage(PassCentre(0), 5, 180);

        Assert.Multiple(() =>
        {
            Assert.That(first.AutoSteerDisengagedThisCycle, Is.False, first.DisengageReason);
            Assert.That(first.Guidance!.HowManyPathsAway, Is.Not.AnyOf(-1, 0, 1));
            Assert.That(first.LineEntryPath, Is.Not.Null);
        });
    }
}
