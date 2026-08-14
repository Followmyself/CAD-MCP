"""外部连接的统一清理与重试入口。"""

from __future__ import annotations

import logging
import os
import shutil
import tempfile
import time
from collections.abc import Callable, Iterable
from pathlib import Path
from typing import TypeVar

import psutil


T = TypeVar("T")
logger = logging.getLogger("cadmcp.guard")


def resolve_path_under_roots(
    path: str | os.PathLike[str],
    roots: Iterable[str | os.PathLike[str]],
    *,
    name: str = "path",
    suffix: str | None = None,
    must_exist: bool = True,
) -> Path:
    """Resolve a user path only when it stays under a configured trusted root."""
    if not isinstance(path, (str, os.PathLike)) or not str(path).strip():
        raise ValueError(f"{name}不能为空")

    resolved = Path(path).expanduser().resolve()
    normalized_roots = tuple(Path(root).expanduser().resolve() for root in roots)
    if not normalized_roots:
        raise ValueError("未配置任何允许的路径根目录")

    inside_root = False
    for root in normalized_roots:
        try:
            resolved.relative_to(root)
        except ValueError:
            continue
        inside_root = True
        break
    if not inside_root:
        allowed = "；".join(str(root) for root in normalized_roots)
        raise ValueError(f"{name}必须位于允许的路径根目录: {allowed}")

    if suffix is not None and resolved.suffix.lower() != suffix.lower():
        raise ValueError(f"{name}必须是{suffix}文件")
    if must_exist and not resolved.is_file():
        raise ValueError(f"{name}不存在: {resolved}")
    return resolved


def kill_process_by_name(name_pattern: str) -> None:
    """终止名称匹配的残留进程，失败时抛错。"""
    pattern = name_pattern.lower()
    for process in psutil.process_iter(["name"]):
        name = process.info.get("name") or ""
        if pattern in name.lower():
            process.kill()
    time.sleep(0.5)


def clean_temp_cache(keyword: str = "gen_py") -> None:
    """清理临时目录中名称包含关键字的缓存。"""
    temp_root = tempfile.gettempdir()
    for item in os.listdir(temp_root):
        if keyword not in item:
            continue
        path = os.path.join(temp_root, item)
        if os.path.isdir(path):
            shutil.rmtree(path, ignore_errors=True)


def robust_connect(
    connect_func: Callable[[], T],
    retries: int = 3,
    process_names: Iterable[str] | None = None,
) -> T:
    """清场后重试外部连接；耗尽重试时明确失败。"""
    if retries < 1:
        raise ValueError("retries 必须大于等于 1")

    names = tuple(process_names or ())
    for name in names:
        kill_process_by_name(name)

    last_error: Exception | None = None
    for attempt in range(1, retries + 1):
        try:
            return connect_func()
        except Exception as error:
            last_error = error
            logger.warning("连接失败 (%s/%s): %s", attempt, retries, error)
            if attempt == retries:
                break
            for name in names:
                kill_process_by_name(name)
            clean_temp_cache()
            time.sleep(1)

    raise RuntimeError(
        f"【环境致命错误】已重试{retries}次，请检查{list(names) or ['127.0.0.1:8765']}是否可用。"
    ) from last_error
