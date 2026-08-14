from __future__ import annotations

import math
import os
import shutil
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch


SRC = Path(__file__).resolve().parents[1] / "src"
sys.path.insert(0, str(SRC))

TEST_ROOT = Path(tempfile.mkdtemp(prefix="cadmcp-tests-"))
COORDINATE_ROOT = TEST_ROOT / "coordinate"
DG2_ROOT = TEST_ROOT / "dg2"
COORDINATE_ROOT.mkdir()
DG2_ROOT.mkdir()
COORDINATE_SOURCE = COORDINATE_ROOT / "坐标更新.dwg"
COORDINATE_SOURCE.touch()
DG2_SOURCE = DG2_ROOT / "sample.dwg"
DG2_SOURCE.touch()
ROOTS_CONFIG = TEST_ROOT / "cadmcp_source_roots.toml"
ROOTS_CONFIG.write_text(
    "[dwg_sources]\n"
    f"roots = ['{COORDINATE_ROOT}', '{DG2_ROOT}']\n",
    encoding="utf-8",
)
unittest.addModuleCleanup(shutil.rmtree, TEST_ROOT, True)

os.environ["CADMCP_LOG_DIR"] = str(TEST_ROOT / "logs")
os.environ["CADMCP_TRANSLATION_ROOT"] = str(COORDINATE_ROOT)
os.environ["CADMCP_DWG_SOURCE_ROOTS_CONFIG"] = str(ROOTS_CONFIG)

import cadmcp_server


class DrawCircleTests(unittest.TestCase):
    def test_rejects_nonpositive_radius(self) -> None:
        with self.assertRaisesRegex(ValueError, "radius 必须大于 0"):
            cadmcp_server.draw_circle(0, 0, 0)

    def test_rejects_nonfinite_coordinate(self) -> None:
        with self.assertRaisesRegex(ValueError, "x 必须是有限数字"):
            cadmcp_server.draw_circle(math.inf, 0, 1)

    def test_rejects_invalid_request_id(self) -> None:
        with self.assertRaisesRegex(ValueError, "request_id"):
            cadmcp_server.draw_circle(0, 0, 1, request_id="含空格 id")

    def test_retry_reuses_request_id(self) -> None:
        payloads: list[dict[str, object]] = []

        def flaky_post(payload: dict[str, object]) -> dict[str, object]:
            payloads.append(payload.copy())
            if len(payloads) == 1:
                raise ConnectionError("temporary")
            return {
                "ok": True,
                "request_id": payload["request_id"],
                "duplicate": False,
                "object_id": "2A1",
            }

        with (
            patch.object(cadmcp_server, "_post_draw", side_effect=flaky_post),
            patch.object(cadmcp_server.env, "clean_temp_cache"),
            patch.object(cadmcp_server.env.time, "sleep"),
        ):
            result = cadmcp_server.draw_circle(0, 0, 100, request_id="test-id-1")

        self.assertTrue(result["ok"])
        self.assertEqual(2, len(payloads))
        self.assertEqual("test-id-1", payloads[0]["request_id"])
        self.assertEqual(payloads[0]["request_id"], payloads[1]["request_id"])


class StartupTests(unittest.TestCase):
    def test_launcher_output_is_decoded_without_masking_native_errors(self) -> None:
        connection_error = cadmcp_server.requests.exceptions.ConnectionError("offline")
        completed = SimpleNamespace(returncode=0, stdout="{}", stderr="")

        with (
            patch.object(
                cadmcp_server,
                "_plugin_health",
                side_effect=[connection_error, connection_error, {"ok": True}],
            ),
            patch.object(cadmcp_server.psutil, "process_iter", return_value=[]),
            patch.object(cadmcp_server.subprocess, "run", return_value=completed) as run,
        ):
            result = cadmcp_server._ensure_plugin_ready()

        self.assertTrue(result["ok"])
        self.assertEqual("utf-8", run.call_args.kwargs["encoding"])
        self.assertEqual("replace", run.call_args.kwargs["errors"])
        self.assertEqual("90", run.call_args.args[0][-1])
        self.assertEqual(100, run.call_args.kwargs["timeout"])


class ArcAnnotationTests(unittest.TestCase):
    def test_rejects_unexpected_arc_dwg(self) -> None:
        with self.assertRaisesRegex(ValueError, "source_dwg 必须是"):
            cadmcp_server.inspect_arc_annotations(r"F:\AppCaches\UserTemp\other.dwg")

    def test_rejects_template_without_both_values(self) -> None:
        with self.assertRaisesRegex(ValueError, "label_template"):
            cadmcp_server.annotate_arcs(label_template="L={length}", request_id="arc-invalid-template")

    def test_arc_payload_and_retry_preserve_request_id(self) -> None:
        payloads: list[dict[str, object]] = []

        def flaky_post(path: str, payload: dict[str, object], timeout: int = 120) -> dict[str, object]:
            self.assertEqual("/annotate_arcs", path)
            payloads.append(payload.copy())
            if len(payloads) == 1:
                raise ConnectionError("temporary")
            return {"ok": True, "request_id": payload["request_id"], "duplicate": False}

        with (
            patch.object(cadmcp_server, "_post_plugin", side_effect=flaky_post),
            patch.object(cadmcp_server.env.time, "sleep"),
        ):
            result = cadmcp_server.annotate_arcs(
                label_template="R={radius}m;L={length}m",
                length_decimals=1,
                radius_decimals=2,
                text_height=1.2,
                leader=True,
                request_id="arc-annotation-test-1",
            )

        self.assertTrue(result["ok"])
        self.assertEqual(2, len(payloads))
        self.assertEqual("arc-annotation-test-1", payloads[0]["request_id"])
        self.assertEqual(payloads[0]["request_id"], payloads[1]["request_id"])
        self.assertEqual(1, payloads[0]["length_decimals"])
        self.assertEqual(2, payloads[0]["radius_decimals"])
        self.assertEqual(1.2, payloads[0]["text_height"])
        self.assertTrue(payloads[0]["leader"])


class Slt73TemplateTests(unittest.TestCase):
    def test_rejects_output_path_outside_project(self) -> None:
        with self.assertRaisesRegex(ValueError, "output_dir 必须是"):
            cadmcp_server.build_slt73_template(
                output_dir=r"F:\AppCaches\UserTemp\outside"
            )

    def test_rejects_unexpected_source_dwg(self) -> None:
        with self.assertRaisesRegex(ValueError, "source_dwg 必须是"):
            cadmcp_server.inspect_cad_workspace(
                source_dwg=r"F:\AppCaches\UserTemp\outside\other.dwg"
            )

    def test_build_retry_reuses_request_id(self) -> None:
        payloads: list[dict[str, object]] = []

        def flaky_post(path: str, payload: dict[str, object], timeout: int = 75) -> dict[str, object]:
            payloads.append(payload.copy())
            if len(payloads) == 1:
                raise ConnectionError("temporary")
            return {
                "ok": True,
                "request_id": payload["request_id"],
                "duplicate": False,
            }

        with (
            patch.object(cadmcp_server, "_post_plugin", side_effect=flaky_post),
            patch.object(cadmcp_server.env, "clean_temp_cache"),
            patch.object(cadmcp_server.env.time, "sleep"),
        ):
            result = cadmcp_server.build_slt73_template(request_id="slt73-test-1")

        self.assertTrue(result["ok"])
        self.assertEqual(2, len(payloads))
        self.assertEqual("slt73-test-1", payloads[0]["request_id"])
        self.assertEqual(payloads[0]["request_id"], payloads[1]["request_id"])

    def test_build_does_not_retry_plugin_error(self) -> None:
        calls = 0

        def deterministic_failure(path: str, payload: dict[str, object], timeout: int = 75) -> dict[str, object]:
            nonlocal calls
            calls += 1
            raise RuntimeError("plugin rejected deterministic input")

        with patch.object(cadmcp_server, "_post_plugin", side_effect=deterministic_failure):
            with self.assertRaisesRegex(RuntimeError, "deterministic"):
                cadmcp_server.build_slt73_template(request_id="slt73-test-no-retry")

        self.assertEqual(1, calls)


class CadTranslationTests(unittest.TestCase):
    def test_translation_saves_from_locked_autocad_document(self) -> None:
        source = (SRC.parent / "autocad-plugin" / "CadTranslationBuilder.cs").read_text(
            encoding="utf-8-sig"
        )
        translate_body = source.split(
            "private static TranslationResponse Translate", 1
        )[1].split("private static Database OpenDatabase", 1)[0]
        self.assertIn("DocumentManager.Open", translate_body)
        self.assertIn("document.LockDocument()", translate_body)
        self.assertIn("database.SaveAs(stage, DwgVersion.Current);", translate_body)
        self.assertNotIn("File.Copy(request.source_dwg, working, false);", translate_body)
        self.assertNotIn("CreateEditableSnapshot", translate_body)

    def test_coordinate_inspection_binds_label_to_leader_first_vertex(self) -> None:
        source = (SRC.parent / "autocad-plugin" / "CadTranslationBuilder.cs").read_text(
            encoding="utf-8-sig"
        )
        self.assertIn('{ "coordinate_binding", BindCoordinateLeader(anchor, coordinatePolylines) }', source)
        self.assertIn('{ "point", PointDictionary(polyline.Vertices[0]) }', source)
        self.assertIn('{ "status", matches.Count == 1 ? "matched"', source)

    def test_translation_preserves_handle_targeted_mapping(self) -> None:
        payloads: list[dict[str, object]] = []

        def capture(path: str, payload: dict[str, object], timeout: int = 120) -> dict[str, object]:
            self.assertEqual("/translate_cad", path)
            payloads.append(payload.copy())
            return {"ok": True, "request_id": payload["request_id"]}

        output = COORDINATE_SOURCE.with_name("坐标更新_handle_test_output.dwg")
        with patch.object(cadmcp_server, "_post_plugin", side_effect=capture):
            cadmcp_server.translate_cad(
                str(COORDINATE_SOURCE),
                str(output),
                {"@handle:9C86": "457988.59"},
                request_id="coordinate-handle-test",
            )

        self.assertEqual("457988.59", payloads[0]["translations"]["@handle:9C86"])

    def test_configured_coordinate_root_is_loaded(self) -> None:
        self.assertIn(COORDINATE_ROOT.resolve(), cadmcp_server.DWG_SOURCE_ROOTS)

    def test_configured_dg2_root_is_loaded(self) -> None:
        self.assertIn(DG2_ROOT.resolve(), cadmcp_server.DWG_SOURCE_ROOTS)

    def test_translation_accepts_original_dg2_dwg_root(self) -> None:
        self.assertTrue(DG2_SOURCE.is_file())
        self.assertEqual(
            str(DG2_SOURCE.resolve()),
            cadmcp_server._normalize_translation_path(str(DG2_SOURCE), "source_dwg", True),
        )

    def test_translation_paths_are_restricted_to_user_dwg_root(self) -> None:
        with self.assertRaisesRegex(ValueError, "必须位于"):
            cadmcp_server.inspect_cad_translation(
                source_dwg=r"F:\AppCaches\UserTemp\outside.dwg"
            )

    def test_translation_refuses_existing_output(self) -> None:
        source = cadmcp_server.TRANSLATION_ROOT / "sample.dwg"
        output = cadmcp_server.TRANSLATION_ROOT / "sample_中文翻译.dwg"
        with patch.object(Path, "is_file", return_value=True), patch.object(
            Path, "exists", side_effect=lambda: True
        ):
            with self.assertRaisesRegex(ValueError, "拒绝覆盖"):
                cadmcp_server.translate_cad(
                    str(source), str(output), {"Xin chao": "你好"}, request_id="translation-test"
                )

    def test_font_repair_refuses_existing_output(self) -> None:
        source = cadmcp_server.TRANSLATION_ROOT / "sample.dwg"
        output = cadmcp_server.TRANSLATION_ROOT / "sample_中文字体修正版.dwg"
        with patch.object(Path, "is_file", return_value=True), patch.object(
            Path, "exists", side_effect=lambda: True
        ):
            with self.assertRaisesRegex(ValueError, "拒绝覆盖"):
                cadmcp_server.repair_cad_fonts(
                    str(source), str(output), request_id="font-repair-test"
                )


class TailraceCulvertTests(unittest.TestCase):
    def test_rejects_tailrace_output_path_outside_project(self) -> None:
        with self.assertRaisesRegex(ValueError, "output_dir 必须是"):
            cadmcp_server.build_tailrace_culvert(
                output_dir=r"F:\AppCaches\UserTemp\outside",
                request_id="tailrace-invalid-path",
            )

    def test_build_payload_preserves_dimensions_and_request_id(self) -> None:
        payloads: list[dict[str, object]] = []

        def capture(path: str, payload: dict[str, object], timeout: int = 120) -> dict[str, object]:
            payloads.append(payload.copy())
            return {"ok": True, "request_id": payload["request_id"], "duplicate": False}

        with patch.object(cadmcp_server, "_post_plugin", side_effect=capture):
            result = cadmcp_server.build_tailrace_culvert(
                length_m=70,
                clear_width_m=4,
                clear_height_m=1.7,
                request_id="tailrace-test-1",
            )

        self.assertTrue(result["ok"])
        self.assertEqual(1, len(payloads))
        self.assertEqual("tailrace-test-1", payloads[0]["request_id"])
        self.assertEqual(70.0, payloads[0]["length_m"])
        self.assertEqual(4.0, payloads[0]["clear_width_m"])
        self.assertEqual(1.7, payloads[0]["clear_height_m"])
        self.assertEqual("COMPANY-HYDRO-RC-2026", payloads[0]["standard"])
        self.assertEqual(3.0, payloads[0]["cover_m"])
        self.assertEqual(0.4, payloads[0]["thickness_m"])
        self.assertEqual(0.45, payloads[0]["bottom_thickness_m"])
        self.assertEqual(550.0, payloads[0]["truck_weight_kn"])
        self.assertEqual(20.0, payloads[0]["traffic_pressure_kpa"])
        self.assertEqual(1.5, payloads[0]["global_safety_factor"])

    def test_rejects_invalid_tailrace_safety_factor_before_transport(self) -> None:
        with patch.object(cadmcp_server, "_post_plugin") as post:
            with self.assertRaisesRegex(ValueError, "global_safety_factor"):
                cadmcp_server.build_tailrace_culvert(
                    global_safety_factor=0.9,
                    request_id="tailrace-invalid-safety",
                )
        post.assert_not_called()
    def test_rejects_nonpositive_tailrace_traffic_before_transport(self) -> None:
        with patch.object(cadmcp_server, "_post_plugin") as post:
            with self.assertRaisesRegex(ValueError, "traffic_pressure_kpa"):
                cadmcp_server.build_tailrace_culvert(
                    traffic_pressure_kpa=0,
                    request_id="tailrace-invalid-traffic",
                )
        post.assert_not_called()


class ImageRedrawTests(unittest.TestCase):
    def test_rejects_image_redraw_output_outside_project(self) -> None:
        with self.assertRaisesRegex(ValueError, "output_dir 必须是"):
            cadmcp_server.build_image_redraw(
                output_dir=r"F:\AppCaches\UserTemp\outside",
                request_id="image-redraw-invalid-output",
            )

    def test_rejects_unexpected_image_path(self) -> None:
        with self.assertRaisesRegex(ValueError, "image_path 必须是"):
            cadmcp_server.build_image_redraw(
                image_path=r"F:\AppCaches\UserTemp\other.jpg",
                request_id="image-redraw-invalid-image",
            )

    def test_image_redraw_payload_and_retry_preserve_request_id(self) -> None:
        payloads: list[dict[str, object]] = []

        def flaky_post(path: str, payload: dict[str, object], timeout: int = 120) -> dict[str, object]:
            self.assertEqual("/build_image_redraw", path)
            payloads.append(payload.copy())
            if len(payloads) == 1:
                raise ConnectionError("temporary")
            return {"ok": True, "request_id": payload["request_id"], "duplicate": False}

        with (
            patch.object(cadmcp_server, "_post_plugin", side_effect=flaky_post),
            patch.object(cadmcp_server.env.time, "sleep"),
        ):
            result = cadmcp_server.build_image_redraw(request_id="image-redraw-test-1")

        self.assertTrue(result["ok"])
        self.assertEqual(2, len(payloads))
        self.assertEqual("image-redraw-test-1", payloads[0]["request_id"])
        self.assertEqual(payloads[0]["request_id"], payloads[1]["request_id"])
        self.assertEqual("COMPANY-HYDRO-RC-2026", payloads[0]["standard"])
        self.assertEqual(str(cadmcp_server.IMAGE_REDRAW_SOURCE_IMAGE.resolve()), payloads[0]["image_path"])


class CopperWaterstopTests(unittest.TestCase):
    def test_rejects_copper_waterstop_output_outside_project(self) -> None:
        with self.assertRaisesRegex(ValueError, "output_dir 必须是"):
            cadmcp_server.build_copper_waterstop(
                output_dir=r"F:\AppCaches\UserTemp\outside",
                request_id="copper-waterstop-invalid-output",
            )

    def test_rejects_unexpected_copper_waterstop_image(self) -> None:
        with self.assertRaisesRegex(ValueError, "image_path 必须是"):
            cadmcp_server.build_copper_waterstop(
                image_path=r"F:\AppCaches\UserTemp\other.png",
                request_id="copper-waterstop-invalid-image",
            )

    def test_copper_waterstop_payload_and_retry_preserve_request_id(self) -> None:
        payloads: list[dict[str, object]] = []

        def flaky_post(path: str, payload: dict[str, object], timeout: int = 120) -> dict[str, object]:
            self.assertEqual("/build_copper_waterstop", path)
            payloads.append(payload.copy())
            if len(payloads) == 1:
                raise ConnectionError("temporary")
            return {"ok": True, "request_id": payload["request_id"], "duplicate": False}

        with (
            patch.object(cadmcp_server, "_post_plugin", side_effect=flaky_post),
            patch.object(cadmcp_server.env.time, "sleep"),
        ):
            result = cadmcp_server.build_copper_waterstop(request_id="copper-waterstop-test-1")

        self.assertTrue(result["ok"])
        self.assertEqual(2, len(payloads))
        self.assertEqual("copper-waterstop-test-1", payloads[0]["request_id"])
        self.assertEqual(payloads[0]["request_id"], payloads[1]["request_id"])
        self.assertEqual("COMPANY-HYDRO-RC-2026", payloads[0]["standard"])
        self.assertEqual(str(cadmcp_server.COPPER_WATERSTOP_SOURCE_IMAGE.resolve()), payloads[0]["image_path"])



class SupportedTailraceCulvertTests(unittest.TestCase):
    def test_supported_tailrace_payload_preserves_total_width_and_center_wall(self) -> None:
        payloads: list[dict[str, object]] = []
        paths: list[str] = []

        def capture(path: str, payload: dict[str, object], timeout: int = 120) -> dict[str, object]:
            paths.append(path)
            payloads.append(payload.copy())
            return {"ok": True, "request_id": payload["request_id"], "duplicate": False}

        with patch.object(cadmcp_server, "_post_plugin", side_effect=capture):
            result = cadmcp_server.build_supported_tailrace_culvert(
                clear_width_m=4.0,
                center_wall_thickness_m=0.4,
                request_id="tailrace-supported-test-1",
            )

        self.assertTrue(result["ok"])
        self.assertEqual(1, len(payloads))
        self.assertEqual(["/build_supported_tailrace_culvert"], paths)
        self.assertEqual(4.0, payloads[0]["clear_width_m"])
        self.assertEqual(0.4, payloads[0]["center_wall_thickness_m"])
        self.assertEqual(
            str(cadmcp_server.SUPPORTED_TAILRACE_OUTPUT_ROOT.resolve()),
            payloads[0]["output_dir"],
        )

    def test_supported_tailrace_rejects_cell_width_outside_about_two_metres(self) -> None:
        with patch.object(cadmcp_server, "_post_plugin") as post:
            with self.assertRaisesRegex(ValueError, "每孔净宽"):
                cadmcp_server.build_supported_tailrace_culvert(
                    clear_width_m=4.0,
                    center_wall_thickness_m=1.0,
                    request_id="tailrace-supported-invalid-cell",
                )
        post.assert_not_called()


if __name__ == "__main__":
    unittest.main()
