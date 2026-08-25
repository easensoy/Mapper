using System;
using CodeGen.IO;
using CodeGen.Mapping;
using CodeGen.Translation;

namespace CodeGen.Devices.RevPi
{
    // The Revolution Pi. It exists only when the profile relocates part of the feed station onto it, so
    // both stages ask the plan rather than a configuration switch.
    public sealed class RevPiBackend : TargetBackend
    {
        public override PlcAssignment Target => PlcAssignment.RevPi;

        public override void EmitDevice(GenerationContext ctx, DeviceScope scope, Action<string> log)
        {
            if (!ctx.Profile.PartialRevPi) return;
            Stage("device emit", log, () =>
            {
                var report = new SystemInjector.BindingApplicationReport();
                RevPiDeviceEmitter.EmitDevice(ctx, report);
                foreach (var m in report.Missing) log(m);
            });
        }

        public override void WireResource(GenerationContext ctx,
            SystemInjector.BindingApplicationReport report, Action<string> log)
        {
            if (!ctx.Profile.PartialRevPi) return;
            Stage("resource wire", log, () => RevPiDeviceEmitter.WireResource(ctx, report));
        }
    }
}
