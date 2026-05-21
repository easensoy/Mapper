using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Translation;

namespace MapperUI.Services
{
    /// <summary>
    /// Emits the canonical event + data wires into the deployed M262 sysres
    /// FBNetwork so EAE actually initialises the application chain
    /// (plcStart → DPAC_FULLINIT → Area → Station → … → Feeder → Process).
    /// No M262IO broker: Sensor/Actuator CATs read/write the M262 pins
    /// directly via their own internal SYMLINK FBs ($${PATH} macros).
    ///
    /// Always overwrites any existing &lt;EventConnections&gt; /
    /// &lt;DataConnections&gt; blocks. Endpoint references use the FB
    /// instance Name when present in the FBNetwork, otherwise fall back to
    /// resolving by FB Type (handles <c>plcStart</c>/<c>DPAC_FULLINIT</c>
    /// which appear with auto-generated names <c>FB2</c>/<c>FB1</c>).
    ///
    /// Each wire is validated against the source/destination FB's
    /// <c>.fbt</c> InterfaceList; ports that don't exist are logged as
    /// <c>[Wire] port not found: {FB}.{port}</c> and that one wire is
    /// skipped (the run continues).
    /// </summary>
    public static class M262SysresWireEmitter
    {
        public sealed record Wire(string Source, string Destination);

        // Component-independent bootstrap event wires. START is the
        // resource's built-in restart FB (EMB_RES_ECO canvas auto-instance).
        // FB1=DPAC_FULLINIT, FB2=plcStart. The init CHAIN
        // (FB1.INITO→Area→Station1→components…→Feed_Station) is built
        // dynamically in Emit() from the components actually present in the
        // sysres — see BuildChainWires — so a missing component never severs
        // the chain (the bug a hardcoded N=2/N=4 list caused). M262IO is
        // intentionally NOT wired: Sensor/Actuator CATs do direct $${PATH}
        // symlink I/O, there is no PLC_RW_M262 broker.
        private static readonly Wire[] BootstrapEventWires =
        {
            new("START.COLD",          "FB1.INIT"),
            new("START.WARM",          "FB1.INIT"),
            new("START.ONLINECHANGE",  "FB1.OC_RETRIGGER"),
            new("FB2.FIRST_INIT",      "FB2.ACK_FIRST"),
        };

        // Canonical CaSBus daisy-chain order for the Feed Station slice
        // (OSDA convention: sensors and actuators interleave in the physical
        // ring). Emit() iterates the components ACTUALLY present in the
        // sysres in this order, then appends any other Sensor/Actuator CAT
        // instance present but unlisted (FBNetwork declaration order) so the
        // init chain and state-report ring are NEVER severed regardless of
        // how many components the syslay emitted (N-component safe).
        private static readonly string[] CaSBusOrder =
        {
            // Station 1 (M262)
            "PartInHopper", "PartAtChecker", "Pusher", "Checker", "Transfer",
            // Station 2 (M580) — Assembly_Station components in PLC-bus order
            "BearingSensor", "ShaftSensor",
            "Bearing_PnP", "Bearing_Gripper",
            "Shaft_Hr", "Shaft_Vr", "Shaft_Gripper", "Clamp",
            // Station 2 (BX1) — Cover pick-and-place
            "TopCoverSenosr",
            "CoverPNP_Hr", "CoverPNP_Vr", "CoverPnp_Gripper",
        };

        private static readonly HashSet<string> SensorCatTypes =
            new(StringComparer.Ordinal) { "Sensor_Bool_CAT" };
        private static readonly HashSet<string> ActuatorCatTypes =
            new(StringComparer.Ordinal)
            {
                "Five_State_Actuator_CAT",
                "Five_State_Actuator_No_Sensors_CAT",
                "Seven_State_Actuator_CAT",
                "Vacuum_Gripper_CAT",
            };

        // Component-independent adapter wires (HMI faceplates + Area/Station
        // structural ring). The CaSBus station chain (Station1→actuators→
        // Feed_Station→Stn1_Term) and the stateRprtCmd report ring
        // (components→Feed_Station→back to first component, closed) are built
        // dynamically in Emit()/BuildChainWires from the components actually
        // present, so they cover N components and the ring is always closed.
        private static readonly Wire[] HmiAdapterWires =
        {
            new("Area_HMI.AreaHMIAdptrOUT",        "Area.AreaHMIAdptrIN"),
            new("Station1_HMI.StationHMIAdptrOUT", "Station1.StationHMIAdptrIN"),
            new("Area.AreaAdptrOUT",               "Station1.AreaAdptrIN"),
        };

        // Endpoints whose LHS is one of these built-in FBs are emitted with
        // the literal name (no FBNetwork lookup, no port validation). Both
        // START and E_RESTART are accepted — EAE exposes one or the other
        // depending on the EMB_RES_ECO resource canvas variant.
        private static readonly HashSet<string> BuiltInRuntimeFbs = new(StringComparer.Ordinal)
        {
            "START",
            "E_RESTART",
        };

        // No top-level data wires. The Feeder / PartInHopper instances
        // expose `athome`/`atwork`/`OutputToWork`/`Input` only as SYMLINK
        // parameter names INSIDE their CAT bodies, not as outer FB data
        // ports. Wiring them at the sysres level fails the build with
        // "port not found". The .hcf ParameterValue rewrite already binds
        // these names to the M262 TM3 channels via PLC_RW_M262 symlinks —
        // no sysres-level DataConnection is needed.
        private static readonly Wire[] DataWires = Array.Empty<Wire>();

        public static void Emit(MapperConfig cfg,
            SystemInjector.BindingApplicationReport report)
        {
            try
            {
                var eaeRoot = M262SysdevEmitter.DeriveEaeProjectRoot(cfg);
                if (eaeRoot == null)
                {
                    report.Missing.Add("[Wire] skipped, EAE project root not derivable");
                    return;
                }
                var sysresPath = LocateM262Sysres(eaeRoot);
                if (sysresPath == null)
                {
                    report.Missing.Add("[Wire] skipped, M262 sysres not found");
                    return;
                }
                var doc = XDocument.Load(sysresPath, LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) { report.Missing.Add("[Wire] skipped, sysres root null"); return; }
                XNamespace ns = root.GetDefaultNamespace();
                var fbNet = root.Element(ns + "FBNetwork");
                if (fbNet == null)
                {
                    report.Missing.Add("[Wire] skipped, no FBNetwork on sysres");
                    return;
                }

                // Build (Name → element) and (Type → element) maps so endpoint
                // references can be either an instance name or a type token.
                var byName = new Dictionary<string, XElement>(StringComparer.Ordinal);
                var byType = new Dictionary<string, XElement>(StringComparer.Ordinal);
                foreach (var fb in fbNet.Elements(ns + "FB"))
                {
                    var n = (string?)fb.Attribute("Name") ?? string.Empty;
                    var t = (string?)fb.Attribute("Type") ?? string.Empty;
                    if (!string.IsNullOrEmpty(n)) byName[n] = fb;
                    if (!string.IsNullOrEmpty(t) && !byType.ContainsKey(t)) byType[t] = fb;
                }

                // Apply the canonical sysres canvas layout (2500 H pitch,
                // top-down: bootstrap → HMI → structural → sensors+process →
                // actuators; no overlap). The same dictionary is mirrored onto
                // the syslay via ApplyLayoutToSyslay at the end of Emit().
                ApplyCanonicalLayout(byName, report, "Sysres");

                // Cache loaded .fbt port lookups so we don't re-parse per wire.
                var portsByType = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                HashSet<string> PortsFor(string type)
                {
                    if (portsByType.TryGetValue(type, out var p)) return p;
                    p = LoadFbtPorts(cfg.TemplateLibraryPath, type);
                    portsByType[type] = p;
                    return p;
                }

                var emittedEvents = new List<(string s, string d)>();
                var emittedData = new List<(string s, string d)>();
                var emittedAdapters = new List<(string s, string d)>();

                bool TryEndpoint(string endpoint, out string name, out string port,
                    out bool builtIn, out string type)
                {
                    name = port = type = string.Empty;
                    builtIn = false;
                    var dot = endpoint.IndexOf('.');
                    if (dot <= 0) return false;
                    var lhs = endpoint.Substring(0, dot);
                    port = endpoint.Substring(dot + 1);
                    if (BuiltInRuntimeFbs.Contains(lhs))
                    {
                        // Built-in runtime FBs (E_RESTART, etc.) are not in
                        // <FBNetwork>; emit literal name and skip port
                        // validation since they have no .fbt in the lib.
                        name = lhs;
                        builtIn = true;
                        return true;
                    }
                    if (byName.TryGetValue(lhs, out var fb) || byType.TryGetValue(lhs, out fb))
                    {
                        name = (string?)fb.Attribute("Name") ?? string.Empty;
                        type = (string?)fb.Attribute("Type") ?? string.Empty;
                        return !string.IsNullOrEmpty(name);
                    }
                    return false;
                }

                void Process(Wire w, List<(string, string)> sink, string label)
                {
                    if (!TryEndpoint(w.Source, out var srcName, out var srcPort, out var srcBuiltIn, out var srcType) ||
                        !TryEndpoint(w.Destination, out var dstName, out var dstPort, out var dstBuiltIn, out var dstType))
                    {
                        // Special case the E_RESTART → plcStart bridge so the
                        // user sees the explicit init-chain failure message.
                        bool isInitBridge = w.Source.StartsWith("E_RESTART.", StringComparison.Ordinal)
                            && w.Destination.StartsWith("plcStart.", StringComparison.Ordinal);
                        if (isInitBridge)
                            report.Missing.Add("[Sysres] E_RESTART or plcStart not found, init chain will not fire");
                        else
                            report.Missing.Add($"[Wire] FB instance not found for {w.Source} → {w.Destination}");
                        return;
                    }
                    if (!srcBuiltIn && !PortExists(PortsFor(srcType), srcPort))
                    {
                        report.Missing.Add($"[Sysres] port not found: {srcName}.{srcPort}, skipping wire");
                        return;
                    }
                    if (!dstBuiltIn && !PortExists(PortsFor(dstType), dstPort))
                    {
                        report.Missing.Add($"[Sysres] port not found: {dstName}.{dstPort}, skipping wire");
                        return;
                    }
                    sink.Add(($"{srcName}.{srcPort}", $"{dstName}.{dstPort}"));
                    report.Missing.Add($"[Sysres] {srcName}.{srcPort} -> {dstName}.{dstPort}");
                }

                // ── Component-driven init chain + CaSBus station chain +
                //    stateRprtCmd report ring. Built from the components
                //    ACTUALLY present (CaSBus order, then any extra CATs) so
                //    a missing component never severs the chain/ring — the
                //    bug a hardcoded N=2/N=4 wire list caused. ────────────
                bool IsSensor(XElement fb) =>
                    SensorCatTypes.Contains((string?)fb.Attribute("Type") ?? string.Empty);
                bool IsActuator(XElement fb) =>
                    ActuatorCatTypes.Contains((string?)fb.Attribute("Type") ?? string.Empty);

                var orderedComps = new List<XElement>();
                var seenComp = new HashSet<string>(StringComparer.Ordinal);
                foreach (var nm in CaSBusOrder)
                    if (byName.TryGetValue(nm, out var cfb) &&
                        (IsSensor(cfb) || IsActuator(cfb)) && seenComp.Add(nm))
                        orderedComps.Add(cfb);
                foreach (var fb in fbNet.Elements(ns + "FB"))
                {
                    var nm = (string?)fb.Attribute("Name") ?? string.Empty;
                    if (nm.Length > 0 && (IsSensor(fb) || IsActuator(fb)) && seenComp.Add(nm))
                        orderedComps.Add(fb);
                }
                string Nm(XElement fb) => (string?)fb.Attribute("Name") ?? string.Empty;
                var compNames = orderedComps.Select(Nm).Where(s => s.Length > 0).ToList();
                var actNames = orderedComps.Where(IsActuator).Select(Nm)
                    .Where(s => s.Length > 0).ToList();

                // Init chain: FB1.INITO→Area→Station1→components…→Feed_Station,
                // wiring INITO(N)→INIT(N+1) so every node is reached.
                var eventWires = new List<Wire>(BootstrapEventWires);
                var initChain = new List<string> { "FB1", "Area", "Station1" };
                initChain.AddRange(compNames);
                initChain.Add("Feed_Station");
                for (int i = 0; i < initChain.Count - 1; i++)
                    eventWires.Add(new Wire($"{initChain[i]}.INITO", $"{initChain[i + 1]}.INIT"));

                var adapterWires = new List<Wire>(HmiAdapterWires);
                // CaSBus station chain — actuators + process ONLY (Sensor_Bool_CAT
                // has no stationAdptr ports): Station1→actuators…→Feed_Station,
                // Feed_Station closed to Stn1_Term.
                var stationChain = new List<string>(actNames) { "Feed_Station" };
                adapterWires.Add(new Wire("Station1.StationAdaptrOUT",
                    $"{stationChain[0]}.stationAdptr_in"));
                for (int i = 0; i < stationChain.Count - 1; i++)
                    adapterWires.Add(new Wire($"{stationChain[i]}.stationAdptr_out",
                        $"{stationChain[i + 1]}.stationAdptr_in"));
                adapterWires.Add(new Wire($"{stationChain[^1]}.stationAdptr_out",
                    "Stn1_Term." + CodeGen.Translation.PortNameValidator.CaSAdptrTerminatorInPort));

                // stateRprtCmd report ring — EVERY component + process, CLOSED:
                // comp(N).out→comp(N+1).in; last→Feed_Station.stateRptCmdAdptr_in;
                // Feed_Station.stateRptCmdAdptr_out→comp(0).in. Process1_Generic
                // uses the *Adptr suffix; Sensor/Actuator CATs use stateRprtCmd_*.
                if (compNames.Count > 0)
                {
                    for (int i = 0; i < compNames.Count - 1; i++)
                        adapterWires.Add(new Wire($"{compNames[i]}.stateRprtCmd_out",
                            $"{compNames[i + 1]}.stateRprtCmd_in"));
                    adapterWires.Add(new Wire($"{compNames[^1]}.stateRprtCmd_out",
                        "Feed_Station.stateRptCmdAdptr_in"));
                    adapterWires.Add(new Wire("Feed_Station.stateRptCmdAdptr_out",
                        $"{compNames[0]}.stateRprtCmd_in"));
                }

                foreach (var w in eventWires)   Process(w, emittedEvents,   "event");
                foreach (var w in DataWires)    Process(w, emittedData,     "data");
                foreach (var w in adapterWires) Process(w, emittedAdapters, "adapter");

                // (Removed) The optional M262IO.HopperEvent / PusherEvent →
                // Feed_Station.state_change fast path is gone with M262IO.
                // Component state reaches the Process exclusively through
                // the stateRprtCmd adapter ring now.

                // Replace all three existing connection blocks with the
                // freshly-emitted set. Adapter wires now live in their own
                // <AdapterConnections> sibling, not folded into events.
                fbNet.Elements(ns + "EventConnections").Remove();
                fbNet.Elements(ns + "DataConnections").Remove();
                fbNet.Elements(ns + "AdapterConnections").Remove();

                if (emittedEvents.Count > 0)
                {
                    var ec = new XElement(ns + "EventConnections");
                    foreach (var (s, d) in emittedEvents)
                        ec.Add(new XElement(ns + "Connection",
                            new XAttribute("Source", s),
                            new XAttribute("Destination", d)));
                    fbNet.Add(ec);
                }
                // Always emit <DataConnections />, even empty, so EAE sees an
                // explicit "no data wires" rather than a missing block.
                var dc = new XElement(ns + "DataConnections");
                foreach (var (s, d) in emittedData)
                    dc.Add(new XElement(ns + "Connection",
                        new XAttribute("Source", s),
                        new XAttribute("Destination", d)));
                fbNet.Add(dc);

                if (emittedAdapters.Count > 0)
                {
                    var ac = new XElement(ns + "AdapterConnections");
                    foreach (var (s, d) in emittedAdapters)
                        ac.Add(new XElement(ns + "Connection",
                            new XAttribute("Source", s),
                            new XAttribute("Destination", d)));
                    fbNet.Add(ac);
                }

                var settings = new XmlWriterSettings
                {
                    OmitXmlDeclaration = false,
                    Indent = true,
                    Encoding = new UTF8Encoding(false),
                };
                using var fs = new FileStream(sysresPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                using var w2 = XmlWriter.Create(fs, settings);
                doc.Save(w2);

                report.Missing.Add(
                    $"[Sysres] wrote {emittedEvents.Count} event + {emittedData.Count} data + " +
                    $"{emittedAdapters.Count} adapter connection(s) to {Path.GetFileName(sysresPath)}");

                // Mirror the SAME CanonicalLayout onto the deployed syslay so
                // the EAE application canvas reads cleanly too. Previously the
                // syslay kept its generator-time coords and overlapped badly
                // with ≥3 components. Best-effort: ApplyLayoutToSyslay silently
                // skips if the file/root is missing. Button 2 writes its syslay
                // to cfg.SyslayPath2 (== ActiveSyslayPath for this path).
                ApplyLayoutToSyslay(cfg.ActiveSyslayPath, report);
            }
            catch (Exception ex)
            {
                report.Missing.Add($"[Wire] failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Canonical canvas layout — applied to BOTH the sysres
        // (ApplyCanonicalLayout in Emit) and the deployed syslay
        // (ApplyLayoutToSyslay, now invoked at the end of Emit). Single source
        // of truth so the two canvases read identically and never overlap.
        //
        // Grid: 2500-unit horizontal pitch (≥2000; clears EAE's widest
        // composite body ~1500-1800 with margin — fixes Feeder/Checker which
        // were only 400 apart and overlapped) and ~700-1400 vertical pitch.
        // Top-down flow: bootstrap → HMI → structural → sensors+process →
        // actuators. START is EAE's built-in E_RESTART auto-instance at the
        // top-left (NOT in the FBNetwork — can't move it, only avoid it):
        // FB2 sits at x=800 (known-clear column) y=1100 (below START's
        // footprint); FB1 (DPAC_FULLINIT) top-right. Feed_Station is pushed
        // right of the sensor row so the stateRprtCmd adapter wires don't
        // cross. M262IO is listed for completeness but is a no-op (the
        // PLC_RW_M262 broker was retired — no such FB is ever instantiated).
        private static readonly Dictionary<string, (int X, int Y)> CanonicalLayout = new(StringComparer.Ordinal)
        {
            // Runtime bootstrap (START is EAE-auto, top-left)
            { "FB1",          (3000, 400)  },   // DPAC_FULLINIT — top-right
            { "FB2",          (800,  1100) },   // plcStart — below START
            // HMI row (y=2000)
            { "Area_HMI",     (2000, 2000) },
            { "Station1_HMI", (4500, 2000) },
            // Structural row (y=2900)
            { "Area",         (2000, 2900) },
            { "Station1",     (4500, 2900) },
            { "Area_Term",    (7000, 2900) },
            // Sensors + process row (y=4000) — Feed_Station right of sensors
            { "PartInHopper", (2000, 4000) },
            { "PartAtChecker",(4500, 4000) },
            { "Feed_Station", (7000, 4000) },
            // Actuator row (y=5400) — 2500 pitch, terminator far right
            // VueOne component "Feeder" is emitted as FB instance "Pusher" by
            // ResolveActuatorDisplayName, matching the rig hardware alias used
            // in the HCF channel bindings (DI00 'PusherAtHome', DI01 'PusherAtWork'
            // etc.). The CanonicalLayout key MUST be "Pusher" — the post-syslay
            // rewrite walks by FB Name= attribute.
            { "Pusher",       (2000, 5400) },
            { "Checker",      (4500, 5400) },
            { "Transfer",     (7000, 5400) },
            { "Stn1_Term",    (9500, 5400) },
            // M580 zone (Station 2 — purple frame). Tidied layout to mirror
            // SMC_Rig_Expo_withClamp's reference:
            //   y=2000: Station2_HMI
            //   y=2900: Station2 + Stn2_Term
            //   y=4000: Assembly_Station Process FB + sensors row
            //   y=5400: Actuator row 1 (Bearing_PnP, Bearing_Gripper, Shaft_Hr, Shaft_Vr)
            //   y=6500: Actuator row 2 (Shaft_Gripper, Clamp)
            // FRAME_M580 spans 12000..20300; all coordinates inside that band.
            { "Station2_HMI",    (14000, 2000) },
            { "Station2",        (14000, 2900) },
            { "Stn2_Term",       (19500, 2900) },
            { "Assembly_Station",(12200, 4000) },
            { "BearingSensor",   (15000, 4000) },
            { "ShaftSensor",     (17500, 4000) },
            { "Bearing_PnP",     (12200, 5400) },
            { "Bearing_Gripper", (14700, 5400) },
            { "Shaft_Hr",        (17200, 5400) },
            { "Shaft_Vr",        (19700, 5400) },
            { "Shaft_Gripper",   (12200, 6500) },
            { "Clamp",           (14700, 6500) },
            // BX1 zone (Station 2 — green frame). Sensors at y=4000, actuators
            // at y=5400, columns at 2500 pitch starting at x=20600 to land
            // inside FRAME_BX1 (20400..27200).
            { "TopCoverSenosr",  (20600, 4000) },
            { "CoverPNP_Hr",     (20600, 5400) },
            { "CoverPNP_Vr",     (23100, 5400) },
            { "CoverPnp_Gripper",(25600, 5400) },
            // No-op (M262IO/PLC_RW_M262 retired — never instantiated)
            { "M262IO",       (9500, 400)  },
        };

        /// <summary>
        /// Force every FB element in <paramref name="byName"/> matching a
        /// CanonicalLayout entry to the spec coordinates, then emit one
        /// <c>[Layout] {Name} -> x=…, y=…</c> line per placed FB.
        /// </summary>
        private static void ApplyCanonicalLayout(Dictionary<string, XElement> byName,
            SystemInjector.BindingApplicationReport report, string source)
        {
            int placed = 0;
            foreach (var kv in CanonicalLayout)
            {
                if (!byName.TryGetValue(kv.Key, out var fb)) continue;
                var oldX = (string?)fb.Attribute("x") ?? "?";
                var oldY = (string?)fb.Attribute("y") ?? "?";
                fb.SetAttributeValue("x", kv.Value.X.ToString(System.Globalization.CultureInfo.InvariantCulture));
                fb.SetAttributeValue("y", kv.Value.Y.ToString(System.Globalization.CultureInfo.InvariantCulture));
                report.Missing.Add(
                    $"[{source} layout] {kv.Key}: ({oldX},{oldY}) -> ({kv.Value.X},{kv.Value.Y})");
                placed++;
            }
            report.Missing.Add($"[{source} layout] {placed}/{CanonicalLayout.Count} FBs placed");
        }

        /// <summary>
        /// Open the syslay at <paramref name="syslayPath"/>, apply the same
        /// CanonicalLayout coordinates to every matching FB inside
        /// <c>SubAppNetwork</c>/<c>FBNetwork</c>, and persist. Best-effort —
        /// silently skips if the file or root is missing.
        /// </summary>
        public static void ApplyLayoutToSyslay(string syslayPath,
            SystemInjector.BindingApplicationReport report)
        {
            try
            {
                if (string.IsNullOrEmpty(syslayPath) || !File.Exists(syslayPath)) return;
                var doc = XDocument.Load(syslayPath, LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                XNamespace ns = root.GetDefaultNamespace();
                var net = root.Element(ns + "SubAppNetwork") ?? root.Element(ns + "FBNetwork");
                if (net == null) return;
                var byName = new Dictionary<string, XElement>(StringComparer.Ordinal);
                foreach (var fb in net.Elements(ns + "FB"))
                {
                    var n = (string?)fb.Attribute("Name") ?? string.Empty;
                    if (!string.IsNullOrEmpty(n)) byName[n] = fb;
                }
                ApplyCanonicalLayout(byName, report, "Syslay");
                var settings = new XmlWriterSettings
                {
                    OmitXmlDeclaration = false,
                    Indent = true,
                    Encoding = new UTF8Encoding(false),
                };
                using var fs = new FileStream(syslayPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                using var w = XmlWriter.Create(fs, settings);
                doc.Save(w);
            }
            catch (Exception ex)
            {
                report.Missing.Add($"[Layout] syslay write failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string? LocateM262Sysres(string eaeRoot)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return null;
            // The M262 sysres sits next to the M262 sysdev. Pick the .sysres
            // whose enclosing folder is named after a .sysdev whose root
            // Device has Type="M262_dPAC".
            foreach (var sysdev in Directory.EnumerateFiles(systemDir, "*.sysdev", SearchOption.AllDirectories))
            {
                try
                {
                    var doc = XDocument.Load(sysdev);
                    var root = doc.Root;
                    if (root == null) continue;
                    var type = (string?)root.Attribute("Type") ?? string.Empty;
                    var nspace = (string?)root.Attribute("Namespace") ?? string.Empty;
                    if (type != "M262_dPAC" || nspace != "SE.DPAC") continue;
                    var sysdevDir = Path.Combine(
                        Path.GetDirectoryName(sysdev)!,
                        Path.GetFileNameWithoutExtension(sysdev));
                    if (!Directory.Exists(sysdevDir)) continue;
                    return Directory.EnumerateFiles(sysdevDir, "*.sysres").FirstOrDefault();
                }
                catch { /* skip */ }
            }
            return null;
        }

        private static bool TryResolve(string endpoint,
            Dictionary<string, XElement> byName,
            Dictionary<string, XElement> byType,
            out string instName, out string instType, out string portName)
        {
            instName = instType = portName = string.Empty;
            var dot = endpoint.IndexOf('.');
            if (dot <= 0) return false;
            var lhs = endpoint.Substring(0, dot);
            portName = endpoint.Substring(dot + 1);
            // Try Name first (exact instance match), then Type fallback.
            if (byName.TryGetValue(lhs, out var fb) || byType.TryGetValue(lhs, out fb))
            {
                instName = (string?)fb.Attribute("Name") ?? string.Empty;
                instType = (string?)fb.Attribute("Type") ?? string.Empty;
                return !string.IsNullOrEmpty(instName) && !string.IsNullOrEmpty(instType);
            }
            return false;
        }

        private static bool PortExists(HashSet<string> ports, string portName)
            => ports.Count == 0 /* unknown FB type — be lenient */ || ports.Contains(portName);

        /// <summary>
        /// Locate the .fbt for <paramref name="typeName"/> under
        /// <c>{libRoot}/{Basic|Composite|Adapter|CAT}/<typeName>/IEC61499/<typeName>.fbt</c>
        /// and return the set of port Names declared inside its
        /// <c>InterfaceList</c> (events, data, adapters). Returns an empty
        /// set if the .fbt isn't found — caller treats empty as "skip
        /// validation" so wires don't fail when the type lives outside the
        /// Template Library (SE.DPAC.DPAC_FULLINIT, SE.AppBase.plcStart).
        /// </summary>
        private static HashSet<string> LoadFbtPorts(string libRoot, string typeName)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(libRoot) || !Directory.Exists(libRoot)) return set;
            string? fbtPath = null;
            foreach (var sub in new[] { "Basic", "Composite", "Adapter", "CAT" })
            {
                var probe = Path.Combine(libRoot, sub, typeName, "IEC61499", typeName + ".fbt");
                if (File.Exists(probe)) { fbtPath = probe; break; }
            }
            if (fbtPath == null)
            {
                // Some types (Area, Station, CaSAdptrTerminator) ship as flat
                // files under Composite/<name>/IEC61499/. Glob as a fallback.
                foreach (var f in Directory.EnumerateFiles(libRoot, typeName + ".fbt", SearchOption.AllDirectories))
                { fbtPath = f; break; }
            }
            if (fbtPath == null) return set;
            try
            {
                var doc = XDocument.Load(fbtPath);
                var iface = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "InterfaceList");
                if (iface == null) return set;
                foreach (var port in iface.Descendants())
                {
                    var n = (string?)port.Attribute("Name");
                    if (!string.IsNullOrEmpty(n)) set.Add(n);
                }
            }
            catch { /* leave set empty */ }
            return set;
        }
    }
}
