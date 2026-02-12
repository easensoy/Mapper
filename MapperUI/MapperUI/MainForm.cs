using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
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

            PopulateMappingRules();
            UpdateDetectedInfo();
        }

        private void PopulateMappingRules()
        {
            dgvMappingRules.Rows.Clear();

            foreach (var component in _loadedComponents)
            {
                var templateSelector = new TemplateSelector();
                var template = templateSelector.SelectTemplate(component);

                if (template == null || !TemplateMatchesStateCount(component, template))
                {
                    continue;
                }

                var newGuid = Guid.NewGuid().ToString();
                var componentGuid = Guid.NewGuid().ToString();
                var initialState = component.States.FirstOrDefault(s => s.InitialState);

                // Color-coded rows
                AddMappingRow(
                    $"<Name>{component.Name}</Name>",
                    $"FBType Name=\"{template.ComponentType}_{component.Name}\"",
                    "TRANSLATED",
                    "Component naming convention",
                    validated: true,
                    Color.LightGreen);
                AddMappingRow(
                    $"<Type>{component.Type}</Type>",
                    $"Template: {template.TemplateName}",
                    "TRANSLATED",
                    "Template selection",
                    validated: true,
                    Color.LightGreen);
                AddMappingRow(
                    "<ComponentID>",
                    $"GUID: {componentGuid}",
                    "TRANSLATED",
                    "Component ID mapped to GUID",
                    validated: true,
                    Color.LightGreen);
                AddMappingRow(
                    "Version=\"1.0.0\"",
                    "VueOne metadata only",
                    "DISCARDED",
                    "Versioning not used",
                    validated: false,
                    Color.LightSalmon);

                if (component.Type == "Actuator")
                {
                    AddMappingRow(
                        $"State Count: {component.States.Count}",
                        $"Expected: {template.ExpectedStateCount} states",
                        "TRANSLATED",
                        "State count aligns with template",
                        validated: true,
                        Color.LightGreen);

                    if (initialState != null)
                    {
                        AddMappingRow(
                            "<Initial_State>True</Initial_State>",
                            $"ECTransition Source=\"START\" Dest=\"{initialState.Name}\"",
                            "ENCODED",
                            "Initial state becomes START transition",
                            validated: true,
                            Color.LightCyan);
                    }

                    AddMappingRow(
                        $"<State_Number>0..{component.States.Count - 1}</State_Number>",
                        $"state_val: INT (0..{component.States.Count - 1})",
                        "TRANSLATED",
                        "State index mapping",
                        validated: true,
                        Color.LightGreen);
                    AddMappingRow(
                        "<Name>ReturnedHome,Advancing...</Name>",
                        "ECState Name in Actuator.fbt",
                        "ENCODED",
                        "State names become ECState nodes",
                        validated: true,
                        Color.LightCyan);
                    AddMappingRow(
                        "<Time>1000</Time>",
                        "VueOne simulation timing",
                        "DISCARDED",
                        "Timing not used in IEC 61499",
                        validated: false,
                        Color.LightSalmon);
                    AddMappingRow(
                        "<Position>118</Position>",
                        "PLC setpoint, not FB logic",
                        "DISCARDED",
                        "Position not encoded in FB",
                        validated: false,
                        Color.LightSalmon);
                    AddMappingRow(
                        "<Counter>1</Counter>",
                        "VueOne counting feature",
                        "DISCARDED",
                        "Counter not used",
                        validated: false,
                        Color.LightSalmon);
                    AddMappingRow(
                        "<StaticState>True/False</StaticState>",
                        "Motion state indicator",
                        "ENCODED",
                        "Static state becomes motion flag",
                        validated: true,
                        Color.LightCyan);
                    AddMappingRow(
                        "<Transition>...</Transition>",
                        "Phase 2: Auto-wiring",
                        "DISCARDED",
                        "Not yet implemented",
                        validated: false,
                        Color.LightSalmon);
                    AddMappingRow(
                        "Sensor feedback (athome, atwork)",
                        "Template: '${PATH}athome', '${PATH}atwork'",
                        "HARDCODED",
                        "Feedback naming convention",
                        validated: true,
                        Color.LightGray);
                    AddMappingRow(
                        "InterfaceList (complete)",
                        "pst_event, action_event, tohome...",
                        "HARDCODED",
                        "Template interface definition",
                        validated: true,
                        Color.LightGray);
                    AddMappingRow(
                        "FBNetwork (complete)",
                        "FB1:FiveStateActuator, IThis:HMI, Inputs/Output symlinks",
                        "HARDCODED",
                        "Template FB network (CAT wrapper)",
                        validated: true,
                        Color.LightGray);
                }
                else
                {
                    AddMappingRow(
                        $"State Count: {component.States.Count}",
                        "Two-state sensor (ON/OFF)",
                        "TRANSLATED",
                        "Sensor state count",
                        validated: true,
                        Color.LightGreen);
                    AddMappingRow(
                        "Sensor input",
                        "FB2:SYMLINKMULTIVARDST (Input)",
                        "HARDCODED",
                        "Template input symlink",
                        validated: true,
                        Color.LightGray);
                    AddMappingRow(
                        "Status output",
                        "FB1.Status → Status",
                        "TRANSLATED",
                        "Sensor status output mapping",
                        validated: true,
                        Color.LightGreen);
                    AddMappingRow(
                        "InterfaceList (complete)",
                        "INIT/INITO/pst_out, Status",
                        "HARDCODED",
                        "Template interface definition",
                        validated: true,
                        Color.LightGray);
                    AddMappingRow(
                        "FBNetwork (complete)",
                        "FB1:Sensor_Bool, FB2:Input symlink",
                        "HARDCODED",
                        "Template FB network (CAT wrapper)",
                        validated: true,
                        Color.LightGray);
                }
            }
        }

        private void UpdateDetectedInfo()
        {
            if (_loadedComponents.Count == 0)
            {
                lblDetectedType.Text = "-";
                lblDetectedName.Text = "-";
                lblDetectedStates.Text = "-";
                lblValidationStatus.Text = "-";
                return;
            }

            var firstComponent = _loadedComponents[0];
            lblDetectedType.Text = firstComponent.Type;
            lblDetectedName.Text = firstComponent.Name;
            lblDetectedStates.Text = firstComponent.States.Count.ToString();
        }
        private void AddMappingRow(
            string vueOne,
            string iec61499,
            string type,
            string rule,
            bool validated,
            Color backColor)
        {
            int index = dgvMappingRules.Rows.Add(vueOne, iec61499, type, rule, validated);
            dgvMappingRules.Rows[index].DefaultCellStyle.BackColor = backColor;
        }
        private async System.Threading.Tasks.Task LoadAndValidate(string xmlPath)
        {
            dgvComponents.Rows.Clear();

            try
            {
                var systemReader = new SystemXmlReader();

                await System.Threading.Tasks.Task.Run(() =>
                {
                    _loadedComponents = systemReader.ReadAllComponents(xmlPath);
                });

                if (_loadedComponents.Count == 0)
                {
                    MessageBox.Show(
                        "No components found in Control.xml.\nCheck that the XML file has Type='System' or Type='Component'.",
                        "Validation Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
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
                    bool templateMatches = template != null && TemplateMatchesStateCount(component, template);
                    string templateName = templateMatches ? template!.TemplateName : "No Template Found";

                    dgvComponents.Rows.Add(component.Name, component.Type, templateName);

                    // Only validate if we have a template for it
                    if (templateMatches)
                    {
                        var validationResult = validator.Validate(component);

                        if (!validationResult.IsValid)
                        {
                            allValid = false;
                        }
                    }
                    else
                    {
                        allValid = false;
                    }
                }

                if (allValid)
                {
                    btnGenerate.Enabled = true;
                    lblStatus.Text = "Validation passed";
                    lblValidationStatus.Text = "PASSED";
                    lblValidationStatus.ForeColor = Color.Green;
                }
                else
                {
                    btnGenerate.Enabled = false;
                    lblStatus.Text = "Validation failed";
                    lblValidationStatus.Text = "FAILED";
                    lblValidationStatus.ForeColor = Color.Red;
                }

                UpdateDetectedInfo();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error loading file.\n{ex.Message}",
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                btnGenerate.Enabled = false;
                lblStatus.Text = "Error";
            }
        }

        private bool TemplateMatchesStateCount(VueOneComponent component, FBTemplate template)
        {
            if (component.Type == "Actuator" && template.ExpectedStateCount > 0)
            {
                return component.States.Count == template.ExpectedStateCount;
            }

            if (component.Type == "Sensor")
            {
                return component.States.Count == 2;
            }

            return true;
        }

        private async void btnGenerate_Click(object sender, EventArgs e)
        {
            try
            {
                lblStatus.Text = "Generating...";
                btnGenerate.Enabled = false;

                var component = _loadedComponents.FirstOrDefault();
                if (component == null)
                {
                    MessageBox.Show(
                        "Load a Control.xml file first.",
                        "No Data",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    lblStatus.Text = "No data loaded";
                    return;
                }

                var result = await _mapperService.RunMapping(component);

                if (!result.Success)
                {
                    if (result.ValidationResult != null && !result.ValidationResult.IsValid)
                    {
                        MessageBox.Show(
                            $"Generation failed for {result.ComponentName}.\n{result.ErrorMessage}",
                            "Generation Failed",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                    else
                    {
                        MessageBox.Show(
                            $"Generation failed.\n{result.ErrorMessage}",
                            "Generation Failed",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                    lblStatus.Text = "Generation failed";
                    return;
                }

                if (result.GeneratedFB == null)
                {
                    MessageBox.Show(
                        "Generation failed: No FB generated.",
                        "Generation Failed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    lblStatus.Text = "Generation failed";
                    return;
                }

                MessageBox.Show(
                    $"Generated: {result.GeneratedFB.FBName}\n" +
                    $"GUID: {result.GeneratedFB.GUID}\n" +
                    "Files: .fbt, .composite.offline.xml, .doc.xml\n" +
                    $"Location: {result.OutputPath}\n" +
                    $"Deployed: {result.DeployPath}\n\n" +
                    "",
                    "Generation Complete",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                lblStatus.Text = $"Generated: {result.GeneratedFB.FBName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error generating output.\n{ex.Message}",
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                lblStatus.Text = "Error";
            }
            finally
            {
                btnGenerate.Enabled = true;
            }
        }
    }
}
