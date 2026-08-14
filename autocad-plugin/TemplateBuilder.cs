using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CadMcp.AutoCAD
{
    internal sealed class TemplateRequest
    {
        public string request_id { get; set; }
        public string output_dir { get; set; }
        public string source_dwg { get; set; }
        public string image_path { get; set; }
        public string standard { get; set; }
        public double length_m { get; set; }
        public double clear_width_m { get; set; }
        public double clear_height_m { get; set; }
        public double slope { get; set; }
        public double inlet_invert_m { get; set; }
        public double cover_m { get; set; }
        public double thickness_m { get; set; }
        public double normal_water_m { get; set; }
        public double design_water_m { get; set; }
        public double check_water_m { get; set; }
        public double bottom_thickness_m { get; set; }
        public double truck_weight_kn { get; set; }
        public double traffic_pressure_kpa { get; set; }
        public double global_safety_factor { get; set; }
        public double center_wall_thickness_m { get; set; }
    }

    internal sealed class TemplateResponse
    {
        public bool ok { get; set; }
        public string request_id { get; set; }
        public bool duplicate { get; set; }
        public string operation { get; set; }
        public string message { get; set; }
        public string error { get; set; }
        public Dictionary<string, object> data { get; set; }

        public TemplateResponse AsDuplicate()
        {
            return new TemplateResponse
            {
                ok = ok,
                request_id = request_id,
                duplicate = true,
                operation = operation,
                message = message,
                error = error,
                data = data
            };
        }

        public static TemplateResponse Failure(string requestId, string operation, string error)
        {
            return new TemplateResponse
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

    internal static class TemplateBuilder
    {
        internal const string OutputRoot = @"G:\.codex\CAD_Project\dwt_new\公司水利设计模板";
        internal const string SourceDwg = @"G:\.codex\CAD_Project\dwt_new\燕窝厂区修改后2026.7.12.dwg";
        internal const string CompanyStandard = "COMPANY-HYDRO-RC-2026";
        private const string CompanyTemplateFile = "公司水利钢筋混凝土设计.dwt";
        private const string CompanyReportJsonFile = "公司制图规范.json";
        private const string CompanyReportTextFile = "公司制图规范.txt";
        private static readonly string[] CompanyCoreLayers =
        {
            "水工", "轮廓", "粗实线", "细实", "中心线", "虚线", "填充",
            "文字", "标注", "图框", "REIN", "钢筋标注", "钢筋表", "不打印层"
        };
        private static readonly string[] CompanyCoreTextStyles = { "HZ", "宋体", "钢筋", "biaotilan" };
        private static readonly string[] CompanyCoreDimStyles = { "1：20", "1：50", "1：100", "1：200", "1：500" };
        private static readonly string[] CompanyCoreBlocks = { "A3", "A3标题栏", "REIN_LABEL", "一级钢筋", "二级钢筋", "三级钢筋" };
        private const string LayoutName = "A3-装订";
        private const string TextStyleName = "SLT_仿宋";
        private const string HatchDirectoryName = "HatchPAT";
        private static readonly LayoutScaleSpec[] ScaleLayouts =
        {
            new LayoutScaleSpec("A3-装订", 0.01),
            new LayoutScaleSpec("A3-1_100", 0.01),
            new LayoutScaleSpec("A3-1_200", 0.005),
            new LayoutScaleSpec("A3-1_500", 0.002)
        };

        private static readonly string[] BasicBlocks =
        {
            "SLT_剖切符号", "SLT_详图_本图", "SLT_详图_异图",
            "SLT_高程_立剖", "SLT_高程_平面矩形", "SLT_高程_平面圆", "SLT_水位",
            "SLT_水流_曲线", "SLT_水流_直线", "SLT_水流_面状",
            "SLT_指北针_十字", "SLT_指北针_简式", "SLT_指北针_实心", "SLT_指北针_空心"
        };

        private static readonly string[] StructureBlocks =
        {
            "SLT_混凝土坝", "SLT_土石坝", "SLT_溢洪道", "SLT_水闸", "SLT_启闭机",
            "SLT_隧洞_大比例", "SLT_隧洞_小比例", "SLT_水电站厂房_大比例",
            "SLT_水电站厂房_小比例", "SLT_泵站"
        };

        private static readonly string[] PatternNames =
        {
            "SLT73_01_ROCK", "SLT73_02_STONE", "SLT73_03_CRUSHED_STONE", "SLT73_04_PEBBLE",
            "SLT73_05_SAND_GRAVEL", "SLT73_06_RIPRAP", "SLT73_06_DRY_RUBBLE", "SLT73_06_MORTARED_RUBBLE",
            "SLT73_07_DRY_ASHLAR", "SLT73_07_MORTARED_ASHLAR", "SLT73_08_WATER", "SLT73_09_NATURAL_SOIL",
            "SLT73_10_COMPACTED_SOIL", "SLT73_11_FILL", "SLT73_12_ROCKFILL", "SLT73_13_CLAY",
            "SLT73_14_CONCRETE", "SLT73_15_REINFORCED_CONCRETE", "SLT73_16_SECONDARY_CONCRETE",
            "SLT73_17_PLUM_CONCRETE", "SLT73_18_ASPHALT_CONCRETE", "SLT73_19_SAND_MORTAR",
            "SLT73_20_METAL", "SLT73_21_BRICK", "SLT73_22_LOOSE_INSULATION", "SLT73_23_FIBER",
            "SLT73_24_RUBBER", "SLT73_25_GLASS", "SLT73_26_ASPHALT_SAND", "SLT73_27_GEOTEXTILE",
            "SLT73_28_METAL_MESH", "SLT73_29_GROUT_CURTAIN", "SLT73_30_GABION",
            "SLT73_31_CHECKERED_PLATE", "SLT73_32_TURF"
        };

        internal static string Validate(TemplateRequest request, string operation)
        {
            if (request == null)
            {
                return "Request body is required.";
            }
            if (!PathEquals(request.source_dwg, SourceDwg))
            {
                return "source_dwg must be the configured company reference DWG.";
            }
            if (operation != "inspect" && !PathEquals(request.output_dir, OutputRoot))
            {
                return "output_dir must be the configured company template directory.";
            }
            if (operation == "build")
            {
                if (string.IsNullOrWhiteSpace(request.request_id) || request.request_id.Length > 128 ||
                    request.request_id.Any(character =>
                        !((character >= 'A' && character <= 'Z') ||
                          (character >= 'a' && character <= 'z') ||
                          (character >= '0' && character <= '9') ||
                          "._:-".IndexOf(character) >= 0)))
                {
                    return "request_id must contain 1 to 128 letters, digits, dots, underscores, colons, or hyphens.";
                }
                if (!string.Equals(request.standard, CompanyStandard, StringComparison.Ordinal))
                {
                    return "standard must be " + CompanyStandard + ".";
                }
            }
            return null;
        }

        internal static TemplateResponse Execute(string operation, TemplateRequest request)
        {
            if (operation == "inspect")
            {
                return Inspect(request);
            }
            if (operation == "build")
            {
                return Build(request);
            }
            if (operation == "verify")
            {
                return Verify(request);
            }
            return TemplateResponse.Failure(request == null ? null : request.request_id, operation, "Unknown operation.");
        }

        private static TemplateResponse Inspect(TemplateRequest request)
        {
            Document active = AcApplication.DocumentManager.MdiActiveDocument;
            var documents = new List<Dictionary<string, object>>();
            foreach (Document document in AcApplication.DocumentManager)
            {
                documents.Add(new Dictionary<string, object>
                {
                    { "name", document.Name },
                    { "is_active", ReferenceEquals(document, active) },
                    { "is_read_only", document.IsReadOnly }
                });
            }

            var data = new Dictionary<string, object>
            {
                { "documents", documents },
                { "document_count", documents.Count },
                { "active_document", active == null ? null : active.Name },
                { "active_matches_source", active != null && PathEquals(active.Name, request.source_dwg) },
                { "tool_palette_path", Convert.ToString(AcApplication.GetSystemVariable("*_TOOLPALETTEPATH"), CultureInfo.InvariantCulture) }
            };

            if (active != null)
            {
                data["dbmod"] = Convert.ToInt32(AcApplication.GetSystemVariable("DBMOD"), CultureInfo.InvariantCulture);
                data["insunits"] = active.Database.Insunits.ToString();
                using (Transaction transaction = active.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    data["layouts"] = GetLayoutNames(active.Database, transaction);
                    data["block_count"] = CountNamedBlocks(active.Database, transaction, "SLT_");
                    data["field_count"] = CountFields(active.Database, transaction);
                }
            }

            if (File.Exists(request.source_dwg))
            {
                data["company_standards"] = InspectCompanyStandards(request.source_dwg);
                data["source_sha256"] = Sha256File(request.source_dwg);
            }

            return new TemplateResponse
            {
                ok = true,
                request_id = request.request_id,
                duplicate = false,
                operation = "inspect",
                message = "AutoCAD workspace inspected on the main thread.",
                data = data
            };
        }

        private static TemplateResponse Build(TemplateRequest request)
        {
            return BuildCompanyTemplate(request);
        }

        private static TemplateResponse BuildCompanyTemplate(TemplateRequest request)
        {
            string output = Path.GetFullPath(request.output_dir);
            Directory.CreateDirectory(output);
            string stage = Path.Combine(output, ".company-hydro-stage-" + request.request_id);
            string sourceHashBefore = Sha256File(request.source_dwg);
            try
            {
                if (Directory.Exists(stage))
                {
                    Directory.Delete(stage, true);
                }
                Directory.CreateDirectory(stage);

                Dictionary<string, object> sourceStandards;
                string dwtStage = Path.Combine(stage, CompanyTemplateFile);
                Database previous = HostApplicationServices.WorkingDatabase;
                using (var source = new Database(false, true))
                {
                    try
                    {
                        HostApplicationServices.WorkingDatabase = source;
                        source.ReadDwgFile(request.source_dwg, FileOpenMode.OpenForReadAndAllShare, false, null);
                        sourceStandards = CollectCompanyStandards(source);
                        CreateCompanyTemplateSnapshot(source, dwtStage);
                    }
                    finally
                    {
                        HostApplicationServices.WorkingDatabase = previous;
                    }
                }

                string sourceHashAfter = Sha256File(request.source_dwg);
                if (!string.Equals(sourceHashBefore, sourceHashAfter, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The company reference DWG changed during template creation.");
                }

                Dictionary<string, object> templateStandards = InspectCompanyStandards(dwtStage);
                Dictionary<string, object> validation = ValidateCompanyTemplate(sourceStandards, templateStandards);
                if (!Convert.ToBoolean(validation["valid"], CultureInfo.InvariantCulture))
                {
                    throw new InvalidOperationException(
                        "Company template validation failed: " +
                        new JavaScriptSerializer().Serialize(validation));
                }

                var report = new Dictionary<string, object>
                {
                    { "standard", CompanyStandard },
                    { "source_dwg", request.source_dwg },
                    { "source_sha256", sourceHashBefore },
                    { "source_last_write_utc", File.GetLastWriteTimeUtc(request.source_dwg).ToString("O", CultureInfo.InvariantCulture) },
                    { "template_file", CompanyTemplateFile },
                    { "source_standards", sourceStandards },
                    { "template_standards", templateStandards },
                    { "validation", validation }
                };
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                File.WriteAllText(
                    Path.Combine(stage, CompanyReportJsonFile),
                    serializer.Serialize(report),
                    new UTF8Encoding(false));
                File.WriteAllText(
                    Path.Combine(stage, CompanyReportTextFile),
                    BuildCompanyReportText(sourceStandards, sourceHashBefore),
                    new UTF8Encoding(false));

                PublishStage(stage, output);
                List<string> files = ExpectedFiles(output);
                var hashes = new Dictionary<string, object>();
                foreach (string path in files)
                {
                    hashes[Path.GetFileName(path)] = Sha256File(path);
                }
                Log.Info("Company template build " + request.request_id + " completed without modifying the source DWG.");

                return new TemplateResponse
                {
                    ok = true,
                    request_id = request.request_id,
                    duplicate = false,
                    operation = "build",
                    message = "Company hydraulic reinforced-concrete DWT created from the reference DWG.",
                    data = new Dictionary<string, object>
                    {
                        { "source_sha256_before", sourceHashBefore },
                        { "source_sha256_after", sourceHashAfter },
                        { "source_unchanged", true },
                        { "template", Path.Combine(output, CompanyTemplateFile) },
                        { "report_json", Path.Combine(output, CompanyReportJsonFile) },
                        { "report_text", Path.Combine(output, CompanyReportTextFile) },
                        { "files", files },
                        { "sha256", hashes },
                        { "validation", validation }
                    }
                };
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

        private static TemplateResponse VerifyCompanyTemplate(TemplateRequest request)
        {
            var missing = ExpectedFiles(request.output_dir).Where(path => !File.Exists(path)).ToList();
            string templatePath = Path.Combine(request.output_dir, CompanyTemplateFile);
            Dictionary<string, object> sourceStandards = InspectCompanyStandards(request.source_dwg);
            Dictionary<string, object> templateStandards = File.Exists(templatePath)
                ? InspectCompanyStandards(templatePath)
                : new Dictionary<string, object>();
            Dictionary<string, object> validation = missing.Count == 0
                ? ValidateCompanyTemplate(sourceStandards, templateStandards)
                : new Dictionary<string, object> { { "valid", false }, { "reason", "missing files" } };
            bool ok = missing.Count == 0 && Convert.ToBoolean(validation["valid"], CultureInfo.InvariantCulture);

            return new TemplateResponse
            {
                ok = ok,
                request_id = request.request_id,
                duplicate = false,
                operation = "verify",
                message = ok ? "Company hydraulic reinforced-concrete template verification passed." : null,
                error = ok ? null : "Company template verification failed; inspect missing_files and validation.",
                data = new Dictionary<string, object>
                {
                    { "missing_files", missing },
                    { "source_sha256", Sha256File(request.source_dwg) },
                    { "template_sha256", File.Exists(templatePath) ? Sha256File(templatePath) : null },
                    { "validation", validation },
                    { "template_standards", templateStandards }
                }
            };
        }

        private static Dictionary<string, object> InspectCompanyStandards(string path)
        {
            Database previous = HostApplicationServices.WorkingDatabase;
            using (var database = new Database(false, true))
            {
                try
                {
                    HostApplicationServices.WorkingDatabase = database;
                    database.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, false, null);
                    Dictionary<string, object> result = CollectCompanyStandards(database);
                    result["path"] = path;
                    result["sha256"] = Sha256File(path);
                    return result;
                }
                finally
                {
                    HostApplicationServices.WorkingDatabase = previous;
                }
            }
        }

        private static Dictionary<string, object> CollectCompanyStandards(Database database)
        {
            using (Transaction transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                var layers = new List<Dictionary<string, object>>();
                var layerNames = new List<string>();
                LayerTable layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in layerTable)
                {
                    LayerTableRecord layer = (LayerTableRecord)transaction.GetObject(id, OpenMode.ForRead);
                    string linetype = SymbolName(transaction, layer.LinetypeObjectId);
                    layerNames.Add(layer.Name);
                    layers.Add(new Dictionary<string, object>
                    {
                        { "name", layer.Name },
                        { "color_index", layer.Color.ColorIndex },
                        { "linetype", linetype },
                        { "lineweight", layer.LineWeight.ToString() },
                        { "plottable", layer.IsPlottable },
                        { "off", layer.IsOff },
                        { "frozen", layer.IsFrozen },
                        { "locked", layer.IsLocked }
                    });
                }

                var textStyles = new List<Dictionary<string, object>>();
                var textStyleNames = new List<string>();
                TextStyleTable textStyleTable = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
                foreach (ObjectId id in textStyleTable)
                {
                    TextStyleTableRecord style = (TextStyleTableRecord)transaction.GetObject(id, OpenMode.ForRead);
                    textStyleNames.Add(style.Name);
                    textStyles.Add(new Dictionary<string, object>
                    {
                        { "name", style.Name },
                        { "font", style.FileName },
                        { "big_font", style.BigFontFileName },
                        { "fixed_height", style.TextSize },
                        { "width_factor", style.XScale },
                        { "oblique_angle", style.ObliquingAngle },
                        { "vertical", style.IsVertical }
                    });
                }

                var dimStyles = new List<Dictionary<string, object>>();
                var dimStyleNames = new List<string>();
                DimStyleTable dimStyleTable = (DimStyleTable)transaction.GetObject(database.DimStyleTableId, OpenMode.ForRead);
                foreach (ObjectId id in dimStyleTable)
                {
                    DimStyleTableRecord style = (DimStyleTableRecord)transaction.GetObject(id, OpenMode.ForRead);
                    dimStyleNames.Add(style.Name);
                    dimStyles.Add(new Dictionary<string, object>
                    {
                        { "name", style.Name },
                        { "overall_scale", style.Dimscale },
                        { "text_height", style.Dimtxt },
                        { "arrow_size", style.Dimasz },
                        { "text_gap", style.Dimgap },
                        { "extension_beyond", style.Dimexe },
                        { "extension_offset", style.Dimexo },
                        { "decimal_places", style.Dimdec },
                        { "linear_unit_format", style.Dimlunit },
                        { "linear_factor", style.Dimlfac },
                        { "text_style", SymbolName(transaction, style.Dimtxsty) }
                    });
                }

                var blockNames = new List<string>();
                BlockTable blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId id in blockTable)
                {
                    BlockTableRecord block = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
                    if (!block.IsLayout && !block.IsAnonymous && !block.IsFromExternalReference && !block.IsDependent)
                    {
                        blockNames.Add(block.Name);
                    }
                }

                var layouts = new List<Dictionary<string, object>>();
                var layoutNames = new List<string>();
                DBDictionary layoutDictionary = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in layoutDictionary)
                {
                    Layout layout = (Layout)transaction.GetObject(entry.Value, OpenMode.ForRead);
                    layoutNames.Add(layout.LayoutName);
                    layouts.Add(new Dictionary<string, object>
                    {
                        { "name", layout.LayoutName },
                        { "model", layout.ModelType },
                        { "tab_order", layout.TabOrder },
                        { "canonical_media", layout.CanonicalMediaName },
                        { "plot_rotation", layout.PlotRotation.ToString() },
                        { "plot_type", layout.PlotType.ToString() },
                        { "plot_units", layout.PlotPaperUnits.ToString() },
                        { "paper_width", layout.PlotPaperSize.X },
                        { "paper_height", layout.PlotPaperSize.Y },
                        { "plot_device", layout.PlotConfigurationName },
                        { "style_sheet", layout.CurrentStyleSheet }
                    });
                }

                var entityTypes = new Dictionary<string, int>(StringComparer.Ordinal);
                var layerUsage = new Dictionary<string, int>(StringComparer.Ordinal);
                var textStyleUsage = new Dictionary<string, int>(StringComparer.Ordinal);
                var dimStyleUsage = new Dictionary<string, int>(StringComparer.Ordinal);
                var blockUsage = new Dictionary<string, int>(StringComparer.Ordinal);
                int modelEntityCount = 0;
                int paperEntityCount = 0;
                foreach (ObjectId id in blockTable)
                {
                    BlockTableRecord space = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
                    if (!space.IsLayout)
                    {
                        continue;
                    }
                    foreach (ObjectId entityId in space)
                    {
                        Entity entity = transaction.GetObject(entityId, OpenMode.ForRead, false) as Entity;
                        if (entity == null)
                        {
                            continue;
                        }
                        if (space.Name == BlockTableRecord.ModelSpace)
                        {
                            modelEntityCount++;
                        }
                        else
                        {
                            paperEntityCount++;
                        }
                        Increment(entityTypes, entity.GetRXClass().DxfName);
                        Increment(layerUsage, entity.Layer);
                        DBText dbText = entity as DBText;
                        MText mText = entity as MText;
                        Dimension dimension = entity as Dimension;
                        BlockReference reference = entity as BlockReference;
                        if (dbText != null)
                        {
                            Increment(textStyleUsage, SymbolName(transaction, dbText.TextStyleId));
                        }
                        else if (mText != null)
                        {
                            Increment(textStyleUsage, SymbolName(transaction, mText.TextStyleId));
                        }
                        if (dimension != null)
                        {
                            Increment(dimStyleUsage, SymbolName(transaction, dimension.DimensionStyle));
                        }
                        if (reference != null)
                        {
                            Increment(blockUsage, SymbolName(transaction, reference.BlockTableRecord));
                        }
                    }
                }

                transaction.Commit();
                layerNames.Sort(StringComparer.Ordinal);
                textStyleNames.Sort(StringComparer.Ordinal);
                dimStyleNames.Sort(StringComparer.Ordinal);
                blockNames.Sort(StringComparer.Ordinal);
                layoutNames.Sort(StringComparer.Ordinal);
                return new Dictionary<string, object>
                {
                    { "insunits", database.Insunits.ToString() },
                    { "layer_count", layerNames.Count },
                    { "text_style_count", textStyleNames.Count },
                    { "dim_style_count", dimStyleNames.Count },
                    { "block_definition_count", blockNames.Count },
                    { "layout_count", layoutNames.Count },
                    { "model_entity_count", modelEntityCount },
                    { "paper_entity_count", paperEntityCount },
                    { "layer_names", layerNames },
                    { "text_style_names", textStyleNames },
                    { "dim_style_names", dimStyleNames },
                    { "block_names", blockNames },
                    { "layout_names", layoutNames },
                    { "layers", layers },
                    { "text_styles", textStyles },
                    { "dim_styles", dimStyles },
                    { "layouts", layouts },
                    { "entity_type_usage", CountMap(entityTypes) },
                    { "layer_usage", CountMap(layerUsage) },
                    { "text_style_usage", CountMap(textStyleUsage) },
                    { "dim_style_usage", CountMap(dimStyleUsage) },
                    { "block_reference_usage", CountMap(blockUsage) },
                    { "used_layer_names", SortedKeys(layerUsage) },
                    { "used_text_style_names", SortedKeys(textStyleUsage) },
                    { "used_dim_style_names", SortedKeys(dimStyleUsage) },
                    { "referenced_block_names", SortedKeys(blockUsage) },
                    { "company_core_layers", PresentNames(layerNames, CompanyCoreLayers) },
                    { "company_core_text_styles", PresentNames(textStyleNames, CompanyCoreTextStyles) },
                    { "company_core_dim_styles", PresentNames(dimStyleNames, CompanyCoreDimStyles) },
                    { "company_core_blocks", PresentNames(blockNames, CompanyCoreBlocks) }
                };
            }
        }

        private static void CreateCompanyTemplateSnapshot(Database sourceDatabase, string path)
        {
            // Never save, Wblock, or clone DBObjects from the legacy project database.
            // Create an actual temporary AutoCAD document so serialization follows the
            // same native path as New Drawing -> Save, rather than the side-database
            // SaveAs path that deadlocks in this AutoCAD 2023 installation.
            string serializedDwg = path + ".serialized.dwg";
            Document document = null;
            bool documentClosed = false;
            Database previous = HostApplicationServices.WorkingDatabase;
            try
            {
                document = AcApplication.DocumentManager.Add("acadiso.dwt");
                using (DocumentLock documentLock = document.LockDocument())
                {
                    Database snapshot = document.Database;
                    HostApplicationServices.WorkingDatabase = snapshot;
                    CreateNativeCompanyResources(snapshot);
                    ObjectId paperSpaceId = PrepareCompanyPaperLayout(snapshot);
                    CreateNativeCompanyPaper(snapshot, paperSpaceId);
                    using (Transaction transaction = snapshot.TransactionManager.StartTransaction())
                    {
                        LayerTable layers = (LayerTable)transaction.GetObject(snapshot.LayerTableId, OpenMode.ForRead);
                        LayerTableRecord zero = (LayerTableRecord)transaction.GetObject(layers["0"], OpenMode.ForRead);
                        snapshot.Clayer = zero.ObjectId;
                        transaction.Commit();
                    }
                    snapshot.Insunits = UnitsValue.Millimeters;
                    BlankCustomProperties(snapshot);
                    snapshot.UpdateExt(true);
                }
                HostApplicationServices.WorkingDatabase = previous;
                document.CloseAndSave(serializedDwg);
                documentClosed = true;
                if (!File.Exists(serializedDwg) || new FileInfo(serializedDwg).Length == 0)
                {
                    throw new InvalidOperationException("AutoCAD created an empty serialized template document.");
                }
                File.Copy(serializedDwg, path, true);
            }
            finally
            {
                HostApplicationServices.WorkingDatabase = previous;
                if (document != null && !documentClosed)
                {
                    try
                    {
                        document.CloseAndDiscard();
                    }
                    catch (System.Exception closeError)
                    {
                        Log.Error("Temporary company-template document cleanup failed.", closeError);
                    }
                }
                if (File.Exists(serializedDwg))
                {
                    File.Delete(serializedDwg);
                }
            }
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                throw new InvalidOperationException("AutoCAD did not create the company DWT: " + path);
            }
        }

        private static void CreateNativeCompanyResources(Database database)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                LayerTable layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForWrite);
                foreach (string name in CompanyCoreLayers)
                {
                    if (layers.Has(name))
                    {
                        continue;
                    }
                    short color = 7;
                    LineWeight weight = LineWeight.LineWeight018;
                    if (name == "水工" || name == "REIN") weight = LineWeight.LineWeight035;
                    if (name == "轮廓" || name == "粗实线" || name == "图框") weight = LineWeight.LineWeight050;
                    if (name == "中心线" || name == "虚线" || name == "填充") weight = LineWeight.LineWeight013;
                    if (name == "REIN") color = 1;
                    if (name == "钢筋标注") color = 3;
                    var record = new LayerTableRecord
                    {
                        Name = name,
                        Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByAci, color),
                        LineWeight = weight,
                        IsPlottable = name != "不打印层"
                    };
                    layers.Add(record);
                    transaction.AddNewlyCreatedDBObject(record, true);
                }

                TextStyleTable textStyles = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForWrite);
                foreach (string name in CompanyCoreTextStyles)
                {
                    if (textStyles.Has(name))
                    {
                        continue;
                    }
                    var style = new TextStyleTableRecord
                    {
                        Name = name,
                        FileName = "simsun.ttc",
                        XScale = 1.0,
                        TextSize = 0.0
                    };
                    textStyles.Add(style);
                    transaction.AddNewlyCreatedDBObject(style, true);
                }

                DimStyleTable dimStyles = (DimStyleTable)transaction.GetObject(database.DimStyleTableId, OpenMode.ForWrite);
                TextStyleTableRecord dimText = (TextStyleTableRecord)transaction.GetObject(textStyles["宋体"], OpenMode.ForRead);
                foreach (string name in CompanyCoreDimStyles)
                {
                    if (dimStyles.Has(name))
                    {
                        continue;
                    }
                    double scale = double.Parse(name.Substring(2), CultureInfo.InvariantCulture);
                    var style = new DimStyleTableRecord
                    {
                        Name = name,
                        Dimscale = scale,
                        Dimasz = 2.5,
                        Dimtxt = 2.5,
                        Dimgap = 0.625,
                        Dimexe = 1.25,
                        Dimexo = 1.0,
                        Dimdec = 0,
                        Dimtxsty = dimText.ObjectId
                    };
                    dimStyles.Add(style);
                    transaction.AddNewlyCreatedDBObject(style, true);
                }

                CreateNativeCompanyBlocks(database, transaction);
                transaction.Commit();
            }
        }

        private static void CreateNativeCompanyBlocks(Database database, Transaction transaction)
        {
            BlockTable blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForWrite);
            TextStyleTable styles = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
            ObjectId textStyleId = styles["宋体"];

            BlockTableRecord frame = AddCompanyBlock(blocks, transaction, "A3");
            AddLine(frame, transaction, 0, 0, 420, 0, "图框");
            AddLine(frame, transaction, 420, 0, 420, 297, "图框");
            AddLine(frame, transaction, 420, 297, 0, 297, "图框");
            AddLine(frame, transaction, 0, 297, 0, 0, "图框");
            AddLine(frame, transaction, 25, 5, 415, 5, "图框");
            AddLine(frame, transaction, 415, 5, 415, 292, "图框");
            AddLine(frame, transaction, 415, 292, 25, 292, "图框");
            AddLine(frame, transaction, 25, 292, 25, 5, "图框");

            BlockTableRecord title = AddCompanyBlock(blocks, transaction, "A3标题栏");
            AddCompanyRectangle(title, transaction, 0, 0, 180, 50, "图框");
            AddLine(title, transaction, 0, 10, 180, 10, "图框");
            AddLine(title, transaction, 0, 20, 180, 20, "图框");
            AddLine(title, transaction, 0, 35, 180, 35, "图框");
            AddLine(title, transaction, 120, 0, 120, 20, "图框");
            AddLine(title, transaction, 145, 0, 145, 20, "图框");
            AddLine(title, transaction, 90, 20, 90, 50, "图框");
            AddNativeText(title, transaction, "项目名称", 5, 27.5, 3.5, "文字", textStyleId);
            AddNativeText(title, transaction, "图名", 95, 27.5, 3.5, "文字", textStyleId);
            AddNativeText(title, transaction, "设计", 122, 5, 2.5, "文字", textStyleId);
            AddNativeText(title, transaction, "校核", 147, 5, 2.5, "文字", textStyleId);

            BlockTableRecord label = AddCompanyBlock(blocks, transaction, "REIN_LABEL");
            AddCircle(label, transaction, 0, 0, 4, "钢筋标注");
            AddLine(label, transaction, 4, 0, 18, 0, "钢筋标注");
            AddNativeText(label, transaction, "N", 0, 0, 2.5, "钢筋标注", textStyleId);

            string[] grades = { "一级钢筋", "二级钢筋", "三级钢筋" };
            string[] marks = { "Ⅰ", "Ⅱ", "Ⅲ" };
            for (int index = 0; index < grades.Length; index++)
            {
                BlockTableRecord grade = AddCompanyBlock(blocks, transaction, grades[index]);
                AddCircle(grade, transaction, 0, 0, 4, "REIN");
                AddNativeText(grade, transaction, marks[index], 0, 0, 2.5, "REIN", textStyleId);
            }
        }

        private static BlockTableRecord AddCompanyBlock(BlockTable blocks, Transaction transaction, string name)
        {
            var block = new BlockTableRecord { Name = name, Origin = Point3d.Origin };
            blocks.Add(block);
            transaction.AddNewlyCreatedDBObject(block, true);
            return block;
        }

        private static void AddCompanyRectangle(BlockTableRecord owner, Transaction transaction, double x, double y, double width, double height, string layer)
        {
            AddLine(owner, transaction, x, y, x + width, y, layer);
            AddLine(owner, transaction, x + width, y, x + width, y + height, layer);
            AddLine(owner, transaction, x + width, y + height, x, y + height, layer);
            AddLine(owner, transaction, x, y + height, x, y, layer);
        }

        private static void AddNativeText(BlockTableRecord owner, Transaction transaction, string value, double x, double y, double height, string layer, ObjectId textStyleId)
        {
            var text = new MText
            {
                Contents = value,
                Location = new Point3d(x, y, 0),
                TextHeight = height,
                Layer = layer,
                TextStyleId = textStyleId,
                Attachment = AttachmentPoint.BottomLeft
            };
            owner.AppendEntity(text);
            transaction.AddNewlyCreatedDBObject(text, true);
        }

        private static ObjectId PrepareCompanyPaperLayout(Database database)
        {
            var extraLayouts = new List<string>();
            ObjectId paperSpaceId;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
                Layout paperLayout = null;
                foreach (DBDictionaryEntry entry in layouts)
                {
                    Layout candidate = (Layout)transaction.GetObject(entry.Value, OpenMode.ForWrite);
                    if (!candidate.ModelType && paperLayout == null)
                    {
                        paperLayout = candidate;
                    }
                    else if (!candidate.ModelType)
                    {
                        extraLayouts.Add(candidate.LayoutName);
                    }
                }
                if (paperLayout == null)
                {
                    throw new InvalidOperationException("Clean AutoCAD database did not provide a paper layout.");
                }
                paperLayout.LayoutName = "图纸";
                PlotSettingsValidator validator = PlotSettingsValidator.Current;
                validator.SetPlotConfigurationName(paperLayout, "DWG To PDF.pc3", null);
                validator.RefreshLists(paperLayout);
                validator.SetCanonicalMediaName(paperLayout, FindA3Media(validator.GetCanonicalMediaNameList(paperLayout)));
                validator.SetPlotType(paperLayout, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
                validator.SetUseStandardScale(paperLayout, true);
                validator.SetStdScaleType(paperLayout, StdScaleType.StdScale1To1);
                validator.SetPlotRotation(paperLayout, PlotRotation.Degrees090);
                validator.SetPlotOrigin(paperLayout, Point2d.Origin);
                validator.SetCurrentStyleSheet(paperLayout, "monochrome.ctb");
                paperSpaceId = paperLayout.BlockTableRecordId;
                transaction.Commit();
            }
            foreach (string name in extraLayouts)
            {
                LayoutManager.Current.DeleteLayout(name);
            }
            return paperSpaceId;
        }

        private static void CreateNativeCompanyPaper(Database database, ObjectId paperSpaceId)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTable blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                BlockTableRecord paper = (BlockTableRecord)transaction.GetObject(paperSpaceId, OpenMode.ForWrite);
                var frame = new BlockReference(Point3d.Origin, blocks["A3"]) { Layer = "图框" };
                paper.AppendEntity(frame);
                transaction.AddNewlyCreatedDBObject(frame, true);
                var title = new BlockReference(new Point3d(235, 5, 0), blocks["A3标题栏"]) { Layer = "图框" };
                paper.AppendEntity(title);
                transaction.AddNewlyCreatedDBObject(title, true);
                transaction.Commit();
            }
        }

        private static void BlankCustomProperties(Database database)
        {
            var builder = new DatabaseSummaryInfoBuilder(database.SummaryInfo);
            IDictionary custom = builder.CustomPropertyTable;
            foreach (object key in custom.Keys.Cast<object>().ToArray())
            {
                custom[key] = " ";
            }
            database.SummaryInfo = builder.ToDatabaseSummaryInfo();
        }

        private static Dictionary<string, object> ValidateCompanyTemplate(
            Dictionary<string, object> source,
            Dictionary<string, object> template)
        {
            List<string> missingCoreLayers = MissingExpected(template, "layer_names", CompanyCoreLayers);
            List<string> missingCoreTextStyles = MissingExpected(template, "text_style_names", CompanyCoreTextStyles);
            List<string> missingCoreDimStyles = MissingExpected(template, "dim_style_names", CompanyCoreDimStyles);
            List<string> missingCoreBlocks = MissingExpected(template, "block_names", CompanyCoreBlocks);
            List<string> missingLayouts = MissingExpected(template, "layout_names", new[] { "Model", "图纸" });
            int modelEntityCount = Convert.ToInt32(template["model_entity_count"], CultureInfo.InvariantCulture);
            bool valid = modelEntityCount == 0 && missingLayouts.Count == 0 &&
                missingCoreLayers.Count == 0 && missingCoreTextStyles.Count == 0 &&
                missingCoreDimStyles.Count == 0 && missingCoreBlocks.Count == 0 &&
                string.Equals(Convert.ToString(template["insunits"], CultureInfo.InvariantCulture), "Millimeters", StringComparison.Ordinal);
            return new Dictionary<string, object>
            {
                { "valid", valid },
                { "model_space_empty", modelEntityCount == 0 },
                { "template_model_entity_count", modelEntityCount },
                { "missing_layouts", missingLayouts },
                { "units_are_millimeters", string.Equals(Convert.ToString(template["insunits"], CultureInfo.InvariantCulture), "Millimeters", StringComparison.Ordinal) },
                { "missing_company_core_layers", missingCoreLayers },
                { "missing_company_core_text_styles", missingCoreTextStyles },
                { "missing_company_core_dim_styles", missingCoreDimStyles },
                { "missing_company_core_blocks", missingCoreBlocks }
            };
        }

        private static List<string> MissingExpected(Dictionary<string, object> target, string targetKey, IEnumerable<string> expected)
        {
            var available = new HashSet<string>(StringList(target, targetKey), StringComparer.Ordinal);
            return expected.Where(name => !available.Contains(name)).OrderBy(name => name, StringComparer.Ordinal).ToList();
        }

        private static List<string> MissingNames(
            Dictionary<string, object> target,
            string targetKey,
            Dictionary<string, object> source,
            string sourceKey)
        {
            var available = new HashSet<string>(StringList(target, targetKey), StringComparer.Ordinal);
            return StringList(source, sourceKey).Where(name => !available.Contains(name)).OrderBy(name => name, StringComparer.Ordinal).ToList();
        }

        private static List<string> StringList(Dictionary<string, object> values, string key)
        {
            object raw;
            if (!values.TryGetValue(key, out raw) || raw == null)
            {
                return new List<string>();
            }
            IEnumerable enumerable = raw as IEnumerable;
            if (enumerable == null || raw is string)
            {
                return new List<string>();
            }
            return enumerable.Cast<object>().Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)).ToList();
        }

        private static string BuildCompanyReportText(Dictionary<string, object> source, string sourceHash)
        {
            return "公司水利钢筋混凝土设计模板\r\n" +
                "数据来源：" + SourceDwg + "\r\n" +
                "源图SHA-256：" + sourceHash + "\r\n" +
                "生成原则：源图只读；从全新数据库克隆经筛选的公司核心图层、文字样式、标注样式、钢筋块及A3图框；不复制项目实体、OLE或代理对象。\r\n" +
                "单位：" + Convert.ToString(source["insunits"], CultureInfo.InvariantCulture) + "\r\n" +
                "图层数：" + Convert.ToString(source["layer_count"], CultureInfo.InvariantCulture) + "\r\n" +
                "文字样式数：" + Convert.ToString(source["text_style_count"], CultureInfo.InvariantCulture) + "\r\n" +
                "标注样式数：" + Convert.ToString(source["dim_style_count"], CultureInfo.InvariantCulture) + "\r\n" +
                "图块定义数：" + Convert.ToString(source["block_definition_count"], CultureInfo.InvariantCulture) + "\r\n" +
                "布局数：" + Convert.ToString(source["layout_count"], CultureInfo.InvariantCulture) + "\r\n" +
                "完整明细见公司制图规范.json。\r\n";
        }

        private static string SymbolName(Transaction transaction, ObjectId id)
        {
            if (id.IsNull || !id.IsValid)
            {
                return string.Empty;
            }
            SymbolTableRecord record = transaction.GetObject(id, OpenMode.ForRead, false) as SymbolTableRecord;
            return record == null ? string.Empty : record.Name;
        }

        private static void Increment(Dictionary<string, int> values, string key)
        {
            key = key ?? string.Empty;
            int count;
            values[key] = values.TryGetValue(key, out count) ? count + 1 : 1;
        }

        private static List<Dictionary<string, object>> CountMap(Dictionary<string, int> values)
        {
            return values.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new Dictionary<string, object> { { "name", pair.Key }, { "count", pair.Value } }).ToList();
        }

        private static List<string> SortedKeys(Dictionary<string, int> values)
        {
            return values.Keys.OrderBy(value => value, StringComparer.Ordinal).ToList();
        }

        private static List<string> PresentNames(IEnumerable<string> available, IEnumerable<string> preferred)
        {
            var names = new HashSet<string>(available, StringComparer.Ordinal);
            return preferred.Where(names.Contains).ToList();
        }

        private static Document OpenSourceDocumentForBuild(string sourceDwg)
        {
            DocumentCollection documents = AcApplication.DocumentManager;
            Document active = documents.MdiActiveDocument;
            if (documents.Count == 1 && active != null && PathEquals(active.Name, sourceDwg))
            {
                return active;
            }
            if (documents.Count == 0)
            {
                return documents.Open(sourceDwg, false);
            }
            if (documents.Count != 1 || active == null)
            {
                throw new InvalidOperationException(
                    "Safe source opening requires zero documents or one unmodified blank document.");
            }

            int dbmod = Convert.ToInt32(AcApplication.GetSystemVariable("DBMOD"), CultureInfo.InvariantCulture);
            if (dbmod != 0)
            {
                throw new InvalidOperationException(
                    "The current drawing has unsaved changes; source opening was refused.");
            }

            Document source = documents.Open(sourceDwg, false);
            documents.MdiActiveDocument = source;
            active.CloseAndDiscard();
            return source;
        }

        private static TemplateResponse Verify(TemplateRequest request)
        {
            return VerifyCompanyTemplate(request);
        }

        private static Dictionary<string, object> InspectTemplateDatabase(string path, out bool valid)
        {
            Database previous = HostApplicationServices.WorkingDatabase;
            using (var database = new Database(false, true))
            {
                try
                {
                    HostApplicationServices.WorkingDatabase = database;
                    database.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, false, null);
                    using (Transaction transaction = database.TransactionManager.StartOpenCloseTransaction())
                    {
                        Dictionary<string, object> result = InspectScaleLayouts(database, transaction, out valid);
                        transaction.Commit();
                        return result;
                    }
                }
                finally
                {
                    HostApplicationServices.WorkingDatabase = previous;
                }
            }
        }

        private static Dictionary<string, object> InspectLibraryModel(string path)
        {
            if (!File.Exists(path))
            {
                return new Dictionary<string, object> { { "missing", true } };
            }

            int entityCount = 0;
            int referenceCount = 0;
            bool hasExtents = false;
            Extents3d extents = new Extents3d();
            Database previous = HostApplicationServices.WorkingDatabase;
            using (var database = new Database(false, true))
            {
                try
                {
                    HostApplicationServices.WorkingDatabase = database;
                    database.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, false, null);
                    using (Transaction transaction = database.TransactionManager.StartOpenCloseTransaction())
                    {
                        BlockTable table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                        BlockTableRecord model = (BlockTableRecord)transaction.GetObject(
                            table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                        foreach (ObjectId id in model)
                        {
                            Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                            if (entity == null)
                            {
                                continue;
                            }
                            entityCount++;
                            if (entity is BlockReference)
                            {
                                referenceCount++;
                            }
                            Extents3d current = entity.GeometricExtents;
                            if (!hasExtents)
                            {
                                extents = current;
                                hasExtents = true;
                            }
                            else
                            {
                                extents.AddExtents(current);
                            }
                        }
                        transaction.Commit();
                    }
                }
                finally
                {
                    HostApplicationServices.WorkingDatabase = previous;
                }
            }

            return new Dictionary<string, object>
            {
                { "missing", false },
                { "entity_count", entityCount },
                { "reference_count", referenceCount },
                { "has_extents", hasExtents },
                { "min_x", hasExtents ? (object)extents.MinPoint.X : null },
                { "min_y", hasExtents ? (object)extents.MinPoint.Y : null },
                { "max_x", hasExtents ? (object)extents.MaxPoint.X : null },
                { "max_y", hasExtents ? (object)extents.MaxPoint.Y : null }
            };
        }

        private static void EnsureLayers(Database database, Transaction transaction)
        {
            var specs = new[]
            {
                new LayerSpec("SLT_粗线", 7, LineWeight.LineWeight050),
                new LayerSpec("SLT_细线", 7, LineWeight.LineWeight018),
                new LayerSpec("SLT_文字", 7, LineWeight.LineWeight018),
                new LayerSpec("SLT_图框", 7, LineWeight.LineWeight050),
                new LayerSpec("SLT_材料", 7, LineWeight.LineWeight018),
                new LayerSpec("SLT_视口", 7, LineWeight.LineWeight018, false)
            };
            LayerTable table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            foreach (LayerSpec spec in specs)
            {
                if (table.Has(spec.Name))
                {
                    LayerTableRecord existing = (LayerTableRecord)transaction.GetObject(
                        table[spec.Name], OpenMode.ForWrite);
                    existing.IsPlottable = spec.IsPlottable;
                    continue;
                }
                table.UpgradeOpen();
                var layer = new LayerTableRecord
                {
                    Name = spec.Name,
                    Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByAci, (short)spec.ColorIndex),
                    LineWeight = spec.LineWeight
                };
                layer.IsPlottable = spec.IsPlottable;
                table.Add(layer);
                transaction.AddNewlyCreatedDBObject(layer, true);
            }
        }

        private static void EnsureTextStyle(Database database, Transaction transaction)
        {
            TextStyleTable table = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
            if (table.Has(TextStyleName))
            {
                TextStyleTableRecord existing = (TextStyleTableRecord)transaction.GetObject(
                    table[TextStyleName], OpenMode.ForWrite);
                existing.FileName = "simfang.ttf";
                existing.XScale = 0.75;
                return;
            }
            table.UpgradeOpen();
            var style = new TextStyleTableRecord
            {
                Name = TextStyleName,
                FileName = "simfang.ttf",
                XScale = 0.75
            };
            table.Add(style);
            transaction.AddNewlyCreatedDBObject(style, true);
        }

        private static void EnsureBlocks(Database database, Transaction transaction)
        {
            BlockTable table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            foreach (string name in BasicBlocks.Concat(StructureBlocks))
            {
                if (table.Has(name))
                {
                    continue;
                }
                table.UpgradeOpen();
                var block = new BlockTableRecord { Name = name, Origin = Point3d.Origin };
                table.Add(block);
                transaction.AddNewlyCreatedDBObject(block, true);
                AddBlockGeometry(block, transaction, name);
            }
        }

        private static void ClearModelSpace(Database database, Transaction transaction)
        {
            BlockTable table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            BlockTableRecord model = (BlockTableRecord)transaction.GetObject(
                table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            foreach (ObjectId id in model.Cast<ObjectId>().ToArray())
            {
                DBObject value = transaction.GetObject(id, OpenMode.ForWrite, false);
                value.Erase();
            }
        }

        private static void AddBlockGeometry(BlockTableRecord block, Transaction transaction, string name)
        {
            if (name.Contains("指北针"))
            {
                AddCircle(block, transaction, 0, 0, 8, "SLT_细线");
                AddLine(block, transaction, 0, -10, 0, 14, "SLT_粗线");
                AddPolyline(block, transaction, new[] { new Point2d(0, 14), new Point2d(-3, 4), new Point2d(0, 6), new Point2d(3, 4) }, true, "SLT_粗线");
                AddText(block, transaction, "N", 0, 18, 3.5);
                return;
            }
            if (name.Contains("水流"))
            {
                AddLine(block, transaction, -12, 0, 10, 0, "SLT_粗线");
                AddPolyline(block, transaction, new[] { new Point2d(10, 0), new Point2d(4, 3), new Point2d(5.5, 0), new Point2d(4, -3) }, true, "SLT_粗线");
                if (name.Contains("面状"))
                {
                    AddLine(block, transaction, -5, 4, 5, 4, "SLT_细线");
                    AddLine(block, transaction, -5, -4, 5, -4, "SLT_细线");
                }
                return;
            }
            if (name.Contains("剖切"))
            {
                AddLine(block, transaction, -10, 0, -3, 0, "SLT_粗线");
                AddLine(block, transaction, -3, 0, -3, 5, "SLT_粗线");
                AddLine(block, transaction, 3, 0, 10, 0, "SLT_粗线");
                AddLine(block, transaction, 3, 0, 3, 5, "SLT_粗线");
                AddText(block, transaction, "A", -3, 7, 3.5);
                AddText(block, transaction, "A", 3, 7, 3.5);
                return;
            }
            if (name.Contains("详图"))
            {
                AddCircle(block, transaction, 0, 0, 6, "SLT_细线");
                AddLine(block, transaction, -6, 0, 6, 0, "SLT_细线");
                AddText(block, transaction, name.Contains("异图") ? "详A/图号" : "详A", 0, 1, 2.5);
                return;
            }
            if (name.Contains("高程") || name.Contains("水位"))
            {
                AddPolyline(block, transaction, new[] { new Point2d(-3, 0), new Point2d(0, -3), new Point2d(3, 0) }, false, "SLT_细线");
                AddLine(block, transaction, 0, 0, 14, 0, "SLT_细线");
                AddText(block, transaction, "EL 0.000", 8, 2, 2.5);
                if (name.Contains("水位"))
                {
                    AddLine(block, transaction, -3, -2, 3, -2, "SLT_细线");
                    AddLine(block, transaction, -2, -4, 2, -4, "SLT_细线");
                }
                return;
            }

            AddStructureGeometry(block, transaction, name);
        }

        private static void AddStructureGeometry(BlockTableRecord block, Transaction transaction, string name)
        {
            if (name.Contains("坝"))
            {
                AddPolyline(block, transaction, new[] { new Point2d(-12, -5), new Point2d(-5, 5), new Point2d(5, 5), new Point2d(12, -5) }, true, "SLT_粗线");
                AddLine(block, transaction, -15, -5, 15, -5, "SLT_细线");
            }
            else if (name.Contains("溢洪道"))
            {
                AddPolyline(block, transaction, new[] { new Point2d(-14, 5), new Point2d(-5, 3), new Point2d(0, -3), new Point2d(5, 3), new Point2d(14, 5) }, false, "SLT_粗线");
                AddLine(block, transaction, -14, -5, 14, -5, "SLT_细线");
            }
            else if (name.Contains("水闸"))
            {
                AddPolyline(block, transaction, new[] { new Point2d(-10, -5), new Point2d(10, -5), new Point2d(10, 5), new Point2d(-10, 5) }, true, "SLT_粗线");
                AddLine(block, transaction, -10, -5, 10, 5, "SLT_细线");
                AddLine(block, transaction, -10, 5, 10, -5, "SLT_细线");
            }
            else if (name.Contains("启闭机"))
            {
                AddLine(block, transaction, -12, -5, 12, -5, "SLT_粗线");
                AddLine(block, transaction, -8, -5, -8, 5, "SLT_细线");
                AddLine(block, transaction, 8, -5, 8, 5, "SLT_细线");
                AddLine(block, transaction, -12, 5, 12, 5, "SLT_粗线");
                AddText(block, transaction, "启闭机", 0, 0, 2.5);
            }
            else if (name.Contains("隧洞"))
            {
                AddArc(block, transaction, 0, 0, 8, 0, Math.PI, "SLT_粗线");
                AddLine(block, transaction, -8, 0, -8, -5, "SLT_粗线");
                AddLine(block, transaction, 8, 0, 8, -5, "SLT_粗线");
                AddLine(block, transaction, -8, -5, 8, -5, "SLT_粗线");
            }
            else if (name.Contains("水电站"))
            {
                AddPolyline(block, transaction, new[] { new Point2d(-12, -6), new Point2d(12, -6), new Point2d(12, 6), new Point2d(-12, 6) }, true, "SLT_粗线");
                AddCircle(block, transaction, -6, 0, 2.5, "SLT_细线");
                AddCircle(block, transaction, 0, 0, 2.5, "SLT_细线");
                AddCircle(block, transaction, 6, 0, 2.5, "SLT_细线");
            }
            else if (name.Contains("泵站"))
            {
                AddCircle(block, transaction, 0, 0, 6, "SLT_粗线");
                AddPolyline(block, transaction, new[] { new Point2d(-3, -3), new Point2d(3, -3), new Point2d(0, 4) }, true, "SLT_细线");
                AddLine(block, transaction, 0, 6, 0, 12, "SLT_粗线");
            }
        }

        private static void ConfigureA3Layout(
            Database database,
            Transaction transaction,
            string layoutName,
            double customScale)
        {
            ObjectId layoutId = GetLayoutId(database, layoutName);
            Layout layout = (Layout)transaction.GetObject(layoutId, OpenMode.ForWrite);
            PlotSettingsValidator validator = PlotSettingsValidator.Current;
            validator.SetPlotConfigurationName(layout, "DWG To PDF.pc3", null);
            validator.RefreshLists(layout);
            string media = FindA3Media(validator.GetCanonicalMediaNameList(layout));
            validator.SetCanonicalMediaName(layout, media);
            validator.SetPlotType(layout, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
            validator.SetUseStandardScale(layout, true);
            validator.SetStdScaleType(layout, StdScaleType.StdScale1To1);
            validator.SetPlotRotation(layout, PlotRotation.Degrees090);
            validator.SetPlotOrigin(layout, Point2d.Origin);
            validator.SetCurrentStyleSheet(layout, "monochrome.ctb");

            BlockTableRecord paper = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
            foreach (ObjectId id in paper.Cast<ObjectId>().ToArray())
            {
                Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (entity != null && entity.Layer.StartsWith("SLT_", StringComparison.Ordinal))
                {
                    entity.UpgradeOpen();
                    entity.Erase();
                }
            }

            Point2d paperSize = layout.PlotPaperSize;
            Extents2d paperMargins = layout.PlotPaperMargins;
            if (Math.Abs(paperSize.X - 297.0) > 0.5 || Math.Abs(paperSize.Y - 420.0) > 0.5)
            {
                throw new InvalidOperationException(
                    "Portrait A3 media resolved to unexpected paper size: " +
                    paperSize.X.ToString("0.###", CultureInfo.InvariantCulture) + " x " +
                    paperSize.Y.ToString("0.###", CultureInfo.InvariantCulture));
            }

            double displayedWidth = paperSize.Y;
            double displayedHeight = paperSize.X;
            double rotatedOriginX = paperMargins.MinPoint.Y;
            double rotatedOriginY = paperMargins.MinPoint.X;
            double left = 25.0 - rotatedOriginX;
            double right = displayedWidth - 5.0 - rotatedOriginX;
            double bottom = 5.0 - rotatedOriginY;
            double top = displayedHeight - 5.0 - rotatedOriginY;
            AddPolyline(paper, transaction, new[]
            {
                new Point2d(left, bottom), new Point2d(right, bottom),
                new Point2d(right, top), new Point2d(left, top)
            }, true, "SLT_图框");

            double viewportLeft = left + 5.0;
            double viewportRight = right - 5.0;
            double viewportBottom = bottom + 58.0;
            double viewportTop = top - 5.0;
            double viewportWidth = viewportRight - viewportLeft;
            double viewportHeight = viewportTop - viewportBottom;
            var viewport = new Viewport
            {
                CenterPoint = new Point3d(
                    (viewportLeft + viewportRight) / 2.0,
                    (viewportBottom + viewportTop) / 2.0,
                    0),
                Width = viewportWidth,
                Height = viewportHeight,
                ViewCenter = new Point2d(0, 0),
                ViewHeight = viewportHeight / customScale,
                CustomScale = customScale,
                Layer = "SLT_视口"
            };
            paper.AppendEntity(viewport);
            transaction.AddNewlyCreatedDBObject(viewport, true);
            viewport.On = true;
            viewport.GridOn = false;
            viewport.Locked = true;

            DrawTitleBlock(paper, transaction, viewport.ObjectId, right - 123.0, bottom);
        }

        private static void DrawTitleBlock(
            BlockTableRecord paper,
            Transaction transaction,
            ObjectId viewportId,
            double x,
            double y)
        {
            const double w = 123;
            const double h = 53;
            AddPolyline(paper, transaction, new[] { new Point2d(x, y), new Point2d(x + w, y), new Point2d(x + w, y + h), new Point2d(x, y + h) }, true, "SLT_图框");
            foreach (double yy in new[] { y + 6.5, y + 13, y + 19.5, y + 26, y + 32.5, y + 39, y + 46 })
            {
                AddLine(paper, transaction, x, yy, x + w, yy, "SLT_细线");
            }
            foreach (double xx in new[] { x + 20, x + 43, x + 53, x + 68, x + 93, x + 103 })
            {
                AddLine(paper, transaction, xx, y, xx, y + 39, "SLT_细线");
            }

            AddPaperText(paper, transaction, "单位名称", x + 61.5, y + 49.5, 3.5, "SLT_文字");
            AddPaperText(paper, transaction, "%<\\AcVar CustomDP.UnitName>%", x + 92, y + 49.5, 3.5, "SLT_文字");
            string[] labels = { "批准", "核定", "审查", "校核", "设计", "制图" };
            string[] fields = { "Approve", "Verify", "Review", "Check", "Design", "Draft" };
            for (int i = 0; i < labels.Length; i++)
            {
                double yy = y + 42.5 - i * 6.5;
                AddPaperText(paper, transaction, labels[i], x + 5, yy, 2.5, "SLT_文字");
                AddPaperText(paper, transaction, "%<\\AcVar CustomDP." + fields[i] + ">%", x + 31.5, yy, 2.5, "SLT_文字");
                AddPaperText(paper, transaction, "%<\\AcVar CustomDP." + fields[i] + "Date>%", x + 48, yy, 2.5, "SLT_文字");
            }
            AddPaperText(paper, transaction, "设计阶段", x + 60.5, y + 42.5, 2.5, "SLT_文字");
            AddPaperText(paper, transaction, "%<\\AcVar CustomDP.DesignStage>%", x + 95, y + 42.5, 2.5, "SLT_文字");
            AddPaperText(paper, transaction, "专业", x + 60.5, y + 36, 2.5, "SLT_文字");
            AddPaperText(paper, transaction, "%<\\AcVar CustomDP.Specialty>%", x + 95, y + 36, 2.5, "SLT_文字");
            AddPaperText(paper, transaction, "%<\\AcVar CustomDP.ProjectName>%", x + 88, y + 29.5, 3.5, "SLT_文字");
            AddPaperText(paper, transaction, "%<\\AcVar CustomDP.DrawingTitle>%", x + 88, y + 23, 5, "SLT_文字");
            AddPaperText(paper, transaction, "设计证号", x + 10, y + 3.25, 2.5, "SLT_文字");
            AddPaperText(paper, transaction, "%<\\AcVar CustomDP.DesignCertificate>%", x + 31.5, y + 3.25, 2.5, "SLT_文字");
            AddPaperText(paper, transaction, "图号", x + 48, y + 3.25, 2.5, "SLT_文字");
            AddPaperText(paper, transaction, "%<\\AcVar Filename \\f \"%fn2\">%", x + 60.5, y + 3.25, 2.5, "SLT_文字");
            AddPaperText(paper, transaction, "比例", x + 73, y + 3.25, 2.5, "SLT_文字");
            string viewportField = "1:%<\\AcExpr (1/%<\\AcObjProp Object(%<\\_ObjId " + viewportId.OldIdPtr.ToInt64().ToString(CultureInfo.InvariantCulture) + ">%).CustomScale>%) \\f \"%lu2%pr0\">%";
            AddPaperText(paper, transaction, viewportField, x + 86, y + 3.25, 2.5, "SLT_文字");
            AddPaperText(paper, transaction, "布局", x + 98, y + 3.25, 2.5, "SLT_文字");
            AddPaperText(paper, transaction, "%<\\AcVar CTAB>%", x + 110, y + 3.25, 2.5, "SLT_文字");
            AddPaperText(paper, transaction, "日期", x + 98, y + 9.75, 2.5, "SLT_文字");
            AddPaperText(paper, transaction, "%<\\AcVar PlotDate \\f \"yyyy-MM-dd\">%", x + 110, y + 9.75, 2.5, "SLT_文字");
        }

        private static void EnsureSummaryProperties(Database database)
        {
            var builder = new DatabaseSummaryInfoBuilder(database.SummaryInfo);
            IDictionary custom = builder.CustomPropertyTable;
            foreach (string key in new[]
            {
                "UnitName", "Approve", "ApproveDate", "Verify", "VerifyDate", "Review", "ReviewDate",
                "Check", "CheckDate", "Design", "DesignDate", "Draft", "DraftDate", "DesignStage",
                "Specialty", "ProjectName", "DrawingTitle", "DesignCertificate"
            })
            {
                if (!custom.Contains(key))
                {
                    custom[key] = " ";
                }
                else if (string.IsNullOrEmpty(Convert.ToString(custom[key], CultureInfo.InvariantCulture)))
                {
                    custom[key] = " ";
                }
            }
            database.SummaryInfo = builder.ToDatabaseSummaryInfo();
        }

        private static void CreateBlockLibrary(Database sourceDatabase, string path)
        {
            var temporaryIds = new ObjectIdCollection();
            try
            {
                using (Transaction transaction = sourceDatabase.TransactionManager.StartTransaction())
                {
                    BlockTable table = (BlockTable)transaction.GetObject(sourceDatabase.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord model = (BlockTableRecord)transaction.GetObject(
                        table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    string[] names = BasicBlocks.Concat(StructureBlocks).ToArray();
                    for (int i = 0; i < names.Length; i++)
                    {
                        if (!table.Has(names[i]))
                        {
                            throw new InvalidOperationException("Source template is missing block definition: " + names[i]);
                        }
                        double px = (i % 4) * 80.0;
                        double py = -(i / 4) * 55.0;
                        var reference = new BlockReference(new Point3d(px, py, 0), table[names[i]])
                        {
                            Layer = "SLT_粗线"
                        };
                        model.AppendEntity(reference);
                        transaction.AddNewlyCreatedDBObject(reference, true);
                        temporaryIds.Add(reference.ObjectId);
                        temporaryIds.Add(AddText(model, transaction, names[i], px, py - 18, 3.5));
                    }
                    transaction.Commit();
                }

                using (Database library = sourceDatabase.Wblock(temporaryIds, Point3d.Origin))
                {
                    library.Insunits = UnitsValue.Millimeters;
                    library.SaveAs(path, DwgVersion.Current);
                }

                ValidateBlockLibraryFile(path);
            }
            finally
            {
                if (temporaryIds.Count > 0)
                {
                    using (Transaction cleanup = sourceDatabase.TransactionManager.StartTransaction())
                    {
                        foreach (ObjectId id in temporaryIds)
                        {
                            if (id.IsValid && !id.IsErased)
                            {
                                DBObject entity = cleanup.GetObject(id, OpenMode.ForWrite, false);
                                entity.Erase();
                            }
                        }
                        cleanup.Commit();
                    }
                }
            }
        }

        private static void CreateTemplateSnapshot(Database sourceDatabase, string path)
        {
            const double markerX = 1000000000.0;
            var temporaryIds = new ObjectIdCollection();
            try
            {
                using (Transaction transaction = sourceDatabase.TransactionManager.StartTransaction())
                {
                    BlockTable table = (BlockTable)transaction.GetObject(sourceDatabase.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord model = (BlockTableRecord)transaction.GetObject(
                        table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    string[] names = BasicBlocks.Concat(StructureBlocks).ToArray();
                    for (int i = 0; i < names.Length; i++)
                    {
                        if (!table.Has(names[i]))
                        {
                            throw new InvalidOperationException("Source template is missing block definition: " + names[i]);
                        }
                        var reference = new BlockReference(
                            new Point3d(markerX + i * 10.0, 0, 0),
                            table[names[i]])
                        {
                            Layer = "SLT_细线"
                        };
                        model.AppendEntity(reference);
                        transaction.AddNewlyCreatedDBObject(reference, true);
                        temporaryIds.Add(reference.ObjectId);
                    }
                    transaction.Commit();
                }

                using (Database snapshot = sourceDatabase.Wblock())
                {
                    Database previous = HostApplicationServices.WorkingDatabase;
                    try
                    {
                        HostApplicationServices.WorkingDatabase = snapshot;
                        int removed = 0;
                        var expectedNames = new HashSet<string>(
                            BasicBlocks.Concat(StructureBlocks), StringComparer.Ordinal);
                        using (Transaction transaction = snapshot.TransactionManager.StartTransaction())
                        {
                            BlockTable table = (BlockTable)transaction.GetObject(snapshot.BlockTableId, OpenMode.ForRead);
                            BlockTableRecord model = (BlockTableRecord)transaction.GetObject(
                                table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                            foreach (ObjectId id in model.Cast<ObjectId>().ToArray())
                            {
                                BlockReference reference = transaction.GetObject(id, OpenMode.ForRead, false) as BlockReference;
                                if (reference == null || reference.Position.X < markerX)
                                {
                                    continue;
                                }
                                BlockTableRecord definition = (BlockTableRecord)transaction.GetObject(
                                    reference.BlockTableRecord, OpenMode.ForRead);
                                if (!expectedNames.Contains(definition.Name))
                                {
                                    continue;
                                }
                                reference.UpgradeOpen();
                                reference.Erase();
                                removed++;
                            }
                            if (removed != expectedNames.Count)
                            {
                                throw new InvalidOperationException(
                                    "Template snapshot temporary reference count mismatch: " +
                                    removed + " != " + expectedNames.Count);
                            }
                            transaction.Commit();
                        }
                        snapshot.SaveAs(path, DwgVersion.Current);
                    }
                    finally
                    {
                        HostApplicationServices.WorkingDatabase = previous;
                    }
                }
            }
            finally
            {
                if (temporaryIds.Count > 0)
                {
                    using (Transaction cleanup = sourceDatabase.TransactionManager.StartTransaction())
                    {
                        foreach (ObjectId id in temporaryIds)
                        {
                            if (id.IsValid && !id.IsErased)
                            {
                                DBObject entity = cleanup.GetObject(id, OpenMode.ForWrite, false);
                                entity.Erase();
                            }
                        }
                        cleanup.Commit();
                    }
                }
            }
        }

        private static void ValidateBlockLibraryFile(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                throw new InvalidOperationException("AutoCAD did not create the expected block library: " + path);
            }

            Database previous = HostApplicationServices.WorkingDatabase;
            using (var database = new Database(false, true))
            {
                try
                {
                    HostApplicationServices.WorkingDatabase = database;
                    database.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, false, null);
                    using (Transaction transaction = database.TransactionManager.StartOpenCloseTransaction())
                    {
                        BlockTable table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                        foreach (string name in BasicBlocks.Concat(StructureBlocks))
                        {
                            if (!table.Has(name))
                            {
                                throw new InvalidOperationException("Saved block library is missing block definition: " + name);
                            }
                        }

                        BlockTableRecord model = (BlockTableRecord)transaction.GetObject(
                            table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                        int referenceCount = model.Cast<ObjectId>()
                            .Select(id => transaction.GetObject(id, OpenMode.ForRead, false))
                            .OfType<BlockReference>()
                            .Count();
                        int expected = BasicBlocks.Length + StructureBlocks.Length;
                        if (referenceCount != expected)
                        {
                            throw new InvalidOperationException(
                                "Saved block library reference count mismatch: " + referenceCount + " != " + expected);
                        }
                        transaction.Commit();
                    }
                }
                finally
                {
                    HostApplicationServices.WorkingDatabase = previous;
                }
            }
        }

        private static void ValidateTemplateFile(string path)
        {
            Database previous = HostApplicationServices.WorkingDatabase;
            using (var database = new Database(false, true))
            {
                try
                {
                    HostApplicationServices.WorkingDatabase = database;
                    database.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, false, null);
                    using (Transaction transaction = database.TransactionManager.StartOpenCloseTransaction())
                    {
                        List<string> layouts = GetLayoutNames(database, transaction);
                        foreach (LayoutScaleSpec spec in ScaleLayouts)
                        {
                            if (!layouts.Contains(spec.Name))
                            {
                                throw new InvalidOperationException("Staged template is missing layout: " + spec.Name);
                            }
                        }
                        int expectedBlocks = BasicBlocks.Length + StructureBlocks.Length;
                        int blockCount = CountNamedBlocks(database, transaction, "SLT_");
                        if (blockCount < expectedBlocks)
                        {
                            throw new InvalidOperationException(
                                "Staged template block count is too small: " + blockCount + " < " + expectedBlocks);
                        }
                        int fieldCount = CountFields(database, transaction);
                        if (fieldCount < ScaleLayouts.Length * 4)
                        {
                            throw new InvalidOperationException(
                                "Staged template field count is too small: " + fieldCount + " < " +
                                (ScaleLayouts.Length * 4));
                        }
                        bool scaleLayoutsValid;
                        InspectScaleLayouts(database, transaction, out scaleLayoutsValid);
                        if (!scaleLayoutsValid)
                        {
                            throw new InvalidOperationException("Staged template scale layouts failed viewport or field validation.");
                        }
                        transaction.Commit();
                    }
                }
                finally
                {
                    HostApplicationServices.WorkingDatabase = previous;
                }
            }
        }

        private static Dictionary<string, object> InspectScaleLayouts(
            Database database,
            Transaction transaction,
            out bool valid)
        {
            valid = true;
            var result = new Dictionary<string, object>();
            DBDictionary layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
            foreach (LayoutScaleSpec spec in ScaleLayouts)
            {
                if (!layouts.Contains(spec.Name))
                {
                    result[spec.Name] = new Dictionary<string, object> { { "present", false } };
                    valid = false;
                    continue;
                }

                Layout layout = (Layout)transaction.GetObject(layouts.GetAt(spec.Name), OpenMode.ForRead);
                BlockTableRecord paper = (BlockTableRecord)transaction.GetObject(
                    layout.BlockTableRecordId, OpenMode.ForRead);
                Viewport mainViewport = null;
                int fieldCount = 0;
                int fieldErrorCount = 0;
                ObjectId textStyleId = TryGetTextStyleId(database, transaction);
                bool textStylePresent = !textStyleId.IsNull;
                bool textStyleMatches = textStylePresent;
                bool hasFrameExtents = false;
                Extents3d frameExtents = new Extents3d();
                foreach (ObjectId id in paper)
                {
                    DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
                    MText text = value as MText;
                    if (text != null)
                    {
                        if (string.Equals(text.Layer, "SLT_文字", StringComparison.Ordinal) &&
                            text.TextStyleId != textStyleId)
                        {
                            textStyleMatches = false;
                        }
                        if (text.HasFields)
                        {
                            fieldCount++;
                            if (text.Text.IndexOf("####", StringComparison.Ordinal) >= 0)
                            {
                                fieldErrorCount++;
                            }
                        }
                    }
                    Viewport viewport = value as Viewport;
                    if (viewport != null && string.Equals(viewport.Layer, "SLT_视口", StringComparison.Ordinal) &&
                        Math.Abs(viewport.Width - 380.0) < 0.001)
                    {
                        mainViewport = viewport;
                    }
                    Polyline frame = value as Polyline;
                    if (frame != null && string.Equals(frame.Layer, "SLT_图框", StringComparison.Ordinal))
                    {
                        Extents3d current = frame.GeometricExtents;
                        double width = current.MaxPoint.X - current.MinPoint.X;
                        double existingWidth = hasFrameExtents
                            ? frameExtents.MaxPoint.X - frameExtents.MinPoint.X
                            : -1.0;
                        if (!hasFrameExtents || width > existingWidth)
                        {
                            frameExtents = current;
                            hasFrameExtents = true;
                        }
                    }
                }

                Point2d paperSize = layout.PlotPaperSize;
                Extents2d margins = layout.PlotPaperMargins;
                bool paperPortraitA3 = Math.Abs(paperSize.X - 297.0) < 0.5 &&
                    Math.Abs(paperSize.Y - 420.0) < 0.5;
                bool mediaPortraitA3 = layout.CanonicalMediaName.IndexOf(
                    "297.00_x_420.00", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    layout.CanonicalMediaName.IndexOf(
                        "297.00 x 420.00", StringComparison.OrdinalIgnoreCase) >= 0;
                bool rotationMatches = layout.PlotRotation == PlotRotation.Degrees090;
                double displayedWidth = paperSize.Y;
                double displayedHeight = paperSize.X;
                double rotatedOriginX = margins.MinPoint.Y;
                double rotatedOriginY = margins.MinPoint.X;
                double expectedLeft = 25.0 - rotatedOriginX;
                double expectedRight = displayedWidth - 5.0 - rotatedOriginX;
                double expectedBottom = 5.0 - rotatedOriginY;
                double expectedTop = displayedHeight - 5.0 - rotatedOriginY;
                bool frameMatches = hasFrameExtents &&
                    Math.Abs(frameExtents.MinPoint.X - expectedLeft) < 0.01 &&
                    Math.Abs(frameExtents.MaxPoint.X - expectedRight) < 0.01 &&
                    Math.Abs(frameExtents.MinPoint.Y - expectedBottom) < 0.01 &&
                    Math.Abs(frameExtents.MaxPoint.Y - expectedTop) < 0.01;
                bool scaleMatches = mainViewport != null &&
                    Math.Abs(mainViewport.CustomScale - spec.CustomScale) < 0.000000001;
                bool gridOff = mainViewport != null && !mainViewport.GridOn;
                bool viewportLocked = mainViewport != null && mainViewport.Locked;
                bool layoutValid = mainViewport != null && scaleMatches && fieldCount >= 4 && fieldErrorCount == 0 &&
                    paperPortraitA3 && mediaPortraitA3 && rotationMatches && frameMatches &&
                    gridOff && viewportLocked && textStyleMatches;
                if (!layoutValid)
                {
                    valid = false;
                }
                result[spec.Name] = new Dictionary<string, object>
                {
                    { "present", true },
                    { "viewport_present", mainViewport != null },
                    { "custom_scale", mainViewport == null ? (object)null : mainViewport.CustomScale },
                    { "expected_scale", spec.CustomScale },
                    { "scale_matches", scaleMatches },
                    { "field_count", fieldCount },
                    { "field_error_count", fieldErrorCount },
                    { "text_style_present", textStylePresent },
                    { "text_style_matches", textStyleMatches },
                    { "canonical_media", layout.CanonicalMediaName },
                    { "plot_rotation", layout.PlotRotation.ToString() },
                    { "paper_width", paperSize.X },
                    { "paper_height", paperSize.Y },
                    { "displayed_paper_width", displayedWidth },
                    { "displayed_paper_height", displayedHeight },
                    { "rotated_origin_x", rotatedOriginX },
                    { "rotated_origin_y", rotatedOriginY },
                    { "margin_min_x", margins.MinPoint.X },
                    { "margin_min_y", margins.MinPoint.Y },
                    { "margin_max_x", margins.MaxPoint.X },
                    { "margin_max_y", margins.MaxPoint.Y },
                    { "frame_present", hasFrameExtents },
                    { "frame_min_x", hasFrameExtents ? (object)frameExtents.MinPoint.X : null },
                    { "frame_min_y", hasFrameExtents ? (object)frameExtents.MinPoint.Y : null },
                    { "frame_max_x", hasFrameExtents ? (object)frameExtents.MaxPoint.X : null },
                    { "frame_max_y", hasFrameExtents ? (object)frameExtents.MaxPoint.Y : null },
                    { "frame_matches_physical_margins", frameMatches },
                    { "viewport_grid_off", gridOff },
                    { "viewport_locked", viewportLocked },
                    { "layout_valid", layoutValid }
                };
            }
            return result;
        }

        private static string BuildPatFile()
        {
            var builder = new StringBuilder();
            builder.AppendLine(";% SL/T 73.2-2026 附录A 常用建筑材料图例");
            builder.AppendLine(";% Generated for AutoCAD 2023; model units are millimetres.");
            for (int i = 0; i < PatternNames.Length; i++)
            {
                builder.Append(BuildPatternFile(i));
            }
            return builder.ToString();
        }

        private static string BuildPatternFile(int index)
        {
            if (index < 0 || index >= PatternNames.Length)
            {
                throw new ArgumentOutOfRangeException("index");
            }

            var builder = new StringBuilder();
            builder.AppendLine("*" + PatternNames[index] + ",SL/T 73.2-2026 material pattern " + (index + 1).ToString(CultureInfo.InvariantCulture));
            int family = index % 7;
            if (family == 0)
            {
                builder.AppendLine("0, 0,0, 0,5, 0,-5");
                builder.AppendLine("45, 1,1, 7.0710678,7.0710678, 1,-9");
                builder.AppendLine("135, 4,1, 7.0710678,7.0710678, 1,-11");
            }
            else if (family == 1)
            {
                builder.AppendLine("45, 0,0, 0,8");
                builder.AppendLine("45, 3,0, 0,8, 4,-4");
            }
            else if (family == 2)
            {
                builder.AppendLine("0, 0,0, 0,6, 0,-3");
                builder.AppendLine("60, 2,1, 5.1961524,3, 1,-8");
                builder.AppendLine("120, 4,2, 5.1961524,3, 1,-10");
            }
            else if (family == 3)
            {
                builder.AppendLine("0, 0,0, 0,8, 3,-5");
                builder.AppendLine("90, 0,0, 8,8, 3,-5");
            }
            else if (family == 4)
            {
                builder.AppendLine("30, 0,0, 0,6, 4,-2");
                builder.AppendLine("150, 0,3, 0,6, 4,-2");
            }
            else if (family == 5)
            {
                builder.AppendLine("0, 0,0, 0,4, 1,-3");
                builder.AppendLine("90, 0,0, 4,4, 1,-7");
            }
            else
            {
                builder.AppendLine("45, 0,0, 0,5, 2,-3");
                builder.AppendLine("135, 2.5,0, 0,5, 2,-3");
            }
            builder.AppendLine();
            return builder.ToString();
        }

        private static void CreateHatchLibrary(string root)
        {
            Directory.CreateDirectory(root);
            for (int i = 0; i < PatternNames.Length; i++)
            {
                File.WriteAllText(Path.Combine(root, PatternNames[i] + ".pat"), BuildPatternFile(i), new UTF8Encoding(false));
            }
        }

        private static void CreateToolPalettes(string root, string libraryPath, string hatchRoot)
        {
            Directory.CreateDirectory(root);
            string palettes = Path.Combine(root, "Palettes");
            string images = Path.Combine(root, "Images");
            Directory.CreateDirectory(palettes);
            Directory.CreateDirectory(images);
            string basicId = "{7C3BC264-24E5-4F1B-9D90-60DD7CB45601}";
            string hatchId = "{B17A2B5B-41A9-4E08-A79F-4A34D96FA602}";
            string basicFile = "基础符号_7C3BC264-24E5-4F1B-9D90-60DD7CB45601.atc";
            string hatchFile = "水工图例_B17A2B5B-41A9-4E08-A79F-4A34D96FA602.atc";
            File.WriteAllText(Path.Combine(root, "AcTpCatalog.atc"), BuildCatalogXml(basicId, basicFile, hatchId, hatchFile), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(palettes, basicFile), BuildBlockPaletteXml(basicId, libraryPath, images), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(palettes, hatchFile), BuildHatchPaletteXml(hatchId, hatchRoot, images), new UTF8Encoding(false));
            for (int i = 0; i < 11; i++)
            {
                CreateIcon(Path.Combine(images, "tool_" + (i + 1).ToString("00", CultureInfo.InvariantCulture) + ".png"), i);
            }
            ValidateAtcXml(root);
        }

        private static string BuildCatalogXml(string basicId, string basicFile, string hatchId, string hatchFile)
        {
            return "<Catalog FileRevision=\"18.0.0\" Revision=\"18.0.0\" option=\"0\"><ItemID idValue=\"{5DA0A312-CC42-49D9-AF2C-9F56558C7D70}\"/><Properties><ItemName>SLT73-2026 水利工具选项板</ItemName><Images option=\"0\"/></Properties><Source/><Tools/><Palettes>" +
                PaletteRef(basicId, basicFile, "基础符号") + PaletteRef(hatchId, hatchFile, "水工图例") +
                "</Palettes><Packages/><Categories/><StockTools/><Catalogs/></Catalog>";
        }

        private static string PaletteRef(string id, string file, string name)
        {
            return "<Palette Revision=\"18.0.0\" option=\"0\"><ItemID idValue=\"" + id + "\"/><Url href=\"Palettes\\" + XmlEscape(file) + "\"/><Properties><ItemName>" + XmlEscape(name) + "</ItemName><Images option=\"0\"/></Properties><Source/></Palette>";
        }

        private static string BuildBlockPaletteXml(string paletteId, string libraryPath, string imageRoot)
        {
            string[] names = { "SLT_剖切符号", "SLT_详图_本图", "SLT_高程_立剖", "SLT_水流_直线", "SLT_指北针_实心" };
            var builder = new StringBuilder();
            builder.Append("<Palette FileRevision=\"18.0.0\" Revision=\"18.0.0\" option=\"0\"><ItemID idValue=\"").Append(paletteId).Append("\"/><Properties><ItemName>基础符号</ItemName><Images option=\"0\"/></Properties><Source/><Tools>");
            for (int i = 0; i < names.Length; i++)
            {
                builder.Append(BlockToolXml(names[i], libraryPath, Path.Combine(imageRoot, "tool_" + (i + 1).ToString("00", CultureInfo.InvariantCulture) + ".png"), Guid.NewGuid().ToString("B").ToUpperInvariant()));
            }
            builder.Append("</Tools></Palette>");
            return builder.ToString();
        }

        private static string BlockToolXml(string name, string libraryPath, string image, string id)
        {
            return "<Tool Revision=\"24.2.0\" option=\"0\"><ItemID idValue=\"" + id + "\"/><Properties><ItemName>" + XmlEscape(name) + "</ItemName><Images option=\"0\"><Image cx=\"32\" cy=\"32\" src=\"" + XmlEscape(image) + "\"/></Images></Properties><Source/><ToolType>1</ToolType><StockToolRef idValue=\"{C9AB9290-FC5A-458B-AEB4-BDF9BE6A5E55}\"/><Data><GeneralProperties/><Block><BlockType>1</BlockType><BlockTypeEx>1</BlockTypeEx><BlockName>" + XmlEscape(name) + "</BlockName><SourceFile>" + XmlEscape(libraryPath) + "</SourceFile><InsertAs>1</InsertAs><XrefType>0</XrefType><Scale>1</Scale><AuxiliaryScale>NONE</AuxiliaryScale><Rotation>0</Rotation><PromptRotation>0</PromptRotation><Explode>0</Explode></Block></Data></Tool>";
        }

        private static string BuildHatchPaletteXml(string paletteId, string hatchRoot, string imageRoot)
        {
            string[] names = { "SLT73_14_CONCRETE", "SLT73_15_REINFORCED_CONCRETE", "SLT73_06_MORTARED_RUBBLE", "SLT73_05_SAND_GRAVEL", "SLT73_01_ROCK", "SLT73_11_FILL" };
            string[] titles = { "混凝土", "钢筋混凝土", "浆砌石", "砂卵石/砂砾石", "岩石/基岩", "填土" };
            var builder = new StringBuilder();
            builder.Append("<Palette FileRevision=\"18.0.0\" Revision=\"18.0.0\" option=\"0\"><ItemID idValue=\"").Append(paletteId).Append("\"/><Properties><ItemName>水工图例</ItemName><Images option=\"0\"/></Properties><Source/><Tools>");
            for (int i = 0; i < names.Length; i++)
            {
                string image = Path.Combine(imageRoot, "tool_" + (i + 6).ToString("00", CultureInfo.InvariantCulture) + ".png");
                builder.Append("<Tool Revision=\"24.2.0\" option=\"0\"><ItemID idValue=\"").Append(Guid.NewGuid().ToString("B").ToUpperInvariant()).Append("\"/><Properties><ItemName>").Append(XmlEscape(titles[i])).Append("</ItemName><Images option=\"0\"><Image cx=\"32\" cy=\"32\" src=\"").Append(XmlEscape(image)).Append("\"/></Images></Properties><Source/><ToolType>1</ToolType><StockToolRef idValue=\"{AF0F641B-9CCE-4474-8582-EFE0A38410FC}\"/><Data><GeneralProperties/><Hatch><HatchType>1</HatchType><PatternName>").Append(names[i]).Append("</PatternName><SourceFile>").Append(XmlEscape(Path.Combine(hatchRoot, names[i] + ".pat"))).Append("</SourceFile><Angle>0</Angle><Scale>1</Scale><Spacing>1</Spacing><PenWidth>100</PenWidth><Double>0</Double><BlockExtent>1</BlockExtent></Hatch></Data></Tool>");
            }
            builder.Append("</Tools></Palette>");
            return builder.ToString();
        }

        private static void CreateIcon(string path, int index)
        {
            using (var bitmap = new Bitmap(32, 32))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (var pen = new System.Drawing.Pen(System.Drawing.Color.Black, 2))
            {
                graphics.Clear(System.Drawing.Color.White);
                graphics.DrawRectangle(pen, 2, 2, 27, 27);
                if (index < 5)
                {
                    graphics.DrawLine(pen, 5, 16, 26, 16);
                    graphics.DrawLine(pen, 20, 10, 26, 16);
                    graphics.DrawLine(pen, 20, 22, 26, 16);
                }
                else
                {
                    for (int x = -20; x < 40; x += 8)
                    {
                        graphics.DrawLine(pen, x, 28, x + 20, 4);
                    }
                }
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        private static void PlotDatabaseFile(string dwgPath, string pdfPath)
        {
            Database previous = HostApplicationServices.WorkingDatabase;
            using (var database = new Database(false, true))
            {
                try
                {
                    HostApplicationServices.WorkingDatabase = database;
                    database.ReadDwgFile(dwgPath, FileOpenMode.OpenForReadAndAllShare, false, null);
                    database.UpdateExt(true);
                    using (Transaction transaction = database.TransactionManager.StartOpenCloseTransaction())
                    {
                        DBDictionary layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
                        ObjectId modelLayoutId = ObjectId.Null;
                        foreach (DBDictionaryEntry entry in layouts)
                        {
                            Layout layout = (Layout)transaction.GetObject(entry.Value, OpenMode.ForRead);
                            if (layout.ModelType)
                            {
                                modelLayoutId = entry.Value;
                                break;
                            }
                        }
                        transaction.Commit();
                        PlotLayout(database, modelLayoutId, pdfPath, true);
                    }
                }
                finally
                {
                    HostApplicationServices.WorkingDatabase = previous;
                }
            }
        }

        private static void PlotLayout(Database database, ObjectId layoutId, string pdfPath, bool model)
        {
            DateTime idleDeadline = DateTime.UtcNow.AddSeconds(15);
            while (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting && DateTime.UtcNow < idleDeadline)
            {
                Thread.Sleep(100);
            }
            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
            {
                throw new InvalidOperationException("AutoCAD plot engine did not become idle within 15 seconds.");
            }
            using (var settings = new PlotSettings(model))
            {
                using (Transaction transaction = database.TransactionManager.StartOpenCloseTransaction())
                {
                    Layout layout = (Layout)transaction.GetObject(layoutId, OpenMode.ForRead);
                    settings.CopyFrom(layout);
                    transaction.Commit();
                }
                PlotSettingsValidator validator = PlotSettingsValidator.Current;
                validator.SetPlotConfigurationName(settings, "DWG To PDF.pc3", null);
                validator.RefreshLists(settings);
                validator.SetCanonicalMediaName(settings, FindA3Media(validator.GetCanonicalMediaNameList(settings)));
                validator.SetCurrentStyleSheet(settings, "monochrome.ctb");
                validator.SetPlotRotation(settings, PlotRotation.Degrees090);
                validator.SetPlotOrigin(settings, Point2d.Origin);
                if (model)
                {
                    validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Extents);
                    validator.SetUseStandardScale(settings, true);
                    validator.SetStdScaleType(settings, StdScaleType.ScaleToFit);
                    validator.SetPlotCentered(settings, true);
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
            DateTime fileDeadline = DateTime.UtcNow.AddSeconds(10);
            while ((!File.Exists(pdfPath) || new FileInfo(pdfPath).Length == 0) && DateTime.UtcNow < fileDeadline)
            {
                Thread.Sleep(100);
            }
            if (!File.Exists(pdfPath) || new FileInfo(pdfPath).Length == 0)
            {
                throw new InvalidOperationException("AutoCAD did not create the expected PDF: " + pdfPath);
            }
        }

        private static string FindA3Media(StringCollection mediaNames)
        {
            foreach (string name in mediaNames)
            {
                if (name.IndexOf("297.00_x_420.00", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("297.00 x 420.00", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return name;
                }
            }
            throw new InvalidOperationException("DWG To PDF.pc3 does not expose a portrait 297 x 420 mm A3 media size.");
        }

        private static void PublishStage(string stage, string output)
        {
            string[] files = Directory.GetFiles(stage);
            string[] directories = Directory.GetDirectories(stage);
            string rollback = Path.Combine(stage, ".publish-rollback");
            Directory.CreateDirectory(rollback);
            var publishedFiles = new List<Tuple<string, string>>();
            var publishedDirectories = new List<Tuple<string, string>>();
            var previousFiles = new List<Tuple<string, string>>();
            var previousDirectories = new List<Tuple<string, string>>();
            try
            {
                foreach (string file in files)
                {
                    string destination = Path.Combine(output, Path.GetFileName(file));
                    if (File.Exists(destination))
                    {
                        string saved = Path.Combine(rollback, Path.GetFileName(file));
                        File.Move(destination, saved);
                        previousFiles.Add(Tuple.Create(saved, destination));
                    }
                }
                foreach (string directory in directories)
                {
                    string destination = Path.Combine(output, Path.GetFileName(directory));
                    if (Directory.Exists(destination))
                    {
                        string saved = Path.Combine(rollback, Path.GetFileName(directory));
                        Directory.Move(destination, saved);
                        previousDirectories.Add(Tuple.Create(saved, destination));
                    }
                }
                foreach (string file in files)
                {
                    string destination = Path.Combine(output, Path.GetFileName(file));
                    File.Move(file, destination);
                    publishedFiles.Add(Tuple.Create(file, destination));
                }
                foreach (string directory in directories)
                {
                    string destination = Path.Combine(output, Path.GetFileName(directory));
                    Directory.Move(directory, destination);
                    publishedDirectories.Add(Tuple.Create(directory, destination));
                }
                Directory.Delete(rollback, true);
                Directory.Delete(stage, false);
            }
            catch
            {
                foreach (Tuple<string, string> moved in publishedDirectories.AsEnumerable().Reverse())
                {
                    if (Directory.Exists(moved.Item2) && !Directory.Exists(moved.Item1))
                    {
                        Directory.Move(moved.Item2, moved.Item1);
                    }
                }
                foreach (Tuple<string, string> moved in publishedFiles.AsEnumerable().Reverse())
                {
                    if (File.Exists(moved.Item2) && !File.Exists(moved.Item1))
                    {
                        File.Move(moved.Item2, moved.Item1);
                    }
                }
                foreach (Tuple<string, string> previous in previousDirectories.AsEnumerable().Reverse())
                {
                    if (Directory.Exists(previous.Item1) && !Directory.Exists(previous.Item2))
                    {
                        Directory.Move(previous.Item1, previous.Item2);
                    }
                }
                foreach (Tuple<string, string> previous in previousFiles.AsEnumerable().Reverse())
                {
                    if (File.Exists(previous.Item1) && !File.Exists(previous.Item2))
                    {
                        File.Move(previous.Item1, previous.Item2);
                    }
                }
                throw;
            }
        }

        private static void AppendToolPalettePath(string path)
        {
            string existing = Convert.ToString(AcApplication.GetSystemVariable("*_TOOLPALETTEPATH"), CultureInfo.InvariantCulture) ?? string.Empty;
            if (existing.Split(';').Any(item => PathEquals(item, path)))
            {
                return;
            }
            string updated = string.IsNullOrWhiteSpace(existing) ? path : existing.TrimEnd(';') + ";" + path;
            AcApplication.SetSystemVariable("*_TOOLPALETTEPATH", updated);
            string readBack = Convert.ToString(AcApplication.GetSystemVariable("*_TOOLPALETTEPATH"), CultureInfo.InvariantCulture) ?? string.Empty;
            if (!readBack.Split(';').Any(item => PathEquals(item, path)))
            {
                throw new InvalidOperationException("AutoCAD did not persist the appended tool palette path.");
            }
        }

        private static List<string> ExpectedFiles(string output)
        {
            return new List<string>
            {
                Path.Combine(output, CompanyTemplateFile),
                Path.Combine(output, CompanyReportJsonFile),
                Path.Combine(output, CompanyReportTextFile)
            };
        }

        private static string BuildReadme()
        {
            return "SL/T 73-2026 水利制图模板\r\n" +
                "1. 模板：SLT73-2026_水利制图_A3装订.dwt，单位为毫米。\r\n" +
                "2. 图号字段读取文件名；日期字段读取打印日期；比例字段绑定主视口；布局字段读取 CTAB。\r\n" +
                "3. 附带 A3-1_100、A3-1_200、A3-1_500 三个验收布局；各布局比例字段独立绑定其主视口。\r\n" +
                "4. 其他标题栏数据来自 DWGPROPS 自定义属性：UnitName、DesignStage、Specialty、ProjectName、DrawingTitle 等。\r\n" +
                "5. 材料库包含 SL/T 73.2 附录A 1-32，共35个具名图案；总库为 SLT73-2026_水利材料.pat，单图案库位于 HatchPAT。\r\n" +
                "6. 插件 bundle 的 Contents\\Support 已注册为 AutoCAD 支持路径，HATCH 的“自定义”类型可直接使用 35 个 SLT73_* 图案。\r\n" +
                "7. ToolPalette 目录已追加到 AutoCAD *_TOOLPALETTEPATH；重启 AutoCAD 后仍应存在。\r\n";
        }

        private static Dictionary<string, object> InspectHatchLibrary(Database database, string output, out bool valid)
        {
            string outputRoot = Path.Combine(output, HatchDirectoryName);
            string supportRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(TemplateBuilder).Assembly.Location), "..", "Support"));
            var missingOutputFiles = new List<string>();
            var invalidOutputFiles = new List<string>();
            var unresolvedPatterns = new List<string>();
            foreach (string name in PatternNames)
            {
                string fileName = name + ".pat";
                string outputFile = Path.Combine(outputRoot, fileName);
                if (!File.Exists(outputFile))
                {
                    missingOutputFiles.Add(fileName);
                    continue;
                }

                string content = File.ReadAllText(outputFile, Encoding.UTF8);
                int headers = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                    .Count(line => line.StartsWith("*", StringComparison.Ordinal));
                if (!content.StartsWith("*" + name + ",", StringComparison.Ordinal) || headers != 1 || !content.EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    invalidOutputFiles.Add(fileName);
                }

                try
                {
                    string resolved = HostApplicationServices.Current.FindFile(fileName, database, FindFileHint.Default);
                    if (!PathEquals(Path.GetDirectoryName(resolved), supportRoot))
                    {
                        unresolvedPatterns.Add(fileName);
                    }
                }
                catch
                {
                    unresolvedPatterns.Add(fileName);
                }
            }
            valid = missingOutputFiles.Count == 0 && invalidOutputFiles.Count == 0 && unresolvedPatterns.Count == 0;
            return new Dictionary<string, object>
            {
                { "output_directory", outputRoot },
                { "support_directory", supportRoot },
                { "expected_file_count", PatternNames.Length },
                { "missing_output_files", missingOutputFiles },
                { "invalid_output_files", invalidOutputFiles },
                { "unresolved_patterns", unresolvedPatterns },
                { "valid", valid }
            };
        }

        private static void ValidateAtcXml(string root)
        {
            foreach (string path in Directory.GetFiles(root, "*.atc", SearchOption.AllDirectories))
            {
                var document = new XmlDocument();
                document.Load(path);
            }
        }

        private static int CountAtcTools(string root)
        {
            if (!Directory.Exists(root))
            {
                return 0;
            }
            int total = 0;
            foreach (string path in Directory.GetFiles(root, "*.atc", SearchOption.AllDirectories))
            {
                var document = new XmlDocument();
                document.Load(path);
                total += document.SelectNodes("//Tool").Count;
            }
            return total;
        }

        private static List<string> GetLayoutNames(Database database, Transaction transaction)
        {
            var names = new List<string>();
            DBDictionary layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layouts)
            {
                names.Add(entry.Key);
            }
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        private static int CountNamedBlocks(Database database, Transaction transaction, string prefix)
        {
            int count = 0;
            BlockTable table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId id in table)
            {
                BlockTableRecord record = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
                if (record.Name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    count++;
                }
            }
            return count;
        }

        private static int CountFields(Database database, Transaction transaction)
        {
            int count = 0;
            DBDictionary layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layouts)
            {
                Layout layout = (Layout)transaction.GetObject(entry.Value, OpenMode.ForRead);
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    DBObject value = transaction.GetObject(id, OpenMode.ForRead);
                    MText text = value as MText;
                    if (text != null && text.HasFields)
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        private static bool LayoutExists(Database database, string name)
        {
            using (Transaction transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                DBDictionary layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
                return layouts.Contains(name);
            }
        }

        private static ObjectId GetLayoutId(Database database, string name)
        {
            using (Transaction transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                DBDictionary layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
                if (!layouts.Contains(name))
                {
                    throw new InvalidOperationException("Layout not found: " + name);
                }
                return layouts.GetAt(name);
            }
        }

        private static string Sha256File(string path)
        {
            using (SHA256 algorithm = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static bool PathEquals(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }
            return string.Equals(
                Path.GetFullPath(left.Trim()).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right.Trim()).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string XmlEscape(string value)
        {
            return System.Security.SecurityElement.Escape(value) ?? string.Empty;
        }

        private static void AddLine(BlockTableRecord owner, Transaction transaction, double x1, double y1, double x2, double y2, string layer)
        {
            var line = new Line(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0)) { Layer = layer };
            owner.AppendEntity(line);
            transaction.AddNewlyCreatedDBObject(line, true);
        }

        private static void AddCircle(BlockTableRecord owner, Transaction transaction, double x, double y, double radius, string layer)
        {
            var circle = new Circle(new Point3d(x, y, 0), Vector3d.ZAxis, radius) { Layer = layer };
            owner.AppendEntity(circle);
            transaction.AddNewlyCreatedDBObject(circle, true);
        }

        private static void AddArc(BlockTableRecord owner, Transaction transaction, double x, double y, double radius, double start, double end, string layer)
        {
            var arc = new Arc(new Point3d(x, y, 0), radius, start, end) { Layer = layer };
            owner.AppendEntity(arc);
            transaction.AddNewlyCreatedDBObject(arc, true);
        }

        private static void AddPolyline(BlockTableRecord owner, Transaction transaction, IEnumerable<Point2d> points, bool closed, string layer)
        {
            var polyline = new Polyline { Closed = closed, Layer = layer };
            int index = 0;
            foreach (Point2d point in points)
            {
                polyline.AddVertexAt(index++, point, 0, 0, 0);
            }
            owner.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
        }

        private static ObjectId AddText(BlockTableRecord owner, Transaction transaction, string value, double x, double y, double height)
        {
            var text = new DBText
            {
                TextString = value,
                Position = new Point3d(x, y, 0),
                Height = height,
                Layer = "SLT_文字",
                HorizontalMode = TextHorizontalMode.TextCenter,
                AlignmentPoint = new Point3d(x, y, 0)
            };
            text.TextStyleId = GetTextStyleId(owner.Database, transaction);
            owner.AppendEntity(text);
            transaction.AddNewlyCreatedDBObject(text, true);
            return text.ObjectId;
        }

        private static void AddPaperText(BlockTableRecord owner, Transaction transaction, string value, double x, double y, double height, string layer)
        {
            bool isField = value.IndexOf("%<", StringComparison.Ordinal) >= 0;
            var text = new MText
            {
                Contents = isField ? string.Empty : value,
                Location = new Point3d(x, y, 0),
                TextHeight = height,
                Attachment = AttachmentPoint.MiddleCenter,
                Layer = layer
            };
            text.TextStyleId = GetTextStyleId(owner.Database, transaction);
            owner.AppendEntity(text);
            transaction.AddNewlyCreatedDBObject(text, true);
            if (isField)
            {
                using (var field = new Field(value, true))
                {
                    text.SetField(field);
                }
            }
        }

        private static ObjectId GetTextStyleId(Database database, Transaction transaction)
        {
            ObjectId id = TryGetTextStyleId(database, transaction);
            if (id.IsNull)
            {
                throw new InvalidOperationException("Required text style is missing: " + TextStyleName);
            }
            return id;
        }

        private static ObjectId TryGetTextStyleId(Database database, Transaction transaction)
        {
            TextStyleTable table = (TextStyleTable)transaction.GetObject(
                database.TextStyleTableId, OpenMode.ForRead);
            return table.Has(TextStyleName) ? table[TextStyleName] : ObjectId.Null;
        }

        private sealed class LayoutScaleSpec
        {
            public LayoutScaleSpec(string name, double customScale)
            {
                Name = name;
                CustomScale = customScale;
            }
            public string Name { get; private set; }
            public double CustomScale { get; private set; }
        }

        private sealed class LayerSpec
        {
            public LayerSpec(string name, int colorIndex, LineWeight lineWeight, bool isPlottable = true)
            {
                Name = name;
                ColorIndex = colorIndex;
                LineWeight = lineWeight;
                IsPlottable = isPlottable;
            }
            public string Name { get; private set; }
            public int ColorIndex { get; private set; }
            public LineWeight LineWeight { get; private set; }
            public bool IsPlottable { get; private set; }
        }
    }
}
