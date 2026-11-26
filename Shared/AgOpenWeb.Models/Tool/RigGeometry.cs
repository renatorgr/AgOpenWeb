// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;

namespace AgOpenWeb.Models.Tool
{
    /// <summary>
    /// Where the tool hangs on the tractor: the one place that turns the vehicle's and the tool's
    /// settings into the tool's mount and the point it is attached at. The tool position (sections,
    /// coverage), the map's implement, the U-turn's swept path and the settings that invalidate a
    /// remembered U-turn all read it, so they cannot disagree (they each had their own copy, and
    /// the copies differed on which flag won when a profile set two).
    /// Distances follow ISO 11783-10's device axes: x along the tractor from the centre of the
    /// rear axle, positive ahead (NonTracked/HITCH_MODEL_PLAN.md).
    /// </summary>
    public static class RigGeometry
    {
        /// <summary>
        /// The tool's mount from its type flags. The flags are set together by
        /// <see cref="ToolConfig.SetToolType"/>, but a profile sets them one by one, so more than
        /// one can be on. As the tool position always read them: a fixed flag makes the tool fixed,
        /// and it is ahead only with the front flag alone; otherwise TBT before trailing. None set
        /// is a rear-fixed tool.
        /// </summary>
        public static ToolMount Mount(ToolConfig tool)
        {
            bool trailed = tool.IsToolTrailing || tool.IsToolTBT;
            if (tool.IsToolFrontFixed && !tool.IsToolRearFixed && !trailed) return ToolMount.FrontFixed;
            if (tool.IsToolFrontFixed || tool.IsToolRearFixed) return ToolMount.RearFixed;
            if (tool.IsToolTBT) return ToolMount.TBT;
            if (tool.IsToolTrailing) return ToolMount.Trailing;
            return ToolMount.RearFixed;
        }

        /// <summary>Front or rear fixed: the tool follows the tractor's heading.</summary>
        public static bool IsRigid(ToolMount mount) => mount is ToolMount.FrontFixed or ToolMount.RearFixed;

        /// <summary>
        /// Where the tool is placed along the tractor from the rear axle (+ ahead), as ISO
        /// 11783-10 chains it: the tractor's connector, then the tool from it. A fixed tool: its
        /// working point, the vehicle's 3-point hitch (front or rear) plus the tool's distance from
        /// it, away from the tractor. A trailed or TBT tool: the drawbar pin, behind the axle.
        /// </summary>
        public static double AttachX(ToolMount mount, double drawbar, double threePoint, double frontHitch, double workingPointFromHitch) =>
            mount switch
            {
                ToolMount.FrontFixed => Math.Abs(frontHitch) + workingPointFromHitch,
                ToolMount.RearFixed => -(Math.Abs(threePoint) + workingPointFromHitch),
                _ => -Math.Abs(drawbar),
            };

        /// <inheritdoc cref="AttachX(ToolMount, double, double, double, double)"/>
        public static double AttachX(VehicleConfig vehicle, ToolConfig tool) =>
            AttachX(Mount(tool), vehicle.HitchLength, vehicle.ThreePointHitchLength, vehicle.FrontHitchLength, tool.WorkingPointFromHitch);

        /// <summary>
        /// A fixed tool's working point from the rear axle, the way profiles stored it before the
        /// hitch points moved to the vehicle (format 2's <c>Tool.HitchLength</c>, positive, ahead
        /// for a front tool): what an older build reads back from a newer profile.
        /// </summary>
        public static double AxleToWorkingPoint(VehicleConfig vehicle, ToolConfig tool) =>
            Mount(tool) == ToolMount.FrontFixed
                ? Math.Abs(vehicle.FrontHitchLength) + tool.WorkingPointFromHitch
                : Math.Abs(vehicle.ThreePointHitchLength) + tool.WorkingPointFromHitch;

        /// <summary>The point <paramref name="x"/> along the tractor (+ ahead) from <paramref name="pivot"/>.</summary>
        public static Vec3 AlongTractor(Vec3 pivot, double heading, double x) =>
            new(pivot.Easting + Math.Sin(heading) * x, pivot.Northing + Math.Cos(heading) * x, heading);

        /// <summary>
        /// Where the point <paramref name="x"/> along the tractor (+ ahead of the rear axle) and
        /// <paramref name="right"/> across it (+ to the right) goes while the pivot drives
        /// <paramref name="pivotPath"/>: each point of the path moved x along the path's own
        /// direction there (from the point before to the point after it) and right across it.
        /// A U-turn and the way onto a pass are planned and driven for the pivot; a front
        /// implement's working centre runs ahead of it and swings outside every bend of the path
        /// (4.7 m ahead on a 5.4 m turn: on a 7.2 m circle, 1.8 m outside), so drawn for the
        /// pivot the path is not where the implement goes. Fork only.
        /// </summary>
        public static List<Vec3> PathOfPointAhead(IReadOnlyList<Vec3> pivotPath, double x, double right = 0)
        {
            int n = pivotPath.Count;
            var result = new List<Vec3>(n);
            double heading = n > 0 ? pivotPath[0].Heading : 0;
            for (int i = 0; i < n; i++)
            {
                // The nearest points before and after that are not this one again.
                int a = i, b = i;
                while (a > 0 && SamePlace(pivotPath[a], pivotPath[i])) a--;
                while (b < n - 1 && SamePlace(pivotPath[b], pivotPath[i])) b++;
                if (GeometryMath.DistanceSquared(pivotPath[a], pivotPath[b]) > 1e-12)
                    heading = Math.Atan2(pivotPath[b].Easting - pivotPath[a].Easting, pivotPath[b].Northing - pivotPath[a].Northing);
                var p = AlongTractor(pivotPath[i], heading, x);
                // To the right of the way it goes: (cos, -sin) of the heading.
                result.Add(new Vec3(p.Easting + Math.Cos(heading) * right, p.Northing - Math.Sin(heading) * right, heading));
            }
            return result;
        }

        private static bool SamePlace(Vec3 a, Vec3 b) =>
            Math.Abs(a.Easting - b.Easting) < 1e-6 && Math.Abs(a.Northing - b.Northing) < 1e-6;

        /// <summary>The longest step the pivot is pulled by in <see cref="PivotPathBehind"/>, m.</summary>
        private const double PullStep = 0.05;

        /// <summary>
        /// The pivot's path while the point <paramref name="x"/> ahead of the rear axle on the
        /// tractor's centre line drives <paramref name="pointPath"/>: the other way round from
        /// <see cref="PathOfPointAhead"/>. The rear axle does not slide sideways, so the pivot
        /// follows that point as a trailer follows its hitch, x behind it: on a steady bend of
        /// radius ρ it runs on a circle of √(ρ² − x²) about the same centre, it comes into a bend
        /// and out of it gradually (onto a straight as e^(−s/x)), and it never turns tighter than
        /// that circle while the point's path bends no tighter than ρ. One pivot point for each
        /// point of the path, in the same order, its heading the tractor's there. The tractor starts
        /// in line with the path's first stretch. A U-turn planned for a front implement's centre
        /// is turned into the path the pivot is steered on with it. Fork only.
        /// </summary>
        public static List<Vec3> PivotPathBehind(IReadOnlyList<Vec3> pointPath, double x)
        {
            int n = pointPath.Count;
            var result = new List<Vec3>(n);
            if (n == 0) return result;
            if (x <= 0)
            {
                result.AddRange(pointPath);
                return result;
            }

            // In line with the first stretch that has a length.
            double heading = pointPath[0].Heading;
            for (int i = 1; i < n; i++)
            {
                if (SamePlace(pointPath[i], pointPath[0])) continue;
                heading = Math.Atan2(pointPath[i].Easting - pointPath[0].Easting, pointPath[i].Northing - pointPath[0].Northing);
                break;
            }

            result.Add(AlongTractor(pointPath[0], ZeroToTwoPi(heading), -x));
            for (int i = 1; i < n; i++)
            {
                double dE = pointPath[i].Easting - pointPath[i - 1].Easting;
                double dN = pointPath[i].Northing - pointPath[i - 1].Northing;
                double length = Math.Sqrt(dE * dE + dN * dN);
                int steps = Math.Max(1, (int)Math.Ceiling(length / PullStep));
                for (int k = 0; k < steps && length > 0; k++)
                {
                    double sE = dE / steps, sN = dN / steps;
                    // The point's move across the tractor turns it: to the right of a heading h is
                    // (cos h, -sin h). The turn is taken at the middle of the step.
                    double first = (sE * Math.Cos(heading) - sN * Math.Sin(heading)) / x;
                    double mid = heading + first / 2;
                    heading += (sE * Math.Cos(mid) - sN * Math.Sin(mid)) / x;
                }
                result.Add(AlongTractor(pointPath[i], ZeroToTwoPi(heading), -x));
            }
            return result;
        }

        private static double ZeroToTwoPi(double heading)
        {
            heading %= 2 * Math.PI;
            return heading < 0 ? heading + 2 * Math.PI : heading;
        }

        /// <summary>
        /// How far ahead of the pivot a front implement works (a combine's header too: its tool
        /// is a front one), 0 for any other mount. Negative if its working point is set behind
        /// the rear axle.
        /// </summary>
        public static double FrontToolReach(VehicleConfig vehicle, ToolConfig tool) =>
            Mount(tool) == ToolMount.FrontFixed ? AttachX(vehicle, tool) : 0;

        /// <summary>
        /// How far a trailed tool's working centre is behind what pulls it (the hitch, or the TBT
        /// tank): the trailing hitch length plus AgOpenGPS's pivot distance
        /// (setTool_trailingToolToPivotLength, the Tool panel's Behind / Ahead): positive with the
        /// tool behind its wheels, negative with it ahead.
        /// </summary>
        public static double ToolBehindPivot(ToolConfig tool) => tool.TrailingHitchLength + tool.TrailingToolToPivotLength;

        /// <summary>
        /// A trailed or TBT tool's working centre, from the tractor's hitch and the headings of the
        /// TBT tank and of the tool.
        /// </summary>
        public static (double Easting, double Northing) TrailedToolCentre(
            double hitchEasting, double hitchNorthing, double tankHeading, double toolHeading, ToolConfig tool)
        {
            if (tool.IsToolTBT)
            {
                hitchEasting -= Math.Sin(tankHeading) * tool.TankTrailingHitchLength;
                hitchNorthing -= Math.Cos(tankHeading) * tool.TankTrailingHitchLength;
            }
            double behind = ToolBehindPivot(tool);
            return (hitchEasting - Math.Sin(toolHeading) * behind, hitchNorthing - Math.Cos(toolHeading) * behind);
        }

        /// <summary>The implement's geometry for <see cref="ImplementSweptPath"/>.</summary>
        public static ToolGeometry Geometry(VehicleConfig vehicle, ToolConfig tool, double width) => new(
            Mount: Mount(tool),
            Width: width,
            Offset: tool.Offset,
            AttachX: AttachX(vehicle, tool),
            TrailingHitchLength: tool.TrailingHitchLength,
            TrailingToolToPivotLength: tool.TrailingToolToPivotLength,
            TankTrailingHitchLength: tool.TankTrailingHitchLength,
            Length: tool.Length);

        /// <summary>
        /// A stamp of every setting that moves the tool relative to the tractor: a value worked out
        /// from the tool's position (a U-turn tried and refused at this end) is stale once it changes.
        /// </summary>
        public static int Stamp(VehicleConfig vehicle, ToolConfig tool) => HashCode.Combine(
            Mount(tool), AttachX(vehicle, tool), tool.TrailingHitchLength,
            tool.TrailingToolToPivotLength, tool.TankTrailingHitchLength, tool.Offset, tool.Length);
    }
}
