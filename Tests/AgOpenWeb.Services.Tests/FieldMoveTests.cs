// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.Headland;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services.GeoJson;
using AgOpenWeb.Services.Headland;
using TrackModel = AgOpenWeb.Models.Track.Track;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Moving a field's drawing (fork only): everything drawn moves by the same metres on the field's
/// plane, a round obstacle stays a circle, and what the GPS recorded (flags, recorded paths) and
/// the origin stay where they were.
/// </summary>
[TestFixture]
public class FieldMoveTests
{
    private const double OriginLat = 39.42147085, OriginLon = -7.55922737; // Tapada da Casa
    private const double DE = 1.5, DN = -2.25;
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"agopenweb_move_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static BoundaryPolygon Square(double e0, double n0, double side)
    {
        var p = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (e0, n0), (e0 + side, n0), (e0 + side, n0 + side), (e0, n0 + side) })
            p.Points.Add(new BoundaryPoint(e, n, 0));
        p.UpdateBounds();
        return p;
    }

    private static LocalPlane Plane() => new(new Wgs84(OriginLat, OriginLon), new SharedFieldProperties());

    private static (double lat, double lon) ToWgs84(double e, double n)
    {
        var w = Plane().ConvertGeoCoordToWgs84(new GeoCoord(n, e));
        return (w.Latitude, w.Longitude);
    }

    private static GeoCoord ToLocal(double lat, double lon) => Plane().ConvertWgs84ToGeoCoord(new Wgs84(lat, lon));

    /// <summary>A field with every part: boundary, a drawn obstacle, a tree, headland, tracks, a recorded path, a flag, a photo, a headland segment.</summary>
    private void SaveFullField()
    {
        var b = new Boundary { OuterBoundary = Square(0, 0, 200), HeadlandPolygon = Square(18, 18, 164) };
        b.InnerBoundaries.Add(Square(60, 60, 30));
        var tree = new BoundaryPolygon { AtObstacle = ObstacleAction.GoRound, IsHard = true };
        tree.SetCircle(new ObstacleCircle(140, 120, 8));
        b.InnerBoundaries.Add(tree);
        GeoJsonFieldService.Save(new Field
        {
            Name = "F", DirectoryPath = _dir, Origin = new Position { Latitude = OriginLat, Longitude = OriginLon }, Boundary = b,
        }, tracks: null);

        var recorded = TrackModel.FromABLine("Driven", new Vec3(30, 30, 0), new Vec3(30, 170, 0));
        recorded.Type = TrackType.RecordedPath;
        GeoJsonFieldService.SaveTracks(_dir, new[] { TrackModel.FromABLine("AB 34", new Vec3(10, 10, 0), new Vec3(10, 190, 0)), recorded });
        GeoJsonFieldService.SaveFlags(_dir, new[] { new Flag(50, 150, FlagColor.Red, 1, "Rock") });
        GeoJsonFieldService.SaveHeadlandLine(_dir, new HeadlandLine
        {
            Tracks = { new HeadlandPath { Name = "Headland", MoveDistance = 18, TrackPoints = { new Vec3(18, 18, 0), new Vec3(182, 18, 0), new Vec3(182, 182, 0) } } },
        });
        var (nwLat, nwLon) = ToWgs84(-20, 220);
        var (seLat, seLon) = ToWgs84(220, -20);
        GeoJsonFieldService.SaveBackground(_dir, new FieldBackground(FieldBackground.DefaultImageFile, nwLat, nwLon, seLat, seLon,
            new MercatorBounds(-841500.0, -841000.0, 4785000.0, 4785500.0)));
        HeadlandSegmentFileService.Save(_dir, new[]
        {
            new HeadlandSegment
            {
                Name = "South", Type = HeadlandSegmentType.Line, Offset = 18,
                BoundaryPoints = { new Vec3(0, 0, 0), new Vec3(200, 0, 0) },
                OffsetPoints = { new Vec3(0, 18, 0), new Vec3(200, 18, 0) },
            },
        });
    }

    private static void AssertMoved(double e, double n, double e0, double n0, string what)
    {
        Assert.That(e, Is.EqualTo(e0 + DE).Within(0.001), what + " easting");
        Assert.That(n, Is.EqualTo(n0 + DN).Within(0.001), what + " northing");
    }

    [Test]
    public void Everything_drawn_moves_and_what_the_GPS_recorded_stays()
    {
        SaveFullField();

        var result = GeoJsonFieldService.MoveField(_dir, DE, DN, moveTracks: true);

        Assert.That(result, Is.EqualTo(new GeoJsonFieldService.FieldMoveResult(1, 2, 2, 1, true)));
        var (field, tracks) = GeoJsonFieldService.Load(_dir);
        var b = field.Boundary!;
        Assert.Multiple(() =>
        {
            Assert.That(field.Origin.Latitude, Is.EqualTo(OriginLat), "the origin never moves");
            Assert.That(field.Origin.Longitude, Is.EqualTo(OriginLon));

            AssertMoved(b.OuterBoundary!.Points[2].Easting, b.OuterBoundary.Points[2].Northing, 200, 200, "outer corner");
            AssertMoved(b.InnerBoundaries[0].Points[0].Easting, b.InnerBoundaries[0].Points[0].Northing, 60, 60, "drawn obstacle");
            AssertMoved(b.HeadlandPolygon!.Points[0].Easting, b.HeadlandPolygon.Points[0].Northing, 18, 18, "headland polygon");

            var tree = b.InnerBoundaries[1];
            Assert.That(tree.Circle, Is.Not.Null, "a tree is still a circle, with its centre moved");
            AssertMoved(tree.Circle!.Value.CenterEasting, tree.Circle.Value.CenterNorthing, 140, 120, "tree centre");
            Assert.That(tree.Circle.Value.Diameter, Is.EqualTo(8));
            Assert.That((tree.AtObstacle, tree.IsHard), Is.EqualTo((ObstacleAction.GoRound, true)), "its choice is kept");

            var ab = tracks.Single(t => t.Name == "AB 34");
            AssertMoved(ab.Points[0].Easting, ab.Points[0].Northing, 10, 10, "track A");
            AssertMoved(ab.Points[1].Easting, ab.Points[1].Northing, 10, 190, "track B");
            var driven = tracks.Single(t => t.Name == "Driven");
            Assert.That(driven.Points[0].Easting, Is.EqualTo(30).Within(0.001), "a recorded path stays");

            var flag = GeoJsonFieldService.LoadFlags(_dir).Single();
            Assert.That((flag.Easting, flag.Northing), Is.EqualTo((50.0, 150.0)).Using<(double, double)>((x, y) =>
                Math.Abs(x.Item1 - y.Item1) < 0.001 && Math.Abs(x.Item2 - y.Item2) < 0.001), "a flag stays");

            var h = GeoJsonFieldService.LoadHeadlandLine(_dir).Tracks.Single();
            AssertMoved(h.TrackPoints[1].Easting, h.TrackPoints[1].Northing, 182, 18, "headland line");

            var seg = HeadlandSegmentFileService.Load(_dir).Single();
            AssertMoved(seg.BoundaryPoints[1].Easting, seg.BoundaryPoints[1].Northing, 200, 0, "segment boundary stretch");
            AssertMoved(seg.OffsetPoints[0].Easting, seg.OffsetPoints[0].Northing, 0, 18, "segment offset line");
        });
    }

    [Test]
    public void The_photo_moves_with_the_drawing()
    {
        SaveFullField();

        GeoJsonFieldService.MoveField(_dir, DE, DN, moveTracks: true);

        var bg = GeoJsonFieldService.LoadBackground(_dir)!;
        var nw = ToLocal(bg.NwLatitude, bg.NwLongitude);
        var se = ToLocal(bg.SeLatitude, bg.SeLongitude);
        Assert.Multiple(() =>
        {
            AssertMoved(nw.Easting, nw.Northing, -20, 220, "photo NW corner");
            AssertMoved(se.Easting, se.Northing, 220, -20, "photo SE corner");
            // Web Mercator stretches by 1/cos(latitude): a metre on the ground is more there.
            double stretch = 1 / Math.Cos(OriginLat * Math.PI / 180);
            var m = bg.Mercator!.Value;
            Assert.That(m.MinX, Is.EqualTo(-841500.0 + DE * stretch).Within(0.01));
            Assert.That(m.MaxX, Is.EqualTo(-841000.0 + DE * stretch).Within(0.01));
            Assert.That(m.MinY, Is.EqualTo(4785000.0 + DN * stretch).Within(0.01));
            Assert.That(m.MaxY, Is.EqualTo(4785500.0 + DN * stretch).Within(0.01));
        });
    }

    [Test]
    public void Tracks_can_stay_where_they_are()
    {
        SaveFullField();

        var result = GeoJsonFieldService.MoveField(_dir, DE, DN, moveTracks: false);

        var ab = GeoJsonFieldService.LoadTracks(_dir).Single(t => t.Name == "AB 34");
        var outer = GeoJsonFieldService.Load(_dir).field.Boundary!.OuterBoundary!;
        Assert.Multiple(() =>
        {
            Assert.That(result.Tracks, Is.Zero);
            Assert.That(ab.Points[0].Easting, Is.EqualTo(10).Within(0.001));
            Assert.That(ab.Points[0].Northing, Is.EqualTo(10).Within(0.001));
            AssertMoved(outer.Points[0].Easting, outer.Points[0].Northing, 0, 0, "outer corner");
        });
    }

    [Test]
    public void Moving_back_puts_every_point_where_it_was()
    {
        SaveFullField();
        var outerBefore = GeoJsonFieldService.Load(_dir).field.Boundary!.OuterBoundary!.Points.ToList();

        GeoJsonFieldService.MoveField(_dir, DE, DN, moveTracks: true);
        GeoJsonFieldService.MoveField(_dir, -DE, -DN, moveTracks: true);

        var outerAfter = GeoJsonFieldService.Load(_dir).field.Boundary!.OuterBoundary!.Points;
        Assert.That(outerAfter.Select(p => (p.Easting, p.Northing)),
            Is.EqualTo(outerBefore.Select(p => (p.Easting, p.Northing))).Using<(double, double)>((x, y) =>
                Math.Abs(x.Item1 - y.Item1) < 1e-6 && Math.Abs(x.Item2 - y.Item2) < 1e-6));
    }

    [Test]
    public void A_field_with_no_photo_or_segments_moves_too()
    {
        GeoJsonFieldService.Save(new Field
        {
            Name = "G", DirectoryPath = _dir, Origin = new Position { Latitude = OriginLat, Longitude = OriginLon },
            Boundary = new Boundary { OuterBoundary = Square(0, 0, 50) },
        }, tracks: null);

        var result = GeoJsonFieldService.MoveField(_dir, DE, DN, moveTracks: true);

        Assert.That(result, Is.EqualTo(new GeoJsonFieldService.FieldMoveResult(1, 0, 0, 0, false)));
        Assert.That(File.Exists(Path.Combine(_dir, "HeadlandSegments.json")), Is.False, "no segments file is made");
    }
}
