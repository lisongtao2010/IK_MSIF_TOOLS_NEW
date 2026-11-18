using System;
using System.Collections.Generic;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Graphics;

namespace CadPartSplitter
{
    /// <summary>
    /// 一页 PDF 的「解析结果快照」：页面尺寸 + 文字层的全部词 + 全部矢量路径。
    ///
    /// 为什么要这一层：文字坐标（<see cref="PartTextExtractor"/>）、圈号标注（<see cref="CalloutExtractor"/>）、
    /// 有线表格（<see cref="RuledTableExtractor"/>）、要領図一覧（<see cref="AssemblyYoryoBuilder"/>）
    /// 原本各自 <c>PdfDocument.Open(pdfPath)</c> 一次 —— 同一页 PDF 在一次运行里被打开、解析 4~5 遍。
    /// PdfPig 的开销主要就在「解析交叉引用表 + 解析页面内容流 + 切词」这几步上（与读几个字节无关），
    /// 重复打开是纯浪费。现在由 <see cref="PartSplitterService.Run"/> 在流程开头 <see cref="Load"/> 一次，
    /// 把词与路径直接交给各模块；各模块保留原来的 <c>string</c> 重载，内部同样走这里（单次调用时行为不变）。
    ///
    /// 内容在 <see cref="Load"/> 内就全部取出来：<c>page.GetWords()</c> 返回的 <c>Word</c> 在构造时
    /// 已算好文本与外接框（不再依赖字体 / 文档对象），<c>PdfPath</c> 只是几何数据 ——
    /// 所以返回后 PDF 句柄即可关闭，不占文件、也不用把 <see cref="PdfDocument"/> 留在内存里。
    /// </summary>
    public sealed class PdfPageContent
    {
        /// <summary>解析失败 / 页码越界 / 页面尺寸无效时的空对象：<see cref="IsValid"/> 为 false，两个列表都为空（非 null）。</summary>
        public static readonly PdfPageContent Empty =
            new PdfPageContent(0, 0, 0, new Word[0], new PdfPath[0]);

        private PdfPageContent(int pageNumber, double width, double height,
                               IReadOnlyList<Word> words, IReadOnlyList<PdfPath> paths)
        {
            PageNumber = pageNumber;
            Width      = width;
            Height     = height;
            Words      = words;
            Paths      = paths;
        }

        /// <summary>页码（PdfPig 口径，从 1 开始）；无效时为 0。</summary>
        public int PageNumber { get; }

        /// <summary>页面宽（PDF 单位 pt）；无效时为 0。</summary>
        public double Width { get; }

        /// <summary>页面高（pt）；无效时为 0。</summary>
        public double Height { get; }

        /// <summary>文字层的全部词（PdfPig 切好词，坐标为用户空间 pt、原点左下、Y 向上）。</summary>
        public IReadOnlyList<Word> Words { get; }

        /// <summary>页面内容流里的全部矢量路径（坐标已应用 CTM，与文字层同一口径）。</summary>
        public IReadOnlyList<PdfPath> Paths { get; }

        /// <summary>页面尺寸是否可用（false 表示 <see cref="Empty"/> 或解析失败）。</summary>
        public bool IsValid
        {
            get { return Width > 0 && Height > 0; }
        }

        /// <summary>词数（省得调用方再判 null）。</summary>
        public int WordCount
        {
            get { return Words.Count; }
        }

        /// <summary>矢量路径条数（省得调用方再判 null）。</summary>
        public int PathCount
        {
            get { return Paths.Count; }
        }

        /// <summary>
        /// 打开 PDF 并把指定页的「词 + 矢量路径 + 页面尺寸」一次读出来。
        ///
        /// 页码越界 / 页面尺寸无效 / 解析异常都只写日志并返回 <see cref="Empty"/>，不抛异常 ——
        /// 文字层只是附加信息，任何一张图纸读不到都不该中断分割流程。
        /// 同一页在一次运行里只需调用一次（结果被文字 / 圈号 / 表格 / 要領図 四个模块共用）。
        /// </summary>
        /// <param name="pdfPath">PDF 路径；为空时直接返回 <see cref="Empty"/>（不写日志）。</param>
        /// <param name="pageIndex">页码（从 0 开始）。</param>
        /// <param name="log">日志回调（可为 null）。</param>
        public static PdfPageContent Load(string pdfPath, int pageIndex, Action<string> log)
        {
            Action<string> write = log ?? (_ => { });

            if (string.IsNullOrEmpty(pdfPath))
                return Empty;

            try
            {
                using (var doc = PdfDocument.Open(pdfPath))
                {
                    if (pageIndex < 0 || pageIndex >= doc.NumberOfPages)
                    {
                        write("[WARN] PDF 页码超出范围（第 " + (pageIndex + 1) + " 页，共 "
                              + doc.NumberOfPages + " 页），跳过文字层 / 矢量层读取。");
                        return Empty;
                    }

                    var page = doc.GetPage(pageIndex + 1);   // PdfPig 页码从 1 开始

                    double width  = page.Width;
                    double height = page.Height;

                    if (width <= 0 || height <= 0)
                    {
                        write("[WARN] PDF 页面尺寸无效（" + width.ToString("0.#") + "×"
                              + height.ToString("0.#") + " pt），跳过文字层 / 矢量层读取。");
                        return Empty;
                    }

                    // 词在构造时已算好文本与外接框；Paths 在解析内容流时已成型，
                    // 这里只是把引用收进一个列表，不会重新解析页面。
                    var words = new List<Word>(page.GetWords());
                    var paths = new List<PdfPath>(page.ExperimentalAccess.Paths);

                    write("[INFO] PDF 页面解析（本页只解析一次，文字 / 圈号 / 表格共用）：词 "
                          + words.Count + " 个 / 矢量路径 " + paths.Count + " 条（页面 "
                          + width.ToString("0.#") + "×" + height.ToString("0.#") + " pt）。");

                    return new PdfPageContent(page.Number, width, height, words, paths);
                }
            }
            catch (Exception ex)
            {
                write("[WARN] PDF 页面解析失败（文字层 / 矢量层，后续按无文字层处理）：" + ex.Message);
                return Empty;
            }
        }
    }
}
