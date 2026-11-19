#!/usr/bin/env python3
"""
triangle_eraser.py
批量擦除工程图纸 PDF 中的「三角形注释符号」（△1, △2 等改版标记）

特点：
  · 只擦除「小三角形框 + 框内数字」的组合，不影响邻近的尺寸数字（如 100）和表格边框线
  · 支持矢量 PDF（CAD/AutoCAD 导出）和扫描版 PDF（图片页面），自动切换检测策略
  · 用白色矩形覆盖（redaction），不改变页面其他所有内容

安装依赖：
  pip install pymupdf opencv-python numpy

用法示例：
  # 单张测试（强烈建议先跑这个确认效果，会在原文件旁生成 *_clean.pdf）
  python triangle_eraser.py --test drawing.pdf

  # 单张测试 + debug 模式（额外输出红框标注图 PNG，直观看到识别了哪些三角形）
  python triangle_eraser.py --test drawing.pdf --debug

  # 批量处理整个文件夹
  python triangle_eraser.py --input ./pdf_in --output ./pdf_out

  # 批量 + debug
  python triangle_eraser.py --input ./pdf_in --output ./pdf_out --debug
"""

import fitz          # PyMuPDF
import cv2
import numpy as np
import os, sys, argparse, math
from pathlib import Path

# ═══════════════════════════════════════════════════════════════════════════════
#  可调参数（如果识别效果不理想，先从这里调整）
# ═══════════════════════════════════════════════════════════════════════════════

# 三角形大小范围（单位：PDF points，1 pt = 1/72 英寸 ≈ 0.353 mm）
# 工程图纸上的改版三角符号通常 5~12 mm，对应 14~34 pt
# 如果漏识别，可适当调大 TRIANGLE_MAX_PT；如果误识别，可调小
TRIANGLE_MIN_PT = 6     # 低于此尺寸的三角形跳过（箭头尖端、极小装饰）
TRIANGLE_MAX_PT = 50    # 超过此尺寸的三角形跳过（大型几何图形、箭头块）

# 擦除区域向外扩展量（pt），确保三角形边线也被完整覆盖住
# 保持尽量小，避免白色矩形扩到相邻内容（如旁边的尺寸数字）
ERASE_PADDING_PT = 1

# 矢量路径检测：三条边长度的「不均匀度」容差（0~1，越大越宽松）
# 等腰/等边三角形约 0~0.15；改版符号有时稍微变形，给到 0.4
SIDE_RATIO_TOLERANCE = 0.45

# 图像层检测（扫描 PDF）：渲染分辨率（dpi）——越高越准但越慢，150 够用
IMAGE_DPI = 150

# 图像层检测：多边形轮廓近似精度（占周长比例），越小越严格（认定为三角形）
APPROX_EPSILON_RATIO = 0.05

# 三角形框内「数字」的搜索扩展量（pt）
# 只用于初步搜索候选词，最终判定仍用中心点是否在框内——保持小值即可
DIGIT_SEARCH_EXPAND = 2

# ═══════════════════════════════════════════════════════════════════════════════
#  矢量路径层检测（适用于 CAD 导出的矢量 PDF）
# ═══════════════════════════════════════════════════════════════════════════════

def _extract_vertices(items):
    """
    从 get_drawings() 的 items 列表提取顶点序列。
    只处理 moveto / lineto，贝塞尔曲线只取终点（粗略近似）。
    """
    pts = []
    for item in items:
        kind = item[0]
        if kind == "m":          # moveto → 起点
            pts.append(item[1])
        elif kind == "l":        # lineto → 终点
            pts.append(item[1])
        elif kind == "c":        # curveto → 只取终点
            pts.append(item[3])
    return pts


def _looks_like_triangle(items):
    """
    判断一条路径是否是三角形：
      · 恰好 3 条直线段（lineto）
      · 没有贝塞尔曲线
      · 三条边的长度接近（等腰/等边）
    """
    line_items  = [i for i in items if i[0] == "l"]
    curve_items = [i for i in items if i[0] == "c"]

    if len(line_items) != 3 or len(curve_items) > 0:
        return False

    pts = _extract_vertices(items)

    # 去掉重复点（起点/终点相同），取前 3 个唯一顶点
    unique = []
    for p in pts:
        if not unique or (abs(p.x - unique[-1].x) > 0.5 or abs(p.y - unique[-1].y) > 0.5):
            unique.append(p)
        if len(unique) == 3:
            break

    if len(unique) < 3:
        return False

    def dist(a, b):
        return math.hypot(a.x - b.x, a.y - b.y)

    sides = [dist(unique[0], unique[1]),
             dist(unique[1], unique[2]),
             dist(unique[2], unique[0])]

    mean_side = sum(sides) / 3
    if mean_side < 1:
        return False

    std = math.sqrt(sum((s - mean_side) ** 2 for s in sides) / 3)
    return (std / mean_side) < SIDE_RATIO_TOLERANCE


def find_vector_triangles(page):
    """
    在矢量图形层（page.get_drawings）查找三角形注释符号。
    返回 list of fitz.Rect（三角形的边界框）。
    """
    found = []

    for d in page.get_drawings():
        rect = d.get("rect")
        if rect is None:
            continue

        w, h = rect.width, rect.height

        # ① 大小过滤
        if w < TRIANGLE_MIN_PT or h < TRIANGLE_MIN_PT:
            continue
        if w > TRIANGLE_MAX_PT or h > TRIANGLE_MAX_PT:
            continue

        # ② 长宽比过滤：三角形边界框大致接近正方形（0.3~3.0）
        ratio = w / h if h > 0 else 0
        if ratio < 0.3 or ratio > 3.0:
            continue

        # ③ 形状过滤：3 条直线段构成的闭合三角形
        items = d.get("items", [])
        if not _looks_like_triangle(items):
            continue

        found.append(rect)

    return found


# ═══════════════════════════════════════════════════════════════════════════════
#  图像层检测（适用于扫描版 PDF / 图片页面，作为矢量检测的兜底）
# ═══════════════════════════════════════════════════════════════════════════════

def find_image_triangles(page, dpi=IMAGE_DPI):
    """
    把页面渲染成灰度图，用 OpenCV 轮廓检测找三角形注释符号。
    返回 list of fitz.Rect（已换算回 PDF points 坐标）。
    """
    mat = fitz.Matrix(dpi / 72, dpi / 72)
    pix = page.get_pixmap(matrix=mat, colorspace=fitz.csGRAY)
    img = np.frombuffer(pix.samples, dtype=np.uint8).reshape(pix.height, pix.width)

    # 二值化（深色线条 → 白色前景，白色背景 → 黑色）
    _, binary = cv2.threshold(img, 200, 255, cv2.THRESH_BINARY_INV)

    contours, _ = cv2.findContours(binary, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)

    # 坐标换算比例：图像像素 → PDF points
    scale = 72.0 / dpi
    found = []

    for cnt in contours:
        area = cv2.contourArea(cnt)
        if area < 20:
            continue    # 噪点，太小

        # 多边形近似：顶点数等于 3 才是三角形
        peri   = cv2.arcLength(cnt, True)
        approx = cv2.approxPolyDP(cnt, APPROX_EPSILON_RATIO * peri, True)

        if len(approx) != 3:
            continue

        # 凸性检查（改版三角形都是凸多边形）
        if not cv2.isContourConvex(approx):
            continue

        x, y, w, h = cv2.boundingRect(cnt)
        w_pt = w * scale
        h_pt = h * scale

        # 大小过滤
        if w_pt < TRIANGLE_MIN_PT or h_pt < TRIANGLE_MIN_PT:
            continue
        if w_pt > TRIANGLE_MAX_PT or h_pt > TRIANGLE_MAX_PT:
            continue

        # 长宽比过滤
        ratio = w / h if h > 0 else 0
        if ratio < 0.3 or ratio > 3.0:
            continue

        # 换算回 PDF 坐标（PyMuPDF 页面坐标原点在左上角，y 轴向下，与图像一致）
        x0 = x       * scale
        y0 = y       * scale
        x1 = (x + w) * scale
        y1 = (y + h) * scale

        found.append(fitz.Rect(x0, y0, x1, y1))

    return found


# ═══════════════════════════════════════════════════════════════════════════════
#  核心验证：三角形框内必须有纯数字
# ═══════════════════════════════════════════════════════════════════════════════

def _word_center_inside(word_tuple, tri_rect):
    """
    判断一个词的「中心点」是否落在三角形边界框内。
    这是防止旁边的尺寸数字（如「100」）被误收的核心判断。
    word_tuple 是 get_text("words") 返回的 (x0, y0, x1, y1, text, ...) 元组。
    """
    wx0, wy0, wx1, wy1 = word_tuple[:4]
    cx = (wx0 + wx1) / 2
    cy = (wy0 + wy1) / 2
    return tri_rect.x0 <= cx <= tri_rect.x1 and tri_rect.y0 <= cy <= tri_rect.y1


def has_digit_inside(page, tri_rect):
    """
    检查三角形边界框内是否有纯数字文本（1~4 位改版编号）。
    判定条件：数字词的「中心点」必须落在三角形框内（不只是与扩展区域相交）。
    这样即使「100」紧贴三角形右侧，只要它的中心在框外，就不会被认为是框内数字。
    """
    search = fitz.Rect(
        tri_rect.x0 - DIGIT_SEARCH_EXPAND,
        tri_rect.y0 - DIGIT_SEARCH_EXPAND,
        tri_rect.x1 + DIGIT_SEARCH_EXPAND,
        tri_rect.y1 + DIGIT_SEARCH_EXPAND,
    )

    for word in page.get_text("words", clip=search):
        text = word[4].strip()
        if text.isdigit() and 1 <= len(text) <= 4:
            # ★ 核心修复：中心点必须在三角形框内，不能只是在扩展搜索区里
            if _word_center_inside(word, tri_rect):
                return True

    return False


def collect_digit_rects_inside(page, tri_rect):
    """
    收集三角形边界框内所有纯数字文字的矩形（用于一并擦除）。
    同样用中心点判定，确保只收框内的数字，不收旁边的尺寸数字（如「100」）。
    """
    search = fitz.Rect(
        tri_rect.x0 - DIGIT_SEARCH_EXPAND,
        tri_rect.y0 - DIGIT_SEARCH_EXPAND,
        tri_rect.x1 + DIGIT_SEARCH_EXPAND,
        tri_rect.y1 + DIGIT_SEARCH_EXPAND,
    )

    rects = []
    for word in page.get_text("words", clip=search):
        text = word[4].strip()
        if text.isdigit() and 1 <= len(text) <= 4:
            # ★ 核心修复：只收中心点在三角形框内的数字
            if _word_center_inside(word, tri_rect):
                rects.append(fitz.Rect(word[:4]))

    return rects


# ═══════════════════════════════════════════════════════════════════════════════
#  方案A：擦除前收集被遮文字 → 擦除 → 恢复文字
# ═══════════════════════════════════════════════════════════════════════════════

def _color_int_to_rgb(color_int):
    """PDF 颜色整数 (0xRRGGBB) → (r, g, b)，各分量 0.0-1.0。"""
    r = ((color_int >> 16) & 0xFF) / 255.0
    g = ((color_int >> 8)  & 0xFF) / 255.0
    b = ( color_int        & 0xFF) / 255.0
    return (r, g, b)


def collect_covered_text(page, tri_rect):
    """
    擦除前，收集三角形白色遮盖区域内「非改版编号」的原始文字信息。
    这些文字在擦除后会被重新插回，防止把原图表格里的字一起抹掉。

    返回 list of dict，每项含 text / bbox / size / color。
    """
    erase_rect = fitz.Rect(
        tri_rect.x0 - ERASE_PADDING_PT,
        tri_rect.y0 - ERASE_PADDING_PT,
        tri_rect.x1 + ERASE_PADDING_PT,
        tri_rect.y1 + ERASE_PADDING_PT,
    )

    result = []
    raw = page.get_text("rawdict", clip=erase_rect)

    for block in raw.get("blocks", []):
        if block.get("type") != 0:   # 只处理文字块
            continue
        for line in block.get("lines", []):
            for span in line.get("spans", []):
                text = span.get("text", "")
                stripped = text.strip()
                if not stripped:
                    continue

                # 三角形框内的改版编号数字（1~4 位纯数字 + 中心在框内）→ 要删，不恢复
                if stripped.isdigit() and 1 <= len(stripped) <= 4:
                    bbox = span["bbox"]
                    cx = (bbox[0] + bbox[2]) / 2
                    cy = (bbox[1] + bbox[3]) / 2
                    if tri_rect.x0 <= cx <= tri_rect.x1 and tri_rect.y0 <= cy <= tri_rect.y1:
                        continue

                result.append({
                    "bbox":  span["bbox"],
                    "text":  text,                               # 保留含空格的原始文本
                    "size":  span.get("size", 10),
                    "color": _color_int_to_rgb(span.get("color", 0)),
                })

    return result


def restore_covered_text(page, covered_list):
    """
    擦除后，把之前收集的文字重新插回页面。
    字号和颜色取自原始数据；字体统一使用 Helvetica（近似）。
    若原始文字含 CJK 字符，Helvetica 无法正确渲染，会打印 WARN。
    """
    for t in covered_list:
        bbox = t["bbox"]
        # 插入点：文字基线左端（bbox 左下角，y 取 y1）
        pt = fitz.Point(bbox[0], bbox[3])
        try:
            page.insert_text(
                pt,
                t["text"],
                fontsize=t["size"],
                color=t["color"],
                fontname="helv",
            )
        except Exception as e:
            print(f"    [WARN] 文字恢复失败（{repr(t['text'][:15])}）：{e}")


# ═══════════════════════════════════════════════════════════════════════════════
#  擦除一页
# ═══════════════════════════════════════════════════════════════════════════════

def erase_triangles_on_page(page, debug=False):
    """
    检测并擦除一页上的所有三角形注释符号。

    返回：
      (erased_count, debug_image_or_None)
      debug_image 是 RGB numpy array，标注了被识别到的区域（红框）。
      注意：debug_image 在 apply_redactions 之前捕获，所以显示的是原始页面。
    """
    # ── 步骤 1：矢量层检测 ────────────────────────────────────────────────────
    triangles = find_vector_triangles(page)
    triangles = [r for r in triangles if has_digit_inside(page, r)]

    # ── 步骤 2：如果矢量层找不到，改用图像层检测（扫描 PDF 兜底）────────────
    used_image_detection = False
    if not triangles:
        triangles = find_image_triangles(page)
        triangles = [r for r in triangles if has_digit_inside(page, r)]
        if triangles:
            used_image_detection = True

    if not triangles:
        return 0, None

    # ── 步骤 3：如果开了 debug，在擦除前先渲染原页面并标注红框 ────────────────
    debug_img = None
    if debug:
        mat = fitz.Matrix(IMAGE_DPI / 72, IMAGE_DPI / 72)
        pix = page.get_pixmap(matrix=mat, colorspace=fitz.csRGB)
        img = np.frombuffer(pix.samples, dtype=np.uint8) \
                .reshape(pix.height, pix.width, 3).copy()
        scale = IMAGE_DPI / 72

        for tri in triangles:
            # 收集该三角形关联的所有擦除区域（三角形本身 + 框内数字）
            digit_rects = collect_digit_rects_inside(page, tri)
            all_rects = [tri] + digit_rects

            for r in all_rects:
                x0 = max(0, int(r.x0 * scale))
                y0 = max(0, int(r.y0 * scale))
                x1 = min(img.shape[1], int(r.x1 * scale))
                y1 = min(img.shape[0], int(r.y1 * scale))
                cv2.rectangle(img, (x0, y0), (x1, y1), (255, 0, 0), 2)  # 红框

        debug_img = img

    # ── 步骤 3.5：收集被三角形遮盖的原始文字（方案A：擦除后需恢复）────────────
    covered_per_tri = []
    for tri in triangles:
        covered = collect_covered_text(page, tri)
        covered_per_tri.append(covered)
        if covered:
            preview = [c["text"].strip() for c in covered if c["text"].strip()]
            print(f"    [INFO] 三角形下检测到原始文字，擦除后将恢复：{preview}")

    # ── 步骤 4：添加 redaction 遮盖（白色矩形）──────────────────────────────
    for tri in triangles:
        # 三角形轮廓本身（加 padding，确保边线也被覆盖）
        padded = fitz.Rect(
            tri.x0 - ERASE_PADDING_PT,
            tri.y0 - ERASE_PADDING_PT,
            tri.x1 + ERASE_PADDING_PT,
            tri.y1 + ERASE_PADDING_PT,
        )
        page.add_redact_annot(padded, fill=(1, 1, 1))   # 白色填充

        # 框内数字文字（单独再加一次，确保文字被清除）
        for digit_rect in collect_digit_rects_inside(page, tri):
            page.add_redact_annot(digit_rect, fill=(1, 1, 1))

    # 应用所有 redaction（同时处理矢量图形和图像像素）
    page.apply_redactions(images=fitz.PDF_REDACT_IMAGE_PIXELS)

    # ── 步骤 5：恢复被三角形遮盖的原始文字 ──────────────────────────────────
    for covered in covered_per_tri:
        restore_covered_text(page, covered)

    method = "图像层" if used_image_detection else "矢量层"
    return len(triangles), debug_img


# ═══════════════════════════════════════════════════════════════════════════════
#  单文件处理
# ═══════════════════════════════════════════════════════════════════════════════

def process_pdf(input_path, output_path, debug=False):
    """
    处理单个 PDF 文件：检测并擦除所有三角形注释符号，保存到 output_path。
    返回总擦除数量。
    """
    input_path  = Path(input_path)
    output_path = Path(output_path)

    doc   = fitz.open(str(input_path))
    total = 0

    for i, page in enumerate(doc):
        count, debug_img = erase_triangles_on_page(page, debug=debug)
        total += count

        if count > 0:
            print(f"    第 {i + 1} 页：擦除 {count} 个三角形注释符号")
        else:
            print(f"    第 {i + 1} 页：未检测到三角形注释符号，跳过")

        # 保存 debug 标注图
        if debug and debug_img is not None:
            debug_path = output_path.with_name(
                output_path.stem + f"_debug_p{i + 1}.png"
            )
            cv2.imwrite(str(debug_path),
                        cv2.cvtColor(debug_img, cv2.COLOR_RGB2BGR))
            print(f"    → Debug 标注图：{debug_path}")

    doc.save(str(output_path), garbage=4, deflate=True)
    doc.close()

    return total


# ═══════════════════════════════════════════════════════════════════════════════
#  批量处理
# ═══════════════════════════════════════════════════════════════════════════════

def batch_process(input_dir, output_dir, debug=False):
    """
    批量处理 input_dir 下所有 .pdf 文件（不递归子文件夹），
    结果保存到 output_dir，保持原文件名不变。
    """
    input_dir  = Path(input_dir)
    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    pdfs = sorted(input_dir.glob("*.pdf"))

    if not pdfs:
        print(f"[警告] 在「{input_dir}」里没有找到 .pdf 文件。")
        return

    print(f"共找到 {len(pdfs)} 个 PDF，输出目录：{output_dir}\n")
    print("=" * 60)

    total_erased = 0
    failed       = []

    for idx, pdf_path in enumerate(pdfs, 1):
        out_path = output_dir / pdf_path.name
        print(f"[{idx}/{len(pdfs)}] {pdf_path.name}")

        try:
            count = process_pdf(pdf_path, out_path, debug=debug)
            print(f"  → 完成，共擦除 {count} 个符号。\n")
            total_erased += count

        except Exception as e:
            print(f"  → [错误] {type(e).__name__}: {e}\n")
            failed.append((pdf_path.name, str(e)))

    print("=" * 60)
    print(f"批量完成：{len(pdfs)} 个文件，共擦除 {total_erased} 个三角形注释符号。")
    print(f"输出目录：{output_dir}")

    if failed:
        print(f"\n以下 {len(failed)} 个文件处理失败（已跳过，不影响其他文件）：")
        for name, err in failed:
            print(f"  ✗ {name}：{err}")


# ═══════════════════════════════════════════════════════════════════════════════
#  命令行入口
# ═══════════════════════════════════════════════════════════════════════════════

def main():
    parser = argparse.ArgumentParser(
        description="批量擦除工程图纸 PDF 中的三角形注释符号（△1, △2 等改版标记）",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
示例：
  # 单张测试（先运行这个确认效果）
  python triangle_eraser.py --test drawing.pdf

  # 单张测试 + debug 模式（生成红框标注 PNG）
  python triangle_eraser.py --test drawing.pdf --debug

  # 批量处理整个文件夹
  python triangle_eraser.py --input ./pdf_in --output ./pdf_out

  # 批量 + debug
  python triangle_eraser.py --input ./pdf_in --output ./pdf_out --debug
        """,
    )

    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument(
        "--test", metavar="PDF文件",
        help="单张测试模式：指定一个 PDF 文件路径，处理后在同目录生成 *_clean.pdf",
    )
    mode.add_argument(
        "--input", metavar="输入文件夹",
        help="批量模式：指定包含 PDF 的输入文件夹",
    )

    parser.add_argument(
        "--output", metavar="输出文件夹",
        help="批量模式下必填；单张模式下可选（默认在原文件旁生成 *_clean.pdf）",
    )
    parser.add_argument(
        "--debug", action="store_true",
        help="额外输出 PNG 标注图，用红框标出识别到的三角形区域（方便核对效果）",
    )

    args = parser.parse_args()

    # ── 单张测试 ──────────────────────────────────────────────────────────────
    if args.test:
        input_path = Path(args.test)

        if not input_path.exists():
            print(f"[错误] 文件不存在：{input_path}")
            sys.exit(1)

        if args.output:
            out_dir = Path(args.output)
            out_dir.mkdir(parents=True, exist_ok=True)
            output_path = out_dir / input_path.name
        else:
            output_path = input_path.with_name(input_path.stem + "_clean.pdf")

        print(f"单张测试：{input_path}")
        print(f"输出路径：{output_path}")
        print("-" * 60)

        try:
            count = process_pdf(input_path, output_path, debug=args.debug)
            print("-" * 60)
            print(f"完成：共擦除 {count} 个三角形注释符号 → {output_path}")

            if count == 0:
                print("\n[提示] 未检测到符号，可能的原因：")
                print("  1. 图纸里的三角形尺寸超出范围（调整 TRIANGLE_MAX_PT / TRIANGLE_MIN_PT）")
                print("  2. 三角形边数不是 3（CAD 用贝塞尔曲线画的，调整 SIDE_RATIO_TOLERANCE）")
                print("  3. 三角形内部没有纯数字文本（框内有字母或汉字，不在擦除范围）")
                print("  加上 --debug 参数可以生成标注图，直观看到检测范围。")

        except Exception as e:
            print(f"[错误] {type(e).__name__}: {e}")
            sys.exit(1)

    # ── 批量模式 ──────────────────────────────────────────────────────────────
    else:
        if not args.output:
            print("[错误] 批量模式下请用 --output 指定输出文件夹。")
            sys.exit(1)

        input_dir = Path(args.input)
        if not input_dir.exists():
            print(f"[错误] 输入文件夹不存在：{input_dir}")
            sys.exit(1)

        batch_process(args.input, args.output, debug=args.debug)


if __name__ == "__main__":
    main()
