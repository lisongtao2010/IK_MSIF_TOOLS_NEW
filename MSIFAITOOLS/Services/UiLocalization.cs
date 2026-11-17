using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace MSIFAITOOLS.Services
{
    /// <summary>
    /// 子画面（部品基本信息 / PDF 预览 / 部材名面板 / 各种设置画面 …）的界面文字中日切换。
    /// <para>
    /// 主画面（Form1）是在 <c>Form1.Localization.cs</c> 里逐个控件赋值；
    /// 子画面控件多、又都是设计器里直接写的中文，逐个赋值太啰嗦，所以这里用「自动记住 + 统一重设」：
    /// <list type="number">
    /// <item>画面构造时 <see cref="Attach"/> 一次：把画面里所有<b>显示文字</b>的控件
    ///       （窗体标题 / 按钮 / 标签 / 分组框 / 标签页 / 列头 / 菜单项）的中文原文记下来当翻译键，
    ///       并按当前语言立刻刷一次；</item>
    /// <item>切换到别的语言时（<see cref="UiText.LanguageChanged"/>）自动按记下的中文重刷，画面不用自己写代码；</item>
    /// <item>代码里动态改文字（例如状态行「保存失败：…」）用 <see cref="SetText(Control,string)"/> /
    ///       <see cref="SetText(ToolStripItem,string)"/> / <see cref="SetText(DataGridViewColumn,string)"/>，
    ///       它会同时更新「原文」，这样切换语言时也不会退回旧文字；</item>
    /// <item>工具提示（<see cref="ToolTip"/>）没法自动枚举，用 <see cref="SetToolTip"/> 登记。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 中文原文照旧写在设计器 / 代码里（<see cref="UiText.Translate"/> 的键），
    /// 日语在 <c>Services\UiText.Japanese.cs</c> 的对照表里维护；查不到就原样显示中文，不会坏。
    /// 输入框（<see cref="TextBoxBase"/> / <see cref="ComboBox"/> 等）的文字是用户数据，<b>不</b>翻译。
    /// </para>
    /// </summary>
    internal static class UiLocalization
    {
        /// <summary>已挂接的画面 → 这个画面里各控件的中文原文（翻译键）。</summary>
        private static readonly ConditionalWeakTable<Form, FormTexts> AttachedForms =
            new ConditionalWeakTable<Form, FormTexts>();

        /// <summary>
        /// 给画面挂上中日切换（构造里 <c>InitializeComponent()</c> 之后调用一次即可）：
        /// 记住当前中文文字 → 按当前语言显示 → 之后切换语言时自动重刷。
        /// 画面关闭时自动解除挂接，不会留在 <see cref="UiText.LanguageChanged"/> 上。
        /// </summary>
        public static void Attach(Form form)
        {
            if (form == null)
            {
                return;
            }

            FormTexts texts;

            if (AttachedForms.TryGetValue(form, out texts))
            {
                Apply(form);
                return;
            }

            texts = new FormTexts();
            AttachedForms.Add(form, texts);

            // 挂接时控件里还是设计器写的中文 → 正好当作翻译键记下来
            Capture(form, texts);
            Apply(form);

            EventHandler handler = delegate { Apply(form); };
            texts.LanguageChangedHandler = handler;
            UiText.LanguageChanged += handler;

            form.FormClosed += delegate { Detach(form); };
        }

        /// <summary>解除挂接（画面关闭时自动调用；重复调用无副作用）。</summary>
        public static void Detach(Form form)
        {
            FormTexts texts;

            if (form == null || !AttachedForms.TryGetValue(form, out texts))
            {
                return;
            }

            if (texts.LanguageChangedHandler != null)
            {
                UiText.LanguageChanged -= texts.LanguageChangedHandler;
                texts.LanguageChangedHandler = null;
            }

            AttachedForms.Remove(form);
        }

        /// <summary>按当前语言把画面里记下的文字重刷一遍（语言切换时自动调用）。</summary>
        public static void Apply(Form form)
        {
            FormTexts texts;

            if (form == null || !AttachedForms.TryGetValue(form, out texts))
            {
                return;
            }

            foreach (KeyValuePair<object, string> pair in texts.Texts)
            {
                SetComponentText(pair.Key, UiText.Translate(pair.Value));
            }

            foreach (TipText tip in texts.Tips)
            {
                tip.ToolTip.SetToolTip(tip.Control, UiText.Translate(tip.Text));
            }
        }

        /// <summary>
        /// 改控件文字（中文原文交给 <see cref="UiText.Translate"/> 翻成当前语言）：
        /// 同时更新记下的原文，切换语言时会按新原文重刷。
        /// </summary>
        public static void SetText(Control control, string chineseText)
        {
            if (control == null)
            {
                return;
            }

            Remember(control, chineseText);
            control.Text = UiText.Translate(chineseText);
        }

        /// <summary>改菜单 / 工具条项的文字（用法同 <see cref="SetText(Control,string)"/>）。</summary>
        public static void SetText(ToolStripItem item, string chineseText)
        {
            if (item == null)
            {
                return;
            }

            Remember(item, chineseText);
            item.Text = UiText.Translate(chineseText);
        }

        /// <summary>改表格列头文字（用法同 <see cref="SetText(Control,string)"/>）。</summary>
        public static void SetText(DataGridViewColumn column, string chineseText)
        {
            if (column == null)
            {
                return;
            }

            Remember(column, chineseText);
            column.HeaderText = UiText.Translate(chineseText);
        }

        /// <summary>
        /// 登记并设置工具提示（<see cref="ToolTip"/> 的提示没法自动枚举，需要逐条登记）。
        /// </summary>
        public static void SetToolTip(ToolTip toolTip, Control control, string chineseText)
        {
            if (toolTip == null || control == null)
            {
                return;
            }

            FormTexts texts = FindTexts(control);

            if (texts != null)
            {
                bool exists = false;

                foreach (TipText tip in texts.Tips)
                {
                    if (ReferenceEquals(tip.ToolTip, toolTip) && ReferenceEquals(tip.Control, control))
                    {
                        tip.Text = chineseText ?? string.Empty;
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                {
                    texts.Tips.Add(new TipText(toolTip, control, chineseText ?? string.Empty));
                }
            }

            toolTip.SetToolTip(control, UiText.Translate(chineseText));
        }

        /// <summary>
        /// 这个控件是否「显示文字」的控件（按钮 / 标签 / 分组框 / 标签页 …）。
        /// 输入框、下拉框、列表、表格单元格是用户数据，返回 false（不翻译）。
        /// </summary>
        private static bool ShowsCaption(Control control)
        {
            return control is Form
                || control is Label
                || control is ButtonBase
                || control is GroupBox
                || control is TabPage
                || control is LinkLabel;
        }

        /// <summary>递归记住画面里所有显示文字控件的当前文字（第一次挂接时调用）。</summary>
        private static void Capture(Control control, FormTexts texts)
        {
            if (control == null)
            {
                return;
            }

            if (ShowsCaption(control))
            {
                Record(control, control.Text, texts);
            }

            DataGridView grid = control as DataGridView;

            if (grid != null)
            {
                foreach (DataGridViewColumn column in grid.Columns)
                {
                    Record(column, column.HeaderText, texts);
                }
            }

            ToolStrip strip = control as ToolStrip;

            if (strip != null)
            {
                CaptureToolStripItems(strip.Items, texts);
            }

            if (control.ContextMenuStrip != null)
            {
                CaptureToolStripItems(control.ContextMenuStrip.Items, texts);
            }

            foreach (Control child in control.Controls)
            {
                Capture(child, texts);
            }
        }

        /// <summary>记住菜单 / 工具条里各项的文字（含二级菜单）。</summary>
        private static void CaptureToolStripItems(ToolStripItemCollection items, FormTexts texts)
        {
            if (items == null)
            {
                return;
            }

            foreach (ToolStripItem item in items)
            {
                Record(item, item.Text, texts);

                ToolStripDropDownItem dropDown = item as ToolStripDropDownItem;

                if (dropDown != null)
                {
                    CaptureToolStripItems(dropDown.DropDownItems, texts);
                }
            }
        }

        /// <summary>记下中文原文（同一控件只记第一次，也就是设计器里写的那份）。</summary>
        private static void Record(object component, string chineseText, FormTexts texts)
        {
            if (component == null || string.IsNullOrEmpty(chineseText) || texts == null)
            {
                return;
            }

            if (!texts.Texts.ContainsKey(component))
            {
                texts.Texts.Add(component, chineseText);
            }
        }

        /// <summary>更新某控件的中文原文（动态文字用 <see cref="SetText(Control,string)"/> 时调用）。</summary>
        private static void Remember(object component, string chineseText)
        {
            FormTexts texts = FindTexts(component);

            if (texts == null)
            {
                return;
            }

            texts.Texts[component] = chineseText ?? string.Empty;
        }

        /// <summary>找到这个控件所在画面挂接时用的记录表（画面没挂接时返回 <c>null</c>）。</summary>
        private static FormTexts FindTexts(object component)
        {
            Control control = component as Control;

            if (control == null)
            {
                ToolStripItem item = component as ToolStripItem;

                if (item != null)
                {
                    control = item.Owner;
                }
            }

            if (control == null)
            {
                DataGridViewColumn column = component as DataGridViewColumn;

                if (column != null)
                {
                    control = column.DataGridView;
                }
            }

            while (control != null)
            {
                Form form = control as Form;

                if (form != null)
                {
                    FormTexts texts;
                    return AttachedForms.TryGetValue(form, out texts) ? texts : null;
                }

                control = control.Parent;
            }

            return null;
        }

        /// <summary>把记下的原文按类型写回控件 / 菜单项 / 列头。</summary>
        private static void SetComponentText(object component, string text)
        {
            Control control = component as Control;

            if (control != null)
            {
                control.Text = text;
                return;
            }

            ToolStripItem item = component as ToolStripItem;

            if (item != null)
            {
                item.Text = text;
                return;
            }

            DataGridViewColumn column = component as DataGridViewColumn;

            if (column != null)
            {
                column.HeaderText = text;
            }
        }

        /// <summary>一个画面的文字记录（中文原文 + 工具提示登记 + 语言切换回调）。</summary>
        private sealed class FormTexts
        {
            public readonly Dictionary<object, string> Texts = new Dictionary<object, string>();

            public readonly List<TipText> Tips = new List<TipText>();

            public EventHandler LanguageChangedHandler { get; set; }
        }

        /// <summary>一条工具提示登记（哪个 <see cref="ToolTip"/> 设在哪个控件上）。</summary>
        private sealed class TipText
        {
            public TipText(ToolTip toolTip, Control control, string text)
            {
                ToolTip = toolTip;
                Control = control;
                Text = text;
            }

            public ToolTip ToolTip { get; private set; }

            public Control Control { get; private set; }

            public string Text { get; set; }
        }
    }
}
