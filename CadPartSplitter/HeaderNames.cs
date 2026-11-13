using System;

namespace CadPartSplitter
{
    /// <summary>
    /// 表头列名的**同义写法**：图纸上同一列常换好几种叫法（「部品コード」/「部品番号」/「品番」…），
    /// 按列名找列时这些写法一律当成同一列 —— 否则只是标题写法不同，整张表就被当成「不认识的表」跳过。
    ///
    /// 匹配规则与 <see cref="CsvFile.IndexOfHeader"/> 完全一致：先 <see cref="CsvFile.Normalize"/> 去掉
    /// 半角 / 全角空格与换行，再「包含」即算命中（PDF 里的表头常被拆成两行、夹着空格）。
    /// 所以**短词不要放进组里**：例如 図番 组里不能写「番号」，否则部品表里的 `番号` 列（行番号 1 / 2 / 3）
    /// 也会被当成 図番。
    ///
    /// 组内**长的写法排在前面**（「図面名称」先于「名称」），<see cref="Match"/> 命中的就是图纸上实际写的那个词，
    /// 日志里能直接看到「这张表的 図番列 标题写的是 部品番号」。
    /// </summary>
    internal static class HeaderNames
    {
        /// <summary>
        /// 「項目」：要領図一覧 的左列，值是 <c>A</c> / <c>B</c> / <c>F</c> 或 <c>A,M</c> 这种合写。
        /// 图纸上也有把这一列写成「品目」「項目記号」的。
        /// </summary>
        public static readonly string[] Item = { "項目", "品目", "項目記号" };

        /// <summary>
        /// 「図番」：要領図 的番号 <c>H310U10022</c>（固定 10 位）。图纸上这一列的标题也写成
        /// 部品番号 / 部品コード / 品番 / 型式 / 型番 —— 同一列换个叫法而已（H094U10008 就写着「部品番号」）。
        /// 这些写法都长不成「番号」那种短词，部品表里的行号列不会被误认。
        /// </summary>
        public static readonly string[] DrawingNumber =
        {
            "図番", "図面番号", "図面NO", "部品番号", "部品コード", "品番", "型式", "型番"
        };

        /// <summary>
        /// 「要領図漢字」：要領図 的名称（<c>枠 縦枠/小縦枠スペーサー取付要領図</c>）。
        /// 多数图纸这一列**根本没有标题**（按位置推断），有标题时也常写成 部品名 / 品名 / 名称。
        /// </summary>
        public static readonly string[] YoryoName =
        {
            "要領図漢字", "要領図名称", "要領図名", "図面名称", "図面名", "部品名", "品名", "名称"
        };

        /// <summary>
        /// 「部品コード」（品番 <c>MZA674</c>）。图纸上也有写成 部品番号 / 品番 / 型式 / 型番 的。
        /// </summary>
        public static readonly string[] PartCode = { "部品コード", "部品番号", "品番", "型式", "型番" };

        /// <summary>「部品名」：图纸上也有写成 品名 / 名称 的。</summary>
        public static readonly string[] PartName = { "部品名", "品名", "名称" };

        /// <summary>
        /// 在一行表头里找这一组同义写法所在的列（左 → 右，第一个命中的列）；-1 表示这一行没有这一列。
        /// 命中方式是「包含」：<c>部品コード(色)</c> 这种带后缀的标题也算。
        /// </summary>
        public static int IndexOf(string[] header, string[] aliases)
        {
            if (header == null || aliases == null)
                return -1;

            for (int c = 0; c < header.Length; c++)
            {
                if (IsMatch(header[c], aliases))
                    return c;
            }

            return -1;
        }

        /// <summary>
        /// <paramref name="column"/> 这一列的表头**写的是哪种叫法**（命中了组里的哪个词，已归一化）；
        /// 没命中返回 null。日志里报出来：图纸上写「部品番号」还是「図番」一眼能看到。
        /// </summary>
        public static string Match(string[] header, int column, string[] aliases)
        {
            if (header == null || aliases == null || column < 0 || column >= header.Length)
                return null;

            string cell = CsvFile.Normalize(header[column]);

            if (cell.Length == 0)
                return null;

            foreach (string alias in aliases)
            {
                string key = CsvFile.Normalize(alias);

                if (key.Length > 0 && cell.IndexOf(key, StringComparison.Ordinal) >= 0)
                    return key;
            }

            return null;
        }

        /// <summary>
        /// 一格文字里有没有这一组写法 —— 用于「表中间重复出现的表头行」「块里的子表头」这种单格判定
        /// （明细格子里也这么判：整格写着 部品番号 的那一行是重复表头，不是明细）。
        /// </summary>
        public static bool IsMatch(string text, string[] aliases)
        {
            if (aliases == null)
                return false;

            string cell = CsvFile.Normalize(text);

            if (cell.Length == 0)
                return false;

            foreach (string alias in aliases)
            {
                string key = CsvFile.Normalize(alias);

                if (key.Length > 0 && cell.IndexOf(key, StringComparison.Ordinal) >= 0)
                    return true;
            }

            return false;
        }
    }
}
