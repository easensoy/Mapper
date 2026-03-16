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
        MapperConfig? _mapperConfig;
        List<VueOneComponent> _loadedComponents = new();
        List<ComponentValidationRow> _validationRows = new();
        SystemXmlReader? _lastReader;
        DebugConsoleForm? _debugConsole;

        static readonly HashSet<string> _allowedInstances = new(StringComparer.OrdinalIgnoreCase)
        {
            "Checker", "Transfer", "Feeder", "Ejector",
            "PartInHopper", "PartAtChecker"
        };

        sealed class ComponentValidationRow
        {
            public VueOneComponent Component { get; init; } = null!;
            public string TemplateName { get; init; } = string.Empty;
            public bool IsValid { get; init; }
            public string FailReason { get; init; } = string.Empty;
        }

        static readonly Color ColorTranslated = Color.FromArgb(56, 142, 60);
        static readonly Color ColorDiscarded = Color.FromArgb(204, 72, 0);
        static readonly Color ColorAssumed = Color.FromArgb(180, 130, 0);
        static readonly Color ColorEncoded = Color.FromArgb(31, 97, 180);
        static readonly Color ColorHardcoded = Color.FromArgb(110, 110, 110);
        static readonly Color ColorSection = Color.FromArgb(220, 230, 242);
        static readonly Color RowEven = Color.White;
        static readonly Color RowOdd = Color.FromArgb(245, 245, 245);
        const string SymPass = "\u2713";
        const string SymFail = "\u2717";

        public MainForm()
        {
            InitializeComponent();
            btnGenerateCode.Enabled = false;
        }

        void menuItemDebugConsole_Click(object sender, EventArgs e)
        {
            if (_debugConsole == null || _debugConsole.IsDisposed)
            {
                _debugConsole = new DebugConsoleForm();
                _debugConsole.PositionBelow(this);
            }
            _debugConsole.Show();
            _debugConsole.BringToFront();
        }

        async void btnBrowse_Click(object sender, EventArgs e)
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

        async Task LoadAndValidateAsync(string path)
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
                    MessageBox.Show("No components found.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    lblStatus.Text = "No components found";
                    return;
                }

                var validator = new ComponentValidator();
                var cfg = Cfg();
                int rowIdx = 0;

                foreach (var rule in RuleEngine.GetAllRules()) AddMappingRuleRow(rule);

                foreach (var comp in _loadedComponents)
                {
                    var vr = Validate(comp, validator, cfg);
                    _validationRows.Add(vr);

                    int idx = dgvComponents.Rows.Add(comp.Name, comp.Type, vr.TemplateName);
                    var row = dgvComponents.Rows[idx];
                    Color bg = (rowIdx++ % 2 == 0) ? RowEven : RowOdd;
                    row.DefaultCellStyle.BackColor = bg;
                    row.DefaultCellStyle.ForeColor = Color.Black;
                    row.Cells[2].Style.ForeColor = vr.IsValid ? ColorTranslated : ColorDiscarded;
                    row.Cells[2].Style.BackColor = bg;
                }

                UpdateDetectedInfo();

                bool ok = _validationRows
                    .Where(r => _allowedInstances.Contains(r.Component.Name))
                    .All(r => r.IsValid);

                SetValidationLabel(ok ? "PASSED" : "FAILED", ok ? Color.Green : Color.Red);
                lblStatus.Text = ok ? "Validation passed." : "Validation failed.";
                btnGenerateCode.Enabled = _validationRows.Any(r => r.IsValid && _allowedInstances.Contains(r.Component.Name));

                GenerateImportInstructions();
            }
            catch (Exception ex)
            {
                MapperLogger.Error(ex.Message);
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                lblStatus.Text = "Error";
            }
        }

        void GenerateImportInstructions()
        {
            try
            {
                var cfg = Cfg();
                if (string.IsNullOrWhiteSpace(cfg.TemplateLibraryPath) ||
                    !Directory.Exists(cfg.TemplateLibraryPath))
                {
                    MapperLogger.Warn("[ImportInstructions] TemplateLibraryPath not set or not found. Skipping.");
                    return;
                }

                var instructions = ImportInstructionGenerator.Generate(cfg, _loadedComponents);

                var summary = new StringBuilder();
                summary.AppendLine("Manual Import Instructions generated.");
                summary.AppendLine();
                summary.AppendLine($"Components detected: {instructions.ComponentMappings.Count}");
                summary.AppendLine($"Types to import: {instructions.ImportSteps.Count}");
                summary.AppendLine();
                summary.AppendLine("Import order:");
                foreach (var step in instructions.ImportSteps)
                    summary.AppendLine($"  Step {step.StepNumber}: {step.FileName}");
                summary.AppendLine();
                summary.AppendLine($"Output: {instructions.OutputDirectory}");
                summary.AppendLine($"Full instructions: IMPORT_ORDER.txt");

                if (instructions.Warnings.Any())
                {
                    summary.AppendLine();
                    foreach (var w in instructions.Warnings)
                        summary.AppendLine($"Warning: {w}");
                }

                MessageBox.Show(summary.ToString(),
                    "Import Instructions Ready",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);

                System.Diagnostics.Process.Start("explorer.exe", instructions.OutputDirectory);
            }
            catch (Exception ex)
            {
                MapperLogger.Warn($"[ImportInstructions] {ex.Message}");
            }
        }

        void btnMappingRules_Click(object sender, EventArgs e)
        {
            dgvMappingRules.Rows.Clear();
            foreach (var rule in RuleEngine.GetAllRules()) AddMappingRuleRow(rule);
        }

        void AddMappingRuleRow(MappingRule rule)
        {
            int idx = dgvMappingRules.Rows.Add(
                rule.IsSection ? rule.SectionTitle : rule.VueOneElement,
                rule.IsSection ? "" : rule.IEC61499Element,
                rule.IsSection ? "" : rule.Type.ToString(),
                rule.IsSection ? "" : rule.TransformationRule,
                rule.IsSection ? "" : (rule.IsImplemented ? SymPass : SymFail));

            var row = dgvMappingRules.Rows[idx];

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
                row.Cells[colMappingType.Index].Style.ForeColor = rule.Type switch
                {
                    UiMappingType.TRANSLATED => ColorTranslated,
                    UiMappingType.DISCARDED => ColorDiscarded,
                    UiMappingType.ASSUMED => ColorAssumed,
                    UiMappingType.ENCODED => ColorEncoded,
                    UiMappingType.HARDCODED => ColorHardcoded,
                    _ => Color.Black
                };
            }

            row.Cells[colMappingValidated.Index].Style.ForeColor =
                rule.IsImplemented ? ColorTranslated : ColorDiscarded;
        }

        static ComponentValidationRow Validate(VueOneComponent comp, ComponentValidator validator, MapperConfig cfg)
        {
            string tPath = TemplatePath(comp, cfg);
            string tName = string.IsNullOrEmpty(tPath) ? "No template found (discarded for this phase)" : Path.GetFileName(tPath);

            switch (comp.Type.ToLowerInvariant())
            {
                case "process": return Pass(comp, tName);
                case "robot":
                    return string.IsNullOrWhiteSpace(cfg.RobotTemplatePath)
                        ? Fail(comp, tName, "RobotTemplatePath not set") : Pass(comp, tName);
                case "actuator":
                    if (comp.States.Count != 5) return Fail(comp, "No template found (discarded for this phase)", $"{comp.States.Count} states, not 5");
                    break;
                case "sensor":
                    if (comp.States.Count != 2) return Fail(comp, "No template found (discarded for this phase)", $"{comp.States.Count} states, not 2");
                    break;
                default: return Fail(comp, tName, $"Unknown type '{comp.Type}'");
            }

            var vr = validator.Validate(comp);
            return vr.IsValid ? Pass(comp, tName) : Fail(comp, tName, string.Join("; ", vr.Errors));
        }

        static string TemplatePath(VueOneComponent comp, MapperConfig cfg) => comp.Type.ToLowerInvariant() switch
        {
            "actuator" => cfg.ActuatorTemplatePath,
            "sensor" => cfg.SensorTemplatePath,
            "process" => cfg.ProcessCATTemplatePath,
            "robot" => cfg.RobotTemplatePath,
            _ => string.Empty
        };

        static ComponentValidationRow Pass(VueOneComponent c, string t) => new() { Component = c, TemplateName = t, IsValid = true };
        static ComponentValidationRow Fail(VueOneComponent c, string t, string r) => new() { Component = c, TemplateName = t, IsValid = false, FailReason = r };

        async void btnGenerateCode_Click(object sender, EventArgs e)
        {
            var toInject = _validationRows
                .Where(r => r.IsValid && _allowedInstances.Contains(r.Component.Name))
                .Select(r => r.Component).ToList();

            if (toInject.Count == 0)
            {
                MessageBox.Show("No in-scope components to inject.", "Nothing to Inject", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            btnGenerateCode.Enabled = false;
            try
            {
                var cfg = Cfg();
                var dfbproj = FindDfbproj(cfg.ActiveSyslayPath);
                if (dfbproj == null) { ShowError("Cannot find .dfbproj."); return; }
                if (!File.Exists(cfg.ActiveSyslayPath)) { ShowError($"syslay not found:\n{cfg.ActiveSyslayPath}"); return; }

                MapperLogger.Info($"Project: {Path.GetFileName(dfbproj)}");

                var injCfg = MapperConfig.Load();
                injCfg.SyslayPath = cfg.ActiveSyslayPath;
                injCfg.SysresPath = cfg.ActiveSysresPath;

                var injector = new SystemInjector();
                var result = await Task.Run(() => injector.Inject(injCfg, toInject));

                if (!result.Success) { ShowError($"Injection failed:\n{result.ErrorMessage}"); return; }

                File.SetLastWriteTime(dfbproj, DateTime.Now);
                lblStatus.Text = $"Done. {result.InjectedFBs.Count} instance(s) injected.";
                MessageBox.Show($"Injected {result.InjectedFBs.Count} instance(s).\nReload Solution in EAE.",
                    "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally { btnGenerateCode.Enabled = true; }
        }

        void dgvComponents_SelectionChanged(object sender, EventArgs e)
        {
            dgvInputs.Rows.Clear();
            dgvOutputs.Rows.Clear();
            if (dgvComponents.SelectedRows.Count == 0) return;

            var name = dgvComponents.SelectedRows[0].Cells[0].Value?.ToString();
            var comp = _loadedComponents.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (comp == null) return;

            foreach (var s in comp.States.OrderBy(st => st.StateNumber))
                dgvInputs.Rows.Add($"State {s.StateNumber}: {s.Name}", "");

            var vr = _validationRows.FirstOrDefault(r => string.Equals(r.Component.Name, name, StringComparison.OrdinalIgnoreCase));
            if (vr is { IsValid: false, FailReason.Length: > 0 })
                dgvOutputs.Rows.Add(vr.FailReason, "");
        }

        void UpdateDetectedInfo()
        {
            if (_loadedComponents.Count == 0) return;
            int a = _loadedComponents.Count(c => c.Type == "Actuator");
            int s = _loadedComponents.Count(c => c.Type == "Sensor");
            int p = _loadedComponents.Count(c => c.Type == "Process");
            int r = _loadedComponents.Count(c => c.Type == "Robot");

            lblDetectedType.Text = _loadedComponents.Count == 1 ? _loadedComponents[0].Type : "System";
            lblDetectedName.Text = _loadedComponents.Count == 1 ? _loadedComponents[0].Name : (_lastReader?.SystemName ?? "-");
            lblDetectedStates.Text = _loadedComponents.Count == 1
                ? $"{_loadedComponents[0].States.Count} states"
                : $"{a} actuators, {s} sensors, {p} processes, {r} robots";
        }

        void SetValidationLabel(string text, Color color)
        {
            lblValidationStatus.Text = text;
            lblValidationStatus.ForeColor = color;
        }

        MapperConfig Cfg() => _mapperConfig ??= MapperConfig.Load();

        static string? FindDfbproj(string startPath)
        {
            var dir = Directory.Exists(startPath) ? startPath : Path.GetDirectoryName(startPath);
            while (dir != null)
            {
                var f = Directory.GetFiles(dir, "*.dfbproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (f != null) return f;
                dir = Directory.GetParent(dir)?.FullName;
            }
            return null;
        }

        void btnGenerateRobotWrapper_Click(object sender, EventArgs e) { }

        void btnGeneratePusherFB_Click(object sender, EventArgs e) { }

        static void ShowError(string msg) => MessageBox.Show(msg, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}