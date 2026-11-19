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
    /// 部材名置换表（SQL Server 的 <c>genpin.dbo.T_M_MergeName</c>）的连接信息。
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
        /// <summary>默认接続先（现场 SQL Server：genpin 库）。</summary>
        public const string DefaultServer = "10.103.126.208";

        /// <summary>默认端口。</summary>
        public const int DefaultPort = 1433;

        /// <summary>默认 DB 名。</summary>
        public const string DefaultDatabase = "genpin";

        /// <summary>默认登录 ID。</summary>
        public const string DefaultUserId = "sqluser_dairen";

        /// <summary>默认密码。</summary>
        public const string DefaultPassword = "dairen1111##";

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

        /// <summary>
        /// 具名连接：<c>connJP1</c>（10.103.126.208 / genpin）。
        /// 部材名置换表 <c>T_M_MergeName</c> 与【CMD 分析】第 6 步的 <c>T_M_PartsMaster</c> 用它
        /// （App.config 的 <c>&lt;connectionStrings&gt;</c>）。
        /// </summary>
        public const string JP1ConnectionName = "connJP1";

        /// <summary>
        /// 具名连接：<c>connJP2</c>（10.103.126.209 / MSIF）。
        /// 部材 mapping（<c>DL_BuhinBuzaiMapping</c>）用它 —— 部品基本信息画面点【保存】时
        /// 把「部品名 / 部材名 / 部品コード」登录 / 更新进该表（<see cref="BuhinBuzaiMappingRepository"/>）。
        /// App.config 的 <c>&lt;connectionStrings&gt;</c> 里必须启用这一行。
        /// </summary>
        public const string JP2ConnectionName = "connJP2";

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
            string password, string source = SourceDefault, int connectTimeoutSeconds = ConnectTimeoutSeconds,
            string connectionName = "")
        {
            Server = string.IsNullOrWhiteSpace(server) ? DefaultServer : server.Trim();
            Port = port <= 0 ? DefaultPort : port;
            Database = string.IsNullOrWhiteSpace(database) ? DefaultDatabase : database.Trim();
            UserId = string.IsNullOrWhiteSpace(userId) ? DefaultUserId : userId.Trim();
            Password = password ?? string.Empty;
            ConfigurationSource = string.IsNullOrWhiteSpace(source) ? SourceDefault : source;
            _connectTimeoutSeconds = connectTimeoutSeconds <= 0 ? ConnectTimeoutSeconds : connectTimeoutSeconds;
            ConnectionName = string.IsNullOrWhiteSpace(connectionName) ? string.Empty : connectionName.Trim();
        }

        /// <summary>接続先（服务器名或 IP）。</summary>
        public string Server { get; private set; }

        /// <summary>端口（默认 1433）。</summary>
        public int Port { get; private set; }

        /// <summary>DB 名（默认 genpin）。</summary>
        public string Database { get; private set; }

        /// <summary>登录 ID。</summary>
        public string UserId { get; private set; }

        /// <summary>密码（内存里是明文）。</summary>
        public string Password { get; private set; }

        /// <summary>本次配置的实际来源（环境变量 / 连接信息维护画面 / App.config / 内置默认值）。</summary>
        public string ConfigurationSource { get; private set; }

        /// <summary>
        /// 具名连接的名字（<c>connJP1</c> / <c>connJP2</c>，见 <see cref="JP1ConnectionName"/>）；
        /// 对不上 App.config 里的具名连接时为空串。日志 / 画面上写成 <c>connJP1</c>，
        /// 便于和 App.config 的 <c>&lt;connectionStrings&gt;</c> 对号。
        /// </summary>
        public string ConnectionName { get; private set; }

        /// <summary>
        /// 本次连接用的超时秒数（默认 <see cref="ConnectTimeoutSeconds"/>）。
        /// 复制连接信息（例：预读时用更短的超时）时把它带上，见 <see cref="MemberNameReplacementCatalog.Preload"/>。
        /// </summary>
        public int TimeoutSeconds
        {
            get { return _connectTimeoutSeconds; }
        }

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
        /// 画面 / 日志用的目标描述（不含密码）：<c>connJP1 = SQL Server 10.103.126.208,1433 / DB genpin</c>
        /// （对不上具名连接时只有 <c>SQL Server … / DB …</c> 这一段）。
        /// </summary>
        public string DescribeTarget()
        {
            string target = "SQL Server " + DataSource + " / DB " + Database;

            return ConnectionName.Length == 0 ? target : ConnectionName + " = " + target;
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
                error = "请填写 DB 名（本工具用 genpin）。";
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

        /// <summary>旧版本的默认接続先（genpin 迁移前：部材名置换表在 <c>MSIF</c> 库）。</summary>
        public const string LegacyDefaultServer = "10.103.126.209";

        /// <summary>旧版本的默认 DB 名。</summary>
        public const string LegacyDefaultDatabase = "MSIF";

        /// <summary>旧版本的默认密码。</summary>
        public const string LegacyDefaultPassword = "dairen1112##";

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
        /// 读取用户配置（= <c>connJP1</c>；旧版本的扁平键同样认）。
        /// 文件不存在 / 没有这个连接时返回 <c>null</c> 且 <paramref name="error"/> 为空；解析失败时给出原因。
        /// </summary>
        public static MemberMappingDbSettings Load(out string error)
        {
            return Load(MemberMappingDbSettings.JP1ConnectionName, out error);
        }

        /// <summary>
        /// 读取指定具名连接（<c>connJP1</c> / <c>connJP2</c>）的用户配置。
        /// <para>
        /// 配置文件里每个连接一节（见 <see cref="Save(MemberMappingDbSettings, string, out string)"/>）；
        /// 只有旧版本留下的扁平键（<c>Server</c> / <c>Port</c> / <c>Database</c> / …）时，把它当作 <c>connJP1</c>。
        /// </para>
        /// </summary>
        public static MemberMappingDbSettings Load(string connectionName, out string error)
        {
            error = string.Empty;
            string name = NormalizeConnectionName(connectionName);
            Dictionary<string, object> data = ReadSettingsFile(out error);

            if (data == null)
            {
                return null;
            }

            Dictionary<string, object> section = GetSection(data, name);

            if (section == null)
            {
                // 旧格式（整份文件没有具名分节）：就是 connJP1 那一套。
                // 已经有别的连接的分节时不再按扁平键猜，免得把别人的配置当成 connJP1。
                if (!IsJp1(name) || HasNamedSection(data))
                {
                    return null;
                }

                section = data;
            }

            MemberMappingDbSettings loaded = new MemberMappingDbSettings(
                GetText(section, "Server"),
                GetInt(section, "Port"),
                GetText(section, "Database"),
                GetText(section, "UserId"),
                Unprotect(GetText(section, "Password")),
                MemberMappingDbSettings.SourceUserSettings,
                MemberMappingDbSettings.ConnectTimeoutSeconds,
                name);

            // 旧版本点【保存】时留下的「整组都等于旧默认值」的配置（10.103.126.209 / MSIF / dairen1112##）
            // 会让程序一直连旧库（那里没有 T_M_MergeName）→ 当作未配置，改用新的内置默认值（genpin）。
            if (IsJp1(name) && IsLegacyDefault(loaded))
            {
                return null;
            }

            return loaded;
        }

        /// <summary>读配置文件内容；文件不存在时返回 <c>null</c>（<paramref name="error"/> 为空）。</summary>
        private static Dictionary<string, object> ReadSettingsFile(out string error)
        {
            error = string.Empty;

            if (!File.Exists(SettingsFilePath))
            {
                return null;
            }

            try
            {
                return Serializer.Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(SettingsFilePath, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>取配置文件里某个具名连接的那一节；没有 / 是空节时返回 <c>null</c>。</summary>
        private static Dictionary<string, object> GetSection(Dictionary<string, object> data, string name)
        {
            object raw;

            if (data == null || !data.TryGetValue(name, out raw) || raw == null)
            {
                return null;
            }

            Dictionary<string, object> section = raw as Dictionary<string, object>;

            return section == null || section.Count == 0 ? null : section;
        }

        /// <summary>配置文件里是否已经有具名连接的分节（旧格式升级后用来区分「扁平键」和「分节」）。</summary>
        private static bool HasNamedSection(Dictionary<string, object> data)
        {
            return GetSection(data, MemberMappingDbSettings.JP1ConnectionName) != null ||
                GetSection(data, MemberMappingDbSettings.JP2ConnectionName) != null;
        }

        /// <summary>连接名规范化：空 / 空白时当作 <c>connJP1</c>。</summary>
        private static string NormalizeConnectionName(string connectionName)
        {
            return string.IsNullOrWhiteSpace(connectionName)
                ? MemberMappingDbSettings.JP1ConnectionName
                : connectionName.Trim();
        }

        /// <summary>是否 <c>connJP1</c>（不区分大小写）。</summary>
        private static bool IsJp1(string connectionName)
        {
            return string.Equals(NormalizeConnectionName(connectionName),
                MemberMappingDbSettings.JP1ConnectionName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 是否是旧版本默认值那一组（genpin 迁移前的 <c>10.103.126.209 / MSIF / dairen1112##</c>）：
        /// 这种配置当作用户没改过 → 忽略，改用新的内置默认值。
        /// </summary>
        private static bool IsLegacyDefault(MemberMappingDbSettings settings)
        {
            if (settings == null)
            {
                return false;
            }

            return string.Equals(settings.Server, LegacyDefaultServer, StringComparison.OrdinalIgnoreCase) &&
                settings.Port == MemberMappingDbSettings.DefaultPort &&
                string.Equals(settings.Database, LegacyDefaultDatabase, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(settings.UserId, MemberMappingDbSettings.DefaultUserId,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(settings.Password, LegacyDefaultPassword, StringComparison.Ordinal);
        }

        /// <summary>保存用户配置（= <c>connJP1</c> 那一套）。失败时返回 false 并给出原因。</summary>
        public static bool Save(MemberMappingDbSettings settings, out string error)
        {
            return Save(settings, MemberMappingDbSettings.JP1ConnectionName, out error);
        }

        /// <summary>
        /// 保存指定具名连接（<c>connJP1</c> / <c>connJP2</c>）的用户配置（文件夹不存在时自动创建）。
        /// <para>
        /// 写入 <c>"connJP1"</c> / <c>"connJP2"</c> 分节；<c>connJP1</c> 同时写一份旧版本的扁平键，
        /// 这样只认扁平键的旧版本程序也能读到同样的接続先。另一个连接的分节原样保留。
        /// </para>
        /// 失败时返回 false 并给出原因。
        /// </summary>
        public static bool Save(MemberMappingDbSettings settings, string connectionName, out string error)
        {
            error = string.Empty;

            if (settings == null)
            {
                error = "连接信息不能为空。";
                return false;
            }

            string name = NormalizeConnectionName(connectionName);

            try
            {
                Directory.CreateDirectory(SettingsFolderPath);

                string readError;
                Dictionary<string, object> data = ReadSettingsFile(out readError) ??
                    new Dictionary<string, object>();

                Dictionary<string, object> section = new Dictionary<string, object>();
                section["Server"] = settings.Server ?? string.Empty;
                section["Port"] = settings.Port;
                section["Database"] = settings.Database ?? string.Empty;
                section["UserId"] = settings.UserId ?? string.Empty;
                section["Password"] = Protect(settings.Password ?? string.Empty);

                data[name] = section;

                if (IsJp1(name))
                {
                    // 兼容旧版本：扁平键 = connJP1
                    data["Server"] = section["Server"];
                    data["Port"] = section["Port"];
                    data["Database"] = section["Database"];
                    data["UserId"] = section["UserId"];
                    data["Password"] = section["Password"];
                }

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

            return new MemberMappingDbSettings(server, port, database, userId, password, overallSource,
                MemberMappingDbSettings.ConnectTimeoutSeconds, MatchConnectionName(server, database));
        }

        /// <summary>
        /// 按具名连接（<c>connJP1</c> / <c>connJP2</c>）解析连接信息。
        /// <para>优先级：环境变量（<c>MSIF_MAPPING_DB_*</c>）→ 本画面保存的该连接的用户配置
        /// → App.config 的具名连接（<paramref name="connectionName"/>）→ appSettings（<c>MappingDb*</c>）→ 内置默认值。</para>
        /// <para>
        /// 每个连接各自保存一份（配置文件里的 <c>connJP1</c> / <c>connJP2</c> 分节），互不影响：
        /// 改 ② MSIF 的接続先 / 密码不会把 ① genpin 置换表那套也改掉。
        /// 画面里改过连接（或设了环境变量）时以那些为准，这样现场换服务器 / 改密码不用改 App.config；
        /// 什么都没配时才用 App.config 里写的那一份。
        /// App.config 里没有这个名字（且画面也没保存过）时退回 <see cref="Resolve"/>，所以老配置照样能用。
        /// </para>
        /// </summary>
        /// <param name="connectionName">具名连接的名字（例：<see cref="MemberMappingDbSettings.JP1ConnectionName"/>）。</param>
        public static MemberMappingDbSettings ResolveByName(string connectionName)
        {
            string name = NormalizeConnectionName(connectionName);
            MemberMappingDbSettings appLevel = LoadNamedConnection(name);

            string settingsError;
            MemberMappingDbSettings userSettings = Load(name, out settingsError);
            LastSettingsError = settingsError;

            // 画面里保存过这个连接：以画面为准（环境变量可以逐项覆盖）
            if (userSettings != null)
            {
                return ApplyEnvironmentOverrides(userSettings, name);
            }

            // 没有画面设置 → 用具名连接，再让环境变量逐项覆盖
            if (appLevel != null)
            {
                return ApplyEnvironmentOverrides(appLevel, appLevel.ConnectionName);
            }

            return Resolve();
        }

        /// <summary>
        /// 部材名置换表 / 【CMD 分析】第 6 步 用的连接：<see cref="MemberMappingDbSettings.JP1ConnectionName"/>
        /// （<c>connJP1</c> = 10.103.126.208 / genpin）。没配具名连接时自动退回 <see cref="Resolve"/> 的结果。
        /// </summary>
        public static MemberMappingDbSettings ResolveJp1()
        {
            return ResolveByName(MemberMappingDbSettings.JP1ConnectionName);
        }

        /// <summary>
        /// 部材 mapping（<c>MSIF.dbo.DL_BuhinBuzaiMapping</c>）用的连接：
        /// <see cref="MemberMappingDbSettings.JP2ConnectionName"/>（<c>connJP2</c> = 10.103.126.209 / MSIF）。
        /// <para>
        /// 由 <see cref="BuhinBuzaiMappingRepository.Register"/> 在部品基本信息画面【保存】时调用。
        /// </para>
        /// <para>
        /// 它有自己的一份用户配置（主画面【接続② MSIF】维护，配置文件的 <c>connJP2</c> 分节），
        /// 和部材名置换表那套（<c>connJP1</c> / genpin）互不影响；
        /// 取值顺序同 <see cref="ResolveByName"/>：环境变量 <c>MSIF_MAPPING_DB_*</c> → 画面保存的 <c>connJP2</c>
        /// → App.config 的 <c>connJP2</c> → <see cref="Resolve"/>。
        /// </para>
        /// <para>
        /// App.config 里没有 <c>connJP2</c>、画面也没保存过时退回 <see cref="Resolve"/>；
        /// 那样会去 genpin 库找这张表 → 保存画面上出现「部材 mapping 登录失败」，
        /// 部品基本信息本身照样保存成功。
        /// </para>
        /// </summary>
        public static MemberMappingDbSettings ResolveJp2()
        {
            return ResolveByName(MemberMappingDbSettings.JP2ConnectionName);
        }

        /// <summary>
        /// 读 App.config 的 <c>&lt;connectionStrings&gt;</c> 里指定名字的连接
        /// （<c>Data Source=服务器,端口;Initial Catalog=库;User ID=…;Password=…</c>）。
        /// 没有这个名字 / 配置系统坏掉 / 连接字符串写错时返回 <c>null</c>（调用方退回原有解析）。
        /// </summary>
        private static MemberMappingDbSettings LoadNamedConnection(string name)
        {
            string connectionString;

            try
            {
                ConnectionStringSettings entry = ConfigurationManager.ConnectionStrings[name];
                connectionString = entry == null ? null : entry.ConnectionString;
            }
            catch (ConfigurationErrorsException)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return null;
            }

            try
            {
                SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder(connectionString.Trim());
                string server = builder.DataSource ?? string.Empty;
                int port = MemberMappingDbSettings.DefaultPort;
                int comma = server.LastIndexOf(',');

                // Data Source 里可以写「10.103.126.208,1433」：端口拆出来单独存，拼连接串时再合回去
                if (comma > 0)
                {
                    string portText = server.Substring(comma + 1).Trim();
                    int parsedPort;

                    if (int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedPort) &&
                        parsedPort > 0 && parsedPort <= 65535)
                    {
                        port = parsedPort;
                    }

                    server = server.Substring(0, comma).Trim();
                }

                return new MemberMappingDbSettings(server, port, builder.InitialCatalog, builder.UserID,
                    builder.Password, MemberMappingDbSettings.SourceAppConfig + "（" + name + "）",
                    MemberMappingDbSettings.ConnectTimeoutSeconds, name);
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        /// <summary>
        /// 按接続先 / DB 名 反查具名连接（<c>connJP1</c> / <c>connJP2</c>）；对不上时返回空串。
        /// 让不带名字的 <see cref="Resolve"/> 也能在日志里显示「connJP1 = …」。
        /// </summary>
        private static string MatchConnectionName(string server, string database)
        {
            string[] names = new string[]
            {
                MemberMappingDbSettings.JP1ConnectionName, MemberMappingDbSettings.JP2ConnectionName
            };

            string serverText = (server ?? string.Empty).Trim();
            string databaseText = (database ?? string.Empty).Trim();

            for (int index = 0; index < names.Length; index++)
            {
                MemberMappingDbSettings named = LoadNamedConnection(names[index]);

                if (named != null &&
                    string.Equals(named.Server, serverText, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(named.Database, databaseText, StringComparison.OrdinalIgnoreCase))
                {
                    return named.ConnectionName;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// 把环境变量（<c>MSIF_MAPPING_DB_*</c>）逐项覆盖到给定的连接信息上（没设的那项不动）。
        /// 现场应急时可以直接设环境变量改接続先 / 密码，不必改 App.config，也不用开画面。
        /// </summary>
        private static MemberMappingDbSettings ApplyEnvironmentOverrides(MemberMappingDbSettings settings,
            string connectionName)
        {
            bool overridden = false;
            string server = settings.Server;
            int port = settings.Port;
            string database = settings.Database;
            string userId = settings.UserId;
            string password = settings.Password;

            string value = Environment.GetEnvironmentVariable(MemberMappingDbSettings.ServerEnvironmentKey);

            if (!string.IsNullOrWhiteSpace(value))
            {
                server = value.Trim();
                overridden = true;
            }

            int parsedPort;
            value = Environment.GetEnvironmentVariable(MemberMappingDbSettings.PortEnvironmentKey);

            if (!string.IsNullOrWhiteSpace(value) &&
                int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedPort) &&
                parsedPort > 0 && parsedPort <= 65535)
            {
                port = parsedPort;
                overridden = true;
            }

            value = Environment.GetEnvironmentVariable(MemberMappingDbSettings.DatabaseEnvironmentKey);

            if (!string.IsNullOrWhiteSpace(value))
            {
                database = value.Trim();
                overridden = true;
            }

            value = Environment.GetEnvironmentVariable(MemberMappingDbSettings.UserEnvironmentKey);

            if (!string.IsNullOrWhiteSpace(value))
            {
                userId = value.Trim();
                overridden = true;
            }

            value = Environment.GetEnvironmentVariable(MemberMappingDbSettings.PasswordEnvironmentKey);

            if (!string.IsNullOrEmpty(value))
            {
                password = value;
                overridden = true;
            }

            if (!overridden)
            {
                return settings;
            }

            return new MemberMappingDbSettings(server, port, database, userId, password,
                MemberMappingDbSettings.SourceEnvironment, settings.TimeoutSeconds, connectionName);
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
