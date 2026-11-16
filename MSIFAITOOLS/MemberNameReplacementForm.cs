using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MSIFAITOOLS.Services;

namespace MSIFAITOOLS
{
    /// <summary>
    /// 「部材名面板」（部材名置换表・参照専用）：SQL Server 表 <c>genpin.dbo.T_M_MergeName</c> 的一览。
    ///
    /// <para>
    /// 表里配的是「图纸 / <c>parts_info.json</c> 上取得的词（<c>MsifDevName</c>）→ 要置换成的词（<c>GenpinName</c>）」，
    /// 程序启动时整表预读进内存（<see cref="MemberNameReplacementCatalog.Preload"/>），
    /// 之后【CMD 分析】第 1 / 2 / 3 步等写进「部材名」的值会按这张表置换
    /// （<see cref="MemberNameReplacementCatalog"/>）。
    /// </para>
    ///
    /// <para>
    /// <b>本画面只读</b>：<c>T_M_MergeName</c> 由 genpin 侧维护（有 <c>DeleteFlg</c> / <c>UpdateBy</c> 等管理列），
    /// 这里只显示有效行（<c>DeleteFlg = 0</c>），要改请到维护方系统改，改完后点【重新载入】。
    /// 连接信息（接続先 / ポート / DB 名 / ID / PASS）由【连接设置】按钮维护
    /// （<see cref="MemberMappingDbSettingsForm"/>），下方灰色小字显示实际连接的目标。
    /// </para>
    /// </summary>
    public partial class MemberNameReplacementForm : Form
    {
        /// <summary>连接目标 / 条数那行文字的正常颜色。</summary>
        private static readonly Color NormalColor = Color.FromArgb(108, 117, 125);

        /// <summary>数据库打不开时的提示颜色。</summary>
        private static readonly Color FailedColor = Color.FromArgb(198, 40, 40);

        public MemberNameReplacementForm()
        {
            InitializeComponent();
            LoadRows();
        }

        /// <summary>
        /// 从 SQL Server 表 <c>T_M_MergeName</c> 读全部有效行（<c>DeleteFlg = 0</c>）填进表格
        /// （读失败只显示在下方那行，不弹窗）。
        /// </summary>
        private void LoadRows()
        {
            dgvMap.Rows.Clear();

            string target = MemberNameReplacementRepository.DescribeTarget();
            IList<MemberNameReplacement> rows;

            try
            {
                using (MemberNameReplacementRepository repository = new MemberNameReplacementRepository())
                {
                    target = repository.TargetDescription;

                    if (!repository.TableExists())
                    {
                        lblDatabase.Text = "连接：" + target +
                            "（库里没有这张表，请确认 DB 名 / 表名）";
                        lblDatabase.ForeColor = FailedColor;

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

                return;
            }

            foreach (MemberNameReplacement row in rows)
            {
                dgvMap.Rows.Add(row.Classification, row.Source, row.Target);
            }

            lblDatabase.Text = "连接：" + target + "（有效 " + rows.Count + " 条）";
            lblDatabase.ForeColor = NormalColor;

            // 画面显示的就是库里的最新内容 → 内存里的置换表也刷新成一致（分析时立即生效）
            MemberNameReplacementCatalog.Reload();
        }

        /// <summary>【重新载入】：重新从数据库读（同时刷新内存里的置换表）。</summary>
        private void BtnReload_Click(object sender, EventArgs e)
        {
            LoadRows();
        }

        /// <summary>
        /// 【连接设置】：打开连接信息维护画面（接続先 / ポート / DB 名 / ID / PASS），
        /// 保存后按新连接重新读表。
        /// </summary>
        private void BtnConnection_Click(object sender, EventArgs e)
        {
            using (MemberMappingDbSettingsForm form = new MemberMappingDbSettingsForm())
            {
                if (form.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }
            }

            LoadRows();
        }

        /// <summary>【关闭】。</summary>
        private void BtnClose_Click(object sender, EventArgs e)
        {
            Close();
        }
    }
}
