using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using CodeGen.Devices.Core;
using CodeGen.Translation;

namespace CodeGen.Devices.RevPi
{
    // The Revolution Pi's field I/O: the Modbus coupler and the symlink bridge to the actuator CATs.
    // No component is named here or in YAML. The coupler type states which signals exist and their word
    // bits, the workbook their owner, the hardware config the broker id and bus cycle.
    public static class RevPiIoBrokerInjector
    {
        // SyslaySysresParityValidator looks for this exact instance name on the RevPi resource.
        internal const string BrokerName = "RevPI_IO";
        internal const string BrokerType = "PLC_RW_REVPI";

        const string BrokerTypeZip = @"Composite\PLC_RW_REVPI.zip";
        const string HcfTemplate = @"RevPi\RevPiIO.modbus.hcf";
        // Bridge instances, swept by these names so a re-deploy converges instead of accumulating.
        const string Publisher = "RevPiSensorPublisher";
        const string Subscriber = "RevPiCoilSubscriber";
        const string ScanFb = "RevPI_IO_Cycle";
        static readonly string[] Bridge = { Publisher, Subscriber, ScanFb };

        // Coordinates inside the coupler's own function block type, not plant layout.
        const int BridgeX = 400, BridgeY = 4400, BridgeColumn = 2600, BridgeRow = 800;
        // The workbook gives a sensor a tag but no channel, so a sensor is matched by tag and takes this port.
        const string SensorPort = "Input";

        internal sealed record Signal(bool IsCoil, string Pin, string Component, string Port)
        {
            public string Symlink(string resource) => $"{resource}.{Component}.{Port}";
        }

        internal sealed record Coupler(IReadOnlyList<Signal> Signals, string Unpacker, string Packer,
            string ModbusRead, string ScanPeriod, string BrokerFbId, string ResourceId)
        {
            public IEnumerable<Signal> Sensors => Signals.Where(s => !s.IsCoil);
            public IEnumerable<Signal> Coils => Signals.Where(s => s.IsCoil);
            public IReadOnlySet<string> Components =>
                new HashSet<string>(Signals.Select(s => s.Component), StringComparer.OrdinalIgnoreCase);
        }

        // Propagates a resolution failure; answering "nothing" would silently reject every RevPi selection.
        public static IReadOnlySet<string> CoveredComponents => Current.Components;

        // Read from the hardware config, whose Modbus LinkNames resolve against it.
        public static string BrokerFbId => Current.BrokerFbId;

        // Called by the deployer once the pristine coupler type is in place, so the resource holds only the broker.
        public static void EmbedBridgeInComposite(string fbtPath) =>
            EmbedBridge(Current, ControllerMap.ResourceForPlc(PlcAssignment.RevPi), fbtPath);

        static Coupler? _current;
        internal static Coupler Current => _current ??= Resolve(MapperConfig.Load().TemplateLibraryPath);

        internal static Coupler Resolve(string? templateLibraryPath)
        {
            var lib = Locate(templateLibraryPath, BrokerTypeZip)
                ?? throw Fail($"the coupler type '{BrokerTypeZip}' is not in the template library");
            var hcf = Locate(templateLibraryPath, HcfTemplate)
                ?? throw Fail($"the hardware config '{HcfTemplate}' is not in the template library");
            var bindings = IoBindingsLoader.LoadBindings(ResolveBindings(MapperConfig.Load().IoBindingsPath));

            // The PRISTINE type is the specification. The deployed copy has already been rewritten here,
            // so reading it back would find a coupler with no coils.
            using var zip = ZipFile.OpenRead(lib);
            var entry = zip.Entries.First(e => e.FullName.EndsWith($"{BrokerType}.fbt", StringComparison.OrdinalIgnoreCase));
            using var stream = entry.Open();
            var type = XDocument.Load(stream);

            var iface = type.Root?.Element("InterfaceList");
            var net = type.Root?.Element("FBNetwork");
            if (iface == null || net == null) throw Fail($"'{BrokerType}' declares no interface or FB network");

            HashSet<string> Names(string g) => new((iface.Element(g)?.Elements() ?? Enumerable.Empty<XElement>())
                .Select(v => (string?)v.Attribute("Name") ?? ""), StringComparer.Ordinal);
            var outs = Names("OutputVars");
            var ins = Names("InputVars");
            var edges = (net.Element("DataConnections")?.Elements("Connection") ?? Enumerable.Empty<XElement>())
                .Select(c => (S: (string?)c.Attribute("Source") ?? "", D: (string?)c.Attribute("Destination") ?? ""))
                .ToList();

            // A sensor is an internal pin feeding a boundary output, a coil the reverse; direction is the whole definition.
            var sensors = edges.Where(e => outs.Contains(e.D) && e.S.Contains('.')).ToList();
            var coils = edges.Where(e => ins.Contains(e.S) && e.D.Contains('.')).ToList();

            var signals = sensors.Select(e => Bind(false, e.S, e.D, bindings))
                .Concat(coils.Select(e => Bind(true, e.D, e.S, bindings)))
                .OrderBy(s => s.IsCoil).ThenBy(s => s.Pin, StringComparer.Ordinal).ToList();

            var read = net.Elements("FB").FirstOrDefault(f =>
                ((string?)f.Attribute("Type") ?? "").StartsWith("SYMLINKMULTIVARDST", StringComparison.Ordinal))
                ?? throw Fail($"'{BrokerType}' declares no Modbus read symlink, so the word can never refresh");

            var h = XDocument.Load(hcf);
            var resId = h.Descendants().Select(e => (string?)e.Attribute("ResourceId"))
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
                ?? throw Fail("the RevPi hardware config declares no ResourceId");
            // LinkName is '<resource>.<fb>.<port>', so the file the broker must satisfy names its id.
            var fbIds = h.Descendants("ParameterValue")
                .Where(p => (string?)p.Attribute("Name") == "LinkName")
                .Select(p => ((string?)p.Attribute("Value") ?? "").Split('.'))
                .Where(a => a.Length >= 3 && a[0] == resId).Select(a => a[1])
                .Distinct(StringComparer.Ordinal).ToList();
            if (fbIds.Count != 1)
                throw Fail($"the RevPi hardware config links to {fbIds.Count} function blocks; exactly one broker must satisfy it");
            var cycle = h.Descendants("ItemProperty")
                .Where(p => string.Equals((string?)p.Element("Name"), "buscycletime", StringComparison.OrdinalIgnoreCase))
                .Select(p => (string?)p.Element("Value")).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
                ?? throw Fail("the RevPi hardware config declares no bus cycle time");

            return new Coupler(signals, Node(sensors[0].S), Node(coils[0].D),
                (string)read.Attribute("Name")!, cycle, fbIds[0], resId);
        }

        // Two routes into the workbook, by channel and by field tag; neither covers the coupler alone, and a
        // disagreement between them is a real wiring fault.
        static Signal Bind(bool isCoil, string pin, string var, IoBindings b)
        {
            var channel = Channel(isCoil, pin);
            var byChannel = b.PinAssignments.TryGetValue(channel, out var pa) ? (pa.ComponentName, pa.Port) : default;
            var byTag = Tag(var, b);
            if (byChannel != default && byTag != default && !byChannel.Equals(byTag))
                throw Fail($"'{var}' owns {byChannel} by channel {channel} but {byTag} by field tag — " +
                           "the coupler and the I/O bindings disagree about the wiring");
            var owner = byChannel != default ? byChannel : byTag;
            if (owner == default)
                throw Fail($"'{var}' (channel {channel}) is in neither the workbook's pin columns nor its tags, " +
                           "so nothing owns it");
            return new Signal(isCoil, pin, owner.Item1, owner.Item2);
        }

        static (string, string) Tag(string tag, IoBindings b)
        {
            bool Is(string? c) => !string.IsNullOrWhiteSpace(c) && string.Equals(tag, c, StringComparison.OrdinalIgnoreCase);
            foreach (var a in b.Actuators.Values)
            {
                if (Is(a.AthomeTag)) return (a.ComponentName, "athome");
                if (Is(a.AtworkTag)) return (a.ComponentName, "atwork");
                if (Is(a.OutputToWorkTag)) return (a.ComponentName, "OutputToWork");
                if (Is(a.OutputToHomeTag)) return (a.ComponentName, "OutputToHome");
            }
            foreach (var s in b.Sensors.Values) if (Is(s.InputTag)) return (s.ComponentName, SensorPort);
            return default;
        }

        // Word-block pins count from one, channels from zero, so pin n is channel n-1.
        static string Channel(bool isCoil, string pin)
        {
            var digits = new string(pin[(pin.IndexOf('.') + 1)..].SkipWhile(c => !char.IsDigit(c)).ToArray());
            if (!int.TryParse(digits, out var n)) throw Fail($"pin '{pin}' carries no bit number");
            return $"{(isCoil ? "DO" : "DI")}{n - 1:D2}";
        }

        internal static string Node(string endpoint) =>
            endpoint.Contains('.') ? endpoint[..endpoint.IndexOf('.')] : endpoint;

        static InvalidOperationException Fail(string why) =>
            new($"The Revolution Pi I/O cannot be resolved: {why}. Nothing has been written.");

        // Configured root first, then beside this build, then out towards the checkout.
        static string? Locate(string? root, string relative)
        {
            var roots = new List<string?> { root };
            foreach (var origin in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
                for (var d = new DirectoryInfo(origin); d != null; d = d.Parent)
                    roots.Add(Path.Combine(d.FullName, "Template Library"));
            return roots.Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => Path.Combine(r!, relative)).FirstOrDefault(File.Exists);
        }

        static string ResolveBindings(string configured) =>
            Path.IsPathRooted(configured) ? configured
            : new[] { AppContext.BaseDirectory, Environment.CurrentDirectory }
                  .Select(o => Path.Combine(o, configured)).FirstOrDefault(File.Exists)
              ?? Path.Combine(AppContext.BaseDirectory, configured);

        // Only the resource copy carries the Mapping; an unmapped resource FB is the orphan EAE offers to repair.
        internal static bool PlaceBroker(Coupler coupler, string path, bool isResource,
            IReadOnlyList<string> hosted, LayoutCatalog layout, string bootFb)
        {
            if (!File.Exists(path)) return false;
            var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            var root = doc.Root;
            if (root == null) return false;
            string ns = root.GetDefaultNamespace().NamespaceName;
            XName N(string n) => XName.Get(n, ns);

            var net = isResource
                ? root.Element(N("SubAppNetwork")) ?? root.Element(N("FBNetwork"))
                : root.Descendants(N("FBNetwork")).Concat(root.Descendants(N("SubAppNetwork")))
                    .FirstOrDefault(n => n.Elements(N("FB"))
                        .Any(f => hosted.Contains((string?)f.Attribute("Name") ?? "")));
            if (net == null) return false;

            var owned = new HashSet<string>(Bridge, StringComparer.Ordinal) { BrokerName };
            var events = Section(net, N("EventConnections"));
            Sweep(net, N, owned, events, Section(net, N("DataConnections")));

            var band = layout.Band(PlcAssignment.RevPi);
            var fb = new XElement(N("FB"),
                new XAttribute("ID", coupler.BrokerFbId), new XAttribute("Name", BrokerName),
                new XAttribute("Type", BrokerType), new XAttribute("Namespace", "Main"));
            if (isResource) fb.Add(new XAttribute("Mapping", coupler.BrokerFbId));
            fb.Add(new XAttribute("x", (band.ColumnBaseX + layout.Geometry.ColumnPitchX * hosted.Count).ToString()),
                   new XAttribute("y", layout.RowY("Actuator").ToString()));

            // EAE requires FB declarations before the boundary Input elements.
            var firstInput = net.Elements(N("Input")).FirstOrDefault();
            if (firstInput != null) firstInput.AddBeforeSelf(fb); else net.Add(fb);

            // The broker must be initialised from something already on this resource, else it never runs.
            var anchor = hosted.FirstOrDefault(h => net.Elements(N("FB"))
                .Any(f => (string?)f.Attribute("Name") == h)) ?? bootFb;
            events.Add(new XElement(N("Connection"), new XAttribute("Source", $"{anchor}.INITO"),
                new XAttribute("Destination", $"{BrokerName}.INIT")));

            doc.Save(path);
            return true;
        }

        internal static void EmbedBridge(Coupler c, string resourceName, string fbtPath)
        {
            if (!File.Exists(fbtPath)) return;
            // No PreserveWhitespace: saving re-indents so each FB lands on its own line, as EAE requires here.
            var doc = XDocument.Load(fbtPath);
            var net = doc.Root?.Element("FBNetwork");
            var ec = net?.Element("EventConnections");
            var dc = net?.Element("DataConnections");
            if (net == null || ec == null || dc == null) return;

            var owned = new HashSet<string>(Bridge, StringComparer.Ordinal);
            Sweep(net, n => n, owned, ec, dc);

            // Measure ids AFTER the sweep: the stored counter advances every render, so reusing it never settles.
            int id = net.Elements("FB").Select(f => int.TryParse((string?)f.Attribute("ID"), out var v) ? v : 0)
                .DefaultIfEmpty(0).Max() + 1;
            int uid = id;
            var firstInput = net.Elements("Input").FirstOrDefault();
            void Add(XElement fb) { if (firstInput != null) firstInput.AddBeforeSelf(fb); else net.Add(fb); }
            void Ev(string s, string d) => ec.Add(new XElement("Connection",
                new XAttribute("Source", s), new XAttribute("Destination", d)));
            void Da(string s, string d) => dc.Add(new XElement("Connection",
                new XAttribute("Source", s), new XAttribute("Destination", d)));

            var sensors = c.Sensors.ToList();
            var coils = c.Coils.ToList();
            var (sa, st) = SymlinkBridge.Pick("SRC", sensors.Count);
            Add(SymlinkBridge.BuildFb("", id++, uid++, Publisher, st, sa,
                sensors.Select(s => s.Symlink(resourceName)).ToList(), BridgeX, BridgeY));
            var (ca, ct) = SymlinkBridge.Pick("DST", coils.Count);
            Add(SymlinkBridge.BuildFb("", id++, uid++, Subscriber, ct, ca,
                coils.Select(s => s.Symlink(resourceName)).ToList(), BridgeX + BridgeColumn, BridgeY));
            // A coil symlink carries no boundary event, so read and write must be re-requested on the
            // coupler's own bus cycle or the words freeze.
            Add(new XElement("FB", new XAttribute("ID", id++), new XAttribute("Name", ScanFb),
                new XAttribute("Type", "E_DELAY"),
                new XAttribute("x", BridgeX.ToString()), new XAttribute("y", (BridgeY + BridgeRow).ToString()),
                new XAttribute("Namespace", "IEC61499.Standard"),
                new XElement("Parameter", new XAttribute("Name", "DT"), new XAttribute("Value", c.ScanPeriod))));

            Ev("INIT", $"{Publisher}.INIT");
            Ev("INIT", $"{Subscriber}.INIT");
            Ev("INIT", $"{ScanFb}.START");
            Ev($"{ScanFb}.EO", $"{ScanFb}.START");
            Ev($"{ScanFb}.EO", $"{c.ModbusRead}.REQ");   // re-read the input word each cycle
            Ev($"{ScanFb}.EO", $"{Subscriber}.REQ");     // read the coil symlinks
            Ev($"{c.Unpacker}.CNF", $"{Publisher}.REQ"); // publish once the word has unpacked
            Ev($"{Subscriber}.CNF", $"{c.Packer}.REQ");  // pack and write once the coils are read

            // VALUE pins are positional, so the coupler's signal order binds a pin to a symlink.
            for (int i = 0; i < sensors.Count; i++) Da(sensors[i].Pin, $"{Publisher}.VALUE{i + 1}");
            for (int i = 0; i < coils.Count; i++) Da($"{Subscriber}.VALUE{i + 1}", coils[i].Pin);

            // The subscriber supersedes the pristine wire into each coil pin; two sources on one
            // destination is an EAE error.
            foreach (var conn in dc.Elements("Connection")
                         .Where(x => coils.Any(k => (string?)x.Attribute("Destination") == k.Pin) &&
                                     Node((string?)x.Attribute("Source") ?? "") != Subscriber).ToList())
                conn.Remove();

            doc.Root!.Elements("Attribute")
                .FirstOrDefault(a => (string?)a.Attribute("Name") == "Configuration.FB.IDCounter")
                ?.SetAttributeValue("Value", id.ToString());
            doc.Save(fbtPath);
        }

        static XElement Section(XElement net, XName name)
        {
            var el = net.Element(name);
            if (el != null) return el;
            el = new XElement(name);
            net.Add(el);
            return el;
        }

        static void Sweep(XElement net, Func<string, XName> N, HashSet<string> owned, params XElement[] groups)
        {
            foreach (var fb in net.Elements(N("FB"))
                         .Where(f => owned.Contains((string?)f.Attribute("Name") ?? "")).ToList())
            {
                // Take the element's indentation with it, else the document grows a blank line per re-deploy
                // and sweep-and-re-add is never byte-idempotent.
                if (fb.PreviousNode is XText ws && string.IsNullOrWhiteSpace(ws.Value)) ws.Remove();
                fb.Remove();
            }
            foreach (var g in groups)
                foreach (var c in g.Elements(N("Connection"))
                             .Where(c => owned.Contains(Node((string?)c.Attribute("Source") ?? "")) ||
                                         owned.Contains(Node((string?)c.Attribute("Destination") ?? ""))).ToList())
                    c.Remove();
        }
    }
}
