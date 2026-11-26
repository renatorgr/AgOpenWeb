// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Services.Geometry;

namespace AgOpenWeb.Services.Track;

/// <summary>
/// The one place a pass is bent round the obstacles chosen as <see cref="ObstacleAction.GoRound"/>,
/// shared by the GPS pipeline (the line steered, the line shown, the light bar) and the U-turn
/// planner (the pass walked to the turn, the pass the turn lands on): every one of them sees the
/// same bent pass, so a turn is never planned from a line the tractor does not drive. The
/// geometry is <see cref="ObstacleDetour"/>; the settings come from the configuration store.
/// <para>
/// It also remembers which side the tractor is going round each obstacle (a pin): while the
/// tractor is beside an obstacle and over on that side, a nudge, a snap or a changed setting
/// bends the new pass round the same side. A tractor that gets to the other side of the
/// obstacle (a U-turn next to it onto the neighbouring pass) lets the pin go, since a bend round
/// the pinned side would then cross the obstacle in front of it. Once the tractor has driven on
/// past the obstacle (<see cref="PassedSlack"/>) its pin holds only the pass being driven, so the
/// line under the tractor never changes, and no other pass: kept for every pass until the bend's
/// own reach was behind (84 m on a tester's field), it was still held after the U-turn at the end
/// of the pass, on a pass with room only on the other side (fork only). An obstacle with no room
/// (<see cref="ObstacleDetour.Bend.Blocked"/>) gets a U-turn before it on this pass, from the
/// planner; the pipeline tells the operator.
/// </para>
/// </summary>
public sealed class GoAroundPasses
{
    private readonly ConfigurationStore _config;
    private readonly IPolygonOffsetService _offset;
    private readonly ObstacleDetour _detour;

    // The side the tractor is going round an obstacle, kept while it is beside it and on that side:
    // the obstacle's centre and the pass's heading alongside it tell which side the tractor is on.
    // PassHeading: the way the pass runs by the obstacle (PassHeadingBy), and From and To how far the
    // grown obstacle reaches along it, back and on from its centre.
    // Passed: the tractor has driven on past it, and the pin holds only the pass being driven.
    // Outline: the obstacle's own outline when the pin was taken (BoundaryPolygon.OutlineStamp; 0
    // when the field was not known), since a pin is kept by the obstacle's place in the field's list.
    private sealed record Pin(int Side, IReadOnlyList<Vec2> Fence, double Reach, double CentreE, double CentreN, double Heading,
        double PassHeading, double From, double To, long Outline, bool Passed = false);

    // A pin lets go when the tractor is this far over on the other side of the obstacle's centre.
    private const double SideSlack = 0.5;

    /// <summary>
    /// An obstacle is passed when all of it, grown, is this far behind the tractor along the pass,
    /// metres, plus how far the rig reaches behind the pivot (<see cref="RigBehind"/>): the whole
    /// rig is past it then.
    /// </summary>
    public const double PassedSlack = 2.0;

    private readonly Dictionary<int, Pin> _pins = new();
    private Models.Track.Track? _pinsTrack;
    // The pass being driven (its offset from the track), and the pins for it: every one.
    private double _pinsDistAway = double.NaN;
    private IReadOnlyDictionary<int, int>? _pinSides;
    // The pins for any other pass: those of obstacles not passed yet.
    private IReadOnlyDictionary<int, int>? _otherPassPinSides;
    // The field of the last bend asked for: a pin taken on that bend is of an obstacle of it.
    private Boundary? _field;

    // The Go around obstacles grown by half the tractor's track, for the straight-ahead check.
    private (Boundary? Boundary, long Stamp, double Grow, List<(int Inner, List<Vec2> Ring)> List) _ahead = (null, 0, 0, new());

    public GoAroundPasses(ConfigurationStore config, IPolygonOffsetService offset)
    {
        _config = config;
        _offset = offset;
        _detour = new ObstacleDetour(offset);
    }

    /// <summary>The bend the tractor was steered along last (null: none on the current pass).</summary>
    public ObstacleDetour.Bend? Current { get; private set; }

    /// <summary>
    /// The Go around obstacles on the pass steered now with no room to go round them: the U-turn
    /// planner treats them as U-turn obstacles on this pass.
    /// </summary>
    public IReadOnlyList<int> CurrentBlocked => Current?.Blocked ?? Array.Empty<int>();

    /// <summary>Whether the field has an obstacle chosen as Go around.</summary>
    public static bool HasGoRound(Boundary? boundary)
    {
        if (boundary == null) return false;
        foreach (var inner in boundary.InnerBoundaries)
            if (inner.IsValid && inner.AtObstacle == ObstacleAction.GoRound) return true;
        return false;
    }

    /// <summary>How far the pass keeps from a Go around obstacle with the settings now.</summary>
    public double KeepOut
    {
        get
        {
            var c = _config;
            return ObstacleDetour.KeepOutFor(c.ActualToolWidth, c.Tool.Offset, c.Vehicle.TrackWidth, Margin);
        }
    }

    /// <summary>
    /// The least safety margin, metres: the room the follower needs on a bend. On the scenario
    /// field the tractor holds a bent line to about 0.25 m, so a margin of nothing puts the
    /// implement into a hard obstacle by that much on every bend.
    /// </summary>
    public const double MinMargin = 0.3;

    /// <summary>The bend radius <see cref="MinMargin"/> holds for: the follower cuts a tighter bend by more.</summary>
    public const double MinMarginRadius = 6.0;

    /// <summary>
    /// The least safety margin for a bend of <paramref name="radius"/>: <see cref="MinMargin"/>
    /// from <see cref="MinMarginRadius"/> up, and more on a tighter bend, faster than in inverse
    /// proportion (the scenario tractor cuts a 4 m bend by 0.4 m, a 6 m one by 0.25 m).
    /// </summary>
    public static double MinMarginFor(double radius) =>
        MinMargin * Math.Pow(Math.Max(1.0, MinMarginRadius / Math.Max(radius, 0.1)), 1.5);

    /// <summary>
    /// The radius the bends are built with: the Curve radius setting, but never under the
    /// tractor's own minimum turning radius (wheelbase over the tangent of the steer angle),
    /// since the follower cannot hold a bend the tractor cannot steer. With a front-mounted
    /// implement, never under √(r² + h²) for a minimum turning radius r and the implement h ahead
    /// of the pivot: the pivot steers a bend of √(R² − h²) to keep the implement on one of R
    /// (<see cref="FrontToolLine"/>), and on a tighter bend the implement runs outside it (fork
    /// only: on a tester's field, simulator, a 5 m bend with a 6 m boom 4.5 m ahead and a 4.6 m
    /// turning radius took the boom 1.35 m off the pass and onto the next tree).
    /// </summary>
    public double Radius
    {
        get
        {
            double setting = _config.Guidance.GoAroundRadius, min = _config.Vehicle.MinTurningRadius;
            if (!double.IsFinite(min) || min <= 0) min = 0;
            if (Models.Tool.RigGeometry.Mount(_config.Tool) == Models.Tool.ToolMount.FrontFixed)
            {
                double h = Math.Abs(Models.Tool.RigGeometry.AttachX(_config.Vehicle, _config.Tool));
                if (double.IsFinite(h)) min = Math.Sqrt(min * min + h * h);
            }
            return Math.Max(setting, min);
        }
    }

    /// <summary>
    /// The safety margin in force: the setting, never under <see cref="MinMarginFor"/> the radius.
    /// What the implement keeps off a hard Go around obstacle, on the bend and in the planner.
    /// </summary>
    public double SafetyMargin => Math.Max(MinMarginFor(Radius), _config.Guidance.GoAroundMargin);

    // The safety margin plus a trailed tool's cut-in: round the obstacle the inside of the bend
    // is the obstacle's side.
    private double Margin => SafetyMargin + ObstacleDetour.TrailedCutIn(_config.Tool, _config.Vehicle.HitchLength, Radius);

    /// <summary>What the pass given to <see cref="For"/> is of a closed track's lap. Open tracks: not read.</summary>
    public enum Lap
    {
        /// <summary>Not bent: the U-turn planner's lap, walked as an open line not joined at its seam.</summary>
        None,
        /// <summary>The whole lap (the line steered, the line shown): bent as a loop.</summary>
        Whole,
        /// <summary>A stretch of it (the piece shown where the lap does not fit everywhere): bent as an open line.</summary>
        Piece,
    }

    /// <summary>
    /// The pass <paramref name="pass"/> (offset <paramref name="distAway"/> from
    /// <paramref name="track"/>) bent round its Go around obstacles, or null when it runs into
    /// none. Cached while nothing changed.
    /// <para>A closed track's lap is bent when <paramref name="lap"/> says what the pass is of it:
    /// the whole lap as a loop (<see cref="ObstacleDetour.Compute"/>), a piece of it as an open line.
    /// The U-turn planner asks with <see cref="Lap.None"/> and gets no bend, as before. Fork only:
    /// laps were never bent, and on a tester's boundary curve (Eucaliptal, 22 Nov 2026) the second
    /// lap ran across hard Go around obstacles with AutoSteer on.</para>
    /// </summary>
    public ObstacleDetour.Bend? For(Models.Track.Track track, double distAway, IReadOnlyList<Vec3> pass, Boundary? boundary,
        Lap lap = Lap.None)
    {
        if (!HasGoRound(boundary)) return null;
        if (track.IsClosed && lap == Lap.None) return null;
        _field = boundary;
        if (_pins.Count > 0) DropPinsOfOtherObstacles(boundary!);
        var pins = !ReferenceEquals(track, _pinsTrack) ? null : IsPassDriven(distAway) ? _pinSides : _otherPassPinSides;
        return _detour.For(track, distAway, pass, boundary, KeepOut, Radius, Margin, pins, track.IsClosed && lap == Lap.Whole);
    }

    // A pin is kept by its obstacle's place in the field's list, and the side it holds is given to
    // whatever obstacle is at that place. With an obstacle deleted, added before it, drawn again or
    // given another choice, that is another obstacle, or the same one with another outline: the
    // pin is let go (the tractor beside it still is pinned again from the bend steered).
    private void DropPinsOfOtherObstacles(Boundary boundary)
    {
        List<int>? gone = null;
        foreach (var (inner, pin) in _pins)
            if (pin.Outline != 0 && OutlineOf(boundary, inner) != pin.Outline) (gone ??= new List<int>()).Add(inner);
        if (gone == null) return;
        foreach (int inner in gone) _pins.Remove(inner);
        PinsChanged();
    }

    // The outline of the Go around obstacle at this place in the field's list; 0 with none there.
    private static long OutlineOf(Boundary? boundary, int inner) =>
        boundary != null && inner >= 0 && inner < boundary.InnerBoundaries.Count
        && boundary.InnerBoundaries[inner] is { IsValid: true, AtObstacle: ObstacleAction.GoRound } obstacle
            ? obstacle.OutlineStamp : 0;

    /// <summary>
    /// The pass bent, or the pass itself: for the planner, which walks whatever line it gets.
    /// </summary>
    public List<Vec3> Bent(Models.Track.Track track, double distAway, List<Vec3> pass, Boundary? boundary)
    {
        var bend = For(track, distAway, pass, boundary);
        return bend != null && bend.GoneRound.Count > 0 ? bend.Points : pass;
    }

    /// <summary>
    /// Called by the pipeline with the bend it steers along this cycle (null when the pass has
    /// none) and the tractor's pivot: pins the side of every obstacle the tractor is beside and
    /// over on the bend's side of, and lets go of the pins of obstacles it has left behind or got
    /// to the other side of. A new track drops every pin.
    /// </summary>
    public void Steered(Models.Track.Track track, ObstacleDetour.Bend? bend, double easting, double northing) =>
        Steered(track, double.NaN, bend, easting, northing, null);

    /// <summary>
    /// As <see cref="Steered(Models.Track.Track, ObstacleDetour.Bend?, double, double)"/>, with the
    /// pass being driven and the way the tractor is moving, so an obstacle it has driven on past
    /// (<see cref="PassedSlack"/>) keeps its pin for that pass only (<see cref="LetGo"/>).
    /// </summary>
    /// <param name="distAway">The pass steered: its offset from the track.</param>
    /// <param name="travelHeading">The tractor's heading going forward, radians; null backing up or
    /// when not known, and then no obstacle is newly counted as passed (backing up, what trails is
    /// the tractor's front and a front implement, which <see cref="RigBehind"/> does not hold).</param>
    public void Steered(Models.Track.Track track, double distAway, ObstacleDetour.Bend? bend, double easting, double northing,
        double? travelHeading)
    {
        Current = bend;
        bool changed = false;
        if (!ReferenceEquals(track, _pinsTrack))
        {
            changed = _pins.Count > 0;
            _pins.Clear();
            _pinsTrack = track;
        }
        changed |= Release(distAway, easting, northing, travelHeading);
        if (bend != null)
            foreach (var r in bend.Rounds)
            {
                if (!r.IsBeside(easting, northing)) continue;
                if (_pins.TryGetValue(r.InnerIndex, out var had) && had.Side == r.Side) continue;
                var (cE, cN) = Centre(r.Fence);
                double heading = HeadingAlongside(bend.Points, cE, cN);
                // Not over on the bend's side yet (still on the line, in the lead-in): a flip would
                // be harmless, so nothing is pinned.
                if (Lateral(cE, cN, heading, easting, northing) * r.Side <= 0) continue;
                double passHeading = PassHeadingBy(bend.Points, r.Fence, heading);
                var (from, to) = Along(r.Fence, cE, cN, passHeading);
                var pin = new Pin(r.Side, r.Fence, r.Reach, cE, cN, heading, passHeading, from, to, OutlineOf(_field, r.InnerIndex));
                // Past it already, on the way back to the pass: nothing left to hold.
                if (IsPassed(pin, AlongPass(pin, easting, northing), travelHeading)) continue;
                _pins[r.InnerIndex] = pin;
                changed = true;
            }
        if (changed) PinsChanged();
    }

    /// <summary>
    /// Called by the pipeline before it asks for the bend it steers on the pass
    /// <paramref name="distAway"/>: lets go of the pins of obstacles the tractor has left (further
    /// from the grown obstacle than its bend reaches, or over on its other side), and of the pins
    /// of obstacles driven on past once another pass is steered. The first bend the pipeline asks
    /// for on another pass is then made without them, as the planner's is. Fork only: a pin was
    /// let go only after the pipeline's bend, and kept until the bend's reach was behind. On a
    /// tester's field (Couto da Ribeira, 26 Nov 2026, simulator) the pin taken beside an obstacle on
    /// one pass was still held after the U-turn 67 m on, onto a pass through the middle of that
    /// obstacle with room only on the other side: "No room to go around Inner 5 on this pass", a
    /// U-turn made before it and 319 m of the pass left.
    /// </summary>
    /// <param name="travelHeading">As <see cref="Steered(Models.Track.Track, double, ObstacleDetour.Bend?, double, double, double?)"/>.</param>
    public void LetGo(Models.Track.Track track, double distAway, double easting, double northing, double? travelHeading)
    {
        if (!ReferenceEquals(track, _pinsTrack)) return; // another track: Steered drops every pin
        if (Release(distAway, easting, northing, travelHeading)) PinsChanged();
    }

    // Lets go of the pins the tractor has left, marks the obstacles it has driven on past, and
    // takes distAway as the pass being driven. True when a pin was let go or marked.
    private bool Release(double distAway, double easting, double northing, double? travelHeading)
    {
        // Another pass: what was passed on the one before holds nothing on it.
        bool otherPass = !IsPassDriven(distAway);
        _pinsDistAway = distAway;
        if (_pins.Count == 0) return false;
        bool changed = false;
        foreach (var (inner, pin) in _pins.ToList())
        {
            if (ObstacleDetour.DistanceToRing(pin.Fence, easting, northing) > pin.Reach
                || Lateral(pin.CentreE, pin.CentreN, pin.Heading, easting, northing) * pin.Side < -SideSlack
                || (pin.Passed && otherPass))
            {
                _pins.Remove(inner);
                changed = true;
                continue;
            }
            // Passed, until the rig is back beside the obstacle itself (backed up to it).
            double along = AlongPass(pin, easting, northing);
            double rig = RigBehind;
            bool passed = pin.Passed ? along < pin.From - rig || along > pin.To + rig : IsPassed(pin, along, travelHeading);
            if (passed != pin.Passed)
            {
                _pins[inner] = pin with { Passed = passed };
                changed = true;
            }
        }
        return changed;
    }

    // Whether distAway is the pass being driven: within half a pass of it, so the line moved by a
    // nudge or a snap is the same pass. A pass not told (the pins' or the one asked for) is.
    private bool IsPassDriven(double distAway)
    {
        if (double.IsNaN(distAway) || double.IsNaN(_pinsDistAway)) return true;
        double spacing = _config.ActualToolWidth - _config.Tool.Overlap;
        double same = double.IsFinite(spacing) && spacing > 0.1 ? spacing / 2 : 0.05;
        return Math.Abs(distAway - _pinsDistAway) < same;
    }

    private void PinsChanged()
    {
        if (_pins.Count == 0)
        {
            _pinSides = null;
            _otherPassPinSides = null;
            return;
        }
        _pinSides = _pins.ToDictionary(p => p.Key, p => p.Value.Side);
        Dictionary<int, int>? other = null;
        foreach (var (inner, pin) in _pins)
            if (!pin.Passed) (other ??= new Dictionary<int, int>())[inner] = pin.Side;
        _otherPassPinSides = other;
    }

    // Whether the tractor has driven on past the pinned obstacle: all of it behind the rig along
    // the pass, the way the tractor is going, forward. Across the pass (in a turn) and backing up
    // nothing is judged.
    private bool IsPassed(Pin pin, double along, double? travelHeading)
    {
        if (travelHeading is not { } travel || !double.IsFinite(travel)) return false;
        double way = Math.Cos(travel - pin.PassHeading);
        if (Math.Abs(way) < 0.5) return false;
        double slack = PassedSlack + RigBehind;
        return way > 0 ? along > pin.To + slack : along < pin.From - slack;
    }

    // How far along the pass the point is from the pinned obstacle's centre, the way the pass runs.
    private static double AlongPass(Pin pin, double easting, double northing) =>
        Math.Sin(pin.PassHeading) * (easting - pin.CentreE) + Math.Cos(pin.PassHeading) * (northing - pin.CentreN);

    /// <summary>
    /// How far the rig reaches behind the pivot, metres: a rear implement's working point, a
    /// trailed one's wheels or working centre (the further back) behind the drawbar pin, a TBT
    /// tank's axle between them; nothing for a front implement.
    /// </summary>
    public double RigBehind
    {
        get
        {
            var tool = _config.Tool;
            var mount = Models.Tool.RigGeometry.Mount(tool);
            if (mount == Models.Tool.ToolMount.FrontFixed) return 0;
            double behind = Math.Max(0, -Models.Tool.RigGeometry.AttachX(_config.Vehicle, tool));
            if (mount is Models.Tool.ToolMount.Trailing or Models.Tool.ToolMount.TBT)
            {
                behind += Math.Abs(tool.TrailingHitchLength) + Math.Max(0, tool.TrailingToolToPivotLength);
                if (mount == Models.Tool.ToolMount.TBT) behind += Math.Abs(tool.TankTrailingHitchLength);
            }
            return double.IsFinite(behind) ? behind : 0;
        }
    }

    /// <summary>Whether the tractor's side of the obstacle is pinned, and which (+1 left of the pass, -1 right); for tests.</summary>
    public int PinnedSide(int innerIndex) => _pins.TryGetValue(innerIndex, out var pin) ? pin.Side : 0;

    /// <summary>Whether the obstacle's pin is one the tractor has driven on past (it holds the pass driven only); for tests.</summary>
    public bool PinIsPassed(int innerIndex) => _pins.TryGetValue(innerIndex, out var pin) && pin.Passed;

    // How far (easting, northing) is to the left of a line through (cE, cN) with the heading (radians).
    private static double Lateral(double cE, double cN, double heading, double easting, double northing) =>
        -Math.Cos(heading) * (easting - cE) + Math.Sin(heading) * (northing - cN);

    private static (double E, double N) Centre(IReadOnlyList<Vec2> ring)
    {
        double e = 0, n = 0;
        foreach (var p in ring) { e += p.Easting; n += p.Northing; }
        return (e / ring.Count, n / ring.Count);
    }

    // How far a ring reaches along a heading (radians) from (cE, cN): the least and the most.
    private static (double From, double To) Along(IReadOnlyList<Vec2> ring, double cE, double cN, double heading)
    {
        double se = Math.Sin(heading), ce = Math.Cos(heading), from = double.MaxValue, to = double.MinValue;
        foreach (var p in ring)
        {
            double a = se * (p.Easting - cE) + ce * (p.Northing - cN);
            if (a < from) from = a;
            if (a > to) to = a;
        }
        return (from, to);
    }

    // The stretch of the bent pass taken as running by the obstacle: within this of its grown outline.
    private const double PassByReach = 10.0;

    // The way the pass runs by the obstacle, radians: from the first to the last point of the
    // stretch of the bent pass that comes nearest the grown obstacle and stays within PassByReach
    // of it, so across the bend and not along one slope of it. The heading alongside is that of
    // the bend's point nearest the obstacle's centre: beside a wide obstacle that point is on the
    // bend's way out or back, up to 45 degrees off the pass. Used when that stretch is too short.
    private static double PassHeadingBy(List<Vec3> bent, IReadOnlyList<Vec2> fence, double headingAlongside)
    {
        int nearest = -1;
        double least = double.MaxValue;
        var dist = new double[bent.Count];
        for (int i = 0; i < bent.Count; i++)
        {
            dist[i] = ObstacleDetour.DistanceToRing(fence, bent[i].Easting, bent[i].Northing);
            if (dist[i] < least) { least = dist[i]; nearest = i; }
        }
        if (nearest < 0 || least > PassByReach) return headingAlongside;
        int first = nearest, last = nearest;
        while (first > 0 && dist[first - 1] <= PassByReach) first--;
        while (last < bent.Count - 1 && dist[last + 1] <= PassByReach) last++;
        double de = bent[last].Easting - bent[first].Easting, dn = bent[last].Northing - bent[first].Northing;
        return de * de + dn * dn < 4.0 ? headingAlongside : Math.Atan2(de, dn);
    }

    // The bent pass's heading where it runs alongside the obstacle (its point nearest the centre:
    // the flat of the bend, parallel to the pass), radians.
    private static double HeadingAlongside(List<Vec3> bent, double cE, double cN)
    {
        int best = 0;
        double bestD = double.MaxValue;
        for (int i = 0; i < bent.Count; i++)
        {
            double de = bent[i].Easting - cE, dn = bent[i].Northing - cN, d = de * de + dn * dn;
            if (d < bestD) { bestD = d; best = i; }
        }
        return bent[best].Heading;
    }

    /// <summary>
    /// The first Go around obstacle (its index in <see cref="Boundary.InnerBoundaries"/>) that
    /// the tractor would run into within <paramref name="distance"/> straight ahead, the
    /// obstacle grown by half the tractor's track, or -1. The safety net under the bend: the
    /// pipeline takes AutoSteer off when this is set and the tractor is off the steered line by
    /// more than the safety margin, since a tractor on the bent line is clear by construction.
    /// </summary>
    public int ObstacleAhead(Boundary? boundary, double easting, double northing, double heading, double distance)
    {
        if (!HasGoRound(boundary)) return -1;
        var grown = GrownForAhead(boundary!);
        if (grown.Count == 0) return -1;
        double se = Math.Sin(heading), ce = Math.Cos(heading);
        int steps = Math.Max(1, (int)Math.Ceiling(distance / 0.5));
        for (int k = 0; k <= steps; k++)
        {
            double t = distance * k / steps, e = easting + se * t, n = northing + ce * t;
            foreach (var (inner, ring) in grown)
                if (GeometryMath.IsPointInPolygon(ring, e, n)) return inner;
        }
        return -1;
    }

    private List<(int Inner, List<Vec2> Ring)> GrownForAhead(Boundary boundary)
    {
        long stamp = YouTurn.YouTurnCreationService.ObstacleStamp(boundary);
        double grow = Math.Max(0.3, _config.Vehicle.TrackWidth / 2);
        var cached = _ahead;
        if (ReferenceEquals(cached.Boundary, boundary) && cached.Stamp == stamp && cached.Grow == grow) return cached.List;
        var list = new List<(int, List<Vec2>)>();
        for (int i = 0; i < boundary.InnerBoundaries.Count; i++)
        {
            var inner = boundary.InnerBoundaries[i];
            if (!inner.IsValid || inner.AtObstacle != ObstacleAction.GoRound) continue;
            var outline = inner.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList();
            var ring = _offset.CreateOutwardOffset(outline, grow);
            if (ring != null && ring.Count >= 3) list.Add((i, ring));
        }
        _ahead = (boundary, stamp, grow, list);
        return list;
    }
}
