using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using MSIFAITOOLS.Services;

namespace MSIFAITOOLS
{
    /// <summary>
    /// AI 设置的独立画面：<b>调用方式</b>（云端 OpenAI 兼容接口 / 本机 Claude Code CLI）、
    /// 服务端点（下拉）、模型（下拉 + 可手工输入）、API Key、超时；
    /// 选择本机 Claude Code 时<b>不需要填 claude 路径</b>：默认「自动查找」（先 PATH，再常见安装位置），
    /// 只有装在非常规位置时才勾掉自动、手工指定。
    /// 保存到 %APPDATA%\MSIFAITOOLS\ai-settings.json，保存后立即生效（无需重启程序）。
    /// </summary>
    public partial class AiSettingsForm : Form
    {
        /// <summary>测试连接时使用的最大超时（秒），避免界面长时间等待。</summary>
        private const int TestTimeoutSeconds = 30;

        /// <summary>模型下拉列表的常用预设（也允许手工输入其它名称）。</summary>
        private static readonly string[] ModelPresets =
        {
            "deepseek-v4-flash",
            "deepseek-v3",
            "deepseek-r1",
            "qwen2.5-72b-instruct",
            "gpt-4o-mini"
        };

        /// <summary>调用方式下拉列表的预设。</summary>
        private static readonly ProviderPreset[] ProviderPresets =
        {
            new ProviderPreset("云端接口（OpenAI 兼容）", AiApiOptions.ProviderCompatibleApi),
            new ProviderPreset("本地 Claude Code CLI", AiApiOptions.ProviderClaudeCodeCli)
        };

        /// <summary>服务端点下拉列表的预设（最后一项为“自定义”）。</summary>
        private static readonly EndpointPreset[] EndpointPresets =
        {
            new EndpointPreset("ModelArts 新加坡（ap-southeast-1）", AiApiOptions.DefaultBaseUrl),
            new EndpointPreset("OpenAI 官方", "https://api.openai.com/v1"),
            new EndpointPreset("DeepSeek 官方", "https://api.deepseek.com/v1"),
            new EndpointPreset("本地 Ollama", "http://localhost:11434/v1"),
            new EndpointPreset("自定义（手动填写）", string.Empty)
        };

        /// <summary>加载配置期间抑制下拉框事件，避免覆盖已保存的接口地址。</summary>
        private bool _loading;

        /// <summary>正在执行“测试连接”（此时锁定输入与关闭）。</summary>
        private bool _busy;

        public AiSettingsForm()
        {
            _loading = true;
            InitializeComponent();
            _loading = false;

            // 按钮 / 标签等文字的中日切换（见 Services\UiLocalization.cs）
            UiLocalization.Attach(this);

            UiLocalization.SetText(lblSettingsPath, "保存位置：" + AiSettingsStore.SettingsFilePath);
            LoadCurrentSettings();
        }

        /// <summary>用当前生效的配置（用户设置 → App.config → 默认值）填充控件。</summary>
        private void LoadCurrentSettings()
        {
            _loading = true;

            try
            {
                AiApiOptions current = AiApiOptions.FromConfiguration();

                txtBaseUrl.Text = current.BaseUrl;
                txtApiKey.Text = current.ApiKey;
                numTimeout.Value = ClampTimeout(current.TimeoutSeconds);

                FillEndpointPresets(current.BaseUrl);
                FillModelPresets(current.Model);
                FillProviderPresets(current.Provider);
                // 路径留空 = 自动查找（一般情况），此时勾上「自动查找」并禁用路径框
                chkClaudeAuto.Checked = current.ClaudeCliPath.Length == 0;
                txtClaudePath.Text = current.ClaudeCliPath;
                txtClaudeModel.Text = current.ClaudeModel;
                txtClaudeEnv.Text = current.ClaudeCliEnvironment;
                UpdateProviderUi();

                if (!string.IsNullOrEmpty(AiApiOptions.LastSettingsError))
                {
                    lblStatus.ForeColor = Color.FromArgb(198, 40, 40);
                    UiLocalization.SetText(lblStatus, "用户配置文件读取失败：" + AiApiOptions.LastSettingsError);
                }
                else if (!string.IsNullOrEmpty(AiApiOptions.LastConfigurationError))
                {
                    lblStatus.ForeColor = Color.FromArgb(198, 40, 40);
                    UiLocalization.SetText(lblStatus, "App.config 读取失败：" + AiApiOptions.LastConfigurationError);
                }
                else
                {
                    lblStatus.ForeColor = Color.FromArgb(52, 73, 94);
                    UiLocalization.SetText(lblStatus, "当前配置来源：" + current.ConfigurationSource);
                }
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>填充服务端点下拉列表，并选中与当前地址匹配的预设。</summary>
        private void FillEndpointPresets(string baseUrl)
        {
            cboEndpointPreset.Items.Clear();
            int matchedIndex = -1;

            for (int i = 0; i < EndpointPresets.Length; i++)
            {
                cboEndpointPreset.Items.Add(EndpointPresets[i]);

                if (EndpointPresets[i].BaseUrl.Length > 0 &&
                    string.Equals(EndpointPresets[i].BaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase))
                {
                    matchedIndex = i;
                }
            }

            // 未匹配到预设时选中“自定义”，由用户决定是否修改地址
            cboEndpointPreset.SelectedIndex = matchedIndex >= 0 ? matchedIndex : EndpointPresets.Length - 1;
        }

        /// <summary>填充模型下拉列表；当前模型不在预设中时插入到首位并选中。</summary>
        private void FillModelPresets(string model)
        {
            cboModel.Items.Clear();

            foreach (string preset in ModelPresets)
            {
                cboModel.Items.Add(preset);
            }

            if (!string.IsNullOrWhiteSpace(model) && !cboModel.Items.Contains(model))
            {
                cboModel.Items.Insert(0, model);
            }

            cboModel.Text = string.IsNullOrWhiteSpace(model) ? AiApiOptions.DefaultModel : model;
        }

        /// <summary>填充调用方式下拉列表，并选中当前配置的方式。</summary>
        private void FillProviderPresets(string provider)
        {
            cboProvider.Items.Clear();

            foreach (ProviderPreset preset in ProviderPresets)
            {
                cboProvider.Items.Add(preset);
            }

            string key = AiApiOptions.NormalizeProvider(provider);
            int matchedIndex = 0;

            for (int i = 0; i < ProviderPresets.Length; i++)
            {
                if (string.Equals(ProviderPresets[i].Key, key, StringComparison.Ordinal))
                {
                    matchedIndex = i;
                }
            }

            cboProvider.SelectedIndex = matchedIndex;
        }

        /// <summary>当前下拉框选中的调用方式（api / claude-cli）。</summary>
        private string SelectedProvider
        {
            get
            {
                ProviderPreset preset = cboProvider.SelectedItem as ProviderPreset;

                return preset == null ? AiApiOptions.ProviderCompatibleApi : preset.Key;
            }
        }

        /// <summary>按调用方式切换要显示的参数区（云端接口 / 本机 Claude Code CLI）。</summary>
        private void UpdateProviderUi()
        {
            bool cli = string.Equals(SelectedProvider, AiApiOptions.ProviderClaudeCodeCli,
                StringComparison.Ordinal);

            grpConnection.Visible = !cli;
            grpCli.Visible = cli;

            UpdateClaudePathUi();
            UpdateProviderNote();
        }

        /// <summary>切换调用方式时立刻切换参数区（不写配置）。</summary>
        private void CboProvider_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_loading)
            {
                return;
            }

            UpdateProviderUi();
        }

        /// <summary>勾选 / 取消「自动查找」时切换路径框可用状态，并刷新提示。</summary>
        private void ChkClaudeAuto_CheckedChanged(object sender, EventArgs e)
        {
            if (_loading)
            {
                return;
            }

            UpdateClaudePathUi();
            UpdateProviderNote();
        }

        /// <summary>
        /// 路径框只在「不自动查找」时可编辑：默认自动查找（PATH → 常见安装位置），
        /// 用户在本机 cmd 里能直接敲 claude 的话，这里什么都不用填。
        /// </summary>
        private void UpdateClaudePathUi()
        {
            bool manual = !chkClaudeAuto.Checked;

            txtClaudePath.Enabled = manual && !_busy;
            btnBrowseClaude.Enabled = manual && !_busy;
        }

        /// <summary>界面上的 claude 路径：勾选「自动查找」时视为空（= 自动）。</summary>
        private string ClaudeCliPathFromUi
        {
            get { return chkClaudeAuto.Checked ? string.Empty : txtClaudePath.Text.Trim(); }
        }

        /// <summary>刷新「调用方式」下面的提示行：cli 模式显示实际找到的 claude 位置。</summary>
        private void UpdateProviderNote()
        {
            if (!string.Equals(SelectedProvider, AiApiOptions.ProviderClaudeCodeCli, StringComparison.Ordinal))
            {
                UiLocalization.SetText(lblProviderNote, "云端接口：OpenAI 兼容的 /chat/completions。");
                return;
            }

            string resolvedPath;
            string error;

            if (ClaudeCodeCliClient.TryResolveExecutable(ClaudeCliPathFromUi, out resolvedPath, out error))
            {
                UiLocalization.SetText(lblProviderNote, "本地 Claude Code：已找到 " + resolvedPath);
            }
            else
            {
                UiLocalization.SetText(lblProviderNote,
                    "本地 Claude Code：未找到 claude（请先在终端确认能运行 claude）");
            }
        }

        /// <summary>
        /// 选择 claude.exe / claude.cmd 的位置（只有装在非常规位置时才需要；选完自动取消「自动查找」）。
        /// </summary>
        private void BtnBrowseClaude_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = UiText.T("选择 Claude Code 程序（claude.exe / claude.cmd）");
                dialog.Filter = "claude（claude.exe / claude.cmd）|claude.exe;claude.cmd;*.exe;*.cmd|所有文件 (*.*)|*.*";

                string current = txtClaudePath.Text.Trim();

                if (current.Length == 0)
                {
                    // 没填过时，从自动找到的位置开始找（找不到就保持系统默认目录）
                    string resolvedPath;
                    string resolveError;

                    current = ClaudeCodeCliClient.TryResolveExecutable(string.Empty,
                        out resolvedPath, out resolveError) ? resolvedPath : string.Empty;
                }

                try
                {
                    string directory = Path.GetDirectoryName(current);

                    if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                    {
                        dialog.InitialDirectory = directory;
                    }
                }
                catch (Exception)
                {
                    // 路径异常时用系统默认目录
                }

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    // 手工指定了路径 → 自动取消「自动查找」，否则填的路径会被忽略
                    chkClaudeAuto.Checked = false;
                    txtClaudePath.Text = dialog.FileName;
                    UpdateProviderNote();
                }
            }
        }

        /// <summary>校验「追加环境变量」的格式（KEY=VALUE，用 ; 分隔）。</summary>
        private static bool TryValidateEnvironment(string text, out string error)
        {
            error = string.Empty;

            try
            {
                ClaudeCodeCliClient.ParseEnvironment(text);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }


        /// <summary>把配置中的超时限制在下拉框允许的范围内。</summary>
        private decimal ClampTimeout(int timeoutSeconds)
        {
            decimal value = timeoutSeconds > 0 ? timeoutSeconds : AiApiOptions.DefaultTimeoutSeconds;

            if (value < numTimeout.Minimum)
            {
                value = numTimeout.Minimum;
            }

            if (value > numTimeout.Maximum)
            {
                value = numTimeout.Maximum;
            }

            return value;
        }

        /// <summary>选择服务端点预设时自动填写接口地址。</summary>
        private void CboEndpointPreset_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_loading)
            {
                return;
            }

            EndpointPreset preset = cboEndpointPreset.SelectedItem as EndpointPreset;

            if (preset == null || preset.BaseUrl.Length == 0)
            {
                // “自定义”保留当前地址，交给用户手工编辑
                txtBaseUrl.Focus();
                return;
            }

            txtBaseUrl.Text = preset.BaseUrl;
        }

        /// <summary>勾选“显示”时取消密钥掩码。</summary>
        private void ChkShowKey_CheckedChanged(object sender, EventArgs e)
        {
            txtApiKey.UseSystemPasswordChar = !chkShowKey.Checked;
        }

        /// <summary>保存设置并返回 DialogResult.OK（由主界面据此刷新提示）。</summary>
        private void BtnSave_Click(object sender, EventArgs e)
        {
            string error;
            AiSettings settings = BuildSettings(out error);

            if (settings == null)
            {
                ShowValidationError(error);
                return;
            }

            if (!AiSettingsStore.Save(settings, out error))
            {
                lblStatus.ForeColor = Color.FromArgb(198, 40, 40);
                UiLocalization.SetText(lblStatus, "保存失败：" + error);
                ShowMessage("保存设置失败：\n" + error, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>取消（不写入配置文件）。</summary>
        private void BtnCancel_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }

        /// <summary>
        /// 用当前输入测试接口连通性（不写入配置文件），
        /// 便于在保存前确认地址、模型与密钥是否可用。
        /// </summary>
        private async void BtnTest_Click(object sender, EventArgs e)
        {
            if (_busy)
            {
                return;
            }

            string error;
            AiSettings settings = BuildSettings(out error);

            if (settings == null)
            {
                ShowValidationError(error);
                return;
            }

            // 测试连接使用较短超时，避免界面长时间无响应；
            // 本机 Claude Code CLI 首次启动 + 模型响应可能较慢，给到设置里的超时（最多 300 秒）
            int testTimeoutSeconds =
                string.Equals(settings.Provider, AiApiOptions.ProviderClaudeCodeCli, StringComparison.Ordinal)
                    ? Math.Min(settings.TimeoutSeconds, 300)
                    : Math.Min(settings.TimeoutSeconds, TestTimeoutSeconds);

            AiApiOptions options = new AiApiOptions(settings.BaseUrl, settings.ApiKey, settings.Model,
                testTimeoutSeconds, AiApiOptions.SourceUserSettings,
                settings.Provider, settings.ClaudeCliPath, settings.ClaudeModel,
                settings.ClaudeCliEnvironment);

            SetBusy(true, options.IsClaudeCodeCli
                ? "正在调用本机 Claude Code CLI（首次启动可能较慢）..."
                : "正在连接 " + options.Endpoint + " ...");

            try
            {
                ChatCompletionClient client = new ChatCompletionClient(options);

                // 用匿名方法（delegate）代替 Lambda 表达式：Task.Run 需要一个无参委托
                Func<string> testWork = delegate
                {
                    return client.Complete("你是接口连通性测试助手。",
                        "请只回复 OK 两个字符，不要输出任何其它内容。");
                };

                string reply = await Task.Run(testWork);

                // 顺带显示这次测试消耗的 token（接口没返回用量时不显示）
                string usageNote = client.LastUsage == null || !client.LastUsage.IsAvailable
                    ? string.Empty
                    : "（" + client.LastUsage.Describe() + "）";

                SetBusy(false, string.Empty);
                lblStatus.ForeColor = Color.FromArgb(46, 125, 50);
                UiLocalization.SetText(lblStatus, (options.IsClaudeCodeCli
                    ? "调用成功，Claude 返回：" + Shorten(reply, 60)
                    : "连接成功，模型返回：" + Shorten(reply, 60)) + usageNote);
            }
            catch (Exception ex)
            {
                SetBusy(false, string.Empty);
                lblStatus.ForeColor = Color.FromArgb(198, 40, 40);
                UiLocalization.SetText(lblStatus, "连接失败：" + Shorten(ex.Message, 200));
            }
        }

        /// <summary>打开用户配置所在目录，便于手工查看或备份。</summary>
        private void BtnOpenFolder_Click(object sender, EventArgs e)
        {
            try
            {
                Directory.CreateDirectory(AiSettingsStore.SettingsFolderPath);
                Process.Start("explorer.exe", "\"" + AiSettingsStore.SettingsFolderPath + "\"");
            }
            catch (Exception ex)
            {
                ShowMessage("打开配置目录失败：\n" + ex.Message, "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>把界面上的输入整理为设置对象；校验失败时返回 null 并给出原因。</summary>
        private AiSettings BuildSettings(out string error)
        {
            error = string.Empty;

            string provider = SelectedProvider;
            bool cli = string.Equals(provider, AiApiOptions.ProviderClaudeCodeCli, StringComparison.Ordinal);
            string baseUrl = txtBaseUrl.Text.Trim();
            string apiKey = txtApiKey.Text.Trim();
            string model = cboModel.Text.Trim();
            string claudeCliPath = ClaudeCliPathFromUi;
            string claudeModel = txtClaudeModel.Text.Trim();
            string claudeCliEnvironment = txtClaudeEnv.Text.Trim();

            // 本机 Claude Code：不要求接口地址 / 密钥，只校验「路径能不能找到」与「环境变量格式」
            if (cli)
            {
                if (claudeCliPath.Length > 0 && ClaudeCodeCliClient.FindOnPath(claudeCliPath).Length == 0)
                {
                    error = "找不到 claude 程序：" + claudeCliPath +
                        "（请确认路径，或勾选「自动查找」让它按 PATH / 常见安装位置查找）";
                    return null;
                }

                string environmentError;

                if (!TryValidateEnvironment(claudeCliEnvironment, out environmentError))
                {
                    error = environmentError;
                    return null;
                }

                return new AiSettings
                {
                    BaseUrl = baseUrl,
                    ApiKey = apiKey,
                    Model = model,
                    TimeoutSeconds = (int)numTimeout.Value,
                    Provider = AiApiOptions.ProviderClaudeCodeCli,
                    ClaudeCliPath = claudeCliPath,
                    ClaudeModel = claudeModel,
                    ClaudeCliEnvironment = claudeCliEnvironment
                };
            }

            if (baseUrl.Length == 0)
            {
                error = "请填写接口地址（例如 https://api-ap-southeast-1.modelarts-maas.com/v1）。";
                return null;
            }

            Uri uri;

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                error = "接口地址必须是 http:// 或 https:// 开头的完整地址。";
                return null;
            }

            if (apiKey.Length == 0)
            {
                error = "请填写 API Key（或把调用方式切换为「本地 Claude Code CLI」）。";
                return null;
            }

            if (model.Length == 0)
            {
                error = "请选择或填写模型名称。";
                return null;
            }

            return new AiSettings
            {
                BaseUrl = baseUrl,
                ApiKey = apiKey,
                Model = model,
                TimeoutSeconds = (int)numTimeout.Value,
                Provider = AiApiOptions.ProviderCompatibleApi,
                ClaudeCliPath = claudeCliPath,
                ClaudeModel = claudeModel,
                ClaudeCliEnvironment = claudeCliEnvironment
            };
        }

        /// <summary>显示校验提示并把焦点移到需要补填的控件。</summary>
        private void ShowValidationError(string message)
        {
            lblStatus.ForeColor = Color.FromArgb(239, 108, 0);
            UiLocalization.SetText(lblStatus, message);
            ShowMessage(message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            if (message.IndexOf("接口地址", StringComparison.Ordinal) >= 0)
            {
                txtBaseUrl.Focus();
            }
            else if (message.IndexOf("API Key", StringComparison.Ordinal) >= 0)
            {
                txtApiKey.Focus();
            }
            else if (message.IndexOf("模型", StringComparison.Ordinal) >= 0)
            {
                cboModel.Focus();
            }
            else if (message.IndexOf("claude 程序", StringComparison.Ordinal) >= 0)
            {
                if (!chkClaudeAuto.Checked)
                {
                    txtClaudePath.Focus();
                }
            }
            else if (message.IndexOf("环境变量", StringComparison.Ordinal) >= 0)
            {
                txtClaudeEnv.Focus();
            }
        }

        /// <summary>统一弹窗：标题 / 正文都按当前语言翻译（正文里的动态部分由对照表的模板匹配处理）。</summary>
        private DialogResult ShowMessage(string message, string caption,
            MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return MessageBox.Show(this, UiText.Translate(message), UiText.T(caption), buttons, icon);
        }

        /// <summary>测试连接期间锁定输入控件并显示进度文字。</summary>
        private void SetBusy(bool busy, string status)
        {
            _busy = busy;
            UseWaitCursor = busy;

            cboProvider.Enabled = !busy;
            cboEndpointPreset.Enabled = !busy;
            txtBaseUrl.Enabled = !busy;
            cboModel.Enabled = !busy;
            txtApiKey.Enabled = !busy;
            chkShowKey.Enabled = !busy;
            chkClaudeAuto.Enabled = !busy;
            txtClaudePath.Enabled = !busy;
            btnBrowseClaude.Enabled = !busy;
            txtClaudeModel.Enabled = !busy;
            txtClaudeEnv.Enabled = !busy;
            numTimeout.Enabled = !busy;
            btnTest.Enabled = !busy;
            btnSave.Enabled = !busy;

            // 勾着「自动查找」时路径框保持禁用（上面统一按 busy 置位后再纠正一次）
            UpdateClaudePathUi();

            UiLocalization.SetText(btnTest, busy ? "测试中..." : "测试连接");

            if (!string.IsNullOrEmpty(status))
            {
                lblStatus.ForeColor = Color.FromArgb(230, 126, 34);
                UiLocalization.SetText(lblStatus, status);
            }
        }

        /// <summary>测试连接进行中不允许关闭窗口，避免回调访问已释放的控件。</summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy)
            {
                e.Cancel = true;
                lblStatus.ForeColor = Color.FromArgb(230, 126, 34);
                UiLocalization.SetText(lblStatus, "测试连接进行中，请稍候...");
                return;
            }

            base.OnFormClosing(e);
        }

        /// <summary>把多行文本压成单行并截断，用于状态栏显示。</summary>
        private static string Shorten(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "（空）";
            }

            string singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();

            return singleLine.Length <= maxLength
                ? singleLine
                : singleLine.Substring(0, maxLength) + "...";
        }

        /// <summary>调用方式下拉列表项。</summary>
        private sealed class ProviderPreset
        {
            public ProviderPreset(string name, string key)
            {
                Name = name;
                Key = key;
            }

            /// <summary>下拉列表中显示的名称（中文原文，日语查对照表）。</summary>
            public string Name { get; private set; }

            /// <summary>配置里保存的值（api / claude-cli）。</summary>
            public string Key { get; private set; }

            public override string ToString()
            {
                return UiText.T(Name);
            }
        }

        /// <summary>服务端点下拉列表项。</summary>
        private sealed class EndpointPreset
        {
            public EndpointPreset(string name, string baseUrl)
            {
                Name = name;
                BaseUrl = baseUrl;
            }

            /// <summary>下拉列表中显示的名称。</summary>
            public string Name { get; private set; }

            /// <summary>接口地址；为空表示“自定义（手动填写）”。</summary>
            public string BaseUrl { get; private set; }

            public override string ToString()
            {
                // 下拉列表里显示的名称：中文原文照写在 EndpointPresets 里，日语查对照表
                return UiText.T(Name);
            }
        }
    }
}
