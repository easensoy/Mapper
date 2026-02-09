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
            this.lblVueOneModel = new System.Windows.Forms.Label();
            this.txtModelPath = new System.Windows.Forms.TextBox();
            this.btnBrowse = new System.Windows.Forms.Button();
            this.btnGenerate = new System.Windows.Forms.Button();
            this.grpValidation = new System.Windows.Forms.GroupBox();
            this.txtOutput = new System.Windows.Forms.RichTextBox();
            this.grpMappingInfo = new System.Windows.Forms.GroupBox();
            this.splitContainer = new System.Windows.Forms.SplitContainer();
            this.dgvComponents = new System.Windows.Forms.DataGridView();
            this.colComponent = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colType = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colFunction = new System.Windows.Forms.DataGridViewTextBoxColumn();
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

            this.menuStrip.SuspendLayout();
            this.grpValidation.SuspendLayout();
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

            // menuStrip
            this.menuStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.fileToolStripMenuItem,
            this.dataToolStripMenuItem,
            this.buildToolStripMenuItem});
            this.menuStrip.Location = new System.Drawing.Point(0, 0);
            this.menuStrip.Name = "menuStrip";
            this.menuStrip.Size = new System.Drawing.Size(1400, 24);
            this.menuStrip.TabIndex = 0;
            this.menuStrip.Text = "menuStrip1";

            // fileToolStripMenuItem
            this.fileToolStripMenuItem.Name = "fileToolStripMenuItem";
            this.fileToolStripMenuItem.Size = new System.Drawing.Size(37, 20);
            this.fileToolStripMenuItem.Text = "File";

            // dataToolStripMenuItem
            this.dataToolStripMenuItem.Name = "dataToolStripMenuItem";
            this.dataToolStripMenuItem.Size = new System.Drawing.Size(43, 20);
            this.dataToolStripMenuItem.Text = "Data";

            // buildToolStripMenuItem
            this.buildToolStripMenuItem.Name = "buildToolStripMenuItem";
            this.buildToolStripMenuItem.Size = new System.Drawing.Size(46, 20);
            this.buildToolStripMenuItem.Text = "Build";

            // lblVueOneModel
            this.lblVueOneModel.AutoSize = true;
            this.lblVueOneModel.Location = new System.Drawing.Point(12, 37);
            this.lblVueOneModel.Name = "lblVueOneModel";
            this.lblVueOneModel.Size = new System.Drawing.Size(100, 15);
            this.lblVueOneModel.TabIndex = 1;
            this.lblVueOneModel.Text = "vueOne Model:";

            // txtModelPath
            this.txtModelPath.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.txtModelPath.Location = new System.Drawing.Point(118, 34);
            this.txtModelPath.Name = "txtModelPath";
            this.txtModelPath.ReadOnly = true;
            this.txtModelPath.Size = new System.Drawing.Size(1040, 23);
            this.txtModelPath.TabIndex = 2;

            // btnBrowse
            this.btnBrowse.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnBrowse.Location = new System.Drawing.Point(1174, 33);
            this.btnBrowse.Name = "btnBrowse";
            this.btnBrowse.Size = new System.Drawing.Size(100, 25);
            this.btnBrowse.TabIndex = 3;
            this.btnBrowse.Text = "Browse";
            this.btnBrowse.UseVisualStyleBackColor = true;
            this.btnBrowse.Click += new System.EventHandler(this.btnBrowse_Click);

            // btnGenerate
            this.btnGenerate.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnGenerate.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(0)))), ((int)(((byte)(122)))), ((int)(((byte)(204)))));
            this.btnGenerate.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnGenerate.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold);
            this.btnGenerate.ForeColor = System.Drawing.Color.White;
            this.btnGenerate.Location = new System.Drawing.Point(1284, 33);
            this.btnGenerate.Name = "btnGenerate";
            this.btnGenerate.Size = new System.Drawing.Size(100, 25);
            this.btnGenerate.TabIndex = 4;
            this.btnGenerate.Text = "Generate FB";
            this.btnGenerate.UseVisualStyleBackColor = false;
            this.btnGenerate.Click += new System.EventHandler(this.btnGenerate_Click);

            // In InitializeComponent():
            this.btnMappingRules = new System.Windows.Forms.Button();

            this.btnMappingRules.Location = new System.Drawing.Point(1075, 75);
            this.btnMappingRules.Name = "btnMappingRules";
            this.btnMappingRules.Size = new System.Drawing.Size(120, 30);
            this.btnMappingRules.TabIndex = 3;
            this.btnMappingRules.Text = "Mapping Rules";
            this.btnMappingRules.UseVisualStyleBackColor = true;
            this.btnMappingRules.Click += new System.EventHandler(this.btnMappingRules_Click);

            this.grpMappingRules = new System.Windows.Forms.GroupBox();
            this.dgvMappingRules = new System.Windows.Forms.DataGridView();
            this.colVueOneElement = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colMappingType = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colIEC61499Element = new System.Windows.Forms.DataGridViewTextBoxColumn();

            this.grpMappingRules.Controls.Add(this.dgvMappingRules);
            this.grpMappingRules.Location = new System.Drawing.Point(15, 400);
            this.grpMappingRules.Name = "grpMappingRules";
            this.grpMappingRules.Size = new System.Drawing.Size(1370, 300);
            this.grpMappingRules.TabIndex = 10;
            this.grpMappingRules.TabStop = false;
            this.grpMappingRules.Text = "Mapping Rules";
            this.grpMappingRules.Visible = false;

            this.Controls.Add(this.btnMappingRules);
            // grpValidation
            this.grpValidation.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.grpValidation.Controls.Add(this.txtOutput);
            this.grpValidation.Location = new System.Drawing.Point(12, 70);
            this.grpValidation.Name = "grpValidation";
            this.grpValidation.Size = new System.Drawing.Size(1376, 250);
            this.grpValidation.TabIndex = 5;
            this.grpValidation.TabStop = false;
            this.grpValidation.Text = "Validation Output";

            // txtOutput - change background to light gray with dark text
            this.txtOutput.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(240)))), ((int)(((byte)(240)))), ((int)(((byte)(240)))));
            this.txtOutput.Dock = System.Windows.Forms.DockStyle.Fill;
            this.txtOutput.Font = new System.Drawing.Font("Consolas", 9F);
            this.txtOutput.ForeColor = System.Drawing.Color.Black;
            this.txtOutput.Location = new System.Drawing.Point(3, 19);
            this.txtOutput.Name = "txtOutput";
            this.txtOutput.ReadOnly = true;
            this.txtOutput.Size = new System.Drawing.Size(1370, 228);
            this.txtOutput.TabIndex = 0;
            this.txtOutput.Text = "";

            // grpMappingInfo
            this.grpMappingInfo.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
            | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.grpMappingInfo.Controls.Add(this.splitContainer);
            this.grpMappingInfo.Location = new System.Drawing.Point(12, 330);
            this.grpMappingInfo.Name = "grpMappingInfo";
            this.grpMappingInfo.Size = new System.Drawing.Size(1376, 390);
            this.grpMappingInfo.TabIndex = 6;
            this.grpMappingInfo.TabStop = false;
            this.grpMappingInfo.Text = "Mapping Information";

            // splitContainer
            this.splitContainer.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitContainer.Location = new System.Drawing.Point(3, 19);
            this.splitContainer.Name = "splitContainer";
            this.splitContainer.Panel1.Controls.Add(this.dgvComponents);
            this.splitContainer.Panel2.Controls.Add(this.panelDetails);
            this.splitContainer.Size = new System.Drawing.Size(1370, 368);
            this.splitContainer.SplitterDistance = 550;
            this.splitContainer.TabIndex = 0;

            // dgvComponents
            this.dgvComponents.AllowUserToAddRows = false;
            this.dgvComponents.AllowUserToDeleteRows = false;
            this.dgvComponents.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dgvComponents.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
            this.colComponent,
            this.colType,
            this.colFunction});
            this.dgvComponents.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dgvComponents.Location = new System.Drawing.Point(0, 0);
            this.dgvComponents.Name = "dgvComponents";
            this.dgvComponents.ReadOnly = true;
            this.dgvComponents.SelectionMode = System.Windows.Forms.DataGridViewSelectionMode.FullRowSelect;
            this.dgvComponents.Size = new System.Drawing.Size(550, 368);
            this.dgvComponents.TabIndex = 0;

            this.dgvMappingRules.AllowUserToAddRows = false;
            this.dgvMappingRules.AllowUserToDeleteRows = false;
            this.dgvMappingRules.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dgvMappingRules.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
            this.colVueOneElement,
            this.colMappingType,
            this.colIEC61499Element});
            this.dgvMappingRules.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dgvMappingRules.Location = new System.Drawing.Point(3, 19);
            this.dgvMappingRules.Name = "dgvMappingRules";
            this.dgvMappingRules.ReadOnly = true;
            this.dgvMappingRules.RowHeadersVisible = false;
            this.dgvMappingRules.Size = new System.Drawing.Size(1364, 278);
            this.dgvMappingRules.TabIndex = 0;

            this.colVueOneElement.HeaderText = "VueOne Element";
            this.colVueOneElement.Name = "colVueOneElement";
            this.colVueOneElement.ReadOnly = true;
            this.colVueOneElement.Width = 350;

            // colComponent
            this.colComponent.HeaderText = "Component";
            this.colComponent.Name = "colComponent";
            this.colComponent.ReadOnly = true;
            this.colComponent.Width = 200;

            // colType
            this.colType.HeaderText = "Type";
            this.colType.Name = "colType";
            this.colType.ReadOnly = true;
            this.colType.Width = 150;

            // colFunction
            this.colFunction.HeaderText = "Function";
            this.colFunction.Name = "colFunction";
            this.colFunction.ReadOnly = true;
            this.colFunction.Width = 180;

            this.colMappingType.HeaderText = "Mapping Type";
            this.colMappingType.Name = "colMappingType";
            this.colMappingType.ReadOnly = true;
            this.colMappingType.Width = 150;

            // panelDetails
            this.panelDetails.Controls.Add(this.grpOutputs);
            this.panelDetails.Controls.Add(this.grpInputs);
            this.panelDetails.Dock = System.Windows.Forms.DockStyle.Fill;
            this.panelDetails.Location = new System.Drawing.Point(0, 0);
            this.panelDetails.Name = "panelDetails";
            this.panelDetails.Size = new System.Drawing.Size(816, 368);
            this.panelDetails.TabIndex = 0;

            // grpOutputs
            this.grpOutputs.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
            | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.grpOutputs.Controls.Add(this.dgvOutputs);
            this.grpOutputs.Location = new System.Drawing.Point(418, 10);
            this.grpOutputs.Name = "grpOutputs";
            this.grpOutputs.Size = new System.Drawing.Size(385, 345);
            this.grpOutputs.TabIndex = 1;
            this.grpOutputs.TabStop = false;
            this.grpOutputs.Text = "Outputs";

            // dgvOutputs
            this.dgvOutputs.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dgvOutputs.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
            this.colOutputName,
            this.colOutputAddress});
            this.dgvOutputs.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dgvOutputs.Location = new System.Drawing.Point(3, 19);
            this.dgvOutputs.Name = "dgvOutputs";
            this.dgvOutputs.Size = new System.Drawing.Size(379, 323);
            this.dgvOutputs.TabIndex = 0;

            // colOutputName
            this.colOutputName.HeaderText = "Name";
            this.colOutputName.Name = "colOutputName";
            this.colOutputName.ReadOnly = true;
            this.colOutputName.Width = 180;

            // colOutputAddress
            this.colOutputAddress.HeaderText = "Address";
            this.colOutputAddress.Name = "colOutputAddress";
            this.colOutputAddress.Width = 150;

            // colIEC61499Element
            this.colIEC61499Element.HeaderText = "IEC 61499 Element";
            this.colIEC61499Element.Name = "colIEC61499Element";
            this.colIEC61499Element.ReadOnly = true;
            this.colIEC61499Element.Width = 850;

            // grpInputs
            this.grpInputs.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
            | System.Windows.Forms.AnchorStyles.Left)));
            this.grpInputs.Controls.Add(this.dgvInputs);
            this.grpInputs.Location = new System.Drawing.Point(15, 10);
            this.grpInputs.Name = "grpInputs";
            this.grpInputs.Size = new System.Drawing.Size(385, 345);
            this.grpInputs.TabIndex = 0;
            this.grpInputs.TabStop = false;
            this.grpInputs.Text = "Inputs";

            // dgvInputs
            this.dgvInputs.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dgvInputs.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
            this.colInputName,
            this.colInputAddress});
            this.dgvInputs.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dgvInputs.Location = new System.Drawing.Point(3, 19);
            this.dgvInputs.Name = "dgvInputs";
            this.dgvInputs.Size = new System.Drawing.Size(379, 323);
            this.dgvInputs.TabIndex = 0;

            // colInputName
            this.colInputName.HeaderText = "Name";
            this.colInputName.Name = "colInputName";
            this.colInputName.ReadOnly = true;
            this.colInputName.Width = 180;

            // colInputAddress
            this.colInputAddress.HeaderText = "Address";
            this.colInputAddress.Name = "colInputAddress";
            this.colInputAddress.Width = 150;

            // statusStrip
            this.statusStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.lblStatus});
            this.statusStrip.Location = new System.Drawing.Point(0, 728);
            this.statusStrip.Name = "statusStrip";
            this.statusStrip.Size = new System.Drawing.Size(1400, 22);
            this.statusStrip.TabIndex = 7;
            this.statusStrip.Text = "statusStrip1";

            // lblStatus
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new System.Drawing.Size(39, 17);
            this.lblStatus.Text = "Ready";

            // MainForm
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1400, 750);
            this.Controls.Add(this.statusStrip);
            this.Controls.Add(this.grpMappingInfo);
            this.Controls.Add(this.grpValidation);
            this.Controls.Add(this.btnGenerate);
            this.Controls.Add(this.btnBrowse);
            this.Controls.Add(this.txtModelPath);
            this.Controls.Add(this.lblVueOneModel);
            this.Controls.Add(this.menuStrip);
            this.Controls.Add(this.grpMappingRules);
            this.MainMenuStrip = this.menuStrip;
            this.Name = "MainForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "VueOne Mapper for IEC 61499";
            this.menuStrip.ResumeLayout(false);
            this.menuStrip.PerformLayout();
            this.grpValidation.ResumeLayout(false);
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

        private MenuStrip menuStrip;
        private ToolStripMenuItem fileToolStripMenuItem;
        private ToolStripMenuItem dataToolStripMenuItem;
        private ToolStripMenuItem buildToolStripMenuItem;
        private Label lblVueOneModel;
        private TextBox txtModelPath;
        private Button btnBrowse;
        private Button btnGenerate;
        private GroupBox grpValidation;
        private RichTextBox txtOutput;
        private GroupBox grpMappingInfo;
        private SplitContainer splitContainer;
        private DataGridView dgvComponents;
        private DataGridViewTextBoxColumn colComponent;
        private DataGridViewTextBoxColumn colType;
        private DataGridViewTextBoxColumn colFunction;
        private Panel panelDetails;
        private GroupBox grpOutputs;
        private DataGridView dgvOutputs;
        private DataGridViewTextBoxColumn colOutputName;
        private DataGridViewComboBoxColumn colOutputAddress;
        private GroupBox grpInputs;
        private DataGridView dgvInputs;
        private DataGridViewTextBoxColumn colInputName;
        private DataGridViewComboBoxColumn colInputAddress;
        private StatusStrip statusStrip;
        private ToolStripStatusLabel lblStatus;
        private System.Windows.Forms.Button btnMappingRules;
        private System.Windows.Forms.DataGridView dgvMappingRules;
        private System.Windows.Forms.DataGridViewTextBoxColumn colVueOneElement;
        private System.Windows.Forms.DataGridViewTextBoxColumn colMappingType;
        private System.Windows.Forms.DataGridViewTextBoxColumn colIEC61499Element;
        private System.Windows.Forms.GroupBox grpMappingRules;

    }
}