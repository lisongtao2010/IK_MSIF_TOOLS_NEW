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
            this.lblHint = new System.Windows.Forms.Label();
            this.lblFilter = new System.Windows.Forms.Label();
            this.txtFilter = new System.Windows.Forms.TextBox();
            this.lvNames = new System.Windows.Forms.ListView();
            this.colMemberName = new System.Windows.Forms.ColumnHeader();
            this.colPartNames = new System.Windows.Forms.ColumnHeader();
            this.lblPreview = new System.Windows.Forms.Label();
            this.lblCustom = new System.Windows.Forms.Label();
            this.txtCustom = new System.Windows.Forms.TextBox();
            this.btnOk = new System.Windows.Forms.Button();
            this.btnCancel = new System.Windows.Forms.Button();
            this.SuspendLayout();
            // 
            // lblHint
            // 
            this.lblHint.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lblHint.Font = new System.Drawing.Font("微软雅黑", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.lblHint.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(52)))), ((int)(((byte)(73)))), ((int)(((byte)(94)))));
            this.lblHint.Location = new System.Drawing.Point(14, 10);
            this.lblHint.Name = "lblHint";
            this.lblHint.Size = new System.Drawing.Size(472, 46);
            this.lblHint.TabIndex = 0;
            this.lblHint.Text = "勾选要用的部材名（可多选，确定后用「|」连接）。\r\n" +
                "这一格里原本用「|」分开的项已经勾上；列表里没有的名字，写在下面的「手工输入」里。";
            // 
            // lblFilter
            // 
            this.lblFilter.Font = new System.Drawing.Font("微软雅黑", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.lblFilter.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(52)))), ((int)(((byte)(73)))), ((int)(((byte)(94)))));
            this.lblFilter.Location = new System.Drawing.Point(14, 66);
            this.lblFilter.Name = "lblFilter";
            this.lblFilter.Size = new System.Drawing.Size(52, 22);
            this.lblFilter.TabIndex = 1;
            this.lblFilter.Text = "过滤：";
            this.lblFilter.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            // 
            // txtFilter
            // 
            this.txtFilter.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.txtFilter.Font = new System.Drawing.Font("微软雅黑", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.txtFilter.Location = new System.Drawing.Point(70, 63);
            this.txtFilter.Name = "txtFilter";
            this.txtFilter.Size = new System.Drawing.Size(416, 26);
            this.txtFilter.TabIndex = 2;
            this.txtFilter.TextChanged += new System.EventHandler(this.TxtFilter_TextChanged);
            // 
            // lvNames
            // 
            this.lvNames.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
            | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lvNames.CheckBoxes = true;
            this.lvNames.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
            this.colMemberName,
            this.colPartNames});
            this.lvNames.Font = new System.Drawing.Font("微软雅黑", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.lvNames.FullRowSelect = true;
            this.lvNames.HeaderStyle = System.Windows.Forms.ColumnHeaderStyle.Nonclickable;
            this.lvNames.HideSelection = false;
            this.lvNames.Location = new System.Drawing.Point(14, 96);
            this.lvNames.MultiSelect = true;
            this.lvNames.Name = "lvNames";
            this.lvNames.Size = new System.Drawing.Size(472, 320);
            this.lvNames.TabIndex = 3;
            this.lvNames.UseCompatibleStateImageBehavior = false;
            this.lvNames.View = System.Windows.Forms.View.Details;
            this.lvNames.ItemChecked += new System.Windows.Forms.ItemCheckedEventHandler(this.LvNames_ItemChecked);
            this.lvNames.MouseDoubleClick += new System.Windows.Forms.MouseEventHandler(this.LvNames_MouseDoubleClick);
            // 
            // colMemberName
            // 
            this.colMemberName.Text = "部材名";
            this.colMemberName.Width = 180;
            // 
            // colPartNames
            // 
            this.colPartNames.Text = "该部材名下的部品名";
            this.colPartNames.Width = 268;
            // 
            // lblPreview
            // 
            this.lblPreview.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lblPreview.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(242)))), ((int)(((byte)(244)))), ((int)(((byte)(247)))));
            this.lblPreview.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.lblPreview.Font = new System.Drawing.Font("微软雅黑", 9F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.lblPreview.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(52)))), ((int)(((byte)(73)))), ((int)(((byte)(94)))));
            this.lblPreview.Location = new System.Drawing.Point(14, 424);
            this.lblPreview.Name = "lblPreview";
            this.lblPreview.Padding = new System.Windows.Forms.Padding(6, 0, 6, 0);
            this.lblPreview.Size = new System.Drawing.Size(472, 34);
            this.lblPreview.TabIndex = 4;
            this.lblPreview.Text = "将写入：";
            this.lblPreview.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            // 
            // lblCustom
            // 
            this.lblCustom.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.lblCustom.Font = new System.Drawing.Font("微软雅黑", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.lblCustom.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(52)))), ((int)(((byte)(73)))), ((int)(((byte)(94)))));
            this.lblCustom.Location = new System.Drawing.Point(14, 468);
            this.lblCustom.Name = "lblCustom";
            this.lblCustom.Size = new System.Drawing.Size(72, 22);
            this.lblCustom.TabIndex = 5;
            this.lblCustom.Text = "手工输入：";
            this.lblCustom.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            // 
            // txtCustom
            // 
            this.txtCustom.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.txtCustom.Font = new System.Drawing.Font("微软雅黑", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.txtCustom.Location = new System.Drawing.Point(88, 465);
            this.txtCustom.Name = "txtCustom";
            this.txtCustom.Size = new System.Drawing.Size(398, 26);
            this.txtCustom.TabIndex = 6;
            this.txtCustom.TextChanged += new System.EventHandler(this.TxtCustom_TextChanged);
            // 
            // btnOk
            // 
            this.btnOk.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnOk.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(232)))), ((int)(((byte)(237)))), ((int)(((byte)(243)))));
            this.btnOk.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnOk.DialogResult = System.Windows.Forms.DialogResult.OK;
            this.btnOk.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(((int)(((byte)(199)))), ((int)(((byte)(208)))), ((int)(((byte)(219)))));
            this.btnOk.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(217)))), ((int)(((byte)(226)))), ((int)(((byte)(236)))));
            this.btnOk.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnOk.Font = new System.Drawing.Font("微软雅黑", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.btnOk.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(52)))), ((int)(((byte)(73)))), ((int)(((byte)(94)))));
            this.btnOk.Location = new System.Drawing.Point(282, 508);
            this.btnOk.Name = "btnOk";
            this.btnOk.Size = new System.Drawing.Size(96, 32);
            this.btnOk.TabIndex = 7;
            this.btnOk.Text = "确定";
            this.btnOk.UseVisualStyleBackColor = false;
            this.btnOk.Click += new System.EventHandler(this.BtnOk_Click);
            // 
            // btnCancel
            // 
            this.btnCancel.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnCancel.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(232)))), ((int)(((byte)(237)))), ((int)(((byte)(243)))));
            this.btnCancel.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnCancel.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.btnCancel.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(((int)(((byte)(199)))), ((int)(((byte)(208)))), ((int)(((byte)(219)))));
            this.btnCancel.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(217)))), ((int)(((byte)(226)))), ((int)(((byte)(236)))));
            this.btnCancel.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnCancel.Font = new System.Drawing.Font("微软雅黑", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.btnCancel.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(52)))), ((int)(((byte)(73)))), ((int)(((byte)(94)))));
            this.btnCancel.Location = new System.Drawing.Point(390, 508);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new System.Drawing.Size(96, 32);
            this.btnCancel.TabIndex = 8;
            this.btnCancel.Text = "取消";
            this.btnCancel.UseVisualStyleBackColor = false;
            // 
            // MemberNamePickerForm
            // 
            this.AcceptButton = this.btnOk;
            this.BackColor = System.Drawing.Color.White;
            this.CancelButton = this.btnCancel;
            this.ClientSize = new System.Drawing.Size(500, 552);
            this.Controls.Add(this.lblHint);
            this.Controls.Add(this.lblFilter);
            this.Controls.Add(this.txtFilter);
            this.Controls.Add(this.lvNames);
            this.Controls.Add(this.lblPreview);
            this.Controls.Add(this.lblCustom);
            this.Controls.Add(this.txtCustom);
            this.Controls.Add(this.btnOk);
            this.Controls.Add(this.btnCancel);
            this.Font = new System.Drawing.Font("微软雅黑", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.KeyPreview = true;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.MinimumSize = new System.Drawing.Size(440, 440);
            this.Name = "MemberNamePickerForm";
            this.ShowIcon = false;
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "选择部材名";
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        private System.Windows.Forms.Label lblHint;
        private System.Windows.Forms.Label lblFilter;
        private System.Windows.Forms.TextBox txtFilter;
        private System.Windows.Forms.ListView lvNames;
        private System.Windows.Forms.ColumnHeader colMemberName;
        private System.Windows.Forms.ColumnHeader colPartNames;
        private System.Windows.Forms.Label lblPreview;
        private System.Windows.Forms.Label lblCustom;
        private System.Windows.Forms.TextBox txtCustom;
        private System.Windows.Forms.Button btnOk;
        private System.Windows.Forms.Button btnCancel;

        #endregion
    }
}
