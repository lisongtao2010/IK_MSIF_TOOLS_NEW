using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MSIFAITOOLS.Services;

namespace MSIFAITOOLS
{
    /// <summary>
    /// 「部材名」被选中时的回调（参数 = 要写进单元格的值；空串 = 清空这一格）。
    /// </summary>
    /// <param name="memberName">选中的部材名（列表里没有的名字则是手工输入的内容）。</param>
    internal delegate void MemberNamePickedHandler(string memberName);

    /// <summary>
    /// 「选择部材名」窗口：由部品基本信息画面（<see cref="PartBasicInfoForm"/>）
    /// 点「部材名」单元格时打开，做成输入法候选面板那样——部材名带编号、分两列显示。
    ///
    /// <para>
    /// <b>外观</b>（<c>MemberNamePickerForm.Style.cs</c>）：贴近 Google 日语输入法的候补面板——
    /// 无标题栏的白色浮窗（1px 细边框 + 系统投影）、顶部灰色圆角检索框（放大镜 + 灰色提示文字）、
    /// 中间是带编号的部材名一览（<b>双列、每列 10 行</b>，选中行浅蓝、悬停行浅灰）、底部一行状态和关闭。
    /// 拖动窗口 = 按住检索框 / 底部空白处拖；改大小 = 拖右下角的手柄（高度固定，只改宽度）。
    /// </para>
    ///
    /// <para>
    /// <b>非模态</b>：窗口打开期间主画面照常操作（改别的格子、换行、看图纸），不会被挡住；
    /// 窗口跟着主画面一起关闭。
    /// </para>
    ///
    /// <para>
    /// <b>位置</b>：由主画面（<see cref="PartBasicInfoForm"/>）按点到的「部材名」单元格摆放
    /// （<see cref="WindowLayoutHelper.PlacePopupNextToCell"/>）——面板落在这一格的<b>右下方</b>：
    /// 左边比这一格的左边再往右半个格宽（至少 32px），上边在这一格的下边再往下 24px，
    /// 所以斜着离开选中的行、也不贴着格子边（<b>不遮挡选中的行</b>）；
    /// 右边放不下时尽量贴住屏幕右侧，下方放不下（靠近屏幕底部）时改放到这一格上面（= 上一行）。
    /// 手动拖动过的位置，会在下一次点「部材名」（或写完自动跳到下一行）时重新贴到那一行的右下方。
    /// </para>
    ///
    /// <para>
    /// <b>按编号写入 / 分页</b>：两列候选项前面都带<b>连续序号</b>，编号就是这条记录的<b>整体位置</b>：
    /// 第 1 页左列 1～10、右列 11～20；第 2 页左列 11～20、右列 21～30……
    /// 直接按数字键 <c>1</c>～<c>9</c>（<c>0</c> = 本页左列第 10 项）就把<b>本页左列</b>
    /// 对应位置的部材名写进主画面当前的「部材名」单元格
    /// （触发 <see cref="MemberNamePicked"/>；单元格原来的 <c>|</c> 多值会被整体替换）。
    /// 数字键始终作用于本页左列，和显示编号 / 鼠标 / <c>←</c> <c>→</c> 选到哪一列无关
    /// （第 2 页左列显示 11～20 时，按 <c>1</c> 仍选这一列第 1 项 = 显示 11 的那一项）。
    /// 右列的项请用鼠标单击，或按 <c>←</c> / <c>→</c> 移过去再按 <c>Enter</c>。
    /// </para>
    ///
    /// <para>
    /// <b>双列、每页 20 项、翻页前进 10 项</b>：一览分<b>两列</b>，每列 10 行，一页共 20 项。
    /// 翻页只前进 / 后退 10 项（半页），所以上一页右列的 10 项在下一页左列还能看到：
    /// 第 1 页 1～20、第 2 页 11～30、第 3 页 21～40……（同一条记录在不同页上编号不变）。
    /// <c>←</c> / <c>→</c> 在两列之间移动选中项，<c>↑</c> / <c>↓</c> 上下换项
    /// （到本页最后一个 / 第一个再按就翻页）。
    /// 记录超过 20 条就能翻到下一页（<c>PageUp</c> / <c>PageDown</c>、<c>↑</c> 到页首再按一次 / <c>↓</c> 到页尾再按一次、
    /// 鼠标滚轮、底部「‹ 1/3 ›」按钮）。过滤条件一变就回到第 1 页，
    /// 所以按 <c>1</c> 永远是本页左列的第 1 项（打开窗口时按 <c>1</c> = 左列第 1 项：就是这一行的推荐项：
    /// 部品CD 一致 → 部品名 一致 → 表示部材名，见下面的候选顺序）。
    /// </para>
    ///
    /// <para>
    /// <b>候选顺序</b>（数字越小越靠前；打开窗口时按 1 就是第 ① 项）：
    /// ① 部材 mapping（SQL Server 表 <c>MSIF.dbo.DL_BuhinBuzaiMapping</c>，
    ///    <see cref="BuhinBuzaiMappingCatalog"/>）里<b>部品CD（<c>BuhinCd</c>）与这一行的「部品コード」一致</b>的
    ///    部材名（<c>BuzaiName</c>）—— 同一条部品实际用过的部材，最可信；
    /// ② 同一张表里<b>部品名（<c>BuhinName</c>）与这一行的「部品名」一致</b>的部材名
    ///    （部品CD 没登记时的退路）；
    ///    ①② 是同一批推荐项：① 排在前面，② 里和 ① 重复的（不分全角 / 半角）只留一个。
    ///    SQL Server 连不上时改读<b>本机表</b>（SQLite <c>DL_BuhinBuzaiMapping</c>，与服务器那张同名），
    ///    所以现场断网也有推荐项（状态行会写一句「用的是本机缓存」）；
    /// ③ 这一行的「部品名」在置换表（SQL Server 表 <c>genpin.dbo.T_M_MergeName</c>）里的<b>表示部材名</b>
    ///    （<c>MsifDevName</c> = 图面上的写法；同一个部材的其它叫法也一起列出来，
    ///    见 <see cref="MemberNameReplacementCatalog.GetDisplayNames"/>）——
    ///    面板里显示、写进单元格的都是这种写法，<b>不会</b>换成原部材名（<c>GenpinName</c>）；
    /// ④ 「部材名」单元格现在的值（拆开 <c>|</c>，方便改回原值）；
    /// ⑤ 部材部品マスタ（<see cref="MemberPartCatalog.MemberNames"/>）里的部材名 一览。
    /// ①～③ 的数据在主画面打开时已经整表读进内存，点「部材名」时<b>只查内存，不连数据库</b>；
    /// ⑤ 的部材部品マスタ 也在主画面打开时读好，窗口打开时不再读盘。
    /// </para>
    ///
    /// <para>
    /// <b>过滤 / 手工输入</b>：顶部输入框打字就按关键字过滤（<c>↑</c> / <c>↓</c> 换候选项）；
    /// 列表里没有的名字直接打在输入框里按 <c>Enter</c> 写入
    /// （输入框是空的时按 <c>Enter</c> 会先确认是否清空这一格）。
    /// 想把数字当关键字打时，按住 <c>Ctrl</c> 再按数字。
    /// </para>
    /// </summary>
    internal sealed partial class MemberNamePickerForm : Form
    {
        /// <summary>多个部材名 之间的分隔符（与【CMD 分析】写进数据库的写法一致）。</summary>
        public const char Separator = '|';

        /// <summary>拆分原值时认可的竖线：半角 <c>|</c> 与全角 <c>｜</c>（老数据 / 手打时两种都有）。</summary>
        private static readonly char[] ValueSeparators = new char[] { Separator, '｜' };

        /// <summary>能用数字键选的最多项数（1～9；第 10 项用 <c>0</c>）。</summary>
        private const int MaxShortcutNumber = 9;

        /// <summary>双列显示：一列放 10 行（左列正好对应数字键 1～9 / 0）。</summary>
        private const int RowsPerColumn = MaxShortcutNumber + 1;

        /// <summary>一览固定两列（左列 = 第 1～10 项，右列 = 第 11～20 项）。</summary>
        private const int ColumnCount = 2;

        /// <summary>
        /// 一页最多显示几项：<see cref="ColumnCount"/> 列 × 每列 <see cref="RowsPerColumn"/> 项 = 20 项。
        /// 两列都显示编号（跨页连续的整体位置：第 1 页 1～20、第 2 页 11～30……）；
        /// 数字键 1～9 / 0 始终对应本页左列第 1～10 项。
        /// </summary>
        private const int PageSize = RowsPerColumn * ColumnCount;

        /// <summary>
        /// 翻页步长：每次只前进 / 后退 10 项（半页）。上一页右列的 10 项会出现在下一页左列，
        /// 所以第 1 页是 1～20、第 2 页是 11～30、第 3 页是 21～40……（编号仍按整体位置连续）。
        /// </summary>
        private const int PageStep = RowsPerColumn;

        /// <summary>「已写入」那行文字的正常颜色（Google 输入法面板的灰字）。</summary>
        private static readonly Color PreviewNormalColor = Color.FromArgb(95, 99, 104);

        /// <summary>「已写入」那行文字在值为空（清空了单元格）时的颜色。</summary>
        private static readonly Color PreviewEmptyColor = Color.FromArgb(217, 48, 37);

        /// <summary>
        /// 全部候选项（推荐项在前：部材 mapping 的部材名 → 表示部材名，其次是这一格现在的值，
        /// 最后是部材部品マスタ 的部材名；去重后保持这个顺序）。
        /// </summary>
        private readonly List<string> _items = new List<string>();

        /// <summary>
        /// 按过滤关键字筛出来的候选项（<see cref="_items"/> 的子集，顺序不变）——
        /// 分页就是按这一份切出每页最多 <see cref="PageSize"/> 项（每翻一页起点前进 <see cref="PageStep"/> 项）。
        /// </summary>
        private readonly List<string> _filtered = new List<string>();

        /// <summary>现在显示的是第几页（从 0 开始；0 = 第 1～20 项，1 = 第 11～30 项）。</summary>
        private int _pageIndex;

        /// <summary>
        /// 选中项现在在左列还是右列（0 = 左列，1 = 右列），只给 <c>←</c> / <c>→</c> 换列用；
        /// 数字键始终选本页左列，和这个值无关。
        /// 每次换页 / 改过滤条件 / 换目标格都回到左列；
        /// 鼠标点过右列的项、<c>←</c> / <c>→</c> 移过去之后才变成右列。
        /// </summary>
        private int _activeColumn;

        /// <summary>部材部品マスタ（可能为 <c>null</c>：候选列表只剩推荐项和原值）。</summary>
        private readonly MemberPartCatalog _catalog;

        /// <summary>
        /// 写入目标单元格当前的值：列表里预先选中这一项，方便看清「改的是哪一格」。
        /// 主画面换了目标单元格时由 <see cref="SetCurrentValue"/> 更新。
        /// </summary>
        private string _currentValue = string.Empty;

        /// <summary>
        /// 这次要写入的那一行的「部品名」（<c>colPartName</c>）：用它去
        /// ① 部材 mapping（<see cref="BuhinBuzaiMappingCatalog.GetRecommendedBuzaiNames"/> 的第 2 段）和
        /// ② 置换表 <c>T_M_MergeName</c>（<see cref="MemberNameReplacementCatalog.GetDisplayNames"/>，
        ///    表示部材名 <c>MsifDevName</c>）查推荐项。主画面换了目标行时由 <see cref="SetTarget"/> 更新；
        /// 空串 = 那一行没填部品名。
        /// </summary>
        private string _partName = string.Empty;

        /// <summary>
        /// 这次要写入的那一行的「部品コード」（<c>colPartCode</c>）：用它在部材 mapping
        /// （<see cref="BuhinBuzaiMappingCatalog.GetRecommendedBuzaiNames"/> 的第 1 段）里查
        /// <b>这条部品用过的部材名</b>，作为第 1 号候选排在最前。空串 = 那一行没填部品コード（只用部品名 查）。
        /// </summary>
        private string _partCode = string.Empty;

        /// <summary>
        /// 这一行的推荐项（顺序 = 部品CD 一致的部材名 → 部品名 一致的部材名 → 表示部材名；
        /// 由 <see cref="LoadMappingTargets"/> 从内存里的
        /// <see cref="BuhinBuzaiMappingCatalog"/> / <see cref="MemberNameReplacementCatalog"/> 取出，
        /// 点「部材名」时<b>不连数据库</b>）。
        /// </summary>
        private string[] _mappingTargets = new string[0];

        /// <summary>置换表（表示部材名）没读进内存的原因（成功时为空串；只写在状态行，不弹窗）。</summary>
        private string _mappingError = string.Empty;

        /// <summary>部材 mapping（以前用过的部材名）没读进内存的原因（成功时为空串；只写在状态行，不弹窗）。</summary>
        private string _buzaiError = string.Empty;

        /// <summary>
        /// 推荐项用的是<b>本机表</b>（SQLite <c>DL_BuhinBuzaiMapping</c>）而不是 SQL Server
        /// （<see cref="BuhinBuzaiMappingCatalog.IsLocalFallback"/>）：只在状态行提示一句，不影响选择。
        /// </summary>
        private bool _buzaiLocalFallback;

        /// <summary>输入框上的操作提示（编号键 / Enter 的用法）。</summary>
        private readonly ToolTip _hintToolTip = new ToolTip();

        public MemberNamePickerForm()
            : this(null, string.Empty)
        {
        }

        /// <summary>
        /// 创建「选择部材名」窗口。
        /// </summary>
        /// <param name="catalog">部材部品マスタ（可为 <c>null</c>：候选列表只剩原值拆出来的项）。</param>
        /// <param name="currentValue">「部材名」单元格当前的值（<c>|</c> 分开的多项会拆开加进候选）。</param>
        public MemberNamePickerForm(MemberPartCatalog catalog, string currentValue)
            : this(catalog, currentValue, string.Empty)
        {
        }

        /// <summary>
        /// 创建「选择部材名」窗口，并按这一行的<b>部品名</b> 查推荐项
        /// （部材 mapping 的部材名 + 置换表的表示部材名），排在列表最前。
        /// </summary>
        /// <param name="catalog">部材部品マスタ（可为 <c>null</c>：候选列表只剩推荐项和原值）。</param>
        /// <param name="currentValue">「部材名」单元格当前的值（<c>|</c> 分开的多项会拆开加进候选）。</param>
        /// <param name="partName">同一行的「部品名」（<c>colPartName</c>；空 = 不知道，只列出候选项）。</param>
        public MemberNamePickerForm(MemberPartCatalog catalog, string currentValue, string partName)
            : this(catalog, currentValue, partName, string.Empty)
        {
        }

        /// <summary>
        /// 创建「选择部材名」窗口，并同时给出这一行的<b>部品名</b> 和<b>部品コード</b>：
        /// 推荐项按 ① 部品CD 一致的部材名（部材 mapping）→ ② 部品名 一致的部材名
        /// → ③ 表示部材名（置换表 <c>MsifDevName</c>）的顺序排，第 1 号候选就是这一行按 1 写入的值。
        /// </summary>
        /// <param name="catalog">部材部品マスタ（可为 <c>null</c>：候选列表只剩推荐项和原值）。</param>
        /// <param name="currentValue">「部材名」单元格当前的值（<c>|</c> 分开的多项会拆开加进候选）。</param>
        /// <param name="partName">同一行的「部品名」（<c>colPartName</c>；空 = 不知道，只列出候选项）。</param>
        /// <param name="partCode">同一行的「部品コード」（<c>colPartCode</c>；空 = 只按部品名 查）。</param>
        public MemberNamePickerForm(MemberPartCatalog catalog, string currentValue, string partName,
            string partCode)
        {
            InitializeComponent();

            // 高度固定（一览正好两列 × 每列 10 行，下面不留空白）：只允许左右拖宽（见 PnlResize_MouseMove）
            MinimumSize = new Size(MinimumSize.Width, Height);

            // 位置由主画面按点到的「部材名」单元格算（WindowLayoutHelper.PlacePopupNextToCell）：
            // 不遮挡选中的那一行、落在它的右下方（半格宽 + 24px 空隙），所以不用 CenterParent（CenterParent 会压住选中的行）
            StartPosition = FormStartPosition.Manual;

            // 按钮 / 标签等文字的中日切换（见 Services\UiLocalization.cs）
            UiLocalization.Attach(this);

            _catalog = catalog;
            _currentValue = currentValue == null ? string.Empty : currentValue.Trim();
            _partName = partName == null ? string.Empty : partName.Trim();
            _partCode = partCode == null ? string.Empty : partCode.Trim();

            UiLocalization.SetToolTip(_hintToolTip, txtFilter,
                "输入关键字过滤部材名；按 1～9（0 = 第 10 项）写入本页左列；←→ 移动选中项，↑↓ 换候选项，Enter 写入。\n" +
                "每页 20 项（两列 × 10 行），翻页前进 10 项（第 2 页从第 11 项开始）：PageUp / PageDown（或 ↑↓ 走到页首 / 页尾、鼠标滚轮、底部 ‹ ›）翻页。");
            UiLocalization.SetToolTip(_hintToolTip, btnPrevPage, "上一页（PageUp / 滚轮往上 / 在页首再按 ↑）");
            UiLocalization.SetToolTip(_hintToolTip, btnNextPage, "下一页（PageDown / 滚轮往下 / 在页尾再按 ↓）");

            // 检索框的灰色提示不是控件文字，语言切换时单独重写一次
            UiText.LanguageChanged += MemberNamePicker_LanguageChanged;
            FormClosed += MemberNamePicker_FormClosed;

            LoadMappingTargets();
            LoadItems();
            ApplyFilter(string.Empty);
            ShowTargetHint();
        }

        /// <summary>
        /// 选好部材名 时触发：主画面收到后立刻写回「部材名」单元格（本窗口不关闭，可以接着选）。
        /// </summary>
        public event MemberNamePickedHandler MemberNamePicked;

        /// <summary>
        /// 切换「要写入的目标单元格」：主画面点了另一行的「部材名」时调用，
        /// 列表里会把这一格现在的值预先选中。
        /// </summary>
        /// <param name="currentValue">目标单元格当前的值（可为 <c>null</c>）。</param>
        public void SetCurrentValue(string currentValue)
        {
            // 只换写入目标、部品名 / 部品コード 不变（推荐项按同一行从内存里再取一次，零开销）
            SetTarget(currentValue, _partName, _partCode);
        }

        /// <summary>
        /// 切换「要写入的目标单元格」并同时更新这一行的<b>部品名</b>
        /// （部品コード 沿用上一次的值）。
        /// </summary>
        /// <param name="currentValue">目标单元格当前的值（可为 <c>null</c>）。</param>
        /// <param name="partName">同一行的「部品名」（可为 <c>null</c> / 空）。</param>
        public void SetTarget(string currentValue, string partName)
        {
            SetTarget(currentValue, partName, _partCode);
        }

        /// <summary>
        /// 切换「要写入的目标单元格」并更新这一行的<b>部品名 / 部品コード</b>
        /// （主画面点了另一行的「部材名」时调用）：列表里会把这一格现在的值预先选中，
        /// 候选列表换成这一行的推荐项（部品CD 一致的部材名 → 部品名 一致的部材名 → 表示部材名）+ 新的原值。
        /// </summary>
        /// <param name="currentValue">目标单元格当前的值（可为 <c>null</c>）。</param>
        /// <param name="partName">同一行的「部品名」（可为 <c>null</c> / 空）。</param>
        /// <param name="partCode">同一行的「部品コード」（可为 <c>null</c> / 空）。</param>
        public void SetTarget(string currentValue, string partName, string partCode)
        {
            _currentValue = currentValue == null ? string.Empty : currentValue.Trim();
            _partName = partName == null ? string.Empty : partName.Trim();
            _partCode = partCode == null ? string.Empty : partCode.Trim();

            LoadMappingTargets();
            LoadItems();
            ApplyFilter(txtFilter.Text);
            ShowTargetHint();
        }

        /// <summary>
        /// 取这一行的推荐项放进 <see cref="_mappingTargets"/>，由 <see cref="LoadItems"/> 排在最前
        /// （第 1 号候选 = 按 1 写入的值）。<b>只查内存</b>：打开窗口、换行都是瞬间的，不连数据库。
        /// <list type="number">
        ///   <item><see cref="BuhinBuzaiMappingCatalog.GetRecommendedBuzaiNames"/>：
        ///     部品不材 mapping 里<b>部品CD 一致</b>的部材名（这条部品实际用过的部材，最可信），
        ///     接着是<b>部品名 一致</b>的部材名（部品CD 没登记时的退路），<b>重复的只留一个</b>；</item>
        ///   <item><see cref="MemberNameReplacementCatalog.GetDisplayNames"/>：
        ///     置换表 <c>T_M_MergeName</c> 里的<b>表示部材名</b>（<c>MsifDevName</c>，
        ///     同一个部材的其它叫法也一起列出）。</item>
        /// </list>
        /// 两边的表都没读进内存时只记在 <see cref="_mappingError"/> / <see cref="_buzaiError"/>
        /// （状态行提示），不弹窗，其余候选照常可用。
        /// 部材 mapping 用的是本机表时（SQL Server 连不上）也会在状态行提示一句
        /// （<see cref="_buzaiLocalFallback"/>）。
        /// </summary>
        private void LoadMappingTargets()
        {
            _mappingTargets = new string[0];
            _mappingError = string.Empty;
            _buzaiError = string.Empty;
            _buzaiLocalFallback = false;

            if (_partName.Length == 0 && _partCode.Length == 0)
            {
                return;
            }

            // 部材 mapping（以前用过的部材名）：第一优先 部品CD 一致 → 第二优先 部品名 一致（去重）
            if (BuhinBuzaiMappingCatalog.IsLoaded)
            {
                AppendTargets(BuhinBuzaiMappingCatalog.GetRecommendedBuzaiNames(_partCode, _partName));

                // SQL Server 连不上、用的是本机表 → 状态行提示一句（不影响选择）
                _buzaiLocalFallback = BuhinBuzaiMappingCatalog.IsLocalFallback;
            }
            else
            {
                // 画面打开时没连上 MSIF 库（本机表里也没有）→ 没有「以前用过的部材名」，但仍然可以用下面的候选
                _buzaiError = BuhinBuzaiMappingCatalog.LastError;
            }

            // 置换表的表示部材名（MsifDevName）
            if (MemberNameReplacementCatalog.IsLoaded)
            {
                AppendTargets(MemberNameReplacementCatalog.GetDisplayNames(_partName));
            }
            else
            {
                // 启动时没连上置换表（主画面打开时补读也失败）→ 没有表示部材名，但其它候选照常可用
                _mappingError = MemberNameReplacementCatalog.LastError;
            }
        }

        /// <summary>把推荐项加进 <see cref="_mappingTargets"/>（去空白、去重、保持顺序）。</summary>
        /// <param name="names">要加进去的部材名（可为 <c>null</c>）。</param>
        private void AppendTargets(IList<string> names)
        {
            if (names == null || names.Count == 0)
            {
                return;
            }

            List<string> targets = new List<string>(_mappingTargets);
            bool added = false;

            foreach (string name in names)
            {
                string text = name == null ? string.Empty : name.Trim();

                if (text.Length > 0 && !targets.Contains(text))
                {
                    targets.Add(text);
                    added = true;
                }
            }

            if (added)
            {
                _mappingTargets = targets.ToArray();
            }
        }

        /// <summary>
        /// 按编号写入（<c>1</c>～<c>9</c> → <b>本页左列</b>第 1～9 项，<c>0</c> → 第 10 项）：
        /// 这一页左列没有这一项时返回 false（这次按键当普通输入处理）。
        /// <para>
        /// 数字键只按「本页左列第几项」算，和显示编号 / 选中项现在在左列还是右列无关
        /// （例如第 2 页左列显示 11～20，按 <c>1</c> 仍选这一列第 1 项 = 显示 11 的那一项）。
        /// </para>
        /// </summary>
        /// <param name="number">数字键的数值（0～9）。</param>
        /// <returns>true = 已写入；false = 本页左列没有这个位置的项。</returns>
        private bool PickByNumber(int number)
        {
            int row = number == 0 ? MaxShortcutNumber : number - 1;
            int index = row;

            if (index < 0 || index >= lstNames.Items.Count)
            {
                return false;
            }

            lstNames.SelectedIndex = index;
            _activeColumn = 0;
            EnsureRowVisible(index);

            Pick(GetItemValue(lstNames.Items[index]));

            return true;
        }

        /// <summary>
        /// 选中项现在在哪一列（0 = 左列，1 = 右列），给 <c>←</c> / <c>→</c> 换列用：
        /// 默认左列，鼠标点过右列的项 / 按过 <c>→</c> 之后变成右列（见 <see cref="_activeColumn"/>）。
        /// <b>数字键不看这个值</b>（始终选本页左列第 1～10 项）。
        /// </summary>
        private int SelectedColumn
        {
            get
            {
                return _activeColumn < 0 ? 0 : (_activeColumn >= ColumnCount ? ColumnCount - 1 : _activeColumn);
            }
        }

        /// <summary>
        /// <c>Enter</c> = 写入：列表里有选中项就写选中项；没有选中项就把输入框里的文字当部材名写进去
        /// （列表里没有的名字走这条路）。输入框是空的时先确认（= 清空这一格）。
        /// </summary>
        private void Commit()
        {
            if (lstNames.SelectedIndex >= 0 && lstNames.SelectedIndex < lstNames.Items.Count)
            {
                Pick(GetItemValue(lstNames.Items[lstNames.SelectedIndex]));

                return;
            }

            string custom = txtFilter.Text.Trim();

            if (custom.Length == 0 &&
                ShowMessage(
                    "输入框是空的：按 Enter 会清空主画面这一格。\n\n要清空吗？",
                    "确认清空", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }

            Pick(custom);
        }

        /// <summary>
        /// 刷新底部提示：还没写入时显示「目标格现在是什么」，方便确认改的是哪一格
        /// （写入之后由 <see cref="UpdatePreview"/> 换成「已写入：…」）。
        /// </summary>
        private void ShowTargetHint()
        {
            lblPreview.ForeColor = PreviewNormalColor;

            string text = _currentValue.Length == 0
                ? "这一格现在是空的"
                : "这一格现在是：" + _currentValue;

            if (_buzaiError.Length > 0)
            {
                text += "（部材 mapping 连不上，没有以前用过的部材名）";
            }

            if (_buzaiLocalFallback)
            {
                text += "（部材 mapping 用的是本机缓存）";
            }

            if (_mappingError.Length > 0)
            {
                text += "（置换表连不上，没有表示部材名）";
            }

            UiLocalization.SetText(lblPreview, text);
        }

        /// <summary>统一弹窗：标题 / 正文都按当前语言翻译（正文里的动态部分由对照表的模板匹配处理）。</summary>
        private DialogResult ShowMessage(string message, string caption,
            MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
        {
            return MessageBox.Show(this, UiText.Translate(message), UiText.T(caption),
                buttons, icon, defaultButton);
        }

        /// <summary>窗口显示时把焦点放到过滤框（直接打字就是搜索）。</summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            txtFilter.Focus();
            txtFilter.SelectAll();
        }

        /// <summary>
        /// 组装候选项：推荐项（部品CD / 部品名 一致的部材名 → 表示部材名）→ 原值拆出来的项
        /// → 部材部品マスタ 的部材名（去重后保持这个顺序）。
        /// <para>
        /// 各来源都<b>原样</b>加入（不再按置换表换成 <c>GenpinName</c>）：面板里看到什么、写进单元格的
        /// 就是什么，保存时也是原值（见 <see cref="PartBasicInfo.SetMemberNameFromInput"/>）。
        /// 原部材名（<c>GenpinName</c>）只在 genpin 侧使用，本工具的画面里不出现。
        /// </para>
        /// </summary>
        private void LoadItems()
        {
            _items.Clear();

            foreach (string target in _mappingTargets)
            {
                AddItem(target);
            }

            foreach (string part in SplitValue(_currentValue))
            {
                AddItem(part);
            }

            if (_catalog != null)
            {
                foreach (string memberName in _catalog.MemberNames)
                {
                    AddItem(memberName);
                }
            }
        }

        /// <summary>加入一个项目（去空白、去重）；已经存在时返回 false。</summary>
        private bool AddItem(string value)
        {
            string text = value == null ? string.Empty : value.Trim();

            if (text.Length == 0 || _items.Contains(text))
            {
                return false;
            }

            _items.Add(text);

            return true;
        }

        /// <summary>把值按 <c>|</c> / <c>｜</c> 拆成多项（去空白、去空、去重，保持顺序）。</summary>
        private static List<string> SplitValue(string value)
        {
            List<string> parts = new List<string>();

            if (string.IsNullOrEmpty(value))
            {
                return parts;
            }

            foreach (string part in value.Split(ValueSeparators))
            {
                string text = part.Trim();

                if (text.Length > 0 && !parts.Contains(text))
                {
                    parts.Add(text);
                }
            }

            return parts;
        }

        /// <summary>
        /// 按过滤关键字重新筛候选项（关键字为空时就是全部），然后从第 1 页开始显示；
        /// 编号只是画出来的（左列第几项 = 数字键几），列表里存的始终是部材名本身。
        /// </summary>
        private void ApplyFilter(string keyword)
        {
            string filter = keyword == null ? string.Empty : keyword.Trim();
            string filterKey = MemberPartCatalogLoader.NormalizeKey(filter);

            _filtered.Clear();

            foreach (string item in _items)
            {
                if (filterKey.Length > 0 &&
                    MemberPartCatalogLoader.NormalizeKey(item).IndexOf(filterKey, StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                _filtered.Add(item);
            }

            // 过滤条件一变就回到第 1 页：按 1 永远是这一页的第 1 号候选，不会因为停留在别的页而错位
            _pageIndex = 0;

            ShowPage();
        }

        /// <summary>
        /// 一共几页（至少 1 页：没有候选项时显示「1/1」）。
        /// 翻页只前进 <see cref="PageStep"/> 项，所以最后一页要能覆盖到最后一条：
        /// 20 条以内 1 页，21～30 条 2 页，31～40 条 3 页……
        /// </summary>
        private int PageCount
        {
            get
            {
                if (_filtered.Count <= PageSize)
                {
                    return 1;
                }

                return 1 + ((_filtered.Count - PageSize + PageStep - 1) / PageStep);
            }
        }

        /// <summary>
        /// 把当前页的候选项放进列表（最多 <see cref="PageSize"/> 项 = 两列 × 每列 10 项），并刷新底部的页码和翻页按钮。
        /// 这一页从整体第 <c>_pageIndex * <see cref="PageStep"/> + 1</c> 项开始（第 1 页 1～20、第 2 页 11～30……）；
        /// 两列都显示编号，编号 = 这一项的整体位置。数字键 1～9 / 0 对应本页左列第 1～10 项。
        /// </summary>
        private void ShowPage()
        {
            int pages = PageCount;

            if (_pageIndex >= pages)
            {
                _pageIndex = pages - 1;
            }

            if (_pageIndex < 0)
            {
                _pageIndex = 0;
            }

            int start = _pageIndex * PageStep;

            lstNames.BeginUpdate();

            try
            {
                lstNames.Items.Clear();

                // 换页时把上一页的选中项清掉（否则 SelectedIndex 会留在同一个行号上，看着像选中了别的项）
                lstNames.SelectedIndex = -1;
                _hoverIndex = -1;
                _activeColumn = 0;

                for (int index = start; index < _filtered.Count && index < start + PageSize; index++)
                {
                    lstNames.Items.Add(_filtered[index]);
                }
            }
            finally
            {
                lstNames.EndUpdate();
            }

            // 每页不超过 20 项（两列 × 10 行），列表高度正好放得下 → 不需要上下滚动
            lstNames.TopIndex = 0;

            lblPage.Text = (_pageIndex + 1).ToString() + "/" + pages.ToString();
            btnPrevPage.Enabled = _pageIndex > 0;
            btnNextPage.Enabled = _pageIndex < pages - 1;

            SelectCurrentValue();
        }

        /// <summary>
        /// 翻页（<c>PageUp</c> / <c>PageDown</c>、<c>↑</c> 到页首 / <c>↓</c> 到页尾时，鼠标滚轮、底部 ‹ ›）。
        /// 翻页只前进 / 后退 10 项，前后两页有 10 项重叠，所以选中项落在<b>新出现的那一半</b>：
        /// 往下翻 = 新页左列第 1 项（上一页右列之后的下一项），往上翻 = 新页左列最后一项
        /// （下一页左列之前的上一项），这样接着按 <c>↑</c> <c>↓</c> 的方向感不会断。
        /// </summary>
        /// <param name="delta">-1 = 上一页，+1 = 下一页。</param>
        /// <returns>true = 真的翻页了（已经到第一页 / 最后一页时返回 false，什么都不做）。</returns>
        private bool ChangePage(int delta)
        {
            if (delta == 0)
            {
                return false;
            }

            int target = _pageIndex + delta;

            if (target < 0 || target >= PageCount)
            {
                return false;
            }

            _pageIndex = target;
            ShowPage();

            if (lstNames.Items.Count == 0)
            {
                return true;
            }

            // 往下翻选新页左列第 1 项（= 上一页右列之后的下一项），往上翻选新页左列最后一项
            // （= 下一页左列之前的上一项）：翻页只走 10 项，前后两页有 10 项重叠，不能选重叠的那一半
            int selected = delta > 0
                ? (lstNames.Items.Count > PageStep ? PageStep : 0)
                : (lstNames.Items.Count > PageStep ? PageStep - 1 : lstNames.Items.Count - 1);

            lstNames.SelectedIndex = selected;
            _activeColumn = lstNames.SelectedIndex / RowsPerColumn;
            EnsureRowVisible(lstNames.SelectedIndex);

            return true;
        }

        /// <summary>底部【‹】：上一页（和 PageUp / 滚轮往上一样）。</summary>
        private void BtnPrevPage_Click(object sender, EventArgs e)
        {
            ChangePage(-1);
            txtFilter.Focus();
            txtFilter.SelectAll();
        }

        /// <summary>底部【›】：下一页（和 PageDown / 滚轮往下一样）。</summary>
        private void BtnNextPage_Click(object sender, EventArgs e)
        {
            ChangePage(1);
            txtFilter.Focus();
            txtFilter.SelectAll();
        }

        /// <summary>
        /// 这一项前面的编号：<b>两列都有</b>，是<b>整体位置</b>（跨页连续、同一条记录编号不变）：
        /// 第 1 页左列 1～10 / 右列 11～20；第 2 页左列 11～20 / 右列 21～30……
        /// </summary>
        /// <param name="index">这一项在<b>当前页</b>里的位置（从 0 开始，0～19）。</param>
        /// <returns>编号文字；超出这一页范围时为空串。</returns>
        private string ShortcutNumber(int index)
        {
            if (index < 0 || index >= PageSize)
            {
                return string.Empty;
            }

            return ((_pageIndex * PageStep) + index + 1).ToString();
        }

        /// <summary>取一项对应的部材名（列表里存的就是部材名本身）。</summary>
        /// <param name="item">列表项。</param>
        private static string GetItemValue(object item)
        {
            string value = item as string;

            return value ?? string.Empty;
        }

        /// <summary>
        /// 把<b>这一页</b>里等于目标单元格当前值的那一项选中（这一页没有它就清空选择）。
        /// 不滚动列表：列表顶部始终是这一页的第 1 号候选，数字键和编号才对得上。
        /// </summary>
        private void SelectCurrentValue()
        {
            lstNames.ClearSelected();

            if (_currentValue.Length == 0)
            {
                return;
            }

            for (int index = 0; index < lstNames.Items.Count; index++)
            {
                if (string.Equals(GetItemValue(lstNames.Items[index]), _currentValue, StringComparison.Ordinal))
                {
                    lstNames.SelectedIndex = index;

                    return;
                }
            }
        }

        /// <summary>
        /// 把一个值交给主画面写入（<paramref name="value"/> 为空串 = 清空这一格），并刷新底部提示；
        /// 写完让列表的选中项跟写入的值保持一致（手工输入的名字不在列表里时就没有选中项）。
        /// </summary>
        /// <param name="value">要写进「部材名」单元格的值。</param>
        private void Pick(string value)
        {
            _currentValue = value == null ? string.Empty : value.Trim();

            UpdatePreview();
            SelectCurrentValue();

            MemberNamePickedHandler handler = MemberNamePicked;

            if (handler != null)
            {
                handler(_currentValue);
            }
        }

        /// <summary>刷新底部「已写入」那行（空值用红色提示已经清空这一格）。</summary>
        private void UpdatePreview()
        {
            bool empty = _currentValue.Length == 0;

            UiLocalization.SetText(lblPreview, empty
                ? "已写入：（空）→ 清空了这一格"
                : "已写入：" + _currentValue);
            lblPreview.ForeColor = empty ? PreviewEmptyColor : PreviewNormalColor;
        }

        /// <summary>过滤关键字变化 → 重建列表。</summary>
        private void TxtFilter_TextChanged(object sender, EventArgs e)
        {
            ApplyFilter(txtFilter.Text);
        }

        /// <summary>
        /// 单击列表里的一项 = 直接把这个部材名 写进主画面（不用复选框、也不用再点确定）。
        /// 同一项再点一次也会重新写入（改回原来的值很方便）；写完把焦点还给输入框，可以接着打字过滤。
        /// </summary>
        private void LstNames_MouseClick(object sender, MouseEventArgs e)
        {
            int index = lstNames.IndexFromPoint(e.Location);

            if (index < 0 || index >= lstNames.Items.Count)
            {
                return;
            }

            // 让这一行处于选中状态，用户能看清刚写进去的是哪一项
            lstNames.SelectedIndex = index;
            _activeColumn = index / RowsPerColumn;

            Pick(GetItemValue(lstNames.Items[index]));

            txtFilter.Focus();
            txtFilter.SelectAll();
        }

        /// <summary>
        /// 键盘：<c>1</c>～<c>9</c> / <c>0</c> 按编号写入（<b>始终是本页左列</b>第 1～10 项），
        /// <c>←</c> / <c>→</c> 在两列之间移动选中项，<c>↑</c> / <c>↓</c> 换候选项（到页首 / 页尾再按就翻页），
        /// <c>PageUp</c> / <c>PageDown</c> 翻页，<c>Enter</c> 写入。
        /// 按住 <c>Ctrl</c> 的数字不拦截（想把它当关键字打进输入框时用）。
        /// </summary>
        /// <param name="msg">要处理的 Windows 消息。</param>
        /// <param name="keyData">按键（含修饰键）。</param>
        /// <returns>true = 已经处理掉（不要再交给控件）。</returns>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            bool inPicker = txtFilter.Focused || lstNames.Focused;

            if (!inPicker || (keyData & (Keys.Control | Keys.Alt)) != 0)
            {
                return base.ProcessCmdKey(ref msg, keyData);
            }

            Keys keyCode = keyData & Keys.KeyCode;

            if (keyCode == Keys.Enter)
            {
                Commit();

                return true;
            }

            if (keyCode == Keys.PageUp || keyCode == Keys.PageDown)
            {
                ChangePage(keyCode == Keys.PageDown ? 1 : -1);

                return true;
            }

            if (keyCode == Keys.Left || keyCode == Keys.Right)
            {
                MoveColumn(keyCode == Keys.Right ? 1 : -1);

                return true;
            }

            if (keyCode == Keys.Down || keyCode == Keys.Up)
            {
                MoveSelection(keyCode == Keys.Down ? 1 : -1);

                return true;
            }

            int number = GetNumberKey(keyCode);

            if (number >= 0 && PickByNumber(number))
            {
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>把主键（字母区的 <c>D0</c>～<c>D9</c> 和数字小键盘）换成数字；不是数字键时返回 -1。</summary>
        /// <param name="keyCode">按键的主键部分。</param>
        /// <returns>0～9；不是数字键时 -1。</returns>
        private static int GetNumberKey(Keys keyCode)
        {
            if (keyCode >= Keys.D0 && keyCode <= Keys.D9)
            {
                return keyCode - Keys.D0;
            }

            if (keyCode >= Keys.NumPad0 && keyCode <= Keys.NumPad9)
            {
                return keyCode - Keys.NumPad0;
            }

            return -1;
        }

        /// <summary>
        /// 在列表里上下移动选中项（在输入框里按 <c>↑</c> / <c>↓</c> 时用）；
        /// 已经在页首再按 <c>↑</c> → 翻到上一页的最后一项，页尾再按 <c>↓</c> → 翻到下一页的第 1 项。
        /// 双列时按列表顺序走：左列第 10 项再往下 = 右列第 1 项（和阅读顺序一致）。
        /// </summary>
        /// <param name="delta">+1 = 下一项，-1 = 上一项。</param>
        private void MoveSelection(int delta)
        {
            if (lstNames.Items.Count == 0)
            {
                // 这一页没有候选项（过滤后为空）→ 按方向翻页（到头了什么都不做）
                ChangePage(delta);

                return;
            }

            int current = lstNames.SelectedIndex;
            int next = current < 0
                ? (delta > 0 ? 0 : lstNames.Items.Count - 1)
                : current + delta;

            if (next < 0)
            {
                ChangePage(-1);

                return;
            }

            if (next >= lstNames.Items.Count)
            {
                ChangePage(1);

                return;
            }

            lstNames.SelectedIndex = next;
            _activeColumn = next / RowsPerColumn;
            EnsureRowVisible(next);
        }

        /// <summary>
        /// 在双列之间左右移动选中项（<c>←</c> / <c>→</c>）：行号不变，只换列；
        /// 目标列没有这么多种项时落到那一列的最后一项。到了最左 / 最右列时什么都不做。
        /// 移动只影响选中项，数字键仍作用于本页左列第 1～10 项（不跟着换列）。
        /// </summary>
        /// <param name="delta">+1 = 右列，-1 = 左列。</param>
        private void MoveColumn(int delta)
        {
            int targetColumn = SelectedColumn + delta;

            if (targetColumn < 0 || targetColumn >= ColumnCount)
            {
                return;
            }

            int current = lstNames.SelectedIndex;
            int row = current < 0 ? 0 : current % RowsPerColumn;
            int index = (targetColumn * RowsPerColumn) + row;

            if (index >= lstNames.Items.Count)
            {
                index = lstNames.Items.Count - 1;
            }

            if (index < 0)
            {
                return;
            }

            lstNames.SelectedIndex = index;
            _activeColumn = targetColumn;
            EnsureRowVisible(index);
        }

        /// <summary>【关闭】：关掉本窗口（已经写入的值留在主画面的单元格里）。</summary>
        private void BtnClose_Click(object sender, EventArgs e)
        {
            Close();
        }
    }
}
