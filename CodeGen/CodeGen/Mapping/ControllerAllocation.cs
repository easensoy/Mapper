using System;
using System.Collections.Generic;
using CodeGen.Translation;

namespace CodeGen.Mapping
{
    // Which controller runs each component, and therefore which stateRprtCmd report ring its reports
    // circulate on.
    //
    // The single allocation authority. Every topology question -- "does this controller host the Feed
    // station?", "do these two processes share a ring?", "which resource does this FB land on?" -- is
    // answered here, from the deployment roster, rather than re-derived at each call site from the shape
    // of a component name. Allocation is a deployment decision, so it belongs to the roster; a name is
    // only ever the key used to look it up.
    public sealed class ControllerAllocation
    {
        // Alternate spellings the twin may use for a component the roster already allocates. Two names for
        // one physical device is a naming fact, not an allocation rule, so the alias resolves to the
        // registered component and inherits whatever controller the roster gives it.
        private static readonly (string Alias, string Registered)[] Aliases =
        {
            ("Rejector", "Ejector"),
            ("Robot_Pick_And_Place1", "Robot"),
        };

        private readonly IReadOnlyDictionary<string, ComponentEntry> _roster;

        private ControllerAllocation(IReadOnlyDictionary<string, ComponentEntry> roster) => _roster = roster;

        // The allocation for the routing mode this generation runs in (M262, full-RevPi or the partial
        // swap). Taken by value so a caller holds a stable snapshot for the whole run.
        public static ControllerAllocation Current => new(ComponentRegistry.ByName);

        public PlcAssignment Of(string? componentName)
        {
            var entry = Lookup(componentName);
            return entry?.Plc ?? PlcAssignment.Unknown;
        }

        // Is this component hosted by whichever controller runs the Feed station (M262 or the RevPi)?
        public bool IsFeedSide(string? componentName) => ControllerMap.IsFeedController(Of(componentName));

        public bool IsOn(string? componentName, PlcAssignment plc) => Of(componentName) == plc;

        // The report ring a component's announcements circulate on. Each controller runs its own ring, so
        // the controller IS the ring -- unless the topology folds them into one, which makes every
        // announcement reachable from every process and collapses the distinction.
        public string RingOf(string? componentName, bool ringsMerged) =>
            ringsMerged ? MergedRing : Of(componentName).ToString();

        public bool SameRing(string? a, string? b, bool ringsMerged) =>
            string.Equals(RingOf(a, ringsMerged), RingOf(b, ringsMerged), StringComparison.Ordinal);

        // The one ring every component sits on once the topology merges them.
        private const string MergedRing = "*";

        private ComponentEntry? Lookup(string? componentName)
        {
            if (string.IsNullOrWhiteSpace(componentName)) return null;
            var name = componentName.Trim();
            if (_roster.TryGetValue(name, out var direct)) return direct;
            foreach (var (alias, registered) in Aliases)
                if (string.Equals(alias, name, StringComparison.OrdinalIgnoreCase) &&
                    _roster.TryGetValue(registered, out var aliased))
                    return aliased;
            return null;
        }
    }
}
