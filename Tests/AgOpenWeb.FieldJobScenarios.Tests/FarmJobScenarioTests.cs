// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>
/// Fork only. A farmer's own machines on his own fields, read from his computer (<see cref="FarmJobs"/>): ignored unless
/// AGOPENWEB_JOB_FIELDS and AGOPENWEB_JOB_FIELD_LIST name them, so CI and the nightly workflows skip it. Long, so
/// [Category("Scenario")]. AGOPENWEB_SCENARIO_ONLY=R11,R21 picks jobs (R1 = every rig on the first field);
/// AGOPENWEB_SCENARIO_REPORT=path.md.
/// </summary>
[TestFixture]
[Category("Scenario")]
[NonParallelizable]
public class FarmJobScenarioTests
{
    public const string Title = "Farm job scenarios: the farm's own tractors and implements on its own fields";
    public const string Intro =
        "The field job harness on the farm's fields as they are saved today (obstacles with their Go around / U-turn choices, the " +
        "headland line, a hard boundary, the skip mode), read from this computer, with the Puma and the Maxxum, the 8 m front boom, " +
        "the no-till drill and a mounted fertiliser spreader at 12 and 20 m. The careful operator, the simulated machine, the " +
        "emulated machine module and truth coverage of the field jobs; the implement is also measured against its pass. Job " +
        "codes R<field><rig> stay the same while the field list does, so two nights' reports can be diffed.";

    [Test]
    [CancelAfter(28_800_000)]
    public async Task Farm_jobs(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(FarmJobs.FieldsRoot) || FarmJobs.FieldList.Count == 0)
            Assert.Ignore("AGOPENWEB_JOB_FIELDS and AGOPENWEB_JOB_FIELD_LIST name no fields on this computer");
        if (!Directory.Exists(FarmJobs.FieldsRoot))
            Assert.Fail("AGOPENWEB_JOB_FIELDS is not a folder: " + FarmJobs.FieldsRoot);
        using var exclusive = FieldJobScenarioTests.OneRunAtATime();
        await Jobs.RunFixtureAsync(FarmJobs.Jobs().Select(j => ("Careful", j)), "farm-job-scenarios-report.md",
            j => j.ToMarkdown(Title, Intro + (FarmJobs.Skipped.Count == 0 ? "" :
                " Not run: " + string.Join("; ", FarmJobs.Skipped) + ".")), ct);
    }
}
