using CodeGen.Application;
using CodeGen.Configuration;
using CodeGen.IO;
using CodeGen.Models;
using CodeGen.Validation;
using CodeGen.Mapping;
using CodeGen.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

using UiMappingType = CodeGen.Models.MappingType;

namespace MapperUI
{
    public partial class MainForm : Form
    {
        MapperConfig? _mapperConfig;
        List<VueOneComponent> _loadedComponents = new();
        List<ComponentValidationRow> _validationRows = new();
        // In-session Device-column overrides. NOT display-only: generation reads these and turns a RevPi
        // pick into the run's DeploymentProfile, which relocates the component. Not persisted.
        readonly Dictionary<string, string> _deviceOverrides = new(StringComparer.OrdinalIgnoreCase);
        // True while the grid is (re)populating, so CellValueChanged ignores programmatic writes.
        bool _populatingGrid;
        SystemXmlReader? _lastReader;
        DebugConsoleForm? _debugConsole;
        StateTransitionTableForm? _stateTransitionTableForm;
        Process? _llmProcess;
        System.Windows.Forms.Timer? _healthTimer;
        readonly string? _startupControlXmlPath;
        readonly bool _startupShowMappingRules;

        static readonly HttpClient _http = new()
        {
            BaseAddress = new Uri("http://127.0.0.1:8100/"),
            Timeout = TimeSpan.FromMinutes(10),
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

        public MainForm() : this(Array.Empty<string>())
        {
        }

        public MainForm(string[] args)
        {
            ParseStartupArgs(args, out _startupControlXmlPath, out _startupShowMappingRules);
            InitializeComponent();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            StartLlmEngine();
            StartHealthPolling();
            ValidatePortNamesOnStartup();
            LogInputFolderContents();
            lblStatus.Text = "Ready";

            if (!string.IsNullOrWhiteSpace(_startupControlXmlPath))
                BeginInvoke(new Action(async () => await LoadStartupControlXmlAsync()));
        }

        static void ParseStartupArgs(string[] args, out string? controlXmlPath, out bool showMappingRules)
        {
            controlXmlPath = null;
            showMappingRules = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.Equals("--control", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    controlXmlPath = args[++i];
                }
                else if (arg.Equals("--show-mapping-rules", StringComparison.OrdinalIgnoreCase))
                {
                    showMappingRules = true;
                }
                else if (controlXmlPath == null && arg.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    controlXmlPath = arg;
                }
            }
        }

        async Task LoadStartupControlXmlAsync()
        {
            try
            {
                await LoadControlXmlFromPathAsync(_startupControlXmlPath!);
                if (_startupShowMappingRules)
                    btnMappingRules_Click(this, EventArgs.Empty);
                AppendActivity("[VueOne] MapperUI opened from VueOne Generate IEC61499 Code.");
            }
            catch (Exception ex)
            {
                ShowError("Could not load startup Control.xml:\n" + ex.Message);
            }
        }

        void ValidatePortNamesOnStartup()
        {
            try
            {
                var libRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Template Library");
                libRoot = Path.GetFullPath(libRoot);
                var mismatches = CodeGen.Translation.PortNameValidator.Validate(libRoot);
                if (mismatches.Count == 0)
                {
                    AppendActivity($"[Startup] Port name validation passed against {libRoot}");
                }
                else
                {
                    AppendActivity($"[Startup] Port name validation found {mismatches.Count} issue(s):");
                    foreach (var m in mismatches)
                        AppendActivity($"  {m.FbType}: {m.Reason}");
                }
            }
            catch (Exception ex)
            {
                AppendActivity($"[Startup] Port name validation skipped: {ex.Message}");
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            _healthTimer?.Stop();
            try { _llmProcess?.Kill(); } catch { }
        }


        void StartLlmEngine()
        {
            var runBat = FindRunBat();
            if (runBat == null)
            {
                AppendActivity("LLMEngine/run.bat not found — start the service manually.");
                return;
            }

            try
            {
                _llmProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = runBat,
                        WorkingDirectory = Path.GetDirectoryName(runBat)!,
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Minimized,
                    }
                };
                _llmProcess.Start();
                AppendActivity("LLM Engine process started.");
            }
            catch (Exception ex)
            {
                AppendActivity($"Could not start LLM Engine: {ex.Message}");
            }
        }

        static string? FindRunBat()
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 7; i++)
            {
                var candidate = Path.Combine(dir, "LLMEngine", "run.bat");
                if (File.Exists(candidate)) return candidate;
                var parent = Path.GetDirectoryName(dir);
                if (parent == null) break;
                dir = parent;
            }
            return null;
        }


        void StartHealthPolling()
        {
            _healthTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _healthTimer.Tick += async (_, _) => await CheckHealthAsync();
            _healthTimer.Start();
        }

        async Task CheckHealthAsync()
        {
            bool ok;
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(1.5));
                var resp = await _http.GetAsync("health", cts.Token);
                ok = resp.IsSuccessStatusCode;
            }
            catch { ok = false; }

            if (InvokeRequired) Invoke(() => SetEngineStatus(ok));
            else SetEngineStatus(ok);
        }

        void SetEngineStatus(bool running)
        {
            lblEngineStatusDot.ForeColor = running ? Color.LimeGreen : Color.Red;
        }


        void AppendActivity(string text)
        {
            if (txtActivityLog.InvokeRequired)
            {
                txtActivityLog.Invoke(() => AppendActivity(text));
                return;
            }
            txtActivityLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
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

        void menuItemStateTransitionTable_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_loadedControlXmlPath) || !File.Exists(_loadedControlXmlPath))
            {
                ShowError("Load a Control.xml first via Browse.");
                return;
            }

            if (_loadedComponents.Count == 0)
            {
                ShowError("The selected Control.xml has not finished loading yet.");
                return;
            }

            try
            {
                if (_stateTransitionTableForm == null || _stateTransitionTableForm.IsDisposed)
                    _stateTransitionTableForm = new StateTransitionTableForm(
                        _loadedControlXmlPath, _loadedComponents);
                else
                    _stateTransitionTableForm.Reload(_loadedControlXmlPath, _loadedComponents);

                _stateTransitionTableForm.Show(this);
                _stateTransitionTableForm.BringToFront();
            }
            catch (Exception ex)
            {
                var fnf = ex as System.IO.FileNotFoundException
                          ?? ex.InnerException as System.IO.FileNotFoundException;
                ShowError(
                    $"State-Transition Table failed: {ex.GetType().Name}: {ex.Message}" +
                    (fnf?.FileName != null ? $"{Environment.NewLine}Missing file: {fnf.FileName}" : string.Empty));
            }
        }

        string? _loadedControlXmlPath;

        // Guards btnTestStation1_Click against re-entry while a generation is in flight.
        bool _generating;

        async void btnBrowse_Click(object sender, EventArgs e)
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "XML Files (*.xml)|*.xml|All Files (*.*)|*.*",
                Title = "Open VueOne Control.xml"
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            await LoadControlXmlFromPathAsync(dlg.FileName);
        }

        async Task LoadControlXmlFromPathAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Control.xml path is empty.", nameof(path));
            if (!File.Exists(path))
                throw new FileNotFoundException("Control.xml not found.", path);

            txtModelPath.Text = path;
            _loadedControlXmlPath = path;
            btnTestStation1.Enabled = true;
            await LoadAndValidateAsync(path);
            menuItemStateTransitionTable.Enabled = _loadedComponents.Count > 0;
        }

        bool TryResolveDemonstratorPath(out string syslayPath)
        {
            var cfg = Cfg();
            syslayPath = cfg.SyslayPath2 ?? string.Empty;
            if (string.IsNullOrEmpty(syslayPath))
            {
                AppendActivity("[Error] Demonstrator paths not configured in mapper_config.json; cannot generate.");
                ShowError("Demonstrator paths not configured in mapper_config.json; cannot generate.");
                return false;
            }
            if (!File.Exists(syslayPath))
            {
                AppendActivity($"[Error] Demonstrator syslay missing: {syslayPath}");
                ShowError($"Demonstrator syslay missing: {syslayPath}");
                return false;
            }
            return true;
        }

        void LogInputFolderContents()
        {
            try
            {
                var inputDir = Path.Combine(AppContext.BaseDirectory, "Input");
                if (!Directory.Exists(inputDir))
                {
                    AppendActivity($"[Startup] Input folder not found at {inputDir}");
                    return;
                }
                var files = Directory.GetFiles(inputDir);
                AppendActivity($"[Startup] Input folder ({inputDir}):");
                foreach (var f in files)
                {
                    var name = Path.GetFileName(f);
                    string status = name.ToLowerInvariant() switch
                    {
                        "vueone_iec61499_mapping.xlsx" => "consumed (mapping rules)",
                        "smc_rig_io_bindings.xlsx" => "consumed (IO bindings)",
                        "appendix_a_iotab_newstop.docx" => "ignored (reference only)",
                        _ => "ignored (unrecognised)"
                    };
                    AppendActivity($"  - {name}: {status}");
                }
            }
            catch (Exception ex)
            {
                AppendActivity($"[Startup] Failed to enumerate Input folder: {ex.Message}");
            }
        }

        // UI only: collect the inputs, hand them to the one generation path, show the result.
        async void btnTestStation1_Click(object sender, EventArgs e)
        {
            // One generation at a time: two runs would race on the deployed tree they both write.
            if (_generating) return;

            try
            {
                if (string.IsNullOrEmpty(_loadedControlXmlPath) || !File.Exists(_loadedControlXmlPath))
                { ShowError("Load a Control.xml first via Browse."); return; }
                if (!TryResolveDemonstratorPath(out var syslayPath)) return;

                _generating = true;
                btnTestStation1.Enabled = false;

                var revpiComponents = CollectRevPiSelection();
                LogControllerChoice(revpiComponents);

                lblStatus.Text = "Generating...";
                AppendActivity($"[Generate] Generating IEC 61499 code end-to-end (Feed · Assembly · Disassembly · covers) into Demonstrator at {syslayPath}...");
                AppendActivity("[Test Runtime] RecipeStep data-array carrier active; physical IO/sensor wiring and rig HOME-FIRST recipe waits are active.");

                var request = new GenerationRequest(_loadedControlXmlPath, Cfg(), revpiComponents);
                var result = await Task.Run(() => GenerateProject.Execute(request, AppendActivity));

                lblStatus.Text = $"Ready  |  {result.BoundCount} I/O bound  |  {result.SyslayPath}";
                MessageBox.Show(
                    "IEC 61499 code generated for the SMC rig — end to end.\n\n" +
                    "Feed_Station (M262)  →  Assembly / Disassembly (M580)  →  Covers (BX1)\n\n" +
                    "Process recipes, interlock safety tables and I/O bindings were emitted across\n" +
                    $"all three controllers ({result.BoundCount} I/O channel(s) bound).\n\n" +
                    $"Demonstrator:\n{result.SyslayPath}\n\n" +
                    "Next: reload the solution in EAE, then Build and Deploy.",
                    "Generate IEC61499 Code", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendActivity($"[Error] {ex}");
                lblStatus.Text = "Ready";
                ShowError(ex.Message);
            }
            finally
            {
                _generating = false;
                btnTestStation1.Enabled = true;
            }
        }

        // Every Feed component DEFAULTS to M262. The swappable set is read from the injector's own signal
        // tables so it cannot drift from what PLC_RW_REVPI physically wires.
        IReadOnlySet<string> CollectRevPiSelection()
        {
            var picked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _deviceOverrides)
            {
                if (!string.Equals(kv.Value, "RevPi", StringComparison.OrdinalIgnoreCase)) continue;
                if (CodeGen.Devices.RevPi.RevPiIoBrokerInjector.CoveredComponents.Contains(kv.Key))
                    picked.Add(kv.Key);
                else
                    AppendActivity($"[Target][!] '{kv.Key}' cannot move to the RevPi — the Modbus coupler (PLC_RW_REVPI) exposes no IO for it; kept on M262.");
            }
            return picked;
        }

        // Logged on EVERY run: a pure-M262 run must be distinguishable from one whose RevPi picks were rejected.
        void LogControllerChoice(IReadOnlySet<string> revpiComponents)
        {
            if (revpiComponents.Count == 0)
            {
                AppendActivity("[Target] Feed controller: M262 (no components routed to the RevPi).");
                return;
            }
            // Declared in device.yml, so the operator is told which components the selection dragged along.
            var alwaysHosted = CodeGen.Configuration.DeviceConfig.Current.RevPi.AlwaysHosts;
            var picked = revpiComponents.Where(c => !alwaysHosted.Contains(c, StringComparer.OrdinalIgnoreCase));
            AppendActivity($"[Target] Feed controller: M262 + Revolution Pi (Soft_dPAC) — {string.Join(", ", picked)} + {string.Join(", ", alwaysHosted)} on the RevPi; M262 keeps the rest (4 controllers).");
            AppendActivity($"[Target] RevPi endpoints: host {Cfg().RevPiHostIp} (Soft dPAC Manager :8080, EAE 'Manage Soft dPAC') / container {Cfg().RevPiTargetIp} (IEC 61499 runtime, EAE Deploy+Login target).");
        }

        async Task LoadAndValidateAsync(string path)
        {
            dgvComponents.Rows.Clear();
            dgvMappingRules.Rows.Clear();
            _loadedComponents.Clear();
            _validationRows.Clear();
            txtActivityLog.Clear();
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

                try
                {
                    bool hasActuator5 = _loadedComponents.Any(c => c.Type == "Actuator" && c.States.Count == 5);
                    bool hasActuator7 = _loadedComponents.Any(c => c.Type == "Actuator" && c.States.Count == 7);
                    bool hasSensor = _loadedComponents.Any(c => c.Type == "Sensor" && c.States.Count == 2);

                    foreach (var rule in MappingRuleEngine.GetRelevantRules(
                        Cfg().MappingRulesPath, hasActuator5, hasActuator7, hasSensor))
                        AddMappingRuleRow(rule);
                }
                catch (Exception ex)
                {
                    var detail = string.IsNullOrWhiteSpace(ex.Message)
                        ? $"{ex.GetType().FullName} (no message)\n{ex.StackTrace}"
                        : $"{ex.GetType().Name}: {ex.Message}";
                    MapperLogger.Error(detail);
                    MessageBox.Show(detail, "Mapping Rules", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                var validator = new ComponentValidator();
                var cfg = Cfg();
                int rowIdx = 0;

                _populatingGrid = true;
                try
                {
                var roster = new CodeGen.Mapping.DeploymentRoster(
                    new CodeGen.Mapping.DeploymentProfile(
                        _deviceOverrides.Where(kv => kv.Value == "RevPi").Select(kv => kv.Key),
                        CodeGen.Configuration.LayoutCatalog.Load()));
                foreach (var comp in _loadedComponents)
                {
                    var vr = Validate(comp, validator, cfg);
                    _validationRows.Add(vr);

                    var reg = roster.Get(comp.Name);
                    string dev = _deviceOverrides.TryGetValue(comp.Name, out var ov)
                        ? ov
                        : (reg?.Plc.ToString() ?? "");
                    // An unmapped/unknown device is stored as null (blank) to avoid a DataError.
                    string? devCell = (dev == "M262" || dev == "M580" || dev == "BX1" || dev == "RevPi") ? dev : null;
                    int idx = dgvComponents.Rows.Add(comp.Name, comp.Type, vr.TemplateName, devCell!);
                    var row = dgvComponents.Rows[idx];
                    Color bg = (rowIdx++ % 2 == 0) ? RowEven : RowOdd;
                    row.DefaultCellStyle.BackColor = bg;
                    row.DefaultCellStyle.ForeColor = Color.Black;
                    row.Cells[2].Style.ForeColor = vr.IsValid ? ColorTranslated : ColorDiscarded;
                    row.Cells[2].Style.BackColor = bg;
                }
                }
                finally { _populatingGrid = false; }

                RefreshDeviceSummary();

                UpdateDetectedInfo();

                bool ok = _validationRows.All(r => r.IsValid);

                SetValidationLabel(ok ? "PASSED" : "FAILED", ok ? Color.Green : Color.Red);
                lblStatus.Text = ok ? "Validation passed." : "Validation failed.";

                var noTemplate = _validationRows
                    .Where(r => r.TemplateName.StartsWith("No template found"))
                    .ToList();

                if (noTemplate.Count > 0)
                {
                    AppendActivity(
                        $"{noTemplate.Count} component(s) have no template and can be generated by the LLM Engine: " +
                        string.Join(", ", noTemplate.Select(r => r.Component.Name)));
                }
            }
            catch (Exception ex)
            {
                MapperLogger.Error(ex.Message);
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                lblStatus.Text = "Error";
            }
        }

        void btnMappingRules_Click(object sender, EventArgs e)
        {
            dgvMappingRules.Rows.Clear();
            try
            {
                IEnumerable<MappingRuleEntry> rules;
                if (_loadedComponents.Count > 0)
                {
                    bool hasActuator5 = _loadedComponents.Any(c => c.Type == "Actuator" && c.States.Count == 5);
                    bool hasActuator7 = _loadedComponents.Any(c => c.Type == "Actuator" && c.States.Count == 7);
                    bool hasSensor = _loadedComponents.Any(c => c.Type == "Sensor" && c.States.Count == 2);
                    rules = MappingRuleEngine.GetRelevantRules(
                        Cfg().MappingRulesPath, hasActuator5, hasActuator7, hasSensor);
                }
                else
                {
                    rules = MappingRuleEngine.GetAllRules(Cfg().MappingRulesPath);
                }

                foreach (var rule in rules)
                    AddMappingRuleRow(rule);
            }
            catch (Exception ex)
            {
                var detail = string.IsNullOrWhiteSpace(ex.Message)
                    ? $"{ex.GetType().FullName} (no message)\n{ex.StackTrace}"
                    : $"{ex.GetType().Name}: {ex.Message}";
                MessageBox.Show(detail, "Mapping Rules", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void AddMappingRuleRow(MappingRuleEntry rule)
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
            switch (comp.Type.ToLowerInvariant())
            {
                case "process": return Pass(comp, ProcessCatFile);
                case "robot":
                    // Type=Robot is a category: the task arm gets Robot_Task_CAT, grippers route as usual.
                    if (TemplateMap.IsRobotTaskArm(comp))
                        return Pass(comp, "Robot_Task_CAT.fbt");
                    if (comp.States.Count != 5)
                        return Fail(comp, "No template found",
                            $"Robot '{comp.Name}' has {comp.States.Count} states — expected 5 (gripper) or 7 (task arm)");
                    return Pass(comp, ResolvedCatFile(comp));
                case "actuator":
                    if (comp.States.Count is not (4 or 5 or 7)
                        && !TemplateMap.IsBranchedSevenState(comp))
                        return Fail(comp, "No template found",
                            $"{comp.States.Count} states — only 4, 5, or 7 (incl. PARALLEL+ALTERNATIVE branched) supported");
                    return Pass(comp, ResolvedCatFile(comp));
                case "sensor":
                    if (comp.States.Count != 2)
                        return Fail(comp, "No template found",
                            $"{comp.States.Count} states, not 2");
                    break;
                default:
                    return Fail(comp, "No template found", $"Unknown type '{comp.Type}'");
            }

            var vr = validator.Validate(comp);
            return vr.IsValid
                ? Pass(comp, SensorCatFile)
                : Fail(comp, SensorCatFile, string.Join("; ", vr.Errors));
        }

        // The types the deployer actually emits, so the grid names the CAT that is generated.
        const string ProcessCatFile = "Process1_Generic.fbt";
        const string SensorCatFile = "Sensor_Bool_CAT.fbt";

        // TemplateMap owns the routing, so the displayed CAT can never drift from the emitted one.
        static string ResolvedCatFile(VueOneComponent c) =>
            TemplateMap.ResolveActuatorCatType(
                c.Name, c.States.Count, TemplateMap.IsBranchedSevenState(c)) + ".fbt";

        static ComponentValidationRow Pass(VueOneComponent c, string t) =>
            new() { Component = c, TemplateName = t, IsValid = true };

        static ComponentValidationRow Fail(VueOneComponent c, string t, string r) =>
            new() { Component = c, TemplateName = t, IsValid = false, FailReason = r };




        // Commit a Device combo selection on pick so CellValueChanged fires without leaving the cell.
        void dgvComponents_CurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            if (dgvComponents.IsCurrentCellDirty &&
                dgvComponents.CurrentCell is DataGridViewComboBoxCell)
                dgvComponents.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }

        // A blank/unlisted combo value would raise a formatting error; swallow it.
        void dgvComponents_DataError(object sender, DataGridViewDataErrorEventArgs e) => e.ThrowException = false;

        void dgvComponents_DeviceChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_populatingGrid || e.RowIndex < 0 || e.ColumnIndex != colDevice.Index) return;
            var row = dgvComponents.Rows[e.RowIndex];
            string comp = row.Cells[0].Value?.ToString() ?? "";
            string dev = row.Cells[colDevice.Index].Value?.ToString() ?? "";
            if (string.IsNullOrEmpty(comp)) return;
            _deviceOverrides[comp] = dev;
            RefreshDeviceSummary();
            bool swappable = string.Equals(dev, "RevPi", StringComparison.OrdinalIgnoreCase) &&
                             CodeGen.Devices.RevPi.RevPiIoBrokerInjector.CoveredComponents.Contains(comp);
            AppendActivity($"[UI] Device set: {comp} -> {dev}" +
                (string.Equals(dev, "RevPi", StringComparison.OrdinalIgnoreCase)
                    ? swappable
                        ? " (applied at Generate: this component moves to the Revolution Pi)."
                        : " — IGNORED at Generate: the RevPi Modbus coupler carries no IO for this component; only Feeder/Checker are swappable."
                    : "."));
        }

        void RefreshDeviceSummary()
        {
            int cM262 = 0, cM580 = 0, cBx1 = 0, cRevPi = 0;
            foreach (DataGridViewRow r in dgvComponents.Rows)
            {
                switch (r.Cells[colDevice.Index].Value?.ToString())
                {
                    case "M262":  cM262++;  break;
                    case "M580":  cM580++;  break;
                    case "BX1":   cBx1++;   break;
                    case "RevPi": cRevPi++; break;
                }
            }
            grpMappingInfo.Text =
                $"Mapping Information   —   M262: {cM262} · M580: {cM580} · BX1: {cBx1}"
                + (cRevPi > 0 ? $" · RevPi: {cRevPi}" : "")
                + " mapped component(s)";
        }

        // Designer anchors don't stretch the body to full width on some DPI/AutoScale configs, so size it here.
        protected override void OnClientSizeChanged(EventArgs e)
        {
            base.OnClientSizeChanged(e);
            RelayoutBody();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RelayoutBody();
        }

        void RelayoutBody()
        {
            if (grpValidation == null || grpMappingInfo == null) return;
            const int margin = 12;
            int fullW = ClientSize.Width - 2 * margin;
            if (fullW < 200) return;

            if (btnTestStation1 != null)
                btnTestStation1.Left = ClientSize.Width - margin - btnTestStation1.Width;

            grpValidation.Width = fullW;
            grpMappingInfo.Width = fullW;
            int statusH = statusStrip?.Height ?? 22;
            grpMappingInfo.Height = Math.Max(160, ClientSize.Height - grpMappingInfo.Top - statusH - margin);

            if (splitMain != null && splitMain.Width > 40)
            {
                int min1 = Math.Max(1, splitMain.Panel1MinSize);
                int max1 = splitMain.Width - splitMain.SplitterWidth - Math.Max(1, splitMain.Panel2MinSize);
                if (max1 > min1)
                {
                    int target = Math.Max(min1, Math.Min((int)(splitMain.Width * 0.58), max1));
                    try { splitMain.SplitterDistance = target; } catch { /* transient during resize */ }
                }
            }
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


        static void ShowError(string msg) =>
            MessageBox.Show(msg, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
