using System;
using System.Collections.Generic;
using System.Linq;


namespace CodeGen.Mapping
{

    public static class TemplateManifest
    {

        // One row per FB type, read from Config/templates.yml and validated at load. Frozen on first
        // use: every derived set below is computed once from it, so nothing downstream can see two
        // different manifests within one run.
        // Frozen on first use: every derived set below is computed once from it, so nothing downstream
        // can see two different manifests within one run.
        // Public because it is the ONE roll of what the Mapper deploys: anything that needs to know
        // which types exist asks here rather than keeping a second list beside it.
        public static readonly IReadOnlyList<TemplateType> Types =
            Configuration.TemplateCatalog.Current.Templates;

        static readonly Dictionary<string, TemplateType> ByName =
            Types.ToDictionary(t => t.Name, StringComparer.Ordinal);

        // One type carries TypeRole.Process, so nothing downstream has to spell its name.
        public static TemplateType ProcessType { get; } = Types.Single(t => t.Role == TypeRole.Process);

        // The one CAT that renders a sensor. A component's role picks its type; a name never does.
        public static TemplateType SensorType { get; } = Types.Single(
            t => t.Role == TypeRole.Sensor && t.Kind == ArtefactKind.Cat);

        // Throws rather than guessing: an unserved role means layout.yml names an instance with no template.
        public static TemplateType ForInfraRole(string role)
        {
            var hits = Types.Where(t => t.InfraRoles.Contains(role, StringComparer.Ordinal)).ToList();
            if (hits.Count == 1) return hits[0];
            throw new InvalidOperationException(hits.Count == 0
                ? $"[Manifest] no template serves the infrastructure role '{role}'."
                : $"[Manifest] {hits.Count} templates serve the infrastructure role '{role}': " +
                  string.Join(", ", hits.Select(t => t.Name)) + ".");
        }

        // THE DEPLOYED FILE FOR A ROLE. A deploy-time patch addresses a type by what it DOES - the
        // process engine, the ring relay, the interlock evaluator - and templates.yml says which type
        // that is. Spelling the filename at the patch instead makes the patch and the catalogue two
        // owners of the same fact, and the one that is wrong fails silently: an absent .fbt is skipped.
        public static string FbtOf(string role) => ForInfraRole(role).Name + ".fbt";

        // The namespace an instance of this type is emitted with. A type that declares none is the
        // Mapper's own, so it carries the project namespace.
        public static string NamespaceOf(TemplateType t) =>
            string.IsNullOrWhiteSpace(t.Namespace)
                ? Configuration.GenerationConfig.Namespace
                : t.Namespace!.Trim();

        // Which CAT serves a graph shape. Where more than one could, the DECLARED priority decides -
        // never the order rows happen to be written in - and an equal priority is refused, because a
        // silently-picked CAT drives the plant with a different command vocabulary.
        public static TemplateType? ForGraph(int stateCount, bool branched)
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

        // The declared sequence for a component on this CAT, or null where it is driven by walking the
        // twin's graph. EXACTLY ONE row may claim a component: two would leave the machine it runs to
        // whichever happens to be written first, so it is refused rather than ordered.
        public static CatExecution? ExecutionFor(string? catType, string? componentType)
        {
            var claiming = Configuration.RigCatalog.Current.Execution
                .Where(e => e.Claims(catType, componentType)).ToList();
            if (claiming.Count > 1)
                throw new InvalidOperationException(
                    $"[CAT] {claiming.Count} execution rows claim '{catType}'/'{componentType}', so which " +
                    "sequence it runs would depend on which was written first.");
            var d = claiming.FirstOrDefault();
            return d == null || d.Mode == Configuration.ExecutionMode.StopDriven ? null : d;
        }

        // A CAT commanded by a handshake rather than stop values declares no protocol; asking is not an error.
        public static CatProtocol? ProtocolOrNull(string? catType) => Find(catType)?.Protocol;

        public static CatProtocol ProtocolOf(string? catType) =>
            Find(catType)?.Protocol
            ?? throw new InvalidOperationException(
                $"[CAT] '{catType}' declares no command protocol, so nothing can say which value drives " +
                "it or which value means it arrived.");

        // Types the Mapper instantiates every run; a stale instance is swept before generation.
        public static IReadOnlySet<string> EmittedTypes { get; } =
            Types.Where(t => t.Emitted).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        public static TemplateType? Find(string? name) =>
            name != null && ByName.TryGetValue(name, out var t) ? t : null;

        // Deployment inventory, in declaration order, minus the ones held back to the end.
        public static IReadOnlyList<string> DeployedLast(ArtefactKind kind) =>
            Types.Where(t => t.Deploy && t.DeployLast && t.Kind == kind).Select(t => t.Name).ToList();

        // Every CAT whose faceplate or telemetry the deployer patches, so a new one is a manifest row.
        public static IReadOnlyList<TemplateType> WithHmiFaceplate =>
            Types.Where(t => t.HmiFaceplate).ToList();

        // The CATs whose symlink subscribers must be enabled for their core to see its own IO.
        public static IReadOnlyList<TemplateType> WithSymlinkQi =>
            Types.Where(t => t.SymlinkQi).ToList();

        public static IReadOnlyList<TemplateType> WithTelemetryTap =>
            Types.Where(t => t.Telemetry != null).ToList();

        public static IReadOnlyList<string> Deployed(ArtefactKind kind) =>
            Types.Where(t => t.Deploy && !t.DeployLast && t.Kind == kind).Select(t => t.Name).ToArray();

        // The ring and station adapter ports a CAT declares. The process CAT spells the ring pair
        // differently from a component CAT, so the spelling is READ from the declaration - and a CAT
        // that declares none is REFUSED rather than given a guess: a wire to a port the type does not
        // have is what EAE rejects the whole resource for.
        public static string RingIn(string? cat)     => Port(cat, "stateR", "_in",  "report-ring in");
        public static string RingOut(string? cat)    => Port(cat, "stateR", "_out", "report-ring out");
        public static string StationIn(string? cat)  => Port(cat, "stationAdptr", "_in",  "station-chain in");
        public static string StationOut(string? cat) => Port(cat, "stationAdptr", "_out", "station-chain out");

        private static string Port(string? cat, string kind, string direction, string role) =>
            (cat == null ? null : Find(cat)?.Ports.FirstOrDefault(p =>
                p.Contains(kind, StringComparison.Ordinal) &&
                p.EndsWith(direction, StringComparison.Ordinal)))
            ?? throw new InvalidOperationException(
                $"[Template] '{cat ?? "(none)"}' is wired as a {role} but templates.yml declares no such " +
                "port for it. A wire to a port a type does not have is what EAE rejects the resource " +
                "for, so the port is declared and checked against the archive rather than assumed.");

        // Read by the mirror AND the parity validator, so the two can never drift.
        public static IReadOnlySet<string> Mirrored { get; } =
            new HashSet<string>(Types.Where(t => t.MirrorToSysres).Select(t => t.Name), StringComparer.Ordinal);

        public static IReadOnlyList<string> ForceRefresh(ArtefactKind kind) =>
            Types.Where(t => t.ForceRefresh && t.Kind == kind).Select(t => t.Name).ToArray();

        public static IReadOnlyDictionary<string, IReadOnlyList<string>> PortContract { get; } =
            Types.Where(t => t.Ports.Count > 0)
                 .ToDictionary(t => t.Name, t => (IReadOnlyList<string>)t.Ports, StringComparer.Ordinal);
    }
}
