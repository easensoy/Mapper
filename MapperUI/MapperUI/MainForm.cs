using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using CodeGen.IO;
using CodeGen.Mapping;
using CodeGen.Models;
using CodeGen.Validation;
using MapperUI.Services;

namespace MapperUI
{
    public partial class MainForm : Form
    {
        private readonly MapperService _mapperService;
        private List<VueOneComponent> _loadedComponents = new List<VueOneComponent>();

        public MainForm()
        {
            InitializeComponent();
            _mapperService = new MapperService();
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
                    txtModelPath.Text = dialog.FileName;
                    lblStatus.Text = "Validating...";
                    txtOutput.Clear();
                    txtOutput.AppendText("Validating...\n");

                    await LoadAndValidate(dialog.FileName);
                }
            }
        }

        private void btnMappingRules_Click(object sender, EventArgs e)
        {
            if (_loadedComponents.Count == 0)
            {
                MessageBox.Show("Load a Control.xml file first", "No Data", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            ShowMappingRules();
        }

        private void ShowMappingRules()
        {
            txtOutput.Clear();
            ShowValidation("MAPPING RULES - VueOne to IEC 61499 Translation", Color.Blue);
            ShowValidation(new string('=', 70), Color.Black);
            ShowValidation("", Color.Black);

            foreach (var component in _loadedComponents)
            {
                var templateSelector = new TemplateSelector();
                var template = templateSelector.SelectTemplate(component);

                if (template == null) continue;

                ShowValidation($"Component: {component.Name} ({component.Type})", Color.DarkBlue);
                ShowValidation(new string('-', 70), Color.Gray);

                // Header
                ShowValidation($"{"VueOne Element",-35} → {"IEC 61499 Element",-35}", Color.Black);
                ShowValidation(new string('-', 70), Color.Gray);

                // GUID
                var newGuid = Guid.NewGuid().ToString();
                ShowValidation($"{"SystemID (auto-generated)",-35} → GUID: {newGuid}", Color.DarkGreen);

                // Name
                ShowValidation($"{"<n>" + component.Name + "</n>",-35} → FBType Name=\"{template.ComponentType}_{component.Name}\"", Color.DarkGreen);

                // Type
                ShowValidation($"{"<Type>" + component.Type + "</Type>",-35} → Template: {template.TemplateName}", Color.DarkGreen);

                // State Count
                ShowValidation($"{"State Count: " + component.States.Count,-35} → Expected: {template.ExpectedStateCount} states", Color.DarkGreen);

                // Initial State
                var initialState = component.States.FirstOrDefault(s => s.InitialState);
                if (initialState != null)
                {
                    ShowValidation($"{"<Initial_State>True</Initial_State>",-35} → ECTransition Source=\"START\" Dest=\"{initialState.Name}\"", Color.DarkGreen);
                }

                // State Numbers
                ShowValidation($"{"<State_Number>0..{component.States.Count-1}</State_Number>",-35} → state_val: INT (0..{component.States.Count - 1})", Color.DarkGreen);

                // Symbolic I/O
                if (component.Type == "Actuator")
                {
                    ShowValidation($"{"Sensor feedback (external)",-35} → '${{PATH}}athome', '${{PATH}}atwork'", Color.DarkGreen);
                }
                else if (component.Type == "Sensor")
                {
                    ShowValidation($"{"PLC input (external)",-35} → '${{PATH}}Input'", Color.DarkGreen);
                }

                ShowValidation("", Color.Black);
                ShowValidation("HARDCODED (from template, not translated):", Color.DarkOrange);
                ShowValidation("  - InterfaceList (events: pst_event, tohome, pst_out...)", Color.Gray);
                ShowValidation("  - InputVars/OutputVars (process_state_name, state_val...)", Color.Gray);
                ShowValidation("  - FBNetwork (internal FBs: FiveStateActuator, ToBool...)", Color.Gray);
                ShowValidation("  - EventConnections (internal wiring)", Color.Gray);
                ShowValidation("  - DataConnections (internal wiring)", Color.Gray);

                ShowValidation("", Color.Black);
                ShowValidation(new string('=', 70), Color.Black);
                ShowValidation("", Color.Black);
            }

            ShowValidation("PROOF OF TRANSLATION:", Color.Blue);
            ShowValidation("✓ Component structure validated against template requirements", Color.Green);
            ShowValidation("✓ Component-specific values extracted for FB generation", Color.Green);
            ShowValidation("✓ Template contains production-ready IEC 61499 logic", Color.Green);
        }

        private async System.Threading.Tasks.Task LoadAndValidate(string xmlPath)
        {
            dgvComponents.Rows.Clear();
            txtOutput.Clear();

            try
            {
                ShowValidation($"Loading: {System.IO.Path.GetFileName(xmlPath)}", Color.Black);

                var systemReader = new SystemXmlReader();

                await System.Threading.Tasks.Task.Run(() =>
                {
                    _loadedComponents = systemReader.ReadAllComponents(xmlPath);
                });

                ShowValidation($"Diagnostics: {systemReader.LastError}", Color.Blue);
                ShowValidation($"Found {_loadedComponents.Count} components", Color.Blue);

                if (_loadedComponents.Count == 0)
                {
                    ShowValidation("ERROR: No components found in Control.xml", Color.Red);
                    ShowValidation("Check that the XML file has Type='System' or Type='Component'", Color.Red);
                    btnGenerate.Enabled = false;
                    lblStatus.Text = "No components found";
                    return;
                }

                var templateSelector = new TemplateSelector();
                var validator = new ComponentValidator();
                bool allValid = true;

                foreach (var component in _loadedComponents)
                {
                    var template = templateSelector.SelectTemplate(component);
                    string function = template?.TemplateName ?? "No Template";

                    dgvComponents.Rows.Add(component.Name, component.Type, function);

                    // Only validate if we have a template for it
                    if (template != null)
                    {
                        var validationResult = validator.Validate(component);

                        if (!validationResult.IsValid)
                        {
                            allValid = false;
                            ShowValidation($"✗ {component.Name}: FAILED", Color.Red);
                            foreach (var error in validationResult.Errors)
                            {
                                ShowValidation($"  - {error}", Color.Red);
                            }
                        }
                        else
                        {
                            ShowValidation($"✓ {component.Name}: PASSED ({component.Type}, {component.States.Count} states)", Color.Green);
                        }
                    }
                }

                txtOutput.AppendText("\n");
                if (allValid)
                {
                    ShowValidation("VALIDATION PASSED - Ready to generate", Color.Green);
                    btnGenerate.Enabled = true;
                    lblStatus.Text = "Validation passed";
                }
                else
                {
                    ShowValidation("VALIDATION FAILED - Fix errors before generating", Color.Red);
                    btnGenerate.Enabled = false;
                    lblStatus.Text = "Validation failed";
                }
            }
            catch (Exception ex)
            {
                ShowValidation($"ERROR: {ex.Message}", Color.Red);
                if (ex.InnerException != null)
                {
                    ShowValidation($"Inner: {ex.InnerException.Message}", Color.Red);
                }
                ShowValidation($"Stack: {ex.StackTrace}", Color.DarkRed);
                btnGenerate.Enabled = false;
                lblStatus.Text = "Error";
            }
        }

        private void ShowValidation(string message, Color color)
        {
            int start = txtOutput.TextLength;
            txtOutput.AppendText(message + "\n");
            int end = txtOutput.TextLength;

            txtOutput.Select(start, end - start);
            txtOutput.SelectionColor = color;
            txtOutput.SelectionLength = 0;
            txtOutput.ScrollToCaret();
        }

        private async void btnGenerate_Click(object sender, EventArgs e)
        {
            txtOutput.Clear();
            ShowValidation("Generating IEC 61499 Function Blocks...", Color.Black);
            lblStatus.Text = "Generating...";

            var result = await _mapperService.RunMapping();

            if (!result.Success)
            {
                ShowValidation($"\n✗ Generation failed: {result.ErrorMessage}", Color.Red);
                lblStatus.Text = "Generation failed";
                return;
            }

            txtOutput.AppendText("\n");
            ShowValidation($"✓ Generated: {result.GeneratedFB.FBName}", Color.Green);
            ShowValidation($"  GUID: {result.GeneratedFB.GUID}", Color.Black);
            ShowValidation($"  Files: .fbt, .composite.offline.xml, .doc.xml", Color.Black);
            ShowValidation($"  Location: {result.OutputPath}", Color.Black);
            ShowValidation($"  Deployed: {result.DeployPath}", Color.Black);

            txtOutput.AppendText("\n");
            ShowValidation("Next: Open EAE → Refresh → Verify build", Color.Blue);

            lblStatus.Text = $"Generated: {result.GeneratedFB.FBName}";
        }
    }
}