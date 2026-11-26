using System;
using System.Configuration;
using System.Globalization;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// AI 的连接配置：<b>调用方式</b>（云端 OpenAI 兼容接口 / 本机 Claude Code CLI）+ 各自的参数。
    /// 取值优先级：环境变量 → AI 设置画面保存的用户配置（%APPDATA%\MSIFAITOOLS\ai-settings.json）
    /// → App.config 的 appSettings → 内置默认值；
    /// 密钥为空且接口地址正是 <see cref="DefaultBaseUrl"/> 时，再兜底用 <see cref="DefaultApiKey"/>。
    /// </summary>
    internal sealed class AiApiOptions
    {
        /// <summary>默认接口地址（OpenAI Compatible）。</summary>
        public const string DefaultBaseUrl = "https://api-ap-southeast-1.modelarts-maas.com/v2";

        /// <summary>
        /// 内置测试密钥（ModelArts 试用期的测试 Key，不是个人密钥）。
        /// <para>
        /// 发布包默认会把 exe.config 的 AiApiKey 清空（Publish-Signed.ps1 的 -SecretMode BlankAiKey），
        /// 清空之后【AI 设置】画面是空的、【AI 分析】直接报「未配置 AI 密钥」。
        /// 所以这里做一层兜底：<b>只有「密钥为空」且「接口地址 = <see cref="DefaultBaseUrl"/>」时才用这个 Key</b>，
        /// 把接口地址改成任何别的值（OpenAI 官方 / DeepSeek / 本地 Ollama …）就不会带上它。
        /// </para>
        /// </summary>
        public const string DefaultApiKey =
            "RG1wlfD1ixUC0DOfX4HJw7KHc_orZYD7_KJw2gxoVjBtAVWCGSKN1JwBcKHYsf-BR2FzDtlMgTiyOVFA4rPCKQ";

        /// <summary>默认模型。</summary>
        public const string DefaultModel = "deepseek-v4.1-flash-1017";

        /// <summary>默认请求超时（秒）。</summary>
        public const int DefaultTimeoutSeconds = 180;

        /// <summary>App.config 中的接口地址键名。</summary>
        public const string BaseUrlSettingKey = "AiBaseUrl";

        /// <summary>App.config 中的密钥键名。</summary>
        public const string ApiKeySettingKey = "AiApiKey";

        /// <summary>App.config 中的模型键名。</summary>
        public const string ModelSettingKey = "AiModel";

        /// <summary>App.config 中的超时键名。</summary>
        public const string TimeoutSettingKey = "AiTimeoutSeconds";

        /// <summary>调用方式：云端 OpenAI 兼容接口（默认）。</summary>
        public const string ProviderCompatibleApi = "api";

        /// <summary>调用方式：本机 Claude Code CLI（claude -p）。</summary>
        public const string ProviderClaudeCodeCli = "claude-cli";

        /// <summary>App.config 中的「调用方式」键名（api / claude-cli）。</summary>
        public const string ProviderSettingKey = "AiProvider";

        /// <summary>App.config 中的「本机 Claude Code 路径」键名。</summary>
        public const string ClaudeCliPathSettingKey = "ClaudeCliPath";

        /// <summary>App.config 中的「本机 Claude Code 模型」键名（留空 = 用本机默认模型）。</summary>
        public const string ClaudeModelSettingKey = "ClaudeModel";

        /// <summary>App.config 中的「本机 Claude Code 追加环境变量」键名（KEY=VALUE;…）。</summary>
        public const string ClaudeCliEnvironmentSettingKey = "ClaudeCliEnvironment";

        /// <summary>覆盖调用方式的环境变量。</summary>
        public const string ProviderEnvironmentKey = "MSIF_AI_PROVIDER";

        /// <summary>覆盖本机 Claude Code 路径的环境变量。</summary>
        public const string ClaudeCliPathEnvironmentKey = "MSIF_CLAUDE_CLI_PATH";

        /// <summary>覆盖本机 Claude Code 模型的环境变量。</summary>
        public const string ClaudeModelEnvironmentKey = "MSIF_CLAUDE_MODEL";

        /// <summary>覆盖本机 Claude Code 追加环境变量的环境变量。</summary>
        public const string ClaudeCliEnvironmentEnvironmentKey = "MSIF_CLAUDE_CLI_ENV";


        /// <summary>覆盖接口地址的环境变量。</summary>
        public const string BaseUrlEnvironmentKey = "MSIF_AI_BASE_URL";

        /// <summary>覆盖密钥的环境变量（优先级最高，便于临时替换密钥）。</summary>
        public const string ApiKeyEnvironmentKey = "MSIF_AI_API_KEY";

        /// <summary>覆盖模型的环境变量。</summary>
        public const string ModelEnvironmentKey = "MSIF_AI_MODEL";

        /// <summary>配置来源：环境变量。</summary>
        public const string SourceEnvironment = "环境变量";

        /// <summary>配置来源：AI 设置画面保存的用户配置。</summary>
        public const string SourceUserSettings = "AI 设置画面";

        /// <summary>配置来源：App.config。</summary>
        public const string SourceAppConfig = "App.config";

        /// <summary>配置来源：内置默认值。</summary>
        public const string SourceDefault = "内置默认值";

        public AiApiOptions(string baseUrl, string apiKey, string model, int timeoutSeconds,
            string source = SourceDefault)
            : this(baseUrl, apiKey, model, timeoutSeconds, source, null, null, null, null)
        {
        }

        public AiApiOptions(string baseUrl, string apiKey, string model, int timeoutSeconds,
            string source, string provider, string claudeCliPath, string claudeModel,
            string claudeCliEnvironment)
        {
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim();
            ApiKey = string.IsNullOrWhiteSpace(apiKey) ? string.Empty : apiKey.Trim();
            Model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
            TimeoutSeconds = timeoutSeconds <= 0 ? DefaultTimeoutSeconds : timeoutSeconds;
            ConfigurationSource = string.IsNullOrWhiteSpace(source) ? SourceDefault : source;
            Provider = NormalizeProvider(provider);
            ClaudeCliPath = string.IsNullOrWhiteSpace(claudeCliPath) ? string.Empty : claudeCliPath.Trim();
            ClaudeModel = string.IsNullOrWhiteSpace(claudeModel) ? string.Empty : claudeModel.Trim();
            ClaudeCliEnvironment = string.IsNullOrWhiteSpace(claudeCliEnvironment)
                ? string.Empty
                : claudeCliEnvironment.Trim();
        }

        /// <summary>接口基础地址（不含 /chat/completions）。</summary>
        public string BaseUrl { get; private set; }

        /// <summary>API 密钥。</summary>
        public string ApiKey { get; private set; }

        /// <summary>模型名称。</summary>
        public string Model { get; private set; }

        /// <summary>单次请求超时（秒）。</summary>
        public int TimeoutSeconds { get; private set; }

        /// <summary>本次配置的实际来源（环境变量 / AI 设置画面 / App.config / 内置默认值）。</summary>
        public string ConfigurationSource { get; private set; }

        /// <summary>
        /// 调用方式：<see cref="ProviderCompatibleApi"/>（云端 OpenAI 兼容接口）
        /// 或 <see cref="ProviderClaudeCodeCli"/>（本机 Claude Code CLI）。
        /// </summary>
        public string Provider { get; private set; }

        /// <summary>
        /// 本机 Claude Code 的可执行文件路径（claude.exe / claude.cmd）。
        /// 留空 = 按常见安装位置与 PATH 自动查找（见 <see cref="ClaudeCodeCliClient.ResolveExecutable"/>）。
        /// </summary>
        public string ClaudeCliPath { get; private set; }

        /// <summary>本机 Claude Code 的模型名（留空 = 用本机默认模型）。</summary>
        public string ClaudeModel { get; private set; }

        /// <summary>本机 Claude Code 的追加环境变量（KEY=VALUE，用 ; 分隔；可空）。</summary>
        public string ClaudeCliEnvironment { get; private set; }

        /// <summary>当前是否使用本机 Claude Code CLI。</summary>
        public bool IsClaudeCodeCli
        {
            get { return string.Equals(Provider, ProviderClaudeCodeCli, StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>日志 / 界面显示用的调用方式名称。</summary>
        public string ProviderDisplayName
        {
            get { return IsClaudeCodeCli ? "本地 Claude Code CLI" : "云端接口（OpenAI 兼容）"; }
        }

        /// <summary>聊天补全接口的完整地址。</summary>
        public string Endpoint
        {
            get { return BaseUrl.TrimEnd('/') + "/chat/completions"; }
        }

        /// <summary>是否已配置（CLI 模式不要求 API Key；接口模式必须有密钥）。</summary>
        public bool IsConfigured
        {
            get { return IsClaudeCodeCli || ApiKey.Length > 0; }
        }

        /// <summary>
        /// 本次配置的密钥是否来自内置测试密钥（<see cref="DefaultApiKey"/>）：
        /// 即配置里没有密钥、接口地址又是 <see cref="DefaultBaseUrl"/> 的情况。
        /// </summary>
        public bool IsBuiltInApiKey { get; private set; }

        /// <summary>
        /// 规范化调用方式：只认 <c>claude-cli</c>（含 claude / claude-code / cli 等写法），
        /// 其它值一律按云端接口处理。
        /// </summary>
        public static string NormalizeProvider(string provider)
        {
            if (string.IsNullOrWhiteSpace(provider))
            {
                return ProviderCompatibleApi;
            }

            string text = provider.Trim();

            if (string.Equals(text, ProviderClaudeCodeCli, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "claude", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "claude-code", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "cli", StringComparison.OrdinalIgnoreCase))
            {
                return ProviderClaudeCodeCli;
            }

            return ProviderCompatibleApi;
        }

        /// <summary>
        /// 最后一次读取 App.config 失败的原因（为空表示读取正常）。
        /// 配置系统初始化失败（例如自定义节声明冲突）时 ConfigurationManager 会抛异常，
        /// 若不把原因暴露出来，界面只能报“未配置密钥”，无法定位问题。
        /// </summary>
        public static string LastConfigurationError { get; private set; }

        /// <summary>
        /// 从环境变量、AI 设置画面保存的用户配置与 App.config 读取配置。
        /// </summary>
        public static AiApiOptions FromConfiguration()
        {
            LastConfigurationError = string.Empty;
            LastSettingsError = string.Empty;

            string settingsError;
            AiSettings userSettings = AiSettingsStore.Load(out settingsError);
            LastSettingsError = settingsError;
            string source;

            string baseUrl = ReadSetting(BaseUrlEnvironmentKey, BaseUrlSettingKey, DefaultBaseUrl,
                userSettings == null ? null : userSettings.BaseUrl, out source);
            string apiKey = ReadSetting(ApiKeyEnvironmentKey, ApiKeySettingKey, string.Empty,
                userSettings == null ? null : userSettings.ApiKey, out source);
            string model = ReadSetting(ModelEnvironmentKey, ModelSettingKey, DefaultModel,
                userSettings == null ? null : userSettings.Model, out source);

            // 调用方式：api（云端接口） / claude-cli（本机 Claude Code）
            string provider = ReadSetting(ProviderEnvironmentKey, ProviderSettingKey,
                ProviderCompatibleApi,
                userSettings == null ? null : userSettings.Provider, out source);
            string providerSource = source;

            // 内置测试密钥兜底：发布包会把 exe.config 的 AiApiKey 清空（-SecretMode BlankAiKey），
            // 只要接口地址仍是 ModelArts v2，就自动用内置测试密钥 —— 否则【AI 设置】是空的、
            // 【AI 分析】一上来就报「未配置 AI 密钥」。换了接口地址 / 用本机 CLI 时不受影响。
            bool usesBuiltInApiKey = TryApplyBuiltInApiKey(ref apiKey, baseUrl, provider);

            string claudeCliPath = ReadSetting(ClaudeCliPathEnvironmentKey, ClaudeCliPathSettingKey,
                string.Empty,
                userSettings == null ? null : userSettings.ClaudeCliPath, out source);
            string claudeModel = ReadSetting(ClaudeModelEnvironmentKey, ClaudeModelSettingKey,
                string.Empty,
                userSettings == null ? null : userSettings.ClaudeModel, out source);
            string claudeCliEnvironment = ReadSetting(ClaudeCliEnvironmentEnvironmentKey,
                ClaudeCliEnvironmentSettingKey, string.Empty,
                userSettings == null ? null : userSettings.ClaudeCliEnvironment, out source);

            int timeoutSeconds = DefaultTimeoutSeconds;
            string timeoutText = ReadSetting("MSIF_AI_TIMEOUT_SECONDS", TimeoutSettingKey,
                DefaultTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                userSettings == null || userSettings.TimeoutSeconds <= 0
                    ? null
                    : userSettings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                out source);

            int parsedTimeout;

            if (!string.IsNullOrWhiteSpace(timeoutText) &&
                int.TryParse(timeoutText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out parsedTimeout) && parsedTimeout > 0)
            {
                timeoutSeconds = parsedTimeout;
            }

            AiApiOptions options = new AiApiOptions(baseUrl, apiKey, model, timeoutSeconds, providerSource,
                provider, claudeCliPath, claudeModel, claudeCliEnvironment);
            options.IsBuiltInApiKey = usesBuiltInApiKey;

            return options;
        }

        /// <summary>
        /// 密钥为空、且接口地址正是 <see cref="DefaultBaseUrl"/> 时，用 <see cref="DefaultApiKey"/> 兜底。
        /// 用本机 Claude Code CLI 时不补（那本来就不需要 Key）。
        /// </summary>
        /// <returns>是否使用了内置测试密钥。</returns>
        private static bool TryApplyBuiltInApiKey(ref string apiKey, string baseUrl, string provider)
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                return false;
            }

            if (!string.Equals(NormalizeProvider(provider), ProviderCompatibleApi, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(NormalizeBaseUrl(baseUrl), NormalizeBaseUrl(DefaultBaseUrl),
                StringComparison.Ordinal))
            {
                return false;
            }

            apiKey = DefaultApiKey;
            return true;
        }

        /// <summary>接口地址比对用：去首尾空白、去末尾的 /、忽略大小写。</summary>
        private static string NormalizeBaseUrl(string baseUrl)
        {
            return (baseUrl ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();
        }

        /// <summary>用户配置文件读取失败的原因（为空表示正常或文件不存在）。</summary>
        public static string LastSettingsError { get; private set; }

        /// <summary>日志用的配置摘要（不输出完整密钥）。</summary>
        public string Describe()
        {
            if (IsClaudeCodeCli)
            {
                return string.Format("调用方式 {0}，路径 {1}，模型 {2}（来源：{3}）",
                    ProviderDisplayName,
                    ClaudeCliPath.Length == 0 ? "自动查找" : ClaudeCliPath,
                    ClaudeModel.Length == 0 ? "本机默认" : ClaudeModel,
                    ConfigurationSource);
            }

            string keyNote = IsBuiltInApiKey ? "，内置测试密钥" : string.Empty;

            return string.Format("接口 {0}，模型 {1}，密钥 {2}（来源：{3}{4}）", BaseUrl, Model,
                MaskApiKey(), ConfigurationSource, keyNote);
        }

        private string MaskApiKey()
        {
            if (ApiKey.Length == 0)
            {
                return "未配置";
            }

            if (ApiKey.Length <= 8)
            {
                return "********";
            }

            return ApiKey.Substring(0, 4) + "****" + ApiKey.Substring(ApiKey.Length - 4);
        }

        /// <summary>
        /// 按“环境变量 → 用户设置文件 → App.config → 默认值”的顺序取值，并记录实际来源。
        /// </summary>
        private static string ReadSetting(string environmentKey, string settingKey, string defaultValue,
            string userSettingValue, out string source)
        {
            string value = Environment.GetEnvironmentVariable(environmentKey);

            if (!string.IsNullOrWhiteSpace(value))
            {
                source = SourceEnvironment;
                return value.Trim();
            }

            if (!string.IsNullOrWhiteSpace(userSettingValue))
            {
                source = SourceUserSettings;
                return userSettingValue.Trim();
            }

            value = ReadAppSetting(settingKey);

            if (!string.IsNullOrWhiteSpace(value))
            {
                source = SourceAppConfig;
                return value.Trim();
            }

            source = SourceDefault;
            return defaultValue;
        }

        private static string ReadAppSetting(string key)
        {
            try
            {
                return ConfigurationManager.AppSettings[key];
            }
            catch (Exception ex)
            {
                // 配置文件损坏 / 配置节声明冲突时不影响程序启动，退化为内置默认值，
                // 但记录原因供界面提示（否则会表现为“密钥已填写却提示未配置”）。
                LastConfigurationError = ex.Message;
                return null;
            }
        }
    }
}
