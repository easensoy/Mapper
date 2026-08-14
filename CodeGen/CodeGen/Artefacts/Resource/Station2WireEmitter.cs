using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.Core;
using CodeGen.Devices.M262;
using CodeGen.Translation;

namespace CodeGen.Devices.Core
{
    public static class Station2WireEmitter
    {
        public static void EmitStation2Resources(GenerationContext ctx,
            SystemInjector.BindingApplicationReport report)
        {
            var cfg = ctx.Config;
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            if (eaeRoot == null)
            {
                report.Missing.Add("[Wire][Stn2] skipped, EAE project root not derivable");
                return;
            }

            Wire(ctx, eaeRoot, CodeGen.Translation.PlcAssignment.M580, report);
            Wire(ctx, eaeRoot, CodeGen.Translation.PlcAssignment.BX1, report);
        }

        // Parameters are synced from the syslay BOTH sides of the wiring pass: before, so the wiring
        // sees the FBs it is about to connect, and after, because EmitForResource rewrites the
        // FBNetwork and a resource that shipped with a stale recipe deploys silently wrong.
        private static void Wire(GenerationContext ctx, string eaeRoot,
            CodeGen.Translation.PlcAssignment plc, SystemInjector.BindingApplicationReport report)
        {
            var deviceType = CodeGen.Mapping.PlcTargets.DeviceType(plc);
            var plan = ctx.ResourceFor(plc);
            var tag = plan.Label;
            var cfg = ctx.Config;
            var sysdev = EaeProjectLayout.FindSysdevByDeviceType(eaeRoot, deviceType);
            var sysres = sysdev == null ? null : EaeProjectLayout.FindSysresFor(sysdev);
            if (sysres == null)
            {
                report.Missing.Add($"[Wire][{tag}] skipped, {tag} sysres not found");
                return;
            }

            // The mirrored-parameter sync covers the process engines too, so their recipes travel with it.
            void Sync(string when)
            {
                var parameters = SysresFbMirror.SyncMirroredFbParametersFromSyslay(cfg.ActiveSyslayPath, sysres);
                if (parameters > 0)
                    report.Missing.Add($"[Wire][{tag}] {when}synced {parameters} mirrored FB parameter set(s) from syslay to sysres");
            }

            Sync(string.Empty);
            // A leftover Cover_Station would be re-discovered and re-wired by the type scan below, so it
            // is swept first. BX1 runs no Process engine: Assembly_Station commands the covers.
            if (plan.ProcessFb == null) SweepCoverStationFromSysres(sysres, report);
            ResourceWireEmitter.EmitForResource(ctx, sysres, plan, report);
            Sync("post-wire ");
        }

        // Removes a stale Cover_Station Process FB + its connections from a BX1 sysres so
        // ResourceWireEmitter.EmitForResource cannot re-discover and re-wire it. Idempotent.
        private static void SweepCoverStationFromSysres(string sysresPath,
            SystemInjector.BindingApplicationReport report)
        {
            try
            {
                if (string.IsNullOrEmpty(sysresPath) || !File.Exists(sysresPath)) return;
                var doc = XDocument.Load(sysresPath);
                var net = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "FBNetwork");
                if (net == null) return;

                static string FbOf(string? ep) =>
                    ep == null ? "" : (ep.Contains('.') ? ep[..ep.IndexOf('.')] : ep);
                const string target = "Cover_Station";

                var fbs = net.Elements().Where(e => e.Name.LocalName == "FB" &&
                    string.Equals((string?)e.Attribute("Name"), target, StringComparison.Ordinal)).ToList();
                if (fbs.Count == 0) return;
                foreach (var fb in fbs) fb.Remove();

                int conns = 0;
                foreach (var grp in net.Elements().Where(e =>
                    e.Name.LocalName is "EventConnections" or "DataConnections" or "AdapterConnections").ToList())
                {
                    foreach (var c in grp.Elements().Where(e => e.Name.LocalName == "Connection" &&
                        (string.Equals(FbOf((string?)e.Attribute("Source")), target, StringComparison.Ordinal) ||
                         string.Equals(FbOf((string?)e.Attribute("Destination")), target, StringComparison.Ordinal))).ToList())
                    { c.Remove(); conns++; }
                }

                doc.Save(sysresPath);
                report.Missing.Add(
                    $"[Wire][BX1] STAGE 4: swept stale Cover_Station FB + {conns} connection(s) " +
                    $"from {Path.GetFileName(sysresPath)} (covers now commanded by Assembly_Station)");
            }
            catch (Exception ex)
            {
                report.Missing.Add($"[Wire][BX1] Cover_Station sweep failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
