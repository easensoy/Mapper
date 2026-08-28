using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using System.IO;
using System.Xml.Linq;
using CodeGen.Translation;

namespace CodeGen.Devices.BX1
{
    // BX1 cover-I/O broker injection. Broker id F6C04A4BA6FA8593 must stay the id the copied BX1 .hcf binds to.
    public static class Bx1IoBrokerInjector
    {
        static readonly XNamespace Ns = CodeGen.Devices.Core.Station2DeviceEmitter.LibElNs;

        // The broker instance's frozen EAE id, read from the descriptor of the target that hosts it:
        // keyed on the name "BX1" a second coupler-bearing device could only reuse the first one's id.
        public static string BrokerFbIdOf(Configuration.CompilerConfiguration cfg, PlcAssignment target) =>
            cfg.Targets.Of(target).Identity.IoBrokerFb;
        public const string BrokerFbName = "BX1_IO";
        public const string BrokerFbType = "PLC_RW_BX1";

        // Derived from the one arity->hash table rather than restated: a second copy of an EAE type name
        // is a second thing to keep right.
        const string ScanFbName = "BX1_IO_Cycle";
        static string ScanPeriod(Configuration.CompilerConfiguration cfg) =>
            Configuration.GenerationConfig.Duration(cfg.Generation.Bx1IoScanPeriodMs);

        // The coupler word as the rig wired it, from Config/device.yml: no plant name or bit number lives here.
        static Configuration.Bx1IoProfile Io(Configuration.CompilerConfiguration cfg) => cfg.Devices.Bx1Io;

        // Init-root cover: the LAST on the profile's chain, so every cover ahead of it has initialised first.
        static string InitRootCover(Configuration.CompilerConfiguration cfg) => Io(cfg).Covers[^1].Component;

        static (string Cover, string? SensorFromHome, string? SensorFromWork,
                string Event, string? CoilToHome, string? CoilToWork)[] Covers(
            Configuration.CompilerConfiguration cfg) =>
            Io(cfg).Covers.Select(c => (c.Component, c.SensorFromHome?.Signal, c.SensorFromWork?.Signal,
                                   c.Event, c.CoilToHome?.Signal, c.CoilToWork?.Signal)).ToArray();

        // symlink name -> word bit. The cover-present bit is published under EVERY top-cover spelling the profile
        // lists: the broker is a shared TYPE and cannot know which one a twin uses; an unsubscribed SRC is inert.
        static (string Sym, int Bit)[] CoverSensors(Configuration.CompilerConfiguration cfg) =>
            Io(cfg).Covers
                .SelectMany(c => new[]
                {
                    c.SensorFromHome == null ? default : ($"{c.Component}.athome", c.SensorFromHome.Bit),
                    c.SensorFromWork == null ? default : ($"{c.Component}.atwork", c.SensorFromWork.Bit),
                })
                .Where(t => t.Item1 != null)
                .Concat(Io(cfg).Covers
                    .Where(c => c.SensorFromWork != null && c.SensorFromHome == null)
                    .SelectMany(c => cfg.Rig.Roles.TopCoverSensor
                        .Select(n => ($"{n}.Input", c.SensorFromWork!.Bit))))
                .ToArray();

        static (string Sym, int Bit)[] CoverCoils(Configuration.CompilerConfiguration cfg) =>
            Io(cfg).Covers
                .SelectMany(c => new[]
                {
                    c.CoilToWork == null ? default : ($"{c.Component}.OutputToWork", c.CoilToWork.Bit),
                    c.CoilToHome == null ? default : ($"{c.Component}.OutputToHome", c.CoilToHome.Bit),
                })
                .Where(t => t.Item1 != null)
                .ToArray();

        // Transforms the deployed PLC_RW_BX1.fbt into the internalized broker (CoverSensorPublisher +
        // CoverCoilSubscriber + ScanCycle); new FBs must be inserted BEFORE Input/Output/connections. Idempotent.
        public static void EmbedCoverBridgeInComposite(Configuration.CompilerConfiguration cfg,
            string fbtPath, string resourceName = "BX1_RES")
        {
            if (!File.Exists(fbtPath)) return;
            // No PreserveWhitespace: Save re-indents so every FB lands on its own line (EAE requires it).
            var doc = XDocument.Load(fbtPath);
            var net = doc.Root?.Element("FBNetwork");
            if (net == null) return;
            if (net.Elements("FB").Any(f => (string?)f.Attribute("Name") == "CoverSensorPublisher"))
            { ApplyBrokerLayout(net); doc.Save(fbtPath); return; }

            var ec = net.Element("EventConnections");
            var dc = net.Element("DataConnections");
            if (ec == null || dc == null) return;

            // Sweep any prior per-cover embed (Sense_*/Coil_*/ScanCycle) + its wires.
            static string FbOf(string? ep) =>
                ep == null ? "" : (ep.Contains('.') ? ep[..ep.IndexOf('.')] : ep);
            bool IsStale(string n) =>
                n.StartsWith("Sense_") || n.StartsWith("Coil_") || n == "ScanCycle";
            foreach (var fb in net.Elements("FB")
                         .Where(f => IsStale((string?)f.Attribute("Name") ?? "")).ToList())
                fb.Remove();
            foreach (var grp in new[] { ec, dc })
                foreach (var conn in grp.Elements("Connection")
                             .Where(c => IsStale(FbOf((string?)c.Attribute("Source"))) ||
                                         IsStale(FbOf((string?)c.Attribute("Destination")))).ToList())
                    conn.Remove();

            var idc = doc.Root!.Elements("Attribute")
                .FirstOrDefault(a => (string?)a.Attribute("Name") == "Configuration.FB.IDCounter");
            int nextId = (idc != null && int.TryParse((string?)idc.Attribute("Value"), out var cur))
                ? System.Math.Max(cur, 24) : 24;
            int uid = 20;
            var firstInput = net.Elements("Input").FirstOrDefault();

            string Iface(int n) => Core.SymlinkBridge.Iface(n);
            (int arity, string type) Pick(string sd, int need) => Core.SymlinkBridge.Pick(sd, need);
            void AddFb(string name, string type, int arity, string[] names, int x, int y)
            {
                var fb = new XElement("FB",
                    new XAttribute("ID", nextId++), new XAttribute("UID", uid++),
                    new XAttribute("Name", name), new XAttribute("Type", type),
                    new XAttribute("x", x.ToString()), new XAttribute("y", y.ToString()),
                    new XAttribute("Namespace", Configuration.GenerationConfig.Namespace),
                    new XElement("Attribute",
                        new XAttribute("Name", "Configuration.GenericFBType.InterfaceParams"),
                        new XAttribute("Value", Iface(arity))),
                    new XElement("Parameter", new XAttribute("Name", "QI"), new XAttribute("Value", "TRUE")));
                for (int i = 0; i < arity; i++)
                    fb.Add(new XElement("Parameter", new XAttribute("Name", $"NAME{i + 1}"),
                        new XAttribute("Value", $"'{names[i]}'")));
                if (firstInput != null) firstInput.AddBeforeSelf(fb); else net.Add(fb);
            }
            void Ev(string s, string d) => ec.Add(new XElement("Connection",
                new XAttribute("Source", s), new XAttribute("Destination", d)));
            void Da(string s, string d) => dc.Add(new XElement("Connection",
                new XAttribute("Source", s), new XAttribute("Destination", d)));

            var (sArity, sType) = Pick("SRC", CoverSensors(cfg).Length);
            var sNames = new string[sArity];
            for (int i = 0; i < sArity; i++)
                sNames[i] = i < CoverSensors(cfg).Length
                    ? $"{resourceName}.{CoverSensors(cfg)[i].Sym}"
                    : $"{resourceName}.{BrokerFbName}.CoverSensorSpare{i + 1}";
            AddFb("CoverSensorPublisher", sType, sArity, sNames, 3000, 700);

            var (cArity, cType) = Pick("DST", CoverCoils(cfg).Length);
            var cNames = new string[cArity];
            for (int i = 0; i < cArity; i++)
                cNames[i] = i < CoverCoils(cfg).Length
                    ? $"{resourceName}.{CoverCoils(cfg)[i].Sym}"
                    : $"{resourceName}.{BrokerFbName}.CoverCoilSpare{i + 1}";
            AddFb("CoverCoilSubscriber", cType, cArity, cNames, 5000, 700);

            // ScanCycle heartbeat: coils are published via an internal symlink with no boundary event, so the
            // subscriber must be REQ'd each cycle or the output word freezes.
            var scan = new XElement("FB",
                new XAttribute("ID", nextId++), new XAttribute("Name", "ScanCycle"),
                new XAttribute("Type", "E_DELAY"), new XAttribute("x", "700"),
                new XAttribute("y", "1400"), new XAttribute("Namespace", "IEC61499.Standard"),
                new XElement("Parameter", new XAttribute("Name", "DT"), new XAttribute("Value", ScanPeriod(cfg))));
            if (firstInput != null) firstInput.AddBeforeSelf(scan); else net.Add(scan);

            Ev("INIT", "CoverSensorPublisher.INIT");
            Ev("INIT", "CoverCoilSubscriber.INIT");
            Ev("INIT", "ScanCycle.START");
            Ev("ScanCycle.EO", "ScanCycle.START");
            Ev("ScanCycle.EO", "EIP_Input_Word.REQ");
            Ev("ScanCycle.EO", "CoverCoilSubscriber.REQ");
            Ev("EIPInputs_Bool.CNF", "CoverSensorPublisher.REQ");
            Ev("CoverCoilSubscriber.CNF", "EIPOutput_Bits.REQ");

            for (int i = 0; i < CoverSensors(cfg).Length; i++)
                Da($"EIPInputs_Bool.bit{CoverSensors(cfg)[i].Bit}", $"CoverSensorPublisher.VALUE{i + 1}");
            for (int i = 0; i < CoverCoils(cfg).Length; i++)
                Da($"CoverCoilSubscriber.VALUE{i + 1}", $"EIPOutput_Bits.bit{CoverCoils(cfg)[i].Bit}");

            // Remove the superseded cover-InputVar -> BitsToWord connections: two data sources per bit is an EAE error.
            var inputVarSources = Io(cfg).Covers
                .SelectMany(c => new[] { c.CoilToWork?.Signal, c.CoilToHome?.Signal })
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
            foreach (var conn in dc.Elements("Connection").Where(c =>
                         ((string?)c.Attribute("Destination"))?.StartsWith("EIPOutput_Bits.bit") == true &&
                         inputVarSources.Contains((string?)c.Attribute("Source"))).ToList())
                conn.Remove();

            idc?.SetAttributeValue("Value", nextId.ToString());
            ApplyBrokerLayout(net);
            doc.Save(fbtPath);
        }

        // Decode the EtherNet/IP input word ONCE at INIT so a cover already in place at power-on is reported
        // without a physical remove-and-replace: INITO carries the sampled word, so the change detector sees the
        // real bit while its PreVal is still FALSE. Fires once per INIT, adds nothing to the scan, idempotent.
        public static bool EnsureInitWordDecodeInComposite(string fbtPath)
        {
            if (!File.Exists(fbtPath)) return false;
            var doc = XDocument.Load(fbtPath);
            var ec = doc.Root?.Element("FBNetwork")?.Element("EventConnections");
            var net = doc.Root?.Element("FBNetwork");
            if (ec == null || net == null) return false;
            // Only meaningful when the composite actually carries the word decoder and the change detector.
            if (!net.Elements("FB").Any(f => (string?)f.Attribute("Name") == "EIPInputs_Bool") ||
                !net.Elements("FB").Any(f => (string?)f.Attribute("Name") == "EIP_Input_Word")) return false;
            if (!EnsureInitWordDecode(ec)) return false;
            doc.Save(fbtPath);
            return true;
        }

        private static bool EnsureInitWordDecode(XElement? ec)
        {
            if (ec == null) return false;
            if (ec.Elements("Connection").Any(c =>
                    (string?)c.Attribute("Source") == "EIP_Input_Word.INITO" &&
                    (string?)c.Attribute("Destination") == "EIPInputs_Bool.REQ")) return false;
            ec.Add(new XElement("Connection",
                new XAttribute("Source", "EIP_Input_Word.INITO"),
                new XAttribute("Destination", "EIPInputs_Bool.REQ")));
            return true;
        }

        // SAFETY (cover safe-start): a Bx1CoverFailsafe gate forces the safe-start actuator HOME on start
        // and holds until its at-home sensor reports. Which actuator, which coils and which sensor bit are
        // the resolved Bx1SafeStart the scanner validator also quotes — not bit numbers spelled here.
        // Fires only while the logic RUNS, NOT on EAE Clean/STOP/fault — that needs the coupler fallback word.
        public static bool InjectCoverFailsafeIntoBrokerType(
            Configuration.CompilerConfiguration cfg, string eaeRoot)
        {
            var plan = Bx1SafeStart.Resolve(cfg);
            var fbt = Path.Combine(eaeRoot, "IEC61499", "PLC_RW_BX1.fbt");
            if (!File.Exists(fbt)) return false;
            var doc = XDocument.Load(fbt);
            var net = doc.Root?.Element("FBNetwork");
            var ec = net?.Element("EventConnections");
            var dc = net?.Element("DataConnections");
            if (net == null || ec == null || dc == null) return false;
            if (net.Elements("FB").Any(f => (string?)f.Attribute("Name") == "CoverFailsafe"))
                return false;

            var idc = doc.Root!.Elements("Attribute")
                .FirstOrDefault(a => (string?)a.Attribute("Name") == "Configuration.FB.IDCounter");
            int nextId = (idc != null && int.TryParse((string?)idc.Attribute("Value"), out var cur)) ? cur : 30;

            var fb = new XElement("FB",
                new XAttribute("ID", nextId++), new XAttribute("Name", "CoverFailsafe"),
                new XAttribute("Type", "Bx1CoverFailsafe"),
                new XAttribute("x", "4600"), new XAttribute("y", "1300"),
                new XAttribute("Namespace", Configuration.GenerationConfig.Namespace));
            var firstInput = net.Elements("Input").FirstOrDefault();
            if (firstInput != null) firstInput.AddBeforeSelf(fb); else net.Add(fb);

            // Reroute each declared coil's bit through the gate, keyed on the bit dest (works for both
            // bridge forms). The gate's own pin names are its type's ABI, so they are read off the
            // deployed type rather than spelled here; the bit each one carries is the declaration's.
            var (drivePins, holdPins, sensorPin) = FailsafePins(eaeRoot);
            if (drivePins.Count != 2 || sensorPin == null || holdPins.Count < plan.HeldOff.Count)
                throw new InvalidOperationException(
                    $"Bx1CoverFailsafe exposes {drivePins.Count} gated drive pins, {holdPins.Count} " +
                    $"hold-off pins and {(sensorPin == null ? "no" : "one")} sensor pin, which cannot " +
                    $"carry the declared safe-start ({plan.HeldOff.Count} coils to hold off).");

            void Reroute(int bit, string fsIn, string fsOut)
            {
                var conn = dc.Elements("Connection").FirstOrDefault(c =>
                    (string?)c.Attribute("Destination") == "EIPOutput_Bits.bit" + bit);
                if (conn == null) return;
                var src = (string?)conn.Attribute("Source") ?? string.Empty;
                conn.Remove();
                dc.Add(new XElement("Connection", new XAttribute("Source", src),
                    new XAttribute("Destination", "CoverFailsafe." + fsIn)));
                dc.Add(new XElement("Connection", new XAttribute("Source", "CoverFailsafe." + fsOut),
                    new XAttribute("Destination", "EIPOutput_Bits.bit" + bit)));
            }
            // The safe actuator's own two coils first (the gate drives these), then everything it holds
            // off, in word-bit order — the order the emitted DataConnections carry.
            Reroute(plan.CoilToWork.Bit, drivePins[0].In, drivePins[0].Out);
            Reroute(plan.CoilToHome.Bit, drivePins[1].In, drivePins[1].Out);
            for (int i = 0; i < plan.HeldOff.Count; i++)
                Reroute(plan.HeldOff[i].Signal.Bit, holdPins[i].In, holdPins[i].Out);

            dc.Add(new XElement("Connection",
                new XAttribute("Source", "EIPInputs_Bool.bit" + plan.SensorFromHome.Bit),
                new XAttribute("Destination", "CoverFailsafe." + sensorPin)));

            foreach (var c in ec.Elements("Connection")
                         .Where(c => (string?)c.Attribute("Destination") == "EIPOutput_Bits.REQ").ToList())
                c.SetAttributeValue("Destination", "CoverFailsafe.REQ");
            ec.Add(new XElement("Connection", new XAttribute("Source", "CoverFailsafe.CNF"),
                new XAttribute("Destination", "EIPOutput_Bits.REQ")));
            ec.Add(new XElement("Connection", new XAttribute("Source", "INIT"),
                new XAttribute("Destination", "CoverFailsafe.INIT")));

            idc?.SetAttributeValue("Value", nextId.ToString());
            doc.Save(fbt);
            return true;
        }

        // The gate's interface, read off the deployed type. A gated pin is an InputVar with a matching
        // g<Name> OutputVar; the remaining InputVar is the release sensor. The FIRST TWO gated pins are
        // the ones the gate DRIVES (work then home) and the rest are held off — that pairing is the FB's
        // own contract, declared by the order it lists them, so a gate with more coils needs no edit here.
        static (System.Collections.Generic.List<(string In, string Out)> Drive,
                System.Collections.Generic.List<(string In, string Out)> Hold,
                string? Sensor) FailsafePins(string eaeRoot)
        {
            var drive = new System.Collections.Generic.List<(string, string)>();
            var hold = new System.Collections.Generic.List<(string, string)>();
            string? sensor = null;
            var fbt = Path.Combine(eaeRoot, "IEC61499", "Bx1CoverFailsafe.fbt");
            if (!File.Exists(fbt)) return (drive, hold, sensor);

            var iface = XDocument.Load(fbt).Root?.Element("InterfaceList");
            var outs = new System.Collections.Generic.HashSet<string>(
                iface?.Element("OutputVars")?.Elements("VarDeclaration")
                    .Select(v => (string?)v.Attribute("Name") ?? string.Empty) ?? Enumerable.Empty<string>(),
                StringComparer.Ordinal);
            foreach (var name in iface?.Element("InputVars")?.Elements("VarDeclaration")
                         .Select(v => (string?)v.Attribute("Name") ?? string.Empty)
                     ?? Enumerable.Empty<string>())
            {
                if (outs.Contains("g" + name)) (drive.Count < 2 ? drive : hold).Add((name, "g" + name));
                else sensor ??= name;
            }
            return (drive, hold, sensor);
        }

        static void ApplyBrokerLayout(XElement net)
        {
            var fbXY = new Dictionary<string, (int x, int y)>
            {
                ["ScanCycle"]            = (400, 300),
                ["EIP_Input_Word"]       = (2100, 300),
                ["EIPInputs_Bool"]       = (3300, 300),
                ["CoverSensorPublisher"] = (5400, 300),
                ["EIPOutput_Bits"]       = (6700, 300),
                ["EIP_Output_Word"]      = (8000, 300),
                ["CoverCoilSubscriber"]  = (5400, 1900),
                ["FB2"]                  = (3300, 1900),
            };
            foreach (var fb in net.Elements("FB"))
                if (fbXY.TryGetValue((string?)fb.Attribute("Name") ?? "", out var p))
                {
                    fb.SetAttributeValue("x", p.x.ToString());
                    fb.SetAttributeValue("y", p.y.ToString());
                }
            int py = 300;
            foreach (var pin in net.Elements("Input"))
            { pin.SetAttributeValue("x", "0"); pin.SetAttributeValue("y", py.ToString()); py += 450; }
            py = 300;
            foreach (var pin in net.Elements("Output"))
            { pin.SetAttributeValue("x", "9400"); pin.SetAttributeValue("y", py.ToString()); py += 450; }
        }

        // Injects the BX1_IO broker + cover symlink bridge into the BX1 SubApp (syslay) and sysres; returns files touched.
        public static int InjectBx1IoBroker(Configuration.CompilerConfiguration cfg, PlcAssignment target,
            string syslayPath,
            SystemInjector.BindingApplicationReport report)
        {
            int touched = 0;
            try
            {
                var bx1Sysres = FindBx1Sysres(cfg, syslayPath);
                var resourceName = ReadResourceName(bx1Sysres) ?? cfg.Targets.Of(target).ResourceName;
                foreach (var (label, path, isSysres) in new[]
                {
                    ("syslay", syslayPath,        false),
                    ("sysres", bx1Sysres ?? "",   true),
                })
                {
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                    try
                    {
                        if (InjectInto(cfg, target, path, isSysres, label, resourceName, report)) touched++;
                    }
                    catch (IOException)
                    {
                        report.Missing.Add($"[BX1][Broker] FAILED to write the cover bridge to the BX1 " +
                            $"{label} — the file is LOCKED. Close the BX1 / BX1_RES view in EAE " +
                            "(or close EAE) before clicking Test Runtime, then re-run.");
                    }
                    catch (Exception ex)
                    {
                        report.Missing.Add($"[BX1][Broker] {label} injection error: {ex.Message}");
                    }
                }
                if (touched == 0)
                    report.Missing.Add("[BX1][Broker] BX1_IO not injected — neither the BX1 SubApp " +
                        "(syslay) nor the BX1 sysres with the cover FBs was found.");
            }
            catch (Exception ex)
            {
                report.Missing.Add($"[BX1][Broker] BX1_IO injection failed: {ex.Message}");
            }
            return touched;
        }

        static bool InjectInto(Configuration.CompilerConfiguration cfg, PlcAssignment target, string path, bool isSysres,
            string label, string resourceName, SystemInjector.BindingApplicationReport report)
        {
            var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            var net = FindCoverNetwork(cfg, doc);
            if (net == null) return false;
            var fileTag = isSysres ? "sysres" : "syslay";

            bool hasGripper = net.Elements(Ns + "FB")
                .Any(f => (string?)f.Attribute("Name") == InitRootCover(cfg));

            var ec = net.Element(Ns + "EventConnections") ?? AddSection(net, "EventConnections");
            var dc = net.Element(Ns + "DataConnections")  ?? AddSection(net, "DataConnections");

            // The broker FB (forced id so the copied .hcf matches).
            AddBrokerFbIfAbsent(net, BrokerFbIdOf(cfg, target), isSysres, isSysres ? 9500 : 32000, 5800);
            if (hasGripper)
                AddEvent(ec, $"{InitRootCover(cfg)}.INITO", $"{BrokerFbName}.INIT");

            // The bridge lives INSIDE PLC_RW_BX1, so the resource carries only BX1_IO. An older deploy put
            // the bridge at resource level, so any such FB is swept here and the tree converges.
            {
                static string FbOf(string? ep) =>
                    ep == null ? "" : (ep.Contains('.') ? ep[..ep.IndexOf('.')] : ep);
                bool IsExtBridge(string n) =>
                    n.StartsWith("BX1IO_Sense_") || n.StartsWith("BX1IO_Coil_") || n == ScanFbName;
                foreach (var fb in net.Elements(Ns + "FB")
                             .Where(f => IsExtBridge((string?)f.Attribute("Name") ?? "")).ToList())
                    fb.Remove();
                foreach (var grp in new[] { ec, dc })
                    foreach (var conn in grp.Elements(Ns + "Connection")
                                 .Where(c => IsExtBridge(FbOf((string?)c.Attribute("Source"))) ||
                                             IsExtBridge(FbOf((string?)c.Attribute("Destination")))).ToList())
                        conn.Remove();

                // A composite has no path to a sibling instance's event input, so the ONE wire that must stay at
                // resource level is the top-cover re-sample trigger; without it a cover in place at power-on is never reported.
                var topCover = new System.Collections.Generic.HashSet<string>(
                    cfg.Rig.Roles.TopCoverSensor, StringComparer.OrdinalIgnoreCase);
                var tcFb = net.Elements(Ns + "FB").FirstOrDefault(f =>
                    (string?)f.Attribute("Type") == cfg.Manifest.SensorType.Name &&
                    topCover.Contains((string?)f.Attribute("Name") ?? ""));
                if (tcFb != null)
                    AddEvent(ec, $"{BrokerFbName}.CoverSensorEvent",
                             $"{(string)tcFb.Attribute("Name")!}.RD");

                SaveWithRetry(doc, path);
                report.Missing.Add($"[BX1][Broker] BX1_IO injected into {label} (resource " +
                    $"{resourceName}); cover bridge INTERNALIZED in PLC_RW_BX1 — swept any external " +
                    "BX1IO_Sense_*/BX1IO_Coil_*/BX1_IO_Cycle FBs.");
                return true;
            }
        }

        // The broker instance itself. It carries no symlink names and no interface arity: the bridge that
        // needed those now lives inside PLC_RW_BX1.
        static void AddBrokerFbIfAbsent(XElement net, string brokerFbId, bool isSysres, int x, int y)
        {
            if (net.Elements(Ns + "FB").Any(f => (string?)f.Attribute("Name") == BrokerFbName)) return;
            var fb = new XElement(Ns + "FB",
                new XAttribute("ID", brokerFbId), new XAttribute("Name", BrokerFbName),
                new XAttribute("Type", BrokerFbType), new XAttribute("Namespace", Configuration.GenerationConfig.Namespace));
            if (isSysres) fb.Add(new XAttribute("Mapping", brokerFbId));
            fb.Add(new XAttribute("x", x.ToString()), new XAttribute("y", y.ToString()));

            var firstConn = net.Element(Ns + "EventConnections")
                         ?? net.Element(Ns + "DataConnections")
                         ?? net.Element(Ns + "AdapterConnections");
            if (firstConn != null) firstConn.AddBeforeSelf(fb);
            else net.Add(fb);
        }

        static void AddEvent(XElement ec, string src, string dst)
        {
            if (ec.Elements(Ns + "Connection").Any(c =>
                (string?)c.Attribute("Source") == src && (string?)c.Attribute("Destination") == dst)) return;
            ec.Add(new XElement(Ns + "Connection", new XAttribute("Source", src), new XAttribute("Destination", dst)));
        }

        static XElement? FindCoverNetwork(Configuration.CompilerConfiguration cfg, XDocument doc)
        {
            foreach (var net in doc.Descendants(Ns + "FBNetwork")
                         .Concat(doc.Descendants(Ns + "SubAppNetwork")))
            {
                if (net.Elements(Ns + "FB").Any(f =>
                {
                    var n = (string?)f.Attribute("Name") ?? string.Empty;
                    return Io(cfg).Covers.Any(c => string.Equals(c.Component, n, StringComparison.Ordinal));
                }))
                    return net;
            }
            return null;
        }

        static XElement AddSection(XElement net, string name)
        {
            var sec = new XElement(Ns + name);
            net.Add(sec);
            return sec;
        }

        // Saves with a few short retries for a transient EAE file lock; re-throws IOException if still locked.
        static void SaveWithRetry(XDocument doc, string path)
        {
            const int attempts = 6;
            for (int i = 0; i < attempts; i++)
            {
                try { doc.Save(path); return; }
                catch (IOException) when (i < attempts - 1)
                {
                    System.Threading.Thread.Sleep(250);
                }
            }
            doc.Save(path);
        }

        static string? ReadResourceName(string? sysresPath)
        {
            if (string.IsNullOrEmpty(sysresPath) || !File.Exists(sysresPath)) return null;
            try { return (string?)XDocument.Load(sysresPath).Root?.Attribute("Name"); }
            catch { return null; }
        }

        static string? FindBx1Sysres(Configuration.CompilerConfiguration cfg, string syslayPath)
        {
            string? systemDir = null;
            foreach (var seed in new[] { Path.GetDirectoryName(syslayPath),
                                         Path.GetDirectoryName(cfg.Paths.SysresPath2) })
            {
                var probe = seed;
                while (!string.IsNullOrEmpty(probe))
                {
                    if (string.Equals(Path.GetFileName(probe), "System", StringComparison.OrdinalIgnoreCase))
                    { systemDir = probe; break; }
                    probe = Path.GetDirectoryName(probe);
                }
                if (systemDir != null) break;
            }
            if (string.IsNullOrEmpty(systemDir) || !Directory.Exists(systemDir)) return null;

            foreach (var sysres in Directory.EnumerateFiles(systemDir, "*.sysres", SearchOption.AllDirectories))
            {
                try
                {
                    var head = File.ReadAllText(sysres);
                    if (Io(cfg).Covers.Any(c => head.Contains(c.Component, StringComparison.Ordinal)))
                        return sysres;
                }
                catch { /* ignore unreadable */ }
            }
            return null;
        }
    }
}
