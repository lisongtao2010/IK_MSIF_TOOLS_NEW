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
    /// 做法参照 <c>triangle_eraser.py</c>（PyMuPDF 定位 + pikepdf 改内容流），
    /// 只是把底层换成 PdfPig（读）+ PDFium（改）。
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
    /// 检测用 PdfPig（两条判据取并集，与 <c>triangle_eraser.py</c> 的「双重锁定」对齐）：
    /// <list type="number">
    ///   <item><b>整条路径就是三角形</b>（py 的策略 1）：矢量层每条路径（含 Form XObject，坐标已应用 CTM）
    ///         去掉重合点后<b>恰好 3 个顶点</b>、没有贝塞尔曲线、三边长度均匀
    ///         （标准差 / 均值 &lt; <see cref="SideUniformRatio"/>）、包围盒 6~50 pt 且长宽比 0.3~3.0，
    ///         并且框内有「数字字符的中心」或「拆成独立短线段的笔画」—— 全满足才算；</item>
    ///   <item><b>两条斜边夹一个数字</b>（py 策略 2 在 C# 里的替代）：CAD 把三角形的三条边画成
    ///         独立对象时整条路径判据失效，改用「1~3 位纯数字的词 + 它周围 3 倍字高内的两条斜边」——
    ///         两条斜边共用一个端点（容差 1/4 字高）、另两端近似等高（底边水平，容差 1/2 字高）、
    ///         底宽 0.5~3 倍字高、高 0.8~4 倍字高、数字中心落在三角形内；</item>
    ///   <item>两条判据命中的框按包围盒去重（容差 <see cref="LineTolerancePt"/>）。
    ///         文字笔画、引线、三角形外侧的尺寸数字都凑不出这些组合。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// 擦除（与 py 的「内容流手术」同一个目标：把符号从图纸上<b>物理删掉</b>，不是盖住了事）：
    /// 逐页遍历 PDFium 的页面对象（<c>FPDFPage_GetObject</c>），把<b>整个落在</b>三角形框里
    /// （容差 <see cref="PathEraseTolerancePt"/> pt）的路径对象交给 <c>FPDFPage_RemoveObject</c> 删掉；
    /// 整串就是 1~4 位数字的文本对象也一并删（<c>FPDFTextObj_GetText</c> 先看清内容，
    /// <c>100 1</c> 这种混着尺寸的绝不删）。删完 <c>FPDFPage_GenerateContent</c> +
    /// <c>FPDF_SaveAsCopy</c> 另存 <c>&lt;输出目录&gt;\&lt;图纸名&gt;_clean.pdf</c>。
    /// </para>
    ///
    /// <para>
    /// 为什么还要白块兜底：py 能按「子路径」粒度切内容流，PDFium 只能整对象删，三种情况删不动 ——
    /// ① 三角形画在 Form XObject 里（页面对象接口看不进去）；② 三角形与表格长线在同一个路径对象里
    /// （整删会把长线一起删掉）；③ 数字与尺寸挤在同一个文本对象里（拆不开）。
    /// 这些框逐个补一块不透明白矩形（外扩 <see cref="ErasePaddingPt"/> pt，等价于 py 的 redaction），
    /// 渲染结果与 py 一致；能纯删的框不盖白块，免得把穿过框的表格线涂掉一小截。
    /// </para>
    ///
    /// <para>
    /// 与 <see cref="PdfAnnotationStripper"/> 的关系：那一步先删注释并另存 <c>_clean.pdf</c>，
    /// 本步骤在它的输出上再「读 → 删对象（删不动的盖白块）→ 另存」，文件名不变（覆盖同一份 <c>_clean.pdf</c>），
    /// 所以渲染（<see cref="PdfRenderer"/>）拿到的始终是同一路径。文字 / 表格 / 圈号分析仍读原 PDF，
    /// 不受影响。任何失败只告警并返回传入的路径，绝不让预处理把识别流程搞崩。
    /// </para>
    ///
    /// <para>
    /// 代价与限制：整串是数字的文本对象能直接删掉；和尺寸挤在同一个文本对象里的数字删不得，
    /// 只能白块盖住 —— 那份 <c>_clean.pdf</c> 的文字层里它还在（渲染时看不见）。
    /// 表格提取读的是原 PDF，本来就有 <c>MarkTriangleMarks</c> 把它剔掉，不影响 CSV。
    /// 扫描件（没有文字层、也没有矢量三角形）认不出，日志里会是「命中 0 处」。
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

        /// <summary>
        /// 框内数字的位数上限（与 <see cref="RuledTableExtractor"/> 的 <c>MarkTriangleMarks</c> 一致；
        /// py 版放宽到 4 位，这里收紧到 3 位，免得把四位数的寸法当成编号误删）。
        /// </summary>
        private const int MaxDigitLength = 3;

        /// <summary>可以整串删掉的文本对象位数上限（py 的 1~4 位；只看「整串是不是数字」，不看上下文）。</summary>
        private const int TextObjDigitMaxLength = 4;

        // ── py 版（triangle_eraser.py）的三角形判据参数 ──

        /// <summary>三角形包围盒下限（pt）：更小的多半是文字笔画 / 箭头尖（py 的 TRIANGLE_MIN_PT）。</summary>
        private const double TriangleMinPt = 6;

        /// <summary>三角形包围盒上限（pt）：更大的就不是「把数字框起来的记号」了（py 的 TRIANGLE_MAX_PT）。</summary>
        private const double TriangleMaxPt = 50;

        /// <summary>三角形包围盒的长宽比下限（py：0.3）。</summary>
        private const double TriangleBoxRatioMin = 0.3;

        /// <summary>三角形包围盒的长宽比上限（py：3.0）。</summary>
        private const double TriangleBoxRatioMax = 3.0;

        /// <summary>三边均匀度：边长标准差 / 均值小于这个数才算三角形（py 的 SIDE_RATIO_TOLERANCE）。</summary>
        private const double SideUniformRatio = 0.45;

        /// <summary>顶点去重容差（pt）：<c>m-l-l-h</c> 的闭合点与起点算同一个顶点（py 的 0.5）。</summary>
        private const double VertexJoinPt = 0.5;

        /// <summary>最短的平均边长（pt）：低于 1 pt 的图形直接否掉（py 的 mean_side &lt; 1）。</summary>
        private const double MinMeanSidePt = 1.0;

        /// <summary>判定「这个对象整个落在三角形框里」的容差（pt，py 的 _should_remove_subpath 容差）。</summary>
        private const double PathEraseTolerancePt = 1.2;

        // ── 「框内有拆出来的笔画」判据（py 的 _has_shx_vector_strokes_inside）──

        /// <summary>笔画要落在这个内缩量之内才算「在三角形里面」（pt）。</summary>
        private const double InnerStrokeMargin = 0.3;

        /// <summary>与三角形框四条边比对时的容差（pt）：同尺寸的对象是三角形自己，不算笔画。</summary>
        private const double SameRectTolerancePt = 0.8;

        /// <summary>笔画的最小宽度（pt）：宽于它才算一笔。</summary>
        private const double InnerStrokeMinWidthPt = 0.3;

        /// <summary>笔画的最小高度（pt）：高于它才算一笔（竖笔）。</summary>
        private const double InnerStrokeMinHeightPt = 1.2;

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

            // ── 擦除：PDFium 删掉整个落在框里的页面对象，删不动的补白矩形 ──
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

                    // 检测用的是 PdfPig 的页内坐标，删除 / 白块用的是 PDFium 的页面坐标；
                    // 页面设了 /Rotate 时 PdfPig 会把文字 / 路径坐标旋到显示方向，两者差 90° 的倍数，
                    // 照着删 / 盖会动错对象 —— 直接跳过，宁可不擦，也不能删掉无辜的图面内容。
                    int rotation = FPDFPage_GetRotation(page);
                    if (rotation != 0)
                    {
                        o.Log("[WARN] 三角形注記符号：第 " + (pageIndex + 1) + " 页设了 /Rotate "
                              + (rotation * 90) + "°，检测坐标与页面坐标不一致，跳过（避免删错 / 盖错位置）。");
                        FPDF_ClosePage(page);
                        return inputPath;
                    }

                    int handled, deletedObjects, whiteRects;
                    try
                    {
                        handled = EraseTriangles(page, triangles, o, out deletedObjects, out whiteRects);
                    }
                    finally
                    {
                        FPDF_ClosePage(page);
                    }

                    // 一处都没动成 → 不另存副本，直接用原路径（与「没有批注时不生成副本」一致）
                    if (handled <= 0)
                        return inputPath;

                    string cleanPath = BuildCleanPath(inputPath, outputDir);
                    string tempPath = cleanPath + ".tmp";

                    if (!TrySave(document, tempPath, o))
                        return inputPath;

                    MoveIntoPlace(tempPath, cleanPath);

                    o.Log("[INFO] 三角形注記符号：第 " + (pageIndex + 1) + " 页处理 " + handled
                          + " 处（△数字）→ 物理删除 " + deletedObjects + " 个页面对象"
                          + (whiteRects > 0 ? "，另外 " + whiteRects + " 处盖白块兜底" : string.Empty)
                          + "，渲染用 → " + cleanPath);

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

            // ① 矢量层先整体过一遍：每条路径（含 Form XObject，坐标已应用 CTM）的子路径顶点、
            //    有没有曲线（判据 1 用），以及全部斜边（判据 2 用）
            var paths = new List<PathInfo>();
            var diagonals = new List<Diag>();

            foreach (PdfPath path in page.Paths)
            {
                PathInfo info = ReadPath(path);
                if (info == null)
                    continue;

                paths.Add(info);

                foreach (List<PdfPoint> points in info.Subpaths)
                    for (int i = 1; i < points.Count; i++)
                        AddDiagonal(points[i - 1], points[i], diagonals);
            }

            // ② 文字层：数字字符（判据 1 判断「框里有数字」）与数字词（判据 2 的起点）
            List<Digit> digits = ReadDigits(page.Words);

            int byPath = 0, byPair = 0;

            // ── 判据 1：整条路径就是三角形 + 框内有数字 / 笔画（py 的策略 1） ──
            foreach (PathInfo info in paths)
            {
                TriangleBox box;
                if (!LooksLikeTriangle(info, out box))
                    continue;

                if (!HasDigitInside(digits, box) && !HasStrokeInside(paths, info, box))
                    continue;

                if (!ContainsBox(result, box))
                {
                    result.Add(box);
                    byPath++;
                }
            }

            // ── 判据 2：两条斜边夹一个数字（py 策略 2 的 C# 替代，覆盖「三条边拆成独立对象」） ──
            if (diagonals.Count >= 2)
            {
                foreach (Word word in page.Words)
                {
                    if (!IsMarkNumber(word.Text))
                        continue;

                    TriangleBox triangle;
                    if (!TryFindTriangle(word.BoundingBox, diagonals, out triangle))
                        continue;

                    if (!ContainsBox(result, triangle))
                    {
                        result.Add(triangle);
                        byPair++;
                    }
                }
            }

            o.Log("[INFO] 三角形注記符号：第 " + (pageIndex + 1) + " 页矢量路径 " + page.PathCount
                  + " 条（斜边 " + diagonals.Count + " 条）、文字 " + page.WordCount + " 个词（数字字符 "
                  + digits.Count + " 个）→ 命中 " + result.Count + " 处 △数字"
                  + (result.Count > 0
                        ? "（整条路径判据 " + byPath + " 处、斜边配对 " + byPair + " 处）。"
                        : "（要「整条路径 = 三角形 且 框内有数字 / 笔画」，"
                          + "或「两条斜边凑成三角 且 框内有 1~3 位数字」才算）。"));

            return result;
        }

        // ── 判据 1 的几何 / 文字工具（对应 py 的 _looks_like_triangle /
        //    _get_normal_digits_inside / _has_shx_vector_strokes_inside） ──

        /// <summary>
        /// 一条矢量路径（PDF 里的一个 path 对象）的几何摘要：各子路径的折线点列、有没有曲线、
        /// 以及整体包围盒（判据 1 与「框内有笔画」都按它判断，等价于 py 的 <c>d["rect"]</c>）。
        /// </summary>
        private sealed class PathInfo
        {
            public readonly List<List<PdfPoint>> Subpaths = new List<List<PdfPoint>>();
            public bool HasCurve;
            public double Left = double.MaxValue;
            public double Bottom = double.MaxValue;
            public double Right = double.MinValue;
            public double Top = double.MinValue;

            public bool HasPoints { get { return Subpaths.Count > 0; } }
            public double Width { get { return Right - Left; } }
            public double Height { get { return Top - Bottom; } }
        }

        /// <summary>把一条 <see cref="PdfPath"/> 读成 <see cref="PathInfo"/>（没有点 / 空路径返回 null）。</summary>
        private static PathInfo ReadPath(PdfPath path)
        {
            if (path == null)
                return null;

            var info = new PathInfo();

            foreach (PdfSubpath sub in path)
            {
                if (sub == null)
                    continue;

                List<PdfPoint> points = Flatten(sub);

                if (points.Count > 0)
                {
                    info.Subpaths.Add(points);

                    foreach (PdfPoint p in points)
                    {
                        if (p.X < info.Left) info.Left = p.X;
                        if (p.X > info.Right) info.Right = p.X;
                        if (p.Y < info.Bottom) info.Bottom = p.Y;
                        if (p.Y > info.Top) info.Top = p.Y;
                    }
                }

                foreach (var command in sub.Commands)
                {
                    if (command is PdfSubpath.CubicBezierCurve
                        || command is PdfSubpath.QuadraticBezierCurve
                        || command is PdfSubpath.BezierCurve)
                    {
                        info.HasCurve = true;
                        break;
                    }
                }
            }

            return info.HasPoints ? info : null;
        }

        /// <summary>
        /// 判据 1（py 的 <c>_looks_like_triangle</c>）：整条路径就是一个三角形 ——
        /// 没有贝塞尔曲线、去重后恰好 3 个顶点（<c>m-l-l-h</c> 的闭合点与起点算同一个）、
        /// 三边长度均匀（标准差 / 均值 &lt; <see cref="SideUniformRatio"/>）、
        /// 包围盒 6~50 pt 且长宽比 0.3~3.0。
        /// </summary>
        private static bool LooksLikeTriangle(PathInfo info, out TriangleBox box)
        {
            box = null;

            if (info == null || !info.HasPoints || info.HasCurve)
                return false;

            double width = info.Width;
            double height = info.Height;

            if (width < TriangleMinPt || height < TriangleMinPt
                || width > TriangleMaxPt || height > TriangleMaxPt)
                return false;

            if (height <= 0)
                return false;

            double ratio = width / height;
            if (ratio < TriangleBoxRatioMin || ratio > TriangleBoxRatioMax)
                return false;

            var unique = new List<PdfPoint>();

            foreach (List<PdfPoint> points in info.Subpaths)
            {
                foreach (PdfPoint p in points)
                {
                    bool duplicate = false;

                    foreach (PdfPoint u in unique)
                    {
                        if (Math.Abs(p.X - u.X) < VertexJoinPt && Math.Abs(p.Y - u.Y) < VertexJoinPt)
                        {
                            duplicate = true;
                            break;
                        }
                    }

                    if (!duplicate)
                        unique.Add(p);
                }
            }

            if (unique.Count != 3)
                return false;

            double s01 = Distance(unique[0], unique[1]);
            double s12 = Distance(unique[1], unique[2]);
            double s20 = Distance(unique[2], unique[0]);

            double mean = (s01 + s12 + s20) / 3.0;
            if (mean < MinMeanSidePt)
                return false;

            double std = Math.Sqrt(((s01 - mean) * (s01 - mean)
                                    + (s12 - mean) * (s12 - mean)
                                    + (s20 - mean) * (s20 - mean)) / 3.0);

            if (std / mean >= SideUniformRatio)
                return false;

            box = new TriangleBox
            {
                Left = info.Left,
                Bottom = info.Bottom,
                Right = info.Right,
                Top = info.Top
            };

            return true;
        }

        /// <summary>两点距离（pt）。</summary>
        private static double Distance(PdfPoint a, PdfPoint b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>文字层里的一个数字字符：字形框的中心（pt，原点在左下）。</summary>
        private sealed class Digit
        {
            public double CenterX;
            public double CenterY;
        }

        /// <summary>
        /// 文字层里的数字字符（半角 / 全角都算）。判据 1 靠它判断「三角形框里有数字」。
        /// 一个词可能跨出三角形（<c>100 1</c> 这种被 △ 挤在一起的），所以按<b>字符</b>取中心，
        /// 与 py 的 <c>rawdict</c> 一致；个别字体拿不到逐字符框时，整词都是数字就退回用词框。
        /// </summary>
        private static List<Digit> ReadDigits(IReadOnlyList<Word> words)
        {
            var list = new List<Digit>();

            if (words == null)
                return list;

            foreach (Word word in words)
            {
                if (word == null)
                    continue;

                bool anyLetter = false;

                if (word.Letters != null)
                {
                    foreach (Letter letter in word.Letters)
                    {
                        if (letter == null)
                            continue;

                        anyLetter = true;

                        if (!IsDigitChar(letter.Value))
                            continue;

                        list.Add(CenterOf(letter.GlyphRectangle));
                    }
                }

                if (!anyLetter && IsMarkNumber(word.Text))
                    list.Add(CenterOf(word.BoundingBox));
            }

            return list;
        }

        /// <summary>取一个矩形框的中心点。</summary>
        private static Digit CenterOf(PdfRectangle rect)
        {
            return new Digit
            {
                CenterX = (rect.Left + rect.Right) / 2.0,
                CenterY = (rect.Bottom + rect.Top) / 2.0
            };
        }

        /// <summary>单个字符是不是数字（半角 0~9 / 全角 ０~９）。</summary>
        private static bool IsDigitChar(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length != 1)
                return false;

            char c = text[0];
            return (c >= '0' && c <= '9') || (c >= '\uFF10' && c <= '\uFF19');
        }

        /// <summary>
        /// 判据 1 的「框里有数字」（py 的 <c>_get_normal_digits_inside</c>）：
        /// 数字字符的中心落在三角形框里就算。py 那个 2 pt 的 <c>DIGIT_SEARCH_EXPAND</c>
        /// 只是 <c>get_text(clip=…)</c> 的取数范围，判定用的仍是三角形原框，这里同理。
        /// </summary>
        private static bool HasDigitInside(List<Digit> digits, TriangleBox box)
        {
            if (digits == null || digits.Count == 0)
                return false;

            foreach (Digit d in digits)
            {
                if (d.CenterX >= box.Left && d.CenterX <= box.Right
                    && d.CenterY >= box.Bottom && d.CenterY <= box.Top)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 判据 1 的「框里有笔画」（py 的 <c>_has_shx_vector_strokes_inside</c>）：
        /// AutoCAD SHX 字体的数字在 PDF 里可能是拆开的矢量笔画（文字层里读不到数字），
        /// 这时只要三角形框内有<b>明显小于三角形本身</b>的其它路径，就认为里面有笔画的数字。
        /// 四条边都与三角形重合的对象是三角形自己，先排除（容差 <see cref="SameRectTolerancePt"/>）。
        /// </summary>
        private static bool HasStrokeInside(List<PathInfo> paths, PathInfo self, TriangleBox box)
        {
            if (paths == null || box == null)
                return false;

            double innerLeft = box.Left + InnerStrokeMargin;
            double innerBottom = box.Bottom + InnerStrokeMargin;
            double innerRight = box.Right - InnerStrokeMargin;
            double innerTop = box.Top - InnerStrokeMargin;

            foreach (PathInfo p in paths)
            {
                if (p == null || ReferenceEquals(p, self))
                    continue;

                if (Math.Abs(p.Left - box.Left) < SameRectTolerancePt
                    && Math.Abs(p.Bottom - box.Bottom) < SameRectTolerancePt
                    && Math.Abs(p.Right - box.Right) < SameRectTolerancePt
                    && Math.Abs(p.Top - box.Top) < SameRectTolerancePt)
                    continue;

                if (p.Left >= innerLeft && p.Right <= innerRight
                    && p.Bottom >= innerBottom && p.Top <= innerTop
                    && (p.Width > InnerStrokeMinWidthPt || p.Height > InnerStrokeMinHeightPt))
                    return true;
            }

            return false;
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
        /// 一个三角形框的处理计划：框里有哪些<b>可以整删</b>的对象（<see cref="Mine"/>），
        /// 以及框里还有没有删不掉 / 只部分相交的东西（<see cref="Leftover"/>）。
        /// </summary>
        private sealed class Frame
        {
            public TriangleBox Box;
            public readonly List<PageObjectInfo> Mine = new List<PageObjectInfo>();
            public bool Leftover;
        }

        /// <summary>
        /// 擦除：先物理删除整个落在三角形框里的页面对象（路径对象、整串是数字的文本对象），
        /// 删不动的再盖白块兜底，最后生成内容流。与 py 的「内容流手术」同一个目标 ——
        /// 符号从图纸上真的消失，而不是只被盖住（只有 PDFium 删不了的那几类才退回白块）。
        /// </summary>
        /// <returns>处理过的三角形个数（纯删除 + 白块），一处都没动成时返回 0。</returns>
        private static int EraseTriangles(
            IntPtr page, List<TriangleBox> triangles, DetectorOptions o,
            out int deletedObjects, out int whiteRects)
        {
            deletedObjects = 0;
            whiteRects = 0;

            if (triangles == null || triangles.Count == 0)
                return 0;

            // 页面级对象（看不到 Form XObject 里的内容 —— 那些框会落到白块兜底）
            List<PageObjectInfo> objects = ReadPageObjects(page);

            var frames = new List<Frame>();
            var toDelete = new List<PageObjectInfo>();

            foreach (TriangleBox triangle in triangles)
            {
                var frame = new Frame { Box = triangle };
                frames.Add(frame);

                if (objects == null)
                    continue;   // 这版 pdfium.dll 没有对象接口 → 整框交给白块

                foreach (PageObjectInfo obj in objects)
                {
                    if (!Intersects(obj, triangle))
                        continue;

                    // 只有「整个落在框里的路径对象 / 整串是数字的文本对象」能整删；
                    // 与长表格线共用一个路径对象的（只部分相交）、图片、Form XObject、
                    // 「100 1」这种混着尺寸的文本，一律不碰 —— 硬删会伤到别的内容
                    if (FullyInside(obj, triangle) && IsDeletableObject(obj))
                    {
                        frame.Mine.Add(obj);

                        if (!obj.Queued)
                        {
                            obj.Queued = true;
                            toDelete.Add(obj);
                        }
                    }
                    else
                    {
                        frame.Leftover = true;
                    }
                }
            }

            // 统一删除：遍历时删会让 FPDFPage_GetObject 的索引错位，所以先收集、后删
            foreach (PageObjectInfo obj in toDelete)
            {
                if (obj.Removed)
                    continue;

                if (FPDFPage_RemoveObject(page, obj.Handle) != 0)
                {
                    obj.Removed = true;
                    deletedObjects++;
                }
            }

            var pureDelete = new List<TriangleBox>();
            var cover = new List<TriangleBox>();

            foreach (Frame frame in frames)
            {
                bool allRemoved = !frame.Leftover && frame.Mine.Count > 0;

                if (allRemoved)
                {
                    foreach (PageObjectInfo obj in frame.Mine)
                    {
                        if (!obj.Removed)
                        {
                            allRemoved = false;
                            break;
                        }
                    }
                }

                if (allRemoved)
                    pureDelete.Add(frame.Box);
                else
                    cover.Add(frame.Box);   // 删不干净（或框里根本没有页面级对象）→ 白块
            }

            foreach (TriangleBox box in cover)
            {
                if (CoverWithWhiteRect(page, box))
                    whiteRects++;
            }

            if (pureDelete.Count + whiteRects == 0)
                return 0;

            if (FPDFPage_GenerateContent(page) == 0)
                o.Log("[WARN] 三角形注記符号：FPDFPage_GenerateContent 失败，删除 / 白块可能没写进内容流。");

            return pureDelete.Count + whiteRects;
        }

        // ── PDFium 页面对象（物理删除用；常量取 FPDF_PAGEOBJECTTYPE 的定义值） ──

        /// <summary>文本对象（<c>FPDF_PAGEOBJ_TEXT</c>）。</summary>
        private const int PageObjText = 1;

        /// <summary>路径对象（<c>FPDF_PAGEOBJ_PATH</c>）—— 三角形和它的边都是这种。</summary>
        private const int PageObjPath = 2;

        /// <summary>
        /// 页面上的一个对象（PDFium 的 <c>FPDF_PAGEOBJECT</c>）：类型、包围盒（pt，原点在左下，
        /// 与 PdfPig 的页面坐标一致），以及「整串是不是 1~4 位数字」（只对文本对象有意义）。
        /// </summary>
        private sealed class PageObjectInfo
        {
            public IntPtr Handle;
            public int Index;
            public int Type;
            public double Left;
            public double Bottom;
            public double Right;
            public double Top;
            public bool IsDigitText;
            public bool Queued;
            public bool Removed;
        }

        /// <summary>
        /// 读出该页所有页面级对象及其包围盒。这版 pdfium.dll 缺对象接口时返回 null，
        /// 调用方退回「全部盖白块」（换回老 dll 也不会崩）。
        /// Form XObject 在页面对象列表里只是一个 FORM 对象、看不到里面 ——
        /// 画在 form 里的 △ 只能靠白块兜底。
        /// </summary>
        private static List<PageObjectInfo> ReadPageObjects(IntPtr page)
        {
            int count;

            try
            {
                count = FPDFPage_CountObjects(page);
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }

            if (count <= 0)
                return new List<PageObjectInfo>();

            var list = new List<PageObjectInfo>(count);
            IntPtr textPage = IntPtr.Zero;

            // 文本对象的内容要靠「文本页」接口读（拿不到就只按类型处理，不删文本）
            try
            {
                textPage = FPDFText_LoadPage(page);
            }
            catch (EntryPointNotFoundException)
            {
                textPage = IntPtr.Zero;
            }

            try
            {
                for (int i = 0; i < count; i++)
                {
                    IntPtr handle = FPDFPage_GetObject(page, i);
                    if (handle == IntPtr.Zero)
                        continue;

                    int type = FPDFPageObj_GetType(handle);

                    float left, bottom, right, top;
                    if (FPDFPageObj_GetBounds(handle, out left, out bottom, out right, out top) == 0)
                        continue;

                    var info = new PageObjectInfo
                    {
                        Handle = handle,
                        Index = i,
                        Type = type,
                        Left = left,
                        Bottom = bottom,
                        Right = right,
                        Top = top
                    };

                    if (type == PageObjText && textPage != IntPtr.Zero)
                        info.IsDigitText = IsDigitOnlyTextObject(handle, textPage);

                    list.Add(info);
                }
            }
            finally
            {
                if (textPage != IntPtr.Zero)
                {
                    try
                    {
                        FPDFText_ClosePage(textPage);
                    }
                    catch (EntryPointNotFoundException)
                    {
                        // 没有这个入口：dll 里那个文本页句柄随文档一起结束，不用管
                    }
                }
            }

            return list;
        }

        /// <summary>
        /// 文本对象的整串内容是不是 1~4 位数字（CAD 里一个数字常常就是一个独立文本对象）。
        /// <c>100 1</c> 这种混着尺寸的返回 false —— 删掉整个对象会连尺寸一起丢。
        /// </summary>
        private static bool IsDigitOnlyTextObject(IntPtr handle, IntPtr textPage)
        {
            int bytes;

            try
            {
                bytes = (int)FPDFTextObj_GetText(handle, textPage, IntPtr.Zero, 0);
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }

            // UTF-16LE：1~4 位数字最多 8 字节，更大的肯定不是编号
            if (bytes <= 0 || bytes > 16)
                return false;

            IntPtr buffer = Marshal.AllocHGlobal(bytes + 2);

            try
            {
                uint written = FPDFTextObj_GetText(handle, textPage, buffer, (uint)(bytes + 2));
                if (written == 0)
                    return false;

                return IsDigitOnly(Marshal.PtrToStringUni(buffer));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>整串就是 1~4 位数字（允许两端空白；全角数字也认）。</summary>
        private static bool IsDigitOnly(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            string value = text.Trim();

            if (value.Length < 1 || value.Length > TextObjDigitMaxLength)
                return false;

            foreach (char c in value)
            {
                bool digit = (c >= '0' && c <= '9')
                             || (c >= '\uFF10' && c <= '\uFF19');

                if (!digit)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// 这个对象能不能为了 △ 注記符号整删：路径对象可以；文本对象只有「整串就是数字」的可以。
        /// 图片 / 阴影 / Form XObject（里面可能是别的图形）一律不动。
        /// </summary>
        private static bool IsDeletableObject(PageObjectInfo obj)
        {
            if (obj == null)
                return false;

            if (obj.Type == PageObjPath)
                return true;

            return obj.Type == PageObjText && obj.IsDigitText;
        }

        /// <summary>对象包围盒与三角形框是否相交（容差 <see cref="PathEraseTolerancePt"/>）。</summary>
        private static bool Intersects(PageObjectInfo obj, TriangleBox box)
        {
            return obj.Right >= box.Left - PathEraseTolerancePt
                   && obj.Left <= box.Right + PathEraseTolerancePt
                   && obj.Top >= box.Bottom - PathEraseTolerancePt
                   && obj.Bottom <= box.Top + PathEraseTolerancePt;
        }

        /// <summary>
        /// 对象包围盒是不是整个落在三角形框里 —— py 的 <c>_should_remove_subpath</c> 的对象级版本。
        /// 长表格线的包围盒远远超出三角形框，所以永远不会被这条判据选中。
        /// </summary>
        private static bool FullyInside(PageObjectInfo obj, TriangleBox box)
        {
            return obj.Left >= box.Left - PathEraseTolerancePt
                   && obj.Right <= box.Right + PathEraseTolerancePt
                   && obj.Bottom >= box.Bottom - PathEraseTolerancePt
                   && obj.Top <= box.Top + PathEraseTolerancePt;
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

        // ── 页面对象 / 内容流（删对象 + 插白矩形 + 生成内容流）──

        [DllImport(PdfiumDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFPage_InsertObject(IntPtr page, IntPtr pageObject);

        /// <summary>页面上的对象个数（含 Form XObject，但看不到 form 内部的对象）。</summary>
        [DllImport(PdfiumDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFPage_CountObjects(IntPtr page);

        [DllImport(PdfiumDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFPage_GetObject(IntPtr page, int index);

        /// <summary>对象类型，取值见 FPDF_PAGEOBJECTTYPE（1 = TEXT、2 = PATH、3 = IMAGE、5 = FORM）。</summary>
        [DllImport(PdfiumDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFPageObj_GetType(IntPtr pageObject);

        /// <summary>对象包围盒（pt，原点在左下，与 PdfPig 的页面坐标一致），返回 1 表示成功。</summary>
        [DllImport(PdfiumDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFPageObj_GetBounds(
            IntPtr pageObject, out float left, out float bottom, out float right, out float top);

        /// <summary>从页面删掉一个对象，返回 1 表示成功（之后对象归 PDFium 管，不要再用这个句柄）。</summary>
        [DllImport(PdfiumDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFPage_RemoveObject(IntPtr page, IntPtr pageObject);

        /// <summary>打开页面的文本页接口（读文本对象内容用），用完必须 <see cref="FPDFText_ClosePage"/>。</summary>
        [DllImport(PdfiumDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFText_LoadPage(IntPtr page);

        [DllImport(PdfiumDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFText_ClosePage(IntPtr textPage);

        /// <summary>
        /// 文本对象的文字（UTF-16LE，带结尾 0）：<paramref name="buffer"/> 传 <see cref="IntPtr.Zero"/>、
        /// <paramref name="length"/> 传 0 时返回需要的字节数。返回写入的字节数。
        /// </summary>
        [DllImport(PdfiumDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint FPDFTextObj_GetText(
            IntPtr textObject, IntPtr textPage, IntPtr buffer, uint length);

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
