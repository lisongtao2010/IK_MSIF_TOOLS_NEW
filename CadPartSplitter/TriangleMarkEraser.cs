using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;

namespace CadPartSplitter
{
    /// <summary>
    /// 从「渲染用的 PDF」里删掉图纸上的三角形注記符号（△1 / △2 … 改版标记），
    /// 做法参照 <c>triangle_eraser.py</c>（PyMuPDF + redaction），只是换成 PDFium。
    ///
    /// <para>
    /// 为什么需要：这类符号是 <b>CAD 画进页面内容流</b>的普通矢量图形 + 文字，
    /// 不是 PDF 注释（annotation），<see cref="PdfAnnotationStripper"/> 的
    /// <c>FPDFPage_RemoveAnnot</c> 删不到它们。它渲染出来就是一个实心 / 空心三角
    /// 加一位数字，规模与零件图上的尺寸标注相当 —— 会把相邻视图连成一片，
    /// 三角形里的数字还会混进邻近表格格子的文字（<c>切断寸法</c> 读成 <c>100 1</c>）。
    /// </para>
    ///
    /// <para>
    /// 检测用 PdfPig（与 <see cref="RuledTableExtractor"/> 的 <c>MarkTriangleMarks</c> 同一套判据）：
    /// <list type="number">
    ///   <item>矢量层：全部路径（含 Form XObject，坐标已应用 CTM）扁平化成线段，
    ///         斜边（两个方向都超过 <see cref="LineTolerancePt"/>、长度 ≥ <see cref="MinLinePt"/>）收进候选表；</item>
    ///   <item>文字层：1~3 位纯数字的词，在它周围 3 倍字高的窗口里找两条斜边 ——
    ///         有公共端点（容差 1/4 字高）、另两端近似等高（底边水平，容差 1/2 字高）、
    ///         底宽 0.5~3 倍字高、三角形高 0.8~4 倍字高、数字中心落在三角形内；</item>
    ///   <item>全部满足才算 △ 注記符号 —— 文字笔画、引线、箭头凑不出这个组合，
    ///         所以不需要「等边」这种在 CAD 里常不成立的约束（py 版要求等边 + 同一 path 内 3 条线，
    ///         等腰的 △ 和拆成三个对象的边都会漏）。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// 擦除：在页面坐标里插一块不透明白矩形（外扩 <see cref="ErasePaddingPt"/> pt），
    /// 等价于 py 版 <c>add_redact_annot(fill=(1,1,1))</c> + <c>apply_redactions()</c> ——
    /// 白矩形落在内容流末尾，渲染时把三角形和里面的数字一起盖住，画在 Form XObject 里的也盖得住。
    /// 最后 <c>FPDFPage_GenerateContent</c> 生成内容流、<c>FPDF_SaveAsCopy</c> 另存
    /// <c>&lt;输出目录&gt;\&lt;图纸名&gt;_clean.pdf</c>。
    ///
    /// 为什么不删对象：三角形可能整条画在 Form XObject 里、也可能三条边是三个独立对象，
    /// 页面级对象接口（<c>FPDFPage_RemoveObject</c>）既看不进 form 也删不了拆开的边；
    /// 白矩形在页面坐标里直接盖，覆盖 form 里的图形没有任何问题，与 py 的 redaction 等价。
    /// </para>
    ///
    /// <para>
    /// 与 <see cref="PdfAnnotationStripper"/> 的关系：那一步先删注释并另存 <c>_clean.pdf</c>，
    /// 本步骤在它的输出上再「读 → 盖白块 → 另存」，文件名不变（覆盖同一份 <c>_clean.pdf</c>），
    /// 所以渲染（<see cref="PdfRenderer"/>）拿到的始终是同一路径。文字 / 表格 / 圈号分析仍读原 PDF，
    /// 不受影响。任何失败只告警并返回传入的路径，绝不让预处理把识别流程搞崩。
    /// </para>
    ///
    /// <para>
    /// 代价与限制：clean.pdf 的文字层里那个数字还在（渲染时被白块盖住）——
    /// 表格提取本来就有 <c>MarkTriangleMarks</c> 把它剔掉，不影响 CSV。
    /// 扫描件（没有文字层）认不出，日志里会是「没找到」。
    /// </para>
    /// </summary>
    internal static class TriangleMarkEraser
    {
        /// <summary>干净副本的文件名后缀（与 <see cref="PdfAnnotationStripper.CleanSuffix"/> 相同）。</summary>
        public const string CleanSuffix = "_clean";

        // ── 几何判据（与 RuledTableExtractor.MarkTriangleMarks 一致，单位都是「字高的倍数」）──

        /// <summary>三角形边长下限：比里面的字还短的斜线不算边。</summary>
        private const double MinSideRatio = 1.0;

        /// <summary>三角形边长上限：再长就是引线 / 视图框，不是把小数字框起来的记号。</summary>
        private const double MaxSideRatio = 4.0;

        /// <summary>两条斜边「公共顶点重合」的容差。</summary>
        private const double ApexJoinRatio = 0.25;

        /// <summary>顶点 / 底边端点的搜索窗口：数字词框向外扩几倍字高。</summary>
        private const double SearchPadRatio = 3.0;

        /// <summary>底边「水平」的容差。</summary>
        private const double BaseLevelRatio = 0.5;

        /// <summary>底宽下限。</summary>
        private const double BaseMinRatio = 0.5;

        /// <summary>底宽上限。</summary>
        private const double BaseMaxRatio = 3.0;

        /// <summary>三角形高的下限。</summary>
        private const double HeightMinRatio = 0.8;

        /// <summary>三角形高的上限。</summary>
        private const double HeightMaxRatio = 4.0;

        // ── 线 / 擦除参数（单位 pt = 1/72 英寸）──

        /// <summary>判定「水平 / 竖直」的坐标容差。</summary>
        private const double LineTolerancePt = 0.5;

        /// <summary>短于此长度的线段忽略（文字笔画碎线、箭头尖）。</summary>
        private const double MinLinePt = 2.0;

        /// <summary>白矩形向外扩展量，把三角形边线和里面的数字一起盖住。</summary>
        private const double ErasePaddingPt = 1.0;

        /// <summary>
        /// PDFium 的填充方式：<c>FPDF_FILLMODE_ALTERNATE</c>（实心填充）。
        /// 新建的路径对象默认「不填充、不描边」（见 PDFium 的 <c>CPDF_PathObject</c>），
        /// 不显式指定就画不出白块，也盖不住任何东西。
        /// </summary>
        private const int FillModeAlternate = 1;

        /// <summary>贝塞尔曲线扁平化时每段的采样数（与 RuledTableExtractor 一致）。</summary>
        private const int BezierSamples = 8;

        /// <summary>框内数字的位数下限（改版编号 1~3 位，与 MarkTriangleMarks 一致）。</summary>
        private const int MinDigitLength = 1;

        /// <summary>框内数字的位数上限。</summary>
        private const int MaxDigitLength = 3;

        private const string PdfiumDll = "pdfium.dll";

        /// <summary>PDFium 的另存回调只有一个全局输出流，并发调用没有意义，串行化保险。</summary>
        private static readonly object SyncRoot = new object();

        /// <summary>回调当前写往哪个流（PDFium 的回调是静态函数指针，只能用静态字段传递）。</summary>
        private static FileStream _currentStream;

        /// <summary>
        /// 返回「渲染要用的 PDF 路径」：本页有 △ 注記符号 → 删掉后另存的
        /// <c>&lt;输出目录&gt;\&lt;图纸名&gt;_clean.pdf</c>；没有 / 非 PDF / 出错 → 原样返回 <paramref name="inputPath"/>。
        /// </summary>
        /// <param name="inputPath">
        /// 待处理的 PDF（通常是 <see cref="PdfAnnotationStripper.PrepareInput"/> 的结果：
        /// 已删注释的 <c>_clean.pdf</c>，或原 PDF）。非 PDF 直接返回。
        /// </param>
        /// <param name="opts">识别参数：<see cref="DetectorOptions.OutputDirectory"/> 必须已确定。</param>
        public static string PrepareInput(string inputPath, DetectorOptions opts)
        {
            if (opts == null || !opts.StripTriangleMarks)
                return inputPath;

            DetectorOptions o = opts;

            if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
                return inputPath;

            if (!string.Equals(Path.GetExtension(inputPath), ".pdf", StringComparison.OrdinalIgnoreCase))
                return inputPath;

            string outputDir = o.OutputDirectory;
            if (string.IsNullOrWhiteSpace(outputDir))
                return inputPath;

            int pageIndex = o.PdfPageIndex;

            // ── 检测：PdfPig 的路径（含 Form XObject）+ 文字层，判据见类注释 ──
            // 这里的输入是渲染用的 PDF（通常是已删注释的 _clean.pdf），
            // 与下游识别用的原 PDF 是两份文件，所以单独解析一次。
            List<TriangleBox> triangles;
            try
            {
                // PdfPageContent 的成功日志是写给「文字 / 圈号 / 表格共用一次解析」的，而这一步读的是
                // 渲染用的 PDF（通常是 _clean.pdf，与识别用的原 PDF 是两份文件）—— 原样打出去会让人
                // 以为原 PDF 被解析了两遍，所以只转发它的问题日志；词数 / 路径数由下面的
                // DetectTriangles 自己记（日志里已带）。
                PdfPageContent page = PdfPageContent.Load(inputPath, pageIndex, message =>
                {
                    if (!message.StartsWith("[INFO]", StringComparison.Ordinal))
                        o.Log(message);
                });

                triangles = DetectTriangles(page, pageIndex, o);
            }
            catch (Exception ex)
            {
                o.Log("[WARN] 三角形注記符号：检测失败（" + ex.GetType().Name + "：" + ex.Message + "），跳过。");
                return inputPath;
            }

            // 没命中：矢量 / 文字的统计已由 DetectTriangles 记过，这里直接返回原路径（不生成副本）
            if (triangles.Count == 0)
                return inputPath;

            // ── 擦除：PDFium 在这几个位置盖白矩形 ──
            lock (SyncRoot)
            {
                byte[] data;
                try
                {
                    data = File.ReadAllBytes(inputPath);
                }
                catch (Exception ex)
                {
                    o.Log("[WARN] 三角形注記符号：读不了 " + inputPath + "（" + ex.GetType().Name + "：" + ex.Message + "），跳过。");
                    return inputPath;
                }

                IntPtr document = IntPtr.Zero;
                IntPtr dataPtr = IntPtr.Zero;

                try
                {
                    dataPtr = Marshal.AllocHGlobal(data.Length);
                    Marshal.Copy(data, 0, dataPtr, data.Length);

                    document = FPDF_LoadMemDocument(dataPtr, data.Length, IntPtr.Zero);
                    if (document == IntPtr.Zero)
                    {
                        o.Log("[WARN] 三角形注記符号：PDFium 打不开 " + Path.GetFileName(inputPath) + "（加密 / 损坏？），跳过。");
                        return inputPath;
                    }

                    int pageCount = FPDF_GetPageCount(document);
                    if (pageIndex < 0 || pageIndex >= pageCount)
                    {
                        o.Log("[WARN] 三角形注記符号：页码 " + pageIndex + " 超出范围（共 " + pageCount + " 页），跳过。");
                        return inputPath;
                    }

                    IntPtr page = FPDF_LoadPage(document, pageIndex);
                    if (page == IntPtr.Zero)
                    {
                        o.Log("[WARN] 三角形注記符号：打不开第 " + (pageIndex + 1) + " 页，跳过。");
                        return inputPath;
                    }

                    // 检测用的是 PdfPig 的页内坐标，白块用的是 PDFium 的页面坐标；
                    // 页面设了 /Rotate 时 PdfPig 会把文字 / 路径坐标旋到显示方向，两者差 90° 的倍数，
                    // 照着盖会把白块盖错位置 —— 直接跳过，宁可不擦，也不能盖掉无辜的图面内容。
                    int rotation = FPDFPage_GetRotation(page);
                    if (rotation != 0)
                    {
                        o.Log("[WARN] 三角形注記符号：第 " + (pageIndex + 1) + " 页设了 /Rotate "
                              + (rotation * 90) + "°，检测坐标与页面坐标不一致，跳过（避免白块盖错位置）。");
                        FPDF_ClosePage(page);
                        return inputPath;
                    }

                    int covered;
                    try
                    {
                        covered = CoverWithWhiteRects(page, triangles, o);
                    }
                    finally
                    {
                        FPDF_ClosePage(page);
                    }

                    // 一块都没盖 → 不另存副本，直接用原路径（与「没有批注时不生成副本」一致）
                    if (covered <= 0)
                        return inputPath;

                    string cleanPath = BuildCleanPath(inputPath, outputDir);
                    string tempPath = cleanPath + ".tmp";

                    if (!TrySave(document, tempPath, o))
                        return inputPath;

                    MoveIntoPlace(tempPath, cleanPath);

                    o.Log("[INFO] 三角形注記符号：第 " + (pageIndex + 1) + " 页遮掉 " + covered
                          + " 处（△数字），渲染用 → " + cleanPath);

                    return cleanPath;
                }
                catch (EntryPointNotFoundException ex)
                {
                    // 当前 pdfium.dll 缺页面对象 / 保存 API：不是错误，只是这一版做不了
                    o.Log("[WARN] 三角形注記符号：当前 pdfium 缺少页面对象 / 保存 API（"
                          + ex.Message + "），跳过（批注清理照常）。");
                    return inputPath;
                }
                catch (Exception ex)
                {
                    o.Log("[WARN] 三角形注記符号：处理失败（" + ex.GetType().Name + "：" + ex.Message + "），跳过。");
                    return inputPath;
                }
                finally
                {
                    if (document != IntPtr.Zero)
                        FPDF_CloseDocument(document);

                    if (dataPtr != IntPtr.Zero)
                        Marshal.FreeHGlobal(dataPtr);
                }
            }
        }

        /// <summary>算出干净副本的路径：<c>&lt;输出目录&gt;\&lt;图纸名&gt;_clean.pdf</c>（名字里已带后缀则不重复加）。</summary>
        private static string BuildCleanPath(string inputPath, string outputDir)
        {
            string name = Path.GetFileNameWithoutExtension(inputPath);
            if (name.EndsWith(CleanSuffix, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - CleanSuffix.Length);

            return Path.Combine(outputDir, name + CleanSuffix + ".pdf");
        }

        /// <summary>
        /// 把临时文件放到目标路径。目标可能就是输入文件本身（<see cref="PdfAnnotationStripper"/>
        /// 已经产出过同名 <c>_clean.pdf</c>），所以先写临时文件再替换，任何一步失败都不丢原文件。
        /// </summary>
        private static void MoveIntoPlace(string tempPath, string targetPath)
        {
            if (File.Exists(targetPath))
            {
                string backup = targetPath + ".bak";
                try
                {
                    File.Replace(tempPath, targetPath, backup);
                    TryDelete(backup);
                    return;
                }
                catch (IOException)
                {
                    // 目标被别的程序占用 / 不在同一个卷：退回「复制覆盖」
                }
                catch (UnauthorizedAccessException)
                {
                    // 同上
                }

                File.Copy(tempPath, targetPath, true);
                TryDelete(tempPath);
                return;
            }

            File.Move(tempPath, targetPath);
        }

        /// <summary>删掉可能残留的临时 / 备份文件，失败无所谓。</summary>
        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
                // 被占用：留着也无害
            }
            catch (UnauthorizedAccessException)
            {
                // 同上
            }
        }

        /// <summary>PDFium 的 FPDF_FILEWRITE::WriteBlock 回调（unsigned long 在 Windows 上是 32 位）。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int WriteBlockDelegate(IntPtr pThis, IntPtr pData, uint size);

        /// <summary>PDFium 的 FPDF_FILEWRITE 结构：version 必须为 1，WriteBlock 是函数指针。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct FPDF_FILEWRITE
        {
            public int version;
            public IntPtr WriteBlock;
        }

        /// <summary>另存时的写块回调：把 PDFium 吐出的字节追加到 <see cref="_currentStream"/>。</summary>
        private static int WriteBlock(IntPtr pThis, IntPtr pData, uint size)
        {
            try
            {
                FileStream stream = _currentStream;
                if (stream == null)
                    return 0;

                int length = (int)size;
                byte[] buffer = new byte[length];
                Marshal.Copy(pData, buffer, 0, length);
                stream.Write(buffer, 0, length);
                return 1;
            }
            catch (Exception)
            {
                return 0;   // 返回 0 让 PDFium 中止保存
            }
        }

        /// <summary>把 <paramref name="document"/> 另存到 <paramref name="path"/>（失败只记日志）。</summary>
        private static bool TrySave(IntPtr document, string path, DetectorOptions o)
        {
            FileStream stream = null;
            GCHandle callbackHandle = default(GCHandle);
            IntPtr structPtr = IntPtr.Zero;

            try
            {
                stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                _currentStream = stream;

                WriteBlockDelegate callback = WriteBlock;
                callbackHandle = GCHandle.Alloc(callback);

                FPDF_FILEWRITE fileWrite = new FPDF_FILEWRITE
                {
                    version = 1,
                    WriteBlock = Marshal.GetFunctionPointerForDelegate(callback)
                };

                structPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FPDF_FILEWRITE)));
                Marshal.StructureToPtr(fileWrite, structPtr, false);

                int ok = FPDF_SaveAsCopy(document, structPtr, 0);
                stream.Flush();

                if (ok == 0)
                    o.Log("[WARN] 三角形注記符号：另存失败（FPDF_SaveAsCopy 返回 0），沿用原 PDF。");

                return ok != 0;
            }
            catch (Exception ex)
            {
                o.Log("[WARN] 三角形注記符号：另存失败（" + ex.GetType().Name + "：" + ex.Message + "），沿用原 PDF。");
                return false;
            }
            finally
            {
                _currentStream = null;

                if (structPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(structPtr);

                if (callbackHandle.IsAllocated)
                    callbackHandle.Free();

                if (stream != null)
                    stream.Dispose();
            }
        }

        /// <summary>
        /// 找出该页所有 △ 注記符号（判据见类注释），返回它们的包围盒
        /// （<see cref="TriangleBox"/>，pt，原点在页面左下，与 PDFium 的页面坐标一致）。
        /// 只读 PDF、不写文件；读不到路径 / 文字层时返回空表。
        /// </summary>
        private static List<TriangleBox> DetectTriangles(PdfPageContent page, int pageIndex, DetectorOptions o)
        {
            var result = new List<TriangleBox>();

            if (page == null || !page.IsValid)
            {
                o.Log("[INFO] 三角形注記符号：第 " + (pageIndex + 1)
                      + " 页读不到文字层 / 矢量层（PDF 解析失败或页码越界），跳过。");
                return result;
            }

            // ① 斜边：全部矢量路径（含 Form XObject，坐标已应用 CTM）扁平化后逐段筛
            var diagonals = new List<Diag>();

            foreach (PdfPath path in page.Paths)
            {
                foreach (PdfSubpath sub in path)
                {
                    List<PdfPoint> points = Flatten(sub);

                    for (int i = 1; i < points.Count; i++)
                        AddDiagonal(points[i - 1], points[i], diagonals);
                }
            }

            // 一条斜边都没有 → 这页不可能有 △（扫描件、只有表格的页都走这里）
            if (diagonals.Count < 2)
            {
                o.Log("[INFO] 三角形注記符号：第 " + (pageIndex + 1) + " 页矢量路径 " + page.PathCount
                      + " 条里凑不出斜边（" + diagonals.Count + " 条），这页没有 △（扫描件也会走这里）。");
                return result;
            }

            // ② 文字层：1~3 位纯数字的词，且周围能找到「顶点重合 + 水平底边」的两条斜边
            foreach (Word word in page.Words)
            {
                if (!IsMarkNumber(word.Text))
                    continue;

                TriangleBox triangle;
                if (!TryFindTriangle(word.BoundingBox, diagonals, out triangle))
                    continue;

                if (!ContainsBox(result, triangle))
                    result.Add(triangle);
            }

            o.Log("[INFO] 三角形注記符号：第 " + (pageIndex + 1) + " 页矢量路径 " + page.PathCount
                  + " 条 → 斜边 " + diagonals.Count + " 条、文字 " + page.WordCount
                  + " 个词 → 命中 " + result.Count + " 处 △数字"
                  + (result.Count > 0 ? "。" : "（要「两条斜边凑成三角 + 框内有 1~3 位数字」同时成立才算）。"));

            return result;
        }

        /// <summary>
        /// 把子路径扁平化成折线点列：直线取端点，贝塞尔曲线按 <see cref="BezierSamples"/> 等分采样。
        /// CAD 导出的边可能是「直线」「折线（多段短直线）」或「曲线」，扁平化后统一按线段处理
        /// （与 <see cref="RuledTableExtractor"/> 的处理一致）。
        /// </summary>
        private static List<PdfPoint> Flatten(PdfSubpath sub)
        {
            var points = new List<PdfPoint>();
            if (sub == null) return points;

            foreach (var command in sub.Commands)
            {
                if (command is PdfSubpath.Move)
                {
                    points.Add(((PdfSubpath.Move)command).Location);
                }
                else if (command is PdfSubpath.Line)
                {
                    var line = (PdfSubpath.Line)command;
                    points.Add(line.From);
                    points.Add(line.To);
                }
                else if (command is PdfSubpath.CubicBezierCurve)
                {
                    var cubic = (PdfSubpath.CubicBezierCurve)command;
                    for (int i = 1; i <= BezierSamples; i++)
                        points.Add(CubicAt(cubic.StartPoint, cubic.FirstControlPoint,
                                           cubic.SecondControlPoint, cubic.EndPoint,
                                           i / (double)BezierSamples));
                }
                else if (command is PdfSubpath.QuadraticBezierCurve)
                {
                    var quad = (PdfSubpath.QuadraticBezierCurve)command;
                    for (int i = 1; i <= BezierSamples; i++)
                        points.Add(QuadAt(quad.StartPoint, quad.ControlPoint, quad.EndPoint,
                                          i / (double)BezierSamples));
                }
                else if (command is PdfSubpath.BezierCurve)
                {
                    var bezier = (PdfSubpath.BezierCurve)command;
                    points.Add(bezier.StartPoint);
                    points.Add(bezier.EndPoint);
                }
            }

            return points;
        }

        /// <summary>
        /// 把一条**斜**边登记下来（两个方向都超过 <see cref="LineTolerancePt"/>、长度不短于
        /// <see cref="MinLinePt"/>）。水平 / 竖直的边不可能是三角形的斜边，直接丢；
        /// 文字笔画、箭头尖凑出来的短斜线被长度下限挡住（判据见 <see cref="TryFindTriangle"/>）。
        /// </summary>
        private static void AddDiagonal(PdfPoint p, PdfPoint q, List<Diag> diagonals)
        {
            double dx = Math.Abs(q.X - p.X);
            double dy = Math.Abs(q.Y - p.Y);

            if (dx <= LineTolerancePt || dy <= LineTolerancePt) return;
            if (Math.Sqrt(dx * dx + dy * dy) < MinLinePt) return;

            diagonals.Add(new Diag { X1 = p.X, Y1 = p.Y, X2 = q.X, Y2 = q.Y });
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
        /// 擦除：把 <paramref name="triangles"/> 逐个盖白块，再生成内容流，返回盖上的个数。
        /// 等价于 py 版的 <c>add_redact_annot(fill=(1,1,1))</c> + <c>apply_redactions()</c>。
        /// </summary>
        private static int CoverWithWhiteRects(IntPtr page, List<TriangleBox> triangles, DetectorOptions o)
        {
            int covered = 0;

            foreach (TriangleBox triangle in triangles)
            {
                if (CoverWithWhiteRect(page, triangle))
                    covered++;
            }

            if (covered > 0 && FPDFPage_GenerateContent(page) == 0)
                o.Log("[WARN] 三角形注記符号：FPDFPage_GenerateContent 失败，白块可能没写进内容流。");

            return covered;
        }

        /// <summary>
        /// 在三角形（外扩 <see cref="ErasePaddingPt"/>）上盖一块不透明白矩形。
        /// 白块加在内容流末尾，Form XObject 里画的三角形也盖得住。
        /// </summary>
        private static bool CoverWithWhiteRect(IntPtr page, TriangleBox triangle)
        {
            double left = triangle.Left - ErasePaddingPt;
            double bottom = triangle.Bottom - ErasePaddingPt;
            double width = (triangle.Right - triangle.Left) + ErasePaddingPt * 2.0;
            double height = (triangle.Top - triangle.Bottom) + ErasePaddingPt * 2.0;

            if (width <= 0 || height <= 0)
                return false;

            IntPtr rect = FPDFPageObj_CreateNewRect(
                (float)left, (float)bottom, (float)width, (float)height);

            if (rect == IntPtr.Zero)
                return false;

            FPDFPageObj_SetFillColor(rect, 255, 255, 255, 255);

            // 新建的路径对象默认「不填充、不描边」，只设颜色是看不见的：
            // 必须显式要「实心填充（ALTERNATE）+ 不描边」，白块才盖得住东西。
            FPDFPath_SetDrawMode(rect, FillModeAlternate, 0);

            // 旧版 pdfium 的 FPDFPage_InsertObject 返回 void，所以不拿返回值判断成功与否；
            // 对象追加在页面对象列表末尾 → 生成内容流后画在最后，盖住前面所有图形（含 Form）
            FPDFPage_InsertObject(page, rect);

            return true;
        }

        /// <summary>
        /// 这个数字词是否被某个 △ 注記符号框住（判据与 <see cref="RuledTableExtractor"/> 的
        /// <c>MarkTriangleMarks</c> 一致）。命中时通过 <paramref name="triangle"/> 返回三角形包围盒。
        ///
        /// <para>
        /// ① 先挑出「长度像三角形边、两端都在词框外扩 3 倍字高范围内」的斜边，缩小配对规模；
        /// ② 两两配对，找公共端点当顶点（容差 1/4 字高）；
        /// ③ 另两端要近似等高（底边水平，容差 1/2 字高）、底宽 0.5~3 倍字高、高 0.8~4 倍字高；
        /// ④ 数字中心要落在三角形里 —— 只与三角形相交不算，紧贴右边的尺寸数字
        ///    （如 <c>100</c>）不能被当成框内编号，这是 py 版加上「中心点在框内」的原因。
        /// </para>
        /// </summary>
        private static bool TryFindTriangle(PdfRectangle wordBox, List<Diag> diagonals, out TriangleBox triangle)
        {
            triangle = null;

            double h = wordBox.Top - wordBox.Bottom;
            if (h <= 0) return false;

            double cx = (wordBox.Left + wordBox.Right) / 2.0;
            double cy = (wordBox.Bottom + wordBox.Top) / 2.0;

            double minLength = h * MinSideRatio;
            double maxLength = h * MaxSideRatio;
            double join = h * ApexJoinRatio;
            double pad = h * SearchPadRatio;

            var near = new List<Diag>();

            foreach (Diag d in diagonals)
            {
                double dx = d.X2 - d.X1, dy = d.Y2 - d.Y1;
                double length = Math.Sqrt(dx * dx + dy * dy);

                if (length < minLength || length > maxLength) continue;
                if (!Near(d.X1, d.Y1, wordBox, pad)) continue;
                if (!Near(d.X2, d.Y2, wordBox, pad)) continue;

                near.Add(d);
            }

            for (int i = 0; i < near.Count; i++)
            {
                for (int j = i + 1; j < near.Count; j++)
                {
                    double apexX, apexY, base1X, base1Y, base2X, base2Y;

                    if (!TryApex(near[i], near[j], join,
                                 out apexX, out apexY, out base1X, out base1Y, out base2X, out base2Y))
                        continue;

                    // ③ 底边水平 + 底宽 / 高在合理范围
                    if (Math.Abs(base1Y - base2Y) > h * BaseLevelRatio) continue;

                    double baseWidth = Math.Abs(base2X - base1X);
                    if (baseWidth < h * BaseMinRatio || baseWidth > h * BaseMaxRatio) continue;

                    double height = Math.Abs(apexY - (base1Y + base2Y) / 2.0);
                    if (height < h * HeightMinRatio || height > h * HeightMaxRatio) continue;

                    // ④ 数字中心落在三角形内（不是包围盒内 —— 斜边外侧是空的）
                    if (!PointInTriangle(cx, cy, apexX, apexY, base1X, base1Y, base2X, base2Y))
                        continue;

                    triangle = new TriangleBox
                    {
                        Left = Math.Min(apexX, Math.Min(base1X, base2X)),
                        Right = Math.Max(apexX, Math.Max(base1X, base2X)),
                        Bottom = Math.Min(apexY, Math.Min(base1Y, base2Y)),
                        Top = Math.Max(apexY, Math.Max(base1Y, base2Y))
                    };

                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 两条斜边是否共用顶点（三角形的顶角）。是则返回顶点与「另外两个端点」；
        /// 共用顶点时取离另一条线远端的那个端点当底边端点，与线段画的方向无关。
        /// </summary>
        private static bool TryApex(Diag a, Diag b, double join,
                                    out double apexX, out double apexY,
                                    out double base1X, out double base1Y,
                                    out double base2X, out double base2Y)
        {
            apexX = apexY = base1X = base1Y = base2X = base2Y = 0;

            // a 的两端分别去配 b 的两端：四种组合里最近的一对就是公共顶点
            double best = double.MaxValue;
            int apexInA = -1, apexInB = -1;

            for (int i = 0; i < 2; i++)
            {
                double ax = i == 0 ? a.X1 : a.X2;
                double ay = i == 0 ? a.Y1 : a.Y2;

                for (int j = 0; j < 2; j++)
                {
                    double bx = j == 0 ? b.X1 : b.X2;
                    double by = j == 0 ? b.Y1 : b.Y2;

                    double dx = ax - bx, dy = ay - by;
                    double distance = Math.Sqrt(dx * dx + dy * dy);

                    if (distance < best) { best = distance; apexInA = i; apexInB = j; }
                }
            }

            if (apexInA < 0 || best > join) return false;

            apexX = apexInA == 0 ? a.X1 : a.X2;
            apexY = apexInA == 0 ? a.Y1 : a.Y2;

            base1X = apexInA == 0 ? a.X2 : a.X1;
            base1Y = apexInA == 0 ? a.Y2 : a.Y1;

            base2X = apexInB == 0 ? b.X2 : b.X1;
            base2Y = apexInB == 0 ? b.Y2 : b.Y1;

            return true;
        }

        /// <summary>点是否落在词框外扩 <paramref name="pad"/> 的矩形内。</summary>
        private static bool Near(double x, double y, PdfRectangle wordBox, double pad)
        {
            return x >= wordBox.Left - pad && x <= wordBox.Right + pad
                && y >= wordBox.Bottom - pad && y <= wordBox.Top + pad;
        }

        /// <summary>
        /// 这个三角形是否已收录（同一个三角形被两个数字词命中、或同一条边被画了两遍时会重复命中）。
        /// 包围盒坐标相差不超过 <see cref="LineTolerancePt"/> 就算同一个。
        /// </summary>
        private static bool ContainsBox(List<TriangleBox> boxes, TriangleBox box)
        {
            foreach (TriangleBox b in boxes)
            {
                if (Math.Abs(b.Left - box.Left) <= LineTolerancePt
                    && Math.Abs(b.Right - box.Right) <= LineTolerancePt
                    && Math.Abs(b.Bottom - box.Bottom) <= LineTolerancePt
                    && Math.Abs(b.Top - box.Top) <= LineTolerancePt)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 点是否落在三角形内（含边界）。用三条边的有向面积同号判定，
        /// 顶点顺时针 / 逆时针、正三角 / 倒三角都一样成立。
        /// </summary>
        private static bool PointInTriangle(double x, double y,
                                            double ax, double ay, double bx, double by,
                                            double cx, double cy)
        {
            double d1 = Side(x, y, ax, ay, bx, by);
            double d2 = Side(x, y, bx, by, cx, cy);
            double d3 = Side(x, y, cx, cy, ax, ay);

            bool negative = d1 < 0 || d2 < 0 || d3 < 0;
            bool positive = d1 > 0 || d2 > 0 || d3 > 0;

            return !(negative && positive);
        }

        /// <summary>点 (px,py) 相对有向边 (ax,ay)→(bx,by) 的位置（&gt;0 左侧、&lt;0 右侧、=0 在线上）。</summary>
        private static double Side(double px, double py, double ax, double ay, double bx, double by)
        {
            return (px - bx) * (ay - by) - (ax - bx) * (py - by);
        }

        /// <summary>
        /// 是否是三角形里的「提醒数字」：1~3 位纯数字（半角、全角都认，与
        /// <see cref="RuledTableExtractor"/> 的 <c>MarkTriangleMarks</c> 一致）。
        /// 品番 / 寸法 / 日文都不满足，因此不会把普通标注当成改版编号。
        /// </summary>
        private static bool IsMarkNumber(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            string value = text.Trim();

            if (value.Length < MinDigitLength || value.Length > MaxDigitLength)
                return false;

            foreach (char c in value)
            {
                bool digit = (c >= '0' && c <= '9')
                             || (c >= '\uFF10' && c <= '\uFF19');   // 全角 ０~９

                if (!digit)
                    return false;
            }

            return true;
        }

        // ── 内部数据结构 ──

        /// <summary>
        /// 一条斜边：两个端点（pt，原点在左下）。△ 注記符号的两条斜边共用顶点、底边水平，
        /// 判据见 <see cref="TryFindTriangle"/>。
        /// </summary>
        private sealed class Diag
        {
            public double X1;
            public double Y1;
            public double X2;
            public double Y2;
        }

        /// <summary>一个 △ 注記符号的包围盒（pt，原点在左下，与 PDFium 的页面坐标一致）。</summary>
        private sealed class TriangleBox
        {
            public double Left { get; set; }
            public double Bottom { get; set; }
            public double Right { get; set; }
            public double Top { get; set; }
        }

        // ── 文档 / 页面 ──

        [DllImport(PdfiumDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDF_LoadMemDocument(IntPtr dataBuf, int size, IntPtr password);

        [DllImport(PdfiumDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_CloseDocument(IntPtr document);

        [DllImport(PdfiumDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDF_GetPageCount(IntPtr document);

        [DllImport(PdfiumDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

        [DllImport(PdfiumDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_ClosePage(IntPtr page);

        /// <summary>页面旋转（0 / 1 / 2 / 3 = 0° / 90° / 180° / 270°），用来挡住坐标系不一致的页。</summary>
        [DllImport(PdfiumDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFPage_GetRotation(IntPtr page);

        [DllImport(PdfiumDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDF_SaveAsCopy(IntPtr document, IntPtr fileWrite, uint flags);

        // ── 页面对象 / 内容流（只用到「插入白矩形 + 生成内容流」）──

        [DllImport(PdfiumDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFPage_InsertObject(IntPtr page, IntPtr pageObject);

        [DllImport(PdfiumDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFPage_GenerateContent(IntPtr page);

        /// <summary>建一个矩形对象（<paramref name="left"/> / <paramref name="bottom"/> 是左下角，单位 pt）。</summary>
        [DllImport(PdfiumDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFPageObj_CreateNewRect(
            float left, float bottom, float width, float height);

        /// <summary>填充色，分量 0~255（白 = 255,255,255,255）。</summary>
        [DllImport(PdfiumDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFPageObj_SetFillColor(
            IntPtr pageObject, uint r, uint g, uint b, uint a);

        /// <summary>
        /// 绘制方式：<paramref name="fillMode"/> 取 <see cref="FillModeAlternate"/> 是实心填充，
        /// <paramref name="stroke"/> = 0 不描边。新建路径对象默认两者都不做，必须显式设置。
        /// </summary>
        [DllImport(PdfiumDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFPath_SetDrawMode(IntPtr pathObject, int fillMode, int stroke);
    }
}
