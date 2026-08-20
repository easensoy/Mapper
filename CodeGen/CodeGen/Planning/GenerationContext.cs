using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Domain.Twin;
using CodeGen.Translation.Process;
using CodeGen.Translation.Interlocks;
using CodeGen.Translation.Process.Recipes;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Models;

namespace CodeGen.Translation
{
    // The station slice a layout emits. Order is load-bearing: it assigns every state_table id.
    public record StationContents(
        VueOneComponent Process,
        List<VueOneComponent> Actuators,
        List<VueOneComponent> Sensors)
    {
        // Every process the twin declares, in declaration order; each announces its phase on a slot.
        public IReadOnlyList<string> Processes { get; init; } = System.Array.Empty<string>();

        // Components the roster gives an idRank. Anything else is APPENDED above them, so a new component
        // cannot renumber one, which would repoint every recipe Wait1Id, interlock SourceID and HCF binding.
        public IReadOnlySet<string> Ranked { get; init; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    // One infrastructure FB a resource needs, fully decided: role, name, template, position, parameters.
    // The emitter renders these; it does not know that an area is called Area or is an Area_CAT.
    public sealed record InfraInstance(
        string Role,
        string Name,
        TemplateType Template,
        string Namespace,
        int X,
        int Y,
        IReadOnlyDictionary<string, string> Parameters);

    // What a resource DOES in the generated topology. Emitters branch on these, never on Label: a label
    // is diagnostic text an engineer may rename in layout.yml, and renaming it must not move a wire.
    public sealed record ResourceCapabilities(
        // Sysres canvas is device-local, so FBs translate to a local origin (the app canvas stays global).
        bool DeviceLocalCanvas,
        // Receives components relocated off another controller, so they must NOT be swept from its sysres.
        bool ReceivesRelocatedComponents,
        // Hands the cover detour out to another controller, so its ring closes across the seam.
        bool OpensCoverSeam,
        // Carries the cover chain itself, open at both ends because another controller commands it.
        bool CarriesDetouredChain,
        // Hosts the Feed ring, so a merged ring closes through its own seam.
        bool HostsFeedRing);

    // One actuator instance's motion contract. Sensed means a real arrival DI closes the wait;
    // timed means the CAT acknowledges after the leg's own duration.
    public sealed record ActuatorTiming(
        bool WorkSensorFitted, bool HomeSensorFitted, int ToWorkMs, int ToHomeMs)
    {
        // A watchdog has to outlast the motion it guards, or it faults every healthy stroke.
        public int FaultWorkMs => ToWorkMs * 2;
        public int FaultHomeMs => ToHomeMs * 2;
    }

    public sealed record ResourcePlan(
        PlcAssignment Plc,
        string Label,
        string? AreaFb,
        string? StationFb,
        string? ProcessFb,
        string? TerminatorFb,
        // Head of the report ring this resource participates in. Bring-up follows that ring, so anything
        // added to it later hangs off this rather than an emitter naming a component to init from.
        string? InitAnchor,
        IReadOnlyList<(string Source, string Destination)> AdapterRelations,
        IReadOnlyList<InfraInstance> Infrastructure,
        (string From, string To)? StationChain,
        ResourceCapabilities Capabilities)
    {
        // The instance filling one infrastructure role on this resource, or null where it declares none.
        public InfraInstance? Infra(string role) =>
            Infrastructure.FirstOrDefault(i => string.Equals(i.Role, role, StringComparison.OrdinalIgnoreCase));
    }

    // Everything one generation decided before any artefact was written. Parse once, plan once, render
    // once. Passed explicitly, so a second generation cannot read the first one's answers.
    public sealed class GenerationContext
    {
        public MapperConfig Config { get; }
        public DeploymentProfile Profile { get; }

        // Where each component runs and is drawn; on the profile, so nothing below reaches the catalog.
        public LayoutCatalog Layout => Profile.Layout;
        public DeploymentRoster Roster { get; }
        public ControllerAllocation Allocation { get; }

        // The twin, parsed and resolved once; no later stage re-scans the component list.
        public TwinModel Twin { get; }

        // A derived read-only VIEW of the twin, in declaration order; not a second source of truth.
        public IReadOnlyList<VueOneComponent> Components { get; }

        // The roster's ordered sensors and actuators, resolved against the twin. Order assigns every id.
        public StationContents Station { get; }

        // Where every announcement circulates and how each cross-boundary dependency is carried.
        public ReportGraph Rings { get; }

        // The emitted FB Type per actuator, decided once. INVARIANTS I-4 requires deploy, parameters,
        // wiring and I/O binding to agree on this; they read it here rather than each resolving again.
        public IReadOnlyDictionary<string, string> CatTypes { get; }

        // BX1 actuators that detour onto the M580 ring, in id order; driven from the M580 recipes.
        public IReadOnlyList<string> DetouredChain { get; }

        public bool IsDetoured(string? name) =>
            DetouredChain.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));

        // Feed-controller nodes the discharge tail splices into the assembly ring, in order. The syslay and
        // the sysres must exclude exactly the same names, else a node gets two sources on one stateRprtCmd_in.
        public IReadOnlyList<string> CrossRingSegment { get; }

        // state_table slot the top-cover sensor reports on; see StateTableAllocation for why it is computed.

        // Every reporter's state_table slot, processes included; see StateTableAllocation.
        public IReadOnlyDictionary<string, int> Slots { get; }

        // state_table size this plan needs; every FBT that declares state_table is patched to exactly this.
        public int StateTableCapacity => StateTableAllocation.Required(Slots);

        // The processes a controller runs, in roster order, which is the order the layout emits them.
        public IEnumerable<VueOneComponent> ProcessesOn(PlcAssignment plc) =>
            Roster.All
                .Where(e => e.Plc == plc)
                .Select(e => Twin.ByName(e.Name))
                .Where(c => c is { IsProcess: true })
                .Select(c => c!.Source);

        // The report ring a resource participates in, named by a component it hosts. A resource with no
        // components of its own takes the ring of the target hosting the station it belongs to.
        private string RingDomainOf(PlcAssignment plc)
        {
            var own = Station.Sensors.Concat(Station.Actuators)
                .Select(c => (c.Name ?? string.Empty).Trim())
                .FirstOrDefault(n => n.Length > 0 && Allocation.Of(n) == plc);
            return Rings.Domain(own ?? plc.ToString());
        }

        public ResourcePlan ResourceFor(PlcAssignment plc)
        {
            var profile = Layout.Resources.FirstOrDefault(r => r.Plc == plc)
                ?? throw new InvalidOperationException(
                    $"[Layout] layout.yml declares no resource for {plc}, so its infrastructure is unknown.");
            string? Role(string role) => profile.Roles.TryGetValue(role, out var n) ? n : null;

            var wires = new List<(string, string)>();
            (string From, string To)? chain = null;
            foreach (var rel in Layout.ResourceRelations)
            {
                var from = Role(rel.From); var to = Role(rel.To);
                if (from == null || to == null) continue;
                var endpoints = ($"{from}.{rel.FromPort}", $"{to}.{rel.ToPort}");
                if (rel.IsChain) chain = endpoints; else wires.Add(endpoints);
            }

            // Geometry is the roster's, so the emitter carries no coordinates of its own.
            var infra = new List<InfraInstance>();
            foreach (var role in InfraRoleOrder)
            {
                var name = Role(role);
                if (name == null) continue;
                var template = TemplateManifest.ForInfraRole(role);
                var row = Roster.All.FirstOrDefault(e =>
                    string.Equals(e.Name, name, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException(
                        $"[Layout] resource {plc} names '{name}' as its {role}, but layout.yml gives it no row, " +
                        "so it has nowhere to be drawn.");
                var parameters = template.NameParameter == null
                    ? (IReadOnlyDictionary<string, string>)EmptyParameters
                    : new Dictionary<string, string> { [template.NameParameter] = Iec61499Literal.FormatString(name) };
                infra.Add(new InfraInstance(role, name, template, InfraNamespace, row.X, row.Y, parameters));
            }

            var anchor = Station.Sensors.Concat(Station.Actuators)
                .Select(c => (c.Name ?? string.Empty).Trim())
                .FirstOrDefault(n => n.Length > 0 &&
                    string.Equals(Rings.Domain(n), RingDomainOf(plc), StringComparison.Ordinal));

            return new ResourcePlan(plc, profile.Label,
                Role("area"), Role("station"),
                ProcessesOn(plc).FirstOrDefault()?.Name?.Trim(),
                Role("terminator"), anchor, wires, infra, chain, CapabilitiesOf(plc));
        }

        // Topology role per controller, decided here and never from the resource Label, which is display
        // text. Feed may be hosted by M262 or, under a partial swap, by the RevPi.
        private ResourceCapabilities CapabilitiesOf(PlcAssignment plc)
        {
            var target = TargetRegistry.Of(plc);
            return new ResourceCapabilities(
                DeviceLocalCanvas:           target.DeviceLocalCanvas,
                ReceivesRelocatedComponents: target.ReceivesRelocatedComponents,
                // Only when the covers actually detour, and only from the target that hands them out.
                OpensCoverSeam:              DetouredChain.Count > 0 && target.OpensCoverSeam,
                // Open at both ends either because another controller commands the chain, or because
                // this run relocated components here and their own process stayed where it was.
                CarriesDetouredChain:        target.CarriesDetouredChain ||
                                             (target.ReceivesRelocatedComponents && Profile.PartialRevPi),
                HostsFeedRing:               target.HostsFeedStation && !target.ReceivesRelocatedComponents);
        }

        // The order a resource's stack is declared in, which is the order it is emitted in.
        private static readonly string[] InfraRoleOrder =
            { "areaHmi", "area", "station", "stationHmi", "terminator", "areaTerminator" };

        // Every infrastructure composite the Mapper emits lives in the project's own namespace.
        private const string InfraNamespace = "Main";

        private static readonly Dictionary<string, string> EmptyParameters = new(StringComparer.Ordinal);

        // Every process-to-process handoff the twin declares, with its transport resolved.
        public ProcessHandoffPlan Handoffs { get; }

        // Everything compiling a recipe needs, resolved ONCE; the compiler never re-asks the rig catalog,
        // the allocation or the topology per process.
        internal ProcessCompiler.Ctx RecipeInputs { get; }

        // The compiled recipe per process, keyed by the twin's own component name.
        public IReadOnlyDictionary<string, RecipeArrays> Recipes { get; }

        // Every actuator's interlock rules, keyed by component name.
        public IReadOnlyDictionary<string, InterlockPlan> Interlocks { get; }

        // Guards the twin states as a choice that a linear recipe had to satisfy in full, and any
        // other semantics the backend could not represent. Read by the run, never by an emitter.
        public IReadOnlyList<string> SemanticFindings { get; }

        // What the plan decided about each actuator instance: whether its arrival is SENSED or
        // timed, and how long each leg is allowed. The emitter formats these; it decides none.
        public IReadOnlyDictionary<string, ActuatorTiming> ActuatorTiming { get; }

        private GenerationContext(MapperConfig config, DeploymentProfile profile,
            DeploymentRoster roster, ControllerAllocation allocation,
            TwinModel twin, StationContents station,
            ReportGraph rings, IReadOnlyDictionary<string, string> catTypes, IReadOnlyList<string> detouredChain,
            IReadOnlyList<string> crossRingSegment,
            IReadOnlyDictionary<string, int> slots,
            ProcessCompiler.Ctx recipeInputs, ProcessHandoffPlan handoffs,
            IReadOnlyDictionary<string, RecipeArrays> recipes,
            IReadOnlyDictionary<string, InterlockPlan> interlocks,
            IReadOnlyDictionary<string, ActuatorTiming> actuatorTiming)
        {
            RecipeInputs = recipeInputs;
            Interlocks = interlocks;
            SemanticFindings = recipeInputs.Findings.ToList();
            ActuatorTiming = actuatorTiming;
            Config = config;
            Profile = profile;
            Roster = roster;
            Allocation = allocation;
            Twin = twin;
            Components = twin.Components.Select(c => c.Source).ToList();
            Station = station;
            Rings = rings;
            CatTypes = catTypes;
            DetouredChain = detouredChain;
            CrossRingSegment = crossRingSegment;
            Slots = slots;
            Handoffs = handoffs;
            Recipes = recipes;
        }

        // Nothing here touches the deployed tree: planning completes before the first artefact is written,
        // so a model the backend cannot express fails with a diagnostic, not a half-generated project.
        public static GenerationContext Plan(MapperConfig config, string controlXmlPath, DeploymentProfile profile)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (string.IsNullOrWhiteSpace(controlXmlPath))
                throw new ArgumentException("Control.xml path is required.", nameof(controlXmlPath));
            if (!System.IO.File.Exists(controlXmlPath))
                throw new System.IO.FileNotFoundException($"Control.xml not found: {controlXmlPath}", controlXmlPath);

            var components = new CodeGen.IO.SystemXmlReader().ReadAllComponents(controlXmlPath);
            return Plan(config, components, profile);
        }

        // The same plan from components already parsed, so the UI's state-transition preview shows what
        // generation will actually produce rather than approximating it.
        public static GenerationContext Plan(
            MapperConfig config, IReadOnlyList<VueOneComponent> components, DeploymentProfile profile)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (components == null) throw new ArgumentNullException(nameof(components));
            // Resolve the twin first: a model whose references do not close is rejected here rather than
            // silently losing whatever the dangling reference asked for.
            var twin = TwinModel.Build(components);
            var roster = new DeploymentRoster(profile);
            // Layout rows are overrides; everything else the twin declares is placed from the model.
            roster.PlaceUnlisted(twin);
            var allocation = new ControllerAllocation(roster);

            var station = ResolveStation(components, roster);
            // Every actuator the twin declares, decided once; the station set is a subset of it.
            var catTypeOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in twin.Components.Where(c => c.IsActuator).Select(c => c.Source))
            {
                var n = (c.Name ?? string.Empty).Trim();
                if (n.Length > 0) catTypeOf[n] = TemplateMap.ResolveActuatorCatType(c);
            }
            var catTypes = station.Actuators.ToDictionary(
                a => a.Name.Trim(), a => catTypeOf[a.Name.Trim()], StringComparer.OrdinalIgnoreCase);
            // Components on a resource that CARRIES a chain another controller commands. The target
            // declares that capability; nothing here names a controller.
            var carried = new HashSet<PlcAssignment>(
                profile.Layout.Resources
                    .Where(r => TargetRegistry.Of(r.Plc).CarriesDetouredChain)
                    .Select(r => r.Plc));
            var detouredChain = station.Actuators
                .Select(a => (a.Name ?? string.Empty).Trim())
                .Where(n => carried.Contains(allocation.Of(n))).ToList();
            bool ringsMerged = ReportGraph.RingsMustMerge(twin, allocation);
            var rings = ReportGraph.Build(
                twin, allocation, ringsMerged, profile.Facts.CarrierSegment, detouredChain);
            var crossRingSegment = rings.DischargeSegment;
            // Every fixed slot the profile declares, from the one stableSlot column.
            var reservations = profile.Layout.Components
                .Where(e => e.StableSlot.HasValue)
                .ToDictionary(e => e.Name, e => e.StableSlot!.Value, StringComparer.OrdinalIgnoreCase);
            var slots = StateTableAllocation.Slots(station, rings, reservations, profile.Facts);
            int topCover = profile.Facts.RingScopedSlotReporters
                .Select(n => slots.TryGetValue(n, out int s) ? s : -1).FirstOrDefault(s => s >= 0, -1);

            var recipeInputs = BuildRecipeInputs(twin, station, slots, rings, topCover, catTypeOf, profile.Facts);
            var actuatorTiming = PlanActuatorTiming(twin, station, catTypes, detouredChain,
                ringsMerged, profile.Facts);
            var interlocks = InterlockEmitter.PlanAll(
                station.Actuators, catTypes, recipeInputs.Ids, twin, rings, allocation,
                slots, recipeInputs.Findings);
            var handoffs = ProcessCompiler.HandoffPlan(recipeInputs);

            // Compile every process once, so the layout and the sysres mirror read the same rows.
            var recipes = new Dictionary<string, RecipeArrays>(StringComparer.OrdinalIgnoreCase);
            foreach (var process in components.Where(ComponentType.IsProcess))
            {
                var name = process.Name?.Trim() ?? string.Empty;
                recipes[name] = ProcessRecipeArrayGenerator.Generate(
                    process, slots[name], recipeInputs, handoffs);
            }

            return new GenerationContext(config, profile, roster, allocation,
                twin, station, rings, catTypes, detouredChain, crossRingSegment, slots,
                recipeInputs, handoffs, recipes, interlocks, actuatorTiming);
        }


        // Whether each actuator's arrival is SENSED or timed, and how long each leg may take.
        // Sensed means some other component's transition observes that arrival state, so a real DI
        // closes the wait; nothing observing it means only the CAT's own timer can acknowledge.
        private static IReadOnlyDictionary<string, ActuatorTiming> PlanActuatorTiming(
            TwinModel twin, StationContents station, IReadOnlyDictionary<string, string> catTypes,
            IReadOnlyList<string> detoured, bool ringsMerged, PlantFacts facts)
        {
            // One sweep of every observed state, rather than one sweep per actuator per state set.
            var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in twin.Components)
                foreach (var st in c.States)
                    foreach (var t in st.Transitions)
                        foreach (var r in t.Leaves)
                            if (!string.Equals(r.Component.Id, c.Id, StringComparison.OrdinalIgnoreCase))
                                observed.Add(r.Component.Id + "|" + r.State.Id);

            int defaultMs = GenerationConfig.Current.DefaultMotionMs;
            var result = new Dictionary<string, ActuatorTiming>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in station.Actuators)
            {
                var name = (a.Name ?? string.Empty).Trim();
                if (name.Length == 0 || result.ContainsKey(name)) continue;

                bool Sensed(HashSet<string> ids) =>
                    ids.Any(id => observed.Contains(a.ComponentID + "|" + id));
                bool work = Sensed(SystemInjector.ResolveAtWorkStateIds(a));
                bool home = Sensed(SystemInjector.ResolveAtHomeStateIds(a));
                int toWork = SystemInjector.ResolveStateTimeMs(a, 1, defaultMs);
                int toHome = SystemInjector.ResolveStateTimeMs(a, 3, defaultMs);

                // A chain another controller commands settles on the declared detour duration.
                if (detoured.Contains(name, StringComparer.OrdinalIgnoreCase))
                    toWork = toHome = GenerationConfig.Current.CoverMotionMs;

                // The plant may contradict what the twin implies: an arrival the deployment
                // declares TIMER-ACKNOWLEDGED has no usable DI, so a sensed wait would never satisfy.
                if (facts.TimerAcknowledged.TryGetValue(name, out int ackMs))
                {
                    work = home = false;
                    if (ackMs > 0) toWork = toHome = ackMs;
                }

                // A jaw closes on a PART, so its "at work" is a grip-detect that asserts only when
                // something is held, not a position DI that always toggles on arrival. The role comes
                // from the twin (a Robot that is not the task arm), never from the instance name.
                if (ringsMerged && ComponentType.Is(a, ComponentType.Robot)
                    && !TemplateMap.IsRobotTaskArm(a)
                    && !detoured.Contains(name, StringComparer.OrdinalIgnoreCase))
                    work = home = false;

                result[name] = new ActuatorTiming(work, home, toWork, toHome);
            }
            return result;
        }

        // Facts about the DEPLOYMENT resolved once: CAT contracts, a sensor's asserted level, the slot a
        // reporter writes and which instance fills the task-arm role.
        private static ProcessCompiler.Ctx BuildRecipeInputs(
            TwinModel twin, StationContents station, IReadOnlyDictionary<string, int> slots,
            ReportGraph rings, int topCoverSlot, Dictionary<string, string> catTypeOf, PlantFacts facts)
        {
            var present = facts.SensorAssertedLevel;

            // Reporters on a RESERVED rather than positional slot, read back from the one allocation so a
            // recipe WAIT and the FB's actuator_id are the same number by construction.
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in facts.InjectedReporters)
                if (slots.TryGetValue(n, out int injected)) byName[n] = injected;
            if (!string.IsNullOrWhiteSpace(facts.TaskArmInstance) &&
                slots.TryGetValue(facts.TaskArmInstance, out int arm))
                byName[facts.TaskArmInstance] = arm;
            if (topCoverSlot >= 0)
                foreach (var n in facts.RingScopedSlotReporters) byName[n] = topCoverSlot;

            // The material bridge is whichever injected sensor rides the cross-controller segment: that
            // membership is what makes its level readable on the far controller. A merged ring needs none.
            var bridge = facts.InjectedReporters.FirstOrDefault(n =>
                rings.DischargeSegment.Contains(n, StringComparer.OrdinalIgnoreCase));

            var protocolOf = new Dictionary<string, CatProtocol>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in catTypeOf)
                if (TemplateManifest.ProtocolOrNull(kv.Value) is { } proto) protocolOf[kv.Key] = proto;

            var taskArms = new HashSet<string>(
                twin.Components.Where(c => TemplateMap.IsRobotTaskArm(c.Source)).Select(c => c.Name),
                StringComparer.OrdinalIgnoreCase);

            return new ProcessCompiler.Ctx
            {
                Ids = ProcessRecipeArrayGenerator.ScopedIds(station, slots),
                IdsByName = byName,
                ProcessIdByName = slots,
                SensorPresent = present,
                Twin = twin,
                Rings = rings,
                CatType = catTypeOf,
                Protocol = protocolOf,
                TaskArms = taskArms,
                MaterialBridgeId = bridge != null && slots.TryGetValue(bridge, out int bid) ? bid : -1,
            };
        }

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

            // idRank is an ordered RESERVATION, not an allowlist: ranked names keep the slots they already
            // have and anything else is APPENDED, so a new component can never renumber an existing one.
            static List<VueOneComponent> Ordered(
                IReadOnlyList<string> reserved,
                Func<string, VueOneComponent?> resolve,
                IEnumerable<VueOneComponent> declared)
            {
                var placed = reserved.Select(resolve).Where(c => c != null).Select(c => c!).ToList();
                var seen = new HashSet<string>(placed.Select(c => c.Name.Trim()), StringComparer.OrdinalIgnoreCase);
                placed.AddRange(declared.Where(c => seen.Add(c.Name.Trim())));
                return placed;
            }

            bool IsRole(VueOneComponent c, params string[] types) =>
                types.Any(t => string.Equals(c.Type, t, StringComparison.OrdinalIgnoreCase));

            return new StationContents(process,
                Ordered(roster.IdOrderActuators,
                        n => ByName(n, ComponentType.Actuator, ComponentType.Robot),
                        components.Where(c => IsRole(c, ComponentType.Actuator, ComponentType.Robot))),
                Ordered(roster.IdOrderSensors,
                        n => ByName(n, ComponentType.Sensor),
                        components.Where(c => IsRole(c, ComponentType.Sensor))))
            {
                Processes = components.Where(ComponentType.IsProcess)
                    .Select(c => c.Name?.Trim() ?? string.Empty)
                    .Where(n => n.Length > 0).ToList(),
                Ranked = new HashSet<string>(
                    roster.IdOrderSensors.Concat(roster.IdOrderActuators), StringComparer.OrdinalIgnoreCase),
            };
        }
    }
}
