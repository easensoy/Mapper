using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Devices.Core;
using CodeGen.Translation;

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

        // broker OutputVar (unpacked Modbus input bit) -> the actuator sensor symlink it feeds.
        static readonly (string Var, string Symlink)[] Sensors =
        {
            ("PusherAtWork", "Feeder.atwork"), ("PusherAtHome", "Feeder.athome"),
            ("checkerUp", "Checker.athome"), ("chekcerDown", "Checker.atwork"),
            ("Hopper", "PartInHopper.Input"),
        };
        // broker InputVar (Modbus output bit) <- the actuator coil symlink that drives it.
        static readonly (string Var, string Symlink)[] Coils =
        {
            ("ExtendPusher", "Feeder.OutputToWork"), ("ExtendChecker", "Checker.OutputToWork"),
        };

        // Inject into the RevPi sysres + syslay. resourceName scopes the absolute symlink names (RevPi_RES).
        public static int Inject(string? sysresPath, string? syslayPath, string resourceName,
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
                try { if (InjectInto(path, isSysres, resourceName)) touched++; }
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

        static bool InjectInto(string path, bool isSysres, string resourceName)
        {
            var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            var root = doc.Root;
            if (root == null) return false;
            string ns = root.GetDefaultNamespace().NamespaceName;
            XName N(string n) => XName.Get(n, ns);

            // The RevPi FBNetwork (sysres: root/FBNetwork; syslay: the RevPi SubApp holding the Feed FBs).
            var net = isSysres
                ? root.Element(N("FBNetwork"))
                : root.Descendants(N("FBNetwork")).FirstOrDefault(fn =>
                    fn.Elements(N("FB")).Any(f => (string?)f.Attribute("Name") == "Feeder"));
            if (net == null) return false;

            // Idempotent: sweep any prior broker + bridge, then re-add.
            bool IsBrokerFb(string n) => n == BrokerFbName || n == "RevPiSensorPublisher"
                || n == "RevPiCoilSubscriber" || n == ScanFbName;
            static string FbOf(string? ep) => ep == null ? "" : (ep.Contains('.') ? ep[..ep.IndexOf('.')] : ep);
            var ec = net.Element(N("EventConnections")) ?? EnsureSection(net, N("EventConnections"));
            var dc = net.Element(N("DataConnections"))  ?? EnsureSection(net, N("DataConnections"));
            foreach (var fb in net.Elements(N("FB")).Where(f => IsBrokerFb((string?)f.Attribute("Name") ?? "")).ToList())
                fb.Remove();
            foreach (var grp in new[] { ec, dc })
                foreach (var c in grp.Elements(N("Connection"))
                             .Where(c => IsBrokerFb(FbOf((string?)c.Attribute("Source"))) ||
                                         IsBrokerFb(FbOf((string?)c.Attribute("Destination")))).ToList())
                    c.Remove();

            int nextId = NextFbId(root, N);
            int uid = 40;
            var firstInput = net.Elements(N("Input")).FirstOrDefault();
            void Add(XElement fb) { if (firstInput != null) firstInput.AddBeforeSelf(fb); else net.Add(fb); }
            void Ev(string s, string d) => ec.Add(new XElement(N("Connection"),
                new XAttribute("Source", s), new XAttribute("Destination", d)));
            void Da(string s, string d) => dc.Add(new XElement(N("Connection"),
                new XAttribute("Source", s), new XAttribute("Destination", d)));

            // 1. The broker FB (forced id so the copied Modbus .hcf's LinkNames resolve).
            Add(new XElement(N("FB"),
                new XAttribute("ID", BrokerFbId), new XAttribute("Name", BrokerFbName),
                new XAttribute("Type", BrokerFbType), new XAttribute("Namespace", "Main"),
                new XAttribute("x", isSysres ? "12180" : "36000"), new XAttribute("y", "6600")));

            // 2. Sensor publisher (SRC): broker OutputVars -> Feed sensor symlinks.
            var (sArity, sType) = SymlinkBridge.Pick("SRC", Sensors.Length);
            var sNames = Sensors.Select(s => $"{resourceName}.{s.Symlink}").ToList();
            Add(SymlinkBridge.BuildFb(ns, nextId++, uid++, "RevPiSensorPublisher", sType, sArity, sNames,
                isSysres ? 13500 : 37500, 6600));

            // 3. Coil subscriber (DST): Feed coil symlinks -> broker InputVars.
            var (cArity, cType) = SymlinkBridge.Pick("DST", Coils.Length);
            var cNames = Coils.Select(c => $"{resourceName}.{c.Symlink}").ToList();
            Add(SymlinkBridge.BuildFb(ns, nextId++, uid++, "RevPiCoilSubscriber", cType, cArity, cNames,
                isSysres ? 10800 : 34800, 6600));

            // 4. ScanCycle heartbeat (our CAT has no plc_out to trigger the broker like the reference).
            Add(new XElement(N("FB"),
                new XAttribute("ID", nextId++), new XAttribute("Name", ScanFbName),
                new XAttribute("Type", "E_DELAY"), new XAttribute("x", isSysres ? "9500" : "33500"),
                new XAttribute("y", "7400"), new XAttribute("Namespace", "IEC61499.Standard"),
                new XElement(N("Parameter"), new XAttribute("Name", "DT"), new XAttribute("Value", ScanPeriod))));

            // 5. Wiring. INIT off the resource's local boot chain; scan drives the Modbus cycle; the broker's
            //    sensor event republishes the sensor symlinks. Full swap: Feed_Station (on RevPi) inits the
            //    broker. Partial swap: Feed_Station is on M262, so anchor off a LOCAL RevPi component present
            //    in BOTH this sysres and the shared syslay (PartInHopper) -> no cross-device INIT wire.
            bool Has(string nm) => net.Elements(N("FB")).Any(f => (string?)f.Attribute("Name") == nm);
            string initSrc = !CodeGen.Configuration.MapperConfig.PartialRevPi && Has("Feed_Station")
                ? "Feed_Station"
                : new[] { "PartInHopper", "Feeder", "Checker", "FB1" }.FirstOrDefault(Has) ?? "FB1";
            Ev($"{initSrc}.INITO", $"{BrokerFbName}.INIT");
            Ev($"{initSrc}.INITO", "RevPiSensorPublisher.INIT");
            Ev($"{initSrc}.INITO", "RevPiCoilSubscriber.INIT");
            Ev($"{initSrc}.INITO", $"{ScanFbName}.START");
            Ev($"{ScanFbName}.EO", $"{ScanFbName}.START");
            Ev($"{ScanFbName}.EO", "RevPiCoilSubscriber.REQ");
            Ev($"{ScanFbName}.EO", $"{BrokerFbName}.REQ_INT_BOOL");
            Ev($"{BrokerFbName}.PLC_EVENT", "RevPiSensorPublisher.REQ");
            for (int i = 0; i < Sensors.Length; i++)
                Da($"{BrokerFbName}.{Sensors[i].Var}", $"RevPiSensorPublisher.VALUE{i + 1}");
            for (int i = 0; i < Coils.Length; i++)
                Da($"RevPiCoilSubscriber.VALUE{i + 1}", $"{BrokerFbName}.{Coils[i].Var}");

            doc.Save(path);
            return true;
        }

        static XElement EnsureSection(XElement net, XName name)
        {
            var el = new XElement(name);
            net.Add(el);
            return el;
        }

        static int NextFbId(XElement root, Func<string, XName> N)
        {
            var idc = root.Elements(N("Attribute"))
                .FirstOrDefault(a => (string?)a.Attribute("Name") == "Configuration.FB.IDCounter");
            int start = (idc != null && int.TryParse((string?)idc.Attribute("Value"), out var c)) ? c : 200;
            return Math.Max(start, 200);
        }
    }
}
