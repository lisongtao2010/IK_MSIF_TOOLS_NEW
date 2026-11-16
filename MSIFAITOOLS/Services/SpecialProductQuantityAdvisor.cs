using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// 一条「数量」判定结果：对应 特注品.json / 特注品.csv 的一条明细
    /// （品種 + 部品名 + 部品コード 用来定位是哪一个部品）。
    /// </summary>
    internal sealed class SpecialProductQuantityItem
    {
        /// <summary>品種（原文照抄，可能为空）。</summary>
        public string Variety { get; set; }

        /// <summary>部品名（原文照抄，可能为空）。</summary>
        public string PartName { get; set; }

        /// <summary>部品コード（原文照抄，可能为空）。</summary>
        public string PartCode { get; set; }

        /// <summary>員数（= 部品基本信息表的「数量」；A / C / G 这类类型代号也照抄）。</summary>
        public string Quantity { get; set; }

        /// <summary>AI 说明这一条的数量是从哪儿读到的（文章 / 表）。</summary>
        public string Source { get; set; }

        /// <summary>AI 给出的判断理由（写日志用）。</summary>
        public string Reason { get; set; }
    }

    /// <summary>一份特注品 Excel 的 AI 判定结果（见 <see cref="SpecialProductQuantityPromptStore"/>）。</summary>
    internal sealed class SpecialProductQuantityAnswer
    {
        public SpecialProductQuantityAnswer()
        {
            Items = new List<SpecialProductQuantityItem>();
        }

        /// <summary>
        /// 整份文件的数量（文章型：正文写着「1個追加(同梱)」时的 1）。
        /// 表型时可能为空 —— 这时只用 <see cref="Items"/> 的逐条值。
        /// </summary>
        public string FileQuantity { get; set; }

        /// <summary>数量是从哪儿读到的（文章 / 表）；AI 没给时为空。</summary>
        public string Source { get; set; }

        /// <summary>AI 的一句话说明。</summary>
        public string Reason { get; set; }

        /// <summary>逐条部品的数量（表型）。</summary>
        public List<SpecialProductQuantityItem> Items { get; private set; }

        /// <summary>是否判到了任何一个数量。</summary>
        public bool HasQuantity
        {
            get
            {
                if (!string.IsNullOrEmpty(FileQuantity))
                {
                    return true;
                }

                foreach (SpecialProductQuantityItem item in Items)
                {
                    if (item != null && !string.IsNullOrEmpty(item.Quantity))
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    /// <summary>
    /// 一次「特注品记录数量设定」的结果：扫描 / AI / 写库的统计 + 提示。
    /// 由 <see cref="SpecialProductQuantityAdvisor.Run"/> 生成，供【AI 分析】写日志。
    /// </summary>
    internal sealed class SpecialProductQuantitySummary
    {
        public SpecialProductQuantitySummary()
        {
            Notes = new List<string>();
        }

        /// <summary>扫描过的 Excel 个数（含不是特注品的）。</summary>
        public int ScannedFileCount { get; set; }

        /// <summary>文件名第 5 位是 <c>Z</c>（特注品）的 Excel 个数。</summary>
        public int SpecialProductFileCount { get; set; }

        /// <summary>读到整表原文、送进 AI 的文件个数。</summary>
        public int ExcelReadFileCount { get; set; }

        /// <summary>调用 AI 的次数（一个特注品文件一次）。</summary>
        public int AiCallCount { get; set; }

        /// <summary>AI 判到「整份文件的数量」的文件个数（文章型）。</summary>
        public int FileQuantityCount { get; set; }

        /// <summary>AI 判到「逐条部品数量」的条数（表型）。</summary>
        public int ItemQuantityCount { get; set; }

        /// <summary>AI 没判到任何数量的文件个数。</summary>
        public int NoQuantityFileCount { get; set; }

        /// <summary>Excel 读不到文字 / AI 调用失败 / 解析失败的文件个数。</summary>
        public int FailedFileCount { get; set; }

        /// <summary>实际改动 特注品.json 的条目数（只算值真的变了的）。</summary>
        public int JsonUpdatedCount { get; set; }

        /// <summary>实际改动部品基本信息表「数量」的行数。</summary>
        public int DatabaseUpdatedCount { get; set; }

        /// <summary>数据库文件不存在 / 还没做过【CMD 分析】，所以没写库的文件个数。</summary>
        public int DatabaseSkippedFileCount { get; set; }

        /// <summary><c>classify\特注品.json</c> 全路径。</summary>
        public string TzJsonPath { get; set; }

        /// <summary>本次是否真的改写了 特注品.json。</summary>
        public bool JsonWritten { get; set; }

        /// <summary>本次使用的提示语来源（外部文件 / 内置资源 / 内置默认值）。</summary>
        public string PromptSource { get; set; }

        /// <summary>处理过程中的提示（由调用方写进日志）。</summary>
        public List<string> Notes { get; private set; }
    }

    /// <summary>
    /// 【AI 分析】追加机能 · <b>特注品记录的数量设定</b>。
    ///
    /// <para>
    /// <b>对象</b>：选择的 PDF 文件夹下文件名第 5 位是 <c>Z</c>（特注品，口径同
    /// <see cref="CmdSpecialProductRule.IsSpecialProductDrawingNo"/>）的 Excel
    /// —— <c>.xls</c> / <c>.xlsx</c> / <c>.xlsm</c> 都读（现场特注品里 <c>.xls</c> 很多，
    /// 所以不复用只认 <c>.xlsx</c> 的 <see cref="CmdSpecialProductCsvExporter"/>）。
    /// </para>
    ///
    /// <para>
    /// <b>为什么交给 AI</b>：数量的写法现场不统一，两种典型形态要靠「看懂文件」才分得清 ——
    /// <list type="number">
    ///   <item><b>文字型</b>（例 <c>I400Z10006.xls</c>）：正文 / 注記 里写着「1個追加(同梱)」
    ///         → 整份文件的数量 = 1（有「同梱」没写数字时按 1）；</item>
    ///   <item><b>表型</b>（例 <c>I400Z10002.xls</c>）：sheet1 等表格里有
    ///         「数量 / 員数 / 個数 / 入数 / QTY」列 → 按部品逐条取该列的值。</item>
    /// </list>
    /// 所以这里把 <see cref="ExcelSheetDumper"/> 导出的<b>整表原文</b>送给 AI，让 AI 自己认表 / 认文，
    /// 规则写在 <c>Prompts\SpecialProductQuantityAdvisor.Prompt.txt</c>（运行时可改）。
    /// </para>
    ///
    /// <para>
    /// <b>结果写到哪</b>（由用户确认：只更新 DB 与 特注品.json）：
    /// <list type="bullet">
    ///   <item>部品基本信息表的「数量」列（<see cref="PartBasicInfoRepository.QuantityColumnName"/>）：
    ///         按「来源表格文件 = 该図番的 <c>特注品.csv</c>」定位【CMD 分析】第 4 步登录的行
    ///         （整份文件的数量 → 该文件全部行；逐条数量 → 按 部品コード / 部品名 对上的行）；</item>
    ///   <item><c>classify\特注品.json</c> 的「員数」字段（该 <c>file</c> 的条目）；</item>
    ///   <item><b>不写 <c>特注品.csv</c></b>：那份文件由【CMD 分析】最前面一步重新生成，手改会被覆盖。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>安全约定</b>：数量只照抄 AI 从 Excel 读到的原文（不换算、不合计、不推测）；
    /// 判不到数量的文件不动任何数据；AI 调用 / 解析失败只跳过这一个文件；
    /// 数据库文件还不存在（没做过【CMD 分析】）时不在这里新建数据库，只写 特注品.json。
    /// </para>
    /// </summary>
    internal static class SpecialProductQuantityAdvisor
    {
        /// <summary>数量在 特注品.json 里的字段名（= 特注品.csv 的同名列）。</summary>
        public const string QuantityKey = "員数";

        /// <summary>能读的 Excel 扩展名（与 <see cref="ExcelSheetDumper"/> 一致）。</summary>
        private static readonly string[] ExcelExtensions = new string[] { ".xls", ".xlsx", ".xlsm" };

        /// <summary>特注品.json 顶层存放明细的键（与 <see cref="CmdTzJsonMaintainer"/> 一致）。</summary>
        private const string ItemKey = CmdTzJsonMaintainer.ItemKey;

        /// <summary>Excel 打开文件时留下的临时文件前缀。</summary>
        private const string TempFilePrefix = "~$";

        /// <summary>送给 AI 的「已登记部品」最多多少条。</summary>
        private const int MaxKnownItemCount = 200;

        /// <summary>AI 给的单元格文本长度上限（去掉换行后截断）。</summary>
        private const int MaxCellLength = 60;

        /// <summary>AI 给的数量长度上限。</summary>
        private const int MaxQuantityLength = 20;

        private static readonly JavaScriptSerializer Serializer = CreateSerializer();

        /// <summary>
        /// 对 <paramref name="pdfDirectory"/> 下所有特注品 Excel 判数量，写到数据库与 特注品.json。
        /// </summary>
        /// <param name="pdfDirectory">选择的 PDF 文件夹（<paramref name="outputDirectory"/> 的上一级）。</param>
        /// <param name="outputDirectory">分解输出根目录（&lt;PDF 文件夹&gt;\AiAppData）。</param>
        /// <param name="databasePath">部品基本信息数据库路径（不存在时跳过写库）。</param>
        /// <param name="options">AI 接口配置（未配置时直接返回）。</param>
        /// <param name="onLog">日志回调（kind, message）。</param>
        public static SpecialProductQuantitySummary Run(string pdfDirectory, string outputDirectory,
            string databasePath, AiApiOptions options, Action<LogKind, string> onLog)
        {
            SpecialProductQuantitySummary summary = new SpecialProductQuantitySummary();

            if (options == null || !options.IsConfigured)
            {
                AddNote(summary, "未配置 AI 接口密钥，特注品数量设定跳过");

                return summary;
            }

            if (string.IsNullOrWhiteSpace(pdfDirectory) || !Directory.Exists(pdfDirectory))
            {
                AddNote(summary, "找不到 PDF 文件夹：" + pdfDirectory);

                return summary;
            }

            string root = (outputDirectory ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string jsonPath = string.IsNullOrEmpty(root)
                ? string.Empty
                : Path.Combine(CmdTzPartCollector.GetClassifyDirectory(root), CmdTzPartCollector.FileName);

            summary.TzJsonPath = jsonPath;

            string[] files;

            try
            {
                files = Directory.GetFiles(pdfDirectory, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                AddNote(summary, "读取文件夹失败（" + pdfDirectory + "）：" + ex.Message);

                return summary;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            SpecialProductQuantityPromptSet prompts = SpecialProductQuantityPromptStore.Load();
            summary.PromptSource = prompts.Source;

            PartAnalysisService.ReportLog(onLog, LogKind.Info, string.Empty);
            PartAnalysisService.ReportLog(onLog, LogKind.Info, string.Format(
                "特注品数量设定：扫描 {0} 下的 Excel（文件名第 {1} 位是 {2} = 特注品）→ AI 判数量 → " +
                "写部品基本信息「{3}」列 + {4} 的「{5}」",
                pdfDirectory, CmdSpecialProductRule.DrawingNoPosition, CmdSpecialProductRule.DrawingNoMark,
                PartBasicInfoRepository.QuantityColumnName, CmdTzPartCollector.FileName, QuantityKey));
            PartAnalysisService.ReportLog(onLog, LogKind.Info, "  提示语：" + prompts.Source);

            if (!string.IsNullOrEmpty(SpecialProductQuantityPromptStore.LastLoadError))
            {
                PartAnalysisService.ReportLog(onLog, LogKind.Warning,
                    "  " + SpecialProductQuantityPromptStore.LastLoadError);
            }

            ChatCompletionClient client = new ChatCompletionClient(options);

            foreach (string file in files)
            {
                ProcessFile(file, root, jsonPath, databasePath, prompts, client, summary, onLog);
            }

            PartAnalysisService.ReportLog(onLog,
                summary.JsonUpdatedCount > 0 || summary.DatabaseUpdatedCount > 0
                    ? LogKind.Success
                    : LogKind.Info,
                "  " + BuildSummaryText(summary));

            foreach (string note in summary.Notes)
            {
                PartAnalysisService.ReportLog(onLog, LogKind.Info, "  提示：" + note);
            }

            return summary;
        }

        /// <summary>处理一个 Excel（不是特注品 / 读不到文字时跳过，不影响其它文件）。</summary>
        private static void ProcessFile(string file, string root, string jsonPath, string databasePath,
            SpecialProductQuantityPromptSet prompts, ChatCompletionClient client,
            SpecialProductQuantitySummary summary, Action<LogKind, string> onLog)
        {
            string fileName = Path.GetFileName(file);

            if (string.IsNullOrEmpty(fileName) ||
                fileName.StartsWith(TempFilePrefix, StringComparison.Ordinal))
            {
                return;
            }

            if (!IsExcelExtension(Path.GetExtension(fileName)))
            {
                return;
            }

            summary.ScannedFileCount++;

            string drawingNo = Path.GetFileNameWithoutExtension(fileName);

            if (!CmdSpecialProductRule.IsSpecialProductDrawingNo(drawingNo))
            {
                return;
            }

            summary.SpecialProductFileCount++;

            ExcelSheetDumpResult dump = ExcelSheetDumper.Read(file, summary.Notes);

            if (dump == null || !dump.HasText)
            {
                summary.FailedFileCount++;
                AddNote(summary, fileName + "：读不到 Excel 文字，跳过");

                return;
            }

            summary.ExcelReadFileCount++;

            List<SpecialProductQuantityItem> known = LoadKnownItems(jsonPath, fileName);
            SpecialProductQuantityAnswer answer;

            try
            {
                answer = AskAi(client, prompts, drawingNo, dump.Text, known);
                summary.AiCallCount++;
            }
            catch (Exception ex)
            {
                summary.FailedFileCount++;
                AddNote(summary, string.Format("{0}：AI 判定失败（{1}），跳过", fileName, ex.Message));

                return;
            }

            if (answer == null)
            {
                summary.FailedFileCount++;
                AddNote(summary, fileName + "：AI 回复解析失败，跳过");

                return;
            }

            if (!string.IsNullOrEmpty(answer.FileQuantity))
            {
                summary.FileQuantityCount++;
            }

            foreach (SpecialProductQuantityItem item in answer.Items)
            {
                if (item != null && !string.IsNullOrEmpty(item.Quantity))
                {
                    summary.ItemQuantityCount++;
                }
            }

            if (!answer.HasQuantity)
            {
                summary.NoQuantityFileCount++;
                AddNote(summary, fileName + "：没有读到数量（AI 说明：" +
                    (string.IsNullOrEmpty(answer.Reason) ? "无" : answer.Reason) + "）");

                return;
            }

            // ① 特注品.json 的「員数」（该 file 的条目）
            int jsonUpdated = UpdateTzJson(jsonPath, fileName, answer, summary);

            if (jsonUpdated > 0)
            {
                summary.JsonUpdatedCount += jsonUpdated;
                summary.JsonWritten = true;
            }

            // ② 部品基本信息表的「数量」（【CMD 分析】第 4 步登录的行：来源表格文件 = 该図番的 特注品.csv）
            string csvPath = string.IsNullOrEmpty(root)
                ? string.Empty
                : Path.Combine(root, drawingNo, CmdSpecialProductCsvExporter.CsvFileName);
            int databaseUpdated = UpdateDatabase(databasePath, csvPath, answer, summary);

            summary.DatabaseUpdatedCount += databaseUpdated;

            PartAnalysisService.ReportLog(onLog, LogKind.Info, string.Format(
                "  {0}：{1}（依据：{2}）→ DB 数量 {3} 行 / 特注品.json {4} 条",
                fileName, DescribeAnswer(answer),
                string.IsNullOrEmpty(answer.Source) ? "AI" : answer.Source,
                databaseUpdated, jsonUpdated));
        }

        /// <summary>把整表原文送 AI，解析出数量判定结果（失败时返回 null）。</summary>
        private static SpecialProductQuantityAnswer AskAi(ChatCompletionClient client,
            SpecialProductQuantityPromptSet prompts, string drawingNo, string excelDump,
            IList<SpecialProductQuantityItem> known)
        {
            string userPrompt = prompts.UserTemplate
                .Replace("{{DRAWING_NO}}", drawingNo ?? string.Empty)
                .Replace("{{KNOWN_ITEMS}}", BuildKnownItemsText(known))
                .Replace("{{EXCEL_DUMP}}", excelDump ?? string.Empty);

            string content = client.Complete(prompts.SystemPrompt, userPrompt);
            string json = ExtractJsonObject(content);

            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            Dictionary<string, object> root = Serializer.DeserializeObject(json) as Dictionary<string, object>;

            if (root == null)
            {
                return null;
            }

            SpecialProductQuantityAnswer answer = new SpecialProductQuantityAnswer();
            answer.FileQuantity = NormalizeQuantity(GetText(root, "fileQuantity", "file_quantity", "数量"));
            answer.Source = Truncate(GetText(root, "source", "来源", "依据"), MaxCellLength);
            answer.Reason = Truncate(GetText(root, "reason", "理由", "说明"), 200);

            object[] items = GetArray(root, "items", "rows", "明细");

            if (items != null)
            {
                foreach (object element in items)
                {
                    Dictionary<string, object> map = element as Dictionary<string, object>;

                    if (map == null)
                    {
                        continue;
                    }

                    SpecialProductQuantityItem item = new SpecialProductQuantityItem();
                    item.Variety = Truncate(GetText(map, "品種", "品种", "variety"), MaxCellLength);
                    item.PartName = Truncate(GetText(map, "部品名", "品名", "partName"), MaxCellLength);
                    item.PartCode = Truncate(GetText(map, "部品コード", "partCode", "part_code"),
                        MaxCellLength);
                    item.Quantity = NormalizeQuantity(GetText(map, "員数", "数量", "qty", "quantity"));
                    item.Source = Truncate(GetText(map, "source", "来源"), MaxCellLength);
                    item.Reason = Truncate(GetText(map, "reason", "理由"), 200);

                    // 既没有品種、也没有部品名和部品コード的行：对不上任何记录，丢掉
                    if (item.Variety.Length == 0 && item.PartName.Length == 0 && item.PartCode.Length == 0)
                    {
                        continue;
                    }

                    answer.Items.Add(item);
                }
            }

            return answer;
        }

        /// <summary>拼「已经登记的部品」清单（送 AI 的对照表）。</summary>
        private static string BuildKnownItemsText(IList<SpecialProductQuantityItem> items)
        {
            if (items == null || items.Count == 0)
            {
                return "（空：这份文件在 " + CmdTzPartCollector.FileName + " 里还没有条目）";
            }

            StringBuilder builder = new StringBuilder();

            foreach (SpecialProductQuantityItem item in items)
            {
                if (item == null)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }

                builder.Append("- 品種：").Append(EmptyAsDash(item.Variety))
                    .Append(" / 部品名：").Append(EmptyAsDash(item.PartName))
                    .Append(" / 部品コード：").Append(EmptyAsDash(item.PartCode));
            }

            return builder.ToString();
        }

        /// <summary>从 特注品.json 读该文件的已有条目（只用品種 / 部品名 / 部品コード 做对照）。</summary>
        private static List<SpecialProductQuantityItem> LoadKnownItems(string jsonPath, string xlsxFileName)
        {
            List<SpecialProductQuantityItem> items = new List<SpecialProductQuantityItem>();

            if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath))
            {
                return items;
            }

            Dictionary<string, object> root;

            try
            {
                root = Serializer.DeserializeObject(File.ReadAllText(jsonPath, Encoding.UTF8))
                    as Dictionary<string, object>;
            }
            catch (Exception)
            {
                return items;
            }

            object[] array = GetArray(root, ItemKey);

            if (array == null)
            {
                return items;
            }

            foreach (object element in array)
            {
                Dictionary<string, object> map = element as Dictionary<string, object>;

                if (map == null || !IsSameExcelFile(GetText(map, "file", "文件名"), xlsxFileName))
                {
                    continue;
                }

                SpecialProductQuantityItem item = new SpecialProductQuantityItem();
                item.Variety = GetText(map, "品種", "品种");
                item.PartName = GetText(map, "部品名", "品名");
                item.PartCode = GetText(map, "部品コード");
                items.Add(item);

                if (items.Count >= MaxKnownItemCount)
                {
                    break;
                }
            }

            return items;
        }

        /// <summary>
        /// 把数量写进 特注品.json 里该文件条目的「員数」（整份文件的数量 → 该文件全部条目；
        /// 逐条数量 → 按 部品コード / 部品名 / 品種 对上的条目，逐条值优先）。返回实际改动的条目数。
        /// </summary>
        private static int UpdateTzJson(string jsonPath, string xlsxFileName,
            SpecialProductQuantityAnswer answer, SpecialProductQuantitySummary summary)
        {
            if (answer == null || !answer.HasQuantity)
            {
                return 0;
            }

            if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath))
            {
                AddNote(summary, xlsxFileName + "：还没有 " + CmdTzPartCollector.FileName +
                    "，不写「" + QuantityKey + "」");

                return 0;
            }

            Dictionary<string, object> root;

            try
            {
                root = Serializer.DeserializeObject(File.ReadAllText(jsonPath, Encoding.UTF8))
                    as Dictionary<string, object>;
            }
            catch (Exception ex)
            {
                AddNote(summary, CmdTzPartCollector.FileName + " 解析失败（" + ex.Message + "），跳过");

                return 0;
            }

            object[] array = GetArray(root, ItemKey);

            if (array == null)
            {
                return 0;
            }

            int changed = 0;

            foreach (object element in array)
            {
                Dictionary<string, object> map = element as Dictionary<string, object>;

                if (map == null || !IsSameExcelFile(GetText(map, "file", "文件名"), xlsxFileName))
                {
                    continue;
                }

                string quantity = string.Empty;
                SpecialProductQuantityItem matched = FindItem(answer.Items, map);

                if (matched != null && !string.IsNullOrEmpty(matched.Quantity))
                {
                    quantity = matched.Quantity;
                }
                else if (!string.IsNullOrEmpty(answer.FileQuantity))
                {
                    quantity = answer.FileQuantity;
                }

                if (string.IsNullOrEmpty(quantity))
                {
                    continue;
                }

                if (string.Equals(GetText(map, QuantityKey), quantity, StringComparison.Ordinal))
                {
                    continue;
                }

                map[QuantityKey] = quantity;
                changed++;
            }

            if (changed > 0)
            {
                try
                {
                    CmdSpecialProductCsvExporter.WriteJsonFile(jsonPath, root, Serializer);
                }
                catch (Exception ex)
                {
                    AddNote(summary, CmdTzPartCollector.FileName + " 写入失败（" + ex.Message + "）");

                    return 0;
                }
            }

            return changed;
        }
        /// <summary>
        /// 把数量写进部品基本信息表的「数量」列：整份文件的数量 → 该 <c>特注品.csv</c> 的全部行；
        /// 逐条数量 → 按 部品コード / 部品名 对上的行（逐条值覆盖整份文件的值）。返回更新的行数。
        /// </summary>
        private static int UpdateDatabase(string databasePath, string csvPath,
            SpecialProductQuantityAnswer answer, SpecialProductQuantitySummary summary)
        {
            if (answer == null || !answer.HasQuantity || string.IsNullOrWhiteSpace(databasePath) ||
                string.IsNullOrWhiteSpace(csvPath))
            {
                return 0;
            }

            string drawingNo = Path.GetFileName(Path.GetDirectoryName(csvPath)) ?? string.Empty;

            // 还没做过【CMD 分析】：库文件都不存在，这里不新建（避免弹出空库）
            if (!File.Exists(databasePath))
            {
                summary.DatabaseSkippedFileCount++;
                AddNote(summary, drawingNo + "：数据库还不存在（先执行【CMD 分析】登录部品，再重跑本步）");

                return 0;
            }

            int updated = 0;

            try
            {
                using (PartBasicInfoRepository repository = new PartBasicInfoRepository(databasePath))
                {
                    if (!string.IsNullOrEmpty(answer.FileQuantity))
                    {
                        updated += repository.UpdateQuantityBySourceFile(csvPath, answer.FileQuantity);
                    }

                    foreach (SpecialProductQuantityItem item in answer.Items)
                    {
                        if (item == null || string.IsNullOrEmpty(item.Quantity))
                        {
                            continue;
                        }

                        updated += repository.UpdateQuantityByPart(csvPath, item.PartCode, item.PartName,
                            item.Quantity);
                    }
                }
            }
            catch (Exception ex)
            {
                AddNote(summary, "写数据库失败（" + ex.Message + "）");

                return 0;
            }

            if (updated == 0)
            {
                AddNote(summary, drawingNo + "：数据库里没有对应的行（该図番还没做【CMD 分析】第 4 步？）");
            }

            return updated;
        }

        /// <summary>把判定结果写成人看的一句话（日志用）。</summary>
        private static string DescribeAnswer(SpecialProductQuantityAnswer answer)
        {
            if (answer == null)
            {
                return "无判定";
            }

            int items = 0;

            foreach (SpecialProductQuantityItem item in answer.Items)
            {
                if (item != null && !string.IsNullOrEmpty(item.Quantity))
                {
                    items++;
                }
            }

            if (!string.IsNullOrEmpty(answer.FileQuantity))
            {
                return "整份 " + answer.FileQuantity + (items > 0 ? "，另有逐条 " + items + " 条" : string.Empty);
            }

            return "逐条 " + items + " 条";
        }

        /// <summary>完成提示（【AI 分析】的汇总文字里用）。</summary>
        public static string BuildSummaryText(SpecialProductQuantitySummary summary)
        {
            if (summary == null)
            {
                return "特注品数量设定：未执行";
            }

            return string.Format(
                "特注品数量设定：扫描 {0} 个 Excel（特注品 {1} 个），AI 调用 {2} 次 → " +
                "写数据库 {3} 行 / {4} 的「{5}」{6} 条（整份 {7} 个 / 逐条 {8} 条 / " +
                "没读到数量 {9} 个 / 跳过 {10} 个{11}）",
                summary.ScannedFileCount, summary.SpecialProductFileCount, summary.AiCallCount,
                summary.DatabaseUpdatedCount, CmdTzPartCollector.FileName, QuantityKey,
                summary.JsonUpdatedCount, summary.FileQuantityCount, summary.ItemQuantityCount,
                summary.NoQuantityFileCount, summary.FailedFileCount,
                summary.DatabaseSkippedFileCount > 0
                    ? "，数据库未就绪 " + summary.DatabaseSkippedFileCount + " 个文件"
                    : string.Empty);
        }
        // ── 小工具 ───────────────────────────────────────────────────────────

        /// <summary>从 AI 回复里取出 JSON 对象（去掉 ```json 代码块与前后说明文字）。</summary>
        private static string ExtractJsonObject(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            int start = content.IndexOf('{');
            int end = content.LastIndexOf('}');

            if (start < 0 || end <= start)
            {
                return null;
            }

            return content.Substring(start, end - start + 1);
        }

        /// <summary>取字典里的文本（按候选键的顺序；空值 / 对象 / 数组按空串处理）。</summary>
        private static string GetText(Dictionary<string, object> map, params string[] keys)
        {
            if (map == null || keys == null)
            {
                return string.Empty;
            }

            foreach (string key in keys)
            {
                if (string.IsNullOrEmpty(key) || !map.ContainsKey(key))
                {
                    continue;
                }

                object value = map[key];

                if (value == null || value is Dictionary<string, object> || value is object[])
                {
                    continue;
                }

                string text = Convert.ToString(value, CultureInfo.InvariantCulture);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }

            return string.Empty;
        }

        /// <summary>取字典里的数组（没有 / 不是数组时返回 null）。</summary>
        private static object[] GetArray(Dictionary<string, object> map, params string[] keys)
        {
            if (map == null || keys == null)
            {
                return null;
            }

            foreach (string key in keys)
            {
                if (string.IsNullOrEmpty(key) || !map.ContainsKey(key))
                {
                    continue;
                }

                object[] array = map[key] as object[];

                if (array != null)
                {
                    return array;
                }
            }

            return null;
        }

        /// <summary>在 AI 给的逐条结果里找与 特注品.json 这一条对应的条目（部品コード → 部品名 → 品種）。</summary>
        private static SpecialProductQuantityItem FindItem(IList<SpecialProductQuantityItem> items,
            Dictionary<string, object> map)
        {
            if (items == null || items.Count == 0 || map == null)
            {
                return null;
            }

            string code = NormalizeKey(GetText(map, "部品コード"));
            string name = NormalizeKey(GetText(map, "部品名", "品名"));
            string variety = NormalizeKey(GetText(map, "品種", "品种"));

            foreach (SpecialProductQuantityItem item in items)
            {
                if (item == null || string.IsNullOrEmpty(item.Quantity))
                {
                    continue;
                }

                string itemCode = NormalizeKey(item.PartCode);
                string itemName = NormalizeKey(item.PartName);
                string itemVariety = NormalizeKey(item.Variety);

                if (code.Length > 0 && itemCode.Length > 0 &&
                    string.Equals(code, itemCode, StringComparison.Ordinal))
                {
                    return item;
                }

                if (name.Length > 0 && itemName.Length > 0 &&
                    string.Equals(name, itemName, StringComparison.Ordinal))
                {
                    return item;
                }

                if (variety.Length > 0 && itemVariety.Length > 0 &&
                    string.Equals(variety, itemVariety, StringComparison.Ordinal))
                {
                    return item;
                }
            }

            return null;
        }

        /// <summary>特注品.json 的 file 与这个 Excel 是不是同一个文件（忽略扩展名与路径）。</summary>
        private static bool IsSameExcelFile(string tzFile, string xlsxFileName)
        {
            if (string.IsNullOrWhiteSpace(tzFile) || string.IsNullOrWhiteSpace(xlsxFileName))
            {
                return false;
            }

            return string.Equals(Path.GetFileNameWithoutExtension(tzFile.Trim()),
                Path.GetFileNameWithoutExtension(xlsxFileName.Trim()), StringComparison.OrdinalIgnoreCase);
        }
        /// <summary>数量文本归一化：去换行 / 前后空白并截断；半角「-」（Excel 里表示「没有」）按空处理。</summary>
        private static string NormalizeQuantity(string value)
        {
            string text = Truncate(value, MaxQuantityLength);

            return string.Equals(text, "-", StringComparison.Ordinal) ? string.Empty : text;
        }

        /// <summary>比较用的归一化：NFKC（全角英数 → 半角）、去空白、统一大写。</summary>
        private static string NormalizeKey(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            string text = value.Normalize(NormalizationForm.FormKC)
                .Replace(" ", string.Empty)
                .Replace("\u3000", string.Empty)
                .Replace("\r", string.Empty)
                .Replace("\n", string.Empty);

            return text.Trim().ToUpperInvariant();
        }

        /// <summary>去掉换行 / 连续空白后截断到指定长度。</summary>
        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            string text = value.Replace("\r", " ").Replace("\n", " ").Trim();

            while (text.IndexOf("  ", StringComparison.Ordinal) >= 0)
            {
                text = text.Replace("  ", " ");
            }

            if (maxLength > 0 && text.Length > maxLength)
            {
                text = text.Substring(0, maxLength).Trim();
            }

            return text;
        }

        /// <summary>空值写成「-」（提示词里给人看）。</summary>
        private static string EmptyAsDash(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
        }

        /// <summary>是不是能读的 Excel 扩展名。</summary>
        private static bool IsExcelExtension(string extension)
        {
            if (string.IsNullOrEmpty(extension))
            {
                return false;
            }

            foreach (string candidate in ExcelExtensions)
            {
                if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>记一条提示（由 <see cref="Run"/> 统一写进日志）。</summary>
        private static void AddNote(SpecialProductQuantitySummary summary, string note)
        {
            if (summary == null || string.IsNullOrWhiteSpace(note))
            {
                return;
            }

            summary.Notes.Add(note.Trim());
        }

        /// <summary>建解析器（<see cref="JavaScriptSerializer"/> 默认 2MB 上限不够：特注品.json 可能更大）。</summary>
        private static JavaScriptSerializer CreateSerializer()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();

            // 与 CmdTzPartCollector.LoadEntries 一致：项目大时 特注品.json 可能超过默认 2MB
            serializer.MaxJsonLength = int.MaxValue;

            return serializer;
        }
    }
}

