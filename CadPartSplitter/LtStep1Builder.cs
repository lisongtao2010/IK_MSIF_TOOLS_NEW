using System;
using System.Collections.Generic;
using System.IO;

namespace CadPartSplitter
{
    /// <summary>
    /// 部品表「下表参照 / 左上表参照」展开：读 <c>tables\table_NN.csv</c>，把主表的这类行按参照表的明细拆开，
    /// 输出下游 AI 环节约定的 <c>01_アッセンブリ図_部品機能情報.csv</c>
    /// （旧文件名 <c>LT_STEP1.csv</c>；开关仍是 <see cref="DetectorOptions.BuildLtStep1Table"/> / <c>--no-lt-step1</c>）。
    ///
    /// 规则（与图纸上的写法一一对应）：
    ///   · 主表 —— 表头（第一行）同时含「番号」「部品名」，它的明细就是输出内容；
    ///     「部品コード」可有可无：有的部品表根本没有这一列，那些表明细照样收，该格留空；
    ///     图纸上品番那一列的表头有时是**空格子**（合并表头 / 漏字，H310U10011 的 table_01 就是
    ///     「番号 / 部品名 / 枠区分 / 員数 / 基本寸法 / 切断寸法 / (空)」，明细最后一列写着 MDU762 …）——
    ///     这时按明细内容认列（那列出现「下表参照 / 左上表参照」，或过半格子是「字母 + 数字」的品番形态），
    ///     认出来的列在输出表头里改名成「部品コード」：品番不会再落到无名列上，写「下表参照」的行也照常展开；
    ///   · 明细行里「部品コード」含「下表参照」/「左上表参照」→ 先按**表头**找参照表：表头里同含
    ///     `見込み` 与品番列名（部品コード / 部品番号 / 品番 / 型式 / 型番）的那张小表就是参照表 ——
    ///     图纸上它的标题写着「見込み / 部品コード」（H467U10001 的 table_02 就是它，而且排在主表
    ///     **后面**，「左上表参照」按方向往前翻是找不到的，那一行会原样留着 `左上表参照`）；
    ///     找不到这种表时才退回按方向翻（「下表参照」往后、「左上表参照」往前，
    ///     取第一张「表头含部品コード且有明细」的表）；
    ///   · 参照表有几条明细，这一行就拆成几条：只把「部品コード」换成参照表对应的值，
    ///     其余列（番号 / 部品名 / 枠区分 / 員数 / 寸法）沿用原行 —— 同一个部品的多个变体共用番号；
    ///   · 参照表找不到 / 明细全空 → 该行原样保留并写 WARN，不做猜测；
    ///   · 品番前面粘着图纸上的**标注记号**（`△4` / `▲3` 这种指名视图的引用号）时，文字层常常只把
    ///     数字漏出来、跟品番并进同一格（H467U10004 的 `MZC●●73` 读成 `4 MZC●●73`）—— 写出去的
    ///     品番一律先剥掉这种「数字 + 空格」前缀（<see cref="StripPartCodeMark"/>，02 也共用同一份规则）；
    ///   · 输出表头里一定有「部品コード」列（主表没有这一列时在末尾补一列、值留空），
    ///     下游按固定列取数时不会因为缺列而错位。
    ///
    /// 编码与其它清单一致：CSV 用带 BOM 的 UTF-8 + CRLF + RFC 4180 转义（Excel 双击即可打开）。
    /// 只读 <c>tables\*.csv</c>；除顺手删掉自己改名前的老文件 <c>LT_STEP1.csv</c> 外，不改动任何已有产物。
    ///
    /// **只有アッセンブリ図（組立図）才写这份清单**：图面分类读自分类清单 <c>classify\PdfInfoList.txt</c>
    /// （<see cref="PdfInfoListBuilder"/> 在流水线里先跑一步生成；清单缺失 / 没有这张图时退回按
    /// <c>tables\*.csv</c> 现判，同一套规则）。基本図 / 加工図 / 要領図 / 未识别 一律跳过，
    /// 并删掉上一次运行可能留下的同名产物。
    /// </summary>
    public static class LtStep1Builder
    {
        /// <summary>
        /// 下游约定的文件名（写在输出目录根，与 tables_info.json 同级）。
        /// 旧名是 <c>LT_STEP1.csv</c>，现按「图纸类别编号 + 日文名」统一成
        /// <c>01_アッセンブリ図_部品機能情報.csv</c>（アッセンブリ図 = 組立図，部品機能情報 = 部品表）。
        /// </summary>
        public const string OutputFileName = "01_アッセンブリ図_部品機能情報.csv";

        /// <summary>
        /// 改名前的老文件名：同一输出目录里若还留着上一次运行写下的它，本次顺手清掉，
        /// 免得下游按老名字取到过期内容。
        /// </summary>
        private const string LegacyOutputFileName = "LT_STEP1.csv";

        /// <summary>
        /// 主表表头必须出现的「行番号」列名（图纸上写作 <c>番号</c>，值是 1 / 2 / 3 …）。
        ///
        /// 「部品コード」不在必须列表里：有的图纸的部品表只写 番号 / 部品名 / 枠区分 / 員数，没有品番列，
        /// 这种表照样要收（下游要「一个部品一条记录」），只是输出的「部品コード」格留空。
        /// </summary>
        private const string RowNumberHeader = "番号";

        /// <summary>
        /// 「部品名」列名 —— 图纸上也有写成 品名 / 名称 的（同义组 <see cref="HeaderNames.PartName"/>）。
        /// 与 <see cref="RowNumberHeader"/> 同时出现，这张表才当部品表（主表）。
        /// 「番号」必须按原字认：<c>NO</c> / <c>番</c> 这类放宽会把表題欄也认成部品表。
        /// </summary>
        private static readonly string[] PartNameHeaders = HeaderNames.PartName;

        /// <summary>
        /// 「部品コード」列名（也是参照表必须有的列）：**输出表头固定写这个**。
        /// 源表里那一列的标题可能写成 部品番号 / 品番 / 型式 / 型番 —— 认列时按同义组
        /// <see cref="HeaderNames.PartCode"/> 认，输出时统一改名成「部品コード」（下游按固定列取数）。
        /// </summary>
        private const string PartCodeHeader = "部品コード";

        /// <summary>「部品コード」列出现这个字样时，表示要用「下表」的明细展开。</summary>
        private const string NextTableKeyword = "下表参照";

        /// <summary>
        /// 「部品コード」列出现这个字样时，表示要用「左上表」的明细展开：
        /// 参照表在图纸上位于左上方，CSV 编号比主表小 —— 于是「左上表参照」就是**往前翻** CSV 文件。
        /// 这是**兜底**规则：先按参照表的表头找（见 <see cref="ReferenceHeaderKeyword"/>），
        /// 只有找不到那样的表时才按方向翻。图纸上「左上 / 下表」只是画的位置，与 CSV 编号没有
        /// 必然关系 —— H467U10001 的参照表就排在主表**后面**（主表 table_01、参照表 table_02）。
        /// </summary>
        private const string PreviousTableKeyword = "左上表参照";

        /// <summary>
        /// 参照表的标志列名：图纸上那张色展开表是「<c>見込み / 部品コード</c>」两列
        /// （见 `見込み` 与 `部品コード` 两个标题）。表头里同时有这两个列名的表就是参照表 ——
        /// 按表头认，不看它排在主表前面还是后面（`H467U10001` 的 `table_02.csv` 就是这种表，
        /// 它排在主表 `table_01` 的**后面**，旧的「向前翻」规则找不到、那一行原样留着 `左上表参照`）。
        /// </summary>
        private const string ReferenceHeaderKeyword = "見込み";

        /// <summary>
        /// 找参照表表头时向下扫的行数（正常在第 1 行；个别图纸前面留了空行 / 合并表头压到第 2 行）。
        /// </summary>
        private const int ReferenceHeaderScanRows = 3;

        /// <summary>
        /// 按明细认品番列时，「这一列出现过下表参照 / 左上表参照」的得分 —— 比任何
        /// 「看起来像品番」的计数都高，用来压过其它候选列。
        /// </summary>
        private const int ReferenceColumnScore = 1000000;

        /// <summary>
        /// 品番里常见的「变体记号」（图纸上 <c>MZA□968</c> 的 <c>□</c> 表示部件色随产品色变），
        /// 认列时和字母 / 数字一样算「品番的合法字符」。
        /// </summary>
        private const string PartCodeVariantMarks = "□■○●△▲◇◆";

        /// <summary>
        /// 图纸上给部品加的**标注记号**（三角 / 圈 里写个数字，指到某个视图）。文字层会把它们跟
        /// 同一格里的品番粘在一起：<c>H467U10004</c> 的 <c>MZC●●73</c> 前面就带着「△4」，
        /// 落盘成 <c>4 MZC●●73</c>（三角没进文字层，只剩数字 + 空格）；图纸上分两行写时是
        /// <c>△4~MZC●●73</c>（格内换行被 CSV 落盘压成 <c>~</c>）。
        /// 与 <see cref="PartCodeVariantMarks"/> 不同：这些记号**不是品番的一部分**，要剥掉。
        /// </summary>
        internal const string PartCodeMarkChars = "△▲▽▼○●◎◇◆□■※＊";

        /// <summary>
        /// 扫描 <paramref name="outputDirectory"/> 下的 <c>tables\*.csv</c> 并合成
        /// <c>01_アッセンブリ図_部品機能情報.csv</c>。
        ///
        /// **只有アッセンブリ図（組立図）才写这份清单**：图面分类取自分类清单
        /// <c>classify\PdfInfoList.txt</c>（<see cref="PdfInfoListBuilder"/> 在这之前刚生成），
        /// 清单缺失时退回按 <c>tables\*.csv</c> 现判；分类不是アッセンブリ図就直接返回 0，
        /// 并把上一次可能留下的同名产物清掉。
        /// </summary>
        /// <returns>写出的明细行数（不含表头）；0 表示没有生成文件。</returns>
        public static int Build(string outputDirectory, DetectorOptions opts)
        {
            DetectorOptions o = opts ?? new DetectorOptions();

            if (!o.BuildLtStep1Table)
            {
                o.Log("[INFO] 01_アッセンブリ図_部品機能情報 合成已关闭（BuildLtStep1Table = false）。");
                return 0;
            }

            if (string.IsNullOrWhiteSpace(outputDirectory))
                return 0;

            // 只有アッセンブリ図（組立図）才需要这份「部品機能情報」清单：
            // 先读分类清单 classify\PdfInfoList.txt（流水线刚写好的图面分类），清单缺失 / 没有这张图时
            // 退回按 tables\*.csv 现判（与清单同一套规则）。判成别的图类就什么都不写。
            string drawingNumber = PdfInfoListBuilder.DrawingNumberFromOutputDirectory(outputDirectory);
            string categorySource;
            string category = PdfInfoListBuilder.ResolveCategory(outputDirectory, drawingNumber, o, out categorySource);

            if (!PdfInfoListBuilder.IsAssemblyCategory(category))
            {
                o.Log("[INFO] 01_アッセンブリ図_部品機能情報：图面分类「"
                      + (string.IsNullOrEmpty(category) ? "未识别" : category) + "」（依据：" + categorySource
                      + "）不是アッセンブリ図，跳过（并清掉旧的同名产物）。");

                DeleteStaleOutput(outputDirectory, o);
                return 0;
            }

            o.Log("[INFO] 01_アッセンブリ図_部品機能情報：图面分类「アッセンブリ図」（依据：" + categorySource + "），开始合成。");

            string csvDir = Path.Combine(outputDirectory, "tables");

            if (!Directory.Exists(csvDir))
            {
                o.Log("[INFO] 01_アッセンブリ図_部品機能情報：没有 tables 目录（本次未生成表格 CSV），跳过。");
                return 0;
            }

            string[] files = Directory.GetFiles(csvDir, "*.csv");

            // 文件名补零（table_01 … table_10），按名字排序即图纸上从上到下的顺序
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            List<Sheet> sheets = LoadSheets(files, o);

            if (sheets.Count == 0)
            {
                o.Log("[INFO] 01_アッセンブリ図_部品機能情報：" + csvDir + " 下没有可读的表格 CSV，跳过。");
                return 0;
            }

            var output = new List<string[]>();
            string[] outHeader = null;
            int mainSheets = 0;
            int expandedRows = 0;

            for (int i = 0; i < sheets.Count; i++)
            {
                Sheet sheet = sheets[i];

                if (!IsPartsTable(sheet.Header))
                    continue;

                // 品番列：先按表头名找（Sheet.CodeColumn）；表头那一格是空的（合并表头 / 表头漏字，
                // H310U10011 的 table_01 就是「番号 / 部品名 / 枠区分 / 員数 / 基本寸法 / 切断寸法 / (空)」）
                // 时再按明细内容认一次。不认的话会连带两个毛病：品番落在「没有名字的那一列」、
                // 输出里的「部品コード」列反而空着；写「下表参照」的那一格读不到关键字、不会被展开。
                int codeCol = sheet.CodeColumn;

                if (codeCol < 0)
                {
                    codeCol = FindPartCodeColumnByData(sheet);

                    if (codeCol >= 0)
                        o.Log("[INFO] 01_アッセンブリ図_部品機能情報：" + sheet.Name + " 表头没有「" + PartCodeHeader
                              + "」，按明细内容认第 " + (codeCol + 1) + " 列（表头是空的）为品番列。");
                }

                // 按列名映射时用的源表头：认出来的品番列在表头里补上「部品コード」这个名字，
                // 这样 MapToHeader 能把它对到输出表头的「部品コード」列，不会再落到无名列上。
                string[] srcHeader = EffectiveHeader(sheet, codeCol);

                // 品番那一列的标题写成同义写法（部品番号 / 品番 / 型式 / 型番）时，上面已把它改名成
                // 「部品コード」；这里记一句，图纸上写的是哪种叫法一眼能看到（输出列名不变）。
                if (codeCol >= 0 && codeCol < sheet.Header.Length)
                {
                    string written = CsvFile.Normalize(sheet.Header[codeCol]);

                    if (written.Length > 0 && written != CsvFile.Normalize(PartCodeHeader))
                    {
                        o.Log("[INFO] 01_アッセンブリ図_部品機能情報：" + sheet.Name + " 品番那一列的表头写的是「"
                              + written + "」→ 按同义写法当成「" + PartCodeHeader + "」（输出列名不变）。");
                    }
                }

                if (outHeader == null)
                    outHeader = EnsurePartCodeColumn(srcHeader, sheet.Name, o);

                mainSheets++;

                if (codeCol < 0)
                    o.Log("[INFO] 01_アッセンブリ図_部品機能情報：" + sheet.Name + " 没有「" + PartCodeHeader
                          + "」列，明细照收，该列留空。");

                int outCodeCol = CsvFile.IndexOfHeader(outHeader, PartCodeHeader);

                for (int r = 1; r < sheet.Rows.Count; r++)
                {
                    string[] row = sheet.Rows[r];

                    if (CsvFile.IsEmptyRow(row))
                        continue;

                    string code = codeCol >= 0 && codeCol < row.Length ? row[codeCol] : string.Empty;

                    if (!IsReferenceKeyword(code))
                    {
                        output.Add(MapRow(row, srcHeader, outHeader, outCodeCol, sheet.Name, r + 1, o));
                        continue;
                    }

                    // 参照表**先按表头认**：图纸上那张色展开表写着「見込み / 部品コード」两个列名，
                    // 与它在主表的前 / 后没有关系 —— 「左上表参照」指的是画在左上方，CSV 编号未必更小
                    // （H467U10001：主表 table_01、参照表 table_02，按方向往前翻什么都找不到）。
                    // 找不到这种表时才退回按方向翻（旧行为：「下表参照」往后、「左上表参照」往前）。
                    string flat = CsvFile.Normalize(code);
                    int step = flat.IndexOf(PreviousTableKeyword, StringComparison.Ordinal) >= 0 ? -1 : 1;
                    string refName;

                    List<string> refCodes = FindReferenceTableByHeader(sheets, i, out refName);

                    if (refCodes != null)
                    {
                        o.Log("[INFO] 01_アッセンブリ図_部品機能情報：" + sheet.Name + " 第 " + (r + 1) + " 行（" + code
                              + "）按表头「" + ReferenceHeaderKeyword + " / " + PartCodeHeader + "」认出参照表 "
                              + refName + "（与主表前后位置无关）。");
                    }
                    else
                    {
                        refCodes = FindReferenceCodes(sheets, i, step, out refName);
                    }

                    if (refCodes == null)
                    {
                        o.Log("[WARN] 01_アッセンブリ図_部品機能情報：" + sheet.Name + " 第 " + (r + 1) + " 行「" + code
                              + "」按" + (step < 0 ? "前（左上）" : "后（下）")
                              + "找不到可用的参照表，该行原样保留。");
                        output.Add(MapRow(row, srcHeader, outHeader, outCodeCol, sheet.Name, r + 1, o));
                        continue;
                    }

                    foreach (string refCode in refCodes)
                    {
                        // 参照表搬过来的品番同样要清一次（标注记号也会落在参照表的格子里）
                        string[] clone = MapRow(row, srcHeader, outHeader, outCodeCol, sheet.Name, r + 1, o);

                        if (outCodeCol >= 0)
                            clone[outCodeCol] = StripPartCodeMark(refCode);

                        output.Add(clone);
                        expandedRows++;
                    }

                    o.Log("[INFO] 01_アッセンブリ図_部品機能情報：" + sheet.Name + " 第 " + (r + 1) + " 行（" + code + "）→ "
                          + refName + " 的 " + refCodes.Count + " 条部品コード："
                          + string.Join(" / ", refCodes.ToArray()));
                }
            }

            if (outHeader == null)
            {
                o.Log("[INFO] 01_アッセンブリ図_部品機能情報：未找到表头含「番号 / 部品名」的表格，不生成 "
                      + OutputFileName + "。");
                return 0;
            }

            string path = Path.Combine(outputDirectory, OutputFileName);

            DeleteLegacyOutput(outputDirectory, o);

            CsvFile.Write(path, outHeader, output);

            o.Log("[INFO] 01_アッセンブリ図_部品機能情報：" + mainSheets + " 张主表 → 展开 " + expandedRows + " 行 → 共 "
                  + output.Count + " 条明细，已保存：" + path);

            return output.Count;
        }

        /// <summary>
        /// 清掉上一次运行留下的旧文件名（改名前的 <c>LT_STEP1.csv</c>）。
        /// 删不掉（被 Excel 打开 / 无权限）只记 WARN，不影响本次输出。
        /// </summary>
        private static void DeleteLegacyOutput(string outputDirectory, DetectorOptions o)
        {
            RemoveIfExists(Path.Combine(outputDirectory, LegacyOutputFileName), "旧文件名残留 ", o);
        }

        /// <summary>
        /// 这张图不是アッセンブリ図时，把上一次运行留下的同名产物清掉
        /// （上次可能判成アッセンブリ図，留着会让下游按固定名字取到过期内容）。
        /// </summary>
        private static void DeleteStaleOutput(string outputDirectory, DetectorOptions o)
        {
            RemoveIfExists(Path.Combine(outputDirectory, OutputFileName), string.Empty, o);
            RemoveIfExists(Path.Combine(outputDirectory, LegacyOutputFileName), "旧文件名残留 ", o);
        }

        /// <summary>
        /// 删一个文件；不存在就什么也不做。删不掉（被 Excel 占用 / 无权限）只记 WARN。
        /// </summary>
        /// <param name="path">要删的文件。</param>
        /// <param name="note">日志里的说明（例如「旧文件名残留 」），可为空。</param>
        /// <param name="o">参数 / 日志。</param>
        private static void RemoveIfExists(string path, string note, DetectorOptions o)
        {
            if (!File.Exists(path))
                return;

            try
            {
                File.Delete(path);
                o.Log("[INFO] 01_アッセンブリ図_部品機能情報：已删除" + note + path);
            }
            catch (IOException)
            {
                o.Log("[WARN] 01_アッセンブリ図_部品機能情報：删不掉" + note + path + "（可能正被 Excel 打开）。");
            }
            catch (UnauthorizedAccessException)
            {
                o.Log("[WARN] 01_アッセンブリ図_部品機能情報：删不掉" + note + path + "（无权限）。");
            }
        }

        // ── CSV 读取 ─────────────────────────────────────────────────────────

        /// <summary>一张表（一个 CSV 文件）的解析结果。</summary>
        private sealed class Sheet
        {
            /// <summary>文件名（含扩展名），日志里用。</summary>
            public string Name;

            /// <summary>按行拆好的单元格；<c>[0]</c> 是表头。</summary>
            public List<string[]> Rows;

            /// <summary>表头里「部品コード」所在列，-1 表示没有这一列。</summary>
            public int CodeColumn;

            /// <summary>表头行（等于 <c>Rows[0]</c>）。</summary>
            public string[] Header
            {
                get { return Rows[0]; }
            }
        }

        /// <summary>
        /// 读 <c>tables\*.csv</c>（一个文件一张表）。读进来先过一遍 <see cref="CsvFile.FoldDoubleRows"/>：
        /// CAD 把同一个字画了两遍时，表头会是 <c>部部品品ココーードド</c> 这种样子 ——
        /// 按列名找不到「部品コード / 番号」，整张表会被当成「不认识的表」跳过（H467U10008）。
        /// </summary>
        /// <param name="files">tables 目录下的 CSV 文件（已按文件名排序）。</param>
        /// <param name="o">参数 / 日志。</param>
        private static List<Sheet> LoadSheets(string[] files, DetectorOptions o)
        {
            var sheets = new List<Sheet>();

            foreach (string file in files)
            {
                List<string[]> rows;

                try
                {
                    rows = CsvFile.Read(file);
                }
                catch (IOException)
                {
                    continue;   // 被 Excel 占用等情况：跳过这一张，不影响其它表
                }

                if (rows.Count == 0)
                    continue;

                // CAD 把同一个字画了两遍时（文字层重叠放置），表头会是 部部品品ココーードド 这种样子：
                // 按列名找不到「部品コード / 番号」，整张表会被当成「不认识的表」跳过（H467U10008）。
                // 先把这种格子还原，再往下判定。
                int folded;
                string sample;
                CsvFile.FoldDoubleRows(rows, out folded, out sample);

                if (folded > 0)
                {
                    o.Log("[INFO] 01_アッセンブリ図_部品機能情報：" + Path.GetFileName(file) + " 有 " + folded
                          + " 格是同一个字被画了两遍（PDF 里文字重叠放置）→ 已还原（" + sample + "）。");
                }

                sheets.Add(new Sheet
                {
                    Name = Path.GetFileName(file),
                    Rows = rows,
                    // 品番列按同义组认：部品コード / 部品番号 / 品番 / 型式 / 型番
                    CodeColumn = HeaderNames.IndexOf(rows[0], HeaderNames.PartCode)
                });
            }

            return sheets;
        }

        // ── 判定 / 映射 ──────────────────────────────────────────────────────

        /// <summary>
        /// 是否部品表（主表）：表头同时含「番号」（按原字）与「部品名」（按同义组：部品名 / 品名 / 名称）。
        /// 「部品コード」可有可无 —— 没有这一列的部品表也收，输出时该列留空（见 <see cref="EnsurePartCodeColumn"/>）。
        /// </summary>
        private static bool IsPartsTable(string[] header)
        {
            if (header == null)
                return false;

            return CsvFile.IndexOfHeader(header, RowNumberHeader) >= 0
                   && HeaderNames.IndexOf(header, PartNameHeaders) >= 0;
        }

        /// <summary>
        /// 取参照表的部品コード清单（按明细行顺序、去空、去重复表头、去链式参照行）。
        /// 表头不在第一行（前面留了空行 / 合并表头压下来）时自己找一次表头行
        /// （<see cref="FindReferenceHeaderRow"/>）。
        /// 没有部品コード列或没有有效值 → 返回 null（表示这张表不能当参照表）。
        /// </summary>
        private static List<string> CollectReferenceCodes(Sheet sheet)
        {
            if (sheet == null || sheet.Rows.Count == 0)
                return null;

            int codeColumn = sheet.CodeColumn;
            int firstRow = 1;

            if (codeColumn < 0)
            {
                // 表头不在第一行（前面留了空行 / 合并表头把它压到第 2 行）：自己找一次表头行
                int headerRow = FindReferenceHeaderRow(sheet);

                if (headerRow < 0)
                    return null;

                codeColumn = HeaderNames.IndexOf(sheet.Rows[headerRow], HeaderNames.PartCode);
                firstRow = headerRow + 1;
            }

            var codes = new List<string>();

            for (int r = firstRow; r < sheet.Rows.Count; r++)
            {
                string[] row = sheet.Rows[r];

                if (row == null || row.Length <= codeColumn)
                    continue;

                string value = CsvFile.Normalize(StripPartCodeMark(row[codeColumn]));

                if (value.Length == 0)
                    continue;

                if (IsReferenceKeyword(value))
                    continue;   // 参照表自己也在「下表参照 / 左上表参照」：不接第二层，避免越扯越乱

                if (HeaderNames.IsMatch(value, HeaderNames.PartCode))
                    continue;   // 重复的表头行（部品コード / 部品番号 / 品番 …）

                codes.Add(value);
            }

            return codes.Count > 0 ? codes : null;
        }

        /// <summary>
        /// 这一格是不是「…表参照」这种指向别处的写法（「下表参照」/「左上表参照」）。
        /// 判定前先归一化：PDF 里这两个词常被拆成两行（「左上表」+「参照」）、中间还夹空格。
        /// </summary>
        private static bool IsReferenceKeyword(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            string flat = CsvFile.Normalize(value);

            return flat.IndexOf(NextTableKeyword, StringComparison.Ordinal) >= 0
                || flat.IndexOf(PreviousTableKeyword, StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// 主表的「部品コード」列在表头里没有名字时，按明细内容认一次那一列。
        ///
        /// 图纸上的部品表有时把品番那一列的表头画成空格子（合并表头 / 表头漏字：H310U10011 的
        /// <c>table_01.csv</c> 就是「番号 / 部品名 / 枠区分 / 員数 / 基本寸法 / 切断寸法 / (空)」，
        /// 明细最后一列写着 <c>MDU762</c> …）。只按表头名找的话这一列认不出来，会连带两个毛病：
        /// 品番落在「没有名字的那一列」、输出里的「部品コード」列反而空着；
        /// 写「下表参照」的那一格读不到关键字 → 那一行不会被参照表展开。
        ///
        /// 只在**主表**上用（表头含「番号」「部品名」）：参照表仍要求表头里写着「部品コード」，
        /// 免得把表題欄、色展開表之类误当成参照表。候选只取「表头是空的」那些列 ——
        /// 表头有名字的列照旧按列名对应，不动。
        /// </summary>
        /// <param name="sheet">主表。</param>
        /// <returns>认出来的品番列号；认不出返回 -1（表里真的没有品番列，输出该列留空）。</returns>
        private static int FindPartCodeColumnByData(Sheet sheet)
        {
            if (sheet == null || sheet.Rows.Count < 2)
                return -1;

            string[] header = sheet.Header;

            int width = header.Length;

            for (int r = 1; r < sheet.Rows.Count; r++)
            {
                if (sheet.Rows[r].Length > width)
                    width = sheet.Rows[r].Length;
            }

            int best = -1;
            int bestScore = 0;

            for (int c = 0; c < width; c++)
            {
                if (c < header.Length && CsvFile.Normalize(header[c]).Length > 0)
                    continue;   // 表头有名字：不是「漏了表头的品番列」

                int filled = 0;
                int looksLikeCode = 0;
                int reference = 0;

                for (int r = 1; r < sheet.Rows.Count; r++)
                {
                    string[] row = sheet.Rows[r];

                    if (CsvFile.IsEmptyRow(row))
                        continue;

                    string value = c < row.Length ? CsvFile.Normalize(row[c]) : string.Empty;

                    if (value.Length == 0)
                        continue;

                    filled++;

                    if (IsReferenceKeyword(value))
                        reference++;
                    else if (LooksLikePartCode(value))
                        looksLikeCode++;
                }

                if (filled == 0)
                    continue;

                // 「下表参照 / 左上表参照」是最硬的证据：只有品番那一格会这么写
                if (reference > 0)
                {
                    if (bestScore < ReferenceColumnScore)
                    {
                        best = c;
                        bestScore = ReferenceColumnScore;
                    }

                    continue;
                }

                // 没有参照行时，要求过半的格子都是「字母 + 数字」的品番形态
                if (looksLikeCode * 2 < filled)
                    continue;

                // 计数一样时取靠右的那一列（图纸上品番列排在最后）
                if (looksLikeCode < bestScore)
                    continue;

                best = c;
                bestScore = looksLikeCode;
            }

            return best;
        }

        /// <summary>
        /// 这一格像不像品番（部品コード）：3–24 字符、至少 1 个半角字母 + 1 个数字，
        /// 除字母数字外只允许 <c>- _ / .</c> 与变体记号 <see cref="PartCodeVariantMarks"/>。
        /// 出现汉字 / 假名 / 全角标点 / 其它符号的一律不算 ——
        /// 这样「下表参照」「ブラケット」「300×40」都不会被误认成品番。
        /// </summary>
        private static bool LooksLikePartCode(string value)
        {
            if (value == null || value.Length < 3 || value.Length > 24)
                return false;

            bool letter = false;
            bool digit = false;

            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];

                if (ch >= '0' && ch <= '9')
                {
                    digit = true;
                    continue;
                }

                if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'))
                {
                    letter = true;
                    continue;
                }

                if (ch == '-' || ch == '_' || ch == '/' || ch == '.'
                    || PartCodeVariantMarks.IndexOf(ch) >= 0)
                    continue;

                return false;
            }

            return letter && digit;
        }

        /// <summary>
        /// 参与「按列名映射」的源表头：把品番那一列在表头里**统一命名成「部品コード」**，
        /// 输出表头（下游按固定列名取数）就能对上 —— 品番不会掉进「名字不一样的那一列」。
        ///
        /// 两种情况都改名：
        ///   · 表头没有「部品コード」这个名字、但明细里认出了品番列（<see cref="FindPartCodeColumnByData"/>）；
        ///   · 表头那一格写着同义写法（「部品番号」「品番」「型式」「型番」）—— 图纸上只是换个叫法，
        ///     输出仍统一成「部品コード」（H094U10008 那种图纸的品番列表头就写「部品番号」）。
        /// 本来就写着「部品コード」时原样返回（不改动原数组）。
        /// </summary>
        /// <param name="sheet">主表。</param>
        /// <param name="codeColumn">品番列号；-1 表示没有。</param>
        /// <returns>按列名映射用的表头（可能是原数组，也可能是改名后的副本）。</returns>
        private static string[] EffectiveHeader(Sheet sheet, int codeColumn)
        {
            if (sheet == null)
                return new string[0];

            string[] header = sheet.Header;

            if (codeColumn < 0)
                return header;

            string key = CsvFile.Normalize(PartCodeHeader);
            string current = codeColumn < header.Length ? CsvFile.Normalize(header[codeColumn]) : string.Empty;

            if (current == key)
                return header;   // 这一格本来就叫「部品コード」：不动它

            int length = codeColumn + 1 > header.Length ? codeColumn + 1 : header.Length;
            var copy = new string[length];

            Array.Copy(header, copy, header.Length);

            for (int c = header.Length; c < length; c++)
                copy[c] = string.Empty;

            copy[codeColumn] = PartCodeHeader;

            return copy;
        }

        /// <summary>
        /// 按**表头**找参照表：表头（前 <see cref="ReferenceHeaderScanRows"/> 行内）同时含
        /// <see cref="ReferenceHeaderKeyword"/>（<c>見込み</c>）与品番列名（<c>部品コード</c> 及其同义写法）
        /// 的表就是参照表 —— 图纸上它的标题就写着「見込み / 部品コード」。挑离主表最近的那张
        /// （同距离优先排在主表**后面**的），一张图有不止一张色展开表时不会串到别的主表的参照表上。
        /// </summary>
        /// <param name="sheets">全部表格（按文件名排序）。</param>
        /// <param name="fromIndex">主表在 <paramref name="sheets"/> 里的下标。</param>
        /// <param name="sheetName">命中的参照表文件名；没找到时写 null。</param>
        /// <returns>参照表的部品コード清单；没有这种表 / 表里没有有效品番返回 null。</returns>
        private static List<string> FindReferenceTableByHeader(List<Sheet> sheets, int fromIndex, out string sheetName)
        {
            sheetName = null;

            if (sheets == null)
                return null;

            int best = -1;
            int bestDistance = int.MaxValue;

            for (int k = 0; k < sheets.Count; k++)
            {
                if (k == fromIndex || !IsReferenceTable(sheets[k]))
                    continue;

                int distance = Math.Abs(k - fromIndex);

                // 同距离优先排在主表后面的那张（「下表参照」比「左上表参照」常见）
                if (distance < bestDistance || (distance == bestDistance && k > best))
                {
                    best = k;
                    bestDistance = distance;
                }
            }

            if (best < 0)
                return null;

            List<string> found = CollectReferenceCodes(sheets[best]);

            if (found == null)
                return null;   // 认出了参照表却一条有效品番都没有 → 当成没找到，交给调用方写 WARN

            sheetName = sheets[best].Name;

            return found;
        }

        /// <summary>
        /// 这张表是不是参照表（图纸上「<c>見込み / 部品コード</c>」两列的小表）——
        /// 表头里同时出现 <see cref="ReferenceHeaderKeyword"/> 与品番列名（同义组 <see cref="HeaderNames.PartCode"/>）。
        /// </summary>
        private static bool IsReferenceTable(Sheet sheet)
        {
            return FindReferenceHeaderRow(sheet) >= 0;
        }

        /// <summary>
        /// 在前 <see cref="ReferenceHeaderScanRows"/> 行里找「<c>見込み</c> + 品番列名」都出现的那一行
        /// （表头正常就在第 1 行，个别图纸前面留了空行）；找不到返回 -1。
        /// </summary>
        private static int FindReferenceHeaderRow(Sheet sheet)
        {
            if (sheet == null || sheet.Rows.Count == 0)
                return -1;

            int scan = Math.Min(sheet.Rows.Count, ReferenceHeaderScanRows);

            for (int r = 0; r < scan; r++)
            {
                string[] row = sheet.Rows[r];

                if (row == null)
                    continue;

                if (CsvFile.IndexOfHeader(row, ReferenceHeaderKeyword) >= 0
                    && HeaderNames.IndexOf(row, HeaderNames.PartCode) >= 0)
                    return r;
            }

            return -1;
        }

        /// <summary>
        /// 按方向找参照表：<paramref name="step"/> = +1 往后翻（「下表参照」）、-1 往前翻（「左上表参照」），
        /// 返回第一张「表头含部品コード且有明细」的表（图纸上位于下方 / 左上方的那个小表）。
        /// </summary>
        /// <param name="sheets">全部表格（按文件名排序，与图纸上从上到下的顺序一致）。</param>
        /// <param name="fromIndex">主表在 <paramref name="sheets"/> 里的下标。</param>
        /// <param name="step">翻的方向：+1 往后、-1 往前。</param>
        /// <param name="sheetName">命中的参照表文件名；没找到时写 null。</param>
        /// <returns>参照表的部品コード清单；找不到返回 null。</returns>
        private static List<string> FindReferenceCodes(List<Sheet> sheets, int fromIndex, int step, out string sheetName)
        {
            sheetName = null;

            for (int k = fromIndex + step; k >= 0 && k < sheets.Count; k += step)
            {
                List<string> found = CollectReferenceCodes(sheets[k]);

                if (found == null)
                    continue;

                sheetName = sheets[k].Name;

                return found;
            }

            return null;
        }

        /// <summary>把源行按「列名」映射到输出表头（列顺序 / 列数不同也能对齐，缺列留空）。</summary>
        private static string[] MapToHeader(string[] row, string[] srcHeader, string[] dstHeader)
        {
            var result = new string[dstHeader.Length];

            for (int c = 0; c < dstHeader.Length; c++)
            {
                string name = CsvFile.Normalize(dstHeader[c]);
                int src = CsvFile.IndexOfHeader(srcHeader, name);

                if (src < 0)
                {
                    // 归一化之后可能变成「被包含」的反向关系，退回同名精确比较
                    for (int s = 0; s < srcHeader.Length; s++)
                    {
                        if (CsvFile.Normalize(srcHeader[s]) == name)
                        {
                            src = s;
                            break;
                        }
                    }
                }

                result[c] = (src >= 0 && src < row.Length) ? row[src] : string.Empty;
            }

            return result;
        }

        /// <summary>
        /// 去掉品番前后的分隔符：半角 / 全角空格、制表符，以及 <c>~</c> ——
        /// <c>tables\*.csv</c> 落盘时把格内换行压成 <c>~</c>，图纸上「△4」和品番分成两行时就是
        /// <c>△4~MZC●●73</c>。
        /// </summary>
        private static string TrimPartCode(string value)
        {
            return value.Trim().Trim('~').Trim();
        }

        /// <summary>
        /// 剥掉品番前面的**标注记号**：<c>4 MZC●●73</c> → <c>MZC●●73</c>、<c>△4 MZC●●73</c> → <c>MZC●●73</c>。
        ///
        /// 图纸上给部品加了「△4 / ▲3」这类引用号，就在品番格子的左边：文字层把三角漏掉后就只剩
        /// 「数字 + 空格」，落盘成 <c>4 MZC●●73</c>（分两行时是 <c>△4~MZC●●73</c>）。
        /// 品番都以字母开头（MZC●●73 / TJ-3086 / LAVS-639），**数字（可带记号）+ 分隔符开头的一定不是品番**，
        /// 前面那段一律剥掉。数字后面没有分隔符时不剥 —— 免得把 <c>2H100</c> 这种真的以数字开头的、
        /// 或 <c>TJ-3086</c> 这种中间夹 `-` 的品番改坏（只有「记号 + 数字 + 字母」照剥：三角被认出来、
        /// 却没留空格）。整格只有标注、没有品番时返回空串。
        ///
        /// <c>internal</c>：`02_要領図_部品機能情報.csv` 的品番也来自同一份文字层，
        /// <see cref="LtStep2Builder"/> 用的是同一份规则（那边在摊平之后统一清一遍）。
        /// </summary>
        internal static string StripPartCodeMark(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            string s = TrimPartCode(value);

            while (s.Length > 0)
            {
                int i = 0;

                // 标注记号本身（三角 / 圈 / 四角 …）
                while (i < s.Length && PartCodeMarkChars.IndexOf(s[i]) >= 0)
                    i++;

                int digits = i;

                while (i < s.Length && s[i] >= '0' && s[i] <= '9')
                    i++;

                if (i == digits)
                    break;   // 开头既没有数字、也没有「记号 + 数字」→ 不是标注（MZC●●73 / TJ-3086）

                // 数字后面是分隔符 → 这是标注；后面直接跟字母且前面带过记号（△4MZC●●73）也算；
                // 否则数字是品番本身的一部分（2H100 / 3M-100），不动它
                bool strip = i >= s.Length
                             || char.IsWhiteSpace(s[i]) || s[i] == '~'
                             || (digits > 0 && char.IsLetter(s[i]));

                if (!strip)
                    break;

                s = TrimPartCode(s.Substring(i));
            }

            return s;
        }

        /// <summary>
        /// <see cref="MapToHeader"/> 之后再清一次品番列：剥掉图纸标注留下的「数字 + 空格」前缀
        /// （见 <see cref="StripPartCodeMark"/>），并记一行日志说明原值 / 新值（一眼能核对）。
        /// </summary>
        /// <param name="row">源明细行。</param>
        /// <param name="srcHeader">源表头（认出来的品番列已改名成 `部品コード`）。</param>
        /// <param name="dstHeader">输出表头。</param>
        /// <param name="codeColumn">输出表头里 `部品コード` 所在列；-1 表示不处理。</param>
        /// <param name="sheetName">主表文件名（日志里用）。</param>
        /// <param name="rowNumber">这一行在 CSV 里的行号（含表头，1 起；日志里用）。</param>
        /// <param name="o">参数 / 日志。</param>
        private static string[] MapRow(string[] row, string[] srcHeader, string[] dstHeader, int codeColumn,
                                       string sheetName, int rowNumber, DetectorOptions o)
        {
            string[] mapped = MapToHeader(row, srcHeader, dstHeader);

            if (codeColumn < 0 || codeColumn >= mapped.Length)
                return mapped;

            string raw = mapped[codeColumn];

            if (string.IsNullOrEmpty(raw))
                return mapped;

            string clean = StripPartCodeMark(raw);

            if (clean != raw)
            {
                o.Log("[INFO] 01_アッセンブリ図_部品機能情報：" + sheetName + " 第 " + rowNumber + " 行 部品コード「"
                      + raw + "」→「" + clean + "」（剥掉图纸上的标注记号）。");
            }

            mapped[codeColumn] = clean;

            return mapped;
        }

        /// <summary>
        /// 输出表头里必须有「部品コード」列：主表自己没这一列时，在末尾补一列（明细值留空）。
        ///
        /// 这样「有的部品表写了品番、有的没写」时，输出仍是同一套列 —— 下游按固定列取数不会错位，
        /// 缺的那一列读出来就是空串。
        /// </summary>
        /// <param name="header">主表表头（只读，不改动原数组）。</param>
        /// <param name="sheetName">主表文件名（日志里用）。</param>
        /// <param name="o">参数 / 日志。</param>
        /// <returns>含「部品コード」列的表头（原表头已经有这一列时原样返回）。</returns>
        private static string[] EnsurePartCodeColumn(string[] header, string sheetName, DetectorOptions o)
        {
            if (header == null)
                return new[] { PartCodeHeader };

            if (CsvFile.IndexOfHeader(header, PartCodeHeader) >= 0)
                return header;

            var extended = new string[header.Length + 1];

            Array.Copy(header, extended, header.Length);
            extended[header.Length] = PartCodeHeader;

            o.Log("[INFO] 01_アッセンブリ図_部品機能情報：" + sheetName + " 没有「" + PartCodeHeader
                  + "」列，输出表头末尾补一列，明细该格留空。");

            return extended;
        }
    }
}
