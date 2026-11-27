// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.GPS;

namespace AgOpenWeb.Services.Implement;

/// <summary>What the tractor's fix said at one instant: where the model puts the implement, and how the tractor drove.
/// Positions on the field's plane (the Offset Fix in them), headings in radians (0 north, clockwise).</summary>
/// <param name="TimeCentiseconds">The tractor receiver's time of day; -1 none (the simulator).</param>
/// <param name="PinE">The drawbar pin (the cycle's hitch point).</param>
/// <param name="ModelWorkE">The model's working centre (the cycle's tool point).</param>
/// <param name="FixQuality">The tractor receiver's own fix (4 RTK fixed).</param>
/// <param name="RtkHoldM">How far the tractor's position is held from its receiver's (Hold the line when RTK is lost);
/// 0 none. It takes out the tractor receiver's own step, so it is NOT added to the implement's position.</param>
public readonly record struct TractorEpoch(
    int TimeCentiseconds,
    double PinE, double PinN,
    double ModelWorkE, double ModelWorkN, double ModelHeadingRad,
    double PivotE, double PivotN, double HeadingRad,
    double SpeedKmh, double RollDeg, double SteerDeg, double CrossTrackM,
    bool AutoSteer, string Pass,
    double DriftE, double DriftN,
    int FixQuality = 0, double RtkHoldM = 0)
{
    /// <summary>No tractor fix at all: every number empty (and no Offset Fix known: the antenna's place is as its
    /// receiver gave it).</summary>
    public static readonly TractorEpoch None = new(-1, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
        double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, false, "",
        0, 0, -1, double.NaN);
}

/// <summary>Why a fix from the implement's GPS gives no pose (it is still written to the file).</summary>
public enum ImplementGpsNoPose
{
    /// <summary>It has one.</summary>
    None = 0,
    /// <summary>The tractor's GPS gave no fix to put it beside.</summary>
    NoTractor,
    /// <summary>The tractor's fix of its time is missing and the nearest is too far off (the tractor's GPS silent).</summary>
    TractorStale,
    /// <summary>The implement's receiver has no fix (quality 0).</summary>
    NoFix,
    /// <summary>No field plane yet.</summary>
    NoPlane,
    /// <summary>The tool is not a trailing one.</summary>
    NotTrailed,
    /// <summary>The antenna is on the pin: no direction.</summary>
    AntennaOnPin,
}

/// <summary>How the implement's GPS is fitted: the implement's own measures (Network IO) and the model's.</summary>
/// <param name="BehindPin">The pin to the working centre (<c>RigGeometry.ToolBehindPivot</c>).</param>
/// <param name="Trailed">The tool is a trailing one: only then does its centre line run through the pin.</param>
public readonly record struct ImplementGpsFitting(
    double BehindPin, double AntennaHeight, double AntennaRight, bool RollInvert, double RollZero, bool Trailed)
{
    /// <summary>The roll as the app uses it: turned round if set, less its reading on level ground; NaN stays NaN.</summary>
    public double RollUsed(double raw) => double.IsNaN(raw) ? double.NaN : (RollInvert ? -raw : raw) - RollZero;
}

/// <summary>One implement fix put beside the tractor's.</summary>
/// <param name="Paired">The tractor's fix of the same receiver time was found; otherwise the nearest one.</param>
/// <param name="TimeDiffCentiseconds">Implement time less tractor time; 0 when either has none.</param>
/// <param name="Pose">Null when there is none: <paramref name="NoPose"/> says why.</param>
public readonly record struct ImplementGpsSample(
    ImplementGpsFix Fix, TractorEpoch Tractor, bool Paired, int TimeDiffCentiseconds,
    double AntennaE, double AntennaN, double RollUsed, ImplementGpsPose? Pose,
    ImplementGpsNoPose NoPose = ImplementGpsNoPose.None);

/// <summary>
/// Puts each fix from the implement's GPS beside the tractor's fix of the same instant (fork only). Both receivers send
/// at 10 Hz on the GNSS second, so the same time of day is the same instant: the tractor's last
/// <see cref="RingSize"/> fixes are kept, and an implement fix waits up to <see cref="HoldMs"/> for its tractor fix
/// (the two come over different links). Without one by then it goes with the nearest, marked unpaired, and only when
/// that one is within <see cref="NearCs"/>: beside an older tractor fix (the tractor's GPS silent) it would be drawn
/// where it is not, so it gets no pose. Every fix comes out, with a pose or the reason for none, so every one is
/// written. Pure, for tests: the clock and the plane are given.
/// </summary>
public sealed class ImplementGpsTracker
{
    public const int RingSize = 40;      // 4 s at 10 Hz
    public const long HoldMs = 500;
    public const int SameInstantCs = 2;  // receivers put their time to the hundredth
    public const int NearCs = 30;        // an unpaired fix gets a pose only this near its tractor fix (0.6 m at 7 km/h)
    public const long StaleMs = 1000;    // with no times to compare: the tractor's latest fix no older than this
    private const int MaxPending = 50;
    private const int DayCs = 8_640_000;

    private readonly TractorEpoch[] _ring = new TractorEpoch[RingSize];
    private int _count, _next;
    private readonly List<(ImplementGpsFix Fix, long ArrivedMs)> _pending = new();
    private long _newestArrivedMs;

    /// <summary>Fixes that came out with no tractor fix at all to put them beside.</summary>
    public long WithoutTractor { get; private set; }
    /// <summary>Fixes lost because nothing took them out (more than <c>MaxPending</c> waiting).</summary>
    public long Lost { get; private set; }

    public void AddTractor(in TractorEpoch epoch, long nowMs)
    {
        _ring[_next] = epoch;
        _next = (_next + 1) % RingSize;
        if (_count < RingSize) _count++;
        _newestArrivedMs = nowMs;
    }

    public void AddImplement(in ImplementGpsFix fix, long arrivedMs)
    {
        _pending.Add((fix, arrivedMs));
        if (_pending.Count > MaxPending) { _pending.RemoveAt(0); Lost++; }
    }

    /// <summary>Forget the tractor's fixes (another plane: they are in the old one's metres). The implement's fixes
    /// still waiting stay, for the tractor's next ones.</summary>
    public void ClearTractor()
    {
        _count = 0; _next = 0;
    }

    /// <summary>Forget everything (the listener stopped, after its last <see cref="Drain"/>).</summary>
    public void Clear()
    {
        ClearTractor();
        _pending.Clear();
    }

    private ref readonly TractorEpoch Newest => ref _ring[(_next + RingSize - 1) % RingSize];

    /// <summary>Time a less time b on the clock of the day (-half a day .. half a day).</summary>
    public static int DiffCs(int a, int b)
    {
        int d = (a - b) % DayCs;
        if (d > DayCs / 2) d -= DayCs;
        if (d < -DayCs / 2) d += DayCs;
        return d;
    }

    /// <summary>Puts every waiting fix that can be beside its tractor fix, in the order they came, and solves it.
    /// <paramref name="toPlane"/> gives a latitude / longitude on the field's plane (null with no plane).</summary>
    public void Drain(long nowMs, ImplementGpsFitting fitting, Func<double, double, (double E, double N)?> toPlane,
        List<ImplementGpsSample> output)
    {
        int i = 0;
        while (i < _pending.Count)
        {
            var (fix, arrived) = _pending[i];
            bool waitedOut = nowMs - arrived > HoldMs;
            if (_count == 0)
            {
                // no tractor fix at all: written as it came, with nothing beside it
                if (waitedOut)
                {
                    output.Add(Solve(fix, TractorEpoch.None, false, 0, fitting, toPlane, ImplementGpsNoPose.NoTractor));
                    _pending.RemoveAt(i);
                    WithoutTractor++;
                }
                else i++;
                continue;
            }
            ref readonly var newest = ref Newest;
            if (fix.TimeCentiseconds < 0 || newest.TimeCentiseconds < 0)
            {
                // no time on one side (the simulator): the tractor's latest fix, unpaired, while it is a fresh one
                bool silent = nowMs - _newestArrivedMs > StaleMs;
                output.Add(Solve(fix, newest, false, 0, fitting, toPlane,
                    silent ? ImplementGpsNoPose.TractorStale : ImplementGpsNoPose.None));
                _pending.RemoveAt(i);
                continue;
            }
            int best = -1, bestAbs = int.MaxValue;
            for (int k = 0; k < _count; k++)
            {
                int t = _ring[k].TimeCentiseconds;
                if (t < 0) continue;
                int d = Math.Abs(DiffCs(fix.TimeCentiseconds, t));
                if (d < bestAbs) { bestAbs = d; best = k; }
            }
            bool tractorPast = DiffCs(newest.TimeCentiseconds, fix.TimeCentiseconds) > SameInstantCs;
            if (best >= 0 && bestAbs <= SameInstantCs)
            {
                output.Add(Solve(fix, _ring[best], true, DiffCs(fix.TimeCentiseconds, _ring[best].TimeCentiseconds), fitting, toPlane));
                _pending.RemoveAt(i);
            }
            else if (waitedOut || tractorPast)
            {
                var e = best >= 0 ? _ring[best] : newest;
                int d = DiffCs(fix.TimeCentiseconds, e.TimeCentiseconds);
                output.Add(Solve(fix, e, false, d, fitting, toPlane,
                    Math.Abs(d) > NearCs ? ImplementGpsNoPose.TractorStale : ImplementGpsNoPose.None));
                _pending.RemoveAt(i);
            }
            else i++;   // its tractor fix is still to come
        }
    }

    /// <summary>The implement's pose from one fix and the tractor's of that instant, or why it has none
    /// (<paramref name="noPose"/>: a reason the pairing already knows). Public for tests.</summary>
    public static ImplementGpsSample Solve(in ImplementGpsFix fix, in TractorEpoch t, bool paired, int diffCs,
        ImplementGpsFitting fitting, Func<double, double, (double E, double N)?> toPlane,
        ImplementGpsNoPose noPose = ImplementGpsNoPose.None)
    {
        double rollUsed = fitting.RollUsed(fix.Roll);
        // a receiver with no fix still sends numbers (its last, or zeros): they are not a place
        if (fix.FixQuality <= 0)
            return new ImplementGpsSample(fix, t, paired, diffCs, double.NaN, double.NaN, rollUsed, null, ImplementGpsNoPose.NoFix);
        var p = toPlane(fix.Latitude, fix.Longitude);
        if (p is not { } plane)
            return new ImplementGpsSample(fix, t, paired, diffCs, double.NaN, double.NaN, rollUsed, null,
                noPose != ImplementGpsNoPose.None ? noPose : ImplementGpsNoPose.NoPlane);
        // the Offset Fix moves the whole map, so the implement too. The RTK hold is NOT added: it takes out the step
        // the tractor's own receiver made, and the implement's receiver made none of it.
        double ae = plane.E + t.DriftE, an = plane.N + t.DriftN;
        if (noPose != ImplementGpsNoPose.None)
            return new ImplementGpsSample(fix, t, paired, diffCs, ae, an, rollUsed, null, noPose);
        if (!fitting.Trailed)
            return new ImplementGpsSample(fix, t, paired, diffCs, ae, an, rollUsed, null, ImplementGpsNoPose.NotTrailed);
        var pose = ImplementGpsGeometry.Solve(ae, an, rollUsed, fix.Heading, t.PinE, t.PinN, fitting.BehindPin,
            fitting.AntennaHeight, fitting.AntennaRight, t.ModelWorkE, t.ModelWorkN, t.ModelHeadingRad);
        return new ImplementGpsSample(fix, t, paired, diffCs, ae, an, rollUsed, pose,
            pose == null ? ImplementGpsNoPose.AntennaOnPin : ImplementGpsNoPose.None);
    }
}
