using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace CodeGen.Hmi
{
    // Which instances a mode broadcast can actually reach, derived from the generated syslay's
    // adapter graph.
    //
    // This is never assumed. The CaS chain is emitted per model - a station whose AreaAdptrIN has no
    // source is simply not on the chain, and every actuator downstream of it keeps whatever mode its
    // CAT initialises to. Deciding reachability by reading the graph means the answer follows the
    // model instead of a comment that goes stale.
    internal sealed class HmiModeReach
    {
        private readonly HashSet<string> _reached;
        private readonly IReadOnlyDictionary<string, List<string>> _edges;
        private readonly IReadOnlyList<string> _sources;
        private readonly IReadOnlyDictionary<string, string> _drives;

        private HmiModeReach(HashSet<string> reached, IReadOnlyDictionary<string, List<string>> edges,
                             IReadOnlyList<string> sources, IReadOnlyDictionary<string, string> drives)
        {
            _reached = reached;
            _edges = edges;
            _sources = sources;
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

        // The chain heads - an instance an HMI faceplate feeds directly. Placing that faceplate is
        // what makes the chain operable; the wiring alone only makes it possible.
        internal IReadOnlyList<string> Sources => _sources;

        internal IReadOnlyList<string> Unreached(IEnumerable<string> candidates) =>
            candidates.Where(c => !_reached.Contains(c)).OrderBy(c => c, StringComparer.Ordinal).ToList();

        internal static HmiModeReach FromSyslay(string syslayPath)
        {
            var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var sources = new List<string>();
            var drives = new Dictionary<string, string>(StringComparer.Ordinal);

            XDocument doc;
            try { doc = XDocument.Load(syslayPath); }
            catch { return new HmiModeReach(new HashSet<string>(StringComparer.Ordinal), edges, sources, drives); }

            var adapters = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "AdapterConnections");
            if (adapters == null)
                return new HmiModeReach(new HashSet<string>(StringComparer.Ordinal), edges, sources, drives);

            foreach (var c in adapters.Elements().Where(e => e.Name.LocalName == "Connection"))
            {
                var src = (string?)c.Attribute("Source");
                var dst = (string?)c.Attribute("Destination");
                if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dst)) continue;

                var (sFb, sPort) = Split(src!);
                var (dFb, dPort) = Split(dst!);
                if (sFb.Length == 0 || dFb.Length == 0) continue;

                // A faceplate feeding a station/area core is where an operator mode selection enters.
                if (dPort.IndexOf("HMIAdptrIN", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (!sources.Contains(dFb, StringComparer.Ordinal)) sources.Add(dFb);
                    drives[sFb] = dFb;   // remember which core this faceplate drives
                    continue;   // the faceplate itself is not a plant node
                }

                if (!edges.TryGetValue(sFb, out var list)) edges[sFb] = list = new List<string>();
                if (!list.Contains(dFb, StringComparer.Ordinal)) list.Add(dFb);
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

            sources.Sort(StringComparer.Ordinal);
            return new HmiModeReach(reached, edges, sources, drives);
        }

        private static (string Fb, string Port) Split(string endpoint)
        {
            var dot = endpoint.IndexOf('.');
            return dot <= 0 ? (endpoint, string.Empty) : (endpoint[..dot], endpoint[(dot + 1)..]);
        }
    }
}
