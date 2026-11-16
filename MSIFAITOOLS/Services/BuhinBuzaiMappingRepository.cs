using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// 部材不材 mapping（SQL Server 表 <c>MSIF.dbo.DL_BuhinBuzaiMapping</c>）的一条明细：
    /// <b>部品名（<c>BuhinName</c>）→ 部材名（<c>BuzaiName</c>）</b>，并带上<b>部品CD（<c>BuhinCd</c>）</b>。
    ///
    /// <para>
    /// 表的主键是 <c>BuhinName</c> + <c>BuzaiName</c>（两者都不能为空），<c>BuhinCd</c> 是后加的
    /// <c>nvarchar(100)</c> 可空列（= 部品基本信息画面上的「部品コード」）。
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

        /// <summary>部品CD（<c>BuhinCd</c>，<c>nvarchar(100)</c>，可空）= 画面上的「部品コード」。</summary>
        public string BuhinCd { get; private set; }

        /// <summary>
        /// 本次保存里同一个「部品名 + 部材名」出现多行时，用这一行补上还空着的 <see cref="BuhinCd"/>
        /// （先到的空值不覆盖已有的值，见 <see cref="BuhinBuzaiMappingRepository.Register"/>）。
        /// </summary>
        /// <param name="buhinCd">部品CD（<c>null</c> 视为空）。</param>
        internal void OverrideBuhinCd(string buhinCd)
        {
            BuhinCd = buhinCd ?? string.Empty;
        }
    }

    /// <summary>
    /// 部材不材 mapping 表（SQL Server <c>[MSIF].[dbo].[DL_BuhinBuzaiMapping]</c>）的仓储。
    ///
    /// <para>
    /// <b>表结构</b>：<c>BuhinName</c>（部品名，<c>nvarchar(100)</c>，主键）/ <c>BuzaiName</c>
    /// （部材名，<c>nvarchar(100)</c>，主键）/ <c>BuhinCd</c>（部品CD，<c>nvarchar(100)</c>，可空）/
    /// <c>Remarks</c>（<c>nvarchar(255)</c>，可空）/ <c>UpdatedAt</c>（<c>datetime2(7)</c>，可空）。
    /// 主键 = <c>BuhinName</c> + <c>BuzaiName</c>，所以登录 / 更新都按这两个值定位。
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

        /// <summary>「部品CD」列名（后加的列，本工具保存时一起写）。</summary>
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
        /// 更新已有的一行：<c>BuhinCd</c> 为空时<b>保留原来的值</b>（画面上没填部品コード时
        /// 不把表里已有的部品CD 冲成空），<c>UpdatedAt</c> 每次刷新。
        /// </summary>
        private const string UpdateSql =
            "UPDATE " + QualifiedTableName + " SET " +
            "[" + BuhinCdColumnName + "] = COALESCE(NULLIF(@BuhinCd, N''), [" + BuhinCdColumnName + "]), " +
            "[" + UpdatedAtColumnName + "] = @UpdatedAt " +
            "WHERE [" + BuhinNameColumnName + "] = @BuhinName AND [" + BuzaiNameColumnName + "] = @BuzaiName;";

        /// <summary>
        /// 登录新的一行（更新 0 行时才执行）：<c>Remarks</c> 不写（留 <c>NULL</c>，那是维护侧的备注）。
        /// </summary>
        private const string InsertSql =
            "INSERT INTO " + QualifiedTableName + " ([" + BuhinNameColumnName + "], [" +
            BuzaiNameColumnName + "], [" + BuhinCdColumnName + "], [" + RemarksColumnName + "], [" +
            UpdatedAtColumnName + "]) VALUES " +
            "(@BuhinName, @BuzaiName, NULLIF(@BuhinCd, N''), NULL, @UpdatedAt);";

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
        /// 先按 <c>BuhinName</c> + <c>BuzaiName</c> 更新（同时写 <c>BuhinCd</c> / <c>UpdatedAt</c>），
        /// 更新 0 行时才 INSERT。
        /// <para>
        /// <c>BuhinCd</c> 的规则：本次有值就覆盖；本次为空时<b>保留表里原来的值</b>
        /// （避免画面上没填部品コード的行把人家维护的部品CD 冲掉）。
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

                            // 主键两列都不能为空（表上 NOT NULL）
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
        /// 部品基本信息画面【保存】时调用：把画面上这一批行的「部品名 / 部材名 / 部品コード」
        /// 登录 / 更新到部材不材 mapping 表（<see cref="TableName"/>）。
        /// <para>
        /// 跳过的行（与「部材部品」规则 <see cref="MemberPartCatalogLoader.RegisterRules"/> 同一套判断）：
        /// 部品名 / 部材名 为空、部材名 = 「その他」（<see cref="PartBasicInfo.MemberNameFallback"/>，
        /// 意思是「不属于任何部材」）、部材名里含 <c>|</c>（多个候选、还没确定）。
        /// </para>
        /// <para>
        /// 同一个「部品名 + 部材名」在本次保存里出现多行时只写一次，<c>BuhinCd</c> 取第一个非空的值。
        /// </para>
        /// <b>不抛异常</b>（连接不上 / 没有权限 / 表不存在都只返回提示文字），
        /// 规则登录失败不影响部品基本信息本身的保存。
        /// </summary>
        /// <param name="parts">部品基本信息的行（可为 <c>null</c>）。</param>
        /// <returns>给人看的提示；没有可登录的行时返回空串。</returns>
        public static string Register(IEnumerable<PartBasicInfo> parts)
        {
            List<BuhinBuzaiMappingEntry> entries = CollectEntries(parts);

            if (entries.Count == 0)
            {
                return string.Empty;
            }

            MemberMappingDbSettings settings;

            try
            {
                settings = MemberMappingDbSettingsStore.ResolveJp2();
            }
            catch (Exception ex)
            {
                return "部材不材 mapping 未登录（连接信息解析失败）：" + ex.Message;
            }

            if (settings == null || !settings.IsConfigured)
            {
                return "部材不材 mapping 未登录：没有可用的连接信息。";
            }

            int inserted;
            int updated;

            // 保存是同步调用（画面线程）：这张表单独用短一点的连接超时，连不上时别让画面卡太久
            MemberMappingDbSettings quickSettings = new MemberMappingDbSettings(
                settings.Server, settings.Port, settings.Database, settings.UserId, settings.Password,
                settings.ConfigurationSource, ShortConnectionTimeoutSeconds, settings.ConnectionName);

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
                return string.Format("部材不材 mapping 登录失败（{0}）：{1}", settings.DescribeTarget(), ex.Message);
            }

            return string.Format("部材不材 mapping（{0}）：新增 {1} 条 / 更新 {2} 条（本次确认 {3} 条）",
                settings.DescribeTarget(), inserted, updated, entries.Count);
        }

        /// <summary>
        /// 从部品基本信息的行里挑出要写进部材不材 mapping 的组合（部品名 + 部材名 去重，带上 部品CD）。
        /// </summary>
        /// <param name="parts">部品基本信息的行（可为 <c>null</c>）。</param>
        private static List<BuhinBuzaiMappingEntry> CollectEntries(IEnumerable<PartBasicInfo> parts)
        {
            List<BuhinBuzaiMappingEntry> entries = new List<BuhinBuzaiMappingEntry>();

            if (parts == null)
            {
                return entries;
            }

            Dictionary<string, BuhinBuzaiMappingEntry> byKey =
                new Dictionary<string, BuhinBuzaiMappingEntry>(StringComparer.Ordinal);

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

                // 键里用 \u0001 分隔：部品名 / 部材名 本身不会含这个字符，不会拼出同一个键
                string key = buhinName + "\u0001" + buzaiName;

                BuhinBuzaiMappingEntry existing;

                if (byKey.TryGetValue(key, out existing))
                {
                    // 画面里重复的行：部品CD 只补第一个非空的值
                    if (existing.BuhinCd.Length == 0)
                    {
                        existing.OverrideBuhinCd(buhinCd);
                    }

                    continue;
                }

                BuhinBuzaiMappingEntry created = new BuhinBuzaiMappingEntry(buhinName, buzaiName, buhinCd);

                byKey.Add(key, created);
                entries.Add(created);
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
        /// <param name="buhinCd">部品CD（已去空白、已截断；空串由 SQL 的 <c>NULLIF</c> 变成 <c>NULL</c>）。</param>
        /// <param name="updatedAt">更新日時。</param>
        private static void BindParameters(SqlCommand command, string buhinName, string buzaiName, string buhinCd,
            DateTime updatedAt)
        {
            command.Parameters["@BuhinName"].Value = buhinName;
            command.Parameters["@BuzaiName"].Value = buzaiName;
            command.Parameters["@BuhinCd"].Value = buhinCd;
            command.Parameters["@UpdatedAt"].Value = updatedAt;
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
