using System;
using System.Globalization;
using System.Text;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// 一次（或累计）AI 调用的 token 用量。
    /// <para>
    /// 云端接口：取响应 JSON 的 <c>usage</c>（<c>prompt_tokens</c> / <c>completion_tokens</c> /
    /// <c>total_tokens</c>，以及可选的 <c>cached_tokens</c> / <c>reasoning_tokens</c>）；
    /// 本机 Claude Code CLI：取 <c>--output-format json</c> 输出里的 <c>usage</c>
    /// （<c>input_tokens</c> + <c>cache_creation_input_tokens</c> + <c>cache_read_input_tokens</c> /
    /// <c>output_tokens</c>）与 <c>total_cost_usd</c>。
    /// </para>
    /// </summary>
    internal sealed class AiTokenUsage
    {
        /// <summary>输入（提示词）token 数。</summary>
        public int PromptTokens { get; set; }

        /// <summary>输出（回复）token 数。</summary>
        public int CompletionTokens { get; set; }

        /// <summary>合计 token 数（接口没给时按输入 + 输出计算）。</summary>
        public int TotalTokens { get; set; }

        /// <summary>其中命中 / 写入缓存的 token 数（接口没给时为 0）。</summary>
        public int CachedTokens { get; set; }

        /// <summary>其中「思考」token 数（接口没给时为 0）。</summary>
        public int ReasoningTokens { get; set; }

        /// <summary>本机 Claude Code CLI 报告的本次费用（USD）；云端接口没有这个值时为 null。</summary>
        public decimal? CostUsd { get; set; }

        /// <summary>是否真的拿到了用量（接口没返回 <c>usage</c> 时为 false）。</summary>
        public bool IsAvailable
        {
            get { return PromptTokens > 0 || CompletionTokens > 0 || TotalTokens > 0; }
        }

        /// <summary>把另一个用量累加到本对象（「本次运行累计」用）。</summary>
        public void Add(AiTokenUsage other)
        {
            if (other == null)
            {
                return;
            }

            PromptTokens += other.PromptTokens;
            CompletionTokens += other.CompletionTokens;
            TotalTokens += other.TotalTokens;
            CachedTokens += other.CachedTokens;
            ReasoningTokens += other.ReasoningTokens;

            if (other.CostUsd.HasValue)
            {
                CostUsd = (CostUsd ?? 0m) + other.CostUsd.Value;
            }
        }

        /// <summary>复制一份（累计对象要按值传给日志，避免之后被继续累加）。</summary>
        public AiTokenUsage Copy()
        {
            AiTokenUsage copy = new AiTokenUsage();
            copy.Add(this);

            return copy;
        }

        /// <summary>
        /// 短描述（不含「AI 用量：」前缀），例：<c>输入 17,958 / 输出 3,357 / 合计 21,315 token（思考 2,643）</c>。
        /// </summary>
        public string Describe()
        {
            if (!IsAvailable)
            {
                return "接口没有返回 token 数量";
            }

            StringBuilder builder = new StringBuilder();

            builder.AppendFormat(CultureInfo.InvariantCulture,
                "输入 {0:N0} / 输出 {1:N0} / 合计 {2:N0} token",
                PromptTokens, CompletionTokens, TotalTokens);

            StringBuilder detail = new StringBuilder();

            if (ReasoningTokens > 0)
            {
                detail.AppendFormat(CultureInfo.InvariantCulture, "思考 {0:N0}", ReasoningTokens);
            }

            if (CachedTokens > 0)
            {
                if (detail.Length > 0)
                {
                    detail.Append("，");
                }

                detail.AppendFormat(CultureInfo.InvariantCulture, "缓存 {0:N0}", CachedTokens);
            }

            if (detail.Length > 0)
            {
                builder.Append("（").Append(detail).Append("）");
            }

            if (CostUsd.HasValue && CostUsd.Value > 0m)
            {
                builder.AppendFormat(CultureInfo.InvariantCulture, "，费用 ${0:0.0000}", CostUsd.Value);
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// <see cref="ChatCompletionClient.UsageReported"/> 的事件参数：
    /// 本次调用的用量 + 本次运行（进程启动以来）的累计用量，供主画面写【运行日志】。
    /// </summary>
    internal sealed class AiUsageEventArgs : EventArgs
    {
        public AiUsageEventArgs(string provider, string model, AiTokenUsage usage, AiTokenUsage sessionUsage)
        {
            Provider = provider ?? string.Empty;
            Model = model ?? string.Empty;
            Usage = usage;
            SessionUsage = sessionUsage;
        }

        /// <summary>调用方式（「云端接口（OpenAI 兼容）」/「本地 Claude Code CLI」）。</summary>
        public string Provider { get; private set; }

        /// <summary>模型名（Claude CLI 没指定模型时是「本机默认模型」）。</summary>
        public string Model { get; private set; }

        /// <summary>本次调用的用量（接口没返回 usage 时为 null）。</summary>
        public AiTokenUsage Usage { get; private set; }

        /// <summary>本次运行（进程内）所有 AI 调用的累计用量。</summary>
        public AiTokenUsage SessionUsage { get; private set; }

        /// <summary>写进运行日志的一整行文字。</summary>
        public string ToLogText()
        {
            StringBuilder builder = new StringBuilder("AI 用量：");

            builder.Append(Usage == null ? "接口没有返回 token 数量" : Usage.Describe());

            if (Provider.Length > 0)
            {
                builder.Append("｜").Append(Provider);
            }

            if (Model.Length > 0)
            {
                builder.Append(" ").Append(Model);
            }

            if (SessionUsage != null && SessionUsage.IsAvailable)
            {
                builder.Append("｜本次运行累计 ");
                builder.AppendFormat(CultureInfo.InvariantCulture,
                    "输入 {0:N0} / 输出 {1:N0} / 合计 {2:N0} token",
                    SessionUsage.PromptTokens, SessionUsage.CompletionTokens, SessionUsage.TotalTokens);
            }

            return builder.ToString();
        }
    }
}
