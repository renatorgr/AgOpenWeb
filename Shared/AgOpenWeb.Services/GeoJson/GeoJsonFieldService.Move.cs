// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.GeoJson;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services.Headland;

namespace AgOpenWeb.Services.GeoJson;

/// <summary>
/// Moving a field's drawing (fork only). A field drawn on an aerial photo is often a metre or
/// two off the ground the GPS sees, the same way over the whole field. Moving it puts what was
/// drawn where it really is, and keeps everything the GPS recorded where it was recorded.
/// </summary>
public partial class GeoJsonFieldService
{
    /// <summary>How many of each part <see cref="MoveField"/> moved.</summary>
    public readonly record struct FieldMoveResult(int Boundaries, int Obstacles, int HeadlandParts, int Tracks, bool Photo);

    /// <summary>
    /// Move everything drawn in the field in <paramref name="fieldDirectory"/> by
    /// (<paramref name="dEasting"/>, <paramref name="dNorthing"/>) metres on the field's plane:
    /// the outer boundary, every obstacle (a round one's centre too), the headland polygon, the
    /// headland lines and segments, the background photo and, with <paramref name="moveTracks"/>,
    /// the tracks (not recorded paths or contours). Flags, recorded paths, contours, coverage and
    /// elevation were put there by the GPS and stay. The origin never changes (coverage tiles
    /// are anchored to it).
    /// </summary>
    public static FieldMoveResult MoveField(string fieldDirectory, double dEasting, double dNorthing, bool moveTracks)
    {
        if (!double.IsFinite(dEasting) || !double.IsFinite(dNorthing))
            throw new ArgumentException("The move must be a number of metres");

        var path = Path.Combine(fieldDirectory, FileName);
        int boundaries = 0, obstacles = 0, headland = 0, tracks = 0;
        bool photo = false;
        lock (FileLock)
        {
            var fc = ReadCollection(path);
            var geo = ProjectionOf(fc);
            double[] Move(double[] c) => MovedCoordinate(geo, c, dEasting, dNorthing);

            foreach (var f in fc.Features)
            {
                switch (GetStringProp(f, FieldPropertyKeys.Role))
                {
                    case FeatureRoles.OuterBoundary:
                        if (MovePolygon(f, Move)) boundaries++;
                        break;
                    case FeatureRoles.InnerBoundary:
                        if (MovePolygon(f, Move)) obstacles++;
                        MoveCircleCentre(f, Move);
                        break;
                    case FeatureRoles.Headland:
                        if (MovePolygon(f, Move)) headland++;
                        break;
                    case FeatureRoles.HeadlandLine:
                        if (MoveLine(f, Move)) headland++;
                        break;
                    case FeatureRoles.Track:
                        var type = (TrackType)GetIntProp(f, FieldPropertyKeys.TrackType);
                        if (moveTracks && type is not (TrackType.RecordedPath or TrackType.Contour) && MoveLine(f, Move))
                            tracks++;
                        break;
                    case FeatureRoles.BackgroundImage:
                        photo = MoveBackground(f, Move) || photo;
                        break;
                }
            }
            Write(path, fc);
        }

        // The Field Builder's headland segments keep their own copy of the boundary stretch
        // they were built from and of their offset line, in plane metres.
        var segments = HeadlandSegmentFileService.Load(fieldDirectory);
        if (segments.Count > 0)
        {
            foreach (var s in segments)
            {
                s.BoundaryPoints = s.BoundaryPoints.Select(p => new Vec3(p.Easting + dEasting, p.Northing + dNorthing, p.Heading)).ToList();
                s.OffsetPoints = s.OffsetPoints.Select(p => new Vec3(p.Easting + dEasting, p.Northing + dNorthing, p.Heading)).ToList();
            }
            HeadlandSegmentFileService.Save(fieldDirectory, segments);
        }

        return new FieldMoveResult(boundaries, obstacles, headland, tracks, photo);
    }

    // [lon, lat, rest...] moved on the plane; the rest (a heading) is kept as it is.
    private static double[] MovedCoordinate(Projection geo, double[] c, double dEasting, double dNorthing)
    {
        var local = geo.ToLocal(c[1], c[0]);
        var (lat, lon) = geo.ToWgs84(new Vec2(local.Easting + dEasting, local.Northing + dNorthing));
        var moved = (double[])c.Clone();
        moved[0] = lon;
        moved[1] = lat;
        return moved;
    }

    private static bool MovePolygon(GeoJsonFeature f, Func<double[], double[]> move)
    {
        if (f.Geometry.Coordinates is not JsonElement je || je.ValueKind != JsonValueKind.Array) return false;
        var rings = je.EnumerateArray().Select(r => ParseCoordArray(r).Select(move).ToArray()).ToArray();
        if (rings.Length == 0) return false;
        f.Geometry.Coordinates = rings;
        return true;
    }

    private static bool MoveLine(GeoJsonFeature f, Func<double[], double[]> move)
    {
        if (ReadLineStringCoords(f.Geometry) is not { Count: > 0 } coords) return false;
        f.Geometry.Coordinates = coords.Select(move).ToArray();
        return true;
    }

    private static void MoveCircleCentre(GeoJsonFeature f, Func<double[], double[]> move)
    {
        if (!f.Properties.TryGetValue(FieldPropertyKeys.CircleCenter, out var val)
            || val is not JsonElement je || je.ValueKind != JsonValueKind.Array || je.GetArrayLength() < 2
            || je[0].ValueKind != JsonValueKind.Number || je[1].ValueKind != JsonValueKind.Number)
            return;
        f.Properties[FieldPropertyKeys.CircleCenter] = move(new[] { je[0].GetDouble(), je[1].GetDouble() });
    }

    // The photo's corners, and its Web-Mercator bounds by as much as its corners moved in them.
    private static bool MoveBackground(GeoJsonFeature f, Func<double[], double[]> move)
    {
        if (ReadPolygonRing(f.Geometry, 0) is not { Count: >= 4 } ring) return false;
        double n = ring.Max(c => c[1]), w = ring.Min(c => c[0]), s = ring.Min(c => c[1]), e = ring.Max(c => c[0]);
        var nw = move(new[] { w, n });
        var se = move(new[] { e, s });
        f.Geometry.Coordinates = new[] { new[] { new[] { nw[0], nw[1] }, new[] { se[0], nw[1] }, new[] { se[0], se[1] }, new[] { nw[0], se[1] }, new[] { nw[0], nw[1] } } };
        if (HasProp(f, FieldPropertyKeys.MercatorMinX))
        {
            Shift(FieldPropertyKeys.MercatorMinX, MercatorX(nw[0]) - MercatorX(w));
            Shift(FieldPropertyKeys.MercatorMaxX, MercatorX(se[0]) - MercatorX(e));
            Shift(FieldPropertyKeys.MercatorMaxY, MercatorY(nw[1]) - MercatorY(n));
            Shift(FieldPropertyKeys.MercatorMinY, MercatorY(se[1]) - MercatorY(s));
        }
        return true;

        void Shift(string key, double by) => f.Properties[key] = GetDoubleProp(f, key) + by;
    }

    private const double MercatorRadius = 6378137.0; // EPSG:3857
    private static double MercatorX(double lon) => MercatorRadius * lon * Math.PI / 180.0;
    private static double MercatorY(double lat) => MercatorRadius * Math.Log(Math.Tan(Math.PI / 4 + lat * Math.PI / 360.0));
}
