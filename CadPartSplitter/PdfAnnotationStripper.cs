using System;
using System.IO;
using System.Runtime.InteropServices;
using Docnet.Core;

namespace CadPartSplitter
{
    /// <summary>
    /// 识别前的 PDF 预处理：删掉页面上的「批注 / 图章」等注释，另存一份干净 PDF。
    ///
    /// <para>
    /// 调用点只有一处 —— <see cref="PartSplitterService"/> 的 <c>Run</c> 开头把这里返回的路径交给渲染
    /// （<c>LoadImage</c> → <see cref="PdfRenderer"/>）。文字 / 表格 / 圈号仍读原 PDF：
    /// 批注是 annotation，不在页面内容流里，干净 PDF 与原 PDF 的文字层、矢量图形完全一致，
    /// 这样产物（<c>parts_text.txt</c> 的 source、<c>PdfInfoList.txt</c> 的文件名等）不会被改成 <c>xxx_clean.pdf</c>。
    /// </para>
    ///
    /// <para>
    /// 实现：直接调 PDFium 的 C 接口（Docnet.Core 的包已带 <c>pdfium.dll</c>，见 csproj 的
    /// <c>DocnetRuntime</c>；运行时文档也已由 <see cref="DocLib"/> 初始化过）：
    /// </para>
    /// <list type="number">
    ///   <item><c>FPDF_LoadMemDocument</c> 把原 PDF 读进内存（只读，原文件不会被改）；</item>
    ///   <item>逐页 <c>FPDFPage_GetAnnotCount</c> + <c>FPDFPage_RemoveAnnot</c> 把注释全部删掉
    ///         （图章 Stamp / 便签 Text / 修订 Ink、Square、FreeText 都属 annotation，一并删掉）；</item>
    ///   <item><c>FPDF_SaveAsCopy</c> 另存为 <c>&lt;图纸名&gt;_clean.pdf</c>。</item>
    /// </list>
    ///
    /// <para>
    /// 任何一步失败（pdfium 加载不到、PDF 加密、另存失败……）都只返回原路径 + 告警，
    /// 识别流程照常进行 —— 这条预处理是「锦上添花」，绝不让它把正常识别搞崩。
    /// </para>
    ///
    /// <para>
    /// 注意：只有真正的 PDF 注释能被删掉。已经被「压扁」（flatten）进页面内容流的图章 / 印章，
    /// 在 PDF 里就是普通矢量图形，任何库都无法区分它与图纸线条，那种只能靠人工处理。
    /// </para>
    /// </summary>
    internal static class PdfAnnotationStripper
    {
        /// <summary>清理后文件名的后缀：<c>&lt;图纸名&gt;_clean.pdf</c>。</summary>
        public const string CleanSuffix = "_clean";

        /// <summary>PDFium 的另存回调只有一个全局输出流，并发调用没有意义，串行化保险。</summary>
        private static readonly object SyncRoot = new object();

        /// <summary>回调当前写往哪个流（PDFium 的回调是静态函数指针，只能用静态字段传递）。</summary>
        private static FileStream _currentStream;

        /// <summary>
        /// 返回「渲染要用的 PDF 路径」：
        /// 有批注 → 删掉后另存的 <c>&lt;输出目录&gt;\&lt;图纸名&gt;_clean.pdf</c>；
        /// 无批注 / 非 PDF / 出错 → 原样返回 <paramref name="inputPath"/>。
        /// </summary>
        /// <param name="inputPath">原始输入文件（PDF / PNG / JPG / …）。</param>
        /// <param name="opts">识别参数：<see cref="DetectorOptions.OutputDirectory"/> 必须已确定。</param>
        public static string PrepareInput(string inputPath, DetectorOptions opts)
        {
            if (opts == null || !opts.StripPdfAnnotations)
                return inputPath;

            if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
                return inputPath;

            if (!string.Equals(Path.GetExtension(inputPath), ".pdf", StringComparison.OrdinalIgnoreCase))
                return inputPath;

            string outputDir = opts.OutputDirectory;
            if (string.IsNullOrWhiteSpace(outputDir))
                return inputPath;

            string cleanPath = null;

            try
            {
                Directory.CreateDirectory(outputDir);

                cleanPath = Path.Combine(
                    outputDir,
                    Path.GetFileNameWithoutExtension(inputPath) + CleanSuffix + ".pdf");

                // 上次清理的副本还在、且不比源文件旧 → 直接复用（同一张图重跑省一次重写）
                if (File.Exists(cleanPath)
                    && File.GetLastWriteTimeUtc(cleanPath) >= File.GetLastWriteTimeUtc(inputPath))
                {
                    opts.Log("[INFO] 批注清理：复用上次清理的 PDF " + Path.GetFileName(cleanPath));
                    return cleanPath;
                }

                int removed;
                string error;

                if (!TryStrip(inputPath, cleanPath, out removed, out error))
                {
                    opts.Log("[WARN] 批注清理失败（" + error + "），改用原 PDF 识别。");
                    return inputPath;
                }

                if (removed <= 0)
                {
                    // 没有批注：不留空转的副本，免得输出目录里多一个让人误解的文件
                    TryDelete(cleanPath);
                    opts.Log("[INFO] 批注清理：该 PDF 没有批注 / 图章，直接用原文件识别。");
                    return inputPath;
                }

                opts.Log("[INFO] 批注清理：删除 " + removed + " 个批注 / 图章 → " + Path.GetFileName(cleanPath));

                // 再确认这份副本能被 PDFium 正常打开（页数 > 0 且与原 PDF 一致），否则宁可退回原 PDF：
                // 预处理只是为了让批注别进渲染图，不能反过来把一次好好的识别搞崩
                //（页码用的是同一个 PdfPageIndex，页数不一致就有越界风险）。
                int sourcePages = 0;
                int cleanPages  = 0;

                try
                {
                    sourcePages = PdfRenderer.GetPageCount(inputPath);
                }
                catch (Exception)
                {
                    sourcePages = 0;
                }

                try
                {
                    cleanPages = PdfRenderer.GetPageCount(cleanPath);
                }
                catch (Exception)
                {
                    cleanPages = 0;
                }

                if (cleanPages <= 0 || (sourcePages > 0 && cleanPages != sourcePages))
                {
                    TryDelete(cleanPath);
                    opts.Log("[WARN] 批注清理：生成的干净 PDF 页数不对（原 " + sourcePages
                             + " 页 / 新 " + cleanPages + " 页），改用原 PDF 识别。");
                    return inputPath;
                }

                return cleanPath;
            }
            catch (Exception ex)
            {
                opts.Log("[WARN] 批注清理出错（" + ex.GetType().Name + "：" + ex.Message + "），改用原 PDF 识别。");
                return inputPath;
            }
        }

        /// <summary>
        /// 真正干活：删掉 <paramref name="sourcePath"/> 里所有页面上的注释，另存到 <paramref name="targetPath"/>。
        /// </summary>
        /// <param name="removed">删掉的注释个数（0 = 原本就没有注释，此时不写文件）。</param>
        /// <param name="error">失败原因（成功时为 null）。</param>
        /// <returns>成功（含「没有注释」）返回 true；pdfium 不可用 / PDF 打不开 / 另存失败返回 false。</returns>
        private static bool TryStrip(string sourcePath, string targetPath, out int removed, out string error)
        {
            lock (SyncRoot)
            {
                removed = 0;
                error   = null;

                IntPtr document    = IntPtr.Zero;
                IntPtr sourceCopy  = IntPtr.Zero;
                IntPtr writeStruct = IntPtr.Zero;

                WriteBlockDelegate callback = WriteBlock;   // 必须保持引用：函数指针指向它，被 GC 回收就崩

                try
                {
                    byte[] bytes = File.ReadAllBytes(sourcePath);

                    // 让 Docnet 先把 PDFium 初始化好（它的静态构造里会调 FPDF_InitLibrary），
                    // 之后才可以直接调 FPDF_* 接口，不必自己再 Init 一遍。
                    object docLib = DocLib.Instance;
                    GC.KeepAlive(docLib);

                    sourceCopy = Marshal.AllocHGlobal(bytes.Length);
                    Marshal.Copy(bytes, 0, sourceCopy, bytes.Length);

                    document = FPDF_LoadMemDocument(sourceCopy, bytes.Length, IntPtr.Zero);
                    if (document == IntPtr.Zero)
                    {
                        error = "PDFium 打不开该 PDF（可能加密或已损坏）";
                        return false;
                    }

                    int pageCount = FPDF_GetPageCount(document);
                    for (int i = 0; i < pageCount; i++)
                    {
                        IntPtr page = FPDF_LoadPage(document, i);
                        if (page == IntPtr.Zero)
                            continue;

                        try
                        {
                            // 从后往前删：删掉一个，索引就变；倒序删除不用重排
                            int count = FPDFPage_GetAnnotCount(page);
                            while (count > 0)
                            {
                                FPDFPage_RemoveAnnot(page, count - 1);
                                removed++;

                                int now = FPDFPage_GetAnnotCount(page);
                                if (now >= count)
                                    break;      // 没减少 → 防止死循环

                                count = now;
                            }
                        }
                        finally
                        {
                            FPDF_ClosePage(page);
                        }
                    }

                    if (removed <= 0)
                        return true;            // 没有注释：不生成副本

                    using (var stream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        _currentStream = stream;

                        writeStruct = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FPDF_FILEWRITE)));
                        var fileWrite = new FPDF_FILEWRITE
                        {
                            version    = 1,
                            WriteBlock = Marshal.GetFunctionPointerForDelegate(callback)
                        };
                        Marshal.StructureToPtr(fileWrite, writeStruct, false);

                        // flags = 0：默认保存模式（PDFium 会完整重写一遍文档）
                        int ok = FPDF_SaveAsCopy(document, writeStruct, 0);
                        _currentStream = null;

                        if (ok == 0)
                        {
                            error = "PDFium 另存失败";
                            return false;
                        }
                    }

                    // 只写了 0 字节 / 文件没落地都当失败，免得后续渲染拿到一个坏文件
                    var written = new FileInfo(targetPath);
                    if (!written.Exists || written.Length == 0)
                    {
                        error = "另存出来的 PDF 是空文件";
                        return false;
                    }

                    return true;
                }
                catch (DllNotFoundException)
                {
                    error = "找不到 pdfium.dll（Docnet.Core 的原生库未随程序一起部署）";
                    return false;
                }
                catch (BadImageFormatException)
                {
                    error = "pdfium.dll 位数不匹配（本程序须以 x64 运行）";
                    return false;
                }
                catch (EntryPointNotFoundException ex)
                {
                    error = "当前 pdfium.dll 版本缺少所需接口：" + ex.Message;
                    return false;
                }
                catch (Exception ex)
                {
                    error = ex.GetType().Name + "：" + ex.Message;
                    return false;
                }
                finally
                {
                    _currentStream = null;

                    if (document != IntPtr.Zero)
                        FPDF_CloseDocument(document);

                    if (writeStruct != IntPtr.Zero)
                        Marshal.FreeHGlobal(writeStruct);

                    if (sourceCopy != IntPtr.Zero)
                        Marshal.FreeHGlobal(sourceCopy);
                }
            }
        }

        /// <summary>删除成功但一个注释都没有时，把可能残留的旧副本清掉。</summary>
        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
                // 副本被别的程序占用：留着也无害
            }
            catch (UnauthorizedAccessException)
            {
                // 同上
            }
        }

        // ── PDFium C 接口（Docnet.Core 的包已带 pdfium.dll；函数全部 cdecl）──────────

        private const string PdfiumDll = "pdfium.dll";

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

        [DllImport(PdfiumDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFPage_GetAnnotCount(IntPtr page);

        [DllImport(PdfiumDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFPage_RemoveAnnot(IntPtr page, int index);

        [DllImport(PdfiumDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDF_SaveAsCopy(IntPtr document, IntPtr fileWrite, uint flags);
    }
}
