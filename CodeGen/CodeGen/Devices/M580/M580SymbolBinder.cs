using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.Devices.M262;
using CodeGen.Mapping;
using CodeGen.Translation;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.Core;

namespace CodeGen.Devices.M580
{
    // Binds the deployed M580 .hcf channel symlinks direct to the consumer CAT instance:
    // {resourceId}.{consumerFbId}.{port}, unquoted and GUID-headed. No PLC_RW_M580 broker FB is emitted,
    // so the authored symlink name is translated to a CAT port. Literal/empty channels are never touched.
    public static class M580SymbolBinder
    {
        // The authored channel symlink name -> the component + CAT port it binds, from Config/smc-rig.yml.
        // The rig owns which channel carries which signal; this file only resolves ids and writes triples.
        private static Dictionary<string, (string Comp, string Port)> ChannelMap(RigCatalog cat, string? catType)
        {

            var m = new Dictionary<string, (string Comp, string Port)>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in cat.M580Channels) m[b.Channel] = (b.Component, b.Port);
            // A two-position swivel and a centre-home one bind the same channels to different ports.
            var swivel = string.Equals(catType, TemplateMap.SevenStateCentreHomeCat, StringComparison.OrdinalIgnoreCase)
                ? cat.SwivelChannels.CentreHome
                : cat.SwivelChannels.TwoPosition;
            foreach (var b in swivel) m[b.Channel] = (b.Component, b.Port);
            return m;
        }

        public static void BindM580(Configuration.CompilerConfiguration? config,
            SystemInjector.BindingApplicationReport report)
        {
            string Log(string m) { var s = $"[HcfBind][M580] {m}"; report.Missing.Add(s); return s; }
            if (config == null) { Log("skipped, no MapperConfig available"); return; }

            try
            {
                var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(config);
                if (string.IsNullOrEmpty(eaeRoot)) { Log("skipped, could not derive EAE project root"); return; }

                var sysdevFile = EaeProjectLayout.FindSysdevByDeviceType(
                    eaeRoot, TargetRegistry.Of(CodeGen.Translation.PlcAssignment.Named("M580")).DeviceType);
                if (sysdevFile == null) { Log("skipped, no deployed M580 sysdev (Type=M580_dPAC)"); return; }

                var stem = Path.GetFileNameWithoutExtension(sysdevFile);
                var folder = Path.Combine(Path.GetDirectoryName(sysdevFile)!, stem);
                var hcfPath = Path.Combine(folder, stem + ".hcf");
                if (!File.Exists(hcfPath)) { Log($"skipped, deployed .hcf not found at {hcfPath} (run the HCF copier first)"); return; }

                var (resId, resName) = HcfBindingSupport.ReadSysresIdentity(folder);
                if (string.IsNullOrEmpty(resId)) { Log("skipped, deployed sysres ID not resolvable"); return; }
                // resName is what EAE's $${PATH} macro resolves to as the leading symlink segment.
                if (string.IsNullOrWhiteSpace(resName)) resName = TargetRegistry.Of(PlcAssignment.Named("M580")).ResourceName;

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

                // The swivel's ports follow whichever CAT its state graph selected, read back from the
                // deployed FB Type; which component IS the swivel comes from the catalog's swivel rows.
                var typeOf = HcfBindingSupport.BuildComponentTypeMap(folder);
                var swivelComponent = config.Rig.SwivelChannels.CentreHome
                    .Concat(config.Rig.SwivelChannels.TwoPosition)
                    .Select(b => b.Component).FirstOrDefault() ?? string.Empty;
                typeOf.TryGetValue(swivelComponent, out var swivelCat);
                var channelMap = ChannelMap(config.Rig, swivelCat);

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
                            // The selected CAT has no port for this channel (e.g. the centre sensor of a
                            // two-stop swivel). EAE cannot convert a dangling symbolic value, so blank it.
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
                            // Component not on this resource (e.g. Clamp in the no-clamp twin), but the .hcf
                            // template still declares its channels symbolically, which EAE cannot convert at
                            // compile. Blank the channel so the build succeeds.
                            missingComp++;
                            pv.SetAttributeValue("Value", "");
                            blanked++;
                            report.Missing.Add(
                                $"[HcfBind][M580] {chan}: '{last}' -> component '{map.Comp}' " +
                                "not on the M580 resource — blanked (unconfigured; was dangling symbolic)");
                            continue;
                        }
                        // The direct GUID triple populates BOTH EAE's device-tree IO view and the Symbolic
                        // Link panel; a quoted per-instance symbolic leaves the Value column blank.
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

                    // Already bound: the middle segment matches a component FB id or name. No-op on rerun.
                    if (compFbIds.Contains(mid) || compFbNames.Contains(mid))
                    { already++; continue; }

                    unmapped++;
                    report.Missing.Add(
                        $"[HcfBind][M580] {chan}: symlink '{last}' not in the M580 channel map — left as-is");
                }

                if (bound > 0 || blanked > 0) HcfBindingSupport.SaveHcf(doc, hcfPath, config.Generation.FileWriteRetries);
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
