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
/// can't be written is dropped and counted (<see cref="Dropped"/>), and one counts as written only once it is flushed
/// to the file. A day's file stops at <see cref="DefaultMaxFileBytes"/> (a receiver's own 10 a second make about
/// 250 MB in 24 h): the app never deletes these files, and a flood must not fill the tablet.
/// </summary>
public sealed class ImplementGpsLog : IDisposable
{
    public const string FolderName = "ImplementGps";
    public const long DefaultMaxFileBytes = 512L * 1024 * 1024;
    private const int QueueSize = 3000;   // 5 minutes at 10 Hz

    /// <summary>The columns. The tractor's are what the app's model used; the implement's are as its GPS sent them, then
    /// solved (<c>ImplementGpsGeometry.Solve</c>). Plane positions in metres, headings in degrees (0 north, clockwise),
    /// across and along in centimetres (+ right of, + ahead of the model's working centre). <c>no_pose</c> says why
    /// the solved columns are empty; the tractor's are empty with no tractor fix at all.</summary>
    public const string Header =
        "tablet_utc,tractor_time,implement_time,paired,time_diff_s," +
        "impl_sentence,impl_fix,impl_sats,impl_hdop,impl_age,impl_lat,impl_lon,impl_alt,impl_speed_kmh," +
        "impl_receiver_heading,impl_roll_raw,impl_roll_used," +
        "antenna_e,antenna_n,centre_e,centre_n,impl_heading,work_e,work_n," +
        "model_work_e,model_work_n,model_heading,across_cm,along_cm,heading_diff,receiver_heading_diff," +
        "pin_e,pin_n,pivot_e,pivot_n,tractor_heading,tractor_speed_kmh,tractor_roll,steer_angle,xte_cm,autosteer,pass," +
        "behind_pin,antenna_height,antenna_right,tractor_fix,tractor_rtk_hold_m,no_pose";

    private readonly string _folder;
    private readonly long _maxFileBytes;
    // Wait, not DropWrite: with DropWrite a full queue drops the row and TryWrite still says yes, so nothing is counted
    private readonly Channel<(DateTime Utc, string? Row)> _rows =
        Channel.CreateBounded<(DateTime, string?)>(new BoundedChannelOptions(QueueSize) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Task _writer;
    private long _written, _dropped;
    private volatile string? _path;
    private volatile string? _error;
    private volatile bool _closeAsked;

    public ImplementGpsLog(string dataFolder, long maxFileBytes = DefaultMaxFileBytes)
    {
        _folder = Path.Combine(dataFolder, FolderName);
        _maxFileBytes = maxFileBytes;
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>The file written to last; null before the first row.</summary>
    public string? CurrentPath => _path;
    /// <summary>That file inside the data folder (<c>ImplementGps/ImplementGps_yyyy-MM-dd.csv</c>): what the screens
    /// show, so the tablet's own folders are not sent to every browser that looks on.</summary>
    public string? CurrentName => _path is { } p ? FolderName + "/" + Path.GetFileName(p) : null;
    /// <summary>Why rows are not written (the disk, or the day's file full); null once one is.</summary>
    public string? Error => _error;
    /// <summary>Rows flushed to the file.</summary>
    public long Written => Interlocked.Read(ref _written);
    /// <summary>Rows not written: the queue full, the file full, or the disk said no.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Queue one row (from <see cref="Row"/>). Never waits.</summary>
    public void Add(DateTime utc, string row)
    {
        if (!_rows.Writer.TryWrite((utc, row))) Interlocked.Increment(ref _dropped);
    }

    /// <summary>Close the day's file once what is queued is written (the listener was switched off), without waiting:
    /// the host loop calls this. The next row opens it again.</summary>
    public void CloseFile()
    {
        _closeAsked = true;
        _rows.Writer.TryWrite((default, null));   // only wakes the writer; with the queue full it is awake already
    }

    private async Task WriteLoopAsync()
    {
        StreamWriter? w = null;
        string? openPath = null;
        long size = 0;
        int unflushed = 0;
        bool full = false;
        int newLine = Environment.NewLine.Length;

        // closes the file; what was not yet flushed counts as written only if the close flushed it
        void Close()
        {
            if (w == null) return;
            try
            {
                w.Dispose();
                Interlocked.Add(ref _written, unflushed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Interlocked.Add(ref _dropped, unflushed);
                _error = ex.Message;
            }
            w = null; openPath = null; unflushed = 0;
        }

        try
        {
            while (await _rows.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_rows.Reader.TryRead(out var item))
                {
                    if (item.Row == null) continue;   // CloseFile's wake-up
                    try
                    {
                        // the day the row was made, on the tablet's own calendar: one file per working day
                        string path = Path.Combine(_folder, "ImplementGps_" +
                            item.Utc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv");
                        if (path != openPath)
                        {
                            Close();
                            Directory.CreateDirectory(_folder);
                            var info = new FileInfo(path);
                            size = info.Exists ? info.Length : 0;
                            w = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                                new UTF8Encoding(false));
                            if (size == 0)
                            {
                                w.WriteLine(Header);
                                size += Header.Length + newLine;
                            }
                            openPath = path;
                            _path = path;
                            full = false;
                        }
                        int length = Encoding.UTF8.GetByteCount(item.Row) + newLine;
                        if (size + length > _maxFileBytes)
                        {
                            Interlocked.Increment(ref _dropped);
                            full = true;
                            _error = string.Create(CultureInfo.InvariantCulture,
                                $"The day's file is full ({_maxFileBytes / (1024 * 1024)} MB): rows are not written");
                            continue;
                        }
                        w!.WriteLine(item.Row);
                        size += length;
                        unflushed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Interlocked.Add(ref _dropped, unflushed + 1);
                        unflushed = 0;
                        _error = ex.Message;
                        try { w?.Dispose(); } catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { }
                        w = null; openPath = null;
                    }
                }
                if (_closeAsked)
                {
                    _closeAsked = false;
                    Close();
                    continue;
                }
                if (w == null || unflushed == 0) continue;
                try
                {
                    w.Flush();
                    Interlocked.Add(ref _written, unflushed);
                    unflushed = 0;
                    if (!full) _error = null;   // a full file stays said
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Interlocked.Add(ref _dropped, unflushed);
                    unflushed = 0;
                    _error = ex.Message;
                    try { w.Dispose(); } catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { }
                    w = null; openPath = null;
                }
            }
        }
        finally
        {
            Close();
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
        // implement time less tractor time; empty when one of them carries none (the simulator, no tractor fix)
        N(t.TimeCentiseconds >= 0 && x.TimeCentiseconds >= 0 ? s.TimeDiffCentiseconds / 100.0 : double.NaN, "0.00");
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
        b.Append(','); if (t.FixQuality >= 0) b.Append(t.AutoSteer ? '1' : '0');
        b.Append(','); b.Append(Csv(t.Pass));
        N(f.BehindPin, "0.000"); N(f.AntennaHeight, "0.000"); N(f.AntennaRight, "0.000");
        b.Append(','); if (t.FixQuality >= 0) b.Append(t.FixQuality.ToString(inv));
        N(t.RtkHoldM, "0.000");
        b.Append(','); b.Append(NoPoseName(s.NoPose));
        return b.ToString();
    }

    /// <summary>The reason as the file and the screens name it; empty with a pose.</summary>
    public static string NoPoseName(ImplementGpsNoPose why) => why switch
    {
        ImplementGpsNoPose.NoTractor => "no_tractor_fix",
        ImplementGpsNoPose.TractorStale => "tractor_fix_missing",
        ImplementGpsNoPose.NoFix => "no_fix",
        ImplementGpsNoPose.NoPlane => "no_plane",
        ImplementGpsNoPose.NotTrailed => "not_trailing",
        ImplementGpsNoPose.AntennaOnPin => "antenna_on_pin",
        _ => "",
    };

    // hundredths of a second of the day -> hh:mm:ss.ss; empty with none
    private static string Time(int cs)
    {
        if (cs < 0) return "";
        int h = cs / 360000, m = cs / 6000 % 60, sec = cs / 100 % 60, hund = cs % 100;
        return string.Create(CultureInfo.InvariantCulture, $"{h:00}:{m:00}:{sec:00}.{hund:00}");
    }

    private static string Csv(string? v) =>
        string.IsNullOrEmpty(v) ? "" : v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? v : "\"" + v.Replace("\"", "\"\"") + "\"";

    /// <summary>Writes what is queued and closes the file, waiting up to 2 s for it: for the app going down and for
    /// tests, never on the host loop (that one calls <see cref="CloseFile"/>).</summary>
    public void Dispose()
    {
        _rows.Writer.TryComplete();
        try { _writer.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
    }
}
