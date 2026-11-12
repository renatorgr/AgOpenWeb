// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;

namespace AgOpenWeb.Services.Pipeline;

/// <summary>
/// The offset passes of the active curve, kept until the track changes. Offsetting tests every
/// offset point against the curve, and a boundary curve has a point every metre or so (#450):
/// the displayed pass, the steered pass and the guidelines beside them were rebuilt every GPS
/// cycle. Now a pass is offset once, when the tractor moves to it. The lists returned are shared:
/// callers copy before changing them. A pass the offset removes altogether has no points (no pass
/// there); <see cref="CurveProcessing.ExtendPass"/> runs a pass on past the ends the offset kept.
/// </summary>
internal sealed class OffsetPassCache
{
    // The pass, the pass the tractor is nearest, and the guidelines either side of them.
    private const int Capacity = 128;

    private readonly object _lock = new();
    private readonly Dictionary<double, (List<Vec3> Points, double PercentRemoved, bool StartKept, bool EndKept, bool StartIsEnd, bool EndIsEnd)> _passes = new();
    private Models.Track.Track? _track;
    private List<Vec3>? _points;
    private int _count;
    private (Vec3 Center, double Radius)? _pivot;
    private double _minTurnRadius;
    // Where each closed pass does not fit (null: all of it fits), kept with the passes. Fork only.
    private readonly Dictionary<double, bool[]?> _unfit = new();
    private double _unfitTolerance;
    private BoundaryPolygon? _unfitSoftFence;

    /// <param name="minTurnRadius">No bend of a curve's pass is tighter than this (the U-turn radius,
    /// what the rig can turn); 0 leaves the passes as offset.</param>
    public (List<Vec3> Points, double PercentRemoved, bool StartKept, bool EndKept, bool StartIsEnd, bool EndIsEnd) Get(Models.Track.Track track, double distAway, double minTurnRadius = 0)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(track, _track) || !ReferenceEquals(track.Points, _points) || track.Points.Count != _count
                || minTurnRadius != _minTurnRadius)
            {
                _minTurnRadius = minTurnRadius;
                _passes.Clear();
                _unfit.Clear();
                _pivot = null;
                _track = track;
                _points = track.Points;
                _count = track.Points.Count;
            }
            if (_passes.TryGetValue(distAway, out var hit)) return hit;
            if (_passes.Count >= Capacity)
            {
                _passes.Clear();
                _unfit.Clear();
            }
            (List<Vec3> Points, double PercentRemoved, bool StartKept, bool EndKept, bool StartIsEnd, bool EndIsEnd) made;
            if (track.IsPivot)
            {
                // A water pivot's passes are circles about its centre, as AgOpenGPS builds them.
                var (center, radius) = _pivot ??= track.PivotCircle();
                made = (CurveProcessing.PivotPass(center, radius, distAway), 0, true, true, false, false);
            }
            else
            {
                // A corner tighter than the rig can turn (an inner pass at a field corner) becomes an
                // arc it can drive; pure pursuit cut it anyway, by up to 2 m.
                made = CurveProcessing.CurvePass(track.Points, distAway, track.IsClosed, minTurnRadius);
            }
            _passes[distAway] = made;
            return made;
        }
    }

    /// <summary>
    /// Where pass <paramref name="distAway"/> of a closed track does not fit
    /// (<see cref="CurveProcessing.PassUnfit"/>: nearer the reference than its offset by more than
    /// <paramref name="tolerance"/>), worked out once with the pass. Null when all of it fits, and
    /// for an open curve, a water pivot or the reference itself. With a soft outer boundary
    /// (<paramref name="softFence"/>) a stretch that goes out of the field fits
    /// (<see cref="CurveProcessing.UnfitInside"/>). Fork only.
    /// </summary>
    public bool[]? Unfit(Models.Track.Track track, double distAway, double minTurnRadius, double tolerance,
        BoundaryPolygon? softFence = null)
    {
        if (!track.IsClosed || track.IsPivot || track.Points.Count <= 2 || Math.Abs(distAway) < 0.01) return null;
        var pass = Get(track, distAway, minTurnRadius);
        lock (_lock)
        {
            if (tolerance != _unfitTolerance || !ReferenceEquals(softFence, _unfitSoftFence))
            {
                _unfit.Clear();
                _unfitTolerance = tolerance;
                _unfitSoftFence = softFence;
            }
            if (_unfit.TryGetValue(distAway, out var hit)) return hit;
            // Bends opened to the turn radius come nearer the reference round a corner's outside:
            // judged against the pass as offset, only when a point is near enough to ask.
            var made = CurveProcessing.PassUnfit(track.Points, pass.Points, distAway, tolerance);
            if (made != null && minTurnRadius > 0)
                made = CurveProcessing.PassUnfit(track.Points, pass.Points, distAway, tolerance,
                    CurveProcessing.CreateOffsetCurveWithInfo(track.Points, distAway, closed: true).Points);
            if (made != null && softFence != null)
                made = CurveProcessing.UnfitInside(pass.Points, made, softFence.IsPointInside);
            if (ReferenceEquals(track, _track)) _unfit[distAway] = made;
            return made;
        }
    }
}
