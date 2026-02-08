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

        private async System.Threading.Tasks.Task LoadAndValidate(string xmlPath)
        {
            dgvComponents.Rows.Clear();
            txtOutput.Clear();

            try
            {
                await System.Threading.Tasks.Task.Run(() =>
                {
                    var systemReader = new SystemXmlReader();
                    _loadedComponents = systemReader.ReadAllComponents(xmlPath);
                });

                if (_loadedComponents.Count == 0)
                {
                    ShowValidation("ERROR: No components found in Control.xml", Color.Red);
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