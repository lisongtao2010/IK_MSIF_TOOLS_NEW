using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CadPartSplitter
{
    /// <summary>
    /// 要領図一覧：读 <c>tables\table_NN.csv</c>，把「項目 / 要領図漢字 / 図番」这种表抽成下游 AI 环节约定的
    /// <c>01_アッセンブリ図_要領図.csv</c>（开关 <see cref="DetectorOptions.BuildAssemblyYoryoList"/> / <c>--no-assembly-yoryo</c>）。
    ///
    /// 图纸上这张小表列的是「这张組立図用到了哪些要領図」，样子（表头被拆散、中间那列没有标题）：
    /// <code>
    /// ,,,項目,,,,,,,,,,,,図番,
    /// ,,,"A,M",,枠 縦枠/小縦枠スペーサー取付要領図,,,,,,H310U10022,
    /// ,,,B,,枠 ロットNO.ラベル 貼付要領図,,,,,,H310U10034,
    /// </code>
    /// 整理成（固定三列，表头统一写全）：
    /// <code>
    /// 項目,要領図漢字,図番
    /// "A,M",枠 縦枠/小縦枠スペーサー取付要領図,H310U10022
    /// B,枠 ロットNO.ラベル 貼付要領図,H310U10034
    /// </code>
    ///
    /// 判定与取列规则：
    ///   · 表头（含「項目」与「図番」的那一行）同时出现这两个词，这张 CSV 才是对象文件，其余表格原样跳过；
    ///   · 「項目」「図番」按列名找；「要領図漢字」多数图纸上**没有标题** ——
    ///     表头里有就按标题找，没有就取「項目」与「図番」之间第一条有内容的列（图纸上它就夹在这两列中间）；
    ///   · 表头下面一行一条，读到「図番」为空或不是 10 位（如 <c>H310U10022</c>）就停 ——
    ///     再往下是表題欄、変更履歴表之类的杂行，不是要領図一覧。
    ///
    /// 除了上面这种「項目 / 要領図漢字 / 図番」分列的写法，还有些图纸把 項目 与 図番 **写在同一格里**、
    /// 或者**折成两行**，而且没有 要領図漢字（这一列留空）：
    /// <code>
    /// A部詳細：H325U10011      ← 冒号分隔
    /// A部・・・H325U10011        ← 点线分隔（・・・ / … / ・ 都认）
    /// A部・・・                  ← 図番 落到下一行、下一格
    /// H325U10011
    /// </code>
    /// 还有一种**整张表都没有表头**、項目 与 図番 **分列在同一行的两格里**的写法（H310U10011 那种組立図）：
    /// <code>
    /// ,,,A,M,,,,,,,,,,,,,,,,,,,,,,H310U10022     ← 項目 在第 15 格、図番 在第 31 格
    /// ,,,B,,,,,,,,,,,,,,,,,,,,,,,,H310U10034
    /// </code>
    /// 这类「内联写法」不看表头，逐格扫：先按 図番 的模式（字母 + 3 位数字 + 字母 + 5 位数字，共 10 位）
    /// 抠出 図番，再把 図番 前面那一截剥掉「部 / 詳細 / ： / ・・・ / …」等饰词，剩下的 <c>A</c> / <c>B</c>
    /// （或 <c>A,M</c>）就是 項目；只有 項目 没有 図番 的格子记下来，等紧跟着的 図番 格子配对；
    /// 分列在同一行时按行配对（这一行必须**恰好** 1 个純 項目 格 + 1 个 図番，多一个都不猜）。
    /// 两条路径按 図番 去重（表格路径先跑，内联只补漏），光秃秃的 図番（表題欄等）不收。
    ///
    /// 图纸上「項目」那一格**纵向合并了两行**时（文字写在两行正中间，H094U10003 那种写法），表格 CSV
    /// 只把 <c>D</c> 写在第 5 行、第 6 行是空的 —— 这儿借 <c>tables_info.json</c> 里同一个单元格的
    /// <c>row_span</c>（≥ 2）把被它盖住的行补上同一个 項目（要領図漢字 那一列同样处理）；
    /// 读不到 JSON、或那格在框线层面没并成一块（<c>row_span</c> 仍是 1）时沿用上一行的 項目，结果一样。
    /// 只有「図番」是 10 位的行才会补，表題欄之类的杂行不会被填。
    ///
    /// **图纸上根本没画表格**的組立図（H527U10001）把这张索引写成自由文字，只有一行说明 + 几行「部詳細」：
    /// <code>
    /// 下記に示す図面を参照のこと。
    /// A部詳細：H325U10011
    /// B部詳細：H325U10011
    /// C部詳細：H325U10012
    /// </code>
    /// 这种图上 `tables\*.csv` 一条也没有（连表題欄都没框线），所以除了表格路径，本类还读**整页文字行**
    /// （<see cref="PartTextExtractor.ReadTextLines"/>，与 <c>parts_text.txt</c> 同一套分行 / 拼字规则）
    /// 再抽一遍：`項目` 按图纸上写的**保留到「部」**（`A部` / `B部` …，表格路径那种剥成 `A` 的规则不适用），
    /// `要領図漢字` 留空，`図番` 仍是 10 位。
    /// 抽法是「先把 `～部` 与 10 位 `図番` 分别找出来、再按位置配对」（<see cref="ReadFreeTextIndex"/>）：
    /// 两者之间写什么（`詳細` / `：` / `・・・` / 空格 / 汉字注释）都不影响，`A部詳細：` 与 `図番` 折成两行
    /// （往上 3 行内）也配得上；全角字母数字先按 Unicode FormKC 化成半角再认。
    /// 配上几条、重复合并几条、几条没配上 `項目`（连原文一起）都写进日志 —— 收少了能一眼看出原因。
    /// 这种写法本身就是組立図的索引，所以**图面分类没判出来（未识别）时也靠它把这张图认成アッセンブリ図**，
    /// 否则这类图纸永远出不了这份清单。同一个 `図番` 挂在两个 `項目` 上（上面的 A部 / B部）两条都留，
    /// 只有「表格路径已经收过的 `図番`」才不重复收。
    ///
    /// 编码与其它清单一致：CSV 用带 BOM 的 UTF-8 + CRLF + RFC 4180 转义（Excel 双击即可打开），
    /// 一格一行（单元格内的换行压成 `~`）。多张表按文件名顺序追加到同一个文件；只读 <c>tables\*.csv</c>
    /// 与 PDF 文字层，不改动任何已有产物（除了图面分类不符时删掉自己上一次写的同名文件）。
    ///
    /// **只有アッセンブリ図（組立図）才写这份清单**（要領図一覧本来就是組立図上的索引）：图面分类读自分类清单
    /// <c>classify\PdfInfoList.txt</c>（<see cref="PdfInfoListBuilder"/> 在流水线里先跑一步生成；清单缺失 /
    /// 没有这张图时退回按 <c>tables\*.csv</c> 现判，同一套规则）。基本図 / 加工図 / 要領図 / 特注 一律跳过，
    /// 并删掉上一次运行可能留下的同名产物。**「未识别」是例外**：没画表格的图纸判不出分类（表題欄也没框线），
    /// 这时只要页面上写着「A部詳細：H…」这种要領図索引，就按アッセンブリ図 处理并生成 —— 没有这种写法才跳过。
    /// </summary>
    public static class AssemblyYoryoBuilder
    {
        /// <summary>
        /// 下游约定的文件名（写在输出目录根，与 <c>01_アッセンブリ図_部品機能情報.csv</c> 同级）。
        /// 编号沿用「01 = アッセンブリ図 系产物」的约定，后面的 <c>要領図</c> 指内容：
        /// 这张組立図用到的要領図一覧（項目 / 要領図漢字 / 図番）。
        /// </summary>
        public const string OutputFileName = "01_アッセンブリ図_要領図.csv";

        /// <summary>「項目」列名（A、B、F … 或 <c>A,M</c> 这种合写；表头里一定有）—— 输出表头用这个名字。</summary>
        private const string ItemHeader = "項目";

        /// <summary>「図番」列名：要領図的図番（表头里一定有，值固定 10 位）—— 输出表头用这个名字。</summary>
        private const string NumberHeader = "図番";

        /// <summary>「要領図漢字」列名：**多数图纸这一列没有标题**，能找到更好，找不到就按位置推断。</summary>
        private const string NameHeader = "要領図漢字";

        /// <summary>
        /// 认表头时「項目」认的所有写法：图纸上也有写成「品目」的（同义组见 <see cref="HeaderNames.Item"/>）。
        /// 输出表头仍然固定写「項目」（下游按这个名字取列）。
        /// </summary>
        private static readonly string[] ItemHeaders = HeaderNames.Item;

        /// <summary>
        /// 认表头时「図番」认的所有写法：図番 / 図面番号 / **部品番号** / 部品コード / 品番 / 型式 / 型番
        /// （<see cref="HeaderNames.DrawingNumber"/>）。H094U10008 那种图纸这一列的标题写的是「部品番号」，
        /// 只认「図番」两个字就会把整张要領図一覧当成「不认识的表」跳过 —— 于是一条都取不到、文件不生成。
        /// 值仍然要过 10 位（<see cref="NumberLength"/>）这一关，所以部品表（品番 5~7 位）不会被误收。
        /// </summary>
        private static readonly string[] NumberHeaders = HeaderNames.DrawingNumber;

        /// <summary>
        /// 认「要領図漢字」列时认的所有写法：要領図漢字 / 要領図名称 / 図面名称 / 部品名 / 品名 / 名称
        /// （<see cref="HeaderNames.YoryoName"/>）。这一列多数图纸没有标题，有标题也只是换个叫法。
        /// </summary>
        private static readonly string[] NameHeaders = HeaderNames.YoryoName;

        /// <summary>図番 的长度：<c>H310U10022</c> 这类固定 10 位；不是 10 位就说明数据区结束了。</summary>
        private const int NumberLength = 10;

        /// <summary>
        /// 図番 的写法：<c>H310U10022</c> 这类「字母 + 3 位数字 + 字母 + 5 位数字」共 10 位。
        /// 只在「内联写法」扫描里用（<c>A部詳細：H325U10011</c> / <c>A部・・・H325U10011</c>）：
        /// 先从一格文字里把 図番 抠出来，図番 前面剩下的那一截才是 項目。
        /// 前后各加一个「不能再贴着字母 / 数字」的断言，免得从更长的编号中间截出 10 位来
        /// （<c>XH325U10011</c> 这种不算）。
        /// </summary>
        private static readonly Regex InlineNumberPattern =
            new Regex("(?<![A-Za-z0-9])[A-Za-z]\\d{3}[A-Za-z]\\d{5}(?![A-Za-z0-9])",
                      RegexOptions.CultureInvariant);

        /// <summary>内联写法剥完饰词后，剩下的必须长这样才算 項目：<c>A</c> / <c>B</c> / <c>A,M</c>。</summary>
        private static readonly Regex ItemPattern =
            new Regex("^[A-Za-z](?:[,、][A-Za-z])*$", RegexOptions.CultureInvariant);

        /// <summary>
        /// 「要領図漢字」格子里应该有的字：漢字 / ひらがな / カタカナ（半角カナ也算）。
        /// 項目 永远只是英文字母（<c>A</c> / <c>A,M</c>），要領図漢字 一定是漢字假名 ——
        /// 两列取反了（項目 里蹦出漢字、要領図漢字 里只有 A,M）按这一条就能判出来。
        /// </summary>
        private static readonly Regex KanjiPattern =
            new Regex("[\\u3040-\\u30FF\\u3400-\\u9FFF\\uFF66-\\uFF9F]", RegexOptions.CultureInvariant);

        /// <summary>
        /// 内联写法里夹在 項目 与 図番 之间的饰词（<c>A部詳細：</c> 的「詳細」「部」）：
        /// 从后往前一层层剥，剥到只剩 <c>A</c> 这种单个字母为止。
        /// </summary>
        private static readonly string[] ItemTailWords = { "詳細", "部" };

        /// <summary>
        /// 同上，但剥的是分隔符 / 空白 / 行内换行占位符 `~`（<c>A部・・・</c> / <c>A部：</c> / <c>A部…</c>）。
        /// 图纸上「・・・」常被拆成三个「・」，所以点线也按单字符剥。
        /// </summary>
        private const string ItemTailChars = "：:・･…‥，,、／/\\～~－-―ー_．.。";

        /// <summary>
        /// 「A部・・・」只写了 項目、図番 在下一格 / 下一行时，最多隔几行还算配对 ——
        /// 超过就当作没配对（免得跨过表題欄把不相干的 図番 拉进来）。
        /// 表格路径（往下看）与页面自由文字（往上找）共用这个阈值。
        /// </summary>
        private const int MaxPendingRowSpan = 3;

        /// <summary>
        /// 页面**自由文字**里的「項目」写法（图纸上没画表格时用，见 <see cref="ReadFreeTextIndex"/>）：
        /// <c>A部</c> / <c>A,B部</c> / <c>A、B部</c> / <c>A・B部</c> / <c>A～C部</c>（范围照原样保留）。
        ///
        /// 与表格路径的内联写法不同：那里剥掉 `部` 只留字母（`A`），这里按需求**保留到「部」**（`A部` / `B部`），
        /// 因为图纸上原文就是这么写的（`A部詳細：H325U10011`），下游要的 `項目` 也就是 `A部`。
        ///
        /// 认之前整行先过 <see cref="NormalizeFreeText"/>（Unicode FormKC），全角 <c>Ａ</c> / <c>，</c> / <c>～</c>
        /// 会化成半角，所以这里只写半角形式；用全角字母画的图纸也认。
        /// 字母与「部」之间允许空格：文字层把 <c>A部</c> 拆成两个词时拼行会补一个空格（<c>A 部詳細：…</c>）。
        /// 前面 <c>(?&lt;![A-Za-z])</c> 只防「从字母串中间起头」（<c>ABC部</c> 不算）。
        /// </summary>
        private static readonly Regex FreeTextItemPattern = new Regex(
            @"(?<![A-Za-z])(?<item>[A-Za-z](?:\s*[,、・.~\-]\s*[A-Za-z])*\s*部)",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// 页面自由文字里的 10 位 <c>図番</c>（<c>H325U10011</c>）：允许中间夹空格 / 连字符
        /// （<c>H325-U10011</c> / <c>H325 U10011</c>），抠出来后再去掉分隔符、用
        /// <see cref="IsWholeNumber"/> 验一遍「整格 10 位」。
        ///
        /// 两头的断言防「从更长的编号中间截出 10 位」：前面不能贴字母（<c>XH325U10011</c> 不算），
        /// 后面不能贴数字（<c>H325U100111</c> 不算）—— 但**字母紧跟要放过**：
        /// 相邻两组连写（<c>…H325U10011B部詳細：…</c>，拼行时没补空格）是正常写法。
        /// </summary>
        private static readonly Regex FreeTextNumberPattern = new Regex(
            @"(?<![A-Za-z])(?<number>[A-Za-z]\s*-?\s*\d{3}\s*-?\s*[A-Za-z]\s*-?\s*\d{5})(?![0-9])",
            RegexOptions.CultureInvariant);

        /// <summary>日志里最多打几条「没配上 項目 的図番」所在行的原文。</summary>
        private const int MaxOrphanSamples = 5;

        /// <summary>那些原文每条最多显示多少字（太长截断，免得一行日志刷屏）。</summary>
        private const int OrphanSampleLength = 60;

        /// <summary>找表头（含「項目」+「図番」的那一行）时最多看前几行。</summary>
        private const int MaxHeaderScanRows = 12;

        /// <summary>推断「要領図漢字」列时，看表头下面几行来判断哪一列有内容。</summary>
        private const int NameProbeRows = 3;

        /// <summary>输出表头：固定三列，下游按列取数。</summary>
        private static readonly string[] OutHeader = { ItemHeader, NameHeader, NumberHeader };

        /// <summary>
        /// 同 <see cref="Build(string, DetectorOptions, string, int, int, int)"/>，但不提供 PDF ——
        /// 只跑表格路径（<c>tables\*.csv</c>），不做「页面自由文字」兜底。
        /// </summary>
        /// <returns>写出的要領図条数（不含表头）；0 表示没有生成文件。</returns>
        public static int Build(string outputDirectory, DetectorOptions opts)
        {
            return Build(outputDirectory, opts, null, 0, 0, 0);
        }

        /// <summary>
        /// 扫描 <paramref name="outputDirectory"/> 下的 <c>tables\*.csv</c>（以及页面自由文字）并写出
        /// <c>01_アッセンブリ図_要領図.csv</c>（图面分类不是「アッセンブリ図」时不写）。
        ///
        /// 两条来源：
        ///   ① 画成表格的要領図一覧 —— 读 <c>tables\*.csv</c>（结构化三列 / 内联写法 / 同行分列）；
        ///   ② 只写在页面上的自由文字 —— 有些組立図**根本没画表格**，只写
        ///      <c>下記に示す図面を参照のこと。</c> + <c>A部詳細：H325U10011</c>…<c>G部詳細：H325U10016</c>
        ///      （H527U10001 就是这种）。这时读整页文字行
        ///      （<see cref="PartTextExtractor.ReadTextLines"/>），按 <see cref="FreeTextPairPattern"/> 抽
        ///      「`A部` + 10 位 図番」；`項目` 按需求**保留到「部」**（`A部` / `B部` …）、`要領図漢字` 留空。
        ///      这种图上没有表格 → 表題欄也判不出图面分类，所以「分类未识别 + 页面上有这种索引」时
        ///      也按アッセンブリ図处理（否则这类图纸永远出不了这份清单）。
        ///
        /// 两条来源按 図番 去重（表格里的 項目 更可靠，重复的不再收）。
        /// </summary>
        /// <param name="outputDirectory">该图纸的输出目录。</param>
        /// <param name="opts">参数 / 日志（可为 null）。</param>
        /// <param name="pdfPath">
        /// 原 PDF 路径（读页面文字行用）；null 表示不读文字层（只跑表格路径）。
        /// </param>
        /// <param name="pageIndex">PDF 页码（0 起）。</param>
        /// <param name="renderWidth">本次渲染的像素宽（文字坐标换算用；≤ 0 时跳过文字层）。</param>
        /// <param name="renderHeight">本次渲染的像素高（同上）。</param>
        /// <returns>写出的要領図条数（不含表头）；0 表示没有生成文件。</returns>
        public static int Build(string outputDirectory, DetectorOptions opts, string pdfPath,
                                int pageIndex, int renderWidth, int renderHeight)
        {
            DetectorOptions o = opts ?? new DetectorOptions();

            if (!o.BuildAssemblyYoryoList)
            {
                o.Log("[INFO] 01_アッセンブリ図_要領図 抽取已关闭（BuildAssemblyYoryoList = false）。");
                return 0;
            }

            if (string.IsNullOrWhiteSpace(outputDirectory))
                return 0;

            // 只有アッセンブリ図（組立図）才有这份索引：先读分类清单 classify\PdfInfoList.txt
            // （流水线刚写好的图面分类），清单缺失 / 没有这张图时退回按 tables\*.csv 现判
            // （与清单同一套规则）。判成别的图类就什么都不写，并清掉上一次留下的同名产物。
            string drawingNumber = PdfInfoListBuilder.DrawingNumberFromOutputDirectory(outputDirectory);
            string categorySource;
            string category = PdfInfoListBuilder.ResolveCategory(outputDirectory, drawingNumber, o, out categorySource);

            // 页面自由文字里的索引（「A部詳細：H325U10011」…）：图纸上**没画表格**时唯一的线索。
            // 先读出来：未识别（没有表格 → 表題欄也判不出来）时靠它决定这张图算不算アッセンブリ図。
            List<string[]> freeIndex;

            if (!PdfInfoListBuilder.IsAssemblyCategory(category))
            {
                if (!string.IsNullOrEmpty(category))
                {
                    o.Log("[INFO] 01_アッセンブリ図_要領図：图面分类「" + category
                          + "」不是アッセンブリ図，跳过（并清掉旧的同名产物）。");

                    DeleteStaleOutput(outputDirectory, o);
                    return 0;
                }

                // 未识别：页面上写着「A部詳細：H…」这种要領図索引时，按アッセンブリ図处理 ——
                // 这种写法本身就是組立図上的索引（要領図 / 加工図 上不会这么写）。
                freeIndex = ReadFreeTextIndex(pdfPath, pageIndex, renderWidth, renderHeight, drawingNumber, o);

                if (freeIndex.Count == 0)
                {
                    o.Log("[INFO] 01_アッセンブリ図_要領図：图面分类「未识别」，页面上也没有"
                          + "「A部詳細：H…」这种要領図索引，跳过（并清掉旧的同名产物）。");

                    DeleteStaleOutput(outputDirectory, o);
                    return 0;
                }

                o.Log("[INFO] 01_アッセンブリ図_要領図：图面分类「未识别」，但页面上有"
                      + "「A部詳細：H…」这种要領図索引 " + freeIndex.Count + " 条，按アッセンブリ図处理。");
            }
            else
            {
                o.Log("[INFO] 01_アッセンブリ図_要領図：图面分类「アッセンブリ図」（依据：" + categorySource + "），开始抽取。");

                freeIndex = ReadFreeTextIndex(pdfPath, pageIndex, renderWidth, renderHeight, drawingNumber, o);
            }

            string csvDir = Path.Combine(outputDirectory, "tables");

            // 没有 tables 目录 / 目录里没有 CSV（这张图根本没画表格）也照跑：
            // 下面还有「页面自由文字」这条路（H527U10001 那种只写着
            // 「下記に示す図面を参照のこと。/ A部詳細：H325U10011 …」的組立図）。
            string[] files = Directory.Exists(csvDir)
                                 ? Directory.GetFiles(csvDir, "*.csv")
                                 : new string[0];

            // 文件名补零（table_01 … table_10），按名字排序即图纸上从上到下的顺序
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            if (files.Length == 0)
            {
                o.Log("[INFO] 01_アッセンブリ図_要領図：tables\\*.csv 里没有表格 CSV（这张图没画表格），"
                      + "只按页面自由文字（「A部詳細：H…」）识别。");
            }

            // tables_info.json 里的「合并单元格」信息：图纸上 項目 那一格纵向合并了 5、6 两行时
            // （文字写在两行正中间），CSV 只在左上角那一行写着 D、第 6 行是空的 ——
            // 靠 row_span 把被覆盖的行补上，明细才不会丢掉一个 項目。
            Dictionary<string, TableMergeInfo> merges = LoadMergeInfo(outputDirectory, o);

            var output = new List<string[]>();

            // 内联写法按「項目 + 図番」去重：同一张要領図写了两遍只收一条，
            // 但同一个 図番 挂在两个 項目 上（图纸上确实会这样写）要两条都留。
            var seenInline = new HashSet<string>(StringComparer.Ordinal);

            // 结构化路径（表头 + 三列）收过的 図番：内联只补漏，不跟它重复。
            var headerNumbers = new HashSet<string>(StringComparer.Ordinal);

            int headerTables = 0;   // 表头含「項目」+「図番」的表张数（日志 / WARN 用）
            int sourceTables = 0;   // 真正取到条数的表张数（结构化 + 内联）

            foreach (string file in files)
            {
                List<string[]> table = LoadCsv(file);

                if (table == null || table.Count == 0)
                    continue;

                int itemColumn;
                int numberColumn;
                string itemHeader;
                string numberHeader;
                int headerRow = FindHeaderRow(table, out itemColumn, out numberColumn,
                                              out itemHeader, out numberHeader);
                int before = output.Count;

                if (headerRow >= 0)
                {
                    // ① 结构化路径：表头同含「項目」+「図番」（都按同义组认），三项各占一列
                    headerTables++;

                    int nameColumn = FindNameColumn(table, headerRow, itemColumn, numberColumn);

                    // 表头的列号未必等于明细的列号：「項目」的标题常写在跨列合并的格子里，
                    // 或表头行比明细行多几个行首空列（H467U10002 的 table_04：「A,M」在第 15 格、
                    // 「項目」标题却落在第 17 格），照表头的列号去取，会把 要領図漢字 读成 項目、
                    // 把 A,M 读成 要領図漢字 —— 整份清单两列内容反了。所以再拿几行明细按值的长相校一次。
                    int headerItemColumn = itemColumn;
                    int headerNameColumn = nameColumn;
                    int headerNumberColumn = numberColumn;

                    AlignColumnsBySample(table, headerRow, ref itemColumn, ref nameColumn, ref numberColumn);

                    if (itemColumn != headerItemColumn || nameColumn != headerNameColumn
                        || numberColumn != headerNumberColumn)
                    {
                        o.Log("[INFO] 01_アッセンブリ図_要領図：" + Path.GetFileName(file)
                              + " 表头与明细的列号对不上（表头「" + itemHeader + "」在第 " + headerItemColumn
                              + " 列、明细的项目值在第 " + itemColumn + " 列），已按值校正三列。");
                    }

                    // 这张表的合并单元格信息（tables_info.json）：CSV 的行与 JSON 的行怎么对应
                    // 交给 MergeRowOf 判断，对不上时它返回 -1，那一行就走「沿用上一行的 項目」。
                    TableMergeInfo merge;

                    if (!merges.TryGetValue(Path.GetFileNameWithoutExtension(file), out merge))
                        merge = null;

                    int filledByMerge = 0;
                    int swapped = 0;
                    string lastItem = string.Empty;

                    for (int r = headerRow + 1; r < table.Count; r++)
                    {
                        string[] row = table[r];
                        string number = CsvFile.Normalize(Cell(row, numberColumn));

                        // 図番 空了 / 不是 10 位 → 数据区结束（再往下是表題欄、変更履歴等杂行）
                        if (number.Length != NumberLength)
                            break;

                        headerNumbers.Add(number);

                        string item = CsvFile.SingleLine(Cell(row, itemColumn).Trim());
                        string name = CsvFile.SingleLine(Cell(row, nameColumn).Trim());
                        bool itemFilled = false;
                        int jsonRow = MergeRowOf(merge, table.Count, r);

                        if (jsonRow > 0)
                        {
                            if (item.Length == 0)
                            {
                                item = MergedText(merge, jsonRow, itemColumn);
                                itemFilled = item.Length > 0;
                            }

                            if (name.Length == 0)
                                name = MergedText(merge, jsonRow, nameColumn);
                        }

                        // 作完再查一遍：項目 一定是英文字母（A / A,M），要領図漢字 一定是漢字假名。
                        // 两格明显反了（項目 格里是漢字、要領図漢字 格里只有 A,M）就对调 ——
                        // 表头与明细错开得再多几格，也不会把 A,M 写进要領図漢字。
                        if (name.Length > 0 && IsItem(name) && !IsItem(item))
                        {
                            string swap = item;
                            item = name;
                            name = swap;
                            swapped++;
                        }

                        // 纵向合并的 項目 补不出来时（图纸上文字写在两行正中间，框线那边可能没并成一个
                        // 单元格，tables_info.json 里两格的 row_span 都是 1）—— 沿用上一行的 項目。
                        // 図番 是 10 位才走到这里，表題欄之类的杂行不会被填
                        if (item.Length == 0 && lastItem.Length > 0)
                        {
                            item = lastItem;
                            itemFilled = true;
                        }

                        if (itemFilled)
                            filledByMerge++;

                        if (item.Length > 0)
                            lastItem = item;

                        output.Add(new[] { item, name, number });
                    }

                    o.Log("[INFO] 01_アッセンブリ図_要領図：" + Path.GetFileName(file)
                          + "（項目列 " + itemColumn + "「" + itemHeader + "」/ 要領図漢字列 "
                          + (nameColumn < 0 ? "无" : nameColumn.ToString())
                          + " / 図番列 " + numberColumn + "「" + numberHeader + "」）取到 "
                          + (output.Count - before) + " 条"
                          + (filledByMerge > 0 ? "（其中 " + filledByMerge + " 条的 項目 按合并单元格补足）" : string.Empty)
                          + (swapped > 0 ? "（其中 " + swapped + " 条的 項目 / 要領図漢字 按值判定对调）" : string.Empty)
                          + "。");
                }

                // ② 内联路径：「A部詳細：H325U10011」/「A部・・・H325U10011」/
                //    「A部・・・」+ 下一行「H325U10011」这类写法（没有表头，要領図漢字 也没有），
                //    以及「A,M」与「H310U10022」分列在同一行两格里、整张表都没表头的写法
                //    （后者只在没有结构化表头的表里启用，免得跟结构化路径抢同一行）
                int inline = ExtractInline(table, drawingNumber, output, headerNumbers, seenInline,
                                           headerRow < 0);

                if (inline > 0)
                {
                    o.Log("[INFO] 01_アッセンブリ図_要領図：" + Path.GetFileName(file)
                          + "（「A部詳細：」/「A部・・・」/「項目・図番 同行分列」内联写法）补到 " + inline
                          + " 条（要領図漢字 留空）。");
                }

                if (output.Count > before)
                    sourceTables++;
            }

            // ④ 页面自由文字：图纸上没画表格、只写着「下記に示す図面を参照のこと。/ A部詳細：H325U10011 …」时
            //    （freeIndex 在上面已经读好，未识别时还靠它把这张图认成アッセンブリ図）。
            //    与表格 / 内联写法按 図番 去重：表格里的 項目 更可靠，重复的不再收。
            int freeAdded = MergeFreeIndex(freeIndex, output);

            if (freeIndex != null && freeIndex.Count > 0)
            {
                o.Log("[INFO] 01_アッセンブリ図_要領図：页面自由文字（「A部詳細：H…」）"
                      + freeIndex.Count + " 条，补进 " + freeAdded + " 条（要領図漢字 留空）。");
            }

            if (output.Count == 0)
            {
                if (headerTables == 0)
                {
                    o.Log("[INFO] 01_アッセンブリ図_要領図：tables\\*.csv 里既没有表头含「"
                          + string.Join(" / ", ItemHeaders) + "」+「"
                          + string.Join(" / ", NumberHeaders) + "」的表，也没有「A部詳細：H…」/「A部・・・」这类写法，"
                          + "也没有「" + ItemHeader + "」与「" + NumberHeader + "」分列在同一行的写法，"
                          + "页面上也没有「A部詳細：H…」这种自由文字，不生成 "
                          + OutputFileName + "。");
                }
                else
                {
                    o.Log("[WARN] 01_アッセンブリ図_要領図：找到 " + headerTables + " 张「" + ItemHeader + " / "
                          + NumberHeader + "」表，但「" + NumberHeader + "」都不是 " + NumberLength + " 位，不生成 "
                          + OutputFileName + "。");
                }

                return 0;
            }

            string path = Path.Combine(outputDirectory, OutputFileName);

            CsvFile.Write(path, OutHeader, output);

            o.Log("[INFO] 01_アッセンブリ図_要領図：" + sourceTables + " 张表"
                  + (freeAdded > 0 ? " + 页面自由文字" : string.Empty)
                  + " → 共 " + output.Count + " 条要領図，已保存：" + path);

            return output.Count;
        }

        // ── 合并单元格（tables_info.json） ────────────────────────────────────

        /// <summary>
        /// 一张表的「纵向合并单元格」信息，来自 <c>tables_info.json</c>：
        /// 图纸上 項目 那一格纵向合并了 5、6 两行（文字写在两行正中间）时，
        /// <c>tables\table_NN.csv</c> 只把文字写在左上角那一行、被覆盖的行是空的 —— 靠这里补足。
        /// </summary>
        private sealed class TableMergeInfo
        {
            /// <summary>可见行（未划线作废）的原始行号，顺序与 <c>table_NN.csv</c> 的行序一致。</summary>
            public readonly List<int> VisibleRowIndexes = new List<int>();

            /// <summary>跨行数 ≥ 2 且有文字的单元格。</summary>
            public readonly List<MergedCell> MergedCells = new List<MergedCell>();
        }

        /// <summary>一个纵向合并的单元格：起始行 / 列 / 跨几行 / 文字。</summary>
        private sealed class MergedCell
        {
            public int Row;
            public int Column;
            public int RowSpan;
            public string Text;
        }

        /// <summary>
        /// CSV 的第 <paramref name="csvRow"/> 行（从 0 起）对应 <c>tables_info.json</c> 的哪一行（从 1 起）；
        /// 对应不上返回 -1（调用方退回「沿用上一行的 項目」）。
        ///
        /// CSV 的行就是「可见行」（划线作废的行两边都不输出），且 <c>ToVisibleGrid</c> 会给没有单元格的
        /// 缺格带照旧留一行 —— 所以只在两边行数**完全相等**时才敢逐行对应；差一行（CSV 末尾空行、
        /// 缺格带那种表）就返回 -1，宁可不补 `要領図漢字`，也不把别的行的文字搬过来。
        /// </summary>
        private static int MergeRowOf(TableMergeInfo merge, int csvRowCount, int csvRow)
        {
            if (merge == null || csvRow < 0)
                return -1;

            if (merge.VisibleRowIndexes.Count != csvRowCount)
                return -1;

            return merge.VisibleRowIndexes[csvRow];
        }

        /// <summary>
        /// 取「覆盖这一行、这一列」的合并单元格文字；没有任何合并单元格盖住它 → 空串。
        /// </summary>
        /// <param name="merge">这张表的合并信息；null 表示读不到（调用方自己走退路）。</param>
        /// <param name="jsonRow">这一行在 <c>tables_info.json</c> 里的原始行号（从 1 起）。</param>
        /// <param name="column">要补的列号 —— <b>CSV 的列号，从 0 起</b>（<c>項目</c> / <c>要領図漢字</c>），
        /// -1 表示没有这一列。<c>tables_info.json</c> 的 <c>column</c> 从 1 起，这里换算。</param>
        private static string MergedText(TableMergeInfo merge, int jsonRow, int column)
        {
            if (merge == null || column < 0)
                return string.Empty;

            int jsonColumn = column + 1;

            for (int i = 0; i < merge.MergedCells.Count; i++)
            {
                MergedCell cell = merge.MergedCells[i];

                if (cell.Column == jsonColumn && jsonRow >= cell.Row && jsonRow < cell.Row + cell.RowSpan)
                    return cell.Text;
            }

            return string.Empty;
        }

        /// <summary>
        /// 读 <c>tables_info.json</c> 里的合并单元格信息（逐行读本项目自己写出的固定格式，不引 JSON 库）：
        /// 每张表记下「可见行的原始行号」（CSV 的行序 == 可见行序，划线作废的行两边都不输出）
        /// 与 <c>row_span ≥ 2</c> 的单元格（起始行 / 列 / 跨行数 / 文字）。
        ///
        /// 文件不存在 / 读不动时返回空字典 —— 调用方会退回「沿用上一行的 項目」。
        /// </summary>
        private static Dictionary<string, TableMergeInfo> LoadMergeInfo(string outputDirectory, DetectorOptions o)
        {
            var result = new Dictionary<string, TableMergeInfo>(StringComparer.OrdinalIgnoreCase);

            string path = Path.Combine(outputDirectory, "tables_info.json");

            if (!File.Exists(path))
                return result;

            string[] lines;

            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (IOException)
            {
                return result;
            }
            catch (UnauthorizedAccessException)
            {
                return result;
            }

            TableMergeInfo current = null;
            int row = -1;
            int column = -1;
            int rowSpan = 1;
            int rowIndex = -1;
            string value;
            int number;
            bool flag;

            foreach (string raw in lines)
            {
                string line = raw.Trim();

                // 一张表的开头（"name": "table_01",）
                if (TryReadString(line, "\"name\"", out value))
                {
                    current = new TableMergeInfo();
                    result[value] = current;
                    row = -1;
                    column = -1;
                    rowSpan = 1;
                    rowIndex = -1;
                    continue;
                }

                if (current == null)
                    continue;

                // 行容器："index" 记原始行号，"struck" 决定它是否出现在 CSV 里
                if (TryReadInt(line, "\"index\"", out number))
                {
                    rowIndex = number;
                    continue;
                }

                if (TryReadBool(line, "\"struck\"", out flag))
                {
                    if (!flag && rowIndex >= 0)
                        current.VisibleRowIndexes.Add(rowIndex);

                    continue;
                }

                // 单元格：row / column / row_span / text（顺序与 RuledTableWriter 写出的一致）
                if (TryReadInt(line, "\"row\"", out number))
                {
                    row = number;
                    continue;
                }

                if (TryReadInt(line, "\"column\"", out number))
                {
                    column = number;
                    continue;
                }

                if (TryReadInt(line, "\"row_span\"", out number))
                {
                    rowSpan = number;
                    continue;
                }

                if (TryReadString(line, "\"text\"", out value))
                {
                    string text = CsvFile.SingleLine(value).Trim();

                    if (rowSpan >= 2 && row >= 0 && column >= 0 && text.Length > 0)
                    {
                        current.MergedCells.Add(new MergedCell
                        {
                            Row = row,
                            Column = column,
                            RowSpan = rowSpan,
                            Text = text
                        });
                    }

                    continue;
                }
            }

            int tablesWithMerge = 0;

            foreach (TableMergeInfo info in result.Values)
            {
                if (info.MergedCells.Count > 0)
                    tablesWithMerge++;
            }

            if (tablesWithMerge > 0)
            {
                o.Log("[INFO] 01_アッセンブリ図_要領図：tables_info.json 里 " + tablesWithMerge
                      + " 张表有纵向合并单元格（row_span ≥ 2），被它盖住的行会用它左上角那格的文字补足。");
            }

            return result;
        }

        /// <summary>读一行里的 <c>"key": 123</c>（数字）；这行没有这个键返回 false。</summary>
        private static bool TryReadInt(string line, string key, out int value)
        {
            value = 0;

            int at = line.IndexOf(key + ":", StringComparison.Ordinal);

            if (at < 0)
                return false;

            int start = at + key.Length + 1;

            while (start < line.Length && line[start] == ' ')
                start++;

            int end = start;

            while (end < line.Length && line[end] >= '0' && line[end] <= '9')
                end++;

            if (end == start)
                return false;

            return int.TryParse(line.Substring(start, end - start), out value);
        }

        /// <summary>
        /// 读一行里的 <c>"key": "文字"</c>（字符串，反转义 <c>\\ \" \r \n \t</c>）；
        /// 这行没有这个键（或值不是引号开头）返回 false。
        /// </summary>
        private static bool TryReadString(string line, string key, out string value)
        {
            value = null;

            int at = line.IndexOf(key + ":", StringComparison.Ordinal);

            if (at < 0)
                return false;

            int start = at + key.Length + 1;

            while (start < line.Length && line[start] == ' ')
                start++;

            if (start >= line.Length || line[start] != '"')
                return false;

            int end = line.LastIndexOf('"');

            value = end > start ? UnescapeJson(line.Substring(start + 1, end - start - 1)) : string.Empty;

            return true;
        }

        /// <summary>读一行里的 <c>"key": true</c> / <c>"key": false</c>；这行没有这个键返回 false。</summary>
        private static bool TryReadBool(string line, string key, out bool value)
        {
            value = false;

            int at = line.IndexOf(key + ":", StringComparison.Ordinal);

            if (at < 0)
                return false;

            int start = at + key.Length + 1;

            while (start < line.Length && line[start] == ' ')
                start++;

            value = start + 4 <= line.Length
                    && string.Compare(line, start, "true", 0, 4, StringComparison.OrdinalIgnoreCase) == 0;

            return true;
        }

        /// <summary>反转义 <see cref="RuledTableWriter"/> 写出的 JSON 字符串（<c>\\ \" \r \n \t</c>）。</summary>
        private static string UnescapeJson(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0)
                return s ?? string.Empty;

            var sb = new StringBuilder(s.Length);

            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length)
                {
                    sb.Append(s[i]);
                    continue;
                }

                char next = s[++i];

                switch (next)
                {
                    case 'r': sb.Append('\r'); break;
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    default: sb.Append(next); break;   // \\ 与 \" → 原样一个字符
                }
            }

            return sb.ToString();
        }

        // ── CSV 读取 ─────────────────────────────────────────────────────────

        /// <summary>读一张表格 CSV；被 Excel 占用 / 读不动时返回 null（跳过这张，不影响其它表）。</summary>
        private static List<string[]> LoadCsv(string path)
        {
            try
            {
                return CsvFile.Read(path);
            }
            catch (IOException)
            {
                return null;
            }
        }

        // ── 判定 / 取列 ──────────────────────────────────────────────────────

        /// <summary>
        /// 找表头行：同一行里同时有「項目」和「図番」的那一行（-1 = 这张表不是要領図一覧）。
        /// 两个列名常隔着十几个空格子，但都落在表头这一行上，所以按行找。
        ///
        /// 两个列名都按**同义组**认（<see cref="ItemHeaders"/> / <see cref="NumberHeaders"/>）：
        /// 図番 那一列写成「部品番号」「部品コード」「品番」「型式」的图纸也认（H094U10008 就是「部品番号」），
        /// 項目 写成「品目」也认 —— 只是叫法不同，不是另一张表。
        /// </summary>
        /// <param name="rows">一张 <c>table_NN.csv</c> 的全部行。</param>
        /// <param name="itemColumn">項目 列号；没找到表头时也是 -1。</param>
        /// <param name="numberColumn">図番 列号；没找到表头时也是 -1。</param>
        /// <param name="itemHeader">表头上 項目 那一列实际写的字（<c>項目</c> / <c>品目</c> …），日志用。</param>
        /// <param name="numberHeader">表头上 図番 那一列实际写的字（<c>図番</c> / <c>部品番号</c> …），日志用。</param>
        private static int FindHeaderRow(List<string[]> rows, out int itemColumn, out int numberColumn,
                                         out string itemHeader, out string numberHeader)
        {
            itemColumn = -1;
            numberColumn = -1;
            itemHeader = ItemHeader;
            numberHeader = NumberHeader;

            int last = Math.Min(rows.Count, MaxHeaderScanRows);

            for (int r = 0; r < last; r++)
            {
                string[] row = rows[r];

                if (row == null)
                    continue;

                int item = HeaderNames.IndexOf(row, ItemHeaders);
                int number = HeaderNames.IndexOf(row, NumberHeaders);

                // 同一格同时命中两组（一格写着「項目 / 図番」）：不是三项分列的表，跳过这一行
                if (item < 0 || number < 0 || item == number)
                    continue;

                itemColumn = item;
                numberColumn = number;
                itemHeader = HeaderNames.Match(row, item, ItemHeaders) ?? ItemHeader;
                numberHeader = HeaderNames.Match(row, number, NumberHeaders) ?? NumberHeader;

                return r;
            }

            return -1;
        }

        // ── 内联写法（A部詳細：H… / A部・・・H… / A部・・・ + 下一行 図番） ──────────

        /// <summary>
        /// 补收「内联写法」：图纸上不写表头，直接把 項目 与 図番 连在一格里，或者折成两格。
        ///   · <c>A部詳細：H325U10011</c>（冒号分隔）
        ///   · <c>A部・・・H325U10011</c>（点线分隔；<c>…</c> / <c>・</c> / 空格 也认）
        ///   · <c>A部・・・</c> 与 <c>H325U10011</c> 分两行 / 两格（第一格记成 pending，紧跟着的 図番 配对）
        ///   · 項目 与 図番 **分列在同一行的两格里**、整张表都没有表头（<c>allowRowPairing</c> 为 true 时）：
        ///     这一行恰好 1 个純 項目 格 + 1 个 図番 才配对（<see cref="FindSingleRowItem"/>）
        /// 这种写法没有 要領図漢字（留空），項目 取 図番 前面的 <c>A</c> / <c>B</c>（或同行那个 項目 格）。
        ///
        /// 收得克制一点，避免把表題欄之类的东西也当成要領図：
        ///   · 光秃秃的 図番（同一格里図番 前面剥不出 項目、也没有 pending / 同行 項目）不收；
        ///   · 同行配对只在整行「1 个 項目 + 1 个 図番」时成立，多一个就整行不猜；
        ///   · 等于这张组立图自己的 図番（<paramref name="drawingNumber"/>）不收；
        ///   · 结构化路径已经收过的 図番（<paramref name="headerNumbers"/>）不收；
        ///   · 同一「項目 + 図番」组合（<paramref name="seenInline"/>）只收一次 ——
        ///     但同一个 図番 挂在两个 項目 上（图纸上会这样写）要两条都留，所以不能只按 図番 去重。
        /// </summary>
        /// <param name="allowRowPairing">
        /// 是否允许「項目 与 図番 分列在同一行」的配对：只有在**这张表没有结构化表头**时才开，
        /// 有表头的表交给结构化路径（否则同一行会被两条路径各收一次，列语义也容易错位）。
        /// </param>
        /// <returns>本次补收的条数（已追加到 <paramref name="output"/>）。</returns>
        private static int ExtractInline(List<string[]> table, string drawingNumber,
                                         List<string[]> output, HashSet<string> headerNumbers,
                                         HashSet<string> seenInline, bool allowRowPairing)
        {
            int added = 0;
            string pending = null;      // 「A部詳細：」/「A部・・・」这种只写了項目、図番在下面
            int pendingRow = -1;

            for (int r = 0; r < table.Count; r++)
            {
                string[] row = table[r];

                if (row == null)
                    continue;

                // 同行分列（整张表都没有表头）：这一行恰好 1 个純 項目 格 + 1 个 図番 时，
                // 拿那个 項目 给同行的 図番 配对；别的行组合一律 null（不猜，宁可少收）
                string rowItem = allowRowPairing ? FindSingleRowItem(row) : null;

                for (int c = 0; c < row.Length; c++)
                {
                    string text = CsvFile.Normalize(Cell(row, c));

                    if (text.Length == 0)
                        continue;

                    MatchCollection matches = InlineNumberPattern.Matches(text);

                    if (matches.Count == 0)
                    {
                        // 只有「A部詳細：」/「A部・・・」：记下来，等下面那格的 図番
                        // （剥完饰词后必须比原文短 —— 光一个「A」不算，那是普通表格，等结构化路径）
                        string tail = TrimItemTail(text);

                        if (tail.Length < text.Length && IsItem(tail))
                        {
                            pending = tail;
                            pendingRow = r;
                        }

                        continue;
                    }

                    foreach (Match match in matches)
                    {
                        string number = match.Value;
                        string item = TrimItemTail(text.Substring(0, match.Index));

                        if (!IsItem(item))
                            item = string.Empty;

                        // 「A部・・・」+ 下一行「H325U10011」：用上面积下的 項目
                        if (item.Length == 0 && pending != null && r - pendingRow <= MaxPendingRowSpan)
                            item = pending;

                        // 項目 与 図番 分列在同一行的两格里（无表头版式）：用同行那个唯一的 項目
                        if (item.Length == 0 && rowItem != null)
                            item = rowItem;

                        pending = null;

                        if (item.Length == 0)
                            continue;   // 剥不出 項目：不猜（表題欄里的裸 図番）

                        if (headerNumbers.Contains(number))
                            continue;   // 结构化路径已经收过这个 図番

                        if (!seenInline.Add(item + "\u0001" + number))
                            continue;   // 同一「項目 + 図番」组合已经收过

                        if (!string.IsNullOrEmpty(drawingNumber)
                            && string.Equals(number, drawingNumber, StringComparison.OrdinalIgnoreCase))
                            continue;   // 这张组立图自己的 図番，不是要領図

                        output.Add(new[] { item, string.Empty, number });
                        added++;
                    }
                }
            }

            return added;
        }

        /// <summary>
        /// 「同行分列」版式的配对（整张表都没有表头时用）：<c>項目</c> 在某一格、<c>図番</c> 在同行另一格，
        /// 中间隔着十几格空白（H310U10011 的 <c>table_04.csv</c>：項目 第 15 格 / 図番 第 31 格）。
        /// <code>
        /// ,,,A,M,,,,,,,,,,,,,,,,,,,,,,H310U10022
        /// ,,,B,,,,,,,,,,,,,,,,,,,,,,,,H310U10034
        /// </code>
        /// 只在这一行**恰好** 1 个純 項目 格（<c>A</c> / <c>A,M</c>）且**恰好** 1 个 10 位 <c>図番</c> 时配对 ——
        /// 多一个都不猜：表題欄里「図番 + 一串数字」那种格子（<c>H310U10011 0666…</c>）不会有 項目 与它同行，
        /// 変更履歴里的 <c>4 -2</c> / <c>3</c> / <c>2 -1</c> 也不是 項目（<see cref="ItemPattern"/> 只认单个字母）。
        /// </summary>
        /// <returns>配得上的 項目；这一行不满足「1 个 項目 + 1 个 図番」时返回 null。</returns>
        private static string FindSingleRowItem(string[] row)
        {
            string item = null;
            int itemCount = 0;
            int numberCount = 0;

            for (int c = 0; c < row.Length; c++)
            {
                string text = CsvFile.Normalize(Cell(row, c));

                if (text.Length == 0)
                    continue;

                int numbers = InlineNumberPattern.Matches(text).Count;

                if (numbers > 0)
                {
                    numberCount += numbers;
                    continue;
                }

                if (IsItem(text))
                {
                    itemCount++;
                    item = text;
                }
            }

            if (itemCount != 1 || numberCount != 1)
                return null;

            return item;
        }

        /// <summary>
        /// 剥掉 項目 与 図番 之间的饰词：<c>A部詳細：</c> / <c>A部・・・</c> / <c>A部</c> / <c>A部…</c> → <c>A</c>。
        /// 从尾巴往前一层层剥（先剥分隔符 / 空白，再剥「詳細」「部」），剥不动就停。
        /// </summary>
        private static string TrimItemTail(string text)
        {
            string s = CsvFile.Normalize(text);
            bool stripped = true;

            while (stripped && s.Length > 0)
            {
                stripped = false;

                foreach (string word in ItemTailWords)
                {
                    if (s.EndsWith(word, StringComparison.Ordinal))
                    {
                        s = s.Substring(0, s.Length - word.Length);
                        stripped = true;
                    }
                }

                if (s.Length > 0 && ItemTailChars.IndexOf(s[s.Length - 1]) >= 0)
                {
                    s = s.Substring(0, s.Length - 1);
                    stripped = true;
                }
            }

            return s;
        }

        /// <summary>剥完饰词后是不是一个 項目（<c>A</c> / <c>B</c> / <c>A,M</c> 这种）。</summary>
        private static bool IsItem(string text)
        {
            return text.Length > 0 && ItemPattern.IsMatch(text);
        }

        /// <summary>
        /// 找「要領図漢字」列：① 表头里写了这个列名（或同义写法：要領図名称 / 図面名称 / 部品名 / 品名 / 名称，
        /// 见 <see cref="NameHeaders"/>）就直接用；
        /// ② 没标题时取「項目」与「図番」之间第一条有内容的列（图纸上它就夹在这两列中间，
        /// 中间常隔着一两个空格子）；③ 版式不同、夹在中间的都空着时，整行里找第一条有内容的列。
        /// -1 表示实在找不到（此时这一列留空，不猜）。
        /// </summary>
        private static int FindNameColumn(List<string[]> rows, int headerRow, int itemColumn, int numberColumn)
        {
            int named = HeaderNames.IndexOf(rows[headerRow], NameHeaders);

            if (named >= 0 && named != itemColumn && named != numberColumn)
                return named;

            int from = Math.Min(itemColumn, numberColumn) + 1;
            int to   = Math.Max(itemColumn, numberColumn) - 1;

            int between = FirstTextColumn(rows, headerRow, from, to, itemColumn, numberColumn);

            if (between >= 0)
                return between;

            return FirstTextColumn(rows, headerRow, 0, int.MaxValue, itemColumn, numberColumn);
        }

        /// <summary>
        /// 用明细的**值**把三列的位置再校一次：表头的列号与明细的列号未必一致 ——
        /// 「項目」的标题常写在跨列合并的格子里，或表头行比明细行多几个行首空列
        /// （H467U10002 的 <c>table_04</c>：<c>A,M</c> 在第 15 格、「項目」标题落在第 17 格、
        /// 「枠 縦枠/小縦枠スペーサー取付要領図」正好压在「項目」标题那一格上）——
        /// 照表头的列号去取，項目 就读到要領図漢字、要領図漢字 反而读到 <c>A,M</c>，整份清单两列反了。
        ///
        /// 所以看表头下面 <see cref="NameProbeRows"/> 行，按值的长相给每列计分：
        ///   · 整格就是 <c>A</c> / <c>A,M</c>（<see cref="ItemPattern"/>）→ 項目 列；
        ///   · 整格就是 10 位 図番（<see cref="IsWholeNumber"/>）→ 図番 列；
        ///   · 含漢字 / 假名（<see cref="KanjiPattern"/>）→ 要領図漢字 列。
        /// 項目 / 図番 有命中就按值改（值比标题可靠）；要領図漢字 只在这一列「明显不对」
        /// （没找到、与 項目 / 図番 撞列、或那一列在明细里一个漢字都没有）时才换，
        /// 免得把旁边的备注文字列抢过来。命中不了（明细还没写 / 合并单元格空着）就保持原样。
        /// </summary>
        private static void AlignColumnsBySample(List<string[]> rows, int headerRow,
                                                 ref int itemColumn, ref int nameColumn, ref int numberColumn)
        {
            int last = Math.Min(headerRow + NameProbeRows, rows.Count - 1);

            int maxColumns = 0;

            for (int r = headerRow + 1; r <= last; r++)
            {
                string[] row = rows[r];

                if (row != null && row.Length > maxColumns)
                    maxColumns = row.Length;
            }

            if (maxColumns == 0)
                return;

            int[] itemScores = new int[maxColumns];
            int[] nameScores = new int[maxColumns];
            int[] numberScores = new int[maxColumns];

            for (int c = 0; c < maxColumns; c++)
            {
                for (int r = headerRow + 1; r <= last; r++)
                {
                    string text = CsvFile.Normalize(Cell(rows[r], c));

                    if (text.Length == 0)
                        continue;

                    if (IsItem(text))
                        itemScores[c]++;
                    else if (IsWholeNumber(text))
                        numberScores[c]++;
                    else if (KanjiPattern.IsMatch(text))
                        nameScores[c]++;
                }
            }

            int numberAt = BestColumn(numberScores);
            int itemAt = BestColumn(itemScores);
            int nameAt = BestColumn(nameScores);

            if (numberAt >= 0)
                numberColumn = numberAt;

            if (itemAt >= 0 && itemAt != numberColumn)
                itemColumn = itemAt;

            bool nameIsWrong = nameColumn < 0
                               || nameColumn == itemColumn
                               || nameColumn == numberColumn
                               || nameColumn >= maxColumns
                               || nameScores[nameColumn] == 0;

            if (nameAt >= 0 && nameIsWrong && nameAt != itemColumn && nameAt != numberColumn)
                nameColumn = nameAt;
        }

        /// <summary>计分表里命中次数最多的列（并列取最左）；一个都没命中返回 -1。</summary>
        private static int BestColumn(int[] scores)
        {
            int best = -1;

            for (int c = 0; c < scores.Length; c++)
            {
                if (scores[c] > 0 && (best < 0 || scores[c] > scores[best]))
                    best = c;
            }

            return best;
        }

        /// <summary>整格就是一个 図番（<c>H310U10022</c>），不是夹在一句话里。</summary>
        private static bool IsWholeNumber(string text)
        {
            if (text.Length != NumberLength)
                return false;

            Match match = InlineNumberPattern.Match(text);

            return match.Success && match.Index == 0 && match.Length == text.Length;
        }

        /// <summary>
        /// 在表头下面 <see cref="NameProbeRows"/> 行里，找 <paramref name="from"/>~<paramref name="to"/>
        /// 这段列中第一条有内容的列（跳过 項目 / 図番 两列）；都没有内容返回 -1。
        /// </summary>
        private static int FirstTextColumn(List<string[]> rows, int headerRow, int from, int to,
                                           int itemColumn, int numberColumn)
        {
            int last = Math.Min(headerRow + NameProbeRows, rows.Count - 1);

            for (int r = headerRow + 1; r <= last; r++)
            {
                string[] row = rows[r];

                if (row == null)
                    continue;

                int max = Math.Min(to, row.Length - 1);

                for (int c = Math.Max(from, 0); c <= max; c++)
                {
                    if (c == itemColumn || c == numberColumn)
                        continue;

                    if (CsvFile.Normalize(Cell(row, c)).Length > 0)
                        return c;
                }
            }

            return -1;
        }

        /// <summary>取单元格（越界 / 缺列 / null 都给空串）。</summary>
        private static string Cell(string[] row, int column)
        {
            if (row == null || column < 0 || column >= row.Length || row[column] == null)
                return string.Empty;

            return row[column];
        }

        // ── 页面自由文字（没画表格的組立図） ─────────────────────────────────

        /// <summary>
        /// 从页面文字行里抽「A部詳細：H325U10011」这种**自由文字**写法（图纸上没画表格时用）。
        ///
        /// 行由 <see cref="PartTextExtractor.ReadTextLines"/> 给出（同一水平线上的词已拼成一条）。
        /// 做法是「先把 項目 与 図番 都找出来，再按位置配对」，不靠一条吃满整句的正则 ——
        /// 图纸上 項目 与 図番 之间写什么（`詳細` / `：` / `・・・` / 空格 / 汉字注释）都不影响配对：
        ///   · <see cref="FreeTextItemPattern"/> 找「字母 + 部」（`A部` / `A,B部` …），记下行号与行内位置；
        ///   · <see cref="FreeTextNumberPattern"/> 找 10 位 図番，记下行号与行内位置；
        ///   · 図番 同一行前面有 項目 → 配给离它最近的那个；
        ///   · 同一行前面没有 → 往上找最近的 項目（最多 <see cref="MaxPendingRowSpan"/> 行），
        ///     对应「`A部詳細：` 与 図番 折成两行」「`A部` 一行、`H…` 下一行」的版式，
        ///     也让「一个 項目 下面跟两三个 図番」的写法都能收；
        ///   · 上面几行里也没有 項目 的 図番 不收（表題欄 / 変更履歴里的裸 図番）。
        ///
        /// 項目 **保留到「部」**（输出 <c>A部</c> / <c>B部</c>，不剥成 <c>A</c>）、要領図漢字 留空。
        /// 整行先过 <see cref="NormalizeFreeText"/>（Unicode FormKC）：全角字母 / 数字 / 冒号 / 逗号化成半角。
        /// 这张組立图自己的 図番（<paramref name="drawingNumber"/>）不收，同一「項目 + 図番」只收一条
        /// （同一个 図番 挂在两个 項目 上时两条都留 —— 图纸上确实会这样写）。
        ///
        /// 收得比页面上写着的少时，日志里会打出「页面几行、`～部` 几处、10 位図番 几处、配上几条、
        /// 重复合并几条、几条没配上 項目」，并把没配上的那几条原文（截断）打出来 ——
        /// 一眼能看出是文字层缺字 / 写法不认识 / 还是真的重复，不用靠猜。
        ///
        /// 没有 PDF（<paramref name="pdfPath"/> 为 null）/ 页面尺寸无效 / 无文字层时返回空列表。
        /// </summary>
        private static List<string[]> ReadFreeTextIndex(string pdfPath, int pageIndex,
                                                        int renderWidth, int renderHeight,
                                                        string drawingNumber, DetectorOptions o)
        {
            var found = new List<string[]>();

            if (string.IsNullOrWhiteSpace(pdfPath) || renderWidth <= 0 || renderHeight <= 0)
                return found;

            List<PartTextLine> lines = PartTextExtractor.ReadTextLines(
                pdfPath, pageIndex, renderWidth, renderHeight, line => o.Log(line));

            if (lines.Count == 0)
                return found;

            // ① 先把整页的「項目」与「図番」都找出来，记下行号 + 行内位置（后面按位置配对）。
            //    换行压平成一行、去掉首尾空白；全角字母数字先化成半角（Ａ部 / １００１１ 也认）。
            //    词与词之间的空格保留（拼行时补的空格是断词线索，不能像 CsvFile.Normalize 那样全删）。
            var items = new List<FreeMark>();
            var numbers = new List<FreeMark>();
            var texts = new List<string>();
            int selfCount = 0;

            for (int i = 0; i < lines.Count; i++)
            {
                string text = NormalizeFreeText(CsvFile.SingleLine(lines[i].Text ?? string.Empty)).Trim();
                texts.Add(text);

                if (text.Length == 0)
                    continue;

                foreach (Match m in FreeTextItemPattern.Matches(text))
                {
                    // 去掉拼行时补的空格：`A ,B 部` → `A,B部`（与表格路径的单元格归一化同一套）
                    string item = CsvFile.Normalize(m.Groups["item"].Value);

                    if (item.Length == 0)
                        continue;

                    items.Add(new FreeMark
                    {
                        Line = i,
                        Index = m.Groups["item"].Index,
                        Text = item
                    });
                }

                foreach (Match m in FreeTextNumberPattern.Matches(text))
                {
                    string number = CanonicalNumber(m.Groups["number"].Value);

                    if (number == null)
                        continue;   // 分隔符去掉后不是 10 位（例如 9 位）：不猜

                    if (IsSelfNumber(number, drawingNumber))
                    {
                        selfCount++;   // 这张组立图自己的 図番（表題欄），不是要領図
                        continue;
                    }

                    numbers.Add(new FreeMark
                    {
                        Line = i,
                        Index = m.Groups["number"].Index,
                        Text = number
                    });
                }
            }

            // ② 按位置配对：同一行取 図番 前面最近的 項目；同一行没有就往上找（≤ MaxPendingRowSpan 行）
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var orphans = new List<FreeMark>();
            int duplicates = 0;

            foreach (FreeMark number in numbers)
            {
                string item = FindFreeItem(items, number);

                if (item == null)
                {
                    orphans.Add(number);   // 上面几行都没有 項目：表題欄之类的裸 図番，不收
                    continue;
                }

                if (!seen.Add(item + "\u0001" + number.Text))
                {
                    duplicates++;   // 同一「項目 + 図番」在这张图上写了两遍：合并成一条
                    continue;
                }

                found.Add(new[] { item, string.Empty, number.Text });
            }

            o.Log("[INFO] 01_アッセンブリ図_要領図：页面文字行 " + lines.Count + " 行"
                  + "（「～部」标记 " + items.Count + " 处 / 10 位図番 " + numbers.Count + " 处"
                  + (selfCount > 0 ? "，另有 " + selfCount + " 处是这张图自己的 図番" : string.Empty)
                  + "）→ 配上 " + found.Count + " 条"
                  + (duplicates > 0 ? "（同一「項目+図番」重复 " + duplicates + " 条已合并）" : string.Empty)
                  + (orphans.Count > 0
                        ? "，另有 " + orphans.Count + " 处図番 上面 " + MaxPendingRowSpan + " 行内找不到「～部」已跳过"
                        : string.Empty)
                  + "。");

            // 有 図番 没配上 項目 时，把前几条的原文打出来：多半是写法不同（図番 9 位 / 項目 不叫「～部」）
            // 或那几处的文字层缺字 —— 看到原文就知道该不该改规则，不用猜。
            for (int i = 0; i < orphans.Count && i < MaxOrphanSamples; i++)
            {
                FreeMark orphan = orphans[i];

                o.Log("[INFO] 01_アッセンブリ図_要領図：没配上 項目 的図番「" + orphan.Text
                      + "」在页面第 " + (orphan.Line + 1) + " 行，原文「"
                      + Clip(texts[orphan.Line], OrphanSampleLength) + "」。");
            }

            if (found.Count > 0)
            {
                o.Log("[INFO] 01_アッセンブリ図_要領図：页面文字行里找到「A部詳細：H…」这种要領図索引 "
                      + found.Count + " 条（項目 保留到「部」，如 " + found[0][0] + "）。");
            }

            return found;
        }

        /// <summary>
        /// 给一个 図番 找它的 項目：优先「同一行、位置在 図番 前面、离它最近」的那个（<c>A部詳細：H…</c>）；
        /// 同一行没有就往上找最近的 項目（最多 <see cref="MaxPendingRowSpan"/> 行 ——
        /// 对应 <c>A部詳細：</c> 与 図番 折成两行，以及一个 項目 下面跟两三个 図番的写法）。
        /// 上面几行也没有返回 null（裸 図番，不收）。
        /// </summary>
        private static string FindFreeItem(List<FreeMark> items, FreeMark number)
        {
            FreeMark sameLine = null;

            foreach (FreeMark item in items)
            {
                if (item.Line != number.Line || item.Index >= number.Index)
                    continue;

                if (sameLine == null || item.Index > sameLine.Index)
                    sameLine = item;
            }

            if (sameLine != null)
                return sameLine.Text;

            FreeMark above = null;

            foreach (FreeMark item in items)
            {
                if (item.Line >= number.Line || number.Line - item.Line > MaxPendingRowSpan)
                    continue;

                if (above == null || item.Line > above.Line
                    || (item.Line == above.Line && item.Index > above.Index))
                    above = item;
            }

            return above == null ? null : above.Text;
        }

        /// <summary>
        /// 把自由文字里抠出来的 図番 规范成 10 位：去掉中间的空格 / 连字符
        /// （<c>H325-U10011</c> / <c>H325 U10011</c> → <c>H325U10011</c>）。
        /// 规范完不是整格 10 位（<see cref="IsWholeNumber"/> 过不去）返回 null。
        /// </summary>
        private static string CanonicalNumber(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return null;

            var sb = new StringBuilder(raw.Length);

            foreach (char ch in raw)
            {
                if ((ch >= '0' && ch <= '9') || (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'))
                    sb.Append(ch);
            }

            string number = sb.ToString();

            return IsWholeNumber(number) ? number : null;
        }

        /// <summary>
        /// 自由文字整行规范化：Unicode FormKC（全角字母数字 <c>Ａ</c> / <c>１</c> 化成 <c>A</c> / <c>1</c>，
        /// 全角 <c>，</c> / <c>：</c> / <c>～</c> 化成半角），这样正则只写半角形式就够，
        /// 图纸上混着全角的写法也认。规范化失败（非法代理对）就用原文。
        /// </summary>
        private static string NormalizeFreeText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            try
            {
                return text.Normalize(NormalizationForm.FormKC);
            }
            catch (ArgumentException)
            {
                return text;
            }
        }

        /// <summary>日志里截一段原文（太长时末尾加「…」，免得一行日志刷屏）。</summary>
        private static string Clip(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max)
                return text ?? string.Empty;

            return text.Substring(0, max) + "…";
        }

        /// <summary>自由文字里的一个标记（項目 或 図番）：在第几行（0 起）、行内第几个字符、文字。</summary>
        private sealed class FreeMark
        {
            public int Line;
            public int Index;
            public string Text;
        }

        /// <summary>
        /// 把页面自由文字里的条目并进输出：**表格 / 内联写法已经收过的 図番 不再收**
        /// （表格里的 項目 更可靠）。
        ///
        /// 判断只拿「表格路径已经收下的 図番」比对，**不把这次补进来的 図番 记进比对集** ——
        /// 图纸上同一个 `図番` 挂在两个 `項目` 上是正常写法（H527U10001 的 `A部` 与 `B部`
        /// 都指向 `H325U10011`），那两条都要留。
        /// </summary>
        /// <returns>真正补进去的条数。</returns>
        private static int MergeFreeIndex(List<string[]> freeIndex, List<string[]> output)
        {
            if (freeIndex == null || freeIndex.Count == 0)
                return 0;

            var tableNumbers = new HashSet<string>(StringComparer.Ordinal);

            foreach (string[] row in output)
            {
                if (row != null && row.Length >= 3 && !string.IsNullOrEmpty(row[2]))
                    tableNumbers.Add(row[2]);
            }

            int added = 0;

            foreach (string[] row in freeIndex)
            {
                if (tableNumbers.Contains(row[2]))
                    continue;

                output.Add(row);
                added++;
            }

            return added;
        }

        /// <summary>这个 図番 是不是这张组立图自己的（图題欄里写着它，不是要領図）。</summary>
        private static bool IsSelfNumber(string number, string drawingNumber)
        {
            return !string.IsNullOrEmpty(drawingNumber)
                   && string.Equals(number, drawingNumber, StringComparison.OrdinalIgnoreCase);
        }

        // ── 清理 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 这张图不是アッセンブリ図时，把上一次运行留下的同名产物清掉
        /// （上次可能判成アッセンブリ図，留着会让下游按固定名字取到过期内容）。
        /// </summary>
        private static void DeleteStaleOutput(string outputDirectory, DetectorOptions o)
        {
            RemoveIfExists(Path.Combine(outputDirectory, OutputFileName), o);
        }

        /// <summary>
        /// 删一个文件；不存在就什么也不做。删不掉（被 Excel 占用 / 无权限）只记 WARN，不影响本次输出。
        /// </summary>
        private static void RemoveIfExists(string path, DetectorOptions o)
        {
            if (!File.Exists(path))
                return;

            try
            {
                File.Delete(path);
                o.Log("[INFO] 01_アッセンブリ図_要領図：已删除 " + path);
            }
            catch (IOException)
            {
                o.Log("[WARN] 01_アッセンブリ図_要領図：删不掉 " + path + "（可能正被 Excel 打开）。");
            }
            catch (UnauthorizedAccessException)
            {
                o.Log("[WARN] 01_アッセンブリ図_要領図：删不掉 " + path + "（无权限）。");
            }
        }
    }
}
