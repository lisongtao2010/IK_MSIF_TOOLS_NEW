using System;
using System.Collections.Generic;
using System.Text;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// 内存里的「部材名置换表」（<see cref="MemberNameReplacementRepository"/> 的只读缓存）。
    ///
    /// <para>
    /// 用途：图上 / <c>parts_info.json</c> 上取得的词不一定是想要的部材名（例：<c>鴨居</c> 要登录成 <c>上枠</c>），
    /// genpin 侧在 SQL Server 表 <c>genpin.dbo.T_M_MergeName</c> 里配好
    /// 「置换前（<c>MsifDevName</c>）→ 置换后（<c>GenpinName</c>）」后，
    /// 写进 <see cref="PartBasicInfo.MemberName"/> 的部材名会按这张表自动置换。
    /// </para>
    ///
    /// <para>
    /// <b>读取时机</b>：程序启动时（<c>Program.Main</c> → <see cref="Preload"/>）把整表读进内存，
    /// 之后置换不再连数据库；【部材名面板】的【重新载入】/【连接设置】保存后调 <see cref="Reload"/> 刷新。
    /// </para>
    ///
    /// <para>
    /// <b>置换规则</b>（<see cref="Replace"/>）：
    /// </para>
    /// <list type="bullet">
    ///   <item>按 <c>|</c> 拆开（<c>鴨居|上レール</c> 这种「分不清是哪张零件图」的值），逐段置换后再拼回去；</item>
    ///   <item>整段命中置换表 → 换成「置换后」；</item>
    ///   <item>带位置前缀时（<c>右鴨居</c> / <c>左鴨居</c> / <c>上鴨居</c> / <c>下鴨居</c>，
    ///         第 2 / 3 步按位置拆条时加的前缀）先剥掉前缀再查表，命中后把前缀原样加回
    ///         → <c>右上枠</c> / <c>左上枠</c>；</item>
    ///   <item>「その他」（<see cref="PartBasicInfo.MemberNameFallback"/>）与空值 <b>不置换</b>。</item>
    /// </list>
    ///
    /// <para>
    /// 置换表为空 / 数据库打不开时 <see cref="Replace"/> <b>原样返回</b>（不影响任何现有行为），
    /// 原因记在 <see cref="LastError"/>。
    /// </para>
    /// </summary>
    internal static class MemberNameReplacementCatalog
    {
        /// <summary>位置前缀（与按位置拆条时加的前缀一致：左右排列 → 右 / 左，上下排列 → 上 / 下）。</summary>
        private static readonly char[] PositionPrefixes = new char[] { '右', '左', '上', '下' };

        /// <summary>当前生效的置换表（键 = 置换前，Ordinal；不可变，整表替换）。</summary>
        private static Dictionary<string, string> _map = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// 是否已经读过数据库（程序启动时 <see cref="Preload"/>；没预读成功的话第一次 <see cref="Replace"/> 会再试一次）。
        /// </summary>
        private static bool _loaded;

        /// <summary>
        /// 当前使用的连接目标
        /// （例：<c>SQL Server 10.103.126.208,1433 / DB genpin / 表 T_M_MergeName</c>；解析失败时是解析出来的目标）。
        /// </summary>
        private static string _targetDescription = string.Empty;

        /// <summary>上一次读取数据库失败的原因（成功时为空串）。</summary>
        private static string _lastError = string.Empty;

        /// <summary>当前生效的置换条数。</summary>
        public static int Count
        {
            get
            {
                EnsureLoaded();

                return _map.Count;
            }
        }

        /// <summary>当前使用的连接目标（日志 / 维护画面显示用）。</summary>
        public static string TargetDescription
        {
            get
            {
                EnsureLoaded();

                return _targetDescription;
            }
        }

        /// <summary>兼容旧调用方：值与 <see cref="TargetDescription"/> 相同（已经不再是文件路径）。</summary>
        public static string DatabasePath
        {
            get { return TargetDescription; }
        }

        /// <summary>上一次读取数据库失败的原因（成功时为空串）。</summary>
        public static string LastError
        {
            get
            {
                EnsureLoaded();

                return _lastError;
            }
        }

        /// <summary>
        /// 程序启动时预读用的连接超时（秒）：数据库连不上时不要把启动卡太久。
        /// 预读失败的话，第一次置换时还会按默认超时（<see cref="MemberMappingDbSettings.ConnectTimeoutSeconds"/>）再试一次。
        /// </summary>
        public const int PreloadTimeoutSeconds = 3;

        /// <summary>
        /// 重新从数据库读一次（【部材名面板】的【重新载入】/【连接设置】保存后调用，保证内存里的规则是最新的）。
        /// </summary>
        public static void Reload()
        {
            _loaded = true;
            Load(null);
        }

        /// <summary>
        /// 程序启动时把整张置换表读进内存（<c>Program.Main</c> 调用）。
        /// 失败只记 <see cref="LastError"/>（置换表为空 → 原样返回），不影响启动；
        /// 启动时连不上的话保持「未加载」，第一次置换时用正常超时再试一次。
        /// </summary>
        public static void Preload()
        {
            _loaded = true;

            MemberMappingDbSettings resolved = MemberMappingDbSettingsStore.Resolve();
            MemberMappingDbSettings quick = new MemberMappingDbSettings(resolved.Server, resolved.Port,
                resolved.Database, resolved.UserId, resolved.Password, resolved.ConfigurationSource,
                PreloadTimeoutSeconds);

            if (!Load(quick))
            {
                _loaded = false;
            }
        }

        /// <summary>
        /// 按置换表替换一个「部材名」（可含 <c>|</c>，如 <c>鴨居|上レール</c>）；
        /// 置换表为空 / 没有命中 / 数据库打不开时原样返回。
        /// </summary>
        /// <param name="memberName">原来的部材名（可为 <c>null</c> / 空）。</param>
        public static string Replace(string memberName)
        {
            string value = memberName ?? string.Empty;

            if (value.Trim().Length == 0)
            {
                return value;
            }

            EnsureLoaded();

            Dictionary<string, string> map = _map;

            if (map.Count == 0)
            {
                return value;
            }

            if (value.IndexOf('|') < 0)
            {
                return ReplaceToken(map, value);
            }

            string[] tokens = value.Split('|');
            StringBuilder builder = new StringBuilder(value.Length + 8);

            for (int index = 0; index < tokens.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append('|');
                }

                builder.Append(ReplaceToken(map, tokens[index]));
            }

            return builder.ToString();
        }

        /// <summary>
        /// 读 SQL Server 表 <c>T_M_MergeName</c>（只取 <c>DeleteFlg = 0</c> 的行）。
        /// 失败只记 <see cref="LastError"/>，置换表置空 → 不影响原值。
        /// </summary>
        /// <param name="settings">
        /// 要用的连接信息；<c>null</c> = 按当前生效的配置（<see cref="MemberMappingDbSettingsStore.Resolve"/>）。
        /// </param>
        /// <returns>是否读成功（失败时 <see cref="LastError"/> 有原因）。</returns>
        private static bool Load(MemberMappingDbSettings settings)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
            string target = MemberNameReplacementRepository.ResolveTargetDescription(settings);
            string error = string.Empty;

            try
            {
                using (MemberNameReplacementRepository repository = new MemberNameReplacementRepository(settings))
                {
                    target = repository.TargetDescription;

                    foreach (MemberNameReplacement row in repository.Query())
                    {
                        if (row.Source.Length == 0 || row.Target.Length == 0)
                        {
                            continue;
                        }

                        if (map.ContainsKey(row.Source))
                        {
                            continue;
                        }

                        map[row.Source] = row.Target;
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            _targetDescription = target;
            _lastError = error;
            _map = map;

            return error.Length == 0;
        }

        /// <summary>第一次使用时加载（之后用缓存，直到 <see cref="Reload"/>）。</summary>
        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            Load(null);
        }

        /// <summary>
        /// 单段置换：整段命中 → 直接用；带位置前缀（右 / 左 / 上 / 下）时剥掉前缀再查，命中后加回前缀。
        /// </summary>
        private static string ReplaceToken(Dictionary<string, string> map, string token)
        {
            string value = (token ?? string.Empty).Trim();

            if (value.Length == 0 ||
                string.Equals(value, PartBasicInfo.MemberNameFallback, StringComparison.Ordinal))
            {
                return value;
            }

            string replaced;

            if (map.TryGetValue(value, out replaced))
            {
                return replaced;
            }

            if (value.Length > 1 && IsPositionPrefix(value[0]))
            {
                string rest = value.Substring(1).Trim();

                if (rest.Length > 0 && map.TryGetValue(rest, out replaced))
                {
                    return value[0] + replaced;
                }
            }

            return value;
        }

        /// <summary>是否是位置前缀（右 / 左 / 上 / 下）。</summary>
        private static bool IsPositionPrefix(char value)
        {
            for (int index = 0; index < PositionPrefixes.Length; index++)
            {
                if (PositionPrefixes[index] == value)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
