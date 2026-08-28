using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Models;

namespace CodeGen.Mapping
{
    /// EVERY QUESTION ABOUT A DECLARED FB TYPE, ANSWERED FROM ONE RUN'S DECLARATIONS.
    ///
    /// templates.yml says which types exist, what each one is for, which ports it carries and which
    /// command vocabulary drives it; smc-rig.yml says which component fills which semantic role. Both
    /// are resolved ONCE here, from the configuration snapshot the run was started with, and the
    /// resolved index travels with that snapshot.
    ///
    /// This used to be a static class whose derived sets were computed on first touch. That is exactly
    /// the shape that cannot be right twice: the first generation in a process froze the catalogue, and
    /// every later run - a different profile, a concurrent run, a test that copied its declarations -
    /// compiled part of itself against the frozen one and part against its own. Nothing announced the
    /// mismatch, because each half was individually valid.
    public sealed class TemplateIndex
    {
        readonly Dictionary<string, TemplateType> _byName;
        readonly IReadOnlyList<CatExecution> _execution;
        readonly SemanticRoles _roles;
        readonly string _projectNamespace;

        /// One roll of what the Mapper deploys: anything needing to know which types exist asks here
        /// rather than keeping a second list beside it.
        public IReadOnlyList<TemplateType> Types { get; }

        public TemplateIndex(TemplateCatalog templates, RigCatalog rig, string projectNamespace)
        {
            if (templates is null) throw new ArgumentNullException(nameof(templates));
            if (rig is null) throw new ArgumentNullException(nameof(rig));

            Types = templates.Templates;
            _byName = Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
            _execution = rig.Execution;
            _roles = rig.Roles;
            _projectNamespace = projectNamespace;

            ProcessType = Types.Single(t => t.Role == TypeRole.Process);
            SensorType = Types.Single(t => t.Role == TypeRole.Sensor && t.Kind == ArtefactKind.Cat);
            EmittedTypes = Types.Where(t => t.Emitted).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            Mirrored = Types.Where(t => t.MirrorToSysres).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            PortContract = Types.Where(t => t.Ports.Count > 0)
                .ToDictionary(t => t.Name, t => (IReadOnlyList<string>)t.Ports, StringComparer.Ordinal);
        }

        /// One type carries TypeRole.Process, so nothing downstream has to spell its name.
        public TemplateType ProcessType { get; }

        /// The one CAT that renders a sensor. A component's role picks its type; a name never does.
        public TemplateType SensorType { get; }

        /// Types the Mapper instantiates every run; a stale instance is swept before generation.
        public IReadOnlySet<string> EmittedTypes { get; }

        /// Read by the mirror AND the parity validator, so the two can never drift.
        public IReadOnlySet<string> Mirrored { get; }

        public IReadOnlyDictionary<string, IReadOnlyList<string>> PortContract { get; }

        public TemplateType? Find(string? name) =>
            name != null && _byName.TryGetValue(name, out var t) ? t : null;

        /// Throws rather than guessing: an unserved role means layout.yml names an instance with no template.
        public TemplateType ForInfraRole(string role)
        {
            var hits = Types.Where(t => t.InfraRoles.Contains(role, StringComparer.Ordinal)).ToList();
            if (hits.Count == 1) return hits[0];
            throw new InvalidOperationException(hits.Count == 0
                ? $"[Manifest] no template serves the infrastructure role '{role}'."
                : $"[Manifest] {hits.Count} templates serve the infrastructure role '{role}': " +
                  string.Join(", ", hits.Select(t => t.Name)) + ".");
        }

        /// THE DEPLOYED FILE FOR A ROLE. A deploy-time patch addresses a type by what it DOES - the
        /// process engine, the ring relay, the interlock evaluator - and templates.yml says which type
        /// that is. Spelling the filename at the patch instead makes the patch and the catalogue two
        /// owners of the same fact, and the one that is wrong fails silently: an absent .fbt is skipped.
        public string FbtOf(string role) => ForInfraRole(role).Name + ".fbt";

        /// The namespace an instance of this type is emitted with. A type that declares none is the
        /// Mapper's own, so it carries the project namespace.
        public string NamespaceOf(TemplateType t) =>
            string.IsNullOrWhiteSpace(t.Namespace) ? _projectNamespace : t.Namespace!.Trim();

        /// Which CAT serves a graph shape. Where more than one could, the DECLARED priority decides -
        /// never the order rows happen to be written in - and an equal priority is refused, because a
        /// silently-picked CAT drives the plant with a different command vocabulary.
        public TemplateType? ForGraph(int stateCount, bool branched)
        {
            var serving = Types
                .Where(t => t.Protocol is { } p && p.Serves(stateCount, branched))
                .OrderByDescending(t => t.Protocol!.Priority)
                .ToList();
            if (serving.Count > 1 && serving[0].Protocol!.Priority == serving[1].Protocol!.Priority)
                throw new InvalidOperationException(
                    $"[CAT] a {stateCount}-state{(branched ? " branched" : string.Empty)} graph is served " +
                    $"by {serving.Count} templates at the same priority (" +
                    string.Join(", ", serving.Select(t => t.Name)) +
                    "), so which command vocabulary the actuator is driven with would depend on which " +
                    "row was written first. Give one of them a higher protocol.priority.");
            return serving.FirstOrDefault();
        }

        /// The declared sequence for a component on this CAT, or null where it is driven by walking the
        /// twin's graph. EXACTLY ONE row may claim a component: two would leave the machine it runs to
        /// whichever happens to be written first, so it is refused rather than ordered.
        public CatExecution? ExecutionFor(string? catType, string? componentType)
        {
            var claiming = _execution.Where(e => e.Claims(catType, componentType)).ToList();
            if (claiming.Count > 1)
                throw new InvalidOperationException(
                    $"[CAT] {claiming.Count} execution rows claim '{catType}'/'{componentType}', so which " +
                    "sequence it runs would depend on which was written first.");
            var d = claiming.FirstOrDefault();
            return d == null || d.Mode == ExecutionMode.StopDriven ? null : d;
        }

        /// THE PORTS A PHASE ANNOUNCEMENT TRAVELS OVER, declared on the process type. Backend
        /// vocabulary rather than a planning decision, and it belongs to the shipped FB - so a
        /// different process FB brings its own ports instead of needing a planner edited.
        public CatPhaseHandoff PhaseTransport =>
            ProcessType.PhaseHandoff
            ?? throw new InvalidOperationException(
                $"[Templates] '{ProcessType.Name}' is the process type but declares no phaseHandoff, so a " +
                "phase announcement has no ports to travel over. Declare it in templates.yml, or no " +
                "cross-controller handoff can be planned.");

        /// A CAT commanded by a handshake rather than stop values declares no protocol; asking is not an error.
        public CatProtocol? ProtocolOrNull(string? catType) => Find(catType)?.Protocol;

        public CatProtocol ProtocolOf(string? catType) =>
            Find(catType)?.Protocol
            ?? throw new InvalidOperationException(
                $"[CAT] '{catType}' declares no command protocol, so nothing can say which value drives " +
                "it or which value means it arrived.");

        /// How a type is asked to report a level it already holds, or null if it cannot be asked.
        /// A type that declares no refresh is never sent one: the wait then relies on a real edge.
        public RefreshDeclaration? RefreshOf(string? catType) => Find(catType)?.Refresh;

        /// Deployment inventory, in declaration order, minus the ones held back to the end.
        public IReadOnlyList<string> DeployedLast(ArtefactKind kind) =>
            Types.Where(t => t.Deploy && t.DeployLast && t.Kind == kind).Select(t => t.Name).ToList();

        public IReadOnlyList<string> Deployed(ArtefactKind kind) =>
            Types.Where(t => t.Deploy && !t.DeployLast && t.Kind == kind).Select(t => t.Name).ToArray();

        /// Every CAT whose faceplate or telemetry the deployer patches, so a new one is a manifest row.
        public IReadOnlyList<TemplateType> WithHmiFaceplate => Types.Where(t => t.HmiFaceplate).ToList();

        /// The CATs whose symlink subscribers must be enabled for their core to see its own IO.
        public IReadOnlyList<TemplateType> WithSymlinkQi => Types.Where(t => t.SymlinkQi).ToList();

        public IReadOnlyList<TemplateType> WithTelemetryTap => Types.Where(t => t.Telemetry != null).ToList();

        public IReadOnlyList<string> ForceRefresh(ArtefactKind kind) =>
            Types.Where(t => t.ForceRefresh && t.Kind == kind).Select(t => t.Name).ToArray();

        // ---- ports -------------------------------------------------------------------------------

        /// The ring and station adapter ports a CAT declares. The process CAT spells the ring pair
        /// differently from a component CAT, so the spelling is READ from the declaration - and a CAT
        /// that declares none is REFUSED rather than given a guess: a wire to a port the type does not
        /// have is what EAE rejects the whole resource for.
        public string RingIn(string? cat) => Port(cat, "stateR", "_in", "report-ring in");
        public string RingOut(string? cat) => Port(cat, "stateR", "_out", "report-ring out");
        public string StationIn(string? cat) => Port(cat, "stationAdptr", "_in", "station-chain in");
        public string StationOut(string? cat) => Port(cat, "stationAdptr", "_out", "station-chain out");

        string Port(string? cat, string kind, string direction, string role) =>
            (cat == null ? null : Find(cat)?.Ports.FirstOrDefault(p =>
                p.Contains(kind, StringComparison.Ordinal) &&
                p.EndsWith(direction, StringComparison.Ordinal)))
            ?? throw new InvalidOperationException(
                $"[Template] '{cat ?? "(none)"}' is wired as a {role} but templates.yml declares no such " +
                "port for it. A wire to a port a type does not have is what EAE rejects the resource " +
                "for, so the port is declared and checked against the archive rather than assumed.");

        /// Threading a CAT with no stationAdptr port dangles it and EAE rejects the whole resource.
        public bool LacksStationAdapter(string? catType) =>
            catType != null && Find(catType) is { StationAdapter: false };

        // ---- component -> type -------------------------------------------------------------------

        /// VueOne types the task arm and the jaws alike as "Robot", so the profile names the arm instance.
        public bool IsRobotTaskArm(VueOneComponent component) =>
            component != null && _roles.Is(_roles.TaskArm, component.Name);

        /// THE ONE component -> FB Type decision; every consumer resolves through here (INVARIANTS.md I-4).
        public string ResolveActuatorCatType(VueOneComponent actuator)
        {
            if (actuator == null)
                throw new ArgumentNullException(nameof(actuator),
                    "[CAT] no component to resolve a type for; a default here would silently pick a command vocabulary.");
            // The task arm runs a handshake rather than a stop sequence, so its CAT is chosen by the
            // role the profile assigns, and the MANIFEST says which template serves that role.
            if (IsRobotTaskArm(actuator)) return ForInfraRole("taskArm").Name;
            return ResolveActuatorCatType(
                actuator.Name ?? string.Empty,
                actuator.States?.Count ?? 0,
                TemplateMap.IsBranchedSevenState(actuator));
        }

        /// The twin's state graph picks the CAT via the manifest. An unclaimed shape fails here rather
        /// than defaulting to five-state, which would command a swivel as if it had one work stop.
        public string ResolveActuatorCatType(string componentName, int stateCount, bool isBranchedSeven) =>
            ForGraph(stateCount, isBranchedSeven)?.Name
            ?? throw new InvalidOperationException(
                $"[CAT] '{componentName}' has a {stateCount}-state" +
                (isBranchedSeven ? " branched" : string.Empty) +
                " graph, which no CAT protocol serves. Give it a shape an existing CAT supports, or add a " +
                "CAT whose protocol declares that shape; the Mapper will not guess a command vocabulary.");
    }
}
