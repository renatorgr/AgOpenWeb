// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services;

/// <summary>
/// The simulator's time-lapse (the sim bar's 10x, fork only): ten seconds of driving pass in
/// each real second, at the speed the simulator shows. It used to multiply the speed by 10, so
/// guidance, U-turns and sections worked at 100-250 km/h and the turns were not made (official
/// issue #726).
///
/// Each call drives the simulated tractor up to "now" on the motion clock
/// (<see cref="Models.Timing.MotionClock"/>, run <see cref="Scale"/> times faster) in the
/// simulator's usual 1/30 s steps, so it steers and turns as at 1x. Every
/// <see cref="FixSeconds"/> of driving one step is a fix for the GPS cycle (a 10 Hz receiver),
/// stamped with the instant it stands for; the others only move the tractor. The steer angle
/// is read before each step: the GPS cycle processes each fix before the next step
/// (<see cref="IGpsPipelineService.SetSimulatorTimeLapse"/>), so the steps after a fix are
/// steered by it, as on a tractor.
/// </summary>
public sealed class SimulatorTimeLapse
{
    /// <summary>How many times faster than real time the time-lapse runs.</summary>
    public const double Scale = 10.0;

    /// <summary>Driving time between two fixes sent to the GPS cycle: a 10 Hz receiver.</summary>
    public const double FixSeconds = 0.1;

    /// <summary>Simulator steps per fix: its own 1/30 s step (<c>MainViewModel._simulatorTimer</c>).</summary>
    public const int StepsPerFix = 3;

    /// <summary>Further behind "now" than this (a PC too slow for 10x, a long cycle), the
    /// simulated tractor waits: the time is let go, never driven in one burst.</summary>
    public const double MaxBehindSeconds = 2.0;

    private readonly IGpsSimulationService _sim;
    private long _reached;   // motion time the simulated tractor has reached

    public SimulatorTimeLapse(IGpsSimulationService sim) => _sim = sim;

    /// <summary>True during a step between two fixes: its position is not a fix for the GPS cycle.</summary>
    public bool IsStepBetweenFixes { get; private set; }

    /// <summary>The motion-clock instant of the fix being sent; 0 when no fix is being sent.</summary>
    public long FixTimestamp { get; private set; }

    /// <summary>Starts from <paramref name="motionNow"/>: no time to catch up.</summary>
    public void Start(long motionNow) => _reached = motionNow;

    /// <summary>
    /// Drives the simulated tractor up to <paramref name="motionNow"/>, whole fixes only (the
    /// rest waits for the next call). <paramref name="steerDegrees"/> is read before each step;
    /// the loop ends early when <paramref name="keepGoing"/> says so (a cycle turned the
    /// simulator or the time-lapse off). Returns the number of fixes sent.
    /// </summary>
    public int Advance(long motionNow, long frequency, Func<double> steerDegrees, Func<bool> keepGoing)
    {
        long fixTicks = (long)(FixSeconds * frequency);
        if (motionNow - _reached > (long)(MaxBehindSeconds * frequency))
            _reached = motionNow - fixTicks;   // too far behind: wait, never jump

        double stepSeconds = FixSeconds / StepsPerFix;
        int fixes = 0;
        while (keepGoing() && motionNow - _reached >= fixTicks)
        {
            IsStepBetweenFixes = true;
            try
            {
                for (int i = 0; i < StepsPerFix - 1; i++)
                    _sim.Tick(steerDegrees(), stepSeconds);
            }
            finally { IsStepBetweenFixes = false; }

            _reached += fixTicks;
            FixTimestamp = _reached;
            try { _sim.Tick(steerDegrees(), stepSeconds); }
            finally { FixTimestamp = 0; }
            fixes++;
        }
        return fixes;
    }
}
