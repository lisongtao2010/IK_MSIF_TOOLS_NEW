using System;
using System.Collections.Generic;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// 内存里的「部材不材 mapping」（SQL Server 表 <c>MSIF.dbo.DL_BuhinBuzaiMapping</c> 的只读缓存）。
    ///
    /// <para>
    /// <b>用途</b>：「选择部材名」窗口（<see cref="MSIFAITOOLS.MemberNamePickerForm"/>）的<b>推荐项</b> ——
    /// 这条部品以前实际用过的部材名（<c>BuzaiName</c>）。
    /// 优先「部品CD（<c>BuhinCd</c>）一致」的行（<see cref="GetBuzaiNamesByBuhinCd"/>，最可信：
    /// 同一张零件图上的部品用的一定是同一个部材），
    /// 其次是「部品名（<c>BuhinName</c>）一致」的行（<see cref="GetBuzaiNamesByBuhinName"/>）。
    /// </para>
    ///
    /// <para>
    /// <b>读取时机</b>：部品基本信息画面打开时（<see cref="MSIFAITOOLS.PartBasicInfoForm"/> 的
    /// <c>PreloadMemberNamePickerData</c> → <see cref="EnsurePreloaded"/>）整表读进内存；
    /// 【保存】把这张表更新成功后（<see cref="BuhinBuzaiMappingRepository.Register(IEnumerable{PartBasicInfo}, out bool)"/>）
    /// 用 <see cref="Reload"/> 刷新 —— 所以刚保存的「部品名 + 部材名」下一次点「部材名」就能选到。
    /// 之后点「部材名」只是内存查找，<b>不连数据库</b>。
    /// </para>
    ///
    /// <para>
    /// <b>查找键</b>：<see cref="MemberPartCatalogLoader.NormalizeKey"/>
    /// （NFKC + 去掉空白 + 统一大写），所以 <c>上ﾚｰﾙ</c> / <c>上レール</c>、
    /// 部品CD 的全角 / 半角差都算同一个值。
    /// 连不上库时 <see cref="IsLoaded"/> = false、<see cref="LastError"/> 有原因：
    /// 窗口只是少一段「以前用过的部材名」，其余候选照常可用，不会因为连不上库把界面卡住。
    /// </para>
    /// </summary>
    internal static class BuhinBuzaiMappingCatalog
    {
        /// <summary>
        /// 预读用的连接超时（秒）：现场没有这台库时不要把画面打开卡太久。
        /// 预读失败也不会在点「部材名」时自动重试（避免界面卡住），
        /// 需要再试时重新打开部品基本信息画面 / 【保存】成功后刷新。
        /// </summary>
        public const int PreloadTimeoutSeconds = 3;

        /// <summary>部品CD（<c>BuhinCd</c> 规整后）→ 部材名 一览（同一个部品CD 可以对应多个部材名）。</summary>
        private static Dictionary<string, List<string>> _byBuhinCd =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        /// <summary>部品名（<c>BuhinName</c> 规整后）→ 部材名 一览（同一个部品名 也可以对应多个部材名）。</summary>
        private static Dictionary<string, List<string>> _byBuhinName =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        /// <summary>表里的全部部材名（去重、按文字排序；「选择部材名」窗口的候补一览用）。</summary>
        private static List<string> _buzaiNames = new List<string>();

        /// <summary>读进内存的行数（画面提示 / 日志用）。</summary>
        private static int _entryCount;

        /// <summary>是否已经<b>成功</b>读过数据库（读进内存的是最新内容）。</summary>
        private static bool _loaded;

        /// <summary>
        /// 这一次运行里是否已经<b>尝试</b>过读数据库（成功与否都算）：
        /// 失败后不在点「部材名」时自动重试（画面操作不会连数据库），
        /// 需要重试时由 <see cref="EnsurePreloaded"/> / <see cref="Reload"/> 触发。
        /// </summary>
        private static bool _loadAttempted;

        /// <summary>当前使用的连接目标（例：<c>SQL Server 10.103.126.209,1433 / DB MSIF / 表 DL_BuhinBuzaiMapping</c>）。</summary>
        private static string _targetDescription = string.Empty;

        /// <summary>上一次读取数据库失败的原因（成功时为空串）。</summary>
        private static string _lastError = string.Empty;

        /// <summary>读进内存的行数（<b>不会触发读库</b>）。</summary>
        public static int EntryCount
        {
            get { return _entryCount; }
        }

        /// <summary>表里的全部部材名（去重、按文字排序；<b>不会触发读库</b>，没预读成功时为空列表）。</summary>
        public static IList<string> BuzaiNames
        {
            get { return _buzaiNames; }
        }

        /// <summary>
        /// 是否已经读进内存（画面打开时 <see cref="EnsurePreloaded"/> 或 <see cref="Reload"/> 成功）。
        /// <b>不会触发读库</b>：为 false 时只是没有这段推荐项，界面照常可用。
        /// </summary>
        public static bool IsLoaded
        {
            get { return _loaded; }
        }

        /// <summary>上一次读取失败的原因（成功时为空串）；<b>不会触发读库</b>。</summary>
        public static string LastError
        {
            get { return _lastError; }
        }

        /// <summary>当前使用的连接目标（画面提示用；<b>不会触发读库</b>）。</summary>
        public static string TargetDescription
        {
            get { return _targetDescription; }
        }

        /// <summary>
        /// 重新从数据库读一次（画面打开时 / 【保存】把这张表更新成功后调用，
        /// 保证内存里的推荐项是最新的）。失败只记 <see cref="LastError"/>，<b>不抛异常</b>。
        /// </summary>
        public static void Reload()
        {
            _loadAttempted = true;
            Load(null);
        }

        /// <summary>
        /// 用<b>短超时</b>读一次整表（画面打开时调用）。失败只记 <see cref="LastError"/>，不影响画面。
        /// </summary>
        public static void Preload()
        {
            MemberMappingDbSettings resolved = MemberMappingDbSettingsStore.ResolveJp2();
            MemberMappingDbSettings quick = new MemberMappingDbSettings(resolved.Server, resolved.Port,
                resolved.Database, resolved.UserId, resolved.Password, resolved.ConfigurationSource,
                PreloadTimeoutSeconds, resolved.ConnectionName);

            _loadAttempted = true;
            Load(quick);
        }

        /// <summary>
        /// 还没读过库时读一次（部品基本信息画面打开时调用）；已经读过（成功或失败）时什么都不做 ——
        /// 重试由重新打开画面 / 【保存】成功后的 <see cref="Reload"/> 负责。
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
        /// 按<b>部品CD</b>（<c>BuhinCd</c>）查部材名 一览（推荐项的第 1 段；<b>只查内存</b>）。
        /// 比较不分全角 / 半角、忽略空白（<see cref="MemberPartCatalogLoader.NormalizeKey"/>）。
        /// </summary>
        /// <param name="buhinCd">画面上的「部品コード」（可为 <c>null</c> / 空）。</param>
        /// <returns>部材名（复制出来的新列表，调用方可以随便改）；没有时为空列表。</returns>
        public static IList<string> GetBuzaiNamesByBuhinCd(string buhinCd)
        {
            return GetNames(_byBuhinCd, buhinCd);
        }

        /// <summary>
        /// 按<b>部品名</b>（<c>BuhinName</c>）查部材名 一览（推荐项的第 2 段；<b>只查内存</b>）。
        /// 同一个部品名 对应多个部材名 时全部返回（按查到时的顺序）。
        /// </summary>
        /// <param name="buhinName">同一行的「部品名」（可为 <c>null</c> / 空）。</param>
        /// <returns>部材名（复制出来的新列表，调用方可以随便改）；没有时为空列表。</returns>
        public static IList<string> GetBuzaiNamesByBuhinName(string buhinName)
        {
            return GetNames(_byBuhinName, buhinName);
        }

        /// <summary>
        /// 读 SQL Server 表 <c>DL_BuhinBuzaiMapping</c>，建好两份索引（部品CD / 部品名 → 部材名）。
        /// 失败只记 <see cref="LastError"/>，索引置空 → 推荐项为空（不影响其它候选）。
        /// </summary>
        /// <param name="settings">
        /// 要用的连接信息；<c>null</c> = 按当前生效的配置（<see cref="MemberMappingDbSettingsStore.ResolveJp2"/>，
        /// 即 <c>connJP2</c> = 10.103.126.209 / MSIF）。
        /// </param>
        /// <returns>是否读成功（失败时 <see cref="LastError"/> 有原因）。</returns>
        private static bool Load(MemberMappingDbSettings settings)
        {
            MemberMappingDbSettings effective = settings ?? MemberMappingDbSettingsStore.ResolveJp2();
            Dictionary<string, List<string>> byBuhinCd =
                new Dictionary<string, List<string>>(StringComparer.Ordinal);
            Dictionary<string, List<string>> byBuhinName =
                new Dictionary<string, List<string>>(StringComparer.Ordinal);
            List<string> buzaiNames = new List<string>();
            string target = BuhinBuzaiMappingRepository.ResolveTargetDescription(effective);
            string error = string.Empty;
            int count = 0;

            try
            {
                using (BuhinBuzaiMappingRepository repository = new BuhinBuzaiMappingRepository(effective))
                {
                    target = repository.Settings.DescribeTarget() + " / 表 " +
                        BuhinBuzaiMappingRepository.TableName;

                    foreach (BuhinBuzaiMappingEntry entry in repository.Query())
                    {
                        count++;

                        AddName(byBuhinCd, entry.BuhinCd, entry.BuzaiName);
                        AddName(byBuhinName, entry.BuhinName, entry.BuzaiName);

                        if (!buzaiNames.Contains(entry.BuzaiName))
                        {
                            buzaiNames.Add(entry.BuzaiName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            buzaiNames.Sort(StringComparer.Ordinal);

            _targetDescription = target;
            _lastError = error;
            _byBuhinCd = byBuhinCd;
            _byBuhinName = byBuhinName;
            _buzaiNames = buzaiNames;
            _entryCount = count;
            _loaded = error.Length == 0;

            return _loaded;
        }

        /// <summary>往索引里加一条（键规整后为空时跳过；部材名 重复时不重复加）。</summary>
        /// <param name="index">索引（部品CD / 部品名 → 部材名）。</param>
        /// <param name="key">部品CD / 部品名（可为 <c>null</c> / 空）。</param>
        /// <param name="buzaiName">部材名（<c>null</c> / 空时跳过）。</param>
        private static void AddName(Dictionary<string, List<string>> index, string key, string buzaiName)
        {
            string name = (buzaiName ?? string.Empty).Trim();
            string normalizedKey = MemberPartCatalogLoader.NormalizeKey(key);

            if (name.Length == 0 || normalizedKey.Length == 0)
            {
                return;
            }

            List<string> names;

            if (!index.TryGetValue(normalizedKey, out names))
            {
                names = new List<string>();
                index.Add(normalizedKey, names);
            }

            if (!names.Contains(name))
            {
                names.Add(name);
            }
        }

        /// <summary>按规整后的键从索引里取部材名（返回复制出来的新列表）。</summary>
        /// <param name="index">索引（部品CD / 部品名 → 部材名）。</param>
        /// <param name="value">部品CD / 部品名（可为 <c>null</c> / 空）。</param>
        private static IList<string> GetNames(Dictionary<string, List<string>> index, string value)
        {
            string key = MemberPartCatalogLoader.NormalizeKey(value);

            if (key.Length == 0)
            {
                return new List<string>();
            }

            List<string> names;

            if (index.TryGetValue(key, out names))
            {
                return new List<string>(names);
            }

            return new List<string>();
        }
    }
}
