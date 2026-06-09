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
    /// <para><b>Stage 1</b> — instantiates the <c>BX1_IO</c> (<c>PLC_RW_BX1</c>) composite
    /// (id <c>F6C04A4BA6FA8593</c>, the id the copied BX1 <c>.hcf</c> binds to) on the BX1
    /// resource so the EtherNet/IP word symlinks (<c>BX1_RES.BX1_IO.EIP_Input/Output_Word_1</c>)
    /// resolve.</para>
    ///
    /// <para><b>Stage 2</b> — the symlink bridge. The broker exposes the cover I/O as
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
    /// Modelled on <see cref="CodeGen.Services.SimulatorPostProcessor.InjectSimSwivelForce"/>.
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
            // Gripper: 1 physical sensor (gripped) -> atwork; no home sensor (handled in Stage 3).
            new CoverMap { Cover = "CoverPnp_Gripper", SensorFromHome = null,               SensorFromWork = "CoverPnpSensor",
                           Event = "CoverSensorEvent", CoilToHome = null,                   CoilToWork = "Cover_Gripper_Q" },
        };

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
                        if (InjectInto(path, isSysres, label, resourceName, report)) touched++;
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
            SystemInjector.BindingApplicationReport report)
        {
            var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            var net = FindCoverNetwork(doc);
            if (net == null) return false;
            var fileTag = isSysres ? "sysres" : "syslay";

            bool hasGripper = net.Elements(Ns + "FB")
                .Any(f => (string?)f.Attribute("Name") == "CoverPnp_Gripper");

            var ec = net.Element(Ns + "EventConnections") ?? AddSection(net, "EventConnections");
            var dc = net.Element(Ns + "DataConnections")  ?? AddSection(net, "DataConnections");

            // --- Stage 1: the broker FB (forced id so the copied .hcf matches) ---
            AddFbIfAbsent(net, BrokerFbId, BrokerFbName, BrokerFbType, "Main", isSysres,
                isSysres ? 9500 : 33000, 5800, ifaceParams: null, name1: null, name2: null);
            if (hasGripper)
                AddEvent(ec, "CoverPnp_Gripper.INITO", $"{BrokerFbName}.INIT");

            // --- Stage 2: the symlink bridge + scan cycle ---
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
            // Output write — ROBUST, NOT a serial chain. Each coil DST reads on EO
            // INDEPENDENTLY, so one cover's not-yet-published coil symlink (e.g. an idle
            // gripper) can't block the others. The output-word WRITE (REQ_INT_BOOL) fires
            // off CoverPNP_Vr's read-confirm — Vr reliably publishes + is read every scan
            // (proven live: BX1IO_Coil_CoverPNP_Vr.VALUE2 tracks the forced coil) — so the
            // broker packs the freshly-read coil values and writes the output word every
            // cycle. (The old serial chain gated the write on the GRIPPER's CNF at the tail,
            // which never fired while the gripper was idle → no write for ANY cover.)
            foreach (var c in Covers)
                AddEvent(ec, $"{ScanFbName}.EO", $"BX1IO_Coil_{c.Cover}.REQ");
            AddEvent(ec, "BX1IO_Coil_CoverPNP_Vr.CNF", $"{BrokerFbName}.REQ_INT_BOOL");

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
