// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

using AgOpenWeb.Models.Base;
using AgOpenWeb.Services.Track;

namespace AgOpenWeb.Services.Pipeline;

/// <summary>
/// The warning to slow down before a bend round a Go around obstacle (fork only). With AutoSteer
/// on the line, each cycle looks a few seconds ahead along the line steered; when a bend comes
/// within that and the tractor is faster than the bend can be followed at (it would cut the bend
/// toward the obstacle by more than the safety margin less <see cref="GoAroundSpeed.Reserve"/>:
/// <see cref="GoAroundSpeed.Cut"/>), the operator is told the speed to slow down to, once per
/// obstacle each time the tractor comes to it. It only warns: it never steers nor changes speed.
/// <para>
/// On a tester's field (22 Nov 2026) a tractor taking a bend round a hard Go around obstacle fast,
/// with a small margin, ran inside it, and the implement in the obstacle took AutoSteer off.
/// </para>
/// </summary>
public sealed class GoAroundSpeedWarning
{
    /// <summary>How far ahead a bend is looked for, seconds of travel, never under <see cref="MinLead"/>.</summary>
    public const double LeadSeconds = 6;

    /// <summary>The least distance ahead a bend is looked for, metres.</summary>
    public const double MinLead = 15;

    /// <summary>A point of the line this near the obstacle grown by the keep-out (or in it) is beside the obstacle, metres.</summary>
    public const double NearM = 1.0;

    /// <summary>The tractor must be this much faster than the safe speed to be warned, km/h.</summary>
    public const double ToleranceKmh = 0.5;

    // One obstacle's bend on the line steered: the obstacle grown by the keep-out, the points of the
    // whole bend, and the points beside the obstacle.
    private sealed record Zone(int Inner, IReadOnlyList<Vec2> Fence, int BendFrom, int BendTo, int NearFrom, int NearTo);

    private IReadOnlyList<Vec3>? _line;
    private double[] _stations = Array.Empty<double>();
    private readonly List<Zone> _zones = new();
    private int _seg = -1;

    // The cut at each speed (tenths of km/h), per obstacle and way, for this line and these settings.
    private readonly Dictionary<(int Inner, bool Forward, int Tenths), double> _cuts = new();
    private GoAroundSpeed.Follower _follower;

    // Obstacles already told about on this pass, each way: told again only after the tractor has left it.
    private readonly HashSet<(int Inner, bool Forward)> _told = new();
    private (object? Track, double DistAway) _pass;

    /// <summary>What a warning found, for the log.</summary>
    public readonly record struct Warning(int Inner, double AheadM, double Kmh, double Cut, double Allowed, int SafeKmh, double CutAtSafe)
    {
        /// <summary>What the operator is told.</summary>
        public string Notice => SafeKmh > 0
            ? $"Slow down to {SafeKmh} km/h for the bend round Inner {Inner + 1}"
            : $"Slow right down for the bend round Inner {Inner + 1}: the steering cuts it by more than the Safety margin even slowly (raise the margin)";

        /// <summary>The numbers behind it, for the log.</summary>
        public string LogLine => FormattableString.Invariant(
            $"Bend round Inner {Inner + 1} {AheadM:F0} m ahead at {Kmh:F1} km/h: the steering would cut it by {Cut:F2} m, {Allowed:F2} m allowed; ")
            + (SafeKmh > 0
                ? FormattableString.Invariant($"{SafeKmh} km/h cuts it by {CutAtSafe:F2} m")
                : "no speed from 2 km/h keeps inside it");
    }

    /// <summary>Forgets everything: AutoSteer off, no bend on the line steered.</summary>
    public void Reset()
    {
        _line = null;
        _zones.Clear();
        _cuts.Clear();
        _told.Clear();
        _pass = default;
    }

    /// <summary>
    /// The cycle's look: <paramref name="steered"/> is the line steered (the pass bent round its Go
    /// around obstacles by <paramref name="bend"/>, and moved for a front implement), the tractor's
    /// pivot at (<paramref name="e"/>, <paramref name="n"/>) heading <paramref name="headingRad"/>
    /// at <paramref name="kmh"/>. Returns the warning to give now, or null.
    /// </summary>
    public Warning? Check(object track, double distAway, ObstacleDetour.Bend bend, IReadOnlyList<Vec3> steered,
        double e, double n, double headingRad, double kmh, GoAroundSpeed.Follower follower, double margin)
    {
        if (!ReferenceEquals(track, _pass.Track) || distAway != _pass.DistAway)
        {
            _told.Clear();
            _pass = (track, distAway);
        }
        if (!ReferenceEquals(steered, _line)) Analyse(steered, bend);
        if (!_follower.Equals(follower))
        {
            _cuts.Clear();
            _follower = follower;
        }
        if (_zones.Count == 0 || kmh <= 0) return null;

        // A new line is searched whole once (a lap's loop has other near points), then walked from there.
        if (_seg < 0) _seg = GoAroundSpeed.NearestSegment(steered, e, n);
        var (s, lineHeading) = GoAroundSpeed.Locate(steered, _stations, ref _seg, e, n);
        bool forward = Math.Cos(headingRad - lineHeading) >= 0;
        double lead = Math.Max(MinLead, LeadSeconds * kmh / 3.6);
        double allowed = GoAroundSpeed.Allowed(margin);
        Warning? warning = null;
        foreach (var z in _zones)
        {
            double sFrom = _stations[z.BendFrom], sTo = _stations[z.BendTo];
            double sNearFrom = _stations[z.NearFrom], sNearTo = _stations[z.NearTo];
            // Past the obstacle: told again the next time the tractor comes to it.
            if (forward ? s > sNearTo + 1 : s < sNearFrom - 1)
            {
                _told.Remove((z.Inner, forward));
                continue;
            }
            double ahead = forward ? sFrom - s : s - sTo;
            if (ahead > lead || _told.Contains((z.Inner, forward)) || warning != null) continue;

            double cut = CutAt(z, forward, kmh);
            if (cut <= allowed) continue;
            int safe = GoAroundSpeed.SafeKmh(v => CutAt(z, forward, v), allowed, kmh - ToleranceKmh);
            if (safe > 0 && kmh <= safe + ToleranceKmh) continue;
            _told.Add((z.Inner, forward));
            warning = new Warning(z.Inner, Math.Max(0, ahead), kmh, cut, allowed, safe, safe > 0 ? CutAt(z, forward, safe) : 0);
        }
        return warning;
    }

    private double CutAt(Zone z, bool forward, double kmh)
    {
        int tenths = (int)Math.Round(kmh * 2) * 5; // half a km/h
        if (_cuts.TryGetValue((z.Inner, forward, tenths), out double cut)) return cut;
        cut = GoAroundSpeed.Cut(_line!, _stations, z.BendFrom, z.BendTo, z.Fence, forward, tenths / 10.0, _follower);
        _cuts[(z.Inner, forward, tenths)] = cut;
        return cut;
    }

    // Where each obstacle's bend is on the line steered: the points within the bend's reach of the
    // obstacle grown by the keep-out, and of those, the ones beside it (within NearM, or in it).
    private void Analyse(IReadOnlyList<Vec3> line, ObstacleDetour.Bend bend)
    {
        _line = line;
        _stations = GoAroundSpeed.Stations(line);
        _zones.Clear();
        _cuts.Clear();
        _seg = -1;
        if (line.Count < 2) return;
        foreach (var r in bend.Rounds)
        {
            if (r.Fence.Count < 3) continue;
            double minE = double.MaxValue, maxE = double.MinValue, minN = double.MaxValue, maxN = double.MinValue;
            foreach (var p in r.Fence)
            {
                minE = Math.Min(minE, p.Easting); maxE = Math.Max(maxE, p.Easting);
                minN = Math.Min(minN, p.Northing); maxN = Math.Max(maxN, p.Northing);
            }
            double grow = Math.Max(r.Reach, NearM);
            int bendFrom = -1, bendTo = -1, nearFrom = -1, nearTo = -1;
            for (int i = 0; i < line.Count; i++)
            {
                var p = line[i];
                if (p.Easting < minE - grow || p.Easting > maxE + grow || p.Northing < minN - grow || p.Northing > maxN + grow) continue;
                double d = ObstacleDetour.DistanceToRing(r.Fence, p.Easting, p.Northing);
                if (d <= r.Reach)
                {
                    if (bendFrom < 0) bendFrom = i;
                    bendTo = i;
                }
                if (d <= NearM || GeometryMath.IsPointInPolygon(r.Fence, p.Easting, p.Northing))
                {
                    if (nearFrom < 0) nearFrom = i;
                    nearTo = i;
                }
            }
            if (nearFrom < 0) continue;
            if (bendFrom < 0 || bendFrom > nearFrom) bendFrom = nearFrom;
            if (bendTo < nearTo) bendTo = nearTo;
            _zones.Add(new Zone(r.InnerIndex, r.Fence, bendFrom, bendTo, nearFrom, nearTo));
        }
    }
}
