using Autodesk.Revit.DB;

namespace KoreaTerrain;

public record SiteBuildResult(int Roads, int BoundarySegments, int SkippedShortSegments);

public static class SiteBuilder
{
    public static SiteBuildResult Create(Document doc, Toposolid topo, TerrainWindow input, XYZ anchor, double levelHeight)
    {
        var data = input.Terrain!;
        double Feet(double m) => UnitUtils.ConvertToInternalUnits(m, UnitTypeId.Meters);
        XYZ Location(MapPoint p) => new(anchor.X + Feet(p.X), anchor.Y + Feet(p.Y),
            levelHeight + Feet(MapGeometry.Height(data, p) - input.Datum + .15));
        int roadsCreated = 0, linesCreated = 0, skipped = 0, faceCount = 0;
        if (input.Roads is { Features.Length: > 0 } roads)
        {
            var existing = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(m => m.Name == "KoreaTerrain · 개략 도로");
            ElementId materialId = existing?.Id ?? Material.Create(doc, "KoreaTerrain · 개략 도로");
            if (existing == null) ((Material)doc.GetElement(materialId)).Color = new Color(85, 85, 85);
            foreach (var road in roads.Features)
            {
                var area = MapGeometry.RoadArea(road, input.FallbackRoadWidth, data.Width);
                if (area.IsEmpty || area.Area < .01) continue;
                using var builder = new TessellatedShapeBuilder();
                builder.OpenConnectedFaceSet(false);
                int localFaces = 0;
                foreach (var triangle in MapGeometry.RoadTriangles(area))
                {
                    if (++faceCount > 100000) throw new InvalidOperationException("도로 면이 너무 복잡합니다. 범위를 줄여주세요.");
                    var vertices = triangle.Select(Location).ToArray();
                    if ((vertices[1] - vertices[0]).CrossProduct(vertices[2] - vertices[0]).Z < 0) Array.Reverse(vertices);
                    builder.AddFace(new TessellatedFace(vertices, materialId));
                    localFaces++;
                }
                builder.CloseConnectedFaceSet();
                if (localFaces == 0) continue;
                builder.Target = TessellatedShapeBuilderTarget.Mesh;
                builder.Fallback = TessellatedShapeBuilderFallback.Salvage;
                builder.Build();
                var objects = builder.GetBuildResult().GetGeometricalObjects();
                if (objects.Count == 0) throw new InvalidOperationException("도로 면을 생성하지 못했습니다.");
                var shape = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
                shape.Name = "주변 도로 · " + road.Name + (road.Width == null ? " (임시 폭)" : " (지도 폭)");
                shape.ApplicationId = "KoreaTerrain"; shape.ApplicationDataId = "OSM-way/" + road.Id;
                shape.SetShape(objects);
                shape.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(FormattableString.Invariant(
                    $"© OpenStreetMap contributors; ODbL https://www.openstreetmap.org/copyright | way {road.Id} | width={road.Width ?? input.FallbackRoadWidth}m ({(road.Width == null ? "user fallback, NOT measured" : "OSM tag, NOT verified")}) | retrieved {roads.RetrievedAt:O} | approximate surface over sampled elevation; not actual road boundary"));
                roadsCreated++;
            }
        }
        var ids = input.SelectedParcelIds;
        if (ids.Count > 0)
        {
            var parent = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            var category = parent.SubCategories.Cast<Category>().FirstOrDefault(c => c.Name == "KoreaTerrain · 대지 경계(참고)");
            if (category == null)
            {
                category = doc.Settings.Categories.NewSubcategory(parent, "KoreaTerrain · 대지 경계(참고)");
                category.LineColor = new Color(230, 110, 0);
                category.SetLineWeight(5, GraphicsStyleType.Projection);
            }
            var style = category.GetGraphicsStyle(GraphicsStyleType.Projection);
            foreach (var path in MapGeometry.Boundary(input.Parcels, ids, data.Width))
            for (int i = 1; i < path.Length; i++)
            {
                var a = path[i - 1]; var b = path[i];
                int parts = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2)) / 10));
                for (int j = 0; j < parts; j++)
                {
                    MapPoint Lerp(double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
                    var start = Location(Lerp((double)j / parts)); var end = Location(Lerp((double)(j + 1) / parts));
                    if (start.DistanceTo(end) <= doc.Application.ShortCurveTolerance * 1.01) { skipped++; continue; }
                    if (++linesCreated > 10000) throw new InvalidOperationException("대지 경계선이 너무 많습니다. 범위를 줄여주세요.");
                    var vector = end - start;
                    var normal = vector.CrossProduct(XYZ.BasisZ).Normalize();
                    var plane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(normal, start));
                    var curve = doc.Create.NewModelCurve(Line.CreateBound(start, end), plane);
                    curve.LineStyle = style;
                }
            }
            var comments = topo.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (comments is { IsReadOnly: false }) comments.Set(comments.AsString() + " | VWorld reference parcels: " + string.Join(",", ids));
        }
        return new(roadsCreated, linesCreated, skipped);
    }
}
