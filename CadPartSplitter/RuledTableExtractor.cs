using System;
using System.Collections.Generic;
using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;

namespace CadPartSplitter
{
    /// <summary>
    /// 提取 PDF 里的「有线表格」：单元格四面都被框线围住的那种表（CAD 导出的图纸里，
    /// 部品表 / 型枠表 / 表題欄 / 変更履歴表 全部属于这一类）。
    ///
    /// 为什么不用「按文字间隙猜行列」（<see cref="PartTextExtractor"/> 里 outside_* 的做法）：
    /// 猜法有两个硬伤 —— ① 空单元格没有文字，猜不出来，列会错位；
    /// ② 合并单元格的文字横跨多列，猜出来会当成一列。CAD 图纸里这两点都很常见。
    ///
    /// 本类的做法是「只认框线」（CAD 导出 PDF 的准确方法）：
    ///   ① 用 PdfPig 取该页全部矢量路径（<c>page.ExperimentalAccess.Paths</c>，坐标已应用 CTM，
    ///      与文字层同一口径），把每条子路径扁平化成折线，挑出水平 / 竖直的边；
    ///      —— CAD 画粗线常把线导出成「0.2mm 高的填充四边形」，它的两条长边同样会被挑出来，
    ///      再按容差合并成一条，因此「描边线」和「填充细矩形」两种画法结果一致；
    ///   ② 共线线段合并（同一 y 的横线、同一 x 的竖线并成一条）；
    ///   ③ 枚举「最小闭合矩形」：上下两条横线 + 左右两条竖线正好围出的矩形，
    ///      且矩形内部没有横线横穿（否则那只是两个更小的格子拼起来的空档）；
    ///      —— 这样得到的每个矩形就是一个**四面都有线**的单元格；
    ///   ④ 相邻单元格（共用边界且方向上有重叠）并成一张表，行列号直接由边界坐标给出，
    ///      因此合并单元格的 RowSpan / ColumnSpan 是准确的；
    ///   ⑤ 文字层里的词按「词中心落在哪个格子」填料，不看间距；
    ///   ⑥ 落在「三角形注記符号（△1 / △2）」里的数字**不算格子内容**
    ///      （<see cref="MarkTriangleMarks"/>）：三角形是矢量图形、里面的数字在文字层里，
    ///      不认出来就会混进那一格的文字（`H986U10002` 的 切断寸法 读成 `100 1`）。
    ///
    /// 划线（取り消し線）的两档语义见 <see cref="MarkStruck"/>：整行划掉（≥2 格被划）→ 行作废；
    /// 只划掉品番那一格（部品コード）→ 只清空该格、明细照收。
    ///
    /// 只读、不参与分割；位图输入（无矢量路径）不产出任何表格。任何异常只写日志。
    /// </summary>
    public static class RuledTableExtractor
    {
        /// <summary>贝塞尔曲线扁平化时每段的等分采样数（与 <see cref="CalloutExtractor"/> 一致）。</summary>
        private const int BezierSamples = 8;

        /// <summary>1 毫米等于多少 PDF 单位（pt）：72 / 25.4。</summary>
        private const double PtPerMm = 72.0 / 25.4;

        /// <summary>
        /// 单元格面积上限（占整页面积的比例）。整页图框的内框也是「四面有线」的矩形，靠这一条排除。
        /// </summary>
        private const double MaxCellAreaRatio = 0.25;

        /// <summary>一张表至少要有几个单元格（单独一个封闭矩形是零件轮廓 / 图框，不是表）。</summary>
        private const int MinTableCells = 2;

        /// <summary>一张表至少要有几个文字字符（全是空格的封闭矩形不算表）。</summary>
        private const int MinTableChars = 2;

        /// <summary>
        /// 两个词的框在 x / y 两个方向的重叠都超过「短边」的这个比例时，当成
        /// 「同一个词被重叠画了两遍」（CAD 出图时文字层重复输出，两框几乎完全重合）。
        /// 正常相邻的重复字符（品番 <c>MZC○○63</c> 里的两个 ○）是并排的，x 方向不重叠，不会被误判。
        /// </summary>
        private const double OverlapWordRatio = 0.6;

        /// <summary>
        /// 提取一页上的全部有线表格。
        /// </summary>
        /// <param name="pdfPath">PDF 路径。</param>
        /// <param name="pageIndex">页码（从 0 开始）。</param>
        /// <param name="imgWidth">渲染图像宽（像素，用于 PDF 坐标 → 像素换算）。</param>
        /// <param name="imgHeight">渲染图像高。</param>
        /// <param name="opts">参数（开关、框线容差、最小单元格尺寸、日志回调）。</param>
        /// <returns>按位置（自上而下、自左而右）排序的表格清单；没有则返回空列表。</returns>
        public static List<RuledTable> Extract(
            string pdfPath, int pageIndex, int imgWidth, int imgHeight, DetectorOptions opts)
        {
            return Extract(pdfPath, pageIndex, imgWidth, imgHeight, opts, null);
        }

        /// <summary>
        /// 同上，并把图面上**被 取り消し線 划掉的词**一并返回（<paramref name="struckPageWords"/>，可为 null）。
        ///
        /// 与「有没有表格」无关：图纸上没画表格、把要領図索引写成自由文字的組立図（<c>H527U10001</c>）
        /// 里也可能把其中一条（<c>J部詳細：H325U10017</c>）划掉 —— 那时没有 <c>tables_info.json</c> 可查，
        /// 划线判断只发生在这里，不交出去下游就不知道哪一条已作废（见 <see cref="StruckWordMark"/>）。
        /// </summary>
        public static List<RuledTable> Extract(
            string pdfPath, int pageIndex, int imgWidth, int imgHeight, DetectorOptions opts,
            List<StruckWordMark> struckPageWords)
        {
            // 兼容旧调用：自己解析一次本页（流水线内部用下面的 PdfPageContent 重载，全页只解析一次）
            // 日志用 o.Log（opts 可能为 null；Log 是方法、不是委托，不能直接放进条件表达式）。
            DetectorOptions o = opts ?? new DetectorOptions();

            return Extract(PdfPageContent.Load(pdfPath, pageIndex, o.Log),
                           imgWidth, imgHeight, opts, struckPageWords);
        }

        /// <summary>
        /// 同上，但直接使用已经解析好的页面内容（<see cref="PdfPageContent"/>：页面尺寸 + 词 + 矢量路径），
        /// 不再打开 PDF。流水线里同一页的文字 / 圈号 / 表格共用这一份解析结果。
        /// </summary>
        public static List<RuledTable> Extract(
            PdfPageContent page, int imgWidth, int imgHeight, DetectorOptions opts,
            List<StruckWordMark> struckPageWords)
        {
            var tables = new List<RuledTable>();
            DetectorOptions o = opts ?? new DetectorOptions();
            Action<string> write = o.Log;

            if (!o.ExtractRuledTables)
            {
                write("[INFO] 有线表格内容提取已关闭（ExtractRuledTables = false）。");
                return tables;
            }

            if (page == null || !page.IsValid)
                return tables;      // 解析失败 / 页码越界：PdfPageContent.Load 里已经写过 WARN

            if (imgWidth <= 0 || imgHeight <= 0)
            {
                write("[WARN] 页面尺寸或渲染尺寸无效，跳过表格内容提取。");
                return tables;
            }

            try
            {
                double pageW = page.Width, pageH = page.Height;

                double scaleX = imgWidth / pageW;
                double scaleY = imgHeight / pageH;
                double mmPerPx = 25.4 / Math.Max(1.0, o.RenderDpi);

                // 毫米参数 → PDF 单位（pt）
                double tol = Math.Max(0.1, o.RuledTableLineToleranceMm) * PtPerMm;
                double minLine = Math.Max(0.2, o.RuledTableMinLineMm) * PtPerMm;
                double minCell = Math.Max(0.5, o.RuledTableMinCellMm) * PtPerMm;
                double maxRowGap = Math.Max(minCell * 2.0, o.RuledTableMaxRowGapMm * PtPerMm);

                var horizontals = new List<Seg>();
                var verticals = new List<Seg>();

                // 斜边不进框线集合，只用来认「三角形注記符号（△1 这种提醒记号）」：
                // 三角形是矢量图形、里面的数字却在文字层里，不认出来就会被当成格子内容
                // （H986U10002 的 切断寸法「100」旁边有个 △1 → 拼成「100 1」）
                var diagonals = new List<Diag>();

                int pathCount = CollectLineSegments(page.Paths, tol, minLine, horizontals, verticals, diagonals);

                MergeCollinear(horizontals, tol);
                MergeCollinear(verticals, tol);

                write("[INFO] 表格框线：矢量路径 " + pathCount + " 条 → 横线 "
                      + horizontals.Count + " 条 / 竖线 " + verticals.Count
                      + " 条（共线段已合并，容差 "
                      + o.RuledTableLineToleranceMm.ToString("0.##") + "mm）");

                // 文字层先读出来：识别「取り消し線（划掉整行的横线）」要靠它 ——
                // 横线从字身中间穿过、又把字整个盖住，那是 CAD 图里「这一行作废」的画法，不是格子边
                List<WordBox> words = ReadWords(page.Words);

                int dupWords = CountOverlappingDuplicates(words);

                if (dupWords > 0)
                {
                    write("[INFO] 表格文字：有 " + dupWords + " 个词是同一个词重叠画了两遍"
                          + "（CAD 文字重复放置 —— 表头会落成 部部品品ココーードド 这种样子）"
                          + "→ 拼接格内文字时只留一个。");
                }

                var struckWords = new bool[words.Count];

                int strikeLines = DropStrikeLines(
                    horizontals, words, tol, minCell, struckWords, out List<Seg> strikeSegs);

                if (strikeLines > 0)
                {
                    write("[INFO] 表格框线：剔除 " + strikeLines + " 条 取り消し線（横穿文字的划线，"
                          + "不是格子边）→ 横线剩 " + horizontals.Count + " 条");

                    foreach (Seg s in strikeSegs)
                    {
                        write("[INFO]   划线 px(y=" + Math.Round((pageH - s.P) * scaleY)
                              + " x=" + Math.Round(s.A * scaleX) + "~" + Math.Round(s.B * scaleX) + ")");
                    }
                }

                int struckWordsCount = 0;
                foreach (bool b in struckWords) if (b) struckWordsCount++;

                // 页面级「被划掉的词」先交出去（与有没有表格无关）：
                // 图纸上没画表格、把要領図索引写成自由文字的組立図（H527U10001）也可能
                // 把其中一条划掉 —— 那时没有 tables_info.json 可查，只能靠这份清单传给下游。
                int pageStruck = CollectStruckPageWords(
                    struckPageWords, words, struckWords, pageH, scaleX, scaleY);

                if (struckWordsCount > 0)
                    write("[INFO] 表格文字：" + struckWordsCount + " 个词被 取り消し線 划掉"
                          + "（整行划掉 → 该行作废、不写进 tables\\*.csv；只划掉品番那一格 → 只清空该格，明细照收）"
                          + (pageStruck > 0
                                ? "；已记进图面划线清单 " + pageStruck
                                  + " 个词（下游 01_アッセンブリ図_要領図 据此剔除作废条目）"
                                : string.Empty));

                // 划线检测放在「有没有框线」之前：没有可围成矩形的框线时也要先把划线词交出去，
                // 之后才判定「这页没有表格」直接返回。
                if (horizontals.Count < 2 || verticals.Count < 2)
                {
                    write("[INFO] 表格内容：该页没有可围成矩形的框线（位图 / 文字已轮廓化的图纸属正常）。");
                    return tables;
                }

                // 三角形注記符号（△1 / △2 这种提醒记号）里的数字：图形在矢量层、数字在文字层，
                // 不认出来就会被当成格子内容（H986U10002 的 切断寸法 读成「100 1」）
                bool[] markWords = MarkTriangleMarks(words, diagonals);

                int markCount = 0;
                foreach (bool b in markWords) if (b) markCount++;

                if (markCount > 0)
                    write("[INFO] 表格文字：" + markCount
                          + " 个词是「三角形注記符号里的数字」（△1 / △2 这种提醒记号："
                          + "三角形是矢量图形、数字在文字层里）→ 不计入格内文字。");

                List<Cell> cells = FindCells(horizontals, verticals, tol, minCell, maxRowGap);
                int rawCells = cells.Count;

                // 图框（整页大小的内框）同样「四面有线」，按面积比排除
                double pageArea = pageW * pageH;
                cells.RemoveAll(c => CellArea(c) > pageArea * MaxCellAreaRatio);

                // 孤立矩形（文字笔画凑出来的假格子）先清掉：留着它，下一步会把它包住的真格子当「套娃」误删
                int isolatedCells = DropIsolatedCells(cells, tol);

                RemoveContainers(cells, tol);

                // 词中心落在哪个格子就归哪个格子，不看间距；
                // 先给面积小的格子挑，这样嵌套 / 重叠时文字留在最里层
                var used = new bool[words.Count];
                var byAreaAsc = new List<Cell>(cells);
                byAreaAsc.Sort((a, b) => CellArea(a).CompareTo(CellArea(b)));

                int assignedWords = 0;
                foreach (var c in byAreaAsc)
                    assignedWords += FillCell(c, words, used, struckWords, markWords);

                List<List<Cell>> groups = GroupCells(cells, tol);

                int kept = 0;
                foreach (var g in groups)
                {
                    RuledTable table = BuildTable(g, tol, pageH, scaleX, scaleY, mmPerPx);
                    if (table == null) continue;

                    tables.Add(table);
                    kept++;
                }

                tables.Sort(ByPosition);
                for (int i = 0; i < tables.Count; i++)
                    // 名称按位置序号编，两位补零：tables\table_01.csv … 在资源管理器 / Excel 里也按顺序排
                    tables[i].Name = "table_" + (i + 1).ToString("00");

                int cellTotal = 0, textCellTotal = 0, struckRowTotal = 0, struckCellTotal = 0;
                foreach (var t in tables)
                {
                    cellTotal += t.CellCount;
                    textCellTotal += t.TextCellCount;
                    struckRowTotal += t.StruckRowCount;
                    struckCellTotal += t.StruckCellCount;
                }

                write("[INFO] 表格内容：闭合矩形 " + rawCells + " 个 → 有效单元格 " + cells.Count
                      + " 个（已排除图框等超大矩形"
                      + (isolatedCells > 0
                          ? "、文字笔画凑出的假矩形 " + isolatedCells + " 个"
                          : string.Empty)
                      + "），归成 " + kept + " 张表 / 单元格 " + cellTotal
                      + " 个（有文字 " + textCellTotal + " 个，共填入 " + assignedWords + " 个词）"
                      + (struckRowTotal > 0
                          ? "；划线作废 " + struckRowTotal + " 行（不写进 tables\\*.csv）"
                          : string.Empty)
                      + (struckCellTotal > 0
                          ? "；品番作废 " + struckCellTotal + " 格（只清空该格，明细照收）"
                          : string.Empty));

                foreach (var t in tables)
                {
                    string strikeNote = string.Empty;

                    if (t.StruckRowCount > 0)
                        strikeNote = "划线作废 " + t.StruckRowCount + " 行 → 实出 " + t.VisibleRowCount + " 行";

                    if (t.StruckCellCount > 0)
                        strikeNote += (strikeNote.Length > 0 ? "；" : string.Empty)
                                      + "品番作废 " + t.StruckCellCount + " 格";

                    write("[TABLE] " + t.Name + "  行 " + t.RowCount + " / 列 " + t.ColumnCount
                          + " / 单元格 " + t.CellCount + "（有文字 " + t.TextCellCount + "）"
                          + (strikeNote.Length > 0 ? "【" + strikeNote + "】" : string.Empty)
                          + "  绝对(x=" + t.PixelX + " y=" + t.PixelY
                          + " w=" + t.PixelW + " h=" + t.PixelH + ")");
                }
            }
            catch (Exception ex)
            {
                write("[WARN] 表格内容提取失败（不影响分割结果）：" + ex.Message);
            }

            return tables;
        }

        // ── 框线采集 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 一条水平或竖直的线段：<see cref="P"/> 是它的固定坐标（横线为 y、竖线为 x），
        /// <see cref="A"/>~<see cref="B"/> 是另一方向上的区间。坐标均为 PDF 单位（pt，Y 向上）。
        /// </summary>
        private class Seg
        {
            public double P;
            public double A;
            public double B;
        }

        /// <summary>
        /// 遍历该页所有矢量路径：水平 / 竖直的边收进 <paramref name="horizontals"/> /
        /// <paramref name="verticals"/>（围格子用），斜边收进 <paramref name="diagonals"/>
        /// （只给「三角形注記符号」的判据用，见 <see cref="MarkTriangleMarks"/>）。
        /// </summary>
        /// <returns>处理过的矢量路径条数（仅用于日志）。</returns>
        private static int CollectLineSegments(
            IReadOnlyList<PdfPath> paths, double tol, double minLine,
            List<Seg> horizontals, List<Seg> verticals, List<Diag> diagonals)
        {
            if (paths == null)
                return 0;

            int pathCount = 0;

            foreach (var path in paths)
            {
                pathCount++;

                foreach (var sub in path)
                {
                    List<PdfPoint> pts = Flatten(sub, BezierSamples);

                    for (int i = 1; i < pts.Count; i++)
                    {
                        AddSegment(pts[i - 1], pts[i], tol, minLine, horizontals, verticals);
                        AddDiagonal(pts[i - 1], pts[i], tol, minLine, diagonals);
                    }
                }
            }

            return pathCount;
        }

        /// <summary>
        /// 把一条边登记成横线 / 竖线：斜边（两个方向都超过容差）忽略；
        /// 长度短于 <paramref name="minLine"/> 的边忽略
        /// （填充细矩形在线宽方向的两条短边、文字笔画、箭头都靠这一条过滤）。
        /// </summary>
        private static void AddSegment(
            PdfPoint p, PdfPoint q, double tol, double minLine,
            List<Seg> horizontals, List<Seg> verticals)
        {
            double dx = Math.Abs(q.X - p.X);
            double dy = Math.Abs(q.Y - p.Y);

            if (dy <= tol && dx >= minLine)
            {
                horizontals.Add(new Seg
                {
                    P = (p.Y + q.Y) / 2.0,
                    A = Math.Min(p.X, q.X),
                    B = Math.Max(p.X, q.X)
                });
            }
            else if (dx <= tol && dy >= minLine)
            {
                verticals.Add(new Seg
                {
                    P = (p.X + q.X) / 2.0,
                    A = Math.Min(p.Y, q.Y),
                    B = Math.Max(p.Y, q.Y)
                });
            }
        }

        /// <summary>
        /// 把一条**斜**边登记下来（两个方向都超过 <paramref name="tol"/>、长度不短于
        /// <paramref name="minLine"/>）。水平 / 竖直的边由 <see cref="AddSegment"/> 收走，
        /// 这里只收三角形注記符号（△1）的边 —— 字身笔画凑出来的短斜线被
        /// <paramref name="minLine"/> 挡在外面（判据见 <see cref="MarkTriangleMarks"/>）。
        /// </summary>
        private static void AddDiagonal(
            PdfPoint p, PdfPoint q, double tol, double minLine, List<Diag> diagonals)
        {
            if (diagonals == null) return;

            double dx = Math.Abs(q.X - p.X);
            double dy = Math.Abs(q.Y - p.Y);

            if (dx <= tol || dy <= tol) return;                 // 水平 / 竖直线：不是三角形的边
            if (Math.Sqrt(dx * dx + dy * dy) < minLine) return;  // 太短：文字笔画、箭头的小斜线

            diagonals.Add(new Diag { X1 = p.X, Y1 = p.Y, X2 = q.X, Y2 = q.Y });
        }

        /// <summary>
        /// 把子路径扁平化成折线点列：直线取端点，贝塞尔曲线按 <paramref name="steps"/> 等分采样。
        /// CAD 导出的线段可能被写成「直线」「折线（多段短直线）」或「曲线」，扁平化后统一处理。
        /// </summary>
        private static List<PdfPoint> Flatten(PdfSubpath sub, int steps)
        {
            var pts = new List<PdfPoint>();
            if (sub == null) return pts;

            foreach (var c in sub.Commands)
            {
                if (c is PdfSubpath.Move)
                {
                    pts.Add(((PdfSubpath.Move)c).Location);
                }
                else if (c is PdfSubpath.Line)
                {
                    var line = (PdfSubpath.Line)c;
                    pts.Add(line.From);
                    pts.Add(line.To);
                }
                else if (c is PdfSubpath.CubicBezierCurve)
                {
                    var cb = (PdfSubpath.CubicBezierCurve)c;
                    for (int i = 1; i <= steps; i++)
                        pts.Add(CubicAt(cb.StartPoint, cb.FirstControlPoint, cb.SecondControlPoint,
                                        cb.EndPoint, i / (double)steps));
                }
                else if (c is PdfSubpath.QuadraticBezierCurve)
                {
                    var qb = (PdfSubpath.QuadraticBezierCurve)c;
                    for (int i = 1; i <= steps; i++)
                        pts.Add(QuadAt(qb.StartPoint, qb.ControlPoint, qb.EndPoint, i / (double)steps));
                }
                else if (c is PdfSubpath.BezierCurve)
                {
                    var b = (PdfSubpath.BezierCurve)c;
                    pts.Add(b.StartPoint);
                    pts.Add(b.EndPoint);
                }
            }

            return pts;
        }

        /// <summary>三次贝塞尔曲线上 t 处的点。</summary>
        private static PdfPoint CubicAt(PdfPoint p0, PdfPoint p1, PdfPoint p2, PdfPoint p3, double t)
        {
            double u = 1 - t;
            double x = u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X;
            double y = u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y;
            return new PdfPoint(x, y);
        }

        /// <summary>二次贝塞尔曲线上 t 处的点。</summary>
        private static PdfPoint QuadAt(PdfPoint p0, PdfPoint p1, PdfPoint p2, double t)
        {
            double u = 1 - t;
            return new PdfPoint(u * u * p0.X + 2 * u * t * p1.X + t * t * p2.X,
                                u * u * p0.Y + 2 * u * t * p1.Y + t * t * p2.Y);
        }

        /// <summary>
        /// 共线线段合并：固定坐标相差 ≤ <paramref name="tol"/>，且区间重叠或间隙 ≤ <paramref name="tol"/>
        /// 的线段并成一条（区间取并集，固定坐标取加权平均）。
        ///
        /// 为什么必须合并：CAD 常把每个单元格画成一个独立矩形，同一条表格线因此被画两遍
        /// （上一格的底边 + 下一格的顶边）；粗线则是「两条长边 + 两条短边」的填充四边形。
        /// 合并之后，每个格子的四条边都对应唯一一条线段，闭合矩形的枚举才不会被重复的线搞乱。
        /// </summary>
        private static void MergeCollinear(List<Seg> segs, double tol)
        {
            if (segs.Count < 2) return;

            segs.Sort((a, b) => a.P != b.P ? a.P.CompareTo(b.P) : a.A.CompareTo(b.A));

            var merged = new List<Seg>();
            var active = new List<Seg>();         // 还在容差范围内的簇
            var activeCounts = new List<int>();

            foreach (var s in segs)
            {
                // 固定坐标已超出容差的簇不再参与匹配，落到结果里
                for (int i = active.Count - 1; i >= 0; i--)
                {
                    if (s.P - active[i].P > tol)
                    {
                        merged.Add(active[i]);
                        active.RemoveAt(i);
                        activeCounts.RemoveAt(i);
                    }
                }

                int best = -1;
                double bestDist = double.MaxValue;

                for (int i = 0; i < active.Count; i++)
                {
                    if (IntervalGap(active[i], s) > tol) continue;   // 区间离得太远，不是同一条线

                    double d = Math.Abs(active[i].P - s.P);
                    if (d < bestDist) { bestDist = d; best = i; }
                }

                if (best < 0)
                {
                    active.Add(new Seg { P = s.P, A = s.A, B = s.B });
                    activeCounts.Add(1);
                }
                else
                {
                    Seg m = active[best];
                    int n = activeCounts[best];

                    m.P = (m.P * n + s.P) / (n + 1);
                    m.A = Math.Min(m.A, s.A);
                    m.B = Math.Max(m.B, s.B);
                    activeCounts[best] = n + 1;
                }
            }

            merged.AddRange(active);
            merged.Sort((a, b) => a.P.CompareTo(b.P));

            segs.Clear();
            segs.AddRange(merged);
        }

        /// <summary>两个区间之间的距离（重叠或相接时为负 / 0）。</summary>
        private static double IntervalGap(Seg a, Seg b)
        {
            return Math.Max(a.A, b.A) - Math.Min(a.B, b.B);
        }

        // ── 取り消し線（划线作废的行）──────────────────────────────────────────

        /// <summary>
        /// 一条横线要盖住词宽的多大比例才算「划掉」。文字轮廓的碎线（本图的文字也是填充路径）
        /// 通常只有 2~4%，真正的划线是 100%，差得很远。
        /// </summary>
        private const double StrikeCoverRatio = 0.6;

        /// <summary>
        /// 划线的落点要在字身高度中间的这一段之内（上下各让出 1/4）——
        /// 写字时的下划线只贴在字身底下、上划线只压着字身顶上，都不会落到中间。
        /// </summary>
        private const double StrikeMiddleMargin = 0.25;

        /// <summary>
        /// 短双线（判据 ③）的间距上限：最小单元格的这几倍。划线常画在字身的上 / 下三分之一处，
        /// 字高 2.5~4mm 时两条线的间距就到了 2~4mm，比 ② 的 2mm 上限宽
        /// （<c>H527U10001</c> 的 <c>J部詳細：H325U10017</c> 就是这种画法）。
        /// </summary>
        private const double StrikeShortPairGapRatio = 3.0;

        /// <summary>
        /// 短双线（判据 ③）里「这条线只划这一个词」的长度上限：词宽的这几倍。
        /// 表格的行边界线横跨整行，远超这个比例，所以不会被 ③ 当成划线删掉。
        /// </summary>
        private const double StrikeShortLineRatio = 1.5;

        /// <summary>
        /// 剔除 取り消し線（划掉整行的横线），并把被划掉的词标出来。
        ///
        /// 为什么必须剔除：CAD 图把某一行作废时，是在那一行的文字上直接画一条（有时两条）横线。
        /// 那不是格子边，却被当成行边界 —— H310U10004 表題欄的
        /// 「枠 建て付け注意ラベル貼付要領図」就是典型：两条相距 1.4mm 的线从字中间穿过，
        /// 把那一格切成 34px / 17px / 30px 三段，表格凭空多出两行，
        /// 文字掉进「比字还矮」的假行里，与同行的「D」、「H310U10025」错开。
        ///
        /// 判据（三道，满足任一即成划线）：
        ///   ① 单线：线的 y 落在某个词字身的中间一半、且横向盖住该词 ≥ <see cref="StrikeCoverRatio"/>
        ///      —— 真框线不会这样：文字写在格子内部，框线最多擦到字身的边；
        ///   ② 双线：两条相距 &lt; <paramref name="minCell"/> 的线夹住同一个词
        ///      —— 这么窄的带子装不下字，不可能是行，两条一起丢；
        ///   ③ 短双线：间距放宽到 <see cref="StrikeShortPairGapRatio"/> 倍最小单元格，
        ///      但两条线都只盖住一个词（长度 ≤ 词宽 × <see cref="StrikeShortLineRatio"/>）
        ///      且都落在词高之内 —— 表格的行边界线横跨整行，不会被当成划线。
        ///
        /// 被划到的词通过 <paramref name="struck"/>（与 <paramref name="words"/> 下标对齐）返回，
        /// 上层据此把「整行作废」的行从 <c>tables\*.csv</c> 里剔掉。
        /// </summary>
        /// <param name="struck">输出：与 <paramref name="words"/> 下标对齐的「被划掉」标记。</param>
        /// <param name="dropped">输出：被剔除的横线（PDF 单位），仅用于日志。</param>
        /// <returns>剔除的横线条数（0 表示没有划线）。</returns>
        private static int DropStrikeLines(
            List<Seg> horizontals, List<WordBox> words, double tol, double minCell, bool[] struck,
            out List<Seg> dropped)
        {
            dropped = new List<Seg>();

            if (horizontals.Count == 0 || words.Count == 0) return 0;

            var drop = new bool[horizontals.Count];

            // ① 一条线从字身中间穿过、且把字盖住 → 划线
            for (int i = 0; i < horizontals.Count; i++)
                if (CrossesWordMiddle(horizontals[i], words, struck))
                    drop[i] = true;

            // ② 两条贴得很近的线夹着同一个词 → 双线划线
            for (int i = 0; i < horizontals.Count; i++)
            {
                Seg top = horizontals[i];

                for (int j = i + 1; j < horizontals.Count; j++)
                {
                    Seg bottom = horizontals[j];

                    double height = bottom.P - top.P;

                    if (height <= tol) continue;                 // 同一条线（合并后不该出现，保险）
                    if (height >= minCell) break;                // 按 y 升序，再往后只会更远

                    if (!PairCrossesWord(top, bottom, words, tol, struck)) continue;

                    drop[i] = true;
                    drop[j] = true;
                }
            }

            // ③ 短双线：间距比「双线」宽（画得松的两条划线，H527U10001 就是这种），
            //    但两条线都只盖住一个词 —— 表格的行边界线横跨整行、远超一个词的宽度，
            //    所以「两条短线只夹住同一个词」只可能是专门划掉这个词的线。
            double shortGap = minCell * StrikeShortPairGapRatio;

            for (int i = 0; i < horizontals.Count; i++)
            {
                if (drop[i]) continue;

                Seg top = horizontals[i];

                for (int j = i + 1; j < horizontals.Count; j++)
                {
                    if (drop[j]) continue;

                    Seg bottom = horizontals[j];

                    double height = bottom.P - top.P;

                    if (height <= tol) continue;
                    if (height > shortGap) break;                // 按 y 升序，再往后只会更远

                    if (!ShortPairCrossesWord(top, bottom, words, tol, struck)) continue;

                    drop[i] = true;
                    drop[j] = true;
                }
            }

            int removed = 0;

            for (int i = horizontals.Count - 1; i >= 0; i--)
            {
                if (!drop[i]) continue;
                dropped.Add(horizontals[i]);
                horizontals.RemoveAt(i);
                removed++;
            }

            dropped.Reverse();   // 从下往上删的，日志里按 y 升序打出来更好读

            return removed;
        }

        /// <summary>
        /// 横线是否「划掉」了某些词：线的 y 在词身中间一半之内，
        /// 且横向盖住词宽的 <see cref="StrikeCoverRatio"/> 以上。命中的词标成 struck。
        /// </summary>
        private static bool CrossesWordMiddle(Seg h, List<WordBox> words, bool[] struck)
        {
            bool hit = false;

            for (int i = 0; i < words.Count; i++)
            {
                WordBox w = words[i];

                double height = w.Y2 - w.Y1;
                if (height <= 0) continue;

                // 词 Y2 = 上边、Y1 = 下边（PDF 单位，Y 向上）
                if (h.P <= w.Y1 + height * StrikeMiddleMargin) continue;
                if (h.P >= w.Y2 - height * StrikeMiddleMargin) continue;

                double cover = Math.Min(h.B, w.X2) - Math.Max(h.A, w.X1);
                if (cover < (w.X2 - w.X1) * StrikeCoverRatio) continue;

                struck[i] = true;
                hit = true;
            }

            return hit;
        }

        /// <summary>
        /// 两条横线（相距 &lt; 最小单元格）是否从同一个词中间穿过：
        /// 两条线在竖直方向都落在词的高度之内（各留 <paramref name="tol"/> 余量），
        /// 且词中心在两条线的公共水平区间内 —— 即那个词确实被这两条线夹住 / 切开。
        /// 命中的词一并标成 struck。
        /// </summary>
        private static bool PairCrossesWord(
            Seg top, Seg bottom, List<WordBox> words, double tol, bool[] struck)
        {
            double a = Math.Max(top.A, bottom.A);   // 两条线的公共水平区间
            double b = Math.Min(top.B, bottom.B);
            if (b - a <= 0) return false;

            bool hit = false;

            for (int i = 0; i < words.Count; i++)
            {
                WordBox w = words[i];

                // 词 Y2 = 上边、Y1 = 下边（PDF 单位，Y 向上）
                if (top.P <= w.Y1 + tol || bottom.P >= w.Y2 - tol) continue;

                double cx = (w.X1 + w.X2) / 2.0;
                if (cx < a - tol || cx > b + tol) continue;

                struck[i] = true;
                hit = true;
            }

            return hit;
        }

        /// <summary>
        /// ③ 短双线：两条横线间距可以放宽（≤ <see cref="StrikeShortPairGapRatio"/> 倍最小单元格），
        /// 但两条线都**只盖住一个词**（长度 ≤ 词宽 × <see cref="StrikeShortLineRatio"/>），
        /// 且两条线都落在词高之内、词中心在公共区间内 —— 那是「用两条线把一个词划掉」的画法。
        ///
        /// 为什么加这道：CAD 里把某一条划掉时两条线的间距常常等于字高（2~4mm），
        /// 超过 ② 的 2mm 上限（<c>H527U10001</c> 的 <c>J部詳細：H325U10017</c> 就是这样）。
        /// 表格的行边界线一定横跨整行（远超一个词的宽度），所以不会被这条判据误判。
        /// </summary>
        private static bool ShortPairCrossesWord(
            Seg top, Seg bottom, List<WordBox> words, double tol, bool[] struck)
        {
            double a = Math.Max(top.A, bottom.A);   // 两条线的公共水平区间
            double b = Math.Min(top.B, bottom.B);
            if (b - a <= 0) return false;

            bool hit = false;

            for (int i = 0; i < words.Count; i++)
            {
                WordBox w = words[i];

                double height = w.Y2 - w.Y1;
                if (height <= 0) continue;

                // 两条线都落在词高之内（与 ② 一样各留 tol 余量）
                if (top.P <= w.Y1 + tol || bottom.P >= w.Y2 - tol) continue;

                double cx = (w.X1 + w.X2) / 2.0;
                if (cx < a - tol || cx > b + tol) continue;

                // 两条线都只够盖住这一个词 → 是专门划它的线，不是表格边界
                double width = w.X2 - w.X1;
                if (top.B - top.A > width * StrikeShortLineRatio) continue;
                if (bottom.B - bottom.A > width * StrikeShortLineRatio) continue;

                struck[i] = true;
                hit = true;
            }

            return hit;
        }

        // ── 三角形注記符号（△1 这种提醒记号）──────────────────────────────────

        /// <summary>
        /// 一条斜边（三角形注記符号的边）。两端点为 PDF 单位坐标（Y 向上），与文字层同一口径。
        ///
        /// 表格识别本身只用水平 / 竖直的线（格子边），斜边只用来认「三角形里那个数字」——
        /// 见 <see cref="MarkTriangleMarks"/>。
        /// </summary>
        private class Diag
        {
            public double X1;
            public double Y1;
            public double X2;
            public double Y2;
        }

        /// <summary>
        /// 三角形注記符号的边长相对字高的下限：三角形的边不会比里面的字还矮（&lt; 1 倍就不成立）。
        /// </summary>
        private const double TriangleMinSideRatio = 1.0;

        /// <summary>
        /// 三角形注記符号的边长相对字高的上限：再大就不是「把数字框起来的记号」了
        /// （视图框 / 箭头 / 剖面符号都超过这个数）。
        /// </summary>
        private const double TriangleMaxSideRatio = 4.0;

        /// <summary>
        /// 认「三角形里作为提醒的数字」（△1 / △2 这种注記符号），返回与
        /// <paramref name="words"/> 下标对齐的标记；标记为 true 的词**不算格子内容**。
        ///
        /// 为什么要认：三角形本身是**矢量图形**（画在图面上，不在文字层里），
        /// 里面的数字却是文字层里的一个孤立数字词 —— 它落在哪个格子就被
        /// <see cref="FillCell"/> 拼进那一格的文字。`H986U10002.pdf` 的部品表就是：
        /// 切断寸法写着 `100`，旁边盖了个 △1，CSV 里成了 <c>100 1</c>，下游把注記記号
        /// 当成了寸法。
        ///
        /// 判据（**全部满足**才算，宁可漏判也不误伤真数据）：
        ///   ① 词本身是 1~3 位的纯数字（与 <see cref="CalloutExtractor"/> 认圈内数字同一约束）；
        ///   ② 有两条斜边，长度都在字高的
        ///      <see cref="TriangleMinSideRatio"/>~<see cref="TriangleMaxSideRatio"/> 倍之间；
        ///   ③ 这两条斜边有一个公共端点（三角形的顶点，容差 1/4 字高）；
        ///   ④ 另两个端点几乎等高（底边水平，容差 1/2 字高），底宽 1/2~3 倍字高，
        ///      顶点到底边的高 0.8~4 倍字高；
        ///   ⑤ 词的中心落在这个三角形里。
        ///
        /// 顶点重合 + 底边水平 + 字在三角形内 —— 文字笔画、引线、箭头都凑不出这个组合；
        /// 实心画法的三角形（一条斜边导出成两条平行的长斜线）也走得通：两条平行线当不成
        /// 「顶点 + 水平底边」，会被 ④ 的底宽判据挡掉。
        /// </summary>
        /// <param name="words">该页文字层的全部词。</param>
        /// <param name="diagonals">该页矢量路径里的斜边（<see cref="AddDiagonal"/>）。</param>
        /// <returns>与 <paramref name="words"/> 下标对齐的「落在三角形注記符号里」标记。</returns>
        private static bool[] MarkTriangleMarks(List<WordBox> words, List<Diag> diagonals)
        {
            var marked = new bool[words.Count];

            if (words.Count == 0 || diagonals == null || diagonals.Count < 2) return marked;

            for (int i = 0; i < words.Count; i++)
            {
                if (!IsMarkNumber(words[i].Text)) continue;
                if (InTriangle(words[i], diagonals)) marked[i] = true;
            }

            return marked;
        }

        /// <summary>
        /// 三角形里那个「提醒数字」：1~3 位的纯数字（半角、全角都认）。
        /// 品番 / 寸法 / 日文都不满足，因此不会被当成注記記号。
        /// </summary>
        private static bool IsMarkNumber(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            string s = text.Trim();

            if (s.Length == 0 || s.Length > 3) return false;

            foreach (char ch in s)
            {
                bool digit = (ch >= '0' && ch <= '9')
                             || (ch >= (char)0xFF10 && ch <= (char)0xFF19);   // 全角 ０~９

                if (!digit) return false;
            }

            return true;
        }

        /// <summary>
        /// 这个词是否被某个三角形注記符号框住（判据见 <see cref="MarkTriangleMarks"/>）。
        /// </summary>
        private static bool InTriangle(WordBox w, List<Diag> diagonals)
        {
            double h = w.Y2 - w.Y1;
            if (h <= 0) return false;

            double cx = (w.X1 + w.X2) / 2.0;
            double cy = (w.Y1 + w.Y2) / 2.0;

            double minLen = h * TriangleMinSideRatio;
            double maxLen = h * TriangleMaxSideRatio;
            double join = h * 0.25;    // 两条斜边「顶点重合」的容差
            double pad = h * 3.0;      // 顶点 / 底边端点的搜索窗口：词框外扩 3 倍字高

            // 先挑出「长度像三角形边、两端又都在词框周围」的斜边，缩小下面两两配对的规模
            var near = new List<Diag>();

            foreach (var d in diagonals)
            {
                double dx = d.X2 - d.X1, dy = d.Y2 - d.Y1;
                double len = Math.Sqrt(dx * dx + dy * dy);

                if (len < minLen || len > maxLen) continue;
                if (!NearBox(d.X1, d.Y1, w, pad)) continue;
                if (!NearBox(d.X2, d.Y2, w, pad)) continue;

                near.Add(d);
            }

            for (int i = 0; i < near.Count; i++)
            {
                for (int j = i + 1; j < near.Count; j++)
                {
                    double apexX, apexY, baseX1, baseY1, baseX2, baseY2;

                    if (!TryApex(near[i], near[j], join,
                                 out apexX, out apexY, out baseX1, out baseY1, out baseX2, out baseY2))
                        continue;

                    if (Math.Abs(baseY1 - baseY2) > h * 0.5) continue;      // 底边不水平

                    double baseWidth = Math.Abs(baseX2 - baseX1);
                    if (baseWidth < h * 0.5 || baseWidth > h * 3.0) continue;

                    double triHeight = Math.Abs(apexY - (baseY1 + baseY2) / 2.0);
                    if (triHeight < h * 0.8 || triHeight > h * 4.0) continue;

                    if (PointInTriangle(cx, cy, apexX, apexY, baseX1, baseY1, baseX2, baseY2))
                        return true;
                }
            }

            return false;
        }

        /// <summary>点是否落在「词框外扩 <paramref name="pad"/>」的窗口里。</summary>
        private static bool NearBox(double x, double y, WordBox w, double pad)
        {
            return x >= w.X1 - pad && x <= w.X2 + pad
                   && y >= w.Y1 - pad && y <= w.Y2 + pad;
        }

        /// <summary>
        /// 两条斜边若有公共端点（容差 <paramref name="join"/>）就当它是三角形的顶点，
        /// 另外两个端点经 <paramref name="baseX1"/> … 带出（底边的两端）。
        /// </summary>
        private static bool TryApex(
            Diag a, Diag b, double join,
            out double apexX, out double apexY,
            out double baseX1, out double baseY1, out double baseX2, out double baseY2)
        {
            if (Near(a.X1, a.Y1, b.X1, b.Y1, join))
            {
                apexX = a.X1; apexY = a.Y1;
                baseX1 = a.X2; baseY1 = a.Y2;
                baseX2 = b.X2; baseY2 = b.Y2;
                return true;
            }

            if (Near(a.X1, a.Y1, b.X2, b.Y2, join))
            {
                apexX = a.X1; apexY = a.Y1;
                baseX1 = a.X2; baseY1 = a.Y2;
                baseX2 = b.X1; baseY2 = b.Y1;
                return true;
            }

            if (Near(a.X2, a.Y2, b.X1, b.Y1, join))
            {
                apexX = a.X2; apexY = a.Y2;
                baseX1 = a.X1; baseY1 = a.Y1;
                baseX2 = b.X2; baseY2 = b.Y2;
                return true;
            }

            if (Near(a.X2, a.Y2, b.X2, b.Y2, join))
            {
                apexX = a.X2; apexY = a.Y2;
                baseX1 = a.X1; baseY1 = a.Y1;
                baseX2 = b.X1; baseY2 = b.Y1;
                return true;
            }

            apexX = apexY = baseX1 = baseY1 = baseX2 = baseY2 = 0;
            return false;
        }

        /// <summary>两点是否在同一个容差内（同一个端点）。</summary>
        private static bool Near(double x1, double y1, double x2, double y2, double tol)
        {
            return Math.Abs(x1 - x2) <= tol && Math.Abs(y1 - y2) <= tol;
        }

        /// <summary>点是否在三角形内（三条边的同侧判定，含边界）。</summary>
        private static bool PointInTriangle(
            double px, double py,
            double x1, double y1, double x2, double y2, double x3, double y3)
        {
            double d1 = Side(px, py, x1, y1, x2, y2);
            double d2 = Side(px, py, x2, y2, x3, y3);
            double d3 = Side(px, py, x3, y3, x1, y1);

            bool negative = d1 < 0 || d2 < 0 || d3 < 0;
            bool positive = d1 > 0 || d2 > 0 || d3 > 0;

            return !(negative && positive);
        }

        /// <summary>点相对有向边 (ax,ay)→(bx,by) 的方位（叉积符号）。</summary>
        private static double Side(
            double px, double py, double ax, double ay, double bx, double by)
        {
            return (px - bx) * (ay - by) - (ax - bx) * (py - by);
        }

        // ── 单元格枚举 ────────────────────────────────────────────────────────

        /// <summary>
        /// 一个候选单元格：由上下两条横线 + 左右两条竖线围出的最小闭合金框。
        /// 坐标为 PDF 单位（pt，Y 向上，左边 X1 &lt; X2，下边 Y1 &lt; Y2）。
        ///
        /// internal 而不是 private：探针（_test\CsvProbe）要直接断言「文字笔画假格子不能挤掉真格子」
        /// 这条流水线，免得照抄一份逻辑去测。
        /// </summary>
        internal class Cell
        {
            public double X1;
            public double X2;
            public double Y1;
            public double Y2;

            public string Text = string.Empty;
            public int WordCount;

            /// <summary>这一格里被划线（取り消し線）划掉的词数。</summary>
            public int StruckWords;
        }

        private static double CellArea(Cell c)
        {
            return Math.Max(0, c.X2 - c.X1) * Math.Max(0, c.Y2 - c.Y1);
        }

        /// <summary>
        /// 枚举所有「最小闭合矩形」= 单元格。
        ///
        /// 做法：以每两条横线（上 <paramref name="horizontals"/>[i] 的 y、下 [j] 的 y）确定一条水平带，
        /// 取带内两条纵向完全跨越该带的竖线作为左右边，得到一个候选矩形；
        /// 但若带内还有横线横穿整个候选宽度，说明这个矩形其实是被横线分隔的几格拼起来的，
        /// 不是**一个**四边闭合的格子 → 丢弃（这样每个格子只会被最贴身的四条边围出来一次）。
        /// </summary>
        private static List<Cell> FindCells(
            List<Seg> horizontals, List<Seg> verticals, double tol, double minCell, double maxRowGap)
        {
            var cells = new List<Cell>();

            var vx = new List<double>(verticals.Count);
            foreach (var v in verticals) vx.Add(v.P);   // 已按 x 升序

            var cross = new List<int>();   // 带内竖线的下标

            for (int i = 0; i < horizontals.Count; i++)
            {
                Seg top = horizontals[i];

                for (int j = i + 1; j < horizontals.Count; j++)
                {
                    Seg bottom = horizontals[j];

                    double height = bottom.P - top.P;
                    if (height <= tol) continue;
                    if (height > maxRowGap) break;            // 横线按 y 升序，再往后只会更远

                    // 上下两条横线的公共 x 区间
                    double xa = Math.Max(top.A, bottom.A);
                    double xb = Math.Min(top.B, bottom.B);
                    if (xb - xa < minCell) continue;

                    // 带内纵向完全跨越的竖线
                    cross.Clear();
                    for (int k = LowerBound(vx, xa - tol); k < verticals.Count; k++)
                    {
                        if (vx[k] > xb + tol) break;

                        Seg v = verticals[k];
                        if (v.A > top.P + tol || v.B < bottom.P - tol) continue;

                        cross.Add(k);
                    }

                    if (cross.Count < 2) continue;

                    for (int k = 0; k + 1 < cross.Count; k++)
                    {
                        double ca = verticals[cross[k]].P;
                        double cb = verticals[cross[k + 1]].P;

                        if (cb - ca < minCell) continue;

                        // 内部有横线横穿整个宽度 → 这是几格拼起来的空档，不是单元格
                        if (HasHorizontalInside(horizontals, i, j, ca, cb, tol)) continue;

                        cells.Add(new Cell { X1 = ca, X2 = cb, Y1 = top.P, Y2 = bottom.P });
                    }
                }
            }

            return cells;
        }

        /// <summary>
        /// 第 i 条与第 j 条横线之间，是否存在一条横线覆盖了 [xa, xb] 的整个宽度。
        /// </summary>
        private static bool HasHorizontalInside(
            List<Seg> horizontals, int topIndex, int bottomIndex, double xa, double xb, double tol)
        {
            for (int k = topIndex + 1; k < bottomIndex; k++)
            {
                Seg h = horizontals[k];

                if (h.P >= horizontals[bottomIndex].P - tol) break;

                if (h.A <= xa + tol && h.B >= xb - tol) return true;
            }

            return false;
        }

        /// <summary>升序数组里第一个 ≥ value 的下标。</summary>
        private static int LowerBound(List<double> sorted, double value)
        {
            int lo = 0, hi = sorted.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (sorted[mid] < value) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        // ── 单元格归组（相邻 → 同一张表）──────────────────────────────────────

        /// <summary>
        /// 剔掉「拼不成表的孤立矩形」。
        ///
        /// 文字轮廓（「図」「要」这类笔画多的字）偶尔会凑出四条边，被 <see cref="FindCells"/> 当成一个候选格子。
        /// 它本身不会变成表（一张表至少要 <see cref="MinTableCells"/> 格），但如果留着，下一步
        /// <see cref="RemoveContainers"/> 会把包住它的<b>真格子</b>当成「套娃」删掉 —— 真格子一没，
        /// 落在里面的一整段文字就再没有归属，只能整段丢掉（H310U10004 表題欄 F 行的
        /// 「枠 ブレーキ部品取付/調整ラベル・注意ラベル貼付要領図」就是这样丢的）。
        ///
        /// 判据：与其余任何候选都不共边相邻 → 不属于任何一张表 → 不是格子。
        /// </summary>
        /// <returns>剔掉的候选数。</returns>
        internal static int DropIsolatedCells(List<Cell> cells, double tol)
        {
            if (cells.Count < 2) return 0;

            List<List<Cell>> groups = GroupCells(cells, tol);

            var keep = new List<Cell>(cells.Count);

            foreach (var g in groups)
                if (g.Count >= MinTableCells)
                    keep.AddRange(g);

            int dropped = cells.Count - keep.Count;
            if (dropped <= 0) return 0;

            cells.Clear();
            cells.AddRange(keep);
            return dropped;
        }

        /// <summary>
        /// 删掉「包住别的候选矩形」的候选：合法单元格是按最小闭合矩形枚举出来的，不会套娃；
        /// 出现在这里说明是边界不整齐（两条表相邻、缺线）拼出来的大矩形，属于误判。
        /// </summary>
        internal static void RemoveContainers(List<Cell> cells, double tol)
        {
            if (cells.Count < 2) return;

            var drop = new bool[cells.Count];

            for (int i = 0; i < cells.Count; i++)
            {
                for (int j = 0; j < cells.Count; j++)
                {
                    if (i == j || drop[i]) continue;

                    Cell a = cells[i], b = cells[j];

                    // b 必须「整个装进」a，且至少有一个方向真的更小
                    bool inside = a.X1 <= b.X1 + tol && a.X2 >= b.X2 - tol
                                  && a.Y1 <= b.Y1 + tol && a.Y2 >= b.Y2 - tol;
                    if (!inside) continue;

                    bool strictlyBigger = (a.X2 - a.X1) > (b.X2 - b.X1) + tol * 2
                                          || (a.Y2 - a.Y1) > (b.Y2 - b.Y1) + tol * 2;
                    if (strictlyBigger) { drop[i] = true; break; }
                }
            }

            for (int i = cells.Count - 1; i >= 0; i--)
                if (drop[i]) cells.RemoveAt(i);
        }

        /// <summary>两个单元格是否共边相邻（共用一条边界，且在边界方向上有重叠）。</summary>
        private static bool AreAdjacent(Cell a, Cell b, double tol)
        {
            double xOverlap = Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1);
            double yOverlap = Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1);

            if (xOverlap > tol
                && (Math.Abs(a.Y2 - b.Y1) <= tol || Math.Abs(b.Y2 - a.Y1) <= tol))
                return true;

            if (yOverlap > tol
                && (Math.Abs(a.X2 - b.X1) <= tol || Math.Abs(b.X2 - a.X1) <= tol))
                return true;

            return false;
        }

        /// <summary>
        /// 把相邻的单元格归成一张表（并查集传递闭包）。
        /// 分开的两张表哪怕挨得很近，只要中间没有共用边界就会被分成两张。
        /// </summary>
        private static List<List<Cell>> GroupCells(List<Cell> cells, double tol)
        {
            var groups = new List<List<Cell>>();
            if (cells.Count == 0) return groups;

            int n = cells.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (!AreAdjacent(cells[i], cells[j], tol)) continue;

                    int ri = Find(parent, i), rj = Find(parent, j);
                    if (ri != rj) parent[rj] = ri;
                }
            }

            var index = new Dictionary<int, int>();
            for (int i = 0; i < n; i++)
            {
                int root = Find(parent, i);
                int at;
                if (!index.TryGetValue(root, out at))
                {
                    at = groups.Count;
                    index[root] = at;
                    groups.Add(new List<Cell>());
                }
                groups[at].Add(cells[i]);
            }

            return groups;
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];   // 路径压缩
                i = parent[i];
            }
            return i;
        }

        /// <summary>
        /// 表的排序：自上而下，同一水平高度带内自左而右。
        /// 用「高度带」分桶而不是直接比较 y 差值，是为了保证比较函数可传递
        /// （List.Sort 遇到不传递的比较器会直接抛异常）。
        /// </summary>
        private static int ByPosition(RuledTable a, RuledTable b)
        {
            int bandA = a.PixelY / PositionBandPx;
            int bandB = b.PixelY / PositionBandPx;

            if (bandA != bandB) return bandA.CompareTo(bandB);

            return a.PixelX.CompareTo(b.PixelX);
        }

        /// <summary>排序时把纵向相差很小的表当作同一高度带（像素）。</summary>
        private const int PositionBandPx = 24;

        // ── 组装表格模型 ──────────────────────────────────────────────────────

        /// <summary>
        /// 把一个单元格集合组装成 <see cref="RuledTable"/>：由边界坐标给出准确的行列号与合并跨度。
        /// 文字太少 / 单元格太少的组直接返回 null（零件轮廓、图框之类的封闭矩形走到这里会被丢掉）。
        /// </summary>
        private static RuledTable BuildTable(
            List<Cell> cells, double tol, double pageH, double scaleX, double scaleY, double mmPerPx)
        {
            if (cells == null || cells.Count < MinTableCells) return null;

            int chars = 0;
            foreach (var c in cells)
                if (!string.IsNullOrEmpty(c.Text)) chars += c.Text.Trim().Length;

            if (chars < MinTableChars) return null;

            // 行 / 列边界：所有单元格左右、上下边的聚类（容差内的当作同一条边界）
            var xs = new List<double>();
            var ys = new List<double>();
            foreach (var c in cells)
            {
                xs.Add(c.X1); xs.Add(c.X2);
                ys.Add(c.Y1); ys.Add(c.Y2);
            }

            List<double> bx = ClusterSorted(xs, tol);
            List<double> by = ClusterSorted(ys, tol);

            if (bx.Count < 2 || by.Count < 2) return null;

            int columns = bx.Count - 1;
            int rows = by.Count - 1;

            var table = new RuledTable { RowCount = rows, ColumnCount = columns };
            var placed = new List<RuledTableCell>();

            foreach (var c in cells)
            {
                int left = NearestIndex(bx, c.X1);
                int right = NearestIndex(bx, c.X2);
                int bottom = NearestIndex(by, c.Y1);
                int top = NearestIndex(by, c.Y2);

                if (right <= left || top <= bottom) continue;

                var cell = new RuledTableCell
                {
                    Column = left + 1,
                    ColumnSpan = right - left,
                    Row = rows - top + 1,   // by 按 y 升序，top 的下标越大越靠上
                    RowSpan = top - bottom,
                    Text = (c.Text ?? string.Empty).Trim(),
                    WordCount = c.WordCount,
                    StruckWordCount = c.StruckWords
                };

                SetPixelRect(cell, c.X1, c.X2, c.Y1, c.Y2, pageH, scaleX, scaleY, mmPerPx);
                placed.Add(cell);
            }

            if (placed.Count < MinTableCells) return null;

            // 按行归位
            var rowMap = new Dictionary<int, RuledTableRow>();
            foreach (var cell in placed)
            {
                RuledTableRow row;
                if (!rowMap.TryGetValue(cell.Row, out row))
                {
                    row = new RuledTableRow { Index = cell.Row };
                    rowMap[cell.Row] = row;
                    table.Rows.Add(row);
                }
                row.Cells.Add(cell);
            }

            table.Rows.Sort((a, b) => a.Index.CompareTo(b.Index));

            MarkStruck(table);

            FillRects(table, mmPerPx);

            return table;
        }

        /// <summary>
        /// 划线（取り消し線）的两档语义 —— 「品番作废」不等于「明细作废」：
        ///
        ///   ① <b>整行划掉</b>：行内有 <b>≥2 格</b>被划线穿过（CAD 图里整行作废就是横着划一道，
        ///      各列文字高低不同，一条线常同时盖住好几列）→ 该行作废（<see cref="RuledTableRow.IsStruck"/>），
        ///      不写进 <c>tables\*.csv</c> / 二维表 —— 与旧行为一致；
        ///   ② <b>只划掉品番那一格</b>：行内只有 1 格被划，且那一格写的是品番 / 図番形态的文字
        ///      （见 <see cref="IsPartCodeCell"/>，典型就是 <c>部品コード</c> 列）→ 图纸上表示
        ///      **这个品番作废、这一条明细仍然有效**：该格输出成空（原文存
        ///      <see cref="RuledTableCell.StruckText"/>），行照常输出、<c>IsStruck</c> 保持 false。
        ///      下游 <c>01_アッセンブリ図_部品機能情報.csv</c> 因此得到「部品コード 空、其余列都在」，
        ///      而不是整条明细消失（H986U10001 就是这样）。
        ///
        /// 为什么不用「被划的格子是不是表头写着部品コード 的那一列」一条件：有的部品表那一列表头是空的
        /// （合并表头 / 漏字），按形态判（字母 + 数字）更稳；两个判据是「或」的关系。
        /// 为什么日文格不这么处理：<c>H310U10004</c> 表題欄的
        /// 「枠 建て付け注意ラベル貼付要領図」被划掉是**整行作废**，那一格是日文、不像品番，
        /// 仍按 ① 整行剔除。
        /// </summary>
        /// <param name="table">已按行归位、尚未填外接框的表。</param>
        private static void MarkStruck(RuledTable table)
        {
            if (table == null || table.Rows.Count == 0) return;

            int codeColStart, codeColEnd;
            FindPartCodeColumn(table, out codeColStart, out codeColEnd);

            foreach (var row in table.Rows)
            {
                List<RuledTableCell> struckCells = null;
                int textCells = 0;

                foreach (var cell in row.Cells)
                {
                    if (string.IsNullOrEmpty(cell.Text)) continue;

                    textCells++;

                    if (!cell.IsStruck) continue;

                    if (struckCells == null) struckCells = new List<RuledTableCell>();
                    struckCells.Add(cell);
                }

                if (struckCells == null) continue;

                // ② 只划掉品番那一格 → 该格输出清空、行保留
                if (struckCells.Count == 1 && textCells >= 2
                    && IsPartCodeCell(struckCells[0], codeColStart, codeColEnd))
                {
                    RuledTableCell only = struckCells[0];

                    // 原文留在 Text 里（JSON / txt 明细仍能看到划掉的是什么），
                    // 下游输出（ToGrid → tables\*.csv、二维表）把它当空格子 —— 见 RuledTable.ToGrid
                    only.StruckText = only.Text;
                    continue;
                }

                // ① 其余情形：整行作废
                row.IsStruck = true;
            }
        }

        /// <summary>
        /// 按表头认品番列时向下扫的行数：正常在第 1 行，合并表头把它压到第 2 / 3 行的图纸也有；
        /// 只看表头这几行，避免明细格里出现的「型式」之类的词被当成列名。
        /// </summary>
        private const int PartCodeHeaderScanRows = 3;

        /// <summary>
        /// 按表头认「品番列」：整张表里最靠上的一格标题写成 部品コード / 部品番号 / 品番 / 型式 / 型番
        /// （同义写法见 <see cref="HeaderNames.PartCode"/>）的那一列。
        /// 认不出时两个输出都是 0（此时只看文字形态，见 <see cref="LooksLikePartCode"/>）。
        /// </summary>
        private static void FindPartCodeColumn(RuledTable table, out int colStart, out int colEnd)
        {
            colStart = 0;
            colEnd = 0;

            foreach (var row in table.Rows)
            {
                if (row.Index > PartCodeHeaderScanRows) break;

                foreach (var cell in row.Cells)
                {
                    if (!HeaderNames.IsMatch(cell.Text, HeaderNames.PartCode)) continue;

                    colStart = cell.Column;
                    colEnd = cell.Column + Math.Max(1, cell.ColumnSpan) - 1;
                    return;
                }
            }
        }

        /// <summary>
        /// 这一格写的是不是「品番 / 図番」——决定「只划掉这一格」时要不要保留整行。
        /// 判据任一即可：
        ///   ① 这一列是表头认出来的品番列（<see cref="FindPartCodeColumn"/>）；
        ///   ② 格内文字同时含英文字母和数字（<c>MDU762</c> / <c>MZC△△65</c> / <c>H310U10025</c>）——
        ///      日文的部品名、員数、枠区分 都不满足，表題欄那种「整行划掉」的划线不会被误判。
        /// </summary>
        private static bool IsPartCodeCell(RuledTableCell cell, int colStart, int colEnd)
        {
            if (colStart > 0)
            {
                int left = cell.Column;
                int right = cell.Column + Math.Max(1, cell.ColumnSpan) - 1;

                if (right >= colStart && left <= colEnd) return true;
            }

            return LooksLikePartCode(cell.Text);
        }

        /// <summary>文字里同时有英文字母和数字 → 当成品番 / 図番形态。</summary>
        private static bool LooksLikePartCode(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            bool letter = false, digit = false;

            foreach (char ch in text)
            {
                if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z')) letter = true;
                else if (ch >= '0' && ch <= '9') digit = true;

                if (letter && digit) return true;
            }

            return false;
        }

        /// <summary>
        /// 补齐行 / 表的外接框：行 = 该行所有单元格的并集，表 = 所有单元格的并集。
        /// </summary>
        private static void FillRects(RuledTable table, double mmPerPx)
        {
            int tx1 = int.MaxValue, ty1 = int.MaxValue, tx2 = int.MinValue, ty2 = int.MinValue;

            foreach (var row in table.Rows)
            {
                row.Cells.Sort((a, b) => a.Column.CompareTo(b.Column));

                int rx1 = int.MaxValue, ry1 = int.MaxValue, rx2 = int.MinValue, ry2 = int.MinValue;

                foreach (var c in row.Cells)
                {
                    if (c.PixelX < rx1) rx1 = c.PixelX;
                    if (c.PixelY < ry1) ry1 = c.PixelY;
                    if (c.PixelX + c.PixelW > rx2) rx2 = c.PixelX + c.PixelW;
                    if (c.PixelY + c.PixelH > ry2) ry2 = c.PixelY + c.PixelH;
                }

                if (row.Cells.Count == 0) continue;

                row.PixelX = rx1;
                row.PixelY = ry1;
                row.PixelW = rx2 - rx1;
                row.PixelH = ry2 - ry1;

                if (rx1 < tx1) tx1 = rx1;
                if (ry1 < ty1) ty1 = ry1;
                if (rx2 > tx2) tx2 = rx2;
                if (ry2 > ty2) ty2 = ry2;
            }

            if (tx1 == int.MaxValue) return;

            table.PixelX = tx1;
            table.PixelY = ty1;
            table.PixelW = tx2 - tx1;
            table.PixelH = ty2 - ty1;

            table.PdfX_mm = Math.Round(tx1 * mmPerPx, 2);
            table.PdfY_mm = Math.Round(ty1 * mmPerPx, 2);
            table.PdfW_mm = Math.Round(table.PixelW * mmPerPx, 2);
            table.PdfH_mm = Math.Round(table.PixelH * mmPerPx, 2);
        }

        /// <summary>PDF 坐标（pt，Y 向上）→ 页面像素（左上原点）+ 毫米坐标。</summary>
        private static void SetPixelRect(
            RuledTableCell cell, double x1, double x2, double y1, double y2,
            double pageH, double scaleX, double scaleY, double mmPerPx)
        {
            cell.PixelX = (int)Math.Round(x1 * scaleX);
            cell.PixelY = (int)Math.Round((pageH - y2) * scaleY);
            cell.PixelW = Math.Max(1, (int)Math.Round((x2 - x1) * scaleX));
            cell.PixelH = Math.Max(1, (int)Math.Round((y2 - y1) * scaleY));

            cell.PdfX_mm = Math.Round(cell.PixelX * mmPerPx, 2);
            cell.PdfY_mm = Math.Round(cell.PixelY * mmPerPx, 2);
            cell.PdfW_mm = Math.Round(cell.PixelW * mmPerPx, 2);
            cell.PdfH_mm = Math.Round(cell.PixelH * mmPerPx, 2);
        }

        /// <summary>升序排序后把差值 ≤ <paramref name="tol"/> 的值并成一个（取平均）。</summary>
        private static List<double> ClusterSorted(List<double> values, double tol)
        {
            values.Sort();

            var result = new List<double>();
            var counts = new List<int>();

            foreach (var v in values)
            {
                int last = result.Count - 1;

                if (last >= 0 && v - result[last] <= tol)
                {
                    int n = counts[last];
                    result[last] = (result[last] * n + v) / (n + 1);
                    counts[last] = n + 1;
                }
                else
                {
                    result.Add(v);
                    counts.Add(1);
                }
            }

            return result;
        }

        /// <summary>升序数组里与 value 最接近的下标。</summary>
        private static int NearestIndex(List<double> sorted, double value)
        {
            int i = LowerBound(sorted, value);

            if (i <= 0) return 0;
            if (i >= sorted.Count) return sorted.Count - 1;

            return (value - sorted[i - 1]) <= (sorted[i] - value) ? i - 1 : i;
        }

        // ── 文字填料 ──────────────────────────────────────────────────────────

        /// <summary>文字层里的一个词（PDF 单位坐标 + 页面像素坐标）。internal 供探针构造测试用。</summary>
        internal class WordBox
        {
            public string Text;
            public double X1;   // 左
            public double X2;   // 右
            public double Y1;   // 下
            public double Y2;   // 上
        }

        /// <summary>把页面文字层里切好的词转成 <see cref="WordBox"/>（剔除空白词与零尺寸框）。</summary>
        private static List<WordBox> ReadWords(IReadOnlyList<Word> pageWords)
        {
            var list = new List<WordBox>();

            if (pageWords == null) return list;

            foreach (var w in pageWords)
            {
                string text = (w.Text ?? string.Empty).Trim();
                if (text.Length == 0) continue;

                var bb = w.BoundingBox;
                if (bb.Right - bb.Left <= 0 || bb.Top - bb.Bottom <= 0) continue;

                list.Add(new WordBox
                {
                    Text = text,
                    X1 = bb.Left,
                    X2 = bb.Right,
                    Y1 = bb.Bottom,
                    Y2 = bb.Top
                });
            }

            return list;
        }

        /// <summary>
        /// 把「被 取り消し線 划掉的词」按页面像素坐标记进 <paramref name="into"/>，返回记了几个。
        ///
        /// 与表格无关：图纸上没画表格、把要領図索引写成自由文字的組立図（H527U10001）也可能把其中一条
        /// （<c>J部詳細：H325U10017</c>）划掉 —— 那时没有 <c>tables_info.json</c> 可查，
        /// 下游 <c>01_アッセンブリ図_要領図.csv</c> 只能靠这份清单知道哪一条已作废（见 <see cref="StruckWordMark"/>）。
        ///
        /// <paramref name="into"/> 为 null（调用方不关心）时什么都不做。
        /// </summary>
        private static int CollectStruckPageWords(
            List<StruckWordMark> into, List<WordBox> words, bool[] struck,
            double pageH, double scaleX, double scaleY)
        {
            if (into == null || words == null || struck == null)
                return 0;

            int added = 0;

            for (int i = 0; i < words.Count && i < struck.Length; i++)
            {
                if (!struck[i]) continue;

                WordBox w = words[i];

                into.Add(new StruckWordMark
                {
                    Text = w.Text,
                    PixelX = (int)Math.Round(w.X1 * scaleX),
                    PixelY = (int)Math.Round((pageH - w.Y2) * scaleY),
                    PixelW = Math.Max(1, (int)Math.Round((w.X2 - w.X1) * scaleX)),
                    PixelH = Math.Max(1, (int)Math.Round((w.Y2 - w.Y1) * scaleY))
                });

                added++;
            }

            return added;
        }

        /// <summary>
        /// 把尚未归属的词填进单元格：词中心落在格子内（四条边框之内）就归该格。
        /// 完全不看文字间距，因此空单元格不会错位、合并单元格也不会被拆开。
        /// 被划线（取り消し線）划掉的词数一并记下 —— 上层据此判定「整行作废」。
        /// 「三角形注記符号里的数字」（<paramref name="inMark"/>，见 <see cref="MarkTriangleMarks"/>）
        /// 不是格子内容：直接跳过，也不占用 <paramref name="used"/>。
        /// </summary>
        /// <returns>填进去的词数。</returns>
        private static int FillCell(
            Cell cell, List<WordBox> words, bool[] used, bool[] struck, bool[] inMark)
        {
            List<WordBox> picked = null;
            int struckCount = 0;

            for (int i = 0; i < words.Count; i++)
            {
                if (used[i]) continue;

                // 三角形注記符号（△1）里的数字：图纸上的提醒记号，不是这一格的内容
                if (inMark != null && i < inMark.Length && inMark[i]) continue;

                WordBox w = words[i];
                double cx = (w.X1 + w.X2) / 2.0;
                double cy = (w.Y1 + w.Y2) / 2.0;

                if (cx < cell.X1 || cx > cell.X2) continue;
                if (cy < cell.Y1 || cy > cell.Y2) continue;

                used[i] = true;
                if (picked == null) picked = new List<WordBox>();
                picked.Add(w);

                if (struck != null && i < struck.Length && struck[i]) struckCount++;
            }

            if (picked == null) return 0;

            cell.Text = ComposeText(picked);
            cell.WordCount = picked.Count;
            cell.StruckWords = struckCount;
            return picked.Count;
        }

        /// <summary>
        /// 剔掉「同一个词被重叠画了两遍」的重复（两框几乎完全重合）—— 否则一格里的
        /// <c>部品コード</c> 会被拼成 <c>部部品品ココーードド</c>，下游按列名认不出这一列
        /// （H467U10008 的 02 就是这么丢的「部品コード」）。
        ///
        /// 判据见 <see cref="OverlapWordRatio"/>：文本相同 + x / y 两个方向的重叠都超过短边 60%。
        /// 只留最先出现的那一个；没有重复时原样返回（不改原有拼接结果）。
        /// </summary>
        private static List<WordBox> DropOverlapping(List<WordBox> words)
        {
            if (words.Count < 2) return words;

            var keep = new List<WordBox>(words.Count);

            foreach (var w in words)
            {
                bool dup = false;

                for (int i = 0; i < keep.Count; i++)
                {
                    WordBox k = keep[i];

                    if (!string.Equals(k.Text, w.Text, StringComparison.Ordinal)) continue;

                    if (OverlapRatio(k.X1, k.X2, w.X1, w.X2) < OverlapWordRatio) continue;
                    if (OverlapRatio(k.Y1, k.Y2, w.Y1, w.Y2) < OverlapWordRatio) continue;

                    dup = true;
                    break;
                }

                if (!dup) keep.Add(w);
            }

            return keep.Count == words.Count ? words : keep;
        }

        /// <summary>
        /// 统计该页有多少个词是「同一个词被重叠画了两遍」（判据与 <see cref="DropOverlapping"/> 一致）。
        /// 只用于日志 —— 图纸有这个问题时，跑完一眼能看到是哪一页、影响了多少词。
        /// </summary>
        private static int CountOverlappingDuplicates(List<WordBox> words)
        {
            if (words.Count < 2) return 0;

            var byText = new Dictionary<string, List<WordBox>>(StringComparer.Ordinal);

            foreach (var w in words)
            {
                List<WordBox> list;
                if (!byText.TryGetValue(w.Text, out list))
                {
                    list = new List<WordBox>();
                    byText[w.Text] = list;
                }

                list.Add(w);
            }

            int dup = 0;

            foreach (var list in byText.Values)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    WordBox a = list[i];

                    for (int j = i + 1; j < list.Count; j++)
                    {
                        WordBox b = list[j];

                        if (OverlapRatio(a.X1, a.X2, b.X1, b.X2) < OverlapWordRatio) continue;
                        if (OverlapRatio(a.Y1, a.Y2, b.Y1, b.Y2) < OverlapWordRatio) continue;

                        dup++;      // 同一个词画了几遍都只算一次
                        break;
                    }
                }
            }

            return dup;
        }

        /// <summary>两个区间重叠的长度占「短的那一个」的比例（并排时为 0，完全重合时为 1）。</summary>
        private static double OverlapRatio(double a1, double a2, double b1, double b2)
        {
            double shorter = Math.Min(a2 - a1, b2 - b1);

            if (shorter <= 0) return 0;

            return (Math.Min(a2, b2) - Math.Max(a1, b1)) / shorter;
        }

        /// <summary>
        /// 把一格内的词拼成文字：先按行（y 中心相近）分组、行内按 x 排序，
        /// 词间距明显（一般大于字高 1/4）时补一个空格，换行也补一个空格。
        ///
        /// 空格判据为什么要两套：CAD 里常见「压扁排字」的长标签（字高不变、字宽只有一半），
        /// 按字高判会把本该有的空格吃掉 —— H310U10004 表題欄 F 行就是「枠 ブレーキ部品…」
        /// 被拼成「枠ブレーキ部品…」。所以再按本格的平均字宽取一次比例，两者取小（并留字高 1/8 的下限）。
        /// </summary>
        internal static string ComposeText(List<WordBox> words)
        {
            // 同一个词被重叠画了两遍时只留一个（否则「部品コード」会拼成 部部品品ココーードド）
            var ordered = DropOverlapping(new List<WordBox>(words));

            ordered.Sort((a, b) =>
            {
                double ca = (a.Y1 + a.Y2) / 2.0;
                double cb = (b.Y1 + b.Y2) / 2.0;
                double h = Math.Min(a.Y2 - a.Y1, b.Y2 - b.Y1);
                if (h <= 0) h = 1.0;

                if (Math.Abs(ca - cb) > h * 0.5) return cb.CompareTo(ca);   // 上 → 下

                return a.X1.CompareTo(b.X1);
            });

            // 本格的平均字宽（字身宽 / 字数，按格内全部词统计）
            double sumWidth = 0;
            int sumChars = 0;

            foreach (var w in ordered)
            {
                int n = w.Text == null ? 0 : w.Text.Length;
                if (n <= 0) continue;

                sumWidth += w.X2 - w.X1;
                sumChars += n;
            }

            double avgAdv = sumChars > 0 ? sumWidth / sumChars : 0;

            var sb = new StringBuilder();
            double prevRight = 0;
            double prevCenter = 0;
            bool hasPrev = false;

            foreach (var w in ordered)
            {
                double cy = (w.Y1 + w.Y2) / 2.0;
                double h = Math.Max(w.Y2 - w.Y1, 1.0);

                double spaceGap = h * 0.25;

                if (avgAdv > 0)
                    spaceGap = Math.Min(spaceGap, Math.Max(avgAdv * 0.30, h * 0.125));

                bool sameLine = hasPrev && Math.Abs(cy - prevCenter) <= h * 0.5;

                if (hasPrev && (!sameLine || w.X1 - prevRight > spaceGap))
                    sb.Append(' ');

                sb.Append(w.Text);

                prevRight = w.X2;
                prevCenter = cy;
                hasPrev = true;
            }

            // 兜底：重复画的若是「一个词里的每个字符」（词级判据看不到），整格双写的串在这里还原一次
            // （部部品品ココーードド → 部品コード；判据见 CsvFile.FoldDouble，重复字 / 数字的真品番不受影响）
            return CsvFile.FoldDouble(sb.ToString());
        }
    }
}
