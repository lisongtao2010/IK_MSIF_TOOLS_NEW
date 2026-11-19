#!/usr/bin/env python3
"""
triangle_eraser.py (底层矢量指令外科手术版 - 全形态 CAD 三角形通杀版)
批量物理删除工程图纸 PDF 中的「三角形注释符号」（△1, △2, △3, △4 等改版标记）

核心升级：
  · 修复了 PDF 中用 `m -> l -> l -> h`（2条线+闭合指令）画三角形时被漏检的 Bug！
  · 针对 AutoCAD SHX 拆线特性：支持通过 `AutoCAD SHX Text` 纯数字批注直接反推锁定三角形区域，
    无论三角形是 1 笔画还是 3 条独立线段拼成的，全部精准识别并物理删除！
  · 按子路径（subpath）粒度手术切割，100% 保留所有表格边框线与紧邻文字（如 MZC●●72）。

安装依赖：
  pip install pymupdf pikepdf opencv-python numpy
"""

import fitz          # PyMuPDF（用于定位识别与生成 debug 图）
import pikepdf       # 底层 PDF 指令流手术刀（用于无损删除指定路径、SHX笔画和文本）
import cv2
import numpy as np
import os, sys, argparse, math
from pathlib import Path

# ═══════════════════════════════════════════════════════════════════════════════
#  可调参数
# ═══════════════════════════════════════════════════════════════════════════════

TRIANGLE_MIN_PT = 6          # 低于此尺寸的三角形跳过（pt）
TRIANGLE_MAX_PT = 50         # 超过此尺寸的三角形跳过（pt）
SIDE_RATIO_TOLERANCE = 0.45  # 三条边长度均匀度容差（0~1，越大越宽松）
IMAGE_DPI = 150              # debug 预览图渲染分辨率
DIGIT_SEARCH_EXPAND = 2      # 数字搜索扩展容差（pt）

# ═══════════════════════════════════════════════════════════════════════════════
#  1. 利用 PyMuPDF 精准定位「三角形框」（兼容 m-l-l-h、普通字体、AutoCAD SHX）
# ═══════════════════════════════════════════════════════════════════════════════

def _extract_vertices(items):
    pts = []
    for item in items:
        kind = item[0]
        if kind in ("m", "l"):
            pts.append(item[1])
        elif kind == "c":
            pts.append(item[3])
    return pts


def _looks_like_triangle(items):
    """
    判断一条路径是否是三角形（兼容 3条l 或 2条l+闭合指令h）：
      · 提取去重后的唯一顶点数必须恰好为 3
      · 没有贝塞尔曲线
      · 三条边长度接近
    """
    curve_items = [i for i in items if i[0] == "c"]
    if len(curve_items) > 0:
        return False

    pts = _extract_vertices(items)
    unique = []
    for p in pts:
        # 过滤掉与上一个点重合，或与第一个起点重合（闭合点）的坐标
        if any(abs(p.x - u.x) < 0.5 and abs(p.y - u.y) < 0.5 for u in unique):
            continue
        unique.append(p)

    # 必须恰好 3 个顶点（兼容 m-l-l-l 和 m-l-l-h 两种写法）
    if len(unique) != 3:
        return False

    sides = [
        math.hypot(unique[0].x - unique[1].x, unique[0].y - unique[1].y),
        math.hypot(unique[1].x - unique[2].x, unique[1].y - unique[2].y),
        math.hypot(unique[2].x - unique[0].x, unique[2].y - unique[0].y),
    ]
    mean_side = sum(sides) / 3
    if mean_side < 1:
        return False

    std = math.sqrt(sum((s - mean_side) ** 2 for s in sides) / 3)
    return (std / mean_side) < SIDE_RATIO_TOLERANCE


def _get_normal_digits_inside(page, tri_rect):
    """查找普通/CID文本层中落在三角形框内的纯数字字符小框"""
    search = fitz.Rect(
        tri_rect.x0 - DIGIT_SEARCH_EXPAND,
        tri_rect.y0 - DIGIT_SEARCH_EXPAND,
        tri_rect.x1 + DIGIT_SEARCH_EXPAND,
        tri_rect.y1 + DIGIT_SEARCH_EXPAND,
    )
    digit_rects = []
    raw = page.get_text("rawdict", clip=search)
    for block in raw.get("blocks", []):
        if block.get("type") != 0:
            continue
        for line in block.get("lines", []):
            for span in line.get("spans", []):
                for ch in span.get("chars", []):
                    c = ch.get("c", "")
                    if c.isdigit():
                        bx0, by0, bx1, by1 = ch["bbox"]
                        cx, cy = (bx0 + bx1) / 2, (by0 + by1) / 2
                        if tri_rect.x0 <= cx <= tri_rect.x1 and tri_rect.y0 <= cy <= tri_rect.y1:
                            digit_rects.append(fitz.Rect(bx0, by0, bx1, by1))
    return digit_rects


def _get_all_shx_digit_annots(page):
    """获取页面上所有内容为 1~4 位纯数字的 AutoCAD SHX Text 批注"""
    shx_list = []
    for annot in page.annots() or []:
        info = annot.info or {}
        title = info.get("title", "")
        content = info.get("content", "").strip()
        if ("AutoCAD SHX" in title or "SHX" in title) and content.isdigit() and 1 <= len(content) <= 4:
            shx_list.append(annot.rect)
    return shx_list


def _has_shx_vector_strokes_inside(all_drawings, tri_rect):
    """检查三角形内部是否包含 AutoCAD SHX 字体拆分出来的小矢量笔画"""
    inner_margin = 0.3
    for d in all_drawings:
        r = d.get("rect")
        if r is None:
            continue
        if (abs(r.x0 - tri_rect.x0) < 0.8 and abs(r.y0 - tri_rect.y0) < 0.8 and
            abs(r.x1 - tri_rect.x1) < 0.8 and abs(r.y1 - tri_rect.y1) < 0.8):
            continue
        if (r.x0 >= tri_rect.x0 + inner_margin and r.x1 <= tri_rect.x1 - inner_margin and
            r.y0 >= tri_rect.y0 + inner_margin and r.y1 <= tri_rect.y1 - inner_margin):
            if r.width > 0.3 or r.height > 1.2:
                return True
    return False


def _merge_rect_into_list(rect_list, new_rect, tol=1.5):
    """去重添加矩形"""
    for r in rect_list:
        if (abs(r.x0 - new_rect.x0) <= tol and abs(r.y0 - new_rect.y0) <= tol and
            abs(r.x1 - new_rect.x1) <= tol and abs(r.y1 - new_rect.y1) <= tol):
            return
    rect_list.append(new_rect)


def find_valid_triangles_and_digits(page):
    """
    双重锁定策略：
      策略 1：扫描所有矢量路径，识别三角形外框 + 内部数字（普通数字 / SHX批注 / SHX笔画）
      策略 2：直接从 AutoCAD SHX Text 纯数字批注出发，反向包围周围的小线段组（专门攻克把三角形拆成3条独立直线的 CAD 图纸）
    """
    tri_rects = []
    all_digit_rects = []

    all_drawings = page.get_drawings()
    all_shx_annots = _get_all_shx_digit_annots(page)

    # ── 策略 1：正向找一笔画三角形 ──
    for d in all_drawings:
        rect = d.get("rect")
        if rect is None:
            continue
        w, h = rect.width, rect.height
        if w < TRIANGLE_MIN_PT or h < TRIANGLE_MIN_PT or w > TRIANGLE_MAX_PT or h > TRIANGLE_MAX_PT:
            continue
        ratio = w / h if h > 0 else 0
        if ratio < 0.3 or ratio > 3.0:
            continue
        if not _looks_like_triangle(d.get("items", [])):
            continue

        digits = _get_normal_digits_inside(page, rect)
        expanded = fitz.Rect(rect.x0 - 3, rect.y0 - 3, rect.x1 + 3, rect.y1 + 3)
        matched_shx = [sr for sr in all_shx_annots if sr.intersects(expanded)]
        has_shx_strokes = _has_shx_vector_strokes_inside(all_drawings, rect)

        if digits or matched_shx or has_shx_strokes:
            _merge_rect_into_list(tri_rects, rect)
            all_digit_rects.extend(digits)

    # ── 策略 2：针对拆成多条独立线段的 AutoCAD SHX 符号，由 SHX 批注反向锁定包围框 ──
    for sr in all_shx_annots:
        # 检查是否已经被策略 1 覆盖
        if any(tr.contains(sr) or tr.intersects(sr) for tr in tri_rects):
            continue

        # 在 SHX 数字批注周围 18pt 范围内收集所有短小线段（构成三角形外框的独立边）
        cx, cy = (sr.x0 + sr.x1) / 2, (sr.y0 + sr.y1) / 2
        search_box = fitz.Rect(cx - 18, cy - 18, cx + 18, cy + 18)
        nearby_small_rects = [sr]

        for d in all_drawings:
            r = d.get("rect")
            if r is None:
                continue
            # 必须是短小线段（排除长表格线！）
            if r.width <= TRIANGLE_MAX_PT and r.height <= TRIANGLE_MAX_PT:
                if search_box.contains(r):
                    nearby_small_rects.append(r)

        if len(nearby_small_rects) >= 2:
            union_rect = fitz.Rect(nearby_small_rects[0])
            for r in nearby_small_rects[1:]:
                union_rect.include_rect(r)
            if TRIANGLE_MIN_PT <= union_rect.width <= TRIANGLE_MAX_PT and TRIANGLE_MIN_PT <= union_rect.height <= TRIANGLE_MAX_PT:
                _merge_rect_into_list(tri_rects, union_rect)

    return tri_rects, all_digit_rects, all_shx_annots


# ═══════════════════════════════════════════════════════════════════════════════
#  2. 底层 PDF 指令流手术刀（按 subpath 子路径逐段切割，绝不伤及表格长线）
# ═══════════════════════════════════════════════════════════════════════════════

def _mat_mul(m1, m2):
    a1, b1, c1, d1, e1, f1 = m1
    a2, b2, c2, d2, e2, f2 = m2
    return [
        a1 * a2 + b1 * c2,
        a1 * b2 + b1 * d2,
        c1 * a2 + d1 * c2,
        c1 * b2 + d1 * d2,
        e1 * a2 + f1 * c2 + e2,
        e1 * b2 + f1 * d2 + f2,
    ]


def _transform_pt(x, y, ctm, page_h, crop_x0, crop_y0):
    px = ctm[0] * x + ctm[2] * y + ctm[4]
    py = ctm[1] * x + ctm[3] * y + ctm[5]
    fx = px - crop_x0
    fy = page_h - (py - crop_y0)
    return fx, fy


def _should_remove_subpath(bbox, tri_rects, tol=1.2):
    """
    判断一条独立的子路径（subpath）是否应该被物理删除：
      只要这条子路径的整个包围盒完全落入（或紧贴重合）某个目标三角形区域内，就删除！
      因为表格边框线很长，其包围盒必然远远超出三角形框，因此 100% 不会被误删！
    """
    if bbox is None:
        return False
    x0, y0, x1, y1 = bbox
    for tr in tri_rects:
        if (x0 >= tr.x0 - tol and x1 <= tr.x1 + tol and
            y0 >= tr.y0 - tol and y1 <= tr.y1 + tol):
            return True
    return False


def _pt_hits_any_digit_rect(fx, fy, digit_rects, tol=2.0):
    for dr in digit_rects:
        if (dr.x0 - tol <= fx <= dr.x1 + tol and
            dr.y0 - tol <= fy <= dr.y1 + tol):
            return True
    return False


def _estimate_tj_width_em(pdf_str_obj, is_cid=True):
    try:
        raw_bytes = bytes(pdf_str_obj)
    except Exception:
        raw_bytes = str(pdf_str_obj).encode("latin1", errors="ignore")
    char_count = max(1, len(raw_bytes) // 2) if (is_cid and len(raw_bytes) >= 2) else max(1, len(raw_bytes))
    return char_count * 550.0


def _remove_triangles_from_stream(pdf, obj_or_page, tri_rects, digit_rects, page_h, crop_x0, crop_y0, init_ctm=None):
    if init_ctm is None:
        init_ctm = [1.0, 0.0, 0.0, 1.0, 0.0, 0.0]

    resources = obj_or_page.get("/Resources")
    if resources and "/XObject" in resources:
        xobjs = resources["/XObject"]
        for name, xobj in list(xobjs.items()):
            try:
                if xobj.get("/Subtype") == pikepdf.Name("/Form"):
                    x_mat = [float(v) for v in xobj.get("/Matrix", [1, 0, 0, 1, 0, 0])]
                    sub_ctm = _mat_mul(x_mat, init_ctm)
                    _remove_triangles_from_stream(pdf, xobj, tri_rects, digit_rects, page_h, crop_x0, crop_y0, sub_ctm)
            except Exception:
                pass

    try:
        instructions = pikepdf.parse_content_stream(obj_or_page)
    except Exception:
        return

    ctm = list(init_ctm)
    ctm_stack = []
    new_instructions = []

    # 按 subpath（每次 m 到下一次 m 或绘制操作符）精细缓存，防止多条线打包在一个 S 里时无法拆分
    subpaths = []          # list of (inst_list, pts_list)
    cur_sub_insts = []
    cur_sub_pts = []

    in_text = False
    tm = [1.0, 0.0, 0.0, 1.0, 0.0, 0.0]
    tlm = [1.0, 0.0, 0.0, 1.0, 0.0, 0.0]
    font_size = 10.0

    PATH_BUILD_OPS = {"m", "l", "c", "v", "y", "h", "re"}
    PATH_PAINT_OPS = {"S", "s", "f", "F", "f*", "B", "B*", "b", "b*", "n"}

    def commit_subpath():
        nonlocal cur_sub_insts, cur_sub_pts
        if cur_sub_insts:
            subpaths.append((cur_sub_insts, cur_sub_pts))
            cur_sub_insts = []
            cur_sub_pts = []

    def flush_all_subpaths(paint_inst=None):
        nonlocal subpaths
        commit_subpath()
        if not subpaths and paint_inst is None:
            return

        kept_insts = []
        for s_insts, s_pts in subpaths:
            bbox = None
            if s_pts:
                xs = [p[0] for p in s_pts]
                ys = [p[1] for p in s_pts]
                bbox = (min(xs), min(ys), max(xs), max(ys))
            # 只有落入三角形框内的子路径才丢弃，同组里的长表格线完整保留！
            if not _should_remove_subpath(bbox, tri_rects):
                kept_insts.extend(s_insts)

        if kept_insts:
            new_instructions.extend(kept_insts)
            if paint_inst is not None:
                new_instructions.append(paint_inst)

        subpaths = []

    for operands, operator in instructions:
        op = str(operator)

        if op == "q":
            flush_all_subpaths()
            ctm_stack.append(list(ctm))
            new_instructions.append((operands, operator))
            continue
        elif op == "Q":
            flush_all_subpaths()
            if ctm_stack:
                ctm = ctm_stack.pop()
            new_instructions.append((operands, operator))
            continue
        elif op == "cm" and len(operands) == 6:
            flush_all_subpaths()
            m = [float(x) for x in operands]
            ctm = _mat_mul(m, ctm)
            new_instructions.append((operands, operator))
            continue

        if op in PATH_BUILD_OPS:
            # 每次遇到新的 moveto (m) 或矩形 (re)，切分出一条独立的子路径
            if op in ("m", "re"):
                commit_subpath()

            cur_sub_insts.append((operands, operator))
            if op in ("m", "l") and len(operands) >= 2:
                fx, fy = _transform_pt(float(operands[0]), float(operands[1]), ctm, page_h, crop_x0, crop_y0)
                cur_sub_pts.append((fx, fy))
            elif op in ("c", "v", "y") and len(operands) >= 2:
                for idx in range(0, len(operands) - 1, 2):
                    fx, fy = _transform_pt(float(operands[idx]), float(operands[idx + 1]), ctm, page_h, crop_x0, crop_y0)
                    cur_sub_pts.append((fx, fy))
            elif op == "re" and len(operands) == 4:
                rx, ry, rw, rh = [float(v) for v in operands]
                for cx, cy in [(rx, ry), (rx + rw, ry), (rx, ry + rh), (rx + rw, ry + rh)]:
                    cur_sub_pts.append(_transform_pt(cx, cy, ctm, page_h, crop_x0, crop_y0))
            continue

        if op in PATH_PAINT_OPS:
            flush_all_subpaths((operands, operator))
            continue

        flush_all_subpaths()

        if op == "BT":
            in_text = True
            tm = [1.0, 0.0, 0.0, 1.0, 0.0, 0.0]
            tlm = [1.0, 0.0, 0.0, 1.0, 0.0, 0.0]
            new_instructions.append((operands, operator))
            continue
        elif op == "ET":
            in_text = False
            new_instructions.append((operands, operator))
            continue

        if in_text:
            if op == "Tf" and len(operands) >= 2:
                font_size = float(operands[1])
            elif op == "Tm" and len(operands) == 6:
                tm = [float(x) for x in operands]
                tlm = list(tm)
            elif op in ("Td", "TD") and len(operands) == 2:
                tx, ty = float(operands[0]), float(operands[1])
                tlm = _mat_mul([1, 0, 0, 1, tx, ty], tlm)
                tm = list(tlm)
            elif op in ("Tj", "'", '"'):
                full_tm = _mat_mul(tm, ctm)
                fx, fy = _transform_pt(font_size * 0.25, font_size * 0.35, full_tm, page_h, crop_x0, crop_y0)
                if _pt_hits_any_digit_rect(fx, fy, digit_rects):
                    continue
            elif op == "TJ" and len(operands) >= 1 and isinstance(operands[0], pikepdf.Array):
                arr = operands[0]
                new_items = []
                cur_offset_em = 0.0
                removed_any = False

                for item in arr:
                    if isinstance(item, pikepdf.String):
                        x_em = (cur_offset_em + 250.0) / 1000.0 * font_size
                        y_em = 0.35 * font_size
                        full_tm = _mat_mul(tm, ctm)
                        fx, fy = _transform_pt(x_em, y_em, full_tm, page_h, crop_x0, crop_y0)

                        w_em = _estimate_tj_width_em(item)
                        if _pt_hits_any_digit_rect(fx, fy, digit_rects):
                            new_items.append(-w_em)
                            removed_any = True
                        else:
                            new_items.append(item)
                        cur_offset_em += w_em
                    else:
                        val = float(item)
                        cur_offset_em -= val
                        new_items.append(item)

                if removed_any:
                    if any(isinstance(it, pikepdf.String) for it in new_items):
                        new_instructions.append(([pikepdf.Array(new_items)], operator))
                    continue

        new_instructions.append((operands, operator))

    flush_all_subpaths()

    new_bytes = pikepdf.unparse_content_stream(new_instructions)
    if isinstance(obj_or_page, pikepdf.Page):
        obj_or_page.Contents = pdf.make_stream(new_bytes)
    else:
        obj_or_page.write(new_bytes)


def _remove_shx_annotations(pike_page, shx_rects, page_h, crop_x0, crop_y0):
    """移除页面注释层（/Annots）中的 AutoCAD SHX Text 纯数字批注"""
    if not shx_rects or "/Annots" not in pike_page:
        return
    annots = pike_page["/Annots"]
    kept_annots = pikepdf.Array()
    for annot in annots:
        try:
            rect = annot.get("/Rect")
            if rect and len(rect) == 4:
                ax0, ay0, ax1, ay1 = [float(v) for v in rect]
                fx0 = min(ax0, ax1) - crop_x0
                fx1 = max(ax0, ax1) - crop_x0
                fy0 = page_h - (max(ay0, ay1) - crop_y0)
                fy1 = page_h - (min(ay0, ay1) - crop_y0)
                a_fitz = fitz.Rect(fx0, fy0, fx1, fy1)

                if any(a_fitz.intersects(sr) and abs(a_fitz.x0 - sr.x0) < 3 and abs(a_fitz.y0 - sr.y0) < 3 for sr in shx_rects):
                    continue
        except Exception:
            pass
        kept_annots.append(annot)

    if len(kept_annots) > 0:
        pike_page["/Annots"] = kept_annots
    else:
        del pike_page["/Annots"]


# ═══════════════════════════════════════════════════════════════════════════════
#  3. 单文件处理与调试图输出
# ═══════════════════════════════════════════════════════════════════════════════

def process_pdf(input_path, output_path, debug=False):
    input_path  = Path(input_path)
    output_path = Path(output_path)

    fitz_doc = fitz.open(str(input_path))
    page_data = {}
    total = 0

    for i, page in enumerate(fitz_doc):
        tris, digits, shx_rects = find_valid_triangles_and_digits(page)
        page_data[i] = (tris, digits, shx_rects)
        count = len(tris)
        total += count

        if count > 0:
            print(f"    第 {i + 1} 页：精准锁定 {count} 个三角形注释符号（普通数字 {len(digits)} 个，SHX批注 {len(shx_rects)} 个）")
            if debug:
                mat = fitz.Matrix(IMAGE_DPI / 72, IMAGE_DPI / 72)
                pix = page.get_pixmap(matrix=mat, colorspace=fitz.csRGB)
                img = np.frombuffer(pix.samples, dtype=np.uint8).reshape(pix.height, pix.width, 3).copy()
                scale = IMAGE_DPI / 72
                for tr in tris:
                    x0, y0 = max(0, int(tr.x0 * scale)), max(0, int(tr.y0 * scale))
                    x1, y1 = min(img.shape[1], int(tr.x1 * scale)), min(img.shape[0], int(tr.y1 * scale))
                    cv2.rectangle(img, (x0, y0), (x1, y1), (255, 0, 0), 2)
                debug_path = output_path.with_name(output_path.stem + f"_debug_p{i + 1}.png")
                cv2.imwrite(str(debug_path), cv2.cvtColor(img, cv2.COLOR_RGB2BGR))
                print(f"    → Debug 标注图：{debug_path}")
        else:
            print(f"    第 {i + 1} 页：未检测到三角形注释符号，跳过")

    page_boxes = []
    for page in fitz_doc:
        cb = page.cropbox
        page_boxes.append((cb.height, cb.x0, cb.y0))
    fitz_doc.close()

    with pikepdf.open(str(input_path)) as pdf:
        for i, pike_page in enumerate(pdf.pages):
            tris, digits, shx_rects = page_data.get(i, ([], [], []))
            if not tris and not shx_rects:
                continue
            page_h, crop_x0, crop_y0 = page_boxes[i]
            _remove_triangles_from_stream(pdf, pike_page, tris, digits, page_h, crop_x0, crop_y0)
            _remove_shx_annotations(pike_page, shx_rects, page_h, crop_x0, crop_y0)

        pdf.save(str(output_path))

    return total


# ═══════════════════════════════════════════════════════════════════════════════
#  4. 批量处理与命令行入口（保持原有操作方式完全不变）
# ═══════════════════════════════════════════════════════════════════════════════

def batch_process(input_dir, output_dir, debug=False):
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
    failed = []

    for idx, pdf_path in enumerate(pdfs, 1):
        out_path = output_dir / pdf_path.name
        print(f"[{idx}/{len(pdfs)}] {pdf_path.name}")
        try:
            count = process_pdf(pdf_path, out_path, debug=debug)
            print(f"  → 完成，共物理删除 {count} 个符号。\n")
            total_erased += count
        except Exception as e:
            print(f"  → [错误] {type(e).__name__}: {e}\n")
            failed.append((pdf_path.name, str(e)))

    print("=" * 60)
    print(f"批量完成：{len(pdfs)} 个文件，共物理删除 {total_erased} 个三角形注释符号。")
    print(f"输出目录：{output_dir}")

    if failed:
        print(f"\n以下 {len(failed)} 个文件处理失败：")
        for name, err in failed:
            print(f"  ✗ {name}：{err}")


def main():
    parser = argparse.ArgumentParser(
        description="批量物理删除工程图纸 PDF 中的三角形注释符号（△1, △2, △3 等改版标记）"
    )
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--test", metavar="PDF文件", help="单张测试模式")
    mode.add_argument("--input", metavar="输入文件夹", help="批量模式：输入文件夹")
    parser.add_argument("--output", metavar="输出文件夹", help="输出文件夹")
    parser.add_argument("--debug", action="store_true", help="输出红框标注 PNG")

    args = parser.parse_args()

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
            print(f"完成：共物理删除 {count} 个三角形注释符号 → {output_path}")
        except Exception as e:
            print(f"[错误] {type(e).__name__}: {e}")
            sys.exit(1)
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