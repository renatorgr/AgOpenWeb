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
public readonly record struct TractorEpoch(
    int TimeCentiseconds,
    double PinE, double PinN,
    double ModelWorkE, double ModelWorkN, double ModelHeadingRad,
    double PivotE, double PivotN, double HeadingRad,
    double SpeedKmh, double RollDeg, double SteerDeg, double CrossTrackM,
    bool AutoSteer, string Pass,
    double DriftE, double DriftN);

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
/// <param name="Pose">Null when there is none: not a trailing tool, no plane, the antenna on the pin.</param>
public readonly record struct ImplementGpsSample(
    ImplementGpsFix Fix, TractorEpoch Tractor, bool Paired, int TimeDiffCentiseconds,
    double AntennaE, double AntennaN, double RollUsed, ImplementGpsPose? Pose);

/// <summary>
/// Puts each fix from the implement's GPS beside the tractor's fix of the same instant (fork only). Both receivers send
/// at 10 Hz on the GNSS second, so the same time of day is the same instant: the tractor's last
/// <see cref="RingSize"/> fixes are kept, and an implement fix waits up to <see cref="HoldMs"/> for its tractor fix
/// (the two come over different links). Without one by then it goes with the nearest, marked unpaired. Pure, for tests:
/// the clock and the plane are given.
/// </summary>
public sealed class ImplementGpsTracker
{
    public const int RingSize = 40;      // 4 s at 10 Hz
    public const long HoldMs = 500;
    public const int SameInstantCs = 2;  // receivers put their time to the hundredth
    private const int MaxPending = 50;
    private const int DayCs = 8_640_000;

    private readonly TractorEpoch[] _ring = new TractorEpoch[RingSize];
    private int _count, _next;
    private readonly List<(ImplementGpsFix Fix, long ArrivedMs)> _pending = new();

    /// <summary>Fixes dropped for want of a tractor fix to put them beside.</summary>
    public long WithoutTractor { get; private set; }

    public void AddTractor(in TractorEpoch epoch)
    {
        _ring[_next] = epoch;
        _next = (_next + 1) % RingSize;
        if (_count < RingSize) _count++;
    }

    public void AddImplement(in ImplementGpsFix fix, long arrivedMs)
    {
        _pending.Add((fix, arrivedMs));
        if (_pending.Count > MaxPending) { _pending.RemoveAt(0); WithoutTractor++; }
    }

    /// <summary>Forget everything (another plane, the listener stopped).</summary>
    public void Clear()
    {
        _count = 0; _next = 0;
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
                if (waitedOut) { _pending.RemoveAt(i); WithoutTractor++; } else i++;
                continue;
            }
            ref readonly var newest = ref Newest;
            if (fix.TimeCentiseconds < 0 || newest.TimeCentiseconds < 0)
            {
                // no time on one side (the simulator): the tractor's latest fix, unpaired
                output.Add(Solve(fix, newest, false, 0, fitting, toPlane));
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
                output.Add(Solve(fix, e, false, DiffCs(fix.TimeCentiseconds, e.TimeCentiseconds), fitting, toPlane));
                _pending.RemoveAt(i);
            }
            else i++;   // its tractor fix is still to come
        }
    }

    /// <summary>The implement's pose from one fix and the tractor's of that instant. Public for tests.</summary>
    public static ImplementGpsSample Solve(in ImplementGpsFix fix, in TractorEpoch t, bool paired, int diffCs,
        ImplementGpsFitting fitting, Func<double, double, (double E, double N)?> toPlane)
    {
        double rollUsed = fitting.RollUsed(fix.Roll);
        var p = toPlane(fix.Latitude, fix.Longitude);
        if (p is not { } plane)
            return new ImplementGpsSample(fix, t, paired, diffCs, double.NaN, double.NaN, rollUsed, null);
        double ae = plane.E + t.DriftE, an = plane.N + t.DriftN;
        ImplementGpsPose? pose = null;
        if (fitting.Trailed)
            pose = ImplementGpsGeometry.Solve(ae, an, rollUsed, fix.Heading, t.PinE, t.PinN, fitting.BehindPin,
                fitting.AntennaHeight, fitting.AntennaRight, t.ModelWorkE, t.ModelWorkN, t.ModelHeadingRad);
        return new ImplementGpsSample(fix, t, paired, diffCs, ae, an, rollUsed, pose);
    }
}
