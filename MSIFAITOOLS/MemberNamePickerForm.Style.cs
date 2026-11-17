using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MSIFAITOOLS.Services;

namespace MSIFAITOOLS
{
    /// <summary>
    /// 「选择部材名」窗口的外观：尽量贴近 Google 日语输入法的候补面板。
    /// <para>
    /// 无标题栏的白色浮窗（细边框 + 系统投影）、顶部灰色圆角检索框、
    /// 中间带编号的候補一览（选中行浅蓝、悬停浅灰）、底部一行状态 + 关闭。
    /// 拖动窗口：按住检索框 / 底部的空白处拖；改变大小：拖右下角的手柄。
    /// </para>
    /// <para>
    /// 这里只放「画」和「窗口行为」，候補的组装 / 过滤 / 写入仍在
    /// <c>MemberNamePickerForm.cs</c> 里。
    /// </para>
    /// </summary>
    internal sealed partial class MemberNamePickerForm
    {
        // ---- 配色（白底 / 浅灰检索框 / 浅蓝选中，Google 输入法面板的配色）----
        private static readonly Color PanelBorderColor = Color.FromArgb(197, 203, 210);
        private static readonly Color SearchBackColor = Color.FromArgb(241, 243, 244);
        private static readonly Color SearchFocusBorderColor = Color.FromArgb(174, 203, 250);
        private static readonly Color SearchIconColor = Color.FromArgb(95, 99, 104);
        private static readonly Color RowTextColor = Color.FromArgb(32, 33, 36);
        private static readonly Color RowNumberColor = Color.FromArgb(154, 160, 166);
        private static readonly Color RowNumberSelectedColor = Color.FromArgb(25, 103, 210);
        private static readonly Color RowSelectedColor = Color.FromArgb(210, 227, 252);
        private static readonly Color RowHoverColor = Color.FromArgb(245, 247, 249);
        private static readonly Color FooterBorderColor = Color.FromArgb(232, 234, 237);
        private static readonly Color GripColor = Color.FromArgb(189, 193, 198);

        /// <summary>候補一行的字体（有「Yu Gothic UI」就用它，日文部材名更像输入法面板；没有就退回微软雅黑）。</summary>
        private static readonly Font RowFont = CreateUiFont(11F);

        /// <summary>候補前面编号的字体（比正文小一号、灰色）。</summary>
        private static readonly Font NumberFont = CreateUiFont(9F);

        /// <summary>候補文字的左端（左边留给编号）。</summary>
        private const int RowTextLeft = 34;

        /// <summary>编号的右端（右对齐，1 位数字看起来不会飘）。</summary>
        private const int NumberRight = 28;

        /// <summary>选中行 / 悬停行色块离列表左右边界的距离。</summary>
        private const int RowInset = 6;

        /// <summary>检索框（灰色圆角块）的高度 / 上边距 / 圆角半径。</summary>
        private const int SearchPillHeight = 32;
        private const int SearchPillTop = 11;
        private const int SearchPillRadius = 6;

        private const int WmNcLButtonDown = 0x00A1;
        private const int HtCaption = 0x0002;
        private const int EmSetCueBanner = 0x1501;
        private const int CsDropShadow = 0x00020000;

        /// <summary>鼠标现在悬停在哪一行（-1 = 没有）。</summary>
        private int _hoverIndex = -1;

        /// <summary>是否正在拖右下角改窗口大小。</summary>
        private bool _resizing;
        private Point _resizeStart;
        private Size _resizeStartSize;

        /// <summary>把滚轮消息转给候補一览（列表不拿焦点，滚轮消息本来会跑到别的控件去）。</summary>
        private CandidateWheelFilter _wheelFilter;

        /// <summary>按名字找字体；找不到时 <see cref="Font"/> 会静默退回别的字体，所以要把名字核对一遍。</summary>
        /// <param name="size">字号（磅）。</param>
        private static Font CreateUiFont(float size)
        {
            string[] families = { "Yu Gothic UI", "Meiryo UI", "微软雅黑", "Microsoft YaHei UI" };

            foreach (string family in families)
            {
                Font font = new Font(family, size, FontStyle.Regular, GraphicsUnit.Point);

                if (string.Equals(font.FontFamily.Name, family, StringComparison.OrdinalIgnoreCase))
                {
                    return font;
                }

                font.Dispose();
            }

            return new Font(SystemFonts.MessageBoxFont.FontFamily, size, FontStyle.Regular, GraphicsUnit.Point);
        }

        /// <summary>圆角矩形路径（半径很小时看着就是「微微圆角」的色块）。</summary>
        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;

            if (bounds.Width <= diameter || bounds.Height <= diameter)
            {
                path.AddRectangle(bounds);

                return path;
            }

            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();

            return path;
        }

        // ------------------------------------------------------------------
        // 窗口本身：1px 细边框 + 系统投影（像输入法的浮层，而不是普通窗口）
        // ------------------------------------------------------------------

        /// <summary>给无边框窗口加一点系统投影，浮在图纸上时层次更清楚。</summary>
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= CsDropShadow;

                return cp;
            }
        }

        /// <summary>画窗口最外面的 1px 细边框（内缩在 <see cref="Padding"/> 那一圈里，不会被控件盖住）。</summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            using (Pen pen = new Pen(PanelBorderColor))
            {
                e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            }
        }

        /// <summary>顶部检索区：灰色圆角块 + 放大镜图标（输入框是无边框的，坐在圆角块里面）。</summary>
        private void PnlSearch_Paint(object sender, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle pill = new Rectangle(12, SearchPillTop,
                Math.Max(20, pnlSearch.ClientSize.Width - 24), SearchPillHeight);

            using (GraphicsPath path = RoundedRect(pill, SearchPillRadius))
            {
                using (SolidBrush brush = new SolidBrush(SearchBackColor))
                {
                    e.Graphics.FillPath(brush, path);
                }

                // 输入框有焦点时给圆角块描一圈浅蓝（输入法面板那种「正在输入」的感觉）
                if (txtFilter.Focused)
                {
                    using (Pen pen = new Pen(SearchFocusBorderColor))
                    {
                        e.Graphics.DrawPath(pen, path);
                    }
                }
            }

            // 放大镜（圆圈 + 手柄）
            using (Pen pen = new Pen(SearchIconColor, 1.6F))
            {
                e.Graphics.DrawEllipse(pen, 23, 22, 8, 8);
                e.Graphics.DrawLine(pen, 30, 29, 33, 32);
            }
        }

        /// <summary>检索框的焦点变化只影响圆角块的描边。</summary>
        private void TxtFilter_FocusChanged(object sender, EventArgs e)
        {
            pnlSearch.Invalidate();
        }

        /// <summary>底部状态行上面画一条浅灰分隔线。</summary>
        private void PnlFooter_Paint(object sender, PaintEventArgs e)
        {
            using (Pen pen = new Pen(FooterBorderColor))
            {
                e.Graphics.DrawLine(pen, 0, 0, pnlFooter.Width, 0);
            }
        }

        /// <summary>右下角改大小的手柄（三个小斜点，光标是 ↖↘）。</summary>
        private void PnlResize_Paint(object sender, PaintEventArgs e)
        {
            using (Pen pen = new Pen(GripColor))
            {
                int w = pnlResize.ClientSize.Width;
                int h = pnlResize.ClientSize.Height;

                e.Graphics.DrawLine(pen, w - 4, h - 11, w - 11, h - 4);
                e.Graphics.DrawLine(pen, w - 4, h - 7, w - 7, h - 4);
                e.Graphics.DrawLine(pen, w - 4, h - 3, w - 3, h - 4);
            }
        }

        // ------------------------------------------------------------------
        // 无标题栏：按住检索区 / 底部空白拖动窗口，拖右下角手柄改大小
        // ------------------------------------------------------------------

        /// <summary>把「按住这里」变成窗口的非客户区拖动（和拖标题栏一样）。</summary>
        private void DragArea_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            ReleaseCapture();
            SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
        }

        /// <summary>开始拖右下角改大小。</summary>
        private void PnlResize_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            _resizing = true;
            _resizeStart = Cursor.Position;
            _resizeStartSize = Size;
            pnlResize.Capture = true;
        }

        /// <summary>拖动手柄时按鼠标位移改窗口大小（不小于设计器里写的最小尺寸）。</summary>
        private void PnlResize_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_resizing)
            {
                return;
            }

            Point now = Cursor.Position;
            Size size = new Size(
                _resizeStartSize.Width + (now.X - _resizeStart.X),
                _resizeStartSize.Height + (now.Y - _resizeStart.Y));

            if (size.Width < MinimumSize.Width)
            {
                size.Width = MinimumSize.Width;
            }

            if (size.Height < MinimumSize.Height)
            {
                size.Height = MinimumSize.Height;
            }

            Size = size;
        }

        /// <summary>松开鼠标 = 结束改大小。</summary>
        private void PnlResize_MouseUp(object sender, MouseEventArgs e)
        {
            _resizing = false;
            pnlResize.Capture = false;
        }

        // ------------------------------------------------------------------
        // 候補一览：自绘（编号 + 部材名，选中行浅蓝）
        // ------------------------------------------------------------------

        /// <summary>画一行候補：左边灰色编号、右边部材名；选中行浅蓝圆角色块，悬停行浅灰。</summary>
        private void LstNames_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= lstNames.Items.Count)
            {
                return;
            }

            Graphics graphics = e.Graphics;
            Rectangle row = e.Bounds;

            // 行底色（列表整体是白的；选中 / 悬停再叠一层）
            using (SolidBrush white = new SolidBrush(Color.White))
            {
                graphics.FillRectangle(white, row);
            }

            bool selected = lstNames.SelectedIndex == e.Index;
            Color? accent = selected ? RowSelectedColor : (e.Index == _hoverIndex ? RowHoverColor : (Color?)null);
            Rectangle pill = new Rectangle(row.X + RowInset, row.Y + 1,
                Math.Max(0, row.Width - (RowInset * 2)), Math.Max(0, row.Height - 2));

            if (accent.HasValue && pill.Width > 6 && pill.Height > 4)
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;

                using (GraphicsPath path = RoundedRect(pill, 3))
                using (SolidBrush brush = new SolidBrush(accent.Value))
                {
                    graphics.FillPath(brush, path);
                }

                graphics.SmoothingMode = SmoothingMode.Default;
            }

            string number = ShortcutNumber(e.Index);

            if (number.Length > 0)
            {
                Rectangle numberRect = new Rectangle(row.X + 10, row.Y, NumberRight - 10, row.Height);

                TextRenderer.DrawText(graphics, number, NumberFont, numberRect,
                    selected ? RowNumberSelectedColor : RowNumberColor,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            string text = lstNames.Items[e.Index] as string ?? string.Empty;
            Rectangle textRect = new Rectangle(row.X + RowTextLeft, row.Y,
                Math.Max(0, row.Width - RowTextLeft - 10), row.Height);

            TextRenderer.DrawText(graphics, text, RowFont, textRect, RowTextColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        /// <summary>鼠标移到哪一行就把那一行画成浅灰（像输入法面板的悬停效果）。</summary>
        private void LstNames_MouseMove(object sender, MouseEventArgs e)
        {
            int index = lstNames.IndexFromPoint(e.Location);

            if (index == _hoverIndex)
            {
                return;
            }

            InvalidateRow(_hoverIndex);
            _hoverIndex = index;
            InvalidateRow(_hoverIndex);
        }

        /// <summary>鼠标离开列表 = 没有悬停行。</summary>
        private void LstNames_MouseLeave(object sender, EventArgs e)
        {
            if (_hoverIndex < 0)
            {
                return;
            }

            InvalidateRow(_hoverIndex);
            _hoverIndex = -1;
        }

        /// <summary>只重画一行（整表重画会闪）。</summary>
        /// <param name="index">行的位置（从 0 开始；-1 = 忽略）。</param>
        private void InvalidateRow(int index)
        {
            if (index < 0 || index >= lstNames.Items.Count)
            {
                return;
            }

            int y = (index - lstNames.TopIndex) * lstNames.ItemHeight;

            if (y + lstNames.ItemHeight <= 0 || y >= lstNames.ClientSize.Height)
            {
                return;
            }

            lstNames.Invalidate(new Rectangle(0, y, lstNames.ClientSize.Width, lstNames.ItemHeight));
        }

        /// <summary>列表里一次能看到几行（窗口缩到最小时让选中行露出来用；正常 10 项都看得见）。</summary>
        private int VisibleRowCount()
        {
            int itemHeight = Math.Max(1, lstNames.ItemHeight);

            return Math.Max(1, lstNames.ClientSize.Height / itemHeight);
        }

        /// <summary>把第 <paramref name="index"/> 行滚进可视范围（键盘 ↑↓ / 数字键选的时候用）。</summary>
        /// <param name="index">行的位置（从 0 开始）。</param>
        private void EnsureRowVisible(int index)
        {
            if (index < 0)
            {
                return;
            }

            int visible = VisibleRowCount();

            if (index < lstNames.TopIndex)
            {
                lstNames.TopIndex = index;
            }
            else if (index >= lstNames.TopIndex + visible)
            {
                lstNames.TopIndex = Math.Max(0, index - visible + 1);
            }
        }

        // ------------------------------------------------------------------
        // 检索框的提示文字 / 滚轮 / Win32
        // ------------------------------------------------------------------

        /// <summary>检索框为空时显示的灰色提示（EM_SETCUEBANNER，和浏览器搜索框一样）。</summary>
        private void ApplySearchCueBanner()
        {
            if (!txtFilter.IsHandleCreated)
            {
                return;
            }

            SendMessage(txtFilter.Handle, EmSetCueBanner, (IntPtr)1, UiText.T("检索部材名（直接打字过滤）"));
        }

        /// <summary>切换界面语言：重写检索框的灰色提示（它不是控件文字，不在自动切换范围内）。</summary>
        private void MemberNamePicker_LanguageChanged(object sender, EventArgs e)
        {
            ApplySearchCueBanner();
        }

        /// <summary>窗口关闭：摘掉语言切换的登记（事件是进程级的，不摘会留着已释放的窗口）。</summary>
        private void MemberNamePicker_FormClosed(object sender, FormClosedEventArgs e)
        {
            UiText.LanguageChanged -= MemberNamePicker_LanguageChanged;
        }

        /// <summary>窗口句柄建好：挂滚轮过滤器 + 写检索框提示。</summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            if (_wheelFilter == null)
            {
                _wheelFilter = new CandidateWheelFilter(this);
            }

            Application.AddMessageFilter(_wheelFilter);
            ApplySearchCueBanner();
        }

        /// <summary>窗口句柄销毁：把滚轮过滤器摘掉（它是进程级的）。</summary>
        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (_wheelFilter != null)
            {
                Application.RemoveMessageFilter(_wheelFilter);
            }

            base.OnHandleDestroyed(e);
        }

        /// <summary>
        /// 列表不拿焦点（焦点一直在检索框里），滚轮消息本来会滚到别的控件上；
        /// 鼠标停在候補一览上时把滚轮接过来<b>翻页</b>（一页 10 项，列表里没有滚动条）。
        /// </summary>
        /// <param name="wheelDelta">滚轮增量（正 = 往上滚 = 上一页）。</param>
        /// <returns>true = 已经处理（不要再传给别的控件）。</returns>
        private bool ScrollCandidateList(int wheelDelta)
        {
            if (wheelDelta == 0 || !Visible || !ContainsFocus)
            {
                return false;
            }

            if (!lstNames.RectangleToScreen(lstNames.ClientRectangle).Contains(Cursor.Position))
            {
                return false;
            }

            if (_hoverIndex >= 0)
            {
                _hoverIndex = -1;
                lstNames.Invalidate();
            }

            // 滚轮往上 = 回上一页，往下 = 下一页（和底部的 ‹ › 按钮一致）
            return ChangePage(wheelDelta > 0 ? -1 : 1);
        }

        /// <summary>滚轮消息过滤器（只认 WM_MOUSEWHEEL，其它原样放行）。</summary>
        private sealed class CandidateWheelFilter : IMessageFilter
        {
            private const int WmMouseWheel = 0x020A;

            private readonly MemberNamePickerForm _form;

            public CandidateWheelFilter(MemberNamePickerForm form)
            {
                _form = form;
            }

            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != WmMouseWheel)
                {
                    return false;
                }

                int delta = (short)((long)m.WParam >> 16);

                return _form.ScrollCandidateList(delta);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
    }
}
