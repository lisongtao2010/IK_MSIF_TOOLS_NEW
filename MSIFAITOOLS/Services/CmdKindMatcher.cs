using System;
using System.Collections.Generic;
using System.Text;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// 【CMD 分析】「品種」比对的公共实现：第 3 步「要領図部品追加」
    /// （<see cref="CmdInstructionPartCollector"/>）与第 4 步「特注品.json 部品追加」
    /// （<see cref="CmdTzPartCollector"/>）共用同一套判定。
    ///
    /// <para>
    /// 比对的一边是清单 <c>classify\PdfInfoList.txt</c> 的「品種」列原文
    /// （老清单没有这一列时是「分类依据」列）。它往往是一整段说明文字，同一张アッセンブリ図会覆盖
    /// 好几条品種，写法也很随意：
    /// </para>
    /// <list type="bullet">
    ///   <item><c>H310 １７ラシッサ V引違戸2枚建枠 Wモーション アッセンブリ図</c>
    ///         → <c>V引違戸2枚建</c> / <c>引違戸2枚建</c> / <c>引違戸2枚建枠</c>；</item>
    ///   <item><c>H310 V片引戸/2/3枚建/V引分戸枠 アッセンブリ図</c>
    ///         → <c>片引戸2枚建</c> / <c>片引戸3枚建</c> / <c>V片引戸2枚建</c> / <c>V片引戸3枚建</c> /
    ///         <c>引分戸</c> / <c>V引分戸</c> / <c>引分戸枠</c>；</item>
    ///   <item><c>H310 V片引戸2枚建V片引戸3枚建 アッセンブリ図</c>
    ///         → <c>片引戸2枚建</c> / <c>片引戸3枚建</c> / <c>V片引戸2枚建</c> / <c>V片引戸3枚建</c>；</item>
    ///   <item><c>文件名 H527U10001_枠 片引戸標準(在来) アッセンブリ図</c>
    ///         → <c>片引戸標準</c> / <c>片引戸標準(在来)</c>。</item>
    /// </list>
    /// <para>
    /// 所以判定口径是「<b>品種原文里出现过的品種都算命中</b>」：把原文归一化（全角转半角、去空白、
    /// 转大写）后，在下面这些文字里找品種：
    /// </para>
    /// <list type="number">
    ///   <item>每个单词（按空白 / 标点切）；</item>
    ///   <item>相邻两个单词拼起来的（<c>V片引戸</c> + <c>2枚建</c>）；</item>
    ///   <item>「数量组合」展开出来的（<c>V片引戸/2/3枚建</c> → <c>V片引戸2枚建</c> + <c>V片引戸3枚建</c>）；</item>
    ///   <item>整段原文压平后的（词里没有分隔符的 <c>V片引戸2枚建V片引戸3枚建</c>）。</item>
    /// </list>
    /// <para>
    /// <b>唯一例外</b>：短品種后面<b>紧跟数字</b>的「泛称」不算命中 —— 原文里有「片引戸2枚建」时，
    /// 泛称「片引戸」不算命中（否则 2枚建 / 3枚建 的図会把所有「片引戸」的行都吞进来）。
    /// 「V」前缀、「枠」后缀、括弧里的写法差别都不算泛称，长短两条<b>都</b>算命中
    /// （<c>V引違戸2枚建</c> と <c>引違戸2枚建</c> / <c>引違戸2枚建枠</c>、
    /// <c>片引戸標準</c> と <c>片引戸標準(在来)</c>）。
    /// </para>
    /// </summary>
    internal static class CmdKindMatcher
    {
        /// <summary>分词用的分隔符（与两个 collector 读清单时用的同一套）。</summary>
        private static readonly char[] WordSeparators = new char[]
        {
            ' ', '\u3000', '\t', '\r', '\n', ',', '、', '，', '/', '／', '・', '|', '｜',
            ':', '：', ';', '；', '(', ')', '（', '）', '「', '」', '【', '】', '［', '］'
        };

        /// <summary>
        /// 「数量组合」里认的单位（<c>V片引戸/2/3枚建</c> 的「枚建」）。长的写在前面。
        /// </summary>
        private static readonly string[] CountUnits = new string[] { "枚建", "枚引", "枚" };

        /// <summary>
        /// 求「品種原文里出现了哪些品種」。
        /// <para>返回的集合用 <see cref="StringComparer.OrdinalIgnoreCase"/> 比较，元素是调用方给的原文
        /// （<paramref name="kindKeys"/> 里的字符串，两个 collector 传的都是 <see cref="NormalizeKey"/>
        /// 归一化后的品種，所以直接 <c>Contains</c> 就能判断）。</para>
        /// </summary>
        /// <param name="kindKeys">要比对的品種（明细文件 / 特注品 里出现过的全部品種）。</param>
        /// <param name="kindText">アッセンブリ図 的品種原文（清单「品種」列；老清单为「分类依据」）。</param>
        public static HashSet<string> ResolveMatchedKinds(IEnumerable<string> kindKeys, string kindText)
        {
            HashSet<string> matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> kinds = new List<string>();
            List<string> originals = new List<string>();

            if (kindKeys != null)
            {
                foreach (string kindKey in kindKeys)
                {
                    string key = NormalizeKey(kindKey);

                    if (key.Length == 0 || kinds.Contains(key))
                    {
                        continue;
                    }

                    kinds.Add(key);
                    originals.Add(kindKey);
                }
            }

            if (kinds.Count == 0)
            {
                return matched;
            }

            List<string> sources = BuildSources(kindText);

            if (sources.Count == 0)
            {
                return matched;
            }

            // ① 每段文字里出现的品種都收进来（同一品種只收一次）
            HashSet<string> hitSet = new HashSet<string>(StringComparer.Ordinal);
            List<string> hits = new List<string>();

            foreach (string source in sources)
            {
                foreach (string kind in kinds)
                {
                    if (hitSet.Contains(kind))
                    {
                        continue;
                    }

                    if (source.IndexOf(kind, StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }

                    hitSet.Add(kind);
                    hits.Add(kind);
                }
            }

            // ② 去掉「后面紧跟数字」的泛称（「片引戸2枚建」已命中时「片引戸」不算）
            foreach (string hit in hits)
            {
                if (IsGenericPrefixOfCountVariation(hit, hits))
                {
                    continue;
                }

                matched.Add(originals[kinds.IndexOf(hit)]);
            }

            return matched;
        }

        /// <summary>
        /// 品種原文里有没有「整词完全一致」的品種（只用于日志措辞：一个都没有 = 按包含比较命中的）。
        /// </summary>
        public static bool AnyExactTokenHit(IEnumerable<string> kindKeys, string kindText)
        {
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);

            if (kindKeys != null)
            {
                foreach (string kindKey in kindKeys)
                {
                    string key = NormalizeKey(kindKey);

                    if (key.Length > 0)
                    {
                        keys.Add(key);
                    }
                }
            }

            if (keys.Count == 0)
            {
                return false;
            }

            foreach (string token in Tokenize(kindText))
            {
                if (keys.Contains(NormalizeKey(token)))
                {
                    return true;
                }
            }

            return false;
        }

        // ── 比对用的「文字池」 ───────────────────────────────────────────────

        /// <summary>
        /// 把品種原文变成一串「要比对的文字」：单词、相邻两词拼接、数量组合展开、整段压平。
        /// 结果已归一化（<see cref="NormalizeKey"/>）并去重。
        /// </summary>
        private static List<string> BuildSources(string kindText)
        {
            List<string> sources = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<string> tokens = new List<string>();

            foreach (string token in Tokenize(kindText))
            {
                string key = NormalizeKey(token);

                if (key.Length == 0)
                {
                    continue;
                }

                tokens.Add(key);
                AddSource(sources, seen, key);
            }

            // 相邻两个单词拼起来（「V片引戸」+「2枚建」、标点把品種切开时用）
            for (int index = 0; index + 1 < tokens.Count; index++)
            {
                AddSource(sources, seen, tokens[index] + tokens[index + 1]);
            }

            // 「数量组合」展开（「V片引戸/2/3枚建」→「V片引戸2枚建」「V片引戸3枚建」）
            for (int index = 0; index < tokens.Count; index++)
            {
                AddCountExpansions(tokens, index, sources, seen);
            }

            // 整段原文压平（品種与品種之间没有分隔符的写法）
            AddSource(sources, seen, NormalizeKey(kindText));

            return sources;
        }

        /// <summary>加一段要比对的文字（空串 / 重复的跳过）。</summary>
        private static void AddSource(IList<string> sources, ISet<string> seen, string text)
        {
            if (string.IsNullOrEmpty(text) || !seen.Add(text))
            {
                return;
            }

            sources.Add(text);
        }

        /// <summary>
        /// 「数量组合」展开：把 <c>基准词 + 数字…+ N枚建</c> 的组合重新拼成品種。
        /// <para>
        /// 例 ①：<c>V片引戸 / 2 / 3枚建</c> → 基准词 <c>V片引戸</c>、数字 <c>2</c>（独立的词）与
        /// <c>3</c>（<c>3枚建</c> 的头）→ 生成 <c>V片引戸2枚建</c>、<c>V片引戸3枚建</c>。
        /// </para>
        /// <para>
        /// 例 ②：<c>V片引戸2枚建 / 3枚建</c> → 基准词自带「2枚建」，先把「2」与「枚建」拆开当基准，
        /// 再与后面的 <c>3</c> 组合 → 生成 <c>V片引戸2枚建</c>、<c>V片引戸3枚建</c>。
        /// </para>
        /// </summary>
        /// <param name="tokens">已归一化的单词。</param>
        /// <param name="index">当前当作「基准词」的单词下标。</param>
        /// <param name="sources">加进去的文字池。</param>
        /// <param name="seen">文字池的去重集合。</param>
        private static void AddCountExpansions(IList<string> tokens, int index, IList<string> sources,
            ISet<string> seen)
        {
            string token = tokens[index] ?? string.Empty;

            if (token.Length == 0)
            {
                return;
            }

            List<string> numbers = new List<string>();
            string baseText = token;
            string unitText = string.Empty;

            // ① 词本身已带「N枚建」（「V片引戸2枚建 / 3枚建」）：把 N 与单位拆出来当基准
            foreach (string unit in CountUnits)
            {
                if (!token.EndsWith(unit, StringComparison.Ordinal))
                {
                    continue;
                }

                int unitStart = token.Length - unit.Length;
                int digits = TrailingDigitCount(token.Substring(0, unitStart));

                if (digits == 0 || digits >= unitStart)
                {
                    continue;
                }

                numbers.Add(token.Substring(unitStart - digits, digits));
                baseText = token.Substring(0, unitStart - digits);
                unitText = unit;

                break;
            }

            if (unitText.Length == 0)
            {
                // ② 词尾带数字（「V片引戸2」）：数字算进号码表，词干当基准
                int trailingDigits = TrailingDigitCount(token);

                if (trailingDigits >= token.Length)
                {
                    // 整个词就是数字（「2」）：由前一个词当基准，这里不处理
                    return;
                }

                if (trailingDigits > 0)
                {
                    numbers.Add(token.Substring(token.Length - trailingDigits));
                    baseText = token.Substring(0, token.Length - trailingDigits);
                }
            }

            // 基准词不能空、不能是纯数字、也不能自己就带「N枚建」（这种由前一个词负责）
            if (baseText.Length == 0 || IsDigits(baseText) || HasCountSuffix(baseText))
            {
                return;
            }

            int next = index + 1;

            while (next < tokens.Count && IsDigits(tokens[next]))
            {
                numbers.Add(tokens[next]);
                next++;
            }

            if (next < tokens.Count)
            {
                string unitToken = tokens[next] ?? string.Empty;
                int leadingDigits = LeadingDigitCount(unitToken);
                string tail = leadingDigits > 0 && leadingDigits < unitToken.Length
                    ? unitToken.Substring(leadingDigits)
                    : unitToken;

                if (leadingDigits < unitToken.Length && IsCountUnit(tail))
                {
                    unitText = tail;

                    if (leadingDigits > 0)
                    {
                        numbers.Add(unitToken.Substring(0, leadingDigits));
                    }
                }
            }

            if (unitText.Length == 0 || numbers.Count == 0)
            {
                return;
            }

            foreach (string number in numbers)
            {
                AddSource(sources, seen, baseText + number + unitText);
            }
        }

        // ── 判定 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// <paramref name="kind"/> 是不是「后面紧跟数字」的泛称：命中集合里有更长的品種，
        /// 其中的 <paramref name="kind"/> 之后紧接数字（<c>片引戸</c> → <c>片引戸2枚建</c>）。
        /// 是的话这个短品種不算命中（否则 2枚建 / 3枚建 的図会把泛称品種的行全吞进来）。
        /// <para>
        /// 「V」前缀（<c>V引違戸2枚建</c>）、「枠」后缀（<c>引違戸2枚建枠</c>）、
        /// 括弧（<c>片引戸標準(在来)</c>）都不算泛称，长短两条都保留。
        /// </para>
        /// </summary>
        /// <param name="kind">要判断的品種（已归一化）。</param>
        /// <param name="hits">原文里出现过的全部品種（已归一化）。</param>
        private static bool IsGenericPrefixOfCountVariation(string kind, IList<string> hits)
        {
            if (string.IsNullOrEmpty(kind) || hits == null)
            {
                return false;
            }

            foreach (string other in hits)
            {
                if (other == null || other.Length <= kind.Length)
                {
                    continue;
                }

                int index = other.IndexOf(kind, StringComparison.Ordinal);

                while (index >= 0)
                {
                    int after = index + kind.Length;

                    if (after < other.Length && IsDigit(other[after]))
                    {
                        return true;
                    }

                    index = other.IndexOf(kind, index + 1, StringComparison.Ordinal);
                }
            }

            return false;
        }

        // ── 小工具 ───────────────────────────────────────────────────────────

        /// <summary>把品種原文按空白与常见标点切成单词（去掉空串）。</summary>
        private static List<string> Tokenize(string text)
        {
            List<string> tokens = new List<string>();

            if (string.IsNullOrEmpty(text))
            {
                return tokens;
            }

            string[] parts = text.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);

            foreach (string part in parts)
            {
                string token = part.Trim();

                if (token.Length > 0)
                {
                    tokens.Add(token);
                }
            }

            return tokens;
        }

        /// <summary>是不是「数量组合」的单位（<c>枚建</c> / <c>枚引</c> / <c>枚…</c>）。</summary>
        private static bool IsCountUnit(string unitText)
        {
            if (string.IsNullOrEmpty(unitText))
            {
                return false;
            }

            foreach (string unit in CountUnits)
            {
                if (unitText.StartsWith(unit, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>词本身是不是「…N枚建」的形式（<c>V片引戸2枚建</c>）。</summary>
        private static bool HasCountSuffix(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (string unit in CountUnits)
            {
                if (!text.EndsWith(unit, StringComparison.Ordinal))
                {
                    continue;
                }

                int unitStart = text.Length - unit.Length;

                if (unitStart > 0 && TrailingDigitCount(text.Substring(0, unitStart)) > 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>整个词是不是数字。</summary>
        private static bool IsDigits(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (char ch in text)
            {
                if (!IsDigit(ch))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>词头连续几个数字。</summary>
        private static int LeadingDigitCount(string text)
        {
            int count = 0;

            if (string.IsNullOrEmpty(text))
            {
                return count;
            }

            while (count < text.Length && IsDigit(text[count]))
            {
                count++;
            }

            return count;
        }

        /// <summary>词尾连续几个数字。</summary>
        private static int TrailingDigitCount(string text)
        {
            int count = 0;

            if (string.IsNullOrEmpty(text))
            {
                return count;
            }

            while (count < text.Length && IsDigit(text[text.Length - 1 - count]))
            {
                count++;
            }

            return count;
        }

        /// <summary>半角数字（归一化后全角数字已经转成半角）。</summary>
        private static bool IsDigit(char value)
        {
            return value >= '0' && value <= '9';
        }

        /// <summary>
        /// 匹配用的归一化：全角 ASCII（含全角数字 / 字母 / 括弧）转半角，去掉空白，统一大写。
        /// 与两个 collector 里的 <c>NormalizeKey</c> 同一套规则（三者都保留一份，避免改动互相牵连：
        /// 本类的这份只负责品種比对）。
        /// </summary>
        internal static string NormalizeKey(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(value.Length);

            foreach (char ch in value)
            {
                char current = ch;

                if (ch >= '\uFF01' && ch <= '\uFF5E')
                {
                    current = (char)(ch - 0xFEE0);
                }
                else if (ch == '\u3000')
                {
                    current = ' ';
                }

                if (current == ' ' || current == '\t' || current == '\r' || current == '\n')
                {
                    continue;
                }

                builder.Append(current);
            }

            return builder.ToString().ToUpperInvariant();
        }
    }
}
