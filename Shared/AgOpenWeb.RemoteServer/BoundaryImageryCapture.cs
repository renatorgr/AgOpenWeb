// Phase MT — Draw boundary on map. Host-side aerial capture: fetch aerial imagery
// covering a drawn boundary's Web-Mercator bounds and composite it into a single PNG
// that becomes the field background. Mirrors the native BoundaryMapDialog's
// CaptureBackgroundImageAsync, but driven by bounds the web client supplies (it captures
// nothing itself — the host stays the brain). SkiaSharp is available via Avalonia.
//
// Provider order (first success wins), see #imagery-source in RemoteServerHost.cs
// for why: IFAP's own rastersapi first — reverse-engineered from the public iSIP
// viewer's own JS (see IfapRasterCapture.cs), it's the same "Ortofotomapas mais
// Recentes" layer that viewer shows, confirmed 2025-05-23 imagery for a Castelo de
// Vide, PT parcel — fresher than anything else tried here. Falls back to the DGT
// (Direção-Geral do Território) WMS — official Portuguese state orthophotos, free,
// no key, CC-BY-4.0, refreshed roughly every 2-3 years (Ortos2018, Ortos2021...).
// Falls back further to Google Map Tiles API (if a local key is configured) or the
// original keyless Bing endpoint for anywhere neither Portuguese source covers
// (Azores, Madeira, outside PT) or if both are unreachable.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SkiaSharp;

namespace AgOpenWeb.RemoteServer;

public static class BoundaryImageryCapture
{
    private const double WorldSize = 2.0 * Math.PI * 6378137.0; // Web-Mercator extent (m)

    // DGT WMS — mainland Portugal only. Its own capabilities advertise
    // MaxWidth=4096/MaxHeight=4096, matching the pixel cap this capture already uses,
    // and it covers Web Mercator (EPSG:3857) directly, so bounds need no reprojection.
    // https://cartografia.dgterritorio.gov.pt/wms/ortos2021 — CC-BY-4.0, "Informação
    // geográfica cedida pela Direção-Geral do Território".
    private const string DgtWmsBaseUrl = "https://cartografia.dgterritorio.gov.pt/wms/ortos2021";
    private const string DgtWmsLayer = "Ortos2021-RGB";
    // Mainland Portugal bounding box in EPSG:3857 (from the layer's own EX_GeographicBoundingBox,
    // reprojected) — used to skip the DGT attempt entirely for Azores/Madeira/anywhere else,
    // rather than waiting on a request the service will just return blank/error for.
    private const double DgtMinX = -1_134_720, DgtMaxX = -635_583;
    private const double DgtMinY = 4_406_300, DgtMaxY = 5_202_960;

    /// <summary>
    /// Fetch + composite the aerial covering the given Web-Mercator bbox into a PNG.
    /// Returns the output file path, or null on failure. Runs off the UI thread.
    ///
    /// SkiaSharp here links libGL + libfontconfig natively and can hard-crash (SIGSEGV)
    /// on a headless Linux board — uncatchable in-process. So the host NEVER calls this
    /// directly; it spawns a child process (see ImageryCaptureProcess) that calls this and
    /// exits, so a native crash takes down only the child. <paramref name="outPath"/> lets
    /// the parent name the file it then reads back; null = a temp file (legacy callers).
    /// </summary>
    public static async Task<string?> CaptureAsync(
        double mercMinX, double mercMaxX, double mercMinY, double mercMaxY, string? outPath = null)
    {
        double mercWidth = mercMaxX - mercMinX, mercHeight = mercMaxY - mercMinY;
        if (mercWidth <= 0 || mercHeight <= 0) return null;

        const int maxPixels = 4096;

        // Try IFAP's rastersapi first — same source the public iSIP viewer's
        // "Ortofotomapas mais Recentes" layer uses, generally the freshest option
        // for mainland Portugal. It composes its own bitmap (different tile grid,
        // different CRS — see IfapRasterCapture) rather than reusing the Bing/Google
        // tile path below.
        {
            const double targetResolution = 0.5; // matches DEFAULT resolution the tiles were calibrated against
            int outW = Math.Clamp((int)Math.Ceiling(mercWidth / targetResolution), 1, maxPixels);
            int outH = Math.Clamp((int)Math.Ceiling(mercHeight / targetResolution), 1, maxPixels);
            SKBitmap? ifapBitmap = null;
            try
            {
                ifapBitmap = await IfapRasterCapture.TryCaptureAsync(
                    mercMinX, mercMaxX, mercMinY, mercMaxY, outW, outH).ConfigureAwait(false);
            }
            catch { /* falls through to DGT/Google/Bing below */ }
            if (ifapBitmap is not null)
            {
                using (ifapBitmap)
                    return SaveBitmap(ifapBitmap, outPath);
            }
        }

        // Try DGT next when the requested area overlaps mainland Portugal — one GetMap
        // call returns the finished, already-cropped image directly (no tile grid to
        // assemble). Any failure (network, service down, genuinely outside coverage
        // despite the bbox overlap check) falls through to the tile-based path below.
        bool overlapsPortugal =
            mercMinX < DgtMaxX && mercMaxX > DgtMinX && mercMinY < DgtMaxY && mercMaxY > DgtMinY;
        if (overlapsPortugal)
        {
            var dgtPath = await TryCaptureFromDgtAsync(
                mercMinX, mercMaxX, mercMinY, mercMaxY, maxPixels, outPath).ConfigureAwait(false);
            if (dgtPath is not null) return dgtPath;
        }

        // Pick the finest tile zoom whose output stays within the pixel cap (memory ceiling).
        int zoom = 1;
        double resolution = WorldSize / 256.0; // m/px at z0
        for (int z = 20; z >= 1; z--)
        {
            double res = WorldSize / (256.0 * (1 << z));
            if (Math.Max(mercWidth, mercHeight) / res <= maxPixels) { zoom = z; resolution = res; break; }
        }

        int outW2 = Math.Max((int)Math.Ceiling(mercWidth / resolution), 1);
        int outH2 = Math.Max((int)Math.Ceiling(mercHeight / resolution), 1);
        double tileMerc = WorldSize / (1 << zoom);

        int txMin = (int)Math.Floor((mercMinX + WorldSize / 2) / tileMerc);
        int txMax = (int)Math.Floor((mercMaxX + WorldSize / 2) / tileMerc);
        int tyMin = (int)Math.Floor((WorldSize / 2 - mercMaxY) / tileMerc); // tile y grows southward
        int tyMax = (int)Math.Floor((WorldSize / 2 - mercMinY) / tileMerc);
        if ((txMax - txMin) > 64 || (tyMax - tyMin) > 64) return null; // sanity

        // Fetch all tiles concurrently (cached in RemoteServerHost).
        var jobs = new List<Task<(int tx, int ty, byte[]? bytes)>>();
        for (int tx = txMin; tx <= txMax; tx++)
            for (int ty = tyMin; ty <= tyMax; ty++)
            {
                int cx = tx, cy = ty;
                jobs.Add(Task.Run(async () => (cx, cy, await RemoteServerHost.FetchSatTileAsync(Quadkey(cx, cy, zoom)))));
            }
        var results = await Task.WhenAll(jobs);

        using var bitmap = new SKBitmap(outW2, outH2, SKColorType.Rgba8888, SKAlphaType.Opaque);
        int drawn = 0;
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High })
        {
            canvas.Clear(SKColors.Black);
            foreach (var (tx, ty, bytes) in results)
            {
                if (bytes is null || bytes.Length < 1000) continue; // skip placeholders/empties
                using var tile = SKBitmap.Decode(bytes);
                if (tile is null) continue;
                double tMinX = -WorldSize / 2 + tx * tileMerc, tMaxX = tMinX + tileMerc;
                double tMaxY = WorldSize / 2 - ty * tileMerc, tMinY = tMaxY - tileMerc;
                var dst = new SKRect(
                    (float)((tMinX - mercMinX) / resolution),
                    (float)((mercMaxY - tMaxY) / resolution),  // pixel y inverted vs Mercator y
                    (float)((tMaxX - mercMinX) / resolution),
                    (float)((mercMaxY - tMinY) / resolution));
                canvas.DrawBitmap(tile, dst, paint);
                drawn++;
            }
        }
        if (drawn == 0) return null;

        return SaveBitmap(bitmap, outPath);
    }

    /// <summary>
    /// One WMS GetMap request to the DGT's Ortos service, covering the full bbox in a
    /// single image — no tile grid needed. Returns the saved path, or null on any
    /// failure (caller falls back to the tile-based providers).
    /// </summary>
    private static async Task<string?> TryCaptureFromDgtAsync(
        double mercMinX, double mercMaxX, double mercMinY, double mercMaxY,
        int maxPixels, string? outPath)
    {
        try
        {
            double mercWidth = mercMaxX - mercMinX, mercHeight = mercMaxY - mercMinY;
            // Aim for ~0.25 m/px (the DGT layer's native resolution) but never exceed
            // the service's own MaxWidth/MaxHeight=4096 advertised in its capabilities.
            const double targetResolution = 0.25;
            int outW = Math.Clamp((int)Math.Ceiling(mercWidth / targetResolution), 1, maxPixels);
            int outH = Math.Clamp((int)Math.Ceiling(mercHeight / targetResolution), 1, maxPixels);

            var url = $"{DgtWmsBaseUrl}?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap" +
                      $"&LAYERS={DgtWmsLayer}&STYLES=" +
                      $"&BBOX={mercMinX.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                      $"{mercMinY.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                      $"{mercMaxX.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                      $"{mercMaxY.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                      $"&CRS=EPSG:3857&WIDTH={outW}&HEIGHT={outH}&FORMAT=image/jpeg";

            var bytes = await RemoteServerHost.FetchDgtMapAsync(url).ConfigureAwait(false);
            // A WMS error still comes back with a 200 and an XML/text body, not JPEG bytes —
            // check for a real image rather than trusting the HTTP status alone.
            if (bytes is null || bytes.Length < 1000) return null;
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is null) return null;

            return SaveBitmap(bitmap, outPath);
        }
        catch { return null; }
    }

    private static string SaveBitmap(SKBitmap bitmap, string? outPath)
    {
        string path;
        if (outPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            path = outPath;
        }
        else
        {
            var dir = Path.Combine(Path.GetTempPath(), "AgOpenWeb_SatCap");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, "BackPic_" + Guid.NewGuid().ToString("N") + ".png");
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(path);
        data.SaveTo(fs);
        return path;
    }

    private static string Quadkey(int x, int y, int z)
    {
        var sb = new StringBuilder(z);
        for (int i = z; i > 0; i--)
        {
            int d = 0, m = 1 << (i - 1);
            if ((x & m) != 0) d += 1;
            if ((y & m) != 0) d += 2;
            sb.Append((char)('0' + d));
        }
        return sb.ToString();
    }
}
