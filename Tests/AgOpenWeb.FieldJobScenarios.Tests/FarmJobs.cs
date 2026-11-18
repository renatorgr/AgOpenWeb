// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>
/// Fork only. A farmer's own machines on his own fields as they are on the day, for a nightly run on his
/// computer. The fields are read from a folder there and never come into the repository:
/// <list type="bullet">
/// <item>AGOPENWEB_JOB_FIELDS: a folder of AgOpenWeb field folders (the data folder's <c>Fields</c>);</item>
/// <item>AGOPENWEB_JOB_FIELD_LIST: the fields to work, <c>;</c>-separated, in a fixed order: field i's jobs are R<i>i</i>1..R<i>i</i>6;</item>
/// <item>AGOPENWEB_JOB_PROFILES (optional): the data folder, whose <c>Vehicles</c> and <c>Tools</c> give the tractor's
/// and the boom's own settings (AGOPENWEB_JOB_VEHICLE, AGOPENWEB_JOB_TOOL name the profiles); without it the measures below.</item>
/// </list>
/// Each field is opened as it was saved (<see cref="JobSpec.FieldFolder"/>: its obstacles with their choices, its
/// headland line, a hard boundary, the skip mode) and worked on its active AB line, or its first AB line when the
/// active track is a curve. The rigs, the last digit of the code: 1 the Puma with the 8 m front boom (2/1/2/1/2 m);
/// 2 the Maxxum with the same boom; 3 the Puma with the semi-mounted no-till drill; 4 the Maxxum with the drill;
/// 5 the Puma with a mounted twin-disc fertiliser spreader at 12 m, 6 the same at 20 m (two sections, left and right).
/// </summary>
public static class FarmJobs
{
    public static string? FieldsRoot => Environment.GetEnvironmentVariable("AGOPENWEB_JOB_FIELDS");

    public static IReadOnlyList<string> FieldList =>
        (Environment.GetEnvironmentVariable("AGOPENWEB_JOB_FIELD_LIST") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The fields named in the list that cannot be worked (no folder, no field.geojson, no AB line), with why.</summary>
    public static readonly List<string> Skipped = new();

    public static IEnumerable<JobSpec> Jobs()
    {
        Skipped.Clear();
        string? root = FieldsRoot;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) yield break;
        var kit = Kit.Load();
        var list = FieldList;
        for (int i = 0; i < list.Count; i++)
        {
            string name = list[i], folder = Path.Combine(root, name);
            if (!File.Exists(Path.Combine(folder, "field.geojson"))) { Skipped.Add($"R{i + 1}x '{name}': no field.geojson in its folder"); continue; }
            var field = ScenarioField.FromFieldFolder(folder, name);
            string? track = AbTrack(folder, field);
            if (track == null) { Skipped.Add($"R{i + 1}x '{name}': no AB line in the field"); continue; }
            double headland = HeadlandWidth(folder);
            bool snake = SkipMode(folder) == 2;
            foreach (var rig in kit.Rigs())
            {
                var watch = snake ? new SkipWorkedJobs.SnakeWatch(careless: false) : null;
                yield return new JobSpec(
                    JobRun.F($"R{i + 1}{rig.Digit}"), $"{name}, {rig.Title}",
                    JobRun.F($"{rig.Story} The field as it was saved ({field.Inner.Count} obstacles, headland {headland:0.#} m, its own boundary and skip mode{(snake ? ": ignore worked tracks" : "")}), worked on its AB line '{track}'."),
                    name, track, rig.V, rig.T, Machines.PumaSys)
                {
                    FieldFolder = folder,
                    Obstacles = true,
                    HeadlandM = headland > 0 ? headland : 12,
                    MakeHeadland = r => FileHeadlandAsync(r, headland),
                    HeadlandSections = rig.HeadlandSections,
                    OffWhenOut = rig.OffWhenOut,
                    UTurnRadius = rig.Radius,
                    UTurnDistanceFromBoundary = rig.Distance,
                    UTurnStyle = rig.Style,
                    LookAheadMatchesValves = rig.LookAheadMatchesValves,
                    SpeedKmh = rig.SpeedKmh,
                    Extra = rig.Extra,
                    PassCheck = true,
                    // A corner of short passes beside the hard fence has no room to turn: start on a full-length pass.
                    StartChordM = 100,
                    StartChordOfLongest = 0.8,
                    DriveOnPastStartWarnings = true,
                    ExtraJudge = (sc, r, flag) =>
                    {
                        // AutoSteer engaged in the headland facing the field: a turn planned at the crossing the
                        // tractor is about to enter fails, and with a hard boundary the operator is told to take over.
                        if (r.StartWarnings.Count > 0)
                            flag(Severity.Bug, "TAKE-OVER-AT-START", JobRun.F($"AgOpenWeb told the operator to take over before the implement had reached the cultivated area on the first pass ({string.Join("; ", r.StartWarnings)}); the operator drove on, {(r.Turns.Count > 0 ? "and the turn at the far end was made" : "and no turn was made")}."));
                        // The snake check needs a turn with the implement's pass known (a job that ends on its first pass has none).
                        if (watch != null && r.Turns.Any(t => t.TruthPassBefore != int.MinValue)) watch.Judge(sc, r, flag);
                    },
                };
            }
        }
    }

    /// <summary>The headland line saved with the field; a field saved without one gets one from the whole boundary.</summary>
    private static async Task<bool> FileHeadlandAsync(JobRun r, double width)
    {
        if (await r.Rig.WaitUntilAsync(() => r.Rig.State.Field.HeadlandLine is { Count: > 2 }, 5000)) return true;
        r.S.Step(JobRun.F($"the field has no headland line: one made from the whole boundary, {(width > 0 ? width : 12):0.#} m"));
        await r.SendAsync("headland.wholeBoundary", JobRun.F($"{(width > 0 ? width : 12)}"));
        return true;
    }

    /// <summary>The active track when it is an AB line (<c>ActiveTrack.txt</c>), else the field's first AB line.</summary>
    private static string? AbTrack(string folder, ScenarioField field)
    {
        string active = "";
        try { active = File.ReadAllText(Path.Combine(folder, "ActiveTrack.txt")).Trim().TrimStart('﻿'); } catch (IOException) { }
        var ab = field.Tracks.Where(t => t.Points.Count == 2).ToList();
        return ab.FirstOrDefault(t => t.Name == active).Name ?? ab.FirstOrDefault().Name;
    }

    private static double HeadlandWidth(string folder)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "field.geojson")));
        foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
            if (f.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object
                && p.TryGetProperty("role", out var role) && role.GetString() == "headland-line"
                && p.TryGetProperty("moveDistance", out var d) && d.ValueKind == JsonValueKind.Number)
                return d.GetDouble();
        return 0;
    }

    private static int SkipMode(string folder)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "UTurnSkip.json")));
            return doc.RootElement.TryGetProperty("mode", out var m) ? m.GetInt32() : 0;
        }
        catch (Exception e) when (e is IOException or JsonException) { return 0; }
    }

    /// <summary>One machine of the farm with the settings it is worked with.</summary>
    public sealed record Rig(int Digit, string Title, string Story, VehicleSpec V, ToolSpec T,
        double Radius, double Distance, int? Style, bool OffWhenOut, bool HeadlandSections, bool LookAheadMatchesValves,
        double SpeedKmh, IReadOnlyList<string> Extra);

    /// <summary>
    /// The tractors and implements. The Puma and the boom come from the farmer's own AgOpenWeb profiles when
    /// AGOPENWEB_JOB_PROFILES names his data folder, so the job runs with what he set that day; the rest are
    /// his measures: the Maxxum (<see cref="Machines.Maxxum"/>), the boom 2.1 m ahead of the front axle,
    /// the drill (<see cref="Machines.DirettaCorsa300"/>, its pivot 0.5 m behind the lower links), and the
    /// spreader's discs 1.2 m behind the lower links.
    /// </summary>
    private sealed class Kit
    {
        public VehicleSpec Puma = Machines.Puma;
        public double PumaBoomHitch = 5.0, MaxxumBoomHitch = 4.8, PumaLowerLinks = 0.9;
        public double[] BoomCm = { 200, 100, 200, 100, 200 };
        // The boom's settings: U-turn, sections (AgOpenWeb's install values unless the profile says otherwise).
        public double Radius = 8, Distance = 2;
        public int Style;
        // Off outside boundary: on, as a new tool in the app (ToolConfig), unless the boom's profile says otherwise.
        public bool OffWhenOut = true, HeadlandSections = true;
        public double? LookOn, LookOff;
        public List<string> BoomExtra = new() { "uturn.extension:20" };
        // The Puma's own guidance gains (the vehicle profile's); none without it: AgOpenWeb's.
        public List<string> PumaGuidance = new();
        public string Source = "the measures (no profiles read)";

        public static Kit Load()
        {
            var k = new Kit();
            string? data = Environment.GetEnvironmentVariable("AGOPENWEB_JOB_PROFILES");
            if (string.IsNullOrEmpty(data)) return k;
            string vName = Environment.GetEnvironmentVariable("AGOPENWEB_JOB_VEHICLE") ?? "Puma 180 CVX";
            string tName = Environment.GetEnvironmentVariable("AGOPENWEB_JOB_TOOL") ?? "Barra pulverizador";
            string vPath = Path.Combine(data, "Vehicles", vName + ".json"), tPath = Path.Combine(data, "Tools", tName + ".json");
            var read = new List<string>();
            if (File.Exists(vPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(vPath));
                var v = doc.RootElement.GetProperty("vehicle");
                double D(string key, double dflt) => v.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : dflt;
                k.Puma = k.Puma with
                {
                    Name = "Case Puma 180 CVX (profile '" + vName + "')",
                    Wheelbase = D("wheelbase", k.Puma.Wheelbase),
                    TrackWidth = D("trackWidth", k.Puma.TrackWidth),
                    AntennaHeight = D("antennaHeight", k.Puma.AntennaHeight),
                    AntennaPivot = D("antennaPivot", k.Puma.AntennaPivot),
                    AntennaOffset = D("antennaOffset", k.Puma.AntennaOffset),
                    HitchLength = D("hitchLength", k.Puma.HitchLength),
                    MaxSteerAngle = D("maxSteerAngle", k.Puma.MaxSteerAngle),
                };
                k.PumaBoomHitch = D("frontHitchLength", k.PumaBoomHitch);
                k.PumaLowerLinks = D("threePointHitchLength", k.PumaLowerLinks);
                if (doc.RootElement.TryGetProperty("guidance", out var g))
                {
                    double G(string key, double dflt) => g.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : dflt;
                    bool pp = !g.TryGetProperty("isPurePursuit", out var ip) || ip.ValueKind != JsonValueKind.False;
                    k.PumaGuidance = new()
                    {
                        "autosteer.isStanleyMode:" + (pp ? "0" : "1"),
                        JobRun.F($"autosteer.steerResponseHold:{G("goalPointLookAheadHold", 3)}"),
                        JobRun.F($"autosteer.speedFactor:{G("goalPointLookAheadMult", 1.5)}"),
                        JobRun.F($"autosteer.acquireFactor:{G("goalPointAcquireFactor", 1.5)}"),
                        JobRun.F($"autosteer.integralGain:{G("purePursuitIntegralGain", 0)}"),
                        JobRun.F($"autosteer.stanleyAggressiveness:{G("stanleyDistanceErrorGain", 0.8)}"),
                        JobRun.F($"autosteer.stanleyOvershootReduction:{G("stanleyHeadingErrorGain", 1)}"),
                    };
                }
                read.Add("vehicle '" + vName + "'");
            }
            if (File.Exists(tPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(tPath));
                var root = doc.RootElement;
                var t = root.GetProperty("tool");
                double D(JsonElement o, string key, double dflt) => o.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : dflt;
                bool B(JsonElement o, string key, bool dflt) => o.TryGetProperty(key, out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : dflt;
                k.PumaBoomHitch += D(t, "workingPointFromHitch", 0);
                k.MaxxumBoomHitch += D(t, "workingPointFromHitch", 0);
                if (root.TryGetProperty("sections", out var s) && s.TryGetProperty("widths", out var w) && w.GetArrayLength() > 0)
                    k.BoomCm = w.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                k.OffWhenOut = B(t, "isSectionOffWhenOut", k.OffWhenOut);
                k.HeadlandSections = B(t, "isHeadlandSectionControl", k.HeadlandSections);
                k.LookOn = D(t, "lookAheadOn", 1.0);
                k.LookOff = D(t, "lookAheadOff", 0.5);
                k.BoomExtra = new() { JobRun.F($"tool.turnOffDelay:{D(t, "turnOffDelay", 0)}") };
                if (root.TryGetProperty("youTurn", out var y))
                {
                    k.Radius = D(y, "turnRadius", k.Radius);
                    k.Distance = D(y, "distanceFromBoundary", k.Distance);
                    k.Style = (int)D(y, "style", 0);
                    k.BoomExtra.Add(JobRun.F($"uturn.extension:{D(y, "extensionLength", 20)}"));
                    k.BoomExtra.Add(JobRun.F($"uturn.goAroundRadius:{D(y, "goAroundRadius", 6)}"));
                    k.BoomExtra.Add(JobRun.F($"uturn.goAroundMargin:{D(y, "goAroundMargin", 1)}"));
                    k.BoomExtra.Add(JobRun.F($"uturn.crossGap:{D(y, "crossGap", 0)}"));
                    k.BoomExtra.Add(JobRun.F($"uturn.entryMode:{(int)D(y, "entryMode", 0)}"));
                    k.BoomExtra.Add(JobRun.F($"uturn.entryStraight:{D(y, "entryStraight", 3)}"));
                }
                read.Add("tool '" + tName + "'");
            }
            if (read.Count > 0) k.Source = "the farmer's AgOpenWeb profiles (" + string.Join(", ", read) + ")";
            return k;
        }

        private ToolSpec Boom(double hitch) =>
            new(JobRun.F($"Front sprayer {BoomCm.Sum() / 100:0.#} m ({string.Join("/", BoomCm.Select(c => (c / 100).ToString("0.#", CultureInfo.InvariantCulture)))} m)"),
                "front", BoomCm.Sum() / 100, BoomCm.Length, HitchLength: hitch, SectionsCm: BoomCm);

        private IReadOnlyList<string> BoomSettings()
        {
            var x = new List<string>(BoomExtra);
            if (LookOn is double on) x.Add(JobRun.F($"tool.lookAheadOn:{on}"));
            if (LookOff is double off) x.Add(JobRun.F($"tool.lookAheadOff:{off}"));
            return x;
        }

        public IEnumerable<Rig> Rigs()
        {
            // With the profile's look-ahead the operator's own value is typed; without it the careful operator's.
            bool matchValves = LookOn == null;
            var boomExtra = BoomSettings();
            yield return new Rig(1, "Puma with the front boom",
                JobRun.F($"The Case Puma with the front boom, the boom's working line {PumaBoomHitch:0.0#} m ahead of the rear axle; settings from {Source}."),
                Puma, Boom(PumaBoomHitch), Radius, Distance, Style, OffWhenOut, HeadlandSections, matchValves, 9, PumaGuidance.Concat(boomExtra).ToList());
            yield return new Rig(2, "Maxxum with the front boom",
                JobRun.F($"The Case IH Maxxum 135 with the same boom, {MaxxumBoomHitch:0.0#} m ahead of the rear axle (2.1 m ahead of the front axle); the boom's settings as on the Puma, AgOpenWeb's guidance gains."),
                Machines.Maxxum, Boom(MaxxumBoomHitch), Radius, Distance, Style, OffWhenOut, HeadlandSections, matchValves, 9, boomExtra);
            // The drill: Keyhole turns, distance from boundary 2.8 m, extension 12.5 m (the suggested-settings page's,
            // as job J14); the radius the tractor allows: 4.3 m on the Maxxum, 5.4 m on the Puma.
            // The drill has no profile of its own here: Off outside boundary on, as a new tool in the app.
            var drillExtra = new List<string> { "uturn.extension:12.5" };
            yield return new Rig(3, "Puma with the no-till drill",
                JobRun.F($"The Case Puma with the Gaspardo Diretta Corsa 300 (semi-mounted 3 m, one section, a 5 cm gap between passes), its pivot {PumaLowerLinks + 0.5:0.0#} m behind the rear axle (0.5 m behind the lower links); radius 5.4 m, Keyhole, distance from boundary 2.8 m, 8 km/h."),
                Puma with { HitchLength = PumaLowerLinks + 0.5 }, Machines.DirettaCorsa300, 5.4, 2.8, 2, true, true, true, 8, PumaGuidance.Concat(drillExtra).ToList());
            yield return new Rig(4, "Maxxum with the no-till drill",
                "The Case IH Maxxum 135 with the drill as in job J14 (pivot 1.54 m behind the rear axle): radius 4.3 m, Keyhole, distance from boundary 2.8 m, 8 km/h.",
                Machines.Maxxum, Machines.DirettaCorsa300, 4.3, 2.8, 2, true, true, true, 8, drillExtra);
            foreach (var (digit, width) in new[] { (5, 12.0), (6, 20.0) })
                yield return new Rig(digit, JobRun.F($"Puma with the fertiliser spreader at {width:0} m"),
                    JobRun.F($"The Case Puma with a mounted twin-disc fertiliser spreader set to {width:0} m in two sections (left and right halves), the discs 1.2 m behind the lower links ({PumaLowerLinks + 1.2:0.0#} m behind the rear axle); the boom's U-turn settings, 10 km/h."),
                    Puma, new ToolSpec(JobRun.F($"Mounted fertiliser spreader {width:0} m (2 x {width / 2:0} m)"), "rear", width, 2, HitchLength: PumaLowerLinks + 1.2),
                    Radius, Distance, Style, OffWhenOut, HeadlandSections, true, 10, PumaGuidance.Append("uturn.extension:20").ToList());
        }
    }
}
