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

        // A resource with no Station/Process/Terminator (BX1) leaves those null; the CaS chain + report ring skip gracefully.
        public sealed record ResourceAnchors(
            string Label,
            string? AreaFb,
            string? StationFb,
            string? ProcessFb,
            string? TerminatorFb,
            IReadOnlyList<Wire> HmiAdapterWires);

        // The init CHAIN is built dynamically in EmitForResource from the components present, so a missing component never severs it.
        private static readonly Wire[] BootstrapEventWires =
        {
            new("START.COLD",          "FB1.INIT"),
            new("START.WARM",          "FB1.INIT"),
            new("START.ONLINECHANGE",  "FB1.OC_RETRIGGER"),
            new("FB2.FIRST_INIT",      "FB2.ACK_FIRST"),
        };

        // FB instance names from Control.xml (NOT hardware aliases): CAT $${PATH} symlinks expand to these, so renaming here severs the chain + bindings.
        private static readonly string[] CaSBusOrder =
        {
            "PartInHopper", "PartAtChecker", "Feeder", "Checker", "Transfer",
            "BearingSensor", "ShaftSensor",
            "Bearing_PnP", "Bearing_Gripper",
            "Shaft_Hr", "Shaft_Vr", "Shaft_Gripper", "Clamp",
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
                "Seven_State_Actuator_Centre_Home_CAT",
                "Vacuum_Gripper_CAT",
                "Robot_Task_CAT",
            };

        // Single source of truth in TemplateMap so the syslay stationChain and this sysres wiring can never drift.
        private static readonly IReadOnlySet<string> NoStationAdapterTypes =
            TemplateMap.NoStationAdapterCatTypes;

        // Hook to exclude a future ring-less CAT from the report ring; kept separate from NoStationAdapterTypes (else ring wires dangle on EAE import).
        private static readonly HashSet<string> NoRingAdapterTypes =
            new(StringComparer.Ordinal);

        internal static readonly Wire[] HmiAdapterWires =
        {
            new("Area_HMI.AreaHMIAdptrOUT",        "Area.AreaHMIAdptrIN"),
            new("Station1_HMI.StationHMIAdptrOUT", "Station1.StationHMIAdptrIN"),
            new("Area.AreaAdptrOUT",               "Station1.AreaAdptrIN"),
            new("Station1.AreaAdptrOUT",           "Area_Term.CasAdptrIN"),
        };

        // Built-in FBs emitted with the literal name (no FBNetwork lookup, no port validation); START/E_RESTART vary by EMB_RES_ECO canvas variant.
        private static readonly HashSet<string> BuiltInRuntimeFbs = new(StringComparer.Ordinal)
        {
            "START",
            "E_RESTART",
        };

        // No sysres-level data wires: athome/atwork/OutputToWork/Input are CAT-body SYMLINK params (fail port validation here); the .hcf binds them via PLC_RW_M262 symlinks.
        private static readonly Wire[] DataWires = Array.Empty<Wire>();

        // Wires one deployed sysres FBNetwork; components discovered from the sysres (CaSBusOrder then declaration order) so chains/ring are N-component-safe. BX1 (no Station/Process/Terminator) gets only the init fan-out + report ring.
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

                // M580/BX1 sysres canvases are device-local, so translate the present FBs to the local origin; M262 keeps raw coords (its FBs already start at x=2000).
                bool translateToOrigin =
                    string.Equals(tag, "M580", StringComparison.Ordinal) ||
                    string.Equals(tag, "BX1",  StringComparison.Ordinal);
                ApplyCanonicalLayout(byName, report, tag, translateToOrigin);

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

                // Component-driven init chain + CaSBus station chain + report ring, built from the components present (CaSBus order then extras) so a missing component never severs them.
                bool IsSensor(XElement fb) =>
                    SensorCatTypes.Contains((string?)fb.Attribute("Type") ?? string.Empty);
                bool IsActuator(XElement fb) =>
                    ActuatorCatTypes.Contains((string?)fb.Attribute("Type") ?? string.Empty);
                bool HasStationAdapter(XElement fb) =>
                    !NoStationAdapterTypes.Contains((string?)fb.Attribute("Type") ?? string.Empty);
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
                var initNames = orderedComps.Select(Nm).Where(s => s.Length > 0).ToList();
                bool robotTail = RobotTailActive(cfg);
                // Ejector/Robot/PartAtAssembly + TopCoverSenosr are kept OFF the Feed ring (driven by the M262->M580 segment / cover detour); on it they'd be double-driven. Mirrors TemplateMap.M262CrossRingSegment.
                var ringNames = orderedComps.Where(HasRingAdapter).Select(Nm)
                    .Where(s => s.Length > 0)
                    .Where(s => !(robotTail &&
                        (string.Equals(s, "Ejector",        StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(s, "Robot",          StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(s, "PartAtAssembly", StringComparison.OrdinalIgnoreCase))))
                    .Where(s => !string.Equals(s, "TopCoverSenosr", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var actNames = orderedComps.Where(c => IsActuator(c) && HasStationAdapter(c))
                    .Select(Nm).Where(s => s.Length > 0).ToList();

                // Every Process1_Generic on this resource (anchor first); the chains/ring below thread through every one. The parked M580 Disassembly is filtered out unless UnparkDisassembly.
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

                // Init chain FB1.INITO->[Area]->[Station]->components...->[Process]; absent anchors collapse out so FB1.INITO fans straight into the first component.
                var eventWires = new List<Wire>(BootstrapEventWires);
                var initChain = new List<string> { "FB1" };
                if (Present(anchors.AreaFb, byName)) initChain.Add(anchors.AreaFb!);
                if (Present(anchors.StationFb, byName)) initChain.Add(anchors.StationFb!);
                // Robot tail (Ejector+Robot) inits LAST so a stall in its cross-PLC bring-up can't block Feed_Station.INIT. M262-only; off -> byte-identical.
                var robotTailInit = RobotTailActive(cfg)
                    ? new HashSet<string>(StringComparer.Ordinal) { "Ejector", "Robot" }
                    : new HashSet<string>(StringComparer.Ordinal);
                initChain.AddRange(initNames.Where(n => !robotTailInit.Contains(n)));
                initChain.AddRange(processNames);
                initChain.AddRange(initNames.Where(n => robotTailInit.Contains(n)));
                for (int i = 0; i < initChain.Count - 1; i++)
                    eventWires.Add(new Wire($"{initChain[i]}.INITO", $"{initChain[i + 1]}.INIT"));

                // MqttConn bring-up MUST be re-emitted onto the sysres (EAE runs the sysres event graph, not the syslay); without it the broker never opens and every embedded MQTT_PUBLISH silently drops. Match by TYPE (each resource has its own MqttConn) not name.
                foreach (var mqttKv in byName)
                {
                    var mqttType = (string?)mqttKv.Value.Attribute("Type");
                    if (!string.Equals(mqttType, "MQTT_CONNECTION", StringComparison.Ordinal) &&
                        !string.Equals(mqttType, "Telemetry", StringComparison.Ordinal))
                        continue;
                    var mqttName = mqttKv.Key;
                    // INIT off the resource boot anchor: Area (M262), else Station (M580), else FB1 (BX1). Self INITO -> CONNECT opens the broker once.
                    var mqttInit = Present(anchors.AreaFb, byName) ? anchors.AreaFb!
                                 : Present(anchors.StationFb, byName) ? anchors.StationFb!
                                 : "FB1";
                    eventWires.Add(new Wire($"{mqttInit}.INITO", $"{mqttName}.INIT"));
                    eventWires.Add(new Wire($"{mqttName}.INITO", $"{mqttName}.CONNECT"));
                }

                var adapterWires = new List<Wire>(anchors.HmiAdapterWires);

                // CaSBus station chain [Station]->actuators...->[Process]->[Terminator]; needs both a Station and a Process anchor, so BX1 skips it (actuators still init + report via the ring).
                bool haveStation = Present(anchors.StationFb, byName);
                if (haveStation && haveProcess)
                {
                    var stationChain = new List<string>(actNames);
                    stationChain.AddRange(processNames);
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

                // stateRprtCmd report ring, closed comp(N).out->comp(N+1).in; with a Process it closes through it, else (BX1) comp(last)->comp(0). Process1_Generic uses the *Adptr suffix; CATs use stateRprtCmd_*.
                if (ringNames.Count > 0)
                {
                    for (int i = 0; i < ringNames.Count - 1; i++)
                        adapterWires.Add(new Wire($"{ringNames[i]}.stateRprtCmd_out",
                            $"{ringNames[i + 1]}.stateRprtCmd_in"));
                    if (haveProcess)
                    {
                        // Cover detour (M580): when covers splice onto the M580 ring, OMIT the local compN->P0 close so the boundary plug isn't double-driven (EAE bridges via syslay). Distinct seam from the robot-tail open below.
                        bool openCoverSeam = string.Equals(tag, "M580", StringComparison.Ordinal);
                        if (openCoverSeam)
                            report.Missing.Add(
                                $"[{tag}] cover detour: left {ringNames[^1]}.stateRprtCmd_out OPEN " +
                                $"(crosses to BX1 covers) and {processNames[0]}.stateRptCmdAdptr_in OPEN " +
                                "(arrives from BX1 CoverPnp_Gripper) — EAE bridges via syslay");
                        else
                            adapterWires.Add(new Wire($"{ringNames[^1]}.stateRprtCmd_out",
                                $"{processNames[0]}.stateRptCmdAdptr_in"));
                        for (int i = 0; i < processNames.Count - 1; i++)
                            adapterWires.Add(new Wire($"{processNames[i]}.stateRptCmdAdptr_out",
                                $"{processNames[i + 1]}.stateRptCmdAdptr_in"));
                        // Boundary-open on the robot-tail (M580) or MergeFeedRing (M262) cross-PLC seams: OMIT the local close-back so the boundary plug isn't double-driven (EAE bridges via syslay). The M262 resource's Label is "Sysres", so its identity check is tag == "Sysres".
                        bool openBoundary =
                            (robotTail && string.Equals(tag, "M580", StringComparison.Ordinal)) ||
                            (CodeGen.Configuration.MapperConfig.MergeFeedRing &&
                             string.Equals(tag, "Sysres", StringComparison.Ordinal));
                        if (openBoundary)
                            report.Missing.Add(
                                $"[{tag}] cross-PLC ring: left {processNames[^1]}.stateRptCmdAdptr_out OPEN " +
                                $"and {ringNames[0]}.stateRprtCmd_in fed via seam — EAE bridges via syslay cross-hops");
                        else
                            adapterWires.Add(new Wire($"{processNames[^1]}.stateRptCmdAdptr_out",
                                $"{ringNames[0]}.stateRprtCmd_in"));
                    }
                    else if (ringNames.Count > 1)
                    {
                        // Cover detour (BX1): when covers are commanded by the M580 ring the BX1 cover chain is OPEN at both ends — OMIT the self-close (EAE bridges via syslay). Off -> BX1 self-closes the broadcast loop locally.
                        bool openCoverChain = string.Equals(tag, "BX1", StringComparison.Ordinal);
                        if (openCoverChain)
                            report.Missing.Add(
                                $"[{tag}] cover detour: cover chain {ringNames[0]}…{ringNames[^1]} ends OPEN " +
                                "(in from M580 Clamp, out to M580 Assembly) — EAE bridges via syslay");
                        else
                            adapterWires.Add(new Wire($"{ringNames[^1]}.stateRprtCmd_out",
                                $"{ringNames[0]}.stateRprtCmd_in"));
                    }
                }

                // M262 cross-ring segment (intra-M262 chain kept OFF the Feed ring); seg[0].in and seg[^1].out stay OPEN (EAE bridges via syslay). No-op on M580/BX1 and when both flags are off.
                var crossSeg = TemplateMap.M262CrossRingSegment(robotTail)
                    .Where(byName.ContainsKey).ToList();
                for (int i = 0; i < crossSeg.Count - 1; i++)
                    adapterWires.Add(new Wire(
                        $"{crossSeg[i]}.stateRprtCmd_out", $"{crossSeg[i + 1]}.stateRprtCmd_in"));
                if (crossSeg.Count > 0)
                {
                    if (CodeGen.Configuration.MapperConfig.MergeFeedRing && ringNames.Count > 0 &&
                        string.Equals(tag, "Sysres", StringComparison.Ordinal)) // "Sysres" = the M262 anchors' Label
                    {
                        // MergeFeedRing seam (M262): the segment tail feeds the Feed head locally so discharge segment + Feed chain are one continuous chain; seg[0].in and Feed_Station.out stay OPEN (EAE bridges via syslay).
                        adapterWires.Add(new Wire(
                            $"{crossSeg[^1]}.stateRprtCmd_out", $"{ringNames[0]}.stateRprtCmd_in"));
                        report.Missing.Add(
                            $"[{tag}] MergeFeedRing seam: {crossSeg[^1]}.stateRprtCmd_out -> {ringNames[0]} " +
                            "(Feed head, local); seg[0].in + Feed_Station.out OPEN — EAE bridges via syslay");
                    }
                    else
                        report.Missing.Add(
                            $"[{tag}] M262 cross-ring segment {string.Join("->", crossSeg)}: ends OPEN " +
                            "(seg[0].in from M580 Disassembly, seg[^1].out to M580 BearingSensor) — EAE bridges via syslay");
                }

                foreach (var w in eventWires)   Process(w, emittedEvents,   "event");
                foreach (var w in DataWires)    Process(w, emittedData,     "data");
                foreach (var w in adapterWires) Process(w, emittedAdapters, "adapter");

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
                // Always emit <DataConnections />, even empty, so EAE sees an explicit "no data wires" rather than a missing block.
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

        private static bool Present(string? name, Dictionary<string, XElement> byName)
            => !string.IsNullOrEmpty(name) && byName.ContainsKey(name);

        // Reads the SAME authority (HandoffPlanner.DischargeActive) BuildStation2Wiring uses for the syslay cross-hops, so the sysres ring topology follows the decision that shaped the syslay; TRUE opens every per-resource ring at the robot-tail boundary.
        private static bool RobotTailActive(MapperConfig cfg)
            => CodeGen.Translation.HandoffPlanner.DischargeActive;

        // Projected from ComponentRegistry (single source of truth) so the table never drifts; applied to both the sysres and the syslay.
        private static readonly Dictionary<string, (int X, int Y)> CanonicalLayout = BuildCanonicalLayout();

        private static Dictionary<string, (int X, int Y)> BuildCanonicalLayout()
        {
            var dict = new Dictionary<string, (int X, int Y)>(StringComparer.Ordinal);
            foreach (var entry in ComponentRegistry.ByName.Values)
                dict[entry.Name] = (entry.X, entry.Y);
            return dict;
        }

        // Device-local sysres canvases (M580/BX1) translate the present FBs back to this origin; the shared syslay's raw coords would leave them off-canvas.
        const int DeviceLocalCanvasOriginX = 2000;
        const int DeviceLocalCanvasOriginY = 2000;

        // translateToOrigin=true (M580/BX1) shifts the group's bounding-box top-left to the device-local origin; false (syslay + M262 sysres) keeps global coordinates.
        private static void ApplyCanonicalLayout(Dictionary<string, XElement> byName,
            SystemInjector.BindingApplicationReport report, string source,
            bool translateToOrigin)
        {
            var present = CanonicalLayout
                .Where(kv => byName.ContainsKey(kv.Key))
                .ToList();
            if (present.Count == 0)
            {
                report.Missing.Add($"[{source} layout] 0/{CanonicalLayout.Count} FBs placed");
                return;
            }

            // Delta translates the component bucket (all but the FB1/FB2 boot pair, which stay fixed) onto the device-local origin.
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
                // FB1/FB2 keep their fixed boot-row positions on every PLC; only the component bucket translates.
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
                ApplyCanonicalLayout(byName, report, "Syslay", translateToOrigin: false);
                ResizeFramesToFitFbs(net, ns, report);
                var settings = new XmlWriterSettings
                {
                    OmitXmlDeclaration = false,
                    Indent = true,
                    // Emit the UTF-8 BOM so a re-run stays byte-identical to the broker's own BOM save (no spurious encoding diff).
                    Encoding = new UTF8Encoding(true),
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

        // Frame membership uses BucketFor (the same partition as the FB mirror) so frames and the resource mirror agree. Keys MUST match the emitted frame Names.
        private static readonly Dictionary<string, PlcAssignment> FrameBucket = new(StringComparer.Ordinal)
        {
            { "FRAME_Station1",      PlcAssignment.M262 },
            { "FRAME_Station2_M580", PlcAssignment.M580 },
            { "FRAME_BX1",           PlcAssignment.BX1  },
        };

        // Per-Type body-size allowance, kept ~15-20% above the observed EAE render so a MoveStyle="AnyContained" frame still ENCLOSES the body (no overflow); raise a type only if EAE shows it overflowing.
        private const int FbEstWidth = 1400;
        private static int FbEstHeight(string type) => type switch
        {
            "Five_State_Actuator_CAT"            => 1800,
            "Five_State_Actuator_No_Sensors_CAT" => 1800,
            "Vacuum_Gripper_CAT"                 => 1800,
            "Seven_State_Actuator_CAT"           => 1500,
            "Seven_State_Actuator_Centre_Home_CAT" => 1800,
            "Robot_Task_CAT"                     => 1500,
            "Process1_Generic"                   => 1000,
            "Sensor_Bool_CAT"                    => 650,
            "Area" or "Station"                  => 600,
            "Area_CAT" or "Station_CAT"          => 500,
            "CaSAdptrTerminator"                 => 450,
            "PLC_RW_M580" or "PLC_RW_BX1" or "PLC_RW_M262" => 1200,
            "DPAC_FULLINIT" or "plcStart"        => 500,
            "MQTT_CONNECTION"                    => 600,
            "Telemetry"                          => 800,
            _                                     => 1100,
        };

        // Grow each zone <Frame> to enclose the FBs its PLC owns (BucketFor); origins clamped to >=0. Best-effort: a frame with no FBs in its bucket is left as-is.
        private static void ResizeFramesToFitFbs(XElement net, XNamespace ns,
            SystemInjector.BindingApplicationReport report)
        {
            // Left pad is widest so the ring wires that loop out the FBs' left edges stay inside the frame.
            const int padLeft = 500, padTop = 220, padRight = 250, padBottom = 260;
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
                // RevPi Feed FBs live in the M262 (Feed) zone frame — byte-identical for M262 (none guess RevPi).
                var inZone = fbs.Where(f =>
                {
                    var b = SysresFbMirror.BucketFor(f.Name);
                    if (b == PlcAssignment.RevPi) b = PlcAssignment.M262;
                    return b == bucket;
                }).ToList();
                if (inZone.Count == 0) continue;

                double minX = inZone.Min(f => f.X);
                double minY = inZone.Min(f => f.Y);
                double maxX = inZone.Max(f => f.X + FbEstWidth);
                double maxY = inZone.Max(f => f.Y + FbEstHeight(f.Type));

                // Derive W/H from the edges (not width/height directly) so the origin clamp never shrinks the bottom/right coverage.
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

        // Locates the deployed .sysres beside the .sysdev whose root <Device> has the given deviceType ("M262_dPAC"/"M580_dPAC"/"Soft_dPAC") in SE.DPAC.
        public static string? LocateSysresByDeviceType(string eaeRoot, string deviceType)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return null;
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

        // Returns the InterfaceList port Names for typeName; empty if not found (caller treats empty as "skip validation" for library-external types like DPAC_FULLINIT/plcStart).
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
                // Fallback glob for types that ship as flat files (Area, Station, CaSAdptrTerminator).
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
