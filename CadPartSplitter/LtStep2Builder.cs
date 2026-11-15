using System;
using System.Collections.Generic;
using System.IO;

namespace CadPartSplitter
{
    /// <summary>
    /// 品種表整理：读 <c>tables\table_NN.csv</c>，把四种表摊平成一行一个部品的
    /// <c>02_要領図_部品機能情報.csv</c>：①「横向重复 N 组 (部品コード, 員数)」的多列重复品種表；
    /// ④「品種 × 部品名 マトリクス表」（表头一排部品名、每行一个品種，格子里是品番）；
    /// ②图纸上普通的「単純部品表」（<c>部品名 / 枠区分</c>；<c>部品コード</c> 可缺）。
    ///
    /// 认得的样子（图纸上常见，表头被拆成两行 + 合并单元格）：
    /// <code>
    /// 品種,部品名 ： 縦枠上部スペーサー,,部品名 ： 小縦枠上部スペーサー,, …
    /// ,部品コード,員数,部品コード,員数, …
    /// ,,,,,,,
    /// 片引戸,MEACC428,1,MEACC428,2, …
    /// </code>
    /// 整理成（一行一个部品，品種 / 品名往下填满；品種表里没有「枠区分」列，该格留空；
    /// 表里没有「基本寸法」「切断寸法」列时这两格也留空）：
    /// <code>
    /// 品種,部品名,枠区分,部品コード,員数,基本寸法,切断寸法
    /// 片引戸,縦枠上部スペーサー,,MEACC428,1,,
    /// 片引戸,小縦枠上部スペーサー,,MEACC428,2,,
    /// </code>
    ///
    /// 收录四种表：
    /// <para>
    /// ①「多列重复的品種表」（上面那种）—— 两条同时满足才认：
    ///   · 表头区里有含「品種」的单元格 → 品種列；
    ///   · 表头区里存在「同一个列名出现 ≥ 2 次」的行（多列重复）→ 每次出现的列就是一组部品ブロック的起点。
    /// 只有一块、没有重复列的表不是这种表。
    /// </para>
    /// <para>
    /// ④「品種 × 部品名 マトリクス表」—— 表头一行写「品種」+ 一排部品名（<c>縦枠上部</c> / <c>縦枠下部</c> /
    /// <c>小縦枠上部</c> …），每行一个品種，格子里就是该品種对应部品的品番，<c>－</c> = 该品種不用这个部品
    /// （<c>H325U10011.pdf</c> 的 <c>table_01.csv</c>）：
    /// </para>
    /// <code>
    /// 品種,縦枠上部,縦枠下部,小縦枠上部,小縦枠下部,目隠し溝上部,目隠し溝下部
    /// 片引戸,MEA△△415,MEA□□418,MEA△△415,MEA□□418,－,－
    /// 片引戸2枚建,MEA△△415,MEA□□418,MEA△△416,MEA□□419,－,－
    /// </code>
    /// **一个非 <c>－</c> 格子一条明细**：品種 = 该行品種列（合并单元格留空时沿用上一行）、
    /// 部品名 = **该列的列名**、部品コード = 格子值，員数 / 基本寸法 / 切断寸法 / 枠区分 留空
    /// → 上面这张表就是 7 × 6 减去 20 个 <c>－</c> = 22 条明细。判据见 <c>ExpandVarietyMatrix</c>：
    /// 表头含「品種」且另有 ≥ 2 个非固定列名（不是 部品コード / 部品名 / 番号 / 員数 / 寸法 …）、
    /// 没有重复列名（重复的是 ①）、明细格过 <c>LooksLikePartCode</c>（且整体至少一个含字母，
    /// 免得把寸法 / 数量表当部品表）。
    /// </para>
    /// <para>
    /// ②「単純部品表」—— 图纸上普通的部品表（<c>H310U10034.pdf</c> / <c>H310U10027.pdf</c> 那种，
    /// 只有一个部品、没有品種列）：
    /// </para>
    /// <code>
    /// 番号,部品名,枠区分,員数,基本寸法,切断寸法,部品コード
    /// 1,ロットＮｏ．ラベル,三方枠,1,-,-,LAVS639
    /// </code>
    /// 表头（前 6 行内）同含「部品名」「枠区分」即认这种表：一行一条明细，
    /// <c>品種</c> 留空（这种表本来就没有品種列）、<c>枠区分</c> 按列名搬 →
    /// <c>,ロットＮｏ．ラベル,三方枠,LAVS639,1,-,-</c>（<c>基本寸法 / 切断寸法</c> 也按列名搬，
    /// 表里没有这一列时该格留空）。
    /// <c>部品コード</c> 可有可无 —— 有些部品表只写「番号 / 部品名 / 枠区分 / 員数 / 基本寸法 / 切断寸法」
    /// （<c>H453U10003.pdf</c> / <c>H453U10004.pdf</c> 就是这种），缺这一列时输出里的 <c>部品コード</c> 恒留空，
    /// 不会因为缺列就把整张表跳过（与 <see cref="LtStep1Builder"/> 的「部品コード 可缺」一致）。
    /// </para>
    /// <para>
    /// ③「部品情報表」—— 图纸右上角那种两列小表（<c>H094U10008要領図.pdf</c> 的 <c>table_01.csv</c>）：
    /// </para>
    /// <code>
    /// No.,部品名称,部品番号
    /// 1,ロットNO.ラベル,LAVS-639
    /// </code>
    /// 表头（前 6 行内）同含「部品名」「部品コード」、**且没有「枠区分」**即认这种表
    /// （有「枠区分」的先按 ② 收，所以不会重）：一行一条明细，<c>品種 / 枠区分</c> 留空，
    /// <c>部品名 / 部品コード</c> 按列名搬 → <c>,ロットNO.ラベル,,LAVS-639,,,</c>。
    /// 表題欄那种「部品名称：ロットNO.ラベル」的标签-值写法不算表头（列名格里带着值），
    /// 明细的 <c>部品コード</c> 还要含数字、不含漢字假名（表題欄的「承認日」之类当不成品番）。
    /// </para>
    /// <para>
    /// 四种表共有的一条「品番なし」规则：<c>部品コード</c> 那一格是 <c>－</c> 这类占位符
    /// （整格只由破折号类字符组成 —— <c>－ - ー — – ― −</c> …，半角 / 全角空格不计）时，
    /// 这条明细不收：图纸上写「－」就是「这个部没有品番」（④ 的マトリクス表里「这个品種不用这个部品」）。
    /// 与「缺列」区分开：整张表都没有 <c>部品コード</c> 列时照旧出明细（该格留空），只有列在、格子里写着「－」才丢这条。
    /// 丢掉的条数会打进日志。
    /// </para>
    /// <para>四种都不是的表原样跳过（不改动）。</para>
    ///
    /// 输出：<c>&lt;输出目录&gt;\02_要領図_部品機能情報.csv</c>（旧名 <c>LT_STEP2.csv</c>；
    /// 要領図 = 要領書，部品機能情報 = 部品表。UTF-8 带 BOM + CRLF + RFC 4180），
    /// 列固定为 品種 / 部品名 / 枠区分 / 部品コード / 員数 / 基本寸法 / 切断寸法 —— 下游按固定列取数
    /// （品種表没有枠区分列 → 该格留空；単純部品表没有品種列 → 该格留空；有的部品表没有部品コード列 → 该格留空；
    /// 表里没有「基本寸法」「切断寸法」列、或那一格是空 → 该格留空，不会因此少一条明细）。
    /// 多张表按文件名顺序追加到同一个文件。只读 <c>tables\*.csv</c>，不改动任何已有产物。
    ///
    /// **只有要領図（要領書）才写这份清单**：图面分类读自分类清单 <c>classify\PdfInfoList.txt</c>
    /// （<see cref="PdfInfoListBuilder"/> 在流水线里先跑一步生成；清单缺失 / 没有这张图时退回按
    /// <c>tables\*.csv</c> 现判，同一套规则）。基本図 / 加工図 / アッセンブリ図 / 未识别 一律跳过，
    /// 并删掉上一次运行可能留下的同名产物。
    /// </summary>
    public static class LtStep2Builder
    {
        /// <summary>
        /// 下游约定的文件名（写在输出目录根，与 01_アッセンブリ図_部品機能情報.csv 同级）。
        /// 旧名是 <c>LT_STEP2.csv</c>，现按「图纸类别编号 + 日文名」统一成
        /// <c>02_要領図_部品機能情報.csv</c>（要領図 = 要領書，部品機能情報 = 部品表）。
        /// </summary>
        public const string OutputFileName = "02_要領図_部品機能情報.csv";

        /// <summary>
        /// 改名前的老文件名：同一输出目录里若还留着上一次运行写下的它，本次顺手清掉，
        /// 免得下游按老名字取到过期内容。
        /// </summary>
        private const string LegacyOutputFileName = "LT_STEP2.csv";

        /// <summary>品種表的标志文字（表头里出现它就认这一列是品種）。</summary>
        private const string VarietyKeyword = "品種";

        /// <summary>块标题的前缀（图纸上写成「部品名 ： 縦枠上部スペーサー」）。</summary>
        private const string PartNameKeyword = "部品名";

        /// <summary>块里的品番列名。</summary>
        private const string PartCodeKeyword = "部品コード";

        /// <summary>块里的数量列名。</summary>
        private const string QuantityKeyword = "員数";

        /// <summary>
        /// 「単純部品表」（<c>番号 / 部品名 / 枠区分 / 員数 / 基本寸法 / 切断寸法 [/ 部品コード]</c>）的标志列名
        /// —— 和 <see cref="PartNameKeyword"/> 同时出现才认这种表（<c>部品コード</c> 可缺）。
        /// </summary>
        private const string FrameKeyword = "枠区分";

        /// <summary>
        /// 「基本寸法」列名（<c>番号 / 部品名 / 枠区分 / 員数 / 基本寸法 / 切断寸法[/ 部品コード]</c>）。
        /// 有这一列就按列名搬到输出；没有这一列 / 这一格是空 → 输出里该格留空，不影响其它列。
        /// </summary>
        private const string BasicSizeKeyword = "基本寸法";

        /// <summary>「切断寸法」列名（与 <see cref="BasicSizeKeyword"/> 同样处理，取不到就留空）。</summary>
        private const string CutSizeKeyword = "切断寸法";

        /// <summary>
        /// 「品番なし」を表す占位符：<c>－</c>（全角）/ <c>-</c>（半角）/ <c>ー</c>（長音）/ <c>—</c> / <c>–</c> …。
        /// 一整格只由这些符号组成（1 个或几个）时不算品番 —— 明细不收；
        /// 真品番里夹着 <c>-</c>（<c>TJ-3086</c>）不受影响：判定要求「整格都是这些符号」。
        /// </summary>
        private static readonly char[] PlaceholderCodeChars =
        {
            (char)0x002D,   // - 半角ハイフンマイナス
            (char)0x2010,   // ハイフン
            (char)0x2011,   // 非分割ハイフン
            (char)0x2012,   // 図形ダッシュ
            (char)0x2013,   // en dash
            (char)0x2014,   // em dash
            (char)0x2015,   // 水平バー
            (char)0x2043,   // ハイフンブレット
            (char)0x2212,   // 数学用マイナス
            (char)0xFE63,   // 小ハイフンマイナス
            (char)0xFF0D,   // 全角ハイフンマイナス（图纸上最常见）
            (char)0x30FC,   // 長音符（表格里也常拿来当「－」）
            (char)0xFF70    // 半角長音符
        };

        /// <summary>找表头（品種列 / 重复列名行 / 部品名+枠区分 行）时最多看前几行。</summary>
        private const int MaxHeaderScanRows = 6;

        /// <summary>
        /// 输出表头：固定 7 列，下游按列取数。
        /// 后两列 <c>基本寸法</c> / <c>切断寸法</c> 是追加的 —— 表里有这一列就搬值，
        /// 没有这一列（或这一格是空）就留空，不会因此少一条明细。
        /// </summary>
        private static readonly string[] OutHeader =
        {
            VarietyKeyword, PartNameKeyword, FrameKeyword, PartCodeKeyword, QuantityKeyword,
            BasicSizeKeyword, CutSizeKeyword
        };

        /// <summary>
        /// 扫描 <paramref name="outputDirectory"/> 下的 <c>tables\*.csv</c> 并写出
        /// <c>02_要領図_部品機能情報.csv</c>（图面分类不是「要領図」时不写）。
        /// </summary>
        /// <returns>写出的明细行数（不含表头）；0 表示没有生成文件。</returns>
        public static int Build(string outputDirectory, DetectorOptions opts)
        {
            DetectorOptions o = opts ?? new DetectorOptions();

            if (!o.BuildLtStep2Table)
            {
                o.Log("[INFO] 02_要領図_部品機能情報 摊平已关闭（BuildLtStep2Table = false）。");
                return 0;
            }

            if (string.IsNullOrWhiteSpace(outputDirectory))
                return 0;

            // 只有要領図（要領書）才需要这份清单：先读分类清单 classify\PdfInfoList.txt（流水线刚写好的
            // 图面分类），清单缺失 / 没有这张图时退回按 tables\*.csv 现判（与清单同一套规则）。
            // 判成别的图类就什么都不写，并清掉上一次留下的同名产物。
            string drawingNumber = PdfInfoListBuilder.DrawingNumberFromOutputDirectory(outputDirectory);
            string categorySource;
            string category = PdfInfoListBuilder.ResolveCategory(outputDirectory, drawingNumber, o, out categorySource);

            if (!PdfInfoListBuilder.IsRequirementCategory(category))
            {
                o.Log("[INFO] 02_要領図_部品機能情報：图面分类「"
                      + (string.IsNullOrEmpty(category) ? "未识别" : category) + "」（依据：" + categorySource
                      + "）不是要領図，跳过（并清掉旧的同名产物）。");

                DeleteStaleOutput(outputDirectory, o);
                return 0;
            }

            o.Log("[INFO] 02_要領図_部品機能情報：图面分类「要領図」（依据：" + categorySource + "），开始摊平。");

            string csvDir = Path.Combine(outputDirectory, "tables");

            if (!Directory.Exists(csvDir))
            {
                o.Log("[INFO] 02_要領図_部品機能情報：没有 tables 目录（本次未生成表格 CSV），跳过。");
                return 0;
            }

            string[] files = Directory.GetFiles(csvDir, "*.csv");

            // 文件名补零（table_01 … table_10），按名字排序即图纸上从上到下的顺序
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            var output = new List<string[]>();
            int tables = 0;
            int placeholders = 0;   // 因 部品コード 是「－」这类占位符而没收的明细总数

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

                // CAD 把同一个字画了两遍时（文字层重叠放置），表头会落成 部部品品ココーードド 这种样子，
                // 「部品コード / 部品名 / 員数」这些列名一个都认不出来（H467U10008 的 02 就是这样，
                // 部品コード 整列空）。先把这种格子还原，再往下摊平。
                int foldedCells;
                string foldedSample;
                CsvFile.FoldDoubleRows(rows, out foldedCells, out foldedSample);

                if (foldedCells > 0)
                {
                    o.Log("[INFO] 02_要領図_部品機能情報：" + Path.GetFileName(file) + " 有 " + foldedCells
                          + " 格是同一个字被画了两遍（PDF 里文字重叠放置）→ 已还原（" + foldedSample + "）。");
                }

                // 四种表都收：④品種 × 部品名 マトリクス表（表头是一排部品名、每行一个品種，
                //               格子里是品番，`－` = 该品種不用这个部品 —— H325U10011 的 table_01）
                //             ①多列重复的品種表（横向排开 N 组部品）
                //             ②単純部品表（番号 / 部品名 / 枠区分 / 員数 / … [/ 部品コード]，品種留空）
                //             ③部品情報表（No. / 部品名称 / 部品番号 这种两列小表，图纸右上角
                //               H094U10008 的 table_01 就是它，没有 枠区分 / 品種）
                // ④ 必须排在 ① 前面：矩阵表的明细行里同一个品番会出现两次（`片引戸,MEA△△415,…` 第 1、3 列
                // 都是 MEA△△415），① 的 HasDuplicateName 会把它当成「列名重复的表头」——
                // H325U10011 就是这么只摊出 8 条（7 行 × 6 列 里有 22 格是品番）。① 里另加了「重复的得像列名」的关。
                // 部品コード 是「－」这类占位符（品番なし）的明细四种表都不收，只计数、在日志里报一句。
                int skipped;
                List<string[]> detail = ExpandVarietyMatrix(rows, out skipped);
                string form = "品種 × 部品名 マトリクス表";

                if (detail == null)
                {
                    detail = Expand(rows, out skipped);
                    form = "多列重复的品種表";
                }

                if (detail == null)
                {
                    detail = ExpandPartTable(rows, out skipped);
                    form = "単純部品表（部品名 / 枠区分）";
                }

                if (detail == null)
                {
                    detail = ExpandItemInfoTable(rows, out skipped);
                    form = "部品情報表（部品名 / 部品コード）";
                }

                if (skipped > 0)
                {
                    placeholders += skipped;

                    o.Log("[INFO] 02_要領図_部品機能情報：" + Path.GetFileName(file) + " 有 " + skipped
                          + " 条明细的 部品コード 是「－」这类占位符（品番なし）→ 不收。");
                }

                if (detail == null || detail.Count == 0)
                    continue;   // 两种形式都不是（或表头下面一条明细都没有 / 全是占位符）→ 不动这张表

                tables++;

                // ① 品番前导的图纸标注（△4 → 4 MZC●●73）② 寸法旁边粘着的三角形注記符号（100 1 → 100）
                List<string[]> cleaned = CleanPartCodes(detail, Path.GetFileName(file), o);
                CleanSizeMarks(cleaned, Path.GetFileName(file), o);
                output.AddRange(cleaned);

                o.Log("[INFO] 02_要領図_部品機能情報：" + Path.GetFileName(file) + " 是" + form + " → 摊平 "
                      + detail.Count + " 条明细。");
            }

            if (tables == 0)
            {
                o.Log("[INFO] 02_要領図_部品機能情報：未找到品種表（品種 + 列名重复）、品種 × 部品名 マトリクス表"
                      + "（表头一排部品名、每行一个品種）、単純部品表"
                      + "（部品名 / 枠区分，部品コード 可缺）或部品情報表（部品名 / 部品コード 两列小表，"
                      + "No. / 部品名称 / 部品番号 那种），不生成 " + OutputFileName + "。");
                return 0;
            }

            string path = Path.Combine(outputDirectory, OutputFileName);

            DeleteLegacyOutput(outputDirectory, o);
            CsvFile.Write(path, OutHeader, output);

            o.Log("[INFO] 02_要領図_部品機能情報：" + tables + " 张表 → 共 " + output.Count + " 条明细，已保存：" + path);

            if (placeholders > 0)
            {
                o.Log("[INFO] 02_要領図_部品機能情報：另有 " + placeholders
                      + " 条明细因 部品コード 是「－」这类占位符（品番なし）未收。");
            }

            return output.Count;
        }

        /// <summary>
        /// 清掉上一次运行留下的旧文件名（改名前的 <c>LT_STEP2.csv</c>）。
        /// 删不掉（被 Excel 打开 / 无权限）只记 WARN，不影响本次输出。
        /// </summary>
        private static void DeleteLegacyOutput(string outputDirectory, DetectorOptions o)
        {
            RemoveIfExists(Path.Combine(outputDirectory, LegacyOutputFileName), "旧文件名残留 ", o);
        }

        /// <summary>
        /// 这张图不是要領図时，把上一次运行留下的同名产物清掉
        /// （上次可能判成要領図，留着会让下游按固定名字取到过期内容）。
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
                o.Log("[INFO] 02_要領図_部品機能情報：已删除" + note + path);
            }
            catch (IOException)
            {
                o.Log("[WARN] 02_要領図_部品機能情報：删不掉" + note + path + "（可能正被 Excel 打开）。");
            }
            catch (UnauthorizedAccessException)
            {
                o.Log("[WARN] 02_要領図_部品機能情報：删不掉" + note + path + "（无权限）。");
            }
        }

        // ── 品番占位符 ───────────────────────────────────────────────────────

        /// <summary>
        /// 这一格的 `部品コード` 是不是「品番なし」的占位符（<c>－</c> / <c>-</c> / <c>ー</c> / <c>—</c> …，
        /// 整格只由破折号类字符组成，半角 / 全角空格不计）。<c>TJ-3086</c> 这种真品番返回 false。
        /// </summary>
        private static bool IsPlaceholderCode(string code)
        {
            string s = CsvFile.Normalize(code);     // 去掉半角 / 全角空格与换行

            if (s.Length == 0)
                return false;                       // 空格子不算占位符（由调用方按「没有品番」处理）

            for (int i = 0; i < s.Length; i++)
            {
                if (Array.IndexOf(PlaceholderCodeChars, s[i]) < 0)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// 摊平出来的明细里，<c>部品コード</c> 前面可能还粘着图纸上的**标注记号**（`△4` / `▲3` 这种
        /// 指名视图的引用号）：文字层常常只把数字漏出来，落盘成 <c>4 MZC●●73</c>（`H467U10004.pdf` 就是），
        /// 那不是品番的一部分。这里在写文件之前统一剥一遍，规则与 01 共用
        /// （<see cref="LtStep1Builder.StripPartCodeMark"/>）：`4 MZC●●73` → `MZC●●73`。
        /// </summary>
        /// <param name="rows">摊平出来的一批明细（固定列，见 <see cref="OutHeader"/>）。</param>
        /// <param name="fileName">这张表的文件名（日志里用）。</param>
        /// <param name="o">参数 / 日志。</param>
        private static List<string[]> CleanPartCodes(List<string[]> rows, string fileName, DetectorOptions o)
        {
            int column = Array.IndexOf(OutHeader, PartCodeKeyword);

            if (rows == null || column < 0)
                return rows;

            int cleaned = 0;
            string sample = null;

            foreach (string[] row in rows)
            {
                if (row == null || column >= row.Length)
                    continue;

                string raw = row[column];

                if (string.IsNullOrEmpty(raw))
                    continue;

                string clean = LtStep1Builder.StripPartCodeMark(raw);

                if (clean == raw)
                    continue;

                row[column] = clean;
                cleaned++;

                if (sample == null)
                    sample = "「" + raw + "」→「" + clean + "」";
            }

            if (cleaned > 0 && o != null)
            {
                o.Log("[INFO] 02_要領図_部品機能情報：" + fileName + " 有 " + cleaned
                      + " 条明细的 部品コード 带着图纸上的标注记号（" + sample + "）→ 已剥掉。");
            }

            return rows;
        }

        // ── 三角形注記符号（△数字）粘在寸法上 ─────────────────────────────────

        /// <summary>
        /// 把 <c>基本寸法</c> / <c>切断寸法</c> 里粘着的**三角形注記符号的数字**剥掉
        /// （<c>100 1</c> → <c>100</c>）。
        ///
        /// 为什么会出现这种值：图纸上在寸法旁边盖了个 △1（提醒 / 注記符号），三角形本身是
        /// 矢量图形、里面的数字却在文字层里，于是跟寸法落进同一格 —— `H986U10002.pdf` 的
        /// <c>切断寸法</c> 就摊平成 <c>100 1</c>。
        /// 正常路径下这一步是多余的（<see cref="RuledTableExtractor.MarkTriangleMarks"/> 已在提取
        /// 表格时把这种数字剔掉、不进 <c>tables\*.csv</c>），留着是为了**上次落盘的旧 CSV** 也能摊对。
        ///
        /// 剥的判据（宁可漏剥也不误伤真寸法）：
        ///   · 整格正好两个词（空格 / 全角空格 / <c>~</c> 分隔）；
        ///   · 其中一个词是**一位**数字 <c>1</c>~<c>9</c>（<c>0</c> 不当注記番号；两位数与
        ///     <c>100 40</c> 这种真两段写法分不开，都不剥）；
        ///   · 另一个词是寸法形态（<c>100</c> / <c>100.5</c> / <c>300×40</c> / <c>300x40</c>）。
        /// 两条都满足时只留那一个寸法 —— 例：<c>100 1</c> → <c>100</c>，<c>1 100</c> → <c>100</c>。
        /// </summary>
        /// <param name="rows">摊平出来的一批明细（固定列，见 <see cref="OutHeader"/>；原地修改）。</param>
        /// <param name="fileName">这张表的文件名（日志里用）。</param>
        /// <param name="o">参数 / 日志。</param>
        /// <returns>被剥掉注記数字的格数。</returns>
        private static int CleanSizeMarks(List<string[]> rows, string fileName, DetectorOptions o)
        {
            if (rows == null) return 0;

            int basic = Array.IndexOf(OutHeader, BasicSizeKeyword);
            int cut = Array.IndexOf(OutHeader, CutSizeKeyword);

            int cleaned = 0;
            string sample = null;

            foreach (string[] row in rows)
            {
                if (row == null) continue;

                cleaned += CleanSizeMarkAt(row, basic, ref sample);
                cleaned += CleanSizeMarkAt(row, cut, ref sample);
            }

            if (cleaned > 0 && o != null)
            {
                o.Log("[INFO] 02_要領図_部品機能情報：" + fileName + " 有 " + cleaned
                      + " 格寸法带着图纸上的三角形注記符号（" + sample + "）→ 已剥掉。");
            }

            return cleaned;
        }

        /// <summary>
        /// <see cref="CleanSizeMarks"/> 的单格版本：把 <paramref name="row"/> 的第
        /// <paramref name="column"/> 列（&lt; 0 表示这一列不存在）按 <see cref="StripMarkDigit"/> 清一遍。
        /// </summary>
        /// <returns>这一格是否被改过（1 / 0）。</returns>
        private static int CleanSizeMarkAt(string[] row, int column, ref string sample)
        {
            if (column < 0 || column >= row.Length) return 0;

            string raw = row[column];

            if (string.IsNullOrEmpty(raw)) return 0;

            string clean = StripMarkDigit(raw);

            if (clean == raw) return 0;

            row[column] = clean;

            if (sample == null)
                sample = "「" + raw + "」→「" + clean + "」";

            return 1;
        }

        /// <summary>
        /// 寸法格「寸法 + 三角形注記符号的数字」→ 只留寸法（判据见 <see cref="CleanSizeMarks"/>）。
        /// 不是这种形态的原样返回。internal 供探针直接断言。
        /// </summary>
        internal static string StripMarkDigit(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            string[] parts = value.Split(new[] { ' ', '　', '~' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 2) return value;

            if (IsMarkDigit(parts[0]) && LooksLikeSize(parts[1])) return parts[1];
            if (IsMarkDigit(parts[1]) && LooksLikeSize(parts[0])) return parts[0];

            return value;
        }

        /// <summary>一位的 <c>1</c>~<c>9</c> —— 三角形注記符号里的号码（<c>0</c> 不当号码用）。</summary>
        private static bool IsMarkDigit(string token)
        {
            return !string.IsNullOrEmpty(token)
                   && token.Length == 1
                   && token[0] >= '1' && token[0] <= '9';
        }

        /// <summary>
        /// 寸法形态：<c>100</c> / <c>100.5</c> / <c>300×40</c> / <c>300x40</c>（全角数字也认）。
        /// 小数点 / 掛け算記号は「数字の直後」にだけ許す（<c>1.2.3</c> のような並びは寸法ではない）。
        /// </summary>
        private static bool LooksLikeSize(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;

            bool hasDigit = false;
            bool lastDigit = false;

            foreach (char raw in token)
            {
                char ch = raw;

                if (ch >= (char)0xFF10 && ch <= (char)0xFF19)          // 全角 ０~９
                    ch = (char)('0' + (ch - (char)0xFF10));

                if (ch >= '0' && ch <= '9')
                {
                    hasDigit = true;
                    lastDigit = true;
                    continue;
                }

                bool separator = ch == '.'
                                 || ch == '×' || ch == 'x' || ch == 'X'
                                 || ch == '*' || ch == '＊';

                if (!separator || !lastDigit) return false;

                lastDigit = false;
            }

            return hasDigit && lastDigit;
        }

        // ── 摊平 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 摊平一张表：找「列名重复」的表头行 → 按块拆出 (品種, 部品名, 枠区分, 部品コード, 員数)。
        /// 这种表没有「枠区分」列，该格固定留空。
        /// 品番格是空 / 只是 <c>－</c> 这类占位符的行不收（占位符的行数由 <paramref name="skipped"/> 带出，供日志用）。
        /// 不是「品種 + 多列重复」表 → 返回 null（调用方跳过，不动这张表）。
        /// </summary>
        /// <param name="rows">一张 <c>tables\table_NN.csv</c> 的全部行。</param>
        /// <param name="skipped">因 <c>部品コード</c> 是占位符（<c>－</c> 等）而没收的格数。</param>
        private static List<string[]> Expand(List<string[]> rows, out int skipped)
        {
            skipped = 0;

            int maxColumns = 0;

            for (int r = 0; r < rows.Count; r++)
            {
                if (rows[r].Length > maxColumns)
                    maxColumns = rows[r].Length;
            }

            if (maxColumns < 3)
                return null;

            // 1) 表头区里找「列名重复」的行 + 含「品種」的列
            int blockRow = -1;
            int varietyColumn = -1;
            string keyName = null;

            int scan = Math.Min(MaxHeaderScanRows, rows.Count);

            for (int r = 0; r < scan; r++)
            {
                // 明细行里同一个品番写了两遍时 HasDuplicateName 也成立
                // （`片引戸,MEA△△415,MEA□□418,MEA△△415,MEA□□418,…` 这种「品種 × 部品名 マトリクス表」
                // 的明细行，H325U10011 的 table_01 就是），那不是表头 —— 重复的那个「列名」得像列名
                // （不是品番、不是「－」占位符）才算，判据集中在 IsRepeatedHeaderRow。
                if (!IsRepeatedHeaderRow(rows[r]))
                    continue;

                keyName = MostRepeatedName(rows[r]);

                int varietyColumnFound = FindVarietyColumn(rows, r);

                if (varietyColumnFound < 0)
                {
                    keyName = null;
                    continue;   // 列名是重复的，但没有品種列 → 不是这种表（例如型枠表的 A/B/C 員数列）
                }

                blockRow = r;
                varietyColumn = varietyColumnFound;
                break;
            }

            if (blockRow < 0 || keyName == null)
                return null;

            // 2) 重复列名出现的每一列 = 一组部品ブロック的起点（左 → 右）
            var starts = new List<int>();

            for (int c = 0; c < rows[blockRow].Length; c++)
            {
                if (CsvFile.Normalize(rows[blockRow][c]) == keyName)
                    starts.Add(c);
            }

            if (starts.Count < 2)
                return null;    // 只有一块 → 不是「多列重复」

            // 3) 每块：品名（块标题）+ 品番列 + 員数列（+ 基本寸法 / 切断寸法 列，有就搬、没有留空）
            var names = new List<string>();
            var codeColumns = new List<int>();
            var quantityColumns = new List<int>();
            var basicSizeColumns = new List<int>();
            var cutSizeColumns = new List<int>();

            for (int b = 0; b < starts.Count; b++)
            {
                int start = starts[b];
                int end = (b + 1 < starts.Count) ? starts[b + 1] - 1 : maxColumns - 1;

                names.Add(BlockName(rows, blockRow, start));

                int codeOffset = -1;
                int quantityOffset = -1;
                int basicSizeOffset = -1;
                int cutSizeOffset = -1;

                for (int c = start; c <= end && c < rows[blockRow].Length; c++)
                {
                    string child = CsvFile.Normalize(rows[blockRow][c]);

                    if (codeOffset < 0 && HeaderNames.IsMatch(child, HeaderNames.PartCode))
                        codeOffset = c - start;

                    if (quantityOffset < 0 && child.IndexOf(QuantityKeyword, StringComparison.Ordinal) >= 0)
                        quantityOffset = c - start;

                    if (basicSizeOffset < 0 && child.IndexOf(BasicSizeKeyword, StringComparison.Ordinal) >= 0)
                        basicSizeOffset = c - start;

                    if (cutSizeOffset < 0 && child.IndexOf(CutSizeKeyword, StringComparison.Ordinal) >= 0)
                        cutSizeOffset = c - start;
                }

                // 块里没有「部品コード」列名时，退回块首列（很多表把品番直接写在标题行那一列下面）
                codeColumns.Add(start + (codeOffset >= 0 ? codeOffset : 0));
                quantityColumns.Add(quantityOffset >= 0 ? start + quantityOffset : -1);
                basicSizeColumns.Add(basicSizeOffset >= 0 ? start + basicSizeOffset : -1);
                cutSizeColumns.Add(cutSizeOffset >= 0 ? start + cutSizeOffset : -1);
            }

            // 4) 明细行：品種往下填满，每块有品番就出一条
            var detail = new List<string[]>();
            string variety = string.Empty;

            for (int r = blockRow + 1; r < rows.Count; r++)
            {
                string[] row = rows[r];

                if (CsvFile.IsEmptyRow(row))
                    continue;   // 合并单元格 / 表格线造成的空行

                string value = Cell(row, varietyColumn).Trim();
                string normalized = CsvFile.Normalize(value);

                // 合并单元格：品種只写在第一行，下面留空 → 沿用上一个（表头行本身不算）
                if (normalized.Length > 0
                    && normalized.IndexOf(VarietyKeyword, StringComparison.Ordinal) < 0)
                    variety = value;

                for (int b = 0; b < starts.Count; b++)
                {
                    string code = Cell(row, codeColumns[b]).Trim();
                    string codeNormalized = CsvFile.Normalize(code);

                    if (codeNormalized.Length == 0)
                        continue;   // 这块这一行没有品番

                    if (IsPlaceholderCode(code))
                    {
                        skipped++;  // 部品コード 是「－」这类占位符 → 这条明细不收
                        continue;
                    }

                    if (HeaderNames.IsMatch(codeNormalized, HeaderNames.PartCode))
                        continue;   // 表中间重复出现的表头行

                    string quantity = CellOf(row, quantityColumns[b]);
                    string basicSize = CellOf(row, basicSizeColumns[b]);
                    string cutSize = CellOf(row, cutSizeColumns[b]);

                    // 品種表里没有「枠区分」列 → 该格固定留空（输出列数与単純部品表一致）；
                    // 基本寸法 / 切断寸法 同样：这一块没有那一列（或这一格是空）就留空。
                    detail.Add(new string[]
                    {
                        variety, names[b], string.Empty, code, quantity, basicSize, cutSize
                    });
                }
            }

            return detail;
        }

        // ── 品種 × 部品名 マトリクス表 ───────────────────────────────────────

        /// <summary>
        /// 摊平第四种形式：「品種 × 部品名 マトリクス表」——
        /// 表头一行写「品種」+ 一排部品名（`縦枠上部` / `縦枠下部` / `小縦枠上部` …），
        /// 每行一个品種，格子就是那个品種对应部品的品番，`－` = 该品種不用这个部品
        /// （<c>H325U10011.pdf</c> 的 <c>table_01.csv</c> 就是这种）：
        /// </summary>
        /// <code>
        /// 品種,縦枠上部,縦枠下部,小縦枠上部,小縦枠下部,目隠し溝上部,目隠し溝下部
        /// 片引戸,MEA△△415,MEA□□418,MEA△△415,MEA□□418,－,－
        /// 片引戸2枚建,MEA△△415,MEA□□418,MEA△△416,MEA□□419,－,－
        /// </code>
        /// <para>
        /// 摊平成「**一个非 `－` 格子一条明细**」：品種 = 该行品種列（合并单元格留空时沿用上一行），
        /// 部品名 = **该列的列名**，部品コード = 格子里的值，員数 / 基本寸法 / 切断寸法 留空
        /// （这种表里没有这几列）→ 上面这张表摊出 7 行 × 6 列 减去 20 个 `－` = 22 条明细。
        /// 少收一格就少一个部品，所以这里不能「一行只取第一格」。
        /// </para>
        /// <para>
        /// 认这种表的条件（表头只在 <see cref="MaxHeaderScanRows"/> 行内找，宁可漏认也不误伤别的表）：
        /// </para>
        /// <list type="bullet">
        /// <item>表头行里有含「品種」的格子，且这一行除它以外还有 ≥ 2 个部品名列
        /// （<see cref="IsMatrixPartColumn"/>）—— 只有 1 个部品名列的是一般的品種別明細表；</item>
        /// <item>这一行**没有重复列名**（重复的是第一种「多列重复的品種表」）；</item>
        /// <item>这一行的**下一行**也不能是重复列名的子表头（`部品コード,員数,部品コード,員数`，
        /// <see cref="IsRepeatedHeaderRow"/>）—— 那是 ① 摊的「块标题行 + 子表头行」，交给 ① 才留得住員数 / 寸法；</item>
        /// <item>列名不是固定列名（部品コード / 部品番号 / 番号 / 員数 / 枠区分 / 基本寸法 / 切断寸法 /
        /// 部品名 / 備考 … 见 <see cref="MatrixFixedNames"/>）—— 写着 `部品名` / `部品コード` 的表
        /// 是第 ②③ 种表，不是マトリクス（那两种得按列搬值，不能拿列名当部品名）；</item>
        /// <item>明细里至少收下 1 条，且收下的格子里至少有一个含半角字母（<see cref="HasLatinLetter"/>）——
        /// 品番一定带字母，全是数字的是寸法 / 数量表，不当部品表
        /// （`H325U10011` 的 <c>table_02</c> 是「製品色 × 記号」表，值 `PP` / `CC` 不到 3 个字，
        /// 过不了 <see cref="LooksLikePartCode"/>，也不会被收）。</item>
        /// </list>
        /// 不是这种表 → 返回 null（调用方接着试下一种）。
        /// </summary>
        /// <param name="rows">一张 <c>tables\table_NN.csv</c> 的全部行。</param>
        /// <param name="skipped">因 部品コード 是「－」这类占位符（该品種不用这个部品）而没收的格数。</param>
        private static List<string[]> ExpandVarietyMatrix(List<string[]> rows, out int skipped)
        {
            skipped = 0;

            if (rows == null || rows.Count == 0)
                return null;

            int headerRow = -1;
            int varietyColumn = -1;
            int scan = Math.Min(MaxHeaderScanRows, rows.Count);

            for (int r = 0; r < scan && headerRow < 0; r++)
            {
                string[] row = rows[r];

                if (row == null || HasDuplicateName(row))
                    continue;   // 有重复列名的是第一种表

                int found = VarietyIndex(row);

                if (found < 0 || CountMatrixPartColumns(row, found) < 2)
                    continue;

                // ①「多列重复的品種表」也长这样（品種 + 一排块标题），区别在**下一行是重复列名的子表头**
                // （`部品コード,員数,部品コード,員数`）—— 那种交给 ① 摊（这里摊会丢掉員数 / 寸法）。
                if (r + 1 < rows.Count && IsRepeatedHeaderRow(rows[r + 1]))
                    continue;

                headerRow = r;
                varietyColumn = found;
            }

            if (headerRow < 0)
                return null;

            string[] header = rows[headerRow];
            var detail = new List<string[]>();
            bool hasLetter = false;
            string variety = string.Empty;

            for (int r = headerRow + 1; r < rows.Count; r++)
            {
                string[] row = rows[r];

                if (CsvFile.IsEmptyRow(row))
                    continue;   // 合并单元格 / 表格线造成的空行

                string value = Cell(row, varietyColumn).Trim();
                string normalized = CsvFile.Normalize(value);

                if (normalized.IndexOf(VarietyKeyword, StringComparison.Ordinal) >= 0)
                    continue;   // 表中间重复出现的表头行

                // 合并单元格：品種只写在第一行，下面留空 → 沿用上一行
                if (normalized.Length > 0)
                    variety = value;

                if (variety.Length == 0)
                    continue;   // 品種列空着、上面也没有品種 → 不是这张表的明细

                for (int c = 0; c < row.Length; c++)
                {
                    if (c == varietyColumn || !IsMatrixPartColumn(header, c))
                        continue;

                    string code = Cell(row, c).Trim();

                    if (code.Length == 0)
                        continue;   // 该品種这一列没有品番

                    if (IsPlaceholderCode(code))
                    {
                        skipped++;  // 「－」= 该品種不用这个部品 → 这条明细不收
                        continue;
                    }

                    if (!LooksLikePartCode(code))
                        continue;   // 寸法 / 数量 / 雜行：不是品番的样子

                    if (HasLatinLetter(code))
                        hasLetter = true;

                    // 这种表没有 員数 / 基本寸法 / 切断寸法 / 枠区分 列 → 那几格留空；
                    // 部品名取列名（表头那一格，过 CsvFile.Normalize 去掉拼字空格）
                    detail.Add(new string[]
                    {
                        variety, CsvFile.Normalize(header[c]), string.Empty, code,
                        string.Empty, string.Empty, string.Empty
                    });
                }
            }

            if (detail.Count == 0 || !hasLetter)
            {
                skipped = 0;    // 不是这种表：占位符计数也不带给调用方（免得日志说谎）
                return null;
            }

            return detail;
        }

        /// <summary>
        /// 一行里含「品種」的列号，-1 表示这一行没有品種列。
        /// </summary>
        private static int VarietyIndex(string[] row)
        {
            if (row == null)
                return -1;

            for (int c = 0; c < row.Length; c++)
            {
                if (CsvFile.Normalize(row[c]).IndexOf(VarietyKeyword, StringComparison.Ordinal) >= 0)
                    return c;
            }

            return -1;
        }

        /// <summary>マトリクス表的部品名列有几个（品種列不算）。</summary>
        private static int CountMatrixPartColumns(string[] headerRow, int varietyColumn)
        {
            int count = 0;

            for (int c = 0; c < headerRow.Length; c++)
            {
                if (c != varietyColumn && IsMatrixPartColumn(headerRow, c))
                    count++;
            }

            return count;
        }

        /// <summary>
        /// 这一列是不是マトリクス表的「部品名」列：列名非空，且不是固定列名
        /// （品番 / 数量 / 枠区分 / 寸法 / 番号 / 備考 … 见 <see cref="MatrixFixedNames"/>）。
        /// 列名是「部品名」「部品コード」的表属于第 ②③ 种（按列搬值），不算マトリクス。
        /// </summary>
        private static bool IsMatrixPartColumn(string[] headerRow, int column)
        {
            string name = CsvFile.Normalize(Cell(headerRow, column));

            if (name.Length == 0)
                return false;

            if (HeaderNames.IsMatch(name, HeaderNames.PartCode))
                return false;

            if (name.IndexOf(VarietyKeyword, StringComparison.Ordinal) >= 0)
                return false;

            foreach (string fixedName in MatrixFixedNames)
            {
                if (name.IndexOf(fixedName, StringComparison.Ordinal) >= 0)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// マトリクス表的列名里不能出现的词：出现这些词的列是固定列（品番 / 数量 / 寸法 / 区分 …），
        /// 不是「部品名」列。后几个只是防御用 —— 万一图纸把固定列画在同一张表里，也不会被当部品名。
        /// </summary>
        private static readonly string[] MatrixFixedNames =
        {
            QuantityKeyword, FrameKeyword, BasicSizeKeyword, CutSizeKeyword,
            PartNameKeyword, "品名", "番号", "備考", "摘要", "数量", "材質", "仕様"
        };

        /// <summary>
        /// 这一格里有半角字母（品番几乎都带：<c>MEA△△415</c> / <c>MDWC652</c> / <c>LAVS639</c>）——
        /// マトリクス表里用它把「寸法だけ / 数量だけ」的表挡在外面。
        /// </summary>
        private static bool HasLatinLetter(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            foreach (char ch in text)
            {
                if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'))
                    return true;
            }

            return false;
        }

        // ── 単純部品表 ───────────────────────────────────────────────────────

        /// <summary>
        /// 摊平第二种形式：「単純部品表」—— 图纸上普通的部品表
        /// （表头 <c>番号 / 部品名 / 枠区分 / 員数 / 基本寸法 / 切断寸法 / 部品コード</c>，
        /// 没有品種列、也没有重复列名）。一行一条明细：<c>品種</c> 留空，
        /// <c>部品名 / 枠区分 / 部品コード / 員数 / 基本寸法 / 切断寸法</c> 都按列名搬
        /// （表里没有哪一列、或那一格是空 → 该格留空）。
        /// <c>部品コード</c> 列可缺（例如 <c>H453U10003.pdf</c> 只有
        /// <c>番号 / 部品名 / 枠区分 / 員数 / 基本寸法 / 切断寸法</c>）—— 缺这一列时明细里的
        /// <c>部品コード</c> 留空，仍然出明细（不再把整张表当「不是部品表」跳过）。
        /// 有 <c>部品コード</c> 列时，那一格是 <c>－</c> 这类占位符的行不收
        /// （图纸上写「－」= 这个部没有品番，这条明细不要）。
        /// 不是这种表 → 返回 null（调用方跳过，不动这张表）。
        /// </summary>
        /// <param name="rows">一张 <c>tables\table_NN.csv</c> 的全部行。</param>
        /// <param name="skipped">因 <c>部品コード</c> 是占位符（<c>－</c> 等）而没收的行数。</param>
        private static List<string[]> ExpandPartTable(List<string[]> rows, out int skipped)
        {
            skipped = 0;

            if (rows == null || rows.Count == 0)
                return null;

            int headerRow = -1;     // 表头起始行（含「部品名」「枠区分」那一行）
            int detailStart = -1;   // 明细从哪一行开始（表头可能占两行）
            int nameColumn = -1;
            int frameColumn = -1;
            int codeColumn = -1;
            int quantityColumn = -1;
            int basicSizeColumn = -1;
            int cutSizeColumn = -1;

            // 1) 前 6 行里找表头：必须有「部品名」+「枠区分」；「部品コード」可缺（缺了该格留空）。
            //    表头被拆成两行时（`番号 / 部品名 / 枠区分` 在上面一行、`部品コード / 員数` 落在紧跟的下一行）
            //    也认，这时明细从表头第二行之后开始。
            //    表題欄 / 変更履歴表 / 枠タイプ表不会同时出现「部品名」+「枠区分」，`A部` 那种空框也不会。
            int scan = Math.Min(MaxHeaderScanRows, rows.Count);

            for (int r = 0; r < scan; r++)
            {
                // 部品名 / 部品コード 都按同义组认：品名 / 名称、部品番号 / 品番 / 型式 / 型番
                int name = HeaderNames.IndexOf(rows[r], HeaderNames.PartName);
                int frame = FindColumn(rows[r], FrameKeyword);

                if (name < 0 || frame < 0)
                    continue;

                int code = HeaderNames.IndexOf(rows[r], HeaderNames.PartCode);
                int quantity = FindColumn(rows[r], QuantityKeyword);
                int basicSize = FindColumn(rows[r], BasicSizeKeyword);
                int cutSize = FindColumn(rows[r], CutSizeKeyword);
                int start = r + 1;

                // 表头拆成两行：只在下一行确实还写着这些列名时才算第二行表头
                // （不把明细行拿来当表头）
                if ((code < 0 || quantity < 0 || basicSize < 0 || cutSize < 0) && start < rows.Count)
                {
                    int nextCode = code < 0 ? HeaderNames.IndexOf(rows[start], HeaderNames.PartCode) : -1;
                    int nextQuantity = quantity < 0 ? FindColumn(rows[start], QuantityKeyword) : -1;
                    int nextBasicSize = basicSize < 0 ? FindColumn(rows[start], BasicSizeKeyword) : -1;
                    int nextCutSize = cutSize < 0 ? FindColumn(rows[start], CutSizeKeyword) : -1;

                    if (nextCode >= 0 || nextQuantity >= 0 || nextBasicSize >= 0 || nextCutSize >= 0)
                    {
                        if (nextCode >= 0)
                            code = nextCode;

                        if (nextQuantity >= 0)
                            quantity = nextQuantity;

                        if (nextBasicSize >= 0)
                            basicSize = nextBasicSize;

                        if (nextCutSize >= 0)
                            cutSize = nextCutSize;

                        start++;    // 第二行也算表头，明细从它的下一行开始
                    }
                }

                headerRow = r;
                detailStart = start;
                nameColumn = name;
                frameColumn = frame;
                codeColumn = code;                  // -1 = 这张表没有「部品コード」列 → 该格留空
                quantityColumn = quantity;          // -1 = 没有「員数」列 → 该格留空
                basicSizeColumn = basicSize;        // -1 = 没有「基本寸法」列 → 该格留空
                cutSizeColumn = cutSize;            // -1 = 没有「切断寸法」列 → 该格留空

                break;
            }

            if (headerRow < 0)
                return null;

            // 2) 明细：一行一条；有「部品コード」列时品番为空 / 只有「－」这类占位符都跳过，
            //    没有这一列时用「部品名」判断
            var detail = new List<string[]>();

            for (int r = detailStart; r < rows.Count; r++)
            {
                string[] row = rows[r];

                if (CsvFile.IsEmptyRow(row))
                    continue;   // 合并单元格 / 表格线造成的空行

                string code = codeColumn >= 0 ? Cell(row, codeColumn).Trim() : string.Empty;
                string name = Cell(row, nameColumn).Trim();

                if (codeColumn >= 0)
                {
                    if (code.Length == 0)
                        continue;   // 这一行没有品番

                    if (IsPlaceholderCode(code))
                    {
                        skipped++;  // 部品コード 是「－」这类占位符 → 这条明细不收
                        continue;
                    }

                    if (HeaderNames.IsMatch(code, HeaderNames.PartCode))
                        continue;   // 表中间重复出现的表头行
                }
                else
                {
                    // 这张表没有「部品コード」列 → 只用「部品名」判断这一行是不是明细
                    if (name.Length == 0)
                        continue;   // 这一行没有部品名

                    if (HeaderNames.IsMatch(name, HeaderNames.PartName))
                        continue;   // 表中间重复出现的表头行（部品名 那一格）
                }

                string frameValue = CellOf(row, frameColumn);
                string quantity = CellOf(row, quantityColumn);
                string basicSize = CellOf(row, basicSizeColumn);
                string cutSize = CellOf(row, cutSizeColumn);

                // 这种表没有品種列 → 品種留空；枠区分 / 基本寸法 / 切断寸法 按列名搬，
                // 没有那一列（或这一格是空）→ 该格留空；没有部品コード 列时该格也留空
                detail.Add(new string[]
                {
                    string.Empty, name, frameValue, code, quantity, basicSize, cutSize
                });
            }

            return detail;
        }

        // ── 部品情報表（部品名 / 部品コード 两列） ─────────────────────────────

        /// <summary>
        /// 摊平第三种形式：「部品情報表」—— 图纸右上角那种两列小表
        /// （<c>H094U10008</c> 的 <c>table_01.csv</c>）：
        /// <code>
        /// No.,部品名称,部品番号
        /// 1,ロットNO.ラベル,LAVS-639
        /// </code>
        /// 表头里**同含「部品名」（<see cref="HeaderNames.PartName"/>：部品名 / 品名 / 名称）
        /// 与「部品コード」（<see cref="HeaderNames.PartCode"/>：部品コード / 部品番号 / 品番 / 型式 / 型番）、
        /// 且没有「枠区分」**时认这种表（有枠区分的是上面那种「単純部品表」，先认那个）：
        /// 一行一条明细，<c>品種 / 枠区分</c> 留空、<c>部品名 / 部品コード</c> 按列名搬
        /// （<c>員数 / 基本寸法 / 切断寸法</c> 有列就搬、没有就留空）。
        ///
        /// 表題欄那种「部品名称：ロットNO.ラベル」的标签-值写法不算表头（列名格里带着值，
        /// 有 <c>：</c> / <c>:</c>）—— 否则一整块表題欄会被当成部品表、把下面的自由行当明细。
        /// 明细的 <c>部品コード</c> 还要过 <see cref="LooksLikePartCode"/>（含数字、不是漢字假名），
        /// 表題欄的「承認日」「尺度」之类不会被当成品番。
        /// 不是这种表 → 返回 null（调用方跳过，不动这张表）。
        /// </summary>
        /// <param name="rows">一张 <c>tables\table_NN.csv</c> 的全部行。</param>
        /// <param name="skipped">因 <c>部品コード</c> 是占位符（<c>－</c> 等）而没收的行数。</param>
        private static List<string[]> ExpandItemInfoTable(List<string[]> rows, out int skipped)
        {
            skipped = 0;

            if (rows == null || rows.Count == 0)
                return null;

            int headerRow = -1;
            int nameColumn = -1;
            int codeColumn = -1;
            int quantityColumn = -1;
            int basicSizeColumn = -1;
            int cutSizeColumn = -1;

            int scan = Math.Min(MaxHeaderScanRows, rows.Count);

            for (int r = 0; r < scan; r++)
            {
                string[] row = rows[r];

                if (row == null)
                    continue;

                int name = HeaderNames.IndexOf(row, HeaderNames.PartName);
                int code = HeaderNames.IndexOf(row, HeaderNames.PartCode);

                if (name < 0 || code < 0 || name == code)
                    continue;

                // 有「枠区分」的是上面那种「単純部品表」，那边已经收过（认不出才轮到本方法）
                if (FindColumn(row, FrameKeyword) >= 0)
                    continue;

                // 表題欄的「部品名称：ロットNO.ラベル」是标签 + 值，不是列名 → 不是表头
                if (HasLabelValue(Cell(row, name)) || HasLabelValue(Cell(row, code)))
                    continue;

                headerRow = r;
                nameColumn = name;
                codeColumn = code;
                quantityColumn = FindColumn(row, QuantityKeyword);
                basicSizeColumn = FindColumn(row, BasicSizeKeyword);
                cutSizeColumn = FindColumn(row, CutSizeKeyword);

                break;
            }

            if (headerRow < 0)
                return null;

            var detail = new List<string[]>();

            for (int r = headerRow + 1; r < rows.Count; r++)
            {
                string[] row = rows[r];

                if (CsvFile.IsEmptyRow(row))
                    continue;   // 合并单元格 / 表格线造成的空行

                string code = Cell(row, codeColumn).Trim();
                string name = Cell(row, nameColumn).Trim();

                if (code.Length == 0 || name.Length == 0)
                    continue;

                if (IsPlaceholderCode(code))
                {
                    skipped++;  // 部品コード 是「－」这类占位符 → 这条明细不收
                    continue;
                }

                // 表中间重复出现的表头行
                if (HeaderNames.IsMatch(code, HeaderNames.PartCode) || HeaderNames.IsMatch(name, HeaderNames.PartName))
                    continue;

                // 品番的样子（含数字、不是漢字假名）—— 表題欄的自由行不会混进来
                if (!LooksLikePartCode(code))
                    continue;

                // 这种表没有品種 / 枠区分 列 → 两格留空
                detail.Add(new string[]
                {
                    string.Empty, name, string.Empty, code,
                    CellOf(row, quantityColumn), CellOf(row, basicSizeColumn), CellOf(row, cutSizeColumn)
                });
            }

            return detail;
        }

        /// <summary>
        /// 这一格是不是「列名 + 值」的标签写法（<c>部品名称：ロットNO.ラベル</c> / <c>部品番号:LAVS-639</c>）——
        /// 表題欄那种写法不是表头。列名格里带全角 / 半角冒号即算。
        /// </summary>
        private static bool HasLabelValue(string cell)
        {
            string s = CsvFile.Normalize(cell);

            return s.IndexOf('：') >= 0 || s.IndexOf(':') >= 0;
        }

        /// <summary>
        /// 这一格像不像部品番号：至少 3 个字、含数字、且不含漢字・假名
        /// （<c>LAVS-639</c> / <c>MEACC428</c> / <c>TJ-3086</c> 都是；
        /// 表題欄的「承認日」「加地謙一」「1/1」这类自由文字不是）。
        /// </summary>
        private static bool LooksLikePartCode(string code)
        {
            string s = CsvFile.Normalize(code);

            if (s.Length < 3)
                return false;

            bool digit = false;

            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];

                if (ch >= '0' && ch <= '9')
                {
                    digit = true;
                    continue;
                }

                // 漢字（CJK）・平假名・片假名・半角片假名 出现即不是品番
                if ((ch >= '\u3040' && ch <= '\u30FF')
                    || (ch >= '\u3400' && ch <= '\u9FFF')
                    || (ch >= '\uFF66' && ch <= '\uFF9F'))
                    return false;

                // 全角の句読点・記号（「：」「／」など）も品番には出ない
                if (ch >= '\u3000' && ch <= '\u303F')
                    return false;
            }

            return digit;
        }

        /// <summary>
        /// 在一行里找含 <paramref name="keyword"/> 的列（左 → 右），-1 表示这一行没有这一列。
        /// </summary>
        private static int FindColumn(string[] row, string keyword)
        {
            if (row == null)
                return -1;

            for (int c = 0; c < row.Length; c++)
            {
                if (CsvFile.Normalize(row[c]).IndexOf(keyword, StringComparison.Ordinal) >= 0)
                    return c;
            }

            return -1;
        }

        // ── 表头判定 ─────────────────────────────────────────────────────────

        /// <summary>这一行的列名里是否有重复（同一列名出现 ≥ 2 次）—— 多列重复表的标志。</summary>
        private static bool HasDuplicateName(string[] row)
        {
            if (row == null)
                return false;

            for (int c = 0; c < row.Length; c++)
            {
                string name = CsvFile.Normalize(row[c]);

                if (name.Length == 0)
                    continue;

                for (int d = c + 1; d < row.Length; d++)
                {
                    if (CsvFile.Normalize(row[d]) == name)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 这一行是不是「列名重复的表头 / 子表头」（<c>部品コード,員数,部品コード,員数 …</c>）：
        /// 有重复的格子、且重复的那个写得**像列名**（不是品番、不是 <c>－</c> 这类占位符）。
        ///
        /// 为什么要看像不像列名：明细行里同一个品番写两遍时 <see cref="HasDuplicateName"/> 也成立
        /// （<c>品種 × 部品名 マトリクス表</c> 的明细行 <c>片引戸,MEA△△415,MEA□□418,MEA△△415,…</c>，
        /// <c>H325U10011</c> 的 <c>table_01</c> 就是），当成表头就会把整张表按「块」摊错
        /// —— 那张表以前只摊出 8 条（应该 22 条）。
        /// </summary>
        private static bool IsRepeatedHeaderRow(string[] row)
        {
            if (!HasDuplicateName(row))
                return false;

            string name = MostRepeatedName(row);

            return name != null && !IsPlaceholderCode(name) && !LooksLikePartCode(name);
        }

        /// <summary>
        /// 在 <paramref name="blockRow"/> 及它上方这两三行里找含「品種」的列（表头常被拆成两行），
        /// -1 表示这张表没有品種列 → 不是这种表。
        /// </summary>
        private static int FindVarietyColumn(List<string[]> rows, int blockRow)
        {
            int last = Math.Min(blockRow, rows.Count - 1);

            for (int r = 0; r <= last; r++)
            {
                string[] row = rows[r];

                for (int c = 0; c < row.Length; c++)
                {
                    if (CsvFile.Normalize(row[c]).IndexOf(VarietyKeyword, StringComparison.Ordinal) >= 0)
                        return c;
                }
            }

            return -1;
        }

        /// <summary>
        /// 出现次数最多的列名（≥ 2 次）；次数相同时取最左边那个 —— 图纸上块的首列是品番
        /// （「部品コード」），右邻是「員数」，两者都重复 N 次，靠左优先正好选中品番列。
        /// </summary>
        private static string MostRepeatedName(string[] row)
        {
            string best = null;
            int bestCount = 0;

            for (int c = 0; c < row.Length; c++)
            {
                string name = CsvFile.Normalize(row[c]);

                if (name.Length == 0)
                    continue;

                int count = 0;

                for (int d = 0; d < row.Length; d++)
                {
                    if (CsvFile.Normalize(row[d]) == name)
                        count++;
                }

                if (count > bestCount)
                {
                    best = name;
                    bestCount = count;
                }
            }

            return bestCount >= 2 ? best : null;
        }

        // ── 块标题 / 单元格 ──────────────────────────────────────────────────

        /// <summary>
        /// 块的品名：从「列名重复行」往上找该块起始列最后一个非空单元格，去掉「部品名 ：」前缀。
        /// </summary>
        private static string BlockName(List<string[]> rows, int blockRow, int column)
        {
            for (int r = blockRow - 1; r >= 0; r--)
            {
                string text = Cell(rows[r], column).Trim();

                if (text.Length > 0)
                    return StripPrefix(text);
            }

            return string.Empty;
        }

        /// <summary>去掉「部品名 ：」「部品名：」这类前缀（全角 / 半角冒号都认）。</summary>
        private static string StripPrefix(string text)
        {
            string s = text.Trim();

            int colon = s.LastIndexOf('：');

            if (colon < 0)
                colon = s.LastIndexOf(':');

            if (colon >= 0)
            {
                string tail = s.Substring(colon + 1).Trim();

                if (tail.Length > 0)
                    return tail;
            }

            if (s.StartsWith(PartNameKeyword, StringComparison.Ordinal))
                s = s.Substring(PartNameKeyword.Length).Trim();

            return s;
        }

        /// <summary>
        /// 取一格的文本（去首尾空白）：列号是 <c>-1</c>（这张表 / 这一块没有那一列）或越界 → 空串。
        /// 输出里「取不到就留空」的列（枠区分 / 員数 / 基本寸法 / 切断寸法）都走这里。
        /// </summary>
        private static string CellOf(string[] row, int column)
        {
            return column < 0 ? string.Empty : Cell(row, column).Trim();
        }

        /// <summary>取单元格（越界返回空串）。</summary>
        private static string Cell(string[] row, int column)
        {
            if (row == null || column < 0 || column >= row.Length || row[column] == null)
                return string.Empty;

            return row[column];
        }
    }
}
