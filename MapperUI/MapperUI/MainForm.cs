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

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            StartLlmEngine();
            StartHealthPolling();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            _healthTimer?.Stop();
            try { _llmProcess?.Kill(); } catch { }
        }

        // ── LLM Engine process ──────────────────────────────────────────────

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

        // ── Health polling ──────────────────────────────────────────────────

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

        // ── Generation Engine button ────────────────────────────────────────

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
                var result = await Task.Run(() => injector.Inject(cfg, generated));

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

        // ── Existing handlers ───────────────────────────────────────────────

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
            _loadedComponents.Clear();
            _validationRows.Clear();
            btnGenerateCode.Enabled = false;
            btnGenerate.Enabled = false;
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
                    foreach (var rule in MappingRuleEngine.GetAllRules(Cfg().MappingRulesPath))
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

                bool ok = _validationRows
                    .Where(r => _allowedInstances.Contains(r.Component.Name))
                    .All(r => r.IsValid);

                SetValidationLabel(ok ? "PASSED" : "FAILED", ok ? Color.Green : Color.Red);
                lblStatus.Text = ok ? "Validation passed." : "Validation failed.";
                btnGenerateCode.Enabled = _validationRows.Any(r => r.IsValid && _allowedInstances.Contains(r.Component.Name));

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
                foreach (var rule in MappingRuleEngine.GetAllRules(Cfg().MappingRulesPath))
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
                ? "No template found (discarded for this phase)"
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
                    {
                        tName = "Seven_State_Actuator_CAT.fbt";
                    }
                    else if (comp.States.Count != 5)
                    {
                        return Fail(comp, "No template found (discarded for this phase)",
                            $"{comp.States.Count} states — not 5 or 7");
                    }
                    break;
                case "sensor":
                    if (comp.States.Count != 2)
                        return Fail(comp, "No template found (discarded for this phase)",
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

                await Task.Run(() => TemplatePackager.Package(
                    cfg.TemplateIec61499Dir,
                    Path.GetDirectoryName(dfbproj)!,
                    dfbproj,
                    cfg.TemplateHmiDir,
                    Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(dfbproj)!)!, "HMI")));

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

        void dgvComponents_SelectionChanged(object sender, EventArgs e)
        {
            // Right panel replaced by Generation Engine — no component detail view needed.
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

        void btnIO_Click(object sender, EventArgs e)
        {
            // IO mapping — future phase
        }

        void btnGenerateTemplate_Click(object sender, EventArgs e)
        {
            // Template generation — future phase
        }

        void btnADP_Click(object sender, EventArgs e)
        {
            // ADP — future phase
        }

        static void ShowError(string msg) =>
            MessageBox.Show(msg, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}