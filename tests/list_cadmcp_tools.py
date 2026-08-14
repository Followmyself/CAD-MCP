from __future__ import annotations

import asyncio
import json
import os
import sys
from pathlib import Path

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client


EXPECTED_TOOLS = {
    "draw_circle",
    "inspect_cad_workspace",
    "inspect_cad_translation",
    "translate_cad",
    "repair_cad_fonts",
    "inspect_arc_annotations",
    "annotate_arcs",
    "verify_arc_annotations",
    "build_slt73_template",
    "verify_slt73_template",
    "build_tailrace_culvert",
    "verify_tailrace_culvert",
    "build_supported_tailrace_culvert",
    "verify_supported_tailrace_culvert",
    "build_image_redraw",
    "verify_image_redraw",
    "build_copper_waterstop",
    "verify_copper_waterstop",
}


async def list_and_validate() -> int:
    server = Path(__file__).resolve().parents[1] / "src" / "cadmcp_server.py"
    parameters = StdioServerParameters(
        command=sys.executable,
        args=["-X", "utf8", str(server)],
        env=os.environ.copy(),
    )
    async with stdio_client(parameters) as (reader, writer):
        async with ClientSession(reader, writer) as session:
            await session.initialize()
            response = await session.list_tools()

    actual = {tool.name for tool in response.tools}
    missing = sorted(EXPECTED_TOOLS - actual)
    unexpected = sorted(actual - EXPECTED_TOOLS)
    print(
        json.dumps(
            {
                "count": len(actual),
                "tools": sorted(actual),
                "missing": missing,
                "unexpected": unexpected,
            },
            ensure_ascii=False,
        )
    )
    return 0 if not missing and not unexpected else 1


if __name__ == "__main__":
    raise SystemExit(asyncio.run(list_and_validate()))
