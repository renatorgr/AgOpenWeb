// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using AgOpenWeb.Models.GPS;
using AgOpenWeb.Services.Implement;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// A GPS on a trailed implement (fork only): each of its fixes put beside the tractor's fix of the same receiver time
/// (<see cref="ImplementGpsTracker"/>), the file of the day (<see cref="ImplementGpsLog"/>) and the datagrams read
/// (<see cref="ImplementGpsReceiver"/>).
/// </summary>
[TestFixture]
public class ImplementGpsTrackerTests
{
    private static readonly ImplementGpsFitting Fitting = new(2.55, 2.2, 0, false, 0, true);

    // the plane: a degree of longitude a metre east, of latitude a metre north (the numbers only have to be told apart)
    private static (double E, double N)? Plane(double lat, double lon) => (lon, lat);

    // the tractor driving north at 2 m/s: its pin at n = 2 * seconds of its time, the model's drill straight behind
    private static TractorEpoch Tractor(int timeCs)
    {
        double n = timeCs < 0 ? 0 : 2.0 * timeCs / 100.0;
        return new TractorEpoch(timeCs, 0, n, 0, n - 2.55, 0, 0, n + 1.54, 0, 7.2, 0.5, 1.5, 0.02, true, "3R", 0, 0);
    }

    // the implement's antenna 3.3 m behind where the pin is at that time, slideE to the right
    private static ImplementGpsFix Implement(int timeCs, double slideE = 0) =>
        new(timeCs, 2.0 * Math.Max(timeCs, 0) / 100.0 - 3.3, slideE, 4, 20, 0.6, 80, 1.0, 3.9, double.NaN, false, double.NaN, false);

    [Test]
    public void A_fix_is_put_beside_the_tractors_fix_of_the_same_time_not_the_latest()
    {
        var t = new ImplementGpsTracker();
        for (int cs = 1000; cs <= 1050; cs += 10) t.AddTractor(Tractor(cs));
        t.AddImplement(Implement(1020), 0);
        var got = new List<ImplementGpsSample>();
        t.Drain(5, Fitting, Plane, got);
        Assert.That(got, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(got[0].Paired, Is.True);
            Assert.That(got[0].Tractor.TimeCentiseconds, Is.EqualTo(1020));
            Assert.That(got[0].Pose!.Value.AcrossM, Is.EqualTo(0).Within(1e-9));
            // beside the latest fix (0.3 s on at 2 m/s) the drill would read 0.6 m behind the model's
            Assert.That(got[0].Pose!.Value.AlongM, Is.EqualTo(0).Within(1e-9));
        });
    }

    [Test]
    public void A_fix_that_comes_before_its_tractor_fix_waits_for_it()
    {
        var t = new ImplementGpsTracker();
        t.AddTractor(Tractor(1000));
        t.AddImplement(Implement(1010), 100);
        var got = new List<ImplementGpsSample>();
        t.Drain(120, Fitting, Plane, got);
        Assert.That(got, Is.Empty, "the tractor's 10.10 is still on its way");
        t.AddTractor(Tractor(1010));
        t.Drain(180, Fitting, Plane, got);
        Assert.That(got, Has.Count.EqualTo(1));
        Assert.That(got[0].Paired, Is.True);
    }

    [Test]
    public void With_no_tractor_fix_of_its_time_the_nearest_is_used_and_said()
    {
        var t = new ImplementGpsTracker();
        t.AddTractor(Tractor(1000));
        t.AddTractor(Tractor(1020));   // the tractor's 10.10 was lost
        t.AddImplement(Implement(1010), 0);
        var got = new List<ImplementGpsSample>();
        t.Drain(10, Fitting, Plane, got);
        Assert.That(got, Has.Count.EqualTo(1), "the tractor is past that time: no use waiting");
        Assert.Multiple(() =>
        {
            Assert.That(got[0].Paired, Is.False);
            Assert.That(Math.Abs(got[0].TimeDiffCentiseconds), Is.EqualTo(10));
        });

        // a receiver whose clock is a second ahead: waited for, then the nearest
        t.AddImplement(Implement(1120), 1000);
        got.Clear();
        t.Drain(1100, Fitting, Plane, got);
        Assert.That(got, Is.Empty);
        t.Drain(1000 + ImplementGpsTracker.HoldMs + 1, Fitting, Plane, got);
        Assert.That(got, Has.Count.EqualTo(1));
        Assert.That(got[0].Paired, Is.False);
        Assert.That(got[0].TimeDiffCentiseconds, Is.EqualTo(100));
    }

    [Test]
    public void A_tractor_with_no_time_on_its_fix_gets_the_latest_unpaired()
    {
        // the simulator's fixes carry no time
        var t = new ImplementGpsTracker();
        t.AddTractor(Tractor(-1));
        t.AddImplement(Implement(1010), 0);
        var got = new List<ImplementGpsSample>();
        t.Drain(1, Fitting, Plane, got);
        Assert.That(got, Has.Count.EqualTo(1));
        Assert.That(got[0].Paired, Is.False);
    }

    [Test]
    public void With_no_tractor_fix_at_all_a_fix_is_dropped_after_the_wait()
    {
        var t = new ImplementGpsTracker();
        t.AddImplement(Implement(1010), 0);
        var got = new List<ImplementGpsSample>();
        t.Drain(100, Fitting, Plane, got);
        t.Drain(ImplementGpsTracker.HoldMs + 1, Fitting, Plane, got);
        Assert.That(got, Is.Empty);
        Assert.That(t.WithoutTractor, Is.EqualTo(1));
    }

    [Test]
    public void The_same_time_holds_across_midnight()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ImplementGpsTracker.DiffCs(5, 8_640_000 - 5), Is.EqualTo(10));
            Assert.That(ImplementGpsTracker.DiffCs(8_640_000 - 5, 5), Is.EqualTo(-10));
        });
    }

    [Test]
    public void The_slide_and_the_roll_setup_reach_the_pose()
    {
        var t = Tractor(1000);
        var slid = ImplementGpsTracker.Solve(Implement(1000, 0.30), t, true, 0, Fitting, Plane);
        Assert.That(slid.Pose!.Value.AcrossM, Is.EqualTo(0.231).Within(0.001));

        // the board turned round in its box and reading 1.5 on level ground: 3.5 as sent is 5 to the right
        var fit = Fitting with { RollInvert = true, RollZero = -8.5 };
        Assert.That(fit.RollUsed(3.5), Is.EqualTo(5).Within(1e-9));
        Assert.That(double.IsNaN(fit.RollUsed(double.NaN)), Is.True);

        // not a trailing tool: heard and written, no pose
        var rigid = ImplementGpsTracker.Solve(Implement(1000), t, true, 0, Fitting with { Trailed = false }, Plane);
        Assert.That(rigid.Pose, Is.Null);
        // no plane yet
        var noPlane = ImplementGpsTracker.Solve(Implement(1000), t, true, 0, Fitting, (_, _) => null);
        Assert.That(noPlane.Pose, Is.Null);
    }

    [Test]
    public void The_Offset_Fix_moves_the_implements_position_as_the_tractors()
    {
        var t = Tractor(1000) with { DriftE = 0.4, DriftN = -0.2 };
        var s = ImplementGpsTracker.Solve(Implement(1000), t, true, 0, Fitting, Plane);
        Assert.That(s.AntennaE, Is.EqualTo(0.4).Within(1e-9));
        Assert.That(s.AntennaN, Is.EqualTo(20 - 3.3 - 0.2).Within(1e-9));
    }

    [Test]
    public void A_row_has_a_value_for_every_column_with_a_point_whatever_the_language()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("pt-PT");   // a comma for the decimals
            var s = ImplementGpsTracker.Solve(Implement(1000, 0.30), Tractor(1000), true, 0, Fitting, Plane);
            string row = ImplementGpsLog.Row(new DateTime(2026, 11, 27, 10, 15, 30, 400, DateTimeKind.Utc), s, Fitting);
            string[] cols = ImplementGpsLog.Header.Split(','), vals = row.Split(',');
            Assert.That(vals, Has.Length.EqualTo(cols.Length), row);
            string V(string name) => vals[Array.IndexOf(cols, name)];
            Assert.Multiple(() =>
            {
                Assert.That(V("tablet_utc"), Is.EqualTo("2026-11-27T10:15:30.400Z"));
                Assert.That(V("tractor_time"), Is.EqualTo("00:00:10.00"));
                Assert.That(V("paired"), Is.EqualTo("1"));
                Assert.That(V("across_cm"), Is.EqualTo("23.1"));   // 2.55 m of the 0.30 m at 3.3 m
                Assert.That(V("pass"), Is.EqualTo("3R"));
                Assert.That(V("xte_cm"), Is.EqualTo("2.0"));
                Assert.That(V("behind_pin"), Is.EqualTo("2.550"));
                Assert.That(V("impl_roll_raw"), Is.Empty, "no roll sent: an empty cell, not NaN");
            });

            // no pose: the same columns, the solved ones empty
            var none = ImplementGpsTracker.Solve(Implement(1000), Tractor(1000), false, 0, Fitting with { Trailed = false }, Plane);
            string[] nv = ImplementGpsLog.Row(DateTime.UtcNow, none, Fitting).Split(',');
            Assert.That(nv, Has.Length.EqualTo(cols.Length));
            Assert.That(nv[Array.IndexOf(cols, "across_cm")], Is.Empty);
            Assert.That(nv[Array.IndexOf(cols, "model_work_n")], Is.Not.Empty);
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Test]
    public void The_file_of_the_day_gets_one_header_and_the_rows()
    {
        string root = Path.Combine(Path.GetTempPath(), "aow-implgps-" + Guid.NewGuid().ToString("N"));
        try
        {
            var day = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
            using (var log = new ImplementGpsLog(root))
            {
                log.Add(day, "a,1");
                log.Add(day.AddSeconds(1), "b,2");
            }
            using (var again = new ImplementGpsLog(root)) again.Add(day.AddSeconds(2), "c,3");   // the app started again
            string[] files = Directory.GetFiles(Path.Combine(root, ImplementGpsLog.FolderName));
            Assert.That(files, Has.Length.EqualTo(1));
            Assert.That(Path.GetFileName(files[0]), Does.StartWith("ImplementGps_2026-11-2").And.EndsWith(".csv"));
            Assert.That(File.ReadAllLines(files[0]), Is.EqualTo(new[] { ImplementGpsLog.Header, "a,1", "b,2", "c,3" }));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Test]
    public void A_datagram_is_read_line_by_line_and_what_is_not_a_position_is_counted()
    {
        static string S(string body)
        {
            byte sum = 0;
            foreach (char c in body) sum ^= (byte)c;
            return "$" + body + "*" + sum.ToString("X2", CultureInfo.InvariantCulture) + "\r\n";
        }
        using var r = new ImplementGpsReceiver();   // not started: no port opened
        string good = S("PAOGI,101530.40,3907.2345678,N,00851.6789012,W,4,30,0.5,85.1,0.8,3.9,271.35,-1.72,0.4,0.0");
        r.Take(Encoding.ASCII.GetBytes(good + S("GNVTG,1.0,T,,M,3.9,N,7.2,K,D") + good), 42);
        Assert.Multiple(() =>
        {
            Assert.That(r.Sentences, Is.EqualTo(3));
            Assert.That(r.Refused, Is.EqualTo(1));
            Assert.That(r.LastType, Is.EqualTo("PAOGI"));
            Assert.That(r.TryTake(out var f, out long at), Is.True);
            Assert.That(at, Is.EqualTo(42));
            Assert.That(f.Roll, Is.EqualTo(-1.72).Within(1e-9));
            Assert.That(r.TryTake(out _, out _), Is.True);
            Assert.That(r.TryTake(out _, out _), Is.False);
        });
    }

    [Test]
    public void The_port_is_heard_on_the_loopback()
    {
        using var r = new ImplementGpsReceiver();
        if (!r.Start()) Assert.Ignore("UDP 9990 is in use on this machine: " + r.Error);
        byte sum = 0;
        const string body = "PANDA,101530.40,3907.2345678,N,00851.6789012,W,4,22,0.7,85.123,1.2,4.321,1234,-25,3,0";
        foreach (char c in body) sum ^= (byte)c;
        byte[] d = Encoding.ASCII.GetBytes("$" + body + "*" + sum.ToString("X2", CultureInfo.InvariantCulture) + "\r\n");
        using var send = new System.Net.Sockets.UdpClient();
        ImplementGpsFix fix = default;
        bool got = false;
        for (int i = 0; i < 50 && !got; i++)
        {
            send.Send(d, d.Length, "127.0.0.1", ImplementGpsReceiver.Port);
            Thread.Sleep(20);
            got = r.TryTake(out fix, out _);
        }
        Assert.That(got, Is.True);
        Assert.That(fix.Heading, Is.EqualTo(123.4).Within(1e-9));
        Assert.That(r.Sender, Is.EqualTo("127.0.0.1"));
    }
}
