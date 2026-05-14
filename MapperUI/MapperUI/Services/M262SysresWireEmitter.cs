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
        // Full event chain per Alex's SMC_Rig_Expo sysres adapted to
        // Mapper's Process1_Generic + adapter-ring design. START is the
        // resource's built-in restart FB (EMB_RES_ECO canvas auto-instance).
        // FB1=DPAC_FULLINIT, FB2=plcStart, Feed_Station=Process1_Generic.
        private static readonly Wire[] EventWires =
        {
            new("START.COLD",          "FB1.INIT"),
            new("START.WARM",          "FB1.INIT"),
            new("START.ONLINECHANGE",  "FB1.OC_RETRIGGER"),
            new("FB2.FIRST_INIT",      "FB2.ACK_FIRST"),
            new("FB1.INITO",           "M262IO.INIT"),
            new("FB1.INITO",           "Area.INIT"),
            new("FB1.INITO",           "Feed_Station.INIT"),
            new("Area.INITO",          "Station1.INIT"),
            // Sequential init through the CaSBus order: Process inits its
            // actuators, each actuator inits its sensor. Replaces the prior
            // FB1.INITO → Feeder/PartInHopper parallel fan-out which broke
            // the OSDA component-init handshake.
            new("Feed_Station.INITO",  "Feeder.INIT"),
            new("Feeder.INITO",        "PartInHopper.INIT"),
            // Runtime I/O event ring. Without these the .hcf TM3 binding
            // is wired but never fires: M262IO's changeEventProcess1 emits
            // PusherEvent on a pin transition → Feeder.action_event runs
            // its state machine → Feeder.plc_out signals back to M262IO's
            // REQ_INT_BOOL so the runtime pushes the new BOOL out to DQ.
            new("M262IO.PusherEvent",  "Feeder.action_event"),
            new("Feeder.plc_out",      "M262IO.REQ_INT_BOOL"),
            // Stage-1 auto-extend: ConstantPusherWriter publishes
            // 'M262IO.ExtendPusher' = TRUE once at boot via FB1.INITO so
            // the cylinder extends without the recipe driving Feeder. INIT
            // must precede REQ — declaration order is preserved by EAE's
            // event scheduler so this two-wire fan-out is safe.
            new("FB1.INITO",           "ConstantPusherWriter.INIT"),
            new("FB1.INITO",           "ConstantPusherWriter.REQ"),
        };

        // Adapter ring: HMI → Area/Station chain → Feed_Station → Feeder
        // → PartInHopper → Stn1_Term, plus the stateRprtCmd report ring
        // closing back to Process1.
        private static readonly Wire[] AdapterWires =
        {
            new("Area_HMI.AreaHMIAdptrOUT",        "Area.AreaHMIAdptrIN"),
            new("Station1_HMI.StationHMIAdptrOUT", "Station1.StationHMIAdptrIN"),
            new("Area.AreaAdptrOUT",               "Station1.AreaAdptrIN"),
            new("Station1.StationAdaptrOUT",       "Feed_Station.stationAdptr_in"),
            new("Feed_Station.stationAdptr_out",   "Feeder.stationAdptr_in"),
            new("Feeder.stationAdptr_out",         "PartInHopper.stationAdptr_in"),
            new("PartInHopper.stationAdptr_out",   "Stn1_Term.CaSAdptrIN"),
            new("Feed_Station.stateRprtCmd_out",   "Feeder.stateRprtCmd_in"),
            new("Feeder.stateRprtCmd_out",         "PartInHopper.stateRprtCmd_in"),
            new("PartInHopper.stateRprtCmd_out",   "Feed_Station.stateRprtCmd_in"),
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

                // Stage-1: discover the publisher symbol used by PLC_RW_M262's
                // internal output FB, then inject a peer SYMLINKMULTIVARSRC at
                // sysres top level that publishes 'M262IO.ExtendPusher' = TRUE.
                // Wires for it are declared in EventWires above; this just
                // makes sure the FB element exists before validation runs.
                LogPublisherSymbolDiscovery(cfg, report);
                EnsureConstantPusherWriter(fbNet, ns, report);

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

                // Apply the canonical sysres canvas layout: hardware top
                // strip (y=200), HMI row (y=800), structural row (y=1400),
                // components row (y=2200). Reads top-down, runtime → init
                // → application → components, mirroring the syslay layout.
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

                foreach (var w in EventWires)   Process(w, emittedEvents,   "event");
                foreach (var w in DataWires)    Process(w, emittedData,     "data");
                foreach (var w in AdapterWires) Process(w, emittedAdapters, "adapter");

                // Optional fast path: M262IO.HopperEvent / PusherEvent →
                // Feed_Station.state_change duplicate the stateRprtCmd ring
                // notifications. Mirrors Alex's working pattern but is
                // valid only if Process1_Generic exposes state_change as a
                // top-level EventInput. Per OSDA spec it currently doesn't
                // (only INIT/INITO are top-level), so we check first and
                // skip both wires with the explicit explanatory log when
                // the port is absent — runtime state still flows through
                // the stateRprtCmd ring.
                var processPorts = PortsFor("Process1_Generic");
                if (processPorts.Count > 0 && !processPorts.Contains("state_change"))
                {
                    report.Missing.Add(
                        "[Sysres] Process1_Generic has no state_change event input, " +
                        "skipping M262IO -> Process1 direct wires, state flows through stateRprtCmd ring only");
                }
                else
                {
                    Process(new Wire("M262IO.HopperEvent", "Feed_Station.state_change"), emittedEvents, "event");
                    Process(new Wire("M262IO.PusherEvent", "Feed_Station.state_change"), emittedEvents, "event");
                }

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

                // Stage-1 expected runtime behaviour. These four lines tell
                // the operator what to look for at the controller end before
                // suspecting a wiring bug.
                report.Missing.Add(
                    "[Stage1] ConstantPusherWriter emits publish of Feeder.OutputToWork = TRUE on boot");
                report.Missing.Add(
                    "[Stage1] DQ00 subscribes to this symbol per .hcf binding");
                report.Missing.Add(
                    "[Stage1] Expected: cylinder extends and stays extended after Deploy + Start");
                report.Missing.Add(
                    "[Stage1] If cylinder does not extend, check DQ00 LED on TM3 module");
            }
            catch (Exception ex)
            {
                report.Missing.Add($"[Wire] failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Stage-1 discovery: read the project's PLC_RW_M262.fbt and surface
        /// every NAME parameter on its internal SYMLINKMULTIVARSRC (the FB
        /// named "output"). Pattern is <c>'$${PATH}ExtendPusher'</c> etc.,
        /// where <c>$${PATH}</c> resolves to the sysres instance name
        /// (<c>M262IO</c>) — so the actual published symbol at runtime is
        /// <c>M262IO.ExtendPusher</c>. Best-effort; silently skips logging
        /// if the .fbt is missing or malformed.
        /// </summary>
        private static void LogPublisherSymbolDiscovery(MapperConfig cfg,
            SystemInjector.BindingApplicationReport report)
        {
            try
            {
                var libRoot = cfg.TemplateLibraryPath;
                if (string.IsNullOrWhiteSpace(libRoot))
                {
                    report.Missing.Add("[Stage1] TemplateLibraryPath empty, skipping PLC_RW_M262.fbt inspection");
                    return;
                }
                var fbtPath = Path.Combine(
                    libRoot, "CAT", "PLC_RW_M262", "IEC61499", "PLC_RW_M262.fbt");
                if (!File.Exists(fbtPath))
                {
                    report.Missing.Add($"[Stage1] PLC_RW_M262.fbt not found at {fbtPath}");
                    return;
                }
                var doc = XDocument.Load(fbtPath);
                var outputFb = doc.Descendants().FirstOrDefault(e =>
                    e.Name.LocalName == "FB" &&
                    string.Equals((string?)e.Attribute("Name"), "output", StringComparison.Ordinal));
                if (outputFb == null)
                {
                    report.Missing.Add(
                        "[Stage1] PLC_RW_M262.fbt has no internal FB named 'output' — " +
                        "ABORT: unrecognised NAME pattern, need manual inspection");
                    return;
                }
                var outType = (string?)outputFb.Attribute("Type") ?? string.Empty;
                var outNs   = (string?)outputFb.Attribute("Namespace") ?? string.Empty;

                report.Missing.Add("[Stage1] PLC_RW_M262 internal Output FB NAMEs:");
                foreach (var p in outputFb.Elements())
                {
                    if (p.Name.LocalName != "Parameter") continue;
                    var pn = (string?)p.Attribute("Name") ?? string.Empty;
                    if (!pn.StartsWith("NAME", StringComparison.Ordinal)) continue;
                    var pv = (string?)p.Attribute("Value") ?? string.Empty;
                    report.Missing.Add($"[Stage1]   {pn} = {pv}");
                }
                report.Missing.Add($"[Stage1]   Type = {outType}");
                report.Missing.Add($"[Stage1]   Namespace = {outNs}");
                report.Missing.Add(
                    "[Stage1] $${PATH}ExtendPusher resolves to 'M262IO.ExtendPusher' " +
                    "(sysres instance of PLC_RW_M262 is named M262IO) — but DQ00 in the " +
                    ".hcf does NOT subscribe to that symbol; it subscribes to " +
                    "'Feeder.OutputToWork' (the symbol Feeder's internal SYMLINKMULTIVARSRC " +
                    "publishes via $${PATH}OutputToWork)");
            }
            catch (Exception ex)
            {
                report.Missing.Add(
                    $"[Stage1] failed to inspect PLC_RW_M262.fbt: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Idempotently inject a <c>ConstantPusherWriter</c> FB into the
        /// sysres FBNetwork. Type matches the generic SYMLINKMULTIVARSRC
        /// variant used inside PLC_RW_M262 but configured for a single
        /// VALUE1 channel. NAME1 is the literal symbol
        /// <c>'Feeder.OutputToWork'</c> — that's the symbol the .hcf
        /// binding for DQ00 subscribes to (verified in EAE's Symbolic
        /// Links view: Feeder.OutputToWork shows Connected to
        /// M262_RES.TM3DQ16T_G.DO00, while PLC_RW_M262's outward
        /// M262IO.ExtendPusher has no subscriber). VALUE1 = TRUE means
        /// the publish drives DQ00 high once at boot.
        /// </summary>
        private static void EnsureConstantPusherWriter(XElement fbNet, XNamespace ns,
            SystemInjector.BindingApplicationReport report)
        {
            const string FbName     = "ConstantPusherWriter";
            const string FbType     = "SYMLINKMULTIVARSRC_19628BFC3C74F1AB1";
            const string FbId       = "F1A5E5C0AB1D5701"; // deterministic 16-char hex
            const string FbNs       = "Main";
            const string IfaceParams = "Runtime.System#I:=1;VALUE${I}:BOOL";
            const string Symbol     = "'Feeder.OutputToWork'";

            var existing = fbNet.Elements(ns + "FB")
                .FirstOrDefault(e => string.Equals(
                    (string?)e.Attribute("Name"), FbName, StringComparison.Ordinal));
            if (existing != null)
            {
                report.Missing.Add(
                    $"[Stage1] {FbName} already on sysres, leaving FB element as-is");
                return;
            }

            var fb = new XElement(ns + "FB",
                new XAttribute("ID",        FbId),
                new XAttribute("Name",      FbName),
                new XAttribute("Type",      FbType),
                new XAttribute("Namespace", FbNs),
                new XAttribute("x",         "8000"),
                new XAttribute("y",         "400"));
            fb.Add(new XElement(ns + "Attribute",
                new XAttribute("Name",  "Configuration.GenericFBType.InterfaceParams"),
                new XAttribute("Value", IfaceParams)));
            fb.Add(new XElement(ns + "Parameter",
                new XAttribute("Name", "QI"),    new XAttribute("Value", "TRUE")));
            fb.Add(new XElement(ns + "Parameter",
                new XAttribute("Name", "NAME1"), new XAttribute("Value", Symbol)));
            fb.Add(new XElement(ns + "Parameter",
                new XAttribute("Name", "VALUE1"), new XAttribute("Value", "TRUE")));
            fbNet.Add(fb);

            report.Missing.Add(
                $"[Stage1] injected {FbName} (Type={FbType}, Namespace={FbNs}) " +
                $"with NAME1={Symbol}, VALUE1=TRUE");
        }

        // Canonical canvas layout — applied verbatim to every FB on both the
        // sysres and the syslay so the two mirror visually. START is the
        // resource's built-in restart FB (auto-instantiated by EAE, NOT
        // emitted here). Every other entry overrides whatever x/y the
        // upstream mirror code generated; previous coordinates are NOT
        // preserved.
        // Coordinates scaled to EAE's actual FB-body width (~1500 units) so
        // adjacent FBs don't overlap. Pitch is ~2000 units horizontal,
        // ~800-1000 units vertical. Matches the proportions of Alex's
        // baseline SMC_Rig_Expo sysres canvas (FBs at x=3760/9760, etc.).
        private static readonly Dictionary<string, (int X, int Y)> CanonicalLayout = new(StringComparer.Ordinal)
        {
            // Runtime row (y=400)
            { "FB2",          (800,  400) },   // plcStart
            { "FB1",          (3000, 400) },   // DPAC_FULLINIT
            { "M262IO",       (5800, 400) },
            // Stage-1 auto-extend writer, parked right of M262IO
            { "ConstantPusherWriter", (8000, 400) },
            // HMI row (y=1600)
            { "Area_HMI",     (3000, 1600) },
            { "Station1_HMI", (5800, 1600) },
            // Control row (y=2700)
            { "Area",         (3000, 2700) },
            { "Station1",     (5800, 2700) },
            { "Area_Term",    (8800, 2700) },
            // Process + sensor row (y=4000)
            { "Feed_Station", (7000, 4000) },
            { "PartInHopper", (4500, 4000) },
            // Component + terminator row (y=5400)
            { "Feeder",       (3800, 5400) },
            { "Stn1_Term",    (10000, 5400) },
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
