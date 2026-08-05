using CodeGen.Configuration;
using CodeGen.IO;
using CodeGen.Models;
using CodeGen.Validation;
using CodeGen.Devices.M262;
using CodeGen.Devices.M580;
using CodeGen.Devices.BX1;
using CodeGen.Devices.RevPi;
using CodeGen.Devices.Core;
using CodeGen.Mapping;
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
        // In-session Device-column overrides. NOT display-only: btnTestStation1_Click reads this at
        // generation time and converts RevPi picks into MapperConfig.RevPiComponents, which is what
        // re-partitions ComponentRegistry. Not persisted across restarts.
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

        // Components in scope for the Test Runtime button: Feed (M262) + Assembly (M580) + covers (BX1).
        static readonly HashSet<string> _allowedInstances = new(StringComparer.OrdinalIgnoreCase)
        {
            "Feeder", "Checker", "Transfer",
            "PartInHopper", "PartAtChecker",

            "Bearing_PnP",
            "Bearing_Gripper",
            "Shaft_Hr", "Shaft_Vr", "Shaft_Gripper",
            "Clamp",
            "BearingSensor", "ShaftSensor",

            "CoverPNP_Hr", "CoverPNP_Vr",
            "CoverPnp_Gripper",
            "TopCoverSenosr",
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
                // Surface the real cause (incl. a missing file name) instead of an unhandled popup.
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

        // MANDATORY re-run of the M262 stack AFTER the syslay is (re)written: Prepare cleans the
        // .sysres (wiping the earlier FB mirror), so without this the .sysres ends up empty and every
        // FB is unmapped on the EAE canvas.
        async Task FinalizeM262StackAsync()
        {
            // Evaluated once up-front so the sysdev and topology branches see the same decision.
            bool m262DeviceExists = await Task.Run(() =>
                M262SysdevEmitter.M262SysdevAlreadyExists(Cfg()));

            string sysdevId = string.Empty;
            if (MapperConfig.FeedStationController == FeedController.RevPi)
            {
                // RevPi hosts the Feed station: emit the Soft_dPAC device + mirror the Feed FBs onto it.
                // M262 sysdev/topology are NOT emitted (any prior M262 device is removed by the emitter).
                try
                {
                    SystemInjector.BindingApplicationReport rr = new();
                    await Task.Run(() => RevPiDeviceEmitter.EmitDevice(Cfg(), rr));
                    foreach (var m in rr.Missing) AppendActivity(m);
                }
                catch (Exception ex) { AppendActivity($"[RevPi][Error] device emit: {ex.Message}"); }
            }
            else
            {
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
                // Topology always runs: solutionData (trust) is preserved, but Equipment JSON (placement)
                // MUST be re-written every run or the M262dPAC never re-appears after a wipe.
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
            }

            // Partial swap: RevPi coexists WITH M262 — emit the Soft_dPAC device (Feeder/Checker + the
            // RevPI_IO Modbus broker) too, AFTER the M262 device so the System GUID folder exists. M262 is
            // NOT swept in partial mode (RevPiDeviceEmitter skips the sweep when PartialRevPi).
            if (MapperConfig.PartialRevPi)
            {
                try
                {
                    SystemInjector.BindingApplicationReport rr = new();
                    await Task.Run(() => RevPiDeviceEmitter.EmitDevice(Cfg(), rr));
                    foreach (var m in rr.Missing) AppendActivity(m);
                }
                catch (Exception ex) { AppendActivity($"[RevPi][Error] partial device emit: {ex.Message}"); }
            }

            // Station 2 — M580 + BX1 sysdev + sysres + HCF + Topology Equipment JSON. Idempotent.
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

            // Purge EAE's per-device compile cache (bin/obj/snapshot.xml): it caches the OLD layout and
            // surfaces stale "2 instances of EMB_RES_ECO"-class errors EAE's own Clean doesn't flush.
            // Runs early so later emitters write into a clean cache.
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

            // Folders.xml — register M580 + BX1 sysdev GUIDs; a sysdev not listed here is silently
            // dropped from EAE's Solution Explorer + Deploy & Diagnostic.
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

            // BroadcastDomain JSON — pin the Default Network subnet/gateway to the live rig values; the
            // M580 endpoint binds this domain, so a mismatch shows red in EAE's connect dialog.
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

            // The BX1 Equipment binds a non-default domain (DeviceNetwork_1); without its
            // BroadcastDomain JSON on disk EAE rejects the whole topology import. EnsureReferencedDomains
            // creates + registers any referenced-but-undeclared domain.
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

            // Strip stale dfbproj entries whose Include references a hex-stem folder with no matching
            // .sysres on disk (else EAE Solution Integrity lists the orphans as missing project files).
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

            // Topology network — Switch + Wire JSON cabling M262 <-> Switch <-> M580 (else every PLC
            // icon floats). Runs AFTER Station2DeviceEmitter so the Equipment UUIDs the wires
            // reference are on disk.
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

            // Mirror the Station-2 FBs from the syslay onto the M580/BX1 resources + emit the
            // per-resource opcua.xml. Runs AFTER the sysdev/sysres shells exist, else the .sysres stay
            // empty and EAE reports "Repair Instances" / "Missing Project Files".
            try
            {
                var s2 = await Task.Run(() => Station2SysresMirror.EmitStation2Sysres(Cfg()));
                AppendActivity($"[Stn2] mirrored FBs → M580:{s2.M580} BX1:{s2.BX1}");
            }
            catch (Exception ex)
            {
                AppendActivity($"[Stn2][Error] sysres mirror: {ex.Message}");
            }

            // Backfill the opcua.xml companions EAE Solution Integrity requires in every resource
            // folder. Non-destructive.
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

            if (MapperConfig.FeedStationController != FeedController.RevPi)
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

            // M580 HCF — authoritative final pass: copies the authored M580IO.hcf verbatim (re-rooted
            // with the resource ID). Runs AFTER Station2DeviceEmitter so it refills the .hcf even after
            // a wipe. Channel symlinks preserved byte-for-byte.
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

            // BX1 HCF — authoritative final pass (same rationale as M580). Bit decoding lives inside the
            // BX1 SIFB, not the .hcf.
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

            // Final dfbproj hygiene: after a wipe the dfbproj can still reference EAE-owned compile
            // artifacts (opcua/offline/opcuaclient/symlink) deleted with the device folders. Strip the
            // dangling refs (EAE regenerates them on Build); never touches the device files.
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

        // The Mapper owns the M262 logical device: an existing .sysdev is preserved (trust kept), an
        // absent one is bootstrapped with the same GUID + resource id. Always returns true; just logs.
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
                    // A missing IO-bindings xlsx = NO actuator athome/atwork/coil bindings -> every
                    // Five_State sensor unbound, nothing triggers. Self-heal from the project source
                    // if found, else fail loud (never proceed silently).
                    var fileName = Path.GetFileName(path);
                    string? recovered = null;
                    var probe = new DirectoryInfo(AppContext.BaseDirectory);
                    for (int up = 0; up < 6 && probe != null; up++, probe = probe.Parent)
                    {
                        var cand = Path.Combine(probe.FullName, "Input", fileName);
                        if (File.Exists(cand)) { recovered = cand; break; }
                    }
                    if (recovered == null)
                    {
                        AppendActivity($"[IoBindings][ERROR] IO-bindings file '{fileName}' NOT FOUND at {path} and no project copy under any parent Input\\ folder. " +
                            "Actuator athome/atwork/coil channels will NOT bind — the generated HCF will be blank for Feeder/Checker/Transfer and NOTHING WILL TRIGGER. " +
                            "Restore Input\\" + fileName + " (from MapperUI\\MapperUI\\Input or MapperTests\\TestData) and regenerate.");
                        return null;
                    }
                    try { File.Copy(recovered, path, overwrite: true); AppendActivity($"[IoBindings] bin copy was missing — self-healed from {recovered}"); }
                    catch (Exception cx) { AppendActivity($"[IoBindings] using project copy {recovered} (could not restore bin copy: {cx.Message})"); path = recovered; }
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
            // report.Missing is the binders' general diagnostic channel, not a list of unbound components -- the
            // HCF binders write their per-channel results and recipe summaries into it. Labelling every line
            // "No binding for component X; will not bind to physical I/O" invented failures that did not exist
            // (a correctly bound five-state swivel, a recipe summary). Emit the lines as what they are.
            foreach (var line in report.Missing)
                AppendActivity($"[IoBindings] {line}");
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

                Cfg().UseRecipeStruct = true;
                MapperConfig.SimulatorRecipeMode = false;

                // Per-component controller choice: every Feed component DEFAULTS to M262. The user can move
                // Feeder and/or Checker to RevPi via the Device dropdown — ONLY those two are swappable (the
                // RevPi Modbus coupler physically wires only Feeder/Checker/Hopper). The chosen components
                // deploy on a Revolution Pi (Soft_dPAC) while M262 KEEPS everything else -> a 4-controller
                // project (M262+RevPi+M580+BX1). PartInHopper follows automatically (its sensor is on the
                // RevPi coupler). No RevPi picks -> RevPiComponents empty -> pure M262 (byte-identical).
                var revpiComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in _deviceOverrides)
                {
                    if (!string.Equals(kv.Value, "RevPi", StringComparison.OrdinalIgnoreCase)) continue;
                    // Swappable == what the RevPi Modbus coupler actually carries, read from the injector's
                    // own signal tables so this can never drift from PLC_RW_REVPI's interface.
                    if (CodeGen.Devices.RevPi.RevPiIoBrokerInjector.CoveredComponents.Contains(kv.Key))
                        revpiComponents.Add(kv.Key);
                    else
                        AppendActivity($"[Target][!] '{kv.Key}' cannot move to the RevPi — the Modbus coupler (PLC_RW_REVPI) exposes no IO for it; kept on M262.");
                }
                if (revpiComponents.Count > 0) revpiComponents.Add("PartInHopper");
                // The Feed station stays on M262 and the RevPi COEXISTS with it. This is a deliberate
                // contract, not a placeholder: the whole-Feed swap is unsupportable because PLC_RW_REVPI
                // carries IO for only Feeder/Checker/PartInHopper, so relocating the whole station would
                // strand Transfer/Ejector/Robot/PartAtAssembly with no physical IO. The per-component swap
                // IS the RevPi mode; RevPiSelectionValidator rejects the full swap by name.
                MapperConfig.FeedStationController = FeedController.M262;
                MapperConfig.RevPiComponents = revpiComponents;

                // Fail BEFORE generating rather than emitting a RevPi project that cannot actuate.
                CodeGen.Validation.Plan.RevPiSelectionValidator.ThrowIfInvalid();

                // Log the controller decision on EVERY run: a pure-M262 run must be distinguishable in the
                // activity log from one where every RevPi pick was silently rejected.
                if (revpiComponents.Count > 0)
                {
                    var picked = revpiComponents.Where(c =>
                        !string.Equals(c, "PartInHopper", StringComparison.OrdinalIgnoreCase));
                    AppendActivity($"[Target] Feed controller: M262 + Revolution Pi (Soft_dPAC) — {string.Join(", ", picked)} + PartInHopper on the RevPi; M262 keeps the rest (4 controllers).");
                    AppendActivity($"[Target] RevPi endpoints: host {Cfg().RevPiHostIp} (Soft dPAC Manager :8080, EAE 'Manage Soft dPAC') / container {Cfg().RevPiTargetIp} (IEC 61499 runtime, EAE Deploy+Login target).");
                }
                else
                {
                    AppendActivity("[Target] Feed controller: M262 (no components routed to the RevPi).");
                }

                lblStatus.Text = "Generating...";
                AppendActivity($"[Generate] Generating IEC 61499 code end-to-end (Feed · Assembly · Disassembly · covers) into Demonstrator at {syslayPath}...");
                AppendActivity("[Test Runtime] RecipeStep data-array carrier active; physical IO/sensor wiring and rig HOME-FIRST recipe waits are active.");

                // STEP 1 — WIPE THE DEMONSTRATOR OUT: a full clean (git reset/clean if it's a repo, then a
                // deep FB/canvas/device wipe) — the reliable "brand-new EAE project" reset, so no stale
                // FB/recipe/interlock or drifted RevPi tree survives. (The former separate 'Clean
                // Demonstrator' button is folded in here: Generate now cleans first, then generates.)
                await DeepCleanDemonstratorAsync(Cfg(), @"C:\Demonstrator", "Generate");

                // STEP 2 — GENERATE IEC 61499 CODE.
                var injector = new SystemInjector();
                var cleanup = await Task.Run(() => injector.PrepareDemonstratorForGeneration(Cfg()));
                LogCleanup(cleanup);

                // Deploy templates AFTER cleanup: cleanup deletes flat root-level Basic FB files, so
                // deploying before it would drop the patched core.
                await DeployUniversalTemplatesAsync();

                var bindings = TryLoadBindings();
                SystemInjector.BindingApplicationReport report = null!;
                var path = await Task.Run(() =>
                    injector.GenerateStation1TestSyslay(Cfg(), _loadedControlXmlPath, bindings, out report));
                LogBindingsReport(report);
                await FinalizeM262StackAsync();

                // Emit the event + data wires into the M262 sysres FBNetwork (init chain + adapter wires
                // + Pusher I/O bindings); without these EAE deploys but nothing inits.
                int wireCountBefore = report.Missing.Count;
                if (MapperConfig.FeedStationController == FeedController.RevPi)
                    await Task.Run(() => RevPiDeviceEmitter.WireResource(Cfg(), report));
                else
                {
                    await Task.Run(() => M262SysresWireEmitter.Emit(Cfg(), report));
                    // Partial swap: also wire the RevPi feed segment (Feeder/Checker ring + Modbus broker).
                    if (MapperConfig.PartialRevPi)
                        await Task.Run(() => RevPiDeviceEmitter.WireResource(Cfg(), report));
                }
                for (int i = wireCountBefore; i < report.Missing.Count; i++)
                {
                    var line = report.Missing[i];
                    if (line.StartsWith("[Wire]") || line.StartsWith("[Sysres"))
                        AppendActivity(line);
                }

                // Station2SysresMirror MUST run BEFORE Station2WireEmitter (it creates the FBs the
                // wiring connects). Re-mirrors so each component's CAT type is synced every Test Runtime
                // (Bearing_PnP Seven_State <-> Five_State); a stale Type trips "Found References to
                // Missing Instances".
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

                // BX1 EtherNet/IP cover-I/O broker: instantiate BX1_IO (PLC_RW_BX1) so the .hcf's
                // EIP_Input/Output_Word_1 symlinks resolve. Runs after the Station-2 mirror/wire so the
                // cover FBs exist. Gated; local to BX1.
                if (Cfg().DeployBx1IoBroker)
                {
                    try
                    {
                        int n = await Task.Run(() =>
                            CodeGen.Devices.BX1.Bx1IoBrokerInjector.InjectBx1IoBroker(Cfg(), syslayPath, report));
                        AppendActivity($"[BX1][Broker] BX1_IO injected into {n} artefact(s).");
                        // Re-fit the syslay layout so the just-injected BX1_IO is gridded and the BX1
                        // frame re-fits to enclose it.
                        await Task.Run(() =>
                            ResourceWireEmitter.ApplyLayoutToSyslay(syslayPath, report));
                    }
                    catch (Exception ex)
                    {
                        AppendActivity($"[BX1][Broker][Error] {ex.Message}");
                    }
                }

                int hcfCountBefore = report.Missing.Count;
                if (MapperConfig.FeedStationController != FeedController.RevPi)
                    await Task.Run(() => HcfPatchService.PatchDeployed(
                        Cfg(), path, bindings, report));
                for (int i = hcfCountBefore; i < report.Missing.Count; i++)
                {
                    var line = report.Missing[i];
                    if (line.StartsWith("[Hcf]"))
                        AppendActivity(line);
                }

                // Station 2 — bind the deployed M580 + BX1 .hcf channel symlinks (symbol binding only).
                // Runs LAST. The IO-bindings xlsx has no Station-2 pin rows, so instead of direct-
                // binding channel->FB.port (as M262) each symlink is unquoted and its resource head
                // re-aligned to the deployed sysres ID.
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

                // SPLIT-BRAIN GUARD: every deployed .hcf DI/DO binding MUST resolve to an FB + pin on
                // the SAME resource's sysres (a legacy symbolic name or a GUID triple pointing at an
                // absent FB never resolves).
                try
                {
                    var eaeRoot = CodeGen.Devices.Core.EaeProjectLayout.DeriveEaeProjectRoot(Cfg());
                    var hcfViolations = await Task.Run(() =>
                        CodeGen.Devices.Core.HcfReferenceValidator.Validate(eaeRoot));
                    if (hcfViolations.Count == 0)
                        AppendActivity("[Hcf][Validate] PASS — every DI/DO binding resolves to a sysres FB + pin (no split-brain).");
                    else
                    {
                        AppendActivity($"[Hcf][Validate] FAIL — {hcfViolations.Count} split-brain HCF binding(s) reference an FB/pin NOT on the resource:");
                        foreach (var v in hcfViolations)
                            AppendActivity($"  [Hcf][SPLIT-BRAIN] {v}");
                    }
                }
                catch (Exception ex)
                {
                    AppendActivity($"[Hcf][Validate][Error] {ex.Message}");
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

                // Sweep orphan .sysres shells: the device-create + .hcf-id-realignment path can leave an
                // empty "RES0" sysres under the OLD id. Runs LATE so each device folder ends with
                // exactly its live resource.
                try
                {
                    var eaeRootSweep = CodeGen.Devices.Core.EaeProjectLayout.DeriveEaeProjectRoot(Cfg());
                    var swept = await Task.Run(() =>
                        CodeGen.Devices.Core.EaeProjectLayout.SweepOrphanSysres(eaeRootSweep, AppendActivity));
                    if (swept > 0)
                        AppendActivity($"[Sysres][Sweep] removed {swept} orphan .sysres shell(s); EAE refreshes obj/System.hash on the next Build.");
                    // Enforce "max 1 resource per device" (the "Device M580 contains 2 instances of
                    // EMB_RES_ECO" guard). No-op on a clean tree.
                    var deduped = await Task.Run(() =>
                        CodeGen.Devices.Core.EaeProjectLayout.DedupeSysdevResources(eaeRootSweep, AppendActivity));
                    if (deduped > 0)
                        AppendActivity($"[Sysdev][Dedupe] removed {deduped} extra <Resource> entry(ies); each device now declares exactly one resource.");
                }
                catch (Exception ex)
                {
                    AppendActivity($"[Sysres][Sweep][Warn] orphan sysres sweep failed: {ex.Message}");
                }

                // POST-SWEEP dfbproj strip: the orphan-sysres sweep deletes a stale .sysres FILE but not
                // its <Compile …\<oldId>.sysres> dfbproj entry, which then dangles. Re-run the stale-stem
                // strip AFTER the sweep so fileless entries go; the live entry stays.
                try
                {
                    var eaeRootStrip = CodeGen.Devices.Core.EaeProjectLayout.DeriveEaeProjectRoot(Cfg());
                    var dfbStrip = System.IO.Path.Combine(eaeRootStrip, "IEC61499", "IEC61499.dfbproj");
                    int strippedLate = await Task.Run(() =>
                        CodeGen.Devices.Core.DfbprojRegistrar.StripStaleSysresStemEntries(dfbStrip, eaeRootStrip));
                    if (strippedLate > 0)
                        AppendActivity(
                            $"[Sysres][Strip] removed {strippedLate} dangling .dfbproj sysres reference(s) after the orphan sweep " +
                            "(the realigned-away device default id, e.g. BX1 117867…).");
                }
                catch (Exception ex)
                {
                    AppendActivity($"[Sysres][Strip][Warn] post-sweep dfbproj strip failed: {ex.Message}");
                }

                // Strip the dead work1/work2ToHomeTime params: the centre-home CAT's two work-to-home
                // E_DELAY timers feed only ReturnToHomeHandler events the No_Sensor ECC ignores.
                try
                {
                    var eaeRootTimer = CodeGen.Devices.Core.EaeProjectLayout.DeriveEaeProjectRoot(Cfg());
                    int timerStripped = await Task.Run(() =>
                        CodeGen.Devices.Core.EaeProjectLayout.StripStaleHomeTimerParams(eaeRootTimer, AppendActivity));
                    if (timerStripped > 0)
                        AppendActivity(
                            $"[Sysres][TimerStrip] removed {timerStripped} stale work1/work2ToHomeTime param(s) " +
                            "from the centre-home swivel sysres (dead timers; values no longer emitted).");
                }
                catch (Exception ex)
                {
                    AppendActivity($"[Sysres][TimerStrip][Warn] home-timer param strip failed: {ex.Message}");
                }

                // HARD syslay<->sysres<->hcf parity guard: the deployable sysres MUST be a faithful
                // projection of the syslay, else EAE deploys the OLD logic silently. A FAIL usually
                // means EAE held a sysres locked during the sync — close EAE and re-run.
                try
                {
                    var eaeRoot = CodeGen.Devices.Core.EaeProjectLayout.DeriveEaeProjectRoot(Cfg());
                    var parity = await Task.Run(() =>
                        CodeGen.Devices.Core.SyslaySysresParityValidator.Validate(eaeRoot, path));
                    if (parity.Count == 0)
                        AppendActivity("[Parity] PASS — every device sysres mirrors the syslay (FBs + recipes + discharge hcf).");
                    else
                    {
                        AppendActivity($"[Parity] FAIL — {parity.Count} sysres/hcf divergence(s) from the syslay (the deployable LAGS the design):");
                        foreach (var v in parity)
                            AppendActivity($"  [Parity][DIVERGENCE] {v}");

                        var resynced = await Task.Run(() =>
                            RuntimeArtifactVerifier.SyncMappedSysresParametersFromSyslay(path, Cfg(), AppendActivity));
                        if (resynced > 0)
                            AppendActivity($"[Parity] retry sync updated {resynced} mapped sysres FB parameter set(s); re-validating.");

                        parity = await Task.Run(() =>
                            CodeGen.Devices.Core.SyslaySysresParityValidator.Validate(eaeRoot, path));
                        if (parity.Count == 0)
                        {
                            AppendActivity("[Parity] PASS after retry sync — deployable sysres now mirrors the syslay.");
                        }
                        else
                        {
                            foreach (var v in parity)
                                AppendActivity($"  [Parity][STILL-DIVERGED] {v}");
                            throw new InvalidOperationException(
                                "Generated deployable sysres still lags the syslay. Close EAE, rerun Generate IEC61499 Code, and do not deploy this stale tree.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppendActivity($"[Parity][Error] {ex.Message}");
                    throw;
                }

                // Topology address-role guard. An IP collision is only visible ACROSS emitters — the RevPi
                // device and the HMI panel are written by unrelated code paths into the SAME broadcast
                // domain — so no single-file check can catch it. A duplicated Soft dPAC CONTAINER address
                // is fatal (it is EAE's deploy/login target and must be uniquely reachable); duplicated
                // host NICs are reported but tolerated, because the shipped tree and the reference both
                // carry one and both import into EAE.
                try
                {
                    var eaeRootAddr = CodeGen.Devices.Core.EaeProjectLayout.DeriveEaeProjectRoot(Cfg());
                    var addr = await Task.Run(() =>
                        CodeGen.Validation.Output.TopologyAddressValidator.Validate(eaeRootAddr, Cfg()));
                    var addrErrors = addr.Where(v => v.IsError).ToList();
                    foreach (var v in addr) AppendActivity($"  [Addr] {v}");
                    if (addr.Count == 0)
                        AppendActivity("[Addr] PASS — every topology endpoint owns its address within its broadcast domain.");
                    if (addrErrors.Count > 0)
                        throw new InvalidOperationException(
                            $"Generated topology has {addrErrors.Count} fatal address collision(s). Two endpoints " +
                            "cannot share one address in a broadcast domain — fix Config/device.yml and re-generate.");
                }
                catch (Exception ex)
                {
                    AppendActivity($"[Addr][Error] {ex.Message}");
                    throw;
                }

                // MQTT connection matrix — flags configs that cannot reach ReturnCode 0.
                try
                {
                    var eaeRootMqtt = CodeGen.Devices.Core.EaeProjectLayout.DeriveEaeProjectRoot(Cfg());
                    var (mqttRows, mqttFindings) = await Task.Run(() =>
                        CodeGen.Devices.Core.MqttConnectionValidator.Inspect(eaeRootMqtt));
                    foreach (var r in mqttRows)
                        AppendActivity($"[MQTT] {r.Resource}.{r.Fb}: URL={r.Url} ConnectionID={r.ConnectionID} " +
                                       $"ClientIdentifier={r.ClientIdentifier} ValidateCert={r.ValidateCert}");
                    foreach (var f in mqttFindings)
                        AppendActivity($"[MQTT] {f}");
                    if (mqttFindings.Any(f => f.Impossible))
                        AppendActivity("[MQTT] IMPOSSIBLE config flagged — it will NOT reach ReturnCode 0. Fix the URL/mode.");
                }
                catch (Exception ex)
                {
                    AppendActivity($"[MQTT][Error] {ex.Message}");
                }

                // SAFETY: BX1 cover safe-start reaches the TM3BC coupler only if the scanner carries the
                // .210 device; warn loudly if it's missing or EIPSCANNER2.xml is empty.
                try
                {
                    var eaeRootScan = CodeGen.Devices.Core.EaeProjectLayout.DeriveEaeProjectRoot(Cfg());
                    var scan = await Task.Run(() => CodeGen.Devices.BX1.Bx1ScannerValidator.Validate(eaeRootScan));
                    foreach (var l in scan.Lines) AppendActivity(l);
                    if (scan.Fatal)
                        AppendActivity("[BX1][Scanner] FATAL — the BX1 EtherNet/IP scanner would compile " +
                            "EMPTY; the cover I/O and the CoverPNP_Hr safe-start cannot reach the coupler. " +
                            "DO NOT deploy until fixed.");
                }
                catch (Exception ex)
                {
                    AppendActivity($"[BX1][Scanner][Error] {ex.Message}");
                }

                TouchDfbprojToTriggerEaeReload();

                AppendActivity($"Generated: {path}");
                lblStatus.Text = $"Ready  |  {report.Bound.Count} I/O bound  |  {path}";
                MessageBox.Show(
                    "IEC 61499 code generated for the SMC rig — end to end.\n\n" +
                    "Feed_Station (M262)  →  Assembly / Disassembly (M580)  →  Covers (BX1)\n\n" +
                    "Process recipes, interlock safety tables and I/O bindings were emitted across\n" +
                    $"all three controllers ({report.Bound.Count} I/O channel(s) bound).\n\n" +
                    $"Demonstrator:\n{path}\n\n" +
                    "Next: reload the solution in EAE, then Build and Deploy.",
                    "Generate IEC61499 Code", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendActivity($"[Error] {ex}");
                lblStatus.Text = "Ready";
                ShowError(ex.Message);
            }
        }

        // Wipe the Demonstrator out: git reset --hard + clean (only when it's a git repo — recreated EAE
        // projects often aren't; then the wiper alone runs), followed by a deep FB/canvas/device wipe that
        // clears HwConfiguration too. Produces a brand-new-EAE-project state. Called by Generate as its
        // first step — the former standalone 'Clean Demonstrator' button is merged into Generate, so a
        // partial-RevPi switch (Feeder/Checker) always starts from a clean tree and can't inherit drift.
        async Task DeepCleanDemonstratorAsync(MapperConfig cfg, string demoRepo, string tag)
        {
            // EAE is intentionally NOT killed; files it holds open surface as sharing-violation warnings.
            if (Directory.Exists(Path.Combine(demoRepo, ".git")))
            {
                var (resetCode, resetOut) = await Task.Run(() => RunGit(demoRepo, "reset --hard"));
                AppendActivity($"[{tag}] git reset --hard -> exit {resetCode}");
                if (!string.IsNullOrWhiteSpace(resetOut)) AppendActivity(resetOut.Trim());

                var (cleanCode, cleanOut) = await Task.Run(() => RunGit(demoRepo, "clean -fd -e *.lock_sln"));
                AppendActivity($"[{tag}] git clean -fd -e *.lock_sln -> exit {cleanCode}");
                if (!string.IsNullOrWhiteSpace(cleanOut)) AppendActivity(cleanOut.Trim());
            }
            else
            {
                AppendActivity($"[{tag}] {demoRepo} is not a git repo — skipping git reset/clean. Wiper still runs.");
            }

            // Deep wipe of FB types + canvas contents (also deletes devices/app + clears HwConfiguration).
            var report = await Task.Run(() => CodeGen.Services.DemonstratorWiper.Wipe(cfg, demoRepo));
            foreach (var step in report.Steps) AppendActivity($"[{tag}] {step}");
            foreach (var w in report.Warnings) AppendActivity($"[{tag}][!] {w}");
            AppendActivity(
                $"[{tag}] wipe summary: {report.FilesEmptied} canvas(es) emptied, " +
                $"{report.FilesDeleted} FB-type file(s) deleted, " +
                $"{report.FoldersDeleted} type folder(s) removed, " +
                $"{report.DfbprojEntriesRemoved} dfbproj entry/entries stripped, " +
                $"{report.HwConfigFilesDeleted} HwConfiguration file(s) cleared.");
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
                foreach (var comp in _loadedComponents)
                {
                    var vr = Validate(comp, validator, cfg);
                    _validationRows.Add(vr);

                    // Device (PLC) from the registry, unless overridden this session.
                    var reg = CodeGen.Mapping.ComponentRegistry.Get(comp.Name);
                    string dev = _deviceOverrides.TryGetValue(comp.Name, out var ov)
                        ? ov
                        : (reg?.Plc.ToString() ?? "");
                    // An unmapped/unknown device is stored as null (blank) to avoid a DataError.
                    string devCell = (dev == "M262" || dev == "M580" || dev == "BX1" || dev == "RevPi") ? dev : null;
                    int idx = dgvComponents.Rows.Add(comp.Name, comp.Type, vr.TemplateName, devCell);
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
            string tPath = TemplatePath(comp, cfg);
            string tName = string.IsNullOrEmpty(tPath)
                ? "No template found"
                : Path.GetFileName(tPath);

            switch (comp.Type.ToLowerInvariant())
            {
                case "process": return Pass(comp, tName);
                case "robot":
                    // Type=Robot is a category: the task arm gets Robot_Task_CAT, every gripper resolves
                    // through the same routing the generator uses.
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

        // One routing decision for the grid and the generator: TemplateMap owns it, so the
        // displayed CAT can never drift from the one actually emitted.
        static string ResolvedCatFile(VueOneComponent c) =>
            TemplateMap.ResolveActuatorCatType(
                c.Name, c.States.Count, TemplateMap.IsBranchedSevenState(c)) + ".fbt";

        static ComponentValidationRow Pass(VueOneComponent c, string t) =>
            new() { Component = c, TemplateName = t, IsValid = true };

        static ComponentValidationRow Fail(VueOneComponent c, string t, string r) =>
            new() { Component = c, TemplateName = t, IsValid = false, FailReason = r };


        void dgvComponents_SelectionChanged(object sender, EventArgs e) { }

        // Commit a Device combo selection on pick so CellValueChanged fires without leaving the cell.
        void dgvComponents_CurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            if (dgvComponents.IsCurrentCellDirty &&
                dgvComponents.CurrentCell is DataGridViewComboBoxCell)
                dgvComponents.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }

        // A blank/unlisted combo value would raise a formatting error; swallow it.
        void dgvComponents_DataError(object sender, DataGridViewDataErrorEventArgs e) => e.ThrowException = false;

        // Record a Device dropdown override + refresh the summary. LIVE — btnTestStation1_Click reads
        // _deviceOverrides at generation time; a RevPi pick on Feeder/Checker relocates that component.
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

        // Recompute per-device counts and refresh the Mapping Information title.
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

        // Designer anchors don't stretch the body sections to full width on some DPI/AutoScale configs;
        // size them to the client area explicitly on every resize.
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

            // Pin the top-right action button (Generate) to the form's right edge.
            if (btnTestStation1 != null)
                btnTestStation1.Left = ClientSize.Width - margin - btnTestStation1.Width;

            // Both body sections span the full width; Mapping Information takes all remaining height.
            grpValidation.Width = fullW;
            grpMappingInfo.Width = fullW;
            int statusH = statusStrip?.Height ?? 22;
            grpMappingInfo.Height = Math.Max(160, ClientSize.Height - grpMappingInfo.Top - statusH - margin);

            // Components grid ~58% of the Mapping split; the activity log fills the rest.
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
