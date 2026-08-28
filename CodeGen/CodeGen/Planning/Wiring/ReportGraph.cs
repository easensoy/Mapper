using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Domain.Twin;
using CodeGen.Mapping;

namespace CodeGen.Translation
{
    // WHERE every announcement circulates, and HOW anything the model needs across a controller boundary
    // gets there. One graph, decided once, so nothing downstream re-answers either question its own way.
    //
    // Two views: NATIVE is the ring a component reports on before any splice (Feed-side controllers share
    // one, every other controller has its own); DOMAIN is the ring it reports on in the FINISHED topology,
    // which is what decides whether two reporters land in the same state_table.
    //
    // A carrier is a splice the declarations describe and the MODEL selects: the merged ring, a chain one
    // target commands on another, the discharge segment. An edge no carrier covers is named and generation stops.
    public sealed class ReportGraph
    {
        private readonly ControllerAllocation _allocation;
        private readonly bool _merged;
        // Which ring each target's components report on once every carrier the model selected is applied.
        private readonly IReadOnlyDictionary<PlcAssignment, ReportDomainId> _domainOf;
        // Components a carrier lifts off their own target's ring onto the ring of the target commanding them.
        private readonly IReadOnlyDictionary<string, PlcAssignment> _carriedOnto;

        // The discharge members another controller COMMANDS. Their bring-up crosses a device boundary, so
        // both canvases init them after the process rather than in front of it.
        public IReadOnlyList<string> DischargeTail { get; }
        public IReadOnlyList<string> DischargeSegment { get; }
        public IReadOnlyList<string> DetouredChain { get; }
        public bool RingsMerged => _merged;

        private ReportGraph(ControllerAllocation allocation, bool merged,
            IReadOnlyDictionary<PlcAssignment, ReportDomainId> domainOf,
            IReadOnlyDictionary<string, PlcAssignment> carriedOnto,
            IReadOnlyList<string> discharge, IReadOnlyList<string> detour,
            IReadOnlyList<string> dischargeTail)
        {
            DischargeTail = dischargeTail;
            _allocation = allocation;
            _merged = merged;
            _domainOf = domainOf;
            _carriedOnto = carriedOnto;
            DischargeSegment = discharge;
            DetouredChain = detour;
        }

        // The ring a component's announcements reach in the finished topology: its own target's, unless a
        // carrier lifted it onto the ring of the target that commands it.
        public ReportDomainId DomainId(string? name)
        {
            var trimmed = (name ?? string.Empty).Trim();
            return DomainOf(_carriedOnto.TryGetValue(trimmed, out var host) ? host : _allocation.Of(trimmed));
        }

        // The same identity as an opaque grouping key, for a consumer that only partitions by it. It is a
        // rendering of the id, never a name anything matches on.
        public string Domain(string? name) => DomainId(name).ToString();

        // The target whose ring carries this component's reports, where a carrier lifted it off its own.
        // Null means it reports on the ring of the target that hosts it, like everything else.
        public PlcAssignment? CarrierOf(string? name) =>
            _carriedOnto.TryGetValue((name ?? string.Empty).Trim(), out var host) ? host : null;

        // The ring a TARGET reports on, for a resource with no components of its own to ask about.
        public ReportDomainId DomainOf(PlcAssignment target) =>
            _domainOf.TryGetValue(target, out var d) ? d : ReportDomainId.Unplaced;

        public bool SameDomain(string? a, string? b) => DomainId(a) == DomainId(b);

        // Wiring topology, not recipe: do the per-controller report rings fold into one?
        //
        // The phase transport carries only a cycle's BOUNDARIES. ANY process waiting on another
        // controller's process in the MIDDLE of its own cycle is holding something for that other
        // station and must observe it live, which a boundary handshake cannot express, so the rings must
        // become one. Asked of every process on every controller: which one it is cannot matter, only
        // that the wait sits inside a cycle rather than at its edge.
        private static bool ProcessesNeedOneRing(TwinModel twin, ControllerAllocation allocation,
            IReadOnlyDictionary<string, CodeGen.Domain.Twin.ProcessGraph> graphs)
        {
            foreach (var proc in twin.Processes)
            {
                if (!graphs.TryGetValue(proc.Name, out var g)) continue;
                var chain = g.Ordered;
                // Index 0 is the cycle entry and the last link closes it; both are boundary handshakes.
                for (int i = 1; i < chain.Count - 1; i++)
                    if (WaitsOnAnotherController(chain[i], proc, twin, allocation))
                        return true;
            }
            return false;
        }

        private static bool WaitsOnAnotherController(Models.VueOneState state, TwinComponent proc,
            TwinModel twin, ControllerAllocation allocation) =>
            state.Transitions
                .SelectMany(t => t.Guard?.References()
                    ?? (IReadOnlyList<Models.VueOneCondition>)Array.Empty<Models.VueOneCondition>())
                .Select(c => twin.ById(c.ComponentID))
                .Any(target => target != null
                    && target.IsProcess
                    && !string.Equals(target.Name, proc.Name, StringComparison.OrdinalIgnoreCase)
                    && allocation.Of(target.Name) != allocation.Of(proc.Name));

        // The finished topology, decided here so nothing downstream re-answers it. A recipe wait needs
        // its source on the same ring; so does an INTERLOCK, because the rule reads the source's slot in
        // the consumer's own state_table. Both are proved against the same graph.
        public static ReportGraph Build(
            TwinModel twin, ControllerAllocation allocation,
            IReadOnlyList<string> declaredDischarge, IReadOnlyList<string> detouredChain,
            IReadOnlyDictionary<string, CodeGen.Domain.Twin.ProcessGraph> graphs, Mapping.TargetIndex targets)
        {
            var graph = Assemble(twin, allocation, ProcessesNeedOneRing(twin, allocation, graphs),
                declaredDischarge, detouredChain, targets);

            // Folding the rings into one is the declared carrier that spans every domain, so it is what
            // an interlock reaching across them needs. Applied only when something actually needs it.
            if (!graph.RingsMerged && Crossings(twin, graph).Count > 0)
                graph = Assemble(twin, allocation, merged: true, targets: targets,
                    declaredDischarge: declaredDischarge, detouredChain: detouredChain);

            var stranded = Crossings(twin, graph);
            if (stranded.Count > 0)
                throw new InvalidOperationException(
                    $"[Transport] {stranded.Count} interlock(s) name a source that does not report onto " +
                    "the ring their consumer reads, so the rule would guard whichever component holds " +
                    $"that slot there: {string.Join("; ", stranded)}. Generation stops rather than " +
                    "emitting a safety rule that does not mean what the model says.");
            return graph;
        }

        // Every (source, consumer) an interlock depends on that the finished topology cannot carry.
        private static IReadOnlyList<string> Crossings(TwinModel twin, ReportGraph graph)
        {
            var crossings = new List<string>();
            foreach (var actuator in twin.Components.Where(c => c.IsActuator))
                foreach (var state in actuator.States)
                    foreach (var reference in state.Interlocks)
                    {
                        var source = reference.Component;
                        if (source == null || source.IsProcess) continue;
                        if (graph.SameDomain(source.Name, actuator.Name)) continue;
                        crossings.Add($"{actuator.Name} blocked on {source.Name}");
                    }
            return crossings;
        }

        private static ReportGraph Assemble(
            TwinModel twin, ControllerAllocation allocation, bool merged,
            IReadOnlyList<string> declaredDischarge, IReadOnlyList<string> detouredChain,
            Mapping.TargetIndex targets)
        {
            var edges = RequiredEdges(twin, allocation);

            bool OnDischarge(TransportEdge e) =>
                declaredDischarge.Contains(e.Target, StringComparer.OrdinalIgnoreCase);
            // A target that DECLARES it carries a chain another one commands splices its components onto
            // the commanding ring. Declared, not inferred from running no process of its own: a target
            // can legitimately host none and still not be something another controller reaches into.
            bool OnDetour(TransportEdge e) =>
                targets.IsRegistered(e.To) && targets.Of(e.To).ChainCommandedBy != null;

            // The rings BEFORE any carrier: targets hosting one station share theirs, everything else is
            // its own. This is what decides whether an edge needs a carrier at all.
            var native = Partition(targets);

            // A merged ring already spans both ends, as does a pair on one native ring.
            bool Spanned(TransportEdge e) =>
                merged || native.Find(allocation.Of(e.Source)) == native.Find(allocation.Of(e.Target));

            var unrouted = edges
                .Where(e => !Spanned(e) && !OnDischarge(e) && !OnDetour(e)).ToList();
            if (unrouted.Count > 0)
                throw new InvalidOperationException(
                    $"[Transport] {unrouted.Count} model dependenc(ies) cross a controller boundary with no " +
                    $"carrier: {string.Join("; ", unrouted.Select(e => e.ToString()))}. Merge the rings, " +
                    "declare the component on smc-rig.yml crossRingSegment, host it on a device whose " +
                    "components detour onto the commanding ring, or place it on the controller that needs " +
                    "it. Generation stops rather than emitting a dependency nothing can carry.");

            // The discharge segment is selected by the edges that NEED it, not by what the twin declares.
            var discharge = edges.Any(OnDischarge)
                ? new List<string>(declaredDischarge)
                : (IReadOnlyList<string>)Array.Empty<string>();

            var commanded = new HashSet<string>(
                edges.Where(e => e.Kind == TransportEdge.Command).Select(e => e.Target),
                StringComparer.OrdinalIgnoreCase);

            // The FINISHED rings: the native partition, plus every carrier the model just selected. A
            // detoured device joins the ring of whichever target drives it; a merged ring joins them all.
            var finished = Partition(targets);
            foreach (var e in edges.Where(OnDetour)) finished.Union(e.From, e.To);
            if (merged) finished.UnionAll();

            // A discharge member stays on its own target but reports onto the commanding target's ring,
            // so it is carried per COMPONENT rather than by folding two targets together.
            var carriedOnto = new Dictionary<string, PlcAssignment>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in edges.Where(OnDischarge)) carriedOnto[e.Target] = e.From;

            return new ReportGraph(allocation, merged, finished.Domains(), carriedOnto,
                discharge, detouredChain, discharge.Where(commanded.Contains).ToList());
        }

        // Targets grouped by the ring they share. Every declared target starts alone; a carrier joins two.
        // The NATIVE rings: a ring owner and every stand-in that shares it. Union-find over the
        // declared relationships, so two targets are on one ring because one of them says it stands in
        // for the other - not because they happen to share a flag that names a station in one plant.
        private static TargetPartition Partition(Mapping.TargetIndex targets)
        {
            var p = new TargetPartition(targets.All.Select(t => t.Plc));
            foreach (var t in targets.All.Where(t => t.StandsInFor != null))
                p.Union(t.StandsInFor!.Value, t.Plc);
            return p;
        }


        // Everything the twin makes one controller depend on another for: a process commanding a component
        // it does not host, and a process watching one. Phase handoffs go to ProcessHandoffPlan instead.
        private static IReadOnlyList<TransportEdge> RequiredEdges(
            TwinModel twin, ControllerAllocation allocation)
        {
            var edges = new List<TransportEdge>();
            foreach (var process in twin.Processes)
            {
                var from = allocation.Of(process.Name);
                if (from == PlcAssignment.Unknown) continue;

                void Add(TwinComponent c, string kind)
                {
                    var to = allocation.Of(c.Name);
                    if (to == PlcAssignment.Unknown || to == from) return;
                    edges.Add(new TransportEdge(kind, process.Name, from, c.Name, to));
                }

                foreach (var c in twin.CommandedBy(process)) Add(c, TransportEdge.Command);
                foreach (var c in twin.Components.Where(x => !x.IsProcess))
                    if (twin.ObservingProcesses(c).Any(p =>
                            string.Equals(p.Name, process.Name, StringComparison.OrdinalIgnoreCase)) &&
                        !twin.CommandingProcesses(c).Any(p =>
                            string.Equals(p.Name, process.Name, StringComparison.OrdinalIgnoreCase)))
                        Add(c, TransportEdge.Observation);
            }
            return edges;
        }
    }

    // One report ring in the finished topology. Its identity is the set of targets that share it, so it
    // has no name to spell and renaming a station cannot change which components land in one state_table.
    public readonly record struct ReportDomainId(PlcAssignment Representative)
    {
        // A component the roster places nowhere reports onto no ring, and so shares one with nothing.
        public static ReportDomainId Unplaced => new(PlcAssignment.Unknown);

        public override string ToString() => $"ring[{Representative}]";
    }

    // Which targets share a ring. Every target starts alone and a carrier joins two; the ring is then the
    // connected component, whatever the targets are called.
    internal sealed class TargetPartition
    {
        private readonly Dictionary<PlcAssignment, PlcAssignment> _parent = new();

        public TargetPartition(IEnumerable<PlcAssignment> targets)
        {
            foreach (var t in targets) _parent[t] = t;
        }

        public PlcAssignment Find(PlcAssignment t)
        {
            if (!_parent.TryGetValue(t, out var up)) return PlcAssignment.Unknown;
            while (!up.Equals(_parent[up])) up = _parent[up] = _parent[_parent[up]];
            return up;
        }

        public void Union(PlcAssignment a, PlcAssignment b)
        {
            var (ra, rb) = (Find(a), Find(b));
            if (ra == PlcAssignment.Unknown || rb == PlcAssignment.Unknown || ra.Equals(rb)) return;
            _parent[rb] = ra;
        }

        public void UnionAll()
        {
            var all = _parent.Keys.ToList();
            for (int i = 1; i < all.Count; i++) Union(all[0], all[i]);
        }

        public IReadOnlyDictionary<PlcAssignment, ReportDomainId> Domains() =>
            _parent.Keys.ToDictionary(t => t, t => new ReportDomainId(Find(t)));
    }

    // One dependency the model places across a controller boundary.
    public sealed record TransportEdge(
        string Kind, string Source, PlcAssignment From, string Target, PlcAssignment To)
    {
        public const string Command = "commands";
        public const string Observation = "observes";

        public override string ToString() => $"{Source}({From}) {Kind} {Target}({To})";
    }
}
