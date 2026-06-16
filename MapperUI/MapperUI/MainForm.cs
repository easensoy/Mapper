using CodeGen.Configuration;
using CodeGen.IO;
using CodeGen.Models;
using CodeGen.Validation;
using CodeGen.Devices.M262;
using CodeGen.Devices.M580;
using CodeGen.Devices.BX1;
using CodeGen.Devices.Core;
using CodeGen.Services;
using CodeGen.Translation;
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
        StateTransitionTableForm? _stateTransitionTableForm;
        Process? _llmProcess;
        System.Windows.Forms.Timer? _healthTimer;

        static readonly HttpClient _http = new()
        {
            BaseAddress = new Uri("http://127.0.0.1:8100/"),
            Timeout = TimeSpan.FromMinutes(10),
        };

        // Components in scope for the Test Runtime button. Feed Station
        // (M262) + Assembly Station (M580 + BX1). Disassembly Process is
        // intentionally NOT in scope yet — Phase 2 will add the Robot +
        // Ejector + Disassembly recipe later.
        static readonly HashSet<string> _allowedInstances = new(StringComparer.OrdinalIgnoreCase)
        {
            // Station 1 (M262) — Feed_Station Process
            "Feeder", "Checker", "Transfer",
            "PartInHopper", "PartAtChecker",

            // Station 2 (M580) — Assembly_Station core
            // Bearing_PnP restored 2026-05-21 alongside Seven_State_Actuator_CAT
            // — Mapper Validator now passes Control.xml for the 13-state
            // PARALLEL+ALTERNATIVE branched actuator (assembly + disassembly
            // sweep). Routed via ResolveActuatorFBType / validator below.
            "Bearing_PnP",
            "Bearing_Gripper",
            "Shaft_Hr", "Shaft_Vr", "Shaft_Gripper",
            "Clamp",
            "BearingSensor", "ShaftSensor",

            // Station 2 (BX1) — Cover Pick-and-Place
            "CoverPNP_Hr", "CoverPNP_Vr",
            "CoverPnp_Gripper",
            "TopCoverSenosr",
        };

        /// <summary>
        /// Vacuum-driven gripper instances (suction cups, single coil, no
        /// athome/atwork sensor pair). Reference SMC_Rig_Expo_withClamp maps
        /// CoverGripper to Vacuum_Gripper_CAT.fbt; bearing/shaft grippers are
        /// mechanical fingers and use Five_State_Actuator_CAT instead.
        /// Detection is by exact name since Control.xml Type=Robot does not
        /// distinguish vacuum from mechanical.
        /// </summary>
        static readonly HashSet<string> _vacuumGripperNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CoverPnp_Gripper",
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
                // Never let the State-Transition Table take down MapperUI with an unhandled popup.
                // Surface the real cause (and the missing file name, if it's a FileNotFound) so it
                // can be fixed, instead of the generic "Unhandled exception" dialog.
                var fnf = ex as System.IO.FileNotFoundException
                          ?? ex.InnerException as System.IO.FileNotFoundException;
                ShowError(
                    $"State-Transition Table failed: {ex.GetType().Name}: {ex.Message}" +
                    (fnf?.FileName != null ? $"{Environment.NewLine}Missing file: {fnf.FileName}" : string.Empty));
            }
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
            btnTestStation1.Enabled = true;
            await LoadAndValidateAsync(dlg.FileName);
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

        async Task DeployUniversalTemplatesAsync()
        {
            try
            {
                var deploy = await Task.Run(() => TemplateLibraryDeployer.DeployUniversalArchitecture(Cfg()));
                AppendActivity($"[Deploy] Registered {deploy.CATsDeployed.Count} CAT(s), " +
                    $"{deploy.BasicFBsDeployed.Count} Basic(s), " +
                    $"{deploy.CompositesDeployed.Count} Composite(s), " +
                    $"{deploy.AdaptersDeployed.Count} Adapter(s) into Demonstrator/IEC61499 " +
                    $"({deploy.FilesExtracted} new, {deploy.FilesSkipped} skipped).");
                foreach (var w in deploy.Warnings)
                    AppendActivity($"[Deploy][Warn] {w}");
            }
            catch (Exception ex)
            {
                AppendActivity($"[Deploy][Error] {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Re-runs the M262 stack AFTER the syslay has been (re)written by
        /// SystemInjector. This is mandatory because the upstream order is:
        ///   1. DeployUniversalTemplatesAsync -> M262SysdevEmitter.Emit (mirrors FBs to .sysres)
        ///   2. PrepareDemonstratorForGeneration -> CLEANS .sysres, wiping the mirror
        ///   3. GenerateStation1TestSyslay -> writes new .syslay only
        /// Without this re-run after step 3, .sysres ends up empty and the .hcf
        /// pin symlinks blank, leaving every FB unmapped on the EAE canvas.
        /// </summary>
        async Task FinalizeM262StackAsync()
        {
            // Trust-preservation guard. Evaluated once up-front so both the
            // sysdev and topology branches see the same decision even if the
            // sysdev gets touched mid-flow.
            bool m262DeviceExists = await Task.Run(() =>
                M262SysdevEmitter.M262SysdevAlreadyExists(Cfg()));

            string sysdevId = string.Empty;
            try
            {
                var sysdev = await Task.Run(() => M262SysdevEmitter.Emit(Cfg()));
                if (sysdev.DevicePreserved)
                {
                    AppendActivity(
                        "[Device] M262 sysdev exists, skipping device creation and " +
                        "config writes to preserve trust binding");
                    AppendActivity(
                        $"[M262] .sysres mirrored {sysdev.SysresFbsMirrored} FB(s) to {sysdev.SysresPath} (application-layer only)");
                }
                else
                {
                    AppendActivity(
                        $"[M262] sysdev re-emitted; .sysres mirrored {sysdev.SysresFbsMirrored} FBs to {sysdev.SysresPath}");
                }
                sysdevId = ReadSysdevId(sysdev.SysdevPath);
            }
            catch (Exception ex)
            {
                AppendActivity($"[M262][Error] sysdev emit: {ex.Message}");
            }
            // Topology emit always runs. The trust binding lives in
            // solutionData (CsConfHash + CertThumbprint), which the emitter
            // now preserves byte-for-byte if already present. Equipment JSON
            // carries only visual device placement on Physical Views and
            // MUST be (re)written every run, otherwise the M262dPAC never
            // appears on the canvas after a Demonstrator wipe — even if a
            // .sysdev with the right GUID is already on disk.
            try
            {
                if (!string.IsNullOrEmpty(sysdevId))
                {
                    var topo = await Task.Run(() => M262TopologyEmitter.Emit(Cfg(), sysdevId));
                    AppendActivity($"[M262] topology emitted: {topo.FilesWritten.Count} JSON file(s), {topo.TopologyProjEntriesAdded} topologyproj entries added");
                    if (m262DeviceExists)
                        AppendActivity("[Device] solutionData preserved (existing trust binding kept intact)");
                    foreach (var w in topo.Warnings)
                        AppendActivity($"[M262][Warn] topology: {w}");
                }
                else
                {
                    AppendActivity("[M262][Warn] topology emit skipped — sysdevId was empty");
                }
            }
            catch (Exception ex)
            {
                AppendActivity($"[M262][Error] topology emit: {ex.Message}");
            }
            if (m262DeviceExists)
                AppendActivity("[Device] M262 sysdev preserved (trust binding intact)");

            // Station 2 — M580 + BX1 sysdev + sysres + HCF copy + Topology
            // Equipment JSON. New per-PLC artefacts modelled on
            // SMC_Rig_Expo_withClamp's reference layout. Idempotent re-runs.
            try
            {
                var stn2 = await Task.Run(() => Station2DeviceEmitter.EmitAll(Cfg()));
                AppendActivity(
                    $"[Stn2] {stn2.FilesWritten.Count} file(s) written, " +
                    $"{stn2.TopologyProjEntriesAdded} topologyproj entries, " +
                    $"{stn2.DfbprojEntriesAdded} dfbproj entries");
                foreach (var f in stn2.FilesWritten)
                    AppendActivity($"[Stn2]   {f}");
                foreach (var w in stn2.Warnings)
                    AppendActivity($"[Stn2][Warn] {w}");
            }
            catch (Exception ex)
            {
                AppendActivity($"[Stn2][Error] Station 2 emit: {ex.Message}");
            }

            // Purge EAE's per-device compile cache (IEC61499\bin\,
            // IEC61499\obj\, snapshot.xml). EAE caches compile state
            // structurally — when a sysres is renamed or a stale sysres is
            // removed, the cache still records the OLD layout and surfaces
            // stale "Device contains 2 instances of EMB_RES_ECO"-class errors
            // even after the disk is clean. EAE's own Clean button does not
            // flush these. Runs early so the subsequent emitters write into a
            // freshly-clean cache.
            try
            {
                var purge = await Task.Run(() => CodeGen.Devices.Core.CompileCachePurger.Purge(Cfg()));
                if (purge.FoldersRemoved > 0 || purge.SnapshotReset)
                    AppendActivity($"[Topology] compile cache purged: {purge.FoldersRemoved} folder(s), snapshot reset={purge.SnapshotReset}");
                foreach (var w in purge.Warnings) AppendActivity($"[Topology][Warn] cache purge: {w}");
            }
            catch (Exception ex)
            {
                AppendActivity($"[Topology][Error] cache purge: {ex.Message}");
            }

            // Folders.xml registration — register M580 + BX1 sysdev GUIDs in
            // the SystemDevice Root folder. EAE seeds Solution Explorer's
            // SystemDevice node + Deploy & Diagnostic enumeration from this
            // file; a sysdev that's not listed here gets silently dropped from
            // D&D even with a valid sysdev + Equipment JSON + dfbproj entry.
            try
            {
                var fx = await Task.Run(() => CodeGen.Devices.Core.FoldersXmlEmitter.Register(Cfg()));
                if (fx.ItemsAdded > 0) AppendActivity($"[Topology] Folders.xml: registered {fx.ItemsAdded} sysdev GUID(s)");
                foreach (var w in fx.Warnings) AppendActivity($"[Topology][Warn] Folders.xml: {w}");
            }
            catch (Exception ex)
            {
                AppendActivity($"[Topology][Error] Folders.xml register: {ex.Message}");
            }

            // BroadcastDomain JSON — pin the Default Network subnet + gateway
            // to the live rig values (cfg.DefaultNetworkSubnetAddress / Mask /
            // Gateway). The M580 endpoint binds to this domain so a mismatch
            // shows red in EAE's connect-to-device verification dialog.
            try
            {
                var bd = await Task.Run(() => CodeGen.Devices.Core.BroadcastDomainEmitter.Emit(Cfg()));
                foreach (var f in bd.FilesWritten) AppendActivity($"[Topology]   {f}");
                foreach (var w in bd.Warnings)     AppendActivity($"[Topology][Warn] {w}");
            }
            catch (Exception ex)
            {
                AppendActivity($"[Topology][Error] BroadcastDomain emit: {ex.Message}");
            }

            // Topology self-consistency guard (RIG path). Station2DeviceEmitter just
            // wrote Equipment_BX1.json, whose softdpac container binds to the
            // softdpacDeviceNet domain (db72f221 = DeviceNetwork_1). That domain is
            // NOT one of the always-emitted ones (Default Network / NoConf), so
            // without a BroadcastDomain_DeviceNetwork_1.json on disk the BX1 Equipment
            // points at a DANGLING domain UUID and EAE rejects the WHOLE topology
            // import ("Unable to import topology / verify file format / Internal
            // Server Error"). EnsureReferencedDomains scans every Equipment_*.json and
            // creates + registers any referenced-but-undeclared domain at
            // 192.168.1.0/24 (matching the reference's DeviceNetwork_1). The sim path
            // already ran this; the rig path never did — that was the root cause.
            try
            {
                var dom = await Task.Run(() =>
                    CodeGen.Devices.Core.BroadcastDomainEmitter.EnsureReferencedDomains(Cfg()));
                foreach (var f in dom.FilesWritten) AppendActivity($"[Topology]   {f}");
                foreach (var w in dom.Warnings)     AppendActivity($"[Topology] {w}");
            }
            catch (Exception ex)
            {
                AppendActivity($"[Topology][Error] domain consistency: {ex.Message}");
            }

            // Strip stale sister-folder dfbproj <Content>/<None>/<Compile>
            // entries — entries whose Include= references a hex-stem folder
            // that no longer has a matching .sysres on disk. Without this
            // step, EAE's Solution Integrity dialog lists every opcua.xml /
            // offline.xml / opcuaclient.xml that used to live in those folders
            // as a "missing project file" — confusing the user even when the
            // sweep correctly removed the actual folders.
            try
            {
                var eae = CodeGen.Devices.Core.EaeProjectLayout.DeriveEaeProjectRoot(Cfg());
                if (!string.IsNullOrEmpty(eae))
                {
                    var dfb = System.IO.Path.Combine(eae, "IEC61499", "IEC61499.dfbproj");
                    int n = await Task.Run(() =>
                        CodeGen.Devices.Core.DfbprojRegistrar.StripStaleSysresStemEntries(dfb, eae));
                    AppendActivity($"[Topology] stripped {n} stale dfbproj sysres-stem entries");
                }
            }
            catch (Exception ex)
            {
                AppendActivity($"[Topology][Error] dfbproj stem sweep: {ex.Message}");
            }

            // Topology network — emit Equipment_Switch_1.json + the Wire JSON
            // files connecting M262 ↔ Switch_1 ↔ M580. Reference rig has all
            // three PLCs cabled into one L2 switch on the Physical Views
            // diagram; without these files the deployed Demonstrator shows
            // every PLC icon floating in isolation with no declared path
            // between them. Runs AFTER Station2DeviceEmitter so the M262 +
            // M580 Equipment UUIDs the wires reference are already on disk.
            try
            {
                var net = await Task.Run(() => CodeGen.Devices.Core.TopologyNetworkEmitter.Emit(Cfg()));
                AppendActivity(
                    $"[Topology] {net.FilesWritten.Count} network file(s) written, " +
                    $"{net.TopologyProjEntriesAdded} topologyproj entries");
                foreach (var f in net.FilesWritten) AppendActivity($"[Topology]   {f}");
                foreach (var w in net.Warnings)     AppendActivity($"[Topology][Warn] {w}");
            }
            catch (Exception ex)
            {
                AppendActivity($"[Topology][Error] network emit: {ex.Message}");
            }

            // Station 2 — mirror the Station-2 FBs from the .syslay onto the
            // M580/BX1 resources (each bucketed by M262SysdevEmitter.BucketFor)
            // and emit the per-resource opcua.xml metadata folder beside each
            // sysres. Runs AFTER Station2DeviceEmitter.EmitAll has written the
            // sysdev/sysres shells. Without this the M580/BX1 .sysres files stay
            // empty and the "{resId}/" folder is missing, so EAE's Solution
            // Integrity reports "Repair Instances" / "Missing Project Files".
            try
            {
                var s2 = await Task.Run(() => Station2SysresMirror.EmitStation2Sysres(Cfg()));
                AppendActivity($"[Stn2] mirrored FBs → M580:{s2.M580} BX1:{s2.BX1}");
            }
            catch (Exception ex)
            {
                AppendActivity($"[Stn2][Error] sysres mirror: {ex.Message}");
            }

            // Backfill the opcua.xml companion files EAE's Solution Integrity
            // requires in every resource/companion folder (current + stale).
            // Non-destructive sweep; only fills files that are missing.
            try
            {
                var eae = CodeGen.Devices.Core.EaeProjectLayout.DeriveEaeProjectRoot(Cfg());
                if (!string.IsNullOrEmpty(eae))
                {
                    int n = CodeGen.Artefacts.OpcuaCompanionEmitter.EnsureOpcuaInAllResourceFolders(eae);
                    AppendActivity($"[Artefacts] opcua.xml companions ensured: {n} created");
                }
            }
            catch (Exception ex)
            {
                AppendActivity($"[Artefacts][Error] opcua sweep: {ex.Message}");
            }

            try
            {
                var hcf = await Task.Run(() => M262HwConfigCopier.Copy(Cfg()));
                AppendActivity($"[M262] hcf re-patched; {hcf.ParametersOverwritten.Count} channel symlink(s) written");
                foreach (var w in hcf.Warnings)
                    AppendActivity($"[M262][Warn] {w}");
            }
            catch (Exception ex)
            {
                AppendActivity($"[M262][Error] hcf patch: {ex.Message}");
            }

            // M580 HCF — authoritative final pass. Copies the user-authored
            // IO-folder M580IO.hcf verbatim into the deployed M580 sysdev folder
            // (re-rooted to DeviceHwConfigurationItems with the resource ID).
            // Runs AFTER Station2DeviceEmitter so it refills the .hcf even when
            // DemonstratorWiper left a 169-byte empty shell or the emit-time copy
            // was skipped because the template path was unresolved. The
            // 'RES0.M580IO.<sym>' channel symlinks are preserved byte-for-byte.
            try
            {
                var hcf = await Task.Run(() => M580HwConfigCopier.Copy(Cfg()));
                AppendActivity($"[M580] hcf deployed; {hcf.FilesCopied} file(s) copied → {hcf.HcfPath}");
                foreach (var w in hcf.Warnings)
                    AppendActivity($"[M580][Warn] {w}");
            }
            catch (Exception ex)
            {
                AppendActivity($"[M580][Error] hcf deploy: {ex.Message}");
            }

            // BX1 HCF — authoritative final pass (same rationale as M580). The
            // BX1IO.hcf is an EtherNet/IP scanner whose TM3 module input/output
            // words route through single VTQWORD symlinks
            // ('{resId}.{fb}.EIP_*_Word_1'); bit decoding lives inside the BX1
            // SIFB, not the .hcf. Carried verbatim. Without this the BX1 device
            // showed an empty hardware config after a wipe.
            try
            {
                var hcf = await Task.Run(() => BX1HwConfigCopier.Copy(Cfg()));
                AppendActivity($"[BX1] hcf deployed; {hcf.FilesCopied} file(s) copied → {hcf.HcfPath}");
                foreach (var w in hcf.Warnings)
                    AppendActivity($"[BX1][Warn] {w}");
            }
            catch (Exception ex)
            {
                AppendActivity($"[BX1][Error] hcf deploy: {ex.Message}");
            }

            // Final dfbproj hygiene. After a device wipe the dfbproj can still
            // reference per-resource EAE compile artifacts (opcua/offline/
            // opcuaclient/symlink) that were deleted with the device folders —
            // EAE's Solution Integrity then lists them as Missing Project Files.
            // All device files (sysdev/sysres/hcf/Properties/Simulation.Binding)
            // are written by now, so the only still-missing System files are those
            // EAE-owned artifacts; strip their dangling refs (EAE regenerates +
            // re-registers them on the next Build). Never touches the device files.
            try
            {
                var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(Cfg());
                if (!string.IsNullOrEmpty(eaeRoot))
                {
                    int stripped = await Task.Run(() =>
                        DfbprojRegistrar.StripDanglingResourceArtifactEntries(eaeRoot));
                    if (stripped > 0)
                        AppendActivity(
                            $"[Device] stripped {stripped} dangling compile-artifact ref(s) " +
                            "from the dfbproj (EAE regenerates them on Build) — Solution Integrity clean");
                }
            }
            catch (Exception ex)
            {
                AppendActivity($"[Device][Warn] dfbproj artifact-ref cleanup: {ex.Message}");
            }
        }

        /// <summary>
        /// Pre-flight note for Button 2. The Mapper now OWNS the M262 logical
        /// device: when an M262 <c>.sysdev</c> already exists it is preserved
        /// (trust binding kept); when it is ABSENT (e.g. right after Clean
        /// Demonstrator) <see cref="M262SysdevEmitter"/> BOOTSTRAPS it from
        /// scratch with the same sysdev GUID + resource id (so trust keyed by
        /// the device GUID is preserved). No longer an abort — the user does
        /// not hand-add the device in EAE. Always returns true; just logs.
        /// </summary>
        bool EnsureM262SysdevReady()
        {
            bool exists = false;
            try { exists = M262SysdevEmitter.M262SysdevAlreadyExists(Cfg()); }
            catch { /* treat as absent — the Mapper bootstraps it below */ }
            AppendActivity(exists
                ? "[Device] M262 sysdev present — preserved (trust binding intact)."
                : "[Device] M262 sysdev absent — Mapper will bootstrap the M262 logical device from scratch.");
            return true;
        }

        static string ReadSysdevId(string sysdevPath)
        {
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(sysdevPath);
                return (string?)doc.Root?.Attribute("ID") ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        void TouchDfbprojToTriggerEaeReload()
        {
            try
            {
                var dfbproj = FindDfbproj(Cfg().SyslayPath2);
                if (dfbproj != null && File.Exists(dfbproj))
                {
                    File.SetLastWriteTime(dfbproj, DateTime.Now);
                    AppendActivity($"[EAE] Touched {Path.GetFileName(dfbproj)} to trigger Reload Solution prompt.");
                }
                else
                {
                    AppendActivity("[EAE] .dfbproj not found; EAE will not auto-detect external changes. Use File > Reload Solution.");
                }
            }
            catch (Exception ex)
            {
                AppendActivity($"[EAE] Failed to touch .dfbproj: {ex.Message}");
            }
        }

        void LogCleanup(SystemInjector.CleanupReport report)
        {
            AppendActivity($"[Cleanup] Removed {report.RemovedFbs.Count} universal FB(s), {report.RemovedConnections} connection(s)");
            foreach (var name in report.RemovedFbs) AppendActivity($"  - {name}");
            AppendActivity($"[Cleanup] Preserved {report.PreservedFbs.Count} non-universal FB(s)");
            foreach (var name in report.PreservedFbs) AppendActivity($"  + {name}");
            foreach (var line in report.DeviceCleanupLog) AppendActivity(line);
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

        async void btnTestStation1_Click(object sender, EventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_loadedControlXmlPath) || !File.Exists(_loadedControlXmlPath))
                { ShowError("Load a Control.xml first via Browse."); return; }
                if (!TryResolveDemonstratorPath(out var syslayPath)) return;
                EnsureM262SysdevReady();  // logs preserve-vs-bootstrap; never aborts (Mapper owns the device)

                Cfg().SimulatorFullSystem = false;
                Cfg().UseRecipeStruct = true;
                MapperConfig.SimulatorRecipeMode = false;

                lblStatus.Text = "Generating...";
                AppendActivity($"[Test Feed Station] Generating into Demonstrator at {syslayPath}...");
                AppendActivity("[Test Runtime] Hardware mode forced: SimulatorFullSystem=false; RecipeStep data-array carrier active; physical IO/sensor wiring and rig HOME-FIRST recipe waits are active.");

                var injector = new SystemInjector();
                var cleanup = await Task.Run(() => injector.PrepareDemonstratorForGeneration(Cfg()));
                LogCleanup(cleanup);

                // Deploy templates after cleanup. The cleanup step deletes flat
                // root-level Basic FB files such as FiveStateActuator.fbt; deploying
                // before it lets the patched FiveState core disappear from the
                // generated EAE project.
                await DeployUniversalTemplatesAsync();

                var bindings = TryLoadBindings();
                SystemInjector.BindingApplicationReport report = null!;
                var path = await Task.Run(() =>
                    injector.GenerateStation1TestSyslay(Cfg(), _loadedControlXmlPath, bindings, out report));
                LogBindingsReport(report);
                await FinalizeM262StackAsync();

                // Patch the deployed M262 .hcf so EAE picks up the symbolic-link
                // bindings on reload. Each rewritten pin lands in
                // report.HcfPinAssignments which we mirror to the Activity panel
                // as one '[Hcf] <pin> <- <value>' line. Skip reasons are already
                // in report.Missing (logged by LogBindingsReport above) but we
                // refresh the missing-tail here to surface the [Hcf] entries
                // added by the patch itself.
                // Emit the canonical event + data wires into the M262 sysres
                // FBNetwork (init chain + adapter wires + Pusher I/O bindings).
                // Without these, EAE deploys but nothing initialises and the
                // pusher never moves.
                int wireCountBefore = report.Missing.Count;
                await Task.Run(() => M262SysresWireEmitter.Emit(Cfg(), report));
                // Layout grid is sysres-only — syslay coordinates left
                // untouched so the user can lay out the application canvas
                // freely without Mapper overwriting on every Button 2.
                for (int i = wireCountBefore; i < report.Missing.Count; i++)
                {
                    var line = report.Missing[i];
                    if (line.StartsWith("[Wire]") || line.StartsWith("[Sysres"))
                        AppendActivity(line);
                }

                // Station 2 — wire the M580 + BX1 sysres FBNetworks with the
                // same proven topology (init chain + CaS station chain + state
                // report ring) using each PLC's own structural FBs. Additive:
                // does NOT touch the M262 sysres or the shared syslay, so the
                // M262 Feed-Station wiring above stays byte-identical. The M580
                // gets the full chain (Station2/Assembly_Station/Stn2_Term); the
                // BX1 has no Station FB this increment so it gets only the INIT
                // fan-out + report ring among its cover actuators.
                //
                // FIRST re-mirror the Station-2 FBs from the freshly regenerated
                // syslay onto the M580/BX1 sysres, so each component's CAT type is
                // synced on EVERY Test Runtime (e.g. Bearing_PnP flipping
                // Seven_State <-> Five_State). This path previously only RE-WIRED
                // Station 2 and never re-mirrored the FBs, so the M580 Bearing_PnP
                // kept a stale Type that mismatched the Five_State syslay -> EAE
                // "Solution Integrity: Found References to Missing Instances:
                // Bearing_PnP". The mirror MUST run before the wiring (it creates/
                // updates the FBs the wiring connects). Same call the full-system
                // path uses (~line 607).
                try
                {
                    var s2m = await Task.Run(() => Station2SysresMirror.EmitStation2Sysres(Cfg()));
                    AppendActivity($"[Stn2] re-mirrored FBs -> M580:{s2m.M580} BX1:{s2m.BX1} (CAT types synced to syslay)");
                }
                catch (Exception ex)
                {
                    AppendActivity($"[Stn2][Error] sysres mirror: {ex.Message}");
                }

                try
                {
                    int s2WireBefore = report.Missing.Count;
                    await Task.Run(() =>
                        Station2WireEmitter.EmitStation2Resources(Cfg(), report));
                    for (int i = s2WireBefore; i < report.Missing.Count; i++)
                    {
                        var line = report.Missing[i];
                        if (line.StartsWith("[Wire][M580]") || line.StartsWith("[M580]") ||
                            line.StartsWith("[Wire][BX1]") || line.StartsWith("[BX1]") ||
                            line.StartsWith("[Wire][Stn2]"))
                            AppendActivity(line);
                    }
                }
                catch (Exception ex)
                {
                    AppendActivity($"[Wire][Stn2][Error] {ex.Message}");
                }

                // BX1 EtherNet/IP cover-I/O broker (Stage 1): instantiate BX1_IO
                // (PLC_RW_BX1, id F6C04A4BA6FA8593) on the BX1 SubApp + sysres so the
                // .hcf's EIP_Input/Output_Word_1 symlinks resolve. Runs after the
                // Station-2 sysres mirror + wire emit so the cover FBs already exist.
                // Gated; local to BX1, so the M580 run is unaffected.
                if (Cfg().DeployBx1IoBroker)
                {
                    try
                    {
                        int n = await Task.Run(() =>
                            CodeGen.Devices.BX1.Bx1IoBrokerInjector.InjectBx1IoBroker(Cfg(), syslayPath, report));
                        AppendActivity($"[BX1][Broker] BX1_IO injected into {n} artefact(s).");
                    }
                    catch (Exception ex)
                    {
                        AppendActivity($"[BX1][Broker][Error] {ex.Message}");
                    }
                }

                int hcfCountBefore = report.Missing.Count;
                await Task.Run(() => HcfPatchService.PatchDeployed(
                    Cfg(), path, bindings, report));
                for (int i = hcfCountBefore; i < report.Missing.Count; i++)
                {
                    var line = report.Missing[i];
                    if (line.StartsWith("[Hcf]"))
                        AppendActivity(line);
                }

                // Station 2 — bind the deployed M580 + BX1 .hcf channel symlinks
                // (symbol binding only; wiring + recipes untouched). Runs LAST so
                // it patches the deployed copy after M580HwConfigCopier.Copy /
                // BX1HwConfigCopier.Copy (in FinalizeM262StackAsync above) wrote it
                // and after EmitStation2Resources mirrored the Station-2 FBs onto
                // the resources. Approach (B): the IO-bindings xlsx has no
                // Station-2 pin rows, so we can't direct-bind channel→FB.port like
                // M262; instead we unquote each symlink and re-align its resource
                // head to the deployed sysres ID so EAE's Symbolic Link view treats
                // it as a link (full resolution still needs the M580IO / BX1 EIP
                // broker FB on the resource — logged by the binder). Does NOT touch
                // the M262 HcfPatchService. Try/catch per spec.
                try
                {
                    int bindBefore = report.Missing.Count;
                    await Task.Run(() =>
                    {
                        CodeGen.Devices.M580.M580SymbolBinder.BindM580(Cfg(), report);
                        CodeGen.Devices.BX1.BX1SymbolBinder.BindBX1(Cfg(), report);
                    });
                    for (int i = bindBefore; i < report.Missing.Count; i++)
                    {
                        var line = report.Missing[i];
                        if (line.StartsWith("[HcfBind]"))
                            AppendActivity(line);
                    }
                }
                catch (Exception ex)
                {
                    AppendActivity($"[HcfBind][Error] {ex.Message}");
                }

                try
                {
                    var synced = await Task.Run(() =>
                        RuntimeArtifactVerifier.SyncMappedSysresParametersFromSyslay(path, Cfg(), AppendActivity));
                    if (synced > 0)
                        AppendActivity($"[Test Runtime] final sysres parameter sync: {synced} mapped FB(s).");
                }
                catch (Exception ex)
                {
                    AppendActivity($"[Test Runtime][Sync][Warn] final sysres parameter sync failed: {ex.Message}");
                }

                TouchDfbprojToTriggerEaeReload();

                AppendActivity($"Generated: {path}");
                lblStatus.Text = $"Ready  |  {path}  |  {report.Bound.Count} bound, {report.Missing.Count} unbound";
                MessageBox.Show($"Generated Test Feed Station into Demonstrator:\n{path}\n\n{report.Bound.Count} bound, {report.Missing.Count} unbound.",
                    "Test Feed Station", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendActivity($"[Error] {ex}");
                lblStatus.Text = "Ready";
                ShowError(ex.Message);
            }
        }

        async void btnCleanDemonstrator_Click(object sender, EventArgs e)
        {
            try
            {
                lblStatus.Text = "Cleaning Demonstrator (deep wipe)...";
                AppendActivity("[Clean] Deep wipe — produces a brand-new-EAE-project state.");

                var demoRepo = @"C:\Demonstrator";

                // EAE is intentionally NOT killed. If it has files open, individual
                // file operations may fail with sharing-violation warnings — those are
                // surfaced in the activity log; everything that can be wiped, is.

                // Step 1 — git reset (tracked → HEAD) + git clean (drop untracked).
                // Only runs if the demonstrator dir is a git repo. Recreated EAE
                // projects often aren't tracked under git — in that case skip
                // the reset/clean and rely on DemonstratorWiper alone.
                if (Directory.Exists(Path.Combine(demoRepo, ".git")))
                {
                    var (resetCode, resetOut) = await Task.Run(() => RunGit(demoRepo, "reset --hard"));
                    AppendActivity($"[Clean] git reset --hard -> exit {resetCode}");
                    if (!string.IsNullOrWhiteSpace(resetOut)) AppendActivity(resetOut.Trim());

                    var (cleanCode, cleanOut) = await Task.Run(() => RunGit(demoRepo, "clean -fd -e *.lock_sln"));
                    AppendActivity($"[Clean] git clean -fd -e *.lock_sln -> exit {cleanCode}");
                    if (!string.IsNullOrWhiteSpace(cleanOut)) AppendActivity(cleanOut.Trim());
                }
                else
                {
                    AppendActivity($"[Clean] {demoRepo} is not a git repo — skipping git reset/clean. Wiper still runs.");
                }

                // Step 2 — deep wipe of FB types + canvas contents (HEAD has FBs in it; reset alone
                // doesn't give us a fresh-project state). Topology/, General/, HMI/ are NOT touched.
                var report = await Task.Run(() => CodeGen.Services.DemonstratorWiper.Wipe(demoRepo));
                foreach (var step in report.Steps) AppendActivity($"[Clean] {step}");
                foreach (var w in report.Warnings) AppendActivity($"[Clean][!] {w}");
                AppendActivity(
                    $"[Clean] summary: {report.FilesEmptied} canvas(es) emptied, " +
                    $"{report.FilesDeleted} FB-type file(s) deleted, " +
                    $"{report.FoldersDeleted} type folder(s) removed, " +
                    $"{report.DfbprojEntriesRemoved} dfbproj entry/entries stripped, " +
                    $"{report.HwConfigFilesDeleted} HwConfiguration file(s) cleared.");

                // After the wipe, run the syslay/sysres cleanup + M262 sysdev
                // Resource-dedup step. The dedup lives in SystemInjector so the
                // same [CleanDevice] logic runs on Button 1/2 — wiring it here
                // means Clean Demonstrator gets it too without duplicating code.
                try
                {
                    var injector = new SystemInjector();
                    var cleanup = await Task.Run(() => injector.PrepareDemonstratorForGeneration(Cfg()));
                    LogCleanup(cleanup);
                }
                catch (Exception ex)
                {
                    AppendActivity($"[Clean][!] Post-wipe Prepare failed: {ex.Message}");
                }

                lblStatus.Text = "Demonstrator wiped";
                AppendActivity(
                    "[Clean] Demonstrator now resembles a brand-new EAE project. " +
                    "Topology preserved; HwConfiguration cleared and the M262 .sysdev " +
                    "Resources block dedup'd — EAE's Devices tree will no longer show " +
                    "duplicate M262_RES nodes.");
            }
            catch (Exception ex)
            {
                AppendActivity($"[Error] {ex}");
                lblStatus.Text = "Ready";
                ShowError(ex.Message);
            }
        }


        static (int exitCode, string output) RunGit(string workingDir, string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, string.IsNullOrEmpty(stderr) ? stdout : stdout + stderr);
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
                    var detail = string.IsNullOrWhiteSpace(ex.Message)
                        ? $"{ex.GetType().FullName} (no message)\n{ex.StackTrace}"
                        : $"{ex.GetType().Name}: {ex.Message}";
                    MapperLogger.Error(detail);
                    MessageBox.Show(detail, "Mapping Rules", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            string tPath = TemplatePath(comp, cfg);
            string tName = string.IsNullOrEmpty(tPath)
                ? "No template found"
                : Path.GetFileName(tPath);

            switch (comp.Type.ToLowerInvariant())
            {
                case "process": return Pass(comp, tName);
                case "robot":
                    // Type=Robot is Control.xml's *category* (manipulator), not a
                    // template choice. Reference SMC_Rig_Expo_withClamp maps:
                    //   CoverGripper            -> Vacuum_Gripper_CAT.fbt (suction)
                    //   Bearing_Gripper / Shaft -> Five_State_Actuator_CAT.fbt (fingers)
                    //   Robot_Pick_And_Place1   -> Robot_Task_CAT.fbt (full arm, 7 states)
                    if (_vacuumGripperNames.Contains(comp.Name))
                        return Pass(comp, "Vacuum_Gripper_CAT.fbt");
                    if (comp.States.Count == 5)
                        return Pass(comp, "Five_State_Actuator_CAT.fbt");
                    if (comp.States.Count == 7)
                        return Pass(comp, "Robot_Task_CAT.fbt");
                    return Fail(comp, "No template found",
                        $"Robot '{comp.Name}' has {comp.States.Count} states — expected 5 (gripper) or 7 (task arm)");
                case "actuator":
                    // Seven_State_Actuator_CAT routing restored 2026-05-21 for
                    // Bearing_PnP. The branched 13-state pattern (assembly path
                    // PARALLEL out of ReturnedHome + disassembly path ALTERNATIVE
                    // out of the same state) collapses onto Seven_State's 7-state
                    // ECC because the physical actuator has only three positions
                    // (Pick, Place, Home) plus two work coils. Detection is by
                    // IsBranchedSevenStateActuator (PARALLEL ∧ ALTERNATIVE on the
                    // resting state) so the rule is name-agnostic — any future
                    // 7+ state branched actuator routes the same way.
                    if (comp.States.Count == 7 || IsBranchedSevenStateActuator(comp))
                        return Pass(comp, "Seven_State_Actuator_CAT.fbt");
                    if (comp.States.Count == 4)
                        return Pass(comp, "Five_State_Actuator_No_Sensors_CAT.fbt");
                    if (comp.States.Count != 5)
                        return Fail(comp, "No template found",
                            $"{comp.States.Count} states — only 4, 5, or 7 (incl. PARALLEL+ALTERNATIVE branched) supported");
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

        /// <summary>
        /// Detects the 13-state "branched swivel" pattern used by Bearing_PnP:
        /// the resting state (typically ReturnedHome) has at least one outgoing
        /// PARALLEL transition AND at least one outgoing ALTERNATIVE transition,
        /// each gated by a different Process condition. The PARALLEL chain runs
        /// 7 main states (assembly: ReturnedHome → TurningPick → AtPick →
        /// TurningPlace → Place → TurningHome → AtHome); the ALTERNATIVE chain
        /// runs 6 reversed states (disassembly: TurningPick2 → AtPick2 →
        /// TurningPlace2 → AtPlace2 → TurningHome2 → AtHome2). The physical
        /// actuator has only three positions (Pick, Place, Home) plus two work
        /// coils, so it fits Seven_State_Actuator_CAT regardless of how many
        /// logical Control.xml states it carries.
        /// </summary>
        static bool IsBranchedSevenStateActuator(VueOneComponent comp)
        {
            foreach (var st in comp.States)
            {
                bool hasParallel = false;
                bool hasAlternative = false;
                foreach (var tr in st.Transitions)
                {
                    if (string.Equals(tr.TransitionType, "PARALLEL", StringComparison.OrdinalIgnoreCase))
                        hasParallel = true;
                    else if (string.Equals(tr.TransitionType, "ALTERNATIVE", StringComparison.OrdinalIgnoreCase))
                        hasAlternative = true;
                }
                if (hasParallel && hasAlternative)
                    return true;
            }
            return false;
        }


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

        void dgvComponents_SelectionChanged(object sender, EventArgs e) { }

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
