using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Translation;

namespace CodeGen.Devices.BX1
{
    // BX1 EtherNet/IP cover-I/O broker injection. Broker id F6C04A4BA6FA8593 is the id the copied
    // BX1 .hcf binds to. Bit map (from PLC_RW_BX1): IN bit0=Hr athome,1=Hr atwork,2=Vr athome,
    // 3=Vr atwork,5=gripper; OUT bit0=Hr OutputToWork,1=Hr OutputToHome,2=Vr OutputToWork,3=gripper.
    // Gated by cfg.DeployBx1IoBroker. Idempotent.
    public static class Bx1IoBrokerInjector
    {
        static readonly XNamespace Ns = "https://www.se.com/LibraryElements";

        public const string BrokerFbId = "F6C04A4BA6FA8593";
        public const string BrokerFbName = "BX1_IO";
        public const string BrokerFbType = "PLC_RW_BX1";

        const string Sym2BoolSrc = "SYMLINKMULTIVARSRC_277E97BEC1451D2C";
        const string Sym2BoolDst = "SYMLINKMULTIVARDST_277E97BEC1451D2C";
        const string TwoBoolIfaceParams = "Runtime.System#I:=2;VALUE${I}:BOOL,BOOL";
        const string ScanFbName = "BX1_IO_Cycle";
        const string ScanPeriod = "T#50ms";

        sealed class CoverMap
        {
            public string Cover = "";
            public string? SensorFromHome;
            public string? SensorFromWork;
            public string Event = "";
            public string? CoilToHome;
            public string? CoilToWork;
        }

        static readonly CoverMap[] Covers =
        {
            new CoverMap { Cover = "CoverPNP_Hr",      SensorFromHome = "CoverPnpHrAtHome", SensorFromWork = "CoverPnpHrAtWork",
                           Event = "CoverPnpHrEvent",  CoilToHome = "Cover_Pnp_Hr_ToHome",  CoilToWork = "Cover_Pnp_Hr_ToWork" },
            new CoverMap { Cover = "CoverPNP_Vr",      SensorFromHome = "CoverPnpVrAtHome", SensorFromWork = "CoverPnpVrAtWork",
                           Event = "CoverPnpVrEvent",  CoilToHome = null,                   CoilToWork = "Cover_Pnp_Vr_Q" },
            // Gripper: 1 physical sensor (gripped) -> atwork; no home sensor.
            new CoverMap { Cover = "CoverPnp_Gripper", SensorFromHome = null,               SensorFromWork = "CoverPnpSensor",
                           Event = "CoverSensorEvent", CoilToHome = null,                   CoilToWork = "Cover_Gripper_Q" },
        };

        // Internalized cover-I/O broker (cfg.Bx1BridgeInsideComposite). Two ordered tables:
        // index = VALUE index = NAME index = wiring order. Bit positions are fixed by the
        // PLC_RW_BX1 WordToBits/BitsToWord core: IN bit0=Hr athome,1=Hr atwork,2=Vr athome,
        // 3=Vr atwork,5=gripper atwork; OUT bit0=Hr OutputToWork,1=Hr OutputToHome,
        // 2=Vr OutputToWork,3=gripper OutputToWork.
        static readonly (string Sym, int Bit)[] CoverSensors =
        {
            ("CoverPNP_Hr.athome", 0), ("CoverPNP_Hr.atwork", 1),
            ("CoverPNP_Vr.athome", 2), ("CoverPNP_Vr.atwork", 3),
            ("CoverPnp_Gripper.atwork", 5),     // gripper has no home sensor
        };
        static readonly (string Sym, int Bit)[] CoverCoils =
        {
            ("CoverPNP_Hr.OutputToWork", 0), ("CoverPNP_Hr.OutputToHome", 1),
            ("CoverPNP_Vr.OutputToWork", 2), ("CoverPnp_Gripper.OutputToWork", 3),
        };

        // EAE compiler-generates SYMLINKMULTIVAR{SRC,DST}_<hash> per BOOL arity; the hash is
        // GUI-computed (not derivable). Only these arities exist, so a publisher/subscriber must
        // use the smallest available arity >= its value count (surplus VALUEs are inert). SRC and
        // DST of the same arity share the hash. 5-BOOL does not exist -> the 5-sensor publisher uses 7.
        static readonly (int Arity, string Hash)[] BoolSymlinkTypes =
        {
            (1, "1559B0FF8170C9BA0"), (2, "277E97BEC1451D2C"), (3, "151ACB50A2F8223B2"),
            (4, "19628BFC3C74F1AB1"), (7, "238520AAD20108C65"), (10, "2183AAEC3B58E76C9"),
            (15, "2217C9CA39686140D"),
        };

        // Transforms the deployed PLC_RW_BX1.fbt into the internalized broker: one CoverSensorPublisher
        // (SYMLINKMULTIVARSRC) + one CoverCoilSubscriber (SYMLINKMULTIVARDST) + one ScanCycle (E_DELAY).
        // New FBs are inserted BEFORE Input/Output/connections (EAE FBNetwork ordering). Idempotent.
        // BX1-only. EAE-runtime unknown: whether ABSOLUTE cross-instance symlink names resolve from
        // inside a composite.
        public static void EmbedCoverBridgeInComposite(string fbtPath, string resourceName = "BX1_RES")
        {
            if (!File.Exists(fbtPath)) return;
            // No PreserveWhitespace: Save re-indents so every FB lands on its own line (EAE requires it).
            var doc = XDocument.Load(fbtPath);
            var net = doc.Root?.Element("FBNetwork");
            if (net == null) return;
            // Already embedded? Re-apply the layout only (deploy is copy-if-absent, so this is the
            // only path an already-embedded file picks up layout changes).
            if (net.Elements("FB").Any(f => (string?)f.Attribute("Name") == "CoverSensorPublisher"))
            { ApplyBrokerLayout(net); doc.Save(fbtPath); return; }

            var ec = net.Element("EventConnections");
            var dc = net.Element("DataConnections");
            if (ec == null || dc == null) return;

            // Sweep any prior per-cover embed (Sense_*/Coil_*/ScanCycle) + its wires so a re-deploy
            // cleanly replaces it with the consolidated broker.
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

            string Iface(int n) =>
                "Runtime.System#I:=" + n + ";VALUE${I}:" + string.Join(",", Enumerable.Repeat("BOOL", n));
            (int arity, string type) Pick(string sd, int need)
            {
                var t = BoolSymlinkTypes.Where(x => x.Arity >= need).OrderBy(x => x.Arity).First();
                return (t.Arity, $"SYMLINKMULTIVAR{sd}_{t.Hash}");
            }
            void AddFb(string name, string type, int arity, string[] names, int x, int y)
            {
                var fb = new XElement("FB",
                    new XAttribute("ID", nextId++), new XAttribute("UID", uid++),
                    new XAttribute("Name", name), new XAttribute("Type", type),
                    new XAttribute("x", x.ToString()), new XAttribute("y", y.ToString()),
                    new XAttribute("Namespace", "Main"),
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

            var (sArity, sType) = Pick("SRC", CoverSensors.Length);
            var sNames = new string[sArity];
            for (int i = 0; i < sArity; i++)
                sNames[i] = i < CoverSensors.Length
                    ? $"{resourceName}.{CoverSensors[i].Sym}"
                    : $"{resourceName}.{BrokerFbName}.CoverSensorSpare{i + 1}";
            AddFb("CoverSensorPublisher", sType, sArity, sNames, 3000, 700);

            var (cArity, cType) = Pick("DST", CoverCoils.Length);
            var cNames = new string[cArity];
            for (int i = 0; i < cArity; i++)
                cNames[i] = i < CoverCoils.Length
                    ? $"{resourceName}.{CoverCoils[i].Sym}"
                    : $"{resourceName}.{BrokerFbName}.CoverCoilSpare{i + 1}";
            AddFb("CoverCoilSubscriber", cType, cArity, cNames, 5000, 700);

            // ScanCycle (E_DELAY) symlink-refresh heartbeat: the cover CAT publishes coils via an
            // internal symlink with no boundary event, so CoverCoilSubscriber must be REQ'd each
            // cycle to re-pack the output word — without it the EtherNet/IP output word freezes.
            var scan = new XElement("FB",
                new XAttribute("ID", nextId++), new XAttribute("Name", "ScanCycle"),
                new XAttribute("Type", "E_DELAY"), new XAttribute("x", "700"),
                new XAttribute("y", "1400"), new XAttribute("Namespace", "IEC61499.Standard"),
                new XElement("Parameter", new XAttribute("Name", "DT"), new XAttribute("Value", ScanPeriod)));
            if (firstInput != null) firstInput.AddBeforeSelf(scan); else net.Add(scan);

            Ev("INIT", "CoverSensorPublisher.INIT");
            Ev("INIT", "CoverCoilSubscriber.INIT");
            Ev("INIT", "ScanCycle.START");
            Ev("ScanCycle.EO", "ScanCycle.START");
            Ev("ScanCycle.EO", "EIP_Input_Word.REQ");
            Ev("ScanCycle.EO", "CoverCoilSubscriber.REQ");
            Ev("EIPInputs_Bool.CNF", "CoverSensorPublisher.REQ");
            Ev("CoverCoilSubscriber.CNF", "EIPOutput_Bits.REQ");

            for (int i = 0; i < CoverSensors.Length; i++)
                Da($"EIPInputs_Bool.bit{CoverSensors[i].Bit}", $"CoverSensorPublisher.VALUE{i + 1}");
            for (int i = 0; i < CoverCoils.Length; i++)
                Da($"CoverCoilSubscriber.VALUE{i + 1}", $"EIPOutput_Bits.bit{CoverCoils[i].Bit}");

            // Remove the superseded cover-InputVar -> BitsToWord connections (two data sources per
            // bit is an EAE error; output bits now come from the subscriber).
            var inputVarSources = new[] { "Cover_Pnp_Hr_ToWork", "Cover_Pnp_Hr_ToHome", "Cover_Pnp_Vr_Q", "Cover_Gripper_Q" };
            foreach (var conn in dc.Elements("Connection").Where(c =>
                         ((string?)c.Attribute("Destination"))?.StartsWith("EIPOutput_Bits.bit") == true &&
                         inputVarSources.Contains((string?)c.Attribute("Source"))).ToList())
                conn.Remove();

            idc?.SetAttributeValue("Value", nextId.ToString());
            ApplyBrokerLayout(net);
            doc.Save(fbtPath);
        }

        // SAFETY (CoverPNP_Hr <-> Bearing_PnP swivel collision). Inserts a Bx1CoverFailsafe gate that,
        // on INIT/cold/warm start, forces CoverPNP_Hr HOME (bit0 ToWork=0, bit1 ToHome=1) and Vr/gripper
        // coils off, and HOLDS until the Hr at-home sensor (input bit0) reads TRUE — so while the logic
        // RUNS the broker can never drive cover_hr to Work on deploy/login/restart (double-acting Hr
        // needs ToHome=1 to return; both coils 0 only holds). Fires only while the logic is alive — does
        // NOT cover EAE Clean/STOP/fault; homing while STOPPED needs the TM3BC coupler output fallback
        // (TM3DQ16T ToHome channel -> 1 = word 16#0002) set on the coupler's web server (192.168.1.210),
        // which the Mapper cannot emit. Idempotent. BX1-only, gated by cfg.Bx1CoverSafeStart.
        public static bool InjectCoverFailsafeIntoBrokerType(string eaeRoot)
        {
            var fbt = Path.Combine(eaeRoot, "IEC61499", "PLC_RW_BX1.fbt");
            if (!File.Exists(fbt)) return false;
            var doc = XDocument.Load(fbt);
            var net = doc.Root?.Element("FBNetwork");
            var ec = net?.Element("EventConnections");
            var dc = net?.Element("DataConnections");
            if (net == null || ec == null || dc == null) return false;
            if (net.Elements("FB").Any(f => (string?)f.Attribute("Name") == "CoverFailsafe"))
                return false; // idempotent

            var idc = doc.Root!.Elements("Attribute")
                .FirstOrDefault(a => (string?)a.Attribute("Name") == "Configuration.FB.IDCounter");
            int nextId = (idc != null && int.TryParse((string?)idc.Attribute("Value"), out var cur)) ? cur : 30;

            var fb = new XElement("FB",
                new XAttribute("ID", nextId++), new XAttribute("Name", "CoverFailsafe"),
                new XAttribute("Type", "Bx1CoverFailsafe"),
                new XAttribute("x", "4600"), new XAttribute("y", "1300"),
                new XAttribute("Namespace", "Main"));
            var firstInput = net.Elements("Input").FirstOrDefault();
            if (firstInput != null) firstInput.AddBeforeSelf(fb); else net.Add(fb);

            // Reroute each output bit's source through the gate, keyed on the bit destination so it
            // works for both the external-bridge and internalized forms.
            void Reroute(string bit, string fsIn, string fsOut)
            {
                var conn = dc.Elements("Connection").FirstOrDefault(c =>
                    (string?)c.Attribute("Destination") == "EIPOutput_Bits." + bit);
                if (conn == null) return;
                var src = (string?)conn.Attribute("Source");
                conn.Remove();
                dc.Add(new XElement("Connection", new XAttribute("Source", src),
                    new XAttribute("Destination", "CoverFailsafe." + fsIn)));
                dc.Add(new XElement("Connection", new XAttribute("Source", "CoverFailsafe." + fsOut),
                    new XAttribute("Destination", "EIPOutput_Bits." + bit)));
            }
            Reroute("bit0", "ToWork", "gToWork");
            Reroute("bit1", "ToHome", "gToHome");
            Reroute("bit2", "Vr", "gVr");
            Reroute("bit3", "Grip", "gGrip");

            // Hr at-home feedback = EIPInputs_Bool.bit0.
            dc.Add(new XElement("Connection", new XAttribute("Source", "EIPInputs_Bool.bit0"),
                new XAttribute("Destination", "CoverFailsafe.AtHome")));

            // The output-word write trigger now fires the gate first; the gate's CNF writes the word.
            // INIT arms the safe-start (force home) before the first scan.
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

        // Lays the embedded broker FBs + pins out in left-to-right columns (EAE renders by x/y).
        // Deterministic, safe to re-run on every (re)deploy. BX1-only.
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
            int py = 300;   // Input pins: far-left edge.
            foreach (var pin in net.Elements("Input"))
            { pin.SetAttributeValue("x", "0"); pin.SetAttributeValue("y", py.ToString()); py += 450; }
            py = 300;       // Output pins: far-right edge.
            foreach (var pin in net.Elements("Output"))
            { pin.SetAttributeValue("x", "9400"); pin.SetAttributeValue("y", py.ToString()); py += 450; }
        }

        // Injects the BX1_IO broker + the cover symlink bridge into the BX1 SubApp (syslay) and the
        // BX1 sysres. Returns the number of files touched. Idempotent.
        public static int InjectBx1IoBroker(MapperConfig cfg, string syslayPath,
            SystemInjector.BindingApplicationReport report)
        {
            int touched = 0;
            try
            {
                var bx1Sysres = FindBx1Sysres(cfg, syslayPath);
                var resourceName = ReadResourceName(bx1Sysres) ?? "BX1_RES";
                foreach (var (label, path, isSysres) in new[]
                {
                    ("syslay", syslayPath,        false),
                    ("sysres", bx1Sysres ?? "",   true),
                })
                {
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                    try
                    {
                        if (InjectInto(path, isSysres, label, resourceName,
                                cfg.Bx1BridgeInsideComposite, report)) touched++;
                    }
                    catch (IOException)
                    {
                        // The deployed .sysres/.syslay is LOCKED (EAE has BX1_RES open) — surface the fix.
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

        static bool InjectInto(string path, bool isSysres, string label, string resourceName,
            bool internalized, SystemInjector.BindingApplicationReport report)
        {
            var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            var net = FindCoverNetwork(doc);
            if (net == null) return false;
            var fileTag = isSysres ? "sysres" : "syslay";

            bool hasGripper = net.Elements(Ns + "FB")
                .Any(f => (string?)f.Attribute("Name") == "CoverPnp_Gripper");

            var ec = net.Element(Ns + "EventConnections") ?? AddSection(net, "EventConnections");
            var dc = net.Element(Ns + "DataConnections")  ?? AddSection(net, "DataConnections");

            // The broker FB (forced id so the copied .hcf matches).
            AddFbIfAbsent(net, BrokerFbId, BrokerFbName, BrokerFbType, "Main", isSysres,
                isSysres ? 9500 : 32000, 5800, ifaceParams: null, name1: null, name2: null);
            if (hasGripper)
                AddEvent(ec, "CoverPnp_Gripper.INITO", $"{BrokerFbName}.INIT");

            // INTERNALIZED (cfg.Bx1BridgeInsideComposite, default): the bridge + scan live inside the
            // PLC_RW_BX1 composite, so the resource carries only BX1_IO — emit no external bridge here.
            if (internalized)
            {
                // Sweep any external bridge FBs/connections a prior external-path deploy left, so the
                // resource ends with only BX1_IO. BX1_IO + its INIT are kept.
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

                SaveWithRetry(doc, path);
                report.Missing.Add($"[BX1][Broker] BX1_IO injected into {label} (resource " +
                    $"{resourceName}); cover bridge INTERNALIZED in PLC_RW_BX1 — swept any external " +
                    "BX1IO_Sense_*/BX1IO_Coil_*/BX1_IO_Cycle FBs (cfg.Bx1BridgeInsideComposite).");
                return true;
            }

            // The EXTERNAL symlink bridge + scan cycle (when Bx1BridgeInsideComposite=false).
            int xSrc = isSysres ? 11000 : 35000;
            int xDst = isSysres ? 13000 : 37000;
            int slot = 0;
            foreach (var c in Covers)
            {
                var srcName = $"BX1IO_Sense_{c.Cover}";
                AddFbIfAbsent(net, Hex16($"{srcName}|{fileTag}"), srcName, Sym2BoolSrc, "Main", isSysres,
                    xSrc, 1500 + slot * 500, TwoBoolIfaceParams,
                    $"'{resourceName}.{c.Cover}.athome'", $"'{resourceName}.{c.Cover}.atwork'");
                AddEvent(ec, $"{BrokerFbName}.{c.Event}", $"{srcName}.REQ");
                if (c.SensorFromHome != null) AddData(dc, $"{BrokerFbName}.{c.SensorFromHome}", $"{srcName}.VALUE1");
                if (c.SensorFromWork != null) AddData(dc, $"{BrokerFbName}.{c.SensorFromWork}", $"{srcName}.VALUE2");

                var dstName = $"BX1IO_Coil_{c.Cover}";
                AddFbIfAbsent(net, Hex16($"{dstName}|{fileTag}"), dstName, Sym2BoolDst, "Main", isSysres,
                    xDst, 1500 + slot * 500, TwoBoolIfaceParams,
                    $"'{resourceName}.{c.Cover}.OutputToHome'", $"'{resourceName}.{c.Cover}.OutputToWork'");
                if (c.CoilToHome != null) AddData(dc, $"{dstName}.VALUE1", $"{BrokerFbName}.{c.CoilToHome}");
                if (c.CoilToWork != null) AddData(dc, $"{dstName}.VALUE2", $"{BrokerFbName}.{c.CoilToWork}");

                // INIT the bridge symlink FBs: a SYMLINKMULTIVAR{SRC,DST} only registers its symlink
                // (QI=TRUE) when its INIT fires, else it stays DISABLED and ignores every scan REQ.
                // Fan from CoverPnp_Gripper.INITO so every bridge FB is registered before the first EO.
                if (hasGripper)
                {
                    AddEvent(ec, "CoverPnp_Gripper.INITO", $"{srcName}.INIT");
                    AddEvent(ec, "CoverPnp_Gripper.INITO", $"{dstName}.INIT");
                }
                slot++;
            }

            // Scan cycle: E_DELAY self-loop.
            AddFbIfAbsent(net, Hex16($"{ScanFbName}|{fileTag}"), ScanFbName, "E_DELAY", "IEC61499.Standard",
                isSysres, isSysres ? 15000 : 39000, 1300, ifaceParams: null, name1: null, name2: null,
                extraParams: new[] { ("DT", ScanPeriod) });
            // Kick from a reliable event: the broker's PLC_EVENT does not fire on a plain INIT.
            // CoverPnp_Gripper.INITO also drives BX1_IO.INIT (init-chain tail), so the broker is ready
            // when the E_DELAY's first EO fires. PLC_EVENT kept as a redundant kick.
            if (hasGripper)
                AddEvent(ec, "CoverPnp_Gripper.INITO", $"{ScanFbName}.START");
            AddEvent(ec, $"{BrokerFbName}.PLC_EVENT", $"{ScanFbName}.START"); // redundant kick
            AddEvent(ec, $"{ScanFbName}.EO", $"{ScanFbName}.START");          // self re-arm
            AddEvent(ec, $"{ScanFbName}.EO", $"{BrokerFbName}.REQ");          // read input word -> sensors
            // Output write: CAUSAL CHAIN over the non-gripper coil readers (EO -> Hr.REQ -> Hr.CNF ->
            // Vr.REQ -> Vr.CNF -> BX1_IO.REQ_INT_BOOL) so no coil is stale when the word is packed — a
            // parallel fan-out RACES and can leave CoverPNP_Hr's home command stale. The gripper is
            // refreshed in parallel and is never a write trigger. Idempotent: drop prior coil-read/write
            // triggers first (AddEvent only de-dupes identical pairs).
            foreach (var conn in ec.Elements(Ns + "Connection")
                         .Where(c => { var d = (string?)c.Attribute("Destination") ?? "";
                                       return (d.StartsWith("BX1IO_Coil_") && d.EndsWith(".REQ"))
                                           || d == $"{BrokerFbName}.REQ_INT_BOOL"; }).ToList())
                conn.Remove();
            var writeChain = Covers.Where(c => !c.Cover.ToLowerInvariant().Contains("gripper")).ToList();
            var parallelReads = Covers.Where(c => c.Cover.ToLowerInvariant().Contains("gripper")).ToList();
            for (int i = 0; i < writeChain.Count; i++)
                AddEvent(ec, i == 0 ? $"{ScanFbName}.EO" : $"BX1IO_Coil_{writeChain[i - 1].Cover}.CNF",
                    $"BX1IO_Coil_{writeChain[i].Cover}.REQ");
            if (writeChain.Count > 0)
                AddEvent(ec, $"BX1IO_Coil_{writeChain[^1].Cover}.CNF", $"{BrokerFbName}.REQ_INT_BOOL");
            foreach (var c in parallelReads)
                AddEvent(ec, $"{ScanFbName}.EO", $"BX1IO_Coil_{c.Cover}.REQ");

            SaveWithRetry(doc, path);
            report.Missing.Add($"[BX1][Broker] BX1_IO + cover symlink bridge ({Covers.Length} covers, " +
                $"E_DELAY {ScanPeriod} scan) injected into {label} (resource {resourceName})" +
                (hasGripper ? "." : " — no gripper anchor; INIT not wired."));
            return true;
        }

        static void AddFbIfAbsent(XElement net, string id, string name, string type, string nsAttr,
            bool isSysres, int x, int y, string? ifaceParams, string? name1, string? name2,
            (string Name, string Value)[]? extraParams = null)
        {
            if (net.Elements(Ns + "FB").Any(f => (string?)f.Attribute("Name") == name)) return;
            var fb = new XElement(Ns + "FB",
                new XAttribute("ID", id), new XAttribute("Name", name),
                new XAttribute("Type", type), new XAttribute("Namespace", nsAttr));
            if (isSysres) fb.Add(new XAttribute("Mapping", id));
            fb.Add(new XAttribute("x", x.ToString()), new XAttribute("y", y.ToString()));
            if (ifaceParams != null)
                fb.Add(new XElement(Ns + "Attribute",
                    new XAttribute("Name", "Configuration.GenericFBType.InterfaceParams"),
                    new XAttribute("Value", ifaceParams)));
            if (name1 != null)
                fb.Add(new XElement(Ns + "Parameter", new XAttribute("Name", "QI"), new XAttribute("Value", "TRUE")));
            if (name1 != null)
                fb.Add(new XElement(Ns + "Parameter", new XAttribute("Name", "NAME1"), new XAttribute("Value", name1)));
            if (name2 != null)
                fb.Add(new XElement(Ns + "Parameter", new XAttribute("Name", "NAME2"), new XAttribute("Value", name2)));
            if (extraParams != null)
                foreach (var (pn, pv) in extraParams)
                    fb.Add(new XElement(Ns + "Parameter", new XAttribute("Name", pn), new XAttribute("Value", pv)));

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

        static void AddData(XElement dc, string src, string dst)
        {
            if (dc.Elements(Ns + "Connection").Any(c =>
                (string?)c.Attribute("Source") == src && (string?)c.Attribute("Destination") == dst)) return;
            dc.Add(new XElement(Ns + "Connection", new XAttribute("Source", src), new XAttribute("Destination", dst)));
        }

        static XElement? FindCoverNetwork(XDocument doc)
        {
            foreach (var net in doc.Descendants(Ns + "FBNetwork")
                         .Concat(doc.Descendants(Ns + "SubAppNetwork")))
            {
                if (net.Elements(Ns + "FB").Any(f =>
                {
                    var n = (string?)f.Attribute("Name");
                    return n == "CoverPNP_Hr" || n == "CoverPNP_Vr" || n == "CoverPnp_Gripper";
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

        // Saves with a few short retries so a transient EAE file lock doesn't drop the write;
        // re-throws IOException if still locked so the caller can prompt closing EAE.
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
            doc.Save(path); // final attempt — let the IOException propagate to the caller
        }

        static string Hex16(string seed)
        {
            using var sha = System.Security.Cryptography.SHA1.Create();
            var b = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(seed));
            var sb = new System.Text.StringBuilder(16);
            for (int i = 0; i < 8; i++) sb.Append(b[i].ToString("X2"));
            return sb.ToString();
        }

        static string? ReadResourceName(string? sysresPath)
        {
            if (string.IsNullOrEmpty(sysresPath) || !File.Exists(sysresPath)) return null;
            try { return (string?)XDocument.Load(sysresPath).Root?.Attribute("Name"); }
            catch { return null; }
        }

        static string? FindBx1Sysres(MapperConfig cfg, string syslayPath)
        {
            string? systemDir = null;
            foreach (var seed in new[] { Path.GetDirectoryName(syslayPath),
                                         Path.GetDirectoryName(cfg.SysresPath2) })
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
                    if (head.Contains("CoverPnp_Gripper") || head.Contains("CoverPNP_Hr"))
                        return sysres;
                }
                catch { /* ignore unreadable */ }
            }
            return null;
        }
    }
}
