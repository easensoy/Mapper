using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
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


        // Emitted with the literal name, no port validation; these vary by EMB_RES_ECO canvas variant.
        private static readonly HashSet<string> BuiltInRuntimeFbs = new(StringComparer.Ordinal)
        {
            "START",
            "E_RESTART",
        };

        // Wires one deployed sysres FBNetwork, from the components the sysres actually carries.
        public static void EmitForResource(GenerationContext ctx, string sysresPath,
            ResourcePlan plan, SystemInjector.BindingApplicationReport report)
        {
            var cfg = ctx.Config;
            try
            {
                var tag = plan.Label;
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

                // This is the LAST writer of every sysres, so a relocated component left here is duplicated
                // across two resources. See Docs/PATCH_RATIONALES P-6.
                if (ctx.Profile.PartialRevPi && !plan.Capabilities.ReceivesRelocatedComponents)
                {
                    var relocated = ctx.Profile.RevPiComponents;
                    var dropFbs = fbNet.Elements(ns + "FB")
                        .Where(f => relocated.Contains((string?)f.Attribute("Name") ?? "")).ToList();
                    foreach (var fb in dropFbs) fb.Remove();
                    foreach (var grp in new[] { "EventConnections", "DataConnections", "AdapterConnections" })
                        fbNet.Element(ns + grp)?.Elements(ns + "Connection")
                            .Where(c => relocated.Any(nm =>
                                ((string?)c.Attribute("Source") ?? "").StartsWith(nm + ".", StringComparison.Ordinal) ||
                                ((string?)c.Attribute("Destination") ?? "").StartsWith(nm + ".", StringComparison.Ordinal)))
                            .ToList().ForEach(c => c.Remove());
                    if (dropFbs.Count > 0)
                        report.Missing.Add($"[Wire][{tag}] dropped {dropFbs.Count} RevPi-relocated FB(s) " +
                            $"({string.Join(", ", relocated)}) from the final {Path.GetFileName(sysresPath)} — " +
                            "they live on the RevPi sysres only (prevents duplicate-instance 'Repair Instances').");
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

                // M580/BX1 sysres canvases are device-local; M262 keeps raw coords (its FBs start at x=2000).
                bool translateToOrigin = plan.Capabilities.DeviceLocalCanvas;
                ApplyCanonicalLayout(ctx, byName, report, tag, translateToOrigin);

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

                bool IsSensor(XElement fb) =>
                    TemplateManifest.SensorTypes.Contains((string?)fb.Attribute("Type") ?? string.Empty);
                bool IsActuator(XElement fb) =>
                    TemplateManifest.ActuatorTypes.Contains((string?)fb.Attribute("Type") ?? string.Empty);
                bool HasStationAdapter(XElement fb) =>
                    !TemplateMap.LacksStationAdapter((string?)fb.Attribute("Type"));
                var orderedComps = new List<XElement>();
                var seenComp = new HashSet<string>(StringComparer.Ordinal);
                foreach (var nm in ctx.Roster.CaSBusOrder)
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
                // Driven by the M262->M580 splice, so OFF the local Feed ring; on both it is double-driven.
                var crossSegment = ctx.CrossRingSegment;
                bool robotTail = crossSegment.Count > 0;
                var ringNames = orderedComps.Select(Nm)
                    .Where(s => s.Length > 0)
                    .Where(s => !crossSegment.Contains(s, StringComparer.OrdinalIgnoreCase))
                    // TopCoverSenosr stays ON the ring so its report reaches Assembly's state_table.
                    .ToList();
                var actNames = orderedComps.Where(c => IsActuator(c) && HasStationAdapter(c))
                    .Select(Nm).Where(s => s.Length > 0).ToList();

                var processNames = new List<string>();
                if (Present(plan.ProcessFb, byName))
                    processNames.Add(plan.ProcessFb!);
                foreach (var fb in fbNet.Elements(ns + "FB"))
                {
                    var nm = (string?)fb.Attribute("Name") ?? string.Empty;
                    if (nm.Length == 0 || processNames.Contains(nm)) continue;
                    if ((string?)fb.Attribute("Type") == "Process1_Generic")
                        processNames.Add(nm);
                }
                bool haveProcess = processNames.Count > 0;

                var eventWires = TargetBootstrap.BringUpWires.Select(w => new Wire(w.Source, w.Destination)).ToList();
                var initChain = new List<string> { "FB1" };
                if (Present(plan.AreaFb, byName)) initChain.Add(plan.AreaFb!);
                if (Present(plan.StationFb, byName)) initChain.Add(plan.StationFb!);
                // Members another controller commands init LAST, so their bring-up cannot block this process.
                var robotTailInit = new HashSet<string>(ctx.Rings.DischargeTail, StringComparer.Ordinal);
                initChain.AddRange(initNames.Where(n => !robotTailInit.Contains(n)));
                initChain.AddRange(processNames);
                initChain.AddRange(initNames.Where(n => robotTailInit.Contains(n)));
                for (int i = 0; i < initChain.Count - 1; i++)
                    eventWires.Add(new Wire($"{initChain[i]}.INITO", $"{initChain[i + 1]}.INIT"));

                // EAE runs the sysres event graph, not the syslay, so MqttConn bring-up must be re-emitted
                // here or the broker never opens. Matched by TYPE: each resource has its own.
                foreach (var mqttKv in byName)
                {
                    var mqttType = (string?)mqttKv.Value.Attribute("Type");
                    if (!string.Equals(mqttType, "MQTT_CONNECTION", StringComparison.Ordinal) &&
                        !string.Equals(mqttType, "Telemetry", StringComparison.Ordinal))
                        continue;
                    var mqttName = mqttKv.Key;
                    var mqttInit = Present(plan.AreaFb, byName) ? plan.AreaFb!
                                 : Present(plan.StationFb, byName) ? plan.StationFb!
                                 : "FB1";
                    eventWires.Add(new Wire($"{mqttInit}.INITO", $"{mqttName}.INIT"));
                    eventWires.Add(new Wire($"{mqttName}.INITO", $"{mqttName}.CONNECT"));
                }

                var adapterWires = plan.AdapterRelations.Select(r => new Wire(r.Source, r.Destination)).ToList();

                // Needs a Station and a Process anchor, so BX1 skips it and reaches the ring only.
                var chain = plan.StationChain;
                bool haveStation = chain != null && Present(plan.StationFb, byName);
                if (haveStation && haveProcess)
                {
                    var stationChain = new List<string>(actNames);
                    stationChain.AddRange(processNames);
                    adapterWires.Add(new Wire(chain!.Value.From, $"{stationChain[0]}.stationAdptr_in"));
                    for (int i = 0; i < stationChain.Count - 1; i++)
                        adapterWires.Add(new Wire($"{stationChain[i]}.stationAdptr_out",
                            $"{stationChain[i + 1]}.stationAdptr_in"));
                    if (Present(plan.TerminatorFb, byName))
                        adapterWires.Add(new Wire($"{stationChain[^1]}.stationAdptr_out", chain.Value.To));
                }
                else
                {
                    report.Missing.Add(
                        $"[{tag}] no Station/Process FB on this resource, " +
                        "skipping CaS station chain (init fan-out + report ring still wired)");
                }

                // Report ring. Process1_Generic uses the *Adptr port suffix; CATs use stateRprtCmd_*.
                if (ringNames.Count > 0)
                {
                    for (int i = 0; i < ringNames.Count - 1; i++)
                        adapterWires.Add(new Wire($"{ringNames[i]}.stateRprtCmd_out",
                            $"{ringNames[i + 1]}.stateRprtCmd_in"));
                    if (haveProcess)
                    {
                        // Cover detour (M580): omit the local close, else the boundary plug is double-driven.
                        bool openCoverSeam = plan.Capabilities.OpensCoverSeam;
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
                        // Cross-controller seam: same boundary-open rule.
                        bool openBoundary =
                            (robotTail && plan.Capabilities.OpensCoverSeam) ||
                            (ctx.Rings.RingsMerged && plan.Capabilities.HostsFeedRing);
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
                        // A chain commanded by another controller's ring is OPEN at both ends.
                        bool openCoverChain = plan.Capabilities.CarriesDetouredChain;
                        if (openCoverChain)
                            report.Missing.Add(
                                $"[{tag}] cover detour: cover chain {ringNames[0]}…{ringNames[^1]} ends OPEN " +
                                "(in from M580 Clamp, out to M580 Assembly) — EAE bridges via syslay");
                        else
                            adapterWires.Add(new Wire($"{ringNames[^1]}.stateRprtCmd_out",
                                $"{ringNames[0]}.stateRprtCmd_in"));
                    }
                }

                // M262 cross-ring segment: kept OFF the Feed ring, both ends left OPEN for EAE to bridge.
                var crossSeg = crossSegment
                    .Where(byName.ContainsKey).ToList();
                for (int i = 0; i < crossSeg.Count - 1; i++)
                    adapterWires.Add(new Wire(
                        $"{crossSeg[i]}.stateRprtCmd_out", $"{crossSeg[i + 1]}.stateRprtCmd_in"));
                if (crossSeg.Count > 0)
                {
                    if (ctx.Rings.RingsMerged && ringNames.Count > 0 && plan.Capabilities.HostsFeedRing)
                    {
                        // Merged-ring seam (M262): the segment tail feeds the Feed head locally.
                        adapterWires.Add(new Wire(
                            $"{crossSeg[^1]}.stateRprtCmd_out", $"{ringNames[0]}.stateRprtCmd_in"));
                        report.Missing.Add(
                            $"[{tag}] merged-ring seam: {crossSeg[^1]}.stateRprtCmd_out -> {ringNames[0]} " +
                            "(Feed head, local); seg[0].in + Feed_Station.out OPEN — EAE bridges via syslay");
                    }
                    else
                        report.Missing.Add(
                            $"[{tag}] M262 cross-ring segment {string.Join("->", crossSeg)}: ends OPEN " +
                            "(seg[0].in from M580 Disassembly, seg[^1].out to M580 BearingSensor) — EAE bridges via syslay");
                }

                foreach (var w in eventWires)   Process(w, emittedEvents,   "event");
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
                // Always emit <DataConnections />, even empty: EAE wants "no data wires" stated, not missing.
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
                report.Missing.Add($"[Wire][{plan.Label}] failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool Present(string? name, Dictionary<string, XElement> byName)
            => !string.IsNullOrEmpty(name) && byName.ContainsKey(name);

        // Projected from the run's roster so the canvas table never drifts from the allocation.
        private static void ApplyCanonicalLayout(GenerationContext ctx, Dictionary<string, XElement> byName,
            SystemInjector.BindingApplicationReport report, string source,
            bool translateToOrigin)
        {
            var canonicalLayout = ctx.Roster.All.ToDictionary(
                e => e.Name, e => (X: e.X, Y: e.Y), StringComparer.Ordinal);
            var present = canonicalLayout
                .Where(kv => byName.ContainsKey(kv.Key))
                .ToList();
            if (present.Count == 0)
            {
                report.Missing.Add($"[{source} layout] 0/{canonicalLayout.Count} FBs placed");
                return;
            }

            int dx = 0, dy = 0;
            if (translateToOrigin)
            {
                var bootPair = new HashSet<string>(StringComparer.Ordinal) { "FB1", "FB2" };
                var components = present.Where(kv => !bootPair.Contains(kv.Key)).ToList();
                if (components.Count > 0)
                {
                    int minX = components.Min(kv => kv.Value.X);
                    int minY = components.Min(kv => kv.Value.Y);
                    dx = ctx.Layout.Geometry.DeviceCanvasOrigin.X - minX;
                    dy = ctx.Layout.Geometry.DeviceCanvasOrigin.Y - minY;
                }
            }

            int placed = 0;
            foreach (var kv in present)
            {
                var fb = byName[kv.Key];
                var oldX = (string?)fb.Attribute("x") ?? "?";
                var oldY = (string?)fb.Attribute("y") ?? "?";
                // FB1/FB2 keep their fixed boot-row positions on every PLC.
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
                $"[{source} layout] {placed}/{canonicalLayout.Count} FBs placed" +
                (translateToOrigin ? $" (component bucket dx={dx} dy={dy} -> device-local origin; FB1/FB2 fixed)" : ""));
        }

        public static void ApplyLayoutToSyslay(GenerationContext ctx, string syslayPath,
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
                ApplyCanonicalLayout(ctx, byName, report, "Syslay", translateToOrigin: false);
                ResizeFramesToFitFbs(ctx, net, ns, report);
                var settings = new XmlWriterSettings
                {
                    OmitXmlDeclaration = false,
                    Indent = true,
                    // Emit the UTF-8 BOM so a re-run stays byte-identical to the broker's own BOM save.
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

        // Membership uses BucketFor, the same partition as the FB mirror. Keys MUST match the frame Names.
        private static readonly Dictionary<string, PlcAssignment> FrameBucket = new(StringComparer.Ordinal)
        {
            { "FRAME_Station1",      PlcAssignment.M262 },
            { "FRAME_Station2_M580", PlcAssignment.M580 },
            { "FRAME_BX1",           PlcAssignment.BX1  },
        };


        private static void ResizeFramesToFitFbs(GenerationContext ctx, XElement net, XNamespace ns,
            SystemInjector.BindingApplicationReport report)
        {
            var body = ctx.Layout.FbBody;
            var pad = ctx.Layout.Geometry.FramePadding;
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
                var inZone = fbs.Where(f =>
                {
                    var b = SysresFbMirror.BucketFor(f.Name, ctx.Allocation);
                    if (b == PlcAssignment.RevPi) b = PlcAssignment.M262;
                    return b == bucket;
                }).ToList();
                if (inZone.Count == 0) continue;

                double minX = inZone.Min(f => f.X);
                double minY = inZone.Min(f => f.Y);
                double maxX = inZone.Max(f => f.X + body.Width);
                double maxY = inZone.Max(f => f.Y + body.HeightOf(f.Type));

                // Derive W/H from the edges, so the origin clamp never shrinks bottom/right coverage.
                double fx = Math.Max(0, minX - pad.Left);
                double fy = Math.Max(0, minY - pad.Top);
                double fw = (maxX + pad.Right) - fx;
                double fh = (maxY + pad.Bottom) - fy;

                frame.SetAttributeValue("X", fx.ToString(inv));
                frame.SetAttributeValue("Y", fy.ToString(inv));
                frame.SetAttributeValue("Width", fw.ToString(inv));
                frame.SetAttributeValue("Height", fh.ToString(inv));
                report.Missing.Add(
                    $"[Layout] frame {fname} ({bucket}) -> X={fx:0} Y={fy:0} W={fw:0} H={fh:0} " +
                    $"encloses {inZone.Count} FB(s)");
            }
        }

        private static bool PortExists(HashSet<string> ports, string portName)
            => ports.Count == 0 /* unknown FB type — be lenient */ || ports.Contains(portName);

        // Empty when the type is not found; the caller reads that as "skip validation".
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
