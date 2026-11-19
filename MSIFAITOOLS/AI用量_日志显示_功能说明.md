# AI token 用量显示到运行日志 · 功能说明

> 更新日期：2026-11-18
> 相关文件：`Services/AiTokenUsage.cs`（新增）、`Services/ChatCompletionClient.cs`、
> `Services/ClaudeCodeCliClient.cs`、`Form1.cs`、`AiSettingsForm.cs`

---

## 1. 一句话概要

每次 AI 调用成功后，在【运行日志】追加一行用量：

```text
10:25:41  AI 用量：输入 17,958 / 输出 3,357 / 合计 21,315 token（思考 2,643）｜云端接口（OpenAI 兼容） deepseek-v4.1-flash-1017｜本次运行累计 输入 17,958 / 输出 3,357 / 合计 21,315 token
```

- 一行 = 一次调用（一次 `ChatCompletionClient.Complete`）；批量分析（特注品数量判定、第 0 步 AI 判定等）会按调用次数逐行输出。
- 每行都带「本次运行累计」，方便估算一轮【一括执行】的总消耗（累计从程序启动开始算，关闭程序清零）。
- 接口没返回 `usage` 时显示「接口没有返回 token 数量」，不影响分析结果。

---

## 2. 数据从哪来

| 调用方式 | 取用量的位置 | 说明 |
|----------|--------------|------|
| 云端接口（OpenAI 兼容 `/chat/completions`） | 响应 JSON 的 `usage` | `prompt_tokens` / `completion_tokens` / `total_tokens`；明细 `prompt_tokens_details.cached_tokens`、`completion_tokens_details.reasoning_tokens`（有就显示「缓存 / 思考」） |
| 本机 Claude Code CLI | `claude -p --output-format json` 的 `usage` / `total_cost_usd` | 输入 = `input_tokens + cache_creation_input_tokens + cache_read_input_tokens`，输出 = `output_tokens`；有费用时附带「费用 $0.xxxx」 |

> Claude CLI 兼容性：若本机 CLI 不认识 `--output-format json`（旧版本），
> `ClaudeCodeCliClient` 会自动退回 `--output-format text` 再跑一次（正文照常返回，日志显示「接口没有返回 token 数量」）。
> 若同时不认识 `--system-prompt-file`，仍按原来的方式把系统提示词拼到标准输入开头。

---

## 3. 代码结构

| 位置 | 作用 |
|------|------|
| `Services/AiTokenUsage.cs` | 用量数据（输入 / 输出 / 合计 / 缓存 / 思考 / 费用）+ 日志文字 `Describe()`；`AiUsageEventArgs` 组装整行日志 `ToLogText()` |
| `ChatCompletionClient.Complete` | 调用成功后 `ReportUsage(usage)`：写实例 `LastUsage`、累加到进程级 `SessionUsage`、触发静态事件 `UsageReported` |
| `ChatCompletionClient.ExtractUsage` | 从响应 JSON 的 `usage` 取数（缺失 → `null`） |
| `ClaudeCodeCliClient.Complete(... out AiTokenUsage usage)` | CLI 分支返回正文的同时取回用量 |
| `Form1.ChatCompletionClient_UsageReported` | 订阅事件 → `AppendLog(...)`（自动切到 UI 线程） |
| `AiSettingsForm.BtnTest_Click` | 「测试连接」成功时顺带显示本次测试的用量 |

订阅在 `Form1` 构造时建立、`OnFormClosed` 时取消；事件处理异常被 `ReportUsage` 吞掉，不会把正常的 AI 结果变成失败。

---

## 4. 确认方法

1. 打开【AI 设置】→【测试连接】：状态行显示「连接成功，模型返回：OK（输入 … / 输出 … / 合计 … token）」。
2. 关掉设置画面看主画面【运行日志】：出现对应的一行「AI 用量：…」。
3. 跑【AI 分析】（特注品数量设定）：每个送 AI 的 Excel 一行，行号的「本次运行累计」逐次增加。
4. 本机 Claude Code CLI 同上；旧版 CLI 只是少了 token 数字，正文与分析结果不受影响。

---

## 5. 注意

- 云端的 `usage` 由服务商返回，个别兼容实现不返回（或只返回部分字段）→ 只显示能取到的部分。
- 累计是「进程内」的：重新启动程序后从 0 开始，不做持久化。
- 日志正文经过 `UiText.Translate`：中日对照表里没有这一条时保持中文。
