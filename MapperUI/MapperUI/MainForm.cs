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

// CS0104 guard: both CodeGen.Mapping and MapperUI.Services define MappingRuleEngine.
using RuleEngine = MapperUI.Services.MappingRuleEngine;
using UiMappingType = MapperUI.Services.MappingType;

namespace MapperUI
{
    public partial class MainForm : Form
    {
        private MapperConfig? _mapperConfig;
        private List<VueOneComponent> _loadedComponents = new();
        private List<ComponentValidationRow> _validationRows = new();
        private SystemXmlReader? _lastReader;
        private DebugConsoleForm? _debugConsole;

        // ── Per-component validation record ───────────────────────────────────

        private sealed class ComponentValidationRow
        {
            public VueOneComponent Component { get; init; } = null!;
            public string TemplateName { get; init; } = string.Empty;
            public bool IsValid { get; init; }
            public string FailReason { get; init; } = string.Empty;
        }

        // ── Colors ────────────────────────────────────────────────────────────

        private static readonly Color ColorTranslated = Color.FromArgb(56, 142, 60);
        private static readonly Color ColorDiscarded = Color.FromArgb(204, 72, 0);
        private static readonly Color ColorAssumed = Color.FromArgb(180, 130, 0);
        private static readonly Color ColorEncoded = Color.FromArgb(31, 97, 180);
        private static readonly Color ColorHardcoded = Color.FromArgb(110, 110, 110);
        private static readonly Color ColorSection = Color.FromArgb(220, 230, 242);

        private static readonly Color RowEven = Color.White;
        private static readonly Color RowOdd = Color.FromArgb(245, 245, 245);

        private const string SymPass = "✓";
        private const string SymFail = "✗";

        // ── Constructor ───────────────────────────────────────────────────────

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
        }

        // ── Populate Mapping Rules table with sections ────────────────────────

        private void PopulateMappingRules()
        {
            dgvMappingRules.Rows.Clear();

            bool hasAct = _loadedComponents.Any(c => TypeIs(c, "Actuator"));
            bool hasSns = _loadedComponents.Any(c => TypeIs(c, "Sensor"));
            bool hasPrc = _loadedComponents.Any(c => TypeIs(c, "Process"));

            var rules = RuleEngine.GetRelevantRules(hasAct, hasSns, hasPrc).ToList();

            int ruleRowIdx = 0;

            foreach (var rule in rules)
            {
                if (rule.IsSection)
                {
                    // Section header row — spans all columns visually
                    int idx = dgvMappingRules.Rows.Add(rule.SectionTitle, "", "", "", "");
                    var sectionRow = dgvMappingRules.Rows[idx];
                    sectionRow.Tag = "section";
                    for (int c = 0; c < sectionRow.Cells.Count; c++)
                    {
                        sectionRow.Cells[c].Style.BackColor = ColorSection;
                        sectionRow.Cells[c].Style.ForeColor = Color.FromArgb(28, 57, 100);
                        sectionRow.Cells[c].Style.Font = new Font(dgvMappingRules.Font, FontStyle.Bold);
                        sectionRow.Cells[c].Style.SelectionBackColor = ColorSection;
                        sectionRow.Cells[c].Style.SelectionForeColor = Color.FromArgb(28, 57, 100);
                    }
                    continue;
                }

                Color bg = (ruleRowIdx++ % 2 == 0) ? RowEven : RowOdd;

                // Validated symbol
                string sym = rule.IsImplemented ? SymPass : SymFail;

                int rowIdx = dgvMappingRules.Rows.Add(
                    rule.VueOneElement,
                    rule.IEC61499Element,
                    rule.Type.ToString(),
                    rule.TransformationRule,
                    sym);

                var row = dgvMappingRules.Rows[rowIdx];

                // Base row background
                for (int c = 0; c < row.Cells.Count; c++)
                    row.Cells[c].Style.BackColor = bg;

                // Mapping Type cell — colored font only
                var typeCell = row.Cells[colMappingType.Index];
                (typeCell.Style.ForeColor, typeCell.Style.Font) = rule.Type switch
                {
                    UiMappingType.TRANSLATED => (ColorTranslated, new Font(dgvMappingRules.Font, FontStyle.Bold)),
                    UiMappingType.DISCARDED => (ColorDiscarded, new Font(dgvMappingRules.Font, FontStyle.Bold)),
                    UiMappingType.ASSUMED => (ColorAssumed, new Font(dgvMappingRules.Font, FontStyle.Bold)),
                    UiMappingType.ENCODED => (ColorEncoded, new Font(dgvMappingRules.Font, FontStyle.Bold)),
                    UiMappingType.HARDCODED => (ColorHardcoded, new Font(dgvMappingRules.Font, FontStyle.Regular)),
                    _ => (Color.Black, dgvMappingRules.Font)
                };
                typeCell.Style.BackColor = bg;

                // Validated cell
                var valCell = row.Cells[colMappingValidated.Index];
                valCell.Style.ForeColor = rule.IsImplemented ? ColorTranslated : ColorDiscarded;
                valCell.Style.BackColor = bg;
            }
        }

        // CellFormatting keeps section rows non-selectable looking
        private void dgvMappingRules_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= dgvMappingRules.Rows.Count) return;
            var row = dgvMappingRules.Rows[e.RowIndex];
            if (row.Tag?.ToString() == "section")
            {
                e.CellStyle.BackColor = ColorSection;
                e.CellStyle.SelectionBackColor = ColorSection;
                e.CellStyle.SelectionForeColor = Color.FromArgb(28, 57, 100);
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

                var validator = new ComponentValidator();
                var cfg = GetMapperConfig();
                int rowIdx = 0;

                foreach (var comp in _loadedComponents)
                {
                    var vr = ValidateComponent(comp, validator, cfg);
                    _validationRows.Add(vr);

                    int idx = dgvComponents.Rows.Add(comp.Name, comp.Type, vr.TemplateName);

                    var row = dgvComponents.Rows[idx];
                    Color bg = (rowIdx++ % 2 == 0) ? RowEven : RowOdd;
                    row.DefaultCellStyle.BackColor = bg;
                    row.DefaultCellStyle.ForeColor = Color.Black;

                    // Color the Template cell: green for valid, red for "No Template Found"
                    var tmplCell = row.Cells[colTemplate.Index];
                    tmplCell.Style.ForeColor = vr.IsValid ? ColorTranslated : ColorDiscarded;
                    tmplCell.Style.BackColor = bg;

                    MapperLogger.Validate(
                        $"{comp.Name} ({comp.Type}) → {vr.TemplateName} " +
                        $"[{(vr.IsValid ? "PASS" : "FAIL: " + vr.FailReason)}]");
                }

                UpdateDetectedInfo();

                bool allPassed = _validationRows.All(r => r.IsValid);
                SetValidationLabel(allPassed ? "PASSED" : "FAILED", allPassed ? Color.Green : Color.Red);
                lblStatus.Text = allPassed
                    ? "Validation passed — click Generate Code to inject all valid components"
                    : "Validation FAILED — click Generate Code to inject only valid components";

                // Enable Generate Code only when at least one valid (injectable) component loaded
                btnGenerateCode.Enabled = _validationRows.Any(r => r.IsValid);
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
        /// Validates one component against its specific CAT type rules.
        ///   Actuator → exactly 5 states required
        ///   Sensor   → exactly 2 states required
        ///   Process  → always valid
        ///   Robot    → valid when RobotTemplatePath is set in mapper_config.json
        ///   Other    → always invalid (no CAT template)
        ///
        /// ResolveTemplatePath already returns empty for wrong state counts, so
        /// tName will be "No Template Found" for e.g. 13-state actuators.
        /// </summary>
        private static ComponentValidationRow ValidateComponent(
            VueOneComponent comp, ComponentValidator validator, MapperConfig cfg)
        {
            // Template name respects state-count guard — 13-state → "No Template Found"
            string tPath = ResolveTemplatePath(comp, cfg);
            string tName = string.IsNullOrEmpty(tPath)
                ? "No template found (discarded for this phase)"
                : Path.GetFileName(tPath);

            switch (comp.Type.ToLowerInvariant())
            {
                case "process":
                    return Pass(comp, tName);

                case "robot":
                    if (string.IsNullOrWhiteSpace(cfg.RobotTemplatePath))
                        return Fail(comp, tName, "RobotTemplatePath not set in mapper_config.json");
                    return Pass(comp, tName);

                case "actuator":
                    if (comp.States.Count != 5)
                        return Fail(comp, tName, "No template found (discarded for this phase)");
                    break;

                case "sensor":
                    if (comp.States.Count != 2)
                        return Fail(comp, tName, "No template found (discarded for this phase)");
                    break;

                default:
                    return Fail(comp, tName, $"Unknown type '{comp.Type}'");
            }

            // Actuator/sensor with correct state count — run standard validator
            var vr = validator.Validate(comp);
            return vr.IsValid
                ? Pass(comp, tName)
                : Fail(comp, tName, string.Join("; ", vr.Errors));
        }

        private static ComponentValidationRow Pass(VueOneComponent c, string t) =>
            new() { Component = c, TemplateName = t, IsValid = true };

        private static ComponentValidationRow Fail(VueOneComponent c, string t, string reason) =>
            new() { Component = c, TemplateName = t, IsValid = false, FailReason = reason };

        // ── Generate Code — injects ALL valid loaded components ───────────────

        private async void btnGenerateCode_Click(object sender, EventArgs e)
        {
            var failed = _validationRows
                .Where(r => !r.IsValid)
                .ToList();

            if (failed.Any())
            {
                var sb = new StringBuilder();
                sb.AppendLine($"⚠  {failed.Count} component(s) failed validation and will be skipped:\n");
                foreach (var f in failed)
                    sb.AppendLine($"  ✗  {f.Component.Name} ({f.Component.Type}) — {f.FailReason}");
                sb.AppendLine("\nAll valid components will be injected. Continue?");

                if (MessageBox.Show(sb.ToString(), "Validation Warnings",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return;
            }

            var toInject = _validationRows
                .Where(r => r.IsValid)
                .Select(r => r.Component)
                .ToList();

            if (toInject.Count == 0)
            {
                MessageBox.Show("No valid components are available to inject.",
                    "Nothing to Inject", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            btnGenerateCode.Enabled = false;
            MapperLogger.Info($"=== Generate Code — {toInject.Count} valid component(s) ===");
            foreach (var c in toInject) MapperLogger.Info($"  → {c.Name} ({c.Type})");

            try
            {
                var cfg = GetMapperConfig();
                MapperLogger.Info($"syslay → {cfg.SyslayPath}");
                MapperLogger.Info($"sysres → {cfg.SysresPath}");

                if (!File.Exists(cfg.SyslayPath))
                {
                    MapperLogger.Error($"syslay not found: {cfg.SyslayPath}");
                    MessageBox.Show($"syslay not found:\n{cfg.SyslayPath}\n\nCheck mapper_config.json.",
                        "Config Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var dfbproj = DeriveProjectFile(cfg.SyslayPath);
                if (dfbproj == null)
                {
                    MapperLogger.Error("Cannot find .dfbproj above syslay path.");
                    MessageBox.Show("Cannot find .dfbproj above the syslay path.\nCheck mapper_config.json.",
                        "Config Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                MapperLogger.Info($"dfbproj → {dfbproj}");

                var dfbContent = await File.ReadAllTextAsync(dfbproj);
                LogCatCheck(dfbContent, "Five_State_Actuator_CAT");
                LogCatCheck(dfbContent, "Sensor_Bool_CAT");
                if (!string.IsNullOrWhiteSpace(cfg.RobotTemplatePath))
                    LogCatCheck(dfbContent, "Robot_Task_CAT");

                if (!dfbContent.Contains("Five_State_Actuator_CAT") || !dfbContent.Contains("Sensor_Bool_CAT"))
                {
                    MapperLogger.Error("Required CAT types not registered — wrong project?");
                    MessageBox.Show(
                        "Five_State_Actuator_CAT or Sensor_Bool_CAT not found in .dfbproj.\nCheck mapper_config.json.",
                        "Wrong Project", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var injector = new SystemInjector();
                var diff = injector.PreviewDiff(cfg, toInject);
                LogDiff(diff);

                if (diff.ToBeInjected.Count == 0)
                {
                    MessageBox.Show(
                        "All valid components already match the project.\nNothing to inject.",
                        "Up To Date", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    MapperLogger.Info("Nothing to inject.");
                    return;
                }

                MapperLogger.Write("Injecting into EAE project files");
                var result = await Task.Run(() => injector.Inject(cfg, toInject));

                if (!result.Success)
                {
                    MapperLogger.Error($"Injection failed: {result.ErrorMessage}");
                    MessageBox.Show($"Injection failed:\n\n{result.ErrorMessage}",
                        "Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                foreach (var fb in result.InjectedFBs) MapperLogger.Info($"  ✓ {fb}");
                foreach (var u in result.UnsupportedComponents) MapperLogger.Warn($"  ! {u}");

                MapperLogger.Touch($"Touching {Path.GetFileName(dfbproj)}");
                File.SetLastWriteTime(dfbproj, DateTime.Now);
                MapperLogger.Info("EAE will show 'Reload Solution' — click Yes.");

                lblStatus.Text = $"Done — {result.InjectedFBs.Count} component(s) injected";
                MapperLogger.Info("=== Done ===");

                var msg = new StringBuilder();
                msg.AppendLine($"Injected {result.InjectedFBs.Count} component(s) successfully.");
                if (result.UnsupportedComponents.Any())
                    msg.AppendLine($"\n{result.UnsupportedComponents.Count} component(s) skipped (see Debug Console).");
                msg.AppendLine("\nSwitch to EAE and click 'Reload Solution'.");

                MessageBox.Show(msg.ToString(), "Done — Reload EAE",
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

        // ── Component selection → I/O panels ─────────────────────────────────

        private void dgvComponents_SelectionChanged(object sender, EventArgs e)
        {
            dgvInputs.Rows.Clear();
            dgvOutputs.Rows.Clear();

            if (dgvComponents.SelectedRows.Count == 0) return;

            // Show detail for the most recently selected row
            var name = dgvComponents.SelectedRows[0].Cells[0].Value?.ToString();
            var comp = _loadedComponents.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (comp == null) return;

            foreach (var s in comp.States.OrderBy(st => st.StateNumber))
                dgvInputs.Rows.Add($"State {s.StateNumber}: {s.Name}", "");

            // Show fail reason in Outputs if component is invalid
            var vr = _validationRows.FirstOrDefault(r =>
                string.Equals(r.Component.Name, name, StringComparison.OrdinalIgnoreCase));
            if (vr != null && !vr.IsValid && !string.IsNullOrEmpty(vr.FailReason))
                dgvOutputs.Rows.Add(vr.FailReason, "");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void UpdateDetectedInfo()
        {
            if (_loadedComponents.Count == 0) return;
            int a = _loadedComponents.Count(c => TypeIs(c, "Actuator"));
            int s = _loadedComponents.Count(c => TypeIs(c, "Sensor"));
            int p = _loadedComponents.Count(c => TypeIs(c, "Process"));

            lblDetectedType.Text = _loadedComponents.Count == 1 ? _loadedComponents[0].Type : "System";
            lblDetectedName.Text = _loadedComponents.Count == 1
                ? _loadedComponents[0].Name : (_lastReader?.SystemName ?? "-");
            lblDetectedStates.Text = _loadedComponents.Count == 1
                ? _loadedComponents[0].States.Count.ToString()
                : $"{_loadedComponents.Count} ({a}A / {s}S / {p}P)";

            // Update label colors after loading
            lblDetectedType.ForeColor = Color.FromArgb(0, 100, 180);
            lblDetectedName.ForeColor = Color.FromArgb(0, 100, 180);
            lblDetectedStates.ForeColor = Color.FromArgb(0, 100, 180);
        }

        private void SetValidationLabel(string text, Color color)
        {
            lblValidationStatus.Text = text;
            lblValidationStatus.ForeColor = color;
        }

        private void LogDiff(SystemInjector.DiffReport diff)
        {
            MapperLogger.Diff($"Already present    : {diff.AlreadyPresent.Count}");
            foreach (var s in diff.AlreadyPresent) MapperLogger.Info($"  = {s}");
            MapperLogger.Diff($"To be injected     : {diff.ToBeInjected.Count}");
            foreach (var i in diff.ToBeInjected) MapperLogger.Info($"  + {i}");
            MapperLogger.Diff($"Unsupported (skip) : {diff.Unsupported.Count}");
            foreach (var u in diff.Unsupported) MapperLogger.Warn($"  ! {u}");
        }

        private static void LogCatCheck(string dfbContent, string catType) =>
            MapperLogger.Validate($"{catType,-30} : {(dfbContent.Contains(catType) ? "FOUND ✓" : "MISSING ✗")}");

        private MapperConfig GetMapperConfig()
        {
            _mapperConfig ??= MapperConfig.Load();
            return _mapperConfig;
        }

        /// <summary>
        /// Resolves the CAT template path for a component.
        /// Returns empty string (→ "No Template Found") for:
        ///   - Actuators that are NOT 5-state (e.g. 13-state Bearing_PnP)
        ///   - Sensors that are NOT 2-state
        ///   - Robot when RobotTemplatePath is not configured
        /// </summary>
        private static string ResolveTemplatePath(VueOneComponent comp, MapperConfig cfg) =>
            comp.Type.ToLowerInvariant() switch
            {
                "actuator" when comp.States.Count == 5 => cfg.ActuatorTemplatePath,
                "sensor" when comp.States.Count == 2 => cfg.SensorTemplatePath,
                "process" => cfg.ProcessCATTemplatePath,
                "robot" when !string.IsNullOrWhiteSpace(cfg.RobotTemplatePath) => cfg.RobotTemplatePath,
                _ => string.Empty
            };

        private static string? DeriveProjectFile(string syslayPath)
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

        private static bool TypeIs(VueOneComponent c, string type) =>
            string.Equals(c.Type, type, StringComparison.OrdinalIgnoreCase);
    }
}