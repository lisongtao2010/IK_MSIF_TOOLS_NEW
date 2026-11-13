using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// 部材名置换表（SQL Server 的 <c>DL_BuhinBuzaiMapping</c>）的连接信息。
    ///
    /// <para>
    /// 取值优先级与 AI 设置一致：环境变量 → 连接信息维护画面保存的用户配置
    /// （<c>%APPDATA%\MSIFAITOOLS\mapping-db-settings.json</c>）→ App.config 的 appSettings → 内置默认值。
    /// </para>
    ///
    /// <para>
    /// 维护画面是 <see cref="MemberMappingDbSettingsForm"/>；画面里点【测试连接】会真的连一次数据库。
    /// 密码在用户配置里用 DPAPI（当前用户）加密保存，App.config / 环境变量里可以写明文。
    /// </para>
    /// </summary>
    internal sealed class MemberMappingDbSettings
    {
        /// <summary>默认接続先（现场 SQL Server）。</summary>
        public const string DefaultServer = "10.103.126.209";

        /// <summary>默认端口。</summary>
        public const int DefaultPort = 1433;

        /// <summary>默认 DB 名。</summary>
        public const string DefaultDatabase = "MSIF";

        /// <summary>默认登录 ID。</summary>
        public const string DefaultUserId = "sqluser_dairen";

        /// <summary>默认密码。</summary>
        public const string DefaultPassword = "dairen1112##";

        /// <summary>App.config 中的接続先键名。</summary>
        public const string ServerSettingKey = "MappingDbServer";

        /// <summary>App.config 中的端口键名。</summary>
        public const string PortSettingKey = "MappingDbPort";

        /// <summary>App.config 中的 DB 名键名。</summary>
        public const string DatabaseSettingKey = "MappingDbDatabase";

        /// <summary>App.config 中的登录 ID 键名。</summary>
        public const string UserSettingKey = "MappingDbUser";

        /// <summary>App.config 中的密码键名。</summary>
        public const string PasswordSettingKey = "MappingDbPassword";

        /// <summary>覆盖接続先的环境变量。</summary>
        public const string ServerEnvironmentKey = "MSIF_MAPPING_DB_SERVER";

        /// <summary>覆盖端口的环境变量。</summary>
        public const string PortEnvironmentKey = "MSIF_MAPPING_DB_PORT";

        /// <summary>覆盖 DB 名的环境变量。</summary>
        public const string DatabaseEnvironmentKey = "MSIF_MAPPING_DB_DATABASE";

        /// <summary>覆盖登录 ID 的环境变量。</summary>
        public const string UserEnvironmentKey = "MSIF_MAPPING_DB_USER";

        /// <summary>覆盖密码的环境变量。</summary>
        public const string PasswordEnvironmentKey = "MSIF_MAPPING_DB_PASSWORD";

        /// <summary>配置来源：环境变量。</summary>
        public const string SourceEnvironment = "环境变量";

        /// <summary>配置来源：连接信息维护画面保存的用户配置。</summary>
        public const string SourceUserSettings = "连接信息维护画面";

        /// <summary>配置来源：App.config。</summary>
        public const string SourceAppConfig = "App.config";

        /// <summary>配置来源：内置默认值。</summary>
        public const string SourceDefault = "内置默认值";

        /// <summary>连接超时（秒）：连不上时不要让画面卡太久。</summary>
        public const int ConnectTimeoutSeconds = 15;

        /// <summary>
        /// 本次连接实际用的超时秒数：默认 <see cref="ConnectTimeoutSeconds"/>，
        /// 交互画面（例：「选择部材名」窗口按部品名 查置换表）可以传更短的值，连不上时少卡一会儿。
        /// </summary>
        private readonly int _connectTimeoutSeconds;

        public MemberMappingDbSettings(string server, int port, string database, string userId,
            string password, string source = SourceDefault, int connectTimeoutSeconds = ConnectTimeoutSeconds)
        {
            Server = string.IsNullOrWhiteSpace(server) ? DefaultServer : server.Trim();
            Port = port <= 0 ? DefaultPort : port;
            Database = string.IsNullOrWhiteSpace(database) ? DefaultDatabase : database.Trim();
            UserId = string.IsNullOrWhiteSpace(userId) ? DefaultUserId : userId.Trim();
            Password = password ?? string.Empty;
            ConfigurationSource = string.IsNullOrWhiteSpace(source) ? SourceDefault : source;
            _connectTimeoutSeconds = connectTimeoutSeconds <= 0 ? ConnectTimeoutSeconds : connectTimeoutSeconds;
        }

        /// <summary>接続先（服务器名或 IP）。</summary>
        public string Server { get; private set; }

        /// <summary>端口（默认 1433）。</summary>
        public int Port { get; private set; }

        /// <summary>DB 名（默认 MSIF）。</summary>
        public string Database { get; private set; }

        /// <summary>登录 ID。</summary>
        public string UserId { get; private set; }

        /// <summary>密码（内存里是明文）。</summary>
        public string Password { get; private set; }

        /// <summary>本次配置的实际来源（环境变量 / 连接信息维护画面 / App.config / 内置默认值）。</summary>
        public string ConfigurationSource { get; private set; }

        /// <summary>是否已配置到可以连接的程度（接続先与 DB 名都有值）。</summary>
        public bool IsConfigured
        {
            get { return Server.Length > 0 && Database.Length > 0; }
        }

        /// <summary>
        /// <c>SqlConnection</c> 用的 DataSource：<c>接続先,端口</c>。
        /// 接続先里已经写了 <c>,端口</c> 或具名实例 <c>服务器\实例</c> 时按用户写的为准。
        /// </summary>
        public string DataSource
        {
            get
            {
                string server = Server.Trim();

                if (Port <= 0 || server.IndexOf(',') >= 0 || server.IndexOf('\\') >= 0)
                {
                    return server;
                }

                return server + "," + Port.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>拼 <c>SqlConnection</c> 用的连接字符串（含超时 / Application Name，便于在服务器端排查）。</summary>
        public string BuildConnectionString()
        {
            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder();

            builder.DataSource = DataSource;
            builder.InitialCatalog = Database;
            builder.UserID = UserId;
            builder.Password = Password;
            builder.ConnectTimeout = _connectTimeoutSeconds;
            builder.ApplicationName = "MSIFAITOOLS";

            return builder.ConnectionString;
        }

        /// <summary>
        /// 画面 / 日志用的目标描述（不含密码）：<c>SQL Server 10.103.126.209,1433 / DB MSIF</c>。
        /// </summary>
        public string DescribeTarget()
        {
            return "SQL Server " + DataSource + " / DB " + Database;
        }

        /// <summary>日志用的配置摘要（不含密码）。</summary>
        public string Describe()
        {
            return DescribeTarget() + " / 登录 " + UserId + "（来源：" + ConfigurationSource + "）";
        }

        /// <summary>
        /// 校验输入（维护画面保存 / 测试连接前调用）。不通过时 <paramref name="error"/> 为提示文字。
        /// </summary>
        public bool Validate(out string error)
        {
            error = string.Empty;

            if (Server.Trim().Length == 0)
            {
                error = "请填写接続先（服务器名或 IP）。";
                return false;
            }

            if (Port < 1 || Port > 65535)
            {
                error = "端口要填 1 ～ 65535 之间的数字（SQL Server 一般是 1433）。";
                return false;
            }

            if (Database.Trim().Length == 0)
            {
                error = "请填写 DB 名（本工具用 MSIF）。";
                return false;
            }

            if (UserId.Trim().Length == 0)
            {
                error = "请填写登录 ID。";
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// 连接信息的用户级持久化（<c>%APPDATA%\MSIFAITOOLS\mapping-db-settings.json</c>）。
    /// 密码用 DPAPI（当前用户）加密后写入；读到明文（例如手工编辑过）时按原样使用。
    /// </summary>
    internal static class MemberMappingDbSettingsStore
    {
        /// <summary>用户配置文件夹名（与 AI 设置同一个文件夹）。</summary>
        public const string FolderName = "MSIFAITOOLS";

        /// <summary>用户配置文件名。</summary>
        public const string FileName = "mapping-db-settings.json";

        /// <summary>加密值前缀（用于与明文区分）。</summary>
        private const string EncryptedPrefix = "dpapi:";

        /// <summary>DPAPI 附加熵。</summary>
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("MSIFAITOOLS.MemberMappingDbSettings.v1");

        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        /// <summary>上一次读取用户配置失败的原因（成功 / 文件不存在时为空串）。</summary>
        public static string LastSettingsError { get; private set; }

        /// <summary>用户配置文件夹（%APPDATA%\MSIFAITOOLS）。</summary>
        public static string SettingsFolderPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);
            }
        }

        /// <summary>用户配置文件完整路径。</summary>
        public static string SettingsFilePath
        {
            get { return Path.Combine(SettingsFolderPath, FileName); }
        }

        /// <summary>配置文件是否已存在（= 画面里保存过连接信息）。</summary>
        public static bool Exists
        {
            get { return File.Exists(SettingsFilePath); }
        }

        /// <summary>
        /// 读取用户配置。文件不存在时返回 <c>null</c> 且 <paramref name="error"/> 为空；解析失败时给出原因。
        /// </summary>
        public static MemberMappingDbSettings Load(out string error)
        {
            error = string.Empty;
            string path = SettingsFilePath;

            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                Dictionary<string, object> data = Serializer.Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(path, Encoding.UTF8));

                if (data == null)
                {
                    return null;
                }

                return new MemberMappingDbSettings(
                    GetText(data, "Server"),
                    GetInt(data, "Port"),
                    GetText(data, "Database"),
                    GetText(data, "UserId"),
                    Unprotect(GetText(data, "Password")),
                    MemberMappingDbSettings.SourceUserSettings);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>保存用户配置（文件夹不存在时自动创建）。失败时返回 false 并给出原因。</summary>
        public static bool Save(MemberMappingDbSettings settings, out string error)
        {
            error = string.Empty;

            if (settings == null)
            {
                error = "连接信息不能为空。";
                return false;
            }

            try
            {
                Directory.CreateDirectory(SettingsFolderPath);

                Dictionary<string, object> data = new Dictionary<string, object>();
                data["Server"] = settings.Server ?? string.Empty;
                data["Port"] = settings.Port;
                data["Database"] = settings.Database ?? string.Empty;
                data["UserId"] = settings.UserId ?? string.Empty;
                data["Password"] = Protect(settings.Password ?? string.Empty);

                File.WriteAllText(SettingsFilePath, Serializer.Serialize(data), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 解析当前生效的连接信息：环境变量 → 维护画面保存的用户配置 → App.config 的 appSettings → 内置默认值。
        /// 读取用户配置失败只记 <see cref="LastSettingsError"/>，不影响连接（继续用下一级配置）。
        /// </summary>
        public static MemberMappingDbSettings Resolve()
        {
            string settingsError;
            MemberMappingDbSettings userSettings = Load(out settingsError);
            LastSettingsError = settingsError;

            string source;
            string server = ReadSetting(MemberMappingDbSettings.ServerEnvironmentKey,
                MemberMappingDbSettings.ServerSettingKey, MemberMappingDbSettings.DefaultServer,
                userSettings == null ? null : userSettings.Server, out source);

            // 以接続先的来源代表整体配置来源
            string overallSource = source;

            string portText = ReadSetting(MemberMappingDbSettings.PortEnvironmentKey,
                MemberMappingDbSettings.PortSettingKey,
                MemberMappingDbSettings.DefaultPort.ToString(CultureInfo.InvariantCulture),
                userSettings == null || userSettings.Port <= 0
                    ? null
                    : userSettings.Port.ToString(CultureInfo.InvariantCulture),
                out source);

            int port = MemberMappingDbSettings.DefaultPort;
            int parsedPort;

            if (!string.IsNullOrWhiteSpace(portText) &&
                int.TryParse(portText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out parsedPort) && parsedPort > 0 && parsedPort <= 65535)
            {
                port = parsedPort;
            }

            string database = ReadSetting(MemberMappingDbSettings.DatabaseEnvironmentKey,
                MemberMappingDbSettings.DatabaseSettingKey, MemberMappingDbSettings.DefaultDatabase,
                userSettings == null ? null : userSettings.Database, out source);

            string userId = ReadSetting(MemberMappingDbSettings.UserEnvironmentKey,
                MemberMappingDbSettings.UserSettingKey, MemberMappingDbSettings.DefaultUserId,
                userSettings == null ? null : userSettings.UserId, out source);

            string password = ReadSetting(MemberMappingDbSettings.PasswordEnvironmentKey,
                MemberMappingDbSettings.PasswordSettingKey, MemberMappingDbSettings.DefaultPassword,
                userSettings == null ? null : userSettings.Password, out source);

            return new MemberMappingDbSettings(server, port, database, userId, password, overallSource);
        }

        /// <summary>
        /// 按“环境变量 → 用户设置文件 → App.config → 默认值”的顺序取值，并记录实际来源。
        /// </summary>
        private static string ReadSetting(string environmentKey, string settingKey, string defaultValue,
            string userSettingValue, out string source)
        {
            string value = Environment.GetEnvironmentVariable(environmentKey);

            if (!string.IsNullOrWhiteSpace(value))
            {
                source = MemberMappingDbSettings.SourceEnvironment;
                return value.Trim();
            }

            if (!string.IsNullOrWhiteSpace(userSettingValue))
            {
                source = MemberMappingDbSettings.SourceUserSettings;
                return userSettingValue.Trim();
            }

            value = ReadAppSetting(settingKey);

            if (!string.IsNullOrWhiteSpace(value))
            {
                source = MemberMappingDbSettings.SourceAppConfig;
                return value.Trim();
            }

            source = MemberMappingDbSettings.SourceDefault;
            return defaultValue;
        }

        /// <summary>读 App.config 的 appSettings（配置系统坏掉时返回 null，退化为默认值）。</summary>
        private static string ReadAppSetting(string key)
        {
            try
            {
                return ConfigurationManager.AppSettings[key];
            }
            catch (ConfigurationErrorsException)
            {
                return null;
            }
        }

        private static string GetText(Dictionary<string, object> data, string key)
        {
            object value;

            if (!data.TryGetValue(key, out value) || value == null)
            {
                return string.Empty;
            }

            return Convert.ToString(value) ?? string.Empty;
        }

        private static int GetInt(Dictionary<string, object> data, string key)
        {
            object value;
            int parsed;

            if (!data.TryGetValue(key, out value) || value == null)
            {
                return 0;
            }

            return int.TryParse(Convert.ToString(value), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }

        private static string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
            {
                return string.Empty;
            }

            try
            {
                byte[] cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy,
                    DataProtectionScope.CurrentUser);
                return EncryptedPrefix + Convert.ToBase64String(cipher);
            }
            catch (Exception)
            {
                // DPAPI 不可用时退化为明文保存（文件仍位于当前用户的配置目录下）
                return plainText;
            }
        }

        private static string Unprotect(string storedText)
        {
            if (string.IsNullOrEmpty(storedText))
            {
                return string.Empty;
            }

            if (!storedText.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            {
                return storedText;
            }

            try
            {
                byte[] cipher = Convert.FromBase64String(storedText.Substring(EncryptedPrefix.Length));
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, Entropy,
                    DataProtectionScope.CurrentUser));
            }
            catch (Exception)
            {
                // 换了用户或机器时无法解密，视为未配置（会退回 App.config / 默认值）
                return string.Empty;
            }
        }
    }
}
