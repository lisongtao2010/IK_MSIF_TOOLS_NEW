using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MSIFAITOOLS.Services;

namespace MSIFAITOOLS
{
    /// <summary>
    /// 「部材名置换表」维护画面（SQL Server 表 <c>DL_BuhinBuzaiMapping</c>）。
    ///
    /// <para>
    /// 表里配「部品名（BuhinName）→ 部材名（BuzaiName）」（例：<c>鴨居</c> → <c>上枠</c>），
    /// 保存后立即生效：之后【CMD 分析】第 1 / 2 / 3 步等写进「部材名」的值会按这张表置换
    /// （<see cref="MemberNameReplacementCatalog"/>）。
    /// </para>
    ///
    /// <para>
    /// 连接信息（接続先 / ポート / DB 名 / ID / PASS）由【连接设置】按钮维护
    /// （<see cref="MemberMappingDbSettingsForm"/>），下方灰色小字显示实际连接的目标。
    /// 保存 = 按主键 <c>(BuhinName, BuzaiName)</c> 精确同步（删除 / 新增 / 更新），
    /// 所以「删除行」也要点【保存】才会写回数据库。
    /// </para>
    /// </summary>
    public partial class MemberNameReplacementForm : Form
    {
        /// <summary>数据库路径 / 条数那行文字的正常颜色。</summary>
        private static readonly Color NormalColor = Color.FromArgb(108, 117, 125);

        /// <summary>数据库打不开时的提示颜色。</summary>
        private static readonly Color FailedColor = Color.FromArgb(198, 40, 40);

        /// <summary>载入 / 重新载入期间抑制「有未保存修改」的标记。</summary>
        private bool _isLoading;

        /// <summary>是否有未保存的修改（关闭 / 重新载入时确认用）。</summary>
        private bool _isDirty;

        public MemberNameReplacementForm()
        {
            InitializeComponent();
            LoadRows();
        }

        /// <summary>
        /// 从 SQL Server 表读全部置换规则填进表格（读失败只显示在下方那行，不弹窗）。
        /// </summary>
        private void LoadRows()
        {
            _isLoading = true;

            try
            {
                dgvMap.Rows.Clear();

                string target = MemberNameReplacementRepository.DescribeTarget();
                IList<MemberNameReplacement> rows;

                try
                {
                    using (MemberNameReplacementRepository repository =
                        new MemberNameReplacementRepository())
                    {
                        target = repository.TargetDescription;

                        if (!repository.TableExists())
                        {
                            lblDatabase.Text = "连接：" + target +
                                "（库里没有这张表，请先用建表 SQL 创建）";
                            lblDatabase.ForeColor = FailedColor;
                            _isDirty = false;

                            return;
                        }

                        rows = repository.Query();
                    }
                }
                catch (Exception ex)
                {
                    lblDatabase.Text = "连接：" + target + "（连接失败：" + ex.Message +
                        "，可点【连接设置】确认接続先 / ID / PASS）";
                    lblDatabase.ForeColor = FailedColor;
                    _isDirty = false;

                    return;
                }

                foreach (MemberNameReplacement row in rows)
                {
                    dgvMap.Rows.Add(row.Source, row.Target, row.Note);
                }

                lblDatabase.Text = "连接：" + target + "（" + rows.Count + " 条）";
                lblDatabase.ForeColor = NormalColor;
                _isDirty = false;

                // 画面显示的就是库里的最新内容 → 内存里的置换表也刷新成一致（分析时立即生效）
                MemberNameReplacementCatalog.Reload();
            }
            finally
            {
                _isLoading = false;
            }
        }

        /// <summary>
        /// 把表格里的行读成置换规则（跳过整行空的行）。
        /// 校验：部品名为空、部材名为空、部品名重复 —— 出错时 <paramref name="error"/> 非空。
        /// </summary>
        private List<MemberNameReplacement> CollectRows(out string error)
        {
            List<MemberNameReplacement> rows = new List<MemberNameReplacement>();
            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.Ordinal);
            error = string.Empty;

            for (int index = 0; index < dgvMap.Rows.Count; index++)
            {
                DataGridViewRow gridRow = dgvMap.Rows[index];

                if (gridRow.IsNewRow)
                {
                    continue;
                }

                string source = GetCellText(gridRow, colSource.Index);
                string target = GetCellText(gridRow, colTarget.Index);
                string note = GetCellText(gridRow, colNote.Index);

                if (source.Length == 0 && target.Length == 0)
                {
                    continue;
                }

                int line = index + 1;

                if (source.Length == 0)
                {
                    error = "第 " + line + " 行的「部品名」为空。";

                    return rows;
                }

                if (target.Length == 0)
                {
                    error = "第 " + line + " 行（" + source + "）的「部材名」为空。";

                    return rows;
                }

                int firstLine;

                if (seen.TryGetValue(source, out firstLine))
                {
                    error = "「" + source + "」重复出现（第 " + firstLine + " 行 / 第 " + line + " 行）。";

                    return rows;
                }

                seen[source] = line;
                rows.Add(new MemberNameReplacement(source, target, note));
            }

            return rows;
        }

        /// <summary>取单元格文本（null → 空串，去首尾空格）。</summary>
        private static string GetCellText(DataGridViewRow row, int columnIndex)
        {
            object value = row.Cells[columnIndex].Value;

            return value == null ? string.Empty : Convert.ToString(value).Trim();
        }

        /// <summary>单元格改过 → 标记「有未保存修改」（载入期间不算）。</summary>
        private void DgvMap_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (!_isLoading)
            {
                _isDirty = true;
            }
        }

        /// <summary>行被删掉 → 标记「有未保存修改」（载入期间不算）。</summary>
        private void DgvMap_RowsRemoved(object sender, DataGridViewRowsRemovedEventArgs e)
        {
            if (!_isLoading)
            {
                _isDirty = true;
            }
        }

        /// <summary>【重新载入】：丢掉画面上的修改，重新从数据库读。</summary>
        private void BtnReload_Click(object sender, EventArgs e)
        {
            if (_isDirty &&
                MessageBox.Show(this, "当前有未保存的修改，重新载入会丢掉这些修改。要继续吗？",
                    "部材名置换表", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            LoadRows();
        }

        /// <summary>
        /// 【连接设置】：打开连接信息维护画面（接続先 / ポート / DB 名 / ID / PASS），
        /// 保存后按新连接重新读表。
        /// </summary>
        private void BtnConnection_Click(object sender, EventArgs e)
        {
            if (_isDirty &&
                MessageBox.Show(this,
                    "当前有未保存的修改，切换连接后会重新读表并丢掉这些修改。要继续吗？",
                    "部材名置换表", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            using (MemberMappingDbSettingsForm form = new MemberMappingDbSettingsForm())
            {
                if (form.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }
            }

            _isDirty = false;
            LoadRows();
        }

        /// <summary>【删除选中行】：删掉选中的整行（还没写库，点【保存】才生效）。</summary>
        private void BtnDeleteRow_Click(object sender, EventArgs e)
        {
            dgvMap.EndEdit();

            List<DataGridViewRow> targets = new List<DataGridViewRow>();

            foreach (DataGridViewRow row in dgvMap.SelectedRows)
            {
                if (!row.IsNewRow)
                {
                    targets.Add(row);
                }
            }

            // 只点了单元格（没选中整行）时，删掉当前行
            if (targets.Count == 0 && dgvMap.CurrentRow != null && !dgvMap.CurrentRow.IsNewRow)
            {
                targets.Add(dgvMap.CurrentRow);
            }

            if (targets.Count == 0)
            {
                MessageBox.Show(this, "请先选中要删除的行（点行号选中整行）。",
                    "部材名置换表", MessageBoxButtons.OK, MessageBoxIcon.Information);

                return;
            }

            for (int index = 0; index < targets.Count; index++)
            {
                dgvMap.Rows.Remove(targets[index]);
            }
        }

        /// <summary>
        /// 【保存】：按主键 <c>(BuhinName, BuzaiName)</c> 精确同步到 SQL Server 表（删除 / 新增 / 更新），
        /// 然后刷新内存里的置换表，下一次分析 / 保存即生效。
        /// </summary>
        private void BtnSave_Click(object sender, EventArgs e)
        {
            dgvMap.EndEdit();

            string error;
            List<MemberNameReplacement> rows = CollectRows(out error);

            if (error.Length > 0)
            {
                MessageBox.Show(this, error, "部材名置换表", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                return;
            }

            if (rows.Count == 0 &&
                MessageBox.Show(this, "置换表为空，保存后会删掉数据库里的全部置换规则。要继续吗？",
                    "部材名置换表", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            string target = MemberNameReplacementRepository.DescribeTarget();

            try
            {
                using (MemberNameReplacementRepository repository =
                    new MemberNameReplacementRepository())
                {
                    target = repository.TargetDescription;
                    repository.Save(rows);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存失败：" + ex.Message + Environment.NewLine +
                    "连接：" + target,
                    "部材名置换表", MessageBoxButtons.OK, MessageBoxIcon.Error);

                return;
            }

            // LoadRows() 里会一并刷新内存中的置换表（MemberNameReplacementCatalog.Reload）
            LoadRows();

            MessageBox.Show(this,
                "已保存 " + rows.Count + " 条置换规则（" + target + "）。" + Environment.NewLine +
                "下一次【CMD 分析】/ 保存时生效。",
                "部材名置换表", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>【关闭】。</summary>
        private void BtnClose_Click(object sender, EventArgs e)
        {
            Close();
        }

        /// <summary>关闭前提醒未保存的修改。</summary>
        private void MemberNameReplacementForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_isDirty)
            {
                return;
            }

            if (MessageBox.Show(this, "置换表有未保存的修改，要放弃修改并关闭吗？",
                "部材名置换表", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                e.Cancel = true;
            }
        }
    }
}
