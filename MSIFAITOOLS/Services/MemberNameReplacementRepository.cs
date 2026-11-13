using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// 部材名置换表的 1 行（SQL Server 表 <c>DL_BuhinBuzaiMapping</c>）：
    /// <c>BuhinName</c>（部品名）= 图纸上的部材名（例：<c>鴨居</c>），
    /// <c>BuzaiName</c>（部材名）= 希望登录进部品基本信息表的名字（例：<c>上枠</c>）。
    /// </summary>
    internal sealed class MemberNameReplacement
    {
        public MemberNameReplacement(string source, string target)
            : this(source, target, string.Empty)
        {
        }

        public MemberNameReplacement(string source, string target, string note)
        {
            Source = (source ?? string.Empty).Trim();
            Target = (target ?? string.Empty).Trim();
            Note = (note ?? string.Empty).Trim();
        }

        /// <summary>部品名 = <c>BuhinName</c>（置换前；空值的行不会被采用）。</summary>
        public string Source { get; private set; }

        /// <summary>部材名 = <c>BuzaiName</c>（置换后；空值的行不会被采用）。</summary>
        public string Target { get; private set; }

        /// <summary>備考 = <c>Remarks</c>（可选）。</summary>
        public string Note { get; private set; }
    }

    /// <summary>
    /// 部材名置换表的 SQL Server 仓储（维护画面 <see cref="MemberNameReplacementForm"/> 用）。
    ///
    /// <para>
    /// <b>表</b>：<c>DL_BuhinBuzaiMapping</c>（<c>BuhinName</c> 部品名 / <c>BuzaiName</c> 部材名 /
    /// <c>Remarks</c> 備考 / <c>UpdatedAt</c> 更新日時；主键 = <c>(BuhinName, BuzaiName)</c>）。
    /// 连接信息由 <see cref="MemberMappingDbSettingsStore"/> 解析
    /// （维护画面 <see cref="MemberMappingDbSettingsForm"/>）。
    /// </para>
    ///
    /// <para>
    /// <b>保存规则</b>（<see cref="Save"/>）：按主键 <c>(BuhinName, BuzaiName)</c> 精确同步 ——
    /// 画面里删掉的行 → <c>DELETE</c>；新加的行 → <c>INSERT</c>；其它 → <c>UPDATE</c>（并刷新 <c>UpdatedAt</c>）。
    /// 所以 <c>Remarks</c>（備考）也能在画面上维护，且不会把别的行冲掉。
    /// </para>
    /// </summary>
    internal sealed class MemberNameReplacementRepository : IDisposable
    {
        /// <summary>表名。</summary>
        public const string TableName = "DL_BuhinBuzaiMapping";

        /// <summary>「部品名」列名（置换前 = 图纸上的部材名；主键之一）。</summary>
        public const string SourceColumnName = "BuhinName";

        /// <summary>「部材名」列名（置换后 = 登录进部品基本信息表的部材名；主键之一）。</summary>
        public const string TargetColumnName = "BuzaiName";

        /// <summary>「備考」列名（可选）。</summary>
        public const string NoteColumnName = "Remarks";

        /// <summary>「更新日時」列名（保存时自动刷新；DATETIME2）。</summary>
        public const string UpdatedAtColumnName = "UpdatedAt";

        /// <summary>部品名列的长度（对应 NVARCHAR(100)）。</summary>
        public const int SourceColumnMaxLength = 100;

        /// <summary>部材名列的长度（对应 NVARCHAR(100)）。</summary>
        public const int TargetColumnMaxLength = 100;

        /// <summary>備考列的长度（对应 NVARCHAR(255)）。</summary>
        public const int NoteColumnMaxLength = 255;

        /// <summary>读出全部行（按「部品名」+「部材名」排序）。</summary>
        private const string SelectSql =
            "SELECT [" + SourceColumnName + "], [" + TargetColumnName + "], [" + NoteColumnName +
            "] FROM [" + TableName + "] ORDER BY [" + SourceColumnName + "], [" + TargetColumnName + "];";

        private readonly MemberMappingDbSettings _settings;
        private readonly SqlConnection _connection;
        private readonly string _targetDescription;

        /// <summary>
        /// 用当前生效的连接信息连数据库（环境变量 → 维护画面保存的用户配置 → App.config → 内置默认值）。
        /// </summary>
        public MemberNameReplacementRepository()
            : this(null)
        {
        }

        /// <summary>
        /// 用指定的连接信息连数据库；<paramref name="settings"/> 为 <c>null</c> 时按
        /// <see cref="MemberMappingDbSettingsStore.Resolve"/> 解析。
        /// </summary>
        public MemberNameReplacementRepository(MemberMappingDbSettings settings)
        {
            _settings = settings ?? MemberMappingDbSettingsStore.Resolve();
            _targetDescription = _settings.DescribeTarget() + " / 表 " + TableName;

            _connection = new SqlConnection(_settings.BuildConnectionString());
            _connection.Open();
        }

        /// <summary>实际使用的连接目标（维护画面上显示给用户看）。</summary>
        public string TargetDescription
        {
            get { return _targetDescription; }
        }

        /// <summary>实际使用的连接信息（画面显示用）。</summary>
        public MemberMappingDbSettings Settings
        {
            get { return _settings; }
        }

        /// <summary>读出全部行（按「部品名」+「部材名」排序）。</summary>
        public IList<MemberNameReplacement> Query()
        {
            List<MemberNameReplacement> rows = new List<MemberNameReplacement>();

            using (SqlCommand command = new SqlCommand(SelectSql, _connection))
            using (SqlDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    rows.Add(new MemberNameReplacement(
                        reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                        reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                        reader.IsDBNull(2) ? string.Empty : reader.GetString(2)));
                }
            }

            return rows;
        }

        /// <summary>
        /// 按「部品名」（<c>BuhinName</c>）查对应的「部材名」（<c>BuzaiName</c>）一览
        /// （「选择部材名」窗口按部品名提示部材名 用；按 <c>BuzaiName</c> 排序，没有时返回空列表）。
        /// 主键是 <c>(BuhinName, BuzaiName)</c>，所以同一个部品名 可能对应多个部材名（全部返回）。
        /// </summary>
        /// <param name="source">部品名（精确匹配；前后空白会被去掉）。</param>
        public IList<string> QueryTargets(string source)
        {
            List<string> targets = new List<string>();
            string value = (source ?? string.Empty).Trim();

            if (value.Length == 0)
            {
                return targets;
            }

            const string sql =
                "SELECT [" + TargetColumnName + "] FROM [" + TableName + "] WHERE [" + SourceColumnName +
                "] = @source ORDER BY [" + TargetColumnName + "];";

            using (SqlCommand command = new SqlCommand(sql, _connection))
            {
                command.Parameters.Add("@source", SqlDbType.NVarChar, SourceColumnMaxLength).Value = value;

                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string target = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();

                        if (target.Length > 0 && !targets.Contains(target))
                        {
                            targets.Add(target);
                        }
                    }
                }
            }

            return targets;
        }

        /// <summary>
        /// 按主键 <c>(BuhinName, BuzaiName)</c> 精确同步（维护画面点【保存】时调用）：
        /// 库里有、这次没提交的键 → <c>DELETE</c>；提交的键库里没有 → <c>INSERT</c>；
        /// 其它 → <c>UPDATE</c>（同时刷新 <c>UpdatedAt</c>）。空「部品名」/ 空「部材名」的行跳过。
        /// </summary>
        public void Save(IList<MemberNameReplacement> rows)
        {
            using (SqlTransaction transaction = _connection.BeginTransaction())
            {
                HashSet<string> existingKeys = QueryKeys(transaction);
                HashSet<string> submittedKeys = new HashSet<string>(StringComparer.Ordinal);

                if (rows != null)
                {
                    foreach (MemberNameReplacement row in rows)
                    {
                        if (row != null && row.Source.Length > 0 && row.Target.Length > 0)
                        {
                            submittedKeys.Add(BuildKey(row.Source, row.Target));
                        }
                    }
                }

                DeleteMissingRows(transaction, existingKeys, submittedKeys);
                UpsertRows(transaction, rows, existingKeys);

                transaction.Commit();
            }
        }

        /// <summary>把 1 行的主键拼成 1 个字符串（<c>BuhinName</c> + 分隔符 + <c>BuzaiName</c>）。</summary>
        private static string BuildKey(string source, string target)
        {
            return (source ?? string.Empty) + "\u0001" + (target ?? string.Empty);
        }

        /// <summary>把主键字符串拆成 <c>@source</c> / <c>@target</c> 两个参数值。</summary>
        private static void SplitKey(string key, SqlParameter source, SqlParameter target)
        {
            int separator = key.IndexOf('\u0001');

            source.Value = separator < 0 ? key : key.Substring(0, separator);
            target.Value = separator < 0 ? string.Empty : key.Substring(separator + 1);
        }

        /// <summary>读出现有行的主键（判断「删除 / 更新 / 插入」用）。</summary>
        private HashSet<string> QueryKeys(SqlTransaction transaction)
        {
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);

            using (SqlCommand command = new SqlCommand(
                "SELECT [" + SourceColumnName + "], [" + TargetColumnName + "] FROM [" + TableName + "];",
                _connection, transaction))
            using (SqlDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    string source = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    string target = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);

                    keys.Add(BuildKey(source.Trim(), target.Trim()));
                }
            }

            return keys;
        }

        /// <summary>删除「库里有、这次提交里没有」的行（= 画面里删掉的行）。</summary>
        private void DeleteMissingRows(SqlTransaction transaction, HashSet<string> existingKeys,
            HashSet<string> submittedKeys)
        {
            using (SqlCommand command = new SqlCommand(
                "DELETE FROM [" + TableName + "] WHERE [" + SourceColumnName + "] = @source AND [" +
                TargetColumnName + "] = @target;",
                _connection, transaction))
            {
                SqlParameter source = command.Parameters.Add("@source", SqlDbType.NVarChar, SourceColumnMaxLength);
                SqlParameter target = command.Parameters.Add("@target", SqlDbType.NVarChar, TargetColumnMaxLength);

                foreach (string key in existingKeys)
                {
                    if (submittedKeys.Contains(key))
                    {
                        continue;
                    }

                    SplitKey(key, source, target);
                    command.ExecuteNonQuery();
                }
            }
        }

        /// <summary>更新 / 插入行（主键 <c>(BuhinName, BuzaiName)</c> 在库里存在 → UPDATE，否则 INSERT）。</summary>
        private void UpsertRows(SqlTransaction transaction, IList<MemberNameReplacement> rows,
            HashSet<string> existingKeys)
        {
            string updateSql = "UPDATE [" + TableName + "] SET [" + NoteColumnName + "] = @note, [" +
                UpdatedAtColumnName + "] = GETDATE() WHERE [" + SourceColumnName + "] = @source AND [" +
                TargetColumnName + "] = @target;";

            string insertSql = "INSERT INTO [" + TableName + "] ([" + SourceColumnName + "], [" +
                TargetColumnName + "], [" + NoteColumnName + "], [" + UpdatedAtColumnName +
                "]) VALUES (@source, @target, @note, GETDATE());";

            using (SqlCommand update = new SqlCommand(updateSql, _connection, transaction))
            using (SqlCommand insert = new SqlCommand(insertSql, _connection, transaction))
            {
                AddValueParameters(update);
                AddValueParameters(insert);

                if (rows == null)
                {
                    return;
                }

                foreach (MemberNameReplacement row in rows)
                {
                    if (row == null || row.Source.Length == 0 || row.Target.Length == 0)
                    {
                        continue;
                    }

                    SetValueParameters(row, update.Parameters);

                    if (existingKeys.Contains(BuildKey(row.Source, row.Target)) &&
                        update.ExecuteNonQuery() > 0)
                    {
                        continue;
                    }

                    SetValueParameters(row, insert.Parameters);
                    insert.ExecuteNonQuery();
                }
            }
        }

        /// <summary>加 @source / @target / @note 三个参数。</summary>
        private static void AddValueParameters(SqlCommand command)
        {
            command.Parameters.Add("@source", SqlDbType.NVarChar, SourceColumnMaxLength);
            command.Parameters.Add("@target", SqlDbType.NVarChar, TargetColumnMaxLength);
            command.Parameters.Add("@note", SqlDbType.NVarChar, NoteColumnMaxLength);
        }

        /// <summary>把 1 行写进参数（備考 为空时存 NULL）。</summary>
        private static void SetValueParameters(MemberNameReplacement row, SqlParameterCollection parameters)
        {
            parameters["@source"].Value = row.Source;
            parameters["@target"].Value = row.Target;
            parameters["@note"].Value = row.Note.Length == 0 ? (object)DBNull.Value : row.Note;
        }

        /// <summary>表里的行数（日志 / 画面提示用）。</summary>
        public int Count()
        {
            using (SqlCommand command = new SqlCommand("SELECT COUNT(*) FROM [" + TableName + "];", _connection))
            {
                object value = command.ExecuteScalar();

                return value == null || value == DBNull.Value
                    ? 0
                    : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>表是否存在于当前库（不存在时画面给出「请先建表」提示）。</summary>
        public bool TableExists()
        {
            using (SqlCommand command = new SqlCommand(
                "SELECT CASE WHEN OBJECT_ID(@name, N'U') IS NULL THEN 0 ELSE 1 END;", _connection))
            {
                command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = TableName;
                object value = command.ExecuteScalar();

                return value != null && value != DBNull.Value &&
                    Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
            }
        }

        /// <summary>关闭数据库连接。</summary>
        public void Dispose()
        {
            if (_connection != null)
            {
                _connection.Dispose();
            }
        }

        /// <summary>
        /// 用当前生效的连接信息描述目标（日志 / 画面用），
        /// 例：<c>SQL Server 10.103.126.209,1433 / DB MSIF / 表 DL_BuhinBuzaiMapping</c>。
        /// </summary>
        public static string DescribeTarget()
        {
            return ResolveTargetDescription(MemberMappingDbSettingsStore.Resolve());
        }

        /// <summary>用指定连接信息描述目标（画面用）。</summary>
        public static string ResolveTargetDescription(MemberMappingDbSettings settings)
        {
            MemberMappingDbSettings resolved = settings ?? MemberMappingDbSettingsStore.Resolve();

            return resolved.DescribeTarget() + " / 表 " + TableName;
        }

        /// <summary>
        /// 【测试连接】：真的连一次库，并检查表 <see cref="TableName"/> 是否存在
        /// （表不存在也算连上了，只是结果文字里提示建表）。
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

                    bool tableExists = false;
                    int rowCount = 0;

                    using (SqlCommand command = new SqlCommand(
                        "SELECT CASE WHEN OBJECT_ID(@name, N'U') IS NULL THEN 0 ELSE 1 END;", connection))
                    {
                        command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = TableName;
                        object value = command.ExecuteScalar();
                        tableExists = value != null && value != DBNull.Value &&
                            Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
                    }

                    if (tableExists)
                    {
                        using (SqlCommand command = new SqlCommand(
                            "SELECT COUNT(*) FROM [" + TableName + "];", connection))
                        {
                            object value = command.ExecuteScalar();
                            rowCount = value == null || value == DBNull.Value
                                ? 0
                                : Convert.ToInt32(value, CultureInfo.InvariantCulture);
                        }
                    }

                    message = tableExists
                        ? "连接成功：" + settings.DescribeTarget() + " / 表 " + TableName +
                          "（" + rowCount + " 条）"
                        : "连接成功，但库 " + settings.Database + " 里没有表 " + TableName +
                          "。请先用建表 SQL 创建该表。";

                    return true;
                }
            }
            catch (Exception ex)
            {
                message = "连接失败：" + ex.Message;
                return false;
            }
        }

    }
}
