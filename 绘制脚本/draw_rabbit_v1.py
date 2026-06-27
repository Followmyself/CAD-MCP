"""
通过 CAD-MCP 后端绘制可爱卡通兔子（完整版，含 hatch 填充）
使用 MCP 服务器的 CADController 类操作 AutoCAD

警告：此版本包含 hatch 填充，容易触发 COM RPC_E_SERVERCALL_RETRYLATER 错误
建议使用 draw_rabbit_v2.py（无 hatch 版本）
"""
import sys
import os
import math
import time
import io

# 修复 Windows 控制台编码问题
if sys.platform == "win32":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8')

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)) + '/src')
from cad_controller import CADController

def draw_cute_rabbit():
    ctrl = CADController()

    print("正在连接 AutoCAD...")
    if not ctrl.start_cad():
        print("[X] 无法连接 AutoCAD，请确保 AutoCAD 已打开")
        return False

    print(f"[OK] 已连接到: {ctrl.doc.Name}")
    doc = ctrl.doc
    ms = doc.ModelSpace

    # ========== 颜色定义 ==========
    WHITE = 7        # AutoCAD 颜色索引: 白色
    BLACK = 0        # 黑色 (ByBlock)
    DARK_GRAY = 251  # 深灰用于轮廓
    PINK = 6         # 品红作为粉色

    # ========== 1. 身体 ==========
    print("[~] 绘制身体...")
    body_center = (0, 0)
    body_pts = []
    for angle in range(0, 361, 5):
        rad = math.radians(angle)
        x = body_center[0] + 35 * math.cos(rad)
        y = body_center[1] + 22 * math.sin(rad)
        body_pts.append((x, y, 0))
    ctrl.draw_polyline(body_pts, closed=True, color=DARK_GRAY)
    ctrl.draw_hatch(body_pts, "SOLID", 1.0, color=WHITE)

    # ========== 2. 头部 ==========
    print("[~] 绘制头部...")
    head_center = (0, 30, 0)
    head_radius = 18
    ctrl.draw_circle(head_center, head_radius, color=DARK_GRAY)
    head_points = []
    for angle in range(0, 360, 10):
        rad = math.radians(angle)
        x = head_center[0] + head_radius * math.cos(rad)
        y = head_center[1] + head_radius * math.sin(rad)
        head_points.append((x, y, 0))
    ctrl.draw_hatch(head_points, "SOLID", 1.0, color=WHITE)

    # ========== 3. 左耳 ==========
    print("[~] 绘制左耳...")
    left_ear_center = (-10, 58)
    left_ear_pts = []
    for angle in range(0, 361, 5):
        rad = math.radians(angle)
        x = left_ear_center[0] + 8 * math.cos(rad)
        y = left_ear_center[1] + 24 * math.sin(rad)
        left_ear_pts.append((x, y, 0))
    ctrl.draw_polyline(left_ear_pts, closed=True, color=DARK_GRAY)
    left_ear_inner = []
    for angle in range(0, 361, 10):
        rad = math.radians(angle)
        x = left_ear_center[0] + 5 * math.cos(rad)
        y = left_ear_center[1] + 18 * math.sin(rad)
        left_ear_inner.append((x, y, 0))
    ctrl.draw_hatch(left_ear_inner, "SOLID", 1.0, color=PINK)

    # ========== 4. 右耳 ==========
    print("[~] 绘制右耳...")
    right_ear_center = (10, 58)
    right_ear_pts = []
    for angle in range(0, 361, 5):
        rad = math.radians(angle)
        x = right_ear_center[0] + 8 * math.cos(rad)
        y = right_ear_center[1] + 24 * math.sin(rad)
        right_ear_pts.append((x, y, 0))
    ctrl.draw_polyline(right_ear_pts, closed=True, color=DARK_GRAY)
    right_ear_inner = []
    for angle in range(0, 361, 10):
        rad = math.radians(angle)
        x = right_ear_center[0] + 5 * math.cos(rad)
        y = right_ear_center[1] + 18 * math.sin(rad)
        right_ear_inner.append((x, y, 0))
    ctrl.draw_hatch(right_ear_inner, "SOLID", 1.0, color=PINK)

    # ========== 5-7. 眼睛 ==========
    print("[~] 绘制眼睛...")
    left_eye_center = (-7, 34, 0)
    ctrl.draw_circle(left_eye_center, 3.5, color=BLACK)
    left_eye_pts = []
    for angle in range(0, 360, 15):
        rad = math.radians(angle)
        x = left_eye_center[0] + 3.5 * math.cos(rad)
        y = left_eye_center[1] + 3.5 * math.sin(rad)
        left_eye_pts.append((x, y, 0))
    ctrl.draw_hatch(left_eye_pts, "SOLID", 1.0, color=BLACK)

    right_eye_center = (7, 34, 0)
    ctrl.draw_circle(right_eye_center, 3.5, color=BLACK)
    right_eye_pts = []
    for angle in range(0, 360, 15):
        rad = math.radians(angle)
        x = right_eye_center[0] + 3.5 * math.cos(rad)
        y = right_eye_center[1] + 3.5 * math.sin(rad)
        right_eye_pts.append((x, y, 0))
    ctrl.draw_hatch(right_eye_pts, "SOLID", 1.0, color=BLACK)

    ctrl.draw_circle((-5.5, 35.5, 0), 1.2, color=WHITE)
    ctrl.draw_circle((8.5, 35.5, 0), 1.2, color=WHITE)

    # ========== 8. 鼻子 ==========
    print("[~] 绘制鼻子...")
    nose_center = (0, 27, 0)
    ctrl.draw_circle(nose_center, 2.5, color=PINK)
    nose_pts = []
    for angle in range(0, 360, 15):
        rad = math.radians(angle)
        x = nose_center[0] + 2.5 * math.cos(rad)
        y = nose_center[1] + 2.5 * math.sin(rad)
        nose_pts.append((x, y, 0))
    ctrl.draw_hatch(nose_pts, "SOLID", 1.0, color=PINK)

    # ========== 9. 嘴巴 ==========
    print("[~] 绘制嘴巴...")
    ctrl.draw_line((0, 24.5, 0), (0, 21, 0), color=DARK_GRAY)
    mouth_left = [(0, 21, 0), (-3, 19, 0), (-6, 19.5, 0)]
    ctrl.draw_polyline(mouth_left, closed=False, color=DARK_GRAY)
    mouth_right = [(0, 21, 0), (3, 19, 0), (6, 19.5, 0)]
    ctrl.draw_polyline(mouth_right, closed=False, color=DARK_GRAY)

    # ========== 10. 腮红 ==========
    print("[~] 绘制腮红...")
    blush_left_center = (-13, 27)
    blush_left_pts = []
    for angle in range(0, 361, 10):
        rad = math.radians(angle)
        x = blush_left_center[0] + 4 * math.cos(rad)
        y = blush_left_center[1] + 2.5 * math.sin(rad)
        blush_left_pts.append((x, y, 0))
    ctrl.draw_polyline(blush_left_pts, closed=True, color=PINK)
    ctrl.draw_hatch(blush_left_pts, "SOLID", 1.0, color=PINK)

    blush_right_center = (13, 27)
    blush_right_pts = []
    for angle in range(0, 361, 10):
        rad = math.radians(angle)
        x = blush_right_center[0] + 4 * math.cos(rad)
        y = blush_right_center[1] + 2.5 * math.sin(rad)
        blush_right_pts.append((x, y, 0))
    ctrl.draw_polyline(blush_right_pts, closed=True, color=PINK)
    ctrl.draw_hatch(blush_right_pts, "SOLID", 1.0, color=PINK)

    # ========== 11. 胡须 ==========
    print("[~] 绘制胡须...")
    ctrl.draw_line((-15, 29, 0), (-28, 32, 0), color=DARK_GRAY)
    ctrl.draw_line((-15, 27, 0), (-28, 27, 0), color=DARK_GRAY)
    ctrl.draw_line((-15, 25, 0), (-28, 22, 0), color=DARK_GRAY)
    ctrl.draw_line((15, 29, 0), (28, 32, 0), color=DARK_GRAY)
    ctrl.draw_line((15, 27, 0), (28, 27, 0), color=DARK_GRAY)
    ctrl.draw_line((15, 25, 0), (28, 22, 0), color=DARK_GRAY)

    # ========== 12. 尾巴 ==========
    print("[~] 绘制尾巴...")
    tail_center = (-38, 5, 0)
    ctrl.draw_circle(tail_center, 8, color=DARK_GRAY)
    tail_pts = []
    for angle in range(0, 360, 10):
        rad = math.radians(angle)
        x = tail_center[0] + 8 * math.cos(rad)
        y = tail_center[1] + 8 * math.sin(rad)
        tail_pts.append((x, y, 0))
    ctrl.draw_hatch(tail_pts, "SOLID", 1.0, color=WHITE)

    # ========== 13-14. 爪子 ==========
    print("[~] 绘制爪子...")
    left_paw_center = (-10, -20)
    left_paw_pts = []
    for angle in range(0, 361, 10):
        rad = math.radians(angle)
        x = left_paw_center[0] + 6 * math.cos(rad)
        y = left_paw_center[1] + 4 * math.sin(rad)
        left_paw_pts.append((x, y, 0))
    ctrl.draw_polyline(left_paw_pts, closed=True, color=DARK_GRAY)
    ctrl.draw_hatch(left_paw_pts, "SOLID", 1.0, color=WHITE)

    right_paw_center = (10, -20)
    right_paw_pts = []
    for angle in range(0, 361, 10):
        rad = math.radians(angle)
        x = right_paw_center[0] + 6 * math.cos(rad)
        y = right_paw_center[1] + 4 * math.sin(rad)
        right_paw_pts.append((x, y, 0))
    ctrl.draw_polyline(right_paw_pts, closed=True, color=DARK_GRAY)
    ctrl.draw_hatch(right_paw_pts, "SOLID", 1.0, color=WHITE)

    left_back_paw = (-18, -20)
    lbp_pts = []
    for angle in range(0, 361, 10):
        rad = math.radians(angle)
        x = left_back_paw[0] + 7 * math.cos(rad)
        y = left_back_paw[1] + 4.5 * math.sin(rad)
        lbp_pts.append((x, y, 0))
    ctrl.draw_polyline(lbp_pts, closed=True, color=DARK_GRAY)
    ctrl.draw_hatch(lbp_pts, "SOLID", 1.0, color=WHITE)

    right_back_paw = (18, -20)
    rbp_pts = []
    for angle in range(0, 361, 10):
        rad = math.radians(angle)
        x = right_back_paw[0] + 7 * math.cos(rad)
        y = right_back_paw[1] + 4.5 * math.sin(rad)
        rbp_pts.append((x, y, 0))
    ctrl.draw_polyline(rbp_pts, closed=True, color=DARK_GRAY)
    ctrl.draw_hatch(rbp_pts, "SOLID", 1.0, color=WHITE)

    # ========== 15. 草地 ==========
    print("[~] 绘制草地...")
    grass_points = [
        (-55, -26, 0), (-40, -28, 0), (-25, -25, 0),
        (-10, -29, 0), (0, -26, 0), (10, -29, 0),
        (25, -25, 0), (40, -28, 0), (55, -26, 0),
    ]
    ctrl.draw_polyline(grass_points, closed=False, color=3)

    # ========== 16. 标题 ==========
    print("[~] 添加标题...")
    ctrl.draw_text((0, 90, 0), "Ke Ai Xiao Tu Zi", height=8, color=DARK_GRAY)

    ctrl.refresh_view()

    print("\n[OK] 兔子绘制完成！")
    print("=" * 40)
    print("      可 爱 兔 子")
    print("=" * 40)
    return True

if __name__ == "__main__":
    draw_cute_rabbit()
