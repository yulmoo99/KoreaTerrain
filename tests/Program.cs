using KoreaTerrain;
using System.Globalization;
using System.Text.Json;

int checks = 0;
void Check(bool pass, string name) { if (!pass) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name) { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; } Check(rejected, name); }
var center = ExampleLocation.Center;
var grid = TerrainGrid.Create(center, 500, 30);
Check(grid.Points.Length == 289 && grid.Side == 17, "500m boundary-inclusive grid");
Check(grid.Points.Select(p => (p.East, p.North)).Distinct().Count() == 289, "unique XY");
Check(grid.Points.First().East == -250 && grid.Points.Last().North == 250, "bounds in metres");
Check(grid.Points[144].Location == center, "centre alignment");
Check(TerrainGrid.Offset(center, 10, 10).Latitude > center.Latitude && TerrainGrid.Offset(center, 10, 10).Longitude > center.Longitude, "east/north orientation");
Reject(() => TerrainGrid.Create(center, double.NaN, 30), "NaN rejected");
Reject(() => TerrainGrid.Create(center, 2100, 30), "oversized extent rejected");
Reject(() => TerrainGrid.Create(center, 500, 1), "oversampling rejected");
Reject(() => TerrainGrid.Create(new(0, 0), 500, 30), "out-of-region rejected");
string Response(object? z, double? lat = null, string dataset = "srtm30m") => JsonSerializer.Serialize(new { status = "OK", results = new[] { new { elevation = z, dataset, location = new { lat = lat ?? center.Latitude, lng = center.Longitude } } } });
Check(TerrainService.ParseElevations(Response(0), new[] { center })[0] == 0, "zero elevation is valid");
Reject(() => TerrainService.ParseElevations(Response(null), new[] { center }), "NoData is never zero-filled");
Reject(() => TerrainService.ParseElevations(Response(20, 36), new[] { center }), "coordinate mismatch rejected");
Reject(() => TerrainService.ParseElevations(Response(20, dataset: "aster30m"), new[] { center }), "wrong dataset rejected");
Reject(() => TerrainService.ParseElevations(Response(20), new[] { center, center }), "partial response rejected");
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
Check(TerrainGrid.Create(center, 500, 30).Points.Length == 289, "locale-independent grid");
MapPoint[] Ring(double x1, double y1, double x2, double y2) => new[] { new MapPoint(x1,y1), new MapPoint(x2,y1), new MapPoint(x2,y2), new MapPoint(x1,y2), new MapPoint(x1,y1) };
ParcelFeature Parcel(string id, params MapPoint[][] rings) => new(id, id, new[] { new ParcelPolygon(rings) });
var left = Parcel("1111111111111111111", Ring(-20, -10, 0, 10));
var right = Parcel("2222222222222222222", Ring(0, -10, 20, 10));
var parcels = new[] { left, right };
var selected = parcels.Select(p => p.Id).ToHashSet();
var union = MapGeometry.SelectedArea(parcels, selected);
Check(Math.Abs(union.Area - 800) < .0001 && Math.Abs(union.Boundary.Length - 120) < .0001, "adjacent parcels merged without internal edge");
Check(MapGeometry.AutoSelect(parcels).Length == 0, "shared-boundary address does not auto-select arbitrarily");
var surrounding = Parcel("3333333333333333333", Ring(-20, -20, 20, 20));
Check(MapGeometry.AutoSelect(new[] { surrounding }).Single() == surrounding.Id, "containing parcel auto-selected");
var holed = Parcel("4444444444444444444", Ring(-20,-20,20,20), Ring(-5,-5,5,5));
Check(MapGeometry.AutoSelect(new[] { holed }).Length == 0, "point in courtyard hole not selected");
var giant = Parcel("5555555555555555555", Ring(-200,-200,200,200));
Check(MapGeometry.Boundary(new[] { giant }, new HashSet<string> { giant.Id }, 120).Length == 0, "crop does not fabricate site boundary");
var separate = Parcel("6666666666666666666", Ring(50,50,60,60));
Check(MapGeometry.SelectedArea(new[] { left, separate }, new HashSet<string> { left.Id, separate.Id }).NumGeometries == 2, "disconnected parcels remain disconnected");
var offset = TerrainGrid.Offset(center, 123, -45);
var local = MapGeometry.Local(center, offset);
Check(Math.Abs(local.X - 123) < .00001 && Math.Abs(local.Y + 45) < .00001, "terrain and vectors share coordinate transform");
Check(MapLayerService.ParseWidth("12 m") == 12 && MapLayerService.ParseWidth("12'6\"") == null && MapLayerService.ParseWidth("NaN") == null, "unknown width units never treated as metres");
var road = new RoadFeature("1", "test", new[] { new MapPoint(-200,0), new MapPoint(200,0) }, 10);
var area = MapGeometry.RoadArea(road, 6, 120);
Check(Math.Abs(area.Area - 1200) < .001 && area.EnvelopeInternal.MinX == -60, "road width and extent clipped correctly");
double TriangleArea(MapPoint[] t) => Math.Abs((t[1].X-t[0].X)*(t[2].Y-t[0].Y)-(t[1].Y-t[0].Y)*(t[2].X-t[0].X))/2;
Check(Math.Abs(MapGeometry.RoadTriangles(area).Sum(TriangleArea) - 1200) < .001, "road triangles preserve footprint area");
var holedShape = MapGeometry.Parcel(holed);
Check(Math.Abs(MapGeometry.RoadTriangles(holedShape).Sum(TriangleArea) - 1500) < .001, "surface mesh preserves holes");
Reject(() => MapGeometry.RoadArea(road, double.NaN, 120), "invalid road width rejected");
var planar = new TerrainData(center, 120, 60, 3, TerrainGrid.Create(center, 120, 60).Points.Select(p => p with { Elevation = 100 + p.East / 10 + p.North / 20 }).ToArray(), "test", DateTimeOffset.UtcNow);
Check(Math.Abs(MapGeometry.Height(planar, new(15,20)) - 102.5) < .00001, "height interpolation across grid");
Reject(() => MapLayerService.ValidateParcelSettings(500, "", ""), "missing parcel credentials rejected");
Reject(() => MapLayerService.ValidateParcelSettings(1500, "test", "https://example.com"), "parcel API area limit enforced");
object[] GeoRing(MapPoint[] ring) => ring.Select(p => { var g = TerrainGrid.Offset(center,p.X,p.Y); return (object)new[] { g.Longitude,g.Latitude }; }).ToArray();
var parcelJson = JsonSerializer.Serialize(new { response = new { status = "OK", record = new { total = "1" }, page = new { total = "1", current = "1" }, result = new { featureCollection = new { features = new[] { new { properties = new { pnu = surrounding.Id, addr = "Synthetic test only" }, geometry = new { type = "Polygon", coordinates = new[] { GeoRing(Ring(-20,-20,20,20)) } } } } } } } });
var parsedParcel = MapLayerService.ParseParcels(parcelJson, center);
Check(parsedParcel.Features.Length == 1 && MapGeometry.AutoSelect(parsedParcel.Features).Length == 1, "VWorld GeoJSON envelope and lon/lat order");
Reject(() => MapLayerService.ParseParcels("{\"response\":{\"status\":\"ERROR\"}}", center), "parcel service errors not treated as empty success");
Reject(() => MapLayerService.ParseRoads("{\"remark\":\"timeout\",\"elements\":[]}", center), "partial Overpass response rejected");
string RoadJson(bool bridge) => JsonSerializer.Serialize(new { elements = new[] { new { type = "way", id = 1, tags = new Dictionary<string,string> { ["highway"] = "residential", ["bridge"] = bridge ? "yes" : "no", ["width"] = "7 m" }, geometry = new[] { new { lat = center.Latitude, lon = center.Longitude }, new { lat = center.Latitude + .001, lon = center.Longitude } } } } });
Check(MapLayerService.ParseRoads(RoadJson(true), center).Excluded == 1, "bridges not flattened onto terrain");
Check(MapLayerService.ParseRoads(RoadJson(false), center).Features.Single().Width == 7, "road geometry and width parsed");
Console.WriteLine($"{checks} checks passed.");
if (args.Length == 0 || args[0] != "--roads") await RoadRetryChecks.Run();

if (args.Length == 2 && args[0] == "--roads")
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
    var roads = await new MapLayerService(http).RoadsAsync(center, 500, CancellationToken.None);
    Directory.CreateDirectory(args[1]);
    await File.WriteAllTextAsync(Path.Combine(args[1], "nmc-roads.json"), JsonSerializer.Serialize(roads, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"LIVE roads={roads.Features.Length}, unknownWidth={roads.Features.Count(r=>r.Width==null)}, excluded={roads.Excluded}");
}
if (args.Length == 2 && args[0] == "--road-sample")
{
    var roads = JsonSerializer.Deserialize<RoadData>(await File.ReadAllTextAsync(args[1]))!;
    int count = 0;
    foreach (var item in roads.Features)
    {
        var shape = MapGeometry.RoadArea(item, 6, 500);
        var triangles = MapGeometry.RoadTriangles(shape).ToArray(); count += triangles.Length;
        if (Math.Abs(triangles.Sum(TriangleArea) - shape.Area) > .01) throw new Exception("Road surface loses area: " + item.Id);
        if (triangles.SelectMany(t=>t).Any(p=>Math.Abs(p.X)>250.00001 || Math.Abs(p.Y)>250.00001)) throw new Exception("Road surface exceeds extent");
    }
    Console.WriteLine($"PASS real road sample clipping and triangulation: {roads.Features.Length} roads, {count} triangles");
}

if (args.Length == 2 && args[0] == "--live")
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
    var terrain = await new TerrainService(http).FetchAsync(center, 500, 30, null, CancellationToken.None);
    Directory.CreateDirectory(args[1]);
    await File.WriteAllTextAsync(Path.Combine(args[1], "nmc-terrain.json"), JsonSerializer.Serialize(terrain, new JsonSerializerOptions { WriteIndented = true }));
    var rows = new[] { "east_m,north_m,elevation_egm96_m,latitude,longitude" }.Concat(terrain.Points.Select(p => FormattableString.Invariant($"{p.East:F4},{p.North:F4},{p.Elevation:F3},{p.Location.Latitude:F8},{p.Location.Longitude:F8}")));
    await File.WriteAllLinesAsync(Path.Combine(args[1], "nmc-terrain.csv"), rows);
    Console.WriteLine(FormattableString.Invariant($"LIVE points={terrain.Points.Length}, min={terrain.Minimum}, max={terrain.Maximum}, spacing={terrain.Spacing}"));
}
