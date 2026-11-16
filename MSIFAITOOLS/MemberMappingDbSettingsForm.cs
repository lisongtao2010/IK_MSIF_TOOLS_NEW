using System;
using System.Drawing;
using System.Windows.Forms;
using MSIFAITOOLS.Services;

namespace MSIFAITOOLS
{
    /// <summary>
    /// 部材名面板用的 SQL Server 连接信息维护画面（表 <c>genpin.dbo.T_M_MergeName</c>）。
    ///
    /// <para>
    /// 保存到 <c>%APPDATA%\MSIFAITOOLS\mapping-db-settings.json</c>（密码用 DPAPI 加密），
    /// 保存后立即生效：【部材名面板】会用新连接重新读表，程序启动时的整表预读也用新连接，无需重启程序。
    /// 【测试连接】会真的连一次数据库，并顺带检查表是否存在。
    /// </para>
    ///
    /// <para>
    /// 取值优先级见 <see cref="MemberMappingDbSettingsStore.Resolve"/>：
    /// 环境变量 → 本画面保存的用户配置 → App.config → 内置默认值。
    /// </para>
    /// </summary>
    public partial class MemberMappingDbSettingsForm : Form
    {
        /// <summary>正常状态的文字颜色。</summary>
        private static readonly Color NormalColor = Color.FromArgb(52, 73, 94);

        /// <summary>失败 / 警告的文字颜色。</summary>
        private static readonly Color FailedColor = Color.FromArgb(198, 40, 40);

        /// <summary>进行中（连接 / 保存）的文字颜色。</summary>
        private static readonly Color BusyColor = Color.FromArgb(230, 126, 34);

        /// <summary>测试连接 / 保存进行中（此时锁定输入与关闭）。</summary>
        private bool _busy;

        public MemberMappingDbSettingsForm()
        {
            InitializeComponent();

            lblSettingsPath.Text = "保存位置：" + MemberMappingDbSettingsStore.SettingsFilePath;
            LoadCurrentSettings();
        }

        /// <summary>用当前生效的配置（环境变量 → 用户配置 → App.config → 默认值）填充控件。</summary>
        private void LoadCurrentSettings()
        {
            MemberMappingDbSettings current = MemberMappingDbSettingsStore.Resolve();

            txtServer.Text = current.Server;
            numPort.Value = current.Port;
            txtDatabase.Text = current.Database;
            txtUser.Text = current.UserId;
            txtPassword.Text = current.Password;

            if (!string.IsNullOrEmpty(MemberMappingDbSettingsStore.LastSettingsError))
            {
                ShowStatus("连接信息文件读取失败：" + MemberMappingDbSettingsStore.LastSettingsError,
                    FailedColor);
                return;
            }

            ShowStatus("当前配置来源：" + current.ConfigurationSource + Environment.NewLine +
                current.Describe(), NormalColor);
        }

        /// <summary>把界面输入读成设置对象；输入不完整时返回 null 并把原因写进 <paramref name="error"/>。</summary>
        private MemberMappingDbSettings TryCollect(out string error)
        {
            error = string.Empty;

            string server = txtServer.Text.Trim();

            if (server.Length == 0)
            {
                error = "请填写接続先（服务器名或 IP）。";
                txtServer.Focus();
                return null;
            }

            string database = txtDatabase.Text.Trim();

            if (database.Length == 0)
            {
                error = "请填写 DB 名（本工具用 genpin）。";
                txtDatabase.Focus();
                return null;
            }

            string userId = txtUser.Text.Trim();

            if (userId.Length == 0)
            {
                error = "请填写登录 ID。";
                txtUser.Focus();
                return null;
            }

            return new MemberMappingDbSettings(server, (int)numPort.Value, database, userId,
                txtPassword.Text, MemberMappingDbSettings.SourceUserSettings);
        }

        /// <summary>【测试连接】：真的连一次库（顺带检查表是否存在），结果只显示不保存。</summary>
        private void BtnTest_Click(object sender, EventArgs e)
        {
            if (_busy)
            {
                return;
            }

            string error;
            MemberMappingDbSettings settings = TryCollect(out error);

            if (settings == null)
            {
                ShowStatus(error, FailedColor);
                return;
            }

            SetBusy(true, "正在连接 " + settings.DescribeTarget() + " ...");

            bool success = false;
            string message = string.Empty;

            try
            {
                success = MemberNameReplacementRepository.TestConnection(settings, out message);
            }
            finally
            {
                SetBusy(false, null);
            }

            ShowStatus(message, success ? Color.FromArgb(46, 125, 50) : FailedColor);
            MessageBox.Show(this, message, success ? "连接测试" : "连接测试（失败）",
                MessageBoxButtons.OK, success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        /// <summary>【保存】：写入用户配置文件，并让置换表缓存用新连接重新读一次。</summary>
        private void BtnSave_Click(object sender, EventArgs e)
        {
            if (_busy)
            {
                return;
            }

            string error;
            MemberMappingDbSettings settings = TryCollect(out error);

            if (settings == null)
            {
                ShowStatus(error, FailedColor);
                return;
            }

            SetBusy(true, "正在保存连接设置...");

            string saveError = string.Empty;
            bool saved = false;

            try
            {
                saved = MemberMappingDbSettingsStore.Save(settings, out saveError);
            }
            finally
            {
                SetBusy(false, null);
            }

            if (!saved)
            {
                ShowStatus("保存失败：" + saveError, FailedColor);
                MessageBox.Show(this, "保存失败：" + saveError, "连接设置",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 让内存里的置换表立刻按新连接重读（分析 / 保存时马上用新规则）
            MemberNameReplacementCatalog.Reload();

            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>【取消】：不改动已保存的连接信息。</summary>
        private void BtnCancel_Click(object sender, EventArgs e)
        {
            if (_busy)
            {
                return;
            }

            DialogResult = DialogResult.Cancel;
            Close();
        }

        /// <summary>「显示密码」勾选切换。</summary>
        private void ChkShowPassword_CheckedChanged(object sender, EventArgs e)
        {
            txtPassword.UseSystemPasswordChar = !chkShowPassword.Checked;
        }

        /// <summary>锁定 / 解锁输入控件（连接 / 保存期间防止重复点击）。</summary>
        private void SetBusy(bool busy, string status)
        {
            _busy = busy;
            UseWaitCursor = busy;

            grpConnection.Enabled = !busy;
            btnTest.Enabled = !busy;
            btnSave.Enabled = !busy;
            btnCancel.Enabled = !busy;
            btnTest.Text = busy ? "连接中..." : "测试连接";

            if (!string.IsNullOrEmpty(status))
            {
                ShowStatus(status, BusyColor);
            }
        }

        /// <summary>更新状态栏文字。</summary>
        private void ShowStatus(string message, Color color)
        {
            lblStatus.ForeColor = color;
            lblStatus.Text = message;
        }

        /// <summary>连接进行中不允许关闭窗口（避免回调访问已释放的控件）。</summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy)
            {
                e.Cancel = true;
                ShowStatus("正在连接数据库，请稍候...", BusyColor);
                return;
            }

            base.OnFormClosing(e);
        }
    }
}
