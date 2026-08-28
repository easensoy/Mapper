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
        // The target this instance emits, handed in by the composition root from the row that
        // declared it. A second controller of this kind is another row, not another class.
        public override PlcAssignment Target { get; }

        public Bx1Backend(PlcAssignment target) => Target = target;

        public override void EmitDevice(GenerationContext ctx, DeviceScope scope, Action<string> log) =>
            Stage("device emit", log,
                () => M580.M580Backend.Report(Station2DeviceEmitter.EmitBx1(ctx.Cfg, Target, scope), log));

        public override void CopyHardwareConfig(GenerationContext ctx, Action<string> log) =>
            Stage("hcf deploy", log, () =>
            {
                var hcf = BX1HwConfigCopier.Copy(ctx.Cfg, Target);
                log($"[BX1] hcf deployed; {hcf.FilesCopied} file(s) copied -> {hcf.HcfPath}");
                foreach (var w in hcf.Warnings) log($"[BX1][Warn] {w}");
            });

        public override void WireResource(GenerationContext ctx,
            SystemInjector.BindingApplicationReport report, Action<string> log) =>
            Stage("resource wire", log, () => ResourceWireEmitter.WireResource(ctx, Target, report));

        // The broker's symlinks resolve to the cover FBs, so it is injected only once every resource
        // has been wired and those FBs exist. A failure here leaves the covers with no IO path at all,
        // so the stage ABORTS rather than reporting a project that cannot actuate.
        public override void FinishApplication(GenerationContext ctx, string syslayPath,
            SystemInjector.BindingApplicationReport report, Action<string> log) =>
            Stage("io broker", log, () =>
            {
                var n = Bx1IoBrokerInjector.InjectBx1IoBroker(ctx.Cfg, Target, syslayPath, report);
                log($"[BX1][Broker] BX1_IO injected into {n} artefact(s).");
                ResourceWireEmitter.ApplyLayoutToSyslay(ctx, syslayPath, report);
            });

        // An EMPTY scanner deploys and simply cannot move a cover, so this is a refusal rather than a
        // warning - and it belongs to the target whose hardware it is about.
        public override void ValidateOutput(GenerationContext ctx, Action<string> log) =>
            Stage("scanner validate", log, () =>
            {
                var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(ctx.Cfg);
                if (string.IsNullOrEmpty(eaeRoot)) return;
                var scan = Bx1ScannerValidator.Validate(ctx.Cfg, eaeRoot);
                foreach (var l in scan.Lines) log(l);
                if (scan.Fatal)
                    throw new InvalidOperationException(
                        "[BX1][Scanner] the BX1 EtherNet/IP scanner would compile EMPTY; the cover I/O " +
                        "and the CoverPNP_Hr safe-start cannot reach the coupler.");
            });
    }
}
