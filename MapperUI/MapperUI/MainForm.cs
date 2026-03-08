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
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

// CS0104: both CodeGen.Mapping and MapperUI.Services define MappingRuleEngine.
using RuleEngine = MapperUI.Services.MappingRuleEngine;

namespace MapperUI
{
    public partial class MainForm : Form
    {
        private MapperConfig? _mapperConfig;
        private List<VueOneComponent> _loadedComponents = new();
        private List<ComponentValidationRow> _validationRows = new();
        private SystemXmlReader? _lastReader;
        private DebugConsoleForm? _debugConsole;

        // ── Per-component validation state ────────────────────────────────────

        private sealed class ComponentValidationRow
        {
            public VueOneComponent Component { get; init; } = null!;
            public string TemplateName { get; init; } = string.Empty;
            public bool IsValid { get; init; }
            public string FailReason { get; init; } = string.Empty;
        }

        // ── Mapping type colors (from xlsx cell fills) ────────────────────────
        private static readonly Color ColorTranslated = Color.FromArgb(56, 142, 60);
        private static readonly Color ColorDiscarded = Color.FromArgb(204, 72, 0);
        private static readonly Color ColorAssumed = Color.FromArgb(180, 130, 0);
        private static readonly Color ColorEncoded = Color.FromArgb(31, 97, 180);
        private static readonly Color ColorHardcoded = Color.FromArgb(110, 110, 110);

        private static readonly Color RowEven = Color.White;
        private static readonly Color RowOdd = Color.FromArgb(245, 245, 245);

        // ── Validated cell symbols ────────────────────────────────────────────
        private const string SymPass = "✓";
        private const string SymFail = "✗";

        public MainForm()
        {
            InitializeComponent();
            btnGenerateCode.Enabled = false;
        }

        // ── Build > Debug Console ─────────────────────────────────────────────

        private void menuItemDebugConsole_Click(object sender, EventArgs e)
        {
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
            using var dlg = new OpenFileDialog
            {
                Filter = "XML Files (*.xml)|*.xml|All Files (*.*)|*.*",
                Title = "Select VueOne Control.xml"
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            txtModelPath.Text = dlg.FileName;
            lblStatus.Text = "Validating...";
            MapperLogger.Parse($"Loading: {dlg.FileName}");
            await LoadAndValidate(dlg.FileName);
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

        private void PopulateMappingRules()
        {
            dgvMappingRules.Rows.Clear();

            bool hasAct = _loadedComponents.Any(c => IsActuator(c));
            bool hasSns = _loadedComponents.Any(c => IsSensor(c));
            bool hasPrc = _loadedComponents.Any(c => IsProcess(c));

            var rules = RuleEngine.GetRelevantRules(hasAct, hasSns, hasPrc).ToList();

            for (int i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                int idx = dgvMappingRules.Rows.Add(
                    rule.VueOneElement,
                    rule.IEC61499Element,
                    rule.Type.ToString(),
                    rule.TransformationRule);

                var row = dgvMappingRules.Rows[idx];
                Color bg = (i % 2 == 0) ? RowEven : RowOdd;
                row.DefaultCellStyle.BackColor = bg;
                row.DefaultCellStyle.ForeColor = Color.Black;

                var typeCell = row.Cells[colMappingType.Index];
                (typeCell.Style.ForeColor, typeCell.Style.Font) = rule.Type switch
                {
                    MappingType.TRANSLATED => (ColorTranslated, new Font(dgvMappingRules.Font, FontStyle.Bold)),
                    MappingType.DISCARDED => (ColorDiscarded, new Font(dgvMappingRules.Font, FontStyle.Bold)),
                    MappingType.ASSUMED => (ColorAssumed, new Font(dgvMappingRules.Font, FontStyle.Bold)),
                    MappingType.ENCODED => (ColorEncoded, new Font(dgvMappingRules.Font, FontStyle.Bold)),
                    MappingType.HARDCODED => (ColorHardcoded, new Font(dgvMappingRules.Font, FontStyle.Regular)),
                    _ => (Color.Black, dgvMappingRules.Font)
                };
                typeCell.Style.BackColor = bg;
            }
        }

        // ── Load and Validate ─────────────────────────────────────────────────

        private async Task LoadAndValidate(string path)
        {
            dgvMappingRules.Rows.Clear();
            dgvComponents.Rows.Clear();
            _validationRows.Clear();
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

                var selector = new TemplateSelector();
                var validator = new ComponentValidator();
                bool anyValid = false;
                int rowIdx = 0;

                foreach (var comp in _loadedComponents)
                {
                    var vr = ValidateComponent(comp, selector, validator);
                    _validationRows.Add(vr);

                    int idx = dgvComponents.Rows.Add(
                        comp.Name,
                        comp.Type,
                        vr.TemplateName,
                        vr.IsValid ? SymPass : SymFail);

                    var row = dgvComponents.Rows[idx];
                    Color bg = (rowIdx++ % 2 == 0) ? RowEven : RowOdd;
                    row.DefaultCellStyle.BackColor = bg;
                    row.DefaultCellStyle.ForeColor = Color.Black;

                    // Color only the Validated cell
                    var cell = row.Cells[colValidated.Index];
                    cell.Style.ForeColor = vr.IsValid
                        ? Color.FromArgb(56, 142, 60)   // bold green
                        : Color.FromArgb(204, 72, 0);   // orange-red
                    cell.Style.BackColor = bg;

                    if (vr.IsValid) anyValid = true;

                    MapperLogger.Validate(
                        $"{comp.Name} ({comp.Type}) → {vr.TemplateName} [{(vr.IsValid ? "PASS" : "FAIL: " + vr.FailReason)}]");
                }

                UpdateDetectedInfo();

                bool allSupported = _validationRows.All(r => r.IsValid);
                bool anySupported = _validationRows.Any(r => r.IsValid);

                if (allSupported)
                {
                    SetValidationLabel("PASSED", Color.Green);
                    lblStatus.Text = "Validation passed";
                }
                else
                {
                    SetValidationLabel("FAILED", Color.Red);
                    lblStatus.Text = "Validation FAILED — unsupported components present";
                }

                // Enable Generate Code as long as at least one injectable component exists
                btnGenerateCode.Enabled = anySupported;
            }
            catch (Exception ex)
            {
                MapperLogger.Error($"LoadAndValidate: {ex.Message}");
                MessageBox.Show($"Error loading file.\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                lblStatus.Text = "Error";
            }
        }

        // ── Per-component validation ──────────────────────────────────────────

        /// <summary>
        /// Validates a single component against its specific CAT type rules:
        ///   Actuator → exactly 5 states required
        ///   Sensor   → exactly 2 states required
        ///   Process  → always valid (any state count)
        ///   Other    → invalid (no CAT template)
        /// </summary>
        private ComponentValidationRow ValidateComponent(
            VueOneComponent comp, TemplateSelector selector, ComponentValidator validator)
        {
            string tPath = ResolveTemplatePath(comp);
            string tName = string.IsNullOrEmpty(tPath) ? "No Template Found" : Path.GetFileName(tPath);

            if (IsProcess(comp))
                return new ComponentValidationRow { Component = comp, TemplateName = tName, IsValid = true };

            if (IsActuator(comp))
            {
                // 5-state rule
                if (comp.States.Count != 5)
                    return new ComponentValidationRow
                    {
                        Component = comp,
                        TemplateName = tName,
                        IsValid = false,
                        FailReason = $"Actuator must have 5 states (has {comp.States.Count})"
                    };

                var vr = validator.Validate(comp);
                return new ComponentValidationRow
                {
                    Component = comp,
                    TemplateName = tName,
                    IsValid = vr.IsValid,
                    FailReason = vr.IsValid ? "" : string.Join("; ", vr.Errors)
                };
            }

            if (IsSensor(comp))
            {
                // 2-state rule
                if (comp.States.Count != 2)
                    return new ComponentValidationRow
                    {
                        Component = comp,
                        TemplateName = tName,
                        IsValid = false,
                        FailReason = $"Sensor must have 2 states (has {comp.States.Count})"
                    };

                var vr = validator.Validate(comp);
                return new ComponentValidationRow
                {
                    Component = comp,
                    TemplateName = tName,
                    IsValid = vr.IsValid,
                    FailReason = vr.IsValid ? "" : string.Join("; ", vr.Errors)
                };
            }

            // Robot, unknown type, or wrong state count
            string reason = comp.Type.ToLower() switch
            {
                "robot" => "Robot type not yet supported",
                "actuator" => $"Unsupported actuator ({comp.States.Count} states — only 5-state supported)",
                "sensor" => $"Unsupported sensor ({comp.States.Count} states — only 2-state supported)",
                _ => $"Unknown type '{comp.Type}'"
            };
            return new ComponentValidationRow
            {
                Component = comp,
                TemplateName = tName,
                IsValid = false,
                FailReason = reason
            };
        }

        // ── Generate Code ─────────────────────────────────────────────────────

        private async void btnGenerateCode_Click(object sender, EventArgs e)
        {
            if (_loadedComponents.Count == 0)
            {
                MessageBox.Show("Load a Control.xml first.", "No Data",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // BLOCKER: warn user if any components failed validation
            var failed = _validationRows.Where(r => !r.IsValid).ToList();
            if (failed.Any())
            {
                var sb = new StringBuilder();
                sb.AppendLine($"⚠  {failed.Count} component(s) failed validation and will be SKIPPED:\n");
                foreach (var f in failed)
                    sb.AppendLine($"  ✗  {f.Component.Name} ({f.Component.Type}) — {f.FailReason}");
                sb.AppendLine();
                sb.AppendLine("Valid components will still be injected.");
                sb.AppendLine("Continue?");

                var answer = MessageBox.Show(sb.ToString(), "Validation Warnings",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                if (answer != DialogResult.Yes) return;
            }

            btnGenerateCode.Enabled = false;
            MapperLogger.Info("=== Generate Code ===");

            try
            {
                _mapperConfig = MapperConfig.Load();
                MapperLogger.Info($"Batch limit : {(_mapperConfig.MaxNewInsertionsPerRun == 0 ? "unlimited" : _mapperConfig.MaxNewInsertionsPerRun.ToString())} new FBs per run");
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
                    MessageBox.Show("Cannot find .dfbproj above syslay path.\nCheck mapper_config.json.",
                        "Config Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                MapperLogger.Info($"dfbproj → {dfbproj}");

                var dfbContent = await File.ReadAllTextAsync(dfbproj);
                bool actuatorReg = dfbContent.Contains("Five_State_Actuator_CAT");
                bool sensorReg = dfbContent.Contains("Sensor_Bool_CAT");
                MapperLogger.Validate($"Five_State_Actuator_CAT : {(actuatorReg ? "FOUND ✓" : "MISSING ✗")}");
                MapperLogger.Validate($"Sensor_Bool_CAT         : {(sensorReg ? "FOUND ✓" : "MISSING ✗")}");

                if (!actuatorReg || !sensorReg)
                {
                    MapperLogger.Error("Required CAT types not registered — wrong project?");
                    MessageBox.Show("Required CAT types not found in .dfbproj.\nCheck mapper_config.json.",
                        "Wrong Project", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var injector = new SystemInjector();
                var cfg = new MapperConfig
                {
                    SyslayPath = _mapperConfig.SyslayPath,
                    SysresPath = _mapperConfig.SysresPath,
                    MaxNewInsertionsPerRun = _mapperConfig.MaxNewInsertionsPerRun
                };

                // Diff
                var diff = injector.PreviewDiff(cfg, _loadedComponents);
                MapperLogger.Diff($"Already present    : {diff.AlreadyPresent.Count}");
                foreach (var s in diff.AlreadyPresent) MapperLogger.Info($"  = {s}");
                MapperLogger.Diff($"To be injected     : {diff.ToBeInjected.Count}");
                foreach (var i in diff.ToBeInjected) MapperLogger.Info($"  + {i}");
                MapperLogger.Diff($"Unsupported (skip) : {diff.Unsupported.Count}");
                foreach (var u in diff.Unsupported) MapperLogger.Warn($"  ! {u}");

                if (diff.ToBeInjected.Count == 0)
                {
                    MessageBox.Show("All injectable components already match the project.\nNothing to inject.",
                        "Up To Date", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    MapperLogger.Info("Nothing to inject — project already up to date.");
                    return;
                }

                // Inject
                MapperLogger.Write("Injecting into EAE project files");
                var result = await Task.Run(() => injector.Inject(cfg, _loadedComponents));

                if (!result.Success)
                {
                    MapperLogger.Error($"Injection failed: {result.ErrorMessage}");
                    MessageBox.Show($"Injection failed:\n\n{result.ErrorMessage}",
                        "Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                MapperLogger.Write($"Injected {result.InjectedFBs.Count} FB(s)");
                foreach (var fb in result.InjectedFBs) MapperLogger.Info($"  ✓ {fb}");
                foreach (var u in result.UnsupportedComponents) MapperLogger.Warn($"  ! skipped: {u}");

                // Touch .dfbproj → EAE shows Reload Solution
                MapperLogger.Touch($"Touching {Path.GetFileName(dfbproj)}");
                File.SetLastWriteTime(dfbproj, DateTime.Now);
                MapperLogger.Info("EAE will show 'Reload Solution' — click Yes.");

                lblStatus.Text = $"Done — {result.InjectedFBs.Count} component(s) injected";
                MapperLogger.Info("=== Done ===");

                var doneMsg = new StringBuilder();
                doneMsg.AppendLine($"Injected {result.InjectedFBs.Count} component(s) successfully.");
                if (result.LimitReached)
                    doneMsg.AppendLine($"\n⚠  Batch limit ({cfg.MaxNewInsertionsPerRun}) reached.\nRun Generate Code again to inject the next batch.");
                if (result.UnsupportedComponents.Any())
                    doneMsg.AppendLine($"\n{result.UnsupportedComponents.Count} unsupported component(s) were skipped.");
                doneMsg.AppendLine("\nSwitch to EAE — click 'Reload Solution'.");

                MessageBox.Show(doneMsg.ToString(), "Done — Reload EAE",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
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

            // Show fail reason in output panel if component is invalid
            var vr = _validationRows.FirstOrDefault(r =>
                string.Equals(r.Component.Name, name, StringComparison.OrdinalIgnoreCase));
            if (vr != null && !vr.IsValid && !string.IsNullOrEmpty(vr.FailReason))
                dgvOutputs.Rows.Add(vr.FailReason, "");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void UpdateDetectedInfo()
        {
            if (_loadedComponents.Count == 0) return;

            int a = _loadedComponents.Count(c => c.Type == "Actuator");
            int s = _loadedComponents.Count(c => c.Type == "Sensor");
            int p = _loadedComponents.Count(c => c.Type == "Process");

            lblDetectedType.Text = _loadedComponents.Count == 1 ? _loadedComponents[0].Type : "System";
            lblDetectedName.Text = _loadedComponents.Count == 1 ? _loadedComponents[0].Name : (_lastReader?.SystemName ?? "-");
            lblDetectedStates.Text = _loadedComponents.Count == 1
                ? _loadedComponents[0].States.Count.ToString()
                : $"{_loadedComponents.Count} ({a}A / {s}S / {p}P)";
        }

        private void SetValidationLabel(string text, Color color)
        {
            lblValidationStatus.Text = text;
            lblValidationStatus.ForeColor = color;
        }

        private string ResolveTemplatePath(VueOneComponent c)
        {
            var cfg = GetMapperConfig();
            return c.Type.ToLowerInvariant() switch
            {
                "actuator" => cfg.ActuatorTemplatePath,
                "sensor" => cfg.SensorTemplatePath,
                "process" => cfg.ProcessCATTemplatePath,
                _ => string.Empty
            };
        }

        private MapperConfig GetMapperConfig()
        {
            _mapperConfig ??= MapperConfig.Load();
            return _mapperConfig;
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

        // ── Type guards ───────────────────────────────────────────────────────
        private static bool IsActuator(VueOneComponent c) =>
            string.Equals(c.Type, "Actuator", StringComparison.OrdinalIgnoreCase);
        private static bool IsSensor(VueOneComponent c) =>
            string.Equals(c.Type, "Sensor", StringComparison.OrdinalIgnoreCase);
        private static bool IsProcess(VueOneComponent c) =>
            string.Equals(c.Type, "Process", StringComparison.OrdinalIgnoreCase);
    }
}