namespace MSIFAITOOLS
{
    partial class MemberNamePickerForm
    {
        /// <summary>必需的设计器变量。</summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>清理所有正在使用的资源。</summary>
        /// <param name="disposing">如果应释放托管资源，为 true；否则为 false。</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }

            base.Dispose(disposing);
        }

        #region Windows 窗体设计器生成的代码

        /// <summary>
        /// 设计器支持所需的方法 - 不要修改
        /// 使用代码编辑器修改此方法的内容。
        /// </summary>
        private void InitializeComponent()
        {
            this.pnlSearch = new System.Windows.Forms.Panel();
            this.txtFilter = new System.Windows.Forms.TextBox();
            this.lstNames = new System.Windows.Forms.ListBox();
            this.pnlFooter = new System.Windows.Forms.Panel();
            this.lblPreview = new System.Windows.Forms.Label();
            this.btnPrevPage = new System.Windows.Forms.Button();
            this.lblPage = new System.Windows.Forms.Label();
            this.btnNextPage = new System.Windows.Forms.Button();
            this.lblHint = new System.Windows.Forms.Label();
            this.btnCancel = new System.Windows.Forms.Button();
            this.pnlResize = new System.Windows.Forms.Panel();
            this.pnlSearch.SuspendLayout();
            this.pnlFooter.SuspendLayout();
            this.SuspendLayout();
            // 
            // pnlSearch
            // 
            this.pnlSearch.BackColor = System.Drawing.Color.White;
            this.pnlSearch.Controls.Add(this.txtFilter);
            this.pnlSearch.Dock = System.Windows.Forms.DockStyle.Top;
            this.pnlSearch.Location = new System.Drawing.Point(1, 1);
            this.pnlSearch.Name = "pnlSearch";
            this.pnlSearch.Size = new System.Drawing.Size(468, 54);
            this.pnlSearch.TabIndex = 0;
            this.pnlSearch.MouseDown += new System.Windows.Forms.MouseEventHandler(this.DragArea_MouseDown);
            this.pnlSearch.Paint += new System.Windows.Forms.PaintEventHandler(this.PnlSearch_Paint);
            // 
            // txtFilter
            // 
            this.txtFilter.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.txtFilter.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(241)))), ((int)(((byte)(243)))), ((int)(((byte)(244)))));
            this.txtFilter.BorderStyle = System.Windows.Forms.BorderStyle.None;
            this.txtFilter.Font = new System.Drawing.Font("微软雅黑", 11F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.txtFilter.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(32)))), ((int)(((byte)(33)))), ((int)(((byte)(36)))));
            this.txtFilter.Location = new System.Drawing.Point(46, 15);
            this.txtFilter.Name = "txtFilter";
            this.txtFilter.Size = new System.Drawing.Size(400, 25);
            this.txtFilter.TabIndex = 0;
            this.txtFilter.TextChanged += new System.EventHandler(this.TxtFilter_TextChanged);
            this.txtFilter.GotFocus += new System.EventHandler(this.TxtFilter_FocusChanged);
            this.txtFilter.LostFocus += new System.EventHandler(this.TxtFilter_FocusChanged);
            // 
            // lstNames
            // 
            this.lstNames.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
            | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lstNames.BackColor = System.Drawing.Color.White;
            this.lstNames.BorderStyle = System.Windows.Forms.BorderStyle.None;
            this.lstNames.ColumnWidth = 229;
            this.lstNames.DrawMode = System.Windows.Forms.DrawMode.OwnerDrawFixed;
            this.lstNames.Font = new System.Drawing.Font("微软雅黑", 11F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.lstNames.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(32)))), ((int)(((byte)(33)))), ((int)(((byte)(36)))));
            this.lstNames.FormattingEnabled = true;
            this.lstNames.HorizontalScrollbar = false;
            this.lstNames.IntegralHeight = false;
            this.lstNames.ItemHeight = 22;
            this.lstNames.Location = new System.Drawing.Point(1, 55);
            this.lstNames.MultiColumn = true;
            this.lstNames.Name = "lstNames";
            this.lstNames.SelectionMode = System.Windows.Forms.SelectionMode.One;
            this.lstNames.Size = new System.Drawing.Size(468, 220);
            this.lstNames.TabIndex = 1;
            this.lstNames.TabStop = false;
            this.lstNames.DrawItem += new System.Windows.Forms.DrawItemEventHandler(this.LstNames_DrawItem);
            this.lstNames.MouseClick += new System.Windows.Forms.MouseEventHandler(this.LstNames_MouseClick);
            this.lstNames.MouseMove += new System.Windows.Forms.MouseEventHandler(this.LstNames_MouseMove);
            this.lstNames.MouseLeave += new System.EventHandler(this.LstNames_MouseLeave);
            this.lstNames.Resize += new System.EventHandler(this.LstNames_Resize);
            // 
            // pnlFooter
            // 
            this.pnlFooter.BackColor = System.Drawing.Color.White;
            this.pnlFooter.Controls.Add(this.lblPreview);
            this.pnlFooter.Controls.Add(this.btnPrevPage);
            this.pnlFooter.Controls.Add(this.lblPage);
            this.pnlFooter.Controls.Add(this.btnNextPage);
            this.pnlFooter.Controls.Add(this.lblHint);
            this.pnlFooter.Controls.Add(this.btnCancel);
            this.pnlFooter.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.pnlFooter.Location = new System.Drawing.Point(1, 277);
            this.pnlFooter.Name = "pnlFooter";
            this.pnlFooter.Size = new System.Drawing.Size(468, 34);
            this.pnlFooter.TabIndex = 2;
            this.pnlFooter.MouseDown += new System.Windows.Forms.MouseEventHandler(this.DragArea_MouseDown);
            this.pnlFooter.Paint += new System.Windows.Forms.PaintEventHandler(this.PnlFooter_Paint);
            // 
            // lblPreview
            // 
            this.lblPreview.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lblPreview.AutoEllipsis = true;
            this.lblPreview.Font = new System.Drawing.Font("微软雅黑", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.lblPreview.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(95)))), ((int)(((byte)(99)))), ((int)(((byte)(104)))));
            this.lblPreview.Location = new System.Drawing.Point(12, 0);
            this.lblPreview.Name = "lblPreview";
            this.lblPreview.Size = new System.Drawing.Size(146, 34);
            this.lblPreview.TabIndex = 0;
            this.lblPreview.Text = "已写入：";
            this.lblPreview.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            this.lblPreview.MouseDown += new System.Windows.Forms.MouseEventHandler(this.DragArea_MouseDown);
            // 
            // btnPrevPage
            // 
            this.btnPrevPage.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnPrevPage.BackColor = System.Drawing.Color.White;
            this.btnPrevPage.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnPrevPage.Enabled = false;
            this.btnPrevPage.FlatAppearance.BorderSize = 0;
            this.btnPrevPage.FlatAppearance.MouseDownBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(232)))), ((int)(((byte)(234)))), ((int)(((byte)(237)))));
            this.btnPrevPage.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(241)))), ((int)(((byte)(243)))), ((int)(((byte)(244)))));
            this.btnPrevPage.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnPrevPage.Font = new System.Drawing.Font("微软雅黑", 10F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.btnPrevPage.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(95)))), ((int)(((byte)(99)))), ((int)(((byte)(104)))));
            this.btnPrevPage.Location = new System.Drawing.Point(212, 5);
            this.btnPrevPage.Name = "btnPrevPage";
            this.btnPrevPage.Size = new System.Drawing.Size(18, 24);
            this.btnPrevPage.TabIndex = 1;
            this.btnPrevPage.TabStop = false;
            this.btnPrevPage.Text = "‹";
            this.btnPrevPage.UseVisualStyleBackColor = false;
            this.btnPrevPage.Click += new System.EventHandler(this.BtnPrevPage_Click);
            // 
            // lblPage
            // 
            this.lblPage.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.lblPage.Font = new System.Drawing.Font("微软雅黑", 8.5F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.lblPage.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(95)))), ((int)(((byte)(99)))), ((int)(((byte)(104)))));
            this.lblPage.Location = new System.Drawing.Point(232, 0);
            this.lblPage.Name = "lblPage";
            this.lblPage.Size = new System.Drawing.Size(28, 34);
            this.lblPage.TabIndex = 2;
            this.lblPage.Text = "1/1";
            this.lblPage.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            this.lblPage.MouseDown += new System.Windows.Forms.MouseEventHandler(this.DragArea_MouseDown);
            // 
            // btnNextPage
            // 
            this.btnNextPage.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnNextPage.BackColor = System.Drawing.Color.White;
            this.btnNextPage.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnNextPage.Enabled = false;
            this.btnNextPage.FlatAppearance.BorderSize = 0;
            this.btnNextPage.FlatAppearance.MouseDownBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(232)))), ((int)(((byte)(234)))), ((int)(((byte)(237)))));
            this.btnNextPage.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(241)))), ((int)(((byte)(243)))), ((int)(((byte)(244)))));
            this.btnNextPage.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnNextPage.Font = new System.Drawing.Font("微软雅黑", 10F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.btnNextPage.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(95)))), ((int)(((byte)(99)))), ((int)(((byte)(104)))));
            this.btnNextPage.Location = new System.Drawing.Point(262, 5);
            this.btnNextPage.Name = "btnNextPage";
            this.btnNextPage.Size = new System.Drawing.Size(18, 24);
            this.btnNextPage.TabIndex = 3;
            this.btnNextPage.TabStop = false;
            this.btnNextPage.Text = "›";
            this.btnNextPage.UseVisualStyleBackColor = false;
            this.btnNextPage.Click += new System.EventHandler(this.BtnNextPage_Click);
            // 
            // lblHint
            // 
            this.lblHint.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.lblHint.Font = new System.Drawing.Font("微软雅黑", 8.5F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.lblHint.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(154)))), ((int)(((byte)(160)))), ((int)(((byte)(166)))));
            this.lblHint.Location = new System.Drawing.Point(287, 0);
            this.lblHint.Name = "lblHint";
            this.lblHint.Size = new System.Drawing.Size(116, 34);
            this.lblHint.TabIndex = 1;
            this.lblHint.Text = "1-9 · ←→↑↓ · Enter";
            this.lblHint.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            this.lblHint.MouseDown += new System.Windows.Forms.MouseEventHandler(this.DragArea_MouseDown);
            // 
            // btnCancel
            // 
            this.btnCancel.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnCancel.BackColor = System.Drawing.Color.White;
            this.btnCancel.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnCancel.FlatAppearance.BorderSize = 0;
            this.btnCancel.FlatAppearance.MouseDownBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(232)))), ((int)(((byte)(234)))), ((int)(((byte)(237)))));
            this.btnCancel.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(241)))), ((int)(((byte)(243)))), ((int)(((byte)(244)))));
            this.btnCancel.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnCancel.Font = new System.Drawing.Font("微软雅黑", 10F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.btnCancel.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(95)))), ((int)(((byte)(99)))), ((int)(((byte)(104)))));
            this.btnCancel.Location = new System.Drawing.Point(426, 5);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new System.Drawing.Size(24, 24);
            this.btnCancel.TabIndex = 2;
            this.btnCancel.TabStop = false;
            this.btnCancel.Text = "✕";
            this.btnCancel.UseVisualStyleBackColor = false;
            this.btnCancel.Click += new System.EventHandler(this.BtnClose_Click);
            // 
            // pnlResize
            // 
            this.pnlResize.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.pnlResize.BackColor = System.Drawing.Color.White;
            this.pnlResize.Cursor = System.Windows.Forms.Cursors.SizeNWSE;
            this.pnlResize.Location = new System.Drawing.Point(455, 295);
            this.pnlResize.Name = "pnlResize";
            this.pnlResize.Size = new System.Drawing.Size(14, 14);
            this.pnlResize.TabIndex = 3;
            this.pnlResize.MouseDown += new System.Windows.Forms.MouseEventHandler(this.PnlResize_MouseDown);
            this.pnlResize.MouseMove += new System.Windows.Forms.MouseEventHandler(this.PnlResize_MouseMove);
            this.pnlResize.MouseUp += new System.Windows.Forms.MouseEventHandler(this.PnlResize_MouseUp);
            this.pnlResize.Paint += new System.Windows.Forms.PaintEventHandler(this.PnlResize_Paint);
            // 
            // MemberNamePickerForm
            // 
            this.BackColor = System.Drawing.Color.White;
            this.CancelButton = this.btnCancel;
            this.ClientSize = new System.Drawing.Size(470, 310);
            this.Controls.Add(this.lstNames);
            this.Controls.Add(this.pnlSearch);
            this.Controls.Add(this.pnlFooter);
            this.Controls.Add(this.pnlResize);
            this.Font = new System.Drawing.Font("微软雅黑", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
            this.KeyPreview = true;
            this.MinimumSize = new System.Drawing.Size(470, 310);
            this.Name = "MemberNamePickerForm";
            this.Padding = new System.Windows.Forms.Padding(1);
            this.ShowIcon = false;
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "选择部材名";
            this.pnlSearch.ResumeLayout(false);
            this.pnlSearch.PerformLayout();
            this.pnlFooter.ResumeLayout(false);
            this.ResumeLayout(false);
        }

        private System.Windows.Forms.Panel pnlSearch;
        private System.Windows.Forms.TextBox txtFilter;
        private System.Windows.Forms.ListBox lstNames;
        private System.Windows.Forms.Panel pnlFooter;
        private System.Windows.Forms.Label lblPreview;
        private System.Windows.Forms.Button btnPrevPage;
        private System.Windows.Forms.Label lblPage;
        private System.Windows.Forms.Button btnNextPage;
        private System.Windows.Forms.Label lblHint;
        private System.Windows.Forms.Button btnCancel;
        private System.Windows.Forms.Panel pnlResize;

        #endregion
    }
}
