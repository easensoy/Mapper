using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Domain.Twin;
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

    // One resource's planned infrastructure: the instance filling each role the target profile declares
    // for it, the processes allocated to it, and the adapter wires between roles it actually has. A role
    // the resource does not declare is absent, not null-by-convention.
    public sealed record ResourcePlan(
        PlcAssignment Plc,
        string Label,
        string? AreaFb,
        string? StationFb,
        string? StationHmiFb,
        string? ProcessFb,
        string? TerminatorFb,
        IReadOnlyList<(string Source, string Destination)> AdapterRelations);

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

        // The twin, parsed and resolved once. Every component, state, transition and condition reference
        // is indexed and bound here, so no later stage re-scans the component list to answer a question
        // the model already settles.
        public TwinModel Twin { get; }

        // A flat read-only VIEW of the twin, in declaration order. Not a second source of truth: it is
        // derived, and the only caller left is the HMI semantic builder. Delete it when that reads the
        // twin directly.
        public IReadOnlyList<VueOneComponent> Components => Twin.Components.Select(c => c.Source).ToList();

        // The station slice the layout emits: the roster's ordered sensors and actuators, resolved against
        // the twin. Order assigns every state_table id.
        public StationContents Station { get; }

        // The per-controller report rings fold into one; see FeedRingMerge for what makes a twin need it.
        public bool RingsMerged { get; }

        // The emitted FB Type per actuator, decided once. INVARIANTS I-4 requires deploy, parameters,
        // wiring and I/O binding to agree on this; they read it here rather than each resolving again.
        public IReadOnlyDictionary<string, string> CatTypes { get; }

        // The BX1 actuators that detour onto the M580 ring, in id order. Their place/remove folds into
        // the M580 recipes, so they are driven from there rather than a BX1-local ring.
        public IReadOnlyList<string> CoverDetour { get; }

        public bool IsCoverDetour(string? name) =>
            CoverDetour.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));

        // The Feed-controller nodes the discharge tail splices into the assembly ring, in order. Empty
        // when the twin declares no tail, which closes every ring locally. Decided once because the
        // syslay and the sysres must exclude exactly the same names: a node left on a locally-closed ring
        // while the segment also drives it gets two sources on one stateRprtCmd_in.
        public IReadOnlyList<string> CrossRingSegment { get; }

        // state_table slot the top-cover sensor reports on; see StateTableAllocation for why it is computed
        // rather than positional.
        public int TopCoverSensorSlot { get; }

        // Every reporter's state_table slot, processes included; see StateTableAllocation for why a
        // process slot can differ from the one the catalog pins.
        public IReadOnlyDictionary<string, int> Slots { get; }

        // The processes a controller runs, in the order the roster declares them -- which is the order the
        // layout emits and the wiring threads them.
        public IEnumerable<VueOneComponent> ProcessesOn(PlcAssignment plc) =>
            Layout.Components
                .Where(e => Allocation.Of(e.Name) == plc)
                .Select(e => Twin.ByName(e.Name))
                .Where(c => c is { IsProcess: true })
                .Select(c => c!.Source);

        // The planned infrastructure for one controller's resource. Roles come from the target profile,
        // the process from the twin via the allocation; neither is a name the emitter knows.
        public ResourcePlan ResourceFor(PlcAssignment plc)
        {
            var profile = Layout.Resources.FirstOrDefault(r => r.Plc == plc)
                ?? throw new InvalidOperationException(
                    $"[Layout] layout.yml declares no resource for {plc}, so its infrastructure is unknown.");
            string? Role(string role) => profile.Roles.TryGetValue(role, out var n) ? n : null;

            var wires = new List<(string, string)>();
            foreach (var rel in Layout.ResourceRelations)
            {
                var from = Role(rel.From); var to = Role(rel.To);
                if (from == null || to == null) continue;
                wires.Add(($"{from}.{rel.FromPort}", $"{to}.{rel.ToPort}"));
            }

            return new ResourcePlan(plc, profile.Label,
                Role("area"), Role("station"), Role("stationHmi"),
                ProcessesOn(plc).FirstOrDefault()?.Name?.Trim(),
                Role("terminator"), wires);
        }

        // Every process-to-process handoff the twin declares, with its transport resolved.
        internal ProcessHandoffPlan Handoffs { get; }

        // The compiled recipe per process, keyed by the twin's own component name.
        public IReadOnlyDictionary<string, RecipeArrays> Recipes { get; }

        private GenerationContext(MapperConfig config, DeploymentProfile profile,
            DeploymentRoster roster, ControllerAllocation allocation,
            TwinModel twin, StationContents station,
            bool ringsMerged, IReadOnlyDictionary<string, string> catTypes, IReadOnlyList<string> coverDetour,
            IReadOnlyList<string> crossRingSegment, int topCoverSensorSlot,
            IReadOnlyDictionary<string, int> slots,
            ProcessHandoffPlan handoffs, IReadOnlyDictionary<string, RecipeArrays> recipes)
        {
            Config = config;
            Profile = profile;
            Roster = roster;
            Allocation = allocation;
            Twin = twin;
            Station = station;
            RingsMerged = ringsMerged;
            CatTypes = catTypes;
            CoverDetour = coverDetour;
            CrossRingSegment = crossRingSegment;
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
            // Resolve the twin before anything derives from it: a model whose references do not close is
            // rejected here rather than silently losing whatever the dangling reference asked for.
            var twin = TwinModel.Build(components);
            var roster = new DeploymentRoster(profile);
            var allocation = new ControllerAllocation(roster);
            RejectUnallocatedComponents(components, allocation);

            var station = ResolveStation(components, roster);
            var catTypes = station.Actuators.ToDictionary(
                a => a.Name.Trim(), TemplateMap.ResolveActuatorCatType, StringComparer.OrdinalIgnoreCase);
            var coverDetour = station.Actuators
                .Select(a => (a.Name ?? string.Empty).Trim())
                .Where(n => allocation.IsOn(n, PlcAssignment.BX1)).ToList();
            bool ringsMerged = FeedRingMerge.Needed(twin, allocation);
            // The tail only exists if the twin declares the actuators it is made of. Its sensor member is
            // synthesised later, so it is not part of the test.
            var crossRingSegment = TemplateMap.M262CrossRingSegment(
                HandoffPlanner.DischargeActive &&
                TemplateMap.M262CrossRingSegment(true)
                    .Where(n => !RigCatalog.Current.SynthSensors.Any(
                        s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase)))
                    .All(n => station.Actuators.Any(
                        a => string.Equals(a.Name?.Trim(), n, StringComparison.OrdinalIgnoreCase))));
            var slots = StateTableAllocation.Slots(station, allocation, ringsMerged);
            int topCover = TemplateMap.TopCoverSensorNames
                .Select(n => slots.TryGetValue(n, out int s) ? s : -1).FirstOrDefault(s => s >= 0, -1);

            var handoffs = ProcessRecipeArrayGenerator.HandoffPlan(twin, slots, allocation, ringsMerged);

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
                    process, station, twin, slots, allocation, ringsMerged, topCover);
            }

            return new GenerationContext(config, profile, roster, allocation,
                twin, station, ringsMerged, catTypes, coverDetour, crossRingSegment, topCover, slots, handoffs, recipes);
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
            ComponentType.IsActuator(c) || ComponentType.IsSensor(c) || ComponentType.Is(c, ComponentType.Robot);

        // The roster's ordered sensors and actuators resolved against the twin. The ORDER is the
        // roster's, because it assigns every state_table id.
        private static StationContents ResolveStation(
            IReadOnlyList<VueOneComponent> components, DeploymentRoster roster)
        {
            VueOneComponent? ByName(string name, params string[] types) =>
                components.FirstOrDefault(c =>
                    types.Any(t => string.Equals(c.Type, t, StringComparison.OrdinalIgnoreCase)) &&
                    string.Equals(c.Name, name, StringComparison.Ordinal));

            var process = roster.All
                .Where(e => ControllerMap.IsFeedController(e.Plc))
                .Select(e => ByName(e.Name, ComponentType.Process))
                .FirstOrDefault(p => p != null)
                ?? throw new InvalidOperationException(
                    "The roster allocates no Process to the Feed controller, so there is no station to emit.");

            return new StationContents(process,
                roster.IdOrderActuators.Select(n => ByName(n, ComponentType.Actuator, ComponentType.Robot))
                    .Where(a => a != null).Select(a => a!).ToList(),
                roster.IdOrderSensors.Select(n => ByName(n, ComponentType.Sensor))
                    .Where(s => s != null).Select(s => s!).ToList());
        }
    }
}
