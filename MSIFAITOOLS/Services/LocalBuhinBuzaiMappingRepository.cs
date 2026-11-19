using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// 本机的「部材 mapping」表（SQLite 表 <c>DL_BuhinBuzaiMapping</c>）：
    /// SQL Server <c>[MSIF].[dbo].[DL_BuhinBuzaiMapping]</c> 的<b>本机副本</b>，
    /// 用在「SQL Server 连不上时，部材名面板照样能推荐以前用过的部材名」。
    ///
    /// <para>
    /// <b>表结构</b>（列名与服务器一致，方便对照 / 导出）：
    /// <c>BuhinName</c>（部品名）/ <c>BuzaiName</c>（部材名）/ <c>BuhinCd</c>（部品CD）/ <c>UpdatedAt</c>（更新日時），
    /// 主键 = <c>BuhinName</c> + <c>BuzaiName</c> + <c>BuhinCd</c>（与服务器的主键相同，所以不会出现重复行）。
    /// 老版本的本机库是两列主键，打开时会自动升级成三列主键（见 <c>UpgradeFromTwoColumnKey</c>）。
    /// </para>
    ///
    /// <para>
    /// <b>数据库文件</b>（<see cref="ResolveDatabasePath"/>）：与「部材部品」マスタ同一个本机库
    /// （<see cref="MemberPartRepository.ResolveDatabasePath"/>：环境变量 <c>MSIF_MEMBER_PARTS_DB</c>
    /// → App.config 的 <c>MemberPartDatabasePath</c> → 默认 <c>D:\MSIFDB\MSIF_MemberParts.db</c>），
    /// 相当于在这台机器的库里多一张 <c>DL_BuhinBuzaiMapping</c> 表。
    /// </para>
    ///
    /// <para>
    /// <b>什么时候写</b>：
    /// ① 部品基本信息画面【保存】时，<see cref="BuhinBuzaiMappingRepository"/> 的 <c>Register</c>
    ///    和服务器同一步更新（服务器写不进去时也写本机，所以本机缓存不会因为网络断掉就丢掉这次登录的组合）；
    /// ② <see cref="BuhinBuzaiMappingCatalog.Reload"/> / <see cref="BuhinBuzaiMappingCatalog.Preload"/>
    ///    整表读服务器成功时，用 <see cref="ReplaceAll"/> 把整表镜像过来（服务器为准，服务器上删掉的行这里也删掉）。
    /// </para>
    ///
    /// <para>
    /// <b>什么时候读</b>：读服务器失败时由 <see cref="BuhinBuzaiMappingCatalog"/> 兜底读这一张表
    /// （见 <see cref="BuhinBuzaiMappingCatalog.IsLocalFallback"/>）。读 / 写失败都<b>不抛异常</b>
    /// （<see cref="TryReplaceAll"/> / <see cref="TryUpsert"/> 只返回原因文字），不会影响画面操作。
    /// </para>
    /// </summary>
    internal sealed class LocalBuhinBuzaiMappingRepository : IDisposable
    {
        /// <summary>表名（与 SQL Server 的表同名，方便对照）。</summary>
        public const string TableName = "DL_BuhinBuzaiMapping";

        /// <summary>「部品名」列名（主键的一部分）。</summary>
        public const string BuhinNameColumnName = "BuhinName";

        /// <summary>「部材名」列名（主键的一部分）。</summary>
        public const string BuzaiNameColumnName = "BuzaiName";

        /// <summary>「部品CD」列名（主键的一部分，= 画面上的「部品コード」；没填时为空串）。</summary>
        public const string BuhinCdColumnName = "BuhinCd";

        /// <summary>「更新日時」列名（本机最后一次写入的时间，排查用）。</summary>
        public const string UpdatedAtColumnName = "UpdatedAt";

        /// <summary><c>BuhinName</c> / <c>BuzaiName</c> / <c>BuhinCd</c> 的列长（与服务器 <c>nvarchar(100)</c> 一致）。</summary>
        public const int NameMaxLength = BuhinBuzaiMappingRepository.NameMaxLength;

        /// <summary><c>BuhinCd</c> 的索引名（按部品CD 取推荐项时用；<c>BuhinName</c> 是主键首列，已有主键索引）。</summary>
        private const string BuhinCdIndexName = "ix_DL_BuhinBuzaiMapping_BuhinCd";

        /// <summary>整表读出（排序与服务器 <c>SELECT</c> 一致，缓存建索引的顺序就一致）。</summary>
        private const string SelectSql =
            "SELECT [" + BuhinNameColumnName + "], [" + BuzaiNameColumnName + "], [" + BuhinCdColumnName +
            "] FROM [" + TableName + "] ORDER BY [" + BuhinCdColumnName + "], [" + BuhinNameColumnName +
            "], [" + BuzaiNameColumnName + "];";

        /// <summary>整表替换时先清空。</summary>
        private const string DeleteAllSql = "DELETE FROM [" + TableName + "];";

        /// <summary>
        /// 插入一行（整表替换 / 更新 0 行时用）：<c>BuhinCd</c> 是主键的一部分（<c>NOT NULL</c>），
        /// 没填部品コード时直接写空串（以前是 <c>NULLIF(@BuhinCd, '')</c>）。
        /// </summary>
        private const string InsertSql =
            "INSERT INTO [" + TableName + "] ([" + BuhinNameColumnName + "], [" + BuzaiNameColumnName + "], [" +
            BuhinCdColumnName + "], [" + UpdatedAtColumnName + "]) " +
            "VALUES (@BuhinName, @BuzaiName, @BuhinCd, @UpdatedAt);";

        /// <summary>
        /// 更新已有的一行（<see cref="Upsert"/>）：按主键三列定位，只刷新 <c>UpdatedAt</c>
        /// —— 与服务器 <see cref="BuhinBuzaiMappingRepository"/> 的更新规则一致。
        /// <c>BuhinCd</c> 用 <c>IFNULL(..., '')</c> 比较：老本机库里这一列可能是 <c>NULL</c>。
        /// </summary>
        private const string UpdateSql =
            "UPDATE [" + TableName + "] SET [" + UpdatedAtColumnName + "] = @UpdatedAt " +
            "WHERE [" + BuhinNameColumnName + "] = @BuhinName AND [" + BuzaiNameColumnName + "] = @BuzaiName AND " +
            "IFNULL([" + BuhinCdColumnName + "], '') = @BuhinCd;";

        /// <summary>
        /// 建表用的 DDL（已存在时什么都不做）：主键是三列（与服务器的表一致），
        /// 所以同一个「部品名 + 部材名」按不同的部品CD 可以有好几行。
        /// 老本机库是两列主键，见 <see cref="UpgradeFromTwoColumnKey"/>。
        /// </summary>
        private const string CreateTableSql =
            "CREATE TABLE IF NOT EXISTS [" + TableName + "] (" +
            "[" + BuhinNameColumnName + "] TEXT NOT NULL, " +
            "[" + BuzaiNameColumnName + "] TEXT NOT NULL, " +
            "[" + BuhinCdColumnName + "] TEXT NOT NULL DEFAULT '', " +
            "[" + UpdatedAtColumnName + "] TEXT, " +
            "PRIMARY KEY ([" + BuhinNameColumnName + "], [" + BuzaiNameColumnName + "], [" + BuhinCdColumnName + "])" +
            ");";

        /// <summary>「部品CD」索引（主键首列是 BuhinName，按部品CD 查的时候用这个索引）。</summary>
        private const string CreateBuhinCdIndexSql = "CREATE INDEX IF NOT EXISTS [" + BuhinCdIndexName + "] ON [" +
            TableName + "] ([" + BuhinCdColumnName + "]);";

        /// <summary>升级两列主键时，老表临时改成的名字。</summary>
        private const string LegacyTableName = TableName + "_legacy";

        private readonly string _databasePath;
        private readonly SQLiteConnection _connection;

        /// <summary>打开（不存在时创建）本机库并确保 <see cref="TableName"/> 存在。</summary>
        public LocalBuhinBuzaiMappingRepository()
            : this(null)
        {
        }

        /// <summary>同上；<paramref name="databasePath"/> 为空时用 <see cref="ResolveDatabasePath"/>。</summary>
        /// <param name="databasePath">SQLite 数据库文件完整路径（可为 <c>null</c> / 空）。</param>
        public LocalBuhinBuzaiMappingRepository(string databasePath)
        {
            _databasePath = string.IsNullOrWhiteSpace(databasePath)
                ? ResolveDatabasePath()
                : databasePath.Trim();

            string folder = Path.GetDirectoryName(_databasePath);

            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            SQLiteConnectionStringBuilder builder = new SQLiteConnectionStringBuilder();
            builder.DataSource = _databasePath;
            builder.FailIfMissing = false;

            _connection = new SQLiteConnection(builder.ToString());
            _connection.Open();

            // 和「部材部品」マスタ共用同一个库文件：别人正在写时等一会儿，不要立刻失败
            Execute("PRAGMA busy_timeout = 3000;");

            EnsureSchema();
        }

        /// <summary>数据库文件的完整路径。</summary>
        public string DatabasePath
        {
            get { return _databasePath; }
        }

        /// <summary>
        /// 本机库的位置：与「部材部品」マスタ同一个文件
        /// （<see cref="MemberPartRepository.ResolveDatabasePath"/>）。
        /// </summary>
        public static string ResolveDatabasePath()
        {
            return MemberPartRepository.ResolveDatabasePath();
        }

        /// <summary>表里的行数。</summary>
        public int Count()
        {
            using (SQLiteCommand command = new SQLiteCommand(
                "SELECT COUNT(*) FROM [" + TableName + "];", _connection))
            {
                object value = command.ExecuteScalar();

                return value == null || value == DBNull.Value
                    ? 0
                    : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// 读出整张表（部品名 / 部材名 为空的行跳过）。
        /// 同一个部品CD / 部品名 可以对应多个部材名，全部原样返回。
        /// </summary>
        public List<BuhinBuzaiMappingEntry> Query()
        {
            List<BuhinBuzaiMappingEntry> entries = new List<BuhinBuzaiMappingEntry>();

            using (SQLiteCommand command = new SQLiteCommand(SelectSql, _connection))
            {
                using (SQLiteDataReader reader = command.ExecuteReader())
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
        /// 用 <paramref name="entries"/> <b>整表替换</b>本机表（一个事务里 DELETE + INSERT）。
        /// 服务器整表读成功时调用：服务器是准，服务器上删掉的行本机也删掉。
        /// </summary>
        /// <param name="entries">要写入的行（<c>null</c> / 空集合时只清空表）。</param>
        /// <returns>实际写入的行数。</returns>
        public int ReplaceAll(IEnumerable<BuhinBuzaiMappingEntry> entries)
        {
            DateTime updatedAt = DateTime.Now;
            int written = 0;

            using (SQLiteTransaction transaction = _connection.BeginTransaction())
            {
                Execute(DeleteAllSql, transaction);

                if (entries != null)
                {
                    string updatedAtText = FormatTimestamp(updatedAt);

                    foreach (BuhinBuzaiMappingEntry entry in entries)
                    {
                        string buhinName;
                        string buzaiName;
                        string buhinCd;

                        if (!TryBuildValues(entry, out buhinName, out buzaiName, out buhinCd))
                        {
                            continue;
                        }

                        ExecuteWrite(InsertSql, transaction, buhinName, buzaiName, buhinCd, updatedAtText);
                        written++;
                    }
                }

                transaction.Commit();
            }

            return written;
        }

        /// <summary>
        /// 登录 / 更新一批行（一个事务）：先按主键三列 <c>BuhinName</c> + <c>BuzaiName</c> +
        /// <c>BuhinCd</c> 更新（只刷新 <c>UpdatedAt</c>），更新 0 行时才 INSERT —— 与服务器的写法一致。
        /// </summary>
        /// <param name="entries">要写的行（<c>null</c> / 空集合时什么都不做）。</param>
        /// <returns>写入的行数（更新 + 新增）。</returns>
        public int Upsert(IEnumerable<BuhinBuzaiMappingEntry> entries)
        {
            if (entries == null)
            {
                return 0;
            }

            DateTime updatedAt = DateTime.Now;
            int written = 0;
            HashSet<string> writtenKeys = new HashSet<string>(StringComparer.Ordinal);
            string updatedAtText = FormatTimestamp(updatedAt);

            using (SQLiteTransaction transaction = _connection.BeginTransaction())
            {
                foreach (BuhinBuzaiMappingEntry entry in entries)
                {
                    string buhinName;
                    string buzaiName;
                    string buhinCd;

                    if (!TryBuildValues(entry, out buhinName, out buzaiName, out buhinCd))
                    {
                        continue;
                    }

                    // 同一次保存里主键完全相同的行只写一次（\u0001 分隔：这些值本身不会含这个字符）
                    string key = buhinName + "\u0001" + buzaiName + "\u0001" + buhinCd;

                    if (!writtenKeys.Add(key))
                    {
                        continue;
                    }

                    if (ExecuteWrite(UpdateSql, transaction, buhinName, buzaiName, buhinCd, updatedAtText) > 0)
                    {
                        written++;
                        continue;
                    }

                    written += ExecuteWrite(InsertSql, transaction, buhinName, buzaiName, buhinCd, updatedAtText);
                }

                transaction.Commit();
            }

            return written;
        }

        /// <summary>
        /// <see cref="ReplaceAll"/> 的「不抛异常」版：只返回原因文字（成功时为<b>空串</b>）。
        /// 给 <see cref="BuhinBuzaiMappingCatalog"/> 整表读服务器成功后镜像本机用。
        /// </summary>
        /// <param name="entries">要写入的行。</param>
        public static string TryReplaceAll(IEnumerable<BuhinBuzaiMappingEntry> entries)
        {
            try
            {
                using (LocalBuhinBuzaiMappingRepository repository = new LocalBuhinBuzaiMappingRepository())
                {
                    repository.ReplaceAll(entries);
                }

                return string.Empty;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary><see cref="Upsert"/> 的「不抛异常」版：只返回原因文字（成功时为<b>空串</b>）。</summary>
        /// <param name="entries">要写的行。</param>
        public static string TryUpsert(IEnumerable<BuhinBuzaiMappingEntry> entries)
        {
            try
            {
                using (LocalBuhinBuzaiMappingRepository repository = new LocalBuhinBuzaiMappingRepository())
                {
                    repository.Upsert(entries);
                }

                return string.Empty;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>建表（已存在则跳过；老的两列主键会自动升级）并创建「部品CD」索引。</summary>
        private void EnsureSchema()
        {
            using (SQLiteTransaction transaction = _connection.BeginTransaction())
            {
                Execute(CreateTableSql, transaction);

                // 老版本的本机库是两列主键（BuhinName + BuzaiName）：升级成三列（+ BuhinCd）
                if (CountPrimaryKeyColumns(transaction) < 3)
                {
                    UpgradeFromTwoColumnKey(transaction);
                }

                Execute(CreateBuhinCdIndexSql, transaction);
                transaction.Commit();
            }
        }

        /// <summary>
        /// 数一下本机表的主键有几列（<c>PRAGMA table_info</c> 的第 6 列 = 主键里的顺序，0 = 不是主键）。
        /// </summary>
        /// <param name="transaction">本次的事务（建表也在里面，看到的是同一份结构）。</param>
        private int CountPrimaryKeyColumns(SQLiteTransaction transaction)
        {
            int count = 0;

            using (SQLiteCommand command = new SQLiteCommand(
                "PRAGMA table_info(\"" + TableName + "\");", _connection, transaction))
            {
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (reader.IsDBNull(5))
                        {
                            continue;
                        }

                        if (Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture) > 0)
                        {
                            count++;
                        }
                    }
                }
            }

            return count;
        }

        /// <summary>
        /// 把老本机库的两列主键表（<c>BuhinName</c> + <c>BuzaiName</c>）升级成三列主键（+ <c>BuhinCd</c>）：
        /// 老表改名 → 建新表 → 数据搬过去（<c>NULL</c> 的部品CD 变空串）→ 删老表。
        /// 老表的主键本身没有重复，所以搬过去不会撞主键。
        /// </summary>
        /// <param name="transaction">本次的事务。</param>
        private void UpgradeFromTwoColumnKey(SQLiteTransaction transaction)
        {
            // 上一次升级中途失败留下的临时表先清掉（服务器那份是准的，下次整表镜像会补回来）
            Execute("DROP TABLE IF EXISTS [" + LegacyTableName + "];", transaction);
            Execute("ALTER TABLE [" + TableName + "] RENAME TO [" + LegacyTableName + "];", transaction);
            Execute(CreateTableSql, transaction);

            // 老结构允许 BuhinCd 为 NULL，新结构是主键的一部分（NOT NULL）：统一成空串
            Execute(
                "INSERT OR REPLACE INTO [" + TableName + "] ([" + BuhinNameColumnName + "], [" +
                BuzaiNameColumnName + "], [" + BuhinCdColumnName + "], [" + UpdatedAtColumnName + "]) " +
                "SELECT [" + BuhinNameColumnName + "], [" + BuzaiNameColumnName + "], IFNULL([" +
                BuhinCdColumnName + "], ''), [" + UpdatedAtColumnName + "] FROM [" + LegacyTableName + "];",
                transaction);

            Execute("DROP TABLE [" + LegacyTableName + "];", transaction);
        }

        /// <summary>执行一条不返回结果的 SQL（自动提交）。</summary>
        private void Execute(string sql)
        {
            using (SQLiteCommand command = new SQLiteCommand(sql, _connection))
            {
                command.ExecuteNonQuery();
            }
        }

        /// <summary>执行一条不返回结果的 SQL（在给定事务里）。</summary>
        private void Execute(string sql, SQLiteTransaction transaction)
        {
            using (SQLiteCommand command = new SQLiteCommand(sql, _connection, transaction))
            {
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 在事务里执行一条写库 SQL（每次新建一个命令，参数用 <c>AddWithValue</c>：
        /// 与 <see cref="MemberPartRepository"/> 的写法一致）。
        /// </summary>
        /// <returns>受影响的行数。</returns>
        private int ExecuteWrite(string sql, SQLiteTransaction transaction,
            string buhinName, string buzaiName, string buhinCd, string updatedAt)
        {
            using (SQLiteCommand command = new SQLiteCommand(sql, _connection, transaction))
            {
                command.Parameters.AddWithValue("@BuhinName", buhinName);
                command.Parameters.AddWithValue("@BuzaiName", buzaiName);
                command.Parameters.AddWithValue("@BuhinCd", buhinCd);
                command.Parameters.AddWithValue("@UpdatedAt", updatedAt);

                return command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 取出一行要写的值（去空白 + 截断）；部品名 / 部材名 为空时返回 false
        /// （两者都是主键的一部分，服务器那张表也是 NOT NULL；部品CD 没填时为空串）。
        /// </summary>
        /// <param name="entry">要写的行（可为 <c>null</c>）。</param>
        /// <param name="buhinName">部品名（去空白 / 截断后）。</param>
        /// <param name="buzaiName">部材名（去空白 / 截断后）。</param>
        /// <param name="buhinCd">部品CD（去空白 / 截断后，可为空串）。</param>
        private static bool TryBuildValues(BuhinBuzaiMappingEntry entry,
            out string buhinName, out string buzaiName, out string buhinCd)
        {
            buhinName = string.Empty;
            buzaiName = string.Empty;
            buhinCd = string.Empty;

            if (entry == null)
            {
                return false;
            }

            buhinName = Normalize(entry.BuhinName, NameMaxLength);
            buzaiName = Normalize(entry.BuzaiName, NameMaxLength);
            buhinCd = Normalize(entry.BuhinCd, NameMaxLength);

            return buhinName.Length > 0 && buzaiName.Length > 0;
        }

        /// <summary>把更新日時格式化成字符串（<c>yyyy-MM-dd HH:mm:ss</c>，与服务器的写法一致）。</summary>
        private static string FormatTimestamp(DateTime value)
        {
            return value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        /// <summary>取一列的文本（<c>null</c> / <c>NULL</c> → 空串，去首尾空白）。</summary>
        private static string GetText(SQLiteDataReader reader, int index)
        {
            return reader.IsDBNull(index) ? string.Empty : (reader.GetValue(index) ?? string.Empty).ToString().Trim();
        }

        /// <summary>去空白 + 按列长截断（与服务器那边的规则一致）。</summary>
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
            if (_connection != null)
            {
                _connection.Dispose();
            }
        }
    }
}
