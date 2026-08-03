using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.Core;
using CodeGen.Devices.M262;
using CodeGen.Mapping;
using CodeGen.Translation;

namespace CodeGen.Devices.M580
{
    // Binds the deployed M580 .hcf channel symlinks so EAE's Symbolic Link view resolves them
    // (M580 sibling of the M262 HcfPatchService). DIRECT binding to the consumer CAT instance:
    // each channel value is {resourceId}.{consumerFbId}.{port} (unquoted, GUID-headed). The CATs do
    // direct $${PATH} symlink I/O (no PLC_RW_M580 broker FB is emitted), so the authored broker
    // symlink name (trailing segment, e.g. ClampAtWork) is translated to the CAT port via
    // M580ChannelMap and bound direct. Idempotent; literal/empty channels ('', scanner ids, T#…)
    // are never touched.
    public static class M580SymbolBinder
    {
        // Maps the authored M580 .hcf channel symlink name (trailing segment, e.g. ClampAtWork) to
        // the Control.xml component + the CAT port it binds. Grippers: "open" = home, "closed" = work.
        private static readonly Dictionary<string, (string Comp, string Port)> M580ChannelMap =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["ClampAtWork"]             = ("Clamp", "atwork"),
                ["ClampAtHome"]             = ("Clamp", "athome"),
                ["Clamp"]                   = ("Clamp", "OutputToWork"),
                ["ShaftPnpVrAtWork"]        = ("Shaft_Vr", "atwork"),
                ["ShaftPnpVrAtHome"]        = ("Shaft_Vr", "athome"),
                ["Shaft_Vertical"]          = ("Shaft_Vr", "OutputToWork"),
                ["ShaftPnpHrAtWork"]        = ("Shaft_Hr", "atwork"),
                ["ShaftPnpHrAtHome"]        = ("Shaft_Hr", "athome"),
                ["Shaft_Horizontal"]        = ("Shaft_Hr", "OutputToWork"),
                ["Bearing_Gripper_Open"]    = ("Bearing_Gripper", "athome"),
                ["Bearing_Gripper_Closed"]  = ("Bearing_Gripper", "atwork"),
                ["Bearing_Gripper_Q"]       = ("Bearing_Gripper", "OutputToWork"),
                ["ShaftPnpGripperOpened"]   = ("Shaft_Gripper", "athome"),
                ["ShaftPnpGripperClosed"]   = ("Shaft_Gripper", "atwork"),
                ["Shaft_Gripper"]           = ("Shaft_Gripper", "OutputToWork"),
                ["Bearing_At_Place_Sensor"] = ("BearingSensor", "Input"),
                ["ShaftPnpSensor"]          = ("ShaftSensor", "Input"),
                // Bearing_PnP swivel channels are added by the static ctor below.
            };

        // The swivel's channel map depends on which CAT the twin's own state graph selected for it, so it is
        // resolved per generation from the deployed FB Type rather than fixed in a static ctor. A Port of ""
        // means "this physical channel has no counterpart on the selected CAT" and is blanked, exactly as a
        // channel whose component is absent — a dangling symbolic value fails the EAE compile.
        private static Dictionary<string, (string Comp, string Port)> SwivelChannels(string? catType)
        {
            var m = new Dictionary<string, (string Comp, string Port)>(StringComparer.OrdinalIgnoreCase);
            if (string.Equals(catType, TemplateMap.SevenStateCentreHomeCat, StringComparison.OrdinalIgnoreCase))
            {
                // Centre-home swivel: Home + Work1 + Work2 are three distinct physical stops. Sensor symlinks
                // athome / atwork1 / atWork2 — CAPITAL W on atWork2 matches the CAT's Inputs NAME3
                // '$${PATH}atWork2'. Coils OutputToWork1 (Work1 = Pick) / OutputToWork2 (Work2 = Place);
                // home closes via No_Sensor_Handler_7SCH.
                // COIL DIRECTION (Left=Work1/Pick, Right=Work2/Place) MUST be confirmed on the rig
                // before motion — Docs/REVERTED_FIXES.md R-12.
                m["SwivelArmAtHome"]    = ("Bearing_PnP", "athome");
                m["SwivelArmAtPick"]    = ("Bearing_PnP", "atwork1");
                m["SwivelArmAtPlace"]   = ("Bearing_PnP", "atWork2");
                m["Swivel_Arm_Left_Q"]  = ("Bearing_PnP", "OutputToWork1");
                m["Swivel_Arm_Right_Q"] = ("Bearing_PnP", "OutputToWork2");
            }
            else
            {
                // Two-position swivel: the twin models only Work1 and Work2 (no centre stop), so the
                // five-state runtime vocabulary carries it — Work1 is the rest position and binds to the
                // CAT's HOME ports, Work2 is the working position and binds to its WORK ports. The centre
                // sensor has no counterpart on this CAT and is blanked. Coil direction follows the same
                // Left=Work1 / Right=Work2 convention as above and carries the same R-12 caveat.
                m["SwivelArmAtPick"]    = ("Bearing_PnP", "athome");
                m["SwivelArmAtPlace"]   = ("Bearing_PnP", "atwork");
                m["SwivelArmAtHome"]    = ("Bearing_PnP", "");
                m["Swivel_Arm_Left_Q"]  = ("Bearing_PnP", "OutputToHome");
                m["Swivel_Arm_Right_Q"] = ("Bearing_PnP", "OutputToWork");
            }
            return m;
        }

        // Direct-bind the deployed M580 X80 .hcf channels to the consumer CAT ports.
        public static void BindM580(MapperConfig? config,
            SystemInjector.BindingApplicationReport report)
        {
            string Log(string m) { var s = $"[HcfBind][M580] {m}"; report.Missing.Add(s); return s; }
            if (config == null) { Log("skipped, no MapperConfig available"); return; }

            try
            {
                var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(config);
                if (string.IsNullOrEmpty(eaeRoot)) { Log("skipped, could not derive EAE project root"); return; }

                var sysdevFile = HcfBindingSupport.FindSysdevByType(eaeRoot, "M580_dPAC", "SE.DPAC");
                if (sysdevFile == null) { Log("skipped, no deployed M580 sysdev (Type=M580_dPAC)"); return; }

                var stem = Path.GetFileNameWithoutExtension(sysdevFile);
                var folder = Path.Combine(Path.GetDirectoryName(sysdevFile)!, stem);
                var hcfPath = Path.Combine(folder, stem + ".hcf");
                if (!File.Exists(hcfPath)) { Log($"skipped, deployed .hcf not found at {hcfPath} (run the HCF copier first)"); return; }

                var (resId, resName) = HcfBindingSupport.ReadSysresIdentity(folder);
                if (string.IsNullOrEmpty(resId)) { Log("skipped, deployed sysres ID not resolvable"); return; }
                // resName is the live Resource Name attribute EAE's $${PATH} macro resolves to as the
                // leading segment of every per-instance symlink the CAT body declares.
                if (string.IsNullOrWhiteSpace(resName)) resName = "RES0";

                var compId = HcfBindingSupport.BuildComponentIdMap(folder);
                if (compId.Count == 0)
                {
                    Log("skipped, no actuator/sensor FBs on the M580 sysres yet " +
                        "(run the Station-2 FB mirror / EmitStation2Sysres first)");
                    return;
                }

                XDocument doc;
                try { doc = XDocument.Load(hcfPath); }
                catch (Exception ex) { Log($"skipped, .hcf parse failed: {ex.GetType().Name}: {ex.Message}"); return; }

                // Resolve the channel map for THIS generation: the swivel's ports follow whichever CAT its
                // own state graph selected, read back from the deployed FB Type.
                var typeOf = HcfBindingSupport.BuildComponentTypeMap(folder);
                typeOf.TryGetValue("Bearing_PnP", out var swivelCat);
                var channelMap = new Dictionary<string, (string Comp, string Port)>(
                    M580ChannelMap, StringComparer.OrdinalIgnoreCase);
                foreach (var kv in SwivelChannels(swivelCat)) channelMap[kv.Key] = kv.Value;

                int bound = 0, already = 0, unmapped = 0, missingComp = 0, literals = 0, blanked = 0;
                var compFbIds   = new HashSet<string>(compId.Values, StringComparer.OrdinalIgnoreCase);
                var compFbNames = new HashSet<string>(compId.Keys,   StringComparer.OrdinalIgnoreCase);

                foreach (var pv in doc.Descendants().Where(e => e.Name.LocalName == "ParameterValue"))
                {
                    var chan = (string?)pv.Attribute("Name") ?? string.Empty;
                    var raw = (string?)pv.Attribute("Value");
                    if (raw == null) continue;

                    if (!HcfBindingSupport.TrySplitSymlink(raw, out var _, out var mid, out var last))
                    {
                        literals++;            // empty / literal / not a head.mid.port triple
                        continue;
                    }

                    if (channelMap.TryGetValue(last, out var map))
                    {
                        if (map.Port.Length == 0)
                        {
                            // The selected CAT has no port for this physical channel (e.g. the centre sensor
                            // of a swivel the twin models with only two stops). Blank it for the same reason
                            // a missing component is blanked: EAE cannot convert a dangling symbolic value.
                            if (!string.IsNullOrEmpty(raw))
                            {
                                pv.SetAttributeValue("Value", "");
                                blanked++;
                                report.Missing.Add(
                                    $"[HcfBind][M580] {chan}: '{last}' has no port on '{map.Comp}'s CAT " +
                                    "(two-position swivel has no centre stop) — blanked (unconfigured)");
                            }
                            else already++;
                            continue;
                        }
                        if (!compId.TryGetValue(map.Comp, out var fbId))
                        {
                            // Component (e.g. Clamp in the no-clamp _vc twin) not on this resource: the .hcf
                            // template still declares its channels as symbolic 'RES0.M580IO.<name>', which EAE
                            // CANNOT convert at compile ("HW Configuration could not convert the symbolic
                            // value") and fails the build. Blank the channel (unconfigured IO) so the compile
                            // succeeds. Clamp model is unaffected: the Clamp FB is present -> binds normally.
                            missingComp++;
                            pv.SetAttributeValue("Value", "");
                            blanked++;
                            report.Missing.Add(
                                $"[HcfBind][M580] {chan}: '{last}' -> component '{map.Comp}' " +
                                "not on the M580 resource — blanked (unconfigured; was dangling symbolic)");
                            continue;
                        }
                        // Form 1 direct GUID triple "<resId>.<fbId>.<port>" (as M262 uses): populates
                        // BOTH EAE's device-tree IO view AND the Symbolic Link panel. A quoted
                        // per-instance symbolic (Form 2) leaves the device-tree Value column blank.
                        var boundVal = $"{resId}.{fbId}.{map.Port}";
                        if (!string.Equals(raw, boundVal, StringComparison.Ordinal))
                        {
                            pv.SetAttributeValue("Value", boundVal);
                            bound++;
                            report.Missing.Add(
                                $"[HcfBind][M580] {chan} = {boundVal}  (was {raw})");
                        }
                        else already++;
                        report.HcfPinAssignments.Add((chan, boundVal));
                        continue;
                    }

                    // Already bound: middle segment matches a component FB id (Form 1) or FB name
                    // (Form 2, kept only for idempotent rerun detection). No-op on rerun.
                    if (compFbIds.Contains(mid) || compFbNames.Contains(mid))
                    { already++; continue; }

                    unmapped++;
                    report.Missing.Add(
                        $"[HcfBind][M580] {chan}: symlink '{last}' not in the M580 channel map — left as-is");
                }

                if (bound > 0 || blanked > 0) HcfBindingSupport.SaveHcf(doc, hcfPath);
                Log($"GUID-bound {bound} channel(s) to CAT ports (resource '{resName}' / {resId}); {already} already bound, " +
                    $"{unmapped} unmapped, {missingComp} missing-component, {literals} literal/empty. " +
                    "Form 1 direct GUID triple ('<resId>.<fbId>.<port>') — populates EAE's " +
                    "device-tree IO view (BMXDDM16025 channel Value column) AND the Symbolic " +
                    "Link side panel; matches the M262 .hcf binding pattern byte-for-byte.");
            }
            catch (Exception ex)
            {
                Log($"failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
