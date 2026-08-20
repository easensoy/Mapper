using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Hmi
{
    // Which instances a mode broadcast can actually reach, read off the emitted adapter graph.
    //
    // This is never assumed. The CaS chain is emitted per model - a station whose AreaAdptrIN has no
    // source is simply not on the chain, and every actuator downstream of it keeps whatever mode its
    // CAT initialises to. Deciding reachability by reading the graph means the answer follows the
    // model instead of a comment that goes stale.
    internal sealed class HmiModeReach
    {
        private readonly HashSet<string> _reached;
        private readonly IReadOnlyDictionary<string, List<string>> _edges;
        private readonly IReadOnlyDictionary<string, string> _drives;

        private HmiModeReach(HashSet<string> reached, IReadOnlyDictionary<string, List<string>> edges,
                             IReadOnlyDictionary<string, string> drives)
        {
            _reached = reached;
            _edges = edges;
            _drives = drives;
        }

        // The station/area CORE a faceplate FB feeds, or null if it feeds none.
        //
        // Load-bearing: only the *_CAT companion carries an HMI contract, so the tile the operator
        // sees is the FACEPLATE (Station1_HMI), while the node that actually sits on the mode chain is
        // the CORE (Station1). Asking whether the faceplate is on the chain always answers no - the
        // graph walk deliberately excludes it - which would report every station as unreachable.
        internal string? CoreDrivenBy(string faceplateFb) =>
            _drives.TryGetValue(faceplateFb, out var core) ? core : null;

        // Reachability for an instance, resolved through the faceplate->core link when there is one.
        internal bool ReachesThrough(string instanceName) =>
            Reaches(CoreDrivenBy(instanceName) ?? instanceName);

        internal bool Reaches(string instanceName) => _reached.Contains(instanceName);

        internal IReadOnlyList<string> Unreached(IEnumerable<string> candidates) =>
            candidates.Where(c => !_reached.Contains(c)).OrderBy(c => c, StringComparer.Ordinal).ToList();

        // `canDrive` decides whether a faceplate FB is a LIVE mode source. A station whose HMI
        // adapter is wired but whose placed symbol cannot raise the mode event is not a source: the
        // adapter exists, the operator has no way to use it, and everything downstream keeps the
        // mode its CAT initialises to. Counting it would report Setup as available on actuators that
        // can never leave Automatic.
        internal static HmiModeReach From(
            HmiSyslay syslay, Func<string, bool> canDrive, IReadOnlyList<string> chainPorts)
        {
            // Only the CaS chain carries the mode. The component report ring is an adapter chain as
            // well, and following it would walk from one station's actuators, round the ring and into
            // another controller's - reporting Setup as available on components no mode can reach.
            bool OnChain(SyslayEdge e) =>
                chainPorts.Count == 0 ||
                (chainPorts.Any(x => e.SourcePort.StartsWith(x, StringComparison.Ordinal)) &&
                 chainPorts.Any(x => e.TargetPort.StartsWith(x, StringComparison.Ordinal)));

            var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var sources = new List<string>();
            var drives = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var e in syslay.Adapters)
            {
                if (!OnChain(e)) continue;

                // A faceplate feeding a station/area core is where an operator mode selection enters.
                if (e.TargetPort.IndexOf("HMIAdptrIN", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    drives[e.SourceFb] = e.TargetFb;   // remember which core this faceplate drives
                    if (canDrive(e.SourceFb) && !sources.Contains(e.TargetFb, StringComparer.Ordinal))
                        sources.Add(e.TargetFb);
                    continue;   // the faceplate itself is not a plant node
                }

                if (!edges.TryGetValue(e.SourceFb, out var list)) edges[e.SourceFb] = list = new List<string>();
                if (!list.Contains(e.TargetFb, StringComparer.Ordinal)) list.Add(e.TargetFb);
            }

            var reached = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>(sources);
            foreach (var s in sources) reached.Add(s);
            while (queue.Count > 0)
            {
                var n = queue.Dequeue();
                if (!edges.TryGetValue(n, out var next)) continue;
                foreach (var d in next)
                    if (reached.Add(d)) queue.Enqueue(d);
            }

            return new HmiModeReach(reached, edges, drives);
        }
    }
}
