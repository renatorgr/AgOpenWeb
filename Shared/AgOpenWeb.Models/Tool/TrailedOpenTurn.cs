// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Models.Tool
{
    /// <summary>
    /// A U-turn sized for a trailed implement: the implement decides the turn, and the tractor
    /// drives what that takes (fork only).
    ///
    /// A turn planned for the tractor at the U-turn radius drags a trailed implement round a much
    /// tighter circle (a drill with its wheels 4.15 m behind the pin, the tractor on 4.3 m: the
    /// discs on 2.5 m, the drawbar 75 to 85° to the tractor, beside the rear tyre), and the
    /// implement is still at an angle to the next pass when the tractor is on it.
    ///
    /// Here the U-turn radius is the implement's: the tractor turns on the wider circle that
    /// keeps the implement's working centre on that radius (<see cref="OpenRadius"/>), and the
    /// turn ends with the swing a driver makes with a trailer, past the next pass and back onto
    /// it, sized so that the implement is square on the pass where the tractor is back on it.
    /// One shape: a quarter circle out of the pass, a straight along the top when the passes are
    /// further apart than the two arcs reach, the arc on round to the pass's direction and
    /// <see cref="Shape.Swing"/> past it, and a counter-arc of that angle at the U-turn radius
    /// onto the pass. The tractor never turns tighter than the U-turn radius.
    ///
    /// The shape exists only where the passes are far enough apart for the wider arc (that drill:
    /// from about 6.6 m, so two 3 m passes skipped): nearer than that the implement is past
    /// square at the end of every such turn, and <see cref="Solve"/> returns null. Passes further
    /// apart than twice the open radius get the wide turn, made at the open radius.
    /// </summary>
    public static class TrailedOpenTurn
    {
        /// <summary>The widest swing past the next pass looked for, rad.</summary>
        public const double MaxSwing = 60.0 * Math.PI / 180.0;

        /// <summary>The most the tractor's arc is opened to, as a share of the U-turn radius: a very
        /// long implement does not make the turn deeper than half as much again.</summary>
        public const double MaxOpenShare = 1.5;

        /// <summary>An open radius under this much more than the U-turn radius is no gain, m: a short
        /// implement follows the tractor closely enough.</summary>
        public const double MinOpen = 0.25;

        /// <summary>The spacing the implement is followed at while the shape is sized, m.</summary>
        public const double FollowStep = 0.1;

        /// <summary>
        /// The shape of an open turn, from the point where the tractor leaves its pass.
        /// </summary>
        /// <param name="Offset">How far the next pass is to the side, m.</param>
        /// <param name="Radius">The radius of the tractor's main arc, m.</param>
        /// <param name="TopStraight">The straight along the top of the turn, between the arc's two halves, m.</param>
        /// <param name="Swing">How far the arc goes on past the pass's direction, and the counter-arc's angle, rad.</param>
        /// <param name="CounterRadius">The radius of the counter-arc back onto the pass, m.</param>
        public sealed record Shape(double Offset, double Radius, double TopStraight, double Swing, double CounterRadius)
        {
            /// <summary>How far the tractor goes beyond the next pass, m.</summary>
            public double PastPass => (Radius + CounterRadius) * (1 - Math.Cos(Swing));

            /// <summary>How far along the next pass, back past the place abreast of the turn's start,
            /// the counter-arc ends, m.</summary>
            public double RunsOn => (Radius + CounterRadius) * Math.Sin(Swing);

            /// <summary>The length of the shape, m.</summary>
            public double Length => Radius * (Math.PI + Swing) + TopStraight + CounterRadius * Swing;
        }

        /// <summary>
        /// The radius the tractor's pivot turns at to keep a trailed or TBT implement's working
        /// centre on a circle of <paramref name="radius"/>, m: in a steady turn every part of the
        /// rig runs on a circle about one centre, the pin √(pivot² + hitch²) from it, the
        /// implement's wheels √(pin² − drawbar²) and its working centre √(wheels² + its distance
        /// from them²). Never more than <see cref="MaxOpenShare"/> times the radius; the radius
        /// itself for any other implement, or when the implement already runs outside it.
        /// <para>
        /// The working centre is the one on the implement's own centre line: an implement shifted
        /// to one side (<see cref="ToolGeometry.Offset"/>) runs about that much inside this circle on a
        /// turn towards its side and outside it on a turn away from it. The offset is left out on
        /// purpose: what the wider arc is for is the angle between tractor and drawbar, which a
        /// shift to one side does not change, and with it the tractor's arc would differ from a
        /// left turn to a right one. A turn planned for a front implement does the same.
        /// </para>
        /// </summary>
        public static double OpenRadius(ToolGeometry tool, double radius)
        {
            if (tool.Mount is not (ToolMount.Trailing or ToolMount.TBT) || !(radius > 0) || !double.IsFinite(radius)) return radius;
            double pin = tool.AttachX;
            double tank = tool.Mount == ToolMount.TBT ? tool.TankTrailingHitchLength : 0.0;
            double squared = radius * radius
                - tool.TrailingToolToPivotLength * tool.TrailingToolToPivotLength
                + tool.TrailingHitchLength * tool.TrailingHitchLength
                + tank * tank
                - pin * pin;
            if (!double.IsFinite(squared) || squared <= radius * radius) return radius;
            return Math.Min(Math.Sqrt(squared), radius * MaxOpenShare);
        }

        /// <summary>
        /// The open turn onto a pass <paramref name="offset"/> m to the side for
        /// <paramref name="tool"/>, or null when there is none: the implement is not a trailed one
        /// or follows the tractor closely enough (<see cref="MinOpen"/>), the passes are nearer
        /// than the wider arc needs (the implement would be past square at the end), or further
        /// apart than twice the open radius (the wide turn's job).
        /// </summary>
        /// <param name="radius">The U-turn radius, m: the implement's working centre's, and the least the tractor turns at.</param>
        public static Shape? Solve(ToolGeometry tool, double radius, double offset)
        {
            if (!(radius > 0) || !(offset > 0) || !double.IsFinite(radius) || !double.IsFinite(offset)) return null;
            double open = OpenRadius(tool, radius);
            if (open < radius + MinOpen || offset > 2 * open) return null;

            // The shape for a swing: the counter-arc has to end on the pass, which gives the main
            // arc's radius; opened no further than the open radius, the rest is a straight along the top.
            Shape? For(double swing)
            {
                double cos = Math.Cos(swing);
                double r = (offset + radius * (1 - cos)) / (1 + cos);
                if (r < radius - 1e-9) return null;      // the tractor would turn tighter than the U-turn radius
                double straight = 0;
                if (r > open)
                {
                    r = open;
                    straight = offset - r * (1 + cos) + radius * (1 - cos);
                }
                return new Shape(offset, r, straight, swing, radius);
            }

            // The first swing that leaves the implement square where the counter-arc ends: with
            // less it still lags, with more it is past the pass's direction.
            const double scan = 1.0 * Math.PI / 180.0;
            double lo = double.NaN, hi = double.NaN;
            for (double a = scan; a <= MaxSwing + 1e-9; a += scan)
            {
                var shape = For(a);
                if (shape == null) continue;
                if (ToolLagAtEnd(tool, shape) <= 0)
                {
                    if (double.IsNaN(lo)) return null;      // past square with the least swing that fits
                    hi = a;
                    break;
                }
                lo = a;
            }
            if (double.IsNaN(lo) || double.IsNaN(hi)) return null;
            for (int i = 0; i < 30; i++)
            {
                double mid = 0.5 * (lo + hi);
                var shape = For(mid);
                if (shape == null || ToolLagAtEnd(tool, shape) > 0) lo = mid; else hi = mid;
            }
            return For(hi);
        }

        /// <summary>
        /// How far the implement's heading is short of the pass's where the counter-arc of
        /// <paramref name="shape"/> ends, rad: above 0 it still lags, below it is past square.
        /// The implement is followed along the shape from in line behind the tractor
        /// (<see cref="ImplementSweptPath"/>, the model the map and the turn's checks use).
        /// </summary>
        public static double ToolLagAtEnd(ToolGeometry tool, Shape shape)
        {
            var path = new List<Vec3>();
            for (double back = 1.0; back > 1e-9; back -= FollowStep) path.Add(new Vec3(0, -back, 0));
            path.AddRange(Build(new Vec3(0, 0, 0), 0, true, shape, FollowStep));
            int end = path.Count - 1;
            // One stretch on along the pass: the pivot's direction at the counter-arc's end.
            path.Add(new Vec3(path[end].Easting, path[end].Northing - FollowStep, Math.PI));
            return Wrap(Math.PI - ImplementSweptPath.Compute(path, tool).ToolCenter[end].Heading);
        }

        /// <summary>
        /// The points of <paramref name="shape"/> from <paramref name="start"/> (the first point),
        /// where the tractor leaves its pass heading <paramref name="heading"/>, to the end of the
        /// counter-arc on the next pass, about <paramref name="step"/> m apart.
        /// </summary>
        public static List<Vec3> Build(Vec3 start, double heading, bool isTurningRight, Shape shape, double step)
        {
            var points = new List<Vec3> { start };
            if (!(step > 0) || !(shape.Radius > 0)) return points;

            var pos = start;
            double theta = heading;
            AddArc(ref pos, ref theta, points, shape.Radius * Math.PI / 2, isTurningRight, shape.Radius, step);
            if (shape.TopStraight > 1e-6)
            {
                int pieces = Math.Max(1, (int)Math.Ceiling(shape.TopStraight / step));
                double sin = Math.Sin(theta), cos = Math.Cos(theta);
                var from = pos;
                for (int i = 1; i <= pieces; i++)
                {
                    double d = shape.TopStraight * i / pieces;
                    pos = new Vec3(from.Easting + sin * d, from.Northing + cos * d, Positive(theta));
                    points.Add(pos);
                }
            }
            AddArc(ref pos, ref theta, points, shape.Radius * (Math.PI / 2 + shape.Swing), isTurningRight, shape.Radius, step);
            if (shape.Swing > 1e-9 && shape.CounterRadius > 0)
                AddArc(ref pos, ref theta, points, shape.CounterRadius * shape.Swing, !isTurningRight, shape.CounterRadius, step);
            return points;
        }

        /// <summary>Adds an arc of <paramref name="length"/> m from <paramref name="pos"/> heading <paramref name="theta"/>.</summary>
        private static void AddArc(ref Vec3 pos, ref double theta, List<Vec3> path, double length, bool isTurningRight,
            double turningRadius, double step)
        {
            int segments = Math.Max(1, (int)Math.Ceiling(length / step));
            double turn = length / segments / turningRadius * (isTurningRight ? 1.0 : -1.0);
            double radius = isTurningRight ? turningRadius : -turningRadius;
            // The arc's centre: to the right of the heading for a right turn.
            double centreE = pos.Easting + Math.Cos(theta) * radius;
            double centreN = pos.Northing - Math.Sin(theta) * radius;
            for (int i = 0; i < segments; i++)
            {
                theta += turn;
                pos = new Vec3(centreE - Math.Cos(theta) * radius, centreN + Math.Sin(theta) * radius, Positive(theta));
                path.Add(pos);
            }
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
