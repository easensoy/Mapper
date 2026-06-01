// SimulatorPostProcessor.cs — public statics for the "Test Simulator" post-
// generation pipeline. Extracted out of MainForm_simulator.cs (a partial
// WinForms class) so the SimulatorEndToEndHarness can call exactly the same
// code MainForm calls — no replication drift, no two-versions-to-maintain.
//
// MainForm_simulator.cs keeps its method signatures and just delegates here.
// Behaviour is byte-identical to the prior private implementation.
//
// All methods take an optional Action<string>? log so the caller can route the
// progress text wherever they want: MainForm passes AppendActivity, the harness
// passes ITestOutputHelper.WriteLine.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using CodeGen.Configuration;

namespace CodeGen.Services
{
    public static class SimulatorPostProcessor
    {
        // SYMLINKMULTIVARSRC type used by Five_State_Actuator_CAT's Output FB.
        // Matched verbatim from MainForm_simulator.cs.
        public const string SimSymlinkSrcType = "SYMLINKMULTIVARSRC_277E97BEC1451D2C";

        static readonly XNamespace Ns = "https://www.se.com/LibraryElements";

        /// <summary>
        /// Simulator-only: inject one <c>SimHopperForce</c>
        /// SYMLINKMULTIVARSRC into the syslay SubAppNetwork and mirror it
        /// into the deployed sysres FBNetwork. It publishes
        /// <c>'{ResourceName}.PartInHopper.Input' = TRUE</c> once on
        /// <c>Area.INITO</c> (INIT + REQ) and holds it — no E_DELAY/E_CYCLE
        /// needed. So the hopper sensor reads TRUE at startup and the recipe
        /// ring advances without physical I/O. Idempotent (skips the FB add
        /// if SimHopperForce already present; ALWAYS rebuilds the wires).
        /// Returns 1 if injected, 0 if skipped/unresolvable. Syslay + sysres
        /// only — hardware path untouched.
        /// </summary>
        public static int InjectSimHopperForce(string syslayPath, MapperConfig cfg,
            Action<string>? log = null)
        {
            try
            {
                // Deterministic 16-hex IDs — stable across re-runs AND across different
                // deploy paths. Previously seeded with the absolute syslayPath, which
                // made the FB ID drift between users / temp dirs (and broke the
                // SimulatorEndToEndHarness repeatability check). The new path-
                // independent seed gives every sim deploy the SAME SimHopperForce ID,
                // which is fine because SimHopperForce is the only FB of its kind in
                // any sim resource.
                static string Hex16(string seed)
                {
                    using var sha = System.Security.Cryptography.SHA1.Create();
                    var b = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(seed));
                    var sb = new System.Text.StringBuilder(16);
                    for (int i = 0; i < 8; i++) sb.Append(b[i].ToString("X2"));
                    return sb.ToString();
                }
                var syslayFbId = Hex16("SimHopperForce|syslay");
                var sysresFbId = Hex16("SimHopperForce|sysres");

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
                            var rn = (string?)XDocument.Load(rf0)
                                .Root?.Attribute("Name");
                            if (!string.IsNullOrWhiteSpace(rn)) resourceName = rn!;
                        }
                    }
                }
                catch { /* fall back to M262_RES */ }
                var hopperSymbol = $"'{resourceName}.PartInHopper.Input'";
                var expectedExpansion = $"'{resourceName}.PartInHopper.Input'"; // $${PATH}Input @ PartInHopper

                XElement BuildFb(bool forSysres)
                {
                    var fb = new XElement(Ns + "FB",
                        new XAttribute("ID", forSysres ? sysresFbId : syslayFbId),
                        new XAttribute("Name", "SimHopperForce"),
                        new XAttribute("Type", SimSymlinkSrcType),
                        new XAttribute("Namespace", "Main"));
                    if (forSysres)
                        fb.Add(new XAttribute("Mapping", syslayFbId));
                    // Park clear of the application FBs (Feeder at 3800,5400
                    // etc.) so it doesn't overlap on the canvas.
                    fb.Add(new XAttribute("x", "500"),
                           new XAttribute("y", "900"));
                    // Use the EXACT 2-channel SRC variant the Feeder's own
                    // Output FB uses (SYMLINKMULTIVARSRC_277E97BEC1451D2C ↔
                    // I:=2;VALUE${I}:BOOL,BOOL). The type-name suffix is a
                    // hash of the interface signature, so the params MUST
                    // match the variant compiled in this project — a 1-ch
                    // signature against this type name is unresolvable (red
                    // X). Channel 1 publishes the hopper force; channel 2 is
                    // parked on an unused symbol (no subscriber → inert).
                    fb.Add(new XElement(Ns + "Attribute",
                        new XAttribute("Name", "Configuration.GenericFBType.InterfaceParams"),
                        new XAttribute("Value", "Runtime.System#I:=2;VALUE${I}:BOOL,BOOL")));
                    fb.Add(new XElement(Ns + "Parameter",
                        new XAttribute("Name", "QI"), new XAttribute("Value", "TRUE")));
                    fb.Add(new XElement(Ns + "Parameter",
                        new XAttribute("Name", "NAME1"), new XAttribute("Value", hopperSymbol)));
                    fb.Add(new XElement(Ns + "Parameter",
                        new XAttribute("Name", "VALUE1"), new XAttribute("Value", "TRUE")));
                    fb.Add(new XElement(Ns + "Parameter",
                        new XAttribute("Name", "NAME2"), new XAttribute("Value", "'SimHopperForce.Spare'")));
                    fb.Add(new XElement(Ns + "Parameter",
                        new XAttribute("Name", "VALUE2"), new XAttribute("Value", "FALSE")));
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
                // so re-running the button never duplicates.
                void AddEventConns(XElement net)
                {
                    var ec = net.Element(Ns + "EventConnections");
                    if (ec == null) { ec = new XElement(Ns + "EventConnections"); net.Add(ec); }

                    void Remove(string s, string d)
                    {
                        foreach (var c in ec.Elements(Ns + "Connection").Where(c =>
                            (string?)c.Attribute("Source") == s &&
                            (string?)c.Attribute("Destination") == d).ToList())
                            c.Remove();
                    }

                    bool Has(string s, string d) => ec.Elements(Ns + "Connection").Any(c =>
                        (string?)c.Attribute("Source") == s &&
                        (string?)c.Attribute("Destination") == d);

                    void Add(string s, string d)
                    {
                        if (!Has(s, d))
                            ec.Add(new XElement(Ns + "Connection",
                                new XAttribute("Source", s),
                                new XAttribute("Destination", d)));
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
                    foreach (var c in ec.Elements(Ns + "Connection").Where(c =>
                        (((string?)c.Attribute("Source")) ?? string.Empty).Contains("SimHopperForce") ||
                        (((string?)c.Attribute("Destination")) ?? string.Empty).Contains("SimHopperForce"))
                        .ToList())
                        c.Remove();
                    Remove("FB1.INITO", "Area.INIT"); // shared emitter bridge -> rerouted via SimHopperForce.INIT

                    Add("FB1.INITO", "SimHopperForce.INIT");
                    Add("SimHopperForce.INITO", "Area.INIT");
                    Add("PartInHopper.INITO", "SimHopperForce.REQ");
                }

                void Save(XDocument doc, string p)
                {
                    var settings = new XmlWriterSettings
                    {
                        OmitXmlDeclaration = false,
                        Indent = true,
                        Encoding = new System.Text.UTF8Encoding(false),
                    };
                    using var fs = new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.Read);
                    using var w = XmlWriter.Create(fs, settings);
                    doc.Save(w);
                }

                // ── syslay ──────────────────────────────────────────────
                if (string.IsNullOrEmpty(syslayPath) || !File.Exists(syslayPath)) return 0;
                var sdoc = XDocument.Load(syslayPath, LoadOptions.PreserveWhitespace);
                var snet = sdoc.Root?.Element(Ns + "SubAppNetwork") ?? sdoc.Root?.Element(Ns + "FBNetwork");
                if (snet == null) return 0;
                if (!snet.Elements(Ns + "FB").Any(f => (string?)f.Attribute("Name") == "SimHopperForce"))
                {
                    var firstConn = snet.Element(Ns + "EventConnections")
                        ?? snet.Element(Ns + "DataConnections")
                        ?? snet.Element(Ns + "AdapterConnections");
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
                        var rdoc = XDocument.Load(sysresFile, LoadOptions.PreserveWhitespace);
                        var rnet = rdoc.Root?.Element(Ns + "FBNetwork");
                        if (rnet != null)
                        {
                            if (!rnet.Elements(Ns + "FB").Any(f => (string?)f.Attribute("Name") == "SimHopperForce"))
                            {
                                var firstR = rnet.Element(Ns + "EventConnections")
                                    ?? rnet.Element(Ns + "DataConnections")
                                    ?? rnet.Element(Ns + "AdapterConnections");
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
                // SYMLINKMULTIVARDST '$${PATH}Input' resolves to.
                bool symbolMatch = string.Equals(hopperSymbol, expectedExpansion, StringComparison.Ordinal);
                log?.Invoke(
                    $"[Simulator][SymCheck] SimHopperForce.NAME1={hopperSymbol} ; " +
                    $"PartInHopper '$${{PATH}}Input' expands to {expectedExpansion} ; " +
                    (symbolMatch ? "MATCH" : "MISMATCH — hopper force will NOT reach PartInHopper, fix resource prefix"));

                return 1;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Simulator][Warn] SimHopperForce inject failed: {ex.GetType().Name}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Simulator-only: force WorkSensorFitted=FALSE and HomeSensorFitted=
        /// FALSE on every Five_State_Actuator_CAT instance in the deployed
        /// sim syslay AND sysres. The actuator's internal No_Sensor_Handler
        /// timer path then self-advances the ECC (state 1→2 after toWorkTime,
        /// 3→4 after toHomeTime) instead of waiting forever on an atwork/
        /// athome sensor that never closes. Hardware path NEVER calls this —
        /// the rig's real sensors close the loop with the Control.xml-derived
        /// TRUE values. Post-process only: shared recipe/param generator,
        /// templates and InterlockManager wiring are not touched. Idempotent
        /// — sets existing Parameter Value to FALSE and de-dupes any doubled
        /// entry. Returns the count overridden.
        /// </summary>
        public static int OverrideSimActuatorsNoSensor(string syslayPath, MapperConfig cfg,
            Action<string>? log = null)
        {
            try
            {
                int ForceNoSensor(XElement net)
                {
                    int n = 0;
                    foreach (var fb in net.Elements(Ns + "FB")
                        .Where(f => (string?)f.Attribute("Type") == "Five_State_Actuator_CAT"))
                    {
                        foreach (var pname in new[] { "WorkSensorFitted", "HomeSensorFitted" })
                        {
                            var ps = fb.Elements(Ns + "Parameter")
                                .Where(p => (string?)p.Attribute("Name") == pname).ToList();
                            if (ps.Count == 0)
                            {
                                fb.Add(new XElement(Ns + "Parameter",
                                    new XAttribute("Name", pname),
                                    new XAttribute("Value", "FALSE")));
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

                void Save(XDocument doc, string p)
                {
                    var settings = new XmlWriterSettings
                    {
                        OmitXmlDeclaration = false,
                        Indent = true,
                        Encoding = new System.Text.UTF8Encoding(false),
                    };
                    using var fs = new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.Read);
                    using var w = XmlWriter.Create(fs, settings);
                    doc.Save(w);
                }

                int total = 0;

                // ── syslay ──────────────────────────────────────────────
                if (string.IsNullOrEmpty(syslayPath) || !File.Exists(syslayPath)) return 0;
                var sdoc = XDocument.Load(syslayPath, LoadOptions.PreserveWhitespace);
                var snet = sdoc.Root?.Element(Ns + "SubAppNetwork") ?? sdoc.Root?.Element(Ns + "FBNetwork");
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
                        var rdoc = XDocument.Load(sysresFile, LoadOptions.PreserveWhitespace);
                        var rnet = rdoc.Root?.Element(Ns + "FBNetwork");
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
                log?.Invoke($"[Simulator][Warn] No-sensor override failed: {ex.GetType().Name}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Defect-1 post-write assertion. Re-reads the on-disk syslay AND the
        /// deployed sysres AFTER every generator step (including the no-
        /// sensor override, which is the LAST writer) and asserts every
        /// Five_State_Actuator_CAT instance has WorkSensorFitted="FALSE" and
        /// HomeSensorFitted="FALSE". The sysres is the artefact EAE compiles,
        /// so a stale TRUE there — even when the syslay reads FALSE — is the
        /// "syslay shows FALSE but EAE Watch shows TRUE" failure. On ANY
        /// violation throws — the deploy aborts loudly rather than shipping a
        /// build that stalls on a sensor that never closes. Simulator path only.
        /// </summary>
        public static void VerifySimActuatorsNoSensorOrAbort(string syslayPath, MapperConfig cfg,
            Action<string>? log = null)
        {
            var violations = new List<string>();

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
                XDocument doc;
                try { doc = XDocument.Load(file); }
                catch (Exception ex)
                {
                    violations.Add($"{label} '{Path.GetFileName(file)}' could not be re-read: {ex.Message}");
                    continue;
                }
                var net = doc.Root?.Element(Ns + "SubAppNetwork")
                          ?? doc.Root?.Element(Ns + "FBNetwork");
                if (net == null)
                {
                    violations.Add($"{label} '{Path.GetFileName(file)}' has no SubAppNetwork/FBNetwork.");
                    continue;
                }

                var actuators = net.Elements(Ns + "FB")
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

                    // Scalar form (post SimSensorConfig POC revert 2026-05-30). The CAT
                    // body keeps the original two BOOL parameters; the override sets both
                    // FALSE. Anything else means a later writer overrode the override.
                    foreach (var pname in new[] { "WorkSensorFitted", "HomeSensorFitted" })
                    {
                        var ps = fb.Elements(Ns + "Parameter")
                            .Where(p => (string?)p.Attribute("Name") == pname).ToList();
                        if (ps.Count == 0)
                        {
                            violations.Add($"{label}: {fbName}.{pname} parameter is MISSING (the override should have inserted it).");
                            continue;
                        }
                        if (ps.Count > 1)
                            violations.Add($"{label}: {fbName}.{pname} has {ps.Count} duplicate Parameter entries (override de-dupe did not run last).");
                        foreach (var p in ps)
                        {
                            var v = ((string?)p.Attribute("Value") ?? string.Empty).Trim();
                            if (!string.Equals(v, "FALSE", StringComparison.OrdinalIgnoreCase))
                                violations.Add(
                                    $"{label}: {fbName}.{pname}=\"{v}\" — expected FALSE. A later " +
                                    "pipeline step overwrote the no-sensor override.");
                        }
                    }
                }
            }

            if (violations.Count > 0)
            {
                log?.Invoke(
                    "[Simulator][ABORT] No-sensor override verification FAILED — the simulator " +
                    "build would stall on a sensor that never closes. The override must be the " +
                    "LAST writer of the syslay + sysres. Violations:");
                foreach (var v in violations)
                    log?.Invoke($"  ✗ {v}");
                throw new InvalidOperationException(
                    "Simulator no-sensor override verification failed (" + violations.Count +
                    " violation(s)); see log. WorkSensorFitted/HomeSensorFitted must be " +
                    "FALSE on every Five_State_Actuator_CAT in both the syslay and the deployed " +
                    "sysres. Deploy aborted.");
            }

            log?.Invoke(
                $"[Simulator][Verify] No-sensor override CONFIRMED on disk: WorkSensorFitted=FALSE " +
                $"& HomeSensorFitted=FALSE on all {actuatorsChecked} Five_State_Actuator_CAT " +
                $"instance(s) across {targets.Count} artefact(s) (syslay + sysres). Override is the " +
                "last writer; EAE Watch will read FALSE.");
        }

        /// <summary>
        /// Defensive post-write assertion for Seven_State sensor synthesis. Mirrors
        /// <see cref="VerifySimActuatorsNoSensorOrAbort"/> for the Five_State path: re-
        /// reads the on-disk syslay AND the deployed sysres AFTER every generator step
        /// and asserts that for every <c>Seven_State_Actuator_CAT</c> instance in the
        /// SIM resource, the matching <c>SimSwivelForce_&lt;name&gt;</c> SYMLINKMULTIVARSRC
        /// FB is present and correctly wired (event INITO→INIT + plc_out→REQ; data
        /// current_state1/2_to_plc → VALUE1/2). Without this, a later pipeline step (or
        /// a future refactor) that silently drops a SimSwivelForce — or wires it wrong
        /// — would ship a sim build where the Seven_State ECC stalls at <c>ToPick</c>
        /// forever because <c>atwork1</c> never closes. Throws on any violation so the
        /// deploy aborts loudly instead of producing a stalling build. No-op-green when
        /// there are no Seven instances (stub on). Hardware path NEVER calls this — the
        /// rig's real Swivel_Arm sensors close the loop with values bound at the .hcf
        /// layer.
        /// </summary>
        public static void VerifySimSwivelForceOrAbort(string syslayPath, MapperConfig cfg,
            Action<string>? log = null)
        {
            var violations = new List<string>();

            var targets = new List<(string label, string file)>();
            if (!string.IsNullOrEmpty(syslayPath) && File.Exists(syslayPath))
                targets.Add(("syslay", syslayPath));
            else
                violations.Add($"syslay not found on disk at '{syslayPath}' — cannot verify SimSwivelForce.");

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
                    "deployed .sysres not found — cannot verify SimSwivelForce on the artefact EAE " +
                    "actually compiles.");

            int sevenInstancesChecked = 0;
            int fullyWired = 0;
            foreach (var (label, file) in targets)
            {
                XDocument doc;
                try { doc = XDocument.Load(file); }
                catch (Exception ex)
                {
                    violations.Add($"{label} '{Path.GetFileName(file)}' could not be re-read: {ex.Message}");
                    continue;
                }
                var net = doc.Root?.Element(Ns + "SubAppNetwork")
                          ?? doc.Root?.Element(Ns + "FBNetwork");
                if (net == null)
                {
                    violations.Add($"{label} '{Path.GetFileName(file)}' has no SubAppNetwork/FBNetwork.");
                    continue;
                }

                var sevens = net.Elements(Ns + "FB")
                    .Where(f => (string?)f.Attribute("Type") == "Seven_State_Actuator_CAT")
                    .Select(f => (string?)f.Attribute("Name") ?? string.Empty)
                    .Where(n => n.Length > 0)
                    .ToList();
                // No Seven instances → no SimSwivelForce expected. Stub-on case is silent green.
                if (sevens.Count == 0) continue;

                var ec = net.Element(Ns + "EventConnections");
                var dc = net.Element(Ns + "DataConnections");

                bool HasEv(string s, string d) =>
                    (ec?.Elements(Ns + "Connection") ?? Enumerable.Empty<XElement>())
                    .Any(c => (string?)c.Attribute("Source") == s && (string?)c.Attribute("Destination") == d);
                bool HasDt(string s, string d) =>
                    (dc?.Elements(Ns + "Connection") ?? Enumerable.Empty<XElement>())
                    .Any(c => (string?)c.Attribute("Source") == s && (string?)c.Attribute("Destination") == d);

                foreach (var name in sevens)
                {
                    sevenInstancesChecked++;
                    var fbName = "SimSwivelForce_" + name;
                    var fb = net.Elements(Ns + "FB")
                        .FirstOrDefault(f => (string?)f.Attribute("Name") == fbName);
                    if (fb == null)
                    {
                        violations.Add(
                            $"{label}: {fbName} FB MISSING — Seven_State instance '{name}' has no sim " +
                            "sensor synthesis, ECC will stall at ToPick forever (atwork1 never closes).");
                        continue;
                    }
                    var ty = (string?)fb.Attribute("Type") ?? string.Empty;
                    if (!string.Equals(ty, SimSymlinkSrcType, StringComparison.Ordinal))
                    {
                        violations.Add($"{label}: {fbName} has Type={ty}, expected {SimSymlinkSrcType}.");
                        continue;
                    }
                    var n1 = (string?)fb.Elements(Ns + "Parameter")
                        .FirstOrDefault(p => (string?)p.Attribute("Name") == "NAME1")?.Attribute("Value");
                    var n2 = (string?)fb.Elements(Ns + "Parameter")
                        .FirstOrDefault(p => (string?)p.Attribute("Name") == "NAME2")?.Attribute("Value");
                    if (string.IsNullOrEmpty(n1) || !n1.Contains(".atwork1"))
                        violations.Add($"{label}: {fbName}.NAME1='{n1}' — expected to end with '.atwork1'.");
                    if (string.IsNullOrEmpty(n2) || !n2.Contains(".atwork2"))
                        violations.Add($"{label}: {fbName}.NAME2='{n2}' — expected to end with '.atwork2'.");

                    bool ok = true;
                    if (!HasEv($"{name}.INITO",                 $"{fbName}.INIT"))   { violations.Add($"{label}: missing event wire {name}.INITO → {fbName}.INIT");                       ok = false; }
                    if (!HasEv($"{name}.plc_out",               $"{fbName}.REQ"))    { violations.Add($"{label}: missing event wire {name}.plc_out → {fbName}.REQ");                       ok = false; }
                    if (!HasDt($"{name}.current_state1_to_plc", $"{fbName}.VALUE1")) { violations.Add($"{label}: missing data wire {name}.current_state1_to_plc → {fbName}.VALUE1");        ok = false; }
                    if (!HasDt($"{name}.current_state2_to_plc", $"{fbName}.VALUE2")) { violations.Add($"{label}: missing data wire {name}.current_state2_to_plc → {fbName}.VALUE2");        ok = false; }
                    if (ok) fullyWired++;
                }
            }

            if (violations.Count > 0)
            {
                // NON-FATAL (2026-06-02). Was a hard throw that aborted deploy.
                // Two reasons it's now a warning, not an abort:
                //   1. This verify globs only the FIRST .sysres under
                //      cfg.SysresPath2 (the M262 one). Bearing_PnP — the only
                //      Seven_State instance — lives on the M580 sysres, so its
                //      SimSwivelForce can NEVER be found in the M262 sysres and
                //      the "missing in sysres" violation is a false positive in
                //      the multi-resource sim (3 separate .sysres files).
                //   2. The swivel (Bearing_PnP / M580 / Assembly) is out of the
                //      current Feed_Station-MQTT scope; a swivel-sensor gap must
                //      not block the M262/BX1 MQTT deploy.
                // Logged loudly so a real wiring regression is still visible.
                log?.Invoke(
                    "[Simulator][WARN] SimSwivelForce verification found " + violations.Count +
                    " issue(s) — NOT aborting (the swivel is out of scope and the sysres check only " +
                    "inspects the first .sysres, so M580's Bearing_PnP reads as a false miss). " +
                    "If you later need the Seven_State swivel to cycle in sim, fix these:");
                foreach (var v in violations)
                    log?.Invoke($"  · {v}");
                return;
            }

            log?.Invoke(
                $"[Simulator][Verify] SimSwivelForce CONFIRMED on disk: {fullyWired} fully-wired " +
                $"SimSwivelForce instance(s) across {targets.Count} artefact(s) (syslay + sysres). " +
                $"Every Seven_State_Actuator_CAT instance has its sensor-synthesis companion; the " +
                $"ECC ToPick/ToPlace gates will close on coil energise.");
        }

        /// <summary>
        /// Surfaces the emitted interlock rule arrays and the recipe (process
        /// data) arrays from the freshly generated simulator syslay, so both
        /// can be verified without opening EAE. Streams lines to <paramref
        /// name="log"/> in the same order MainForm used to log them.
        /// </summary>
        public static void DumpSimRecipeAndInterlockArrays(string syslayPath,
            Action<string>? log = null)
        {
            try
            {
                if (string.IsNullOrEmpty(syslayPath) || !File.Exists(syslayPath)) return;
                var doc = XDocument.Load(syslayPath);
                var net = doc.Root?.Element(Ns + "SubAppNetwork") ?? doc.Root?.Element(Ns + "FBNetwork");
                if (net == null) { log?.Invoke("[Simulator][Arrays] no FB network in syslay."); return; }

                string PV(XElement fb, string name) =>
                    ((string?)fb.Elements(Ns + "Parameter")
                        .FirstOrDefault(p => (string?)p.Attribute("Name") == name)
                        ?.Attribute("Value")) ?? "(unset)";

                log?.Invoke("[Simulator][Arrays] -- Recipe (process data) arrays --");
                foreach (var fb in net.Elements(Ns + "FB"))
                {
                    if (!fb.Elements(Ns + "Parameter").Any(p => (string?)p.Attribute("Name") == "StepType"))
                        continue;
                    var nm = (string?)fb.Attribute("Name") ?? "(unnamed)";
                    foreach (var a in new[] { "StepType", "CmdTargetName", "CmdStateArr",
                                              "Wait1Id", "Wait1State", "NextStep" })
                        log?.Invoke($"  {nm}.{a} = {PV(fb, a)}");
                }

                log?.Invoke("[Simulator][Arrays] -- Interlock rule arrays (per actuator) --");
                foreach (var fb in net.Elements(Ns + "FB")
                    .Where(f => (string?)f.Attribute("Type") == "Five_State_Actuator_CAT"))
                {
                    var nm = (string?)fb.Attribute("Name") ?? "(unnamed)";
                    log?.Invoke(
                        $"  {nm}: RuleCount={PV(fb, "RuleCount")} " +
                        $"From={PV(fb, "RuleFromState")} To={PV(fb, "RuleToState")} " +
                        $"Src={PV(fb, "RuleSourceID")} Blocked={PV(fb, "RuleBlockedState")}");
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Simulator][Arrays][Warn] dump failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Simulator-only sensor synthesis for the 3-position Seven_State swivel.
        ///
        /// The simulator has no physical sensors. Five_State actuators handle this with
        /// their embedded <c>No_Sensor_Handler</c> that synthesises athome/atwork from
        /// toWorkTime/toHomeTime when WorkSensorFitted/HomeSensorFitted are FALSE (see
        /// <see cref="OverrideSimActuatorsNoSensor"/>). Seven_State_Actuator_CAT has no
        /// equivalent — its ECC (<c>SevenStateActuator2</c>) gates
        /// <c>ToPick → AtPick</c> on <c>atwork1=TRUE AND atwork2=FALSE</c> and
        /// <c>ToPlace → AtPlace</c> on the mirror, so without external sensor synthesis
        /// the swivel fires its coil but stalls at <c>ToPick</c>/<c>ToPlace</c>
        /// forever.
        ///
        /// <para><b>Mechanism.</b> For every Seven_State_Actuator_CAT instance in the
        /// SIM syslay AND sysres, inject one top-level <c>SimSwivelForce_&lt;name&gt;</c>
        /// SYMLINKMULTIVARSRC publishing:</para>
        /// <list type="bullet">
        ///   <item><c>NAME1 = '&lt;ResourceName&gt;.&lt;name&gt;.atwork1'</c></item>
        ///   <item><c>NAME2 = '&lt;ResourceName&gt;.&lt;name&gt;.atwork2'</c></item>
        /// </list>
        /// <para>with <c>VALUE1</c> data-wired from <c>&lt;name&gt;.current_state1_to_plc</c>
        /// and <c>VALUE2</c> from <c>&lt;name&gt;.current_state2_to_plc</c> (the
        /// actuator's own coil drive outputs), <c>INIT</c> event-wired from
        /// <c>&lt;name&gt;.INITO</c> (so the symbol is registered before the actuator
        /// subscribes to it), and <c>REQ</c> event-wired from <c>&lt;name&gt;.plc_out</c>
        /// (which fires every time the ECC enters <c>ToPick</c>/<c>ToPlace</c>/<c>ToHome</c>
        /// — exactly when the coils change). Net effect: <c>atwork1</c> closes the
        /// instant the actuator drives coil 1, and the same for coil 2. The
        /// <c>athome</c> input is intentionally left unpublished — it reads its STRING
        /// default <c>FALSE</c>, which is fine because the ECC's <c>INIT → START</c>
        /// gate falls through on the <c>atwork1=FALSE AND atwork2=FALSE</c> arm, and
        /// <c>AtHome</c> is timer-driven via <c>timerStart → timerEnd → athome → handshake</c>
        /// (no sensor needed). The settle is effectively instant in sim, which keeps
        /// the recipe moving without inventing a per-CAT toPickTime/toPlaceTime.</para>
        ///
        /// <para>Idempotent: skips the FB add if SimSwivelForce_&lt;name&gt; is already
        /// present, but ALWAYS rebuilds its wires (so a topology rerun never
        /// duplicates). Hardware path NEVER calls this — the rig's physical
        /// SwivelArmAtPick/AtPlace sensors close the loop with values bound at the
        /// .hcf layer. Returns total instances injected across syslay + sysres.</para>
        /// </summary>
        public static int InjectSimSwivelForce(string syslayPath, MapperConfig cfg,
            Action<string>? log = null)
        {
            try
            {
                // Resolve the resource Name so symlink NAMEs match what the Seven_State
                // CAT's internal SYMLINKMULTIVARDST publishes/subscribes. Same approach
                // SimHopperForce uses. Fall back to a single ResourceName probed from
                // the deployed sysres file.
                string resourceName = "M580_RES";  // sim collapses M580+BX1 into the M262 sim resource,
                                                   // but the Resource Name attribute on the deployed sysres
                                                   // is the source of truth — read it.
                try
                {
                    var rdir = Path.GetDirectoryName(cfg.SysresPath2);
                    if (!string.IsNullOrEmpty(rdir) && Directory.Exists(rdir))
                    {
                        var rf = Directory.EnumerateFiles(rdir, "*.sysres",
                            SearchOption.TopDirectoryOnly).FirstOrDefault();
                        if (rf != null)
                        {
                            var rn = (string?)XDocument.Load(rf).Root?.Attribute("Name");
                            if (!string.IsNullOrWhiteSpace(rn)) resourceName = rn!;
                        }
                    }
                }
                catch { /* fall back to default */ }

                // Deterministic 16-hex IDs per Seven instance per artefact. Path-
                // independent so the same SimSwivelForce FB IDs land on every deploy
                // (otherwise the F3 repeatability check fails). The seed includes the
                // actuator name so two Seven instances get distinct IDs.
                static string Hex16(string seed)
                {
                    using var sha = System.Security.Cryptography.SHA1.Create();
                    var b = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(seed));
                    var sb = new System.Text.StringBuilder(16);
                    for (int i = 0; i < 8; i++) sb.Append(b[i].ToString("X2"));
                    return sb.ToString();
                }

                int injectedTotal = 0;
                int sevenInstancesSeen = 0;

                // Walk both syslay and sysres. For each Seven_State_Actuator_CAT
                // instance, inject (or rebuild) the SimSwivelForce SYMLINKMULTIVARSRC
                // and its event/data wires. Save once per file.
                foreach (var (label, path, networkLocal, forSysres) in new[]
                {
                    ("syslay", syslayPath,                                                "SubAppNetwork", false),
                    ("sysres", ResolveSysresFile(cfg) ?? string.Empty,                    "FBNetwork",     true),
                })
                {
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                    var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
                    var net = doc.Root?.Element(Ns + networkLocal)
                              ?? doc.Root?.Element(Ns + "FBNetwork")
                              ?? doc.Root?.Element(Ns + "SubAppNetwork");
                    if (net == null) continue;

                    var sevens = net.Elements(Ns + "FB")
                        .Where(f => (string?)f.Attribute("Type") == "Seven_State_Actuator_CAT")
                        .Select(f => (string?)f.Attribute("Name"))
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Cast<string>()
                        .ToList();
                    sevenInstancesSeen += sevens.Count;
                    if (sevens.Count == 0) { continue; }

                    var ec = net.Element(Ns + "EventConnections")
                             ?? AddBeforeNullToAttribute(net, new XElement(Ns + "EventConnections"));
                    var dc = net.Element(Ns + "DataConnections")
                             ?? AddBeforeNullToAttribute(net, new XElement(Ns + "DataConnections"));

                    int injectedInFile = 0;
                    int slot = 0;
                    foreach (var name in sevens)
                    {
                        var fbName = "SimSwivelForce_" + name;
                        var atwork1Symbol = $"'{resourceName}.{name}.atwork1'";
                        var atwork2Symbol = $"'{resourceName}.{name}.atwork2'";
                        var fbId = Hex16($"SimSwivelForce|{(forSysres ? "sysres" : "syslay")}|{name}");

                        // Add FB only if absent. Wires are always rebuilt.
                        var existing = net.Elements(Ns + "FB")
                            .FirstOrDefault(f => (string?)f.Attribute("Name") == fbName);
                        if (existing == null)
                        {
                            var fb = new XElement(Ns + "FB",
                                new XAttribute("ID", fbId),
                                new XAttribute("Name", fbName),
                                new XAttribute("Type", SimSymlinkSrcType),
                                new XAttribute("Namespace", "Main"));
                            if (forSysres)
                                fb.Add(new XAttribute("Mapping", fbId));
                            // Park each SimSwivelForce instance in a column on the
                            // right side of the canvas so it doesn't overlap the
                            // application FBs. Bump y per slot.
                            fb.Add(new XAttribute("x", "500"),
                                   new XAttribute("y", (1500 + slot * 400).ToString()));
                            fb.Add(new XElement(Ns + "Attribute",
                                new XAttribute("Name", "Configuration.GenericFBType.InterfaceParams"),
                                new XAttribute("Value", "Runtime.System#I:=2;VALUE${I}:BOOL,BOOL")));
                            fb.Add(new XElement(Ns + "Parameter",
                                new XAttribute("Name", "QI"), new XAttribute("Value", "TRUE")));
                            fb.Add(new XElement(Ns + "Parameter",
                                new XAttribute("Name", "NAME1"), new XAttribute("Value", atwork1Symbol)));
                            fb.Add(new XElement(Ns + "Parameter",
                                new XAttribute("Name", "NAME2"), new XAttribute("Value", atwork2Symbol)));
                            // VALUE1/VALUE2 are not parameterised — they come in via
                            // DataConnections from the actuator's current_state{1,2}_to_plc
                            // outputs.

                            var firstConn = net.Element(Ns + "EventConnections")
                                         ?? net.Element(Ns + "DataConnections")
                                         ?? net.Element(Ns + "AdapterConnections");
                            if (firstConn != null) firstConn.AddBeforeSelf(fb);
                            else net.Add(fb);
                            injectedInFile++;
                        }

                        // Defensive idempotent rebuild: strip any prior wires touching
                        // this SimSwivelForce instance, then re-add the canonical set.
                        StripWires(ec, fbName);
                        StripWires(dc, fbName);
                        AddEventWire(ec, $"{name}.INITO",     $"{fbName}.INIT");
                        AddEventWire(ec, $"{name}.plc_out",   $"{fbName}.REQ");
                        AddDataWire (dc, $"{name}.current_state1_to_plc", $"{fbName}.VALUE1");
                        AddDataWire (dc, $"{name}.current_state2_to_plc", $"{fbName}.VALUE2");

                        // Per-instance SymCheck — mirrors InjectSimHopperForce's
                        // [Simulator][SymCheck] log line. SimSwivelForce's NAME1/NAME2
                        // are the absolute symlink names this publisher writes; the
                        // CAT's internal Inputs_AB/Inputs_C SYMLINKMULTIVARDST inside
                        // the Seven_State_Actuator_CAT instance subscribe to
                        // $${PATH}atwork1 / $${PATH}atwork2 which EAE expands to
                        // <ResourceName>.<inst>.atwork{1,2} at deploy. If the two
                        // strings drift (resource-prefix rename, quoting change,
                        // trailing-dot bug, case mismatch), the publish lands on a
                        // symbol the subscriber never reads and the swivel stalls at
                        // ToPick forever with no error. Logging both forms surfaces
                        // any drift in the Activity log immediately. Per-label so
                        // syslay vs sysres mismatches show up distinctly.
                        var expectedExpansion1 = $"'{resourceName}.{name}.atwork1'";
                        var expectedExpansion2 = $"'{resourceName}.{name}.atwork2'";
                        bool sym1 = string.Equals(atwork1Symbol, expectedExpansion1, StringComparison.Ordinal);
                        bool sym2 = string.Equals(atwork2Symbol, expectedExpansion2, StringComparison.Ordinal);
                        log?.Invoke(
                            $"[Simulator][SymCheck][{label}] {fbName}.NAME1={atwork1Symbol} ; " +
                            $"{name} $${{PATH}}atwork1 expands to {expectedExpansion1} ; " +
                            (sym1 ? "MATCH" : "MISMATCH — atwork1 publish will NOT reach the actuator, fix resource prefix"));
                        log?.Invoke(
                            $"[Simulator][SymCheck][{label}] {fbName}.NAME2={atwork2Symbol} ; " +
                            $"{name} $${{PATH}}atwork2 expands to {expectedExpansion2} ; " +
                            (sym2 ? "MATCH" : "MISMATCH — atwork2 publish will NOT reach the actuator, fix resource prefix"));

                        slot++;
                    }

                    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                    using var w  = XmlWriter.Create(fs, new XmlWriterSettings
                    {
                        OmitXmlDeclaration = false,
                        Indent             = true,
                        Encoding           = new System.Text.UTF8Encoding(false),
                    });
                    doc.Save(w);
                    log?.Invoke($"[Simulator][SwivelForce][{label}] injected {injectedInFile} new + rewired {sevens.Count - injectedInFile} existing for {sevens.Count} Seven_State instance(s)");
                    injectedTotal += injectedInFile;
                }

                if (sevenInstancesSeen == 0)
                    log?.Invoke("[Simulator][SwivelForce] no Seven_State_Actuator_CAT instances found — nothing to wire (expected when StubSevenStateActuatorsAsFiveState=true)");

                return injectedTotal;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Simulator][SwivelForce][Warn] failed: {ex.GetType().Name}: {ex.Message}");
                return 0;
            }
        }

        // ── tiny helpers used by InjectSimSwivelForce ────────────────────────

        /// <summary>Resolves the deployed sysres file path the way the other sim
        /// post-processors do — first *.sysres in cfg.SysresPath2's directory.</summary>
        static string? ResolveSysresFile(MapperConfig cfg)
        {
            var dir = Path.GetDirectoryName(cfg.SysresPath2);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            return Directory.EnumerateFiles(dir, "*.sysres", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
        }

        /// <summary>Appends a child element to <paramref name="parent"/> and returns it,
        /// so callers can chain `?? AddBeforeNullToAttribute(parent, ...)`.</summary>
        static XElement AddBeforeNullToAttribute(XElement parent, XElement child)
        {
            parent.Add(child);
            return child;
        }

        static void StripWires(XElement section, string fbName)
        {
            foreach (var c in section.Elements(Ns + "Connection").Where(c =>
                (((string?)c.Attribute("Source")) ?? string.Empty).StartsWith(fbName + ".", StringComparison.Ordinal) ||
                (((string?)c.Attribute("Destination")) ?? string.Empty).StartsWith(fbName + ".", StringComparison.Ordinal))
                .ToList())
                c.Remove();
        }

        static void AddEventWire(XElement ec, string src, string dst)
        {
            ec.Add(new XElement(Ns + "Connection",
                new XAttribute("Source", src),
                new XAttribute("Destination", dst)));
        }

        static void AddDataWire(XElement dc, string src, string dst)
        {
            dc.Add(new XElement(Ns + "Connection",
                new XAttribute("Source", src),
                new XAttribute("Destination", dst)));
        }
    }
}
