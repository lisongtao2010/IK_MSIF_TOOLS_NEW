using System;
using System.Collections.Generic;
using System.IO;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// 图纸文件夹里「Excel 文件」的识别与枚举（【分类结果】一览里与 PDF 一起显示的那一类）。
    /// <para>
    /// PDF 走「开始分解 → CMD 分析 → AI 分析」这条分析链；Excel 只做<b>显示 + 预览</b>：
    /// 它没有分解产物、也没有分类清单（PdfInfoList.txt）的记录，所以一览里单独标成「Excel」，
    /// 不参与分类统计里的「未知」计数，也不进入任何分析步骤（见 Form1.PopulateClassification）。
    /// </para>
    /// <para>
    /// 支持的扩展名 = <see cref="ExcelDataReader"/> 能读的那几种：
    /// <c>.xlsx</c>（OpenXML）、<c>.xlsm</c>（带宏）、<c>.xlsb</c>（二进制）、<c>.xls</c>（BIFF）。
    /// 只扫描文件夹<b>第一层</b>（与 <see cref="PdfSplitService.FindPdfFiles"/> 一致，不进入子文件夹）。
    /// </para>
    /// </summary>
    internal static class ExcelFileCatalog
    {
        /// <summary>视为 Excel 文件的扩展名（不区分大小写，含点）。</summary>
        private static readonly string[] ExcelExtensions = new string[]
        {
            ".xlsx",
            ".xlsm",
            ".xlsb",
            ".xls"
        };

        /// <summary>支持的扩展名（只读，界面上可以拿去显示 / 提示）。</summary>
        public static IList<string> Extensions
        {
            get { return Array.AsReadOnly(ExcelExtensions); }
        }

        /// <summary>
        /// 路径是不是 Excel 文件（按扩展名判断，不检查文件是否存在）。
        /// </summary>
        /// <param name="path">文件路径或文件名（可以为 <c>null</c>）。</param>
        public static bool IsExcelFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string extension;

            try
            {
                extension = Path.GetExtension(path);
            }
            catch (ArgumentException)
            {
                // 路径里有非法字符：当作不是 Excel
                return false;
            }

            if (string.IsNullOrEmpty(extension))
            {
                return false;
            }

            foreach (string candidate in ExcelExtensions)
            {
                if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 找出文件夹第一层里的所有 Excel 文件，返回按序号（Ordinal）排序的<b>文件名</b>（不含路径）。
        /// 文件夹不存在 / 为空时返回空数组（不抛异常：预览是辅助功能，不能影响主流程）。
        /// </summary>
        /// <param name="folder">图纸（PDF）所在文件夹。</param>
        public static string[] FindExcelFiles(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return new string[0];
            }

            List<string> names = new List<string>();

            try
            {
                if (!Directory.Exists(folder))
                {
                    return new string[0];
                }

                foreach (string path in Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly))
                {
                    if (IsExcelFile(path))
                    {
                        names.Add(Path.GetFileName(path));
                    }
                }
            }
            catch (Exception)
            {
                // 没权限 / 路径被拔掉等：当作没有 Excel 文件
                return new string[0];
            }

            // 与 PDF 一览的排序规则一致（序数比较）
            names.Sort(StringComparer.Ordinal);

            return names.ToArray();
        }
    }
}
