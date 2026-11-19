using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// 部材 mapping（SQL Server 表 <c>MSIF.dbo.DL_BuhinBuzaiMapping</c>）的一条明细：
    /// <b>部品名（<c>BuhinName</c>）→ 部材名（<c>BuzaiName</c>）</b>，并带上<b>部品CD（<c>BuhinCd</c>）</b>。
    ///
    /// <para>
    /// 表的主键是三列 <c>BuhinName</c> + <c>BuzaiName</c> + <c>BuhinCd</c>
    /// （三列都是 <c>nvarchar(100)</c>、都不能为空）。<c>BuhinCd</c> = 部品基本信息画面上的「部品コード」，
    /// 画面上没填时用<b>空串</b>表示（主键列不能是 <c>NULL</c>）。
    /// 所以同一个「部品名 + 部材名」可以按不同的部品CD 存成多行。
    /// </para>
    /// </summary>
    internal sealed class BuhinBuzaiMappingEntry
    {
        public BuhinBuzaiMappingEntry(string buhinName, string buzaiName, string buhinCd)
        {
            BuhinName = buhinName ?? string.Empty;
            BuzaiName = buzaiName ?? string.Empty;
            BuhinCd = buhinCd ?? string.Empty;
        }

        /// <summary>部品名（<c>BuhinName</c>，主键的一部分，<c>nvarchar(100)</c>）。</summary>
        public string BuhinName { get; private set; }

        /// <summary>部材名（<c>BuzaiName</c>，主键的一部分，<c>nvarchar(100)</c>）。</summary>
        public string BuzaiName { get; private set; }

        /// <summary>部品CD（<c>BuhinCd</c>，<c>nvarchar(100)</c>，主键的一部分，画面上没填时为空串）= 画面上的「部品コード」。</summary>
        public string BuhinCd { get; private set; }
    }

    /// <summary>
    /// 部材 mapping 表（SQL Server <c>[MSIF].[dbo].[DL_BuhinBuzaiMapping]</c>）的仓储。
    ///
    /// <para>
    /// <b>表结构</b>：<c>BuhinName</c>（部品名，<c>nvarchar(100)</c>，主键）/ <c>BuzaiName</c>
    /// （部材名，<c>nvarchar(100)</c>，主键）/ <c>BuhinCd</c>（部品CD，<c>nvarchar(100)</c>，主键）/
    /// <c>Remarks</c>（<c>nvarchar(255)</c>，可空）/ <c>UpdatedAt</c>（<c>datetime2(7)</c>，可空）。
    /// 主键 = <c>BuhinName</c> + <c>BuzaiName</c> + <c>BuhinCd</c>（三列都不能为空；画面上没填
    /// 部品コード时写<b>空串</b>），所以登录 / 更新都按这三个值定位。
    /// </para>
    ///
    /// <para>
    /// <b>什么时候写</b>：部品基本信息画面（<see cref="MSIFAITOOLS.PartBasicInfoForm"/>）点【保存】、
    /// 部品基本信息写进 SQLite 成功后，由 <see cref="Register"/> 把画面上这一批行的
    /// 「部品名 / 部材名 / 部品コード」登录 / 更新进这张表
    /// （和「部材部品」规则的 <see cref="MemberPartCatalogLoader.RegisterRules"/> 同一个时机）。
    /// 失败只返回提示，<b>不抛异常</b>，不影响部品基本信息本身的保存。
    /// </para>
    ///
    /// <para>
    /// <b>连接</b>：<c>connJP2</c>（App.config 的 <c>&lt;connectionStrings&gt;</c>，
    /// 10.103.126.209 / MSIF；见 <see cref="MemberMappingDbSettings.JP2ConnectionName"/>），
    /// 由 <see cref="MemberMappingDbSettingsStore.ResolveJp2"/> 解析
    /// （环境变量 → <c>connJP2</c> → 默认值；连接设置画面保存的那一套是 genpin 用的，
    /// 只有它正好也指向 MSIF 这台库时才采用）。
    /// 注意和部材名置换表（<c>connJP1</c> / genpin）不是同一个库。
    /// </para>
    /// </summary>
    internal sealed class BuhinBuzaiMappingRepository : IDisposable
    {
        /// <summary>表名。</summary>
        public const string TableName = "DL_BuhinBuzaiMapping";

        /// <summary>架构名。</summary>
        public const string SchemaName = "dbo";

        /// <summary>「部品名」列名（主键的一部分）。</summary>
        public const string BuhinNameColumnName = "BuhinName";

        /// <summary>「部材名」列名（主键的一部分）。</summary>
        public const string BuzaiNameColumnName = "BuzaiName";

        /// <summary>「部品CD」列名（主键的一部分，本工具保存时一起写；没填时写空串）。</summary>
        public const string BuhinCdColumnName = "BuhinCd";

        /// <summary>「備考」列名（本表维护侧的备注：本工具新建行时留 <c>NULL</c>，更新时不改动这一列）。</summary>
        public const string RemarksColumnName = "Remarks";

        /// <summary>「更新日時」列名（本工具每次登录 / 更新都写当前时间）。</summary>
        public const string UpdatedAtColumnName = "UpdatedAt";

        /// <summary><c>BuhinName</c> / <c>BuzaiName</c> / <c>BuhinCd</c> 的列长（<c>nvarchar(100)</c>）。</summary>
        public const int NameMaxLength = 100;

        /// <summary><c>Remarks</c> 的列长（<c>nvarchar(255)</c>）。</summary>
        public const int RemarksMaxLength = 255;

        /// <summary>
        /// 保存时写这张表的连接超时（秒）：比 <see cref="MemberMappingDbSettings.ConnectTimeoutSeconds"/>（15 秒）
        /// 短一些 —— 现场没有这台库时，【保存】画面不会卡太久（失败只是多一句提示，部品基本信息照样保存）。
        /// </summary>
        private const int ShortConnectionTimeoutSeconds = 5;

        /// <summary>带架构名的表名（<c>[dbo].[DL_BuhinBuzaiMapping]</c>）。</summary>
        private const string QualifiedTableName = "[" + SchemaName + "].[" + TableName + "]";

        /// <summary>
        /// 更新已有的一行：主键三列（<c>BuhinName</c> / <c>BuzaiName</c> / <c>BuhinCd</c>）都是定位条件，
        /// 所以这里只刷新 <c>UpdatedAt</c>（主键列本身不会被改）。
        /// <para>
        /// <c>BuhinCd</c> 用 <c>ISNULL(..., N'')</c> 比较：主键列现在不能为空，但加主键前留下的老数据里
        /// 这一列可能是 <c>NULL</c>，空串也要能命中那些行（否则会多登录一条重复的行）。
        /// </para>
        /// </summary>
        private const string UpdateSql =
            "UPDATE " + QualifiedTableName + " SET " +
            "[" + UpdatedAtColumnName + "] = @UpdatedAt " +
            "WHERE [" + BuhinNameColumnName + "] = @BuhinName AND [" + BuzaiNameColumnName + "] = @BuzaiName AND " +
            "ISNULL([" + BuhinCdColumnName + "], N'') = @BuhinCd;";

        /// <summary>
        /// 登录新的一行（更新 0 行时才执行）：<c>Remarks</c> 不写（留 <c>NULL</c>，那是维护侧的备注）。
        /// <c>BuhinCd</c> 是主键的一部分（不能为 <c>NULL</c>）：画面上没填部品コード时<b>直接写空串</b>，
        /// 所以这里不再用以前的 <c>NULLIF(@BuhinCd, N'')</c>。
        /// </summary>
        private const string InsertSql =
            "INSERT INTO " + QualifiedTableName + " ([" + BuhinNameColumnName + "], [" +
            BuzaiNameColumnName + "], [" + BuhinCdColumnName + "], [" + RemarksColumnName + "], [" +
            UpdatedAtColumnName + "]) VALUES " +
            "(@BuhinName, @BuzaiName, @BuhinCd, NULL, @UpdatedAt);";

        /// <summary>
        /// 整表读出（「选择部材名」窗口的推荐项用，<see cref="BuhinBuzaiMappingCatalog"/> 在
        /// 画面打开 / 【保存】成功后调用）。只取三个名字列，
        /// 按 <c>BuhinCd</c> → <c>BuhinName</c> → <c>BuzaiName</c> 排序（同一个部品CD 的部材名排在一起）。
        /// </summary>
        private const string SelectSql =
            "SELECT [" + BuhinNameColumnName + "], [" + BuzaiNameColumnName + "], [" + BuhinCdColumnName +
            "] FROM " + QualifiedTableName + " ORDER BY [" + BuhinCdColumnName + "], [" +
            BuhinNameColumnName + "], [" + BuzaiNameColumnName + "];";

        private readonly SqlConnection _connection;

        private readonly int _commandTimeoutSeconds;

        /// <summary>
        /// 按给定的连接信息打开连接（连接信息一般来自
        /// <see cref="MemberMappingDbSettingsStore.ResolveJp2"/>）。连不上时构造函数抛异常。
        /// </summary>
        /// <param name="settings">连接信息（不能为 <c>null</c>）。</param>
        public BuhinBuzaiMappingRepository(MemberMappingDbSettings settings)
            : this(settings, 0)
        {
        }

        /// <summary>同上；<paramref name="commandTimeoutSeconds"/> = 0 时用连接信息里的超时秒数。</summary>
        /// <param name="settings">连接信息（不能为 <c>null</c>）。</param>
        /// <param name="commandTimeoutSeconds">SQL 命令超时（秒）；0 = 用 <see cref="MemberMappingDbSettings.TimeoutSeconds"/>。</param>
        public BuhinBuzaiMappingRepository(MemberMappingDbSettings settings, int commandTimeoutSeconds)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            Settings = settings;
            _commandTimeoutSeconds = commandTimeoutSeconds > 0 ? commandTimeoutSeconds : settings.TimeoutSeconds;
            _connection = new SqlConnection(settings.BuildConnectionString());
            _connection.Open();
        }

        /// <summary>本次用的连接信息（日志 / 画面上可以 <see cref="MemberMappingDbSettings.DescribeTarget"/>）。</summary>
        public MemberMappingDbSettings Settings { get; private set; }

        /// <summary>
        /// 登录 / 更新一批行（同一事务内，一条 SQL 一次往返）：
        /// 先按主键三列 <c>BuhinName</c> + <c>BuzaiName</c> + <c>BuhinCd</c> 更新（只刷新 <c>UpdatedAt</c>），
        /// 更新 0 行时才 INSERT。
        /// <para>
        /// 三列都是主键，所以不再出现「本次没填部品コード就把已有值冲掉」的情况：
        /// 部品CD 不同就是<b>不同的行</b>（同一个「部品名 + 部材名」可以有多个部品CD）。
        /// </para>
        /// </summary>
        /// <param name="entries">要写的行（<c>null</c> / 空集合时什么都不做）。</param>
        /// <param name="insertedCount">新登录的行数（更新了多少行是返回值）。</param>
        /// <returns>实际更新的行数。</returns>
        public int Upsert(IEnumerable<BuhinBuzaiMappingEntry> entries, out int insertedCount)
        {
            insertedCount = 0;

            if (entries == null)
            {
                return 0;
            }

            DateTime updatedAt = DateTime.Now;
            int updatedCount = 0;

            using (SqlTransaction transaction = _connection.BeginTransaction())
            {
                using (SqlCommand updateCommand = CreateCommand(UpdateSql, transaction))
                {
                    using (SqlCommand insertCommand = CreateCommand(InsertSql, transaction))
                    {
                        foreach (BuhinBuzaiMappingEntry entry in entries)
                        {
                            if (entry == null)
                            {
                                continue;
                            }

                            string buhinName = Normalize(entry.BuhinName, NameMaxLength);
                            string buzaiName = Normalize(entry.BuzaiName, NameMaxLength);

                            // 部品名 / 部材名 是主键的一部分，都不能为空（表上 NOT NULL）；
                            // 部品CD 没填时是空串（主键列也不能是 NULL）
                            if (buhinName.Length == 0 || buzaiName.Length == 0)
                            {
                                continue;
                            }

                            string buhinCd = Normalize(entry.BuhinCd, NameMaxLength);

                            BindParameters(updateCommand, buhinName, buzaiName, buhinCd, updatedAt);

                            if (updateCommand.ExecuteNonQuery() > 0)
                            {
                                updatedCount++;
                                continue;
                            }

                            BindParameters(insertCommand, buhinName, buzaiName, buhinCd, updatedAt);
                            insertedCount += insertCommand.ExecuteNonQuery();
                        }
                    }
                }

                transaction.Commit();
            }

            return updatedCount;
        }

        /// <summary>
        /// 【测试连接】：真的连一次库，并检查表 <see cref="TableName"/> 是否存在
        /// （表不存在也算连上了，只是结果文字里提示确认 DB 名）。
        /// <para>
        /// 主画面【接続② MSIF】的连接设置画面用它；<see cref="MemberNameReplacementRepository.TestConnection"/>
        /// 是 <c>connJP1</c>（置换表）那一侧的同样功能。
        /// </para>
        /// </summary>
        /// <param name="settings">要测试的连接信息。</param>
        /// <param name="message">结果文字（成功 / 失败原因）。</param>
        public static bool TestConnection(MemberMappingDbSettings settings, out string message)
        {
            message = string.Empty;

            if (settings == null)
            {
                message = "连接信息不能为空。";
                return false;
            }

            string validationError;

            if (!settings.Validate(out validationError))
            {
                message = validationError;
                return false;
            }

            try
            {
                using (SqlConnection connection = new SqlConnection(settings.BuildConnectionString()))
                {
                    connection.Open();

                    bool tableExists;
                    int rowCount = 0;

                    using (SqlCommand command = new SqlCommand(
                        "SELECT CASE WHEN OBJECT_ID(@name, N'U') IS NULL THEN 0 ELSE 1 END;", connection))
                    {
                        command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value =
                            SchemaName + "." + TableName;
                        object value = command.ExecuteScalar();
                        tableExists = value != null && value != DBNull.Value && Convert.ToInt32(value) != 0;
                    }

                    if (tableExists)
                    {
                        using (SqlCommand command = new SqlCommand(
                            "SELECT COUNT(*) FROM " + QualifiedTableName + ";", connection))
                        {
                            object value = command.ExecuteScalar();
                            rowCount = value == null || value == DBNull.Value ? -1 : Convert.ToInt32(value);
                        }
                    }

                    message = tableExists
                        ? "连接成功：" + settings.DescribeTarget() + " / 表 " + SchemaName + "." + TableName +
                          "（现有 " + rowCount + " 行）"
                        : "连接成功，但库 " + settings.Database + " 里没有表 " + SchemaName + "." + TableName +
                          "。请确认 DB 名（部材 mapping 在 MSIF 库）以及该表是否存在。";

                    return true;
                }
            }
            catch (Exception ex)
            {
                message = "连接失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 用当前生效的连接信息描述目标（日志 / 画面提示用），
        /// 例：<c>SQL Server 10.103.126.209,1433 / DB MSIF / 表 DL_BuhinBuzaiMapping</c>。
        /// </summary>
        public static string DescribeTarget()
        {
            return ResolveTargetDescription(null);
        }

        /// <summary>用指定连接信息描述目标（<paramref name="settings"/> 为 <c>null</c> 时按 <see cref="MemberMappingDbSettingsStore.ResolveJp2"/>）。</summary>
        /// <param name="settings">连接信息（可为 <c>null</c>）。</param>
        public static string ResolveTargetDescription(MemberMappingDbSettings settings)
        {
            MemberMappingDbSettings resolved = settings ?? MemberMappingDbSettingsStore.ResolveJp2();

            return resolved.DescribeTarget() + " / 表 " + TableName;
        }

        /// <summary>
        /// 读出整张表（只读；「选择部材名」窗口的推荐项由 <see cref="BuhinBuzaiMappingCatalog"/> 缓存一份）。
        /// <para>
        /// 部品名 / 部材名 为空的行跳过（主键的一部分，写入时也不会出现）。
        /// 同一个「部品CD」可能对应多个部材名、同一个「部品名」也可能对应多个部材名，全部原样返回。
        /// </para>
        /// 连不上 / 没有权限 / 表不存在时<b>抛异常</b>（调用方转成提示文字）。
        /// </summary>
        /// <returns>读到的行（按 <c>BuhinCd</c> → <c>BuhinName</c> → <c>BuzaiName</c> 排序）。</returns>
        public List<BuhinBuzaiMappingEntry> Query()
        {
            List<BuhinBuzaiMappingEntry> entries = new List<BuhinBuzaiMappingEntry>();

            using (SqlCommand command = new SqlCommand(SelectSql, _connection))
            {
                command.CommandTimeout = _commandTimeoutSeconds;

                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string buhinName = GetText(reader, 0);
                        string buzaiName = GetText(reader, 1);

                        if (buhinName.Length == 0 || buzaiName.Length == 0)
                        {
                            continue;
                        }

                        entries.Add(new BuhinBuzaiMappingEntry(buhinName, buzaiName, GetText(reader, 2)));
                    }
                }
            }

            return entries;
        }

        /// <summary>
        /// 部品基本信息画面【保存】时调用：把画面上这一批行的「部品名 / 部材名 / 部品コード」
        /// 登录 / 更新到部材 mapping 表（<see cref="TableName"/>）。
        /// <para>
        /// 跳过的行（与「部材部品」规则 <see cref="MemberPartCatalogLoader.RegisterRules"/> 同一套判断）：
        /// 部品名 / 部材名 为空、部材名 = 「その他」（<see cref="PartBasicInfo.MemberNameFallback"/>，
        /// 意思是「不属于任何部材」）、部材名里含 <c>|</c>（多个候选、还没确定）。
        /// </para>
        /// <para>
        /// 同一个「部品名 + 部材名 + 部品CD」在本次保存里出现多行时只写一次
        /// （主键是这三列，部品CD 不同的行算不同的 mapping）。
        /// </para>
        /// <para>
        /// 同一批组合还会写进<b>本机表</b>（SQLite <c>DL_BuhinBuzaiMapping</c>，
        /// <see cref="LocalBuhinBuzaiMappingRepository"/>）：SQL Server 写不进去时也写，
        /// 这样下次连不上服务器时「选择部材名」窗口还能推荐这些组合。
        /// </para>
        /// <b>不抛异常</b>（连接不上 / 没有权限 / 表不存在都只返回提示文字），
        /// 规则登录失败不影响部品基本信息本身的保存。
        /// </summary>
        /// <param name="parts">部品基本信息的行（可为 <c>null</c>）。</param>
        /// <returns>给人看的提示；没有可登录的行时返回空串。</returns>
        public static string Register(IEnumerable<PartBasicInfo> parts)
        {
            bool succeeded;

            return Register(parts, out succeeded);
        }

        /// <summary>
        /// 同上；<paramref name="succeeded"/> 告诉调用方这张表是否<b>真的更新成功</b>
        /// （画面用它决定要不要重新读一次内存里的 <see cref="BuhinBuzaiMappingCatalog"/>）。
        /// </summary>
        /// <param name="parts">部品基本信息的行（可为 <c>null</c>）。</param>
        /// <param name="succeeded">true = 已登录 / 更新（没有可登录的行也算成功）；false = 连接信息 / 写库失败。</param>
        /// <returns>给人看的提示；没有可登录的行时返回空串。</returns>
        public static string Register(IEnumerable<PartBasicInfo> parts, out bool succeeded)
        {
            succeeded = false;

            List<BuhinBuzaiMappingEntry> entries = CollectEntries(parts);

            if (entries.Count == 0)
            {
                succeeded = true;

                return string.Empty;
            }

            MemberMappingDbSettings settings;

            try
            {
                settings = MemberMappingDbSettingsStore.ResolveJp2();
            }
            catch (Exception ex)
            {
                return AppendLocalCacheNote("部材 mapping 未登录（连接信息解析失败）：" + ex.Message,
                    LocalBuhinBuzaiMappingRepository.TryUpsert(entries));
            }

            if (settings == null || !settings.IsConfigured)
            {
                // 服务器没配置也照样记一份到本机表：部材名面板至少还能推荐以前登录过的组合
                return AppendLocalCacheNote("部材 mapping 未登录：没有可用的连接信息。",
                    LocalBuhinBuzaiMappingRepository.TryUpsert(entries));
            }

            int inserted;
            int updated;

            // 保存是同步调用（画面线程）：这张表单独用短一点的连接超时，连不上时别让画面卡太久
            MemberMappingDbSettings quickSettings = new MemberMappingDbSettings(
                settings.Server, settings.Port, settings.Database, settings.UserId, settings.Password,
                settings.ConfigurationSource, ShortConnectionTimeoutSeconds, settings.ConnectionName);

            string localError;

            try
            {
                using (BuhinBuzaiMappingRepository repository =
                    new BuhinBuzaiMappingRepository(quickSettings))
                {
                    updated = repository.Upsert(entries, out inserted);
                }
            }
            catch (Exception ex)
            {
                // 服务器写不进去也把这批写进本机表：部材名面板下次照样能推荐（只在这台机器上有效）
                localError = LocalBuhinBuzaiMappingRepository.TryUpsert(entries);

                return AppendLocalCacheNote(
                    string.Format("部材 mapping 登录失败（{0}）：{1}", settings.DescribeTarget(), ex.Message),
                    localError);
            }

            succeeded = true;

            // 本机缓存也跟着更新：下次 SQL Server 连不上时，部材名面板还能推荐这次登录的组合
            localError = LocalBuhinBuzaiMappingRepository.TryUpsert(entries);

            return AppendLocalCacheNote(
                string.Format("部材 mapping（{0}）：新增 {1} 条 / 更新 {2} 条（本次确认 {3} 条）",
                    settings.DescribeTarget(), inserted, updated, entries.Count),
                localError);
        }

        /// <summary>
        /// 本机表（SQLite <c>DL_BuhinBuzaiMapping</c>）没更新成功时，在返回的提示后面补一句原因；
        /// 更新成功（<paramref name="localError"/> 为空串）时原样返回。
        /// </summary>
        /// <param name="message">给画面看的提示。</param>
        /// <param name="localError">本机表写入失败的原因（成功时为空串）。</param>
        private static string AppendLocalCacheNote(string message, string localError)
        {
            return string.IsNullOrEmpty(localError)
                ? message
                : message + "；本机表 " + LocalBuhinBuzaiMappingRepository.TableName + " 未更新：" + localError;
        }

        /// <summary>
        /// 从部品基本信息的行里挑出要写进部材 mapping 的组合
        /// （<b>部品名 + 部材名 + 部品CD</b> 三列去重 —— 这三列就是表的主键）。
        /// </summary>
        /// <param name="parts">部品基本信息的行（可为 <c>null</c>）。</param>
        private static List<BuhinBuzaiMappingEntry> CollectEntries(IEnumerable<PartBasicInfo> parts)
        {
            List<BuhinBuzaiMappingEntry> entries = new List<BuhinBuzaiMappingEntry>();

            if (parts == null)
            {
                return entries;
            }

            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);

            foreach (PartBasicInfo part in parts)
            {
                if (part == null)
                {
                    continue;
                }

                string buhinName = Normalize(part.PartName, NameMaxLength);
                string buzaiName = Normalize(part.MemberName, NameMaxLength);

                if (buhinName.Length == 0 || !IsLearnableBuzaiName(buzaiName))
                {
                    continue;
                }

                string buhinCd = Normalize(part.PartCode, NameMaxLength);

                // 主键三列都进键里（\u0001 分隔：部品名 / 部材名 / 部品CD 本身不会含这个字符）
                string key = buhinName + "\u0001" + buzaiName + "\u0001" + buhinCd;

                if (!keys.Add(key))
                {
                    // 本次保存里主键完全相同的行只写一次
                    continue;
                }

                entries.Add(new BuhinBuzaiMappingEntry(buhinName, buzaiName, buhinCd));
            }

            return entries;
        }

        /// <summary>
        /// 这一行的部材名 能不能当成 mapping 的「部材名」（<c>BuzaiName</c>）：
        /// 空 / 「その他」/ 含 <c>|</c>（多个候选、未确定）都不算。
        /// 判断规则与 <see cref="MemberPartCatalogLoader.RegisterRules"/> 完全一致。
        /// </summary>
        /// <param name="buzaiName">画面上的部材名。</param>
        private static bool IsLearnableBuzaiName(string buzaiName)
        {
            if (string.IsNullOrWhiteSpace(buzaiName))
            {
                return false;
            }

            if (buzaiName.IndexOf('|') >= 0 || buzaiName.IndexOf('｜') >= 0)
            {
                return false;
            }

            string key = MemberPartCatalogLoader.NormalizeKey(buzaiName);

            return key.Length > 0 &&
                !string.Equals(key, MemberPartCatalogLoader.NormalizeKey(PartBasicInfo.MemberNameFallback),
                    StringComparison.Ordinal);
        }

        /// <summary>按 <see cref="UpdateSql"/> / <see cref="InsertSql"/> 建命令（参数一次建好，之后只改值）。</summary>
        /// <param name="sql">SQL 语句。</param>
        /// <param name="transaction">本次的事务。</param>
        private SqlCommand CreateCommand(string sql, SqlTransaction transaction)
        {
            SqlCommand command = new SqlCommand(sql, _connection, transaction);

            command.CommandTimeout = _commandTimeoutSeconds;
            command.Parameters.Add("@BuhinName", SqlDbType.NVarChar, NameMaxLength);
            command.Parameters.Add("@BuzaiName", SqlDbType.NVarChar, NameMaxLength);
            command.Parameters.Add("@BuhinCd", SqlDbType.NVarChar, NameMaxLength);
            command.Parameters.Add("@UpdatedAt", SqlDbType.DateTime2);

            return command;
        }

        /// <summary>给命令的参数赋值（<see cref="UpdateSql"/> 与 <see cref="InsertSql"/> 的参数名相同）。</summary>
        /// <param name="command">已建好参数的命令。</param>
        /// <param name="buhinName">部品名（已去空白、已截断）。</param>
        /// <param name="buzaiName">部材名（已去空白、已截断）。</param>
        /// <param name="buhinCd">部品CD（已去空白、已截断；没填时为空串 —— 主键列不能是 <c>NULL</c>）。</param>
        /// <param name="updatedAt">更新日時。</param>
        private static void BindParameters(SqlCommand command, string buhinName, string buzaiName, string buhinCd,
            DateTime updatedAt)
        {
            command.Parameters["@BuhinName"].Value = buhinName;
            command.Parameters["@BuzaiName"].Value = buzaiName;
            command.Parameters["@BuhinCd"].Value = buhinCd;
            command.Parameters["@UpdatedAt"].Value = updatedAt;
        }

        /// <summary>取一列的文本（<c>null</c> / <c>DBNull</c> → 空串，去首尾空白）。</summary>
        private static string GetText(SqlDataReader reader, int index)
        {
            return reader.IsDBNull(index) ? string.Empty : (reader.GetValue(index) ?? string.Empty).ToString().Trim();
        }

        /// <summary>去空白 + 按列长截断（列是 <c>nvarchar(100)</c> / <c>nvarchar(255)</c>，超长会整条写失败）。</summary>
        /// <param name="value">原值（可为 <c>null</c>）。</param>
        /// <param name="maxLength">最大长度（字符数）。</param>
        private static string Normalize(string value, int maxLength)
        {
            string text = value == null ? string.Empty : value.Trim();

            if (maxLength <= 0 || text.Length <= maxLength)
            {
                return text;
            }

            return text.Substring(0, maxLength);
        }

        /// <summary>关闭连接（用 <c>using</c> 包住本对象即可）。</summary>
        public void Dispose()
        {
            _connection.Dispose();
        }
    }
}
