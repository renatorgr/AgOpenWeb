// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using NUnit.Framework;

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>
/// The farm jobs' random block without a rig (milliseconds): a seed is always the same job, and the field's copy
/// has the turned AB line and the swapped obstacles, the farmer's own folder untouched.
/// </summary>
[TestFixture]
[Category("Unit")]
public class FarmRandomJobTests
{
    private string _dir = "";

    [SetUp]
    public void SetUp() => _dir = Directory.CreateTempSubdirectory("farm-random-").FullName;

    [TearDown]
    public void TearDown() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Test]
    public void A_seed_draws_the_same_job_every_time()
    {
        var a = FarmJobs.Draw(2026112801, 5, i => i + 2);
        var b = FarmJobs.Draw(2026112801, 5, i => i + 2);
        Assert.That(b with { Swap = a.Swap }, Is.EqualTo(a));
        Assert.That(b.Swap, Is.EqualTo(a.Swap));
        Assert.That(a.Swap, Has.Count.EqualTo(a.FieldIndex + 2));
    }

    [Test]
    public void The_draws_stay_in_their_ranges_and_vary_over_a_night()
    {
        var draws = FarmJobs.SeedsFor(20261128, 99).Select(s => FarmJobs.Draw(s, 5, _ => 3)).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(draws.All(d => d.FieldIndex is >= 0 and < 5 && d.RigDigit is >= 1 and <= 6), Is.True);
            Assert.That(draws.All(d => d.AngleDeg is >= -90 and < 90 && d.SpreaderWidth is >= 10 and <= 28), Is.True);
            Assert.That(draws.All(d => d.Skip is not { } s || (s.Mode is >= 0 and <= 2 && s.Rows is >= 0 and <= 2 && (s.Mode == 0 || s.Rows >= 1))), Is.True);
            Assert.That(draws.Select(d => d.FieldIndex).Distinct().Count(), Is.EqualTo(5));
            Assert.That(draws.Select(d => d.RigDigit).Distinct().Count(), Is.EqualTo(6));
            Assert.That(draws.Count(d => d.AngleDeg != 0), Is.InRange(40, 80));
            Assert.That(draws.Count(d => !d.StartAtEdge), Is.InRange(50, 90));
            Assert.That(draws.Count(d => d.Swap.Any(x => x)), Is.GreaterThan(20));
        });
    }

    [Test]
    public void Seeds_are_read_as_typed_and_a_night_s_follow_its_date()
    {
        Assert.That(FarmJobs.ParseSeeds(" 2026112803, 7 "), Is.EqualTo(new[] { 2026112803, 7 }));
        Assert.Throws<InvalidOperationException>(() => FarmJobs.ParseSeeds("12,x"));
        Assert.That(FarmJobs.SeedsFor(20261128, 3), Is.EqualTo(new[] { 2026112801, 2026112802, 2026112803 }));
    }

    private string WriteField()
    {
        string src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(src);
        static JsonArray Ring(double lon, double lat, double d) => new(new JsonArray(
            new JsonArray(lon, lat, 0.0), new JsonArray(lon + d, lat, 0.0), new JsonArray(lon + d, lat + d, 0.0),
            new JsonArray(lon, lat + d, 0.0), new JsonArray(lon, lat, 0.0)));
        JsonObject Feature(string type, JsonNode coords, JsonObject props) =>
            new() { ["type"] = "Feature", ["geometry"] = new JsonObject { ["type"] = type, ["coordinates"] = coords }, ["properties"] = props };
        var doc = new JsonObject
        {
            ["type"] = "FeatureCollection",
            ["features"] = new JsonArray(
                Feature("Point", new JsonArray(-8.0, 39.0), new JsonObject { ["role"] = "metadata", ["originLatitude"] = 39.0, ["originLongitude"] = -8.0 }),
                Feature("Polygon", Ring(-8.0, 39.0, 0.01), new JsonObject { ["role"] = "outer-boundary", ["isHard"] = true }),
                Feature("Polygon", Ring(-7.998, 39.002, 0.0005), new JsonObject { ["role"] = "inner-boundary", ["atObstacle"] = "turn", ["isDriveThrough"] = false }),
                Feature("Polygon", Ring(-7.995, 39.005, 0.0005), new JsonObject { ["role"] = "inner-boundary", ["atObstacle"] = "goRound", ["isDriveThrough"] = false }),
                Feature("Polygon", Ring(-7.993, 39.007, 0.0005), new JsonObject { ["role"] = "inner-boundary", ["atObstacle"] = "driveThrough", ["isDriveThrough"] = true }),
                Feature("LineString", new JsonArray(new JsonArray(-7.995, 39.001, 0.0), new JsonArray(-7.995, 39.009, 0.0)),
                    new JsonObject { ["role"] = "track", ["name"] = "AB 0", ["trackType"] = 2 })),
        };
        File.WriteAllText(Path.Combine(src, "field.geojson"), doc.ToJsonString());
        File.WriteAllText(Path.Combine(src, "UTurnSkip.json"), "{ \"mode\": 2, \"rows\": 1 }");
        return src;
    }

    [Test]
    public void The_copy_has_the_turned_line_and_the_swapped_obstacles()
    {
        string src = WriteField(), dst = Path.Combine(_dir, "copy");
        string before = File.ReadAllText(Path.Combine(src, "field.geojson"));
        var (track, swapped) = FarmJobs.MakeRandomField(src, dst, "AB 0", 90, new[] { true, false, true });

        Assert.That(File.ReadAllText(Path.Combine(src, "field.geojson")), Is.EqualTo(before), "the farmer's own field is never changed");
        Assert.That(File.Exists(Path.Combine(dst, "UTurnSkip.json")), Is.True);
        Assert.That(track, Is.EqualTo("Random AB +90 deg"));
        Assert.That(swapped, Is.EqualTo(new[] { "Inner 1 U-turn to Go around" }), "a Drive through obstacle keeps its choice");

        var field = ScenarioField.FromFieldFolder(dst, "copy");
        var line = field.Tracks.Single(t => t.Name == track).Points;
        double mLon = 111320 * Math.Cos(39.005 * Math.PI / 180);
        double dE = (line[1].Lon - line[0].Lon) * mLon, dN = (line[1].Lat - line[0].Lat) * 111320;
        Assert.Multiple(() =>
        {
            Assert.That(Math.Sqrt(dE * dE + dN * dN), Is.EqualTo(200).Within(0.01));
            Assert.That(Math.Atan2(dE, dN) * 180 / Math.PI, Is.EqualTo(90).Within(1e-6), "the north-south line turned 90° clockwise runs east");
            Assert.That((line[0].Lat + line[1].Lat) / 2, Is.EqualTo(39.005).Within(1e-9));
            Assert.That((line[0].Lon + line[1].Lon) / 2, Is.EqualTo(-7.995).Within(1e-9));
            Assert.That(field.Tracks.Any(t => t.Name == "AB 0"), Is.True, "the field's own line stays");
        });

        var props = JsonNode.Parse(File.ReadAllText(Path.Combine(dst, "field.geojson")))!["features"]!.AsArray()
            .Select(f => f!["properties"]!).Where(p => (string?)p["role"] == "inner-boundary").ToList();
        Assert.That(props.Select(p => (string?)p["atObstacle"]), Is.EqualTo(new[] { "goRound", "goRound", "driveThrough" }));
    }

    [Test]
    public void No_angle_works_the_field_s_own_line()
    {
        string src = WriteField(), dst = Path.Combine(_dir, "copy");
        var (track, swapped) = FarmJobs.MakeRandomField(src, dst, "AB 0", 0, Array.Empty<bool>());
        Assert.That(track, Is.EqualTo("AB 0"));
        Assert.That(swapped, Is.Empty);
        Assert.That(ScenarioField.FromFieldFolder(dst, "copy").Tracks, Has.Count.EqualTo(1));
    }
}
