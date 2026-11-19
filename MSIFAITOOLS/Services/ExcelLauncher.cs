using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace MSIFAITOOLS.Services
{
    /// <summary>打开 Excel 文件的三种结果（供画面决定状态栏文字）。</summary>
    internal enum ExcelOpenOutcome
    {
        /// <summary>该工作簿已经在 Excel 里打开：只把它的窗口激活并放到最前。</summary>
        Activated,

        /// <summary>由本工具在运行中的 Excel 里打开（原本没有打开这个文件）。</summary>
        Opened,

        /// <summary>没有可控制的 Excel（未安装 / 运行在别的权限级别）：交给系统默认程序打开。</summary>
        ShellOpened
    }

    /// <summary>打开 Excel 文件的结果：成功与否 + 说明（失败时 <see cref="Error"/> 是原因）。</summary>
    internal struct ExcelOpenResult
    {
        /// <summary>是否已经打开（或交给系统打开）。</summary>
        public bool Success;

        /// <summary>成功时的打开方式。</summary>
        public ExcelOpenOutcome Outcome;

        /// <summary>失败原因（成功时为空字符串）。</summary>
        public string Error;
    }

    /// <summary>
    /// 用本机安装的 Excel 打开工作簿，并把 Excel 窗口放到最前。
    /// <para>
    /// 【分类结果】里的 Excel 行（<c>.xlsx</c> / <c>.xlsm</c> / <c>.xlsb</c> / <c>.xls</c>）不再用本工具的
    /// 预览窗口显示：公式、图片、图表、多个工作表都是 Excel 自己的显示结果，直接用 Excel 看最准确。
    /// </para>
    /// <list type="bullet">
    /// <item>该工作簿<b>已经在 Excel 里打开</b> → 只激活那个工作簿窗口 + 把 Excel 放到最前，不再打开第二份；</item>
    /// <item>Excel 正在运行但没打开这个文件 → 在同一个 Excel 实例里 <c>Workbooks.Open</c>（不新起进程）；</item>
    /// <item>没有运行中的 Excel → 用系统文件关联打开（Excel / WPS 都可以）；</item>
    /// <item>本机没有 Excel → 由系统关联程序打开（例如 WPS），失败时返回原因。</item>
    /// </list>
    /// <para>
    /// 全部用 late binding（<c>Marshal.GetActiveObject</c> + <c>dynamic</c>），
    /// <b>不引用 Microsoft.Office.Interop.Excel</b>：
    /// 没装 Excel 的机器也能正常启动本程序，只在真的点 Excel 行时才提示。
    /// </para>
    /// <para>
    /// 只能在 UI 线程（STA，带消息泵）调用：COM 的 <c>GetActiveObject</c> 要求 STA，
    /// 且 Excel 弹出的对话框需要消息泵才能处理。
    /// </para>
    /// </summary>
    internal static class ExcelLauncher
    {
        /// <summary>ShowWindow 的 SW_RESTORE：把最小化的窗口还原。</summary>
        private const int SwRestore = 9;

        /// <summary>窗口是否最小化。</summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        /// <summary>异步还原 / 显示窗口（不等待，避免卡住本进程）。</summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        /// <summary>把窗口放到最前并给它焦点。</summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>
        /// 打开 / 激活指定工作簿（见类型说明里的四种情况）。
        /// </summary>
        /// <param name="filePath">Excel 文件路径。</param>
        /// <returns>打开结果；失败时 <see cref="ExcelOpenResult.Error"/> 是原因。</returns>
        public static ExcelOpenResult OpenOrActivate(string filePath)
        {
            ExcelOpenResult result = new ExcelOpenResult();
            result.Error = string.Empty;

            if (string.IsNullOrWhiteSpace(filePath))
            {
                result.Error = "文件路径为空。";
                return result;
            }

            string fullPath;

            try
            {
                fullPath = Path.GetFullPath(filePath);
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }

            if (!File.Exists(fullPath))
            {
                result.Error = "文件不存在：" + fullPath;
                return result;
            }

            Exception comFailure = null;
            object runningExcel = TryGetRunningExcel();

            if (runningExcel != null)
            {
                try
                {
                    ExcelOpenOutcome outcome;

                    if (TryUseRunningExcel(runningExcel, fullPath, out outcome))
                    {
                        result.Success = true;
                        result.Outcome = outcome;
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    // 用户取消了 Excel 的对话框（只读 / 修复提示）/ 文件格式不支持等：
                    // 记下原因，下面再交给系统关联程序，让用户看到 Excel 自己的提示
                    comFailure = ex;
                }
                finally
                {
                    ReleaseComObject(runningExcel);
                }
            }

            // 没有运行中的 Excel（或 COM 打开失败）：交给 Windows 的文件关联。
            // 此时 Excel 里如果其实已经开着这个文件，Windows / Excel 的 DDE 会激活那个窗口。
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo(fullPath);
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);

                result.Success = true;
                result.Outcome = ExcelOpenOutcome.ShellOpened;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = comFailure == null
                    ? ex.Message
                    : comFailure.Message + " / " + ex.Message;

                return result;
            }
        }

        /// <summary>
        /// 在运行中的 Excel 里找到 / 打开工作簿并置前。
        /// </summary>
        /// <param name="runningExcel">运行中的 Excel.Application（<c>Marshal.GetActiveObject</c> 得到）。</param>
        /// <param name="fullPath">工作簿的完整路径。</param>
        /// <param name="outcome">成功时的打开方式。</param>
        /// <returns>成功返回 true；COM 调用抛异常时由调用方处理（这里不吞）。</returns>
        private static bool TryUseRunningExcel(object runningExcel, string fullPath, out ExcelOpenOutcome outcome)
        {
            outcome = ExcelOpenOutcome.Activated;

            dynamic excel = runningExcel;

            object openBook = FindOpenWorkbook(excel, fullPath);

            if (openBook != null)
            {
                dynamic book = openBook;

                // 隐藏的自动化实例（Visible = false）也要显示出来，否则「放到最前」什么也看不到
                MakeVisible(excel);

                // 同一个 Excel 里可能开着好几个文件：先激活这个工作簿，再激活 Excel 窗口
                book.Activate();

                PutExcelToFront(excel);

                outcome = ExcelOpenOutcome.Activated;
                return true;
            }

            // 没打开过：在同一个 Excel 实例里打开（不新起 EXCEL.EXE）。
            // 只传文件名，其余用 Excel 的默认行为 —— 和用户双击文件一样，
            // 只读 / 修复等提示由 Excel 自己弹。
            dynamic opened = excel.Workbooks.Open(fullPath);

            MakeVisible(excel);

            opened.Activate();

            PutExcelToFront(excel);

            outcome = ExcelOpenOutcome.Opened;
            return true;
        }

        /// <summary>
        /// 取运行中的 Excel 实例（Running Object Table 里注册的 "Excel.Application"）。
        /// 没有正在运行的 Excel 时返回 <c>null</c>（这是正常情况，不是错误）。
        /// </summary>
        private static object TryGetRunningExcel()
        {
            try
            {
                return Marshal.GetActiveObject("Excel.Application");
            }
            catch (COMException)
            {
                // MK_E_UNAVAILABLE：没有运行中的 Excel（或者 Excel 运行在别的权限级别）
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 在 Excel 已打开的工作簿里找路径相同的那个（不区分大小写，短路径 / 长路径都按完整路径比较）。
        /// </summary>
        /// <returns>找到时返回该工作簿的 COM 对象；没有时返回 <c>null</c>。</returns>
        private static object FindOpenWorkbook(dynamic excel, string fullPath)
        {
            dynamic workbooks = excel.Workbooks;
            int count = Convert.ToInt32(workbooks.Count);

            for (int index = 1; index <= count; index++)
            {
                try
                {
                    dynamic book = workbooks[index];

                    if (SamePath(Convert.ToString(book.FullName), fullPath))
                    {
                        return book;
                    }
                }
                catch (Exception)
                {
                    // 个别工作簿（加载中 / 受保护视图）取不到 FullName：跳过，不影响其它工作簿
                }
            }

            return null;
        }

        /// <summary>两个路径是否指同一个文件（按完整路径比较，不区分大小写）。</summary>
        private static bool SamePath(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            {
                return false;
            }

            try
            {
                return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>把 Excel 主窗口显示出来（隐藏的自动化实例 → 可见）。</summary>
        private static void MakeVisible(dynamic excel)
        {
            try
            {
                excel.Visible = true;
            }
            catch (Exception)
            {
                // 个别版本上 Application.Visible 会抛异常：不影响后面的工作簿激活
            }
        }

        /// <summary>
        /// 把 Excel 主窗口放到最前：最小化时先还原，再 <c>SetForegroundWindow</c>。
        /// <para>
        /// Windows 的前台锁（foreground lock）可能让 <c>SetForegroundWindow</c> 失败 ——
        /// 那也没关系：上面的 <c>Activate</c> 通常已经把窗口提到前面，这里只是补一刀。
        /// </para>
        /// </summary>
        private static void PutExcelToFront(dynamic excel)
        {
            try
            {
                IntPtr hwnd = new IntPtr(Convert.ToInt32(excel.Hwnd));

                if (hwnd == IntPtr.Zero)
                {
                    return;
                }

                if (IsIconic(hwnd))
                {
                    ShowWindowAsync(hwnd, SwRestore);
                }

                SetForegroundWindow(hwnd);
            }
            catch (Exception)
            {
                // 取不到 Hwnd / 窗口已经关掉：忽略
            }
        }

        /// <summary>释放 COM 引用（Excel 进程本身继续运行，这里是用户已经开着的实例）。</summary>
        private static void ReleaseComObject(object comObject)
        {
            if (comObject == null)
            {
                return;
            }

            try
            {
                if (Marshal.IsComObject(comObject))
                {
                    Marshal.ReleaseComObject(comObject);
                }
            }
            catch (Exception)
            {
                // 已经断开 / 进程已经退出：忽略
            }
        }
    }
}
