using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Devices.Core;
using CodeGen.IO;
using CodeGen.Mapping;
using CodeGen.Translation;

namespace CodeGen.Devices.RevPi
{
    // The Revolution Pi. It exists only when the run assigns components to it, so every stage asks the
    // plan rather than a configuration switch.
    public sealed class RevPiBackend : TargetBackend
    {
        // The row this instance emits, handed in by the composition root. A second controller of
        // this kind is another row, not another class.
        public RevPiBackend(Mapping.TargetDescriptor descriptor) : base(descriptor) { }

        // Its Modbus coupler reads a fixed set of signals, so only those components have IO here.
        public override System.Collections.Generic.IReadOnlySet<string> ServableComponents(
            Configuration.CompilerConfiguration cfg) =>
            RevPiIoBrokerInjector.CoveredComponents(cfg);

        // Its Modbus coupler carries a fixed set of channels, so a component assigned here that the
        // coupler cannot read would deploy with no IO and could never actuate. That is this target's
        // hardware contract, so this target is what refuses it.
        public override void ValidateAssignment(GenerationContext ctx)
        {
            if (!ctx.Profile.AssignsAnythingTo(Target)) return;
            var assigned = ctx.Profile.Assignments
                .Where(kv => kv.Value == Target).Select(kv => kv.Key).ToList();
            Validation.Plan.RevPiSelectionValidator.ThrowIfInvalid(ctx.Cfg, ctx.Profile, ctx.IoBearing(assigned));
        }

        public override void EmitDevice(GenerationContext ctx, DeviceScope scope, Action<string> log)
        {
            if (!ctx.Profile.HasAssignments) return;
            Stage("device emit", log, () =>
            {
                var report = new SystemInjector.BindingApplicationReport();
                RevPiDeviceEmitter.EmitDevice(ctx, Target, report);
                foreach (var m in report.Missing) log(m);
            });
        }

        public override void WireResource(GenerationContext ctx,
            SystemInjector.BindingApplicationReport report, Action<string> log)
        {
            if (!ctx.Profile.HasAssignments) return;
            Stage("resource wire", log, () => RevPiDeviceEmitter.WireResource(ctx, Target, report));
        }

        // ITS FEED IO: the Modbus broker on its own resource, carrying a Mapping to a matching
        // application instance, plus the .hcf whose LinkNames resolve to it. Without all three the
        // device deploys and no Feed actuator can read or write a channel.
        //
        // This used to sit in the generic syslay/sysres parity validator, which made a target-independent
        // pass the owner of one target's hardware contract - and put its findings through a retry that
        // re-syncs sysres PARAMETERS, which can never restore a missing broker or an unbound .hcf.
        public override void ValidateOutput(GenerationContext ctx, Action<string> log)
        {
            if (!ctx.Profile.HasAssignments) return;
            Stage("feed IO validate", log, () =>
            {
                var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(ctx.Cfg);
                if (string.IsNullOrEmpty(eaeRoot)) return;

                var problems = FeedIoProblems(ctx, eaeRoot!);
                foreach (var p in problems) log($"[RevPi][IO] {p}");
                if (problems.Count > 0)
                    throw new InvalidOperationException(
                        $"[RevPi][IO] {problems.Count} problem(s) with this target's Feed IO; the device " +
                        "would deploy with nothing able to read or write a channel.");
            });
        }

        List<string> FeedIoProblems(GenerationContext ctx, string eaeRoot)
        {
            var cfg = ctx.Cfg;
            var problems = new List<string>();
            var brokerFbId = RevPiIoBrokerInjector.BrokerFbId(cfg);
            const string brokerName = RevPiIoBrokerInjector.BrokerName;

            var sysdev = EaeProjectLayout.FindSysdevByDeviceTypeAndName(
                eaeRoot, Descriptor.DeviceType, Descriptor.DeviceName!);
            var sysres = sysdev == null ? null : EaeProjectLayout.FindSysresFor(sysdev);
            if (string.IsNullOrEmpty(sysres) || !System.IO.File.Exists(sysres))
            {
                problems.Add("components are assigned here but this target's sysres was not found — " +
                             "they have no deployable resource");
                return problems;
            }

            System.Xml.Linq.XElement? broker;
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(sysres);
                System.Xml.Linq.XNamespace ns = doc.Root!.GetDefaultNamespace();
                broker = doc.Descendants(ns + "FB")
                    .FirstOrDefault(e => (string?)e.Attribute("Name") == brokerName);
            }
            catch (Exception ex)
            {
                problems.Add($"unreadable sysres '{System.IO.Path.GetFileName(sysres)}': {ex.Message}");
                return problems;
            }

            if (broker == null)
                problems.Add($"the {brokerName} Modbus broker is not on this target's sysres — the " +
                             "assigned actuators would have no physical IO");
            else
            {
                // A resource FB with no Mapping is an ORPHAN: EAE has no application instance to bind it
                // to, which is the documented "Repair Instances" class.
                if (string.IsNullOrWhiteSpace((string?)broker.Attribute("Mapping")))
                    problems.Add($"{brokerName} has no Mapping attribute — it is an orphan resource " +
                                 "instance with no application-layer counterpart");
                // The .hcf's LinkNames are <resourceId>.<fbId>.<port>, so the FB id is load-bearing.
                if (!string.Equals((string?)broker.Attribute("ID"), brokerFbId, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{brokerName}'s FB ID is not {brokerFbId} — the Modbus .hcf LinkNames " +
                                 "resolve against that id and would not bind");
            }

            // The application layer must declare the broker too, else the sysres Mapping dangles.
            try
            {
                if (!SysresFbMirror.ReadTopLevelFbsWithSystemModelFallback(
                        ctx.Config.ActiveSyslayPath, cfg.Generation.ProjectNamespace)
                        .Any(f => string.Equals(f.Name, brokerName, StringComparison.Ordinal)))
                    problems.Add($"{brokerName} is missing from the generated syslay — the resource " +
                                 "instance has no application counterpart to map onto");
            }
            catch { /* an unreadable syslay is reported by the pass that owns it */ }

            var folder = System.IO.Path.GetDirectoryName(sysres);
            var hcf = folder != null && System.IO.Directory.Exists(folder)
                ? System.IO.Directory.EnumerateFiles(folder, "*.hcf").FirstOrDefault() : null;
            if (hcf == null)
                problems.Add("the Modbus .hcf is missing — EAE reports Missing Project Files");
            else if (!System.IO.File.ReadAllText(hcf).Contains(brokerFbId, StringComparison.Ordinal))
                problems.Add($"the .hcf's Modbus LinkNames do not resolve to the {brokerName} broker FB " +
                             "— the Feed IO would not bind");
            return problems;
        }
    }
}
