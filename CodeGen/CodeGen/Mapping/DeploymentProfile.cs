using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Mapping
{
    // What the operator chose for THIS run: which Feed components the RevPi hosts instead of the M262.
    //
    // The M262 always runs the Feed station. A whole-station swap is not expressible — PLC_RW_REVPI carries
    // IO for a fixed subset, so relocating the rest would deploy them with no physical channels — so the
    // profile is one set, not a mode plus a set. Immutable, and passed rather than parked in a static: the
    // routing used to travel as process-wide mutable state, which two runs in one process could not share.
    public sealed class DeploymentProfile
    {
        public static readonly DeploymentProfile M262Only = new(Array.Empty<string>());

        public IReadOnlySet<string> RevPiComponents { get; }

        public DeploymentProfile(IEnumerable<string> revPiComponents)
        {
            var selected = new HashSet<string>(
                revPiComponents ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            // PartInHopper is not a free choice: its sensor is physically read by the RevPI_IO Modbus
            // coupler, so moving any Feed component to the RevPi takes the hopper sensor with it.
            if (selected.Count > 0) selected.Add("PartInHopper");
            RevPiComponents = selected;
        }

        // Some Feed components on the RevPi, the M262 keeping the rest — the four-controller coexistence.
        public bool PartialRevPi => RevPiComponents.Count > 0;

        public bool RunsOnRevPi(string? componentName) =>
            !string.IsNullOrEmpty(componentName) && RevPiComponents.Contains(componentName!);

        public override string ToString() =>
            PartialRevPi
                ? "RevPi:" + string.Join(",", RevPiComponents.OrderBy(n => n, StringComparer.Ordinal))
                : "M262";
    }
}
