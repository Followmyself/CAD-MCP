using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CadMcp.AutoCAD
{
    internal static class CopperWaterstopBuilder
    {
        private const string OutputDirectory = @"G:\.codex\CAD_Project\画图";
        private const string SourceDwt = @"G:\.codex\skills\company-hydraulic-rc-design\assets\公司水利钢筋混凝土设计.dwt";
        private const string SourceImage = @"G:\.codex\CAD_Project\画图\紫铜片止水.png";
        private const string SourceImageSha256 = "062A41BE2CD97317DE36C9A0A434C884624C0339D2B21283E976C26CE7D56F73";
        private const string OutputFileName = "紫铜片止水.dwg";
        private const string PreviewPdfFileName = "紫铜片止水_预览.pdf";
        private const string Standard = "COMPANY-HYDRO-RC-2026";

        private const string OutlineLayer = "轮廓";
        private const string CoarseLayer = "粗实线";
        private const string FineLayer = "细实";
        private const string FillLayer = "填充";
        private const string TextLayer = "文字";
        private const string DimensionLayer = "标注";
        private const string FrameLayer = "图框";
        private const string RebarLayer = "REIN";
        private const string RebarTableLayer = "钢筋表";
        private const string TextStyleName = "宋体";
        private const string DimensionStyleName = "1：20";

        internal static string Validate(TemplateRequest request, string operation)
        {
            if (request == null)
            {
                return "Request body is required.";
            }
            if (operation != "copper_waterstop_build" && operation != "copper_waterstop_verify")
            {
                return "Unknown copper waterstop operation.";
            }
            if (!PathEquals(request.output_dir, OutputDirectory))
            {
                return "output_dir must be exactly " + OutputDirectory + ".";
            }
            if (!PathEquals(request.source_dwg, SourceDwt) || !File.Exists(SourceDwt))
            {
                return "source_dwg must be the company hydraulic RC DWT: " + SourceDwt + ".";
            }
            if (!PathEquals(request.image_path, SourceImage) || !File.Exists(SourceImage))
            {
                return "image_path must be the inspected copper waterstop image: " + SourceImage + ".";
            }
            if (!string.Equals(Sha256(SourceImage), SourceImageSha256, StringComparison.OrdinalIgnoreCase))
            {
                return "Source image SHA-256 changed; inspect the new image before drawing.";
            }
            if (operation == "copper_waterstop_build")
            {
                if (!ValidRequestId(request.request_id))
                {
                    return "request_id must contain 1 to 128 ASCII letters, digits, dots, underscores, colons or hyphens.";
                }
                if (!string.Equals(request.standard, Standard, StringComparison.Ordinal))
                {
                    return "standard must be " + Standard + ".";
                }
            }
            return null;
        }

        internal static TemplateResponse Execute(string operation, TemplateRequest request)
        {
            if (operation == "copper_waterstop_build")
            {
                return Build(request);
            }
            if (operation == "copper_waterstop_verify")
            {
                return Verify(request);
            }
            return TemplateResponse.Failure(request == null ? null : request.request_id, operation, "Unknown operation.");
        }

        private static TemplateResponse Build(TemplateRequest request)
        {
            Directory.CreateDirectory(OutputDirectory);
            string finalPath = Path.Combine(OutputDirectory, OutputFileName);
            if (File.Exists(finalPath))
            {
                TemplateResponse existing = Verify(request);
                if (existing.ok)
                {
                    EnsurePreviewPdf(finalPath);
                    existing.request_id = request.request_id;
                    existing.operation = "copper_waterstop_build";
                    existing.duplicate = true;
                    existing.message = "Verified existing copper-waterstop DWG; no duplicate drawing was created.";
                    return existing;
                }
                throw new InvalidOperationException("Existing output failed verification and will not be overwritten: " + existing.error);
            }

            string stagePath = Path.Combine(OutputDirectory, ".copper-waterstop-stage-" + request.request_id + ".dwg");
            EnsureChildPath(stagePath, OutputDirectory);
            if (File.Exists(stagePath))
            {
                File.Delete(stagePath);
            }

            Document document = null;
            try
            {
                document = AcApplication.DocumentManager.Add(SourceDwt);
                if (document == null)
                {
                    throw new InvalidOperationException("AutoCAD could not create a drawing from the company hydraulic RC DWT.");
                }
                using (document.LockDocument())
                {
                    Database database = document.Database;
                    database.Insunits = UnitsValue.Millimeters;
                    SetSummary(database, request.request_id);
                    using (Transaction transaction = database.TransactionManager.StartTransaction())
                    {
                        BlockTable table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                        BlockTableRecord model = (BlockTableRecord)transaction.GetObject(
                            table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                        foreach (ObjectId id in model.Cast<ObjectId>().ToArray())
                        {
                            Entity entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                            if (entity != null)
                            {
                                entity.Erase();
                            }
                        }
                        RequireResources(database, transaction);
                        DrawSheet(database, transaction, model);
                        transaction.Commit();
                    }
                    database.UpdateExt(true);
                    document.Editor.Regen();
                    database.SaveAs(stagePath, DwgVersion.Current);
                }
                document.CloseAndDiscard();
                document = null;

                VerifyDrawingDatabase(stagePath);
                File.Move(stagePath, finalPath);
                EnsurePreviewPdf(finalPath);
                TemplateResponse response = Verify(request);
                if (!response.ok)
                {
                    throw new InvalidOperationException("Published DWG failed verification: " + response.error);
                }
                response.request_id = request.request_id;
                response.operation = "copper_waterstop_build";
                response.message = "Copper waterstop image redrawn and verified as an editable DWG through AutoCAD CAD-MCP.";
                return response;
            }
            finally
            {
                if (document != null)
                {
                    document.CloseAndDiscard();
                }
                if (File.Exists(stagePath))
                {
                    File.Delete(stagePath);
                }
            }
        }

        private static TemplateResponse Verify(TemplateRequest request)
        {
            string path = Path.Combine(OutputDirectory, OutputFileName);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                return TemplateResponse.Failure(request == null ? null : request.request_id, "copper_waterstop_verify", "DWG is missing or empty: " + path);
            }
            if (!string.Equals(Sha256(SourceImage), SourceImageSha256, StringComparison.OrdinalIgnoreCase))
            {
                return TemplateResponse.Failure(request == null ? null : request.request_id, "copper_waterstop_verify", "Source image SHA-256 changed after drawing.");
            }

            try
            {
                CloseActiveOutputDocument(path);
                Dictionary<string, object> metrics = InspectDrawing(path);
                int entityCount = Convert.ToInt32(metrics["entity_count"], CultureInfo.InvariantCulture);
                int mtextCount = Convert.ToInt32(metrics["mtext_count"], CultureInfo.InvariantCulture);
                int dimensionCount = Convert.ToInt32(metrics["dimension_count"], CultureInfo.InvariantCulture);
                int lineworkCount = Convert.ToInt32(metrics["linework_count"], CultureInfo.InvariantCulture);
                int solidCount = Convert.ToInt32(metrics["solid_count"], CultureInfo.InvariantCulture);
                int rasterCount = Convert.ToInt32(metrics["raster_count"], CultureInfo.InvariantCulture);
                bool criticalTextPresent = Convert.ToBoolean(metrics["critical_text_present"], CultureInfo.InvariantCulture);
                bool extentsOk = Convert.ToBoolean(metrics["extents_ok"], CultureInfo.InvariantCulture);
                bool layersOk = Convert.ToBoolean(metrics["layers_ok"], CultureInfo.InvariantCulture);
                if (entityCount < 180 || mtextCount < 28 || dimensionCount < 10 ||
                    lineworkCount < 120 || solidCount != 0 || rasterCount != 0 ||
                    !criticalTextPresent || !extentsOk || !layersOk)
                {
                    throw new InvalidOperationException(string.Format(
                        CultureInfo.InvariantCulture,
                        "Semantic CAD verification failed: entities={0}, mtext={1}, dimensions={2}, linework={3}, solids={4}, rasters={5}, critical_text={6}, extents_ok={7}, layers_ok={8}.",
                        entityCount, mtextCount, dimensionCount, lineworkCount, solidCount,
                        rasterCount, criticalTextPresent, extentsOk, layersOk));
                }
                metrics["dwg_path"] = path;
                metrics["dwg_sha256"] = Sha256(path);
                metrics["source_image_path"] = SourceImage;
                metrics["source_image_sha256"] = SourceImageSha256;
                metrics["source_dwt_path"] = SourceDwt;
                metrics["source_dwt_sha256"] = Sha256(SourceDwt);
                string previewPath = Path.Combine(OutputDirectory, PreviewPdfFileName);
                metrics["preview_pdf_path"] = previewPath;
                metrics["preview_pdf_size"] = File.Exists(previewPath) ? new FileInfo(previewPath).Length : 0L;
                metrics["drawing_status"] = "按源图语义化CAD复绘；未改变图示尺寸与材料说明";
                return new TemplateResponse
                {
                    ok = true,
                    request_id = request == null ? null : request.request_id,
                    duplicate = false,
                    operation = "copper_waterstop_verify",
                    message = "DWG reopened in AutoCAD database and passed geometry, layer, dimension and text checks.",
                    error = null,
                    data = metrics
                };
            }
            catch (System.Exception error)
            {
                return TemplateResponse.Failure(request == null ? null : request.request_id, "copper_waterstop_verify", error.Message);
            }
        }

        private static void DrawSheet(Database database, Transaction transaction, BlockTableRecord model)
        {
            AddRectangle(model, transaction, 0, 0, 420, 297, FrameLayer);
            AddRectangle(model, transaction, 5, 5, 410, 287, FrameLayer);
            AddMText(database, model, transaction, 210, 287, "紫铜片止水大样图（伸缩缝处）", 8.0, TextLayer, AttachmentPoint.MiddleCenter, 300);
            AddLine(model, transaction, 118, 279, 302, 279, FineLayer);
            AddLine(model, transaction, 118, 278, 302, 278, FineLayer);
            AddMText(database, model, transaction, 210, 270, "剖面图", 6.0, TextLayer, AttachmentPoint.MiddleCenter, 60);
            AddLine(model, transaction, 195, 265, 225, 265, FineLayer);

            DrawMainSection(database, transaction, model);
            DrawMaterialTable(database, transaction, model);
            DrawCopperDetail(database, transaction, model);
            DrawNotesAndLegend(database, transaction, model);
        }

        private static void DrawMainSection(Database database, Transaction transaction, BlockTableRecord model)
        {
            AddRectangle(model, transaction, 20, 80, 180, 165, OutlineLayer);
            AddRectangle(model, transaction, 220, 80, 180, 165, OutlineLayer);
            AddRectangle(model, transaction, 200, 80, 20, 165, CoarseLayer);
            DrawHoneycomb(transaction, model, 200, 80, 20, 165);
            DrawConcreteTexture(transaction, model, 20, 80, 180, 165, 54, 17);
            DrawConcreteTexture(transaction, model, 220, 80, 180, 165, 54, 43);

            AddLine(model, transaction, 14, 245, 406, 245, OutlineLayer);
            AddMText(database, model, transaction, 350, 260, "临水面", 4.5, TextLayer, AttachmentPoint.MiddleCenter, 50);
            AddLine(model, transaction, 337, 255, 342, 263, FineLayer);
            AddLine(model, transaction, 342, 263, 363, 263, FineLayer);
            AddLine(model, transaction, 348, 251, 362, 251, FineLayer);
            AddLine(model, transaction, 351, 249, 359, 249, FineLayer);

            AddMText(database, model, transaction, 110, 190, "新浇筑砼", 6.0, TextLayer, AttachmentPoint.MiddleCenter, 70);
            AddMText(database, model, transaction, 310, 190, "新浇筑砼", 6.0, TextLayer, AttachmentPoint.MiddleCenter, 70);
            AddMText(database, model, transaction, 210, 252, "伸缩缝宽20", 4.5, TextLayer, AttachmentPoint.MiddleCenter, 55);
            AddMText(database, model, transaction, 258, 222, "闭孔泡沫板嵌缝板\\P（中压）", 4.3, TextLayer, AttachmentPoint.MiddleLeft, 75);
            AddLine(model, transaction, 242, 218, 226, 218, FineLayer);
            AddLine(model, transaction, 226, 218, 220, 211, FineLayer);

            DrawWaterstop(transaction, model, 210, 100, 1.0);
            AddMText(database, model, transaction, 250, 160, "紫铜片止水\\PT2紫铜，δ=1.2mm", 4.0, TextLayer, AttachmentPoint.MiddleLeft, 90);
            AddLine(model, transaction, 250, 154, 235, 154, FineLayer);
            AddLine(model, transaction, 235, 154, 230, 104, FineLayer);

            ObjectId dimStyle = GetDimensionStyle(database, transaction);
            AddDimension(model, transaction, 110, 245, 210, 245, 110, 258, "100", dimStyle);
            AddDimension(model, transaction, 200, 245, 220, 245, 200, 258, "20", dimStyle);
            AddDimension(model, transaction, 210, 245, 310, 245, 210, 258, "100", dimStyle);
            AddDimension(model, transaction, 20, 100, 20, 245, 10, 100, "145", dimStyle);
            AddDimension(model, transaction, 100, 100, 100, 110, 92, 100, "10", dimStyle);
            AddDimension(model, transaction, 320, 100, 320, 110, 328, 100, "10", dimStyle);

            AddMText(database, model, transaction, 40, 72, "注：紫铜片中部做圆弧“鼻子”，\\P高度H=10mm，半径R=10mm。", 3.2, TextLayer, AttachmentPoint.MiddleLeft, 100);
            AddRectangle(model, transaction, 35, 62, 115, 20, FineLayer);
            AddMText(database, model, transaction, 120, 71, "紫铜片埋入砼内\\P≥100", 3.4, TextLayer, AttachmentPoint.MiddleCenter, 55);
            AddMText(database, model, transaction, 210, 71, "伸缩缝宽20", 3.4, TextLayer, AttachmentPoint.MiddleCenter, 45);
            AddMText(database, model, transaction, 300, 71, "紫铜片埋入砼内\\P≥100", 3.4, TextLayer, AttachmentPoint.MiddleCenter, 55);
        }

        private static void DrawCopperDetail(Database database, Transaction transaction, BlockTableRecord model)
        {
            AddMText(database, model, transaction, 210, 58, "铜片构造详图", 4.8, TextLayer, AttachmentPoint.MiddleCenter, 70);
            const double scale = 0.70;
            DrawWaterstop(transaction, model, 210, 34, scale);
            ObjectId dimStyle = GetDimensionStyle(database, transaction);
            double left = 210 - 110 * scale;
            double gapLeft = 210 - 10 * scale;
            double gapRight = 210 + 10 * scale;
            double right = 210 + 110 * scale;
            AddDimension(model, transaction, left, 27, gapLeft, 27, left, 22, "100", dimStyle);
            AddDimension(model, transaction, gapLeft, 27, gapRight, 27, gapLeft, 22, "20", dimStyle);
            AddDimension(model, transaction, gapRight, 27, right, 27, gapRight, 22, "100", dimStyle);
            AddDimension(model, transaction, left, 18, right, 18, left, 13, "220", dimStyle);
            AddDimension(model, transaction, 210, 34, 210, 41, 219, 34, "10", dimStyle);
            AddDimension(model, transaction, right, 34, right, 41, right + 8, 34, "10", dimStyle);
            AddMText(database, model, transaction, 230, 48, "R10", 3.4, DimensionLayer, AttachmentPoint.MiddleCenter, 25);
            AddLine(model, transaction, 224, 46, 218, 40, DimensionLayer);
            AddMText(database, model, transaction, 210, 8, "说明：铜片加工成型后成整体，“鼻子”对准伸缩缝中心。", 3.0, TextLayer, AttachmentPoint.MiddleCenter, 180);
        }

        private static void DrawMaterialTable(Database database, Transaction transaction, BlockTableRecord model)
        {
            const double x = 8;
            const double y = 8;
            const double width = 105;
            const double height = 51;
            AddMText(database, model, transaction, x + width / 2, y + height + 4, "尺寸及材料表", 4.0, TextLayer, AttachmentPoint.MiddleCenter, width);
            AddRectangle(model, transaction, x, y, width, height, RebarTableLayer);
            AddLine(model, transaction, x + 43, y, x + 43, y + height, RebarTableLayer);
            AddLine(model, transaction, x + 74, y, x + 74, y + height, RebarTableLayer);
            for (int row = 1; row < 8; row++)
            {
                AddLine(model, transaction, x, y + row * height / 8.0, x + width, y + row * height / 8.0, RebarTableLayer);
            }
            string[,] values =
            {
                { "项目", "规格/尺寸", "备注" },
                { "紫铜片止水材质", "T2紫铜", "—" },
                { "紫铜片厚度", "δ=1.2mm", "推荐厚度" },
                { "伸缩缝宽度", "20mm", "±2mm" },
                { "紫铜片两侧埋入长度", "≥100mm", "每侧" },
                { "鼻子高度H", "10mm", "±2mm" },
                { "鼻子半径R", "10mm", "±2mm" },
                { "距临水面距离/折边", "145/10mm", "至上表面/两侧" }
            };
            for (int row = 0; row < 8; row++)
            {
                double textY = y + height - (row + 0.5) * height / 8.0;
                AddMText(database, model, transaction, x + 21.5, textY, values[row, 0], 2.15, TextLayer, AttachmentPoint.MiddleCenter, 41);
                AddMText(database, model, transaction, x + 58.5, textY, values[row, 1], 2.15, TextLayer, AttachmentPoint.MiddleCenter, 29);
                AddMText(database, model, transaction, x + 89.5, textY, values[row, 2], 2.15, TextLayer, AttachmentPoint.MiddleCenter, 29);
            }
        }

        private static void DrawNotesAndLegend(Database database, Transaction transaction, BlockTableRecord model)
        {
            AddMText(database, model, transaction, 312, 59, "说明：", 3.6, TextLayer, AttachmentPoint.MiddleLeft, 35);
            AddMText(database, model, transaction, 312, 54,
                "1. 紫铜片应居中设置，不得与钢筋直接接触。\\P" +
                "2. 紫铜片与混凝土密贴，不得扭曲。\\P" +
                "3. 闭孔泡沫板嵌缝顶面与混凝土面齐平。\\P" +
                "4. 紫铜片翼缘埋入砼内的混凝土应振捣密实。",
                2.7, TextLayer, AttachmentPoint.TopLeft, 98);

            AddRectangle(model, transaction, 310, 8, 102, 23, FineLayer);
            AddMText(database, model, transaction, 361, 28, "图例", 3.4, TextLayer, AttachmentPoint.MiddleCenter, 30);
            AddRectangle(model, transaction, 315, 20, 18, 6, FillLayer);
            AddHexagon(model, transaction, 319, 23, 2.2, FillLayer);
            AddHexagon(model, transaction, 325, 23, 2.2, FillLayer);
            AddHexagon(model, transaction, 331, 23, 2.2, FillLayer);
            AddMText(database, model, transaction, 337, 23, "闭孔泡沫板（中压）", 2.6, TextLayer, AttachmentPoint.MiddleLeft, 72);
            AddRectangle(model, transaction, 315, 12, 18, 5, FineLayer);
            AddLine(model, transaction, 315, 14, 333, 14, RebarLayer);
            AddLine(model, transaction, 315, 15.2, 333, 15.2, RebarLayer);
            AddMText(database, model, transaction, 337, 14.5, "紫铜片止水（T2紫铜，δ=1.2mm）", 2.4, TextLayer, AttachmentPoint.MiddleLeft, 72);
        }

        private static void DrawWaterstop(Transaction transaction, BlockTableRecord model, double centerX, double baselineY, double scale)
        {
            double half = 110 * scale;
            double halfGap = 10 * scale;
            double fold = 10 * scale;
            var outer = new Polyline(6) { Layer = RebarLayer };
            outer.AddVertexAt(0, new Point2d(centerX - half, baselineY + fold), 0, 0, 0);
            outer.AddVertexAt(1, new Point2d(centerX - half, baselineY), 0, 0, 0);
            outer.AddVertexAt(2, new Point2d(centerX - halfGap, baselineY), 1.0, 0, 0);
            outer.AddVertexAt(3, new Point2d(centerX + halfGap, baselineY), 0, 0, 0);
            outer.AddVertexAt(4, new Point2d(centerX + half, baselineY), 0, 0, 0);
            outer.AddVertexAt(5, new Point2d(centerX + half, baselineY + fold), 0, 0, 0);
            AddEntity(model, transaction, outer);

            double thickness = 1.2 * scale;
            var inner = new Polyline(6) { Layer = RebarLayer };
            inner.AddVertexAt(0, new Point2d(centerX - half + thickness, baselineY + fold), 0, 0, 0);
            inner.AddVertexAt(1, new Point2d(centerX - half + thickness, baselineY + thickness), 0, 0, 0);
            inner.AddVertexAt(2, new Point2d(centerX - halfGap + thickness, baselineY + thickness), 1.0, 0, 0);
            inner.AddVertexAt(3, new Point2d(centerX + halfGap - thickness, baselineY + thickness), 0, 0, 0);
            inner.AddVertexAt(4, new Point2d(centerX + half - thickness, baselineY + thickness), 0, 0, 0);
            inner.AddVertexAt(5, new Point2d(centerX + half - thickness, baselineY + fold), 0, 0, 0);
            AddEntity(model, transaction, inner);
        }

        private static void DrawHoneycomb(Transaction transaction, BlockTableRecord model,
            double x, double y, double width, double height)
        {
            const double radius = 2.4;
            double dx = radius * 1.55;
            double dy = radius * 1.75;
            int row = 0;
            for (double cy = y + radius; cy <= y + height - radius; cy += dy, row++)
            {
                double offset = row % 2 == 0 ? 0 : dx / 2;
                for (double cx = x + radius + offset; cx <= x + width - radius; cx += dx)
                {
                    AddHexagon(model, transaction, cx, cy, radius, FillLayer);
                }
            }
        }

        private static void DrawConcreteTexture(Transaction transaction, BlockTableRecord model,
            double x, double y, double width, double height, int count, int seed)
        {
            var random = new Random(seed);
            for (int index = 0; index < count; index++)
            {
                double cx = x + 5 + random.NextDouble() * (width - 10);
                double cy = y + 5 + random.NextDouble() * (height - 10);
                double size = 0.8 + random.NextDouble() * 1.2;
                var triangle = new Polyline(3) { Layer = FillLayer, Closed = true };
                triangle.AddVertexAt(0, new Point2d(cx, cy + size), 0, 0, 0);
                triangle.AddVertexAt(1, new Point2d(cx - size, cy - size), 0, 0, 0);
                triangle.AddVertexAt(2, new Point2d(cx + size, cy - size), 0, 0, 0);
                AddEntity(model, transaction, triangle);
            }
        }

        private static void AddHexagon(BlockTableRecord model, Transaction transaction,
            double cx, double cy, double radius, string layer)
        {
            var polyline = new Polyline(6) { Layer = layer, Closed = true };
            for (int index = 0; index < 6; index++)
            {
                double angle = Math.PI / 3.0 * index;
                polyline.AddVertexAt(index, new Point2d(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle)), 0, 0, 0);
            }
            AddEntity(model, transaction, polyline);
        }

        private static void AddRectangle(BlockTableRecord model, Transaction transaction,
            double x, double y, double width, double height, string layer)
        {
            var polyline = new Polyline(4) { Layer = layer, Closed = true };
            polyline.AddVertexAt(0, new Point2d(x, y), 0, 0, 0);
            polyline.AddVertexAt(1, new Point2d(x + width, y), 0, 0, 0);
            polyline.AddVertexAt(2, new Point2d(x + width, y + height), 0, 0, 0);
            polyline.AddVertexAt(3, new Point2d(x, y + height), 0, 0, 0);
            AddEntity(model, transaction, polyline);
        }

        private static void AddLine(BlockTableRecord model, Transaction transaction,
            double x1, double y1, double x2, double y2, string layer)
        {
            AddEntity(model, transaction, new Line(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0)) { Layer = layer });
        }

        private static void AddDimension(BlockTableRecord model, Transaction transaction,
            double x1, double y1, double x2, double y2, double dx, double dy, string text, ObjectId style)
        {
            var dimension = new AlignedDimension(
                new Point3d(x1, y1, 0),
                new Point3d(x2, y2, 0),
                new Point3d(dx, dy, 0),
                text,
                style)
            {
                Layer = DimensionLayer,
                Dimscale = 1.0,
                Dimtxt = 2.5,
                Dimasz = 2.0,
                Dimgap = 0.6,
                Dimexo = 0.6,
                Dimexe = 1.0
            };
            AddEntity(model, transaction, dimension);
        }

        private static void AddMText(Database database, BlockTableRecord model, Transaction transaction,
            double x, double y, string text, double height, string layer,
            AttachmentPoint attachment, double width)
        {
            TextStyleTable styles = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
            var entity = new MText
            {
                Location = new Point3d(x, y, 0),
                Contents = text,
                TextHeight = height,
                Layer = layer,
                TextStyleId = styles[TextStyleName],
                Attachment = attachment
            };
            if (width > 0)
            {
                entity.Width = width;
            }
            AddEntity(model, transaction, entity);
        }

        private static void AddEntity(BlockTableRecord model, Transaction transaction, Entity entity)
        {
            model.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
        }

        private static ObjectId GetDimensionStyle(Database database, Transaction transaction)
        {
            DimStyleTable styles = (DimStyleTable)transaction.GetObject(database.DimStyleTableId, OpenMode.ForRead);
            return styles.Has(DimensionStyleName) ? styles[DimensionStyleName] : database.Dimstyle;
        }

        private static void RequireResources(Database database, Transaction transaction)
        {
            LayerTable layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            foreach (string name in new[] { OutlineLayer, CoarseLayer, FineLayer, FillLayer, TextLayer, DimensionLayer, FrameLayer, RebarLayer, RebarTableLayer })
            {
                if (!layers.Has(name))
                {
                    throw new InvalidOperationException("Company hydraulic RC DWT is missing layer: " + name);
                }
            }
            TextStyleTable textStyles = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
            if (!textStyles.Has(TextStyleName))
            {
                throw new InvalidOperationException("Company hydraulic RC DWT is missing text style: " + TextStyleName);
            }
            DimStyleTable dimStyles = (DimStyleTable)transaction.GetObject(database.DimStyleTableId, OpenMode.ForRead);
            if (!dimStyles.Has(DimensionStyleName))
            {
                throw new InvalidOperationException("Company hydraulic RC DWT is missing dimension style: " + DimensionStyleName);
            }
        }

        private static void SetSummary(Database database, string requestId)
        {
            var builder = new DatabaseSummaryInfoBuilder(database.SummaryInfo)
            {
                Title = "紫铜片止水大样图（伸缩缝处）",
                Subject = "既有 PNG 图像的可编辑 CAD 工程图复绘",
                Author = "Codex / AutoCAD CAD-MCP"
            };
            IDictionary custom = builder.CustomPropertyTable;
            custom["SourceImage"] = SourceImage;
            custom["SourceImageSha256"] = SourceImageSha256;
            custom["RequestId"] = requestId;
            custom["DrawingStatus"] = "按源图语义化CAD复绘；未改变图示尺寸与材料说明";
            database.SummaryInfo = builder.ToDatabaseSummaryInfo();
        }

        private static void VerifyDrawingDatabase(string path)
        {
            using (var database = new Database(false, true))
            {
                database.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null);
                database.CloseInput(true);
                if (database.Insunits != UnitsValue.Millimeters)
                {
                    throw new InvalidOperationException("DWG INSUNITS is not millimeters.");
                }
            }
        }

        private static void CloseActiveOutputDocument(string path)
        {
            Document active = AcApplication.DocumentManager.MdiActiveDocument;
            if (active == null)
            {
                return;
            }
            if (!PathEquals(active.Name, path))
            {
                throw new InvalidOperationException("Another AutoCAD document is active; refusing to close it during copper-waterstop verification: " + active.Name);
            }
            active.CloseAndDiscard();
        }

        private static Dictionary<string, object> InspectDrawing(string path)
        {
            using (var database = new Database(false, true))
            {
                database.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null);
                database.CloseInput(true);
                int entityCount = 0;
                int mtextCount = 0;
                int dimensionCount = 0;
                int lineworkCount = 0;
                int solidCount = 0;
                int rasterCount = 0;
                var texts = new List<string>();
                var usedLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Extents3d? extents = null;
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    BlockTable table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord model = (BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in model)
                    {
                        Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (entity == null)
                        {
                            continue;
                        }
                        entityCount++;
                        usedLayers.Add(entity.Layer);
                        if (entity is MText)
                        {
                            mtextCount++;
                            texts.Add(((MText)entity).Text);
                        }
                        if (entity is Dimension)
                        {
                            dimensionCount++;
                        }
                        if (entity is Line || entity is Polyline)
                        {
                            lineworkCount++;
                        }
                        if (entity is Solid)
                        {
                            solidCount++;
                        }
                        if (entity is RasterImage)
                        {
                            rasterCount++;
                        }
                        try
                        {
                            Extents3d candidate = entity.GeometricExtents;
                            if (!extents.HasValue)
                            {
                                extents = candidate;
                            }
                            else
                            {
                                Extents3d combined = extents.Value;
                                combined.AddExtents(candidate);
                                extents = combined;
                            }
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception)
                        {
                        }
                    }
                    transaction.Commit();
                }
                string allText = string.Join("|", texts);
                bool criticalTextPresent = allText.Contains("紫铜片止水大样图") &&
                    allText.Contains("剖面图") &&
                    allText.Contains("铜片构造详图") &&
                    allText.Contains("T2紫铜") &&
                    allText.Contains("δ=1.2mm") &&
                    allText.Contains("伸缩缝宽20") &&
                    allText.Contains("闭孔泡沫板") &&
                    allText.Contains("临水面") &&
                    allText.Contains("新浇筑砼");
                bool extentsOk = extents.HasValue &&
                    extents.Value.MinPoint.X >= -1.0 && extents.Value.MinPoint.Y >= -1.0 &&
                    extents.Value.MinPoint.X <= 0.1 && extents.Value.MinPoint.Y <= 0.1 &&
                    extents.Value.MaxPoint.X >= 419.9 && extents.Value.MaxPoint.Y >= 296.9 &&
                    extents.Value.MaxPoint.X <= 421.0 && extents.Value.MaxPoint.Y <= 298.0;
                bool layersOk = new[] { OutlineLayer, FineLayer, FillLayer, TextLayer, DimensionLayer, FrameLayer, RebarLayer, RebarTableLayer }
                    .All(layer => usedLayers.Contains(layer));
                return new Dictionary<string, object>
                {
                    { "entity_count", entityCount },
                    { "mtext_count", mtextCount },
                    { "dimension_count", dimensionCount },
                    { "linework_count", lineworkCount },
                    { "solid_count", solidCount },
                    { "raster_count", rasterCount },
                    { "critical_text_present", criticalTextPresent },
                    { "extents_ok", extentsOk },
                    { "layers_ok", layersOk },
                    { "used_layers", usedLayers.OrderBy(item => item).ToArray() },
                    { "extents_min", extents.HasValue ? new[] { extents.Value.MinPoint.X, extents.Value.MinPoint.Y } : new double[0] },
                    { "extents_max", extents.HasValue ? new[] { extents.Value.MaxPoint.X, extents.Value.MaxPoint.Y } : new double[0] },
                    { "insunits", database.Insunits.ToString() },
                    { "file_size", new FileInfo(path).Length }
                };
            }
        }

        private static void EnsurePreviewPdf(string dwgPath)
        {
            string pdfPath = Path.Combine(OutputDirectory, PreviewPdfFileName);
            if (File.Exists(pdfPath) && new FileInfo(pdfPath).Length > 0)
            {
                return;
            }
            Document document = null;
            try
            {
                document = AcApplication.DocumentManager.Open(dwgPath, false);
                using (document.LockDocument())
                {
                    Database database = document.Database;
                    LayoutManager.Current.CurrentLayout = "Model";
                    ObjectId layoutId;
                    using (Transaction transaction = database.TransactionManager.StartOpenCloseTransaction())
                    {
                        DBDictionary layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
                        layoutId = layouts.GetAt("Model");
                        transaction.Commit();
                    }
                    object previousBackgroundPlot = AcApplication.GetSystemVariable("BACKGROUNDPLOT");
                    try
                    {
                        AcApplication.SetSystemVariable("BACKGROUNDPLOT", 0);
                        PlotModel(database, layoutId, pdfPath);
                    }
                    finally
                    {
                        AcApplication.SetSystemVariable("BACKGROUNDPLOT", previousBackgroundPlot);
                    }
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
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while ((!File.Exists(pdfPath) || new FileInfo(pdfPath).Length == 0) && DateTime.UtcNow < deadline)
            {
                System.Threading.Thread.Sleep(100);
            }
            if (!File.Exists(pdfPath) || new FileInfo(pdfPath).Length == 0)
            {
                throw new InvalidOperationException("AutoCAD did not create the preview PDF: " + pdfPath);
            }
        }

        private static void PlotModel(Database database, ObjectId layoutId, string pdfPath)
        {
            using (Transaction transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                Layout layout = (Layout)transaction.GetObject(layoutId, OpenMode.ForRead);
                using (var settings = new PlotSettings(true))
                {
                    settings.CopyFrom(layout);
                    PlotSettingsValidator validator = PlotSettingsValidator.Current;
                    validator.SetPlotConfigurationName(settings, "DWG To PDF.pc3", null);
                    validator.RefreshLists(settings);
                    string media = validator.GetCanonicalMediaNameList(settings)
                        .Cast<string>()
                        .FirstOrDefault(name => name.IndexOf("A3", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            name.IndexOf("420.00_x_297.00", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (string.IsNullOrWhiteSpace(media))
                    {
                        throw new InvalidOperationException("DWG To PDF.pc3 does not expose an A3 420x297 mm media.");
                    }
                    validator.SetCanonicalMediaName(settings, media);
                    validator.SetPlotPaperUnits(settings, PlotPaperUnit.Millimeters);
                    validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Extents);
                    validator.SetUseStandardScale(settings, true);
                    validator.SetStdScaleType(settings, StdScaleType.ScaleToFit);
                    validator.SetPlotCentered(settings, true);
                    validator.SetPlotRotation(settings, PlotRotation.Degrees000);
                    try
                    {
                        validator.SetCurrentStyleSheet(settings, "monochrome.ctb");
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                    }

                    var info = new PlotInfo { Layout = layoutId, OverrideSettings = settings };
                    var infoValidator = new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled };
                    infoValidator.Validate(info);
                    using (PlotEngine engine = PlotFactory.CreatePublishEngine())
                    using (var progress = new PlotProgressDialog(false, 1, true))
                    {
                        progress.OnBeginPlot();
                        progress.IsVisible = false;
                        engine.BeginPlot(progress, null);
                        engine.BeginDocument(info, database.OriginalFileName, null, 1, true, pdfPath);
                        var pageInfo = new PlotPageInfo();
                        engine.BeginPage(pageInfo, info, true, null);
                        engine.BeginGenerateGraphics(null);
                        engine.EndGenerateGraphics(null);
                        engine.EndPage(null);
                        engine.EndDocument(null);
                        engine.EndPlot(null);
                        progress.OnEndPlot();
                    }
                }
                transaction.Commit();
            }
        }

        private static string Sha256(string path)
        {
            using (SHA256 algorithm = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
            }
        }

        private static bool ValidRequestId(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value.All(character =>
                (character >= 'A' && character <= 'Z') ||
                (character >= 'a' && character <= 'z') ||
                (character >= '0' && character <= '9') ||
                "._:-".IndexOf(character) >= 0);
        }

        private static bool PathEquals(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }
            return string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureChildPath(string child, string parent)
        {
            string childFull = Path.GetFullPath(child);
            string parentFull = Path.GetFullPath(parent).TrimEnd('\\') + "\\";
            if (!childFull.StartsWith(parentFull, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Stage path escaped the output directory.");
            }
        }
    }
}
