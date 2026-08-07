using System;
using System.Collections.Generic;
using CodeGen.Configuration;
using CodeGen.Translation;

namespace CodeGen.Mapping
{
    // Which controller runs each component, and therefore which stateRprtCmd report ring its reports
    // circulate on.
    //
    // The single allocation authority. Every topology question -- "does this controller host the Feed
    // station?", "do these two processes share a ring?", "which resource does this FB land on?" -- is
    // answered here, from the run's roster, rather than re-derived at each call site from the shape of a
    // component name. Allocation is a deployment decision, so it belongs to the roster; a name is only
    // ever the key used to look it up.
    public sealed class ControllerAllocation
    {
        private readonly DeploymentRoster _roster;
        private readonly IReadOnlyDictionary<string, string> _aliases;

        public ControllerAllocation(DeploymentRoster roster)
        {
            _roster = roster ?? throw new ArgumentNullException(nameof(roster));
            _aliases = roster.Profile.Layout.Aliases;
        }

        public PlcAssignment Of(string? componentName) => Lookup(componentName)?.Plc ?? PlcAssignment.Unknown;

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
            return _roster.Get(name)
                ?? (_aliases.TryGetValue(name, out var registered) ? _roster.Get(registered) : null);
        }
    }
}
