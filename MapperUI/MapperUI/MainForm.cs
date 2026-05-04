using CodeGen.Configuration;
using CodeGen.IO;
using CodeGen.Models;
using CodeGen.Validation;
using MapperUI.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
        SystemXmlReader? _lastReader;
        DebugConsoleForm? _debugConsole;
        Process? _llmProcess;
        System.Windows.Forms.Timer? _healthTimer;

        static readonly HttpClient _http = new()
        {
            BaseAddress = new Uri("http://127.0.0.1:8100/"),
            Timeout = TimeSpan.FromMinutes(10),
        };

        static readonly HashSet<string> _allowedInstances = new(StringComparer.OrdinalIgnoreCase)
        {
            "Checker", "Transfer", "Feeder", "Ejector",
            "PartInHopper", "PartAtChecker",
            "Bearing_PnP"
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

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            StartLlmEngine();
            StartHealthPolling();
            ValidatePortNamesOnStartup();
            LogInputFolderContents();
            lblStatus.Text = "Ready";
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


        async void btnGenerate_Click(object sender, EventArgs e)
        {
            btnGenerate.Enabled = false;
            txtActivityLog.Clear();

            var rejected = _validationRows
                .Where(r => r.TemplateName.StartsWith("No template found"))
                .Select(r => r.Component)
                .ToList();

            if (rejected.Count == 0)
            {
                AppendActivity("No rejected components to generate.");
                btnGenerate.Enabled = true;
                return;
            }

            AppendActivity($"Sending {rejected.Count} component(s) to LLM Engine…");

            try
            {
                var payload = new
                {
                    components = rejected.Select(c => new
                    {
                        ComponentID = c.ComponentID,
                        Name = c.Name,
                        Description = c.Description,
                        Type = c.Type,
                        States = c.States.Select(s => new
                        {
                            StateID = s.StateID,
                            Name = s.Name,
                            StateNumber = s.StateNumber,
                            InitialState = s.InitialState,
                            Time = s.Time,
                            Position = s.Position,
                            Counter = s.Counter,
                            StaticState = s.StaticState,
                        }),
                        NameTag = c.NameTag,
                    }),
                    control_xml_path = txtModelPath.Text,
                    pdf_paths = Array.Empty<string>(),
                };

                var json = JsonSerializer.Serialize(payload);
                using var body = new StringContent(json, Encoding.UTF8, "application/json");
                var postResp = await _http.PostAsync("generate", body);
                postResp.EnsureSuccessStatusCode();

                var postJson = await postResp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(postJson);
                var jobId = doc.RootElement.GetProperty("job_id").GetString()
                    ?? throw new Exception("No job_id in response.");

                AppendActivity($"Job submitted: {jobId}. Polling…");

                List<VueOneComponent> generated;
                while (true)
                {
                    await Task.Delay(2000);
                    var pollResp = await _http.GetAsync($"status/{jobId}");
                    pollResp.EnsureSuccessStatusCode();
                    var pollJson = await pollResp.Content.ReadAsStringAsync();
                    using var pollDoc = JsonDocument.Parse(pollJson);
                    var status = pollDoc.RootElement.GetProperty("status").GetString();
                    AppendActivity($"  status = {status}");

                    if (status == "completed")
                    {
                        var resultJson = pollDoc.RootElement.GetProperty("result").GetRawText();
                        generated = JsonSerializer.Deserialize<List<VueOneComponent>>(resultJson,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                            ?? new();
                        break;
                    }
                    if (status == "failed")
                    {
                        AppendActivity("LLM generation failed.");
                        return;
                    }
                }

                AppendActivity($"Deserialised {generated.Count} component(s). Injecting…");

                var cfg = Cfg();
                var injector = new SystemInjector();
                var result = await Task.Run(() => injector.Inject(cfg, generated,
                    controlXmlPath: null, mappingRulesPath: cfg.MappingRulesPath));

                if (result.Success)
                {
                    AppendActivity($"Done. {result.InjectedFBs.Count} FB(s) injected.");
                    Invoke(() => lblStatus.Text =
                        $"LLM injection done — {result.InjectedFBs.Count} FB(s).");
                }
                else
                {
                    AppendActivity($"Injection failed: {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                AppendActivity($"[Error] {ex.Message}");
            }
            finally
            {
                Invoke(() => btnGenerate.Enabled = true);
            }
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

        string? _loadedControlXmlPath;

        async void btnBrowse_Click(object sender, EventArgs e)
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "XML Files (*.xml)|*.xml|All Files (*.*)|*.*",
                Title = "Open VueOne Control.xml"
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            txtModelPath.Text = dlg.FileName;
            _loadedControlXmlPath = dlg.FileName;
            btnProcessFB.Enabled = true;
            btnTestStation1.Enabled = true;
            btnGenerateAll.Enabled = true;
            await LoadAndValidateAsync(dlg.FileName);
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

        void LogCleanup(SystemInjector.CleanupReport report)
        {
            AppendActivity($"[Cleanup] Removed {report.RemovedFbs.Count} universal FB(s), {report.RemovedConnections} connection(s)");
            foreach (var name in report.RemovedFbs) AppendActivity($"  - {name}");
            AppendActivity($"[Cleanup] Preserved {report.PreservedFbs.Count} non-universal FB(s)");
            foreach (var name in report.PreservedFbs) AppendActivity($"  + {name}");
        }

        CodeGen.Translation.IoBindings? TryLoadBindings()
        {
            try
            {
                var cfg = Cfg();
                var path = cfg.IoBindingsPath;
                if (!Path.IsPathRooted(path))
                    path = Path.Combine(AppContext.BaseDirectory, path);
                if (!File.Exists(path))
                {
                    AppendActivity($"[IoBindings] Bindings file not found at {path}; symlinks will use template defaults.");
                    return null;
                }
                var bindings = CodeGen.Translation.IoBindingsLoader.LoadBindings(path);
                AppendActivity($"[IoBindings] Loaded {bindings.Actuators.Count} actuator + {bindings.Sensors.Count} sensor binding(s) from {path}");
                return bindings;
            }
            catch (Exception ex)
            {
                AppendActivity($"[IoBindings] Failed to load: {ex.Message}");
                return null;
            }
        }

        void LogBindingsReport(SystemInjector.BindingApplicationReport report)
        {
            foreach (var (comp, detail) in report.Bound)
                AppendActivity($"[IoBindings] {comp} bound: {detail}");
            foreach (var miss in report.Missing)
                AppendActivity($"[IoBindings] No binding for component {miss}; component will not bind to physical I/O");
            if (report.Bound.Count > 0)
            {
                AppendActivity("[IoBindings] Symlink override via nested FB is invalid IEC 61499; PLC_RW_M262 variables must be renamed to match $${PATH} expansion: " +
                    "PusherAtHome to Pusher.athome, PusherAtWork to Pusher.atwork, ExtendPusher to Pusher.OutputToWork, Hopper to PartInHopper.Input. " +
                    "This is a one-time manual edit in PLC_RW_M262.fbt and is not Mapper's job.");
            }
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

        async void btnProcessFB_Click(object sender, EventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_loadedControlXmlPath) || !File.Exists(_loadedControlXmlPath))
                { ShowError("Load a Control.xml first via Browse."); return; }
                if (!TryResolveDemonstratorPath(out var syslayPath)) return;

                lblStatus.Text = "Generating...";
                AppendActivity($"[Button 1] Generating Process FB into Demonstrator at {syslayPath}...");

                var injector = new SystemInjector();
                var cleanup = await Task.Run(() => injector.PrepareDemonstratorForGeneration(Cfg()));
                LogCleanup(cleanup);

                AppendActivity("[IoBindings] skipped, Process FB has no symlinks");
                SystemInjector.BindingApplicationReport report = null!;
                var path = await Task.Run(() =>
                    injector.GenerateProcessFBSyslay(Cfg(), _loadedControlXmlPath, null, out report));
                LogBindingsReport(report);

                AppendActivity($"Generated: {path}");
                lblStatus.Text = $"Ready  |  {path}  |  Process FB only";
                MessageBox.Show($"Generated Process FB into Demonstrator:\n{path}",
                    "Process FB", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendActivity($"[Error] {ex}");
                lblStatus.Text = "Ready";
                ShowError(ex.Message);
            }
        }

        async void btnTestStation1_Click(object sender, EventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_loadedControlXmlPath) || !File.Exists(_loadedControlXmlPath))
                { ShowError("Load a Control.xml first via Browse."); return; }
                if (!TryResolveDemonstratorPath(out var syslayPath)) return;

                lblStatus.Text = "Generating...";
                AppendActivity($"[Button 2] Generating Test Station 1 (Pusher) into Demonstrator at {syslayPath}...");

                var injector = new SystemInjector();
                var cleanup = await Task.Run(() => injector.PrepareDemonstratorForGeneration(Cfg()));
                LogCleanup(cleanup);

                var bindings = TryLoadBindings();
                SystemInjector.BindingApplicationReport report = null!;
                var path = await Task.Run(() =>
                    injector.GenerateStation1TestSyslay(Cfg(), _loadedControlXmlPath, bindings, out report));
                LogBindingsReport(report);

                AppendActivity($"Generated: {path}");
                lblStatus.Text = $"Ready  |  {path}  |  {report.Bound.Count} bound, {report.Missing.Count} unbound";
                MessageBox.Show($"Generated Test Station 1 into Demonstrator:\n{path}\n\n{report.Bound.Count} bound, {report.Missing.Count} unbound.",
                    "Test Station 1", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendActivity($"[Error] {ex}");
                lblStatus.Text = "Ready";
                ShowError(ex.Message);
            }
        }

        async void btnGenerateAll_Click(object sender, EventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_loadedControlXmlPath) || !File.Exists(_loadedControlXmlPath))
                { ShowError("Load a Control.xml first via Browse."); return; }
                if (!TryResolveDemonstratorPath(out var syslayPath)) return;

                lblStatus.Text = "Generating...";
                AppendActivity($"[Button 3] Generating Full System (all stations) into Demonstrator at {syslayPath}...");

                var injector = new SystemInjector();
                var cleanup = await Task.Run(() => injector.PrepareDemonstratorForGeneration(Cfg()));
                LogCleanup(cleanup);

                var bindings = TryLoadBindings();
                SystemInjector.BindingApplicationReport report = null!;
                var path = await Task.Run(() =>
                    injector.GenerateFullSystemSyslay(Cfg(), _loadedControlXmlPath, bindings, out report));
                LogBindingsReport(report);

                AppendActivity($"Generated: {path}");
                lblStatus.Text = $"Ready  |  {path}  |  {report.Bound.Count} bound, {report.Missing.Count} unbound";
                MessageBox.Show($"Generated Full System into Demonstrator:\n{path}\n\n{report.Bound.Count} bound, {report.Missing.Count} unbound.",
                    "Generate All", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendActivity($"[Error] {ex}");
                lblStatus.Text = "Ready";
                ShowError(ex.Message);
            }
        }

        async void btnGeneratePusherTest_Click(object sender, EventArgs e)
        {
            try
            {
                if (!TryResolveDemonstratorPath(out var syslayPath)) return;

                lblStatus.Text = "Generating...";
                AppendActivity($"Generating Pusher Test into Demonstrator at {syslayPath}...");

                var injector = new SystemInjector();
                var report = await Task.Run(() => injector.PrepareDemonstratorForGeneration(Cfg()));
                LogCleanup(report);

                var bindings = TryLoadBindings();
                SystemInjector.BindingApplicationReport bindingReport = null!;
                var path = await Task.Run(() => injector.GeneratePusherTestSyslayToPath(syslayPath, bindings, out bindingReport));
                LogBindingsReport(bindingReport);

                AppendActivity($"Generated: {path}");
                lblStatus.Text = $"Ready  |  {path}  |  {bindingReport.Bound.Count} bound, {bindingReport.Missing.Count} unbound";
                MessageBox.Show($"Generated into Demonstrator:\n{path}\n\n{bindingReport.Bound.Count} components bound, {bindingReport.Missing.Count} without bindings.",
                    "Pusher Test", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendActivity($"[Error] {ex}");
                lblStatus.Text = "Ready";
                ShowError(ex.Message);
            }
        }

        async void btnGenerateFeedStation_Click(object sender, EventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_loadedControlXmlPath) || !File.Exists(_loadedControlXmlPath))
                {
                    ShowError("Load a Control.xml first via Browse.");
                    return;
                }
                if (!TryResolveDemonstratorPath(out var syslayPath)) return;

                lblStatus.Text = "Generating...";
                AppendActivity($"Generating Feed_Station into Demonstrator at {syslayPath}...");

                var injector = new SystemInjector();
                var report = await Task.Run(() => injector.PrepareDemonstratorForGeneration(Cfg()));
                LogCleanup(report);

                var bindings = TryLoadBindings();
                SystemInjector.BindingApplicationReport bindingReport = null!;
                var path = await Task.Run(() => injector.GenerateFeedStationSyslayToPath(_loadedControlXmlPath, syslayPath, bindings, out bindingReport));
                LogBindingsReport(bindingReport);

                AppendActivity($"Generated: {path}");
                AppendActivity("[v1] DataConnections not generated; manual wiring required for sensor-to-process status feeds.");
                lblStatus.Text = $"Ready  |  {path}  |  {bindingReport.Bound.Count} bound, {bindingReport.Missing.Count} unbound";
                MessageBox.Show($"Generated into Demonstrator:\n{path}\n\n{bindingReport.Bound.Count} components bound, {bindingReport.Missing.Count} without bindings.\nv1 limitation: DataConnections empty.",
                    "Feed Station", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendActivity($"[Error] {ex}");
                lblStatus.Text = "Ready";
                ShowError(ex.Message);
            }
        }

        async Task LoadAndValidateAsync(string path)
        {
            dgvComponents.Rows.Clear();
            dgvMappingRules.Rows.Clear();
            _loadedComponents.Clear();
            _validationRows.Clear();
            btnGenerateCode.Enabled = false;
            btnGenerate.Enabled = false;
            btnGenerateSevenState.Enabled = false;
            btnGenerateProcessFB.Enabled = false;
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
                    MapperLogger.Error(ex.Message);
                    MessageBox.Show(ex.Message, "Mapping Rules", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                var validator = new ComponentValidator();
                var cfg = Cfg();
                int rowIdx = 0;

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

                bool ok = _validationRows.All(r => r.IsValid);

                SetValidationLabel(ok ? "PASSED" : "FAILED", ok ? Color.Green : Color.Red);
                lblStatus.Text = ok ? "Validation passed." : "Validation failed.";
                btnGenerateCode.Enabled = ok && _validationRows.Any(r => r.IsValid && _allowedInstances.Contains(r.Component.Name));
                btnGenerateSevenState.Enabled = ok && _loadedComponents.Any(c => c.Type == "Actuator" && c.States.Count == 7);
                btnGenerateProcessFB.Enabled = ok && _loadedComponents.Any(c => c.Type == "Process");

                var noTemplate = _validationRows
                    .Where(r => r.TemplateName.StartsWith("No template found"))
                    .ToList();

                if (noTemplate.Count > 0)
                {
                    btnGenerate.Enabled = true;
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
                MessageBox.Show(ex.Message, "Mapping Rules", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            string tPath = TemplatePath(comp, cfg);
            string tName = string.IsNullOrEmpty(tPath)
                ? "No template found"
                : Path.GetFileName(tPath);

            switch (comp.Type.ToLowerInvariant())
            {
                case "process": return Pass(comp, tName);
                case "robot":
                    return string.IsNullOrWhiteSpace(cfg.RobotTemplatePath)
                        ? Fail(comp, tName, "RobotTemplatePath not set")
                        : Pass(comp, tName);
                case "actuator":
                    if (comp.States.Count == 7)
                        return Pass(comp, "Seven_State_Actuator_CAT.fbt");
                    if (comp.States.Count != 5)
                        return Fail(comp, "No template found",
                            $"{comp.States.Count} states — not 5 or 7");
                    break;
                case "sensor":
                    if (comp.States.Count != 2)
                        return Fail(comp, "No template found",
                            $"{comp.States.Count} states, not 2");
                    break;
                default:
                    return Fail(comp, tName, $"Unknown type '{comp.Type}'");
            }

            var vr = validator.Validate(comp);
            return vr.IsValid ?
                Pass(comp, tName) : Fail(comp, tName, string.Join("; ", vr.Errors));
        }

        static string TemplatePath(VueOneComponent comp, MapperConfig cfg) => comp.Type.ToLowerInvariant() switch
        {
            "actuator" => cfg.ActuatorTemplatePath,
            "sensor" => cfg.SensorTemplatePath,
            "process" => cfg.ProcessCATTemplatePath,
            "robot" => cfg.RobotTemplatePath,
            _ => string.Empty
        };

        static ComponentValidationRow Pass(VueOneComponent c, string t) =>
            new() { Component = c, TemplateName = t, IsValid = true };

        static ComponentValidationRow Fail(VueOneComponent c, string t, string r) =>
            new() { Component = c, TemplateName = t, IsValid = false, FailReason = r };


        async void btnGenerateCode_Click(object sender, EventArgs e)
        {
            var toInject = _validationRows
                .Where(r => r.IsValid && _allowedInstances.Contains(r.Component.Name))
                .Select(r => r.Component).ToList();

            if (toInject.Count == 0)
            {
                MessageBox.Show("No in-scope components to inject.", "Nothing to Inject",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
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

                var deployResult = await Task.Run(() => TemplateLibraryDeployer.Deploy(cfg, toInject));
                if (!deployResult.Success)
                {
                    var warns = string.Join("\n", deployResult.Warnings);
                    MapperLogger.Error($"Template deploy warnings: {warns}");
                }
                MapperLogger.Info($"[Deploy] {deployResult.CATsDeployed.Count} CAT(s), " +
                                 $"{deployResult.BasicFBsDeployed.Count} Basic FB(s), " +
                                 $"{deployResult.FilesExtracted} files extracted, " +
                                 $"{deployResult.FilesSkipped} skipped (already present).");

                if (Directory.Exists(cfg.TemplateIec61499Dir))
                {
                    await Task.Run(() => TemplatePackager.Package(
                        cfg.TemplateIec61499Dir,
                        Path.GetDirectoryName(dfbproj)!,
                        dfbproj,
                        cfg.TemplateHmiDir,
                        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(dfbproj)!)!, "HMI")));
                }

                var injCfg = MapperConfig.Load();
                injCfg.SyslayPath = cfg.ActiveSyslayPath;
                injCfg.SysresPath = cfg.ActiveSysresPath;

                var injector = new SystemInjector();
                var rulesPath = cfg.MappingRulesPath;
                var result = await Task.Run(() => injector.Inject(injCfg, toInject,
                    controlXmlPath: null, mappingRulesPath: rulesPath));

                if (!result.Success) { ShowError($"Injection failed:\n{result.ErrorMessage}"); return; }

                File.SetLastWriteTime(dfbproj, DateTime.Now);
                lblStatus.Text = $"Done. {result.InjectedFBs.Count} instance(s) injected.";
                MessageBox.Show($"Injected {result.InjectedFBs.Count} instance(s).\nReload Solution in EAE.",
                    "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally { btnGenerateCode.Enabled = true; }
        }

        async void btnGenerateSevenState_Click(object sender, EventArgs e)
        {
            var sevenStateComps = _loadedComponents
                .Where(c => c.Type == "Actuator" && c.States.Count == 7)
                .ToList();

            if (sevenStateComps.Count == 0)
            {
                MessageBox.Show("No seven-state actuator components found.", "Nothing to Inject",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            btnGenerateSevenState.Enabled = false;
            try
            {
                var cfg = Cfg();
                var dfbproj = FindDfbproj(cfg.ActiveSyslayPath);
                if (dfbproj == null) { ShowError("Cannot find .dfbproj."); return; }
                if (!File.Exists(cfg.ActiveSyslayPath)) { ShowError($"syslay not found:\n{cfg.ActiveSyslayPath}"); return; }

                MapperLogger.Info($"Seven State FB generation — {sevenStateComps.Count} component(s)");

                var deployResult = await Task.Run(() => TemplateLibraryDeployer.Deploy(cfg, sevenStateComps));
                if (!deployResult.Success)
                {
                    var warns = string.Join("\n", deployResult.Warnings);
                    MapperLogger.Error($"Template deploy warnings: {warns}");
                }
                MapperLogger.Info($"[Deploy] {deployResult.CATsDeployed.Count} CAT(s), " +
                                 $"{deployResult.BasicFBsDeployed.Count} Basic FB(s), " +
                                 $"{deployResult.FilesExtracted} files extracted, " +
                                 $"{deployResult.FilesSkipped} skipped (already present).");

                if (Directory.Exists(cfg.TemplateIec61499Dir))
                {
                    await Task.Run(() => TemplatePackager.Package(
                        cfg.TemplateIec61499Dir,
                        Path.GetDirectoryName(dfbproj)!,
                        dfbproj,
                        cfg.TemplateHmiDir,
                        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(dfbproj)!)!, "HMI")));
                }

                var injCfg = MapperConfig.Load();
                injCfg.SyslayPath = cfg.ActiveSyslayPath;
                injCfg.SysresPath = cfg.ActiveSysresPath;

                var injector = new SystemInjector();
                var rulesPath = cfg.MappingRulesPath;
                var result = await Task.Run(() => injector.Inject(injCfg, sevenStateComps,
                    controlXmlPath: null, mappingRulesPath: rulesPath));

                if (!result.Success) { ShowError($"Injection failed:\n{result.ErrorMessage}"); return; }

                File.SetLastWriteTime(dfbproj, DateTime.Now);
                lblStatus.Text = $"Done. {result.InjectedFBs.Count} seven-state instance(s) injected.";
                MessageBox.Show($"Injected {result.InjectedFBs.Count} seven-state actuator instance(s).\nReload Solution in EAE.",
                    "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally { btnGenerateSevenState.Enabled = true; }
        }

        async void btnGenerateProcessFB_Click(object sender, EventArgs e)
        {
            var processes = _loadedComponents
                .Where(c => c.Type == "Process")
                .ToList();

            if (processes.Count == 0)
            {
                MessageBox.Show("No Process components found in Control.xml.", "Nothing to Generate",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            btnGenerateProcessFB.Enabled = false;
            try
            {
                var cfg = Cfg();
                var dfbproj = FindDfbproj(cfg.ActiveSyslayPath);
                if (dfbproj == null) { ShowError("Cannot find .dfbproj."); return; }
                if (!File.Exists(cfg.ActiveSyslayPath)) { ShowError($"syslay not found:\n{cfg.ActiveSyslayPath}"); return; }

                MapperLogger.Info($"Process FB generation — {processes.Count} process(es)");
                AppendActivity($"Generating Process FB for {processes.Count} process(es)...");
                AppendActivity("[IoBindings] skipped, Process FB has no symlinks");

                var cleanupInjector = new SystemInjector();
                var cleanupReport = await Task.Run(() => cleanupInjector.PrepareDemonstratorForGeneration(cfg));
                LogCleanup(cleanupReport);

                var deployResult = await Task.Run(() => TemplateLibraryDeployer.Deploy(cfg, processes));
                if (!deployResult.Success)
                {
                    var warns = string.Join("\n", deployResult.Warnings);
                    MapperLogger.Error($"Template deploy warnings: {warns}");
                }
                MapperLogger.Info($"[Deploy] {deployResult.CATsDeployed.Count} CAT(s), " +
                                 $"{deployResult.BasicFBsDeployed.Count} Basic FB(s), " +
                                 $"{deployResult.FilesExtracted} files extracted, " +
                                 $"{deployResult.FilesSkipped} skipped (already present).");

                if (Directory.Exists(cfg.TemplateIec61499Dir))
                {
                    await Task.Run(() => TemplatePackager.Package(
                        cfg.TemplateIec61499Dir,
                        Path.GetDirectoryName(dfbproj)!,
                        dfbproj,
                        cfg.TemplateHmiDir,
                        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(dfbproj)!)!, "HMI")));
                }

                var injCfg = MapperConfig.Load();
                injCfg.SyslayPath = cfg.ActiveSyslayPath;
                injCfg.SysresPath = cfg.ActiveSysresPath;

                var injector = new SystemInjector();
                var rulesPath = cfg.MappingRulesPath;
                var result = await Task.Run(() => injector.Inject(injCfg, processes,
                    controlXmlPath: null, mappingRulesPath: rulesPath,
                    crossReferenceComponents: _loadedComponents));

                if (!result.Success) { ShowError($"Injection failed:\n{result.ErrorMessage}"); return; }

                File.SetLastWriteTime(dfbproj, DateTime.Now);

                foreach (var msg in result.InjectedFBs.Where(s => s.StartsWith("[StepTable]") || s.StartsWith("  Step ") || s.StartsWith("  WARN")))
                    AppendActivity(msg);

                lblStatus.Text = $"Done. {processes.Count} process(es) generated with step tables.";
                MessageBox.Show($"Generated {processes.Count} Process FB(s) with step tables.\nReload Solution in EAE.",
                    "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally { btnGenerateProcessFB.Enabled = true; }
        }

        async void btnGeneratePusherFB_Click(object sender, EventArgs e)
        {
            try
            {
                var cfg = Cfg();
                var dfbproj = FindDfbproj(cfg.ActiveSyslayPath);
                if (dfbproj == null) { ShowError("Cannot find .dfbproj."); return; }
                if (!File.Exists(cfg.ActiveSyslayPath)) { ShowError("syslay not found."); return; }

                var result = await Task.Run(() => PusherFBGenerator.Generate(cfg, _loadedComponents));
                MessageBox.Show(result, "FBs Generated", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        void dgvComponents_SelectionChanged(object sender, EventArgs e) { }

        void btnIO_Click(object sender, EventArgs e) { }

        void btnGenerateTemplate_Click(object sender, EventArgs e) { }

        void btnADP_Click(object sender, EventArgs e) { }

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

        static void ShowError(string msg) =>
            MessageBox.Show(msg, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}