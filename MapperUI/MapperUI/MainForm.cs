using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CodeGen.Configuration;
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
        private MapperConfig? _mapperConfig;
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

                var resolvedTemplateName = ResolveTemplateName(component);
                var templateNameToShow = string.IsNullOrWhiteSpace(resolvedTemplateName)
                    ? template.TemplateName
                    : resolvedTemplateName;
                var templateBaseName = Path.GetFileNameWithoutExtension(templateNameToShow);
                if (string.Equals(component.Type, "Sensor", StringComparison.OrdinalIgnoreCase)
                    && templateBaseName.EndsWith("_CAT", StringComparison.OrdinalIgnoreCase))
                {
                    templateBaseName = templateBaseName[..^4];
                }
                var isCatSensorTemplate = string.Equals(templateNameToShow, "Sensor_Bool_CAT.fbt", StringComparison.OrdinalIgnoreCase);
                var componentGuid = Guid.NewGuid().ToString();
                var initialState = component.States.FirstOrDefault(s => s.InitialState);

                AddMappingRow(
                    $"<Name>{component.Name}</Name>",
                    $"FBType Name=\"{templateBaseName}_{component.Name}\"",
                    "TRANSLATED",
                    "Component naming convention",
                    validated: true,
                    Color.LightGreen);
                AddMappingRow(
                    $"<Type>{component.Type}</Type>",
                    $"Template: {templateNameToShow}",
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

                if (string.Equals(component.Type, "Actuator", StringComparison.OrdinalIgnoreCase))
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
                        "ECState Name in Five_State_Actuator.fbt",
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
                        "INIT, pst_event, action_event, tohome...",
                        "HARDCODED",
                        "Template interface definition",
                        validated: true,
                        Color.LightGray);
                    AddMappingRow(
                        "FBNetwork (complete)",
                        "FB1:FiveStateActuator, IThis:HMI, Inputs/Output symlinks",
                        "HARDCODED",
                        "Template FB network (composite wrapper)",
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
                    if (isCatSensorTemplate)
                    {
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
                    else
                    {
                        AddMappingRow(
                            "Sensor input",
                            "Input: BOOL",
                            "TRANSLATED",
                            "Basic sensor input mapping",
                            validated: true,
                            Color.LightGreen);
                        AddMappingRow(
                            "Sensor output",
                            "value: BOOL / CHANGED",
                            "TRANSLATED",
                            "Basic sensor output mapping",
                            validated: true,
                            Color.LightGreen);
                        AddMappingRow(
                            "InterfaceList (complete)",
                            "INIT, REQ, CHANGED, INITO, CNF",
                            "HARDCODED",
                            "Template interface definition",
                            validated: true,
                            Color.LightGray);
                        AddMappingRow(
                            "FB logic",
                            "BasicFB ECC: START/INIT/REQ",
                            "HARDCODED",
                            "Template FB network (basic sensor)",
                            validated: true,
                            Color.LightGray);
                    }
                }
            }
        }

        private MapperConfig GetMapperConfig()
        {
            _mapperConfig ??= MapperConfig.Load();
            return _mapperConfig;
        }

        private string ResolveTemplatePath(VueOneComponent component)
        {
            var config = GetMapperConfig();

            return string.Equals(component.Type, "Actuator", StringComparison.OrdinalIgnoreCase)
                ? config.ActuatorTemplatePath
                : config.SensorTemplatePath;
        }

        private string ResolveTemplateName(VueOneComponent component)
        {
            var path = ResolveTemplatePath(component);
            return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path);
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
            dgvMappingRules.Rows.Clear();

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
                    string resolvedTemplatePath = ResolveTemplatePath(component);
                    bool templateExists = File.Exists(resolvedTemplatePath);
                    string templateName = templateMatches && templateExists
                        ? Path.GetFileName(resolvedTemplatePath)
                        : "No Template Found";

                    dgvComponents.Rows.Add(component.Name, component.Type, templateName);

                    if (templateMatches && templateExists)
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
            if (string.Equals(component.Type, "Actuator", StringComparison.OrdinalIgnoreCase) && template.ExpectedStateCount > 0)
            {
                return component.States.Count == template.ExpectedStateCount;
            }

            if (string.Equals(component.Type, "Sensor", StringComparison.OrdinalIgnoreCase))
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

                if (_loadedComponents.Count == 0)
                {
                    MessageBox.Show(
                        "Load a Control.xml file first.",
                        "No Data",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    lblStatus.Text = "No data loaded";
                    return;
                }

                var generatedNames = new List<string>();

                foreach (var component in _loadedComponents)
                {
                    var result = await _mapperService.RunMapping(component);

                    if (!result.Success || result.GeneratedFB == null)
                    {
                        MessageBox.Show(
                            $"Generation failed for {component.Name}.\n{result.ErrorMessage}",
                            "Generation Failed",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        lblStatus.Text = "Generation failed";
                        return;
                    }

                    generatedNames.Add(result.GeneratedFB.FBName);
                }

                MessageBox.Show(
                    "Generated FBs:\n" + string.Join("\n", generatedNames) + "\n\n" +
                    "Files: .fbt, .composite.offline.xml, .doc.xml, .meta.xml\n" +
                    "Next: Open EAE → Refresh → Verify build",
                    "Generation Complete",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                lblStatus.Text = $"Generated {generatedNames.Count} component(s)";
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
