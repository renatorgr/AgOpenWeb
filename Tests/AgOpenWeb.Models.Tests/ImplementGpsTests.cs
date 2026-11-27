// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Text;
using AgOpenWeb.Models.GPS;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// A GPS on a trailed implement (fork only): its sentences read (<see cref="ImplementGpsSentence"/>) and where the
/// implement is worked out from its antenna and the tractor's pin (<see cref="ImplementGpsGeometry"/>). The numbers are
/// a tester's drill: discs 2.55 m behind the pin, the antennas on a rail 3.3 m behind it, 3 m apart, 2.2 m up.
/// </summary>
[TestFixture]
public class ImplementGpsTests
{
    private const double BehindPin = 2.55, Rail = 3.3, Height = 2.2;

    private static byte[] Sentence(string body)
    {
        byte sum = 0;
        foreach (char c in body) sum ^= (byte)c;
        return Encoding.ASCII.GetBytes("$" + body + "*" + sum.ToString("X2", CultureInfo.InvariantCulture) + "\r\n");
    }

    [Test]
    public void A_PANDA_is_read_with_its_time_position_and_the_IMU_in_tenths()
    {
        // 39 deg 07.2345 N, 8 deg 51.6789 W; heading 123.4, roll -2.5 (tenths)
        var ok = ImplementGpsSentence.TryParse(
            Sentence("PANDA,101530.40,3907.2345678,N,00851.6789012,W,4,22,0.7,85.123,1.2,4.321,1234,-25,3,0"), out var f);
        Assert.That(ok, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(f.TimeCentiseconds, Is.EqualTo(10 * 360000 + 15 * 6000 + 3040));
            Assert.That(f.Latitude, Is.EqualTo(39 + 7.2345678 / 60).Within(1e-10));
            Assert.That(f.Longitude, Is.EqualTo(-(8 + 51.6789012 / 60)).Within(1e-10));
            Assert.That(f.FixQuality, Is.EqualTo(4));
            Assert.That(f.Satellites, Is.EqualTo(22));
            Assert.That(f.Age, Is.EqualTo(1.2).Within(1e-9));
            Assert.That(f.SpeedKnots, Is.EqualTo(4.321).Within(1e-9));
            Assert.That(f.Heading, Is.EqualTo(123.4).Within(1e-9));
            Assert.That(f.Roll, Is.EqualTo(-2.5).Within(1e-9));
            Assert.That(f.IsPaogi, Is.False);
            Assert.That(f.HasDualHeading, Is.False);
        });
    }

    [Test]
    public void A_PANDA_with_no_IMU_has_no_heading_and_no_roll()
    {
        Assert.That(ImplementGpsSentence.TryParse(
            Sentence("PANDA,101530.40,3907.2345678,N,00851.6789012,W,4,22,0.7,85.123,1.2,4.321,65535,0,0,0"), out var f), Is.True);
        Assert.That(double.IsNaN(f.Heading) && double.IsNaN(f.Roll), Is.True);
    }

    [Test]
    public void A_PAOGI_carries_the_antennas_heading_and_roll_in_degrees()
    {
        Assert.That(ImplementGpsSentence.TryParse(
            Sentence("PAOGI,235959.90,3907.2345678,N,00851.6789012,W,4,30,0.5,85.1,0.8,3.9,271.35,-1.72,0.4,0.0"), out var f), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(f.IsPaogi && f.HasDualHeading, Is.True);
            Assert.That(f.Heading, Is.EqualTo(271.35).Within(1e-9));
            Assert.That(f.Roll, Is.EqualTo(-1.72).Within(1e-9));
            Assert.That(f.TimeCentiseconds, Is.EqualTo(8_640_000 - 10));
        });
    }

    [Test]
    public void Anything_else_is_refused()
    {
        var good = Sentence("PANDA,101530.40,3907.2345678,N,00851.6789012,W,4,22,0.7,85.123,1.2,4.321,1234,-25,3,0");
        var badSum = (byte[])good.Clone();
        badSum[10] ^= 1;
        Assert.Multiple(() =>
        {
            Assert.That(ImplementGpsSentence.TryParse(badSum, out _), Is.False, "a damaged sentence");
            Assert.That(ImplementGpsSentence.TryParse(Sentence("GNGGA,101530.40,3907.2345678,N,00851.6789012,W,4,22,0.7,85.123,M,50,M,1.2,0"), out _), Is.False, "another sentence");
            Assert.That(ImplementGpsSentence.TryParse(Sentence("PANDA,101530.40,,N,,W,0,0,,,,,65535,0,0,0"), out _), Is.False, "no position");
            Assert.That(ImplementGpsSentence.TryParse(new byte[] { 0x80, 0x81, 0x7F, 0xC8, 3, 56, 0, 0, 0x47 }, out _), Is.False, "a PGN");
            Assert.That(ImplementGpsSentence.TryParse(ReadOnlySpan<byte>.Empty, out _), Is.False, "nothing");
        });
    }

    // The tractor heading north, its pin at the origin; the model's drill straight behind it.
    private static ImplementGpsPose Solve(double antennaE, double antennaN, double roll = double.NaN, double antennaRight = 0,
        double receiverHeading = double.NaN) =>
        ImplementGpsGeometry.Solve(antennaE, antennaN, roll, receiverHeading, 0, 0, BehindPin, Height, antennaRight,
            0, -BehindPin, 0)!.Value;

    [Test]
    public void Straight_behind_the_pin_the_real_drill_is_the_models()
    {
        var p = Solve(0, -Rail);
        Assert.Multiple(() =>
        {
            Assert.That(p.AcrossM, Is.EqualTo(0).Within(1e-9));
            Assert.That(p.AlongM, Is.EqualTo(0).Within(1e-9));
            Assert.That(p.HeadingDiffDeg, Is.EqualTo(0).Within(1e-9));
            Assert.That(p.WorkN, Is.EqualTo(-BehindPin).Within(1e-9));
        });
    }

    [Test]
    public void A_drill_slid_downhill_is_across_by_its_share_of_the_slide()
    {
        // the rail 0.30 m right of the tractor's line: the drill turned about the pin, its discs 2.55 / 3.3 of that across
        double angle = Math.Asin(0.30 / Rail);
        var p = Solve(0.30, -Rail * Math.Cos(angle));
        Assert.Multiple(() =>
        {
            Assert.That(p.AcrossM, Is.EqualTo(BehindPin * Math.Sin(angle)).Within(1e-6));
            Assert.That(p.AcrossM, Is.EqualTo(0.232).Within(0.001));
            Assert.That(p.HeadingDiffDeg, Is.EqualTo(-angle * 180 / Math.PI).Within(1e-6), "its nose points left when its tail is right");
            Assert.That(p.AlongM, Is.GreaterThan(0).And.LessThan(0.02));
        });
    }

    [Test]
    public void The_antennas_place_on_the_rail_is_taken_out()
    {
        // a dual's first antenna 1.5 m right of the centre line, the drill straight behind
        var p = Solve(1.5, -Rail, antennaRight: 1.5);
        Assert.That(p.AcrossM, Is.EqualTo(0).Within(1e-6));
        Assert.That(p.CentreE, Is.EqualTo(0).Within(1e-6));
        // ... and the receiver's own heading agrees with the line to the pin
        var q = Solve(1.5, -Rail, antennaRight: 1.5, receiverHeading: 359.2);
        Assert.That(q.ReceiverHeadingDiffDeg, Is.EqualTo(-0.8).Within(1e-6));
    }

    [Test]
    public void A_roll_to_the_right_carries_a_high_antenna_right_and_is_taken_out()
    {
        // 5 degrees to the right at 2.2 m: the antenna 0.19 m right of the ground under it
        double lean = Math.Sin(5 * Math.PI / 180) * Height;
        var leaning = Solve(lean, -Rail, roll: 5);
        var ignored = Solve(lean, -Rail);
        Assert.Multiple(() =>
        {
            Assert.That(lean, Is.EqualTo(0.192).Within(0.001));
            Assert.That(leaning.AcrossM, Is.EqualTo(0).Within(1e-6), "with the roll");
            Assert.That(ignored.AcrossM, Is.EqualTo(0.148).Within(0.002), "without it the lean reads as a slide");
        });
    }

    [Test]
    public void It_holds_on_any_heading()
    {
        // the tractor heading 250 degrees: the same slide, turned
        double h = 250 * Math.PI / 180, slide = 0.30, angle = Math.Asin(slide / Rail);
        double fe = Math.Sin(h), fn = Math.Cos(h), re = Math.Cos(h), rn = -Math.Sin(h);
        double back = Rail * Math.Cos(angle);
        var p = ImplementGpsGeometry.Solve(-fe * back + re * slide, -fn * back + rn * slide, double.NaN, double.NaN,
            0, 0, BehindPin, Height, 0, -fe * BehindPin, -fn * BehindPin, h)!.Value;
        Assert.That(p.AcrossM, Is.EqualTo(BehindPin * Math.Sin(angle)).Within(1e-6));
        Assert.That(p.HeadingRad, Is.EqualTo(h - angle).Within(1e-9));
    }

    [Test]
    public void An_antenna_on_the_pin_gives_no_pose()
    {
        Assert.That(ImplementGpsGeometry.Solve(0.2, -0.3, double.NaN, double.NaN, 0, 0, BehindPin, Height, 0, 0, -BehindPin, 0), Is.Null);
    }
}
