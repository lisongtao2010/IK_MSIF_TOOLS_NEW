using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace MSIFAITOOLS.Services
{
    /// <summary>可重试的接口异常（超时、限流、5xx）。</summary>
    internal sealed class AiTransientException : Exception
    {
        public AiTransientException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// AI 调用客户端：按 <see cref="AiApiOptions.Provider"/> 分派到两种后端之一。
    /// <list type="bullet">
    ///   <item><b>云端接口</b>（默认）：OpenAI 兼容的 <c>/chat/completions</c>，只依赖 .NET Framework 自带的
    ///         HttpClient；请求体手工拼装、响应体用 JavaScriptSerializer 解析，避免额外 NuGet 依赖。</item>
    ///   <item><b>本机 Claude Code CLI</b>：把同样的系统 / 用户提示词交给本机的 <c>claude -p</c>
    ///         （见 <see cref="ClaudeCodeCliClient"/>），不需要 API Key，适合本机已登录 Claude Code
    ///         （含 Google Cloud 等第三方认证）的现场。</item>
    /// </list>
    /// 上层调用方（AI 分析 / CMD 分析各步骤）只看到本类的 <see cref="Complete"/>，
    /// 切换调用方式不需要改动它们的代码。
    /// </summary>
    internal sealed class ChatCompletionClient
    {
        /// <summary>进程内共享的 HttpClient（复用连接，避免端口耗尽）。</summary>
        private static readonly HttpClient SharedClient = new HttpClient();

        private const int MaxAttemptCount = 3;

        private readonly AiApiOptions _options;

        /// <summary>累计用量时加锁（AI 调用可能来自不同线程，例如后台分析与【AI 设置】的测试）。</summary>
        private static readonly object UsageLock = new object();

        /// <summary>本次运行（进程内）所有 AI 调用的累计 token 用量。</summary>
        private static readonly AiTokenUsage SessionUsage = new AiTokenUsage();

        /// <summary>
        /// 每次 <see cref="Complete"/> 成功后触发，把本次与累计 token 用量交给上层
        /// （主画面订阅后写进【运行日志】；没有订阅者时不影响调用）。
        /// </summary>
        public static event EventHandler<AiUsageEventArgs> UsageReported;

        /// <summary>本实例最近一次成功调用拿到的 token 用量（接口没返回 usage 时为 null）。</summary>
        public AiTokenUsage LastUsage { get; private set; }

        public ChatCompletionClient(AiApiOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _options = options;

            if (options.IsClaudeCodeCli)
            {
                // 本机 CLI 不需要 API Key；可执行文件路径在第一次调用时解析，
                // 这样「先保存设置、后安装 / 登录 Claude Code」也能正常保存。
                return;
            }

            if (!options.IsConfigured)
            {
                throw new InvalidOperationException(
                    "未配置 AI 接口密钥：请在程序内【AI 设置】中填写 API Key" +
                    "（也可使用 App.config 的 AiApiKey 或环境变量 MSIF_AI_API_KEY；" +
                    "或把调用方式切换为「本地 Claude Code CLI」）。");
            }
        }

        /// <summary>
        /// 发送一次对话补全请求，返回助手回复的正文。
        /// 超时 / 429 / 5xx 会自动重试，其余错误直接抛出。
        /// </summary>
        /// <param name="systemPrompt">系统提示词。</param>
        /// <param name="userPrompt">用户提示词。</param>
        public string Complete(string systemPrompt, string userPrompt)
        {
            if (_options.IsClaudeCodeCli)
            {
                // 本机 Claude Code CLI：一次性把提示词交给 claude -p（详见 ClaudeCodeCliClient）
                AiTokenUsage cliUsage;
                string cliReply = ClaudeCodeCliClient.Complete(_options.ClaudeCliPath, _options.ClaudeModel,
                    _options.ClaudeCliEnvironment, systemPrompt, userPrompt, _options.TimeoutSeconds,
                    out cliUsage);

                ReportUsage(cliUsage);

                return cliReply;
            }

            string requestBody = BuildRequestBody(systemPrompt, userPrompt);
            Exception lastError = null;

            for (int attempt = 1; attempt <= MaxAttemptCount; attempt++)
            {
                try
                {
                    AiTokenUsage usage;
                    string content = SendOnce(requestBody, out usage);

                    // 每次成功调用都把 token 用量报给上层（主画面 →【运行日志】）
                    ReportUsage(usage);

                    return content;
                }
                catch (AiTransientException ex)
                {
                    lastError = ex;

                    if (attempt < MaxAttemptCount)
                    {
                        Thread.Sleep(attempt * 2000);
                    }
                }
            }

            throw new InvalidOperationException(
                "AI 接口调用失败：" + (lastError == null ? "未知原因" : lastError.Message), lastError);
        }

        /// <summary>真正发起 HTTP 请求并解析回复（同时取出 token 用量）。</summary>
        private string SendOnce(string requestBody, out AiTokenUsage usage)
        {
            usage = null;

            byte[] payload = Encoding.UTF8.GetBytes(requestBody);

            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint))
            using (CancellationTokenSource timeoutSource =
                new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds)))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _options.ApiKey);
                request.Content = new ByteArrayContent(payload);
                request.Content.Headers.ContentType =
                    new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

                HttpResponseMessage response = null;
                string responseText;

                try
                {
                    response = SharedClient.SendAsync(request, timeoutSource.Token).GetAwaiter().GetResult();
                    responseText = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException ex)
                {
                    throw new AiTransientException("请求超时（" + _options.TimeoutSeconds + " 秒）", ex);
                }
                catch (HttpRequestException ex)
                {
                    throw new AiTransientException("网络错误：" + ex.Message, ex);
                }

                using (response)
                {
                    int statusCode = (int)response.StatusCode;

                    if (statusCode == 429 || statusCode >= 500)
                    {
                        throw new AiTransientException("接口返回 " + statusCode + "：" + Shorten(responseText), null);
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException("接口返回 " + statusCode + "：" + Shorten(responseText));
                    }
                }

                // 同一个响应里既取正文，也取 token 用量（接口没返回 usage 时 usage = null）
                Dictionary<string, object> root = ParseResponse(responseText);

                usage = ExtractUsage(root);

                return ExtractContent(root, responseText);
            }
        }

        /// <summary>拼装 OpenAI 兼容的请求体。</summary>
        private string BuildRequestBody(string systemPrompt, string userPrompt)
        {
            StringBuilder builder = new StringBuilder();

            builder.Append("{\"model\":\"").Append(EscapeJson(_options.Model)).Append("\",");
            builder.Append("\"temperature\":0,");
            builder.Append("\"stream\":false,");
            builder.Append("\"messages\":[");
            builder.Append("{\"role\":\"system\",\"content\":\"").Append(EscapeJson(systemPrompt)).Append("\"},");
            builder.Append("{\"role\":\"user\",\"content\":\"").Append(EscapeJson(userPrompt)).Append("\"}");
            builder.Append("]}");

            return builder.ToString();
        }

        /// <summary>把响应文本解析成 JSON 对象（失败时抛出带截断原文的异常）。</summary>
        private static Dictionary<string, object> ParseResponse(string responseText)
        {
            object parsed;

            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = int.MaxValue;
                parsed = serializer.DeserializeObject(responseText);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "无法解析 AI 接口返回的 JSON：" + Shorten(responseText), ex);
            }

            Dictionary<string, object> root = parsed as Dictionary<string, object>;

            if (root == null)
            {
                throw new InvalidOperationException("AI 接口返回格式异常：" + Shorten(responseText));
            }

            return root;
        }

        /// <summary>从响应 JSON 中取出 choices[0].message.content。</summary>
        private static string ExtractContent(Dictionary<string, object> root, string responseText)
        {
            object choicesValue;

            if (!root.TryGetValue("choices", out choicesValue))
            {
                throw new InvalidOperationException("AI 接口返回中没有 choices：" + Shorten(responseText));
            }

            object[] choices = choicesValue as object[];

            if (choices == null || choices.Length == 0)
            {
                throw new InvalidOperationException("AI 接口返回的 choices 为空：" + Shorten(responseText));
            }

            Dictionary<string, object> firstChoice = choices[0] as Dictionary<string, object>;
            object messageValue;
            Dictionary<string, object> message;

            if (firstChoice == null ||
                !firstChoice.TryGetValue("message", out messageValue) ||
                (message = messageValue as Dictionary<string, object>) == null)
            {
                throw new InvalidOperationException("AI 接口返回中没有 message：" + Shorten(responseText));
            }

            object contentValue;

            if (!message.TryGetValue("content", out contentValue) || contentValue == null)
            {
                return string.Empty;
            }

            return Convert.ToString(contentValue, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 从响应 JSON 的 <c>usage</c> 里取 token 用量；没有 usage 或全为 0 时返回 null。
        /// <para>
        /// 兼容 OpenAI 兼容接口的常见写法：<c>prompt_tokens</c> / <c>completion_tokens</c> /
        /// <c>total_tokens</c>，明细 <c>prompt_tokens_details.cached_tokens</c>、
        /// <c>completion_tokens_details.reasoning_tokens</c>（缺了就按 0 处理）。
        /// </para>
        /// </summary>
        private static AiTokenUsage ExtractUsage(Dictionary<string, object> root)
        {
            Dictionary<string, object> usageNode = ReadDictionary(root, "usage");

            if (usageNode == null)
            {
                return null;
            }

            AiTokenUsage usage = new AiTokenUsage();
            usage.PromptTokens = ReadInt(usageNode, "prompt_tokens");
            usage.CompletionTokens = ReadInt(usageNode, "completion_tokens");
            usage.TotalTokens = ReadInt(usageNode, "total_tokens");

            if (usage.TotalTokens <= 0)
            {
                usage.TotalTokens = usage.PromptTokens + usage.CompletionTokens;
            }

            Dictionary<string, object> promptDetails = ReadDictionary(usageNode, "prompt_tokens_details");

            if (promptDetails != null)
            {
                usage.CachedTokens = ReadInt(promptDetails, "cached_tokens");
            }

            Dictionary<string, object> completionDetails = ReadDictionary(usageNode, "completion_tokens_details");

            if (completionDetails != null)
            {
                usage.ReasoningTokens = ReadInt(completionDetails, "reasoning_tokens");
            }

            return usage.IsAvailable ? usage : null;
        }

        /// <summary>取 JSON 对象里的子对象（不存在 / 类型不对时返回 null）。</summary>
        private static Dictionary<string, object> ReadDictionary(Dictionary<string, object> node, string key)
        {
            object value;

            if (node == null || string.IsNullOrEmpty(key) || !node.TryGetValue(key, out value))
            {
                return null;
            }

            return value as Dictionary<string, object>;
        }

        /// <summary>取 JSON 对象里的整数值（缺失 / 非数字时返回 0）。</summary>
        private static int ReadInt(Dictionary<string, object> node, string key)
        {
            object value;

            if (node == null || string.IsNullOrEmpty(key) || !node.TryGetValue(key, out value) || value == null)
            {
                return 0;
            }

            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                // 个别接口会把数字写成字符串 / 小数，取不到就算 0，不影响正文
                return 0;
            }
        }

        /// <summary>
        /// 记录本次用量（实例 <see cref="LastUsage"/> + 进程内累计），并通知
        /// <see cref="UsageReported"/> 的订阅者。订阅者（日志）出错不影响 AI 结果。
        /// </summary>
        private void ReportUsage(AiTokenUsage usage)
        {
            LastUsage = usage;

            AiTokenUsage sessionCopy;

            lock (UsageLock)
            {
                if (usage != null)
                {
                    SessionUsage.Add(usage);
                }

                sessionCopy = SessionUsage.Copy();
            }

            EventHandler<AiUsageEventArgs> handler = UsageReported;

            if (handler == null)
            {
                return;
            }

            string model = _options.IsClaudeCodeCli
                ? (string.IsNullOrEmpty(_options.ClaudeModel) ? "本机默认模型" : _options.ClaudeModel)
                : _options.Model;

            try
            {
                handler(this, new AiUsageEventArgs(_options.ProviderDisplayName, model, usage, sessionCopy));
            }
            catch (Exception)
            {
                // 日志 / 界面回调异常不能把正常的 AI 结果变成失败
            }
        }

        /// <summary>把文本转成 JSON 字符串字面量内容（不含首尾引号，含内部的转义）。</summary>
        public static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(value.Length + 16);

            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;

                    case '\\':
                        builder.Append("\\\\");
                        break;

                    case '\r':
                        builder.Append("\\r");
                        break;

                    case '\n':
                        builder.Append("\\n");
                        break;

                    case '\t':
                        builder.Append("\\t");
                        break;

                    case '\b':
                        builder.Append("\\b");
                        break;

                    case '\f':
                        builder.Append("\\f");
                        break;

                    default:
                        if (c < ' ')
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            return builder.ToString();
        }

        /// <summary>截断过长的响应文本，便于放进日志与异常信息。</summary>
        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "（空）";
            }

            string singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();

            return singleLine.Length <= 300 ? singleLine : singleLine.Substring(0, 300) + "...";
        }
    }
}
