// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;

namespace AgOpenWeb.Services.Fields;

/// <summary>
/// The pace of a job: the ground painted for each hour of work, so the time left is the ground still to paint
/// at the pace the job has gone so far, turns and the headland included. The instant speed times the width
/// (AgOpenGPS's time left) jumped with every change of speed on a slope and was ∞ in every turn and stop.
/// Work is time with the tractor moving (above <see cref="MovingMps"/>) on or near the field; a stop (a refill,
/// a word with someone) or a trip off the field is not counted, nor is ground painted then. Kept in the job
/// (<see cref="AgOpenWeb.Models.Job.Job.WorkSeconds"/>), so a job opened again goes on at its own pace. Fork only.
/// </summary>
public sealed class JobPace
{
    /// <summary>Slower than this (about 1 km/h) the tractor is standing, not working.</summary>
    public const double MovingMps = 0.3;

    /// <summary>A longer step between samples is a gap (the host held up, a tablet asleep): not counted.</summary>
    public const double MaxStepSeconds = 5;

    /// <summary>More ground than this per second between two samples is a job's coverage loading or
    /// another job's, not work: no implement paints 1000 m² a second (36 m at 100 km/h).</summary>
    public const double MaxPaintSqMPerSecond = 1000;

    /// <summary>The pace is used once the job has this much work behind it; before, the time left comes
    /// from the speed and the width, as before.</summary>
    public const double MinWorkSeconds = 120, MinPaintedSqM = 1000;

    private double _lastSeconds = double.NaN;
    private double _lastPainted = double.NaN;

    /// <summary>Seconds of work in the job.</summary>
    public double WorkSeconds { get; private set; }

    /// <summary>Ground painted while working, m² (painted once; overlap is not new ground).</summary>
    public double PaintedSqM { get; private set; }

    /// <summary>Start on a job with what it had: the next sample is the first, so the job's coverage
    /// already on the map is not taken for work.</summary>
    public void Start(double workSeconds, double paintedSqM)
    {
        WorkSeconds = double.IsFinite(workSeconds) && workSeconds > 0 ? workSeconds : 0;
        PaintedSqM = double.IsFinite(paintedSqM) && paintedSqM > 0 ? paintedSqM : 0;
        _lastSeconds = double.NaN;
        _lastPainted = double.NaN;
    }

    /// <summary>One sample: the clock (s), the ground painted on the map at least once (m²), and whether the
    /// tractor is working (moving on or near the field).</summary>
    public void Sample(double seconds, double paintedSqM, bool working)
    {
        double dt = seconds - _lastSeconds, dA = paintedSqM - _lastPainted;
        bool first = double.IsNaN(_lastSeconds);
        _lastSeconds = seconds;
        _lastPainted = paintedSqM;
        if (first) return;
        if (paintedSqM <= 0 && dA < 0)
        {
            // The applied area was deleted: the job starts again.
            WorkSeconds = 0;
            PaintedSqM = 0;
            return;
        }
        if (!(dt > 0) || dt > MaxStepSeconds || !working) return;
        WorkSeconds += dt;
        if (dA > 0 && dA <= MaxPaintSqMPerSecond * dt) PaintedSqM += dA;
    }

    /// <summary>m² painted per second of work; NaN until the job has <see cref="MinWorkSeconds"/> and
    /// <see cref="MinPaintedSqM"/> behind it.</summary>
    public double PaceSqMPerSecond =>
        WorkSeconds >= MinWorkSeconds && PaintedSqM >= MinPaintedSqM ? PaintedSqM / WorkSeconds : double.NaN;

    /// <summary>The time to paint <paramref name="leftSqM"/>, seconds: at the job's pace, or before the job
    /// has one at <paramref name="instantSqMPerSecond"/> (the speed times the width; 0 or less when standing);
    /// NaN when neither is known.</summary>
    public double TimeLeftSeconds(double leftSqM, double instantSqMPerSecond)
    {
        if (!double.IsFinite(leftSqM)) return double.NaN;
        if (leftSqM <= 0) return 0;
        double pace = PaceSqMPerSecond;
        if (pace > 0) return leftSqM / pace;
        return instantSqMPerSecond > 0 ? leftSqM / instantSqMPerSecond : double.NaN;
    }
}
