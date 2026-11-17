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
    /// 「置换前（<c>MsifDevName</c> = 图面上取得的词）→ 置换后（<c>GenpinName</c> = 要登录的词）」后，
    /// 写进 <see cref="PartBasicInfo.MemberName"/> 的部材名会按这张表自动置换。
    /// 连接用 App.config 的具名连接 <c>connJP1</c>（10.103.126.208 / genpin，
    /// <see cref="MemberMappingDbSettingsStore.ResolveJp1"/>）；没配时退回老的单连接配置。
    /// </para>
    ///
    /// <para>
    /// <b>读取时机</b>：程序启动时（<c>Program.Main</c> → <see cref="Preload"/>）把整表读进内存，
    /// 之后置换不再连数据库；主画面（<see cref="PartBasicInfoForm"/>）打开时再调一次
    /// <see cref="EnsurePreloaded"/>（启动时没读成功的话用短超时补读一次）；
    /// 【部材名面板】的【重新载入】/【连接设置】保存后调 <see cref="Reload"/> 刷新。
    /// <b>画面里查推荐项只走内存</b>（<see cref="GetDisplayNames"/>），不会因为连不上库把界面卡住。
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

        /// <summary>
        /// 当前生效的置换表（键 = 置换前经 <see cref="NormalizeLookupKey"/> 规整后的值，Ordinal；不可变，整表替换）。
        /// </summary>
        private static Dictionary<string, string> _map = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// 置换前 → 全部「置换后」（1:N，去重、按 <c>GenpinName</c> 排序；不可变，整表替换）。
        /// 键与 <see cref="_map"/> 一样是规整后的值。<see cref="GetDisplayNames"/> 用它找出
        /// 「这个写法属于哪个部材（原部材名）」，再从这个部材的全部表示名里取候补。
        /// </summary>
        private static Dictionary<string, List<string>> _targets =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        /// <summary>
        /// 置换后 → 全部「置换前」（1:N；键 = 规整后的 <c>GenpinName</c>，值 = 表里的 <c>MsifDevName</c> 原文，
        /// 去重、按文字排序；不可变，整表替换）。
        /// 「选择部材名」窗口的<b>表示部材名</b>候补就是从这份里取的
        /// （同一个部材在图面上有 <c>上ﾚｰﾙ</c> / <c>上ｱﾙﾐ</c> / <c>ｱﾙﾐ上</c> 等多种叫法）。
        /// </summary>
        private static Dictionary<string, List<string>> _sources =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        /// <summary>
        /// 置换前（规整后）→ 表里那一行的原文（<c>MsifDevName</c> 的写法；不可变，整表替换）。
        /// 例：<c>上レール</c> 查表时返回表里的 <c>上ﾚｰﾙ</c>。
        /// </summary>
        private static Dictionary<string, string> _sourceText =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>是否已经<b>成功</b>读过数据库（读进内存的是最新内容）。</summary>
        private static bool _loaded;

        /// <summary>
        /// 这一次运行里是否已经<b>尝试</b>过读数据库（成功与否都算）。
        /// 失败后不再自动重试（画面里点「部材名」不再因为连不上库而卡住），
        /// 需要重试时由 <see cref="EnsurePreloaded"/> / <see cref="Reload"/> / 【重新载入】触发。
        /// </summary>
        private static bool _loadAttempted;

        /// <summary>
        /// 当前使用的连接目标
        /// （例：<c>connJP1 = SQL Server 10.103.126.208,1433 / DB genpin / 表 T_M_MergeName</c>；
        /// 解析失败时是解析出来的目标）。
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

        /// <summary>
        /// 置换表是否已经读进内存（启动时 <see cref="Preload"/> 或主画面打开时 <see cref="EnsurePreloaded"/> 成功）。
        /// <b>不会触发读库</b>：为 false 时只是没有推荐项，界面照常可用。
        /// </summary>
        public static bool IsLoaded
        {
            get { return _loaded; }
        }

        /// <summary>
        /// 上一次读取数据库失败的原因（成功时为空串）；<b>不会触发读库</b>（库里连不上时画面不会被卡住）。
        /// </summary>
        public static string LastError
        {
            get { return _lastError; }
        }

        /// <summary>
        /// 预读用的连接超时（秒）：数据库连不上时不要把启动 / 主画面打开卡太久。
        /// 预读失败也不会在置换 / 打开「选择部材名」窗口时自动重试（避免界面卡住），
        /// 需要再试时用【部材名面板】的【重新载入】。
        /// </summary>
        public const int PreloadTimeoutSeconds = 3;

        /// <summary>
        /// 重新从数据库读一次（【部材名面板】的【重新载入】/【连接设置】保存后调用，保证内存里的规则是最新的）。
        /// </summary>
        public static void Reload()
        {
            _loadAttempted = true;
            Load(null);
        }

        /// <summary>
        /// 程序启动时把整张置换表读进内存（<c>Program.Main</c> 调用）。
        /// 失败只记 <see cref="LastError"/>（置换表为空 → 原样返回），不影响启动；
        /// 启动时连不上就不再自动重试（避免画面操作时卡住），
        /// 需要再试时由主画面打开时的 <see cref="EnsurePreloaded"/> 或【重新载入】触发。
        /// </summary>
        public static void Preload()
        {
            MemberMappingDbSettings resolved = MemberMappingDbSettingsStore.ResolveJp1();
            MemberMappingDbSettings quick = new MemberMappingDbSettings(resolved.Server, resolved.Port,
                resolved.Database, resolved.UserId, resolved.Password, resolved.ConfigurationSource,
                PreloadTimeoutSeconds, resolved.ConnectionName);

            _loadAttempted = true;
            Load(quick);
        }

        /// <summary>
        /// 还没读过库时读一次（主画面 <see cref="PartBasicInfoForm"/> 打开时调用）：启动时预读失败的话，
        /// 在这里用<b>短超时</b>再试一次，之后「选择部材名」窗口只查内存。
        /// 已经读过（成功或失败）时什么都不做。
        /// </summary>
        public static void EnsurePreloaded()
        {
            if (_loadAttempted)
            {
                return;
            }

            Preload();
        }

        /// <summary>
        /// 按置换表替换一个「部材名」（可含 <c>|</c>，如 <c>鴨居|上レール</c>）；
        /// 置换表为空 / 没有命中 / 数据库打不开时原样返回。
        /// 查表时不区分全角 / 半角片假名（见 <see cref="NormalizeLookupKey"/>）。
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
        /// 要用的连接信息；<c>null</c> = 按当前生效的配置
        /// （<see cref="MemberMappingDbSettingsStore.ResolveJp1"/>，即 <c>connJP1</c> = 10.103.126.208 / genpin）。
        /// </param>
        /// <returns>是否读成功（失败时 <see cref="LastError"/> 有原因）。</returns>
        private static bool Load(MemberMappingDbSettings settings)
        {
            // 置换表在 <connJP1> = 10.103.126.208 / genpin（App.config 的 connectionStrings）；
            // 没配具名连接时退回老的单连接配置（Environment / 连接设置画面 / MappingDb* / 默认值）。
            MemberMappingDbSettings effective = settings ?? MemberMappingDbSettingsStore.ResolveJp1();
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, List<string>> targets =
                new Dictionary<string, List<string>>(StringComparer.Ordinal);
            Dictionary<string, List<string>> sources =
                new Dictionary<string, List<string>>(StringComparer.Ordinal);
            Dictionary<string, string> sourceText = new Dictionary<string, string>(StringComparer.Ordinal);
            string target = MemberNameReplacementRepository.ResolveTargetDescription(effective);
            string error = string.Empty;

            try
            {
                using (MemberNameReplacementRepository repository = new MemberNameReplacementRepository(effective))
                {
                    target = repository.TargetDescription;

                    foreach (MemberNameReplacement row in repository.Query())
                    {
                        if (row.Source.Length == 0 || row.Target.Length == 0)
                        {
                            continue;
                        }

                        string key = NormalizeLookupKey(row.Source);

                        if (!map.ContainsKey(key))
                        {
                            map[key] = row.Target;
                        }

                        // 同一个置换前可能对应多个置换后：全部留在内存里（不连库也能列出来），
                        // 顺序与原来按 SQL「ORDER BY GenpinName」查出来的一致 → 统一按 GenpinName 排序。
                        List<string> found;

                        if (!targets.TryGetValue(key, out found))
                        {
                            found = new List<string>();
                            targets.Add(key, found);
                        }

                        if (!found.Contains(row.Target))
                        {
                            found.Add(row.Target);
                        }

                        // 反向也留一份：同一个置换后（GenpinName）可能有多个置换前
                        // （MsifDevName = 图面上的不同叫法），「表示部材名」候补就从这里取
                        // （见 GetDisplayNames）。
                        string targetKey = NormalizeLookupKey(row.Target);
                        List<string> variants;

                        if (!sources.TryGetValue(targetKey, out variants))
                        {
                            variants = new List<string>();
                            sources.Add(targetKey, variants);
                        }

                        if (!variants.Contains(row.Source))
                        {
                            variants.Add(row.Source);
                        }

                        // 表里那一行的写法（半角 / 全角、送假名 等以表为准）
                        if (!sourceText.ContainsKey(key))
                        {
                            sourceText.Add(key, row.Source.Trim());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            foreach (List<string> found in targets.Values)
            {
                found.Sort(StringComparer.Ordinal);
            }

            foreach (List<string> found in sources.Values)
            {
                found.Sort(StringComparer.Ordinal);
            }

            _targetDescription = target;
            _lastError = error;
            _map = map;
            _targets = targets;
            _sources = sources;
            _sourceText = sourceText;
            _loaded = error.Length == 0;

            return _loaded;
        }

        /// <summary>第一次使用时加载（之后用缓存，直到 <see cref="Reload"/>）。</summary>
        private static void EnsureLoaded()
        {
            // 这一次已经试过（含失败）→ 直接用内存里的内容，画面操作不会再连数据库。
            // 需要重试时由 <see cref="EnsurePreloaded"/> / <see cref="Reload"/> 触发。
            if (_loadAttempted)
            {
                return;
            }

            _loadAttempted = true;
            Load(null);
        }

        /// <summary>
        /// 「选择部材名」窗口的<b>表示部材名</b>（<c>MsifDevName</c>）候补（<b>只查内存，不连数据库</b>）。
        /// <para>
        /// 拿这一行的「部品名」当图面上的叫法去查：
        /// ① 表里有这个写法（不分全角 / 半角）时，先用表里那一行的原文（<c>上レール</c> → <c>上ﾚｰﾙ</c>）；
        /// ② 再列出<b>同一个部材</b>（<c>GenpinName</c> = 原部材名）在图面上的其它叫法
        ///    （<c>上ﾚｰﾙ</c> / <c>上ｱﾙﾐ</c> / <c>ｱﾙﾐ上</c> …）；
        /// ③ 「部品名」本身就是原部材名（<c>GenpinName</c>）时，直接列这个部材的全部叫法。
        /// </para>
        /// 面板上就<b>照这些写法显示 / 写入</b>（不再换成 <c>GenpinName</c>）：保存时也原样登录，
        /// 原部材名 只在 genpin 侧使用。没预读成功 / 没命中时返回空列表。
        /// </summary>
        /// <param name="term">
        /// 要查的词（一般是同一行的「部品名」；按 <see cref="NormalizeLookupKey"/> 匹配：
        /// 半角 / 全角片假名视为同一写法，前后空白会被去掉）。
        /// </param>
        /// <returns>表示部材名（复制出来的新列表，调用方可以随便改）；没有时为空列表。</returns>
        public static IList<string> GetDisplayNames(string term)
        {
            List<string> names = new List<string>();
            string value = (term ?? string.Empty).Trim();

            if (value.Length == 0)
            {
                return names;
            }

            EnsureLoaded();

            Dictionary<string, List<string>> targets = _targets;
            Dictionary<string, List<string>> sources = _sources;
            Dictionary<string, string> sourceText = _sourceText;

            if (sources.Count == 0)
            {
                return names;
            }

            string key = NormalizeLookupKey(value);
            string display;

            // ① 表里有这个写法：先把表里的原文放第 1 个（例：部品名「上レール」→「上ﾚｰﾙ」）
            if (sourceText.TryGetValue(key, out display))
            {
                names.Add(display);
            }

            // ② 同一个部材（GenpinName）在图面上的其它叫法
            List<string> targetNames;

            if (targets.TryGetValue(key, out targetNames))
            {
                for (int index = 0; index < targetNames.Count; index++)
                {
                    AddDisplayNames(names, sources, targetNames[index]);
                }
            }

            // ③ 部品名 本身就是原部材名（GenpinName）时：直接列这个部材的全部叫法
            if (names.Count == 0)
            {
                AddDisplayNames(names, sources, value);
            }

            return names;
        }

        /// <summary>
        /// 把一个「原部材名」（<c>GenpinName</c>）对应的全部表示名（<c>MsifDevName</c>）加进列表（去重）。
        /// </summary>
        /// <param name="names">要加进去的列表（保持顺序）。</param>
        /// <param name="sources">反向索引（规整后的 <c>GenpinName</c> → <c>MsifDevName</c> 一览）。</param>
        /// <param name="genpinName">原部材名（<c>GenpinName</c>）。</param>
        private static void AddDisplayNames(List<string> names,
            Dictionary<string, List<string>> sources, string genpinName)
        {
            List<string> variants;

            if (!sources.TryGetValue(NormalizeLookupKey(genpinName), out variants))
            {
                return;
            }

            for (int index = 0; index < variants.Count; index++)
            {
                if (!names.Contains(variants[index]))
                {
                    names.Add(variants[index]);
                }
            }
        }

        /// <summary>
        /// 单段置换：整段命中 → 直接用；带位置前缀（右 / 左 / 上 / 下）时剥掉前缀再查，命中后加回前缀。
        /// 查表键统一走 <see cref="NormalizeLookupKey"/>（半角 / 全角片假名视为同一种写法）。
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

            if (map.TryGetValue(NormalizeLookupKey(value), out replaced))
            {
                return replaced;
            }

            if (value.Length > 1 && IsPositionPrefix(value[0]))
            {
                string rest = value.Substring(1).Trim();

                if (rest.Length > 0 && map.TryGetValue(NormalizeLookupKey(rest), out replaced))
                {
                    return value[0] + replaced;
                }
            }

            return value;
        }

        /// <summary>
        /// 置换表查找用的键：NFKC（半角片假名 → 全角片假名）+ 去掉首尾空白。
        /// <para>
        /// 置换表（<c>T_M_MergeName</c>）里多为全角写法，而部品基本信息表里的「部材名 / 部品名」
        /// 登录时已转成半角片假名（见 <see cref="JapaneseTextConverter.ToHalfWidthKatakana"/>）。
        /// 键统一成同一种写法，两种宽度都能命中（否则形如 <c>上ﾚｰﾙ</c> 的部品名查不到置换项，
        /// 「选择部材名」窗口的推荐项会消失）。
        /// </para>
        /// </summary>
        private static string NormalizeLookupKey(string value)
        {
            return JapaneseTextConverter.NormalizeWidth(value ?? string.Empty).Trim();
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
