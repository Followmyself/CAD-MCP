"""
简化版兔子 - 只用基本绘图命令，不用 hatch
通过 CAD-MCP 的 CADController 操作 AutoCAD

特点：
- 所有椭圆用多段线近似，避免 AddEllipse 的 COM 问题
- 完全避免 hatch，防止 RPC_E_SERVERCALL_RETRYLATER 级联失败
- 操作间加 command_delay=0.3 让 COM 线程有时间处理
"""
import sys
import os
import math
import time
import io

if sys.platform == "win32":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8')

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)) + '/src')
from cad_controller import CADController

def ellipse_points(cx, cy, rx, ry, n=36):
    """生成椭圆多段线的点"""
    pts = []
    for i in range(n + 1):
        angle = 2 * math.pi * i / n
        x = cx + rx * math.cos(angle)
        y = cy + ry * math.sin(angle)
        pts.append((x, y, 0))
    return pts

def draw_cute_rabbit():
    ctrl = CADController()
    ctrl.command_delay = 0.3  # 增加延迟避免 COM 冲突

    print("正在连接 AutoCAD...")
    if not ctrl.start_cad():
        print("[X] 无法连接 AutoCAD")
        return False

    print(f"[OK] 已连接到: {ctrl.doc.Name}")

    # 新建干净图纸
    try:
        new_doc = ctrl.app.Documents.Add()
        ctrl.doc = new_doc
        print(f"[OK] 新建图纸: {ctrl.doc.Name}")
    except:
        print("[!] 使用当前图纸")

    time.sleep(1)

    DARK = 251   # 深灰轮廓
    WHITE = 7    # 白色
    PINK = 6     # 品红当粉色
    GREEN = 3    # 绿色

    ms = ctrl.doc.ModelSpace

    # ===== 1. 身体 - 大椭圆多段线 =====
    print("[1/10] 身体...")
    pts = ellipse_points(0, 0, 35, 22, 60)
    ctrl.draw_polyline(pts, closed=True, color=DARK)

    # ===== 2. 头部 - 圆 =====
    print("[2/10] 头部...")
    ctrl.draw_circle((0, 30, 0), 18, color=DARK)

    # ===== 3. 左耳 =====
    print("[3/10] 左耳...")
    pts = ellipse_points(-10, 58, 7, 24, 40)
    ctrl.draw_polyline(pts, closed=True, color=DARK)
    # 内耳
    pts_inner = ellipse_points(-10, 58, 4, 17, 30)
    ctrl.draw_polyline(pts_inner, closed=True, color=PINK)

    # ===== 4. 右耳 =====
    print("[4/10] 右耳...")
    pts = ellipse_points(10, 58, 7, 24, 40)
    ctrl.draw_polyline(pts, closed=True, color=DARK)
    pts_inner = ellipse_points(10, 58, 4, 17, 30)
    ctrl.draw_polyline(pts_inner, closed=True, color=PINK)

    # ===== 5. 眼睛 =====
    print("[5/10] 眼睛...")
    ctrl.draw_circle((-7, 34, 0), 3.5, color=0)
    ctrl.draw_circle((7, 34, 0), 3.5, color=0)
    # 高光
    ctrl.draw_circle((-5.5, 35.5, 0), 1.2, color=WHITE)
    ctrl.draw_circle((8.5, 35.5, 0), 1.2, color=WHITE)

    # ===== 6. 鼻子 =====
    print("[6/10] 鼻子...")
    ctrl.draw_circle((0, 27, 0), 2.5, color=PINK)

    # ===== 7. 嘴巴 =====
    print("[7/10] 嘴巴...")
    ctrl.draw_line((0, 24.5, 0), (0, 21, 0), color=DARK)
    ctrl.draw_line((0, 21, 0), (-5, 19.5, 0), color=DARK)
    ctrl.draw_line((0, 21, 0), (5, 19.5, 0), color=DARK)

    # ===== 8. 腮红 =====
    print("[8/10] 腮红...")
    pts = ellipse_points(-13, 27, 4, 2.5, 24)
    ctrl.draw_polyline(pts, closed=True, color=PINK)
    pts = ellipse_points(13, 27, 4, 2.5, 24)
    ctrl.draw_polyline(pts, closed=True, color=PINK)

    # ===== 9. 胡须 =====
    print("[9/10] 胡须...")
    for dx, dy in [(28, 32), (28, 27), (28, 22)]:
        ctrl.draw_line((-15, 29, 0), (-dx, dy, 0), color=DARK)
        ctrl.draw_line((15, 29, 0), (dx, dy, 0), color=DARK)
        time.sleep(0.1)

    # ===== 10. 尾巴 =====
    print("[10/10] 尾巴...")
    ctrl.draw_circle((-38, 5, 0), 7, color=DARK)

    ctrl.refresh_view()

    print("\n" + "=" * 40)
    print("   [OK] 兔子绘制完成！")
    print("   - 身体 (椭圆)")
    print("   - 头部 (圆)")
    print("   - 2只长耳朵 (粉色填充)")
    print("   - 2只圆眼睛 + 高光")
    print("   - 粉色鼻子")
    print("   - 微笑嘴巴")
    print("   - 2团腮红")
    print("   - 6根胡须")
    print("   - 白色尾巴")
    print("=" * 40)
    return True

if __name__ == "__main__":
    draw_cute_rabbit()
