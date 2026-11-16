using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// CadPartSplitter 批量分割的执行结果。
    /// </summary>
    internal sealed class CadPartSplitResult
    {
        public CadPartSplitResult()
        {
            ExitCode = -1;
        }

        /// <summary>找到并成功启动了外部 CadPartSplitter.exe 时为 true（找不到时由调用方跳过第 1 步）。</summary>
        public bool ExeFound { get; set; }

        /// <summary>实际执行的 CadPartSplitter.exe 完整路径（找不到时为 null）。</summary>
        public string ExePath { get; set; }

        /// <summary>退出码为 0 时为 true（含「该文件夹下没有可处理的 PDF」这种情况）。</summary>
        public bool Success { get; set; }

        /// <summary>等价于原命令行退出码。0 = 全部成功，1 = 输入无效，2 = 有失败项。</summary>
        public int ExitCode { get; set; }

        /// <summary>失败原因（成功时为 null）。</summary>
        public string ErrorMessage { get; set; }

        /// <summary>输出末尾若干行（失败时便于排查）。</summary>
        public string OutputTail { get; set; }

        /// <summary>CadPartSplitter 自己输出的汇总行（形如「共 N 张：成功 X，失败 Y，零件合计 Z 个」）。</summary>
        public string Summary { get; set; }

        /// <summary>本次执行耗时（毫秒）。</summary>
        public long ElapsedMilliseconds { get; set; }
    }

    /// <summary>
    /// 以子进程方式启动独立的 <c>CadPartSplitter.exe</c>，做「零件分割」。
    ///
    /// 之所以走外部程序（而不是在本进程内调用）：
    ///   * 分割工程自带 OpenCvSharp4 的 x64 原生库与 pdfium.dll，单独一个 exe 更好维护，
    ///     也能单独打开它的界面 / 命令行调试；
    ///   * 升级分割逻辑时只替换 <c>CadPartSplitter.exe</c>，不必重新编译整个 MSIF AI TOOLS。
    /// 【开始分解】只执行本类找到的 exe；主程序里不再编译分割源码（<c>MSIFAITOOLS.csproj</c>
    /// 已去掉 <c>..\CadPartSplitter\*.cs</c> 链接），日志里的输出根目录由本类的
    /// <see cref="GetDefaultBatchOutputDirectory"/> 按 exe 的同一规则复刻。
    ///
    /// exe 位置：首选 <c>&lt;程序目录&gt;\CadPartSplitter.exe</c> —— 与 <c>MSIFAITOOLS.exe</c>
    /// 同一个文件夹（现在的发布布局：把分割工程输出目录里的 CadPartSplitter.exe 连同它依赖的
    /// pdfium.dll / OpenCvSharp 原生库一起放到 MSIFAITOOLS.exe 旁边即可）。
    /// 兼容旧布局：<c>&lt;程序目录&gt;\CadPartSplitter\CadPartSplitter.exe</c>
    /// （分割工程的 <c>OutputPath</c> 仍是 <c>..\MSIFAITOOLS\bin\{Debug,Release}\CadPartSplitter\</c>，
    /// VS 里 F5 出来的只有这一份），同目录那份不存在时才用它。
    /// 优先级最高的是环境变量 <c>CADPARTSPLITTER_EXE</c> 显式指定的路径（调试时临时指向别的版本用；
    /// 不会去上级目录 / 分割工程源码目录里找）。
    ///
    /// 对调用方的约定：
    ///   * 输出目录：<c>&lt;输入文件夹&gt;\AiAppData\&lt;图纸名&gt;</c>（不传第二个参数，由 exe 自己推导）；
    ///   * 逐行日志：把 exe 的 stdout / stderr 逐行回调给 <c>onLog</c>（含 <c>[DONE]</c> / <c>[批量完成]</c> 汇总行）；
    ///   * 退出码语义：0 = 全部成功（含「没有可处理的 PDF」），1 = 输入无效 / 找不到 exe，2 = 有失败项。
    /// </summary>
    internal static class CadPartSplitterRunner
    {
        /// <summary>外部执行程序的名称。</summary>
        public const string ExeFileName = "CadPartSplitter.exe";

        /// <summary>可选：显式指定 CadPartSplitter.exe 路径的环境变量（优先级最高，不设就走 &lt;程序目录&gt; 下的默认位置）。</summary>
        public const string ExePathEnvironmentVariable = "CADPARTSPLITTER_EXE";

        /// <summary>旧布局的子目录名（&lt;程序目录&gt;\CadPartSplitter\），只作兼容兜底用（见 <see cref="FindExePath"/>）。</summary>
        public const string ExeFolderName = "CadPartSplitter";

        /// <summary>最多缓存多少行输出（只用于失败时提示，不落盘，防止内存无限增长）。</summary>
        private const int MaxBufferedLines = 4000;

        /// <summary>失败时拼进错误信息的末尾行数。</summary>
        private const int TailLineCount = 8;

        /// <summary>
        /// 取要执行的 <c>CadPartSplitter.exe</c>，优先级依次为：
        /// <list type="number">
        ///   <item><description>环境变量 <c>CADPARTSPLITTER_EXE</c> 指定的路径（文件确实存在才算）；</description></item>
        ///   <item><description><c>&lt;程序目录&gt;\CadPartSplitter.exe</c> —— 与 MSIFAITOOLS.exe 同目录（现在的发布布局）；</description></item>
        ///   <item><description><c>&lt;程序目录&gt;\CadPartSplitter\CadPartSplitter.exe</c> —— 旧布局 / VS 调试输出，兼容兜底。</description></item>
        /// </list>
        /// 全都找不到时返回 null，由 <see cref="RunBatch"/> 转成「已跳过第 1 步」的警告。
        /// </summary>
        public static string FindExePath()
        {
            // 可选覆盖：设了 CADPARTSPLITTER_EXE 就用它（调试时临时指向别的版本），不设走默认位置
            string configured = null;

            try
            {
                configured = Environment.GetEnvironmentVariable(ExePathEnvironmentVariable);
            }
            catch (Exception)
            {
                // 读不到环境变量就用下面的默认位置
            }

            if (!string.IsNullOrWhiteSpace(configured))
            {
                configured = configured.Trim().Trim('"');

                try
                {
                    if (File.Exists(configured))
                    {
                        return Path.GetFullPath(configured);
                    }
                }
                catch (Exception)
                {
                    // 环境变量里是非法路径，忽略它
                }
            }

            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

            // 首选：<程序目录>\CadPartSplitter.exe —— 与 MSIFAITOOLS.exe 同一个文件夹。
            // 发布时把分割工程输出里的 CadPartSplitter.exe 连同它依赖的 pdfium.dll /
            // OpenCvSharp 原生库一起放到 MSIFAITOOLS.exe 旁边即可。
            string sameFolder = TryResolveExistingFile(baseDirectory, null, ExeFileName);

            if (sameFolder != null)
            {
                return sameFolder;
            }

            // 兼容旧布局：<程序目录>\CadPartSplitter\CadPartSplitter.exe
            // （分割工程的 OutputPath 仍是 ..\MSIFAITOOLS\bin\{Debug,Release}\CadPartSplitter\，
            //   VS 里 F5 出来的只有这一份）。两处都没有就返回 null —— 跳过第 1 步，不阻断第 2 步。
            return TryResolveExistingFile(baseDirectory, ExeFolderName, ExeFileName);
        }

        /// <summary>
        /// 在 <paramref name="baseDirectory"/>（可再带一层子目录 <paramref name="folderName"/>）下
        /// 找 <paramref name="fileName"/>，找到时返回完整路径；不存在或路径非法时返回 null。
        /// </summary>
        /// <param name="baseDirectory">基准目录（<c>AppDomain.CurrentDomain.BaseDirectory</c>）。</param>
        /// <param name="folderName">子目录名；null / 空表示直接在基准目录下找。</param>
        /// <param name="fileName">文件名。</param>
        private static string TryResolveExistingFile(string baseDirectory, string folderName, string fileName)
        {
            string candidate;

            try
            {
                candidate = string.IsNullOrEmpty(folderName)
                    ? Path.Combine(baseDirectory, fileName)
                    : Path.Combine(baseDirectory, folderName, fileName);
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
            catch (PathTooLongException)
            {
                return null;
            }

            try
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (Exception)
            {
                // 路径非法（极少见）就当没找到
            }

            return null;
        }

        /// <summary>
        /// 启动 <see cref="ExeFileName"/> 批量分割该文件夹下的所有 PDF
        /// （等价于命令行 <c>CadPartSplitter.exe &lt;文件夹&gt;</c>）。
        ///
        /// 说明：
        ///   * 同步阻塞，几十毫秒到数分钟（取决于 PDF 数量），界面线程请放在 Task.Run 里调用；
        ///   * 输出目录：&lt;文件夹&gt;\AiAppData\&lt;图纸名&gt;（不传第二个参数，由 exe 自己推导），
        ///     与本工具的分解输出目录相同，文件名互不冲突；
        ///   * exe 的 stdout / stderr 逐行回调给 <paramref name="onLog"/>
        ///     （在 exe 的输出线程上触发，界面更新请自行 Invoke），
        ///     其中 <c>[DONE]</c> / <c>[批量完成]</c> 行会填进 <see cref="CadPartSplitResult.Summary"/>；
        ///   * 找不到 exe / 文件夹不存在 / 启动失败等任何情况都不向外抛出，
        ///     以 <see cref="CadPartSplitResult.Success"/> = false +
        ///     <see cref="CadPartSplitResult.ErrorMessage"/> 返回。
        /// </summary>
        /// <param name="folder">待处理的 PDF 文件夹。</param>
        /// <param name="onLog">逐行日志回调，可为 null。</param>
        /// <returns>执行结果。</returns>
        public static CadPartSplitResult RunBatch(string folder, Action<string> onLog)
        {
            CadPartSplitResult result = new CadPartSplitResult();
            List<string> lines = new List<string>();
            Stopwatch watch = Stopwatch.StartNew();

            // 先定位外部程序（找不到时只记警告，不抛异常 —— 第 2 步照常执行）
            string exePath = FindExePath();

            result.ExeFound = !string.IsNullOrEmpty(exePath);
            result.ExePath = exePath;

            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                watch.Stop();
                result.ElapsedMilliseconds = watch.ElapsedMilliseconds;
                result.ExitCode = 1;
                result.ErrorMessage = "PDF 文件夹不存在：" + folder;
                return result;
            }

            // 逐行收集：既回调给界面，也留着拼失败提示 / 汇总行（与合并前读 stdout 的做法一致）
            Action<string> onLine = delegate(string line)
            {
                if (line == null)
                {
                    return;
                }

                lock (lines)
                {
                    lines.Add(line);

                    if (lines.Count > MaxBufferedLines)
                    {
                        lines.RemoveAt(0);
                    }
                }

                if (onLog != null)
                {
                    try
                    {
                        onLog(line);
                    }
                    catch (Exception)
                    {
                        // 界面刷新失败不影响分割
                    }
                }
            };

            try
            {
                if (string.IsNullOrEmpty(exePath))
                {
                    // 找不到 exe：不阻断第 2 步，界面会显示「已跳过 CadPartSplitter 零件分割」
                    result.ExitCode = 1;
                    // 提示按「首选位置」写：与 MSIFAITOOLS.exe 同目录
                    // （旧布局 <程序目录>\CadPartSplitter\ 也仍然能用，见 FindExePath）
                    result.ErrorMessage =
                        "找不到 " + ExeFileName + "：请把它放到 "
                        + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ExeFileName)
                        + "（与 MSIFAITOOLS.exe 同目录），或用环境变量 "
                        + ExePathEnvironmentVariable + " 指定完整路径";

                    onLine("[ERROR] " + result.ErrorMessage);
                }
                else
                {
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    startInfo.FileName = exePath;

                    // 只传输入文件夹：输出根目录由 exe 自己推导为
                    // <输入文件夹>\AiAppData，每张图纸再各自一个子文件夹
                    startInfo.Arguments = QuoteArgument(folder);

                    // 以 exe 所在目录为工作目录，避免 exe 依赖「当前目录」找原生 dll / Prompts
                    startInfo.WorkingDirectory = Path.GetDirectoryName(exePath)
                                                 ?? AppDomain.CurrentDomain.BaseDirectory;
                    startInfo.UseShellExecute = false;
                    startInfo.CreateNoWindow = true;
                    startInfo.RedirectStandardOutput = true;
                    startInfo.RedirectStandardError = true;
                    startInfo.StandardOutputEncoding = Encoding.UTF8;
                    startInfo.StandardErrorEncoding = Encoding.UTF8;

                    onLine("[INFO] 零件分割：启动外部程序 " + exePath);
                    onLine("[INFO] 命令行：\"" + exePath + "\" " + startInfo.Arguments);
                    onLine("[INFO] 输出根目录："
                           + GetDefaultBatchOutputDirectory(folder));

                    using (Process process = new Process())
                    {
                        process.StartInfo = startInfo;

                        // exe 的 stdout / stderr 逐行转发（[CAD] 前缀由调用方自己加）
                        process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                        {
                            onLine(e.Data);
                        };
                        process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                        {
                            onLine(e.Data);
                        };

                        process.Start();
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                        process.WaitForExit();

                        // 再等一次：让异步读流把尾部几行也回调完（.NET Framework 的既有行为）
                        process.WaitForExit();

                        result.ExitCode = process.ExitCode;
                    }
                }
            }
            catch (Exception ex)
            {
                result.ExitCode = 2;
                result.ErrorMessage = ex.GetType().Name + "：" + ex.Message;

                onLine("[ERROR] " + result.ErrorMessage);
            }

            if (watch.IsRunning)
            {
                watch.Stop();
            }

            result.ElapsedMilliseconds = watch.ElapsedMilliseconds;

            lock (lines)
            {
                result.OutputTail = BuildTail(lines);
                result.Summary = FindSummary(lines);
            }

            result.Success = result.ExitCode == 0;

            if (!result.Success && string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                result.ErrorMessage = DescribeExitCode(result.ExitCode);
            }

            return result;
        }

        /// <summary>输出根目录的子文件夹名（与 CadPartSplitter 的 <c>DetectorOptions.AiAppDataFolderName</c> 一致）。</summary>
        private const string AiAppDataFolderName = "AiAppData";

        /// <summary>
        /// 复刻 <c>CadPartSplitter.DetectorOptions.GetDefaultBatchOutputDirectory</c>：
        /// 把输入文件夹转成完整路径后拼上 <c>AiAppData</c>。
        /// <para>
        /// 命令行只传「输入文件夹」、不传输出目录，exe 自己就是按这个规则落地的，
        /// 所以日志里显示的目录必须用同一套推导，才不会和实际输出位置对不上。
        /// 路径非法（空 / 含非法字符 / 过长）时退回当前工作目录。
        /// </para>
        /// </summary>
        /// <param name="inputDirectory">用户选择的 PDF 文件夹。</param>
        private static string GetDefaultBatchOutputDirectory(string inputDirectory)
        {
            string dir = null;

            if (!string.IsNullOrWhiteSpace(inputDirectory))
            {
                try
                {
                    dir = Path.GetFullPath(inputDirectory);
                }
                catch (ArgumentException)
                {
                    dir = null;
                }
                catch (NotSupportedException)
                {
                    dir = null;
                }
                catch (PathTooLongException)
                {
                    dir = null;
                }
            }

            if (string.IsNullOrEmpty(dir))
            {
                dir = Environment.CurrentDirectory;
            }

            return Path.Combine(dir, AiAppDataFolderName);
        }

        /// <summary>
        /// 把退出码翻译成人话（取值范围见 CadPartSplitter.CommandLineRunner）。
        /// </summary>
        private static string DescribeExitCode(int exitCode)
        {
            switch (exitCode)
            {
                case 1:
                    return "输入无效 / 找不到 CadPartSplitter.exe（退出码 1）";

                case 2:
                    return "有部分 PDF 分割失败（退出码 2），详见上方 [CAD] 日志";

                default:
                    return "进程异常结束（退出码 " + exitCode + "）";
            }
        }

        /// <summary>
        /// 把路径包成一个命令行参数：含空格 / 制表符 / 引号时加双引号，
        /// 末尾的反斜杠双写（否则会和结尾引号连成 <c>\"</c> 造成误转义）。
        /// </summary>
        private static string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            if (value.IndexOf(' ') < 0 && value.IndexOf('\t') < 0 && value.IndexOf('"') < 0)
            {
                return value;
            }

            string escaped = value.Replace("\"", "\\\"");

            if (escaped.EndsWith("\\", StringComparison.Ordinal))
            {
                escaped += "\\";
            }

            return "\"" + escaped + "\"";
        }

        /// <summary>
        /// 取输出末尾若干行，用于失败提示。
        /// </summary>
        private static string BuildTail(List<string> lines)
        {
            if (lines == null || lines.Count == 0)
            {
                return null;
            }

            int start = Math.Max(0, lines.Count - TailLineCount);
            StringBuilder builder = new StringBuilder();

            for (int i = start; i < lines.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                builder.AppendLine(lines[i]);
            }

            return builder.ToString().Trim();
        }

        /// <summary>
        /// 从输出中找出最后一条汇总行（「[DONE] …」或「[批量完成] …」）。
        /// </summary>
        private static string FindSummary(List<string> lines)
        {
            if (lines == null)
            {
                return null;
            }

            for (int i = lines.Count - 1; i >= 0; i--)
            {
                string line = lines[i];

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (line.StartsWith("[DONE]", StringComparison.Ordinal) ||
                    line.StartsWith("[批量完成]", StringComparison.Ordinal))
                {
                    return line.Trim();
                }
            }

            return null;
        }

    }
}
