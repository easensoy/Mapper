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
    /// <summary>
    /// Binds the deployed M580 <c>.hcf</c> hardware-config channel symlinks so
    /// EAE's "Symbolic Link" view resolves them. This is the M580 sibling of the
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
    /// <para>Idempotent: a channel already bound to a component FB id is left
    /// unchanged. Empty/literal channels (<c>''</c>, scanner ids, <c>T#…</c>) are
    /// never touched. Does NOT touch the M262 <see cref="HcfPatchService"/>.</para>
    /// </summary>
    public static class M580SymbolBinder
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

                var sysdevFile = HcfBindingSupport.FindSysdevByType(eaeRoot, "M580_dPAC", "SE.DPAC");
                if (sysdevFile == null) { Log("skipped, no deployed M580 sysdev (Type=M580_dPAC)"); return; }

                var stem = Path.GetFileNameWithoutExtension(sysdevFile);
                var folder = Path.Combine(Path.GetDirectoryName(sysdevFile)!, stem);
                var hcfPath = Path.Combine(folder, stem + ".hcf");
                if (!File.Exists(hcfPath)) { Log($"skipped, deployed .hcf not found at {hcfPath} (run the HCF copier first)"); return; }

                var (resId, resName) = HcfBindingSupport.ReadSysresIdentity(folder);
                if (string.IsNullOrEmpty(resId)) { Log("skipped, deployed sysres ID not resolvable"); return; }
                // resName is the live Resource Name attribute (e.g. "M580_RES").
                // It is what EAE's $${PATH} macro resolves to as the leading
                // segment of every per-instance symlink the CAT body declares —
                // so the symlink-form binding we emit below ("M580_RES.X.Y")
                // matches the symlink the CAT already publishes for the same
                // FB instance and EAE renders it in the Symbolic Link view.
                if (string.IsNullOrWhiteSpace(resName)) resName = "M580_RES";

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
                        // Form 2 (per-instance symbolic): '<ResourceName>.<FBName>.<port>'.
                        // Switched from Form 1 (direct GUID '<resId>.<fbId>.<port>') on
                        // 2026-05-26 so EAE's Symbolic Link view shows each channel as a
                        // proper symlink instead of an opaque GUID triple. The CAT body
                        // already publishes a matching SYMLINK for every instance via
                        // SYMLINKMULTIVARDST 'NAME = $${PATH}<port>' and SYMLINKMULTIVARSRC
                        // 'NAME = $${PATH}<port>', and EAE resolves $${PATH} to the
                        // FB's resource-prefixed path ("M580_RES.Bearing_PnP" etc.) — so
                        // the value we write here is the exact name the CAT-internal
                        // symlink table carries, and runtime resolution succeeds without
                        // a PLC_RW_M580 broker FB (the reference SMC_Rig_Expo had one
                        // because its .hcf used the broker prefix 'RES0.M580IO.*'; we
                        // use the consumer-FB prefix instead). Single-quoted because
                        // EAE's PNConfiguratorBuildTask treats the channel value as a
                        // string literal exactly like the template's 'RES0.M580IO.*' form.
                        var boundVal = $"'{resName}.{map.Comp}.{map.Port}'";
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

                    // Already bound — middle segment matches one of our component FB ids
                    // (Form 1, legacy direct GUID) OR one of our component FB names
                    // (Form 2, current per-instance symbolic). Both are no-ops on rerun.
                    if (compFbIds.Contains(mid) || compFbNames.Contains(mid))
                    { already++; continue; }

                    unmapped++;
                    report.Missing.Add(
                        $"[HcfBind][M580] {chan}: symlink '{last}' not in the M580 channel map — left as-is");
                }

                if (bound > 0) HcfBindingSupport.SaveHcf(doc, hcfPath);
                Log($"symlink-bound {bound} channel(s) to CAT ports (resource '{resName}'); {already} already bound, " +
                    $"{unmapped} unmapped, {missingComp} missing-component, {literals} literal/empty. " +
                    "Form 2 per-instance symbolic ('<resName>.<FB>.<port>') — EAE Symbolic Link " +
                    "view shows the symlinks, no broker FB required.");
            }
            catch (Exception ex)
            {
                Log($"failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
