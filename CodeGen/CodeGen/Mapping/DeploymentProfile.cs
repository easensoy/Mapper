using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;

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
        IReadOnlyDictionary<string, int> TimerAcknowledged)
    {
        public bool TakesRingScopedSlot(string? name) =>
            RingScopedSlotReporters.Any(r => string.Equals(r, (name ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase));

        // Read from the declarative plant profile, which is where a deployment states these.
        public static PlantFacts Declared()
        {
            var c = RigCatalog.Current;
            return new PlantFacts(
                c.ComponentIdCeiling,
                c.Roles.TopCoverSensor,
                c.CrossRingSegment,
                c.SensorInterlocks.ToDictionary(s => s.Sensor, s => s.PresentState,
                    StringComparer.OrdinalIgnoreCase),
                c.SynthSensors.Select(s => s.Name).ToList(),
                c.Roles.TaskArm,
                c.FeedbackModes.Where(f => f.IsTimerAcknowledged)
                    .ToDictionary(f => f.Component, f => f.AckMs, StringComparer.OrdinalIgnoreCase));
        }
    }

    public sealed class DeploymentProfile
    {
        public LayoutCatalog Layout { get; }

        // The plant facts a PLAN needs, resolved once here. Planning reasons about capabilities and
        // reachability; which instance fills a role, and which components a carrier already spans,
        // are declarations of THIS deployment, so they enter the compiler through the profile.
        public PlantFacts Facts { get; }
        public IReadOnlySet<string> RevPiComponents { get; }

        public DeploymentProfile(IEnumerable<string> revPiComponents, LayoutCatalog layout)
            : this(revPiComponents, layout, PlantFacts.Declared())
        {
        }

        public DeploymentProfile(IEnumerable<string> revPiComponents, LayoutCatalog layout,
            PlantFacts facts)
        {
            Layout = layout ?? throw new ArgumentNullException(nameof(layout));
            Facts = facts ?? throw new ArgumentNullException(nameof(facts));
            var selected = new HashSet<string>(
                revPiComponents ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            // device.yml declares which components the RevPi coupler is the only reader of; hosting
            // anything there takes them along. A hardware contract of the target, not a name known here.
            if (selected.Count > 0)
                foreach (var required in DeviceConfig.Current.RevPi.AlwaysHosts)
                    selected.Add(required);
            RevPiComponents = selected;
        }

        public static DeploymentProfile M262Only(LayoutCatalog layout) =>
            new(Array.Empty<string>(), layout);

        // Some Feed components on the RevPi, the M262 keeping the rest.
        public bool PartialRevPi => RevPiComponents.Count > 0;

        public bool RunsOnRevPi(string? componentName) =>
            !string.IsNullOrEmpty(componentName) && RevPiComponents.Contains(componentName!);

        public override string ToString() =>
            PartialRevPi
                ? "RevPi:" + string.Join(",", RevPiComponents.OrderBy(n => n, StringComparer.Ordinal))
                : "M262";
    }
}
