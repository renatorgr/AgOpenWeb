// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Models.YouTurn
{
    /// <summary>
    /// Pure geometry for the U-turns onto a pass closer than twice the turn radius, which a
    /// single semicircle can't reach.
    ///
    /// <see cref="BuildOffsetArc"/> is Brian Tischler's "sagitta" U-turn (AOG dev-fork
    /// CYouTurn.GetOffsetSemicirclePoints / AddCoordinatesToPath), the default: a short
    /// counter-arc lead-in, then one offset arc that lands on the pass already heading back.
    /// The lead-in makes the path meet the crop row tangentially, eliminating the
    /// straight-leg→arc curvature step that makes Omega turns feel sharp.
    ///
    /// <see cref="BuildKeyhole"/> mirrors that lead-in at the end, centring the loop between
    /// the passes (#418). With 6 m passes at an 8 m radius the sagitta turn is 22.8 m deep and
    /// reaches 10 m past the start pass; the keyhole 19.6 m and 5 m either side.
    ///
    /// Heading convention matches the rest of the guidance code:
    /// <c>theta = atan2(easting, northing)</c> (clockwise from north).
    /// </summary>
    public static class SagittaTurnGeometry
    {
        private const double TwoPi = Math.PI * 2.0;

        /// <summary>
        /// Builds the sagitta offset arc starting at <paramref name="start"/>.
        /// </summary>
        /// <param name="start">Arc start point.</param>
        /// <param name="theta">Start heading in radians.</param>
        /// <param name="isTurningRight">True to curve right, false to curve left.</param>
        /// <param name="turningRadius">Turn radius in metres (must be &gt; 0).</param>
        /// <param name="offsetDistance">
        /// Pullback from a full 2R semicircle, in metres. The arc lands
        /// <c>2R - offsetDistance</c> sideways, so to land exactly on a row at
        /// lateral distance <c>L</c> pass <c>offsetDistance = 2R - L</c>. 0 gives a
        /// plain semicircle landing at 2R. Valid range [0, 2R].
        /// </param>
        /// <param name="angle">Main sweep angle in radians (Math.PI for a semicircle).</param>
        public static List<Vec3> BuildOffsetArc(Vec3 start, double theta, bool isTurningRight,
            double turningRadius, double offsetDistance, double angle)
        {
            var points = new List<Vec3> { start };
            if (turningRadius <= 0) return points;

            // Sagitta relation s = R(1 - cos θ)  ->  θ = acos(1 - s/R).
            // The 0.5 factor splits the sagitta across the lead-in counter-arc.
            double ratio = 1.0 - (offsetDistance * 0.5) / turningRadius;
            if (ratio > 1.0) ratio = 1.0;
            if (ratio < -1.0) ratio = -1.0;
            double firstArcAngle = Math.Acos(ratio);

            var pos = start;

            if (offsetDistance > 0)
            {
                // Counter-arc lead-in: curve the opposite way to align the entry tangent.
                AddArc(ref pos, ref theta, points, firstArcAngle * turningRadius, !isTurningRight, turningRadius);
            }

            // Main arc completes the turn; the extra firstArcAngle compensates for the lead-in.
            double remainingAngle = angle + firstArcAngle;
            AddArc(ref pos, ref theta, points, remainingAngle * turningRadius, isTurningRight, turningRadius);

            return points;
        }

        /// <summary>
        /// The sagitta turn from any heading (fork only): a counter-arc, then the main arc, both of
        /// <paramref name="turningRadius"/>, ending on the line through <paramref name="linePoint"/>
        /// heading <paramref name="lineHeading"/>, the way the line is driven back. From a start on
        /// the pass at its own heading it is <see cref="BuildOffsetArc"/>; from a start on a Go
        /// around bend, off the pass and at an angle to it, it still lands on the next pass at its
        /// heading, where the offset arc, turning 180° from the bend's heading, ended up to 45° off
        /// it. A line further to the side than that reaches gets an arc, a straight and a quarter circle
        /// (<see cref="ArcLineArc"/>). Empty when no such turn lands on the line.
        /// </summary>
        /// <param name="start">Arc start, on the line the tractor drives.</param>
        /// <param name="theta">Start heading in radians (clockwise from north).</param>
        /// <param name="isTurningRight">True to turn right, false to turn left.</param>
        /// <param name="turningRadius">Turn radius in metres (must be &gt; 0).</param>
        /// <param name="linePoint">A point of the line the turn lands on.</param>
        /// <param name="lineHeading">The heading the line is driven at after the turn, radians.</param>
        public static List<Vec3> BuildToLine(Vec3 start, double theta, bool isTurningRight,
            double turningRadius, Vec2 linePoint, double lineHeading)
        {
            if (turningRadius <= 0) return new List<Vec3>();
            double sign = isTurningRight ? 1.0 : -1.0;
            // The main arc turns the start heading round to the line's, the counter-arc's angle more.
            double turn = (sign * (lineHeading - theta)) % TwoPi;
            if (turn < 0) turn += TwoPi;
            if (turn < Math.PI / 2 || turn > 3 * Math.PI / 2) return new List<Vec3>();

            // Where a turn with a counter-arc of `a` ends, across the line (+ right of it).
            double sinL = Math.Sin(lineHeading), cosL = Math.Cos(lineHeading);
            double Across(double a, List<Vec3>? points)
            {
                var path = points ?? new List<Vec3>();
                if (points != null) path.Add(start);
                var pos = start;
                double h = theta;
                if (a > 0) AddArc(ref pos, ref h, path, a * turningRadius, !isTurningRight, turningRadius);
                AddArc(ref pos, ref h, path, (turn + a) * turningRadius, isTurningRight, turningRadius);
                return (pos.Easting - linePoint.Easting) * cosL - (pos.Northing - linePoint.Northing) * sinL;
            }

            double lo = 0, hi = Math.PI / 2;
            double fLo = Across(lo, null), fHi = Across(hi, null);
            if (fLo * fHi > 0) return ArcLineArc(start, theta, isTurningRight, turningRadius, turn, linePoint, lineHeading);
            for (int i = 0; i < 50 && hi - lo > 1e-9; i++)
            {
                double mid = (lo + hi) / 2;
                double f = Across(mid, null);
                if ((f > 0) == (fLo > 0)) { lo = mid; fLo = f; }
                else hi = mid;
            }
            var result = new List<Vec3>();
            Across((lo + hi) / 2, result);
            return result;
        }

        /// <summary>
        /// <see cref="BuildToLine"/> onto a line further to the side than its two arcs reach (a start
        /// on a Go around bend that took the pass away from the next one): an arc square to the line,
        /// straight on, and a quarter circle onto it, as AgOpenGPS's wide turn. Empty when the line is
        /// nearer than that.
        /// </summary>
        private static List<Vec3> ArcLineArc(Vec3 start, double theta, bool isTurningRight, double turningRadius,
            double turn, Vec2 linePoint, double lineHeading)
        {
            double sinL = Math.Sin(lineHeading), cosL = Math.Cos(lineHeading);
            double Across(Vec3 p) => (p.Easting - linePoint.Easting) * cosL - (p.Northing - linePoint.Northing) * sinL;
            var points = new List<Vec3> { start };
            var pos = start;
            double h = theta;
            AddArc(ref pos, ref h, points, (turn - Math.PI / 2) * turningRadius, isTurningRight, turningRadius);
            // Square to the line now: a quarter circle from here ends one radius nearer it.
            var probe = new List<Vec3>();
            var end = pos;
            double hEnd = h;
            AddArc(ref end, ref hEnd, probe, Math.PI / 2 * turningRadius, isTurningRight, turningRadius);
            double rate = Math.Sin(h) * cosL - Math.Cos(h) * sinL; // across per metre straight on
            if (Math.Abs(rate) < 0.5) return new List<Vec3>();
            double straight = -Across(end) / rate;
            if (straight < 0) return new List<Vec3>();
            int steps = (int)Math.Ceiling(straight / (turningRadius * 0.1));
            var from = pos;
            for (int i = 1; i <= steps; i++)
            {
                double d = straight * i / steps;
                pos = new Vec3(from.Easting + Math.Sin(h) * d, from.Northing + Math.Cos(h) * d, h);
                points.Add(pos);
            }
            AddArc(ref pos, ref h, points, Math.PI / 2 * turningRadius, isTurningRight, turningRadius);
            return points;
        }

        /// <summary>
        /// The keyhole turn from <paramref name="start"/> onto the pass <paramref name="lateral"/>
        /// metres to the side: it ends there, level with the start, heading back.
        /// </summary>
        /// <param name="start">Turn start, on the current pass.</param>
        /// <param name="theta">Start heading in radians.</param>
        /// <param name="isTurningRight">True to turn right, false to turn left.</param>
        /// <param name="turningRadius">Turn radius in metres (must be &gt; 0).</param>
        /// <param name="lateral">Distance to the target pass, in [0, 2R]. At 2R the
        /// counter-arcs vanish and the turn is a plain semicircle.</param>
        public static List<Vec3> BuildKeyhole(Vec3 start, double theta, bool isTurningRight,
            double turningRadius, double lateral)
        {
            var points = new List<Vec3> { start };
            if (turningRadius <= 0) return points;

            // Counter-arc centres sit R beyond each pass, level with the start; the main arc's
            // centre midway between the passes, 2R from both (the arcs touch). Each counter-arc
            // turns through the angle of the line between its centre and the main one.
            double l = Math.Clamp(lateral, 0, 2 * turningRadius);
            double across = l / 2 + turningRadius;
            double ahead = Math.Sqrt(Math.Max(0, 4 * turningRadius * turningRadius - across * across));
            double counter = Math.Atan2(ahead, across);

            var pos = start;
            if (counter > 0) AddArc(ref pos, ref theta, points, counter * turningRadius, !isTurningRight, turningRadius);
            AddArc(ref pos, ref theta, points, (Math.PI + 2 * counter) * turningRadius, isTurningRight, turningRadius);
            if (counter > 0) AddArc(ref pos, ref theta, points, counter * turningRadius, !isTurningRight, turningRadius);
            return points;
        }

        /// <summary>How far past the start a keyhole onto a pass <paramref name="lateral"/>
        /// metres away reaches: the main arc's centre plus its radius.</summary>
        public static double KeyholeDepth(double turningRadius, double lateral)
        {
            double across = Math.Clamp(lateral, 0, 2 * turningRadius) / 2 + turningRadius;
            return turningRadius + Math.Sqrt(Math.Max(0, 4 * turningRadius * turningRadius - across * across));
        }

        /// <summary>
        /// Appends points along a constant-radius arc of the given <paramref name="length"/>,
        /// advancing <paramref name="pos"/> and <paramref name="theta"/> by reference.
        /// </summary>
        private static void AddArc(ref Vec3 pos, ref double theta, List<Vec3> path,
            double length, bool isTurningRight, double turningRadius)
        {
            int segments = (int)Math.Ceiling(length / (turningRadius * 0.1));
            if (segments < 1) segments = 1;

            double dist = length / segments;
            double turnParameter = (dist / turningRadius) * (isTurningRight ? 1.0 : -1.0);
            double radius = isTurningRight ? turningRadius : -turningRadius;

            double sinH = Math.Sin(theta);
            double cosH = Math.Cos(theta);

            for (int i = 0; i < segments; i++)
            {
                // Step to the arc centre, rotate the heading, step back out to the rim.
                pos.Easting += cosH * radius;
                pos.Northing -= sinH * radius;

                theta += turnParameter;
                theta %= TwoPi;

                sinH = Math.Sin(theta);
                cosH = Math.Cos(theta);

                pos.Easting -= cosH * radius;
                pos.Northing += sinH * radius;
                pos.Heading = theta;

                path.Add(pos);
            }
        }
    }
}
