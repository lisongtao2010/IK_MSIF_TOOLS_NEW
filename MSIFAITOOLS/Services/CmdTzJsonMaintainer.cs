using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// 一次「第 0 步 · 特注品.json 维护」的结果：扫描 / AI / 追加的统计 + 提示。
    /// 由 <see cref="CmdTzJsonMaintainer.Run"/> 生成，供【CMD 分析】写日志。
    /// </summary>
    internal sealed class CmdTzJsonMaintainResult
    {
        public CmdTzJsonMaintainResult()
        {
            Notes = new List<string>();
        }

        /// <summary>扫描过的 Excel 个数（含不是特注品的）。</summary>
        public int ScannedFileCount { get; set; }

        /// <summary>文件名第 5 位是 <c>Z</c>（特注品）的 Excel 个数。</summary>
        public int SpecialProductFileCount { get; set; }

        /// <summary>读不到部品表（没有表头 / 打不开）而跳过的个数。</summary>
        public int NoPartTableFileCount { get; set; }

        /// <summary>调用 AI 的次数（一个特注品文件一次）。</summary>
        public int AiCallCount { get; set; }

        /// <summary>AI 判定「要追加」且有条目真正写进去的文件个数。</summary>
        public int AiAddedFileCount { get; set; }

        /// <summary>AI 判定「不用追加」（或判定要追加但没有可写条目）的文件个数。</summary>
        public int AiRejectedFileCount { get; set; }

        /// <summary>AI 调用 / 解析失败的文件个数（失败时 特注品.json 保持不动）。</summary>
        public int AiFailedFileCount { get; set; }

        /// <summary>实际追加进 特注品.json 的条目数。</summary>
        public int AddedRowCount { get; set; }

        /// <summary>AI 给了、但和已有条目重复（品種 + 部品名 + 枠区分 + 部品コード + 追加対応特注寸法 相同）而没写的条数。</summary>
        public int SkippedDuplicateCount { get; set; }

        /// <summary>维护前 特注品.json 里已有的条目数。</summary>
        public int ExistingRowCount { get; set; }

        /// <summary>
        /// <c>特注品.json</c> 里<b>已经有这个文件的条目</b>、所以本次没读 Excel 也没送 AI 的文件个数
        /// （只补 json 里还没有的文件）。
        /// </summary>
        public int ExistingFileSkippedCount { get; set; }

        /// <summary>上面那些被跳过的文件在 <c>特注品.json</c> 里已有的条目数合计（仅供参考）。</summary>
        public int ExistingFileRowCount { get; set; }

        /// <summary>本次开始时 <c>classify\特注品.json</c> 是否存在（不存在 = 按「作成名」让 AI 建一份新的）。</summary>
        public bool JsonExistedAtStart { get; set; }

        /// <summary>本次是否<b>新建</b>了 <c>特注品.json</c>（原本不存在 + 真有条目写进去）。</summary>
        public bool JsonCreated { get; set; }

        /// <summary><c>classify\特注品.json</c> 全路径。</summary>
        public string TzJsonPath { get; set; }

        /// <summary>是否真的写了 特注品.json（一条都没追加时不写，旧文件保持不动）。</summary>
        public bool Written { get; set; }

        /// <summary>处理过程中的提示（由调用方写进日志）。</summary>
        public List<string> Notes { get; private set; }
    }

    /// <summary>
    /// 【CMD 分析】<b>第 0 步 · 特注品.json 维护</b>（主按钮【CMD 分析】<b>固定最先执行</b>，
    /// 排在「最前面 · 特注品 CSV 生成」之前；界面上没有开关）。
    ///
    /// <para>
    /// 遍历 PDF 文件夹里文件名第 5 位是 <c>Z</c>（特注品）的 Excel，把<b>整份文件的表格原文</b>
    /// （所有工作表 + 行号 + 单元格原文，见 <see cref="ExcelSheetDumper"/>）交给 AI，
    /// <b>由 AI 自己认出部品表并判断哪些条目需要追加到 <c>classify\特注品.json</c></b>；
    /// AI 说「不用追加」的文件不动 json，说「要追加」的按 AI 给出的条目写入。
    /// </para>
    ///
    /// <para>
    /// <b>只补 json 里还没有的文件</b>：遍历到的特注品 Excel，先看 <c>特注品.json</c> 里有没有
    /// <b>这个文件</b>（条目的 <c>file</c> 字段）的明细；
    ///   · 一条都没有（json 里还不包含这个文件）→ 才读 Excel、让 AI 读取并<b>补足</b> json；
    ///   · 已经有明细 → 这个文件<b>不读 Excel、不送 AI</b>，直接跳过（日志里写「跳过：已在 json 里」）。
    /// 这样重跑【CMD 分析】时不会对同一份 Excel 反复调用 AI（成本 / 时间都会浪费在重复判定上）。
    /// </para>
    ///
    /// <para>
    /// 为什么要整表送 AI：现场 Excel / PDF 的格式多种多样，靠「按列名找表头」的规则永远会漏
    /// （今天就有：文件里有 <c>カメラ</c> 表但表头认不出 → 规则 0 条 → AI 什么也没看到）。
    /// 让 AI 直接读文件原文，才是对付千奇百怪排版的正解。
    /// </para>
    ///
    /// <para>
    /// <b>和「最前面 · 特注品 CSV 生成」的区别</b>：那一步是把 Excel 的部品表原样导成
    /// <c>AiAppData\&lt;文件名&gt;\特注品.csv</c>（并做字段补足），<b>不判定</b>「该不该进 特注品.json」；
    /// 本步骤专门做「判定 + 维护 json」，所以排在最前面：json 先补全，后面的第 4 步
    /// （<see cref="CmdTzPartCollector"/>）与 CSV 生成都能用到更完整的内容。
    /// </para>
    ///
    /// <para>
    /// <b>作成名（<c>特注品.json</c> 还不存在时）</b>：没有可追加的对象，AI 的活改成
    /// 「把这份部品表里属于特注品的条目<b>全部列出</b>」，程序用这些原文条目<b>新建</b>一份
    /// <c>特注品.json</c>；AI 一条都没给出时按<b>部品表明细原样作成</b>（兜底，与「特注品 CSV 生成」
    /// 同口径：已排除「追加不要」的行），保证这一步一定能建出文件。
    /// </para>
    ///
    /// <para>
    /// <b>怎么认部品表</b>（关键：<b>不靠表头规则，靠 AI 读整份文件</b>）：现场 Excel 的排版千差万别
    /// （部品表可能在任意一张工作表、任意位置，表头多行、合并单元格、竖排说明都有），
    /// 所以每次调用 AI 之前，都用 <see cref="ExcelSheetDumper"/> 把<b>整份文件的所有工作表</b>导出成
    /// 「<c>### 工作表「表名」</c> + <c>行N: 单元格 | 单元格 | …</c>」的原文送过去，
    /// 由 AI 自己认出哪一块是部品表、哪一列是 品種 / 部品名 / 枠区分 / 部品コード / 追加対応特注寸法。
    /// </para>
    ///
    /// <para>
    /// 程序仍会先按列名规则解析一遍（工作表 <c>カメラ</c> → 退回第一张表；别名见
    /// <c>PartNameHeaders</c> / <c>PartCodeHeaders</c>，比较前做 NFKC 归一化），
    /// 但它的结果只用于两件事：① 作为参照一起送给 AI；② AI 没给出可用条目时的作成名兜底。
    /// 规则一条都没解析出来时<b>照样送 AI</b>（整表原文才是主依据）。
    /// </para>
    ///
    /// <para>
    /// <b>安全约定</b>：
    /// <list type="bullet">
    ///   <item><c>特注品.json</c> 里已经有这个文件的条目时，这个文件<b>不读 Excel、不送 AI</b>
    ///         （= 只补 json 里还没有的文件；见 <see cref="CountExistingForFile"/>）；</item>
    ///   <item>能在规则解析结果里对上的条目，字段一律用<b>部品表原文</b>（AI 只决定「哪一行要追加」）；</item>
    ///   <item>规则没解析出来的条目（Excel 排版认不出），字段用 <b>AI 从整表原文里读到的原文</b>，
    ///         并且至少要给出「部品名」或「部品コード」，全空的条目丢弃；</item>
    ///   <item>和已有条目重复（品種 + 部品名 + 枠区分 + 部品コード + 追加対応特注寸法 <b>全同</b>）的条目<b>不重复写</b>；
    ///         只比前三项会把同一部品在多个 特注 W 範囲 的行当成重复丢掉（<c>I400Z10002</c> 的 <c>MZB976</c> 就是这种）；</item>
    ///   <item>写着「追加不要 / 追加しない」这类字样的行不送 AI 也不写（<see cref="CmdTzPartCollector.ContainsNoNeedMark"/>）；</item>
    ///   <item>写回时同步顶层计数器：<c>item_count</c> 改成实际条数、<c>file_count</c> 只往上调
    ///         —— 不同步就会留下「<c>item_count: 23</c> 但 <c>items</c> 37 条」这种旧值（见 <see cref="SyncCounters"/>）；</item>
    ///   <item>一条都没追加时<b>不写文件</b>（旧 特注品.json 保持不动）；JSON 解析失败时整步跳过，不覆盖；</item>
    ///   <item>单个文件读不动 / AI 调用失败只跳过这一个文件，不打断整批。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// 提示语见 <see cref="TzJsonMaintainPromptStore"/>（外部文件
    /// <c>Prompts\TzJsonMaintain.Prompt.txt</c> 可运行时修改）。
    /// </para>
    /// </summary>
    internal static class CmdTzJsonMaintainer
    {
        /// <summary>特注品.json 文件名（与 <see cref="CmdTzPartCollector.FileName"/> 同一份）。</summary>
        public const string TzJsonFileName = CmdTzPartCollector.FileName;

        /// <summary>优先读的工作表名（与 <c>CadPartSplitter\TzJsonBuilder.PreferredSheetName</c> 一致）。</summary>
        public const string PreferredSheetName = "カメラ";

        /// <summary>退回第一张工作表时，明细里记的表名（只影响 json 的 sheet 字段与日志）。</summary>
        private const string FirstSheetName = "<第一张表>";

        /// <summary>特注品.json 顶层存放明细的键（与 <see cref="CmdTzPartCollector"/> 一致）。</summary>
        public const string ItemKey = "items";

        /// <summary>
        /// 特注品.json 顶层的明细条数键（由 <c>CadPartSplitter\TzJsonBuilder</c> 写出；
        /// 存在就同步 —— 不同步会留下「item_count 23 / items 实际 37 条」这种旧值）。
        /// </summary>
        private const string ItemCountKey = "item_count";

        /// <summary>特注品.json 顶层的来源 Excel 个数键（同上，存在就同步；只往上调）。</summary>
        private const string FileCountKey = "file_count";

        /// <summary>本步骤写进去的条目在 <c>source</c> 字段上的值（<c>rule</c> = 规则判出来的）。</summary>
        public const string SourceAi = "ai";

        /// <summary>明细里的五个字段名（顺序即 JSON 里的字段顺序）。</summary>
        private static readonly string[] FieldKeys = new string[]
        {
            "品種", "部品名", "枠区分", "部品コード", "追加対応特注寸法"
        };

        private const string FileKey = "file";
        private const string SheetKey = "sheet";
        private const string RowKey = "row";
        private const string SourceKey = "source";

        /// <summary>能读的 Excel 扩展名（与「特注品 CSV 生成」一致）。</summary>
        private static readonly string[] ExcelExtensions = { ".xls", ".xlsx", ".xlsm" };

        /// <summary>部品表里找表头的最大扫描行数（表头上方可能有表题 / 変更履歴）。</summary>
        private const int HeaderScanRows = 80;

        /// <summary>一个文件最多取多少条明细（防止提示词过长）。</summary>
        private const int MaxRowsPerFile = 200;

        /// <summary>「品種」列的别名（比较时按 <see cref="NormalizeHeader"/> 归一化）。</summary>
        private static readonly string[] VarietyHeaders = new string[] { "品種", "品种" };

        /// <summary>「部品名」列的别名（<c>部品名称</c> / <c>品名</c> / <c>名称</c> 的写法也认）。</summary>
        private static readonly string[] PartNameHeaders = new string[] { "部品名", "部品名称", "品名", "名称" };

        /// <summary>「枠区分」列的别名。</summary>
        private static readonly string[] FrameKindHeaders = new string[] { "枠区分", "框区分" };

        /// <summary>
        /// 「部品コード」列的别名（<c>部品ｺｰﾄﾞ</c> 归一化后与 <c>部品コード</c> 相同；
        /// <c>品番</c> / <c>型番</c> / <c>型式</c> 是常见写法）。
        /// </summary>
        private static readonly string[] PartCodeHeaders = new string[]
        {
            "部品コード", "品番", "型番", "型式"
        };

        /// <summary>「追加対応特注寸法」列的别名（没有这一列时该字段留空）。</summary>
        private static readonly string[] SpecialSizeHeaders = new string[] { "追加対応特注寸法", "特注寸法" };

        /// <summary>送给 AI 的明细最多多少行（超出截断，并在提示词里注明）。</summary>
        private const int MaxRowsForPrompt = 120;

        /// <summary>一个单元格最多多少字符。</summary>
        private const int MaxCellLength = 120;

        /// <summary>AI 回复解析失败时，日志里显示的原文长度。</summary>
        private const int ShortTextLength = 160;

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private static readonly JavaScriptSerializer Serializer = CreateSerializer();

        /// <summary>一条从 Excel 部品表读出来的明细（写进 json 之前，只在本类内部用）。</summary>
        private sealed class TzRow
        {
            public string Variety;

            public string PartName;

            public string FrameKind;

            public string PartCode;

            public string SpecialSize;

            /// <summary>Excel 里的行号（1 起）。</summary>
            public int Row;

            /// <summary>来源表名。</summary>
            public string Sheet;

            /// <summary>
            /// <c>true</c> = 规则解析的部品表里对不上这一条，字段是 <b>AI 从整表原文里读出来的</b>
            /// （Excel 排版千差万别，规则认不出部品表时只能以 AI 读到的为准）。
            /// </summary>
            public bool FromAi;
        }

        /// <summary>
        /// 执行第 0 步：读 <paramref name="pdfFolder"/> 下第 5 位是 <c>Z</c> 的 Excel，
        /// 用 AI 判定是否把部品追加进 <c>&lt;classifyDirectory&gt;\特注品.json</c>。
        /// <para>
        /// <b>只处理 json 里还没有的文件</b>：某个 Excel 在 <c>特注品.json</c> 里已经有条目时，
        /// 这个文件连 Excel 都不读、也不送 AI（见 <see cref="CountExistingForFile"/>）。
        /// </para>
        /// </summary>
        /// <param name="pdfFolder">PDF 文件夹（= 分解输出根目录的上一级）。</param>
        /// <param name="classifyDirectory">分类目录（<c>&lt;输出根目录&gt;\classify</c>）。</param>
        /// <param name="options">已配置的 AI 接口参数（未配置时本方法直接返回，不动 json）。</param>
        /// <param name="onLog">日志回调（kind, message）。</param>
        public static CmdTzJsonMaintainResult Run(string pdfFolder, string classifyDirectory,
            AiApiOptions options, Action<LogKind, string> onLog)
        {
            CmdTzJsonMaintainResult result = new CmdTzJsonMaintainResult();

            if (string.IsNullOrWhiteSpace(pdfFolder) || !Directory.Exists(pdfFolder))
            {
                AddNote(result.Notes, "找不到 PDF 文件夹（" + (pdfFolder ?? "空") + "），跳过");
                return result;
            }

            if (string.IsNullOrWhiteSpace(classifyDirectory))
            {
                AddNote(result.Notes, "分类目录为空，跳过");
                return result;
            }

            string jsonPath = Path.Combine(classifyDirectory, TzJsonFileName);
            result.TzJsonPath = jsonPath;

            // ── 读现有 特注品.json（读不动就整步跳过：宁可不动，也不要把人工维护的内容清掉）──
            Dictionary<string, object> root = null;
            ArrayList items = new ArrayList();
            Dictionary<string, bool> existingKeys = new Dictionary<string, bool>(StringComparer.Ordinal);

            // 不存在 = 本次走「作成名」：没有可追加的对象，让 AI 从部品表建一份新的
            bool jsonExists = File.Exists(jsonPath);
            bool createMode = !jsonExists;

            result.JsonExistedAtStart = jsonExists;

            if (jsonExists)
            {
                try
                {
                    object parsed = Serializer.DeserializeObject(File.ReadAllText(jsonPath, Encoding.UTF8));
                    root = parsed as Dictionary<string, object>;

                    if (root == null)
                    {
                        AddNote(result.Notes, TzJsonFileName + " 顶层不是 JSON 对象（" + jsonPath +
                            "），本步骤跳过（不覆盖原文件）");
                        return result;
                    }

                    object rawItems;

                    if (root.TryGetValue(ItemKey, out rawItems))
                    {
                        IEnumerable sequence = rawItems as IEnumerable;

                        if (sequence != null && !(rawItems is string))
                        {
                            foreach (object item in sequence)
                            {
                                if (item != null)
                                {
                                    items.Add(item);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AddNote(result.Notes, TzJsonFileName + " 读取失败（" + ex.GetType().Name + "：" +
                        ex.Message + "），本步骤跳过（不覆盖原文件）");
                    return result;
                }
            }
            else
            {
                AddNote(result.Notes, "还没有 " + TzJsonFileName + "（" + jsonPath +
                    "）：本次按「作成名」让 AI 从部品表建一份新的");
            }

            result.ExistingRowCount = items.Count;

            foreach (object item in items)
            {
                Dictionary<string, object> map = item as Dictionary<string, object>;

                if (map != null)
                {
                    existingKeys[BuildKey(ReadText(map, FieldKeys[0]), ReadText(map, FieldKeys[1]),
                        ReadText(map, FieldKeys[2]), ReadText(map, FieldKeys[3]),
                        ReadText(map, FieldKeys[4]))] = true;
                }
            }

            // ── AI 客户端（未配置密钥时不动 json，只记日志）──
            ChatCompletionClient client;

            try
            {
                client = new ChatCompletionClient(options);
            }
            catch (Exception ex)
            {
                AddNote(result.Notes, "AI 不可用（" + ex.Message + "），第 0 步跳过（不动 " +
                    TzJsonFileName + "）");
                return result;
            }

            TzJsonMaintainPromptSet prompts = TzJsonMaintainPromptStore.Load();

            // 提示语若是旧版（没有 {{JSON_STATE}}），作成名就只能靠系统提示词里的规则兜着
            if (!prompts.UserTemplate.Contains("{{JSON_STATE}}"))
            {
                PartAnalysisService.ReportLog(onLog, LogKind.Warning,
                    "  提示语里没有 {{JSON_STATE}}（可能是旧版）：作成名只靠系统提示词，" +
                    "建议把最新的 Prompts\\TzJsonMaintain.Prompt.txt 放到 exe 目录的 Prompts 下");
            }

            // 旧版提示语没有 {{EXCEL_DUMP}}：整表原文会附在提示词最后（仍能送出去，但位置不理想）
            if (!prompts.UserTemplate.Contains("{{EXCEL_DUMP}}"))
            {
                PartAnalysisService.ReportLog(onLog, LogKind.Warning,
                    "  提示语里没有 {{EXCEL_DUMP}}（可能是旧版）：整表原文会被附在提示词最后，" +
                    "建议把最新的 Prompts\\TzJsonMaintain.Prompt.txt 放到 exe 目录的 Prompts 下");
            }

            // ── 扫 PDF 文件夹里的特注品 Excel ──
            List<string> files = FindExcelFiles(pdfFolder, result);

            // 扫描结果先写日志：文件不在这一层 / 名字不是特注品时，日志里一眼能看出来
            PartAnalysisService.ReportLog(onLog, LogKind.Info, string.Format(
                "  扫描结果：{0} 下直接放着 Excel {1} 个，其中特注品（文件名第 {2} 位是 {3}）{4} 个",
                pdfFolder, result.ScannedFileCount, CmdSpecialProductRule.DrawingNoPosition,
                CmdSpecialProductRule.DrawingNoMark, files.Count));

            if (files.Count > 0)
            {
                PartAnalysisService.ReportLog(onLog, LogKind.Info, "  特注品文件：" + DescribeFileNames(files));
            }

            if (files.Count == 0)
            {
                AddNote(result.Notes, string.Format(
                    "没有文件名第 {0} 位是 {1} 的 Excel（{2} 下直接放着 .xls/.xlsx/.xlsm 共 {3} 个，" +
                    "子文件夹不扫描），{4} 不变",
                    CmdSpecialProductRule.DrawingNoPosition, CmdSpecialProductRule.DrawingNoMark,
                    pdfFolder, result.ScannedFileCount, TzJsonFileName));

                return result;
            }

            int addedTotal = 0;

            foreach (string file in files)
            {
                string fileName = Path.GetFileName(file);
                string drawingNo = Path.GetFileNameWithoutExtension(file);
                List<string> notes = new List<string>();
                List<TzRow> rows;

                // ── 特注品.json 里已经有这个文件的条目 → 这个文件不用再处理 ──
                // （要求：只对「json 里还没有的文件」让 AI 读取并补足 json。
                //   已经登记过的文件再送 AI 也只会被去重逻辑挡掉，白花时间和 token）
                int existingForThisFile = CountExistingForFile(items, fileName);

                if (existingForThisFile > 0)
                {
                    result.ExistingFileSkippedCount++;
                    result.ExistingFileRowCount += existingForThisFile;

                    PartAnalysisService.ReportLog(onLog, LogKind.Info, string.Format(
                        "  跳过：{0} 已在 {1} 里（{2} 条明细）→ 不读 Excel、不送 AI",
                        fileName, TzJsonFileName, existingForThisFile));

                    continue;
                }

                // 进度直接写日志：result.Notes 要等整个方法返回才统一输出，
                // AI 调用期间日志一片空白，会让人以为这一步没在跑
                PartAnalysisService.ReportLog(onLog, LogKind.Info, "  读部品表：" + fileName);

                try
                {
                    rows = ReadPartTable(file, notes);
                }
                catch (Exception ex)
                {
                    result.AiFailedFileCount++;
                    AddNote(result.Notes, fileName + "：读取失败（" + ex.GetType().Name + "：" +
                        ex.Message + "），跳过");
                    continue;
                }

                // ── 整表原文：现场排版千差万别，规则认不出部品表时，主依据就是这份原文 ──
                ExcelSheetDumpResult dump = ExcelSheetDumper.Read(file, notes);

                PartAnalysisService.ReportLog(onLog, LogKind.Info, string.Format(
                    "  整表原文：工作表 {0} 张（{1}），可送 AI 的文字 {2} 行{3}",
                    dump.SheetCount, DescribeSheetNames(dump), dump.RowCount,
                    dump.Truncated ? "（已按字符上限截断）" : string.Empty));

                // 读部品表时最多试两条路（「カメラ」表 → 第一张表），同一个原因会出现两次：
                // 读取器自己的提示已经带文件名时不重复加前缀，并去重，日志才看得清
                HashSet<string> noteSet = new HashSet<string>(StringComparer.Ordinal);

                foreach (string note in notes)
                {
                    string text = note.StartsWith(fileName, StringComparison.OrdinalIgnoreCase)
                        ? note
                        : fileName + "：" + note;

                    if (noteSet.Add(text))
                    {
                        AddNote(result.Notes, text);
                    }
                }

                if (rows.Count == 0 && !dump.HasText)
                {
                    result.NoPartTableFileCount++;

                    // 只有「Excel 一个字都读不出来」才不送 AI：
                    // 规则没解析出明细不算（排版认不出时让 AI 读整表原文）
                    AddNote(result.Notes, fileName +
                        "：这个文件既没解析出部品表明细，整表也没读到任何文字" +
                        "（可能不是真正的 Excel 或文件已损坏），没有可送 AI 的内容，跳过");

                    continue;
                }

                if (rows.Count == 0)
                {
                    // 规则认不出表头是常态（现场排版千奇百怪）：不跳过，让 AI 从整表原文里自己认
                    PartAnalysisService.ReportLog(onLog, LogKind.Info,
                        "  规则没解析出部品表明细（表头认不出）→ 改成按整表原文让 AI 自己认");
                }

                // 同一个文件已经登记的条目（给 AI 参考，避免重复）
                List<string> existingForFile = CollectExistingForFile(items, fileName);
                string reply;

                try
                {
                    result.AiCallCount++;

                    // 送出去之前先写一行：AI 回复要等好几秒，日志里要能看到「正在等 AI」
                    PartAnalysisService.ReportLog(onLog, LogKind.Info, string.Format(
                        "  AI 判定中（{2}）：{0}（整表原文 {1} 行）…", fileName, dump.RowCount,
                        createMode ? "作成名：新建 特注品.json" : "追加判定"));

                    reply = client.Complete(prompts.SystemPrompt,
                        BuildUserPrompt(prompts.UserTemplate, drawingNo, rows, existingForFile,
                            createMode, dump.Text));
                }
                catch (Exception ex)
                {
                    result.AiFailedFileCount++;
                    AddNote(result.Notes, fileName + "：AI 调用失败（" + ex.Message +
                        "），这个文件跳过（json 不变）");
                    continue;
                }

                Dictionary<string, object> answer = ParseJsonReply(reply);

                if (answer == null)
                {
                    result.AiFailedFileCount++;
                    AddNote(result.Notes, fileName + "：AI 回复不是 JSON（" + Shorten(reply) +
                        "），这个文件跳过（json 不变）");
                    continue;
                }

                string reason = ReadRawText(answer, "reason");
                bool add = ReadBool(answer, "add");
                // 规则解析结果里对得上 → 用部品表原文；对不上（规则没认出这张表）→ 用 AI 读到的原文
                int aiRowCount = 0;
                List<TzRow> accepted = AcceptRows(answer, rows, out aiRowCount);

                int rawItemCount = 0;

                foreach (object ignored in ReadArray(answer, "items"))
                {
                    rawItemCount++;
                }

                // 逐文件把 AI 的判定结果写进日志：不然只能看到「没有作成」，不知道为什么
                PartAnalysisService.ReportLog(onLog, LogKind.Info, string.Format(
                    "  AI 返回：add={0}，items {1} 条，可用 {2} 条（其中 AI 直接给出字段 {3} 条）{4}",
                    add, rawItemCount, accepted.Count, aiRowCount,
                    reason.Length == 0 ? string.Empty : "，理由：" + Shorten(reason)));

                bool fallbackUsed = false;

                if (accepted.Count == 0 && createMode && rows.Count > 0)
                {
                    // 作成名兜底：json 还不存在时，AI 一条都没点名（或点名对不上部品表）就按
                    // 「最前面 · 特注品 CSV 生成」同口径，把这份部品表的有效明细原样当成新建条目，
                    // 保证 特注品.json 一定能作成（明细已经排除「追加不要」行与空行）
                    accepted = new List<TzRow>(rows);
                    fallbackUsed = true;

                    PartAnalysisService.ReportLog(onLog, LogKind.Warning, string.Format(
                        "  AI 没给出可用条目 → 作成名兜底：把部品表的 {0} 条明细原样写进新建的 {1}",
                        accepted.Count, TzJsonFileName));

                    AddNote(result.Notes, string.Format(
                        "{0}：作成名兜底 —— AI 没给出可用条目，按部品表原样列出 {1} 条" +
                        "（与「特注品 CSV 生成」同口径）", fileName, accepted.Count));
                }
                else if (!add && accepted.Count == 0)
                {
                    result.AiRejectedFileCount++;
                    AddNote(result.Notes, string.Format("{0}：AI 判定不追加（{1} 条部品表明细）{2}",
                        fileName, rows.Count, reason.Length == 0 ? string.Empty : "：" + reason));
                    continue;
                }

                int added = 0;
                int skipped = 0;

                foreach (TzRow row in accepted)
                {
                    string key = BuildKey(row.Variety, row.PartName, row.FrameKind, row.PartCode,
                        row.SpecialSize);

                    if (existingKeys.ContainsKey(key))
                    {
                        skipped++;
                        continue;
                    }

                    existingKeys[key] = true;
                    items.Add(BuildJsonEntry(row, fileName));
                    added++;
                }

                result.SkippedDuplicateCount += skipped;

                if (added > 0)
                {
                    result.AiAddedFileCount++;
                    addedTotal += added;
                    AddNote(result.Notes, string.Format(
                        fallbackUsed ? "{0}：作成名兜底 → 部品表原样 {1} 条{2}{3}" :
                        createMode ? "{0}：作成名 → AI 列出 {1} 条{2}{3}" : "{0}：AI 判定追加 {1} 条{2}{3}",
                        fileName, added,
                        skipped > 0 ? "（重复跳过 " + skipped + " 条）" : string.Empty,
                        reason.Length == 0 ? string.Empty : "：" + reason));
                }
                else
                {
                    result.AiRejectedFileCount++;
                    AddNote(result.Notes, string.Format(
                        "{0}：AI 判定要追加，但没有可写的条目（全空 / 全部重复 {1} 条）{2}",
                        fileName, skipped, reason.Length == 0 ? string.Empty : "：" + reason));
                }
            }

            result.AddedRowCount = addedTotal;

            if (addedTotal == 0)
            {
                AddNote(result.Notes, createMode
                    ? TzJsonFileName + " 还不存在，AI 也没给出可用的条目 → 本次没有作成（文件仍未创建）"
                    : TzJsonFileName + " 没有变化（一条都没追加，不写文件）" +
                      (result.ExistingFileSkippedCount > 0
                          ? "；" + result.ExistingFileSkippedCount + " 个特注品文件已在 json 里，已跳过"
                          : string.Empty));

                return result;
            }

            // ── 写回 特注品.json（保留其它顶层键，只替换 items）──
            if (root == null)
            {
                root = new Dictionary<string, object>();
            }

            root[ItemKey] = items;

            // ── 顶层计数器同步（TzJsonBuilder 写的 item_count / file_count）──
            SyncCounters(root, items, onLog);

            try
            {
                Directory.CreateDirectory(classifyDirectory);
                File.WriteAllText(jsonPath, FormatJson(root), Utf8NoBom);
                result.Written = true;

                if (createMode)
                {
                    // 原来没有这个文件：这一写就是「作成」
                    result.JsonCreated = true;
                    AddNote(result.Notes, TzJsonFileName + " 不存在 → 本次新建完成（" + addedTotal + " 条）");
                }
            }
            catch (Exception ex)
            {
                result.AddedRowCount = 0;
                AddNote(result.Notes, TzJsonFileName + " 写入失败（" + ex.GetType().Name + "：" +
                    ex.Message + "）：本次追加的 " + addedTotal + " 条没有保存");
            }

            return result;
        }

        /// <summary>
        /// 写回前同步顶层的计数器 <c>item_count</c> / <c>file_count</c>
        /// （键由 <c>CadPartSplitter\TzJsonBuilder</c> 写出，
        /// <see cref="CmdSpecialProductCsvExporter"/> 也按同一口径维护）。
        /// <para>
        /// 不同步就会留下「<c>item_count: 23</c>，<c>items</c> 实际 37 条」这种旧值：
        /// 第 0 步只替换 <see cref="ItemKey"/>，计数器是别的步骤写的，不跟着改就永远是旧数。
        /// </para>
        /// <para>
        /// <c>file_count</c> <b>只往上调</b>：这里看不到「这次扫到几个 Excel」，只能数
        /// <c>items</c> 里出现过几个 <c>file</c>；改小会把「扫到了但一条明细都没有」的文件算丢。
        /// </para>
        /// </summary>
        private static void SyncCounters(IDictionary<string, object> root, IEnumerable items,
            Action<LogKind, string> onLog)
        {
            if (root == null || items == null)
            {
                return;
            }

            int count = 0;
            Dictionary<string, bool> files = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (object item in items)
            {
                count++;

                Dictionary<string, object> map = item as Dictionary<string, object>;

                if (map == null)
                {
                    continue;
                }

                string file = ReadRawText(map, FileKey);

                if (!string.IsNullOrWhiteSpace(file))
                {
                    files[file] = true;
                }
            }

            int oldItemCount = ReadCounter(root, ItemCountKey);
            int oldFileCount = ReadCounter(root, FileCountKey);
            List<string> changes = new List<string>();

            if (root.ContainsKey(ItemCountKey) && oldItemCount != count)
            {
                root[ItemCountKey] = count;
                changes.Add(string.Format("item_count {0} → {1}", oldItemCount, count));
            }

            if (root.ContainsKey(FileCountKey) && files.Count > oldFileCount)
            {
                root[FileCountKey] = files.Count;
                changes.Add(string.Format("file_count {0} → {1}", oldFileCount, files.Count));
            }

            if (changes.Count > 0)
            {
                PartAnalysisService.ReportLog(onLog, LogKind.Info,
                    "  计数器同步：" + string.Join("，", changes.ToArray()));
            }
        }

        /// <summary>读顶层计数器的整数值（没有 / 读不动时返回 0）。</summary>
        private static int ReadCounter(IDictionary<string, object> root, string key)
        {
            object raw;

            if (root == null || !root.TryGetValue(key, out raw) || raw == null)
            {
                return 0;
            }

            try
            {
                return Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// 列出 <paramref name="folder"/> 下的 Excel，返回其中「文件名第 5 位是 Z」的那些（按文件名排序）。
        /// 同时把扫描总数写进 <paramref name="result"/>。
        /// </summary>
        private static List<string> FindExcelFiles(string folder, CmdTzJsonMaintainResult result)
        {
            List<string> special = new List<string>();

            foreach (string extension in ExcelExtensions)
            {
                string[] found;

                try
                {
                    found = Directory.GetFiles(folder, "*" + extension, SearchOption.TopDirectoryOnly);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (string file in found)
                {
                    string fileName = Path.GetFileName(file);

                    // ~$ 开头是 Excel 打开时留下的临时文件
                    if (fileName.StartsWith("~$", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    result.ScannedFileCount++;

                    if (CmdSpecialProductRule.IsSpecialProductDrawingNo(
                        Path.GetFileNameWithoutExtension(file)))
                    {
                        special.Add(file);
                    }
                }
            }

            special.Sort(StringComparer.OrdinalIgnoreCase);
            result.SpecialProductFileCount = special.Count;

            return special;
        }

        /// <summary>日志用：把文件名列表压成一行（最多 10 个）。</summary>
        private static string DescribeFileNames(IList<string> files)
        {
            if (files == null || files.Count == 0)
            {
                return "（无）";
            }

            const int MaxShown = 10;
            int limit = Math.Min(files.Count, MaxShown);
            StringBuilder builder = new StringBuilder();

            for (int index = 0; index < limit; index++)
            {
                if (index > 0)
                {
                    builder.Append('、');
                }

                builder.Append(Path.GetFileName(files[index]));
            }

            if (files.Count > limit)
            {
                builder.Append("…（其余 ").Append(files.Count - limit).Append(" 个）");
            }

            return builder.ToString();
        }

        /// <summary>
        /// 日志用：把表的前 <paramref name="maxRows"/> 行压成
        /// 「行1: a | b | c ／ 行2: …」（每格最多 24 字，尾部空列丢掉）。
        /// 只在「表头认不出」时用，方便对着日志核对实际排版。
        /// </summary>
        private static string DescribeRows(IList<string[]> sheet, int maxRows)
        {
            if (sheet == null || sheet.Count == 0)
            {
                return "（读不到任何行）";
            }

            StringBuilder builder = new StringBuilder();
            int limit = Math.Min(sheet.Count, maxRows);

            for (int index = 0; index < limit; index++)
            {
                if (index > 0)
                {
                    builder.Append(" ／ ");
                }

                builder.Append('行').Append(index + 1).Append(": ");

                string[] cells = sheet[index];
                int last = cells == null ? -1 : cells.Length - 1;

                while (last >= 0 && string.IsNullOrWhiteSpace(cells[last]))
                {
                    last--;
                }

                if (last < 0)
                {
                    builder.Append("（空行）");
                    continue;
                }

                for (int column = 0; column <= last; column++)
                {
                    if (column > 0)
                    {
                        builder.Append(" | ");
                    }

                    string text = NormalizeCell(cells[column]);

                    builder.Append(text.Length <= 24 ? text : text.Substring(0, 24) + "…");
                }
            }

            if (sheet.Count > limit)
            {
                builder.Append(" ／ …（共 ").Append(sheet.Count).Append(" 行）");
            }

            return builder.ToString();
        }

        /// <summary>
        /// 读一个 Excel 的部品表：优先 <see cref="PreferredSheetName"/>（カメラ），
        /// 没有这张表 / 这张表读不出部品时退回第一张表。
        /// 两条路都读不出明细时，把表的前几行写进 <paramref name="notes"/>（日志里能直接看到实际排版）。
        /// </summary>
        private static List<TzRow> ReadPartTable(string filePath, IList<string> notes)
        {
            List<string[]> sheet = XlsxSheetReader.ReadSheet(filePath, PreferredSheetName, notes);
            List<TzRow> rows = ExtractRows(sheet, PreferredSheetName);

            if (rows.Count > 0)
            {
                return rows;
            }

            List<string[]> first = XlsxSheetReader.ReadFirstSheet(filePath, notes);
            rows = ExtractRows(first, FirstSheetName);

            if (rows.Count > 0)
            {
                AddNote(notes, string.Format("「{0}」表没读出明细 → 改用第一张工作表（{1} 条明细）",
                    PreferredSheetName, rows.Count));

                return rows;
            }

            // 还是读不出：把表头区域原文打出来。只写「找不到表头」的话，
            // 日志里看不到实际排版，没法判断是读取问题还是列名不同
            if (sheet != null && sheet.Count > 0)
            {
                AddNote(notes, string.Format("「{0}」表共 {1} 行，前几行：{2}",
                    PreferredSheetName, sheet.Count, DescribeRows(sheet, 8)));
            }

            if (first != null && first.Count > 0)
            {
                AddNote(notes, string.Format("第一张工作表共 {0} 行，前几行：{1}",
                    first.Count, DescribeRows(first, 8)));
            }

            return rows;
        }

        /// <summary>
        /// 从一张表里按列名找表头，抽出 品種 / 部品名 / 枠区分 / 部品コード / 追加対応特注寸法 五列。
        /// 表头找不到 / 一条明细都没有时返回空表。
        /// </summary>
        private static List<TzRow> ExtractRows(IList<string[]> sheet, string sheetName)
        {
            List<TzRow> rows = new List<TzRow>();

            if (sheet == null || sheet.Count == 0)
            {
                return rows;
            }

            int[] columns = null;
            int headerIndex = -1;
            int scanLimit = Math.Min(sheet.Count, HeaderScanRows);

            for (int index = 0; index < scanLimit; index++)
            {
                int[] map = MapHeader(sheet[index]);

                if (map != null)
                {
                    columns = map;
                    headerIndex = index;
                    break;
                }
            }

            if (columns == null)
            {
                return rows;
            }

            string lastVariety = string.Empty;

            for (int index = headerIndex + 1; index < sheet.Count; index++)
            {
                string[] cells = sheet[index];

                if (cells == null)
                {
                    continue;
                }

                // 又遇到一块表的表头（一份工作簿里常有多块）：换成新的列映射接着读。
                // 这里要「部品名 + 部品コード」两列都认出来才算表头行 —— 表头匹配放宽之后，
                // 明细行里某个格子碰巧写着「部品名…」时不能被当成表头吃掉
                int[] map = IsTableHeaderRow(cells) ? MapHeader(cells) : null;

                if (map != null)
                {
                    columns = map;
                    continue;
                }

                string variety = Cell(cells, columns[0]);
                string partName = Cell(cells, columns[1]);
                string frameKind = Cell(cells, columns[2]);
                string partCode = Cell(cells, columns[3]);
                string specialSize = Cell(cells, columns[4]);

                // 品種 空着（合并单元格的续行）时沿用上一行
                if (variety.Length == 0)
                {
                    variety = lastVariety;
                }
                else
                {
                    lastVariety = variety;
                }

                if (partName.Length == 0 && partCode.Length == 0)
                {
                    continue;
                }

                // 「追加不要」这类字样：不送 AI，也不写进 json
                if (CmdTzPartCollector.ContainsNoNeedMark(variety) ||
                    CmdTzPartCollector.ContainsNoNeedMark(partName) ||
                    CmdTzPartCollector.ContainsNoNeedMark(frameKind) ||
                    CmdTzPartCollector.ContainsNoNeedMark(partCode) ||
                    CmdTzPartCollector.ContainsNoNeedMark(specialSize))
                {
                    continue;
                }

                TzRow row = new TzRow();
                row.Variety = variety;
                row.PartName = partName;
                row.FrameKind = frameKind;
                row.PartCode = partCode;
                row.SpecialSize = specialSize;
                row.Row = index + 1;
                row.Sheet = sheetName;

                rows.Add(row);

                if (rows.Count >= MaxRowsPerFile)
                {
                    break;
                }
            }

            return rows;
        }

        /// <summary>
        /// 认出表头行：返回五列各自的列号（0 起），认不出时返回 <c>null</c>。
        /// <para>
        /// 口径与「最前面 · 特注品 CSV 生成」（<see cref="CmdSpecialProductCsvExporter"/>）一致：
        /// 每列按<b>别名</b>比对，比对前先 <see cref="NormalizeHeader"/>（NFKC：全角→半角、半角片假名→全角片假名，
        /// 再去空白、转大写），所以 <c>部品ｺｰﾄﾞ</c> / <c>品 種</c> / <c>品番</c> 这些写法都能对上；
        /// 整格等于列名或以列名开头（表头写成「部品名 ： 縦枠上部スペーサー」那种）都算。
        /// </para>
        /// <para>
        /// 最低要求：至少认出一列「部品名」或「部品コード」。品種 / 枠区分 / 追加対応特注寸法
        /// 缺列时那一列按空值处理（部品表没有特注寸法列时照样能把部品列出来）。
        /// </para>
        /// </summary>
        private static int[] MapHeader(string[] cells)
        {
            if (cells == null || cells.Length == 0)
            {
                return null;
            }

            int[] map = MapHeaderCore(cells, false);

            // 严格比对（整格等于列名 / 以列名开头）认不出来时，再放宽到「格子里包含列名」
            // —— 表头写成「品種※」「①部品コード（型番）」这种带修饰的样子也能认出来
            if (map == null)
            {
                map = MapHeaderCore(cells, true);
            }

            return map;
        }

        /// <summary><see cref="MapHeader"/> 的实现（<paramref name="allowContains"/> = 允许「包含」命中）。</summary>
        private static int[] MapHeaderCore(string[] cells, bool allowContains)
        {
            int[] map = new int[FieldKeys.Length];

            map[0] = IndexOfHeader(cells, VarietyHeaders, allowContains);
            map[1] = IndexOfHeader(cells, PartNameHeaders, allowContains);
            map[2] = IndexOfHeader(cells, FrameKindHeaders, allowContains);
            map[3] = IndexOfHeader(cells, PartCodeHeaders, allowContains);
            map[4] = IndexOfHeader(cells, SpecialSizeHeaders, allowContains);

            // 至少要认出「部品名」或「部品コード」，否则不算表头
            if (map[1] < 0 && map[3] < 0)
            {
                return null;
            }

            return map;
        }

        /// <summary>
        /// 这一行像不像「另一块表的表头」（读明细的循环里用）：要求「部品名」与「部品コード」
        /// 两列都能按严格口径（等于 / 开头）认出来，避免把只写着「部品名…」的明细行当成表头跳过。
        /// </summary>
        private static bool IsTableHeaderRow(string[] cells)
        {
            return IndexOfHeader(cells, PartNameHeaders, false) >= 0 &&
                IndexOfHeader(cells, PartCodeHeaders, false) >= 0;
        }

        /// <summary>
        /// 在一行表头里找别名命中的列号（0 起）；没有时返回 -1。
        /// 整格等于列名，或以列名开头（「部品名 ： …」）都算命中；
        /// <paramref name="allowContains"/> = <c>true</c> 时「包含」也算。
        /// </summary>
        private static int IndexOfHeader(string[] cells, string[] aliases, bool allowContains)
        {
            if (cells == null || aliases == null)
            {
                return -1;
            }

            for (int column = 0; column < cells.Length; column++)
            {
                string value = NormalizeHeader(cells[column]);

                if (value.Length == 0)
                {
                    continue;
                }

                foreach (string alias in aliases)
                {
                    string key = NormalizeHeader(alias);

                    if (key.Length == 0)
                    {
                        continue;
                    }

                    if (string.Equals(value, key, StringComparison.Ordinal) ||
                        value.StartsWith(key, StringComparison.Ordinal) ||
                        (allowContains && value.IndexOf(key, StringComparison.Ordinal) >= 0))
                    {
                        return column;
                    }
                }
            }

            return -1;
        }

        /// <summary>
        /// 表头归一化（只用于比对）：NFKC（全角 → 半角、半角片假名 → 全角片假名）
        /// → 去掉所有空白（含全角空格）→ 转大写。
        /// </summary>
        private static string NormalizeHeader(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            string text = value.Normalize(NormalizationForm.FormKC);
            StringBuilder builder = new StringBuilder(text.Length);

            foreach (char ch in text)
            {
                if (char.IsWhiteSpace(ch))
                {
                    continue;
                }

                builder.Append(char.ToUpperInvariant(ch));
            }

            return builder.ToString();
        }

        /// <summary>取一格（列号越界 / null 都返回空串）。</summary>
        private static string Cell(string[] cells, int index)
        {
            if (cells == null || index < 0 || index >= cells.Length || cells[index] == null)
            {
                return string.Empty;
            }

            return NormalizeCell(cells[index]);
        }

        /// <summary>把工作表名拼成一行日志文本（没有时返回「无」）。</summary>
        private static string DescribeSheetNames(ExcelSheetDumpResult dump)
        {
            if (dump == null || dump.SheetNames == null || dump.SheetNames.Count == 0)
            {
                return "无";
            }

            return string.Join(" / ", dump.SheetNames.ToArray());
        }

        /// <summary>取一个整数字段（不是数字时返回 0，调用方按「不知道行号」处理）。</summary>
        private static int ReadInt(Dictionary<string, object> map, string key)
        {
            string text = ReadText(map, key);
            int parsed;

            if (text.Length > 0 &&
                int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }

            return 0;
        }

        /// <summary>
        /// 数一下 <c>特注品.json</c> 里同一份 Excel 已经登记了多少条明细
        /// （按条目的 <c>file</c> 字段比对，忽略大小写）。
        /// <para>
        /// 用途：<b>0 条 = json 里还没有这个文件</b> → 才读 Excel 并让 AI 补足；
        /// 大于 0 条 → 这个文件已经登记过，第 0 步直接跳过。
        /// 与 <see cref="CollectExistingForFile"/> 不同，这里<b>不按提示词上限截断</b>，数字是真实条数。
        /// </para>
        /// </summary>
        private static int CountExistingForFile(IEnumerable items, string fileName)
        {
            if (items == null || string.IsNullOrEmpty(fileName))
            {
                return 0;
            }

            int count = 0;

            foreach (object item in items)
            {
                Dictionary<string, object> map = item as Dictionary<string, object>;

                if (map == null)
                {
                    continue;
                }

                if (string.Equals(ReadRawText(map, FileKey), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>收集 特注品.json 里同一份 Excel 已经登记的条目（给 AI 参考，避免重复）。</summary>
        private static List<string> CollectExistingForFile(IEnumerable items, string fileName)
        {
            List<string> list = new List<string>();

            foreach (object item in items)
            {
                Dictionary<string, object> map = item as Dictionary<string, object>;

                if (map == null)
                {
                    continue;
                }

                if (!string.Equals(ReadRawText(map, FileKey), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                list.Add(string.Format("{0} / {1} / {2}", ReadText(map, FieldKeys[0]),
                    ReadText(map, FieldKeys[1]), ReadText(map, FieldKeys[3])));
            }

            if (list.Count > MaxRowsForPrompt)
            {
                list = list.GetRange(0, MaxRowsForPrompt);
            }

            return list;
        }

        /// <summary>
        /// 把 AI 回复里的 <c>items</c> 落地成要写进 json 的行：
        /// <list type="bullet">
        ///   <item>能在<b>规则解析结果</b>里找到同一行（品種 + 部品名 + 部品コード → 逐步放宽）时，
        ///         用<b>部品表原文</b>（最可靠）；</item>
        ///   <item>找不到时说明规则没认出这张表（排版千奇百怪），此时<b>直接用 AI 从整表原文里读到的字段</b>
        ///         （<see cref="TzRow.FromAi"/> = <c>true</c>，并记进 <paramref name="aiRowCount"/>）——
        ///         这正是「把文件交给 AI 读」的意义，不能再按老规则把它丢掉；</item>
        ///   <item>「部品名」和「部品コード」都空着的条目一律丢弃（没有可定位的部品）。</item>
        /// </list>
        /// </summary>
        /// <param name="answer">AI 回复的 JSON 对象。</param>
        /// <param name="known">规则解析出来的部品表明细（可能为空）。</param>
        /// <param name="aiRowCount">返回：其中「字段由 AI 直接给出」的条数。</param>
        private static List<TzRow> AcceptRows(Dictionary<string, object> answer, IList<TzRow> known,
            out int aiRowCount)
        {
            List<TzRow> accepted = new List<TzRow>();
            IEnumerable<object> items = ReadArray(answer, "items");

            aiRowCount = 0;

            foreach (object item in items)
            {
                Dictionary<string, object> map = item as Dictionary<string, object>;

                if (map == null)
                {
                    continue;
                }

                string variety = ReadText(map, FieldKeys[0]);
                string partName = ReadText(map, FieldKeys[1]);
                string partCode = ReadText(map, FieldKeys[3]);

                if (partName.Length == 0 && partCode.Length == 0)
                {
                    continue;
                }

                TzRow matched = MatchRow(known, variety, partName, partCode);

                if (matched != null)
                {
                    if (!accepted.Contains(matched))
                    {
                        accepted.Add(matched);
                    }

                    continue;
                }

                TzRow aiRow = new TzRow();

                aiRow.Variety = variety;
                aiRow.PartName = partName;
                aiRow.FrameKind = ReadText(map, FieldKeys[2]);
                aiRow.PartCode = partCode;
                aiRow.SpecialSize = ReadText(map, FieldKeys[4]);
                aiRow.Sheet = ReadText(map, "sheet");
                aiRow.Row = ReadInt(map, "row");
                aiRow.FromAi = true;

                if (!ContainsSameAiRow(accepted, aiRow))
                {
                    accepted.Add(aiRow);
                    aiRowCount++;
                }
            }

            return accepted;
        }

        /// <summary>
        /// AI 直接给出的条目里去重：品種 + 部品名 + 枠区分 + 部品コード + 追加対応特注寸法
        /// 全部相同才算同一条（与 <see cref="BuildKey"/> 同口径）。
        /// <para>
        /// 不能用「品種 + 部品名 + 部品コード」去重：同一部品在同一 品種 下会按特注 W 范围
        /// 出现多次（例如 I400Z10002 的 MZB976：<c>1025≦W＜1325</c> 与 <c>1325≦W≦1408</c>），
        /// 那样会在 AI 条目落地时就把后面的区间当成重复丢掉。
        /// </para>
        /// </summary>
        private static bool ContainsSameAiRow(IList<TzRow> rows, TzRow candidate)
        {
            string key = BuildKey(candidate.Variety, candidate.PartName, candidate.FrameKind,
                candidate.PartCode, candidate.SpecialSize);

            foreach (TzRow row in rows)
            {
                if (row.FromAi && string.Equals(
                    BuildKey(row.Variety, row.PartName, row.FrameKind, row.PartCode, row.SpecialSize),
                    key, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 在部品表里找 AI 点名的那一行：先按「品種 + 部品名 + 部品コード」，
        /// 再放宽到「部品名 + 部品コード」「部品コード」「部品名」。
        /// </summary>
        private static TzRow MatchRow(IList<TzRow> rows, string variety, string partName, string partCode)
        {
            if (rows == null || rows.Count == 0)
            {
                return null;
            }

            foreach (TzRow row in rows)
            {
                if (EqualsKey(row.Variety, variety) && EqualsKey(row.PartName, partName) &&
                    EqualsKey(row.PartCode, partCode))
                {
                    return row;
                }
            }

            foreach (TzRow row in rows)
            {
                if (partName.Length > 0 && EqualsKey(row.PartName, partName) &&
                    EqualsKey(row.PartCode, partCode))
                {
                    return row;
                }
            }

            foreach (TzRow row in rows)
            {
                if (partCode.Length > 0 && EqualsKey(row.PartCode, partCode))
                {
                    return row;
                }
            }

            foreach (TzRow row in rows)
            {
                if (partName.Length > 0 && EqualsKey(row.PartName, partName))
                {
                    return row;
                }
            }

            return null;
        }

        /// <summary>两个字段是否「同一个值」（去空白 + 忽略大小写）。</summary>
        private static bool EqualsKey(string left, string right)
        {
            return string.Equals(NormalizeKey(left), NormalizeKey(right), StringComparison.Ordinal);
        }

        /// <summary>把一条明细写成 特注品.json 的一项（键名与 <c>TzJsonBuilder</c> 一致）。</summary>
        private static Dictionary<string, object> BuildJsonEntry(TzRow row, string fileName)
        {
            Dictionary<string, object> entry = new Dictionary<string, object>();

            entry[FieldKeys[0]] = row.Variety ?? string.Empty;
            entry[FieldKeys[1]] = row.PartName ?? string.Empty;
            entry[FieldKeys[2]] = row.FrameKind ?? string.Empty;
            entry[FieldKeys[3]] = row.PartCode ?? string.Empty;
            entry[FieldKeys[4]] = row.SpecialSize ?? string.Empty;
            entry[FileKey] = fileName;
            entry[SheetKey] = row.Sheet ?? string.Empty;
            entry[RowKey] = row.Row;
            entry[SourceKey] = SourceAi;

            return entry;
        }

        /// <summary>
        /// 去重用的键：品種 + 部品名 + 枠区分 + 部品コード + 追加対応特注寸法（去空白 + 忽略大小写）。
        /// <para>
        /// <b>为什么把「枠区分 / 追加対応特注寸法」也算进键里</b>：同一部品在同一 品種 下
        /// 可能对应多个特注尺寸区间（例如 I400Z10002 的 MZB976：<c>1025≦W＜1325</c> 与
        /// <c>1325≦W≦1408</c>），只按 品種 + 部品名 + 部品コード 去重会把后面的区间当成重复丢掉。
        /// 这与 <c>TzJsonBuilder</c> 的条目键（表名 + 行号 + 品種 + 追加対応特注寸法）口径一致：
        /// 同一部品的不同尺寸区间要各留一条。
        /// </para>
        /// </summary>
        private static string BuildKey(string variety, string partName, string frameKind,
            string partCode, string specialSize)
        {
            return NormalizeKey(variety) + "\u0001" + NormalizeKey(partName) + "\u0001" +
                NormalizeKey(frameKind) + "\u0001" + NormalizeKey(partCode) + "\u0001" +
                NormalizeKey(specialSize);
        }

        /// <summary>键的归一化：去掉所有空白（含全角空格），转大写。</summary>
        private static string NormalizeKey(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(value.Length);

            foreach (char ch in value)
            {
                if (char.IsWhiteSpace(ch) || ch == '\u3000')
                {
                    continue;
                }

                builder.Append(ch);
            }

            return builder.ToString().ToUpperInvariant();
        }

        /// <summary>单元格归一化：换行 / 制表符压成空格 → 去首尾空白 → 截断。</summary>
        private static string NormalizeCell(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(value.Length);

            foreach (char ch in value)
            {
                builder.Append(ch == '\r' || ch == '\n' || ch == '\t' ? ' ' : ch);
            }

            string text = builder.ToString().Trim();

            if (text.Length > MaxCellLength)
            {
                text = text.Substring(0, MaxCellLength).Trim();
            }

            return text;
        }

        /// <summary>
        /// 拼用户提示词：替换 <c>{{DRAWING_NO}}</c> / <c>{{EXISTING_ITEMS}}</c> /
        /// <c>{{EXCEL_TABLE}}</c> / <c>{{EXCEL_DUMP}}</c> / <c>{{JSON_STATE}}</c>。
        /// </summary>
        /// <param name="template">用户提示词模板（外部文件或内置默认值）。</param>
        /// <param name="drawingNo">図番（Excel 文件名去扩展名）。</param>
        /// <param name="rows">规则解析出来的部品表明细（可能为空，仅供 AI 参照）。</param>
        /// <param name="existing">同一份 Excel 已在 特注品.json 里的条目。</param>
        /// <param name="createMode">
        /// <c>true</c> = 还没有 特注品.json，按「作成名」告诉 AI：这是新文件，把特注品条目全部列出。
        /// </param>
        /// <param name="excelDump">
        /// 整表原文（<see cref="ExcelSheetDumper"/> 导出：工作表名 + 行号 + 单元格原文）。
        /// <b>这才是 AI 认部品表的主要依据</b>——现场排版千差万别，规则解析结果经常是空的。
        /// </param>
        private static string BuildUserPrompt(string template, string drawingNo, IList<TzRow> rows,
            IList<string> existing, bool createMode, string excelDump)
        {
            StringBuilder table = new StringBuilder();

            table.Append("| ").Append(string.Join(" | ", FieldKeys)).Append(" |\n");
            table.Append("| --- | --- | --- | --- | --- |\n");

            int limit = Math.Min(rows.Count, MaxRowsForPrompt);

            for (int index = 0; index < limit; index++)
            {
                TzRow row = rows[index];

                table.Append("| ").Append(EscapeCell(row.Variety))
                    .Append(" | ").Append(EscapeCell(row.PartName))
                    .Append(" | ").Append(EscapeCell(row.FrameKind))
                    .Append(" | ").Append(EscapeCell(row.PartCode))
                    .Append(" | ").Append(EscapeCell(row.SpecialSize))
                    .Append(" |\n");
            }

            if (rows.Count == 0)
            {
                table.Append("\n（程序按列名规则没解析出明细 —— 请直接看「Excel 整表原文」自己认表）\n");
            }

            if (rows.Count > limit)
            {
                table.Append("\n（部品表还有 ").Append(rows.Count - limit)
                    .Append(" 行没列出，只列了前 ").Append(limit).Append(" 行）\n");
            }

            StringBuilder existingText = new StringBuilder();

            if (existing == null || existing.Count == 0)
            {
                existingText.Append("（没有）");
            }
            else
            {
                foreach (string line in existing)
                {
                    existingText.Append("- ").Append(line).Append('\n');
                }
            }

            string prompt = template ?? string.Empty;
            prompt = prompt.Replace("{{DRAWING_NO}}", drawingNo ?? string.Empty);
            prompt = prompt.Replace("{{EXISTING_ITEMS}}", existingText.ToString().TrimEnd());
            prompt = prompt.Replace("{{EXCEL_TABLE}}", table.ToString().TrimEnd());
            prompt = prompt.Replace("{{EXCEL_DUMP}}", string.IsNullOrEmpty(excelDump)
                ? "（整表没有读到任何文字）"
                : excelDump);
            prompt = prompt.Replace("{{JSON_STATE}}", createMode
                ? "还没有 特注品.json（作成名）：这是一份新文件，请把这份部品表里所有属于特注品的条目全部列出来。"
                : "特注品.json 已存在：只列出还需要追加的条目，上面列出的「已经有的条目」不要重复给。");

            // 旧版提示语里没有 {{EXCEL_DUMP}} 时，把整表原文附在最后：
            // 否则 AI 只能看到规则解析结果，规则认不出部品表时等于什么都没看到
            if (!string.IsNullOrEmpty(excelDump) &&
                (template == null || template.IndexOf("{{EXCEL_DUMP}}", StringComparison.Ordinal) < 0))
            {
                prompt = prompt + "\n\n「Excel 整表原文」（工作表名 + 行号 + 单元格原文，用 | 分隔）：\n" +
                    excelDump;
            }

            return prompt;
        }

        /// <summary>Markdown 表格用：竖线换成全角，换行压成空格。</summary>
        private static string EscapeCell(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Replace('|', '｜').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        /// <summary>从 AI 回复里剥出 JSON 对象（去掉 ``` 围栏 / 前后说明文字）。</summary>
        private static Dictionary<string, object> ParseJsonReply(string reply)
        {
            if (string.IsNullOrEmpty(reply))
            {
                return null;
            }

            int start = reply.IndexOf('{');
            int end = reply.LastIndexOf('}');

            if (start < 0 || end <= start)
            {
                return null;
            }

            string json = reply.Substring(start, end - start + 1);

            try
            {
                return Serializer.DeserializeObject(json) as Dictionary<string, object>;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>取一个数组字段（不是数组时返回空表）。</summary>
        private static IEnumerable<object> ReadArray(IDictionary<string, object> map, string key)
        {
            object value;

            if (map == null || !map.TryGetValue(key, out value) || value == null || value is string)
            {
                return new object[0];
            }

            IEnumerable sequence = value as IEnumerable;

            if (sequence == null)
            {
                return new object[0];
            }

            List<object> list = new List<object>();

            foreach (object item in sequence)
            {
                if (item != null)
                {
                    list.Add(item);
                }
            }

            return list;
        }

        /// <summary>取一个字符串字段（归一化 + 截断）。</summary>
        private static string ReadText(IDictionary<string, object> map, string key)
        {
            return NormalizeCell(ReadRawText(map, key));
        }

        /// <summary>取一个字符串字段原文（只压换行、不截断）。</summary>
        private static string ReadRawText(IDictionary<string, object> map, string key)
        {
            object value;

            if (map == null || !map.TryGetValue(key, out value) || value == null)
            {
                return string.Empty;
            }

            string text = Convert.ToString(value, CultureInfo.InvariantCulture);

            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
        }

        /// <summary>取一个布尔字段（<c>true</c> / <c>"true"</c> / <c>"1"</c> / <c>"はい"</c> / <c>"是"</c> 都算 true）。</summary>
        private static bool ReadBool(IDictionary<string, object> map, string key)
        {
            object value;

            if (map == null || !map.TryGetValue(key, out value) || value == null)
            {
                return false;
            }

            if (value is bool)
            {
                return (bool)value;
            }

            string text = Convert.ToString(value, CultureInfo.InvariantCulture);

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            text = text.Trim();

            return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "1", StringComparison.Ordinal) ||
                string.Equals(text, "はい", StringComparison.Ordinal) ||
                string.Equals(text, "是", StringComparison.Ordinal);
        }

        /// <summary>把 JSON 写成带缩进的多行文本（UTF-8 无 BOM，与 特注品.json 的约定一致）。</summary>
        private static string FormatJson(object value)
        {
            StringBuilder builder = new StringBuilder();
            WriteJsonValue(builder, value, 0);
            builder.Append('\n');

            return builder.ToString();
        }

        /// <summary><see cref="FormatJson"/> 的递归实现（缩进 2 空格）。</summary>
        private static void WriteJsonValue(StringBuilder builder, object value, int depth)
        {
            if (value == null)
            {
                builder.Append("null");
                return;
            }

            string text = value as string;

            if (text != null)
            {
                builder.Append(Serializer.Serialize(text));
                return;
            }

            if (value is bool)
            {
                builder.Append((bool)value ? "true" : "false");
                return;
            }

            if (value is int || value is long || value is short || value is byte)
            {
                builder.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture)
                    .ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is double || value is float || value is decimal)
            {
                builder.Append(Convert.ToDouble(value, CultureInfo.InvariantCulture)
                    .ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            IDictionary<string, object> map = value as IDictionary<string, object>;

            if (map != null)
            {
                builder.Append('{');

                bool first = true;

                foreach (KeyValuePair<string, object> pair in map)
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    builder.Append('\n');
                    builder.Append(' ', (depth + 1) * 2);
                    builder.Append(Serializer.Serialize(pair.Key));
                    builder.Append(": ");
                    WriteJsonValue(builder, pair.Value, depth + 1);
                    first = false;
                }

                if (!first)
                {
                    builder.Append('\n');
                    builder.Append(' ', depth * 2);
                }

                builder.Append('}');
                return;
            }

            IEnumerable sequence = value as IEnumerable;

            if (sequence != null)
            {
                builder.Append('[');

                bool first = true;

                foreach (object item in sequence)
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    builder.Append('\n');
                    builder.Append(' ', (depth + 1) * 2);
                    WriteJsonValue(builder, item, depth + 1);
                    first = false;
                }

                if (!first)
                {
                    builder.Append('\n');
                    builder.Append(' ', depth * 2);
                }

                builder.Append(']');
                return;
            }

            builder.Append(Serializer.Serialize(value));
        }

        /// <summary>压缩成一段短文本（日志用）。</summary>
        private static string Shorten(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(content.Length);

            foreach (char ch in content)
            {
                builder.Append(ch == '\r' || ch == '\n' || ch == '\t' ? ' ' : ch);
            }

            string text = builder.ToString().Trim();

            return text.Length <= ShortTextLength
                ? text
                : text.Substring(0, ShortTextLength) + "…";
        }

        /// <summary>往提示列表里加一条（空白不加）。</summary>
        private static void AddNote(IList<string> notes, string message)
        {
            if (notes != null && !string.IsNullOrWhiteSpace(message))
            {
                notes.Add(message);
            }
        }

        /// <summary>建 JavaScriptSerializer（放宽长度上限：特注品.json 可能很大）。</summary>
        private static JavaScriptSerializer CreateSerializer()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            serializer.RecursionLimit = 200;

            return serializer;
        }
    }
}
