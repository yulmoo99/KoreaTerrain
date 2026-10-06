using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KoreaTerrain;

public sealed class TerrainWindow : Window
{
    private readonly TextBox address = Box("서울 중구 을지로 245"), width = Box("500"), spacing = Box("30"), datum = Box("0");
    private readonly PasswordBox key = new() { Padding = new Thickness(6) };
    private readonly PasswordBox parcelKey = new() { Padding = new Thickness(6) };
    private readonly TextBox parcelDomain = Box(""), roadWidth = Box("6");
    private readonly CheckBox includeRoads = new() { Content = "주변 도로 만들기 (개략 면)", IsChecked = true, Margin = new Thickness(0, 10, 0, 4) };
    private readonly CheckBox includeParcels = new() { Content = "대지 경계 가져오기 (브이월드 키 필요)", Margin = new Thickness(0, 8, 0, 4) };
    private readonly ListBox parcelList = new() { SelectionMode = SelectionMode.Multiple, MaxHeight = 140 };
    private readonly TextBlock selectionInfo = new() { Text = "필지를 불러오면 여기서 추가 선택할 수 있습니다.", TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox matches = new() { MinHeight = 32 };
    private readonly ComboBox levels = new() { MinHeight = 30 }, types = new() { MinHeight = 30 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8) };
    private readonly Canvas preview = new() { Width = 340, Height = 340, Background = Brushes.WhiteSmoke };
    private readonly Button search = Button("주소 검색"), fetch = Button("지형·도로·필지 불러오기"), create = Button("레빗에 배치"), map = Button("지도에서 위치 확인"), example = Button("국립중앙의료원 예시 위치");
    private readonly StackPanel input = new();
    private readonly Button retryRoads = Button("도로만 다시 불러오기");
    private bool busy;
    private string? roadError, parcelError;
    private readonly CancellationTokenSource lifetime = new();
    private readonly HttpClient http;
    public TerrainData? Terrain { get; private set; }
    public RoadData? Roads { get; private set; }
    public ParcelFeature[] Parcels { get; private set; } = Array.Empty<ParcelFeature>();
    public HashSet<string> SelectedParcelIds => parcelList.SelectedItems.Cast<ParcelFeature>().Select(p => p.Id).ToHashSet();
    public double FallbackRoadWidth => Number(roadWidth.Text);
    public string LocationLabel { get; private set; } = "";
    public int LevelIndex => levels.SelectedIndex;
    public int TypeIndex => types.SelectedIndex;
    public double Datum { get; private set; }

    private static TextBox Box(string text) => new() { Text = text, Padding = new Thickness(6), MinHeight = 30 };
    private static Button Button(string text) => new() { Content = text, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 6, 6, 0) };
    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 12, 0, 5), TextWrapping = TextWrapping.Wrap };

    public TerrainWindow(string[] levelNames, string[] typeNames, HttpClient? client = null)
    {
        http = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        Title = "한국 지형 · 도로 · 대지 경계 — 0.3";
        Width = 900; Height = 830; MinWidth = 800; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Malgun Gothic"); FontSize = 13;
        var root = new Grid { Margin = new Thickness(24), Background = Brushes.White };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        var scroll = new ScrollViewer { Content = input, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 24, 0) };
        root.Children.Add(scroll);
        input.Children.Add(new TextBlock { Text = "주소로 주변 지형 만들기", FontSize = 23, FontWeight = FontWeights.SemiBold });
        input.Children.Add(Label("1. 위치 선택")); input.Children.Add(address);
        input.Children.Add(Label("카카오 REST API 키 · 이번 실행에서만 사용")); input.Children.Add(key);
        input.Children.Add(search); input.Children.Add(Label("검색 결과를 선택하세요")); input.Children.Add(matches);
        input.Children.Add(example); input.Children.Add(map);
        input.Children.Add(Label("2. 정사각형 범위 (m, 120~2,000)")); input.Children.Add(width);
        input.Children.Add(Label("조회 점 간격 (m, 30~120)")); input.Children.Add(spacing);
        input.Children.Add(includeRoads);
        input.Children.Add(Label("도로 폭 정보가 없을 때 적용할 임시 폭 (m)")); input.Children.Add(roadWidth);
        input.Children.Add(includeParcels);
        var parcelSettings = new StackPanel();
        parcelSettings.Children.Add(Label("브이월드 인증키 · 이번 실행에서만 사용")); parcelSettings.Children.Add(parcelKey);
        parcelSettings.Children.Add(Label("인증키 발급 시 등록한 URL")); parcelSettings.Children.Add(parcelDomain);
        var parcelExpander = new Expander { Header = "필지 조회 설정", Content = parcelSettings, Margin = new Thickness(0, 4, 0, 4) };
        input.Children.Add(parcelExpander);
        input.Children.Add(fetch);
        input.Children.Add(Label("3. 배치 기준 레벨")); input.Children.Add(levels);
        input.Children.Add(Label("지형 솔리드 유형")); input.Children.Add(types);
        input.Children.Add(Label("선택한 레벨에 맞출 자료 표고 (m)")); input.Children.Add(datum);
        input.Children.Add(Label("배치 버튼을 누른 뒤 평면 뷰에서 중심점을 선택합니다. 모델 +Y를 북쪽으로 가정하며 공유좌표는 설정하지 않습니다."));
        input.Children.Add(create);
        var right = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        var rightScroll = new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(rightScroll, 1); root.Children.Add(rightScroll);
        right.Children.Add(new TextBlock { Text = "지형 · 도로 · 필지", FontSize = 20, FontWeight = FontWeights.SemiBold });
        right.Children.Add(Label("북 ↑  동 →  ·  도로: 회색 / 선택 필지: 주황\n필지 면이나 아래 목록을 클릭하면 선택·해제됩니다."));
        right.Children.Add(preview); right.Children.Add(status);
        right.Children.Add(retryRoads); retryRoads.IsEnabled = false;
        right.Children.Add(selectionInfo); right.Children.Add(parcelList);
        right.Children.Add(Label("표고: SRTM 약 30m급 / Open Topo Data\n도로: © OpenStreetMap contributors · ODbL\n필지: 브이월드 연속지적도 (참고용)\n\n도로 폭 누락 구간에는 임시 폭을 사용합니다. 교량·터널은 제외합니다. 실제 도로 경계나 측량 대지 경계와 다를 수 있습니다."));
        var close = Button("닫기 / 작업 취소"); close.Click += (_, _) => Close(); right.Children.Add(close);
        Content = root;
        levels.ItemsSource = levelNames; levels.SelectedIndex = 0;
        types.ItemsSource = typeNames; types.SelectedIndex = 0;
        fetch.IsEnabled = false; map.IsEnabled = false; create.IsEnabled = false;
        status.Text = "주소를 검색하거나 예시 위치를 선택하세요.";
        address.TextChanged += (_, _) => { matches.ItemsSource = null; Invalidate(); };
        width.TextChanged += (_, _) => Invalidate(); spacing.TextChanged += (_, _) => Invalidate();
        roadWidth.TextChanged += (_, _) => { if (Terrain != null) { try { Draw(Terrain); } catch (ArgumentException) { status.Text = "임시 도로 폭에 1~40m의 숫자를 입력하세요."; } } };
        includeRoads.Checked += (_, _) => RefreshStatus();
        includeRoads.Unchecked += (_, _) => { Roads = null; roadError = null; if (Terrain != null) Draw(Terrain); RefreshStatus(); };
        includeParcels.Checked += (_, _) => { parcelExpander.IsExpanded = true; RefreshStatus(); };
        includeParcels.Unchecked += (_, _) => { Parcels = Array.Empty<ParcelFeature>(); parcelList.ItemsSource = null; parcelError = null; if (Terrain != null) Draw(Terrain); RefreshStatus(); };
        parcelList.SelectionChanged += (_, _) =>
        {
            if (Terrain == null) return;
            selectionInfo.Text = $"선택 {SelectedParcelIds.Count}필지 · 합친 바깥 경계를 생성합니다.";
            Draw(Terrain);
        };
        matches.SelectionChanged += (_, _) => { Invalidate(); fetch.IsEnabled = map.IsEnabled = matches.SelectedItem is AddressMatch; };
        search.Click += async (_, _) => await Run(async service =>
        {
            var results = await service.SearchAsync(address.Text, key.Password, lifetime.Token);
            matches.ItemsSource = results;
            status.Text = results.Length == 0 ? "검색 결과가 없습니다. 도로명·지번 주소를 입력하세요." : "검색 결과에서 위치를 선택하세요.";
        });
        example.Click += (_, _) =>
        {
            address.Text = "서울 중구 을지로 245";
            matches.ItemsSource = new[] { new AddressMatch("국립중앙의료원 · 공식 홈페이지 지도 좌표", ExampleLocation.Center) };
            matches.SelectedIndex = 0;
            status.Text = "예시 좌표를 선택했습니다. 지도에서 위치를 확인하세요.";
        };
        map.Click += (_, _) =>
        {
            if (matches.SelectedItem is not AddressMatch selected) return;
            string url = FormattableString.Invariant($"https://www.openstreetmap.org/?mlat={selected.Location.Latitude:F7}&mlon={selected.Location.Longitude:F7}#map=17/{selected.Location.Latitude:F7}/{selected.Location.Longitude:F7}");
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { status.Text = "지도를 열 수 없습니다: " + ex.Message; }
        };
        fetch.Click += async (_, _) => await Run(async service =>
        {
            Invalidate();
            var selected = matches.SelectedItem as AddressMatch ?? throw new ArgumentException("위치를 먼저 선택하세요.");
            double extent = Number(width.Text);
            if (includeRoads.IsChecked == true && (FallbackRoadWidth < 1 || FallbackRoadWidth > 40)) throw new ArgumentException("임시 도로 폭은 1~40m로 입력하세요.");
            var data = await service.FetchAsync(selected.Location, extent, Number(spacing.Text),
                new Progress<string>(text => status.Text = text), lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            Terrain = data; LocationLabel = selected.Address;
            datum.Text = data.Minimum.ToString("F2", CultureInfo.InvariantCulture);
            Draw(data);
            var layers = new MapLayerService(http);
            ParcelFeature[] parcels = Array.Empty<ParcelFeature>();
            if (includeRoads.IsChecked == true) await LoadRoads();
            if (includeParcels.IsChecked == true)
            {
                try { status.Text = "주변 필지를 불러오는 중입니다. 지형은 유지됩니다."; parcels = await layers.ParcelsAsync(selected.Location, extent, parcelKey.Password, parcelDomain.Text, lifetime.Token); }
                catch (Exception ex) when (!lifetime.IsCancellationRequested) { parcelError = ex is OperationCanceledException ? "필지 조회 시간 초과" : ex.Message; }
            }
            lifetime.Token.ThrowIfCancellationRequested();
            Parcels = parcels;
            parcelList.ItemsSource = parcels;
            var auto = MapGeometry.AutoSelect(parcels).ToHashSet();
            foreach (var parcel in parcels.Where(p => auto.Contains(p.Id))) parcelList.SelectedItems.Add(parcel);
            selectionInfo.Text = parcels.Length == 0 ? "필지 미조회 또는 조회 결과 없음" : auto.Count == 0 ? "주소 위치의 필지를 확정하지 못했습니다. 필지를 직접 선택하세요." : "주소 위치의 필지 1개를 선택했습니다. 주변 필지를 추가할 수 있습니다.";
            datum.Text = data.Minimum.ToString("F2", CultureInfo.InvariantCulture);
            Draw(data);
            RefreshStatus();
        });
        retryRoads.Click += async (_, _) => await Run(async _ => { await LoadRoads(); RefreshStatus(); });
        create.Click += (_, _) =>
        {
            try
            {
                Datum = Number(datum.Text);
                if (Roads != null && (FallbackRoadWidth < 1 || FallbackRoadWidth > 40)) throw new ArgumentException("임시 도로 폭은 1~40m로 입력하세요.");
                if (Terrain == null || Math.Max(Math.Abs(Terrain.Minimum - Datum), Math.Abs(Terrain.Maximum - Datum)) > 5000)
                    throw new ArgumentException("배치 기준 표고를 확인하세요. 지형과의 높이 차이는 5,000m 이내여야 합니다.");
                var missing = new List<string>();
                if (includeRoads.IsChecked == true && Roads == null) missing.Add("도로");
                if (includeParcels.IsChecked == true && SelectedParcelIds.Count == 0) missing.Add("대지 경계");
                if (missing.Count > 0 && MessageBox.Show(this, string.Join("·", missing) + "가 준비되지 않았습니다. 해당 항목을 제외하고 현재 표시된 자료만 배치할까요?", "일부 자료만 배치", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                DialogResult = true;
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        Closed += (_, _) => { lifetime.Cancel(); key.Clear(); parcelKey.Clear(); http.Dispose(); };
    }

    private async Task Run(Func<TerrainService, Task> action)
    {
        if (busy) return;
        busy = true; input.IsEnabled = false; retryRoads.IsEnabled = false; parcelList.IsEnabled = false;
        try { await action(new TerrainService(http)); }
        catch (OperationCanceledException) { status.Text = "작업이 취소되었거나 요청 시간이 초과되었습니다."; }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { busy = false; input.IsEnabled = true; parcelList.IsEnabled = true; create.IsEnabled = Terrain != null; retryRoads.IsEnabled = Terrain != null && includeRoads.IsChecked == true; }
    }
    private async Task LoadRoads()
    {
        if (Terrain == null) return;
        try
        {
            var result = await new MapLayerService(http).RoadsAsync(Terrain.Center, Terrain.Width, lifetime.Token,
                new Progress<string>(message => { if (!lifetime.IsCancellationRequested) status.Text = message; }));
            lifetime.Token.ThrowIfCancellationRequested();
            Roads = result; roadError = null;
        }
        catch (Exception ex) when (!lifetime.IsCancellationRequested) { roadError = ex is OperationCanceledException ? "도로 조회 시간 초과" : ex.Message; }
        if (!lifetime.IsCancellationRequested) Draw(Terrain);
    }
    private void RefreshStatus()
    {
        retryRoads.IsEnabled = !busy && Terrain != null && includeRoads.IsChecked == true;
        if (Terrain == null) return;
        string roadState = includeRoads.IsChecked != true ? "도로: 제외" : Roads == null ? "도로: 미조회·실패 — 도로만 다시 불러오기 가능" : $"도로: {Roads.Features.Length}개 (폭 추정 {Roads.Features.Count(r => r.Width == null)}개)";
        status.Text = $"지형 준비 완료: {Terrain.Points.Length}점\n{roadState}\n필지: {Parcels.Length}개\n" +
            (roadError == null ? "" : roadError + "\n") + (parcelError == null ? "" : parcelError + "\n") +
            "받아온 자료는 유지됩니다. 레빗에 배치할 수 있습니다.";
    }
    private void Invalidate()
    {
        Terrain = null; Roads = null; Parcels = Array.Empty<ParcelFeature>(); parcelList.ItemsSource = null;
        roadError = parcelError = null; retryRoads.IsEnabled = false;
        selectionInfo.Text = "필지를 불러오면 여기서 추가 선택할 수 있습니다.";
        create.IsEnabled = false; preview.Children.Clear();
        fetch.IsEnabled = map.IsEnabled = matches.SelectedItem is AddressMatch;
        status.Text = "위치와 범위를 확인하고 표고를 불러오세요.";
    }
    private static double Number(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
            throw new ArgumentException("유효한 숫자를 입력하세요. 소수점은 마침표를 사용하세요.");
        return value;
    }
    private void Draw(TerrainData data)
    {
        preview.Children.Clear(); preview.ClipToBounds = true; double cell = 340.0 / data.Side;
        for (int y = 0; y < data.Side; y++)
        for (int x = 0; x < data.Side; x++)
        {
            var point = data.Points[y * data.Side + x];
            double t = (point.Elevation - data.Minimum) / Math.Max(1, data.Maximum - data.Minimum);
            var tile = new Rectangle { Width = cell + .2, Height = cell + .2,
                Fill = new SolidColorBrush(Color.FromRgb((byte)(35 + t * 205), (byte)(85 + t * 105), (byte)(130 - t * 65))),
                ToolTip = $"표고 {point.Elevation:F1}m\n동 {point.East:F1}m / 북 {point.North:F1}m" };
            Canvas.SetLeft(tile, x * cell); Canvas.SetTop(tile, (data.Side - 1 - y) * cell); preview.Children.Add(tile);
        }
        if (Roads != null)
            foreach (var road in Roads.Features)
            {
                var line = new Polyline { Stroke = Brushes.DimGray, StrokeThickness = Math.Max(1, (road.Width ?? FallbackRoadWidth) / data.Width * 340),
                    ToolTip = road.Name + (road.Width == null ? " · 임시 폭" : " · 지도 폭"), IsHitTestVisible = false };
                foreach (var p in road.Points) line.Points.Add(Screen(p, data));
                preview.Children.Add(line);
            }
        foreach (var parcel in Parcels)
        {
            bool selected = SelectedParcelIds.Contains(parcel.Id);
            var geometry = new PathGeometry { FillRule = FillRule.EvenOdd };
            foreach (var polygon in parcel.Polygons)
            foreach (var ring in polygon.Rings)
            {
                var figure = new PathFigure { StartPoint = Screen(ring[0], data), IsClosed = true };
                figure.Segments.Add(new PolyLineSegment(ring.Skip(1).Select(p => Screen(p, data)), true));
                geometry.Figures.Add(figure);
            }
            var path = new System.Windows.Shapes.Path { Data = geometry, Stroke = selected ? Brushes.DarkOrange : Brushes.White,
                StrokeThickness = selected ? 2 : .7, Fill = selected ? new SolidColorBrush(Color.FromArgb(65, 255, 160, 0)) : Brushes.Transparent, ToolTip = parcel.Label };
            path.MouseLeftButtonDown += (_, e) => { if (parcelList.SelectedItems.Contains(parcel)) parcelList.SelectedItems.Remove(parcel); else parcelList.SelectedItems.Add(parcel); e.Handled = true; };
            preview.Children.Add(path);
        }
        var marker = new Ellipse { Width = 7, Height = 7, Fill = Brushes.Crimson, Stroke = Brushes.White, StrokeThickness = 1, IsHitTestVisible = false };
        Canvas.SetLeft(marker, 166.5); Canvas.SetTop(marker, 166.5); preview.Children.Add(marker);
    }
    private static System.Windows.Point Screen(MapPoint p, TerrainData data) => new((p.X / data.Width + .5) * 340, (.5 - p.Y / data.Width) * 340);
}
