using System;
using System.Collections.Generic;
using System.Windows.Forms;
using CodeGen.IO;
using CodeGen.Mapping;
using CodeGen.Models;
using MapperUI.Services;

namespace MapperUI
{
    public partial class MainForm : Form
    {
        private readonly MapperService _mapperService;
        private string _currentControlXmlPath = string.Empty;
        private List<VueOneComponent> _loadedComponents = new List<VueOneComponent>();

        public MainForm()
        {
            InitializeComponent();
            _mapperService = new MapperService();
            InitializeUI();
        }

        private void InitializeUI()
        {
            txtOutput.Text = "Ready to validate VueOne Control.xml...\n\n";
            txtOutput.Text += "Instructions:\n";
            txtOutput.Text += "1. Click 'Browse' to select Control.xml file\n";
            txtOutput.Text += "2. System will parse and display components\n";
            txtOutput.Text += "3. Validation will run automatically\n";
            txtOutput.Text += "4. Click 'Generate FB' to create IEC 61499 files\n";

            btnGenerate.Enabled = false;
        }

        private async void btnBrowse_Click(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "XML Files (*.xml)|*.xml|All Files (*.*)|*.*";
                dialog.Title = "Select VueOne Control.xml";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    _currentControlXmlPath = dialog.FileName;
                    txtModelPath.Text = dialog.FileName;
                    lblStatus.Text = "Parsing Control.xml...";

                    await ParseAndDisplayComponents();
                }
            }
        }

        private async System.Threading.Tasks.Task ParseAndDisplayComponents()
        {
            txtOutput.Clear();
            dgvComponents.Rows.Clear();

            AppendOutput("╔══════════════════════════════════════════════════════════╗\n", false, false, true);
            AppendOutput("║           VueOne Control.xml Parser                      ║\n", false, false, true);
            AppendOutput("╚══════════════════════════════════════════════════════════╝\n\n", false, false, true);

            AppendOutput($"[INFO] Loading: {System.IO.Path.GetFileName(_currentControlXmlPath)}\n\n");

            try
            {
                await System.Threading.Tasks.Task.Run(() =>
                {
                    var systemReader = new SystemXmlReader();
                    _loadedComponents = systemReader.ReadAllComponents(_currentControlXmlPath);
                });

                AppendOutput("─────────────────────────────────────────────────────────\n", false, false, true);
                AppendOutput("COMPONENT DISCOVERY\n", false, false, true);
                AppendOutput("─────────────────────────────────────────────────────────\n\n", false, false, true);

                AppendOutput($"Total Components Found: {_loadedComponents.Count}\n\n");

                var templateSelector = new TemplateSelector();
                int actuatorCount = 0;
                int sensorCount = 0;
                int processCount = 0;
                int unknownCount = 0;

                foreach (var component in _loadedComponents)
                {
                    var template = templateSelector.SelectTemplate(component);
                    string function = template?.TemplateName ?? "No Template";

                    dgvComponents.Rows.Add(component.Name, component.Type, function);

                    if (component.Type == "Actuator") actuatorCount++;
                    else if (component.Type == "Sensor") sensorCount++;
                    else if (component.Type == "Process") processCount++;
                    else unknownCount++;

                    string icon = component.Type switch
                    {
                        "Actuator" => "🔧",
                        "Sensor" => "📡",
                        "Process" => "⚙️",
                        _ => "❓"
                    };

                    AppendOutput($"{icon} {component.Name,-20} Type: {component.Type,-10} States: {component.States.Count}\n");
                }

                AppendOutput("\n─────────────────────────────────────────────────────────\n", false, false, true);
                AppendOutput("COMPONENT STATISTICS\n", false, false, true);
                AppendOutput("─────────────────────────────────────────────────────────\n\n", false, false, true);

                AppendOutput($"Actuators : {actuatorCount}\n", false, true);
                AppendOutput($"Sensors   : {sensorCount}\n", false, true);
                AppendOutput($"Processes : {processCount}\n");
                AppendOutput($"Unknown   : {unknownCount}\n");

                AppendOutput("\n[INFO] Components loaded into grid\n");
                AppendOutput("[INFO] Starting validation...\n\n");

                lblStatus.Text = $"Loaded {_loadedComponents.Count} components";

                await ValidateComponents();
            }
            catch (Exception ex)
            {
                AppendOutput($"\n[ERROR] Failed to parse Control.xml: {ex.Message}\n", true);
                lblStatus.Text = "Parse Error";
            }
        }

        private async System.Threading.Tasks.Task ValidateComponents()
        {
            AppendOutput("─────────────────────────────────────────────────────────\n", false, false, true);
            AppendOutput("VALIDATION ANALYSIS\n", false, false, true);
            AppendOutput("─────────────────────────────────────────────────────────\n\n", false, false, true);

            var validationService = new ValidationService();
            var templateSelector = new TemplateSelector();

            int validCount = 0;
            int invalidCount = 0;

            foreach (var component in _loadedComponents)
            {
                var template = templateSelector.SelectTemplate(component);

                if (template != null)
                {
                    AppendOutput($"[PASS] {component.Name}: {component.Type} with {component.States.Count} states → {template.TemplateName}\n", false, true);
                    validCount++;
                }
                else
                {
                    AppendOutput($"[FAIL] {component.Name}: No template for {component.Type} with {component.States.Count} states\n", true);
                    invalidCount++;
                }
            }

            AppendOutput("\n─────────────────────────────────────────────────────────\n", false, false, true);
            AppendOutput("VALIDATION SUMMARY\n", false, false, true);
            AppendOutput("─────────────────────────────────────────────────────────\n\n", false, false, true);

            AppendOutput($"Valid Components   : {validCount}\n", false, true);
            AppendOutput($"Invalid Components : {invalidCount}\n", invalidCount > 0);

            if (validCount > 0 && invalidCount == 0)
            {
                AppendOutput("\n[✓] ALL COMPONENTS VALID - Ready for generation\n\n", false, true);
                btnGenerate.Enabled = true;
                lblStatus.Text = "Validation Passed";
            }
            else if (validCount > 0)
            {
                AppendOutput("\n[!] PARTIAL VALIDATION - Some components can be generated\n\n", true);
                btnGenerate.Enabled = true;
                lblStatus.Text = $"Partial: {validCount} valid, {invalidCount} invalid";
            }
            else
            {
                AppendOutput("\n[✗] NO VALID COMPONENTS - Cannot generate\n\n", true);
                btnGenerate.Enabled = false;
                lblStatus.Text = "Validation Failed";
            }

            AppendOutput("╔══════════════════════════════════════════════════════════╗\n", false, false, true);
            AppendOutput($"║  Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}                          ║\n", false, false, true);
            AppendOutput("╚══════════════════════════════════════════════════════════╝\n", false, false, true);
        }

        private async void btnGenerate_Click(object sender, EventArgs e)
        {
            txtOutput.Clear();

            AppendOutput("╔══════════════════════════════════════════════════════════╗\n", false, false, true);
            AppendOutput("║         IEC 61499 Function Block Generation             ║\n", false, false, true);
            AppendOutput("╚══════════════════════════════════════════════════════════╝\n\n", false, false, true);

            AppendOutput("[PROCESS] Starting code generation...\n\n");
            lblStatus.Text = "Generating...";

            var result = await _mapperService.RunMapping();

            if (!result.Success)
            {
                AppendOutput($"[FAIL] Generation failed: {result.ErrorMessage}\n", true);
                lblStatus.Text = "Generation Failed";
                return;
            }

            AppendOutput("─────────────────────────────────────────────────────────\n", false, false, true);
            AppendOutput("GENERATED ARTIFACTS\n", false, false, true);
            AppendOutput("─────────────────────────────────────────────────────────\n\n", false, false, true);

            AppendOutput($"Function Block Name : {result.GeneratedFB.FBName}\n", false, true);
            AppendOutput($"GUID                : {result.GeneratedFB.GUID}\n");
            AppendOutput($"Component Source    : {result.ComponentName}\n\n");

            AppendOutput("Generated Files:\n");
            AppendOutput($"  • {result.GeneratedFB.FbtFile}\n");
            AppendOutput($"  • {result.GeneratedFB.CompositeFile}\n");
            AppendOutput($"  • {result.GeneratedFB.DocFile}\n\n");

            AppendOutput($"Output Location     : {result.OutputPath}\n");
            AppendOutput($"Deployed to EAE     : {result.DeployPath}\n\n");

            AppendOutput("─────────────────────────────────────────────────────────\n", false, false, true);
            AppendOutput("NEXT STEPS\n", false, false, true);
            AppendOutput("─────────────────────────────────────────────────────────\n\n", false, false, true);

            AppendOutput("1. Open EAE (Engineering Automation Environment)\n");
            AppendOutput("2. Right-click Solution → Refresh\n");
            AppendOutput("3. Verify build in console (should show 'Build successful')\n");
            AppendOutput("4. Check Composite folder for new Function Block\n\n");

            AppendOutput("[✓] GENERATION COMPLETE\n\n", false, true);

            AppendOutput("╔══════════════════════════════════════════════════════════╗\n", false, false, true);
            AppendOutput($"║  Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}                          ║\n", false, false, true);
            AppendOutput("╚══════════════════════════════════════════════════════════╝\n", false, false, true);

            lblStatus.Text = $"Generated: {result.GeneratedFB.FBName}";
        }

        private void AppendOutput(string text, bool isError = false, bool isSuccess = false, bool isBorder = false)
        {
            if (txtOutput.InvokeRequired)
            {
                txtOutput.Invoke(new Action(() => AppendOutput(text, isError, isSuccess, isBorder)));
                return;
            }

            int start = txtOutput.TextLength;
            txtOutput.AppendText(text);
            int end = txtOutput.TextLength;

            txtOutput.Select(start, end - start);

            if (isBorder)
                txtOutput.SelectionColor = System.Drawing.Color.FromArgb(70, 130, 180);
            else if (isError)
                txtOutput.SelectionColor = System.Drawing.Color.FromArgb(220, 20, 60);
            else if (isSuccess)
                txtOutput.SelectionColor = System.Drawing.Color.FromArgb(34, 139, 34);
            else
                txtOutput.SelectionColor = System.Drawing.Color.Black;

            txtOutput.SelectionLength = 0;
            txtOutput.ScrollToCaret();
        }
    }
}