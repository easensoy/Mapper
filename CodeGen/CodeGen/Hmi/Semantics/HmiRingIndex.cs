using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace CodeGen.Hmi
{
    // Report-ring identity, derived from the finished syslay's stateRprtCmd adapter graph.
    //
    // WHY THIS EXISTS. A state_table slot is only meaningful inside the ring that writes it. The same
    // numeric slot is legitimately reused on a different ring - in the SMC model slot 6 is Transfer on
    // the Feed ring and TopCoverSensor on the assembly/discharge ring. Resolving a RuleTable SourceID
    // or a recipe WAIT by CONTROLLER, or by a global lookup, therefore names the wrong component:
    // Ejector's rule "block 0->2 while slot 6 = 2" is about TopCoverSensor, which shares Ejector's
    // ring, and never about Transfer, which does not.
    //
    // Rings are discovered, not configured: every stateRprtCmd edge joins two instances, and each
    // connected component of that graph is one ring. Identity is the ordinally-smallest member name,
    // so it is deterministic and independent of emission order.
    internal sealed class HmiRingIndex
    {
        private readonly IReadOnlyDictionary<string, string> _ringOf;      // instance -> ring id
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _members;
        private readonly IReadOnlyDictionary<(string Ring, int Slot), string> _slotOwner;
        private readonly IReadOnlyList<string> _conflicts;

        private HmiRingIndex(
            IReadOnlyDictionary<string, string> ringOf,
            IReadOnlyDictionary<string, IReadOnlyList<string>> members,
            IReadOnlyDictionary<(string, int), string> slotOwner,
            IReadOnlyList<string> conflicts)
        {
            _ringOf = ringOf;
            _members = members;
            _slotOwner = slotOwner;
            _conflicts = conflicts;
        }

        internal IReadOnlyList<string> Rings => _members.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        internal IReadOnlyList<string> Members(string ring) =>
            _members.TryGetValue(ring, out var m) ? m : Array.Empty<string>();

        internal string? RingOf(string instance) =>
            _ringOf.TryGetValue(instance, out var r) ? r : null;

        internal bool SameRing(string a, string b) =>
            RingOf(a) is { } ra && RingOf(b) is { } rb && ra == rb;

        // Two reporters on ONE ring writing one slot is a real fault - the later report silently
        // overwrites the earlier and every rule reading that slot becomes undefined.
        internal IReadOnlyList<string> Conflicts => _conflicts;

        // Resolve a slot strictly inside the ring that owns it. No controller scope, no global guess:
        // an unresolvable slot is reported as unresolvable so the caller can render it honestly.
        internal string? Resolve(string observerInstance, int slot)
        {
            var ring = RingOf(observerInstance);
            if (ring == null) return null;
            return _slotOwner.TryGetValue((ring, slot), out var owner) ? owner : null;
        }

        internal static HmiRingIndex Build(string syslayPath, IReadOnlyDictionary<string, int> slots)
        {
            var undirected = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            XDocument doc;
            try { doc = XDocument.Load(syslayPath); }
            catch
            {
                return new HmiRingIndex(
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
                    new Dictionary<(string, int), string>(),
                    Array.Empty<string>());
            }

            var adapters = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "AdapterConnections");
            foreach (var c in adapters?.Elements().Where(e => e.Name.LocalName == "Connection")
                              ?? Enumerable.Empty<XElement>())
            {
                var src = (string?)c.Attribute("Source") ?? string.Empty;
                var dst = (string?)c.Attribute("Destination") ?? string.Empty;
                if (!IsReportPort(src) && !IsReportPort(dst)) continue;

                var a = Fb(src);
                var b = Fb(dst);
                if (a.Length == 0 || b.Length == 0) continue;

                if (!undirected.TryGetValue(a, out var sa)) undirected[a] = sa = new HashSet<string>(StringComparer.Ordinal);
                if (!undirected.TryGetValue(b, out var sb)) undirected[b] = sb = new HashSet<string>(StringComparer.Ordinal);
                sa.Add(b);
                sb.Add(a);
            }

            var ringOf = new Dictionary<string, string>(StringComparer.Ordinal);
            var members = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);

            foreach (var start in undirected.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (!visited.Add(start)) continue;

                var component = new List<string> { start };
                var stack = new Stack<string>();
                stack.Push(start);
                while (stack.Count > 0)
                {
                    foreach (var n in undirected[stack.Pop()].OrderBy(k => k, StringComparer.Ordinal))
                        if (visited.Add(n)) { component.Add(n); stack.Push(n); }
                }

                component.Sort(StringComparer.Ordinal);
                var id = component[0];               // deterministic, emission-order independent
                members[id] = component;
                foreach (var n in component) ringOf[n] = id;
            }

            var slotOwner = new Dictionary<(string, int), string>();
            var conflicts = new List<string>();
            foreach (var (name, slot) in slots.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (!ringOf.TryGetValue(name, out var ring)) continue;   // not on any report ring
                var key = (ring, slot);
                if (slotOwner.TryGetValue(key, out var existing))
                {
                    conflicts.Add($"ring '{ring}': slot {slot} is claimed by both '{existing}' and '{name}'.");
                    continue;
                }
                slotOwner[key] = name;
            }

            return new HmiRingIndex(ringOf, members, slotOwner, conflicts);
        }

        private static bool IsReportPort(string endpoint) =>
            endpoint.IndexOf("stateRprtCmd", StringComparison.OrdinalIgnoreCase) >= 0 ||
            endpoint.IndexOf("stateRptCmdAdptr", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string Fb(string endpoint)
        {
            var dot = endpoint.IndexOf('.');
            return dot <= 0 ? endpoint : endpoint[..dot];
        }
    }
}
