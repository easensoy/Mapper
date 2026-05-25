using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Translation;

namespace CodeGen.Devices.M262
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

        private static readonly HashSet<string> SensorCatTypes =
            new(StringComparer.Ordinal) { "Sensor_Bool_CAT" };
        private static readonly HashSet<string> ActuatorCatTypes =
            new(StringComparer.Ordinal)
            {
                "Five_State_Actuator_CAT",
                "Five_State_Actuator_No_Sensors_CAT",
                // Seven_State_Actuator_CAT — Bearing_PnP (13-state PARALLEL+
                // ALTERNATIVE branched) routes here. It is INIT-chained like
                // any actuator BUT, unlike Five_State, its .fbt declares NO CaS
                // adapter ports (no stationAdptr_*/stateRprtCmd_*), so it is
                // excluded from the station chain + report ring via
                // NoCaSAdapterTypes below. (The prior "same adapter port names"
                // claim was stale — verified against the zipped .fbt.)
                "Seven_State_Actuator_CAT",
                "Vacuum_Gripper_CAT",
            };

        // CAT types whose .fbt declares NO CaS adapter ports
        // (stationAdptr_in/out, stateRprtCmd_in/out). These are still INIT-
        // chained (they expose INIT/INITO) but are excluded from the CaSBus
        // station chain and the stateRprtCmd report ring, otherwise the emitter
        // would write dangling adapter wires to ports that don't exist (EAE
        // flags them on import). Seven_State_Actuator_CAT (Bearing_PnP) is the
        // only such type present in the rig; verified against the zipped .fbt.
        private static readonly HashSet<string> NoCaSAdapterTypes =
            new(StringComparer.Ordinal) { "Seven_State_Actuator_CAT" };

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

        // ── M262 Feed-Station structural anchors. These are the ONLY
        //    M262-specific names; everything component-driven is built from
        //    the CATs present in the sysres. Passed to EmitForResource so the
        //    same wiring core serves the M580/BX1 with their own anchors. The
        //    label "Sysres" is preserved so the M262 activity-log lines and
        //    file output stay byte-identical to the pre-generalisation path.
        private static readonly ResourceAnchors M262Anchors = new(
            Label:        "Sysres",
            AreaFb:       "Area",
            StationFb:    "Station1",
            ProcessFb:    "Feed_Station",
            TerminatorFb: "Stn1_Term",
            HmiAdapterWires: HmiAdapterWires);

        /// <summary>
        /// M262 Feed-Station entry point. UNCHANGED behaviour: locates the
        /// M262 sysres by device Type, wires it with the M262 anchors, and
        /// mirrors the canonical layout onto the deployed syslay. The wiring
        /// inputs (sysres path, anchors, layout) are identical to the
        /// pre-generalisation code path, so the emitted bytes are unchanged.
        /// </summary>
        public static void Emit(MapperConfig cfg,
            SystemInjector.BindingApplicationReport report)
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
            EmitForResource(cfg, sysresPath, M262Anchors, report);

            // Mirror the SAME CanonicalLayout onto the deployed syslay so the
            // EAE application canvas reads cleanly too. Best-effort:
            // ApplyLayoutToSyslay silently skips if the file/root is missing.
            // syslay-only; the M580/BX1 share this single application canvas so
            // EmitForResource intentionally does NOT touch it (only the M262
            // path mirrors layout to keep that output unchanged).
            ApplyLayoutToSyslay(cfg.ActiveSyslayPath, report);
        }

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
                // FBs present on THIS resource are moved).
                ApplyCanonicalLayout(byName, report, tag);

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
                // True when the FB's type exposes the CaS adapter ports
                // (stationAdptr_*/stateRprtCmd_*). Seven_State_Actuator_CAT does
                // NOT, so it is INIT-chained but kept out of the station chain
                // and report ring. (M262's components are all Sensor/Five_State,
                // which DO have the ports — so this filters nothing on M262 and
                // its output is unchanged.)
                bool HasCaSAdapter(XElement fb) =>
                    !NoCaSAdapterTypes.Contains((string?)fb.Attribute("Type") ?? string.Empty);

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
                // initNames — every component, in CaSBus order, for the INIT
                // chain (all CATs expose INIT/INITO).
                var initNames = orderedComps.Select(Nm).Where(s => s.Length > 0).ToList();
                // ringNames — components that expose the stateRprtCmd adapter
                // (excludes Seven_State); used for the report ring.
                var ringNames = orderedComps.Where(HasCaSAdapter).Select(Nm)
                    .Where(s => s.Length > 0).ToList();
                // actNames — actuators that expose the stationAdptr adapter
                // (excludes Seven_State); used for the CaS station chain.
                var actNames = orderedComps.Where(c => IsActuator(c) && HasCaSAdapter(c))
                    .Select(Nm).Where(s => s.Length > 0).ToList();

                // Init chain: FB1.INITO→[Area]→[Station]→components…→[Process],
                // wiring INITO(N)→INIT(N+1) so every node is reached. Anchors
                // that are null/absent on this resource (e.g. BX1 has no Area /
                // Station / Process) collapse out, leaving FB1.INITO fanning
                // straight into the first component so actuators still init.
                var eventWires = new List<Wire>(BootstrapEventWires);
                var initChain = new List<string> { "FB1" };
                if (Present(anchors.AreaFb, byName)) initChain.Add(anchors.AreaFb!);
                if (Present(anchors.StationFb, byName)) initChain.Add(anchors.StationFb!);
                initChain.AddRange(initNames);
                if (Present(anchors.ProcessFb, byName)) initChain.Add(anchors.ProcessFb!);
                for (int i = 0; i < initChain.Count - 1; i++)
                    eventWires.Add(new Wire($"{initChain[i]}.INITO", $"{initChain[i + 1]}.INIT"));

                var adapterWires = new List<Wire>(anchors.HmiAdapterWires);

                // CaSBus station chain — actuators + process ONLY (Sensor_Bool_CAT
                // has no stationAdptr ports): [Station]→actuators…→[Process],
                // [Process] closed to [Terminator]. Requires BOTH a Station
                // anchor (chain source) and a Process anchor (chain tail). BX1
                // has neither, so this whole block is skipped — its actuators
                // still initialise via the fan-out above and report via the
                // ring below.
                bool haveStation = Present(anchors.StationFb, byName);
                bool haveProcess = Present(anchors.ProcessFb, byName);
                if (haveStation && haveProcess)
                {
                    var stationChain = new List<string>(actNames) { anchors.ProcessFb! };
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
                if (ringNames.Count > 0)
                {
                    for (int i = 0; i < ringNames.Count - 1; i++)
                        adapterWires.Add(new Wire($"{ringNames[i]}.stateRprtCmd_out",
                            $"{ringNames[i + 1]}.stateRprtCmd_in"));
                    if (haveProcess)
                    {
                        adapterWires.Add(new Wire($"{ringNames[^1]}.stateRprtCmd_out",
                            $"{anchors.ProcessFb}.stateRptCmdAdptr_in"));
                        adapterWires.Add(new Wire($"{anchors.ProcessFb}.stateRptCmdAdptr_out",
                            $"{ringNames[0]}.stateRprtCmd_in"));
                    }
                    else if (ringNames.Count > 1)
                    {
                        adapterWires.Add(new Wire($"{ringNames[^1]}.stateRprtCmd_out",
                            $"{ringNames[0]}.stateRprtCmd_in"));
                    }
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
            // Actuator row (y=5400) — 2500 pitch, terminator far right.
            // Use the Control.xml component name "Feeder" as the FB Name
            // attribute; the previous "Pusher" alias broke symbolic-link
            // PATH expansion (CAT SYMLINKMULTIVARDST $${PATH} macros
            // resolved to Pusher.athome instead of Feeder.athome and lost
            // their channel bindings). Hardware-side hardware names
            // (PusherAtHome, ExtendPusher) stay inside the .hcf only.
            { "Feeder",       (2000, 5400) },
            { "Checker",      (4500, 5400) },
            { "Transfer",     (7000, 5400) },
            { "Stn1_Term",    (9500, 5400) },
            // M580 zone (Station 2 — purple frame). Reference-style columns
            // (SMC_Rig_Expo_withClamp stacks its 8 M580 actuators in ONE vertical
            // column at constant x with ~1060 row pitch, processes in a separate
            // column to the right). We mirror that:
            //   x=12300  actuator column  (6 actuators, 1300 pitch)
            //   x=14800  sensor column    (2 sensors)
            //   x=17300  process column   (Assembly_Station, Disassembly — tall,
            //                               3000 pitch so the Process bodies clear)
            //   y=2000   structural row   (Station2_HMI, Station2, Stn2_Term)
            // No two FBs share a slot (the old layout stacked Bearing_PnP and
            // Bearing_Gripper on the SAME (12200,5400) point, and put the tall
            // Assembly Process directly above an actuator). FRAME sizing is now
            // dynamic (ResizeFramesToFitFbs) so the purple frame grows to enclose
            // whatever lands here — no overflow regardless of FB count.
            { "Station2_HMI",    (12300, 2000) },
            { "Station2",        (14800, 2000) },
            { "Stn2_Term",       (17300, 2000) },
            // actuator column (x=12300)
            { "Bearing_PnP",     (12300, 3400) },
            { "Bearing_Gripper", (12300, 4700) },
            { "Shaft_Hr",        (12300, 6000) },
            { "Shaft_Vr",        (12300, 7300) },
            { "Shaft_Gripper",   (12300, 8600) },
            { "Clamp",           (12300, 9900) },
            // sensor column (x=14800)
            { "BearingSensor",   (14800, 3400) },
            { "ShaftSensor",     (14800, 4700) },
            // process column (x=17300) — Process1_Generic bodies are tall
            { "Assembly_Station",(17300, 3400) },
            { "Disassembly",     (17300, 6400) },
            // BX1 zone (Station 2 — green frame). Actuator column at x=20700
            // (1300 pitch) with the top-cover sensor beside it; mirrors the M580
            // column style. Dynamic frame sizing encloses them.
            { "TopCoverSenosr",  (23200, 3400) },
            { "CoverPNP_Hr",     (20700, 3400) },
            { "CoverPNP_Vr",     (20700, 4700) },
            { "CoverPnp_Gripper",(20700, 6000) },
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

        // EAE computes an FB body's rendered height from its port count (the size
        // is NOT in the file), so to size enclosing frames we estimate height per
        // Type. Width is ~uniform. These only drive frame bounds — generous is fine.
        private const int FbEstWidth = 720;
        private static int FbEstHeight(string type) => type switch
        {
            "Process1_Generic"                   => 2600,
            "Seven_State_Actuator_CAT"           => 1500,
            "Five_State_Actuator_CAT"            => 1300,
            "Five_State_Actuator_No_Sensors_CAT" => 1300,
            "Vacuum_Gripper_CAT"                 => 1300,
            "Sensor_Bool_CAT"                    => 900,
            "Area" or "Area_CAT"                 => 1200,
            "Station" or "Station_CAT"           => 1200,
            "CaSAdptrTerminator"                 => 700,
            "PLC_RW_M580" or "PLC_RW_BX1" or "PLC_RW_M262" => 1800,
            "DPAC_FULLINIT" or "plcStart"        => 700,
            "MQTT_CONNECTION"                    => 900,
            _                                     => 1300,
        };

        /// <summary>
        /// Resize each coloured zone &lt;Frame&gt; so it fully ENCLOSES the FBs
        /// that belong to its PLC (<see cref="M262SysdevEmitter.BucketFor"/>), with
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
                var inZone = fbs.Where(f => M262SysdevEmitter.BucketFor(f.Name) == bucket).ToList();
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

        private static string? LocateM262Sysres(string eaeRoot)
            => LocateSysresByDeviceType(eaeRoot, "M262_dPAC");

        /// <summary>
        /// Locates the deployed .sysres beside the .sysdev whose root
        /// <c>&lt;Device&gt;</c> has the given <paramref name="deviceType"/>
        /// (e.g. "M262_dPAC", "M580_dPAC", "Soft_dPAC") in the SE.DPAC
        /// namespace. Mirrors <c>M262SysdevEmitter.FindSysdevByDeviceType</c>
        /// + <c>FindSysresFor</c> but returns the .sysres path directly.
        /// </summary>
        private static string? LocateSysresByDeviceType(string eaeRoot, string deviceType)
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

        // ── Station-2 structural anchors. The M580 carries the full
        //    Assembly_Station slice (Station + Process + Terminator); the BX1
        //    carries only the cover pick-and-place actuators + sensor with NO
        //    Station/Process/Terminator of its own in this increment, so its
        //    anchors are all null and EmitForResource gives it just the INIT
        //    fan-out + the report ring among the BX1 components.
        private static readonly ResourceAnchors M580Anchors = new(
            Label:        "M580",
            AreaFb:       null,                 // Area lives on the M262 only
            StationFb:    "Station2",
            ProcessFb:    "Assembly_Station",
            TerminatorFb: "Stn2_Term",
            HmiAdapterWires: new[]
            {
                // Station2 faceplate; no Area on this resource so no Area ring.
                new Wire("Station2_HMI.StationHMIAdptrOUT", "Station2.StationHMIAdptrIN"),
            });

        private static readonly ResourceAnchors BX1Anchors = new(
            Label:        "BX1",
            AreaFb:       null,
            StationFb:    null,                 // no Station FB on BX1 (graceful skip)
            ProcessFb:    null,                 // no Process FB on BX1
            TerminatorFb: null,
            HmiAdapterWires: Array.Empty<Wire>());

        /// <summary>
        /// Wires the deployed M580 + BX1 sysres FBNetworks (each located by
        /// device Type) with the SAME proven topology core as the M262, using
        /// each PLC's own structural anchors. Additive — does NOT touch the
        /// M262 sysres or the shared application syslay. The M580 gets the full
        /// init chain + CaS station chain + report ring; the BX1 (no Station FB)
        /// gets the init fan-out + report ring only. Returns true if at least
        /// one resource was located and wired.
        /// </summary>
        public static void EmitStation2Resources(MapperConfig cfg,
            SystemInjector.BindingApplicationReport report)
        {
            var eaeRoot = M262SysdevEmitter.DeriveEaeProjectRoot(cfg);
            if (eaeRoot == null)
            {
                report.Missing.Add("[Wire][Stn2] skipped, EAE project root not derivable");
                return;
            }

            var m580 = LocateSysresByDeviceType(eaeRoot, "M580_dPAC");
            if (m580 != null) EmitForResource(cfg, m580, M580Anchors, report);
            else report.Missing.Add("[Wire][M580] skipped, M580 sysres not found");

            var bx1 = LocateSysresByDeviceType(eaeRoot, "Soft_dPAC");
            if (bx1 != null) EmitForResource(cfg, bx1, BX1Anchors, report);
            else report.Missing.Add("[Wire][BX1] skipped, BX1 sysres not found");
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
