using System;
using System.Linq;
using CodeGen.Translation;
using System.IO;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.Core;
using CodeGen.Devices.M262;
using CodeGen.Mapping;

namespace CodeGen.Devices.Core
{
    public static class Station2WireEmitter
    {
        public static void WireResource(GenerationContext ctx, CodeGen.Translation.PlcAssignment plc,
            SystemInjector.BindingApplicationReport report)
        {
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(ctx.Config);
            if (eaeRoot == null)
            {
                report.Missing.Add("[Wire] skipped, EAE project root not derivable");
                return;
            }
            Wire(ctx, eaeRoot, plc, report);
        }

        // Parameters are synced from the syslay BOTH sides of the wiring pass: before, so the wiring
        // sees the FBs it is about to connect, and after, because EmitForResource rewrites the
        // FBNetwork and a resource that shipped with a stale recipe deploys silently wrong.
        private static void Wire(GenerationContext ctx, string eaeRoot,
            CodeGen.Translation.PlcAssignment plc, SystemInjector.BindingApplicationReport report)
        {
            var deviceType = TargetRegistry.Of(plc).DeviceType;
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
            ResourceWireEmitter.EmitForResource(ctx, sysres, plan, report);
            Sync("post-wire ");
        }
    }
}
