using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace CodeGen.Hmi
{
    // One emitted function block, as the finished syslay declares it. The Id IS the EAE TagName, so
    // this is the deployment fact a faceplate binds to - not something the plan can be asked for.
    // Nothing else is taken from the emitted FB: recipes and interlock rules come from the plan.
    internal sealed record SyslayFb(string Id, string Name, string Type);

    // One adapter edge, split into instance and port at both ends.
    internal readonly record struct SyslayEdge(string SourceFb, string SourcePort, string TargetFb, string TargetPort);

    // The generated syslay, read ONCE per generation.
    //
    // Everything the HMI needs from the emitted layout - the FB set that carries the TagNames, and
    // the adapter graph that carries the mode chain - comes from this one parse. A file that cannot
    // be read yields an empty index rather than an exception, because a missing syslay is already
    // reported by the caller that asked for it.
    internal sealed class HmiSyslay
    {
        private HmiSyslay(IReadOnlyList<SyslayFb> fbs, IReadOnlyList<SyslayEdge> adapters)
        {
            Fbs = fbs;
            Adapters = adapters;
            Ids = new HashSet<string>(fbs.Select(f => f.Id), StringComparer.Ordinal);
        }

        internal IReadOnlyList<SyslayFb> Fbs { get; }
        internal IReadOnlyList<SyslayEdge> Adapters { get; }
        internal IReadOnlySet<string> Ids { get; }

        internal static HmiSyslay Empty() =>
            new(Array.Empty<SyslayFb>(), Array.Empty<SyslayEdge>());

        internal static HmiSyslay Load(string syslayPath)
        {
            XDocument doc;
            try { doc = XDocument.Load(syslayPath); }
            catch { return Empty(); }

            var fbs = new List<SyslayFb>();
            foreach (var fb in doc.Descendants().Where(e => e.Name.LocalName == "FB"))
            {
                var id = (string?)fb.Attribute("ID");
                var name = (string?)fb.Attribute("Name");
                var type = (string?)fb.Attribute("Type");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(type)) continue;

                fbs.Add(new SyslayFb(id!, name!, type!));
            }

            var adapters = new List<SyslayEdge>();
            var section = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "AdapterConnections");
            foreach (var c in section?.Elements().Where(e => e.Name.LocalName == "Connection")
                              ?? Enumerable.Empty<XElement>())
            {
                var (sFb, sPort) = Split((string?)c.Attribute("Source"));
                var (dFb, dPort) = Split((string?)c.Attribute("Destination"));
                if (sFb.Length > 0 && dFb.Length > 0) adapters.Add(new SyslayEdge(sFb, sPort, dFb, dPort));
            }

            return new HmiSyslay(fbs, adapters);
        }

        private static (string Fb, string Port) Split(string? endpoint)
        {
            if (string.IsNullOrEmpty(endpoint)) return (string.Empty, string.Empty);
            var dot = endpoint!.IndexOf('.');
            return dot <= 0 ? (endpoint, string.Empty) : (endpoint[..dot], endpoint[(dot + 1)..]);
        }
    }

    // Which component owns a state_table slot, scoped to the report ring that writes it.
    //
    // WHY SCOPE MATTERS. A slot is only meaningful inside the ring that writes it. The same numeric
    // slot is legitimately reused on a different ring - in the SMC model slot 6 is Transfer on the
    // Feed ring and TopCoverSensor on the assembly/discharge ring. Resolving a RuleTable SourceID or
    // a recipe WAIT globally therefore names the wrong component.
    //
    // The ring itself is NOT re-derived here: the plan already decided it, and asking the plan is
    // what keeps the panel's answer and the controller's answer the same one.
    internal sealed class HmiSlotIndex
    {
        private readonly Func<string, string> _ringOf;
        private readonly IReadOnlyDictionary<(string Ring, int Slot), string> _owner;

        private HmiSlotIndex(Func<string, string> ringOf,
                             IReadOnlyDictionary<(string, int), string> owner,
                             IReadOnlyList<string> conflicts)
        {
            _ringOf = ringOf;
            _owner = owner;
            Conflicts = conflicts;
        }

        // Two reporters on ONE ring writing one slot is a real fault - the later report silently
        // overwrites the earlier and every rule reading that slot becomes undefined.
        internal IReadOnlyList<string> Conflicts { get; }

        internal string RingOf(string instance) => _ringOf(instance);

        // An unresolvable slot is reported as unresolvable so the caller can render it honestly.
        internal string? Resolve(string observerInstance, int slot) =>
            _owner.TryGetValue((_ringOf(observerInstance), slot), out var owner) ? owner : null;

        // A cross-ring reference - the handshake case, where a consumer waits on a slot another
        // ring writes and a transport carries it across. Answered ONLY when exactly one instance
        // in the whole plan owns that number: the moment two rings reuse it (slot 6 is Transfer
        // on Feed and TopCoverSensor on assembly) the answer would be a coin toss, and a
        // confidently wrong component name is worse than none.
        internal string? ResolveAnywhere(int slot)
        {
            var owners = _owner.Where(kv => kv.Key.Item2 == slot)
                .Select(kv => kv.Value).Distinct(StringComparer.Ordinal).ToList();
            return owners.Count == 1 ? owners[0] : null;
        }

        internal static HmiSlotIndex Build(Func<string, string> ringOf, IReadOnlyDictionary<string, int> slots)
        {
            var owner = new Dictionary<(string, int), string>();
            var conflicts = new List<string>();

            foreach (var (name, slot) in slots.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var key = (ringOf(name), slot);
                if (owner.TryGetValue(key, out var existing))
                {
                    conflicts.Add($"ring '{key.Item1}': slot {slot} is claimed by both '{existing}' and '{name}'.");
                    continue;
                }
                owner[key] = name;
            }

            return new HmiSlotIndex(ringOf, owner, conflicts);
        }
    }
}
