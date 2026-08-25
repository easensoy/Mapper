using System;
using System.Collections.Generic;
using System.Linq;
using static CodeGen.Translation.Process.Recipes.TransitionChainParser;
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
    // A carrier is a splice the profile declares and the MODEL selects: the merged ring, the cover detour,
    // the discharge segment. An edge no carrier covers is named and generation stops.
    public sealed class ReportGraph
    {
        private const string FeedRing = "feed";
        private const string AssemblyRing = "assembly";
        private const string OneRing = "*";

        private readonly ControllerAllocation _allocation;
        private readonly bool _merged;
        private readonly HashSet<string> _reHomed;

        public IReadOnlyList<TransportEdge> Edges { get; }

        // The discharge members another controller COMMANDS. Their bring-up crosses a device boundary, so
        // both canvases init them after the process rather than in front of it.
        public IReadOnlyList<string> DischargeTail { get; }
        public IReadOnlyList<string> DischargeSegment { get; }
        public IReadOnlyList<string> DetouredChain { get; }
        public bool RingsMerged => _merged;

        private ReportGraph(ControllerAllocation allocation, bool merged, IEnumerable<string> reHomed,
            IReadOnlyList<TransportEdge> edges, IReadOnlyList<string> discharge, IReadOnlyList<string> detour,
            IReadOnlyList<string> dischargeTail)
        {
            DischargeTail = dischargeTail;
            _allocation = allocation;
            _merged = merged;
            _reHomed = new HashSet<string>(reHomed, StringComparer.OrdinalIgnoreCase);
            Edges = edges;
            DischargeSegment = discharge;
            DetouredChain = detour;
        }

        // The ring a component's announcements reach in the finished topology.
        public string Domain(string? name) =>
            _merged ? OneRing
            : _allocation.IsFeedSide(name) && !_reHomed.Contains((name ?? string.Empty).Trim())
                ? FeedRing
                : AssemblyRing;

        public bool SameDomain(string? a, string? b) =>
            string.Equals(Domain(a), Domain(b), StringComparison.Ordinal);

        // Wiring topology, not recipe: do the per-controller report rings fold into one?
        //
        // The phase transport carries only a cycle's BOUNDARIES. A Feed-side process waiting on another
        // controller's process in the MIDDLE of its own cycle is holding material for the downstream
        // station and must observe it live, which a boundary handshake cannot express, so the rings must
        // become one. Derived from the transition chain and the allocation; no process name participates.
        private static bool ProcessesNeedOneRing(TwinModel twin, ControllerAllocation allocation)
        {
            foreach (var proc in twin.Processes)
            {
                if (!allocation.IsFeedSide(proc.Name)) continue;

                var chain = OrderStatesByTransitionChain(proc.Source.States);
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
            IReadOnlyList<string> declaredDischarge, IReadOnlyList<string> detouredChain)
        {
            var graph = Assemble(twin, allocation, ProcessesNeedOneRing(twin, allocation),
                declaredDischarge, detouredChain);

            // Folding the rings into one is the declared carrier that spans every domain, so it is what
            // an interlock reaching across them needs. Applied only when something actually needs it.
            if (!graph.RingsMerged && Crossings(twin, graph).Count > 0)
                graph = Assemble(twin, allocation, merged: true, declaredDischarge, detouredChain);

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
            IReadOnlyList<string> declaredDischarge, IReadOnlyList<string> detouredChain)
        {
            var edges = RequiredEdges(twin, allocation);

            bool OnDischarge(TransportEdge e) =>
                declaredDischarge.Contains(e.Target, StringComparer.OrdinalIgnoreCase);
            // A device that runs no process of its own cannot act alone: all its components splice onto
            // the ring driving them. Asked of the DEVICE, so a new component on it is carried too.
            var hostsAProcess = new HashSet<PlcAssignment>(
                twin.Processes.Select(p => allocation.Of(p.Name)).Where(t => t != PlcAssignment.Unknown));
            bool OnDetour(TransportEdge e) => !hostsAProcess.Contains(e.To);

            // A merged ring already spans both ends, as does a pair on one native ring.
            bool Spanned(TransportEdge e) =>
                merged ||
                string.Equals(NativeOf(allocation, e.Source), NativeOf(allocation, e.Target),
                    StringComparison.Ordinal);

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
            return new ReportGraph(allocation, merged,
                discharge.Concat(detouredChain), edges, discharge, detouredChain,
                discharge.Where(commanded.Contains).ToList());
        }

        // The ring a component reports on BEFORE any splice: what decides whether an edge needs a carrier.
        private static string NativeOf(ControllerAllocation a, string? name) =>
            a.IsFeedSide(name) ? FeedRing : a.Of(name).ToString();

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

    // One dependency the model places across a controller boundary.
    public sealed record TransportEdge(
        string Kind, string Source, PlcAssignment From, string Target, PlcAssignment To)
    {
        public const string Command = "commands";
        public const string Observation = "observes";

        public override string ToString() => $"{Source}({From}) {Kind} {Target}({To})";
    }
}
