// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Newtonsoft.Json;
using AgOpenWeb.Models;
using AgOpenWeb.Models.AgShare;
using AgOpenWeb.Models.Base;
using TrackModel = AgOpenWeb.Models.Track.Track;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services.GeoJson;

namespace AgOpenWeb.Services.AgShare
{
    /// <summary>
    /// Service for downloading field data from AgShare cloud service.
    /// </summary>
    public class AgShareDownloaderService(AgShareClient agShareClient)
    {
        /// <summary>
        /// Downloads a field and saves it to disk
        /// </summary>
        public async Task<(bool success, string message)> DownloadAndSaveAsync(Guid fieldId, string fieldsDirectory)
        {
            try
            {
                string json = await agShareClient.DownloadFieldAsync(fieldId);
                var dto = JsonConvert.DeserializeObject<AgShareFieldDto>(json);
                var model = AgShareFieldParser.Parse(dto!);
                string fieldDir = Path.Combine(fieldsDirectory, model.Name);
                await FieldFileWriter.WriteAllFilesAsync(model, fieldDir);
                return (true, "Download successful");
            }
            catch (Exception ex)
            {
                return (false, $"Download failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        /// <summary>
        /// Retrieves a list of user-owned fields
        /// </summary>
        public async Task<List<AgShareGetOwnFieldDto>> GetOwnFieldsAsync()
        {
            return await agShareClient.GetOwnFieldsAsync();
        }

        /// <summary>
        /// Downloads a field DTO for preview only
        /// </summary>
        public async Task<AgShareFieldDto?> DownloadFieldPreviewAsync(Guid fieldId)
        {
            string json = await agShareClient.DownloadFieldAsync(fieldId);
            return JsonConvert.DeserializeObject<AgShareFieldDto>(json);
        }

        /// <summary>
        /// Downloads all user fields with progress reporting
        /// </summary>
        public async Task<(int Downloaded, int Skipped)> DownloadAllAsync(
            string fieldsDirectory,
            bool forceOverwrite = false,
            IProgress<int>? progress = null)
        {
            var fields = await GetOwnFieldsAsync();
            int skipped = 0, downloaded = 0;

            foreach (var field in fields)
            {
                string dir = Path.Combine(fieldsDirectory, field.Name);
                string agsharePath = Path.Combine(dir, "agshare.txt");

                bool alreadyExists = false;
                if (File.Exists(agsharePath))
                {
                    try
                    {
                        var id = (await File.ReadAllTextAsync(agsharePath)).Trim();
                        alreadyExists = Guid.TryParse(id, out Guid guid) && guid == field.Id;
                    }
                    catch
                    {
                        //If the ID file is unreadable, treat it as non-existent
                    }
                }

                if (alreadyExists && !forceOverwrite)
                {
                    skipped++;
                }
                else
                {
                    var preview = await DownloadFieldPreviewAsync(field.Id);
                    if (preview != null)
                    {
                        var model = AgShareFieldParser.Parse(preview);
                        await FieldFileWriter.WriteAllFilesAsync(model, dir);
                        downloaded++;
                    }
                }

                progress?.Report(downloaded + skipped);
            }

            return (downloaded, skipped);
        }
    }

    /// <summary>
    /// Writes a downloaded LocalFieldModel into a field folder: field.geojson for the field
    /// itself, plus the per-feature files AgOpenWeb still keeps in AgOpenGPS formats.
    /// </summary>
    public static class FieldFileWriter
    {
        /// <summary>
        /// Writes all files required for a field
        /// </summary>
        public static async Task WriteAllFilesAsync(LocalFieldModel field, string fieldDir)
        {
            if (!Directory.Exists(fieldDir))
                Directory.CreateDirectory(fieldDir);

            await WriteAgShareIdAsync(fieldDir, field.FieldId);
            WriteFieldGeoJson(fieldDir, field.Origin, field.Boundaries);
            GeoJsonFieldService.SaveTracks(fieldDir, ToTracks(field.AbLines));
            // Flags, headland lines, contours and the applied area are local work, not part of
            // what AgShare stores, so a re-download leaves them alone (AgOpenGPS #1203).
        }

        /// <summary>
        /// Writes agshare.txt with the field ID
        /// </summary>
        private static async Task WriteAgShareIdAsync(string fieldDir, Guid fieldId)
        {
            await File.WriteAllTextAsync(Path.Combine(fieldDir, "agshare.txt"), fieldId.ToString());
        }

        /// <summary>
        /// Writes the origin and boundary rings to field.geojson. A re-download over an existing
        /// field replaces those and keeps the rest (headland, background image); with no rings
        /// in the download the existing boundary stays. An earlier download still in AgOpenGPS
        /// files is imported first, so it can't win over this one on the next open.
        /// </summary>
        private static void WriteFieldGeoJson(string fieldDir, Wgs84 origin, List<List<LocalPoint>>? boundaries)
        {
            var fields = new FieldService();
            Field field;
            try
            {
                field = fields.LoadField(fieldDir);
            }
            catch (FileNotFoundException)
            {
                field = new Field { Name = Path.GetFileName(fieldDir), CreatedDate = DateTime.Now };
            }
            field.DirectoryPath = fieldDir;
            // AgShare stores the holes' outlines, not what the tractor does at each one. A hole of
            // a re-download that is still where the field has one keeps the operator's choice
            // (U-turn, Go around, drive through, Hard): written drive-through like a new one, a
            // tree chosen as U-turn was driven through after the field was downloaded again. Only
            // with the origin unchanged: the outlines are in the origin's plane.
            bool sameOrigin = field.Origin != null
                && Math.Abs(field.Origin.Latitude - origin.Latitude) < 1e-9
                && Math.Abs(field.Origin.Longitude - origin.Longitude) < 1e-9;
            var holesBefore = sameOrigin && field.Boundary != null
                ? new List<BoundaryPolygon>(field.Boundary.InnerBoundaries)
                : new List<BoundaryPolygon>();
            field.Origin = new Position { Latitude = origin.Latitude, Longitude = origin.Longitude };
            field.LastModifiedDate = DateTime.Now;

            if (boundaries is { Count: > 0 })
            {
                var boundary = field.Boundary ?? new Boundary();
                boundary.OuterBoundary = null;
                boundary.InnerBoundaries.Clear();
                for (int i = 0; i < boundaries.Count; i++)
                {
                    var polygon = new BoundaryPolygon();
                    foreach (var pt in BoundaryUtils.WithHeadings(ConvertToVec3List(boundaries[i])))
                        polygon.Points.Add(new BoundaryPoint(pt.Easting, pt.Northing, pt.Heading));
                    polygon.UpdateBounds();
                    if (i == 0)
                        boundary.OuterBoundary = polygon;
                    else
                    {
                        // Holes were written drive-through, as before.
                        polygon.IsDriveThrough = true;
                        boundary.AddNewObstacle(polygon);
                        if (SameHole(polygon, holesBefore) is { } was)
                        {
                            polygon.AtObstacle = was.AtObstacle;
                            polygon.IsHard = was.IsHard;
                        }
                    }
                }
                field.Boundary = boundary;
            }

            fields.SaveField(field);
        }

        /// <summary>How far a hole's box may differ from the field's own hole and still be that hole, m.</summary>
        private const double SameHoleTolerance = 0.5;

        /// <summary>
        /// The field's own hole a downloaded one is: every side of its bounding box within
        /// <see cref="SameHoleTolerance"/> of that hole's. Null for a hole the field did not have.
        /// </summary>
        private static BoundaryPolygon? SameHole(BoundaryPolygon hole, List<BoundaryPolygon> before)
        {
            if (hole.Points.Count == 0) return null;
            var box = Box(hole);
            foreach (var was in before)
            {
                if (was.Points.Count == 0) continue;
                var b = Box(was);
                if (Math.Abs(b.MinE - box.MinE) <= SameHoleTolerance && Math.Abs(b.MaxE - box.MaxE) <= SameHoleTolerance
                    && Math.Abs(b.MinN - box.MinN) <= SameHoleTolerance && Math.Abs(b.MaxN - box.MaxN) <= SameHoleTolerance)
                    return was;
            }
            return null;
        }

        private static (double MinE, double MaxE, double MinN, double MaxN) Box(BoundaryPolygon polygon)
        {
            double minE = double.MaxValue, maxE = double.MinValue, minN = double.MaxValue, maxN = double.MinValue;
            foreach (var p in polygon.Points)
            {
                minE = Math.Min(minE, p.Easting); maxE = Math.Max(maxE, p.Easting);
                minN = Math.Min(minN, p.Northing); maxN = Math.Max(maxN, p.Northing);
            }
            return (minE, maxE, minN, maxN);
        }

        /// <summary>
        /// The downloaded AB lines and curves as tracks. They replace the field's tracks, as the
        /// download always replaced its track file.
        /// </summary>
        private static List<TrackModel> ToTracks(List<AbLineLocal> abLines)
        {
            var tracks = new List<TrackModel>();
            foreach (var ab in abLines)
            {
                var track = new TrackModel { Name = ab.Name ?? "Unnamed", IsVisible = true };
                if (ab.CurvePoints is { Count: > 1 })
                {
                    track.Type = TrackType.Curve;
                    track.Points = ab.CurvePoints.Select(p => new Vec3(p.Easting, p.Northing, p.Heading)).ToList();
                }
                else
                {
                    track.Type = TrackType.ABLine;
                    track.Points = new List<Vec3>
                    {
                        new(ab.PtA.Easting, ab.PtA.Northing, ab.Heading),
                        new(ab.PtB.Easting, ab.PtB.Northing, ab.Heading),
                    };
                }
                tracks.Add(track);
            }
            return tracks;
        }

        /// <summary>
        /// Helper to convert LocalPoint list to Vec3 list
        /// </summary>
        private static List<Vec3> ConvertToVec3List(List<LocalPoint> points)
        {
            var result = new List<Vec3>();
            foreach (var pt in points)
            {
                result.Add(pt);
            }
            return result;
        }
    }
}
