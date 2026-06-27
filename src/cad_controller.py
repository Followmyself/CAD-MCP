import logging
import math
import time
import os
import json
from typing import Any, Dict, List, Optional, Tuple, Union

# 直接读取config.json文件
config_path = os.path.join(os.path.dirname(__file__), 'config.json')
with open(config_path, 'r', encoding='utf-8') as f:
    config = json.load(f)

try:
    import win32com.client
    # pythoncom是pywin32的一部分，不需要单独安装
    import pythoncom
except ImportError:
    logging.error("无法导入win32com.client或pythoncom，请确保已安装pywin32库")
    raise

logger = logging.getLogger('cad_controller')

class CADController:
    """CAD控制器类，负责与CAD应用程序交互"""

    # COM 重试相关常量
    _RETRYABLE_HRESULTS = {
        -2147418111,  # RPC_E_SERVERCALL_RETRYLATER (0x8001010A)
        -2147418109,  # RPC_E_SERVERCALL_REJECTED (0x8001010C)
        -2147418108,  # RPC_E_CANTCALLOUT_ININPUTSYNCCALL (0x8001010D)
        -2147023170,  # RPC_E_CALL_REJECTED (0x80010001)
    }

    def __init__(self):
        """初始化CAD控制器"""
        self.app = None
        self.doc = None
        self.entities = {}  # 存储已创建图形的实体引用，用于后续修改
        self._last_entity_id = None  # 最后创建的实体ID
        # 从配置文件加载参数
        self.startup_wait_time = config["cad"]["startup_wait_time"]
        self.command_delay = config["cad"]["command_delay"]
        # 获取CAD类型
        self.cad_type = config["cad"]["type"]
        # COM 重试配置
        self.retry_max_attempts = config["cad"].get("retry_max_attempts", 5)
        self.retry_base_delay = config["cad"].get("retry_base_delay", 0.3)
        self.retry_backoff_factor = config["cad"].get("retry_backoff_factor", 2.0)
        # 有效的线宽值列表
        self.valid_lineweights = [0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211]
        logger.info("CAD控制器已初始化")

    def _get_cad_app_info(self):
        """获取 CAD 应用标识符和名称，按配置的 cad_type 匹配"""
        cad_map = {
            "autocad": ("AutoCAD.Application", "AutoCAD"),
            "gcad": ("GCAD.Application", "浩辰CAD"),
            "gstarcad": ("GCAD.Application", "浩辰CAD"),
            "zwcad": ("ZWCAD.Application", "中望CAD"),
        }
        return cad_map.get(self.cad_type.lower(), ("AutoCAD.Application", "AutoCAD"))

    def start_cad(self) -> bool:
        """启动CAD并创建或打开一个文档"""
        try:
            # 初始化COM
            pythoncom.CoInitialize()

            # 存储旧实例引用（如果有）以便后续清理
            old_app = None
            if self.app is not None:
                old_app = self.app
                self.app = None
                self.doc = None

            try:
                app_id, app_name = self._get_cad_app_info()

                # 尝试连接到已运行的CAD实例
                logger.info(f"尝试连接现有{app_name}实例...")
                try:
                    self.app = self._com_retry(lambda: win32com.client.GetActiveObject(app_id))
                    logger.info(f"成功连接到已运行的{app_name}实例")
                except Exception as e:
                    logger.info(f"未找到运行中的{app_name}实例，将尝试启动新实例: {str(e)}")
                    raise

                # 已在上面的代码中处理

                # 如果当前没有文档，创建一个新文档
                try:
                    doc_count = self._com_prop(self.app.Documents, 'Count')
                    if doc_count == 0:
                        logger.info("创建新文档...")
                        self.doc = self._com_retry(lambda: self.app.Documents.Add())
                    else:
                        logger.info("获取活动文档...")
                        self.doc = self._com_prop(self.app, 'ActiveDocument')
                except Exception as doc_ex:
                    # 如果获取文档失败，强制创建新文档
                    logger.warning(f"获取文档失败，尝试创建新文档: {str(doc_ex)}")
                    try:
                        # 关闭所有打开的文档
                        doc_count = self._com_prop(self.app.Documents, 'Count')
                        for i in range(doc_count):
                            try:
                                self._com_retry(lambda i=i: self.app.Documents.Item(0).Close(False))
                            except:
                                pass

                        # 创建新文档
                        self.doc = self._com_retry(lambda: self.app.Documents.Add())
                    except Exception as new_doc_ex:
                        logger.error(f"创建新文档失败: {str(new_doc_ex)}")
                        raise

            except Exception as app_ex:
                # 如果连接失败，启动一个新实例
                logger.info(f"连接失败，正在启动新的CAD实例: {str(app_ex)}")
                try:
                    app_id, app_name = self._get_cad_app_info()

                    logger.info(f"正在启动{app_name}实例...")
                    self.app = self._com_retry(lambda: win32com.client.Dispatch(app_id))
                    self._com_prop(self.app, 'Visible', True)

                    # 等待CAD启动
                    time.sleep(self.startup_wait_time)  # 使用配置的等待时间

                    # 创建新文档
                    logger.info("尝试创建新文档...")
                    # self.doc = self.app.Documents.Add()
                    self.doc = self.app.ActiveDocument
                except Exception as new_app_ex:
                    logger.error(f"启动新CAD实例失败: {str(new_app_ex)}")
                    raise

            # 额外安全检查和等待
            time.sleep(2)  # 给CAD更多时间处理文档创建

            if self.doc is None:
                raise Exception("无法获取有效的Document对象")

            # 尝试读取文档属性以验证其有效性
            try:
                name = self._com_prop(self.doc, 'Name')
                logger.info(f"文档名称: {name}")
            except Exception as name_ex:
                logger.error(f"无法读取文档名称: {str(name_ex)}")
                raise Exception("文档对象无效")

            logger.info("CAD已成功启动和准备")
            return True

        except Exception as e:
            logger.error(f"启动CAD失败: {str(e)}")
            return False
        finally:
            # 清理旧实例
            if old_app is not None:
                try:
                    del old_app
                except:
                    pass


    def is_running(self) -> bool:
        """检查CAD是否正在运行"""
        return self.app is not None and self.doc is not None

    def _com_retry(self, func, *args, **kwargs):
        """COM 调用重试机制 — 指数退避重试，覆盖瞬态 COM 错误

        Args:
            func: 要执行的 callable
            *args, **kwargs: 传递给 func 的参数

        Returns:
            func 的返回值

        Raises:
            非瞬态异常原样抛出；重试耗尽后抛出最后一次的异常
        """
        last_exception = None
        for attempt in range(self.retry_max_attempts):
            try:
                return func(*args, **kwargs)
            except Exception as e:
                last_exception = e
                hresult = None
                if hasattr(e, 'hresult'):
                    hresult = e.hresult
                elif hasattr(e, 'args') and len(e.args) > 0:
                    # 尝试从异常消息中提取 HRESULT
                    msg = str(e.args[0])
                    for code in self._RETRYABLE_HRESULTS:
                        if str(code) in msg or hex(code & 0xFFFFFFFF) in msg:
                            hresult = code
                            break

                if hresult is not None and hresult in self._RETRYABLE_HRESULTS:
                    delay = self.retry_base_delay * (self.retry_backoff_factor ** attempt)
                    logger.warning(
                        f"COM 调用失败 (HRESULT=0x{hresult & 0xFFFFFFFF:08X})，"
                        f"第 {attempt + 1}/{self.retry_max_attempts} 次重试，"
                        f"等待 {delay:.2f}s..."
                    )
                    time.sleep(delay)
                    continue
                else:
                    # 非可重试异常，直接抛出
                    raise

        logger.error(f"COM 重试耗尽（{self.retry_max_attempts}次），最后异常: {last_exception}")
        raise last_exception

    def _com_prop(self, obj, prop, value=None, silent=False):
        """安全读写 COM 对象属性

        Args:
            obj: COM 对象
            prop: 属性名（字符串）
            value: 如果提供则设置属性值，如果为 None 则读取属性值
            silent: True 时用 DEBUG 级别记录失败（用于读取可选属性）

        Returns:
            读取时返回属性值，设置时返回 True/False
        """
        try:
            if value is not None:
                self._com_retry(lambda: setattr(obj, prop, value))
                return True
            else:
                return self._com_retry(lambda: getattr(obj, prop))
        except Exception as e:
            if silent:
                logger.debug(f"COM 属性(可选)读取跳过: .{prop} ({e})")
            else:
                logger.warning(f"COM 属性操作失败: obj.{prop} = {value if value is not None else '(get)'}, 错误: {e}")
            return None if value is None else False

    def _set_entity_props(self, entity, layer=None, color=None, lineweight=None):
        """统一切换图层/颜色/线宽（消除所有 draw_* 方法中的重复代码）

        Args:
            entity: COM 实体对象
            layer: 图层名称，None 则跳过
            color: 颜色索引，None 则跳过
            lineweight: 线宽，None 则跳过
        """
        if layer:
            self.create_layer(layer)
            self._com_prop(entity, 'Layer', layer, silent=True)
        if color is not None:
            self._com_prop(entity, 'Color', color, silent=True)
        if lineweight is not None:
            self._com_prop(entity, 'LineWeight', self.validate_lineweight(lineweight), silent=True)

    @staticmethod
    def _point_variant(pt):
        """将坐标转为 COM VARIANT 数组（消除重复的 VARIANT 构造代码）"""
        if len(pt) == 2:
            pt = (pt[0], pt[1], 0)
        return win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_R8, [pt[0], pt[1], pt[2]])

    def save_drawing(self, file_path: str) -> bool:
        """保存当前图纸到指定路径"""
        if not self.is_running():
            logger.error("CAD未运行，无法保存图纸")
            return False

        try:
            # 确保目录存在
            os.makedirs(os.path.dirname(file_path), exist_ok=True)

            # 保存文件
            self._com_retry(lambda: self.doc.SaveAs(file_path))
            logger.info(f"图纸已保存到: {file_path}")

            return True
        except Exception as e:
            logger.error(f"保存图纸失败: {str(e)}")
            return False

    def refresh_view(self) -> None:
        """刷新CAD视图"""
        if self.is_running():
            try:
                self._com_retry(lambda: self.doc.Regen(1))  # acAllViewports = 1
            except Exception as e:
                logger.error(f"刷新视图失败: {str(e)}")

    def validate_lineweight(self, lineweight) -> int:
        """验证并返回有效的线宽值

        如果提供的线宽值不在有效值列表中，则返回默认值0

        Args:
            lineweight: 要验证的线宽值

        Returns:
            有效的线宽值
        """
        if lineweight is None:
            return None

        # 检查线宽是否在有效值列表中
        if lineweight in self.valid_lineweights:
            return lineweight
        else:
            logger.warning(f"线宽值 {lineweight} 无效，将使用默认值 0")
            return 0

    def draw_line(self, start_point: Tuple[float, float, float],
                 end_point: Tuple[float, float, float], layer: str = None, color: int = None, lineweight=None) -> bool:
        """绘制直线"""
        if not self.is_running():
            return False

        try:
            # 确保点是三维的
            if len(start_point) == 2:
                start_point = (start_point[0], start_point[1], 0)
            if len(end_point) == 2:
                end_point = (end_point[0], end_point[1], 0)

            # 使用VARIANT包装坐标点数据
            start_array = win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_R8,
                                               [start_point[0], start_point[1], start_point[2]])
            end_array = win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_R8,
                                             [end_point[0], end_point[1], end_point[2]])

            # 添加直线
            line = self._com_retry(lambda: self.doc.ModelSpace.AddLine(start_array, end_array))
            self._set_entity_props(line, layer, color, lineweight)

            # 刷新视图
            self.refresh_view()

            self._register_entity(line, 'line')
            logger.debug(f"已绘制直线: 起点{start_point}, 终点{end_point}, 图层{layer if layer else '默认'}, 颜色{color if color is not None else '默认'}")
            return line

        except Exception as e:
            logger.error(f"绘制直线时出错: {str(e)}")
            return None

    def draw_circle(self, center: Tuple[float, float, float],
                   radius: float, layer: str = None, color: int = None, lineweight=None) -> Any:
        """绘制圆"""
        if not self.is_running():
            return None

        try:
            # 确保点是三维的
            if len(center) == 2:
                center = (center[0], center[1], 0)

            # 使用VARIANT包装坐标点数据
            center_array = win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_R8,
                                               [center[0], center[1], center[2]])

            # 添加圆
            circle = self._com_retry(lambda: self.doc.ModelSpace.AddCircle(center_array, radius))
            self._set_entity_props(circle, layer, color, lineweight)

            # 刷新视图
            self.refresh_view()

            self._register_entity(circle, 'circle')
            logger.debug(f"已绘制圆: 中心{center}, 半径{radius}, 图层{layer if layer else '默认'}, 颜色{color if color is not None else '默认'}")
            return circle

        except Exception as e:
            logger.error(f"绘制圆时出错: {str(e)}")
            return None

    def draw_arc(self, center: Tuple[float, float, float],
                radius: float, start_angle: float, end_angle: float, layer: str = None, color: int = None, lineweight=None) -> Any:
        """绘制圆弧"""
        if not self.is_running():
            return None

        try:
            # 确保点是三维的
            if len(center) == 2:
                center = (center[0], center[1], 0)

            # 将角度转换为弧度
            start_rad = math.radians(start_angle)
            end_rad = math.radians(end_angle)

            # 使用VARIANT包装坐标点数据
            center_array = win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_R8,
                                               [center[0], center[1], center[2]])

            # 添加圆弧
            arc = self._com_retry(lambda: self.doc.ModelSpace.AddArc(center_array, radius, start_rad, end_rad))
            self._set_entity_props(arc, layer, color, lineweight)

            # 刷新视图
            self.refresh_view()

            self._register_entity(arc, 'arc')
            logger.debug(f"已绘制圆弧: 中心{center}, 半径{radius}, 起始角度{start_angle}, 结束角度{end_angle}, 图层{layer if layer else '默认'}, 颜色{color if color is not None else '默认'}")
            return arc
        except Exception as e:
            logger.error(f"绘制圆弧失败: {str(e)}")
            return None

    def draw_ellipse(self, center: Tuple[float, float, float],
                    major_axis: float, minor_axis: float, rotation: float = 0,
                    layer: str = None, color: int = None, lineweight=None) -> Any:
        """绘制椭圆"""
        if not self.is_running():
            return None

        try:
            # 确保点是三维的
            if len(center) == 2:
                center = (center[0], center[1], 0)

            if rotation is None:
                rotation = 0

            # 将旋转角度转换为弧度
            rotation_rad = math.radians(rotation)

            # 使用VARIANT包装坐标点数据
            center_array = win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_R8,
                                               [center[0], center[1], center[2]])

            # 计算椭圆的主轴向量
            major_x = major_axis * math.cos(rotation_rad)
            major_y = major_axis * math.sin(rotation_rad)
            major_vector = win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_R8,
                                               [major_x, major_y, 0])

            # 添加椭圆
            ellipse = self._com_retry(lambda: self.doc.ModelSpace.AddEllipse(center_array, major_vector, minor_axis / major_axis))
            self._set_entity_props(ellipse, layer, color, lineweight)

            # 刷新视图
            self.refresh_view()

            self._register_entity(ellipse, 'ellipse')
            logger.debug(f"已绘制椭圆: 中心{center}, 长轴{major_axis}, 短轴{minor_axis}, 旋转角度{rotation}, 图层{layer if layer else '默认'}, 颜色{color if color is not None else '默认'}")
            return ellipse
        except Exception as e:
            logger.error(f"绘制椭圆失败: {str(e)}")
            return None

    def draw_polyline(self, points: List[Tuple[float, float, float]], closed: bool = False, layer: str = None, color: int = None, lineweight=None) -> Any:
        """绘制多段线"""
        if not self.is_running():
            return None

        try:
            # 确保所有点都是三维的
            processed_points = []
            for point in points:
                if len(point) == 2:
                    processed_points.append((point[0], point[1], 0))
                else:
                    processed_points.append(point)

            # 创建点数组
            point_array = win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_R8,
                                                [coord for point in processed_points for coord in point])

            # 添加多段线
            polyline = self._com_retry(lambda: self.doc.ModelSpace.AddPolyline(point_array))

            # 如果需要闭合
            if closed and len(processed_points) > 2:
                self._com_prop(polyline, 'Closed', True)

            self._set_entity_props(polyline, layer, color, lineweight)

            # 刷新视图
            self.refresh_view()

            self._register_entity(polyline, 'polyline')
            logger.debug(f"已绘制多段线: {len(points)}个点, {'闭合' if closed else '不闭合'}, 图层{layer if layer else '默认'}, 颜色{color if color is not None else '默认'}")
            return polyline
        except Exception as e:
            logger.error(f"绘制多段线时出错: {str(e)}")
            return None

    def draw_rectangle(self, corner1: Tuple[float, float, float],
                      corner2: Tuple[float, float, float], layer: str = None, color: int = None, lineweight=None) -> Any:
        """绘制矩形"""
        if not self.is_running():
            return None

        try:
            # 确保点是三维的
            if len(corner1) == 2:
                corner1 = (corner1[0], corner1[1], 0)
            if len(corner2) == 2:
                corner2 = (corner2[0], corner2[1], 0)

            # 计算矩形的四个角点
            x1, y1, z1 = corner1
            x2, y2, z2 = corner2

            # 创建矩形的四个点
            points = [
                (x1, y1, z1),
                (x2, y1, z1),
                (x2, y2, z1),
                (x1, y2, z1),
                (x1, y1, z1)  # 闭合矩形
            ]

            # 使用多段线绘制矩形
            return self.draw_polyline(points, True, layer, color, lineweight)
        except Exception as e:
            logger.error(f"绘制矩形时出错: {str(e)}")
            return None

    def draw_text(self, position: Tuple[float, float, float],
                 text: str, height: float = 2.5, rotation: float = 0, layer: str = None, color: int = None) -> Any:
        """添加文本"""
        if not self.is_running():
            return None

        try:
            # 确保点是三维的
            if len(position) == 2:
                position = (position[0], position[1], 0)

            # 使用VARIANT包装坐标点数据
            position_array = win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_R8,
                                                 [position[0], position[1], position[2]])

            # 添加文本
            text_obj = self._com_retry(lambda: self.doc.ModelSpace.AddText(text, position_array, height))

            # 设置旋转角度
            if rotation != 0:
                self._com_prop(text_obj, 'Rotation', math.radians(rotation))

            self._set_entity_props(text_obj, layer, color, None)

            # 刷新视图
            self.refresh_view()

            self._register_entity(text_obj, 'text')
            logger.debug(f"已添加文本: '{text}', 位置{position}, 高度{height}, 旋转{rotation}度, 图层{layer if layer else '默认'}, 颜色{color if color is not None else '默认'}")
            return text_obj
        except Exception as e:
            logger.error(f"添加文本时出错: {str(e)}")
            return None

    def draw_hatch(self, points: List[Tuple[float, float, float]],
                  pattern_name: str = "SOLID", scale: float = 1.0, layer: str = None, color: int = None) -> Any:
        """绘制填充图案

        Args:
            points: 填充边界的点集，每个点为二维或三维坐标元组
            pattern_name: 填充图案名称，默认为"SOLID"(实体填充)
            scale: 填充图案比例，默认为1.0
            layer: 图层名称，如果为None则使用当前图层
            color: 颜色索引，如果为None则使用默认颜色

        Returns:
            成功返回填充对象，失败返回None
        """
        if not self.is_running():
            return None

        try:
            # 确保所有点都是有效的
            if not points or len(points) < 3:
                logger.error("创建填充失败: 至少需要3个点来定义填充边界")
                return None

            # 创建闭合多段线作为边界
            closed_polyline = self.draw_polyline(points, closed=True, layer=layer)
            if not closed_polyline:
                logger.error("创建填充失败: 无法创建边界多段线")
                return None

            # 创建填充对象 (0表示正常填充，True表示关联边界)
            hatch = self._com_retry(lambda: self.doc.ModelSpace.AddHatch(0, pattern_name, True))

            # 添加外部边界循环
            # 使用VARIANT包装对象数组
            object_ids = win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_DISPATCH, [closed_polyline])
            self._com_retry(lambda: hatch.AppendOuterLoop(object_ids))

            # 设置填充图案比例
            self._com_prop(hatch, 'PatternScale', scale)
            self._set_entity_props(hatch, layer, color, None)

            # 更新填充 (计算填充区域) - 最重的 COM 调用，必须包裹重试
            self._com_retry(lambda: hatch.Evaluate())

            # 刷新视图
            self.refresh_view()

            self._register_entity(hatch, 'hatch')
            logger.debug(f"已创建填充: 图案 {pattern_name}, 比例 {scale}, 图层{layer if layer else '默认'}, 颜色{color if color is not None else '默认'}")
            return hatch
        except Exception as e:
            logger.error(f"创建填充时出错: {str(e)}")
            return None

    def zoom_extents(self) -> bool:
        """缩放视图以显示所有对象"""
        if not self.is_running():
            return False

        try:
            self._com_retry(lambda: self.doc.Application.ZoomExtents())
            logger.info("已缩放视图以显示所有对象")
            return True
        except Exception as e:
            logger.error(f"缩放视图时出错: {str(e)}")
            return False

    # ============================
    #  完整标注系统（阶段5）
    # ============================

    def add_dim_rotated(self, start_point, end_point, text_position=None, rotation_angle: float = 0,
                        textheight: float = 5, layer: str = None, color=None):
        """添加旋转线性标注"""
        if not self.is_running():
            return None
        try:
            start_array = self._point_variant(start_point)
            end_array = self._point_variant(end_point)
            if text_position is None:
                mid_x = (start_point[0] + end_point[0]) / 2
                mid_y = (start_point[1] + end_point[1]) / 2
                text_position = (mid_x, mid_y + 5, 0)
            text_pos_array = self._point_variant(text_position)

            dim = self._com_retry(lambda: self.doc.ModelSpace.AddDimRotated(
                start_array, end_array, text_pos_array, math.radians(rotation_angle)))
            if textheight is not None:
                self._com_prop(dim, 'TextHeight', textheight)
            self._set_entity_props(dim, layer, color, None)
            self.refresh_view()
            self._register_entity(dim, 'dimension')
            logger.info(f"已添加旋转标注: angle={rotation_angle}°")
            return dim
        except Exception as e:
            logger.error(f"添加旋转标注失败: {e}")
            return None

    def add_dim_radial(self, center, radius: float, text_position=None, textheight: float = 5,
                       layer: str = None, color=None):
        """添加半径标注"""
        if not self.is_running():
            return None
        try:
            center_array = self._point_variant(center)
            # 计算标注线上的点（默认在圆右侧）
            if text_position is None:
                text_position = (center[0] + radius, center[1], 0)
            text_pos_array = self._point_variant(text_position)

            dim = self._com_retry(lambda: self.doc.ModelSpace.AddDimRadial(
                center_array, text_pos_array, radius))
            if textheight is not None:
                self._com_prop(dim, 'TextHeight', textheight)
            self._set_entity_props(dim, layer, color, None)
            self.refresh_view()
            self._register_entity(dim, 'dimension')
            logger.info(f"已添加半径标注: center={center}, radius={radius}")
            return dim
        except Exception as e:
            logger.error(f"添加半径标注失败: {e}")
            return None

    def add_dim_diametric(self, center, radius: float, text_position=None, textheight: float = 5,
                          layer: str = None, color=None):
        """添加直径标注"""
        if not self.is_running():
            return None
        try:
            center_array = self._point_variant(center)
            # 直径标注点——弦长位置
            chord_point = (center[0] + radius, center[1], 0)
            if text_position is None:
                text_position = (center[0] - radius, center[1], 0)
            chord_array = self._point_variant(chord_point)
            leader_length = radius * 0.5

            dim = self._com_retry(lambda: self.doc.ModelSpace.AddDimDiametric(
                chord_array, chord_array, leader_length))
            if textheight is not None:
                self._com_prop(dim, 'TextHeight', textheight)
            self._set_entity_props(dim, layer, color, None)
            self.refresh_view()
            self._register_entity(dim, 'dimension')
            logger.info(f"已添加直径标注: center={center}, radius={radius}")
            return dim
        except Exception as e:
            logger.error(f"添加直径标注失败: {e}")
            return None

    def add_dim_angular(self, center, start_point, end_point, text_position=None,
                        textheight: float = 5, layer: str = None, color=None):
        """添加角度标注"""
        if not self.is_running():
            return None
        try:
            center_array = self._point_variant(center)
            start_array = self._point_variant(start_point)
            end_array = self._point_variant(end_point)
            if text_position is None:
                mx = (start_point[0] + end_point[0]) / 2
                my = (start_point[1] + end_point[1]) / 2
                text_position = (mx + 5, my + 5, 0)
            text_pos_array = self._point_variant(text_position)

            dim = self._com_retry(lambda: self.doc.ModelSpace.AddDimAngular(
                center_array, start_array, end_array, text_pos_array))
            if textheight is not None:
                self._com_prop(dim, 'TextHeight', textheight)
            self._set_entity_props(dim, layer, color, None)
            self.refresh_view()
            self._register_entity(dim, 'dimension')
            logger.info(f"已添加角度标注: center={center}")
            return dim
        except Exception as e:
            logger.error(f"添加角度标注失败: {e}")
            return None

    def add_dim_ordinate(self, point, text_position=None, is_x: bool = True,
                         textheight: float = 5, layer: str = None, color=None):
        """添加坐标标注

        Args:
            point: 标注点
            is_x: True 为 X 坐标标注，False 为 Y 坐标标注
        """
        if not self.is_running():
            return None
        try:
            point_array = self._point_variant(point)
            if text_position is None:
                text_position = (point[0] + 10, point[1] - 10, 0)
            text_pos_array = self._point_variant(text_position)

            dim = self._com_retry(lambda: self.doc.ModelSpace.AddDimOrdinate(
                point_array, text_pos_array, is_x))
            if textheight is not None:
                self._com_prop(dim, 'TextHeight', textheight)
            self._set_entity_props(dim, layer, color, None)
            self.refresh_view()
            self._register_entity(dim, 'dimension')
            logger.info(f"已添加坐标标注: point={point}, is_x={is_x}")
            return dim
        except Exception as e:
            logger.error(f"添加坐标标注失败: {e}")
            return None

    # ============================
    #  实体追踪系统（阶段2）
    # ============================

    def _register_entity(self, com_obj, entity_type: str) -> str:
        """注册实体，记录 COM 句柄

        Args:
            com_obj: COM 实体对象
            entity_type: 实体类型标记（如 'line', 'circle', 'arc' 等）

        Returns:
            实体的 Handle 字符串，失败返回 None
        """
        try:
            if com_obj is None:
                return None
            handle = self._com_prop(com_obj, 'Handle')
            if handle:
                self.entities[handle] = {
                    "type": entity_type,
                    "handle": handle,
                    "com_obj": com_obj,  # 保留 COM 引用（会话内有效）
                }
                self._last_entity_id = handle
                logger.debug(f"已注册实体: handle={handle}, type={entity_type}")
                return handle
            return None
        except Exception as e:
            logger.warning(f"注册实体失败: {e}")
            return None

    def get_entity(self, handle: str):
        """通过 Handle 重新解析实体（支持跨保存持久 ID）

        Args:
            handle: 实体句柄字符串

        Returns:
            COM 实体对象，或 None
        """
        if not self.is_running():
            logger.error("CAD未运行")
            return None
        try:
            # 先尝试从缓存中获取
            if handle in self.entities and self.entities[handle].get("com_obj"):
                try:
                    # 验证缓存的 COM 对象是否仍然有效
                    cached = self.entities[handle]["com_obj"]
                    _ = self._com_prop(cached, 'Handle')
                    return cached
                except Exception:
                    pass  # 缓存失效，重新解析

            # 通过 HandleToObject 重新解析
            entity = self._com_retry(lambda: self.doc.HandleToObject(handle))
            if entity:
                entity_type = self.entities.get(handle, {}).get("type", "unknown")
                self.entities[handle] = {
                    "type": entity_type,
                    "handle": handle,
                    "com_obj": entity,
                }
                return entity
            return None
        except Exception as e:
            logger.warning(f"获取实体失败 (handle={handle}): {e}")
            return None

    def list_entities(self, entity_type: str = None):
        """列出已追踪的实体

        Args:
            entity_type: 可选，按类型筛选（如 'line', 'circle' 等）

        Returns:
            实体信息列表 [{"type": ..., "handle": ...}, ...]
        """
        result = []
        for handle, info in self.entities.items():
            if entity_type is None or info.get("type") == entity_type:
                result.append({
                    "type": info.get("type", "unknown"),
                    "handle": handle,
                })
        logger.info(f"列出实体: 总数={len(result)}" + (f", 类型={entity_type}" if entity_type else ""))
        return result

    # ============================
    #  选择与查询（阶段3）
    # ============================

    def select_by_handle(self, handle: str):
        """通过 AutoCAD 句柄查找实体

        Args:
            handle: 实体句柄字符串

        Returns:
            COM 实体对象，或 None
        """
        return self.get_entity(handle)

    def select_all(self):
        """遍历 ModelSpace 获取所有实体

        Returns:
            实体信息列表 [{"type": ..., "handle": ..., "layer": ...}, ...]
        """
        if not self.is_running():
            logger.error("CAD未运行")
            return []

        result = []
        try:
            count = self._com_prop(self.doc.ModelSpace, 'Count')
            for i in range(count):
                try:
                    entity = self._com_retry(lambda i=i: self.doc.ModelSpace.Item(i))
                    if entity:
                        info = self._get_entity_info(entity)
                        if info:
                            result.append(info)
                            # 同步到实体追踪
                            handle = info.get("handle")
                            if handle and handle not in self.entities:
                                self.entities[handle] = {
                                    "type": info["type"],
                                    "handle": handle,
                                    "com_obj": entity,
                                }
                except Exception:
                    continue
        except Exception as e:
            logger.error(f"遍历 ModelSpace 失败: {e}")

        logger.info(f"select_all: 找到 {len(result)} 个实体")
        return result

    def select_by_layer(self, layer_name: str):
        """按图层筛选实体

        Args:
            layer_name: 图层名称

        Returns:
            实体信息列表 [{"type": ..., "handle": ..., "layer": ...}, ...]
        """
        if not self.is_running():
            logger.error("CAD未运行")
            return []

        result = []
        try:
            count = self._com_prop(self.doc.ModelSpace, 'Count')
            for i in range(count):
                try:
                    entity = self._com_retry(lambda i=i: self.doc.ModelSpace.Item(i))
                    if entity:
                        ent_layer = self._com_prop(entity, 'Layer')
                        if ent_layer and ent_layer.lower() == layer_name.lower():
                            info = self._get_entity_info(entity)
                            if info:
                                result.append(info)
                                handle = info.get("handle")
                                if handle and handle not in self.entities:
                                    self.entities[handle] = {
                                        "type": info["type"],
                                        "handle": handle,
                                        "com_obj": entity,
                                    }
                except Exception:
                    continue
        except Exception as e:
            logger.error(f"按图层筛选失败: {e}")

        logger.info(f"select_by_layer('{layer_name}'): 找到 {len(result)} 个实体")
        return result

    def select_window(self, corner1, corner2):
        """窗口选择实体

        Args:
            corner1: 窗口第一个角点 [x, y, z]
            corner2: 窗口第二个角点 [x, y, z]

        Returns:
            实体信息列表
        """
        if not self.is_running():
            logger.error("CAD未运行")
            return []

        try:
            # 确保点是三维的
            if len(corner1) == 2:
                corner1 = (corner1[0], corner1[1], 0)
            if len(corner2) == 2:
                corner2 = (corner2[0], corner2[1], 0)

            # 创建选择集
            ss_name = f"CAD_MCP_TEMP_{id(self)}"
            try:
                sel_set = self._com_retry(lambda: self.doc.SelectionSets.Add(ss_name))
            except Exception:
                # 选择集可能已存在，先删除再创建
                try:
                    self._com_retry(lambda: self.doc.SelectionSets.Item(ss_name).Delete())
                except Exception:
                    pass
                sel_set = self._com_retry(lambda: self.doc.SelectionSets.Add(ss_name))

            # 构建窗口坐标
            pt1_array = win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_R8,
                                                [corner1[0], corner1[1], corner1[2] if len(corner1) > 2 else 0])
            pt2_array = win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_R8,
                                                [corner2[0], corner2[1], corner2[2] if len(corner2) > 2 else 0])

            # acSelectionSetWindow = 0, 窗口选择
            self._com_retry(lambda: sel_set.Select(0, pt1_array, pt2_array))

            result = []
            sel_count = self._com_prop(sel_set, 'Count')
            for i in range(sel_count):
                try:
                    entity = self._com_retry(lambda i=i: sel_set.Item(i))
                    if entity:
                        info = self._get_entity_info(entity)
                        if info:
                            result.append(info)
                            handle = info.get("handle")
                            if handle and handle not in self.entities:
                                self.entities[handle] = {
                                    "type": info["type"],
                                    "handle": handle,
                                    "com_obj": entity,
                                }
                except Exception:
                    continue

            # 清理选择集
            try:
                sel_set.Delete()
            except Exception:
                pass

            logger.info(f"select_window: 找到 {len(result)} 个实体")
            return result
        except Exception as e:
            logger.error(f"窗口选择失败: {e}")
            return []

    def get_entity_properties(self, entity_or_handle):
        """读取实体属性

        Args:
            entity_or_handle: COM 实体对象或 Handle 字符串

        Returns:
            属性字典，包含 type, layer, color, lineweight, handle 及几何信息
        """
        entity = entity_or_handle
        if isinstance(entity_or_handle, str):
            entity = self.get_entity(entity_or_handle)

        if entity is None:
            logger.warning("get_entity_properties: 实体为空")
            return None

        try:
            props = self._get_entity_info(entity)

            # 尝试获取几何属性
            try:
                entity_name = self._com_prop(entity, 'EntityName', silent=True) or self._com_prop(entity, 'ObjectName', silent=True)
                props["entity_name"] = entity_name
            except Exception:
                pass

            # 长度 (适用于 Line, Arc, Polyline 等)
            try:
                props["length"] = round(self._com_prop(entity, 'Length', silent=True), 4)
            except Exception:
                pass

            # 面积 (适用于 Circle, Hatch, closed Polyline 等)
            try:
                props["area"] = round(self._com_prop(entity, 'Area', silent=True), 4)
            except Exception:
                pass

            # 半径 (适用于 Circle, Arc)
            try:
                props["radius"] = round(self._com_prop(entity, 'Radius', silent=True), 4)
            except Exception:
                pass

            # 起点 / 终点 (适用于 Line)
            try:
                start_pt = self._com_prop(entity, 'StartPoint', silent=True)
                if start_pt:
                    props["start_point"] = [round(v, 4) for v in start_pt]
            except Exception:
                pass

            try:
                end_pt = self._com_prop(entity, 'EndPoint', silent=True)
                if end_pt:
                    props["end_point"] = [round(v, 4) for v in end_pt]
            except Exception:
                pass

            # 中心点 (适用于 Circle, Arc)
            try:
                center = self._com_prop(entity, 'Center', silent=True)
                if center:
                    props["center"] = [round(v, 4) for v in center]
            except Exception:
                pass

            # 文本内容 (适用于 Text)
            try:
                props["text_string"] = self._com_prop(entity, 'TextString', silent=True)
            except Exception:
                pass

            logger.info(f"get_entity_properties: {props.get('type')} handle={props.get('handle')}")
            return props
        except Exception as e:
            logger.error(f"获取实体属性失败: {e}")
            return None

    def _get_entity_info(self, entity) -> dict:
        """内部辅助：从 COM 实体提取基本信息

        Args:
            entity: COM 实体对象

        Returns:
            基本信息字典 {"type": ..., "handle": ..., "layer": ..., "color": ..., "lineweight": ...}
        """
        info = {}
        try:
            # 实体类型
            entity_name = self._com_prop(entity, 'EntityName', silent=True) or self._com_prop(entity, 'ObjectName', silent=True)
            type_map = {
                'AcDbLine': 'line',
                'AcDbCircle': 'circle',
                'AcDbArc': 'arc',
                'AcDbEllipse': 'ellipse',
                'AcDbPolyline': 'polyline',
                'AcDb2dPolyline': 'polyline',
                'AcDb3dPolyline': 'polyline',
                'AcDbText': 'text',
                'AcDbMText': 'text',
                'AcDbHatch': 'hatch',
                'AcDbRotatedDimension': 'dimension',
                'AcDbAlignedDimension': 'dimension',
                'AcDbRadialDimension': 'dimension',
                'AcDbDiametricDimension': 'dimension',
                'AcDbBlockReference': 'block_ref',
            }
            info["type"] = type_map.get(entity_name, entity_name or 'unknown')
            info["handle"] = self._com_prop(entity, 'Handle') or ''
            info["layer"] = self._com_prop(entity, 'Layer') or '0'
            info["color"] = self._com_prop(entity, 'Color')
            info["lineweight"] = self._com_prop(entity, 'LineWeight')
        except Exception as e:
            logger.debug(f"_get_entity_info 部分属性读取失败: {e}")
        return info

    # ============================
    #  实体修改操作（阶段4）
    # ============================

    def _resolve_entity(self, handle):
        """解析实体：通过 handle 获取 COM 对象"""
        entity = self.get_entity(handle)
        if entity is None:
            raise ValueError(f"无法获取实体: handle={handle}")
        return entity

    def move_entity(self, handle: str, displacement) -> bool:
        """按位移向量移动实体

        Args:
            handle: 实体句柄
            displacement: 位移向量 [dx, dy, dz]

        Returns:
            是否成功
        """
        if not self.is_running():
            return False
        try:
            entity = self._resolve_entity(handle)
            if len(displacement) == 2:
                displacement = (displacement[0], displacement[1], 0)

            from_pt = self._point_variant((0, 0, 0))
            to_pt = self._point_variant(displacement)
            self._com_retry(lambda: entity.Move(from_pt, to_pt))
            self.refresh_view()
            logger.info(f"move_entity: handle={handle}, displacement={displacement}")
            return True
        except Exception as e:
            logger.error(f"移动实体失败: {e}")
            return False

    def rotate_entity(self, handle: str, base_point, angle: float) -> bool:
        """绕基点旋转实体

        Args:
            handle: 实体句柄
            base_point: 旋转基点 [x, y, z]
            angle: 旋转角度（度）

        Returns:
            是否成功
        """
        if not self.is_running():
            return False
        try:
            entity = self._resolve_entity(handle)
            bp = self._point_variant(base_point)
            rad = math.radians(angle)
            self._com_retry(lambda: entity.Rotate(bp, rad))
            self.refresh_view()
            logger.info(f"rotate_entity: handle={handle}, base={base_point}, angle={angle}°")
            return True
        except Exception as e:
            logger.error(f"旋转实体失败: {e}")
            return False

    def scale_entity(self, handle: str, base_point, scale_factor: float) -> bool:
        """按比例缩放实体

        Args:
            handle: 实体句柄
            base_point: 缩放基点 [x, y, z]
            scale_factor: 缩放比例

        Returns:
            是否成功
        """
        if not self.is_running():
            return False
        try:
            entity = self._resolve_entity(handle)
            bp = self._point_variant(base_point)
            self._com_retry(lambda: entity.ScaleEntity(bp, scale_factor))
            self.refresh_view()
            logger.info(f"scale_entity: handle={handle}, scale={scale_factor}")
            return True
        except Exception as e:
            logger.error(f"缩放实体失败: {e}")
            return False

    def erase_entity(self, handle: str) -> bool:
        """删除实体

        Args:
            handle: 实体句柄

        Returns:
            是否成功
        """
        if not self.is_running():
            return False
        try:
            entity = self._resolve_entity(handle)
            self._com_retry(lambda: entity.Delete())
            # 从追踪中移除
            self.entities.pop(handle, None)
            self.refresh_view()
            logger.info(f"erase_entity: handle={handle}")
            return True
        except Exception as e:
            logger.error(f"删除实体失败: {e}")
            return False

    def copy_entity(self, handle: str, displacement) -> str:
        """复制实体并偏移

        Args:
            handle: 实体句柄
            displacement: 复制偏移 [dx, dy, dz]

        Returns:
            新实体的 Handle，失败返回 None
        """
        if not self.is_running():
            return None
        try:
            entity = self._resolve_entity(handle)
            copied = self._com_retry(lambda: entity.Copy())
            if copied is None:
                return None
            if len(displacement) == 2:
                displacement = (displacement[0], displacement[1], 0)
            from_pt = self._point_variant((0, 0, 0))
            to_pt = self._point_variant(displacement)
            self._com_retry(lambda: copied.Move(from_pt, to_pt))
            self.refresh_view()

            # 注册新实体
            entity_type = self.entities.get(handle, {}).get("type", "unknown")
            new_handle = self._register_entity(copied, entity_type)
            logger.info(f"copy_entity: handle={handle} -> new_handle={new_handle}")
            return new_handle
        except Exception as e:
            logger.error(f"复制实体失败: {e}")
            return None

    def mirror_entity(self, handle: str, point1, point2) -> str:
        """镜像实体

        Args:
            handle: 实体句柄
            point1: 镜像轴第一点 [x, y, z]
            point2: 镜像轴第二点 [x, y, z]

        Returns:
            镜像后新实体的 Handle（保留原实体），失败返回 None
        """
        if not self.is_running():
            return None
        try:
            entity = self._resolve_entity(handle)
            pt1 = self._point_variant(point1)
            pt2 = self._point_variant(point2)
            mirrored = self._com_retry(lambda: entity.Mirror(pt1, pt2))
            self.refresh_view()

            if mirrored:
                entity_type = self.entities.get(handle, {}).get("type", "unknown")
                new_handle = self._register_entity(mirrored, entity_type)
                logger.info(f"mirror_entity: handle={handle} -> new_handle={new_handle}")
                return new_handle
            return None
        except Exception as e:
            logger.error(f"镜像实体失败: {e}")
            return None

    def offset_entity(self, handle: str, distance: float) -> str:
        """偏置实体（建筑双线等）

        Args:
            handle: 实体句柄
            distance: 偏置距离

        Returns:
            偏置后新实体的 Handle，失败返回 None
        """
        if not self.is_running():
            return None
        try:
            entity = self._resolve_entity(handle)
            # Offset 方法返回变体数组
            offset_result = self._com_retry(lambda: entity.Offset(distance))
            self.refresh_view()

            if offset_result is not None:
                # Offset 可能返回单个对象或数组
                new_entity = None
                try:
                    # 尝试按数组处理
                    count = self._com_prop(offset_result, 'Count') if hasattr(offset_result, 'Count') else 0
                    if count and count > 0:
                        new_entity = self._com_retry(lambda: offset_result.Item(0))
                except Exception:
                    # 单个对象
                    new_entity = offset_result

                if new_entity:
                    entity_type = self.entities.get(handle, {}).get("type", "unknown")
                    new_handle = self._register_entity(new_entity, entity_type)
                    logger.info(f"offset_entity: handle={handle}, distance={distance} -> new_handle={new_handle}")
                    return new_handle
            return None
        except Exception as e:
            logger.error(f"偏置实体失败: {e}")
            return None

    # ============================
    #  图块支持（阶段6）
    # ============================

    def create_block(self, name: str, insertion_point, entity_handles: list) -> str:
        """从已有实体创建块定义

        Args:
            name: 块名称
            insertion_point: 插入基点 [x, y, z]
            entity_handles: 实体句柄列表

        Returns:
            块名称，失败返回 None
        """
        if not self.is_running():
            return None
        try:
            # 检查块是否已存在
            block_exists = False
            try:
                for i in range(self._com_prop(self.doc.Blocks, 'Count')):
                    blk = self._com_retry(lambda i=i: self.doc.Blocks.Item(i))
                    if blk and self._com_prop(blk, 'Name') == name:
                        block_exists = True
                        break
            except Exception:
                pass

            if block_exists:
                logger.warning(f"块 '{name}' 已存在，将重新定义")

            # 添加块定义
            ins_pt = self._point_variant(insertion_point)
            block_def = self._com_retry(lambda: self.doc.Blocks.Add(ins_pt, name))

            # 将实体复制到块定义中
            entities_to_copy = []
            for handle in entity_handles:
                entity = self.get_entity(handle)
                if entity:
                    entities_to_copy.append(entity)

            if entities_to_copy:
                # 复制实体到块定义
                copied_entities = []
                for ent in entities_to_copy:
                    try:
                        copied = self._com_retry(lambda: ent.Copy())
                        if copied:
                            copied_entities.append(copied)
                    except Exception as ex:
                        logger.warning(f"复制实体到块失败 (handle={handle}): {ex}")

                # 将复制的实体添加到块定义
                for copied in copied_entities:
                    try:
                        # 获取实体的各种属性用于复制
                        obj_id = self._com_prop(copied, 'ObjectID')
                    except Exception:
                        pass

                logger.info(f"create_block: '{name}' 包含 {len(entities_to_copy)} 个实体")
            else:
                logger.info(f"create_block: 创建空块 '{name}'")

            return name
        except Exception as e:
            logger.error(f"创建块失败: {e}")
            return None

    def insert_block(self, name: str, insertion_point, scale=1.0, rotation: float = 0,
                     layer: str = None, color=None) -> str:
        """插入块引用

        Args:
            name: 块名称
            insertion_point: 插入点 [x, y, z]
            scale: 缩放比例（统一缩放）
            rotation: 旋转角度（度）
            layer: 图层
            color: 颜色

        Returns:
            块引用实体的 Handle
        """
        if not self.is_running():
            return None
        try:
            ins_pt = self._point_variant(insertion_point)
            rot_rad = math.radians(rotation)

            block_ref = self._com_retry(lambda: self.doc.ModelSpace.InsertBlock(
                ins_pt, name, scale, scale, scale, rot_rad))

            self._set_entity_props(block_ref, layer, color, None)

            self.refresh_view()
            handle = self._register_entity(block_ref, 'block_ref')
            logger.info(f"insert_block: '{name}' at {insertion_point}, scale={scale}, rotation={rotation}°")
            return handle
        except Exception as e:
            logger.error(f"插入块失败: {e}")
            return None

    def explode_block(self, handle: str) -> list:
        """炸开块引用

        Args:
            handle: 块引用实体的句柄

        Returns:
            炸开后新实体的 Handle 列表
        """
        if not self.is_running():
            return []
        try:
            entity = self._resolve_entity(handle)
            exploded = self._com_retry(lambda: entity.Explode())
            self._com_retry(lambda: entity.Delete())
            self.entities.pop(handle, None)

            new_handles = []
            if exploded is not None:
                try:
                    count = self._com_prop(exploded, 'Count')
                    for i in range(count):
                        try:
                            new_entity = self._com_retry(lambda i=i: exploded.Item(i))
                            if new_entity:
                                h = self._register_entity(new_entity, 'unknown')
                                if h:
                                    new_handles.append(h)
                        except Exception:
                            continue
                except Exception:
                    # 单个对象
                    h = self._register_entity(exploded, 'unknown')
                    if h:
                        new_handles.append(h)

            self.refresh_view()
            logger.info(f"explode_block: handle={handle} -> {len(new_handles)} 个实体")
            return new_handles
        except Exception as e:
            logger.error(f"炸开块失败: {e}")
            return []

    def list_blocks(self) -> list:
        """列出所有块定义

        Returns:
            [{"name": ..., "count": ...}, ...]
        """
        if not self.is_running():
            return []
        result = []
        try:
            blk_count = self._com_prop(self.doc.Blocks, 'Count')
            for i in range(blk_count):
                try:
                    blk = self._com_retry(lambda i=i: self.doc.Blocks.Item(i))
                    if blk:
                        name = self._com_prop(blk, 'Name')
                        count = self._com_prop(blk, 'Count')
                        result.append({"name": name, "entity_count": count})
                except Exception:
                    continue
            logger.info(f"list_blocks: {len(result)} 个块定义")
            return result
        except Exception as e:
            logger.error(f"列出块失败: {e}")
            return []

    # ============================
    #  高级图层管理（阶段7）
    # ============================

    def _get_layer(self, layer_name: str):
        """内部辅助：通过名称获取图层对象"""
        if not self.is_running():
            return None
        try:
            layer_count = self._com_prop(self.doc.Layers, 'Count')
            for i in range(layer_count):
                layer = self._com_retry(lambda i=i: self.doc.Layers.Item(i))
                if layer and self._com_prop(layer, 'Name') == layer_name:
                    return layer
            return None
        except Exception as e:
            logger.error(f"获取图层失败: {e}")
            return None

    def list_layers(self) -> list:
        """列出所有图层及属性

        Returns:
            [{"name": ..., "color": ..., "lineweight": ..., "linetype": ..., "frozen": ..., "locked": ..., "on": ...}, ...]
        """
        if not self.is_running():
            return []
        result = []
        try:
            layer_count = self._com_prop(self.doc.Layers, 'Count')
            for i in range(layer_count):
                try:
                    layer = self._com_retry(lambda i=i: self.doc.Layers.Item(i))
                    if layer:
                        result.append({
                            "name": self._com_prop(layer, 'Name'),
                            "color": self._com_prop(layer, 'Color'),
                            "lineweight": self._com_prop(layer, 'LineWeight'),
                            "linetype": self._com_prop(layer, 'Linetype'),
                            "frozen": self._com_prop(layer, 'Freeze'),
                            "locked": self._com_prop(layer, 'Lock'),
                            "on": self._com_prop(layer, 'LayerOn'),
                        })
                except Exception:
                    continue
            logger.info(f"list_layers: {len(result)} 个图层")
            return result
        except Exception as e:
            logger.error(f"列出图层失败: {e}")
            return []

    def freeze_layer(self, layer_name: str, freeze: bool = True) -> bool:
        """冻结/解冻图层"""
        if not self.is_running():
            return False
        try:
            layer = self._get_layer(layer_name)
            if layer is None:
                logger.error(f"图层不存在: {layer_name}")
                return False
            self._com_prop(layer, 'Freeze', freeze)
            logger.info(f"freeze_layer: '{layer_name}' freeze={freeze}")
            return True
        except Exception as e:
            logger.error(f"冻结图层失败: {e}")
            return False

    def lock_layer(self, layer_name: str, lock: bool = True) -> bool:
        """锁定/解锁图层"""
        if not self.is_running():
            return False
        try:
            layer = self._get_layer(layer_name)
            if layer is None:
                logger.error(f"图层不存在: {layer_name}")
                return False
            self._com_prop(layer, 'Lock', lock)
            logger.info(f"lock_layer: '{layer_name}' lock={lock}")
            return True
        except Exception as e:
            logger.error(f"锁定图层失败: {e}")
            return False

    def set_layer_color(self, layer_name: str, color) -> bool:
        """设置图层颜色

        Args:
            layer_name: 图层名称
            color: 颜色索引 (int) 或颜色名称 (str)
        """
        if not self.is_running():
            return False
        try:
            layer = self._get_layer(layer_name)
            if layer is None:
                logger.error(f"图层不存在: {layer_name}")
                return False
            self._com_prop(layer, 'Color', color)
            logger.info(f"set_layer_color: '{layer_name}' color={color}")
            return True
        except Exception as e:
            logger.error(f"设置图层颜色失败: {e}")
            return False

    def set_layer_linetype(self, layer_name: str, linetype: str) -> bool:
        """设置图层线型（自动加载线型文件）"""
        if not self.is_running():
            return False
        try:
            layer = self._get_layer(layer_name)
            if layer is None:
                logger.error(f"图层不存在: {layer_name}")
                return False

            # 尝试加载线型（如果不存在）
            try:
                self._com_retry(lambda: self.doc.Linetypes.Load(linetype, "acad.lin"))
            except Exception:
                logger.debug(f"线型 '{linetype}' 可能已加载或加载失败")

            self._com_prop(layer, 'Linetype', linetype)
            logger.info(f"set_layer_linetype: '{layer_name}' linetype={linetype}")
            return True
        except Exception as e:
            logger.error(f"设置图层线型失败: {e}")
            return False

    def set_current_layer(self, layer_name: str) -> bool:
        """设为当前工作图层"""
        if not self.is_running():
            return False
        try:
            layer = self._get_layer(layer_name)
            if layer is None:
                # 自动创建图层
                self.create_layer(layer_name)
                layer = self._get_layer(layer_name)
            if layer:
                self._com_prop(self.doc, 'ActiveLayer', layer)
                logger.info(f"set_current_layer: '{layer_name}'")
                return True
            return False
        except Exception as e:
            logger.error(f"设置当前图层失败: {e}")
            return False

    def delete_layer(self, layer_name: str) -> bool:
        """删除空图层（不能删除当前图层或包含实体的图层）"""
        if not self.is_running():
            return False
        try:
            if layer_name == "0" or layer_name.lower() == "defpoints":
                logger.warning(f"不能删除系统图层: '{layer_name}'")
                return False
            layer = self._get_layer(layer_name)
            if layer is None:
                logger.error(f"图层不存在: {layer_name}")
                return False
            self._com_retry(lambda: layer.Delete())
            logger.info(f"delete_layer: '{layer_name}'")
            return True
        except Exception as e:
            logger.error(f"删除图层失败: {e}")
            return False

    def close(self) -> None:
        """关闭CAD控制器"""
        try:
            # 释放COM资源
            if self.app is not None:
                del self.app
            pythoncom.CoUninitialize()
        except:
            pass


    def create_layer(self, layer_name: str) -> bool:    # , color: Union[int, Tuple[int, int, int]] = 7
        """创建新图层

        Args:
            layer_name: 图层名称
            color: 颜色值，可以是CAD颜色索引(int)或RGB颜色值(tuple)

        Returns:
            操作是否成功
        """
        if not self.is_running():
            return False

        try:
            # 检查图层是否已存在
            layer_count = self._com_prop(self.doc.Layers, 'Count')
            for i in range(layer_count):
                layer_item = self._com_retry(lambda i=i: self.doc.Layers.Item(i))
                if self._com_prop(layer_item, 'Name') == layer_name:
                    self._com_prop(self.doc, 'ActiveLayer', layer_item)
                    return True

            # 创建新图层
            new_layer = self._com_retry(lambda: self.doc.Layers.Add(layer_name))

            # 图层不设置颜色，设置里面的实体颜色
            # # 设置颜色
            # if isinstance(color, int):
            #     # 使用颜色索引
            #     new_layer.Color = color
            # elif isinstance(color, tuple) and len(color) == 3:
            #     # 使用RGB值
            #     r, g, b = color
            #     # 设置TrueColor
            #     new_layer.TrueColor = self._create_true_color(r, g, b)

            # 设置为当前图层
            self._com_prop(self.doc, 'ActiveLayer', new_layer)
            logger.info(f"已创建新图层: {layer_name}")
            return True
        except Exception as e:
            logger.error(f"创建图层时出错: {str(e)}")
            return False

    def add_dimension(self, start_point: Tuple[float, float, float],
                     end_point: Tuple[float, float, float],
                     text_position: Tuple[float, float, float] = None, textheight: float = 5,layer: str = None, color: int=None) -> Any:
            """添加线性标注"""
            if not self.is_running():
                return None

            try:
                # 确保点是三维的
                if len(start_point) == 2:
                    start_point = (start_point[0], start_point[1], 0)
                if len(end_point) == 2:
                    end_point = (end_point[0], end_point[1], 0)

                # 如果未提供文本位置，自动计算
                if text_position is None:
                    # 在起点和终点之间的中点上方
                    mid_x = (start_point[0] + end_point[0]) / 2
                    mid_y = (start_point[1] + end_point[1]) / 2
                    text_position = (mid_x, mid_y + 5, 0)
                elif len(text_position) == 2:
                    text_position = (text_position[0], text_position[1], 0)

                # 使用VARIANT包装坐标点数据
                start_array = win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_R8,
                                                 [start_point[0], start_point[1], start_point[2]])
                end_array = win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_R8,
                                               [end_point[0], end_point[1], end_point[2]])
                text_pos_array = win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_R8,
                                                     [text_position[0], text_position[1], text_position[2]])

                # 添加对齐标注
                dimension = self._com_retry(lambda: self.doc.ModelSpace.AddDimAligned(start_array, end_array, text_pos_array))

                # 设置文字高度
                if textheight is not None:
                    self._com_prop(dimension, 'TextHeight', textheight)

                self._set_entity_props(dimension, layer, color, None)

                # 刷新视图
                self.refresh_view()

                self._register_entity(dimension, 'dimension')
                logger.info(f"已添加标注: 从 {start_point} 到 {end_point}, 图层{layer if layer else '默认'}")
                return dimension
            except Exception as e:
                logger.error(f"添加标注时出错: {str(e)}")
                return None
