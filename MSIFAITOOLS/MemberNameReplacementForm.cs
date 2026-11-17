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
    /// 表里配的是「表示部材名 <c>MsifDevName</c>（图纸 / <c>parts_info.json</c> 上取得的词）
    /// → 原部材名 <c>GenpinName</c>（genpin 主档的叫法）」，
    /// 程序启动时整表预读进内存（<see cref="MemberNameReplacementCatalog.Preload"/>），
    /// 之后【CMD 分析】第 1 / 2 / 3 步等<b>自动取得部材名</b>时写进「部材名」的值会按这张表置换
    /// （<see cref="MemberNameReplacementCatalog"/>）；
    /// 「选择部材名」窗口的<b>表示部材名 候补</b>（<c>MsifDevName</c>）也来自这张表
    /// （<see cref="MemberNameReplacementCatalog.GetDisplayNames"/>，不会给出原部材名）；
    /// <b>画面【保存】不置换</b>（用户填 / 选的是什么就登录什么，见 <c>PartBasicInfo.SetMemberNameFromInput</c>）。
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

            // 按钮 / 列头等文字的中日切换（见 Services\UiLocalization.cs）
            UiLocalization.Attach(this);

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
                        UiLocalization.SetText(lblDatabase, "连接：" + target +
                            "（库里没有这张表，请确认 DB 名 / 表名）");
                        lblDatabase.ForeColor = FailedColor;

                        return;
                    }

                    rows = repository.Query();
                }
            }
            catch (Exception ex)
            {
                UiLocalization.SetText(lblDatabase, "连接：" + target + "（连接失败：" + ex.Message +
                    "，可点【连接设置】确认接続先 / ID / PASS）");
                lblDatabase.ForeColor = FailedColor;

                return;
            }

            foreach (MemberNameReplacement row in rows)
            {
                dgvMap.Rows.Add(row.Classification, row.Source, row.Target);
            }

            UiLocalization.SetText(lblDatabase, "连接：" + target + "（有效 " + rows.Count + " 条）");
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
