// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models;
using AgOpenWeb.Models.GPS;
using AgOpenWeb.Models.State;
using AgOpenWeb.Models.Tool;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Implement;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.ViewModels;

/// <summary>What the implement's GPS says, for the screens (read off the host loop: one object, replaced whole).</summary>
/// <param name="Listening">UDP 9990 is open.</param>
/// <param name="Error">Why it is not, or why the file is not written.</param>
/// <param name="Heard">A fix came in the last 2 s.</param>
/// <param name="TimeDiffS">The implement's fix time less the tractor's; NaN when one of them carries no time (the simulator).</param>
/// <param name="Why">Why no implement is drawn, as a short name the page puts into words (<c>ImplementGpsLog.NoPoseName</c>,
/// or "nothing_yet", "silent", "wait_tractor"); null when one is.</param>
/// <param name="AntennaAcrossM">The antenna against the model's working centre, as <c>AcrossM</c>.</param>
/// <param name="SampleMs">When the last pose was made (<see cref="Environment.TickCount64"/>); 0 none.</param>
/// <param name="LogName">The day's file inside the data folder (not the tablet's whole path: every browser reads this).</param>
/// <param name="Lost">Fixes that reached the port and are not in the file: too many a second, the app busy, the disk.</param>
/// <param name="Repeated">Fixes that came twice and were taken once.</param>
/// <param name="OtherSender">A second address sending positions while the first is heard; its fixes are not used.</param>
public sealed record ImplementGpsView(
    bool Enabled, bool Listening, string? Error, string? Sender, string? Sentence, bool Heard, double RateHz,
    int FixQuality, int Satellites, double AgeS, bool Paired, double TimeDiffS, double ReceiverHeadingDeg,
    double RollRawDeg, double RollUsedDeg, bool HasPose, double AcrossM, double AlongM, double HeadingDiffDeg,
    double ReceiverHeadingDiffDeg, double AntennaAcrossM, double AntennaAlongM, string? Why, long SampleMs,
    long Refused, string? LogName, long LogRows, double AntennaHeight, double AntennaRight, bool RollInvert,
    double RollZero, double BehindPin, bool Trailed, long Lost = 0, long Repeated = 0, string? OtherSender = null,
    long FromOthers = 0)
{
    public static readonly ImplementGpsView Off = new(false, false, null, null, null, false, 0, 0, 0, double.NaN, false,
        0, double.NaN, double.NaN, double.NaN, false, 0, 0, 0, double.NaN, 0, 0, null, 0, 0, null, 0, 0, 0, false, 0, 0, false);
}

/// <summary>
/// MainViewModel partial: a GPS on a trailed implement (fork only; Network IO › Implement GPS, off by default). An AiO
/// board on the implement, in its "Implement" role (renatorgr/Boards), sends its fixes to UDP 9990; each is put beside
/// the tractor's fix of the same instant (<see cref="ImplementGpsTracker"/>), solved for where the implement really is
/// (<see cref="ImplementGpsGeometry"/>: its heading from the antenna to the tractor's pin, the antenna's height through
/// the roll and its side offset taken out, the working centre the model's distance behind the pin), drawn beside the
/// model (the Tick's implement part) and written to a file of the day (<see cref="ImplementGpsLog"/>). It steers
/// nothing and changes nothing the model does: it shows how far the model's implement is from the real one (a trailed
/// drill on a slope slides downhill; the model follows the pin) and records it to decide what to do about it.
/// </summary>
public partial class MainViewModel
{
    private ImplementGpsReceiver? _implReceiver;
    private ImplementGpsLog? _implLog;
    private readonly ImplementGpsTracker _implTracker = new();
    private readonly List<ImplementGpsSample> _implSamples = new();
    private LocalPlane? _implPlane;
    private ImplementGpsSample? _implLast;
    private long _implLastFixMs, _implLastPoseMs;
    private int _implRateCount;
    private long _implRateFromMs;
    private double _implRateHz;
    private string? _implWhy;
    private volatile ImplementGpsView _implementGpsView = ImplementGpsView.Off;
    private bool _implStopHooked;
    private long _implBindTriedMs;
    private string? _implBindError;
    private const long ImplBindRetryMs = 5000;

    /// <summary>The implement GPS as the screens show it.</summary>
    public ImplementGpsView ImplementGps => _implementGpsView;

    /// <summary>Opens or closes UDP 9990 with the setting (called at the 5 Hz status tick, so the setting's order of
    /// loading doesn't matter). A port that could not be opened (another program on it) is tried again every
    /// <see cref="ImplBindRetryMs"/>, so it comes by itself once the port is free.</summary>
    private void SyncImplementGpsListener()
    {
        bool want = ConfigStore.Connections.ImplementGpsEnabled && !_backendStopping.IsCancellationRequested;
        long now = Environment.TickCount64;
        if (want && _implReceiver is { Error: not null } failed && now - _implBindTriedMs >= ImplBindRetryMs)
        {
            failed.Dispose();
            _implReceiver = null;
        }
        if (want && _implReceiver == null)
        {
            if (!_implStopHooked)
            {
                _implStopHooked = true;
                // the backend going (Android and iOS outlive it): the port and the file closed, from any thread
                _backendStopping.Token.Register(() => { _implReceiver?.Dispose(); _implLog?.Dispose(); });
            }
            var r = new ImplementGpsReceiver();
            bool open = r.Start();
            _implReceiver = r;
            _implBindTriedMs = now;
            _implLog ??= new ImplementGpsLog(AppDataRoot.Documents);
            // a new session: nothing of the last one is "heard" or drawn
            _implTracker.Clear();
            _implLast = null;
            _implWhy = null;
            _implLastFixMs = 0; _implLastPoseMs = 0;
            _implRateFromMs = now; _implRateCount = 0; _implRateHz = 0;
            if (open) _logger.LogInformation("[ImplementGps] Listening on UDP {Port}", ImplementGpsReceiver.Port);
            else if (r.Error != _implBindError) _logger.LogWarning("[ImplementGps] {Error}", r.Error);   // once, not every 5 s
            _implBindError = r.Error;
        }
        else if (!want && _implReceiver != null)
        {
            StopImplementGps();
            _logger.LogInformation("[ImplementGps] Stopped");
        }
    }

    /// <summary>Switched off: the port closed, what was already heard written (each beside the nearest tractor fix,
    /// none waited for), the file closed without waiting for the disk.</summary>
    private void StopImplementGps()
    {
        _implReceiver?.Dispose();          // no more datagrams; the queue is still there to empty
        DrainImplementGps(flush: true);
        _implReceiver = null;
        _implBindError = null;
        _implLog?.CloseFile();
        _implTracker.Clear();
        _implLast = null;
        _implWhy = null;
        _implLastFixMs = 0; _implLastPoseMs = 0;
        _implementGpsView = ImplementGpsView.Off;
    }

    private ImplementGpsFitting ImplementGpsFittingNow()
    {
        var c = ConfigStore.Connections;
        var tool = ConfigStore.Tool;
        return new ImplementGpsFitting(RigGeometry.ToolBehindPivot(tool), c.ImplementGpsAntennaHeight,
            c.ImplementGpsAntennaRight, c.ImplementGpsRollInvert, c.ImplementGpsRollZero,
            RigGeometry.Mount(tool) == ToolMount.Trailing);
    }

    /// <summary>Each tractor fix: kept for the implement's fixes of the same instant, then those solved.</summary>
    private void OnImplementGpsCycle(GpsCycleResult result)
    {
        if (_implReceiver == null) return;
        if (!ReferenceEquals(State.Field.LocalPlane, _implPlane))
        {
            _implPlane = State.Field.LocalPlane;
            _implTracker.ClearTractor();   // its fixes are in the old plane's metres
        }
        if (result.GpsValid && result.IsToolPositionReady)
        {
            var g = result.Guidance;
            _implTracker.AddTractor(new TractorEpoch(
                result.FixTimeCentiseconds,
                result.HitchEasting, result.HitchNorthing,
                result.ToolEasting, result.ToolNorthing, result.ToolHeadingRadians,
                result.Easting, result.Northing, result.Heading * Math.PI / 180.0,
                result.Speed * 3.6, result.RollDegrees, g?.SteerAngle ?? double.NaN,
                g is { IsGuidanceActive: true } ? g.CrossTrackError : double.NaN,
                result.IsAutoSteerEngaged, g is { IsGuidanceActive: true } ? g.CurrentLineLabel : "",
                result.DriftEasting, result.DriftNorthing, result.FixQuality, result.RtkHoldM),
                Environment.TickCount64);
        }
        DrainImplementGps();
    }

    /// <param name="flush">The listener is stopping: nothing waits for a tractor fix still to come.</param>
    private void DrainImplementGps(bool flush = false)
    {
        var r = _implReceiver;
        if (r == null) return;
        long now = Environment.TickCount64;
        while (r.TryTake(out var fix, out long arrived))
        {
            _implTracker.AddImplement(fix, arrived);
            _implLastFixMs = arrived;
            _implRateCount++;
        }
        _implSamples.Clear();
        var plane = State.Field.LocalPlane;
        var fitting = ImplementGpsFittingNow();
        _implTracker.Drain(flush ? now + ImplementGpsTracker.HoldMs + 1 : now, fitting, (lat, lon) =>
        {
            if (plane == null) return null;
            var g = plane.ConvertWgs84ToGeoCoord(new Wgs84(lat, lon));
            return (g.Easting, g.Northing);
        }, _implSamples);
        if (_implSamples.Count == 0) return;

        var utc = DateTime.UtcNow;
        foreach (var s in _implSamples)
        {
            _implLog?.Add(utc, ImplementGpsLog.Row(utc, s, fitting));
            _implLast = s;
            if (s.Pose != null) _implLastPoseMs = now;
        }
        var last = _implSamples[^1];
        _implWhy = last.Pose != null ? null : ImplementGpsLog.NoPoseName(last.NoPose);
        if (!flush) PublishImplementGpsView(now);
    }

    /// <summary>5 Hz: fixes waited out, the rate, the view (also with the tractor's GPS silent).</summary>
    private void ImplementGpsStatusTick()
    {
        SyncImplementGpsListener();
        var r = _implReceiver;
        if (r == null) return;
        DrainImplementGps();
        long now = Environment.TickCount64;
        if (now - _implRateFromMs >= 1000)
        {
            _implRateHz = _implRateCount * 1000.0 / (now - _implRateFromMs);
            _implRateCount = 0;
            _implRateFromMs = now;
        }
        PublishImplementGpsView(now);
    }

    private void PublishImplementGpsView(long now)
    {
        var r = _implReceiver;
        if (r == null) { _implementGpsView = ImplementGpsView.Off; return; }
        var c = ConfigStore.Connections;
        var fitting = ImplementGpsFittingNow();
        bool heard = _implLastFixMs != 0 && now - _implLastFixMs < 2000;
        var s = _implLast;
        var fix = s?.Fix ?? default;
        var pose = s?.Pose;
        bool fresh = pose != null && now - _implLastPoseMs < 2000;
        double antAcross = 0, antAlong = 0;
        if (s is { } sv && pose != null)
        {
            double de = sv.AntennaE - sv.Tractor.ModelWorkE, dn = sv.AntennaN - sv.Tractor.ModelWorkN, h = sv.Tractor.ModelHeadingRad;
            antAcross = de * Math.Cos(h) - dn * Math.Sin(h);
            antAlong = de * Math.Sin(h) + dn * Math.Cos(h);
        }
        string? why = !heard ? (_implLastFixMs == 0 ? "nothing_yet" : "silent")
            : s == null ? "wait_tractor" : _implWhy;
        string? error = r.Error ?? _implLog?.Error;
        _implementGpsView = new ImplementGpsView(
            true, r.Error == null, error, r.Sender, r.LastType, heard, heard ? _implRateHz : 0,
            fix.FixQuality, fix.Satellites, fix.Age, s?.Paired ?? false,
            s is { } t && t.Fix.TimeCentiseconds >= 0 && t.Tractor.TimeCentiseconds >= 0 ? t.TimeDiffCentiseconds / 100.0 : double.NaN,
            fix.Heading, fix.Roll, s?.RollUsed ?? double.NaN,
            fresh, pose?.AcrossM ?? 0, pose?.AlongM ?? 0, pose?.HeadingDiffDeg ?? 0, pose?.ReceiverHeadingDiffDeg ?? double.NaN,
            antAcross, antAlong, why, fresh ? _implLastPoseMs : 0,
            r.Refused, _implLog?.CurrentName, _implLog?.Written ?? 0,
            c.ImplementGpsAntennaHeight, c.ImplementGpsAntennaRight, c.ImplementGpsRollInvert, c.ImplementGpsRollZero,
            fitting.BehindPin, fitting.Trailed,
            r.Lost + _implTracker.Lost + (_implLog?.Dropped ?? 0), r.Repeated, r.OtherSenderAt(now), r.FromOthers);
    }
}
