using System;
using CodeGen.Devices.Core;
using CodeGen.IO;
using CodeGen.Mapping;
using CodeGen.Translation;

namespace CodeGen.Devices.M262
{
    // The M262 dPAC: the device EAE binds its trust to, and the resource that carries the workbook's
    // physical channels.
    public sealed class M262Backend : TargetBackend
    {
        // The target this instance emits, handed in by the composition root from the row that
        // declared it. A second controller of this kind is another row, not another class.
        public override PlcAssignment Target { get; }

        public M262Backend(PlcAssignment target) => Target = target;

        // An existing device is PRESERVED rather than re-created: re-emitting the sysdev would break the
        // trust binding EAE holds against it, and the application layer is mirrored either way.
        public override void EmitDevice(GenerationContext ctx, DeviceScope scope, Action<string> log)
        {
            var cfg = ctx.Cfg;
            bool existed = false;
            try { existed = M262SysdevEmitter.M262SysdevAlreadyExists(cfg); } catch { }

            var sysdevId = string.Empty;
            Stage("sysdev emit", log, () =>
            {
                var sysdev = M262SysdevEmitter.Emit(ctx);
                log(sysdev.DevicePreserved
                    ? $"[M262] sysdev preserved (trust binding intact); .sysres mirrored " +
                      $"{sysdev.SysresFbsMirrored} FB(s) to {sysdev.SysresPath}"
                    : $"[M262] sysdev re-emitted; .sysres mirrored {sysdev.SysresFbsMirrored} FBs to " +
                      sysdev.SysresPath);
                sysdevId = EaeProjectLayout.ReadSysdevId(sysdev.SysdevPath);
            });

            // The equipment JSON is re-written every run, or the device never re-appears after a wipe.
            Stage("topology emit", log, () =>
            {
                if (string.IsNullOrEmpty(sysdevId))
                {
                    log("[M262][Warn] topology emit skipped - the sysdev id was empty");
                    return;
                }
                var topo = M262TopologyEmitter.Emit(cfg, sysdevId);
                log($"[M262] topology emitted: {topo.FilesWritten.Count} JSON file(s), " +
                    $"{topo.TopologyProjEntriesAdded} topologyproj entries added");
                if (existed) log("[M262] solutionData preserved (existing trust binding kept intact)");
                foreach (var w in topo.Warnings) log($"[M262][Warn] topology: {w}");
            });
        }

        public override void CopyHardwareConfig(GenerationContext ctx, Action<string> log) =>
            Stage("hcf patch", log, () =>
            {
                var hcf = HwConfigVerbatimCopier.CopyFor(
                    ctx.Cfg, Target, ctx.Config.M262HcfTemplatePath);
                log($"[M262] hcf re-patched; {hcf.ParametersOverwritten.Count} channel symlink(s) written");
                foreach (var w in hcf.Warnings) log($"[M262][Warn] {w}");
            });

        // Without these wires EAE deploys the resource but nothing inits.
        public override void WireResource(GenerationContext ctx,
            SystemInjector.BindingApplicationReport report, Action<string> log) =>
            Stage("resource wire", log, () =>
            {
                ResourceWireEmitter.WireResource(ctx, Target, report);
                // The canvas is shared, so its layout is refreshed once, from the target that hosts the
                // station every other resource hangs off.
                ResourceWireEmitter.ApplyLayoutToSyslay(ctx, ctx.Config.ActiveSyslayPath, report);
            });

        // The workbook's DI/DO rows: this is the only target whose channels come from it.
        public override void BindHardware(GenerationContext ctx, IoBindings? bindings,
            SystemInjector.BindingApplicationReport report, Action<string> log) =>
            Stage("hcf bind", log, () => HcfPatchService.PatchDeployed(ctx, bindings, report));
    }
}
