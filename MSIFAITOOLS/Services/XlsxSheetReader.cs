using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using ExcelDataReader;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// 读 <c>.xlsx</c>（OpenXML）第一张工作表的内容，返回「行 → 单元格文本数组」。
    ///
    /// <para>
    /// 本工具只需要读 Excel 里的文本（部品コード / 使い分け 等），不写 Excel、不需要样式，
    /// 所以主路径直接用 .NET 自带的 <see cref="ZipArchive"/>（xlsx = zip 包）+
    /// <see cref="XmlDocument"/> 解 <c>xl/sharedStrings.xml</c> 与 <c>xl/worksheets/sheet1.xml</c>。
    /// </para>
    ///
    /// <para>
    /// <b>主路径打不开时不直接放弃</b>（图纸文件夹里的 Excel 是别人保存的，什么样的都有），
    /// 按顺序再试两条路：
    /// <list type="number">
    ///   <item>zip 尾部的 EOCD 里「条目数」写错、<see cref="ZipArchive"/> 报
    ///         「中央ディレクトリの終わりに予期されるエントリの数が…」时，
    ///         按中央目录里实际的条目数把这两个数字改对，再读一次；</item>
    ///   <item>还是不行就用 <c>ExcelDataReader</c>（本项目已引用，CadPartSplitter 读部品表也用它；
    ///         老 <c>.xls</c>（BIFF）这种根本不是 zip 的文件，只有它能读）。</item>
    /// </list>
    /// 兜底成功时会在提示里写明「走了哪条兜底、为什么」，日志里一眼能看出来。
    /// </para>
    ///
    /// <para>支持的内容：</para>
    /// <list type="bullet">
    ///   <item>共享字符串（<c>t="s"</c>，绝大多数文本单元格）；</item>
    ///   <item>内联字符串（<c>t="inlineStr"</c>，部分工具写出的文件）；</item>
    ///   <item>数字 / 公式结果（<c>t="n"</c> / <c>t="str"</c>，直接取 <c>&lt;v&gt;</c> 文本）；</item>
    ///   <item>空的 <c>&lt;c&gt;</c>（值按空字符串处理）。</item>
    /// </list>
    /// 不处理日期格式 / 合并单元格 / 多工作表（只读第一张 sheet，与「拾い出し表」的用法一致）。
    /// </summary>
    internal static class XlsxSheetReader
    {
        /// <summary>共享字符串表的 zip 内路径。</summary>
        private const string SharedStringsEntryName = "xl/sharedStrings.xml";

        /// <summary>工作表 zip 内路径的目录前缀。</summary>
        private const string WorksheetEntryPrefix = "xl/worksheets/";

        /// <summary>工作簿定义（表名 → rId）的 zip 内路径。</summary>
        private const string WorkbookEntryName = "xl/workbook.xml";

        /// <summary>工作簿关系表（rId → 工作表路径）的 zip 内路径。</summary>
        private const string WorkbookRelationshipsEntryName = "xl/_rels/workbook.xml.rels";

        /// <summary>打包内路径的目录前缀（<c>worksheets/sheet1.xml</c> → <c>xl/worksheets/sheet1.xml</c>）。</summary>
        private const string PackageDirectoryPrefix = "xl/";

        /// <summary>
        /// 读第一张工作表。文件不存在 / 打不开 / 不是 xlsx 时返回空表，
        /// 失败原因写入 <paramref name="notes"/>（供日志提示，不抛异常）。
        /// </summary>
        /// <param name="filePath">xlsx 文件绝对路径。</param>
        /// <param name="notes">失败说明（可为 <c>null</c>）。</param>
        /// <returns>每一行为一个字符串数组（下标 = 列号 - 1，缺列按空字符串补齐）。</returns>
        public static List<string[]> ReadFirstSheet(string filePath, IList<string> notes)
        {
            return ReadSheetCore(filePath, null, notes);
        }

        /// <summary>
        /// 读<b>指定名字</b>的工作表（例：Excel 部品表的「カメラ」表）。找不到这张表 / 文件打不开时
        /// 返回空表并把原因写进 <paramref name="notes"/>（不抛异常）；
        /// <paramref name="sheetName"/> 为空时与 <see cref="ReadFirstSheet"/> 完全相同。
        /// </summary>
        /// <param name="filePath">xlsx 文件绝对路径。</param>
        /// <param name="sheetName">工作表名（Excel 里显示的标签名，例 <c>カメラ</c>）。</param>
        /// <param name="notes">失败说明（可为 <c>null</c>）。</param>
        public static List<string[]> ReadSheet(string filePath, string sheetName, IList<string> notes)
        {
            return ReadSheetCore(filePath, sheetName, notes);
        }

        /// <summary>
        /// <see cref="ReadFirstSheet"/> / <see cref="ReadSheet"/> 的共同实现。
        ///
        /// <para>
        /// 按三条路读，前一条失败才走后面一条（图纸文件夹里的 Excel 是别人保存的，
        /// 版式 / zip 结构什么样的都有，宁可多试一次，也不要让整份部品表读不出来）：
        /// </para>
        /// <list type="number">
        ///   <item>正常路径：<see cref="ZipArchive"/> + <see cref="XmlDocument"/>；</item>
        ///   <item>zip 尾部的 EOCD 里「条目数」写错时（.NET 会直接抛异常，Excel 自己却能打开），
        ///         按中央目录实际条目数修正后重读（见 <see cref="TryRepairZip"/>）；</item>
        ///   <item>仍读不出时改用 ExcelDataReader（项目已引用；老 <c>.xls</c> 只有它能读）。</item>
        /// </list>
        /// </summary>
        private static List<string[]> ReadSheetCore(string filePath, string sheetName, IList<string> notes)
        {
            List<string[]> rows = new List<string[]>();

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                AddNote(notes, "找不到文件：" + filePath);
                return rows;
            }

            string fileName = Path.GetFileName(filePath);

            // ── ① 正常路径：xlsx = zip 包 ──
            Exception zipError = null;

            try
            {
                using (FileStream stream = OpenRead(filePath))
                {
                    return ReadWorkbook(stream, fileName, sheetName, notes);
                }
            }
            catch (Exception ex)
            {
                zipError = ex;
            }

            // ── ② 兜底 1：EOCD 的「条目数」与实际中央目录条目数不一致时，修正后再读 ──
            try
            {
                string repairDetail;
                MemoryStream repaired = TryRepairZip(filePath, out repairDetail);

                if (repaired != null)
                {
                    using (repaired)
                    {
                        List<string[]> repairedRows = ReadWorkbook(repaired, fileName, sheetName, notes);
                        AddNote(notes, fileName + "：" + repairDetail);

                        return repairedRows;
                    }
                }
            }
            catch (Exception ex)
            {
                AddNote(notes, fileName + "：修正中央目录条目数后仍然读取失败（" + ex.Message + "）");
            }

            // ── ③ 兜底 2：ExcelDataReader（能读老 .xls，xlsx 的容错一般也比 ZipArchive 宽）──
            try
            {
                string excelDetail;

                if (TryReadWithExcelDataReader(filePath, sheetName, rows, notes, out excelDetail))
                {
                    AddNote(notes, fileName + "：" + excelDetail);

                    return rows;
                }
            }
            catch (Exception)
            {
                // 兜底也失败：下面用 ZipArchive 的原始原因报告，用户看得懂是什么问题
            }

            string reason = zipError == null
                ? "原因不明"
                : zipError.GetType().Name + "：" + zipError.Message;

            AddNote(notes, fileName + "：读取失败（" + reason + "）");
            AddNote(notes, fileName + "：" + DescribeFile(filePath));

            return rows;
        }

        /// <summary>
        /// 从 zip 流读工作簿：共享字符串 + 指定（或第一张）工作表 → 「行 → 单元格文本数组」。
        /// 打不开 / 结构异常时抛异常，由 <see cref="ReadSheetCore"/> 决定走哪条兜底路径。
        /// </summary>
        /// <param name="stream">zip 流（文件流，或修正过 EOCD 的内存流）。</param>
        /// <param name="fileName">日志里的文件名（只用于提示）。</param>
        /// <param name="sheetName">工作表名；空 = 第一张表。</param>
        /// <param name="notes">提示（工作表找不到时写一条）。</param>
        private static List<string[]> ReadWorkbook(Stream stream, string fileName, string sheetName,
            IList<string> notes)
        {
            List<string[]> rows = new List<string[]>();

            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, false, Encoding.UTF8))
            {
                List<string> sharedStrings = ReadSharedStrings(archive);
                bool byName = !string.IsNullOrWhiteSpace(sheetName);
                ZipArchiveEntry sheetEntry = byName
                    ? FindWorksheetEntry(archive, sheetName.Trim())
                    : FindFirstWorksheetEntry(archive);

                if (sheetEntry == null)
                {
                    AddNote(notes, fileName + (byName
                        ? "：找不到工作表「" + sheetName.Trim() + "」"
                        : "：找不到工作表（xl/worksheets/sheet1.xml）"));

                    return rows;
                }

                using (Stream sheetStream = sheetEntry.Open())
                {
                    XmlDocument document = new XmlDocument();
                    document.Load(sheetStream);

                    XmlNode sheetData = FindDescendant(document.DocumentElement, "sheetData");

                    if (sheetData == null)
                    {
                        return rows;
                    }

                    foreach (XmlNode rowNode in sheetData.ChildNodes)
                    {
                        if (rowNode == null || rowNode.NodeType != XmlNodeType.Element ||
                            !IsName(rowNode, "row"))
                        {
                            continue;
                        }

                        rows.Add(ReadRow(rowNode, sharedStrings));
                    }
                }
            }

            return rows;
        }

        /// <summary>只读打开文件（<c>FileShare.ReadWrite</c>：Excel 正开着这个文件时也能读）。</summary>
        private static FileStream OpenRead(string filePath)
        {
            return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }

        /// <summary>
        /// 读不动时补一行文件信息（大小 + 头部格式）：日志里据此判断是文件被截断 /
        /// 本来就不是 xlsx（老式 <c>.xls</c>）/ 内容损坏，不用再让用户去翻文件。
        /// </summary>
        private static string DescribeFile(string filePath)
        {
            try
            {
                long size = new FileInfo(filePath).Length;
                byte[] head = new byte[4];
                int read;

                using (FileStream stream = OpenRead(filePath))
                {
                    read = stream.Read(head, 0, head.Length);
                }

                bool zip = read >= 2 && head[0] == (byte)'P' && head[1] == (byte)'K';

                // D0 CF 11 E0 = OLE2 容器头（Excel 97-2003 的 .xls）
                bool ole2 = read == 4 && head[0] == 0xD0 && head[1] == 0xCF && head[2] == 0x11 &&
                    head[3] == 0xE0;

                return "文件 " + size + " 字节，头部是 " + (zip
                    ? "zip（xlsx）"
                    : ole2
                        ? "OLE2（老式 .xls）"
                        : "未知格式（可能是 Excel 95 / 文本 / 损坏文件）");
            }
            catch (Exception)
            {
                return "文件信息读不出来（文件可能已被删除 / 被占用）";
            }
        }

        /// <summary>读 <c>xl/sharedStrings.xml</c>（没有该文件时返回空表）。</summary>
        private static List<string> ReadSharedStrings(ZipArchive archive)
        {
            List<string> values = new List<string>();
            ZipArchiveEntry entry = archive.GetEntry(SharedStringsEntryName);

            if (entry == null)
            {
                return values;
            }

            using (Stream stream = entry.Open())
            {
                XmlDocument document = new XmlDocument();
                document.Load(stream);

                XmlNode root = document.DocumentElement;

                if (root == null)
                {
                    return values;
                }

                foreach (XmlNode item in root.ChildNodes)
                {
                    if (item != null && item.NodeType == XmlNodeType.Element && IsName(item, "si"))
                    {
                        // <si> 里可能有 <r><t> 分段（富文本），InnerText 会把各段拼起来
                        values.Add(item.InnerText ?? string.Empty);
                    }
                }
            }

            return values;
        }

        /// <summary>取 zip 里第一张 worksheet（优先 sheet1.xml，其次按名字排序的第一个）。</summary>
        private static ZipArchiveEntry FindFirstWorksheetEntry(ZipArchive archive)
        {
            ZipArchiveEntry fallback = null;

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.FullName) ||
                    !entry.FullName.StartsWith(WorksheetEntryPrefix, StringComparison.OrdinalIgnoreCase) ||
                    !entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(entry.FullName, WorksheetEntryPrefix + "sheet1.xml",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }

                if (fallback == null || string.CompareOrdinal(entry.FullName, fallback.FullName) < 0)
                {
                    fallback = entry;
                }
            }

            return fallback;
        }

        /// <summary>
        /// 按工作表名找 zip 里的 worksheet：读 <c>xl/workbook.xml</c> 的
        /// <c>&lt;sheet name="…" r:id="rIdN"/&gt;</c>，再用 <c>xl/_rels/workbook.xml.rels</c>
        /// 把 rId 换成实际路径。找不到（或工作簿定义损坏）返回 <c>null</c>。
        /// </summary>
        private static ZipArchiveEntry FindWorksheetEntry(ZipArchive archive, string sheetName)
        {
            if (archive == null || string.IsNullOrWhiteSpace(sheetName))
            {
                return null;
            }

            try
            {
                string relationshipId = FindSheetRelationshipId(archive, sheetName);

                if (string.IsNullOrEmpty(relationshipId))
                {
                    return null;
                }

                string target = FindRelationshipTarget(archive, relationshipId);

                if (string.IsNullOrEmpty(target))
                {
                    return null;
                }

                string entryName = NormalizeEntryName(target);
                ZipArchiveEntry entry = archive.GetEntry(entryName);

                if (entry != null)
                {
                    return entry;
                }

                // 路径写法 / 大小写不一致时再按不区分大小写找一遍
                foreach (ZipArchiveEntry candidate in archive.Entries)
                {
                    if (string.Equals(candidate.FullName, entryName, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }
            catch (Exception)
            {
                // 工作簿定义读不出来：当作「找不到这张表」，由调用方决定是否退回第一张工作表
            }

            return null;
        }

        /// <summary>在 <c>xl/workbook.xml</c> 里找「表名 → r:id」；找不到返回空字符串。</summary>
        private static string FindSheetRelationshipId(ZipArchive archive, string sheetName)
        {
            ZipArchiveEntry entry = archive.GetEntry(WorkbookEntryName);

            if (entry == null)
            {
                return string.Empty;
            }

            using (Stream stream = entry.Open())
            {
                XmlDocument document = new XmlDocument();
                document.Load(stream);

                XmlNode sheets = FindDescendant(document.DocumentElement, "sheets");

                if (sheets == null)
                {
                    return string.Empty;
                }

                foreach (XmlNode node in sheets.ChildNodes)
                {
                    if (node == null || node.NodeType != XmlNodeType.Element || !IsName(node, "sheet"))
                    {
                        continue;
                    }

                    XmlAttribute nameAttribute = node.Attributes == null ? null : node.Attributes["name"];

                    if (nameAttribute == null ||
                        !string.Equals((nameAttribute.Value ?? string.Empty).Trim(), sheetName,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    return ReadRelationshipId(node.Attributes);
                }
            }

            return string.Empty;
        }

        /// <summary>取 <c>&lt;sheet&gt;</c> 的 <c>r:id</c>（LocalName = <c>id</c>，与 <c>sheetId</c> 区分）。</summary>
        private static string ReadRelationshipId(XmlAttributeCollection attributes)
        {
            if (attributes == null)
            {
                return string.Empty;
            }

            foreach (XmlAttribute attribute in attributes)
            {
                if (attribute != null && string.Equals(attribute.LocalName, "id", StringComparison.Ordinal))
                {
                    return attribute.Value ?? string.Empty;
                }
            }

            return string.Empty;
        }

        /// <summary>在 <c>xl/_rels/workbook.xml.rels</c> 里把 rId 换成 <c>Target</c>；找不到返回空字符串。</summary>
        private static string FindRelationshipTarget(ZipArchive archive, string relationshipId)
        {
            ZipArchiveEntry entry = archive.GetEntry(WorkbookRelationshipsEntryName);

            if (entry == null)
            {
                return string.Empty;
            }

            using (Stream stream = entry.Open())
            {
                XmlDocument document = new XmlDocument();
                document.Load(stream);

                XmlNode root = document.DocumentElement;

                if (root == null)
                {
                    return string.Empty;
                }

                foreach (XmlNode node in root.ChildNodes)
                {
                    if (node == null || node.NodeType != XmlNodeType.Element || !IsName(node, "Relationship"))
                    {
                        continue;
                    }

                    XmlAttribute idAttribute = node.Attributes == null ? null : node.Attributes["Id"];

                    if (idAttribute == null ||
                        !string.Equals(idAttribute.Value, relationshipId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    XmlAttribute targetAttribute = node.Attributes["Target"];

                    return targetAttribute == null ? string.Empty : (targetAttribute.Value ?? string.Empty);
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// 把 rels 的 <c>Target</c> 变成 zip 内的完整路径：
        /// <c>/xl/worksheets/sheet2.xml</c> → <c>xl/worksheets/sheet2.xml</c>；
        /// <c>worksheets/sheet2.xml</c> → <c>xl/worksheets/sheet2.xml</c>。
        /// </summary>
        private static string NormalizeEntryName(string target)
        {
            string value = (target ?? string.Empty).Replace('\\', '/').Trim();

            while (value.StartsWith("/", StringComparison.Ordinal))
            {
                value = value.Substring(1);
            }

            if (value.StartsWith(PackageDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            return PackageDirectoryPrefix + value;
        }

        /// <summary>把一行的单元格解成「列号 - 1」下标的文本数组。</summary>
        private static string[] ReadRow(XmlNode rowNode, IList<string> sharedStrings)
        {
            List<string> cells = new List<string>();
            int nextIndex = 0;

            foreach (XmlNode cellNode in rowNode.ChildNodes)
            {
                if (cellNode == null || cellNode.NodeType != XmlNodeType.Element || !IsName(cellNode, "c"))
                {
                    continue;
                }

                // <c r="L3"> 的 r 给出「列字母 + 行号」；没有 r 时按出现顺序排列
                int columnIndex = ResolveColumnIndex(cellNode.Attributes, nextIndex);
                nextIndex = columnIndex + 1;

                while (cells.Count <= columnIndex)
                {
                    cells.Add(string.Empty);
                }

                cells[columnIndex] = ReadCellText(cellNode, sharedStrings);
            }

            return cells.ToArray();
        }

        /// <summary>解析 <c>r="L3"</c> 里的列号（L → 11）；解析不了时用 <paramref name="fallback"/>。</summary>
        private static int ResolveColumnIndex(XmlAttributeCollection attributes, int fallback)
        {
            if (attributes == null)
            {
                return fallback;
            }

            XmlAttribute reference = attributes["r"];

            if (reference == null || string.IsNullOrEmpty(reference.Value))
            {
                return fallback;
            }

            int column = 0;
            int letters = 0;

            foreach (char ch in reference.Value)
            {
                if (ch >= 'A' && ch <= 'Z')
                {
                    column = column * 26 + (ch - 'A' + 1);
                    letters++;
                }
                else if (ch >= 'a' && ch <= 'z')
                {
                    column = column * 26 + (ch - 'a' + 1);
                    letters++;
                }
                else
                {
                    break;
                }
            }

            return letters == 0 ? fallback : column - 1;
        }

        /// <summary>读一个单元格的文本（共享字符串 / 内联字符串 / 数字）。</summary>
        private static string ReadCellText(XmlNode cellNode, IList<string> sharedStrings)
        {
            string type = string.Empty;
            XmlAttribute typeAttribute = cellNode.Attributes == null ? null : cellNode.Attributes["t"];

            if (typeAttribute != null)
            {
                type = typeAttribute.Value ?? string.Empty;
            }

            if (string.Equals(type, "inlineStr", StringComparison.OrdinalIgnoreCase))
            {
                XmlNode inlineText = FindDescendant(cellNode, "t");
                return inlineText == null ? string.Empty : (inlineText.InnerText ?? string.Empty).Trim();
            }

            XmlNode valueNode = FindChild(cellNode, "v");
            string raw = valueNode == null ? string.Empty : (valueNode.InnerText ?? string.Empty);

            if (string.Equals(type, "s", StringComparison.OrdinalIgnoreCase))
            {
                int index;

                if (int.TryParse(raw.Trim(), out index) && index >= 0 && index < sharedStrings.Count)
                {
                    return (sharedStrings[index] ?? string.Empty).Trim();
                }

                return string.Empty;
            }

            return raw.Trim();
        }

        /// <summary>按 localName 找直接子元素（兼容 transitional / strict 两种命名空间）。</summary>
        private static XmlNode FindChild(XmlNode parent, string localName)
        {
            if (parent == null)
            {
                return null;
            }

            foreach (XmlNode child in parent.ChildNodes)
            {
                if (child != null && child.NodeType == XmlNodeType.Element && IsName(child, localName))
                {
                    return child;
                }
            }

            return null;
        }

        /// <summary>按 localName 深度优先找第一个后代元素。</summary>
        private static XmlNode FindDescendant(XmlNode parent, string localName)
        {
            if (parent == null)
            {
                return null;
            }

            foreach (XmlNode child in parent.ChildNodes)
            {
                if (child == null || child.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (IsName(child, localName))
                {
                    return child;
                }

                XmlNode found = FindDescendant(child, localName);

                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static bool IsName(XmlNode node, string localName)
        {
            return string.Equals(node.LocalName ?? node.Name, localName, StringComparison.Ordinal);
        }

        /// <summary>zip 尾部 EOCD（End Of Central Directory）签名：<c>PK\005\006</c>。</summary>
        private const uint EndOfCentralDirectorySignature = 0x06054B50;

        /// <summary>中央目录条目（Central Directory File Header）签名：<c>PK\001\002</c>。</summary>
        private const uint CentralDirectoryEntrySignature = 0x02014B50;

        /// <summary>允许整份读进内存做修正的最大文件大小（再大就算了，别把内存吃掉）。</summary>
        private const long MaxRepairFileSize = 256L * 1024 * 1024;

        /// <summary>
        /// 修正「zip 尾部 EOCD 里写的条目数」与「中央目录实际条目数」不一致的问题，
        /// 返回可以重新交给 <see cref="ZipArchive"/> 的内存流；不需要修正 / 修正不了时返回 <c>null</c>。
        ///
        /// <para>
        /// 背景：图纸文件夹里 <c>H310Z10004.XLS</c> 这类 Excel 自己打得开，但 .NET 的
        /// <see cref="ZipArchive"/> 报「中央ディレクトリの終わりに予期されるエントリの数が
        /// 中央ディレクトリ内のエントリの数に対応しません」—— 尾部记录写着「共 N 个条目」，
        /// 中央目录里实际是 M 个（N ≠ M）。本方法按实际条目数改写 EOCD 里那两个 2 字节字段
        /// （偏移 +8 = 本盘条目数、+10 = 总条目数），别的字节一个都不动。
        /// </para>
        ///
        /// <para>ZIP64 / 超大文件 / 不是 zip / 条目数本来就对：返回 <c>null</c>（交给后面的兜底）。</para>
        /// </summary>
        /// <param name="filePath">xlsx 文件路径。</param>
        /// <param name="detail">修正说明（成功时给出「原来的数 / 实际的数」，写进日志）。</param>
        private static MemoryStream TryRepairZip(string filePath, out string detail)
        {
            detail = string.Empty;

            byte[] data;

            try
            {
                using (FileStream stream = OpenRead(filePath))
                {
                    if (stream.Length < 22 || stream.Length > MaxRepairFileSize)
                    {
                        return null;
                    }

                    data = new byte[stream.Length];
                    int read = 0;

                    while (read < data.Length)
                    {
                        int count = stream.Read(data, read, data.Length - read);

                        if (count <= 0)
                        {
                            return null;
                        }

                        read += count;
                    }
                }
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            // ── 找 EOCD：从文件尾部往前找（尾部最多还有 64KB 的注释区）──
            int eocd = -1;
            int lowest = Math.Max(0, data.Length - 22 - 0xFFFF);

            for (int i = data.Length - 22; i >= lowest; i--)
            {
                if (ReadUInt32(data, i) == EndOfCentralDirectorySignature)
                {
                    eocd = i;
                    break;
                }
            }

            if (eocd < 0)
            {
                return null;
            }

            int recorded = ReadUInt16(data, eocd + 10);
            uint directorySize = ReadUInt32(data, eocd + 12);
            uint directoryOffset = ReadUInt32(data, eocd + 16);

            // ZIP64（字段写满 0xFF / 0xFFFF）或数值明显不对：不猜，交给后面的兜底
            if (recorded == 0xFFFF || directorySize == 0xFFFFFFFF || directoryOffset == 0xFFFFFFFF ||
                directoryOffset >= data.Length)
            {
                return null;
            }

            // ── 按中央目录实际数一遍条目 ──
            int actual = 0;
            long position = directoryOffset;

            while (position + 46 <= data.Length &&
                ReadUInt32(data, (int)position) == CentralDirectoryEntrySignature)
            {
                int nameLength = ReadUInt16(data, (int)position + 28);
                int extraLength = ReadUInt16(data, (int)position + 30);
                int commentLength = ReadUInt16(data, (int)position + 32);

                position += 46 + nameLength + extraLength + commentLength;
                actual++;
            }

            if (actual == 0 || actual == recorded || actual > 0xFFFF)
            {
                return null;                // 数得对 / 数不出来：这个错不是本方法能修的
            }

            // ── 改写 EOCD 的两个「条目数」字段 ──
            WriteUInt16(data, eocd + 8, (ushort)actual);
            WriteUInt16(data, eocd + 10, (ushort)actual);

            detail = "xlsx 尾部记录的条目数是 " + recorded + "，中央目录里实际是 " + actual +
                " 个（Excel 自己打得开），已按实际条目数修正后重读";

            return new MemoryStream(data, false);
        }

        /// <summary>读小端 2 字节无符号整数（zip 结构一律小端）。</summary>
        private static int ReadUInt16(byte[] data, int offset)
        {
            return data[offset] | (data[offset + 1] << 8);
        }

        /// <summary>读小端 4 字节无符号整数。</summary>
        private static uint ReadUInt32(byte[] data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) |
                (data[offset + 3] << 24));
        }

        /// <summary>写小端 2 字节无符号整数（只用于修正 EOCD 的条目数）。</summary>
        private static void WriteUInt16(byte[] data, int offset, ushort value)
        {
            data[offset] = (byte)(value & 0xFF);
            data[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        /// <summary>
        /// 兜底读取：用 ExcelDataReader（项目已引用，<c>CadPartSplitter.TzJsonBuilder</c> 读部品表也用它）。
        /// 它按文件内容判断格式：老 <c>.xls</c>（BIFF）这种根本不是 zip 的也能读，
        /// xlsx 的容错一般也比 <see cref="ZipArchive"/> 宽。
        /// </summary>
        /// <param name="filePath">Excel 文件路径。</param>
        /// <param name="sheetName">工作表名；空 = 第一张表。</param>
        /// <param name="rows">读到的内容（行 → 单元格文本数组）。</param>
        /// <param name="notes">提示（找不到指定工作表时写一条）。</param>
        /// <param name="detail">成功说明（写进日志：为什么走了这条兜底）。</param>
        /// <returns>工作簿打得开（哪怕一条明细都没有）返回 <c>true</c>；打不开时返回 <c>false</c>。</returns>
        private static bool TryReadWithExcelDataReader(string filePath, string sheetName,
            List<string[]> rows, IList<string> notes, out string detail)
        {
            detail = string.Empty;

            ExcelReaderConfiguration configuration = new ExcelReaderConfiguration
            {
                // 老 .xls 没写代码页时按日文 Shift-JIS 解（与 CadPartSplitter 的部品表读取一致）
                FallbackEncoding = Encoding.GetEncoding(932),
                LeaveOpen = false
            };

            using (FileStream stream = OpenRead(filePath))
            using (IExcelDataReader reader = ExcelReaderFactory.CreateReader(stream, configuration))
            {
                bool byName = !string.IsNullOrWhiteSpace(sheetName);
                string wanted = byName ? sheetName.Trim() : string.Empty;
                int index = 0;

                while (true)
                {
                    index++;

                    string name = ReadSheetName(reader, index);
                    bool match = byName
                        ? string.Equals(name, wanted, StringComparison.Ordinal)
                        : index == 1;

                    if (match)
                    {
                        ReadAllRows(reader, rows);
                        detail = "ZipArchive 打不开，改用 ExcelDataReader 读取成功";
                        return true;
                    }

                    // 不是要找的表：先把这张表的行读到底再换下一张
                    // （个别格式不读完就 NextResult 会读不出后面的表）
                    while (reader.Read())
                    {
                    }

                    if (!reader.NextResult())
                    {
                        break;
                    }
                }

                // 工作簿打得开，但没有要找的那张表：与正常路径同样的提示
                if (byName)
                {
                    AddNote(notes, Path.GetFileName(filePath) + "：找不到工作表「" + wanted + "」");
                }

                detail = "ZipArchive 打不开，改用 ExcelDataReader 打开了工作簿";
                return true;
            }
        }

        /// <summary>取当前工作表名；读不到时用「表N」占位（只影响日志与提示）。</summary>
        private static string ReadSheetName(IExcelDataReader reader, int index)
        {
            try
            {
                string name = reader.Name;

                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name.Trim();
                }
            }
            catch (NotSupportedException)
            {
                // 个别格式不带表名
            }
            catch (InvalidOperationException)
            {
                // 同上
            }

            return "表" + index.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 把当前工作表整张读成「行 → 单元格文本数组」（空行也保留，行号才对应得上 Excel）。
        /// 单个单元格读失败只让那一格变成空串，不打断整张表。
        /// </summary>
        private static void ReadAllRows(IExcelDataReader reader, List<string[]> rows)
        {
            while (reader.Read())
            {
                int count = reader.FieldCount;
                string[] row = new string[count];

                for (int c = 0; c < count; c++)
                {
                    object value;

                    try
                    {
                        value = reader.GetValue(c);
                    }
                    catch (Exception)
                    {
                        value = null;           // 损坏的单元格：当空格处理
                    }

                    row[c] = ToText(value);
                }

                rows.Add(row);
            }
        }

        /// <summary>
        /// 单元格值 → 文本：字符串原样，数字去掉多余的 <c>.0</c>（員数 1 不写成 1.0），
        /// 日期写成 <c>yyyy-MM-dd</c>。与 <c>CadPartSplitter.TzJsonBuilder</c> 的格式化一致，
        /// 两条读取路径读出来的表能对得上。
        /// </summary>
        private static string ToText(object value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            string text = value as string;

            if (text != null)
            {
                return text;
            }

            if (value is bool)
            {
                return ((bool)value) ? "TRUE" : "FALSE";
            }

            if (value is DateTime)
            {
                return ((DateTime)value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            if (value is double || value is float || value is decimal)
            {
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);

                if (Math.Abs(number - Math.Round(number)) < 1e-9)
                {
                    return ((long)Math.Round(number)).ToString(CultureInfo.InvariantCulture);
                }

                return number.ToString("0.########", CultureInfo.InvariantCulture);
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static void AddNote(IList<string> notes, string message)
        {
            if (notes != null && !string.IsNullOrWhiteSpace(message))
            {
                notes.Add(message);
            }
        }
    }
}
