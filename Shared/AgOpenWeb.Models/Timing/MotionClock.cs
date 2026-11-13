// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Models.Timing;

/// <summary>
/// The clock the vehicle's motion is measured on: the GPS cycle stamps each fix with it,
/// the position estimator dead-reckons by it, and the 100 Hz control loop and the map's
/// render pull ask the estimator for the pose at its "now". It is <see cref="Clock.Current"/>,
/// except while the simulator runs in time-lapse (the sim bar's 10x), when it runs
/// <see cref="Scale"/> times faster: ten seconds of driving pass in each real second, at the
/// speed the simulator shows. Everything else (NTRIP, module silence, the web client's
/// timeline, logs) stays on real time. Fork only.
/// </summary>
public static class MotionClock
{
    private static readonly object Gate = new();
    // Motion time = _motionAnchor + (real - _realAnchor) * _scale. Both anchors 0 and the
    // scale 1 (the default) make it the real clock itself, timestamp for timestamp.
    private static long _realAnchor;
    private static long _motionAnchor;
    private static double _scale = 1.0;

    /// <summary>How many times faster than real time the motion clock runs (1 = real time).</summary>
    public static double Scale
    {
        get { lock (Gate) return _scale; }
    }

    /// <summary>Now on the motion clock, in <see cref="Clock.Current"/>'s timestamp units.</summary>
    public static long GetTimestamp() => FromReal(Clock.Current.GetTimestamp());

    /// <summary>The motion-clock timestamp of a real <see cref="Clock.Current"/> timestamp.</summary>
    public static long FromReal(long realTimestamp)
    {
        lock (Gate)
        {
            if (_scale == 1.0) return _motionAnchor + (realTimestamp - _realAnchor);
            return _motionAnchor + (long)((realTimestamp - _realAnchor) * _scale);
        }
    }

    /// <summary>
    /// Runs the motion clock <paramref name="scale"/> times faster from now on. It never
    /// jumps: the new rate starts from the motion time reached, so a pose stamped before the
    /// change still dead-reckons from the right instant.
    /// </summary>
    public static void SetScale(double scale)
    {
        if (!(scale > 0) || double.IsInfinity(scale))
            throw new System.ArgumentOutOfRangeException(nameof(scale), "The scale must be positive");
        lock (Gate)
        {
            if (scale == _scale) return;
            long real = Clock.Current.GetTimestamp();
            long motion = _scale == 1.0
                ? _motionAnchor + (real - _realAnchor)
                : _motionAnchor + (long)((real - _realAnchor) * _scale);
            _realAnchor = real;
            _motionAnchor = motion;
            _scale = scale;
        }
    }

    /// <summary>Back to the real clock exactly (tests; never needed by the app).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _realAnchor = 0;
            _motionAnchor = 0;
            _scale = 1.0;
        }
    }
}
