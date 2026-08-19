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
        // The authored .hcf channel symlink name -> the component + CAT port it binds, from
        // Config/smc-rig.yml. The rig owns which physical channel carries which signal; this file only
        // resolves the ids and writes the Form-1 triples.
        private static Dictionary<string, (string Comp, string Port)> ChannelMap(string? catType)
        {
            var cat = RigCatalog.Current;
            var m = new Dictionary<string, (string Comp, string Port)>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in cat.M580Channels) m[b.Channel] = (b.Component, b.Port);
            // The swivel set follows the CAT the twin's own state graph selected, so a two-position model
            // and a centre-home model bind the same physical channels to different ports.
            var swivel = string.Equals(catType, TemplateMap.SevenStateCentreHomeCat, StringComparison.OrdinalIgnoreCase)
                ? cat.SwivelChannels.CentreHome
                : cat.SwivelChannels.TwoPosition;
            foreach (var b in swivel) m[b.Channel] = (b.Component, b.Port);
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

                var sysdevFile = HcfBindingSupport.FindSysdevByType(eaeRoot, TargetRegistry.Of(CodeGen.Translation.PlcAssignment.M580).DeviceType, TargetDescriptor.DeviceNamespace);
                if (sysdevFile == null) { Log("skipped, no deployed M580 sysdev (Type=M580_dPAC)"); return; }

                var stem = Path.GetFileNameWithoutExtension(sysdevFile);
                var folder = Path.Combine(Path.GetDirectoryName(sysdevFile)!, stem);
                var hcfPath = Path.Combine(folder, stem + ".hcf");
                if (!File.Exists(hcfPath)) { Log($"skipped, deployed .hcf not found at {hcfPath} (run the HCF copier first)"); return; }

                var (resId, resName) = HcfBindingSupport.ReadSysresIdentity(folder);
                if (string.IsNullOrEmpty(resId)) { Log("skipped, deployed sysres ID not resolvable"); return; }
                // resName is the live Resource Name attribute EAE's $${PATH} macro resolves to as the
                // leading segment of every per-instance symlink the CAT body declares.
                if (string.IsNullOrWhiteSpace(resName)) resName = CodeGen.Mapping.ControllerMap.ResourceForPlc(PlcAssignment.M580);

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
                // Which component IS the swivel comes from the catalog's own swivel rows, not a name
                // this binder knows.
                var swivelComponent = RigCatalog.Current.SwivelChannels.CentreHome
                    .Concat(RigCatalog.Current.SwivelChannels.TwoPosition)
                    .Select(b => b.Component).FirstOrDefault() ?? string.Empty;
                typeOf.TryGetValue(swivelComponent, out var swivelCat);
                var channelMap = ChannelMap(swivelCat);

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
