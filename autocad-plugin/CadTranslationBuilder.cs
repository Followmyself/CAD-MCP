using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;

namespace CadMcp.AutoCAD
{
    internal sealed class TranslationRequest
    {
        public string request_id { get; set; }
        public string source_dwg { get; set; }
        public string output_dwg { get; set; }
        public Dictionary<string, string> translations { get; set; }
        public List<string> style_names { get; set; }
        public string label_template { get; set; }
        public int length_decimals { get; set; }
        public int radius_decimals { get; set; }
        public double text_height { get; set; }
        public bool leader { get; set; }
    }

    internal sealed class TranslationResponse
    {
        public bool ok { get; set; }
        public string request_id { get; set; }
        public bool duplicate { get; set; }
        public string operation { get; set; }
        public string message { get; set; }
        public string error { get; set; }
        public Dictionary<string, object> data { get; set; }

        public static TranslationResponse Failure(string requestId, string operation, string error)
        {
            return new TranslationResponse
            {
                ok = false,
                request_id = requestId,
                duplicate = false,
                operation = operation,
                error = error,
                data = new Dictionary<string, object>()
            };
        }
    }

    internal static class CadTranslationBuilder
    {
        private sealed class GeometryCandidate
        {
            internal string Handle;
            internal string Type;
            internal string Layer;
            internal string PointKind;
            internal Autodesk.AutoCAD.Geometry.Point3d Position;
        }

        private sealed class CoordinatePolyline
        {
            internal string Handle;
            internal bool Closed;
            internal List<Autodesk.AutoCAD.Geometry.Point3d> Vertices;
        }

        internal static string Validate(TranslationRequest request, string operation)
        {
            if (request == null)
            {
                return "Request body is required.";
            }
            if (string.IsNullOrWhiteSpace(request.request_id) || request.request_id.Length > 128 ||
                request.request_id.Any(character =>
                    !((character >= 'A' && character <= 'Z') ||
                      (character >= 'a' && character <= 'z') ||
                      (character >= '0' && character <= '9') ||
                      "._:-".IndexOf(character) >= 0)))
            {
                return "request_id must contain 1 to 128 letters, digits, dots, underscores, colons, or hyphens.";
            }
            if (string.IsNullOrWhiteSpace(request.source_dwg) ||
                !string.Equals(Path.GetExtension(request.source_dwg), ".dwg", StringComparison.OrdinalIgnoreCase))
            {
                return "source_dwg must be an existing DWG file.";
            }
            if (!File.Exists(request.source_dwg))
            {
                return "source_dwg does not exist.";
            }
            if (operation == "translate" || operation == "repair_fonts")
            {
                if (string.IsNullOrWhiteSpace(request.output_dwg) ||
                    !string.Equals(Path.GetExtension(request.output_dwg), ".dwg", StringComparison.OrdinalIgnoreCase))
                {
                    return "output_dwg must be a DWG path.";
                }
                if (string.Equals(Path.GetFullPath(request.source_dwg), Path.GetFullPath(request.output_dwg), StringComparison.OrdinalIgnoreCase))
                {
                    return "output_dwg must differ from source_dwg.";
                }
                if (File.Exists(request.output_dwg))
                {
                    return "output_dwg already exists; refusing to overwrite an existing file.";
                }
                if (operation == "translate" && request.translations == null)
                {
                    return "translations is required for translate.";
                }
            }
            return null;
        }

        internal static TranslationResponse Execute(string operation, TranslationRequest request)
        {
            return operation == "inspect"
                ? Inspect(request)
                : operation == "repair_fonts"
                    ? RepairFonts(request)
                    : Translate(request);
        }

        private static TranslationResponse Inspect(TranslationRequest request)
        {
            using (Database database = OpenDatabase(request.source_dwg))
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                List<Dictionary<string, object>> texts = ReadTexts(database, transaction);
                List<Dictionary<string, object>> styles = ReadTextStyles(database, transaction);
                List<CoordinatePolyline> coordinatePolylines =
                    ReadCoordinatePolylines(database, transaction);
                List<Dictionary<string, object>> coordinateCandidates =
                    ReadCoordinateLabelCandidates(database, transaction, coordinatePolylines);
                transaction.Commit();
                return new TranslationResponse
                {
                    ok = true,
                    request_id = request.request_id,
                    duplicate = false,
                    operation = "inspect",
                    message = "DWG text entities inspected through AutoCAD Managed API.",
                    data = new Dictionary<string, object>
                    {
                        { "source_dwg", request.source_dwg },
                        { "text_count", texts.Count },
                        { "texts", texts },
                        { "text_styles", styles },
                        { "coordinate_label_candidates", coordinateCandidates },
                        { "coordinate_polyline_count", coordinatePolylines.Count },
                        { "coordinate_polylines", CoordinatePolylineDictionaries(coordinatePolylines) }
                    }
                };
            }
        }

        private static List<Dictionary<string, object>> ReadCoordinateLabelCandidates(
            Database database,
            Transaction transaction,
            List<CoordinatePolyline> coordinatePolylines)
        {
            var labels = new List<DBText>();
            var candidates = new List<GeometryCandidate>();
            BlockTable table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            BlockTableRecord modelSpace = (BlockTableRecord)transaction.GetObject(
                table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId entityId in modelSpace)
            {
                Entity entity = transaction.GetObject(entityId, OpenMode.ForRead, false) as Entity;
                if (entity == null || entity.IsErased)
                {
                    continue;
                }
                DBText label = entity as DBText;
                if (label != null && string.Equals(label.Layer, "坐标", StringComparison.OrdinalIgnoreCase))
                {
                    labels.Add(label);
                    continue;
                }
                if (string.Equals(entity.Layer, "表格", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                AddGeometryCandidates(candidates, entity, transaction);
            }

            var result = new List<Dictionary<string, object>>();
            foreach (DBText label in labels)
            {
                Autodesk.AutoCAD.Geometry.Point3d anchor =
                    label.HorizontalMode == TextHorizontalMode.TextLeft &&
                    label.VerticalMode == TextVerticalMode.TextBase
                        ? label.Position
                        : label.AlignmentPoint;
                var nearest = candidates
                    .Select(candidate => new
                    {
                        Candidate = candidate,
                        Distance = Distance2d(anchor, candidate.Position)
                    })
                    .Where(item => item.Distance <= 50.0)
                    .OrderBy(item => item.Distance)
                    .Take(8)
                    .Select(item => new Dictionary<string, object>
                    {
                        { "handle", item.Candidate.Handle },
                        { "type", item.Candidate.Type },
                        { "layer", item.Candidate.Layer },
                        { "point_kind", item.Candidate.PointKind },
                        { "position", PointDictionary(item.Candidate.Position) },
                        { "distance", item.Distance }
                    })
                    .ToList();
                result.Add(new Dictionary<string, object>
                {
                    { "label", label.TextString },
                    { "label_handle", label.Handle.ToString() },
                    { "label_position", PointDictionary(anchor) },
                    { "coordinate_binding", BindCoordinateLeader(anchor, coordinatePolylines) },
                    { "nearest_geometry", nearest }
                });
            }
            return result;
        }

        private static List<CoordinatePolyline> ReadCoordinatePolylines(
            Database database,
            Transaction transaction)
        {
            var result = new List<CoordinatePolyline>();
            BlockTable table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            BlockTableRecord modelSpace = (BlockTableRecord)transaction.GetObject(
                table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId entityId in modelSpace)
            {
                Polyline polyline = transaction.GetObject(entityId, OpenMode.ForRead, false) as Polyline;
                if (polyline == null || polyline.IsErased ||
                    !string.Equals(polyline.Layer, "坐标", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var vertices = new List<Autodesk.AutoCAD.Geometry.Point3d>();
                for (int index = 0; index < polyline.NumberOfVertices; index++)
                {
                    vertices.Add(polyline.GetPoint3dAt(index));
                }
                result.Add(new CoordinatePolyline
                {
                    Handle = polyline.Handle.ToString(),
                    Closed = polyline.Closed,
                    Vertices = vertices
                });
            }
            return result;
        }

        private static Dictionary<string, object> BindCoordinateLeader(
            Autodesk.AutoCAD.Geometry.Point3d labelAnchor,
            List<CoordinatePolyline> coordinatePolylines)
        {
            const double horizontalTolerance = 1e-7;
            const double onSegmentTolerance = 1e-6;
            var matches = new List<Dictionary<string, object>>();
            foreach (CoordinatePolyline polyline in coordinatePolylines)
            {
                if (polyline.Closed || polyline.Vertices == null || polyline.Vertices.Count < 3)
                {
                    continue;
                }
                for (int index = 1; index < polyline.Vertices.Count - 1; index++)
                {
                    Autodesk.AutoCAD.Geometry.Point3d start = polyline.Vertices[index];
                    Autodesk.AutoCAD.Geometry.Point3d end = polyline.Vertices[index + 1];
                    if (Math.Abs(start.Y - end.Y) > horizontalTolerance ||
                        DistancePointToSegment2d(
                            labelAnchor.X, labelAnchor.Y,
                            start.X, start.Y,
                            end.X, end.Y) > onSegmentTolerance)
                    {
                        continue;
                    }
                    matches.Add(new Dictionary<string, object>
                    {
                        { "leader_handle", polyline.Handle },
                        { "point", PointDictionary(polyline.Vertices[0]) },
                        { "baseline_segment", index + ":" + (index + 1) },
                        { "baseline_start", PointDictionary(start) },
                        { "baseline_end", PointDictionary(end) }
                    });
                }
            }
            return new Dictionary<string, object>
            {
                { "status", matches.Count == 1 ? "matched" : matches.Count == 0 ? "unmatched" : "ambiguous" },
                { "match_count", matches.Count },
                { "matches", matches }
            };
        }

        private static List<Dictionary<string, object>> CoordinatePolylineDictionaries(
            List<CoordinatePolyline> coordinatePolylines)
        {
            return coordinatePolylines.Select(polyline => new Dictionary<string, object>
            {
                { "handle", polyline.Handle },
                { "closed", polyline.Closed },
                { "vertex_count", polyline.Vertices.Count },
                { "vertices", polyline.Vertices.Select(PointDictionary).ToList() }
            }).ToList();
        }

        private static double DistancePointToSegment2d(
            double pointX,
            double pointY,
            double startX,
            double startY,
            double endX,
            double endY)
        {
            double dx = endX - startX;
            double dy = endY - startY;
            double lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= 0.0)
            {
                double zeroDx = pointX - startX;
                double zeroDy = pointY - startY;
                return Math.Sqrt(zeroDx * zeroDx + zeroDy * zeroDy);
            }
            double projection = ((pointX - startX) * dx + (pointY - startY) * dy) / lengthSquared;
            projection = Math.Max(0.0, Math.Min(1.0, projection));
            double nearestX = startX + projection * dx;
            double nearestY = startY + projection * dy;
            double pointDx = pointX - nearestX;
            double pointDy = pointY - nearestY;
            return Math.Sqrt(pointDx * pointDx + pointDy * pointDy);
        }

        private static void AddGeometryCandidates(
            List<GeometryCandidate> result,
            Entity entity,
            Transaction transaction)
        {
            DBPoint point = entity as DBPoint;
            if (point != null)
            {
                AddGeometryCandidate(result, entity, "position", point.Position);
                return;
            }
            BlockReference block = entity as BlockReference;
            if (block != null)
            {
                AddGeometryCandidate(result, entity, "insertion", block.Position);
                return;
            }
            Line line = entity as Line;
            if (line != null)
            {
                AddGeometryCandidate(result, entity, "start", line.StartPoint);
                AddGeometryCandidate(result, entity, "end", line.EndPoint);
                return;
            }
            Circle circle = entity as Circle;
            if (circle != null)
            {
                AddGeometryCandidate(result, entity, "center", circle.Center);
                return;
            }
            Arc arc = entity as Arc;
            if (arc != null)
            {
                AddGeometryCandidate(result, entity, "start", arc.StartPoint);
                AddGeometryCandidate(result, entity, "end", arc.EndPoint);
                AddGeometryCandidate(result, entity, "center", arc.Center);
                return;
            }
            Polyline polyline = entity as Polyline;
            if (polyline != null)
            {
                for (int index = 0; index < polyline.NumberOfVertices; index++)
                {
                    AddGeometryCandidate(result, entity, "vertex:" + index, polyline.GetPoint3dAt(index));
                }
                return;
            }
            Polyline2d polyline2d = entity as Polyline2d;
            if (polyline2d != null)
            {
                int index = 0;
                foreach (ObjectId vertexId in polyline2d)
                {
                    Vertex2d vertex = transaction.GetObject(vertexId, OpenMode.ForRead, false) as Vertex2d;
                    if (vertex != null)
                    {
                        AddGeometryCandidate(result, entity, "vertex:" + index, vertex.Position);
                        index++;
                    }
                }
                return;
            }
            Polyline3d polyline3d = entity as Polyline3d;
            if (polyline3d != null)
            {
                int index = 0;
                foreach (ObjectId vertexId in polyline3d)
                {
                    PolylineVertex3d vertex = transaction.GetObject(vertexId, OpenMode.ForRead, false) as PolylineVertex3d;
                    if (vertex != null)
                    {
                        AddGeometryCandidate(result, entity, "vertex:" + index, vertex.Position);
                        index++;
                    }
                }
            }
        }

        private static void AddGeometryCandidate(
            List<GeometryCandidate> result,
            Entity entity,
            string pointKind,
            Autodesk.AutoCAD.Geometry.Point3d position)
        {
            result.Add(new GeometryCandidate
            {
                Handle = entity.Handle.ToString(),
                Type = entity.GetType().Name,
                Layer = entity.Layer,
                PointKind = pointKind,
                Position = position
            });
        }

        private static double Distance2d(
            Autodesk.AutoCAD.Geometry.Point3d first,
            Autodesk.AutoCAD.Geometry.Point3d second)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static Dictionary<string, object> PointDictionary(
            Autodesk.AutoCAD.Geometry.Point3d point)
        {
            return new Dictionary<string, object>
            {
                { "x", point.X },
                { "y", point.Y },
                { "z", point.Z }
            };
        }

        private static TranslationResponse RepairFonts(TranslationRequest request)
        {
            const string primaryFont = "txt.shx";
            const string bigFont = "gbcbig.shx";
            string output = Path.GetFullPath(request.output_dwg);
            string stage = output + ".partial-" + Guid.NewGuid().ToString("N") + ".dwg";
            int styleCount = 0;
            int entityCount = 0;
            try
            {
                using (Database sourceDatabase = OpenDatabase(request.source_dwg))
                using (Database database = CreateEditableSnapshot(sourceDatabase))
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    HashSet<ObjectId> styleIds = CollectTextStyleIds(database, transaction, ref entityCount);
                    TextStyleTable textStyleTable =
                        (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
                    foreach (ObjectId styleId in textStyleTable)
                    {
                        TextStyleTableRecord style =
                            transaction.GetObject(styleId, OpenMode.ForRead, false) as TextStyleTableRecord;
                        if (style != null && !style.IsErased && !style.IsShapeFile && !style.IsDependent)
                        {
                            styleIds.Add(styleId);
                        }
                    }
                    styleCount = RepairTextStyles(database, transaction, styleIds, primaryFont, bigFont, request.style_names);
                    transaction.Commit();
                    Log.Info("Completed font edits for " + request.request_id + "; saving WBLOCK snapshot.");
                    SaveDatabaseAs(database, stage);
                }
                if (File.Exists(output))
                {
                    throw new InvalidOperationException("output_dwg appeared during font repair; refusing to overwrite it.");
                }
                File.Move(stage, output);
                return new TranslationResponse
                {
                    ok = true,
                    request_id = request.request_id,
                    duplicate = false,
                    operation = "repair_fonts",
                    message = "CAD text styles repaired with AutoCAD built-in txt.shx and gbcbig.shx.",
                    data = new Dictionary<string, object>
                    {
                        { "source_dwg", request.source_dwg },
                        { "output_dwg", output },
                        { "font_file", primaryFont },
                        { "big_font_file", bigFont },
                        { "selected_style_names", request.style_names ?? new List<string>() },
                        { "text_entity_count", entityCount },
                        { "changed_style_count", styleCount }
                    }
                };
            }
            finally
            {
                if (File.Exists(stage))
                {
                    File.Delete(stage);
                }
            }
        }

        private static TranslationResponse Translate(TranslationRequest request)
        {
            string output = Path.GetFullPath(request.output_dwg);
            string stage = output + ".partial-" + Guid.NewGuid().ToString("N") + ".dwg";
            int changed = 0;
            int total = 0;
            Autodesk.AutoCAD.ApplicationServices.Document document = null;
            try
            {
                Log.Info("Opening source DWG as an AutoCAD document for " + request.request_id + ".");
                document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.Open(
                    request.source_dwg,
                    false);
                if (document == null)
                {
                    throw new InvalidOperationException("AutoCAD could not open the source DWG as an editable document.");
                }
                using (document.LockDocument())
                {
                    Database database = document.Database;
                    using (Transaction transaction = database.TransactionManager.StartTransaction())
                    {
                        total = ReplaceTexts(database, transaction, request.translations, ref changed);
                        transaction.Commit();
                    }
                    database.UpdateExt(true);
                    document.Editor.Regen();
                    Log.Info("Completed text edits for " + request.request_id + "; saving from locked AutoCAD document.");
                    database.SaveAs(stage, DwgVersion.Current);
                    if (!File.Exists(stage) || new FileInfo(stage).Length == 0)
                    {
                        throw new IOException("AutoCAD document SaveAs did not create a non-empty stage DWG.");
                    }
                }
                document.CloseAndDiscard();
                document = null;
                if (File.Exists(output))
                {
                    throw new InvalidOperationException("output_dwg appeared during translation; refusing to overwrite it.");
                }
                File.Move(stage, output);
                return new TranslationResponse
                {
                    ok = true,
                    request_id = request.request_id,
                    duplicate = false,
                    operation = "translate",
                    message = "Translated DWG saved without modifying the source file.",
                    data = new Dictionary<string, object>
                    {
                        { "source_dwg", request.source_dwg },
                        { "output_dwg", output },
                        { "text_count", total },
                        { "changed_count", changed },
                        { "mapping_count", request.translations.Count }
                    }
                };
            }
            finally
            {
                if (document != null)
                {
                    document.CloseAndDiscard();
                }
                if (File.Exists(stage))
                {
                    File.Delete(stage);
                }
            }
        }

        private static Database OpenDatabase(string path)
        {
            Database database = new Database(false, true);
            try
            {
                database.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, false, null);
                return database;
            }
            catch
            {
                database.Dispose();
                throw;
            }
        }

        private static Database CreateEditableSnapshot(Database sourceDatabase)
        {
            if (sourceDatabase == null)
            {
                throw new ArgumentNullException("sourceDatabase");
            }
            Log.Info("Creating editable WBLOCK snapshot.");
            Database snapshot = sourceDatabase.Wblock();
            if (snapshot == null)
            {
                throw new InvalidOperationException("AutoCAD returned no editable WBLOCK snapshot.");
            }
            return snapshot;
        }

        private static void SaveDatabaseAs(Database database, string path)
        {
            Database previousWorkingDatabase = HostApplicationServices.WorkingDatabase;
            try
            {
                HostApplicationServices.WorkingDatabase = database;
                Log.Info("Saving editable WBLOCK snapshot to stage file.");
                database.SaveAs(path, DwgVersion.Current);
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                {
                    throw new IOException("AutoCAD SaveAs returned without creating a non-empty stage DWG.");
                }
                Log.Info("Stage DWG saved successfully.");
            }
            finally
            {
                HostApplicationServices.WorkingDatabase = previousWorkingDatabase;
            }
        }

        private static List<Dictionary<string, object>> ReadTexts(Database database, Transaction transaction)
        {
            var result = new List<Dictionary<string, object>>();
            BlockTable table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId blockId in table)
            {
                BlockTableRecord block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
                foreach (ObjectId entityId in block)
                {
                    Entity entity = transaction.GetObject(entityId, OpenMode.ForRead, false) as Entity;
                    AddEntityText(result, entity, block.Name);
                }
            }
            foreach (ObjectId blockId in table)
            {
                BlockTableRecord block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
                foreach (ObjectId entityId in block)
                {
                    BlockReference reference = transaction.GetObject(entityId, OpenMode.ForRead, false) as BlockReference;
                    if (reference == null || reference.AttributeCollection == null)
                    {
                        continue;
                    }
                    foreach (ObjectId attributeId in reference.AttributeCollection)
                    {
                        AttributeReference attribute = transaction.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference;
                        if (attribute != null)
                        {
                            AddText(
                                result,
                                "AttributeReference",
                                attribute.Handle.ToString(),
                                block.Name,
                                attribute.TextString,
                                attribute.Layer,
                                attribute.Position);
                        }
                    }
                }
            }
            return result;
        }

        private static List<Dictionary<string, object>> ReadTextStyles(Database database, Transaction transaction)
        {
            var result = new List<Dictionary<string, object>>();
            TextStyleTable table = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
            foreach (ObjectId styleId in table)
            {
                TextStyleTableRecord style = transaction.GetObject(styleId, OpenMode.ForRead) as TextStyleTableRecord;
                if (style == null)
                {
                    continue;
                }
                result.Add(new Dictionary<string, object>
                {
                    { "name", style.Name },
                    { "font_file", style.FileName },
                    { "big_font_file", style.BigFontFileName },
                    { "is_shape_file", style.IsShapeFile },
                    { "is_vertical", style.IsVertical }
                });
            }
            return result;
        }

        private static HashSet<ObjectId> CollectTextStyleIds(Database database, Transaction transaction, ref int entityCount)
        {
            var styleIds = new HashSet<ObjectId>();
            BlockTable table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId blockId in table)
            {
                BlockTableRecord block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
                foreach (ObjectId entityId in block)
                {
                    Entity entity = transaction.GetObject(entityId, OpenMode.ForRead, false) as Entity;
                    AddEntityTextStyle(entity, styleIds, ref entityCount, transaction);
                }
            }
            foreach (ObjectId blockId in table)
            {
                BlockTableRecord block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
                foreach (ObjectId entityId in block)
                {
                    BlockReference reference = transaction.GetObject(entityId, OpenMode.ForRead, false) as BlockReference;
                    if (reference == null || reference.AttributeCollection == null)
                    {
                        continue;
                    }
                    foreach (ObjectId attributeId in reference.AttributeCollection)
                    {
                        AttributeReference attribute = transaction.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference;
                        if (attribute != null)
                        {
                            styleIds.Add(attribute.TextStyleId);
                            entityCount++;
                        }
                    }
                }
            }
            return styleIds;
        }

        private static void AddEntityTextStyle(Entity entity, HashSet<ObjectId> styleIds, ref int entityCount, Transaction transaction)
        {
            if (entity == null)
            {
                return;
            }
            DBText text = entity as DBText;
            if (text != null)
            {
                styleIds.Add(text.TextStyleId);
                entityCount++;
                return;
            }
            MText mtext = entity as MText;
            if (mtext != null)
            {
                styleIds.Add(mtext.TextStyleId);
                entityCount++;
                return;
            }
            AttributeDefinition definition = entity as AttributeDefinition;
            if (definition != null)
            {
                styleIds.Add(definition.TextStyleId);
                entityCount++;
                return;
            }
            Dimension dimension = entity as Dimension;
            if (dimension != null)
            {
                styleIds.Add(dimension.TextStyleId);
                entityCount++;
                return;
            }
            Table table = entity as Table;
            if (table != null)
            {
                TableStyle tableStyle = transaction.GetObject(table.TableStyle, OpenMode.ForRead, false) as TableStyle;
                for (int row = 0; row < table.Rows.Count; row++)
                {
                    for (int column = 0; column < table.Columns.Count; column++)
                    {
                        Cell cell = table.Cells[row, column];
                        ObjectId? styleId = cell.TextStyleId;
                        if (styleId.HasValue && !styleId.Value.IsNull)
                        {
                            styleIds.Add(styleId.Value);
                        }
                        if (tableStyle != null)
                        {
                            string cellStyleName = cell.Style;
                            if (!string.IsNullOrEmpty(cellStyleName))
                            {
                                ObjectId tableStyleTextId = tableStyle.TextStyle(cellStyleName);
                                if (!tableStyleTextId.IsNull)
                                {
                                    styleIds.Add(tableStyleTextId);
                                }
                            }
                        }
                        entityCount++;
                    }
                }
            }
        }

        private static int RepairTextStyles(Database database, Transaction transaction, HashSet<ObjectId> styleIds, string primaryFont, string bigFont, List<string> selectedStyleNames)
        {
            int changed = 0;
            HashSet<string> selected = selectedStyleNames == null
                ? null
                : new HashSet<string>(selectedStyleNames, StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId styleId in styleIds)
            {
                TextStyleTableRecord style = transaction.GetObject(styleId, OpenMode.ForWrite, false) as TextStyleTableRecord;
                if (style == null || style.IsErased)
                {
                    continue;
                }
                if (selected != null && !selected.Contains(style.Name))
                {
                    continue;
                }
                bool needsChange = !string.Equals(style.FileName, primaryFont, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(style.BigFontFileName, bigFont, StringComparison.OrdinalIgnoreCase);
                if (!needsChange)
                {
                    continue;
                }
                style.FileName = primaryFont;
                style.BigFontFileName = bigFont;
                changed++;
            }
            return changed;
        }

        private static int ReplaceTexts(Database database, Transaction transaction, Dictionary<string, string> translations, ref int changed)
        {
            int total = 0;
            BlockTable table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId blockId in table)
            {
                BlockTableRecord block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
                foreach (ObjectId entityId in block)
                {
                    Entity entity = transaction.GetObject(entityId, OpenMode.ForWrite, false) as Entity;
                    total += ReplaceEntityText(entity, translations, ref changed);
                }
            }
            foreach (ObjectId blockId in table)
            {
                BlockTableRecord block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
                foreach (ObjectId entityId in block)
                {
                    BlockReference reference = transaction.GetObject(entityId, OpenMode.ForRead, false) as BlockReference;
                    if (reference == null || reference.AttributeCollection == null)
                    {
                        continue;
                    }
                    foreach (ObjectId attributeId in reference.AttributeCollection)
                    {
                        AttributeReference attribute = transaction.GetObject(attributeId, OpenMode.ForWrite, false) as AttributeReference;
                        if (attribute != null)
                        {
                            total++;
                            ReplaceValue(
                                attribute.TextString,
                                value => attribute.TextString = value,
                                translations,
                                attribute.Handle.ToString(),
                                ref changed);
                        }
                    }
                }
            }
            return total;
        }

        private static int ReplaceEntityText(Entity entity, Dictionary<string, string> translations, ref int changed)
        {
            if (entity == null)
            {
                return 0;
            }
            DBText text = entity as DBText;
            if (text != null)
            {
                ReplaceValue(text.TextString, value => text.TextString = value, translations, text.Handle.ToString(), ref changed);
                return 1;
            }
            MText mtext = entity as MText;
            if (mtext != null)
            {
                ReplaceValue(mtext.Text, value => mtext.Contents = value, translations, mtext.Handle.ToString(), ref changed);
                return 1;
            }
            AttributeDefinition definition = entity as AttributeDefinition;
            if (definition != null)
            {
                ReplaceValue(definition.TextString, value => definition.TextString = value, translations, definition.Handle.ToString(), ref changed);
                return 1;
            }
            Dimension dimension = entity as Dimension;
            if (dimension != null)
            {
                ReplaceValue(dimension.DimensionText, value => dimension.DimensionText = value, translations, dimension.Handle.ToString(), ref changed);
                return 1;
            }
            return 0;
        }

        private static void AddEntityText(List<Dictionary<string, object>> result, Entity entity, string blockName)
        {
            if (entity == null)
            {
                return;
            }
            DBText text = entity as DBText;
            if (text != null)
            {
                AddText(result, "DBText", text.Handle.ToString(), blockName, text.TextString, text.Layer, text.Position);
                return;
            }
            MText mtext = entity as MText;
            if (mtext != null)
            {
                AddText(result, "MText", mtext.Handle.ToString(), blockName, mtext.Text, mtext.Layer, mtext.Location);
                return;
            }
            AttributeDefinition definition = entity as AttributeDefinition;
            if (definition != null)
            {
                AddText(result, "AttributeDefinition", definition.Handle.ToString(), blockName, definition.TextString, definition.Layer, definition.Position);
                return;
            }
            Dimension dimension = entity as Dimension;
            if (dimension != null)
            {
                AddText(result, "Dimension", dimension.Handle.ToString(), blockName, dimension.DimensionText, dimension.Layer, dimension.TextPosition);
            }
        }

        private static void AddText(
            List<Dictionary<string, object>> result,
            string type,
            string handle,
            string blockName,
            string value,
            string layer,
            Autodesk.AutoCAD.Geometry.Point3d position)
        {
            if (!string.IsNullOrEmpty(value))
            {
                result.Add(new Dictionary<string, object>
                {
                    { "type", type },
                    { "handle", handle },
                    { "block", blockName },
                    { "text", value },
                    { "layer", layer },
                    { "position", new Dictionary<string, object>
                        {
                            { "x", position.X },
                            { "y", position.Y },
                            { "z", position.Z }
                        }
                    }
                });
            }
        }

        private static string ResolveReplacement(string current, string handle, Dictionary<string, string> translations)
        {
            string replacement;
            if (!string.IsNullOrEmpty(handle) &&
                translations.TryGetValue("@handle:" + handle, out replacement))
            {
                return replacement;
            }
            return translations.TryGetValue(current, out replacement) ? replacement : null;
        }

        private static void ReplaceValue(
            string current,
            Action<string> setter,
            Dictionary<string, string> translations,
            string handle,
            ref int changed)
        {
            if (string.IsNullOrEmpty(current))
            {
                return;
            }
            string translated = ResolveReplacement(current, handle, translations);
            if (translated != null && !string.Equals(current, translated, StringComparison.Ordinal))
            {
                setter(translated);
                changed++;
            }
        }
    }
}
