using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Windows.Interop;

namespace KoreaTerrain;

[Transaction(TransactionMode.Manual)]
public sealed class Command : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        if (uiDoc == null || uiDoc.Document.IsFamilyDocument)
        {
            TaskDialog.Show("한국 지형", "프로젝트 문서를 연 뒤 실행하세요.");
            return Result.Cancelled;
        }
        var doc = uiDoc.Document;
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToArray();
        var types = new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).Cast<ToposolidType>().ToArray();
        if (levels.Length == 0 || types.Length == 0)
        {
            TaskDialog.Show("한국 지형", "레벨과 지형 솔리드 유형이 있는 프로젝트 템플릿을 사용하세요.");
            return Result.Cancelled;
        }
        var window = new TerrainWindow(levels.Select(l => l.Name).ToArray(), types.Select(t => t.Name).ToArray());
        new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;
        if (window.ShowDialog() != true || window.Terrain == null) return Result.Cancelled;
        var data = window.Terrain;
        var level = levels[window.LevelIndex];
        try
        {
            XYZ anchor;
            try { anchor = uiDoc.Selection.PickPoint("지형 중심을 배치할 점을 선택하세요. ESC: 취소"); }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            double datum = window.Datum;
            double Metres(double value) => UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Meters);
            // API XYZ heights are absolute internal Z; datum maps to the selected level.
            var points = data.Points.Select(p => new XYZ(anchor.X + Metres(p.East), anchor.Y + Metres(p.North),
                level.Elevation + Metres(p.Elevation - datum))).ToList();
            using var transaction = new Transaction(doc, "지형·도로·대지 경계 생성");
            transaction.Start();
            var topo = Toposolid.Create(doc, points, types[window.TypeIndex].Id, level.Id);
            var comments = topo.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (comments != null && !comments.IsReadOnly)
                comments.Set(FormattableString.Invariant($"KoreaTerrain prototype | {window.LocationLabel} | {data.Center.Latitude:F8},{data.Center.Longitude:F8} | {data.Source} | {data.RetrievedAt:O} | datum={datum:F3}m -> level {level.Name}; +Y=geographic north assumed; shared coordinates not assigned"));
            var siteResult = SiteBuilder.Create(doc, topo, window, anchor, level.Elevation);
            if (transaction.Commit() != TransactionStatus.Committed)
                throw new InvalidOperationException("레빗에서 지형 생성을 완료하지 못했습니다.");
            uiDoc.Selection.SetElementIds(new[] { topo.Id });
            TaskDialog.Show("한국 지형", $"지형 {data.Points.Length:N0}점 / 도로 {siteResult.Roads}개 / 경계선 {siteResult.BoundarySegments}개를 생성했습니다.\n짧아서 제외한 경계 구간: {siteResult.SkippedShortSegments}개\n\n자료 표고 {datum:F2}m를 레벨 '{level.Name}'에 맞췄습니다.\n모델 +Y 방향을 북쪽으로 가정합니다. 공유좌표는 설정하지 않습니다.\n도로·경계는 초기 검토용이며 측량 경계가 아닙니다.");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}
