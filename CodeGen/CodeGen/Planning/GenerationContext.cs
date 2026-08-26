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
        IReadOnlyList<string> Processes,
        string? TerminatorFb,
        // Head of the report ring this resource participates in. Bring-up follows that ring, so anything
        // added to it later hangs off this rather than an emitter naming a component to init from.
        string? InitAnchor,
        IReadOnlyList<(string Source, string Destination)> AdapterRelations,
        IReadOnlyList<InfraInstance> Infrastructure,
        (string From, string To)? StationChain,
        ResourceCapabilities Capabilities);

    // Everything one generation decided before any artefact was written. Parse once, plan once, render
    // once. Passed explicitly, so a second generation cannot read the first one's answers.
    public sealed class GenerationContext
    {
        // The one immutable configuration this run compiles against, taken at the composition root.
        // Nothing below re-reads a declaration file: a stage that asked again could see a different
        // answer than the stage before it, and the two would disagree about the same generation.
        public Configuration.CompilerConfiguration Cfg { get; }

        // Machine-local paths - a PROJECTION of the snapshot, not a second object. The emitters and
        // the prebuilt VueOne runner both take this type, so it stays reachable under its own name.
        public MapperConfig Config => Cfg.Paths;

        // The declared FB types and deployment targets THIS run compiles against. Forwarded rather
        // than rebuilt, so a planner cannot end up holding a different index from its own snapshot.
        public Mapping.TemplateIndex Manifest => Cfg.Manifest;
        public Mapping.TargetIndex Targets => Cfg.Targets;

        public DeploymentProfile Profile { get; }

        // Where each component runs and is drawn; on the profile, so nothing below reaches the catalog.
        public LayoutCatalog Layout => Profile.Layout;
        public DeploymentRoster Roster { get; }

        // The name each component is EMITTED under: the overrides workbook, else the suffix-stripping
        // convention. Decided here so the layout, the resource plan and every wiring pass agree by
        // construction rather than each re-resolving it.
        public IReadOnlyDictionary<string, string> InstanceNames { get; }

        public string InstanceName(string? twinName) =>
            twinName != null && InstanceNames.TryGetValue(twinName.Trim(), out var n)
                ? n : (twinName ?? string.Empty).Trim();
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

        // Reporters this deployment injects that the twin does not declare, and that this run actually
        // emits. An injected reporter rides the cross-controller segment, so without that segment there
        // is nothing for it to ride and no FB is created: asked once here, so the emitter that creates
        // it and the plan that wires it cannot disagree about whether it exists.
        public IReadOnlyList<string> InjectedReporters =>
            CrossRingSegment.Count == 0
                ? System.Array.Empty<string>()
                : Profile.Facts.InjectedReporters
                    .Where(n => !Station.Sensors.Concat(Station.Actuators)
                        .Any(c => string.Equals(c.Name?.Trim(), n, StringComparison.OrdinalIgnoreCase)))
                    .Where(Roster.Contains).ToList();

        // state_table slot the top-cover sensor reports on; see StateTableAllocation for why it is computed.

        // Every reporter's state_table slot, processes included; see StateTableAllocation.
        public IReadOnlyDictionary<string, int> Slots { get; }

        // state_table size this plan needs; every FBT that declares state_table is patched to exactly this.
        public int StateTableCapacity => StateTableAllocation.Required(Cfg.Generation.StateTableCapacity, Slots);

        // Recipe rows the engine types must be able to hold. Declared, and every FBT that declares a
        // recipe array is patched to exactly this - so the type can always carry what the plan emits.
        public int RecipeCapacity => Cfg.Generation.RecipeArraySize;

        // Rule capacity this plan needs. One past nothing: the widest table any actuator asked for,
        // never below the declared floor. Every FB that declares the rule array is patched to exactly
        // this, so a rule is never dropped to make the model fit the type.
        public int InterlockCapacity => Math.Max(
            Cfg.Interlocks.RuleArraySize,
            Interlocks.Count == 0 ? 0 : Interlocks.Values.Max(p => p.Count));

        // A target that only EXISTS when something is relocated onto it is not emitted when nothing is,
        // so anything else the roster placed there would be planned onto a device the run never writes
        // and would silently run nowhere. The capability is declared per target; no controller is named.
        private static void AssertEveryPlacementHasADevice(
            StationContents station, ControllerAllocation allocation, DeploymentProfile profile,
            Mapping.TargetIndex targets)
        {
            if (profile.HasAssignments) return;                    // every declared target is emitted
            var absent = profile.Layout.Resources
                .Select(r => r.Plc)
                .Where(plc => targets.Of(plc).ReceivesRelocatedComponents)
                .ToHashSet();
            if (absent.Count == 0) return;

            var stranded = station.Actuators.Concat(station.Sensors)
                .Select(c => (c.Name ?? string.Empty).Trim())
                .Concat(station.Processes.Select(p => p.Trim()))
                .Where(n => n.Length > 0 && absent.Contains(allocation.Of(n)))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            if (stranded.Count == 0) return;

            throw new InvalidOperationException(
                "[Placement] " + string.Join(", ", stranded) + " " +
                (stranded.Count == 1 ? "is" : "are") + " placed on a target this run does not emit, " +
                "because that target only exists when components are relocated onto it and none were. " +
                "Relocate them there or place them on a target the run writes; generation stops rather " +
                "than planning work onto a device that will not exist.");
        }

        // Of these names, the ones the twin declares as something with a physical channel. A process is
        // logic and needs none, so a target's IO contract has nothing to say about hosting one.
        public IReadOnlyCollection<string> IoBearing(IEnumerable<string> names) =>
            names.Where(n => Twin.ByName(n) is { } c && (c.IsActuator || c.IsSensor))
                 .ToList();

        // The processes a controller runs, in roster order, which is the order the layout emits them.
        public IEnumerable<VueOneComponent> ProcessesOn(PlcAssignment plc) =>
            Roster.All
                .Where(e => e.Plc == plc)
                .Select(e => Twin.ByName(e.Name))
                .Where(c => c is { IsProcess: true })
                .Select(c => c!.Source);

        // The report ring a resource participates in. Asked of the TARGET, so a resource with no
        // components of its own still answers from the planned topology rather than from a name.
        private ReportDomainId RingDomainOf(PlcAssignment plc) => Rings.DomainOf(plc);

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
            foreach (var role in Layout.InfraEmitOrder)
            {
                var name = Role(role);
                if (name == null) continue;
                var template = Manifest.ForInfraRole(role);
                var row = Roster.All.FirstOrDefault(e =>
                    string.Equals(e.Name, name, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException(
                        $"[Layout] resource {plc} names '{name}' as its {role}, but layout.yml gives it no row, " +
                        "so it has nowhere to be drawn.");
                var parameters = template.NameParameter == null
                    ? (IReadOnlyDictionary<string, string>)EmptyParameters
                    : new Dictionary<string, string> { [template.NameParameter] = Iec61499Literal.FormatString(name) };
                infra.Add(new InfraInstance(role, name, template,
                    Configuration.GenerationConfig.Namespace, row.X, row.Y, parameters));
            }

            var anchor = Station.Sensors.Concat(Station.Actuators)
                .Select(c => (c.Name ?? string.Empty).Trim())
                .FirstOrDefault(n => n.Length > 0 && Rings.DomainId(n) == RingDomainOf(plc));

            return new ResourcePlan(plc, profile.Label,
                Role("area"), Role("station"),
                ProcessesOn(plc).Select(p => InstanceName(p.Name)).ToList(),
                Role("terminator"), anchor, wires, infra, chain, CapabilitiesOf(plc));
        }

        // Topology role per controller, decided here and never from the resource Label, which is display
        // text. Feed may be hosted by M262 or, under a partial swap, by the RevPi.
        private ResourceCapabilities CapabilitiesOf(PlcAssignment plc)
        {
            var target = Targets.Of(plc);
            return new ResourceCapabilities(
                DeviceLocalCanvas:           target.DeviceLocalCanvas,
                ReceivesRelocatedComponents: target.ReceivesRelocatedComponents,
                // Only when the covers actually detour, and only from the target that hands them out.
                OpensCoverSeam:              DetouredChain.Count > 0 && target.OpensCoverSeam,
                // Open at both ends either because another controller commands the chain, or because
                // this run relocated components here and their own process stayed where it was.
                CarriesDetouredChain:        target.CarriesDetouredChain ||
                                             (target.ReceivesRelocatedComponents && Profile.HasAssignments),
                HostsFeedRing:               target.HostsFeedStation && Mapping.TargetIndex.OwnsRing(target));
        }

        // Whether this run emits a resource for that target at all. A target that only exists when
        // something is relocated onto it is not emitted when nothing is, so anything planned there would
        // be written to a device the run never creates.
        public bool Emits(PlcAssignment plc) =>
            Targets.IsRegistered(plc) &&
            (!Targets.Of(plc).ReceivesRelocatedComponents || Profile.HasAssignments);

        private static readonly Dictionary<string, string> EmptyParameters = new(StringComparer.Ordinal);

        // Every process-to-process handoff the twin declares, with its transport resolved.
        public ProcessHandoffPlan Handoffs { get; }

        // Everything compiling a recipe needs, resolved ONCE; the compiler never re-asks the rig catalog,
        // the allocation or the topology per process.
        internal ProcessCompiler.Ctx RecipeInputs { get; }

        // Each process's validated control flow. One owner: nothing walks a process state machine for
        // itself, so the execution order, the successor and the entry state have one answer each.

        // The compiled recipe per process, keyed by the twin's own component name.
        public IReadOnlyDictionary<string, RecipeArrays> Recipes { get; }

        // Every actuator's interlock rules, keyed by component name.
        public IReadOnlyDictionary<string, InterlockPlan> Interlocks { get; }

        // Guards the twin states as a choice that a linear recipe had to satisfy in full, and any
        // other semantics the backend could not represent. Read by the run, never by an emitter.
        public IReadOnlyList<string> SemanticFindings { get; }

        // Every guard leaf the twin declares and what became of it. The plan proves it is complete
        // before returning, so a reader can ask what happened to any condition in the model.
        public GuardCoverage GuardCoverage => RecipeInputs.Coverage;

        // What the plan decided about each actuator instance: whether its arrival is SENSED or
        // timed, and how long each leg is allowed. The emitter formats these; it decides none.
        public IReadOnlyDictionary<string, ActuatorTiming> ActuatorTiming { get; }

        private GenerationContext(Configuration.CompilerConfiguration cfg, DeploymentProfile profile,
            DeploymentRoster roster, ControllerAllocation allocation,
            TwinModel twin, StationContents station,
            ReportGraph rings, IReadOnlyDictionary<string, string> catTypes, IReadOnlyList<string> detouredChain,
            IReadOnlyList<string> crossRingSegment,
            IReadOnlyDictionary<string, int> slots,
            ProcessCompiler.Ctx recipeInputs, ProcessHandoffPlan handoffs,
            IReadOnlyDictionary<string, RecipeArrays> recipes,
            IReadOnlyDictionary<string, InterlockPlan> interlocks,
            IReadOnlyDictionary<string, ActuatorTiming> actuatorTiming,
            IReadOnlyDictionary<string, string> instanceNames)
        {
            InstanceNames = instanceNames;
            RecipeInputs = recipeInputs;
            Interlocks = interlocks;
            SemanticFindings = recipeInputs.Findings.ToList();
            ActuatorTiming = actuatorTiming;
            Cfg = cfg;
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
        public static GenerationContext Plan(Configuration.CompilerConfiguration config,
            string controlXmlPath, DeploymentProfile profile)
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
        public static GenerationContext Plan(Configuration.CompilerConfiguration config,
            IReadOnlyList<VueOneComponent> components, DeploymentProfile profile)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (components == null) throw new ArgumentNullException(nameof(components));
            // Resolve the twin first: a model whose references do not close is rejected here rather than
            // silently losing whatever the dangling reference asked for.
            return Plan(config, TwinModel.Build(components), profile);
        }

        // THE PLAN, from an already-resolved twin. The composition root resolves the model once and
        // hands the same IR to the capability report and to planning, so the two cannot describe
        // different twins - and planning never re-reads the file it was compiled from.
        public static GenerationContext Plan(Configuration.CompilerConfiguration config,
            TwinModel twin, DeploymentProfile profile)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (twin == null) throw new ArgumentNullException(nameof(twin));
            // The twin preserves declaration order (TwinModel.Build appends as it walks), and that order
            // is load-bearing: it is what allocates state_table slots sensors-first.
            var components = twin.Components.Select(c => c.Source).ToList();
            var roster = new DeploymentRoster(profile);
            // Layout rows are overrides; everything else the twin declares is placed from the model.
            roster.PlaceUnlisted(twin);
            var allocation = new ControllerAllocation(roster);

            var station = ResolveStation(components, roster);
            AssertEveryPlacementHasADevice(station, allocation, profile, config.Targets);
            // Every actuator the twin declares, decided once; the station set is a subset of it.
            var catTypeOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in twin.Components.Where(c => c.IsActuator).Select(c => c.Source))
            {
                var n = (c.Name ?? string.Empty).Trim();
                if (n.Length > 0) catTypeOf[n] = config.Manifest.ResolveActuatorCatType(c);
            }
            var catTypes = station.Actuators.ToDictionary(
                a => a.Name.Trim(), a => catTypeOf[a.Name.Trim()], StringComparer.OrdinalIgnoreCase);
            // Components on a resource that CARRIES a chain another controller commands. The target
            // declares that capability; nothing here names a controller.
            var carried = new HashSet<PlcAssignment>(
                profile.Layout.Resources
                    .Where(r => config.Targets.Of(r.Plc).CarriesDetouredChain)
                    .Select(r => r.Plc));
            var detouredChain = station.Actuators
                .Select(a => (a.Name ?? string.Empty).Trim())
                .Where(n => carried.Contains(allocation.Of(n))).ToList();
            // Every process's control flow, resolved and VALIDATED before anything else reads it: a
            // state machine the recipe engine cannot represent is refused here, which is before any
            // file is touched. Built for every declared process, not only the ones a recipe is for.
            var graphs = components.Where(ComponentType.IsProcess).ToDictionary(
                p => (p.Name ?? string.Empty).Trim(),
                CodeGen.Domain.Twin.ProcessGraph.Build,
                StringComparer.OrdinalIgnoreCase);

            var rings = ReportGraph.Build(
                twin, allocation, profile.Facts.CarrierSegment, detouredChain, graphs, config.Targets);
            bool ringsMerged = rings.RingsMerged;
            var crossRingSegment = rings.DischargeSegment;
            // Every fixed slot the profile declares, from the one stableSlot column.
            var reservations = profile.Layout.Components
                .Where(e => e.StableSlot.HasValue)
                .ToDictionary(e => e.Name, e => e.StableSlot!.Value, StringComparer.OrdinalIgnoreCase);
            var slots = StateTableAllocation.Slots(station, rings, reservations, profile.Facts, config.Generation.StateTableCapacity);
            int topCover = profile.Facts.RingScopedSlotReporters
                .Select(n => slots.TryGetValue(n, out int s) ? s : -1).FirstOrDefault(s => s >= 0, -1);

            var recipeInputs = BuildRecipeInputs(twin, station, slots, rings, topCover, catTypeOf, profile.Facts, config.Manifest);
            recipeInputs.Graphs = graphs;
            var actuatorTiming = PlanActuatorTiming(config.Generation, config.Manifest, twin, station, catTypeOf,
                detouredChain, ringsMerged, profile.Facts);
            var interlocks = InterlockEmitter.PlanAll(
                station.Actuators, catTypes, recipeInputs.Ids, twin, recipeInputs.Findings, config.Manifest);
            var handoffs = ProcessCompiler.HandoffPlan(recipeInputs);

            // Compile every process once, so the layout and the sysres mirror read the same rows.
            var recipes = new Dictionary<string, RecipeArrays>(StringComparer.OrdinalIgnoreCase);
            foreach (var process in components.Where(ComponentType.IsProcess))
            {
                var name = process.Name?.Trim() ?? string.Empty;
                recipes[name] = ProcessRecipeArrayGenerator.Generate(
                    process, slots[name], recipeInputs, handoffs, config.Generation.RecipeArraySize);
            }

            // Every guard leaf the twin declares reached a decision. Not "was warned about": a control
            // semantic that reaches nothing is a defect, so an unaccounted leaf stops the run here -
            // which is before any file is written.
            recipeInputs.Coverage.AssertCovers(components.Where(ComponentType.IsProcess)
                .SelectMany(ProcessCompiler.DeclaredLeaves).ToList());

            // An FB has ONE owner. A target's declared broker and a roster row of the same name are two
            // statements about where it runs, and the mirror reads the roster first - so a contradiction
            // would silently place the broker on a resource its target never wired.
            foreach (var target in config.Targets.All.Where(t => t.IoBroker != null))
            {
                var placed = allocation.Of(target.IoBroker!);
                if (placed != PlcAssignment.Unknown && placed != target.Plc)
                    throw new InvalidOperationException(
                        $"[Deployment] '{target.IoBroker}' is declared as {target.Plc}'s ioBroker but " +
                        $"layout.yml places it on {placed}. An emitted FB has one owner; the two " +
                        "declarations must name the same target.");
            }

            // Resolved once, from the same workbook the emitters used to each load for themselves.
            var overrides = string.IsNullOrWhiteSpace(config.Paths.MappingRulesPath)
                ? new InstanceNameOverridesLoader.Overrides()
                : InstanceNameOverridesLoader.Load(config.Paths.MappingRulesPath);
            var instanceNames = twin.Components.ToDictionary(
                c => (c.Name ?? string.Empty).Trim(),
                c => InstanceNameResolver.Resolve(c.Source, overrides.ByComponentId, overrides.ByVueOneName),
                StringComparer.OrdinalIgnoreCase);

            return new GenerationContext(config, profile, roster, allocation,
                twin, station, rings, catTypes, detouredChain, crossRingSegment, slots,
                recipeInputs, handoffs, recipes, interlocks, actuatorTiming, instanceNames);
        }


        // Whether each actuator's arrival is SENSED or timed, and how long each leg may take.
        // Sensed means some other component's transition observes that arrival state, so a real DI
        // closes the wait; nothing observing it means only the CAT's own timer can acknowledge.
        private static IReadOnlyDictionary<string, ActuatorTiming> PlanActuatorTiming(
            Configuration.GenerationConfig generation, Mapping.TemplateIndex manifest,
            TwinModel twin, StationContents station, IReadOnlyDictionary<string, string> catTypeOf,
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

            int defaultMs = generation.DefaultMotionMs;
            var result = new Dictionary<string, ActuatorTiming>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in station.Actuators)
            {
                var name = (a.Name ?? string.Empty).Trim();
                if (name.Length == 0 || result.ContainsKey(name)) continue;

                // Which twin states are a stop, and which is the motion leg toward it, is the CAT's
                // declaration. A CAT that declares no stop vocabulary runs a handshake instead and
                // carries no timing at all, so there is nothing here to decide for it.
                var protocol = catTypeOf.TryGetValue(name, out var cat)
                    ? manifest.ProtocolOrNull(cat) : null;
                if (protocol == null || protocol.Stops.Count == 0) continue;

                // SENSED means some OTHER component's transition observes that arrival, so a real DI
                // closes the wait; nothing observing it means only the CAT's own timer can acknowledge.
                bool Sensed(string stop) =>
                    a.States.Any(st => st.StaticState &&
                        protocol.StopNumbersFor(stop).Contains(st.StateNumber) &&
                        observed.Contains(a.ComponentID + "|" + st.StateID));
                int LegMs(string stop)
                {
                    var leg = protocol.LegFor(stop);
                    var st = leg == null ? null
                        : a.States.FirstOrDefault(x => x.StateNumber == leg.Value);
                    return st == null || st.Time <= 0 ? defaultMs : st.Time;
                }

                var workStop = protocol.Has(CatProtocol.Work) ? CatProtocol.Work : CatProtocol.Work1;
                bool work = Sensed(workStop);
                bool home = Sensed(CatProtocol.Home);
                int toWork = LegMs(workStop);
                int toHome = LegMs(CatProtocol.Home);

                // A chain another controller commands settles on the declared detour duration.
                if (detoured.Contains(name, StringComparer.OrdinalIgnoreCase))
                    toWork = toHome = generation.CoverMotionMs;

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
                    && !manifest.IsRobotTaskArm(a)
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
            ReportGraph rings, int topCoverSlot, Dictionary<string, string> catTypeOf, PlantFacts facts,
            Mapping.TemplateIndex manifest)
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

            // A DECLARED carrier's slot. Only a reporter the deployment names may stand for a phase, and
            // only where its level is readable on the far controller - which is what riding the
            // cross-controller segment, or sharing the ring, gives it.
            var carrierSlots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var declared in facts.Handoff.Carriers)
                if (slots.TryGetValue(declared.Carrier, out int cid)) carrierSlots[declared.Carrier] = cid;

            var protocolOf = new Dictionary<string, CatProtocol>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in catTypeOf)
                if (manifest.ProtocolOrNull(kv.Value) is { } proto) protocolOf[kv.Key] = proto;

            // Which components run a declared command sequence rather than a walk to a numbered stop.
            // The CAT and the twin's own type decide it, so the compiler never has to know what a
            // component is - only what its CAT does with a movement.
            var executionOf = new Dictionary<string, CatExecution>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in twin.Components)
                if (catTypeOf.TryGetValue(c.Name, out var cat) &&
                    manifest.ExecutionFor(cat, c.Source?.Type) is { } exec)
                    executionOf[c.Name] = exec;

            return new ProcessCompiler.Ctx
            {
                Ids = ProcessRecipeArrayGenerator.ScopedIds(station, slots),
                IdsByName = byName,
                ProcessIdByName = slots,
                SensorPresent = present,
                Twin = twin,
                Rings = rings,
                Manifest = manifest,
                CatType = catTypeOf,
                Protocol = protocolOf,
                Execution = executionOf,
                Handoff = facts.Handoff,
                CarrierSlots = carrierSlots,
            };
        }

        private static StationContents ResolveStation(
            IReadOnlyList<VueOneComponent> components, DeploymentRoster roster)
        {
            VueOneComponent? ByName(string name, params string[] types) =>
                components.FirstOrDefault(c =>
                    types.Any(t => string.Equals(c.Type, t, StringComparison.OrdinalIgnoreCase)) &&
                    string.Equals(c.Name, name, StringComparison.Ordinal));


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

            return new StationContents(
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
