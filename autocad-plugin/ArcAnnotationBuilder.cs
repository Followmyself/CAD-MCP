using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CadMcp.AutoCAD
{
    internal static class ArcAnnotationBuilder
    {
        internal const string SourceDwg = @"G:\.codex\CAD_Project\统计\Drawing1.dwg";
        private const string XDataApp = "CADMCP_ARC_ANNOTATION";
        private static readonly Regex RequestIdPattern = new Regex(@"^[A-Za-z0-9._:-]{1,128}$", RegexOptions.CultureInvariant);
        private static readonly Regex LengthPattern = new Regex(@"L\s*=?\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex RadiusPattern = new Regex(@"R\s*=?\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static string Validate(TranslationRequest request, string operation)
        {
            if (request == null)
            {
                return "Request body is required.";
            }
            if (!RequestIdPattern.IsMatch(request.request_id ?? string.Empty))
            {
                return "request_id must contain 1 to 128 letters, digits, dots, underscores, colons, or hyphens.";
            }
            if (string.IsNullOrWhiteSpace(request.source_dwg) ||
                !string.Equals(Path.GetFullPath(request.source_dwg), Path.GetFullPath(SourceDwg), StringComparison.OrdinalIgnoreCase))
            {
                return "source_dwg must be G:\\.codex\\CAD_Project\\统计\\Drawing1.dwg.";
            }
            if (!File.Exists(request.source_dwg))
            {
                return "source_dwg does not exist.";
            }
            if (operation == "arc_apply")
            {
                if (request.length_decimals < 0 || request.length_decimals > 6 ||
                    request.radius_decimals < 0 || request.radius_decimals > 6)
                {
                    return "length_decimals and radius_decimals must be between 0 and 6.";
                }
                if (double.IsNaN(request.text_height) || double.IsInfinity(request.text_height) || request.text_height < 0)
                {
                    return "text_height must be zero (automatic) or a finite positive number.";
                }
                string template = request.label_template ?? string.Empty;
                if (!template.Contains("{length}") || !template.Contains("{radius}"))
                {
                    return "label_template must contain {length} and {radius}.";
                }
            }
            return null;
        }

        internal static TranslationResponse Execute(string operation, TranslationRequest request)
        {
            if (operation == "arc_inspect")
            {
                return Inspect(request, false);
            }
            if (operation == "arc_apply")
            {
                return Apply(request);
            }
            if (operation == "arc_verify")
            {
                return Inspect(request, true);
            }
            return TranslationResponse.Failure(request == null ? null : request.request_id, operation, "Unknown arc annotation operation.");
        }

        private static TranslationResponse Inspect(TranslationRequest request, bool verify)
        {
            Document document = null;
            try
            {
                Log.Info("Arc annotation " + (verify ? "verify" : "inspect") + ": opening DWG as an AutoCAD document.");
                document = AcApplication.DocumentManager.Open(request.source_dwg, false);
                if (document == null)
                {
                    throw new InvalidOperationException("AutoCAD could not open the DWG for arc annotation inspection.");
                }
                using (document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Database database = document.Database;
                    List<ArcInfo> arcs = ReadArcs(database, transaction);
                    List<Dictionary<string, object>> texts = ReadTexts(database, transaction);
                    List<Dictionary<string, object>> leaders = ReadLeaders(database, transaction);
                    List<Dictionary<string, object>> dimensions = ReadDimensions(database, transaction);
                    int taggedLabels = CountTagged(database, transaction, "MText");
                    int taggedLeaders = CountTagged(database, transaction, "Leader");
                    int taggedGuideLines = CountTagged(database, transaction, "Line");
                    List<Dictionary<string, object>> mismatches = VerifyTaggedLabels(database, transaction, arcs);
                    transaction.Commit();

                    bool ok = !verify || (arcs.Count > 0 && taggedLabels == arcs.Count && taggedGuideLines == arcs.Count && mismatches.Count == 0);
                    return new TranslationResponse
                    {
                        ok = ok,
                        request_id = request.request_id,
                        duplicate = false,
                        operation = verify ? "arc_verify" : "arc_inspect",
                        message = verify
                            ? "Arc annotations reopened and verified through AutoCAD Managed API."
                            : "Arc geometry and existing annotation format inspected through AutoCAD Managed API.",
                        error = ok ? null : "Arc annotation verification failed; inspect data.mismatches and tagged counts.",
                        data = new Dictionary<string, object>
                        {
                            { "source_dwg", request.source_dwg },
                            { "native_arc_count", arcs.Count(item => item.Kind == "Arc") },
                            { "polyline_arc_segment_count", arcs.Count(item => item.Kind == "PolylineArcSegment") },
                            { "arc_count", arcs.Count },
                            { "arcs", arcs.Select(item => item.ToDictionary()).ToList() },
                            { "text_count", texts.Count },
                            { "texts", texts },
                            { "leader_count", leaders.Count },
                            { "leaders", leaders },
                            { "dimension_count", dimensions.Count },
                            { "dimensions", dimensions },
                            { "tagged_label_count", taggedLabels },
                            { "tagged_leader_count", taggedLeaders },
                            { "tagged_guide_line_count", taggedGuideLines },
                            { "mismatches", mismatches },
                            { "insunits", database.Insunits.ToString() },
                            { "extmin", Point(database.Extmin) },
                            { "extmax", Point(database.Extmax) }
                        }
                    };
                }
            }
            finally
            {
                if (document != null)
                {
                    document.CloseAndDiscard();
                }
            }
        }

        private static TranslationResponse Apply(TranslationRequest request)
        {
            string source = Path.GetFullPath(request.source_dwg);
            string directory = Path.GetDirectoryName(source);
            string stage = Path.Combine(directory, ".arc-annotation-stage-" + request.request_id + ".dwg");
            string backup = Path.Combine(directory, "Drawing1.before-arc-annotations-" + request.request_id + ".dwg");
            if (File.Exists(stage))
            {
                throw new InvalidOperationException("A stale stage DWG exists; refusing to overwrite it: " + stage);
            }
            try
            {
                Log.Info("Arc annotation apply " + request.request_id + ": opening source DWG.");
                Document document = null;
                int createdLabels;
                int createdLeaders;
                int skippedExisting;
                List<string> labelHandles = new List<string>();
                string styleName;
                string layerName;
                double usedTextHeight;
                bool duplicate;

                try
                {
                    document = AcApplication.DocumentManager.Open(source, false);
                    if (document == null)
                    {
                        throw new InvalidOperationException("AutoCAD could not open the source DWG as an editable document.");
                    }
                    using (document.LockDocument())
                    using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                    {
                        Database database = document.Database;
                        Log.Info("Arc annotation apply " + request.request_id + ": document lock and transaction acquired.");
                        List<ArcInfo> arcs = ReadArcs(database, transaction);
                        if (arcs.Count == 0)
                        {
                            throw new InvalidOperationException("No native Arc or bulged Polyline arc segments were found in model space.");
                        }
                        duplicate = HasRequestMarker(database, transaction, request.request_id);
                        if (duplicate)
                        {
                            transaction.Commit();
                            return new TranslationResponse
                            {
                                ok = true,
                                request_id = request.request_id,
                                duplicate = true,
                                operation = "arc_apply",
                                message = "This request_id is already persisted in the DWG; no entities were added.",
                                data = new Dictionary<string, object>
                                {
                                    { "source_dwg", source },
                                    { "arc_count", arcs.Count },
                                    { "created_label_count", 0 },
                                    { "created_guide_line_count", 0 }
                                }
                            };
                        }

                        BlockTable blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                        BlockTableRecord model = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                        AnnotationStyle style = FindAnnotationStyle(database, transaction, request.text_height);
                        styleName = style.TextStyleName;
                        layerName = style.LayerName;
                        usedTextHeight = style.TextHeight;
                        EnsureRegApp(database, transaction);

                        List<ExistingLabel> existing = ReadExistingLabels(database, transaction, true);
                        Dictionary<string, Point3d> labelPositions = BuildColumnPositions(arcs, database.Extmin, database.Extmax, style.TextHeight);
                        createdLabels = 0;
                        createdLeaders = 0;
                        skippedExisting = 0;
                        foreach (ArcInfo arc in arcs)
                        {
                            if (existing.Any(label => NearlyEqual(label.Length, arc.Length) && NearlyEqual(label.Radius, arc.Radius)))
                            {
                                skippedExisting++;
                                continue;
                            }

                            string contents = FormatLabel(request.label_template, request.length_decimals, request.radius_decimals, arc.Length, arc.Radius);
                            Point3d landing = labelPositions[arc.Key];

                            MText text = new MText();
                            text.SetDatabaseDefaults(database);
                            text.LayerId = style.LayerId;
                            text.TextStyleId = style.TextStyleId;
                            text.TextHeight = style.TextHeight;
                            text.Rotation = style.Rotation;
                            text.Attachment = arc.Kind == "Arc" ? AttachmentPoint.MiddleRight : AttachmentPoint.MiddleLeft;
                            text.Location = landing;
                            text.Contents = contents;
                            model.AppendEntity(text);
                            transaction.AddNewlyCreatedDBObject(text, true);
                            SetTag(text, request.request_id, arc.Key);
                            labelHandles.Add(text.Handle.ToString());
                            createdLabels++;

                            if (request.leader)
                            {
                                Line guide = new Line(arc.MidPoint, landing);
                                guide.SetDatabaseDefaults(database);
                                guide.LayerId = style.LayerId;
                                model.AppendEntity(guide);
                                transaction.AddNewlyCreatedDBObject(guide, true);
                                SetTag(guide, request.request_id, arc.Key);
                                createdLeaders++;
                            }
                        }
                        if (createdLabels == 0 && skippedExisting == 0)
                        {
                            throw new InvalidOperationException("No arc annotations were created.");
                        }
                        WriteRequestMarker(database, transaction, request.request_id, createdLabels);
                        transaction.Commit();
                        database.UpdateExt(true);
                        document.Editor.Regen();
                        Log.Info("Arc annotation apply " + request.request_id + ": transaction committed; saving stage DWG from locked document.");
                        database.SaveAs(stage, DwgVersion.Current);
                        if (!File.Exists(stage) || new FileInfo(stage).Length == 0)
                        {
                            throw new IOException("AutoCAD SaveAs did not create a non-empty stage DWG.");
                        }
                        Log.Info("Arc annotation apply " + request.request_id + ": stage DWG saved.");
                    }
                    document.CloseAndDiscard();
                    document = null;
                }
                finally
                {
                    if (document != null)
                    {
                        document.CloseAndDiscard();
                    }
                }

                if (File.Exists(backup))
                {
                    throw new InvalidOperationException("Backup path already exists; refusing to overwrite it: " + backup);
                }
                File.Replace(stage, source, backup, true);
                Log.Info("Arc annotation apply " + request.request_id + ": source atomically replaced; activating drawing.");
                ActivateDrawing(source);
                Log.Info("Arc annotation apply " + request.request_id + ": completed.");

                return new TranslationResponse
                {
                    ok = true,
                    request_id = request.request_id,
                    duplicate = false,
                    operation = "arc_apply",
                    message = "Arc length and radius labels were atomically written to Drawing1.dwg and the drawing was opened in AutoCAD.",
                    data = new Dictionary<string, object>
                    {
                        { "source_dwg", source },
                        { "backup_dwg", backup },
                        { "created_label_count", createdLabels },
                        { "created_guide_line_count", createdLeaders },
                        { "skipped_existing_count", skippedExisting },
                        { "label_handles", labelHandles },
                        { "text_style", styleName },
                        { "layer", layerName },
                        { "text_height", usedTextHeight },
                        { "label_template", request.label_template },
                        { "length_decimals", request.length_decimals },
                        { "radius_decimals", request.radius_decimals },
                        { "layout", "two external columns with guide lines" }
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

        internal static string FormatLabel(string template, int lengthDecimals, int radiusDecimals, double length, double radius)
        {
            string lengthFormat = "F" + lengthDecimals.ToString(CultureInfo.InvariantCulture);
            string radiusFormat = "F" + radiusDecimals.ToString(CultureInfo.InvariantCulture);
            return template
                .Replace("{length}", length.ToString(lengthFormat, CultureInfo.InvariantCulture))
                .Replace("{radius}", radius.ToString(radiusFormat, CultureInfo.InvariantCulture));
        }

        private static Dictionary<string, Point3d> BuildColumnPositions(List<ArcInfo> arcs, Point3d extmin, Point3d extmax, double textHeight)
        {
            var result = new Dictionary<string, Point3d>(StringComparer.Ordinal);
            List<ArcInfo> left = arcs.Where(item => item.Kind == "Arc").OrderByDescending(item => item.Radius).ToList();
            List<ArcInfo> right = arcs.Where(item => item.Kind == "PolylineArcSegment").OrderByDescending(item => item.Radius).ToList();
            AddColumn(result, left, extmin.X - Math.Max(2.0, textHeight * 1.5), extmin.Y, extmax.Y);
            AddColumn(result, right, extmax.X + Math.Max(2.0, textHeight * 1.5), extmin.Y, extmax.Y);
            return result;
        }

        private static void AddColumn(Dictionary<string, Point3d> positions, List<ArcInfo> arcs, double x, double bottom, double top)
        {
            if (arcs.Count == 0) return;
            double span = Math.Max(1.0, top - bottom);
            double step = arcs.Count == 1 ? 0 : span / (arcs.Count - 1);
            for (int index = 0; index < arcs.Count; index++)
            {
                positions[arcs[index].Key] = new Point3d(x, top - index * step, 0);
            }
        }

        private static List<ArcInfo> ReadArcs(Database database, Transaction transaction)
        {
            var result = new List<ArcInfo>();
            BlockTable table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            BlockTableRecord model = (BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in model)
            {
                Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                Arc arc = entity as Arc;
                if (arc != null)
                {
                    double middle = arc.StartParam + (arc.EndParam - arc.StartParam) / 2.0;
                    Point3d midpoint = arc.GetPointAtParameter(middle);
                    result.Add(new ArcInfo
                    {
                        Kind = "Arc",
                        Key = arc.Handle.ToString(),
                        Handle = arc.Handle.ToString(),
                        SegmentIndex = -1,
                        Layer = arc.Layer,
                        Center = arc.Center,
                        MidPoint = midpoint,
                        LabelDirection = midpoint - arc.Center,
                        Radius = arc.Radius,
                        Length = arc.GetDistanceAtParameter(arc.EndParam),
                        StartAngle = arc.StartAngle,
                        EndAngle = arc.EndAngle
                    });
                    continue;
                }
                Polyline polyline = entity as Polyline;
                if (polyline == null || polyline.NumberOfVertices < 2)
                {
                    continue;
                }
                int segments = polyline.Closed ? polyline.NumberOfVertices : polyline.NumberOfVertices - 1;
                for (int index = 0; index < segments; index++)
                {
                    double bulge = polyline.GetBulgeAt(index);
                    if (Math.Abs(bulge) < 1e-12)
                    {
                        continue;
                    }
                    Point2d start = polyline.GetPoint2dAt(index);
                    Point2d end = polyline.GetPoint2dAt((index + 1) % polyline.NumberOfVertices);
                    double chord = start.GetDistanceTo(end);
                    double theta = 4.0 * Math.Atan(Math.Abs(bulge));
                    double radius = chord * (1.0 + bulge * bulge) / (4.0 * Math.Abs(bulge));
                    Point3d midpoint = polyline.GetPointAtParameter(index + 0.5);
                    Point3d chordMidpoint = new Point3d((start.X + end.X) / 2.0, (start.Y + end.Y) / 2.0, polyline.Elevation);
                    result.Add(new ArcInfo
                    {
                        Kind = "PolylineArcSegment",
                        Key = polyline.Handle + ":" + index.ToString(CultureInfo.InvariantCulture),
                        Handle = polyline.Handle.ToString(),
                        SegmentIndex = index,
                        Layer = polyline.Layer,
                        Center = chordMidpoint,
                        MidPoint = midpoint,
                        LabelDirection = midpoint - chordMidpoint,
                        Radius = radius,
                        Length = radius * theta,
                        StartAngle = 0,
                        EndAngle = theta
                    });
                }
            }
            return result;
        }

        private static List<Dictionary<string, object>> ReadTexts(Database database, Transaction transaction)
        {
            var result = new List<Dictionary<string, object>>();
            BlockTable table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            BlockTableRecord model = (BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in model)
            {
                Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                MText mtext = entity as MText;
                if (mtext != null)
                {
                    result.Add(TextDictionary("MText", mtext.Handle.ToString(), mtext.Contents, mtext.Layer, mtext.Location, mtext.TextHeight, mtext.Rotation, TextStyleName(mtext.TextStyleId, transaction), IsTagged(mtext)));
                    continue;
                }
                DBText text = entity as DBText;
                if (text != null)
                {
                    result.Add(TextDictionary("DBText", text.Handle.ToString(), text.TextString, text.Layer, text.Position, text.Height, text.Rotation, TextStyleName(text.TextStyleId, transaction), IsTagged(text)));
                }
            }
            return result;
        }

        private static List<Dictionary<string, object>> ReadLeaders(Database database, Transaction transaction)
        {
            var result = new List<Dictionary<string, object>>();
            BlockTable table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            BlockTableRecord model = (BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in model)
            {
                Leader leader = transaction.GetObject(id, OpenMode.ForRead, false) as Leader;
                if (leader == null)
                {
                    continue;
                }
                var vertices = new List<Dictionary<string, object>>();
                for (int index = 0; index < leader.NumVertices; index++)
                {
                    vertices.Add(Point(leader.VertexAt(index)));
                }
                result.Add(new Dictionary<string, object>
                {
                    { "handle", leader.Handle.ToString() },
                    { "layer", leader.Layer },
                    { "has_arrow_head", leader.HasArrowHead },
                    { "vertices", vertices },
                    { "tagged", IsTagged(leader) }
                });
            }
            return result;
        }

        private static List<Dictionary<string, object>> ReadDimensions(Database database, Transaction transaction)
        {
            var result = new List<Dictionary<string, object>>();
            BlockTable table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            BlockTableRecord model = (BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in model)
            {
                Dimension dimension = transaction.GetObject(id, OpenMode.ForRead, false) as Dimension;
                if (dimension == null)
                {
                    continue;
                }
                result.Add(new Dictionary<string, object>
                {
                    { "type", dimension.GetType().Name },
                    { "handle", dimension.Handle.ToString() },
                    { "layer", dimension.Layer },
                    { "dimension_text", dimension.DimensionText },
                    { "measurement", dimension.Measurement },
                    { "text_position", Point(dimension.TextPosition) }
                });
            }
            return result;
        }

        private static AnnotationStyle FindAnnotationStyle(Database database, Transaction transaction, double requestedHeight)
        {
            var heights = new List<double>();
            AnnotationStyle sample = null;
            BlockTable table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            BlockTableRecord model = (BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in model)
            {
                Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                MText mtext = entity as MText;
                if (mtext != null)
                {
                    if (mtext.TextHeight > 0) heights.Add(mtext.TextHeight);
                    if (sample == null && LooksLikeArcLabel(mtext.Contents))
                    {
                        sample = new AnnotationStyle(mtext.TextStyleId, TextStyleName(mtext.TextStyleId, transaction), mtext.LayerId, mtext.Layer, mtext.TextHeight, mtext.Rotation);
                    }
                    continue;
                }
                DBText text = entity as DBText;
                if (text != null)
                {
                    if (text.Height > 0) heights.Add(text.Height);
                    if (sample == null && LooksLikeArcLabel(text.TextString))
                    {
                        sample = new AnnotationStyle(text.TextStyleId, TextStyleName(text.TextStyleId, transaction), text.LayerId, text.Layer, text.Height, text.Rotation);
                    }
                }
            }
            double height = requestedHeight > 0 ? requestedHeight : Median(heights);
            if (height <= 0)
            {
                double diagonal = database.Extmin.DistanceTo(database.Extmax);
                height = diagonal > 0 ? diagonal / 100.0 : 2.5;
            }
            if (sample != null)
            {
                sample.TextHeight = requestedHeight > 0 ? requestedHeight : sample.TextHeight;
                if (sample.TextHeight <= 0) sample.TextHeight = height;
                return sample;
            }
            TextStyleTable styles = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
            ObjectId textStyleId = styles.Has("宋体") ? styles["宋体"] : database.Textstyle;
            LayerTable layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            ObjectId layerId = layers.Has("标注") ? layers["标注"] : database.Clayer;
            return new AnnotationStyle(textStyleId, TextStyleName(textStyleId, transaction), layerId, LayerName(layerId, transaction), height, 0);
        }

        private static List<ExistingLabel> ReadExistingLabels(Database database, Transaction transaction, bool taggedOnly)
        {
            var result = new List<ExistingLabel>();
            foreach (Dictionary<string, object> item in ReadTexts(database, transaction))
            {
                if (taggedOnly && !Convert.ToBoolean(item["tagged"], CultureInfo.InvariantCulture))
                {
                    continue;
                }
                string content = Convert.ToString(item["content"], CultureInfo.InvariantCulture);
                Match length = LengthPattern.Match(content ?? string.Empty);
                Match radius = RadiusPattern.Match(content ?? string.Empty);
                double l;
                double r;
                if (length.Success && radius.Success &&
                    double.TryParse(length.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out l) &&
                    double.TryParse(radius.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out r))
                {
                    result.Add(new ExistingLabel { Length = l, Radius = r });
                }
            }
            return result;
        }

        private static List<Dictionary<string, object>> VerifyTaggedLabels(Database database, Transaction transaction, List<ArcInfo> arcs)
        {
            var mismatches = new List<Dictionary<string, object>>();
            foreach (ExistingLabel label in ReadExistingLabels(database, transaction, true))
            {
                if (!arcs.Any(arc => NearlyEqual(label.Length, arc.Length) && NearlyEqual(label.Radius, arc.Radius)))
                {
                    mismatches.Add(new Dictionary<string, object>
                    {
                        { "length", label.Length },
                        { "radius", label.Radius },
                        { "error", "No matching arc geometry." }
                    });
                }
            }
            return mismatches;
        }

        private static bool NearlyEqual(double first, double second)
        {
            return Math.Abs(first - second) <= Math.Max(0.051, Math.Abs(second) * 0.0002);
        }

        private static bool LooksLikeArcLabel(string content)
        {
            return LengthPattern.IsMatch(content ?? string.Empty) && RadiusPattern.IsMatch(content ?? string.Empty);
        }

        private static int CountTagged(Database database, Transaction transaction, string typeName)
        {
            int count = 0;
            BlockTable table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            BlockTableRecord model = (BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in model)
            {
                Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (entity != null && entity.GetType().Name == typeName && IsTagged(entity)) count++;
            }
            return count;
        }

        private static bool HasRequestMarker(Database database, Transaction transaction, string requestId)
        {
            BlockTable table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            BlockTableRecord model = (BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in model)
            {
                Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                ResultBuffer data = entity == null ? null : entity.GetXDataForApplication(XDataApp);
                if (data == null) continue;
                foreach (TypedValue value in data)
                {
                    if (value.TypeCode == 1000 && string.Equals(Convert.ToString(value.Value), requestId, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static void WriteRequestMarker(Database database, Transaction transaction, string requestId, int createdCount)
        {
            DBDictionary dictionary = (DBDictionary)transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForWrite);
            string key = "CADMCP_ARC_" + requestId;
            if (dictionary.Contains(key)) return;
            Xrecord record = new Xrecord
            {
                Data = new ResultBuffer(
                    new TypedValue((int)DxfCode.Text, requestId),
                    new TypedValue((int)DxfCode.Int32, createdCount))
            };
            dictionary.SetAt(key, record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static void EnsureRegApp(Database database, Transaction transaction)
        {
            RegAppTable table = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead);
            if (table.Has(XDataApp)) return;
            table.UpgradeOpen();
            RegAppTableRecord record = new RegAppTableRecord { Name = XDataApp };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static void SetTag(Entity entity, string requestId, string arcKey)
        {
            entity.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, XDataApp),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, requestId),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, arcKey));
        }

        private static bool IsTagged(Entity entity)
        {
            return entity.GetXDataForApplication(XDataApp) != null;
        }

        private static void ActivateDrawing(string path)
        {
            DocumentCollection documents = AcApplication.DocumentManager;
            foreach (Document document in documents)
            {
                if (string.Equals(Path.GetFullPath(document.Name), path, StringComparison.OrdinalIgnoreCase))
                {
                    documents.MdiActiveDocument = document;
                    document.Editor.Regen();
                    return;
                }
            }
            Document opened = documents.Open(path, false);
            documents.MdiActiveDocument = opened;
            opened.Editor.Regen();
        }

        private static Dictionary<string, object> TextDictionary(string type, string handle, string content, string layer, Point3d position, double height, double rotation, string style, bool tagged)
        {
            return new Dictionary<string, object>
            {
                { "type", type }, { "handle", handle }, { "content", content }, { "layer", layer },
                { "position", Point(position) }, { "height", height }, { "rotation", rotation },
                { "text_style", style }, { "tagged", tagged }
            };
        }

        private static Dictionary<string, object> Point(Point3d point)
        {
            return new Dictionary<string, object> { { "x", point.X }, { "y", point.Y }, { "z", point.Z } };
        }

        private static string TextStyleName(ObjectId id, Transaction transaction)
        {
            TextStyleTableRecord record = transaction.GetObject(id, OpenMode.ForRead, false) as TextStyleTableRecord;
            return record == null ? string.Empty : record.Name;
        }

        private static string LayerName(ObjectId id, Transaction transaction)
        {
            LayerTableRecord record = transaction.GetObject(id, OpenMode.ForRead, false) as LayerTableRecord;
            return record == null ? string.Empty : record.Name;
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0) return 0;
            values.Sort();
            int middle = values.Count / 2;
            return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2.0;
        }

        private sealed class ArcInfo
        {
            public string Kind;
            public string Key;
            public string Handle;
            public int SegmentIndex;
            public string Layer;
            public Point3d Center;
            public Point3d MidPoint;
            public Vector3d LabelDirection;
            public double Radius;
            public double Length;
            public double StartAngle;
            public double EndAngle;

            public Dictionary<string, object> ToDictionary()
            {
                return new Dictionary<string, object>
                {
                    { "kind", Kind }, { "key", Key }, { "handle", Handle }, { "segment_index", SegmentIndex },
                    { "layer", Layer }, { "center", Point(Center) }, { "midpoint", Point(MidPoint) },
                    { "radius", Radius }, { "length", Length }, { "start_angle", StartAngle }, { "end_angle", EndAngle }
                };
            }
        }

        private sealed class ExistingLabel
        {
            public double Length;
            public double Radius;
        }

        private sealed class AnnotationStyle
        {
            public AnnotationStyle(ObjectId textStyleId, string textStyleName, ObjectId layerId, string layerName, double textHeight, double rotation)
            {
                TextStyleId = textStyleId;
                TextStyleName = textStyleName;
                LayerId = layerId;
                LayerName = layerName;
                TextHeight = textHeight;
                Rotation = rotation;
            }

            public ObjectId TextStyleId;
            public string TextStyleName;
            public ObjectId LayerId;
            public string LayerName;
            public double TextHeight;
            public double Rotation;
        }
    }
}
