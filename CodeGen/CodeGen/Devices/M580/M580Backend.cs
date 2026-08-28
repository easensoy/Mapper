using System;
using CodeGen.Devices.Core;
using CodeGen.IO;
using CodeGen.Mapping;
using CodeGen.Translation;

namespace CodeGen.Devices.M580
{
    // The M580 dPAC: the assembly side's controller.
    public sealed class M580Backend : TargetBackend
    {
        // The target this instance emits, handed in by the composition root from the row that
        // declared it. A second controller of this kind is another row, not another class.
        public override PlcAssignment Target { get; }

        public M580Backend(PlcAssignment target) => Target = target;

        public override void EmitDevice(GenerationContext ctx, DeviceScope scope, Action<string> log) =>
            Stage("device emit", log, () => Report(Station2DeviceEmitter.EmitM580(ctx.Cfg, Target, scope), log));

        // The authored .hcf carried verbatim and re-rooted with the resource id, so it refills after a wipe.
        public override void CopyHardwareConfig(GenerationContext ctx, Action<string> log) =>
            Stage("hcf deploy", log, () =>
            {
                var hcf = HwConfigVerbatimCopier.CopyFor(
                    ctx.Cfg, Target, ctx.Config.M580HcfTemplatePath);
                log($"[M580] hcf deployed; {hcf.FilesCopied} file(s) copied -> {hcf.HcfPath}");
                foreach (var w in hcf.Warnings) log($"[M580][Warn] {w}");
            });

        public override void WireResource(GenerationContext ctx,
            SystemInjector.BindingApplicationReport report, Action<string> log) =>
            Stage("resource wire", log, () => ResourceWireEmitter.WireResource(ctx, Target, report));

        // The workbook carries no rows for this target, so its symlinks are re-aligned to the deployed
        // sysres id instead.
        public override void BindHardware(GenerationContext ctx, IoBindings? bindings,
            SystemInjector.BindingApplicationReport report, Action<string> log) =>
            Stage("hcf bind", log, () => M580SymbolBinder.BindM580(ctx.Cfg, Target, report));

        internal static void Report(Station2DeviceEmitter.EmitResult result, Action<string> log)
        {
            foreach (var f in result.FilesWritten) log($"[Stn2]   {f}");
            foreach (var w in result.Warnings) log($"[Stn2][Warn] {w}");
        }
    }
}
