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
        private readonly MapperService _mapperService;
        private MapperConfig? _mapperConfig;
        private List<VueOneComponent> _loadedComponents = new List<VueOneComponent>();
        private SystemXmlReader? _lastReader;

        public MainForm()
        {
            InitializeComponent();
            _mapperService = new MapperService();
            MapperLogger.OnEntry += OnLogEntry;
            btnGenerate.Enabled = false;
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

        // ── Mapping Rules button ──────────────────────────────────────────────

        private void btnMappingRules_Click(object sender, EventArgs e)
        {
            if (_loadedComponents.Count == 0)
            {
                MessageBox.Show("Load a Control.xml file first.", "No Data",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            PopulateMappingRules();
            UpdateDetectedInfo();
        }

        // ── Core mapping-rules population — calls MappingRuleEngine ──────────

        private void PopulateMappingRules()
        {
            dgvMappingRules.Rows.Clear();

            // ── System-level rows (once at top) ───────────────────────────────
            var sysId = _lastReader?.SystemID ?? "";
            var sysName = _lastReader?.SystemName ?? "";

            if (!string.IsNullOrWhiteSpace(sysId) || !string.IsNullOrWhiteSpace(sysName))
            {
                // Section header
                AddSectionHeader($"── SYSTEM: {sysName} ──────────────────────────────");

                foreach (var rule in MappingRuleEngine.GetSystemRules(sysId, sysName))
                    AddRuleRow(rule);
            }

            // ── Per-component rows ────────────────────────────────────────────
            foreach (var component in _loadedComponents)
            {
                var fbType = component.Type switch
                {
                    "Actuator" when component.States.Count == 5 => "Five_State_Actuator_CAT",
                    "Sensor" when component.States.Count == 2 => "Sensor_Bool_CAT",
                    "Process" => "Process1_CAT",
                    _ => $"? ({component.Type}, {component.States.Count} states)"
                };

                // Section header per component
                AddSectionHeader(
                    $"── COMPONENT: {component.Name}  [{component.Type}]  →  {fbType} ──");

                foreach (var rule in MappingRuleEngine.GetComponentRules(component))
                    AddRuleRow(rule);
            }

            MapperLogger.Info(
                $"Mapping Rules populated: {dgvMappingRules.Rows.Count} rows " +
                $"across {_loadedComponents.Count} component(s)");
        }

        // ── Adds a grey section-header row ───────────────────────────────────

        private void AddSectionHeader(string text)
        {
            int idx = dgvMappingRules.Rows.Add(text, "", "", "", false);
            var row = dgvMappingRules.Rows[idx];
            row.DefaultCellStyle.BackColor = Color.FromArgb(55, 55, 75);
            row.DefaultCellStyle.ForeColor = Color.White;
            row.DefaultCellStyle.Font =
                new Font("Segoe UI", 8.5f, FontStyle.Bold);
        }

        // ── Adds one MappingRule row with colour-coding by type ───────────────

        private void AddRuleRow(MappingRule rule)
        {
            // Type label shown in the "Mapping Type" column
            string typeLabel = rule.Type.ToString();

            // Transformation Rule column = rule text; Notes appended if non-empty
            string ruleText = string.IsNullOrWhiteSpace(rule.Notes)
                ? rule.TransformationRule
                : $"{rule.TransformationRule}  [{rule.Notes}]";

            int idx = dgvMappingRules.Rows.Add(
                rule.VueOneElement,
                rule.IEC61499Element,
                typeLabel,
                ruleText,
                rule.Validated);   // ticks the checkbox column

            var row = dgvMappingRules.Rows[idx];

            // Row colour by mapping type
            row.DefaultCellStyle.BackColor = rule.Type switch
            {
                MappingType.TRANSLATED => Color.FromArgb(220, 255, 220),   // light green
                MappingType.HARDCODED => Color.FromArgb(220, 220, 220),   // light grey
                MappingType.ASSUMED => Color.FromArgb(255, 255, 200),   // light yellow
                MappingType.ENCODED => Color.FromArgb(200, 230, 255),   // light blue
                MappingType.DISCARDED => Color.FromArgb(255, 220, 215),   // light red/salmon
                _ => Color.White
            };

            // DISCARDED rows: dim foreground so they visually recede
            if (rule.Type == MappingType.DISCARDED)
                row.DefaultCellStyle.ForeColor = Color.FromArgb(130, 80, 70);
        }

        // ── Load & Validate ───────────────────────────────────────────────────

        private async Task LoadAndValidate(string xmlPath)
        {
            dgvComponents.Rows.Clear();
            dgvMappingRules.Rows.Clear();

            try
            {
                var systemReader = new SystemXmlReader();

                await Task.Run(() =>
                {
                    _loadedComponents = systemReader.ReadAllComponents(xmlPath);
                });

                _lastReader = systemReader;
                MapperLogger.Parse(
                    $"Parsed {_loadedComponents.Count} component(s) from {Path.GetFileName(xmlPath)}");

                if (_loadedComponents.Count == 0)
                {
                    MessageBox.Show(
                        "No components found in Control.xml.\n" +
                        "Check that the XML has Type='System' or Type='Component'.",
                        "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    btnGenerate.Enabled = false;
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
                    bool isProcess = string.Equals(component.Type, "Process",
                                         StringComparison.OrdinalIgnoreCase);
                    bool tMatch = template != null &&
                                     TemplateMatchesStateCount(component, template);
                    string tPath = ResolveTemplatePath(component);
                    bool tExists = !string.IsNullOrEmpty(tPath) && File.Exists(tPath);

                    string tName = tMatch
                        ? (tExists ? Path.GetFileName(tPath)
                                   : Path.GetFileName(tPath) + " (not found)")
                        : "No Template Found";

                    dgvComponents.Rows.Add(component.Name, component.Type, tName);
                    MapperLogger.Validate($"{component.Name} ({component.Type}) → {tName}");

                    if (isProcess)
                    {
                        hasInjectables = true;
                    }
                    else if (tMatch && tExists)
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
                    btnGenerate.Enabled = true;
                    lblStatus.Text = "Validation passed";
                    lblValidationStatus.Text = "PASSED";
                    lblValidationStatus.ForeColor = Color.Green;
                    MapperLogger.Validate("Phase 1 validation PASSED");
                }
                else
                {
                    btnGenerate.Enabled = false;
                    lblStatus.Text = "Validation failed";
                    lblValidationStatus.Text = "FAILED";
                    lblValidationStatus.ForeColor = Color.Red;
                    MapperLogger.Warn("Phase 1 validation FAILED — check templates");
                }

                btnInjectSystem.Enabled = hasInjectables;
                UpdateDetectedInfo();
            }
            catch (Exception ex)
            {
                MapperLogger.Error($"LoadAndValidate: {ex.Message}");
                MessageBox.Show($"Error loading file.\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                btnGenerate.Enabled = false;
                lblStatus.Text = "Error";
            }
        }

        // ── Generate FB ───────────────────────────────────────────────────────

        private async void btnGenerate_Click(object sender, EventArgs e)
        {
            try
            {
                lblStatus.Text = "Generating...";
                btnGenerate.Enabled = false;
                MapperLogger.Info("Generate FB started");

                if (_loadedComponents.Count == 0)
                {
                    MessageBox.Show(
                        "Load a Control.xml file first.\n\n" +
                        "Note: Use 'Generate Staged Project' for Process components.",
                        "No Data", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var results = new List<MapperResult>();

                foreach (var component in _loadedComponents)
                {
                    bool isProcess = string.Equals(component.Type, "Process",
                                         StringComparison.OrdinalIgnoreCase);
                    if (isProcess)
                    {
                        MapperLogger.Info(
                            $"Skipping {component.Name} (Process — use Generate Staged Project)");
                        continue;
                    }

                    MapperLogger.Info($"Generating FB for {component.Name} ({component.Type})");
                    var result = await _mapperService.RunMapping(component);
                    results.Add(result);

                    if (result.Success)
                        MapperLogger.Write($"  ✓ {component.Name} → {result.OutputPath}");
                    else
                        MapperLogger.Error($"  ✗ {component.Name}: {result.ErrorMessage}");
                }

                bool allOk = results.All(r => r.Success);

                if (allOk)
                {
                    lblStatus.Text = "Generation complete";
                    MapperLogger.Info("Generate FB complete — all succeeded");
                    MessageBox.Show(
                        $"Generated {results.Count(r => r.Success)} FB(s) successfully.",
                        "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    lblStatus.Text = "Generation completed with errors";
                    var errors = string.Join("\n",
                        results.Where(r => !r.Success)
                               .Select(r => $"• {r.ComponentName}: {r.ErrorMessage}"));
                    MessageBox.Show($"Some FBs failed:\n\n{errors}", "Partial Failure",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                MapperLogger.Error($"Generate FB exception: {ex.Message}");
                MessageBox.Show($"Unexpected error.\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnGenerate.Enabled = true;
            }
        }

        // ── Generate Staged Project ───────────────────────────────────────────

        private async void btnInjectSystem_Click(object sender, EventArgs e)
        {
            if (_loadedComponents == null || _loadedComponents.Count == 0)
            {
                MapperLogger.Error("No components loaded — browse a Control.xml first.");
                MessageBox.Show("Load a Control.xml first.", "No Data",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            btnInjectSystem.Enabled = false;

            try
            {
                _mapperConfig = MapperConfig.Load();
                MapperLogger.Info("Config loaded.");
                MapperLogger.Info($"syslay → {_mapperConfig.SyslayPath}");
                MapperLogger.Info($"sysres → {_mapperConfig.SysresPath}");

                if (!File.Exists(_mapperConfig.SyslayPath))
                {
                    MapperLogger.Error($"syslay not found: {_mapperConfig.SyslayPath}");
                    MessageBox.Show(
                        $"syslay not found:\n{_mapperConfig.SyslayPath}\n\nCheck mapper_config.json.",
                        "Config Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var dfbproj = DeriveBaselineFolder(_mapperConfig.SyslayPath);
                if (dfbproj == null)
                {
                    MapperLogger.Error("Cannot find .dfbproj above syslay path.");
                    MessageBox.Show(
                        "Cannot find .dfbproj above the syslay path.\nCheck mapper_config.json.",
                        "Config Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                MapperLogger.Info($"dfbproj → {dfbproj}");

                MapperLogger.Validate("Checking CAT type registrations in .dfbproj");
                var dfbprojContent = await File.ReadAllTextAsync(dfbproj);
                bool actuatorReg = dfbprojContent.Contains("Five_State_Actuator_CAT");
                bool sensorReg = dfbprojContent.Contains("Sensor_Bool_CAT");
                bool processReg = dfbprojContent.Contains("Process1_CAT");

                MapperLogger.Validate(
                    $"Five_State_Actuator_CAT : {(actuatorReg ? "FOUND ✓" : "MISSING ✗")}");
                MapperLogger.Validate(
                    $"Sensor_Bool_CAT         : {(sensorReg ? "FOUND ✓" : "MISSING ✗")}");
                MapperLogger.Validate(
                    $"Process1_CAT            : {(processReg ? "FOUND ✓" : "MISSING ✗")}");

                if (!actuatorReg || !sensorReg)
                {
                    MapperLogger.Error("Required CAT types not registered — wrong project?");
                    MessageBox.Show(
                        "Required CAT types not found in .dfbproj.\n" +
                        "Is mapper_config.json pointing at the right project?",
                        "Wrong Project", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                MapperLogger.Diff("Running diff against live syslay");
                var injector = new SystemInjector();
                var config = new MapperConfig
                {
                    SyslayPath = _mapperConfig.SyslayPath,
                    SysresPath = _mapperConfig.SysresPath
                };
                var diff = injector.PreviewDiff(config, _loadedComponents);

                MapperLogger.Diff(
                    $"Already in project: {diff.AlreadyPresent.Count}  |  " +
                    $"Will remap: {diff.ToBeInjected.Count}  |  " +
                    $"Unsupported: {diff.Unsupported.Count}");
                foreach (var s in diff.AlreadyPresent) MapperLogger.Info($"  = {s}");
                foreach (var i in diff.ToBeInjected) MapperLogger.Info($"  + {i}");
                foreach (var u in diff.Unsupported) MapperLogger.Warn($"  ! {u}");

                if (diff.ToBeInjected.Count == 0)
                {
                    MapperLogger.Info("Nothing to remap — project already up to date.");
                    MessageBox.Show(
                        "All components already match the project.\nNothing to remap.",
                        "Up To Date", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                MapperLogger.Remap("Remapping FB names in live project files");
                var result = await Task.Run(() => injector.Inject(config, _loadedComponents));

                if (!result.Success)
                {
                    MapperLogger.Error($"Remap failed: {result.ErrorMessage}");
                    MessageBox.Show($"Remap failed:\n\n{result.ErrorMessage}",
                        "Remap Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                MapperLogger.Write(
                    $"Remap complete — {result.InjectedFBs.Count} FB(s) renamed");
                foreach (var fb in result.InjectedFBs)
                    MapperLogger.Info($"  ✓ {fb}");

                MapperLogger.Touch($"Touching {Path.GetFileName(dfbproj)}");
                File.SetLastWriteTime(dfbproj, DateTime.Now);
                MapperLogger.Info("EAE will show 'Reload Solution' — click Yes.");

                tabMain.SelectedTab = tabPageDebug;

                MessageBox.Show(
                    $"Remapped {result.InjectedFBs.Count} component(s) into the live project.\n\n" +
                    "Switch to EAE — it will show a 'Reload Solution' dialog.\nClick Yes.",
                    "Done — Reload EAE", MessageBoxButtons.OK, MessageBoxIcon.Information);

                MapperLogger.Info("=== Done ===");
            }
            catch (Exception ex)
            {
                MapperLogger.Error($"Exception: {ex.Message}");
                MessageBox.Show($"Unexpected error:\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnInjectSystem.Enabled = true;
            }
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

        private void debugConsoleToolStripMenuItem_Click(object sender, EventArgs e)
        {
            tabMain.SelectedTab = tabPageDebug;
            MapperLogger.Info("=== Debug Console ===");
        }

        // ── Component selection → state info in Inputs panel ─────────────────

        private void dgvComponents_SelectionChanged(object sender, EventArgs e)
        {
            dgvInputs.Rows.Clear();
            dgvOutputs.Rows.Clear();

            if (dgvComponents.SelectedRows.Count == 0) return;

            var name = dgvComponents.SelectedRows[0].Cells[0].Value?.ToString();
            var comp = _loadedComponents.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

            if (comp == null) return;

            // VueOneComponent has no Inputs/Outputs properties — show states as reference
            foreach (var state in comp.States.OrderBy(s => s.StateNumber))
                dgvInputs.Rows.Add(
                    $"State {state.StateNumber}: {state.Name}",
                    state.InitialState ? "★ initial" : "");
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
            if (string.Equals(component.Type, "Actuator", StringComparison.OrdinalIgnoreCase))
                return component.States.Count == 5;
            if (string.Equals(component.Type, "Sensor", StringComparison.OrdinalIgnoreCase))
                return component.States.Count == 2;
            if (string.Equals(component.Type, "Process", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        private MapperConfig GetMapperConfig()
        {
            _mapperConfig ??= MapperConfig.Load();
            return _mapperConfig;
        }

        private string ResolveTemplatePath(VueOneComponent component)
        {
            var config = GetMapperConfig();
            if (string.Equals(component.Type, "Actuator", StringComparison.OrdinalIgnoreCase))
                return config.ActuatorTemplatePath;
            if (string.Equals(component.Type, "Sensor", StringComparison.OrdinalIgnoreCase))
                return config.SensorTemplatePath;
            if (string.Equals(component.Type, "Process", StringComparison.OrdinalIgnoreCase))
                return config.ProcessCATTemplatePath;
            return string.Empty;
        }

        private string ResolveTemplateName(VueOneComponent component)
        {
            var path = ResolveTemplatePath(component);
            return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path);
        }

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