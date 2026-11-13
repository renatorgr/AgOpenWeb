// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Services.AutoSteer;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// Each change of a steering setting (what goes to the steer module, and Max Steer Angle) goes into
/// the log with its value before and after: "[Steer settings] Ackermann 90 -> 0". Fork only.
/// </summary>
public partial class MainViewModel
{
    private SteerSettingsLog? _steerSettingsLog;

    private void LogSteerSettingChanges() =>
        _steerSettingsLog = new SteerSettingsLog(ConfigStore,
            change => _logger.LogInformation("[Steer settings] {Change}", change));
}
