using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace CadPartSplitter
{
    /// <summary>
    /// 「不规则分割」兜底：矩形分割只得到一块时，把被尺寸线 / 引线串成一块的多个子视图重新拆开。
    ///
    /// <para>
    /// 为什么矩形分割拆不开：一张組立図上往往排着正面 / 侧面 / 断面 / 部分詳細等好几个视图，
    /// 它们之间由尺寸线、引出线、中心线连着（或者形态学膨胀把相邻视图连成一排）。
    /// 连通域只有一个 ⇒ 外接矩形也只有一个，「全糊成一张」与「把零件切碎」之间没有中间档，
    /// 怎么调 <see cref="DetectorOptions.DilateKernelWidth"/> 都二选一。
    /// </para>
    ///
    /// <para>
    /// 做法：不按矩形切，而按「像素归属」切 ——
    ///   ① <b>填实闭合轮廓</b>：CAD 视图的外轮廓是闭合的线，用 <c>DrawContours(thickness: -1)</c>
    ///      填充后每个视图变成一块实心图形；尺寸线 / 引线不闭合，填不出面积 ⇒ 实体之间天然分开。
    ///      包住整页的大框（分组框 / 图框残片，面积 &gt; 40% 页）不填，否则一填就把所有视图吞成一块。
    ///   ② <b>开运算剥细桥</b>：实体之间若还残留 1~3 px 的细桥，3×3 开运算即可断开；不够就逐级
    ///      加大到 17×17。开运算只影响「定位用的种子」的大小，最终掩膜仍取自原二值图，不会侵蚀图形。
    ///   ②½ <b>按间距归并</b>：一个视图被尺寸线切成两三个闭合实体时，碎片之间的间距也在
    ///      <see cref="DetectorOptions.ClusterGapMm"/>（默认 3 mm）以内 —— 按<b>单链聚类</b>并回一块，
    ///      否则一个视图会被导成好几张 <c>Part_NN.png</c>（见 <c>MergeNearBodies</c>）。
    ///   ③ <b>外扩归属</b>：每个实体向四周外扩 <see cref="DetectorOptions.IrregularSplitReach"/> px，
    ///      范围内的线框 / 标注算它的；块与块重叠的区域按面积大的先占（占用表逐块累加）。
    ///   ④ <b>多边形掩膜</b>：每块取邻域内墨迹的最大外轮廓，<c>ApproxPolyDP</c> 简化成多边形；
    ///      导出时按多边形裁剪、掩膜外涂白 —— 矩形框互相重叠也不会把别的视图一起带进来。
    /// </para>
    ///
    /// <para>
    /// 两个入口：
    ///   ① 矩形分割<b>恰好只出 1 块</b>时兜底（<see cref="DetectorOptions.IrregularSplitWhenSingle"/>）；
    ///   ② 「大块再拆」：矩形分割出了多块时，对<b>够大的块</b>（外接框面积占页 ≥
    ///      <see cref="DetectorOptions.IrregularSplitLargeAreaRatio"/>）再拆一次，块里落进 ≥2 个实体就替换
    ///      （<see cref="DetectorOptions.IrregularSplitLargeBlocks"/>，见 <c>PartDetector.TryRefineBlocks</c>）。
    /// 拆不出来就原样保留矩形结果，不会让正常图纸的产物变样。
    /// </para>
    /// </summary>
    internal static class IrregularSplitter
    {
        /// <summary>开运算核的候选尺寸：从小到大试，能拆成 ≥2 块实体就停（只影响定位种子）。</summary>
        private static readonly int[] KernelCandidates = { 3, 5, 7, 9, 13, 17 };

        /// <summary>包住整页的大闭合框（分组框 / 图框残片）不参与填充：面积占页超过此比例就跳过。</summary>
        private const double GroupFrameAreaRatio = 0.40;

        /// <summary>多边形简化的容差（像素）：300 DPI 下 2 px 约 0.17 mm，肉眼不可见。</summary>
        private const double PolygonEpsilon = 2.0;

        /// <summary>
        /// 填实前先做一次闭运算的最小核尺寸（像素）：CAD 视图的外轮廓常因「断口 / 被尺寸线打断」
        /// 缺几个像素而闭合不成环，5×5 闭运算能把 ≤4 px 的断口接上，让视图变成可填充的闭合轮廓。
        /// 开启毫米聚合规则（<see cref="DetectorOptions.ClusterGapMm"/> &gt; 0）时，
        /// 实际核尺寸放大到「聚合间距」（默认 3 mm ⇒ 37 px），本值只作下限。
        /// </summary>
        private const int CloseGapPx = 5;

        /// <summary>掩膜向外放的像素：轮廓线本身有 1~2 px 宽，不放大会把图形自己的边线裁掉。</summary>
        private const int MaskDilatePx = 2;

        /// <summary>
        /// 不规则分割得到的一块（一个子视图）。
        /// 导出时按 <see cref="Polygon"/> 生成掩膜裁剪，掩膜外的像素涂白。
        /// </summary>
        public class Region
        {
            /// <summary>裁切框（页面像素，已含 <see cref="DetectorOptions.Padding"/> 边距，限制在页面内）。</summary>
            public Rect Bounds { get; set; }

            /// <summary>该块的外轮廓多边形（页面像素坐标，已简化）。</summary>
            public Point[] Polygon { get; set; }

            /// <summary>邻域内的墨迹面积（平方像素），用于日志与过滤。</summary>
            public double InkArea { get; set; }
        }

        /// <summary>
        /// 把一张「已经擦掉图框、遮蔽掉表格」的二值图拆成多个不规则区域。
        /// 拆不开（仍只有 1 块实体）时返回空列表，调用方保留矩形分割结果。
        /// </summary>
        /// <param name="binary">二值图（前景 = 255）：与 <see cref="PartDetector.Detect"/> 检测用的是同一张。</param>
        /// <param name="opts">识别参数（面积 / 边距 / 外扩 / 日志回调）。</param>
        public static List<Region> Split(Mat binary, DetectorOptions opts)
        {
            var regions = new List<Region>();
            if (binary == null || binary.Empty() || opts == null)
                return regions;

            int width  = binary.Cols;
            int height = binary.Rows;
            if (width < 64 || height < 64)
                return regions;

            int closedCount;
            using (Mat filled = BuildFilled(binary, opts, out closedCount))
            {
                if (closedCount == 0)
                {
                    opts.Log("[INFO] 不规则分割：图上找不到能填实的闭合轮廓，跳过");
                    return regions;
                }

                // ① + ② 逐级加大开运算，直到能拆出 ≥2 块实体
                List<Point[]> bodies = null;
                int usedKernel = 0;

                foreach (int k in KernelCandidates)
                {
                    usedKernel = k;
                    bodies = FindBodies(filled, k, opts);
                    if (bodies.Count >= 2)
                        break;
                }

                if (bodies == null || bodies.Count == 0)
                {
                    opts.Log("[INFO] 不规则分割：填充实心后没有合格实体，跳过");
                    return regions;
                }

                // ②½ 按「墨迹间距」把同一个视图的碎片归并回一块（单链聚类）：
                //     一个视图被尺寸线 / 中心线切成两三个闭合实体时，碎片之间的间距同样在
                //     ClusterGapMm 以内；不归并就会把一个视图导成好几张 Part_NN.png。
                int gapRadius = opts.ClusterGapRadiusPx(opts.RenderDpi);
                if (gapRadius > 0 && bodies.Count >= 2)
                {
                    int before = bodies.Count;
                    bodies = MergeNearBodies(bodies, gapRadius, width, height);

                    if (bodies.Count != before)
                    {
                        opts.Log($"[INFO] 不规则分割：按间距 ≤ {opts.ClusterGapMm:0.##} mm 归并实体碎片"
                                  + $" {before} → {bodies.Count} 个");
                    }
                }

                if (bodies.Count < 2)
                {
                    opts.Log($"[INFO] 不规则分割：填充 + 开运算 {usedKernel}×{usedKernel} 后仍只有 1 块实体，"
                              + "拆不开（保留矩形结果）");
                    return regions;
                }

                // ③ 大块先挑地：小块外扩时会避开已经被大块占走的部分
                bodies.Sort((a, b) => Cv2.ContourArea(b).CompareTo(Cv2.ContourArea(a)));

                opts.Log($"[INFO] 不规则分割：闭合实体 {closedCount} 个 → 开运算 {usedKernel}×{usedKernel} 后"
                          + $"剩 {bodies.Count} 个视图实体");

                int reach = Math.Max(10, opts.IrregularSplitReach);

                using (Mat claimed = new Mat(height, width, MatType.CV_8UC1, Scalar.Black))
                {
                    foreach (Point[] body in bodies)
                    {
                        Region region = BuildRegion(binary, claimed, body, reach, opts);
                        if (region != null)
                            regions.Add(region);
                    }
                }

                if (regions.Count > 1)
                {
                    regions.Sort((a, b) =>
                    {
                        int c = a.Bounds.Y.CompareTo(b.Bounds.Y);
                        return c != 0 ? c : a.Bounds.X.CompareTo(b.Bounds.X);
                    });

                    opts.Log($"[INFO] 不规则分割：拆出 {regions.Count} 块（各按自己的多边形掩膜裁剪）");
                }
            }

            return regions;
        }

        /// <summary>
        /// 把「闭合轮廓」填实：CAD 视图的外轮廓闭合成环，<c>DrawContours</c> 填充后整块视图变成实心；
        /// 尺寸线 / 引线 / 中心线不闭合，填不出面积 —— 于是「视图实体」之间本来就是分开的。
        /// </summary>
        /// <param name="closedCount">实际被填实的轮廓数（= 候选视图实体数）。</param>
        private static Mat BuildFilled(Mat binary, DetectorOptions opts, out int closedCount)
        {
            int width  = binary.Cols;
            int height = binary.Rows;

            Mat filled = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
            closedCount = 0;

            double minArea  = Math.Max(2000.0, opts.MinContourArea * 0.20);
            double pageArea = width * (double)height;

            // 填实前的「断口桥接」核：至少 CloseGapPx，开启毫米规则时放大到「聚合间距」
            // （视图外轮廓常被尺寸线 / 引线打断 1~3 mm，3 mm 的缝也要能接上，否则填不出面积）
            int  radius    = opts.ClusterGapRadiusPx(opts.RenderDpi);
            int  closeSize = Math.Max(CloseGapPx, radius * 2 + 1);
            if (closeSize % 2 == 0)
                closeSize++;

            using (Mat bridged = new Mat())
            using (Mat closeKernel = Cv2.GetStructuringElement(
                       MorphShapes.Ellipse, new Size(closeSize, closeSize)))
            {
                // 先把断口接上：视图外轮廓缺一小段线时也能闭合成可填充的环
                Cv2.MorphologyEx(binary, bridged, MorphTypes.Close, closeKernel);

                using (Mat src = bridged.Clone())
                {
                    Point[][] contours;
                    HierarchyIndex[] hierarchy;

                    Cv2.FindContours(
                        src, out contours, out hierarchy,
                        RetrievalModes.List,
                        ContourApproximationModes.ApproxSimple);

                    foreach (Point[] contour in contours)
                    {
                        double area = Cv2.ContourArea(contour);
                        if (area < minArea)
                            continue;

                        // 包住整页的大框不填：一填就把里面的所有视图吞成一块，等于没拆
                        if (area > pageArea * GroupFrameAreaRatio)
                            continue;

                        Cv2.DrawContours(filled, new[] { contour }, -1, Scalar.All(255), -1);
                        closedCount++;
                    }
                }
            }

            return filled;
        }

        /// <summary>
        /// 开运算剥掉实体之间的细桥后取各实体的外轮廓（= 拆分后的「种子」）。
        /// 核越大越容易断开，但实体本身会被侵蚀变小；种子只用来定位，最终掩膜取自原二值图，所以不影响图形。
        /// </summary>
        private static List<Point[]> FindBodies(Mat filled, int kernelSize, DetectorOptions opts)
        {
            var bodies = new List<Point[]>();

            double minArea = Math.Max(2000.0, opts.MinContourArea * 0.20);
            int minWidth   = Math.Max(40, opts.MinBoundingWidth / 2);
            int minHeight  = Math.Max(20, opts.MinBoundingHeight / 2);

            using (Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(kernelSize, kernelSize)))
            using (Mat opened = new Mat())
            {
                Cv2.MorphologyEx(filled, opened, MorphTypes.Open, kernel);

                using (Mat src = opened.Clone())
                {
                    Point[][] contours;
                    HierarchyIndex[] hierarchy;

                    Cv2.FindContours(
                        src, out contours, out hierarchy,
                        RetrievalModes.External,
                        ContourApproximationModes.ApproxSimple);

                    foreach (Point[] contour in contours)
                    {
                        if (Cv2.ContourArea(contour) < minArea)
                            continue;

                        Rect box = Cv2.BoundingRect(contour);
                        if (box.Width < minWidth || box.Height < minHeight)
                            continue;

                        bodies.Add(contour);
                    }
                }
            }

            return bodies;
        }

        /// <summary>
        /// 按「墨迹间距」把相邻的实体种子归并成一块（单链聚类）。
        ///
        /// 同一个视图被尺寸线 / 中心线切成两三个闭合实体时，碎片之间的间距也在
        /// <see cref="DetectorOptions.ClusterGapMm"/> 以内（图纸里线是连续的、文字离最近的线不超过几毫米）；
        /// 不归并就会把一个视图导成好几张 <c>Part_NN.png</c>。
        ///
        /// 归并依据是<b>外接框间距</b>（≥ 实际墨迹间距，略偏保守），所以阈值取
        /// <c>2 × 半径</c>（≈ <see cref="DetectorOptions.ClusterGapMm"/>）；同组碎片填进同一张掩膜后
        /// 取最大外轮廓，得到合并后的实体。
        /// </summary>
        /// <param name="bodies">开运算得到的实体种子（页面坐标）。</param>
        /// <param name="radius">聚合半径（像素），见 <see cref="DetectorOptions.ClusterGapRadiusPx"/>。</param>
        /// <param name="width">页面宽（像素），用于建掩膜。</param>
        /// <param name="height">页面高（像素），用于建掩膜。</param>
        private static List<Point[]> MergeNearBodies(List<Point[]> bodies, int radius, int width, int height)
        {
            int n = bodies.Count;
            if (radius <= 0 || n < 2)
                return bodies;

            var boxes  = new Rect[n];
            var parent = new int[n];

            for (int i = 0; i < n; i++)
            {
                boxes[i]  = Cv2.BoundingRect(bodies[i]);
                parent[i] = i;
            }

            int gap = radius * 2;

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (!WithinGap(boxes[i], boxes[j], gap))
                        continue;

                    int rootI = FindRoot(parent, i);
                    int rootJ = FindRoot(parent, j);
                    if (rootI != rootJ)
                        parent[rootJ] = rootI;
                }
            }

            // 按并查集的根分组；全都各自一组时直接返回原列表（不动产物）
            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int root = FindRoot(parent, i);

                List<int> list;
                if (!groups.TryGetValue(root, out list))
                {
                    list = new List<int>();
                    groups[root] = list;
                }

                list.Add(i);
            }

            if (groups.Count == n)
                return bodies;

            var merged = new List<Point[]>(groups.Count);

            foreach (List<int> group in groups.Values)
            {
                if (group.Count == 1)
                {
                    merged.Add(bodies[group[0]]);
                    continue;
                }

                Point[] outer;
                using (Mat mask = new Mat(height, width, MatType.CV_8UC1, Scalar.Black))
                {
                    foreach (int idx in group)
                        Cv2.DrawContours(mask, new[] { bodies[idx] }, -1, Scalar.All(255), -1);

                    outer = FindLargestContour(mask);
                }

                if (outer != null)
                {
                    merged.Add(outer);
                }
                else
                {
                    // 合并失败（理论上不会发生）：保持原样，宁可少并也不错并
                    foreach (int idx in group)
                        merged.Add(bodies[idx]);
                }
            }

            return merged;
        }

        /// <summary>两个外接框的间距是否 ≤ <paramref name="gap"/>（像素）。</summary>
        private static bool WithinGap(Rect a, Rect b, int gap)
        {
            if (a.X - gap >= b.X + b.Width)  return false;
            if (b.X - gap >= a.X + a.Width)  return false;
            if (a.Y - gap >= b.Y + b.Height) return false;
            if (b.Y - gap >= a.Y + a.Height) return false;
            return true;
        }

        /// <summary>并查集：找根（带路径压缩）。</summary>
        private static int FindRoot(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        /// <summary>取二值图里面积最大的外轮廓；一个轮廓都没有时返回 null。</summary>
        private static Point[] FindLargestContour(Mat binary)
        {
            Point[] best     = null;
            double  bestArea = 0.0;

            using (Mat src = binary.Clone())
            {
                Point[][] contours;
                HierarchyIndex[] hierarchy;

                Cv2.FindContours(
                    src, out contours, out hierarchy,
                    RetrievalModes.External,
                    ContourApproximationModes.ApproxSimple);

                foreach (Point[] contour in contours)
                {
                    double area = Cv2.ContourArea(contour);
                    if (area > bestArea)
                    {
                        bestArea = area;
                        best     = contour;
                    }
                }
            }

            return best;
        }

        /// <summary>
        /// 由一块「实体种子」生成一个 <see cref="Region"/>：外扩 reach 把它的线框 / 标注圈回来，
        /// 减去已被别的块占走的像素，取其中墨迹的最大外轮廓做成多边形。
        /// </summary>
        /// <param name="claimed">占用表（全图，255 = 已被前面的块挑走）：避免相邻块把彼此的线框带进自己的掩膜。</param>
        private static Region BuildRegion(Mat binary, Mat claimed, Point[] body, int reach, DetectorOptions opts)
        {
            int width  = binary.Cols;
            int height = binary.Rows;

            Rect seedBox = Cv2.BoundingRect(body);
            Rect roi = IntersectPage(
                new Rect(seedBox.X - reach, seedBox.Y - reach,
                         seedBox.Width + reach * 2, seedBox.Height + reach * 2),
                width, height);

            if (roi.Width <= 1 || roi.Height <= 1)
                return null;

            using (Mat near       = new Mat(roi.Height, roi.Width, MatType.CV_8UC1, Scalar.Black))
            using (Mat free       = new Mat())
            using (Mat detail     = new Mat())
            using (Mat binRoi     = new Mat(binary, roi))
            using (Mat claimedRoi = new Mat(claimed, roi))
            using (Mat reachKernel = Cv2.GetStructuringElement(
                       MorphShapes.Rect, new Size(reach * 2 + 1, reach * 2 + 1)))
            {
                // ① 种子 = 本视图实心体（换成 ROI 局部坐标），外扩 reach 圈住它自己的线框 / 标注
                Point[] localBody = Offset(body, -roi.X, -roi.Y);
                Cv2.DrawContours(near, new[] { localBody }, -1, Scalar.All(255), -1);
                Cv2.Dilate(near, near, reachKernel);

                // ② 已被前面的块占走的像素不再属于本块（面积大的先挑地，块之间不会互相带线）
                Cv2.BitwiseNot(claimedRoi, free);
                Cv2.BitwiseAnd(near, free, near);

                // ③ 本块的细节 = 原二值图 ∩ 本块邻域（原图里的线框是空心的，正是要的形状）
                Cv2.BitwiseAnd(binRoi, near, detail);

                Region region = FindOutermostPolygon(detail, roi, width, height, opts);
                if (region == null)
                    return null;

                // ④ 本块邻域记入占用表，后面的块避开它
                Cv2.BitwiseOr(claimedRoi, near, claimedRoi);

                return region;
            }
        }

        /// <summary>
        /// 取一块邻域内墨迹的最大外轮廓 → 简化成多边形 → 外接框（含边距）。
        /// 墨迹太少或外接框几乎占满整页时判定为「不是子视图」，返回 null。
        /// </summary>
        private static Region FindOutermostPolygon(Mat detail, Rect roi, int imgWidth, int imgHeight,
                                                   DetectorOptions opts)
        {
            Point[] best = null;
            double bestArea = 0.0;

            using (Mat src = detail.Clone())
            {
                Point[][] contours;
                HierarchyIndex[] hierarchy;

                Cv2.FindContours(
                    src, out contours, out hierarchy,
                    RetrievalModes.External,
                    ContourApproximationModes.ApproxSimple);

                foreach (Point[] contour in contours)
                {
                    double area = Cv2.ContourArea(contour);
                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = contour;
                    }
                }
            }

            double minInk = Math.Max(1000.0, opts.MinContourArea * 0.10);
            if (best == null)
                return null;

            if (bestArea < minInk)
            {
                opts.Log($"[INFO] 不规则分割：跳过一块（邻域墨迹 {bestArea:0} < {minInk:0} 平方像素）");
                return null;
            }

            Point[] polygon = Offset(Cv2.ApproxPolyDP(best, PolygonEpsilon, true), roi.X, roi.Y);
            if (polygon.Length < 3)
                return null;

            Rect box = Cv2.BoundingRect(polygon);
            int pad  = Math.Max(0, opts.Padding);
            Rect bounds = IntersectPage(
                new Rect(box.X - pad, box.Y - pad, box.Width + pad * 2, box.Height + pad * 2),
                imgWidth, imgHeight);

            if (bounds.Width <= 1 || bounds.Height <= 1)
                return null;

            double fillLimit = Math.Max(opts.MaxBboxFillRatio, 0.60);
            double fillRatio = bounds.Width * (double)bounds.Height / (imgWidth * (double)imgHeight);

            if (fillRatio > fillLimit)
            {
                opts.Log($"[INFO] 不规则分割：跳过一块（外接框占页 {fillRatio:P0} > {fillLimit:P0}，"
                          + "可能是整体外框）");
                return null;
            }

            return new Region
            {
                Bounds   = bounds,
                Polygon  = polygon,
                InkArea  = bestArea
            };
        }

        /// <summary>
        /// 按多边形生成掩膜（与 <paramref name="roi"/> 同尺寸，255 = 属于该块）。
        /// 轮廓线本身有 1~2 px 宽，向外放 <see cref="MaskDilatePx"/> px，避免把图形自己的边线裁掉。
        /// </summary>
        /// <param name="polygon">该块的外轮廓（页面像素坐标）。</param>
        /// <param name="roi">裁切框，必须与要导出的裁切区域完全一致。</param>
        public static Mat BuildMask(Point[] polygon, Rect roi)
        {
            Mat mask = new Mat(roi.Height, roi.Width, MatType.CV_8UC1, Scalar.Black);

            if (polygon == null || polygon.Length < 3 || roi.Width <= 0 || roi.Height <= 0)
                return mask;

            Point[] local = Offset(polygon, -roi.X, -roi.Y);
            Cv2.DrawContours(mask, new[] { local }, -1, Scalar.All(255), -1);

            using (Mat kernel = Cv2.GetStructuringElement(
                       MorphShapes.Rect, new Size(MaskDilatePx * 2 + 1, MaskDilatePx * 2 + 1)))
            {
                Cv2.Dilate(mask, mask, kernel);
            }

            return mask;
        }


        /// <summary>轮廓点整体平移（局部坐标 ↔ 页面坐标）。</summary>
        private static Point[] Offset(Point[] points, int dx, int dy)
        {
            var moved = new Point[points.Length];
            for (int i = 0; i < points.Length; i++)
                moved[i] = new Point(points[i].X + dx, points[i].Y + dy);
            return moved;
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
    }
}
