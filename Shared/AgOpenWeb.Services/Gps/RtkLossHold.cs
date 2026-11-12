// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;

namespace AgOpenWeb.Services.Gps;

/// <summary>
/// Holds the line when RTK is lost (fork only). When the corrections stop (no mobile network in a
/// valley), the receiver falls from RTK fixed to float, DGPS or a plain fix, and its position steps
/// half a metre to two metres sideways at once: AutoSteer then pulled the tractor across to the new
/// position. Here the step is measured against where the tractor would be from its own travel (the
/// last two fixes) and taken out, so the line is held. The receiver's error grows with the minutes,
/// so the hold lasts <see cref="HoldLimitS"/> at most; then, and when RTK fixed is back (its own step
/// taken out too), the offset goes to 0 at <see cref="EaseOutMPerS"/>: no pull on the steering.
/// <para>Only a change of fix quality is a step: a position that wanders while the quality stays is
/// followed, as before. Fixes further apart than <see cref="MaxGapS"/>, and steps larger than
/// <see cref="MaxStepM"/> (another plane, the simulator switched), are not taken out.</para>
/// </summary>
public sealed class RtkLossHold
{
    /// <summary>A step smaller than this is noise: nothing to take out, m.</summary>
    public const double MinStepM = 0.02;

    /// <summary>A step larger than this is not a change of solution: it is followed, m.</summary>
    public const double MaxStepM = 5.0;

    /// <summary>Fixes further apart than this can't tell a step from travel, s.</summary>
    public const double MaxGapS = 1.0;

    /// <summary>How fast a held offset goes back to 0, m/s: 1 m in 20 s, a gentle bend at work speed.</summary>
    public const double EaseOutMPerS = 0.05;

    /// <summary>The longest hold, s: the receiver's own error has grown to the size of the step by then.</summary>
    public const double HoldLimitS = 180.0;

    /// <summary>RTK fixed (GGA quality 4).</summary>
    public const int RtkFixed = 4;

    /// <summary>What an update did, for the operator.</summary>
    public enum Change
    {
        None,
        /// <summary>RTK fixed was lost: the position is held (<see cref="Step.StepM"/> taken out).</summary>
        Held,
        /// <summary>The hold lasted <see cref="HoldLimitS"/>: the offset now goes to 0.</summary>
        LimitReached,
        /// <summary>RTK fixed is back: the offset left goes to 0.</summary>
        Back,
    }

    /// <summary>The result of one fix: what changed, and the step taken out (0 when none), m.</summary>
    public readonly record struct Step(Change What, double StepM);

    /// <summary>Added to the receiver's position, m (east).</summary>
    public double OffsetE { get; private set; }

    /// <summary>Added to the receiver's position, m (north).</summary>
    public double OffsetN { get; private set; }

    /// <summary>The offset's size, m.</summary>
    public double OffsetM => Math.Sqrt(OffsetE * OffsetE + OffsetN * OffsetN);

    /// <summary>RTK fixed is lost and the hold has not reached its limit.</summary>
    public bool IsHolding { get; private set; }

    /// <summary>Seconds since RTK fixed was lost, while holding.</summary>
    public double HeldForS => IsHolding && _hasPrev ? _prevT - _holdSince : 0;

    private bool _hasPrev;
    private double _prevE, _prevN, _prevT;
    private int _prevQuality;
    // The travel from one fix to the next at the receiver's own rate. Fixes are measured per
    // epoch, not per second of arrival: two fixes handled in one burst are still 0.1 s of travel
    // apart, and a fix the receiver skipped is two epochs of travel.
    private bool _hasStep;
    private double _stepE, _stepN;
    private double _interval; // the usual time between fixes, s (0 until known)
    private double _holdSince;

    /// <summary>Forget everything: a new plane (field opened or closed), the simulator switched.</summary>
    public void Reset()
    {
        _hasPrev = _hasStep = false;
        _interval = 0;
        IsHolding = false;
        OffsetE = OffsetN = 0;
    }

    /// <summary>
    /// One fix from the receiver, before the offset is added.
    /// </summary>
    /// <param name="quality">GGA fix quality (0 none, 1 GPS, 2 DGPS, 4 RTK fixed, 5 RTK float).</param>
    /// <param name="easting">The receiver's position on the field's plane, m.</param>
    /// <param name="northing">The receiver's position on the field's plane, m.</param>
    /// <param name="timeS">When it arrived, s (any origin).</param>
    /// <param name="enabled">The operator's setting: off, no new step is taken out and an offset left goes to 0.</param>
    public Step Update(int quality, double easting, double northing, double timeS, bool enabled)
    {
        if (quality <= 0) return default; // no position: the next fix measures across the gap
        double dt = _hasPrev ? timeS - _prevT : double.NaN;
        bool consecutive = dt >= 0 && dt <= MaxGapS;
        // How many of the receiver's epochs since the last fix (1 in a burst or before the rate is known).
        int epochs = consecutive && _interval > 0 ? Math.Max(1, (int)Math.Round(dt / _interval)) : 1;
        var what = Change.None;
        double taken = 0;
        if (!consecutive) _hasStep = false; // after a gap the old travel says nothing

        if (_hasPrev && quality != _prevQuality)
        {
            bool leaving = _prevQuality == RtkFixed;
            bool returning = quality == RtkFixed;
            if (enabled && consecutive && (leaving || returning || IsHolding))
            {
                // Where the tractor would be from its own travel; the rest is the receiver's step.
                double se = easting - (_prevE + (_hasStep ? _stepE * epochs : 0));
                double sn = northing - (_prevN + (_hasStep ? _stepN * epochs : 0));
                double s = Math.Sqrt(se * se + sn * sn);
                if (s >= MinStepM && s <= MaxStepM)
                {
                    OffsetE -= se;
                    OffsetN -= sn;
                    taken = s;
                }
            }
            if (leaving && enabled)
            {
                IsHolding = true;
                _holdSince = timeS;
                what = Change.Held;
            }
            else if (returning)
            {
                if (IsHolding || OffsetM > 0) what = Change.Back;
                IsHolding = false;
            }
        }
        else if (consecutive)
        {
            // Travel at the same quality: the step for the next prediction, per epoch.
            _stepE = (easting - _prevE) / epochs;
            _stepN = (northing - _prevN) / epochs;
            _hasStep = true;
        }
        if (consecutive && dt > 0.02) // a burst says nothing about the receiver's rate
            _interval = _interval > 0 ? _interval + 0.2 * (dt / epochs - _interval) : dt;

        if (IsHolding && (!enabled || timeS - _holdSince >= HoldLimitS))
        {
            IsHolding = false;
            if (enabled) what = Change.LimitReached;
        }

        // Not holding: the offset goes to 0, gently.
        if (!IsHolding && consecutive && what != Change.Back && OffsetM > 0)
        {
            double m = OffsetM, left = Math.Max(0, m - EaseOutMPerS * dt);
            if (left < 1e-4) OffsetE = OffsetN = 0;
            else { OffsetE *= left / m; OffsetN *= left / m; }
        }

        _hasPrev = true;
        _prevE = easting;
        _prevN = northing;
        _prevT = timeS;
        _prevQuality = quality;
        return new Step(what, taken);
    }
}
