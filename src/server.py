import os
import sys
import json
import logging
from pathlib import Path
from mcp.server.models import InitializationOptions
import mcp.types as types
from mcp.server import NotificationOptions, Server
import mcp.server.stdio
from pydantic import AnyUrl
from typing import Any, Dict, List
import sys

sys.dont_write_bytecode = True

# 使用绝对导入

from nlp_processor import NLPProcessor

from cad_controller import CADController


# 配置Windows环境下的UTF-8编码
if sys.platform == "win32" and os.environ.get('PYTHONIOENCODING') is None:
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")

# 配置日志
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s',
    handlers=[
        logging.StreamHandler(),
        logging.FileHandler('cad_mcp.log', encoding='utf-8')
    ]
)


logger = logging.getLogger('mcp_cad_server')
logger.info("启动 CAD MCP 服务器")


PROMPT_TEMPLATE = """
助手的目标是演示如何使用CAD MCP服务。通过这个服务，您可以直接在对话中控制CAD软件，创建和修改图形。

<cad-mcp>

提示：
这个服务器提供了一个名为"cad-assistant"的预设提示，帮助用户通过自然语言指令控制CAD。用户可以要求绘制各种形状、修改图形元素或保存图纸。

资源：
此服务器提供"drawing://current"资源，表示当前的CAD图纸状态。

工具：

此服务器提供以下基本绘图工具：
"draw_line": 在CAD中绘制直线
"draw_circle": 在CAD中绘制圆
"draw_arc": 在CAD中绘制弧
"draw_ellipse": 在CAD中绘制椭圆
"draw_polyline": 在CAD中绘制多段线
"draw_rectangle": 在CAD中绘制矩形
"draw_text": 在CAD中添加文本
"draw_hatch": 在CAD中绘制填充
"save_drawing": 保存当前图纸
"add_dimension": 在CAD中添加线性标注
"process_command": 处理自然语言命令并转换为CAD操作

</cad-mcp>

请以友好的方式开始演示，例如："嗨！今天我将向您展示如何使用CAD MCP服务。通过这个服务，您可以直接在我们的对话中控制CAD软件，无需手动操作界面。让我们开始吧！"
"""

# 后期再做
# "erase_entity": 删除指定的实体
# "move_entity": 移动指定的实体
# "rotate_entity": 旋转指定的实体
# "scale_entity": 缩放指定的实体

class Config:

    def __init__(self):
        # 直接读取config.json文件
        config_path = os.path.join(os.path.dirname(__file__), 'config.json')
        with open(config_path, 'r', encoding='utf-8') as f:
            self.config = json.load(f)
        logger.info("配置文件加载成功")

    @property
    def server_name(self) -> str:
        return self.config['server']['name']

    @property
    def server_version(self) -> str:
        return self.config['server']['version']

class CADService:
    def __init__(self):
        """初始化CAD服务"""
        self.controller = CADController()
        self.nlp_processor = NLPProcessor()
        self.drawing_state = {
            "entities": [],
            "current_layer": "0",
            "last_command": "",
            "last_result": ""
        }
        logger.info("CAD服务已初始化")

    def start_cad(self):
        """启动CAD"""
        return self.controller.start_cad()

    def draw_line(self, start_point, end_point, layer=None, color=None, lineweight=None):
        """绘制直线"""
        if not self.controller.is_running():
            self.start_cad()

        # 使用当前图层或指定图层
        current_layer = layer or self.drawing_state["current_layer"]

        result = self.controller.draw_line(start_point, end_point, current_layer, color, lineweight)
        if result:
            self.drawing_state["entities"].append({
                "type": "line",
                "entity_id": self.controller._last_entity_id,
                "start": start_point,
                "end": end_point,
                "layer": current_layer,
                "color": color,
                "lineweight": lineweight
            })
            self.drawing_state["last_command"] = f"绘制直线从{start_point}到{end_point}"
            self.drawing_state["last_result"] = "成功"
        else:
            self.drawing_state["last_result"] = "失败"

        return result

    def draw_circle(self, center, radius, layer=None, color=None, lineweight=None):
        """绘制圆"""
        if not self.controller.is_running():
            self.start_cad()

        # 使用当前图层或指定图层
        current_layer = layer or self.drawing_state["current_layer"]

        result = self.controller.draw_circle(center, radius, current_layer, color, lineweight)
        if result:
            self.drawing_state["entities"].append({
                "type": "circle",
                "entity_id": self.controller._last_entity_id,
                "center": center,
                "radius": radius,
                "layer": current_layer,
                "color": color,
                "lineweight": lineweight
            })
            self.drawing_state["last_command"] = f"绘制圆，中心点{center}，半径{radius}，图层{current_layer}"
            self.drawing_state["last_result"] = "成功"
        else:
            self.drawing_state["last_result"] = "失败"

        return result

    def draw_arc(self, center, radius, start_angle, end_angle, layer=None, color=None, lineweight=None):
        """绘制弧"""
        if not self.controller.is_running():
            self.start_cad()

        # 使用当前图层或指定图层
        current_layer = layer or self.drawing_state["current_layer"]

        result = self.controller.draw_arc(center, radius, start_angle, end_angle, current_layer, color, lineweight)

        if result:
            self.drawing_state["entities"].append({
                "type": "arc",
                "entity_id": self.controller._last_entity_id,
                "center": center,
                "radius": radius,
                "start_angle": start_angle,
                "end_angle": end_angle,
                "layer": current_layer,
                "color": color,
                "lineweight": lineweight
            })
            self.drawing_state["last_command"] = f"绘制弧，中心点{center}，半径{radius}，起始角度{start_angle}，结束角度{end_angle}，图层{current_layer}"
            self.drawing_state["last_result"] = "成功"
        else:
            self.drawing_state["last_result"] = "失败"

        return result

    def draw_ellipse(self, center, major_axis, minor_axis, rotation=0, layer=None, color=None, lineweight=None):
        """绘制椭圆"""
        if not self.controller.is_running():
            self.start_cad()

        # 使用当前图层或指定图层
        current_layer = layer or self.drawing_state["current_layer"]

        result = self.controller.draw_ellipse(center, major_axis, minor_axis, rotation, current_layer, color, lineweight)

        if result:
            self.drawing_state["entities"].append({
                "type": "ellipse",
                "entity_id": self.controller._last_entity_id,
                "center": center,
                "major_axis": major_axis,
                "minor_axis": minor_axis,
                "rotation": rotation,
                "layer": current_layer,
                "color": color,
                "lineweight": lineweight
            })
            self.drawing_state["last_command"] = f"绘制椭圆，中心点{center}，长轴{major_axis}，短轴{minor_axis}，旋转角度{rotation}，图层{current_layer}"
            self.drawing_state["last_result"] = "成功"
        else:
            self.drawing_state["last_result"] = "失败"

        return result

    def draw_polyline(self, points, closed=False, layer=None, color=None, lineweight=None):
        """绘制多段线"""
        if not self.controller.is_running():
            self.start_cad()

        # 使用当前图层或指定图层
        current_layer = layer or self.drawing_state["current_layer"]

        result = self.controller.draw_polyline(points, closed, current_layer, color, lineweight)
        if result:
            self.drawing_state["entities"].append({
                "type": "polyline",
                "entity_id": self.controller._last_entity_id,
                "points": points,
                "closed": closed,
                "layer": current_layer,
                "color": color,
                "lineweight": lineweight
            })
            self.drawing_state["last_command"] = f"绘制多段线，点集{points}，{'闭合' if closed else '不闭合'}，图层{current_layer}"
            self.drawing_state["last_result"] = "成功"
        else:
            self.drawing_state["last_result"] = "失败"

        return result

    def draw_rectangle(self, corner1, corner2, layer=None, color=None, lineweight=None):
        """绘制矩形"""
        if not self.controller.is_running():
            self.start_cad()

        # 使用当前图层或指定图层
        current_layer = layer or self.drawing_state["current_layer"]

        result = self.controller.draw_rectangle(corner1, corner2, current_layer, color, lineweight)
        if result:
            self.drawing_state["entities"].append({
                "type": "rectangle",
                "entity_id": self.controller._last_entity_id,
                "corner1": corner1,
                "corner2": corner2,
                "layer": current_layer,
                "color": color,
                "lineweight": lineweight
            })
            self.drawing_state["last_command"] = f"绘制矩形，对角点{corner1}和{corner2}，图层{current_layer}"
            self.drawing_state["last_result"] = "成功"
        else:
            self.drawing_state["last_result"] = "失败"

        return result

    def draw_text(self, position, text, height=2.5, rotation=0, layer=None, color=None):
        """添加文本"""
        if not self.controller.is_running():
            self.start_cad()

        # 使用当前图层或指定图层
        current_layer = layer or self.drawing_state["current_layer"]

        result = self.controller.draw_text(position, text, height, rotation, current_layer, color)
        if result:
            self.drawing_state["entities"].append({
                "type": "text",
                "entity_id": self.controller._last_entity_id,
                "position": position,
                "text": text,
                "height": height,
                "rotation": rotation,
                "layer": current_layer,
                "color": color
            })
            self.drawing_state["last_command"] = f"添加文本'{text}'，位置{position}，高度{height}，旋转{rotation}"
            self.drawing_state["last_result"] = "成功"
        else:
            self.drawing_state["last_result"] = "失败"

        return result

    def draw_hatch(self, points, pattern_name="SOLID", scale=1.0, layer=None, color=None):
        """绘制填充"""
        if not self.controller.is_running():
            self.start_cad()

        # 使用当前图层或指定图层
        current_layer = layer or self.drawing_state["current_layer"]

        result = self.controller.draw_hatch(points, pattern_name, scale, current_layer, color)
        if result:
            self.drawing_state["entities"].append({
                "type": "hatch",
                "entity_id": self.controller._last_entity_id,
                "points": points,
                "pattern_name": pattern_name,
                "scale": scale,
                "layer": current_layer,
                "color": color
            })
            self.drawing_state["last_command"] = f"绘制填充，点集{points}，图案{pattern_name}，比例{scale}，图层{current_layer}"
            self.drawing_state["last_result"] = "成功"
        else:
            self.drawing_state["last_result"] = "失败"

        return result

    def add_dimension(self, dim_type="aligned", start_point=None, end_point=None,
                       center=None, radius=None, text_position=None, textheight=5,
                       rotation_angle=0, is_x=True, layer=None, color=None):
        """添加标注（统一入口，支持6种类型）

        dim_type: aligned / rotated / radial / diametric / angular / ordinate
        """
        if not self.controller.is_running():
            self.start_cad()

        current_layer = layer or self.drawing_state["current_layer"]
        result = None

        if dim_type == "aligned":
            if not start_point or not end_point:
                return None
            result = self.controller.add_dimension(start_point, end_point, text_position, textheight, current_layer, color)
        elif dim_type == "rotated":
            if not start_point or not end_point:
                return None
            result = self.controller.add_dim_rotated(start_point, end_point, text_position, rotation_angle, textheight, current_layer, color)
        elif dim_type == "radial":
            if not center or radius is None:
                return None
            result = self.controller.add_dim_radial(center, radius, text_position, textheight, current_layer, color)
        elif dim_type == "diametric":
            if not center or radius is None:
                return None
            result = self.controller.add_dim_diametric(center, radius, text_position, textheight, current_layer, color)
        elif dim_type == "angular":
            if not center or not start_point or not end_point:
                return None
            result = self.controller.add_dim_angular(center, start_point, end_point, text_position, textheight, current_layer, color)
        elif dim_type == "ordinate":
            if not start_point:
                return None
            result = self.controller.add_dim_ordinate(start_point, text_position, is_x, textheight, current_layer, color)

        if result:
            self.drawing_state["entities"].append({
                "type": "dimension",
                "dim_type": dim_type,
                "entity_id": self.controller._last_entity_id,
                "start": start_point,
                "end": end_point,
                "center": center,
                "radius": radius,
                "text_position": text_position,
                "textheight": textheight,
                "layer": current_layer,
                "color": color
            })
            self.drawing_state["last_command"] = f"添加{dim_type}标注"
            self.drawing_state["last_result"] = "成功"
        else:
            self.drawing_state["last_result"] = "失败"

        return result

    def save_drawing(self, file_path):
        """保存图纸"""
        if not self.controller.is_running():
            return False

        result = self.controller.save_drawing(file_path)
        if result:
            self.drawing_state["last_command"] = f"保存图纸到{file_path}"
            self.drawing_state["last_result"] = "成功"
        else:
            self.drawing_state["last_result"] = "失败"

        return result

    # ============================
    #  选择与查询服务方法（阶段3）
    # ============================

    def list_entities(self, entity_type: str = None):
        """列出已追踪的实体"""
        if not self.controller.is_running():
            self.start_cad()
        return self.controller.list_entities(entity_type)

    def get_entity_properties(self, handle: str):
        """获取实体属性"""
        if not self.controller.is_running():
            self.start_cad()
        return self.controller.get_entity_properties(handle)

    def select_window(self, corner1, corner2):
        """窗口选择实体"""
        if not self.controller.is_running():
            self.start_cad()
        return self.controller.select_window(corner1, corner2)

    # ============================
    #  实体修改服务方法（阶段4）
    # ============================

    def move_entity(self, handle: str, displacement):
        if not self.controller.is_running():
            self.start_cad()
        return self.controller.move_entity(handle, displacement)

    def rotate_entity(self, handle: str, base_point, angle: float):
        if not self.controller.is_running():
            self.start_cad()
        return self.controller.rotate_entity(handle, base_point, angle)

    def scale_entity(self, handle: str, base_point, scale_factor: float):
        if not self.controller.is_running():
            self.start_cad()
        return self.controller.scale_entity(handle, base_point, scale_factor)

    def erase_entity(self, handle: str):
        if not self.controller.is_running():
            self.start_cad()
        return self.controller.erase_entity(handle)

    def copy_entity(self, handle: str, displacement):
        if not self.controller.is_running():
            self.start_cad()
        return self.controller.copy_entity(handle, displacement)

    def mirror_entity(self, handle: str, point1, point2):
        if not self.controller.is_running():
            self.start_cad()
        return self.controller.mirror_entity(handle, point1, point2)

    def offset_entity(self, handle: str, distance: float):
        if not self.controller.is_running():
            self.start_cad()
        return self.controller.offset_entity(handle, distance)

    # ============================
    #  图块支持服务方法（阶段6）
    # ============================

    def create_block(self, name: str, insertion_point, entity_handles: list):
        if not self.controller.is_running():
            self.start_cad()
        return self.controller.create_block(name, insertion_point, entity_handles)

    def insert_block(self, name: str, insertion_point, scale=1.0, rotation=0.0, layer=None, color=None):
        if not self.controller.is_running():
            self.start_cad()
        return self.controller.insert_block(name, insertion_point, scale, rotation, layer, color)

    def explode_block(self, handle: str):
        if not self.controller.is_running():
            self.start_cad()
        return self.controller.explode_block(handle)

    def list_blocks(self):
        if not self.controller.is_running():
            self.start_cad()
        return self.controller.list_blocks()

    # ============================
    #  高级图层管理服务方法（阶段7）
    # ============================

    def list_layers(self):
        if not self.controller.is_running():
            self.start_cad()
        return self.controller.list_layers()

    def freeze_layer(self, layer_name: str, freeze: bool = True):
        if not self.controller.is_running():
            self.start_cad()
        return self.controller.freeze_layer(layer_name, freeze)

    def lock_layer(self, layer_name: str, lock: bool = True):
        if not self.controller.is_running():
            self.start_cad()
        return self.controller.lock_layer(layer_name, lock)

    def set_layer_color(self, layer_name: str, color):
        if not self.controller.is_running():
            self.start_cad()
        return self.controller.set_layer_color(layer_name, color)

    def set_layer_linetype(self, layer_name: str, linetype: str):
        if not self.controller.is_running():
            self.start_cad()
        return self.controller.set_layer_linetype(layer_name, linetype)

    def set_current_layer(self, layer_name: str):
        if not self.controller.is_running():
            self.start_cad()
        return self.controller.set_current_layer(layer_name)

    def delete_layer(self, layer_name: str):
        if not self.controller.is_running():
            self.start_cad()
        return self.controller.delete_layer(layer_name)

    def _resolve_color(self, raw_color):
        """颜色解析辅助"""
        if raw_color is None:
            return None
        resolved = self.nlp_processor.extract_color_from_command(raw_color)
        return resolved if resolved is not None else raw_color

    def process_command(self, command: str) -> Dict[str, Any]:
        """处理自然语言命令"""
        if not self.controller.is_running():
            self.start_cad()
        # 使用NLP处理器解析命令
        parsed_command = self.nlp_processor.process_command(command)
        command_type = parsed_command.get("type")
        try:
            # 基本绘图命令处理
            if command_type == "draw_line":
                start_point = parsed_command.get("start_point")
                end_point = parsed_command.get("end_point")
                # 获取颜色参数
                color = self._resolve_color(parsed_command.get("color"))
                lineweight = parsed_command.get("lineweight")
                result = self.draw_line(start_point, end_point, None, color, lineweight)
                return {
                    "success": result is not None,
                    "message": "直线已绘制" if result else "绘制直线失败",
                    "entity_id": result.Handle if result else None
                }

            elif command_type == "draw_circle":
                center = parsed_command.get("center")
                radius = parsed_command.get("radius")
                color = self._resolve_color(parsed_command.get("color"))
                # 获取线宽参数
                lineweight = parsed_command.get("lineweight")
                result = self.draw_circle(center, radius, None, color, lineweight)
                return {
                    "success": result is not None,
                    "message": "圆已绘制" if result else "绘制圆失败",
                    "entity_id": result.Handle if result else None
                }

            elif command_type == "draw_arc":
                center = parsed_command.get("center")
                radius = parsed_command.get("radius")
                start_angle = parsed_command.get("start_angle")
                end_angle = parsed_command.get("end_angle")
                # 确保所有必要参数都存在且有效
                if center is None or radius is None or start_angle is None or end_angle is None:
                    return {
                        "success": False,
                        "message": "绘制圆弧失败：缺少必要参数",
                        "error": "缺少必要参数：中心点、半径、起始角度或结束角度"
                    }
                color = self._resolve_color(parsed_command.get("color"))
                # 获取线宽参数
                lineweight = parsed_command.get("lineweight")
                result = self.draw_arc(center, radius, start_angle, end_angle, None, color, lineweight)

                return {
                    "success": result is not None,
                    "message": "圆弧已绘制" if result else "绘制圆弧失败",
                    "entity_id": result.Handle if result else None
                }

            elif command_type == "draw_ellipse":
                center = parsed_command.get("center")
                major_axis = parsed_command.get("major_axis")
                minor_axis = parsed_command.get("minor_axis")
                rotation = parsed_command.get("rotation", 0)  # 默认旋转角度为0
                # 确保所有必要参数都存在且有效
                if center is None or major_axis is None or minor_axis is None:
                    return {
                        "success": False,
                        "message": "绘制椭圆失败：缺少必要参数",
                        "error": "缺少必要参数：中心点、长轴或短轴"
                    }
                color = self._resolve_color(parsed_command.get("color"))
                # 获取线宽参数
                lineweight = parsed_command.get("lineweight")
                result = self.draw_ellipse(center, major_axis, minor_axis, rotation, None, color, lineweight)

                return {
                    "success": result is not None,
                    "message": "椭圆已绘制" if result else "绘制椭圆失败",
                    "entity_id": result.Handle if result else None
                }

            elif command_type == "draw_rectangle":
                corner1 = parsed_command.get("corner1")
                corner2 = parsed_command.get("corner2")
                color = self._resolve_color(parsed_command.get("color"))
                # 获取线宽参数
                lineweight = parsed_command.get("lineweight")
                result = self.draw_rectangle(corner1, corner2, None, color, lineweight)

                return {
                    "success": result is not None,
                    "message": "矩形已绘制" if result else "绘制矩形失败",
                    "entity_id": result.Handle if result else None
                }

            elif command_type == "draw_text":
                position = parsed_command.get("position")
                text = parsed_command.get("text")
                height = parsed_command.get("height", 2.5)
                rotation = parsed_command.get("rotation", 0)
                color = self._resolve_color(parsed_command.get("color"))
                result = self.draw_text(position, text, height, rotation, None, color)

                return {
                    "success": result is not None,
                    "message": "文本已添加" if result else "添加文本失败",
                    "entity_id": result.Handle if result else None
                }

            elif command_type == "draw_hatch":
                points = parsed_command.get("points")
                pattern_name = parsed_command.get("pattern_name", "SOLID")
                scale = parsed_command.get("scale", 1.0)
                # 确保所有必要参数都存在且有效
                if points is None or len(points) < 3:
                    return {
                        "success": False,
                        "message": "绘制填充失败：缺少必要参数或点数不足",
                        "error": "填充边界至少需要3个点"
                    }
                color = self._resolve_color(parsed_command.get("color"))
                result = self.draw_hatch(points, pattern_name, scale, None, color)

                return {
                    "success": result is not None,
                    "message": "填充已绘制" if result else "绘制填充失败",
                    "entity_id": result.Handle if result else None
                }

            # 处理标注
            elif command_type == "add_dimension":
                start_point = parsed_command.get("start_point")
                end_point = parsed_command.get("end_point")
                text_position = parsed_command.get("text_position")
                result = self.controller.add_dimension(start_point, end_point, text_position)
                return {
                    "success": result is not None,
                    "message": "标注已添加" if result else "添加标注失败",
                    "entity_id": result.Handle if result else None
                }

            elif command_type == "save":
                file_path = parsed_command.get("file_path")
                result = self.save_drawing(file_path)

                return {
                    "success": result,
                    "message": f"图纸已保存到 {file_path}" if result else f"保存图纸到 {file_path} 失败"
                }


            # 处理图层操作
            elif command_type == "create_layer":
                layer_name = parsed_command.get("layer_name")
                color = parsed_command.get("color", 7)
                result = self.controller.create_layer(layer_name)
                return {
                    "success": result,
                    "message": f"图层 {layer_name} 已创建" if result else f"创建图层 {layer_name} 失败"
                }

            # ============ 阶段3-8: 新增命令类型 ============

            # --- 修改操作 ---
            elif command_type == "move_entity":
                handle = parsed_command.get("handle") or self._resolve_handle_from_index(parsed_command.get("entity_index"))
                displacement = parsed_command.get("displacement", [100, 0, 0])
                if not handle:
                    return {"success": False, "message": "无法确定要移动的实体"}
                result = self.move_entity(handle, displacement)
                return {"success": result, "message": "实体已移动" if result else "移动实体失败"}

            elif command_type == "rotate_entity":
                handle = parsed_command.get("handle") or self._resolve_handle_from_index(parsed_command.get("entity_index"))
                base_point = parsed_command.get("base_point", [0, 0, 0])
                angle = parsed_command.get("angle", 90)
                if not handle:
                    return {"success": False, "message": "无法确定要旋转的实体"}
                result = self.rotate_entity(handle, base_point, angle)
                return {"success": result, "message": "实体已旋转" if result else "旋转实体失败"}

            elif command_type == "scale_entity":
                handle = parsed_command.get("handle") or self._resolve_handle_from_index(parsed_command.get("entity_index"))
                base_point = parsed_command.get("base_point", [0, 0, 0])
                scale_factor = parsed_command.get("scale_factor", 2.0)
                if not handle:
                    return {"success": False, "message": "无法确定要缩放的实体"}
                result = self.scale_entity(handle, base_point, scale_factor)
                return {"success": result, "message": "实体已缩放" if result else "缩放实体失败"}

            elif command_type == "erase_entity":
                handle = parsed_command.get("handle") or self._resolve_handle_from_index(parsed_command.get("entity_index"))
                if not handle:
                    return {"success": False, "message": "无法确定要删除的实体"}
                result = self.erase_entity(handle)
                return {"success": result, "message": "实体已删除" if result else "删除实体失败"}

            elif command_type == "copy_entity":
                handle = parsed_command.get("handle") or self._resolve_handle_from_index(parsed_command.get("entity_index"))
                displacement = parsed_command.get("displacement", [100, 0, 0])
                if not handle:
                    return {"success": False, "message": "无法确定要复制的实体"}
                result = self.copy_entity(handle, displacement)
                return {"success": result is not None, "message": "实体已复制" if result else "复制实体失败", "entity_id": result}

            elif command_type == "mirror_entity":
                handle = parsed_command.get("handle") or self._resolve_handle_from_index(parsed_command.get("entity_index"))
                point1 = parsed_command.get("point1", [0, 0, 0])
                point2 = parsed_command.get("point2", [100, 0, 0])
                if not handle:
                    return {"success": False, "message": "无法确定要镜像的实体"}
                result = self.mirror_entity(handle, point1, point2)
                return {"success": result is not None, "message": "实体已镜像" if result else "镜像实体失败", "entity_id": result}

            elif command_type == "offset_entity":
                handle = parsed_command.get("handle") or self._resolve_handle_from_index(parsed_command.get("entity_index"))
                distance = parsed_command.get("distance", 10)
                if not handle:
                    return {"success": False, "message": "无法确定要偏置的实体"}
                result = self.offset_entity(handle, distance)
                return {"success": result is not None, "message": "实体已偏置" if result else "偏置实体失败", "entity_id": result}

            # --- 查询操作 ---
            elif command_type == "list_entities":
                entity_type = parsed_command.get("entity_type")
                result = self.list_entities(entity_type)
                return {"success": True, "entities": result, "count": len(result)}

            elif command_type == "get_entity_properties":
                handle = parsed_command.get("handle") or self._resolve_handle_from_index(parsed_command.get("entity_index"))
                if not handle:
                    return {"success": False, "message": "无法确定要查询的实体"}
                result = self.get_entity_properties(handle)
                return {"success": result is not None, "properties": result}

            elif command_type == "list_layers":
                result = self.list_layers()
                return {"success": True, "layers": result, "count": len(result)}

            elif command_type == "list_blocks":
                result = self.list_blocks()
                return {"success": True, "blocks": result, "count": len(result)}

            # --- 图层管理 ---
            elif command_type == "freeze_layer":
                layer_name = parsed_command.get("layer_name", "0")
                freeze = parsed_command.get("freeze", True)
                result = self.freeze_layer(layer_name, freeze)
                return {"success": result, "message": f"图层'{layer_name}'已{'冻结' if freeze else '解冻'}" if result else "操作失败"}

            elif command_type == "unfreeze_layer":
                layer_name = parsed_command.get("layer_name", "0")
                result = self.freeze_layer(layer_name, False)
                return {"success": result, "message": f"图层'{layer_name}'已解冻" if result else "操作失败"}

            elif command_type == "lock_layer":
                layer_name = parsed_command.get("layer_name", "0")
                lock = parsed_command.get("lock", True)
                result = self.lock_layer(layer_name, lock)
                return {"success": result, "message": f"图层'{layer_name}'已{'锁定' if lock else '解锁'}" if result else "操作失败"}

            elif command_type == "unlock_layer":
                layer_name = parsed_command.get("layer_name", "0")
                result = self.lock_layer(layer_name, False)
                return {"success": result, "message": f"图层'{layer_name}'已解锁" if result else "操作失败"}

            elif command_type == "set_layer_color":
                layer_name = parsed_command.get("layer_name")
                color = parsed_command.get("color", 7)
                if not layer_name:
                    return {"success": False, "message": "缺少图层名称"}
                result = self.set_layer_color(layer_name, color)
                return {"success": result, "message": f"图层'{layer_name}'颜色已设置" if result else "设置失败"}

            elif command_type == "set_layer_linetype":
                layer_name = parsed_command.get("layer_name")
                linetype = parsed_command.get("linetype", "Continuous")
                if not layer_name:
                    return {"success": False, "message": "缺少图层名称"}
                result = self.set_layer_linetype(layer_name, linetype)
                return {"success": result, "message": f"图层'{layer_name}'线型已设置" if result else "设置失败"}

            elif command_type == "set_current_layer":
                layer_name = parsed_command.get("layer_name")
                if not layer_name:
                    return {"success": False, "message": "缺少图层名称"}
                result = self.set_current_layer(layer_name)
                return {"success": result, "message": f"当前图层已切换为'{layer_name}'" if result else "切换失败"}

            elif command_type == "delete_layer":
                layer_name = parsed_command.get("layer_name")
                if not layer_name:
                    return {"success": False, "message": "缺少图层名称"}
                result = self.delete_layer(layer_name)
                return {"success": result, "message": f"图层'{layer_name}'已删除" if result else "删除失败"}

            # --- 图块操作 ---
            elif command_type == "create_block":
                name = parsed_command.get("name")
                insertion_point = parsed_command.get("insertion_point", [0, 0, 0])
                entity_indices = parsed_command.get("entity_indices", [])
                handles = [self._resolve_handle_from_index(i) for i in entity_indices]
                handles = [h for h in handles if h]
                if not name:
                    return {"success": False, "message": "缺少块名称"}
                result = self.create_block(name, insertion_point, handles)
                return {"success": result is not None, "message": f"图块'{name}'已创建" if result else "创建失败"}

            elif command_type == "insert_block":
                name = parsed_command.get("name")
                insertion_point = parsed_command.get("insertion_point", [0, 0, 0])
                scale = parsed_command.get("scale", 1.0)
                if not name:
                    return {"success": False, "message": "缺少块名称"}
                result = self.insert_block(name, insertion_point, scale)
                return {"success": result is not None, "message": f"图块'{name}'已插入" if result else "插入失败", "entity_id": result}

            elif command_type == "explode_block":
                handle = parsed_command.get("handle") or self._resolve_handle_from_index(parsed_command.get("entity_index"))
                if not handle:
                    return {"success": False, "message": "无法确定要炸开的图块"}
                result = self.explode_block(handle)
                return {"success": len(result) > 0 if result else False, "message": f"图块已炸开，产生{len(result) if result else 0}个实体"}

            # --- 高级标注 ---
            elif command_type in ("add_dim_radial", "add_dim_diametric", "add_dim_angular", "add_dim_ordinate", "add_dimension"):
                dim_type = parsed_command.get("dim_type", "aligned")
                start_point = parsed_command.get("start_point")
                end_point = parsed_command.get("end_point")
                center = parsed_command.get("center")
                radius = parsed_command.get("radius")
                text_position = parsed_command.get("text_position")
                if dim_type == "aligned":
                    result = self.controller.add_dimension(start_point, end_point, text_position)
                elif dim_type == "radial":
                    result = self.controller.add_dim_radial(center, radius, text_position)
                elif dim_type == "diametric":
                    result = self.controller.add_dim_diametric(center, radius, text_position)
                elif dim_type == "angular":
                    result = self.controller.add_dim_angular(center, start_point, end_point, text_position)
                elif dim_type == "ordinate":
                    is_x = parsed_command.get("is_x", True)
                    result = self.controller.add_dim_ordinate(start_point, text_position, is_x)
                else:
                    result = None
                return {
                    "success": result is not None,
                    "message": f"{dim_type}标注已添加" if result else "添加标注失败",
                    "entity_id": self.controller._last_entity_id
                }

        except Exception as e:
            return {
                "success": False,
                "message": f"处理命令时出错: {str(e)}"
            }

    def _resolve_handle_from_index(self, index: int) -> str:
        """通过序号（1-based）解析实体 handle"""
        if index is None:
            return None
        try:
            entities = self.controller.list_entities()
            if 1 <= index <= len(entities):
                return entities[index - 1].get("handle")
        except Exception:
            pass
        return None

async def main():
    """主入口函数"""
    logger.info("启动 CAD MCP 服务器")

    # 加载配置
    config = Config()
    cad_service = CADService()
    server = Server(config.server_name)

    # 注册处理程序
    logger.debug("注册处理程序")

    @server.list_resources()
    async def handle_list_resources() -> list[types.Resource]:
        logger.debug("处理 list_resources 请求")
        return [
            types.Resource(
                uri=AnyUrl("drawing://current"),
                name="当前CAD图纸",
                description="当前CAD图纸的状态",
                mimeType="application/json",
            )
        ]

    @server.read_resource()
    async def handle_read_resource(uri: AnyUrl) -> str:
        logger.debug(f"处理 read_resource 请求，URI: {uri}")
        if uri.scheme != "drawing":
            logger.error(f"不支持的 URI 协议: {uri.scheme}")
            raise ValueError(f"不支持的 URI 协议: {uri.scheme}")

        path = str(uri).replace("drawing://", "")
        if not path or path != "current":
            logger.error(f"未知的资源路径: {path}")
            raise ValueError(f"未知的资源路径: {path}")

        return json.dumps(cad_service.drawing_state)


    @server.list_tools()
    async def handle_list_tools() -> list[types.Tool]:
        """列出可用工具"""
        return [
            types.Tool(
                name="draw_line",
                description="在CAD中绘制直线",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "start_point": {
                            "type": "array",
                            "description": "起点坐标 [x, y, z]",
                            "items": {"type": "number"},
                            "minItems": 2,
                            "maxItems": 3
                        },
                        "end_point": {
                            "type": "array",
                            "description": "终点坐标 [x, y, z]",
                            "items": {"type": "number"},
                            "minItems": 2,
                            "maxItems": 3
                        },
                        "layer": {"type": "string", "description": "图层名称（可选）"},
                        "color": {"type": "string", "description": "颜色名称（可选）"},
                        "lineweight": {"type": "number", "description": "线宽（可选）"}
                    },
                    "required": ["start_point", "end_point"],
                },
            ),

            types.Tool(
                name="draw_circle",
                description="在CAD中绘制圆",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "center": {
                            "type": "array",
                            "description": "圆心坐标 [x, y, z]",
                            "items": {"type": "number"},
                            "minItems": 2,
                            "maxItems": 3
                        },
                        "radius": {"type": "number", "description": "圆的半径"},
                        "layer": {"type": "string", "description": "图层名称（可选）"},
                        "color": {"type": "string", "description": "颜色名称（可选）"},
                        "lineweight": {"type": "number", "description": "线宽（可选）"}
                    },
                    "required": ["center", "radius"],
                },
            ),

            types.Tool(
                name="draw_arc",
                description="在CAD中绘制弧",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "center": {
                            "type": "array",
                            "description": "圆心坐标 [x, y, z]",
                            "items": {"type": "number"},
                            "minItems": 2,
                            "maxItems": 3
                        },
                        "radius": {"type": "number", "description": "弧的半径"},
                        "start_angle": {"type": "number", "description": "起始角度（度）"},
                        "end_angle": {"type": "number", "description": "结束角度（度）"},
                        "layer": {"type": "string", "description": "图层名称（可选）"},
                        "color": {"type": "string", "description": "颜色名称（可选）"},
                        "lineweight": {"type": "number", "description": "线宽（可选）"}
                    },
                    "required": ["center", "radius", "start_angle", "end_angle"],
                },
            ),

            types.Tool(
                name="draw_ellipse",
                description="在CAD中绘制椭圆",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "center": {
                            "type": "array",
                            "description": "椭圆中心坐标 [x, y, z]",
                            "items": {"type": "number"},
                            "minItems": 2,
                            "maxItems": 3
                        },
                        "major_axis": {"type": "number", "description": "长轴长度"},
                        "minor_axis": {"type": "number", "description": "短轴长度"},
                        "rotation": {"type": "number", "description": "旋转角度（度）（可选）"},
                        "layer": {"type": "string", "description": "图层名称（可选）"},
                        "color": {"type": "string", "description": "颜色名称（可选）"},
                        "lineweight": {"type": "number", "description": "线宽（可选）"}
                    },
                    "required": ["center", "major_axis", "minor_axis"],
                },
            ),

            types.Tool(
                name="draw_polyline",
                description="在CAD中绘制多段线",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "points": {
                            "type": "array",
                            "description": "点集 [[x1, y1, z1], [x2, y2, z2], ...]",
                            "items": {
                                "type": "array",
                                "items": {"type": "number"},
                                "minItems": 2,
                                "maxItems": 3
                            },
                            "minItems": 2
                        },
                        "closed": {"type": "boolean", "description": "是否闭合"},
                        "layer": {"type": "string", "description": "图层名称（可选）"},
                        "color": {"type": "string", "description": "颜色名称（可选）"},
                        "lineweight": {"type": "number", "description": "线宽（可选）"}
                    },
                    "required": ["points"],
                },
            ),

            types.Tool(
                name="draw_rectangle",
                description="在CAD中绘制矩形",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "corner1": {
                            "type": "array",
                            "description": "第一个角点坐标 [x, y, z]",
                            "items": {"type": "number"},
                            "minItems": 2,
                            "maxItems": 3
                        },
                        "corner2": {
                            "type": "array",
                            "description": "第二个角点坐标 [x, y, z]",
                            "items": {"type": "number"},
                            "minItems": 2,
                            "maxItems": 3
                        },
                        "layer": {"type": "string", "description": "图层名称（可选）"},
                        "color": {"type": "string", "description": "颜色名称（可选）"},
                        "lineweight": {"type": "number", "description": "线宽（可选）"}
                    },
                    "required": ["corner1", "corner2"],
                },
            ),

            types.Tool(
                name="draw_text",
                description="在CAD中添加文本",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "position": {
                            "type": "array",
                            "description": "插入点坐标 [x, y, z]",
                            "items": {"type": "number"},
                            "minItems": 2,
                            "maxItems": 3
                        },
                        "text": {"type": "string", "description": "文本内容"},
                        "height": {"type": "number", "description": "文本高度"},
                        "rotation": {"type": "number", "description": "旋转角度（度）"},
                        "layer": {"type": "string", "description": "图层名称（可选）"},
                        "color": {"type": "string", "description": "颜色名称（可选）"}
                    },
                    "required": ["position", "text"],
                },
            ),

            types.Tool(
                name="draw_hatch",
                description="在CAD中绘制填充",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "points": {
                            "type": "array",
                            "description": "填充边界点集 [[x1, y1, z1], [x2, y2, z2], ...]",
                            "items": {
                                "type": "array",
                                "items": {"type": "number"},
                                "minItems": 2,
                                "maxItems": 3
                            },
                            "minItems": 3
                        },
                        "pattern_name": {"type": "string", "description": "填充图案名称（可选，默认为SOLID）"},
                        "scale": {"type": "number", "description": "填充图案比例（可选，默认为1.0）"},
                        "layer": {"type": "string", "description": "图层名称（可选）"},
                        "color": {"type": "string", "description": "颜色名称（可选）"}
                    },
                    "required": ["points"],
                },
            ),

            types.Tool(
                name="add_dimension",
                description="在CAD中添加标注，支持对齐/旋转/半径/直径/角度/坐标标注",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "dim_type": {
                            "type": "string",
                            "description": "标注类型: aligned(对齐), rotated(旋转), radial(半径), diametric(直径), angular(角度), ordinate(坐标)。默认 aligned",
                            "enum": ["aligned", "rotated", "radial", "diametric", "angular", "ordinate"]
                        },
                        "start_point": {"type": "array", "description": "起点坐标 [x, y, z]（对齐/旋转标注用）", "items": {"type": "number"}, "minItems": 2, "maxItems": 3},
                        "end_point": {"type": "array", "description": "终点坐标 [x, y, z]（对齐/旋转/角度标注用）", "items": {"type": "number"}, "minItems": 2, "maxItems": 3},
                        "center": {"type": "array", "description": "圆心/中心坐标 [x, y, z]（半径/直径/角度标注用）", "items": {"type": "number"}, "minItems": 2, "maxItems": 3},
                        "radius": {"type": "number", "description": "半径（半径/直径标注用）"},
                        "text_position": {"type": "array", "description": "文本位置坐标 [x, y, z]，可选", "items": {"type": "number"}, "minItems": 2, "maxItems": 3},
                        "textheight": {"type": "number", "description": "标注文本高度，可选"},
                        "rotation_angle": {"type": "number", "description": "旋转角度（度）（旋转标注用）"},
                        "is_x": {"type": "boolean", "description": "坐标标注方向: true=X坐标, false=Y坐标，默认true"},
                        "layer": {"type": "string", "description": "图层名称，可选"},
                        "color": {"type": "string", "description": "颜色名称，可选"}
                    },
                },
            ),

            types.Tool(
                name="save_drawing",
                description="保存当前图纸",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "file_path": {"type": "string", "description": "保存路径"}
                    },
                    "required": ["file_path"],
                },
            ),

            types.Tool(
                name="process_command",
                description="处理自然语言命令并转换为CAD操作",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "command": {"type": "string", "description": "自然语言命令"}
                    },
                    "required": ["command"],
                },
            ),
            # ============ 阶段3: 选择与查询 ============
            types.Tool(
                name="list_entities",
                description="列出CAD中已追踪的实体，可按类型筛选",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "entity_type": {"type": "string", "description": "实体类型筛选（可选），如 line/circle/arc/polyline/text/hatch/dimension"}
                    },
                },
            ),
            types.Tool(
                name="get_entity_properties",
                description="获取指定实体的详细属性信息",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "handle": {"type": "string", "description": "实体句柄（entity_id）"}
                    },
                    "required": ["handle"],
                },
            ),
            types.Tool(
                name="select_window",
                description="通过窗口选择范围内的实体",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "corner1": {"type": "array", "description": "窗口第一个角点 [x, y]", "items": {"type": "number"}, "minItems": 2, "maxItems": 3},
                        "corner2": {"type": "array", "description": "窗口第二个角点 [x, y]", "items": {"type": "number"}, "minItems": 2, "maxItems": 3}
                    },
                    "required": ["corner1", "corner2"],
                },
            ),
            # ============ 阶段4: 实体修改 ============
            types.Tool(
                name="move_entity",
                description="按位移向量移动实体",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "handle": {"type": "string", "description": "实体句柄"},
                        "displacement": {"type": "array", "description": "位移向量 [dx, dy, dz]", "items": {"type": "number"}, "minItems": 2, "maxItems": 3}
                    },
                    "required": ["handle", "displacement"],
                },
            ),
            types.Tool(
                name="rotate_entity",
                description="绕基点旋转实体",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "handle": {"type": "string", "description": "实体句柄"},
                        "base_point": {"type": "array", "description": "旋转基点 [x, y, z]", "items": {"type": "number"}, "minItems": 2, "maxItems": 3},
                        "angle": {"type": "number", "description": "旋转角度（度）"}
                    },
                    "required": ["handle", "base_point", "angle"],
                },
            ),
            types.Tool(
                name="scale_entity",
                description="按比例缩放实体",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "handle": {"type": "string", "description": "实体句柄"},
                        "base_point": {"type": "array", "description": "缩放基点 [x, y, z]", "items": {"type": "number"}, "minItems": 2, "maxItems": 3},
                        "scale_factor": {"type": "number", "description": "缩放比例"}
                    },
                    "required": ["handle", "base_point", "scale_factor"],
                },
            ),
            types.Tool(
                name="erase_entity",
                description="删除指定实体",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "handle": {"type": "string", "description": "实体句柄"}
                    },
                    "required": ["handle"],
                },
            ),
            types.Tool(
                name="copy_entity",
                description="复制实体并偏移",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "handle": {"type": "string", "description": "实体句柄"},
                        "displacement": {"type": "array", "description": "复制偏移 [dx, dy, dz]", "items": {"type": "number"}, "minItems": 2, "maxItems": 3}
                    },
                    "required": ["handle", "displacement"],
                },
            ),
            types.Tool(
                name="mirror_entity",
                description="镜像实体",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "handle": {"type": "string", "description": "实体句柄"},
                        "point1": {"type": "array", "description": "镜像轴第一点 [x, y, z]", "items": {"type": "number"}, "minItems": 2, "maxItems": 3},
                        "point2": {"type": "array", "description": "镜像轴第二点 [x, y, z]", "items": {"type": "number"}, "minItems": 2, "maxItems": 3}
                    },
                    "required": ["handle", "point1", "point2"],
                },
            ),
            types.Tool(
                name="offset_entity",
                description="偏置实体（建筑双线等）",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "handle": {"type": "string", "description": "实体句柄"},
                        "distance": {"type": "number", "description": "偏置距离"}
                    },
                    "required": ["handle", "distance"],
                },
            ),
            # ============ 阶段6: 图块支持 ============
            types.Tool(
                name="create_block",
                description="从已有实体创建块定义",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "name": {"type": "string", "description": "块名称"},
                        "insertion_point": {"type": "array", "description": "插入基点 [x, y, z]", "items": {"type": "number"}, "minItems": 2, "maxItems": 3},
                        "entity_handles": {"type": "array", "description": "要包含的实体句柄列表", "items": {"type": "string"}}
                    },
                    "required": ["name", "insertion_point", "entity_handles"],
                },
            ),
            types.Tool(
                name="insert_block",
                description="插入块引用到图纸",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "name": {"type": "string", "description": "块名称"},
                        "insertion_point": {"type": "array", "description": "插入点 [x, y, z]", "items": {"type": "number"}, "minItems": 2, "maxItems": 3},
                        "scale": {"type": "number", "description": "缩放比例（默认1.0）"},
                        "rotation": {"type": "number", "description": "旋转角度（度，默认0）"},
                        "layer": {"type": "string", "description": "图层"},
                        "color": {"type": "string", "description": "颜色"}
                    },
                    "required": ["name", "insertion_point"],
                },
            ),
            types.Tool(
                name="explode_block",
                description="炸开块引用为独立实体",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "handle": {"type": "string", "description": "块引用实体句柄"}
                    },
                    "required": ["handle"],
                },
            ),
            types.Tool(
                name="list_blocks",
                description="列出所有块定义",
                inputSchema={
                    "type": "object",
                    "properties": {},
                },
            ),
            # ============ 阶段7: 高级图层管理 ============
            types.Tool(
                name="list_layers",
                description="列出所有图层及属性",
                inputSchema={
                    "type": "object",
                    "properties": {},
                },
            ),
            types.Tool(
                name="freeze_layer",
                description="冻结或解冻图层",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "layer_name": {"type": "string", "description": "图层名称"},
                        "freeze": {"type": "boolean", "description": "true=冻结, false=解冻"}
                    },
                    "required": ["layer_name", "freeze"],
                },
            ),
            types.Tool(
                name="lock_layer",
                description="锁定或解锁图层",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "layer_name": {"type": "string", "description": "图层名称"},
                        "lock": {"type": "boolean", "description": "true=锁定, false=解锁"}
                    },
                    "required": ["layer_name", "lock"],
                },
            ),
            types.Tool(
                name="set_layer_color",
                description="设置图层颜色",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "layer_name": {"type": "string", "description": "图层名称"},
                        "color": {"type": "string", "description": "颜色名称或索引"}
                    },
                    "required": ["layer_name", "color"],
                },
            ),
            types.Tool(
                name="set_layer_linetype",
                description="设置图层线型（自动加载线型）",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "layer_name": {"type": "string", "description": "图层名称"},
                        "linetype": {"type": "string", "description": "线型名称，如 Continuous/Dashed/Center"}
                    },
                    "required": ["layer_name", "linetype"],
                },
            ),
            types.Tool(
                name="set_current_layer",
                description="设为当前工作图层",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "layer_name": {"type": "string", "description": "图层名称"}
                    },
                    "required": ["layer_name"],
                },
            ),
            types.Tool(
                name="delete_layer",
                description="删除空图层",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "layer_name": {"type": "string", "description": "图层名称"}
                    },
                    "required": ["layer_name"],
                },
            ),
        ]

    @server.call_tool()
    async def handle_call_tool(
        name: str, arguments: dict[str, Any] | None
    ) -> list[types.TextContent | types.ImageContent | types.EmbeddedResource]:
        """处理工具执行请求"""
        try:
            if not arguments:
                raise ValueError("缺少参数")

            # 颜色解析辅助（消除 handle_call_tool 中 9 处重复代码）
            def _resolve_color(raw_color):
                """将颜色名称/索引统一解析为 CAD 颜色码"""
                if raw_color is None:
                    return None
                resolved = cad_service.nlp_processor.extract_color_from_command(raw_color)
                return resolved if resolved is not None else raw_color

            if name == "draw_line":
                start_point = arguments.get("start_point")
                end_point = arguments.get("end_point")
                layer = arguments.get("layer")
                color = _resolve_color(arguments.get("color"))
                lineweight = arguments.get("lineweight")

                if not start_point or not end_point:
                    raise ValueError("缺少起点或终点坐标")


                result = cad_service.draw_line(start_point, end_point, layer, color, lineweight)
                return [types.TextContent(type="text", text=str(result))]

            elif name == "draw_circle":
                center = arguments.get("center")
                radius = arguments.get("radius")
                layer = arguments.get("layer")
                color = arguments.get("color")
                lineweight = arguments.get("lineweight")

                color = _resolve_color(color)

                if not center or radius is None:
                    raise ValueError("缺少圆心坐标或半径")


                result = cad_service.draw_circle(center, radius, layer, color, lineweight)
                return [types.TextContent(type="text", text=str(result))]

            elif name == "draw_arc":
                center = arguments.get("center")
                radius = arguments.get("radius")
                start_angle = arguments.get("start_angle")
                end_angle = arguments.get("end_angle")
                layer = arguments.get("layer")
                color = arguments.get("color")
                lineweight = arguments.get("lineweight")

                color = _resolve_color(color)

                # 详细检查每个参数并提供具体的错误信息
                error_msgs = []
                if not center:
                    error_msgs.append("中心点坐标")
                if radius is None:
                    error_msgs.append("半径")
                if start_angle is None:
                    error_msgs.append("起始角度")
                if end_angle is None:
                    error_msgs.append("结束角度")

                if error_msgs:
                    raise ValueError(f"缺少必要参数: {', '.join(error_msgs)}")

                result = cad_service.draw_arc(center, radius, start_angle, end_angle, layer, color, lineweight)
                return [types.TextContent(type="text", text=str(result))]

            elif name == "draw_ellipse":
                center = arguments.get("center")
                major_axis = arguments.get("major_axis")
                minor_axis = arguments.get("minor_axis")
                rotation = arguments.get("rotation")
                layer = arguments.get("layer")
                color = arguments.get("color")
                lineweight = arguments.get("lineweight")

                color = _resolve_color(color)

                # 详细检查每个参数并提供具体的错误信息
                error_msgs = []
                if not center:
                    error_msgs.append("中心点坐标")
                if major_axis is None:
                    error_msgs.append("长轴")
                if minor_axis is None:
                    error_msgs.append("短轴")

                if error_msgs:
                    raise ValueError(f"缺少必要参数: {', '.join(error_msgs)}")

                result = cad_service.draw_ellipse(center, major_axis, minor_axis, rotation, layer, color, lineweight)
                return [types.TextContent(type="text", text=str(result))]

            elif name == "draw_polyline":
                points = arguments.get("points")
                closed = arguments.get("closed", False)
                layer = arguments.get("layer")
                color = arguments.get("color")
                lineweight = arguments.get("lineweight")

                color = _resolve_color(color)

                if not points or len(points) < 2:
                    raise ValueError("缺少点集或点数不足")

                result = cad_service.draw_polyline(points, closed, layer, color, lineweight)
                return [types.TextContent(type="text", text=str(result))]

            elif name == "draw_rectangle":
                corner1 = arguments.get("corner1")
                corner2 = arguments.get("corner2")
                layer = arguments.get("layer")
                color = arguments.get("color")
                lineweight = arguments.get("lineweight")

                color = _resolve_color(color)

                if not corner1 or not corner2:
                    raise ValueError("缺少角点坐标")

                result = cad_service.draw_rectangle(corner1, corner2, layer, color, lineweight)
                return [types.TextContent(type="text", text=str(result))]


            elif name == "draw_text":
                position = arguments.get("position")
                text = arguments.get("text")
                height = arguments.get("height", 2.5)
                rotation = arguments.get("rotation", 0)
                layer = arguments.get("layer")
                color = arguments.get("color")

                color = _resolve_color(color)

                if not position or not text:
                    raise ValueError("缺少插入点坐标或文本内容")

                result = cad_service.draw_text(position, text, height, rotation, layer, color)
                return [types.TextContent(type="text", text=str(result))]


            elif name == "draw_hatch":
                points = arguments.get("points")
                pattern_name = arguments.get("pattern_name", "SOLID")
                scale = arguments.get("scale", 1.0)
                layer = arguments.get("layer")
                color = arguments.get("color")

                color = _resolve_color(color)

                if not points or len(points) < 3:
                    raise ValueError("缺少点集或点数不足，填充边界至少需要3个点")

                result = cad_service.draw_hatch(points, pattern_name, scale, layer, color)
                return [types.TextContent(type="text", text=str(result))]

            elif name == "save_drawing":
                file_path = arguments.get("file_path")

                if not file_path:
                    raise ValueError("缺少保存路径")

                result = cad_service.save_drawing(file_path)
                return [types.TextContent(type="text", text=str(result))]

            elif name == "add_dimension":
                dim_type = arguments.get("dim_type", "aligned")
                start_point = arguments.get("start_point")
                end_point = arguments.get("end_point")
                center = arguments.get("center")
                radius = arguments.get("radius")
                text_position = arguments.get("text_position")
                layer = arguments.get("layer")
                color = _resolve_color(arguments.get("color"))
                textheight = arguments.get("textheight")
                rotation_angle = arguments.get("rotation_angle", 0)
                is_x = arguments.get("is_x", True)

                result = cad_service.add_dimension(
                    dim_type=dim_type, start_point=start_point, end_point=end_point,
                    center=center, radius=radius, text_position=text_position,
                    textheight=textheight, rotation_angle=rotation_angle, is_x=is_x,
                    layer=layer, color=color
                )
                return [types.TextContent(type="text", text=str(result))]

            elif name == "process_command":
                command = arguments.get("command")

                if not command:
                    raise ValueError("缺少命令")

                result = cad_service.process_command(command)
                return [types.TextContent(type="text", text=str(result))]

            # ============ 阶段3: 选择与查询 ============
            elif name == "list_entities":
                entity_type = arguments.get("entity_type")
                result = cad_service.list_entities(entity_type)
                return [types.TextContent(type="text", text=json.dumps(result, ensure_ascii=False))]

            elif name == "get_entity_properties":
                handle = arguments.get("handle")
                if not handle:
                    raise ValueError("缺少实体句柄")
                result = cad_service.get_entity_properties(handle)
                return [types.TextContent(type="text", text=json.dumps(result, ensure_ascii=False) if result else "None")]

            elif name == "select_window":
                corner1 = arguments.get("corner1")
                corner2 = arguments.get("corner2")
                if not corner1 or not corner2:
                    raise ValueError("缺少窗口角点坐标")
                result = cad_service.select_window(corner1, corner2)
                return [types.TextContent(type="text", text=json.dumps(result, ensure_ascii=False))]

            # ============ 阶段4: 实体修改 ============
            elif name == "move_entity":
                handle = arguments.get("handle")
                displacement = arguments.get("displacement")
                if not handle or not displacement:
                    raise ValueError("缺少实体句柄或位移向量")
                result = cad_service.move_entity(handle, displacement)
                return [types.TextContent(type="text", text=str(result))]

            elif name == "rotate_entity":
                handle = arguments.get("handle")
                base_point = arguments.get("base_point")
                angle = arguments.get("angle")
                if not handle or not base_point or angle is None:
                    raise ValueError("缺少必要参数")
                result = cad_service.rotate_entity(handle, base_point, angle)
                return [types.TextContent(type="text", text=str(result))]

            elif name == "scale_entity":
                handle = arguments.get("handle")
                base_point = arguments.get("base_point")
                scale_factor = arguments.get("scale_factor")
                if not handle or not base_point or scale_factor is None:
                    raise ValueError("缺少必要参数")
                result = cad_service.scale_entity(handle, base_point, scale_factor)
                return [types.TextContent(type="text", text=str(result))]

            elif name == "erase_entity":
                handle = arguments.get("handle")
                if not handle:
                    raise ValueError("缺少实体句柄")
                result = cad_service.erase_entity(handle)
                return [types.TextContent(type="text", text=str(result))]

            elif name == "copy_entity":
                handle = arguments.get("handle")
                displacement = arguments.get("displacement")
                if not handle or not displacement:
                    raise ValueError("缺少实体句柄或偏移")
                result = cad_service.copy_entity(handle, displacement)
                return [types.TextContent(type="text", text=str(result))]

            elif name == "mirror_entity":
                handle = arguments.get("handle")
                point1 = arguments.get("point1")
                point2 = arguments.get("point2")
                if not handle or not point1 or not point2:
                    raise ValueError("缺少必要参数")
                result = cad_service.mirror_entity(handle, point1, point2)
                return [types.TextContent(type="text", text=str(result))]

            elif name == "offset_entity":
                handle = arguments.get("handle")
                distance = arguments.get("distance")
                if not handle or distance is None:
                    raise ValueError("缺少必要参数")
                result = cad_service.offset_entity(handle, distance)
                return [types.TextContent(type="text", text=str(result))]

            # ============ 阶段6: 图块支持 ============
            elif name == "create_block":
                name = arguments.get("name")
                insertion_point = arguments.get("insertion_point")
                entity_handles = arguments.get("entity_handles", [])
                if not name or not insertion_point or not entity_handles:
                    raise ValueError("缺少必要参数")
                result = cad_service.create_block(name, insertion_point, entity_handles)
                return [types.TextContent(type="text", text=json.dumps(result, ensure_ascii=False))]

            elif name == "insert_block":
                name = arguments.get("name")
                insertion_point = arguments.get("insertion_point")
                scale = arguments.get("scale", 1.0)
                rotation = arguments.get("rotation", 0.0)
                layer = arguments.get("layer")
                color = arguments.get("color")
                if not name or not insertion_point:
                    raise ValueError("缺少必要参数")
                result = cad_service.insert_block(name, insertion_point, scale, rotation, layer, color)
                return [types.TextContent(type="text", text=str(result))]

            elif name == "explode_block":
                handle = arguments.get("handle")
                if not handle:
                    raise ValueError("缺少实体句柄")
                result = cad_service.explode_block(handle)
                return [types.TextContent(type="text", text=json.dumps(result, ensure_ascii=False))]

            elif name == "list_blocks":
                result = cad_service.list_blocks()
                return [types.TextContent(type="text", text=json.dumps(result, ensure_ascii=False))]

            # ============ 阶段7: 高级图层管理 ============
            elif name == "list_layers":
                result = cad_service.list_layers()
                return [types.TextContent(type="text", text=json.dumps(result, ensure_ascii=False))]

            elif name == "freeze_layer":
                layer_name = arguments.get("layer_name")
                freeze = arguments.get("freeze", True)
                if not layer_name:
                    raise ValueError("缺少图层名称")
                result = cad_service.freeze_layer(layer_name, freeze)
                return [types.TextContent(type="text", text=str(result))]

            elif name == "lock_layer":
                layer_name = arguments.get("layer_name")
                lock = arguments.get("lock", True)
                if not layer_name:
                    raise ValueError("缺少图层名称")
                result = cad_service.lock_layer(layer_name, lock)
                return [types.TextContent(type="text", text=str(result))]

            elif name == "set_layer_color":
                layer_name = arguments.get("layer_name")
                color = arguments.get("color")
                if not layer_name or not color:
                    raise ValueError("缺少参数")
                result = cad_service.set_layer_color(layer_name, color)
                return [types.TextContent(type="text", text=str(result))]

            elif name == "set_layer_linetype":
                layer_name = arguments.get("layer_name")
                linetype = arguments.get("linetype")
                if not layer_name or not linetype:
                    raise ValueError("缺少参数")
                result = cad_service.set_layer_linetype(layer_name, linetype)
                return [types.TextContent(type="text", text=str(result))]

            elif name == "set_current_layer":
                layer_name = arguments.get("layer_name")
                if not layer_name:
                    raise ValueError("缺少图层名称")
                result = cad_service.set_current_layer(layer_name)
                return [types.TextContent(type="text", text=str(result))]

            elif name == "delete_layer":
                layer_name = arguments.get("layer_name")
                if not layer_name:
                    raise ValueError("缺少图层名称")
                result = cad_service.delete_layer(layer_name)
                return [types.TextContent(type="text", text=str(result))]

            else:
                raise ValueError(f"未知工具: {name}")

        except Exception as e:
            return [types.TextContent(type="text", text=f"错误: {str(e)}")]

    @server.list_prompts()
    async def handle_list_prompts() -> list[types.Prompt]:
        logger.debug("处理 list_prompts 请求")
        return [
            types.Prompt(
                name="cad-assistant",
                description="一个用于通过自然语言控制CAD的助手",
                arguments=[],
            )
        ]

    @server.get_prompt()
    async def handle_get_prompt(name: str, arguments: dict[str, str] | None) -> types.GetPromptResult:
        logger.debug(f"处理 get_prompt 请求，名称: {name}，参数: {arguments}")
        if name != "cad-assistant":
            logger.error(f"未知的提示: {name}")
            raise ValueError(f"未知的提示: {name}")

        prompt = PROMPT_TEMPLATE
        logger.debug("生成提示模板")

        return types.GetPromptResult(
            description="CAD助手提示模板",
            messages=[
                types.PromptMessage(
                    role="user",
                    content=types.TextContent(type="text", text=prompt.strip()),
                )
            ],
        )

    async with mcp.server.stdio.stdio_server() as (read_stream, write_stream):
        logger.info("服务器正在使用 stdio 传输运行")
        await server.run(
            read_stream,
            write_stream,
            InitializationOptions(
                server_name=config.server_name,
                server_version=config.server_version,
                capabilities=server.get_capabilities(
                    notification_options=NotificationOptions(),
                    experimental_capabilities={},
                ),
            ),
        )

if __name__ == "__main__":
    import asyncio
    asyncio.run(main())
