using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using OpenCvSharp;

namespace CadPartSplitter
{
    /// <summary>
    /// 把 PDF 文字层里的文字补画（烧）回零件图 PNG（默认开启，命令行 <c>--no-burn-text</c> 关闭）。
    ///
    /// 为什么需要：某些图纸的日文用的是<b>内嵌的 MS-PGothic 子集</b>（Type0 / Identity-H），
    /// PDFium（Docnet.Core）渲染时画不出这些字形 —— 页面上「鴨居 / 三方枠 / G部」这类标注整块空白，
    /// 而同一位置上的圈内数字（④4、⑧8）和圆圈却正常，于是裁出来的 Part_XX.png 只剩线条、
    /// 没有任何文字，人眼看不出来这是「哪一件」。
    ///
    /// 文字层本身是好的：<see cref="PartTextExtractor"/>（PdfPig）能读到每个词的内容与紧贴外接框，
    /// 所以这里直接用词框把字画回 PNG：
    ///   · 位置 / 宽高 = <see cref="PartText.PixelX"/> …（页面像素）减去零件框左上角，与裁切图 1:1；
    ///   · 字形用系统字体（MS Gothic → Yu Gothic → Meiryo …）按字形墨迹框等比拉伸到词框里；
    ///   · 渲染里已经画出字的词（圈内数字、本来就能正常渲染的英数字）按<b>框内墨迹占比</b>跳过，
    ///     不会出现「同一个字画两遍」的重影。
    ///
    /// 只改 PNG 像素，不动 <c>parts_info.json</c> / <c>parts_text.txt</c> 里的坐标与文字。
    ///
    /// 局限：
    ///   · 字形是系统字体的字形，不是原 PDF 字体的字形（细部形状略有差别，字距按词框整体拉伸）；
    ///   · 竖排文字按横排画（会被压扁）—— 本批图纸的标注基本都是横排；
    ///   · 词框大半落在零件框外的词只画可见的那部分（与裁切图范围一致）。
    /// </summary>
    internal static class PartTextBurner
    {
        /// <summary>
        /// 补画时优先使用的字体（按顺序取系统里装了的第一个）。
        /// 图纸标注是「MS-PGothic / MS-Gothic」风格，先找它，再退到 Windows 自带的日文 / 中文 UI 字体。
        /// </summary>
        private static readonly string[] FontCandidates =
        {
            "MS Gothic",
            "ＭＳ ゴシック",
            "MS PGothic",
            "Yu Gothic",
            "Yu Gothic UI",
            "Meiryo",
            "Meiryo UI",
            "SimSun",
            "SimHei",
            "Microsoft YaHei",
            "Arial Unicode MS"
        };

        /// <summary>
        /// 把各零件图 PNG 里「渲染没画出来」的文字补画上去。
        /// 任何一张失败只记一行告警，不抛异常（零件图已经落盘，不能因为补字毁了整批结果）。
        /// </summary>
        /// <param name="parts">已落盘的零件信息（<see cref="PartInfo.Texts"/> 需已由 <see cref="PartTextExtractor.Attach"/> 填好）。</param>
        /// <param name="opts">参数与日志（输出目录、墨迹跳过阈值都在这里）。</param>
        public static void Burn(List<PartInfo> parts, DetectorOptions opts)
        {
            if (opts == null || parts == null || parts.Count == 0)
                return;

            if (!opts.BurnTextIntoParts)
                return;

            int wordTotal = CountWords(parts);
            if (wordTotal == 0)
                return; // 位图输入 / 没有文字层：什么都不做，也不打日志

            FontFamily family = ResolveFontFamily();
            if (family == null)
            {
                opts.Log("[WARN] 文字补画：系统里找不到可用的字体（MS Gothic / Yu Gothic / Meiryo …），"
                         + "跳过补画 —— 零件图里会缺少日文标注。");
                return;
            }

            int burnedWords = 0;
            int keptWords   = 0;
            int burnedFiles = 0;
            int failedFiles = 0;

            try
            {
                foreach (PartInfo part in parts)
                {
                    if (part == null || part.Texts == null || part.Texts.Count == 0)
                        continue;

                    string pngPath = Path.Combine(opts.OutputDirectory ?? string.Empty, part.FileName);
                    if (!File.Exists(pngPath))
                        continue;

                    // ① 先看每个词框里是不是已经有墨：渲染已经画出字的词跳过
                    List<PartText> missing = SelectMissingWords(pngPath, part, opts);
                    if (missing == null)
                    {
                        failedFiles++;
                        continue;
                    }

                    if (missing.Count == 0)
                    {
                        keptWords += part.Texts.Count;
                        continue;
                    }

                    // ② 把缺的词按词框画回 PNG
                    if (DrawWords(pngPath, part, family, missing, opts))
                    {
                        burnedFiles++;
                        burnedWords += missing.Count;
                        keptWords   += part.Texts.Count - missing.Count;
                    }
                    else
                    {
                        failedFiles++;
                    }
                }
            }
            finally
            {
                family.Dispose();
            }

            if (burnedWords > 0)
            {
                opts.Log("[INFO] 文字补画：" + burnedFiles + " 张零件图补上 " + burnedWords + " 处文字"
                         + "（另有 " + keptWords + " 处渲染里已经画出，跳过）"
                         + (failedFiles > 0 ? "；" + failedFiles + " 张处理失败（见上方告警）" : string.Empty));
            }
            else if (failedFiles > 0)
            {
                opts.Log("[WARN] 文字补画：没有成功补上任何文字（" + failedFiles + " 张处理失败），零件图可能缺少标注。");
            }
            else
            {
                opts.Log("[INFO] 文字补画：该页文字在渲染里都已经画出来了，未改动零件图（" + keptWords + " 处）。");
            }
        }

        /// <summary>统计所有零件上的词数（没有文字层时返回 0，用于「什么都不做」的短路）。</summary>
        private static int CountWords(List<PartInfo> parts)
        {
            int total = 0;
            foreach (PartInfo part in parts)
            {
                if (part == null || part.Texts == null)
                    continue;

                foreach (PartText word in part.Texts)
                {
                    if (word != null && !string.IsNullOrWhiteSpace(word.Text))
                        total++;
                }
            }
            return total;
        }

        /// <summary>
        /// 挑出「零件图上渲染没画出来」的词：词框裁到图内，取框内墨迹占比，
        /// 低于 <see cref="DetectorOptions.BurnTextSkipInkRatio"/> 的视为缺字。
        /// </summary>
        /// <returns>需要补画的词；PNG 读不出来时返回 null（调用方记为失败）。</returns>
        private static List<PartText> SelectMissingWords(string pngPath, PartInfo part, DetectorOptions opts)
        {
            Mat gray = null;
            try
            {
                gray = Cv2.ImRead(pngPath, ImreadModes.Grayscale);
            }
            catch (Exception ex)
            {
                opts.Log("[WARN] 文字补画：读取零件图失败 " + Path.GetFileName(pngPath)
                         + " - " + ex.GetType().Name + ": " + ex.Message);
            }

            if (gray == null || gray.Empty())
            {
                if (gray != null)
                    gray.Dispose();

                opts.Log("[WARN] 文字补画：读取零件图失败 " + Path.GetFileName(pngPath));
                return null;
            }

            var missing = new List<PartText>();

            try
            {
                foreach (PartText word in part.Texts)
                {
                    if (word == null || string.IsNullOrWhiteSpace(word.Text))
                        continue;

                    // 页面绝对像素 → 该零件图的局部像素
                    int x = word.PixelX - part.PixelX;
                    int y = word.PixelY - part.PixelY;

                    // 词框裁到图内（跨在零件框边上的词只补可见的那部分）
                    int x0 = Math.Max(0, x);
                    int y0 = Math.Max(0, y);
                    int x1 = Math.Min(gray.Cols, x + word.PixelW);
                    int y1 = Math.Min(gray.Rows, y + word.PixelH);

                    if (x1 - x0 < 2 || y1 - y0 < 2)
                        continue; // 整个词落在图外

                    var roi = new Rect(x0, y0, x1 - x0, y1 - y0);
                    if (InkRatio(gray, roi) >= opts.BurnTextSkipInkRatio)
                        continue; // 渲染里已经有字（圈内数字、英数字，或压在词框上的线条）

                    missing.Add(word);
                }
            }
            finally
            {
                gray.Dispose();
            }

            return missing;
        }

        /// <summary>框内「暗像素」占比（0~1）。白底黑线 / 黑字的零件图里，就是有墨的比例。</summary>
        private static double InkRatio(Mat gray, Rect roi)
        {
            long area = (long)roi.Width * roi.Height;
            if (area <= 0)
                return 1.0;

            using (var patch = new Mat(gray, roi))
            using (var dark = new Mat())
            {
                // 反向二值化：<=160 的像素变 255（墨迹），其余变 0（纸白）
                Cv2.Threshold(patch, dark, 160, 255, ThresholdTypes.BinaryInv);
                return Cv2.CountNonZero(dark) / (double)area;
            }
        }

        /// <summary>
        /// 把若干词画进 PNG（读进内存 → 画 → 写临时文件 → 覆盖原文件，
        /// 避免 GDI+ 一边读一边写把文件锁死）。
        /// </summary>
        /// <returns>true = 已写回；false = 失败（已记告警）。</returns>
        private static bool DrawWords(string pngPath, PartInfo part, FontFamily family,
                                      List<PartText> words, DetectorOptions opts)
        {
            string tempPath = pngPath + ".burn.tmp.png";

            try
            {
                byte[] bytes = File.ReadAllBytes(pngPath);

                using (var stream = new MemoryStream(bytes, false))
                using (var bitmap = new Bitmap(stream))
                using (var graphics = Graphics.FromImage(bitmap))
                using (var brush = new SolidBrush(System.Drawing.Color.Black))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.CompositingQuality = CompositingQuality.HighQuality;

                    foreach (PartText word in words)
                        DrawOneWord(graphics, brush, family, part, word);

                    bitmap.Save(tempPath, ImageFormat.Png);
                }

                File.Copy(tempPath, pngPath, true);
                return true;
            }
            catch (Exception ex)
            {
                opts.Log("[WARN] 文字补画失败：" + Path.GetFileName(pngPath)
                         + " - " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // 临时文件删不掉不影响结果
                }
                catch (UnauthorizedAccessException)
                {
                    // 同上
                }
            }
        }

        /// <summary>
        /// 画一个词：用 <see cref="GraphicsPath.AddString(string, FontFamily, int, float, PointF, StringFormat)"/>
        /// 取字形轮廓，再把「墨迹外接框」整体仿射到词框（位置、宽高都按文字层给的坐标走），
        /// 于是不同字号 / 不同词长的字都能贴住原来的位置与大小。
        /// </summary>
        private static void DrawOneWord(Graphics graphics, Brush brush, FontFamily family,
                                        PartInfo part, PartText word)
        {
            int x = word.PixelX - part.PixelX;
            int y = word.PixelY - part.PixelY;
            int w = word.PixelW;
            int h = word.PixelH;

            if (w < 2 || h < 2)
                return;

            using (var path = new GraphicsPath())
            using (var format = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                format.FormatFlags |= StringFormatFlags.NoWrap;

                path.AddString(word.Text, family, (int)System.Drawing.FontStyle.Regular, 100f,
                               new PointF(0f, 0f), format);

                RectangleF bounds = path.GetBounds();
                if (bounds.Width <= 0f || bounds.Height <= 0f)
                    return;

                GraphicsState state = graphics.Save();
                try
                {
                    graphics.TranslateTransform(x, y);
                    graphics.ScaleTransform(w / bounds.Width, h / bounds.Height);
                    graphics.TranslateTransform(-bounds.X, -bounds.Y);
                    graphics.FillPath(brush, path);
                }
                finally
                {
                    graphics.Restore(state);
                }
            }
        }

        /// <summary>
        /// 取系统里装了的第一个候选字体；一个都没有返回 null。
        /// 先用 <see cref="InstalledFontCollection"/> 核对再 new，避免「字体不存在」抛异常。
        /// </summary>
        private static FontFamily ResolveFontFamily()
        {
            using (var installed = new InstalledFontCollection())
            {
                foreach (string name in FontCandidates)
                {
                    foreach (FontFamily family in installed.Families)
                    {
                        if (!string.Equals(family.Name, name, StringComparison.OrdinalIgnoreCase))
                            continue;

                        try
                        {
                            return new FontFamily(family.Name);
                        }
                        catch (ArgumentException)
                        {
                            // 字体文件损坏之类：换下一个候选
                        }
                    }
                }
            }

            return null;
        }
    }
}
