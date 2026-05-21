// MainForm_simulator.cs  --  SIMULATOR ("Test Simulator" button) ONLY.
// Split out of MainForm.cs so the simulator pipeline cannot be confused with
// the hardware "Test Runtime" (old "Test Feed Station") path, which stays in
// MainForm.cs and is intentionally left untouched.
//
// SENSOR MODEL (read before "fixing" this). The actuator position sensors are
// held NOT fitted (WorkSensorFitted=FALSE / HomeSensorFitted=FALSE) on
// purpose. Forcing the sensors permanently TRUE deadlocks the
// FiveStateActuator ECC: its INIT state only leaves via (atwork = FALSE) to
// AtHomeInit or (atwork = TRUE AND athome = FALSE) to AtWork, so both sensors
// TRUE strands every actuator in INIT forever, and any static single sensor
// force either deadlocks ToWork to AtWork (which needs atwork TRUE arriving
// in sequence) or skips the motion command entirely. With the sensors not
// fitted the embedded No_Sensor_Handler synthesises atwork / athome from
// toWorkTime / toHomeTime in the correct mutually exclusive sequence, while
// toWorkInterlock=FALSE / toHomeInterlock=FALSE still gate the START of
// motion, so the interlock rule arrays are still fully exercised in
// simulation. This is the only model that both walks and proves the
// interlocks. PartInHopper is held TRUE for the whole run via the injected
// SimHopperForce publisher.
using CodeGen.Configuration;
using CodeGen.IO;
using CodeGen.Models;
using MapperUI.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MapperUI
{
    public partial class MainForm
    {
        // ── Test Station 1 Pusher-Simulator ─────────────────────────────────
        // Identical to btnTestStation1_Click (Button 2): SAME Demonstrator
        // project, SAME artefacts, SAME pipeline. The ONLY difference is one
        // post-step — flip the deployed .sysres Resource Type
        // EMB_RES_ECO -> SIM_RES so EAE runs it on the software simulator
        // instead of the M262 hardware runtime. No separate DemonstratorSim
        // folder, no path swap, no .sln launching. Re-run Button 2 to return
        // the project to hardware mode.
        async void btnGenerateFullSystemSimulator_Click(object sender, EventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_loadedControlXmlPath) || !File.Exists(_loadedControlXmlPath))
                { ShowError("Load a Control.xml first via Browse."); return; }
                if (!TryResolveDemonstratorPath(out var syslayPath)) return;
                if (!EnsureM262SysdevExistsOrAbort()) return;

                // Flip the cfg into simulator-full-system mode so the
                // downstream pipeline (SystemLayoutInjector / Station2DeviceEmitter
                // / ProcessRecipeArrayGenerator) collapses every Control.xml
                // Process into one SIM resource. The hardware path (Button 2 /
                // btnTestStation1) leaves this FALSE so Feed Station emits
                // byte-identical to today's working output.
                Cfg().SimulatorFullSystem = true;

                lblStatus.Text = "Generating (Simulator)...";
                AppendActivity($"[Test Full-System Simulator] Generating into Demonstrator at {syslayPath} — SIMULATOR FULL-SYSTEM mode...");
                AppendActivity(
                    "[Simulator] Full-system collapse: Feed_Station + Assembly_Station + Disassembly " +
                    "all emit as Process_Generic FBs in one SIM resource. M580/BX1 sysdev/sysres/HCF " +
                    "emission skipped. Cross-process handshakes wired Process[i].state_update → " +
                    "Process[j].state_change directly (no PLC_RW gateway, no SIFB).");
                AppendActivity(
                    "[Simulator][KnownLimitation] Per-Process scoped ID registry (today): each Process's " +
                    "Wait1Id values reference its own actuator/sensor ordering. Cross-process Wait1Id " +
                    "references (e.g. Assembly waiting on Feed_Station/TransferReturned) get logged as " +
                    "'out of scope' by ProcessRecipeArrayGenerator.ClassifyState and dropped. The direct " +
                    "Process state_update→state_change event wires fire at the canvas level, but " +
                    "downstream gating still depends on intra-Process conditions surviving. If the " +
                    "simulator stalls at a Process boundary, the next iteration is a global sensors-first " +
                    "ID scheme so cross-process actuator/sensor references resolve.");

                await DeployUniversalTemplatesAsync();

                var injector = new SystemInjector();
                var cleanup = await Task.Run(() => injector.PrepareDemonstratorForGeneration(Cfg()));
                LogCleanup(cleanup);

                var bindings = TryLoadBindings();
                SystemInjector.BindingApplicationReport report = null!;
                var path = await Task.Run(() =>
                    injector.GenerateStation1TestSyslay(Cfg(), _loadedControlXmlPath, bindings, out report));
                LogBindingsReport(report);
                await FinalizeM262StackAsync();

                int wireCountBefore = report.Missing.Count;
                await Task.Run(() => MapperUI.Services.M262SysresWireEmitter.Emit(Cfg(), report));
                for (int i = wireCountBefore; i < report.Missing.Count; i++)
                {
                    var line = report.Missing[i];
                    if (line.StartsWith("[Wire]") || line.StartsWith("[Sysres"))
                        AppendActivity(line);
                }

                int hcfCountBefore = report.Missing.Count;
                await Task.Run(() => MapperUI.Services.HcfPatchService.PatchDeployed(
                    Cfg(), path, bindings, report));
                for (int i = hcfCountBefore; i < report.Missing.Count; i++)
                {
                    var line = report.Missing[i];
                    if (line.StartsWith("[Hcf]"))
                        AppendActivity(line);
                }

                // No .sysres Resource Type flip. EAE has no SIM_RES type
                // (verified: ERR_NO_SUCH_TYPE on Runtime.Management.SIM_RES).
                // Resource stays EMB_RES_ECO; simulator mode = the
                // "Local Test" Active Network Profile picked in EAE.
                //
                // ORDER IS LOAD-BEARING (Defect 1 fix). InjectSimHopperForce
                // re-loads and re-saves BOTH the syslay and the sysres to add
                // the SimHopperForce FB + wires. When the no-sensor override
                // ran BEFORE it, that second save round-tripped the file and
                // EAE Watch came up with WorkSensorFitted=TRUE again (the
                // on-disk syslay still read FALSE, but the sysres EAE compiles
                // had been re-emitted from a pre-override state). The override
                // MUST be the LAST writer of the syslay + sysres so nothing
                // can overwrite it. So: SimHopperForce first, no-sensor
                // override last, then a post-write re-read assertion.

                // Simulator-only post-process: inject a SimHopperForce
                // SYMLINKMULTIVARSRC that publishes 'PartInHopper.Input' =
                // TRUE on Area.INITO, so the hopper sensor reads TRUE at
                // startup and the recipe ring advances without physical I/O.
                // syslay + sysres only, simulator button only.
                int simFb = InjectSimHopperForce(path, Cfg());
                if (simFb > 0)
                {
                    AppendActivity("Simulator hopper forced TRUE via SimHopperForce SYMLINKMULTIVARSRC");
                    AppendActivity("[Simulator][InitSeq] FB1.INITO -> SimHopperForce.INIT -> Area.INIT -> Station1 " +
                        "-> PartInHopper.INIT (FB2 subscribes) -> PartInHopper.INITO -> { Feeder.INIT (chain) + " +
                        "SimHopperForce.REQ (one-shot publish) } — publish lands AFTER FB2 subscribed; FB2.CNF fires, " +
                        "state_table[0]=PartInHopper");
                }
                else
                    AppendActivity("[Simulator][Warn] SimHopperForce not injected (already present or canvas not resolvable).");

                // Simulator-only post-process, LAST writer: in pure sim there
                // is no physical cylinder/sensor, so force WorkSensorFitted=
                // FALSE and HomeSensorFitted=FALSE on every Five_State_
                // Actuator_CAT instance — the actuator's internal
                // No_Sensor_Handler timer path then self-advances the ECC
                // (1->2 after toWorkTime, 3->4 after toHomeTime) with no
                // external sensor. Button 2 (hardware) NEVER calls this; the
                // rig's real sensors close the loop with the Control.xml-
                // derived TRUE values. Runs AFTER InjectSimHopperForce so it
                // is the final write of the syslay + sysres.
                int simNoSensor = OverrideSimActuatorsNoSensor(path, Cfg());
                AppendActivity(simNoSensor > 0
                    ? $"[Simulator] No-sensor mode: WorkSensorFitted/HomeSensorFitted forced FALSE on {simNoSensor} " +
                      "Five_State_Actuator_CAT instance(s) (Feeder/Checker/Transfer) — ECC self-advances via timer"
                    : "[Simulator][Warn] No-sensor override touched 0 actuators (none found or sim syslay missing).");

                // Post-write verification (Defect 1). Re-read the on-disk
                // syslay AND sysres AFTER every generator step has run and
                // assert WorkSensorFitted="FALSE" / HomeSensorFitted="FALSE"
                // on every Five_State_Actuator_CAT instance. The sysres is the
                // artefact EAE actually compiles, so a TRUE there is exactly
                // the "syslay shows FALSE but EAE Watch shows TRUE" symptom.
                // Any violation throws — the deploy aborts loudly rather than
                // shipping a sim build that stalls on a sensor that never
                // closes.
                VerifySimActuatorsNoSensorOrAbort(path, Cfg());
                DumpSimRecipeAndInterlockArrays(path);

                // EAE Solution Integrity needs an opcua.xml stub next to BOTH
                // the syslay AND the deployed sysres. GenerateFeedStationSyslayToPath
                // already emits one next to the syslay; this mirrors it next
                // to the sysres EAE actually compiles so the integrity dialog
                // (the one Alper hit with "Missing Project Files: opcua.xml")
                // doesn't fire on Reload Solution.
                try
                {
                    var sysresDirOpcua = Path.GetDirectoryName(Cfg().SysresPath2);
                    if (!string.IsNullOrEmpty(sysresDirOpcua) && Directory.Exists(sysresDirOpcua))
                    {
                        var sysresFileOpcua = Directory
                            .EnumerateFiles(sysresDirOpcua, "*.sysres", SearchOption.TopDirectoryOnly)
                            .FirstOrDefault();
                        if (sysresFileOpcua != null)
                        {
                            MapperUI.Services.SystemInjector
                                .EnsureOpcuaXmlBesideArtefact(sysresFileOpcua);
                            AppendActivity(
                                $"[Simulator][OPCUA] Emitted opcua.xml stubs beside syslay + sysres " +
                                $"({Path.GetFileName(sysresFileOpcua)}) — Solution Integrity check satisfied.");
                        }
                    }
                }
                catch (Exception opcuaEx)
                {
                    AppendActivity($"[Simulator][OPCUA][Warn] opcua.xml stub emit failed: {opcuaEx.Message}");
                }

                TouchDfbprojToTriggerEaeReload();

                AppendActivity($"Generated (Simulator): {path}");
                AppendActivity("[Simulator] Resource stays EMB_RES_ECO — EAE has no SIM_RES type. " +
                    "Simulator mode = the \"Local Test\" Active Network Profile, selected in EAE.");
                AppendActivity("[Simulator] In EAE: Reload Solution, set Active Network Profile to \"Local Test\", Deploy. " +
                    "After deploy, login and Watch Feed_Station.ProcessEngine CurrentStep / cmd_target_name / cmd_state / CMDREQ. " +
                    "Hopper is forced TRUE on INIT → PartInHopper fires CNF → StateHandling publishes state=1 id=0 on the ring " +
                    "→ ProcessHandler writes state_table[0]=1 → ProcessEngine leaves WAIT_STEP and fires CMDREQ " +
                    "cmd_target_name='feeder' cmd_state=1, proving the recipe arrays end to end.");
                lblStatus.Text = $"Ready (Simulator)  |  {path}  |  {report.Bound.Count} bound, {report.Missing.Count} unbound";
                MessageBox.Show(
                    $"Generated Test Feed Station Simulator into Demonstrator:\n{path}\n\n" +
                    $"{report.Bound.Count} bound, {report.Missing.Count} unbound.\n\n" +
                    "Hopper input is forced TRUE at startup via the injected SimHopperForce " +
                    "SYMLINKMULTIVARSRC (syslay + sysres). Resource stays EMB_RES_ECO — " +
                    "simulator mode is the \"Local Test\" Active Network Profile you pick in EAE.\n\n" +
                    "In EAE: Reload Solution, set Active Network Profile to \"Local Test\", Deploy. " +
                    "After deploy, login and Watch Feed_Station.ProcessEngine CurrentStep / " +
                    "cmd_target_name / cmd_state / CMDREQ — expect cmd_target_name='feeder', " +
                    "cmd_state=1 once the ring advances.",
                    "Test Feed Station Simulator", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendActivity($"[Simulator][Error] {ex}");
                lblStatus.Text = "Ready";
                ShowError(ex.Message);
            }
        }

        // SYMLINKMULTIVARSRC type used by Five_State_Actuator_CAT's Output FB.
        const string SimSymlinkSrcType = "SYMLINKMULTIVARSRC_277E97BEC1451D2C";

        /// <summary>
        /// Simulator-only: inject one <c>SimHopperForce</c>
        /// SYMLINKMULTIVARSRC into the syslay SubAppNetwork and mirror it
        /// into the deployed sysres FBNetwork. It publishes
        /// <c>'PartInHopper.Input' = TRUE</c> once on <c>Area.INITO</c>
        /// (INIT + REQ) and holds it — no E_DELAY/E_CYCLE needed. So the
        /// hopper sensor reads TRUE at startup and the recipe ring advances
        /// without physical I/O. Idempotent (skips if SimHopperForce already
        /// present). Returns 1 if injected, 0 if skipped/unresolvable.
        /// syslay + sysres only — hardware path untouched.
        /// </summary>
        int InjectSimHopperForce(string syslayPath, MapperConfig cfg)
        {
            try
            {
                System.Xml.Linq.XNamespace ns = "https://www.se.com/LibraryElements";

                // Deterministic 16-hex IDs (stable across re-runs).
                static string Hex16(string seed)
                {
                    using var sha = System.Security.Cryptography.SHA1.Create();
                    var b = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(seed));
                    var sb = new System.Text.StringBuilder(16);
                    for (int i = 0; i < 8; i++) sb.Append(b[i].ToString("X2"));
                    return sb.ToString();
                }
                var syslayFbId = Hex16("SimHopperForce|syslay|" + syslayPath);
                var sysresFbId = Hex16("SimHopperForce|sysres|" + syslayPath);

                // Resolve the resource Name from the deployed sysres root.
                // PartInHopper's internal SYMLINKMULTIVARDST subscribes to
                // '$${PATH}Input'; $${PATH} expands relative to the FB
                // instance scope under the resource, so for a top-level
                // PartInHopper the absolute symbol is
                // '{ResourceName}.PartInHopper.Input' (this project's
                // symbolic-link namespace is resource-qualified — cf. the
                // Symbolic Links view showing M262_RES.Feeder.athome). A
                // bare 'PartInHopper.Input' (no resource prefix) does NOT
                // match what the subscriber resolves, so the publish is lost.
                string resourceName = "M262_RES";
                try
                {
                    var rdir0 = Path.GetDirectoryName(cfg.SysresPath2);
                    if (!string.IsNullOrEmpty(rdir0) && Directory.Exists(rdir0))
                    {
                        var rf0 = Directory.EnumerateFiles(rdir0, "*.sysres",
                            SearchOption.TopDirectoryOnly).FirstOrDefault();
                        if (rf0 != null)
                        {
                            var rn = (string?)System.Xml.Linq.XDocument.Load(rf0)
                                .Root?.Attribute("Name");
                            if (!string.IsNullOrWhiteSpace(rn)) resourceName = rn!;
                        }
                    }
                }
                catch { /* fall back to M262_RES */ }
                var hopperSymbol = $"'{resourceName}.PartInHopper.Input'";
                var expectedExpansion = $"'{resourceName}.PartInHopper.Input'"; // $${PATH}Input @ PartInHopper

                System.Xml.Linq.XElement BuildFb(bool forSysres)
                {
                    var fb = new System.Xml.Linq.XElement(ns + "FB",
                        new System.Xml.Linq.XAttribute("ID", forSysres ? sysresFbId : syslayFbId),
                        new System.Xml.Linq.XAttribute("Name", "SimHopperForce"),
                        new System.Xml.Linq.XAttribute("Type", SimSymlinkSrcType),
                        new System.Xml.Linq.XAttribute("Namespace", "Main"));
                    if (forSysres)
                        fb.Add(new System.Xml.Linq.XAttribute("Mapping", syslayFbId));
                    // Park clear of the application FBs (Feeder at 3800,5400
                    // etc.) so it doesn't overlap on the canvas.
                    fb.Add(new System.Xml.Linq.XAttribute("x", "500"),
                           new System.Xml.Linq.XAttribute("y", "900"));
                    // Use the EXACT 2-channel SRC variant the Feeder's own
                    // Output FB uses (SYMLINKMULTIVARSRC_277E97BEC1451D2C ↔
                    // I:=2;VALUE${I}:BOOL,BOOL). The type-name suffix is a
                    // hash of the interface signature, so the params MUST
                    // match the variant compiled in this project — a 1-ch
                    // signature against this type name is unresolvable (red
                    // X). Channel 1 publishes the hopper force; channel 2 is
                    // parked on an unused symbol (no subscriber → inert).
                    fb.Add(new System.Xml.Linq.XElement(ns + "Attribute",
                        new System.Xml.Linq.XAttribute("Name", "Configuration.GenericFBType.InterfaceParams"),
                        new System.Xml.Linq.XAttribute("Value", "Runtime.System#I:=2;VALUE${I}:BOOL,BOOL")));
                    fb.Add(new System.Xml.Linq.XElement(ns + "Parameter",
                        new System.Xml.Linq.XAttribute("Name", "QI"), new System.Xml.Linq.XAttribute("Value", "TRUE")));
                    fb.Add(new System.Xml.Linq.XElement(ns + "Parameter",
                        new System.Xml.Linq.XAttribute("Name", "NAME1"), new System.Xml.Linq.XAttribute("Value", hopperSymbol)));
                    fb.Add(new System.Xml.Linq.XElement(ns + "Parameter",
                        new System.Xml.Linq.XAttribute("Name", "VALUE1"), new System.Xml.Linq.XAttribute("Value", "TRUE")));
                    fb.Add(new System.Xml.Linq.XElement(ns + "Parameter",
                        new System.Xml.Linq.XAttribute("Name", "NAME2"), new System.Xml.Linq.XAttribute("Value", "'SimHopperForce.Spare'")));
                    fb.Add(new System.Xml.Linq.XElement(ns + "Parameter",
                        new System.Xml.Linq.XAttribute("Name", "VALUE2"), new System.Xml.Linq.XAttribute("Value", "FALSE")));
                    return fb;
                }

                // SimHopperForce publish-AFTER-subscribe wiring (simulator
                // path only; hardware never calls this — there is no
                // SimHopperForce on the rig, the TM3 driver publishes the
                // symbol continuously). The SYMLINKMULTIVARDST inside
                // PartInHopper (FB2) only subscribes on its own INIT and only
                // confirms a publish that arrives AFTER it has subscribed
                // (Audit-confirmed: the prior "publish before the chain"
                // wiring lost the one-shot value). So: the SRC inits early
                // (symbol registered), the app chain runs and
                // PartInHopper.INIT subscribes FB2, then PartInHopper.INITO
                // fires the SRC's one-shot REQ publish — landing on an
                // already-subscribed FB2. AddEventConns rebuilds these wires
                // defensively (every prior SimHopperForce wire stripped first)
                // so re-running the button never duplicates. Hardware path
                // never calls this method.
                void AddEventConns(System.Xml.Linq.XElement net)
                {
                    var ec = net.Element(ns + "EventConnections");
                    if (ec == null) { ec = new System.Xml.Linq.XElement(ns + "EventConnections"); net.Add(ec); }

                    void Remove(string s, string d)
                    {
                        foreach (var c in ec.Elements(ns + "Connection").Where(c =>
                            (string?)c.Attribute("Source") == s &&
                            (string?)c.Attribute("Destination") == d).ToList())
                            c.Remove();
                    }

                    bool Has(string s, string d) => ec.Elements(ns + "Connection").Any(c =>
                        (string?)c.Attribute("Source") == s &&
                        (string?)c.Attribute("Destination") == d);

                    void Add(string s, string d)
                    {
                        if (!Has(s, d))
                            ec.Add(new System.Xml.Linq.XElement(ns + "Connection",
                                new System.Xml.Linq.XAttribute("Source", s),
                                new System.Xml.Linq.XAttribute("Destination", d)));
                    }

                    // Defensive idempotent rebuild: strip EVERY existing
                    // SimHopperForce-related connection (any prior round /
                    // topology) plus the shared emitter's FB1.INITO ->
                    // Area.INIT bridge, then emit the publish-after-subscribe
                    // wiring from scratch — re-running the button never
                    // duplicates.
                    //   FB1.INITO            -> SimHopperForce.INIT  (SRC inits early; symbol registered)
                    //   SimHopperForce.INITO -> Area.INIT            (app chain proceeds after SRC init)
                    //   PartInHopper.INITO   -> SimHopperForce.REQ   (one-shot publish AFTER FB2 subscribed)
                    // The shared emitter's PartInHopper.INITO -> Feeder.INIT
                    // is left intact (parallel fan-out from the same event
                    // source is valid in EAE). The old self-fire
                    // SimHopperForce.INITO -> SimHopperForce.REQ and the
                    // SimHopperForce.CNF -> Area.INIT gate are gone.
                    foreach (var c in ec.Elements(ns + "Connection").Where(c =>
                        (((string?)c.Attribute("Source")) ?? string.Empty).Contains("SimHopperForce") ||
                        (((string?)c.Attribute("Destination")) ?? string.Empty).Contains("SimHopperForce"))
                        .ToList())
                        c.Remove();
                    Remove("FB1.INITO", "Area.INIT"); // shared emitter bridge -> rerouted via SimHopperForce.INIT

                    Add("FB1.INITO", "SimHopperForce.INIT");
                    Add("SimHopperForce.INITO", "Area.INIT");
                    Add("PartInHopper.INITO", "SimHopperForce.REQ");
                }

                void Save(System.Xml.Linq.XDocument doc, string p)
                {
                    var settings = new System.Xml.XmlWriterSettings
                    {
                        OmitXmlDeclaration = false,
                        Indent = true,
                        Encoding = new System.Text.UTF8Encoding(false),
                    };
                    using var fs = new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.Read);
                    using var w = System.Xml.XmlWriter.Create(fs, settings);
                    doc.Save(w);
                }

                // ── syslay ──────────────────────────────────────────────
                if (string.IsNullOrEmpty(syslayPath) || !File.Exists(syslayPath)) return 0;
                var sdoc = System.Xml.Linq.XDocument.Load(syslayPath, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var snet = sdoc.Root?.Element(ns + "SubAppNetwork") ?? sdoc.Root?.Element(ns + "FBNetwork");
                if (snet == null) return 0;
                // Add the FB only if absent, but ALWAYS (idempotently)
                // ensure the wires — SimHopperForce is not in CleanFile's
                // removal lists so the FB can persist across regens; gating
                // the wire emission behind "FB absent" left the FB present
                // but unwired. AddEventConns removes stale wires and adds the
                // two chain-end wires only if missing, so re-running is safe.
                if (!snet.Elements(ns + "FB").Any(f => (string?)f.Attribute("Name") == "SimHopperForce"))
                {
                    var firstConn = snet.Element(ns + "EventConnections")
                        ?? snet.Element(ns + "DataConnections")
                        ?? snet.Element(ns + "AdapterConnections");
                    if (firstConn != null) firstConn.AddBeforeSelf(BuildFb(false));
                    else snet.Add(BuildFb(false));
                }
                AddEventConns(snet);
                Save(sdoc, syslayPath);

                // ── sysres (the actual deployed file, EAE may have renamed) ─
                var sysresDir = Path.GetDirectoryName(cfg.SysresPath2);
                if (!string.IsNullOrEmpty(sysresDir) && Directory.Exists(sysresDir))
                {
                    var sysresFile = Directory
                        .EnumerateFiles(sysresDir, "*.sysres", SearchOption.TopDirectoryOnly)
                        .FirstOrDefault();
                    if (sysresFile != null)
                    {
                        var rdoc = System.Xml.Linq.XDocument.Load(sysresFile, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                        var rnet = rdoc.Root?.Element(ns + "FBNetwork");
                        if (rnet != null)
                        {
                            // FB only if absent; wires ALWAYS (idempotent).
                            // The previous "skip everything if FB present"
                            // gate is exactly why the sysres ended up with
                            // the FB but zero SimHopperForce EventConnections.
                            if (!rnet.Elements(ns + "FB").Any(f => (string?)f.Attribute("Name") == "SimHopperForce"))
                            {
                                var firstR = rnet.Element(ns + "EventConnections")
                                    ?? rnet.Element(ns + "DataConnections")
                                    ?? rnet.Element(ns + "AdapterConnections");
                                if (firstR != null) firstR.AddBeforeSelf(BuildFb(true));
                                else rnet.Add(BuildFb(true));
                            }
                            AddEventConns(rnet);
                            Save(rdoc, sysresFile);
                        }
                    }
                }

                // Verification: SimHopperForce.NAME1 (publisher) must equal
                // the absolute symbol PartInHopper's internal
                // SYMLINKMULTIVARDST '$${PATH}Input' resolves to. Surface
                // both so any future scope/prefix drift is obvious in the
                // Activity panel on the very next deploy.
                bool symbolMatch = string.Equals(hopperSymbol, expectedExpansion, StringComparison.Ordinal);
                AppendActivity(
                    $"[Simulator][SymCheck] SimHopperForce.NAME1={hopperSymbol} ; " +
                    $"PartInHopper '$${{PATH}}Input' expands to {expectedExpansion} ; " +
                    (symbolMatch ? "MATCH" : "MISMATCH — hopper force will NOT reach PartInHopper, fix resource prefix"));

                return 1;
            }
            catch (Exception ex)
            {
                AppendActivity($"[Simulator][Warn] SimHopperForce inject failed: {ex.GetType().Name}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Simulator-only (Button 4) override: force WorkSensorFitted=FALSE
        /// and HomeSensorFitted=FALSE on every Five_State_Actuator_CAT
        /// instance in the deployed sim syslay AND sysres. In pure simulation
        /// there is no physical cylinder/sensor, so the actuator's internal
        /// No_Sensor_Handler timer path must drive the ECC (state 1->2 after
        /// toWorkTime, 3->4 after toHomeTime) instead of waiting forever on an
        /// atwork/athome sensor that never closes. Applies to Feeder, Checker,
        /// Transfer. Button 2 (hardware) NEVER calls this — the rig's real
        /// sensors must close the loop, so BuildActuatorParameters'
        /// Control.xml-derived TRUE values stand untouched. Post-process only:
        /// shared recipe/param generator, templates and InterlockManager
        /// wiring are not touched. Idempotent — sets the existing Parameter
        /// Value to FALSE and de-dupes any doubled entry, so re-running
        /// Button 4 emits the same FALSE cleanly. Returns the count overridden.
        /// </summary>
        int OverrideSimActuatorsNoSensor(string syslayPath, MapperConfig cfg)
        {
            try
            {
                System.Xml.Linq.XNamespace ns = "https://www.se.com/LibraryElements";

                int ForceNoSensor(System.Xml.Linq.XElement net)
                {
                    int n = 0;
                    foreach (var fb in net.Elements(ns + "FB")
                        .Where(f => (string?)f.Attribute("Type") == "Five_State_Actuator_CAT"))
                    {
                        foreach (var pname in new[] { "WorkSensorFitted", "HomeSensorFitted" })
                        {
                            var ps = fb.Elements(ns + "Parameter")
                                .Where(p => (string?)p.Attribute("Name") == pname).ToList();
                            if (ps.Count == 0)
                            {
                                fb.Add(new System.Xml.Linq.XElement(ns + "Parameter",
                                    new System.Xml.Linq.XAttribute("Name", pname),
                                    new System.Xml.Linq.XAttribute("Value", "FALSE")));
                            }
                            else
                            {
                                ps[0].SetAttributeValue("Value", "FALSE");
                                for (int i = 1; i < ps.Count; i++) ps[i].Remove(); // de-dupe → idempotent
                            }
                        }
                        n++;
                    }
                    return n;
                }

                void Save(System.Xml.Linq.XDocument doc, string p)
                {
                    var settings = new System.Xml.XmlWriterSettings
                    {
                        OmitXmlDeclaration = false,
                        Indent = true,
                        Encoding = new System.Text.UTF8Encoding(false),
                    };
                    using var fs = new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.Read);
                    using var w = System.Xml.XmlWriter.Create(fs, settings);
                    doc.Save(w);
                }

                int total = 0;

                // ── syslay ──────────────────────────────────────────────
                if (string.IsNullOrEmpty(syslayPath) || !File.Exists(syslayPath)) return 0;
                var sdoc = System.Xml.Linq.XDocument.Load(syslayPath, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var snet = sdoc.Root?.Element(ns + "SubAppNetwork") ?? sdoc.Root?.Element(ns + "FBNetwork");
                if (snet != null)
                {
                    total = ForceNoSensor(snet);
                    Save(sdoc, syslayPath);
                }

                // ── sysres (the actual runtime artefact EAE compiles) ───
                var sysresDir = Path.GetDirectoryName(cfg.SysresPath2);
                if (!string.IsNullOrEmpty(sysresDir) && Directory.Exists(sysresDir))
                {
                    var sysresFile = Directory
                        .EnumerateFiles(sysresDir, "*.sysres", SearchOption.TopDirectoryOnly)
                        .FirstOrDefault();
                    if (sysresFile != null)
                    {
                        var rdoc = System.Xml.Linq.XDocument.Load(sysresFile, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                        var rnet = rdoc.Root?.Element(ns + "FBNetwork");
                        if (rnet != null)
                        {
                            ForceNoSensor(rnet);
                            Save(rdoc, sysresFile);
                        }
                    }
                }

                return total;
            }
            catch (Exception ex)
            {
                AppendActivity($"[Simulator][Warn] No-sensor override failed: {ex.GetType().Name}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Defect 1 post-write assertion. Re-reads the on-disk syslay AND the
        /// deployed sysres AFTER every generator step (including the no-sensor
        /// override, which is now the LAST writer) and asserts every
        /// Five_State_Actuator_CAT instance has WorkSensorFitted="FALSE" and
        /// HomeSensorFitted="FALSE". The sysres is the artefact EAE compiles,
        /// so a stale TRUE there — even when the syslay reads FALSE — is
        /// exactly the "syslay shows FALSE but EAE Watch shows TRUE" failure.
        /// On ANY violation (missing param, wrong value, or no actuators found
        /// at all) this logs a loud Activity-panel error and throws, aborting
        /// the simulator deploy rather than shipping a build that stalls on a
        /// sensor that never closes. Simulator path only — Button 2 never
        /// calls this.
        /// </summary>
        void VerifySimActuatorsNoSensorOrAbort(string syslayPath, MapperConfig cfg)
        {
            System.Xml.Linq.XNamespace ns = "https://www.se.com/LibraryElements";
            var violations = new List<string>();

            // (artefact-label, file-path) pairs. The sysres path is resolved
            // the same way OverrideSimActuatorsNoSensor / InjectSimHopperForce
            // resolve it (EAE may have renamed the *.sysres in that dir).
            var targets = new List<(string label, string file)>();
            if (!string.IsNullOrEmpty(syslayPath) && File.Exists(syslayPath))
                targets.Add(("syslay", syslayPath));
            else
                violations.Add($"syslay not found on disk at '{syslayPath}' — cannot verify the no-sensor override.");

            string? sysresFile = null;
            var sysresDir = Path.GetDirectoryName(cfg.SysresPath2);
            if (!string.IsNullOrEmpty(sysresDir) && Directory.Exists(sysresDir))
                sysresFile = Directory
                    .EnumerateFiles(sysresDir, "*.sysres", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();
            if (sysresFile != null)
                targets.Add(("sysres", sysresFile));
            else
                violations.Add(
                    "deployed .sysres not found — cannot verify the no-sensor override on the " +
                    "artefact EAE actually compiles (this is the file EAE Watch reflects).");

            int actuatorsChecked = 0;
            foreach (var (label, file) in targets)
            {
                System.Xml.Linq.XDocument doc;
                try { doc = System.Xml.Linq.XDocument.Load(file); }
                catch (Exception ex)
                {
                    violations.Add($"{label} '{Path.GetFileName(file)}' could not be re-read: {ex.Message}");
                    continue;
                }
                var net = doc.Root?.Element(ns + "SubAppNetwork")
                          ?? doc.Root?.Element(ns + "FBNetwork");
                if (net == null)
                {
                    violations.Add($"{label} '{Path.GetFileName(file)}' has no SubAppNetwork/FBNetwork.");
                    continue;
                }

                var actuators = net.Elements(ns + "FB")
                    .Where(f => (string?)f.Attribute("Type") == "Five_State_Actuator_CAT")
                    .ToList();
                if (actuators.Count == 0)
                    violations.Add(
                        $"{label} '{Path.GetFileName(file)}' contains ZERO Five_State_Actuator_CAT " +
                        "instances — the simulator recipe must drive Feeder/Checker/Transfer; an " +
                        "empty actuator set means the generator did not emit the actuators.");

                foreach (var fb in actuators)
                {
                    actuatorsChecked++;
                    var fbName = (string?)fb.Attribute("Name") ?? "(unnamed)";
                    foreach (var pname in new[] { "WorkSensorFitted", "HomeSensorFitted" })
                    {
                        var ps = fb.Elements(ns + "Parameter")
                            .Where(p => (string?)p.Attribute("Name") == pname).ToList();
                        if (ps.Count == 0)
                        {
                            violations.Add($"{label}: {fbName}.{pname} parameter is MISSING (expected FALSE).");
                            continue;
                        }
                        if (ps.Count > 1)
                            violations.Add(
                                $"{label}: {fbName}.{pname} has {ps.Count} duplicate Parameter entries " +
                                "(override de-dupe did not run last).");
                        foreach (var p in ps)
                        {
                            var v = ((string?)p.Attribute("Value") ?? string.Empty).Trim();
                            if (!string.Equals(v, "FALSE", StringComparison.OrdinalIgnoreCase))
                                violations.Add(
                                    $"{label}: {fbName}.{pname}=\"{v}\" — expected \"FALSE\". A later " +
                                    "pipeline step overwrote the no-sensor override.");
                        }
                    }
                }
            }

            if (violations.Count > 0)
            {
                AppendActivity(
                    "[Simulator][ABORT] No-sensor override verification FAILED — the simulator " +
                    "build would stall on a sensor that never closes. The override must be the " +
                    "LAST writer of the syslay + sysres. Violations:");
                foreach (var v in violations)
                    AppendActivity($"  ✗ {v}");
                throw new InvalidOperationException(
                    "Simulator no-sensor override verification failed (" + violations.Count +
                    " violation(s)); see Activity log. WorkSensorFitted/HomeSensorFitted must be " +
                    "FALSE on every Five_State_Actuator_CAT in both the syslay and the deployed " +
                    "sysres. Deploy aborted.");
            }

            AppendActivity(
                $"[Simulator][Verify] No-sensor override CONFIRMED on disk: WorkSensorFitted=FALSE " +
                $"& HomeSensorFitted=FALSE on all {actuatorsChecked} Five_State_Actuator_CAT " +
                $"instance(s) across {targets.Count} artefact(s) (syslay + sysres). Override is the " +
                "last writer; EAE Watch will read FALSE.");
        }




        // Surfaces the emitted interlock rule arrays and the recipe (process
        // data) arrays from the freshly generated simulator syslay into the
        // Activity log, so both can be verified without opening EAE.
        void DumpSimRecipeAndInterlockArrays(string syslayPath)
        {
            try
            {
                if (string.IsNullOrEmpty(syslayPath) || !File.Exists(syslayPath)) return;
                System.Xml.Linq.XNamespace ns = "https://www.se.com/LibraryElements";
                var doc = System.Xml.Linq.XDocument.Load(syslayPath);
                var net = doc.Root?.Element(ns + "SubAppNetwork") ?? doc.Root?.Element(ns + "FBNetwork");
                if (net == null) { AppendActivity("[Simulator][Arrays] no FB network in syslay."); return; }

                string PV(System.Xml.Linq.XElement fb, string name) =>
                    ((string?)fb.Elements(ns + "Parameter")
                        .FirstOrDefault(p => (string?)p.Attribute("Name") == name)
                        ?.Attribute("Value")) ?? "(unset)";

                AppendActivity("[Simulator][Arrays] -- Recipe (process data) arrays --");
                foreach (var fb in net.Elements(ns + "FB"))
                {
                    if (!fb.Elements(ns + "Parameter").Any(p => (string?)p.Attribute("Name") == "StepType"))
                        continue;
                    var nm = (string?)fb.Attribute("Name") ?? "(unnamed)";
                    foreach (var a in new[] { "StepType", "CmdTargetName", "CmdStateArr",
                                              "Wait1Id", "Wait1State", "NextStep" })
                        AppendActivity($"  {nm}.{a} = {PV(fb, a)}");
                }

                AppendActivity("[Simulator][Arrays] -- Interlock rule arrays (per actuator) --");
                foreach (var fb in net.Elements(ns + "FB")
                    .Where(f => (string?)f.Attribute("Type") == "Five_State_Actuator_CAT"))
                {
                    var nm = (string?)fb.Attribute("Name") ?? "(unnamed)";
                    AppendActivity(
                        $"  {nm}: RuleCount={PV(fb, "RuleCount")} " +
                        $"From={PV(fb, "RuleFromState")} To={PV(fb, "RuleToState")} " +
                        $"Src={PV(fb, "RuleSourceID")} Blocked={PV(fb, "RuleBlockedState")}");
                }
            }
            catch (Exception ex)
            {
                AppendActivity($"[Simulator][Arrays][Warn] dump failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}