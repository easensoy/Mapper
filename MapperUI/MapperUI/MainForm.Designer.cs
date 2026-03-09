// MapperUI/MapperUI/MainForm.Designer.cs
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
            // ── Declare all controls ──────────────────────────────────────────
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
            this.btnGenerateRobotWrapper = new System.Windows.Forms.Button();   // ← NEW

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
            this.splitContainer = new System.Windows.Forms.SplitContainer();

            this.dgvComponents = new System.Windows.Forms.DataGridView();
            this.colComponent = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colType = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colTemplate = new System.Windows.Forms.DataGridViewTextBoxColumn();

            this.panelDetails = new System.Windows.Forms.Panel();
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

            // ── Begin init ────────────────────────────────────────────────────
            this.menuStrip.SuspendLayout();
            this.grpValidation.SuspendLayout();
            this.pnlDetectedInfo.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dgvMappingRules)).BeginInit();
            this.grpMappingInfo.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer)).BeginInit();
            this.splitContainer.Panel1.SuspendLayout();
            this.splitContainer.Panel2.SuspendLayout();
            this.splitContainer.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dgvComponents)).BeginInit();
            this.panelDetails.SuspendLayout();
            this.grpInputs.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dgvInputs)).BeginInit();
            this.grpOutputs.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dgvOutputs)).BeginInit();
            this.statusStrip.SuspendLayout();
            this.SuspendLayout();

            // ── Menu ──────────────────────────────────────────────────────────
            this.menuStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
                this.fileToolStripMenuItem,
                this.dataToolStripMenuItem,
                this.buildToolStripMenuItem });
            this.menuStrip.Location = new System.Drawing.Point(0, 0);
            this.menuStrip.Name = "menuStrip";
            this.menuStrip.Size = new System.Drawing.Size(1400, 24);
            this.menuStrip.TabIndex = 0;

            this.fileToolStripMenuItem.Name = "fileToolStripMenuItem";
            this.fileToolStripMenuItem.Text = "File";

            this.dataToolStripMenuItem.Name = "dataToolStripMenuItem";
            this.dataToolStripMenuItem.Text = "Data";

            this.buildToolStripMenuItem.Name = "buildToolStripMenuItem";
            this.buildToolStripMenuItem.Text = "Build";
            this.buildToolStripMenuItem.DropDownItems.Add(this.menuItemDebugConsole);

            this.menuItemDebugConsole.Name = "menuItemDebugConsole";
            this.menuItemDebugConsole.Text = "Debug Console";
            this.menuItemDebugConsole.Click += new System.EventHandler(this.menuItemDebugConsole_Click);

            // ── Toolbar row (y=28) ────────────────────────────────────────────
            //   lblVueOneModel | txtModelPath | btnMappingRules | btnBrowse
            //   btnGenerateCode | btnGenerateRobotWrapper

            this.lblVueOneModel.AutoSize = true;
            this.lblVueOneModel.Location = new System.Drawing.Point(12, 33);
            this.lblVueOneModel.Name = "lblVueOneModel";
            this.lblVueOneModel.Text = "vueOne Model:";

            this.txtModelPath.Anchor =
                System.Windows.Forms.AnchorStyles.Top |
                System.Windows.Forms.AnchorStyles.Left |
                System.Windows.Forms.AnchorStyles.Right;
            this.txtModelPath.Location = new System.Drawing.Point(102, 30);
            this.txtModelPath.Name = "txtModelPath";
            this.txtModelPath.ReadOnly = true;
            this.txtModelPath.Size = new System.Drawing.Size(940, 23);
            this.txtModelPath.TabIndex = 1;

            this.btnMappingRules.Anchor =
                System.Windows.Forms.AnchorStyles.Top |
                System.Windows.Forms.AnchorStyles.Right;
            this.btnMappingRules.Location = new System.Drawing.Point(1050, 28);
            this.btnMappingRules.Name = "btnMappingRules";
            this.btnMappingRules.Size = new System.Drawing.Size(98, 25);
            this.btnMappingRules.TabIndex = 3;
            this.btnMappingRules.Text = "Mapping Rules";
            this.btnMappingRules.UseVisualStyleBackColor = true;
            this.btnMappingRules.Click += new System.EventHandler(this.btnMappingRules_Click);

            this.btnBrowse.Anchor =
                System.Windows.Forms.AnchorStyles.Top |
                System.Windows.Forms.AnchorStyles.Right;
            this.btnBrowse.Location = new System.Drawing.Point(1156, 28);
            this.btnBrowse.Name = "btnBrowse";
            this.btnBrowse.Size = new System.Drawing.Size(84, 25);
            this.btnBrowse.TabIndex = 4;
            this.btnBrowse.Text = "Browse";
            this.btnBrowse.UseVisualStyleBackColor = true;
            this.btnBrowse.Click += new System.EventHandler(this.btnBrowse_Click);

            // "Generate Code" — blue, top-right
            this.btnGenerateCode.Anchor =
                System.Windows.Forms.AnchorStyles.Top |
                System.Windows.Forms.AnchorStyles.Right;
            this.btnGenerateCode.BackColor = System.Drawing.Color.FromArgb(0, 122, 204);
            this.btnGenerateCode.Enabled = false;
            this.btnGenerateCode.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnGenerateCode.FlatAppearance.BorderSize = 0;
            this.btnGenerateCode.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold);
            this.btnGenerateCode.ForeColor = System.Drawing.Color.White;
            this.btnGenerateCode.Location = new System.Drawing.Point(1248, 28);
            this.btnGenerateCode.Name = "btnGenerateCode";
            this.btnGenerateCode.Size = new System.Drawing.Size(140, 25);
            this.btnGenerateCode.TabIndex = 5;
            this.btnGenerateCode.Text = "Generate Code";
            this.btnGenerateCode.UseVisualStyleBackColor = false;
            this.btnGenerateCode.Click += new System.EventHandler(this.btnGenerateCode_Click);

            // "Generate Robot_Task_CAT" — green, directly below Generate Code
            this.btnGenerateRobotWrapper.Anchor =
                System.Windows.Forms.AnchorStyles.Top |
                System.Windows.Forms.AnchorStyles.Right;
            this.btnGenerateRobotWrapper.BackColor = System.Drawing.Color.FromArgb(0, 150, 80);
            this.btnGenerateRobotWrapper.Enabled = true;
            this.btnGenerateRobotWrapper.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnGenerateRobotWrapper.FlatAppearance.BorderSize = 0;
            this.btnGenerateRobotWrapper.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold);
            this.btnGenerateRobotWrapper.ForeColor = System.Drawing.Color.White;
            this.btnGenerateRobotWrapper.Location = new System.Drawing.Point(1248, 57);   // 29 px below
            this.btnGenerateRobotWrapper.Name = "btnGenerateRobotWrapper";
            this.btnGenerateRobotWrapper.Size = new System.Drawing.Size(140, 25);
            this.btnGenerateRobotWrapper.TabIndex = 6;
            this.btnGenerateRobotWrapper.Text = "CAT Wrapper Generator";
            this.btnGenerateRobotWrapper.UseVisualStyleBackColor = false;
            this.btnGenerateRobotWrapper.Click += new System.EventHandler(this.btnGenerateRobotWrapper_Click);

            // ── Validation Output group (starts at y=90 to clear both buttons) ─
            this.grpValidation.Anchor =
                System.Windows.Forms.AnchorStyles.Top |
                System.Windows.Forms.AnchorStyles.Left |
                System.Windows.Forms.AnchorStyles.Right;
            this.grpValidation.Controls.Add(this.dgvMappingRules);
            this.grpValidation.Controls.Add(this.pnlDetectedInfo);
            this.grpValidation.Location = new System.Drawing.Point(12, 90);   // was 68
            this.grpValidation.Name = "grpValidation";
            this.grpValidation.Size = new System.Drawing.Size(1376, 310);
            this.grpValidation.TabIndex = 7;
            this.grpValidation.TabStop = false;
            this.grpValidation.Text = "Validation Output";

            // ── Detected-info flow panel ──────────────────────────────────────
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
            this.pnlDetectedInfo.Name = "pnlDetectedInfo";
            this.pnlDetectedInfo.Padding = new System.Windows.Forms.Padding(4, 2, 4, 2);
            this.pnlDetectedInfo.Size = new System.Drawing.Size(1370, 26);
            this.pnlDetectedInfo.WrapContents = false;

            SetLabel(this.lblDetectedPrefix, "Type: ", bold: false);
            SetLabel(this.lblDetectedType, "—", bold: true);
            SetLabel(this.lblNamePrefix, "   Name: ", bold: false);
            SetLabel(this.lblDetectedName, "—", bold: true);
            SetLabel(this.lblStatePrefix, "   States: ", bold: false);
            SetLabel(this.lblDetectedStates, "—", bold: true);
            SetLabel(this.lblValidationPrefix, "   Validation: ", bold: false);
            SetLabel(this.lblValidationStatus, "—", bold: true);

            // ── Mapping Rules DataGridView ────────────────────────────────────
            this.dgvMappingRules.AllowUserToAddRows = false;
            this.dgvMappingRules.AllowUserToDeleteRows = false;
            this.dgvMappingRules.AutoSizeColumnsMode =
                System.Windows.Forms.DataGridViewAutoSizeColumnsMode.None;
            this.dgvMappingRules.BackgroundColor = System.Drawing.SystemColors.Window;
            this.dgvMappingRules.ColumnHeadersHeightSizeMode =
                System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dgvMappingRules.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
                this.colVueOneElement,
                this.colIEC61499Element,
                this.colMappingType,
                this.colMappingRule,
                this.colMappingValidated });
            this.dgvMappingRules.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dgvMappingRules.Name = "dgvMappingRules";
            this.dgvMappingRules.ReadOnly = true;
            this.dgvMappingRules.RowHeadersVisible = false;
            this.dgvMappingRules.TabIndex = 1;
            this.dgvMappingRules.DefaultCellStyle.Font = new System.Drawing.Font("Segoe UI", 9F);

            this.colVueOneElement.HeaderText = "VueOne Element";
            this.colVueOneElement.Name = "colVueOneElement";
            this.colVueOneElement.ReadOnly = true;
            this.colVueOneElement.Width = 240;

            this.colIEC61499Element.HeaderText = "IEC 61499 Element";
            this.colIEC61499Element.Name = "colIEC61499Element";
            this.colIEC61499Element.ReadOnly = true;
            this.colIEC61499Element.Width = 220;

            this.colMappingType.HeaderText = "Mapping Type";
            this.colMappingType.Name = "colMappingType";
            this.colMappingType.ReadOnly = true;
            this.colMappingType.Width = 100;

            this.colMappingRule.HeaderText = "Mapping Rule";
            this.colMappingRule.Name = "colMappingRule";
            this.colMappingRule.ReadOnly = true;
            this.colMappingRule.AutoSizeMode =
                System.Windows.Forms.DataGridViewAutoSizeColumnMode.Fill;

            this.colMappingValidated.HeaderText = "Validated";
            this.colMappingValidated.Name = "colMappingValidated";
            this.colMappingValidated.ReadOnly = true;
            this.colMappingValidated.Width = 72;
            this.colMappingValidated.DefaultCellStyle.Alignment =
                System.Windows.Forms.DataGridViewContentAlignment.MiddleCenter;
            this.colMappingValidated.DefaultCellStyle.Font =
                new System.Drawing.Font("Segoe UI", 11F, System.Drawing.FontStyle.Bold);

            // ── Mapping Information group ─────────────────────────────────────
            this.grpMappingInfo.Anchor =
                System.Windows.Forms.AnchorStyles.Top |
                System.Windows.Forms.AnchorStyles.Bottom |
                System.Windows.Forms.AnchorStyles.Left |
                System.Windows.Forms.AnchorStyles.Right;
            this.grpMappingInfo.Controls.Add(this.splitContainer);
            this.grpMappingInfo.Location = new System.Drawing.Point(12, 408);  // 90+310+8
            this.grpMappingInfo.Name = "grpMappingInfo";
            this.grpMappingInfo.Size = new System.Drawing.Size(1376, 320);
            this.grpMappingInfo.TabIndex = 8;
            this.grpMappingInfo.TabStop = false;
            this.grpMappingInfo.Text = "Mapping Information";

            this.splitContainer.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitContainer.Location = new System.Drawing.Point(3, 19);
            this.splitContainer.Name = "splitContainer";
            this.splitContainer.Size = new System.Drawing.Size(1370, 298);
            this.splitContainer.SplitterDistance = 760;
            this.splitContainer.TabIndex = 0;

            // ── Left: Component grid ──────────────────────────────────────────
            this.dgvComponents.AllowUserToAddRows = false;
            this.dgvComponents.AllowUserToDeleteRows = false;
            this.dgvComponents.BackgroundColor = System.Drawing.SystemColors.Window;
            this.dgvComponents.ColumnHeadersHeightSizeMode =
                System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dgvComponents.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
                this.colComponent, this.colType, this.colTemplate });
            this.dgvComponents.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dgvComponents.Name = "dgvComponents";
            this.dgvComponents.ReadOnly = true;
            this.dgvComponents.RowHeadersVisible = true;
            this.dgvComponents.MultiSelect = true;
            this.dgvComponents.SelectionMode =
                System.Windows.Forms.DataGridViewSelectionMode.FullRowSelect;
            this.dgvComponents.DefaultCellStyle.Font = new System.Drawing.Font("Segoe UI", 9F);
            this.dgvComponents.TabIndex = 0;
            this.dgvComponents.SelectionChanged += new System.EventHandler(this.dgvComponents_SelectionChanged);
            this.splitContainer.Panel1.Controls.Add(this.dgvComponents);

            this.colComponent.HeaderText = "Component";
            this.colComponent.Name = "colComponent";
            this.colComponent.ReadOnly = true;
            this.colComponent.Width = 130;

            this.colType.HeaderText = "Type";
            this.colType.Name = "colType";
            this.colType.ReadOnly = true;
            this.colType.Width = 75;

            this.colTemplate.HeaderText = "Template";
            this.colTemplate.Name = "colTemplate";
            this.colTemplate.ReadOnly = true;
            this.colTemplate.AutoSizeMode =
                System.Windows.Forms.DataGridViewAutoSizeColumnMode.Fill;

            // ── Right: I/O detail panels ──────────────────────────────────────
            this.panelDetails.Dock = System.Windows.Forms.DockStyle.Fill;
            this.panelDetails.Controls.Add(this.grpOutputs);
            this.panelDetails.Controls.Add(this.grpInputs);
            this.splitContainer.Panel2.Controls.Add(this.panelDetails);

            this.grpInputs.Controls.Add(this.dgvInputs);
            this.grpInputs.Dock = System.Windows.Forms.DockStyle.Left;
            this.grpInputs.Name = "grpInputs";
            this.grpInputs.Size = new System.Drawing.Size(248, 298);
            this.grpInputs.TabIndex = 0;
            this.grpInputs.TabStop = false;
            this.grpInputs.Text = "Inputs";

            this.dgvInputs.AllowUserToAddRows = false;
            this.dgvInputs.AllowUserToDeleteRows = false;
            this.dgvInputs.AutoSizeColumnsMode =
                System.Windows.Forms.DataGridViewAutoSizeColumnsMode.Fill;
            this.dgvInputs.ColumnHeadersHeightSizeMode =
                System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dgvInputs.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
                this.colInputName, this.colInputAddress });
            this.dgvInputs.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dgvInputs.Name = "dgvInputs";
            this.dgvInputs.RowHeadersVisible = false;
            this.dgvInputs.TabIndex = 0;

            this.colInputName.FillWeight = 60F;
            this.colInputName.HeaderText = "Name";
            this.colInputName.Name = "colInputName";
            this.colInputName.ReadOnly = true;

            this.colInputAddress.FillWeight = 40F;
            this.colInputAddress.HeaderText = "Address";
            this.colInputAddress.Name = "colInputAddress";

            this.grpOutputs.Controls.Add(this.dgvOutputs);
            this.grpOutputs.Dock = System.Windows.Forms.DockStyle.Fill;
            this.grpOutputs.Name = "grpOutputs";
            this.grpOutputs.TabIndex = 1;
            this.grpOutputs.TabStop = false;
            this.grpOutputs.Text = "Outputs";

            this.dgvOutputs.AllowUserToAddRows = false;
            this.dgvOutputs.AllowUserToDeleteRows = false;
            this.dgvOutputs.AutoSizeColumnsMode =
                System.Windows.Forms.DataGridViewAutoSizeColumnsMode.Fill;
            this.dgvOutputs.ColumnHeadersHeightSizeMode =
                System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dgvOutputs.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
                this.colOutputName, this.colOutputAddress });
            this.dgvOutputs.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dgvOutputs.Name = "dgvOutputs";
            this.dgvOutputs.RowHeadersVisible = false;
            this.dgvOutputs.TabIndex = 0;

            this.colOutputName.FillWeight = 60F;
            this.colOutputName.HeaderText = "Name";
            this.colOutputName.Name = "colOutputName";
            this.colOutputName.ReadOnly = true;

            this.colOutputAddress.FillWeight = 40F;
            this.colOutputAddress.HeaderText = "Address";
            this.colOutputAddress.Name = "colOutputAddress";

            // ── Status strip ──────────────────────────────────────────────────
            this.statusStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[] { this.lblStatus });
            this.statusStrip.Name = "statusStrip";
            this.statusStrip.TabIndex = 9;
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Text = "Browse a Control.xml to begin";

            // ── Form ──────────────────────────────────────────────────────────
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1400, 770);

            this.Controls.Add(this.grpMappingInfo);
            this.Controls.Add(this.grpValidation);
            this.Controls.Add(this.btnGenerateRobotWrapper);   // ← NEW (add before GenerateCode so tab order is natural)
            this.Controls.Add(this.btnGenerateCode);
            this.Controls.Add(this.btnBrowse);
            this.Controls.Add(this.btnMappingRules);
            this.Controls.Add(this.txtModelPath);
            this.Controls.Add(this.lblVueOneModel);
            this.Controls.Add(this.statusStrip);
            this.Controls.Add(this.menuStrip);

            this.MainMenuStrip = this.menuStrip;
            this.MinimumSize = new System.Drawing.Size(1100, 700);
            this.Name = "MainForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "VueOne Mapper for IEC 61499";

            // ── Resume ────────────────────────────────────────────────────────
            this.menuStrip.ResumeLayout(false);
            this.menuStrip.PerformLayout();
            this.grpValidation.ResumeLayout(false);
            this.grpValidation.PerformLayout();
            this.pnlDetectedInfo.ResumeLayout(false);
            this.pnlDetectedInfo.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dgvMappingRules)).EndInit();
            this.grpMappingInfo.ResumeLayout(false);
            this.splitContainer.Panel1.ResumeLayout(false);
            this.splitContainer.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer)).EndInit();
            this.splitContainer.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.dgvComponents)).EndInit();
            this.panelDetails.ResumeLayout(false);
            this.grpInputs.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.dgvInputs)).EndInit();
            this.grpOutputs.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.dgvOutputs)).EndInit();
            this.statusStrip.ResumeLayout(false);
            this.statusStrip.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        // ── Label helper ──────────────────────────────────────────────────────
        private static void SetLabel(System.Windows.Forms.Label lbl, string text, bool bold)
        {
            lbl.AutoSize = true;
            lbl.Text = text;
            lbl.Font = new System.Drawing.Font("Segoe UI", 9F,
                bold ? System.Drawing.FontStyle.Bold : System.Drawing.FontStyle.Regular);
            lbl.Margin = new System.Windows.Forms.Padding(0, 4, 0, 0);
        }

        #endregion

        // ── Field declarations ────────────────────────────────────────────────
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
        private System.Windows.Forms.Button btnGenerateRobotWrapper;   // ← NEW
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
        private System.Windows.Forms.SplitContainer splitContainer;
        private System.Windows.Forms.DataGridView dgvComponents;
        private System.Windows.Forms.DataGridViewTextBoxColumn colComponent;
        private System.Windows.Forms.DataGridViewTextBoxColumn colType;
        private System.Windows.Forms.DataGridViewTextBoxColumn colTemplate;
        private System.Windows.Forms.Panel panelDetails;
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