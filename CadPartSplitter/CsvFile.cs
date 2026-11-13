using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CadPartSplitter
{
    /// <summary>
    /// 项目内共用的 CSV 工具（RFC 4180）：读、写、字段转义，外加表头 / 空行的归一化判定。
    ///
    /// <c>tables\table_NN.csv</c>、<c>01_アッセンブリ図_部品機能情報.csv</c>、<c>02_要領図_部品機能情報.csv</c>
    /// 都由这里读写，编码风格统一：写出一律是「带 BOM 的 UTF-8 + CRLF」（Excel 双击打开日文 / 中文不乱码），
    /// 读入时 BOM 自动丢弃、CRLF / LF 都收、引号内的逗号照原样保留。
    ///
    /// **一格一行**：写出一律先把字段内的换行压成 <see cref="LineBreakPlaceholder"/>（`~`）——
    /// RFC 4180 允许引号内换行，但 Excel 以及任何按行读 CSV 的下游脚本遇到它都会串行，
    /// 所以「一段文字里有换行」的格子只留一个 `~` 当断行记号。
    /// </summary>
    internal static class CsvFile
    {
        /// <summary>
        /// 写在 CSV 里代替换行的字符：单元格内的换行（CRLF / CR / LF）压成它，
        /// 保证「一条记录只占一行」，Excel 与按行读 CSV 的下游脚本都不会串行。
        /// </summary>
        public const char LineBreakPlaceholder = '~';

        /// <summary>
        /// 把文本里的换行（CRLF / CR / LF）压成 <see cref="LineBreakPlaceholder"/>（`~`）：
        /// 一个单元格必须留在一行里，否则 CSV 会串行、<c>tables_text.txt</c> 的框线表也会被撑坏。
        /// 连续换行只留一个 `~`；行首 / 行尾的换行直接丢掉（不产生多余的 `~`）。
        /// </summary>
        public static string SingleLine(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;

            if (s.IndexOf('\r') < 0 && s.IndexOf('\n') < 0)
                return s;

            var sb = new StringBuilder(s.Length);
            bool pendingBreak = false;

            foreach (char ch in s)
            {
                if (ch == '\r' || ch == '\n')
                {
                    pendingBreak = true;
                    continue;
                }

                if (pendingBreak)
                {
                    pendingBreak = false;

                    if (sb.Length > 0)
                        sb.Append(LineBreakPlaceholder);   // 行首的换行不补 '~'
                }

                sb.Append(ch);
            }

            return sb.ToString();
        }

        /// <summary>
        /// CSV 字段转义：含逗号 / 引号的字段加双引号，内部引号翻倍（RFC 4180）；
        /// 字段内的换行先由 <see cref="SingleLine"/> 压成 `~`（一格一行，避免串行）。
        /// </summary>
        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;

            string flat = SingleLine(s);

            if (flat.IndexOf(',') < 0 && flat.IndexOf('"') < 0)
                return flat;

            return "\"" + flat.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>表头 / 单元格归一化：去掉半角 / 全角空格与换行（PDF 里的表头常被拆成两行）。</summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;

            return s.Replace(" ", string.Empty)
                    .Replace("\u3000", string.Empty)
                    .Replace("\r", string.Empty)
                    .Replace("\n", string.Empty)
                    .Trim();
        }

        /// <summary>
        /// 「同一个字被画了两遍」的还原：CAD 导出的 PDF 偶尔把文字**重复放置一次**（两个字形框几乎重合），
        /// 文字层落进 CSV 就成了 <c>部部品品ココーードド</c> —— 表头因此认不出「部品コード」这一列，
        /// 02 的 <c>部品コード</c> 整列空（H467U10008 就是）。
        ///
        /// 判据（要求整格都满足，宁可漏折也不误伤真值）：
        ///   ① 去掉半角 / 全角空格与换行后长度 ≥ 4、且为偶数；
        ///   ② 每两位相同（<c>部部</c> / <c>品品</c> / <c>ーー</c> …）；
        ///   ③ 不是「交替重复」（<c>ＡＢＡＢ</c> 这种真值凑巧每两位相同，但 <c>s[0] == s[2]</c>）——
        ///      真双写时 <c>s[0]</c> 与 <c>s[2]</c> 是两个不同的字（<c>部</c> / <c>品</c>）。
        /// 折半后仍满足再折一次（「写三遍」也能还原）。
        ///
        /// 不满足任何一条时**原样返回**（连空格都不动），所以 <c>MZC○○63</c>（长度 7）、
        /// <c>TI-3086</c>、<c>2H100</c> 这些带重复字 / 数字的真品番不会被改。
        /// </summary>
        public static string FoldDouble(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;

            string flat = Normalize(s);     // 去掉半角 / 全角空格与换行，"部 部 品 品" 也能判

            if (flat.Length < 4 || flat.Length % 2 != 0)
                return s;

            for (int i = 0; i < flat.Length; i += 2)
            {
                if (flat[i] != flat[i + 1])
                    return s;
            }

            // 「ＡＢＡＢ」这类交替重复不是「每字写两遍」（双写时 s[0] 与 s[2] 不同），别动它
            if (flat[0] == flat[2])
                return s;

            var sb = new StringBuilder(flat.Length / 2);

            for (int i = 0; i < flat.Length; i += 2)
                sb.Append(flat[i]);

            string folded = sb.ToString();

            return folded.Length < flat.Length ? FoldDouble(folded) : s;
        }

        /// <summary>
        /// 整张表逐格过一遍 <see cref="FoldDouble"/>（文字重叠绘制的兜底），
        /// 顺手统计折了几格、第一例长什么样 —— 调用方记进日志，跑完就知道这张图有没有这个毛病。
        /// 就地改 <paramref name="rows"/> 里的格子，返回同一个列表。
        /// </summary>
        /// <param name="rows">一张 CSV 的全部行。</param>
        /// <param name="folded">输出：被还原的格子数（0 = 这张表没这个问题）。</param>
        /// <param name="sample">输出：第一例的「原样」→「还原后」，没有则为 null。</param>
        public static List<string[]> FoldDoubleRows(List<string[]> rows, out int folded, out string sample)
        {
            folded = 0;
            sample = null;

            if (rows == null)
                return null;

            for (int r = 0; r < rows.Count; r++)
            {
                string[] row = rows[r];

                if (row == null)
                    continue;

                for (int c = 0; c < row.Length; c++)
                {
                    string cell = row[c];

                    if (string.IsNullOrEmpty(cell))
                        continue;

                    string clean = FoldDouble(cell);

                    if (string.Equals(clean, cell, StringComparison.Ordinal))
                        continue;

                    row[c] = clean;
                    folded++;

                    if (sample == null)
                        sample = "「" + cell + "」→「" + clean + "」";
                }
            }

            return rows;
        }

        /// <summary>整行都是空白（尾部空行、Excel 留下的空行、合并单元格造成的空行）。</summary>
        public static bool IsEmptyRow(string[] row)
        {
            if (row == null)
                return true;

            foreach (string cell in row)
            {
                if (!string.IsNullOrEmpty(Normalize(cell)))
                    return false;
            }

            return true;
        }

        /// <summary>表头里找某个列名（归一化后「包含」即算命中），返回列号，-1 表示没有。</summary>
        public static int IndexOfHeader(string[] header, string keyword)
        {
            if (header == null || string.IsNullOrEmpty(keyword))
                return -1;

            string key = Normalize(keyword);

            for (int c = 0; c < header.Length; c++)
            {
                if (Normalize(header[c]).IndexOf(key, StringComparison.Ordinal) >= 0)
                    return c;
            }

            return -1;
        }

        /// <summary>
        /// RFC 4180 读取：逗号分隔、双引号包裹的字段内可含逗号 / 换行、内部引号用两个引号转义；
        /// BOM（如果有）由 <see cref="File.ReadAllText(string, Encoding)"/> 自动吃掉。
        /// </summary>
        public static List<string[]> Read(string path)
        {
            var rows = new List<string[]>();
            string text = File.ReadAllText(path, Encoding.UTF8);

            if (text.Length == 0)
                return rows;

            var row = new List<string>();
            var field = new StringBuilder();
            bool inQuotes = false;
            int i = 0;

            while (i < text.Length)
            {
                char ch = text[i];

                if (inQuotes)
                {
                    if (ch == '\"')
                    {
                        // "" → 一个引号；单个 " → 引号段结束
                        if (i + 1 < text.Length && text[i + 1] == '\"')
                        {
                            field.Append('\"');
                            i += 2;
                            continue;
                        }

                        inQuotes = false;
                        i++;
                        continue;
                    }

                    field.Append(ch);
                    i++;
                    continue;
                }

                switch (ch)
                {
                    case '\"':
                        inQuotes = true;
                        i++;
                        break;

                    case ',':
                        row.Add(field.ToString());
                        field.Length = 0;
                        i++;
                        break;

                    case '\r':
                        i++;    // CRLF / 单独的 CR 都当行尾（引号内的 CR 已在前面的分支保留）
                        break;

                    case '\n':
                        row.Add(field.ToString());
                        field.Length = 0;
                        rows.Add(row.ToArray());
                        row.Clear();
                        i++;
                        break;

                    default:
                        field.Append(ch);
                        i++;
                        break;
                }
            }

            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                rows.Add(row.ToArray());
            }

            return rows;
        }

        /// <summary>
        /// 写出 CSV：<paramref name="header"/> 非空则先写表头，然后写 <paramref name="rows"/>；
        /// 编码为带 BOM 的 UTF-8 + CRLF（与 <c>tables\*.csv</c> 一致）。
        /// </summary>
        public static void Write(string path, string[] header, IList<string[]> rows)
        {
            var sb = new StringBuilder();

            if (header != null && header.Length > 0)
                AppendLine(sb, header);

            if (rows != null)
            {
                for (int r = 0; r < rows.Count; r++)
                    AppendLine(sb, rows[r]);
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        /// <summary>追加一行：字段逐个经 <see cref="Escape"/>（含换行压平），行尾 CRLF。</summary>
        public static void AppendLine(StringBuilder sb, string[] row)
        {
            if (row == null)
                return;

            for (int c = 0; c < row.Length; c++)
            {
                if (c > 0)
                    sb.Append(',');

                sb.Append(Escape(row[c]));
            }

            sb.Append("\r\n");
        }
    }
}
