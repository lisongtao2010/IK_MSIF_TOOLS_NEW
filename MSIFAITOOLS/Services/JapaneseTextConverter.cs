using System;
using System.Collections.Generic;
using System.Text;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// 日文文本的宽度转换 / 兼容性归一化。
    ///
    /// <para>
    /// <b><see cref="ToHalfWidthKatakana"/></b>：全角片假名 → 半角片假名（另含<b>括号</b> <c>（）</c> → <c>()</c>）。
    /// 部品基本信息表「登录」（<see cref="PartBasicInfoRepository"/> 写库）时，
    /// 「部材名」「部品名」先走这里统一成半角片假名：
    /// 例 <c>上レール</c> → <c>上ﾚｰﾙ</c>、<c>カバー</c> → <c>ｶﾊﾞｰ</c>、
    /// <c>部品セット（フラットレール）</c> → <c>部品ｾｯﾄ(ﾌﾗｯﾄﾚｰﾙ)</c>。
    /// 濁音 / 半濁音写成「半角清音 + ﾞ / ﾟ」两个字符（<c>ガ</c> → <c>ｶﾞ</c>）。
    /// 平假名 / 汉字 / 英数 / 其它标点（「」。、）<b>原样保留</b>
    /// （只转片假名 + 长音「ー」+ 中点「・」+ 浊点 + 括号）。
    /// </para>
    ///
    /// <para>
    /// <b><see cref="NormalizeWidth"/></b>：NFKC 兼容性归一化
    /// （半角片假名 → 全角片假名、全角英数 → 半角）。
    /// 库里的值已经是半角片假名、而 PDF / Excel 原文多为全角时，<b>比较前两边各走一次这里</b>，
    /// 两种写法就当成同一个名字（见 <see cref="CmdPartAnalysisService"/> 的部品名规整、
    /// <see cref="MemberNameReplacementCatalog"/> 的置换表查找、<see cref="PartBasicInfoRepository.UpdateQuantityByPart"/>）。
    /// </para>
    ///
    /// <para>
    /// 两个方法都不抛异常：<c>null</c> / 空串原样返回；
    /// <see cref="NormalizeWidth"/> 遇到孤立代理项等非法序列时退回原文。
    /// </para>
    /// </summary>
    internal static class JapaneseTextConverter
    {
        /// <summary>
        /// 全角 → 半角 的对照表（片假名 + 长音 / 中点 / 浊点 + 括号）。
        /// 每个元素：<b>第 1 个字符 = 全角</b>，其余字符 = 半角写法（濁音 / 半濁音是「半角清音 + ﾞ / ﾟ」两个字符）。
        /// 没有半角写法的 ヰ / ヱ / ヽ / ヾ 不列在这里（原样保留）。
        /// </summary>
        private static readonly string[] FullToHalfWidthPairs = new string[]
        {
            // 清音 / 小字
            "ァｧ", "アｱ", "ィｨ", "イｲ", "ゥｩ", "ウｳ", "ェｪ", "エｴ", "ォｫ", "オｵ",
            "カｶ", "キｷ", "クｸ", "ケｹ", "コｺ",
            "サｻ", "シｼ", "スｽ", "セｾ", "ソｿ",
            "タﾀ", "チﾁ", "ツﾂ", "ッｯ", "テﾃ", "トﾄ",
            "ナﾅ", "ニﾆ", "ヌﾇ", "ネﾈ", "ノﾉ",
            "ハﾊ", "ヒﾋ", "フﾌ", "ヘﾍ", "ホﾎ",
            "マﾏ", "ミﾐ", "ムﾑ", "メﾒ", "モﾓ",
            "ヤﾔ", "ュｭ", "ユﾕ", "ョｮ", "ヨﾖ",
            "ラﾗ", "リﾘ", "ルﾙ", "レﾚ", "ロﾛ",
            "ヮﾜ", "ワﾜ", "ヲｦ", "ンﾝ", "ヵｶ", "ヶｹ",
            // 濁音
            "ガｶﾞ", "ギｷﾞ", "グｸﾞ", "ゲｹﾞ", "ゴｺﾞ",
            "ザｻﾞ", "ジｼﾞ", "ズｽﾞ", "ゼｾﾞ", "ゾｿﾞ",
            "ダﾀﾞ", "ヂﾁﾞ", "ヅﾂﾞ", "デﾃﾞ", "ドﾄﾞ",
            "バﾊﾞ", "ビﾋﾞ", "ブﾌﾞ", "ベﾍﾞ", "ボﾎﾞ",
            "ヴｳﾞ", "ヷﾜﾞ", "ヺｦﾞ",
            // 半濁音
            "パﾊﾟ", "ピﾋﾟ", "プﾌﾟ", "ペﾍﾟ", "ポﾎﾟ",
            // 片假名里的符号（长音 / 中点 / 单独的浊点 / 半浊点）
            "ーｰ", "・･", "゛ﾞ", "゜ﾟ",
            // 括号：全角 → 半角（部品名 常见写法「部品セット（フラットレール）」→「部品ｾｯﾄ(ﾌﾗｯﾄﾚｰﾙ)」）
            "（(", "）)"
        };

        /// <summary>全角 → 半角 的查表（键 = 全角字符，值 = 半角写法，1 或 2 个字符）。</summary>
        private static readonly Dictionary<char, string> FullToHalfWidthMap =
            BuildFullToHalfWidthMap();

        private static Dictionary<char, string> BuildFullToHalfWidthMap()
        {
            Dictionary<char, string> map = new Dictionary<char, string>(FullToHalfWidthPairs.Length);

            for (int index = 0; index < FullToHalfWidthPairs.Length; index++)
            {
                string pair = FullToHalfWidthPairs[index];

                if (pair == null || pair.Length < 2)
                {
                    continue;
                }

                map[pair[0]] = pair.Substring(1);
            }

            return map;
        }

        /// <summary>
        /// 全角片假名 → 半角片假名（清音 / 小字 / 濁音 / 半濁音 / 长音 ー / 中点 ・），
        /// 外加全角括号 <c>（</c> <c>）</c> → <c>(</c> <c>)</c>（名称里的括号也跟着变半角）。
        /// 其它字符（平假名 / 汉字 / 英数 / 「」。、 等标点、空白）原样保留。
        /// </summary>
        /// <param name="text">要转换的文本（<c>null</c> / 空串原样返回）。</param>
        /// <returns>转换后的文本；没有需要转换的字符时返回<b>同一个实例</b>。</returns>
        public static string ToHalfWidthKatakana(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            StringBuilder builder = null;

            for (int index = 0; index < text.Length; index++)
            {
                char current = text[index];
                string half;

                if (!FullToHalfWidthMap.TryGetValue(current, out half))
                {
                    if (builder != null)
                    {
                        builder.Append(current);
                    }

                    continue;
                }

                if (builder == null)
                {
                    // 第一次命中才建 StringBuilder，把前面没变的字符一次带上
                    builder = new StringBuilder(text.Length + 8);
                    builder.Append(text, 0, index);
                }

                builder.Append(half);
            }

            return builder == null ? text : builder.ToString();
        }

        /// <summary>
        /// 兼容性归一化（Unicode NFKC）：半角片假名 → 全角片假名、全角英数 → 半角英数。
        /// 比较「库里已转成半角片假名的值」和「PDF / Excel 原文（多为全角）」时用。
        /// </summary>
        /// <param name="text">要归一化的文本（<c>null</c> / 空串原样返回）。</param>
        /// <returns>归一化后的文本；非法序列（孤立代理项等）时返回原文。</returns>
        public static string NormalizeWidth(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            try
            {
                return text.Normalize(NormalizationForm.FormKC);
            }
            catch (ArgumentException)
            {
                // 孤立代理项等非法序列：退回原文（至少不影响其它比较规则）
                return text;
            }
        }
    }
}
