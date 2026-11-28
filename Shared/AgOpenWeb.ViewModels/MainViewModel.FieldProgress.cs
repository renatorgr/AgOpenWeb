// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services.Fields;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// MainViewModel partial: how much of the field is still to paint and how long it will take (fork only).
/// <list type="bullet">
/// <item>The ground still to paint is counted on the coverage map inside the field's boundary, less its
/// obstacles (<see cref="Services.Interfaces.ICoverageMapService.MeasureCoverage"/>), every
/// <see cref="FieldCountEverySeconds"/> s off the host loop. The status bar's "Left" was the headland's area
/// less every pass painted (headland passes, overlap and paint outside the fence too), which went below 0:
/// "Done 11.26 ha Left -14%" on Tapada da Casa (simulator, 23 Nov 2026).</item>
/// <item>The time left is that ground at the job's own pace (<see cref="JobPace"/>, sampled every second),
/// not at the instant speed times the width, which jumped with every change of speed.</item>
/// <item>An area selected on the map (<see cref="SetAreaSelection"/>) is counted with the field: its ground
/// inside the field, how much of that is still to paint and the time to paint it.</item>
/// </list>
/// </summary>
public partial class MainViewModel
{
    /// <summary>The field is counted every this many seconds.</summary>
    internal const int FieldCountEverySeconds = 3;

    /// <summary>The tractor counts as on the field within this distance of its outer boundary (a turn
    /// can take the pivot a little out).</summary>
    internal const double PaceFieldReachM = 30;

    /// <summary>Faster than this the instant rate (speed times the width) stands in for a pace the job
    /// doesn't have yet (AgOpenGPS: no estimate below 2 km/h).</summary>
    private const double InstantRateMinMps = 2 / 3.6;

    private Services.Interfaces.IUiTimer? _fieldProgressTimer;
    private readonly JobPace _jobPace = new();
    private int _fieldProgressTicks;
    private int _fieldCountInFlight;
    private int _fieldCountGeneration;
    private double _fieldInsideSqM = double.NaN;
    private double _fieldPaintedSqM = double.NaN;
    private double _selectionInsideSqM = double.NaN;
    private double _selectionPaintedSqM = double.NaN;
    private bool _fieldCountPending;

    /// <summary>A selection has at most this many corners (one tapped on the map has a few dozen).</summary>
    internal const int MaxSelectionCorners = 1000;

    /// <summary>Ground inside the field (boundary less obstacles) at the last count, m²; NaN before the
    /// first count and with no boundary.</summary>
    public double FieldInsideSqM => Volatile.Read(ref _fieldInsideSqM);

    /// <summary>Of that, the ground painted, m².</summary>
    public double FieldPaintedSqM => Volatile.Read(ref _fieldPaintedSqM);

    /// <summary>The ground inside the field still to paint, m²; NaN when not counted.</summary>
    public double FieldUnpaintedSqM
    {
        get
        {
            double inside = FieldInsideSqM, painted = FieldPaintedSqM;
            return double.IsFinite(inside) && double.IsFinite(painted) ? Math.Max(0, inside - painted) : double.NaN;
        }
    }

    /// <summary>The area selected on the map: its ground inside the field (less the obstacles), m²; NaN with
    /// no selection or before its first count.</summary>
    public double SelectionInsideSqM => Volatile.Read(ref _selectionInsideSqM);

    /// <summary>Of that, the ground still to paint, m².</summary>
    public double SelectionUnpaintedSqM
    {
        get
        {
            double inside = SelectionInsideSqM, painted = Volatile.Read(ref _selectionPaintedSqM);
            return double.IsFinite(inside) && double.IsFinite(painted) ? Math.Max(0, inside - painted) : double.NaN;
        }
    }

    /// <summary>The time to paint what is left of the selection, seconds, as <see cref="FieldTimeLeftSeconds"/>.</summary>
    public double SelectionTimeLeftSeconds => _jobPace.TimeLeftSeconds(SelectionUnpaintedSqM, InstantSqMPerSecond());

    /// <summary>Select an area on the map (its corners, field metres, three or more), or clear the selection
    /// (null, or fewer than three corners). It is counted at once, or as soon as the count running ends.</summary>
    public void SetAreaSelection(IReadOnlyList<Vec2>? corners)
    {
        Vec2[]? selection = null;
        if (corners != null && corners.Count >= 3 && corners.Count <= MaxSelectionCorners)
        {
            selection = new Vec2[corners.Count];
            for (int i = 0; i < selection.Length; i++)
            {
                var c = corners[i];
                if (!double.IsFinite(c.Easting) || !double.IsFinite(c.Northing)) { selection = null; break; }
                selection[i] = c;
            }
        }
        State.FieldTools.AreaSelection = selection;
        Volatile.Write(ref _selectionInsideSqM, double.NaN);
        Volatile.Write(ref _selectionPaintedSqM, double.NaN);
        if (selection != null && IsFieldOpen) StartFieldCount();
    }

    /// <summary>The job's pace, ha/hr; NaN until it has <see cref="JobPace.MinWorkSeconds"/> of work.</summary>
    public double JobPaceHaPerHour => _jobPace.PaceSqMPerSecond * 3600 / 10000;

    /// <summary>The time to paint what is left of the field, seconds: at the job's pace, or before it has one
    /// at the speed times the whole implement; NaN with no boundary or nothing to go on.</summary>
    public double FieldTimeLeftSeconds
    {
        get
        {
            return _jobPace.TimeLeftSeconds(FieldUnpaintedSqM, InstantSqMPerSecond());
        }
    }

    /// <summary>The speed times the whole implement, m²/s, above walking pace; else 0.</summary>
    private double InstantSqMPerSecond()
    {
        double speed = Math.Abs(State.Vehicle.Speed);
        return speed > InstantRateMinMps ? speed * Math.Max(0, ConfigStore.ActualToolWidth) : 0;
    }

    /// <summary>Start counting the field and timing the job. Called with the field open and its coverage
    /// loaded (next to <see cref="StartCoverageAutosave"/>).</summary>
    // Field session steps (MainViewModel.FieldSession.cs): started once coverage is loaded, stopped with the autosave.
    private Task OpenFieldProgress(FieldOpening o)
    {
        StartFieldProgress();
        return Task.CompletedTask;
    }

    private void CloseFieldProgress() => StopFieldProgress();

    private void StartFieldProgress()
    {
        var job = _jobService.ActiveJob;
        _jobPace.Start(job?.WorkSeconds ?? 0, job?.WorkPaintedSqM ?? 0);
        _fieldProgressTicks = 0;
        ForgetFieldCounts();
        if (_fieldProgressTimer != null) return;
        _fieldProgressTimer = _timerFactory.Create();
        _fieldProgressTimer.Interval = TimeSpan.FromSeconds(1);
        _fieldProgressTimer.Tick += OnFieldProgressTick;
        _fieldProgressTimer.Start();
    }

    /// <summary>Stop it; the numbers go back to unknown. Called where the coverage autosave stops.</summary>
    private void StopFieldProgress()
    {
        if (_fieldProgressTimer != null)
        {
            _fieldProgressTimer.Stop();
            _fieldProgressTimer.Tick -= OnFieldProgressTick;
            _fieldProgressTimer = null;
        }
        ForgetFieldCounts();
        _jobPace.Start(0, 0);
    }

    /// <summary>The counts back to unknown and the selection cleared (another field, or none): a count
    /// still running is dropped.</summary>
    private void ForgetFieldCounts()
    {
        _fieldCountGeneration++;
        _fieldCountPending = false;
        State.FieldTools.AreaSelection = null;
        Volatile.Write(ref _fieldInsideSqM, double.NaN);
        Volatile.Write(ref _fieldPaintedSqM, double.NaN);
        Volatile.Write(ref _selectionInsideSqM, double.NaN);
        Volatile.Write(ref _selectionPaintedSqM, double.NaN);
    }

    private void OnFieldProgressTick(object? sender, EventArgs e) => FieldProgressTick();

    /// <summary>One second: a sample of the job's pace, and every <see cref="FieldCountEverySeconds"/> a
    /// count of the field. Internal so tests can drive it without the timer.</summary>
    internal void FieldProgressTick()
    {
        if (!IsFieldOpen) return;
        double seconds = MotionClock.GetTimestamp() / (double)Clock.Current.Frequency;
        SampleJobPace(seconds);
        bool due = _fieldProgressTicks++ % FieldCountEverySeconds == 0;
        if (due || _fieldCountPending) StartFieldCount();
    }

    /// <summary>The pace's sample at <paramref name="seconds"/>, the job keeping what it has.</summary>
    internal void SampleJobPace(double seconds)
    {
        _jobPace.Sample(seconds, _coverageMapService.ActualAreaCovered, IsWorkingForPace());
        if (_jobService.ActiveJob is { } job)
        {
            job.WorkSeconds = _jobPace.WorkSeconds;
            job.WorkPaintedSqM = _jobPace.PaintedSqM;
        }
    }

    /// <summary>Moving, and on the field or near it (anywhere, with no boundary).</summary>
    private bool IsWorkingForPace()
    {
        if (Math.Abs(State.Vehicle.Speed) < JobPace.MovingMps) return false;
        var outer = State.Field.CurrentBoundary?.OuterBoundary;
        if (outer == null || !outer.IsValid) return true;
        double e = State.Vehicle.Easting, n = State.Vehicle.Northing;
        return outer.IsPointInside(e, n) || DistanceToRing(outer.Points, e, n) <= PaceFieldReachM;
    }

    private static double DistanceToRing(IReadOnlyList<Models.BoundaryPoint> pts, double e, double n)
    {
        double best = double.PositiveInfinity;
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing, len2 = dx * dx + dy * dy;
            double t = len2 > 0 ? Math.Clamp(((e - a.Easting) * dx + (n - a.Northing) * dy) / len2, 0, 1) : 0;
            double px = a.Easting + t * dx - e, py = a.Northing + t * dy - n;
            best = Math.Min(best, px * px + py * py);
        }
        return Math.Sqrt(best);
    }

    /// <summary>The field's rings as the count takes them: the outer boundary and every obstacle (each a
    /// hole: nothing is applied in one, drive-through or not); null with no boundary.</summary>
    internal IReadOnlyList<IReadOnlyList<Vec2>>? FieldRings()
    {
        var b = State.Field.CurrentBoundary;
        if (b?.OuterBoundary == null || !b.OuterBoundary.IsValid) return null;
        var rings = new List<IReadOnlyList<Vec2>> { RingOf(b.OuterBoundary.Points) };
        foreach (var inner in b.InnerBoundaries)
            if (inner.IsValid) rings.Add(RingOf(inner.Points));
        return rings;
    }

    private static Vec2[] RingOf(List<Models.BoundaryPoint> pts)
    {
        var ring = new Vec2[pts.Count];
        for (int i = 0; i < ring.Length; i++) ring[i] = new Vec2(pts[i].Easting, pts[i].Northing);
        return ring;
    }

    /// <summary>Count the field and the selection on a worker thread (one count at a time; asked for while
    /// one runs, the next tick starts it); a result is dropped when the field closed or opened again
    /// meanwhile, and the selection's when it changed.</summary>
    private void StartFieldCount()
    {
        var rings = FieldRings();
        var selection = State.FieldTools.AreaSelection;
        if (rings == null)
        {
            Volatile.Write(ref _fieldInsideSqM, double.NaN);
            Volatile.Write(ref _fieldPaintedSqM, double.NaN);
            if (selection == null) return;
        }
        if (Interlocked.CompareExchange(ref _fieldCountInFlight, 1, 0) != 0) { _fieldCountPending = true; return; }
        _fieldCountPending = false;
        int generation = _fieldCountGeneration;
        _ = Task.Run(() =>
        {
            try { CountField(rings, selection, generation); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Field] Counting the ground left to paint failed");
            }
            finally
            {
                Interlocked.Exchange(ref _fieldCountInFlight, 0);
            }
        });
    }

    /// <summary>The count itself: the field (when it has a boundary), then the selection kept to it.</summary>
    private void CountField(IReadOnlyList<IReadOnlyList<Vec2>>? rings, IReadOnlyList<Vec2>? selection, int generation)
    {
        if (rings != null)
        {
            var (inside, painted) = _coverageMapService.MeasureCoverage(rings);
            if (generation != Volatile.Read(ref _fieldCountGeneration)) return;
            Volatile.Write(ref _fieldInsideSqM, inside);
            Volatile.Write(ref _fieldPaintedSqM, painted);
        }
        if (selection == null) return;
        var (selInside, selPainted) = _coverageMapService.MeasureCoverage(new[] { selection }, rings);
        if (generation != Volatile.Read(ref _fieldCountGeneration)
            || !ReferenceEquals(selection, State.FieldTools.AreaSelection)) return;
        Volatile.Write(ref _selectionInsideSqM, selInside);
        Volatile.Write(ref _selectionPaintedSqM, selPainted);
    }

    /// <summary>Count the field and the selection now, on this thread (tests).</summary>
    internal void CountFieldNow() => CountField(FieldRings(), State.FieldTools.AreaSelection, _fieldCountGeneration);
}
