from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path


SRC = Path(__file__).resolve().parents[1] / "src"
sys.path.insert(0, str(SRC))

import check_cadmcp


class ConfigLoadingTests(unittest.TestCase):
    def test_accepts_utf8_bom(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            config = Path(directory) / "config.toml"
            config.write_text(
                '[mcp_servers.CAD]\ncommand = "D:\\\\anaconda\\\\python.exe"\n',
                encoding="utf-8-sig",
            )

            loaded = check_cadmcp._load_config(config)

        self.assertEqual(
            r"D:\anaconda\python.exe",
            loaded["mcp_servers"]["CAD"]["command"],
        )

    def test_cad_config_uses_only_declared_runtime_paths(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            config = Path(directory) / "config.toml"
            config.write_text(
                "[mcp_servers.CAD]\n"
                'command = "D:\\\\anaconda\\\\python.exe"\n'
                f'args = ["{str(check_cadmcp.MCP_SERVER).replace(chr(92), chr(92) * 2)}"]\n'
                "[mcp_servers.CAD.env]\n"
                f'CADMCP_LOG_DIR = "{str(check_cadmcp.MCP_LOG_DIR).replace(chr(92), chr(92) * 2)}"\n'
                f'TEMP = "{str(check_cadmcp.TEMP_ROOT).replace(chr(92), chr(92) * 2)}"\n'
                f'TMP = "{str(check_cadmcp.TEMP_ROOT).replace(chr(92), chr(92) * 2)}"\n',
                encoding="utf-8",
            )
            cad = check_cadmcp._load_config(config)["mcp_servers"]["CAD"]

        self.assertEqual(r"D:\anaconda\python.exe", cad["command"])
        self.assertEqual([str(check_cadmcp.MCP_SERVER)], cad["args"])
        self.assertEqual(str(check_cadmcp.MCP_LOG_DIR), cad["env"]["CADMCP_LOG_DIR"])
        self.assertEqual(str(check_cadmcp.TEMP_ROOT), cad["env"]["TEMP"])
        self.assertEqual(str(check_cadmcp.TEMP_ROOT), cad["env"]["TMP"])

    def test_trusted_path_matching_is_case_and_separator_insensitive(self) -> None:
        trusted = (
            r"C:\Other;C:\PROGRAMDATA\Autodesk\ApplicationPlugins\CADMCP.bundle\Contents\Windows\\"
        )
        self.assertTrue(
            check_cadmcp._path_list_contains(
                trusted,
                Path(r"C:\ProgramData\Autodesk\ApplicationPlugins\CADMCP.bundle\Contents\Windows"),
            )
        )

    def test_trusted_path_matching_rejects_parent_only(self) -> None:
        self.assertFalse(
            check_cadmcp._path_list_contains(
                r"C:\ProgramData\Autodesk\ApplicationPlugins",
                Path(r"C:\ProgramData\Autodesk\ApplicationPlugins\CADMCP.bundle\Contents\Windows"),
            )
        )


if __name__ == "__main__":
    unittest.main()
