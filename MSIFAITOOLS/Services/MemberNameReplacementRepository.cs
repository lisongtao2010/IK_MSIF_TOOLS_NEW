using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// 部材名置换表的 1 行（SQL Server 表 <c>genpin.dbo.T_M_MergeName</c>）：
    /// <c>MsifDevName</c>（图纸 / <c>parts_info.json</c> 上取得的词）= 置换前，
    /// <c>GenpinName</c>（要置换成的词）= 置换后。
    /// <c>Classification</c> 是分类，只用于画面显示，不参与匹配。
    /// </summary>
    internal sealed class MemberNameReplacement
    {
        public MemberNameReplacement(string source, string target)
            : this(source, target, string.Empty)
        {
        }

        public MemberNameReplacement(string source, string target, string classification)
        {
            Source = (source ?? string.Empty).Trim();
            Target = (target ?? string.Empty).Trim();
            Classification = (classification ?? string.Empty).Trim();
        }

        /// <summary>置换前 = <c>MsifDevName</c>（空值的行不会被采用）。</summary>
        public string Source { get; private set; }

        /// <summary>置换后 = <c>GenpinName</c>（空值的行不会被采用）。</summary>
        public string Target { get; private set; }

        /// <summary>分類 = <c>Classification</c>（画面显示用，不参与匹配）。</summary>
        public string Classification { get; private set; }

        /// <summary>兼容旧调用方（旧表的 <c>Remarks</c> 列）：值与 <see cref="Classification"/> 相同。</summary>
        public string Note
        {
            get { return Classification; }
        }
    }

    /// <summary>
    /// 部材名置换表的 SQL Server 仓储（画面 <see cref="MemberNameReplacementForm"/> 用）。
    ///
    /// <para>
    /// <b>表</b>：<c>genpin.dbo.T_M_MergeName</c>（<c>Classification</c> 分類 / <c>MsifDevName</c> 置换前 /
    /// <c>GenpinName</c> 置换后 / <c>DeleteFlg</c> 削除フラグ / <c>CreateBy</c>・<c>CreateTime</c> /
    /// <c>UpdateBy</c>・<c>UpdateTime</c>）。匹配条件 = <c>MsifDevName</c> → <c>GenpinName</c>，
    /// 只采用 <c>DeleteFlg = 0</c>（含 NULL）的行。连接信息由 <see cref="MemberMappingDbSettingsStore"/> 解析
    /// （画面 <see cref="MemberMappingDbSettingsForm"/>）。
    /// </para>
    ///
    /// <para>
    /// <b>只读</b>：这张表由 genpin 侧维护（有 <c>DeleteFlg</c> / <c>UpdateBy</c> 等管理列），
    /// 本工具只读不写 —— 画面里改不了，也没有保存处理。
    /// </para>
    /// </summary>
    internal sealed class MemberNameReplacementRepository : IDisposable
    {
        /// <summary>表名。</summary>
        public const string TableName = "T_M_MergeName";

        /// <summary>「分類」列名（画面显示用，不参与匹配）。</summary>
        public const string ClassificationColumnName = "Classification";

        /// <summary>「置换前」列名（图纸 / <c>parts_info.json</c> 上取得的词；匹配条件）。</summary>
        public const string SourceColumnName = "MsifDevName";

        /// <summary>「置换后」列名（要置换成的词）。</summary>
        public const string TargetColumnName = "GenpinName";

        /// <summary>「削除フラグ」列名（只采用 0 / false / NULL 的行）。</summary>
        public const string DeleteFlagColumnName = "DeleteFlg";

        /// <summary>查询参数的长度（<c>nvarchar(max)</c>）：表的列长未定，按不截断传值。</summary>
        private const int ParameterSize = -1;

        /// <summary>
        /// 读出全部行（按「分類」+「置换前」+「置换后」排序）。
        /// <c>DeleteFlg</c> 转成文字取回来，在 C# 侧过滤 —— bit / int / char 都能用。
        /// </summary>
        private const string SelectSql =
            "SELECT [" + ClassificationColumnName + "], [" + SourceColumnName + "], [" + TargetColumnName +
            "], CONVERT(NVARCHAR(20), [" + DeleteFlagColumnName + "]) FROM [" + TableName +
            "] ORDER BY [" + ClassificationColumnName + "], [" + SourceColumnName + "], [" +
            TargetColumnName + "];";

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
        /// <see cref="MemberMappingDbSettingsStore.ResolveJp1"/> 解析（<c>connJP1</c> / genpin）。
        /// </summary>
        public MemberNameReplacementRepository(MemberMappingDbSettings settings)
        {
            _settings = settings ?? MemberMappingDbSettingsStore.ResolveJp1();
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

        /// <summary>
        /// 读出全部有效行（<c>DeleteFlg = 0</c> / 未设置的行），已削除的行跳过。
        /// </summary>
        public IList<MemberNameReplacement> Query()
        {
            List<MemberNameReplacement> rows = new List<MemberNameReplacement>();

            using (SqlCommand command = new SqlCommand(SelectSql, _connection))
            using (SqlDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (IsDeleted(GetText(reader, 3)))
                    {
                        continue;
                    }

                    rows.Add(new MemberNameReplacement(
                        GetText(reader, 1), GetText(reader, 2), GetText(reader, 0)));
                }
            }

            return rows;
        }

        /// <summary>
        /// 按「置换前」（<c>MsifDevName</c>）查对应的「置换后」（<c>GenpinName</c>）一览
        /// （「选择部材名」窗口按部品名提示部材名 用；按 <c>GenpinName</c> 排序，没有时返回空列表）。
        /// 已削除（<c>DeleteFlg</c> 非 0）的行不算；同一个词可能对应多个置换后（全部返回、去重）。
        /// </summary>
        /// <param name="source">置换前（精确匹配；前后空白会被去掉）。</param>
        public IList<string> QueryTargets(string source)
        {
            List<string> targets = new List<string>();
            string value = (source ?? string.Empty).Trim();

            if (value.Length == 0)
            {
                return targets;
            }

            const string sql =
                "SELECT [" + TargetColumnName + "], CONVERT(NVARCHAR(20), [" + DeleteFlagColumnName +
                "]) FROM [" + TableName + "] WHERE [" + SourceColumnName + "] = @source ORDER BY [" +
                TargetColumnName + "];";

            using (SqlCommand command = new SqlCommand(sql, _connection))
            {
                command.Parameters.Add("@source", SqlDbType.NVarChar, ParameterSize).Value = value;

                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (IsDeleted(GetText(reader, 1)))
                        {
                            continue;
                        }

                        string target = GetText(reader, 0);

                        if (target.Length > 0 && !targets.Contains(target))
                        {
                            targets.Add(target);
                        }
                    }
                }
            }

            return targets;
        }

        /// <summary>取单元格文本（null / DBNull → 空串，去首尾空格）。</summary>
        private static string GetText(SqlDataReader reader, int index)
        {
            return reader.IsDBNull(index) ? string.Empty : (reader.GetValue(index) ?? string.Empty).ToString().Trim();
        }

        /// <summary>
        /// <c>DeleteFlg</c> 是否表示「已削除」：<c>0</c> / <c>false</c> / <c>F</c> / <c>N</c> / NULL = 未削除，
        /// 其它（<c>1</c> / <c>true</c> / <c>T</c> / <c>Y</c> / 其它削除代码）= 已削除（bit / int / char 都按文字判断）。
        /// </summary>
        private static bool IsDeleted(string value)
        {
            string text = (value ?? string.Empty).Trim();

            if (text.Length == 0)
            {
                return false;
            }

            return !string.Equals(text, "0", StringComparison.Ordinal) &&
                !string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(text, "f", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(text, "n", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>表里的有效行数（日志 / 画面提示用；已削除的行不算，与 <see cref="Query"/> 一致）。</summary>
        public int Count()
        {
            return Query().Count;
        }

        /// <summary>表是否存在于当前库（不存在时画面提示确认 DB 名 / 表名）。</summary>
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
        /// 例：<c>SQL Server 10.103.126.208,1433 / DB genpin / 表 T_M_MergeName</c>。
        /// </summary>
        public static string DescribeTarget()
        {
            return ResolveTargetDescription(MemberMappingDbSettingsStore.ResolveJp1());
        }

        /// <summary>用指定连接信息描述目标（画面用）。</summary>
        public static string ResolveTargetDescription(MemberMappingDbSettings settings)
        {
            MemberMappingDbSettings resolved = settings ?? MemberMappingDbSettingsStore.ResolveJp1();

            return resolved.DescribeTarget() + " / 表 " + TableName;
        }

        /// <summary>
        /// 【测试连接】：真的连一次库，并检查表 <see cref="TableName"/> 是否存在
        /// （表不存在也算连上了，只是结果文字里提示确认 DB 名）。
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
                        // 与 Query() 一致：只数有效行（DeleteFlg 为 0 / false / F / N / NULL）
                        using (SqlCommand command = new SqlCommand(
                            "SELECT COUNT(*) FROM [" + TableName + "] WHERE CONVERT(NVARCHAR(20), ISNULL([" +
                            DeleteFlagColumnName + "], 0)) NOT IN (N'1', N'true', N'True', N't', N'T', " +
                            "N'y', N'Y', N'-1');", connection))
                        {
                            object value = command.ExecuteScalar();
                            rowCount = value == null || value == DBNull.Value
                                ? 0
                                : Convert.ToInt32(value, CultureInfo.InvariantCulture);
                        }
                    }

                    message = tableExists
                        ? "连接成功：" + settings.DescribeTarget() + " / 表 " + TableName +
                          "（有效 " + rowCount + " 条）"
                        : "连接成功，但库 " + settings.Database + " 里没有表 " + TableName +
                          "。请确认 DB 名（本工具读 genpin 库）以及该表是否存在。";

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
