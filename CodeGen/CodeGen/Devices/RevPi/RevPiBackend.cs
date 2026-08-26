using System;
using System.Linq;
using CodeGen.IO;
using CodeGen.Mapping;
using CodeGen.Translation;

namespace CodeGen.Devices.RevPi
{
    // The Revolution Pi. It exists only when the run assigns components to it, so every stage asks the
    // plan rather than a configuration switch.
    public sealed class RevPiBackend : TargetBackend
    {
        // The target this instance emits, handed in by the composition root from the row that
        // declared it. A second controller of this kind is another row, not another class.
        public override PlcAssignment Target { get; }

        public RevPiBackend(PlcAssignment target) => Target = target;

        // Its Modbus coupler reads a fixed set of signals, so only those components have IO here.
        public override System.Collections.Generic.IReadOnlySet<string> ServableComponents =>
            RevPiIoBrokerInjector.CoveredComponents;

        // Its Modbus coupler carries a fixed set of channels, so a component assigned here that the
        // coupler cannot read would deploy with no IO and could never actuate. That is this target's
        // hardware contract, so this target is what refuses it.
        public override void ValidateAssignment(GenerationContext ctx)
        {
            if (!ctx.Profile.AssignsAnythingTo(Target)) return;
            var assigned = ctx.Profile.Assignments
                .Where(kv => kv.Value == Target).Select(kv => kv.Key).ToList();
            Validation.Plan.RevPiSelectionValidator.ThrowIfInvalid(ctx.Profile, ctx.IoBearing(assigned));
        }

        public override void EmitDevice(GenerationContext ctx, DeviceScope scope, Action<string> log)
        {
            if (!ctx.Profile.HasAssignments) return;
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
            if (!ctx.Profile.HasAssignments) return;
            Stage("resource wire", log, () => RevPiDeviceEmitter.WireResource(ctx, report));
        }
    }
}
