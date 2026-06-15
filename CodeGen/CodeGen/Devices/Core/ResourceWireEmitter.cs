using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Translation;

namespace CodeGen.Devices.Core
{
    public static class ResourceWireEmitter
    {
        public sealed record Wire(string Source, string Destination);

        /// <summary>
        /// Per-resource structural anchor set. Everything component-driven
        /// (the init chain body, the CaSBus station chain, the stateRprtCmd
        /// report ring) is built from the Sensor/Actuator CATs ACTUALLY present
        /// in the target sysres — see <see cref="EmitForResource"/> — so only
        /// the fixed structural FB names differ between PLCs. This record
        /// carries those names so the proven M262 wiring core can target the
        /// M580 (Station2/Assembly_Station/Stn2_Term) and the BX1 (no Station
        /// FB at all in this increment) without forking the logic.
        ///
        /// A resource with no Station/Process/Terminator (BX1) leaves
        /// <see cref="StationFb"/>/<see cref="ProcessFb"/>/<see cref="TerminatorFb"/>
        /// null and the CaS station chain + report ring are skipped gracefully;
        /// the init fan-out still runs so its actuators initialise.
        /// </summary>
        public sealed record ResourceAnchors(
            string Label,                 // log tag, e.g. "Sysres", "M580", "BX1"
            string? AreaFb,               // Area structural FB (null on M580/BX1)
            string? StationFb,            // Station structural FB (null on BX1)
            string? ProcessFb,            // Process1_Generic FB (null on BX1)
            string? TerminatorFb,         // CaSAdptrTerminator FB (null on BX1)
            IReadOnlyList<Wire> HmiAdapterWires);

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
            // Station 1 (M262) — FB instance names from Control.xml, NOT
            // hardware-side aliases. The CAT SYMLINKMULTIVARDST/SRC macros
            // expand $${PATH} to the FB instance name; if we order/rename
            // here to "Pusher" but the FB instance is "Feeder", the init
            // chain is severed and symlink PATHs lose their bindings.
            "PartInHopper", "PartAtChecker", "Feeder", "Checker", "Transfer",
            // Station 2 (M580) — Assembly_Station components in PLC-bus order.
            // Bearing_PnP (Seven_State_Actuator_CAT) is the live swivel/bearing
            // actuator; Bearing_Gripper kept for back-compat with older syslays.
            "BearingSensor", "ShaftSensor",
            "Bearing_PnP", "Bearing_Gripper",
            "Shaft_Hr", "Shaft_Vr", "Shaft_Gripper", "Clamp",
            // Station 2 (BX1) — Cover pick-and-place
            "TopCoverSenosr",
            "CoverPNP_Hr", "CoverPNP_Vr", "CoverPnp_Gripper",
        };

        private const string RingCrossGateType = "RingCrossGate";
        private const string M580CoverRingGate = "M580_CoverRingGate";
        private const string Bx1CoverRingGate = "BX1_CoverRingGate";

        private static readonly HashSet<string> SensorCatTypes =
            new(StringComparer.Ordinal) { "Sensor_Bool_CAT" };
        private static readonly HashSet<string> ActuatorCatTypes =
            new(StringComparer.Ordinal)
            {
                "Five_State_Actuator_CAT",
                "Five_State_Actuator_No_Sensors_CAT",
                // Seven_State_Actuator_CAT — Bearing_PnP (13-state PARALLEL+
                // ALTERNATIVE branched) routes here. It is INIT-chained like
                // any actuator. Task #69 added a stateRprtCmd ring node to its
                // .fbt, so it now JOINS the report ring (NoRingAdapterTypes is
                // empty) and Process1_Generic can command/read it. It still has
                // NO stationAdptr port, so it stays off the CaSBus station chain
                // (listed in NoStationAdapterTypes below).
                "Seven_State_Actuator_CAT",
                // Centre-home swivel (Bearing_PnP, 2026-06-02). Unlike the old
                // Seven CAT it HAS stationAdptr_in/out + stateRprtCmd_in/out, so
                // it is treated like any Five_State actuator: INIT-chained, on the
                // report ring AND on the CaSBus station chain (NOT listed in
                // NoStationAdapterTypes) — the station chain is what delivers
                // ModeCMD to the core, without which the ECC never leaves home.
                "Seven_State_Actuator_Centre_Home_CAT",
                "Vacuum_Gripper_CAT",
                // STAGE 5b: the UR3e task arm. Has stateRprtCmd_in/out (ring node grafted into
                // its .fbt) so Process1_Generic commands/reads it over the report ring; NO
                // stationAdptr (listed in NoStationAdapterTypes below → off the CaSBus chain).
                "Robot_Task_CAT",
            };

        // CAT types whose .fbt declares NO stationAdptr (CaSAdptr) port. They
        // are INIT-chained and (if they have the ring port) join the
        // stateRprtCmd report ring, but stay OFF the CaSBus station/mode/fault
        // chain. Seven_State_Actuator_CAT (Bearing_PnP) gained a stateRprtCmd
        // ring node (task #69) so Process1_Generic can command/read it, but it
        // still has no stationAdptr — so it is listed here (station chain only).
        // Single source of truth lives in TemplateMap so the syslay wiring
        // (BuildFeedStationWiring/BuildStation2Wiring) and this sysres wiring can never drift.
        private static readonly IReadOnlySet<string> NoStationAdapterTypes =
            TemplateMap.NoStationAdapterCatTypes;

        // CAT types whose .fbt declares NO stateRprtCmd ring port. Empty now
        // that Seven_State_Actuator_CAT carries the ring node; kept as an
        // explicit hook so a future ring-less CAT can be excluded from the
        // report ring without re-plumbing the filter. Excluding a type here
        // (but not from NoStationAdapterTypes) would dangle ring wires on EAE
        // import, so the two sets are deliberately kept separate.
        private static readonly HashSet<string> NoRingAdapterTypes =
            new(StringComparer.Ordinal);

        // Component-independent adapter wires (HMI faceplates + Area/Station
        // structural ring). The CaSBus station chain (Station1→actuators→
        // Feed_Station→Stn1_Term) and the stateRprtCmd report ring
        // (components→Feed_Station→back to first component, closed) are built
        // dynamically in Emit()/BuildChainWires from the components actually
        // present, so they cover N components and the ring is always closed.
        internal static readonly Wire[] HmiAdapterWires =
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

        /// <summary>
        /// Wires one deployed sysres FBNetwork using the proven M262 topology,
        /// parameterised by <paramref name="anchors"/>:
        ///   • bootstrap event wires (START→FB1→…)
        ///   • init chain FB1.INITO→[Area]→[Station]→components…→[Process]
        ///     (every present node reached; missing anchors collapse out)
        ///   • CaSBus station chain [Station]→actuators…→[Process]→[Terminator]
        ///   • closed stateRprtCmd report ring among all components + [Process]
        ///   • the resource's HMI/structural adapter wires.
        /// Components (Sensor/Actuator CATs) are discovered from the sysres
        /// itself in <see cref="CaSBusOrder"/> then declaration order, so the
        /// chains/ring are N-component-safe and never severed.
        ///
        /// A resource with no Station/Process/Terminator (BX1) gets ONLY the
        /// init fan-out + the report ring among its own components; the CaS
        /// station chain is skipped gracefully (no Station/Process anchor).
        /// </summary>
        public static void EmitForResource(MapperConfig cfg, string sysresPath,
            ResourceAnchors anchors, SystemInjector.BindingApplicationReport report)
        {
            try
            {
                var tag = anchors.Label;
                if (!File.Exists(sysresPath))
                {
                    report.Missing.Add($"[Wire][{tag}] skipped, sysres not found: {sysresPath}");
                    return;
                }
                var doc = XDocument.Load(sysresPath, LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) { report.Missing.Add($"[Wire][{tag}] skipped, sysres root null"); return; }
                XNamespace ns = root.GetDefaultNamespace();
                var fbNet = root.Element(ns + "FBNetwork");
                if (fbNet == null)
                {
                    report.Missing.Add($"[Wire][{tag}] skipped, no FBNetwork on sysres");
                    return;
                }

                // Build (Name → element) and (Type → element) maps so endpoint
                // references can be either an instance name or a type token.
                var recipeSyncCount = SysresFbMirror.SyncProcessRecipesFromSyslay(
                    cfg.ActiveSyslayPath, doc);
                if (recipeSyncCount > 0)
                    report.Missing.Add(
                        $"[Wire][{tag}] synced {recipeSyncCount} Process recipe(s) from syslay to sysres");

                var byName = new Dictionary<string, XElement>(StringComparer.Ordinal);
                var byType = new Dictionary<string, XElement>(StringComparer.Ordinal);
                foreach (var fb in fbNet.Elements(ns + "FB"))
                {
                    var n = (string?)fb.Attribute("Name") ?? string.Empty;
                    var t = (string?)fb.Attribute("Type") ?? string.Empty;
                    if (!string.IsNullOrEmpty(n)) byName[n] = fb;
                    if (!string.IsNullOrEmpty(t) && !byType.ContainsKey(t)) byType[t] = fb;
                }

                // Apply the canonical sysres canvas layout (single source of
                // truth dict holds all M262 + M580 + BX1 coordinates; only the
                // FBs present on THIS resource are moved). The M580 / BX1
                // sysres canvases are device-local so we translate the present
                // entries to the device-local canvas origin (so the chain lands
                // next to FB1 / FB2 at x=2000 instead of x=12200+ on the M580
                // own canvas). The M262 sysres keeps the raw CanonicalLayout
                // coords because its FBs already start at x=2000 (Area / Feeder /
                // PartInHopper are authored at that origin).
                bool translateToOrigin =
                    string.Equals(tag, "M580", StringComparison.Ordinal) ||
                    string.Equals(tag, "BX1",  StringComparison.Ordinal);
                ApplyCanonicalLayout(byName, report, tag, translateToOrigin);

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
                            report.Missing.Add($"[{tag}] E_RESTART or plcStart not found, init chain will not fire");
                        else
                            report.Missing.Add($"[Wire] FB instance not found for {w.Source} → {w.Destination}");
                        return;
                    }
                    if (!srcBuiltIn && !PortExists(PortsFor(srcType), srcPort))
                    {
                        report.Missing.Add($"[{tag}] port not found: {srcName}.{srcPort}, skipping wire");
                        return;
                    }
                    if (!dstBuiltIn && !PortExists(PortsFor(dstType), dstPort))
                    {
                        report.Missing.Add($"[{tag}] port not found: {dstName}.{dstPort}, skipping wire");
                        return;
                    }
                    sink.Add(($"{srcName}.{srcPort}", $"{dstName}.{dstPort}"));
                    report.Missing.Add($"[{tag}] {srcName}.{srcPort} -> {dstName}.{dstPort}");
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
                // True when the FB's type exposes the stationAdptr (CaSAdptr)
                // port — used for the CaSBus station/mode/fault chain.
                // Seven_State_Actuator_CAT does NOT, so it is kept off that chain.
                bool HasStationAdapter(XElement fb) =>
                    !NoStationAdapterTypes.Contains((string?)fb.Attribute("Type") ?? string.Empty);
                // True when the FB's type exposes the stateRprtCmd ring port —
                // used for the report ring. Seven_State_Actuator_CAT now carries
                // the ring node (task #69), so Bearing_PnP joins the ring and
                // Process1_Generic can command/read it. (M262's components are
                // all Sensor/Five_State, which already have the port — so this
                // filters nothing on M262 and its output is unchanged.)
                bool HasRingAdapter(XElement fb) =>
                    !NoRingAdapterTypes.Contains((string?)fb.Attribute("Type") ?? string.Empty);

                var orderedComps = new List<XElement>();
                var seenComp = new HashSet<string>(StringComparer.Ordinal);
                foreach (var nm in CaSBusOrder)
                    if (byName.TryGetValue(nm, out var cfb) &&
                        (IsSensor(cfb) || IsActuator(cfb)))
                    {
                        if (seenComp.Add(nm))
                            orderedComps.Add(cfb);
                    }
                foreach (var fb in fbNet.Elements(ns + "FB"))
                {
                    var nm = (string?)fb.Attribute("Name") ?? string.Empty;
                    if (nm.Length == 0 || (!IsSensor(fb) && !IsActuator(fb))) continue;
                    if (seenComp.Add(nm))
                        orderedComps.Add(fb);
                }
                string Nm(XElement fb) => (string?)fb.Attribute("Name") ?? string.Empty;
                // initNames — every component, in CaSBus order, for the INIT
                // chain (all CATs expose INIT/INITO).
                var initNames = orderedComps.Select(Nm).Where(s => s.Length > 0).ToList();
                // STAGE 5b: when the robot tail is active, Ejector + Robot LEAVE the local
                // (M262 Feed) report ring — they form a separate cross-PLC segment (Ejector.in
                // arrives from Disassembly, Ejector.out→Robot.in is local, Robot.out crosses to
                // BearingSensor). They must NOT be in ringNames, or the Feed ring would drive
                // Robot.stateRprtCmd_out a SECOND time (the double-drive). No-op on M580/BX1
                // (no Ejector/Robot there). They stay in initNames so they are still INIT'd.
                // Off → ringNames byte-identical.
                bool robotTail = RobotTailActive(cfg);
                // FEED -> ASSEMBLY MERGE: when on, the M262 Feed ring and the M580 ring are spliced
                // into one ring (BuildStation2Wiring adds Disassembly.out->PartInHopper.in +
                // Feed_Station.out->BearingSensor.in). BOTH this M580 and this M262 sysres ring then
                // leave their process close-back OPEN at the boundary (handled below) so EAE bridges
                // the open ends instead of double-driving them.
                bool feedMerge = MapperConfig.FeedAssemblyHandshake && !robotTail;
                // PARTATASSEMBLY CROSS-RING (FeedAssemblyPartBridge): PartAtAssembly is a synthesized
                // M262 sensor spliced into the M580 ring (Disassembly -> PartAtAssembly -> BearingSensor
                // via two cross-device hops EAE bridges). It must NOT join the M262 Feed ring — there it
                // would report {id 5} into M262's table (colliding with Checker) AND be driven twice.
                // Detected from the syslay hop actually emitted (single source of truth). No-op on
                // M580/BX1 (no PartAtAssembly there).
                bool partBridge = PartBridgeActive(cfg);
                // ringNames — components that expose the stateRprtCmd adapter
                // (now INCLUDES Seven_State/Bearing_PnP); used for the report ring.
                var ringNames = orderedComps.Where(HasRingAdapter).Select(Nm)
                    .Where(s => s.Length > 0)
                    .Where(s => !(robotTail &&
                        (string.Equals(s, "Ejector", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(s, "Robot",   StringComparison.OrdinalIgnoreCase))))
                    .Where(s => !(partBridge &&
                        string.Equals(s, "PartAtAssembly", StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                // actNames — actuators that expose the stationAdptr adapter
                // (still excludes Seven_State, which has no stationAdptr); used
                // for the CaS station chain.
                var actNames = orderedComps.Where(c => IsActuator(c) && HasStationAdapter(c))
                    .Select(Nm).Where(s => s.Length > 0).ToList();

                // processNames — EVERY Process1_Generic on this resource, anchor
                // first then any others. M580 carries BOTH Assembly_Station and
                // Disassembly; the init chain, CaS station chain and report ring
                // below thread through every one (so a second process is never left
                // unwired), and they get cross-process state_update->state_change
                // wires too. M262 has only Feed_Station, so this is a single-element
                // list and its output is byte-identical to before. The parked
                // M580 Disassembly process is filtered out below.
                // STAGE 5a (MapperConfig.UnparkDisassembly): when the flag is on, STOP
                // bypassing Disassembly — it then enters processNames and the init chain, CaS
                // station chain and stateRprtCmd report ring below thread through BOTH engines
                // automatically (compN -> Assembly -> Disassembly -> comp0). Default-off keeps
                // today's proven single-engine wiring byte-identical.
                bool BypassParkedM580Disassembly(string name) =>
                    !MapperConfig.UnparkDisassembly &&
                    string.Equals(anchors.ProcessFb, "Assembly_Station", StringComparison.Ordinal) &&
                    (string.Equals(name, "Disassembly", StringComparison.Ordinal) ||
                     string.Equals(name, "Disassembly_Station", StringComparison.Ordinal));

                var processNames = new List<string>();
                if (Present(anchors.ProcessFb, byName) &&
                    !BypassParkedM580Disassembly(anchors.ProcessFb!))
                    processNames.Add(anchors.ProcessFb!);
                foreach (var fb in fbNet.Elements(ns + "FB"))
                {
                    var nm = (string?)fb.Attribute("Name") ?? string.Empty;
                    if (nm.Length == 0 || processNames.Contains(nm)) continue;
                    if (BypassParkedM580Disassembly(nm)) continue;
                    if ((string?)fb.Attribute("Type") == "Process1_Generic")
                        processNames.Add(nm);
                }
                if (byName.ContainsKey("Disassembly") && BypassParkedM580Disassembly("Disassembly"))
                    report.Missing.Add("[M580 RES0] Disassembly parked and bypassed in init/CaS/stateRprtCmd wiring");
                bool haveProcess = processNames.Count > 0;
                string? ringGateName = null;
                if (string.Equals(tag, "M580", StringComparison.Ordinal) &&
                    byName.ContainsKey(M580CoverRingGate))
                    ringGateName = M580CoverRingGate;
                else if (string.Equals(tag, "BX1", StringComparison.Ordinal) &&
                         byName.ContainsKey(Bx1CoverRingGate))
                    ringGateName = Bx1CoverRingGate;

                // Init chain: FB1.INITO→[Area]→[Station]→components…→[Process],
                // wiring INITO(N)→INIT(N+1) so every node is reached. Anchors
                // that are null/absent on this resource (e.g. BX1 has no Area /
                // Station / Process) collapse out, leaving FB1.INITO fanning
                // straight into the first component so actuators still init.
                var eventWires = new List<Wire>(BootstrapEventWires);
                var initChain = new List<string> { "FB1" };
                if (Present(anchors.AreaFb, byName)) initChain.Add(anchors.AreaFb!);
                if (Present(anchors.StationFb, byName)) initChain.Add(anchors.StationFb!);
                // STAGE 5b: the M262 robot-tail (Ejector + Robot) are Disassembly's actuators that
                // merely live on M262 and carry cross-PLC adapter links to M580. Keep them OUT of
                // the critical init path to the process: init the Feed components -> process FIRST,
                // then the tail. Otherwise a stall in the Robot's bring-up (its cross-PLC links)
                // blocks Feed_Station.INIT and the whole Feed station is dead when all three PLCs run
                // (it works M262-alone because the links are absent). Self-scoping: only the M262
                // resource has Ejector/Robot in initNames. Off -> tail empty -> byte-identical.
                var robotTailInit = RobotTailActive(cfg)
                    ? new HashSet<string>(StringComparer.Ordinal) { "Ejector", "Robot" }
                    : new HashSet<string>(StringComparer.Ordinal);
                // PartAtAssembly is a cross-PLC node (its report crosses to M580); init it LAST too,
                // after Feed -> Feed_Station, so its cross-PLC adapter bring-up never sits in the
                // critical path to Feed_Station.INIT (same reasoning as the robot tail). M262-only.
                if (partBridge) robotTailInit.Add("PartAtAssembly");
                initChain.AddRange(initNames.Where(n => !robotTailInit.Contains(n)));
                if (ringGateName != null) initChain.Add(ringGateName);
                initChain.AddRange(processNames);   // every process is INIT-chained (before the tail)
                initChain.AddRange(initNames.Where(n => robotTailInit.Contains(n)));  // tail inits last
                for (int i = 0; i < initChain.Count - 1; i++)
                    eventWires.Add(new Wire($"{initChain[i]}.INITO", $"{initChain[i + 1]}.INIT"));

                // MqttConn (MQTT_CONNECTION) bring-up. Added 2026-05-26 after the
                // PLC published nothing to the broker on a full rig cycle —
                // mosquitto logged only the desktop subscriber's connection,
                // never the M262 (192.168.1.10). The syslay already carries
                // Area.INITO → MqttConn.INIT and MqttConn.INITO → MqttConn.CONNECT
                // (SystemLayoutInjector lines 1444-1445), and SysresFbMirror
                // mirrors the MqttConn FB instance onto whichever resource gets
                // it (M262 by default), but the wires that pull MqttConn out of
                // reset were never emitted onto the sysres FBNetwork — they
                // exist on the syslay only. At runtime EAE executes each
                // resource's sysres event graph, so a wire that lives only on
                // the syslay never fires: MqttConn.INIT never sees the event,
                // the broker connection never opens, and every embedded
                // MQTT_PUBLISH inside the CATs (Five_State_Actuator_CAT,
                // Sensor_Bool_CAT, Process1_Generic) binds-by-ConnectionID to
                // an unopened connection and silently drops every publish.
                // Re-emitting the same two wires onto whichever sysres carries
                // MqttConn (M262 only in this rig) makes the runtime fire
                // INIT and self-fire CONNECT exactly once, after Area has come
                // up, so the broker connection is established before the first
                // state-change publish. Skipped cleanly when MqttConn is not
                // on this resource (MqttPublishEnabled=false, or M580/BX1).
                // Find the MQTT_CONNECTION FB on THIS resource by TYPE, not by
                // the fixed name "MqttConn" — each resource now has its own
                // (MqttConn on BX1, MqttConn_M262 on M262, MqttConn_M580 on
                // M580) so the embedded MqttPub can bind + publish locally.
                // Wire whichever one is here: <boot>.INITO → <fb>.INIT and
                // <fb>.INITO → <fb>.CONNECT so the broker connection opens once
                // after the resource comes up.
                foreach (var mqttKv in byName)
                {
                    if (!string.Equals((string?)mqttKv.Value.Attribute("Type"),
                            "MQTT_CONNECTION", StringComparison.Ordinal))
                        continue;
                    var mqttName = mqttKv.Key;
                    // INIT off the resource boot: Area.INITO when present (M262),
                    // else FB1.INITO. BX1 has no Area, so without the FB1 fallback
                    // the MqttConn.INIT never fires and the broker never opens.
                    var mqttInit = Present(anchors.AreaFb, byName) ? anchors.AreaFb! : "FB1";
                    eventWires.Add(new Wire($"{mqttInit}.INITO", $"{mqttName}.INIT"));
                    eventWires.Add(new Wire($"{mqttName}.INITO", $"{mqttName}.CONNECT"));
                }

                // Cross-process synchronisation REMOVED 2026-05-26: the prior
                // code emitted Process[i].state_update → Process[j].state_change
                // wires on the resource between every Process pair (M580 linked
                // Assembly_Station ↔ Disassembly). Process1_Generic.fbt declares
                // only INIT/INITO events — no state_update / state_change — so
                // EAE rejected those wires (ERR_NO_SUCH_EVENT) and the project
                // failed to compile, leaving the runtime offline (Pusher did not
                // trigger). Recipe behaviour is unchanged: cross-process
                // HandShake conditions were already dropped as out-of-scope
                // refs, so the wires were purely speculative.

                var adapterWires = new List<Wire>(anchors.HmiAdapterWires);

                // CaSBus station chain — actuators + process ONLY (Sensor_Bool_CAT
                // has no stationAdptr ports): [Station]→actuators…→[Process],
                // [Process] closed to [Terminator]. Requires BOTH a Station
                // anchor (chain source) and a Process anchor (chain tail). BX1
                // has neither, so this whole block is skipped — its actuators
                // still initialise via the fan-out above and report via the
                // ring below.
                bool haveStation = Present(anchors.StationFb, byName);
                if (haveStation && haveProcess)
                {
                    var stationChain = new List<string>(actNames);
                    stationChain.AddRange(processNames);   // actuators…→Process(es)→Terminator
                    adapterWires.Add(new Wire($"{anchors.StationFb}.StationAdaptrOUT",
                        $"{stationChain[0]}.stationAdptr_in"));
                    for (int i = 0; i < stationChain.Count - 1; i++)
                        adapterWires.Add(new Wire($"{stationChain[i]}.stationAdptr_out",
                            $"{stationChain[i + 1]}.stationAdptr_in"));
                    if (Present(anchors.TerminatorFb, byName))
                        adapterWires.Add(new Wire($"{stationChain[^1]}.stationAdptr_out",
                            $"{anchors.TerminatorFb}." +
                            CodeGen.Translation.PortNameValidator.CaSAdptrTerminatorInPort));
                }
                else
                {
                    report.Missing.Add(
                        $"[{tag}] no Station/Process FB on this resource, " +
                        "skipping CaS station chain (init fan-out + report ring still wired)");
                }

                // stateRprtCmd report ring — every adapter-capable component
                // (ringNames excludes Seven_State, which lacks the port) plus
                // [Process] if present, CLOSED: comp(N).out→comp(N+1).in. With a
                // Process anchor the ring closes through it (last→Process.in,
                // Process.out→comp0.in); without one (BX1) the ring closes
                // directly comp(last)→comp(0) so the components still gossip
                // state among themselves. Process1_Generic uses the *Adptr
                // suffix; Sensor/Actuator CATs use stateRprtCmd_*.
                // CROSS-PLC COVER RING (task #69, MapperConfig.ExtendStateRingAcrossBx1):
                // when the syslay carries the M580→BX1 cross-hop, this resource's ring is
                // a PORTION of one ring spanning both PLCs, so it must be left OPEN at the
                // device boundary — otherwise the boundary adapter socket would be driven
                // by both this sysres close AND the EAE-bridged cross-hop. We gate on the
                // cross-hop ACTUALLY present in the generated syslay (single source of
                // truth) so the sysres opens iff the syslay crossed — never half-and-half.
                //   M580: drop the Clamp→Assembly_Station close (Clamp.out crosses to BX1).
                //   BX1 : drop the CoverPnp_Gripper→TopCoverSenosr self-close (it crosses
                //         to Assembly_Station). The far ends are joined by the syslay hops.
                bool crossRingActive = CrossPlcCoverRingActive(cfg);
                bool openM580Boundary = crossRingActive && haveProcess &&
                    string.Equals(tag, "M580", StringComparison.Ordinal) &&
                    ringNames.Count > 0 &&
                    string.Equals(ringNames[^1], "Clamp", StringComparison.OrdinalIgnoreCase);
                bool openBx1Boundary = crossRingActive && !haveProcess &&
                    string.Equals(tag, "BX1", StringComparison.Ordinal) &&
                    ringNames.Count > 1;

                if (ringNames.Count > 0)
                {
                    for (int i = 0; i < ringNames.Count - 1; i++)
                        adapterWires.Add(new Wire($"{ringNames[i]}.stateRprtCmd_out",
                            $"{ringNames[i + 1]}.stateRprtCmd_in"));
                    if (haveProcess)
                    {
                        // Close the ring THROUGH every process in turn:
                        //   compN → P0 → P1 → … → comp0
                        // so each Process FB reads the whole component state ring.
                        // Under the cross-PLC cover ring, the compN(=Clamp)→Process close
                        // is OMITTED — Clamp.out crosses to BX1 (TopCoverSenosr) and the
                        // process's in-edge arrives from BX1 (CoverPnp_Gripper) via the
                        // syslay hop EAE bridges. The Process→comp0 close-back stays local.
                        if (!openM580Boundary)
                            adapterWires.Add(new Wire($"{ringNames[^1]}.stateRprtCmd_out",
                                $"{processNames[0]}.stateRptCmdAdptr_in"));
                        else
                            report.Missing.Add(
                                $"[{tag}] cross-PLC cover ring: left {ringNames[^1]}.stateRprtCmd_out " +
                                $"OPEN (crosses to BX1 TopCoverSenosr) and {processNames[0]}" +
                                ".stateRptCmdAdptr_in OPEN (arrives from BX1 CoverPnp_Gripper) — EAE bridges via syslay");
                        for (int i = 0; i < processNames.Count - 1; i++)
                            adapterWires.Add(new Wire($"{processNames[i]}.stateRptCmdAdptr_out",
                                $"{processNames[i + 1]}.stateRptCmdAdptr_in"));
                        if (ringGateName != null)
                        {
                            adapterWires.Add(new Wire($"{processNames[^1]}.stateRptCmdAdptr_out",
                                $"{ringGateName}.stateRprtCmd_in"));
                            adapterWires.Add(new Wire($"{ringGateName}.stateRprtCmd_out",
                                $"{ringNames[0]}.stateRprtCmd_in"));
                        }
                        else
                        {
                            // CROSS-PLC RING boundary-open: when a cross-PLC hop leaves this resource's
                            // ring open at the boundary, OMIT the local close-back so the boundary
                            // socket is not double-driven (EAE bridges the open ends via the syslay
                            // cross-hops). Robot tail: M580 Disassembly.out crosses to M262 Ejector.
                            // Feed merge: BOTH rings open — M580 Disassembly.out crosses to M262
                            // PartInHopper AND M262 Feed_Station.out crosses to M580 BearingSensor.
                            bool openBoundary =
                                (robotTail && string.Equals(tag, "M580", StringComparison.Ordinal)) ||
                                (feedMerge && (string.Equals(tag, "M580", StringComparison.Ordinal) ||
                                               string.Equals(tag, "M262", StringComparison.Ordinal))) ||
                                // PartAtAssembly cross-ring: M580 omits the Disassembly->BearingSensor
                                // close-back (Disassembly.out crosses to M262 PartAtAssembly, and the
                                // M580 head arrives from PartAtAssembly). M262 needs no open here —
                                // PartAtAssembly is excluded from the Feed ring above, so the Feed
                                // close-back is untouched and the Feed ring stays closed + local.
                                (partBridge && string.Equals(tag, "M580", StringComparison.Ordinal));
                            if (openBoundary)
                                report.Missing.Add(
                                    $"[{tag}] cross-PLC ring: left {processNames[^1]}.stateRptCmdAdptr_out OPEN " +
                                    $"and {ringNames[0]}.stateRprtCmd_in OPEN — EAE bridges via syslay cross-hops");
                            else
                                adapterWires.Add(new Wire($"{processNames[^1]}.stateRptCmdAdptr_out",
                                    $"{ringNames[0]}.stateRprtCmd_in"));
                        }
                    }
                    else if (ringNames.Count > 1)
                    {
                        // BX1: self-close UNLESS the cross-PLC cover ring is active, in
                        // which case the chain is OPEN (last→first omitted) — the two ends
                        // join the M580 ring via the syslay cross-hops EAE bridges.
                        if (ringGateName != null)
                        {
                            adapterWires.Add(new Wire($"{ringNames[^1]}.stateRprtCmd_out",
                                $"{ringGateName}.stateRprtCmd_in"));
                            adapterWires.Add(new Wire($"{ringGateName}.stateRprtCmd_out",
                                $"{ringNames[0]}.stateRprtCmd_in"));
                        }
                        else if (!openBx1Boundary)
                            adapterWires.Add(new Wire($"{ringNames[^1]}.stateRprtCmd_out",
                                $"{ringNames[0]}.stateRprtCmd_in"));
                        else
                            report.Missing.Add(
                                $"[{tag}] cross-PLC cover ring: left {ringNames[^1]}.stateRprtCmd_out " +
                                $"OPEN (crosses to M580 Assembly_Station) and {ringNames[0]}" +
                                ".stateRprtCmd_in OPEN (arrives from M580 Clamp) — EAE bridges via syslay");
                    }
                }

                // STAGE 5b: the M262 ejector→robot tail segment, kept OFF the Feed ring (the
                // ejector+robot were filtered out of ringNames above). Only the intra-M262 hop is
                // local; Ejector.stateRprtCmd_in is left OPEN (arrives from M580 Disassembly) and
                // Robot.stateRprtCmd_out is left OPEN (crosses to M580 BearingSensor) — EAE bridges
                // both via the syslay cross-hops. Only the M262 resource carries both FBs, so this
                // is a no-op on M580/BX1, and a no-op entirely when the flag is off.
                if (robotTail && byName.ContainsKey("Ejector") && byName.ContainsKey("Robot"))
                {
                    adapterWires.Add(new Wire("Ejector.stateRprtCmd_out", "Robot.stateRprtCmd_in"));
                    report.Missing.Add(
                        $"[{tag}] robot tail: Ejector→Robot local segment added; Ejector.stateRprtCmd_in " +
                        "OPEN (from M580 Disassembly), Robot.stateRprtCmd_out OPEN (to M580 BearingSensor)");
                }

                foreach (var w in eventWires)   Process(w, emittedEvents,   "event");
                foreach (var w in DataWires)    Process(w, emittedData,     "data");
                foreach (var w in adapterWires) Process(w, emittedAdapters, "adapter");

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
                    $"[{tag}] wrote {emittedEvents.Count} event + {emittedData.Count} data + " +
                    $"{emittedAdapters.Count} adapter connection(s) to {Path.GetFileName(sysresPath)}");
            }
            catch (Exception ex)
            {
                report.Missing.Add($"[Wire][{anchors.Label}] failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>True when <paramref name="name"/> is non-null and an FB
        /// with that instance name exists on the resource. Used so structural
        /// anchors absent on a given PLC (e.g. BX1 has no Area/Station/Process)
        /// collapse out of the init chain / station chain rather than emitting
        /// dangling wires that fail port validation.</summary>
        private static bool Present(string? name, Dictionary<string, XElement> byName)
            => !string.IsNullOrEmpty(name) && byName.ContainsKey(name);

        /// <summary>
        /// True when the active generated syslay actually carries the M580→BX1
        /// cross-PLC cover-ring hop (Clamp.stateRprtCmd_out → TopCoverSenosr
        /// .stateRprtCmd_in). This is the single source of truth that keeps the
        /// per-resource sysres ring opening EXACTLY when SystemLayoutInjector
        /// spliced the covers in — so the boundary adapter sockets are never both
        /// closed locally AND bridged (which would double-drive them), nor left
        /// dangling on both sides. Returns false (→ keep the local ring CLOSED,
        /// i.e. today's working two-ring behaviour) when the flag is off, the
        /// syslay is missing/unreadable, or the hop is absent.
        /// </summary>
        private static bool CrossPlcCoverRingActive(MapperConfig cfg)
        {
            if (cfg == null || !MapperConfig.ExtendStateRingAcrossBx1) return false;
            try
            {
                var path = cfg.ActiveSyslayPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                var doc = XDocument.Load(path);
                var dns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
                return doc.Descendants(dns + "Connection").Any(c =>
                    string.Equals((string?)c.Attribute("Source"),
                        "Clamp.stateRprtCmd_out", StringComparison.Ordinal) &&
                    string.Equals((string?)c.Attribute("Destination"),
                        "TopCoverSenosr.stateRprtCmd_in", StringComparison.Ordinal));
            }
            catch
            {
                // Any read/parse failure → safest is to keep the ring CLOSED locally.
                return false;
            }
        }

        /// <summary>
        /// STAGE 5b — single source of truth (mirrors <see cref="CrossPlcCoverRingActive"/>):
        /// TRUE when the generated syslay actually carries the M580→M262 robot-tail hop
        /// (Disassembly.stateRptCmdAdptr_out → Ejector.stateRprtCmd_in). When TRUE, every
        /// per-resource sysres ring is opened EXACTLY at the robot-tail boundary: the M262 ring
        /// drops Ejector+Robot (they become a separate Ejector→Robot segment with open ends) and
        /// the M580 ring omits the Disassembly→BearingSensor close-back — so no boundary adapter
        /// plug is driven twice. Returns false (→ keep rings closed locally, today's behaviour)
        /// when the flag is off, the syslay is missing/unreadable, or the hop is absent.
        /// </summary>
        private static bool RobotTailActive(MapperConfig cfg)
        {
            if (cfg == null || !MapperConfig.EnableRobotTaskTail) return false;
            try
            {
                var path = cfg.ActiveSyslayPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                var doc = XDocument.Load(path);
                var dns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
                return doc.Descendants(dns + "Connection").Any(c =>
                    string.Equals((string?)c.Attribute("Source"),
                        "Disassembly.stateRptCmdAdptr_out", StringComparison.Ordinal) &&
                    string.Equals((string?)c.Attribute("Destination"),
                        "Ejector.stateRprtCmd_in", StringComparison.Ordinal));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Single source of truth (mirrors <see cref="RobotTailActive"/>) for the PartAtAssembly
        /// cross-ring (<see cref="MapperConfig.FeedAssemblyPartBridge"/>): TRUE when the generated
        /// syslay actually carries the M580->M262 hop
        /// (Disassembly.stateRptCmdAdptr_out -> PartAtAssembly.stateRprtCmd_in). When TRUE, the M262
        /// sysres keeps PartAtAssembly OUT of the Feed ring (so it never reports into M262's table)
        /// and the M580 sysres omits the Disassembly->BearingSensor close-back — EAE bridges the two
        /// cross-device hops. Returns false (rings closed locally) when the flag is off, the syslay is
        /// missing/unreadable, or the hop is absent.
        /// </summary>
        private static bool PartBridgeActive(MapperConfig cfg)
        {
            if (cfg == null || !MapperConfig.FeedAssemblyPartBridge) return false;
            try
            {
                var path = cfg.ActiveSyslayPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                var doc = XDocument.Load(path);
                var dns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
                return doc.Descendants(dns + "Connection").Any(c =>
                    string.Equals((string?)c.Attribute("Source"),
                        "Disassembly.stateRptCmdAdptr_out", StringComparison.Ordinal) &&
                    string.Equals((string?)c.Attribute("Destination"),
                        "PartAtAssembly.stateRprtCmd_in", StringComparison.Ordinal));
            }
            catch
            {
                return false;
            }
        }

        // Canonical canvas layout — applied to BOTH the sysres
        // (ApplyCanonicalLayout in Emit) and the deployed syslay
        // (ApplyLayoutToSyslay, now invoked at the end of Emit). Single source
        // of truth lives in CodeGen.Mapping.ComponentRegistry — this dictionary
        // is projected from there so the table never drifts.
        //
        // Grid (defined in CodeGen.Mapping.LayoutGrid): 2500-unit horizontal
        // pitch, ~700-1400 vertical pitch, per-PLC actuator row Y. Top-down
        // flow: bootstrap → HMI → structural → sensors+process → actuators.
        // START is EAE's built-in E_RESTART auto-instance at the top-left (NOT
        // in the FBNetwork — can't move it, only avoid it): FB2 sits at x=800
        // y=1100 (below START's footprint); FB1 (DPAC_FULLINIT) top-right.
        // Adding/moving a component = one row in ComponentRegistry.Build();
        // every position consumer here picks it up automatically.
        private static readonly Dictionary<string, (int X, int Y)> CanonicalLayout = BuildCanonicalLayout();

        private static Dictionary<string, (int X, int Y)> BuildCanonicalLayout()
        {
            var dict = new Dictionary<string, (int X, int Y)>(StringComparer.Ordinal);
            foreach (var entry in ComponentRegistry.ByName.Values)
                dict[entry.Name] = (entry.X, entry.Y);
            return dict;
        }

        // Sysres canvases are device-local — the M580 sysres only shows the M580
        // FBs, the BX1 sysres only shows the BX1 FBs. The CanonicalLayout
        // coordinates were authored for the SHARED syslay where every PLC lives
        // on one canvas (M262 at x=2000-9500, M580 at x=12200-27200, BX1 at
        // x=29000+), so re-applying those raw on a single-PLC sysres leaves the
        // M580 chain way off to the right of FB1/FB2. When we're stamping a
        // device-local canvas, pull the present FBs back so their leftmost
        // column lands at this origin — matches the M262 sysres layout (Area at
        // x=2000) and keeps the chain right next to FB1 (x=1900).
        const int DeviceLocalCanvasOriginX = 2000;
        const int DeviceLocalCanvasOriginY = 2000;

        /// <summary>
        /// Force every FB element in <paramref name="byName"/> matching a
        /// CanonicalLayout entry to the spec coordinates, then emit one
        /// <c>[Layout] {Name} -&gt; x=…, y=…</c> line per placed FB. When
        /// <paramref name="translateToOrigin"/> is true (set on M580 / BX1
        /// sysres canvases), all matching entries are shifted as a group so
        /// their bounding-box top-left sits at
        /// (<see cref="DeviceLocalCanvasOriginX"/>, <see cref="DeviceLocalCanvasOriginY"/>);
        /// relative spacing inside the bucket is preserved. The shared syslay
        /// and the M262 sysres pass <c>translateToOrigin=false</c> so they keep
        /// the global SubAppNetwork coordinates.
        /// </summary>
        private static void ApplyCanonicalLayout(Dictionary<string, XElement> byName,
            SystemInjector.BindingApplicationReport report, string source,
            bool translateToOrigin)
        {
            // 1. Collect the present FBs (intersection of byName and CanonicalLayout).
            var present = CanonicalLayout
                .Where(kv => byName.ContainsKey(kv.Key))
                .ToList();
            if (present.Count == 0)
            {
                report.Missing.Add($"[{source} layout] 0/{CanonicalLayout.Count} FBs placed");
                return;
            }

            // 2. Decide the per-axis delta to translate the COMPONENT bucket
            //    (everything except the FB1/FB2 boot pair) onto the device-local
            //    canvas origin. FB1 (DPAC_FULLINIT) and FB2 (plcStart) are the
            //    boot anchors — their canonical positions are intentionally near
            //    (3000, 400) and (800, 1100) so the user sees the same boot row
            //    on every PLC. The component bucket below them then lines up at
            //    y=2000+ matching the M262 sysres (Area_HMI at y=2000, Station1
            //    at y=2900, sensors/processes at y=4000, actuators at y=5400).
            int dx = 0, dy = 0;
            if (translateToOrigin)
            {
                var bootPair = new HashSet<string>(StringComparer.Ordinal) { "FB1", "FB2" };
                var components = present.Where(kv => !bootPair.Contains(kv.Key)).ToList();
                if (components.Count > 0)
                {
                    int minX = components.Min(kv => kv.Value.X);
                    int minY = components.Min(kv => kv.Value.Y);
                    dx = DeviceLocalCanvasOriginX - minX;
                    dy = DeviceLocalCanvasOriginY - minY;
                }
            }

            int placed = 0;
            foreach (var kv in present)
            {
                var fb = byName[kv.Key];
                var oldX = (string?)fb.Attribute("x") ?? "?";
                var oldY = (string?)fb.Attribute("y") ?? "?";
                // FB1/FB2 stay at their CanonicalLayout positions on every PLC
                // (consistent boot-row anchor); only the component bucket
                // translates. M262 sysres uses translateToOrigin=false so this
                // branch reduces to the identity transform there.
                bool isBootPair = string.Equals(kv.Key, "FB1", StringComparison.Ordinal)
                                || string.Equals(kv.Key, "FB2", StringComparison.Ordinal);
                int newX = kv.Value.X + (isBootPair ? 0 : dx);
                int newY = kv.Value.Y + (isBootPair ? 0 : dy);
                fb.SetAttributeValue("x", newX.ToString(System.Globalization.CultureInfo.InvariantCulture));
                fb.SetAttributeValue("y", newY.ToString(System.Globalization.CultureInfo.InvariantCulture));
                report.Missing.Add(
                    $"[{source} layout] {kv.Key}: ({oldX},{oldY}) -> ({newX},{newY})");
                placed++;
            }
            report.Missing.Add(
                $"[{source} layout] {placed}/{CanonicalLayout.Count} FBs placed" +
                (translateToOrigin ? $" (component bucket dx={dx} dy={dy} -> device-local origin; FB1/FB2 fixed)" : ""));
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
                // The shared syslay carries every PLC's FBs on one canvas, so we
                // KEEP the raw CanonicalLayout coords (M262 left, M580 middle,
                // BX1 right). translateToOrigin=false.
                ApplyCanonicalLayout(byName, report, "Syslay", translateToOrigin: false);
                // Grow each coloured zone frame to fully enclose its FBs so
                // nothing overflows the frame edges (the user's "positioning is
                // terrible / overflow" report). Runs AFTER the FBs are placed.
                ResizeFramesToFitFbs(net, ns, report);
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

        // Each coloured zone <Frame> wraps the FBs that BucketFor() routes to its
        // PLC. (BucketFor lives in M262SysdevEmitter — same partitioning the FB
        // mirror uses, so the frame membership and the resource mirror agree.)
        private static readonly Dictionary<string, PlcAssignment> FrameBucket = new(StringComparer.Ordinal)
        {
            { "FRAME_Station1",      PlcAssignment.M262 },
            { "FRAME_Station2_M580", PlcAssignment.M580 },
            { "FRAME_Station2_BX1",  PlcAssignment.BX1  },
        };

        // EAE renders an FB body's height from its INTERFACE port-row count (the
        // size is not stored in the file): height ≈ maxPortRows × ~150 + header.
        // These constants are derived from the ACTUAL port counts of the deployed
        // CATs (measured from the .fbt InterfaceList), NOT guessed — that is what
        // makes the frame sizing and the row spacing below reliable rather than a
        // perpetual under-estimate:
        //   Five_State_Actuator_CAT  = 18 rows  -> ~3050   (the tall data-driven
        //                                                    actuator: Rule[10]×4 +
        //                                                    Target + fault + name/id)
        //   Seven_State_Actuator_CAT = 10 rows  -> ~1850
        //   Process1_Generic         =  9 rows  -> ~1700
        //   Sensor_Bool_CAT          =  3 rows  -> ~800
        //   Station/Area composites  =  2 rows  -> ~650
        //   *_CAT faceplates / terminators (adapter-only) -> small
        private const int FbEstWidth = 900;
        private static int FbEstHeight(string type) => type switch
        {
            "Five_State_Actuator_CAT"            => 3050,   // 18 port rows
            "Five_State_Actuator_No_Sensors_CAT" => 3050,
            "Vacuum_Gripper_CAT"                 => 3050,
            "Seven_State_Actuator_CAT"           => 1850,   // 10 port rows
            "Seven_State_Actuator_Centre_Home_CAT" => 3050, // 16 INIT data inputs + 4 adapters
            "Process1_Generic"                   => 1700,   //  9 port rows
            "Sensor_Bool_CAT"                    => 800,    //  3 port rows
            "Area" or "Station"                  => 650,    //  2 port rows
            "Area_CAT" or "Station_CAT"          => 500,    //  HMI faceplate
            "CaSAdptrTerminator"                 => 450,
            "PLC_RW_M580" or "PLC_RW_BX1" or "PLC_RW_M262" => 1500,
            "DPAC_FULLINIT" or "plcStart"        => 500,
            "MQTT_CONNECTION"                    => 600,
            _                                     => 1500,
        };

        /// <summary>
        /// Resize each coloured zone &lt;Frame&gt; so it fully ENCLOSES the FBs
        /// that belong to its PLC (<see cref="SysresFbMirror.BucketFor"/>), with
        /// padding and a per-Type rendered-height allowance below the lowest FB.
        /// Fixes the overflow where tall Process/actuator bodies and out-of-dict
        /// FBs (Disassembly) spilled past the fixed frame bounds — mirrors how the
        /// SMC_Rig reference sizes each frame to wrap its contents. Frame origins
        /// are clamped to ≥0 so a high FB (MqttConn at y=200) can't push a frame
        /// off-canvas. Best-effort: a frame with no FBs in its bucket is left as-is.
        /// </summary>
        private static void ResizeFramesToFitFbs(XElement net, XNamespace ns,
            SystemInjector.BindingApplicationReport report)
        {
            const int padLeft = 420, padTop = 600, padRight = 460, padBottom = 520;
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            var fbs = new List<(string Name, double X, double Y, string Type)>();
            foreach (var fb in net.Elements(ns + "FB"))
            {
                var name = (string?)fb.Attribute("Name") ?? string.Empty;
                if (name.Length == 0) continue;
                double.TryParse((string?)fb.Attribute("x"), System.Globalization.NumberStyles.Any, inv, out var x);
                double.TryParse((string?)fb.Attribute("y"), System.Globalization.NumberStyles.Any, inv, out var y);
                fbs.Add((name, x, y, (string?)fb.Attribute("Type") ?? string.Empty));
            }

            foreach (var frame in net.Elements(ns + "Frame").ToList())
            {
                var fname = (string?)frame.Attribute("Name") ?? string.Empty;
                if (!FrameBucket.TryGetValue(fname, out var bucket)) continue;
                var inZone = fbs.Where(f => SysresFbMirror.BucketFor(f.Name) == bucket).ToList();
                if (inZone.Count == 0) continue;

                double minX = inZone.Min(f => f.X);
                double minY = inZone.Min(f => f.Y);
                double maxX = inZone.Max(f => f.X + FbEstWidth);
                double maxY = inZone.Max(f => f.Y + FbEstHeight(f.Type));

                // Compute edges, clamp origin to >=0, derive W/H from edges so the
                // clamp never shrinks the bottom/right coverage.
                double fx = Math.Max(0, minX - padLeft);
                double fy = Math.Max(0, minY - padTop);
                double fw = (maxX + padRight) - fx;
                double fh = (maxY + padBottom) - fy;

                frame.SetAttributeValue("X", fx.ToString(inv));
                frame.SetAttributeValue("Y", fy.ToString(inv));
                frame.SetAttributeValue("Width", fw.ToString(inv));
                frame.SetAttributeValue("Height", fh.ToString(inv));
                report.Missing.Add(
                    $"[Layout] frame {fname} ({bucket}) -> X={fx:0} Y={fy:0} W={fw:0} H={fh:0} " +
                    $"encloses {inZone.Count} FB(s)");
            }
        }

        /// <summary>
        /// Locates the deployed .sysres beside the .sysdev whose root
        /// <c>&lt;Device&gt;</c> has the given <paramref name="deviceType"/>
        /// (e.g. "M262_dPAC", "M580_dPAC", "Soft_dPAC") in the SE.DPAC
        /// namespace. Mirrors <c>M262SysdevEmitter.FindSysdevByDeviceType</c>
        /// + <c>FindSysresFor</c> but returns the .sysres path directly.
        /// </summary>
        public static string? LocateSysresByDeviceType(string eaeRoot, string deviceType)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return null;
            // The sysres sits next to the matching sysdev. Pick the .sysres
            // whose enclosing folder is named after a .sysdev whose root
            // Device has the requested Type in SE.DPAC.
            foreach (var sysdev in Directory.EnumerateFiles(systemDir, "*.sysdev", SearchOption.AllDirectories))
            {
                try
                {
                    var doc = XDocument.Load(sysdev);
                    var root = doc.Root;
                    if (root == null) continue;
                    var type = (string?)root.Attribute("Type") ?? string.Empty;
                    var nspace = (string?)root.Attribute("Namespace") ?? string.Empty;
                    if (type != deviceType || nspace != "SE.DPAC") continue;
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
