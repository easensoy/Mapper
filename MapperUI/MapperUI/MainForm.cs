using CodeGen.Configuration;
using CodeGen.IO;
using CodeGen.Mapping;
using CodeGen.Models;
using CodeGen.Validation;
using MapperUI.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MapperUI
{
    public partial class MainForm : Form
    {
        private MapperConfig? _mapperConfig;
        private List<VueOneComponent> _loadedComponents = new List<VueOneComponent>();
        private SystemXmlReader? _lastReader;

        public MainForm()
        {
            InitializeComponent();
            MapperLogger.OnEntry += OnLogEntry;
            btnGenerateCode.Enabled = false;
        }

        // ── Browse ────────────────────────────────────────────────────────────

        private async void btnBrowse_Click(object sender, EventArgs e)
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "XML Files (*.xml)|*.xml|All Files (*.*)|*.*",
                Title = "Select VueOne Control.xml"
            };

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                txtModelPath.Text = dialog.FileName;
                lblStatus.Text = "Validating...";
                MapperLogger.Parse($"Loading: {dialog.FileName}");
                await LoadAndValidate(dialog.FileName);
            }
        }

        // ── Mapping Rules ─────────────────────────────────────────────────────

        private void btnMappingRules_Click(object sender, EventArgs e)
        {
            if (_loadedComponents.Count == 0)
            {
                MessageBox.Show("Load a Control.xml file first", "No Data",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                    continue;

                var resolvedTemplateName = ResolveTemplateName(component);
                var templateNameToShow = string.IsNullOrWhiteSpace(resolvedTemplateName)
                    ? "No Template Found"
                    : resolvedTemplateName;

                AddMappingRow(
                    component.Name,
                    templateNameToShow,
                    component.Type,
                    $"State count: {component.States.Count}",
                    true,
                    Color.White);
            }
        }

        // ── Load and Validate ─────────────────────────────────────────────────

        private async Task LoadAndValidate(string path)
        {
            dgvMappingRules.Rows.Clear();
            dgvComponents.Rows.Clear();
            btnGenerateCode.Enabled = false;

            try
            {
                var reader = new SystemXmlReader();
                _loadedComponents = reader.ReadComponents(path);
                _lastReader = reader;

                if (_loadedComponents.Count == 0)
                {
                    MessageBox.Show("No components found in Control.xml",
                        "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    btnGenerateCode.Enabled = false;
                    lblStatus.Text = "No components found";
                    return;
                }

                var templateSelector = new TemplateSelector();
                var validator = new ComponentValidator();
                bool phase1Valid = true;
                bool hasInjectables = false;

                foreach (var component in _loadedComponents)
                {
                    var template = templateSelector.SelectTemplate(component);
                    bool isProcess = string.Equals(component.Type, "Process", StringComparison.OrdinalIgnoreCase);
                    bool templateMatch = template != null && TemplateMatchesStateCount(component, template);
                    string tPath = ResolveTemplatePath(component);
                    bool tExists = !string.IsNullOrEmpty(tPath) && File.Exists(tPath);

                    string templateName = templateMatch
                        ? (tExists ? Path.GetFileName(tPath) : Path.GetFileName(tPath) + " (not found)")
                        : "No Template Found";

                    dgvComponents.Rows.Add(component.Name, component.Type, templateName);
                    MapperLogger.Validate($"{component.Name} ({component.Type}) → {templateName}");

                    if (isProcess)
                    {
                        hasInjectables = true;
                    }
                    else if (templateMatch && tExists)
                    {
                        var vr = validator.Validate(component);
                        if (!vr.IsValid) phase1Valid = false;
                        hasInjectables = true;
                    }
                    else
                    {
                        phase1Valid = false;
                    }
                }

                if (phase1Valid)
                {
                    lblStatus.Text = "Validation passed";
                    lblValidationStatus.Text = "PASSED";
                    lblValidationStatus.ForeColor = Color.Green;
                    MapperLogger.Validate("Phase 1 validation PASSED");
                }
                else
                {
                    lblStatus.Text = "Phase 1 validation FAILED — check templates";
                    lblValidationStatus.Text = "FAILED";
                    lblValidationStatus.ForeColor = Color.Red;
                    MapperLogger.Warn("Phase 1 validation FAILED — check templates");
                }

                // Generate Code is enabled whenever there are any injectable components,
                // even if some are unsupported. Unsupported ones will be skipped and logged.
                btnGenerateCode.Enabled = hasInjectables;
                UpdateDetectedInfo();
            }
            catch (Exception ex)
            {
                MapperLogger.Error($"LoadAndValidate failed: {ex.Message}");
                MessageBox.Show($"Error loading file.\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                btnGenerateCode.Enabled = false;
                lblStatus.Text = "Error";
            }
        }

        // ── Generate Code ─────────────────────────────────────────────────────
        // Single entry point replacing the old "Generate FB" + "Generate Staged Project".
        // Calls SystemInjector directly — no StagingProjectBuilder, no baseline copy.

        private async void btnGenerateCode_Click(object sender, EventArgs e)
        {
            if (_loadedComponents == null || _loadedComponents.Count == 0)
            {
                MapperLogger.Error("No components loaded — browse a Control.xml first.");
                MessageBox.Show("Load a Control.xml first.", "No Data",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            btnGenerateCode.Enabled = false;

            // Switch to Debug Console immediately so the user sees live output
            tabMain.SelectedTab = tabPageDebug;
            MapperLogger.Info("=== Generate Code ===");

            try
            {
                _mapperConfig = MapperConfig.Load();

                // 1. Validate required config paths
                var dfbproj = Directory
                    .GetFiles(_mapperConfig.ProjectFolder ?? "", "*.dfbproj", SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (dfbproj == null)
                {
                    MapperLogger.Error("No .dfbproj found — check mapper_config.json ProjectFolder setting.");
                    MessageBox.Show("No .dfbproj found in the configured project folder.\nCheck mapper_config.json.",
                        "Config Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                MapperLogger.Validate($".dfbproj : {Path.GetFileName(dfbproj)}");

                // 2. Diff
                MapperLogger.Diff("Running diff against live syslay");
                var injector = new SystemInjector();
                var config = new MapperConfig
                {
                    SyslayPath = _mapperConfig.SyslayPath,
                    SysresPath = _mapperConfig.SysresPath
                };
                var diff = injector.PreviewDiff(config, _loadedComponents);

                MapperLogger.Diff($"Already in project : {diff.AlreadyPresent.Count}");
                foreach (var s in diff.AlreadyPresent) MapperLogger.Info($"  = {s}");
                MapperLogger.Diff($"Will be generated  : {diff.ToBeInjected.Count}");
                foreach (var i in diff.ToBeInjected) MapperLogger.Info($"  + {i}");
                MapperLogger.Diff($"Unsupported (skip) : {diff.Unsupported.Count}");
                foreach (var u in diff.Unsupported) MapperLogger.Warn($"  ! {u}");

                if (diff.ToBeInjected.Count == 0)
                {
                    MapperLogger.Info("Nothing to generate — project already up to date.");
                    MessageBox.Show("All components already match the project.\nNothing to generate.",
                        "Up To Date", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // 3. Inject / generate
                MapperLogger.Write("Generating EAE project files");
                var result = await Task.Run(() => injector.Inject(config, _loadedComponents));

                if (!result.Success)
                {
                    MapperLogger.Error($"Generation failed: {result.ErrorMessage}");
                    MessageBox.Show($"Generation failed:\n\n{result.ErrorMessage}",
                        "Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                MapperLogger.Write($"Generated {result.InjectedFBs.Count} FB(s)");
                foreach (var fb in result.InjectedFBs) MapperLogger.Info($"  ✓ {fb}");

                // 4. Touch .dfbproj → triggers EAE Reload Solution
                MapperLogger.Touch($"Touching {Path.GetFileName(dfbproj)}");
                File.SetLastWriteTime(dfbproj, DateTime.Now);
                MapperLogger.Info("EAE will show 'Reload Solution' — click Yes.");

                lblStatus.Text = $"Done — {result.InjectedFBs.Count} component(s) generated";
                MapperLogger.Info("=== Done ===");

                MessageBox.Show(
                    $"Generated {result.InjectedFBs.Count} component(s) successfully.\n\n" +
                    "Switch to EAE — it will show a 'Reload Solution' dialog.\nClick Yes.",
                    "Done — Reload EAE", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MapperLogger.Error($"Exception: {ex.Message}");
                MessageBox.Show($"Unexpected error:\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnGenerateCode.Enabled = true;
            }
        }

        // ── Build menu navigation ─────────────────────────────────────────────

        private void menuItemMapper_Click(object sender, EventArgs e)
        {
            tabMain.SelectedTab = tabPageMapper;
        }

        private void menuItemDebugConsole_Click(object sender, EventArgs e)
        {
            tabMain.SelectedTab = tabPageDebug;
            MapperLogger.Info("=== Debug Console ===");
        }

        // ── Debug Console ─────────────────────────────────────────────────────

        private void OnLogEntry(LogEntry entry)
        {
            if (dgvLog.InvokeRequired) { dgvLog.Invoke(() => OnLogEntry(entry)); return; }

            var color = entry.Step switch
            {
                LogStep.ERROR => Color.OrangeRed,
                LogStep.WARN => Color.Yellow,
                LogStep.REMAP => Color.LimeGreen,
                LogStep.WRITE => Color.DeepSkyBlue,
                LogStep.TOUCH => Color.Plum,
                LogStep.VALIDATE => Color.Aquamarine,
                LogStep.DIFF => Color.LightSkyBlue,
                LogStep.PARSE => Color.LightSalmon,
                _ => Color.LimeGreen
            };

            var idx = dgvLog.Rows.Add(
                entry.Timestamp.ToString("HH:mm:ss.fff"),
                entry.Step.ToString(),
                entry.Action);

            dgvLog.Rows[idx].DefaultCellStyle.ForeColor = color;
            dgvLog.FirstDisplayedScrollingRowIndex = dgvLog.Rows.Count - 1;

            if (dgvLog.Rows.Count > 1000)
                dgvLog.Rows.RemoveAt(0);

            if (entry.Step != LogStep.INFO)
                lblStatus.Text = entry.Action.Length > 100
                    ? entry.Action[..100] + "…"
                    : entry.Action;
        }

        private void btnClearLog_Click(object sender, EventArgs e)
        {
            dgvLog.Rows.Clear();
            MapperLogger.Info("Log cleared.");
        }

        // ── Component selection → I/O details ────────────────────────────────

        private void dgvComponents_SelectionChanged(object sender, EventArgs e)
        {
            dgvInputs.Rows.Clear();
            dgvOutputs.Rows.Clear();

            if (dgvComponents.SelectedRows.Count == 0) return;

            var name = dgvComponents.SelectedRows[0].Cells[0].Value?.ToString();
            var comp = _loadedComponents.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

            if (comp == null) return;

            foreach (var state in comp.States.OrderBy(s => s.StateNumber))
                dgvInputs.Rows.Add($"State {state.StateNumber}: {state.Name}", "");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void UpdateDetectedInfo()
        {
            if (_loadedComponents.Count == 0 || _lastReader == null) return;

            if (_loadedComponents.Count == 1)
            {
                var c = _loadedComponents[0];
                lblDetectedType.Text = c.Type;
                lblDetectedName.Text = c.Name;
                lblDetectedStates.Text = c.States.Count.ToString();
                return;
            }

            int a = _loadedComponents.Count(c => c.Type == "Actuator");
            int s = _loadedComponents.Count(c => c.Type == "Sensor");
            int p = _loadedComponents.Count(c => c.Type == "Process");

            lblDetectedType.Text = "System";
            lblDetectedName.Text = _lastReader.SystemName;
            lblDetectedStates.Text = $"{_loadedComponents.Count} ({a}A / {s}S / {p}P)";
        }

        private void AddMappingRow(string vueOne, string iec61499, string type,
            string rule, bool validated, Color backColor)
        {
            int idx = dgvMappingRules.Rows.Add(vueOne, iec61499, type, rule, validated);
            dgvMappingRules.Rows[idx].DefaultCellStyle.BackColor = backColor;
        }

        private bool TemplateMatchesStateCount(VueOneComponent component, FBTemplate template)
        {
            if (string.Equals(component.Type, "Actuator", StringComparison.OrdinalIgnoreCase))
                return component.States.Count == 5;
            if (string.Equals(component.Type, "Sensor", StringComparison.OrdinalIgnoreCase))
                return component.States.Count == 2;
            if (string.Equals(component.Type, "Process", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        private string ResolveTemplatePath(VueOneComponent component)
        {
            var config = MapperConfig.Load();
            if (string.Equals(component.Type, "Actuator", StringComparison.OrdinalIgnoreCase))
                return config.ActuatorTemplatePath;
            if (string.Equals(component.Type, "Sensor", StringComparison.OrdinalIgnoreCase))
                return config.SensorTemplatePath;
            return string.Empty;
        }

        private string ResolveTemplateName(VueOneComponent component)
        {
            var path = ResolveTemplatePath(component);
            return string.IsNullOrEmpty(path) ? string.Empty : Path.GetFileName(path);
        }

        private MapperConfig GetMapperConfig()
        {
            _mapperConfig ??= MapperConfig.Load();
            return _mapperConfig;
        }
    }
}