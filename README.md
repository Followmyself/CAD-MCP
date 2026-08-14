# CAD-MCP — AutoCAD Managed API 工具集

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Python](https://img.shields.io/badge/Python-3.11+-blue.svg)](https://www.python.org/)
[![AutoCAD](https://img.shields.io/badge/AutoCAD-2023-red.svg)](https://www.autodesk.com/products/autocad/)

这是一个面向真实工程图纸的 MCP 工具集。当前主链路不再让 Python 通过
COM 直接操作 CAD，而是把请求交给 AutoCAD 进程内的 .NET Framework 4.8
插件，再由 AutoCAD Managed API 完成读取、绘制、保存和重开核验。

```text
Codex / MCP 客户端
        │ stdio
        ▼
src/cadmcp_server.py
        │ HTTP（仅 127.0.0.1:8765）
        ▼
CadMcp.AutoCAD.dll
        │ DocumentLock + Transaction
        ▼
AutoCAD 2023 Managed API / DWG
```

## 当前工具（18 个）

| 类别 | MCP 工具 | 作用 |
|---|---|---|
| 基础绘图 | `draw_circle` | 在当前模型空间绘圆，使用 `request_id` 防重复 |
| 工作区检查 | `inspect_cad_workspace` | 读取参考 DWG 的图层、文字、标注、图块和布局 |
| 文字与坐标 | `inspect_cad_translation` | 读取文字句柄、图层、位置及坐标关联信息 |
| 文字与坐标 | `translate_cad` | 按文本或 `@handle:<句柄>` 精确替换并另存 DWG |
| 文字与坐标 | `repair_cad_fonts` | 修复文字样式并另存新文件 |
| 圆弧标注 | `inspect_arc_annotations` | 只读检查 Arc 与带 bulge 的 Polyline 弧段 |
| 圆弧标注 | `annotate_arcs` | 原子写入半径、弧长标签和引出线 |
| 圆弧标注 | `verify_arc_annotations` | 重开 DWG 并核对标注实体和数值 |
| 公司模板 | `build_slt73_template` | 从参考 DWG 克隆制图资源并生成 DWT |
| 公司模板 | `verify_slt73_template` | 核验 DWT 可重开、模型空间和样式资源 |
| 尾水涵 | `build_tailrace_culvert` | 生成纵断面、横剖面、配筋图和计算说明 |
| 尾水涵 | `verify_tailrace_culvert` | 核验 DWG、PDF、计算说明及 DWG 重开 |
| 双孔尾水涵 | `build_supported_tailrace_culvert` | 生成带连续中墙的双孔方案 |
| 双孔尾水涵 | `verify_supported_tailrace_culvert` | 核验双孔方案的图纸和说明 |
| 图片复绘 | `build_image_redraw` | 将箱涵配筋参考图复绘为可编辑 DWG |
| 图片复绘 | `verify_image_redraw` | 核验实体、图层、文字、尺寸和图幅 |
| 止水构造 | `build_copper_waterstop` | 复绘紫铜片止水构造图 |
| 止水构造 | `verify_copper_waterstop` | 重开并核验止水构造 DWG |

## 安全与一致性设计

- 插件只监听 `127.0.0.1:8765`，不向局域网暴露写图接口。
- 每个逻辑写入请求使用唯一 `request_id`；传输重试复用同一个 ID。
- 复杂写入采用正式打开文档、`DocumentLock`、单事务、阶段文件、重开核验和原子替换。
- 输出路径和源 DWG 使用允许列表校验，拒绝越界路径和意外覆盖。
- HTTP/AutoCAD 错误直接抛出，不返回空对象伪装成功。
- 写入工具均有对应的检查或核验路径；超时后先查日志和文件状态，再决定是否重放。

## 已验证结果

- Python 回归测试：`37/37` 通过。
- AutoCAD 插件协议测试：`26/26` 通过。
- 圆弧任务：识别并标注 51 段圆弧，生成 51 条 MText 与 51 条引出线；重开核验无数值不匹配，同一请求 ID 重放不新增实体。
- 坐标表工具：支持按实体句柄优先替换，避免相同数字文本被批量误改。特定 62 项坐标更新任务尚未完成真实写入验收，因此不在这里宣称已交付。

以上数字是当前工作站的验收快照，不代表任意 DWG 都能直接套用。公司模板、尾水涵、图片复绘和止水构造工具依赖调用方提供的参考 DWT、图片与允许路径配置；这些项目数据不包含在仓库中。

## 目录

```text
CAD-MCP/
├── src/
│   ├── cadmcp_server.py       # 当前 MCP 入口（18 个 Managed API 工具）
│   ├── check_cadmcp.py        # 部署、端口、配置和 DLL 健康检查
│   └── utils/guard.py         # 有界重试与临时缓存守卫
├── autocad-plugin/
│   ├── Plugin.cs              # 本地 HTTP 监听、主线程调度和请求路由
│   ├── *Builder.cs            # 各类 DWG 构建、检查和核验实现
│   ├── CadMcp.AutoCAD.csproj
│   ├── CadMcp.ProtocolTests.csproj
│   └── CADMCP.bundle/PackageContents.xml
├── tests/
│   ├── test_cadmcp_server.py
│   ├── test_check_cadmcp.py
│   └── list_cadmcp_tools.py   # 新 MCP 进程核对 18 个注册工具
├── requirements-cadmcp-http.txt
└── README_CADMCP_HTTP.md      # 当前链路的部署说明
```

## 构建与验证

Python 端：

```powershell
python -m pip install -r requirements-cadmcp-http.txt
python -m unittest discover -s tests -p 'test_*.py' -v
python .\tests\list_cadmcp_tools.py
```

AutoCAD 插件使用 AutoCAD 2023 Managed DLL 构建。标准安装路径可直接构建；非标准路径通过 `AutoCADDir` 覆盖：

```powershell
msbuild .\autocad-plugin\CadMcp.ProtocolTests.csproj `
  /t:Rebuild /p:Configuration=Release /p:Platform=x64 `
  /p:AutoCADDir='C:\Program Files\Autodesk\AutoCAD 2023'

.\autocad-plugin\bin\x64\ProtocolTests\CadMcp.ProtocolTests.exe
```

把构建生成的 `autocad-plugin/CADMCP.bundle` 安装到 AutoCAD 的
`ApplicationPlugins` 目录后，启动 AutoCAD 并运行：

```powershell
python .\src\check_cadmcp.py
```

MCP 客户端入口为：

```text
python <repo>\src\cadmcp_server.py
```

当前部署对源 DWG、输出目录、PowerShell、日志和启动器路径有严格允许列表。
迁移到另一台机器前，请按 [Managed API 部署说明](README_CADMCP_HTTP.md) 配置这些路径，
不要删除路径校验。

## 旧版 COM 工具

仓库仍保留 `src/server.py`、`src/cad_controller.py` 和原有 32 个 COM 工具，
用于追溯和兼容旧配置；它们不是当前 AutoCAD 2023 主链路。新增能力和工程图纸写入
应使用 `src/cadmcp_server.py` 与 `autocad-plugin/`。

## License

MIT License © 2025 曹瑞

基于 [daobataotie/CAD-MCP](https://github.com/daobataotie/CAD-MCP) 扩展开发。
