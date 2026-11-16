using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using MSIFAITOOLS.Services;
using UglyToad.PdfPig;

namespace MSIFAITOOLS
{
    public partial class Form1 : Form
    {
        /// <summary>最近一次分解的输出根目录（&lt;PDF 文件夹&gt;\AiAppData，其下每个 PDF 一个「図番号」子文件夹）。</summary>
        private string _lastOutputDir;

        private string _classifyDirectory;

        private IList<PdfClassification> _classifications;

        private bool _isRunning;

        private string _startButtonText;

        /// <summary>【AI 分析】按钮的初始文字（运行中显示“分析中...”）。</summary>
        private string _aiButtonText;

        /// <summary>【CMD 分析】按钮的初始文字（运行中显示“分析中...”）。</summary>
        private string _cmdButtonText;

        /// <summary>【一括执行】按钮的初始文字（执行中显示“执行中...”）。</summary>
        private string _runAllButtonText;

        /// <summary>
        /// 是否正在执行【一括执行】（开始分解 → CMD 分析 → AI 分析 连跑）。
        /// 三个步骤是分开 await 的，步骤之间 <see cref="_isRunning"/> 会短暂变回 false；
        /// 用这个标志让 <see cref="SetRunning"/> 在整批结束前保持按钮置灰，避免中途被再点一次。
        /// </summary>
        private bool _allStepsRunning;

        /// <summary>PDF 预览窗口（非模态，选中分类列表行时复用同一个窗口）。</summary>
        private PdfPreviewForm _previewForm;

        /// <summary>部品基本信息窗口（非模态，选中分类列表行后复用同一个窗口）。</summary>
        private PartBasicInfoForm _partInfoForm;

        /// <summary>部材名置换表维护窗口（非模态，重复点击【部材名置换表】复用同一个窗口）。</summary>
        private MemberNameReplacementForm _memberNameMapForm;

        /// <summary>填充分类表格期间抑制「选中即预览」，避免批量刷新时弹出预览窗口。</summary>
        private bool _suppressAutoPreview;

        /// <summary>上一次自动预览过的 PDF 路径，用于避免同一行被重复预览（例如预览窗口关闭后焦点变化）。</summary>
        private string _lastAutoPreviewKey;

        /// <summary>
        /// 本次「当前行变化」是否由用户按方向键（↑↓ / PgUp / PgDn / Home / End）引起。
        /// <para>
        /// 用来区分「用户真的选了一行」和「DataGridView 自己把第一行设成当前行」：
        /// 切换标签页、表格刚拿到焦点、程序填表之后，表格都会自动把第一行设为当前行，
        /// 那时如果开着「选中即预览」，就会莫名其妙弹出第一份 PDF 的预览窗口。
        /// </para>
        /// </summary>
        private bool _classifyRowChangeByKey;

        /// <summary>
        /// 是否已经把「PDF 预览不可用（pdfium.dll）」的原因写进日志。
        /// 自动预览每换一行都会尝试一次，只在第一次写，避免刷屏。
        /// </summary>
        private bool _pdfiumWarningLogged;

        /// <summary>右键点击日志框时记录的选择起点（右键会把插入点移到点击处并清空选择，菜单弹出时用它恢复选择）。</summary>
        private int _logSelectionStartOnRightClick;

        /// <summary>右键点击日志框时记录的选择长度（0 = 没有选中内容）。</summary>
        private int _logSelectionLengthOnRightClick;

        public Form1()
        {
            InitializeComponent();
            InitializeClassifyGrid();
            _startButtonText = btnStart.Text;
            _aiButtonText = btnAiAnalyze.Text;
            _cmdButtonText = btnCmdAnalyze.Text;
            _runAllButtonText = btnRunAll.Text;

            // 七个子按钮（第 0 步 + 第 1~6 步）的文字统一取自 CmdAnalysisSteps
            // （步骤名的唯一定义处），保证按钮 / 日志叫法一致
            btnCmdStep0.Text = CmdAnalysisSteps.ButtonText(CmdAnalysisStepKind.MaintainTzJson);
            btnCmdStep1.Text = CmdAnalysisSteps.ButtonText(CmdAnalysisStepKind.Assembly);
            btnCmdStep2.Text = CmdAnalysisSteps.ButtonText(CmdAnalysisStepKind.CalloutSplit);
            btnCmdStep3.Text = CmdAnalysisSteps.ButtonText(CmdAnalysisStepKind.YoryoAppend);
            btnCmdStep4.Text = CmdAnalysisSteps.ButtonText(CmdAnalysisStepKind.TzAppend);
            btnCmdStep5.Text = CmdAnalysisSteps.ButtonText(CmdAnalysisStepKind.PartCode);
            btnCmdStep6.Text = CmdAnalysisSteps.ButtonText(CmdAnalysisStepKind.UsageKind);

            // 【分类结果】标签选中时进入「一览全画面」，切回【运行日志】时自动还原
            tabMain.SelectedIndexChanged += TabMain_SelectedIndexChanged;
            tabMain.MouseDown += TabMain_MouseDown;
            tabMain.MouseUp += TabMain_MouseUp;
            pnlContent.SizeChanged += PnlContent_SizeChanged;
        }

        /// <summary>
        /// 窗体首次显示：输出启动信息、内置 PDF 解析引擎（PdfPig）版本，
        /// 以及 PDF 预览用的原生库（pdfium.dll）实际使用的是哪一份。
        /// </summary>
        private void Form1_Shown(object sender, EventArgs e)
        {
            AppendLog("MSIF AI TOOLS 已启动，请选择 PDF 文件夹后开始分解。", Color.FromArgb(120, 200, 255));
            AppendLogRunInfo();
            ShowEngineInfo();
            ShowPdfiumInfo();

            // 标题栏 Debug 默认不勾选：收起 ③ 分步执行，④ 运行日志 撑满。
            // 放在 Shown 里再算一次，用的是 DPI / 字体自动缩放之后的真实尺寸。
            ApplyStepsPanelLayout();
        }

        /// <summary>
        /// 输出「这次运行的是哪个 exe」以及几个控件在运行时的真实坐标 / 自动缩放基准。
        /// <para>
        /// 用于排查「在设计画面挪了控件位置、运行起来却还是原来的样子」：
        /// ① <b>编译时间</b>不是刚刚 → 跑的是旧 exe（编译失败时 VS 会问「是否运行上次成功编译的版本」，
        ///     选「是」就是这个结果；也可能手动启动的是别的目录里的副本）；
        /// ② <b>坐标</b>不是设计器里写的值，且「本次实际」与「设计基准」不同 → 被 DPI / 字体自动缩放
        ///     改了位置（App.config 的 <c>DpiAwareness = PerMonitorV2</c> + <c>AutoScaleMode.Font</c>
        ///     在非 100% 缩放的显示器上就会这样）；
        /// ③ 坐标与设计器完全一致、画面却没变 → 挪的是别的控件，或者设计器的改动没有保存进
        ///     <c>Form1.Designer.cs</c>（该文件被外部改动后 VS 提示重新加载时选「是」也会丢弃未保存的设计改动）。
        /// </para>
        /// </summary>
        private void AppendLogRunInfo()
        {
            try
            {
                string exePath = Application.ExecutablePath;
                string builtAt = File.Exists(exePath)
                    ? File.GetLastWriteTime(exePath).ToString("yyyy-MM-dd HH:mm:ss")
                    : "未知";

                AppendLog(string.Format("程序：{0}（编译 {1}）", exePath, builtAt),
                    Color.FromArgb(180, 180, 180));

                AppendLog(string.Format("窗体：ClientSize={0}×{1}，AutoScaleMode={2}，设计基准={3}，本次实际={4}",
                    ClientSize.Width, ClientSize.Height, AutoScaleMode,
                    DescribeSizeF(AutoScaleDimensions), DescribeSizeF(CurrentAutoScaleDimensions)),
                    Color.FromArgb(180, 180, 180));

                AppendLog(string.Format("布局：grpInput={0}，grpAction={1}，grpSteps={2}，grpLog={3}，btnCmdAnalyze={4}，btnRunAll={5}，btnOpenOutput={6}",
                    DescribeControlBounds(grpInput), DescribeControlBounds(grpAction),
                    DescribeControlBounds(grpSteps), DescribeControlBounds(grpLog),
                    DescribeControlBounds(btnCmdAnalyze), DescribeControlBounds(btnRunAll),
                    DescribeControlBounds(btnOpenOutput)),
                    Color.FromArgb(180, 180, 180));
            }
            catch (Exception)
            {
                // 只为排查布局，取不到就算了，不影响启动
            }
        }

        /// <summary>把控件的位置 / 尺寸格式化成「(X,Y) 宽×高」（排查布局时对照设计器用）。</summary>
        private static string DescribeControlBounds(Control control)
        {
            return control == null
                ? "无"
                : string.Format("({0},{1}) {2}×{3}", control.Left, control.Top, control.Width, control.Height);
        }

        /// <summary>把 SizeF 格式化成「7×17」（AutoScaleDimensions 用）。</summary>
        private static string DescribeSizeF(SizeF size)
        {
            return size.Width.ToString("0.##") + "×" + size.Height.ToString("0.##");
        }

        /// <summary>
        /// 在状态栏与日志中显示内置 PDF 解析引擎（PdfPig）的版本信息。
        /// </summary>
        private void ShowEngineInfo()
        {
            string version = GetEngineVersion();
            lblEngine.Text = "引擎：PdfPig " + version;
            lblEngine.ForeColor = Color.FromArgb(46, 125, 50);
            AppendLog("PDF 解析引擎：PdfPig " + version + "（纯 .NET，无需 Python 运行时）",
                Color.FromArgb(150, 220, 150));
        }

        /// <summary>
        /// 在日志里说明 PDF 预览用的原生库（pdfium.dll）是哪一份。
        /// <para>
        /// 找不到 / 版本不匹配时打印每份候选的失败原因与处理办法：预览不可用，但分解、分类、
        /// AI 分析等其它功能都不受影响（那些只用纯 .NET 的 PdfPig）。
        /// </para>
        /// </summary>
        private void ShowPdfiumInfo()
        {
            string message;

            if (PdfiumRuntime.EnsureAvailable(out message))
            {
                AppendLog("PDF 预览引擎：" + message, Color.FromArgb(150, 220, 150));
                return;
            }

            _pdfiumWarningLogged = true;
            AppendLog("PDF 预览不可用：" + message, Color.FromArgb(255, 200, 120));
        }

        /// <summary>
        /// 读取 PdfPig 程序集的版本号。
        /// </summary>
        private static string GetEngineVersion()
        {
            try
            {
                Version version = typeof(PdfDocument).Assembly.GetName().Version;
                if (version == null)
                {
                    return "未知";
                }

                return version.Major + "." + version.Minor + "." + version.Build;
            }
            catch (Exception)
            {
                return "未知";
            }
        }

        /// <summary>
        /// 浏览选择 PDF 所在文件夹。
        /// </summary>
        private void BtnBrowseFolder_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.Description = "请选择存放 PDF 文件的文件夹";
                fbd.ShowNewFolderButton = false;

                string current = txtFolder.Text.Trim();
                if (current.Length > 0 && Directory.Exists(current))
                {
                    fbd.SelectedPath = current;
                }

                if (fbd.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                txtFolder.Text = fbd.SelectedPath;

                int count = CountPdfFiles(fbd.SelectedPath);
                lblPdfInfo.Text = "PDF：" + count + " 个";
                SetStatus("已选择文件夹（PDF " + count + " 个）", Color.FromArgb(21, 101, 192));
                AppendLog("选择目录：" + fbd.SelectedPath, Color.FromArgb(180, 180, 180));
                AppendLog("目录内 PDF 数量：" + count + " 个", Color.FromArgb(180, 180, 180));
            }
        }

        /// <summary>
        /// 【部材名置换表】：打开维护画面（非模态，重复点击复用同一个窗口）。
        /// 保存后 <see cref="MemberNameReplacementCatalog"/> 立即刷新，
        /// 下一次【CMD 分析】/ 保存就按新规则置换部材名（例：鴨居 → 上枠）。
        /// </summary>
        private void BtnMemberNameMap_Click(object sender, EventArgs e)
        {
            if (_memberNameMapForm == null || _memberNameMapForm.IsDisposed)
            {
                _memberNameMapForm = new MemberNameReplacementForm();
                _memberNameMapForm.Owner = this;
            }

            if (!_memberNameMapForm.Visible)
            {
                _memberNameMapForm.Show();
            }

            if (_memberNameMapForm.WindowState == FormWindowState.Minimized)
            {
                _memberNameMapForm.WindowState = FormWindowState.Normal;
            }

            _memberNameMapForm.Activate();
        }

        /// <summary>
        /// 统计指定目录下（不递归）的 PDF 文件数量。
        /// </summary>
        private static int CountPdfFiles(string folder)
        {
            try
            {
                return PdfSplitService.FindPdfFiles(folder).Length;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// 部品基本信息数据库的路径：<b>&lt;所选 PDF 文件夹&gt;\AiAppData\MSIF_Parts.db</b>
        /// （= 分解输出根目录下的 <c>MSIF_Parts.db</c>，见
        /// <see cref="PartBasicInfoRepository.ResolveDatabasePath(string)"/>）。
        /// <para>
        /// 数据库跟着图纸文件夹走：一个项目一份库，把文件夹拷走就带走了全部部品数据，
        /// 也不会出现「换一个项目 / 换一台机器，看到的还是别的项目的部品」。
        /// 还没分解过（没有输出根目录）时按「已选文件夹\AiAppData」推算；
        /// 连文件夹都没选时退回程序目录下的旧位置（&lt;程序目录&gt;\Data\MSIF_Parts.db）。
        /// </para>
        /// </summary>
        private string ResolvePartDatabasePath()
        {
            string outputDir = _lastOutputDir;

            if (string.IsNullOrWhiteSpace(outputDir))
            {
                string folder = txtFolder.Text.Trim();

                if (folder.Length > 0 && Directory.Exists(folder))
                {
                    outputDir = PdfSplitService.GetOutputRoot(folder);
                }
            }

            return PartBasicInfoRepository.ResolveDatabasePath(outputDir);
        }

        /// <summary>
        /// 更新状态栏文本与颜色（可从任意线程调用）。
        /// </summary>
        private void SetStatus(string text, Color color)
        {
            RunOnUi(delegate
            {
                lblStatus.Text = text;
                lblStatus.ForeColor = color;
            });
        }

        /// <summary>
        /// 把界面更新操作切换到 UI 线程执行（后台线程调用时自动 BeginInvoke）。
        /// </summary>
        private void RunOnUi(Action action)
        {
            if (action == null || IsDisposed || Disposing)
            {
                return;
            }

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(action);
                }
                catch (InvalidOperationException)
                {
                    // 窗体正在关闭，忽略界面更新
                }

                return;
            }

            action();
        }

        /// <summary>
        /// 向运行日志追加一行带时间戳的信息（线程安全）。
        /// 追加时如果用户已经选中了日志文本（准备复制），保持该选择不变，
        /// 避免后台持续输出把选中清掉、导致日志“选不中 / 复制不了”。
        /// </summary>
        private void AppendLog(string message, Color color)
        {
            if (rtbLog == null || rtbLog.IsDisposed)
            {
                return;
            }

            if (rtbLog.InvokeRequired)
            {
                rtbLog.BeginInvoke(new Action<string, Color>(AppendLog), message, color);
                return;
            }

            // 先记住用户当前的选择，追加完再恢复（否则 SelectionStart 移到末尾会清空选择）
            int savedSelectionStart = rtbLog.SelectionStart;
            int savedSelectionLength = rtbLog.SelectionLength;

            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.SelectionLength = 0;
            rtbLog.SelectionColor = color;
            rtbLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
            rtbLog.SelectionColor = rtbLog.ForeColor;

            if (savedSelectionLength > 0)
            {
                // 用户正在选日志：恢复选择，且不强制滚动到底部
                rtbLog.Select(savedSelectionStart, savedSelectionLength);
            }
            else
            {
                rtbLog.ScrollToCaret();
            }
        }

        /// <summary>
        /// 日志框快捷键：Ctrl+C 复制选中内容（没有选中时复制全部日志）、Ctrl+A 全选。
        /// 显式处理可以避免只读 RichTextBox 在不同输入法 / 焦点下的快捷键差异。
        /// </summary>
        private void RtbLog_KeyDown(object sender, KeyEventArgs e)
        {
            if (!e.Control)
            {
                return;
            }

            if (e.KeyCode == Keys.C)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                CopyLogToClipboard(true);
            }
            else if (e.KeyCode == Keys.A)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                rtbLog.SelectAll();
            }
        }

        /// <summary>
        /// 在日志框上按下鼠标时记录右键前的选择区间，供右键菜单恢复选择使用。
        /// </summary>
        private void RtbLog_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
            {
                return;
            }

            _logSelectionStartOnRightClick = rtbLog.SelectionStart;
            _logSelectionLengthOnRightClick = rtbLog.SelectionLength;
        }

        /// <summary>
        /// 日志右键菜单弹出前：按当前是否有选中内容刷新菜单文字与可用状态。
        /// </summary>
        private void CmsLog_Opening(object sender, CancelEventArgs e)
        {
            // 右键点击日志时 RichTextBox 会清空原选择，这里恢复右键前的选择，
            // 否则「复制选中内容」会被误判成「没有选中」而复制整份日志。
            if (rtbLog.SelectionLength == 0 &&
                _logSelectionLengthOnRightClick > 0 &&
                _logSelectionStartOnRightClick + _logSelectionLengthOnRightClick <= rtbLog.TextLength)
            {
                rtbLog.Select(_logSelectionStartOnRightClick, _logSelectionLengthOnRightClick);
            }

            bool hasText = rtbLog.TextLength > 0;
            bool hasSelection = rtbLog.SelectionLength > 0;

            mnuLogCopy.Enabled = hasText;
            mnuLogCopyAll.Enabled = hasText;
            mnuLogSelectAll.Enabled = hasText;
            mnuLogCopy.Text = hasSelection ? "复制选中内容" : "复制全部内容";
        }

        /// <summary>日志右键菜单「复制选中内容」（没有选中时复制全部日志）。</summary>
        private void MnuLogCopy_Click(object sender, EventArgs e)
        {
            CopyLogToClipboard(true);
        }

        /// <summary>日志右键菜单「复制全部日志」。</summary>
        private void MnuLogCopyAll_Click(object sender, EventArgs e)
        {
            CopyLogToClipboard(false);
        }

        /// <summary>日志右键菜单「全选」。</summary>
        private void MnuLogSelectAll_Click(object sender, EventArgs e)
        {
            if (rtbLog.TextLength == 0)
            {
                return;
            }

            rtbLog.Focus();
            rtbLog.SelectAll();
        }

        /// <summary>
        /// 把日志框内容复制到剪贴板；要求只复制选中内容但没有选中时，退回复制全部日志。
        /// </summary>
        /// <param name="selectionOnly">true = 优先复制选中内容；false = 复制全部日志。</param>
        private void CopyLogToClipboard(bool selectionOnly)
        {
            string text = selectionOnly && rtbLog.SelectionLength > 0
                ? rtbLog.SelectedText
                : rtbLog.Text;

            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            try
            {
                Clipboard.SetText(text);
                SetStatus("已复制日志：" + text.Length + " 个字符", Color.FromArgb(46, 125, 50));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "复制到剪贴板失败：\n" + ex.Message, "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// 切换执行期间界面控件的可用状态。
        /// </summary>
        private void SetRunning(bool running)
        {
            _isRunning = running;

            // 一括执行中（三个步骤之间的空档）也保持置灰：running 会在每步结束时短暂变回 false
            bool busy = running || _allStepsRunning;

            btnStart.Enabled = !busy;
            btnAiAnalyze.Enabled = !busy;
            btnCmdAnalyze.Enabled = !busy;
            btnRunAll.Enabled = !busy;
            // 七个子按钮（第 0 步 + 第 1 / 2 / 3 / 4 / 5 / 6 步）执行期间只置灰，文字保持不变
            btnCmdStep0.Enabled = !busy;
            btnCmdStep1.Enabled = !busy;
            btnCmdStep2.Enabled = !busy;
            btnCmdStep3.Enabled = !busy;
            btnCmdStep4.Enabled = !busy;
            btnCmdStep5.Enabled = !busy;
            btnCmdStep6.Enabled = !busy;
            btnAiSettings.Enabled = !busy;
            btnBrowse.Enabled = !busy;
            txtFolder.Enabled = !busy;
            chkCadSplit.Enabled = !busy;
            btnStart.Text = running ? "分解中..." : _startButtonText;
            btnAiAnalyze.Text = running ? "分析中..." : _aiButtonText;
            btnCmdAnalyze.Text = running ? "分析中..." : _cmdButtonText;
            btnRunAll.Text = busy ? "执行中..." : _runAllButtonText;

            if (running)
            {
                progressBar.Style = ProgressBarStyle.Marquee;
                progressBar.MarqueeAnimationSpeed = 30;
            }
            else
            {
                progressBar.MarqueeAnimationSpeed = 0;
                progressBar.Style = ProgressBarStyle.Continuous;
            }
        }

        /// <summary>
        /// 刷新单个文件的处理进度显示（由后台线程通过 RunOnUi 调度到 UI 线程）。
        /// </summary>
        private void UpdateProgress(PdfSplitProgress progress)
        {
            if (progress == null)
            {
                return;
            }

            lblProgress.Text = string.Format("{0} {1}/{2}：{3}",
                progress.Stage, progress.Index, progress.Total, progress.FileName);

            SetStatus(string.Format("正在分解 {0}/{1}（{2}%）",
                progress.Index, progress.Total, progress.Percent), Color.FromArgb(230, 126, 34));
        }

        /// <summary>
        /// 分解进度回调（普通方法，供 PdfSplitService 以委托方式调用）：
        /// 先切换到 UI 线程，再刷新进度显示。
        /// </summary>
        private void OnSplitProgress(PdfSplitProgress progress)
        {
            RunOnUi(delegate
            {
                UpdateProgress(progress);
            });
        }

        /// <summary>
        /// 分解与 AI 分析共用的日志回调（普通方法，供服务层以委托方式调用）。
        /// </summary>
        private void OnServiceLog(LogKind kind, string message)
        {
            AppendLog(message, GetLogColor(kind));
        }

        /// <summary>
        /// 把日志级别映射为控制台配色。
        /// </summary>
        private static Color GetLogColor(LogKind kind)
        {
            switch (kind)
            {
                case LogKind.Success:
                    return Color.FromArgb(150, 220, 150);

                case LogKind.Warning:
                    return Color.FromArgb(255, 200, 120);

                case LogKind.Error:
                    return Color.FromArgb(255, 140, 140);

                default:
                    return Color.FromArgb(212, 212, 212);
            }
        }

        /// <summary>
        /// 第 1 步：调用外部程序 CadPartSplitter（x64）批量分割本文件夹内的 PDF，
        /// 结果写入 &lt;PDF 文件夹&gt;\AiAppData\&lt;図番号&gt;\（Part_NN.png / parts_info.json / parts_text.txt 等），
        /// 与本工具随后生成的 xy.txt / table.txt / plu_*.txt 位于同一子文件夹、互不覆盖。
        ///
        /// 未勾选、找不到程序或分割失败时只记录警告，不阻断第 2 步（本工具自身的分解）。
        /// 注意：本方法在后台线程执行（由 BtnStart_Click 的 Task.Run 调用），
        /// 界面刷新走 AppendLog / SetStatus / RunOnUi，它们内部已处理线程切换。
        /// </summary>
        /// <param name="folder">PDF 所在文件夹。</param>
        private void RunCadPartSplitterStep(string folder)
        {
            AppendLog("--------------------------------------------------", Color.FromArgb(90, 90, 90));

            if (chkCadSplit != null && !chkCadSplit.Checked)
            {
                AppendLog("已按设置跳过 CadPartSplitter 零件分割。", Color.FromArgb(255, 200, 120));
                return;
            }

            AppendLog("第 1 步：调用 CadPartSplitter 做零件分割", Color.FromArgb(120, 200, 255));
            SetStatus("正在执行 CadPartSplitter 零件分割...", Color.FromArgb(230, 126, 34));
            RunOnUi(delegate { lblProgress.Text = "CadPartSplitter 分割中..."; });

            CadPartSplitResult cadResult = CadPartSplitterRunner.RunBatch(folder, AppendCadSplitLog);

            if (!cadResult.ExeFound)
            {
                AppendLog("已跳过 CadPartSplitter 零件分割：" + cadResult.ErrorMessage,
                    Color.FromArgb(255, 200, 120));
                return;
            }

            AppendLog("调用程序：" + cadResult.ExePath, Color.FromArgb(180, 180, 180));
            AppendLog("CadPartSplitter 耗时 " + (cadResult.ElapsedMilliseconds / 1000.0).ToString("0.0") + " 秒",
                Color.FromArgb(180, 180, 180));

            if (cadResult.Success)
            {
                AppendLog(string.IsNullOrEmpty(cadResult.Summary)
                        ? "CadPartSplitter 零件分割完成。"
                        : "CadPartSplitter 零件分割完成：" + cadResult.Summary,
                    Color.FromArgb(150, 220, 150));
                return;
            }

            AppendLog("CadPartSplitter 未全部成功：" + cadResult.ErrorMessage, Color.FromArgb(255, 200, 120));

            if (!string.IsNullOrEmpty(cadResult.OutputTail))
            {
                AppendLog("CadPartSplitter 输出末尾：" + Environment.NewLine + cadResult.OutputTail,
                    Color.FromArgb(255, 200, 120));
            }

            AppendLog("继续执行第 2 步（本工具的 PDF 分解）。", Color.FromArgb(255, 200, 120));
        }

        /// <summary>
        /// CadPartSplitter 的逐行输出回调（来自后台线程），统一加上 [CAD] 前缀以便与本工具日志区分。
        /// </summary>
        private void AppendCadSplitLog(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            AppendLog("[CAD] " + line, Color.FromArgb(170, 190, 210));
        }

        /// <summary>
        /// 第 2 步开始前刷新界面：把进度显示从 CadPartSplitter 阶段切回本工具的分解。
        /// </summary>
        /// <param name="pdfCount">待处理的 PDF 数量。</param>
        private void ReportLocalSplitStart(int pdfCount)
        {
            AppendLog("第 2 步：执行 MSIF AI TOOLS 自身的 PDF 分解（" + pdfCount + " 个 PDF）",
                Color.FromArgb(120, 200, 255));
            SetStatus("正在分解（共 " + pdfCount + " 个 PDF）...", Color.FromArgb(230, 126, 34));
            RunOnUi(delegate { lblProgress.Text = "正在分解，请稍候..."; });
        }

        /// <summary>
        /// 【开始分解】按钮：单独点击时执行一次分解（结束时弹提示）。
        /// </summary>
        private async void BtnStart_Click(object sender, EventArgs e)
        {
            await RunSplitAsync(true);
        }

        /// <summary>
        /// 开始分解的实体：先调用 CadPartSplitter 做零件分割，再调用内置 .NET PDF 引擎（PdfPig）批量处理文件夹内所有 PDF。
        /// <para>
        /// 【一括执行】（<see cref="BtnRunAll_Click"/>）也调用这里，但传
        /// <paramref name="showCompletionDialog"/> = <c>false</c>：中间步骤不弹「完成 / 失败」框（日志里照样有），
        /// 由一括执行最后统一提示，避免连跑三个按钮时要连点三次 OK。
        /// </para>
        /// </summary>
        /// <param name="showCompletionDialog">是否弹出结束 / 失败对话框。</param>
        /// <returns>分解结果可用（含「直接使用现有结果」）返回 <c>true</c>；前置检查失败 / 执行异常返回 <c>false</c>。</returns>
        private async Task<bool> RunSplitAsync(bool showCompletionDialog)
        {
            if (_isRunning)
            {
                return false;
            }

            string folder = txtFolder.Text.Trim();

            if (folder.Length == 0 || !Directory.Exists(folder))
            {
                MessageBox.Show(this, "请确认已选择有效的 PDF 文件夹！", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetStatus("文件夹无效", Color.FromArgb(239, 108, 0));
                return false;
            }

            // 统计文件夹中的 PDF 数量（严格匹配 .pdf 扩展名）
            string[] pdfFiles;

            try
            {
                pdfFiles = PdfSplitService.FindPdfFiles(folder);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "读取文件夹失败：\n" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (pdfFiles.Length == 0)
            {
                MessageBox.Show(this, "该文件夹中没有找到 PDF 文件！", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetStatus("未找到 PDF 文件", Color.FromArgb(239, 108, 0));
                return false;
            }

            lblPdfInfo.Text = "PDF：" + pdfFiles.Length + " 个";

            // 已存在全部分解结果时，先询问是否重新分解（开发调试时避免每次都重新解析 PDF）
            if (PdfSplitService.CountCompletedOutputs(folder, pdfFiles, null) == pdfFiles.Length)
            {
                DialogResult answer = MessageBox.Show(this,
                    "该文件夹中已存在全部分解结果（" + pdfFiles.Length + " 个 PDF 的 xy / table / plu txt 文件齐全）。\n\n" +
                    "是否重新分解？\n\n" +
                    "「是」= 重新分解并覆盖现有结果\n" +
                    "「否」= 直接使用现有结果（可接着点击【AI 分析】/【CMD 分析】）",
                    "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);

                if (answer != DialogResult.Yes)
                {
                    UseExistingSplitResult(folder, pdfFiles);
                    return true;
                }
            }

            // 先复位进度条（连续模式），再切换到运行态（跑马灯）
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Value = 0;
            rtbLog.Clear();

            SetRunning(true);
            AppendLog("==================================================", Color.FromArgb(90, 90, 90));
            AppendLog("PDF 文件夹：" + folder, Color.FromArgb(120, 200, 255));
            AppendLog("待处理 PDF：" + pdfFiles.Length + " 个", Color.FromArgb(120, 200, 255));
            SetStatus("正在分解（共 " + pdfFiles.Length + " 个 PDF）...", Color.FromArgb(230, 126, 34));
            lblProgress.Text = "正在分解，请稍候...";

            PdfSplitResult result = null;
            Exception failure = null;

            try
            {
                PdfSplitService service = new PdfSplitService();

                // 用匿名方法（delegate）代替 Lambda 表达式：Task.Run 需要一个无参委托。
                // 进度与日志回调都改用本窗体的普通方法，便于单步跟踪。
                Func<PdfSplitResult> splitWork = delegate
                {
                    // 第 1 步：先调用 CadPartSplitter 做零件分割（Part_NN.png / parts_info.json 等），
                    // 结果落在同一个 <PDF 文件夹>\AiAppData\<図番号> 下，与本工具的 txt 结果互不覆盖。
                    // 找不到程序或分割失败只记警告，不阻断第 2 步。
                    RunCadPartSplitterStep(folder);

                    // 第 2 步：本工具自身的分解（xy / table / plu 等文本结果）
                    ReportLocalSplitStart(pdfFiles.Length);

                    return service.Split(folder, OnSplitProgress, OnServiceLog);
                };

                result = await Task.Run(splitWork);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            SetRunning(false);

            if (failure != null)
            {
                AppendLog("分解失败：" + failure.Message, Color.FromArgb(255, 140, 140));
                SetStatus("分解异常", Color.FromArgb(198, 40, 40));
                lblProgress.Text = "执行异常";

                if (showCompletionDialog)
                {
                    MessageBox.Show(this, "分解失败：\n" + failure.Message, "异常",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                return false;
            }

            if (result == null)
            {
                SetStatus("分解失败", Color.FromArgb(198, 40, 40));
                lblProgress.Text = "分解失败";
                return false;
            }

            string outputDir = result.OutputDirectory;
            _lastOutputDir = outputDir;

            // 分类结果：分解后由 PdfSplitService 读取 classify\PdfInfoList.txt，
            // 清单里没有的 PDF 记为未知，在【分类结果】页标黄提示（需在上游工具的分类清单里补充）
            _classifyDirectory = result.ClassifyDirectory;
            PopulateClassification(result.Classifications);

            int unknownCount = PdfClassifier.Count(result.Classifications, PdfCategory.Unknown);

            if (unknownCount > 0)
            {
                tabMain.SelectedTab = tabPageClassify;
            }

            AppendLog("--------------------------------------------------", Color.FromArgb(90, 90, 90));

            if (!result.HasErrors)
            {
                AppendLog("全部处理成功，共 " + result.Total + " 个 PDF。", Color.FromArgb(150, 220, 150));
                AppendLog("输出根目录：" + outputDir, Color.FromArgb(180, 180, 180));
                AppendLog("每个 PDF 的子目录：" + outputDir + Path.DirectorySeparatorChar + "<図番号>",
                    Color.FromArgb(180, 180, 180));
                AppendLog("分类清单目录：" + _classifyDirectory, Color.FromArgb(180, 180, 180));
                AppendLog("分类统计：" + PdfClassifier.BuildSummaryText(result.Classifications),
                    unknownCount > 0 ? Color.FromArgb(255, 200, 120) : Color.FromArgb(150, 220, 150));

                SetStatus("分解完成（" + result.Total + " 个 PDF）", Color.FromArgb(46, 125, 50));
                lblProgress.Text = "分解完成";
                progressBar.Style = ProgressBarStyle.Continuous;
                progressBar.Value = progressBar.Maximum;

                if (showCompletionDialog)
                {
                    MessageBox.Show(this,
                        "批量分解成功！\n\n共处理 " + result.Total + " 个 PDF\n\n" +
                        "输出根目录（每个 PDF 一个以図番号命名的子文件夹）：\n" + outputDir + "\n\n" +
                        "各 PDF 子文件夹中的文件：\n" +
                        "xy.txt / xy_imgtext.txt / table.txt /\n" +
                        "plu_text.txt / plu_layout.txt / plu_table.txt / plu_image.txt / plu_all.txt\n\n" +
                        "CadPartSplitter 生成的部品表（CMD 分析用）：\n" +
                        CmdPartAnalyzer.LtStep1FileName + "（アッセンブリ図）/ " +
                        CmdInstructionPartCollector.YoryoListFileName + "（要領図一覧）/ " +
                        CmdInstructionPartCollector.YoryoPartFileName + "（要領図）\n\n" +
                        "分类清单（PdfInfoList.txt）：\n" + _classifyDirectory + "\n\n" +
                        PdfClassifier.BuildSummaryText(result.Classifications) +
                        (unknownCount > 0
                            ? "\n\n有 " + unknownCount + " 个 PDF 在分类清单里没有记录，请在上游工具的分类清单里补充。"
                            : "\n\n分类已全部确定。"),
                        "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            else
            {
                AppendLog("成功 " + result.Succeeded + " 个，失败 " + result.Failed +
                    " 个，详见上方 ERROR 信息。", Color.FromArgb(255, 140, 140));

                if (unknownCount > 0)
                {
                    AppendLog("有 " + unknownCount + " 个 PDF 在分类清单里没有记录，请在上游工具的分类清单里补充。",
                        Color.FromArgb(255, 200, 120));
                }

                SetStatus("分解完成（成功 " + result.Succeeded + "，失败 " + result.Failed + "）",
                    Color.FromArgb(198, 40, 40));
                lblProgress.Text = "分解完成（有失败）";
                progressBar.Style = ProgressBarStyle.Continuous;
                progressBar.Value = 0;

                if (showCompletionDialog)
                {
                    MessageBox.Show(this,
                        "分解完成，但有 " + result.Failed + " 个文件处理失败。\n\n" +
                        "成功 " + result.Succeeded + " / 共 " + result.Total + " 个 PDF\n\n" +
                        "首个错误：\n" + result.Errors[0],
                        "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            return true;
        }


        /// <summary>
        /// 复用已有的分解结果（用户对「是否重新分解」选择「否」时调用）：
        /// 不重新分解，直接还原输出目录与分类列表，使用户可以接着执行【AI 分析】/【CMD 分析】。
        /// </summary>
        /// <param name="folder">PDF 所在文件夹。</param>
        /// <param name="pdfFiles">该文件夹中的 PDF 文件名（含扩展名）。</param>
        private void UseExistingSplitResult(string folder, string[] pdfFiles)
        {
            string outputRoot = PdfSplitService.GetOutputRoot(folder);
            string classifyDir = PdfSplitService.GetClassifyDirectory(outputRoot);

            _lastOutputDir = outputRoot;
            _classifyDirectory = classifyDir;

            lblPdfInfo.Text = "PDF：" + pdfFiles.Length + " 个";

            // 分类读自 <输出根目录>\classify\PdfInfoList.txt（外部生成，本工具只读）：
            // 清单里没有的 PDF 记为未知，在画面上标黄提示
            IList<PdfClassification> items = PdfClassifier.LoadInfoList(folder, pdfFiles, outputRoot);

            PopulateClassification(items);

            int unknownCount = PdfClassifier.Count(items, PdfCategory.Unknown);

            if (unknownCount > 0)
            {
                tabMain.SelectedTab = tabPageClassify;
            }

            AppendLog("--------------------------------------------------", Color.FromArgb(90, 90, 90));
            AppendLog("已跳过重新分解：直接使用现有分解结果（" + pdfFiles.Length + " 个 PDF）",
                Color.FromArgb(150, 220, 150));
            AppendLog("输出根目录：" + outputRoot, Color.FromArgb(180, 180, 180));

            string infoListPath = PdfClassifier.GetInfoListPath(outputRoot);

            AppendLog("分类清单：" + infoListPath + (File.Exists(infoListPath) ? string.Empty : "（不存在）"),
                Color.FromArgb(180, 180, 180));
            AppendLog("分类统计：" + PdfClassifier.BuildSummaryText(items),
                unknownCount > 0 ? Color.FromArgb(255, 200, 120) : Color.FromArgb(150, 220, 150));

            if (unknownCount > 0)
            {
                AppendLog("有 " + unknownCount + " 个 PDF 在分类清单里没有记录，请在上游工具的分类清单里补充。",
                    Color.FromArgb(255, 200, 120));
            }

            AppendLog("现有结果已就绪，可直接点击【AI 分析】或【CMD 分析】。", Color.FromArgb(150, 220, 150));

            SetStatus("已使用现有分解结果（" + pdfFiles.Length + " 个 PDF）", Color.FromArgb(46, 125, 50));
            lblProgress.Text = "使用现有分解结果";
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Value = 0;
        }

        /// <summary>
        /// 打开独立的 AI 接口设置画面（服务端点 / 模型下拉、API Key、超时）。
        /// 保存后写入用户配置文件并立即生效，无需重启程序。
        /// </summary>
        private void BtnAiSettings_Click(object sender, EventArgs e)
        {
            using (AiSettingsForm form = new AiSettingsForm())
            {
                if (form.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }
            }

            AiApiOptions options = AiApiOptions.FromConfiguration();

            AppendLog("AI 设置已保存：" + options.Describe(), Color.FromArgb(180, 220, 255));

            if (!string.IsNullOrEmpty(AiApiOptions.LastSettingsError))
            {
                AppendLog("用户配置文件读取失败：" + AiApiOptions.LastSettingsError,
                    Color.FromArgb(255, 200, 120));
                SetStatus("AI 设置已保存（读取异常）", Color.FromArgb(239, 108, 0));
                return;
            }

            SetStatus("AI 设置已保存", Color.FromArgb(46, 125, 50));
        }

        /// <summary>【AI 分析】按钮：单独点击时执行一次 AI 分析（结束时弹提示）。</summary>
        private async void BtnAiAnalyze_Click(object sender, EventArgs e)
        {
            await RunAiAnalysisAsync(true);
        }

        /// <summary>
        /// 【AI 分析】＝部材名 AI 推荐：遍历【分类结果】中「アッセンブリ図」的 PDF，
        /// 对该図番里「数据来源 = CMD分析・アッセンブリ図」的<b>全部</b>行，
        /// 用该页 <c>parts_info.json</c> 的圈号坐标 + 零件图文字坐标 + 部材マスタ
        /// （<c>部材部品\部材部品.csv</c>）让 AI 判断「每个番号装在哪个部材上」，
        /// 结果写进部品基本信息表的「部材名AI推荐」列（见 <see cref="MemberNameAiAdvisor"/>）。
        /// <para>
        /// 为什么需要 AI：第 1 步把圈号所在零件图的全部 texts 都当成同一个番号的部材名，
        /// 两个番号挨得很近时会写成 <c>鴨居|上レール</c>；而且图上的文字可能是部品名
        /// （<c>部品ｾｯﾄ</c> / <c>取説</c>）、或者根本没写部材名。
        /// </para>
        /// <para>
        /// <b>追加机能</b>：部材名判定之后，再把 PDF 文件夹下文件名第 5 位是 <c>Z</c> 的 Excel
        /// （<c>.xls</c> / <c>.xlsx</c> / <c>.xlsm</c>）交给 AI 判「数量」，
        /// 写进部品基本信息表的「数量」列与 <c>classify\特注品.json</c> 的「員数」
        /// （见 <see cref="SpecialProductQuantityAdvisor"/>；失败不影响部材名结果）。
        /// </para>
        /// <para>
        /// 旧版本这里是「用 AI 从 plu_table.txt 提取整张部品表」，该功能已停用
        /// （<see cref="PartAnalysisService.AnalyzeAndSave"/> 不再被按钮调用）。
        /// </para>
        /// </summary>
        /// <param name="showCompletionDialog">是否弹出结束 / 失败对话框（【一括执行】的中间步骤传 <c>false</c>）。</param>
        /// <returns>AI 分析正常结束返回 <c>true</c>；前置检查失败 / 执行异常返回 <c>false</c>。</returns>
        private async Task<bool> RunAiAnalysisAsync(bool showCompletionDialog)
        {
            if (_isRunning)
            {
                return false;
            }

            if (_classifications == null || _classifications.Count == 0)
            {
                MessageBox.Show(this, "请先点击【开始分解】，并在【分类结果】页确认分类后再执行 AI 分析。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetStatus("尚未分解，无法 AI 分析", Color.FromArgb(239, 108, 0));
                return false;
            }

            string outputDir = _lastOutputDir;

            if (string.IsNullOrEmpty(outputDir) || !Directory.Exists(outputDir))
            {
                MessageBox.Show(this,
                    "找不到分解输出目录，请先执行【开始分解】。\n\n" + (outputDir ?? string.Empty),
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetStatus("找不到分解输出目录", Color.FromArgb(239, 108, 0));
                return false;
            }

            // 后台线程只读快照，避免分析期间界面修改分类造成并发问题
            List<PdfClassification> snapshot = new List<PdfClassification>();

            foreach (PdfClassification item in _classifications)
            {
                if (item != null && item.Category == PdfCategory.Assembly)
                {
                    snapshot.Add(new PdfClassification(item.FilePath, item.Category, item.Evidence,
                        item.IsAutomatic));
                }
            }

            if (snapshot.Count == 0)
            {
                MessageBox.Show(this,
                    "【分类结果】中没有「" + PdfCategoryNames.Assembly + "」的 PDF。\n\n" +
                    "请在【分类结果】页确认分类（分类由上游工具写入 PdfInfoList.txt）后再执行。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetStatus("没有「" + PdfCategoryNames.Assembly + "」，无需分析", Color.FromArgb(239, 108, 0));
                return false;
            }

            // 【开发调试】PartAnalysisService.DebugSingleFileOnly = true 时只分析指定 PDF
            if (PartAnalysisService.IsDebugSingleFileMode)
            {
                List<PdfClassification> debugTargets = PartAnalysisService.ApplyDebugFilter(snapshot);

                if (debugTargets.Count == 0)
                {
                    MessageBox.Show(this,
                        "【调试模式】只分析「" + PartAnalysisService.DebugSingleFileName + "」，" +
                        "但【分类结果】中没有这个文件。\n\n" +
                        "请确认该 PDF 已分类为「" + PdfCategoryNames.Assembly + "」，" +
                        "或把 PartAnalysisService.DebugSingleFileOnly 改为 false。",
                        "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    SetStatus("【调试模式】未找到 " + PartAnalysisService.DebugSingleFileName,
                        Color.FromArgb(239, 108, 0));
                    return false;
                }

                snapshot = debugTargets;
            }

            AiApiOptions options = AiApiOptions.FromConfiguration();

            if (!options.IsConfigured)
            {
                string configError = AiApiOptions.LastConfigurationError;
                string settingsError = AiApiOptions.LastSettingsError;
                string errorNote = string.Empty;

                if (!string.IsNullOrEmpty(configError))
                {
                    errorNote += "\n\nApp.config 读取失败：\n" + configError;
                }

                if (!string.IsNullOrEmpty(settingsError))
                {
                    errorNote += "\n\n用户设置文件读取失败：\n" + settingsError;
                }

                MessageBox.Show(this,
                    "尚未配置 AI 接口密钥。\n\n" +
                    "请点击【AI 设置】按钮填写 ApiKey（保存后立即生效），" +
                    "或在 App.config 的 <appSettings> 中填写 AiApiKey，" +
                    "或设置环境变量 " + AiApiOptions.ApiKeyEnvironmentKey + "。" + errorNote,
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                AppendLog("AI 分析已取消：未配置 AiApiKey（可在【AI 设置】中填写）/ " +
                    AiApiOptions.ApiKeyEnvironmentKey +
                    (string.IsNullOrEmpty(configError) ? string.Empty : "（App.config 读取失败：" + configError + "）") +
                    (string.IsNullOrEmpty(settingsError) ? string.Empty : "（用户设置读取失败：" + settingsError + "）"),
                    Color.FromArgb(255, 200, 120));
                SetStatus("未配置 AI 密钥", Color.FromArgb(239, 108, 0));
                return false;
            }

            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Value = 0;

            SetRunning(true);
            tabMain.SelectedTab = tabPageLog;

            AppendLog("==================================================", Color.FromArgb(90, 90, 90));
            AppendLog(string.Format("AI 分析开始（部材名判定）：{0} 个「{1}」PDF", snapshot.Count,
                PdfCategoryNames.Assembly), Color.FromArgb(120, 200, 255));
            AppendLog("AI 配置：" + options.Describe(), Color.FromArgb(180, 180, 180));
            AppendLog("分解输出目录：" + outputDir, Color.FromArgb(180, 180, 180));
            AppendLog("只处理「数据来源 = " + PartSourceKind.CmdAssembly + "」的行（部材名 为空 / その他 / 含 | 都要判），" +
                "参照「" + MemberPartCatalogLoader.FolderName + "\\" + MemberPartCatalogLoader.FileName +
                "」→ 结果写进「" + PartBasicInfoRepository.MemberNameAiColumnName + "」列",
                Color.FromArgb(180, 180, 180));
            AppendLog("部品基本信息数据库：" + ResolvePartDatabasePath(),
                Color.FromArgb(180, 180, 180));

            if (PartAnalysisService.IsDebugSingleFileMode)
            {
                AppendLog("【调试模式】只分析指定文件：" + PartAnalysisService.DebugSingleFileName,
                    Color.FromArgb(255, 200, 120));
            }

            SetStatus("正在 AI 分析（部材名判定，共 " + snapshot.Count + " 个 PDF）...", Color.FromArgb(230, 126, 34));
            lblProgress.Text = "AI 分析中（部材名判定）...";

            MemberNameAiSummary summary = null;
            Exception failure = null;

            try
            {
                MemberNameAiAdvisor advisor = new MemberNameAiAdvisor(options,
                    ResolvePartDatabasePath());

                // 同上：用匿名方法（delegate）代替 Lambda 表达式，日志回调复用 OnServiceLog
                Func<MemberNameAiSummary> analyzeWork = delegate
                {
                    return advisor.AnalyzeAndSave(snapshot, outputDir, OnServiceLog);
                };

                summary = await Task.Run(analyzeWork);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            // 【AI 分析】追加机能 · 特注品记录的数量设定：
            // 部材名判定之后，再把 PDF 文件夹下文件名第 5 位是 Z 的 Excel（.xls / .xlsx / .xlsm）交给 AI 判数量，
            // 写进部品基本信息表的「数量」列与 classify\特注品.json 的「員数」。
            // 数量判定失败只记日志，不影响上面已经完成的部材名结果（见 SpecialProductQuantityAdvisor）。
            SpecialProductQuantitySummary quantitySummary = null;

            if (failure == null)
            {
                string databasePath = ResolvePartDatabasePath();

                try
                {
                    Func<SpecialProductQuantitySummary> quantityWork = delegate
                    {
                        string root = outputDir.TrimEnd(Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar);

                        return SpecialProductQuantityAdvisor.Run(Path.GetDirectoryName(root), outputDir,
                            databasePath, options, OnServiceLog);
                    };

                    quantitySummary = await Task.Run(quantityWork);
                }
                catch (Exception ex)
                {
                    AppendLog("特注品数量设定失败：" + ex.Message, Color.FromArgb(255, 200, 120));
                }
            }

            SetRunning(false);

            if (failure != null)
            {
                AppendLog("AI 分析失败：" + failure.Message, Color.FromArgb(255, 140, 140));
                SetStatus("AI 分析异常", Color.FromArgb(198, 40, 40));
                lblProgress.Text = "AI 分析异常";

                if (showCompletionDialog)
                {
                    MessageBox.Show(this, "AI 分析失败：\n" + failure.Message, "异常",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                return false;
            }

            if (summary == null)
            {
                SetStatus("AI 分析未返回结果", Color.FromArgb(198, 40, 40));
                lblProgress.Text = "AI 分析未返回结果";
                return false;
            }

            string summaryText = MemberNameAiAdvisor.BuildSummaryText(summary);

            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Value = summary.SavedRowCount > 0 ? progressBar.Maximum : 0;
            lblProgress.Text = "AI 分析完成";

            AppendLog("--------------------------------------------------", Color.FromArgb(90, 90, 90));
            AppendLog(summaryText, summary.SuggestedRowCount > 0
                ? Color.FromArgb(150, 220, 150)
                : Color.FromArgb(255, 200, 120));
            AppendLog("部品基本信息数据库：" + summary.DatabasePath, Color.FromArgb(180, 180, 180));

            if (quantitySummary != null)
            {
                AppendLog(SpecialProductQuantityAdvisor.BuildSummaryText(quantitySummary),
                    quantitySummary.DatabaseUpdatedCount > 0 || quantitySummary.JsonUpdatedCount > 0
                        ? Color.FromArgb(150, 220, 150)
                        : Color.FromArgb(255, 200, 120));
            }

            SetStatus(summaryText, summary.FailedDrawingCount > 0
                ? Color.FromArgb(255, 140, 140)
                : Color.FromArgb(46, 125, 50));

            if (showCompletionDialog)
            {
                MessageBox.Show(this,
                    "AI 分析完成！\n\n" + summaryText + "\n\n" +
                    (quantitySummary == null
                        ? string.Empty
                        : SpecialProductQuantityAdvisor.BuildSummaryText(quantitySummary) + "\n\n") +
                    "部品基本信息数据库：\n" + summary.DatabasePath + "\n\n" +
                    "可在【分类结果】选中 PDF 行后点击【部品信息】，查看「" +
                    PartBasicInfoRepository.MemberNameAiColumnName + "」列。",
                    "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            return true;
        }

        /// <summary>
        /// 【CMD 分析】遍历【分类结果】中「アッセンブリ図」的 PDF，
        /// 用本工具自己的逻辑（<b>不调用 AI</b>）读取 CadPartSplitter 生成的
        /// 01_アッセンブリ図_部品機能情報.csv（&lt;输出根目录&gt;\&lt;図番号&gt;\…，旧名 LT_STEP1.csv），
        /// 以 xy.txt 校验后登录到 SQLite 部品基本信息表（与【AI 分析】的「部材名AI推荐」同一张表）。
        ///
        /// 点一次依次走「第 0 步 + 最前面 1 步 + 第 1~6 步」（名字见 <see cref="CmdAnalysisSteps"/>）：
        /// <b>第 0 步 特注品.json 维护（固定最先执行）</b>（PDF 文件夹下文件名第 5 位是 Z 的 xlsx，
        /// 只处理 特注品.json 里还没有的文件 →
        /// 用 AI 判定是否追加进 classify\特注品.json）→
        /// <b>最前面</b> 特注品 CSV 生成（PDF 文件夹下文件名第 5 位是 Z 的 xlsx →
        /// <c>AiAppData\&lt;文件名&gt;\特注品.csv</c>）→
        /// 第 1 步 装配图部品入库 → 第 2 步 圈号拆分 → 第 3 步 要領図部品追加 → 第 4 步 特注品.json 部品追加 →
        /// 第 5 步 部品コード正規化 → 第 6 步 使い分け自動設定；日志前缀为「【第 N 步 · 名字】」
        /// （最前面那步是「【最前面 · 特注品 CSV 生成】」，不占第 N 步编号）。
        /// 第 2 / 3 步按 PdfInfoList.txt 的「分类依据」与 01_アッセンブリ図_要領図.csv →
        /// 各要領図的 02_要領図_部品機能情報.csv 追加部品；第 4 步按同一份分类依据把 classify\特注品.json
        /// （Excel 部品表）里「品種」命中的明细追加进来；第 5 步按 部品マスタ\Tbl_部品マスタ.csv 的「色」
        /// 把部品コード 里的颜色换成 □、连续符号压成一个 □（撞键的行跳过 = 重複チェック）；
        /// 第 6 步按（正規化後の）部品コード 把 hiroidashiData 里拾い出し表的「使い分け」填到同一部品的多行上，
        /// 全部登录到同一张表。
        /// 登录后可在【分类结果】选中 PDF 行 →【部品信息】查看明细。
        /// 步骤说明见 docs\CMD分析_步骤命名.md。
        /// </summary>
        private async void BtnCmdAnalyze_Click(object sender, EventArgs e)
        {
            await RunCmdAnalysisAsync(CmdAnalysisStepKind.All);
        }

        /// <summary>
        /// 子按钮【第 0 步 · 特注品.json 维护】：只跑第 0 步
        /// （PDF 文件夹下文件名第 5 位是 <c>Z</c> 的 xlsx → 读部品表 → 用 AI 判定哪些条目要追加进
        /// <c>classify\特注品.json</c>）。**不看 checkbox**：点这个按钮就是要跑第 0 步；
        /// 主按钮【CMD 分析】也会固定先跑第 0 步（界面上已经没有 checkbox）。这一步不碰数据库。
        /// </summary>
        private async void BtnCmdStep0_Click(object sender, EventArgs e)
        {
            await RunCmdAnalysisAsync(CmdAnalysisStepKind.MaintainTzJson);
        }

        /// <summary>子按钮【第 1 步 · 装配图部品入库】：只重跑第 1 步（读 01_アッセンブリ図_部品機能情報.csv → 入库）。</summary>
        private async void BtnCmdStep1_Click(object sender, EventArgs e)
        {
            await RunCmdAnalysisAsync(CmdAnalysisStepKind.Assembly);
        }

        /// <summary>子按钮【第 2 步 · 圈号拆分】：只重跑第 2 步（按 parts_info.json 的圈号把部品行按位置拆分）。</summary>
        private async void BtnCmdStep2_Click(object sender, EventArgs e)
        {
            await RunCmdAnalysisAsync(CmdAnalysisStepKind.CalloutSplit);
        }

        /// <summary>子按钮【第 3 步 · 要領図部品追加】：只重跑第 3 步（挂各要領図的 02_要領図_部品機能情報.csv）。</summary>
        private async void BtnCmdStep3_Click(object sender, EventArgs e)
        {
            await RunCmdAnalysisAsync(CmdAnalysisStepKind.YoryoAppend);
        }

        /// <summary>
        /// 子按钮【第 4 步 · 特注品.json 部品追加】：只重跑第 4 步
        /// （classify\特注品.json 里品種命中アッセンブリ図 分类依据的明细挂到该図番上）。
        /// </summary>
        private async void BtnCmdStep4_Click(object sender, EventArgs e)
        {
            await RunCmdAnalysisAsync(CmdAnalysisStepKind.TzAppend);
        }

        /// <summary>
        /// 子按钮【第 5 步 · 部品コード正規化】：只重跑第 5 步
        /// （按 部品マスタ\Tbl_部品マスタ.csv 的「色」把部品コード 换成 □，连续符号压成一个 □，
        /// 撞键的行跳过 = 重複チェック）。
        /// </summary>
        private async void BtnCmdStep5_Click(object sender, EventArgs e)
        {
            await RunCmdAnalysisAsync(CmdAnalysisStepKind.PartCode);
        }

        /// <summary>
        /// 子按钮【第 6 步 · 使い分け自動設定】：只重跑第 6 步
        /// （同一図番内 7 个字段全同的多行 → 按正規化後の部品コード 查 hiroidashiData 的 xlsx → 填「使い分け」）。
        /// **要在第 5 步之后跑**，部品コード 才是 MEA□429 这种和拾い出し表一致的写法。
        /// </summary>
        private async void BtnCmdStep6_Click(object sender, EventArgs e)
        {
            await RunCmdAnalysisAsync(CmdAnalysisStepKind.UsageKind);
        }

        /// <summary>
        /// 【CMD 分析】主按钮（<see cref="CmdAnalysisStepKind.All"/>）与七个子按钮
        /// （第 0 步 + 第 1~6 步）共用的执行流程：
        /// 校验分类 / 输出目录 → 做快照 → 后台执行 <see cref="CmdPartAnalysisService"/> 的分析 → 汇总显示。
        /// 单步执行时只跑该步，日志里仍用「【第 N 步 · 名字】」前缀。
        /// </summary>
        /// <param name="stepKind">要执行的步骤。</param>
        /// <param name="showCompletionDialog">是否弹出结束 / 失败对话框（【一括执行】的中间步骤传 <c>false</c>）。</param>
        /// <returns>步骤正常结束返回 <c>true</c>；前置检查失败 / 执行异常返回 <c>false</c>。</returns>
        private async Task<bool> RunCmdAnalysisAsync(CmdAnalysisStepKind stepKind, bool showCompletionDialog = true)
        {
            if (_isRunning)
            {
                return false;
            }

            if (_classifications == null || _classifications.Count == 0)
            {
                MessageBox.Show(this, "请先点击【开始分解】，并在【分类结果】页确认分类后再执行 CMD 分析。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetStatus("尚未分解，无法 CMD 分析", Color.FromArgb(239, 108, 0));
                return false;
            }

            string outputDir = _lastOutputDir;

            if (string.IsNullOrEmpty(outputDir) || !Directory.Exists(outputDir))
            {
                MessageBox.Show(this,
                    "找不到分解输出目录，请先执行【开始分解】。\n\n" + (outputDir ?? string.Empty),
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetStatus("找不到分解输出目录", Color.FromArgb(239, 108, 0));
                return false;
            }

            // 后台线程只读快照，避免分析期间界面修改分类造成并发问题
            List<PdfClassification> snapshot = new List<PdfClassification>();

            foreach (PdfClassification item in _classifications)
            {
                if (item != null && item.Category == PdfCategory.Assembly)
                {
                    snapshot.Add(new PdfClassification(item.FilePath, item.Category, item.Evidence,
                        item.IsAutomatic));
                }
            }

            // 「最前面 · 特注品 CSV 生成」只遍历 PDF 文件夹下的 xlsx，不依赖アッセンブリ図：
            // 主按钮 / 该步骤在没有装配图时也继续（服务层会打「无需分析」后返回，CSV 照常生成）
            if (snapshot.Count == 0 && stepKind != CmdAnalysisStepKind.All &&
                stepKind != CmdAnalysisStepKind.SpecialProductCsv &&
                stepKind != CmdAnalysisStepKind.MaintainTzJson)
            {
                MessageBox.Show(this,
                    "【分类结果】中没有「" + PdfCategoryNames.Assembly + "」的 PDF。\n\n" +
                    "请在【分类结果】页确认分类（分类由上游工具写入 PdfInfoList.txt）后再执行。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetStatus("没有「" + PdfCategoryNames.Assembly + "」，无需分析", Color.FromArgb(239, 108, 0));
                return false;
            }

            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Value = 0;

            // 第 0 步 特注品.json 维护：主按钮【CMD 分析】固定最先执行
            // （界面上的 checkbox 已禁用，只作说明；服务层对 CmdAnalysisStepKind.All 会无条件先跑这一步，
            //  单步按钮只跑自己那一步，不会连带第 0 步）

            // 主按钮 / 子按钮共用：日志与状态栏都用同一步骤名（名字来自 CmdAnalysisSteps）
            CmdAnalysisStep step = CmdAnalysisSteps.GetStep(stepKind);
            string actionName = step == null ? "CMD 分析" : step.Label;

            SetRunning(true);
            tabMain.SelectedTab = tabPageLog;

            AppendLog("==================================================", Color.FromArgb(90, 90, 90));
            AppendLog(string.Format("{0} 开始：{1} 个「{2}」PDF（不调用 AI：{3} + 要領図の部品情報）",
                actionName, snapshot.Count, PdfCategoryNames.Assembly, CmdPartAnalyzer.LtStep1FileName),
                Color.FromArgb(120, 200, 255));
            AppendLog(CmdAnalysisSteps.DescribeRun(stepKind), Color.FromArgb(120, 200, 255));

            if (stepKind == CmdAnalysisStepKind.All)
            {
                AppendLog(CmdAnalysisSteps.MaintainTzJsonStep.LogHeader +
                    " 固定最先执行：只对 特注品.json 里还没有的文件让 AI 读取并补足",
                    Color.FromArgb(120, 200, 255));
            }
            AppendLog("分解输出目录：" + outputDir, Color.FromArgb(180, 180, 180));
            AppendLog("部品基本信息数据库：" + ResolvePartDatabasePath(),
                Color.FromArgb(180, 180, 180));

            SetStatus("正在 " + actionName + "（共 " + snapshot.Count + " 个 PDF）...", Color.FromArgb(230, 126, 34));
            lblProgress.Text = actionName + " 中...";

            PartAnalysisSummary summary = null;
            Exception failure = null;

            try
            {
                CmdPartAnalysisService service =
                    new CmdPartAnalysisService(ResolvePartDatabasePath());

                // 后台线程执行：用匿名方法（delegate）代替 Lambda 表达式
                Func<PartAnalysisSummary> analyzeWork = delegate
                {
                    return service.AnalyzeAndSave(snapshot, outputDir, OnServiceLog, stepKind);
                };

                summary = await Task.Run(analyzeWork);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            SetRunning(false);

            if (failure != null)
            {
                AppendLog(actionName + " 失败：" + failure.Message, Color.FromArgb(255, 140, 140));
                SetStatus(actionName + " 异常", Color.FromArgb(198, 40, 40));
                lblProgress.Text = actionName + " 异常";

                if (showCompletionDialog)
                {
                    MessageBox.Show(this, actionName + " 失败：\n" + failure.Message, "异常",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                return false;
            }

            if (summary == null)
            {
                SetStatus(actionName + " 未返回结果", Color.FromArgb(198, 40, 40));
                lblProgress.Text = actionName + " 未返回结果";
                return false;
            }

            string summaryText = CmdPartAnalysisService.BuildSummaryText(summary);

            // 第 0 步不写数据库：完成提示换成说明文字，避免看起来像「保存 0 行」的失败
            if (stepKind == CmdAnalysisStepKind.MaintainTzJson)
            {
                summaryText = CmdAnalysisSteps.MaintainTzJsonStep.Label +
                    " 已完成（这一步不写数据库，明细见上方日志）。";
            }

            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Value = summary.SavedRowCount > 0 ? progressBar.Maximum : 0;
            lblProgress.Text = actionName + " 完成";

            AppendLog("--------------------------------------------------", Color.FromArgb(90, 90, 90));
            AppendLog(summaryText, summary.SavedRowCount > 0
                ? Color.FromArgb(150, 220, 150)
                : Color.FromArgb(255, 200, 120));
            AppendLog("部品基本信息数据库：" + summary.DatabasePath, Color.FromArgb(180, 180, 180));

            SetStatus(summaryText, summary.FailedFileCount > 0
                ? Color.FromArgb(255, 140, 140)
                : Color.FromArgb(46, 125, 50));

            if (showCompletionDialog)
            {
                MessageBox.Show(this,
                    actionName + " 完成！\n\n" + summaryText + "\n\n" +
                    "部品基本信息数据库：\n" + summary.DatabasePath + "\n\n" +
                    "可在【分类结果】选中 PDF 行后点击【部品信息】查看登录的部品基本信息。",
                    "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            return true;
        }

        /// <summary>
        /// 【一括执行】：按顺序自动跑【开始分解】→【CMD 分析】→【AI 分析】。
        /// <para>
        /// 三个按钮各自的实体分别复用：<see cref="RunSplitAsync"/> / <see cref="RunCmdAnalysisAsync"/> /
        /// <see cref="RunAiAnalysisAsync"/>，中间步骤传 <c>showCompletionDialog = false</c>
        /// （日志、状态栏、进度条照常刷新，只是不弹「完成」框），最后统一弹一次结果。
        /// </para>
        /// <para>
        /// 任何一步返回 <c>false</c>（前置检查不通过 / 执行异常 / 用户取消）就停下，
        /// 不再跑后面的步骤 —— 后面的步骤都依赖前一步的产物（分解结果 → CMD 入库 → AI 判定）。
        /// 分解时「是否重新分解」的询问框仍然会弹（那是决定，不是提示）。
        /// </para>
        /// </summary>
        private async void BtnRunAll_Click(object sender, EventArgs e)
        {
            if (_isRunning || _allStepsRunning)
            {
                return;
            }

            _allStepsRunning = true;

            try
            {
                await RunAllStepsAsync();
            }
            finally
            {
                _allStepsRunning = false;
                SetRunning(false);
            }
        }

        /// <summary>
        /// 【一括执行】的实体：开始分解 → CMD 分析 → AI 分析 顺序执行，全部结束（或中途失败）后弹一次提示。
        /// </summary>
        private async Task RunAllStepsAsync()
        {
            AppendLog("==================================================", Color.FromArgb(90, 90, 90));
            AppendLog("一括执行开始：【开始分解】→【CMD 分析】→【AI 分析】（依次执行，任一步失败即停止）",
                Color.FromArgb(200, 170, 255));
            SetStatus("一括执行中：开始分解...", Color.FromArgb(230, 126, 34));

            // ① 开始分解（不弹完成框；「是否重新分解」的询问框照常）
            bool splitOk = await RunSplitAsync(false);

            if (!splitOk)
            {
                AppendLog("一括执行已停止：【开始分解】未成功，后续【CMD 分析】/【AI 分析】未执行。",
                    Color.FromArgb(255, 140, 140));
                SetStatus("一括执行已停止（开始分解失败）", Color.FromArgb(198, 40, 40));

                MessageBox.Show(this,
                    "一括执行已停止。\n\n【开始分解】未成功，后续【CMD 分析】/【AI 分析】没有执行。\n\n" +
                    "原因见【运行日志】，处理后可以重新点击【一括执行】，或单独重试某一步。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // ② CMD 分析（第 0 步 + 第 1~6 步，与单个按钮完全相同的流程）
            SetStatus("一括执行中：CMD 分析...", Color.FromArgb(230, 126, 34));

            bool cmdOk = await RunCmdAnalysisAsync(CmdAnalysisStepKind.All, false);

            if (!cmdOk)
            {
                AppendLog("一括执行已停止：【CMD 分析】未成功，后续【AI 分析】未执行。",
                    Color.FromArgb(255, 140, 140));
                SetStatus("一括执行已停止（CMD 分析失败）", Color.FromArgb(198, 40, 40));

                MessageBox.Show(this,
                    "一括执行已停止。\n\n【开始分解】已完成，但【CMD 分析】未成功，" +
                    "【AI 分析】没有执行。\n\n原因见【运行日志】。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // ③ AI 分析（部材名 AI 推荐 + 特注品数量）
            SetStatus("一括执行中：AI 分析...", Color.FromArgb(230, 126, 34));

            bool aiOk = await RunAiAnalysisAsync(false);

            if (!aiOk)
            {
                AppendLog("一括执行结束（AI 分析未完成）：分解与 CMD 分析的结果已保存。",
                    Color.FromArgb(255, 200, 120));
                SetStatus("一括执行结束（AI 分析未完成）", Color.FromArgb(198, 40, 40));

                MessageBox.Show(this,
                    "一括执行结束，但【AI 分析】未完成。\n\n" +
                    "【开始分解】与【CMD 分析】的结果已经保存，原因见【运行日志】。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            AppendLog("--------------------------------------------------", Color.FromArgb(90, 90, 90));
            AppendLog("一括执行完成：开始分解 → CMD 分析 → AI 分析 全部结束。",
                Color.FromArgb(150, 220, 150));
            SetStatus("一括执行完成", Color.FromArgb(46, 125, 50));
            lblProgress.Text = "一括执行完成";

            MessageBox.Show(this,
                "一括执行完成！\n\n" +
                "① 开始分解\n② CMD 分析（第 0 ~ 6 步）\n③ AI 分析（部材名 AI 推荐 + 特注品数量）\n\n" +
                "每一步的结果明细见【运行日志】；部品基本信息可在【分类结果】选中 PDF 行后" +
                "点击【部品信息】查看。",
                "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>
        /// 初始化分类结果表格的列（序号 / 文件名 / 分类 / 判定依据），全部只读显示。
        /// 分类由外部工具生成的 PdfInfoList.txt 决定，本画面只显示、不改判。
        /// </summary>
        private void InitializeClassifyGrid()
        {
            dgvClassify.AutoGenerateColumns = false;
            dgvClassify.Columns.Clear();

            DataGridViewTextBoxColumn colNo = new DataGridViewTextBoxColumn();
            colNo.Name = "colNo";
            colNo.HeaderText = "No";
            colNo.Width = 44;
            colNo.ReadOnly = true;
            colNo.SortMode = DataGridViewColumnSortMode.NotSortable;

            DataGridViewTextBoxColumn colFile = new DataGridViewTextBoxColumn();
            colFile.Name = "colFile";
            colFile.HeaderText = "文件名";
            colFile.Width = 220;
            colFile.ReadOnly = true;
            colFile.SortMode = DataGridViewColumnSortMode.NotSortable;

            DataGridViewTextBoxColumn colCategory = new DataGridViewTextBoxColumn();
            colCategory.Name = "colCategory";
            colCategory.HeaderText = "分类";
            colCategory.Width = 170;
            colCategory.ReadOnly = true;
            colCategory.SortMode = DataGridViewColumnSortMode.NotSortable;

            DataGridViewTextBoxColumn colEvidence = new DataGridViewTextBoxColumn();
            colEvidence.Name = "colEvidence";
            colEvidence.HeaderText = "判定依据";
            colEvidence.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            colEvidence.ReadOnly = true;
            colEvidence.SortMode = DataGridViewColumnSortMode.NotSortable;

            dgvClassify.Columns.AddRange(new DataGridViewColumn[] { colNo, colFile, colCategory, colEvidence });
        }

        /// <summary>
        /// 用分解结果刷新分类表格（必须调用：清空旧行 → 逐行填充 → 刷新统计）。
        /// </summary>
        private void PopulateClassification(IList<PdfClassification> items)
        {
            _classifications = items ?? new List<PdfClassification>();

            _lastAutoPreviewKey = null;
            _suppressAutoPreview = true;

            try
            {
                dgvClassify.Rows.Clear();

                for (int index = 0; index < _classifications.Count; index++)
                {
                    PdfClassification item = _classifications[index];

                    int rowIndex = dgvClassify.Rows.Add(
                        (index + 1).ToString(),
                        item.FileName,
                        item.CategoryName,
                        item.Evidence);

                    dgvClassify.Rows[rowIndex].Tag = item;
                    ApplyRowStyle(rowIndex);
                }
            }
            finally
            {
                _suppressAutoPreview = false;
            }

            // 填完不要留「默认选中第一行」：否则开着「选中即预览」就会直接弹出第一份 PDF
            ClearClassifySelection();

            RefreshClassifySummary();
        }

        /// <summary>
        /// 清空【分类结果】的选中状态（一行都不选中）。
        /// <para>
        /// 切换标签页、程序填表、表格刚拿到焦点时，DataGridView 会自己把<b>第一行</b>设成当前行，
        /// 于是开着「选中即预览」就会直接弹出第一份 PDF 的预览窗口 —— 看起来就像「点一下
        /// 【分类结果】标签，PDF 画面就自己打开了」。所以在这些时机主动把选中状态清掉。
        /// </para>
        /// <para>
        /// 必须把 <see cref="DataGridView.CurrentCell"/> 设成 <c>null</c>：只调 <c>ClearSelection()</c>
        /// 的话 <c>CurrentRow</c> 还停在第一行，【预览 PDF】/【部品信息】仍会作用在那一行上。
        /// </para>
        /// </summary>
        private void ClearClassifySelection()
        {
            if (dgvClassify == null || dgvClassify.IsDisposed || dgvClassify.Rows.Count == 0)
            {
                _lastAutoPreviewKey = null;
                return;
            }

            bool previous = _suppressAutoPreview;
            _suppressAutoPreview = true;
            _classifyRowChangeByKey = false;

            try
            {
                dgvClassify.CurrentCell = null;
                dgvClassify.ClearSelection();
            }
            finally
            {
                _suppressAutoPreview = previous;
            }

            // 清掉「上次预览过的文件」：用户接下来点任意一行（包括第一行）都应该能预览
            _lastAutoPreviewKey = null;
        }

        /// <summary>
        /// 按分类状态给行着色：未知（分类清单里没有该图的记录）为淡黄色，其余为白底。
        /// </summary>
        private void ApplyRowStyle(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= dgvClassify.Rows.Count)
            {
                return;
            }

            DataGridViewRow row = dgvClassify.Rows[rowIndex];
            PdfClassification item = row.Tag as PdfClassification;

            if (item == null)
            {
                return;
            }

            if (item.NeedsUserChoice)
            {
                row.DefaultCellStyle.BackColor = Color.FromArgb(255, 243, 205);
                row.DefaultCellStyle.ForeColor = Color.FromArgb(153, 84, 0);
            }
            else
            {
                row.DefaultCellStyle.BackColor = Color.White;
                row.DefaultCellStyle.ForeColor = Color.FromArgb(33, 37, 41);
            }

            row.Cells["colEvidence"].Value = item.Evidence;
        }

        /// <summary>
        /// 表格取值异常时静默处理，避免弹出系统错误对话框。
        /// </summary>
        private void DgvClassify_DataError(object sender, DataGridViewDataErrorEventArgs e)
        {
            e.ThrowException = false;
        }

        /// <summary>
        /// 分类列表当前行变化：勾选「选中即预览」时自动在预览窗口中显示该 PDF。
        /// 同一行的重复事件会被忽略（避免预览窗口关闭后又被焦点变化重新拉起）。
        /// <para>
        /// 只有「表格可见 + 表格有焦点 + 这次换行是用户操作（鼠标点行 / 方向键）」才预览：
        /// 切换标签页、程序填表、窗口首次布局时 DataGridView 都会自己把 CurrentCell 设成第一行，
        /// 那不是用户选了一行 —— 点【分类结果】标签就弹出第一份 PDF 的预览窗口就是这个原因。
        /// </para>
        /// </summary>
        private void DgvClassify_CurrentCellChanged(object sender, EventArgs e)
        {
            // 键盘标志只对「本次」变化有效：先取走再判断（后面的提前 return 不会把它留到下一次）
            bool changeByKey = _classifyRowChangeByKey;
            _classifyRowChangeByKey = false;

            if (_suppressAutoPreview || !chkAutoPreview.Checked)
            {
                return;
            }

            // 只有用户真的在表格里移动选中行时才预览：
            // 切换标签页、程序刷新表格、窗口首次布局都会把 CurrentCell 设成第一行，
            // 那不算「选了一行」，不应该弹出预览窗口。
            if (!dgvClassify.Visible || !dgvClassify.ContainsFocus)
            {
                return;
            }

            // 鼠标左键还按着 = 用户正在点某一行；按方向键 = 用户在用键盘换行。
            // 两者都不是（表格自己把第一行设成当前行）就什么都不做。
            bool changeByMouse =
                (System.Windows.Forms.Control.MouseButtons & MouseButtons.Left) == MouseButtons.Left;

            if (!changeByMouse && !changeByKey)
            {
                return;
            }

            PdfClassification item = GetSelectedClassification();

            if (item == null || string.IsNullOrEmpty(item.FilePath))
            {
                return;
            }

            if (string.Equals(_lastAutoPreviewKey, item.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ShowPdfPreview(item, false);
        }

        /// <summary>
        /// 双击分类列表中的某一行：显示该 PDF 并前置预览窗口。
        /// </summary>
        private void DgvClassify_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0)
            {
                return;
            }

            ShowPdfPreview(GetSelectedClassification(), true);
        }

        /// <summary>
        /// 点击「部品信息」按钮：在独立窗口中显示当前选中行对应 PDF 的部品基本信息。
        /// </summary>
        private void BtnPartInfo_Click(object sender, EventArgs e)
        {
            ShowPartInfo(GetSelectedClassification(), true);
        }

        /// <summary>
        /// 分类结果表格的快捷键：按 Enter 打开当前行的部品基本信息；
        /// 全画面显示时按 Esc 退回【运行日志】标签并还原布局。
        /// </summary>
        private void DgvClassify_KeyDown(object sender, KeyEventArgs e)
        {
            // 方向键 / PgUp / PgDn / Home / End 会移动当前行：记下「这次换行是用户按出来的」，
            // 供 DgvClassify_CurrentCellChanged 判断要不要弹出预览窗口
            switch (e.KeyCode)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.PageUp:
                case Keys.PageDown:
                case Keys.Home:
                case Keys.End:
                    _classifyRowChangeByKey = true;
                    break;
            }

            if (e.KeyCode == Keys.Escape && _classifyFullScreen)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;

                ExitClassifyFullScreen();
                tabMain.SelectedTab = tabPageLog;
                SetStatus("已退出【分类结果】全画面", Color.FromArgb(108, 117, 125));
                return;
            }

            if (e.KeyCode != Keys.Enter)
            {
                return;
            }

            e.Handled = true;
            e.SuppressKeyPress = true;
            ShowPartInfo(GetSelectedClassification(), true);
        }

        /// <summary>
        /// 右键菜单弹出前，把鼠标所在行设为当前行（否则菜单会作用在上次选中的行上）。
        /// </summary>
        private void CmsClassify_Opening(object sender, CancelEventArgs e)
        {
            Point location = dgvClassify.PointToClient(Cursor.Position);
            DataGridView.HitTestInfo hit = dgvClassify.HitTest(location.X, location.Y);

            if (hit.RowIndex >= 0 && hit.RowIndex < dgvClassify.Rows.Count)
            {
                dgvClassify.CurrentCell = dgvClassify.Rows[hit.RowIndex].Cells[0];
            }

            PdfClassification item = GetSelectedClassification();
            bool hasItem = item != null && !string.IsNullOrEmpty(item.FilePath);

            mnuShowPartInfo.Enabled = hasItem;
            mnuPreviewPdf.Enabled = hasItem;
        }

        /// <summary>右键菜单「查看部品基本信息」。</summary>
        private void MnuShowPartInfo_Click(object sender, EventArgs e)
        {
            ShowPartInfo(GetSelectedClassification(), true);
        }

        /// <summary>右键菜单「预览 PDF」。</summary>
        private void MnuPreviewPdf_Click(object sender, EventArgs e)
        {
            ShowPdfPreview(GetSelectedClassification(), true);
        }

        /// <summary>
        /// 点击「预览 PDF」按钮：显示当前选中行的 PDF。
        /// </summary>
        private void BtnPreviewPdf_Click(object sender, EventArgs e)
        {
            PdfClassification item = GetSelectedClassification();

            if (item == null)
            {
                SetStatus("请先在分类结果列表中选择一行", Color.FromArgb(198, 40, 40));
                return;
            }

            ShowPdfPreview(item, true);
        }

        /// <summary>
        /// 「选中即预览」开关变化时在日志中提示当前状态。
        /// </summary>
        private void ChkAutoPreview_CheckedChanged(object sender, EventArgs e)
        {
            _lastAutoPreviewKey = null;

            AppendLog(chkAutoPreview.Checked
                    ? "已开启自动预览：在【分类结果】中选中一行即显示该 PDF。"
                    : "已关闭自动预览：可双击行或点击「预览 PDF」按钮显示。",
                Color.FromArgb(150, 200, 255));
        }

        /// <summary>
        /// 取分类表格当前选中行对应的分类结果（没有选中时返回 null）。
        /// </summary>
        private PdfClassification GetSelectedClassification()
        {
            if (dgvClassify.CurrentRow == null)
            {
                return null;
            }

            return dgvClassify.CurrentRow.Tag as PdfClassification;
        }

        /// <summary>
        /// 在独立预览窗口（PdfPreviewForm）中显示指定 PDF；窗口已存在时复用并切换文件。
        /// </summary>
        /// <param name="item">要预览的分类结果。</param>
        /// <param name="bringToFront">是否把预览窗口提到最前。</param>
        private void ShowPdfPreview(PdfClassification item, bool bringToFront)
        {
            if (item == null || string.IsNullOrEmpty(item.FilePath))
            {
                return;
            }

            if (!File.Exists(item.FilePath))
            {
                SetStatus("PDF 不存在：" + item.FileName, Color.FromArgb(198, 40, 40));
                return;
            }

            string pdfiumMessage;

            if (!PdfiumRuntime.EnsureAvailable(out pdfiumMessage))
            {
                // 预览引擎不可用时不再打开预览窗口：否则「选中即预览」每换一行都会弹一次
                // 「无法显示该 PDF」，看起来就像点一下列表就报错。
                SetStatus("PDF 预览不可用：" + pdfiumMessage, Color.FromArgb(198, 40, 40));

                if (!_pdfiumWarningLogged)
                {
                    _pdfiumWarningLogged = true;
                    AppendLog("PDF 预览不可用：" + pdfiumMessage, Color.FromArgb(255, 200, 120));
                }

                return;
            }

            _lastAutoPreviewKey = item.FilePath;

            // 部品基本信息画面正在显示同一份 PDF 时，直接复用它的预览窗口（避免打开两个预览窗口）
            if (TryShowPreviewInPartInfoForm(item, bringToFront))
            {
                return;
            }

            if (_previewForm == null || _previewForm.IsDisposed)
            {
                _previewForm = new PdfPreviewForm();
                // 不设 Owner：预览窗口是独立的顶层窗口，不会「始终浮在主窗口之上」，
                // 点一下主窗口即可把它压到后面；关闭主窗口时由 OnFormClosed 一并关闭。
                _previewForm.FormClosed += PreviewForm_FormClosed;

                // 多屏时把预览窗口放到「主画面以外的屏幕」：不要和主画面（本窗口）挤在同一块屏上，
                // 一弹出就把【分类结果】一览挡住；单屏时占左侧一半（和部品基本信息左右并排）。
                WindowLayoutHelper.ArrangePreview(_previewForm, this);

                _previewForm.Show();
            }

            // 「选中即预览」不激活窗口，避免预览画面一直跳到主窗口前面；双击 /【预览 PDF】时才前置
            _previewForm.ShowPdf(item.FilePath, bringToFront);

            if (bringToFront)
            {
                _previewForm.BringToFront();
                _previewForm.Activate();
            }
        }

        /// <summary>
        /// 部品基本信息画面已打开且显示同一份 PDF 时，使用它自己的预览窗口显示 / 前置预览。
        /// （两个画面成对显示：预览在主画面以外的屏幕、部品基本信息在主画面所在的屏幕）
        /// </summary>
        /// <param name="item">要预览的分类结果。</param>
        /// <param name="bringToFront">是否把预览窗口提到最前。</param>
        /// <returns>已由部品基本信息画面的预览窗口处理时返回 true。</returns>
        private bool TryShowPreviewInPartInfoForm(PdfClassification item, bool bringToFront)
        {
            if (item == null || _partInfoForm == null || _partInfoForm.IsDisposed || !_partInfoForm.Visible)
            {
                return false;
            }

            if (!string.Equals(_partInfoForm.PdfPath, item.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _partInfoForm.ShowPreview(bringToFront);

            return true;
        }

        /// <summary>
        /// 把主画面已打开的 PDF 预览窗口移交给部品基本信息画面，
        /// 避免同时存在两个预览窗口（部品基本信息关闭时由它一并关闭预览）。
        /// </summary>
        private void TransferPreviewFormToPartInfo()
        {
            if (_partInfoForm == null || _partInfoForm.IsDisposed)
            {
                return;
            }

            PdfPreviewForm preview = _previewForm;
            _previewForm = null;

            if (preview == null || preview.IsDisposed)
            {
                return;
            }

            preview.FormClosed -= PreviewForm_FormClosed;
            _partInfoForm.AttachPreviewForm(preview);
        }

        /// <summary>
        /// 在独立窗口（PartBasicInfoForm）中显示指定 PDF（図番）已登录到 SQLite 的部品基本信息；
        /// 窗口已存在时复用并切换到该 PDF。PDF 预览画面与本画面同时打开：
        /// 预览窗口在主画面以外的屏幕（不和主画面挤在一块屏上）、部品基本信息在主画面所在的屏幕
        /// （只有 1 块屏幕时左右并排）。
        /// </summary>
        /// <param name="item">要查看的分类结果。</param>
        /// <param name="bringToFront">是否把窗口提到最前。</param>
        private void ShowPartInfo(PdfClassification item, bool bringToFront)
        {
            if (item == null || string.IsNullOrEmpty(item.FilePath))
            {
                SetStatus("请先在分类结果列表中选择一行", Color.FromArgb(198, 40, 40));
                return;
            }

            string databasePath = ResolvePartDatabasePath();

            // 换了图纸文件夹（= 换了一份库）：先把旧窗口关掉重建，免得继续读写上一个项目的库。
            // 旧窗口有未保存的修改时会先询问，用户选「取消」就继续用旧窗口（他正在编辑旧库的数据）
            if (_partInfoForm != null && !_partInfoForm.IsDisposed &&
                !string.Equals(_partInfoForm.DatabasePath, databasePath, StringComparison.OrdinalIgnoreCase))
            {
                _partInfoForm.Close();
            }

            bool created = _partInfoForm == null || _partInfoForm.IsDisposed;

            if (created)
            {
                _partInfoForm = new PartBasicInfoForm(databasePath);
                _partInfoForm.Owner = this;
                _partInfoForm.FormClosed += PartInfoForm_FormClosed;

                // 主画面已打开的预览窗口移交给部品基本信息画面（两个画面成对显示）
                TransferPreviewFormToPartInfo();
            }

            _partInfoForm.ShowDrawing(item.FilePath);

            if (created || !_partInfoForm.Visible)
            {
                _partInfoForm.Show();
            }

            if (bringToFront)
            {
                _partInfoForm.BringToFront();
                _partInfoForm.Activate();
            }

            SetStatus("部品基本信息：" + PartBasicInfoRepository.ResolveDrawingNo(item.FilePath),
                Color.FromArgb(46, 125, 50));
        }

        /// <summary>部品基本信息窗口关闭后清空引用，便于下次重新创建。</summary>
        private void PartInfoForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            PartBasicInfoForm form = sender as PartBasicInfoForm;

            if (form != null)
            {
                form.FormClosed -= PartInfoForm_FormClosed;
            }

            _partInfoForm = null;
        }

        /// <summary>预览窗口关闭后清空引用，便于下次重新创建。</summary>
        private void PreviewForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            PdfPreviewForm form = sender as PdfPreviewForm;

            if (form != null)
            {
                form.FormClosed -= PreviewForm_FormClosed;
            }

            _previewForm = null;
        }

        /// <summary>主窗口关闭时一并关闭预览窗口，释放 PDF 文件句柄。</summary>
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // Close() 会触发 FormClosed（事件里会清空字段），所以先取到局部变量
            PdfPreviewForm preview = _previewForm;
            PartBasicInfoForm partInfo = _partInfoForm;

            _previewForm = null;
            _partInfoForm = null;

            if (preview != null && !preview.IsDisposed)
            {
                preview.Close();
                preview.Dispose();
            }

            if (partInfo != null && !partInfo.IsDisposed)
            {
                partInfo.Close();
                partInfo.Dispose();
            }

            base.OnFormClosed(e);
        }

        /// <summary>是否正处于「【分类结果】一览占满整个窗口」的状态。</summary>
        private bool _classifyFullScreen;

        /// <summary>
        /// 本次标签切换是否由用户「点了标签头」引起。
        /// <para>
        /// 程序自己也会切换标签（分解结束、<see cref="PdfCategory"/> 里有未知文件时自动切到【分类结果】，
        /// 运行 AI 分析 / CMD 分析时切到【运行日志】），这些不应该把界面切进 / 切出全画面，
        /// 否则分解一结束 ② 执行区（含【AI 分析】按钮）就被收起来了。
        /// </para>
        /// </summary>
        private bool _tabSwitchByUser;

        /// <summary>进入全画面之前 grpLog 的位置 / 大小（退出时原样还原）。</summary>
        private Rectangle _grpLogBoundsBeforeFullScreen;

        /// <summary>进入全画面之前 ① 输入设置 是否可见（退出时还原）。</summary>
        private bool _grpInputVisibleBeforeFullScreen;

        /// <summary>进入全画面之前 ② 执行 是否可见（退出时还原）。</summary>
        private bool _grpActionVisibleBeforeFullScreen;

        /// <summary>进入全画面之前日志组的标题（退出时还原）。</summary>
        private string _grpLogTextBeforeFullScreen;

        /// <summary>进入全画面之前的窗体状态（只在本次确实最大化过时才还原窗口大小）。</summary>
        private FormWindowState _windowStateBeforeFullScreen;

        /// <summary>进入全画面之前的窗体位置 / 大小（退出时还原）。</summary>
        private Rectangle _windowBoundsBeforeFullScreen;

        /// <summary>全画面时日志组与内容区四周保留的边距（像素）。</summary>
        private const int ClassifyFullScreenMargin = 6;

        /// <summary>
        /// 主标签页切换：用户点【分类结果】→ 一览铺满整个窗口；切回【运行日志】→ 还原原来的四段布局。
        /// <para>
        /// 只有用户点击标签头引起的切换才进入全画面（见 <see cref="_tabSwitchByUser"/>）；
        /// 程序自动切换（分解结束后切到【分类结果】看未知文件、运行时切到【运行日志】）保持原布局。
        /// </para>
        /// <para>
        /// 进入全画面会改动窗口状态（最大化），所以放到消息处理完之后再做：
        /// 在 <c>MouseDown</c> 的调用栈里直接最大化窗口会打断 TabControl 自己的选中处理。
        /// </para>
        /// </summary>
        private void TabMain_SelectedIndexChanged(object sender, EventArgs e)
        {
            bool byUser = _tabSwitchByUser;
            _tabSwitchByUser = false;

            // 切换标签不是「在列表里选了一行」：切换过程中 DataGridView 会把 CurrentCell
            // 设成第一行（可能发生在新标签页拿到焦点之后），这里先压住一次「选中即预览」，
            // 等这条消息处理完再放开 —— 用户随后真的点行 / 按方向键时才会弹预览窗口。
            _suppressAutoPreview = true;

            if (byUser && tabMain.SelectedTab == tabPageClassify)
            {
                RunAfterCurrentMessage(EnterClassifyFullScreen);
            }
            else
            {
                RunAfterCurrentMessage(ExitClassifyFullScreen);
            }

            // 切到【分类结果】时不要默认选中第一行：表格拿到焦点 / 变成可见时，DataGridView
            // 会自己把第一行设成当前行，开着「选中即预览」就直接弹出第一份 PDF 的预览窗口。
            // 排在「进入全画面」之后注册：那一步会把焦点交给表格，表格因此才会选中第一行。
            if (tabMain.SelectedTab == tabPageClassify)
            {
                RunAfterCurrentMessage(ClearClassifySelection);
            }

            // 恢复放在全画面切换之后注册：先做完「最大化 + 重新布局」，再放开「选中即预览」
            RunAfterCurrentMessage(delegate { _suppressAutoPreview = false; });
        }

        /// <summary>
        /// 只有点在「标签头」上才算用户主动切换标签；点在页面内容（表格 / 日志）上不算。
        /// </summary>
        private void TabMain_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            for (int index = 0; index < tabMain.TabCount; index++)
            {
                if (tabMain.GetTabRect(index).Contains(e.Location))
                {
                    _tabSwitchByUser = true;
                    return;
                }
            }
        }

        /// <summary>
        /// 点击结束（含点到已经选中的标签头、没有发生切换的情况）后清掉「用户切换」标记。
        /// <para>
        /// 这里同样延后一条消息再清：不同 Windows 主题 / TabControl 实现里，选中变化通知
        /// （TCN_SELCHANGE → <c>SelectedIndexChanged</c>）有可能在 <c>MouseUp</c> 之后才到达，
        /// 立刻清标记会把同一次点击的切换漏掉（点了标签却不进全画面）。
        /// </para>
        /// </summary>
        private void TabMain_MouseUp(object sender, MouseEventArgs e)
        {
            RunAfterCurrentMessage(delegate { _tabSwitchByUser = false; });
        }

        /// <summary>
        /// 把动作排到「当前消息处理完之后」执行。
        /// <para>
        /// 与 <see cref="RunOnUi"/> 不同：<c>RunOnUi</c> 在 UI 线程上是<b>立即</b>执行的，
        /// 而这里即使已经在 UI 线程也要 <c>BeginInvoke</c> 排到下一次消息循环 ——
        /// 切换标签的 <c>SelectedIndexChanged</c> 是在 TabControl 的鼠标消息里触发的，
        /// 在那里面直接最大化 / 还原窗口会打断 TabControl 自己的选中与绘制。
        /// </para>
        /// </summary>
        private void RunAfterCurrentMessage(Action action)
        {
            if (action == null || IsDisposed || Disposing)
            {
                return;
            }

            try
            {
                if (IsHandleCreated)
                {
                    BeginInvoke(action);
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                // 窗口正在销毁：忽略
                return;
            }

            // 句柄还没创建（极端情况）：直接执行，至少不丢状态
            action();
        }

        /// <summary>
        /// 内容区尺寸变化（窗口最大化 / 还原 / 拖动边框）时，让下半部分继续贴合：
        /// 全画面 → 一览铺满；正常布局 → ④ 运行日志 按 Debug 状态重排、始终撑到内容区底部。
        /// </summary>
        private void PnlContent_SizeChanged(object sender, EventArgs e)
        {
            if (_classifyFullScreen)
            {
                ApplyClassifyFullScreenLayout();
                return;
            }

            ApplyStepsPanelLayout();
        }

        /// <summary>
        /// 进入【分类结果】全画面：收起 ① 输入设置、② 执行 与 ③ 分步执行，把整个内容区让给一览表格，
        /// 并把窗口最大化；切回其它标签时由 <see cref="ExitClassifyFullScreen"/> 全部还原。
        /// </summary>
        private void EnterClassifyFullScreen()
        {
            if (_classifyFullScreen)
            {
                ApplyClassifyFullScreenLayout();
                return;
            }

            _classifyFullScreen = true;

            // 先记下原始布局，退出时逐项还原（不写死设计器里的具体数值）
            _grpLogBoundsBeforeFullScreen = grpLog.Bounds;
            _grpLogTextBeforeFullScreen = grpLog.Text;
            _grpInputVisibleBeforeFullScreen = grpInput.Visible;
            _grpActionVisibleBeforeFullScreen = grpAction.Visible;
            _windowStateBeforeFullScreen = WindowState;
            _windowBoundsBeforeFullScreen = Bounds;

            // 看一览时用不到这三块，收起来才能把高度全部让给表格
            grpInput.Visible = false;
            grpAction.Visible = false;
            grpSteps.Visible = false;

            grpLog.Text = "④  分类结果一览（全画面）— 按 Esc 或切回【运行日志】标签即还原布局";

            // 「全画面」= 窗口先最大化；本来就已经最大化 / 最小化时不动窗口
            if (_windowStateBeforeFullScreen == FormWindowState.Normal)
            {
                WindowState = FormWindowState.Maximized;
            }

            ApplyClassifyFullScreenLayout();

            SetStatus("【分类结果】全画面显示：切回【运行日志】标签还原布局",
                Color.FromArgb(46, 125, 50));

            // 焦点交给表格：上下键即可逐行浏览、回车查看部品信息
            if (dgvClassify != null && dgvClassify.Visible)
            {
                dgvClassify.Focus();

                // 表格拿到焦点时 DataGridView 会自动把第一行设成当前行 → 立刻清掉：
                // 不要「点一下【分类结果】标签就默认选中第一行、还弹出第一份 PDF 的预览窗口」。
                ClearClassifySelection();
            }
        }

        /// <summary>
        /// 退出【分类结果】全画面：还原 ① / ② 的显示状态、日志组标题与位置、窗口大小；
        /// ③ 分步执行 的显示与否由标题栏 Debug 决定（见 <see cref="ApplyStepsPanelLayout"/>）。
        /// </summary>
        private void ExitClassifyFullScreen()
        {
            if (!_classifyFullScreen)
            {
                return;
            }

            _classifyFullScreen = false;

            grpInput.Visible = _grpInputVisibleBeforeFullScreen;
            grpAction.Visible = _grpActionVisibleBeforeFullScreen;
            grpLog.Text = _grpLogTextBeforeFullScreen;

            // 只有本次确实把窗口最大化过，才需要还原窗口大小（用户本来最大化就保持最大化）
            if (_windowStateBeforeFullScreen == FormWindowState.Normal)
            {
                if (WindowState == FormWindowState.Maximized)
                {
                    WindowState = FormWindowState.Normal;
                }

                Bounds = _windowBoundsBeforeFullScreen;
            }

            grpLog.Bounds = _grpLogBoundsBeforeFullScreen;

            // ③ 分步执行 显示与否由标题栏 Debug 决定；并按内容区实际高度重排 ④（不留空白）
            ApplyStepsPanelLayout();
        }

        /// <summary>
        /// 全画面状态下把 grpLog（里面是 tabMain → 【分类结果】表格）铺满内容区，
        /// 四周留 <see cref="ClassifyFullScreenMargin"/> 边距，与正常布局的视觉留白一致。
        /// </summary>
        private void ApplyClassifyFullScreenLayout()
        {
            if (!_classifyFullScreen || pnlContent == null || grpLog == null)
            {
                return;
            }

            int width = pnlContent.ClientSize.Width - ClassifyFullScreenMargin * 2;
            int height = pnlContent.ClientSize.Height - ClassifyFullScreenMargin * 2;

            if (width <= 0 || height <= 0)
            {
                return;
            }

            grpLog.SetBounds(ClassifyFullScreenMargin, ClassifyFullScreenMargin, width, height);
        }

        /// <summary>
        /// 按标题栏「Debug」勾选状态摆放内容区下半部分：
        /// <list type="bullet">
        /// <item>勾选 → 显示 ③ 分步执行（CMD 分析第 0 ~ 6 步），④ 运行日志 排到它下方；</item>
        /// <item>不勾选（默认）→ 收起 ③，④ 运行日志 直接顶到 ② 执行 下面，并一直撑到内容区底部（不留空白）。</item>
        /// </list>
        /// <para>
        /// 位置不用写死的常量：② 执行 的底边、③ 的高度都取控件的实际值，
        /// 这样 DPI / 字体自动缩放改了尺寸后依然贴合。<see cref="_classifyFullScreen"/> 期间不动，
        /// 那张画面由 <see cref="ApplyClassifyFullScreenLayout"/> 负责（退出全画面时会再调一次本方法）。
        /// </para>
        /// </summary>
        private void ApplyStepsPanelLayout()
        {
            if (pnlContent == null || grpAction == null || grpSteps == null || grpLog == null)
            {
                return;
            }

            if (_classifyFullScreen)
            {
                // 看一览时 ③ 一直收起，避免盖在表格上
                grpSteps.Visible = false;
                return;
            }

            bool showSteps = chkDebug != null && chkDebug.Checked;

            grpSteps.Visible = showSteps;

            // 左 / 右 / 底留白直接取 ② 执行 的设计左边距（20px），块间距取 ③ 与 ② 之间的设计间距（6px）：
            // 都是控件运行时的真实值，DPI / 字体自动缩放把尺寸放大后依然保持均匀留白。
            int margin = grpAction.Left;
            int gap = Math.Max(0, grpSteps.Top - grpAction.Bottom);
            int width = pnlContent.ClientSize.Width - margin * 2;
            int top = grpAction.Bottom + gap;
            int bottom = pnlContent.ClientSize.Height - margin;

            if (showSteps)
            {
                // 有 ③ 时 ④ 排在它下面（grpSteps.Bottom 随 DPI 缩放，别写成常量）
                top = grpSteps.Bottom + gap;
            }

            int height = bottom - top;

            if (width <= 0 || height <= 0)
            {
                return;
            }

            grpLog.SetBounds(margin, top, width, height);
        }

        /// <summary>
        /// 标题栏「Debug」开关：勾选后显示 ③ 分步执行 面板（第 0 ~ 6 步可单独执行），
        /// 取消勾选则收起该面板，并让 ④ 运行日志 撑满剩余高度（不留空白）。
        /// </summary>
        private void ChkDebug_CheckedChanged(object sender, EventArgs e)
        {
            ApplyStepsPanelLayout();

            bool debugOn = chkDebug.Checked;

            AppendLog(
                debugOn
                    ? "Debug 已开启：显示【分步执行】面板（CMD 分析 第 0 ~ 6 步可单独执行）。"
                    : "Debug 已关闭：隐藏【分步执行】面板，④ 运行日志 已撑满内容区。",
                Color.FromArgb(150, 200, 255));

            SetStatus(debugOn ? "已显示【分步执行】面板" : "已隐藏【分步执行】面板",
                Color.FromArgb(108, 117, 125));
        }

        /// <summary>
        /// 刷新分类统计标签（显示各类数量与未知数量）。
        /// </summary>
        private void RefreshClassifySummary()
        {
            if (_classifications == null || _classifications.Count == 0)
            {
                lblClassifySummary.Text = "尚未执行分类";
                lblClassifySummary.ForeColor = Color.FromArgb(52, 73, 94);
                return;
            }

            int unknown = PdfClassifier.Count(_classifications, PdfCategory.Unknown);

            lblClassifySummary.Text = PdfClassifier.BuildSummaryText(_classifications) + "\n" +
                (unknown > 0 ? "未知 " + unknown + " 个：分类清单（PdfInfoList.txt）里没有记录" : "全部已确定");

            lblClassifySummary.ForeColor = unknown > 0
                ? Color.FromArgb(198, 40, 40)
                : Color.FromArgb(46, 125, 50);
        }

        /// <summary>
        /// 打开分类清单目录（&lt;PDF 文件夹&gt;\AiAppData\classify），
        /// 便于查看 / 由外部工具维护其中的 PdfInfoList.txt。
        /// </summary>
        private void BtnOpenClassifyDir_Click(object sender, EventArgs e)
        {
            string target = _classifyDirectory;

            if (string.IsNullOrEmpty(target) || !Directory.Exists(target))
            {
                string folder = txtFolder.Text.Trim();

                if (folder.Length > 0 && Directory.Exists(folder))
                {
                    target = PdfSplitService.GetClassifyDirectory(PdfSplitService.GetOutputRoot(folder));
                }
            }

            if (string.IsNullOrEmpty(target) || !Directory.Exists(target))
            {
                MessageBox.Show(this, "分类目录还不存在，请先执行 PDF 分解。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                Process.Start("explorer.exe", "\"" + target + "\"");
                AppendLog("打开目录：" + target, Color.FromArgb(180, 180, 180));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开目录失败：\n" + ex.Message, "异常",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnOpenOutput_Click(object sender, EventArgs e)
        {
            string target = null;

            if (!string.IsNullOrEmpty(_lastOutputDir) && Directory.Exists(_lastOutputDir))
            {
                target = _lastOutputDir;
            }
            else
            {
                string folder = txtFolder.Text.Trim();
                if (folder.Length > 0 && Directory.Exists(folder))
                {
                    // 优先打开分解输出的根目录（<PDF 文件夹>\AiAppData）
                    string outputRoot = PdfSplitService.GetOutputRoot(folder);
                    target = Directory.Exists(outputRoot) ? outputRoot : folder;
                }
            }

            if (target == null)
            {
                MessageBox.Show(this, "暂无可打开的输出目录，请先选择 PDF 文件夹并执行分解。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                Process.Start("explorer.exe", "\"" + target + "\"");
                AppendLog("打开目录：" + target, Color.FromArgb(180, 180, 180));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开目录失败：\n" + ex.Message, "异常",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
