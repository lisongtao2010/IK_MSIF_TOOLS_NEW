using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ExcelDataReader;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// 一个 Excel 文件的「整表原文」：把<b>所有工作表的所有行</b>按
    /// 「工作表名 + 行号 + 单元格原文」导出成纯文本，交给 AI 自己看懂排版。
    /// 由 <see cref="ExcelSheetDumper.Read"/> 生成，供 <see cref="CmdTzJsonMaintainer"/> 拼提示词。
    /// </summary>
    internal sealed class ExcelSheetDumpResult
    {
        public ExcelSheetDumpResult()
        {
            SheetNames = new List<string>();
            Text = string.Empty;
        }

        /// <summary>
        /// 整表原文。每张表一段：
        /// <c>### 工作表「カメラ」</c>，然后每行 <c>行12: 品種 | 部品名 | …</c>。
        /// </summary>
        public string Text { get; set; }

        /// <summary>读到的工作表张数。</summary>
        public int SheetCount { get; set; }

        /// <summary>实际写进 <see cref="Text"/> 的非空行数。</summary>
        public int RowCount { get; set; }

        /// <summary>是否因为字符 / 工作表张数上限截断了后面的内容。</summary>
        public bool Truncated { get; set; }

        /// <summary>工作表名（按 Excel 里的顺序）。</summary>
        public List<string> SheetNames { get; private set; }

        /// <summary>是否读到了可以送给 AI 的文字。</summary>
        public bool HasText
        {
            get { return RowCount > 0; }
        }
    }

    /// <summary>
    /// Excel 整表原文导出器（<c>.xls</c> / <c>.xlsx</c> / <c>.xlsm</c> 都读）。
    ///
    /// <para>
    /// <b>为什么要有它</b>：现场 Excel 的排版千差万别（部品表可能在任意一张工作表、任意位置，
    /// 表头多行 / 合并单元格 / 竖排说明都有），靠「按列名找表头」的规则永远会漏。
    /// 所以第 0 步把<b>整份文件的文字原样</b>发给 AI，让 AI 自己认出部品表和每一列；
    /// 规则解析只作为「AI 不可用时」的兜底。
    /// </para>
    ///
    /// <para>
    /// <b>格式约定</b>（AI 侧按这个读，见 <c>Prompts\TzJsonMaintain.Prompt.txt</c>）：
    /// 每张表一行抬头 <c>### 工作表「表名」</c>；数据行 <c>行N: 单元格 | 单元格 | …</c>，
    /// 单元格保持 Excel 里的左右顺序（空格子留空，只去掉行尾的空格子），
    /// 每个单元格内的换行压成空格、竖线换成全角 <c>｜</c>、超长截断。
    /// </para>
    /// </summary>
    internal static class ExcelSheetDumper
    {
        /// <summary>送给 AI 的整表文本字符上限（超出截断，避免提示词过长 / 请求超时）。</summary>
        public const int MaxTotalChars = 30000;

        /// <summary>单行文本最多多少字符。</summary>
        private const int MaxLineChars = 600;

        /// <summary>一个单元格最多多少字符。</summary>
        private const int MaxCellChars = 80;

        /// <summary>一张表最多取多少列（防止大片空白列把行拉得很长）。</summary>
        private const int MaxColumnCount = 40;

        /// <summary>一次最多导出多少张工作表（超出部分不上送，只记日志）。</summary>
        private const int MaxSheetCount = 12;

        /// <summary>
        /// 单张工作表最多导出多少字符（保证多张表时每张都有机会出现在提示词里：
        /// 否则第一张大表就能把 <see cref="MaxTotalChars"/> 吃光，部品表在后面那张就看不到了）。
        /// </summary>
        private const int MaxSheetChars = 8000;

        /// <summary>
        /// 读出一份 Excel 的整表原文。读不动时返回空结果并把原因写进 <paramref name="notes"/>，
        /// <b>不抛异常</b>（调用方还要用规则解析的结果兜底）。
        /// </summary>
        /// <param name="filePath">Excel 文件绝对路径。</param>
        /// <param name="notes">提示收集器（可为 null）。</param>
        public static ExcelSheetDumpResult Read(string filePath, IList<string> notes)
        {
            ExcelSheetDumpResult dump = new ExcelSheetDumpResult();

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                AddNote(notes, "整表读取：找不到文件（" + (filePath ?? "空") + "）");

                return dump;
            }

            try
            {
                StringBuilder builder = new StringBuilder(4096);
                int sheetIndex = 0;
                bool moreSheets = false;

                using (FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite))
                using (IExcelDataReader reader = ExcelReaderFactory.CreateReader(stream,
                    CreateConfiguration()))
                {
                    do
                    {
                        sheetIndex++;

                        string sheetName = ReadSheetName(reader, sheetIndex);
                        int rowsBefore = dump.RowCount;

                        dump.SheetNames.Add(sheetName);
                        dump.SheetCount++;

                        builder.Append("### 工作表「").Append(sheetName).Append("」\n");

                        int rowIndex = 0;
                        int sheetChars = 0;
                        bool sheetCut = false;

                        while (reader.Read())
                        {
                            rowIndex++;

                            string line = BuildRowLine(reader, rowIndex);

                            if (line.Length == 0)
                            {
                                // 整行空白（Excel 里常见的空行）：行号照样前进，但不发给 AI
                                continue;
                            }

                            // 单表配额：大表不会把后面工作表的位置挤掉
                            if (sheetChars + line.Length + 1 > MaxSheetChars)
                            {
                                sheetCut = true;

                                break;
                            }

                            if (builder.Length + line.Length + 1 > MaxTotalChars)
                            {
                                dump.Truncated = true;

                                break;
                            }

                            builder.Append(line).Append('\n');
                            sheetChars += line.Length + 1;
                            dump.RowCount++;
                        }

                        if (sheetCut)
                        {
                            builder.Append("（这张表后面的内容没发出去：单表上限 ")
                                .Append(MaxSheetChars.ToString(CultureInfo.InvariantCulture))
                                .Append(" 字符）\n");
                        }

                        if (dump.RowCount == rowsBefore)
                        {
                            builder.Append("（这张表没有可读的文字）\n");
                        }

                        if (dump.Truncated)
                        {
                            builder.Append("（后面还有内容没发出去：已到 ")
                                .Append(MaxTotalChars.ToString(CultureInfo.InvariantCulture))
                                .Append(" 字符上限）\n");

                            break;
                        }

                        if (dump.SheetCount >= MaxSheetCount)
                        {
                            moreSheets = true;

                            break;
                        }
                    }
                    while (reader.NextResult());
                }

                if (moreSheets)
                {
                    dump.Truncated = true;

                    AddNote(notes, "整表读取：工作表超过 " +
                        MaxSheetCount.ToString(CultureInfo.InvariantCulture) + " 张，只发了前 " +
                        MaxSheetCount.ToString(CultureInfo.InvariantCulture) + " 张");
                }

                dump.Text = builder.ToString().Trim();
            }
            catch (Exception ex)
            {
                AddNote(notes, "整表读取失败（" + ex.GetType().Name + "：" + ex.Message + "）");

                dump = new ExcelSheetDumpResult();
            }

            return dump;
        }

        /// <summary>读取器配置：<c>.xls</c>（BIFF）里取不到代码页时的兜底编码用日文 Shift_JIS。</summary>
        private static ExcelReaderConfiguration CreateConfiguration()
        {
            ExcelReaderConfiguration configuration = new ExcelReaderConfiguration();

            try
            {
                // .NET Framework 自带代码页，932 一般都能拿到；拿不到就保持读取器默认
                configuration.FallbackEncoding = Encoding.GetEncoding(932);
            }
            catch (Exception)
            {
                // 极端环境里取不到 932：不影响 .xlsx，.xls 的日文可能乱码
            }

            return configuration;
        }

        /// <summary>取当前工作表名（取不到时用「表N」）。</summary>
        private static string ReadSheetName(IExcelDataReader reader, int index)
        {
            try
            {
                string name = reader.Name;

                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name.Trim();
                }
            }
            catch (Exception)
            {
                // 某些损坏的表读 Name 会抛：按序号命名就行
            }

            return "表" + index.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>把当前读到的这一行拼成 <c>行N: a | b | c</c>（整行空白时返回空串）。</summary>
        private static string BuildRowLine(IExcelDataReader reader, int rowIndex)
        {
            int count;

            try
            {
                count = reader.FieldCount;
            }
            catch (Exception)
            {
                return string.Empty;
            }

            if (count < 0)
            {
                count = 0;
            }

            if (count > MaxColumnCount)
            {
                count = MaxColumnCount;
            }

            List<string> cells = new List<string>(count);

            for (int index = 0; index < count; index++)
            {
                cells.Add(ReadCell(reader, index));
            }

            // 去掉行尾的空格子（不影响「第几列是什么」，但能让文本短很多）
            int last = cells.Count - 1;

            while (last >= 0 && cells[last].Length == 0)
            {
                last--;
            }

            if (last < 0)
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(64);

            builder.Append('行').Append(rowIndex.ToString(CultureInfo.InvariantCulture)).Append(": ");

            for (int index = 0; index <= last; index++)
            {
                if (index > 0)
                {
                    builder.Append(" | ");
                }

                builder.Append(cells[index]);
            }

            string line = builder.ToString();

            return line.Length <= MaxLineChars ? line : line.Substring(0, MaxLineChars) + "…";
        }

        /// <summary>读一个单元格：日期按 <c>yyyy/MM/dd</c>，其余按原文；换行压空格、竖线换全角。</summary>
        private static string ReadCell(IExcelDataReader reader, int index)
        {
            object value;

            try
            {
                value = reader.GetValue(index);
            }
            catch (Exception)
            {
                // 合并单元格 / 损坏单元格读值会抛：当作空格子
                return string.Empty;
            }

            if (value == null || value is DBNull)
            {
                return string.Empty;
            }

            string text = value is DateTime
                ? ((DateTime)value).ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)
                : Convert.ToString(value, CultureInfo.InvariantCulture);

            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(text.Length);

            foreach (char ch in text)
            {
                if (ch == '\r' || ch == '\n' || ch == '\t')
                {
                    builder.Append(' ');
                }
                else if (ch == '|')
                {
                    // 竖线是行内分隔符，单元格里的换成全角，避免 AI 数错列
                    builder.Append('｜');
                }
                else if (ch < ' ')
                {
                    builder.Append(' ');
                }
                else
                {
                    builder.Append(ch);
                }
            }

            string normalized = builder.ToString().Trim();

            if (normalized.Length > MaxCellChars)
            {
                normalized = normalized.Substring(0, MaxCellChars).Trim();
            }

            return normalized;
        }

        /// <summary>记录一条提示（<paramref name="notes"/> 为 null 时忽略）。</summary>
        private static void AddNote(IList<string> notes, string message)
        {
            if (notes != null)
            {
                notes.Add(message);
            }
        }
    }
}
