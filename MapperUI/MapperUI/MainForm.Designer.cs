using System.Windows.Forms;

namespace MapperUI
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }

            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            this.menuStrip = new System.Windows.Forms.MenuStrip();
            this.fileToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.dataToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.buildToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.debugConsoleToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.pnlLog = new System.Windows.Forms.Panel();
            this.txtLog = new System.Windows.Forms.RichTextBox();
            this.lblVueOneModel = new System.Windows.Forms.Label();
            this.txtModelPath = new System.Windows.Forms.TextBox();
            this.btnMappingRules = new System.Windows.Forms.Button();
            this.btnBrowse = new System.Windows.Forms.Button();
            this.btnGenerate = new System.Windows.Forms.Button();
            this.btnInjectSystem = new System.Windows.Forms.Button();
            this.grpValidation = new System.Windows.Forms.GroupBox();
            this.dgvMappingRules = new System.Windows.Forms.DataGridView();
            this.colVueOneElement = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colIEC61499Element = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colMappingType = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colMappingRule = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colValidated = new System.Windows.Forms.DataGridViewCheckBoxColumn();
            this.pnlDetectedInfo = new System.Windows.Forms.FlowLayoutPanel();
            this.lblDetectedPrefix = new System.Windows.Forms.Label();
            this.lblDetectedType = new System.Windows.Forms.Label();
            this.lblNamePrefix = new System.Windows.Forms.Label();
            this.lblDetectedName = new System.Windows.Forms.Label();
            this.lblStatePrefix = new System.Windows.Forms.Label();
            this.lblDetectedStates = new System.Windows.Forms.Label();
            this.lblValidationPrefix = new System.Windows.Forms.Label();
            this.lblValidationStatus = new System.Windows.Forms.Label();
            this.grpMappingInfo = new System.Windows.Forms.GroupBox();
            this.splitContainer = new System.Windows.Forms.SplitContainer();
            this.dgvComponents = new System.Windows.Forms.DataGridView();
            this.colComponent = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colType = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colTemplate = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.panelDetails = new System.Windows.Forms.Panel();
            this.grpOutputs = new System.Windows.Forms.GroupBox();
            this.dgvOutputs = new System.Windows.Forms.DataGridView();
            this.colOutputName = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colOutputAddress = new System.Windows.Forms.DataGridViewComboBoxColumn();
            this.grpInputs = new System.Windows.Forms.GroupBox();
            this.dgvInputs = new System.Windows.Forms.DataGridView();
            this.colInputName = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colInputAddress = new System.Windows.Forms.DataGridViewComboBoxColumn();
            this.statusStrip = new System.Windows.Forms.StatusStrip();
            this.lblStatus = new System.Windows.Forms.ToolStripStatusLabel();
            this.tabMain = new System.Windows.Forms.TabControl();
            this.tabPageMapper = new System.Windows.Forms.TabPage();
            this.tabPageDebug = new System.Windows.Forms.TabPage();
            this.dgvLog = new System.Windows.Forms.DataGridView();
            this.btnClearLog = new System.Windows.Forms.Button();
            this.colLogTime = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colLogStep = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colLogAction = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.debugConsoleToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.menuStrip.SuspendLayout();
            this.grpValidation.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dgvMappingRules)).BeginInit();
            this.pnlDetectedInfo.SuspendLayout();
            this.grpMappingInfo.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer)).BeginInit();
            this.splitContainer.Panel1.SuspendLayout();
            this.splitContainer.Panel2.SuspendLayout();
            this.splitContainer.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dgvComponents)).BeginInit();
            this.panelDetails.SuspendLayout();
            this.grpOutputs.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dgvOutputs)).BeginInit();
            this.grpInputs.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dgvInputs)).BeginInit();
            this.statusStrip.SuspendLayout();
            this.SuspendLayout();
            // 
            // menuStrip
            // 
            this.menuStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.fileToolStripMenuItem,
            this.dataToolStripMenuItem,
            this.buildToolStripMenuItem});
            this.menuStrip.Location = new System.Drawing.Point(0, 0);
            this.menuStrip.Name = "menuStrip";
            this.menuStrip.Size = new System.Drawing.Size(1400, 24);
            this.menuStrip.TabIndex = 0;
            this.menuStrip.Text = "menuStrip";
            // 
            // fileToolStripMenuItem
            // 
            this.fileToolStripMenuItem.Name = "fileToolStripMenuItem";
            this.fileToolStripMenuItem.Size = new System.Drawing.Size(37, 20);
            this.fileToolStripMenuItem.Text = "File";
            // 
            // dataToolStripMenuItem
            // 
            this.dataToolStripMenuItem.Name = "dataToolStripMenuItem";
            this.dataToolStripMenuItem.Size = new System.Drawing.Size(43, 20);
            this.dataToolStripMenuItem.Text = "Data";
            // 
            // buildToolStripMenuItem
            // 
            this.buildToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.debugConsoleToolStripMenuItem });
            this.buildToolStripMenuItem.Name = "buildToolStripMenuItem";
            this.buildToolStripMenuItem.Size = new System.Drawing.Size(46, 20);
            this.buildToolStripMenuItem.Text = "Build";
            // 
            // debugConsoleToolStripMenuItem
            // 
            this.debugConsoleToolStripMenuItem.Name = "debugConsoleToolStripMenuItem";
            this.debugConsoleToolStripMenuItem.Text = "Debug Console";
            this.debugConsoleToolStripMenuItem.Click += new System.EventHandler(this.debugConsoleToolStripMenuItem_Click);
            // 
            // lblVueOneModel
            // 
            this.lblVueOneModel.AutoSize = true;
            this.lblVueOneModel.Location = new System.Drawing.Point(12, 37);
            this.lblVueOneModel.Name = "lblVueOneModel";
            this.lblVueOneModel.Size = new System.Drawing.Size(100, 15);
            this.lblVueOneModel.TabIndex = 1;
            this.lblVueOneModel.Text = "vueOne Model:";
            // 
            // txtModelPath
            // 
            this.txtModelPath.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.txtModelPath.Location = new System.Drawing.Point(118, 34);
            this.txtModelPath.Name = "txtModelPath";
            this.txtModelPath.ReadOnly = true;
            this.txtModelPath.Size = new System.Drawing.Size(924, 23);
            this.txtModelPath.TabIndex = 2;
            // 
            // btnMappingRules
            // 
            this.btnMappingRules.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnMappingRules.Location = new System.Drawing.Point(1048, 33);
            this.btnMappingRules.Name = "btnMappingRules";
            this.btnMappingRules.Size = new System.Drawing.Size(120, 25);
            this.btnMappingRules.TabIndex = 3;
            this.btnMappingRules.Text = "Mapping Rules";
            this.btnMappingRules.UseVisualStyleBackColor = true;
            this.btnMappingRules.Click += new System.EventHandler(this.btnMappingRules_Click);
            // 
            // btnBrowse
            // 
            this.btnBrowse.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnBrowse.Location = new System.Drawing.Point(1174, 33);
            this.btnBrowse.Name = "btnBrowse";
            this.btnBrowse.Size = new System.Drawing.Size(100, 25);
            this.btnBrowse.TabIndex = 4;
            this.btnBrowse.Text = "Browse";
            this.btnBrowse.UseVisualStyleBackColor = true;
            this.btnBrowse.Click += new System.EventHandler(this.btnBrowse_Click);
            // 
            // btnGenerate
            // 
            this.btnGenerate.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnGenerate.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(0)))), ((int)(((byte)(122)))), ((int)(((byte)(204)))));
            this.btnGenerate.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnGenerate.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.btnGenerate.ForeColor = System.Drawing.Color.White;
            this.btnGenerate.Location = new System.Drawing.Point(1280, 33);
            this.btnGenerate.Name = "btnGenerate";
            this.btnGenerate.Size = new System.Drawing.Size(108, 25);
            this.btnGenerate.TabIndex = 5;
            this.btnGenerate.Text = "Generate FB";
            this.btnGenerate.UseVisualStyleBackColor = false;
            this.btnGenerate.Click += new System.EventHandler(this.btnGenerate_Click);

            // 
            // btnInjectSystem
            // 
            this.btnInjectSystem.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnInjectSystem.BackColor = System.Drawing.Color.FromArgb(0, 153, 76);
            this.btnInjectSystem.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnInjectSystem.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.btnInjectSystem.ForeColor = System.Drawing.Color.White;
            this.btnInjectSystem.Location = new System.Drawing.Point(1048, 63);
            this.btnInjectSystem.Name = "btnInjectSystem";
            this.btnInjectSystem.Size = new System.Drawing.Size(340, 25);
            this.btnInjectSystem.TabIndex = 6;
            this.btnInjectSystem.Text = "Generate Staged Project";
            this.btnInjectSystem.UseVisualStyleBackColor = false;
            this.btnInjectSystem.Click += new System.EventHandler(this.btnInjectSystem_Click);

            // 
            // grpValidation
            // 
            this.grpValidation.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.grpValidation.Controls.Add(this.dgvMappingRules);
            this.grpValidation.Controls.Add(this.pnlDetectedInfo);
            this.grpValidation.Location = new System.Drawing.Point(12, 100);
            this.grpValidation.Name = "grpValidation";
            this.grpValidation.Size = new System.Drawing.Size(1376, 300);
            this.grpValidation.TabIndex = 6;
            this.grpValidation.TabStop = false;
            this.grpValidation.Text = "Validation Output";
            // 
            // dgvMappingRules
            // 
            this.dgvMappingRules.AllowUserToAddRows = false;
            this.dgvMappingRules.AllowUserToDeleteRows = false;
            this.dgvMappingRules.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dgvMappingRules.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
            this.colVueOneElement,
            this.colIEC61499Element,
            this.colMappingType,
            this.colMappingRule,
            this.colValidated});
            this.dgvMappingRules.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dgvMappingRules.Location = new System.Drawing.Point(3, 40);
            this.dgvMappingRules.Name = "dgvMappingRules";
            this.dgvMappingRules.ReadOnly = true;
            this.dgvMappingRules.RowHeadersVisible = false;
            this.dgvMappingRules.Size = new System.Drawing.Size(1370, 257);
            this.dgvMappingRules.TabIndex = 1;
            // 
            // colVueOneElement
            // 
            this.colVueOneElement.HeaderText = "VueOne Element";
            this.colVueOneElement.Name = "colVueOneElement";
            this.colVueOneElement.ReadOnly = true;
            this.colVueOneElement.Width = 260;
            // 
            // colIEC61499Element
            // 
            this.colIEC61499Element.HeaderText = "IEC 61499 Element";
            this.colIEC61499Element.Name = "colIEC61499Element";
            this.colIEC61499Element.ReadOnly = true;
            this.colIEC61499Element.Width = 320;
            // 
            // colMappingType
            // 
            this.colMappingType.HeaderText = "Mapping Type";
            this.colMappingType.Name = "colMappingType";
            this.colMappingType.ReadOnly = true;
            this.colMappingType.Width = 140;
            // 
            // colMappingRule
            // 
            this.colMappingRule.HeaderText = "Mapping Rule";
            this.colMappingRule.Name = "colMappingRule";
            this.colMappingRule.ReadOnly = true;
            this.colMappingRule.Width = 400;
            // 
            // colValidated
            // 
            this.colValidated.HeaderText = "Validated";
            this.colValidated.Name = "colValidated";
            this.colValidated.ReadOnly = true;
            this.colValidated.Width = 80;
            // 
            // pnlDetectedInfo
            // 
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
            this.pnlDetectedInfo.Location = new System.Drawing.Point(3, 19);
            this.pnlDetectedInfo.Name = "pnlDetectedInfo";
            this.pnlDetectedInfo.Padding = new System.Windows.Forms.Padding(0, 0, 0, 6);
            this.pnlDetectedInfo.Size = new System.Drawing.Size(1370, 21);
            this.pnlDetectedInfo.TabIndex = 0;
            this.pnlDetectedInfo.WrapContents = false;
            // 
            // lblDetectedPrefix
            // 
            this.lblDetectedPrefix.AutoSize = true;
            this.lblDetectedPrefix.Location = new System.Drawing.Point(3, 0);
            this.lblDetectedPrefix.Name = "lblDetectedPrefix";
            this.lblDetectedPrefix.Size = new System.Drawing.Size(57, 15);
            this.lblDetectedPrefix.TabIndex = 0;
            this.lblDetectedPrefix.Text = "Detected:";
            // 
            // lblDetectedType
            // 
            this.lblDetectedType.AutoSize = true;
            this.lblDetectedType.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.lblDetectedType.ForeColor = System.Drawing.Color.Green;
            this.lblDetectedType.Location = new System.Drawing.Point(66, 0);
            this.lblDetectedType.Name = "lblDetectedType";
            this.lblDetectedType.Size = new System.Drawing.Size(12, 15);
            this.lblDetectedType.TabIndex = 1;
            this.lblDetectedType.Text = "-";
            // 
            // lblNamePrefix
            // 
            this.lblNamePrefix.AutoSize = true;
            this.lblNamePrefix.Location = new System.Drawing.Point(86, 0);
            this.lblNamePrefix.Margin = new System.Windows.Forms.Padding(8, 0, 0, 0);
            this.lblNamePrefix.Name = "lblNamePrefix";
            this.lblNamePrefix.Size = new System.Drawing.Size(42, 15);
            this.lblNamePrefix.TabIndex = 2;
            this.lblNamePrefix.Text = "Name:";
            // 
            // lblDetectedName
            // 
            this.lblDetectedName.AutoSize = true;
            this.lblDetectedName.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.lblDetectedName.ForeColor = System.Drawing.Color.Green;
            this.lblDetectedName.Location = new System.Drawing.Point(128, 0);
            this.lblDetectedName.Name = "lblDetectedName";
            this.lblDetectedName.Size = new System.Drawing.Size(12, 15);
            this.lblDetectedName.TabIndex = 3;
            this.lblDetectedName.Text = "-";
            // 
            // lblStatePrefix
            // 
            this.lblStatePrefix.AutoSize = true;
            this.lblStatePrefix.Location = new System.Drawing.Point(148, 0);
            this.lblStatePrefix.Margin = new System.Windows.Forms.Padding(8, 0, 0, 0);
            this.lblStatePrefix.Name = "lblStatePrefix";
            this.lblStatePrefix.Size = new System.Drawing.Size(71, 15);
            this.lblStatePrefix.TabIndex = 4;
            //this.lblStatePrefix.Text = "State Count:";
            this.lblStatePrefix.Text = "Components:";
            // 
            // lblDetectedStates
            // 
            this.lblDetectedStates.AutoSize = true;
            this.lblDetectedStates.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.lblDetectedStates.ForeColor = System.Drawing.Color.Green;
            this.lblDetectedStates.Location = new System.Drawing.Point(219, 0);
            this.lblDetectedStates.Name = "lblDetectedStates";
            this.lblDetectedStates.Size = new System.Drawing.Size(12, 15);
            this.lblDetectedStates.TabIndex = 5;
            this.lblDetectedStates.Text = "-";
            // 
            // lblValidationPrefix
            // 
            this.lblValidationPrefix.AutoSize = true;
            this.lblValidationPrefix.Location = new System.Drawing.Point(239, 0);
            this.lblValidationPrefix.Margin = new System.Windows.Forms.Padding(8, 0, 0, 0);
            this.lblValidationPrefix.Name = "lblValidationPrefix";
            this.lblValidationPrefix.Size = new System.Drawing.Size(62, 15);
            this.lblValidationPrefix.TabIndex = 6;
            this.lblValidationPrefix.Text = "Validation:";
            // 
            // lblValidationStatus
            // 
            this.lblValidationStatus.AutoSize = true;
            this.lblValidationStatus.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.lblValidationStatus.ForeColor = System.Drawing.Color.Green;
            this.lblValidationStatus.Location = new System.Drawing.Point(301, 0);
            this.lblValidationStatus.Name = "lblValidationStatus";
            this.lblValidationStatus.Size = new System.Drawing.Size(12, 15);
            this.lblValidationStatus.TabIndex = 7;
            this.lblValidationStatus.Text = "-";
            // 
            // grpMappingInfo
            // 
            this.grpMappingInfo.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
            | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.grpMappingInfo.Controls.Add(this.splitContainer);
            this.grpMappingInfo.Location = new System.Drawing.Point(12, 376);
            this.grpMappingInfo.Name = "grpMappingInfo";
            this.grpMappingInfo.Size = new System.Drawing.Size(1376, 346);
            this.grpMappingInfo.TabIndex = 7;
            this.grpMappingInfo.TabStop = false;
            this.grpMappingInfo.Text = "Mapping Information";
            // 
            // splitContainer
            // 
            this.splitContainer.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitContainer.Location = new System.Drawing.Point(3, 19);
            this.splitContainer.Name = "splitContainer";
            // 
            // splitContainer.Panel1
            // 
            this.splitContainer.Panel1.Controls.Add(this.dgvComponents);
            // 
            // splitContainer.Panel2
            // 
            this.splitContainer.Panel2.Controls.Add(this.panelDetails);
            this.splitContainer.Size = new System.Drawing.Size(1370, 324);
            this.splitContainer.SplitterDistance = 550;
            this.splitContainer.TabIndex = 0;
            // 
            // dgvComponents
            // 
            this.dgvComponents.AllowUserToAddRows = false;
            this.dgvComponents.AllowUserToDeleteRows = false;
            this.dgvComponents.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dgvComponents.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
            this.colComponent,
            this.colType,
            this.colTemplate});
            this.dgvComponents.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dgvComponents.Location = new System.Drawing.Point(0, 0);
            this.dgvComponents.Name = "dgvComponents";
            this.dgvComponents.ReadOnly = true;
            this.dgvComponents.RowHeadersVisible = false;
            this.dgvComponents.SelectionMode = System.Windows.Forms.DataGridViewSelectionMode.FullRowSelect;
            this.dgvComponents.Size = new System.Drawing.Size(550, 324);
            this.dgvComponents.TabIndex = 0;
            // 
            // colComponent
            // 
            this.colComponent.HeaderText = "Component";
            this.colComponent.Name = "colComponent";
            this.colComponent.ReadOnly = true;
            this.colComponent.Width = 200;
            // 
            // colType
            // 
            this.colType.HeaderText = "Type";
            this.colType.Name = "colType";
            this.colType.ReadOnly = true;
            this.colType.Width = 120;
            // 
            // colTemplate
            // 
            this.colTemplate.AutoSizeMode = System.Windows.Forms.DataGridViewAutoSizeColumnMode.Fill;
            this.colTemplate.HeaderText = "Template";
            this.colTemplate.Name = "colTemplate";
            this.colTemplate.ReadOnly = true;
            // 
            // panelDetails
            // 
            this.panelDetails.Controls.Add(this.grpOutputs);
            this.panelDetails.Controls.Add(this.grpInputs);
            this.panelDetails.Dock = System.Windows.Forms.DockStyle.Fill;
            this.panelDetails.Location = new System.Drawing.Point(0, 0);
            this.panelDetails.Name = "panelDetails";
            this.panelDetails.Size = new System.Drawing.Size(816, 324);
            this.panelDetails.TabIndex = 0;
            // 
            // grpOutputs
            // 
            this.grpOutputs.Controls.Add(this.dgvOutputs);
            this.grpOutputs.Dock = System.Windows.Forms.DockStyle.Fill;
            this.grpOutputs.Location = new System.Drawing.Point(385, 0);
            this.grpOutputs.Name = "grpOutputs";
            this.grpOutputs.Size = new System.Drawing.Size(431, 324);
            this.grpOutputs.TabIndex = 1;
            this.grpOutputs.TabStop = false;
            this.grpOutputs.Text = "Outputs";
            // 
            // dgvOutputs
            // 
            this.dgvOutputs.AutoSizeColumnsMode = System.Windows.Forms.DataGridViewAutoSizeColumnsMode.Fill;
            this.dgvOutputs.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dgvOutputs.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
            this.colOutputName,
            this.colOutputAddress});
            this.dgvOutputs.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dgvOutputs.Location = new System.Drawing.Point(3, 19);
            this.dgvOutputs.Name = "dgvOutputs";
            this.dgvOutputs.RowHeadersVisible = false;
            this.dgvOutputs.Size = new System.Drawing.Size(425, 302);
            this.dgvOutputs.TabIndex = 0;
            // 
            // colOutputName
            // 
            this.colOutputName.FillWeight = 55F;
            this.colOutputName.HeaderText = "Name";
            this.colOutputName.Name = "colOutputName";
            this.colOutputName.ReadOnly = true;
            // 
            // colOutputAddress
            // 
            this.colOutputAddress.FillWeight = 45F;
            this.colOutputAddress.HeaderText = "Address";
            this.colOutputAddress.Name = "colOutputAddress";
            // 
            // grpInputs
            // 
            this.grpInputs.Controls.Add(this.dgvInputs);
            this.grpInputs.Dock = System.Windows.Forms.DockStyle.Left;
            this.grpInputs.Location = new System.Drawing.Point(0, 0);
            this.grpInputs.Name = "grpInputs";
            this.grpInputs.Size = new System.Drawing.Size(385, 324);
            this.grpInputs.TabIndex = 0;
            this.grpInputs.TabStop = false;
            this.grpInputs.Text = "Inputs";
            // 
            // dgvInputs
            // 
            this.dgvInputs.AutoSizeColumnsMode = System.Windows.Forms.DataGridViewAutoSizeColumnsMode.Fill;
            this.dgvInputs.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dgvInputs.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
            this.colInputName,
            this.colInputAddress});
            this.dgvInputs.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dgvInputs.Location = new System.Drawing.Point(3, 19);
            this.dgvInputs.Name = "dgvInputs";
            this.dgvInputs.RowHeadersVisible = false;
            this.dgvInputs.Size = new System.Drawing.Size(379, 302);
            this.dgvInputs.TabIndex = 0;
            // 
            // colInputName
            // 
            this.colInputName.FillWeight = 55F;
            this.colInputName.HeaderText = "Name";
            this.colInputName.Name = "colInputName";
            this.colInputName.ReadOnly = true;
            // 
            // colInputAddress
            // 
            this.colInputAddress.FillWeight = 45F;
            this.colInputAddress.HeaderText = "Address";
            this.colInputAddress.Name = "colInputAddress";
            // 
            // statusStrip
            // 
            this.statusStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.lblStatus});
            this.statusStrip.Location = new System.Drawing.Point(0, 728);
            this.statusStrip.Name = "statusStrip";
            this.statusStrip.Size = new System.Drawing.Size(1400, 22);
            this.statusStrip.TabIndex = 8;
            this.statusStrip.Text = "statusStrip";
            // 
            // lblStatus
            // 
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new System.Drawing.Size(39, 17);
            this.lblStatus.Text = "Ready";
            // 
            // MainForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1400, 780);
            this.ClientSize = new System.Drawing.Size(1400, 820);

            // ── tabPageMapper — move all existing controls here ───────────────────────
            this.tabPageMapper.Text = "Mapper";
            this.tabPageMapper.Padding = new System.Windows.Forms.Padding(3);
            this.tabPageMapper.Controls.Add(this.grpMappingInfo);
            this.tabPageMapper.Controls.Add(this.grpValidation);
            this.tabPageMapper.Controls.Add(this.btnGenerate);
            this.tabPageMapper.Controls.Add(this.btnInjectSystem);
            this.tabPageMapper.Controls.Add(this.btnBrowse);
            this.tabPageMapper.Controls.Add(this.btnMappingRules);
            this.tabPageMapper.Controls.Add(this.txtModelPath);
            this.tabPageMapper.Controls.Add(this.lblVueOneModel);

            // Adjust positions: lblVueOneModel and txtModelPath were at y=34/37,
            // grpValidation was at y=63, grpMappingInfo at y=376.
            // Inside a TabPage the top is already offset by the tab header (~25px) — keep as-is.

            // ── tabPageDebug — Debug Console ──────────────────────────────────────────
            this.tabPageDebug.Text = "Debug Console";
            this.tabPageDebug.Padding = new System.Windows.Forms.Padding(3);

            // Clear button
            this.btnClearLog.Text = "Clear";
            this.btnClearLog.Dock = System.Windows.Forms.DockStyle.Top;
            this.btnClearLog.Height = 26;
            this.btnClearLog.BackColor = System.Drawing.Color.FromArgb(60, 60, 60);
            this.btnClearLog.ForeColor = System.Drawing.Color.White;
            this.btnClearLog.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnClearLog.Click += new System.EventHandler(this.btnClearLog_Click);

            // dgvLog
            this.dgvLog.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dgvLog.AllowUserToAddRows = false;
            this.dgvLog.AllowUserToDeleteRows = false;
            this.dgvLog.ReadOnly = true;
            this.dgvLog.RowHeadersVisible = false;
            this.dgvLog.AutoSizeRowsMode = System.Windows.Forms.DataGridViewAutoSizeRowsMode.None;
            this.dgvLog.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            this.dgvLog.SelectionMode = System.Windows.Forms.DataGridViewSelectionMode.FullRowSelect;
            this.dgvLog.BackgroundColor = System.Drawing.Color.FromArgb(30, 30, 30);
            this.dgvLog.GridColor = System.Drawing.Color.FromArgb(60, 60, 60);
            this.dgvLog.DefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(30, 30, 30);
            this.dgvLog.DefaultCellStyle.ForeColor = System.Drawing.Color.LimeGreen;
            this.dgvLog.DefaultCellStyle.Font = new System.Drawing.Font("Consolas", 9F);
            this.dgvLog.ColumnHeadersDefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(45, 45, 45);
            this.dgvLog.ColumnHeadersDefaultCellStyle.ForeColor = System.Drawing.Color.White;
            this.dgvLog.ColumnHeadersDefaultCellStyle.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold);
            this.dgvLog.EnableHeadersVisualStyles = false;
            this.dgvLog.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
            this.colLogTime, this.colLogStep, this.colLogAction });
            this.dgvLog.Name = "dgvLog";

            // colLogTime
            this.colLogTime.HeaderText = "Timestamp";
            this.colLogTime.Name = "colLogTime";
            this.colLogTime.ReadOnly = true;
            this.colLogTime.Width = 110;
            this.colLogTime.DefaultCellStyle.ForeColor = System.Drawing.Color.Cyan;

            // colLogStep
            this.colLogStep.HeaderText = "Step";
            this.colLogStep.Name = "colLogStep";
            this.colLogStep.ReadOnly = true;
            this.colLogStep.Width = 90;
            this.colLogStep.DefaultCellStyle.Font = new System.Drawing.Font("Consolas", 9F, System.Drawing.FontStyle.Bold);

            // colLogAction
            this.colLogAction.HeaderText = "Action";
            this.colLogAction.Name = "colLogAction";
            this.colLogAction.ReadOnly = true;
            this.colLogAction.AutoSizeMode = System.Windows.Forms.DataGridViewAutoSizeColumnMode.Fill;

            this.tabPageDebug.Controls.Add(this.dgvLog);
            this.tabPageDebug.Controls.Add(this.btnClearLog);

            // ── tabMain ───────────────────────────────────────────────────────────────
            this.tabMain.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tabMain.TabPages.Add(this.tabPageMapper);
            this.tabMain.TabPages.Add(this.tabPageDebug);
            this.tabMain.Name = "tabMain";

            // ── Form ─────────────────────────────────────────────────────────────────
            this.Controls.Add(this.tabMain);
            this.Controls.Add(this.statusStrip);
            this.Controls.Add(this.menuStrip);
            this.MainMenuStrip = this.menuStrip;
            this.Name = "MainForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "VueOne Mapper for IEC 61499";

            this.MainMenuStrip = this.menuStrip;
            this.Name = "MainForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "VueOne Mapper for IEC 61499";
            // 
            // pnlLog
            // 
            this.pnlLog.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.pnlLog.Height = 160;
            this.pnlLog.Visible = false;
            this.pnlLog.Controls.Add(this.txtLog);
            // 
            // txtLog
            // 
            this.txtLog.Dock = System.Windows.Forms.DockStyle.Fill;
            this.txtLog.BackColor = System.Drawing.Color.FromArgb(30, 30, 30);
            this.txtLog.ForeColor = System.Drawing.Color.LimeGreen;
            this.txtLog.Font = new System.Drawing.Font("Consolas", 8.5F);
            this.txtLog.ReadOnly = true;
            this.txtLog.ScrollBars = System.Windows.Forms.RichTextBoxScrollBars.Vertical;
            this.txtLog.Name = "txtLog";
            this.Controls.Add(this.pnlLog);


            this.menuStrip.ResumeLayout(false);
            this.menuStrip.PerformLayout();
            this.grpValidation.ResumeLayout(false);
            this.tabMain.ResumeLayout(false);
            this.tabPageMapper.ResumeLayout(false);
            this.tabPageDebug.ResumeLayout(false);

            ((System.ComponentModel.ISupportInitialize)(this.dgvLog)).EndInit();
            this.grpValidation.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dgvMappingRules)).EndInit();
            this.pnlDetectedInfo.ResumeLayout(false);
            this.pnlDetectedInfo.PerformLayout();
            this.grpMappingInfo.ResumeLayout(false);
            this.splitContainer.Panel1.ResumeLayout(false);
            this.splitContainer.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer)).EndInit();
            this.splitContainer.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.dgvComponents)).EndInit();
            this.panelDetails.ResumeLayout(false);
            this.grpOutputs.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.dgvOutputs)).EndInit();
            this.grpInputs.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.dgvInputs)).EndInit();
            this.statusStrip.ResumeLayout(false);
            this.statusStrip.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.MenuStrip menuStrip;
        private System.Windows.Forms.ToolStripMenuItem fileToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem dataToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem buildToolStripMenuItem;
        private System.Windows.Forms.Label lblVueOneModel;
        private System.Windows.Forms.TextBox txtModelPath;
        private System.Windows.Forms.Button btnMappingRules;
        private System.Windows.Forms.Button btnBrowse;
        private System.Windows.Forms.Button btnGenerate;
        private System.Windows.Forms.Button btnInjectSystem;
        private System.Windows.Forms.GroupBox grpValidation;
        private System.Windows.Forms.DataGridView dgvMappingRules;
        private System.Windows.Forms.DataGridViewTextBoxColumn colVueOneElement;
        private System.Windows.Forms.DataGridViewTextBoxColumn colIEC61499Element;
        private System.Windows.Forms.DataGridViewTextBoxColumn colMappingType;
        private System.Windows.Forms.DataGridViewTextBoxColumn colMappingRule;
        private System.Windows.Forms.DataGridViewCheckBoxColumn colValidated;
        private System.Windows.Forms.FlowLayoutPanel pnlDetectedInfo;
        private System.Windows.Forms.Label lblDetectedPrefix;
        private System.Windows.Forms.Label lblDetectedType;
        private System.Windows.Forms.Label lblNamePrefix;
        private System.Windows.Forms.Label lblDetectedName;
        private System.Windows.Forms.Label lblStatePrefix;
        private System.Windows.Forms.Label lblDetectedStates;
        private System.Windows.Forms.Label lblValidationPrefix;
        private System.Windows.Forms.Label lblValidationStatus;
        private System.Windows.Forms.GroupBox grpMappingInfo;
        private System.Windows.Forms.SplitContainer splitContainer;
        private System.Windows.Forms.DataGridView dgvComponents;
        private System.Windows.Forms.DataGridViewTextBoxColumn colComponent;
        private System.Windows.Forms.DataGridViewTextBoxColumn colType;
        private System.Windows.Forms.DataGridViewTextBoxColumn colTemplate;
        private System.Windows.Forms.Panel panelDetails;
        private System.Windows.Forms.GroupBox grpOutputs;
        private System.Windows.Forms.DataGridView dgvOutputs;
        private System.Windows.Forms.DataGridViewTextBoxColumn colOutputName;
        private System.Windows.Forms.DataGridViewComboBoxColumn colOutputAddress;
        private System.Windows.Forms.GroupBox grpInputs;
        private System.Windows.Forms.DataGridView dgvInputs;
        private System.Windows.Forms.DataGridViewTextBoxColumn colInputName;
        private System.Windows.Forms.DataGridViewComboBoxColumn colInputAddress;
        private System.Windows.Forms.StatusStrip statusStrip;
        private System.Windows.Forms.ToolStripStatusLabel lblStatus;
        private System.Windows.Forms.ToolStripMenuItem debugConsoleToolStripMenuItem;
        private System.Windows.Forms.Panel pnlLog;
        private System.Windows.Forms.RichTextBox txtLog;
        private System.Windows.Forms.TabControl tabMain;
        private System.Windows.Forms.TabPage tabPageMapper;
        private System.Windows.Forms.TabPage tabPageDebug;
        private System.Windows.Forms.DataGridView dgvLog;
        private System.Windows.Forms.Button btnClearLog;
        private System.Windows.Forms.DataGridViewTextBoxColumn colLogTime;
        private System.Windows.Forms.DataGridViewTextBoxColumn colLogStep;
        private System.Windows.Forms.DataGridViewTextBoxColumn colLogAction;
    }
}
