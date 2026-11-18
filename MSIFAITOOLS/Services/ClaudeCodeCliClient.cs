using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// 本机 <b>Claude Code CLI</b>（<c>claude -p</c> 非交互模式）的定位与调用。
    ///
    /// <para>
    /// 用途：【AI 设置】除了「云端 OpenAI 兼容接口」以外，还可以选择<b>本机已安装并登录的 Claude Code</b>
    /// 来跑同样的分析（<see cref="ChatCompletionClient"/> 按 <see cref="AiApiOptions.Provider"/> 分派到这里）。
    /// 这样现场没有外网 / 没有 API Key、但本机装好了 Claude Code（含通过 Google Cloud 等第三方认证登录的情况）时，
    /// 也能使用 AI 功能。
    /// </para>
    ///
    /// <para>
    /// <b>怎么把提示词交给 claude</b>：
    /// <list type="bullet">
    ///   <item>用户提示词（可能几万字）走<b>标准输入</b>（UTF-8 原始字节，避免 .NET Framework 的
    ///         重定向编码按系统代码页转换而出现乱码）；</item>
    ///   <item>系统提示词写进临时文件，用 <c>--system-prompt-file</c> 传入，避免命令行长度限制；</item>
    ///   <item>若本机 CLI 版本不认识 <c>--system-prompt-file</c>，自动退回「把系统提示词拼在标准输入开头」再跑一次；</item>
    ///   <item>工作目录固定为 <c>%TEMP%\MSIFAITOOLS\claude-cli</c>，避免把项目的 CLAUDE.md / MCP 配置卷进来。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>环境变量</b>：默认继承本进程的环境变量；【AI 设置】里填的「追加环境变量」
    /// （形如 <c>CLAUDE_CODE_USE_VERTEX=1;CLOUD_ML_REGION=asia-northeast1</c>）会覆盖同名变量，
    /// 便于通过 Google Cloud（Vertex）等方式认证的 Claude Code 在 GUI 启动时也能拿到必要变量。
    /// </para>
    /// </summary>
    internal static class ClaudeCodeCliClient
    {
        /// <summary>可执行文件名（不含扩展名）。</summary>
        public const string ExecutableBaseName = "claude";

        /// <summary>工作目录（%TEMP%\MSIFAITOOLS\claude-cli）。</summary>
        public static string WorkingDirectory
        {
            get { return Path.Combine(Path.GetTempPath(), "MSIFAITOOLS", "claude-cli"); }
        }

        /// <summary>
        /// 传给 <c>-p</c> 的固定短指示（纯 ASCII：避免命令行代码页把日文 / 中文参数弄成乱码）。
        /// 真正的内容（系统提示词 + 用户提示词）走标准输入。
        /// </summary>
        private const string PrintQuery =
            "Read the instructions given in the input below and follow them exactly. " +
            "Do not use any tools. Output only the requested result, with no preamble and no explanation.";

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>找不到时的统一错误信息（【AI 设置】保存 / 测试连接都会显示它）。</summary>
        private const string NotFoundMessage =
            "找不到本机 Claude Code CLI（claude）。请先安装 Claude Code 并完成登录" +
            "（终端里执行 claude 能正常对话即可），一般不需要指定路径；" +
            "只有装在非常规位置时，才需要在【AI 设置】里指定 claude 路径。";

        /// <summary>
        /// 解析要执行的 <c>claude</c> 程序路径（不需要用户填写，通常自动就能找到）：
        /// ① 设置里填了路径 → 用它（可只填文件名，按 PATH 查找）；
        /// ② 没填 → <b>先查 PATH</b>（能直接在 cmd 里敲 <c>claude</c> 就一定命中）
        ///          → 再查常见安装位置（<c>%USERPROFILE%\.local\bin</c>、<c>%APPDATA%\npm</c> 等）。
        /// 找不到时抛出带操作提示的异常。
        /// </summary>
        public static string ResolveExecutable(string configuredPath)
        {
            string configured = NormalizePath(configuredPath);

            if (configured.Length > 0)
            {
                if (File.Exists(configured))
                {
                    return Path.GetFullPath(configured);
                }

                string found = FindOnPath(configured);

                if (!string.IsNullOrEmpty(found))
                {
                    return found;
                }

                throw new InvalidOperationException(
                    "找不到 Claude Code CLI：" + configured +
                    "（请在【AI 设置】里确认「claude 路径」，或改为自动查找）");
            }

            // ① 先按 PATH 找：只要在本机任意 cmd 窗口里敲 claude 能跑，这里就会命中
            string fromPath = FindExecutableOnPath();

            if (!string.IsNullOrEmpty(fromPath))
            {
                return fromPath;
            }

            // ② PATH 里没有 → 再看常见安装位置
            List<string> candidates = new List<string>();
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            if (!string.IsNullOrEmpty(userProfile))
            {
                // 官方 native 安装 / 旧版 npm 全局安装
                candidates.Add(Path.Combine(userProfile, ".local", "bin", "claude.exe"));
                candidates.Add(Path.Combine(userProfile, ".local", "bin", "claude.cmd"));
                candidates.Add(Path.Combine(userProfile, ".claude", "local", "claude.exe"));
            }

            if (!string.IsNullOrEmpty(appData))
            {
                candidates.Add(Path.Combine(appData, "npm", "claude.cmd"));
                candidates.Add(Path.Combine(appData, "npm", "claude.exe"));
            }

            if (!string.IsNullOrEmpty(localAppData))
            {
                candidates.Add(Path.Combine(localAppData, "Programs", "claude", "claude.exe"));
                candidates.Add(Path.Combine(localAppData, "Claude", "claude.exe"));
            }

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            throw new InvalidOperationException(NotFoundMessage);
        }

        /// <summary>
        /// 试查找 claude（不抛异常）：<paramref name="configuredPath"/> 为空 = 自动查找。
        /// 【AI 设置】用它把「已找到：C:\…\claude.cmd」或失败原因直接显示在界面上。
        /// </summary>
        public static bool TryResolveExecutable(string configuredPath,
            out string resolvedPath, out string error)
        {
            resolvedPath = string.Empty;
            error = string.Empty;

            try
            {
                resolvedPath = ResolveExecutable(configuredPath);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>依次按 exe / cmd / bat 的顺序在 PATH 里找 claude；找不到返回空字符串。</summary>
        private static string FindExecutableOnPath()
        {
            string found = FindOnPath(ExecutableBaseName + ".exe");

            if (string.IsNullOrEmpty(found))
            {
                found = FindOnPath(ExecutableBaseName + ".cmd");
            }

            if (string.IsNullOrEmpty(found))
            {
                found = FindOnPath(ExecutableBaseName + ".bat");
            }

            return found;
        }

        /// <summary>在 PATH 的各个目录里查找指定文件名；找不到返回空字符串。</summary>
        public static string FindOnPath(string fileName)
        {
            string name = NormalizePath(fileName);

            if (name.Length == 0)
            {
                return string.Empty;
            }

            if (name.IndexOf('\\') >= 0 || name.IndexOf('/') >= 0 || Path.IsPathRooted(name))
            {
                return File.Exists(name) ? Path.GetFullPath(name) : string.Empty;
            }

            string path = Environment.GetEnvironmentVariable("PATH");

            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            string[] directories = path.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string directory in directories)
            {
                string trimmed = directory.Trim().Trim('"');

                if (trimmed.Length == 0)
                {
                    continue;
                }

                try
                {
                    string candidate = Path.Combine(trimmed, name);

                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch (Exception)
                {
                    // PATH 里有非法路径（含引号 / 通配符）时跳过，不影响其它目录
                }
            }

            return string.Empty;
        }

        /// <summary>执行 <c>claude --version</c> 取版本号（【AI 设置】的测试按钮用，确认路径可用）。</summary>
        public static string GetVersion(string executablePath, int timeoutSeconds)
        {
            List<string> arguments = new List<string>();
            arguments.Add("--version");

            string stdout;
            string stderr;
            Execute(executablePath, arguments, string.Empty, timeoutSeconds, null, out stdout, out stderr);

            string text = (stdout ?? string.Empty).Trim();

            return text.Length > 0 ? text : (stderr ?? string.Empty).Trim();
        }

        /// <summary>
        /// 以非交互模式执行一次问答，返回 Claude 的正文回答（不关心 token 用量时的简写）。
        /// </summary>
        /// <param name="executablePath">claude.exe / claude.cmd 的完整路径（见 <see cref="ResolveExecutable"/>）。</param>
        /// <param name="model">模型名（可空 = 用本机 Claude Code 的默认模型）。</param>
        /// <param name="environmentText">追加环境变量（KEY=VALUE，用 ; 或换行分隔；可空）。</param>
        /// <param name="systemPrompt">系统提示词（可空）。</param>
        /// <param name="userPrompt">用户提示词（可以很长，走标准输入）。</param>
        /// <param name="timeoutSeconds">超时（秒）。</param>
        public static string Complete(string executablePath, string model, string environmentText,
            string systemPrompt, string userPrompt, int timeoutSeconds)
        {
            AiTokenUsage usage;

            return Complete(executablePath, model, environmentText, systemPrompt, userPrompt,
                timeoutSeconds, out usage);
        }

        /// <summary>
        /// 以非交互模式执行一次问答，返回正文，并取回本次 token 用量。
        /// </summary>
        /// <remarks>
        /// 优先用 <c>--output-format json</c>（新版 CLI）：stdout 是 JSON，<c>result</c> 是正文、
        /// <c>usage</c> 是 token 用量、<c>total_cost_usd</c> 是本次费用；
        /// 旧版 CLI 不认识 json 输出时自动退回 <c>--output-format text</c>
        /// （正文照常返回，<paramref name="usage"/> 为 null）。
        /// </remarks>
        /// <param name="executablePath">claude.exe / claude.cmd 的完整路径（见 <see cref="ResolveExecutable"/>）。</param>
        /// <param name="model">模型名（可空 = 用本机 Claude Code 的默认模型）。</param>
        /// <param name="environmentText">追加环境变量（KEY=VALUE，用 ; 或换行分隔；可空）。</param>
        /// <param name="systemPrompt">系统提示词（可空）。</param>
        /// <param name="userPrompt">用户提示词（可以很长，走标准输入）。</param>
        /// <param name="timeoutSeconds">超时（秒）。</param>
        /// <param name="usage">本次调用的 token 用量（CLI 没返回时为 null）。</param>
        public static string Complete(string executablePath, string model, string environmentText,
            string systemPrompt, string userPrompt, int timeoutSeconds, out AiTokenUsage usage)
        {
            usage = null;

            string executable = ResolveExecutable(executablePath);
            string workingDirectory = EnsureWorkingDirectory();
            Dictionary<string, string> environment = ParseEnvironment(environmentText);

            string systemPromptFile = null;

            try
            {
                bool useSystemPromptFile = !string.IsNullOrWhiteSpace(systemPrompt);

                if (useSystemPromptFile)
                {
                    systemPromptFile = Path.Combine(workingDirectory,
                        "system-" + Guid.NewGuid().ToString("N") + ".txt");
                    File.WriteAllText(systemPromptFile, systemPrompt, Utf8NoBom);
                }

                string stdout;
                string stderr;
                bool jsonOutput = true;

                try
                {
                    Execute(executable, BuildArguments(model, systemPromptFile, jsonOutput), userPrompt,
                        timeoutSeconds, environment, out stdout, out stderr);
                }
                catch (ClaudeCliUsageException)
                {
                    // 本机 CLI 不认识参数，分两种情况退回：
                    // ① 旧版本没有 --system-prompt-file → 系统提示词拼在标准输入开头再跑一次；
                    // ② 旧版本连 --output-format json 也没有 → 再用纯文本输出跑一次（拿不到 token 用量）。
                    string combined = useSystemPromptFile
                        ? systemPrompt + Environment.NewLine + Environment.NewLine + userPrompt
                        : userPrompt;

                    try
                    {
                        Execute(executable, BuildArguments(model, null, jsonOutput), combined,
                            timeoutSeconds, environment, out stdout, out stderr);
                    }
                    catch (ClaudeCliUsageException)
                    {
                        jsonOutput = false;

                        Execute(executable, BuildArguments(model, null, jsonOutput), combined,
                            timeoutSeconds, environment, out stdout, out stderr);
                    }
                }

                string reply;

                if (!TryReadJsonReply(stdout, out reply, out usage))
                {
                    // 不是 CLI 的 JSON 结果（旧版 text 输出 / 其它实现）：按原文返回
                    reply = (stdout ?? string.Empty).Trim();
                }

                if (reply.Length == 0)
                {
                    string detail = Shorten(stderr);

                    throw new InvalidOperationException(
                        "本机 Claude Code CLI 没有返回任何内容" +
                        (detail.Length == 0 ? "。" : "：" + detail));
                }

                return reply;
            }
            finally
            {
                if (!string.IsNullOrEmpty(systemPromptFile))
                {
                    try
                    {
                        File.Delete(systemPromptFile);
                    }
                    catch (Exception)
                    {
                        // 临时文件删不掉不影响结果
                    }
                }
            }
        }

        /// <summary>
        /// 解析 <c>--output-format json</c> 的输出：正文取 <c>result</c>，
        /// 用量取 <c>usage</c>（input_tokens / cache_creation_input_tokens / cache_read_input_tokens /
        /// output_tokens）与 <c>total_cost_usd</c>。
        /// </summary>
        /// <returns>stdout 是 CLI 的 JSON 结果时返回 true；不是 JSON（旧版 text 输出等）返回 false。</returns>
        private static bool TryReadJsonReply(string stdout, out string reply, out AiTokenUsage usage)
        {
            reply = null;
            usage = null;

            string text = (stdout ?? string.Empty).Trim();

            if (text.Length == 0 || text[0] != '{')
            {
                return false;
            }

            Dictionary<string, object> root;

            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = int.MaxValue;
                root = serializer.DeserializeObject(text) as Dictionary<string, object>;
            }
            catch (Exception)
            {
                // 不是 JSON（例如 claude 输出了日志文字）：按原文交给上层
                return false;
            }

            if (root == null)
            {
                return false;
            }

            object resultValue;

            if (!root.TryGetValue("result", out resultValue) || resultValue == null)
            {
                return false;
            }

            reply = Convert.ToString(resultValue, CultureInfo.InvariantCulture).Trim();

            if (ReadBool(root, "is_error"))
            {
                string detail = reply.Length == 0 ? "未知原因" : Shorten(reply);

                throw new InvalidOperationException("本机 Claude Code CLI 执行失败：" + detail);
            }

            usage = ReadCliUsage(root);

            return true;
        }

        /// <summary>从 CLI 的 JSON 结果里取 token 用量（没有 usage 或全为 0 时返回 null）。</summary>
        private static AiTokenUsage ReadCliUsage(Dictionary<string, object> root)
        {
            AiTokenUsage usage = new AiTokenUsage();
            Dictionary<string, object> node = ReadDictionary(root, "usage");

            if (node != null)
            {
                // input_tokens 只是「未命中缓存的输入」，缓存读 / 写要另外加上才是总输入
                int cacheWrite = ReadInt(node, "cache_creation_input_tokens");
                int cacheRead = ReadInt(node, "cache_read_input_tokens");

                usage.PromptTokens = ReadInt(node, "input_tokens") + cacheWrite + cacheRead;
                usage.CompletionTokens = ReadInt(node, "output_tokens");
                usage.TotalTokens = usage.PromptTokens + usage.CompletionTokens;
                usage.CachedTokens = cacheWrite + cacheRead;
            }

            decimal cost;

            if (TryReadDecimal(root, "total_cost_usd", out cost) && cost > 0m)
            {
                usage.CostUsd = cost;
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
                return 0;
            }
        }

        /// <summary>取 JSON 对象里的布尔值（缺失 / 非布尔时返回 false）。</summary>
        private static bool ReadBool(Dictionary<string, object> node, string key)
        {
            object value;

            if (node == null || string.IsNullOrEmpty(key) || !node.TryGetValue(key, out value) || value == null)
            {
                return false;
            }

            if (value is bool)
            {
                return (bool)value;
            }

            bool parsed;

            return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) && parsed;
        }

        /// <summary>取 JSON 对象里的小数值（缺失 / 非数字时返回 false）。</summary>
        private static bool TryReadDecimal(Dictionary<string, object> node, string key, out decimal result)
        {
            result = 0m;

            object value;

            if (node == null || string.IsNullOrEmpty(key) || !node.TryGetValue(key, out value) || value == null)
            {
                return false;
            }

            try
            {
                result = Convert.ToDecimal(value, CultureInfo.InvariantCulture);

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>把 "KEY=VALUE;KEY2=VALUE2" 解析成字典；格式不对时抛出异常（便于界面提示）。</summary>
        public static Dictionary<string, string> ParseEnvironment(string environmentText)
        {
            Dictionary<string, string> result =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(environmentText))
            {
                return result;
            }

            string[] entries = environmentText.Split(new char[] { ';', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (string entry in entries)
            {
                string text = entry.Trim();

                if (text.Length == 0)
                {
                    continue;
                }

                int separator = text.IndexOf('=');

                if (separator <= 0)
                {
                    throw new InvalidOperationException(
                        "追加环境变量的格式应为 KEY=VALUE（用 ; 分隔），无法识别：" + text);
                }

                string key = text.Substring(0, separator).Trim();
                string value = text.Substring(separator + 1).Trim();

                if (key.Length == 0)
                {
                    throw new InvalidOperationException("追加环境变量缺少变量名：" + text);
                }

                result[key] = value;
            }

            return result;
        }

        /// <summary>
        /// 拼装命令行参数（-p / 输出格式 / 系统提示词文件 / 模型）。
        /// </summary>
        /// <param name="model">模型名（可空）。</param>
        /// <param name="systemPromptFile">系统提示词文件（可空 = 不传）。</param>
        /// <param name="jsonOutput">
        /// true = <c>--output-format json</c>：stdout 是带 <c>result</c> / <c>usage</c> /
        /// <c>total_cost_usd</c> 的 JSON，可以取到 token 用量（见 <see cref="Complete(string,string,string,string,string,int,out AiTokenUsage)"/>）；
        /// false = <c>--output-format text</c>：旧版 CLI 不认 json 时退回这种写法（拿不到 token 用量）。
        /// </param>
        private static List<string> BuildArguments(string model, string systemPromptFile, bool jsonOutput)
        {
            List<string> arguments = new List<string>();
            arguments.Add("-p");
            arguments.Add(PrintQuery);
            arguments.Add("--output-format");
            arguments.Add(jsonOutput ? "json" : "text");

            if (!string.IsNullOrWhiteSpace(systemPromptFile))
            {
                arguments.Add("--system-prompt-file");
                arguments.Add(systemPromptFile);
            }

            if (!string.IsNullOrWhiteSpace(model))
            {
                arguments.Add("--model");
                arguments.Add(model.Trim());
            }

            return arguments;
        }

        /// <summary>确认工作目录存在（不存在时创建）。</summary>
        private static string EnsureWorkingDirectory()
        {
            string directory = WorkingDirectory;

            try
            {
                Directory.CreateDirectory(directory);
                return directory;
            }
            catch (Exception)
            {
                // 临时目录建不出来时退回系统临时目录
                return Path.GetTempPath();
            }
        }

        /// <summary>不带追加环境变量的执行（测试版本号用）。</summary>
        private static void Execute(string executablePath, IList<string> arguments, string stdinText,
            int timeoutSeconds, out string stdout, out string stderr)
        {
            Execute(executablePath, arguments, stdinText, timeoutSeconds, null, out stdout, out stderr);
        }

        /// <summary>
        /// 启动子进程、写入标准输入、读取标准输出 / 标准错误。
        /// 标准输入用 UTF-8 原始字节写入（不经 StreamWriter，避免系统代码页转换）。
        /// </summary>
        private static void Execute(string executablePath, IList<string> arguments, string stdinText,
            int timeoutSeconds, IDictionary<string, string> environment,
            out string stdout, out string stderr)
        {
            string executable = ResolveExecutable(executablePath);
            string workingDirectory = EnsureWorkingDirectory();
            bool useCommandShell = IsBatchFile(executable);
            string commandShell = Environment.GetEnvironmentVariable("ComSpec");

            if (string.IsNullOrEmpty(commandShell))
            {
                commandShell = "cmd.exe";
            }

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = useCommandShell ? commandShell : executable;
            startInfo.Arguments = useCommandShell
                ? BuildCommandShellArguments(executable, arguments)
                : JoinArguments(arguments);
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = Utf8NoBom;
            startInfo.StandardErrorEncoding = Utf8NoBom;
            startInfo.WorkingDirectory = workingDirectory;

            if (environment != null)
            {
                foreach (KeyValuePair<string, string> pair in environment)
                {
                    // 覆盖同名变量（EnvironmentVariables 已预置当前进程的变量）
                    startInfo.EnvironmentVariables[pair.Key] = pair.Value;
                }
            }

            int timeoutMilliseconds = Math.Max(10, timeoutSeconds) * 1000;

            using (Process process = new Process())
            {
                process.StartInfo = startInfo;
                process.Start();

                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();

                try
                {
                    byte[] payload = Utf8NoBom.GetBytes(stdinText ?? string.Empty);

                    if (payload.Length > 0)
                    {
                        process.StandardInput.BaseStream.Write(payload, 0, payload.Length);
                        process.StandardInput.BaseStream.Flush();
                    }
                }
                catch (IOException)
                {
                    // 子进程提前退出（例如参数不认识）时管道已关闭，忽略写入失败
                }
                catch (ObjectDisposedException)
                {
                    // 同上
                }
                finally
                {
                    try
                    {
                        process.StandardInput.Close();
                    }
                    catch (Exception)
                    {
                        // 忽略
                    }
                }

                if (!process.WaitForExit(timeoutMilliseconds))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception)
                    {
                        // 进程可能已经退出
                    }

                    stdout = string.Empty;
                    stderr = string.Empty;

                    throw new TimeoutException(
                        "本机 Claude Code CLI 超过 " + timeoutSeconds + " 秒没有响应" +
                        "（可在【AI 设置】里加大超时，或减少一次送入的文本量）。");
                }

                stdout = outputTask.GetAwaiter().GetResult();
                stderr = errorTask.GetAwaiter().GetResult();

                int exitCode = process.ExitCode;

                if (exitCode != 0)
                {
                    string detail = Shorten(stderr);

                    if (detail.Length == 0)
                    {
                        detail = Shorten(stdout);
                    }

                    if (LooksLikeUnknownOption(detail))
                    {
                        throw new ClaudeCliUsageException(
                            "本机 Claude Code CLI 不支持本次使用的参数：" + detail);
                    }

                    throw new InvalidOperationException(
                        "本机 Claude Code CLI 执行失败（退出码 " + exitCode + "）：" + detail);
                }
            }
        }

        /// <summary>命令行里出现「不认识的参数」时的提示文字（用于退回旧版调用方式重试）。</summary>
        private static bool LooksLikeUnknownOption(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            return text.IndexOf("unknown option", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("unknown argument", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("unrecognized option", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>是否为 .cmd / .bat（需要经 cmd.exe 执行）。</summary>
        private static bool IsBatchFile(string path)
        {
            string extension = Path.GetExtension(path);

            return string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>cmd.exe 的 <c>/d /s /c "…"</c> 形式（外层引号由 /s 规则剥掉）。</summary>
        private static string BuildCommandShellArguments(string executablePath, IList<string> arguments)
        {
            return "/d /s /c \"" + Quote(executablePath) + " " + JoinArguments(arguments) + "\"";
        }

        /// <summary>按 Windows 命令行规则拼接参数。</summary>
        private static string JoinArguments(IList<string> arguments)
        {
            StringBuilder builder = new StringBuilder();

            for (int i = 0; i < arguments.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(Quote(arguments[i]));
            }

            return builder.ToString();
        }

        /// <summary>按 Windows 命令行规则给单个参数加引号（含反斜杠 / 双引号转义）。</summary>
        private static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            bool needQuote = false;

            foreach (char c in value)
            {
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '"')
                {
                    needQuote = true;
                    break;
                }
            }

            if (!needQuote)
            {
                return value;
            }

            StringBuilder builder = new StringBuilder(value.Length + 8);
            builder.Append('"');
            int backslashes = 0;

            foreach (char c in value)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                    backslashes = 0;
                    continue;
                }

                if (backslashes > 0)
                {
                    builder.Append('\\', backslashes);
                    backslashes = 0;
                }

                builder.Append(c);
            }

            if (backslashes > 0)
            {
                builder.Append('\\', backslashes * 2);
            }

            builder.Append('"');

            return builder.ToString();
        }

        /// <summary>去掉包裹的引号与首尾空白。</summary>
        private static string NormalizePath(string path)
        {
            return string.IsNullOrEmpty(path) ? string.Empty : path.Trim().Trim('"').Trim();
        }

        /// <summary>把输出压成单行并截断，便于放进日志 / 异常信息。</summary>
        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();

            return singleLine.Length <= 400 ? singleLine : singleLine.Substring(0, 400) + "...";
        }

        /// <summary>本机 CLI 不认识参数的内部异常（用于退回旧版调用方式重试一次）。</summary>
        private sealed class ClaudeCliUsageException : Exception
        {
            public ClaudeCliUsageException(string message)
                : base(message)
            {
            }
        }
    }
}
