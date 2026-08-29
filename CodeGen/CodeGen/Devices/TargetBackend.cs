using System;
using System.IO;
using CodeGen.Configuration;
using CodeGen.Devices.Core;
using CodeGen.IO;
using CodeGen.Mapping;
using CodeGen.Translation;

namespace CodeGen.Devices
{
    // One target's hardware behaviour, in one place. The pipeline drives the run and never names a
    // controller: it asks every registered backend, in registration order, to take its turn.
    //
    // What a target IS stays data - device.yml gives it a device type, a resource name, an authored
    // hardware config and its capability flags - so a component or a process on an existing target is a
    // roster row and nothing here changes. What a target DOES is typed C#, because emitting a device is
    // executable behaviour rather than a value: genuinely new hardware earns a class.
    public interface ITargetBackend
    {
        PlcAssignment Target { get; }

        // The components this target's own hardware can serve, or empty when it serves whatever the
        // roster puts on it. A coupler carries the channels it carries, so the answer is the TARGET's -
        // the UI and the validator both ask here rather than each naming a particular injector.
        //
        // It takes the run's declarations because the coupler is resolved from the library THAT run
        // points at: as a stored property it was resolved once per process and every later run,
        // whatever bundle it held, was handed the first one's answer.
        System.Collections.Generic.IReadOnlySet<string> ServableComponents(
            Configuration.CompilerConfiguration cfg);

        // Whether this target can actually serve what the run assigned to it. Its own hardware decides -
        // a coupler carries the channels it carries - so the answer lives with the target rather than in
        // the compiler. Runs after the plan and BEFORE anything is written, so an unsupportable
        // assignment costs nothing.
        void ValidateAssignment(GenerationContext ctx);

        // The device: its sysdev, its resource shell and the topology that declares it to EAE.
        void EmitDevice(GenerationContext ctx, DeviceScope scope, Action<string> log);

        // The authored hardware configuration, carried into the device folder and re-rooted.
        void CopyHardwareConfig(GenerationContext ctx, Action<string> log);

        // The resource's own FB network: init chain, station chain and report ring.
        void WireResource(GenerationContext ctx, SystemInjector.BindingApplicationReport report,
            Action<string> log);

        // Physical channels bound to the FBs this target hosts.
        void BindHardware(GenerationContext ctx, IoBindings? bindings,
            SystemInjector.BindingApplicationReport report, Action<string> log);

        // Anything this target must do to the shared canvas AFTER every resource is wired - an IO
        // broker whose symlinks resolve to FBs the wiring created, for instance. On the backend so the
        // pipeline drives the run without knowing which target has such a thing.
        void FinishApplication(GenerationContext ctx, string syslayPath,
            SystemInjector.BindingApplicationReport report, Action<string> log);

        // The last word on this target's own artefacts, once everything is written.
        void ValidateOutput(GenerationContext ctx, Action<string> log);
    }

    // What every device emit needs and none of them should derive twice: where the project is, which
    // System folder holds its devices, and the solution identity they all tag themselves with.
    public sealed record DeviceScope(string EaeRoot, string SystemGuidDir, string SolutionId)
    {
        // No scope means no device can be written at all. Skipping would leave an application with
        // nothing to run it on and still report a generated project, so the run STOPS here.
        public static DeviceScope Open(Configuration.CompilerConfiguration cfg)
        {
            var root = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            if (string.IsNullOrEmpty(root))
                throw new InvalidOperationException(
                    "[Device] the EAE project root cannot be derived from the configured project, so no " +
                    "device can be emitted.");
            var guidDir = EaeProjectLayout.FindSystemGuidDir(root);
            if (guidDir == null)
                throw new InvalidOperationException(
                    $"[Device] no System GUID folder under {Path.Combine(root, "IEC61499", "System")}, so " +
                    "no device can be emitted.");
            return new DeviceScope(root, guidDir, EaeProjectLayout.ReadProjectGuid(root) ?? string.Empty);
        }
    }

    // Nothing to do for this stage on this target. Stated once here rather than as an empty method on
    // every backend, so a backend's file shows only what that target actually does.
    public abstract class TargetBackend : ITargetBackend
    {
        // THE ROW THAT DECLARED THIS TARGET, resolved once by the factory that composed the backend.
        // Each backend used to declare an identical Target property and a one-line constructor, and
        // then ask the snapshot for its own descriptor again per stage - the same question, five times,
        // with five chances to ask it of a different snapshot.
        protected Mapping.TargetDescriptor Descriptor { get; }

        protected TargetBackend(Mapping.TargetDescriptor descriptor) =>
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));

        public PlcAssignment Target => Descriptor.Plc;

        // Most targets serve whatever the roster puts on them; only one with its own IO contract lists.
        public virtual System.Collections.Generic.IReadOnlySet<string> ServableComponents(
            Configuration.CompilerConfiguration cfg) =>
            new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        // Most targets serve whatever the roster puts on them; only one with its own IO contract says no.
        public virtual void ValidateAssignment(GenerationContext ctx) { }

        public virtual void EmitDevice(GenerationContext ctx, DeviceScope scope, Action<string> log) { }

        public virtual void CopyHardwareConfig(GenerationContext ctx, Action<string> log) { }

        public virtual void WireResource(GenerationContext ctx,
            SystemInjector.BindingApplicationReport report, Action<string> log) { }

        public virtual void BindHardware(GenerationContext ctx, IoBindings? bindings,
            SystemInjector.BindingApplicationReport report, Action<string> log) { }

        public virtual void FinishApplication(GenerationContext ctx, string syslayPath,
            SystemInjector.BindingApplicationReport report, Action<string> log) { }

        public virtual void ValidateOutput(GenerationContext ctx, Action<string> log) { }

        // Every stage here is REQUIRED: a backend only declares one for work its target genuinely needs
        // (an optional step is simply not overridden). A failed stage leaves a partial device, and a
        // partial device deploys - so the failure is reported against its target and then ABORTS the run
        // rather than letting later stages write over a broken one and the pipeline report success.
        protected void Stage(string stage, Action<string> log, Action work)
        {
            try { work(); }
            catch (TargetStageException) { throw; }
            catch (Exception ex)
            {
                log($"[{Target}][Error] {stage}: {ex.Message}");
                throw new TargetStageException(Target, stage, ex);
            }
        }
    }

    // Names the target, the stage and the original cause, so a failure says which half of which device
    // is unfinished without reading the log back.
    public sealed class TargetStageException : InvalidOperationException
    {
        public TargetStageException(PlcAssignment target, string stage, Exception cause)
            : base($"[{target}] {stage} FAILED: {cause.Message}", cause)
        {
            Target = target;
            Stage = stage;
        }

        public PlcAssignment Target { get; }
        public string Stage { get; }
    }
}
