using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.Core;
using CodeGen.Devices.M262;
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

        // Bearing_PnP swivel bindings; target CAT ports differ between the real Seven_State swivel
        // and the Five_State stub (MapperConfig.StubSevenStateActuatorsAsFiveState).
        static M580SymbolBinder()
        {
            if (MapperConfig.StubSevenStateActuatorsAsFiveState)
            {
                // Five_State stub: bind BOTH coils (Left = extend toward pick/work, Right = return
                // home) so the double-acting swivel can go home. The 3rd (Place) sensor has no
                // Five_State port; runs sensorless/timer-settled.
                M580ChannelMap["SwivelArmAtHome"]    = ("Bearing_PnP", "athome");
                M580ChannelMap["SwivelArmAtPick"]    = ("Bearing_PnP", "atwork");
                M580ChannelMap["Swivel_Arm_Left_Q"]  = ("Bearing_PnP", "OutputToWork");
                M580ChannelMap["Swivel_Arm_Right_Q"] = ("Bearing_PnP", "OutputToHome");
            }
            else
            {
                // Centre-home swivel CAT. Sensor symlinks: athome / atwork1 / atWork2 — CAPITAL W on
                // atWork2 matches the CAT's Inputs NAME3 '$${PATH}atWork2'. Coils: OutputToWork1
                // (Work1 = Pick), OutputToWork2 (Work2 = Place); home closes via No_Sensor_Handler_7SCH.
                // COIL DIRECTION (Left=Work1/Pick, Right=Work2/Place) MUST be confirmed on the rig
                // before motion — Docs/REVERTED_FIXES.md R-12.
                M580ChannelMap["SwivelArmAtHome"]    = ("Bearing_PnP", "athome");
                M580ChannelMap["SwivelArmAtPick"]    = ("Bearing_PnP", "atwork1");
                M580ChannelMap["SwivelArmAtPlace"]   = ("Bearing_PnP", "atWork2");
                M580ChannelMap["Swivel_Arm_Left_Q"]  = ("Bearing_PnP", "OutputToWork1");
                M580ChannelMap["Swivel_Arm_Right_Q"] = ("Bearing_PnP", "OutputToWork2");
            }
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

                int bound = 0, already = 0, unmapped = 0, missingComp = 0, literals = 0;
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

                if (bound > 0) HcfBindingSupport.SaveHcf(doc, hcfPath);
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
