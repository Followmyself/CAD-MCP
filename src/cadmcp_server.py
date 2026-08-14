"""Codex MCP 到 AutoCAD 进程内插件的 HTTP 桥。"""

from __future__ import annotations

import json
import logging
import math
import os
import re
import subprocess
import threading
import tomllib
import uuid
from pathlib import Path
from typing import Any

import requests
import psutil
import utils.guard as env
from mcp.server.fastmcp import FastMCP


PLUGIN_BASE_URL = "http://127.0.0.1:8765"
PLUGIN_URL = PLUGIN_BASE_URL + "/draw_circle"
ALLOWED_OUTPUT_ROOT = Path(r"G:\.codex\CAD_Project\dwt_new\公司水利设计模板")
COMPANY_SOURCE_DWG = Path(r"G:\.codex\CAD_Project\dwt_new\燕窝厂区修改后2026.7.12.dwg")
TAILRACE_OUTPUT_ROOT = Path(r"G:\.codex\CAD_Project\尾水涵设计")
SUPPORTED_TAILRACE_OUTPUT_ROOT = TAILRACE_OUTPUT_ROOT / "中间支撑双孔方案"
TAILRACE_SOURCE_DWT = ALLOWED_OUTPUT_ROOT / "公司水利钢筋混凝土设计.dwt"
IMAGE_REDRAW_OUTPUT_ROOT = Path(r"G:\.codex\CAD_Project\pdftocad")
IMAGE_REDRAW_SOURCE_DWT = Path(
    r"G:\.codex\skills\company-hydraulic-rc-design\assets\公司水利钢筋混凝土设计.dwt"
)
IMAGE_REDRAW_SOURCE_IMAGE = IMAGE_REDRAW_OUTPUT_ROOT / "pic.jpg"
COPPER_WATERSTOP_OUTPUT_ROOT = Path(r"G:\.codex\CAD_Project\画图")
COPPER_WATERSTOP_SOURCE_DWT = IMAGE_REDRAW_SOURCE_DWT
COPPER_WATERSTOP_SOURCE_IMAGE = COPPER_WATERSTOP_OUTPUT_ROOT / "紫铜片止水.png"
ARC_ANNOTATION_DWG = Path(r"G:\.codex\CAD_Project\统计\Drawing1.dwg")
TRANSLATION_ROOT = Path(
    os.environ.get("CADMCP_TRANSLATION_ROOT", str(Path.home() / "CAD-MCP-Work"))
)
DWG_SOURCE_ROOTS_CONFIG = Path(
    os.environ.get(
        "CADMCP_DWG_SOURCE_ROOTS_CONFIG",
        r"G:\.codex\CAD_Project\cadmcp_source_roots.toml",
    )
)
AUTOCAD_LAUNCHER = Path(r"G:\.codex\tools\Start-AutoCADForCadMcp.ps1")
POWERSHELL = Path(r"G:\.codex\runtimes\powershell\7.6.3\pwsh.exe")
REQUEST_ID_PATTERN = re.compile(r"^[A-Za-z0-9._:-]{1,128}$")
LOG_PATH = Path(
    os.environ.get(
        "CADMCP_LOG_DIR",
        r"G:\.codex\logs\CAD-MCP",
    )
) / "mcp-server.log"
LOG_PATH.parent.mkdir(parents=True, exist_ok=True)

logger = logging.getLogger("cadmcp.server")
logger.setLevel(logging.INFO)
logger.propagate = False
if not logger.handlers:
    handler = logging.FileHandler(LOG_PATH, encoding="utf-8")
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
    logger.addHandler(handler)

_startup_lock = threading.Lock()


def _load_dwg_source_roots() -> tuple[Path, ...]:
    if not DWG_SOURCE_ROOTS_CONFIG.is_file():
        raise RuntimeError(f"CAD MCP DWG路径配置不存在: {DWG_SOURCE_ROOTS_CONFIG}")
    try:
        config = tomllib.loads(DWG_SOURCE_ROOTS_CONFIG.read_text(encoding="utf-8-sig"))
    except (OSError, tomllib.TOMLDecodeError) as error:
        raise RuntimeError(f"CAD MCP DWG路径配置无法读取: {DWG_SOURCE_ROOTS_CONFIG}") from error

    roots = config.get("dwg_sources", {}).get("roots")
    if not isinstance(roots, list) or not roots or not all(isinstance(root, str) for root in roots):
        raise RuntimeError("CAD MCP DWG路径配置必须包含非空的 dwg_sources.roots 字符串列表")

    normalized = tuple(Path(root).expanduser().resolve() for root in roots)
    if len(set(normalized)) != len(normalized):
        raise RuntimeError("CAD MCP DWG路径配置包含重复根目录")
    return normalized


DWG_SOURCE_ROOTS = _load_dwg_source_roots()

mcp = FastMCP(
    "CAD",
    instructions="通过 AutoCAD 2023 进程内 .NET 插件在当前 DWG 中绘图。",
    log_level="ERROR",
)


def _validate_number(name: str, value: float) -> float:
    try:
        normalized = float(value)
    except (TypeError, ValueError) as error:
        raise ValueError(f"{name} 必须是数字") from error
    if not math.isfinite(normalized):
        raise ValueError(f"{name} 必须是有限数字")
    return normalized


def _normalize_request_id(request_id: str | None = None) -> str:
    normalized = request_id or str(uuid.uuid4())
    if not REQUEST_ID_PATTERN.fullmatch(normalized):
        raise ValueError("request_id 只能包含字母、数字、点、下划线、冒号或连字符，长度 1-128")
    return normalized


def _post_draw(payload: dict[str, Any]) -> dict[str, Any]:
    return _post_plugin("/draw_circle", payload, timeout=20)


def _plugin_health() -> dict[str, Any]:
    response = requests.get(PLUGIN_BASE_URL + "/health", timeout=3)
    response.raise_for_status()
    payload = response.json()
    if not isinstance(payload, dict) or not payload.get("ok"):
        raise RuntimeError(f"AutoCAD 插件健康检查失败: {payload}")
    return payload


def _ensure_plugin_ready() -> dict[str, Any]:
    """在新 Codex 会话中按需启动 AutoCAD；绝不终止已有用户进程。"""
    try:
        return _plugin_health()
    except (requests.exceptions.RequestException, RuntimeError) as first_error:
        with _startup_lock:
            try:
                return _plugin_health()
            except (requests.exceptions.RequestException, RuntimeError):
                acad_running = any(
                    (process.info.get("name") or "").lower() == "acad.exe"
                    for process in psutil.process_iter(["name"])
                )
                if acad_running:
                    raise RuntimeError(
                        "AutoCAD 正在运行，但 CAD MCP 插件端口 8765 未健康响应；"
                        "为保护已打开图纸，服务端不会终止该进程。"
                    ) from first_error
                if not POWERSHELL.is_file() or not AUTOCAD_LAUNCHER.is_file():
                    raise RuntimeError("CAD MCP 自动启动器或 PowerShell 运行时不存在。") from first_error
                result = subprocess.run(
                    [
                        str(POWERSHELL),
                        "-NoProfile",
                        "-ExecutionPolicy",
                        "Bypass",
                        "-File",
                        str(AUTOCAD_LAUNCHER),
                        "-WaitForHealthSeconds",
                        "90",
                    ],
                    capture_output=True,
                    text=True,
                    encoding="utf-8",
                    errors="replace",
                    timeout=100,
                    check=False,
                )
                if result.returncode != 0:
                    detail = (result.stderr or result.stdout).strip()[:800]
                    raise RuntimeError(f"CAD MCP 自动启动失败: {detail}") from first_error
                payload = _plugin_health()
                logger.info("已通过持久启动器恢复 AutoCAD CAD MCP 链路。")
                return payload


def _post_plugin(path: str, payload: dict[str, Any], timeout: int = 75) -> dict[str, Any]:
    _ensure_plugin_ready()
    response = requests.post(PLUGIN_BASE_URL + path, json=payload, timeout=timeout)
    try:
        data = response.json()
    except requests.exceptions.JSONDecodeError as error:
        raise RuntimeError(
            f"AutoCAD 插件返回非 JSON 响应，HTTP {response.status_code}: {response.text[:300]}"
        ) from error

    if response.status_code >= 400:
        detail = data.get("error") if isinstance(data, dict) else data
        raise RuntimeError(f"AutoCAD 插件执行失败，HTTP {response.status_code}: {detail}")
    if not isinstance(data, dict) or not data.get("ok"):
        raise RuntimeError(f"AutoCAD 插件未确认绘图成功: {data}")
    return data


def _with_transport_retries(callback, retries: int = 3) -> dict[str, Any]:
    """只重试连接和超时；插件已返回的确定性错误必须立即暴露。"""
    transient = (
        requests.exceptions.ConnectionError,
        requests.exceptions.Timeout,
        ConnectionError,
        TimeoutError,
    )
    for attempt in range(1, retries + 1):
        try:
            return callback()
        except transient:
            if attempt == retries:
                raise
            env.time.sleep(1)
    raise AssertionError("unreachable")


def _normalize_output_dir(output_dir: str) -> str:
    if not isinstance(output_dir, str) or not output_dir.strip():
        raise ValueError("output_dir 不能为空")
    resolved = Path(output_dir).expanduser().resolve()
    allowed = ALLOWED_OUTPUT_ROOT.resolve()
    if resolved != allowed:
        raise ValueError(f"output_dir 必须是 {allowed}")
    return str(resolved)


def _normalize_source_dwg(source_dwg: str) -> str:
    if not isinstance(source_dwg, str) or not source_dwg.strip():
        raise ValueError("source_dwg 不能为空")
    resolved = Path(source_dwg).expanduser().resolve()
    allowed = COMPANY_SOURCE_DWG.resolve()
    if resolved != allowed:
        raise ValueError(f"source_dwg 必须是 {allowed}")
    if not resolved.is_file():
        raise ValueError(f"source_dwg 不存在: {resolved}")
    return str(resolved)


def _normalize_tailrace_output_dir(output_dir: str) -> str:
    if not isinstance(output_dir, str) or not output_dir.strip():
        raise ValueError("output_dir 不能为空")
    resolved = Path(output_dir).expanduser().resolve()
    allowed = TAILRACE_OUTPUT_ROOT.resolve()
    if resolved != allowed:
        raise ValueError(f"output_dir 必须是 {allowed}")
    return str(resolved)


def _normalize_supported_tailrace_output_dir(output_dir: str) -> str:
    if not isinstance(output_dir, str) or not output_dir.strip():
        raise ValueError("output_dir 不能为空")
    resolved = Path(output_dir).expanduser().resolve()
    allowed = SUPPORTED_TAILRACE_OUTPUT_ROOT.resolve()
    if resolved != allowed:
        raise ValueError(f"output_dir 必须是 {allowed}")
    return str(resolved)


def _normalize_tailrace_source(source_dwg: str) -> str:
    if not isinstance(source_dwg, str) or not source_dwg.strip():
        raise ValueError("source_dwg 不能为空")
    resolved = Path(source_dwg).expanduser().resolve()
    allowed = TAILRACE_SOURCE_DWT.resolve()
    if resolved != allowed:
        raise ValueError(f"source_dwg 必须是 {allowed}")
    if not resolved.is_file():
        raise ValueError(f"source_dwg 不存在: {resolved}")
    return str(resolved)


def _normalize_image_redraw_output_dir(output_dir: str) -> str:
    if not isinstance(output_dir, str) or not output_dir.strip():
        raise ValueError("output_dir 不能为空")
    resolved = Path(output_dir).expanduser().resolve()
    allowed = IMAGE_REDRAW_OUTPUT_ROOT.resolve()
    if resolved != allowed:
        raise ValueError(f"output_dir 必须是 {allowed}")
    return str(resolved)


def _normalize_image_redraw_source(source_dwg: str) -> str:
    if not isinstance(source_dwg, str) or not source_dwg.strip():
        raise ValueError("source_dwg 不能为空")
    resolved = Path(source_dwg).expanduser().resolve()
    allowed = IMAGE_REDRAW_SOURCE_DWT.resolve()
    if resolved != allowed:
        raise ValueError(f"source_dwg 必须是 {allowed}")
    if not resolved.is_file():
        raise ValueError(f"source_dwg 不存在: {resolved}")
    return str(resolved)


def _normalize_image_path(image_path: str) -> str:
    if not isinstance(image_path, str) or not image_path.strip():
        raise ValueError("image_path 不能为空")
    resolved = Path(image_path).expanduser().resolve()
    allowed = IMAGE_REDRAW_SOURCE_IMAGE.resolve()
    if resolved != allowed:
        raise ValueError(f"image_path 必须是 {allowed}")
    if not resolved.is_file():
        raise ValueError(f"image_path 不存在: {resolved}")
    return str(resolved)


def _normalize_copper_waterstop_output_dir(output_dir: str) -> str:
    if not isinstance(output_dir, str) or not output_dir.strip():
        raise ValueError("output_dir 不能为空")
    resolved = Path(output_dir).expanduser().resolve()
    allowed = COPPER_WATERSTOP_OUTPUT_ROOT.resolve()
    if resolved != allowed:
        raise ValueError(f"output_dir 必须是 {allowed}")
    return str(resolved)


def _normalize_copper_waterstop_source(source_dwg: str) -> str:
    if not isinstance(source_dwg, str) or not source_dwg.strip():
        raise ValueError("source_dwg 不能为空")
    resolved = Path(source_dwg).expanduser().resolve()
    allowed = COPPER_WATERSTOP_SOURCE_DWT.resolve()
    if resolved != allowed:
        raise ValueError(f"source_dwg 必须是 {allowed}")
    if not resolved.is_file():
        raise ValueError(f"source_dwg 不存在: {resolved}")
    return str(resolved)


def _normalize_copper_waterstop_image(image_path: str) -> str:
    if not isinstance(image_path, str) or not image_path.strip():
        raise ValueError("image_path 不能为空")
    resolved = Path(image_path).expanduser().resolve()
    allowed = COPPER_WATERSTOP_SOURCE_IMAGE.resolve()
    if resolved != allowed:
        raise ValueError(f"image_path 必须是 {allowed}")
    if not resolved.is_file():
        raise ValueError(f"image_path 不存在: {resolved}")
    return str(resolved)


def _normalize_translation_path(path: str, name: str, must_exist: bool) -> str:
    return str(
        env.resolve_path_under_roots(
            path,
            DWG_SOURCE_ROOTS,
            name=name,
            suffix=".dwg",
            must_exist=must_exist,
        )
    )


def _normalize_arc_annotation_dwg(source_dwg: str) -> str:
    if not isinstance(source_dwg, str) or not source_dwg.strip():
        raise ValueError("source_dwg 不能为空")
    resolved = Path(source_dwg).expanduser().resolve()
    allowed = ARC_ANNOTATION_DWG.resolve()
    if resolved != allowed:
        raise ValueError(f"source_dwg 必须是 {allowed}")
    if not resolved.is_file():
        raise ValueError(f"source_dwg 不存在: {resolved}")
    return str(resolved)


@mcp.tool(
    name="draw_circle",
    description="在 AutoCAD 2023 当前 DWG 的模型空间中绘制圆。",
)
def draw_circle(
    x: float,
    y: float,
    radius: float,
    z: float = 0.0,
    request_id: str | None = None,
) -> dict[str, Any]:
    """绘圆；HTTP 重试始终复用同一个 request_id，避免重复实体。"""
    center_x = _validate_number("x", x)
    center_y = _validate_number("y", y)
    center_z = _validate_number("z", z)
    normalized_radius = _validate_number("radius", radius)
    if normalized_radius <= 0:
        raise ValueError("radius 必须大于 0")

    normalized_request_id = _normalize_request_id(request_id)
    payload = {
        "request_id": normalized_request_id,
        "center": {"x": center_x, "y": center_y, "z": center_z},
        "radius": normalized_radius,
    }
    logger.info("发送绘圆请求: %s", json.dumps(payload, ensure_ascii=False))

    result = _with_transport_retries(lambda: _post_draw(payload), retries=3)
    logger.info("绘圆成功: %s", json.dumps(result, ensure_ascii=False))
    return result


@mcp.tool(
    name="inspect_cad_workspace",
    description="只读检查 AutoCAD 2023 工作区，并提取公司参考 DWG 的图层、文字、标注、图块、布局及使用频次。",
)
def inspect_cad_workspace(
    source_dwg: str = str(COMPANY_SOURCE_DWG),
) -> dict[str, Any]:
    payload = {"source_dwg": _normalize_source_dwg(source_dwg)}
    logger.info("发送工作区检查请求: %s", json.dumps(payload, ensure_ascii=False))
    return _with_transport_retries(
        lambda: _post_plugin("/inspect_cad_workspace", payload), retries=3
    )


@mcp.tool(
    name="inspect_cad_translation",
    description="通过 AutoCAD Managed API 读取指定 DWG 中的文字实体，供越南语翻译使用。",
)
def inspect_cad_translation(source_dwg: str) -> dict[str, Any]:
    payload = {
        "request_id": _normalize_request_id(),
        "source_dwg": _normalize_translation_path(source_dwg, "source_dwg", True),
    }
    logger.info("发送 CAD 文字检查请求: %s", json.dumps(payload, ensure_ascii=False))
    return _with_transport_retries(
        lambda: _post_plugin("/inspect_cad_translation", payload, timeout=120), retries=3
    )


@mcp.tool(
    name="translate_cad",
    description="通过 AutoCAD Managed API 将指定 DWG 的文字映射写入新的 DWG 文件，拒绝覆盖已有文件。",
)
def translate_cad(
    source_dwg: str,
    output_dwg: str,
    translations: dict[str, str],
    request_id: str | None = None,
) -> dict[str, Any]:
    normalized_request_id = _normalize_request_id(request_id)
    source = _normalize_translation_path(source_dwg, "source_dwg", True)
    output = _normalize_translation_path(output_dwg, "output_dwg", False)
    if Path(source).resolve() == Path(output).resolve():
        raise ValueError("output_dwg 必须不同于 source_dwg")
    if Path(output).exists():
        raise ValueError(f"output_dwg 已存在，拒绝覆盖: {output}")
    if not isinstance(translations, dict):
        raise ValueError("translations 必须是字典")
    normalized_translations = {str(key): str(value) for key, value in translations.items()}
    payload = {
        "request_id": normalized_request_id,
        "source_dwg": source,
        "output_dwg": output,
        "translations": normalized_translations,
    }
    logger.info("发送 CAD 翻译请求: %s", json.dumps({**payload, "translations": len(normalized_translations)}, ensure_ascii=False))
    return _with_transport_retries(
        lambda: _post_plugin("/translate_cad", payload, timeout=120), retries=3
    )


@mcp.tool(
    name="repair_cad_fonts",
    description="通过 AutoCAD Managed API 将指定 DWG 的文字样式修复为 AutoCAD 内置 txt.shx + gbcbig.shx，并另存为新文件。",
)
def repair_cad_fonts(
    source_dwg: str,
    output_dwg: str,
    request_id: str | None = None,
    style_names: list[str] | None = None,
) -> dict[str, Any]:
    normalized_request_id = _normalize_request_id(request_id)
    source = _normalize_translation_path(source_dwg, "source_dwg", True)
    output = _normalize_translation_path(output_dwg, "output_dwg", False)
    if Path(source).resolve() == Path(output).resolve():
        raise ValueError("output_dwg 必须不同于 source_dwg")
    if Path(output).exists():
        raise ValueError(f"output_dwg 已存在，拒绝覆盖: {output}")
    payload = {
        "request_id": normalized_request_id,
        "source_dwg": source,
        "output_dwg": output,
    }
    if style_names is not None:
        if not isinstance(style_names, list) or not all(isinstance(name, str) for name in style_names):
            raise ValueError("style_names 必须是字符串列表")
        payload["style_names"] = style_names
    logger.info("发送 CAD 字体修复请求: %s", json.dumps(payload, ensure_ascii=False))
    return _with_transport_retries(
        lambda: _post_plugin("/repair_cad_fonts", payload, timeout=120), retries=3
    )


@mcp.tool(
    name="inspect_arc_annotations",
    description="只读检查 Drawing1.dwg 中的圆弧、文字、引线和现有标注格式。",
)
def inspect_arc_annotations(
    source_dwg: str = str(ARC_ANNOTATION_DWG),
) -> dict[str, Any]:
    payload = {
        "request_id": _normalize_request_id(),
        "source_dwg": _normalize_arc_annotation_dwg(source_dwg),
    }
    logger.info("发送圆弧标注检查请求: %s", json.dumps(payload, ensure_ascii=False))
    return _with_transport_retries(
        lambda: _post_plugin("/inspect_arc_annotations", payload, timeout=120), retries=3
    )


@mcp.tool(
    name="annotate_arcs",
    description="在 Drawing1.dwg 中原子标注每段圆弧的弧长和半径，并保留可恢复备份。",
)
def annotate_arcs(
    source_dwg: str = str(ARC_ANNOTATION_DWG),
    label_template: str = "R={radius}m;L={length}m",
    length_decimals: int = 1,
    radius_decimals: int = 2,
    text_height: float = 1.2,
    leader: bool = True,
    request_id: str | None = None,
) -> dict[str, Any]:
    if not isinstance(label_template, str) or "{length}" not in label_template or "{radius}" not in label_template:
        raise ValueError("label_template 必须包含 {length} 和 {radius}")
    if (
        isinstance(length_decimals, bool)
        or not isinstance(length_decimals, int)
        or not 0 <= length_decimals <= 6
        or isinstance(radius_decimals, bool)
        or not isinstance(radius_decimals, int)
        or not 0 <= radius_decimals <= 6
    ):
        raise ValueError("length_decimals 和 radius_decimals 必须是 0 到 6 的整数")
    normalized_height = _validate_number("text_height", text_height)
    if normalized_height < 0:
        raise ValueError("text_height 必须大于等于 0；0 表示自动匹配图中格式")
    if not isinstance(leader, bool):
        raise ValueError("leader 必须是布尔值")
    payload = {
        "request_id": _normalize_request_id(request_id),
        "source_dwg": _normalize_arc_annotation_dwg(source_dwg),
        "label_template": label_template,
        "length_decimals": length_decimals,
        "radius_decimals": radius_decimals,
        "text_height": normalized_height,
        "leader": leader,
    }
    logger.info("发送圆弧标注请求: %s", json.dumps(payload, ensure_ascii=False))
    return _with_transport_retries(
        lambda: _post_plugin("/annotate_arcs", payload, timeout=120), retries=3
    )


@mcp.tool(
    name="verify_arc_annotations",
    description="只读重开 Drawing1.dwg，核对圆弧标注实体及弧长、半径数值。",
)
def verify_arc_annotations(
    source_dwg: str = str(ARC_ANNOTATION_DWG),
) -> dict[str, Any]:
    payload = {
        "request_id": _normalize_request_id(),
        "source_dwg": _normalize_arc_annotation_dwg(source_dwg),
    }
    logger.info("发送圆弧标注核验请求: %s", json.dumps(payload, ensure_ascii=False))
    return _with_transport_retries(
        lambda: _post_plugin("/verify_arc_annotations", payload, timeout=120), retries=3
    )


@mcp.tool(
    name="build_slt73_template",
    description="只读克隆公司参考 DWG 的真实制图资源，清除项目几何后原子生成公司水利钢筋混凝土 DWT。",
)
def build_slt73_template(
    output_dir: str = str(ALLOWED_OUTPUT_ROOT),
    source_dwg: str = str(COMPANY_SOURCE_DWG),
    request_id: str | None = None,
) -> dict[str, Any]:
    normalized_request_id = _normalize_request_id(request_id)
    payload = {
        "request_id": normalized_request_id,
        "output_dir": _normalize_output_dir(output_dir),
        "source_dwg": _normalize_source_dwg(source_dwg),
        "standard": "COMPANY-HYDRO-RC-2026",
    }
    logger.info("发送模板构建请求: %s", json.dumps(payload, ensure_ascii=False))
    result = _with_transport_retries(
        lambda: _post_plugin("/build_slt73_template", payload, timeout=75), retries=3
    )
    logger.info("模板构建成功: %s", json.dumps(result, ensure_ascii=False))
    return result


@mcp.tool(
    name="verify_slt73_template",
    description="只读核验公司 DWT 可重开、模型空间为空、源图哈希不变且实际使用的公司样式完整保留。",
)
def verify_slt73_template(
    output_dir: str = str(ALLOWED_OUTPUT_ROOT),
    source_dwg: str = str(COMPANY_SOURCE_DWG),
) -> dict[str, Any]:
    payload = {
        "output_dir": _normalize_output_dir(output_dir),
        "source_dwg": _normalize_source_dwg(source_dwg),
        "standard": "COMPANY-HYDRO-RC-2026",
    }
    logger.info("发送模板核验请求: %s", json.dumps(payload, ensure_ascii=False))
    return _with_transport_retries(
        lambda: _post_plugin("/verify_slt73_template", payload, timeout=75), retries=3
    )


@mcp.tool(
    name="build_tailrace_culvert",
    description="在AutoCAD中原子生成按550kN场区卡车、3m覆土及1.5安全系数设计的尾水涵纵断面、横剖面、配筋图及计算说明。",
)
def build_tailrace_culvert(
    output_dir: str = str(TAILRACE_OUTPUT_ROOT),
    source_dwg: str = str(TAILRACE_SOURCE_DWT),
    length_m: float = 70.0,
    clear_width_m: float = 4.0,
    clear_height_m: float = 1.7,
    slope: float = 0.002,
    inlet_invert_m: float = 692.0,
    cover_m: float = 3.0,
    thickness_m: float = 0.4,
    bottom_thickness_m: float = 0.45,
    truck_weight_kn: float = 550.0,
    traffic_pressure_kpa: float = 20.0,
    global_safety_factor: float = 1.5,
    normal_water_m: float = 693.10,
    design_water_m: float = 693.70,
    check_water_m: float = 694.01,
    request_id: str | None = None,
) -> dict[str, Any]:
    normalized_request_id = _normalize_request_id(request_id)
    payload = {
        "request_id": normalized_request_id,
        "output_dir": _normalize_tailrace_output_dir(output_dir),
        "source_dwg": _normalize_tailrace_source(source_dwg),
        "standard": "COMPANY-HYDRO-RC-2026",
        "length_m": _validate_number("length_m", length_m),
        "clear_width_m": _validate_number("clear_width_m", clear_width_m),
        "clear_height_m": _validate_number("clear_height_m", clear_height_m),
        "slope": _validate_number("slope", slope),
        "inlet_invert_m": _validate_number("inlet_invert_m", inlet_invert_m),
        "cover_m": _validate_number("cover_m", cover_m),
        "thickness_m": _validate_number("thickness_m", thickness_m),
        "bottom_thickness_m": _validate_number("bottom_thickness_m", bottom_thickness_m),
        "truck_weight_kn": _validate_number("truck_weight_kn", truck_weight_kn),
        "traffic_pressure_kpa": _validate_number("traffic_pressure_kpa", traffic_pressure_kpa),
        "global_safety_factor": _validate_number("global_safety_factor", global_safety_factor),
        "normal_water_m": _validate_number("normal_water_m", normal_water_m),
        "design_water_m": _validate_number("design_water_m", design_water_m),
        "check_water_m": _validate_number("check_water_m", check_water_m),
    }
    for name in (
        "length_m",
        "clear_width_m",
        "clear_height_m",
        "thickness_m",
        "bottom_thickness_m",
        "truck_weight_kn",
        "traffic_pressure_kpa",
    ):
        if payload[name] <= 0:
            raise ValueError(f"{name} 必须大于 0")
    if payload["cover_m"] < 0:
        raise ValueError("cover_m 不得小于 0")
    if not 1.0 <= payload["global_safety_factor"] <= 3.0:
        raise ValueError("global_safety_factor 必须在 1.0 到 3.0 之间")
    if not 0.0 <= payload["slope"] <= 0.02:
        raise ValueError("slope 必须在 0 到 0.02 之间")
    if not payload["normal_water_m"] <= payload["design_water_m"] <= payload["check_water_m"]:
        raise ValueError("水位必须满足 normal <= design <= check")
    logger.info("发送尾水涵成套图构建请求: %s", json.dumps(payload, ensure_ascii=False))
    result = _with_transport_retries(
        lambda: _post_plugin("/build_tailrace_culvert", payload, timeout=120), retries=3
    )
    logger.info("尾水涵成套图构建成功: %s", json.dumps(result, ensure_ascii=False))
    return result


@mcp.tool(
    name="verify_tailrace_culvert",
    description="只读核验尾水涵三张DWG、三张PDF和设计计算说明是否完整且DWG可重新打开。",
)
def verify_tailrace_culvert(
    output_dir: str = str(TAILRACE_OUTPUT_ROOT),
    source_dwg: str = str(TAILRACE_SOURCE_DWT),
) -> dict[str, Any]:
    payload = {
        "output_dir": _normalize_tailrace_output_dir(output_dir),
        "source_dwg": _normalize_tailrace_source(source_dwg),
    }
    logger.info("发送尾水涵成套图核验请求: %s", json.dumps(payload, ensure_ascii=False))
    return _with_transport_retries(
        lambda: _post_plugin("/verify_tailrace_culvert", payload, timeout=75), retries=3
    )


@mcp.tool(
    name="build_supported_tailrace_culvert",
    description="在AutoCAD中原子生成总内宽4m、400mm连续中墙、双孔各净宽1.8m的尾水涵横剖面、配筋图和计算说明。",
)
def build_supported_tailrace_culvert(
    output_dir: str = str(SUPPORTED_TAILRACE_OUTPUT_ROOT),
    source_dwg: str = str(TAILRACE_SOURCE_DWT),
    length_m: float = 70.0,
    clear_width_m: float = 4.0,
    center_wall_thickness_m: float = 0.4,
    clear_height_m: float = 1.7,
    slope: float = 0.002,
    inlet_invert_m: float = 692.0,
    cover_m: float = 3.0,
    thickness_m: float = 0.4,
    bottom_thickness_m: float = 0.45,
    truck_weight_kn: float = 550.0,
    traffic_pressure_kpa: float = 20.0,
    global_safety_factor: float = 1.5,
    normal_water_m: float = 693.10,
    design_water_m: float = 693.70,
    check_water_m: float = 694.01,
    request_id: str | None = None,
) -> dict[str, Any]:
    normalized_request_id = _normalize_request_id(request_id)
    payload = {
        "request_id": normalized_request_id,
        "output_dir": _normalize_supported_tailrace_output_dir(output_dir),
        "source_dwg": _normalize_tailrace_source(source_dwg),
        "standard": "COMPANY-HYDRO-RC-2026",
        "length_m": _validate_number("length_m", length_m),
        "clear_width_m": _validate_number("clear_width_m", clear_width_m),
        "center_wall_thickness_m": _validate_number("center_wall_thickness_m", center_wall_thickness_m),
        "clear_height_m": _validate_number("clear_height_m", clear_height_m),
        "slope": _validate_number("slope", slope),
        "inlet_invert_m": _validate_number("inlet_invert_m", inlet_invert_m),
        "cover_m": _validate_number("cover_m", cover_m),
        "thickness_m": _validate_number("thickness_m", thickness_m),
        "bottom_thickness_m": _validate_number("bottom_thickness_m", bottom_thickness_m),
        "truck_weight_kn": _validate_number("truck_weight_kn", truck_weight_kn),
        "traffic_pressure_kpa": _validate_number("traffic_pressure_kpa", traffic_pressure_kpa),
        "global_safety_factor": _validate_number("global_safety_factor", global_safety_factor),
        "normal_water_m": _validate_number("normal_water_m", normal_water_m),
        "design_water_m": _validate_number("design_water_m", design_water_m),
        "check_water_m": _validate_number("check_water_m", check_water_m),
    }
    for name in (
        "length_m",
        "clear_width_m",
        "center_wall_thickness_m",
        "clear_height_m",
        "thickness_m",
        "bottom_thickness_m",
        "truck_weight_kn",
        "traffic_pressure_kpa",
    ):
        if payload[name] <= 0:
            raise ValueError(f"{name} 必须大于 0")
    if payload["center_wall_thickness_m"] >= payload["clear_width_m"]:
        raise ValueError("center_wall_thickness_m 必须小于 clear_width_m")
    cell_width = (payload["clear_width_m"] - payload["center_wall_thickness_m"]) / 2
    if not 1.75 <= cell_width <= 2.05:
        raise ValueError("双孔方案每孔净宽必须约为2.0m（允许1.75m至2.05m）")
    if payload["cover_m"] < 0:
        raise ValueError("cover_m 不得小于 0")
    if not 1.0 <= payload["global_safety_factor"] <= 3.0:
        raise ValueError("global_safety_factor 必须在 1.0 到 3.0 之间")
    if not 0.0 <= payload["slope"] <= 0.02:
        raise ValueError("slope 必须在 0 到 0.02 之间")
    if not payload["normal_water_m"] <= payload["design_water_m"] <= payload["check_water_m"]:
        raise ValueError("水位必须满足 normal <= design <= check")
    logger.info("发送尾水涵双孔中墙方案构建请求: %s", json.dumps(payload, ensure_ascii=False))
    result = _with_transport_retries(
        lambda: _post_plugin("/build_supported_tailrace_culvert", payload, timeout=120), retries=3
    )
    logger.info("尾水涵双孔中墙方案构建成功: %s", json.dumps(result, ensure_ascii=False))
    return result


@mcp.tool(
    name="verify_supported_tailrace_culvert",
    description="只读核验尾水涵双孔中墙方案的两张DWG、两张PDF和计算说明，并在AutoCAD数据库中重开DWG。",
)
def verify_supported_tailrace_culvert(
    output_dir: str = str(SUPPORTED_TAILRACE_OUTPUT_ROOT),
    source_dwg: str = str(TAILRACE_SOURCE_DWT),
) -> dict[str, Any]:
    payload = {
        "output_dir": _normalize_supported_tailrace_output_dir(output_dir),
        "source_dwg": _normalize_tailrace_source(source_dwg),
    }
    logger.info("发送尾水涵双孔中墙方案核验请求: %s", json.dumps(payload, ensure_ascii=False))
    return _with_transport_retries(
        lambda: _post_plugin("/verify_supported_tailrace_culvert", payload, timeout=75), retries=3
    )


@mcp.tool(
    name="build_image_redraw",
    description="使用公司水利DWT，在AutoCAD中将指定的2m×2m箱涵配筋JPG原子重绘为DWG。",
)
def build_image_redraw(
    output_dir: str = str(IMAGE_REDRAW_OUTPUT_ROOT),
    source_dwg: str = str(IMAGE_REDRAW_SOURCE_DWT),
    image_path: str = str(IMAGE_REDRAW_SOURCE_IMAGE),
    request_id: str | None = None,
) -> dict[str, Any]:
    normalized_request_id = _normalize_request_id(request_id)
    payload = {
        "request_id": normalized_request_id,
        "output_dir": _normalize_image_redraw_output_dir(output_dir),
        "source_dwg": _normalize_image_redraw_source(source_dwg),
        "image_path": _normalize_image_path(image_path),
        "standard": "COMPANY-HYDRO-RC-2026",
    }
    logger.info("发送图片重绘请求: %s", json.dumps(payload, ensure_ascii=False))
    result = _with_transport_retries(
        lambda: _post_plugin("/build_image_redraw", payload, timeout=120), retries=3
    )
    logger.info("图片重绘成功: %s", json.dumps(result, ensure_ascii=False))
    return result


@mcp.tool(
    name="verify_image_redraw",
    description="只读重开并核验图片重绘DWG的实体数量、图层、关键文字、尺寸和图幅范围。",
)
def verify_image_redraw(
    output_dir: str = str(IMAGE_REDRAW_OUTPUT_ROOT),
    source_dwg: str = str(IMAGE_REDRAW_SOURCE_DWT),
    image_path: str = str(IMAGE_REDRAW_SOURCE_IMAGE),
) -> dict[str, Any]:
    payload = {
        "output_dir": _normalize_image_redraw_output_dir(output_dir),
        "source_dwg": _normalize_image_redraw_source(source_dwg),
        "image_path": _normalize_image_path(image_path),
    }
    logger.info("发送图片重绘核验请求: %s", json.dumps(payload, ensure_ascii=False))
    return _with_transport_retries(
        lambda: _post_plugin("/verify_image_redraw", payload, timeout=75), retries=3
    )


@mcp.tool(
    name="build_copper_waterstop",
    description="使用公司水利DWT，在AutoCAD中将指定紫铜片止水PNG原子复绘为同目录可编辑DWG。",
)
def build_copper_waterstop(
    output_dir: str = str(COPPER_WATERSTOP_OUTPUT_ROOT),
    source_dwg: str = str(COPPER_WATERSTOP_SOURCE_DWT),
    image_path: str = str(COPPER_WATERSTOP_SOURCE_IMAGE),
    request_id: str | None = None,
) -> dict[str, Any]:
    normalized_request_id = _normalize_request_id(request_id)
    payload = {
        "request_id": normalized_request_id,
        "output_dir": _normalize_copper_waterstop_output_dir(output_dir),
        "source_dwg": _normalize_copper_waterstop_source(source_dwg),
        "image_path": _normalize_copper_waterstop_image(image_path),
        "standard": "COMPANY-HYDRO-RC-2026",
    }
    logger.info("发送紫铜片止水复绘请求: %s", json.dumps(payload, ensure_ascii=False))
    result = _with_transport_retries(
        lambda: _post_plugin("/build_copper_waterstop", payload, timeout=120), retries=3
    )
    logger.info("紫铜片止水复绘成功: %s", json.dumps(result, ensure_ascii=False))
    return result


@mcp.tool(
    name="verify_copper_waterstop",
    description="只读重开并核验紫铜片止水DWG的实体、图层、文字、尺寸、单位和图幅范围。",
)
def verify_copper_waterstop(
    output_dir: str = str(COPPER_WATERSTOP_OUTPUT_ROOT),
    source_dwg: str = str(COPPER_WATERSTOP_SOURCE_DWT),
    image_path: str = str(COPPER_WATERSTOP_SOURCE_IMAGE),
) -> dict[str, Any]:
    payload = {
        "output_dir": _normalize_copper_waterstop_output_dir(output_dir),
        "source_dwg": _normalize_copper_waterstop_source(source_dwg),
        "image_path": _normalize_copper_waterstop_image(image_path),
    }
    logger.info("发送紫铜片止水复绘核验请求: %s", json.dumps(payload, ensure_ascii=False))
    return _with_transport_retries(
        lambda: _post_plugin("/verify_copper_waterstop", payload, timeout=75), retries=3
    )


if __name__ == "__main__":
    mcp.run(transport="stdio")
