using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PdfiumViewer;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// pdfium 原生库（pdfium.dll）的定位与自检。
    /// <para>
    /// PdfiumViewer 2.13.0 的 <c>PdfLibrary</c> 初始化 / 终结时会调用原生
    /// <c>FPDF_AddRef</c> / <c>FPDF_Release</c>；它固定按「exe 目录\x64\Pdfium.dll」找原生库，
    /// 找不到就让 Windows 按名字 <c>pdfium.dll</c> 去 exe 目录 / System32 / PATH 里搜。
    /// 搜到的那份版本不对时（旧版只有 <c>FPDF_InitLibrary</c>、或别的软件自带的 pdfium），
    /// 第一次渲染就抛 <see cref="EntryPointNotFoundException"/>（找不到入口点 FPDF_Release），
    /// 画面上就是「无法显示该 PDF」。
    /// </para>
    /// <para>
    /// 这里先用 <c>LoadLibrary</c> + <c>GetProcAddress</c> 把候选 dll 的导出表检查一遍，
    /// 把<b>确认能用</b>的那份通过 <see cref="PdfiumResolver.Resolve"/> 交给 PdfiumViewer 使用；
    /// 每一份候选的检查结果（不存在 / 加载失败 / 缺哪个导出）记进 <see cref="Diagnostic"/>，
    /// 由主画面日志显示。必须在使用任何 PdfiumViewer 类型<b>之前</b>执行
    /// （<c>Program.Main</c> 里已调用 <see cref="EnsureReady"/>）。
    /// </para>
    /// </summary>
    internal static class PdfiumRuntime
    {
        /// <summary>指定 pdfium.dll 完整路径的环境变量（优先级最高，排查 / 现场替换 dll 时用）。</summary>
        public const string PdfiumPathEnvironmentVariable = "MSIF_PDFIUM_DLL";

        private static readonly object SyncRoot = new object();

        /// <summary>是否已经检测过（检测只做一次，结果缓存）。</summary>
        private static bool _probed;

        /// <summary>是否找到了可用的 pdfium.dll。</summary>
        private static bool _available;

        /// <summary>校验通过、交给 PdfiumViewer 使用的 dll 完整路径（未找到时为 null）。</summary>
        private static string _resolvedPath;

        /// <summary>检测结果说明（可用 = 路径；不可用 = 每份候选失败的原因）。</summary>
        private static string _diagnostic = "尚未检测 pdfium.dll。";

        /// <summary>是否已挂上 <see cref="PdfiumResolver.Resolve"/> 事件。</summary>
        private static bool _resolverHooked;

        /// <summary>是否找到可用的 pdfium.dll（首次访问时自动检测）。</summary>
        public static bool IsAvailable
        {
            get
            {
                EnsureProbed();

                return _available;
            }
        }

        /// <summary>校验通过的 pdfium.dll 完整路径（不可用时为 null）。</summary>
        public static string ResolvedPath
        {
            get
            {
                EnsureProbed();

                return _resolvedPath;
            }
        }

        /// <summary>检测结果说明（可直接写进日志）。</summary>
        public static string Diagnostic
        {
            get
            {
                EnsureProbed();

                return _diagnostic;
            }
        }

        /// <summary>执行（或复用）检测，只关心成功与否时用这个。</summary>
        public static bool EnsureReady()
        {
            string message;

            return EnsureAvailable(out message);
        }

        /// <summary>
        /// 执行（或复用）检测。<paramref name="message"/> 返回检测结果说明：
        /// 成功时是实际使用的 dll 路径，失败时是每份候选的失败原因与处理建议。
        /// </summary>
        /// <param name="message">检测结果说明。</param>
        /// <returns>找到可用的 pdfium.dll 时返回 true。</returns>
        public static bool EnsureAvailable(out string message)
        {
            EnsureProbed();

            message = _diagnostic;

            return _available;
        }

        /// <summary>检测一次并缓存结果（线程安全）。</summary>
        private static void EnsureProbed()
        {
            lock (SyncRoot)
            {
                if (_probed)
                {
                    return;
                }

                _probed = true;

                try
                {
                    Probe();
                }
                catch (Exception ex)
                {
                    // 找不到 PdfiumViewer.dll / kernel32 调用失败等：按「不可用」处理，不影响其它功能
                    _available = false;
                    _resolvedPath = null;
                    _diagnostic = "检测 pdfium.dll 时发生异常：" + ex.Message;
                }
            }
        }

        /// <summary>
        /// 依次检查候选 dll，第一个通过校验的即为本次使用的原生库，
        /// 并通过 <see cref="PdfiumResolver.Resolve"/> 把它发给 PdfiumViewer。
        /// </summary>
        private static void Probe()
        {
            List<string> notes = new List<string>();

            foreach (string candidate in GetCandidatePaths())
            {
                string note;

                if (TryValidate(candidate, out note))
                {
                    _available = true;
                    _resolvedPath = Path.GetFullPath(candidate);

                    HookResolver();

                    _diagnostic = "已使用 " + _resolvedPath;

                    return;
                }

                notes.Add(note);
            }

            // 程序目录里没有可用的：再试「按名字 pdfium.dll」实际能解析到的那一份。
            // PdfiumViewer 找不到 x64\Pdfium.dll 时就是这么找的 —— 常见的坑是这一步拿到了
            // 别的软件（或 System32 / PATH 上）的旧 pdfium.dll，于是报「找不到入口点 FPDF_Release」。
            string byNamePath = ResolveByName();

            if (!string.IsNullOrEmpty(byNamePath))
            {
                string byNameNote;

                if (TryValidate(byNamePath, out byNameNote))
                {
                    _available = true;
                    _resolvedPath = Path.GetFullPath(byNamePath);

                    HookResolver();

                    _diagnostic = "已使用（按名字解析）" + _resolvedPath;

                    return;
                }

                notes.Add(byNameNote);
            }

            _available = false;
            _resolvedPath = null;

            string processBits = (IntPtr.Size * 8) + " 位进程";
            string platformFolder = IntPtr.Size == 8 ? "x64" : "x86";
            string nativePackage = IntPtr.Size == 8 ? "x86_64" : "x86";

            if (notes.Count == 0)
            {
                _diagnostic = string.Format(
                    "没有找到 pdfium.dll（{0}）。请把 PdfiumViewer.Native.{1}.v8-xfa 包里的 {2}\\pdfium.dll " +
                    "放到程序目录下的同名子目录，或用环境变量 {3} 指定完整路径。",
                    processBits, nativePackage, platformFolder, PdfiumPathEnvironmentVariable);

                return;
            }

            _diagnostic = string.Format(
                "找不到可用的 pdfium.dll（{0}）：{1}。" +
                "PdfiumViewer 2.13.0 需要 FPDF_AddRef / FPDF_Release，请用 PdfiumViewer.Native.x86_64.v8-xfa" +
                "（2018.4.8.256 或更新）里的 x64\\pdfium.dll 覆盖程序目录下的那一份，" +
                "或用环境变量 {2} 指定正确的 dll。",
                processBits,
                string.Join("；", notes.ToArray()),
                PdfiumPathEnvironmentVariable);
        }

        /// <summary>
        /// 候选 dll 路径（按优先级）：环境变量 → 程序目录下位数对应的子目录 → 程序目录根。
        /// PdfiumViewer 自己的规则是「exe 目录\x64\Pdfium.dll」，所以子目录那份排在最前。
        /// </summary>
        private static List<string> GetCandidatePaths()
        {
            List<string> paths = new List<string>();

            string fromEnvironment = Environment.GetEnvironmentVariable(PdfiumPathEnvironmentVariable);

            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                string explicitPath = fromEnvironment.Trim().Trim('"');

                if (explicitPath.Length > 0)
                {
                    paths.Add(explicitPath);
                }
            }

            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string platformFolder = IntPtr.Size == 8 ? "x64" : "x86";

            AddCandidate(paths, Path.Combine(baseDirectory, platformFolder, "pdfium.dll"));
            AddCandidate(paths, Path.Combine(baseDirectory, "pdfium.dll"));

            return paths;
        }

        /// <summary>加入候选列表（忽略空值与重复项）。</summary>
        private static void AddCandidate(List<string> paths, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            for (int index = 0; index < paths.Count; index++)
            {
                if (string.Equals(paths[index], path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            paths.Add(path);
        }

        /// <summary>
        /// 按名字 <c>pdfium.dll</c> 让 Windows 自己找（exe 目录 → System32 → PATH），
        /// 返回实际解析到的文件完整路径（找不到时返回 null）。
        /// </summary>
        private static string ResolveByName()
        {
            IntPtr module = LoadLibrary("pdfium.dll");

            if (module == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                StringBuilder buffer = new StringBuilder(1024);

                if (GetModuleFileName(module, buffer, buffer.Capacity) == 0)
                {
                    return null;
                }

                return buffer.ToString();
            }
            finally
            {
                FreeLibrary(module);
            }
        }

        /// <summary>
        /// 校验一份候选 dll：文件存在 → 能被 LoadLibrary 加载 → 导出表里有 PdfiumViewer 2.13.0
        /// 必需的 <c>FPDF_AddRef</c> / <c>FPDF_Release</c> / <c>FPDF_LoadDocument</c>。
        /// <para>
        /// 校验失败会立即 FreeLibrary：DllImport 是按模块名（"pdfium.dll"）匹配已加载模块的，
        /// 把不匹配的那份留在进程里，后面就可能被误用。
        /// </para>
        /// </summary>
        /// <param name="path">候选 dll 路径。</param>
        /// <param name="note">校验结果说明（失败原因）。</param>
        /// <returns>可以使用时返回 true。</returns>
        private static bool TryValidate(string path, out string note)
        {
            if (!File.Exists(path))
            {
                note = path + "（不存在）";

                return false;
            }

            IntPtr module = LoadLibrary(path);

            if (module == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();

                // 位数不对（32 位 dll 放进 x64 进程）时 Win32 错误码是 193（不是有效的 Win32 应用程序）
                note = string.Format("{0}（加载失败：Win32 {1} {2}）",
                    path, error, new Win32Exception(error).Message);

                return false;
            }

            bool hasAddRef = GetProcAddress(module, "FPDF_AddRef") != IntPtr.Zero;
            bool hasRelease = GetProcAddress(module, "FPDF_Release") != IntPtr.Zero;
            bool hasLoadDocument = GetProcAddress(module, "FPDF_LoadDocument") != IntPtr.Zero;
            bool hasLegacyInit = GetProcAddress(module, "FPDF_InitLibrary") != IntPtr.Zero;

            if (hasAddRef && hasRelease && hasLoadDocument)
            {
                // 校验通过：故意保持加载状态。PdfiumViewer 之后 LoadLibrary 同一个文件只会增加引用计数，
                // 不会再按名字去找别的 pdfium.dll。
                note = path + "（可用）";

                return true;
            }

            FreeLibrary(module);

            List<string> missing = new List<string>();

            if (!hasAddRef)
            {
                missing.Add("FPDF_AddRef");
            }

            if (!hasRelease)
            {
                missing.Add("FPDF_Release");
            }

            if (!hasLoadDocument)
            {
                missing.Add("FPDF_LoadDocument");
            }

            note = path + "（缺导出：" + string.Join("、", missing.ToArray());

            if (!hasAddRef && !hasRelease && hasLegacyInit)
            {
                note += "；这是只有 FPDF_InitLibrary 的旧版 pdfium，PdfiumViewer 2.13 用不了";
            }

            note += "）";

            return false;
        }

        /// <summary>
        /// 挂上 PdfiumViewer 的原生库解析事件：<see cref="PdfiumResolver.Resolve"/> 在
        /// <c>NativeMethods</c> 的静态构造里被调用，返回的路径存在且能 LoadLibrary 时
        /// PdfiumViewer 就不再自己去找 dll。
        /// </summary>
        private static void HookResolver()
        {
            if (_resolverHooked)
            {
                return;
            }

            _resolverHooked = true;
            PdfiumResolver.Resolve += PdfiumResolver_Resolve;
        }

        /// <summary>
        /// 把校验过的 dll 路径交给 PdfiumViewer。
        /// 这里不能抛异常：它是在 PdfiumViewer 的静态构造里被调用的，抛出去会让整个类型初始化失败。
        /// </summary>
        private static void PdfiumResolver_Resolve(object sender, PdfiumResolveEventArgs e)
        {
            if (!string.IsNullOrEmpty(_resolvedPath))
            {
                e.PdfiumFileName = _resolvedPath;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string fileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetModuleFileName(IntPtr module, StringBuilder fileName, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, BestFitMapping = false)]
        private static extern IntPtr GetProcAddress(IntPtr module, string procName);
    }
}
