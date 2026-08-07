using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Models;
using CodeGen.Translation.Process;
using CodeGen.Translation.Process.Recipes;

namespace CodeGen.Translation
{
    // The station slice a layout emits. Order is load-bearing: it assigns every state_table id.
    public record StationContents(
        VueOneComponent Process,
        List<VueOneComponent> Actuators,
        List<VueOneComponent> Sensors);

    // Everything one generation decided before any artefact was written. Parse once, plan once, render
    // once: the twin is read a single time, every derivation happens here, and the emitters are handed the
    // answers rather than re-deriving them from a static or from a file already on disk. Constructed at
    // the entry point and passed explicitly, so a second generation cannot read the first one's answers.
    public sealed class GenerationContext
    {
        public MapperConfig Config { get; }
        public DeploymentProfile Profile { get; }

        // Where each component runs and is drawn. Carried on the profile, so nothing below this point
        // reaches for the catalog.
        public LayoutCatalog Layout => Profile.Layout;
        public DeploymentRoster Roster { get; }
        public ControllerAllocation Allocation { get; }

        // The twin, parsed once.
        public IReadOnlyList<VueOneComponent> Components { get; }

        // The station slice the layout emits: the roster's ordered sensors and actuators, resolved against
        // the twin. Order assigns every state_table id.
        public StationContents Station { get; }

        // The per-controller report rings fold into one; see FeedRingMerge for what makes a twin need it.
        public bool RingsMerged { get; }

        // state_table slot the top-cover sensor reports on; see StateTableAllocation for why it is computed
        // rather than positional.
        public int TopCoverSensorSlot { get; }

        // Every reporter's state_table slot, processes included; see StateTableAllocation for why a
        // process slot can differ from the one the catalog pins.
        public IReadOnlyDictionary<string, int> Slots { get; }

        // Every process-to-process handoff the twin declares, with its transport resolved.
        internal ProcessHandoffPlan Handoffs { get; }

        // The compiled recipe per process, keyed by the twin's own component name.
        public IReadOnlyDictionary<string, RecipeArrays> Recipes { get; }

        private GenerationContext(MapperConfig config, DeploymentProfile profile,
            DeploymentRoster roster, ControllerAllocation allocation,
            IReadOnlyList<VueOneComponent> components, StationContents station,
            bool ringsMerged, int topCoverSensorSlot, IReadOnlyDictionary<string, int> slots,
            ProcessHandoffPlan handoffs, IReadOnlyDictionary<string, RecipeArrays> recipes)
        {
            Config = config;
            Profile = profile;
            Roster = roster;
            Allocation = allocation;
            Components = components;
            Station = station;
            RingsMerged = ringsMerged;
            TopCoverSensorSlot = topCoverSensorSlot;
            Slots = slots;
            Handoffs = handoffs;
            Recipes = recipes;
        }

        // Read the twin and derive everything the run needs. Nothing here touches the deployed tree, so
        // planning completes before the first artefact is written and a model the backend cannot express
        // fails with a diagnostic instead of a half-generated project.
        public static GenerationContext Plan(MapperConfig config, string controlXmlPath, DeploymentProfile profile)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (string.IsNullOrWhiteSpace(controlXmlPath))
                throw new ArgumentException("Control.xml path is required.", nameof(controlXmlPath));
            if (!System.IO.File.Exists(controlXmlPath))
                throw new System.IO.FileNotFoundException($"Control.xml not found: {controlXmlPath}", controlXmlPath);

            var components = new CodeGen.IO.SystemXmlReader().ReadAllComponents(controlXmlPath);
            var roster = new DeploymentRoster(profile);
            var allocation = new ControllerAllocation(roster);
            RejectUnallocatedComponents(components, allocation);

            var station = ResolveStation(components, roster);
            bool ringsMerged = FeedRingMerge.Needed(components, allocation);
            var slots = StateTableAllocation.Slots(station, allocation, ringsMerged);
            int topCover = TemplateMap.TopCoverSensorNames
                .Select(n => slots.TryGetValue(n, out int s) ? s : -1).FirstOrDefault(s => s >= 0, -1);

            var handoffs = ProcessRecipeArrayGenerator.HandoffPlan(components, slots, allocation, ringsMerged);

            // Compile every process the twin declares, in one pass, so the layout and the sysres mirror read
            // the same rows rather than each asking for them again.
            var recipes = new Dictionary<string, RecipeArrays>(StringComparer.OrdinalIgnoreCase);
            foreach (var process in components.Where(ComponentType.IsProcess))
            {
                var name = process.Name?.Trim() ?? string.Empty;
                if (!slots.ContainsKey(name))
                    throw new InvalidOperationException(
                        $"[state_table] Process '{name}' has no slot. Add it to processSlots in " +
                        "Config/smc-rig.yml; without one it cannot announce its phase and every peer " +
                        "waiting on it would block.");
                recipes[name] = ProcessRecipeArrayGenerator.Generate(
                    process, station, components, slots, allocation, ringsMerged, topCover);
            }

            return new GenerationContext(config, profile, roster, allocation,
                components, station, ringsMerged, topCover, slots, handoffs, recipes);
        }

        // A component the roster does not place has no controller, no canvas position and no state_table
        // slot, so every later step would skip it without a word. Fail here, naming all of them at once.
        private static void RejectUnallocatedComponents(
            IReadOnlyList<VueOneComponent> components, ControllerAllocation allocation)
        {
            var unallocated = components
                .Where(c => ComponentType.IsProcess(c) || IsPlaceable(c))
                .Where(c => allocation.Of(c.Name) == PlcAssignment.Unknown)
                .Select(c => $"'{c.Name}' (Type={c.Type})")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            if (unallocated.Count == 0) return;
            throw new InvalidOperationException(
                $"[Deployment] Control.xml declares {unallocated.Count} component(s) the deployment roster " +
                $"does not place: {string.Join(", ", unallocated)}. Each needs a row in " +
                "Config/layout.yml naming the controller that runs it, or an alias to one that has it; " +
                "without one it has no controller, no canvas position and no state_table slot, and every " +
                "later step would skip it silently.");
        }

        private static bool IsPlaceable(VueOneComponent c) =>
            c.Type is "Actuator" or "Sensor" or "Robot";

        // The roster's ordered sensors and actuators resolved against the twin. The ORDER is the
        // roster's, because it assigns every state_table id.
        private static StationContents ResolveStation(
            IReadOnlyList<VueOneComponent> components, DeploymentRoster roster)
        {
            var process = SystemInjector.FindStation1Process(components.ToList())
                ?? throw new InvalidOperationException(
                    "No Process referencing a 'Feeder' actuator was found in Control.xml.");

            VueOneComponent? ByName(string name, params string[] types) =>
                components.FirstOrDefault(c =>
                    types.Any(t => string.Equals(c.Type, t, StringComparison.OrdinalIgnoreCase)) &&
                    string.Equals(c.Name, name, StringComparison.Ordinal));

            return new StationContents(process,
                roster.IdOrderActuators.Select(n => ByName(n, "Actuator", "Robot"))
                    .Where(a => a != null).Select(a => a!).ToList(),
                roster.IdOrderSensors.Select(n => ByName(n, "Sensor"))
                    .Where(s => s != null).Select(s => s!).ToList());
        }
    }
}
