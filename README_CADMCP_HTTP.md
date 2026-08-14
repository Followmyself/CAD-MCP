# AutoCAD Managed API 部署说明

## 主链路

当前入口是 `src/cadmcp_server.py`。它通过 stdio 向 MCP 客户端注册工具，
并把请求发送到只监听 `127.0.0.1:8765` 的 AutoCAD 2023 进程内插件。

```text
MCP 客户端 → Python FastMCP → 本机 HTTP → AutoCAD .NET 插件 → Managed API
```

`src/server.py` 和 `src/cad_controller.py` 是保留的旧版 COM 实现，不属于当前链路。

## Python 端

```powershell
python -m pip install -r requirements-cadmcp-http.txt
python .\src\cadmcp_server.py
```

服务端在 `src/cadmcp_server.py` 顶部集中定义部署路径，包括：

- 公司参考 DWG 与模板输出目录；
- 尾水涵、图片复绘和紫铜片止水输出目录；
- 圆弧标注目标 DWG；
- DWG 允许根目录配置；
- AutoCAD 启动器、PowerShell 和日志目录。

迁移机器时必须更新这些路径并保留允许列表校验。DWG 允许根目录配置采用 TOML：

```toml
[dwg_sources]
roots = [
  'D:\CAD\Projects',
  'E:\CAD\Translation',
]
```

## AutoCAD 插件

插件源码位于 `autocad-plugin/`，目标框架为 .NET Framework 4.8，目标平台为 x64。
默认从 AutoCAD 2023 标准安装目录引用 Managed DLL；非标准安装路径用
`AutoCADDir` 构建属性覆盖：

```powershell
msbuild .\autocad-plugin\CadMcp.ProtocolTests.csproj `
  /t:Rebuild /p:Configuration=Release /p:Platform=x64 `
  /p:AutoCADDir='D:\Apps\AutoCAD 2023'
```

构建完成后运行协议测试：

```powershell
.\autocad-plugin\bin\x64\ProtocolTests\CadMcp.ProtocolTests.exe
```

`CadMcp.AutoCAD.csproj` 会把 Release DLL 和 PDB 同步到
`autocad-plugin/CADMCP.bundle/Contents/Windows/`。将整个 `CADMCP.bundle`
复制到当前用户的 AutoCAD `ApplicationPlugins` 目录，重新启动 AutoCAD，
插件会按 `PackageContents.xml` 自动加载。

## MCP 客户端配置

将命令和路径替换为本机实际值：

```json
{
  "mcpServers": {
    "CAD": {
      "command": "python.exe",
      "args": ["C:\\path\\to\\CAD-MCP\\src\\cadmcp_server.py"]
    }
  }
}
```

## 验收

```powershell
python -m unittest discover -s tests -p 'test_*.py' -v
python .\tests\list_cadmcp_tools.py
python .\src\check_cadmcp.py
```

验收至少应确认：

- Python 回归测试全部通过；
- 插件协议测试全部通过；
- `GET /health` 返回 `ok=true`；
- 构建 DLL 与已部署 DLL 哈希一致；
- MCP 客户端能看到 README 中列出的 18 个 Managed API 工具；
- 对真实写入工具，候选 DWG 非空且能够由 AutoCAD Managed API 重开核验。
