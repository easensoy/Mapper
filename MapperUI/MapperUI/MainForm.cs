// MapperUI/MapperUI/MainForm.cs
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

using RuleEngine = MapperUI.Services.MappingRuleEngine;
using MappingRule = MapperUI.Services.MappingRuleEntry;
using UiMappingType = MapperUI.Services.MappingType;

namespace MapperUI
{
    public partial class MainForm : Form
    {
        // ── State ─────────────────────────────────────────────────────────────
        private MapperConfig? _mapperConfig;
        private List<VueOneComponent> _loadedComponents = new();
        private List<ComponentValidationRow> _validationRows = new();
        private SystemXmlReader? _lastReader;
        private DebugConsoleForm? _debugConsole;

        // ── In-scope whitelist (actuators + sensors that are ready to inject) ─
        private static readonly HashSet<string> _allowedInstances =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // Actuators
                "Checker", "Transfer", "Feeder", "Ejector",
                // Sensors
                "PartInHopper", "PartAtChecker"
            };

        // ── Per-component validation record ──────────────────────────────────
        private sealed class ComponentValidationRow
        {
            public VueOneComponent Component { get; init; } = null!;
            public string TemplateName { get; init; } = string.Empty;
            public bool IsValid { get; init; }
            public string FailReason { get; init; } = string.Empty;
        }

        // ── Palette ───────────────────────────────────────────────────────────
        private static readonly Color ColorTranslated = Color.FromArgb(56, 142, 60);
        private static readonly Color ColorDiscarded = Color.FromArgb(204, 72, 0);
        private static readonly Color ColorAssumed = Color.FromArgb(180, 130, 0);
        private static readonly Color ColorEncoded = Color.FromArgb(31, 97, 180);
        private static readonly Color ColorHardcoded = Color.FromArgb(110, 110, 110);
        private static readonly Color ColorSection = Color.FromArgb(220, 230, 242);
        private static readonly Color RowEven = Color.White;
        private static readonly Color RowOdd = Color.FromArgb(245, 245, 245);
        private const string SymPass = "\u2713";
        private const string SymFail = "\u2717";

        // ── Constructor ───────────────────────────────────────────────────────
        public MainForm()
        {
            InitializeComponent();
            btnGenerateCode.Enabled = false;
            btnGenerateRobotWrapper.Enabled = true;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Menu
        // ─────────────────────────────────────────────────────────────────────

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

        // ─────────────────────────────────────────────────────────────────────
        // Browse + Load
        // ─────────────────────────────────────────────────────────────────────

        private async void btnBrowse_Click(object sender, EventArgs e)
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "XML Files (*.xml)|*.xml|All Files (*.*)|*.*",
                Title = "Open VueOne Control.xml"
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            txtModelPath.Text = dlg.FileName;
            await LoadAndValidateAsync(dlg.FileName);
        }

        private async Task LoadAndValidateAsync(string path)
        {
            dgvComponents.Rows.Clear();
            dgvMappingRules.Rows.Clear();
            dgvInputs.Rows.Clear();
            dgvOutputs.Rows.Clear();
            _loadedComponents.Clear();
            _validationRows.Clear();
            btnGenerateCode.Enabled = false;
            lblStatus.Text = "Loading\u2026";

            try
            {
                MapperLogger.Info($"Loading: {path}");

                _lastReader = new SystemXmlReader();

                _loadedComponents = await Task.Run(() => _lastReader.ReadAllComponents(path));

                if (_loadedComponents.Count == 0)
                {
                    MapperLogger.Error("No components found in file.");
                    MessageBox.Show("No components found in the selected file.",
                        "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    lblStatus.Text = "No components found";
                    return;
                }

                // ── Populate Mapping Rules grid ───────────────────────────────
                foreach (var rule in RuleEngine.GetAllRules())
                    AddMappingRuleRow(rule);

                var validator = new ComponentValidator();
                var cfg = GetMapperConfig();
                int rowIdx = 0;

                foreach (var comp in _loadedComponents)
                {
                    var vr = ValidateComponent(comp, validator, cfg);
                    _validationRows.Add(vr);

                    bool inScope = _allowedInstances.Contains(comp.Name);

                    int idx = dgvComponents.Rows.Add(comp.Name, comp.Type, vr.TemplateName);
                    var row = dgvComponents.Rows[idx];
                    Color bg = (rowIdx++ % 2 == 0) ? RowEven : RowOdd;
                    row.DefaultCellStyle.BackColor = bg;
                    row.DefaultCellStyle.ForeColor = Color.Black;

                    var tmplCell = row.Cells[2]; // Template column (3rd column)
                    bool showAsActive = vr.IsValid && inScope;
                    tmplCell.Style.ForeColor = showAsActive ? ColorTranslated : ColorDiscarded;
                    tmplCell.Style.BackColor = bg;

                    MapperLogger.Validate(
                        $"{comp.Name} ({comp.Type})  {vr.TemplateName} " +
                        $"[{(vr.IsValid ? "PASS" : "FAIL: " + vr.FailReason)}]" +
                        $"{(inScope ? "" : " [OUT OF SCOPE]")}");
                }

                UpdateDetectedInfo();

                bool allScopedPassed = _validationRows
                    .Where(r => _allowedInstances.Contains(r.Component.Name))
                    .All(r => r.IsValid);

                SetValidationLabel(
                    allScopedPassed ? "PASSED" : "FAILED",
                    allScopedPassed ? Color.Green : Color.Red);

                lblStatus.Text = allScopedPassed
                    ? "Validation passed. In-scope components ready to inject."
                    : "Validation failed. Check in-scope components.";

                btnGenerateCode.Enabled = _validationRows
                    .Any(r => r.IsValid && _allowedInstances.Contains(r.Component.Name));
            }
            catch (Exception ex)
            {
                MapperLogger.Error($"LoadAndValidate: {ex.Message}");
                MessageBox.Show($"Error loading file.\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                lblStatus.Text = "Error";
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Mapping Rules (View)
        // ─────────────────────────────────────────────────────────────────────

        private void btnMappingRules_Click(object sender, EventArgs e)
        {
            dgvMappingRules.Rows.Clear();

            _ = GetMapperConfig();
            foreach (var rule in RuleEngine.GetAllRules())
                AddMappingRuleRow(rule);

            MapperLogger.Info("Mapping rules refreshed.");
        }

        private void AddMappingRuleRow(MappingRule rule)
        {
            var vueOneElement = rule.IsSection ? rule.SectionTitle : rule.VueOneElement;
            var iecElement = rule.IsSection ? string.Empty : rule.IEC61499Element;
            var mapType = rule.IsSection ? string.Empty : rule.Type.ToString();
            var transformRule = rule.IsSection ? string.Empty : rule.TransformationRule;
            var validated = rule.IsSection ? string.Empty : (rule.IsImplemented ? SymPass : SymFail);

            int idx = dgvMappingRules.Rows.Add(
                vueOneElement,
                iecElement,
                mapType,
                transformRule,
                validated);

            var row = dgvMappingRules.Rows[idx];

            Color fg = rule.Type switch
            {
                UiMappingType.TRANSLATED => ColorTranslated,
                UiMappingType.DISCARDED => ColorDiscarded,
                UiMappingType.ASSUMED => ColorAssumed,
                UiMappingType.ENCODED => ColorEncoded,
                UiMappingType.HARDCODED => ColorHardcoded,
                _ => Color.Black
            };

            if (rule.IsSection)
            {
                foreach (DataGridViewCell cell in row.Cells)
                {
                    cell.Style.BackColor = ColorSection;
                    cell.Style.ForeColor = Color.FromArgb(30, 50, 100);
                    cell.Style.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
                }
            }
            else
            {
                row.Cells[colMappingType.Index].Style.ForeColor = fg;
            }

            row.Cells[colMappingValidated.Index].Style.ForeColor =
                rule.IsImplemented ? ColorTranslated : ColorDiscarded;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Per-component validation
        // ─────────────────────────────────────────────────────────────────────

        private static ComponentValidationRow ValidateComponent(
            VueOneComponent comp, ComponentValidator validator, MapperConfig cfg)
        {
            // Out-of-scope components get no template — they are not processed.
            bool inScope = _allowedInstances.Contains(comp.Name);

            string tPath = inScope ? ResolveTemplatePath(comp, cfg) : string.Empty;
            string tName = string.IsNullOrEmpty(tPath)
                ? "No template found (discarded for this phase)"
                : Path.GetFileName(tPath);

            if (!inScope)
                return Pass(comp, tName); // out of scope — show as discarded, not failed

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
                        return Fail(comp, tName, "Actuator must have exactly 5 states");
                    break;

                case "sensor":
                    if (comp.States.Count != 2)
                        return Fail(comp, tName, "Sensor must have exactly 2 states");
                    break;

                default:
                    return Fail(comp, tName, $"Unknown type '{comp.Type}'");
            }

            var vr = validator.Validate(comp);
            return vr.IsValid
                ? Pass(comp, tName)
                : Fail(comp, tName, string.Join("; ", vr.Errors));
        }

        private static string ResolveTemplatePath(VueOneComponent comp, MapperConfig cfg)
        {
            return comp.Type.ToLowerInvariant() switch
            {
                "actuator" => cfg.ActuatorTemplatePath,
                "sensor" => cfg.SensorTemplatePath,
                "process" => cfg.ProcessCATTemplatePath,
                "robot" => cfg.RobotTemplatePath,
                _ => string.Empty
            };
        }

        private static ComponentValidationRow Pass(VueOneComponent c, string t) =>
            new() { Component = c, TemplateName = t, IsValid = true };

        private static ComponentValidationRow Fail(VueOneComponent c, string t, string reason) =>
            new() { Component = c, TemplateName = t, IsValid = false, FailReason = reason };

        // ─────────────────────────────────────────────────────────────────────
        // Generate Code (Clone CAT types + Inject into syslay / sysres)
        // ─────────────────────────────────────────────────────────────────────

        private async void btnGenerateCode_Click(object sender, EventArgs e)
        {
            // ── pre-flight validation warnings ────────────────────────────────
            var failedInScope = _validationRows
                .Where(r => !r.IsValid && _allowedInstances.Contains(r.Component.Name))
                .ToList();

            if (failedInScope.Any())
            {
                var sb = new StringBuilder();
                sb.AppendLine($"{failedInScope.Count} in-scope component(s) failed validation:\n");
                foreach (var f in failedInScope)
                    sb.AppendLine($"  {SymFail}  {f.Component.Name} ({f.Component.Type}): {f.FailReason}");
                sb.AppendLine("\nOnly the remaining valid in-scope components will be injected. Continue?");
                if (MessageBox.Show(sb.ToString(), "Validation Warnings",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return;
            }

            // ── build inject list: all valid in-scope components ──────────────
            var toInject = _validationRows
                .Where(r => r.IsValid && _allowedInstances.Contains(r.Component.Name))
                .Select(r => r.Component)
                .ToList();

            foreach (var b in _validationRows.Where(r => !_allowedInstances.Contains(r.Component.Name)))
                MapperLogger.Info($"{b.Component.Name} ({b.Component.Type}) out of scope — skipped.");

            if (toInject.Count == 0)
            {
                MessageBox.Show(
                    "No in-scope components are ready to inject.\n\n" +
                    "In-scope: Checker, Transfer, Feeder, Ejector (actuators) " +
                    "and PartInHopper, PartAtChecker (sensors).",
                    "Nothing to Inject", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            btnGenerateCode.Enabled = false;

            var inScope = toInject.Select(c => $"{c.Name} ({c.Type})");
            MapperLogger.Info($"Generating code. {toInject.Count} component(s): {string.Join(", ", inScope)}");
            foreach (var c in toInject) MapperLogger.Info($"{SymPass} {c.Name} ({c.Type})");

            try
            {
                var cfg = GetMapperConfig();
                MapperLogger.Info($"syslay : {cfg.SyslayPath}");
                MapperLogger.Info($"sysres : {cfg.SysresPath}");

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
                MapperLogger.Info($"Project: {Path.GetFileName(dfbproj)}");

                var dfbContent = await File.ReadAllTextAsync(dfbproj);
                LogCatCheck(dfbContent, "Five_State_Actuator_CAT");
                LogCatCheck(dfbContent, "Sensor_Bool_CAT");
                if (!string.IsNullOrWhiteSpace(cfg.RobotTemplatePath))
                    LogCatCheck(dfbContent, "Robot_Task_CAT");

                if (!dfbContent.Contains("Five_State_Actuator_CAT") ||
                    !dfbContent.Contains("Sensor_Bool_CAT"))
                {
                    MapperLogger.Error("Required CAT types not registered in this project.");
                    MessageBox.Show(
                        "Five_State_Actuator_CAT or Sensor_Bool_CAT not found in .dfbproj.\n" +
                        "Check mapper_config.json.",
                        "Wrong Project", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // ─────────────────────────────────────────────────────────────
                // PHASE 1: Clone CAT types for each in-scope component
                // ─────────────────────────────────────────────────────────────
                // Creates IEC61499\{TemplateName}_{ComponentName}\ with all 11
                // files and registers them in dfbproj.

                MapperLogger.Info("── Phase 1: Cloning CAT types ──");
                int cloned = 0;
                foreach (var comp in toInject)
                {
                    var templatePath = ResolveTemplatePath(comp, cfg);
                    if (string.IsNullOrEmpty(templatePath) || !File.Exists(templatePath))
                    {
                        MapperLogger.Warn($"  {SymFail} {comp.Name}: no template — skipped clone.");
                        continue;
                    }

                    var templateBaseName = Path.GetFileNameWithoutExtension(templatePath);
                    var componentToken = new string(comp.Name
                        .Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
                    var newCatName = $"{templateBaseName}_{componentToken}";

                    try
                    {
                        var cloneResult = CatTypeCloner.Clone(
                            templatePath, newCatName, dfbproj, comp.Name);
                        MapperLogger.Info($"  {SymPass} {newCatName} — created and registered.");
                        cloned++;
                    }
                    catch (Exception ex)
                    {
                        MapperLogger.Error($"  {SymFail} {comp.Name}: clone failed — {ex.Message}");
                    }
                }
                MapperLogger.Info($"── Phase 1 complete: {cloned} CAT type(s) cloned ──");

                // ─────────────────────────────────────────────────────────────
                // PHASE 2: Inject instances into syslay / sysres
                // ─────────────────────────────────────────────────────────────

                MapperLogger.Info("── Phase 2: Injecting instances into syslay/sysres ──");

                var injector = new SystemInjector();
                var diff = injector.PreviewDiff(cfg, toInject);
                LogDiff(diff);

                if (diff.ToBeInjected.Count == 0)
                {
                    MessageBox.Show(
                        $"{cloned} CAT type(s) cloned.\n\n" +
                        "All in-scope instances already present in syslay.\n" +
                        "Switch to EAE and click Reload Solution.",
                        "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    MapperLogger.Info("No new instances to inject (syslay already up to date).");
                    return;
                }

                MapperLogger.Write("Injecting into EAE project files\u2026");
                var result = await Task.Run(() => injector.Inject(cfg, toInject));

                if (!result.Success)
                {
                    MapperLogger.Error($"Injection failed: {result.ErrorMessage}");
                    MessageBox.Show($"Injection failed:\n\n{result.ErrorMessage}",
                        "Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                foreach (var fb in result.InjectedFBs) MapperLogger.Info($"{SymPass} {fb}");
                foreach (var u in result.UnsupportedComponents) MapperLogger.Warn(u);

                MapperLogger.Touch($"Touching {Path.GetFileName(dfbproj)}");
                File.SetLastWriteTime(dfbproj, DateTime.Now);
                MapperLogger.Info("Reload Solution in EAE to apply changes.");

                lblStatus.Text = $"Done. {cloned} type(s) cloned, {result.InjectedFBs.Count} instance(s) injected.";
                MapperLogger.Info("Generation complete.");

                var msg = new StringBuilder();
                msg.AppendLine($"Cloned {cloned} CAT type(s).");
                msg.AppendLine($"Injected {result.InjectedFBs.Count} instance(s) into syslay/sysres.");
                if (result.UnsupportedComponents.Any())
                    msg.AppendLine(
                        $"\n{result.UnsupportedComponents.Count} component(s) skipped (see Debug Console).");
                msg.AppendLine("\nSwitch to EAE and click Reload Solution.");
                MessageBox.Show(msg.ToString(), "Done: Reload EAE",
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

        // ─────────────────────────────────────────────────────────────────────
        // Generate Robot_Task_CAT Wrapper
        // ─────────────────────────────────────────────────────────────────────

        private async void btnGenerateRobotWrapper_Click(object sender, EventArgs e)
        {
            btnGenerateRobotWrapper.Enabled = false;
            MapperLogger.Info("──────────────────────────────────────────");
            MapperLogger.Info("Generate CAT wrapper — started.");

            try
            {
                var cfg = GetMapperConfig();

                var dfbprojPath = DeriveProjectFile(cfg.SyslayPath);
                if (dfbprojPath == null)
                {
                    MapperLogger.Error("Cannot locate IEC61499.dfbproj above syslay path.");
                    MessageBox.Show(
                        "Cannot locate IEC61499.dfbproj above the syslay path.\n\n" +
                        "Check mapper_config.json \u2192 SyslayPath.",
                        "Config Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                MapperLogger.Info($"Target project : {Path.GetFileName(dfbprojPath)}");
                MapperLogger.Info($"Robot template : {cfg.RobotTemplatePath}");

                var result = await Task.Run(
                    () => RobotTaskCatRegistrar.Register(cfg, dfbprojPath));

                MapperLogger.Info("Generate CAT wrapper — complete.");
                MapperLogger.Info(result);

                MessageBox.Show(result, "CAT Wrapper Generated",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);

                var dfbContent = await File.ReadAllTextAsync(dfbprojPath);
                LogCatCheck(dfbContent, "Robot_Task_CAT");
            }
            catch (Exception ex)
            {
                MapperLogger.Error($"CAT wrapper generation failed: {ex.Message}");
                MessageBox.Show($"CAT wrapper generation failed:\n\n{ex.Message}",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnGenerateRobotWrapper.Enabled = true;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Generate Single Pusher FB (legacy / test button)
        // ─────────────────────────────────────────────────────────────────────

        private async void btnGeneratePusherFB_Click(object sender, EventArgs e)
        {
            MapperLogger.Info("Generate Pusher FB — started.");
            try
            {
                var cfg = GetMapperConfig();
                var dfbprojPath = DeriveProjectFile(cfg.SyslayPath);
                if (dfbprojPath == null)
                {
                    MessageBox.Show("Cannot locate IEC61499.dfbproj.\nCheck mapper_config.json.",
                        "Config Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var templatePath = cfg.ActuatorTemplatePath;
                if (string.IsNullOrEmpty(templatePath) || !File.Exists(templatePath))
                {
                    MessageBox.Show("ActuatorTemplatePath not found.\nCheck mapper_config.json.",
                        "Config Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var templateBaseName = Path.GetFileNameWithoutExtension(templatePath);
                var newCatName = $"{templateBaseName}_Pusher";

                var result = await Task.Run(
                    () => CatTypeCloner.Clone(templatePath, newCatName, dfbprojPath, "Pusher"));

                MapperLogger.Info(result);
                MessageBox.Show(result, "Pusher FB Generated",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MapperLogger.Error($"Pusher FB generation failed: {ex.Message}");
                MessageBox.Show($"Failed:\n\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Component grid selection -> I/O detail panels
        // ─────────────────────────────────────────────────────────────────────

        private void dgvComponents_SelectionChanged(object sender, EventArgs e)
        {
            dgvInputs.Rows.Clear();
            dgvOutputs.Rows.Clear();
            if (dgvComponents.SelectedRows.Count == 0) return;

            var name = dgvComponents.SelectedRows[0].Cells[0].Value?.ToString();
            var comp = _loadedComponents.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (comp == null) return;

            foreach (var s in comp.States.OrderBy(st => st.StateNumber))
                dgvInputs.Rows.Add($"State {s.StateNumber}: {s.Name}", "");

            var vr = _validationRows.FirstOrDefault(r =>
                string.Equals(r.Component.Name, name, StringComparison.OrdinalIgnoreCase));
            if (vr != null && !vr.IsValid && !string.IsNullOrEmpty(vr.FailReason))
                dgvOutputs.Rows.Add(vr.FailReason, "");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

        private void UpdateDetectedInfo()
        {
            if (_loadedComponents.Count == 0) return;

            int a = _loadedComponents.Count(c => TypeIs(c, "Actuator"));
            int s = _loadedComponents.Count(c => TypeIs(c, "Sensor"));
            int p = _loadedComponents.Count(c => TypeIs(c, "Process"));
            int r = _loadedComponents.Count(c => TypeIs(c, "Robot"));

            lblDetectedType.Text = _loadedComponents.Count == 1
                ? _loadedComponents[0].Type : "System";
            lblDetectedName.Text = _loadedComponents.Count == 1
                ? _loadedComponents[0].Name : (_lastReader?.SystemName ?? "-");
            lblDetectedStates.Text = _loadedComponents.Count == 1
                ? $"{_loadedComponents[0].States.Count} states"
                : $"{a} actuators, {s} sensors, {p} processes, {r} robots";
        }

        private static bool TypeIs(VueOneComponent c, string t) =>
            string.Equals(c.Type, t, StringComparison.OrdinalIgnoreCase);

        private void SetValidationLabel(string text, Color color)
        {
            lblValidationStatus.Text = text;
            lblValidationStatus.ForeColor = color;
        }

        private MapperConfig GetMapperConfig()
        {
            _mapperConfig ??= MapperConfig.Load();
            return _mapperConfig;
        }

        /// <summary>
        /// Walks up from the given path until IEC61499.dfbproj is found.
        /// </summary>
        private static string? DeriveProjectFile(string startPath)
        {
            var dir = Directory.Exists(startPath)
                ? startPath
                : Path.GetDirectoryName(startPath);

            while (dir != null)
            {
                var candidate = Directory.GetFiles(dir, "*.dfbproj", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();
                if (candidate != null) return candidate;
                dir = Directory.GetParent(dir)?.FullName;
            }
            return null;
        }

        private static void LogCatCheck(string dfbContent, string catName)
        {
            bool found = dfbContent.Contains(catName);
            if (found)
                MapperLogger.Info($"  {SymPass} {catName} registered in .dfbproj");
            else
                MapperLogger.Warn($"  {SymFail} {catName} NOT found in .dfbproj");
        }

        private static void LogDiff(SystemInjector.DiffReport diff)
        {
            MapperLogger.Info($"Diff: {diff.ToBeInjected.Count} to inject, " +
                              $"{diff.AlreadyPresent.Count} already present.");
            foreach (var n in diff.ToBeInjected) MapperLogger.Info($"  + {n}");
            foreach (var n in diff.AlreadyPresent) MapperLogger.Info($"  = {n} (already exists)");
        }
    }
}