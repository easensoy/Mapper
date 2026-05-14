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
    /// (plcStart → DPAC_FULLINIT → Area → Station → … → Feeder → M262IO)
    /// and wires the Pusher I/O bindings (PusherAtHome → Feeder.athome, etc.).
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

        // Minimal init chain — just enough to bring M262IO online so the
        // pusher cylinder can be force-toggled from EAE's Watch list. Area /
        // Station / Process / HMI / Terminator wiring is deferred until each
        // adapter port has been validated against its CAT .fbt — emitting
        // unverified ports caused EAE compile failures.
        private static readonly Wire[] EventWires =
        {
            // Bridge the resource's built-in E_RESTART FB into plcStart so
            // the init chain actually fires on COLD or WARM start. E_RESTART
            // is auto-instantiated by every EMB_RES_ECO resource canvas — do
            // NOT emit it into <FBNetwork>; reference it by literal name only.
            new("E_RESTART.COLD",      "plcStart.ACK_FIRST"),
            new("E_RESTART.WARM",      "plcStart.ACK_FIRST"),
            new("plcStart.FIRST_INIT", "DPAC_FULLINIT.INIT"),
            new("DPAC_FULLINIT.INITO", "M262IO.INIT"),
        };

        // Endpoints whose LHS is one of these built-in FBs are emitted with
        // the literal name (no FBNetwork lookup, no port validation).
        private static readonly HashSet<string> BuiltInRuntimeFbs = new(StringComparer.Ordinal)
        {
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
                        report.Missing.Add($"[Wire] port not found: {srcType}.{srcPort}");
                        return;
                    }
                    if (!dstBuiltIn && !PortExists(PortsFor(dstType), dstPort))
                    {
                        report.Missing.Add($"[Wire] port not found: {dstType}.{dstPort}");
                        return;
                    }
                    sink.Add(($"{srcName}.{srcPort}", $"{dstName}.{dstPort}"));
                    report.Missing.Add($"[Wire] {srcName}.{srcPort} → {dstName}.{dstPort}");
                }

                foreach (var w in EventWires) Process(w, emittedEvents, "event");
                foreach (var w in DataWires)  Process(w, emittedData,  "data");

                // Replace any existing connection blocks. EAE accepts either
                // EventConnections or AdapterConnections as a sibling under
                // FBNetwork; we follow the spec and put adapter wires inside
                // EventConnections.
                fbNet.Elements(ns + "EventConnections").Remove();
                fbNet.Elements(ns + "DataConnections").Remove();

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
                    $"[Wire] wrote {emittedEvents.Count} event + {emittedData.Count} data connection(s) " +
                    $"to {Path.GetFileName(sysresPath)}");
            }
            catch (Exception ex)
            {
                report.Missing.Add($"[Wire] failed: {ex.GetType().Name}: {ex.Message}");
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
