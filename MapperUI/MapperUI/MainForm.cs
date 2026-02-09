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

        //private void btnMappingRules_Click(object sender, EventArgs e)
        //{
        //    if (_loadedComponents.Count == 0)
        //    {
        //        MessageBox.Show("Load a Control.xml file first", "No Data", MessageBoxButtons.OK, MessageBoxIcon.Information);
        //        return;
        //    }

        //    ShowMappingRules();
        //}

        private void btnMappingRules_Click(object sender, EventArgs e)
        {
            if (_loadedComponents.Count == 0)
            {
                MessageBox.Show("Load a Control.xml file first", "No Data", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            grpMappingRules.Visible = !grpMappingRules.Visible;

            if (grpMappingRules.Visible)
            {
                PopulateMappingRules();
                btnMappingRules.Text = "Hide Rules";
            }
            else
            {
                btnMappingRules.Text = "Mapping Rules";
            }
        }

        private void PopulateMappingRules()
        {
            dgvMappingRules.Rows.Clear();

            foreach (var component in _loadedComponents)
            {
                var templateSelector = new TemplateSelector();
                var template = templateSelector.SelectTemplate(component);

                if (template == null) continue;

                var newGuid = Guid.NewGuid().ToString();
                var initialState = component.States.FirstOrDefault(s => s.InitialState);

                // Color-coded rows
                AddMappingRow("SystemID", "TRANSLATED", $"GUID: {newGuid}", Color.LightGreen);
                AddMappingRow($"<Name>{component.Name}</Name>", "TRANSLATED", $"FBType Name=\"{template.ComponentType}_{component.Name}\"", Color.LightGreen);
                AddMappingRow($"<Type>{component.Type}</Type>", "TRANSLATED", $"Template: {template.TemplateName}", Color.LightGreen);
                AddMappingRow("<ComponentID>...", "DISCARDED", "Not used in IEC 61499", Color.LightSalmon);
                AddMappingRow("Version=\"1.0.0\"", "DISCARDED", "VueOne metadata only", Color.LightSalmon);
                AddMappingRow($"State Count: {component.States.Count}", "TRANSLATED", $"Expected: {template.ExpectedStateCount} states", Color.LightGreen);

                if (initialState != null)
                {
                    AddMappingRow("<Initial_State>True</Initial_State>", "ENCODED", $"ECTransition Source=\"START\" Dest=\"{initialState.Name}\"", Color.LightCyan);
                }

                AddMappingRow($"<State_Number>0..{component.States.Count - 1}</State_Number>", "TRANSLATED", $"state_val: INT (0..{component.States.Count - 1})", Color.LightGreen);
                AddMappingRow("<Name>ReturnedHome,Advancing...</Name>", "ENCODED", "ECState Name in Actuator.fbt", Color.LightCyan);
                AddMappingRow("<Time>1000</Time>", "DISCARDED", "VueOne simulation timing", Color.LightSalmon);
                AddMappingRow("<Position>118</Position>", "DISCARDED", "PLC setpoint, not FB logic", Color.LightSalmon);
                AddMappingRow("<Counter>1</Counter>", "DISCARDED", "VueOne counting feature", Color.LightSalmon);
                AddMappingRow("<StaticState>True/False</StaticState>", "ENCODED", "Motion state indicator", Color.LightCyan);
                AddMappingRow("<Transition>...</Transition>", "DISCARDED", "Phase 2: Auto-wiring", Color.LightSalmon);

                if (component.Type == "Actuator")
                {
                    AddMappingRow("Sensor feedback (athome, atwork)", "HARDCODED", "Template: '${PATH}athome', '${PATH}atwork'", Color.LightGray);
                }

                AddMappingRow("InterfaceList (complete)", "HARDCODED", "pst_event, action_event, tohome...", Color.LightGray);
                AddMappingRow("FBNetwork (complete)", "HARDCODED", "FB1:ToBool, FB3:Actuator, connections", Color.LightGray);
            }
        }
        private void AddMappingRow(string vueOne, string type, string iec61499, Color backColor)
        {
            int index = dgvMappingRules.Rows.Add(vueOne, type, iec61499);
            dgvMappingRules.Rows[index].DefaultCellStyle.BackColor = backColor;
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
            try
            {
                txtOutput.Clear();
                ShowValidation("Generating IEC 61499 Function Blocks...", Color.Black);
                lblStatus.Text = "Generating...";
                btnGenerate.Enabled = false;

                var result = await _mapperService.RunMapping();

                if (!result.Success)
                {
                    ShowValidation($"\n✗ Generation failed: {result.ErrorMessage}", Color.Red);
                    lblStatus.Text = "Generation failed";
                    return;
                }

                // NULL CHECK
                if (result.GeneratedFB == null)
                {
                    ShowValidation("\n✗ Generation failed: No FB generated", Color.Red);
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
            catch (Exception ex)
            {
                ShowValidation($"\n✗ ERROR: {ex.Message}", Color.Red);
                ShowValidation($"Stack: {ex.StackTrace}", Color.DarkRed);
                lblStatus.Text = "Error";
            }
            finally
            {
                btnGenerate.Enabled = true;
            }
        }
    }
}