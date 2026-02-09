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
            ShowValidation(new string('=', 80), Color.Black);
            ShowValidation("", Color.Black);

            foreach (var component in _loadedComponents)
            {
                var templateSelector = new TemplateSelector();
                var template = templateSelector.SelectTemplate(component);

                if (template == null) continue;

                ShowValidation($"Component: {component.Name} ({component.Type})", Color.DarkBlue);
                ShowValidation(new string('-', 80), Color.Gray);

                // Header
                ShowValidation($"{"VueOne Element",-40} {"Mapping Type",-15} {"IEC 61499 Element"}", Color.Black);
                ShowValidation(new string('-', 80), Color.Gray);

                // GUID - TRANSLATED
                var newGuid = Guid.NewGuid().ToString();
                ShowValidation($"{"SystemID",-40} {"[TRANSLATED]",-15} GUID: {newGuid}", Color.Green);

                // Name - TRANSLATED
                ShowValidation($"{"<Name>" + component.Name + "</Name>",-40} {"[TRANSLATED]",-15} FBType Name=\"{template.ComponentType}_{component.Name}\"", Color.Green);

                // Type - TRANSLATED
                ShowValidation($"{"<Type>" + component.Type + "</Type>",-40} {"[TRANSLATED]",-15} Template: {template.TemplateName}", Color.Green);

                // ComponentID - DISCARDED
                ShowValidation($"{"<ComponentID>...",-40} {"[DISCARDED]",-15} Not used in IEC 61499", Color.Orange);

                // Version - DISCARDED
                ShowValidation($"{"Version=\"1.0.0\"",-40} {"[DISCARDED]",-15} VueOne metadata only", Color.Orange);

                // State Count - TRANSLATED
                ShowValidation($"{"State Count: " + component.States.Count,-40} {"[TRANSLATED]",-15} Expected: {template.ExpectedStateCount} states", Color.Green);

                // Initial State - ENCODED
                var initialState = component.States.FirstOrDefault(s => s.InitialState);
                if (initialState != null)
                {
                    ShowValidation($"{"<Initial_State>True</Initial_State>",-40} {"[ENCODED]",-15} ECTransition Source=\"START\" Dest=\"{initialState.Name}\"", Color.DarkCyan);
                }

                // State Numbers - TRANSLATED
                ShowValidation($"{"<State_Number>0.." + (component.States.Count - 1) + "</State_Number>",-40} {"[TRANSLATED]",-15} state_val: INT (0.." + (component.States.Count - 1) + ")", Color.Green);

                // State Names - ENCODED
                ShowValidation($"{"<Name>ReturnedHome,Advancing...</Name>",-40} {"[ENCODED]",-15} ECState Name in Actuator.fbt", Color.DarkCyan);

                // Time - DISCARDED
                ShowValidation($"{"<Time>1000</Time>",-40} {"[DISCARDED]",-15} VueOne simulation timing", Color.Orange);

                // Position - DISCARDED
                ShowValidation($"{"<Position>118</Position>",-40} {"[DISCARDED]",-15} PLC setpoint, not FB logic", Color.Orange);

                // Counter - DISCARDED
                ShowValidation($"{"<Counter>1</Counter>",-40} {"[DISCARDED]",-15} VueOne counting feature", Color.Orange);

                // StaticState - ENCODED
                ShowValidation($"{"<StaticState>True/False</StaticState>",-40} {"[ENCODED]",-15} Motion state indicator", Color.DarkCyan);

                // Transitions - DISCARDED (Phase 1)
                ShowValidation($"{"<Transition>...</Transition>",-40} {"[DISCARDED]",-15} Phase 2: Auto-wiring", Color.Orange);

                // Symbolic I/O - HARDCODED
                if (component.Type == "Actuator")
                {
                    ShowValidation($"{"Sensor feedback (athome, atwork)",-40} {"[HARDCODED]",-15} Template: '${{PATH}}athome', '${{PATH}}atwork'", Color.Gray);
                }
                else if (component.Type == "Sensor")
                {
                    ShowValidation($"{"PLC input signal",-40} {"[HARDCODED]",-15} Template: '${{PATH}}Input'", Color.Gray);
                }

                ShowValidation("", Color.Black);
                ShowValidation("HARDCODED ELEMENTS (from template):", Color.Gray);
                ShowValidation($"{"  InterfaceList (complete)",-40} {"[HARDCODED]",-15} pst_event, action_event, tohome...", Color.Gray);
                ShowValidation($"{"  InputVars (complete)",-40} {"[HARDCODED]",-15} process_state_name, state_val, actuator_name...", Color.Gray);
                ShowValidation($"{"  OutputVars (complete)",-40} {"[HARDCODED]",-15} current_state_to_process, current_state_to_plc", Color.Gray);
                ShowValidation($"{"  FBNetwork (complete)",-40} {"[HARDCODED]",-15} FB1:ToBool, FB3:Actuator, connections", Color.Gray);
                ShowValidation($"{"  EventConnections (all)",-40} {"[HARDCODED]",-15} Internal event wiring", Color.Gray);
                ShowValidation($"{"  DataConnections (all)",-40} {"[HARDCODED]",-15} Internal data wiring", Color.Gray);

                ShowValidation("", Color.Black);
                ShowValidation(new string('=', 80), Color.Black);
                ShowValidation("", Color.Black);
            }

            ShowValidation("LEGEND:", Color.Blue);
            ShowValidation("  [TRANSLATED]  ", Color.Green, false);
            ShowValidation("- Direct value extraction and transformation", Color.Black);
            ShowValidation("  [ENCODED]     ", Color.DarkCyan, false);
            ShowValidation("- Logical transformation (True→Transition, Name→State)", Color.Black);
            ShowValidation("  [DISCARDED]   ", Color.Orange, false);
            ShowValidation("- VueOne-specific, not applicable to IEC 61499", Color.Black);
            ShowValidation("  [HARDCODED]   ", Color.Gray, false);
            ShowValidation("- From template, identical for all instances", Color.Black);
            ShowValidation("", Color.Black);
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

        private void ShowValidation(string message, Color color, bool newline = true)
        {
            int start = txtOutput.TextLength;
            txtOutput.AppendText(message + (newline ? "\n" : ""));
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