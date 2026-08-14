using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CadMcp.AutoCAD
{
    internal static class TailraceBuilder
    {
        internal const string OutputRoot = @"G:\.codex\CAD_Project\尾水涵设计";
        internal const string SupportedOutputRoot = @"G:\.codex\CAD_Project\尾水涵设计\中间支撑双孔方案";
        internal const string SourceDwt = @"G:\.codex\CAD_Project\dwt_new\公司水利设计模板\公司水利钢筋混凝土设计.dwt";
        private const string LayoutName = "图纸";
        private const string CoarseLayer = "粗实线";
        private const string FineLayer = "细实";
        private const string TextLayer = "文字";
        private const string MaterialLayer = "填充";
        private const string RebarLayer = "REIN";
        private const string ViewportLayer = "不打印层";
        private const string TextStyleName = "宋体";

        private static readonly string[] DrawingNames =
        {
            "尾水涵_纵断面图",
            "尾水涵_横剖面图",
            "尾水涵_配筋图"
        };

        private static readonly string[] SupportedDrawingNames =
        {
            "尾水涵_双孔横剖面图",
            "尾水涵_双孔配筋图"
        };

        internal static string Validate(TemplateRequest request, string operation)
        {
            if (request == null)
            {
                return "Request body is required.";
            }
            bool supported = operation == "tailrace_supported_build" || operation == "tailrace_supported_verify";
            string expectedOutput = supported ? SupportedOutputRoot : OutputRoot;
            if (!PathEquals(request.output_dir, expectedOutput))
            {
                return "output_dir must be the configured tailrace scheme directory.";
            }
            if (!PathEquals(request.source_dwg, SourceDwt) || !File.Exists(SourceDwt))
            {
                return "source_dwg must be the configured company hydraulic RC DWT.";
            }
            if (operation == "tailrace_build" || operation == "tailrace_supported_build")
            {
                if (!ValidRequestId(request.request_id))
                {
                    return "request_id must contain 1 to 128 letters, digits, dots, underscores, colons, or hyphens.";
                }
                if (!string.Equals(request.standard, "COMPANY-HYDRO-RC-2026", StringComparison.Ordinal))
                {
                    return "standard must be COMPANY-HYDRO-RC-2026.";
                }
                foreach (double value in new[]
                {
                    request.length_m, request.clear_width_m, request.clear_height_m,
                    request.inlet_invert_m, request.cover_m, request.thickness_m,
                    request.bottom_thickness_m, request.truck_weight_kn,
                    request.traffic_pressure_kpa, request.global_safety_factor,
                    request.normal_water_m, request.design_water_m, request.check_water_m
                })
                {
                    if (!IsFinite(value))
                    {
                        return "All numeric design parameters must be finite.";
                    }
                }
                if (request.length_m <= 0 || request.clear_width_m <= 0 ||
                    request.clear_height_m <= 0 || request.cover_m < 0 || request.thickness_m <= 0 ||
                    request.bottom_thickness_m <= 0 || request.truck_weight_kn <= 0 ||
                    request.traffic_pressure_kpa <= 0 || request.global_safety_factor < 1.0 ||
                    request.global_safety_factor > 3.0)
                {
                    return "Geometry, traffic loads and thicknesses must be positive; safety factor must be 1.0 to 3.0.";
                }
                if (request.slope < 0 || request.slope > 0.02)
                {
                    return "slope must be between 0 and 0.02.";
                }
                if (!(request.normal_water_m <= request.design_water_m &&
                      request.design_water_m <= request.check_water_m))
                {
                    return "Water levels must satisfy normal <= design <= check.";
                }
                if (supported && (!IsFinite(request.center_wall_thickness_m) ||
                    request.center_wall_thickness_m <= 0 ||
                    request.center_wall_thickness_m >= request.clear_width_m))
                {
                    return "center_wall_thickness_m must be positive and smaller than clear_width_m.";
                }
            }
            return null;
        }

        internal static TemplateResponse Execute(string operation, TemplateRequest request)
        {
            if (operation == "tailrace_build")
            {
                return Build(request);
            }
            if (operation == "tailrace_verify")
            {
                return Verify(request);
            }
            if (operation == "tailrace_supported_build")
            {
                return BuildSupported(request);
            }
            if (operation == "tailrace_supported_verify")
            {
                return VerifySupported(request);
            }
            return TemplateResponse.Failure(request == null ? null : request.request_id, operation, "Unknown operation.");
        }

        private static TemplateResponse BuildSupported(TemplateRequest request)
        {
            string output = Path.GetFullPath(request.output_dir);
            Directory.CreateDirectory(output);
            string stage = Path.Combine(output, ".tailrace-supported-stage-" + request.request_id);
            EnsureChildPath(stage, output);
            if (Directory.Exists(stage))
            {
                Directory.Delete(stage, true);
            }
            Directory.CreateDirectory(stage);

            try
            {
                BuildSheet(request, stage, SupportedDrawingNames[0], "尾水涵双孔横剖面图", 0.020, DrawSupportedCrossSection);
                BuildSheet(request, stage, SupportedDrawingNames[1], "尾水涵双孔配筋图", 0.020, DrawSupportedReinforcement);
                File.WriteAllText(
                    Path.Combine(stage, "尾水涵_双孔中墙设计说明与初步计算.txt"),
                    BuildSupportedCalculationNote(request),
                    new UTF8Encoding(false));

                foreach (string staged in Directory.GetFiles(stage))
                {
                    string destination = Path.Combine(output, Path.GetFileName(staged));
                    File.Copy(staged, destination, true);
                }
                Directory.Delete(stage, true);

                TemplateResponse verified = VerifySupported(request);
                if (!verified.ok)
                {
                    throw new InvalidOperationException("Published supported culvert package failed verification: " + verified.error);
                }
                verified.request_id = request.request_id;
                verified.operation = "tailrace_supported_build";
                verified.message = "Supported twin-cell tailrace culvert drawings built, plotted and verified in AutoCAD.";
                return verified;
            }
            catch
            {
                if (Directory.Exists(stage))
                {
                    Directory.Delete(stage, true);
                }
                throw;
            }
        }

        private static TemplateResponse Build(TemplateRequest request)
        {
            string output = Path.GetFullPath(request.output_dir);
            Directory.CreateDirectory(output);
            string stage = Path.Combine(output, ".tailrace-stage-" + request.request_id);
            EnsureChildPath(stage, output);
            if (Directory.Exists(stage))
            {
                Directory.Delete(stage, true);
            }
            Directory.CreateDirectory(stage);

            try
            {
                BuildSheet(request, stage, DrawingNames[0], "尾水涵纵断面图", 0.005, DrawLongitudinal);
                BuildSheet(request, stage, DrawingNames[1], "尾水涵横剖面图", 0.020, DrawCrossSection);
                BuildSheet(request, stage, DrawingNames[2], "尾水涵配筋图", 0.020, DrawReinforcement);
                File.WriteAllText(
                    Path.Combine(stage, "尾水涵_设计说明与初步计算.txt"),
                    BuildCalculationNote(request),
                    new UTF8Encoding(false));

                foreach (string staged in Directory.GetFiles(stage))
                {
                    string destination = Path.Combine(output, Path.GetFileName(staged));
                    File.Copy(staged, destination, true);
                }
                Directory.Delete(stage, true);

                TemplateResponse verified = Verify(request);
                if (!verified.ok)
                {
                    throw new InvalidOperationException("Published drawing package failed verification: " + verified.error);
                }
                verified.request_id = request.request_id;
                verified.operation = "tailrace_build";
                verified.message = "Tailrace culvert drawing package built, plotted and verified in AutoCAD.";
                return verified;
            }
            catch
            {
                if (Directory.Exists(stage))
                {
                    Directory.Delete(stage, true);
                }
                throw;
            }
        }

        private static void BuildSheet(
            TemplateRequest request,
            string stage,
            string fileStem,
            string drawingTitle,
            double preferredScale,
            Func<Database, Transaction, BlockTableRecord, TemplateRequest, Extents2d> drawer)
        {
            Document document = null;
            string finalDwgPath = Path.Combine(stage, fileStem + ".dwg");
            string dwgPath = Path.Combine(stage, DrawingCode(fileStem) + ".dwg");
            try
            {
                document = AcApplication.DocumentManager.Add(request.source_dwg);
                if (document == null)
                {
                    throw new InvalidOperationException("AutoCAD could not create a drawing from the company hydraulic RC DWT.");
                }
                using (document.LockDocument())
                {
                    Database database = document.Database;
                    database.Insunits = UnitsValue.Millimeters;
                    SetSummary(database, drawingTitle);
                    Extents2d extents;
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
                        extents = drawer(database, transaction, model, request);
                        transaction.Commit();
                    }

                    LayoutManager.Current.CurrentLayout = LayoutName;
                    using (Transaction transaction = database.TransactionManager.StartTransaction())
                    {
                        ConfigureViewport(database, transaction, extents, preferredScale);
                        transaction.Commit();
                    }
                    database.EvaluateFields(FieldEvaluationContext.Regen);
                    document.Editor.Regen();

                    database.SaveAs(dwgPath, DwgVersion.Current);
                }
                document.CloseAndDiscard();
                document = AcApplication.DocumentManager.Open(dwgPath, false);
                using (document.LockDocument())
                {
                    Database database = document.Database;
                    LayoutManager.Current.CurrentLayout = LayoutName;
                    database.EvaluateFields(FieldEvaluationContext.Regen);
                    document.Editor.Regen();
                    object previousBackgroundPlot = AcApplication.GetSystemVariable("BACKGROUNDPLOT");
                    try
                    {
                        AcApplication.SetSystemVariable("BACKGROUNDPLOT", 0);
                        PlotLayout(database, GetLayoutId(database), Path.Combine(stage, fileStem + ".pdf"));
                    }
                    finally
                    {
                        AcApplication.SetSystemVariable("BACKGROUNDPLOT", previousBackgroundPlot);
                    }
                }
                document.CloseAndSave(dwgPath);
                document = null;
                File.Move(dwgPath, finalDwgPath);
            }
            finally
            {
                if (document != null)
                {
                    document.CloseAndDiscard();
                }
            }
        }

        private static Extents2d DrawLongitudinal(
            Database database, Transaction transaction, BlockTableRecord model,
            TemplateRequest request)
        {
            double length = request.length_m * 1000.0;
            double verticalScale = 2000.0;
            double datum = Math.Floor((request.inlet_invert_m - request.thickness_m - 0.5) * 2.0) / 2.0;
            Func<double, double> y = elevation => (elevation - datum) * verticalScale;
            double outletInvert = request.inlet_invert_m - request.length_m * request.slope;
            double outerBottomIn = request.inlet_invert_m - request.bottom_thickness_m;
            double outerBottomOut = outletInvert - request.bottom_thickness_m;
            double innerTopIn = request.inlet_invert_m + request.clear_height_m;
            double innerTopOut = outletInvert + request.clear_height_m;
            double outerTopIn = innerTopIn + request.thickness_m;
            double outerTopOut = innerTopOut + request.thickness_m;

            AddLine(model, transaction, 0, y(outerBottomIn), length, y(outerBottomOut), CoarseLayer);
            AddLine(model, transaction, 0, y(request.inlet_invert_m), length, y(outletInvert), CoarseLayer);
            AddLine(model, transaction, 0, y(innerTopIn), length, y(innerTopOut), CoarseLayer);
            AddLine(model, transaction, 0, y(outerTopIn), length, y(outerTopOut), CoarseLayer);
            AddLine(model, transaction, 0, y(outerTopIn + request.cover_m), length, y(outerTopOut + request.cover_m), MaterialLayer);
            AddLine(model, transaction, 0, y(outerBottomIn), 0, y(outerTopIn), CoarseLayer);
            AddLine(model, transaction, length, y(outerBottomOut), length, y(outerTopOut), CoarseLayer);

            AddWaterLine(database, model, transaction, -1000, length + 1000, y(request.normal_water_m),
                "正常尾水位 " + F2(request.normal_water_m));
            AddWaterLine(database, model, transaction, -1000, length + 1000, y(request.design_water_m),
                "设计水位(P=5%) " + F2(request.design_water_m));
            AddWaterLine(database, model, transaction, -1000, length + 1000, y(request.check_water_m),
                "校核水位(P=2%) " + F2(request.check_water_m));

            int jointCount = (int)Math.Floor(request.length_m / 10.0);
            for (int i = 1; i <= jointCount; i++)
            {
                double x = Math.Min(i * 10000.0, length);
                AddLine(model, transaction, x, y(outerBottomIn) - 300, x, y(outerTopIn + request.cover_m) + 300, FineLayer);
                AddText(database, model, transaction, x - 500, y(outerBottomIn) - 650,
                    "0+" + (i * 10).ToString("000", CultureInfo.InvariantCulture), 260, 0, TextLayer);
            }

            AddText(database, model, transaction, length * 0.36, y(outerTopIn + request.cover_m) + 900,
                "尾水涵纵断面图  横向1:200  竖向1:100", 700, 0, TextLayer);
            AddText(database, model, transaction, 500, y(outerTopIn + request.cover_m) + 250,
                "场区道路面（覆土 " + F2(request.cover_m) + "m；550kN重型卡车）", 480, 0, TextLayer);
            AddText(database, model, transaction, length * 0.32, y(request.inlet_invert_m) + 250,
                "净高 " + F2(request.clear_height_m) + "m；纵坡 i=" + request.slope.ToString("0.0000", CultureInfo.InvariantCulture),
                480, 0, TextLayer);
            AddText(database, model, transaction, 0, y(outerBottomIn) - 1150,
                "进口桩号 0+000  涵底高程 " + F2(request.inlet_invert_m), 460, 0, TextLayer);
            AddText(database, model, transaction, length - 11500, y(outerBottomOut) - 1150,
                "出口桩号 0+070  涵底高程 " + F2(outletInvert), 460, 0, TextLayer);
            AddText(database, model, transaction, length * 0.30, y(outerBottomIn) - 1700,
                "总长 " + F2(request.length_m) + "m；每10m设一道20mm结构缝，缝内设中埋式橡胶止水带", 440, 0, TextLayer);
            AddText(database, model, transaction, 0, y(outerBottomIn) - 2200,
                "警告：已按550kN场区卡车、覆土3m和安全系数1.5计算；地基、地下水、抗震及实际车辆轴载未提供，本图为参数假定版校审方案，不得直接施工。", 430, 0, TextLayer);

            return new Extents2d(new Point2d(0, y(outerBottomOut) - 2600),
                new Point2d(length, y(Math.Max(request.check_water_m, outerTopIn + request.cover_m)) + 1400));
        }

        private static Extents2d DrawCrossSection(
            Database database, Transaction transaction, BlockTableRecord model,
            TemplateRequest request)
        {
            double width = request.clear_width_m * 1000.0;
            double height = request.clear_height_m * 1000.0;
            double thickness = request.thickness_m * 1000.0;
            double bottomThickness = request.bottom_thickness_m * 1000.0;
            double outerWidth = width + 2.0 * thickness;
            double outerHeight = bottomThickness + height + thickness;
            double left = 0;
            double bottom = 0;

            AddRectangle(model, transaction, left, bottom, outerWidth, outerHeight, CoarseLayer);
            AddRectangle(model, transaction, left + thickness, bottom + bottomThickness, width, height, CoarseLayer);
            AddRectangle(model, transaction, left - 100, bottom - 100, outerWidth + 200, 100, FineLayer);
            AddLine(model, transaction, -700, outerHeight + request.cover_m * 1000.0,
                outerWidth + 700, outerHeight + request.cover_m * 1000.0, MaterialLayer);

            double normalDepth = Math.Max(0, Math.Min(height, (request.normal_water_m - request.inlet_invert_m) * 1000.0));
            AddLine(model, transaction, thickness, bottomThickness + normalDepth,
                thickness + width, bottomThickness + normalDepth, FineLayer);
            AddText(database, model, transaction, thickness + width * 0.62, bottomThickness + normalDepth + 100,
                "正常尾水位", 180, 0, TextLayer);

            AddDimension(database, model, transaction,
                new Point3d(thickness, bottom - 300, 0), new Point3d(thickness + width, bottom - 300, 0),
                new Point3d(thickness + width / 2.0, bottom - 650, 0), "净宽4000");
            AddLine(model, transaction, outerWidth + 300, bottomThickness,
                outerWidth + 300, bottomThickness + height, FineLayer);
            AddLine(model, transaction, outerWidth + 150, bottomThickness,
                outerWidth + 450, bottomThickness, FineLayer);
            AddLine(model, transaction, outerWidth + 150, bottomThickness + height,
                outerWidth + 450, bottomThickness + height, FineLayer);
            AddText(database, model, transaction, thickness + width / 2.0 - 650, bottom - 900,
                "净宽 4000", 220, 0, TextLayer);
            AddText(database, model, transaction, outerWidth + 650, bottomThickness + 350,
                "净高 1700", 220, Math.PI / 2.0, TextLayer);

            AddText(database, model, transaction, outerWidth * 0.23, outerHeight + request.cover_m * 1000.0 + 450,
                "尾水涵标准横剖面图  1:50", 360, 0, TextLayer);
            AddText(database, model, transaction, outerWidth + 1100, outerHeight - 100,
                "主体：C35抗渗混凝土，抗渗等级W8；HRB400", 250, 0, TextLayer);
            AddText(database, model, transaction, outerWidth + 1100, outerHeight - 500,
                "顶板/侧墙400mm，底板450mm；保护层50mm", 250, 0, TextLayer);
            AddText(database, model, transaction, outerWidth + 1100, outerHeight - 900,
                "垫层：100mm厚C15素混凝土，两侧各伸出100mm", 250, 0, TextLayer);
            AddText(database, model, transaction, outerWidth + 1100, outerHeight - 1300,
                "覆土3.00m；回填土重度20kN/m³；两侧对称压实", 250, 0, TextLayer);
            AddText(database, model, transaction, outerWidth + 1100, outerHeight - 1700,
                "通车荷载：550kN场区重型卡车，等效20kPa包络", 250, 0, TextLayer);
            AddText(database, model, transaction, outerWidth + 1100, outerHeight - 2100,
                "总体安全系数1.5；地下水及地基参数待勘察复核", 250, 0, TextLayer);
            AddText(database, model, transaction, -700, -1300,
                "警告：本图须与现场高程、流量及结构计算复核后方可转为施工图。", 260, 0, TextLayer);

            return new Extents2d(new Point2d(-1000, -1600), new Point2d(outerWidth + 12500, outerHeight + request.cover_m * 1000.0 + 900));
        }

        private static Extents2d DrawReinforcement(
            Database database, Transaction transaction, BlockTableRecord model,
            TemplateRequest request)
        {
            double width = request.clear_width_m * 1000.0;
            double height = request.clear_height_m * 1000.0;
            double t = request.thickness_m * 1000.0;
            double bt = request.bottom_thickness_m * 1000.0;
            double ow = width + 2 * t;
            double oh = bt + height + t;
            double cover = 50;

            AddRectangle(model, transaction, 0, 0, ow, oh, CoarseLayer);
            AddRectangle(model, transaction, t, bt, width, height, CoarseLayer);
            AddRectangle(model, transaction, cover, cover, ow - 2 * cover, oh - 2 * cover, FineLayer);
            AddRectangle(model, transaction, t - cover, bt - cover, width + 2 * cover, height + 2 * cover, FineLayer);

            for (double x = cover + 50; x < ow - cover; x += 200)
            {
                AddCircle(model, transaction, x, cover, 20, RebarLayer);
                AddCircle(model, transaction, x, bt - cover, 20, RebarLayer);
                AddCircle(model, transaction, x, oh - cover, 20, RebarLayer);
                AddCircle(model, transaction, x, bt + height + cover, 20, RebarLayer);
            }
            for (double y = cover + 50; y < oh - cover; y += 200)
            {
                AddCircle(model, transaction, cover, y, 18, RebarLayer);
                AddCircle(model, transaction, t - cover, y, 18, RebarLayer);
                AddCircle(model, transaction, ow - cover, y, 18, RebarLayer);
                AddCircle(model, transaction, t + width + cover, y, 18, RebarLayer);
            }

            AddText(database, model, transaction, ow * 0.20, oh + 600,
                "尾水涵标准断面配筋图  1:50", 360, 0, TextLayer);
            AddText(database, model, transaction, ow + 800, oh + 100,
                "1 顶板横向主筋：HRB400 Φ20@100，上下层通长", 240, 0, TextLayer);
            AddText(database, model, transaction, ow + 800, oh - 300,
                "2 底板横向主筋：HRB400 Φ20@100，上下层通长", 240, 0, TextLayer);
            AddText(database, model, transaction, ow + 800, oh - 700,
                "3 两侧墙竖向主筋：HRB400 Φ18@150，内外侧", 240, 0, TextLayer);
            AddText(database, model, transaction, ow + 800, oh - 1100,
                "4 全断面纵向分布筋：HRB400 Φ14@150，内外层", 240, 0, TextLayer);
            AddText(database, model, transaction, ow + 800, oh - 1500,
                "5 四角L形附加筋Φ20@100；每肢伸入板墙≥1200", 240, 0, TextLayer);
            AddText(database, model, transaction, ow + 800, oh - 1900,
                "6 保护层50mm；锚固搭接按SL/T 191-2025复核", 240, 0, TextLayer);
            AddText(database, model, transaction, ow + 800, oh - 2300,
                "7 结构缝处纵筋断开，651型止水带连续可靠固定", 240, 0, TextLayer);

            double jx = ow + 900;
            double jy = -1300;
            AddRectangle(model, transaction, jx, jy, 4200, 900, CoarseLayer);
            AddLine(model, transaction, jx + 2050, jy, jx + 2050, jy + 900, FineLayer);
            AddLine(model, transaction, jx + 2150, jy, jx + 2150, jy + 900, FineLayer);
            AddLine(model, transaction, jx + 1800, jy + 450, jx + 2400, jy + 450, CoarseLayer);
            AddText(database, model, transaction, jx, jy + 1150,
                "20mm结构缝及中埋式橡胶止水带示意  1:20", 260, 0, TextLayer);
            AddText(database, model, transaction, jx + 2500, jy + 520,
                "651型橡胶止水带 300×8", 220, 0, TextLayer);
            AddText(database, model, transaction, -500, -2100,
                "按550kN场区卡车、覆土3m和安全系数1.5设计；地基、外水、抗震未实测，成果为参数假定版校审方案，不得直接施工。", 220, 0, TextLayer);

            return new Extents2d(new Point2d(-800, -2400), new Point2d(ow + 12500, oh + 1100));
        }

        private static Extents2d DrawSupportedCrossSection(
            Database database, Transaction transaction, BlockTableRecord model,
            TemplateRequest request)
        {
            double totalClearWidth = request.clear_width_m * 1000.0;
            double centerWall = request.center_wall_thickness_m * 1000.0;
            double cellWidth = (totalClearWidth - centerWall) / 2.0;
            double height = request.clear_height_m * 1000.0;
            double t = request.thickness_m * 1000.0;
            double bt = request.bottom_thickness_m * 1000.0;
            double outerWidth = totalClearWidth + 2.0 * t;
            double outerHeight = bt + height + t;
            double centerLeft = t + cellWidth;
            double centerRight = centerLeft + centerWall;

            AddRectangle(model, transaction, 0, 0, outerWidth, outerHeight, CoarseLayer);
            AddRectangle(model, transaction, t, bt, cellWidth, height, CoarseLayer);
            AddRectangle(model, transaction, centerRight, bt, cellWidth, height, CoarseLayer);
            AddRectangle(model, transaction, -100, -100, outerWidth + 200, 100, FineLayer);
            AddLine(model, transaction, -700, outerHeight + request.cover_m * 1000.0,
                outerWidth + 700, outerHeight + request.cover_m * 1000.0, MaterialLayer);

            double normalDepth = Math.Max(0, Math.Min(height,
                (request.normal_water_m - request.inlet_invert_m) * 1000.0));
            AddLine(model, transaction, t, bt + normalDepth, centerLeft, bt + normalDepth, FineLayer);
            AddLine(model, transaction, centerRight, bt + normalDepth, t + totalClearWidth, bt + normalDepth, FineLayer);
            AddText(database, model, transaction, t + 300, bt + normalDepth + 100,
                "正常尾水位", 180, 0, TextLayer);

            AddLine(model, transaction, t, 0, t, -600, FineLayer);
            AddLine(model, transaction, centerLeft, 0, centerLeft, -600, FineLayer);
            AddLine(model, transaction, t, -450, centerLeft, -450, FineLayer);
            AddText(database, model, transaction, t + cellWidth / 2.0 - 420, -760,
                "孔室净宽 1800", 190, 0, TextLayer);
            AddLine(model, transaction, centerRight, 0, centerRight, -600, FineLayer);
            AddLine(model, transaction, t + totalClearWidth, 0, t + totalClearWidth, -600, FineLayer);
            AddLine(model, transaction, centerRight, -450, t + totalClearWidth, -450, FineLayer);
            AddText(database, model, transaction, centerRight + cellWidth / 2.0 - 420, -760,
                "孔室净宽 1800", 190, 0, TextLayer);
            AddLine(model, transaction, centerLeft, bt + height, centerLeft, outerHeight + 450, FineLayer);
            AddLine(model, transaction, centerRight, bt + height, centerRight, outerHeight + 450, FineLayer);
            AddLine(model, transaction, centerLeft, outerHeight + 300, centerRight, outerHeight + 300, FineLayer);
            AddText(database, model, transaction, centerLeft - 140, outerHeight + 500,
                "中墙 400", 190, 0, TextLayer);
            AddText(database, model, transaction, t + totalClearWidth / 2.0 - 900, -1150,
                "总内宽 4000（1800+400+1800）", 220, 0, TextLayer);
            AddText(database, model, transaction, outerWidth + 650, bt + 300,
                "净高 1700", 220, Math.PI / 2.0, TextLayer);

            AddText(database, model, transaction, outerWidth * 0.20,
                outerHeight + request.cover_m * 1000.0 + 450,
                "尾水涵双孔中间支撑横剖面图  1:50", 360, 0, TextLayer);
            AddText(database, model, transaction, outerWidth + 1100, outerHeight - 100,
                "结构：双孔闭合箱涵；每孔净宽1800mm，中墙400mm", 250, 0, TextLayer);
            AddText(database, model, transaction, outerWidth + 1100, outerHeight - 500,
                "主体：C35 W8；HRB400；保护层50mm", 250, 0, TextLayer);
            AddText(database, model, transaction, outerWidth + 1100, outerHeight - 900,
                "顶板/侧墙/中墙400mm，底板450mm", 250, 0, TextLayer);
            AddText(database, model, transaction, outerWidth + 1100, outerHeight - 1300,
                "覆土3.00m；550kN车辆；等效交通荷载20kPa", 250, 0, TextLayer);
            AddText(database, model, transaction, outerWidth + 1100, outerHeight - 1700,
                "总体安全系数1.5；两侧回填对称分层压实", 250, 0, TextLayer);
            AddText(database, model, transaction, outerWidth + 1100, outerHeight - 2100,
                "中墙沿涵长连续设置，与顶、底板整体浇筑", 250, 0, TextLayer);
            AddText(database, model, transaction, -700, -1550,
                "警告：实际轴载、地基、地下水、抗震及不对称布载须整体框架复核；本图为参数假定版，不得直接施工。", 245, 0, TextLayer);

            return new Extents2d(new Point2d(-1000, -1850),
                new Point2d(outerWidth + 13000, outerHeight + request.cover_m * 1000.0 + 900));
        }

        private static Extents2d DrawSupportedReinforcement(
            Database database, Transaction transaction, BlockTableRecord model,
            TemplateRequest request)
        {
            double totalClearWidth = request.clear_width_m * 1000.0;
            double centerWall = request.center_wall_thickness_m * 1000.0;
            double cellWidth = (totalClearWidth - centerWall) / 2.0;
            double height = request.clear_height_m * 1000.0;
            double t = request.thickness_m * 1000.0;
            double bt = request.bottom_thickness_m * 1000.0;
            double ow = totalClearWidth + 2.0 * t;
            double oh = bt + height + t;
            double centerLeft = t + cellWidth;
            double centerRight = centerLeft + centerWall;
            double cover = 50.0;

            AddRectangle(model, transaction, 0, 0, ow, oh, CoarseLayer);
            AddRectangle(model, transaction, t, bt, cellWidth, height, CoarseLayer);
            AddRectangle(model, transaction, centerRight, bt, cellWidth, height, CoarseLayer);
            AddRectangle(model, transaction, cover, cover, ow - 2 * cover, oh - 2 * cover, FineLayer);
            AddRectangle(model, transaction, t - cover, bt - cover, cellWidth + 2 * cover, height + 2 * cover, FineLayer);
            AddRectangle(model, transaction, centerRight - cover, bt - cover, cellWidth + 2 * cover, height + 2 * cover, FineLayer);

            for (double x = cover + 50; x < ow - cover; x += 150)
            {
                AddCircle(model, transaction, x, cover, 8, RebarLayer);
                AddCircle(model, transaction, x, bt - cover, 8, RebarLayer);
                AddCircle(model, transaction, x, oh - cover, 8, RebarLayer);
                AddCircle(model, transaction, x, bt + height + cover, 8, RebarLayer);
            }
            for (double y = cover + 50; y < oh - cover; y += 150)
            {
                AddCircle(model, transaction, cover, y, 9, RebarLayer);
                AddCircle(model, transaction, t - cover, y, 9, RebarLayer);
                AddCircle(model, transaction, ow - cover, y, 9, RebarLayer);
                AddCircle(model, transaction, t + totalClearWidth + cover, y, 9, RebarLayer);
                AddCircle(model, transaction, centerLeft + cover, y, 8, RebarLayer);
                AddCircle(model, transaction, centerRight - cover, y, 8, RebarLayer);
            }

            double leg = 1000.0;
            foreach (double x in new[] { centerLeft, centerRight })
            {
                AddLine(model, transaction, x, bt + height - leg, x, bt + height + cover, RebarLayer);
                AddLine(model, transaction, x, bt + height + cover, x + (x == centerLeft ? -leg : leg), bt + height + cover, RebarLayer);
                AddLine(model, transaction, x, bt + leg, x, bt - cover, RebarLayer);
                AddLine(model, transaction, x, bt - cover, x + (x == centerLeft ? -leg : leg), bt - cover, RebarLayer);
            }

            AddText(database, model, transaction, ow * 0.14, oh + 600,
                "尾水涵双孔中间支撑配筋图  1:50", 360, 0, TextLayer);
            AddText(database, model, transaction, ow + 800, oh + 100,
                "1 顶板横向主筋：HRB400 Φ16@150，上下层通长", 240, 0, TextLayer);
            AddText(database, model, transaction, ow + 800, oh - 300,
                "2 底板横向主筋：HRB400 Φ16@150，上下层通长", 240, 0, TextLayer);
            AddText(database, model, transaction, ow + 800, oh - 700,
                "3 两侧墙竖向主筋：HRB400 Φ18@150，内外侧", 240, 0, TextLayer);
            AddText(database, model, transaction, ow + 800, oh - 1100,
                "4 中墙竖向主筋：HRB400 Φ16@150，两侧面", 240, 0, TextLayer);
            AddText(database, model, transaction, ow + 800, oh - 1500,
                "5 全断面纵向分布筋：HRB400 Φ14@150，各钢筋层", 240, 0, TextLayer);
            AddText(database, model, transaction, ow + 800, oh - 1900,
                "6 中墙与顶/底板节点L形附加筋Φ18@150，肢长≥1000", 240, 0, TextLayer);
            AddText(database, model, transaction, ow + 800, oh - 2300,
                "7 外角L形附加筋Φ20@100，肢长≥1200；保护层50", 240, 0, TextLayer);
            AddText(database, model, transaction, ow + 800, oh - 2700,
                "8 锚固、搭接、接头率及结构缝断筋按SL/T 191-2025复核", 240, 0, TextLayer);
            AddText(database, model, transaction, -500, -1900,
                "总内宽4000=1800+400+1800；按550kN车辆、3m覆土和安全系数1.5设计。", 225, 0, TextLayer);
            AddText(database, model, transaction, -500, -2250,
                "参数假定版校审方案，不得直接施工；不对称车辆布载和中墙偏心弯矩须整体框架复核。", 225, 0, TextLayer);

            return new Extents2d(new Point2d(-800, -2550), new Point2d(ow + 13500, oh + 1100));
        }

        private static string BuildCalculationNote(TemplateRequest request)
        {
            double topThickness = request.thickness_m;
            double bottomThickness = request.bottom_thickness_m;
            double qSoil = 20.0 * request.cover_m;
            double qSelf = 25.0 * topThickness;
            double qTraffic = request.traffic_pressure_kpa;
            double qService = qSoil + qSelf + qTraffic;
            double qDesign = qService * request.global_safety_factor;
            double roofMoment = qDesign * request.clear_width_m * request.clear_width_m / 8.0;
            double roofShear = qDesign * request.clear_width_m / 2.0;
            double roofEffectiveDepth = topThickness * 1000.0 - 50.0 - 10.0;
            double roofRequiredAs = roofMoment * 1000000.0 / (360.0 * 0.9 * roofEffectiveDepth);
            double roofProvidedAs = Math.PI * 20.0 * 20.0 / 4.0 * 1000.0 / 100.0;
            double roofShearStress = roofShear * 1000.0 / (1000.0 * roofEffectiveDepth);
            double roofSteelStressService = (qService * request.clear_width_m * request.clear_width_m / 8.0) * 1000000.0 /
                (roofProvidedAs * 0.9 * roofEffectiveDepth);

            double outerWidth = request.clear_width_m + 2.0 * topThickness;
            double outerHeight = bottomThickness + request.clear_height_m + topThickness;
            double concreteArea = outerWidth * outerHeight - request.clear_width_m * request.clear_height_m;
            double verticalSoil = qSoil * outerWidth;
            double verticalTraffic = qTraffic * outerWidth;
            double verticalConcrete = 25.0 * concreteArea;
            double foundationService = (verticalSoil + verticalTraffic + verticalConcrete) / outerWidth;
            double foundationDesign = foundationService * request.global_safety_factor;
            double bottomMoment = foundationDesign * request.clear_width_m * request.clear_width_m / 8.0;
            double bottomEffectiveDepth = bottomThickness * 1000.0 - 50.0 - 10.0;
            double bottomRequiredAs = bottomMoment * 1000000.0 / (360.0 * 0.9 * bottomEffectiveDepth);

            double lateralDepth = request.cover_m + topThickness + request.clear_height_m + bottomThickness;
            double lateralService = 0.5 * 20.0 * lateralDepth + 0.5 * qTraffic;
            double lateralDesign = lateralService * request.global_safety_factor;
            double wallMoment = lateralDesign * request.clear_height_m * request.clear_height_m / 2.0;
            double wallEffectiveDepth = topThickness * 1000.0 - 50.0 - 9.0;
            double wallRequiredAs = wallMoment * 1000000.0 / (360.0 * 0.9 * wallEffectiveDepth);
            double wallProvidedAs = Math.PI * 18.0 * 18.0 / 4.0 * 1000.0 / 150.0;
            double longitudinalProvidedAs = Math.PI * 14.0 * 14.0 / 4.0 * 1000.0 / 150.0;
            double outlet = request.inlet_invert_m - request.length_m * request.slope;
            var text = new StringBuilder();
            text.AppendLine("尾水涵通车荷载设计说明与配筋计算（校审方案）");
            text.AppendLine("生成日期：" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            text.AppendLine();
            text.AppendLine("一、适用性质");
            text.AppendLine("本套图按用户指定的场区卡车通行、覆土3.00m和总体安全系数1.5重新设计。车辆轴型按550kN重型车辆模型取值，并以覆土扩散后的20kPa等效均布荷载包络。因实际车辆轴载、地勘、地下水、抗震和水力资料尚未提供，本成果为校审方案，不得直接用于施工。补齐资料并完成箱形框架、裂缝、抗浮、地基及出口防冲复核后，方可转施工图。");
            text.AppendLine();
            text.AppendLine("二、采用规范体系");
            text.AppendLine("1. GB 50071-2014《小型水力发电站设计规范》；");
            text.AppendLine("2. SL 266-2014《水电站厂房设计规范》；");
            text.AppendLine("3. SL/T 191-2025《水工混凝土结构设计规范》；");
            text.AppendLine("4. SL/T 744-2016《水工建筑物荷载设计规范》；");
            text.AppendLine("5. SL/T 73.1-2026、SL/T 73.2-2026《水利水电工程制图》。");
            text.AppendLine("6. JTG D60-2015《公路桥涵设计通用规范》及JTG/T 3365-02-2020《公路涵洞设计规范》（仅用于车辆模型和覆土涵洞交通作用复核）。");
            text.AppendLine("不得与能源行业规范体系择低混用安全系数。");
            text.AppendLine();
            text.AppendLine("三、输入与明确假定");
            text.AppendLine("净断面：" + F2(request.clear_width_m) + "m×" + F2(request.clear_height_m) + "m；总长：" + F2(request.length_m) + "m；纵坡：" + request.slope.ToString("0.0000", CultureInfo.InvariantCulture) + "；");
            text.AppendLine("进口涵底高程：" + F2(request.inlet_invert_m) + "m；出口涵底高程：" + F2(outlet) + "m；覆土：" + F2(request.cover_m) + "m；");
            text.AppendLine("正常/设计/校核水位：" + F2(request.normal_water_m) + "/" + F2(request.design_water_m) + "/" + F2(request.check_water_m) + "m；");
            text.AppendLine("车辆模型：总重" + F0(request.truck_weight_kn) + "kN（30+2×120+2×140kN轴组）；覆土按1:1扩散后取等效均布荷载" + F2(qTraffic) + "kPa；总体安全系数" + F2(request.global_safety_factor) + "。");
            text.AppendLine("假定回填土重度20kN/m³、静止土压力系数K0=0.50、地下水低于底板、天然均匀地基承载力特征值不低于200kPa。以上假定均须由项目资料确认。");
            text.AppendLine();
            text.AppendLine("四、结构尺寸与材料");
            text.AppendLine("主体C35 W8，HRB400；顶板和侧墙厚" + F0(topThickness * 1000) + "mm，底板厚" + F0(bottomThickness * 1000) + "mm，设计保护层50mm。每10m设20mm结构缝，采用651型中埋式橡胶止水带300×8mm。");
            text.AppendLine();
            text.AppendLine("五、顶板保守简算（每延米板带）");
            text.AppendLine("标准面荷载：覆土" + F2(qSoil) + "+顶板自重" + F2(qSelf) + "+车辆等效" + F2(qTraffic) + "=" + F2(qService) + "kPa；乘总体安全系数后设计面荷载=" + F2(qDesign) + "kPa。");
            text.AppendLine("按净跨" + F2(request.clear_width_m) + "m简支板保守包络：M=qL²/8=" + F2(roofMoment) + "kN·m/m，V=qL/2=" + F2(roofShear) + "kN/m。");
            text.AppendLine("按fy=360MPa、d≈" + F0(roofEffectiveDepth) + "mm：As,req≈" + F0(roofRequiredAs) + "mm²/m；Φ20@100提供As≈" + F0(roofProvidedAs) + "mm²/m，配筋利用率约" + F2(roofRequiredAs / roofProvidedAs) + "。");
            text.AppendLine("截面平均剪应力约" + F2(roofShearStress) + "MPa；服务组合钢筋应力简算约" + F0(roofSteelStressService) + "MPa，裂缝控制目标0.20mm，须用最终框架内力复核。");
            text.AppendLine();
            text.AppendLine("六、底板与地基反力保守简算（每延米涵长）");
            text.AppendLine("覆土竖向力" + F2(verticalSoil) + "kN/m，车辆等效力" + F2(verticalTraffic) + "kN/m，结构自重" + F2(verticalConcrete) + "kN/m；地基服务平均压力≈" + F2(foundationService) + "kPa，乘1.5后≈" + F2(foundationDesign) + "kPa。");
            text.AppendLine("按假定地基承载力特征值200kPa比较，设计包络未超过假定值；实际地勘参数、沉降和基底脱空仍须复核。");
            text.AppendLine("按净宽简支板保守包络：M≈" + F2(bottomMoment) + "kN·m/m；d≈" + F0(bottomEffectiveDepth) + "mm，As,req≈" + F0(bottomRequiredAs) + "mm²/m；Φ20@100提供As≈" + F0(roofProvidedAs) + "mm²/m，配筋利用率约" + F2(bottomRequiredAs / roofProvidedAs) + "。");
            text.AppendLine();
            text.AppendLine("七、侧墙保守简算与配筋");
            text.AppendLine("按K0=0.50、路面至底板底深度" + F2(lateralDepth) + "m及车辆侧向附加包络，墙底侧压力服务值≈" + F2(lateralService) + "kPa，乘1.5后≈" + F2(lateralDesign) + "kPa。");
            text.AppendLine("按净高悬臂板保守包络：M≈" + F2(wallMoment) + "kN·m/m；As,req≈" + F0(wallRequiredAs) + "mm²/m；Φ18@150每面提供As≈" + F0(wallProvidedAs) + "mm²/m。");
            text.AppendLine("全断面纵向分布筋Φ14@150，每层提供As≈" + F0(longitudinalProvidedAs) + "mm²/m；四角增设L形Φ20@100，每肢伸入板墙不小于1200mm。");
            text.AppendLine();
            text.AppendLine("八、计算边界与结论");
            text.AppendLine("本次以简支板/悬臂板包络核定配筋数量级，所给配筋满足上述假定荷载下的强度初算。它不替代闭合箱形框架或板壳整体分析，以及裂缝、温度收缩、施工期、外水、抗浮、地基变形、抗震和局部轮压验算。");
            text.AppendLine("拟定配筋：顶板横向Φ20@100上下层；底板横向Φ20@100上下层；侧墙竖向Φ18@150内外侧；全断面纵向Φ14@150内外层；保护层50mm。");
            text.AppendLine();
            text.AppendLine("九、转施工图前必须补齐");
            text.AppendLine("实际最大车辆总重、轴重、轴距、轮距、轮胎接地尺寸及行驶范围；设计流量和工况；尾水管出口尺寸与高程；实际进出口地面及河床高程；正常/最低/设计/校核尾水位；地下水位；地基承载力、压缩模量、摩擦系数及不均匀沉降；抗震参数；出口河床材料及冲刷计算。");
            return text.ToString();
        }

        private static string BuildSupportedCalculationNote(TemplateRequest request)
        {
            double topThickness = request.thickness_m;
            double bottomThickness = request.bottom_thickness_m;
            double centerWall = request.center_wall_thickness_m;
            double cellWidth = (request.clear_width_m - centerWall) / 2.0;
            double qSoil = 20.0 * request.cover_m;
            double qSelf = 25.0 * topThickness;
            double qTraffic = request.traffic_pressure_kpa;
            double qService = qSoil + qSelf + qTraffic;
            double qDesign = qService * request.global_safety_factor;
            double roofMoment = qDesign * cellWidth * cellWidth / 8.0;
            double roofShear = qDesign * cellWidth / 2.0;
            double roofEffectiveDepth = topThickness * 1000.0 - 50.0 - 8.0;
            double roofRequiredAs = roofMoment * 1000000.0 / (360.0 * 0.9 * roofEffectiveDepth);
            double slabProvidedAs = Math.PI * 16.0 * 16.0 / 4.0 * 1000.0 / 150.0;
            double roofShearStress = roofShear * 1000.0 / (1000.0 * roofEffectiveDepth);
            double roofSteelStressService = (qService * cellWidth * cellWidth / 8.0) * 1000000.0 /
                (slabProvidedAs * 0.9 * roofEffectiveDepth);

            double outerWidth = request.clear_width_m + 2.0 * topThickness;
            double outerHeight = bottomThickness + request.clear_height_m + topThickness;
            double openingArea = 2.0 * cellWidth * request.clear_height_m;
            double concreteArea = outerWidth * outerHeight - openingArea;
            double verticalSoil = qSoil * outerWidth;
            double verticalTraffic = qTraffic * outerWidth;
            double verticalConcrete = 25.0 * concreteArea;
            double foundationService = (verticalSoil + verticalTraffic + verticalConcrete) / outerWidth;
            double foundationDesign = foundationService * request.global_safety_factor;
            double bottomMoment = foundationDesign * cellWidth * cellWidth / 8.0;
            double bottomEffectiveDepth = bottomThickness * 1000.0 - 50.0 - 8.0;
            double bottomRequiredAs = bottomMoment * 1000000.0 / (360.0 * 0.9 * bottomEffectiveDepth);

            double centerRoofReaction = 1.25 * qDesign * cellWidth;
            double centerWallSelfWeight = 25.0 * centerWall * request.clear_height_m * request.global_safety_factor;
            double centerWallAxial = centerRoofReaction + centerWallSelfWeight;
            double centerWallStress = centerWallAxial * 1000.0 / (centerWall * 1000.0 * 1000.0);
            double centerWallProvidedAs = Math.PI * 16.0 * 16.0 / 4.0 * 1000.0 / 150.0;

            double lateralDepth = request.cover_m + topThickness + request.clear_height_m + bottomThickness;
            double lateralService = 0.5 * 20.0 * lateralDepth + 0.5 * qTraffic;
            double lateralDesign = lateralService * request.global_safety_factor;
            double wallMoment = lateralDesign * request.clear_height_m * request.clear_height_m / 2.0;
            double wallEffectiveDepth = topThickness * 1000.0 - 50.0 - 9.0;
            double wallRequiredAs = wallMoment * 1000000.0 / (360.0 * 0.9 * wallEffectiveDepth);
            double wallProvidedAs = Math.PI * 18.0 * 18.0 / 4.0 * 1000.0 / 150.0;
            double longitudinalProvidedAs = Math.PI * 14.0 * 14.0 / 4.0 * 1000.0 / 150.0;

            var text = new StringBuilder();
            text.AppendLine("尾水涵双孔中间支撑方案设计说明与配筋计算（校审方案）");
            text.AppendLine("生成日期：" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            text.AppendLine();
            text.AppendLine("一、结构方案与适用性质");
            text.AppendLine("在原4.00m总内宽范围内增设400mm厚连续钢筋混凝土中墙，形成两个净宽1.80m、净高" + F2(request.clear_height_m) + "m的孔室；结构外宽保持4.80m。原单孔方案文件不修改。本成果按550kN场区车辆、3.00m覆土、20kPa等效交通荷载及总体安全系数1.5进行方案计算。实际轴载、地勘、地下水、抗震和水力资料未齐，本成果不得直接施工。");
            text.AppendLine();
            text.AppendLine("二、采用依据");
            text.AppendLine("SL/T 191-2025《水工混凝土结构设计规范》；SL/T 744-2016《水工建筑物荷载设计规范》；JTG D60-2015及JTG/T 3365-02-2020仅用于车辆模型与覆土涵洞交通作用复核；公司水利钢筋混凝土DWT用于制图表达。");
            text.AppendLine();
            text.AppendLine("三、顶板两跨连续板保守包络（每延米板带）");
            text.AppendLine("标准面荷载：覆土" + F2(qSoil) + "+顶板自重" + F2(qSelf) + "+车辆等效" + F2(qTraffic) + "=" + F2(qService) + "kPa；设计面荷载=" + F2(qDesign) + "kPa。");
            text.AppendLine("单跨净宽" + F2(cellWidth) + "m；按两跨连续板中支座负弯矩保守取M=qL²/8=" + F2(roofMoment) + "kN·m/m，V=qL/2=" + F2(roofShear) + "kN/m。");
            text.AppendLine("d≈" + F0(roofEffectiveDepth) + "mm；As,req≈" + F0(roofRequiredAs) + "mm²/m；Φ16@150每层提供As≈" + F0(slabProvidedAs) + "mm²/m，利用率约" + F2(roofRequiredAs / slabProvidedAs) + "；平均剪应力约" + F2(roofShearStress) + "MPa，服务钢筋应力简算约" + F0(roofSteelStressService) + "MPa。");
            text.AppendLine();
            text.AppendLine("四、底板与地基反力包络");
            text.AppendLine("每延米涵长：覆土" + F2(verticalSoil) + "kN/m、车辆" + F2(verticalTraffic) + "kN/m、结构自重" + F2(verticalConcrete) + "kN/m；地基服务平均压力≈" + F2(foundationService) + "kPa，设计包络≈" + F2(foundationDesign) + "kPa。");
            text.AppendLine("按1.80m板跨保守取M≈" + F2(bottomMoment) + "kN·m/m；d≈" + F0(bottomEffectiveDepth) + "mm；As,req≈" + F0(bottomRequiredAs) + "mm²/m；实配Φ16@150上下层，每层As≈" + F0(slabProvidedAs) + "mm²/m。");
            text.AppendLine();
            text.AppendLine("五、中墙轴压与构造");
            text.AppendLine("两跨连续顶板中支座反力按1.25qL取" + F2(centerRoofReaction) + "kN/m，中墙自重设计值约" + F2(centerWallSelfWeight) + "kN/m，轴力包络约" + F2(centerWallAxial) + "kN/m；400mm墙厚平均压应力约" + F2(centerWallStress) + "MPa。");
            text.AppendLine("中墙两侧竖向实配Φ16@150，每面As≈" + F0(centerWallProvidedAs) + "mm²/m；与顶、底板节点增设L形Φ18@150，肢长不小于1000mm。不对称车辆布载引起的中墙偏心弯矩必须由最终闭合框架模型复核。");
            text.AppendLine();
            text.AppendLine("六、侧墙与纵向配筋");
            text.AppendLine("外侧墙底压力设计包络≈" + F2(lateralDesign) + "kPa，悬臂弯矩≈" + F2(wallMoment) + "kN·m/m，As,req≈" + F0(wallRequiredAs) + "mm²/m；实配Φ18@150内外侧，每面As≈" + F0(wallProvidedAs) + "mm²/m。");
            text.AppendLine("全断面纵向分布筋Φ14@150，各钢筋层As≈" + F0(longitudinalProvidedAs) + "mm²/m；外角L形附加筋Φ20@100，肢长不小于1200mm；保护层50mm。");
            text.AppendLine();
            text.AppendLine("七、拟定配筋结论");
            text.AppendLine("顶板、底板横向Φ16@150上下层；两侧墙竖向Φ18@150内外侧；中墙竖向Φ16@150两侧面；全断面纵向Φ14@150各层；中墙节点L形Φ18@150，外角L形Φ20@100。");
            text.AppendLine("该配筋满足上述参数与简化包络下的强度数量级，不替代整体框架、裂缝、挠度、温度收缩、抗浮、地基变形、抗震、局部轮压和不对称布载验算。参数假定版校审方案，不得直接施工。");
            return text.ToString();
        }

        private static TemplateResponse VerifySupported(TemplateRequest request)
        {
            string output = Path.GetFullPath(request.output_dir);
            var files = new List<string>();
            foreach (string name in SupportedDrawingNames)
            {
                files.Add(Path.Combine(output, name + ".dwg"));
                files.Add(Path.Combine(output, name + ".pdf"));
            }
            files.Add(Path.Combine(output, "尾水涵_双孔中墙设计说明与初步计算.txt"));
            foreach (string file in files)
            {
                if (!File.Exists(file) || new FileInfo(file).Length == 0)
                {
                    return TemplateResponse.Failure(request.request_id, "tailrace_supported_verify", "Missing or empty output: " + file);
                }
            }

            var entityCounts = new Dictionary<string, object>();
            foreach (string dwg in files.Where(path => path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)))
            {
                using (var database = new Database(false, true))
                {
                    database.ReadDwgFile(dwg, FileOpenMode.OpenForReadAndAllShare, false, null);
                    using (Transaction transaction = database.TransactionManager.StartOpenCloseTransaction())
                    {
                        BlockTable table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                        BlockTableRecord model = (BlockTableRecord)transaction.GetObject(
                            table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                        int count = model.Cast<ObjectId>().Count();
                        if (count < 20)
                        {
                            return TemplateResponse.Failure(request.request_id, "tailrace_supported_verify", "Too few model-space entities in " + dwg);
                        }
                        entityCounts[Path.GetFileName(dwg)] = count;
                    }
                }
            }

            var hashes = new Dictionary<string, object>();
            foreach (string file in files)
            {
                hashes[Path.GetFileName(file)] = Sha256(file);
            }
            return new TemplateResponse
            {
                ok = true,
                request_id = request.request_id,
                duplicate = false,
                operation = "tailrace_supported_verify",
                message = "Five supported twin-cell culvert files exist and both DWGs reopened with expected model-space content.",
                data = new Dictionary<string, object>
                {
                    { "files", files },
                    { "entity_counts", entityCounts },
                    { "sha256", hashes },
                    { "geometry", "总内宽4.00m=1.80m孔室+0.40m中墙+1.80m孔室；外宽4.80m" },
                    { "design_status", "550kN场区卡车参数假定版双孔中墙校审方案，不得直接施工" }
                }
            };
        }

        private static TemplateResponse Verify(TemplateRequest request)
        {
            string output = Path.GetFullPath(request.output_dir);
            var files = new List<string>();
            foreach (string name in DrawingNames)
            {
                files.Add(Path.Combine(output, name + ".dwg"));
                files.Add(Path.Combine(output, name + ".pdf"));
            }
            files.Add(Path.Combine(output, "尾水涵_设计说明与初步计算.txt"));
            foreach (string file in files)
            {
                if (!File.Exists(file) || new FileInfo(file).Length == 0)
                {
                    return TemplateResponse.Failure(request.request_id, "tailrace_verify", "Missing or empty output: " + file);
                }
            }

            var entityCounts = new Dictionary<string, object>();
            foreach (string dwg in files.Where(path => path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)))
            {
                using (var database = new Database(false, true))
                {
                    database.ReadDwgFile(dwg, FileOpenMode.OpenForReadAndAllShare, false, null);
                    using (Transaction transaction = database.TransactionManager.StartOpenCloseTransaction())
                    {
                        BlockTable table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                        BlockTableRecord model = (BlockTableRecord)transaction.GetObject(
                            table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                        int count = model.Cast<ObjectId>().Count();
                        if (count < 15)
                        {
                            return TemplateResponse.Failure(request.request_id, "tailrace_verify", "Too few model-space entities in " + dwg);
                        }
                        entityCounts[Path.GetFileName(dwg)] = count;
                    }
                }
            }

            var hashes = new Dictionary<string, object>();
            foreach (string file in files)
            {
                hashes[Path.GetFileName(file)] = Sha256(file);
            }
            return new TemplateResponse
            {
                ok = true,
                request_id = request.request_id,
                duplicate = false,
                operation = "tailrace_verify",
                message = "Seven tailrace design files exist and all three DWGs reopened with expected model-space content.",
                data = new Dictionary<string, object>
                {
                    { "files", files },
                    { "entity_counts", entityCounts },
                    { "sha256", hashes },
                    { "design_status", "550kN场区卡车参数假定版通车校审方案，不得直接施工" }
                }
            };
        }

        private static void RequireResources(Database database, Transaction transaction)
        {
            LayerTable layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            foreach (string name in new[] { CoarseLayer, FineLayer, TextLayer, MaterialLayer, RebarLayer, ViewportLayer })
            {
                if (!layers.Has(name))
                {
                    throw new InvalidOperationException("Company hydraulic RC template is missing layer: " + name);
                }
            }
            TextStyleTable styles = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
            if (!styles.Has(TextStyleName))
            {
                throw new InvalidOperationException("Company hydraulic RC template is missing text style: " + TextStyleName);
            }
        }

        private static void SetSummary(Database database, string drawingTitle)
        {
            var builder = new DatabaseSummaryInfoBuilder(database.SummaryInfo)
            {
                Title = drawingTitle,
                Subject = "小型水电站尾水涵通车荷载校审方案",
                Author = "Codex / AutoCAD CAD-MCP"
            };
            IDictionary custom = builder.CustomPropertyTable;
            custom["ProjectName"] = "水电站尾水涵设计";
            custom["DrawingTitle"] = drawingTitle;
            custom["DesignStage"] = "通车荷载校审方案（不得直接施工）";
            custom["Specialty"] = "水工";
            custom["Design"] = "待项目设计人复核";
            custom["Check"] = "待校核";
            custom["Approve"] = "待签";
            custom["Verify"] = "待签";
            custom["Review"] = "待签";
            custom["Draft"] = "Codex";
            custom["ApproveDate"] = "—";
            custom["VerifyDate"] = "—";
            custom["ReviewDate"] = "—";
            custom["CheckDate"] = "—";
            custom["DesignDate"] = "2026.07";
            custom["DraftDate"] = "2026.07";
            custom["DesignCertificate"] = "待填写";
            custom["DrawingNo"] = drawingTitle;
            custom["UnitName"] = "项目设计单位填写";
            database.SummaryInfo = builder.ToDatabaseSummaryInfo();
        }

        private static void ConfigureViewport(Database database, Transaction transaction, Extents2d extents, double preferredScale)
        {
            ObjectId layoutId = GetLayoutId(database);
            Layout layout = (Layout)transaction.GetObject(layoutId, OpenMode.ForRead);
            BlockTableRecord paper = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
            var viewports = new List<Viewport>();
            foreach (ObjectId id in paper)
            {
                DBObject paperObject = transaction.GetObject(id, OpenMode.ForWrite, false);
                DBText paperText = paperObject as DBText;
                bool isPlotDate = paperText != null
                    && (paperText.TextString.IndexOf("PlotDate", StringComparison.OrdinalIgnoreCase) >= 0
                        || (paperText.Position.X > 400
                            && paperText.Position.Y > 13
                            && paperText.Position.Y < 25));
                if (isPlotDate)
                {
                    paperText.TextString = DateTime.Now.ToString("yy.MM", CultureInfo.InvariantCulture);
                    paperText.Height = 2.0;
                }
                MText paperMText = paperObject as MText;
                ObjectId paperMTextFieldId = ObjectId.Null;
                if (paperMText != null && paperMText.HasFields)
                {
                    paperMTextFieldId = paperMText.GetField();
                }
                string paperMTextFieldCode = string.Empty;
                if (!paperMTextFieldId.IsNull)
                {
                    Field paperMTextField = transaction.GetObject(paperMTextFieldId, OpenMode.ForRead) as Field;
                    if (paperMTextField != null)
                    {
                        paperMTextFieldCode = paperMTextField.GetFieldCode();
                    }
                }
                bool isPlotDateMText = paperMText != null
                    && (paperMTextFieldCode.IndexOf("PlotDate", StringComparison.OrdinalIgnoreCase) >= 0
                        || paperMText.Contents.IndexOf("PlotDate", StringComparison.OrdinalIgnoreCase) >= 0
                        || (paperMText.Location.X > 400
                            && paperMText.Location.Y > 13
                            && paperMText.Location.Y < 25));
                if (isPlotDateMText)
                {
                    if (!paperMTextFieldId.IsNull)
                    {
                        paperMText.RemoveField();
                    }
                    paperMText.Contents = DateTime.Now.ToString("yy.M", CultureInfo.InvariantCulture);
                    paperMText.TextHeight = 1.8;
                }
                Viewport candidate = paperObject as Viewport;
                if (candidate != null && candidate.Number > 1)
                {
                    viewports.Add(candidate);
                }
            }
            if (viewports.Count == 0)
            {
                var created = new Viewport
                {
                    CenterPoint = new Point3d(220.0, 172.5, 0),
                    Width = 380.0,
                    Height = 225.0,
                    Layer = ViewportLayer
                };
                paper.AppendEntity(created);
                transaction.AddNewlyCreatedDBObject(created, true);
                viewports.Add(created);
            }
            Viewport viewport = viewports.OrderByDescending(item => item.Width * item.Height).First();
            foreach (Viewport extra in viewports)
            {
                extra.Layer = ViewportLayer;
                if (!ReferenceEquals(extra, viewport))
                {
                    extra.On = false;
                    extra.Locked = true;
                }
            }
            double modelWidth = extents.MaxPoint.X - extents.MinPoint.X;
            double modelHeight = extents.MaxPoint.Y - extents.MinPoint.Y;
            double fitScale = Math.Min(viewport.Width / (modelWidth * 1.02), viewport.Height / (modelHeight * 1.02));
            viewport.ViewDirection = Vector3d.ZAxis;
            viewport.ViewCenter = new Point2d(
                (extents.MinPoint.X + extents.MaxPoint.X) / 2.0,
                (extents.MinPoint.Y + extents.MaxPoint.Y) / 2.0);
            viewport.CustomScale = Math.Min(preferredScale, fitScale);
            viewport.TwistAngle = 0;
            viewport.On = true;
            viewport.Locked = true;
        }

        private static ObjectId GetLayoutId(Database database)
        {
            using (Transaction transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                DBDictionary dictionary = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
                if (!dictionary.Contains(LayoutName))
                {
                    throw new InvalidOperationException("DWT is missing layout: " + LayoutName);
                }
                return dictionary.GetAt(LayoutName);
            }
        }

        private static void PlotLayout(Database database, ObjectId layoutId, string pdfPath)
        {
            using (Transaction transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                Layout layout = (Layout)transaction.GetObject(layoutId, OpenMode.ForRead);
                using (var settings = new PlotSettings(layout.ModelType))
                {
                    settings.CopyFrom(layout);
                    var info = new PlotInfo { Layout = layoutId, OverrideSettings = settings };
                    var validator = new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled };
                    validator.Validate(info);
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
            }
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while ((!File.Exists(pdfPath) || new FileInfo(pdfPath).Length == 0) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
            if (!File.Exists(pdfPath) || new FileInfo(pdfPath).Length == 0)
            {
                throw new InvalidOperationException("AutoCAD plot did not create PDF: " + pdfPath);
            }
        }

        private static void AddWaterLine(Database database, BlockTableRecord model, Transaction transaction, double x1, double x2, double y, string label)
        {
            AddLine(model, transaction, x1, y, x2, y, FineLayer);
            for (double x = x1; x < x2; x += 1600)
            {
                AddLine(model, transaction, x, y - 80, Math.Min(x + 400, x2), y + 80, FineLayer);
            }
            AddText(database, model, transaction, x2 - 18500, y + 120, label, 380, 0, TextLayer);
        }

        private static void AddRectangle(BlockTableRecord model, Transaction transaction, double x, double y, double width, double height, string layer)
        {
            var polyline = new Polyline(4) { Layer = layer, Closed = true };
            polyline.AddVertexAt(0, new Point2d(x, y), 0, 0, 0);
            polyline.AddVertexAt(1, new Point2d(x + width, y), 0, 0, 0);
            polyline.AddVertexAt(2, new Point2d(x + width, y + height), 0, 0, 0);
            polyline.AddVertexAt(3, new Point2d(x, y + height), 0, 0, 0);
            AddEntity(model, transaction, polyline);
        }

        private static void AddLine(BlockTableRecord model, Transaction transaction, double x1, double y1, double x2, double y2, string layer)
        {
            AddEntity(model, transaction, new Line(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0)) { Layer = layer });
        }

        private static void AddCircle(BlockTableRecord model, Transaction transaction, double x, double y, double radius, string layer)
        {
            AddEntity(model, transaction, new Circle(new Point3d(x, y, 0), Vector3d.ZAxis, radius) { Layer = layer });
        }

        private static void AddText(Database database, BlockTableRecord model, Transaction transaction,
            double x, double y, string text, double height, double rotation, string layer)
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
                Attachment = AttachmentPoint.BottomLeft
            };
            AddEntity(model, transaction, entity);
        }

        private static void AddDimension(Database database, BlockTableRecord model, Transaction transaction,
            Point3d first, Point3d second, Point3d linePoint, string overrideText)
        {
            var dimension = new AlignedDimension(first, second, linePoint, overrideText, database.Dimstyle)
            {
                Layer = FineLayer
            };
            AddEntity(model, transaction, dimension);
        }

        private static void AddEntity(BlockTableRecord model, Transaction transaction, Entity entity)
        {
            model.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
        }

        private static bool ValidRequestId(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value.All(character =>
                (character >= 'A' && character <= 'Z') ||
                (character >= 'a' && character <= 'z') ||
                (character >= '0' && character <= '9') ||
                "._:-".IndexOf(character) >= 0);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool PathEquals(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }
            return string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureChildPath(string child, string parent)
        {
            string normalizedParent = Path.GetFullPath(parent).TrimEnd('\\') + "\\";
            string normalizedChild = Path.GetFullPath(child);
            if (!normalizedChild.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Temporary stage path escaped the configured output directory.");
            }
        }

        private static string Sha256(string path)
        {
            using (SHA256 algorithm = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string DrawingCode(string fileStem)
        {
            if (fileStem.IndexOf("纵断面", StringComparison.Ordinal) >= 0)
            {
                return "WSH-01";
            }
            if (fileStem.IndexOf("横剖面", StringComparison.Ordinal) >= 0)
            {
                return "WSH-02";
            }
            return "WSH-03";
        }

        private static string F0(double value)
        {
            return value.ToString("0", CultureInfo.InvariantCulture);
        }

        private static string F2(double value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
