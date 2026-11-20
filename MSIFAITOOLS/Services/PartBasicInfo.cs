using System;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// 从 PDF 表格（plu_table.txt）中提取的一条部品基本信息。
    /// 字段与 SQLite 的「部品基本信息」表一一对应，一处改动需同步另一处。
    /// </summary>
    internal sealed class PartBasicInfo
    {
        /// <summary>
        /// 「部材名」的实际存储值（见 <see cref="MemberName"/>：<b>只有分析流程赋值时</b>才按置换表置换；
        /// 画面输入 / 从库读取走 <see cref="SetMemberNameFromInput"/> / <see cref="SetMemberNameFromStore"/>，原样保留）。
        /// </summary>
        private string _memberName = string.Empty;

        public PartBasicInfo(string drawingNo, int page, int number, string partName, string partDrawingNo,
            string frameKind, string quantity, string basicSize, string cutSize, string partCode)
        {
            DrawingNo = Clean(drawingNo);
            Page = page < 0 ? 0 : page;
            Number = number;
            PartName = Clean(partName);
            MemberName = string.Empty;
            MemberNameAiSuggestion = string.Empty;
            PartDrawingNo = Clean(partDrawingNo);
            FrameKind = Clean(frameKind);
            Quantity = Clean(quantity);
            BasicSize = Clean(basicSize);
            CutSize = Clean(cutSize);
            PartCode = Clean(partCode);
            HeightMin = string.Empty;
            HeightMax = string.Empty;
            WidthMin = string.Empty;
            WidthMax = string.Empty;
            UsageKind = string.Empty;
            SpecialNote = string.Empty;
            VerifyStatus = PartVerifyStatus.NotChecked;
            Remark = string.Empty;
            SourceKind = string.Empty;
            SourceTag = string.Empty;
            SourceFile = string.Empty;
            PdfFile = string.Empty;
            LoginTime = string.Empty;
        }

        /// <summary>図番（PDF 文件名去掉扩展名）。</summary>
        public string DrawingNo { get; private set; }

        /// <summary>表格所在页码（从 1 开始；无法判定时为 0）。</summary>
        public int Page { get; private set; }

        /// <summary>番号。</summary>
        public int Number { get; private set; }

        /// <summary>部品名。</summary>
        public string PartName { get; private set; }

        /// <summary>
        /// 部材名：用「番号」去该页目录的 <c>parts_info.json</c> 找圈号（<c>"text": "4"</c> 且
        /// <c>"display": "④4"</c> 的 <c>callouts</c>），取这些圈号所在零件图（<c>index</c>）的全部
        /// <c>texts</c>，<b>去掉含数字的文字</b>、<b>去掉「XX部」形式的位置标记</b>，
        /// 剩下的去重后用 <c>|</c> 连接（例如 <c>縦枠</c>、<c>鴨居|上レール</c>）。
        /// 由 <see cref="PartsInfoIndex.ResolveCalloutMemberName(int)"/> 计算；
        /// 番号对不上圈号 / 没有 <c>parts_info.json</c> 的行留空。
        /// <para>
        /// <b>位置前缀</b>：与部品名同一套 —— 按位置拆条时（<see cref="CmdCalloutPartSplitter"/>、
        /// <see cref="CmdInstructionPartCollector"/>），每条取<b>该条对应零件图</b>的部材名再加
        /// <c>右 / 左</c>（或 <c>上 / 下</c>）：<c>縦枠</c> → <c>右縦枠</c> / <c>左縦枠</c>
        /// （<see cref="PartsInfoIndex.PartMarkSplitPlan.PrefixedMemberNameAt"/>）。
        /// 部材名为空、或已是 <see cref="MemberNameFallback"/>（「その他」）时<b>不加前缀</b>。
        /// </para>
        /// <b>留空的行写库时会统一填成 <see cref="MemberNameFallback"/>＝「その他」</b>
        /// （<see cref="PartBasicInfoRepository.BindInsertParameters"/>），所以画面上不会出现空的「部材名」。
        /// <para>
        /// <b>部材名置换</b>：setter 会把写入的值交给 <see cref="MemberNameReplacementCatalog.Replace"/>，
        /// 按维护画面（<see cref="MemberNameReplacementForm"/>）配好的「置换前 → 置换后」表替换
        /// （例：<c>鴨居</c> → <c>上枠</c>、<c>右鴨居</c> → <c>右上枠</c>；「その他」不置换）。
        /// 置换表里的 <c>GenpinName</c> = <b>原部材名</b>（genpin 主档的叫法）、
        /// <c>MsifDevName</c> = <b>表示部材名</b>（图面上的写法）—— 置换结果只在 genpin 侧使用，
        /// 本工具的画面上显示 / 保存的是表示部材名（见 <see cref="MemberNamePickerForm"/>）。
        /// 置换表为空 / 数据库打不开时原样保留，所以不配置时行为与以前完全一样。
        /// </para>
        /// <para>
        /// <b>只给「自动取得部材名的时点」用</b>：第 1 / 2 / 3 步分析
        /// （<c>CmdPartAnalysisService</c> / <c>CmdCalloutPartSplitter</c> / <c>CmdInstructionPartCollector</c>）。
        /// <b>画面【保存】不置换</b>：用户在「部材名」单元格里填 / 选的词原样登录，
        /// 走 <see cref="SetMemberNameFromInput"/>（否则置换表里配了 <c>上ﾚｰﾙ → アルミ上</c> 时，
        /// 画面输入的 <c>上ﾚｰﾙ</c> 一点【保存】就被悄悄改成 <c>アルミ上</c>）。
        /// </para>
        /// </summary>
        public string MemberName
        {
            get { return _memberName; }
            set { _memberName = MemberNameReplacementCatalog.Replace(value); }
        }

        /// <summary>
        /// 从数据库（部品基本信息表）读出来的部材名<b>原样</b>写入，<b>不再</b>走
        /// <see cref="MemberNameReplacementCatalog.Replace"/>。
        /// <para>
        /// <b>为什么</b>：置换只在「取得部材名的那个点」做一次 —— 第 1 步按圈号取
        /// （<c>CmdPartAnalysisService.ApplyMemberNames</c>）、第 2 / 3 步按位置拆条
        /// （<c>CmdCalloutPartSplitter.Clone</c> / <c>CmdInstructionPartCollector</c>）。
        /// 「选择部材名」窗口的候选<b>不再</b>置换（显示的是表示部材名 <c>MsifDevName</c> /
        /// 部材 mapping 的 <c>BuzaiName</c>）。
        /// 库里存的已经是置换后的值；读取时再换一次的话，
        /// 置换表里存在链式规则（<c>A → B</c> 且 <c>B → C</c>）时会连换两次（<c>A → C</c>），
        /// 第 2 / 3 步「读出来 → 改 → 整张表写回」（<c>PartBasicInfoRepository.Query</c> →
        /// <c>Save</c>）更会把置换结果一层层叠加。
        /// </para>
        /// <para>
        /// 由 <see cref="PartBasicInfoRepository.Query"/> 调用；分析流程给部材名赋值用
        /// <see cref="MemberName"/>（会置换），画面输入用 <see cref="SetMemberNameFromInput"/>。
        /// </para>
        /// </summary>
        /// <param name="memberName">数据库里的部材名（<c>null</c> 视为空）。</param>
        internal void SetMemberNameFromStore(string memberName)
        {
            _memberName = memberName ?? string.Empty;
        }

        /// <summary>
        /// 画面（部品基本信息表的「部材名」单元格 / 「选择部材名」窗口）里<b>填 / 选</b>的值
        /// <b>原样</b>写入，<b>不</b>走 <see cref="MemberNameReplacementCatalog.Replace"/>。
        /// <para>
        /// <b>为什么</b>：置换表（<c>genpin.dbo.T_M_MergeName</c>）是给「从图纸 / <c>parts_info.json</c>
        /// 自动取得的词」用的（见 <see cref="MemberName"/>）。用户在画面上写的是什么就该登录什么 ——
        /// 例如表里配了 <c>上ﾚｰﾙ → アルミ上</c>，用户在「部材名」里填 <c>上ﾚｰﾙ</c> 点【保存】时
        /// <b>不应该</b>被换成 <c>アルミ上</c>。
        /// </para>
        /// <para>
        /// 由 <c>PartBasicInfoForm</c> 的保存处理调用（其余不置换的路径见
        /// <see cref="SetMemberNameFromStore"/>）。
        /// </para>
        /// </summary>
        /// <param name="memberName">画面上输入 / 选择的部材名（<c>null</c> 视为空；首尾空白由写库时去掉）。</param>
        internal void SetMemberNameFromInput(string memberName)
        {
            _memberName = memberName ?? string.Empty;
        }

        /// <summary>
        /// 「部材名AI推荐」：<b>历史残留</b>。
        /// <para>
        /// 以前【AI 分析】会把为「数据来源 = CMD分析・アッセンブリ図」的行推荐的部材名写进这一列
        /// （只推荐、不覆盖「部材名」）。该机能已删除：部品基本信息画面不再表示这一列，
        /// 也不再调用 AI 判部材名。属性 / 数据库列保留只为兼容老数据库与老数据，
        /// 保存时原值透传（见 <c>PartBasicInfoForm</c> 的读取 / 保存逻辑），不参与任何判定。
        /// </para>
        /// <para>数据库列名 = <see cref="PartBasicInfoRepository.MemberNameAiColumnName"/>（「部材名AI推荐」）。</para>
        /// </summary>
        public string MemberNameAiSuggestion { get; set; }

        /// <summary>
        /// 「部材名」为空时使用的默认值「その他」。
        /// 写库时由 <see cref="PartBasicInfoRepository.BindInsertParameters"/> 统一套用
        /// （老数据库打开时 <see cref="PartBasicInfoRepository"/> 也会把空值补成这个值），
        /// 所以画面上看到的「部材名」不会再有空白格。
        /// </summary>
        public const string MemberNameFallback = "その他";

        /// <summary>部品図番（表格没有该列时为空）。</summary>
        public string PartDrawingNo { get; private set; }

        /// <summary>区分（表格的「枠区分」列；数据库列名 = 区分）。</summary>
        public string FrameKind { get; private set; }

        /// <summary>数量（表格的「員数」列；数据库列名 = 数量。可能是数字或字母，故按文本保存）。</summary>
        public string Quantity { get; private set; }

        /// <summary>基本寸法。</summary>
        public string BasicSize { get; private set; }

        /// <summary>切断寸法。</summary>
        public string CutSize { get; private set; }

        /// <summary>部品コード。</summary>
        public string PartCode { get; private set; }

        /// <summary>H下限（特寸時使い分け表的 H 下限；没有这项数据时为空）。数据库列名 = H下限。</summary>
        public string HeightMin { get; set; }

        /// <summary>H上限（特寸時使い分け表的 H 上限；没有这项数据时为空）。数据库列名 = H上限。</summary>
        public string HeightMax { get; set; }

        /// <summary>W下限（特寸時使い分け表的 W 下限；没有这项数据时为空）。数据库列名 = W下限。</summary>
        public string WidthMin { get; set; }

        /// <summary>W上限（特寸時使い分け表的 W 上限；没有这项数据时为空）。数据库列名 = W上限。</summary>
        public string WidthMax { get; set; }

        /// <summary>
        /// 使い分け（同一部品按品種 / 尺寸分用哪一个时的区分）。数据库列名 = 使い分け。
        /// 由【CMD 分析】第 6 步「使い分け自動設定」按拾い出し表（<c>hiroidashiData\*.xlsx</c> 第 12 列）
        /// 自动填（<see cref="CmdUsageKindCollector"/>）：同一図番内「<b>部品名</b> + 区分 / 参照元 /
        /// 部品コード / H・W 上下限」全同的 2 行以上才算对象（按位置拆开的 右… / 左… 部品名不同，
        /// 不算同一部品、不成组），已有值的行不覆盖，所以画面上仍可手工改。本来没有数据源时留空。
        /// </summary>
        public string UsageKind { get; set; }

        /// <summary>
        /// 特注（追加対応特注寸法 等）。【CMD 分析】第 4 步把 特注品.json 的「追加対応特注寸法」写在这里，
        /// 其余来源留空、可在画面上手工填。<b>図番 第 5 位是 Z 的 PDF</b> 读到的行由第 1 / 2 / 3 步
        /// 写成 <see cref="SpecialProductMark"/>（「特注品」，见 <see cref="CmdSpecialProductRule"/>）。
        /// 数据库列名 = 特注。
        /// </summary>
        public string SpecialNote { get; set; }

        /// <summary>「特注」等于 1 的标记（特注品.json 的追加対応部品；<see cref="CmdTzPartCollector.SpecialNoteMark"/> 同值）。</summary>
        public const string SpecialNoteMark = "1";

        /// <summary>
        /// 「特注」= 特注品的标记：<b>図番（PDF 文件名）第 5 位是 Z</b> 的 PDF 读到的行写这个值
        /// （见 <see cref="CmdSpecialProductRule"/>）。与 <see cref="SpecialNoteMark"/> 一样，
        /// 表示「数量 / 切断寸法 要保留」。
        /// </summary>
        public const string SpecialProductMark = "特注品";

        /// <summary>
        /// 「特注」是否表示特注：等于 <see cref="SpecialNoteMark"/>（1）或
        /// <see cref="SpecialProductMark"/>（特注品），忽略前后空白。
        /// </summary>
        public bool IsSpecialNote()
        {
            string value = (SpecialNote ?? string.Empty).Trim();

            return string.Equals(value, SpecialNoteMark, StringComparison.Ordinal) ||
                string.Equals(value, SpecialProductMark, StringComparison.Ordinal);
        }

        /// <summary>
        /// 【CMD 分析】写库前统一套用的规则：<b>「特注」既不是 1 也不是「特注品」的行，
        /// 「数量」「切断寸法」一律清空</b>
        /// （这两列只在特注的行上有意义；其它行即使表里读到了值也不写库）。
        /// 「特注品」= 図番 第 5 位是 Z 的 PDF 读到的行（见 <see cref="CmdSpecialProductRule"/>）。
        /// 返回本次是否真的清掉了值（调用方用于日志统计）。
        /// </summary>
        public bool ClearQuantityUnlessSpecial()
        {
            if (IsSpecialNote())
            {
                return false;
            }

            bool cleared = Quantity.Length > 0 || CutSize.Length > 0;
            Quantity = string.Empty;
            CutSize = string.Empty;

            return cleared;
        }

        /// <summary>
        /// 【CMD 分析】第 3 步专用：把「区分」改成指定值
        /// （要領図行与「アッセンブリ図」行部品名相同时，用装配图行的区分覆盖，见
        /// <see cref="CmdPartAnalysisService"/> 的同名合并规则）。
        /// </summary>
        public void OverrideFrameKind(string frameKind)
        {
            FrameKind = Clean(frameKind);
        }

        /// <summary>
        /// 【CMD 分析】第 3 步专用：把「番号」改成指定值
        /// （要領図行与「アッセンブリ図」行部品名相同时，被删掉的装配图行的<b>番号</b>由要領図行接手，
        /// 见 <see cref="CmdPartAnalysisService"/> 的同名合并规则 ——
        /// 这样画面上仍然能看到装配图的番号，「页码 + 番号 + 部品コード」的唯一键也照着装配图走）。
        /// </summary>
        /// <param name="number">要保留的番号；<c>≤ 0</c>（没有有效番号）时忽略，保持原值。</param>
        public void OverrideNumber(int number)
        {
            if (number > 0)
            {
                Number = number;
            }
        }

        /// <summary>坐标校验状态（取值见 <see cref="PartVerifyStatus"/>）。</summary>
        public string VerifyStatus { get; set; }

        /// <summary>备注：坐标校验明细、AI 提示等。</summary>
        public string Remark { get; set; }

        /// <summary>数据来源（取值见 <see cref="PartSourceKind"/>）：AI 分析 / CMD 分析・アッセンブリ図 / CMD 分析・要領図 / 手工追加。</summary>
        public string SourceKind { get; set; }

        /// <summary>
        /// 参照元（数据库列名 = 参照元）。<b>写库时一律写成该行的「図番」</b>
        /// （<see cref="PartBasicInfoRepository.BindInsertParameters"/>；
        /// 老数据库打开时 <see cref="PartBasicInfoRepository"/> 也会把旧值改成「図番」），
        /// 所以数据库 / 画面上的「参照元」永远等于「図番」。
        /// 这个属性在内存里保存的是来源简称（取值见 <see cref="PartSourceTag"/>：要領図 /
        /// アッセンブリ図 / 特注品.json（旧简称 tz.json 也认）/ AI分析 / 手工追加），只作为「数据来源」为空时的兜底判定用。
        /// </summary>
        public string SourceTag { get; set; }

        /// <summary>来源文件（plu_table.txt 的完整路径）。</summary>
        public string SourceFile { get; set; }

        /// <summary>来源 PDF 的完整路径。</summary>
        public string PdfFile { get; set; }

        /// <summary>登录时间（从数据库读出时填充；新提取的对象为空）。</summary>
        public string LoginTime { get; set; }

        /// <summary>用于日志/界面的简短描述。</summary>
        public string ToShortText()
        {
            return string.Format("#{0} {1}（{2}）", Number, PartName, PartCode);
        }

        private static string Clean(string value)
        {
            return value == null ? string.Empty : value.Trim();
        }
    }

    /// <summary>部品基本信息的坐标校验状态（同时作为写入数据库的文字）。</summary>
    internal static class PartVerifyStatus
    {
        /// <summary>部品名 / 部品コード 在 xy.txt 的对应页中全部找到。</summary>
        public const string Passed = "坐标一致";

        /// <summary>有部品名 / 部品コード 未能在 xy.txt 中找到。</summary>
        public const string Failed = "坐标不符";

        /// <summary>没有 xy.txt（或无法解析），未做校验。</summary>
        public const string NotChecked = "未校验";

        /// <summary>全部状态（画面下拉框的候选项）。</summary>
        public static readonly string[] All = new string[] { Passed, Failed, NotChecked };
    }

    /// <summary>
    /// 部品基本信息的「数据来源」（同时作为写入数据库的文字）。
    /// 用于区分同一张装配图下的行是哪个功能登录进来的：
    /// 【AI 分析】读 plu_table.txt；【CMD 分析】阶段 A 读 01_アッセンブリ図_部品機能情報.csv，
    /// 阶段 B 读 02_要領図_部品機能情報.csv；画面上还能手工追加行。
    /// </summary>
    internal static class PartSourceKind
    {
        /// <summary>【AI 分析】：用 AI 从 plu_table.txt 提取（<c>AiPartAnalyzer</c>）。</summary>
        public const string AiAnalysis = "AI分析";

        /// <summary>【CMD 分析】阶段 A：读 01_アッセンブリ図_部品機能情報.csv（<c>CmdPartAnalyzer</c>）。</summary>
        public const string CmdAssembly = "CMD分析・アッセンブリ図";

        /// <summary>【CMD 分析】阶段 B：读 02_要領図_部品機能情報.csv，挂在装配图名下（<c>CmdInstructionPartCollector</c>）。</summary>
        public const string CmdYoryo = "CMD分析・要領図";

        /// <summary>
        /// 【CMD 分析】第 4 步：读最前面那步生成的 <c>AiAppData\&lt;文件名&gt;\特注品.csv</c>
        /// （Excel 部品表；没有 CSV 时退回 <c>classify\特注品.json</c>），挂在装配图名下（<c>CmdTzPartCollector</c>）。
        /// <para>
        /// 值与文件名同步（<c>classify\特注品.json</c>，旧名 <c>tz.json</c>）。升级前写进库的行是旧值
        /// <see cref="LegacyCmdTz"/>，第 4 步重跑时由 <c>CmdPartAnalysisService.IsTzRow</c> 一起认出来，
        /// 照样剔除 / 覆盖，不会留下重复行。
        /// </para>
        /// </summary>
        public const string CmdTz = "CMD分析・特注品.json";

        /// <summary>
        /// 第 4 步「数据来源」的<b>旧值</b>「<c>CMD分析・tz.json</c>」：只用于识别升级前写进库的行
        /// （<c>CmdPartAnalysisService.IsTzRow</c>），新写入的行一律用 <see cref="CmdTz"/>。
        /// </summary>
        public const string LegacyCmdTz = "CMD分析・tz.json";

        /// <summary>部品基本信息画面上手工追加的行。</summary>
        public const string Manual = "手工追加";

        /// <summary>全部取值（说明 / 下拉用）。</summary>
        public static readonly string[] All =
            new string[] { AiAnalysis, CmdAssembly, CmdYoryo, CmdTz, Manual };
    }

    /// <summary>
    /// 部品基本信息的「来源标识」：<see cref="PartSourceKind"/> 的简称（同时作为写入数据库的文字）。
    /// 用于在画面上一眼区分这一行的部品来自「要領図」还是「アッセンブリ図」自身的部品表。
    /// <b>注意</b>：数据库的「参照元」列现在写的是该行的「図番」
    /// （见 <see cref="PartBasicInfo.SourceTag"/>），这些简称只留在内存对象上，
    /// 以及老数据「数据来源」为空时的兜底判定里。
    /// </summary>
    internal static class PartSourceTag
    {
        /// <summary>【AI 分析】（<see cref="PartSourceKind.AiAnalysis"/> 的简称）。</summary>
        public const string AiAnalysis = "AI分析";

        /// <summary>【CMD 分析】阶段 A：装配图自身的部品表（<see cref="PartSourceKind.CmdAssembly"/> 的简称）。</summary>
        public const string CmdAssembly = "アッセンブリ図";

        /// <summary>【CMD 分析】阶段 B：要領図 的部品（<see cref="PartSourceKind.CmdYoryo"/> 的简称）。</summary>
        public const string CmdYoryo = "要領図";

        /// <summary>【CMD 分析】第 4 步：Excel 部品表的部品（<see cref="PartSourceKind.CmdTz"/> 的简称）。</summary>
        public const string CmdTz = "特注品.json";

        /// <summary>
        /// 第 4 步「参照元」的<b>旧简称</b>「<c>tz.json</c>」：只用于识别升级前的老行
        /// （「数据来源」为空、「参照元」= 旧简称），<c>CmdPartAnalysisService.IsTzRow</c> 一起认。
        /// </summary>
        public const string LegacyCmdTz = "tz.json";

        /// <summary>部品基本信息画面上手工追加的行（<see cref="PartSourceKind.Manual"/> 的简称）。</summary>
        public const string Manual = "手工追加";

        /// <summary>全部取值（说明 / 下拉用）。</summary>
        public static readonly string[] All =
            new string[] { AiAnalysis, CmdAssembly, CmdYoryo, CmdTz, Manual };

        /// <summary>把「数据来源」（<see cref="PartSourceKind"/>）换成对应的「来源标识」；不认识的返回空。</summary>
        public static string FromSourceKind(string sourceKind)
        {
            if (sourceKind == PartSourceKind.CmdYoryo)
            {
                return CmdYoryo;
            }

            if (sourceKind == PartSourceKind.CmdAssembly)
            {
                return CmdAssembly;
            }

            if (sourceKind == PartSourceKind.AiAnalysis)
            {
                return AiAnalysis;
            }

            if (sourceKind == PartSourceKind.Manual)
            {
                return Manual;
            }

            if (sourceKind == PartSourceKind.CmdTz)
            {
                return CmdTz;
            }

            return string.Empty;
        }
    }
}
