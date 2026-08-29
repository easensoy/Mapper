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

        // Wires the resource this target runs on. One owner for every target: which sysres file that
        // is comes from the target's declared device type, and what goes on it comes from the plan.
        //
        // The parameter sync runs BOTH sides of the wiring pass - before, so the wiring sees the FBs it
        // is about to connect, and after, because rebuilding the FBNetwork would otherwise ship a stale
        // recipe on a resource that deploys perfectly well.
        public static void WireResource(GenerationContext ctx, PlcAssignment plc,
            SystemInjector.BindingApplicationReport report)
        {
            var plan = ctx.ResourceFor(plc);
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(ctx.Cfg);
            if (eaeRoot == null)
            {
                report.Missing.Add($"[Wire][{plan.Label}] skipped, EAE project root not derivable");
                return;
            }
            var sysdev = EaeProjectLayout.FindSysdevByDeviceType(eaeRoot, ctx.Targets.Of(plc).DeviceType);
            var sysres = sysdev == null ? null : EaeProjectLayout.FindSysresFor(sysdev);
            if (sysres == null)
            {
                report.Missing.Add($"[Wire][{plan.Label}] skipped, its resource is not on disk");
                return;
            }

            void Sync(string when)
            {
                var n = SysresFbMirror.SyncMirroredFbParametersFromSyslay(ctx.Config.ActiveSyslayPath, sysres,
                    ctx.Cfg.Generation.ProjectNamespace);
                if (n > 0)
                    report.Missing.Add($"[Wire][{plan.Label}] {when}synced {n} mirrored FB parameter set(s)");
            }

            Sync(string.Empty);
            EmitForResource(ctx, sysres, plan, report);
            Sync("post-wire ");
        }

        // Wires one deployed sysres FBNetwork, from the components the sysres actually carries.
        public static void EmitForResource(GenerationContext ctx, string sysresPath,
            ResourcePlan plan, SystemInjector.BindingApplicationReport report)
        {
            var cfg = ctx.Cfg;
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
                if (ctx.Profile.HasAssignments && !plan.Capabilities.StandsInForAnother)
                {
                    var relocated = ctx.Profile.Assignments.Keys;
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
                    cfg.Paths.ActiveSyslayPath, doc, cfg.Manifest, cfg.Generation.ProjectNamespace);
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
                    sink.Add(($"{srcName}.{srcPort}", $"{dstName}.{dstPort}"));
                    report.Missing.Add($"[{tag}] {srcName}.{srcPort} -> {dstName}.{dstPort}");
                }

                // The SAME graph the shared canvas was drawn from, projected into this resource's own
                // order. Membership, seams and chain endpoints were decided there; presence is the only
                // thing left to check here, because a wire to an FB the mirror did not create is a wire
                // to nothing. See ChainOrder for why the two orders are not normalised into one.
                var graph = ResourceWiringPlanner.For(ctx, plan.Plc, ChainOrder.Resource);
                bool Here(string? name) => Present(name, byName);

                var eventWires = ctx.Targets.BringUp.Select(w => new Wire(w.Source, w.Destination)).ToList();
                var initChain = graph.InitChain.Where(Here).ToList();
                for (int i = 0; i < initChain.Count - 1; i++)
                    eventWires.Add(new Wire($"{initChain[i]}.INITO", $"{initChain[i + 1]}.INIT"));

                // EAE runs the sysres event graph, not the syslay, so the connection's bring-up must be
                // re-emitted here or the broker never opens.
                foreach (var (source, destination) in graph.ConnectionLinks)
                    eventWires.Add(new Wire(source, destination));

                foreach (var seam in graph.OpenSeams) report.Missing.Add($"[{tag}] {seam}");

                var adapterWires = graph.AdapterRelations
                    .Concat(graph.StationLinks)
                    .Concat(graph.RingLinks)
                    .Concat(graph.SegmentLinks)
                    .Select(r => new Wire(r.Source, r.Destination)).ToList();

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
            // A boot FB is emitted under its declared role name, so the roles ARE the boot instances.
            var bootRoles = ctx.Targets.BootRoles;
            if (translateToOrigin)
            {
                var components = present.Where(kv => !bootRoles.Contains(kv.Key)).ToList();
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
                // A boot FB keeps its fixed boot-row position on every target.
                bool isBoot = bootRoles.Contains(kv.Key);
                int newX = kv.Value.X + (isBoot ? 0 : dx);
                int newY = kv.Value.Y + (isBoot ? 0 : dy);
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


        // A target that RECEIVES relocated components draws no zone of its own: the components came from
        // another target's station and are drawn inside that station's frame, which is the target hosting
        // the same station without receiving anything.
        private static PlcAssignment FrameOwner(PlcAssignment bucket, Mapping.TargetIndex targets)
        {
            return targets.IsRegistered(bucket)
                ? targets.RingHostOf(targets.Of(bucket))
                : bucket;
        }

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
                // Which target a frame belongs to is layout.yml's, declared beside the frame itself.
                var owner = ctx.Layout.Bands.FirstOrDefault(b =>
                    string.Equals(b.Frame?.Name, fname, StringComparison.Ordinal));
                if (owner == null) continue;
                // Membership uses BucketFor, the same partition as the FB mirror.
                var inZone = fbs.Where(f =>
                    FrameOwner(SysresFbMirror.BucketFor(f.Name, ctx.Allocation, ctx.Cfg), ctx.Targets) == owner.Plc).ToList();
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
                    $"[Layout] frame {fname} ({owner.Plc}) -> X={fx:0} Y={fy:0} W={fw:0} H={fh:0} " +
                    $"encloses {inZone.Count} FB(s)");
            }
        }

    }
}
