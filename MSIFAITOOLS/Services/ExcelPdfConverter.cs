// ─────────────────────────────────────────────────────────────────────────────
// 【已停用】Excel → PDF 导出（旧 Excel 预览窗口的【PDF 视图】用）——
// 已从 MSIFAITOOLS.csproj 的 <Compile> 列表移除，不参与编译。
//
// 现在 Excel 行直接用本机 Excel 打开并置前（见 Services\ExcelLauncher.cs）：
// 公式 / 图片 / 图表 / 多个工作表都由 Excel 自己显示，不需要再导出成 PDF 再用 pdfium 渲染。
//
// 需要恢复【PDF 视图】时：把本文件与 XlsxPreviewForm.cs 重新加回 csproj 的 <Compile> 列表。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// 用本机安装的 Excel（COM）把 Excel 文件导出成 PDF，供 Excel 预览窗口「原样」显示。
    /// <para>
    /// 为什么不在窗口里直接放 Excel：<c>WebBrowser</c> 是 IE（MSHTML），没有 Excel 的渲染器；
    /// 老的 OWC（Office Web Components）ActiveX 从 Office 2007 起不再随 Office 分发、
    /// 还被 IE 的 killbit 屏蔽，而且本程序是 x64 —— 32 位的 OWC 根本加载不了。
    /// 把 Excel 的窗口「塞」进自己的窗口（SetParent）又会碰到单实例抢窗口 / 进程残留的问题。
    /// 交给 Excel 自己排版成 PDF，再用工程里已有的 pdfium 渲染，是保真度和稳定性之间最合适的做法。
    /// </para>
    /// <para>
    /// <b>只读、不保存</b>：以 <c>ReadOnly:=true</c> 打开工作簿，导出后 <c>Close(false)</c>，
    /// 从不写回源文件。转换结果按「源文件路径 + 大小 + 最后写入时间」缓存在
    /// <c>%TEMP%\MSIFAITOOLS\ExcelPreview</c>，同一个文件第二次起秒开。
    /// </para>
    /// <para>
    /// 用 late binding（<see cref="Type.GetTypeFromProgID"/> + <c>dynamic</c>），
    /// 不引用 <c>Microsoft.Office.Interop.Excel</c>：现场机器没装 Excel 时程序照常启动，
    /// 只是本类返回 <c>false</c>，预览窗口退回表格预览。
    /// </para>
    /// <para>
    /// <b>必须在 STA 线程调用</b>（WinForms 的 UI 线程即是）。COM 调用是同步的，
    /// 大工作簿会让界面卡住几秒，调用方应先显示「正在生成…」并切等待光标。
    /// </para>
    /// </summary>
    internal static class ExcelPdfConverter
    {
        /// <summary>Excel 的 ProgID。</summary>
        private const string ExcelProgId = "Excel.Application";

        /// <summary><c>xlTypePDF</c>：<c>ExportAsFixedFormat</c> 的导出格式。</summary>
        private const int XlTypePdf = 0;

        /// <summary><c>xlQualityStandard</c>：标准画质（够看，文件也不会太大）。</summary>
        private const int XlQualityStandard = 0;

        /// <summary>本机是否装了 Excel（只查注册表里的 ProgID，不启动 Excel）。</summary>
        public static bool IsExcelAvailable
        {
            get
            {
                try
                {
                    return Type.GetTypeFromProgID(ExcelProgId) != null;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>缓存目录（不存在时自动创建）。</summary>
        public static string CacheDirectory
        {
            get
            {
                string directory = Path.Combine(Path.GetTempPath(), "MSIFAITOOLS", "ExcelPreview");

                Directory.CreateDirectory(directory);

                return directory;
            }
        }

        /// <summary>
        /// 缓存 PDF 的完整路径：由「完整路径 + 文件大小 + 最后写入时间」算 SHA-256。
        /// 源文件一改，缓存路径就变，天然不会显示旧的转换结果。
        /// </summary>
        public static string BuildCachePath(string excelFilePath)
        {
            string fullPath = Path.GetFullPath(excelFilePath);
            FileInfo info = new FileInfo(fullPath);
            string stamp = string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}",
                fullPath.ToLowerInvariant(),
                info.Exists ? info.Length : 0L,
                info.Exists ? info.LastWriteTimeUtc.Ticks : 0L);

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(stamp));
                StringBuilder builder = new StringBuilder(hash.Length * 2);

                foreach (byte value in hash)
                {
                    builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                }

                return Path.Combine(CacheDirectory, builder.ToString(0, 32) + ".pdf");
            }
        }

        /// <summary>
        /// 取之前转换好的 PDF（没有 / 源文件改过 / 读不了时返回 <c>false</c>）。
        /// 有缓存时预览窗口可以直接进 PDF 视图，不用等 Excel 启动。
        /// </summary>
        public static bool TryGetCachedPdf(string excelFilePath, out string pdfPath)
        {
            pdfPath = null;

            if (string.IsNullOrWhiteSpace(excelFilePath) || !File.Exists(excelFilePath))
            {
                return false;
            }

            try
            {
                string cachePath = BuildCachePath(excelFilePath);

                if (!File.Exists(cachePath) || new FileInfo(cachePath).Length <= 0)
                {
                    return false;
                }

                pdfPath = cachePath;

                return true;
            }
            catch (Exception)
            {
                // 缓存只是加速用的：取不到就当没有，重新转换
                return false;
            }
        }

        /// <summary>
        /// 删掉这个 Excel 对应的缓存 PDF（【刷新】时用：别的程序可能刚改过内容）。
        /// <b>调用前必须先释放 pdfium 的 <c>PdfDocument</c></b>，否则文件还被占着删不掉。
        /// </summary>
        public static void DeleteCachedPdf(string excelFilePath)
        {
            if (string.IsNullOrWhiteSpace(excelFilePath))
            {
                return;
            }

            try
            {
                string cachePath = BuildCachePath(excelFilePath);

                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                }
            }
            catch (Exception)
            {
                // 删不掉（被占用）不影响功能：缓存文件名带源文件时间戳，下次会重新生成新文件
            }
        }

        /// <summary>
        /// 用 Excel 把工作簿（全部工作表）导出成 PDF，成功时 <paramref name="pdfPath"/> 是缓存文件路径。
        /// 失败返回 <c>false</c>，原因放在 <paramref name="error"/>（已是当前语言，可直接显示在状态栏）。
        /// </summary>
        /// <param name="excelFilePath">Excel 文件完整路径（.xlsx / .xlsm / .xlsb / .xls 都行）。</param>
        /// <param name="pdfPath">成功时的缓存 PDF 路径。</param>
        /// <param name="error">失败原因。</param>
        public static bool TryConvert(string excelFilePath, out string pdfPath, out string error)
        {
            pdfPath = null;
            error = null;

            if (string.IsNullOrWhiteSpace(excelFilePath) || !File.Exists(excelFilePath))
            {
                error = UiText.F("文件不存在：{0}", excelFilePath ?? string.Empty);

                return false;
            }

            Type excelType;

            try
            {
                excelType = Type.GetTypeFromProgID(ExcelProgId);
            }
            catch (Exception ex)
            {
                error = UiText.F("生成 PDF 失败：{0}", Describe(ex));

                return false;
            }

            if (excelType == null)
            {
                error = UiText.T("本机没有安装 Excel。");

                return false;
            }

            string targetPath;
            string temporaryPath;

            try
            {
                targetPath = BuildCachePath(excelFilePath);
                temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            }
            catch (Exception ex)
            {
                error = UiText.F("生成 PDF 失败：{0}", Describe(ex));

                return false;
            }

            object application = null;
            object workbook = null;
            bool quitApplication = false;

            try
            {
                application = Activator.CreateInstance(excelType);

                dynamic app = application;

                // CreateObject 一般会开一个新的 Excel 进程；万一连到的是用户正在用的实例，
                // 就不能把它藏起来、更不能 Quit（会把用户打开的工作簿一起关掉）
                bool ownInstance = GetWorkbookCount(app) == 0;

                quitApplication = ownInstance;

                bool originalAlerts = true;

                try
                {
                    originalAlerts = (bool)app.DisplayAlerts;
                }
                catch (Exception)
                {
                    // 读不到就按默认值 true 处理（后面写完再恢复一次，失败也无所谓）
                }

                try
                {
                    app.DisplayAlerts = false;
                }
                catch (Exception)
                {
                }

                if (ownInstance)
                {
                    try
                    {
                        app.Visible = false;
                    }
                    catch (Exception)
                    {
                    }

                    try
                    {
                        app.ScreenUpdating = false;
                    }
                    catch (Exception)
                    {
                    }
                }

                try
                {
                    // UpdateLinks=0（不更新外部链接）、ReadOnly=true（绝不写回源文件）
                    workbook = app.Workbooks.Open(excelFilePath, 0, true);

                    // Type.Missing 占位：#6 From / #7 To 用默认值（= 全部页）；
                    // #5 IgnorePrintAreas=false：尊重 Excel 里设的打印区域，导出的版面才与打印一致
                    ((dynamic)workbook).ExportAsFixedFormat(XlTypePdf, temporaryPath, XlQualityStandard,
                        true, false, Type.Missing, Type.Missing, false);
                }
                finally
                {
                    if (workbook != null)
                    {
                        try
                        {
                            ((dynamic)workbook).Close(false);
                        }
                        catch (Exception)
                        {
                        }
                    }

                    try
                    {
                        app.DisplayAlerts = originalAlerts;
                    }
                    catch (Exception)
                    {
                    }
                }

                if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length <= 0)
                {
                    error = UiText.T("Excel 没有生成 PDF 文件。");

                    return false;
                }

                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }

                File.Move(temporaryPath, targetPath);

                pdfPath = targetPath;

                return true;
            }
            catch (Exception ex)
            {
                error = UiText.F("生成 PDF 失败：{0}", Describe(ex));

                return false;
            }
            finally
            {
                ReleaseComObject(workbook);

                if (application != null)
                {
                    if (quitApplication)
                    {
                        try
                        {
                            ((dynamic)application).Quit();
                        }
                        catch (Exception)
                        {
                        }
                    }

                    ReleaseComObject(application);
                }

                TryDeleteFile(temporaryPath);
            }
        }

        /// <summary>读 <c>Application.Workbooks.Count</c>；读不到返回 -1（当作「不是自己的实例」，最安全）。</summary>
        private static int GetWorkbookCount(dynamic app)
        {
            try
            {
                return (int)app.Workbooks.Count;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>COM 异常文字常带很长的 HRESULT 说明：只留第一行并限长，状态栏才放得下。</summary>
        private static string Describe(Exception ex)
        {
            if (ex == null)
            {
                return string.Empty;
            }

            string message = ex.Message ?? string.Empty;
            int lineBreak = message.IndexOfAny(new[] { '\r', '\n' });

            if (lineBreak >= 0)
            {
                message = message.Substring(0, lineBreak);
            }

            if (message.Length > 240)
            {
                message = message.Substring(0, 240) + "…";
            }

            return message;
        }

        /// <summary>释放 COM 对象（不是 COM 对象时静默跳过）。</summary>
        private static void ReleaseComObject(object value)
        {
            if (value == null || !Marshal.IsComObject(value))
            {
                return;
            }

            try
            {
                Marshal.FinalReleaseComObject(value);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>删临时文件（失败忽略）。</summary>
        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
