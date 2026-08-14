using System;
using System.Reflection;

namespace CadMcp.ProtocolTests
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                Assembly assembly = Assembly.Load("CadMcp.AutoCAD");
                Type requestType = assembly.GetType("CadMcp.AutoCAD.TemplateRequest", true);
                Type builderType = assembly.GetType("CadMcp.AutoCAD.TemplateBuilder", true);
                Type translationBuilderType = assembly.GetType("CadMcp.AutoCAD.CadTranslationBuilder", true);
                MethodInfo saveDatabaseAs = translationBuilderType.GetMethod(
                    "SaveDatabaseAs", BindingFlags.NonPublic | BindingFlags.Static);
                if (saveDatabaseAs == null)
                {
                    throw new InvalidOperationException("CadTranslationBuilder.SaveDatabaseAs was not found.");
                }
                MethodInfo resolveReplacement = translationBuilderType.GetMethod(
                    "ResolveReplacement", BindingFlags.NonPublic | BindingFlags.Static);
                if (resolveReplacement == null)
                {
                    throw new InvalidOperationException("CadTranslationBuilder.ResolveReplacement was not found.");
                }
                var replacements = new System.Collections.Generic.Dictionary<string, string>
                {
                    { "2885668.57", "legacy" },
                    { "@handle:9C86", "457988.59" }
                };
                AssertEqual(
                    "457988.59",
                    resolveReplacement.Invoke(null, new object[] { "2885668.57", "9C86", replacements }),
                    "handle replacement takes precedence");
                AssertEqual(
                    "legacy",
                    resolveReplacement.Invoke(null, new object[] { "2885668.57", "FFFF", replacements }),
                    "text replacement fallback");
                MethodInfo distancePointToSegment = translationBuilderType.GetMethod(
                    "DistancePointToSegment2d", BindingFlags.NonPublic | BindingFlags.Static);
                if (distancePointToSegment == null)
                {
                    throw new InvalidOperationException("CadTranslationBuilder.DistancePointToSegment2d was not found.");
                }
                AssertEqual(
                    0.0,
                    distancePointToSegment.Invoke(null, new object[] { 5.0, 0.0, 0.0, 0.0, 10.0, 0.0 }),
                    "coordinate label lies on leader baseline");
                AssertEqual(
                    2.0,
                    distancePointToSegment.Invoke(null, new object[] { 5.0, 2.0, 0.0, 0.0, 10.0, 0.0 }),
                    "coordinate label offset from leader baseline");
                MethodInfo validate = builderType.GetMethod("Validate", BindingFlags.NonPublic | BindingFlags.Static);
                if (validate == null)
                {
                    throw new InvalidOperationException("TemplateBuilder.Validate was not found.");
                }

                object valid = NewRequest(requestType,
                    "protocol-test-1",
                    @"G:\.codex\CAD_Project\dwt_new\公司水利设计模板",
                    @"G:\.codex\CAD_Project\dwt_new\燕窝厂区修改后2026.7.12.dwg",
                    "COMPANY-HYDRO-RC-2026");
                AssertEqual(null, validate.Invoke(null, new[] { valid, "build" }), "valid build request");

                object outside = NewRequest(requestType,
                    "protocol-test-2",
                    @"F:\AppCaches\UserTemp\outside",
                    @"G:\.codex\CAD_Project\dwt_new\燕窝厂区修改后2026.7.12.dwg",
                    "COMPANY-HYDRO-RC-2026");
                AssertContains("output_dir", validate.Invoke(null, new[] { outside, "build" }), "outside output path");

                object wrongStandard = NewRequest(requestType,
                    "protocol-test-3",
                    @"G:\.codex\CAD_Project\dwt_new\公司水利设计模板",
                    @"G:\.codex\CAD_Project\dwt_new\燕窝厂区修改后2026.7.12.dwg",
                    "OTHER");
                AssertContains("standard", validate.Invoke(null, new[] { wrongStandard, "build" }), "wrong standard");

                object unsafeId = NewRequest(requestType,
                    @"..\escape",
                    @"G:\.codex\CAD_Project\dwt_new\公司水利设计模板",
                    @"G:\.codex\CAD_Project\dwt_new\燕窝厂区修改后2026.7.12.dwg",
                    "COMPANY-HYDRO-RC-2026");
                AssertContains("request_id", validate.Invoke(null, new[] { unsafeId, "build" }), "unsafe request id");

                MethodInfo buildPatternFile = builderType.GetMethod("BuildPatternFile", BindingFlags.NonPublic | BindingFlags.Static);
                if (buildPatternFile == null)
                {
                    throw new InvalidOperationException("TemplateBuilder.BuildPatternFile was not found.");
                }
                string firstPattern = Convert.ToString(buildPatternFile.Invoke(null, new object[] { 0 }));
                string lastPattern = Convert.ToString(buildPatternFile.Invoke(null, new object[] { 34 }));
                AssertContains("*SLT73_01_ROCK,", firstPattern, "first PAT header");
                AssertContains("*SLT73_32_TURF,", lastPattern, "last PAT header");
                AssertEqual(1, HeaderCount(firstPattern), "single PAT header count");
                AssertEqual(true, firstPattern.EndsWith("\r\n\r\n", StringComparison.Ordinal), "single PAT trailing blank line");

                Type tailraceBuilderType = assembly.GetType("CadMcp.AutoCAD.TailraceBuilder", true);
                MethodInfo validateTailrace = tailraceBuilderType.GetMethod("Validate", BindingFlags.NonPublic | BindingFlags.Static);
                if (validateTailrace == null)
                {
                    throw new InvalidOperationException("TailraceBuilder.Validate was not found.");
                }
                object tailrace = NewTailraceRequest(requestType, "tailrace-protocol-1", 693.10, 693.70, 694.01);
                AssertEqual(null, validateTailrace.Invoke(null, new[] { tailrace, "tailrace_build" }), "valid tailrace request");
                object badWaterOrder = NewTailraceRequest(requestType, "tailrace-protocol-2", 694.10, 693.70, 694.01);
                AssertContains("normal <= design <= check", validateTailrace.Invoke(null, new[] { badWaterOrder, "tailrace_build" }), "tailrace water order");

                object supported = NewSupportedTailraceRequest(requestType, "tailrace-supported-protocol-1", 0.4);
                AssertEqual(null, validateTailrace.Invoke(null, new[] { supported, "tailrace_supported_build" }), "valid supported tailrace request");
                object badCenterWall = NewSupportedTailraceRequest(requestType, "tailrace-supported-protocol-2", 4.0);
                AssertContains("center_wall_thickness_m", validateTailrace.Invoke(null, new[] { badCenterWall, "tailrace_supported_build" }), "supported tailrace center wall");
                object wrongSupportedOutput = NewSupportedTailraceRequest(requestType, "tailrace-supported-protocol-3", 0.4);
                requestType.GetProperty("output_dir").SetValue(wrongSupportedOutput, @"G:\.codex\CAD_Project\尾水涵设计", null);
                AssertContains("output_dir", validateTailrace.Invoke(null, new[] { wrongSupportedOutput, "tailrace_supported_build" }), "supported tailrace output path");

                Type imageRedrawBuilderType = assembly.GetType("CadMcp.AutoCAD.ImageRedrawBuilder", true);
                MethodInfo validateImageRedraw = imageRedrawBuilderType.GetMethod("Validate", BindingFlags.NonPublic | BindingFlags.Static);
                if (validateImageRedraw == null)
                {
                    throw new InvalidOperationException("ImageRedrawBuilder.Validate was not found.");
                }
                object imageRedraw = NewImageRedrawRequest(requestType, "image-redraw-protocol-1");
                AssertEqual(null, validateImageRedraw.Invoke(null, new[] { imageRedraw, "image_redraw_build" }), "valid image redraw request");
                object wrongImage = NewImageRedrawRequest(requestType, "image-redraw-protocol-2");
                requestType.GetProperty("image_path").SetValue(wrongImage, @"F:\AppCaches\UserTemp\other.jpg", null);
                AssertContains("image_path", validateImageRedraw.Invoke(null, new[] { wrongImage, "image_redraw_build" }), "unexpected redraw image");
                object wrongRedrawOutput = NewImageRedrawRequest(requestType, "image-redraw-protocol-3");
                requestType.GetProperty("output_dir").SetValue(wrongRedrawOutput, @"F:\AppCaches\UserTemp\outside", null);
                AssertContains("output_dir", validateImageRedraw.Invoke(null, new[] { wrongRedrawOutput, "image_redraw_build" }), "unexpected redraw output");

                Type copperWaterstopBuilderType = assembly.GetType("CadMcp.AutoCAD.CopperWaterstopBuilder", true);
                MethodInfo validateCopperWaterstop = copperWaterstopBuilderType.GetMethod("Validate", BindingFlags.NonPublic | BindingFlags.Static);
                if (validateCopperWaterstop == null)
                {
                    throw new InvalidOperationException("CopperWaterstopBuilder.Validate was not found.");
                }
                object copperWaterstop = NewCopperWaterstopRequest(requestType, "copper-waterstop-protocol-1");
                AssertEqual(null, validateCopperWaterstop.Invoke(null, new[] { copperWaterstop, "copper_waterstop_build" }), "valid copper waterstop request");
                object wrongCopperImage = NewCopperWaterstopRequest(requestType, "copper-waterstop-protocol-2");
                requestType.GetProperty("image_path").SetValue(wrongCopperImage, @"F:\AppCaches\UserTemp\other.png", null);
                AssertContains("image_path", validateCopperWaterstop.Invoke(null, new[] { wrongCopperImage, "copper_waterstop_build" }), "unexpected copper waterstop image");
                object wrongCopperOutput = NewCopperWaterstopRequest(requestType, "copper-waterstop-protocol-3");
                requestType.GetProperty("output_dir").SetValue(wrongCopperOutput, @"F:\AppCaches\UserTemp\outside", null);
                AssertContains("output_dir", validateCopperWaterstop.Invoke(null, new[] { wrongCopperOutput, "copper_waterstop_build" }), "unexpected copper waterstop output");

                Type translationRequestType = assembly.GetType("CadMcp.AutoCAD.TranslationRequest", true);
                Type arcBuilderType = assembly.GetType("CadMcp.AutoCAD.ArcAnnotationBuilder", true);
                MethodInfo validateArc = arcBuilderType.GetMethod("Validate", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo formatArc = arcBuilderType.GetMethod("FormatLabel", BindingFlags.NonPublic | BindingFlags.Static);
                if (validateArc == null || formatArc == null)
                {
                    throw new InvalidOperationException("ArcAnnotationBuilder protocol methods were not found.");
                }
                object arcRequest = NewArcRequest(translationRequestType, "arc-protocol-1", @"G:\.codex\CAD_Project\统计\Drawing1.dwg");
                AssertEqual(null, validateArc.Invoke(null, new[] { arcRequest, "arc_apply" }), "valid arc annotation request");
                object wrongArcPath = NewArcRequest(translationRequestType, "arc-protocol-2", @"F:\AppCaches\UserTemp\Drawing1.dwg");
                AssertContains("source_dwg", validateArc.Invoke(null, new[] { wrongArcPath, "arc_apply" }), "unexpected arc DWG path");
                AssertEqual("R=10.00m;L=31.4m", formatArc.Invoke(null, new object[] { "R={radius}m;L={length}m", 1, 2, 31.4159, 10.0 }), "arc label formatting");

                Console.WriteLine("CadMcp protocol validation tests passed (26/26).");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        private static object NewRequest(Type type, string id, string output, string source, string standard)
        {
            object request = Activator.CreateInstance(type, true);
            type.GetProperty("request_id").SetValue(request, id, null);
            type.GetProperty("output_dir").SetValue(request, output, null);
            type.GetProperty("source_dwg").SetValue(request, source, null);
            type.GetProperty("standard").SetValue(request, standard, null);
            return request;
        }

        private static object NewTailraceRequest(Type type, string id, double normal, double design, double check)
        {
            object request = NewRequest(
                type,
                id,
                @"G:\.codex\CAD_Project\尾水涵设计",
                @"G:\.codex\CAD_Project\dwt_new\公司水利设计模板\公司水利钢筋混凝土设计.dwt",
                "COMPANY-HYDRO-RC-2026");
            type.GetProperty("length_m").SetValue(request, 70.0, null);
            type.GetProperty("clear_width_m").SetValue(request, 4.0, null);
            type.GetProperty("clear_height_m").SetValue(request, 1.7, null);
            type.GetProperty("slope").SetValue(request, 0.002, null);
            type.GetProperty("inlet_invert_m").SetValue(request, 692.0, null);
            type.GetProperty("cover_m").SetValue(request, 3.0, null);
            type.GetProperty("thickness_m").SetValue(request, 0.4, null);
            type.GetProperty("bottom_thickness_m").SetValue(request, 0.45, null);
            type.GetProperty("truck_weight_kn").SetValue(request, 550.0, null);
            type.GetProperty("traffic_pressure_kpa").SetValue(request, 20.0, null);
            type.GetProperty("global_safety_factor").SetValue(request, 1.5, null);
            type.GetProperty("normal_water_m").SetValue(request, normal, null);
            type.GetProperty("design_water_m").SetValue(request, design, null);
            type.GetProperty("check_water_m").SetValue(request, check, null);
            return request;
        }

        private static object NewSupportedTailraceRequest(Type type, string id, double centerWall)
        {
            object request = NewTailraceRequest(type, id, 693.10, 693.70, 694.01);
            type.GetProperty("output_dir").SetValue(request, @"G:\.codex\CAD_Project\尾水涵设计\中间支撑双孔方案", null);
            type.GetProperty("center_wall_thickness_m").SetValue(request, centerWall, null);
            return request;
        }

        private static object NewImageRedrawRequest(Type type, string id)
        {
            object request = NewRequest(
                type,
                id,
                @"G:\.codex\CAD_Project\pdftocad",
                @"G:\.codex\skills\company-hydraulic-rc-design\assets\公司水利钢筋混凝土设计.dwt",
                "COMPANY-HYDRO-RC-2026");
            type.GetProperty("image_path").SetValue(request, @"G:\.codex\CAD_Project\pdftocad\pic.jpg", null);
            return request;
        }

        private static object NewCopperWaterstopRequest(Type type, string id)
        {
            object request = NewRequest(
                type,
                id,
                @"G:\.codex\CAD_Project\画图",
                @"G:\.codex\skills\company-hydraulic-rc-design\assets\公司水利钢筋混凝土设计.dwt",
                "COMPANY-HYDRO-RC-2026");
            type.GetProperty("image_path").SetValue(request, @"G:\.codex\CAD_Project\画图\紫铜片止水.png", null);
            return request;
        }

        private static object NewArcRequest(Type type, string id, string source)
        {
            object request = Activator.CreateInstance(type, true);
            type.GetProperty("request_id").SetValue(request, id, null);
            type.GetProperty("source_dwg").SetValue(request, source, null);
            type.GetProperty("label_template").SetValue(request, "R={radius}m;L={length}m", null);
            type.GetProperty("length_decimals").SetValue(request, 1, null);
            type.GetProperty("radius_decimals").SetValue(request, 2, null);
            type.GetProperty("text_height").SetValue(request, 1.2, null);
            type.GetProperty("leader").SetValue(request, true, null);
            return request;
        }

        private static void AssertEqual(object expected, object actual, string name)
        {
            if (!Equals(expected, actual))
            {
                throw new InvalidOperationException(name + " failed: expected " + expected + ", actual " + actual);
            }
        }

        private static void AssertContains(string expected, object actual, string name)
        {
            string value = Convert.ToString(actual);
            if (value == null || value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException(name + " failed: " + value);
            }
        }

        private static int HeaderCount(string content)
        {
            int count = 0;
            foreach (string line in content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (line.StartsWith("*", StringComparison.Ordinal))
                {
                    count++;
                }
            }
            return count;
        }
    }
}
