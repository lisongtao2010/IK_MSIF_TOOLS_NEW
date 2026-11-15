using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MSIFAITOOLS.Services;

namespace MSIFAITOOLS
{
    /// <summary>
    /// 「部品名」（<c>colPartName</c>）单元格的<b>不抢焦点候选列表</b>（IME 的候选窗口那种感觉）。
    ///
    /// <para>
    /// <b>什么时候出现</b>：在「部品名」单元格里打字时，按已输入的内容过滤候选项，
    /// 贴着这一格弹出 <see cref="PartNameCandidatePopup"/>；按 <c>F4</c> 也可以手动开关。
    /// </para>
    ///
    /// <para>
    /// <b>焦点不动</b>：候选窗口是 <c>WS_EX_NOACTIVATE</c>（见 <see cref="PartNameCandidatePopup"/>），
    /// 打字 / 点候选项 / 按数字键都不会把焦点从表格的编辑框抢走，光标位置也不会跳。
    /// </para>
    ///
    /// <para>
    /// <b>「部材名」正在内嵌编辑时不弹</b>：「部材名」和「部品名」共用同一个编辑控件，
    /// 在「部材名」单元格里打字（F2 / 直接打字）时把候选列表收起来、不再跟着弹
    /// （见 <see cref="IsMemberNameCellEditing"/> / <see cref="DgvParts_CellBeginEdit"/>）。
    /// 点「部材名」打开的「选择部材名」窗口（<see cref="MemberNamePickerForm"/>）<b>不算</b>内嵌编辑。
    /// </para>
    ///
    /// <para>
    /// <b>怎么选</b>：数字键 <c>1</c>～<c>9</c> 选第 1～9 项、<c>0</c> 选第 10 项；
    /// 也可以直接用鼠标点。选中后把部品名 写进编辑框并关闭候选列表（继续打字会再弹出来）。
    /// <c>Esc</c> 只关候选列表，不退出单元格编辑。
    /// </para>
    ///
    /// <para>
    /// <b>候选项从哪来 / 谁排在前面</b>（<see cref="PartNameSource"/>；数字越小越靠前，
    /// 列表右侧的灰色标识就是这里的来源）：
    /// <list type="number">
    ///   <item><b>分割</b> —— 备注里带「<see cref="CmdCalloutPartSplitter.RemarkPositionPrefix"/>」的行：
    ///     部材名 是按圈号 / 位置从零件图分割出来的，最可信；</item>
    ///   <item><b>AI</b> —— 「部材名 AI 推荐」（<c>colMemberNameAi</c>）有值的行；</item>
    ///   <item><b>DB</b> —— 部材部品マスタ（<see cref="MemberPartCatalog"/>＝SQLite「部材部品」表，
    ///     保存时由 <see cref="MemberPartCatalogLoader.RegisterRules"/> 登录的关联项目）里
    ///     和本行「部材名」对应的部品名（本行没有部材名 时用整张表）；</item>
    ///   <item><b>表</b> —— 表格里其它行已经填好的部品名（同一张图里通常反复用同样的几种）；</item>
    ///   <item><b>マスタ</b> —— 部品マスタ（<c>部品マスタ\Tbl_部品マスタ.csv</c> 的第 2 列，
    ///     和「部品コード」自动推定用的是同一份）。</item>
    /// </list>
    /// 按归一化后的部品名 去重（先出现的写法为准 → 优先度高的来源优先）。
    /// 过滤顺序：先「以输入内容开头」的，再「包含输入内容」的；同一档里短的在前。
    /// 已经和输入内容完全一样的那一项不再列出（不用再选一次自己）。
    /// </para>
    /// </summary>
    internal sealed partial class PartBasicInfoForm
    {
        /// <summary>
        /// 候选项的来源优先度（数字越小越靠前）+ 列表右侧显示的灰色标识。
        /// <list type="bullet">
        ///   <item><c>Split</c>：备注里带「位置：」的行（部材名 来自分割）；</item>
        ///   <item><c>AiMemberName</c>：「部材名 AI 推荐」有值的行；</item>
        ///   <item><c>DbRelation</c>：部材部品 DB（部材名 → 部品名 的关联项目）；</item>
        ///   <item><c>Grid</c>：表格里其它行；</item>
        ///   <item><c>PartMaster</c>：部品マスタ CSV。</item>
        /// </list>
        /// </summary>
        private static class PartNameSource
        {
            /// <summary>被分割的行（备注含「位置：」），优先度最高。</summary>
            public const int Split = 0;

            /// <summary>分割行在列表右侧显示的标识。</summary>
            public const string SplitHint = "分割";

            /// <summary>部材名 来自 AI 推荐的行（<c>colMemberNameAi</c> 有值）。</summary>
            public const int AiMemberName = 1;

            /// <summary>AI 推荐行在列表右侧显示的标识。</summary>
            public const string AiMemberNameHint = "AI";

            /// <summary>部材部品 DB 里登记的关联项目。</summary>
            public const int DbRelation = 2;

            /// <summary>DB 关联项目在列表右侧显示的标识。</summary>
            public const string DbRelationHint = "DB";

            /// <summary>表格里其它行已有的部品名。</summary>
            public const int Grid = 3;

            /// <summary>表格里其它行在列表右侧显示的标识。</summary>
            public const string GridHint = "表";

            /// <summary>部品マスタ（CSV）的部品名。</summary>
            public const int PartMaster = 4;

            /// <summary>部品マスタ 在列表右侧显示的标识。</summary>
            public const string PartMasterHint = "マスタ";
        }

        /// <summary>候选窗口（懒创建，隐藏后复用）。</summary>
        private PartNameCandidatePopup _partNameCandidatePopup;

        /// <summary>候选窗口这次要写入的单元格（换格 / 换行后引用失效，写之前会再校验一次）。</summary>
        private DataGridViewCell _partNameCandidateCell;

        /// <summary>正在编辑「部品名」的那个编辑框（TextBox），用来读输入内容 / 回写选中的部品名。</summary>
        private TextBox _partNameCandidateEditor;

        /// <summary>程序自己改写编辑框文字期间，不重新计算候选列表（否则选完又立刻弹出来）。</summary>
        private bool _suppressPartNameCandidateRefresh;

        /// <summary>
        /// 编辑框里「初始化用的文字」是否已经填完：
        /// <see cref="DataGridView"/> 进入编辑时会先往编辑框里塞当前值（会触发 <c>TextChanged</c>），
        /// 那不是用户在打字，不能因此弹出候选列表。等这一轮消息处理完再置 true。
        /// </summary>
        private bool _partNameCandidateEditorReady;

        /// <summary>
        /// 「部材名 AI 推荐」列的名字（<c>colMemberNameAi</c>）：有值 = 这一行的部材名 是 AI 推的
        /// （还没人工确认），候选列表里排在「分割」之后、DB 关联之前。
        /// </summary>
        private const string MemberNameAiColumnName = "colMemberNameAi";

        /// <summary>
        /// 「部品名」单元格开始编辑时（单击 / 直接打字 / F2）：挂上编辑框的 <c>TextChanged</c>，
        /// 换成别的列时摘掉并收起候选列表。
        /// </summary>
        private void DgvParts_EditingControlShowing(object sender, DataGridViewEditingControlShowingEventArgs e)
        {
            DetachPartNameCandidateEditor();

            DataGridViewCell cell = dgvParts.CurrentCell;

            if (cell == null ||
                !string.Equals(cell.OwningColumn.Name, PartNameColumnName, StringComparison.Ordinal))
            {
                return;
            }

            TextBox editor = e.Control as TextBox;

            if (editor == null)
            {
                return;
            }

            // 第一次用到时要读部品マスタ / 部材部品マスタ（SQLite / CSV，比较慢）→
            // 进入编辑时就先读好，打字时不会卡一下（之后整画面复用）
            GetPartMasterIndex();
            GetMemberNameCatalog();

            _partNameCandidateEditor = editor;
            _partNameCandidateCell = cell;
            editor.TextChanged += PartNameCandidateEditor_TextChanged;

            // 进入编辑时 DataGridView 会把单元格现在的值塞进编辑框（触发 TextChanged），
            // 那不是用户输入 → 这一轮先不响应，等消息循环空下来再允许弹候选。
            if (IsHandleCreated)
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (ReferenceEquals(_partNameCandidateEditor, editor))
                    {
                        _partNameCandidateEditorReady = true;
                    }
                });
            }
            else
            {
                _partNameCandidateEditorReady = true;
            }
        }

        /// <summary>单元格编辑结束（回车 / 点别处 / 切行）：摘掉编辑框事件并收起候选列表。</summary>
        private void DgvParts_CellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            DetachPartNameCandidateEditor();
        }

        /// <summary>
        /// 单元格开始进入编辑：如果进入的是「部材名」（<c>colMemberName</c>）的<b>内嵌编辑</b>
        /// （F2 / 直接打字），先把「部品名 候補」窗口收掉。
        /// <para>
        /// 「部材名」和「部品名」用的是同一个编辑控件（<see cref="DataGridView.EditingControl"/>），
        /// 候选窗口挂在这个控件的 <c>TextChanged</c> 上 —— 在「部材名」里打字时不能弹部品名的候选
        /// （见 <see cref="IsMemberNameCellEditing"/>）。
        /// </para>
        /// <para>
        /// 这里<b>只收窗口、不摘事件</b>：摘了之后万一这一轮没有回到
        /// <see cref="DgvParts_EditingControlShowing"/>，候选列表就再也弹不出来了。
        /// </para>
        /// </summary>
        private void DgvParts_CellBeginEdit(object sender, DataGridViewCellCancelEventArgs e)
        {
            if (e == null || e.ColumnIndex < 0 || e.ColumnIndex >= dgvParts.Columns.Count)
            {
                return;
            }

            if (IsMemberNameColumn(dgvParts.Columns[e.ColumnIndex]))
            {
                HidePartNameCandidates();
            }
        }

        /// <summary>
        /// 网格里的「部材名」单元格现在是不是<b>内嵌编辑</b>状态（F2 / 直接打字让单元格自己打开编辑框）。
        /// <para>
        /// 点「部材名」打开的是「选择部材名」窗口（<see cref="ShowMemberNamePicker"/>），那<b>不算</b>
        /// 这里的「内嵌编辑」—— 用那个窗口挑部材名 期间，其它列的「部品名 候補」照常可用。
        /// </para>
        /// <para>
        /// 内嵌编辑「部材名」期间不弹「部品名 候補」：两列共用同一个编辑控件，
        /// 候选列表浮在上面既挡视线，数字键 / <c>Esc</c> 还会被
        /// <see cref="PartNameCandidatePopup.PreFilterMessage"/> 拦下来。
        /// </para>
        /// </summary>
        private bool IsMemberNameCellEditing()
        {
            if (!dgvParts.IsCurrentCellInEditMode)
            {
                return false;
            }

            DataGridViewCell cell = dgvParts.CurrentCell;

            return cell != null && IsMemberNameColumn(cell.OwningColumn);
        }

        /// <summary>表格滚动 / 改列宽时单元格位置变了 → 候选列表的位置会不对，直接收起。</summary>
        private void DgvParts_HidePartNameCandidates(object sender, EventArgs e)
        {
            HidePartNameCandidates();
        }

        /// <summary>焦点离开表格（点按钮 / 切到别的画面）→ 收起候选列表。</summary>
        private void DgvParts_Leave(object sender, EventArgs e)
        {
            HidePartNameCandidates();
        }

        /// <summary>编辑框内容变化 → 重新按输入内容过滤候选（空的输入内容不弹）。</summary>
        private void PartNameCandidateEditor_TextChanged(object sender, EventArgs e)
        {
            if (_suppressPartNameCandidateRefresh || _suppressGridEvents ||
                !_partNameCandidateEditorReady)
            {
                return;
            }

            ShowPartNameCandidates(false);
        }

        /// <summary>摘掉编辑框的 <c>TextChanged</c> 并收起候选列表。</summary>
        private void DetachPartNameCandidateEditor()
        {
            TextBox editor = _partNameCandidateEditor;

            _partNameCandidateEditor = null;
            _partNameCandidateEditorReady = false;

            if (editor != null)
            {
                editor.TextChanged -= PartNameCandidateEditor_TextChanged;
            }

            HidePartNameCandidates();
        }

        /// <summary>
        /// 按当前「部品名」编辑框里的内容重新计算候选项并显示 / 刷新候选窗口。
        /// <para>
        /// 网格里的「部材名」单元格正在内嵌编辑（F2 / 直接打字）时直接收起、不显示 ——
        /// 两列共用同一个编辑控件，在「部材名」里打字也会走到这里
        /// （见 <see cref="IsMemberNameCellEditing"/>）。
        /// </para>
        /// </summary>
        /// <param name="allowEmptyPrefix">
        /// true = 输入内容为空也显示候选（按 F4 手动打开时用）；
        /// false = 输入内容为空就收起（打字过程中用，没输入时不打扰）。
        /// </param>
        private void ShowPartNameCandidates(bool allowEmptyPrefix)
        {
            if (IsMemberNameCellEditing())
            {
                // 「部材名」正在内嵌编辑 → 不显示部品名的候选（这一格不是部品名）
                HidePartNameCandidates();

                return;
            }

            DataGridViewCell cell = dgvParts.CurrentCell;

            if (cell == null ||
                !string.Equals(cell.OwningColumn.Name, PartNameColumnName, StringComparison.Ordinal))
            {
                HidePartNameCandidates();

                return;
            }

            if (!dgvParts.IsCurrentCellInEditMode)
            {
                try
                {
                    dgvParts.BeginEdit(true);
                }
                catch (Exception)
                {
                    // 不能进入编辑（只读 / 正在提交等）→ 不显示候选
                    return;
                }
            }

            TextBox editor = dgvParts.EditingControl as TextBox;
            string prefix = editor == null || editor.Text == null ? string.Empty : editor.Text.Trim();

            if (!allowEmptyPrefix && prefix.Length == 0)
            {
                HidePartNameCandidates();

                return;
            }

            if (editor != null && !ReferenceEquals(_partNameCandidateEditor, editor))
            {
                // F4 打开时编辑框可能还没挂过事件（或者换了编辑框）→ 在这里补上
                if (_partNameCandidateEditor != null)
                {
                    _partNameCandidateEditor.TextChanged -= PartNameCandidateEditor_TextChanged;
                }

                _partNameCandidateEditor = editor;
                _partNameCandidateEditorReady = true;
                editor.TextChanged += PartNameCandidateEditor_TextChanged;
            }

            _partNameCandidateCell = cell;

            IList<PartNameCandidateOption> candidates = FindPartNameCandidates(prefix);

            if (candidates.Count == 0)
            {
                HidePartNameCandidates();

                return;
            }

            EnsurePartNameCandidatePopup();

            Rectangle cellBounds = dgvParts.GetCellDisplayRectangle(cell.ColumnIndex, cell.RowIndex, false);

            _partNameCandidatePopup.ShowCandidates(candidates, dgvParts.RectangleToScreen(cellBounds));
        }

        /// <summary>F4：开着就收起，没开就按当前输入内容（可以为空）打开。</summary>
        private void TogglePartNameCandidates()
        {
            if (_partNameCandidatePopup != null && !_partNameCandidatePopup.IsDisposed &&
                _partNameCandidatePopup.Visible)
            {
                HidePartNameCandidates();

                return;
            }

            ShowPartNameCandidates(true);
        }

        /// <summary>收起候选列表（窗口本身留着复用）。</summary>
        private void HidePartNameCandidates()
        {
            PartNameCandidatePopup popup = _partNameCandidatePopup;

            if (popup != null && !popup.IsDisposed && popup.Visible)
            {
                popup.HideCandidates();
            }
        }

        /// <summary>懒创建候选窗口，并挂上「选中某一项」的回调。</summary>
        private void EnsurePartNameCandidatePopup()
        {
            if (_partNameCandidatePopup != null && !_partNameCandidatePopup.IsDisposed)
            {
                return;
            }

            PartNameCandidatePopup popup = new PartNameCandidatePopup(this);
            popup.CandidatePicked += PartNameCandidatePopup_CandidatePicked;
            _partNameCandidatePopup = popup;

            // 切到别的程序（本画面失去激活）时把候选列表收掉 —— 候选窗口不激活，
            // 收不到 Deactivate，只能由本画面代劳
            Deactivate += PartBasicInfoForm_Deactivate;
        }

        /// <summary>本画面失去激活（切到别的程序 / 别的窗口）→ 收起候选列表。</summary>
        private void PartBasicInfoForm_Deactivate(object sender, EventArgs e)
        {
            HidePartNameCandidates();
        }

        /// <summary>
        /// 候选窗口选中了某一项：把部品名 写进正在编辑的「部品名」编辑框（光标放到末尾，继续打字）。
        /// 单元格仍然是编辑状态，焦点一直在表格里。
        /// </summary>
        /// <param name="partName">选中的部品名（候选项里的原文）。</param>
        private void PartNameCandidatePopup_CandidatePicked(string partName)
        {
            DataGridViewCell cell = _partNameCandidateCell;

            if (cell == null || cell.OwningRow == null ||
                !ReferenceEquals(cell.OwningRow.DataGridView, dgvParts) ||
                !ReferenceEquals(dgvParts.CurrentCell, cell))
            {
                // 期间换了格 / 删了行 → 不写，免得写到别的地方
                return;
            }

            bool previous = _suppressPartNameCandidateRefresh;

            _suppressPartNameCandidateRefresh = true;

            try
            {
                TextBox editor = dgvParts.IsCurrentCellInEditMode
                    ? dgvParts.EditingControl as TextBox
                    : null;

                if (editor != null)
                {
                    editor.Text = partName;
                    editor.SelectionStart = editor.TextLength;
                    editor.SelectionLength = 0;
                }
                else
                {
                    cell.Value = partName;
                }
            }
            finally
            {
                _suppressPartNameCandidateRefresh = previous;
            }
        }

        /// <summary>关闭本画面时释放候选窗口（它是 Owned 窗口，这里再显式收一次尾）。</summary>
        private void ClosePartNameCandidatePopup()
        {
            PartNameCandidatePopup popup = _partNameCandidatePopup;

            _partNameCandidatePopup = null;
            _partNameCandidateCell = null;

            DetachPartNameCandidateEditor();

            Deactivate -= PartBasicInfoForm_Deactivate;

            if (popup != null && !popup.IsDisposed)
            {
                popup.CandidatePicked -= PartNameCandidatePopup_CandidatePicked;
                popup.Close();
            }
        }

        /// <summary>
        /// 收集「部品名」的候选项并按 <paramref name="prefix"/> 过滤。
        /// <para>
        /// <b>优先度</b>（见 <see cref="PartNameSource"/>；越小越靠前，列表右侧的灰色标识就是这里的来源）：
        /// <list type="number">
        ///   <item><b>分割</b>：备注里带「位置：」（<see cref="CmdCalloutPartSplitter.RemarkPositionPrefix"/>）
        ///     的行 —— 部材名 是按圈号 / 位置从零件图分割出来的，最可信；</item>
        ///   <item><b>AI</b>：「部材名 AI 推荐」（<c>colMemberNameAi</c>）有值的行；</item>
        ///   <item><b>DB</b>：部材部品マスタ（<see cref="MemberPartCatalog"/>＝SQLite「部材部品」表）里
        ///     和本行「部材名」对应的部品名（本行没有部材名 / 表里没登记时用整张表）；</item>
        ///   <item><b>表</b>：表格里其它行已经填好的部品名（同一张图里通常反复用同样的几种）；</item>
        ///   <item><b>マスタ</b>：部品マスタ（<c>部品マスタ\Tbl_部品マスタ.csv</c>，
        ///     <see cref="CmdPartMasterIndex.PartNames"/>）—— 和「部品コード」自动推定用的是同一份。</item>
        /// </list>
        /// 按归一化后的部品名 去重（先出现的写法为准 → 优先度高的来源优先），最多返回
        /// <see cref="PartNameCandidatePopup.MaxCandidates"/> 项。
        /// </para>
        /// </summary>
        /// <param name="prefix">正在输入的内容（空 = 不过滤）。</param>
        /// <returns>
        /// 排好序的候选项（不含和 <paramref name="prefix"/> 完全一样的那一项；
        /// 输入内容本身就是某个部品名 的完整写法时返回空列表 —— 已经输完了，不用再选）。
        /// </returns>
        private IList<PartNameCandidateOption> FindPartNameCandidates(string prefix)
        {
            List<PartNameCandidate> pool = new List<PartNameCandidate>();
            Dictionary<string, PartNameCandidate> byKey =
                new Dictionary<string, PartNameCandidate>(StringComparer.Ordinal);

            // ① 被分割的行（部材名 来自按圈号 / 位置 分割）
            foreach (DataGridViewRow row in dgvParts.Rows)
            {
                if (row.IsNewRow || !IsSplitRow(row))
                {
                    continue;
                }

                AddPartNameCandidate(pool, byKey, GetCellText(row, PartNameColumnName),
                    PartNameSource.Split, PartNameSource.SplitHint);
            }

            // ② 部材名 是 AI 推荐的行
            foreach (DataGridViewRow row in dgvParts.Rows)
            {
                if (row.IsNewRow || !IsAiMemberNameRow(row))
                {
                    continue;
                }

                AddPartNameCandidate(pool, byKey, GetCellText(row, PartNameColumnName),
                    PartNameSource.AiMemberName, PartNameSource.AiMemberNameHint);
            }

            // ③ 部材部品 DB 里和本行部材名 关联的部品名（保存时登录的关联项目）
            AddMemberPartCandidates(pool, byKey, GetEditingRowMemberName());

            // ④ 表格里其它行已经填好的部品名
            foreach (DataGridViewRow row in dgvParts.Rows)
            {
                if (row.IsNewRow)
                {
                    continue;
                }

                AddPartNameCandidate(pool, byKey, GetCellText(row, PartNameColumnName),
                    PartNameSource.Grid, PartNameSource.GridHint);
            }

            // ⑤ 部品マスタ（部品コード 自动推定用的同一份 CSV）里的部品名 一览 → 标准写法
            CmdPartMasterIndex partMaster = GetPartMasterIndex();

            if (partMaster != null && partMaster.PartNames != null)
            {
                foreach (string partName in partMaster.PartNames)
                {
                    AddPartNameCandidate(pool, byKey, partName, PartNameSource.PartMaster,
                        PartNameSource.PartMasterHint);
                }
            }

            string prefixKey = MemberPartCatalogLoader.NormalizeKey(prefix);
            List<PartNameCandidate> matched = new List<PartNameCandidate>();

            // 输入内容已经和某个部品名 完全一样（刚选完 / 手打完整）→ 不再弹候选，
            // 免得选完一项立刻又冒出一堆「包含这几个字」的别的名字
            if (prefixKey.Length > 0 && byKey.ContainsKey(prefixKey))
            {
                return new List<PartNameCandidateOption>();
            }

            foreach (PartNameCandidate candidate in pool)
            {
                if (prefixKey.Length == 0)
                {
                    matched.Add(candidate);

                    continue;
                }

                if (string.Equals(candidate.Key, prefixKey, StringComparison.Ordinal))
                {
                    // 已经是输入内容的完整写法 → 不用再列出来
                    continue;
                }

                int found = candidate.Key.IndexOf(prefixKey, StringComparison.Ordinal);

                if (found < 0)
                {
                    continue;
                }

                // 以输入内容开头的排在前面（0 档），只是包含的排在后面（10 档）
                candidate.Rank = (found == 0 ? 0 : 10) + candidate.SourceOrder;
                matched.Add(candidate);
            }

            matched.Sort(ComparePartNameCandidates);

            List<PartNameCandidateOption> result = new List<PartNameCandidateOption>();

            foreach (PartNameCandidate candidate in matched)
            {
                if (result.Count >= PartNameCandidatePopup.MaxCandidates)
                {
                    break;
                }

                result.Add(new PartNameCandidateOption(candidate.Text, candidate.Hint));
            }

            return result;
        }

        /// <summary>
        /// 这一行的「部材名」是不是 AI 推荐的那个：「部材名 AI 推荐」（<c>colMemberNameAi</c>）有值、
        /// 并且「部材名」为空或与推荐值相同（归一化后比较，半角カナ / 全角英数 / 空白 / 大小写都算一样）。
        /// <para>
        /// 人工改过部材名 的行<b>不算</b> —— 那种行的部材名 已经是用户确认过的，
        /// 候选顺序里归到「表格其它行」这一档，不再冒充 AI 推荐。
        /// </para>
        /// </summary>
        private static bool IsAiMemberNameRow(DataGridViewRow row)
        {
            string suggestion = GetCellText(row, MemberNameAiColumnName);

            if (suggestion.Length == 0)
            {
                return false;
            }

            string memberName = GetCellText(row, MemberNameColumnName);

            if (memberName.Length == 0)
            {
                return true;
            }

            return string.Equals(MemberPartCatalogLoader.NormalizeKey(memberName),
                MemberPartCatalogLoader.NormalizeKey(suggestion), StringComparison.Ordinal);
        }

        /// <summary>
        /// 丢掉部材部品マスタ 的缓存（<see cref="_memberNameCatalog"/>）：
        /// 保存时 <see cref="MemberPartCatalogLoader.RegisterRules"/> 会往「部材部品」表登录新的关联规则，
        /// 不丢缓存的话「DB」这一档候选要到下次打开画面才会包含刚保存的部品名。
        /// 只清引用，真正读库还是由 <see cref="GetMemberNameCatalog"/> 按需做（失败也不影响其它功能）。
        /// </summary>
        private void InvalidateMemberPartCatalogCache()
        {
            _memberNameCatalog = null;
            _memberNameCatalogLoaded = false;
        }

        /// <summary>
        /// 正在编辑的那一行（候选窗口对应的行）的「部材名」；取不到时为空串。
        /// 多值时原样返回（<c>|</c> 连接），由 <see cref="AddMemberPartCandidates"/> 拆开查。
        /// </summary>
        private string GetEditingRowMemberName()
        {
            DataGridViewCell cell = _partNameCandidateCell;

            if (cell == null || cell.OwningRow == null)
            {
                cell = dgvParts.CurrentCell;
            }

            if (cell == null || cell.OwningRow == null)
            {
                return string.Empty;
            }

            return GetCellText(cell.OwningRow, MemberNameColumnName);
        }

        /// <summary>
        /// 把部材部品マスタ（<see cref="MemberPartCatalog"/>＝SQLite「部材部品」表，
        /// 保存时由 <see cref="MemberPartCatalogLoader.RegisterRules"/> 登录的关联项目）里
        /// 和 <paramref name="memberName"/> 关联的部品名 加进池子（优先度 = DB）。
        /// <para>
        /// <paramref name="memberName"/> 可能是多值（<c>|</c> 连接）→ 每一段都查；
        /// 一段都没查到（本行还没填部材名 / 表里没登记）时退回整张表 —— 候选不会因此变少，
        /// 只是排不到「DB」那一档。
        /// </para>
        /// </summary>
        private void AddMemberPartCandidates(List<PartNameCandidate> pool,
            Dictionary<string, PartNameCandidate> byKey, string memberName)
        {
            MemberPartCatalog catalog = GetMemberNameCatalog();

            if (catalog == null || catalog.MemberNames == null)
            {
                return;
            }

            bool found = false;
            string[] memberNames = (memberName ?? string.Empty).Split('|');

            foreach (string member in memberNames)
            {
                string value = member.Trim();

                if (value.Length == 0 || !HasMemberPartNames(catalog, value))
                {
                    continue;
                }

                AddMemberPartNames(pool, byKey, catalog, value);
                found = true;
            }

            if (found)
            {
                return;
            }

            foreach (string catalogMember in catalog.MemberNames)
            {
                AddMemberPartNames(pool, byKey, catalog, catalogMember);
            }
        }

        /// <summary>「部材部品」表里这个部材名 有没有登记部品名（没登记就不用查了）。</summary>
        private static bool HasMemberPartNames(MemberPartCatalog catalog, string memberName)
        {
            IList<string> partNames = catalog.GetPartNames(memberName);

            return partNames != null && partNames.Count > 0;
        }

        /// <summary>把一个部材名 关联的部品名 全部加进池子（优先度 = DB 关联）。</summary>
        private static void AddMemberPartNames(List<PartNameCandidate> pool,
            Dictionary<string, PartNameCandidate> byKey, MemberPartCatalog catalog, string memberName)
        {
            IList<string> partNames = catalog.GetPartNames(memberName);

            if (partNames == null)
            {
                return;
            }

            foreach (string partName in partNames)
            {
                AddPartNameCandidate(pool, byKey, partName, PartNameSource.DbRelation,
                    PartNameSource.DbRelationHint);
            }
        }

        /// <summary>把候选项加进池子里（空值 / 重复（归一化后相同）时忽略）。</summary>
        /// <param name="pool">候选项池（保持加入顺序）。</param>
        /// <param name="byKey">归一化后的部品名 → 候选项（去重用）。</param>
        /// <param name="value">部品名的原文。</param>
        /// <param name="sourceOrder">
        /// 来源的优先度（<see cref="PartNameSource"/>：0 = 分割行，1 = 部材名 AI 推荐，
        /// 2 = 部材部品 DB，3 = 表格其它行，4 = 部品マスタ）。先加进来的优先度更高，重复的不覆盖。
        /// </param>
        /// <param name="hint">列表右侧显示的来源标识（<c>分割</c> / <c>AI</c> / <c>DB</c> / <c>表</c> / <c>マスタ</c>）。</param>
        private static void AddPartNameCandidate(List<PartNameCandidate> pool,
            Dictionary<string, PartNameCandidate> byKey, string value, int sourceOrder, string hint)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            string text = value.Trim();
            string key = MemberPartCatalogLoader.NormalizeKey(text);

            if (key.Length == 0 || byKey.ContainsKey(key))
            {
                return;
            }

            PartNameCandidate candidate = new PartNameCandidate(text, key, sourceOrder, hint);

            byKey.Add(key, candidate);
            pool.Add(candidate);
        }

        /// <summary>候选项排序：优先度 → 短的在前面 → 字典序（保证同一个输入内容每次顺序一样）。</summary>
        private static int ComparePartNameCandidates(PartNameCandidate left, PartNameCandidate right)
        {
            int result = left.Rank.CompareTo(right.Rank);

            if (result != 0)
            {
                return result;
            }

            result = left.Key.Length.CompareTo(right.Key.Length);

            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(left.Key, right.Key);

            return result != 0 ? result : string.CompareOrdinal(left.Text, right.Text);
        }

        /// <summary>候选项：显示用的原文 + 来源标识 + 归一化后的比较键 + 来源优先度 / 本次排序用的档位。</summary>
        private sealed class PartNameCandidate
        {
            public PartNameCandidate(string text, string key, int sourceOrder, string hint)
            {
                Text = text;
                Key = key;
                SourceOrder = sourceOrder;
                Rank = sourceOrder;
                Hint = hint ?? string.Empty;
            }

            /// <summary>显示 / 写入用的部品名 原文。</summary>
            public string Text { get; private set; }

            /// <summary>归一化后的部品名（去重 / 过滤用）。</summary>
            public string Key { get; private set; }

            /// <summary>列表右侧显示的来源标识（<c>分割</c> / <c>AI</c> / <c>DB</c> / <c>表</c> / <c>マスタ</c>）。</summary>
            public string Hint { get; private set; }

            /// <summary>来源的优先度（见 <see cref="PartNameSource"/>；越小越靠前）。</summary>
            public int SourceOrder { get; private set; }

            /// <summary>本次排序用的档位（「以输入内容开头」= 0 档，「包含」= 10 档，再加来源优先度）。</summary>
            public int Rank { get; set; }
        }
    }
}
