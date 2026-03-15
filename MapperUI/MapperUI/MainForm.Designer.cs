namespace MapperUI
{
    partial class MainForm
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
            this.menuStrip = new System.Windows.Forms.MenuStrip();
            this.fileToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.dataToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.buildToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.menuItemDebugConsole = new System.Windows.Forms.ToolStripMenuItem();
            this.lblVueOneModel = new System.Windows.Forms.Label();
            this.txtModelPath = new System.Windows.Forms.TextBox();
            this.btnMappingRules = new System.Windows.Forms.Button();
            this.btnBrowse = new System.Windows.Forms.Button();
            this.btnGenerateCode = new System.Windows.Forms.Button();
            this.grpValidation = new System.Windows.Forms.GroupBox();
            this.pnlDetectedInfo = new System.Windows.Forms.FlowLayoutPanel();
            this.lblDetectedPrefix = new System.Windows.Forms.Label();
            this.lblDetectedType = new System.Windows.Forms.Label();
            this.lblNamePrefix = new System.Windows.Forms.Label();
            this.lblDetectedName = new System.Windows.Forms.Label();
            this.lblStatePrefix = new System.Windows.Forms.Label();
            this.lblDetectedStates = new System.Windows.Forms.Label();
            this.lblValidationPrefix = new System.Windows.Forms.Label();
            this.lblValidationStatus = new System.Windows.Forms.Label();
            this.dgvMappingRules = new System.Windows.Forms.DataGridView();
            this.colVueOneElement = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colIEC61499Element = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colMappingType = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colMappingRule = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colMappingValidated = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.grpMappingInfo = new System.Windows.Forms.GroupBox();
            this.splitMain = new System.Windows.Forms.SplitContainer();
            this.dgvComponents = new System.Windows.Forms.DataGridView();
            this.colComponent = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colType = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colTemplate = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.splitDetail = new System.Windows.Forms.SplitContainer();
            this.grpInputs = new System.Windows.Forms.GroupBox();
            this.dgvInputs = new System.Windows.Forms.DataGridView();
            this.colInputName = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colInputAddress = new System.Windows.Forms.DataGridViewComboBoxColumn();
            this.grpOutputs = new System.Windows.Forms.GroupBox();
            this.dgvOutputs = new System.Windows.Forms.DataGridView();
            this.colOutputName = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colOutputAddress = new System.Windows.Forms.DataGridViewComboBoxColumn();
            this.statusStrip = new System.Windows.Forms.StatusStrip();
            this.lblStatus = new System.Windows.Forms.ToolStripStatusLabel();

            this.menuStrip.SuspendLayout();
            this.grpValidation.SuspendLayout();
            this.pnlDetectedInfo.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dgvMappingRules)).BeginInit();
            this.grpMappingInfo.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitMain)).BeginInit();
            this.splitMain.Panel1.SuspendLayout();
            this.splitMain.Panel2.SuspendLayout();
            this.splitMain.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dgvComponents)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.splitDetail)).BeginInit();
            this.splitDetail.Panel1.SuspendLayout();
            this.splitDetail.Panel2.SuspendLayout();
            this.splitDetail.SuspendLayout();
            this.grpInputs.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dgvInputs)).BeginInit();
            this.grpOutputs.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dgvOutputs)).BeginInit();
            this.statusStrip.SuspendLayout();
            this.SuspendLayout();

            // Menu
            this.menuStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
                this.fileToolStripMenuItem, this.dataToolStripMenuItem, this.buildToolStripMenuItem });
            this.menuStrip.Location = new System.Drawing.Point(0, 0);
            this.menuStrip.Size = new System.Drawing.Size(1400, 24);
            this.fileToolStripMenuItem.Text = "File";
            this.dataToolStripMenuItem.Text = "Data";
            this.buildToolStripMenuItem.Text = "Build";
            this.buildToolStripMenuItem.DropDownItems.Add(this.menuItemDebugConsole);
            this.menuItemDebugConsole.Text = "Debug Console";
            this.menuItemDebugConsole.Click += new System.EventHandler(this.menuItemDebugConsole_Click);

            // Toolbar
            this.lblVueOneModel.AutoSize = true;
            this.lblVueOneModel.Location = new System.Drawing.Point(12, 33);
            this.lblVueOneModel.Text = "vueOne Model:";

            this.txtModelPath.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.txtModelPath.Location = new System.Drawing.Point(102, 30);
            this.txtModelPath.ReadOnly = true;
            this.txtModelPath.Size = new System.Drawing.Size(1000, 23);

            this.btnMappingRules.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            this.btnMappingRules.Location = new System.Drawing.Point(1110, 28);
            this.btnMappingRules.Size = new System.Drawing.Size(80, 25);
            this.btnMappingRules.Text = "Mapping";
            this.btnMappingRules.Click += new System.EventHandler(this.btnMappingRules_Click);

            this.btnBrowse.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            this.btnBrowse.Location = new System.Drawing.Point(1196, 28);
            this.btnBrowse.Size = new System.Drawing.Size(60, 25);
            this.btnBrowse.Text = "Browse";
            this.btnBrowse.Click += new System.EventHandler(this.btnBrowse_Click);

            this.btnGenerateCode.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            this.btnGenerateCode.BackColor = System.Drawing.Color.FromArgb(0, 122, 204);
            this.btnGenerateCode.Enabled = false;
            this.btnGenerateCode.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnGenerateCode.FlatAppearance.BorderSize = 0;
            this.btnGenerateCode.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold);
            this.btnGenerateCode.ForeColor = System.Drawing.Color.White;
            this.btnGenerateCode.Location = new System.Drawing.Point(1262, 28);
            this.btnGenerateCode.Size = new System.Drawing.Size(126, 25);
            this.btnGenerateCode.Text = "Generate Code";
            this.btnGenerateCode.UseVisualStyleBackColor = false;
            this.btnGenerateCode.Click += new System.EventHandler(this.btnGenerateCode_Click);

            // Validation Output
            this.grpValidation.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.grpValidation.Controls.Add(this.dgvMappingRules);
            this.grpValidation.Controls.Add(this.pnlDetectedInfo);
            this.grpValidation.Location = new System.Drawing.Point(12, 60);
            this.grpValidation.Size = new System.Drawing.Size(1376, 380);
            this.grpValidation.Text = "Validation Output";

            this.pnlDetectedInfo.AutoSize = true;
            this.pnlDetectedInfo.Controls.Add(this.lblDetectedPrefix);
            this.pnlDetectedInfo.Controls.Add(this.lblDetectedType);
            this.pnlDetectedInfo.Controls.Add(this.lblNamePrefix);
            this.pnlDetectedInfo.Controls.Add(this.lblDetectedName);
            this.pnlDetectedInfo.Controls.Add(this.lblStatePrefix);
            this.pnlDetectedInfo.Controls.Add(this.lblDetectedStates);
            this.pnlDetectedInfo.Controls.Add(this.lblValidationPrefix);
            this.pnlDetectedInfo.Controls.Add(this.lblValidationStatus);
            this.pnlDetectedInfo.Dock = System.Windows.Forms.DockStyle.Top;
            this.pnlDetectedInfo.FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight;
            this.pnlDetectedInfo.Padding = new System.Windows.Forms.Padding(4, 2, 4, 2);
            this.pnlDetectedInfo.Size = new System.Drawing.Size(1370, 26);
            this.pnlDetectedInfo.WrapContents = false;

            SetLabel(this.lblDetectedPrefix, "Type: ", false);
            SetLabel(this.lblDetectedType, "-", true);
            SetLabel(this.lblNamePrefix, "  Name: ", false);
            SetLabel(this.lblDetectedName, "-", true);
            SetLabel(this.lblStatePrefix, "  States: ", false);
            SetLabel(this.lblDetectedStates, "-", true);
            SetLabel(this.lblValidationPrefix, "  Status: ", false);
            SetLabel(this.lblValidationStatus, "-", true);

            // Mapping rules grid — columns sized to be readable
            this.dgvMappingRules.AllowUserToAddRows = false;
            this.dgvMappingRules.AllowUserToDeleteRows = false;
            this.dgvMappingRules.BackgroundColor = System.Drawing.Color.White;
            this.dgvMappingRules.BorderStyle = System.Windows.Forms.BorderStyle.None;
            this.dgvMappingRules.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            this.dgvMappingRules.ColumnHeadersHeight = 32;
            this.dgvMappingRules.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
                this.colVueOneElement, this.colIEC61499Element, this.colMappingType, this.colMappingRule, this.colMappingValidated });
            this.dgvMappingRules.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dgvMappingRules.ReadOnly = true;
            this.dgvMappingRules.RowHeadersVisible = false;
            this.dgvMappingRules.SelectionMode = System.Windows.Forms.DataGridViewSelectionMode.FullRowSelect;

            this.colVueOneElement.HeaderText = "VueOne Element";
            this.colVueOneElement.Width = 140;
            this.colIEC61499Element.HeaderText = "IEC 61499 Element";
            this.colIEC61499Element.Width = 160;
            this.colMappingType.HeaderText = "Mapping Type";
            this.colMappingType.Width = 145;
            this.colMappingRule.HeaderText = "Transformation Rule";
            this.colMappingRule.AutoSizeMode = System.Windows.Forms.DataGridViewAutoSizeColumnMode.Fill;
            this.colMappingValidated.HeaderText = "Validated";
            this.colMappingValidated.Width = 90;
            this.colMappingValidated.DefaultCellStyle.Alignment = System.Windows.Forms.DataGridViewContentAlignment.MiddleCenter;
            this.colMappingValidated.DefaultCellStyle.Font = new System.Drawing.Font("Segoe UI", 11F, System.Drawing.FontStyle.Bold);

            // Mapping Information
            this.grpMappingInfo.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.grpMappingInfo.Controls.Add(this.splitMain);
            this.grpMappingInfo.Location = new System.Drawing.Point(12, 446);
            this.grpMappingInfo.Size = new System.Drawing.Size(1376, 320);
            this.grpMappingInfo.Text = "Mapping Information";

            // Main split: Left=Components, Right=Detail
            this.splitMain.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitMain.SplitterDistance = 760;

            // Left: Components grid
            this.dgvComponents.AllowUserToAddRows = false;
            this.dgvComponents.AllowUserToDeleteRows = false;
            this.dgvComponents.BackgroundColor = System.Drawing.SystemColors.Window;
            this.dgvComponents.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dgvComponents.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
                this.colComponent, this.colType, this.colTemplate });
            this.dgvComponents.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dgvComponents.ReadOnly = true;
            this.dgvComponents.RowHeadersVisible = true;
            this.dgvComponents.MultiSelect = true;
            this.dgvComponents.SelectionMode = System.Windows.Forms.DataGridViewSelectionMode.FullRowSelect;
            this.dgvComponents.DefaultCellStyle.Font = new System.Drawing.Font("Segoe UI", 9F);
            this.dgvComponents.SelectionChanged += new System.EventHandler(this.dgvComponents_SelectionChanged);
            this.splitMain.Panel1.Controls.Add(this.dgvComponents);

            this.colComponent.HeaderText = "Component";
            this.colComponent.Width = 130;
            this.colComponent.ReadOnly = true;
            this.colType.HeaderText = "Type";
            this.colType.Width = 75;
            this.colType.ReadOnly = true;
            this.colTemplate.HeaderText = "Template";
            this.colTemplate.ReadOnly = true;
            this.colTemplate.AutoSizeMode = System.Windows.Forms.DataGridViewAutoSizeColumnMode.Fill;

            // Right: Detail split — Top=Inputs, Bottom=Outputs
            this.splitDetail.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitDetail.Orientation = System.Windows.Forms.Orientation.Horizontal;
            this.splitDetail.SplitterDistance = 200;
            this.splitMain.Panel2.Controls.Add(this.splitDetail);

            // Top: Inputs / States
            this.grpInputs.Controls.Add(this.dgvInputs);
            this.grpInputs.Dock = System.Windows.Forms.DockStyle.Fill;
            this.grpInputs.Text = "States";
            this.splitDetail.Panel1.Controls.Add(this.grpInputs);

            this.dgvInputs.AllowUserToAddRows = false;
            this.dgvInputs.AllowUserToDeleteRows = false;
            this.dgvInputs.AutoSizeColumnsMode = System.Windows.Forms.DataGridViewAutoSizeColumnsMode.Fill;
            this.dgvInputs.BackgroundColor = System.Drawing.Color.White;
            this.dgvInputs.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dgvInputs.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] { this.colInputName, this.colInputAddress });
            this.dgvInputs.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dgvInputs.RowHeadersVisible = false;

            this.colInputName.FillWeight = 60F;
            this.colInputName.HeaderText = "State";
            this.colInputName.ReadOnly = true;
            this.colInputAddress.FillWeight = 40F;
            this.colInputAddress.HeaderText = "Address";

            // Bottom: Outputs / Validation
            this.grpOutputs.Controls.Add(this.dgvOutputs);
            this.grpOutputs.Dock = System.Windows.Forms.DockStyle.Fill;
            this.grpOutputs.Text = "Validation";
            this.splitDetail.Panel2.Controls.Add(this.grpOutputs);

            this.dgvOutputs.AllowUserToAddRows = false;
            this.dgvOutputs.AllowUserToDeleteRows = false;
            this.dgvOutputs.AutoSizeColumnsMode = System.Windows.Forms.DataGridViewAutoSizeColumnsMode.Fill;
            this.dgvOutputs.BackgroundColor = System.Drawing.Color.White;
            this.dgvOutputs.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dgvOutputs.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] { this.colOutputName, this.colOutputAddress });
            this.dgvOutputs.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dgvOutputs.ReadOnly = true;
            this.dgvOutputs.RowHeadersVisible = false;

            this.colOutputName.FillWeight = 70F;
            this.colOutputName.HeaderText = "Output";
            this.colOutputName.ReadOnly = true;
            this.colOutputAddress.FillWeight = 30F;
            this.colOutputAddress.HeaderText = "Address";
            this.colOutputAddress.ReadOnly = true;

            // Status strip
            this.statusStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[] { this.lblStatus });
            this.lblStatus.Text = "Ready";

            // Form
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1400, 792);
            this.Controls.Add(this.grpMappingInfo);
            this.Controls.Add(this.grpValidation);
            this.Controls.Add(this.btnGenerateCode);
            this.Controls.Add(this.btnBrowse);
            this.Controls.Add(this.btnMappingRules);
            this.Controls.Add(this.txtModelPath);
            this.Controls.Add(this.lblVueOneModel);
            this.Controls.Add(this.statusStrip);
            this.Controls.Add(this.menuStrip);
            this.MainMenuStrip = this.menuStrip;
            this.MinimumSize = new System.Drawing.Size(1100, 700);
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "VueOne Mapper for IEC 61499";

            this.menuStrip.ResumeLayout(false);
            this.menuStrip.PerformLayout();
            this.grpValidation.ResumeLayout(false);
            this.grpValidation.PerformLayout();
            this.pnlDetectedInfo.ResumeLayout(false);
            this.pnlDetectedInfo.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dgvMappingRules)).EndInit();
            this.grpMappingInfo.ResumeLayout(false);
            this.splitMain.Panel1.ResumeLayout(false);
            this.splitMain.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitMain)).EndInit();
            this.splitMain.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.dgvComponents)).EndInit();
            this.splitDetail.Panel1.ResumeLayout(false);
            this.splitDetail.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitDetail)).EndInit();
            this.splitDetail.ResumeLayout(false);
            this.grpInputs.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.dgvInputs)).EndInit();
            this.grpOutputs.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.dgvOutputs)).EndInit();
            this.statusStrip.ResumeLayout(false);
            this.statusStrip.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        private static void SetLabel(System.Windows.Forms.Label lbl, string text, bool bold)
        {
            lbl.AutoSize = true;
            lbl.Text = text;
            lbl.Font = new System.Drawing.Font("Segoe UI", 9F,
                bold ? System.Drawing.FontStyle.Bold : System.Drawing.FontStyle.Regular);
            lbl.Margin = new System.Windows.Forms.Padding(0, 4, 0, 0);
        }

        #endregion

        private System.Windows.Forms.MenuStrip menuStrip;
        private System.Windows.Forms.ToolStripMenuItem fileToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem dataToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem buildToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem menuItemDebugConsole;
        private System.Windows.Forms.Label lblVueOneModel;
        private System.Windows.Forms.TextBox txtModelPath;
        private System.Windows.Forms.Button btnMappingRules;
        private System.Windows.Forms.Button btnBrowse;
        private System.Windows.Forms.Button btnGenerateCode;
        private System.Windows.Forms.GroupBox grpValidation;
        private System.Windows.Forms.FlowLayoutPanel pnlDetectedInfo;
        private System.Windows.Forms.Label lblDetectedPrefix;
        private System.Windows.Forms.Label lblDetectedType;
        private System.Windows.Forms.Label lblNamePrefix;
        private System.Windows.Forms.Label lblDetectedName;
        private System.Windows.Forms.Label lblStatePrefix;
        private System.Windows.Forms.Label lblDetectedStates;
        private System.Windows.Forms.Label lblValidationPrefix;
        private System.Windows.Forms.Label lblValidationStatus;
        private System.Windows.Forms.DataGridView dgvMappingRules;
        private System.Windows.Forms.DataGridViewTextBoxColumn colVueOneElement;
        private System.Windows.Forms.DataGridViewTextBoxColumn colIEC61499Element;
        private System.Windows.Forms.DataGridViewTextBoxColumn colMappingType;
        private System.Windows.Forms.DataGridViewTextBoxColumn colMappingRule;
        private System.Windows.Forms.DataGridViewTextBoxColumn colMappingValidated;
        private System.Windows.Forms.GroupBox grpMappingInfo;
        private System.Windows.Forms.SplitContainer splitMain;
        private System.Windows.Forms.DataGridView dgvComponents;
        private System.Windows.Forms.DataGridViewTextBoxColumn colComponent;
        private System.Windows.Forms.DataGridViewTextBoxColumn colType;
        private System.Windows.Forms.DataGridViewTextBoxColumn colTemplate;
        private System.Windows.Forms.SplitContainer splitDetail;
        private System.Windows.Forms.GroupBox grpInputs;
        private System.Windows.Forms.DataGridView dgvInputs;
        private System.Windows.Forms.DataGridViewTextBoxColumn colInputName;
        private System.Windows.Forms.DataGridViewComboBoxColumn colInputAddress;
        private System.Windows.Forms.GroupBox grpOutputs;
        private System.Windows.Forms.DataGridView dgvOutputs;
        private System.Windows.Forms.DataGridViewTextBoxColumn colOutputName;
        private System.Windows.Forms.DataGridViewComboBoxColumn colOutputAddress;
        private System.Windows.Forms.StatusStrip statusStrip;
        private System.Windows.Forms.ToolStripStatusLabel lblStatus;
    }
}