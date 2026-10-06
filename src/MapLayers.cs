using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;

namespace KoreaTerrain;

public record MapPoint(double X, double Y);
public record ParcelPolygon(MapPoint[][] Rings);
public record ParcelFeature(string Id, string Label, ParcelPolygon[] Polygons)
{
    public override string ToString() => Label;
}
public record RoadFeature(string Id, string Name, MapPoint[] Points, double? Width);
public record RoadData(RoadFeature[] Features, int Excluded, DateTimeOffset RetrievedAt);
public record ParcelPage(ParcelFeature[] Features, int TotalPages, int CurrentPage, int TotalRecords);

public static class MapGeometry
{
    public static readonly GeometryFactory Factory = new();
    public static MapPoint Local(GeoPoint center, GeoPoint point)
    {
        var unit = TerrainGrid.Offset(center, 1, 1);
        return new((point.Longitude - center.Longitude) / (unit.Longitude - center.Longitude),
            (point.Latitude - center.Latitude) / (unit.Latitude - center.Latitude));
    }
    public static Coordinate C(MapPoint p) => new(p.X, p.Y);
    public static MapPoint P(Coordinate p) => new(p.X, p.Y);
    public static Geometry Box(double width) => Factory.ToGeometry(new Envelope(-width / 2, width / 2, -width / 2, width / 2));
    public static Geometry Parcel(ParcelFeature parcel)
    {
        var polygons = parcel.Polygons.Select(p => Factory.CreatePolygon(
            Factory.CreateLinearRing(p.Rings[0].Select(C).ToArray()),
            p.Rings.Skip(1).Select(r => Factory.CreateLinearRing(r.Select(C).ToArray())).ToArray())).ToArray();
        return Factory.CreateMultiPolygon(polygons);
    }
    public static string[] AutoSelect(IEnumerable<ParcelFeature> parcels)
    {
        var center = Factory.CreatePoint(new Coordinate(0, 0));
        var containing = parcels.Where(p => Parcel(p).Covers(center)).Select(p => p.Id).ToArray();
        // A point on a shared boundary is ambiguous; do not silently choose either parcel.
        return containing.Length == 1 ? containing : Array.Empty<string>();
    }
    public static Geometry SelectedArea(IEnumerable<ParcelFeature> parcels, ISet<string> ids)
    {
        var geometries = parcels.Where(p => ids.Contains(p.Id)).Select(Parcel).ToArray();
        return geometries.Length == 0 ? Factory.CreatePolygon() : UnaryUnionOp.Union(geometries);
    }
    public static MapPoint[][] Boundary(IEnumerable<ParcelFeature> parcels, ISet<string> ids, double width)
    {
        // Clip the boundary, NOT the polygon: the extent must not become a fake parcel edge.
        var area = SelectedArea(parcels, ids);
        return area.IsEmpty ? Array.Empty<MapPoint[]>() : Lines(area.Boundary.Intersection(Box(width))).ToArray();
    }
    public static IEnumerable<MapPoint[]> Lines(Geometry geometry)
    {
        if (geometry.IsEmpty) yield break;
        if (geometry is LineString line) { yield return line.Coordinates.Select(P).ToArray(); yield break; }
        for (int i = 0; i < geometry.NumGeometries && geometry is GeometryCollection; i++)
            foreach (var part in Lines(geometry.GetGeometryN(i))) yield return part;
    }
    public static Geometry RoadArea(RoadFeature road, double fallbackWidth, double extent)
    {
        if (!double.IsFinite(fallbackWidth) || fallbackWidth < 1 || fallbackWidth > 40)
            throw new ArgumentException("임시 도로 폭은 1~40m로 입력하세요.");
        return Factory.CreateLineString(road.Points.Select(C).ToArray()).Buffer((road.Width ?? fallbackWidth) / 2, 4)
            .Intersection(Box(extent));
    }
    public static double Height(TerrainData terrain, MapPoint p)
    {
        double x = Math.Clamp((p.X + terrain.Width / 2) / terrain.Spacing, 0, terrain.Side - 1);
        double y = Math.Clamp((p.Y + terrain.Width / 2) / terrain.Spacing, 0, terrain.Side - 1);
        int ix = Math.Min((int)x, terrain.Side - 2), iy = Math.Min((int)y, terrain.Side - 2);
        double u = x - ix, v = y - iy;
        double H(int dx, int dy) => terrain.Points[(iy + dy) * terrain.Side + ix + dx].Elevation;
        return (1 - v) * ((1 - u) * H(0, 0) + u * H(1, 0)) + v * ((1 - u) * H(0, 1) + u * H(1, 1));
    }
    public static IEnumerable<Polygon> Polygons(Geometry geometry)
    {
        if (geometry.IsEmpty) yield break;
        if (geometry is Polygon polygon) { yield return polygon; yield break; }
        if (geometry is GeometryCollection)
            for (int i = 0; i < geometry.NumGeometries; i++)
                foreach (var child in Polygons(geometry.GetGeometryN(i))) yield return child;
    }
    public static IEnumerable<MapPoint[]> RoadTriangles(Geometry area)
    {
        foreach (var polygon in Polygons(area))
        {
            var envelope = polygon.EnvelopeInternal;
            const double step = 15;
            for (double x = Math.Floor(envelope.MinX / step) * step; x < envelope.MaxX; x += step)
            for (double y = Math.Floor(envelope.MinY / step) * step; y < envelope.MaxY; y += step)
            {
                var tile = Factory.ToGeometry(new Envelope(x, x + step, y, y + step));
                foreach (var part in Polygons(polygon.Intersection(tile)))
                {
                    if (part.Area < .00001) continue;
                    foreach (var triangle in Polygons(NetTopologySuite.Triangulate.Polygon.PolygonTriangulator.Triangulate(part)))
                        if (triangle.Area >= .00001) yield return triangle.Coordinates.Take(3).Select(P).ToArray();
                }
            }
        }
    }
}

public sealed class MapLayerService
{
    private readonly HttpClient http;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private static readonly SemaphoreSlim RoadGate = new(1, 1);
    private static readonly Dictionary<string, RoadData> RoadCache = new();
    private static DateTimeOffset lastRoadRequest = DateTimeOffset.MinValue;
    public MapLayerService(HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.http = http;
        this.delay = delay ?? Task.Delay;
    }

    public async Task<RoadData> RoadsAsync(GeoPoint center, double width, CancellationToken ct, IProgress<string>? progress = null)
    {
        _ = TerrainGrid.Create(center, width, 30);
        var sw = TerrainGrid.Offset(center, -width / 2 - 50, -width / 2 - 50);
        var ne = TerrainGrid.Offset(center, width / 2 + 50, width / 2 + 50);
        string cacheKey = FormattableString.Invariant($"{center.Latitude:R},{center.Longitude:R},{width:R}");
        await RoadGate.WaitAsync(ct);
        try
        {
            if (RoadCache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.RetrievedAt < TimeSpan.FromMinutes(10)) return cached;
            for (int attempt = 0; attempt < 2; attempt++)
            {
            var pause = TimeSpan.FromSeconds(30) - (DateTimeOffset.UtcNow - lastRoadRequest);
            if (pause > TimeSpan.Zero) { progress?.Report("도로 서버 요청 간격을 기다리는 중입니다. 지형은 유지됩니다."); await delay(pause, ct); }
            progress?.Report(attempt == 0 ? "주변 도로 조회 중… 서버 상황에 따라 최대 90초 걸릴 수 있습니다." : "도로 자동 재시도 중 (1회)…");
            string query = FormattableString.Invariant($"[out:json][timeout:60];way[\"highway\"~\"^(motorway|trunk|primary|secondary|tertiary|unclassified|residential|living_street|service|motorway_link|trunk_link|primary_link|secondary_link|tertiary_link)$\"]({sw.Latitude:F8},{sw.Longitude:F8},{ne.Latitude:F8},{ne.Longitude:F8});out geom;");
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://overpass-api.de/api/interpreter");
            request.Headers.UserAgent.ParseAdd("KoreaTerrain-Prototype/0.3");
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["data"] = query });
            lastRoadRequest = DateTimeOffset.UtcNow;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            try
            {
            using var response = await http.SendAsync(request, deadline.Token);
            if (attempt == 0 && (int)response.StatusCode is 408 or 502 or 503 or 504)
            {
                progress?.Report($"도로 서버 응답 지연 ({(int)response.StatusCode}). 30초 후 한 번 재시도합니다.");
                await delay(TimeSpan.FromSeconds(30), ct);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"도로 조회 실패 ({(int)response.StatusCode}). 잠시 후 다시 시도하거나 도로 옵션을 꺼주세요.");
            var result = ParseRoads(await response.Content.ReadAsStringAsync(deadline.Token), center);
            if (RoadCache.Count > 12) RoadCache.Clear();
            RoadCache[cacheKey] = result;
            return result;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt == 1) throw new InvalidOperationException("도로 서버 응답 시간이 초과되었습니다. 지형은 유지됩니다. 도로만 다시 불러올 수 있습니다.");
                progress?.Report("도로 응답 시간이 초과되어 30초 후 한 번 재시도합니다.");
                await delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (HttpRequestException)
            {
                if (attempt == 1) throw new InvalidOperationException("도로 서버에 연결하지 못했습니다. 지형은 유지됩니다.");
                progress?.Report("도로 연결에 실패해 30초 후 한 번 재시도합니다.");
                await delay(TimeSpan.FromSeconds(30), ct);
            }
            }
            throw new InvalidOperationException("도로 조회를 완료하지 못했습니다.");
        }
        finally { RoadGate.Release(); }
    }

    public static RoadData ParseRoads(string body, GeoPoint center)
    {
        using var json = JsonDocument.Parse(body);
        if (json.RootElement.TryGetProperty("remark", out _)) throw new InvalidOperationException("도로 조회가 일부만 처리되어 중단했습니다. 범위를 줄여 다시 시도하세요.");
        var roads = new List<RoadFeature>(); int excluded = 0;
        foreach (var way in json.RootElement.GetProperty("elements").EnumerateArray())
        {
            if (way.GetProperty("type").GetString() != "way") continue;
            var tags = way.GetProperty("tags");
            string Tag(string name) => tags.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
            bool Elevated(string name) => Tag(name) is not ("" or "no" or "0");
            if (Elevated("bridge") || Elevated("tunnel") || Elevated("layer") || Tag("area") == "yes") { excluded++; continue; }
            if (!way.TryGetProperty("geometry", out var points)) throw new InvalidOperationException("도로 좌표가 누락되었습니다.");
            var local = points.EnumerateArray().Select(p => ReadPoint(p.GetProperty("lat").GetDouble(), p.GetProperty("lon").GetDouble(), center)).ToArray();
            var clean = new List<MapPoint>();
            foreach (var point in local) if (clean.Count == 0 || point != clean[^1]) clean.Add(point);
            if (clean.Count < 2) { excluded++; continue; }
            roads.Add(new(way.GetProperty("id").ToString(), Tag("name") is "" ? Tag("highway") : Tag("name"), clean.ToArray(), ParseWidth(Tag("width"))));
        }
        if (roads.Count > 2000) throw new InvalidOperationException("도로가 너무 많습니다. 범위를 줄여주세요.");
        return new(roads.ToArray(), excluded, DateTimeOffset.UtcNow);
    }
    public static double? ParseWidth(string raw)
    {
        string value = raw.Trim();
        if (value.EndsWith(" m", StringComparison.Ordinal)) value = value[..^2].Trim();
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n >= 1 && n <= 80 ? n : null;
    }
    private static MapPoint ReadPoint(double lat, double lon, GeoPoint center)
    {
        if (!double.IsFinite(lat) || !double.IsFinite(lon) || lat < 32 || lat > 40 || lon < 123 || lon > 133)
            throw new InvalidOperationException("지도 응답의 좌표계 또는 위치를 확인할 수 없습니다.");
        return MapGeometry.Local(center, new(lat, lon));
    }

    public async Task<ParcelFeature[]> ParcelsAsync(GeoPoint center, double width, string key, string domain, CancellationToken ct)
    {
        ValidateParcelSettings(width, key, domain);
        _ = TerrainGrid.Create(center, width, 30);
        var sw = TerrainGrid.Offset(center, -width / 2, -width / 2);
        var ne = TerrainGrid.Offset(center, width / 2, width / 2);
        var all = new Dictionary<string, ParcelFeature>();
        int expectedTotal = -1;
        for (int page = 1; page <= 20; page++)
        {
            var query = new Dictionary<string, string> {
                ["service"] = "data", ["version"] = "2.0", ["request"] = "GetFeature", ["data"] = "LP_PA_CBND_BUBUN",
                ["key"] = key.Trim(), ["domain"] = domain.Trim(), ["crs"] = "EPSG:4326", ["format"] = "json",
                ["size"] = "1000", ["page"] = page.ToString(CultureInfo.InvariantCulture), ["geometry"] = "true", ["attribute"] = "true",
                ["geomFilter"] = FormattableString.Invariant($"BOX({sw.Longitude:F8},{sw.Latitude:F8},{ne.Longitude:F8},{ne.Latitude:F8})")
            };
            string url = "https://api.vworld.kr/req/data?" + string.Join("&", query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
            string body;
            try
            {
                using var response = await http.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"필지 조회 실패 ({(int)response.StatusCode}). 인증키와 등록 URL을 확인하세요.");
                body = await response.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException) { throw new InvalidOperationException("브이월드에 연결하지 못했습니다. 네트워크 상태를 확인하세요."); }
            var parsed = ParseParcels(body, center);
            if (parsed.CurrentPage != page && parsed.TotalRecords != 0) throw new InvalidOperationException("필지 응답의 페이지 순서가 다릅니다.");
            if (expectedTotal >= 0 && expectedTotal != parsed.TotalRecords) throw new InvalidOperationException("조회 중 필지 목록이 변경되었습니다. 다시 조회하세요.");
            expectedTotal = parsed.TotalRecords;
            if (expectedTotal > 5000 || parsed.TotalPages > 20) throw new InvalidOperationException("필지가 너무 많습니다. 범위를 줄여주세요.");
            foreach (var item in parsed.Features) all[item.Id] = item;
            if (page >= parsed.TotalPages)
            {
                if (all.Count != expectedTotal) throw new InvalidOperationException("필지 응답이 누락되거나 중복되었습니다. 범위를 줄여 다시 조회하세요.");
                return all.Values.ToArray();
            }
            if (parsed.Features.Length == 0) throw new InvalidOperationException("필지 응답 페이지가 비어 있습니다.");
            await Task.Delay(200, ct);
        }
        throw new InvalidOperationException("필지 조회 한도를 초과했습니다.");
    }
    public static void ValidateParcelSettings(double width, string key, string domain)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(domain)) throw new ArgumentException("필지 경계를 가져오려면 브이월드 인증키와 발급 시 등록한 URL을 입력하세요.");
        if (width > 1400) throw new ArgumentException("필지 조회를 포함하면 범위를 1,400m 이하로 설정하세요.");
    }
    public static ParcelPage ParseParcels(string body, GeoPoint center)
    {
        using var json = JsonDocument.Parse(body);
        var response = json.RootElement.GetProperty("response");
        string? status = response.GetProperty("status").GetString();
        if (status == "NOT_FOUND") return new(Array.Empty<ParcelFeature>(), 1, 1, 0);
        if (status != "OK") throw new InvalidOperationException("브이월드 필지 조회 오류입니다. 인증키·등록 URL·서비스 권한을 확인하세요.");
        int Number(JsonElement obj, string property) => int.Parse(obj.GetProperty(property).ToString(), CultureInfo.InvariantCulture);
        int total = Number(response.GetProperty("record"), "total");
        int pages = Number(response.GetProperty("page"), "total"), current = Number(response.GetProperty("page"), "current");
        if (total < 0 || pages < 1 || current < 1) throw new InvalidOperationException("필지 응답 건수가 올바르지 않습니다.");
        var features = new List<ParcelFeature>();
        foreach (var feature in response.GetProperty("result").GetProperty("featureCollection").GetProperty("features").EnumerateArray())
        {
            var properties = feature.GetProperty("properties");
            string id = properties.GetProperty("pnu").GetString() ?? "";
            if (id.Length != 19 || !id.All(char.IsDigit)) throw new InvalidOperationException("유효한 필지 번호가 없습니다.");
            string label = properties.TryGetProperty("addr", out var addr) ? addr.GetString() ?? id : id;
            var geometry = feature.GetProperty("geometry");
            string? kind = geometry.GetProperty("type").GetString();
            var coordinates = geometry.GetProperty("coordinates");
            var polygonArrays = kind == "Polygon" ? new[] { coordinates } : kind == "MultiPolygon" ? coordinates.EnumerateArray().ToArray() : throw new InvalidOperationException("필지 면 도형이 아닙니다.");
            var polygons = polygonArrays.Select(poly => new ParcelPolygon(poly.EnumerateArray().Select(ring =>
            {
                var points = ring.EnumerateArray().Select(p => ReadPoint(p[1].GetDouble(), p[0].GetDouble(), center)).ToList();
                if (points.Count < 4 || points[0] != points[^1]) throw new InvalidOperationException("닫히지 않은 필지 경계입니다.");
                return points.ToArray();
            }).ToArray())).ToArray();
            if (polygons.Length == 0 || polygons.Any(p => p.Rings.Length == 0)) throw new InvalidOperationException("빈 필지 도형입니다.");
            var parcel = new ParcelFeature(id, label + " · " + id, polygons);
            var shape = MapGeometry.Parcel(parcel);
            if (!shape.IsValid || shape.IsEmpty) throw new InvalidOperationException("잘못된 필지 도형이 있어 중단했습니다. 범위를 줄여주세요.");
            features.Add(parcel);
        }
        return new(features.ToArray(), pages, current, total);
    }
}
