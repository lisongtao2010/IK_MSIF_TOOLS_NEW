using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MSIFAITOOLS
{
    /// <summary>
    /// 「部品基本信息」与「PDF 预览」两个窗口的摆放规则。
    /// <para>
    /// 有 2 块以上屏幕时：<b>PDF 预览放到主画面（Form1）以外的屏幕</b> —— 预览窗口不要开在
    /// 主画面所在的那块屏上（会和主画面抢同一块屏、把一览表格挡住）；
    /// 部品基本信息放到主画面所在的屏幕（和预览窗口一屏一个，成对使用）。
    /// </para>
    /// <para>只有 1 块屏幕时：PDF 预览占左侧、部品基本信息占右侧，尽量互不遮挡。</para>
    /// <para>
    /// 另外负责 <b>z 顺序</b>（<see cref="PlaceBehindMainWindowIfOverlapping"/>）：
    /// 两个辅助窗口都不能「一直保持在主画面前边」—— 主画面被点到 / 激活时必须能盖住它们。
    /// </para>
    /// <para>
    /// 还有一个「贴在表格单元格旁边」的小工具（<see cref="PlacePopupNextToCell"/>）：
    /// 「选择部材名」面板用它摆到点到的「部材名」单元格<b>下面那一行</b>（不遮挡选中的行）。
    /// </para>
    /// </summary>
    internal static class WindowLayoutHelper
    {
        /// <summary>拿不到任何屏幕信息时用的兜底工作区（只影响位置计算，不影响功能）。</summary>
        private static readonly Rectangle FallbackWorkingArea = new Rectangle(0, 0, 1280, 800);

        /// <summary>PDF 预览窗口允许的最小宽度（与 PdfPreviewForm.MinimumSize 的宽度一致）。</summary>
        internal const int PreviewMinWidth = 760;

        /// <summary>部品基本信息窗口允许的最小宽度（与 PartBasicInfoForm.MinimumSize 的宽度一致）。</summary>
        internal const int EditorMinWidth = 860;

        /// <summary>单屏并排时部品基本信息窗口占工作区宽度的比例。</summary>
        private const double EditorWidthRatio = 0.45;

        /// <summary>
        /// 首次打开时摆放两个窗口：PDF 预览 → <b>主画面以外的屏幕</b>，
        /// 部品基本信息 → 主画面所在的屏幕（只有 1 块屏幕时左右并排）。
        /// </summary>
        /// <param name="previewForm">PDF 预览窗口。</param>
        /// <param name="editorForm">部品基本信息窗口。</param>
        /// <param name="mainForm">
        /// 主画面（通常是 Form1）：预览窗口会避开它所在的屏幕。传 <c>null</c> 时按主屏处理。
        /// </param>
        internal static void ArrangePreviewAndEditor(Form previewForm, Form editorForm, Form mainForm = null)
        {
            Rectangle previewBounds;
            Rectangle editorBounds;

            SplitForCurrentScreens(ResolveMainWorkingArea(mainForm), out previewBounds, out editorBounds);

            ApplyBounds(previewForm, previewBounds);
            ApplyBounds(editorForm, editorBounds);
        }

        /// <summary>
        /// 只摆放 PDF 预览窗口（用户关闭预览后再次打开时调用，不移动部品基本信息窗口）：
        /// 位置 = 主画面以外的屏幕（只有 1 块屏幕时占左侧）。
        /// </summary>
        /// <param name="previewForm">PDF 预览窗口。</param>
        /// <param name="mainForm">
        /// 主画面（通常是 Form1）：预览窗口会避开它所在的屏幕。传 <c>null</c> 时按主屏处理。
        /// </param>
        internal static void ArrangePreview(Form previewForm, Form mainForm = null)
        {
            Rectangle previewBounds;
            Rectangle editorBounds;

            SplitForCurrentScreens(ResolveMainWorkingArea(mainForm), out previewBounds, out editorBounds);

            ApplyBounds(previewForm, previewBounds);
        }

        /// <summary>
        /// 只摆放部品基本信息窗口（PDF 预览窗口已由其他画面打开并摆好时调用）：
        /// 位置 = 主画面所在的屏幕（只有 1 块屏幕时占右侧）。
        /// </summary>
        /// <param name="editorForm">部品基本信息窗口。</param>
        /// <param name="mainForm">
        /// 主画面（通常是 Form1）。传 <c>null</c> 时按主屏处理。
        /// </param>
        internal static void ArrangeEditor(Form editorForm, Form mainForm = null)
        {
            Rectangle previewBounds;
            Rectangle editorBounds;

            SplitForCurrentScreens(ResolveMainWorkingArea(mainForm), out previewBounds, out editorBounds);

            ApplyBounds(editorForm, editorBounds);
        }

        /// <summary>
        /// 按当前屏幕数量计算两个窗口的位置。
        /// </summary>
        /// <param name="mainArea">主画面所在屏幕的工作区（PDF 预览窗口会避开它）。</param>
        /// <param name="previewBounds">PDF 预览窗口的目标位置。</param>
        /// <param name="editorBounds">部品基本信息窗口的目标位置。</param>
        private static void SplitForCurrentScreens(Rectangle mainArea,
            out Rectangle previewBounds, out Rectangle editorBounds)
        {
            Rectangle[] workingAreas = GetOrderedWorkingAreas(mainArea);

            if (workingAreas.Length >= 2)
            {
                SplitAcrossScreens(workingAreas, out previewBounds, out editorBounds);
            }
            else if (workingAreas.Length == 1)
            {
                SplitOnSingleScreen(workingAreas[0], out previewBounds, out editorBounds);
            }
            else
            {
                SplitOnSingleScreen(FallbackWorkingArea, out previewBounds, out editorBounds);
            }
        }

        /// <summary>
        /// 取「主画面」所在屏幕的工作区：PDF 预览窗口要避开这块屏幕
        /// （多屏时不要把预览窗口开在主画面所在的那块屏上）。
        /// <para>
        /// 主画面 = <paramref name="mainForm"/>（通常是 Form1）；
        /// 传 <c>null</c> / 窗口已释放时按主屏（<see cref="Screen.PrimaryScreen"/>）处理。
        /// </para>
        /// </summary>
        /// <param name="mainForm">主画面窗口（可以为 <c>null</c>）。</param>
        /// <returns>主画面所在屏幕的工作区。</returns>
        private static Rectangle ResolveMainWorkingArea(Form mainForm)
        {
            if (mainForm != null && !mainForm.IsDisposed)
            {
                Screen mainScreen = Screen.FromControl(mainForm);

                if (mainScreen != null)
                {
                    return mainScreen.WorkingArea;
                }
            }

            Screen primary = Screen.PrimaryScreen;

            return primary != null ? primary.WorkingArea : FallbackWorkingArea;
        }

        /// <summary>
        /// 按「<b>主画面以外的屏幕</b> 在前、主画面所在屏幕 在后」的顺序返回各屏幕工作区。
        /// <para>
        /// 下标 0 = PDF 预览窗口用的屏幕（不和主画面挤在一块屏上），
        /// 最后一块 = 部品基本信息窗口用的屏幕（主画面所在的那块）。
        /// <see cref="Screen.AllScreens"/> 的下标并不保证主屏 / 主画面在最前，所以这里显式排序。
        /// </para>
        /// </summary>
        /// <param name="mainArea">主画面所在屏幕的工作区。</param>
        /// <returns>排好序的各屏幕工作区。</returns>
        private static Rectangle[] GetOrderedWorkingAreas(Rectangle mainArea)
        {
            Screen[] screens = Screen.AllScreens;

            if (!IsScreenWorkingArea(mainArea))
            {
                // 主画面工作区不在任何屏幕上（多屏信息刚变化 / 传进来的是兜底矩形）：
                // 退回「主屏 = 主画面」的老行为，至少不会把窗口摆到屏幕外面
                Screen primary = Screen.PrimaryScreen;
                mainArea = primary != null ? primary.WorkingArea : mainArea;
            }

            List<Rectangle> otherAreas = new List<Rectangle>(screens.Length);
            List<Rectangle> mainAreas = new List<Rectangle>(1);

            for (int i = 0; i < screens.Length; i++)
            {
                Screen screen = screens[i];

                if (screen == null)
                {
                    continue;
                }

                Rectangle area = screen.WorkingArea;

                if (area.Equals(mainArea))
                {
                    mainAreas.Add(area);
                }
                else
                {
                    otherAreas.Add(area);
                }
            }

            // 主画面所在屏幕排在最后 → 有 2 块屏时预览窗口一定落在「另一块屏」上
            otherAreas.AddRange(mainAreas);

            return otherAreas.ToArray();
        }

        /// <summary>这个工作区是不是某一块屏幕的工作区（多屏信息不全时用来兜底判断）。</summary>
        /// <param name="area">要判断的工作区矩形。</param>
        /// <returns>true = 是当前某块屏幕的工作区。</returns>
        private static bool IsScreenWorkingArea(Rectangle area)
        {
            Screen[] screens = Screen.AllScreens;

            for (int i = 0; i < screens.Length; i++)
            {
                if (screens[i] != null && screens[i].WorkingArea.Equals(area))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 多屏：<paramref name="workingAreas"/>[0]（= <b>主画面以外的屏幕</b>）给 PDF 预览，
        /// <b>最后一块</b>（= 主画面所在的屏幕）给部品基本信息
        /// （3 块以上屏幕时中间那些屏幕不用，主画面照样看得见）。
        /// 副屏在主屏左侧 / 上方时工作区坐标可能为负值，这里直接使用工作区矩形。
        /// </summary>
        /// <param name="workingAreas">各屏幕的工作区（下标 0 = 主画面以外的屏幕，最后一块 = 主画面所在屏幕）。</param>
        /// <param name="previewBounds">PDF 预览窗口的目标位置。</param>
        /// <param name="editorBounds">部品基本信息窗口的目标位置。</param>
        internal static void SplitAcrossScreens(Rectangle[] workingAreas, out Rectangle previewBounds, out Rectangle editorBounds)
        {
            if (workingAreas == null || workingAreas.Length == 0)
            {
                throw new ArgumentException("至少需要一个屏幕工作区。", "workingAreas");
            }

            previewBounds = workingAreas[0];
            editorBounds = workingAreas[workingAreas.Length - 1];
        }

        /// <summary>
        /// 单屏：左侧给 PDF 预览、右侧给部品基本信息；工作区太窄时按窗口最小宽度分配。
        /// </summary>
        /// <param name="area">屏幕工作区。</param>
        /// <param name="previewBounds">PDF 预览窗口的目标位置。</param>
        /// <param name="editorBounds">部品基本信息窗口的目标位置。</param>
        internal static void SplitOnSingleScreen(Rectangle area, out Rectangle previewBounds, out Rectangle editorBounds)
        {
            int editorWidth = (int)Math.Round(area.Width * EditorWidthRatio);

            if (editorWidth < EditorMinWidth)
            {
                editorWidth = Math.Min(EditorMinWidth, area.Width);
            }

            int previewWidth = area.Width - editorWidth;

            if (previewWidth < PreviewMinWidth)
            {
                previewWidth = Math.Min(PreviewMinWidth, area.Width);
                editorWidth = area.Width - previewWidth;
            }

            previewBounds = new Rectangle(area.Left, area.Top, previewWidth, area.Height);
            editorBounds = new Rectangle(area.Left + previewWidth, area.Top, editorWidth, area.Height);
        }

        /// <summary>把窗口移动到指定矩形（之后用户仍可自由拖动；只在首次打开时调用）。</summary>
        /// <param name="form">要摆放的窗口。</param>
        /// <param name="bounds">目标位置（屏幕坐标）。</param>
        internal static void ApplyBounds(Form form, Rectangle bounds)
        {
            if (form == null || form.IsDisposed)
            {
                return;
            }

            if (form.WindowState != FormWindowState.Normal)
            {
                form.WindowState = FormWindowState.Normal;
            }

            // 设计器里是 CenterScreen（以前是 CenterParent），必须先改成 Manual，否则 Bounds 会被居中逻辑覆盖
            form.StartPosition = FormStartPosition.Manual;
            form.Bounds = bounds;
        }

        /// <summary>
        /// 辅助窗口（PDF 预览）和主画面显示区域<b>有重叠</b>时，把它放到主画面<b>后面</b>
        /// （只改 z 顺序，不移动 / 不激活）。
        /// <para>
        /// 为什么需要：预览窗口是用 <see cref="Form.ShowWithoutActivation"/>（不抢焦点）显示的，
        /// 这时<b>主画面一直是激活窗口</b>，用户再点主画面不会触发激活、不会自动盖住预览窗口 ——
        /// 预览就会「一直保持在主画面前边」，主画面被挡着却拿不回来。
        /// 有重叠时把预览插到主画面下面：主画面随时可见 / 可操作，点一下预览窗口仍可把它提到前面。
        /// </para>
        /// <para>
        /// 不重叠时（多屏：预览在主画面以外的屏幕）什么都不做 —— z 顺序不影响各自屏幕上的显示。
        /// </para>
        /// </summary>
        /// <param name="window">辅助窗口（PDF 预览）。</param>
        /// <param name="mainForm">主画面（Form1）。</param>
        internal static void PlaceBehindMainWindowIfOverlapping(Form window, Form mainForm)
        {
            if (window == null || window.IsDisposed || !window.Visible ||
                mainForm == null || mainForm.IsDisposed || !mainForm.Visible)
            {
                return;
            }

            if (mainForm.WindowState == FormWindowState.Minimized)
            {
                return;
            }

            if (!window.Bounds.IntersectsWith(mainForm.Bounds))
            {
                return;
            }

            PlaceBehind(window, mainForm);
        }

        /// <summary>把 <paramref name="window"/> 插到 <paramref name="reference"/> 的<b>紧后面</b>（z 顺序）。</summary>
        /// <param name="window">要下沉的窗口。</param>
        /// <param name="reference">保持在它前面的窗口。</param>
        private static void PlaceBehind(Form window, Form reference)
        {
            if (window == null || window.IsDisposed || !window.IsHandleCreated ||
                reference == null || reference.IsDisposed || !reference.IsHandleCreated)
            {
                return;
            }

            // hWndInsertAfter = reference：窗口排在 reference 后面（reference 保持在最前）
            SetWindowPos(window.Handle, reference.Handle, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
        }

        /// <summary>
        /// 把辅助窗口（PDF 预览）提到<b>最前，但不激活</b>（焦点 / 键盘输入仍留在原来的窗口）。
        /// <para>
        /// 只在调用方<b>确实需要</b>把辅助窗口摆到前面时用。
        /// 「部品基本信息画面选中记录 → PDF 预览闪红框」<b>不再调用本方法</b>：
        /// 每换一行提一次最前，预览窗口会在屏幕上跳一下，也会把用户故意放到后面的窗口拉到前面。
        /// </para>
        /// <para>
        /// 不用 <see cref="Control.BringToFront"/>：它内部是 <c>SetWindowPos(HWND_TOP)</c>，
        /// 不带 <c>SWP_NOACTIVATE</c> 时会把预览窗口激活、抢走表格的焦点
        /// （选中一行后方向键就跑不动了）。
        /// </para>
        /// </summary>
        /// <param name="window">要提到最前的窗口（已释放 / 没有句柄时什么都不做）。</param>
        internal static void BringToFrontWithoutActivate(Form window)
        {
            if (window == null || window.IsDisposed || !window.IsHandleCreated)
            {
                return;
            }

            // hWndInsertAfter = HWND_TOP(0)：排到最前；SWP_NOACTIVATE：不抢焦点
            SetWindowPos(window.Handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }

        /// <summary>
        /// 把浮动面板（「选择部材名」窗口等）摆到<b>目标单元格下面那一行</b>：
        /// 面板上边贴着单元格的下边（留 <paramref name="gap"/> 像素空隙）、左边和这一格对齐，
        /// 所以<b>不会盖住选中的这一行</b>，又离点到的格子很近。
        /// <para>
        /// 下方放不下（超出屏幕工作区）→ 改放到单元格<b>上面</b>（= 上面那一行）；
        /// 上面也放不下 → 贴住工作区底部（尽量少压住这一行）。
        /// 横向超出工作区时往左挪，仍然超出就贴住工作区。
        /// </para>
        /// </summary>
        /// <param name="popup">要摆放的浮动面板（已释放 / 没有有效大小时什么都不做）。</param>
        /// <param name="cellScreenBounds">
        /// 目标单元格在<b>屏幕</b>坐标里的矩形（<c>DataGridView.RectangleToScreen</c> 的结果；
        /// 宽 / 高 ≤ 0 时什么都不做）。
        /// </param>
        /// <param name="gap">面板与单元格之间的空隙（像素）。</param>
        internal static void PlacePopupNextToCell(Form popup, Rectangle cellScreenBounds, int gap = 2)
        {
            if (popup == null || popup.IsDisposed ||
                cellScreenBounds.Width <= 0 || cellScreenBounds.Height <= 0)
            {
                return;
            }

            Size size = popup.Size;

            if (size.Width <= 0 || size.Height <= 0)
            {
                return;
            }

            Rectangle workArea = ResolveWorkArea(cellScreenBounds);

            // 横向：优先和单元格左边对齐；右边超出工作区就往左挪，还超出就贴住工作区（面板比屏幕还宽时）
            int left = cellScreenBounds.Left;

            if (left + size.Width > workArea.Right)
            {
                left = cellScreenBounds.Right - size.Width;
            }

            left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - size.Width));

            // 纵向：先放下方（= 选择行的下一行）；放不下改放上方（= 上一行）；都放不下就贴住工作区底部
            int top = cellScreenBounds.Bottom + gap;

            if (top + size.Height > workArea.Bottom)
            {
                int above = cellScreenBounds.Top - gap - size.Height;

                top = above >= workArea.Top
                    ? above
                    : Math.Max(workArea.Top, workArea.Bottom - size.Height);
            }

            popup.Location = new Point(left, top);
        }

        /// <summary>屏幕矩形所在的那块屏幕的工作区（拿不到屏幕信息时用兜底工作区）。</summary>
        private static Rectangle ResolveWorkArea(Rectangle screenBounds)
        {
            Screen screen = Screen.FromRectangle(screenBounds);

            return screen == null ? FallbackWorkingArea : screen.WorkingArea;
        }

        /// <summary>SetWindowPos：不改变位置。</summary>
        private const uint SwpNoMove = 0x0002;

        /// <summary>SetWindowPos：不改变大小。</summary>
        private const uint SwpNoSize = 0x0001;

        /// <summary>SetWindowPos：不激活窗口（主画面保持激活 / 焦点不变）。</summary>
        private const uint SwpNoActivate = 0x0010;

        /// <summary>SetWindowPos：不改变 Owner 的 z 顺序。</summary>
        private const uint SwpNoOwnerZOrder = 0x0200;

        /// <summary>调整窗口的 z 顺序（不移动、不改变大小、不激活）。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint uFlags);
    }
}
