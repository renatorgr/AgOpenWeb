// Phase MT — Draw boundary on map. IFAP (Instituto de Financiamento da Agricultura
// e Pescas) rastersapi client: fetches aerial tiles from the same raster service
// that backs the "Ortofotomapas mais Recentes" layer in the public iSIP viewer
// (publico-isip.ifap.pt). For mainland Portugal this is typically the freshest
// source available — confirmed 2025-05-23 imagery for a Castelo de Vide, PT parcel
// at the time this was written, newer than the DGT's own published Ortos series
// (Ortos2021) or Google/Bing's general-purpose satellite layers.
//
// PROTOCOL — reverse-engineered from the iSIP Público client's own JS
// (map.rasterLayer.js, function computeTileName), not guessed from URL patterns:
//
//   {base}cat{catalogId}/nre{resolutionCm:0000}/0/{Y}/{X}.jpg
//
//   catalogId  — always 1000 for this layer (DRAWING_RASTER_LAYER_ID in the source)
//   resolutionCm — metres/pixel * 100, zero-padded to 4 digits (0.5 m/px -> "0050")
//   Y, X       — tile origin (bottom-left corner) in EPSG:3763 (ETRS89 / Portugal
//                TM06), rounded to the nearest metre. Confirmed against two
//                independent cursor-position/tile-URL pairs captured from the live
//                iSIP viewer, and against a proj4 transform of Castelo de Vide's
//                known lat/lon landing within ~300m of the derived point.
//   Tile size  — 300x200 PIXELS (DEFAULT_TILESIZE_X/Y in the source), i.e.
//                (300*resolution) x (200*resolution) METRES on the ground.
//
// EPSG:3763 is NOT Web Mercator — every other provider in this file (DGT, Google,
// Bing) works in EPSG:3857, so IFAP tiles are individually reprojected onto the
// output canvas via their four corners rather than composited as axis-aligned
// rectangles like the others. Over a single field's extent (tens to low hundreds
// of metres) the residual distortion from treating each tile's corners as locally
// affine is negligible for a background image.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SkiaSharp;

namespace AgOpenWeb.RemoteServer;

public static class IfapRasterCapture
{
    private const string BaseUrl = "https://www.ifap.pt/isip/rastersapi/";
    private const int CatalogId = 1000;       // DRAWING_RASTER_LAYER_ID in iSIP's own JS
    private const int TileSizePxX = 300;      // DEFAULT_TILESIZE_X
    private const int TileSizePxY = 200;      // DEFAULT_TILESIZE_Y
    private const double DefaultResolution = 0.5; // m/px — matches the examples this was calibrated against

    // Mainland Portugal's extent in EPSG:3763, roughly — used the same way as the
    // DGT bbox check in BoundaryImageryCapture: skip the attempt entirely outside
    // coverage rather than let requests fail one by one. Derived from EPSG:3763's
    // published area of use (Portugal mainland onshore) via the same forward
    // transform used below, with generous padding — this only needs to be a quick
    // pre-filter, not a precise boundary.
    private const double PtMinX = -70_000, PtMaxX = 270_000;
    private const double PtMinY = -320_000, PtMaxY = -10_000;

    /// <summary>
    /// Attempt to capture the given Web-Mercator (EPSG:3857) bbox from the IFAP
    /// rastersapi. Returns a composited SKBitmap positioned to exactly fill that
    /// bbox at the given pixel resolution, or null if the area is outside
    /// EPSG:3763's mainland-Portugal coverage or the service is unreachable.
    /// Caller (BoundaryImageryCapture) owns saving/encoding — this only builds
    /// the bitmap so it composes the same way the tile-grid providers do.
    /// </summary>
    public static async Task<SKBitmap?> TryCaptureAsync(
        double mercMinX, double mercMaxX, double mercMinY, double mercMaxY,
        int outW, int outH)
    {
        // Reproject all four corners (not just min/max) — EPSG:3763 is a Transverse
        // Mercator with a different central meridian than Web Mercator, so the
        // mapping isn't axis-aligned; using only two corners would silently miss
        // area near the edges of anything but a small bbox.
        var corners3857 = new (double x, double y)[]
        {
            (mercMinX, mercMinY), (mercMaxX, mercMinY),
            (mercMaxX, mercMaxY), (mercMinX, mercMaxY),
        };
        var corners3763 = new (double x, double y)[4];
        for (int i = 0; i < 4; i++)
        {
            var (lon, lat) = WebMercatorToLonLat(corners3857[i].x, corners3857[i].y);
            corners3763[i] = LonLatToPtTm06(lon, lat);
        }

        double ptMinX = corners3763[0].x, ptMaxX = corners3763[0].x;
        double ptMinY = corners3763[0].y, ptMaxY = corners3763[0].y;
        foreach (var (x, y) in corners3763)
        {
            if (x < ptMinX) ptMinX = x; if (x > ptMaxX) ptMaxX = x;
            if (y < ptMinY) ptMinY = y; if (y > ptMaxY) ptMaxY = y;
        }

        bool overlapsPortugal = ptMinX < PtMaxX && ptMaxX > PtMinX && ptMinY < PtMaxY && ptMaxY > PtMinY;
        if (!overlapsPortugal) return null;

        double resolution = DefaultResolution;
        double worldTileSizeX = TileSizePxX * resolution; // metres, easting direction
        double worldTileSizeY = TileSizePxY * resolution; // metres, northing direction

        // Snap to the tile grid the same way the iSIP client itself does before
        // naming a tile (floor to the tile size), so requested tile origins line
        // up with what the service actually has, not an arbitrary offset grid.
        double gridMinX = Math.Floor(ptMinX / worldTileSizeX) * worldTileSizeX;
        double gridMinY = Math.Floor(ptMinY / worldTileSizeY) * worldTileSizeY;
        double gridMaxX = Math.Ceiling(ptMaxX / worldTileSizeX) * worldTileSizeX;
        double gridMaxY = Math.Ceiling(ptMaxY / worldTileSizeY) * worldTileSizeY;

        int tilesX = (int)Math.Round((gridMaxX - gridMinX) / worldTileSizeX);
        int tilesY = (int)Math.Round((gridMaxY - gridMinY) / worldTileSizeY);
        if (tilesX <= 0 || tilesY <= 0 || tilesX > 64 || tilesY > 64) return null; // sanity, same cap as Bing/Google grids

        var jobs = new List<Task<(int tx, int ty, byte[]? bytes)>>();
        for (int tx = 0; tx < tilesX; tx++)
            for (int ty = 0; ty < tilesY; ty++)
            {
                int cx = tx, cy = ty;
                double tileX = gridMinX + cx * worldTileSizeX;
                double tileY = gridMinY + cy * worldTileSizeY;
                string url = BuildTileUrl(tileX, tileY, resolution);
                jobs.Add(Task.Run(async () => (cx, cy, await RemoteServerHost.FetchDgtMapAsync(url).ConfigureAwait(false))));
            }
        var results = await Task.WhenAll(jobs).ConfigureAwait(false);

        var bitmap = new SKBitmap(outW, outH, SKColorType.Rgba8888, SKAlphaType.Opaque);
        int drawn = 0;
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High })
        {
            canvas.Clear(SKColors.Black);
            foreach (var (tx, ty, bytes) in results)
            {
                if (bytes is null || bytes.Length < 500) continue; // skip missing/placeholder tiles
                using var tile = SKBitmap.Decode(bytes);
                if (tile is null) continue;

                double tileMinX = gridMinX + tx * worldTileSizeX, tileMaxX = tileMinX + worldTileSizeX;
                double tileMinY = gridMinY + ty * worldTileSizeY, tileMaxY = tileMinY + worldTileSizeY;

                // Reproject this tile's four corners from EPSG:3763 back through
                // EPSG:3857 to output-canvas pixel space, then draw via that
                // quad rather than an axis-aligned rect — keeps this correct even
                // though the two projections aren't parallel.
                var dstQuad = new SKPoint[4];
                var srcCorners3763 = new (double x, double y)[]
                {
                    (tileMinX, tileMinY), (tileMaxX, tileMinY),
                    (tileMaxX, tileMaxY), (tileMinX, tileMaxY),
                };
                for (int i = 0; i < 4; i++)
                {
                    var (lon, lat) = PtTm06ToLonLat(srcCorners3763[i].x, srcCorners3763[i].y);
                    var (mx, my) = LonLatToWebMercator(lon, lat);
                    dstQuad[i] = new SKPoint(
                        (float)((mx - mercMinX) / (mercMaxX - mercMinX) * outW),
                        (float)((mercMaxY - my) / (mercMaxY - mercMinY) * outH)); // y inverted, pixels grow downward
                }

                DrawBitmapToQuad(canvas, tile, dstQuad, paint);
                drawn++;
            }
        }

        if (drawn == 0) { bitmap.Dispose(); return null; }
        return bitmap;
    }

    private static string BuildTileUrl(double tileX, double tileY, double resolution)
    {
        long x = (long)Math.Round(tileX);
        long y = (long)Math.Round(tileY);
        string res = ((int)Math.Round(resolution * 100)).ToString().PadLeft(4, '0');
        return $"{BaseUrl}cat{CatalogId}/nre{res}/0/{y}/{x}.jpg";
    }

    /// <summary>
    /// Draw a bitmap warped onto an arbitrary quadrilateral (not necessarily an
    /// axis-aligned rect) by splitting it into two triangles and mapping each with
    /// its own affine transform — SkiaSharp has no built-in quad-to-quad draw.
    /// Good enough for a single small tile; not meant for large-scale warping.
    /// </summary>
    private static void DrawBitmapToQuad(SKCanvas canvas, SKBitmap src, SKPoint[] dstQuad, SKPaint paint)
    {
        int w = src.Width, h = src.Height;
        var srcTri1 = new[] { new SKPoint(0, 0), new SKPoint(w, 0), new SKPoint(0, h) };
        var dstTri1 = new[] { dstQuad[0], dstQuad[1], dstQuad[3] }; // matches (minX,minY),(maxX,minY),(minX,maxY)
        var srcTri2 = new[] { new SKPoint(w, 0), new SKPoint(w, h), new SKPoint(0, h) };
        var dstTri2 = new[] { dstQuad[1], dstQuad[2], dstQuad[3] };

        DrawTriangle(canvas, src, srcTri1, dstTri1, paint);
        DrawTriangle(canvas, src, srcTri2, dstTri2, paint);
    }

    private static void DrawTriangle(SKCanvas canvas, SKBitmap src, SKPoint[] srcTri, SKPoint[] dstTri, SKPaint paint)
    {
        var matrix = ComputeAffine(srcTri, dstTri);
        if (matrix is null) return;

        using var clipPath = new SKPath();
        clipPath.MoveTo(dstTri[0]); clipPath.LineTo(dstTri[1]); clipPath.LineTo(dstTri[2]); clipPath.Close();

        canvas.Save();
        canvas.ClipPath(clipPath, antialias: true);
        canvas.Concat(matrix.Value); // avoid TotalMatrix (had preview-build bugs in SkiaSharp 3.1, mono/SkiaSharp#2868) — Concat composes onto the current matrix directly
        canvas.DrawBitmap(src, 0, 0, paint);
        canvas.Restore();
    }

    private static SKMatrix? ComputeAffine(SKPoint[] src, SKPoint[] dst)
    {
        // Solve the 2D affine transform mapping src[i] -> dst[i] for i=0,1,2.
        double x0 = src[0].X, y0 = src[0].Y, x1 = src[1].X, y1 = src[1].Y, x2 = src[2].X, y2 = src[2].Y;
        double u0 = dst[0].X, v0 = dst[0].Y, u1 = dst[1].X, v1 = dst[1].Y, u2 = dst[2].X, v2 = dst[2].Y;

        double det = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (Math.Abs(det) < 1e-9) return null;

        double a = ((u1 - u0) * (y2 - y0) - (u2 - u0) * (y1 - y0)) / det;
        double b = ((u2 - u0) * (x1 - x0) - (u1 - u0) * (x2 - x0)) / det;
        double c = u0 - a * x0 - b * y0;
        double d = ((v1 - v0) * (y2 - y0) - (v2 - v0) * (y1 - y0)) / det;
        double e = ((v2 - v0) * (x1 - x0) - (v1 - v0) * (x2 - x0)) / det;
        double f = v0 - d * x0 - e * y0;

        return new SKMatrix((float)a, (float)b, (float)c, (float)d, (float)e, (float)f, 0, 0, 1);
    }

    // ── EPSG:3857 (Web Mercator) <-> EPSG:4326 (lat/lon) ────────────────────────
    private const double EarthRadius = 6378137.0; // WGS84/GRS80 semi-major axis (m)

    private static (double lon, double lat) WebMercatorToLonLat(double x, double y)
    {
        double lon = x / EarthRadius * 180.0 / Math.PI;
        double lat = (2 * Math.Atan(Math.Exp(y / EarthRadius)) - Math.PI / 2) * 180.0 / Math.PI;
        return (lon, lat);
    }

    private static (double x, double y) LonLatToWebMercator(double lon, double lat)
    {
        double x = lon * Math.PI / 180.0 * EarthRadius;
        double latRad = lat * Math.PI / 180.0;
        double y = Math.Log(Math.Tan(Math.PI / 4 + latRad / 2)) * EarthRadius;
        return (x, y);
    }

    // ── EPSG:4326 (lat/lon) <-> EPSG:3763 (ETRS89 / Portugal TM06) ──────────────
    // Transverse Mercator, GRS80 ellipsoid. Parameters from the official EPSG
    // registry (epsg.io/3763): lat_0=39.6682583333333, lon_0=-8.13310833333333,
    // k0=1, x0=0, y0=0. Implemented directly (Snyder's forward/inverse TM series)
    // rather than pulling in a full projection library for one CRS pair.
    private const double Grs80A = 6378137.0;
    private const double Grs80F = 1.0 / 298.257222101;
    private const double Tm06Lat0Deg = 39.6682583333333;
    private const double Tm06Lon0Deg = -8.13310833333333;

    private static (double x, double y) LonLatToPtTm06(double lonDeg, double latDeg)
    {
        double a = Grs80A;
        double f = Grs80F;
        double e2 = f * (2 - f);
        double ep2 = e2 / (1 - e2);

        double lat0 = Tm06Lat0Deg * Math.PI / 180.0;
        double lon0 = Tm06Lon0Deg * Math.PI / 180.0;
        double lat = latDeg * Math.PI / 180.0;
        double lon = lonDeg * Math.PI / 180.0;

        double n = f / (2 - f);
        double n2 = n * n, n3 = n2 * n, n4 = n3 * n;

        // Meridian arc length (Krueger series) from the equator to lat0 and lat.
        double A = a / (1 + n) * (1 + n2 / 4 + n4 / 64);

        double MeridianArc(double phi)
        {
            return A * (phi
                - (3 * n / 2 - 9 * n3 / 16) * Math.Sin(2 * phi)
                + (15 * n2 / 16 - 15 * n4 / 32) * Math.Sin(4 * phi)
                - (35 * n3 / 48) * Math.Sin(6 * phi)
                + (315 * n4 / 512) * Math.Sin(8 * phi));
        }

        double M0 = MeridianArc(lat0);
        double M = MeridianArc(lat);

        double t = Math.Tan(lat);
        double t2 = t * t;
        double c = ep2 * Math.Cos(lat) * Math.Cos(lat);
        double nu = a / Math.Sqrt(1 - e2 * Math.Sin(lat) * Math.Sin(lat));
        double A1 = (lon - lon0) * Math.Cos(lat);
        double A2 = A1 * A1, A3 = A2 * A1, A4 = A3 * A1, A5 = A4 * A1, A6 = A5 * A1;

        double x = nu * (A1 + (1 - t2 + c) * A3 / 6
            + (5 - 18 * t2 + t2 * t2 + 72 * c - 58 * ep2) * A5 / 120);
        double y = (M - M0) + nu * t * (A2 / 2
            + (5 - t2 + 9 * c + 4 * c * c) * A4 / 24
            + (61 - 58 * t2 + t2 * t2 + 600 * c - 330 * ep2) * A6 / 720);

        return (x, y); // k0=1, x0=0, y0=0 for EPSG:3763
    }

    private static (double lon, double lat) PtTm06ToLonLat(double x, double y)
    {
        double a = Grs80A;
        double f = Grs80F;
        double e2 = f * (2 - f);
        double ep2 = e2 / (1 - e2);
        double e1 = (1 - Math.Sqrt(1 - e2)) / (1 + Math.Sqrt(1 - e2));

        double lat0 = Tm06Lat0Deg * Math.PI / 180.0;
        double lon0 = Tm06Lon0Deg * Math.PI / 180.0;

        double n = f / (2 - f);
        double n2 = n * n, n3 = n2 * n, n4 = n3 * n;
        double A = a / (1 + n) * (1 + n2 / 4 + n4 / 64);

        double MeridianArc(double phi)
        {
            return A * (phi
                - (3 * n / 2 - 9 * n3 / 16) * Math.Sin(2 * phi)
                + (15 * n2 / 16 - 15 * n4 / 32) * Math.Sin(4 * phi)
                - (35 * n3 / 48) * Math.Sin(6 * phi)
                + (315 * n4 / 512) * Math.Sin(8 * phi));
        }

        double M0 = MeridianArc(lat0);
        double M = M0 + y;
        double mu = M / A;

        double phi1 = mu
            + (3 * e1 / 2 - 27 * e1 * e1 * e1 / 32) * Math.Sin(2 * mu)
            + (21 * e1 * e1 / 16 - 55 * e1 * e1 * e1 * e1 / 32) * Math.Sin(4 * mu)
            + (151 * e1 * e1 * e1 / 96) * Math.Sin(6 * mu)
            + (1097 * e1 * e1 * e1 * e1 / 512) * Math.Sin(8 * mu);

        double t1 = Math.Tan(phi1);
        double t1_2 = t1 * t1;
        double c1 = ep2 * Math.Cos(phi1) * Math.Cos(phi1);
        double nu1 = a / Math.Sqrt(1 - e2 * Math.Sin(phi1) * Math.Sin(phi1));
        double rho1 = a * (1 - e2) / Math.Pow(1 - e2 * Math.Sin(phi1) * Math.Sin(phi1), 1.5);
        double dd = x / nu1;
        double d2 = dd * dd, d3 = d2 * dd, d4 = d3 * dd, d5 = d4 * dd, d6 = d5 * dd;

        double lat = phi1 - (nu1 * t1 / rho1) * (d2 / 2
            - (5 + 3 * t1_2 + 10 * c1 - 4 * c1 * c1 - 9 * ep2) * d4 / 24
            + (61 + 90 * t1_2 + 298 * c1 + 45 * t1_2 * t1_2 - 252 * ep2 - 3 * c1 * c1) * d6 / 720);

        double lon = lon0 + (dd
            - (1 + 2 * t1_2 + c1) * d3 / 6
            + (5 - 2 * c1 + 28 * t1_2 - 3 * c1 * c1 + 8 * ep2 + 24 * t1_2 * t1_2) * d5 / 120) / Math.Cos(phi1);

        return (lon * 180.0 / Math.PI, lat * 180.0 / Math.PI);
    }
}
