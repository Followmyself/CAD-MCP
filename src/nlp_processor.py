import logging
import re
import math
from typing import Any, Dict, List, Optional, Tuple
import json
import os
import os.path

# 直接读取config.json文件
config_path = os.path.join(os.path.dirname(__file__), 'config.json')
with open(config_path, 'r', encoding='utf-8') as f:
    config = json.load(f)

logger = logging.getLogger('nlp_processor')

class NLPProcessor:
    """自然语言处理器类，负责解析用户指令并转换为CAD操作"""

    def __init__(self):
        """
        初始化NLP处理器
        """

        self.logger = logging.getLogger(__name__)
        self.logger.info("自然语言处理器已初始化")

        # 对于老版本的CAD不支持设置RGB颜色，所以为了兼容性，按照索引设置颜色

        # 详细颜色名称到RGB值的映射
        self.color_rgb_map = {
            # 基本颜色
            "红色": 1,
            "黄色": 2,
            "绿色": 3,
            "青色": 4,
            "蓝色": 5,
            "洋红色": 6,
            "白色": 7,
            "灰色": 8,
            "浅灰色": 9,
            "黑色": 250,
            "棕色": 251,
            "橙色": 30,
            "紫色": 200,
            "粉色": 221,


            # 基本颜色 - 英文键
            "Red": 1,
            "Yellow": 2,
            "Green": 3,
            "Cyan": 4,
            "Blue": 5,
            "Magenta": 6,
            "White": 7,
            "Gray": 8,
            "Light Gray": 9,
            "Black": 250,
            "Brown": 251,
            "Orange": 30,
            "Purple": 200,
            "Pink": 221,

        }

        # 扩展关键词映射
        self.shape_keywords = {
            # 基本形状
            "直线": "line", "线": "line",
            "圆": "circle", "圆形": "circle",
            "弧": "arc", "圆弧": "arc",
            "矩形": "rectangle", "方形": "rectangle", "正方形": "square",
            "多段线": "polyline", "折线": "polyline",
            "文本": "text", "文字": "text",
            "标注": "dimension", "尺寸标注": "dimension",

        }

        # 动作关键词映射
        self.action_keywords = {
            # 基本动作
            "画": "draw", "绘制": "draw", "创建": "draw", "添加": "draw",
            "修改": "modify", "调整": "modify", "改变": "modify",
            "移动": "move", "旋转": "rotate", "缩放": "scale",
            "放大": "scale_up", "缩小": "scale_down",
            "删除": "erase", "擦除": "erase", "移除": "erase",
            "复制": "copy", "拷贝": "copy",
            "镜像": "mirror", "偏置": "offset", "偏移": "offset",
            "保存": "save",

            # 查询动作
            "列出": "list", "查询": "query", "查看": "query", "显示": "list",
            "获取": "query", "属性": "query",

            # 专业操作
            "标注": "dimension",
            "填充": "hatch",
            "炸开": "explode", "分解": "explode",
            "插入": "insert",
        }

        # 修改操作关键词
        self.modify_keywords = {
            "移动": "move", "平移": "move",
            "旋转": "rotate", "转动": "rotate",
            "缩放": "scale", "放大": "scale", "缩小": "scale",
            "删除": "erase", "擦除": "erase", "移除": "erase",
            "复制": "copy", "拷贝": "copy",
            "镜像": "mirror", "对称": "mirror",
            "偏置": "offset", "偏移": "offset",
        }

        # 图层操作关键词
        self.layer_keywords = {
            "冻结": "freeze", "解冻": "unfreeze",
            "锁定": "lock", "解锁": "unlock",
            "颜色": "color",
            "线型": "linetype",
            "当前图层": "set_current",
        }

        # 查询关键词
        self.query_keywords = {
            "列出": "list", "显示": "list", "所有": "list",
            "属性": "properties", "信息": "info",
            "是什么": "what_is", "有哪些": "list",
        }


    def extract_color_from_command(self, command: str) -> Optional[int]:

        if command is None:
            return 7

        try:
            num = int(command)
            if num >= 1 and num <= 255:
                return num
        except:
            pass

        # 将命令转换为小写
        command = command.lower()

        # 尝试匹配颜色名称
        for color_name in self.color_rgb_map.keys():
            if color_name.lower() in command:
                return self.color_rgb_map[color_name]

        # 尝试匹配颜色描述（如"淡蓝色"）
        color_pattern = r'([深浅淡]?[a-zA-Z一-龥]+色)'
        color_matches = re.findall(color_pattern, command)

        for color_match in color_matches:
            # 检查是否是已知的颜色名称
            if color_match in self.color_rgb_map:
                return self.color_rgb_map[color_match]

        # 如果找不到颜色信息，返回7 默认白色
        return 7

    def process_command(self, command: str) -> Dict[str, Any]:
        """处理自然语言命令并返回结果"""
        self.logger.info(f"处理命令: {command}")

        # 解析命令
        parsed_command = self.parse_command(command)

        # 返回解析结果
        return parsed_command

    def parse_command(self, command: str) -> Dict[str, Any]:
        """解析自然语言命令并转换为CAD操作参数"""
        self.logger.info(f"解析命令: {command}")

        # 将命令转换为小写并去除多余空格
        command = command.lower().strip()

        # 尝试识别命令类型
        command_type = self._identify_command_type(command)
        self.logger.debug(f"识别到的命令类型: {command_type}")

        # 根据命令类型分发到不同的处理函数
        # --- 基本绘图 ---
        if command_type == "draw_line":
            return self._parse_draw_line(command)
        elif command_type == "draw_circle":
            return self._parse_draw_circle(command)
        elif command_type == "draw_arc":
            return self._parse_draw_arc(command)
        elif command_type == "draw_rectangle":
            return self._parse_draw_rectangle(command)
        elif command_type == "draw_polyline":
            return self._parse_draw_polyline(command)
        elif command_type == "draw_text":
            return self._parse_draw_text(command)
        elif command_type == "draw_hatch":
            return self._parse_draw_hatch(command)
        elif command_type == "add_dimension":
            return self._parse_draw_hatch(command)  # 沿用已有逻辑

        # --- 修改操作 ---
        elif command_type == "move_entity":
            return self._parse_move_entity(command)
        elif command_type == "rotate_entity":
            return self._parse_rotate_entity(command)
        elif command_type == "scale_entity":
            return self._parse_scale_entity(command)
        elif command_type == "erase_entity":
            return self._parse_erase_entity(command)
        elif command_type == "copy_entity":
            return self._parse_copy_entity(command)
        elif command_type == "mirror_entity":
            return self._parse_mirror_entity(command)
        elif command_type == "offset_entity":
            return self._parse_offset_entity(command)

        # --- 查询操作 ---
        elif command_type == "list_entities":
            return self._parse_list_entities(command)
        elif command_type == "get_entity_properties":
            return self._parse_get_entity_properties(command)
        elif command_type == "list_layers":
            return self._parse_list_layers(command)
        elif command_type == "list_blocks":
            return self._parse_list_blocks(command)

        # --- 图层管理 ---
        elif command_type == "freeze_layer":
            return self._parse_freeze_layer(command)
        elif command_type == "unfreeze_layer":
            return self._parse_unfreeze_layer(command)
        elif command_type == "lock_layer":
            return self._parse_lock_layer(command)
        elif command_type == "unlock_layer":
            return self._parse_unlock_layer(command)
        elif command_type == "set_layer_color":
            return self._parse_set_layer_color(command)
        elif command_type == "set_layer_linetype":
            return self._parse_set_layer_linetype(command)
        elif command_type == "set_current_layer":
            return self._parse_set_current_layer(command)
        elif command_type == "delete_layer":
            return self._parse_delete_layer(command)
        elif command_type == "create_layer":
            return self._parse_create_layer(command)

        # --- 图块操作 ---
        elif command_type == "create_block":
            return self._parse_create_block(command)
        elif command_type == "insert_block":
            return self._parse_insert_block(command)
        elif command_type == "explode_block":
            return self._parse_explode_block(command)

        # --- 高级标注 ---
        elif command_type == "add_dim_radial":
            return self._parse_add_dim_radial(command)
        elif command_type == "add_dim_diametric":
            return self._parse_add_dim_diametric(command)
        elif command_type == "add_dim_angular":
            return self._parse_add_dim_angular(command)
        elif command_type == "add_dim_ordinate":
            return self._parse_add_dim_ordinate(command)

        elif command_type == "save":
            return self._parse_save(command)

        else:
            return {
                "type": "unknown",
                "error": "无法识别的命令类型",
                "original_command": command
            }

    def _identify_command_type(self, command: str) -> str:
        """识别命令类型（扩展版——覆盖绘图/修改/查询/图层/图块/标注）"""
        # ---- 查询操作 ----
        if any(kw in command for kw in ["列出", "显示"]):
            if "图层" in command:
                return "list_layers"
            if "块" in command or "图块" in command:
                return "list_blocks"
            if "实体" in command or "图形" in command or "对象" in command:
                return "list_entities"
            if "属性" in command:
                return "get_entity_properties"
            return "list_entities"

        if "属性" in command and ("查询" in command or "查看" in command or "获取" in command):
            return "get_entity_properties"

        # ---- 修改操作 ----
        for kw, action in self.modify_keywords.items():
            if kw in command:
                if action == "move":
                    return "move_entity"
                elif action == "rotate":
                    return "rotate_entity"
                elif action == "scale":
                    return "scale_entity"
                elif action == "erase":
                    return "erase_entity"
                elif action == "copy":
                    return "copy_entity"
                elif action == "mirror":
                    return "mirror_entity"
                elif action == "offset":
                    return "offset_entity"

        # ---- 图层管理 ----
        if "图层" in command:
            if "冻结" in command:
                return "freeze_layer"
            if "解冻" in command:
                return "unfreeze_layer"
            if "锁定" in command:
                return "lock_layer"
            if "解锁" in command:
                return "unlock_layer"
            if "颜色" in command:
                return "set_layer_color"
            if "线型" in command:
                return "set_layer_linetype"
            if "删除" in command:
                return "delete_layer"
            if any(action in command for action in ["创建", "新建", "添加"]):
                return "create_layer"
            if "当前" in command or "切换" in command:
                return "set_current_layer"
            return "list_layers"

        # ---- 图块操作 ----
        if "块" in command or "图块" in command:
            if "创建" in command or "新建" in command or "定义" in command:
                return "create_block"
            if "插入" in command:
                return "insert_block"
            if "炸开" in command or "分解" in command or "打散" in command:
                return "explode_block"

        # ---- 标注操作 ----
        if "标注" in command:
            if "半径" in command:
                return "add_dim_radial"
            if "直径" in command:
                return "add_dim_diametric"
            if "角度" in command:
                return "add_dim_angular"
            if "坐标" in command:
                return "add_dim_ordinate"
            return "add_dimension"

        # ---- 基本绘图 ----
        for action, action_type in self.action_keywords.items():
            if action in command:
                for shape, shape_type in self.shape_keywords.items():
                    if shape in command:
                        if action_type == "draw":
                            if shape_type == "line":
                                return "draw_line"
                            elif shape_type == "circle":
                                return "draw_circle"
                            elif shape_type == "arc":
                                return "draw_arc"
                            elif shape_type in ["rectangle", "square"]:
                                return "draw_rectangle"
                            elif shape_type == "polyline":
                                return "draw_polyline"
                            elif shape_type == "text":
                                return "draw_text"
                            elif shape_type == "dimension":
                                return "add_dimension"

        if "保存" in command:
            return "save"

        return "unknown"

    def _extract_coordinates(self, text: str) -> List[Tuple[float, float, float]]:
        """从文本中提取坐标点"""
        # 匹配坐标格式: (x,y,z) 或 (x,y) 或 x,y,z 或 x,y
        pattern = r'\(?\s*(-?\d+\.?\d*)\s*,\s*(-?\d+\.?\d*)(?:\s*,\s*(-?\d+\.?\d*))?\s*\)?'
        matches = re.finditer(pattern, text)

        coordinates = []
        for match in matches:
            x = float(match.group(1))
            y = float(match.group(2))
            z = float(match.group(3)) if match.group(3) else 0.0
            coordinates.append((x, y, z))

        return coordinates

    def _extract_numbers(self, text: str) -> List[float]:
        """从文本中提取数字"""
        pattern = r'(-?\d+\.?\d*)'
        matches = re.findall(pattern, text)
        return [float(match) for match in matches]

    def _parse_draw_line(self, command: str) -> Dict[str, Any]:
        """解析绘制直线命令"""
        # 尝试提取坐标
        coordinates = self._extract_coordinates(command)

        if len(coordinates) >= 2:
            # 如果找到至少两个坐标点，使用前两个作为起点和终点
            return {
                "type": "draw_line",
                "start_point": coordinates[0],
                "end_point": coordinates[1]
            }
        else:
            # 如果没有找到足够的坐标点，尝试使用默认值
            # 这里可以根据需要设置默认的起点和终点
            return {
                "type": "draw_line",
                "start_point": (0, 0, 0),
                "end_point": (100, 100, 0),
                "note": "使用默认坐标，因为命令中未提供足够的坐标信息"
            }

    def _parse_draw_circle(self, command: str) -> Dict[str, Any]:
        """解析绘制圆命令"""
        # 尝试提取坐标和半径
        coordinates = self._extract_coordinates(command)
        numbers = self._extract_numbers(command)

        # 提取半径
        radius = None
        radius_pattern = r'(?:半径|r|radius)[^\d]*?(-?\d+\.?\d*)'
        radius_match = re.search(radius_pattern, command, re.IGNORECASE)
        if radius_match:
            radius = float(radius_match.group(1))
        elif len(numbers) > 0:
            # 如果没有明确指定半径，使用找到的第一个数字作为半径
            radius = numbers[0]
        else:
            # 默认半径
            radius = 50.0

        # 提取中心点
        center = None
        if len(coordinates) > 0:
            center = coordinates[0]
        else:
            # 默认中心点
            center = (0, 0, 0)

        return {
            "type": "draw_circle",
            "center": center,
            "radius": radius
        }

    def _parse_draw_arc(self, command: str) -> Dict[str, Any]:
        """解析绘制圆弧命令"""
        # 尝试提取坐标、半径和角度
        coordinates = self._extract_coordinates(command)
        numbers = self._extract_numbers(command)

        # 提取中心点
        center = None
        if len(coordinates) > 0:
            center = coordinates[0]
        else:
            # 默认中心点
            center = (0, 0, 0)

        # 提取半径
        radius = None
        radius_pattern = r'(?:半径|r|radius)[^\d]*?(-?\d+\.?\d*)'
        radius_match = re.search(radius_pattern, command, re.IGNORECASE)
        if radius_match:
            radius = float(radius_match.group(1))
        elif len(numbers) > 0:
            # 如果没有明确指定半径，使用找到的第一个数字作为半径
            radius = numbers[0]
        else:
            # 默认半径
            radius = 50.0

        # 提取起始角度和结束角度
        start_angle = 0.0
        end_angle = 90.0

        start_angle_pattern = r'(?:起始角度|start angle)[^\d]*?(-?\d+\.?\d*)'
        start_angle_match = re.search(start_angle_pattern, command, re.IGNORECASE)
        if start_angle_match:
            start_angle = float(start_angle_match.group(1))

        end_angle_pattern = r'(?:结束角度|end angle)[^\d]*?(-?\d+\.?\d*)'
        end_angle_match = re.search(end_angle_pattern, command, re.IGNORECASE)
        if end_angle_match:
            end_angle = float(end_angle_match.group(1))

        # 确保所有必要参数都有值
        if center is None:
            center = (0, 0, 0)
        if radius is None:
            radius = 50.0
        if start_angle is None:
            start_angle = 0.0
        if end_angle is None:
            end_angle = 90.0

        self.logger.debug(f"解析圆弧命令结果: 中心点={center}, 半径={radius}, 起始角度={start_angle}, 结束角度={end_angle}")

        return {
            "type": "draw_arc",
            "center": center,
            "radius": radius,
            "start_angle": start_angle,
            "end_angle": end_angle
        }

    def _parse_draw_rectangle(self, command: str) -> Dict[str, Any]:
        """解析绘制矩形命令"""
        # 尝试提取坐标
        coordinates = self._extract_coordinates(command)

        if len(coordinates) >= 2:
            # 如果找到至少两个坐标点，使用前两个作为对角点
            return {
                "type": "draw_rectangle",
                "corner1": coordinates[0],
                "corner2": coordinates[1]
            }
        else:
            # 如果没有找到足够的坐标点，尝试提取宽度和高度
            width = 100.0
            height = 100.0

            width_pattern = r'(?:宽度|width)[^\d]*?(-?\d+\.?\d*)'
            width_match = re.search(width_pattern, command, re.IGNORECASE)
            if width_match:
                width = float(width_match.group(1))

            height_pattern = r'(?:高度|height)[^\d]*?(-?\d+\.?\d*)'
            height_match = re.search(height_pattern, command, re.IGNORECASE)
            if height_match:
                height = float(height_match.group(1))

            # 如果找到一个坐标点，使用它作为起点
            if len(coordinates) == 1:
                corner1 = coordinates[0]
                corner2 = (corner1[0] + width, corner1[1] + height, corner1[2])
            else:
                # 默认起点和终点
                corner1 = (0, 0, 0)
                corner2 = (width, height, 0)

            return {
                "type": "draw_rectangle",
                "corner1": corner1,
                "corner2": corner2
            }

    def _parse_draw_polyline(self, command: str) -> Dict[str, Any]:
        """解析绘制多段线命令"""
        # 尝试提取坐标
        coordinates = self._extract_coordinates(command)

        # 检查是否需要闭合
        closed = "闭合" in command or "封闭" in command

        if len(coordinates) >= 2:
            # 如果找到至少两个坐标点，使用它们作为多段线的点
            return {
                "type": "draw_polyline",
                "points": coordinates,
                "closed": closed
            }
        else:
            # 如果没有找到足够的坐标点，返回错误
            return {
                "type": "error",
                "message": "绘制多段线需要至少两个坐标点"
            }

    def _parse_draw_text(self, command: str) -> Dict[str, Any]:
        """解析绘制文本命令"""
        # 尝试提取坐标
        coordinates = self._extract_coordinates(command)

        # 提取文本内容
        text_pattern = r'[文本内容|text|内容][：:]\s*[\"\'](.*?)[\"\']'
        text_match = re.search(text_pattern, command)

        text = ""
        if text_match:
            text = text_match.group(1)
        else:
            # 尝试提取引号中的内容作为文本
            quote_pattern = r'[\"\'](.*?)[\"\']'
            quote_match = re.search(quote_pattern, command)
            if quote_match:
                text = quote_match.group(1)
            else:
                # 如果没有找到引号中的内容，使用默认文本
                text = "示例文本"

        # 提取文本高度
        height = 2.5  # 默认高度
        height_pattern = r'(?:高度|height)[^\d]*?(-?\d+\.?\d*)'
        height_match = re.search(height_pattern, command, re.IGNORECASE)
        if height_match:
            height = float(height_match.group(1))

        # 提取旋转角度
        rotation = 0.0  # 默认角度
        rotation_pattern = r'(?:旋转|角度|rotation)[^\d]*?(-?\d+\.?\d*)'
        rotation_match = re.search(rotation_pattern, command, re.IGNORECASE)
        if rotation_match:
            rotation = float(rotation_match.group(1))

        # 提取插入点
        position = None
        if len(coordinates) > 0:
            position = coordinates[0]
        else:
            # 默认插入点
            position = (0, 0, 0)

        return {
            "type": "draw_text",
            "position": position,
            "text": text,
            "height": height,
            "rotation": rotation
        }

    def _parse_draw_hatch(self, command: str) -> Dict[str, Any]:
        """解析绘制填充命令"""
        # 尝试提取坐标点集
        coordinates = self._extract_coordinates(command)

        # 提取填充图案名称
        pattern_name = "SOLID"  # 默认为实体填充
        pattern_patterns = [
            r'(?:图案|pattern)[^\w]*?["\'](.*?)["\']\'',
            r'(?:图案|pattern)[^\w]*?(\w+)'
        ]

        for pattern in pattern_patterns:
            pattern_match = re.search(pattern, command, re.IGNORECASE)
            if pattern_match:
                pattern_name = pattern_match.group(1).upper()
                break

        # 提取填充比例
        scale = 1.0  # 默认比例
        scale_pattern = r'(?:比例|缩放|scale)[^\d]*?(\d+\.?\d*)'
        scale_match = re.search(scale_pattern, command, re.IGNORECASE)
        if scale_match:
            scale = float(scale_match.group(1))

        # 检查是否有足够的点来定义填充边界
        if len(coordinates) >= 3:
            self.logger.debug(f"解析填充命令结果: 点集={coordinates}, 图案={pattern_name}, 比例={scale}")
            return {
                "type": "draw_hatch",
                "points": coordinates,
                "pattern_name": pattern_name,
                "scale": scale
            }
        else:
            # 如果没有足够的点，返回错误信息
            self.logger.warning("填充命令解析失败: 需要至少3个点来定义填充边界")
            return {
                "type": "error",
                "message": "绘制填充需要至少3个点来定义边界"
            }


    # ============================
    #  修改操作解析
    # ============================

    def _parse_move_entity(self, command: str) -> Dict[str, Any]:
        """解析移动实体命令，如 '移动第3个圆向右100'"""
        coordinates = self._extract_coordinates(command)
        numbers = self._extract_numbers(command)
        result = {"type": "move_entity"}

        # 尝试提取实体序号
        index = self._extract_entity_index(command)
        if index is not None:
            result["entity_index"] = index

        # 提取位移
        displacement = [0, 0, 0]
        # 方向关键词
        if "向右" in command or "右边" in command:
            displacement[0] = numbers[-1] if numbers else 100
        elif "向左" in command or "左边" in command:
            displacement[0] = -(numbers[-1]) if numbers else -100
        elif "向上" in command or "上面" in command:
            displacement[1] = numbers[-1] if numbers else 100
        elif "向下" in command or "下面" in command:
            displacement[1] = -(numbers[-1]) if numbers else -100

        if len(coordinates) >= 1:
            displacement = [coordinates[-1][0], coordinates[-1][1], 0]

        if len(numbers) >= 1:
            # 使用最后一个数字作为位移量
            if displacement == [0, 0, 0]:
                displacement[0] = numbers[-1]

        result["displacement"] = displacement
        return result

    def _parse_rotate_entity(self, command: str) -> Dict[str, Any]:
        """解析旋转命令"""
        coordinates = self._extract_coordinates(command)
        numbers = self._extract_numbers(command)
        result = {"type": "rotate_entity"}

        index = self._extract_entity_index(command)
        if index is not None:
            result["entity_index"] = index

        # 提取旋转角度
        angle = 90.0
        angle_patterns = [
            r'(\d+\.?\d*)\s*度',
            r'(?:旋转|角度)[^\d]*(\d+\.?\d*)',
        ]
        for pat in angle_patterns:
            m = re.search(pat, command)
            if m:
                angle = float(m.group(1))
                break
        if not angle and numbers:
            angle = numbers[-1]

        result["angle"] = angle

        # 提取基点
        if coordinates:
            result["base_point"] = coordinates[0]
        else:
            result["base_point"] = (0, 0, 0)

        return result

    def _parse_scale_entity(self, command: str) -> Dict[str, Any]:
        """解析缩放命令"""
        numbers = self._extract_numbers(command)
        coordinates = self._extract_coordinates(command)
        result = {"type": "scale_entity"}

        index = self._extract_entity_index(command)
        if index is not None:
            result["entity_index"] = index

        factor = 2.0
        factor_pat = r'(\d+\.?\d*)\s*倍'
        m = re.search(factor_pat, command)
        if m:
            factor = float(m.group(1))
        elif numbers:
            factor = numbers[-1]

        result["scale_factor"] = factor
        result["base_point"] = coordinates[0] if coordinates else (0, 0, 0)
        return result

    def _parse_erase_entity(self, command: str) -> Dict[str, Any]:
        """解析删除命令"""
        result = {"type": "erase_entity"}
        index = self._extract_entity_index(command)
        if index is not None:
            result["entity_index"] = index
        return result

    def _parse_copy_entity(self, command: str) -> Dict[str, Any]:
        """解析复制命令"""
        coordinates = self._extract_coordinates(command)
        numbers = self._extract_numbers(command)
        result = {"type": "copy_entity"}

        index = self._extract_entity_index(command)
        if index is not None:
            result["entity_index"] = index

        displacement = [100, 0, 0]
        if len(coordinates) >= 1:
            displacement = [coordinates[-1][0], coordinates[-1][1], 0]
        elif numbers:
            displacement = [numbers[-1], 0, 0]

        result["displacement"] = displacement
        return result

    def _parse_mirror_entity(self, command: str) -> Dict[str, Any]:
        """解析镜像命令"""
        coordinates = self._extract_coordinates(command)
        result = {"type": "mirror_entity"}

        index = self._extract_entity_index(command)
        if index is not None:
            result["entity_index"] = index

        if len(coordinates) >= 2:
            result["point1"] = coordinates[0]
            result["point2"] = coordinates[1]
        else:
            result["point1"] = (0, 0, 0)
            result["point2"] = (100, 0, 0)

        return result

    def _parse_offset_entity(self, command: str) -> Dict[str, Any]:
        """解析偏置命令"""
        numbers = self._extract_numbers(command)
        result = {"type": "offset_entity"}

        index = self._extract_entity_index(command)
        if index is not None:
            result["entity_index"] = index

        distance = 10.0
        dist_pat = r'(?:距离|偏置|偏移)[^\d]*(\d+\.?\d*)'
        m = re.search(dist_pat, command)
        if m:
            distance = float(m.group(1))
        elif numbers:
            distance = numbers[-1]

        result["distance"] = distance
        return result

    # ============================
    #  查询操作解析
    # ============================

    def _parse_list_entities(self, command: str) -> Dict[str, Any]:
        """解析列出实体命令"""
        result = {"type": "list_entities"}
        for shape, shape_type in self.shape_keywords.items():
            if shape in command:
                result["entity_type"] = shape_type
                break
        return result

    def _parse_get_entity_properties(self, command: str) -> Dict[str, Any]:
        """解析查询属性命令"""
        result = {"type": "get_entity_properties"}
        index = self._extract_entity_index(command)
        if index is not None:
            result["entity_index"] = index
        return result

    def _parse_list_layers(self, command: str) -> Dict[str, Any]:
        return {"type": "list_layers"}

    def _parse_list_blocks(self, command: str) -> Dict[str, Any]:
        return {"type": "list_blocks"}

    # ============================
    #  图层操作解析
    # ============================

    def _parse_freeze_layer(self, command: str) -> Dict[str, Any]:
        """解析冻结图层命令"""
        result = {"type": "freeze_layer", "freeze": True}
        layer_name = self._extract_layer_name(command)
        if layer_name:
            result["layer_name"] = layer_name
        return result

    def _parse_unfreeze_layer(self, command: str) -> Dict[str, Any]:
        """解析解冻图层命令"""
        result = {"type": "freeze_layer", "freeze": False}
        layer_name = self._extract_layer_name(command)
        if layer_name:
            result["layer_name"] = layer_name
        return result

    def _parse_lock_layer(self, command: str) -> Dict[str, Any]:
        result = {"type": "lock_layer", "lock": True}
        layer_name = self._extract_layer_name(command)
        if layer_name:
            result["layer_name"] = layer_name
        return result

    def _parse_unlock_layer(self, command: str) -> Dict[str, Any]:
        result = {"type": "lock_layer", "lock": False}
        layer_name = self._extract_layer_name(command)
        if layer_name:
            result["layer_name"] = layer_name
        return result

    def _parse_set_layer_color(self, command: str) -> Dict[str, Any]:
        result = {"type": "set_layer_color"}
        layer_name = self._extract_layer_name(command)
        if layer_name:
            result["layer_name"] = layer_name
        color = self.extract_color_from_command(command)
        result["color"] = color
        return result

    def _parse_set_layer_linetype(self, command: str) -> Dict[str, Any]:
        result = {"type": "set_layer_linetype"}
        layer_name = self._extract_layer_name(command)
        if layer_name:
            result["layer_name"] = layer_name
        # 提取线型名称
        lt_patterns = [r'为(\w+)', r'设置(\w+)', r'(\w+)线型']
        for pat in lt_patterns:
            m = re.search(pat, command)
            if m:
                result["linetype"] = m.group(1).capitalize()
                break
        return result

    def _parse_set_current_layer(self, command: str) -> Dict[str, Any]:
        result = {"type": "set_current_layer"}
        layer_name = self._extract_layer_name(command)
        if layer_name:
            result["layer_name"] = layer_name
        return result

    def _parse_delete_layer(self, command: str) -> Dict[str, Any]:
        result = {"type": "delete_layer"}
        layer_name = self._extract_layer_name(command)
        if layer_name:
            result["layer_name"] = layer_name
        return result

    def _parse_create_layer(self, command: str) -> Dict[str, Any]:
        result = {"type": "create_layer"}
        layer_name = self._extract_layer_name(command)
        if layer_name:
            result["layer_name"] = layer_name
        return result

    # ============================
    #  图块操作解析
    # ============================

    def _parse_create_block(self, command: str) -> Dict[str, Any]:
        """解析创建图块命令"""
        coordinates = self._extract_coordinates(command)
        result = {"type": "create_block"}

        # 提取块名
        name_pat = r'(?:创建|新建|定义).*?[块图块][\s"\'""]*(\w+)'
        m = re.search(name_pat, command)
        if m:
            result["name"] = m.group(1)

        # 提取实体序号
        entity_pat = r'实体\s*(\d+(?:\s*[和,、\s]\s*\d+)*)'
        m = re.search(entity_pat, command)
        if m:
            indices = re.findall(r'\d+', m.group(1))
            result["entity_indices"] = [int(i) for i in indices]

        result["insertion_point"] = coordinates[0] if coordinates else (0, 0, 0)
        return result

    def _parse_insert_block(self, command: str) -> Dict[str, Any]:
        """解析插入图块命令"""
        coordinates = self._extract_coordinates(command)
        numbers = self._extract_numbers(command)
        result = {"type": "insert_block"}

        # 提取块名
        name_pat = r'(?:插入|放置).*?[块图块][\s"\'""]*(\w+)'
        m = re.search(name_pat, command)
        if m:
            result["name"] = m.group(1)

        result["insertion_point"] = coordinates[0] if coordinates else (0, 0, 0)
        result["scale"] = numbers[-1] if numbers else 1.0
        return result

    def _parse_explode_block(self, command: str) -> Dict[str, Any]:
        """解析炸开图块命令"""
        result = {"type": "explode_block"}
        index = self._extract_entity_index(command)
        if index is not None:
            result["entity_index"] = index
        return result

    # ============================
    #  高级标注解析
    # ============================

    def _parse_add_dim_radial(self, command: str) -> Dict[str, Any]:
        coordinates = self._extract_coordinates(command)
        numbers = self._extract_numbers(command)
        result = {"type": "add_dimension", "dim_type": "radial"}
        result["center"] = coordinates[0] if coordinates else (0, 0, 0)
        result["radius"] = numbers[-1] if numbers else 50
        return result

    def _parse_add_dim_diametric(self, command: str) -> Dict[str, Any]:
        coordinates = self._extract_coordinates(command)
        numbers = self._extract_numbers(command)
        result = {"type": "add_dimension", "dim_type": "diametric"}
        result["center"] = coordinates[0] if coordinates else (0, 0, 0)
        result["radius"] = (numbers[-1] / 2) if numbers else 50
        return result

    def _parse_add_dim_angular(self, command: str) -> Dict[str, Any]:
        coordinates = self._extract_coordinates(command)
        result = {"type": "add_dimension", "dim_type": "angular"}
        if len(coordinates) >= 3:
            result["center"] = coordinates[0]
            result["start_point"] = coordinates[1]
            result["end_point"] = coordinates[2]
        elif len(coordinates) >= 1:
            result["center"] = coordinates[0]
        return result

    def _parse_add_dim_ordinate(self, command: str) -> Dict[str, Any]:
        coordinates = self._extract_coordinates(command)
        result = {"type": "add_dimension", "dim_type": "ordinate"}
        result["start_point"] = coordinates[0] if coordinates else (0, 0, 0)
        return result

    # ============================
    #  辅助方法
    # ============================

    def _extract_entity_index(self, command: str) -> int:
        """提取实体序号，如 '第3个' -> 3, '第1个' -> 1"""
        patterns = [
            r'第\s*(\d+)\s*个',
            r'第\s*(\d+)',
            r'(\d+)号',
            r'实体\s*(\d+)',
        ]
        for pat in patterns:
            m = re.search(pat, command)
            if m:
                return int(m.group(1))
        return None

    def _extract_layer_name(self, command: str) -> str:
        """提取图层名称"""
        patterns = [
            r'图层\s*(\w+)',
            r'图层[\s\"\'“”]*(\w+)',
        ]
        for pat in patterns:
            m = re.search(pat, command)
            if m:
                return m.group(1)
        return None

    def _parse_save(self, command: str) -> Dict[str, Any]:
        """解析保存命令"""
        # 尝试提取文件路径
        path_pattern = r'(?:路径|保存到|path)[^\w]*?[\"\'](.*?)[\"\']'
        path_match = re.search(path_pattern, command, re.IGNORECASE)

        if path_match:
            file_path = path_match.group(1)
        else:
            # 默认文件路径
            file_path = os.path.join(config["output"]["directory"], config["output"]["default_filename"])

        return {
            "type": "save",
            "file_path": file_path
        }
