// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

namespace AgOpenWeb.Services.Interfaces;

/// <summary>
/// Produces a final vehicle heading from a raw GPS-sentence heading by
/// applying dual-antenna offset, fix-to-fix smoothing at low speed, and
/// (when available) IMU fusion.
///
/// Called by the cycle worker once per tick, after parsing and before
/// guidance. Holds fix-to-fix state between calls; <see cref="Reset"/>
/// clears that state on field close or connection drop.
/// </summary>
public interface IGpsHeadingFusionService
{
    /// <summary>
    /// Compute the final heading given the raw GPS sentence heading, the
    /// IMU heading (when valid), and the current fix's position and speed.
    /// </summary>
    /// <param name="gpsHeading">Raw heading from the NMEA sentence, degrees 0–360.
    /// For PANDA this is seeded from the IMU; for PAOGI it's the dual-antenna heading.</param>
    /// <param name="imuHeading">IMU heading in degrees 0–360. Only meaningful
    /// when <paramref name="imuValid"/> is true (PANDA + IMU present).</param>
    /// <param name="imuValid">True when the IMU block in the latest sentence
    /// is valid. False for PAOGI or PANDA with the 65535 sentinel.</param>
    /// <param name="speedMs">Current speed, m/s.</param>
    /// <param name="easting">Current easting, meters, local frame.</param>
    /// <param name="northing">Current northing, meters, local frame.</param>
    /// <param name="ksxtHeading">Dual-antenna heading read directly from the
    /// latest $KSXT sentence, degrees 0–360. Only meaningful when
    /// <paramref name="ksxtValid"/> is true. Defaults to 0 for callers that
    /// don't parse KSXT (single-antenna setups).</param>
    /// <param name="ksxtValid">True when a $KSXT sentence with a valid
    /// heading fix was received this cycle. When true, this takes priority
    /// over the static <c>IsDualGps</c> setting: the dual-antenna solution
    /// is used because it is actually fresh right now, not because the
    /// operator ticked a box. When false — antenna blocked, or no KSXT
    /// parser wired up — falls back to <paramref name="gpsHeading"/> /
    /// <paramref name="imuHeading"/> fusion exactly as before. Defaults to
    /// false, so existing callers keep their current behavior unchanged.</param>
    /// <returns>Final heading in degrees, normalized to 0–360.</returns>
    double FuseHeading(double gpsHeading, double imuHeading, bool imuValid,
                       double speedMs, double easting, double northing,
                       double ksxtHeading = 0, bool ksxtValid = false);

    /// <summary>
    /// Discard fix-to-fix history. Call on field close or GPS reconnect.
    /// </summary>
    void Reset();
}
