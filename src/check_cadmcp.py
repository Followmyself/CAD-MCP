"""只读检查 Codex → MCP → AutoCAD 插件链路。"""

from __future__ import annotations

import hashlib
import json
import os
import sys
import tomllib
import winreg
from pathlib import Path
from typing import Any

import psutil
import requests
import utils.guard as env


AUTOCAD_EXE = Path(
    os.environ.get("CADMCP_AUTOCAD_EXE", r"G:\CAD2023anzhuangbao\AutoCAD 2023\acad.exe")
)
PROJECT_DLL = (
    Path(__file__).resolve().parents[1]
    / "autocad-plugin"
    / "bin"
    / "x64"
    / "Release"
    / "CadMcp.AutoCAD.dll"
)
DEPLOYED_DLL = Path(
    r"C:\ProgramData\Autodesk\ApplicationPlugins\CADMCP.bundle\Contents\Windows\CadMcp.AutoCAD.dll"
)
CONFIG = Path(os.environ.get("CADMCP_CODEX_CONFIG", r"G:\.codex\config.toml"))
MCP_SERVER = Path(__file__).resolve().with_name("cadmcp_server.py")
MCP_LOG_DIR = Path(r"G:\.codex\logs\CAD-MCP")
TEMP_ROOT = Path(r"F:\AppCaches\UserTemp")
DWG_SOURCE_ROOTS_CONFIG = Path(
    os.environ.get(
        "CADMCP_DWG_SOURCE_ROOTS_CONFIG",
        r"G:\.codex\CAD_Project\cadmcp_source_roots.toml",
    )
)
DG2_SOURCE_ROOT = Path(
    r"F:\项目\越南德隆\越南德隆水电站\DG2 - TKKT-tâp 1-tap 10 (Xb-2022 _ file mem)"
)
TRUSTED_PLUGIN_DIR = DEPLOYED_DLL.parent
AUTOCAD_PROFILES_KEY = r"Software\Autodesk\AutoCAD\R24.2\ACAD-6101:804\Profiles"
HEALTH_URL = "http://127.0.0.1:8765/health"
REQUIRED_TOOLS = {
    "draw_circle",
    "inspect_cad_workspace",
    "inspect_cad_translation",
    "translate_cad",
    "repair_cad_fonts",
    "build_slt73_template",
    "verify_slt73_template",
    "build_image_redraw",
    "verify_image_redraw",
    "build_copper_waterstop",
    "verify_copper_waterstop",
    "inspect_arc_annotations",
    "annotate_arcs",
    "verify_arc_annotations",
}


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _load_config(path: Path = CONFIG) -> dict[str, Any]:
    return tomllib.loads(path.read_text(encoding="utf-8-sig"))


def _path_list_contains(value: str, expected: Path) -> bool:
    expected_key = os.path.normcase(os.path.abspath(str(expected))).rstrip("\\/")
    return any(
        os.path.normcase(os.path.abspath(item.strip())).rstrip("\\/") == expected_key
        for item in value.split(";")
        if item.strip()
    )


def _trusted_profiles() -> dict[str, bool]:
    result: dict[str, bool] = {}
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, AUTOCAD_PROFILES_KEY) as profiles:
        index = 0
        while True:
            try:
                profile = winreg.EnumKey(profiles, index)
            except OSError:
                break
            index += 1
            variables_path = AUTOCAD_PROFILES_KEY + "\\" + profile + "\\Variables"
            try:
                with winreg.OpenKey(winreg.HKEY_CURRENT_USER, variables_path) as variables:
                    trusted, _ = winreg.QueryValueEx(variables, "TRUSTEDPATHS")
            except FileNotFoundError:
                trusted = ""
            result[profile] = _path_list_contains(str(trusted), TRUSTED_PLUGIN_DIR)
    return result


def _configured_dwg_roots() -> tuple[Path, ...]:
    config = tomllib.loads(DWG_SOURCE_ROOTS_CONFIG.read_text(encoding="utf-8-sig"))
    roots = config.get("dwg_sources", {}).get("roots")
    if not isinstance(roots, list) or not all(isinstance(root, str) for root in roots):
        raise RuntimeError("CAD MCP DWG路径配置格式错误")
    return tuple(Path(root).expanduser().resolve() for root in roots)


def _health() -> dict[str, Any]:
    response = requests.get(HEALTH_URL, timeout=5)
    response.raise_for_status()
    payload = response.json()
    if not payload.get("ok"):
        raise RuntimeError(f"插件健康检查未返回 ok=true: {payload}")
    tools = set(payload.get("tools") or [])
    missing = REQUIRED_TOOLS - tools
    if missing:
        raise RuntimeError(f"插件未暴露必需工具: {sorted(missing)}")
    return payload


def main() -> int:
    config = _load_config()
    cad_config = config.get("mcp_servers", {}).get("CAD")
    acad_processes = [
        process.info
        for process in psutil.process_iter(["pid", "name", "exe"])
        if (process.info.get("name") or "").lower() == "acad.exe"
    ]

    report: dict[str, Any] = {
        "autocad_exe": AUTOCAD_EXE.is_file(),
        "autocad_processes": acad_processes,
        "project_dll": PROJECT_DLL.is_file(),
        "deployed_dll": DEPLOYED_DLL.is_file(),
        "dll_hash_match": (
            PROJECT_DLL.is_file()
            and DEPLOYED_DLL.is_file()
            and _sha256(PROJECT_DLL) == _sha256(DEPLOYED_DLL)
        ),
        "codex_mcp_configured": bool(cad_config),
        "new_paths_configured": bool(
            cad_config
            and cad_config.get("command") == r"D:\anaconda\python.exe"
            and cad_config.get("args") == [str(MCP_SERVER)]
            and cad_config.get("env", {}).get("CADMCP_LOG_DIR") == str(MCP_LOG_DIR)
            and cad_config.get("env", {}).get("TEMP") == str(TEMP_ROOT)
            and cad_config.get("env", {}).get("TMP") == str(TEMP_ROOT)
        ),
    }
    try:
        configured_roots = _configured_dwg_roots()
        report["dwg_source_roots"] = [str(root) for root in configured_roots]
        report["dg2_dwg_root_configured"] = DG2_SOURCE_ROOT.resolve() in configured_roots
    except Exception as error:
        report["dwg_source_roots_error"] = repr(error)
        report["dg2_dwg_root_configured"] = False
    report["trusted_profiles"] = _trusted_profiles()
    report["trusted_plugin_path"] = bool(report["trusted_profiles"]) and all(
        report["trusted_profiles"].values()
    )

    try:
        report["plugin_health"] = env.robust_connect(_health, retries=3)
    except Exception as error:
        report["plugin_health_error"] = repr(error)

    print(json.dumps(report, ensure_ascii=False, indent=2, default=str))
    required = (
        report["autocad_exe"],
        report["project_dll"],
        report["deployed_dll"],
        report["dll_hash_match"],
        report["codex_mcp_configured"],
        report["new_paths_configured"],
        report["dg2_dwg_root_configured"],
        report["trusted_plugin_path"],
        "plugin_health" in report,
    )
    if all(required):
        return 0
    raise RuntimeError("CAD MCP 环境检查失败；请按上方具体失败项和日志定位根因。")


if __name__ == "__main__":
    sys.exit(main())
