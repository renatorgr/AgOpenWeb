// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Guidance;
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

namespace AgOpenWeb.Services.Tests.Pipeline;

/// <summary>
/// A front implement on an open AB curve, through the whole cycle (fork only): a straight, a
/// bend one way, a straight, a bend the other way, a straight, for two machines (a tractor with
/// a front boom, a combine with its header), the wheels doing at once what is asked or answering
/// late at a limited speed. Pure Pursuit on the line moved for the implement (FrontToolLine)
/// left the implement 9 to 12 cm off the pass where a 20 m bend starts and ends, as it turns
/// early into the bend and early out of it; <see cref="FrontToolFollower"/> follows the line's
/// own bend there. The explicit tests print the tables.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class FrontToolFollowerTests
{
    private const double SpeedKmh = 8;
    private const double Dt = 0.1; // GPS cycle, s

    private readonly Wgs84 _origin = new(43.7128, -74.006);
    private LocalPlane _plane = null!;
    private GpsService _gpsService = null!;
    private GpsPipelineService _pipeline = null!;
    private List<GpsCycleResult> _results = null!;

    private sealed record Machine(string Name, double Wheelbase, double Reach, double MaxSteerDeg, double Width);

    // The Puma with the 8 m front boom (profile "Puma 180 - engate": wheelbase 2.9 m, Max Steer
    // Angle 35, front hitch 4.9 m) and Travis's John Deere 9660 (wheelbase 3.81 m, front axle to
    // cutter bar 3.56 m, 35 ft header; a 26 ft (7.9 m) turning radius, Travis 25 Nov 2026).
    private static readonly Machine[] Machines =
    {
        new("Puma + 8 m front boom", 2.9, 4.9, 35, 8),
        new("JD 9660 + 35 ft header", 3.81, 3.56, Math.Atan(3.81 / 7.9) * 180 / Math.PI, 10.67),
    };

    private sealed record Wheels(string Name, double LateS, double RateDegS);

    private static readonly Wheels[] Steerings =
    {
        new("at once", 0, double.PositiveInfinity),
        new("0.2 s late, 20 deg/s", 0.2, 20),
    };

    private void Build(Machine m, bool frontCorrection)
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.Vehicle.AntennaPivot = 0;
        config.Vehicle.AntennaOffset = 0;
        config.Vehicle.AntennaHeight = 0;
        config.Vehicle.Wheelbase = m.Wheelbase;
        config.Vehicle.MaxSteerAngle = m.MaxSteerDeg;
        config.Tool.Width = m.Width;
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, m.Width * 100);
        config.Tool.Overlap = 0;
        // Without the correction the pivot is steered on the line: a rear tool's line.
        config.Tool.IsToolFrontFixed = frontCorrection;
        config.Tool.IsToolRearFixed = !frontCorrection;
        config.Tool.IsToolTrailing = false;
        config.Tool.IsToolTBT = false;
        config.Vehicle.ThreePointHitchLength = config.Vehicle.FrontHitchLength = m.Reach;
        config.Tool.WorkingPointFromHitch = 0;
        config.Guidance.UTurnRadius = 8;

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
                    new Geometry.PolygonOffsetService(), config),
                new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, config),
                NullLogger<YouTurnStateMachine>.Instance, config),
            Substitute.For<IAudioService>(),
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

    private void TearDownRun()
    {
        _pipeline.Stop();
        _gpsService.Stop();
    }

    private const double Lead = 80, Between = 60, Tail = 80;

    /// <summary>North <see cref="Lead"/> m, a quarter bend right of radius r, east
    /// <see cref="Between"/> m, a quarter bend left, north <see cref="Tail"/> m; a point every 0.5 m.
    /// Also where each bend starts and ends along the line.</summary>
    private static (List<Vec3> Line, double[] Marks) Curve(double r)
    {
        var pts = new List<Vec3>();
        double e = 0, n = 0, h = 0;
        void Straight(double len)
        {
            int k = (int)Math.Round(len / 0.5);
            for (int i = 0; i < k; i++) { e += Math.Sin(h) * 0.5; n += Math.Cos(h) * 0.5; pts.Add(new Vec3(e, n, h)); }
        }
        void Bend(double sign)
        {
            double len = r * Math.PI / 2;
            int k = Math.Max(4, (int)Math.Round(len / 0.5));
            double ds = len / k;
            for (int i = 0; i < k; i++)
            {
                h += sign * ds / r;
                e += Math.Sin(h - sign * ds / (2 * r)) * ds;
                n += Math.Cos(h - sign * ds / (2 * r)) * ds;
                pts.Add(new Vec3(e, n, h));
            }
        }
        pts.Add(new Vec3(0, 0, 0));
        Straight(Lead);
        double a0 = Lead;
        Bend(+1);
        double a1 = a0 + r * Math.PI / 2;
        Straight(Between);
        double b0 = a1 + Between;
        Bend(-1);
        double b1 = b0 + r * Math.PI / 2;
        Straight(Tail);
        return (pts, new[] { a0, a1, b0, b1 });
    }

    /// <summary>Signed distance from the line (+ right of it, in its own direction) and how far along it.</summary>
    private static (double Off, double Along) Project(List<Vec3> line, double[] cum, double e, double n)
    {
        double best = double.MaxValue, off = 0, along = 0;
        for (int i = 1; i < line.Count; i++)
        {
            double ax = line[i - 1].Easting, ay = line[i - 1].Northing;
            double dx = line[i].Easting - ax, dy = line[i].Northing - ay;
            double l2 = dx * dx + dy * dy;
            double t = l2 < 1e-12 ? 0 : Math.Clamp(((e - ax) * dx + (n - ay) * dy) / l2, 0, 1);
            double px = ax + t * dx, py = ay + t * dy;
            double d2 = (e - px) * (e - px) + (n - py) * (n - py);
            if (d2 < best)
            {
                best = d2;
                double cross = dx * (n - ay) - dy * (e - ax); // left of the stretch is +
                off = -Math.Sign(cross) * Math.Sqrt(d2);
                along = cum[i - 1] + t * Math.Sqrt(l2);
            }
        }
        return (off, along);
    }

    private sealed record Run(double ArcMean, double ArcWorst, double EntryWorst, double ExitWorst, double PivotArcMean, bool Off, string? Why,
        double SteerJitter = 0, double JoinPast = 0, double JoinLeft = 0);

    private Run Drive(Machine m, Wheels w, double r, bool correction, Action<double, double, double, double, double>? trace = null,
        bool follower = true, double posNoise = 0, double headNoiseDeg = 0, double startRight = 0)
    {
        Build(m, correction);
        _pipeline.FrontToolFollowerEnabled = follower;
        try
        {
            var (line, marks) = Curve(r);
            var cum = new double[line.Count];
            for (int i = 1; i < line.Count; i++) cum[i] = cum[i - 1] + GeometryMath.Distance(line[i - 1], line[i]);
            _pipeline.SetActiveTrack(new Models.Track.Track
            {
                Name = "S",
                Points = line,
                Type = Models.Track.TrackType.Curve,
                IsClosed = false,
                NoPassOffset = true, // the line is the pass: no half width beside it
            }, passNumber: 0, nudgeOffset: 0);
            _pipeline.SetAutoSteerEngaged(true);

            double v = SpeedKmh / 3.6, e = startRight, n = 5, h = 0, wheel = 0;
            var rnd = new Random(7);
            double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
            double lastAsk = double.NaN, jitterSum = 0; int jitterN = 0;
            double maxSteer = m.MaxSteerDeg;
            var asked = new Queue<double>();
            int lateCycles = (int)Math.Round(w.LateS / Dt);
            var arc = new List<double>();
            double arcWorst = 0, entryWorst = 0, exitWorst = 0, pivotSum = 0, joinPast = 0, joinLeft = 0;
            int pivotN = 0;
            double end = marks[3] + Tail - 15;
            for (int i = 0; i < 4000; i++)
            {
                double me = e + posNoise * Gauss(), mn = n + posNoise * Gauss(), mh = h + headNoiseDeg * Math.PI / 180 * Gauss();
                var wgs = _plane.ConvertGeoCoordToWgs84(new GeoCoord(mn, me));
                _gpsService.UpdateGpsData(new GpsData
                {
                    CurrentPosition = new Position
                    {
                        Latitude = wgs.Latitude, Longitude = wgs.Longitude,
                        Heading = mh * 180.0 / Math.PI, Speed = v,
                    },
                    FixQuality = 4,
                    IsValid = true,
                });
                var res = _results[^1];
                if (res.AutoSteerDisengagedThisCycle) return new Run(0, 0, 0, 0, 0, true, res.DisengageReason);
                double ask = Math.Clamp(res.Guidance!.SteerAngle, -maxSteer, maxSteer);
                if (i > 100 && n < Lead - 10 && !double.IsNaN(lastAsk)) { jitterSum += (ask - lastAsk) * (ask - lastAsk); jitterN++; }
                lastAsk = ask;
                asked.Enqueue(ask);
                double target = asked.Count > lateCycles ? asked.Dequeue() : 0;
                // Ten steps a cycle: the wheels turn towards what was asked at their rate.
                for (int k = 0; k < 10; k++)
                {
                    double dt = Dt / 10, maxStep = w.RateDegS * dt;
                    wheel += Math.Clamp(target - wheel, -maxStep, maxStep);
                    double step = v * dt;
                    h += step / m.Wheelbase * Math.Tan(wheel * Math.PI / 180.0);
                    e += Math.Sin(h) * step;
                    n += Math.Cos(h) * step;
                }
                double ie = e + Math.Sin(h) * m.Reach, inn = n + Math.Cos(h) * m.Reach;
                var (off, along) = Project(line, cum, ie, inn);
                if (along > end) break;
                trace?.Invoke(along, off, e, n, h);
                if (along < Lead - 10) { joinPast = Math.Max(joinPast, -off); if (along > Lead - 25) joinLeft = Math.Max(joinLeft, Math.Abs(off)); }
                if (along < 30) continue; // settled on the first straight
                bool inA = along > marks[0] + 0.25 * (marks[1] - marks[0]) && along < marks[1] - 0.25 * (marks[1] - marks[0]);
                bool inB = along > marks[2] + 0.25 * (marks[3] - marks[2]) && along < marks[3] - 0.25 * (marks[3] - marks[2]);
                // Off is + right of the line; on the right bend the outside is the left, on the left
                // bend the right: count + as outside the bend.
                if (inA) { arc.Add(-off); arcWorst = Math.Max(arcWorst, Math.Abs(off)); }
                if (inB) { arc.Add(off); arcWorst = Math.Max(arcWorst, Math.Abs(off)); }
                if (inA || inB)
                {
                    var (po, _) = Project(line, cum, e, n);
                    pivotSum += inA ? -po : po;
                    pivotN++;
                }
                foreach (var mk in new[] { marks[0], marks[2] })
                    if (Math.Abs(along - mk) < 4 * m.Reach) entryWorst = Math.Max(entryWorst, Math.Abs(off));
                foreach (var mk in new[] { marks[1], marks[3] })
                    if (Math.Abs(along - mk) < 4 * m.Reach) exitWorst = Math.Max(exitWorst, Math.Abs(off));
            }
            return new Run(arc.Count > 0 ? arc.Average() : double.NaN, arcWorst, entryWorst, exitWorst,
                pivotN > 0 ? pivotSum / pivotN : double.NaN, false, null, jitterN > 0 ? Math.Sqrt(jitterSum / jitterN) : 0, joinPast, joinLeft);
        }
        finally { TearDownRun(); }
    }

    /// <summary>Along the line round the first bend's entry and exit with Pure Pursuit on the
    /// line steered (the follower off): the implement off the line, the pivot off the line steered
    /// and its heading against that line's. Where what is left came from.</summary>
    [Test, Explicit("Prints a trace")]
    public void Trace()
    {
        var m = Machines[0];
        const double r = 20;
        var (line, marks) = Curve(r);
        var steered = FrontToolLine.Steered(line, false, m.Reach, m.Wheelbase / Math.Tan(m.MaxSteerDeg * Math.PI / 180))!;
        var cumS = new double[steered.Count];
        for (int i = 1; i < steered.Count; i++) cumS[i] = cumS[i - 1] + GeometryMath.Distance(steered[i - 1], steered[i]);
        var sb = new StringBuilder();
        sb.AppendLine($"{m.Name}, R {r} m; bend from {marks[0]:F1} to {marks[1]:F1} m along");
        sb.AppendLine(" impl along | impl off | pivot off steered | heading err deg | h*sin(err)");
        int k = 0;
        Drive(m, Steerings[0], r, true, follower: false, trace: (along, off, e, n, h) =>
        {
            if (k++ % 2 != 0) return;
            if (!(Math.Abs(along - marks[0]) < 20 || Math.Abs(along - marks[1]) < 20)) return;
            var (po, pa) = Project(steered, cumS, e, n);
            int j = Array.FindIndex(cumS, c => c >= pa);
            j = Math.Clamp(j, 1, steered.Count - 1);
            double lh = Math.Atan2(steered[j].Easting - steered[j - 1].Easting, steered[j].Northing - steered[j - 1].Northing);
            double err = Math.IEEERemainder(h - lh, 2 * Math.PI);
            sb.AppendLine($" {along,9:F1} | {off,8:F3} | {po,17:F3} | {err * 180 / Math.PI,15:F2} | {m.Reach * Math.Sin(err),8:F3}");
        });
        TestContext.Out.WriteLine(sb.ToString());
    }

    /// <summary>The same with a receiver's noise (position and heading): how much the wheels are
    /// asked to move from one fix to the next on the straight, and the implement at the bends.</summary>
    [Test, Explicit("Prints a table")]
    public void Noise()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Noise: position sd / heading sd | steered by | R m | entry / exit worst m | steer change per fix on the straight, deg (rms)");
        foreach (var m in Machines)
            foreach (var (pn, hn) in new[] { (0.0, 0.0), (0.01, 0.2), (0.02, 0.5) })
                foreach (var r in new[] { 20.0, 10 })
                    foreach (var f in new[] { false, true })
                    {
                        var x = Drive(m, Steerings[1], r, true, null, f, pn, hn);
                        sb.AppendLine($"{m.Name,-24} {pn * 100,3:F0} cm / {hn,3:F1} deg | {(f ? "follower" : "pure pursuit"),-12} | {r,3:F0} | {x.EntryWorst,4:F2} / {x.ExitWorst,4:F2} | {x.SteerJitter,5:F2}");
                    }
        TestContext.Out.WriteLine(sb.ToString());
    }

    [Test]
    public void A_front_boom_stays_on_the_pass_where_a_bend_starts_and_ends()
    {
        var puma = Machines[0];
        var before = Drive(puma, Steerings[1], 20, true, follower: false);
        var now = Drive(puma, Steerings[1], 20, true);
        TestContext.Out.WriteLine($"entry / exit: Pure Pursuit {before.EntryWorst:F3} / {before.ExitWorst:F3}, follower {now.EntryWorst:F3} / {now.ExitWorst:F3} m");
        Assert.Multiple(() =>
        {
            Assert.That(before.Off || now.Off, Is.False, before.Why ?? now.Why);
            Assert.That(Math.Max(before.EntryWorst, before.ExitWorst), Is.GreaterThan(0.08), "what Pure Pursuit left");
            Assert.That(Math.Max(now.EntryWorst, now.ExitWorst), Is.LessThan(0.04));
            Assert.That(Math.Abs(now.ArcMean), Is.LessThan(0.02), "the middle of the bend on the pass");
        });
    }

    [Test]
    public void A_combine_header_stays_on_the_pass_round_a_tight_bend()
    {
        var combine = Machines[1];
        var before = Drive(combine, Steerings[0], 10, true, follower: false);
        var now = Drive(combine, Steerings[0], 10, true);
        TestContext.Out.WriteLine($"entry / exit: Pure Pursuit {before.EntryWorst:F3} / {before.ExitWorst:F3}, follower {now.EntryWorst:F3} / {now.ExitWorst:F3} m");
        Assert.Multiple(() =>
        {
            Assert.That(before.Off || now.Off, Is.False, before.Why ?? now.Why);
            Assert.That(Math.Max(before.EntryWorst, before.ExitWorst), Is.GreaterThan(0.2), "what Pure Pursuit left");
            Assert.That(Math.Max(now.EntryWorst, now.ExitWorst), Is.LessThan(0.12));
        });
    }

    [Test]
    public void With_a_receivers_noise_the_wheels_move_no_more_than_with_Pure_Pursuit()
    {
        var puma = Machines[0];
        var before = Drive(puma, Steerings[1], 20, true, null, follower: false, posNoise: 0.02, headNoiseDeg: 0.5);
        var now = Drive(puma, Steerings[1], 20, true, null, follower: true, posNoise: 0.02, headNoiseDeg: 0.5);
        TestContext.Out.WriteLine($"steer change per fix (rms): Pure Pursuit {before.SteerJitter:F2}, follower {now.SteerJitter:F2} deg");
        Assert.That(now.SteerJitter, Is.LessThanOrEqualTo(before.SteerJitter * 1.1));
        Assert.That(Math.Max(now.EntryWorst, now.ExitWorst), Is.LessThan(Math.Max(before.EntryWorst, before.ExitWorst)));
    }

    [Test]
    public void Joining_the_line_from_the_side_is_as_before()
    {
        // Engaged 2 m right of the line: Pure Pursuit brings the tractor in, the follower takes
        // over within a metre of it. The implement does not swing further past the line, and is
        // on it before the bend.
        var puma = Machines[0];
        var before = Drive(puma, Steerings[1], 20, true, follower: false, startRight: 2);
        var now = Drive(puma, Steerings[1], 20, true, startRight: 2);
        TestContext.Out.WriteLine($"past the line joining: Pure Pursuit {before.JoinPast:F3}, follower {now.JoinPast:F3} m; "
            + $"off it 15-25 m before the bend: {before.JoinLeft:F3}, {now.JoinLeft:F3} m");
        Assert.Multiple(() =>
        {
            Assert.That(before.Off || now.Off, Is.False, before.Why ?? now.Why);
            Assert.That(now.JoinPast, Is.LessThanOrEqualTo(before.JoinPast + 0.02));
            Assert.That(now.JoinLeft, Is.LessThan(0.03));
        });
    }

    [Test, Explicit("Prints a table")]
    public void Table()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Implement centre off the line, m (+ = outside the bend). Arc: middle half of each bend.");
        sb.AppendLine("Entry / exit: worst within 4 reaches of where a bend starts / ends.");
        foreach (var m in Machines)
        {
            sb.AppendLine();
            sb.AppendLine($"{m.Name}: wheelbase {m.Wheelbase:F2} m, reach {m.Reach:F2} m, max steer {m.MaxSteerDeg:F1} deg (turning radius {m.Wheelbase / Math.Tan(m.MaxSteerDeg * Math.PI / 180):F1} m), {SpeedKmh} km/h");
            foreach (var w in Steerings)
            {
                sb.AppendLine($"  wheels {w.Name}");
                sb.AppendLine("    R m | geometry | without: arc mean / worst | Pure Pursuit on the line moved: arc mean / entry / exit | with the follower: arc mean / arc worst / entry / exit");
                foreach (var r in new[] { 50.0, 30, 20, 15, 10, 8 })
                {
                    double geom = Math.Sqrt(r * r + m.Reach * m.Reach) - r;
                    var a = Drive(m, w, r, correction: false);
                    var b = Drive(m, w, r, correction: true, follower: false);
                    var c = Drive(m, w, r, correction: true);
                    string sa = a.Off ? "AutoSteer off: " + a.Why : $"{a.ArcMean,5:F2} / {a.ArcWorst,4:F2}";
                    string sbb = b.Off ? "AutoSteer off: " + b.Why : $"{b.ArcMean,5:F2} / {b.EntryWorst,4:F2} / {b.ExitWorst,4:F2}";
                    string sc = c.Off ? "AutoSteer off: " + c.Why : $"{c.ArcMean,5:F2} / {c.ArcWorst,4:F2} / {c.EntryWorst,4:F2} / {c.ExitWorst,4:F2}";
                    sb.AppendLine($"    {r,3:F0} | {geom,5:F2}    | {sa,-25} | {sbb,-55} | {sc}");
                }
            }
        }
        TestContext.Out.WriteLine(sb.ToString());
    }
}
