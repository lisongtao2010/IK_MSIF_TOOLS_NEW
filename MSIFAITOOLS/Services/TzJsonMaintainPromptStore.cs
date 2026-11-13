using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// 【CMD 分析】第 0 步 · 特注品.json 维护的提示语：系统提示词 + 用户提示词模板 + 来源说明。
    /// 用户模板里的占位符（由 <see cref="CmdTzJsonMaintainer"/> 替换）：
    /// <c>{{DRAWING_NO}}</c> / <c>{{EXISTING_ITEMS}}</c> / <c>{{EXCEL_TABLE}}</c> /
    /// <c>{{EXCEL_DUMP}}</c>（整表原文，主依据）/ <c>{{JSON_STATE}}</c>。
    /// </summary>
    internal sealed class TzJsonMaintainPromptSet
    {
        public TzJsonMaintainPromptSet(string systemPrompt, string userTemplate, string source)
        {
            SystemPrompt = string.IsNullOrEmpty(systemPrompt) ? string.Empty : systemPrompt.Trim();
            UserTemplate = string.IsNullOrEmpty(userTemplate) ? string.Empty : userTemplate.Trim();
            Source = source ?? string.Empty;
        }

        /// <summary>系统提示词（角色与输出约束）。</summary>
        public string SystemPrompt { get; private set; }

        /// <summary>用户提示词模板（含三个占位符）。</summary>
        public string UserTemplate { get; private set; }

        /// <summary>提示语来源说明（文件路径等，用于写入运行日志）。</summary>
        public string Source { get; private set; }
    }

    /// <summary>
    /// 第 0 步提示语的加载器：优先读 exe 目录
    /// <c>Prompts\TzJsonMaintain.Prompt.txt</c>（运行时可改、无需重新编译），
    /// 不可用时回退到编译进 exe 的同名内嵌资源，最后回退到内置默认值。
    /// 文件变化后（按修改时间）自动重新加载。
    /// <para>
    /// 文件格式与 <see cref="SpecialProductPromptStore"/> 相同，分两个分节：
    /// <c>[system]</c> / <c>[user]</c>。第一个分节之前的 <c>#</c> 注释行不送给 AI。
    /// </para>
    /// </summary>
    internal static class TzJsonMaintainPromptStore
    {
        /// <summary>提示语文件所在目录（相对 exe 目录）。</summary>
        public const string PromptDirectoryName = "Prompts";

        /// <summary>提示语文件名。</summary>
        public const string PromptFileName = "TzJsonMaintain.Prompt.txt";

        /// <summary>内嵌资源名（编译进 exe 的同一份提示语文件）。</summary>
        public const string EmbeddedResourceName = "MSIFAITOOLS.Prompts.TzJsonMaintain.Prompt.txt";

        /// <summary>来源说明：外部文件。</summary>
        public const string SourceExternal = "外部文件";

        /// <summary>来源说明：内嵌资源。</summary>
        public const string SourceEmbedded = "内置资源";

        /// <summary>来源说明：内置默认值。</summary>
        public const string SourceDefault = "内置默认值";

        private const string SectionSystem = "system";

        private const string SectionUser = "user";

        /// <summary>兜底用的系统提示词（外部文件与内置资源都不可用时）。</summary>
        private const string DefaultSystemPrompt =
            "你是建筑门窗（MSIF）特注品部品表的整理专家。用户会给你一份 Excel 文件的「整表原文」" +
            "（図番 文件名第 5 位是 Z 的特注品），以及 classify\\特注品.json 里同一份文件已经登记的条目。" +
            "整表原文的写法：每张工作表一段「### 工作表「表名」」，下面每行「行N: 单元格 | 单元格 | …」，" +
            "单元格按 Excel 里的原样排列，空格子就是空着。" +
            "现场 Excel 的排版千差万别：部品表可能在任意一张表、任意位置，表头可能多行、有合并单元格、有说明文字，" +
            "所以你要自己看懂这份文件：先找出部品表（品種 / 部品名 / 枠区分 / 部品コード / 追加対応特注寸法 这几列），" +
            "再判断其中哪些条目应该追加到 特注品.json。" +
            "程序按列名规则解析出来的结果只作参考，可能是空的，不要被它限制，一切以整表原文为准。" +
            "已经在 json 里的条目（品種 + 部品名 + 部品コード 相同）不要重复给；" +
            "只给真正属于特注品、需要按 品種 追加対応的部品；" +
            "合计行、注記行、写着「追加不要」的行、没有任何部品名和部品コード的行不要给；" +
            "用户说「还没有 特注品.json（作成名）」时按新建处理：把该部品表里属于特注品的条目全部列出；" +
            "不要改写、不要编造：字段只能照抄整表原文里的单元格内容，原文里没有的内容一律留空。" +
            "你只输出一个 JSON 对象，不输出解释文字，不输出 Markdown 代码块，不输出注释。";

        /// <summary>兜底用的用户提示词模板。</summary>
        private const string DefaultUserTemplate =
            "図番：{{DRAWING_NO}}\n\n" +
            "任务：判断下面「Excel 部品表」里哪些条目要追加到 classify\\特注品.json，并按 JSON 输出。\n\n" +
            "「特注品.json 里已经有的条目」（同一份 Excel 的：品種 / 部品名 / 部品コード）：\n" +
            "{{EXISTING_ITEMS}}\n\n" +
            "特注品.json 的当前状态：\n" +
            "{{JSON_STATE}}\n\n" +
            "「Excel 部品表」（程序按列名规则解析的结果，可能是空的，仅供参考）：\n" +
            "{{EXCEL_TABLE}}\n\n" +
            "「Excel 整表原文」（每张工作表一段：### 工作表「表名」；每行：行N: 单元格 | 单元格 | …，" +
            "单元格按 Excel 里的原样排列，空格子就是空着）：\n" +
            "{{EXCEL_DUMP}}\n\n" +
            "注意：现场 Excel 的排版千差万别，部品表可能在任意一张表、任意位置；请你自己从整表原文里" +
            "找出部品表并认出每一列（品種 / 部品名 / 枠区分 / 部品コード / 追加対応特注寸法），" +
            "items 里尽量带上 sheet（工作表名）与 row（行号）方便核对。\n\n" +
            "输出格式（严格按这个 JSON 结构，不要加别的键）：\n" +
            "{\"add\": true 或 false, \"reason\": \"一句话说明\", " +
            "\"items\": [{\"品種\": \"\", \"部品名\": \"\", \"枠区分\": \"\", " +
            "\"部品コード\": \"\", \"追加対応特注寸法\": \"\"}]}\n";

        private static readonly object SyncRoot = new object();

        private static TzJsonMaintainPromptSet _cached;

        private static DateTime _cachedStamp = DateTime.MinValue;

        private static string _lastLoadError;

        /// <summary>最近一次加载失败的原因（读外部文件 / 读内嵌资源失败时）。</summary>
        public static string LastLoadError
        {
            get { return _lastLoadError; }
        }

        /// <summary>
        /// 取提示语（第一次调用时读文件，之后按修改时间判断是否需要重新加载）。
        /// 文件不存在 / 读不动时不抛异常，退回内置资源或内置默认值。
        /// </summary>
        public static TzJsonMaintainPromptSet Load()
        {
            string path = GetPromptPath();
            DateTime stamp = DateTime.MinValue;

            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    stamp = File.GetLastWriteTimeUtc(path);
                }
            }
            catch (Exception)
            {
                stamp = DateTime.MinValue;
            }

            lock (SyncRoot)
            {
                if (_cached != null && stamp == _cachedStamp)
                {
                    return _cached;
                }

                TzJsonMaintainPromptSet set = LoadCore(path);
                _cached = set;
                _cachedStamp = stamp;

                return set;
            }
        }

        /// <summary>提示语文件的全路径（exe 目录\Prompts\TzJsonMaintain.Prompt.txt）。</summary>
        public static string GetPromptPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PromptDirectoryName,
                PromptFileName);
        }

        /// <summary>真正加载：外部文件 → 内嵌资源 → 内置默认值。</summary>
        private static TzJsonMaintainPromptSet LoadCore(string path)
        {
            string text = TryReadFile(path);
            string source = SourceExternal;

            if (string.IsNullOrWhiteSpace(text))
            {
                text = TryReadEmbedded();
                source = SourceEmbedded;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return new TzJsonMaintainPromptSet(DefaultSystemPrompt, DefaultUserTemplate,
                    SourceDefault);
            }

            Dictionary<string, string> sections = ParseSections(text);

            string system;

            if (!sections.TryGetValue(SectionSystem, out system) || string.IsNullOrWhiteSpace(system))
            {
                system = DefaultSystemPrompt;
            }

            string user;

            if (!sections.TryGetValue(SectionUser, out user) || string.IsNullOrWhiteSpace(user))
            {
                user = DefaultUserTemplate;
            }

            return new TzJsonMaintainPromptSet(system, user, source + "（" + path + "）");
        }

        /// <summary>
        /// 按 <c>[分节名]</c> 拆分提示语文件；第一个分节之前的行（<c>#</c> 注释）丢掉。
        /// </summary>
        private static Dictionary<string, string> ParseSections(string text)
        {
            Dictionary<string, string> sections =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(text))
            {
                return sections;
            }

            string currentName = null;
            StringBuilder current = new StringBuilder();
            bool seenSection = false;

            using (StringReader reader = new StringReader(text))
            {
                string line;

                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();

                    if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']')
                    {
                        FlushSection(sections, currentName, current);
                        currentName = trimmed.Substring(1, trimmed.Length - 2).Trim();
                        current = new StringBuilder();
                        seenSection = true;

                        continue;
                    }

                    if (!seenSection || current == null)
                    {
                        continue;
                    }

                    current.AppendLine(line);
                }
            }

            FlushSection(sections, currentName, current);

            return sections;
        }

        /// <summary>把当前分节内容写入字典（同名分节多次出现时按顺序拼接）。</summary>
        private static void FlushSection(IDictionary<string, string> sections, string name,
            StringBuilder content)
        {
            if (string.IsNullOrWhiteSpace(name) || content == null)
            {
                return;
            }

            string text = content.ToString().Trim();

            if (text.Length == 0)
            {
                return;
            }

            if (sections.ContainsKey(name))
            {
                sections[name] = (sections[name] + "\n" + text).Trim();
            }
            else
            {
                sections[name] = text;
            }
        }

        /// <summary>读取外部提示语文件（UTF-8，自动识别 BOM）。</summary>
        private static string TryReadFile(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    // 外部文件不存在是正常情况（exe 目录里没有也能用内置资源），不记为错误
                    return null;
                }

                return File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                SetLoadError("读取提示语文件失败（" + path + "）：" + ex.Message);

                return null;
            }
        }

        /// <summary>读取编译进 exe 的提示语资源。</summary>
        private static string TryReadEmbedded()
        {
            try
            {
                Assembly assembly = typeof(TzJsonMaintainPromptStore).Assembly;

                using (Stream stream = assembly.GetManifestResourceStream(EmbeddedResourceName))
                {
                    if (stream == null)
                    {
                        return null;
                    }

                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            catch (Exception ex)
            {
                SetLoadError("读取内置提示语资源失败：" + ex.Message);

                return null;
            }
        }

        /// <summary>记录失败原因（多次失败时保留最后一条）。</summary>
        private static void SetLoadError(string message)
        {
            _lastLoadError = message;
        }
    }
}
