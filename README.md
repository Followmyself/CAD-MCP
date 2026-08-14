# CAD-MCP — 工程制图 MCP 服务器

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Python](https://img.shields.io/badge/Python-3.8+-blue.svg)](https://www.python.org/)
[![MCP](https://img.shields.io/badge/MCP-1.0-8A2BE2.svg)](https://modelcontextprotocol.io/)

基于 MCP（Model Context Protocol）的 AutoCAD / 浩辰CAD / 中望CAD 控制服务器。通过 Claude Code 等 AI 助手，用自然语言在 CAD 中完成工程制图全流程。

> 源自 [daobataotie/CAD-MCP](https://github.com/daobataotie/CAD-MCP)，2026年6月完成 9 阶段工程制图升级。

## 特性

- 🎨 **8 种基础绘图**：直线、圆、圆弧、椭圆、多段线、矩形、文字、填充
- ✏️ **7 种修改操作**：移动、旋转、缩放、删除、复制、镜像、偏置
- 🔍 **实体查询**：按图层/类型/窗口筛选，读取几何属性（长度、面积、半径等）
- 📐 **6 种标注**：对齐、旋转、半径、直径、角度、坐标标注
- 🧱 **图块支持**：创建块定义、插入块引用、炸开块
- 🗂️ **完整图层管理**：创建/列表/冻结/锁定/颜色/线型/删除
- 💬 **自然语言命令**：支持中文口语化指令
- 🔄 **COM 重试机制**：指数退避，自动恢复瞬态错误

## 工具清单（32个）

| 类别 | 工具 |
|------|------|
| 绘图 (8) | `draw_line` `draw_circle` `draw_arc` `draw_ellipse` `draw_polyline` `draw_rectangle` `draw_text` `draw_hatch` |
| 修改 (7) | `move_entity` `rotate_entity` `scale_entity` `erase_entity` `copy_entity` `mirror_entity` `offset_entity` |
| 查询 (3) | `list_entities` `get_entity_properties` `select_window` |
| 标注 (1) | `add_dimension`（6种类型：aligned / rotated / radial / diametric / angular / ordinate） |
| 图块 (4) | `create_block` `insert_block` `explode_block` `list_blocks` |
| 图层 (7) | `list_layers` `freeze_layer` `lock_layer` `set_layer_color` `set_layer_linetype` `set_current_layer` `delete_layer` |
| 其他 (2) | `save_drawing` `process_command` |

## 快速开始

### 前置要求

- Windows 系统
- AutoCAD 2023+ / 浩辰CAD / 中望CAD（已安装）
- Python 3.8+
- pywin32

### 安装

```bash
git clone https://github.com/YOUR_USERNAME/CAD-MCP.git
cd CAD-MCP
pip install -r requirements.txt
```

### MCP 配置

将以下内容添加到 Claude Code 的 `.mcp.json`：

```json
{
  "mcpServers": {
    "CAD": {
      "command": "python.exe",
      "args": ["C:\\path\\to\\CAD-MCP\\src\\server.py"]
    }
  }
}
```

### CAD 类型配置

编辑 `src/config.json`：

```json
{
  "cad": {
    "type": "AUTOCAD",
    "startup_wait_time": 20,
    "command_delay": 0.5,
    "retry_max_attempts": 5,
    "retry_base_delay": 0.3,
    "retry_backoff_factor": 2.0
  }
}
```

支持的 CAD 类型：
- `AUTOCAD` — AutoCAD
- `GCAD` / `GStarCAD` — 浩辰CAD
- `ZWCAD` — 中望CAD

### 使用示例

打开 AutoCAD 后，在 Claude Code 中直接对话：

```
画一个圆心在(100,100)，半径50的红色圆
给这个圆添加半径标注
把这个圆向右移动200
列出所有实体
在(0,0)和(300,300)之间画一个矩形
创建一个名为"门窗"的图块包含实体1和2
列出所有图层
```

### 演示脚本

```bash
python draw_rabbit_v2.py  # 绘制一只兔子
```

## 项目结构

```
CAD-MCP/
├── src/
│   ├── server.py              # MCP 服务层（1605行）
│   ├── cad_controller.py      # COM 控制器（1458行）
│   ├── nlp_processor.py       # NLP 解析器（883行）
│   ├── config.json            # 配置文件
│   └── __init__.py
├── 绘制脚本/                   # 演示脚本
│   ├── draw_rabbit_v2.py      # 兔子绘制（稳定版）
│   └── draw_rabbit_v1.py      # 兔子绘制（完整版）
├── 配置参考/                   # MCP 配置示例
│   ├── .mcp.json
│   └── CLAUDE.md-CAD规范.txt
├── imgs/                      # 截图和演示
├── requirements.txt
└── LICENSE
```

## 技术架构

```
用户自然语言 → NLPProcessor → CADService → CADController → AutoCAD COM API
     (中文)        (解析)        (路由)        (COM封装)        (AutoCAD)
```

### COM 稳定性

内置指数退避重试机制，覆盖以下瞬态错误：

| 错误码 | 含义 |
|--------|------|
| `0x8001010A` | RPC_E_SERVERCALL_RETRYLATER |
| `0x8001010C` | RPC_E_SERVERCALL_REJECTED |
| `0x8001010D` | RPC_E_CANTCALLOUT_ININPUTSYNCCALL |
| `0x80010001` | RPC_E_CALL_REJECTED |

## 升级记录（v2.0 — 2026.06）

相比原版 10 个工具的显著增强：

- ✅ COM 重试机制：连续 20 次填充 0 失败
- ✅ 32 个 MCP 工具（从 10 个扩展）
- ✅ 实体追踪与属性查询
- ✅ 7 种修改操作
- ✅ 6 种标注类型
- ✅ 图块完整生命周期
- ✅ 高级图层管理

## License

MIT License © 2025 曹瑞

基于 [daobataotie/CAD-MCP](https://github.com/daobataotie/CAD-MCP) 扩展开发。
