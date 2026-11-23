// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Services.Fields;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// The job's pace (<see cref="JobPace"/>): ground painted per second of work, so the time left is what is left
/// at the pace the job has gone, turns included, and does not jump with the speed. Stops and gaps are not work;
/// a job's coverage loading is not ground painted. Fork only.
/// </summary>
[TestFixture]
public class JobPaceTests
{
    /// <summary>A 12 m boom at 8 km/h paints 26.7 m² a second.</summary>
    private const double Boom = 12, Speed = 8 / 3.6;

    [Test]
    public void Passes_and_turns_give_the_pace_of_the_whole_job()
    {
        var pace = new JobPace();
        pace.Start(0, 0);
        double t = 0, painted = 0;
        pace.Sample(t, painted, true);
        // Ten rounds of a 60 s pass and a 20 s turn (sections off).
        for (int round = 0; round < 10; round++)
        {
            for (int s = 0; s < 60; s++) { t += 1; painted += Boom * Speed; pace.Sample(t, painted, true); }
            for (int s = 0; s < 20; s++) { t += 1; pace.Sample(t, painted, true); }
        }

        Assert.Multiple(() =>
        {
            Assert.That(pace.WorkSeconds, Is.EqualTo(800).Within(1e-9));
            Assert.That(pace.PaceSqMPerSecond, Is.EqualTo(Boom * Speed * 60 / 80).Within(1e-9),
                "three quarters of the time painting");
            Assert.That(pace.TimeLeftSeconds(10000, Boom * Speed), Is.EqualTo(10000 / (Boom * Speed * 0.75)).Within(1e-6),
                "1 ha at the job's pace, not at the speed times the width");
        });
    }

    [Test]
    public void Standing_still_and_a_gap_are_not_work()
    {
        var pace = new JobPace();
        pace.Start(0, 0);
        pace.Sample(0, 0, true);
        pace.Sample(1, 20, true);
        pace.Sample(2, 20, false);   // stopped to refill
        pace.Sample(3, 20, false);
        pace.Sample(30, 40, true);   // 27 s with no sample: the tablet slept

        Assert.Multiple(() =>
        {
            Assert.That(pace.WorkSeconds, Is.EqualTo(1));
            Assert.That(pace.PaintedSqM, Is.EqualTo(20));
        });
    }

    [Test]
    public void A_job_opened_again_goes_on_from_its_own_numbers_and_its_coverage_is_not_work()
    {
        var pace = new JobPace();
        pace.Start(3600, 20000);         // the job had 2 ha in an hour
        pace.Sample(100, 20000, true);   // its coverage on the map: the first sample
        pace.Sample(101, 20020, true);

        Assert.Multiple(() =>
        {
            Assert.That(pace.WorkSeconds, Is.EqualTo(3601));
            Assert.That(pace.PaintedSqM, Is.EqualTo(20020));
            Assert.That(pace.PaceSqMPerSecond * 3600 / 10000, Is.EqualTo(2.0).Within(0.01), "2 ha/hr at once");
        });
    }

    [Test]
    public void Coverage_loading_between_two_samples_is_not_ground_painted()
    {
        var pace = new JobPace();
        pace.Start(0, 0);
        pace.Sample(0, 0, true);
        pace.Sample(1, 50000, true); // 5 ha in a second: another job's coverage loaded

        Assert.That(pace.PaintedSqM, Is.Zero);
    }

    [Test]
    public void The_applied_area_deleted_starts_the_pace_again()
    {
        var pace = new JobPace();
        pace.Start(600, 10000);
        pace.Sample(0, 10000, true);
        pace.Sample(1, 0, true);

        Assert.Multiple(() =>
        {
            Assert.That(pace.WorkSeconds, Is.Zero);
            Assert.That(pace.PaintedSqM, Is.Zero);
        });
    }

    [Test]
    public void Before_two_minutes_of_work_the_time_left_is_at_the_speed_and_width()
    {
        var pace = new JobPace();
        pace.Start(0, 0);
        pace.Sample(0, 0, true);
        for (int s = 1; s <= 60; s++) pace.Sample(s, s * Boom * Speed, true);

        Assert.Multiple(() =>
        {
            Assert.That(pace.PaceSqMPerSecond, Is.NaN, "a minute is not a pace yet");
            Assert.That(pace.TimeLeftSeconds(10000, Boom * Speed), Is.EqualTo(10000 / (Boom * Speed)).Within(1e-6));
            Assert.That(pace.TimeLeftSeconds(10000, 0), Is.NaN, "standing, with no pace: not known");
            Assert.That(pace.TimeLeftSeconds(0, 0), Is.Zero, "nothing left");
        });
    }

    /// <summary>The ask (23 Nov 2026): at a nearly steady speed, with the small changes a slope makes, the
    /// time left jumped. 8 km/h ± 1 km/h, a reading a second, 10 ha to do: from ten minutes in, the time the
    /// job will end (now plus the time left) by the speed times the width swings by more than 10 minutes;
    /// at the job's pace it moves by under a minute.</summary>
    [Test]
    public void At_a_speed_that_wobbles_the_time_left_holds_steady()
    {
        var pace = new JobPace();
        pace.Start(0, 0);
        var rng = new Random(3);
        double t = 0, painted = 0, left = 100000;
        pace.Sample(t, painted, true);
        double minInstant = double.MaxValue, maxInstant = 0, minPace = double.MaxValue, maxPace = 0;
        for (int s = 0; s < 1800; s++)
        {
            double v = (8 + (rng.NextDouble() * 2 - 1)) / 3.6;
            t += 1; painted += Boom * v; left -= Boom * v;
            pace.Sample(t, painted, true);
            if (s < 600) continue;
            double instantEnd = t + left / (Boom * v), paceEnd = t + pace.TimeLeftSeconds(left, Boom * v);
            minInstant = Math.Min(minInstant, instantEnd); maxInstant = Math.Max(maxInstant, instantEnd);
            minPace = Math.Min(minPace, paceEnd); maxPace = Math.Max(maxPace, paceEnd);
        }

        Assert.Multiple(() =>
        {
            Assert.That(maxInstant - minInstant, Is.GreaterThan(10 * 60), "the old estimate's swing");
            Assert.That(maxPace - minPace, Is.LessThan(60), "at the job's pace");
        });
        TestContext.Out.WriteLine($"end time swing: speed x width {(maxInstant - minInstant) / 60:F1} min, job pace {(maxPace - minPace):F0} s");
    }
}
