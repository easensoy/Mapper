using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Translation;

namespace CodeGen.Mapping
{
    // The deployment inputs for THIS run: the catalog saying where every component runs and is drawn, plus
    // which Feed components the operator moved onto the RevPi. A whole-station swap is not expressible:
    // PLC_RW_REVPI carries IO for a fixed subset only, so the selection is one set, not a mode plus a set.
    // What THIS plant declares about itself, in capability terms. Every member answers a question
    // planning asks generically; none of them names a controller, a CAT or an EAE artefact.
    public sealed record PlantFacts(
        // Highest slot in the positional component range; reserved slots sit above it.
        int ComponentIdCeiling,
        // Reporters whose slot is chosen from what is FREE on their own ring rather than
        // positionally, because they report onto a ring their position does not determine.
        IReadOnlyList<string> RingScopedSlotReporters,
        // Components a declared carrier already spans, and so can transport a dependency across.
        IReadOnlyList<string> CarrierSegment,
        // A sensor and the value that means "asserted", which polarity alone cannot tell.
        IReadOnlyDictionary<string, int> SensorAssertedLevel,
        // Reporters this deployment injects that the twin does not declare.
        IReadOnlyList<string> InjectedReporters,
        // The instance running a task handshake rather than a stop sequence, if any.
        string TaskArmInstance,
        // Instances whose arrival the plant declares un-sensed, with the acknowledge time.
        IReadOnlyDictionary<string, int> TimerAcknowledged,
        // What a cross-process reference means where the recipe cannot simply wait for the phase.
        HandoffPolicy Handoff)
    {
        public bool TakesRingScopedSlot(string? name) =>
            RingScopedSlotReporters.Any(r => string.Equals(r, (name ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase));

        // Read from the declarative plant profile, which is where a deployment states these. The
        // catalog is handed in: a profile built from one snapshot and facts read from another would
        // be two configurations inside one run.
        public static PlantFacts Declared(RigCatalog c)
        {
            return new PlantFacts(
                c.ComponentIdCeiling,
                c.Roles.TopCoverSensor,
                c.CrossRingSegment,
                c.SensorInterlocks.ToDictionary(s => s.Sensor, s => s.PresentState,
                    StringComparer.OrdinalIgnoreCase),
                c.SynthSensors.Select(s => s.Name).ToList(),
                c.Roles.TaskArm,
                c.FeedbackModes.Where(f => f.IsTimerAcknowledged)
                    .ToDictionary(f => f.Component, f => f.AckMs, StringComparer.OrdinalIgnoreCase),
                c.Handoff);
        }
    }

    // The deployment inputs for THIS run: the catalog saying where every component runs and is drawn,
    // the plant's own declarations, and the ASSIGNMENTS this run makes - components the operator moved
    // off the target layout.yml places them on.
    //
    // An assignment is generic: a component name and the target it runs on. Nothing here knows which
    // controller that is, so moving a component to a target that exists is a roster decision and
    // nothing in the compiler changes.
    public sealed class DeploymentProfile
    {
        public LayoutCatalog Layout { get; }

        // The plant facts a PLAN needs, resolved once here. Planning reasons about capabilities and
        // reachability; which instance fills a role, and which components a carrier already spans,
        // are declarations of THIS deployment, so they enter the compiler through the profile.
        public PlantFacts Facts { get; }

        // Component -> the target it runs on, for components this run moved off their layout row.
        public IReadOnlyDictionary<string, PlcAssignment> Assignments { get; }

        // Built FROM the run's declarations: the layout it places against and the plant facts it
        // compiles against come from one snapshot, so they cannot describe two different rigs.
        // facts overrides what the rig catalog declares, for a plant that states its own - a synthetic
        // one built in code, for instance. Left null it is the declared profile, which is the rig.
        public DeploymentProfile(IReadOnlyDictionary<string, PlcAssignment> assignments,
            Configuration.CompilerConfiguration cfg, PlantFacts? facts = null)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var devices = cfg.Devices;
            Layout = cfg.Layout;
            Facts = facts ?? PlantFacts.Declared(cfg.Rig);

            var placed = new Dictionary<string, PlcAssignment>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in assignments ?? new Dictionary<string, PlcAssignment>())
            {
                if (!TargetRegistry.IsRegistered(kv.Value))
                    throw new ArgumentException(
                        $"[Deployment] '{kv.Key}' is assigned to {kv.Value}, which no backend implements.",
                        nameof(assignments));
                placed[kv.Key] = kv.Value;
            }
            // A target may declare components its own hardware is the only reader of; hosting anything
            // there takes them along. That is the TARGET's hardware contract, declared in device.yml.
            foreach (var target in placed.Values.Distinct().ToList())
                foreach (var required in devices.AlwaysHostedBy(target))
                    placed[required] = target;
            Assignments = placed;
        }

        // Every component keeps its layout row: nothing is moved.
        public static DeploymentProfile AsPlaced(Configuration.CompilerConfiguration cfg,
            PlantFacts? facts = null) =>
            new(new Dictionary<string, PlcAssignment>(), cfg, facts);

        // THE BOUNDARY ADAPTER. MapperUI and the prebuilt VueOne runner send a SET of component names
        // bound for the one target that exists to receive components moved off another - that is the
        // shape their binary contract has always had. It becomes generic assignments here, once, so
        // nothing inside the compiler works in terms of a particular controller.
        public static DeploymentProfile Relocating(
            IEnumerable<string>? names, Configuration.CompilerConfiguration cfg, PlantFacts? facts = null)
        {
            var target = TargetRegistry.All.FirstOrDefault(t => t.ReceivesRelocatedComponents)?.Plc;
            var map = new Dictionary<string, PlcAssignment>(StringComparer.OrdinalIgnoreCase);
            if (target != null)
                foreach (var name in names ?? Array.Empty<string>()) map[name] = target.Value;
            return new DeploymentProfile(map, cfg, facts);
        }

        // Whether this run moved anything at all.
        public bool HasAssignments => Assignments.Count > 0;

        // The same question under the name the HMI module compiles against. That module is owned by a
        // separate session, so its vocabulary is kept here rather than edited from this side; it is one
        // expression forwarding to the one above, not a second answer.
        public bool PartialRevPi => HasAssignments;

        // The target this run runs a component on, or null where its layout row stands.
        public PlcAssignment? AssignedTarget(string? componentName) =>
            componentName != null && Assignments.TryGetValue(componentName.Trim(), out var plc)
                ? plc : null;

        // Whether this run assigns anything to a target - which is what makes a target that exists
        // only to receive relocated components part of this run.
        public bool AssignsAnythingTo(PlcAssignment plc) => Assignments.Values.Contains(plc);

        public override string ToString() =>
            HasAssignments
                ? string.Join(",", Assignments.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => $"{kv.Key}->{kv.Value}"))
                : "as placed";
    }
}
