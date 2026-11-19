using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using MSIFAITOOLS.Services;
using PdfiumViewer;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;
using PdfPigPage = UglyToad.PdfPig.Content.Page;

namespace MSIFAITOOLS
{
    /// <summary>
    /// PDF 预览窗口（用 pdfium 渲染成位图）：在【分类结果】列表中选中一行时显示对应 PDF。
    /// 支持翻页、放大 / 缩小、适合宽度、适合页面、实际大小、外部程序打开。
    /// 缩放：Ctrl + 鼠标滚轮（以鼠标位置为中心）；平移：「✋ 拖动」按钮开启后用左键拖动画面，任意模式下按住鼠标中键也可拖动。
    /// 页面上可以像 PDF 阅读器一样拖拽选择文字（双击选词、Ctrl+A 全选、Ctrl+C 复制选中文字），
    /// 文字位置由 PdfPig 的字形坐标提供，与 pdfium 渲染的位图对齐。
    /// 本窗口只做显示，不会修改 PDF 文件。
    /// </summary>
    public partial class PdfPreviewForm : Form
    {
        /// <summary>缩放下限（相对 100% = 96 DPI 的实际大小）。</summary>
        private const float MinZoom = 0.1F;

        /// <summary>缩放上限。</summary>
        private const float MaxZoom = 6F;

        /// <summary>PDF 点（1/72 英寸）到 96 DPI 像素的换算比例，即「100% 显示」。</summary>
        private const float BaseScale = 96F / 72F;

        /// <summary>页面四周留白（像素）。</summary>
        private const int PageMargin = 12;

        /// <summary>单页渲染的最大像素数（防止极端放大占用过多内存）。</summary>
        private const long MaxRenderPixels = 25000000L;

        /// <summary>红框闪烁的间隔（毫秒）：一次亮 / 灭 = 2 × 该值，3 次约 1.2 秒。</summary>
        private const int FlashInterval = 200;

        /// <summary>红框闪烁的翻转次数（3 次亮灭 = 6 次翻转）。</summary>
        private const int FlashToggleCount = 6;

        /// <summary>用于红框定位的关键字最短长度（单个字符会到处命中，不能用来定位）。</summary>
        private const int FlashKeywordMinLength = 2;

        /// <summary>
        /// 宽松匹配（部品CD / 部品図番）时，两个字符之间最多允许夹几个分隔符（<c>-</c> <c>_</c> <c>/</c> <c>・</c> 等）：
        /// 图纸上的部品CD 常印成 <c>MZA1-968</c> / <c>MZB 2-963</c> 这种带分隔符的样子。
        /// </summary>
        private const int FlashMaxSeparatorSkip = 2;

        /// <summary>
        /// 宽松匹配的结果最长允许比关键字多几个字符
        /// （防止一路跳过分隔符、把旁边的文字一起框进红框里）。
        /// </summary>
        private const int FlashMaxExtraLength = 8;

        private PdfDocument _document;

        private string _currentPath;

        private int _pageIndex;

        private float _zoom = BaseScale;

        private PdfScaleMode _scaleMode = PdfScaleMode.FitWidth;

        private bool _isRendering;

        /// <summary>用于「文字可选择」的 PdfPig 文档（打开失败时为 null，预览功能不受影响）。</summary>
        private PdfPigDocument _textDocument;

        /// <summary>PdfPig 文档的只读文件流（共享读写 + 允许删除，预览时其它步骤仍可移动该 PDF）。</summary>
        private FileStream _textStream;

        /// <summary>当前页的文字选择模型（首次需要选择时才解析，避免影响翻页速度）。</summary>
        private PdfTextSelectionModel _selectionModel;

        /// <summary>文字选择模型对应的页索引（-1 表示尚未加载）。</summary>
        private int _selectionModelPage = -1;

        /// <summary>
        /// 闪烁红框的位置（PDF 坐标，左上角原点、单位 PDF 点）。<c>null</c> = 不显示。
        /// 部品基本信息画面选中一行时用它在预览画面上标出这一行的来源位置
        /// （见 <see cref="FlashTextHighlight"/>）。
        /// </summary>
        private PdfSelectionRectangle? _flashRectangle;

        /// <summary>闪烁红框所在的页索引（-1 = 不显示）。</summary>
        private int _flashPage = -1;

        /// <summary>闪烁红框此刻是否可见（定时器每响一次翻转）。</summary>
        private bool _flashVisible;

        /// <summary>剩下的翻转次数（6 次 = 亮 / 灭各 3 次）。</summary>
        private int _flashTogglesLeft;

        /// <summary>红框闪烁定时器（间隔 <see cref="FlashInterval"/> 毫秒）。</summary>
        private Timer _flashTimer;

        /// <summary>选择的起始插入符位置（0 ~ 字符总数，-1 表示没有选择）。</summary>
        private int _selectionAnchor = -1;

        /// <summary>选择的结束插入符位置。</summary>
        private int _selectionCaret = -1;

        /// <summary>是否正在拖动选择文字。</summary>
        private bool _isSelectingText;

        /// <summary>页面右键菜单（复制 / 全选 / 清除选择）。</summary>
        private ContextMenuStrip _selectionMenu;

        /// <summary>右键菜单项：复制选中文字。</summary>
        private ToolStripMenuItem _miCopySelection;

        /// <summary>右键菜单项：全选本页文字。</summary>
        private ToolStripMenuItem _miSelectAll;

        /// <summary>是否处于「小手拖动」模式（开启后左键拖动只平移画面，不选择文字）。</summary>
        private bool _isPanMode;

        /// <summary>是否正在拖动画面。</summary>
        private bool _isPanning;

        /// <summary>开始拖动时的鼠标屏幕坐标。</summary>
        private Point _panStartScreen;

        /// <summary>开始拖动时画布的滚动偏移（正值，单位像素）。</summary>
        private Point _panStartScroll;

        /// <summary>鼠标滚轮消息过滤器（Ctrl + 滚轮缩放，普通滚轮滚动画布）。</summary>
        private CanvasWheelMessageFilter _wheelFilter;

        public PdfPreviewForm()
        {
            InitializeComponent();
            ApplyButtonStyle();
            InitializeSelection();
            InitializeFlashHighlight();

            // 按钮 / 状态栏 / 右键菜单的中日切换（见 Services\UiLocalization.cs）：
            // 右键菜单项要在这一步之前建好，挂接时才能一起记下中文原文
            UiLocalization.Attach(this);

            InitializePan();
        }

        /// <summary>缩放方式。</summary>
        private enum PdfScaleMode
        {
            /// <summary>用户指定的固定缩放（放大 / 缩小 / 实际大小）。</summary>
            Custom,

            /// <summary>按窗口宽度适应。</summary>
            FitWidth,

            /// <summary>整页适应窗口。</summary>
            FitPage
        }

        /// <summary>当前显示的 PDF 完整路径（未加载时为 null）。</summary>
        public string CurrentPath
        {
            get { return _currentPath; }
        }

        /// <summary>
        /// 显示窗口时不抢焦点：预览是辅助窗口，不打断主画面 / 部品基本信息画面的键盘操作
        /// （「选中即预览」用方向键移动行时焦点留在列表里）。
        /// 需要把预览提到最前时由调用方显式 BringToFront() + Activate()（双击行 /【预览 PDF】）。
        /// </summary>
        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        /// <summary>
        /// 显示指定的 PDF。若与当前显示的已是同一文件，则只把窗口前置并保持当前页 / 缩放。
        /// 文件不存在或损坏时在状态栏与弹窗中提示，不抛出异常。
        /// </summary>
        /// <param name="pdfFilePath">PDF 完整路径。</param>
        /// <param name="activate">
        /// 已是同一文件时是否激活（提到最前）本窗口；
        /// 「选中即预览」传 false，避免预览画面一直跳到主画面前面。
        /// </param>
        public void ShowPdf(string pdfFilePath, bool activate = true)
        {
            if (string.IsNullOrWhiteSpace(pdfFilePath))
            {
                return;
            }

            if (!File.Exists(pdfFilePath))
            {
                ShowError("文件不存在：" + pdfFilePath);
                return;
            }

            if (_document != null && string.Equals(_currentPath, pdfFilePath, StringComparison.OrdinalIgnoreCase))
            {
                if (activate)
                {
                    if (WindowState == FormWindowState.Minimized)
                    {
                        WindowState = FormWindowState.Normal;
                    }

                    Activate();
                }

                return;
            }

            LoadDocument(pdfFilePath, activate);
        }

        /// <summary>
        /// 在指定页上找到 <paramref name="keywords"/> 里第一个出现的文字，用红框闪 <b>3 次</b>标出它的位置，
        /// 并把画面滚到能看见它的地方。部品基本信息画面「选中记录时」用它指出这一行在图纸上的来源位置。
        /// <para>
        /// 找不到时（该页没有这些文字 / PDF 没有文字层 / 文档未打开）什么都不做，只返回 <c>false</c>：
        /// 不弹提示、也不改状态栏，避免每选一行就报一次错。
        /// </para>
        /// </summary>
        /// <param name="pageIndex">页索引（0 起；超出范围时夹到 0 ～ 页数-1）。</param>
        /// <param name="keywords">
        /// 要定位的文字（按顺序尝试，先用排在前面的；部品基本信息画面传的是
        /// <b>部品図番 → 部品CD（部品コード）→ 部品名</b>，这一行有部品図番 时就是它第 1 优先）。
        /// 只在同一行（竖排时同一列）内匹配，全角 / 半角与空白差异会被忽略（见 <see cref="NormalizeFlashKeyword"/>）。
        /// 同一个关键字命中多行时，优先「同一行里还含有其它关键字」最多的那一行（同分取最前）。
        /// 每个关键字按原样找不到时还会再走一遍<b>宽松匹配</b>（见 <see cref="TryMatchFlashPattern"/>）：
        /// <c>□</c> <c>○</c> 之类的可变位当「任意 0～1 个字符」、字符之间容忍 <c>-</c> 等分隔符，
        /// 因为库里的部品CD（<c>MZA□968</c>）和图纸上印的（<c>MZA1-968</c>）写法常常不一样，
        /// 部品図番 在图上也可能少了 <c>-</c>（库 <c>HJ-1234</c>，图纸 <c>HJ1234</c>）。
        /// 若某个含可变位的关键字（模糊 CD）宽松匹配也没命中，<b>不再退到后面的关键字</b>（直接返回 <c>false</c>）。
        /// </param>
        /// <returns>true = 找到并已开始闪烁；false = 没找到（或文档未打开）。</returns>
        public bool FlashTextHighlight(int pageIndex, IList<string> keywords)
        {
            if (_document == null || keywords == null || keywords.Count == 0)
            {
                return false;
            }

            int target = pageIndex;

            if (target < 0)
            {
                target = 0;
            }

            if (target > _document.PageCount - 1)
            {
                target = _document.PageCount - 1;
            }

            if (target != _pageIndex)
            {
                // 翻到那一页（换页时旧的闪烁红框会在 RenderPage 里清掉）
                GoToPage(target);
            }

            if (_document == null)
            {
                return false;
            }

            PdfTextSelectionModel model = GetPageSelectionModel(_pageIndex);

            if (model == null)
            {
                return false;
            }

            PdfSelectionRectangle rectangle;

            if (!TryFindTextRectangle(model, keywords, out rectangle))
            {
                return false;
            }

            ShowFlashRectangle(rectangle);

            return true;
        }

        /// <summary>
        /// 加载 PDF（失败时关闭旧文档并给出提示）。
        /// </summary>
        /// <param name="pdfFilePath">PDF 完整路径。</param>
        /// <param name="showErrorDialog">
        /// 失败时是否弹出对话框：用户主动预览（双击 /【预览 PDF】/ 右键菜单）传 true；
        /// 「选中即预览」传 false —— 每换一行就弹一个框太吵，错误只写在窗口状态栏里。
        /// </param>
        private void LoadDocument(string pdfFilePath, bool showErrorDialog)
        {
            Cursor previousCursor = Cursor;

            Cursor = Cursors.WaitCursor;

            try
            {
                CloseDocument();

                string pdfiumMessage;

                if (!PdfiumRuntime.EnsureAvailable(out pdfiumMessage))
                {
                    // 原生库不可用时不能碰 PdfiumViewer：PdfLibrary 初始化 / 终结时要调
                    // FPDF_AddRef / FPDF_Release，缺这两个导出时会在终结器里再抛一次
                    // EntryPointNotFoundException（调试器里看起来像整个程序崩了）。
                    throw new InvalidOperationException("PDF 渲染引擎不可用（pdfium.dll）。" + pdfiumMessage +
                        Environment.NewLine + "可先用工具栏的【打开外部】让系统默认程序打开。");
                }

                _document = PdfDocument.Load(pdfFilePath);
                _currentPath = pdfFilePath;
                _pageIndex = 0;
                _scaleMode = PdfScaleMode.FitWidth;

                // 另外用 PdfPig 打开一份（只读）用于文字选择；失败时只影响“能否选字”，不影响预览
                OpenTextDocument(pdfFilePath);

                UiLocalization.SetText(this, "PDF 预览 - " + Path.GetFileName(pdfFilePath));

                RenderPage();
            }
            catch (Exception ex)
            {
                CloseDocument();

                // 保留路径：【打开外部】按钮还要用它（引擎不可用时仍能让系统默认程序打开 PDF）
                _currentPath = pdfFilePath;
                UiLocalization.SetText(this, "PDF 预览 - 读取失败");
                ShowError("无法显示该 PDF：" + ex.Message);

                if (showErrorDialog)
                {
                    ShowMessage(
                        "无法显示该 PDF：\n" + pdfFilePath + "\n\n" + ex.Message,
                        "PDF 预览", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            finally
            {
                Cursor = previousCursor;
            }
        }

        /// <summary>释放当前 PDF 与页面位图。</summary>
        private void CloseDocument()
        {
            // 换文档 / 关闭窗口：闪烁红框立刻清掉
            ClearFlashHighlight();

            Image image = picPage.Image;

            picPage.Image = null;

            if (image != null)
            {
                image.Dispose();
            }

            if (_document != null)
            {
                _document.Dispose();
                _document = null;
            }

            CloseTextDocument();
        }

        /// <summary>
        /// 打开 PdfPig 文档用于文字选择。用 FileShare.ReadWrite | Delete 打开文件流，
        /// 保证预览期间其它步骤（分类整理）仍能移动 / 覆盖该 PDF。
        /// </summary>
        private void OpenTextDocument(string pdfFilePath)
        {
            CloseTextDocument();

            try
            {
                _textStream = new FileStream(pdfFilePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                _textDocument = PdfPigDocument.Open(_textStream);
            }
            catch (Exception)
            {
                // PDF 加密 / 损坏等情况下 PdfPig 可能打不开：此时禁用选字，预览照常可用
                CloseTextDocument();
            }
        }

        /// <summary>释放 PdfPig 文档、文件流与文字选择模型。</summary>
        private void CloseTextDocument()
        {
            ResetSelection();

            if (_textDocument != null)
            {
                try
                {
                    _textDocument.Dispose();
                }
                catch (Exception)
                {
                    // 释放失败不影响窗口关闭
                }

                _textDocument = null;
            }

            if (_textStream != null)
            {
                try
                {
                    _textStream.Dispose();
                }
                catch (Exception)
                {
                    // 释放失败不影响窗口关闭
                }

                _textStream = null;
            }

            _selectionModel = null;
            _selectionModelPage = -1;
        }

        /// <summary>按当前页 / 缩放方式渲染页面位图并显示。</summary>
        private void RenderPage()
        {
            if (_document == null || _isRendering)
            {
                return;
            }

            int pageCount = _document.PageCount;

            if (pageCount <= 0)
            {
                ShowError("该 PDF 没有可显示的页面。");
                return;
            }

            if (_pageIndex < 0)
            {
                _pageIndex = 0;
            }

            if (_pageIndex >= pageCount)
            {
                _pageIndex = pageCount - 1;
            }

            SizeF pageSize;

            try
            {
                pageSize = _document.PageSizes[_pageIndex];
            }
            catch (Exception ex)
            {
                ShowError("读取页面尺寸失败：" + ex.Message);
                return;
            }

            if (pageSize.Width <= 0F || pageSize.Height <= 0F)
            {
                ShowError("页面尺寸无效。");
                return;
            }

            int viewWidth = Math.Max(120, pnlCanvas.ClientSize.Width - PageMargin * 2);
            int viewHeight = Math.Max(120, pnlCanvas.ClientSize.Height - PageMargin * 2);
            float scale;

            switch (_scaleMode)
            {
                case PdfScaleMode.FitWidth:
                    scale = viewWidth / pageSize.Width;
                    break;

                case PdfScaleMode.FitPage:
                    scale = Math.Min(viewWidth / pageSize.Width, viewHeight / pageSize.Height);
                    break;

                default:
                    scale = _zoom;
                    break;
            }

            scale = Clamp(scale, MinZoom, MaxZoom);

            int width = Math.Max(1, (int)Math.Round(pageSize.Width * scale));
            int height = Math.Max(1, (int)Math.Round(pageSize.Height * scale));
            long pixels = (long)width * height;

            if (pixels > MaxRenderPixels)
            {
                // 极端放大时按比例收缩，避免一次申请过大的位图
                double factor = Math.Sqrt((double)MaxRenderPixels / pixels);

                width = Math.Max(1, (int)(width * factor));
                height = Math.Max(1, (int)(height * factor));
                scale = width / pageSize.Width;
            }

            _zoom = scale;
            _isRendering = true;

            // 页码变化时清空选择；缩放只改变像素比例，PDF 坐标不变，选择可以保留
            if (_selectionModelPage != _pageIndex)
            {
                ResetSelection();
            }

            // 闪烁红框只属于原来那一页，换页后丢掉（缩放时 PDF 坐标不变，保留即可）
            if (_flashRectangle.HasValue && _flashPage != _pageIndex)
            {
                ClearFlashHighlight();
            }

            try
            {
                Image image = _document.Render(_pageIndex, width, height, 96F, 96F, false);
                Image previous = picPage.Image;

                picPage.Size = new Size(width, height);
                picPage.Image = image;

                if (previous != null)
                {
                    previous.Dispose();
                }

                LayoutPage();
                UpdateStatus();
            }
            catch (Exception ex)
            {
                ShowError("渲染失败：" + ex.Message);
            }
            finally
            {
                _isRendering = false;
            }
        }

        /// <summary>把页面位图居中放在画布中（比可视区大时靠左上并出现滚动条）。</summary>
        private void LayoutPage()
        {
            int left = Math.Max(PageMargin, (pnlCanvas.ClientSize.Width - picPage.Width) / 2);

            picPage.Location = new Point(left, PageMargin);
        }

        /// <summary>刷新状态栏（文件名 / 页码 / 缩放比例）。</summary>
        private void UpdateStatus()
        {
            lblStatus.ForeColor = Color.FromArgb(52, 73, 94);

            if (_document == null)
            {
                UiLocalization.SetText(lblStatus, "尚未选择 PDF");
                lblPageInfo.Text = "- / -";
                lblZoom.Text = "-";
                return;
            }

            int percent = (int)Math.Round(_zoom / BaseScale * 100);

            lblPageInfo.Text = (_pageIndex + 1) + " / " + _document.PageCount;
            lblZoom.Text = percent + "%";

            // 中文原文照旧拼好（对照表按模板匹配），再交给 SetText 翻成当前语言
            string status = string.Format("{0}　第 {1} / {2} 页　缩放 {3}%　（← → 翻页，Ctrl+滚轮 / +- 缩放，滚轮滚动，拖拽选字后 Ctrl+C 复制）",
                Path.GetFileName(_currentPath ?? string.Empty),
                _pageIndex + 1,
                _document.PageCount,
                percent);

            if (_isPanMode)
            {
                status += "　拖动模式：按住左键拖动画布";
            }

            PdfTextRange selection = GetSelectionRange();

            if (!selection.IsEmpty)
            {
                status += "　已选 " + selection.Length + " 个字符";
            }

            UiLocalization.SetText(lblStatus, status);
        }

        /// <summary>在状态栏显示红色错误信息。</summary>
        private void ShowError(string message)
        {
            UiLocalization.SetText(lblStatus, message);
            lblStatus.ForeColor = Color.FromArgb(198, 40, 40);
        }

        /// <summary>统一弹窗：标题 / 正文都按当前语言翻译（正文里的动态部分由对照表的模板匹配处理）。</summary>
        private DialogResult ShowMessage(string message, string caption,
            MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return MessageBox.Show(this, UiText.Translate(message), UiText.T(caption), buttons, icon);
        }

        /// <summary>把数值限制在 [min, max] 区间内。</summary>
        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
            {
                return min;
            }

            if (value > max)
            {
                return max;
            }

            return value;
        }

        /// <summary>上一页。</summary>
        private void BtnPrevPage_Click(object sender, EventArgs e)
        {
            GoToPage(_pageIndex - 1);
        }

        /// <summary>下一页。</summary>
        private void BtnNextPage_Click(object sender, EventArgs e)
        {
            GoToPage(_pageIndex + 1);
        }

        /// <summary>跳到指定页（自动限制在有效范围内）。</summary>
        private void GoToPage(int pageIndex)
        {
            if (_document == null)
            {
                return;
            }

            int target = pageIndex;

            if (target < 0)
            {
                target = 0;
            }

            if (target > _document.PageCount - 1)
            {
                target = _document.PageCount - 1;
            }

            if (target == _pageIndex)
            {
                return;
            }

            _pageIndex = target;
            RenderPage();
            pnlCanvas.AutoScrollPosition = new Point(0, 0);
        }

        /// <summary>放大一档。</summary>
        private void BtnZoomIn_Click(object sender, EventArgs e)
        {
            ApplyZoom(_zoom * 1.25F);
        }

        /// <summary>缩小一档。</summary>
        private void BtnZoomOut_Click(object sender, EventArgs e)
        {
            ApplyZoom(_zoom / 1.25F);
        }

        /// <summary>按固定比例缩放（100% = 96 DPI 的实际大小）。</summary>
        private void ApplyZoom(float zoom)
        {
            if (_document == null)
            {
                return;
            }

            _scaleMode = PdfScaleMode.Custom;
            _zoom = Clamp(zoom, MinZoom, MaxZoom);
            RenderPage();
        }

        /// <summary>适合窗口宽度。</summary>
        private void BtnFitWidth_Click(object sender, EventArgs e)
        {
            _scaleMode = PdfScaleMode.FitWidth;
            RenderPage();
        }

        /// <summary>整页适应窗口。</summary>
        private void BtnFitPage_Click(object sender, EventArgs e)
        {
            _scaleMode = PdfScaleMode.FitPage;
            RenderPage();
        }

        /// <summary>恢复到实际大小（100%）。</summary>
        private void BtnActualSize_Click(object sender, EventArgs e)
        {
            ApplyZoom(BaseScale);
        }

        /// <summary>用系统默认程序打开当前 PDF（例如 Adobe Reader，便于打印或批注）。</summary>
        private void BtnOpenExternal_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_currentPath) || !File.Exists(_currentPath))
            {
                ShowError("当前没有可打开的 PDF。");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(_currentPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ShowError("无法调用外部程序打开：" + ex.Message);
            }
        }

        /// <summary>关闭预览窗口。</summary>
        private void BtnClose_Click(object sender, EventArgs e)
        {
            Close();
        }

        /// <summary>画布尺寸变化：延迟重新渲染，避免拖动窗口时反复渲染。</summary>
        private void PnlCanvas_Resize(object sender, EventArgs e)
        {
            if (_document == null)
            {
                return;
            }

            resizeTimer.Stop();
            resizeTimer.Start();
        }

        /// <summary>尺寸稳定后按「适合宽度 / 适合页面」重新渲染。</summary>
        private void ResizeTimer_Tick(object sender, EventArgs e)
        {
            resizeTimer.Stop();

            if (_document == null || _scaleMode == PdfScaleMode.Custom)
            {
                return;
            }

            RenderPage();
        }

        /// <summary>键盘操作：← / PageUp 上一页，→ / PageDown 下一页，+ / - 缩放，Ctrl+C 复制选中文字，Ctrl+A 全选，Esc 关闭。</summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.Control && e.KeyCode == Keys.C)
            {
                CopySelection();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.Control && e.KeyCode == Keys.A)
            {
                SelectAllText();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            switch (e.KeyCode)
            {
                case Keys.Left:
                case Keys.PageUp:
                    GoToPage(_pageIndex - 1);
                    e.Handled = true;
                    break;

                case Keys.Right:
                case Keys.PageDown:
                    GoToPage(_pageIndex + 1);
                    e.Handled = true;
                    break;

                case Keys.Add:
                case Keys.Oemplus:
                    ApplyZoom(_zoom * 1.25F);
                    e.Handled = true;
                    break;

                case Keys.Subtract:
                case Keys.OemMinus:
                    ApplyZoom(_zoom / 1.25F);
                    e.Handled = true;
                    break;

                case Keys.Escape:
                    if (!GetSelectionRange().IsEmpty)
                    {
                        ResetSelection();
                        picPage.Invalidate();
                        UpdateStatus();
                    }
                    else
                    {
                        Close();
                    }

                    break;
            }
        }

        /// <summary>
        /// 初始化文字选择：把鼠标 / 绘制 / 右键菜单事件挂到页面位图上。
        /// 高亮直接画在 PictureBox 的 Paint 事件里，保证与页面位图像素坐标一一对应。
        /// </summary>
        private void InitializeSelection()
        {
            _miCopySelection = new ToolStripMenuItem("复制选中文字(&C)", null, MiCopySelection_Click);
            _miSelectAll = new ToolStripMenuItem("全选本页文字(&A)", null, MiSelectAll_Click);
            ToolStripMenuItem clearItem = new ToolStripMenuItem("清除选择(&E)", null, MiClearSelection_Click);

            _selectionMenu = new ContextMenuStrip();
            _selectionMenu.Items.Add(_miCopySelection);
            _selectionMenu.Items.Add(_miSelectAll);
            _selectionMenu.Items.Add(new ToolStripSeparator());
            _selectionMenu.Items.Add(clearItem);
            _selectionMenu.Opening += SelectionMenu_Opening;

            picPage.ContextMenuStrip = _selectionMenu;
            picPage.Paint += PicPage_Paint;
            picPage.MouseDown += PicPage_MouseDown;
            picPage.MouseMove += PicPage_MouseMove;
            picPage.MouseUp += PicPage_MouseUp;
            picPage.MouseLeave += PicPage_MouseLeave;
        }

        /// <summary>
        /// 初始化平移（小手）与滚轮支持：按钮提示、进程级滚轮过滤器。
        /// 滚轮用消息过滤器处理，因为 WM_MOUSEWHEEL 只发给有焦点的控件，
        /// 若只在 Form.WndProc 里处理，点过工具栏按钮后（焦点在按钮上）就会失效。
        /// </summary>
        private void InitializePan()
        {
            ToolTip tip = new ToolTip(components);

            UiLocalization.SetToolTip(tip, btnPan,
                "开启后用左键按住页面拖动画面（任何模式下按住鼠标中键也可拖动）\nCtrl + 鼠标滚轮：以鼠标位置为中心缩放");
            UiLocalization.SetToolTip(tip, pnlCanvas, "Ctrl + 鼠标滚轮缩放，滚轮上下滚动画面");

            _wheelFilter = new CanvasWheelMessageFilter(this);
            Application.AddMessageFilter(_wheelFilter);

            UpdatePanButton();
        }

        /// <summary>工具栏：切换「✋ 拖动」模式。</summary>
        private void BtnPan_Click(object sender, EventArgs e)
        {
            SetPanMode(!_isPanMode);
        }

        /// <summary>开启 / 关闭拖动模式（开启后左键拖动平移画面，不再选择文字）。</summary>
        private void SetPanMode(bool enabled)
        {
            if (_isPanMode == enabled)
            {
                return;
            }

            _isPanMode = enabled;

            if (!_isPanMode)
            {
                EndPan();
            }

            UpdatePanButton();
            UpdateStatus();
        }

        /// <summary>按当前拖动模式刷新「✋ 拖动」按钮与页面光标。</summary>
        private void UpdatePanButton()
        {
            if (_isPanMode)
            {
                btnPan.BackColor = Color.FromArgb(204, 226, 247);
                btnPan.ForeColor = Color.FromArgb(0, 80, 160);
                btnPan.FlatAppearance.BorderColor = Color.FromArgb(0, 120, 215);
                picPage.Cursor = Cursors.Hand;
            }
            else
            {
                btnPan.BackColor = Color.FromArgb(232, 237, 243);
                btnPan.ForeColor = Color.FromArgb(52, 73, 94);
                btnPan.FlatAppearance.BorderColor = Color.FromArgb(199, 208, 219);
                picPage.Cursor = Cursors.Default;
            }
        }

        /// <summary>开始拖动画面（记住起始鼠标位置与滚动偏移）。</summary>
        /// <param name="screenPoint">鼠标当前屏幕坐标。</param>
        private void BeginPan(Point screenPoint)
        {
            if (_document == null)
            {
                return;
            }

            _isPanning = true;
            _panStartScreen = screenPoint;
            _panStartScroll = GetCanvasScrollOffset();
            picPage.Capture = true;
            picPage.Cursor = Cursors.Hand;
        }

        /// <summary>拖动画面：鼠标移动多少，内容就跟着移动多少（抓手手感）。</summary>
        /// <param name="screenPoint">鼠标当前屏幕坐标。</param>
        private void UpdatePanTo(Point screenPoint)
        {
            int offsetX = _panStartScroll.X - (screenPoint.X - _panStartScreen.X);
            int offsetY = _panStartScroll.Y - (screenPoint.Y - _panStartScreen.Y);

            pnlCanvas.AutoScrollPosition = new Point(Math.Max(0, offsetX), Math.Max(0, offsetY));
        }

        /// <summary>结束拖动画面。</summary>
        private void EndPan()
        {
            if (!_isPanning)
            {
                return;
            }

            _isPanning = false;

            if (picPage.Capture)
            {
                picPage.Capture = false;
            }

            picPage.Cursor = _isPanMode ? Cursors.Hand : Cursors.Default;
        }

        /// <summary>当前画布的滚动偏移（正值，单位像素）。</summary>
        private Point GetCanvasScrollOffset()
        {
            Point position = pnlCanvas.AutoScrollPosition;

            return new Point(-position.X, -position.Y);
        }

        /// <summary>按像素滚动画布（offsetY 为正表示向下滚动、看到更下面的内容）。</summary>
        private void ScrollCanvasBy(int offsetY)
        {
            Point scroll = GetCanvasScrollOffset();

            pnlCanvas.AutoScrollPosition = new Point(scroll.X, Math.Max(0, scroll.Y + offsetY));
        }

        /// <summary>按像素横向滚动画布（Shift + 滚轮时使用）。</summary>
        private void ScrollCanvasHorizontal(int offsetX)
        {
            Point scroll = GetCanvasScrollOffset();

            pnlCanvas.AutoScrollPosition = new Point(Math.Max(0, scroll.X + offsetX), scroll.Y);
        }

        /// <summary>以画布上的指定点为中心缩放（缩放后该点下的内容保持不动）。</summary>
        private void ZoomAtPoint(float factor, Point canvasPoint)
        {
            if (_document == null || picPage.Image == null || _zoom <= 0F)
            {
                return;
            }

            float oldZoom = _zoom;
            float newZoom = Clamp(oldZoom * factor, MinZoom, MaxZoom);

            if (Math.Abs(newZoom - oldZoom) < 0.0001F)
            {
                return;
            }

            // 鼠标位置下的 PDF 点（相对页面左上角，单位 PDF 点）。
            // picPage.Left / Top 已经是「页面在画布上的显示位置」（AutoScroll 会把子控件移到负坐标），
            // 所以这里不能再加滚动偏移。
            double pdfX = (canvasPoint.X - picPage.Left) / (double)oldZoom;
            double pdfY = (canvasPoint.Y - picPage.Top) / (double)oldZoom;

            ApplyZoom(newZoom);

            // 缩放后位图已重新渲染并重新居中，把滚动位置调到让同一个 PDF 点回到鼠标处。
            // 设置滚动条可能改变可视区宽度（滚动条出现 / 消失），所以校正两次。
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Point scroll = GetCanvasScrollOffset();

                // 页面左上角在「内容坐标」中的位置 = 显示位置 + 滚动偏移
                int contentLeft = picPage.Left + scroll.X;
                int contentTop = picPage.Top + scroll.Y;

                Point next = new Point(
                    Math.Max(0, (int)Math.Round(contentLeft + pdfX * _zoom - canvasPoint.X)),
                    Math.Max(0, (int)Math.Round(contentTop + pdfY * _zoom - canvasPoint.Y)));

                if (next == scroll)
                {
                    break;
                }

                pnlCanvas.AutoScrollPosition = next;
            }
        }

        /// <summary>
        /// 处理鼠标滚轮消息。返回 true 表示已处理（不再交给焦点控件）。
        /// Ctrl + 滚轮：以鼠标位置为中心缩放；普通滚轮：滚动画面。
        /// </summary>
        private bool HandleCanvasWheel(ref Message m)
        {
            if (IsDisposed || !Visible || !ContainsFocus)
            {
                return false;
            }

            Point screenPoint = Cursor.Position;
            bool inCanvas = pnlCanvas.RectangleToScreen(pnlCanvas.ClientRectangle).Contains(screenPoint);
            bool control = (ModifierKeys & Keys.Control) == Keys.Control;

            if (!inCanvas && !control)
            {
                return false;
            }

            // WParam 高 16 位是有符号的滚轮增量（+120 向前 / -120 向后，高精度滚轮可能是其它倍数）
            int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);

            if (delta == 0 || _document == null)
            {
                return false;
            }

            if (control)
            {
                Point center = inCanvas
                    ? pnlCanvas.PointToClient(screenPoint)
                    : new Point(pnlCanvas.ClientSize.Width / 2, pnlCanvas.ClientSize.Height / 2);

                ZoomAtPoint(delta > 0 ? 1.15F : 1F / 1.15F, center);
                return true;
            }

            int lines = SystemInformation.MouseWheelScrollLines;

            if (lines < 0)
            {
                // 系统设置为「滚轮不滚动」
                return true;
            }

            int step = lines == 0
                ? Math.Max(40, pnlCanvas.ClientSize.Height - 20)
                : Math.Max(20, lines * 20);

            if ((ModifierKeys & Keys.Shift) == Keys.Shift)
            {
                // Shift + 滚轮：横向滚动
                ScrollCanvasHorizontal(-delta * step / 120);
            }
            else
            {
                ScrollCanvasBy(-delta * step / 120);
            }

            return true;
        }

        /// <summary>菜单弹出时按当前状态启用 / 禁用菜单项。</summary>
        private void SelectionMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _miCopySelection.Enabled = !GetSelectionRange().IsEmpty;
            _miSelectAll.Enabled = _textDocument != null || _selectionModel != null;
        }

        /// <summary>在页面位图之上绘制选择高亮与插入符竖线。</summary>
        private void PicPage_Paint(object sender, PaintEventArgs e)
        {
            // 闪烁红框（部品基本信息画面选中记录时标出来源位置）
            DrawFlashHighlight(e.Graphics);

            if (_selectionModel == null)
            {
                return;
            }

            float scale = _zoom;
            PdfTextRange range = GetSelectionRange();

            if (!range.IsEmpty)
            {
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(70, 0, 120, 215)))
                {
                    foreach (PdfSelectionRectangle item in _selectionModel.GetRectangles(range))
                    {
                        e.Graphics.FillRectangle(brush, ToPixelRectangle(item, scale));
                    }
                }
            }

            if (_isSelectingText && _selectionCaret >= 0)
            {
                RectangleF caret = ToPixelRectangle(_selectionModel.GetCaretBar(_selectionCaret), scale);

                if (caret.Width >= caret.Height)
                {
                    // 竖排文字的插入符是一条横线
                    caret.Height = 1F;
                }
                else
                {
                    caret.Width = 1F;
                }

                using (SolidBrush caretBrush = new SolidBrush(Color.FromArgb(210, 0, 90, 180)))
                {
                    e.Graphics.FillRectangle(caretBrush, caret);
                }
            }
        }

        /// <summary>把 PDF 点坐标的矩形换算成位图像素坐标的矩形。</summary>
        private static RectangleF ToPixelRectangle(PdfSelectionRectangle rect, float scale)
        {
            return new RectangleF(
                (float)(rect.Left * scale),
                (float)(rect.Top * scale),
                Math.Max(1F, (float)(rect.Width * scale)),
                Math.Max(1F, (float)(rect.Height * scale)));
        }

        /// <summary>按下鼠标：拖动模式下（或按中键）平移画面，否则开始新的选择（双击选词、三击选行）。</summary>
        private void PicPage_MouseDown(object sender, MouseEventArgs e)
        {
            // 中键：任何模式下都可以拖动画布
            if (e.Button == MouseButtons.Middle)
            {
                BeginPan(Cursor.Position);
                return;
            }

            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            // 「✋ 拖动」模式下左键只平移画面，不选择文字
            if (_isPanMode)
            {
                BeginPan(Cursor.Position);
                return;
            }

            PdfTextSelectionModel model = EnsureSelectionModel();

            if (model == null)
            {
                return;
            }

            double x;
            double y;

            if (!TryGetPdfPoint(e, out x, out y))
            {
                return;
            }

            int caret = model.HitTestCaret(x, y);

            if (caret < 0)
            {
                ResetSelection();
                picPage.Invalidate();
                UpdateStatus();
                return;
            }

            if (e.Clicks >= 3)
            {
                ApplySelection(model.SelectLine(caret));
                return;
            }

            if (e.Clicks == 2)
            {
                ApplySelection(model.SelectWord(caret));
                return;
            }

            _selectionAnchor = caret;
            _selectionCaret = caret;
            _isSelectingText = true;
            picPage.Capture = true;
            picPage.Invalidate();
            UpdateStatus();
        }

        /// <summary>移动鼠标：拖动时平移画面；拖动模式下固定手形光标；否则扩展选择 / 切换 I 形光标。</summary>
        private void PicPage_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isPanning)
            {
                UpdatePanTo(Cursor.Position);
                return;
            }

            if (_isPanMode)
            {
                picPage.Cursor = Cursors.Hand;
                return;
            }

            if (_selectionModel == null)
            {
                return;
            }

            double x;
            double y;

            if (!TryGetPdfPoint(e, out x, out y))
            {
                return;
            }

            if (!_isSelectingText)
            {
                picPage.Cursor = _selectionModel.HitTestCaret(x, y) >= 0 ? Cursors.IBeam : Cursors.Default;
                return;
            }

            int caret = _selectionModel.HitTestCaret(x, y);

            if (caret < 0 || caret == _selectionCaret)
            {
                return;
            }

            _selectionCaret = caret;
            picPage.Invalidate();
            UpdateStatus();
        }

        /// <summary>松开鼠标：结束拖动或结束拖动选择。</summary>
        private void PicPage_MouseUp(object sender, MouseEventArgs e)
        {
            if (_isPanning)
            {
                EndPan();
                return;
            }

            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            _isSelectingText = false;

            if (picPage.Capture)
            {
                picPage.Capture = false;
            }

            picPage.Invalidate();
            UpdateStatus();
        }

        /// <summary>鼠标离开页面时恢复默认光标（拖动中或拖动模式下保持手形）。</summary>
        private void PicPage_MouseLeave(object sender, EventArgs e)
        {
            if (_isSelectingText || _isPanning || _isPanMode)
            {
                return;
            }

            picPage.Cursor = Cursors.Default;
        }

        /// <summary>当前选择的字符范围（没有选择时为空范围）。</summary>
        private PdfTextRange GetSelectionRange()
        {
            if (_selectionModel == null || _selectionAnchor < 0 || _selectionCaret < 0)
            {
                return PdfTextRange.Empty;
            }

            int start = Math.Min(_selectionAnchor, _selectionCaret);
            int end = Math.Max(_selectionAnchor, _selectionCaret) - 1;

            return start <= end ? new PdfTextRange(start, end) : PdfTextRange.Empty;
        }

        /// <summary>清除当前选择。</summary>
        private void ResetSelection()
        {
            _selectionAnchor = -1;
            _selectionCaret = -1;
            _isSelectingText = false;

            if (picPage.Capture)
            {
                picPage.Capture = false;
            }
        }

        /// <summary>把字符范围设置成当前选择并刷新显示。</summary>
        private void ApplySelection(PdfTextRange range)
        {
            if (range.IsEmpty)
            {
                ResetSelection();
            }
            else
            {
                _selectionAnchor = range.Start;
                _selectionCaret = range.End + 1;
                _isSelectingText = false;
            }

            picPage.Invalidate();
            UpdateStatus();
        }

        /// <summary>取当前页的文字选择模型（首次使用时才解析 PdfPig 内容，失败返回 null）。</summary>
        private PdfTextSelectionModel EnsureSelectionModel()
        {
            if (_selectionModel != null && _selectionModelPage == _pageIndex)
            {
                return _selectionModel;
            }

            if (_textDocument == null)
            {
                ShowError("本页文字无法选择：该 PDF 无法用 PdfPig 解析（可能是加密文件）。");
                return null;
            }

            Cursor previousCursor = Cursor;

            Cursor = Cursors.WaitCursor;

            try
            {
                PdfPigPage page = _textDocument.GetPage(_pageIndex + 1);

                _selectionModel = PdfTextSelectionModel.Create(page);
                _selectionModelPage = _pageIndex;
            }
            catch (Exception ex)
            {
                _selectionModel = null;
                _selectionModelPage = -1;
                ShowError("提取本页文字失败：" + ex.Message);
                return null;
            }
            finally
            {
                Cursor = previousCursor;
            }

            if (_selectionModel.Chars.Count == 0)
            {
                ShowError("本页没有可选择的文字（可能是扫描图片或纯矢量图）。");
                return null;
            }

            // 之前可能显示了错误提示，这里恢复正常状态栏
            UpdateStatus();

            return _selectionModel;
        }

        /// <summary>初始化红框闪烁定时器（部品基本信息画面选中记录时用，见 <see cref="FlashTextHighlight"/>）。</summary>
        private void InitializeFlashHighlight()
        {
            _flashTimer = new Timer(components);
            _flashTimer.Interval = FlashInterval;
            _flashTimer.Tick += FlashTimer_Tick;
        }

        /// <summary>
        /// 取指定页的文字选择模型（<see cref="EnsureSelectionModel"/> 的静默版）：
        /// 解析失败 / 该页没有文字时返回 <c>null</c>，<b>不写状态栏、不弹提示</b>。
        /// </summary>
        private PdfTextSelectionModel GetPageSelectionModel(int pageIndex)
        {
            if (_textDocument == null)
            {
                return null;
            }

            if (_selectionModel != null && _selectionModelPage == pageIndex)
            {
                return _selectionModel;
            }

            try
            {
                PdfPigPage page = _textDocument.GetPage(pageIndex + 1);
                PdfTextSelectionModel model = PdfTextSelectionModel.Create(page);

                if (model.Chars.Count == 0)
                {
                    return null;
                }

                // 换了页：原来的选择（字符下标）已经失效
                ResetSelection();

                _selectionModel = model;
                _selectionModelPage = pageIndex;

                return model;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 在页面上找 <paramref name="keywords"/> 里第一个出现的文字，返回它的外接矩形（PDF 坐标）。
        /// <para>
        /// 关键字按传入顺序优先 —— 部品基本信息画面传的是「部品図番 → 部品CD（部品コード）→ 部品名」，
        /// 所以<b>部品図番 是第 1 优先</b>（这一行有部品図番时就用它定位）：只要它在页面上出现，
        /// 就不会改用部品CD / 部品名；部品図番 在图上找不到时才退到部品CD。
        /// 每个关键字逐行（竖排时逐列）匹配，不跨行。
        /// </para>
        /// <para>
        /// 同一个关键字在页面上命中<b>多行</b>时按「这一行里还含有其它关键字的个数」打分，取分数最高的那一行
        /// （部品図番 与部品CD / 部品名 同时出现的那一行 → 更像部品表里的那一行，见
        /// <see cref="CountOtherNeedles"/>）；分数相同取最靠前的一行，结果稳定可复现。
        /// </para>
        /// <para>
        /// 每个关键字都先按原样找，找不到再走一遍<b>宽松匹配</b>（见 <see cref="TryMatchFlashPattern"/>）：
        /// 库里的部品CD 常写成 <c>MZA□968</c> / <c>MZB○○963</c>（<c>□</c> <c>○</c> = 可变位），
        /// 图纸上印的却是 <c>MZA1-968</c> 这样的具体值；部品図番 在图上也可能不带 <c>-</c>
        /// （库 <c>HJ-1234</c>，图纸 <c>HJ1234</c>）。宽松匹配把可变位当「任意 0～1 个字符」，
        /// 并容忍字符之间夹 <c>-</c> 之类的分隔符。
        /// 若某个含可变位的模糊 CD 连宽松匹配也没命中，就直接返回 <c>false</c>：
        /// <b>不再往下退到部品名</b> —— 部品名是同一族部品共用的（例如「部品セット（フラットレール）」
        /// 既是 <c>MZA□968</c> 又是 <c>MZB○○963</c>），退过去只会闪到别的部品上。
        /// </para>
        /// </summary>
        private static bool TryFindTextRectangle(PdfTextSelectionModel model, IList<string> keywords,
            out PdfSelectionRectangle rectangle)
        {
            rectangle = new PdfSelectionRectangle(0, 0, 0, 0);

            List<string> needles = new List<string>(keywords.Count);

            foreach (string keyword in keywords)
            {
                string needle = NormalizeFlashKeyword(keyword);

                if (needle.Length >= FlashKeywordMinLength && !needles.Contains(needle))
                {
                    needles.Add(needle);
                }
            }

            if (needles.Count == 0)
            {
                return false;
            }

            IList<PdfSelectableChar> chars = model.Chars;

            // 先把每一行拼成一个字符串，同时记住每个字对应的原始字符下标
            List<string> lineTexts = new List<string>(model.Lines.Count);
            List<List<int>> lineCharIndexes = new List<List<int>>(model.Lines.Count);

            foreach (PdfSelectableLine line in model.Lines)
            {
                int first = line.FirstCharIndex;
                int count = line.Count;

                if (count <= 0 || first < 0 || first + count > chars.Count)
                {
                    continue;
                }

                StringBuilder text = new StringBuilder(count);
                List<int> charIndexes = new List<int>(count);

                for (int offset = 0; offset < count; offset++)
                {
                    int charIndex = first + offset;
                    string value = NormalizeFlashKeyword(chars[charIndex].Text);

                    for (int index = 0; index < value.Length; index++)
                    {
                        text.Append(value[index]);
                        charIndexes.Add(charIndex);
                    }
                }

                lineTexts.Add(text.ToString());
                lineCharIndexes.Add(charIndexes);
            }

            for (int needleIndex = 0; needleIndex < needles.Count; needleIndex++)
            {
                string needle = needles[needleIndex];

                // 每个关键字都先按原样找一遍，找不到再用宽松匹配找一遍：
                // 库里的部品CD（MZA□968）和图纸上（MZA1-968）写法常不一样，
                // 部品図番 在图上也可能少了 '-'（库 HJ-1234，图纸 HJ1234）。
                for (int pass = 0; pass < 2; pass++)
                {
                    bool useFuzzy = pass == 1;
                    int bestLine = -1;
                    int bestStart = -1;
                    int bestLength = needle.Length;
                    int bestScore = -1;

                    for (int lineIndex = 0; lineIndex < lineTexts.Count; lineIndex++)
                    {
                        string lineText = lineTexts[lineIndex];
                        int start;
                        int length;

                        if (useFuzzy)
                        {
                            if (!TryFindFlashPatternInLine(lineText, needle, out start, out length))
                            {
                                continue;
                            }
                        }
                        else
                        {
                            start = lineText.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                            length = needle.Length;

                            if (start < 0)
                            {
                                continue;
                            }
                        }

                        // 同一行里还含有别的关键字（部品図番 + 部品CD + 部品名 之类）越多 → 越像部品表里的一行。
                        // 不能「碰到第一个含别的关键字的行就用」：同一页上关键字可能出现好几次，
                        // 后面那一行往往才是同时印着部品CD / 部品名 的部品表行，所以这里按分数取最优。
                        int score = CountOtherNeedles(lineText, needles, needle);

                        if (score > bestScore)
                        {
                            bestLine = lineIndex;
                            bestStart = start;
                            bestLength = length;
                            bestScore = score;

                            // 含全部关键字 = 最高分，不会再更好了
                            if (score >= needles.Count - 1)
                            {
                                break;
                            }
                        }
                    }

                    if (bestLine < 0)
                    {
                        continue;
                    }

                    rectangle = GetCharsRectangle(chars, lineCharIndexes[bestLine], bestStart, bestLength);

                    return true;
                }

                if (ContainsFlashWildcard(needle))
                {
                    // 这个关键字本身是模糊 CD（含 □ / ○ = 可变位）而图纸上没找到对应具体值：
                    // 再往下退到部品名只会命中同一族的别的部品（部品名共用的多行）→ 宁可不闪
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// 这一行里还含有<b>其它</b>关键字的<b>个数</b>（用来在同一个关键字的多个命中行里挑最像部品表那一行的）：
        /// 部品図番 命中多处时，同时印着部品CD / 部品名 的那一行得分更高。
        /// </summary>
        private static int CountOtherNeedles(string text, IList<string> needles, string current)
        {
            int count = 0;

            for (int index = 0; index < needles.Count; index++)
            {
                string other = needles[index];

                if (string.Equals(other, current, StringComparison.Ordinal))
                {
                    continue;
                }

                if (text.IndexOf(other, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// 部品CD 里的<b>可变位</b>记号：库里的部品CD 常写成 <c>MZA□968</c> / <c>MZB○○963</c>
        /// （<c>□</c> <c>○</c> = 任意 1 个字符），图纸上印的却是 <c>MZA1-968</c> / <c>MZB2-963</c>
        /// 这样的具体值，所以匹配时要当通配符处理。
        /// </summary>
        private static bool IsFlashWildcard(char value)
        {
            return value == '□' || value == '○' || value == '〇' || value == '◯' || value == '△';
        }

        /// <summary>部品CD 里是否含有可变位记号（见 <see cref="IsFlashWildcard"/>）。</summary>
        private static bool ContainsFlashWildcard(string value)
        {
            for (int index = 0; index < value.Length; index++)
            {
                if (IsFlashWildcard(value[index]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>在一行文字里用宽松规则找 <paramref name="pattern"/>，返回最靠前的那一处。</summary>
        private static bool TryFindFlashPatternInLine(string text, string pattern, out int start, out int length)
        {
            for (int index = 0; index < text.Length; index++)
            {
                if (TryMatchFlashPattern(text, index, pattern, out length))
                {
                    start = index;

                    return true;
                }
            }

            start = -1;
            length = 0;

            return false;
        }

        /// <summary>
        /// 从 <paramref name="start"/> 起宽松匹配 <paramref name="pattern"/>（部品CD / 部品図番 用）：
        /// <list type="bullet">
        ///   <item>可变位记号（<c>□</c> <c>○</c> <c>〇</c> <c>◯</c> <c>△</c>）匹配 <b>0 或 1 个字符</b>
        ///     （图纸上印成具体字符，也可能不印）；</item>
        ///   <item>其余字符必须一致（不分大小写），但两者之间允许夹<b>分隔符</b>
        ///     （<c>-</c> <c>_</c> <c>/</c> <c>・</c> 等，最多连续 <see cref="FlashMaxSeparatorSkip"/> 个）；</item>
        ///   <item><b>关键字里自带的分隔符也可以缺省</b>：库 <c>HJ-1234</c> 能命中图纸上的 <c>HJ1234</c>；</item>
        ///   <item>匹配长度不能超过「关键字长度 + <see cref="FlashMaxExtraLength"/>」，
        ///     免得一路跳过分隔符把旁边的文字也框进红框里。</item>
        /// </list>
        /// </summary>
        private static bool TryMatchFlashPattern(string text, int start, string pattern, out int length)
        {
            length = 0;

            if (pattern.Length == 0 || start < 0 || start >= text.Length)
            {
                return false;
            }

            // 起点必须是真字符（除非关键字本身以可变位 / 分隔符开头），这样红框不会从分隔符起画
            if (char.IsLetterOrDigit(pattern[0]) && !char.IsLetterOrDigit(text[start]))
            {
                return false;
            }

            int end;

            if (!TryMatchFlashPatternAt(text, start, pattern, 0, out end))
            {
                return false;
            }

            length = end - start;

            return length > 0 && length <= pattern.Length + FlashMaxExtraLength;
        }

        /// <summary><see cref="TryMatchFlashPattern"/> 的回溯部分（<paramref name="patternIndex"/> 之前的都已匹配）。</summary>
        private static bool TryMatchFlashPatternAt(string text, int textIndex, string pattern, int patternIndex,
            out int end)
        {
            end = textIndex;

            if (patternIndex >= pattern.Length)
            {
                end = textIndex;

                return true;
            }

            char patternChar = pattern[patternIndex];

            if (IsFlashWildcard(patternChar))
            {
                // 可变位前面可能先夹着分隔符（MZA-1-968 里的第一个 '-'）：先跳过去，
                // 否则可变位会把 '-' 当成那「1 个字符」吃掉，后面的数字就对不上了
                int wildcardIndex = textIndex;
                int wildcardSkipped = 0;

                while (wildcardIndex < text.Length && wildcardSkipped < FlashMaxSeparatorSkip &&
                    !char.IsLetterOrDigit(text[wildcardIndex]))
                {
                    wildcardIndex++;
                    wildcardSkipped++;
                }

                // 可变位：图纸上可能印着 1 个字符…
                if (wildcardIndex < text.Length && char.IsLetterOrDigit(text[wildcardIndex]) &&
                    TryMatchFlashPatternAt(text, wildcardIndex + 1, pattern, patternIndex + 1, out end))
                {
                    return true;
                }

                // …也可能没印（0 个字符）
                return TryMatchFlashPatternAt(text, wildcardIndex, pattern, patternIndex + 1, out end);
            }

            int index = textIndex;
            int skipped = 0;

            // 字符之间夹的分隔符跳过；分隔符本身就是关键字的字符时（例如 MZA-968 里的 '-'）不跳
            while (index < text.Length && skipped < FlashMaxSeparatorSkip &&
                !char.IsLetterOrDigit(text[index]) &&
                char.ToUpperInvariant(text[index]) != char.ToUpperInvariant(patternChar))
            {
                index++;
                skipped++;
            }

            if (index < text.Length &&
                char.ToUpperInvariant(text[index]) == char.ToUpperInvariant(patternChar))
            {
                return TryMatchFlashPatternAt(text, index + 1, pattern, patternIndex + 1, out end);
            }

            // 关键字里的分隔符（'-' '_' '/' '・' 之类）图纸上可能没印：
            // 允许「不消耗文字、直接跳过这个关键字字符」，这样库 HJ-1234 能命中图纸 HJ1234。
            // 每次递归都往后一个 patternIndex，不会再回到同一状态，不会死循环。
            if (!char.IsLetterOrDigit(patternChar))
            {
                return TryMatchFlashPatternAt(text, index, pattern, patternIndex + 1, out end);
            }

            return false;
        }

        /// <summary>
        /// 把「行内归一化字符串」的一段换算成原始字符的外接矩形
        /// （四周各留 1 点，红框不会紧贴着字）。
        /// </summary>
        private static PdfSelectionRectangle GetCharsRectangle(IList<PdfSelectableChar> chars,
            IList<int> charIndexes, int start, int length)
        {
            double left = double.MaxValue;
            double top = double.MaxValue;
            double right = double.MinValue;
            double bottom = double.MinValue;

            int first = Math.Max(0, start);
            int last = Math.Min(charIndexes.Count - 1, start + length - 1);

            for (int index = first; index <= last; index++)
            {
                PdfSelectableChar item = chars[charIndexes[index]];

                left = Math.Min(left, item.Left);
                top = Math.Min(top, item.Top);
                right = Math.Max(right, item.Right);
                bottom = Math.Max(bottom, item.Bottom);
            }

            if (left > right || top > bottom)
            {
                return new PdfSelectionRectangle(0, 0, 0, 0);
            }

            const double padding = 1.0;

            return new PdfSelectionRectangle(
                left - padding, top - padding, right + padding, bottom + padding);
        }

        /// <summary>
        /// 闪烁定位用的文字归一化：NFKC 兼容性归一化（全角英数 → 半角、半角片假名 → 全角）
        /// ＋ 去掉全部空白，让「部品コード」之类在两边的写法差异不影响匹配。
        /// </summary>
        private static string NormalizeFlashKeyword(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            // 单个 ASCII 字符（PDF 里的英数 / 记号的绝大多数）本来就是归一化形式，直接返回，
            // 省掉逐字 Normalize 的开销（每选一行都要把整页文字拼一遍）
            if (value.Length == 1 && value[0] < 0x80 && !char.IsWhiteSpace(value[0]))
            {
                return value;
            }

            string normalized = JapaneseTextConverter.NormalizeWidth(value);

            if (string.IsNullOrEmpty(normalized))
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(normalized.Length);

            for (int index = 0; index < normalized.Length; index++)
            {
                if (!char.IsWhiteSpace(normalized[index]))
                {
                    builder.Append(normalized[index]);
                }
            }

            return builder.ToString();
        }

        /// <summary>开始闪烁红框（亮 / 灭各 3 次），并把画面滚到能看见它的位置。</summary>
        private void ShowFlashRectangle(PdfSelectionRectangle rectangle)
        {
            _flashRectangle = rectangle;
            _flashPage = _pageIndex;
            _flashVisible = true;
            _flashTogglesLeft = FlashToggleCount;

            if (_flashTimer != null)
            {
                _flashTimer.Stop();
                _flashTimer.Start();
            }

            ScrollToFlashRectangle(rectangle);
            picPage.Invalidate();
        }

        /// <summary>闪烁定时器：翻转红框的显示 / 隐藏，3 次后清除。</summary>
        private void FlashTimer_Tick(object sender, EventArgs e)
        {
            _flashTogglesLeft--;

            if (_flashTogglesLeft <= 0)
            {
                ClearFlashHighlight();
                return;
            }

            _flashVisible = !_flashVisible;
            picPage.Invalidate();
        }

        /// <summary>停止闪烁并清除红框。</summary>
        private void ClearFlashHighlight()
        {
            if (_flashTimer != null)
            {
                _flashTimer.Stop();
            }

            _flashTogglesLeft = 0;
            _flashVisible = false;
            _flashRectangle = null;
            _flashPage = -1;

            if (!IsDisposed && picPage != null && picPage.IsHandleCreated)
            {
                picPage.Invalidate();
            }
        }

        /// <summary>
        /// 把画面滚到红框位置（尽量把它放在可见区域的中央；不需要滚动时不动）。
        /// 计算方式与 Ctrl + 滚轮缩放一致：<c>picPage.Left / Top</c> 已经包含 AutoScroll 的位移。
        /// </summary>
        private void ScrollToFlashRectangle(PdfSelectionRectangle rectangle)
        {
            if (picPage.Image == null || _zoom <= 0F)
            {
                return;
            }

            Point scroll = GetCanvasScrollOffset();

            // 页面左上角在「内容坐标」中的位置 = 显示位置 + 滚动偏移
            int contentLeft = picPage.Left + scroll.X;
            int contentTop = picPage.Top + scroll.Y;

            int centerX = (int)Math.Round(contentLeft + (rectangle.Left + rectangle.Width / 2.0) * _zoom);
            int centerY = (int)Math.Round(contentTop + (rectangle.Top + rectangle.Height / 2.0) * _zoom);

            pnlCanvas.AutoScrollPosition = new Point(
                Math.Max(0, centerX - pnlCanvas.ClientSize.Width / 2),
                Math.Max(0, centerY - pnlCanvas.ClientSize.Height / 2));
        }

        /// <summary>在页面位图上画闪烁红框（红色 2.5 像素外框 + 淡红填充）。</summary>
        private void DrawFlashHighlight(Graphics graphics)
        {
            if (!_flashVisible || !_flashRectangle.HasValue || _flashPage != _pageIndex)
            {
                return;
            }

            RectangleF rectangle = ToPixelRectangle(_flashRectangle.Value, _zoom);

            rectangle.Inflate(2F, 2F);

            using (SolidBrush fill = new SolidBrush(Color.FromArgb(45, 230, 30, 30)))
            using (Pen pen = new Pen(Color.FromArgb(255, 220, 20, 20), 2.5F))
            {
                graphics.FillRectangle(fill, rectangle);
                graphics.DrawRectangle(pen, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
            }
        }

        /// <summary>把鼠标位置换算成页面 PDF 坐标（左上角原点、单位 PDF 点）。</summary>
        private bool TryGetPdfPoint(MouseEventArgs e, out double x, out double y)
        {
            x = 0.0;
            y = 0.0;

            if (_zoom <= 0F || picPage.Image == null)
            {
                return false;
            }

            x = e.X / (double)_zoom;
            y = e.Y / (double)_zoom;

            return true;
        }

        /// <summary>复制选中文字到剪贴板（Ctrl+C / 右键菜单）。</summary>
        private void CopySelection()
        {
            string text = _selectionModel == null ? string.Empty : _selectionModel.GetText(GetSelectionRange());

            if (string.IsNullOrEmpty(text))
            {
                ShowError("没有选中文字：请先在页面上拖拽选择一段文字。");
                return;
            }

            try
            {
                Clipboard.SetText(text);
                lblStatus.ForeColor = Color.FromArgb(52, 73, 94);
                UiLocalization.SetText(lblStatus, "已复制 " + text.Length + " 个字符到剪贴板。");
            }
            catch (Exception ex)
            {
                ShowError("复制到剪贴板失败：" + ex.Message);
            }
        }

        /// <summary>全选本页文字（Ctrl+A）。</summary>
        private void SelectAllText()
        {
            PdfTextSelectionModel model = EnsureSelectionModel();

            if (model == null)
            {
                return;
            }

            ApplySelection(model.SelectAll());
        }

        /// <summary>右键菜单：复制选中文字。</summary>
        private void MiCopySelection_Click(object sender, EventArgs e)
        {
            CopySelection();
        }

        /// <summary>右键菜单：全选本页文字。</summary>
        private void MiSelectAll_Click(object sender, EventArgs e)
        {
            SelectAllText();
        }

        /// <summary>右键菜单：清除选择。</summary>
        private void MiClearSelection_Click(object sender, EventArgs e)
        {
            ResetSelection();
            picPage.Invalidate();
            UpdateStatus();
        }

        /// <summary>统一工具栏按钮外观（与主窗口风格保持一致）。</summary>
        private void ApplyButtonStyle()
        {
            foreach (Control control in pnlToolbar.Controls)
            {
                Button button = control as Button;

                if (button == null)
                {
                    continue;
                }

                button.BackColor = Color.FromArgb(232, 237, 243);
                button.ForeColor = Color.FromArgb(52, 73, 94);
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Color.FromArgb(199, 208, 219);
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(217, 226, 236);
                button.Cursor = Cursors.Hand;
                button.UseVisualStyleBackColor = false;
            }
        }

        /// <summary>Windows 消息：鼠标滚轮（Ctrl + 滚轮缩放、滚轮滚动画布）。</summary>
        private const int WmMouseWheel = 0x020A;

        /// <summary>
        /// 兜底处理发给本窗体的滚轮消息（正常情况下已由 <see cref="CanvasWheelMessageFilter"/> 先处理）。
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmMouseWheel && HandleCanvasWheel(ref m))
            {
                return;
            }

            base.WndProc(ref m);
        }

        /// <summary>
        /// 进程级鼠标滚轮过滤器：WM_MOUSEWHEEL 只会发给有焦点的控件，
        /// 用过滤器才能保证「鼠标在预览画面上时」Ctrl + 滚轮缩放与滚轮滚动始终生效。
        /// </summary>
        private sealed class CanvasWheelMessageFilter : IMessageFilter
        {
            private readonly PdfPreviewForm _owner;

            public CanvasWheelMessageFilter(PdfPreviewForm owner)
            {
                _owner = owner;
            }

            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != WmMouseWheel)
                {
                    return false;
                }

                return _owner.HandleCanvasWheel(ref m);
            }
        }

        /// <summary>窗口关闭时释放 pdfium 文档、PdfPig 文档与位图，避免文件被占用。</summary>
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            resizeTimer.Stop();

            if (_flashTimer != null)
            {
                _flashTimer.Stop();
            }

            if (_wheelFilter != null)
            {
                Application.RemoveMessageFilter(_wheelFilter);
                _wheelFilter = null;
            }

            CloseDocument();

            if (_selectionMenu != null)
            {
                _selectionMenu.Dispose();
                _selectionMenu = null;
            }

            base.OnFormClosed(e);
        }
    }
}
