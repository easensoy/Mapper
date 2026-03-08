namespace MapperUI
{
    partial class DebugConsoleForm
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && components != null) components.Dispose();
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            this.dgvLog = new System.Windows.Forms.DataGridView();
            this.colLogTime = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colLogStep = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colLogAction = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.btnClearLog = new System.Windows.Forms.Button();

            ((System.ComponentModel.ISupportInitialize)(this.dgvLog)).BeginInit();
            this.SuspendLayout();

            // btnClearLog
            this.btnClearLog.Dock = System.Windows.Forms.DockStyle.Top;
            this.btnClearLog.Height = 28;
            this.btnClearLog.Text = "Clear Log";
            this.btnClearLog.BackColor = System.Drawing.Color.FromArgb(60, 60, 60);
            this.btnClearLog.ForeColor = System.Drawing.Color.White;
            this.btnClearLog.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnClearLog.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(80, 80, 80);
            this.btnClearLog.Name = "btnClearLog";
            this.btnClearLog.TabIndex = 0;
            this.btnClearLog.Click += new System.EventHandler(this.btnClearLog_Click);

            // dgvLog
            this.dgvLog.AllowUserToAddRows = false;
            this.dgvLog.AllowUserToDeleteRows = false;
            this.dgvLog.BackgroundColor = System.Drawing.Color.FromArgb(30, 30, 30);
            this.dgvLog.BorderStyle = System.Windows.Forms.BorderStyle.None;
            this.dgvLog.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dgvLog.ColumnHeadersDefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(45, 45, 45);
            this.dgvLog.ColumnHeadersDefaultCellStyle.ForeColor = System.Drawing.Color.White;
            this.dgvLog.ColumnHeadersDefaultCellStyle.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold);
            this.dgvLog.EnableHeadersVisualStyles = false;
            this.dgvLog.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
                this.colLogTime, this.colLogStep, this.colLogAction });
            this.dgvLog.DefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(30, 30, 30);
            this.dgvLog.DefaultCellStyle.ForeColor = System.Drawing.Color.LimeGreen;
            this.dgvLog.DefaultCellStyle.Font = new System.Drawing.Font("Consolas", 9F);
            this.dgvLog.DefaultCellStyle.SelectionBackColor = System.Drawing.Color.FromArgb(60, 60, 60);
            this.dgvLog.GridColor = System.Drawing.Color.FromArgb(55, 55, 55);
            this.dgvLog.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dgvLog.Name = "dgvLog";
            this.dgvLog.ReadOnly = true;
            this.dgvLog.RowHeadersVisible = false;
            this.dgvLog.SelectionMode = System.Windows.Forms.DataGridViewSelectionMode.FullRowSelect;
            this.dgvLog.TabIndex = 1;

            this.colLogTime.HeaderText = "Timestamp";
            this.colLogTime.Name = "colLogTime";
            this.colLogTime.ReadOnly = true;
            this.colLogTime.Width = 110;
            this.colLogTime.DefaultCellStyle.ForeColor = System.Drawing.Color.Cyan;

            this.colLogStep.HeaderText = "Step";
            this.colLogStep.Name = "colLogStep";
            this.colLogStep.ReadOnly = true;
            this.colLogStep.Width = 90;
            this.colLogStep.DefaultCellStyle.Font = new System.Drawing.Font("Consolas", 9F, System.Drawing.FontStyle.Bold);

            this.colLogAction.HeaderText = "Action";
            this.colLogAction.Name = "colLogAction";
            this.colLogAction.ReadOnly = true;
            this.colLogAction.AutoSizeMode = System.Windows.Forms.DataGridViewAutoSizeColumnMode.Fill;

            // Form
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.BackColor = System.Drawing.Color.FromArgb(30, 30, 30);
            this.ClientSize = new System.Drawing.Size(1000, 500);
            this.Controls.Add(this.dgvLog);
            this.Controls.Add(this.btnClearLog);
            this.MinimumSize = new System.Drawing.Size(700, 350);
            this.Name = "DebugConsoleForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
            this.Text = "Debug Console";

            ((System.ComponentModel.ISupportInitialize)(this.dgvLog)).EndInit();
            this.ResumeLayout(false);
        }

        #endregion

        private System.Windows.Forms.DataGridView dgvLog;
        private System.Windows.Forms.DataGridViewTextBoxColumn colLogTime;
        private System.Windows.Forms.DataGridViewTextBoxColumn colLogStep;
        private System.Windows.Forms.DataGridViewTextBoxColumn colLogAction;
        private System.Windows.Forms.Button btnClearLog;
    }
}