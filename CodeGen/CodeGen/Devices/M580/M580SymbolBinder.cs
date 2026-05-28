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
                // Bearing_PnP swivel channels are added by the static ctor below —
                // their target ports differ between the real Seven_State swivel and
                // the interim Five_State stub.
            };

        // Bearing_PnP (swivel) channel bindings, split out because the target CAT
        // ports differ between the real Seven_State swivel and the interim
        // Five_State stub (MapperConfig.StubSevenStateActuatorsAsFiveState).
        static M580SymbolBinder()
        {
            if (MapperConfig.StubSevenStateActuatorsAsFiveState)
            {
                // Five_State stub: single drive coil + athome/atwork sensor pair.
                // Pick = the Five_State "work" position, driven by the Left coil.
                // The Place sensor and Right coil have no Five_State port, so they
                // are intentionally left unbound — the swivel runs sensorless /
                // timer-settled (see SystemLayoutInjector.BuildActuatorParameters),
                // so the missing second sensor/coil cannot stall the recipe.
                // Restore the full 5-channel map when task #69 brings the
                // Seven_State CAT back.
                M580ChannelMap["SwivelArmAtHome"]   = ("Bearing_PnP", "athome");
                M580ChannelMap["SwivelArmAtPick"]   = ("Bearing_PnP", "atwork");
                M580ChannelMap["Swivel_Arm_Left_Q"] = ("Bearing_PnP", "OutputToWork");
            }
            else
            {
                // Real Seven_State swivel: 3 position sensors + 2 drive coils.
                M580ChannelMap["SwivelArmAtHome"]    = ("Bearing_PnP", "athome");
                M580ChannelMap["SwivelArmAtPick"]    = ("Bearing_PnP", "atwork1");
                M580ChannelMap["SwivelArmAtPlace"]   = ("Bearing_PnP", "atwork2");
                M580ChannelMap["Swivel_Arm_Left_Q"]  = ("Bearing_PnP", "current_state1_to_plc");
                M580ChannelMap["Swivel_Arm_Right_Q"] = ("Bearing_PnP", "current_state2_to_plc");
            }
        }

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
                        // Form 1 (direct GUID triple): "<resId>.<fbId>.<port>" — unquoted,
                        // dot-separated, GUID-headed. SAME form the M262 .hcf uses for
                        // every PusherAtHome / Hopper / etc channel — verified by reading
                        // the deployed M262 .hcf side-by-side with M580 on 2026-05-26:
                        //   M262 DI00 -> "1459BCD12760907D.60AEF2679BD52F88.athome"  (Form 1)
                        //   M580 DI00 -> "'M580_RES.Bearing_PnP.atwork1'"            (Form 2)
                        //
                        // Switched BACK from Form 2 ("'<ResName>.<FBName>.<port>'", quoted,
                        // per-instance symbolic) on 2026-05-26 because Form 2 populated only
                        // EAE's Symbolic Link side panel — the device-tree IO view
                        // (System > Devices > M580 > M580_RES > BMEXBP0400 > BMXDDM16025)
                        // left every channel's Value column blank, because EAE's Hardware
                        // Configurator parses only the Form 1 GUID triple into that view.
                        // Form 1 populates BOTH the device-tree IO view AND the symlink
                        // panel — no broker FB required, because the CAT body's
                        // SYMLINKMULTIVARSRC/DST table publishes a $${PATH}<port> symlink
                        // for every instance, and EAE's runtime resolves the GUID triple
                        // against that symlink table at deploy time. Mirrors M262 exactly.
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

                    // Already bound — middle segment matches one of our component FB ids
                    // (Form 1, current direct GUID — the form we now emit and the same
                    // one M262 uses) OR one of our component FB names (Form 2, legacy
                    // per-instance symbolic kept here ONLY for idempotent rerun detection
                    // so prior-Form-2 .hcfs upgrade cleanly on next deploy without spurious
                    // "unmapped" log lines). Both branches are no-ops on rerun.
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
