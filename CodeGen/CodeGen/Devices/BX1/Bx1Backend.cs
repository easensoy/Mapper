using System;
using CodeGen.Devices.Core;
using CodeGen.IO;
using CodeGen.Mapping;
using CodeGen.Translation;

namespace CodeGen.Devices.BX1
{
    // The BX1 Soft dPAC: the cover station, and the EtherNet/IP coupler its scanner drives.
    public sealed class Bx1Backend : TargetBackend
    {
        public override PlcAssignment Target => PlcAssignment.BX1;

        public override void EmitDevice(GenerationContext ctx, DeviceScope scope, Action<string> log) =>
            Stage("device emit", log,
                () => M580.M580Backend.Report(Station2DeviceEmitter.EmitBx1(ctx.Config, scope), log));

        public override void CopyHardwareConfig(GenerationContext ctx, Action<string> log) =>
            Stage("hcf deploy", log, () =>
            {
                var hcf = BX1HwConfigCopier.Copy(ctx.Config);
                log($"[BX1] hcf deployed; {hcf.FilesCopied} file(s) copied -> {hcf.HcfPath}");
                foreach (var w in hcf.Warnings) log($"[BX1][Warn] {w}");
            });

        public override void WireResource(GenerationContext ctx,
            SystemInjector.BindingApplicationReport report, Action<string> log) =>
            Stage("resource wire", log, () => Station2WireEmitter.WireResource(ctx, Target, report));
    }
}
