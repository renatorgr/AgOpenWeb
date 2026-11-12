// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Models.Configuration;

/// <summary>
/// How AutoSteer joins a new line with the tractor in the headland (GuidanceConfig.LineEntryMode;
/// fork only). The values are saved in the profiles and sent on the wire: keep them.
/// </summary>
public enum LineEntryMode
{
    /// <summary>Steer to the nearest point of the line, as AgOpenGPS.</summary>
    Nearest = 0,

    /// <summary>Join at the nearest point and offer the way to the start of the pass through the
    /// headland, with a sound and two buttons; the operator picks.</summary>
    Ask = 1,

    /// <summary>Drive through the headland to the start of the pass when the way fits.</summary>
    PassStart = 2,
}
