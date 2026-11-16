using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// 第 6 步的一条「使い分け」明细 —— 只保留用到的两列：部品コード（形材番号）与 使い分け。
    ///
    /// <para>
    /// 数据源是 SQL Server 的 <c>[genpin].[dbo].[T_M_PartsMaster]</c>
    /// （<c>ShapeNumber</c> → <c>BranchNumber</c>）；
    /// 连不上数据库时退回读旧的 <c>hiroidashiData\*.xlsx</c>
    /// （第 4 列「部品ｺｰﾄﾞ/形材番号」/ 第 12 列「使い分け」）。
    /// 两个来源都装进同一个索引，第 6 步后面的处理逻辑完全一样。
    /// </para>
    /// </summary>
    internal sealed class CmdUsageKindEntry
    {
        public CmdUsageKindEntry(string partCode, string usageKind, string fileName, int rowNumber)
        {
            PartCode = partCode ?? string.Empty;
            UsageKind = usageKind ?? string.Empty;
            FileName = fileName ?? string.Empty;
            RowNumber = rowNumber;
        }

        /// <summary>部品コード / 形材番号（数据库 = <c>ShapeNumber</c>、xlsx = 第 4 列）。</summary>
        public string PartCode { get; private set; }

        /// <summary>使い分け（数据库 = <c>BranchNumber</c>、xlsx = 第 12 列）。</summary>
        public string UsageKind { get; private set; }

        /// <summary>来源（数据库表名 / xlsx 文件名）。</summary>
        public string FileName { get; private set; }

        /// <summary>来源里的行序号（从 1 开始；数据库按查询结果顺序，xlsx 是表里的行号）。</summary>
        public int RowNumber { get; private set; }
    }

    /// <summary>
    /// 使い分け索引：<c>部品コード（形材番号） → 使い分け（按表里的出现顺序）</c>。
    /// 同一部品コード在多个品種 / 多个来源里出现时，值全部按「来源 → 行序号」顺序收集，
    /// 第 6 步按这个顺序依次填到部品基本信息表的多行上。
    /// </summary>
    internal sealed class CmdUsageKindIndex
    {
        private readonly List<CmdUsageKindEntry> _entries = new List<CmdUsageKindEntry>();

        /// <summary>全部明细（顺序 = 来源顺序 → 行序号顺序）。</summary>
        public IList<CmdUsageKindEntry> Entries
        {
            get { return _entries; }
        }

        /// <summary>
        /// 本次实际用的数据源说明（日志里显示给用户看），例：
        /// <c>connJP1 = SQL Server 10.103.126.208,1433 / DB genpin / 表 T_M_PartsMaster</c>。
        /// 没读到（连不上 / 找不到文件夹）时为空串。
        /// </summary>
        public string SourceDescription { get; set; }

        /// <summary>读到的 xlsx 文件数（只有走旧的 hiroidashiData 后备时才大于 0）。</summary>
        public int FileCount { get; set; }

        /// <summary>明细条数（部品コード为空的行不计）。</summary>
        public int EntryCount
        {
            get { return _entries.Count; }
        }

        public void Add(CmdUsageKindEntry entry)
        {
            if (entry != null && !string.IsNullOrWhiteSpace(entry.PartCode))
            {
                _entries.Add(entry);
            }
        }

        /// <summary>
        /// 取该部品コード对应的明细（按出现顺序，<b>跳过 使い分け 为空的行</b>）。
        /// 每条明细都带着它自己记录的部品コード / 形材番号（<see cref="CmdUsageKindEntry.PartCode"/>）、
        /// 来源（表名 / 文件名）与行序号 —— 第 6 步填值前要用它<b>逐行核对</b>该行的部品コード。
        /// <para>
        /// 匹配时把 <c>□ / ■ / △ / ▲ / ○ / ●</c> 等符号当通配符（任一侧含符号即可）：
        /// <c>T_M_PartsMaster.[ShapeNumber]</c> 与部品基本信息表的「部品コード」哪个带符号都能命中，
        /// 一个符号代表 <b>1～2 个字符</b>（<see cref="CmdUsageKindCollector.PartCodeMatches"/>）。
        /// </para>
        /// </summary>
        /// <param name="partCode">部品基本信息表的「部品コード」。</param>
        public List<CmdUsageKindEntry> FindEntries(string partCode)
        {
            List<CmdUsageKindEntry> matched = new List<CmdUsageKindEntry>();

            if (string.IsNullOrWhiteSpace(partCode))
            {
                return matched;
            }

            string key = partCode.Trim();

            foreach (CmdUsageKindEntry entry in _entries)
            {
                if (!CmdUsageKindCollector.PartCodeMatches(key, entry.PartCode))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(entry.UsageKind))
                {
                    continue;
                }

                matched.Add(entry);
            }

            return matched;
        }

        /// <summary>
        /// 取该部品コード对应的「使い分け」值（按出现顺序，跳过空值）。
        /// 只需要值的调用方用这个；要同时核对「这条值来自哪个部品コード」时用
        /// <see cref="FindEntries"/>（第 6 步就是这么做的）。
        /// </summary>
        /// <param name="partCode">部品基本信息表的「部品コード」。</param>
        public List<string> FindUsageKinds(string partCode)
        {
            List<string> values = new List<string>();

            foreach (CmdUsageKindEntry entry in FindEntries(partCode))
            {
                values.Add(entry.UsageKind);
            }

            return values;
        }
    }

    /// <summary>
    /// 【CMD 分析】第 6 步「使い分け自動設定」的数据源：
    /// SQL Server 的 <c>[genpin].[dbo].[T_M_PartsMaster]</c>。
    ///
    /// <para>
    /// <b>取的两列</b>：
    /// <c>ShapeNumber</c>（形材番号 = 部品コード。与部品基本信息表的「部品コード」比对）、
    /// <c>BranchNumber</c>（使い分け，要填到部品基本信息表的值）。
    /// 也就是画面/日志上说的「<b>ShapeNumber = 输入的部品コード → 取 BranchNumber</b>」。
    /// </para>
    ///
    /// <para>
    /// <b>连接信息</b>：与部材名置换表共用一份（<see cref="MemberMappingDbSettingsStore.Resolve"/>，
    /// 默认 <c>10.103.126.208,1433 / genpin</c>）；在【部材名面板】→【连接设置】里改，
    /// 也可以用环境变量 <c>MSIF_MAPPING_DB_*</c> / App.config 覆盖。
    /// 表名里的库名按连接信息的 DB 名拼（默认就是 <c>genpin</c>），换成别的库也能跑。
    /// </para>
    ///
    /// <para>
    /// <b>部品コード 的比对</b>：表里 / 部品基本信息表里都可能写成 <c>MEA□429</c>（□ = 可变的颜色 / 种别字符），
    /// 也可能写成具体值 <c>MEACC429</c>，所以两边都按「通配符」比对
    /// （<see cref="PartCodeMatches"/>：一个符号 = 任意 1～2 个字符），否则一条都对不上。
    /// </para>
    ///
    /// <para>
    /// <b>旧数据源（后备）</b>：数据库连不上时（网络断 / SQL Server 停机 / 没有权限）退回读
    /// <c>hiroidashiData</c> 文件夹里的拾い出し表 <c>*.xlsx</c>，日志里会明确写出退回的原因，
    /// 不会因为连不上库就让第 6 步整步失败：
    /// ① App.config 的 <c>HiroidashiDataPath</c> →
    /// ② 环境变量 <c>MSIF_HIROIDASHI_DATA</c> →
    /// ③ 从分解输出目录（<c>&lt;PDF 文件夹&gt;\AiAppData</c>）逐级向上找 <c>hiroidashiData</c> /
    ///    <c>MSIF_InputData\hiroidashiData</c> →
    /// ④ 从程序目录逐级向上同样找。
    /// xlsx 的第 4 列 = <c>部品ｺｰﾄﾞ/形材番号</c>、第 12 列 = <c>使い分け</c>（第 1 行是表头）。
    /// </para>
    /// </summary>
    internal static class CmdUsageKindCollector
    {
        /// <summary>
        /// 部品マスタ表的表名 —— 第 6 步「使い分け」的数据源
        /// （<c>[genpin].[dbo].[T_M_PartsMaster]</c>）。
        /// </summary>
        public const string PartsMasterTableName = "T_M_PartsMaster";

        /// <summary>部品マスタ表的 schema（<c>dbo</c>）。</summary>
        public const string PartsMasterSchemaName = "dbo";

        /// <summary>形材番号 列名（= 部品コード；查询条件）。</summary>
        public const string ShapeNumberColumnName = "ShapeNumber";

        /// <summary>使い分け 列名（要填到部品基本信息表「使い分け」的值）。</summary>
        public const string BranchNumberColumnName = "BranchNumber";

        /// <summary>读部品マスタ表时的命令超时（秒）：整表读进内存，比默认的 30 秒宽松一点。</summary>
        private const int QueryTimeoutSeconds = 120;

        /// <summary>旧的拾い出し表所在的文件夹名（数据库连不上时的后备）。</summary>
        public const string FolderName = "hiroidashiData";

        /// <summary>上游数据根目录名（<c>MSIF_InputData\hiroidashiData</c> 这种布局）。</summary>
        public const string InputDataFolderName = "MSIF_InputData";

        /// <summary>App.config 里可覆盖文件夹位置的键。</summary>
        public const string SettingKey = "HiroidashiDataPath";

        /// <summary>可覆盖文件夹位置的环境变量。</summary>
        public const string EnvironmentVariable = "MSIF_HIROIDASHI_DATA";

        /// <summary>部品ｺｰﾄﾞ/形材番号 所在的列（第 4 列，从 0 开始 = 3）。</summary>
        public const int PartCodeColumnIndex = 3;

        /// <summary>使い分け 所在的列（第 12 列，从 0 开始 = 11）。</summary>
        public const int UsageKindColumnIndex = 11;

        /// <summary>表头文字（用来跳过表头行）。</summary>
        public const string PartCodeHeaderText = "部品ｺｰﾄﾞ/形材番号";

        /// <summary>表头文字（用来跳过表头行）。</summary>
        public const string UsageKindHeaderText = "使い分け";

        /// <summary>向上查找目录时的最大层数（避免异常路径下一直往上找；16 层足够到盘符根）。</summary>
        private const int MaxSearchDepth = 16;

        /// <summary>当成通配符处理的符号（部品コード 里的可变字符，一个符号 = 任意 1～2 个字符）。</summary>
        private static readonly char[] WildcardChars = new char[] { '□', '■', '△', '▲', '○', '●', '◇', '◆' };

        /// <summary>部品コード 比对用的正则缓存（pattern → Regex）。</summary>
        private static readonly Dictionary<string, Regex> PatternCache = new Dictionary<string, Regex>();

        /// <summary>
        /// 建立 使い分け 索引：<b>先查 SQL Server 的 <see cref="PartsMasterTableName"/> 表</b>
        /// （<c>ShapeNumber</c> = 部品コード → <c>BranchNumber</c> = 使い分け）；
        /// 连不上 / 读不到数据时才退回旧的 <c>hiroidashiData\*.xlsx</c> 拾い出し表
        /// （退回的原因会写进 <paramref name="notes"/>，日志里看得见）。
        /// </summary>
        /// <param name="outputDirectory">
        /// 分解输出根目录（&lt;PDF 文件夹&gt;\AiAppData）。只有要找后备 xlsx 时才用，可为 <c>null</c>。
        /// </param>
        /// <param name="notes">提示信息（可为 <c>null</c>）。</param>
        public static CmdUsageKindIndex LoadIndex(string outputDirectory, IList<string> notes)
        {
            CmdUsageKindIndex fromDatabase = LoadPartsMasterIndex(notes);

            if (fromDatabase.EntryCount > 0)
            {
                return fromDatabase;
            }

            // 数据库里没读到：退回旧的 hiroidashiData\*.xlsx（找不到就到此为止，失败原因上面已经记过）
            string directory = ResolveDirectory(outputDirectory, notes);

            if (string.IsNullOrEmpty(directory))
            {
                return fromDatabase;
            }

            CmdUsageKindIndex legacy = LoadLegacyIndex(directory, notes);

            if (legacy.EntryCount > 0)
            {
                legacy.SourceDescription = "文件夹 " + directory;

                AddNote(notes, string.Format(
                    "本次改用旧的 {0} 文件夹（{1} 条明细）—— 数据库恢复后会自动优先用 {2}",
                    FolderName, legacy.EntryCount, PartsMasterTableName));
            }

            return legacy;
        }

        /// <summary>
        /// 从 SQL Server 的 <c>[&lt;DB&gt;].[dbo].[T_M_PartsMaster]</c> 读全部
        /// 「<c>ShapeNumber</c> → <c>BranchNumber</c>」。
        /// 连接信息与部材名置换表共用（App.config 的具名连接 <c>connJP1</c> = 10.103.126.208 / genpin，
        /// <see cref="MemberMappingDbSettingsStore.ResolveJp1"/>）。
        ///
        /// <para>
        /// 读不到（连不上 / 表不存在 / 没有权限 / 全是空值）时返回<b>空索引</b>并说明原因、
        /// <b>不抛异常</b> —— 第 6 步只是填「使い分け」一列，这一步失败不该让整个 CMD 分析中断。
        /// </para>
        /// <para>
        /// 行按「形材番号 → 使い分け」排序：同一形材番号的 使い分け 按数字大小（<c>2</c> 在 <c>10</c> 前面）
        /// 排列，第 6 步按这个顺序依次填到同组的多行上。
        /// 表里同一「形材番号 + 使い分け」重复出现时只留一条（品種 / 材質 等别的列不同不算另一种）。
        /// </para>
        /// </summary>
        /// <param name="notes">提示信息（可为 <c>null</c>）。</param>
        public static CmdUsageKindIndex LoadPartsMasterIndex(IList<string> notes)
        {
            CmdUsageKindIndex index = new CmdUsageKindIndex();
            MemberMappingDbSettings settings;

            try
            {
                settings = MemberMappingDbSettingsStore.ResolveJp1();
            }
            catch (Exception ex)
            {
                AddNote(notes, "读取数据库连接信息失败：" + ex.Message);
                return index;
            }

            string target = settings.DescribeTarget() + " / 表 " + BuildPartsMasterTableName(settings);
            List<CmdUsageKindEntry> rows = new List<CmdUsageKindEntry>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using (SqlConnection connection = new SqlConnection(settings.BuildConnectionString()))
                {
                    connection.Open();

                    using (SqlCommand command = new SqlCommand(BuildPartsMasterSelectSql(settings), connection))
                    {
                        command.CommandTimeout = QueryTimeoutSeconds;

                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            int rowNumber = 0;

                            while (reader.Read())
                            {
                                rowNumber++;

                                string shapeNumber = GetText(reader, 0);
                                string branchNumber = GetText(reader, 1);

                                // 形材番号 没有就无从比对；使い分け 为空的行 FindEntries 也会跳过，这里直接不装
                                if (shapeNumber.Length == 0 || branchNumber.Length == 0)
                                {
                                    continue;
                                }

                                // 同一「形材番号 + 使い分け」在表里重复出现（品種 / 材質 等别的列不同）时只算一条，
                                // 否则会白占第 6 步的填值位置
                                if (!seen.Add(shapeNumber + "\u0001" + branchNumber))
                                {
                                    continue;
                                }

                                rows.Add(new CmdUsageKindEntry(shapeNumber, branchNumber,
                                    PartsMasterTableName, rowNumber));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AddNote(notes, string.Format(
                    "读取 {0} 失败：{1}（第 6 步会用旧的 {2} 文件夹作后备）", target, ex.Message, FolderName));

                return index;
            }

            if (rows.Count == 0)
            {
                AddNote(notes, string.Format(
                    "{0} 里没有可用的行（{1} / {2} 为空的都被跳过）",
                    target, ShapeNumberColumnName, BranchNumberColumnName));

                return index;
            }

            // 同一形材番号内按 使い分け 的顺序排（2 在 10 前面），不同形材番号按番号字面排
            rows.Sort(CompareEntries);

            foreach (CmdUsageKindEntry row in rows)
            {
                index.Add(row);
            }

            index.SourceDescription = target;

            return index;
        }

        /// <summary>拼完整表名：<c>[genpin].[dbo].[T_M_PartsMaster]</c>（库名用连接信息里的 DB 名）。</summary>
        private static string BuildPartsMasterTableName(MemberMappingDbSettings settings)
        {
            string database = settings == null || string.IsNullOrWhiteSpace(settings.Database)
                ? MemberMappingDbSettings.DefaultDatabase
                : settings.Database.Trim();

            return "[" + database + "].[" + PartsMasterSchemaName + "].[" + PartsMasterTableName + "]";
        }

        /// <summary>
        /// 读部品マスタ表的 SQL：<c>[ShapeNumber]</c> / <c>[BranchNumber]</c> 两列，
        /// <c>DISTINCT</c> 掉重复组合（同形材番号在不同品種 / 材質下行会重复出现）。
        ///
        /// <para>
        /// 整表读进内存后由 <see cref="PartCodeMatches"/> 做
        /// 「<c>□</c> = 任意 1～2 个字符」的比对 —— 部品マスタ 和 部品基本情報表 两边都可能写成
        /// <c>MEA□429</c>，也可能写成具体值 <c>MEACC429</c>，光靠
        /// <c>WHERE [ShapeNumber] = @code</c> 会一条都对不上，所以不在 SQL 里等值过滤，
        /// 而是在 C# 侧用同一套通配规则比对（`□` 可出现在任何位置，前置 / 后置都行）。
        /// </para>
        /// </summary>
        private static string BuildPartsMasterSelectSql(MemberMappingDbSettings settings)
        {
            return "SELECT DISTINCT [" + ShapeNumberColumnName + "], [" + BranchNumberColumnName + "] FROM " +
                BuildPartsMasterTableName(settings) +
                " WHERE [" + ShapeNumberColumnName + "] IS NOT NULL AND LTRIM(RTRIM([" +
                ShapeNumberColumnName + "])) <> '' " +
                "ORDER BY [" + ShapeNumberColumnName + "], [" + BranchNumberColumnName + "];";
        }

        /// <summary>索引行排序：形材番号 → 使い分け（数字优先按大小） → 使い分け 字面。</summary>
        private static int CompareEntries(CmdUsageKindEntry left, CmdUsageKindEntry right)
        {
            int result = string.Compare(left.PartCode, right.PartCode, StringComparison.OrdinalIgnoreCase);

            if (result != 0)
            {
                return result;
            }

            result = CompareUsageKind(left.UsageKind, right.UsageKind);

            return result != 0
                ? result
                : string.Compare(left.UsageKind, right.UsageKind, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>两个「使い分け」值的比较：都是数字时按数值，否则按字面（数字排在文字前面）。</summary>
        private static int CompareUsageKind(string left, string right)
        {
            int leftNumber;
            int rightNumber;

            bool leftIsNumber = int.TryParse((left ?? string.Empty).Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out leftNumber);
            bool rightIsNumber = int.TryParse((right ?? string.Empty).Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out rightNumber);

            if (leftIsNumber && rightIsNumber)
            {
                return leftNumber.CompareTo(rightNumber);
            }

            if (leftIsNumber != rightIsNumber)
            {
                return leftIsNumber ? -1 : 1;
            }

            return 0;
        }

        /// <summary>取单元格文本（null / DBNull → 空串，去首尾空格）。</summary>
        private static string GetText(SqlDataReader reader, int index)
        {
            return reader.IsDBNull(index)
                ? string.Empty
                : (reader.GetValue(index) ?? string.Empty).ToString().Trim();
        }

        /// <summary>
        /// 解析 <c>hiroidashiData</c> 文件夹的位置；找不到时返回空字符串，
        /// 尝试过的位置写入 <paramref name="notes"/>（日志里提示用）。
        /// </summary>
        /// <param name="outputDirectory">分解输出根目录（&lt;PDF 文件夹&gt;\AiAppData）。</param>
        /// <param name="notes">提示信息（可为 <c>null</c>）。</param>
        public static string ResolveDirectory(string outputDirectory, IList<string> notes)
        {
            string configured = ReadConfiguredDirectory();

            if (!string.IsNullOrEmpty(configured))
            {
                return configured;
            }

            string fromOutput = FindUpwards(outputDirectory);

            if (!string.IsNullOrEmpty(fromOutput))
            {
                return fromOutput;
            }

            string fromProgram = FindUpwards(AppDomain.CurrentDomain.BaseDirectory);

            if (!string.IsNullOrEmpty(fromProgram))
            {
                return fromProgram;
            }

            AddNote(notes, string.Format(
                "找不到 {0} 文件夹（已看：App.config 的 {1}、环境变量 {2}、分解输出目录与程序目录的上级）。",
                FolderName, SettingKey, EnvironmentVariable));

            return string.Empty;
        }

        /// <summary>读 App.config / 环境变量里的文件夹位置（相对路径按程序目录解析）。</summary>
        private static string ReadConfiguredDirectory()
        {
            string fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
            string resolved = ResolveCandidate(fromEnvironment);

            if (!string.IsNullOrEmpty(resolved))
            {
                return resolved;
            }

            string fromConfiguration;

            try
            {
                fromConfiguration = ConfigurationManager.AppSettings[SettingKey];
            }
            catch (ConfigurationErrorsException)
            {
                fromConfiguration = null;
            }

            return ResolveCandidate(fromConfiguration);
        }

        /// <summary>把候选路径（绝对 / 相对程序目录）变成存在的绝对路径；不存在返回空。</summary>
        private static string ResolveCandidate(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return string.Empty;
            }

            string path = candidate.Trim();

            try
            {
                if (!Path.IsPathRooted(path))
                {
                    path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
                }

                path = Path.GetFullPath(path);

                return Directory.Exists(path) ? path : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>从 <paramref name="startDirectory"/> 起逐级向上找 <c>hiroidashiData</c>。</summary>
        private static string FindUpwards(string startDirectory)
        {
            if (string.IsNullOrWhiteSpace(startDirectory))
            {
                return string.Empty;
            }

            string current;

            try
            {
                current = Path.GetFullPath(startDirectory.Trim());
            }
            catch (Exception)
            {
                return string.Empty;
            }

            for (int depth = 0; depth <= MaxSearchDepth && !string.IsNullOrEmpty(current); depth++)
            {
                string direct = ResolveCandidate(Path.Combine(current, FolderName));

                if (!string.IsNullOrEmpty(direct))
                {
                    return direct;
                }

                string nested = ResolveCandidate(Path.Combine(current, InputDataFolderName, FolderName));

                if (!string.IsNullOrEmpty(nested))
                {
                    return nested;
                }

                try
                {
                    DirectoryInfo parent = Directory.GetParent(current);
                    current = parent == null ? string.Empty : parent.FullName;
                }
                catch (Exception)
                {
                    return string.Empty;
                }
            }

            return string.Empty;
        }

        private static void AddNote(IList<string> notes, string message)
        {
            if (notes != null && !string.IsNullOrWhiteSpace(message))
            {
                notes.Add(message);
            }
        }

        /// <summary>
        /// 读 <paramref name="directory"/> 下的全部 <c>*.xlsx</c>（文件名排序），建立索引。
        /// 读不了的文件只记提示，不影响其它文件。
        /// <para>
        /// <b>旧的拾い出し表后备</b>：正常路径是 <see cref="LoadIndex(string, IList{string})"/>
        /// 查数据库；只有数据库连不上时才会走到这里（也可以被单元测试 / 手工排查单独调用）。
        /// </para>
        /// </summary>
        /// <param name="directory">拾い出し表所在文件夹。</param>
        /// <param name="notes">提示信息（可为 <c>null</c>）。</param>
        public static CmdUsageKindIndex LoadLegacyIndex(string directory, IList<string> notes)
        {
            CmdUsageKindIndex index = new CmdUsageKindIndex();

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                AddNote(notes, "拾い出し表文件夹不存在：" + directory);
                return index;
            }

            string[] files;

            try
            {
                files = Directory.GetFiles(directory, "*.xlsx", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                AddNote(notes, "读取文件夹失败（" + directory + "）：" + ex.Message);
                return index;
            }

            // 文件名排序，保证每次执行「按顺序设置」的结果一样
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            foreach (string file in files)
            {
                string fileName = Path.GetFileName(file);

                // Excel 打开时生成的临时文件
                if (fileName.StartsWith("~$", StringComparison.Ordinal))
                {
                    continue;
                }

                List<string> fileNotes = new List<string>();
                List<string[]> rows = XlsxSheetReader.ReadFirstSheet(file, fileNotes);

                foreach (string note in fileNotes)
                {
                    AddNote(notes, note);
                }

                index.FileCount++;

                for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    string[] cells = rows[rowIndex];
                    string partCode = CellText(cells, PartCodeColumnIndex);

                    if (IsHeaderRow(cells) || string.IsNullOrWhiteSpace(partCode))
                    {
                        continue;
                    }

                    index.Add(new CmdUsageKindEntry(partCode, CellText(cells, UsageKindColumnIndex),
                        fileName, rowIndex + 1));
                }
            }

            return index;
        }

        /// <summary>表头行判定（第 4 列 / 第 12 列出现表头文字）。</summary>
        private static bool IsHeaderRow(string[] cells)
        {
            return string.Equals(CellText(cells, PartCodeColumnIndex), PartCodeHeaderText,
                       StringComparison.Ordinal) ||
                   string.Equals(CellText(cells, UsageKindColumnIndex), UsageKindHeaderText,
                       StringComparison.Ordinal);
        }

        /// <summary>取一行里第 <paramref name="index"/> 列的文本（缺列返回空字符串）。</summary>
        private static string CellText(string[] cells, int index)
        {
            if (cells == null || index < 0 || index >= cells.Length || cells[index] == null)
            {
                return string.Empty;
            }

            return cells[index].Trim();
        }

        /// <summary>
        /// 两个部品コード是否指同一个部品：完全相同就算命中；否则只要任一侧带通配符
        /// （<c>□ / △ / ○ …</c>）就按通配符比对（<c>MEA□429</c> 与 <c>MEACC429</c> 视为相同）。
        /// 一个符号代表<b>任意 1～2 个字符</b>（见 <see cref="MatchWildcard"/>）。
        /// </summary>
        /// <param name="left">一侧的部品コード（通常是部品基本信息表里的值）。</param>
        /// <param name="right">另一侧的部品コード（通常来自 <c>T_M_PartsMaster.ShapeNumber</c> / xlsx 第 4 列）。</param>
        public static bool PartCodeMatches(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            string first = left.Trim();
            string second = right.Trim();

            if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return MatchWildcard(first, second) || MatchWildcard(second, first);
        }

        /// <summary>
        /// 把 <paramref name="pattern"/> 里的通配符换成 <c>.{1,2}</c> 后匹配 <paramref name="value"/>。
        /// <para>
        /// 一个符号代表 <b>任意 1～2 个字符</b>（现场的写法：<c>MEA□429</c> → <c>MEAS429</c> /
        /// <c>MEACC429</c>；第 5 步会把 <c>MEA□□405</c> 压成 <c>MEA□405</c>，所以 2 个字符也要能命中），
        /// 不是无限长度：<c>MEA429</c>（0 个字符）不会被当成 <c>MEA□429</c>。
        /// </para>
        /// </summary>
        private static bool MatchWildcard(string pattern, string value)
        {
            if (!ContainsWildcard(pattern))
            {
                return false;
            }

            // 通配符以外的字符太少时（例如只有「□」）不管内容都会命中，不算匹配
            if (CountLiteralChars(pattern) < 2)
            {
                return false;
            }

            Regex regex;

            if (!PatternCache.TryGetValue(pattern, out regex))
            {
                StringBuilder builder = new StringBuilder("^");

                foreach (char ch in pattern)
                {
                    if (IsWildcard(ch))
                    {
                        // 一个符号 = 任意 1～2 个字符（□ 可能是 1 个字，也可能是 2 个字）
                        builder.Append(".{1,2}");
                    }
                    else
                    {
                        builder.Append(Regex.Escape(ch.ToString()));
                    }
                }

                builder.Append('$');

                regex = new Regex(builder.ToString(),
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

                PatternCache[pattern] = regex;
            }

            return regex.IsMatch(value);
        }

        private static bool ContainsWildcard(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            foreach (char ch in value)
            {
                if (IsWildcard(ch))
                {
                    return true;
                }
            }

            return false;
        }

        private static int CountLiteralChars(string value)
        {
            int count = 0;

            if (string.IsNullOrEmpty(value))
            {
                return count;
            }

            foreach (char ch in value)
            {
                if (!IsWildcard(ch))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool IsWildcard(char ch)
        {
            foreach (char wildcard in WildcardChars)
            {
                if (ch == wildcard)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
