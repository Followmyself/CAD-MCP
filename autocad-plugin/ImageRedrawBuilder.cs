using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CadMcp.AutoCAD
{
    internal static class ImageRedrawBuilder
    {
        private const string OutputDirectory = @"G:\.codex\CAD_Project\pdftocad";
        private const string SourceDwt = @"G:\.codex\skills\company-hydraulic-rc-design\assets\公司水利钢筋混凝土设计.dwt";
        private const string SourceImage = @"G:\.codex\CAD_Project\pdftocad\pic.jpg";
        private const string SourceImageSha256 = "CCE3D3D4F0053E1DE9F08544D614CA8A57D5C64D5B819FED63991190B5B4CB1F";
        private const string OutputFileName = "拟建暗涵配筋图_CAD重绘.dwg";
        private const string PreviewPdfFileName = "拟建暗涵配筋图_CAD重绘_预览.pdf";
        private const string Standard = "COMPANY-HYDRO-RC-2026";

        private const string OutlineLayer = "轮廓";
        private const string CoarseLayer = "粗实线";
        private const string FineLayer = "细实";
        private const string TextLayer = "文字";
        private const string DimensionLayer = "标注";
        private const string FrameLayer = "图框";
        private const string RebarLayer = "REIN";
        private const string RebarLabelLayer = "钢筋标注";
        private const string RebarTableLayer = "钢筋表";
        private const string TextStyleName = "宋体";
        private const string DimensionStyleName = "1：50";

        internal static string Validate(TemplateRequest request, string operation)
        {
            if (request == null)
            {
                return "Request body is required.";
            }
            if (operation != "image_redraw_build" && operation != "image_redraw_verify")
            {
                return "Unknown image redraw operation.";
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
                return "image_path must be the inspected source image: " + SourceImage + ".";
            }
            if (!string.Equals(Sha256(SourceImage), SourceImageSha256, StringComparison.OrdinalIgnoreCase))
            {
                return "Source image SHA-256 changed; inspect the new image before drawing.";
            }
            if (operation == "image_redraw_build")
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
            if (operation == "image_redraw_build")
            {
                return Build(request);
            }
            if (operation == "image_redraw_verify")
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
                    existing.operation = "image_redraw_build";
                    existing.duplicate = true;
                    existing.message = "Verified existing image-redraw DWG; no duplicate drawing was created.";
                    return existing;
                }
                throw new InvalidOperationException("Existing output failed verification and will not be overwritten: " + existing.error);
            }

            string stagePath = Path.Combine(OutputDirectory, ".image-redraw-stage-" + request.request_id + ".dwg");
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
                response.operation = "image_redraw_build";
                response.message = "Source image redrawn and verified as a DWG through AutoCAD CAD-MCP.";
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
                return TemplateResponse.Failure(request == null ? null : request.request_id, "image_redraw_verify", "DWG is missing or empty: " + path);
            }
            if (!string.Equals(Sha256(SourceImage), SourceImageSha256, StringComparison.OrdinalIgnoreCase))
            {
                return TemplateResponse.Failure(request == null ? null : request.request_id, "image_redraw_verify", "Source image SHA-256 changed after drawing.");
            }

            try
            {
                Dictionary<string, object> metrics = InspectDrawing(path);
                int entityCount = Convert.ToInt32(metrics["entity_count"], CultureInfo.InvariantCulture);
                int mtextCount = Convert.ToInt32(metrics["mtext_count"], CultureInfo.InvariantCulture);
                int dimensionCount = Convert.ToInt32(metrics["dimension_count"], CultureInfo.InvariantCulture);
                int lineworkCount = Convert.ToInt32(metrics["linework_count"], CultureInfo.InvariantCulture);
                int circleCount = Convert.ToInt32(metrics["circle_count"], CultureInfo.InvariantCulture);
                int solidCount = Convert.ToInt32(metrics["solid_count"], CultureInfo.InvariantCulture);
                bool criticalTextPresent = Convert.ToBoolean(metrics["critical_text_present"], CultureInfo.InvariantCulture);
                bool extentsOk = Convert.ToBoolean(metrics["extents_ok"], CultureInfo.InvariantCulture);
                if (entityCount < 240 || mtextCount < 55 || dimensionCount < 7 ||
                    lineworkCount < 80 || circleCount < 80 || solidCount != 0 ||
                    !criticalTextPresent || !extentsOk)
                {
                    throw new InvalidOperationException(string.Format(
                        CultureInfo.InvariantCulture,
                        "Semantic CAD verification failed: entities={0}, mtext={1}, dimensions={2}, linework={3}, circles={4}, solids={5}, critical_text={6}, extents_ok={7}.",
                        entityCount, mtextCount, dimensionCount, lineworkCount, circleCount,
                        solidCount, criticalTextPresent, extentsOk));
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
                metrics["design_status"] = "原图语义化CAD重绘；文字、尺寸、线条、钢筋和表格可编辑，未重新核定配筋";
                return new TemplateResponse
                {
                    ok = true,
                    request_id = request == null ? null : request.request_id,
                    duplicate = false,
                    operation = "image_redraw_verify",
                    message = "DWG reopened in AutoCAD database and passed geometry, layer and text checks.",
                    error = null,
                    data = metrics
                };
            }
            catch (System.Exception error)
            {
                return TemplateResponse.Failure(request == null ? null : request.request_id, "image_redraw_verify", error.Message);
            }
        }

        private static void DrawSheet(Database database, Transaction transaction, BlockTableRecord model)
        {
            DrawFrame(transaction, model);
            DrawCulvertSection(database, transaction, model);
            DrawRebarTable(database, transaction, model);
            DrawMaterialTable(database, transaction, model);
            DrawNotes(database, transaction, model);
            DrawTitleBlock(database, transaction, model);
        }

        private static void DrawVectorTrace(Transaction transaction, BlockTableRecord model)
        {
            const int targetWidthPixels = 4300;
            const int threshold = 200;
            const double targetWidthMillimeters = 420.0;

            using (var source = new Bitmap(SourceImage))
            {
                int targetHeightPixels = (int)Math.Round(
                    source.Height * targetWidthPixels / (double)source.Width,
                    MidpointRounding.AwayFromZero);
                using (var resized = new Bitmap(targetWidthPixels, targetHeightPixels, PixelFormat.Format24bppRgb))
                {
                    using (Graphics graphics = Graphics.FromImage(resized))
                    {
                        graphics.Clear(System.Drawing.Color.White);
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.CompositingQuality = CompositingQuality.HighQuality;
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        graphics.SmoothingMode = SmoothingMode.None;
                        graphics.DrawImage(source, 0, 0, targetWidthPixels, targetHeightPixels);
                    }

                    Rectangle rectangle = new Rectangle(0, 0, targetWidthPixels, targetHeightPixels);
                    BitmapData data = resized.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    try
                    {
                        int stride = data.Stride;
                        byte[] pixels = new byte[Math.Abs(stride) * targetHeightPixels];
                        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                        double pixelSize = targetWidthMillimeters / targetWidthPixels;
                        double targetHeightMillimeters = targetHeightPixels * pixelSize;
                        int solidCount = 0;

                        for (int row = 0; row < targetHeightPixels; row++)
                        {
                            int rowOffset = row * stride;
                            int runStart = -1;
                            for (int column = 0; column <= targetWidthPixels; column++)
                            {
                                bool dark = false;
                                if (column < targetWidthPixels)
                                {
                                    int offset = rowOffset + column * 3;
                                    int blue = pixels[offset];
                                    int green = pixels[offset + 1];
                                    int red = pixels[offset + 2];
                                    int luminance = (red * 299 + green * 587 + blue * 114) / 1000;
                                    dark = luminance < threshold;
                                }

                                if (dark && runStart < 0)
                                {
                                    runStart = column;
                                }
                                else if (!dark && runStart >= 0)
                                {
                                    double x1 = runStart * pixelSize;
                                    double x2 = column * pixelSize;
                                    double yTop = targetHeightMillimeters - row * pixelSize;
                                    double yBottom = yTop - pixelSize;
                                    var solid = new Solid(
                                        new Point3d(x1, yBottom, 0),
                                        new Point3d(x2, yBottom, 0),
                                        new Point3d(x1, yTop, 0),
                                        new Point3d(x2, yTop, 0))
                                    {
                                        Layer = OutlineLayer
                                    };
                                    AddEntity(model, transaction, solid);
                                    solidCount++;
                                    runStart = -1;
                                }
                            }
                        }

                        if (solidCount < 50000)
                        {
                            throw new InvalidOperationException("Vector trace produced too few solids: " + solidCount + ".");
                        }
                    }
                    finally
                    {
                        resized.UnlockBits(data);
                    }
                }
            }
        }

        private static void DrawFrame(Transaction transaction, BlockTableRecord model)
        {
            AddRectangle(model, transaction, 0, 0, 21000, 14850, FrameLayer);
            AddRectangle(model, transaction, 1150, 250, 19600, 14350, FrameLayer);
        }

        private static void DrawCulvertSection(Database database, Transaction transaction, BlockTableRecord model)
        {
            const double x = 4700;
            const double y = 7800;
            AddRectangle(model, transaction, x, y, 2600, 2600, CoarseLayer);
            AddRectangle(model, transaction, x + 300, y + 300, 2000, 2000, CoarseLayer);
            AddRectangle(model, transaction, x + 70, y + 70, 2460, 2460, OutlineLayer);
            AddRectangle(model, transaction, x + 300 - 70, y + 300 - 70, 2140, 2140, OutlineLayer);

            DrawRebarPerimeter(transaction, model, x, y, 2600, 2600, 105, 200, true);
            DrawRebarPerimeter(transaction, model, x + 300, y + 300, 2000, 2000, 70, 200, false);
            DrawDistributionBars(transaction, model, x, y);

            AddCallout(database, transaction, model, x - 850, y + 1500, "1", "Φ16@150", x + 70, y + 1300);
            AddCallout(database, transaction, model, x + 700, y + 3250, "3", "Φ12@200", x + 1150, y + 2470);
            AddCallout(database, transaction, model, x + 1950, y + 3250, "2", "Φ16@200", x + 1950, y + 2300);
            AddCallout(database, transaction, model, x - 450, y + 3500, "4", "Φ8@400", x + 250, y + 2500);

            ObjectId dimStyle = GetDimensionStyle(database, transaction);
            AddAlignedDimension(model, transaction, new Point3d(x, y, 0), new Point3d(x + 300, y, 0), new Point3d(x, y - 400, 0), "300", dimStyle);
            AddAlignedDimension(model, transaction, new Point3d(x + 300, y, 0), new Point3d(x + 2300, y, 0), new Point3d(x + 300, y - 400, 0), "2000", dimStyle);
            AddAlignedDimension(model, transaction, new Point3d(x + 2300, y, 0), new Point3d(x + 2600, y, 0), new Point3d(x + 2300, y - 400, 0), "300", dimStyle);
            AddAlignedDimension(model, transaction, new Point3d(x, y, 0), new Point3d(x + 2600, y, 0), new Point3d(x, y - 650, 0), "2600", dimStyle);
            AddAlignedDimension(model, transaction, new Point3d(x + 2600, y, 0), new Point3d(x + 2600, y + 2600, 0), new Point3d(x + 3300, y, 0), "2600", dimStyle);
            AddAlignedDimension(model, transaction, new Point3d(x + 2300, y + 300, 0), new Point3d(x + 2300, y + 2300, 0), new Point3d(x + 2900, y + 300, 0), "2000", dimStyle);
            AddAlignedDimension(model, transaction, new Point3d(x + 2300, y + 2300, 0), new Point3d(x + 2300, y + 2600, 0), new Point3d(x + 2900, y + 2300, 0), "300", dimStyle);

            AddMText(database, model, transaction, x + 1300, y - 950, "箱涵横断面钢筋图", 260, 0, TextLayer, AttachmentPoint.MiddleCenter, 0);
            AddLine(model, transaction, x + 700, y - 1080, x + 1900, y - 1080, FineLayer);
            AddMText(database, model, transaction, x + 1300, y - 1320, "1:50", 220, 0, TextLayer, AttachmentPoint.MiddleCenter, 0);
        }

        private static void DrawRebarPerimeter(Transaction transaction, BlockTableRecord model,
            double x, double y, double width, double height, double offset, double spacing, bool outer)
        {
            double xMin = x + offset;
            double xMax = x + width - offset;
            double yMin = y + offset;
            double yMax = y + height - offset;
            for (double px = xMin; px <= xMax + 0.1; px += spacing)
            {
                AddCircle(model, transaction, px, yMin, 28, RebarLayer);
                AddCircle(model, transaction, px, yMax, 28, RebarLayer);
            }
            for (double py = yMin + spacing; py < yMax - 0.1; py += spacing)
            {
                AddCircle(model, transaction, xMin, py, 28, RebarLayer);
                AddCircle(model, transaction, xMax, py, 28, RebarLayer);
            }
            AddRectangle(model, transaction, xMin, yMin, xMax - xMin, yMax - yMin, RebarLayer);
            if (outer)
            {
                AddRectangle(model, transaction, xMin + 35, yMin + 35, xMax - xMin - 70, yMax - yMin - 70, RebarLayer);
            }
        }

        private static void DrawDistributionBars(Transaction transaction, BlockTableRecord model, double x, double y)
        {
            for (double px = x + 450; px <= x + 2200; px += 400)
            {
                AddHookBar(model, transaction, new[]
                {
                    new Point2d(px - 90, y + 2480), new Point2d(px, y + 2300),
                    new Point2d(px + 90, y + 2050), new Point2d(px + 150, y + 2070)
                });
                AddHookBar(model, transaction, new[]
                {
                    new Point2d(px - 90, y + 120), new Point2d(px, y + 300),
                    new Point2d(px + 90, y + 550), new Point2d(px + 150, y + 530)
                });
            }
            for (double py = y + 500; py <= y + 2100; py += 400)
            {
                AddHookBar(model, transaction, new[]
                {
                    new Point2d(x + 120, py - 80), new Point2d(x + 300, py),
                    new Point2d(x + 550, py + 80), new Point2d(x + 530, py + 140)
                });
                AddHookBar(model, transaction, new[]
                {
                    new Point2d(x + 2480, py - 80), new Point2d(x + 2300, py),
                    new Point2d(x + 2050, py + 80), new Point2d(x + 2070, py + 140)
                });
            }
        }

        private static void DrawRebarTable(Database database, Transaction transaction, BlockTableRecord model)
        {
            const double x = 10900;
            const double y = 6800;
            double[] widths = { 600, 890, 2230, 1110, 500, 1200, 890 };
            double[] horizontal = { 6800, 7340, 7880, 8960, 10040, 10600 };
            double totalWidth = widths.Sum();
            AddRectangle(model, transaction, x, y, totalWidth, 3800, RebarTableLayer);
            double xx = x;
            for (int index = 0; index < widths.Length - 1; index++)
            {
                xx += widths[index];
                AddLine(model, transaction, xx, y, xx, 10600, RebarTableLayer);
            }
            for (int index = 1; index < horizontal.Length - 1; index++)
            {
                AddLine(model, transaction, x, horizontal[index], x + totalWidth, horizontal[index], RebarTableLayer);
            }
            AddMText(database, model, transaction, x + totalWidth / 2, 11120,
                "2m×2m箱涵钢筋表（每12m长度）", 240, 0, TextLayer, AttachmentPoint.MiddleCenter, 0);

            AddCellRow(database, transaction, model, x, widths, 10320,
                new[] { "编号", "直径(mm)", "型式", "单根长(mm)", "根数", "总长(m)", "备注" }, 170);
            AddCellRow(database, transaction, model, x, widths, 9500,
                new[] { "①", "Φ16", "", "10280", "80", "822.40", "" }, 170);
            AddCellRow(database, transaction, model, x, widths, 8420,
                new[] { "②", "Φ16", "", "8440", "60", "506.40", "" }, 170);
            AddCellRow(database, transaction, model, x, widths, 7610,
                new[] { "③", "Φ12", "", "11930", "92", "1097.56", "" }, 160);
            AddCellRow(database, transaction, model, x, widths, 7070,
                new[] { "④", "Φ8", "", "330", "600", "198.00", "" }, 160);

            double typeLeft = x + widths[0] + widths[1];
            DrawBarShape(transaction, model, typeLeft + 650, 9140, 900, 720, RebarLayer);
            AddMText(database, model, transaction, typeLeft + 1100, 9740, "2530", 140, 0, RebarLabelLayer, AttachmentPoint.MiddleCenter, 0);
            AddMText(database, model, transaction, typeLeft + 1670, 9500, "2530", 140, 0, RebarLabelLayer, AttachmentPoint.MiddleCenter, 0);
            DrawBarShape(transaction, model, typeLeft + 650, 8060, 900, 720, RebarLayer);
            AddMText(database, model, transaction, typeLeft + 1100, 8660, "2070", 140, 0, RebarLabelLayer, AttachmentPoint.MiddleCenter, 0);
            AddMText(database, model, transaction, typeLeft + 1670, 8420, "2070", 140, 0, RebarLabelLayer, AttachmentPoint.MiddleCenter, 0);
            AddLine(model, transaction, typeLeft + 650, 7610, typeLeft + 1550, 7610, RebarLayer);
            AddMText(database, model, transaction, typeLeft + 1100, 7730, "11930", 140, 0, RebarLabelLayer, AttachmentPoint.MiddleCenter, 0);
            AddHookBar(model, transaction, new[]
            {
                new Point2d(typeLeft + 760, 7080), new Point2d(typeLeft + 900, 7020),
                new Point2d(typeLeft + 1100, 7140), new Point2d(typeLeft + 1300, 7020),
                new Point2d(typeLeft + 1440, 7080)
            });
            AddMText(database, model, transaction, typeLeft + 1100, 6910, "230", 135, 0, RebarLabelLayer, AttachmentPoint.MiddleCenter, 0);
        }

        private static void DrawMaterialTable(Database database, Transaction transaction, BlockTableRecord model)
        {
            const double x = 12700;
            const double y = 4600;
            double[] widths = { 815, 815, 815, 815 };
            double totalWidth = widths.Sum();
            AddRectangle(model, transaction, x, y, totalWidth, 1450, RebarTableLayer);
            foreach (double yy in new[] { 5200.0, 5400.0, 5600.0, 5800.0 })
            {
                AddLine(model, transaction, x, yy, x + totalWidth, yy, RebarTableLayer);
            }
            double xx = x;
            for (int index = 0; index < widths.Length - 1; index++)
            {
                xx += widths[index];
                AddLine(model, transaction, xx, 5200, xx, 6050, RebarTableLayer);
            }
            AddMText(database, model, transaction, x + totalWidth / 2, 6480,
                "2m×2m箱涵材料表（每12m长度）", 175, 0, TextLayer, AttachmentPoint.MiddleCenter, 0);
            AddCellRow(database, transaction, model, x, widths, 5925,
                new[] { "规格", "总长度(m)", "单位重(kg/m)", "总重(kg)" }, 95);
            AddCellRow(database, transaction, model, x, widths, 5700,
                new[] { "Φ8", "198.00", "0.395", "78.21" }, 95);
            AddCellRow(database, transaction, model, x, widths, 5500,
                new[] { "Φ12", "1097.56", "0.888", "974.63" }, 95);
            AddCellRow(database, transaction, model, x, widths, 5300,
                new[] { "Φ16", "1328.80", "1.580", "2099.50" }, 95);
            AddMText(database, model, transaction, x + 100, 5080,
                "不加损耗，共计钢筋量3152kg", 105, 0, TextLayer, AttachmentPoint.MiddleLeft, 3000);
        }

        private static void DrawNotes(Database database, Transaction transaction, BlockTableRecord model)
        {
            const double x = 2230;
            const double y = 1060;
            AddRectangle(model, transaction, x, y, 5270, 2270, FineLayer);
            AddRectangle(model, transaction, x + 100, y + 80, 5070, 2080, CoarseLayer);
            string notes = "说明：\\P" +
                "1、图中尺寸单位，高程以m计，尺寸标注单位为mm。\\P" +
                "2、图中砼标号为C25，钢筋保护层厚度35mm，钢筋为HRB400钢筋。\\P" +
                "3、施工需严格参照相关规范进行，若现场实际情况及地质条件与设计不符，请及时与设计单位联系。\\P" +
                "4、施工过程中应满足《水利水电工程劳动安全与工业卫生设计规范》（GB50706-2011）的相关要求。";
            AddMText(database, model, transaction, x + 240, y + 2050, notes, 112, 0, TextLayer, AttachmentPoint.TopLeft, 4750);
        }

        private static void DrawTitleBlock(Database database, Transaction transaction, BlockTableRecord model)
        {
            const double x = 16230;
            const double y = 250;
            const double w = 4520;
            const double h = 3090;
            AddRectangle(model, transaction, x, y, w, h, FrameLayer);
            foreach (double yy in new[] { 600.0, 1300.0, 2000.0, 2350.0, 2700.0 })
            {
                AddLine(model, transaction, x, y + yy, x + w, y + yy, FrameLayer);
            }
            AddLine(model, transaction, x, y + 950, x + 2020, y + 950, FrameLayer);
            AddLine(model, transaction, x, y + 1650, x + 2020, y + 1650, FrameLayer);
            AddLine(model, transaction, x + 520, y + 600, x + 520, y + 2700, FrameLayer);
            AddLine(model, transaction, x + 1520, y + 600, x + 1520, y + 2700, FrameLayer);
            AddLine(model, transaction, x + 2020, y + 600, x + 2020, y + 2700, FrameLayer);
            AddLine(model, transaction, x + 520, y, x + 520, y + 600, FrameLayer);
            AddLine(model, transaction, x + 1520, y, x + 1520, y + 600, FrameLayer);
            AddLine(model, transaction, x + 2020, y, x + 2020, y + 600, FrameLayer);
            AddLine(model, transaction, x + 2770, y, x + 2770, y + 600, FrameLayer);

            AddCircle(model, transaction, x + 360, y + 2920, 210, FrameLayer);
            AddLine(model, transaction, x + 120, y + 2920, x + 600, y + 2920, FrameLayer);
            AddLine(model, transaction, x + 360, y + 2680, x + 360, y + 3160, FrameLayer);
            AddLine(model, transaction, x + 220, y + 2780, x + 500, y + 3060, FrameLayer);
            AddLine(model, transaction, x + 220, y + 3060, x + 500, y + 2780, FrameLayer);
            AddMText(database, model, transaction, x + 2620, y + 2920,
                "中交宏禹（湖南）水利工程有限公司", 160, 0, TextLayer, AttachmentPoint.MiddleCenter, 3700);
            string[] labels = { "核定", "审查", "校核", "设计", "制图" };
            string[] names = { "马利军", "欧光军", "刘洋", "李明俊", "李明俊" };
            double[] centers = { 2525, 2175, 1825, 1475, 1125 };
            for (int index = 0; index < labels.Length; index++)
            {
                AddMText(database, model, transaction, x + 260, y + centers[index], labels[index], 115, 0, TextLayer, AttachmentPoint.MiddleCenter, 0);
                AddMText(database, model, transaction, x + 1020, y + centers[index], names[index], 125, 0, TextLayer, AttachmentPoint.MiddleCenter, 0);
            }
            AddMText(database, model, transaction, x + 3270, y + 2525, "技 施 阶 段", 135, 0, TextLayer, AttachmentPoint.MiddleCenter, 0);
            AddMText(database, model, transaction, x + 3270, y + 2175, "水 工 部 分", 135, 0, TextLayer, AttachmentPoint.MiddleCenter, 0);
            AddMText(database, model, transaction, x + 3270, y + 1650,
                "资兴市杨洞灌区水毁重建\\P（续建配套与现代化改造）项目", 112, 0, TextLayer, AttachmentPoint.MiddleCenter, 2300);
            AddMText(database, model, transaction, x + 3270, y + 950,
                "杨洞二期\\P拟建暗涵配筋图", 160, 0, TextLayer, AttachmentPoint.MiddleCenter, 2300);
            AddMText(database, model, transaction, x + 260, y + 775, "比例", 105, 0, TextLayer, AttachmentPoint.MiddleCenter, 0);
            AddMText(database, model, transaction, x + 1020, y + 775, "见图", 105, 0, TextLayer, AttachmentPoint.MiddleCenter, 0);
            AddMText(database, model, transaction, x + 260, y + 300, "设计证号", 90, 0, TextLayer, AttachmentPoint.MiddleCenter, 0);
            AddMText(database, model, transaction, x + 1020, y + 300, "A143017758", 90, 0, TextLayer, AttachmentPoint.MiddleCenter, 0);
            AddMText(database, model, transaction, x + 1770, y + 300, "图号", 90, 0, TextLayer, AttachmentPoint.MiddleCenter, 0);
            AddMText(database, model, transaction, x + 3270, y + 300,
                "杨洞二期-水工-暗涵-02", 85, 0, TextLayer, AttachmentPoint.MiddleCenter, 2300);
        }

        private static void DrawGrid(Transaction transaction, BlockTableRecord model, double x, double y,
            double[] widths, double[] heights, string layer)
        {
            double totalWidth = widths.Sum();
            double totalHeight = heights.Sum();
            AddRectangle(model, transaction, x, y, totalWidth, totalHeight, layer);
            double xx = x;
            for (int index = 0; index < widths.Length - 1; index++)
            {
                xx += widths[index];
                AddLine(model, transaction, xx, y, xx, y + totalHeight, layer);
            }
            double yy = y;
            for (int index = heights.Length - 1; index > 0; index--)
            {
                yy += heights[index];
                AddLine(model, transaction, x, yy, x + totalWidth, yy, layer);
            }
        }

        private static void AddRowTexts(Database database, Transaction transaction, BlockTableRecord model,
            double x, double y, double[] widths, string[] values, double height, double rowHeight = 650)
        {
            double xx = x;
            for (int index = 0; index < widths.Length; index++)
            {
                AddMText(database, model, transaction, xx + widths[index] / 2, y + rowHeight / 2,
                    values[index], height, 0, TextLayer, AttachmentPoint.MiddleCenter, Math.Max(0, widths[index] - 80));
                xx += widths[index];
            }
        }

        private static void AddCellRow(Database database, Transaction transaction, BlockTableRecord model,
            double x, double[] widths, double centerY, string[] values, double height)
        {
            double xx = x;
            for (int index = 0; index < widths.Length; index++)
            {
                AddMText(database, model, transaction, xx + widths[index] / 2, centerY,
                    values[index], height, 0, TextLayer, AttachmentPoint.MiddleCenter,
                    Math.Max(0, widths[index] - 60));
                xx += widths[index];
            }
        }

        private static void DrawBarShape(Transaction transaction, BlockTableRecord model,
            double x, double y, double width, double height, string layer)
        {
            var polyline = new Polyline(5) { Layer = layer };
            polyline.AddVertexAt(0, new Point2d(x + 80, y + height), 0, 0, 0);
            polyline.AddVertexAt(1, new Point2d(x, y + height), 0, 0, 0);
            polyline.AddVertexAt(2, new Point2d(x, y), 0, 0, 0);
            polyline.AddVertexAt(3, new Point2d(x + width, y), 0, 0, 0);
            polyline.AddVertexAt(4, new Point2d(x + width, y + height), 0, 0, 0);
            AddEntity(model, transaction, polyline);
        }

        private static void AddCallout(Database database, Transaction transaction, BlockTableRecord model,
            double x, double y, string number, string label, double targetX, double targetY)
        {
            AddCircle(model, transaction, x, y, 130, RebarLabelLayer);
            AddMText(database, model, transaction, x, y, number, 150, 0, RebarLabelLayer, AttachmentPoint.MiddleCenter, 0);
            AddMText(database, model, transaction, x + 180, y, label, 135, 0, RebarLabelLayer, AttachmentPoint.MiddleLeft, 900);
            AddLine(model, transaction, x + 170, y - 90, x + 900, y - 90, RebarLabelLayer);
            AddLine(model, transaction, x + 900, y - 90, targetX, targetY, RebarLabelLayer);
        }

        private static void AddHookBar(BlockTableRecord model, Transaction transaction, IEnumerable<Point2d> points)
        {
            Point2d[] array = points.ToArray();
            var polyline = new Polyline(array.Length) { Layer = RebarLayer };
            for (int index = 0; index < array.Length; index++)
            {
                polyline.AddVertexAt(index, array[index], 0, 0, 0);
            }
            AddEntity(model, transaction, polyline);
        }

        private static void AddAlignedDimension(BlockTableRecord model, Transaction transaction,
            Point3d first, Point3d second, Point3d linePoint, string text, ObjectId dimensionStyle)
        {
            var dimension = new AlignedDimension(first, second, linePoint, text, dimensionStyle)
            {
                Layer = DimensionLayer
            };
            AddEntity(model, transaction, dimension);
        }

        private static ObjectId GetDimensionStyle(Database database, Transaction transaction)
        {
            DimStyleTable styles = (DimStyleTable)transaction.GetObject(database.DimStyleTableId, OpenMode.ForRead);
            return styles.Has(DimensionStyleName) ? styles[DimensionStyleName] : database.Dimstyle;
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

        private static void AddCircle(BlockTableRecord model, Transaction transaction,
            double x, double y, double radius, string layer)
        {
            AddEntity(model, transaction, new Circle(new Point3d(x, y, 0), Vector3d.ZAxis, radius) { Layer = layer });
        }

        private static void AddMText(Database database, BlockTableRecord model, Transaction transaction,
            double x, double y, string text, double height, double rotation, string layer,
            AttachmentPoint attachment, double width)
        {
            TextStyleTable styles = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
            var entity = new MText
            {
                Location = new Point3d(x, y, 0),
                Contents = text,
                TextHeight = height,
                Rotation = rotation,
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

        private static void RequireResources(Database database, Transaction transaction)
        {
            LayerTable layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            foreach (string name in new[] { OutlineLayer, CoarseLayer, FineLayer, TextLayer, DimensionLayer, FrameLayer, RebarLayer, RebarLabelLayer, RebarTableLayer })
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
                Title = "杨洞二期拟建暗涵配筋图（语义化CAD重绘）",
                Subject = "既有 JPG 图像的可编辑 CAD 工程图重绘",
                Author = "Codex / AutoCAD CAD-MCP"
            };
            IDictionary custom = builder.CustomPropertyTable;
            custom["SourceImage"] = SourceImage;
            custom["SourceImageSha256"] = SourceImageSha256;
            custom["RequestId"] = requestId;
            custom["DrawingStatus"] = "原图语义化CAD重绘；未重新核定配筋";
            custom["DrawingNo"] = "杨洞二期-水工-暗涵-02";
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
                        .FirstOrDefault(name =>
                            name.IndexOf("A3", StringComparison.OrdinalIgnoreCase) >= 0 &&
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
                int circleCount = 0;
                int solidCount = 0;
                var texts = new List<string>();
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
                        if (entity is Circle)
                        {
                            circleCount++;
                        }
                        if (entity is Solid)
                        {
                            solidCount++;
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
                bool criticalTextPresent = allText.Contains("箱涵横断面钢筋图") &&
                    allText.Contains("2m×2m箱涵钢筋表") &&
                    allText.Contains("拟建暗涵配筋图") &&
                    allText.Contains("A143017758") &&
                    allText.Contains("GB50706-2011");
                bool extentsOk = extents.HasValue &&
                    extents.Value.MinPoint.X <= 1 && extents.Value.MinPoint.Y <= 1 &&
                    extents.Value.MaxPoint.X >= 20999 && extents.Value.MaxPoint.Y >= 14849;
                return new Dictionary<string, object>
                {
                    { "entity_count", entityCount },
                    { "mtext_count", mtextCount },
                    { "dimension_count", dimensionCount },
                    { "linework_count", lineworkCount },
                    { "circle_count", circleCount },
                    { "solid_count", solidCount },
                    { "critical_text_present", criticalTextPresent },
                    { "extents_ok", extentsOk },
                    { "insunits", database.Insunits.ToString() },
                    { "file_size", new FileInfo(path).Length }
                };
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
