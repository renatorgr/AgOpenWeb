// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AgOpenWeb.Services.Implement;

/// <summary>
/// Writes every implement fix beside the tractor's to a file of the day (fork only):
/// <c>&lt;data folder&gt;/ImplementGps/ImplementGps_yyyy-MM-dd.csv</c>, one row per fix, numbers with a point. The rows
/// are made on the host loop and written on a worker of their own, so a slow disk never holds the loop up; a row that
/// can't be written is dropped and counted.
/// </summary>
public sealed class ImplementGpsLog : IDisposable
{
    public const string FolderName = "ImplementGps";

    /// <summary>The columns. The tractor's are what the app's model used; the implement's are as its GPS sent them, then
    /// solved (<c>ImplementGpsGeometry.Solve</c>). Plane positions in metres, headings in degrees (0 north, clockwise),
    /// across and along in centimetres (+ right of, + ahead of the model's working centre).</summary>
    public const string Header =
        "tablet_utc,tractor_time,implement_time,paired,time_diff_s," +
        "impl_sentence,impl_fix,impl_sats,impl_hdop,impl_age,impl_lat,impl_lon,impl_alt,impl_speed_kmh," +
        "impl_receiver_heading,impl_roll_raw,impl_roll_used," +
        "antenna_e,antenna_n,centre_e,centre_n,impl_heading,work_e,work_n," +
        "model_work_e,model_work_n,model_heading,across_cm,along_cm,heading_diff,receiver_heading_diff," +
        "pin_e,pin_n,pivot_e,pivot_n,tractor_heading,tractor_speed_kmh,tractor_roll,steer_angle,xte_cm,autosteer,pass," +
        "behind_pin,antenna_height,antenna_right";

    private readonly string _folder;
    private readonly Channel<(DateTime Utc, string Row)> _rows =
        Channel.CreateBounded<(DateTime, string)>(new BoundedChannelOptions(3000) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Task _writer;
    private long _written, _dropped;
    private volatile string? _path;
    private volatile string? _error;

    public ImplementGpsLog(string dataFolder)
    {
        _folder = Path.Combine(dataFolder, FolderName);
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>The file written to last; null before the first row.</summary>
    public string? CurrentPath => _path;
    /// <summary>Why the last row could not be written; null once one is.</summary>
    public string? Error => _error;
    public long Written => Interlocked.Read(ref _written);
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Queue one row (from <see cref="Row"/>).</summary>
    public void Add(DateTime utc, string row)
    {
        if (!_rows.Writer.TryWrite((utc, row))) Interlocked.Increment(ref _dropped);
    }

    private async Task WriteLoopAsync()
    {
        StreamWriter? w = null;
        string? openPath = null;
        try
        {
            while (await _rows.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_rows.Reader.TryRead(out var item))
                {
                    try
                    {
                        // the day the row was made, on the tablet's own calendar: one file per working day
                        string path = Path.Combine(_folder, "ImplementGps_" +
                            item.Utc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv");
                        if (path != openPath)
                        {
                            w?.Dispose();
                            w = null; openPath = null;
                            Directory.CreateDirectory(_folder);
                            bool fresh = !File.Exists(path) || new FileInfo(path).Length == 0;
                            w = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                                new UTF8Encoding(false));
                            if (fresh) w.WriteLine(Header);
                            openPath = path;
                            _path = path;
                        }
                        w!.WriteLine(item.Row);
                        Interlocked.Increment(ref _written);
                        _error = null;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Interlocked.Increment(ref _dropped);
                        _error = ex.Message;
                        w?.Dispose(); w = null; openPath = null;
                    }
                }
                try { w?.Flush(); } catch (IOException) { }
            }
        }
        finally
        {
            try { w?.Dispose(); } catch (IOException) { }
        }
    }

    /// <summary>One row for <see cref="Header"/>.</summary>
    public static string Row(DateTime utc, in ImplementGpsSample s, in ImplementGpsFitting f)
    {
        var inv = CultureInfo.InvariantCulture;
        var b = new StringBuilder(420);
        var t = s.Tractor;
        var x = s.Fix;
        void N(double v, string fmt) { b.Append(','); if (double.IsFinite(v)) b.Append(v.ToString(fmt, inv)); }
        void Deg(double rad) => N(rad * 180.0 / Math.PI, "0.000");
        b.Append(utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", inv));
        b.Append(','); b.Append(Time(t.TimeCentiseconds));
        b.Append(','); b.Append(Time(x.TimeCentiseconds));
        b.Append(','); b.Append(s.Paired ? '1' : '0');
        N(s.TimeDiffCentiseconds / 100.0, "0.00");
        b.Append(','); b.Append(x.IsPaogi ? "PAOGI" : "PANDA");
        b.Append(','); b.Append(x.FixQuality.ToString(inv));
        b.Append(','); b.Append(x.Satellites.ToString(inv));
        N(x.Hdop, "0.0#"); N(x.Age, "0.0#");
        N(x.Latitude, "0.000000000"); N(x.Longitude, "0.000000000"); N(x.Altitude, "0.000");
        N(x.SpeedKnots * 1.852, "0.00");
        N(x.Heading, "0.00"); N(x.Roll, "0.00"); N(s.RollUsed, "0.00");
        N(s.AntennaE, "0.000"); N(s.AntennaN, "0.000");
        if (s.Pose is { } p)
        {
            N(p.CentreE, "0.000"); N(p.CentreN, "0.000"); Deg(p.HeadingRad);
            N(p.WorkE, "0.000"); N(p.WorkN, "0.000");
        }
        else b.Append(",,,,,");
        N(t.ModelWorkE, "0.000"); N(t.ModelWorkN, "0.000"); Deg(t.ModelHeadingRad);
        if (s.Pose is { } q)
        {
            N(q.AcrossM * 100, "0.0"); N(q.AlongM * 100, "0.0"); N(q.HeadingDiffDeg, "0.000"); N(q.ReceiverHeadingDiffDeg, "0.00");
        }
        else b.Append(",,,,");
        N(t.PinE, "0.000"); N(t.PinN, "0.000"); N(t.PivotE, "0.000"); N(t.PivotN, "0.000"); Deg(t.HeadingRad);
        N(t.SpeedKmh, "0.00"); N(t.RollDeg, "0.00"); N(t.SteerDeg, "0.00"); N(t.CrossTrackM * 100, "0.0");
        b.Append(','); b.Append(t.AutoSteer ? '1' : '0');
        b.Append(','); b.Append(Csv(t.Pass));
        N(f.BehindPin, "0.000"); N(f.AntennaHeight, "0.000"); N(f.AntennaRight, "0.000");
        return b.ToString();
    }

    // hundredths of a second of the day -> hh:mm:ss.ss; empty with none
    private static string Time(int cs)
    {
        if (cs < 0) return "";
        int h = cs / 360000, m = cs / 6000 % 60, sec = cs / 100 % 60, hund = cs % 100;
        return string.Create(CultureInfo.InvariantCulture, $"{h:00}:{m:00}:{sec:00}.{hund:00}");
    }

    private static string Csv(string? v) =>
        string.IsNullOrEmpty(v) ? "" : v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? v : "\"" + v.Replace("\"", "\"\"") + "\"";

    /// <summary>Writes what is queued and closes the file.</summary>
    public void Dispose()
    {
        _rows.Writer.TryComplete();
        try { _writer.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
    }
}
