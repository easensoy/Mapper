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

        // Single Debug Console instance — shown/hidden, never re-created
        private DebugConsoleForm? _debugConsole;

        // ── Mapping type font colors (from VueOne_IEC61499_Mapping.xlsx) ────
        // Background of xlsx cells converted to equivalent readable text colors
        private static readonly Color ColorTranslated = Color.FromArgb(56, 142, 60);   // bold green
        private static readonly Color ColorDiscarded = Color.FromArgb(204, 72, 0);   // orange-red
        private static readonly Color ColorAssumed = Color.FromArgb(180, 130, 0);   // dark amber
        private static readonly Color ColorEncoded = Color.FromArgb(31, 97, 180);  // royal blue
        private static readonly Color ColorHardcoded = Color.FromArgb(100, 100, 100);  // gray

        // Alternating row backgrounds — plain white and very light gray
        private static readonly Color RowEven = Color.White;
        private static readonly Color RowOdd = Color.FromArgb(245, 245, 245);

        public MainForm()
        {
            InitializeComponent();
            btnGenerateCode.Enabled = false;
        }

        // ── Build menu ────────────────────────────────────────────────────────

        private void menuItemDebugConsole_Click(object sender, EventArgs e)
        {
            // Create once; re-use if already open
            if (_debugConsole == null || _debugConsole.IsDisposed)
            {
                _debugConsole = new DebugConsoleForm();
                _debugConsole.PositionBelow(this);
            }

            _debugConsole.Show();
            _debugConsole.BringToFront();
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
                MessageBox.Show("Load a Control.xml file first.", "No Data",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            PopulateMappingRules();
        }

        /// <summary>
        /// Fills the Mapping Rules grid with element-level rules from MappingRuleEngine.
        /// Row backgrounds alternate white / light gray.
        /// Only the Mapping Type cell carries a colored font — no colored backgrounds.
        /// </summary>
        private void PopulateMappingRules()
        {
            dgvMappingRules.Rows.Clear();

            bool hasActuators = _loadedComponents.Any(c =>
                string.Equals(c.Type, "Actuator", StringComparison.OrdinalIgnoreCase));
            bool hasSensors = _loadedComponents.Any(c =>
                string.Equals(c.Type, "Sensor", StringComparison.OrdinalIgnoreCase));
            bool hasProcesses = _loadedComponents.Any(c =>
                string.Equals(c.Type, "Process", StringComparison.OrdinalIgnoreCase));

            var rules = MappingRuleEngine.GetRelevantRules(hasActuators, hasSensors, hasProcesses).ToList();

            for (int i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                int idx = dgvMappingRules.Rows.Add(
                    rule.VueOneElement,
                    rule.IEC61499Element,
                    rule.Type.ToString(),
                    rule.TransformationRule);

                var row = dgvMappingRules.Rows[idx];

                // Alternating white / light-gray row background — no color-coded backgrounds
                Color rowBg = (i % 2 == 0) ? RowEven : RowOdd;
                row.DefaultCellStyle.BackColor = rowBg;
                row.DefaultCellStyle.ForeColor = Color.Black;

                // Color and bold only the Mapping Type cell
                var typeCell = row.Cells[colMappingType.Index];
                (typeCell.Style.ForeColor, typeCell.Style.Font) = rule.Type switch
                {
                    MappingType.TRANSLATED => (ColorTranslated,
                        new Font(dgvMappingRules.Font, FontStyle.Bold)),
                    MappingType.DISCARDED => (ColorDiscarded,
                        new Font(dgvMappingRules.Font, FontStyle.Bold)),
                    MappingType.ASSUMED => (ColorAssumed,
                        new Font(dgvMappingRules.Font, FontStyle.Bold)),
                    MappingType.ENCODED => (ColorEncoded,
                        new Font(dgvMappingRules.Font, FontStyle.Bold)),
                    MappingType.HARDCODED => (ColorHardcoded,
                        new Font(dgvMappingRules.Font, FontStyle.Regular)),
                    _ => (Color.Black,
                        dgvMappingRules.Font)
                };
                typeCell.Style.BackColor = rowBg; // keep same background, no highlight
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
                _loadedComponents = reader.ReadAllComponents(path);
                _lastReader = reader;

                if (_loadedComponents.Count == 0)
                {
                    MessageBox.Show("No components found in Control.xml.",
                        "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    lblStatus.Text = "No components found";
                    return;
                }

                var templateSelector = new TemplateSelector();
                var validator = new ComponentValidator();
                bool phase1Valid = true;
                bool hasInjectables = false;

                int rowIdx = 0;
                foreach (var component in _loadedComponents)
                {
                    var template = templateSelector.SelectTemplate(component);
                    bool isProcess = string.Equals(component.Type, "Process", StringComparison.OrdinalIgnoreCase);
                    bool matchesType = template != null && TemplateMatchesStateCount(component, template);
                    string tPath = ResolveTemplatePath(component);
                    bool tExists = !string.IsNullOrEmpty(tPath) && File.Exists(tPath);

                    string templateName;
                    if (isProcess)
                        templateName = Path.GetFileName(tPath);
                    else if (matchesType && tExists)
                        templateName = Path.GetFileName(tPath);
                    else
                        templateName = "No Template Found";

                    int idx = dgvComponents.Rows.Add(component.Name, component.Type, templateName);
                    var row = dgvComponents.Rows[idx];

                    // Alternating background — no validation color-coding
                    row.DefaultCellStyle.BackColor = (rowIdx % 2 == 0) ? RowEven : RowOdd;
                    rowIdx++;

                    MapperLogger.Validate($"{component.Name} ({component.Type}) → {templateName}");

                    if (isProcess)
                    {
                        hasInjectables = true;
                    }
                    else if (matchesType && tExists)
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

                // Update the detected info bar
                UpdateDetectedInfo();

                if (phase1Valid)
                {
                    lblValidationStatus.Text = "PASSED";
                    lblValidationStatus.ForeColor = Color.Green;
                    lblStatus.Text = "Validation passed";
                    MapperLogger.Validate("Phase 1 validation PASSED");
                }
                else
                {
                    lblValidationStatus.Text = "FAILED";
                    lblValidationStatus.ForeColor = Color.Red;
                    lblStatus.Text = "Validation FAILED — unsupported components present";
                    MapperLogger.Warn("Phase 1 validation FAILED");
                }

                btnGenerateCode.Enabled = hasInjectables;
            }
            catch (Exception ex)
            {
                MapperLogger.Error($"LoadAndValidate failed: {ex.Message}");
                MessageBox.Show($"Error loading file.\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                lblStatus.Text = "Error";
            }
        }

        // ── Generate Code ─────────────────────────────────────────────────────

        private async void btnGenerateCode_Click(object sender, EventArgs e)
        {
            if (_loadedComponents == null || _loadedComponents.Count == 0)
            {
                MessageBox.Show("Load a Control.xml first.", "No Data",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            btnGenerateCode.Enabled = false;
            MapperLogger.Info("=== Generate Code ===");

            try
            {
                _mapperConfig = MapperConfig.Load();
                MapperLogger.Info($"syslay → {_mapperConfig.SyslayPath}");
                MapperLogger.Info($"sysres → {_mapperConfig.SysresPath}");

                if (!File.Exists(_mapperConfig.SyslayPath))
                {
                    MapperLogger.Error($"syslay not found: {_mapperConfig.SyslayPath}");
                    MessageBox.Show($"syslay not found:\n{_mapperConfig.SyslayPath}\n\nCheck mapper_config.json.",
                        "Config Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var dfbproj = DeriveBaselineFolder(_mapperConfig.SyslayPath);
                if (dfbproj == null)
                {
                    MapperLogger.Error("Cannot find .dfbproj above syslay path.");
                    MessageBox.Show("Cannot find .dfbproj above the syslay path.\nCheck mapper_config.json.",
                        "Config Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                MapperLogger.Info($"dfbproj → {dfbproj}");

                // Verify CAT type registrations
                MapperLogger.Validate("Checking CAT type registrations");
                var dfbContent = await File.ReadAllTextAsync(dfbproj);
                bool actuatorReg = dfbContent.Contains("Five_State_Actuator_CAT");
                bool sensorReg = dfbContent.Contains("Sensor_Bool_CAT");
                bool processReg = dfbContent.Contains("Process1_CAT");

                MapperLogger.Validate($"Five_State_Actuator_CAT : {(actuatorReg ? "FOUND ✓" : "MISSING ✗")}");
                MapperLogger.Validate($"Sensor_Bool_CAT         : {(sensorReg ? "FOUND ✓" : "MISSING ✗")}");
                MapperLogger.Validate($"Process1_CAT            : {(processReg ? "FOUND ✓" : "MISSING ✗")}");

                if (!actuatorReg || !sensorReg)
                {
                    MapperLogger.Error("Required CAT types not registered — wrong project?");
                    MessageBox.Show("Required CAT types not found in .dfbproj.\nCheck mapper_config.json.",
                        "Wrong Project", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // Diff
                MapperLogger.Diff("Running diff against live syslay");
                var injector = new SystemInjector();
                var config = new MapperConfig
                {
                    SyslayPath = _mapperConfig.SyslayPath,
                    SysresPath = _mapperConfig.SysresPath
                };
                var diff = injector.PreviewDiff(config, _loadedComponents);

                MapperLogger.Diff($"Already present    : {diff.AlreadyPresent.Count}");
                foreach (var s in diff.AlreadyPresent) MapperLogger.Info($"  = {s}");
                MapperLogger.Diff($"To be injected     : {diff.ToBeInjected.Count}");
                foreach (var i in diff.ToBeInjected) MapperLogger.Info($"  + {i}");
                MapperLogger.Diff($"Unsupported (skip) : {diff.Unsupported.Count}");
                foreach (var u in diff.Unsupported) MapperLogger.Warn($"  ! {u}");

                if (diff.ToBeInjected.Count == 0)
                {
                    MapperLogger.Info("Nothing to inject — project already up to date.");
                    MessageBox.Show("All components already match the project.\nNothing to inject.",
                        "Up To Date", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // Inject
                MapperLogger.Write("Injecting into EAE project files");
                var result = await Task.Run(() => injector.Inject(config, _loadedComponents));

                if (!result.Success)
                {
                    MapperLogger.Error($"Injection failed: {result.ErrorMessage}");
                    MessageBox.Show($"Injection failed:\n\n{result.ErrorMessage}",
                        "Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                MapperLogger.Write($"Injected {result.InjectedFBs.Count} FB(s)");
                foreach (var fb in result.InjectedFBs) MapperLogger.Info($"  ✓ {fb}");

                if (result.UnsupportedComponents.Count > 0)
                {
                    MapperLogger.Warn($"Skipped {result.UnsupportedComponents.Count} unsupported component(s)");
                    foreach (var u in result.UnsupportedComponents) MapperLogger.Warn($"  ! {u}");
                }

                // Touch .dfbproj → triggers EAE Reload Solution
                MapperLogger.Touch($"Touching {Path.GetFileName(dfbproj)}");
                File.SetLastWriteTime(dfbproj, DateTime.Now);
                MapperLogger.Info("EAE will show 'Reload Solution' — click Yes.");

                lblStatus.Text = $"Done — {result.InjectedFBs.Count} component(s) injected";
                MapperLogger.Info("=== Done ===");

                MessageBox.Show(
                    $"Injected {result.InjectedFBs.Count} component(s) successfully.\n\n" +
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

        // ── Component selection → Inputs panel ───────────────────────────────

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

        private bool TemplateMatchesStateCount(VueOneComponent component, FBTemplate template)
        {
            return component.Type.ToLower() switch
            {
                "actuator" => component.States.Count == 5,
                "sensor" => component.States.Count == 2,
                "process" => true,
                _ => false
            };
        }

        private string ResolveTemplatePath(VueOneComponent component)
        {
            var config = GetMapperConfig();
            return component.Type.ToLower() switch
            {
                "actuator" => config.ActuatorTemplatePath,
                "sensor" => config.SensorTemplatePath,
                "process" => config.ProcessCATTemplatePath,
                _ => string.Empty
            };
        }

        private MapperConfig GetMapperConfig()
        {
            _mapperConfig ??= MapperConfig.Load();
            return _mapperConfig;
        }

        /// <summary>
        /// Walks up from the syslay path until it finds a folder containing a .dfbproj.
        /// </summary>
        private static string? DeriveBaselineFolder(string syslayPath)
        {
            var dir = Path.GetDirectoryName(syslayPath);
            while (dir != null)
            {
                var match = Directory.GetFiles(dir, "*.dfbproj").FirstOrDefault();
                if (match != null) return match;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }
    }
}