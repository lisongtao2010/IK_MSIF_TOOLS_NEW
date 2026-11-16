using System;
using System.Collections.Generic;
using System.Windows.Forms;
using MSIFAITOOLS.Services;

namespace MSIFAITOOLS
{
    static class Program
    {
        /// <summary>
        /// 应用程序的主入口点。
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            PrepareMemberPartDatabase();
            PreparePdfRenderer();

            Application.Run(new Form1());
        }

        /// <summary>
        /// 定位并自检 PDF 预览用的原生库（pdfium.dll）。
        /// <para>
        /// 必须在任何 PdfiumViewer 调用之前执行：PdfiumViewer 2.13.0 的 <c>PdfLibrary</c> 初始化 /
        /// 终结时要调原生 <c>FPDF_AddRef</c> / <c>FPDF_Release</c>，它自己只按「exe 目录\x64\Pdfium.dll」
        /// 找，找不到就按名字让 Windows 去别处搜（PATH / System32 里别的 pdfium.dll），
        /// 版本不匹配时预览直接报「找不到入口点 FPDF_Release」。
        /// </para>
        /// <para>
        /// 失败不影响启动：只是 PDF 预览不可用，原因由 <see cref="PdfiumRuntime.Diagnostic"/>
        /// 写进主画面运行日志（见 <c>Form1.ShowPdfiumInfo</c>）。
        /// </para>
        /// </summary>
        private static void PreparePdfRenderer()
        {
            try
            {
                PdfiumRuntime.EnsureReady();
            }
            catch (Exception)
            {
                // 自检本身出问题也不影响启动（预览时会再检查一次并给出提示）
            }
        }

        /// <summary>
        /// 准备部材部品マスタ 的 SQLite 数据库（默认 <c>D:\MSIFDB\MSIF_MemberParts.db</c>）：
        /// 第一次启动时把 <c>部材部品\部材部品.csv</c> 导入「部材部品」表，之后只读数据库。
        /// 失败不影响启动（【AI 分析】时会再试一次，并把原因写进日志）。
        /// </summary>
        private static void PrepareMemberPartDatabase()
        {
            try
            {
                MemberPartCatalogLoader.LoadFor(null, new List<string>());
            }
            catch (Exception)
            {
                // 数据库 / CSV 都不可用时【AI 分析】只用图纸上的文字，这里静默跳过
            }
        }
    }
}

