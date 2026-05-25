using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.M262;
using CodeGen.Translation;

namespace CodeGen.Devices.Core
{
    /// <summary>
    /// Binds the deployed Station-2 <c>.hcf</c> hardware-config channel symlinks so
    /// EAE's "Symbolic Link" view resolves them. This is the M580/BX1 sibling of the
    /// M262 <see cref="HcfPatchService"/> and follows the SAME proven scheme:
    /// <b>DIRECT binding</b> to the consumer CAT instance — each channel value is
    /// <c>{resourceId}.{consumerFbId}.{port}</c> (unquoted, GUID-headed), pointing
    /// straight at the actuator/sensor FB on the resource.
    ///
    /// <para><b>Why direct, not a broker.</b> The reference
    /// <c>SMC_Rig_Expo_withClamp</c> routes M580 I/O through a <c>PLC_RW_M580</c>
    /// broker FB ("M580IO") whose channels read <c>{resId}.{M580IO_id}.SwivelArmAtPick</c>.
    /// But this Mapper's actuator CATs do <b>direct</b> <c>$${PATH}</c> symlink I/O
    /// (Five_State_Actuator_CAT exposes <c>athome/atwork/OutputToWork</c>;
    /// Sensor_Bool_CAT exposes <c>Input</c>) exactly like the working M262 — they
    /// have no broker-facing ports, and no <c>PLC_RW_M580</c> FB is emitted. So the
    /// authored broker symlinks cannot resolve. Instead of bolting on a broker, we
    /// translate the reference channel name (the trailing segment of the authored
    /// value, e.g. <c>ClampAtWork</c>) to the matching consumer CAT port via
    /// <see cref="M580ChannelMap"/> and bind direct. The reference name carries the
    /// component+position, so the map is a fixed lookup, not a guess.</para>
    ///
    /// <para><b>BX1 is deferred</b> (see <see cref="BindBX1"/>): it is EtherNet/IP,
    /// so its <c>.hcf</c> carries <i>word</i> channels (<c>EIP_Input_Word_1</c>) that
    /// only a <c>PLC_RW_BX1</c> broker's internal <c>WordToBits</c> can unpack — a
    /// direct bit-level CAT cannot consume a word. That needs the broker design and
    /// is left untouched here by request.</para>
    ///
    /// <para>Idempotent: a channel already bound to a component FB id is left
    /// unchanged. Empty/literal channels (<c>''</c>, scanner ids, <c>T#…</c>) are
    /// never touched. Does NOT touch the M262 <see cref="HcfPatchService"/>.</para>
    /// </summary>
    public static class HcfSymbolBinder
    {
        /// <summary>
        /// Maps the authored M580 <c>.hcf</c> channel symlink name (the trailing
        /// segment of the value, e.g. <c>'RES0.M580IO.ClampAtWork'</c> -> key
        /// <c>ClampAtWork</c>) to the Control.xml component instance and the CAT
        /// port the channel binds to. Ports by CAT type:
        ///   • Five_State_Actuator_CAT (Clamp, Shaft_Hr/Vr, mechanical grippers):
        ///       <c>athome</c>/<c>atwork</c> (sensor symlinks), <c>OutputToWork</c>
        ///       (the single drive-coil symlink).
        ///   • Seven_State_Actuator_CAT (Bearing_PnP swivel): <c>athome</c>,
        ///       <c>atwork1</c>, <c>atwork2</c> (3 position sensors),
        ///       <c>current_state1_to_plc</c>/<c>current_state2_to_plc</c> (2 coils).
        ///   • Sensor_Bool_CAT (BearingSensor, ShaftSensor): <c>Input</c>.
        /// Grippers: "open" = home, "closed" = work.
        /// </summary>
        private static readonly Dictionary<string, (string Comp, string Port)> M580ChannelMap =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // Clamp (Five_State, single coil)
                ["ClampAtWork"]             = ("Clamp", "atwork"),
                ["ClampAtHome"]             = ("Clamp", "athome"),
                ["Clamp"]                   = ("Clamp", "OutputToWork"),
                // Shaft vertical / horizontal (Five_State)
                ["ShaftPnpVrAtWork"]        = ("Shaft_Vr", "atwork"),
                ["ShaftPnpVrAtHome"]        = ("Shaft_Vr", "athome"),
                ["Shaft_Vertical"]          = ("Shaft_Vr", "OutputToWork"),
                ["ShaftPnpHrAtWork"]        = ("Shaft_Hr", "atwork"),
                ["ShaftPnpHrAtHome"]        = ("Shaft_Hr", "athome"),
                ["Shaft_Horizontal"]        = ("Shaft_Hr", "OutputToWork"),
                // Mechanical grippers (Five_State; open = home, closed = work)
                ["Bearing_Gripper_Open"]    = ("Bearing_Gripper", "athome"),
                ["Bearing_Gripper_Closed"]  = ("Bearing_Gripper", "atwork"),
                ["Bearing_Gripper_Q"]       = ("Bearing_Gripper", "OutputToWork"),
                ["ShaftPnpGripperOpened"]   = ("Shaft_Gripper", "athome"),
                ["ShaftPnpGripperClosed"]   = ("Shaft_Gripper", "atwork"),
                ["Shaft_Gripper"]           = ("Shaft_Gripper", "OutputToWork"),
                // Sensors (Sensor_Bool_CAT)
                ["Bearing_At_Place_Sensor"] = ("BearingSensor", "Input"),
                ["ShaftPnpSensor"]          = ("ShaftSensor", "Input"),
                // Bearing_PnP swivel (Seven_State: 3 position sensors + 2 coils)
                ["SwivelArmAtHome"]         = ("Bearing_PnP", "athome"),
                ["SwivelArmAtPick"]         = ("Bearing_PnP", "atwork1"),
                ["SwivelArmAtPlace"]        = ("Bearing_PnP", "atwork2"),
                ["Swivel_Arm_Left_Q"]       = ("Bearing_PnP", "current_state1_to_plc"),
                ["Swivel_Arm_Right_Q"]      = ("Bearing_PnP", "current_state2_to_plc"),
            };

        /// <summary>Direct-bind the deployed M580 X80 <c>.hcf</c> channels to the
        /// consumer CAT ports. Tag <c>[HcfBind][M580]</c>.</summary>
        public static void BindM580(MapperConfig? config,
            SystemInjector.BindingApplicationReport report)
        {
            string Log(string m) { var s = $"[HcfBind][M580] {m}"; report.Missing.Add(s); return s; }
            if (config == null) { Log("skipped, no MapperConfig available"); return; }

            try
            {
                var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(config);
                if (string.IsNullOrEmpty(eaeRoot)) { Log("skipped, could not derive EAE project root"); return; }

                var sysdevFile = FindSysdevByType(eaeRoot, "M580_dPAC", "SE.DPAC");
                if (sysdevFile == null) { Log("skipped, no deployed M580 sysdev (Type=M580_dPAC)"); return; }

                var stem = Path.GetFileNameWithoutExtension(sysdevFile);
                var folder = Path.Combine(Path.GetDirectoryName(sysdevFile)!, stem);
                var hcfPath = Path.Combine(folder, stem + ".hcf");
                if (!File.Exists(hcfPath)) { Log($"skipped, deployed .hcf not found at {hcfPath} (run the HCF copier first)"); return; }

                var (resId, _) = ReadSysresIdentity(folder);
                if (string.IsNullOrEmpty(resId)) { Log("skipped, deployed sysres ID not resolvable"); return; }

                var compId = BuildComponentIdMap(folder);
                if (compId.Count == 0)
                {
                    Log("skipped, no actuator/sensor FBs on the M580 sysres yet " +
                        "(run the Station-2 FB mirror / EmitStation2Sysres first)");
                    return;
                }

                XDocument doc;
                try { doc = XDocument.Load(hcfPath); }
                catch (Exception ex) { Log($"skipped, .hcf parse failed: {ex.GetType().Name}: {ex.Message}"); return; }

                int bound = 0, already = 0, unmapped = 0, missingComp = 0, literals = 0;
                var compFbIds = new HashSet<string>(compId.Values, StringComparer.OrdinalIgnoreCase);

                foreach (var pv in doc.Descendants().Where(e => e.Name.LocalName == "ParameterValue"))
                {
                    var chan = (string?)pv.Attribute("Name") ?? string.Empty;
                    var raw = (string?)pv.Attribute("Value");
                    if (raw == null) continue;

                    if (!TrySplitSymlink(raw, out var _, out var mid, out var last))
                    {
                        literals++;            // empty / literal / not a head.mid.port triple
                        continue;
                    }

                    // Reference form: trailing segment is a known broker symlink name.
                    if (M580ChannelMap.TryGetValue(last, out var map))
                    {
                        if (!compId.TryGetValue(map.Comp, out var fbId))
                        {
                            missingComp++;
                            report.Missing.Add(
                                $"[HcfBind][M580] {chan}: '{last}' -> component '{map.Comp}' " +
                                "not present on the M580 resource — left as-is");
                            continue;
                        }
                        var boundVal = $"{resId}.{fbId}.{map.Port}";
                        if (!string.Equals(raw, boundVal, StringComparison.Ordinal))
                        {
                            pv.SetAttributeValue("Value", boundVal);
                            bound++;
                            report.Missing.Add(
                                $"[HcfBind][M580] {chan} = {boundVal}  ({map.Comp}.{map.Port}; was {raw})");
                        }
                        else already++;
                        report.HcfPinAssignments.Add((chan, boundVal));
                        continue;
                    }

                    // Already bound (middle segment is one of our component FB ids).
                    if (compFbIds.Contains(mid)) { already++; continue; }

                    unmapped++;
                    report.Missing.Add(
                        $"[HcfBind][M580] {chan}: symlink '{last}' not in the M580 channel map — left as-is");
                }

                if (bound > 0) SaveHcf(doc, hcfPath);
                Log($"direct-bound {bound} channel(s) to CAT ports (resId {resId}); {already} already bound, " +
                    $"{unmapped} unmapped, {missingComp} missing-component, {literals} literal/empty. " +
                    "No broker — each channel points straight at the actuator/sensor FB (M262-style).");
            }
            catch (Exception ex)
            {
                Log($"failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// BX1 is intentionally deferred. Its EtherNet/IP <c>.hcf</c> carries
        /// word-level channels (<c>EIP_Input_Word_1</c>) that only a
        /// <c>PLC_RW_BX1</c> broker's internal <c>WordToBits</c>/<c>BitsToWord</c>
        /// can unpack; the Mapper's direct bit-level CATs (athome/atwork) cannot
        /// consume a word. Binding it correctly needs the broker design, so this
        /// only records the deferral and leaves the BX1 <c>.hcf</c> untouched.
        /// Tag <c>[HcfBind][BX1]</c>.
        /// </summary>
        public static void BindBX1(MapperConfig? config,
            SystemInjector.BindingApplicationReport report)
        {
            report.Missing.Add(
                "[HcfBind][BX1] deferred — BX1 is EtherNet/IP (word channels EIP_Input_Word_1/" +
                "EIP_Output_Word_1). Resolving them needs a PLC_RW_BX1 broker FB on the BX1 " +
                "resource to unpack word->bits (WordToBits); the direct bit-level CATs cannot " +
                "consume a word. Left the BX1 .hcf untouched pending the broker decision.");
        }

        // ── helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Splits a channel value into head.mid.last. Strips a single layer of
        /// wrapping single quotes first. Returns false for empty values, genuine
        /// string literals (no dots / quoted scanner ids), and <c>T#…</c> durations.
        /// </summary>
        private static bool TrySplitSymlink(string raw, out string head, out string mid, out string last)
        {
            head = mid = last = string.Empty;
            var t = raw.Trim();
            if (t.Length == 0) return false;
            bool quoted = t.Length >= 2 && t[0] == '\'' && t[^1] == '\'';
            var inner = quoted ? t.Substring(1, t.Length - 2).Trim() : t;
            if (inner.Length == 0) return false;
            if (inner.StartsWith("T#", StringComparison.OrdinalIgnoreCase)) return false;
            var parts = inner.Split('.');
            if (parts.Length != 3) return false;
            if (parts.Any(p => p.Length == 0)) return false;
            head = parts[0]; mid = parts[1]; last = parts[2];
            return true;
        }

        /// <summary>component instance Name -> FB id, read from the deployed
        /// <c>.sysres</c> in the sysdev folder (the actuator/sensor CATs mirrored
        /// there). The .hcf channel's middle segment must be this id so EAE
        /// resolves the link to the FB instance.</summary>
        private static Dictionary<string, string> BuildComponentIdMap(string sysdevFolder)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var sysres = Directory.Exists(sysdevFolder)
                    ? Directory.EnumerateFiles(sysdevFolder, "*.sysres").FirstOrDefault()
                    : null;
                if (sysres == null) return map;
                var root = XDocument.Load(sysres).Root;
                if (root == null) return map;
                XNamespace ns = root.GetDefaultNamespace();
                var net = root.Element(ns + "FBNetwork");
                if (net == null) return map;
                foreach (var fb in net.Elements(ns + "FB"))
                {
                    var n = (string?)fb.Attribute("Name");
                    var id = (string?)fb.Attribute("ID");
                    if (!string.IsNullOrEmpty(n) && !string.IsNullOrEmpty(id))
                        map[n!] = id!;
                }
            }
            catch { /* best-effort */ }
            return map;
        }

        private static string? FindSysdevByType(string eaeRoot, string deviceType, string deviceNamespace)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return null;
            foreach (var sd in Directory.EnumerateFiles(systemDir, "*.sysdev", SearchOption.AllDirectories))
            {
                try
                {
                    var root = XDocument.Load(sd).Root;
                    if (root == null || root.Name.LocalName != "Device") continue;
                    if ((string?)root.Attribute("Type") == deviceType &&
                        (string?)root.Attribute("Namespace") == deviceNamespace)
                        return sd;
                }
                catch { /* skip malformed */ }
            }
            return null;
        }

        /// <summary>Read the deployed resource ID (prefers the .sysres root
        /// <c>ID</c> attribute, falls back to the file stem) and Name.</summary>
        private static (string Id, string? Name) ReadSysresIdentity(string sysdevFolder)
        {
            try
            {
                var sysres = Directory.Exists(sysdevFolder)
                    ? Directory.EnumerateFiles(sysdevFolder, "*.sysres").FirstOrDefault()
                    : null;
                if (sysres == null) return (string.Empty, null);
                string id = Path.GetFileNameWithoutExtension(sysres);
                string? name = null;
                try
                {
                    var root = XDocument.Load(sysres).Root;
                    var rootId = (string?)root?.Attribute("ID");
                    if (!string.IsNullOrWhiteSpace(rootId)) id = rootId!;
                    name = (string?)root?.Attribute("Name");
                }
                catch { /* fall back to file stem */ }
                return (id, name);
            }
            catch { return (string.Empty, null); }
        }

        /// <summary>Save with UTF-8 + BOM and 2-space indent, retrying if EAE
        /// briefly holds a write lock.</summary>
        private static void SaveHcf(XDocument doc, string hcfPath)
        {
            var settings = new System.Xml.XmlWriterSettings
            {
                OmitXmlDeclaration = false,
                Indent = true,
                Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            };
            const int MaxAttempts = 8;
            int delayMs = 50;
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    using var fs = new FileStream(hcfPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                    using var w = System.Xml.XmlWriter.Create(fs, settings);
                    doc.Save(w);
                    return;
                }
                catch (IOException) when (attempt < MaxAttempts)
                {
                    System.Threading.Thread.Sleep(delayMs);
                    delayMs = Math.Min(delayMs * 2, 800);
                }
                catch (UnauthorizedAccessException) when (attempt < MaxAttempts)
                {
                    System.Threading.Thread.Sleep(delayMs);
                    delayMs = Math.Min(delayMs * 2, 800);
                }
            }
        }
    }
}
