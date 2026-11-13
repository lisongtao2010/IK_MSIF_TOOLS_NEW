using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MSIFAITOOLS.Services;

namespace MSIFAITOOLS
{
    /// <summary>
    /// 「部材名」输入辅助窗口：由部品基本信息画面（<see cref="PartBasicInfoForm"/>）
    /// 点「部材名」单元格时弹出，用来从<b>常用部材名</b>里挑（而不是让人对着图纸手打）。
    ///
    /// <para>
    /// <b>候选项</b> = 部材部品マスタ（<c>D:\MSIFDB\MSIF_MemberParts.db</c> 的「部材部品」表，
    /// 首次运行时由 <c>部材部品\部材部品.csv</c> 导入）里的部材名 一览
    /// （<see cref="MemberPartCatalog.MemberNames"/>，去重、保持登录顺序）。第二列同时列出
    /// 这个部材名 下面登记的部品名，方便在写法相近的部材名 之间判断。
    /// </para>
    ///
    /// <para>
    /// <b>单元格原来的值</b>如果是 <c>|</c>（或全角 <c>｜</c>）分开的多项
    /// （例：<c>鴨居|上レール</c>），那些项也会加进列表并<b>预先勾选</b>；
    /// 勾选多项后按列表顺序用 <c>|</c> 连接写回（与【CMD 分析】第 1 / 2 / 3 步生成的写法一致）。
    /// 列表里没有的名字可以在「手工输入」里直接写，写的内容原样写回单元格。
    /// </para>
    /// </summary>
    internal sealed partial class MemberNamePickerForm : Form
    {
        /// <summary>多个部材名 之间的分隔符（与【CMD 分析】写进数据库的写法一致）。</summary>
        public const char Separator = '|';

        /// <summary>拆分原值时认可的竖线：半角 <c>|</c> 与全角 <c>｜</c>（老数据 / 手打时两种都有）。</summary>
        private static readonly char[] ValueSeparators = new char[] { Separator, '｜' };

        /// <summary>「将写入」那行文字的正常颜色。</summary>
        private static readonly Color PreviewNormalColor = Color.FromArgb(52, 73, 94);

        /// <summary>「将写入」那行文字在结果为空时的颜色（提示会清空单元格）。</summary>
        private static readonly Color PreviewEmptyColor = Color.FromArgb(198, 40, 40);

        /// <summary>勾选状态（部材名 原文 → 是否勾选）：过滤时重建列表也要保留。</summary>
        private readonly Dictionary<string, bool> _checkedItems =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        /// <summary>列表里的全部项目（原值拆出来的项在前，候选项在后；保持这个顺序输出）。</summary>
        private readonly List<string> _items = new List<string>();

        /// <summary>打开时单元格里的原值（判断「值有没有变」用）。</summary>
        private readonly string _originalValue = string.Empty;

        /// <summary>部材部品マスタ（可能为 <c>null</c>：读不到时只有空列表 + 手工输入）。</summary>
        private readonly MemberPartCatalog _catalog;

        /// <summary>重建列表期间抑制「勾选变化」（避免把默认勾选状态当成用户操作）。</summary>
        private bool _isLoading;

        public MemberNamePickerForm()
            : this(null, string.Empty)
        {
        }

        /// <summary>
        /// 创建「部材名」辅助窗口。
        /// </summary>
        /// <param name="catalog">部材部品マスタ（可为 <c>null</c>：候选列表只剩原值拆出来的项）。</param>
        /// <param name="currentValue">单元格当前的值（<c>|</c> 分开的多项会拆开并预先勾选）。</param>
        public MemberNamePickerForm(MemberPartCatalog catalog, string currentValue)
        {
            InitializeComponent();

            _catalog = catalog;
            _originalValue = currentValue == null ? string.Empty : currentValue.Trim();

            LoadItems();
            ApplyFilter(string.Empty);
        }

        /// <summary>确定后要写回单元格的值（勾选项按列表顺序用 <c>|</c> 连接；一项都没勾时是空串）。</summary>
        public string SelectedValue
        {
            get { return BuildValue(); }
        }

        /// <summary>窗口显示时把焦点放到过滤框（直接打字就是搜索）。</summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            txtFilter.Focus();
            txtFilter.SelectAll();
        }

        /// <summary>组装列表项目：原值拆出来的项（勾上）→ 部材部品マスタ 的部材名。</summary>
        private void LoadItems()
        {
            _isLoading = true;

            try
            {
                foreach (string part in SplitValue(_originalValue))
                {
                    AddItem(part);
                    _checkedItems[part] = true;
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

            if (!_checkedItems.ContainsKey(text))
            {
                _checkedItems[text] = false;
            }

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

        /// <summary>按过滤关键字重建列表（勾选状态保留）；关键字为空时显示全部项目。</summary>
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
                    row.Checked = IsChecked(item);
                    lvNames.Items.Add(row);
                }
            }
            finally
            {
                lvNames.EndUpdate();
                _isLoading = false;
            }

            UpdatePreview();
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

        /// <summary>这一项当前是否勾选。</summary>
        private bool IsChecked(string item)
        {
            bool value;

            return _checkedItems.TryGetValue(item, out value) && value;
        }

        /// <summary>当前勾选项的部材名（按列表顺序）。</summary>
        private List<string> GetCheckedItems()
        {
            List<string> selected = new List<string>();

            foreach (string item in _items)
            {
                if (IsChecked(item))
                {
                    selected.Add(item);
                }
            }

            return selected;
        }

        /// <summary>
        /// 算写回单元格的值：「手工输入」有内容时原样用它（列表外的部材名），
        /// 否则勾选项按列表顺序用 <c>|</c> 连接；一项都没勾时返回空串（= 清空单元格）。
        /// </summary>
        private string BuildValue()
        {
            string custom = txtCustom.Text.Trim();

            if (custom.Length > 0)
            {
                return custom;
            }

            List<string> selected = GetCheckedItems();

            return selected.Count == 0
                ? string.Empty
                : string.Join(Separator.ToString(), selected.ToArray());
        }

        /// <summary>刷新「将写入」那行（空值用红色提示会清空单元格）。</summary>
        private void UpdatePreview()
        {
            string value = BuildValue();

            lblPreview.Text = "将写入：" + (value.Length == 0 ? "（空 → 清空这一格）" : value);
            lblPreview.ForeColor = value.Length == 0 ? PreviewEmptyColor : PreviewNormalColor;
        }

        /// <summary>过滤关键字变化 → 重建列表。</summary>
        private void TxtFilter_TextChanged(object sender, EventArgs e)
        {
            ApplyFilter(txtFilter.Text);
        }

        /// <summary>手工输入的内容变化 → 刷新预览。</summary>
        private void TxtCustom_TextChanged(object sender, EventArgs e)
        {
            UpdatePreview();
        }

        /// <summary>勾选变化 → 记进勾选表 + 刷新预览。</summary>
        private void LvNames_ItemChecked(object sender, ItemCheckedEventArgs e)
        {
            if (_isLoading || e.Item == null)
            {
                return;
            }

            string item = (e.Item.Tag as string) ?? e.Item.Text;

            _checkedItems[item] = e.Item.Checked;

            UpdatePreview();
        }

        /// <summary>双击列表项 = 切换勾选（不用精确点中复选框）。</summary>
        private void LvNames_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            ListViewItem item = lvNames.GetItemAt(e.X, e.Y);

            if (item == null)
            {
                return;
            }

            item.Checked = !item.Checked;
        }

        /// <summary>「确定」：一项都没勾会清空单元格，原来有值时会再确认一次。</summary>
        private void BtnOk_Click(object sender, EventArgs e)
        {
            if (BuildValue().Length == 0 && _originalValue.Length > 0 &&
                MessageBox.Show(this,
                    "没有勾选任何部材名，确定要把这一格清空吗？",
                    "确认清空", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                // 不关闭窗口，回到选择状态
                DialogResult = DialogResult.None;
            }
        }
    }
}
