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
    /// 点「部材名」单元格时打开，做成输入法候选面板那样——只有一列部材名，前面带编号。
    ///
    /// <para>
    /// <b>非模态</b>：窗口打开期间主画面照常操作（改别的格子、换行、看图纸），不会被挡住；
    /// 窗口跟着主画面一起关闭。
    /// </para>
    ///
    /// <para>
    /// <b>按编号写入</b>：候选项前面都带编号（1～9，第 10 项是 0），
    /// 直接按数字键就把对应编号的部材名写进主画面当前的「部材名」单元格
    /// （触发 <see cref="MemberNamePicked"/>；单元格原来的 <c>|</c> 多值会被整体替换）。
    /// 鼠标单击哪一项也一样；第 11 项以后没有编号，只能单击。
    /// </para>
    ///
    /// <para>
    /// <b>候选顺序</b>：
    /// ① 这一行的「部品名」在置换表（SQL Server 表 <c>genpin.dbo.T_M_MergeName</c>，
    ///    <c>MsifDevName</c> = 取得的词）里对应的「置换后」（<c>GenpinName</c>）——排最前，按 1 就是它；
    /// ② 「部材名」单元格现在的值（拆开 <c>|</c>，方便改回原值）；
    /// ③ 部材部品マスタ（<see cref="MemberPartCatalog.MemberNames"/>）里的部材名 一览。
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

        /// <summary>能用数字键选的最多项数（1～9）。</summary>
        private const int MaxShortcutNumber = 9;

        /// <summary>没有数字快捷键的行用的对齐空白（约等于「编号 + 2 个半角空格」的宽度）。</summary>
        private const string NumberPadding = "    ";

        /// <summary>「已写入」那行文字的正常颜色。</summary>
        private static readonly Color PreviewNormalColor = Color.FromArgb(52, 73, 94);

        /// <summary>「已写入」那行文字在值为空（清空了单元格）时的颜色。</summary>
        private static readonly Color PreviewEmptyColor = Color.FromArgb(198, 40, 40);

        /// <summary>
        /// 全部候选项（置换表的推荐项在前，其次是这一格现在的值，最后是部材部品マスタ 的部材名；
        /// 去重后保持这个顺序）。
        /// </summary>
        private readonly List<string> _items = new List<string>();

        /// <summary>部材部品マスタ（可能为 <c>null</c>：候选列表只剩推荐项和原值）。</summary>
        private readonly MemberPartCatalog _catalog;

        /// <summary>
        /// 写入目标单元格当前的值：列表里预先选中这一项，方便看清「改的是哪一格」。
        /// 主画面换了目标单元格时由 <see cref="SetCurrentValue"/> 更新。
        /// </summary>
        private string _currentValue = string.Empty;

        /// <summary>
        /// 这次要写入的那一行的「部品名」（<c>colPartName</c>）：用它去置换表
        /// <c>T_M_MergeName</c>（<c>MsifDevName</c>）查对应的置换后（<c>GenpinName</c>），
        /// 作为第 1 号候选排在列表最前。主画面换了目标行时由 <see cref="SetTarget"/> 更新；
        /// 空串 = 那一行没填部品名。
        /// </summary>
        private string _partName = string.Empty;

        /// <summary>
        /// 部品名 → 部材名 的查询结果缓存（键 = 部品名，Ordinal）：
        /// 主画面在同一个部品名 上反复点「部材名」时不再重复连 SQL Server。
        /// 查询失败的结果<b>不</b>缓存（下次点还会再试一次）。
        /// </summary>
        private readonly Dictionary<string, string[]> _mappingCache =
            new Dictionary<string, string[]>(StringComparer.Ordinal);

        /// <summary>置换表查到的推荐项（<c>GenpinName</c>；按部品名 查不到时为空数组）。</summary>
        private string[] _mappingTargets = new string[0];

        /// <summary>置换表查询失败的原因（成功时为空串；只写在状态行，不弹窗）。</summary>
        private string _mappingError = string.Empty;

        /// <summary>输入框上的操作提示（编号键 / Enter 的用法）。</summary>
        private readonly ToolTip _hintToolTip = new ToolTip();

        /// <summary>
        /// 「选择部材名」窗口查置换表用的连接超时（秒）：交互画面里连不上时不要像维护画面那样等满 15 秒。
        /// </summary>
        private const int MappingConnectTimeoutSeconds = 3;

        /// <summary>连接信息（第一次查置换表时解析一次，之后复用；还没解析时为 <c>null</c>）。</summary>
        private MemberMappingDbSettings _mappingSettings;

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
        /// 创建「选择部材名」窗口，并按这一行的<b>部品名</b> 去置换表
        /// （SQL Server 表 <c>T_M_MergeName</c>）查对应的部材名，作为第 1 号候选排在列表最前。
        /// </summary>
        /// <param name="catalog">部材部品マスタ（可为 <c>null</c>：候选列表只剩推荐项和原值）。</param>
        /// <param name="currentValue">「部材名」单元格当前的值（<c>|</c> 分开的多项会拆开加进候选）。</param>
        /// <param name="partName">同一行的「部品名」（<c>colPartName</c>；空 = 不知道，只列出候选项）。</param>
        public MemberNamePickerForm(MemberPartCatalog catalog, string currentValue, string partName)
        {
            InitializeComponent();

            _catalog = catalog;
            _currentValue = currentValue == null ? string.Empty : currentValue.Trim();
            _partName = partName == null ? string.Empty : partName.Trim();

            _hintToolTip.SetToolTip(txtFilter,
                "输入关键字过滤部材名；按 1～9（0 = 第 10 项）直接写入；↑↓ 换候选项，Enter 写入。");

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
            // 只换写入目标、部品名不变（推荐项按同一个部品名 重新查一次，命中缓存时是零开销）
            SetTarget(currentValue, _partName);
        }

        /// <summary>
        /// 切换「要写入的目标单元格」并同时更新这一行的<b>部品名</b>
        /// （主画面点了另一行的「部材名」时调用）：列表里会把这一格现在的值预先选中，
        /// 候选列表换成新部品名 在置换表里对应的推荐项 + 新的原值。
        /// </summary>
        /// <param name="currentValue">目标单元格当前的值（可为 <c>null</c>）。</param>
        /// <param name="partName">同一行的「部品名」（可为 <c>null</c> / 空）。</param>
        public void SetTarget(string currentValue, string partName)
        {
            _currentValue = currentValue == null ? string.Empty : currentValue.Trim();
            _partName = partName == null ? string.Empty : partName.Trim();

            LoadMappingTargets();
            LoadItems();
            ApplyFilter(txtFilter.Text);
            ShowTargetHint();
        }

        /// <summary>
        /// 按 <see cref="_partName"/> 去置换表（SQL Server 表 <c>genpin.dbo.T_M_MergeName</c> 的
        /// <c>MsifDevName</c>）查 <c>GenpinName</c>，结果放进 <see cref="_mappingTargets"/>，
        /// 由 <see cref="LoadItems"/> 排成第 1 号候选；查不到 / 连不上只记在 <see cref="_mappingError"/>，
        /// 不弹窗、不影响下面的候选列表。
        /// </summary>
        private void LoadMappingTargets()
        {
            _mappingTargets = new string[0];
            _mappingError = string.Empty;

            if (_partName.Length == 0)
            {
                return;
            }

            string[] targets;
            string error;

            if (!TryQueryMapping(_partName, out targets, out error))
            {
                _mappingError = error == null ? string.Empty : error;

                return;
            }

            _mappingTargets = targets;
        }

        /// <summary>
        /// 按部品名 查置换表里的部材名（带缓存；<b>失败不缓存</b> → 下次点还会再试）。
        /// </summary>
        /// <param name="partName">部品名（非空）。</param>
        /// <param name="targets">命中的部材名（已排序；没有时为空数组）。</param>
        /// <param name="error">查询失败的原因（成功时为 <c>null</c>）。</param>
        /// <returns>true = 查询成功（可能有 0 条）；false = 连接 / SQL 出错。</returns>
        private bool TryQueryMapping(string partName, out string[] targets, out string error)
        {
            targets = new string[0];
            error = null;

            string[] cached;

            if (_mappingCache.TryGetValue(partName, out cached))
            {
                targets = cached;

                return true;
            }

            try
            {
                using (MemberNameReplacementRepository repository =
                    new MemberNameReplacementRepository(ResolveMappingSettings()))
                {
                    IList<string> found = repository.QueryTargets(partName);
                    targets = new List<string>(found).ToArray();
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;

                return false;
            }

            _mappingCache[partName] = targets;

            return true;
        }

        /// <summary>
        /// 查置换表用的连接信息（窗口生命周期内只解析一次）：超时改成
        /// <see cref="MappingConnectTimeoutSeconds"/>，服务器不可达时画面只卡几秒。
        /// </summary>
        private MemberMappingDbSettings ResolveMappingSettings()
        {
            MemberMappingDbSettings settings = _mappingSettings;

            if (settings != null)
            {
                return settings;
            }

            MemberMappingDbSettings resolved = MemberMappingDbSettingsStore.ResolveJp1();

            settings = new MemberMappingDbSettings(resolved.Server, resolved.Port, resolved.Database,
                resolved.UserId, resolved.Password, resolved.ConfigurationSource, MappingConnectTimeoutSeconds,
                resolved.ConnectionName);
            _mappingSettings = settings;

            return settings;
        }

        /// <summary>
        /// 按编号写入（<c>1</c>～<c>9</c> → 第 1～9 项，<c>0</c> → 第 10 项）：
        /// 编号对得上当前列表时就写入并返回 true；没有这一项时返回 false（这次按键当普通输入处理）。
        /// </summary>
        /// <param name="number">数字键的数值（0～9）。</param>
        /// <returns>true = 已写入；false = 列表里没有这个编号的项。</returns>
        private bool PickByNumber(int number)
        {
            int index = number == 0 ? MaxShortcutNumber : number - 1;

            if (index < 0 || index >= lvNames.Items.Count)
            {
                return false;
            }

            ListViewItem row = lvNames.Items[index];
            row.Selected = true;
            row.Focused = true;
            row.EnsureVisible();

            Pick(GetItemValue(row));

            return true;
        }

        /// <summary>
        /// <c>Enter</c> = 写入：列表里有选中项就写选中项；没有选中项就把输入框里的文字当部材名写进去
        /// （列表里没有的名字走这条路）。输入框是空的时先确认（= 清空这一格）。
        /// </summary>
        private void Commit()
        {
            if (lvNames.SelectedItems.Count > 0)
            {
                Pick(GetItemValue(lvNames.SelectedItems[0]));

                return;
            }

            string custom = txtFilter.Text.Trim();

            if (custom.Length == 0 &&
                MessageBox.Show(this,
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

            if (_mappingError.Length > 0)
            {
                text += "（置换表连不上，没有推荐项）";
            }

            lblPreview.Text = text;
        }

        /// <summary>窗口显示时把焦点放到过滤框（直接打字就是搜索）。</summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            txtFilter.Focus();
            txtFilter.SelectAll();
        }

        /// <summary>
        /// 组装候选项：置换表的推荐项 → 原值拆出来的项 → 部材部品マスタ 的部材名（去重后保持这个顺序）。
        /// <para>
        /// 置换表的推荐项（<c>GenpinName</c>）已经是置换<b>后</b>的词，原样加入；
        /// 原值拆出来的项与部材部品マスタ 的部材名 是图面上的叫法（<c>MsifDevName</c>），
        /// 加入前先按 <see cref="MemberNameReplacementCatalog.Replace"/> 置换一次
        /// （与 <see cref="PartBasicInfo.MemberName"/> 的 setter 同一规则：<c>鴨居 → 上枠</c>、
        /// <c>右鴨居 → 右上枠</c>），所以在这里挑名字写入的就是置换后的值。
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
                AddItem(MemberNameReplacementCatalog.Replace(part));
            }

            if (_catalog != null)
            {
                foreach (string memberName in MemberNameReplacementCatalog.ReplaceMany(_catalog.MemberNames))
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
        /// 按过滤关键字重建列表（关键字为空时显示全部候选项），每一行前面加上编号；
        /// 重建后选中目标单元格现在的值，并让列宽跟着窗口走。
        /// </summary>
        private void ApplyFilter(string keyword)
        {
            string filter = keyword == null ? string.Empty : keyword.Trim();
            string filterKey = MemberPartCatalogLoader.NormalizeKey(filter);

            lvNames.BeginUpdate();

            try
            {
                lvNames.Items.Clear();

                foreach (string item in _items)
                {
                    if (filterKey.Length > 0 &&
                        MemberPartCatalogLoader.NormalizeKey(item).IndexOf(filterKey, StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }

                    ListViewItem row = new ListViewItem(NumberPrefix(lvNames.Items.Count) + item);
                    row.Tag = item;
                    lvNames.Items.Add(row);
                }
            }
            finally
            {
                lvNames.EndUpdate();
                AdjustColumnWidth();
            }

            SelectCurrentValue();
        }

        /// <summary>
        /// 第 <paramref name="index"/> 项（从 0 开始）前面的编号文字：
        /// 前 9 项是 <c>1</c>～<c>9</c>，第 10 项是 <c>0</c>，再往后没有快捷键、只留对齐的空白。
        /// </summary>
        /// <param name="index">这一项在列表里的位置（从 0 开始）。</param>
        /// <returns>宽度一致的前缀（编号 + 2 个半角空格，或等宽的对齐空白）。</returns>
        private static string NumberPrefix(int index)
        {
            if (index < MaxShortcutNumber)
            {
                return (index + 1).ToString() + "  ";
            }

            if (index == MaxShortcutNumber)
            {
                return "0  ";
            }

            return NumberPadding;
        }

        /// <summary>让唯一的那一列铺满列表宽度（窗口拉宽 / 缩窄、出现滚动条时都跟着变）。</summary>
        private void AdjustColumnWidth()
        {
            int width = lvNames.ClientSize.Width - 4;

            if (width > 80 && colMemberName.Width != width)
            {
                colMemberName.Width = width;
            }
        }

        /// <summary>列表宽度变了 → 列宽跟着变。</summary>
        private void LvNames_Resize(object sender, EventArgs e)
        {
            AdjustColumnWidth();
        }

        /// <summary>取一行对应的部材名（真实值放在 <c>Tag</c> 里，行文字前面带编号）。</summary>
        private static string GetItemValue(ListViewItem row)
        {
            if (row == null)
            {
                return string.Empty;
            }

            string value = row.Tag as string;

            return value == null ? row.Text : value;
        }

        /// <summary>
        /// 把列表里等于目标单元格当前值的那一项选中（没有相同项时清空选择）。
        /// <b>不滚动</b>列表：列表顶部始终是第 1 号候选，数字键和编号才对得上。
        /// </summary>
        private void SelectCurrentValue()
        {
            lvNames.SelectedItems.Clear();

            if (_currentValue.Length == 0)
            {
                return;
            }

            foreach (ListViewItem row in lvNames.Items)
            {
                string item = row.Tag as string;

                if (item != null && string.Equals(item, _currentValue, StringComparison.Ordinal))
                {
                    row.Selected = true;

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

            lblPreview.Text = empty
                ? "已写入：（空）→ 清空了这一格"
                : "已写入：" + _currentValue;
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
        private void LvNames_MouseClick(object sender, MouseEventArgs e)
        {
            ListViewItem row = lvNames.GetItemAt(e.X, e.Y);

            if (row == null)
            {
                return;
            }

            // 让这一行处于选中状态，用户能看清刚写进去的是哪一项
            row.Selected = true;
            row.Focused = true;

            Pick(GetItemValue(row));

            txtFilter.Focus();
            txtFilter.SelectAll();
        }

        /// <summary>
        /// 键盘：<c>1</c>～<c>9</c> / <c>0</c> 按编号写入，<c>↑</c> / <c>↓</c> 换候选项，<c>Enter</c> 写入。
        /// 按住 <c>Ctrl</c> 的数字不拦截（想把它当关键字打进输入框时用）。
        /// </summary>
        /// <param name="msg">要处理的 Windows 消息。</param>
        /// <param name="keyData">按键（含修饰键）。</param>
        /// <returns>true = 已经处理掉（不要再交给控件）。</returns>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            bool inPicker = txtFilter.Focused || lvNames.Focused;

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

        /// <summary>在列表里上下移动选中项（在输入框里按 <c>↑</c> / <c>↓</c> 时用）。</summary>
        /// <param name="delta">+1 = 下一项，-1 = 上一项。</param>
        private void MoveSelection(int delta)
        {
            if (lvNames.Items.Count == 0)
            {
                return;
            }

            int current = lvNames.FocusedItem == null ? -1 : lvNames.FocusedItem.Index;
            int next = current < 0
                ? (delta > 0 ? 0 : lvNames.Items.Count - 1)
                : current + delta;

            if (next < 0 || next >= lvNames.Items.Count)
            {
                return;
            }

            lvNames.SelectedItems.Clear();

            ListViewItem row = lvNames.Items[next];
            row.Selected = true;
            row.Focused = true;
            row.EnsureVisible();
        }

        /// <summary>【关闭】：关掉本窗口（已经写入的值留在主画面的单元格里）。</summary>
        private void BtnClose_Click(object sender, EventArgs e)
        {
            Close();
        }
    }
}
