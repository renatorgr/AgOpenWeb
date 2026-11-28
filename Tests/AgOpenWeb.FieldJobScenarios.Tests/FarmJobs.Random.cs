// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>
/// Fork only. The farm jobs' random block: a few jobs a night on the same fields and machines, each drawn from a
/// seed, so the app is driven on ways the fixed jobs (R codes) never take. The fixed jobs stay as they are, to see
/// what changed from one night to the next; these find what no fixed job reaches. A job draws, in this order: the
/// field, the rig (1-6; the spreader's width 10-28 m), the AB line's angle (the field's own, or turned by up to 90°
/// about its middle), the start (the first pass from the edge, as the fixed jobs, or any pass a start can be made
/// on, either way), the skip button, and for each obstacle whether its U-turn / Go around choice is swapped.
/// The changes are made in a copy of the field folder (never in the farmer's own). The seed is in the job's title
/// and story: AGOPENWEB_JOB_RANDOM_SEEDS=seed runs that job again, the same in every way.
/// <list type="bullet">
/// <item>AGOPENWEB_JOB_RANDOM: how many jobs (0-99; none without it);</item>
/// <item>AGOPENWEB_JOB_RANDOM_SEED (optional): the night's base, its jobs' seeds base×100+1..N; by default today's
/// date (UTC, yyyyMMdd), so every night draws other jobs;</item>
/// <item>AGOPENWEB_JOB_RANDOM_SEEDS (optional): <c>,</c>-separated seeds to run again, in place of the two above.</item>
/// </list>
/// The jobs are X1..XN in the order of their seeds; the farm's other variables (fields, profiles, settings typed on
/// top) are read as for the fixed jobs, and AGOPENWEB_JOB_SKIP is used where a job keeps the field's skip.
/// </summary>
public static partial class FarmJobs
{
    /// <summary>The seeds of this run's random jobs (none when neither AGOPENWEB_JOB_RANDOM nor _SEEDS asks for any).</summary>
    public static IReadOnlyList<int> RandomSeeds()
    {
        string typed = (Environment.GetEnvironmentVariable("AGOPENWEB_JOB_RANDOM_SEEDS") ?? "").Trim();
        if (typed.Length > 0) return ParseSeeds(typed);
        string count = (Environment.GetEnvironmentVariable("AGOPENWEB_JOB_RANDOM") ?? "").Trim();
        if (count.Length == 0) return Array.Empty<int>();
        if (!int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n > 99)
            throw new InvalidOperationException($"AGOPENWEB_JOB_RANDOM: '{count}' is not a number of jobs from 0 to 99.");
        string baseText = (Environment.GetEnvironmentVariable("AGOPENWEB_JOB_RANDOM_SEED") ?? "").Trim();
        int baseSeed;
        if (baseText.Length == 0) baseSeed = int.Parse(DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        else if (!int.TryParse(baseText, NumberStyles.None, CultureInfo.InvariantCulture, out baseSeed) || baseSeed > 21_474_835)
            throw new InvalidOperationException($"AGOPENWEB_JOB_RANDOM_SEED: '{baseText}' is not a whole number up to 21474835.");
        return SeedsFor(baseSeed, n);
    }

    /// <summary>A night's seeds: base×100 + 1..<paramref name="count"/> (20261128 gives 2026112801, 2026112802, ...).</summary>
    public static IReadOnlyList<int> SeedsFor(int baseSeed, int count) =>
        Enumerable.Range(1, count).Select(i => baseSeed * 100 + i).ToList();

    /// <summary>AGOPENWEB_JOB_RANDOM_SEEDS: whole numbers split by <c>,</c>; anything else stops the run, as the
    /// jobs asked for again would otherwise be other jobs.</summary>
    public static IReadOnlyList<int> ParseSeeds(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s =>
            int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v
                : throw new InvalidOperationException($"AGOPENWEB_JOB_RANDOM_SEEDS: '{s}' is not a seed (whole numbers split by ',').")).ToList();

    /// <summary>What one seed drew. <see cref="Skip"/> null: the field's own skip button (or AGOPENWEB_JOB_SKIP).
    /// <see cref="Swap"/>: one per obstacle, in the field's order: its U-turn / Go around choice swapped.</summary>
    public sealed record RandomDraw(int Seed, int FieldIndex, int RigDigit, double SpreaderWidth, int AngleDeg,
        bool StartAtEdge, double StartFraction, bool StartAgainst, (int Mode, int Rows)? Skip, IReadOnlyList<bool> Swap);

    /// <summary>The draw of <paramref name="seed"/>, always in the same order, so a seed is the same job on any
    /// computer and any night (System.Random with a seed is the same sequence on every .NET).</summary>
    public static RandomDraw Draw(int seed, int fieldCount, Func<int, int> obstaclesOf)
    {
        var rnd = new Random(seed);
        int field = rnd.Next(fieldCount);
        int rig = rnd.Next(1, 7);
        double width = 10 + 2 * rnd.Next(0, 10);
        int angle = rnd.NextDouble() < 0.4 ? 0 : rnd.Next(-90, 90);
        bool edge = rnd.NextDouble() < 0.3;
        double fraction = rnd.NextDouble();
        bool against = rnd.Next(2) == 1;
        (int, int)? skip = null;
        if (rnd.NextDouble() >= 0.3)
        {
            int mode = rnd.Next(3), rows = rnd.Next(0, 3);
            skip = (mode, mode != 0 ? Math.Max(1, rows) : rows);
        }
        var swap = Enumerable.Range(0, obstaclesOf(field)).Select(_ => rnd.NextDouble() < 0.25).ToList();
        return new RandomDraw(seed, field, rig, width, angle, edge, edge ? 0 : fraction, !edge && against, skip, swap);
    }

    public static IEnumerable<JobSpec> RandomJobs()
    {
        Skipped.Clear();
        string? root = FieldsRoot;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) yield break;
        var seeds = RandomSeeds();
        var list = FieldList;
        if (list.Count == 0 || seeds.Count == 0) yield break;
        var kit = Kit.Load();
        var typed = TypedOnTop;
        var skipTyped = SkipTyped;
        string forThisRun = typed.Count > 0 ? " For this run the operator typed on top: " + string.Join(", ", typed.Select(Shown)) + "." : "";
        for (int n = 0; n < seeds.Count; n++)
        {
            int seed = seeds[n];
            var d = Draw(seed, list.Count, i => InnerCount(Path.Combine(root, list[i])));
            string code = JobRun.F($"X{n + 1}"), name = list[d.FieldIndex], folder = Path.Combine(root, name);
            if (!File.Exists(Path.Combine(folder, "field.geojson"))) { Skipped.Add($"{code} (seed {seed}) '{name}': no field.geojson in its folder"); continue; }
            string? baseTrack = AbTrack(folder, ScenarioField.FromFieldFolder(folder, name));
            if (baseTrack == null) { Skipped.Add($"{code} (seed {seed}) '{name}': no AB line in the field"); continue; }
            string copy = Path.Combine(Path.GetTempPath(), "agopenweb-random-jobs", seed.ToString(CultureInfo.InvariantCulture), name);
            var (track, swapped) = MakeRandomField(folder, copy, baseTrack, d.AngleDeg, d.Swap);
            var field = ScenarioField.FromFieldFolder(copy, name);
            var rig = d.RigDigit <= 4 ? kit.Rigs().First(r => r.Digit == d.RigDigit) : kit.Spreader(d.RigDigit, d.SpreaderWidth);
            var skip = d.Skip ?? skipTyped;
            bool snake = (skip?.Mode ?? SkipMode(copy)) == 2;
            double headland = HeadlandWidth(copy);
            string drawn = JobRun.F($"Random job, seed {seed} (AGOPENWEB_JOB_RANDOM_SEEDS={seed} runs it again): ")
                + (d.AngleDeg == 0 ? JobRun.F($"the field's AB line '{track}'") : JobRun.F($"the AB line '{baseTrack}' turned {d.AngleDeg:+0;-0}° about its middle ('{track}')"))
                + "; " + (d.StartAtEdge ? "the start on the first pass from the edge, as the fixed jobs"
                    : JobRun.F($"the start {d.StartFraction * 100:0} % of the way across the passes a start can be made on, {(d.StartAgainst ? "against" : "along")} A to B"))
                + "; " + (d.Skip is { } s ? JobRun.F($"the skip button set to mode {s.Mode}, {s.Rows} row(s)") : skipTyped is { } t ? JobRun.F($"the skip button at AGOPENWEB_JOB_SKIP's mode {t.Mode}, {t.Rows} row(s)") : "the field's own skip button")
                + "; " + (swapped.Count == 0 ? JobRun.F($"the {field.Inner.Count} obstacle(s) as saved") : "obstacles swapped: " + string.Join(", ", swapped))
                + ".";
            yield return MakeJob(code, name, copy, track, headland, rig, skip, typed,
                drawn + JobRun.F($" The field otherwise as it was saved ({field.Inner.Count} obstacles, headland {headland:0.#} m, its own boundary{(snake ? "; ignore worked tracks" : "")}).") + forThisRun,
                snake) with
            {
                Title = JobRun.F($"{name}, {rig.Title}, seed {seed}"),
                RandomStart = d.StartAtEdge ? null : (d.StartFraction, d.StartAgainst, snake),
            };
        }
    }

    /// <summary>How many obstacles the field folder's <c>field.geojson</c> has (0 without one).</summary>
    private static int InnerCount(string folder)
    {
        string path = Path.Combine(folder, "field.geojson");
        if (!File.Exists(path)) return 0;
        var doc = JsonNode.Parse(File.ReadAllText(path));
        return (doc?["features"] as JsonArray)?.Count(f => (string?)f?["properties"]?["role"] == "inner-boundary") ?? 0;
    }

    /// <summary>
    /// A random job's field: the files of <paramref name="src"/> the jobs open (as <c>OpenFieldFolderAsync</c> copies
    /// them) copied to <paramref name="dst"/>, its <c>field.geojson</c> changed: with an angle, an AB line named
    /// "Random AB +37 deg" turned that much (clockwise) about the middle of <paramref name="baseTrack"/>, 200 m long;
    /// each obstacle <paramref name="swap"/> marks, chosen as U-turn or Go around, given the other choice
    /// (Drive through ones stay). Returns the track to work and what was swapped, for the story.
    /// </summary>
    public static (string Track, List<string> Swapped) MakeRandomField(string src, string dst, string baseTrack, int angleDeg, IReadOnlyList<bool> swap)
    {
        if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);
        Directory.CreateDirectory(dst);
        foreach (var f in new[] { "field.origin", "HeadlandSegments.json", "UTurnSkip.json", "TramConfig.json" })
        {
            string from = Path.Combine(src, f);
            if (File.Exists(from)) File.Copy(from, Path.Combine(dst, f));
        }
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(src, "field.geojson")))!;
        var features = (JsonArray)doc["features"]!;
        string track = baseTrack;
        if (angleDeg != 0)
        {
            var line = features.First(f => (string?)f?["properties"]?["role"] == "track" && (string?)f?["properties"]?["name"] == baseTrack)!;
            var pts = (JsonArray)line["geometry"]!["coordinates"]!;
            double lonA = (double)pts[0]![0]!, latA = (double)pts[0]![1]!, lonB = (double)pts[^1]![0]!, latB = (double)pts[^1]![1]!;
            double lat = (latA + latB) / 2, lon = (lonA + lonB) / 2, mLon = 111320 * Math.Cos(lat * Math.PI / 180);
            double h = Math.Atan2((lonB - lonA) * mLon, (latB - latA) * 111320) + angleDeg * Math.PI / 180;
            h = (h % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
            double dLon = 100 * Math.Sin(h) / mLon, dLat = 100 * Math.Cos(h) / 111320;
            track = JobRun.F($"Random AB {angleDeg:+0;-0} deg");
            features.Add(new JsonObject
            {
                ["type"] = "Feature",
                ["geometry"] = new JsonObject
                {
                    ["type"] = "LineString",
                    ["coordinates"] = new JsonArray(new JsonArray(lon - dLon, lat - dLat, h), new JsonArray(lon + dLon, lat + dLat, h)),
                },
                ["properties"] = new JsonObject
                {
                    ["role"] = "track", ["name"] = track, ["trackType"] = 2, ["isClosed"] = false,
                    ["noPassOffset"] = false, ["nudgeDistance"] = 0, ["isVisible"] = true,
                },
            });
        }
        var swapped = new List<string>();
        int j = 0;
        foreach (var f in features)
        {
            if ((string?)f?["properties"]?["role"] != "inner-boundary") continue;
            var p = (JsonObject)f!["properties"]!;
            string at = (string?)p["atObstacle"] ?? (p["isDriveThrough"]?.GetValue<bool>() == true ? "driveThrough" : "turn");
            if (j < swap.Count && swap[j] && at is "turn" or "goRound")
            {
                string to = at == "turn" ? "goRound" : "turn";
                p["atObstacle"] = to;
                p["isDriveThrough"] = false;
                swapped.Add(JobRun.F($"Inner {j + 1} {(at == "turn" ? "U-turn" : "Go around")} to {(to == "turn" ? "U-turn" : "Go around")}"));
            }
            j++;
        }
        File.WriteAllText(Path.Combine(dst, "field.geojson"), doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return (track, swapped);
    }
}
