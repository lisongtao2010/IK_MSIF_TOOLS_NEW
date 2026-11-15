using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenCvSharp;

namespace CadPartSplitter
{
    /// <summary>
    /// 核心零件检测与分割器。
    /// Pipeline：
    ///   1. 灰度化 + 自适应反向二值化（线条 = 白色前景）
    ///   2. 擦除四周图框（贯穿页边的长横线 / 长竖线），避免图框把内部视图粘成一块
    ///   3. 遮蔽表格区域：优先按「文字层推断出的表格块」精确遮蔽，否则整列遮蔽（BOM / 标题栏）
    ///   4. 形态学闭运算 + 膨胀（聚合同一零件的碎散图元）
    ///   5. Tree 轮廓提取 + 几何筛选 + 重叠合并
    ///   6. 按 Y 坐标排序，裁切导出 PNG，同时记录像素与 PDF 坐标
    /// </summary>
    public class PartDetector
    {
        private readonly DetectorOptions _opts;

        /// <summary>本次运行实际采用的右侧遮蔽起始列（像素）。由 <see cref="MaskRight"/> / <see cref="MaskTableAreas"/> 写入。</summary>
        private int _maskStartX = -1;

        /// <summary>true = 本次采用「表格块」精确遮蔽；false = 整列遮蔽。</summary>
        private bool _maskByBlocks;

        /// <summary>本次擦除的图框横线行号（供导出时把同样的线从原图上擦掉）。</summary>
        private readonly List<int> _frameRows = new List<int>();

        /// <summary>本次擦除的图框竖线列号（供导出时把同样的线从原图上擦掉）。</summary>
        private readonly List<int> _frameCols = new List<int>();

        /// <summary>
        /// 表格列带起点（「从右向左的密实列带」左边界）。块遮蔽时用它剔除
        /// 「整块都落在表格带里」的表格残片（块遮蔽只涂黑检出的表格矩形，
        /// 表格之间的连接线 / 空白行残留仍可能连成伪零件）。
        /// -1 表示未计算。
        /// </summary>
        private int _tableBandStartX = -1;

        /// <summary>「视图聚合规则」日志只打一次（<see cref="DilateForGrouping"/> 可能被调用两轮）。</summary>
        private bool _loggedClusterRule;

        /// <summary>
        /// 本次运行实际采用的右侧遮蔽起始列（像素，-1 表示尚未计算）。
        /// 该列右侧即「表格 / 标题栏区」，文字记录模块据此把零件框以外的文字
        /// 分成「表格区」与「其它未归属」两类。
        /// 块遮蔽时取「中心落在右半页的表格块」的最小左边界；
        /// 整列遮蔽时取遮蔽线本身（自动检测或固定比例）。
        /// </summary>
        public int MaskStartX
        {
            get { return _maskStartX; }
        }

        public PartDetector(DetectorOptions opts)
        {
            _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        }

        /// <summary>
        /// 对输入图像执行完整的零件检测与分割，将结果写入输出目录。
        /// </summary>
        /// <param name="original">原始 BGR 图像（来自 PDF 渲染或直接读取），不会被修改。</param>
        /// <returns>检测并导出的零件信息列表（含像素坐标与 PDF 坐标）。</returns>
        public List<PartInfo> Detect(Mat original)
        {
            return Detect(original, null);
        }

        /// <summary>
        /// 对输入图像执行完整的零件检测与分割，将结果写入输出目录。
        /// </summary>
        /// <param name="original">原始 BGR 图像（来自 PDF 渲染或直接读取），不会被修改。</param>
        /// <param name="tableAreas">
        /// 由文字层推断出的表格 / 标题栏矩形（页面像素）。非空时只遮蔽这些矩形，
        /// 其余像素原样保留；为 null 或空则退回「右侧整列遮蔽」。
        /// </param>
        /// <returns>检测并导出的零件信息列表（含像素坐标与 PDF 坐标）。</returns>
        public List<PartInfo> Detect(Mat original, IList<PageRect> tableAreas)
        {
            return Detect(original, tableAreas, null);
        }

        /// <summary>
        /// 对输入图像执行完整的零件检测与分割，将结果写入输出目录。
        /// </summary>
        /// <param name="original">原始 BGR 图像（来自 PDF 渲染或直接读取），不会被修改。</param>
        /// <param name="tableAreas">
        /// 由文字层推断出的表格 / 标题栏矩形（页面像素）。非空时只遮蔽这些矩形，
        /// 其余像素原样保留；为 null 或空则退回「右侧整列遮蔽」。
        /// </param>
        /// <param name="textBoxes">
        /// 整页「文字行」外接框（页面像素）。非空且开启 <see cref="DetectorOptions.DropTextHeavyParts"/> 时，
        /// 会丢弃「文字行合计面积占自身外框 ≥ <see cref="DetectorOptions.TextHeavyRatio"/>」的块
        /// （表題欄 / 変更履歴表等表格残片，不是零件图）。传入 null 表示不筛除。
        /// </param>
        /// <returns>检测并导出的零件信息列表（含像素坐标与 PDF 坐标）。</returns>
        public List<PartInfo> Detect(Mat original, IList<PageRect> tableAreas, IList<PageRect> textBoxes)
        {
            if (original == null || original.Empty())
                throw new ArgumentException("输入图像为空。");

            Directory.CreateDirectory(_opts.OutputDirectory);

            int imgWidth  = original.Cols;
            int imgHeight = original.Rows;
            _opts.Log($"[INFO] 图像尺寸：{imgWidth} × {imgHeight} px");

            SaveDebug(original, "debug_00_original.png");

            using (Mat gray   = ToGray(original))
            using (Mat binary = AdaptiveBinarize(gray))
            using (Mat clean  = RemovePageFrameIfEnabled(binary))
            {
                SaveDebug(clean, "debug_01_binary.png");

                List<Rect> partRects = null;

                // 检测实际使用的二值图（图框已擦、表格已遮蔽）：矩形分割只出 1 块时，
                // 「不规则分割」要在这张图上把粘连的视图重新拆开（见 IrregularSplitter）。
                Mat maskedKeep = null;

                try
                {
                    // ── 方式 A：按文字层推断出的表格块精确遮蔽 ──────────────────
                    if (_opts.MaskTableBlocks && tableAreas != null && tableAreas.Count > 0)
                    {
                        // 顺便算出「表格列带」左边界（只为剔除块遮蔽漏掉的表格残片）
                        _tableBandStartX = DetectTableColumnStart(clean);

                        Mat masked = MaskTableAreas(clean, tableAreas);
                        SaveDebug(masked, "debug_02_masked.png");
                        partRects = DilateAndExtract(masked, imgWidth, imgHeight);

                        if (partRects.Count == 0)
                        {
                            _opts.Log("[WARN] 按表格块遮蔽后未检出零件，回退为整列遮蔽重试。");
                            masked.Dispose();
                            partRects     = null;
                            _maskStartX   = -1;
                            _maskByBlocks = false;
                        }
                        else
                        {
                            maskedKeep = masked;
                        }
                    }

                    // ── 方式 B：右侧整列遮蔽（也是方式 A 的兜底）──────────────────
                    if (partRects == null)
                    {
                        Mat masked = MaskRight(clean, _opts.RightMaskRatio);
                        SaveDebug(masked, "debug_02_masked.png");
                        partRects  = DilateAndExtract(masked, imgWidth, imgHeight);
                        maskedKeep = masked;
                    }

                    _opts.Log($"[INFO] 有效零件区域：{partRects.Count} 个");

                    // 表格残片（表題欄 / 変更履歴表）文字密、占比高：在导出 PNG 之前筛掉，
                    // 这样输出文件编号仍然连续，也不会留下「只有一张表」的伪零件图。
                    if (partRects.Count > 0 && _opts.DropTextHeavyParts && textBoxes != null && textBoxes.Count > 0)
                        partRects = DropTextHeavyRects(partRects, textBoxes);

                    // ── 兜底：矩形分割只出 1 块 → 不规则分割 ────────────────────
                    // 多个子视图被尺寸线 / 引线串成一个连通域时，外接矩形只有一个，
                    // 怎么调核都拆不开；改按「像素归属」拆（见 IrregularSplitter），
                    // 每块用多边形掩膜裁剪，矩形框重叠也不会把别的视图带进来。
                    if (_opts.IrregularSplitWhenSingle && partRects.Count == 1 && maskedKeep != null)
                    {
                        List<IrregularSplitter.Region> regions = IrregularSplitter.Split(maskedKeep, _opts);

                        if (regions.Count > 1 && _opts.DropTextHeavyParts
                            && textBoxes != null && textBoxes.Count > 0)
                        {
                            regions = DropTextHeavyRegions(regions, textBoxes);
                        }

                        if (regions.Count > 0)
                        {
                            _opts.Log($"[INFO] 矩形分割只得到 1 块 → 改用不规则分割：拆成 {regions.Count} 块");
                            using (Mat exportImage = ErasePageFrameOn(original))
                            {
                                return ExportIrregularParts(exportImage, regions, imgWidth, imgHeight);
                            }
                        }

                        _opts.Log("[INFO] 不规则分割没能拆开，保留矩形分割结果"
                                  + "（可调 IrregularSplitReach，或用 --no-irregular-split 撤回）");
                    }

                    if (partRects.Count == 0)
                    {
                        PrintNoResultHint();
                        return new List<PartInfo>();
                    }

                    // ── 大块再拆：矩形分割出了多块，但个别大块里可能还粘着多个视图 ──
                    // 正面 / 侧面 / 断面之间由尺寸线、引线串着，形态学膨胀后连成一块，
                    // 外接矩形只能「整块留下」。这里对整页再做一次「按像素归属」的拆分
                    // （与上面「矩形分割只出 1 块」的兜底同一套算法），
                    // 哪个矩形块里落进了 ≥2 个实体就换成这些实体的多边形掩膜，其余块保持矩形裁切。
                    List<IrregularSplitter.Region> refinedBlocks =
                        TryRefineBlocks(partRects, maskedKeep, imgWidth, imgHeight);

                    if (refinedBlocks != null)
                    {
                        using (Mat refinedExport = ErasePageFrameOn(original))
                        {
                            return ExportIrregularParts(refinedExport, refinedBlocks, imgWidth, imgHeight);
                        }
                    }

                    // 导出裁切用的是原始彩色图：把图框线一并擦掉。
                    // 否则输出图会带着图纸的左 / 上 / 下边框（右边框落在零件框外），
                    // 看上去就像「整页图纸被竖着切了一刀、右边的部分没了」。
                    using (Mat exportImage = ErasePageFrameOn(original))
                    {
                        return ExportParts(exportImage, partRects, imgWidth, imgHeight);
                    }
                }
                finally
                {
                    if (maskedKeep != null)
                        maskedKeep.Dispose();
                }
            }
        }

        /// <summary>膨胀聚合 → 轮廓提取 → 几何筛选 + 重叠合并。</summary>
        private List<Rect> DilateAndExtract(Mat masked, int imgWidth, int imgHeight)
        {
            using (Mat dilated = DilateForGrouping(masked))
            {
                SaveDebug(dilated, "debug_03_dilated.png");
                return ExtractPartRects(dilated, imgWidth, imgHeight);
            }
        }

        // ── 私有：各流水线步骤 ────────────────────────────────────────────────

        private static Mat ToGray(Mat src)
        {
            if (src.Channels() == 1)
                return src.Clone();

            Mat gray = new Mat();
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            return gray;
        }

        private Mat AdaptiveBinarize(Mat gray)
        {
            int blockSize = _opts.AdaptiveBlockSize;
            if (blockSize < 3)      blockSize = 3;
            if (blockSize % 2 == 0) blockSize++;

            Mat binary = new Mat();
            Cv2.AdaptiveThreshold(
                gray, binary,
                maxValue:       255,
                adaptiveMethod: AdaptiveThresholdTypes.GaussianC,
                thresholdType:  ThresholdTypes.BinaryInv,
                blockSize:      blockSize,
                c:              _opts.AdaptiveC);
            return binary;
        }

        /// <summary>
        /// 遮蔽右侧 BOM 表 / 标题栏区域。
        ///
        /// 优先使用「列墨迹」自动定位表格区左边界（<see cref="DetectorOptions.AutoMaskRightColumn"/>），
        /// 自动定位不可用或结果异常时退回 <paramref name="ratio"/> 固定比例。
        /// 实际采用的边界写入 <see cref="_maskStartX"/>，供轮廓筛选复用同一条边界。
        /// </summary>
        private Mat MaskRight(Mat binary, double ratio)
        {
            int maskStartX = DetermineMaskStartX(binary, ratio);
            _maskStartX    = maskStartX;
            _maskByBlocks  = false;

            Mat result = binary.Clone();
            if (maskStartX < result.Cols)
            {
                Rect maskRect = new Rect(maskStartX, 0, result.Cols - maskStartX, result.Rows);
                result[maskRect].SetTo(Scalar.Black);
            }
            return result;
        }

        /// <summary>
        /// 计算右侧遮蔽的起始列：自动检测优先，失败时用固定比例兜底。
        /// </summary>
        private int DetermineMaskStartX(Mat binary, double ratio)
        {
            int width = binary.Cols;
            int fallbackX = ClampColumn((int)Math.Round(width * (1.0 - ratio)), width);

            if (!_opts.AutoMaskRightColumn)
            {
                _opts.Log($"[INFO] 右侧遮蔽：固定比例 {ratio:P0} → X={fallbackX}（自动检测已关闭）");
                return fallbackX;
            }

            int autoX = DetectTableColumnStart(binary);
            if (autoX < 0)
            {
                _opts.Log($"[INFO] 右侧遮蔽：未检出表格/标题栏列带，改用固定比例 {ratio:P0} → X={fallbackX}");
                return fallbackX;
            }

            double maskedRatio = (width - autoX) / (double)width;
            if (maskedRatio < _opts.AutoMaskMinMaskedRatio || maskedRatio > _opts.AutoMaskMaxMaskedRatio)
            {
                _opts.Log($"[INFO] 右侧遮蔽：自动检出 X={autoX}（占宽 {maskedRatio:P0}）超出允许范围 "
                          + $"{_opts.AutoMaskMinMaskedRatio:P0}~{_opts.AutoMaskMaxMaskedRatio:P0}，"
                          + $"改用固定比例 {ratio:P0} → X={fallbackX}");
                return fallbackX;
            }

            _opts.Log($"[INFO] 右侧遮蔽：自动检出表格/标题栏区 X={autoX}（遮蔽右侧 {maskedRatio:P0}，"
                      + $"较固定比例 {ratio:P0} 保留更多图纸内容）");
            return autoX;
        }

        /// <summary>
        /// 检测右侧表格 / 标题栏区的起始列（像素 X）；未检出返回 -1。
        ///
        /// 原理：
        ///   ① 统计每一列的前景像素数（列墨迹）；
        ///   ② 从最右侧有墨迹的列开始向左延伸，只要该列墨迹 ≥ 图高 × AutoMaskColumnInkRatio 就继续；
        ///   ③ 延伸中断处即「密实文字带」（表格线 + 文字）的左边界。
        /// 图纸中的零件即使紧挨着表格，两者之间也必然存在至少一列几乎空白的列（引线/尺寸线不会整列贯穿），
        /// 因此该边界不会吃进零件区。
        /// </summary>
        private int DetectTableColumnStart(Mat binary)
        {
            int width  = binary.Cols;
            int height = binary.Rows;
            if (width < 16 || height < 16) return -1;

            int[] ink = new int[width];
            for (int x = 0; x < width; x++)
            {
                using (Mat column = binary.Col(x))
                    ink[x] = Cv2.CountNonZero(column);
            }

            int lastInk = width - 1;
            while (lastInk > 0 && ink[lastInk] == 0) lastInk--;
            if (lastInk <= 0) return -1;

            int threshold = (int)Math.Round(height * _opts.AutoMaskColumnInkRatio);
            if (threshold < 8) threshold = 8;

            int x2 = lastInk;
            while (x2 > 0 && ink[x2] >= threshold) x2--;

            int start = x2 + 1;
            if (start >= lastInk) return -1;   // 完全没有密实列

            return start;
        }

        private static int ClampColumn(int x, int width)
        {
            if (x < 0) return 0;
            if (x > width) return width;
            return x;
        }

        // ── 图框擦除 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 擦除图纸四周的图框（<see cref="DetectorOptions.RemovePageFrame"/>）。
        ///
        /// 判据：一整行（或一整列）有墨迹的像素数 ≥ 幅面 × <see cref="DetectorOptions.PageFrameSpanRatio"/>，
        /// 且该行 / 列位于页边 <see cref="DetectorOptions.PageFrameEdgeRatio"/> 带内。
        /// 擦除时上下（左右）各多擦 2px，避免抗锯齿残留把形态学重新连起来。
        /// </summary>
        private Mat RemovePageFrameIfEnabled(Mat binary)
        {
            Mat result = binary.Clone();
            _frameRows.Clear();
            _frameCols.Clear();

            if (!_opts.RemovePageFrame)
                return result;

            int w = result.Cols;
            int h = result.Rows;
            if (w < 64 || h < 64)
                return result;

            int spanW = (int)Math.Round(w * _opts.PageFrameSpanRatio);
            int spanH = (int)Math.Round(h * _opts.PageFrameSpanRatio);
            int edgeW = (int)Math.Round(w * _opts.PageFrameEdgeRatio);
            int edgeH = (int)Math.Round(h * _opts.PageFrameEdgeRatio);

            int rows = 0, cols = 0;

            // ① 顶部 / 底部的贯穿横线
            for (int y = 0; y < h; y++)
            {
                if (y >= edgeH && y < h - edgeH) continue;

                int ink;
                using (Mat row = result.Row(y))
                    ink = Cv2.CountNonZero(row);

                if (ink < spanW) continue;
                WipeBand(result, y - 2, y + 2, vertical: false);
                _frameRows.Add(y);
                rows++;
            }

            // ② 左侧 / 右侧的贯穿竖线
            for (int x = 0; x < w; x++)
            {
                if (x >= edgeW && x < w - edgeW) continue;

                int ink;
                using (Mat col = result.Col(x))
                    ink = Cv2.CountNonZero(col);

                if (ink < spanH) continue;
                WipeBand(result, x - 2, x + 2, vertical: true);
                _frameCols.Add(x);
                cols++;
            }

            if (rows > 0 || cols > 0)
                _opts.Log($"[INFO] 图框擦除：横线 {rows} 条 / 竖线 {cols} 条（避免图框把内部视图粘成一块）");

            return result;
        }

        /// <summary>
        /// 把已检出的图框线从「导出用图像」里一并擦掉（涂白）。
        ///
        /// 检测阶段擦的是二值图，导出裁切用的却是原始彩色图；若不擦掉，图框会跟着
        /// 零件一起被裁进输出图里，看起来就像「整页图纸被竖着切了一刀」——
        /// 左 / 上 / 下边框都在，右边框却没有（右边框落在零件框之外）。
        /// 擦除范围与检测阶段完全一致（±2px），不影响任何图元。
        /// </summary>
        private Mat ErasePageFrameOn(Mat image)
        {
            Mat result = image.Clone();
            if (!_opts.RemovePageFrame)
                return result;
            if (_frameRows.Count == 0 && _frameCols.Count == 0)
                return result;

            foreach (int y in _frameRows)
                WipeBandWhite(result, y - 2, y + 2, vertical: false);

            foreach (int x in _frameCols)
                WipeBandWhite(result, x - 2, x + 2, vertical: true);

            _opts.Log($"[INFO] 导出图已同步擦除图框：横线 {_frameRows.Count} 条 / 竖线 {_frameCols.Count} 条");

            return result;
        }

        /// <summary>把 [from, to] 号行（vertical=false）或列（vertical=true）涂白，越界自动裁剪。</summary>
        private static void WipeBandWhite(Mat img, int from, int to, bool vertical)
        {
            int max = (vertical ? img.Cols : img.Rows) - 1;
            int a = Math.Max(0, from);
            int b = Math.Min(max, to);
            if (b < a) return;

            if (vertical)
            {
                using (Mat band = img.ColRange(a, b + 1))
                    band.SetTo(Scalar.All(255));
            }
            else
            {
                using (Mat band = img.RowRange(a, b + 1))
                    band.SetTo(Scalar.All(255));
            }
        }


        /// <summary>把 [from, to] 号行（vertical=false）或列（vertical=true）涂黑，越界自动裁剪。</summary>
        private static void WipeBand(Mat img, int from, int to, bool vertical)
        {
            int max = (vertical ? img.Cols : img.Rows) - 1;
            int a = Math.Max(0, from);
            int b = Math.Min(max, to);
            if (b < a) return;

            if (vertical)
            {
                using (Mat band = img.ColRange(a, b + 1))   // ColRange(start, end)：end 不含
                    band.SetTo(Scalar.Black);
            }
            else
            {
                using (Mat band = img.RowRange(a, b + 1))
                    band.SetTo(Scalar.Black);
            }
        }

        // ── 表格块遮蔽 ────────────────────────────────────────────────────────

        /// <summary>
        /// 只涂黑文字层推断出的表格 / 标题栏矩形（外扩 <see cref="DetectorOptions.TableMaskPadding"/>）。
        ///
        /// 与整列遮蔽的区别：整列遮蔽假设「表格区与零件区在 X 方向可分」，一旦表格在右上、
        /// 零件视图在中部（X 投影重叠），密实列带会吃进零件区；块遮蔽只擦表格本身，
        /// 表格左右 / 下方的零件视图完整保留。
        ///
        /// <see cref="MaskStartX"/> 取「中心落在右半页的表格块」的最小左边界，
        /// 供文字模块把框外文字分成「表格区 / 其它」（左下角表題欄不参与该判定）。
        /// </summary>
        private Mat MaskTableAreas(Mat binary, IList<PageRect> areas)
        {
            Mat result = binary.Clone();
            int pad    = Math.Max(0, _opts.TableMaskPadding);
            int minX   = int.MaxValue;
            int masked = 0;

            foreach (var a in areas)
            {
                Rect r = IntersectPage(
                    new Rect(a.X - pad, a.Y - pad, a.W + pad * 2, a.H + pad * 2),
                    result.Cols, result.Rows);

                if (r.Width > 0 && r.Height > 0)
                {
                    result[r].SetTo(Scalar.Black);
                    masked++;
                }

                // 只有「右半页」的表格块用来确定文字分区起点（左下角表題欄不算）
                if (a.X + a.W / 2.0 >= result.Cols * 0.5)
                    minX = Math.Min(minX, a.X);
            }

            _maskStartX   = minX == int.MaxValue ? -1 : minX;
            _maskByBlocks = true;

            _opts.Log($"[INFO] 表格遮蔽：按块遮蔽 {masked} 块（保留表格区以外的零件视图），"
                      + $"表格区起点 X={_maskStartX}");

            return result;
        }

        /// <summary>把矩形裁剪到页面范围内；完全在页面外时返回空矩形。</summary>
        private static Rect IntersectPage(Rect r, int width, int height)
        {
            int left   = Math.Max(0, r.X);
            int top    = Math.Max(0, r.Y);
            int right  = Math.Min(width,  r.X + r.Width);
            int bottom = Math.Min(height, r.Y + r.Height);

            if (right <= left || bottom <= top)
                return new Rect(0, 0, 0, 0);

            return new Rect(left, top, right - left, bottom - top);
        }

        /// <summary>
        /// 把碎散图元聚合成「一个视图一块」的连通域，交给轮廓提取。
        ///
        /// 两种规则：
        ///   ① <b>毫米间距（默认）</b>：<see cref="DetectorOptions.ClusterGapMm"/> &gt; 0 时，
        ///      用「边长 ≈ 阈值」的圆形核做闭运算 + 膨胀 —— 等价于单链聚类：
        ///      两个黑点之间的间距 ≤ 阈值（默认 3 mm）就并进同一块。与 DPI 无关、横纵一致，
        ///      不会出现「同一个 3 mm 的缝，横向并得起来、纵向并不起来」。
        ///   ② <b>像素核（旧行为）</b>：阈值 ≤ 0 时用 <see cref="DetectorOptions.DilateKernelWidth"/> ×
        ///      <see cref="DetectorOptions.DilateKernelHeight"/> 的矩形核（默认 60 × 10、2 次迭代）。
        /// </summary>
        private Mat DilateForGrouping(Mat binary)
        {
            int radius = _opts.ClusterGapRadiusPx(_opts.RenderDpi);

            if (radius <= 0)
            {
                Size kernelSize = new Size(_opts.DilateKernelWidth, _opts.DilateKernelHeight);
                using (Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, kernelSize))
                {
                    Mat legacy = new Mat();
                    Cv2.MorphologyEx(binary, legacy, MorphTypes.Close, kernel,
                                     iterations: _opts.DilateIterations);
                    Cv2.Dilate(legacy, legacy, kernel, iterations: 1);
                    return legacy;
                }
            }

            // 边长 2r+1 的核：闭运算桥接距离 ≤ 2r 的两段墨迹（2r ≈ ClusterGapMm）
            int size = radius * 2 + 1;

            if (!_loggedClusterRule)
            {
                _loggedClusterRule = true;
                _opts.Log($"[INFO] 视图聚合规则：墨迹间距 ≤ {_opts.ClusterGapMm:0.##} mm 视为同一张零件图"
                          + $"（{_opts.RenderDpi:0} DPI ⇒ 核 {size}×{size} px，单链聚类）；"
                          + "想切得更碎调小 --cluster-gap-mm，想糊成一张调大");
            }

            using (Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(size, size)))
            {
                // 闭运算 = 先桥接 ≤2r 的缝，再腐蚀回去（图形本身不被撑大）
                Mat result = new Mat();
                Cv2.MorphologyEx(binary, result, MorphTypes.Close, kernel);

                // 再统一膨胀 r：让「本来就挨着」的碎块并成一个圆滑连通域，
                // 同时给裁切框留出 r 的余量（原像素核在这里也是膨胀，不是白留）。
                Cv2.Dilate(result, result, kernel, iterations: 1);

                return result;
            }
        }

        /// <summary>
        /// 提取轮廓（Tree 模式避免图纸外框吞噬内部零件），过滤后返回有效矩形列表。
        ///
        /// 过滤规则：
        ///   ① 轮廓面积 ≥ MinContourArea（去除孤立文字/噪点）
        ///   ② 外接框宽高 ≥ 最小宽高（去除细线）
        ///   ③ 外接框面积 / 全图面积 ≤ MaxBboxFillRatio（排除图纸外边框矩形）
        ///   ④ 外接框左边沿 &lt; 遮蔽边界（排除完全落在 BOM 区的轮廓，仅整列遮蔽时生效）
        ///      注意：用左边沿而非中心 X，允许跨越边界的宽零件被完整检测到；
        ///            边界取自 <see cref="MaskRight"/> 实际采用的 X（自动检测或固定比例）。
        ///            表格块遮蔽时表格像素已被涂黑，不再按左边界筛选，
        ///            否则会误删「X 落在表格区范围内、但位于表格下方 / 左侧」的零件视图。
        /// </summary>
        private List<Rect> ExtractPartRects(Mat dilated, int imgWidth, int imgHeight)
        {
            Point[][] contours;
            HierarchyIndex[] hierarchy;

            Cv2.FindContours(
                dilated, out contours, out hierarchy,
                RetrievalModes.Tree,
                ContourApproximationModes.ApproxSimple);

            // 与遮蔽方式保持一致：整列遮蔽用遮蔽线；表格块遮蔽时表格已涂黑，不再限制左边界
            int rightBoundary = _maskByBlocks
                ? imgWidth
                : (_maskStartX >= 0
                    ? _maskStartX
                    : (int)(imgWidth * (1.0 - _opts.RightMaskRatio)));
            double fullArea      = (double)imgWidth * imgHeight;

            var  candidates        = new List<Rect>();
            bool warnedTruncation  = false;
            int  fragmentCount     = 0;

            foreach (Point[] contour in contours)
            {
                double area = Cv2.ContourArea(contour);
                if (area < _opts.MinContourArea) continue;

                Rect bbox = Cv2.BoundingRect(contour);

                if (bbox.Width  < _opts.MinBoundingWidth)  continue;
                if (bbox.Height < _opts.MinBoundingHeight) continue;

                // 排除覆盖面积过大的图纸外框
                // 图框已在二值图阶段被擦除（RemovePageFrame），这里只需拦住「几乎整页」的异常块，
                // 否则会把「多个视图通过引线连成的大块」（例如竖排 4 个断面部约占 25%）误判为图框。
                double fillLimit = _opts.RemovePageFrame
                    ? Math.Max(_opts.MaxBboxFillRatio, 0.60)
                    : _opts.MaxBboxFillRatio;

                double bboxArea = bbox.Width * (double)bbox.Height;
                if (bboxArea / fullArea > fillLimit) continue;

                // 排除完全落在遮蔽区右侧的轮廓（用左边沿判断，允许跨边界的零件）
                if (bbox.Left >= rightBoundary) continue;

                // 块遮蔽模式：整块都落在「表格列带」里的候选，是块遮蔽漏掉的表格残片
                // （表格之间的连接线 / 空白行残留连成的块），直接丢弃。
                if (_maskByBlocks && _tableBandStartX > 0 && bbox.Left >= _tableBandStartX)
                {
                    fragmentCount++;
                    continue;
                }

                Rect paddedRect = AddPadding(bbox, _opts.Padding, imgWidth, imgHeight);
                candidates.Add(paddedRect);

                if (!_maskByBlocks && !warnedTruncation && bbox.Right + _opts.Padding >= rightBoundary)
                {
                    warnedTruncation = true;
                    _opts.Log($"[WARN] 有零件延伸至遮蔽边界 X={rightBoundary}，右侧可能被裁断；" +
                              "可减小「右侧遮蔽」比例后重试。");
                }
            }

            _opts.Log($"[INFO] 轮廓筛选后候选区域：{candidates.Count} 个（合并前）"
                      + (fragmentCount > 0 ? $"，另有 {fragmentCount} 个表格残片已丢弃" : string.Empty));

            return MergeOverlappingRects(candidates, _opts.MergeOverlapThreshold);
        }

        private static Rect AddPadding(Rect r, int padding, int imgW, int imgH)
        {
            int x      = Math.Max(0,    r.X      - padding);
            int y      = Math.Max(0,    r.Y      - padding);
            int right  = Math.Min(imgW, r.Right  + padding);
            int bottom = Math.Min(imgH, r.Bottom + padding);
            return new Rect(x, y, right - x, bottom - y);
        }

        private static List<Rect> MergeOverlappingRects(List<Rect> rects, double overlapThreshold)
        {
            if (rects.Count == 0) return rects;

            var merged = rects.OrderBy(r => r.Y).ThenBy(r => r.X).ToList();

            bool anyMerge = true;
            while (anyMerge)
            {
                anyMerge = false;
                for (int i = 0; i < merged.Count; i++)
                {
                    for (int j = i + 1; j < merged.Count; j++)
                    {
                        Rect inter = Rect.Intersect(merged[i], merged[j]);
                        if (inter.Width <= 0 || inter.Height <= 0) continue;

                        double interArea = inter.Width  * (double)inter.Height;
                        double areaI     = merged[i].Width * (double)merged[i].Height;
                        double areaJ     = merged[j].Width * (double)merged[j].Height;

                        if (interArea / Math.Min(areaI, areaJ) > overlapThreshold)
                        {
                            merged[i] = UnionRect(merged[i], merged[j]);
                            merged.RemoveAt(j);
                            j--;
                            anyMerge = true;
                        }
                    }
                }
            }

            return merged;
        }

        private static Rect UnionRect(Rect a, Rect b)
        {
            int x      = Math.Min(a.X,      b.X);
            int y      = Math.Min(a.Y,      b.Y);
            int right  = Math.Max(a.Right,  b.Right);
            int bottom = Math.Max(a.Bottom, b.Bottom);
            return new Rect(x, y, right - x, bottom - y);
        }

        /// <summary>
        /// 丢弃「文字行合计面积占比」过高的块 —— 它们不是要分割的零件图。
        ///
        /// 判据：块内所有文字行框与该块的<b>交集面积之和 ÷ 块面积</b> ≥
        /// <see cref="DetectorOptions.TextHeavyRatio"/>（默认 0.60）。
        /// 用交集而不是把整行框计入，跨在块边界上的行不会把自己整块算给这个块。
        ///
        /// 为什么能区分：零件图上的文字是尺寸数字、引线注记，一行只有几个字、散在各处，
        /// 行框加起来占不了多大面积；而表題欄 / 変更履歴表 / 部品表每一行都横跨整张表、
        /// 行行相邻，行框几乎铺满整块。实测（300 DPI，A2/A1 图纸）：
        /// 正常零件图 0.01~0.10，表格残片 0.55~0.85。
        ///
        /// 全部候选都被判为文字块时<b>保留原样并告警</b>：宁可多输出一张，
        /// 也不要让「整页都是文字说明」的图纸返回空结果。
        /// </summary>
        private List<Rect> DropTextHeavyRects(List<Rect> rects, IList<PageRect> textBoxes)
        {
            double threshold = _opts.TextHeavyRatio;
            if (threshold <= 0.0 || threshold > 1.0)
                threshold = 0.60;

            var kept  = new List<Rect>();
            var drops = new List<string>();

            foreach (var r in rects)
            {
                if (r.Width <= 0 || r.Height <= 0)
                    continue;

                double ratio = TextAreaRatio(r, textBoxes);

                // 调参用：--debug 时列出每个候选块的实测占比（决定 TextHeavyRatio 取多少）
                if (_opts.SaveDebugImages)
                    _opts.Log($"[RATIO] x={r.X} y={r.Y} w={r.Width} h={r.Height} 文字行占比 {ratio:0.000}");

                if (ratio >= threshold)
                {
                    drops.Add($"x={r.X} y={r.Y} w={r.Width} h={r.Height} 文字行占比 {ratio:0.00}");
                    continue;
                }

                kept.Add(r);
            }

            if (drops.Count == 0)
                return rects;

            if (kept.Count == 0)
            {
                _opts.Log($"[WARN] {drops.Count} 个候选块的文字行占比都 ≥ {threshold:0.00}，"
                          + "已全部保留（避免输出为空；可用 --text-ratio 调高阈值）。");
                return rects;
            }

            foreach (string d in drops)
                _opts.Log($"[DROP] 文字行占比过高，判定不是零件图：{d}");

            _opts.Log($"[INFO] 文字行占比筛除：丢弃 {drops.Count} 个 / 保留 {kept.Count} 个（阈值 {threshold:0.00}）");
            return kept;
        }

        /// <summary>
        /// 同 <see cref="DropTextHeavyRects"/>，用于不规则分割拆出来的块（按块的外接框判）。
        ///
        /// 不规则分割是为了拆「多个视图粘成一块」，但它也可能把表題欄这类文字表格拆成几块，
        /// 所以拆完照样过一遍文字占比，只留下真正的视图。
        /// </summary>
        private List<IrregularSplitter.Region> DropTextHeavyRegions(
            List<IrregularSplitter.Region> regions, IList<PageRect> textBoxes)
        {
            double threshold = _opts.TextHeavyRatio;
            if (threshold <= 0.0 || threshold > 1.0)
                threshold = 0.60;

            var kept  = new List<IrregularSplitter.Region>();
            var drops = new List<string>();

            foreach (var region in regions)
            {
                Rect r = region.Bounds;
                if (r.Width <= 0 || r.Height <= 0)
                    continue;

                double ratio = TextAreaRatio(r, textBoxes);

                if (_opts.SaveDebugImages)
                    _opts.Log($"[RATIO] x={r.X} y={r.Y} w={r.Width} h={r.Height} 文字行占比 {ratio:0.000}（不规则块）");

                if (ratio >= threshold)
                {
                    drops.Add($"x={r.X} y={r.Y} w={r.Width} h={r.Height} 文字行占比 {ratio:0.00}");
                    continue;
                }

                kept.Add(region);
            }

            if (drops.Count == 0)
                return regions;

            if (kept.Count == 0)
            {
                _opts.Log($"[WARN] 不规则分割出的 {drops.Count} 块文字行占比都 ≥ {threshold:0.00}，"
                          + "已全部保留（避免输出为空；可用 --text-ratio 调高阈值）。");
                return regions;
            }

            foreach (string d in drops)
                _opts.Log($"[DROP] 文字行占比过高，判定不是零件图：{d}");

            _opts.Log($"[INFO] 不规则分割 + 文字行占比筛除：丢弃 {drops.Count} 块 / 保留 {kept.Count} 块"
                      + $"（阈值 {threshold:0.00}）");
            return kept;
        }

        /// <summary>
        /// 块内「文字行框与块的交集面积之和」占块面积的比例。
        /// 用交集而不是把整行框计入：跨在块边界上的行不会把自己整块算给这个块。
        /// </summary>
        private static double TextAreaRatio(Rect r, IList<PageRect> textBoxes)
        {
            double area = r.Width * (double)r.Height;
            if (area <= 0.0)
                return 0.0;

            double textArea = 0.0;
            foreach (var t in textBoxes)
            {
                int x0 = Math.Max(r.Left,   t.X);
                int y0 = Math.Max(r.Top,    t.Y);
                int x1 = Math.Min(r.Right,  t.Right);
                int y1 = Math.Min(r.Bottom, t.Bottom);
                if (x1 <= x0 || y1 <= y0) continue;

                textArea += (x1 - x0) * (double)(y1 - y0);
            }

            if (textArea > area) textArea = area;   // 词框互相重叠时封顶
            return textArea / area;
        }

        /// <summary>
        /// 按 Y 坐标排序，裁切每个零件并保存 PNG；同时计算 PDF 坐标（mm）。
        /// PDF 坐标以页面左上角为原点，与图像坐标轴方向一致。
        /// </summary>
        private List<PartInfo> ExportParts(Mat original, List<Rect> partRects,
                                           int imgWidth, int imgHeight)
        {
            double mmPerPx = 25.4 / _opts.RenderDpi;
            var result     = new List<PartInfo>();
            var sorted     = partRects.OrderBy(r => r.Y).ToList();
            var failed     = new List<string>();

            _opts.Log($"[INFO] 导出零件图：共 {sorted.Count} 块（按 Y 从上到下编号；"
                      + "保存失败的那张会跳过并顺延编号，不会留空号）");

            for (int i = 0; i < sorted.Count; i++)
            {
                Rect roi = sorted[i];

                // 编号按「已经成功的张数」走，而不是循环下标：
                // 以前用 i + 1，某一张保存失败（文件被占用 / 只读）时后面整排照旧编号，
                // 产物里就凭空出现「没有 Part_01.png / Part_02.png，从 Part_03.png 开始」的空号。
                string name = $"Part_{result.Count + 1:D2}.png";
                string path = Path.Combine(_opts.OutputDirectory, name);

                if (_opts.SaveDebugImages)
                    _opts.Log($"[RECT] {name} <- x={roi.X} y={roi.Y} w={roi.Width} h={roi.Height}");

                using (Mat crop = new Mat(original, roi))
                {
                    if (!SavePartPng(crop, path))
                    {
                        failed.Add(name);
                        continue;
                    }
                }

                _opts.Log($"[SAVE] {name}  ({roi.Width}×{roi.Height} px  Y={roi.Y})");

                result.Add(new PartInfo
                {
                    Index    = result.Count + 1,
                    FileName = name,
                    PixelX   = roi.X,
                    PixelY   = roi.Y,
                    PixelW   = roi.Width,
                    PixelH   = roi.Height,
                    PdfX_mm  = roi.X      * mmPerPx,
                    PdfY_mm  = roi.Y      * mmPerPx,
                    PdfW_mm  = roi.Width  * mmPerPx,
                    PdfH_mm  = roi.Height * mmPerPx
                });
            }

            ReportSaveFailures(failed);
            CleanupStalePartImages(result.Count);

            return result;
        }

        /// <summary>
        /// 「大块再拆」：矩形分割已经出了多块，但个别大块里可能还粘着多个视图
        /// （正面 / 侧面 / 断面之间由尺寸线、引线串着，形态学膨胀后连成一块，外接矩形只能整块留下）。
        ///
        /// 做法与「矩形分割只出 1 块」时的兜底完全相同，只是作用范围从「整页唯一一块」
        /// 变成「每个够大的块」：
        ///   ① 对整页（已擦图框、已遮蔽表格的）二值图做一次 <see cref="IrregularSplitter.Split"/>；
        ///   ② 把拆出来的实体按「外接框中心落在哪个矩形块里」归到各块；
        ///   ③ 某块里落进 ≥2 个实体、且每个实体自己够得上一个零件
        ///      （<see cref="IrregularSplitter.Region.InkArea"/> ≥ <see cref="DetectorOptions.MinContourArea"/>）时，
        ///      用这些实体替换该块；否则保留原矩形块（整框导出）。
        ///
        /// 门槛为什么不放开：闭轮廓填充会把「视图的外框」本身也算成一个实体，
        /// 门槛太低会把一个视图拆成「外框 + 内部图形」两块。
        /// 只要有一个块被拆开就返回完整块列表（矩形块用 <c>Polygon = null</c> 标记）；
        /// 一个都没拆开时返回 null，调用方走原来的矩形导出，产物与改动前完全一致。
        /// </summary>
        /// <param name="partRects">矩形分割的结果（已过筛除 / 合并）。</param>
        /// <param name="maskedKeep">检测用的二值图（图框已擦、表格已遮蔽）；为 null 时不做。</param>
        private List<IrregularSplitter.Region> TryRefineBlocks(List<Rect> partRects, Mat maskedKeep,
                                                               int imgWidth, int imgHeight)
        {
            if (!_opts.IrregularSplitLargeBlocks || maskedKeep == null || partRects == null)
                return null;

            double pageArea = imgWidth * (double)imgHeight;
            if (pageArea <= 0.0)
                return null;

            bool hasCandidate = false;
            foreach (Rect rect in partRects)
            {
                double ratio = rect.Width * (double)rect.Height / pageArea;
                if (ratio >= _opts.IrregularSplitLargeAreaRatio)
                {
                    hasCandidate = true;
                    break;
                }
            }

            if (!hasCandidate)
                return null;

            // 整页拆一次：坐标系与 partRects 完全一致（都是页面像素）
            List<IrregularSplitter.Region> regions = IrregularSplitter.Split(maskedKeep, _opts);
            if (regions.Count < 2)
                return null;

            var blocks = new List<IrregularSplitter.Region>();
            var used   = new bool[regions.Count];
            var inside = new List<int>();
            int refinedBlocks = 0;

            foreach (Rect rect in partRects.OrderBy(r => r.Y).ThenBy(r => r.X))
            {
                double ratio    = rect.Width * (double)rect.Height / pageArea;
                bool isCandidate = ratio >= _opts.IrregularSplitLargeAreaRatio;

                if (isCandidate)
                {
                    inside.Clear();

                    for (int i = 0; i < regions.Count; i++)
                    {
                        if (used[i])
                            continue;

                        // 实体自己够不上一个零件 → 不算「一个视图」（多半是视图外框 / 局部符号）
                        if (regions[i].InkArea < _opts.MinContourArea)
                            continue;

                        Rect b = regions[i].Bounds;
                        int cx = b.X + b.Width / 2;
                        int cy = b.Y + b.Height / 2;
                        if (cx < rect.X || cx >= rect.X + rect.Width)
                            continue;
                        if (cy < rect.Y || cy >= rect.Y + rect.Height)
                            continue;

                        inside.Add(i);
                    }
                }

                if (isCandidate && inside.Count >= 2)
                {
                    foreach (int i in inside)
                    {
                        used[i] = true;
                        blocks.Add(regions[i]);
                    }

                    refinedBlocks++;
                    continue;
                }

                // 没拆开（或本来就不是大块）：保留矩形块，导出时整框裁切
                blocks.Add(new IrregularSplitter.Region { Bounds = rect, Polygon = null });
            }

            if (refinedBlocks == 0)
                return null;

            _opts.Log($"[INFO] 大块再拆：{refinedBlocks} 个块里粘着多个视图 → 按像素归属拆开"
                      + $"（{partRects.Count} 块 → {blocks.Count} 块，其余块保持矩形裁切）");

            return blocks;
        }

        /// <summary>
        /// 导出「不规则分割」得到的块：矩形裁切框 + 多边形掩膜，掩膜外的像素涂白。
        ///
        /// 与 <see cref="ExportParts"/> 的区别只有一个 —— 裁切后不是整块矩形都留下，而是只留
        /// 该块多边形内的像素。相邻视图的矩形框即使重叠，彼此的线框也不会串进来
        /// （这正是矩形分割拆不开时仍然能分开的原因）。
        ///
        /// 文件名、编号规则、<see cref="PartInfo"/> 里的坐标口径与矩形分割完全一致
        /// （坐标仍记外接框，下游按矩形读图不会错位；掩膜外的空白是白底）。
        ///
        /// 「大块再拆」（<see cref="TryRefineBlocks"/>）用的是同一张列表：被拆细的块带多边形，
        /// 没被拆细的块 <c>Polygon == null</c> → 整框导出（与 <see cref="ExportParts"/> 一致）。
        /// </summary>
        private List<PartInfo> ExportIrregularParts(Mat original, List<IrregularSplitter.Region> regions,
                                                   int imgWidth, int imgHeight)
        {
            double mmPerPx = 25.4 / _opts.RenderDpi;
            var result     = new List<PartInfo>();
            var sorted     = regions.OrderBy(r => r.Bounds.Y).ThenBy(r => r.Bounds.X).ToList();
            var failed     = new List<string>();

            for (int i = 0; i < sorted.Count; i++)
            {
                IrregularSplitter.Region region = sorted[i];
                Rect roi = region.Bounds;

                // 与 ExportParts 一致：编号按「已经成功的张数」走，保存失败不留空号
                string name = $"Part_{result.Count + 1:D2}.png";
                string path = Path.Combine(_opts.OutputDirectory, name);

                bool hasPolygon = region.Polygon != null && region.Polygon.Length >= 3;

                if (_opts.SaveDebugImages)
                    _opts.Log($"[RECT] {name} <- x={roi.X} y={roi.Y} w={roi.Width} h={roi.Height}"
                              + (hasPolygon ? $"  不规则掩膜 {region.Polygon.Length} 点" : string.Empty));

                using (Mat crop   = new Mat(original, roi))
                using (Mat output = new Mat(roi.Height, roi.Width, original.Type(), Scalar.All(255)))
                {
                    if (hasPolygon)
                    {
                        using (Mat mask = IrregularSplitter.BuildMask(region.Polygon, roi))
                            crop.CopyTo(output, mask);
                    }
                    else
                    {
                        // Polygon == null：这块没被拆细（「大块再拆」里的矩形块），整框导出
                        crop.CopyTo(output);
                    }

                    if (!SavePartPng(output, path))
                    {
                        failed.Add(name);
                        continue;
                    }
                }

                _opts.Log(hasPolygon
                    ? $"[SAVE] {name}  ({roi.Width}×{roi.Height} px  Y={roi.Y}  不规则掩膜 {region.Polygon.Length} 点)"
                    : $"[SAVE] {name}  ({roi.Width}×{roi.Height} px  Y={roi.Y})");

                result.Add(new PartInfo
                {
                    Index    = result.Count + 1,
                    FileName = name,
                    PixelX   = roi.X,
                    PixelY   = roi.Y,
                    PixelW   = roi.Width,
                    PixelH   = roi.Height,
                    PdfX_mm  = roi.X      * mmPerPx,
                    PdfY_mm  = roi.Y      * mmPerPx,
                    PdfW_mm  = roi.Width  * mmPerPx,
                    PdfH_mm  = roi.Height * mmPerPx
                });
            }

            ReportSaveFailures(failed);
            CleanupStalePartImages(result.Count);

            return result;
        }

        /// <summary>
        /// 把裁好的零件图写成 PNG，失败时按「清只读属性 → 写临时文件再覆盖」重试两次，并把原因写进日志。
        ///
        /// 为什么要这么绕：以前只判 <see cref="Mat.SaveImage(string)"/> 的返回值、失败就直接
        /// <c>continue</c>，于是产物里会<b>凭空少一张图</b>（典型症状：输出目录里没有
        /// <c>Part_01.png</c> / <c>Part_02.png</c>，编号从 <c>Part_03.png</c> 才开始），
        /// 日志里只有一行「保存失败」看不出原因。最常见的原因是<b>同名文件正被其它程序打开</b>
        /// （看图软件 / Excel / 资源管理器缩略图预览），其次是文件带只读属性（从 ZIP 解压、网盘同步的目录常见）。
        /// </summary>
        /// <param name="image">已裁好（不规则分割时已按掩膜涂白）的零件图。</param>
        /// <param name="path">目标 PNG 路径。</param>
        /// <returns>true = 已落盘；false = 三次都失败（调用方顺延编号，不留空号）。</returns>
        private bool SavePartPng(Mat image, string path)
        {
            if (image == null || image.Empty())
            {
                _opts.Log($"[WARN] 保存失败（图像为空）：{path}");
                return false;
            }

            if (image.SaveImage(path))
                return true;

            string reason = DescribeSaveFailure(path);

            // 只读属性已在上面的 DescribeSaveFailure 里清掉，这里再试一次
            if (image.SaveImage(path))
            {
                _opts.Log($"[WARN] 保存重试成功：{Path.GetFileName(path)}（{reason}）");
                return true;
            }

            // 最后的机会：先写临时文件，再覆盖目标（目标文件被别的进程占着写入句柄时也能成功）
            string tempPath = path + ".tmp.png";
            try
            {
                if (image.SaveImage(tempPath))
                {
                    File.Copy(tempPath, path, true);
                    _opts.Log($"[WARN] 保存重试成功：{Path.GetFileName(path)}（{reason}；改用临时文件覆盖）");
                    return true;
                }
            }
            catch (Exception ex)
            {
                reason += "；临时文件覆盖也失败 - " + ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // 临时文件删不掉不影响结果
                }
                catch (UnauthorizedAccessException)
                {
                    // 同上
                }
            }

            _opts.Log($"[WARN] 保存失败：{path} —— {reason}。"
                      + "关掉占用该文件的程序（看图软件 / Excel / 缩略图预览）或去掉只读属性后重跑即可；"
                      + "本次这张图跳过，后面的编号自动顺延，不会留下空号。");
            return false;
        }

        /// <summary>推测 PNG 保存失败的原因；顺手清掉「只读」属性（下次重试就能写进去）。</summary>
        private static string DescribeSaveFailure(string path)
        {
            try
            {
                var info = new FileInfo(path);

                if (info.Exists && (info.Attributes & FileAttributes.ReadOnly) != 0)
                {
                    info.IsReadOnly = false;
                    return "同名文件是只读的（已清掉只读属性后重试）";
                }

                if (info.Exists)
                    return "同名文件可能正被其它程序打开（看图软件 / Excel / 资源管理器缩略图预览）";

                string dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    return "输出目录不存在";

                return "输出目录可能不可写（权限 / 磁盘已满 / 网络盘掉线）";
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// <summary>导出结束后把「有几张没落盘」汇总成一行，避免只有一行「保存失败」被后面的 LOG 淹掉。</summary>
        private void ReportSaveFailures(List<string> failed)
        {
            if (failed == null || failed.Count == 0)
                return;

            _opts.Log($"[WARN] 有 {failed.Count} 张零件图没能落盘：{string.Join("、", failed.ToArray())}"
                      + "（原因见上面的「保存失败」一行；关掉占用文件的程序后重跑即可）");
        }

        /// <summary>
        /// 删掉上一次运行遗留、本次没有对应零件图的多余 <c>Part_NN.png</c>。
        ///
        /// 例：上次拆出 12 张、这次只导出 10 张 → <c>Part_11.png</c> / <c>Part_12.png</c> 是上次的旧图，
        /// 留着会让下游按文件名取到过期内容（看起来就像「这次的图跟上次不一样」）。
        /// 只在本次至少导出一张时才清理，且只认「<c>Part_</c> + 纯数字」的文件名，
        /// <c>Part_01_callouts.png</c> 这类核对图不会被误删。
        /// </summary>
        private void CleanupStalePartImages(int keptCount)
        {
            if (keptCount <= 0)
                return;

            string[] files;
            try
            {
                files = Directory.GetFiles(_opts.OutputDirectory, "Part_*.png");
            }
            catch (Exception ex)
            {
                _opts.Log("[WARN] 清理旧零件图失败：" + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            var removed = new List<string>();

            foreach (string file in files)
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                if (stem == null || !stem.StartsWith("Part_", StringComparison.Ordinal))
                    continue;

                // 只认 Part_01 / Part_012 这种「Part_ + 纯数字」，Part_01_callouts.png 之类跳过
                string digits = stem.Substring(5);
                if (digits.Length < 2)
                    continue;

                int no;
                if (!int.TryParse(digits, out no))
                    continue;

                if (no <= keptCount)
                    continue;

                try
                {
                    File.Delete(file);
                    removed.Add(stem + ".png");
                }
                catch (IOException)
                {
                    _opts.Log($"[WARN] 旧零件图删不掉（可能正被占用）：{stem}.png");
                }
                catch (UnauthorizedAccessException)
                {
                    _opts.Log($"[WARN] 旧零件图删不掉（无权限）：{stem}.png");
                }
            }

            if (removed.Count > 0)
                _opts.Log($"[INFO] 清理上次残留的零件图：{string.Join("、", removed.ToArray())}"
                          + $"（本次只导出 {keptCount} 张）");
        }

        private void SaveDebug(Mat img, string filename)
        {
            if (!_opts.SaveDebugImages) return;
            img.SaveImage(Path.Combine(_opts.OutputDirectory, filename));
        }

        private void PrintNoResultHint()
        {
            _opts.Log("[WARN] 未检测到任何有效零件。请逐项检查：");
            _opts.Log("       ① 保存调试图后查看 debug_02_masked.png：右侧表格区遮蔽线是否盖住了零件" +
                      "（关闭「自动遮蔽」并减小 RightMaskRatio 到 0.40 试试）");
            _opts.Log("       ② MinContourArea 是否设置过高（尝试降至 10000）");
            _opts.Log("       ③ 膨胀核是否过大导致整页粘连成一块（减小 DilateKernelWidth/Height）");
            _opts.Log("       ④ 开启 SaveDebugImages=true 观察中间处理结果");
        }
    }
}
