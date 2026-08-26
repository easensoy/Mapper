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
            PopulateDeviceColumn();
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

        // The selectable controllers ARE the registered targets, and what a relocation target can serve
        // is its own coupler's answer. Both were baked into designer code, where neither could follow
        // device.yml - a target added there was silently blanked and a component list went stale.
        void PopulateDeviceColumn()
        {
            var targets = Declarations().Targets.All;
            colDevice.Items.Clear();
            foreach (var t in targets) colDevice.Items.Add(t.Plc.ToString());

            var home = targets.FirstOrDefault(t => t.HostsFeedStation && !t.ReceivesRelocatedComponents);
            var relocation = targets.FirstOrDefault(t => t.ReceivesRelocatedComponents);
            colDevice.ToolTipText = relocation == null
                ? "Hosting controller for this component."
                : $"Hosting controller. Set a component to {relocation.Plc} to host it there instead of " +
                  $"{home?.Plc.ToString() ?? "its home controller"}; only " +
                  string.Join(", ", ServableBy(relocation.Plc)) +
                  $" have IO on that target, and the rest stay put. " +
                  $"{targets.Count} controllers are registered.";
        }

        // What a target's own hardware can serve is the TARGET's answer. The UI composes the SAME
        // backend list a run does rather than naming a particular device's injector, so the grid can
        // never describe a different set than Generate uses, and a project whose relocation host is
        // different hardware needs no edit here.
        static IReadOnlySet<string> ServableBy(CodeGen.Translation.PlcAssignment plc) =>
            CodeGen.Application.GenerateProject.Backends()
                .FirstOrDefault(b => b.Target == plc)?.ServableComponents
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                // The SAME configuration and placement the run is planned with, so the preview cannot
                // show a different project than Generate writes.
                var previewCfg = Cfg();
                var previewProfile = CodeGen.Mapping.DeploymentProfile.Relocating(
                    CollectRevPiSelection(), Declarations());

                if (_stateTransitionTableForm == null || _stateTransitionTableForm.IsDisposed)
                    _stateTransitionTableForm = new StateTransitionTableForm(
                        _loadedControlXmlPath, _loadedComponents, previewCfg, previewProfile);
                else
                    _stateTransitionTableForm.Reload(
                        _loadedControlXmlPath, _loadedComponents, previewCfg, previewProfile);

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
                    // Which files are consumed is the CONFIGURATION's answer: retarget a path in
                    // mapper_config.json and this log follows it. A table of file names here could
                    // only report what the generator used to read.
                    var name = Path.GetFileName(f);
                    var cfg = Cfg();
                    string status =
                        Same(name, cfg.MappingRulesPath) ? "consumed (mapping rules)"
                        : Same(name, cfg.IoBindingsPath) ? "consumed (IO bindings)"
                        : "not read by the generator";
                    AppendActivity($"  - {name}: {status}");
                }
            }
            catch (Exception ex)
            {
                AppendActivity($"[Startup] Failed to enumerate Input folder: {ex.Message}");
            }
        }

        // A configured path may be relative, absolute or bare; only its file name identifies the file.
        static bool Same(string name, string? configuredPath) =>
            !string.IsNullOrWhiteSpace(configuredPath) &&
            string.Equals(name, Path.GetFileName(configuredPath), StringComparison.OrdinalIgnoreCase);

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
                    "IEC 61499 code generated — end to end.\n\n" +
                    "Process recipes, interlock safety tables and I/O bindings were emitted across\n" +
                    $"{Declarations().Targets.All.Count} controller(s) " +
                    $"({result.BoundCount} I/O channel(s) bound).\n\n" +
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

        // The operator's selection, PASSED THROUGH. Whether a target can serve a component is that
        // target's own answer and the compiler REFUSES a selection it cannot host; filtering it here
        // silently overrode that refusal, so an unservable pick looked accepted and quietly did nothing.
        IReadOnlySet<string> CollectRevPiSelection()
        {
            var relocation = Declarations().Targets.All
                .FirstOrDefault(t => t.ReceivesRelocatedComponents);
            if (relocation == null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return _deviceOverrides
                .Where(kv => string.Equals(kv.Value, relocation.Plc.ToString(), StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        // Logged on EVERY run: a run that relocates nothing must be distinguishable from one that does.
        // Every name comes from the registry or device.yml, so a controller added there is reported.
        void LogControllerChoice(IReadOnlySet<string> relocated)
        {
            var targets = Declarations().Targets.All;
            var home = targets.FirstOrDefault(t => t.HostsFeedStation && !t.ReceivesRelocatedComponents);
            var relocation = targets.FirstOrDefault(t => t.ReceivesRelocatedComponents);
            if (relocated.Count == 0 || relocation == null)
            {
                AppendActivity($"[Target] Feed controller: {home?.Plc.ToString() ?? "(none declared)"} " +
                               "(nothing relocated).");
                return;
            }
            var alwaysHosted = Declarations().Devices.AlwaysHostedBy(relocation.Plc);
            var picked = relocated.Where(c => !alwaysHosted.Contains(c, StringComparer.OrdinalIgnoreCase));
            AppendActivity(
                $"[Target] Feed controller: {home?.Plc} + {relocation.Plc} — " +
                $"{string.Join(", ", picked)} + {string.Join(", ", alwaysHosted)} on the {relocation.Plc}; " +
                $"{home?.Plc} keeps the rest ({targets.Count} controllers).");
            AppendActivity($"[Target] {relocation.Plc} endpoints: host {Cfg().RevPiHostIp} " +
                           "(Soft dPAC Manager :8080, EAE 'Manage Soft dPAC') / container " +
                           $"{Cfg().RevPiTargetIp} (IEC 61499 runtime, EAE Deploy+Login target).");
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
                    foreach (var rule in MappingRuleEngine.GetRelevantRules(
                        Cfg().MappingRulesPath, ShapesPresent(_loadedComponents, Declarations().Manifest)))
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
                // The SAME selection and placement the run is planned with, so the Device column
                // cannot show a controller Generate would not use.
                var roster = new CodeGen.Mapping.DeploymentRoster(
                    CodeGen.Mapping.DeploymentProfile.Relocating(
                        CollectRevPiSelection(), Declarations()));
                foreach (var comp in _loadedComponents)
                {
                    var vr = Validate(comp, validator, Declarations().Manifest);
                    _validationRows.Add(vr);

                    var reg = roster.Get(comp.Name);
                    string dev = _deviceOverrides.TryGetValue(comp.Name, out var ov)
                        ? ov
                        : (reg?.Plc.ToString() ?? "");
                    // A device the registry does not know is stored as null (blank) to avoid a DataError.
                    // Which controllers exist is device.yml's answer, so a target added there appears
                    // here without an edit instead of being silently blanked by a list in the UI.
                    string? devCell = Declarations().Targets.All
                        .Any(t => string.Equals(t.Plc.ToString(), dev, StringComparison.Ordinal)) ? dev : null;
                    int idx = dgvComponents.Rows.Add(comp.Name, comp.Type, vr.TemplateName, devCell!);
                    var row = dgvComponents.Rows[idx];
                    Color bg = (rowIdx++ % 2 == 0) ? RowEven : RowOdd;
                    row.DefaultCellStyle.BackColor = bg;
                    row.DefaultCellStyle.ForeColor = Color.Black;
                    row.Cells[2].Style.ForeColor = vr.IsValid ? ColorTranslated : ColorDiscarded;
                    row.Cells[2].Style.BackColor = bg;
                    // The reason a component was refused: computed for every failed row and, until now,
                    // thrown away. The operator saw "No template found" and nothing about WHY.
                    if (!vr.IsValid && vr.FailReason.Length > 0)
                        row.Cells[2].ToolTipText = vr.FailReason;
                }
                }
                finally { _populatingGrid = false; }

                RefreshDeviceSummary();

                UpdateDetectedInfo();

                bool ok = _validationRows.All(r => r.IsValid);

                SetValidationLabel(ok ? "PASSED" : "FAILED", ok ? Color.Green : Color.Red);
                lblStatus.Text = ok ? "Validation passed." : "Validation failed.";

                var noTemplate = _validationRows.Where(r => !r.IsValid).ToList();
                if (noTemplate.Count > 0)
                {
                    AppendActivity(
                        $"{noTemplate.Count} component(s) have no template and can be generated by the LLM Engine: " +
                        string.Join(", ", noTemplate.Select(r => r.Component.Name)));
                    foreach (var r in noTemplate.Where(r => r.FailReason.Length > 0))
                        AppendActivity($"  - {r.Component.Name}: {r.FailReason}");
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

                    rules = MappingRuleEngine.GetRelevantRules(
                        Cfg().MappingRulesPath, ShapesPresent(_loadedComponents, Declarations().Manifest));
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


        // The grid REPORTS the compiler's decision; it does not make one. Which shapes a CAT serves is
        // declared in templates.yml and answered by the run's TemplateIndex, so a CAT added there
        // shows up here with no edit - and the row can never name a template the run would not emit.
        static ComponentValidationRow Validate(VueOneComponent comp, ComponentValidator validator,
            CodeGen.Mapping.TemplateIndex manifest)
        {
            if (ComponentType.IsProcess(comp))
                return Pass(comp, CatFile(manifest.ProcessType.Name));

            if (ComponentType.IsSensor(comp))
            {
                var sensorFile = CatFile(manifest.SensorType.Name);
                var vr = validator.Validate(comp);
                return vr.IsValid
                    ? Pass(comp, sensorFile)
                    : Fail(comp, sensorFile, vr.Summary);
            }

            if (!ComponentType.IsActuator(comp) && !ComponentType.Is(comp, ComponentType.Robot))
                return Fail(comp, NoTemplate, $"Unknown type '{comp.Type}'");

            // The one component -> FB Type decision, asked of its owner. Its refusal message already
            // says which shapes ARE served, so the grid shows that rather than a second rule here.
            try
            {
                return Pass(comp, CatFile(manifest.ResolveActuatorCatType(comp)));
            }
            catch (InvalidOperationException ex)
            {
                return Fail(comp, NoTemplate, ex.Message);
            }
        }

        // Which CATs this twin actually needs, answered by the one resolver the run uses. A shape no
        // CAT serves is not this grid's decision to make, so it is skipped rather than misreported.
        static IEnumerable<string> ShapesPresent(IEnumerable<VueOneComponent> components,
            CodeGen.Mapping.TemplateIndex manifest)
        {
            var names = new List<string>();
            foreach (var c in components)
            {
                if (ComponentType.IsSensor(c)) { names.Add(manifest.SensorType.Name); continue; }
                if (!ComponentType.IsActuator(c) && !ComponentType.Is(c, ComponentType.Robot)) continue;
                try { names.Add(manifest.ResolveActuatorCatType(c)); }
                catch (InvalidOperationException) { }
            }
            return names;
        }

        const string NoTemplate = "No template found";

        static string CatFile(string typeName) => typeName + ".fbt";

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
            // Which target receives relocated components, and which components its coupler can serve,
            // are both the compiler's answers - naming either here is a second one that can contradict it.
            bool relocating = IsRelocationTarget(dev);
            var servable = ServableBy(CodeGen.Translation.PlcAssignment.Named(dev));
            AppendActivity($"[UI] Device set: {comp} -> {dev}" +
                (relocating
                    ? servable.Contains(comp)
                        ? $" (applied at Generate: this component moves to the {dev})."
                        : $" — IGNORED at Generate: the {dev} coupler carries no IO for this component. " +
                          $"It serves: {string.Join(", ", servable)}."
                    : "."));
        }

        // One count per REGISTERED target, in declaration order. A controller added to device.yml is
        // counted here without an edit; a per-controller counter could only omit it.
        void RefreshDeviceSummary()
        {
            var counts = Declarations().Targets.All
                .ToDictionary(t => t.Plc.ToString(), _ => 0, StringComparer.Ordinal);
            foreach (DataGridViewRow r in dgvComponents.Rows)
            {
                var dev = r.Cells[colDevice.Index].Value?.ToString();
                if (dev != null && counts.ContainsKey(dev)) counts[dev]++;
            }
            var shown = counts.Where(kv => kv.Value > 0 || !IsRelocationTarget(kv.Key))
                              .Select(kv => $"{kv.Key}: {kv.Value}");
            grpMappingInfo.Text =
                "Mapping Information   —   " + string.Join(" · ", shown) + " mapped component(s)";
        }

        // A target that only ever receives relocated components has nothing on it until something is
        // moved there, so it is left out of the summary until it does.
        bool IsRelocationTarget(string plc) =>
            Declarations().Targets.All.Any(t =>
                string.Equals(t.Plc.ToString(), plc, StringComparison.Ordinal) &&
                t.ReceivesRelocatedComponents);

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

        // THE UI's COMPOSITION ROOT. Read once per window, so the grid, the preview and the run all
        // describe the same configuration; a second read could show a project Generate would not write.
        CodeGen.Configuration.CompilerConfiguration Declarations() =>
            _declarations ??= CodeGen.Configuration.CompilerConfiguration.Load(Cfg());
        CodeGen.Configuration.CompilerConfiguration? _declarations;


        static void ShowError(string msg) =>
            MessageBox.Show(msg, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
