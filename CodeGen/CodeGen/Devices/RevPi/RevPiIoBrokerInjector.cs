using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Devices.Core;
using CodeGen.Translation;
using CodeGen.Mapping;

namespace CodeGen.Devices.RevPi
{
    // Injects the Revolution Pi Modbus IO broker + its symlink bridge onto the RevPi sysres (and syslay),
    // exactly the pattern BX1 uses (PLC_RW_BX1 word broker + external SYMLINKMULTIVAR bridge), but with the
    // reference's Modbus broker PLC_RW_REVPI. Our Five_State CAT binds IO via symlinks ($${PATH}athome/
    // atwork/OutputToWork), NOT the direct pins Jyotsna's reference CAT wires — so the bridge connects the
    // broker's InputVars/OutputVars to the Feed actuators' symlink names, and a ScanCycle heartbeat drives
    // the Modbus read/write (the reference's plc_out trigger is a direct-wire pin our CAT lacks).
    //
    // Scope = exactly what the reference broker exposes: Feeder (Pusher) + Checker + PartInHopper (Hopper).
    // Transfer/Ejector/Robot/PartAtAssembly have no Modbus IO in the reference, so none is invented here.
    public static class RevPiIoBrokerInjector
    {
        public const string BrokerFbId   = "A6B61E2425DB1C30";   // matches the .hcf MB_Read/Write LinkNames
        public const string BrokerFbName = "RevPI_IO";
        public const string BrokerFbType = "PLC_RW_REVPI";
        const string ScanFbName = "RevPI_IO_Cycle";
        const string ScanPeriod = "T#80ms";                       // = the reference Modbus buscycletime

        // broker OutputVar (unpacked Modbus input bit) -> the actuator sensor symlink it feeds. Pin = the
        // internal Bitman_1 output the INTERNALIZED publisher taps directly (external path uses Var+Symlink).
        static readonly (string Var, string Symlink, string Pin)[] Sensors =
        {
            ("PusherAtWork", "Feeder.atwork", "OUT2"), ("PusherAtHome", "Feeder.athome", "OUT1"),
            ("checkerUp", "Checker.athome", "OUT4"), ("chekcerDown", "Checker.atwork", "OUT5"),
            ("Hopper", "PartInHopper.Input", "OUT3"),
        };
        // broker InputVar (Modbus output bit) <- the actuator coil symlink. Pin = the internal BITMAN_OUT input
        // the INTERNALIZED subscriber drives directly.
        static readonly (string Var, string Symlink, string Pin)[] Coils =
        {
            ("ExtendPusher", "Feeder.OutputToWork", "IN1"), ("ExtendChecker", "Checker.OutputToWork", "IN2"),
        };

        // The Feed components whose physical IO the RevPi Modbus coupler actually carries, derived from the
        // two tables above so it can never drift from them. This BOUNDS what may be hosted on the RevPi: a
        // component relocated here without a signal in PLC_RW_REVPI's interface would deploy with no
        // physical IO and could never actuate — silently, which is the worst failure mode on a rig.
        public static IReadOnlySet<string> CoveredComponents { get; } =
            new HashSet<string>(
                Sensors.Select(s => s.Symlink)
                       .Concat(Coils.Select(c => c.Symlink))
                       .Select(sym => sym.Split('.')[0]),
                StringComparer.OrdinalIgnoreCase);

        // Inject into the RevPi sysres + syslay. resourceName scopes the absolute symlink names (RevPi_RES).
        public static int Inject(DeploymentProfile profile, string? sysresPath, string? syslayPath, string resourceName,
            SystemInjector.BindingApplicationReport report)
        {
            int touched = 0;
            foreach (var (label, path, isSysres) in new[]
            {
                ("sysres", sysresPath ?? "", true),
                ("syslay", syslayPath ?? "", false),
            })
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                try { if (InjectInto(profile, path, isSysres, resourceName)) touched++; }
                catch (IOException)
                {
                    report.Missing.Add($"[RevPi][Broker] FAILED to write RevPI_IO to the {label} — file " +
                        "LOCKED. Close the RevPi_RES view in EAE (or close EAE) before Test Runtime.");
                }
                catch (Exception ex) { report.Missing.Add($"[RevPi][Broker] {label} injection error: {ex.Message}"); }
            }
            if (touched > 0)
                report.Missing.Add($"[RevPi][Broker] RevPI_IO (PLC_RW_REVPI) + Modbus symlink bridge injected " +
                    "(Feeder/Checker/PartInHopper); scan-driven, resolves the .hcf MB_Read/Write LinkNames.");
            return touched;
        }

        static bool InjectInto(DeploymentProfile profile, string path, bool isSysres, string resourceName)
        {
            var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            var root = doc.Root;
            if (root == null) return false;
            string ns = root.GetDefaultNamespace().NamespaceName;
            XName N(string n) => XName.Get(n, ns);

            // The RevPi network (sysres: root/FBNetwork; syslay: the network holding the Feed FBs).
            // SyslayBuilder emits <SubAppNetwork>, NOT <FBNetwork>, so searching only FBNetwork silently
            // matched nothing and the syslay half of this injector never ran — RevPI_IO existed on the
            // resource with no application instance behind it. Search BOTH, as Bx1IoBrokerInjector does.
            var net = isSysres
                ? root.Element(N("SubAppNetwork")) ?? root.Element(N("FBNetwork"))
                : root.Descendants(N("FBNetwork")).Concat(root.Descendants(N("SubAppNetwork")))
                      .FirstOrDefault(fn =>
                          fn.Elements(N("FB")).Any(f => (string?)f.Attribute("Name") == "Feeder"));
            if (net == null) return false;

            // Idempotent: sweep any prior broker + bridge, then re-add.
            bool IsBrokerFb(string n) => n == BrokerFbName || n == "RevPiSensorPublisher"
                || n == "RevPiCoilSubscriber" || n == ScanFbName;
            static string FbOf(string? ep) => ep == null ? "" : (ep.Contains('.') ? ep[..ep.IndexOf('.')] : ep);
            var ec = net.Element(N("EventConnections")) ?? EnsureSection(net, N("EventConnections"));
            var dc = net.Element(N("DataConnections"))  ?? EnsureSection(net, N("DataConnections"));
            foreach (var fb in net.Elements(N("FB")).Where(f => IsBrokerFb((string?)f.Attribute("Name") ?? "")).ToList())
            {
                // Drop the element's own indentation text node with it. The document is loaded with
                // PreserveWhitespace, so removing only the <FB> leaves an orphan blank line behind and the
                // resource grows a few bytes of whitespace on EVERY re-deploy — sweep-and-re-add would
                // never be byte-idempotent.
                if (fb.PreviousNode is XText ws && string.IsNullOrWhiteSpace(ws.Value)) ws.Remove();
                fb.Remove();
            }
            foreach (var grp in new[] { ec, dc })
                foreach (var c in grp.Elements(N("Connection"))
                             .Where(c => IsBrokerFb(FbOf((string?)c.Attribute("Source"))) ||
                                         IsBrokerFb(FbOf((string?)c.Attribute("Destination")))).ToList())
                    c.Remove();

            var firstInput = net.Elements(N("Input")).FirstOrDefault();
            void Add(XElement fb) { if (firstInput != null) firstInput.AddBeforeSelf(fb); else net.Add(fb); }
            void Ev(string s, string d) => ec.Add(new XElement(N("Connection"),
                new XAttribute("Source", s), new XAttribute("Destination", d)));

            // 1. The broker FB (forced id so the copied Modbus .hcf's LinkNames resolve).
            var brokerFb = new XElement(N("FB"),
                new XAttribute("ID", BrokerFbId), new XAttribute("Name", BrokerFbName),
                new XAttribute("Type", BrokerFbType), new XAttribute("Namespace", "Main"));
            // A resource FB must name the application instance it realises; without Mapping EAE sees an
            // unmapped instance ("Repair Instances"). The syslay FB carries this same ID, so Mapping ==
            // ID — the shape Bx1IoBrokerInjector emits and the deployed BX1_IO already ships.
            if (isSysres) brokerFb.Add(new XAttribute("Mapping", BrokerFbId));
            brokerFb.Add(new XAttribute("x", isSysres ? "12180" : "36000"), new XAttribute("y", "6600"));
            Add(brokerFb);

            // 2. INIT anchor (always): the resource's local boot chain. Full swap: Feed_Station (on RevPi) inits
            //    the broker. Partial swap: Feed_Station is on M262, so anchor off a LOCAL RevPi component in
            //    BOTH this sysres and the shared syslay (PartInHopper) -> no cross-device INIT wire.
            bool Has(string nm) => net.Elements(N("FB")).Any(f => (string?)f.Attribute("Name") == nm);
            string initSrc = !profile.PartialRevPi && Has("Feed_Station")
                ? "Feed_Station"
                : new[] { "PartInHopper", "Feeder", "Checker", "FB1" }.FirstOrDefault(Has) ?? "FB1";
            Ev($"{initSrc}.INITO", $"{BrokerFbName}.INIT");


            doc.Save(path);
            return true;
        }

        // Internalize the Modbus symlink bridge INTO the deployed PLC_RW_REVPI composite so the RevPi sysres
        // instantiates ONLY RevPI_IO (the bridge Publisher/Subscriber/ScanCycle live inside the type). Mirrors
        // BX1's EmbedCoverBridgeInComposite: the publisher taps the composite's internal unpacked sensor bits
        // (Bitman_1.OUTn), the subscriber drives the internal packer (BITMAN_OUT.INn), and an E_DELAY re-reads
        // (DI_Read_Word) + re-writes (BITMAN_OUT) each cycle. Sweep-and-rebuild = self-healing on re-deploy and
        // deterministic from the pristine template. resourceName scopes the absolute symlink NAMEs (RevPi_RES).
        public static void EmbedBridgeInComposite(string fbtPath, string resourceName = "RevPi_RES")
        {
            if (!File.Exists(fbtPath)) return;
            // No PreserveWhitespace: Save re-indents so every FB lands on its own line (EAE requires it).
            var doc = XDocument.Load(fbtPath);
            var net = doc.Root?.Element("FBNetwork");
            var ec = net?.Element("EventConnections");
            var dc = net?.Element("DataConnections");
            if (net == null || ec == null || dc == null) return;

            static string FbOf(string? ep) => ep == null ? "" : (ep.Contains('.') ? ep[..ep.IndexOf('.')] : ep);
            bool IsBridge(string n) => n is "RevPiSensorPublisher" or "RevPiCoilSubscriber" or ScanFbName;
            foreach (var fb in net.Elements("FB").Where(f => IsBridge((string?)f.Attribute("Name") ?? "")).ToList())
                fb.Remove();
            foreach (var grp in new[] { ec, dc })
                foreach (var c in grp.Elements("Connection")
                             .Where(c => IsBridge(FbOf((string?)c.Attribute("Source"))) ||
                                         IsBridge(FbOf((string?)c.Attribute("Destination")))).ToList())
                    c.Remove();

            var idc = doc.Root!.Elements("Attribute")
                .FirstOrDefault(a => (string?)a.Attribute("Name") == "Configuration.FB.IDCounter");
            int nextId = (idc != null && int.TryParse((string?)idc.Attribute("Value"), out var cur))
                ? Math.Max(cur, 40) : 40;
            int uid = 40;
            var firstInput = net.Elements("Input").FirstOrDefault();
            void Add(XElement fb) { if (firstInput != null) firstInput.AddBeforeSelf(fb); else net.Add(fb); }
            void Ev(string s, string d) => ec.Add(new XElement("Connection",
                new XAttribute("Source", s), new XAttribute("Destination", d)));
            void Da(string s, string d) => dc.Add(new XElement("Connection",
                new XAttribute("Source", s), new XAttribute("Destination", d)));

            // Publisher (SRC): internal Bitman_1 sensor bits -> the Feed actuator sensor symlinks.
            var (sArity, sType) = SymlinkBridge.Pick("SRC", Sensors.Length);
            Add(SymlinkBridge.BuildFb("", nextId++, uid++, "RevPiSensorPublisher", sType, sArity,
                Sensors.Select(s => $"{resourceName}.{s.Symlink}").ToList(), 400, 4400));
            // Subscriber (DST): the Feed actuator coil symlinks -> internal BITMAN_OUT.
            var (cArity, cType) = SymlinkBridge.Pick("DST", Coils.Length);
            Add(SymlinkBridge.BuildFb("", nextId++, uid++, "RevPiCoilSubscriber", cType, cArity,
                Coils.Select(c => $"{resourceName}.{c.Symlink}").ToList(), 3000, 4400));
            // ScanCycle heartbeat: our CAT publishes coils via a symlink with no boundary event, so the read/
            // write must be REQ'd each cycle or the Modbus words freeze.
            Add(new XElement("FB",
                new XAttribute("ID", nextId++), new XAttribute("Name", ScanFbName),
                new XAttribute("Type", "E_DELAY"), new XAttribute("x", "400"), new XAttribute("y", "5200"),
                new XAttribute("Namespace", "IEC61499.Standard"),
                new XElement("Parameter", new XAttribute("Name", "DT"), new XAttribute("Value", ScanPeriod))));

            Ev("INIT", "RevPiSensorPublisher.INIT");
            Ev("INIT", "RevPiCoilSubscriber.INIT");
            Ev("INIT", $"{ScanFbName}.START");
            Ev($"{ScanFbName}.EO", $"{ScanFbName}.START");
            Ev($"{ScanFbName}.EO", "DI_Read_Word.REQ");          // re-read the Modbus input word each cycle
            Ev($"{ScanFbName}.EO", "RevPiCoilSubscriber.REQ");   // read the coil symlinks
            Ev("Bitman_1.CNF", "RevPiSensorPublisher.REQ");      // publish the sensor bits after the word unpacks
            Ev("RevPiCoilSubscriber.CNF", "BITMAN_OUT.REQ");     // pack + write the coils after they're read

            for (int i = 0; i < Sensors.Length; i++)
                Da($"Bitman_1.{Sensors[i].Pin}", $"RevPiSensorPublisher.VALUE{i + 1}");
            for (int i = 0; i < Coils.Length; i++)
                Da($"RevPiCoilSubscriber.VALUE{i + 1}", $"BITMAN_OUT.{Coils[i].Pin}");

            // Remove the boundary InputVar -> BITMAN_OUT connections (two data sources per pin is an EAE error).
            var inputVarSources = Coils.Select(c => c.Var).ToHashSet();
            foreach (var c in dc.Elements("Connection").Where(c =>
                         ((string?)c.Attribute("Destination"))?.StartsWith("BITMAN_OUT.IN") == true &&
                         inputVarSources.Contains((string?)c.Attribute("Source") ?? "")).ToList())
                c.Remove();

            idc?.SetAttributeValue("Value", nextId.ToString());
            doc.Save(fbtPath);
        }

        static XElement EnsureSection(XElement net, XName name)
        {
            var el = new XElement(name);
            net.Add(el);
            return el;
        }

    }
}
