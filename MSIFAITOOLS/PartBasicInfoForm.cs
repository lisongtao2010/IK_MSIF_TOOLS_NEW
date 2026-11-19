using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MSIFAITOOLS.Services;

namespace MSIFAITOOLS
{
    /// <summary>
    /// 部品基本信息画面：显示指定 PDF（図番）已登录到 SQLite 的部品明细，
    /// 并支持在画面上直接编辑、追加行、删除行，保存后回写数据库。
    /// 由【分类结果】选中 PDF 行后点击【部品信息】/右键菜单/Enter 打开；
    /// 窗口可复用（<see cref="ShowDrawing"/> 切换显示内容）。
    ///
    /// <para>
    /// <b>全局只保留一个</b>：主画面打开【部品信息】之前先用 <see cref="FindOpenInstance"/> 取已经打开的那一个，
    /// 不同图番只切换显示内容、不再新建窗口；构造函数里也会先关掉还活着的旧画面
    /// （见 <see cref="_currentInstance"/> / <c>Form1.ShowPartInfo</c>）。
    /// </para>
    ///
    /// <para>
    /// <b>表格操作</b>：单击 = 只选中<b>这一格</b>（不再整行选中）并<b>直接进入编辑、内容全选</b>，
    /// 再打一次字就替换掉原来的值（见 <see cref="DgvParts_CellClick"/>）。
    /// <b>「行号」列</b>（显示在最左侧、<c>colNo</c>、Excel 行号栏那样）：只读、追加 / 删除行后自动重排，
    /// 点这一格 = <b>选中整行</b>（按住 Ctrl 可多选 → 【删除行】一次删多行）。
    /// 「部材名」这一格单击时改为打开「选择部材名」窗口 <see cref="MemberNamePickerForm"/>
    /// （<b>非模态</b>：开着也能操作本画面；候选列表像输入法那样每项带编号，按 1～9 / 0 或单击就立刻写进这一格）。
    /// 窗口里按同一行的「部品名」（<c>colPartName</c>）去置换表
    /// （SQL Server 表 <c>genpin.dbo.T_M_MergeName</c>）查到的部材名排在<b>第 1 号</b>，按 <c>1</c> 就写入。
    /// 删除 / 追加行以<b>选中单元格所在的行</b>为基准（<see cref="GetSelectedRows"/>）。
    /// <b>右键菜单</b>（Excel 那样）：在表格上点右键 →「在上方插入行」「在下方插入行」「删除行」，
    /// 作用在右键的那一行上（见 <see cref="InitializePartsContextMenu"/>）。
    /// </para>
    /// </summary>
    internal sealed partial class PartBasicInfoForm : Form
    {
        /// <summary>本画面打开的 PDF 预览窗口（关闭本画面时一并关闭）。</summary>
        private PdfPreviewForm _previewForm;

        /// <summary>
        /// 上一次在 PDF 预览画面上闪红框的行号（<see cref="FlashSelectedRowSource"/>）。
        /// 同一行只闪一次（连续点这一行的不同单元格不重复闪）；换图 / 重新载入后重置成 -1。
        /// </summary>
        private int _lastFlashRowIndex = -1;

        /// <summary>当前显示的図番（PDF 文件名去扩展名）。</summary>
        private string _drawingNo = string.Empty;

        /// <summary>当前显示的 PDF 完整路径。</summary>
        private string _pdfPath = string.Empty;

        /// <summary>
        /// 指定的数据库路径（由主画面按「&lt;所选 PDF 文件夹&gt;\AiAppData\MSIF_Parts.db」算出并传入；
        /// 为空时退回 <see cref="PartBasicInfoRepository.ResolveDatabasePath()"/>）。
        /// </summary>
        private readonly string _databasePath;

        /// <summary>填表期间抑制「单元格变更」引起的脏标记。</summary>
        private bool _suppressGridEvents;

        /// <summary>画面上是否存在尚未保存到数据库的修改。</summary>
        private bool _isDirty;

        /// <summary>窗口尚未显示，需要在 <see cref="OnShown"/> 里打开 PDF 预览窗口。</summary>
        private bool _openPreviewOnShown;

        /// <summary>部品基本信息窗口是否已按屏幕摆放（只摆一次，之后保留用户拖动的位置）。</summary>
        private bool _editorPlaced;

        /// <summary>
        /// 表格的右键菜单（Excel 那样）：<b>在上方插入行</b> / <b>在下方插入行</b> / <b>删除行</b>。
        /// 由 <see cref="InitializePartsContextMenu"/> 用代码创建（这块表格的列本来就是代码建的）。
        /// </summary>
        private ContextMenuStrip _partsMenu;

        /// <summary>右键菜单「在上方插入行」：插到右键那一行的上面。</summary>
        private ToolStripMenuItem _menuInsertRowAbove;

        /// <summary>右键菜单「在下方插入行」：插到右键那一行的下面。</summary>
        private ToolStripMenuItem _menuInsertRowBelow;

        /// <summary>右键菜单「删除行」（选中多行时文字变成「删除选中的 N 行」）。</summary>
        private ToolStripMenuItem _menuDeleteRow;

        /// <summary>
        /// 这一次鼠标按下是不是<b>右键</b>：右键 = 弹出菜单（插入行 / 删除行），
        /// 不能像左键那样进入编辑 / 打开「选择部材名」窗口（见 <see cref="DgvParts_CellClick"/>）。
        /// </summary>
        private bool _rightClickInProgress;

        /// <summary>
        /// 「部品コード」为空时该单元格的背景色（粉红色）—— 表示这个值不是图纸 / CSV 给的，
        /// 而是按「部品名」从部品マスタ（<c>部品マスタ\Tbl_部品マスタ.csv</c>）推定的，需要人工确认。
        /// </summary>
        private static readonly Color PartCodeGuessBackColor = Color.FromArgb(255, 192, 203);

        /// <summary>部品マスタ 索引（部品名 → 部品コード）；第一次用到时读一次 CSV。</summary>
        private CmdPartMasterIndex _partMasterIndex;

        /// <summary>是否已经尝试读过部品マスタ（读不到也只试一次，不反复读盘）。</summary>
        private bool _partMasterLoaded;

        /// <summary>
        /// 当前打开的部品基本信息画面（<b>全局只保留一个</b>）：构造函数里登记、<see cref="OnFormClosed"/> 里注销。
        /// <para>
        /// 主画面（<c>Form1.ShowPartInfo</c>）在打开【部品信息】之前先用 <see cref="FindOpenInstance"/> 取这一个，
        /// 不同图番只调 <see cref="ShowDrawing"/> 切换显示内容，<b>不再新建第二个窗口</b>。
        /// 这样不管点多少次【部品信息】、换多少张图（包括换了图纸文件夹 = 换了一份库），
        /// 屏幕上始终只有一个部品基本信息画面。
        /// </para>
        /// </summary>
        private static PartBasicInfoForm _currentInstance;

        /// <summary>
        /// 「行号」列的名字（<c>colNo</c>）：画面行号（Excel 最左侧那一列），显示在最前面（<c>DisplayIndex = 0</c>）
        /// 且 <c>Frozen</c>；只读，追加 / 删除行后由 <see cref="RenumberRows"/> 重排。
        /// 点这一格 = 选中整行（见 <see cref="SelectWholeRow"/>）。
        /// </summary>
        private const string RowNumberColumnName = "colNo";

        /// <summary>
        /// 「行号」列的背景色（浅灰）：和业务列以及「数据来源」的来源色区分开，
        /// 一眼看出这一列不是数据（Excel 的行号栏那种感觉）。
        /// </summary>
        private static readonly Color RowNumberBackColor = Color.FromArgb(242, 242, 242);

        /// <summary>「行号」列的文字颜色（深灰，比数据文字淡一点）。</summary>
        private static readonly Color RowNumberForeColor = Color.FromArgb(112, 112, 112);

        /// <summary>
        /// 「部材名」列的名字（<c>colMemberName</c>）：多值用 <c>|</c> 连接的那一列，
        /// 单击时打开「选择部材名」窗口（<see cref="MemberNamePickerForm"/>）。
        /// </summary>
        private const string MemberNameColumnName = "colMemberName";

        /// <summary>
        /// 「部品名」列的名字（<c>colPartName</c>）：打开「选择部材名」窗口
        /// （<see cref="MemberNamePickerForm"/>）时传给窗口，
        /// 用来去部材 mapping（<c>DL_BuhinBuzaiMapping</c> 的 <c>BuhinName</c>）和
        /// 置换表（<c>T_M_MergeName</c> 的表示部材名 <c>MsifDevName</c>）查推荐项。
        /// </summary>
        private const string PartNameColumnName = "colPartName";

        /// <summary>
        /// 「部品コード」列的名字（<c>colPartCode</c>）：同样在打开「选择部材名」窗口时传给窗口，
        /// 用来去部材 mapping 查<b>这个部品实际用过的部材名</b>
        /// （<c>DL_BuhinBuzaiMapping.BuhinCd</c> = 这一列 → <c>BuzaiName</c>，推荐项里排最前）。
        /// </summary>
        private const string PartCodeColumnName = "colPartCode";

        /// <summary>
        /// 部材部品マスタ（部材名 → 部品名）索引：给「选择部材名」窗口当候选列表用。
        /// 画面打开时读一次（数据库优先，其次 <c>部材部品\部材部品.csv</c>），之后整画面复用。
        /// </summary>
        private MemberPartCatalog _memberNameCatalog;

        /// <summary>是否已经尝试读过部材部品マスタ（读不到也只试一次，不反复读盘）。</summary>
        private bool _memberNameCatalogLoaded;

        /// <summary>
        /// 「选择部材名」窗口（<see cref="MemberNamePickerForm"/>，<b>非模态</b>）：
        /// 窗口打开期间本画面照常操作（不会被挡住）；单击里面的一项就立刻写回当前「部材名」单元格。
        /// </summary>
        private MemberNamePickerForm _memberNamePicker;

        /// <summary>
        /// 「选择部材名」窗口这次要写入的单元格：窗口是非模态的，期间可能换行 / 换格，
        /// 所以记单元格引用而不是行号；行被删掉 / 换了図番以后引用会失效，写之前会再校验一次。
        /// </summary>
        private DataGridViewCell _memberNamePickerCell;

        /// <summary>
        /// 整行背景色（淡绿色）：这一行来自 <b>アッセンブリ図</b> 自身的部品表
        /// （「数据来源」= <see cref="PartSourceKind.CmdAssembly"/>，【CMD 分析】阶段 A）。
        /// </summary>
        private static readonly Color SourceAssemblyBackColor = Color.FromArgb(226, 240, 217);

        /// <summary>
        /// 整行背景色（淡蓝色）：这一行来自 <b>要領図</b> 的部品
        /// （「数据来源」= <see cref="PartSourceKind.CmdYoryo"/>，【CMD 分析】阶段 B）。
        /// </summary>
        private static readonly Color SourceYoryoBackColor = Color.FromArgb(222, 235, 247);

        /// <summary>
        /// 整行背景色（淡粉色）：这一行是<b>特注</b>
        /// （「特注」= <see cref="PartBasicInfo.SpecialNoteMark"/>，即 特注品.json 的追加対応特注寸法）。
        /// 比推定值的粉红色（<see cref="PartCodeGuessBackColor"/>）浅，两者能区分开。
        /// </summary>
        private static readonly Color SpecialNoteBackColor = Color.FromArgb(252, 228, 236);

        /// <summary>
        /// 整行背景色（淡紫色）：这一行是<b>被分割</b>过的
        /// （备注里有 <see cref="CmdCalloutPartSplitter.RemarkPositionPrefix"/>＝<c>位置：</c>，
        /// 第 2 步按圈号把 1 行拆成「右… / 左…」多条时，拆出来的每一条都会带上这个标记）。
        /// </summary>
        private static readonly Color SplitRowBackColor = Color.FromArgb(230, 224, 245);

        /// <summary>整行背景色（白色）：不属于上面任何一类（特注品.json / AI 分析 / 手工追加 / 数据来源为空）。</summary>
        private static readonly Color PlainRowBackColor = Color.White;

        /// <summary>「校验状态」= 坐标不符 时的单元格背景色（红底 + 白字，最需要注意）。</summary>
        private static readonly Color FailedStatusBackColor = Color.FromArgb(198, 40, 40);

        /// <summary>「校验状态」= 坐标一致 时的单元格文字颜色（深绿色）。</summary>
        private static readonly Color PassedStatusForeColor = Color.FromArgb(0, 100, 0);

        /// <summary>
        /// 「保存 Excel」导出的列（画面列名，顺序 = Excel 列顺序）：
        /// 部材名 / 部品名 / 区分 / 数量 / 切断寸法 / 部品ｺｰﾄﾞ / 参照元 / H下限 / H上限 / W下限 / W上限 / 使い分け / 特注。
        /// </summary>
        private static readonly string[] ExportColumnNames =
        {
            "colMemberName", "colPartName", "colFrameKind", "colQuantity", "colCutSize",
            "colPartCode", "colSourceTag", "colHeightMin", "colHeightMax", "colWidthMin",
            "colWidthMax", "colUsageKind", "colSpecialNote"
        };

        /// <summary>
        /// 导出表头（与 <see cref="ExportColumnNames"/> 一一对应）。
        /// 「部品ｺｰﾄﾞ」按要求用半角片假名（与拾い出し表 / マスタ的写法一致），其余与画面列名相同。
        /// </summary>
        private static readonly string[] ExportColumnHeaders =
        {
            "部材名", "部品名", "区分", "数量", "切断寸法", "部品ｺｰﾄﾞ", "参照元",
            "H下限", "H上限", "W下限", "W上限", "使い分け", "特注"
        };

        /// <summary>导出列宽（Excel 字符数，与 <see cref="ExportColumnHeaders"/> 一一对应）。</summary>
        private static readonly double[] ExportColumnWidths =
        {
            22, 34, 8, 8, 18, 16, 16, 10, 10, 10, 10, 16, 20
        };

        /// <summary>
        /// 导出时要写成 <b>数字类型</b>单元格的列（表头名，取自 <see cref="ExportColumnHeaders"/>）：
        /// 数量 / 切断寸法 / H下限 / H上限 / W下限 / W上限 —— Excel 里可直接排序、求和。
        /// 这几列里不是数字的值（如 <c>3-100</c>、<c>2枚</c>）仍按文本写，内容不丢。
        /// </summary>
        private static readonly string[] ExportNumericColumnHeaders =
        {
            "数量", "切断寸法", "H下限", "H上限", "W下限", "W上限"
        };

        /// <summary>
        /// <see cref="ExportNumericColumnHeaders"/> 在 <see cref="ExportColumnHeaders"/> 里的下标
        /// （按列名自动定位，表头顺序 / 名字改了也不会错位）。
        /// </summary>
        private static readonly int[] ExportNumericColumnIndexes =
            ResolveExportNumericColumnIndexes();

        /// <summary>导出目录下的一级子文件夹名（&lt;AiAppData&gt;\output\AssyDwg）。</summary>
        private const string ExportOutputFolderName = "output";

        /// <summary>导出目录下的二级子文件夹名（&lt;AiAppData&gt;\output\AssyDwg）。</summary>
        private const string ExportDrawingFolderName = "AssyDwg";

        /// <summary>导出文件名前缀（<c>a_&lt;図番&gt;.xlsx</c>）。</summary>
        private const string ExportFileNamePrefix = "a_";

        /// <summary>
        /// 导出 Excel 的工作表名：固定写「アセンブリ図」（不再用図番 —— 客户按图纸种类认这张表，
        /// 図番 里有 <c>_</c> 之类字符时 sheet 名也不好看）。文件里只有这一张表。
        /// </summary>
        private const string ExportSheetName = "アセンブリ図";

        public PartBasicInfoForm()
            : this(null)
        {
        }

        /// <summary>
        /// 创建部品基本信息画面。
        /// </summary>
        /// <param name="databasePath">
        /// 数据库文件路径（&lt;所选 PDF 文件夹&gt;\AiAppData\MSIF_Parts.db，由主画面传入）；
        /// 为空时按 App.config / 程序目录解析。
        /// </param>
        public PartBasicInfoForm(string databasePath)
        {
            InitializeComponent();
            InitializePartsGrid();
            InitializePartsContextMenu();

            // 按钮 / 列头 / 右键菜单 / 标签等文字的中日切换（见 Services\UiLocalization.cs）
            UiLocalization.Attach(this);

            _databasePath = string.IsNullOrWhiteSpace(databasePath) ? null : databasePath.Trim();

            // 部品基本信息画面全局只保留一个：若此前还有活着的旧画面（引用丢了 / 直接 new 过），
            // 先把它关掉再登记这一个（旧画面有未保存的修改时由它自己询问；
            // 用户选「取消」就保留旧画面，主画面那边也不会再新建第二个 —— 见 Form1.ShowPartInfo）
            PartBasicInfoForm previous = FindOpenInstance();

            if (previous != null && !ReferenceEquals(previous, this))
            {
                previous.Close();
            }

            _currentInstance = this;
        }

        /// <summary>当前显示的図番。</summary>
        public string DrawingNo
        {
            get { return _drawingNo; }
        }

        /// <summary>当前显示的 PDF 完整路径。</summary>
        public string PdfPath
        {
            get { return _pdfPath; }
        }

        /// <summary>
        /// 本画面使用的数据库路径（为空 = 按 App.config / 程序目录解析）。
        /// 主画面在打开前用它判断「图纸文件夹换了没有」——换了就关掉重建，免得写进上一个项目的库。
        /// </summary>
        public string DatabasePath
        {
            get { return _databasePath; }
        }

        /// <summary>
        /// 取现在已经打开的部品基本信息画面（<b>全局只保留一个</b>）。
        /// <para>
        /// 先看 <see cref="_currentInstance"/>；引用丢了也能再从 <see cref="Application.OpenForms"/>
        /// 里把活着的画面找回来（并重新记成当前画面）。
        /// </para>
        /// <para>
        /// 主画面（<c>Form1.ShowPartInfo</c>）在打开【部品信息】之前先调这个方法：
        /// 已经有窗口就<b>只切换图番</b>（<see cref="ShowDrawing"/>），
        /// 只有真的没有窗口时才 <c>new</c> —— 所以不管点多少次、换多少张图，
        /// 屏幕上始终只有一个部品基本信息画面。
        /// </para>
        /// </summary>
        /// <returns>已打开的部品基本信息画面；没有打开时返回 <c>null</c>。</returns>
        internal static PartBasicInfoForm FindOpenInstance()
        {
            PartBasicInfoForm current = _currentInstance;

            if (current != null && !current.IsDisposed)
            {
                return current;
            }

            // 兜底：引用丢了（例如在别处直接 new 出来的）也扫一遍已经打开的窗口
            foreach (Form open in Application.OpenForms)
            {
                PartBasicInfoForm candidate = open as PartBasicInfoForm;

                if (candidate != null && !candidate.IsDisposed)
                {
                    _currentInstance = candidate;

                    return candidate;
                }
            }

            _currentInstance = null;

            return null;
        }

        /// <summary>
        /// 主画面（<see cref="Form1"/>）的引用：只用于<b>窗口摆放 / z 顺序</b>判断
        /// （PDF 预览窗口要避开主画面所在的屏幕），<b>不建立 Owner 关系</b>。
        /// <para>
        /// 不能设 <see cref="Form.Owner"/>：Owned 窗口永远浮在 Owner 之上，
        /// 本画面就会「一直保持在主画面前边」，点主画面也压不下去。
        /// </para>
        /// </summary>
        internal Form MainWindow { get; set; }

        /// <summary>
        /// 切换到指定 PDF 对应的部品基本信息（重新从数据库读取），
        /// 并让 PDF 预览窗口同时显示该 PDF（两个画面成对显示）。
        /// 若有未保存的修改，先询问是否保存。
        /// </summary>
        /// <param name="pdfFilePath">PDF 完整路径（図番 = 文件名去扩展名）。</param>
        public void ShowDrawing(string pdfFilePath)
        {
            if (!ConfirmUnsavedChanges("切换图番"))
            {
                return;
            }

            // 换図番后表格会整个重填：先关掉「选择部材名」窗口，免得写到下一个図番的行上
            CloseMemberNamePicker();

            string newPath = pdfFilePath == null ? string.Empty : pdfFilePath.Trim();
            bool drawingChanged = !string.Equals(_pdfPath, newPath, StringComparison.OrdinalIgnoreCase);

            _pdfPath = newPath;
            _drawingNo = PartBasicInfoRepository.ResolveDrawingNo(_pdfPath);

            UiLocalization.SetText(lblDrawingNo, "図番：" + (_drawingNo.Length == 0 ? "-" : _drawingNo));
            UiLocalization.SetText(lblPdfPath, "PDF：" + (_pdfPath.Length == 0 ? "-" : _pdfPath));
            UpdateTitle();

            LoadParts();

            if (drawingChanged)
            {
                // 与部品基本信息画面同时打开 PDF 预览（窗口未显示时延后到 OnShown）
                ShowPreviewWindow(false);
            }
            else
            {
                // 同一图番刷新：预览已打开时只同步文件，不抢焦点
                SyncPreviewDocument();
            }
        }

        /// <summary>
        /// 从 SQLite 读取当前図番的部品基本信息并填充表格（异常不会抛出，只显示在画面上）。
        /// </summary>
        private void LoadParts()
        {
            IList<PartBasicInfo> parts = new List<PartBasicInfo>();
            string databasePath = _databasePath ?? PartBasicInfoRepository.ResolveDatabasePath();
            string error = null;

            if (_drawingNo.Length == 0)
            {
                error = "无法从 PDF 文件名解析出図番，请确认文件路径。";
            }
            else if (!File.Exists(databasePath))
            {
                error = "数据库尚未创建（请先执行【CMD 分析】或【AI 分析】）：\n" + databasePath;
            }
            else
            {
                try
                {
                    using (PartBasicInfoRepository repository = new PartBasicInfoRepository(databasePath))
                    {
                        parts = repository.Query(_drawingNo);
                    }
                }
                catch (Exception ex)
                {
                    error = "读取数据库失败：" + ex.Message;
                }
            }

            UiLocalization.SetText(lblDatabase, "数据库：" + databasePath);

            SetDirty(false);
            PopulateParts(parts);

            if (error != null)
            {
                UiLocalization.SetText(lblSummary, "读取失败");
                lblSummary.ForeColor = Color.FromArgb(198, 40, 40);
                UiLocalization.SetText(lblEmpty, error);
            }
            else
            {
                lblSummary.ForeColor = Color.FromArgb(52, 73, 94);
                UiLocalization.SetText(lblEmpty,
                    "该図番尚未登录部品基本信息。\n可点击【追加行】手工录入，或先对该 PDF 执行【AI 分析】。");
            }

            lblEmpty.Visible = parts.Count == 0;
            dgvParts.Visible = parts.Count > 0;
        }

        /// <summary>把部品明细填入表格（填表期间不产生脏标记）。</summary>
        private void PopulateParts(IList<PartBasicInfo> parts)
        {
            // 换图 / 重新载入时先把「部品名」候选列表收掉（旧的候选项 / 编辑框引用都已经失效）
            DetachPartNameCandidateEditor();

            // 「部材部品」表可能刚被保存流程（RegisterRules）写过 → 丢掉缓存，下次重新读
            InvalidateMemberPartCatalogCache();

            _suppressGridEvents = true;

            try
            {
                dgvParts.Rows.Clear();

                for (int index = 0; index < parts.Count; index++)
                {
                    PartBasicInfo part = parts[index];
                    string status = IsKnownVerifyStatus(part.VerifyStatus)
                        ? part.VerifyStatus
                        : PartVerifyStatus.NotChecked;

                    int rowIndex = dgvParts.Rows.Add(
                        part.Number.ToString(),     // 番号
                        part.MemberName,            // 部材名
                        part.PartName,              // 部品名
                        part.FrameKind,             // 区分
                        part.Quantity,              // 数量
                        part.CutSize,               // 切断寸法
                        part.PartCode,              // 部品コード
                        part.SourceTag,             // 参照元
                        part.HeightMin,             // H下限
                        part.HeightMax,             // H上限
                        part.WidthMin,              // W下限
                        part.WidthMax,              // W上限
                        part.UsageKind,             // 使い分け
                        part.SpecialNote,           // 特注
                        part.Page.ToString(),       // 页码
                        part.PartDrawingNo,         // 部品図番
                        part.BasicSize,             // 基本寸法
                        status,                     // 校验状态
                        part.Remark,                // 备注
                        part.SourceKind,            // 数据来源
                        part.LoginTime,             // 登录时间
                        (index + 1).ToString());    // No

                    DataGridViewRow row = dgvParts.Rows[rowIndex];
                    row.Tag = part;
                    // 整行背景 = 数据来源 / 特注 / 是否被分割（淡绿 / 淡蓝 / 淡粉 / 淡紫）
                    ApplyRowStyle(row);

                    // 部品コード 为空的行：按部品名 从部品マスタ 推定，并标粉红色提醒确认
                    ApplyPartCodeGuess(row, true);
                }
            }
            finally
            {
                _suppressGridEvents = false;
            }

            RefreshGridSummary();

            // 表格重填了：行号对应的还是别的记录 → 允许对新表的第一行也闪红框
            _lastFlashRowIndex = -1;
        }

        /// <summary>
        /// 初始化部品明细表格的列（与 <see cref="PopulateParts"/> 的填充顺序一致）。
        /// 前 14 列是业务必需列，按「番号 / 部材名 / 部品名 / 区分 / 数量 / 切断寸法 / 部品コード /
        /// 参照元 / H下限 / H上限 / W下限 / W上限 / 使い分け / 特注」的顺序优先显示，其余为辅助列。
        /// 最前面还固定一列「行号」（<c>colNo</c>，Excel 的行号栏那样，只读、点一下选中整行）；
        /// 它在列集合里仍放在最后（<see cref="PopulateParts"/> / <c>InsertBlankRow</c> 的位置参数不用改），
        /// 只靠 <c>DisplayIndex = 0</c> + <c>Frozen</c> 显示在最左侧。
        /// 除「行号」「数据来源」「登录时间」以外均可编辑；「校验状态」为下拉选择。
        /// 「参照元」画面上可以手工编辑，但<b>登录数据库时统一写成「図番」的前 10 位</b>
        /// （见 <see cref="PartBasicInfoRepository.ReferenceTagLength"/> 与 <c>ResolveReferenceTag</c>），
        /// 保存后画面重新载入会显示回「図番」的前 10 位。
        /// </summary>
        private void InitializePartsGrid()
        {
            dgvParts.AutoGenerateColumns = false;
            dgvParts.Columns.Clear();

            // ── 业务必需列（优先显示）─────────────────────────────────────
            AddColumn("colNumber", "番号", 46, DataGridViewContentAlignment.MiddleCenter, false);
            // 部材名：用「番号」在 parts_info.json 的 callouts 里找圈号后，取该圈号所在零件图的
            // texts（去掉数字、去掉「XX部」）用 | 连接；程序自动填，也允许在画面上修正
            AddColumn("colMemberName", "部材名", 120, DataGridViewContentAlignment.MiddleLeft, false);
            AddColumn("colPartName", "部品名", 170, DataGridViewContentAlignment.MiddleLeft, false);
            AddColumn("colFrameKind", "区分", 66, DataGridViewContentAlignment.MiddleCenter, false);
            AddColumn("colQuantity", "数量", 56, DataGridViewContentAlignment.MiddleCenter, false);
            AddColumn("colCutSize", "切断寸法", 100, DataGridViewContentAlignment.MiddleLeft, false);
            AddColumn("colPartCode", "部品コード", 102, DataGridViewContentAlignment.MiddleLeft, false);
            // 参照元 = 该行的「図番」的前 10 位：画面上允许手工编辑（对照图纸 / 导出 Excel 时方便），
            // 但登录数据库时统一由 PartBasicInfoRepository.BindInsertParameters 按 ResolveReferenceTag
            // 覆盖成「図番」的前 10 位（老数据库打开时也会把不等于该值的行改成同样的前 10 位），
            // 所以点【保存】后画面重新载入会显示回「図番」的前 10 位；
            // 来源分类看后面的「数据来源」列
            AddColumn("colSourceTag", "参照元", 92, DataGridViewContentAlignment.MiddleCenter, false);
            // 表头提示：说清楚「能改，但保存后会被 図番 前 10 位 覆盖」
            dgvParts.Columns["colSourceTag"].HeaderCell.ToolTipText =
                "参照元 = 这一行的「図番」的前 10 位（登录数据库时由程序统一写入）。\n" +
                "画面上可以手工编辑（导出 Excel 用的是画面上的值），\n" +
                "但点【保存】后会改回「図番」的前 10 位。";
            AddColumn("colHeightMin", "H下限", 64, DataGridViewContentAlignment.MiddleCenter, false);
            AddColumn("colHeightMax", "H上限", 64, DataGridViewContentAlignment.MiddleCenter, false);
            AddColumn("colWidthMin", "W下限", 64, DataGridViewContentAlignment.MiddleCenter, false);
            AddColumn("colWidthMax", "W上限", 64, DataGridViewContentAlignment.MiddleCenter, false);
            AddColumn("colUsageKind", "使い分け", 96, DataGridViewContentAlignment.MiddleLeft, false);
            AddColumn("colSpecialNote", "特注", 120, DataGridViewContentAlignment.MiddleLeft, false);

            // ── 辅助列 ───────────────────────────────────────────────────
            AddColumn("colPage", "页码", 46, DataGridViewContentAlignment.MiddleCenter, false);
            AddColumn("colPartDrawingNo", "部品図番", 118, DataGridViewContentAlignment.MiddleLeft, false);
            AddColumn("colBasicSize", "基本寸法", 100, DataGridViewContentAlignment.MiddleLeft, false);
            AddVerifyStatusColumn("colVerifyStatus", "校验状态", 88);
            AddColumn("colRemark", "备注", 170, DataGridViewContentAlignment.MiddleLeft, false);
            // 数据来源 = 这行是哪个功能写的（CMD分析・アッセンブリ図 / CMD分析・要領図 / CMD分析・特注品.json / AI分析 / 手工追加；
            // 升级前写入的旧值 CMD分析・tz.json 也按第 4 步的行处理），由程序写入，画面不可编辑
            AddColumn("colSourceKind", "数据来源", 140, DataGridViewContentAlignment.MiddleCenter);
            // 行背景色的图例（鼠标停在「数据来源」表头上显示）：颜色含义见 ApplyRowStyle
            dgvParts.Columns["colSourceKind"].HeaderCell.ToolTipText =
                "行背景色（同一行满足多个时按此顺序取第一个）：\n" +
                "  淡绿 = 数据来源「CMD分析・アッセンブリ図」\n" +
                "  淡蓝 = 数据来源「CMD分析・要領図」\n" +
                "  淡粉 = 特注（特注 = 1）\n" +
                "  淡紫 = 被分割的行（备注含「位置：」，第 2 步按圈号拆出来的）\n" +
                "  白色 = 其它（特注品.json / AI 分析 / 手工追加）\n" +
                "「部品コード」单元格的粉红色 = 按部品名 从部品マスタ 推定的值（需确认）";
            AddColumn("colLoginTime", "登录时间", 130, DataGridViewContentAlignment.MiddleCenter);
            // 行号 = 画面行号（Excel 最左侧那一列）：只读，追加 / 删除行后自动重排（RenumberRows），
            // 点这一格 = 选中整行（见 DgvParts_CellClick → SelectWholeRow）。
            // 列集合里仍放在最后 —— Rows.Add / Rows.Insert 的位置参数是按「列集合顺序」传值的；
            // 显示顺序单独用 DisplayIndex = 0 指定，再加 Frozen（横向滚动时行号不跟着跑掉）。
            AddColumn(RowNumberColumnName, "行号", 46, DataGridViewContentAlignment.MiddleCenter);
            DataGridViewColumn rowNumberColumn = dgvParts.Columns[RowNumberColumnName];
            rowNumberColumn.DisplayIndex = 0;
            rowNumberColumn.Frozen = true;
            rowNumberColumn.DefaultCellStyle.BackColor = RowNumberBackColor;
            rowNumberColumn.DefaultCellStyle.ForeColor = RowNumberForeColor;
            rowNumberColumn.HeaderCell.ToolTipText =
                "画面行号（追加 / 删除行后自动重排，不可编辑）。\n" +
                "点这一格 = 选中整行；按住 Ctrl 点可以多选，【删除行】一次删多行。";
        }

        /// <summary>添加一个文本列。</summary>
        /// <param name="readOnly">
        /// true = 只读（行号、数据来源、登录时间等由程序维护的列）；
        /// 「参照元」虽然登录数据库时由程序覆盖成「図番」前 10 位，但画面上允许手工编辑，所以传 false。
        /// </param>
        private void AddColumn(string name, string headerText, int width,
            DataGridViewContentAlignment alignment, bool readOnly = true)
        {
            DataGridViewTextBoxColumn column = new DataGridViewTextBoxColumn();
            column.Name = name;
            column.HeaderText = headerText;
            column.Width = width;
            column.ReadOnly = readOnly;
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
            column.DefaultCellStyle.Alignment = alignment;
            column.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            dgvParts.Columns.Add(column);
        }

        /// <summary>添加「校验状态」下拉列（候选项 = <see cref="PartVerifyStatus.All"/>）。</summary>
        private void AddVerifyStatusColumn(string name, string headerText, int width)
        {
            DataGridViewComboBoxColumn column = new DataGridViewComboBoxColumn();
            column.Name = name;
            column.HeaderText = headerText;
            column.Width = width;
            column.ReadOnly = false;
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
            column.FlatStyle = FlatStyle.Flat;
            column.DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton;
            column.Items.AddRange(PartVerifyStatus.All);
            column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            dgvParts.Columns.Add(column);
        }

        /// <summary>
        /// 表格的右键菜单（Excel 那样）：<b>在上方插入行</b> / <b>在下方插入行</b> / <b>删除行</b>。
        /// 作用对象 = 右键点到的行（右键时先把那一格设为当前选中格，见 <see cref="DgvParts_CellMouseDown"/>）；
        /// 具体动作和按钮【追加行】/【删除行】完全一样（<see cref="InsertBlankRow"/> / <see cref="DeleteSelectedRows"/>），
        /// 所以确认提示、未保存标记、序号重排都一致。
        /// </summary>
        private void InitializePartsContextMenu()
        {
            _partsMenu = new ContextMenuStrip();
            _partsMenu.ShowItemToolTips = true;

            _menuInsertRowAbove = new ToolStripMenuItem("在上方插入行");
            _menuInsertRowAbove.Click += MenuInsertRowAbove_Click;

            _menuInsertRowBelow = new ToolStripMenuItem("在下方插入行");
            _menuInsertRowBelow.Click += MenuInsertRowBelow_Click;

            _menuDeleteRow = new ToolStripMenuItem("删除行");
            _menuDeleteRow.Click += MenuDeleteRow_Click;

            _partsMenu.Items.Add(_menuInsertRowAbove);
            _partsMenu.Items.Add(_menuInsertRowBelow);
            _partsMenu.Items.Add(new ToolStripSeparator());
            _partsMenu.Items.Add(_menuDeleteRow);

            // 每次弹出前按当前状态启用 / 禁用（一行都没有时不能删；还没有図番时什么都不能做）
            _partsMenu.Opening += PartsMenu_Opening;

            dgvParts.ContextMenuStrip = _partsMenu;

            // 一行都没有时表格被 lblEmpty 挡着：让它也弹同一个菜单
            //（这时没有参照行，「上 / 下方插入」都等于追加到末尾 = 建第一行）
            lblEmpty.ContextMenuStrip = _partsMenu;
        }

        /// <summary>
        /// 【追加行】按钮：在选中行的<b>下方</b>插入一行（未选中任何行时追加到表格末尾）。
        /// </summary>
        private void BtnAddRow_Click(object sender, EventArgs e)
        {
            InsertBlankRow(false);
        }

        /// <summary>
        /// 在选中行的上方 / 下方插入一行空白行（右键菜单「在上方插入行」「在下方插入行」；
        /// 【追加行】按钮 = 在下方插入）。插完直接进入「部品名」的编辑。
        /// <para>
        /// 页码沿用参照行，番号取其下一个（和原来的【追加行】一致）；
        /// 表格里一行都没有时按「页码 1 / 番号 1」建第一行。
        /// </para>
        /// </summary>
        /// <param name="above">true = 插在选中行的上方；false = 插在选中行的下方（没有选中行时追加到末尾）。</param>
        private void InsertBlankRow(bool above)
        {
            if (_drawingNo.Length == 0)
            {
                ShowMessage("无法从 PDF 文件名解析出図番，不能追加部品信息。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            dgvParts.EndEdit();

            // 参照行 = 选中行（往上插取最靠上的、往下插取最靠下的）；没有选中行时取当前单元格所在行；
            // 都没有（或表是空的）就追加到末尾
            DataGridViewRow anchor = GetInsertAnchorRow(!above);
            int insertIndex;

            if (anchor == null)
            {
                insertIndex = dgvParts.Rows.Count;
            }
            else
            {
                insertIndex = above ? anchor.Index : anchor.Index + 1;
            }

            int page = 1;
            int number = 1;

            if (anchor != null)
            {
                int anchorPage;
                int anchorNumber;

                if (int.TryParse(GetCellText(anchor, "colPage"), out anchorPage) && anchorPage > 0)
                {
                    page = anchorPage;
                }

                if (int.TryParse(GetCellText(anchor, "colNumber"), out anchorNumber) && anchorNumber > 0)
                {
                    number = anchorNumber + 1;
                }
            }

            _suppressGridEvents = true;
            DataGridViewRow row;

            try
            {
                dgvParts.Visible = true;
                lblEmpty.Visible = false;

                dgvParts.Rows.Insert(insertIndex,
                    number.ToString(),                  // 番号
                    string.Empty,                       // 部材名
                    string.Empty,                       // 部品名
                    string.Empty,                       // 区分
                    string.Empty,                       // 数量
                    string.Empty,                       // 切断寸法
                    string.Empty,                       // 部品コード
                    string.Empty,                       // 参照元（保存时按「手工追加」写入）
                    string.Empty,                       // H下限
                    string.Empty,                       // H上限
                    string.Empty,                       // W下限
                    string.Empty,                       // W上限
                    string.Empty,                       // 使い分け
                    string.Empty,                       // 特注
                    page.ToString(),                    // 页码
                    string.Empty,                       // 部品図番
                    string.Empty,                       // 基本寸法
                    PartVerifyStatus.NotChecked,        // 校验状态
                    string.Empty,                       // 备注
                    string.Empty,                       // 数据来源
                    string.Empty,                       // 登录时间
                    (insertIndex + 1).ToString());      // No

                row = dgvParts.Rows[insertIndex];
                row.Tag = null;
                ApplyRowStyle(row);

                // 新行的「部品コード」是空的 → 先标粉红色（填了部品名 之后会自动推定）
                ApplyPartCodeGuess(row, false);
                dgvParts.CurrentCell = row.Cells["colPartName"];
            }
            finally
            {
                _suppressGridEvents = false;
            }

            SetDirty(true);
            RenumberRows();

            if (dgvParts.IsHandleCreated)
            {
                dgvParts.BeginEdit(true);
            }
        }

        /// <summary>
        /// 取「插入行 / 追加行」的参照行：优先取选中的行（<paramref name="bottomMost"/> = <c>true</c> 取最靠下的一行，
        /// <c>false</c> 取最靠上的一行），没有选中行时取当前单元格所在行；表格为空时返回 <c>null</c>
        /// （表示追加到末尾）。
        /// </summary>
        /// <param name="bottomMost">true = 选中的多行里取最靠下的；false = 取最靠上的。</param>
        private DataGridViewRow GetInsertAnchorRow(bool bottomMost)
        {
            DataGridViewRow anchor = null;

            foreach (DataGridViewRow row in GetSelectedRows())
            {
                if (anchor == null ||
                    (bottomMost ? row.Index > anchor.Index : row.Index < anchor.Index))
                {
                    anchor = row;
                }
            }

            if (anchor == null && dgvParts.CurrentCell != null)
            {
                DataGridViewRow currentRow = dgvParts.CurrentCell.OwningRow;

                if (currentRow != null && !currentRow.IsNewRow)
                {
                    anchor = currentRow;
                }
            }

            return anchor;
        }

        /// <summary>
        /// 当前「选中的行」：表格是单元格选中模式（<c>DataGridViewSelectionMode.CellSelect</c>）时
        /// <c>SelectedRows</c> 是空的，所以按<b>选中的单元格所在的行</b>收集（同一行只取一次）。
        /// 整行选中模式（或用户点了「行号」列）时退回 <c>SelectedRows</c>。
        /// </summary>
        /// <returns>选中的行（不含新增行；一行都没选中时返回空列表）。</returns>
        private List<DataGridViewRow> GetSelectedRows()
        {
            List<DataGridViewRow> rows = new List<DataGridViewRow>();
            List<int> usedIndexes = new List<int>();

            foreach (DataGridViewCell cell in dgvParts.SelectedCells)
            {
                if (cell == null || cell.RowIndex < 0 || usedIndexes.Contains(cell.RowIndex))
                {
                    continue;
                }

                DataGridViewRow row = dgvParts.Rows[cell.RowIndex];

                if (row.IsNewRow)
                {
                    continue;
                }

                usedIndexes.Add(cell.RowIndex);
                rows.Add(row);
            }

            if (rows.Count == 0)
            {
                foreach (DataGridViewRow row in dgvParts.SelectedRows)
                {
                    if (!row.IsNewRow)
                    {
                        rows.Add(row);
                    }
                }
            }

            return rows;
        }

        /// <summary>「删除行」：删除选中单元格所在的行（按住 Ctrl 选中多格时可一次删多行）。</summary>
        private void BtnDeleteRow_Click(object sender, EventArgs e)
        {
            DeleteSelectedRows();
        }

        /// <summary>删除当前选中的行，并刷新序号与统计。</summary>
        private void DeleteSelectedRows()
        {
            dgvParts.EndEdit();

            // 表格是「单元格选中」模式，SelectedRows 通常是空的 → 按选中的单元格收集行
            List<DataGridViewRow> targets = GetSelectedRows();

            if (targets.Count == 0 && dgvParts.CurrentCell != null)
            {
                DataGridViewRow currentRow = dgvParts.CurrentCell.OwningRow;

                if (currentRow != null && !currentRow.IsNewRow)
                {
                    targets.Add(currentRow);
                }
            }

            if (targets.Count == 0)
            {
                ShowMessage("请先选中要删除的行。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (ShowMessage(
                    "确定删除选中的 " + targets.Count + " 行吗？\n（点击【保存】后才会真正从数据库删除）",
                    "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }

            _suppressGridEvents = true;

            try
            {
                foreach (DataGridViewRow row in targets)
                {
                    dgvParts.Rows.Remove(row);
                }
            }
            finally
            {
                _suppressGridEvents = false;
            }

            RenumberRows();
            SetDirty(true);
        }

        /// <summary>右键菜单「在上方插入行」：在右键那一行的上面插一行空白行。</summary>
        private void MenuInsertRowAbove_Click(object sender, EventArgs e)
        {
            InsertBlankRow(true);
        }

        /// <summary>右键菜单「在下方插入行」：在右键那一行的下面插一行空白行。</summary>
        private void MenuInsertRowBelow_Click(object sender, EventArgs e)
        {
            InsertBlankRow(false);
        }

        /// <summary>右键菜单「删除行」：和【删除行】按钮一样删掉选中的行（会先确认）。</summary>
        private void MenuDeleteRow_Click(object sender, EventArgs e)
        {
            DeleteSelectedRows();
        }

        /// <summary>
        /// 右键菜单打开前：按当前状态启用 / 禁用各项，并把「删除行」的文字换成这次要删的行数。
        /// </summary>
        private void PartsMenu_Opening(object sender, CancelEventArgs e)
        {
            bool hasDrawingNo = _drawingNo.Length > 0;

            // 没有図番时空白行存不进数据库（保存会报「无法解析出図番」）→ 两个插入项置灰
            _menuInsertRowAbove.Enabled = hasDrawingNo;
            _menuInsertRowBelow.Enabled = hasDrawingNo;

            int targetRowCount = CountMenuTargetRows();

            _menuDeleteRow.Enabled = hasDrawingNo && targetRowCount > 0;
            _menuDeleteRow.Text = targetRowCount > 1 ? "删除选中的 " + targetRowCount + " 行" : "删除行";

            DataGridViewRow anchorAbove = GetInsertAnchorRow(false);
            DataGridViewRow anchorBelow = GetInsertAnchorRow(true);

            _menuInsertRowAbove.ToolTipText = anchorAbove == null
                ? "追加到表格末尾（没有选中行）"
                : "在第 " + (anchorAbove.Index + 1) + " 行的上方插入一行空白行";

            _menuInsertRowBelow.ToolTipText = anchorBelow == null
                ? "追加到表格末尾（没有选中行）"
                : "在第 " + (anchorBelow.Index + 1) + " 行的下方插入一行空白行";
        }

        /// <summary>
        /// 右键点表格：先把「这一格」设为当前选中格，再弹出右键菜单
        /// （和 Excel 一样，菜单作用在右键的那一行；Ctrl 多选时保持多选，一次删多行）。
        /// <para>
        /// 点的是表头（<c>RowIndex &lt; 0</c>）时不动当前选中格；
        /// 右键不进入编辑 / 不打开「选择部材名」窗口 —— <see cref="DgvParts_CellClick"/> 靠
        /// <see cref="_rightClickInProgress"/> 区分左右键。
        /// </para>
        /// </summary>
        private void DgvParts_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            _rightClickInProgress = e.Button == MouseButtons.Right;

            if (!_rightClickInProgress || e.RowIndex < 0 || e.ColumnIndex < 0)
            {
                return;
            }

            if (e.RowIndex >= dgvParts.Rows.Count || e.ColumnIndex >= dgvParts.Columns.Count)
            {
                return;
            }

            DataGridViewCell cell = dgvParts.Rows[e.RowIndex].Cells[e.ColumnIndex];

            if (cell == null || cell.Selected)
            {
                // 已经选中的格子（Ctrl 多选的那几行）保持原样：删除时一次删这几行
                return;
            }

            // 正在编辑别的格子时先提交，别把用户刚打的字丢掉
            if (dgvParts.IsCurrentCellInEditMode)
            {
                try
                {
                    dgvParts.CommitEdit(DataGridViewDataErrorContexts.Commit);
                }
                catch (Exception)
                {
                    // 提交失败（下拉列的值不在候选里等）→ 照样把这一格设为当前格
                }
            }

            try
            {
                dgvParts.CurrentCell = cell;
            }
            catch (Exception)
            {
                // 设置失败（表格不可用等）→ 保持原来的选中格，菜单仍按现在的选中行工作
            }
        }

        /// <summary>
        /// 右键菜单「删除行」这次会删几行：优先选中的行（可多行），
        /// 没有选中的行时算当前单元格所在行；都没有时返回 0（菜单项置灰）。
        /// </summary>
        private int CountMenuTargetRows()
        {
            List<DataGridViewRow> rows = GetSelectedRows();

            if (rows.Count > 0)
            {
                return rows.Count;
            }

            DataGridViewCell cell = dgvParts.CurrentCell;

            if (cell != null && cell.OwningRow != null && !cell.OwningRow.IsNewRow)
            {
                return 1;
            }

            return 0;
        }

        /// <summary>「刷新」：重新从数据库读取当前図番的部品明细（会丢弃未保存的修改）。</summary>
        private void BtnRefresh_Click(object sender, EventArgs e)
        {
            if (!ConfirmUnsavedChanges("重新载入"))
            {
                return;
            }

            LoadParts();
        }

        /// <summary>「保存」：把表格内容写回数据库（该図番整体替换）。</summary>
        private void BtnSave_Click(object sender, EventArgs e)
        {
            SaveParts();
        }

        /// <summary>
        /// 校验表格内容并整体保存到数据库，成功后重新载入画面。
        /// </summary>
        /// <returns>true = 已保存（或无修改）；false = 校验失败 / 保存失败 / 用户取消。</returns>
        private bool SaveParts()
        {
            if (_drawingNo.Length == 0)
            {
                ShowMessage("无法从 PDF 文件名解析出図番，不能保存部品信息。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            dgvParts.EndEdit();

            List<PartBasicInfo> parts = new List<PartBasicInfo>();
            List<string> errors = new List<string>();
            Dictionary<string, int> usedKeys = new Dictionary<string, int>();

            foreach (DataGridViewRow row in dgvParts.Rows)
            {
                if (row.IsNewRow)
                {
                    continue;
                }

                int rowNo = row.Index + 1;
                int page;
                int number;

                if (!int.TryParse(GetCellText(row, "colPage"), out page) || page < 0)
                {
                    errors.Add("第 " + rowNo + " 行：「页码」必须是不小于 0 的整数。");
                    continue;
                }

                if (!int.TryParse(GetCellText(row, "colNumber"), out number) || number <= 0)
                {
                    errors.Add("第 " + rowNo + " 行：「番号」必须是大于 0 的整数。");
                    continue;
                }

                // 唯一键与数据库索引一致：页码 + 番号 + 部品コード
                // （部品表「下表参照」展开后，同一番号会有多条不同部品コード的明细）
                string partCode = GetCellText(row, "colPartCode");
                string key = page.ToString() + "/" + number.ToString() + "/" + partCode;
                int firstRowNo;

                if (usedKeys.TryGetValue(key, out firstRowNo))
                {
                    errors.Add("第 " + rowNo + " 行：「页码 " + page + " / 番号 " + number +
                        " / 部品コード " + (partCode.Length == 0 ? "（空）" : partCode) +
                        "」与第 " + firstRowNo + " 行重复。");
                    continue;
                }

                usedKeys[key] = rowNo;

                PartBasicInfo origin = row.Tag as PartBasicInfo;

                PartBasicInfo part = new PartBasicInfo(
                    _drawingNo,
                    page,
                    number,
                    GetCellText(row, "colPartName"),
                    GetCellText(row, "colPartDrawingNo"),
                    GetCellText(row, "colFrameKind"),
                    GetCellText(row, "colQuantity"),
                    GetCellText(row, "colBasicSize"),
                    GetCellText(row, "colCutSize"),
                    GetCellText(row, "colPartCode"));

                string status = GetCellText(row, "colVerifyStatus");
                part.VerifyStatus = IsKnownVerifyStatus(status) ? status : PartVerifyStatus.NotChecked;
                // 部材名：画面上填 / 选的是什么就登录什么 —— 不按置换表 T_M_MergeName 置换
                // （置换只用于分析自动取得部材名的时点，见 PartBasicInfo.MemberName / SetMemberNameFromInput）
                part.SetMemberNameFromInput(GetCellText(row, "colMemberName"));
                part.HeightMin = GetCellText(row, "colHeightMin");
                part.HeightMax = GetCellText(row, "colHeightMax");
                part.WidthMin = GetCellText(row, "colWidthMin");
                part.WidthMax = GetCellText(row, "colWidthMax");
                part.UsageKind = GetCellText(row, "colUsageKind");
                part.SpecialNote = GetCellText(row, "colSpecialNote");
                part.Remark = GetCellText(row, "colRemark");

                if (origin != null)
                {
                    // 未修改的行沿用原来的来源与登录时间
                    part.SourceKind = origin.SourceKind ?? string.Empty;
                    // 「参照元」不取画面上的值（画面上可以手工编辑，只影响「保存 Excel」导出）：
                    // 登录数据库时由仓储统一按「図番」前 10 位覆盖，见 PartBasicInfoRepository.BindInsertParameters
                    part.SourceTag = origin.SourceTag ?? string.Empty;
                    part.SourceFile = origin.SourceFile ?? string.Empty;
                    part.PdfFile = string.IsNullOrEmpty(origin.PdfFile) ? _pdfPath : origin.PdfFile;
                    part.LoginTime = origin.LoginTime ?? string.Empty;
                    // 「部材名AI推荐」已不在画面上表示（AI 推荐机能已删除）：保存时沿用库里原来的值，
                    // 免得手工保存一次就把老数据清掉（这一列只作历史残留，不再参与任何逻辑）
                    part.MemberNameAiSuggestion = origin.MemberNameAiSuggestion ?? string.Empty;
                }
                else
                {
                    // 新追加的行：来源 = 手工追加；登录时间留空，由仓储写入当前时间
                    part.SourceKind = PartSourceKind.Manual;
                    part.SourceTag = PartSourceTag.Manual;
                    part.SourceFile = string.Empty;
                    part.PdfFile = _pdfPath;
                    part.LoginTime = string.Empty;
                }

                parts.Add(part);
            }

            if (errors.Count > 0)
            {
                ShowMessage("保存前请先修正以下问题：\n\n" + string.Join("\n", errors.ToArray()),
                    "无法保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            int blankCount = 0;

            foreach (PartBasicInfo part in parts)
            {
                if (part.PartName.Length == 0 && part.PartCode.Length == 0)
                {
                    blankCount++;
                }
            }

            if (blankCount > 0 &&
                ShowMessage("有 " + blankCount + " 行的「部品名」与「部品コード」都为空，仍要保存吗？",
                    "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return false;
            }

            if (parts.Count == 0 &&
                ShowMessage("表格中没有任何行，保存会清空该図番的全部部品明细。\n确定要继续吗？",
                    "确认清空", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return false;
            }

            string databasePath = _databasePath ?? PartBasicInfoRepository.ResolveDatabasePath();
            int written;

            try
            {
                using (PartBasicInfoRepository repository = new PartBasicInfoRepository(databasePath))
                {
                    written = repository.ReplaceByDrawing(_drawingNo, parts);
                }
            }
            catch (Exception ex)
            {
                ShowMessage("保存到数据库失败：\n" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            // 部品基本信息写库成功后，把这次确定的「部材名 + 部品名」当成新的对应规则登录进
            // 部材部品 数据库（D:\MSIFDB\MSIF_MemberParts.db：「その他」/ 含 | 的行不登录）。
            // 失败只提示，不影响上面的保存结果。
            string ruleMessage = MemberPartCatalogLoader.RegisterRules(parts);

            // 新登录的规则要立刻能出现在「选择部材名」窗口里 → 重新读一次内存里的 部材部品マスタ
            RefreshMemberNameCatalog();

            // 同一个时机，把画面上的「部品名 + 部材名 + 部品コード」登录 / 更新到 MSIF 库的
            // 部材 mapping 表（SQL Server 10.103.126.209 / MSIF 的 dbo.DL_BuhinBuzaiMapping，
            // 连接名 connJP2）。表的主键是 BuhinName + BuzaiName，部品CD = 画面上的「部品コード」
            // （BuhinCd），所以更新时会把部品CD 一起刷新；BuhinCd 为空时保留表里原来的值。
            // 同样失败只提示（连不上 / 没权限 / 表不存在），不影响部品基本信息的保存。
            bool buhinBuzaiUpdated;
            string buhinBuzaiMessage = BuhinBuzaiMappingRepository.Register(parts, out buhinBuzaiUpdated);

            // 部材 mapping 更新成功 → 重新读一次内存（部品名 / 部品CD → 部材名），
            // 刚登录的「部品名 + 部材名」下一次点「部材名」就能在推荐项里选到（失败只影响推荐项，不弹提示）
            if (buhinBuzaiUpdated)
            {
                BuhinBuzaiMappingCatalog.Reload();
            }

            LoadParts();

            ShowMessage("已保存 " + written + " 条部品基本信息。" +
                (ruleMessage.Length == 0 ? string.Empty : "\n" + ruleMessage) +
                (buhinBuzaiMessage.Length == 0 ? string.Empty : "\n" + buhinBuzaiMessage), "完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);

            return true;
        }

        /// <summary>
        /// 按列名把 <see cref="ExportNumericColumnHeaders"/> 映射成 <see cref="ExportColumnHeaders"/>
        /// 的下标（找不到的列名直接跳过；顺序与声明顺序一致）。
        /// </summary>
        private static int[] ResolveExportNumericColumnIndexes()
        {
            List<int> indexes = new List<int>(ExportNumericColumnHeaders.Length);

            for (int index = 0; index < ExportNumericColumnHeaders.Length; index++)
            {
                int columnIndex = Array.IndexOf(ExportColumnHeaders, ExportNumericColumnHeaders[index]);

                if (columnIndex >= 0)
                {
                    indexes.Add(columnIndex);
                }
            }

            return indexes.ToArray();
        }

        /// <summary>「保存 Excel」：把画面当前内容导出成 <c>a_&lt;図番&gt;.xlsx</c>。</summary>
        private void BtnSaveExcel_Click(object sender, EventArgs e)
        {
            ExportToExcel();
        }

        /// <summary>
        /// 把画面当前显示的内容（含尚未保存到数据库的修改）导出成
        /// <c>&lt;PDF 文件夹&gt;\AiAppData\output\AssyDwg\a_&lt;図番&gt;.xlsx</c>。
        ///
        /// <para>
        /// 只导出必要列（见 <see cref="ExportColumnNames"/>），行顺序 = 画面顺序；
        /// 空值单元格留空。不改数据库、不改画面内容。
        /// </para>
        ///
        /// <para>
        /// 数量 / 切断寸法 / H下限 / H上限 / W下限 / W上限 这 6 列写成<b>数字类型</b>单元格
        /// （见 <see cref="ExportNumericColumnIndexes"/>），Excel 里可直接排序 / 求和；
        /// 这 6 列里不是数字的值（如 <c>3-100</c>、<c>2枚</c>）仍然按文本写，内容不丢。
        /// 其余列全部按文本写（<c>3-100</c>、<c>0012</c> 之类不会被 Excel 转成日期 / 数字）。
        /// </para>
        ///
        /// <para>
        /// 工作表名固定写「アセンブリ図」（<see cref="ExportSheetName"/>），不再用図番 ——
        /// 文件里只有这一张表，図番 已经体现在文件名（<c>a_&lt;図番&gt;.xlsx</c>）上。
        /// </para>
        /// </summary>
        /// <returns>true = 已导出；false = 未导出（无図番 / 无数据 / 用户取消 / 写入失败）。</returns>
        private bool ExportToExcel()
        {
            if (_drawingNo.Length == 0)
            {
                ShowMessage("无法从 PDF 文件名解析出図番，不能导出 Excel。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            string outputDirectory = ResolveExcelOutputDirectory();

            if (outputDirectory == null)
            {
                ShowMessage(
                    "找不到 PDF 所在文件夹，无法确定导出目录：\n" +
                    (_pdfPath.Length == 0 ? "(未指定 PDF)" : _pdfPath),
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            dgvParts.EndEdit();

            List<string[]> rows = new List<string[]>();

            foreach (DataGridViewRow row in dgvParts.Rows)
            {
                if (row.IsNewRow)
                {
                    continue;
                }

                string[] values = new string[ExportColumnNames.Length];

                for (int index = 0; index < ExportColumnNames.Length; index++)
                {
                    values[index] = GetCellText(row, ExportColumnNames[index]);
                }

                rows.Add(values);
            }

            if (rows.Count == 0)
            {
                ShowMessage("当前図番没有任何部品明细，没有可导出的内容。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            string filePath = Path.Combine(outputDirectory, ExportFileNamePrefix + _drawingNo + ".xlsx");

            if (File.Exists(filePath) &&
                ShowMessage("文件已存在，要覆盖吗？\n\n" + filePath, "确认覆盖",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return false;
            }

            if (_isDirty &&
                ShowMessage(
                    "画面上还有未保存到数据库的修改。\n" +
                    "导出的是画面当前内容（含未保存的修改）。\n\n继续导出吗？",
                    "确认导出", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return false;
            }

            try
            {
                XlsxSheetWriter.WriteSheet(filePath, ExportSheetName, ExportColumnHeaders, rows,
                    ExportColumnWidths, ExportNumericColumnIndexes);
            }
            catch (IOException ex)
            {
                ShowMessage(
                    "无法写入 Excel 文件（可能正被 Excel 打开）：\n" + filePath + "\n\n" + ex.Message,
                    "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            catch (Exception ex)
            {
                ShowMessage("导出 Excel 失败：\n" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (ShowMessage(
                    "已导出 " + rows.Count + " 条到：\n" + filePath + "\n\n是否打开所在文件夹？",
                    "导出完成", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            {
                OpenFolder(outputDirectory);
            }

            return true;
        }

        /// <summary>
        /// 计算「保存 Excel」的导出目录（只计算路径，不创建目录）：
        /// <c>&lt;PDF 文件夹&gt;\AiAppData\output\AssyDwg</c>。
        /// 找不到 PDF 所在文件夹（未指定 PDF / 文件夹已删除）时返回 <c>null</c>。
        /// </summary>
        private string ResolveExcelOutputDirectory()
        {
            string pdfFolder = _pdfPath.Length == 0 ? null : Path.GetDirectoryName(_pdfPath);

            if (string.IsNullOrEmpty(pdfFolder) || !Directory.Exists(pdfFolder))
            {
                return null;
            }

            return Path.Combine(
                PdfSplitService.GetOutputRoot(pdfFolder),
                ExportOutputFolderName,
                ExportDrawingFolderName);
        }

        /// <summary>用资源管理器打开指定文件夹（失败只提示，不抛出）。</summary>
        /// <param name="folder">文件夹绝对路径。</param>
        private void OpenFolder(string folder)
        {
            try
            {
                Process.Start("explorer.exe", "\"" + folder + "\"");
            }
            catch (Exception ex)
            {
                ShowMessage("打开文件夹失败：\n" + ex.Message, "异常",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>「预览 PDF」：显示该図番对应的 PDF（窗口已打开时前置）。</summary>
        private void BtnPreviewPdf_Click(object sender, EventArgs e)
        {
            if (_pdfPath.Length == 0 || !File.Exists(_pdfPath))
            {
                ShowMessage(
                    "找不到对应的 PDF 文件：\n" + (_pdfPath.Length == 0 ? "(未指定)" : _pdfPath),
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ShowPreviewWindow(true);
        }

        /// <summary>
        /// 显示（必要时创建）PDF 预览窗口并让它显示当前图番的 PDF。
        /// 本画面尚未显示时延后到 <see cref="OnShown"/> 执行，避免窗口出现在错误的屏幕 / 位置。
        /// </summary>
        /// <param name="activatePreview">是否把预览窗口提到最前（用户点【预览 PDF】时为 true）。</param>
        private void ShowPreviewWindow(bool activatePreview)
        {
            if (_pdfPath.Length == 0 || !File.Exists(_pdfPath))
            {
                return;
            }

            if (!Visible)
            {
                _openPreviewOnShown = true;
                return;
            }

            bool created = _previewForm == null || _previewForm.IsDisposed;

            if (created)
            {
                _previewForm = new PdfPreviewForm();
                _previewForm.FormClosed += PreviewForm_FormClosed;
                // 不设 Owner（不用 Show(this)）：预览窗口是独立的顶层窗口，不会「始终浮在本画面之上」，
                // 本画面关闭时由 OnFormClosed 一并关闭预览窗口。
            }

            // 先摆位置再 Show：窗口直接出现在最终位置，不会先在屏幕中央闪一下再跳走
            bool placeEditor = !_editorPlaced;

            if (created && placeEditor)
            {
                // 首次打开：PDF 预览 → 主画面以外的屏幕（不和主画面挤在一块屏上），
                // 部品基本信息 → 主画面所在的屏幕（单屏时左右并排）
                _editorPlaced = true;
                WindowLayoutHelper.ArrangePreviewAndEditor(_previewForm, this, ResolveMainWindowForm());
            }
            else if (created)
            {
                // 用户关闭预览后再次打开：只把预览窗口放回「主画面以外的屏幕」
                WindowLayoutHelper.ArrangePreview(_previewForm, ResolveMainWindowForm());
            }
            else if (placeEditor)
            {
                // 预览窗口由主画面打开并已摆好，只把编辑画面移到主画面所在的屏幕
                _editorPlaced = true;
                WindowLayoutHelper.ArrangeEditor(this, ResolveMainWindowForm());
            }

            if (!_previewForm.Visible)
            {
                _previewForm.Show();
            }

            if (!string.Equals(_previewForm.CurrentPath, _pdfPath, StringComparison.OrdinalIgnoreCase))
            {
                // 换文档；activatePreview = false（随本画面自动打开）时不把预览窗口激活到前面
                _previewForm.ShowPdf(_pdfPath, activatePreview);
            }

            if (activatePreview)
            {
                _previewForm.BringToFront();
                _previewForm.Activate();
            }
            else
            {
                // 自动打开的预览窗口不抢焦点，但也不能一直压在主画面前面：
                // 和主画面有重叠时把它放到主画面后面（不重叠 = 另一块屏，不动 z 顺序）。
                WindowLayoutHelper.PlaceBehindMainWindowIfOverlapping(_previewForm, ResolveMainWindowForm());
            }
        }

        /// <summary>
        /// 摆放窗口时用的「主画面」：PDF 预览窗口要避开它所在的屏幕
        /// （多屏时不要把预览窗口开在主画面所在的那块屏上），并作为 z 顺序的比较对象。
        /// <para>
        /// 本画面由主画面（<see cref="Form1"/>）打开时用 <see cref="MainWindow"/>；
        /// 没有设置（单独打开 / 已释放）时退回 <see cref="Form.Owner"/>，
        /// 两者都没有时退回本画面自己 —— 预览窗口至少不会和编辑画面挤在同一块屏上。
        /// </para>
        /// </summary>
        /// <returns>主画面窗口（一定不为 <c>null</c>）。</returns>
        private Form ResolveMainWindowForm()
        {
            Form mainWindow = MainWindow;

            if (mainWindow != null && !mainWindow.IsDisposed)
            {
                return mainWindow;
            }

            Form owner = Owner;

            return owner != null && !owner.IsDisposed ? owner : this;
        }

        /// <summary>把已打开的 PDF 预览窗口同步到当前图番的 PDF（不抢焦点）。</summary>
        private void SyncPreviewDocument()
        {
            if (_previewForm == null || _previewForm.IsDisposed || _pdfPath.Length == 0)
            {
                return;
            }

            if (!string.Equals(_previewForm.CurrentPath, _pdfPath, StringComparison.OrdinalIgnoreCase))
            {
                // 只换文档，不激活窗口（预览画面不抢到编辑画面前面）
                _previewForm.ShowPdf(_pdfPath, false);
            }
        }

        /// <summary>
        /// 表格的选择变了（点格子 / 方向键移动）：把这一行的来源位置在 PDF 预览画面上闪 3 次红框
        /// （见 <see cref="FlashSelectedRowSource"/>）。
        /// </summary>
        private void DgvParts_SelectionChanged(object sender, EventArgs e)
        {
            if (_suppressGridEvents)
            {
                return;
            }

            FlashSelectedRowSource();
        }

        /// <summary>
        /// 在 PDF 预览画面上闪 3 次红框，标出当前行在图纸上的来源位置。
        /// <para>
        /// 定位文字按「部品図番 → 部品コード → 部品名」的顺序找（第一个能在该页找到的就算命中；
        /// 有部品図番 时它就是第 1 优先），
        /// 位置取 <see cref="PartBasicInfo.Page"/> 那一页（页码为空 / 非法时不动）。
        /// </para>
        /// <para>
        /// 只在本画面自己的预览窗口已经打开时闪（不主动创建新窗口）；<b>只改预览画面上的红框，
        /// 不动窗口</b> —— 不把预览窗口提到最前、不激活、不抢焦点，
        /// 窗口的前后关系与焦点完全保持原样（用户把预览窗口放在后面时也不会被拉到前面）。
        /// 同一行只闪一次（<see cref="_lastFlashRowIndex"/>），换图 / 重新载入后重置。
        /// </para>
        /// <para>
        /// <b>特注品的行不闪</b>（<see cref="IsSpecialOrderPartRow"/>：「特注」= 1，或「数据来源」= 特注品.json）：
        /// 这些部品来自特注品 Excel（<c>AiAppData\&lt;図番&gt;\特注品.csv</c>）的部品表，
        /// 图纸上不一定印着对应的部品コード / 部品名，闪了也多半找不到、或命中到别的部品上反而误导。
        /// </para>
        /// </summary>
        private void FlashSelectedRowSource()
        {
            if (_previewForm == null || _previewForm.IsDisposed || !_previewForm.Visible)
            {
                return;
            }

            // 预览画面上显示的必须是这张图（换了图还没同步时不要闪到别的 PDF 上）
            if (_pdfPath.Length == 0 ||
                !string.Equals(_previewForm.CurrentPath, _pdfPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            DataGridViewCell cell = dgvParts.CurrentCell;
            DataGridViewRow row = cell == null ? null : cell.OwningRow;

            if (row == null || row.IsNewRow || row.Index == _lastFlashRowIndex)
            {
                return;
            }

            if (IsSpecialOrderPartRow(row))
            {
                // 特注品（「特注」= 1 / 数据来源 = 特注品.json）不在图纸上定位 → 不闪；
                // 记成「已处理」，同一行里换格（SelectionChanged 会反复触发）时不再重复判断
                _lastFlashRowIndex = row.Index;
                return;
            }

            int pageIndex = ResolveRowPageIndex(row);

            if (pageIndex < 0)
            {
                // 不知道这一行来自哪一页（老数据 / 手工追加的行）→ 不闪
                return;
            }

            List<string> keywords = CollectRowSourceKeywords(row);

            if (keywords.Count == 0)
            {
                return;
            }

            _lastFlashRowIndex = row.Index;

            // 只闪红框，不动窗口的 z 顺序 / 焦点：
            // 以前这里用 WindowLayoutHelper.BringToFrontWithoutActivate 把预览窗口提到最前，
            // 每换一行提一次，窗口会在屏幕上跳一下（看起来像预览画面在闪），
            // 也会把用户故意放到后面的预览窗口又拉到前面 —— 已去掉。
            _previewForm.FlashTextHighlight(pageIndex, keywords);
        }

        /// <summary>
        /// 行的「页码」列换算成 PDF 预览用的页索引（0 起）：
        /// 空值 / 不是数字 / ≤ 0（手工追加的行）时返回 -1 = 不知道来自哪一页。
        /// </summary>
        private static int ResolveRowPageIndex(DataGridViewRow row)
        {
            int page;

            if (!int.TryParse(GetCellText(row, "colPage"), out page) || page <= 0)
            {
                return -1;
            }

            return page - 1;
        }

        /// <summary>
        /// 用来在 PDF 上定位这一行位置的候选文字（按优先级）：
        /// <b>部品図番 → 部品CD（部品コード）→ 部品名</b>。
        /// <para>
        /// <b>部品図番 第 1 优先</b>：这一行有部品図番（<c>colPartDrawingNo</c>）就用它去图纸上找 ——
        /// 图纸上印的是具体的图番，比部品CD 好认（部品CD 可能是 <c>MZA□968</c> 这种带可变位的模糊 CD，
        /// 图纸上印的却是 <c>MZA1-968</c>；而且同一张图上多个部品可能共用一族 CD）。
        /// 部品図番 在图纸上找不到时才退到部品CD，再找不到才退到部品名。
        /// 太短（1 个字符）的文字会到处命中，不参与定位。
        /// </para>
        /// <para>
        /// 同一个关键字在图纸上出现多处时，由 <see cref="PdfPreviewForm.FlashTextHighlight"/> 内部按
        /// 「同一行里还含有其它关键字（部品CD / 部品名）的个数」打分，取最像部品表那一行。
        /// </para>
        /// <para>
        /// 关键字里的 <c>□</c> / <c>○</c>（<c>〇</c> <c>◯</c> <c>△</c>）是<b>可变位</b>
        /// （库里写 <c>MZA□968</c>，图纸上印 <c>MZA1-968</c>），所以每个关键字都由
        /// <see cref="PdfPreviewForm.FlashTextHighlight"/> 再做一遍宽松匹配
        /// （可变位 = 任意 0～1 个字符，字符之间允许夹 <c>-</c> 等分隔符，关键字自带的分隔符也可缺省 ——
        /// 库 <c>HJ-1234</c> 能命中图纸 <c>HJ1234</c>）。
        /// 含可变位的模糊 CD 连宽松匹配也找不到时<b>不再往下退化</b>到部品名 —— 同族部品的部品名是共用的，
        /// 退过去只会闪到别的部品上。
        /// </para>
        /// </summary>
        private static List<string> CollectRowSourceKeywords(DataGridViewRow row)
        {
            List<string> keywords = new List<string>();

            // 部品図番 第 1 优先：图纸上印的是具体图番，比可能是模糊 CD（MZA□968）的部品CD 好认
            AddSourceKeyword(keywords, GetCellText(row, "colPartDrawingNo"));
            AddSourceKeyword(keywords, GetCellText(row, PartCodeColumnName));
            AddSourceKeyword(keywords, GetCellText(row, PartNameColumnName));

            return keywords;
        }

        /// <summary>把非空、长度 ≥ 2 的关键字加进列表（重复的不加）。</summary>
        private static void AddSourceKeyword(List<string> keywords, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            string keyword = value.Trim();

            if (keyword.Length < 2 || keywords.Contains(keyword))
            {
                return;
            }

            keywords.Add(keyword);
        }

        /// <summary>
        /// 该行是不是<b>特注品</b>（选到这种行时不在 PDF 上闪红框，见 <see cref="FlashSelectedRowSource"/>）：
        /// <list type="bullet">
        ///   <item>「特注」= <see cref="PartBasicInfo.SpecialNoteMark"/>（<c>1</c>）—— 画面上淡粉色的行
        ///     （特注品.json 的追加対応特注寸法，判定同 <see cref="IsSpecialNoteRow"/>）；</item>
        ///   <item>「数据来源」= <see cref="PartSourceKind.CmdTz"/>（<c>CMD分析・特注品.json</c>）或旧值
        ///     <see cref="PartSourceKind.LegacyCmdTz"/>（<c>CMD分析・tz.json</c>，升级前写进库的行）。</item>
        /// </list>
        /// <para>
        /// 特注品的部品来自特注品 Excel（<c>AiAppData\&lt;図番&gt;\特注品.csv</c>）的部品表，
        /// 图纸（PDF）上不一定印着对应的部品コード / 部品名：闪红框要么找不到、要么命中到别的部品上反而误导，
        /// 所以这种行直接不闪（和「找不到就不闪」的静默失败同一个思路）。
        /// </para>
        /// </summary>
        /// <param name="row">要判断的行（<c>null</c> / 新增行时返回 false）。</param>
        private static bool IsSpecialOrderPartRow(DataGridViewRow row)
        {
            if (row == null || row.IsNewRow)
            {
                return false;
            }

            if (IsSpecialNoteRow(row))
            {
                return true;
            }

            string sourceKind = GetCellText(row, "colSourceKind");

            return string.Equals(sourceKind, PartSourceKind.CmdTz, StringComparison.Ordinal) ||
                string.Equals(sourceKind, PartSourceKind.LegacyCmdTz, StringComparison.Ordinal);
        }

        /// <summary>
        /// 接管主画面已经打开的 PDF 预览窗口（避免同时存在两个预览窗口）。
        /// 接管后由本画面负责切换内容，并在本画面关闭时一并关闭。
        /// </summary>
        /// <param name="previewForm">主画面已打开的预览窗口。</param>
        internal void AttachPreviewForm(PdfPreviewForm previewForm)
        {
            if (previewForm == null || previewForm.IsDisposed)
            {
                return;
            }

            if (_previewForm != null && !_previewForm.IsDisposed
                && !ReferenceEquals(_previewForm, previewForm))
            {
                PdfPreviewForm oldPreview = _previewForm;
                _previewForm = null;
                oldPreview.Close();
                oldPreview.Dispose();
            }

            _previewForm = previewForm;
            _previewForm.FormClosed += PreviewForm_FormClosed;
        }

        /// <summary>显示 / 前置本画面的 PDF 预览窗口（主画面复用同一窗口时调用）。</summary>
        /// <param name="bringToFront">是否把预览窗口提到最前。</param>
        internal void ShowPreview(bool bringToFront)
        {
            ShowPreviewWindow(bringToFront);
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

        /// <summary>「关闭」。</summary>
        private void BtnClose_Click(object sender, EventArgs e)
        {
            Close();
        }

        /// <summary>更新标题（有未保存的修改时追加提示）。</summary>
        private void UpdateTitle()
        {
            string title = _drawingNo.Length == 0 ? "部品基本信息" : "部品基本信息 - " + _drawingNo;

            UiLocalization.SetText(this, _isDirty ? title + "　＊有未保存的修改" : title);
            UiLocalization.SetText(btnSave, _isDirty ? "保存 ＊" : "保存");
        }

        /// <summary>设置「是否有未保存的修改」并刷新标题、统计标签。</summary>
        private void SetDirty(bool dirty)
        {
            _isDirty = dirty;
            UpdateTitle();
            RefreshGridSummary();
        }

        /// <summary>按表格当前内容统计条数与校验状态，并提示是否有未保存的修改。</summary>
        private void RefreshGridSummary()
        {
            int total = 0;
            int passedCount = 0;
            int failedCount = 0;
            int notCheckedCount = 0;

            foreach (DataGridViewRow row in dgvParts.Rows)
            {
                if (row.IsNewRow)
                {
                    continue;
                }

                total++;

                string status = GetCellText(row, "colVerifyStatus");

                if (status == PartVerifyStatus.Passed)
                {
                    passedCount++;
                }
                else if (status == PartVerifyStatus.Failed)
                {
                    failedCount++;
                }
                else
                {
                    notCheckedCount++;
                }
            }

            UiLocalization.SetText(lblSummary, string.Format("共 {0} 条（坐标一致 {1} / 不符 {2} / 未校验 {3}）{4}",
                total, passedCount, failedCount, notCheckedCount,
                _isDirty ? "　＊未保存" : string.Empty));
        }

        /// <summary>重新编号「行号」列（<see cref="RowNumberColumnName"/>：追加 / 删除行后保持连续）。</summary>
        private void RenumberRows()
        {
            _suppressGridEvents = true;

            try
            {
                for (int index = 0; index < dgvParts.Rows.Count; index++)
                {
                    DataGridViewRow row = dgvParts.Rows[index];

                    if (row.IsNewRow)
                    {
                        continue;
                    }

                    row.Cells[RowNumberColumnName].Value = (index + 1).ToString();
                }
            }
            finally
            {
                _suppressGridEvents = false;
            }
        }

        /// <summary>
        /// 按「数据来源 / 特注 / 是否被分割」给整行上色（一行同时满足多个条件时，按下面的顺序取第一条）。
        /// <list type="bullet">
        ///   <item><b>特注</b>（「特注」= <see cref="PartBasicInfo.SpecialNoteMark"/>）→ <b>淡粉色</b>；</item>
        ///   <item><b>被分割的行</b>（备注里有 <see cref="CmdCalloutPartSplitter.RemarkPositionPrefix"/>＝<c>位置：</c>，
        ///     由第 2 步按圈号把 1 行拆成多条产生，拆出来的每一条都算）→ <b>淡紫色</b>；</item>
        ///   <item><b>数据来源 = <see cref="PartSourceKind.CmdAssembly"/></b>（アッセンブリ図 自身的部品表）→ <b>淡绿色</b>；</item>
        ///   <item><b>数据来源 = <see cref="PartSourceKind.CmdYoryo"/></b>（要領図 的部品）→ <b>淡蓝色</b>；</item>
        ///   <item>其它（<see cref="PartSourceKind.CmdTz"/> / <see cref="PartSourceKind.AiAnalysis"/> /
        ///     <see cref="PartSourceKind.Manual"/> / 数据来源为空）→ 白色。</item>
        /// </list>
        /// <para>
        /// 「校验状态」不再用整行背景表示（会把上面的来源色盖掉），改成只给「校验状态」单元格上色
        /// （<see cref="ApplyVerifyStatusCellStyle"/>：坐标不符 = 红底白字、坐标一致 = 深绿色字）。
        /// 「部品コード」单元格的粉红色（推定值）由 <see cref="ApplyPartCodeGuess"/> 单独设置，
        /// 单元格样式优先于行样式，所以两种颜色可以同时看到。
        /// </para>
        /// </summary>
        /// <param name="row">要处理的行（<c>null</c> / 新增行时忽略）。</param>
        private static void ApplyRowStyle(DataGridViewRow row)
        {
            if (row == null || row.IsNewRow)
            {
                return;
            }

            row.DefaultCellStyle.BackColor = ResolveRowBackColor(row);
            // 行号格固定浅灰（Excel 的行号栏那样），不跟着「数据来源」的淡绿 / 淡蓝 / 特注淡粉变
            DataGridViewCell rowNumberCell = row.Cells[RowNumberColumnName];

            if (rowNumberCell != null)
            {
                rowNumberCell.Style.BackColor = RowNumberBackColor;
                rowNumberCell.Style.ForeColor = RowNumberForeColor;
            }

            ApplyVerifyStatusCellStyle(row);
            ApplySourceKindToolTip(row);
        }

        /// <summary>
        /// 按「特注 → 被分割 → アッセンブリ図 → 要領図」的顺序决定整行背景色（都不满足时为白色）。
        /// </summary>
        private static Color ResolveRowBackColor(DataGridViewRow row)
        {
            if (IsSpecialNoteRow(row))
            {
                return SpecialNoteBackColor;
            }

            if (IsSplitRow(row))
            {
                return SplitRowBackColor;
            }

            string sourceKind = GetCellText(row, "colSourceKind");

            if (string.Equals(sourceKind, PartSourceKind.CmdAssembly, StringComparison.Ordinal))
            {
                return SourceAssemblyBackColor;
            }

            if (string.Equals(sourceKind, PartSourceKind.CmdYoryo, StringComparison.Ordinal))
            {
                return SourceYoryoBackColor;
            }

            return PlainRowBackColor;
        }

        /// <summary>
        /// 该行是否为<b>特注</b>：「特注」= <see cref="PartBasicInfo.SpecialNoteMark"/>（<c>1</c>）。
        /// 特注品.json 的追加対応特注寸法一律是 1，画面上也能手工改成 1。
        /// </summary>
        private static bool IsSpecialNoteRow(DataGridViewRow row)
        {
            return string.Equals(GetCellText(row, "colSpecialNote"), PartBasicInfo.SpecialNoteMark,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// 该行是否<b>被按位置分割过</b>：备注里带 <see cref="CmdCalloutPartSplitter.RemarkPositionPrefix"/>（<c>位置：</c>）。
        /// 第 2 步按圈号把 1 行拆成「右… / 左…」多条时，拆出来的<b>每一条</b>备注都会追加这个标记
        /// （<c>CmdCalloutPartSplitter.BuildPositionRemark</c>）。
        /// </summary>
        private static bool IsSplitRow(DataGridViewRow row)
        {
            return GetCellText(row, "colRemark").IndexOf(CmdCalloutPartSplitter.RemarkPositionPrefix,
                StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// 给「数据来源」单元格设置 ToolTip，说明这一行为什么用这个背景色
        /// （颜色含义见 <see cref="ApplyRowStyle"/>）。
        /// </summary>
        private static void ApplySourceKindToolTip(DataGridViewRow row)
        {
            DataGridViewCell cell = row.Cells["colSourceKind"];

            if (cell == null)
            {
                return;
            }

            if (IsSpecialNoteRow(row))
            {
                cell.ToolTipText = UiText.F("行背景 淡粉 = 特注（「特注」= {0}）", PartBasicInfo.SpecialNoteMark);
                return;
            }

            if (IsSplitRow(row))
            {
                cell.ToolTipText = UiText.F("行背景 淡紫 = 被分割的行（备注含「{0}」，按圈号 / 位置 1 行拆成多条）",
                    CmdCalloutPartSplitter.RemarkPositionPrefix);
                return;
            }

            string sourceKind = GetCellText(row, "colSourceKind");

            if (string.Equals(sourceKind, PartSourceKind.CmdAssembly, StringComparison.Ordinal))
            {
                cell.ToolTipText = UiText.F("行背景 淡绿 = 数据来源「{0}」", PartSourceKind.CmdAssembly);
                return;
            }

            if (string.Equals(sourceKind, PartSourceKind.CmdYoryo, StringComparison.Ordinal))
            {
                cell.ToolTipText = UiText.F("行背景 淡蓝 = 数据来源「{0}」", PartSourceKind.CmdYoryo);
                return;
            }

            cell.ToolTipText = sourceKind.Length == 0
                ? UiText.T("数据来源为空（老数据 / 未写来源）→ 行背景 白色")
                : UiText.F("行背景 白色 = 数据来源「{0}」（不属于上面几类）", sourceKind);
        }

        /// <summary>
        /// 只给「校验状态」单元格上色（不动整行背景，避免盖住「数据来源」的颜色）：
        /// 坐标不符 = 红底白字，坐标一致 = 深绿色字，未校验 = 默认。
        /// </summary>
        private static void ApplyVerifyStatusCellStyle(DataGridViewRow row)
        {
            DataGridViewCell cell = row.Cells["colVerifyStatus"];

            if (cell == null)
            {
                return;
            }

            string status = GetCellText(row, "colVerifyStatus");

            if (status == PartVerifyStatus.Failed)
            {
                cell.Style.BackColor = FailedStatusBackColor;
                cell.Style.ForeColor = Color.White;
            }
            else if (status == PartVerifyStatus.Passed)
            {
                cell.Style.BackColor = Color.Empty;
                cell.Style.ForeColor = PassedStatusForeColor;
            }
            else
            {
                cell.Style.BackColor = Color.Empty;
                cell.Style.ForeColor = Color.Empty;
            }
        }

        /// <summary>
        /// 「部品コード」为空的行：用「部品名」去部品マスタ（<c>部品マスタ\Tbl_部品マスタ.csv</c>）找部品コード。
        /// <list type="bullet">
        ///   <item>只命中 1 个 → 填进单元格（推定值），并把该单元格标成<b>粉红色</b>；</item>
        ///   <item>命中多个（同一个部品名 对应材質 / 色 不同的多个部品コード）→ <b>不填</b>，只标粉红色，
        ///     ToolTip 里列出全部候选，让用户按材質 / 色 自己选；</item>
        ///   <item>一个都没有 / 部品名为空 → 只标粉红色，ToolTip 说明原因。</item>
        /// </list>
        /// 已经有部品コード 的行清掉粉红色（= 不是推定值）。改了「部品名」之后会重新推定。
        /// </summary>
        /// <param name="row">要处理的行（<c>null</c> / 新增行时忽略）。</param>
        /// <param name="assignValue">
        /// true = 允许把推定值填进单元格（载入明细 / 用户改了「部品名」时）；
        /// false = 只更新粉红色标记和 ToolTip，<b>不动单元格里的值</b>
        /// （用户正在手工改「部品コード」时，不要把刚清空的值又自动填回去）。
        /// </param>
        private void ApplyPartCodeGuess(DataGridViewRow row, bool assignValue)
        {
            if (row == null || row.IsNewRow)
            {
                return;
            }

            DataGridViewCell cell = row.Cells["colPartCode"];

            if (cell == null)
            {
                return;
            }

            if (GetCellText(row, "colPartCode").Length > 0)
            {
                // 已经有部品コード（图纸 / CSV / 用户填写）→ 不是推定值，恢复普通背景
                cell.Style.BackColor = Color.Empty;
                cell.ToolTipText = string.Empty;

                return;
            }

            // 没有部品コード 的行一律标粉红色（推定值 / 缺值都需要人工确认）
            cell.Style.BackColor = PartCodeGuessBackColor;

            string partName = GetCellText(row, "colPartName");

            if (partName.Length == 0)
            {
                cell.ToolTipText = UiText.F("部品コード 为空，部品名 也为空 → 无法从部品マスタ（{0}）推定，请手工输入",
                    CmdPartCodeNormalizer.FileName);

                return;
            }

            IList<CmdPartMasterEntry> candidates = GetPartMasterIndex().FindByPartName(partName);

            if (candidates.Count == 1)
            {
                CmdPartMasterEntry found = candidates[0];

                if (!assignValue)
                {
                    // 用户正在手工改部品コード → 只告诉他マスタ里是什么，不替他填
                    cell.ToolTipText = UiText.F(
                        "部品コード 为空 → 部品名「{0}」在部品マスタ 里是 {1}（{2} 第 {3} 行）→ 需要时请手工输入",
                        partName, found.PartCode, found.FileName, found.RowNumber);

                    return;
                }

                // 自动填值不算「用户改动」（不要因为填值再触发一次本方法 / 脏标记）
                bool previous = _suppressGridEvents;

                _suppressGridEvents = true;

                try
                {
                    cell.Value = found.PartCode;
                }
                finally
                {
                    _suppressGridEvents = previous;
                }

                cell.ToolTipText = UiText.F(
                    "部品コード 为空 → 按部品名「{0}」从部品マスタ 推定：{1}（{2} 第 {3} 行）。请确认。",
                    partName, found.PartCode, found.FileName, found.RowNumber);

                return;
            }

            if (candidates.Count > 1)
            {
                List<string> codes = new List<string>();

                foreach (CmdPartMasterEntry candidate in candidates)
                {
                    codes.Add(candidate.PartCode);
                }

                cell.ToolTipText = UiText.F(
                    "部品マスタ 里「{0}」有 {1} 个部品コード：{2} → 请按材質 / 色 手工选择",
                    partName, candidates.Count, string.Join(" / ", codes.ToArray()));

                return;
            }

            cell.ToolTipText = UiText.F(
                "部品マスタ（{0}）里没有「{1}」→ 请手工输入部品コード",
                CmdPartCodeNormalizer.FileName, partName);
        }

        /// <summary>
        /// 取部品マスタ 索引（第一次用到时读 <c>部品マスタ\Tbl_部品マスタ.csv</c>，整个画面生命周期只读一次）。
        /// 找不到 / 读不了时返回空索引 —— 只是不自动补部品コード，不影响画面其它功能，也不弹提示。
        /// </summary>
        private CmdPartMasterIndex GetPartMasterIndex()
        {
            if (_partMasterLoaded)
            {
                return _partMasterIndex;
            }

            _partMasterLoaded = true;

            CmdPartMasterIndex index = new CmdPartMasterIndex();

            try
            {
                string searchFrom = null;

                if (_pdfPath.Length > 0)
                {
                    searchFrom = Path.GetDirectoryName(_pdfPath);
                }

                string filePath = CmdPartCodeNormalizer.ResolveDataSource(searchFrom, null);

                if (!string.IsNullOrEmpty(filePath))
                {
                    index = CmdPartCodeNormalizer.LoadIndex(filePath, null);
                }
            }
            catch (Exception)
            {
                // 部品マスタ 读不到 / 格式异常：只是不自动补部品コード（不去打扰用户）
            }

            _partMasterIndex = index;

            return _partMasterIndex;
        }

        /// <summary>读取单元格文本（NULL 视为空字符串，并去掉首尾空白）。</summary>
        private static string GetCellText(DataGridViewRow row, string columnName)
        {
            if (row == null)
            {
                return string.Empty;
            }

            object value = row.Cells[columnName].Value;

            return value == null || value == DBNull.Value
                ? string.Empty
                : Convert.ToString(value).Trim();
        }

        /// <summary>判断是否为 <see cref="PartVerifyStatus"/> 中定义的状态。</summary>
        private static bool IsKnownVerifyStatus(string status)
        {
            if (string.IsNullOrEmpty(status))
            {
                return false;
            }

            foreach (string item in PartVerifyStatus.All)
            {
                if (item == status)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 有未保存的修改时询问：「是」= 保存后继续，「否」= 放弃修改继续，「取消」= 中止操作。
        /// </summary>
        /// <param name="action">后续动作的描述（如「切换图番」「关闭窗口」）。</param>
        /// <returns>true = 可以继续；false = 中止当前操作。</returns>
        private bool ConfirmUnsavedChanges(string action)
        {
            if (!_isDirty)
            {
                return true;
            }

            DialogResult result = ShowMessage(
                "有未保存的修改。\n\n是否先保存，再" + action + "？\n\n" +
                "「是」：保存后" + action + "\n「否」：放弃修改并" + action + "\n「取消」：留在当前画面",
                "确认", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1);

            if (result == DialogResult.Cancel)
            {
                return false;
            }

            if (result == DialogResult.Yes)
            {
                return SaveParts();
            }

            return true;
        }

        /// <summary>
        /// 单击单元格：只选中<b>这一格</b>（不再整行选中），并<b>直接进入编辑、把内容全选</b>
        /// （EditMode 是 <c>EditOnKeystrokeOrF2</c>，不手动 BeginEdit 的话要点两次才可以改）。
        /// <list type="bullet">
        ///   <item><b>右键</b>（<see cref="_rightClickInProgress"/>）→ 只弹右键菜单（插入行 / 删除行），不进入编辑；</item>
        ///   <item>「部材名」→ 打开「选择部材名」窗口（<see cref="ShowMemberNamePicker"/>），不进入普通编辑；</item>
        ///   <item>「行号」（最左侧）→ <b>选中整行</b>（Excel 那样；按住 Ctrl 可多选），不进入编辑（见 <see cref="SelectWholeRow"/>）；</item>
        ///   <item>其它只读列（数据来源 / 登录时间）→ 只选中单元格；</item>
        ///   <item>其它列 → 选中 + 进入编辑 + 全选（再打一个字就替换掉原来的值）。</item>
        /// </list>
        /// </summary>
        private void DgvParts_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (_rightClickInProgress)
            {
                // 右键 = 弹出右键菜单（插入行 / 删除行），不要进入编辑 / 不要打开「选择部材名」
                return;
            }

            if (_suppressGridEvents || e.RowIndex < 0 || e.ColumnIndex < 0)
            {
                return;
            }

            if (e.RowIndex >= dgvParts.Rows.Count || e.ColumnIndex >= dgvParts.Columns.Count)
            {
                return;
            }

            DataGridViewCell cell = dgvParts.Rows[e.RowIndex].Cells[e.ColumnIndex];

            // 行号列（Excel 最左侧的行号栏）：点一下 = 选中整行，不进入编辑
            // （列本身也是只读的，下面是普通「只读列」分支的前面）
            if (cell != null && IsRowNumberColumn(cell.OwningColumn))
            {
                SelectWholeRow(cell.OwningRow);
                return;
            }

            if (cell == null || dgvParts.ReadOnly || cell.ReadOnly)
            {
                return;
            }

            // Ctrl / Shift 按下时是「多选单元格」（【删除行】一次删多行）→ 只选中，不进入编辑
            if ((Control.ModifierKeys & (Keys.Control | Keys.Shift)) != Keys.None)
            {
                return;
            }

            if (IsMemberNameColumn(cell.OwningColumn))
            {
                // 部材名 是「从常用部材名 里挑一个」的列 → 用选择窗口写，而不是让用户手打
                ShowMemberNamePicker(cell);
                return;
            }

            BeginCellEditAndSelectAll(cell);
        }

        /// <summary>这一列是不是「行号」（<see cref="RowNumberColumnName"/>：显示在最左侧的只读行号列）。</summary>
        private static bool IsRowNumberColumn(DataGridViewColumn column)
        {
            return column != null && string.Equals(column.Name, RowNumberColumnName, StringComparison.Ordinal);
        }

        /// <summary>
        /// 点「行号」单元格：<b>整行选中</b>（和 Excel 点行号一样），不进入编辑。
        /// <para>
        /// 按住 Ctrl 时<b>追加</b>到当前选择（多选几行 →【删除行】一次删多行）；不按 Ctrl 时先清掉原来的选择。
        /// 选中的仍然只是「单元格」（表格是 <c>CellSelect</c> 模式）—— 删除 / 插入行按「选中单元格所在的行」取，
        /// 见 <see cref="GetSelectedRows"/>，那边不用改。
        /// </para>
        /// </summary>
        /// <param name="row">要选中的行（<c>null</c> / 新增行时什么都不做）。</param>
        private void SelectWholeRow(DataGridViewRow row)
        {
            if (row == null || row.IsNewRow)
            {
                return;
            }

            bool addToSelection = (Control.ModifierKeys & Keys.Control) == Keys.Control;

            if (!addToSelection)
            {
                dgvParts.ClearSelection();
            }

            row.Selected = true;

            // 当前单元格留在这一行的「行号」格：方向键从这一行继续走，
            // 右键菜单 / Delete 的作用对象也还是这一行
            if (dgvParts.CurrentCell == null || !ReferenceEquals(dgvParts.CurrentCell.OwningRow, row))
            {
                try
                {
                    dgvParts.CurrentCell = row.Cells[RowNumberColumnName];
                }
                catch (Exception)
                {
                    // 表格正在更新 / 这一格暂时不能成为当前格 → 只保留整行选中，不报错
                }
            }
        }

        /// <summary>
        /// 让这个单元格进入编辑状态，并把编辑框里的文字全选。
        /// 已经在编辑<b>同一格</b>时不重做（第二次点击要把光标放在点到的位置，不能又全选）。
        /// </summary>
        private void BeginCellEditAndSelectAll(DataGridViewCell cell)
        {
            if (cell == null)
            {
                return;
            }

            if (dgvParts.IsCurrentCellInEditMode && ReferenceEquals(dgvParts.CurrentCell, cell))
            {
                return;
            }

            try
            {
                dgvParts.CurrentCell = cell;
                dgvParts.BeginEdit(true);
            }
            catch (Exception)
            {
                // 下拉列（校验状态）的值不在候选里等情况 → 只选中单元格，不进入编辑
                return;
            }

            SelectEditingText();
        }

        /// <summary>
        /// 把单元格编辑框（TextBox）里的文字全选；编辑框还没建好时排到消息循环里再试一次
        /// （DataGridView 在 BeginEdit 之后才真正创建编辑控件）。
        /// </summary>
        private void SelectEditingText()
        {
            if (!dgvParts.IsCurrentCellInEditMode)
            {
                return;
            }

            TextBox editor = dgvParts.EditingControl as TextBox;

            if (editor != null)
            {
                editor.SelectAll();
                return;
            }

            if (!dgvParts.IsHandleCreated)
            {
                return;
            }

            BeginInvoke((MethodInvoker)delegate
            {
                if (!dgvParts.IsCurrentCellInEditMode)
                {
                    return;
                }

                TextBox late = dgvParts.EditingControl as TextBox;

                if (late != null)
                {
                    late.SelectAll();
                }
            });
        }

        /// <summary>这一列是不是「部材名」（<see cref="MemberNameColumnName"/>：多值用 <c>|</c> 连接的那一列）。</summary>
        private static bool IsMemberNameColumn(DataGridViewColumn column)
        {
            return column != null && string.Equals(column.Name, MemberNameColumnName, StringComparison.Ordinal);
        }

        /// <summary>
        /// 点「部材名」单元格时打开「选择部材名」窗口（<see cref="MemberNamePickerForm"/>）。
        /// 窗口是<b>非模态</b>的：开着的时候本画面照常操作（改别的格子 / 换行 / 看图纸都不受影响）；
        /// 单击窗口里的一项，就立刻把这一项写进这一格（见 <see cref="MemberNamePicker_MemberNamePicked"/>），
        /// 不用勾选、也不用点确定。窗口已经开着时只把「写入目标」换成这一次点的格子。
        /// 窗口被「部材名」单元格的内嵌编辑（F2 / 双击 / 直接打字）收起过
        /// （<see cref="HideMemberNamePicker"/>）时，再点这一格会重新显示。
        /// </summary>
        private void ShowMemberNamePicker(DataGridViewCell cell)
        {
            if (cell == null)
            {
                return;
            }

            _memberNamePickerCell = cell;

            string currentValue = GetCellText(cell.OwningRow, cell.OwningColumn.Name);

            // 同一行的「部品名」= 部材 mapping 的 BuhinName / 置换表的 MsifDevName，
            // 「部品コード」= 部材 mapping 的 BuhinCd：
            // 窗口用它们查出部材名（以前用过的部材名 / 表示部材名）排在候选最前，按数字键就能直接写入
            string partName = GetCellText(cell.OwningRow, PartNameColumnName);
            string partCode = GetCellText(cell.OwningRow, PartCodeColumnName);

            if (_memberNamePicker == null || _memberNamePicker.IsDisposed)
            {
                MemberNamePickerForm picker =
                    new MemberNamePickerForm(GetMemberNameCatalog(), currentValue, partName, partCode);

                picker.MemberNamePicked += MemberNamePicker_MemberNamePicked;
                picker.FormClosed += MemberNamePicker_FormClosed;
                _memberNamePicker = picker;

                // 先摆好位置再显示（StartPosition = Manual）：面板贴在点到的这一格下面那一行，
                // 不遮挡选中的行，也不会先在画面中间闪一下再跳过来
                PlaceMemberNamePicker(picker, cell);

                // 非模态（Show 而不是 ShowDialog）：开着窗口也能操作本画面；
                // Owned 窗口会跟着本画面一起关闭，省掉手工收尾。
                picker.Show(this);
                return;
            }

            // 已经开着：切换写入目标（列表里选中这一格现在的值 + 推荐项换成这一行的部品名 / 部品コード 对应的），
            // 不重新读マスタ
            _memberNamePicker.SetTarget(currentValue, partName, partCode);

            if (!_memberNamePicker.Visible)
            {
                _memberNamePicker.Show(this);
            }

            // 换了一行 / 表格滚动过 → 重新贴到这一格下面那一行（面板不压在选中的行上）
            PlaceMemberNamePicker(_memberNamePicker, cell);

            _memberNamePicker.Activate();
        }

        /// <summary>
        /// 把「选择部材名」窗口摆到目标单元格<b>下面那一行</b>的位置：
        /// 上边贴着这一格的下边（不遮挡选中的这一行）、左边和这一格对齐，
        /// 下方放不下时改放到这一格上面（见 <see cref="WindowLayoutHelper.PlacePopupNextToCell"/>）。
        /// <para>
        /// 这一格现在不在画面上（被滚出去 / 表格还没布局）时退回「相对本画面居中」，
        /// 面板至少不会跑到屏幕角落以外。
        /// </para>
        /// </summary>
        /// <param name="picker">「选择部材名」窗口（<c>null</c> / 已释放时什么都不做）。</param>
        /// <param name="cell">目标单元格（「部材名」列）。</param>
        private void PlaceMemberNamePicker(MemberNamePickerForm picker, DataGridViewCell cell)
        {
            if (picker == null || picker.IsDisposed || cell == null || cell.OwningRow == null ||
                !IsHandleCreated || !dgvParts.IsHandleCreated)
            {
                return;
            }

            // 这一格在表格里的位置（相对表格客户区）→ 换成屏幕坐标交给摆放方法
            Rectangle cellBounds = dgvParts.GetCellDisplayRectangle(cell.ColumnIndex, cell.RowIndex, true);

            if (cellBounds.Width > 0 && cellBounds.Height > 0)
            {
                WindowLayoutHelper.PlacePopupNextToCell(picker, dgvParts.RectangleToScreen(cellBounds));

                return;
            }

            // 这一格现在看不见（滚出画面等）→ 相对本画面居中（原来的行为）
            Rectangle ownerBounds = RectangleToScreen(ClientRectangle);

            picker.Location = new Point(
                ownerBounds.Left + (ownerBounds.Width - picker.Width) / 2,
                ownerBounds.Top + (ownerBounds.Height - picker.Height) / 2);
        }

        /// <summary>
        /// 「选择部材名」窗口单击了某一项 / 点了【写入】：立刻把值写进目标单元格
        /// （值没变时不产生「有未保存的修改」；空串 = 清空这一格）。
        /// <para>
        /// 写完（值没变也一样）把表格的选择移到<b>下一行的「部材名」</b>，
        /// 窗口的写入目标跟着换过去（见 <see cref="MoveToNextRowMemberNameCell"/>）——
        /// 这样按 <c>1</c> / 点候选项就能一行一行往下填。
        /// </para>
        /// </summary>
        /// <param name="memberName">选中的部材名（或手工输入的内容）。</param>
        private void MemberNamePicker_MemberNamePicked(string memberName)
        {
            DataGridViewCell cell = _memberNamePickerCell;

            if (cell == null || cell.OwningRow == null ||
                !ReferenceEquals(cell.OwningRow.DataGridView, dgvParts))
            {
                // 目标行已经被删掉 / 换了図番（窗口还开着）：不写，免得写到别的行
                return;
            }

            string selectedValue = memberName == null ? string.Empty : memberName.Trim();
            string currentValue = GetCellText(cell.OwningRow, cell.OwningColumn.Name);

            if (!string.Equals(selectedValue, currentValue, StringComparison.Ordinal))
            {
                if (dgvParts.IsCurrentCellInEditMode)
                {
                    if (ReferenceEquals(dgvParts.CurrentCell, cell))
                    {
                        // 正在编辑的就是这一格：先退出编辑，免得编辑框把旧值写回去
                        dgvParts.CancelEdit();
                    }
                    else
                    {
                        // 正在编辑别的格子（窗口开着也能改表格）：先把那一格提交，别丢掉用户打的字
                        try
                        {
                            dgvParts.CommitEdit(DataGridViewDataErrorContexts.Commit);
                        }
                        catch (Exception)
                        {
                            // 提交失败（没有默认处理）时保持原样，继续写「部材名」
                        }
                    }
                }

                cell.Value = selectedValue;
            }
            // 值没变时不赋值 → 不产生「有未保存的修改」；但选择照样往下走

            MoveToNextRowMemberNameCell(cell);
        }

        /// <summary>
        /// 把表格的选择移到<b>下一行</b>的「部材名」单元格，并把「选择部材名」窗口的写入目标
        /// 换成这一格（列表里预先选中的值 / 推荐项 = 这一行的部品CD / 部品名 对应的部材名）。
        /// <para>
        /// 已经到最后一行（下方没有可见行）时停在原地，不循环、也不自动追加行。
        /// 只移动选择、不进入编辑：改「部材名」一律走选择窗口。
        /// </para>
        /// </summary>
        /// <param name="cell">刚写完的「部材名」单元格。</param>
        private void MoveToNextRowMemberNameCell(DataGridViewCell cell)
        {
            DataGridViewRow row = cell == null ? null : cell.OwningRow;

            if (row == null || !ReferenceEquals(row.DataGridView, dgvParts))
            {
                return;
            }

            // 往下找第一个「看得见」的行（隐藏的行不能当选中目标）
            DataGridViewRow nextRow = null;

            for (int index = row.Index + 1; index < dgvParts.Rows.Count; index++)
            {
                DataGridViewRow candidate = dgvParts.Rows[index];

                if (candidate != null && candidate.Visible)
                {
                    nextRow = candidate;
                    break;
                }
            }

            if (nextRow == null)
            {
                return;
            }

            DataGridViewCell nextCell = nextRow.Cells[cell.ColumnIndex];

            if (nextCell == null || nextCell.ReadOnly)
            {
                return;
            }

            SetMemberNamePickerTarget(nextCell, nextRow);

            try
            {
                dgvParts.CurrentCell = nextCell;
            }
            catch (Exception)
            {
                // 该格暂时不能成为当前单元格（例如正在校验）→ 保持原选择，不报错
            }

            // 写完自动跳到下一行：面板跟着贴到新的这一格下面那一行
            // （放在设置 CurrentCell 之后 —— 表格可能为了显示这一行而滚动，先设位置会算错）
            PlaceMemberNamePicker(_memberNamePicker, nextCell);
        }

        /// <summary>
        /// 换「选择部材名」窗口的写入目标：单元格引用记到 <see cref="_memberNamePickerCell"/>，
        /// 窗口开着时把这一格现在的值、同一行的部品名 / 部品コード 一起交给它
        /// （推荐项 / 预先选中项跟着换；窗口没开就不用管，下次点「部材名」会新建）。
        /// </summary>
        /// <param name="cell">新的目标单元格（「部材名」列）。</param>
        /// <param name="row">该单元格所在行（<c>null</c> 时从单元格取）。</param>
        private void SetMemberNamePickerTarget(DataGridViewCell cell, DataGridViewRow row)
        {
            if (cell == null)
            {
                return;
            }

            _memberNamePickerCell = cell;

            if (_memberNamePicker == null || _memberNamePicker.IsDisposed)
            {
                return;
            }

            DataGridViewRow targetRow = row ?? cell.OwningRow;
            string currentValue = targetRow == null ? string.Empty : GetCellText(targetRow, cell.OwningColumn.Name);
            string partName = targetRow == null ? string.Empty : GetCellText(targetRow, PartNameColumnName);
            string partCode = targetRow == null ? string.Empty : GetCellText(targetRow, PartCodeColumnName);

            _memberNamePicker.SetTarget(currentValue, partName, partCode);
        }

        /// <summary>「选择部材名」窗口关闭后清掉引用（下次点「部材名」会重新创建）。</summary>
        private void MemberNamePicker_FormClosed(object sender, FormClosedEventArgs e)
        {
            MemberNamePickerForm picker = sender as MemberNamePickerForm;

            if (picker != null)
            {
                picker.MemberNamePicked -= MemberNamePicker_MemberNamePicked;
                picker.FormClosed -= MemberNamePicker_FormClosed;
            }

            _memberNamePicker = null;
            _memberNamePickerCell = null;
        }

        /// <summary>
        /// 收起「选择部材名」窗口（<b>不关闭</b>）：「部材名」单元格进入<b>内嵌编辑</b>
        /// （F2 / 直接打字 / 双击）时用 —— 编辑框和候选面板同时开着，
        /// 既挡视线，面板上的数字键 / 滑轮还会把值写进正在编辑的这一格
        /// （见 <see cref="DgvParts_CellBeginEdit"/>）。
        /// <para>
        /// 再点「部材名」单元格会重新显示（<see cref="ShowMemberNamePicker"/> 里有
        /// <c>!Visible</c> 就 <c>Show(this)</c> 的分支），所以这里只 <c>Hide</c> 不 <c>Close</c>：
        /// 窗口里的候选列表 / 滚动位置都留着。
        /// </para>
        /// </summary>
        private void HideMemberNamePicker()
        {
            MemberNamePickerForm picker = _memberNamePicker;

            if (picker != null && !picker.IsDisposed && picker.Visible)
            {
                picker.Hide();
            }
        }

        /// <summary>
        /// 关掉「选择部材名」窗口（换図番 / 关闭本画面时用）：
        /// 已经写进单元格的值不受影响，只是这次的选择目标作废。
        /// </summary>
        private void CloseMemberNamePicker()
        {
            MemberNamePickerForm picker = _memberNamePicker;

            _memberNamePicker = null;
            _memberNamePickerCell = null;

            if (picker != null && !picker.IsDisposed)
            {
                picker.Close();
            }
        }

        /// <summary>
        /// 部材部品マスタ 的索引（部材名 一览 + 每个部材名 下的部品名）：给「部材名」辅助窗口当候选列表用。
        /// 画面打开时（<see cref="OnLoad"/>）读一次（数据库优先，其次 <c>部材部品\部材部品.csv</c>），
        /// 之后整画面复用 —— 打开「选择部材名」窗口时不再读盘。
        /// 读不到时返回 <c>null</c> —— 只是候选列表为空（窗口里仍可手工输入），不弹提示。
        /// </summary>
        private MemberPartCatalog GetMemberNameCatalog()
        {
            if (_memberNameCatalogLoaded)
            {
                return _memberNameCatalog;
            }

            _memberNameCatalogLoaded = true;

            // 第一次可能要打开 SQLite 数据库 / 导入 部材部品.csv → 短暂显示等待光标
            Cursor previousCursor = Cursor;
            Cursor = Cursors.WaitCursor;

            try
            {
                string searchFrom = _pdfPath.Length > 0 ? Path.GetDirectoryName(_pdfPath) : null;

                _memberNameCatalog = MemberPartCatalogLoader.LoadFor(searchFrom, null);
            }
            catch (Exception)
            {
                // 部材部品マスタ 读不到 / 格式异常：候选项为空（不去打扰用户）
            }
            finally
            {
                Cursor = previousCursor;
            }

            return _memberNameCatalog;
        }

        /// <summary>
        /// 部材部品 表更新后（保存时登录了新规则）重新读一次内存里的索引，
        /// 让「选择部材名」窗口立刻能选到新登录的部材名（读不到时只是候补变少，不弹提示）。
        /// </summary>
        private void RefreshMemberNameCatalog()
        {
            _memberNameCatalogLoaded = false;
            _memberNameCatalog = null;

            GetMemberNameCatalog();
        }

        /// <summary>单元格内容变化时：刷新行颜色、统计与未保存标记。</summary>
        private void DgvParts_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_suppressGridEvents || e.RowIndex < 0 || e.ColumnIndex < 0)
            {
                return;
            }

            DataGridViewRow row = dgvParts.Rows[e.RowIndex];
            string columnName = dgvParts.Columns[e.ColumnIndex].Name;

            // 校验状态改了 → 只刷「校验状态」单元格的配色（红底白字 / 深绿色字）；
            // 特注改成 1 → 整行淡粉；备注里出现 / 去掉「位置：」→ 整行淡紫（被分割）→ 整行背景要跟着换
            if (columnName == "colVerifyStatus" || columnName == "colSpecialNote" ||
                columnName == "colRemark")
            {
                ApplyRowStyle(row);
            }

            // 部品名 改了 → 按部品名 从部品マスタ 重新推定部品コード（粉红色 = 推定值）；
            // 部品コード 改了 → 只刷新粉红色标记（手工清空时不要把值自动填回去）
            if (columnName == "colPartName")
            {
                ApplyPartCodeGuess(row, true);
            }
            else if (columnName == "colPartCode")
            {
                ApplyPartCodeGuess(row, false);
            }

            if (columnName != RowNumberColumnName)
            {
                SetDirty(true);
            }
        }

        /// <summary>
        /// 表格内按 Delete 删除选中的行。
        /// <para>
        /// 单元格单击就会进入编辑（<see cref="DgvParts_CellClick"/>），编辑中按 Delete 只删单元格里的文字，
        /// 所以「删行」请用【删除行】按钮 / <see cref="DeleteSelectedRows"/>（未进入编辑时 Delete 仍然删行）。
        /// </para>
        /// </summary>
        private void DgvParts_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete && !dgvParts.IsCurrentCellInEditMode)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                DeleteSelectedRows();
            }
        }

        /// <summary>画面内按 Ctrl+S 保存。</summary>
        private void PartBasicInfoForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.S)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                SaveParts();
                return;
            }

            // F4：在当前「部品名」单元格上开关候选列表（输入内容为空也可以打开，方便挑一个常用的）
            if (e.KeyCode == Keys.F4 && !e.Control && !e.Alt)
            {
                if (IsPartNameCellInUse())
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    TogglePartNameCandidates();
                }
            }
        }

        /// <summary>
        /// 当前单元格是不是正在编辑的「部品名」（<see cref="PartNameColumnName"/>）：
        /// 候选列表 / F4 只对这一列生效，别的列（数量 / 部品コード 等）照常输入数字。
        /// </summary>
        private bool IsPartNameCellInUse()
        {
            DataGridViewCell cell = dgvParts.CurrentCell;

            return cell != null && dgvParts.ContainsFocus &&
                string.Equals(cell.OwningColumn.Name, PartNameColumnName, StringComparison.Ordinal);
        }

        /// <summary>关闭前确认未保存的修改（系统关机/任务管理器结束时不询问）。</summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.WindowsShutDown ||
                e.CloseReason == CloseReason.TaskManagerClosing)
            {
                base.OnFormClosing(e);
                return;
            }

            if (!ConfirmUnsavedChanges("关闭窗口"))
            {
                e.Cancel = true;
                return;
            }

            base.OnFormClosing(e);
        }

        /// <summary>
        /// 画面打开时把「选择部材名」窗口要用的数据<b>一次性</b>读进内存：
        /// ① 部材 mapping（SQL Server 的 <c>MSIF.dbo.DL_BuhinBuzaiMapping</c>：部品CD / 部品名 → 部材名，
        ///    推荐项里最靠前的一段）；
        /// ② 部材名置换表（SQL Server 的 <c>genpin.dbo.T_M_MergeName</c>；程序启动时已经预读过，
        ///    这里只在启动时没读成功的情况下用短超时补读一次）；
        /// ③ 部材部品マスタ（SQLite「部材部品」表里的部材名 一览）。
        /// 之后点「部材名」单元格只是内存查找，不会再连数据库 / 读盘。
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            PreloadMemberNamePickerData();
        }

        /// <summary>
        /// 预读「选择部材名」窗口的数据（部材 mapping + 置换表 + 部材部品マスタ）。
        /// 失败只影响候选项，不弹提示。
        /// </summary>
        private void PreloadMemberNamePickerData()
        {
            try
            {
                // 部品CD / 部品名 → 部材名（现场没有 MSIF 库时这里用 3 秒超时放弃，只是没有推荐项）
                BuhinBuzaiMappingCatalog.EnsurePreloaded();
            }
            catch (Exception)
            {
                // 读不到只影响推荐项（原因记在 BuhinBuzaiMappingCatalog.LastError）
            }

            try
            {
                MemberNameReplacementCatalog.EnsurePreloaded();
            }
            catch (Exception)
            {
                // 置换表读不到只影响推荐项（原因记在 MemberNameReplacementCatalog.LastError）
            }

            // 部材部品マスタ：读不到时 GetMemberNameCatalog 返回 null（窗口里只是候补为空，可以手工输入）
            GetMemberNameCatalog();
        }

        /// <summary>
        /// 窗口显示后与部品基本信息画面同时打开 PDF 预览窗口：
        /// PDF 预览 → 主画面以外的屏幕（不占用主画面所在的那块屏），
        /// 部品基本信息 → 主画面所在的屏幕（只有 1 块屏幕时左右并排）。
        /// </summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            if (_openPreviewOnShown)
            {
                _openPreviewOnShown = false;
                ShowPreviewWindow(false);

                // 自动打开的预览窗口不抢焦点，焦点留在编辑画面
                Activate();
            }

            // 兜底：没有打开预览窗口（例如 PDF 不存在，ShowPreviewWindow 提前返回）时也按屏幕摆一次。
            // 本画面已不再设 Owner，设计器的 CenterParent 失去参照，不摆的话窗口会跑到左上角。
            if (!_editorPlaced)
            {
                _editorPlaced = true;
                WindowLayoutHelper.ArrangeEditor(this, ResolveMainWindowForm());
            }
        }

        /// <summary>
        /// 关闭本画面时：注销「当前画面」（全局只保留一个，见 <see cref="FindOpenInstance"/>）、
        /// 一并关闭 PDF 预览窗口，释放文件句柄。
        /// </summary>
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // 注销「当前画面」：下次打开【部品信息】会新建一个
            if (ReferenceEquals(_currentInstance, this))
            {
                _currentInstance = null;
            }

            PdfPreviewForm preview = _previewForm;
            _previewForm = null;

            // 「选择部材名」窗口是 Owned 窗口（正常情况下会随本画面一起关闭），这里再显式收一次尾
            CloseMemberNamePicker();

            // 「部品名」候选窗口同理（Owned 窗口，隐藏后复用，这里释放掉）
            ClosePartNameCandidatePopup();

            // Close() 会触发 FormClosed，所以先把引用取到局部变量再关闭
            if (preview != null && !preview.IsDisposed)
            {
                preview.Close();
                preview.Dispose();
            }

            base.OnFormClosed(e);
        }

        /// <summary>
        /// 统一弹窗：标题 / 正文都按当前语言翻译
        /// （正文里的动态部分由 <c>Services\UiText.Japanese.cs</c> 的模板匹配处理）。
        /// </summary>
        private DialogResult ShowMessage(string message, string caption,
            MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return MessageBox.Show(this, UiText.Translate(message), UiText.T(caption), buttons, icon);
        }

        /// <summary>统一弹窗（可指定默认按钮）。</summary>
        private DialogResult ShowMessage(string message, string caption,
            MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
        {
            return MessageBox.Show(this, UiText.Translate(message), UiText.T(caption),
                buttons, icon, defaultButton);
        }
    }
}
