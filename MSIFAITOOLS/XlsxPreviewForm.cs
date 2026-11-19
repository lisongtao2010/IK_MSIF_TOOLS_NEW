// ─────────────────────────────────────────────────────────────────────────────
// 【已停用】Excel 预览窗口 —— 已从 MSIFAITOOLS.csproj 的 <Compile> 列表移除，不参与编译。
//
// 现在【分类结果】里的 Excel 行（.xlsx / .xlsm / .xlsb / .xls）不再用本工具的预览窗口显示：
// 双击 / Enter /【预览】/ 右键「预览」时直接用本机 Excel 打开该工作簿，并把它放到最前
// （已经打开时只激活那个窗口，不重复打开）—— 见 Services\ExcelLauncher.cs。
//
// 需要恢复「只读表格 + 图片叠加 + PDF 视图」的旧做法时，把本文件与
// Services\ExcelPdfConverter.cs 重新加回 csproj 的 <Compile> 列表，并在 Form1 里恢复调用
// ShowExcel(path, bringToFront) 即可。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using ExcelDataReader;
using MSIFAITOOLS.Services;
using PdfiumViewer;

namespace MSIFAITOOLS
{
    /// <summary>
    /// Excel 预览窗口：在【分类结果】一览里选中 Excel 行时，用只读表格显示它的内容。
    /// <para>
    /// 用 <c>ExcelDataReader</c>（工程已引用，<c>Services\XlsxSheetReader</c> 也用它）读取
    /// <c>.xlsx</c> / <c>.xlsm</c> / <c>.xlsb</c> / <c>.xls</c>，一个工作表一页（左上角下拉框切换）。
    /// 表格按 Excel 原样显示（行号从 1 开始、列头是 A / B / C …），不做合并单元格 / 公式 / 颜色还原 ——
    /// 这里只用于「快速看一眼内容」，要看格式请点【用 Excel 打开】。
    /// </para>
    /// <para>
    /// 与 <see cref="PdfPreviewForm"/> 一样是非模态、<b>不抢焦点</b>显示（<see cref="ShowWithoutActivation"/>）：
    /// 「选中即预览」用方向键换行时焦点留在主画面的列表里，需要前置时由主画面显式 Activate。
    /// 文件以共享方式（<see cref="FileShare.ReadWrite"/>）只读打开，预览期间别的程序仍可保存该文件。
    /// </para>
    /// </summary>
    public sealed class XlsxPreviewForm : Form
    {
        /// <summary>一次最多显示的行数（Excel 可能有几万行，全塞进 DataGridView 会把界面卡住）。</summary>
        private const int MaxPreviewRows = 2000;

        /// <summary>
        /// 一次最多显示的列数（A ~ BL）。上限压得比较保守：
        /// 行数 × 列数 最多 2000 × 64 = 12.8 万个单元格，DataGridView 才不会卡。
        /// </summary>
        private const int MaxPreviewColumns = 64;

        /// <summary>PDF 视图里页面四周的留白（像素）。</summary>
        private const int PdfPageMargin = 8;

        /// <summary>PDF 单页渲染的最大像素数（放大过头时按比例收缩，避免一次申请过大的位图）。</summary>
        private const long MaxPdfRenderPixels = 25000000L;

        /// <summary>PDF 视图的最小缩放（1F ≈ 页面的原始像素尺寸）。</summary>
        private const float MinPdfScale = 0.1F;

        /// <summary>PDF 视图的最大缩放。</summary>
        private const float MaxPdfScale = 6F;

        /// <summary>状态栏正常文字颜色。</summary>
        private static readonly Color StatusOkColor = Color.FromArgb(46, 125, 50);

        /// <summary>状态栏错误文字颜色。</summary>
        private static readonly Color StatusErrorColor = Color.FromArgb(198, 40, 40);

        /// <summary>状态栏一般说明颜色。</summary>
        private static readonly Color StatusInfoColor = Color.FromArgb(52, 73, 94);

        /// <summary>当前显示的 Excel 完整路径（未加载时为 <c>null</c>）。</summary>
        private string _currentPath;

        /// <summary>工作簿里的工作表名（顺序与 Excel 里一致）。</summary>
        private readonly List<string> _sheetNames = new List<string>();

        /// <summary>当前显示的工作表索引（0 起）。</summary>
        private int _sheetIndex;

        /// <summary>程序正在重填工作表下拉框（此时不要触发「换表」）。</summary>
        private bool _updatingSheetList;

        private Panel _pnlToolbar;

        private Label _lblSheet;

        private ComboBox _cboSheet;

        private Button _btnRefresh;

        private Button _btnOpenExternal;

        private Button _btnClose;

        private DataGridView _grid;

        private Label _lblStatus;

        /// <summary>
        /// 工作簿里的全部图片（.xlsx / .xlsm 一次性取出，按工作表过滤后显示）。
        /// 图片不在单元格数据里（ExcelDataReader 只读单元格的值），见 <see cref="XlsxImageCatalog"/>。
        /// </summary>
        private readonly List<XlsxImageInfo> _images = new List<XlsxImageInfo>();

        /// <summary>当前工作表上正在显示的图片控件（换表 / 刷新 / 关闭时重建）。</summary>
        private readonly List<ImageOverlay> _overlays = new List<ImageOverlay>();

        /// <summary>【显示图片】勾选框：图片会盖住下面的单元格，只想看文字时可以临时关掉。</summary>
        private CheckBox _chkImages;

        /// <summary>
        /// 这个工作簿里是否有 Excel 365「图片放在单元格中」的数据（这种图片预览不显示，
        /// 状态栏会提示用【用 Excel 打开】）。
        /// </summary>
        private bool _hasInCellImages;

        /// <summary>
        /// 【PDF 视图】按钮：用本机 Excel 把工作簿导出成 PDF 后原样显示（保真度最高）。
        /// 没装 Excel / 转换失败时留在表格视图。
        /// </summary>
        private Button _btnViewPdf;

        /// <summary>【表格视图】按钮：回到 <c>ExcelDataReader</c> 读出来的只读表格。</summary>
        private Button _btnViewGrid;

        /// <summary>PDF 视图的容器（自动滚动：页面比窗口大时可上下滚）。</summary>
        private Panel _pnlPdf;

        /// <summary>显示 PDF 当前页位图的控件。</summary>
        private PictureBox _picPdf;

        /// <summary>PDF 视图专用的翻页 / 缩放工具条（只在 PDF 视图显示）。</summary>
        private Panel _pnlPdfBar;

        /// <summary>【上一页】。</summary>
        private Button _btnPdfPrev;

        /// <summary>【下一页】。</summary>
        private Button _btnPdfNext;

        /// <summary>页码（形如 <c>2 / 5</c>，纯数字所以不用翻译）。</summary>
        private Label _lblPdfPage;

        /// <summary>【放大】。</summary>
        private Button _btnPdfZoomIn;

        /// <summary>【缩小】。</summary>
        private Button _btnPdfZoomOut;

        /// <summary>【适合宽度】。</summary>
        private Button _btnPdfFitWidth;

        /// <summary>当前加载的缓存 PDF（未加载时为 <c>null</c>）。</summary>
        private PdfDocument _pdfDocument;

        /// <summary>缓存 PDF 对应的 Excel 文件（换文件时用来判断要不要重新转换）。</summary>
        private string _pdfSourcePath;

        /// <summary>PDF 当前页索引（0 起）。</summary>
        private int _pdfPageIndex;

        /// <summary>PDF 是否按窗口宽度自适应（true = 忽略 <see cref="_pdfScale"/>，按宽度重算）。</summary>
        private bool _pdfFitWidth = true;

        /// <summary>非自适应时的缩放倍数（1F ≈ 页面原始像素尺寸）。</summary>
        private float _pdfScale = 1F;

        /// <summary>当前是否显示 PDF 视图。</summary>
        private bool _pdfViewActive;

        /// <summary>这个 Excel 是否已经尝试过转换 PDF（避免每次换行都重复失败）。</summary>
        private bool _pdfTried;

        /// <summary>上次转换 / 加载 PDF 失败的原因（状态栏用；成功后为 <c>null</c>）。</summary>
        private string _pdfError;

        /// <summary>上次渲染 PDF 时的可视宽度（拖动窗口时避免每几个像素就重渲染一次）。</summary>
        private int _lastPdfRenderWidth;

        public XlsxPreviewForm()
        {
            InitializeUi();

            // 按钮 / 标签的中日切换（见 Services\UiLocalization.cs）：控件建好之后挂接一次即可
            UiLocalization.Attach(this);
        }

        /// <summary>
        /// 显示窗口时不抢焦点：预览是辅助窗口，不打断主画面 / 部品基本信息画面的键盘操作
        /// （「选中即预览」用方向键移动行时焦点留在列表里）。
        /// 需要把预览提到最前时由调用方显式 BringToFront() + Activate()。
        /// </summary>
        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        /// <summary>当前显示的 Excel 完整路径（未加载时为 <c>null</c>）。</summary>
        public string CurrentPath
        {
            get { return _currentPath; }
        }

        /// <summary>
        /// 显示指定的 Excel 文件。与当前显示的已是同一文件时只把窗口前置（保持当前工作表）。
        /// 文件不存在 / 读不了时在状态栏提示（用户主动打开时再弹一次对话框），不抛异常。
        /// </summary>
        /// <param name="excelFilePath">Excel 文件完整路径。</param>
        /// <param name="activate">
        /// 已是同一文件时是否激活（提到最前）本窗口；
        /// 「选中即预览」传 false，避免预览画面一直跳到主画面前面。
        /// </param>
        public void ShowExcel(string excelFilePath, bool activate = true)
        {
            if (string.IsNullOrWhiteSpace(excelFilePath))
            {
                return;
            }

            if (!File.Exists(excelFilePath))
            {
                SetStatus(UiText.F("文件不存在：{0}", excelFilePath), StatusErrorColor);
                return;
            }

            if (string.Equals(_currentPath, excelFilePath, StringComparison.OrdinalIgnoreCase) &&
                _sheetNames.Count > 0)
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

            LoadWorkbook(excelFilePath, activate);
        }

        /// <summary>Esc = 关闭预览窗口（与 PDF 预览窗口的习惯一致）；PDF 视图里 PageUp / PageDown = 翻页。</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                Close();
                return true;
            }

            if (_pdfViewActive)
            {
                if (keyData == Keys.PageDown)
                {
                    GoToPdfPage(_pdfPageIndex + 1);
                    return true;
                }

                if (keyData == Keys.PageUp)
                {
                    GoToPdfPage(_pdfPageIndex - 1);
                    return true;
                }
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>
        /// 用代码建界面（本窗口没有 Designer：内容随文件变化，用代码建控件反而更清楚）：
        /// 上部工具条（工作表下拉 / 刷新 / 用 Excel 打开 / 关闭）、中间只读表格、底部状态栏。
        /// </summary>
        private void InitializeUi()
        {
            Text = "Excel 预览";
            StartPosition = FormStartPosition.Manual;
            MinimumSize = new Size(720, 480);
            ClientSize = new Size(1084, 700);
            BackColor = Color.FromArgb(245, 247, 250);
            Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular, GraphicsUnit.Point, ((byte)(134)));

            _grid = new DataGridView();
            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToOrderColumns = false;
            _grid.ReadOnly = true;
            _grid.RowHeadersVisible = true;
            _grid.RowHeadersWidth = 56;
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText;
            _grid.BackgroundColor = Color.White;
            _grid.BorderStyle = BorderStyle.None;
            _grid.EnableHeadersVisualStyles = false;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(221, 230, 242);
            _grid.ColumnHeadersDefaultCellStyle.Font =
                new Font("Microsoft YaHei", 9F, FontStyle.Bold, GraphicsUnit.Point, ((byte)(134)));
            _grid.RowHeadersDefaultCellStyle.BackColor = Color.FromArgb(235, 240, 246);
            _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 251, 253);
            _grid.DataError += DgvPreview_DataError;

            // 图片是叠在表格上的子控件：滚动 / 改变大小 / 改列宽时要重新算位置
            _grid.Scroll += Grid_Scroll;
            _grid.SizeChanged += Grid_SizeChanged;
            _grid.ColumnWidthChanged += Grid_ColumnWidthChanged;

            _lblStatus = new Label();
            _lblStatus.Dock = DockStyle.Bottom;
            _lblStatus.Height = 26;
            _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            _lblStatus.Padding = new Padding(8, 0, 8, 0);
            _lblStatus.BackColor = Color.FromArgb(236, 240, 245);
            _lblStatus.ForeColor = StatusInfoColor;
            _lblStatus.Text = "尚未选择文件";

            _pnlToolbar = new Panel();
            _pnlToolbar.Dock = DockStyle.Top;
            _pnlToolbar.Height = 46;
            _pnlToolbar.BackColor = Color.FromArgb(242, 244, 247);

            _lblSheet = new Label();
            _lblSheet.AutoSize = true;
            _lblSheet.Location = new Point(10, 16);
            _lblSheet.Text = "工作表：";

            _cboSheet = new ComboBox();
            _cboSheet.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboSheet.Location = new Point(72, 11);
            _cboSheet.Size = new Size(260, 25);
            _cboSheet.SelectedIndexChanged += CboSheet_SelectedIndexChanged;

            _btnRefresh = CreateToolbarButton("刷新", new Point(344, 9), 70);
            _btnRefresh.Click += BtnRefresh_Click;

            _btnOpenExternal = CreateToolbarButton("用 Excel 打开", new Point(420, 9), 130);
            _btnOpenExternal.Click += BtnOpenExternal_Click;

            // 视图切换：表格（ExcelDataReader，快）／ PDF（Excel 导出后原样显示，保真）
            _btnViewPdf = CreateToolbarButton("PDF 视图", new Point(560, 9), 90);
            _btnViewPdf.Click += BtnViewPdf_Click;

            _btnViewGrid = CreateToolbarButton("表格视图", new Point(656, 9), 90);
            _btnViewGrid.Enabled = false;
            _btnViewGrid.Click += BtnViewGrid_Click;

            _btnClose = CreateToolbarButton("关闭", new Point(988, 9), 90);
            _btnClose.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnClose.Click += BtnClose_Click;

            _chkImages = new CheckBox();
            _chkImages.Text = "显示图片";
            _chkImages.AutoSize = true;
            _chkImages.Checked = true;
            _chkImages.Location = new Point(756, 15);
            _chkImages.CheckedChanged += ChkImages_CheckedChanged;

            _pnlToolbar.Controls.Add(_lblSheet);
            _pnlToolbar.Controls.Add(_cboSheet);
            _pnlToolbar.Controls.Add(_btnRefresh);
            _pnlToolbar.Controls.Add(_btnOpenExternal);
            _pnlToolbar.Controls.Add(_btnViewPdf);
            _pnlToolbar.Controls.Add(_btnViewGrid);
            _pnlToolbar.Controls.Add(_chkImages);
            _pnlToolbar.Controls.Add(_btnClose);

            // PDF 视图：容器（可滚动）+ 页面位图；内容在【PDF 视图】时才有
            _picPdf = new PictureBox();
            _picPdf.Location = new Point(PdfPageMargin, PdfPageMargin);
            _picPdf.SizeMode = PictureBoxSizeMode.Normal;
            _picPdf.BackColor = Color.White;

            _pnlPdf = new Panel();
            _pnlPdf.Dock = DockStyle.Fill;
            _pnlPdf.AutoScroll = true;
            _pnlPdf.BackColor = Color.FromArgb(120, 124, 130);
            _pnlPdf.Visible = false;
            _pnlPdf.Controls.Add(_picPdf);
            _pnlPdf.Resize += PnlPdf_Resize;

            // PDF 视图专用的翻页 / 缩放条（表格视图时隐藏）
            _btnPdfPrev = CreateToolbarButton("上一页", new Point(8, 4), 80);
            _btnPdfPrev.Click += BtnPdfPrev_Click;

            _lblPdfPage = new Label();
            _lblPdfPage.AutoSize = true;
            _lblPdfPage.Location = new Point(96, 10);
            _lblPdfPage.Text = "1 / 1";

            _btnPdfNext = CreateToolbarButton("下一页", new Point(200, 4), 80);
            _btnPdfNext.Click += BtnPdfNext_Click;

            _btnPdfZoomOut = CreateToolbarButton("缩小", new Point(292, 4), 60);
            _btnPdfZoomOut.Click += BtnPdfZoomOut_Click;

            _btnPdfZoomIn = CreateToolbarButton("放大", new Point(358, 4), 60);
            _btnPdfZoomIn.Click += BtnPdfZoomIn_Click;

            _btnPdfFitWidth = CreateToolbarButton("适合宽度", new Point(424, 4), 100);
            _btnPdfFitWidth.Click += BtnPdfFitWidth_Click;

            _pnlPdfBar = new Panel();
            _pnlPdfBar.Dock = DockStyle.Top;
            _pnlPdfBar.Height = 36;
            _pnlPdfBar.BackColor = Color.FromArgb(236, 240, 245);
            _pnlPdfBar.Visible = false;
            _pnlPdfBar.Controls.Add(_btnPdfPrev);
            _pnlPdfBar.Controls.Add(_lblPdfPage);
            _pnlPdfBar.Controls.Add(_btnPdfNext);
            _pnlPdfBar.Controls.Add(_btnPdfZoomOut);
            _pnlPdfBar.Controls.Add(_btnPdfZoomIn);
            _pnlPdfBar.Controls.Add(_btnPdfFitWidth);

            // 先加 Fill，再加 Top / Bottom：停靠时后加入的控件先占位，表格才会拿到剩下的整块区域
            // （_grid 与 _pnlPdf 都是 Fill，同一时刻只显示一个）
            Controls.Add(_grid);
            Controls.Add(_pnlPdf);
            Controls.Add(_lblStatus);
            Controls.Add(_pnlPdfBar);
            Controls.Add(_pnlToolbar);
        }

        /// <summary>建一个与 PDF 预览窗口风格一致的工具条按钮。</summary>
        private static Button CreateToolbarButton(string text, Point location, int width)
        {
            Button button = new Button();
            button.Text = text;
            button.Location = location;
            button.Size = new Size(width, 28);
            button.UseVisualStyleBackColor = true;

            return button;
        }

        /// <summary>表格取值异常时静默处理，避免弹出系统错误对话框。</summary>
        private void DgvPreview_DataError(object sender, DataGridViewDataErrorEventArgs e)
        {
            e.ThrowException = false;
        }

        /// <summary>换工作表（下拉框选中项变化）。</summary>
        private void CboSheet_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_updatingSheetList || _cboSheet == null || _cboSheet.SelectedIndex < 0)
            {
                return;
            }

            int index = _cboSheet.SelectedIndex;

            if (index == _sheetIndex)
            {
                return;
            }

            LoadSheet(index);
        }

        /// <summary>【刷新】：重新读取当前工作表（别的程序改了内容后用）。</summary>
        private void BtnRefresh_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_currentPath))
            {
                return;
            }

            // 图片也一起重取：别的程序可能既改了单元格，又加了 / 换了图片
            _images.Clear();
            _images.AddRange(XlsxImageCatalog.ReadImages(_currentPath));
            _hasInCellImages = XlsxImageCatalog.HasInCellImages(_currentPath);

            // 缓存 PDF 也可能过期了：先释放（pdfium 还拿着文件时删不掉缓存），再删缓存
            bool wasPdfView = _pdfViewActive;

            ClosePdfDocument();
            ExcelPdfConverter.DeleteCachedPdf(_currentPath);

            _pdfTried = false;
            _pdfError = null;

            SetPdfViewActive(false);

            LoadSheet(_sheetIndex);

            if (wasPdfView)
            {
                // 正在看 PDF：重新转换一次（别的程序可能刚改过内容）
                if (EnsurePdfDocument())
                {
                    SetPdfViewActive(true);
                }
                else
                {
                    SetStatus(_pdfError, StatusErrorColor);
                }
            }
        }

        /// <summary>【用 Excel 打开】：交给系统默认程序（要看格式 / 编辑时用）。</summary>
        private void BtnOpenExternal_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_currentPath) || !File.Exists(_currentPath))
            {
                SetStatus(UiText.T("请先选中一个 Excel 文件"), StatusErrorColor);
                return;
            }

            try
            {
                Process.Start(_currentPath);

                SetStatus(UiText.F("已用系统默认程序打开：{0}", Path.GetFileName(_currentPath)),
                    StatusOkColor);
            }
            catch (Exception ex)
            {
                SetStatus(UiText.F("打开失败：{0}", ex.Message), StatusErrorColor);
            }
        }

        /// <summary>【关闭】。</summary>
        private void BtnClose_Click(object sender, EventArgs e)
        {
            Close();
        }

        /// <summary>
        /// 读工作簿：先取全部工作表名，再显示第 1 张。
        /// 失败时清空表格并在状态栏（用户主动打开时再弹一次对话框）提示原因，不抛异常。
        /// </summary>
        /// <param name="excelFilePath">Excel 完整路径。</param>
        /// <param name="showErrorDialog">失败时是否弹对话框（「选中即预览」传 false，只写状态栏）。</param>
        private void LoadWorkbook(string excelFilePath, bool showErrorDialog)
        {
            Cursor previousCursor = Cursor;

            Cursor = Cursors.WaitCursor;

            try
            {
                _sheetNames.Clear();

                using (FileStream stream = OpenSharedRead(excelFilePath))
                using (IExcelDataReader reader = ExcelReaderFactory.CreateReader(stream))
                {
                    do
                    {
                        _sheetNames.Add(ReadSheetName(reader, _sheetNames.Count));
                    }
                    while (reader.NextResult());
                }

                _currentPath = excelFilePath;
                _sheetIndex = 0;

                // 图片不在单元格数据里，单独从 .xlsx / .xlsm 包里取（含锚点单元格）；
                // .xls / .xlsb 取不出来，返回空列表（状态栏会提示用【用 Excel 打开】）
                _images.Clear();
                _images.AddRange(XlsxImageCatalog.ReadImages(excelFilePath));
                _hasInCellImages = XlsxImageCatalog.HasInCellImages(excelFilePath);

                FillSheetList();

                UiLocalization.SetText(this, "Excel 预览 - " + Path.GetFileName(excelFilePath));

                // 换文件：PDF 视图的内容属于上一个文件，先释放并回到表格视图
                ClosePdfDocument();
                SetPdfViewActive(false);

                LoadSheet(0);

                // 之前转换过（缓存还在）就直接进 PDF 视图；没有缓存就留在表格视图，
                // 等用户点【PDF 视图】再启动 Excel
                PreparePdfView(excelFilePath);
            }
            catch (Exception ex)
            {
                _currentPath = excelFilePath;
                _sheetNames.Clear();
                _images.Clear();
                _hasInCellImages = false;

                ClosePdfDocument();
                FillSheetList();
                ClearGrid();
                SetPdfViewActive(false);

                UiLocalization.SetText(this, "Excel 预览 - 读取失败");
                SetStatus(UiText.F("无法读取该 Excel：{0}", ex.Message), StatusErrorColor);

                if (showErrorDialog)
                {
                    MessageBox.Show(this,
                        UiText.F("无法读取该 Excel：\n{0}\n\n{1}", excelFilePath, ex.Message),
                        UiText.T("Excel 预览"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            finally
            {
                Cursor = previousCursor;
            }
        }

        /// <summary>
        /// 读并显示指定的工作表：整张表读进「行 → 单元格文本」，再填进表格。
        /// 最多显示 <see cref="MaxPreviewRows"/> 行 × <see cref="MaxPreviewColumns"/> 列，
        /// 超出部分在状态栏注明（完整内容用【用 Excel 打开】看）。
        /// </summary>
        /// <param name="index">工作表索引（0 起；超出范围时夹到有效范围）。</param>
        private void LoadSheet(int index)
        {
            if (string.IsNullOrEmpty(_currentPath) || _sheetNames.Count == 0)
            {
                ClearGrid();
                return;
            }

            int target = Math.Max(0, Math.Min(index, _sheetNames.Count - 1));
            Cursor previousCursor = Cursor;

            Cursor = Cursors.WaitCursor;

            try
            {
                List<string[]> rows = new List<string[]>();
                bool truncated = false;

                using (FileStream stream = OpenSharedRead(_currentPath))
                using (IExcelDataReader reader = ExcelReaderFactory.CreateReader(stream))
                {
                    // ExcelDataReader 是「只能往前读」的：要跳到第 N 张表，先跳过前面几张
                    for (int skip = 0; skip < target; skip++)
                    {
                        if (!reader.NextResult())
                        {
                            break;
                        }
                    }

                    while (reader.Read())
                    {
                        if (rows.Count >= MaxPreviewRows)
                        {
                            truncated = true;
                            break;
                        }

                        int count = Math.Min(reader.FieldCount, MaxPreviewColumns);
                        string[] row = new string[count];

                        for (int c = 0; c < count; c++)
                        {
                            object value;

                            try
                            {
                                value = reader.GetValue(c);
                            }
                            catch (Exception)
                            {
                                value = null;       // 损坏的单元格：当空格处理
                            }

                            row[c] = ToText(value);
                        }

                        rows.Add(row);
                    }
                }

                _sheetIndex = target;

                FillGrid(rows);
                SyncSheetList();
                ShowSheetImages();

                if (_pdfViewActive)
                {
                    // PDF 视图显示的是整个工作簿：换工作表不改变画面，只保留 PDF 的说明
                    SetStatus(BuildPdfStatusText(), StatusOkColor);
                }
                else
                {
                    SetStatus(BuildStatusText(rows, truncated) + BuildImageNote(), StatusOkColor);
                }
            }
            catch (Exception ex)
            {
                ClearGrid();
                SetStatus(UiText.F("无法读取工作表：{0}", ex.Message), StatusErrorColor);
            }
            finally
            {
                Cursor = previousCursor;
            }
        }

        /// <summary>把工作表名重填进下拉框（换工作簿时用，选中第 1 张）。</summary>
        private void FillSheetList()
        {
            if (_cboSheet == null)
            {
                return;
            }

            _updatingSheetList = true;

            try
            {
                _cboSheet.Items.Clear();

                foreach (string name in _sheetNames)
                {
                    _cboSheet.Items.Add(name);
                }

                if (_sheetNames.Count > 0)
                {
                    _cboSheet.SelectedIndex = Math.Min(Math.Max(_sheetIndex, 0), _sheetNames.Count - 1);
                }
            }
            finally
            {
                _updatingSheetList = false;
            }
        }

        /// <summary>把下拉框的选中项同步成 <see cref="_sheetIndex"/>（程序换表后调用，不触发重新读取）。</summary>
        private void SyncSheetList()
        {
            if (_cboSheet == null)
            {
                return;
            }

            _updatingSheetList = true;

            try
            {
                if (_cboSheet.Items.Count > 0 &&
                    _sheetIndex >= 0 && _sheetIndex < _cboSheet.Items.Count)
                {
                    _cboSheet.SelectedIndex = _sheetIndex;
                }
            }
            finally
            {
                _updatingSheetList = false;
            }
        }

        /// <summary>清空表格（列也一起清掉：下一张表的列数可能不同）。</summary>
        private void ClearGrid()
        {
            ClearImageOverlays();

            if (_grid == null)
            {
                return;
            }

            _grid.Rows.Clear();
            _grid.Columns.Clear();
        }

        /// <summary>
        /// 把当前工作表上的图片贴到表格上（贴在锚点单元格的左上角）。
        /// <para>
        /// 图片是「浮在单元格上」的对象，不在 ExcelDataReader 的读取范围里，
        /// 由 <see cref="XlsxImageCatalog"/> 从 .xlsx / .xlsm 包里取出来（带锚点单元格），
        /// 这里用 PictureBox 叠到对应位置，观感与 Excel 一致（会盖住下面的单元格，
        /// 想看单元格文字时取消勾选【显示图片】）。
        /// </para>
        /// </summary>
        private void ShowSheetImages()
        {
            ClearImageOverlays();

            if (_grid == null || _images.Count == 0)
            {
                return;
            }

            if (_chkImages != null && !_chkImages.Checked)
            {
                return;
            }

            // PDF 视图里图片是 Excel 自己画在页面上的：不用在表格上再叠一层
            if (_pdfViewActive)
            {
                return;
            }

            int columnCount = _grid.Columns.Count;
            int rowCount = _grid.Rows.Count;

            foreach (XlsxImageInfo image in _images)
            {
                if (image.SheetIndex != _sheetIndex)
                {
                    continue;
                }

                // 锚点落在预览范围外（列 / 行被截断）时没法定位，跳过
                if (image.Column < 0 || image.Row < 0 ||
                    image.Column >= columnCount || image.Row >= rowCount)
                {
                    continue;
                }

                Image bitmap = LoadImage(image.Data);

                if (bitmap == null)
                {
                    continue;
                }

                PictureBox box = new PictureBox();
                box.Image = bitmap;
                box.SizeMode = PictureBoxSizeMode.Zoom;
                box.BackColor = Color.White;
                box.BorderStyle = BorderStyle.FixedSingle;
                box.Size = FitImageSize(new Size(bitmap.Width, bitmap.Height));

                ImageOverlay overlay = new ImageOverlay();
                overlay.Box = box;
                overlay.Column = image.Column;
                overlay.Row = image.Row;

                _overlays.Add(overlay);
                _grid.Controls.Add(box);
            }

            PositionImageOverlays();
        }

        /// <summary>
        /// 重新摆放图片：按锚点单元格在表格里的显示矩形定位。
        /// 表格滚动 / 窗口缩放 / 列宽变化后都要调用（子控件不会自己跟着滚）。
        /// </summary>
        private void PositionImageOverlays()
        {
            if (_grid == null || _overlays.Count == 0)
            {
                return;
            }

            // 数据区（不含行列头和滚动条）：表格的子控件画在表头上面，
            // 所以锚点滚出数据区时必须把图片收起来，否则会盖住列头。
            // 不用 _grid.DisplayRectangle：DataGridView 的滚动不是 AutoScroll，那个矩形不可靠。
            Rectangle client = _grid.ClientRectangle;
            int left = _grid.RowHeadersVisible ? _grid.RowHeadersWidth : 0;
            int top = _grid.ColumnHeadersVisible ? _grid.ColumnHeadersHeight : 0;
            Rectangle body = new Rectangle(client.Left + left, client.Top + top,
                Math.Max(client.Width - left, 0), Math.Max(client.Height - top, 0));

            foreach (ImageOverlay overlay in _overlays)
            {
                PictureBox box = overlay.Box;

                if (box == null)
                {
                    continue;
                }

                Rectangle cell = Rectangle.Empty;

                if (overlay.Column < _grid.Columns.Count && overlay.Row < _grid.Rows.Count)
                {
                    cell = _grid.GetCellDisplayRectangle(overlay.Column, overlay.Row, false);
                }

                if (cell.Width <= 0 || cell.Height <= 0)
                {
                    box.Visible = false;
                    continue;
                }

                box.Location = new Point(cell.Left + 2, cell.Top + 2);

                // 数据区外的部分裁掉（Region）：表格的子控件会盖在滚动条上面，
                // 不裁掉的话靠右 / 靠下的图片会把滚动条挡住、点不动。
                // 只有「可见尺寸变了」才换 Region（Region 是 GDI 句柄，不用每次滚动都新建）
                int visibleWidth = Math.Min(box.Width, body.Right - box.Left);
                int visibleHeight = Math.Min(box.Height, body.Bottom - box.Top);

                if (cell.Left < body.Left || cell.Top < body.Top ||
                    visibleWidth <= 0 || visibleHeight <= 0)
                {
                    box.Visible = false;
                    continue;
                }

                if (overlay.RegionWidth != visibleWidth || overlay.RegionHeight != visibleHeight)
                {
                    box.Region = new Region(new Rectangle(0, 0, visibleWidth, visibleHeight));
                    overlay.RegionWidth = visibleWidth;
                    overlay.RegionHeight = visibleHeight;
                }

                box.Visible = true;
            }
        }

        /// <summary>移除并释放当前显示的图片控件（图片本身也要 Dispose：换表次数多了会漏内存）。</summary>
        private void ClearImageOverlays()
        {
            foreach (ImageOverlay overlay in _overlays)
            {
                PictureBox box = overlay.Box;

                if (box == null)
                {
                    continue;
                }

                if (_grid != null)
                {
                    _grid.Controls.Remove(box);
                }

                Image bitmap = box.Image;
                box.Image = null;

                if (bitmap != null)
                {
                    bitmap.Dispose();
                }

                box.Dispose();
            }

            _overlays.Clear();
        }

        /// <summary>
        /// 把「行 → 单元格文本」填进表格：列头用 Excel 的列名（A / B / C …），
        /// 行头用 Excel 的行号（1 起），这样单元格位置与 Excel 里能对得上。
        /// </summary>
        private void FillGrid(IList<string[]> rows)
        {
            _grid.SuspendLayout();

            try
            {
                _grid.Rows.Clear();
                _grid.Columns.Clear();

                int columns = 0;

                foreach (string[] row in rows)
                {
                    columns = Math.Max(columns, row == null ? 0 : row.Length);
                }

                columns = Math.Min(Math.Max(columns, 1), MaxPreviewColumns);

                for (int c = 0; c < columns; c++)
                {
                    DataGridViewTextBoxColumn column = new DataGridViewTextBoxColumn();
                    column.Name = "col" + c.ToString(CultureInfo.InvariantCulture);
                    column.HeaderText = ColumnName(c);
                    column.ReadOnly = true;
                    column.SortMode = DataGridViewColumnSortMode.NotSortable;
                    column.Width = 110;

                    _grid.Columns.Add(column);
                }

                foreach (string[] row in rows)
                {
                    _grid.Rows.Add((object[])row);
                }

                for (int r = 0; r < _grid.Rows.Count; r++)
                {
                    _grid.Rows[r].HeaderCell.Value =
                        (r + 1).ToString(CultureInfo.InvariantCulture);
                }
            }
            finally
            {
                _grid.ResumeLayout();
            }
        }

        /// <summary>
        /// 状态栏文字：「工作表：xxx（2/5）／ 显示 30 行 × 8 列」，
        /// 行数超限 / 空表时再补一句说明。
        /// </summary>
        private string BuildStatusText(IList<string[]> rows, bool truncated)
        {
            int columns = 0;

            foreach (string[] row in rows)
            {
                columns = Math.Max(columns, row == null ? 0 : row.Length);
            }

            string sheetName = _sheetIndex >= 0 && _sheetIndex < _sheetNames.Count
                ? _sheetNames[_sheetIndex]
                : string.Empty;

            string text = UiText.F("工作表：{0}（{1}/{2}）／ 显示 {3} 行 × {4} 列",
                sheetName,
                (_sheetIndex + 1).ToString(CultureInfo.CurrentCulture),
                _sheetNames.Count.ToString(CultureInfo.CurrentCulture),
                rows.Count.ToString(CultureInfo.CurrentCulture),
                columns.ToString(CultureInfo.CurrentCulture));

            if (truncated)
            {
                text += " " + UiText.F("（仅显示前 {0} 行，完整内容请用【用 Excel 打开】）",
                    MaxPreviewRows.ToString(CultureInfo.CurrentCulture));
            }

            if (rows.Count == 0)
            {
                text += " " + UiText.T("（这张表是空的）");
            }

            return text;
        }

        /// <summary>
        /// 以「共享读」方式打开文件：预览期间别的程序（Excel 自己 / 上层工具）仍可以保存该文件，
        /// 不会因为这里开着就报「文件被占用」。
        /// </summary>
        private static FileStream OpenSharedRead(string filePath)
        {
            return new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }

        /// <summary>取工作表名；读不到时用「SheetN」占位（只影响下拉框显示）。</summary>
        private static string ReadSheetName(IExcelDataReader reader, int index)
        {
            try
            {
                string name = reader.Name;

                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name.Trim();
                }
            }
            catch (NotSupportedException)
            {
                // 个别格式不带表名
            }
            catch (InvalidOperationException)
            {
                // 同上
            }

            return "Sheet" + (index + 1).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>列索引 → Excel 列名（0 → A、25 → Z、26 → AA）。</summary>
        private static string ColumnName(int index)
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            int value = index;

            while (value >= 0)
            {
                builder.Insert(0, (char)('A' + value % 26));
                value = value / 26 - 1;
            }

            return builder.ToString();
        }

        /// <summary>
        /// 单元格值 → 文本：字符串原样，数字去掉多余的 <c>.0</c>，日期写成 <c>yyyy-MM-dd</c>
        /// （与 <see cref="XlsxSheetReader"/> 的格式化保持一致，两条读取路径读出来的表能对得上）。
        /// </summary>
        private static string ToText(object value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            string text = value as string;

            if (text != null)
            {
                return text;
            }

            if (value is bool)
            {
                return ((bool)value) ? "TRUE" : "FALSE";
            }

            if (value is DateTime)
            {
                return ((DateTime)value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            if (value is double || value is float || value is decimal)
            {
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);

                if (Math.Abs(number - Math.Round(number)) < 1e-9)
                {
                    return ((long)Math.Round(number)).ToString(CultureInfo.InvariantCulture);
                }

                return number.ToString("0.########", CultureInfo.InvariantCulture);
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        /// <summary>改状态栏文字 + 颜色。</summary>
        private void SetStatus(string text, Color color)
        {
            if (_lblStatus == null)
            {
                return;
            }

            _lblStatus.Text = text ?? string.Empty;
            _lblStatus.ForeColor = color;
        }

        /// <summary>
        /// 状态栏末尾补一句图片说明：有图片时写「含 N 张图片」；
        /// .xls / .xlsb 取不出图片时提示用【用 Excel 打开】。
        /// </summary>
        private string BuildImageNote()
        {
            if (_overlays.Count > 0)
            {
                return " " + UiText.F("（含 {0} 张图片，已叠在对应的单元格上）",
                    _overlays.Count.ToString(CultureInfo.CurrentCulture));
            }

            string extension = string.IsNullOrEmpty(_currentPath)
                ? string.Empty
                : Path.GetExtension(_currentPath);

            if (string.Equals(extension, ".xls", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".xlsb", StringComparison.OrdinalIgnoreCase))
            {
                return " " + UiText.T("（.xls / .xlsb 文件不显示图片，需要看图片请用【用 Excel 打开】）");
            }

            if (_hasInCellImages)
            {
                return " " + UiText.T("（该 Excel 的图片是「放在单元格中」的新格式，预览不显示，请用【用 Excel 打开】）");
            }

            return string.Empty;
        }

        /// <summary>图片字节 → 位图；解不了的格式（损坏 / 少见格式）返回 <c>null</c>（这张图不显示）。</summary>
        private static Image LoadImage(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return null;
            }

            try
            {
                using (MemoryStream stream = new MemoryStream(data))
                using (Image image = Image.FromStream(stream))
                {
                    // Image.FromStream 要求流在图片整个生命周期内保持打开：拷一份出来自己管
                    return new Bitmap(image);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 图片显示尺寸：原始尺寸优先，超过 480 × 360 的按比例缩小
        /// （预览窗口比 Excel 小，原尺寸的大图会把半张表盖住）。
        /// </summary>
        private static Size FitImageSize(Size natural)
        {
            const int maxWidth = 480;
            const int maxHeight = 360;

            int width = Math.Max(natural.Width, 1);
            int height = Math.Max(natural.Height, 1);

            if (width <= maxWidth && height <= maxHeight)
            {
                return new Size(width, height);
            }

            double scale = Math.Min((double)maxWidth / width, (double)maxHeight / height);

            return new Size(Math.Max(1, (int)Math.Round(width * scale)),
                Math.Max(1, (int)Math.Round(height * scale)));
        }

        /// <summary>表格滚动：图片跟着单元格一起移动。</summary>
        private void Grid_Scroll(object sender, ScrollEventArgs e)
        {
            PositionImageOverlays();
        }

        /// <summary>表格改变大小（窗口缩放）：重新摆放图片。</summary>
        private void Grid_SizeChanged(object sender, EventArgs e)
        {
            PositionImageOverlays();
        }

        /// <summary>列宽变化：锚点单元格的横坐标变了。</summary>
        private void Grid_ColumnWidthChanged(object sender, DataGridViewColumnEventArgs e)
        {
            PositionImageOverlays();
        }

        /// <summary>【显示图片】勾选变化：重新贴上 / 收起图片。</summary>
        private void ChkImages_CheckedChanged(object sender, EventArgs e)
        {
            ShowSheetImages();
        }

        // ───────────────────────── PDF 视图（用 Excel 原样显示）─────────────────────────

        /// <summary>
        /// 【PDF 视图】：用本机 Excel 把工作簿导出成 PDF 后显示
        /// （图片、图表、条件格式、合并单元格都与 Excel 一致）。
        /// 没装 Excel / 转换失败时留在表格视图，原因写在状态栏。
        /// </summary>
        private void BtnViewPdf_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_currentPath))
            {
                return;
            }

            if (_pdfDocument == null && !ExcelPdfConverter.IsExcelAvailable)
            {
                // 没装 Excel：不启动 COM，直接说明（表格视图照常可用）；
                // 以前生成过的缓存还在时仍然可以显示（下面会走缓存）
                string cachedPath;

                if (!ExcelPdfConverter.TryGetCachedPdf(_currentPath, out cachedPath))
                {
                    _pdfError = UiText.T("本机没有安装 Excel。");
                    SetPdfViewActive(false);
                    SetStatus(_pdfError, StatusErrorColor);

                    return;
                }
            }

            if (_pdfDocument == null && !EnsurePdfDocument())
            {
                // 「选中即预览」很频繁：失败只写状态栏，不弹对话框
                SetPdfViewActive(false);
                SetStatus(_pdfError, StatusErrorColor);

                return;
            }

            SetPdfViewActive(true);
        }

        /// <summary>【表格视图】：回到只读表格。</summary>
        private void BtnViewGrid_Click(object sender, EventArgs e)
        {
            SetPdfViewActive(false);
        }

        /// <summary>
        /// 切换视图：PDF 视图下隐藏表格与图片、显示 PDF 容器和翻页条；
        /// 表格视图下反过来，并把图片按当前滚动位置重新贴一次。
        /// </summary>
        private void SetPdfViewActive(bool active)
        {
            _pdfViewActive = active && _pdfDocument != null;

            if (_grid != null)
            {
                _grid.Visible = !_pdfViewActive;
            }

            if (_pnlPdf != null)
            {
                _pnlPdf.Visible = _pdfViewActive;
            }

            if (_pnlPdfBar != null)
            {
                _pnlPdfBar.Visible = _pdfViewActive;
            }

            if (_btnViewPdf != null)
            {
                _btnViewPdf.Enabled = !_pdfViewActive && !string.IsNullOrEmpty(_currentPath);
            }

            if (_btnViewGrid != null)
            {
                _btnViewGrid.Enabled = _pdfViewActive;
            }

            // 【显示图片】只是表格视图的叠加层：PDF 视图里图片本来就是 Excel 画好的
            if (_chkImages != null)
            {
                _chkImages.Enabled = !_pdfViewActive;
            }

            if (_pdfViewActive)
            {
                RenderPdfPage();
                UpdatePdfBar();
                SetStatus(BuildPdfStatusText(), StatusOkColor);
            }
            else
            {
                ShowSheetImages();
                PositionImageOverlays();
            }
        }

        /// <summary>
        /// 确保当前 Excel 对应的 PDF 已加载：有缓存直接用，没有就调 Excel 导出（首次 1~3 秒）。
        /// 失败时返回 <c>false</c>，原因写进 <see cref="_pdfError"/>。
        /// </summary>
        private bool EnsurePdfDocument()
        {
            if (_pdfDocument != null &&
                string.Equals(_pdfSourcePath, _currentPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 这个文件已经试过且失败了：不重复启动 Excel（点【刷新】可以重试）
            if (_pdfTried && !string.IsNullOrEmpty(_pdfError))
            {
                return false;
            }

            if (string.IsNullOrEmpty(_currentPath) || !File.Exists(_currentPath))
            {
                _pdfError = UiText.F("文件不存在：{0}", _currentPath ?? string.Empty);

                return false;
            }

            _pdfTried = true;

            Cursor previousCursor = Cursor;

            Cursor = Cursors.WaitCursor;

            try
            {
                SetStatus(UiText.T("正在用 Excel 生成预览，请稍候…"), StatusInfoColor);
                Refresh();

                string pdfPath;
                string reason;

                if (!ExcelPdfConverter.TryGetCachedPdf(_currentPath, out pdfPath) &&
                    !ExcelPdfConverter.TryConvert(_currentPath, out pdfPath, out reason))
                {
                    _pdfError = UiText.F("Excel 无法生成预览（{0}），已切换为表格预览",
                        reason ?? string.Empty);

                    return false;
                }

                if (!TryLoadPdfDocument(pdfPath, out reason))
                {
                    _pdfError = UiText.F("PDF 视图不可用（{0}），已切换为表格预览",
                        reason ?? string.Empty);

                    return false;
                }

                _pdfError = null;

                return true;
            }
            finally
            {
                Cursor = previousCursor;
            }
        }

        /// <summary>打开缓存 PDF（先释放旧的）。失败时 <paramref name="error"/> 是原因。</summary>
        private bool TryLoadPdfDocument(string pdfPath, out string error)
        {
            error = null;

            ClosePdfDocument();

            try
            {
                string pdfiumMessage;

                if (!PdfiumRuntime.EnsureAvailable(out pdfiumMessage))
                {
                    // 原生库不可用时不能碰 PdfiumViewer：PdfLibrary 初始化 / 终结时要调
                    // FPDF_AddRef / FPDF_Release，缺这两个导出时会在终结器里再抛一次异常
                    error = UiText.T("PDF 渲染引擎不可用（pdfium.dll）") +
                        (string.IsNullOrEmpty(pdfiumMessage) ? string.Empty : "：" + pdfiumMessage);

                    return false;
                }

                _pdfDocument = PdfDocument.Load(pdfPath);
                _pdfSourcePath = _currentPath;
                _pdfPageIndex = 0;
                _pdfFitWidth = true;
                _pdfScale = 1F;
                _lastPdfRenderWidth = 0;

                return _pdfDocument.PageCount > 0;
            }
            catch (Exception ex)
            {
                ClosePdfDocument();
                error = ex.Message;

                return false;
            }
        }

        /// <summary>
        /// 换文件后：之前转换过（缓存还在）就直接进 PDF 视图；没有缓存就留在表格视图，
        /// 等用户点【PDF 视图】再启动 Excel（「选中即预览」很频繁，不能每次打开文件都等几秒）。
        /// </summary>
        private void PreparePdfView(string excelFilePath)
        {
            ClosePdfDocument();

            _pdfTried = false;
            _pdfError = null;

            string cachedPath;
            string error;

            if (!ExcelPdfConverter.TryGetCachedPdf(excelFilePath, out cachedPath) ||
                !TryLoadPdfDocument(cachedPath, out error))
            {
                SetPdfViewActive(false);

                return;
            }

            _pdfTried = true;
            SetPdfViewActive(true);
        }

        /// <summary>
        /// 按当前页 / 缩放渲染页面位图（页面尺寸取自 PDF，位图按 96 DPI 渲染）。
        /// </summary>
        private void RenderPdfPage()
        {
            if (_pdfDocument == null || _picPdf == null || _pnlPdf == null || !_pnlPdf.Visible)
            {
                return;
            }

            int pageCount = _pdfDocument.PageCount;

            if (pageCount <= 0)
            {
                SetStatus(UiText.T("这个 PDF 没有可显示的页面。"), StatusErrorColor);

                return;
            }

            _pdfPageIndex = Math.Max(0, Math.Min(_pdfPageIndex, pageCount - 1));

            SizeF pageSize;

            try
            {
                pageSize = _pdfDocument.PageSizes[_pdfPageIndex];
            }
            catch (Exception ex)
            {
                SetStatus(UiText.F("PDF 渲染失败：{0}", ex.Message), StatusErrorColor);

                return;
            }

            if (pageSize.Width <= 0F || pageSize.Height <= 0F)
            {
                SetStatus(UiText.T("这个 PDF 没有可显示的页面。"), StatusErrorColor);

                return;
            }

            int viewWidth = Math.Max(120, _pnlPdf.ClientSize.Width - PdfPageMargin * 2);
            float scale = _pdfFitWidth ? viewWidth / pageSize.Width : _pdfScale;

            scale = Math.Max(MinPdfScale, Math.Min(MaxPdfScale, scale));

            int width = Math.Max(1, (int)Math.Round(pageSize.Width * scale));
            int height = Math.Max(1, (int)Math.Round(pageSize.Height * scale));
            long pixels = (long)width * height;

            if (pixels > MaxPdfRenderPixels)
            {
                // 放大过头时按比例收缩，避免一次申请过大的位图
                double shrink = Math.Sqrt((double)MaxPdfRenderPixels / pixels);

                width = Math.Max(1, (int)Math.Round(width * shrink));
                height = Math.Max(1, (int)Math.Round(height * shrink));
                scale = width / pageSize.Width;
            }

            _pdfScale = scale;
            _lastPdfRenderWidth = _pnlPdf.ClientSize.Width;

            try
            {
                Image image = _pdfDocument.Render(_pdfPageIndex, width, height, 96F, 96F, false);
                Image previous = _picPdf.Image;

                _picPdf.Size = new Size(width, height);
                _picPdf.Image = image;

                if (previous != null)
                {
                    previous.Dispose();
                }

                LayoutPdfPage();
            }
            catch (Exception ex)
            {
                SetStatus(UiText.F("PDF 渲染失败：{0}", ex.Message), StatusErrorColor);
            }
        }

        /// <summary>把页面位图居中放在容器里（比可视区大时靠左上，出现滚动条）。</summary>
        private void LayoutPdfPage()
        {
            if (_picPdf == null || _pnlPdf == null)
            {
                return;
            }

            int left = Math.Max(PdfPageMargin, (_pnlPdf.ClientSize.Width - _picPdf.Width) / 2);

            _picPdf.Location = new Point(left, PdfPageMargin);
        }

        /// <summary>页码文字与翻页 / 缩放按钮的启用状态。</summary>
        private void UpdatePdfBar()
        {
            int pageCount = _pdfDocument == null ? 0 : _pdfDocument.PageCount;
            int current = pageCount <= 0 ? 0 : _pdfPageIndex + 1;

            if (_lblPdfPage != null)
            {
                // 纯数字，不需要中日翻译
                _lblPdfPage.Text = current.ToString(CultureInfo.CurrentCulture) + " / " +
                    Math.Max(pageCount, 1).ToString(CultureInfo.CurrentCulture);
            }

            if (_btnPdfPrev != null)
            {
                _btnPdfPrev.Enabled = _pdfPageIndex > 0;
            }

            if (_btnPdfNext != null)
            {
                _btnPdfNext.Enabled = pageCount > 0 && _pdfPageIndex < pageCount - 1;
            }

            if (_btnPdfZoomIn != null)
            {
                _btnPdfZoomIn.Enabled = _pdfDocument != null;
            }

            if (_btnPdfZoomOut != null)
            {
                _btnPdfZoomOut.Enabled = _pdfDocument != null;
            }

            if (_btnPdfFitWidth != null)
            {
                _btnPdfFitWidth.Enabled = _pdfDocument != null;
            }
        }

        private void BtnPdfPrev_Click(object sender, EventArgs e)
        {
            GoToPdfPage(_pdfPageIndex - 1);
        }

        private void BtnPdfNext_Click(object sender, EventArgs e)
        {
            GoToPdfPage(_pdfPageIndex + 1);
        }

        /// <summary>翻到指定页（越界时夹到有效范围；换页后回到页面顶部）。</summary>
        private void GoToPdfPage(int index)
        {
            if (_pdfDocument == null)
            {
                return;
            }

            int target = Math.Max(0, Math.Min(index, _pdfDocument.PageCount - 1));

            if (target == _pdfPageIndex)
            {
                return;
            }

            _pdfPageIndex = target;

            RenderPdfPage();
            UpdatePdfBar();

            if (_pnlPdf != null)
            {
                _pnlPdf.AutoScrollPosition = new Point(0, 0);
            }

            SetStatus(BuildPdfStatusText(), StatusOkColor);
        }

        private void BtnPdfZoomIn_Click(object sender, EventArgs e)
        {
            ZoomPdf(1.25F);
        }

        private void BtnPdfZoomOut_Click(object sender, EventArgs e)
        {
            ZoomPdf(1F / 1.25F);
        }

        private void BtnPdfFitWidth_Click(object sender, EventArgs e)
        {
            _pdfFitWidth = true;

            RenderPdfPage();
            UpdatePdfBar();
        }

        /// <summary>按倍数放大 / 缩小（放大后改为固定比例，不再按宽度自适应）。</summary>
        private void ZoomPdf(float factor)
        {
            if (_pdfDocument == null)
            {
                return;
            }

            _pdfFitWidth = false;
            _pdfScale = Math.Max(MinPdfScale, Math.Min(MaxPdfScale, _pdfScale * factor));

            RenderPdfPage();
            UpdatePdfBar();
        }

        /// <summary>窗口宽度变化：自适应时重渲染（差值小于 16 像素不重做，避免拖动窗口时卡）。</summary>
        private void PnlPdf_Resize(object sender, EventArgs e)
        {
            if (!_pdfViewActive || !_pdfFitWidth || _pnlPdf == null)
            {
                return;
            }

            if (Math.Abs(_pnlPdf.ClientSize.Width - _lastPdfRenderWidth) < 16)
            {
                return;
            }

            RenderPdfPage();
        }

        /// <summary>PDF 视图的状态栏文字。</summary>
        private string BuildPdfStatusText()
        {
            int pageCount = _pdfDocument == null ? 0 : _pdfDocument.PageCount;

            string text = UiText.F("PDF 视图：用 Excel 原样显示（共 {0} 页，当前第 {1} 页）",
                Math.Max(pageCount, 1).ToString(CultureInfo.CurrentCulture),
                (_pdfPageIndex + 1).ToString(CultureInfo.CurrentCulture));

            if (_sheetNames.Count > 1)
            {
                text += " " + UiText.T("（PDF 里包含整个工作簿的所有工作表）");
            }

            return text;
        }

        /// <summary>释放当前 PDF 与页面位图（换文件 / 刷新 / 关闭窗口时调用）。</summary>
        private void ClosePdfDocument()
        {
            if (_picPdf != null)
            {
                Image image = _picPdf.Image;

                _picPdf.Image = null;

                if (image != null)
                {
                    image.Dispose();
                }
            }

            if (_pdfDocument != null)
            {
                try
                {
                    _pdfDocument.Dispose();
                }
                catch (Exception)
                {
                    // 释放失败不影响关闭窗口
                }

                _pdfDocument = null;
            }

            _pdfSourcePath = null;
            _pdfPageIndex = 0;
            _pdfFitWidth = true;
            _pdfScale = 1F;
            _lastPdfRenderWidth = 0;
        }

        /// <summary>关闭窗口时释放图片（图片是手动加成表格子控件的，Form 不会自动释放）。</summary>
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            ClearImageOverlays();
            _images.Clear();
            ClosePdfDocument();

            base.OnFormClosed(e);
        }

        /// <summary>一张叠在表格上的图片：控件 + 它的锚点单元格（重新定位时要用）。</summary>
        private sealed class ImageOverlay
        {
            /// <summary>显示图片的控件（由本窗口创建、本窗口释放）。</summary>
            public PictureBox Box { get; set; }

            /// <summary>锚点单元格的列（0 起）。</summary>
            public int Column { get; set; }

            /// <summary>锚点单元格的行（0 起）。</summary>
            public int Row { get; set; }

            /// <summary>当前裁剪区域（Region）的宽；0 = 还没设过。</summary>
            public int RegionWidth { get; set; }

            /// <summary>当前裁剪区域（Region）的高；0 = 还没设过。</summary>
            public int RegionHeight { get; set; }
        }
    }
}
