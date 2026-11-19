using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// 从 .xlsx / .xlsm 里取出来的一张图片，以及它在工作表里的锚点单元格。
    /// <para>
    /// <see cref="Column"/> / <see cref="Row"/> 是图片左上角所在单元格（0 起，与 Excel 的 A1 对应关系：
    /// 列 5 / 行 19 = F20）。旧的 Excel 预览窗口按这个位置把图片叠到表格上。
    /// </para>
    /// <para>
    /// 现在【分类结果】的 Excel 行改为直接用本机 Excel 打开（见 <see cref="ExcelLauncher"/>），
    /// 不再有「把图片叠到表格上」的画面，本类型目前没有调用方（保留编译，需要时可复用）。
    /// </para>
    /// </summary>
    internal sealed class XlsxImageInfo
    {
        /// <summary>所属工作表索引（0 起，与 ExcelDataReader 的表顺序一致）。</summary>
        public int SheetIndex { get; set; }

        /// <summary>锚点单元格的列（0 起：0 = A、25 = Z、26 = AA）。</summary>
        public int Column { get; set; }

        /// <summary>锚点单元格的行（0 起：0 = Excel 的第 1 行）。</summary>
        public int Row { get; set; }

        /// <summary>图片文件的原始字节（PNG / JPEG / GIF / BMP / EMF / WMF …）。</summary>
        public byte[] Data { get; set; }

        /// <summary>图片扩展名（含点，如 <c>.png</c>），只用于日志 / 排错。</summary>
        public string Extension { get; set; }
    }

    /// <summary>
    /// 读取 .xlsx / .xlsm 里的图片（<c>xl/media</c>）和它们的锚点单元格。
    /// <para>
    /// 为什么需要单独做这件事：单元格内容用的是 <c>ExcelDataReader</c>，它只读<b>单元格的值</b>，
    /// 图片 / 图表这类「浮在表格上的对象」不在它的读取范围内（存在包里的
    /// <c>xl/drawings/*.xml</c> + <c>xl/media/*</c>，与单元格数据是两套东西）。
    /// 所以这里直接按 OpenXML 包结构把图片挖出来，交给预览窗口叠加到表格上。
    /// </para>
    /// <para>
    /// 限制：
    /// <list type="bullet">
    /// <item><c>.xls</c>（BIFF）/ <c>.xlsb</c> 的图片存在二进制记录里，这里不支持 → 返回空（预览窗口会提示用【用 Excel 打开】）；</item>
    /// <item>只取「图片」（<c>twoCellAnchor</c> / <c>oneCellAnchor</c> 里的 <c>a:blip</c>），
    /// 图表 / 形状 / OLE 对象（嵌入的 PDF、Word 等）不渲染；</item>
    /// <item>文件损坏 / 结构不认识时统一当成「没有图片」，绝不抛异常（图片只是附加信息，不能影响单元格预览）。</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static class XlsxImageCatalog
    {
        /// <summary>一个工作簿最多取多少张图片（防止异常文件把内存吃光）。</summary>
        private const int MaxImages = 200;

        /// <summary>单张图片最大字节数（超过的跳过）。</summary>
        private const long MaxImageBytes = 16L * 1024 * 1024;

        /// <summary>全部图片合计最大字节数（超过后不再取）。</summary>
        private const long MaxTotalBytes = 96L * 1024 * 1024;

        /// <summary>Excel 主命名空间（workbook.xml / sheet*.xml）。</summary>
        private static readonly XNamespace SpreadsheetMain =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        /// <summary>Excel 绘图命名空间（drawing*.xml 的 xdr: 前缀）。</summary>
        private static readonly XNamespace SpreadsheetDrawing =
            "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";

        /// <summary>DrawingML 命名空间（a:blip 等）。</summary>
        private static readonly XNamespace DrawingML =
            "http://schemas.openxmlformats.org/drawingml/2006/main";

        /// <summary>Office 关系命名空间（r:id / r:embed 等）。</summary>
        private static readonly XNamespace OfficeRelationship =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        /// <summary>包关系命名空间（*.rels 里的 Relationship 元素）。</summary>
        private static readonly XNamespace PackageRelationship =
            "http://schemas.openxmlformats.org/package/2006/relationships";

        /// <summary>
        /// 这个扩展名能不能取图片：只有 <c>.xlsx</c> / <c>.xlsm</c>（OpenXML 包）能按包结构读，
        /// <c>.xls</c> / <c>.xlsb</c> 是二进制格式，返回 false。
        /// </summary>
        public static bool IsSupportedFormat(string excelFilePath)
        {
            if (string.IsNullOrEmpty(excelFilePath))
            {
                return false;
            }

            string extension = Path.GetExtension(excelFilePath);

            return string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".xlsm", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 是否含有 Excel 365「图片放在单元格中」（Place in Cell）的数据。
        /// <para>
        /// 这种图片不走 <c>xl/drawings</c>，而是放在 <c>xl/richData/</c>（单元格的 <c>vm</c> 属性 →
        /// metadata → richValue → richValueRel → xl/media），本类不解析它。
        /// 预览窗口据此在状态栏提示「请用【用 Excel 打开】」，免得用户以为功能没生效。
        /// </para>
        /// </summary>
        public static bool HasInCellImages(string excelFilePath)
        {
            if (!IsSupportedFormat(excelFilePath) || !File.Exists(excelFilePath))
            {
                return false;
            }

            try
            {
                using (FileStream stream = new FileStream(excelFilePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (string.Equals(entry.FullName, "xl/richData/richValueRel.xml",
                            StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 读不了就当没有：这只是给状态栏用的附加信息
            }

            return false;
        }

        /// <summary>
        /// 读出工作簿里的全部图片（按工作表分组，见 <see cref="XlsxImageInfo.SheetIndex"/>）。
        /// 读不到 / 格式不支持时返回空列表，不抛异常。
        /// </summary>
        /// <param name="excelFilePath">Excel 文件完整路径。</param>
        public static IList<XlsxImageInfo> ReadImages(string excelFilePath)
        {
            List<XlsxImageInfo> images = new List<XlsxImageInfo>();

            if (!IsSupportedFormat(excelFilePath) || !File.Exists(excelFilePath))
            {
                return images;
            }

            try
            {
                using (FileStream stream = new FileStream(excelFilePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    Dictionary<string, ZipArchiveEntry> entries =
                        new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);

                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        entries[entry.FullName] = entry;
                    }

                    List<string> sheetParts = ReadSheetParts(entries);
                    long totalBytes = 0;

                    for (int sheetIndex = 0; sheetIndex < sheetParts.Count; sheetIndex++)
                    {
                        if (images.Count >= MaxImages || totalBytes >= MaxTotalBytes)
                        {
                            break;
                        }

                        totalBytes += ReadSheetImages(entries, sheetParts[sheetIndex], sheetIndex, images);
                    }
                }
            }
            catch (Exception)
            {
                // 包结构不认识 / 文件被别的程序独占：当作「没有图片」，单元格预览照旧
                images.Clear();
            }

            return images;
        }

        /// <summary>按 xl/workbook.xml 的顺序取各工作表部件路径（顺序与 ExcelDataReader 的表索引一致）。</summary>
        private static List<string> ReadSheetParts(Dictionary<string, ZipArchiveEntry> entries)
        {
            List<string> parts = new List<string>();
            XDocument workbook = LoadXml(entries, "xl/workbook.xml");

            if (workbook == null || workbook.Root == null)
            {
                return parts;
            }

            XElement sheets = workbook.Root.Element(SpreadsheetMain + "sheets");

            if (sheets == null)
            {
                return parts;
            }

            Dictionary<string, Relationship> relationships =
                ReadRelationships(entries, "xl/workbook.xml");

            foreach (XElement sheet in sheets.Elements(SpreadsheetMain + "sheet"))
            {
                XAttribute id = sheet.Attribute(OfficeRelationship + "id");

                if (id == null)
                {
                    continue;
                }

                Relationship relationship;

                if (relationships.TryGetValue(id.Value, out relationship) &&
                    !string.IsNullOrEmpty(relationship.Target))
                {
                    // 图表工作表（chartsheet）/ 对话框表（dialogsheet）不算：
                    // ExcelDataReader 的工作表索引里没有它们，混进来会让「图片的表索引」与单元格错位
                    if (string.IsNullOrEmpty(relationship.Type) ||
                        relationship.Type.EndsWith("/worksheet", StringComparison.OrdinalIgnoreCase))
                    {
                        parts.Add(relationship.Target);
                    }
                }
            }

            return parts;
        }

        /// <summary>取某张工作表上的图片，追加到 <paramref name="images"/>，返回本次读到的字节数合计。</summary>
        private static long ReadSheetImages(Dictionary<string, ZipArchiveEntry> entries, string sheetPart,
            int sheetIndex, List<XlsxImageInfo> images)
        {
            long bytes = 0;
            Dictionary<string, Relationship> sheetRelationships = ReadRelationships(entries, sheetPart);
            string drawingPart = null;

            foreach (KeyValuePair<string, Relationship> pair in sheetRelationships)
            {
                Relationship relationship = pair.Value;

                if (relationship != null && relationship.Type != null &&
                    relationship.Type.EndsWith("/drawing", StringComparison.OrdinalIgnoreCase))
                {
                    drawingPart = relationship.Target;
                    break;
                }
            }

            if (string.IsNullOrEmpty(drawingPart))
            {
                return bytes;
            }

            XDocument drawing = LoadXml(entries, drawingPart);

            if (drawing == null || drawing.Root == null)
            {
                return bytes;
            }

            Dictionary<string, Relationship> drawingRelationships = ReadRelationships(entries, drawingPart);

            foreach (XElement anchor in drawing.Root.Elements())
            {
                if (images.Count >= MaxImages)
                {
                    break;
                }

                string anchorName = anchor.Name.LocalName;

                // absoluteAnchor（绝对定位）不知道属于哪一格，跳过；graphicFrame（图表）里没有图片
                if (!string.Equals(anchorName, "twoCellAnchor", StringComparison.Ordinal) &&
                    !string.Equals(anchorName, "oneCellAnchor", StringComparison.Ordinal))
                {
                    continue;
                }

                XElement from = anchor.Element(SpreadsheetDrawing + "from");

                if (from == null)
                {
                    continue;
                }

                int column = ReadInt(from.Element(SpreadsheetDrawing + "col"));
                int row = ReadInt(from.Element(SpreadsheetDrawing + "row"));

                if (column < 0 || row < 0)
                {
                    continue;
                }

                XElement blip = null;

                foreach (XElement element in anchor.Descendants(DrawingML + "blip"))
                {
                    blip = element;
                    break;
                }

                if (blip == null)
                {
                    continue;   // 图表 / 形状 / OLE 对象：没有图片
                }

                XAttribute embed = blip.Attribute(OfficeRelationship + "embed");

                if (embed == null)
                {
                    continue;
                }

                Relationship mediaRelationship;

                if (!drawingRelationships.TryGetValue(embed.Value, out mediaRelationship))
                {
                    continue;
                }

                byte[] data = ReadEntryBytes(entries, mediaRelationship.Target);

                if (data == null || data.Length == 0)
                {
                    continue;
                }

                XlsxImageInfo image = new XlsxImageInfo();
                image.SheetIndex = sheetIndex;
                image.Column = column;
                image.Row = row;
                image.Data = data;
                image.Extension = Path.GetExtension(mediaRelationship.Target);

                images.Add(image);
                bytes += data.Length;
            }

            return bytes;
        }

        /// <summary>
        /// 读某个部件的 *.rels（如 xl/worksheets/_rels/sheet1.xml.rels），
        /// 返回「关系 Id → 目标部件路径（已解析成包内绝对路径，如 xl/drawings/drawing1.xml）」。
        /// 外部链接（TargetMode="External"）忽略。
        /// </summary>
        private static Dictionary<string, Relationship> ReadRelationships(
            Dictionary<string, ZipArchiveEntry> entries, string partPath)
        {
            Dictionary<string, Relationship> result = new Dictionary<string, Relationship>(StringComparer.Ordinal);
            XDocument document = LoadXml(entries, GetRelationshipPartPath(partPath));

            if (document == null || document.Root == null)
            {
                return result;
            }

            foreach (XElement element in document.Root.Elements(PackageRelationship + "Relationship"))
            {
                XAttribute id = element.Attribute("Id");
                XAttribute type = element.Attribute("Type");
                XAttribute target = element.Attribute("Target");
                XAttribute targetMode = element.Attribute("TargetMode");

                if (id == null || target == null)
                {
                    continue;
                }

                if (targetMode != null &&
                    string.Equals(targetMode.Value, "External", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string resolved = ResolvePartPath(partPath, target.Value);

                if (string.IsNullOrEmpty(resolved))
                {
                    continue;
                }

                Relationship relationship = new Relationship();
                relationship.Type = type == null ? string.Empty : type.Value;
                relationship.Target = resolved;

                result[id.Value] = relationship;
            }

            return result;
        }

        /// <summary>部件路径 → 它的 .rels 路径（xl/workbook.xml → xl/_rels/workbook.xml.rels）。</summary>
        private static string GetRelationshipPartPath(string partPath)
        {
            if (string.IsNullOrEmpty(partPath))
            {
                return null;
            }

            int slash = partPath.LastIndexOf('/');

            if (slash < 0)
            {
                return "_rels/" + partPath + ".rels";
            }

            return partPath.Substring(0, slash) + "/_rels/" + partPath.Substring(slash + 1) + ".rels";
        }

        /// <summary>
        /// 把 *.rels 里的相对 Target（如 ../drawings/drawing1.xml、/xl/media/image1.png）
        /// 解析成包内绝对路径（xl/drawings/drawing1.xml）。
        /// </summary>
        private static string ResolvePartPath(string sourcePartPath, string target)
        {
            if (string.IsNullOrEmpty(target))
            {
                return null;
            }

            string normalized = target.Replace('\\', '/');

            if (normalized.StartsWith("/", StringComparison.Ordinal))
            {
                return normalized.TrimStart('/');
            }

            List<string> segments = new List<string>();
            int slash = string.IsNullOrEmpty(sourcePartPath) ? -1 : sourcePartPath.LastIndexOf('/');

            if (slash > 0)
            {
                segments.AddRange(sourcePartPath.Substring(0, slash)
                    .Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries));
            }

            foreach (string part in normalized.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(part, ".", StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(part, "..", StringComparison.Ordinal))
                {
                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }

                    continue;
                }

                segments.Add(part);
            }

            return string.Join("/", segments.ToArray());
        }

        /// <summary>读包里的一个 XML 部件；不存在或解析失败返回 <c>null</c>。</summary>
        private static XDocument LoadXml(Dictionary<string, ZipArchiveEntry> entries, string partPath)
        {
            ZipArchiveEntry entry;

            if (string.IsNullOrEmpty(partPath) || !entries.TryGetValue(partPath, out entry))
            {
                return null;
            }

            try
            {
                using (Stream stream = entry.Open())
                {
                    return XDocument.Load(stream);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>读包里的一张图片（超过大小上限的跳过）。</summary>
        private static byte[] ReadEntryBytes(Dictionary<string, ZipArchiveEntry> entries, string partPath)
        {
            ZipArchiveEntry entry;

            if (string.IsNullOrEmpty(partPath) || !entries.TryGetValue(partPath, out entry))
            {
                return null;
            }

            if (entry.Length <= 0 || entry.Length > MaxImageBytes)
            {
                return null;
            }

            try
            {
                using (Stream stream = entry.Open())
                using (MemoryStream memory = new MemoryStream((int)entry.Length))
                {
                    stream.CopyTo(memory);

                    return memory.ToArray();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>读一个 XML 元素的整数值（如 &lt;xdr:col&gt;5&lt;/xdr:col&gt;）；读不到返回 -1。</summary>
        private static int ReadInt(XElement element)
        {
            if (element == null)
            {
                return -1;
            }

            int value;

            return int.TryParse(element.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : -1;
        }

        /// <summary>*.rels 里的一条关系（Type 用于区分 drawing / image / worksheet …）。</summary>
        private sealed class Relationship
        {
            /// <summary>关系类型（完整 URI，如 …/relationships/drawing）。</summary>
            public string Type { get; set; }

            /// <summary>目标部件的包内绝对路径。</summary>
            public string Target { get; set; }
        }
    }
}
