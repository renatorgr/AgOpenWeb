// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Models.Tool
{
    /// <summary>
    /// A turn's exit swung wide, so a trailed implement is square on the next pass when the tractor
    /// is (fork only).
    ///
    /// A turn is planned for the tractor's pivot: its last arc ends on the next pass and the
    /// tractor is straight from there. A trailed implement cuts inside the turn and is still at an
    /// angle to the pass then; it comes onto the pass only over three times its length or so (a
    /// drill 4 m behind its pin: 15 to 20 m past the top of the turn), so the first metres of every
    /// pass are worked crooked. A driver swings wide: past the next pass and back onto it, which
    /// pulls the implement square.
    ///
    /// <see cref="Apply"/> does that to a planned path. The path is kept up to the top of its last
    /// arc (the place where the tractor stops going away from the field); from there the tail is
    /// a straight, the arc carried on past the pass's direction by the swing angle, and a
    /// counter-arc of that angle back onto the pass, all at the turn radius, so the tractor never
    /// turns tighter than planned. The straight's length follows from the angle (the counter-arc
    /// has to end on the pass), and the angle is the one that leaves the implement's heading on
    /// the pass's where the counter-arc ends, found by following the implement along the path
    /// (<see cref="ImplementSweptPath"/>, the model the map and the turn's checks use).
    /// A turn that already swings as much (an omega between rows close together) is left alone.
    /// </summary>
    public static class TrailedExitSwing
    {
        /// <summary>The widest swing looked for, rad.</summary>
        public const double MaxSwing = 60.0 * Math.PI / 180.0;

        /// <summary>The spacing of the points of the new tail, m.</summary>
        public const double Step = 0.5;

        /// <summary>The least straight along the pass after the counter-arc, m: the line takes over
        /// from a turn 4 m before its end.</summary>
        public const double LegAfter = 6.0;

        /// <summary>An implement nearer than this to square where the planned turn ends is left alone, rad.</summary>
        public const double SquareEnough = 1.0 * Math.PI / 180.0;

        /// <summary>A swing under this is not made, rad: a short implement follows the tractor closely enough.</summary>
        public const double MinSwing = 5.0 * Math.PI / 180.0;

        /// <summary>
        /// The swung path and what was done to it.
        /// </summary>
        /// <param name="Path">The pivot's path: the planned one up to <paramref name="FromIndex"/>, the new tail after it.</param>
        /// <param name="Swing">How far the last arc is carried on past the pass's direction, rad.</param>
        /// <param name="Straight">The straight put in before the last arc, m.</param>
        /// <param name="FromIndex">The last point of the planned path that is kept.</param>
        /// <param name="OnPassIndex">Where the counter-arc ends: the tractor straight on the pass, the implement square behind it.</param>
        /// <param name="PastPass">How far the tractor goes beyond the next pass, m.</param>
        /// <param name="RunsOnBy">How much further along the pass the path ends than the planned one, m.</param>
        /// <param name="ToolAngleBefore">The implement's angle to the pass where the planned turn's last arc ended, rad.</param>
        public sealed record Result(List<Vec3> Path, double Swing, double Straight, int FromIndex, int OnPassIndex,
            double PastPass, double RunsOnBy, double ToolAngleBefore);

        /// <summary>
        /// <paramref name="path"/> with its exit swung wide for <paramref name="tool"/>, or null when
        /// there is nothing to gain or no such exit: the path does not end with an arc onto a
        /// straight stretch, the implement is square already or nearly (a swing under <see cref="MinSwing"/>), it would take a swing over
        /// <see cref="MaxSwing"/>, or the new tail leaves the ground <paramref name="allowed"/>.
        /// </summary>
        /// <param name="path">The pivot's path as planned: it ends along the next pass.</param>
        /// <param name="tool">The implement (a trailed or TBT one).</param>
        /// <param name="radius">The turn radius, m: the tail's arcs.</param>
        /// <param name="allowed">Where the tractor may drive the new tail (the turn area); null: anywhere.</param>
        public static Result? Apply(IReadOnlyList<Vec3> path, ToolGeometry tool, double radius, Func<Vec3, bool>? allowed = null)
        {
            if (path == null || path.Count < 8 || !(radius > 0) || !double.IsFinite(radius)) return null;
            if (tool.Mount is not (ToolMount.Trailing or ToolMount.TBT)) return null;

            int n = path.Count;
            var end = path[n - 1];

            // The pass the path ends along: the direction of its last metre.
            int back = n - 2;
            while (back > 0 && Dist(path[back], end) < 1.0) back--;
            if (Dist(path[back], end) < 1e-6) return null;
            double passHeading = Math.Atan2(end.Easting - path[back].Easting, end.Northing - path[back].Northing);

            // Where the path comes onto that line: the first point of its straight end.
            int onPass = n - 1;
            while (onPass > 1
                   && Math.Abs(Lateral(path[onPass - 1], end, passHeading)) <= 0.03
                   && Math.Abs(Wrap(Chord(path, onPass - 1) - passHeading)) <= 2.0 * Math.PI / 180.0)
                onPass--;
            if (onPass < 3 || onPass >= n - 1) return null;

            // Which way the turn goes round (+1 clockwise, to the right), and which way its last arc does.
            double net = 0;
            for (int i = 1; i < n - 1; i++) net += Wrap(Chord(path, i) - Chord(path, i - 1));
            int lastDir = ArcDirectionBefore(path, onPass);
            if (lastDir == 0) return null;
            int dir = Math.Abs(net) >= 0.75 * Math.PI ? Math.Sign(net) : lastDir;

            // A counter-arc already there (an omega's exit) is taken off: the new tail has its own.
            int arcEnd = onPass;
            if (lastDir != dir)
            {
                while (arcEnd > 2 && TurnAt(path, arcEnd - 1) * dir <= 0) arcEnd--;
                if (arcEnd <= 2) return null;
            }

            // Back along the last arc to its top: the first point from which the tractor no longer
            // goes away from the field (its direction within 90° of the pass's), or the arc's start.
            int top = arcEnd;
            while (top > 2)
            {
                double arriving = Chord(path, top - 2);     // the direction the point before is reached in
                if (Math.Cos(arriving - passHeading) < -1e-6) break;      // square to the pass is not going away
                if (TurnAt(path, top - 1) * dir <= 1e-4) break;      // the arc starts here (a straight or another arc before it)
                top--;
            }

            // The implement where the planned turn ends: nothing to gain when it is square there.
            var planned = ImplementSweptPath.Compute(path, tool);
            double angleBefore = Wrap(planned.ToolCenter[onPass].Heading - passHeading);
            if (dir * angleBefore > -SquareEnough) return null;       // square, or swung too far already

            // Tried from the top; where the tail leaves the allowed ground, from further round the
            // arc, where the straight heads back into the field.
            for (int from = top; from <= arcEnd; from++)
            {
                double arriving = Chord(path, from - 1);
                double toGo = dir * Wrap(passHeading - arriving);       // the arc left before the pass's direction
                if (toGo < 30.0 * Math.PI / 180.0) break;
                var result = Solve(path, from, arriving, end, passHeading, dir, radius, tool, angleBefore, allowed, out bool outside);
                if (result != null) return result;
                if (!outside) break;
            }
            return null;
        }

        /// <summary>The swing from one point of the last arc: the angle that leaves the implement square.</summary>
        private static Result? Solve(IReadOnlyList<Vec3> path, int from, double arriving, Vec3 end, double passHeading, int dir,
            double radius, ToolGeometry tool, double angleBefore, Func<Vec3, bool>? allowed, out bool outside)
        {
            outside = false;
            // dir * (implement's angle to the pass) where the counter-arc ends: under 0 it still lags.
            double Lag(double swing, out List<Vec3>? candidate, out int onPassIndex, out double straight)
            {
                candidate = Build(path, from, arriving, end, passHeading, dir, radius, swing, out onPassIndex, out straight);
                if (candidate == null) return double.NaN;
                var swept = ImplementSweptPath.Compute(candidate, tool);
                return dir * Wrap(swept.ToolCenter[onPassIndex].Heading - passHeading);
            }

            // The first swing that fits (the straight not negative), then the first that is enough.
            const double scan = 3.0 * Math.PI / 180.0;
            double lo = double.NaN, hi = double.NaN;
            for (double a = 0; a <= MaxSwing + 1e-9; a += scan)
            {
                double lag = Lag(a, out _, out _, out _);
                if (double.IsNaN(lag)) continue;
                if (lag >= 0)
                {
                    if (double.IsNaN(lo)) return null;      // the least swing that fits is too much already
                    hi = a;
                    break;
                }
                lo = a;
            }
            if (double.IsNaN(lo) || double.IsNaN(hi)) return null;
            for (int i = 0; i < 24; i++)
            {
                double mid = 0.5 * (lo + hi);
                double lag = Lag(mid, out _, out _, out _);
                if (double.IsNaN(lag) || lag < 0) lo = mid; else hi = mid;
            }

            double swing = hi;
            if (swing < MinSwing) return null;
            if (double.IsNaN(Lag(swing, out var best, out int onPassAt, out double straightBefore)) || best == null) return null;

            double pastPass = 0;
            for (int i = from + 1; i <= onPassAt; i++)
            {
                if (allowed != null && !allowed(best[i])) { outside = true; return null; }
                // Beyond the pass: on the far side of it from where the tail starts.
                double side = Lateral(best[i], end, passHeading) * -Math.Sign(Lateral(path[from], end, passHeading));
                if (side > pastPass) pastPass = side;
            }
            double runsOn = Along(best[^1], end, passHeading);
            return new Result(best, swing, straightBefore, from, onPassAt, pastPass, Math.Max(0, runsOn), Math.Abs(angleBefore));
        }

        /// <summary>
        /// The path up to <paramref name="from"/>, then a straight, the arc on to the pass's direction
        /// and <paramref name="swing"/> past it, the counter-arc back, and the pass; null when the
        /// counter-arc can't end on the pass with a straight of 0 or more.
        /// </summary>
        private static List<Vec3>? Build(IReadOnlyList<Vec3> path, int from, double arriving, Vec3 end, double passHeading, int dir,
            double radius, double swing, out int onPassIndex, out double straight)
        {
            onPassIndex = -1;
            straight = 0;
            double sinOff = Math.Sin(arriving - passHeading);
            if (Math.Abs(sinOff) < 0.2) return null;

            double over = passHeading + dir * swing;                     // the direction where the arc hands over to the counter-arc
            // From the tail's first point to the counter-arc's end, without the straight.
            var (cE, cN) = Centre(arriving, dir);
            var (oE, oN) = Centre(over, dir);
            var (pE, pN) = Centre(passHeading, dir);
            double shiftE = radius * (cE - 2 * oE + pE), shiftN = radius * (cN - 2 * oN + pN);
            var start = path[from];
            double lateral = Lateral(new Vec3(start.Easting + shiftE, start.Northing + shiftN, 0), end, passHeading);
            straight = -lateral / sinOff;
            if (straight < -1e-6) return null;
            if (straight < 0) straight = 0;

            var result = new List<Vec3>(from + 1 + 64);
            for (int i = 0; i <= from; i++) result.Add(path[i]);

            // The straight.
            double e = start.Easting, nn = start.Northing;
            int pieces = (int)Math.Ceiling(straight / Step);
            for (int i = 1; i <= pieces; i++)
            {
                double d = straight * i / pieces;
                result.Add(new Vec3(start.Easting + Math.Sin(arriving) * d, start.Northing + Math.Cos(arriving) * d, arriving));
            }
            e = start.Easting + Math.Sin(arriving) * straight;
            nn = start.Northing + Math.Cos(arriving) * straight;

            // The arc, on past the pass's direction.
            double arcAngle = dir * Wrap(passHeading - arriving) + swing;
            if (arcAngle <= 0) return null;
            AddArc(result, ref e, ref nn, arriving, dir, radius, arcAngle);
            // The counter-arc back onto the pass.
            if (swing > 1e-9) AddArc(result, ref e, ref nn, over, -dir, radius, swing);
            onPassIndex = result.Count - 1;

            // Along the pass: as far as the planned path went, and far enough for the line to take over.
            double leg = Math.Max(LegAfter, Along(end, new Vec3(e, nn, 0), passHeading));
            pieces = (int)Math.Ceiling(leg / Step);
            for (int i = 1; i <= pieces; i++)
            {
                double d = leg * i / pieces;
                result.Add(new Vec3(e + Math.Sin(passHeading) * d, nn + Math.Cos(passHeading) * d, Positive(passHeading)));
            }
            return result;
        }

        /// <summary>Adds an arc from (e, n) heading <paramref name="heading"/>, turning <paramref name="dir"/> through <paramref name="angle"/>.</summary>
        private static void AddArc(List<Vec3> result, ref double e, ref double n, double heading, int dir, double radius, double angle)
        {
            var (cE, cN) = Centre(heading, dir);
            double centreE = e + radius * cE, centreN = n + radius * cN;
            int pieces = Math.Max(1, (int)Math.Ceiling(angle * radius / Step));
            for (int i = 1; i <= pieces; i++)
            {
                double h = heading + dir * angle * i / pieces;
                var (hE, hN) = Centre(h, dir);
                result.Add(new Vec3(centreE - radius * hE, centreN - radius * hN, Positive(h)));
            }
            e = result[^1].Easting;
            n = result[^1].Northing;
        }

        /// <summary>The unit vector from a point heading <paramref name="heading"/> to the centre of
        /// its turn: to the right for a clockwise turn.</summary>
        private static (double E, double N) Centre(double heading, int dir) => (dir * Math.Cos(heading), -dir * Math.Sin(heading));

        /// <summary>Which way the path turns just before <paramref name="index"/>: +1 clockwise, −1 the other way, 0 for none.</summary>
        private static int ArcDirectionBefore(IReadOnlyList<Vec3> path, int index)
        {
            double sum = 0;
            for (int i = index; i >= 2; i--)
            {
                sum += Wrap(Chord(path, i - 1) - Chord(path, i - 2));
                if (Math.Abs(sum) >= 5.0 * Math.PI / 180.0) return Math.Sign(sum);
            }
            return 0;
        }

        /// <summary>The turn at point <paramref name="i"/>: the change of direction from the stretch before it to the one after, rad.</summary>
        private static double TurnAt(IReadOnlyList<Vec3> path, int i) =>
            i <= 0 || i >= path.Count - 1 ? 0 : Wrap(Chord(path, i) - Chord(path, i - 1));

        /// <summary>The direction from point <paramref name="i"/> to the next.</summary>
        private static double Chord(IReadOnlyList<Vec3> path, int i)
        {
            int a = Math.Clamp(i, 0, path.Count - 2);
            return Math.Atan2(path[a + 1].Easting - path[a].Easting, path[a + 1].Northing - path[a].Northing);
        }

        /// <summary>How far <paramref name="p"/> is to the right of the line through <paramref name="on"/> heading <paramref name="heading"/>.</summary>
        private static double Lateral(Vec3 p, Vec3 on, double heading) =>
            (p.Easting - on.Easting) * Math.Cos(heading) - (p.Northing - on.Northing) * Math.Sin(heading);

        /// <summary>How far <paramref name="p"/> is along the line from <paramref name="from"/> heading <paramref name="heading"/>.</summary>
        private static double Along(Vec3 p, Vec3 from, double heading) =>
            (p.Easting - from.Easting) * Math.Sin(heading) + (p.Northing - from.Northing) * Math.Cos(heading);

        private static double Dist(Vec3 a, Vec3 b)
        {
            double dE = a.Easting - b.Easting, dN = a.Northing - b.Northing;
            return Math.Sqrt(dE * dE + dN * dN);
        }

        private static double Wrap(double angle)
        {
            while (angle > Math.PI) angle -= 2 * Math.PI;
            while (angle < -Math.PI) angle += 2 * Math.PI;
            return angle;
        }

        private static double Positive(double heading)
        {
            heading %= 2 * Math.PI;
            return heading < 0 ? heading + 2 * Math.PI : heading;
        }
    }
}
