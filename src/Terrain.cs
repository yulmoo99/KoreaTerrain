using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace KoreaTerrain;

public record GeoPoint(double Latitude, double Longitude);
public record AddressMatch(string Address, GeoPoint Location)
{
    public override string ToString() => Address;
}
public record Sample(double East, double North, double Elevation, GeoPoint Location);
public record TerrainData(GeoPoint Center, double Width, double Spacing, int Side, Sample[] Points,
    string Source, DateTimeOffset RetrievedAt)
{
    public double Minimum => Points.Min(p => p.Elevation);
    public double Maximum => Points.Max(p => p.Elevation);
}

public static class TerrainGrid
{
    // First-order local WGS84 mapping, suitable for this prototype's <= 2 km extent.
    // +X east, +Y north. This is not a Korean cadastral/shared coordinate transform.
    public static GeoPoint Offset(GeoPoint center, double east, double north)
    {
        const double a = 6378137, e2 = 0.0066943799901413165;
        double phi = center.Latitude * Math.PI / 180;
        double w = Math.Sqrt(1 - e2 * Math.Pow(Math.Sin(phi), 2));
        double meridian = a * (1 - e2) / (w * w * w);
        double prime = a / w;
        return new(center.Latitude + north / meridian * 180 / Math.PI,
            center.Longitude + east / (prime * Math.Cos(phi)) * 180 / Math.PI);
    }

    public static (int Side, double Spacing, Sample[] Points) Create(GeoPoint center, double width, double requestedSpacing)
    {
        if (!double.IsFinite(center.Latitude) || !double.IsFinite(center.Longitude) ||
            center.Latitude < 33 || center.Latitude > 38.7 || center.Longitude < 124 || center.Longitude > 132)
            throw new ArgumentException("한국 주변 좌표 범위만 지원합니다. 위도·경도를 확인하세요.");
        if (!double.IsFinite(width) || width < 120 || width > 2000)
            throw new ArgumentException("생성 범위는 120~2,000m 사이로 입력하세요.");
        if (!double.IsFinite(requestedSpacing) || requestedSpacing < 30 || requestedSpacing > 120)
            throw new ArgumentException("점 간격은 30~120m 사이로 입력하세요.");
        // Round down to avoid promising a finer grid than requested; include both boundaries.
        int segments = Math.Max(2, (int)Math.Floor(width / requestedSpacing));
        int side = segments + 1;
        if (side * side > 5000) throw new ArgumentException("점 개수가 너무 많습니다.");
        double spacing = width / segments;
        var points = new List<Sample>();
        for (int y = 0; y < side; y++)
        for (int x = 0; x < side; x++)
        {
            double east = -width / 2 + x * spacing, north = -width / 2 + y * spacing;
            points.Add(new(east, north, double.NaN, Offset(center, east, north)));
        }
        return (side, spacing, points.ToArray());
    }
}

public sealed class TerrainService
{
    private readonly HttpClient http;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTimeOffset lastRequest = DateTimeOffset.MinValue;
    public TerrainService(HttpClient http) => this.http = http;

    public async Task<AddressMatch[]> SearchAsync(string address, string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("주소를 입력하세요.");
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("카카오 REST API 키를 입력하세요. 키는 파일에 저장하지 않습니다.");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://dapi.kakao.com/v2/local/search/address.json?size=30&query=" + Uri.EscapeDataString(address.Trim()));
        request.Headers.Authorization = new AuthenticationHeaderValue("KakaoAK", apiKey.Trim());
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"주소 검색 실패 ({(int)response.StatusCode}). 키와 카카오 API 사용 권한을 확인하세요.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("documents").EnumerateArray()
            .Where(d => d.GetProperty("address_type").GetString() is "ROAD_ADDR" or "REGION_ADDR")
            .Select(d => new AddressMatch(d.GetProperty("address_name").GetString()!,
                new(double.Parse(d.GetProperty("y").GetString()!, CultureInfo.InvariantCulture),
                    double.Parse(d.GetProperty("x").GetString()!, CultureInfo.InvariantCulture))))
            .ToArray();
    }

    public async Task<TerrainData> FetchAsync(GeoPoint center, double width, double spacing,
        IProgress<string>? progress, CancellationToken ct)
    {
        var grid = TerrainGrid.Create(center, width, spacing);
        var points = grid.Points;
        for (int start = 0; start < points.Length; start += 100)
        {
            ct.ThrowIfCancellationRequested();
            var batch = points.Skip(start).Take(100).ToArray();
            string locations = string.Join("|", batch.Select(p => FormattableString.Invariant($"{p.Location.Latitude:F8},{p.Location.Longitude:F8}")));
            string body;
            await Gate.WaitAsync(ct);
            try
            {
                var delay = TimeSpan.FromMilliseconds(1100) - (DateTimeOffset.UtcNow - lastRequest);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
                lastRequest = DateTimeOffset.UtcNow;
                using var response = await http.GetAsync("https://api.opentopodata.org/v1/srtm30m?interpolation=bilinear&locations=" + Uri.EscapeDataString(locations), ct);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"표고 조회 실패 ({(int)response.StatusCode}). 요청 제한 또는 네트워크 상태를 확인하세요.");
                body = await response.Content.ReadAsStringAsync(ct);
            }
            finally { Gate.Release(); }
            var elevations = ParseElevations(body, batch.Select(p => p.Location).ToArray());
            for (int i = 0; i < batch.Length; i++) points[start + i] = batch[i] with { Elevation = elevations[i] };
            progress?.Report($"표고 조회 {Math.Min(start + 100, points.Length):N0} / {points.Length:N0}점");
        }
        return new(center, width, grid.Spacing, grid.Side, points,
            "SRTM GL1 v3 (~30m), Open Topo Data; bilinear; elevation: EGM96 metres", DateTimeOffset.UtcNow);
    }

    public static double[] ParseElevations(string body, GeoPoint[] requested)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        if (root.GetProperty("status").GetString() != "OK") throw new InvalidOperationException("표고 서비스가 오류를 반환했습니다.");
        var results = root.GetProperty("results").EnumerateArray().ToArray();
        if (results.Length != requested.Length) throw new InvalidOperationException("요청한 점과 응답 개수가 다릅니다.");
        var values = new double[results.Length];
        for (int i = 0; i < results.Length; i++)
        {
            var row = results[i];
            var loc = row.GetProperty("location");
            if (Math.Abs(loc.GetProperty("lat").GetDouble() - requested[i].Latitude) > 0.000001 ||
                Math.Abs(loc.GetProperty("lng").GetDouble() - requested[i].Longitude) > 0.000001)
                throw new InvalidOperationException("표고 응답의 위치가 요청한 위치와 다릅니다.");
            if (row.GetProperty("dataset").GetString() != "srtm30m") throw new InvalidOperationException("예상하지 않은 표고 자료입니다.");
            var z = row.GetProperty("elevation");
            if (z.ValueKind != JsonValueKind.Number || !z.TryGetDouble(out double height) ||
                !double.IsFinite(height) || height < -500 || height > 9000)
                throw new InvalidOperationException("높이가 없는 점 또는 잘못된 높이가 포함되어 생성을 중단했습니다. 범위를 조절하세요.");
            values[i] = height;
        }
        return values;
    }
}
