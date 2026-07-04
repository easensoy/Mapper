using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Translation;

namespace CodeGen.Devices.BX1
{
    /// <summary>
    /// BX1 EtherNet/IP cover-I/O broker injection.
    ///
    /// <para><b>Broker instance</b> — instantiates the <c>BX1_IO</c> (<c>PLC_RW_BX1</c>) composite
    /// (id <c>F6C04A4BA6FA8593</c>, the id the copied BX1 <c>.hcf</c> binds to) on the BX1
    /// resource so the EtherNet/IP word symlinks (<c>BX1_RES.BX1_IO.EIP_Input/Output_Word_1</c>)
    /// resolve.</para>
    ///
    /// <para><b>Symlink bridge</b> — the broker exposes the cover I/O as
    /// top-level FB ports (sensor OutputVars <c>CoverPnp*AtWork/AtHome</c>/<c>CoverPnpSensor</c>;
    /// coil InputVars <c>Cover_Pnp_*</c>/<c>Cover_Gripper_Q</c>; change events
    /// <c>CoverPnp*Event</c>). Our ring-model covers read/write their I/O via symlinks
    /// (<c>BX1_RES.&lt;cover&gt;.athome/atwork</c> in, <c>.OutputToWork/OutputToHome</c> out) — so
    /// we inject per-cover bridge FBs:
    /// <list type="bullet">
    ///   <item>a 2-BOOL SYMLINKMULTIVARSRC that PUBLISHES the cover's sensor symlinks from the
    ///         broker's sensor outputs (fired by the broker's per-cover change event);</item>
    ///   <item>a 2-BOOL SYMLINKMULTIVARDST that READS the cover's coil symlinks into the broker's
    ///         coil inputs (fired by the scan cycle).</item>
    /// </list>
    /// An <c>E_DELAY</c> self-loop (<c>BX1_IO_Cycle</c>) drives the scan: every period it triggers
    /// the broker read (<c>REQ</c> → sensors republished) and the coil-read chain (→ broker
    /// <c>REQ_INT_BOOL</c> → output word written). No cover CAT changes — they already expose the
    /// symlinks the bridge targets.</para>
    ///
    /// Bit map (from <c>PLC_RW_BX1</c>): IN bit0=Hr athome,1=Hr atwork,2=Vr athome,3=Vr atwork,
    /// 5=gripper; OUT bit0=Hr OutputToWork,1=Hr OutputToHome,2=Vr OutputToWork,3=gripper.
    /// Modelled on the symlink-bridge pattern (formerly the simulator swivel-force injector, removed).
    /// Gated by <c>cfg.DeployBx1IoBroker</c>. Idempotent.
    /// </summary>
    public static class Bx1IoBrokerInjector
    {
        static readonly XNamespace Ns = "https://www.se.com/LibraryElements";

        public const string BrokerFbId = "F6C04A4BA6FA8593";
        public const string BrokerFbName = "BX1_IO";
        public const string BrokerFbType = "PLC_RW_BX1";

        // 2-BOOL symlink generics our cover CATs already use (confirmed present in the project).
        const string Sym2BoolSrc = "SYMLINKMULTIVARSRC_277E97BEC1451D2C";
        const string Sym2BoolDst = "SYMLINKMULTIVARDST_277E97BEC1451D2C";
        const string TwoBoolIfaceParams = "Runtime.System#I:=2;VALUE${I}:BOOL,BOOL";
        const string ScanFbName = "BX1_IO_Cycle";
        const string ScanPeriod = "T#50ms";

        sealed class CoverMap
        {
            public string Cover = "";
            public string? SensorFromHome;   // broker OutputVar -> SRC.VALUE1 (athome)
            public string? SensorFromWork;   // broker OutputVar -> SRC.VALUE2 (atwork)
            public string Event = "";        // broker change event -> SRC.REQ
            public string? CoilToHome;       // DST.VALUE1 (OutputToHome) -> broker InputVar
            public string? CoilToWork;       // DST.VALUE2 (OutputToWork) -> broker InputVar
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

        // ── Internalized cover-I/O broker (cfg.Bx1BridgeInsideComposite) ──────────────────
        // MINIMAL, data-driven. The whole design is these two ordered tables: index = VALUE
        // index = NAME index = wiring order. Bit positions match the PLC_RW_BX1 WordToBits/
        // BitsToWord core (fixed, verified vs the .fbt): IN bit0=Hr athome,1=Hr atwork,2=Vr
        // athome,3=Vr atwork,5=gripper atwork; OUT bit0=Hr OutputToWork,1=Hr OutputToHome,
        // 2=Vr OutputToWork,3=gripper OutputToWork.
        static readonly (string Sym, int Bit)[] CoverSensors =
        {
            ("CoverPNP_Hr.athome", 0), ("CoverPNP_Hr.atwork", 1),
            ("CoverPNP_Vr.athome", 2), ("CoverPNP_Vr.atwork", 3),
            ("CoverPnp_Gripper.atwork", 5),     // gripper has no home sensor — none invented
        };
        static readonly (string Sym, int Bit)[] CoverCoils =
        {
            ("CoverPNP_Hr.OutputToWork", 0), ("CoverPNP_Hr.OutputToHome", 1),
            ("CoverPNP_Vr.OutputToWork", 2), ("CoverPnp_Gripper.OutputToWork", 3),
        };

        // EAE compiler-generates SYMLINKMULTIVAR{SRC,DST}_<hash> per BOOL arity; the hash is
        // GUI-computed (NOT derivable — confirmed: not crc64/fnv/md5/sha). Only these arities
        // exist in EAE's generated set, so a publisher/subscriber must use the smallest
        // available arity >= its value count (surplus VALUEs are inert). SRC and DST of the
        // same arity share the hash. 5-BOOL does not exist -> the 5-sensor publisher uses 7.
        static readonly (int Arity, string Hash)[] BoolSymlinkTypes =
        {
            (1, "1559B0FF8170C9BA0"), (2, "277E97BEC1451D2C"), (3, "151ACB50A2F8223B2"),
            (4, "19628BFC3C74F1AB1"), (7, "238520AAD20108C65"), (10, "2183AAEC3B58E76C9"),
            (15, "2217C9CA39686140D"),
        };

        /// <summary>
        /// Transforms the deployed <c>PLC_RW_BX1.fbt</c> into the INTERNALIZED broker, MINIMAL form:
        /// ONE <c>CoverSensorPublisher</c> (SYMLINKMULTIVARSRC) publishing ALL cover athome/atwork
        /// symbols + ONE <c>CoverCoilSubscriber</c> (SYMLINKMULTIVARDST) consuming ALL cover
        /// OutputToWork/Home symbols + one <c>ScanCycle</c> (E_DELAY) — NO per-cover FBs, NO serial
        /// coil chain (the subscriber's own CNF packs the word, independent of Hr/Gripper). VALUEs
        /// map straight onto the existing WordToBits/BitsToWord bits; the superseded
        /// cover-InputVar→bit connections are removed. New FBs are inserted BEFORE Input/Output/
        /// connections (EAE FBNetwork ordering). Idempotent; upgrades a prior per-cover embed by
        /// sweeping its Sense_*/Coil_* FBs first. EIP word symlinks + the .hcf binding untouched.
        /// BX1-only. The single EAE-runtime unknown: do ABSOLUTE cross-instance symlink names
        /// resolve from inside a composite (validated tomorrow via the watch points).
        /// </summary>
        public static void EmbedCoverBridgeInComposite(string fbtPath, string resourceName = "BX1_RES")
        {
            if (!File.Exists(fbtPath)) return;
            // No PreserveWhitespace: Save re-indents the whole file so every FB lands on its own
            // line (never "stacked on one XML line"). EAE renders by the x/y below regardless.
            var doc = XDocument.Load(fbtPath);
            var net = doc.Root?.Element("FBNetwork");
            if (net == null) return;
            // Already embedded? Re-apply the LAYOUT only and stop. ExtractToEae is copy-if-absent,
            // so a re-deploy never re-copies the pristine base over the live .fbt — re-laying-out
            // here is the only way an already-embedded file picks up layout/spacing changes.
            if (net.Elements("FB").Any(f => (string?)f.Attribute("Name") == "CoverSensorPublisher"))
            { ApplyBrokerLayout(net); doc.Save(fbtPath); return; }

            var ec = net.Element("EventConnections");
            var dc = net.Element("DataConnections");
            if (ec == null || dc == null) return;

            // Upgrade path: sweep any prior PER-COVER embed (Sense_*/Coil_*/ScanCycle) + its wires
            // so re-deploying onto an old embed cleanly replaces it with the consolidated broker.
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

            // ONE sensor publisher (smallest available arity >= sensor count; surplus = inert).
            var (sArity, sType) = Pick("SRC", CoverSensors.Length);
            var sNames = new string[sArity];
            for (int i = 0; i < sArity; i++)
                sNames[i] = i < CoverSensors.Length
                    ? $"{resourceName}.{CoverSensors[i].Sym}"
                    : $"{resourceName}.{BrokerFbName}.CoverSensorSpare{i + 1}";
            AddFb("CoverSensorPublisher", sType, sArity, sNames, 3000, 700);

            // ONE coil subscriber (4 coils -> exact 4-BOOL DST).
            var (cArity, cType) = Pick("DST", CoverCoils.Length);
            var cNames = new string[cArity];
            for (int i = 0; i < cArity; i++)
                cNames[i] = i < CoverCoils.Length
                    ? $"{resourceName}.{CoverCoils[i].Sym}"
                    : $"{resourceName}.{BrokerFbName}.CoverCoilSpare{i + 1}";
            AddFb("CoverCoilSubscriber", cType, cArity, cNames, 5000, 700);

            // ONE scan tick (ScanCycle, E_DELAY @ ScanPeriod). This is NOT general PLC polling —
            // it is a contained BX1 symbolic-link REFRESH heartbeat, structurally required by the
            // universal Five_State_Actuator_CAT: that CAT publishes each cover coil via an INTERNAL
            // SYMLINKMULTIVARSRC (its 'Output' FB) that writes the symbol ONLY when its own REQ
            // fires (a coil-change event) and exposes NO public coil pin/event on its boundary
            // (InterfaceList has no OutputVars; EventOutputs = INITO only). A symlink carries a
            // VALUE, not an event, so CoverCoilSubscriber receives no notification when a coil
            // changes — it must be REQ'd each cycle to re-sample the registry and re-pack the
            // output word. Without this tick the EtherNet/IP output word freezes at 16#0000 and the
            // cover never actuates. (The SE reference avoids the poll only by using a different
            // broker-MODEL cover CAT with public coil pins — adopting that means editing the CAT,
            // which is out of scope; so polling is mandatory for the symlink-only design.)
            var scan = new XElement("FB",
                new XAttribute("ID", nextId++), new XAttribute("Name", "ScanCycle"),
                new XAttribute("Type", "E_DELAY"), new XAttribute("x", "700"),
                new XAttribute("y", "1400"), new XAttribute("Namespace", "IEC61499.Standard"),
                new XElement("Parameter", new XAttribute("Name", "DT"), new XAttribute("Value", ScanPeriod)));
            if (firstInput != null) firstInput.AddBeforeSelf(scan); else net.Add(scan);

            // Events — INIT registers both symlinks + starts the tick; the tick reads input word
            // AND coils in PARALLEL; sensors publish after WordToBits; the subscriber's OWN CNF
            // packs the output word (no per-cover CNF dependency — single FB, single confirm).
            Ev("INIT", "CoverSensorPublisher.INIT");
            Ev("INIT", "CoverCoilSubscriber.INIT");
            Ev("INIT", "ScanCycle.START");
            Ev("ScanCycle.EO", "ScanCycle.START");
            Ev("ScanCycle.EO", "EIP_Input_Word.REQ");
            Ev("ScanCycle.EO", "CoverCoilSubscriber.REQ");
            Ev("EIPInputs_Bool.CNF", "CoverSensorPublisher.REQ");
            Ev("CoverCoilSubscriber.CNF", "EIPOutput_Bits.REQ");

            // Data — input bits -> publisher VALUEs; subscriber VALUEs -> output bits.
            for (int i = 0; i < CoverSensors.Length; i++)
                Da($"EIPInputs_Bool.bit{CoverSensors[i].Bit}", $"CoverSensorPublisher.VALUE{i + 1}");
            for (int i = 0; i < CoverCoils.Length; i++)
                Da($"CoverCoilSubscriber.VALUE{i + 1}", $"EIPOutput_Bits.bit{CoverCoils[i].Bit}");

            // Remove the superseded cover-InputVar -> BitsToWord bit connections (output bits now
            // come from the subscriber; leaving both = two data sources per bit = an EAE error).
            var inputVarSources = new[] { "Cover_Pnp_Hr_ToWork", "Cover_Pnp_Hr_ToHome", "Cover_Pnp_Vr_Q", "Cover_Gripper_Q" };
            foreach (var conn in dc.Elements("Connection").Where(c =>
                         ((string?)c.Attribute("Destination"))?.StartsWith("EIPOutput_Bits.bit") == true &&
                         inputVarSources.Contains((string?)c.Attribute("Source"))).ToList())
                conn.Remove();

            idc?.SetAttributeValue("Value", nextId.ToString());
            ApplyBrokerLayout(net);
            doc.Save(fbtPath);
        }

        /// <summary>
        /// SAFETY (CoverPNP_Hr &lt;-&gt; Bearing_PnP swivel collision). Inserts a <c>Bx1CoverFailsafe</c>
        /// gate into the deployed <c>PLC_RW_BX1</c> broker, between the cover coil sources and the
        /// EtherNet/IP output-word bits. On INIT / cold / warm start the gate forces CoverPNP_Hr to
        /// HOME (bit0 <c>ToWork</c>=0, bit1 <c>ToHome</c>=1) and the Vr/gripper coils off, and HOLDS
        /// that until the Hr at-home sensor (input word bit0) reads TRUE — then it passes the live
        /// cover coils through. So while the BX1 logic RUNS the broker can NEVER drive cover_hr to Work
        /// on deploy/login/restart, and it actively RETRACTS the cover home if it was left at Work (the
        /// double-acting Hr cylinder needs ToHome=1 to return; both coils 0 only holds it). NOTE: this
        /// gate fires only on INIT and on the E_DELAY scan REQ — only while the logic is ALIVE. It does
        /// NOT cover EAE Clean/STOP/fault (logic torn down, the scan stops, the output word freezes).
        /// Homing the cover while STOPPED requires the TM3BC coupler output fallback (TM3DQ16T ToHome
        /// channel -&gt; 1 = fallback word 16#0002), set on the coupler's OWN embedded web server
        /// (192.168.1.210), which the Mapper cannot emit. Keys on the
        /// <c>EIPOutput_Bits.bit0-3</c> / <c>.REQ</c> wiring, so it patches both the external-bridge and
        /// internalized broker forms. Idempotent. Returns true if it inserted the gate. BX1-only,
        /// gated by <c>cfg.Bx1CoverSafeStart</c>.
        /// </summary>
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

            // Reroute the source feeding each output bit THROUGH the gate. Key on the bit
            // DESTINATION so it works whether the source is the broker InputVar (external bridge)
            // or CoverCoilSubscriber.VALUE (internalized).
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

            // Hr at-home feedback = EIPInputs_Bool.bit0 (verified in PLC_RW_BX1).
            dc.Add(new XElement("Connection", new XAttribute("Source", "EIPInputs_Bool.bit0"),
                new XAttribute("Destination", "CoverFailsafe.AtHome")));

            // Whatever triggered the output-word write now triggers the gate first; the gate's CNF
            // writes the word. INIT arms the safe-start (force home) before the first scan.
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

        /// <summary>
        /// Lays the embedded broker FBs + interface pins out in clean left-to-right columns with
        /// generous spacing so EAE renders them with no FB/pin/label/wire overlap. Deterministic,
        /// safe to re-run on every (re)deploy. BX1-only.
        /// </summary>
        static void ApplyBrokerLayout(XElement net)
        {
            // EAE renders by these x/y. Compact left-to-right columns ~1200-2100 apart — still
            // enough that the wide symlink NAME labels clear the next FB — with the two tall
            // symlink FBs sharing the mid-right column ~1600 apart vertically. ScanCycle stands
            // alone far-left; FB2 drops to the lower row under WordToBits; all Input pins pin to
            // x=0, all Output pins to the far-right edge (x=9400).
            var fbXY = new Dictionary<string, (int x, int y)>
            {
                ["ScanCycle"]            = (400, 300),     // far-left: the tick
                ["EIP_Input_Word"]       = (2100, 300),    // upper/mid-left: word symlink in
                ["EIPInputs_Bool"]       = (3300, 300),    // mid: WordToBits
                ["CoverSensorPublisher"] = (5400, 300),    // mid-right upper: sensor SRC
                ["EIPOutput_Bits"]       = (6700, 300),    // right: BitsToWord
                ["EIP_Output_Word"]      = (8000, 300),    // far-right: word symlink out
                ["CoverCoilSubscriber"]  = (5400, 1900),   // mid-right lower: coil DST
                ["FB2"]                  = (3300, 1900),    // lower row: changeEventM262_2
            };
            foreach (var fb in net.Elements("FB"))
                if (fbXY.TryGetValue((string?)fb.Attribute("Name") ?? "", out var p))
                {
                    fb.SetAttributeValue("x", p.x.ToString());
                    fb.SetAttributeValue("y", p.y.ToString());
                }
            int py = 300;   // Input pins: far-left edge, generous vertical gaps.
            foreach (var pin in net.Elements("Input"))
            { pin.SetAttributeValue("x", "0"); pin.SetAttributeValue("y", py.ToString()); py += 450; }
            py = 300;       // Output pins: far-right edge, clear of every FB.
            foreach (var pin in net.Elements("Output"))
            { pin.SetAttributeValue("x", "9400"); pin.SetAttributeValue("y", py.ToString()); py += 450; }
        }

        /// <summary>
        /// Injects the BX1_IO broker + the cover symlink bridge into the BX1 SubApp (syslay)
        /// and the BX1 sysres. Returns the number of files touched. Idempotent.
        /// </summary>
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
                        // The deployed .sysres/.syslay is LOCKED — almost always because
                        // EAE has the resource (BX1_RES) open in its Watch / resource view
                        // while Test Runtime runs. The syslay write can succeed and the
                        // sysres write silently fail, leaving the broker without its cover
                        // bridge. Surface it loudly with the fix.
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

            // --- The broker FB (forced id so the copied .hcf matches) ---
            // Syslay X aligned to the BX1 registry col-3 position (ColumnBaseX 26000 + 3*2000)
            // so the broker sits right after the (tightened) cover columns, not 3000 east of them.
            AddFbIfAbsent(net, BrokerFbId, BrokerFbName, BrokerFbType, "Main", isSysres,
                isSysres ? 9500 : 32000, 5800, ifaceParams: null, name1: null, name2: null);
            if (hasGripper)
                AddEvent(ec, "CoverPnp_Gripper.INITO", $"{BrokerFbName}.INIT");

            // INTERNALIZED (cfg.Bx1BridgeInsideComposite, default): the per-cover symlink
            // bridge + the scan cycle live INSIDE the PLC_RW_BX1 composite
            // (EmbedCoverBridgeInComposite, driven by CoverIoBits). The generated resource
            // carries ONLY BX1_IO — emit none of the external bridge here. Acceptance:
            // no BX1IO_Sense_*/BX1IO_Coil_*/BX1_IO_Cycle in the sysres/syslay.
            if (internalized)
            {
                // Sweep any external bridge FBs/connections a prior external-path deploy may
                // have left in this file, so the resource ends with ONLY BX1_IO (acceptance:
                // no BX1IO_Sense_*/BX1IO_Coil_*/BX1_IO_Cycle). BX1_IO + its INIT are kept.
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

            // --- The EXTERNAL symlink bridge + scan cycle (default when Bx1BridgeInsideComposite=false) ---
            int xSrc = isSysres ? 11000 : 35000;
            int xDst = isSysres ? 13000 : 37000;
            int slot = 0;
            foreach (var c in Covers)
            {
                // Sensor publisher: broker sensor outputs -> publish BX1_RES.<cover>.athome/atwork.
                var srcName = $"BX1IO_Sense_{c.Cover}";
                AddFbIfAbsent(net, Hex16($"{srcName}|{fileTag}"), srcName, Sym2BoolSrc, "Main", isSysres,
                    xSrc, 1500 + slot * 500, TwoBoolIfaceParams,
                    $"'{resourceName}.{c.Cover}.athome'", $"'{resourceName}.{c.Cover}.atwork'");
                AddEvent(ec, $"{BrokerFbName}.{c.Event}", $"{srcName}.REQ");
                if (c.SensorFromHome != null) AddData(dc, $"{BrokerFbName}.{c.SensorFromHome}", $"{srcName}.VALUE1");
                if (c.SensorFromWork != null) AddData(dc, $"{BrokerFbName}.{c.SensorFromWork}", $"{srcName}.VALUE2");

                // Coil reader: read BX1_RES.<cover>.OutputToHome/OutputToWork -> broker coil inputs.
                var dstName = $"BX1IO_Coil_{c.Cover}";
                AddFbIfAbsent(net, Hex16($"{dstName}|{fileTag}"), dstName, Sym2BoolDst, "Main", isSysres,
                    xDst, 1500 + slot * 500, TwoBoolIfaceParams,
                    $"'{resourceName}.{c.Cover}.OutputToHome'", $"'{resourceName}.{c.Cover}.OutputToWork'");
                if (c.CoilToHome != null) AddData(dc, $"{dstName}.VALUE1", $"{BrokerFbName}.{c.CoilToHome}");
                if (c.CoilToWork != null) AddData(dc, $"{dstName}.VALUE2", $"{BrokerFbName}.{c.CoilToWork}");

                // INIT the bridge symlink FBs. A SYMLINKMULTIVAR{SRC,DST} only registers
                // its symlink + samples QI=TRUE when its INIT fires; without INIT it stays
                // DISABLED (EAE Watch shows the type-default $${PATH}/QI=FALSE, INIT=0,
                // CNF=0, VALUE=FALSE) and ignores every REQ the scan sends. The broker is
                // INIT'd (line above); these were not — that's why the forced coil never
                // reached the output word. Fan from CoverPnp_Gripper.INITO (same root that
                // INITs the broker + kicks the scan), so every bridge FB is registered
                // before the E_DELAY's first EO arrives.
                if (hasGripper)
                {
                    AddEvent(ec, "CoverPnp_Gripper.INITO", $"{srcName}.INIT");
                    AddEvent(ec, "CoverPnp_Gripper.INITO", $"{dstName}.INIT");
                }
                slot++;
            }

            // Scan cycle: E_DELAY self-loop kicked by the broker's post-init PLC_EVENT.
            AddFbIfAbsent(net, Hex16($"{ScanFbName}|{fileTag}"), ScanFbName, "E_DELAY", "IEC61499.Standard",
                isSysres, isSysres ? 15000 : 39000, 1300, ifaceParams: null, name1: null, name2: null,
                extraParams: new[] { ("DT", ScanPeriod) });
            // Kick the scan from a RELIABLE event. The broker's PLC_EVENT depends on its
            // internal changeEventM262_2 firing INITO, which does not fire on a plain INIT —
            // so it never kicks the cycle. CoverPnp_Gripper.INITO is the event that ALSO
            // drives BX1_IO.INIT (it's the tail of the init chain), so the broker is
            // initialised in the same scan; the E_DELAY's first EO (after DT) then fires
            // with the broker ready. Keep PLC_EVENT too as a harmless redundant kick.
            if (hasGripper)
                AddEvent(ec, "CoverPnp_Gripper.INITO", $"{ScanFbName}.START");
            AddEvent(ec, $"{BrokerFbName}.PLC_EVENT", $"{ScanFbName}.START"); // redundant kick (if it fires)
            AddEvent(ec, $"{ScanFbName}.EO", $"{ScanFbName}.START");          // self re-arm
            AddEvent(ec, $"{ScanFbName}.EO", $"{BrokerFbName}.REQ");          // read input word -> sensors
            // Output write — CAUSAL CHAIN over the home-critical (non-gripper) cover coil readers so
            // the output-word WRITE can NEVER fire before CoverPNP_Hr's ToHome/ToWork are freshly read.
            // The old PARALLEL fan-out (EO -> every coil .REQ; write on Vr.CNF) RACED: the write could
            // run before Hr.REQ had refreshed BX1IO_Coil_CoverPNP_Hr.VALUE1/2, leaving Cover_Pnp_Hr_ToHome
            // STALE so EIPOutput_Bits.bit1 never carried the home command — cover_hr did not go home
            // (while the M580 direct-I/O Shaft_Hr did). Fix: chain
            //   EO -> Hr.REQ -> (Hr.CNF) -> Vr.REQ -> (Vr.CNF) -> BX1_IO.REQ_INT_BOOL
            // so every coil in the write path is provably fresh before the word is packed. The gripper
            // is refreshed in PARALLEL off EO and is NEVER a write trigger — an idle gripper can't block
            // the write (the failure mode of the original serial chain that gated the write on the
            // gripper's tail CNF). Idempotent: drop any prior coil-read/write triggers first so a
            // re-deploy onto an already-wired sysres ends with EXACTLY this chain (AddEvent only
            // de-dupes identical pairs, it never prunes the now-orphaned parallel EO->Vr.REQ).
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

        /// <summary>Saves with a few short retries so a transient lock (EAE briefly
        /// touching the file) doesn't drop the write. Re-throws IOException if still
        /// locked after the retries so the caller can tell the user to close EAE.</summary>
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
