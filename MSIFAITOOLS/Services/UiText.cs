using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MSIFAITOOLS.Services
{
    /// <summary>界面语言（主画面标题栏的「中文 / 日本語」切换）。</summary>
    internal enum UiLanguage
    {
        /// <summary>简体中文。</summary>
        Chinese = 0,

        /// <summary>日本語。</summary>
        Japanese = 1
    }

    /// <summary>
    /// 画面文字的中日双语支持（Form1 的标题栏「中文 / 日本語」切换）。
    /// <para>
    /// 中文文字照旧写在代码里（日志 / 弹窗 / 按钮都只写一份中文），
    /// 这份中文文字作为「翻译键」到 <c>UiText.Japanese.cs</c> 里查日语。
    /// 查不到就原样返回中文 —— 所以新增文字时即使忘了补翻译，程序也不会坏，
    /// 只是那一句仍显示中文。
    /// </para>
    /// <para>
    /// 查表规则（<see cref="Translate"/>）：
    /// ① 完全一致（含换行归一化）→ 直接用日语；
    /// ② 字典里带 <c>{0}</c> / <c>{1}</c> 的条目当作模板，用正则匹配「固定部分 +
    ///    任意动态部分」，例如 <c>「已选择文件夹（PDF {0} 个）」</c> 能匹配
    ///    <c>「已选择文件夹（PDF 12 个）」</c>，再用动态部分填回日语模板；
    /// ③ 都不匹配 → 返回中文原文。
    /// 这样日志 / 状态栏 / 弹窗里的「动态拼接文字」不用逐句改代码，只要在字典里加一条模板。
    /// </para>
    /// <para>
    /// 选择结果保存到 <c>%APPDATA%\MSIFAITOOLS\ui-language.json</c>
    /// （环境变量 <c>MSIF_UI_LANGUAGE</c> 优先，zh / cn = 中文，ja / jp = 日本語）。
    /// </para>
    /// </summary>
    internal static partial class UiText
    {
        /// <summary>环境变量名（最高优先级，便于现场固定语言或测试）。</summary>
        public const string LanguageEnvironmentKey = "MSIF_UI_LANGUAGE";

        /// <summary>用户配置文件夹名（与 AI 设置 / 连接设置共用）。</summary>
        public const string SettingsFolderName = "MSIFAITOOLS";

        /// <summary>语言设置文件名。</summary>
        public const string SettingsFileName = "ui-language.json";

        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        private static readonly object PatternSyncRoot = new object();

        /// <summary>带占位符（<c>{0}</c>）的日语条目对应的正则（懒加载）。</summary>
        private static List<JapanesePattern> _patterns;

        /// <summary>当前界面语言（默认中文）。</summary>
        public static UiLanguage Language { get; private set; }

        /// <summary>当前是否日语显示。</summary>
        public static bool IsJapanese
        {
            get { return Language == UiLanguage.Japanese; }
        }

        /// <summary>语言切换完成（<see cref="SetLanguage"/> 之后触发，供画面刷新文字）。</summary>
        public static event EventHandler LanguageChanged;

        /// <summary>语言设置文件完整路径。</summary>
        public static string SettingsFilePath
        {
            get
            {
                return Path.Combine(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        SettingsFolderName),
                    SettingsFileName);
            }
        }

        /// <summary>
        /// 读取上次保存的语言（程序启动时调用一次；读不到就用中文）。
        /// 环境变量 <c>MSIF_UI_LANGUAGE</c> 优先于保存的设置。
        /// </summary>
        public static void LoadSaved()
        {
            string fromEnvironment = Environment.GetEnvironmentVariable(LanguageEnvironmentKey);

            if (!string.IsNullOrEmpty(fromEnvironment))
            {
                Language = ParseLanguage(fromEnvironment);
                return;
            }

            try
            {
                string path = SettingsFilePath;

                if (!File.Exists(path))
                {
                    return;
                }

                Dictionary<string, object> data = Serializer.Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(path, Encoding.UTF8));

                object value;

                if (data != null && data.TryGetValue("Language", out value) && value != null)
                {
                    Language = ParseLanguage(Convert.ToString(value));
                }
            }
            catch (Exception)
            {
                // 设置文件坏了 / 没权限：退回中文，不影响启动
            }
        }

        /// <summary>
        /// 切换界面语言：记住选择（写不进文件也不影响本次切换），并通知画面刷新文字。
        /// </summary>
        public static void SetLanguage(UiLanguage language)
        {
            if (Language == language)
            {
                return;
            }

            Language = language;

            string error;
            Save(out error);

            EventHandler handler = LanguageChanged;

            if (handler != null)
            {
                handler(null, EventArgs.Empty);
            }
        }

        /// <summary>把当前语言写进用户设置文件。失败时返回 false 并给出原因。</summary>
        public static bool Save(out string error)
        {
            error = string.Empty;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath));

                Dictionary<string, object> data = new Dictionary<string, object>();
                data["Language"] = IsJapanese ? "ja" : "zh";

                File.WriteAllText(SettingsFilePath, Serializer.Serialize(data), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 取中文文字对应的日语（中文模式下原样返回中文）。
        /// 未登记的条目返回中文原文；带动态部分的文字见 <see cref="Translate"/>。
        /// </summary>
        public static string T(string chineseText)
        {
            return Translate(chineseText);
        }

        /// <summary>
        /// 中日文二选一（调用处直接给出两种文字：<c>UiText.P("中文", "日本語")</c>）。
        /// 用于只在一处出现、不必进字典的短句。
        /// </summary>
        public static string P(string chineseText, string japaneseText)
        {
            return IsJapanese ? japaneseText : chineseText;
        }

        /// <summary>
        /// 先按 <see cref="T"/> 取模板的日语，再做 <see cref="string.Format(string,object[])"/>。
        /// 例：<c>UiText.F("已保存 {0} 个文件", count)</c>。
        /// </summary>
        public static string F(string chineseFormat, params object[] args)
        {
            string format = Translate(chineseFormat);

            return string.Format(CultureInfo.CurrentCulture, format, args ?? new object[0]);
        }

        /// <summary>
        /// 把一句中文（可以是动态拼接出来的整句）翻成日语；中文模式或查不到时返回原文。
        /// </summary>
        public static string Translate(string chineseText)
        {
            if (!IsJapanese || string.IsNullOrEmpty(chineseText))
            {
                return chineseText;
            }

            // 弹窗里的多行文字可能是 \r\n，字典统一按 \n 写
            string normalized = chineseText.IndexOf('\r') >= 0
                ? chineseText.Replace("\r\n", "\n")
                : chineseText;

            string japanese;

            if (JapaneseTexts.TryGetValue(normalized, out japanese))
            {
                return japanese;
            }

            List<JapanesePattern> patterns = GetPatterns();

            for (int index = 0; index < patterns.Count; index++)
            {
                JapanesePattern pattern = patterns[index];
                Match match = pattern.Expression.Match(normalized);

                if (!match.Success)
                {
                    continue;
                }

                object[] args = new object[match.Groups.Count - 1];

                for (int group = 1; group < match.Groups.Count; group++)
                {
                    args[group - 1] = match.Groups[group].Value;
                }

                try
                {
                    return string.Format(CultureInfo.CurrentCulture, pattern.JapaneseTemplate, args);
                }
                catch (FormatException)
                {
                    // 日语模板里占位符写错：退回中文，不让日志 / 弹窗崩掉
                    return chineseText;
                }
            }

            return chineseText;
        }

        /// <summary>该中文是否已登记日语（用于自检 / 排查漏翻）。</summary>
        public static bool HasTranslation(string chineseText)
        {
            if (string.IsNullOrEmpty(chineseText))
            {
                return false;
            }

            return JapaneseTexts.ContainsKey(chineseText.Replace("\r\n", "\n"));
        }

        /// <summary>已登记的日语条目数（用于自检）。</summary>
        public static int TranslationCount
        {
            get { return JapaneseTexts.Count; }
        }

        private static UiLanguage ParseLanguage(string value)
        {
            string text = (value ?? string.Empty).Trim().ToLowerInvariant();

            if (text == "ja" || text == "jp" || text == "jpn" || text == "japanese" ||
                text == "日本語" || text == "日语" || text == "日語")
            {
                return UiLanguage.Japanese;
            }

            return UiLanguage.Chinese;
        }

        private static List<JapanesePattern> GetPatterns()
        {
            lock (PatternSyncRoot)
            {
                if (_patterns != null)
                {
                    return _patterns;
                }

                List<KeyValuePair<string, string>> entries =
                    new List<KeyValuePair<string, string>>(JapaneseTexts);

                // 固定部分越长的模板越先试，避免「输出根目录：{0}」抢掉更具体的句子
                entries.Sort(delegate (KeyValuePair<string, string> left, KeyValuePair<string, string> right)
                {
                    return right.Key.Length.CompareTo(left.Key.Length);
                });

                List<JapanesePattern> patterns = new List<JapanesePattern>();

                for (int index = 0; index < entries.Count; index++)
                {
                    JapanesePattern pattern = CreatePattern(entries[index].Key, entries[index].Value);

                    if (pattern != null)
                    {
                        patterns.Add(pattern);
                    }
                }

                _patterns = patterns;
                return _patterns;
            }
        }

        /// <summary>
        /// 把「中文模板（含 {0} {1} … {10} …）」编译成正则：固定部分按原文转义，占位符按 (.+?) 抓取。
        /// 不含占位符的条目返回 null（那种直接用完全一致查表即可）。
        /// 占位符按出现顺序从 {0} 开始连续编号（可两位数，如 {10}）。
        /// </summary>
        private static JapanesePattern CreatePattern(string chineseTemplate, string japaneseTemplate)
        {
            StringBuilder expression = new StringBuilder("^");
            int index = 0;
            int group = 0;

            while (index < chineseTemplate.Length)
            {
                int open = chineseTemplate.IndexOf('{', index);

                if (open < 0)
                {
                    expression.Append(Regex.Escape(chineseTemplate.Substring(index)));
                    break;
                }

                // 只认 {数字} 形式的占位符（数字可以多位）；其它（没有闭合 / 非数字）按普通文字处理
                int close = open + 1;

                while (close < chineseTemplate.Length && char.IsDigit(chineseTemplate[close]))
                {
                    close++;
                }

                if (close == open + 1 || close >= chineseTemplate.Length || chineseTemplate[close] != '}')
                {
                    expression.Append(Regex.Escape(chineseTemplate.Substring(index, open - index + 1)));
                    index = open + 1;
                    continue;
                }

                expression.Append(Regex.Escape(chineseTemplate.Substring(index, open - index)));
                expression.Append("(.+?)");
                group++;
                index = close + 1;
            }

            if (group == 0)
            {
                return null;
            }

            expression.Append("$");

            JapanesePattern pattern = new JapanesePattern();
            pattern.Expression = new Regex(expression.ToString(),
                RegexOptions.Singleline | RegexOptions.CultureInvariant);
            pattern.JapaneseTemplate = japaneseTemplate;

            return pattern;
        }

        /// <summary>「中文模板（含占位符）→ 日语模板」的正则缓存项。</summary>
        private sealed class JapanesePattern
        {
            public Regex Expression { get; set; }

            public string JapaneseTemplate { get; set; }
        }
    }
}
