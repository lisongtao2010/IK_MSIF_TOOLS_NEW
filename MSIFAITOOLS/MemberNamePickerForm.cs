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
    /// 点「部材名」单元格时打开，用来从<b>常用部材名</b>里挑一个（而不是让人对着图纸手打）。
    ///
    /// <para>
    /// <b>非模态</b>：窗口打开期间主画面照常操作（改别的格子、换行、看图纸），不会被挡住；
    /// 窗口跟着主画面一起关闭。
    /// </para>
    ///
    /// <para>
    /// <b>单击即写入</b>：部材名 只能选一个，所以列表不用复选框 ——
    /// 单击哪一项，就立刻把这一项写进主画面当前的「部材名」单元格
    /// （触发 <see cref="MemberNamePicked"/>；单元格原来的 <c>|</c> 多值会被整体替换）。
    /// 列表里没有的名字写在「手工输入」里点【写入】（留空点【写入】= 清空这一格）。
    /// </para>
    ///
    /// <para>
    /// <b>候选项</b> = 部材部品マスタ（<c>D:\MSIFDB\MSIF_MemberParts.db</c> 的「部材部品」表，
    /// 首次运行时由 <c>部材部品\部材部品.csv</c> 导入）里的部材名 一览
    /// （<see cref="MemberPartCatalog.MemberNames"/>，去重、保持登录顺序）。第二列同时列出
    /// 这个部材名 下面登记的部品名，方便在写法相近的部材名 之间判断。
    /// </para>
    ///
    /// <para>
    /// <b>顶部那一行</b>（<c>lblMapping</c>）= 这一行的「部品名」（<c>colPartName</c>）在置换表
    /// （SQL Server 表 <c>DL_BuhinBuzaiMapping</c>，<c>BuhinName</c> = 部品名）里对应的
    /// 「部材名」（<c>BuzaiName</c>）：点这一行就直接写进目标单元格；查不到 / 连不上时红字说明原因，
    /// 不影响下面的候选列表和手工输入。
    /// </para>
    /// </summary>
    internal sealed partial class MemberNamePickerForm : Form
    {
        /// <summary>多个部材名 之间的分隔符（与【CMD 分析】写进数据库的写法一致）。</summary>
        public const char Separator = '|';

        /// <summary>拆分原值时认可的竖线：半角 <c>|</c> 与全角 <c>｜</c>（老数据 / 手打时两种都有）。</summary>
        private static readonly char[] ValueSeparators = new char[] { Separator, '｜' };

        /// <summary>「已写入」那行文字的正常颜色。</summary>
        private static readonly Color PreviewNormalColor = Color.FromArgb(52, 73, 94);

        /// <summary>「已写入」那行文字在值为空（清空了单元格）时的颜色。</summary>
        private static readonly Color PreviewEmptyColor = Color.FromArgb(198, 40, 40);

        /// <summary>列表里的全部项目（原值拆出来的项在前，候选项在后；保持这个顺序显示）。</summary>
        private readonly List<string> _items = new List<string>();

        /// <summary>部材部品マスタ（可能为 <c>null</c>：读不到时只有空列表 + 手工输入）。</summary>
        private readonly MemberPartCatalog _catalog;

        /// <summary>
        /// 写入目标单元格当前的值：列表里预先选中这一项，方便看清「改的是哪一格」。
        /// 主画面换了目标单元格时由 <see cref="SetCurrentValue"/> 更新。
        /// </summary>
        private string _currentValue = string.Empty;

        /// <summary>重建列表期间抑制「选中变化」引起的副作用。</summary>
        private bool _isLoading;

        /// <summary>
        /// 这次要写入的那一行的「部品名」（<c>colPartName</c>）：用它去置换表
        /// <c>DL_BuhinBuzaiMapping</c> 查对应的部材名，显示在顶部那一行。
        /// 主画面换了目标行时由 <see cref="SetTarget"/> 更新；空串 = 那一行没填部品名。
        /// </summary>
        private string _partName = string.Empty;

        /// <summary>
        /// 部品名 → 部材名 的查询结果缓存（键 = 部品名，Ordinal）：
        /// 主画面在同一个部品名 上反复点「部材名」时不再重复连 SQL Server。
        /// 查询失败的结果<b>不</b>缓存（下次点还会再试一次）。
        /// </summary>
        private readonly Dictionary<string, string[]> _mappingCache =
            new Dictionary<string, string[]>(StringComparer.Ordinal);

        /// <summary>顶部那行「部品名 → 部材名」现在给出的部材名（点那一行就写入这个值；空 = 不能点）。</summary>
        private string _mappingTarget = string.Empty;

        /// <summary>顶部「部品名 → 部材名」那行文字的正常颜色（查到 / 提示）。</summary>
        private static readonly Color MappingNormalColor = Color.FromArgb(52, 73, 94);

        /// <summary>顶部「部品名 → 部材名」那行文字命中时的颜色（深绿色，表示点一下就能写入）。</summary>
        private static readonly Color MappingHitColor = Color.FromArgb(27, 94, 32);

        /// <summary>顶部「部品名 → 部材名」那行文字查不到 / 查询失败时的颜色。</summary>
        private static readonly Color MappingMissColor = Color.FromArgb(198, 40, 40);

        /// <summary>顶部那一行文字的 ToolTip（名字太长被裁掉时也能看到完整内容）。</summary>
        private readonly ToolTip _mappingToolTip = new ToolTip();

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
        /// （SQL Server 表 <c>DL_BuhinBuzaiMapping</c>）查对应的部材名，显示在顶部那一行。
        /// </summary>
        /// <param name="catalog">部材部品マスタ（可为 <c>null</c>：候选列表只剩原值拆出来的项）。</param>
        /// <param name="currentValue">「部材名」单元格当前的值（<c>|</c> 分开的多项会拆开加进候选）。</param>
        /// <param name="partName">同一行的「部品名」（<c>colPartName</c>；空 = 不知道，只显示提示）。</param>
        public MemberNamePickerForm(MemberPartCatalog catalog, string currentValue, string partName)
        {
            InitializeComponent();

            _catalog = catalog;
            _currentValue = currentValue == null ? string.Empty : currentValue.Trim();
            _partName = partName == null ? string.Empty : partName.Trim();

            LoadItems();
            ApplyFilter(string.Empty);
            ShowMapping();
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
            // 只换写入目标、部品名不变（顶部那一行会按同一个部品名 重新查一次，命中缓存时是零开销）
            SetTarget(currentValue, _partName);
        }

        /// <summary>
        /// 切换「要写入的目标单元格」并同时更新这一行的<b>部品名</b>
        /// （主画面点了另一行的「部材名」时调用）：列表里会把这一格现在的值预先选中，
        /// 顶部那一行换成新部品名 在置换表里对应的部材名。
        /// </summary>
        /// <param name="currentValue">目标单元格当前的值（可为 <c>null</c>）。</param>
        /// <param name="partName">同一行的「部品名」（可为 <c>null</c> / 空）。</param>
        public void SetTarget(string currentValue, string partName)
        {
            _currentValue = currentValue == null ? string.Empty : currentValue.Trim();
            _partName = partName == null ? string.Empty : partName.Trim();

            SelectCurrentValue();
            ShowTargetHint();
            ShowMapping();
        }

        /// <summary>
        /// 顶部那一行「部品名 → 部材名」：按 <see cref="_partName"/> 去置换表
        /// （SQL Server 表 <c>DL_BuhinBuzaiMapping</c> 的 <c>BuhinName</c> = 部品名）查 <c>BuzaiName</c> 显示出来。
        /// <list type="bullet">
        ///   <item>查到 → 深绿色，点这一行就把第一个部材名 写进目标单元格；</item>
        ///   <item>没填部品名 / 置换表里没有 / 查询失败 → 红字说明原因（下面的候选列表和手工输入照常可用）。</item>
        /// </list>
        /// </summary>
        private void ShowMapping()
        {
            _mappingTarget = string.Empty;
            lblMapping.Cursor = Cursors.Default;

            string text;
            Color color = MappingNormalColor;

            if (_partName.Length == 0)
            {
                text = "部品名：（这一行没有填部品名）→ 无法从置换表查部材名";
            }
            else
            {
                string[] targets;
                string error;

                if (!TryQueryMapping(_partName, out targets, out error))
                {
                    text = "部品名「" + _partName + "」→ 置换表（" +
                        MemberNameReplacementRepository.TableName + "）查询失败：" + error;
                    color = MappingMissColor;
                }
                else if (targets.Length == 0)
                {
                    text = "部品名「" + _partName + "」→ 置换表（" +
                        MemberNameReplacementRepository.TableName + "）里没有对应的部材名" +
                        "（可在【部材名置换表】里登录）";
                }
                else
                {
                    _mappingTarget = targets[0];
                    lblMapping.Cursor = Cursors.Hand;
                    text = "部品名「" + _partName + "」→ 部材名：" + string.Join(" / ", targets) +
                        "（点这一行直接写入）";
                    color = MappingHitColor;
                }
            }

            lblMapping.Text = text;
            lblMapping.ForeColor = color;
            _mappingToolTip.SetToolTip(lblMapping, text);
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

            MemberMappingDbSettings resolved = MemberMappingDbSettingsStore.Resolve();

            settings = new MemberMappingDbSettings(resolved.Server, resolved.Port, resolved.Database,
                resolved.UserId, resolved.Password, resolved.ConfigurationSource, MappingConnectTimeoutSeconds);
            _mappingSettings = settings;

            return settings;
        }

        /// <summary>
        /// 点顶部那一行「部品名 → 部材名」= 直接把这个部材名 写进目标单元格
        /// （查到多个时写第一个；没查到时这一行不可点）。
        /// </summary>
        private void LblMapping_Click(object sender, EventArgs e)
        {
            if (_mappingTarget.Length == 0)
            {
                return;
            }

            Pick(_mappingTarget);
        }

        /// <summary>
        /// 刷新底部提示：还没写入时显示「目标格现在是什么」，方便确认改的是哪一格
        /// （写入之后由 <see cref="UpdatePreview"/> 换成「已写入：…」）。
        /// </summary>
        private void ShowTargetHint()
        {
            lblPreview.ForeColor = PreviewNormalColor;

            lblPreview.Text = _currentValue.Length == 0
                ? "这一格现在是空的：单击列表里的部材名就会写入。"
                : "这一格现在是：" + _currentValue + "（单击列表就把它换掉）";
        }

        /// <summary>窗口显示时把焦点放到过滤框（直接打字就是搜索）。</summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            txtFilter.Focus();
            txtFilter.SelectAll();
        }

        /// <summary>组装列表项目：原值拆出来的项 → 部材部品マスタ 的部材名。</summary>
        private void LoadItems()
        {
            _isLoading = true;

            try
            {
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
            finally
            {
                _isLoading = false;
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

        /// <summary>按过滤关键字重建列表；关键字为空时显示全部项目。重建后选中目标单元格现在的值。</summary>
        private void ApplyFilter(string keyword)
        {
            string filter = keyword == null ? string.Empty : keyword.Trim();
            string filterKey = MemberPartCatalogLoader.NormalizeKey(filter);

            _isLoading = true;
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

                    ListViewItem row = new ListViewItem(item);
                    row.SubItems.Add(DescribePartNames(item));
                    row.Tag = item;
                    lvNames.Items.Add(row);
                }
            }
            finally
            {
                lvNames.EndUpdate();
                _isLoading = false;
            }

            SelectCurrentValue();
        }

        /// <summary>这个部材名 下面登记的部品名（用 <c> / </c> 连接；没有时为空串）。</summary>
        private string DescribePartNames(string memberName)
        {
            if (_catalog == null)
            {
                return string.Empty;
            }

            IList<string> partNames = _catalog.GetPartNames(memberName);

            if (partNames == null || partNames.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(" / ", new List<string>(partNames).ToArray());
        }

        /// <summary>把列表里等于目标单元格当前值的那一项选中（没有相同项时清空选择）。</summary>
        private void SelectCurrentValue()
        {
            if (_isLoading)
            {
                // 重建列表期间不要动选择（重建完 ApplyFilter 会再调一次）
                return;
            }

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
                    row.EnsureVisible();
                    return;
                }
            }
        }

        /// <summary>
        /// 把一个值交给主画面写入（<paramref name="value"/> 为空串 = 清空这一格），并刷新底部提示。
        /// </summary>
        /// <param name="value">要写进「部材名」单元格的值。</param>
        private void Pick(string value)
        {
            _currentValue = value == null ? string.Empty : value.Trim();

            UpdatePreview();

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
        /// 同一项再点一次也会重新写入（改回原来的值很方便）。
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

            Pick((row.Tag as string) ?? row.Text);
        }

        /// <summary>
        /// 【写入】/「手工输入」里按回车：把输入的内容写进主画面（列表里没有的部材名 用这里写）。
        /// 输入框是空的时确认之后才写空值（= 清空这一格）。
        /// </summary>
        private void BtnOk_Click(object sender, EventArgs e)
        {
            string custom = txtCustom.Text.Trim();

            if (custom.Length == 0 &&
                MessageBox.Show(this,
                    "「手工输入」是空的：点【写入】会清空主画面这一格。\n\n要清空吗？",
                    "确认清空", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }

            Pick(custom);
        }

        /// <summary>【关闭】：关掉本窗口（已经写入的值留在主画面的单元格里）。</summary>
        private void BtnClose_Click(object sender, EventArgs e)
        {
            Close();
        }
    }
}
