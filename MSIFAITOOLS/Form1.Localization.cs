using System;
using System.Drawing;
using System.Windows.Forms;
using MSIFAITOOLS.Services;

namespace MSIFAITOOLS
{
    /// <summary>
    /// 主画面的中日双语（标题栏右上角的「中文」/「日本語」按钮）。
    /// <para>
    /// 中文原文照旧写在 Form1.cs / Form1.Designer.cs 里（翻译键），
    /// 日语在 <c>Services\UiText.Japanese.cs</c> 的对照表里维护；
    /// 这里只负责「按当前语言把画面上的文字重新刷一遍」，
    /// 以及把语言选择记到 <c>%APPDATA%\MSIFAITOOLS\ui-language.json</c>。
    /// </para>
    /// <para>
    /// 运行日志 / 状态栏 / 弹窗的中文在产生处不用改：
    /// <see cref="AppendLog"/> / <see cref="SetStatus"/> / <see cref="ShowMessage(string,string)"/>
    /// 内部统一走 <see cref="UiText.Translate"/>，对照表里有条目就输出日语。
    /// </para>
    /// </summary>
    public partial class Form1
    {
        /// <summary>状态栏「PDF：N 个」用的最近一次 PDF 数量（切换语言后按它重画）。</summary>
        private int _lastPdfCount;

        /// <summary>状态栏「引擎：PdfPig x.y.z」用的最近一次版本号。</summary>
        private string _lastEngineVersion;

        /// <summary>状态栏最近一次的中文文字（切换语言后按它重画）。</summary>
        private string _lastStatusText;

        /// <summary>正在按语言重刷画面文字（防止重入）。</summary>
        private bool _applyingLanguage;

        /// <summary>
        /// 窗体构造时调用一次：读取上次保存的语言并刷新全部文字。
        /// 必须在 <see cref="InitializeClassifyGrid"/> 之后（列头要按语言重设）。
        /// </summary>
        private void InitializeLanguage()
        {
            UiText.LoadSaved();
            UiText.LanguageChanged += UiText_LanguageChanged;
            ApplyLanguage();
        }

        /// <summary>语言切换事件：重刷画面文字，并在日志 / 状态栏留一行记录。</summary>
        private void UiText_LanguageChanged(object sender, EventArgs e)
        {
            ApplyLanguage();

            AppendLog(
                UiText.P("界面语言已切换为：中文。", "表示言語を日本語に切り替えました。"),
                Color.FromArgb(120, 200, 255));

            SetStatus(UiText.P("界面语言：中文", "表示言語：日本語"), Color.FromArgb(21, 101, 192));
        }

        /// <summary>标题栏「中文」按钮：切换为中文。</summary>
        private void BtnLanguageZh_Click(object sender, EventArgs e)
        {
            UiText.SetLanguage(UiLanguage.Chinese);
            ApplyLanguage();
        }

        /// <summary>标题栏「日本語」按钮：切换为日语。</summary>
        private void BtnLanguageJa_Click(object sender, EventArgs e)
        {
            UiText.SetLanguage(UiLanguage.Japanese);
            ApplyLanguage();
        }

        /// <summary>
        /// 按当前语言重刷画面上的全部文字（控件文字 / 标签页 / 表头 / 右键菜单 / 状态栏）。
        /// 中文模式下 <see cref="UiText.T"/> 原样返回中文，所以两种语言只用这一套赋值。
        /// </summary>
        private void ApplyLanguage()
        {
            if (_applyingLanguage)
            {
                return;
            }

            _applyingLanguage = true;

            try
            {
                // ── 标题栏 ──
                Text = UiText.T("MSIF AI TOOLS  -  PDF 分解工具（坐标 + 文本 + 分类）");
                lblAppSub.Text = UiText.T("PDF 分解工具 · 坐标 / 文本 / plu（pdfplumber 风格）");
                chkDebug.Text = UiText.T("Debug（显示分步执行面板）");
                btnAiSettings.Text = UiText.T("AI 設定");
                lblDbHint.Text = UiText.T("SQL Server 接続：");
                btnDbJp1.Text = UiText.T("接続① genpin");
                btnDbJp2.Text = UiText.T("接続② MSIF");

                tipHeader.SetToolTip(btnDbJp1,
                    UiText.T("维护 ① genpin 库的连接（部材名置换表 T_M_MergeName / CMD 分析 T_M_PartsMaster）"));
                tipHeader.SetToolTip(btnDbJp2,
                    UiText.T("维护 ② MSIF 库的连接（部材 mapping DL_BuhinBuzaiMapping）"));
                tipHeader.SetToolTip(btnLangZh,
                    UiText.T("界面语言：中文 / 日本語（切换后立即生效，并记住选择）"));
                tipHeader.SetToolTip(btnLangJa, tipHeader.GetToolTip(btnLangZh));

                // ── ① 输入设置 ──
                grpInput.Text = UiText.T("①  输入设置");
                lblFolder.Text = UiText.T("PDF 文件夹：");
                btnBrowse.Text = UiText.T("浏览...");
                chkCadSplit.Text = UiText.T("开始分解前先调用 CadPartSplitter 做零件分割（Part_NN.png 等）");

                // ── ② 执行 / ③ 分步执行 ──
                grpAction.Text = UiText.T("②  执行（一括执行：开始分解 → CMD 分析 → AI 分析 一次跑完）");
                grpSteps.Text = UiText.T("③  分步执行（开始分解 / CMD 分析 / AI 分析 / 第 0 ~ 6 步，可单独执行）");
                btnOpenOutput.Text = UiText.T("打开输出目录");
                lblCmdStepHint.Text = UiText.T("分步执行：");
                lblCmdStepNote.Text = UiText.T("※ 上方【CMD 分析】= 先跑第 0 步（特注品.json 维护），再依次跑完 6 步");
                chkCmdStep0.Text = UiText.T("第 0 步 · 特注品.json 维护（固定先执行）");

                // ── ④ 运行日志 / 分类结果 ──
                tabPageLog.Text = UiText.T("运行日志");
                tabPageClassify.Text = UiText.T("分类结果");
                mnuLogCopyAll.Text = UiText.T("复制全部日志");
                mnuLogSelectAll.Text = UiText.T("全选");
                mnuShowPartInfo.Text = UiText.T("查看部品基本信息");
                mnuPreviewPdf.Text = UiText.T("预览 PDF");
                chkAutoPreview.Text = UiText.T("选中即预览");
                btnPartInfo.Text = UiText.T("部品信息");
                btnPreviewPdf.Text = UiText.T("预览 PDF");
                btnOpenClassifyDir.Text = UiText.T("打开分类目录");

                if (dgvClassify != null)
                {
                    dgvClassify.Columns["colFile"].HeaderText = UiText.T("文件名");
                    dgvClassify.Columns["colCategory"].HeaderText = UiText.T("分类");
                    dgvClassify.Columns["colEvidence"].HeaderText = UiText.T("判定依据");
                }

                UpdateLanguageButtons();
                ApplyDynamicLanguageTexts();
            }
            finally
            {
                _applyingLanguage = false;
            }
        }

        /// <summary>两个语言按钮的选中状态（选中的那个高亮）。</summary>
        private void UpdateLanguageButtons()
        {
            if (btnLangZh == null || btnLangJa == null)
            {
                return;
            }

            bool japanese = UiText.IsJapanese;

            StyleLanguageButton(btnLangZh, !japanese);
            StyleLanguageButton(btnLangJa, japanese);
        }

        private static void StyleLanguageButton(Button button, bool selected)
        {
            button.BackColor = selected
                ? Color.FromArgb(((int)(((byte)(52)))), ((int)(((byte)(92)))), ((int)(((byte)(140)))))
                : Color.FromArgb(((int)(((byte)(27)))), ((int)(((byte)(58)))), ((int)(((byte)(95)))));
            button.ForeColor = selected
                ? Color.White
                : Color.FromArgb(((int)(((byte)(169)))), ((int)(((byte)(196)))), ((int)(((byte)(224)))));
            button.Font = new Font("Microsoft YaHei", 9F,
                selected ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point, ((byte)(134)));
        }

        /// <summary>
        /// 按当前语言重画「运行中会变」的文字：按钮基础文字、第 0 ~ 6 步按钮、
        /// 进度标签、状态栏、日志组标题、分类统计。
        /// </summary>
        private void ApplyDynamicLanguageTexts()
        {
            _startButtonText = UiText.T("开始分解");
            _cmdButtonText = UiText.T("CMD 分析");
            _aiButtonText = UiText.T("AI 分析");
            _runAllButtonText = UiText.T("一括执行");

            UpdateStepButtonTexts();

            bool busy = _isRunning || _allStepsRunning;
            btnStart.Text = _isRunning ? UiText.T("分解中...") : _startButtonText;
            btnCmdAnalyze.Text = _isRunning ? UiText.T("分析中...") : _cmdButtonText;
            btnAiAnalyze.Text = _isRunning ? UiText.T("分析中...") : _aiButtonText;
            btnRunAll.Text = busy ? UiText.T("执行中...") : _runAllButtonText;

            if (!busy)
            {
                lblProgress.Text = UiText.T("等待开始");
            }

            UpdatePdfCountLabel();
            UpdateEngineLabel();
            UpdateLogGroupTitle();
            RefreshLogMenuCaption();
            RefreshClassifySummary();

            lblStatus.Text = UiText.Translate(string.IsNullOrEmpty(_lastStatusText) ? "就绪" : _lastStatusText);
        }

        /// <summary>第 0 ~ 6 步按钮的文字（步骤名只在 CmdAnalysisSteps 里定义，这里只做翻译）。</summary>
        private void UpdateStepButtonTexts()
        {
            btnCmdStep0.Text = UiText.T(CmdAnalysisSteps.ButtonText(CmdAnalysisStepKind.MaintainTzJson));
            btnCmdStep1.Text = UiText.T(CmdAnalysisSteps.ButtonText(CmdAnalysisStepKind.Assembly));
            btnCmdStep2.Text = UiText.T(CmdAnalysisSteps.ButtonText(CmdAnalysisStepKind.CalloutSplit));
            btnCmdStep3.Text = UiText.T(CmdAnalysisSteps.ButtonText(CmdAnalysisStepKind.YoryoAppend));
            btnCmdStep4.Text = UiText.T(CmdAnalysisSteps.ButtonText(CmdAnalysisStepKind.TzAppend));
            btnCmdStep5.Text = UiText.T(CmdAnalysisSteps.ButtonText(CmdAnalysisStepKind.PartCode));
            btnCmdStep6.Text = UiText.T(CmdAnalysisSteps.ButtonText(CmdAnalysisStepKind.UsageKind));
        }

        /// <summary>刷新进度标签（中文原文进 <see cref="UiText.Translate"/>，日语模式输出日语）。</summary>
        private void SetProgressText(string chineseText)
        {
            lblProgress.Text = UiText.Translate(chineseText);
        }

        /// <summary>状态栏「PDF：N 个」（按语言显示「件 / 个」）。</summary>
        private void UpdatePdfCountLabel(int count)
        {
            _lastPdfCount = count;
            UpdatePdfCountLabel();
        }

        private void UpdatePdfCountLabel()
        {
            lblPdfInfo.Text = UiText.F("PDF：{0} 个", _lastPdfCount);
        }

        /// <summary>状态栏「引擎：PdfPig 版本」。</summary>
        private void UpdateEngineLabel(string version)
        {
            _lastEngineVersion = version;
            UpdateEngineLabel();
        }

        private void UpdateEngineLabel()
        {
            if (string.IsNullOrEmpty(_lastEngineVersion))
            {
                lblEngine.Text = UiText.T("引擎：加载中...");
                return;
            }

            lblEngine.Text = UiText.F("引擎：PdfPig {0}", _lastEngineVersion);
        }

        /// <summary>④ 日志组标题（看【分类结果】全画面时文字不同）。</summary>
        private void UpdateLogGroupTitle()
        {
            grpLog.Text = _classifyFullScreen
                ? UiText.T("④  分类结果一览（全画面）— 按 Esc 或切回【运行日志】标签即还原布局")
                : UiText.T("④  运行日志 / 分类结果");
        }

        /// <summary>日志右键菜单第一项：有选中内容时是「复制选中内容」，否则「复制全部内容」。</summary>
        private void RefreshLogMenuCaption()
        {
            bool hasSelection = rtbLog != null && rtbLog.SelectionLength > 0;
            mnuLogCopy.Text = UiText.T(hasSelection ? "复制选中内容" : "复制全部内容");
        }

        /// <summary>
        /// 统一弹窗：标题 / 正文都按当前语言翻译（正文里的动态部分由对照表的模板匹配处理）。
        /// </summary>
        private DialogResult ShowMessage(string message, string caption)
        {
            return ShowMessage(message, caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private DialogResult ShowMessage(string message, string caption,
            MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return MessageBox.Show(this, UiText.Translate(message), UiText.T(caption), buttons, icon);
        }

        private DialogResult ShowMessage(string message, string caption,
            MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
        {
            return MessageBox.Show(this, UiText.Translate(message), UiText.T(caption),
                buttons, icon, defaultButton);
        }
    }
}


