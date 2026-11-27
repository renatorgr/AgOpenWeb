// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
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
        return new TractorEpoch(timeCs, 0, n, 0, n - 2.55, 0, 0, n + 1.54, 0, 7.2, 0.5, 1.5, 0.02, true, "3R", 0, 0, 4, 0);
    }

    // a $PAOGI of the implement at a time of day (hhmmss.ss) and a latitude's last digits, with its checksum
    private static string Sentence(string time, string latEnd = "5678")
    {
        string body = $"PAOGI,{time},3907.234{latEnd},N,00851.6789012,W,4,30,0.5,85.1,0.8,3.9,271.35,-1.72,0.4,0.0";
        byte sum = 0;
        foreach (char c in body) sum ^= (byte)c;
        return "$" + body + "*" + sum.ToString("X2", CultureInfo.InvariantCulture) + "\r\n";
    }
    private static byte[] Datagram(string time, string latEnd = "5678") => Encoding.ASCII.GetBytes(Sentence(time, latEnd));

    // the implement's antenna 3.3 m behind where the pin is at that time, slideE to the right
    private static ImplementGpsFix Implement(int timeCs, double slideE = 0) =>
        new(timeCs, 2.0 * Math.Max(timeCs, 0) / 100.0 - 3.3, slideE, 4, 20, 0.6, 80, 1.0, 3.9, double.NaN, false, double.NaN, false);

    [Test]
    public void A_fix_is_put_beside_the_tractors_fix_of_the_same_time_not_the_latest()
    {
        var t = new ImplementGpsTracker();
        for (int cs = 1000; cs <= 1050; cs += 10) t.AddTractor(Tractor(cs), 0);
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
        t.AddTractor(Tractor(1000), 100);
        t.AddImplement(Implement(1010), 100);
        var got = new List<ImplementGpsSample>();
        t.Drain(120, Fitting, Plane, got);
        Assert.That(got, Is.Empty, "the tractor's 10.10 is still on its way");
        t.AddTractor(Tractor(1010), 170);
        t.Drain(180, Fitting, Plane, got);
        Assert.That(got, Has.Count.EqualTo(1));
        Assert.That(got[0].Paired, Is.True);
    }

    [Test]
    public void With_no_tractor_fix_of_its_time_the_nearest_is_used_and_said()
    {
        var t = new ImplementGpsTracker();
        t.AddTractor(Tractor(1000), 0);
        t.AddTractor(Tractor(1020), 0);   // the tractor's 10.10 was lost
        t.AddImplement(Implement(1010), 0);
        var got = new List<ImplementGpsSample>();
        t.Drain(10, Fitting, Plane, got);
        Assert.That(got, Has.Count.EqualTo(1), "the tractor is past that time: no use waiting");
        Assert.Multiple(() =>
        {
            Assert.That(got[0].Paired, Is.False);
            Assert.That(Math.Abs(got[0].TimeDiffCentiseconds), Is.EqualTo(10));
            Assert.That(got[0].Pose, Is.Not.Null, "a tenth of a second off is near enough to draw");
            Assert.That(got[0].NoPose, Is.EqualTo(ImplementGpsNoPose.None));
        });
    }

    [Test]
    public void Beside_a_tractor_fix_that_is_too_old_a_fix_is_written_and_not_drawn()
    {
        // the tractor's GPS went silent at 10.20; the implement's goes on (at 7 km/h a second is 2 m: beside the
        // tractor's last place the drill would be drawn where it is not)
        var t = new ImplementGpsTracker();
        t.AddTractor(Tractor(1000), 0);
        t.AddTractor(Tractor(1020), 200);
        t.AddImplement(Implement(1120), 1200);
        var got = new List<ImplementGpsSample>();
        t.Drain(1300, Fitting, Plane, got);
        Assert.That(got, Is.Empty, "its tractor fix may still come");
        t.Drain(1200 + ImplementGpsTracker.HoldMs + 1, Fitting, Plane, got);
        Assert.That(got, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(got[0].Paired, Is.False);
            Assert.That(got[0].TimeDiffCentiseconds, Is.EqualTo(100));
            Assert.That(got[0].Pose, Is.Null);
            Assert.That(got[0].NoPose, Is.EqualTo(ImplementGpsNoPose.TractorStale));
            Assert.That(double.IsFinite(got[0].AntennaN), Is.True, "where the antenna is still goes to the file");
        });
        // just inside the limit: drawn
        t.AddImplement(Implement(1020 + ImplementGpsTracker.NearCs), 2000);
        got.Clear();
        t.Drain(2000 + ImplementGpsTracker.HoldMs + 1, Fitting, Plane, got);
        Assert.That(got[0].Pose, Is.Not.Null);
    }

    [Test]
    public void A_tractor_with_no_time_on_its_fix_gets_the_latest_unpaired_while_it_is_fresh()
    {
        // the simulator's fixes carry no time
        var t = new ImplementGpsTracker();
        t.AddTractor(Tractor(-1), 0);
        t.AddImplement(Implement(1010), 0);
        var got = new List<ImplementGpsSample>();
        t.Drain(1, Fitting, Plane, got);
        Assert.That(got, Has.Count.EqualTo(1));
        Assert.That(got[0].Paired, Is.False);
        Assert.That(got[0].Pose, Is.Not.Null);

        // nothing from the tractor for more than a second: no times to compare, so its age decides
        t.AddImplement(Implement(1020), ImplementGpsTracker.StaleMs + 10);
        got.Clear();
        t.Drain(ImplementGpsTracker.StaleMs + 11, Fitting, Plane, got);
        Assert.That(got, Has.Count.EqualTo(1));
        Assert.That(got[0].Pose, Is.Null);
        Assert.That(got[0].NoPose, Is.EqualTo(ImplementGpsNoPose.TractorStale));
    }

    [Test]
    public void With_no_tractor_fix_at_all_a_fix_is_still_written_after_the_wait()
    {
        var t = new ImplementGpsTracker();
        t.AddImplement(Implement(1010), 0);
        var got = new List<ImplementGpsSample>();
        t.Drain(100, Fitting, Plane, got);
        Assert.That(got, Is.Empty, "the tractor's first fix may be on its way");
        t.Drain(ImplementGpsTracker.HoldMs + 1, Fitting, Plane, got);
        Assert.That(got, Has.Count.EqualTo(1));
        Assert.That(t.WithoutTractor, Is.EqualTo(1));
        Assert.That(got[0].Pose, Is.Null);
        Assert.That(got[0].NoPose, Is.EqualTo(ImplementGpsNoPose.NoTractor));

        string[] cols = ImplementGpsLog.Header.Split(','), vals = ImplementGpsLog.Row(DateTime.UtcNow, got[0], Fitting).Split(',');
        string V(string name) => vals[Array.IndexOf(cols, name)];
        Assert.Multiple(() =>
        {
            Assert.That(vals, Has.Length.EqualTo(cols.Length));
            Assert.That(V("implement_time"), Is.EqualTo("00:00:10.10"));
            Assert.That(V("impl_lat"), Is.Not.Empty);
            Assert.That(V("antenna_n"), Is.Not.Empty);
            Assert.That(V("tractor_time"), Is.Empty);
            Assert.That(V("time_diff_s"), Is.Empty);
            Assert.That(V("pin_e"), Is.Empty);
            Assert.That(V("autosteer"), Is.Empty);
            Assert.That(V("tractor_fix"), Is.Empty);
            Assert.That(V("no_pose"), Is.EqualTo("no_tractor_fix"));
        });
    }

    [Test]
    public void Another_plane_forgets_the_tractors_fixes_and_keeps_the_implements()
    {
        var t = new ImplementGpsTracker();
        t.AddTractor(Tractor(1000), 0);
        t.AddImplement(Implement(1010), 0);
        t.ClearTractor();   // a field opened: the kept fixes are in the old plane's metres
        var got = new List<ImplementGpsSample>();
        t.Drain(10, Fitting, Plane, got);
        Assert.That(got, Is.Empty);
        t.AddTractor(Tractor(1010), 20);
        t.Drain(30, Fitting, Plane, got);
        Assert.That(got, Has.Count.EqualTo(1));
        Assert.That(got[0].Paired, Is.True);
    }

    [Test]
    public void Fixes_nobody_takes_out_are_counted_as_lost()
    {
        var t = new ImplementGpsTracker();
        for (int i = 0; i < 53; i++) t.AddImplement(Implement(1000 + i), 0);
        Assert.That(t.Lost, Is.EqualTo(3));
    }

    [Test]
    public void A_receiver_with_no_fix_gives_no_place()
    {
        // quality 0: the numbers are its last ones, or zeros
        var noFix = Implement(1000) with { FixQuality = 0 };
        var s = ImplementGpsTracker.Solve(noFix, Tractor(1000), true, 0, Fitting, Plane);
        Assert.Multiple(() =>
        {
            Assert.That(s.Pose, Is.Null);
            Assert.That(s.NoPose, Is.EqualTo(ImplementGpsNoPose.NoFix));
            Assert.That(double.IsNaN(s.AntennaE), Is.True);
        });
        string[] cols = ImplementGpsLog.Header.Split(','), vals = ImplementGpsLog.Row(DateTime.UtcNow, s, Fitting).Split(',');
        Assert.That(vals[Array.IndexOf(cols, "impl_fix")], Is.EqualTo("0"));
        Assert.That(vals[Array.IndexOf(cols, "antenna_e")], Is.Empty);
        Assert.That(vals[Array.IndexOf(cols, "no_pose")], Is.EqualTo("no_fix"));
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
        Assert.That(rigid.NoPose, Is.EqualTo(ImplementGpsNoPose.NotTrailed));
        // no plane yet
        var noPlane = ImplementGpsTracker.Solve(Implement(1000), t, true, 0, Fitting, (_, _) => null);
        Assert.That(noPlane.Pose, Is.Null);
        Assert.That(noPlane.NoPose, Is.EqualTo(ImplementGpsNoPose.NoPlane));
        // the antenna on the pin: no direction
        var onPin = ImplementGpsTracker.Solve(Implement(1000) with { Latitude = 20, Longitude = 0 }, t, true, 0,
            Fitting with { AntennaHeight = 0 }, Plane);
        Assert.That(onPin.Pose, Is.Null);
        Assert.That(onPin.NoPose, Is.EqualTo(ImplementGpsNoPose.AntennaOnPin));
    }

    [Test]
    public void The_Offset_Fix_moves_the_implements_position_as_the_tractors()
    {
        var t = Tractor(1000) with { DriftE = 0.4, DriftN = -0.2 };
        var s = ImplementGpsTracker.Solve(Implement(1000), t, true, 0, Fitting, Plane);
        Assert.That(s.AntennaE, Is.EqualTo(0.4).Within(1e-9));
        Assert.That(s.AntennaN, Is.EqualTo(20 - 3.3 - 0.2).Within(1e-9));

        // the RTK hold is the tractor receiver's own step taken out of the tractor's place: the implement's receiver
        // made none of it, so its place does not move with it (only written beside it)
        var held = ImplementGpsTracker.Solve(Implement(1000), Tractor(1000) with { RtkHoldM = 0.35 }, true, 0, Fitting, Plane);
        Assert.That(held.AntennaE, Is.EqualTo(0).Within(1e-9));
        Assert.That(held.AntennaN, Is.EqualTo(20 - 3.3).Within(1e-9));
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
                Assert.That(V("autosteer"), Is.EqualTo("1"));
                Assert.That(V("tractor_fix"), Is.EqualTo("4"));
                Assert.That(V("tractor_rtk_hold_m"), Is.EqualTo("0.000"));
                Assert.That(V("no_pose"), Is.Empty);
            });

            // no pose: the same columns, the solved ones empty
            var none = ImplementGpsTracker.Solve(Implement(1000), Tractor(1000), false, 0, Fitting with { Trailed = false }, Plane);
            string[] nv = ImplementGpsLog.Row(DateTime.UtcNow, none, Fitting).Split(',');
            Assert.That(nv, Has.Length.EqualTo(cols.Length));
            Assert.That(nv[Array.IndexOf(cols, "across_cm")], Is.Empty);
            Assert.That(nv[Array.IndexOf(cols, "model_work_n")], Is.Not.Empty);
            Assert.That(nv[Array.IndexOf(cols, "no_pose")], Is.EqualTo("not_trailing"));
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
    public void Switched_off_the_file_is_let_go_and_the_next_row_goes_on_in_it()
    {
        string root = Path.Combine(Path.GetTempPath(), "aow-implgps-" + Guid.NewGuid().ToString("N"));
        try
        {
            var day = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
            using var log = new ImplementGpsLog(root);
            log.Add(day, "a,1");
            log.CloseFile();   // does not wait: the host loop calls it
            Assert.That(SpinWait.SpinUntil(() => log.Written == 1, 5000), Is.True, "the row is written");
            string file = log.CurrentPath!;
            // let go: another program may now write to it
            Assert.That(SpinWait.SpinUntil(() =>
            {
                try { using var f = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return true; }
                catch (IOException) { return false; }
            }, 5000), Is.True, "the file is closed");
            Assert.That(log.CurrentName, Is.EqualTo(ImplementGpsLog.FolderName + "/" + Path.GetFileName(file)),
                "the screens get the name in the data folder, not the tablet's whole path");
            log.Add(day.AddSeconds(1), "b,2");
            Assert.That(SpinWait.SpinUntil(() => log.Written == 2, 5000), Is.True);
            log.Dispose();
            Assert.That(File.ReadAllLines(file), Is.EqualTo(new[] { ImplementGpsLog.Header, "a,1", "b,2" }));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Test]
    public void Every_row_is_either_written_or_counted_as_dropped()
    {
        string root = Path.Combine(Path.GetTempPath(), "aow-implgps-" + Guid.NewGuid().ToString("N"));
        try
        {
            // more rows at once than the queue holds: the ones that do not fit are counted, none vanish
            var day = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
            const int rows = 20000;
            long written, dropped;
            using (var log = new ImplementGpsLog(root))
            {
                for (int i = 0; i < rows; i++) log.Add(day, "r," + i.ToString(CultureInfo.InvariantCulture));
                Assert.That(SpinWait.SpinUntil(() => log.Written + log.Dropped == rows, 10000), Is.True,
                    $"written {log.Written} + dropped {log.Dropped} of {rows}");
                written = log.Written; dropped = log.Dropped;
            }
            string file = Directory.GetFiles(Path.Combine(root, ImplementGpsLog.FolderName))[0];
            Assert.That(File.ReadAllLines(file), Has.Length.EqualTo(written + 1), "what is counted as written is in the file");
            Assert.That(written + dropped, Is.EqualTo(rows));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Test]
    public void The_days_file_stops_at_its_limit_and_says_so()
    {
        string root = Path.Combine(Path.GetTempPath(), "aow-implgps-" + Guid.NewGuid().ToString("N"));
        try
        {
            var day = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
            string row = new string('x', 98);   // 100 bytes with its line end on Linux, 101 on Windows
            long limit = ImplementGpsLog.Header.Length + Environment.NewLine.Length + 3 * (98 + Environment.NewLine.Length);
            using (var log = new ImplementGpsLog(root, limit))
            {
                for (int i = 0; i < 5; i++) log.Add(day, row);
                Assert.That(SpinWait.SpinUntil(() => log.Written + log.Dropped == 5, 5000), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(log.Written, Is.EqualTo(3));
                    Assert.That(log.Dropped, Is.EqualTo(2));
                    Assert.That(log.Error, Does.Contain("full"));
                });
            }
            string file = Directory.GetFiles(Path.Combine(root, ImplementGpsLog.FolderName))[0];
            Assert.That(new FileInfo(file).Length, Is.EqualTo(limit));
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
        string good = Sentence("101530.40"), next = Sentence("101530.50");
        r.Take(Encoding.ASCII.GetBytes(good + S("GNVTG,1.0,T,,M,3.9,N,7.2,K,D") + next), 42);
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
    public void The_same_fix_twice_is_taken_once()
    {
        // a datagram that came by two links (the cable and the WiFi bridge), or a sender repeating itself
        using var r = new ImplementGpsReceiver();
        r.Take(Datagram("101530.40"), 0);
        r.Take(Datagram("101530.40"), 3);
        r.Take(Datagram("101530.50"), 100);
        // the same time and another place is another fix (two receivers would be two senders; see below)
        r.Take(Datagram("101530.50", "5699"), 101);
        int taken = 0;
        while (r.TryTake(out _, out _)) taken++;
        Assert.That(taken, Is.EqualTo(3));
        Assert.That(r.Repeated, Is.EqualTo(1));
    }

    [Test]
    public void One_sender_is_followed_and_a_second_is_counted_not_used()
    {
        // two boards left in the Implement role, or anything else on the network sending positions to the port:
        // mixed, they would be one implement jumping between two places
        using var r = new ImplementGpsReceiver();
        var board = IPAddress.Parse("192.168.5.126");
        var other = IPAddress.Parse("192.168.5.77");
        r.Take(Datagram("101530.40"), 0, board);
        r.Take(Datagram("101530.40", "9999"), 10, other);
        r.Take(Datagram("101530.50"), 100, board);
        r.Take(Datagram("101530.50", "9999"), 110, other);
        int taken = 0;
        while (r.TryTake(out var f, out _)) { taken++; Assert.That(f.Latitude, Is.EqualTo(39 + 7.2345678 / 60).Within(1e-9)); }
        Assert.Multiple(() =>
        {
            Assert.That(taken, Is.EqualTo(2));
            Assert.That(r.Sender, Is.EqualTo("192.168.5.126"));
            Assert.That(r.FromOthers, Is.EqualTo(2));
            Assert.That(r.OtherSenderAt(200), Is.EqualTo("192.168.5.77"));
            Assert.That(r.OtherSenderAt(110 + ImplementGpsReceiver.SenderHoldMs + 1), Is.Null, "it stopped: no longer said");
        });

        // the board followed is silent for 5 s (unplugged, swapped): the next one heard is followed
        long later = 100 + ImplementGpsReceiver.SenderHoldMs + 1;
        r.Take(Datagram("101540.00", "9999"), later, other);
        Assert.That(r.TryTake(out _, out _), Is.True);
        Assert.That(r.Sender, Is.EqualTo("192.168.5.77"));
    }

    [Test]
    public void More_fixes_a_second_than_any_receiver_sends_are_not_taken()
    {
        using var r = new ImplementGpsReceiver();
        // 200 different fixes inside one second
        for (int i = 0; i < 200; i++)
            r.Take(Datagram("1015" + (i / 100).ToString("00", CultureInfo.InvariantCulture) + "." + (i % 100).ToString("00", CultureInfo.InvariantCulture)), i * 4);
        int taken = 0;
        while (r.TryTake(out _, out _)) taken++;
        Assert.That(taken, Is.EqualTo(ImplementGpsReceiver.MaxPerSecond));
        Assert.That(r.Lost, Is.EqualTo(200 - ImplementGpsReceiver.MaxPerSecond));
        // the next second starts again
        r.Take(Datagram("101600.00"), 1000);
        Assert.That(r.TryTake(out _, out _), Is.True);
    }

    [Test]
    public void Fixes_the_app_never_takes_are_counted_as_lost_not_kept_for_ever()
    {
        using var r = new ImplementGpsReceiver();
        // 15 s at 10 a second with the host loop stopped
        for (int i = 0; i < 150; i++)
            r.Take(Datagram("10" + (15 + i / 600).ToString("00", CultureInfo.InvariantCulture) + (i / 10 % 60).ToString("00", CultureInfo.InvariantCulture) + "." + (i % 10 * 10).ToString("00", CultureInfo.InvariantCulture)), i * 100L);
        int taken = 0;
        while (r.TryTake(out _, out _)) taken++;
        Assert.That(taken, Is.EqualTo(100));
        Assert.That(r.Lost, Is.EqualTo(50));
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
