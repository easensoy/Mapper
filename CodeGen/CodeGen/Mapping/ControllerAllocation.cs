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


        private ComponentEntry? Lookup(string? componentName)
        {
            if (string.IsNullOrWhiteSpace(componentName)) return null;
            var name = componentName.Trim();
            return _roster.Get(name)
                ?? (_aliases.TryGetValue(name, out var registered) ? _roster.Get(registered) : null);
        }
    }
}
