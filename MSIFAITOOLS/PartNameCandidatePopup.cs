using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MSIFAITOOLS.Services;

namespace MSIFAITOOLS
{
    /// <summary>
    /// 「部品名」候选项被选中时的回调（参数 = 要写进单元格的部品名）。
    /// </summary>
    /// <param name="partName">选中的部品名（候选项里的原文）。</param>
    internal delegate void PartNameCandidatePickedHandler(string partName);

    /// <summary>
    /// 候选项的一条：<see cref="Text"/> = 显示 / 写入单元格的部品名 原文，
    /// <see cref="Hint"/> = 列表右侧灰色的<b>来源标识</b>（<c>分割</c> / <c>AI</c> / <c>DB</c> /
    /// <c>表</c> / <c>マスタ</c>；空 = 不显示）。
    /// <para>
    /// 来源标识同时也是排序依据 —— 见 <c>PartBasicInfoForm.FindPartNameCandidates</c>：
    /// <b>分割 &gt; 部材名 AI 推荐 &gt; 部材部品 DB 的关联项目 &gt; 表格其它行 &gt; 部品マスタ</b>。
    /// </para>
    /// </summary>
    internal sealed class PartNameCandidateOption
    {
        public PartNameCandidateOption(string text, string hint)
        {
            Text = text ?? string.Empty;
            Hint = hint ?? string.Empty;
        }

        /// <summary>部品名 原文（选中后写进单元格的就是它）。</summary>
        public string Text { get; private set; }

        /// <summary>来源标识（列表右侧灰色小字；空 = 不显示）。</summary>
        public string Hint { get; private set; }
    }

    /// <summary>
    /// 「部品名」用的<b>不抢焦点</b>候选窗口（IME 的候选列表那种感觉）：
    /// 由部品基本信息画面（<see cref="PartBasicInfoForm"/>）在编辑「部品名」（<c>colPartName</c>）
    /// 单元格时打开，显示按已输入内容过滤出来的部品名 候选。
    ///
    /// <para>
    /// <b>不抢焦点</b>：<see cref="ShowWithoutActivation"/> + <see cref="CreateParams"/> 里的
    /// <c>WS_EX_NOACTIVATE</c> / <c>WS_EX_TOOLWINDOW</c> → 弹出 / 鼠标点击都不会激活本窗口，
    /// 焦点一直留在表格的单元格编辑框里（可以继续打字），点候选项也不会中断编辑。
    /// 也不出现在任务栏 / Alt+Tab 里。
    /// </para>
    ///
    /// <para>
    /// <b>数字键直接选</b>：窗口显示期间用 <see cref="IMessageFilter"/> 拦下发给编辑框的
    /// <c>WM_KEYDOWN</c>：<c>1</c>～<c>9</c> 选第 1～9 项、<c>0</c> 选第 10 项，
    /// <c>Esc</c> 只关掉候选列表（不退出单元格编辑）。因为不抢焦点，这些键本来会打进单元格，
    /// 所以必须在消息循环阶段拦下来（<see cref="PreFilterMessage"/>）。
    /// </para>
    ///
    /// <para>
    /// 列表是自绘的（没有子控件）：子控件（ListBox 等）点一下会把焦点从表格抢过去，
    /// 自绘 + 鼠标坐标判断就不会。
    /// </para>
    /// </summary>
    internal sealed class PartNameCandidatePopup : Form, IMessageFilter
    {
        /// <summary>最多显示 / 可选中的候选项数（数字键 1～9 = 前 9 项，0 = 第 10 项）。</summary>
        public const int MaxCandidates = 10;

        /// <summary><c>WS_EX_NOACTIVATE</c>：显示 / 点击都不激活本窗口（焦点留在表格里）。</summary>
        private const int WsExNoActivate = 0x08000000;

        /// <summary><c>WS_EX_TOOLWINDOW</c>：不显示在任务栏 / Alt+Tab 里。</summary>
        private const int WsExToolWindow = 0x00000080;

        /// <summary><c>WM_KEYDOWN</c>：拦数字键 / Esc 用。</summary>
        private const int WmKeyDown = 0x0100;

        /// <summary>每一行候选的高度（像素）。</summary>
        private const int RowHeight = 22;

        /// <summary>左右内边距。</summary>
        private const int PaddingX = 8;

        /// <summary>上下内边距。</summary>
        private const int PaddingY = 5;

        /// <summary>左侧序号（<c>1</c>～<c>9</c> / <c>0</c>）占的宽度。</summary>
        private const int NumberWidth = 18;

        /// <summary>底部操作提示那一行的高度。</summary>
        private const int FooterHeight = 18;

        /// <summary>窗口最小宽度。</summary>
        private const int MinWidth = 190;

        /// <summary>窗口最大宽度（名字太长时省略号，不撑满屏幕）。</summary>
        private const int MaxWidth = 460;

        /// <summary>序号文字的颜色。</summary>
        private static readonly Color NumberColor = Color.FromArgb(149, 165, 166);

        /// <summary>候选文字的颜色。</summary>
        private static readonly Color TextColor = Color.FromArgb(52, 73, 94);

        /// <summary>鼠标所在那一行的背景色。</summary>
        private static readonly Color HoverBackColor = Color.FromArgb(232, 240, 254);

        /// <summary>边框颜色（与画面上的按钮边框一致）。</summary>
        private static readonly Color BorderColor = Color.FromArgb(199, 208, 219);

        /// <summary>底部提示文字的颜色。</summary>
        private static readonly Color HintColor = Color.FromArgb(149, 165, 166);

        /// <summary>底部提示 / 来源标识用的字体（比候选项小一号）。</summary>
        private readonly Font _hintFont = new Font("微软雅黑", 7.5F, FontStyle.Regular, GraphicsUnit.Point);

        /// <summary>候选项（按显示顺序；最多 <see cref="MaxCandidates"/> 项，已按来源优先度排好）。</summary>
        private readonly List<PartNameCandidateOption> _items = new List<PartNameCandidateOption>();

        /// <summary>本窗口的 Owner（= 部品基本信息画面；为 <c>null</c> 时直接 <see cref="Form.Show()"/>）。</summary>
        private readonly Form _owner;

        /// <summary>鼠标所在的行（-1 = 没有）。</summary>
        private int _hoverIndex = -1;

        /// <summary>是否已经把自己挂到消息过滤器上（避免重复添加）。</summary>
        private bool _filterAdded;

        /// <summary>
        /// 创建候选窗口。
        /// </summary>
        /// <param name="owner">Owner 窗口（部品基本信息画面；可为 <c>null</c>）。</param>
        public PartNameCandidatePopup(Form owner)
        {
            _owner = owner;

            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.White;
            Font = new Font("微软雅黑", 9F, FontStyle.Regular, GraphicsUnit.Point);
            FormBorderStyle = FormBorderStyle.None;
            KeyPreview = true;
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = UiText.T("部品名 候補");

            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer, true);
            UpdateStyles();
        }

        /// <summary>选中某一项时触发（本窗口随后自动隐藏）。</summary>
        public event PartNameCandidatePickedHandler CandidatePicked;

        /// <summary>当前显示的候选项（部品名 原文的只读副本，调试 / 测试用）。</summary>
        public IList<string> Candidates
        {
            get
            {
                List<string> texts = new List<string>(_items.Count);

                foreach (PartNameCandidateOption item in _items)
                {
                    texts.Add(item.Text);
                }

                return texts;
            }
        }

        /// <summary>
        /// 显示 / 刷新候选列表，并贴着 <paramref name="anchorBounds"/>（单元格的屏幕矩形）弹出。
        /// </summary>
        /// <param name="candidates">
        /// 候选项（按来源优先度排好；多于 <see cref="MaxCandidates"/> 时只取前几项）。
        /// </param>
        /// <param name="anchorBounds">锚点矩形（单元格的屏幕坐标）；列表显示在它下面，下面放不下时显示在上面。</param>
        public void ShowCandidates(IList<PartNameCandidateOption> candidates, Rectangle anchorBounds)
        {
            _items.Clear();

            if (candidates != null)
            {
                foreach (PartNameCandidateOption candidate in candidates)
                {
                    if (candidate == null || string.IsNullOrWhiteSpace(candidate.Text) ||
                        _items.Count >= MaxCandidates)
                    {
                        continue;
                    }

                    _items.Add(new PartNameCandidateOption(candidate.Text.Trim(), candidate.Hint));
                }
            }

            if (_items.Count == 0)
            {
                HideCandidates();

                return;
            }

            _hoverIndex = -1;
            ApplyBounds(anchorBounds);

            if (!Visible)
            {
                if (_owner != null && !_owner.IsDisposed && _owner.Visible)
                {
                    // 有 Owner：跟着主画面一起关闭，并且总是浮在主画面之上（不会盖到别的程序上去）
                    Show(_owner);
                }
                else
                {
                    Show();
                }
            }
            else
            {
                Invalidate();
            }
        }

        /// <summary>隐藏候选列表（不释放窗口，下次直接复用）。</summary>
        public void HideCandidates()
        {
            _hoverIndex = -1;

            if (Visible)
            {
                Hide();
            }
        }

        /// <summary>不激活本窗口（显示时焦点留在表格的编辑框里）。</summary>
        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        /// <summary>加 <c>WS_EX_NOACTIVATE</c>（点击也不抢焦点）+ <c>WS_EX_TOOLWINDOW</c>（不进任务栏）。</summary>
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams createParams = base.CreateParams;
                createParams.ExStyle |= WsExNoActivate | WsExToolWindow;

                return createParams;
            }
        }

        /// <summary>显示时挂上消息过滤器（拦数字键 / Esc），隐藏时摘掉。</summary>
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);

            if (Visible)
            {
                if (!_filterAdded)
                {
                    Application.AddMessageFilter(this);
                    _filterAdded = true;
                }
            }
            else
            {
                RemoveMessageFilter();
            }
        }

        /// <summary>释放时确保消息过滤器已经摘掉（窗口没显示过就关闭的情况）。</summary>
        protected override void Dispose(bool disposing)
        {
            RemoveMessageFilter();

            if (disposing)
            {
                _hintFont.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// 消息循环阶段拦下发给表格编辑框的按键：数字键 = 选候选项，<c>Esc</c> = 关掉候选列表。
        /// 其它键（包括方向键 / 回车 / 文字）原样放行，编辑框继续正常输入。
        /// </summary>
        public bool PreFilterMessage(ref Message m)
        {
            if (!Visible || _items.Count == 0 || m.Msg != WmKeyDown)
            {
                return false;
            }

            // Ctrl / Alt / Shift 组合键是别的功能（复制、切换输入法等），不拦
            if (Control.ModifierKeys != Keys.None)
            {
                return false;
            }

            Keys key = (Keys)(int)m.WParam;

            if (key == Keys.Escape)
            {
                HideCandidates();

                return true;
            }

            int index = DigitToIndex(key);

            if (index < 0 || index >= _items.Count)
            {
                // 没有这一项 → 不拦，数字照常打进单元格（例如部品名 里本来就有数字）
                return false;
            }

            Pick(index);

            return true;
        }

        /// <summary>自绘：白底 + 边框 + 每行「序号 + 部品名 + 右侧来源标识」+ 底部操作提示。</summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            Graphics graphics = e.Graphics;

            using (SolidBrush background = new SolidBrush(Color.White))
            {
                graphics.FillRectangle(background, ClientRectangle);
            }

            using (Pen border = new Pen(BorderColor))
            {
                graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            }

            // 右侧灰色来源标识占的宽度（全部没有标识时为 0）
            int hintArea = MeasureHintWidth();
            int hintGap = hintArea > 0 ? 8 : 0;
            int textWidth = Width - NumberWidth - PaddingX * 3 - hintArea - hintGap;

            for (int index = 0; index < _items.Count; index++)
            {
                int top = PaddingY + index * RowHeight;

                if (index == _hoverIndex)
                {
                    using (SolidBrush hover = new SolidBrush(HoverBackColor))
                    {
                        graphics.FillRectangle(hover, 1, top, Width - 2, RowHeight);
                    }
                }

                string number = index == MaxCandidates - 1 ? "0" : (index + 1).ToString();

                TextRenderer.DrawText(graphics, number, Font,
                    new Rectangle(PaddingX, top, NumberWidth, RowHeight), NumberColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);

                TextRenderer.DrawText(graphics, _items[index].Text, Font,
                    new Rectangle(PaddingX + NumberWidth, top, textWidth, RowHeight), TextColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                if (hintArea > 0 && _items[index].Hint.Length > 0)
                {
                    // 来源标识（分割 / AI / DB / 表 / マスタ）：右对齐、灰色
                    TextRenderer.DrawText(graphics, _items[index].Hint, _hintFont,
                        new Rectangle(Width - PaddingX - hintArea, top, hintArea, RowHeight), HintColor,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Right |
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                }
            }

            TextRenderer.DrawText(graphics,
                UiText.T("数字键 1-9 / 0 选择、Esc 关闭（不会离开单元格）"), _hintFont,
                new Rectangle(PaddingX, Height - FooterHeight - 1, Width - PaddingX * 2, FooterHeight),
                HintColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        /// <summary>鼠标移到某一行 → 高亮那一行（点击即可选中）。</summary>
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            int index = HitTest(e.Y);

            if (index != _hoverIndex)
            {
                _hoverIndex = index;
                Invalidate();
            }
        }

        /// <summary>鼠标离开 → 取消高亮。</summary>
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);

            if (_hoverIndex != -1)
            {
                _hoverIndex = -1;
                Invalidate();
            }
        }

        /// <summary>
        /// 单击某一行 = 选中这一项（本窗口是 <c>WS_EX_NOACTIVATE</c>，不会把焦点从表格抢走）。
        /// </summary>
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            int index = HitTest(e.Y);

            if (index >= 0)
            {
                Pick(index);
            }
        }

        /// <summary>窗口失去激活（例如用户切到别的程序）→ 收起候选列表。</summary>
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);

            HideCandidates();
        }

        /// <summary>按鼠标 Y 坐标算第几行（不在任何一行上 = -1）。</summary>
        private int HitTest(int y)
        {
            if (y < PaddingY)
            {
                return -1;
            }

            int index = (y - PaddingY) / RowHeight;

            // 底部提示那一行不算候选项
            if (index < 0 || index >= _items.Count)
            {
                return -1;
            }

            return index;
        }

        /// <summary>
        /// <c>1</c>～<c>9</c> / 小键盘 <c>1</c>～<c>9</c> → 0～8，
        /// <c>0</c> / 小键盘 <c>0</c> → 9；其它键 = -1。
        /// </summary>
        private static int DigitToIndex(Keys key)
        {
            if (key >= Keys.D1 && key <= Keys.D9)
            {
                return (int)key - (int)Keys.D1;
            }

            if (key == Keys.D0)
            {
                return 9;
            }

            if (key >= Keys.NumPad1 && key <= Keys.NumPad9)
            {
                return (int)key - (int)Keys.NumPad1;
            }

            if (key == Keys.NumPad0)
            {
                return 9;
            }

            return -1;
        }

        /// <summary>选中第 <paramref name="index"/> 项：先收起窗口，再通知主画面写入。</summary>
        private void Pick(int index)
        {
            if (index < 0 || index >= _items.Count)
            {
                return;
            }

            string value = _items[index].Text;

            // 先隐藏（顺便摘掉消息过滤器），主画面收到回调后改写编辑框文字时不会再弹出来
            HideCandidates();

            PartNameCandidatePickedHandler handler = CandidatePicked;

            if (handler != null)
            {
                handler(value);
            }
        }

        /// <summary>按锚点矩形算出窗口位置 / 大小（下面放不下就翻到上面；不超出所在屏幕的工作区）。</summary>
        private void ApplyBounds(Rectangle anchorBounds)
        {
            int width = MinWidth;
            int hintArea = MeasureHintWidth();
            int hintGap = hintArea > 0 ? 8 : 0;

            foreach (PartNameCandidateOption item in _items)
            {
                int itemWidth = TextRenderer.MeasureText(item.Text, Font).Width +
                    NumberWidth + PaddingX * 3 + (item.Hint.Length == 0 ? 0 : hintArea + hintGap);

                if (itemWidth > width)
                {
                    width = itemWidth;
                }
            }

            width = Math.Min(width, MaxWidth);

            int height = PaddingY * 2 + _items.Count * RowHeight + FooterHeight;

            Rectangle workingArea = Screen.FromRectangle(anchorBounds).WorkingArea;
            int left = anchorBounds.Left;
            int top = anchorBounds.Bottom;

            if (left + width > workingArea.Right)
            {
                left = workingArea.Right - width;
            }

            if (left < workingArea.Left)
            {
                left = workingArea.Left;
            }

            // 单元格下面放不下 → 显示在单元格上面（不要超出屏幕）
            if (top + height > workingArea.Bottom)
            {
                top = anchorBounds.Top - height - 2;
            }

            if (top < workingArea.Top)
            {
                top = workingArea.Top;
            }

            Bounds = new Rectangle(left, top, width, height);
        }

        /// <summary>所有候选项的来源标识里最宽的那个（像素；都没有标识时 = 0）。</summary>
        private int MeasureHintWidth()
        {
            int width = 0;

            foreach (PartNameCandidateOption item in _items)
            {
                if (item.Hint.Length == 0)
                {
                    continue;
                }

                int itemWidth = TextRenderer.MeasureText(item.Hint, _hintFont).Width;

                if (itemWidth > width)
                {
                    width = itemWidth;
                }
            }

            return width;
        }

        /// <summary>摘掉消息过滤器（没挂过也没关系）。</summary>
        private void RemoveMessageFilter()
        {
            if (!_filterAdded)
            {
                return;
            }

            Application.RemoveMessageFilter(this);
            _filterAdded = false;
        }
    }
}
