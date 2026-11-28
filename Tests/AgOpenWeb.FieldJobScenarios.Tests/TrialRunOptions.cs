// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>
/// TRIAL, weekend run only (not for merging). The runner armed on the test PC gives a run only its typed
/// settings (AGOPENWEB_JOB_SETTINGS), so a run's own harness options ride there as <c>harness.*</c> entries:
/// <c>harness.fromPass</c>, <c>harness.turns</c> and <c>harness.handTurns</c>. Before anything in this
/// assembly reads its environment they are moved to the variables the harness reads
/// (AGOPENWEB_SCENARIO_FROM_PASS, AGOPENWEB_SCENARIO_TURNS, AGOPENWEB_JOB_HAND_TURNS) and taken out of the
/// typed settings, so nothing of them is typed into AgOpenWeb.
/// </summary>
internal static class TrialRunOptions
{
    /// <summary>What was moved, for the report's story (empty when nothing was).</summary>
    internal static string Moved { get; private set; } = "";

    [ModuleInitializer]
    internal static void Init()
    {
        string typed = Environment.GetEnvironmentVariable("AGOPENWEB_JOB_SETTINGS") ?? "";
        if (!typed.Contains("harness.", StringComparison.Ordinal)) return;
        var keep = new List<string>();
        var moved = new List<string>();
        foreach (string x in typed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = x.IndexOf(':');
            string? variable = (colon > 0 ? x[..colon] : "") switch
            {
                "harness.fromPass" => "AGOPENWEB_SCENARIO_FROM_PASS",
                "harness.turns" => "AGOPENWEB_SCENARIO_TURNS",
                "harness.handTurns" => "AGOPENWEB_JOB_HAND_TURNS",
                _ => null,
            };
            if (variable == null) { keep.Add(x); continue; }
            Environment.SetEnvironmentVariable(variable, x[(colon + 1)..]);
            moved.Add(x);
        }
        Environment.SetEnvironmentVariable("AGOPENWEB_JOB_SETTINGS", string.Join(";", keep));
        Moved = string.Join(", ", moved);
    }
}
