using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;

namespace CodeGen.Mapping
{
    // The deployment inputs for THIS run: the catalog that says where every component runs and is drawn,
    // and which Feed components the operator moved onto the RevPi.
    //
    // The M262 always runs the Feed station. A whole-station swap is not expressible — PLC_RW_REVPI carries
    // IO for a fixed subset, so relocating the rest would deploy them with no physical channels — so the
    // selection is one set, not a mode plus a set. Immutable, and passed rather than parked in a static:
    // the routing and the catalog both used to be reached ambiently from inside the emitters.
    public sealed class DeploymentProfile
    {
        public LayoutCatalog Layout { get; }
        public IReadOnlySet<string> RevPiComponents { get; }

        public DeploymentProfile(IEnumerable<string> revPiComponents, LayoutCatalog layout)
        {
            Layout = layout ?? throw new ArgumentNullException(nameof(layout));
            var selected = new HashSet<string>(
                revPiComponents ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            // Hosting anything on the RevPi takes its coupler's own signals along: device.yml declares
            // which components that target's I/O hardware is the only reader of. A hardware contract of
            // the target, not a name this planner knows.
            if (selected.Count > 0)
                foreach (var required in DeviceConfig.Current.RevPi.AlwaysHosts)
                    selected.Add(required);
            RevPiComponents = selected;
        }

        // The default deployment: the catalog as authored, nothing moved onto the RevPi.
        public static DeploymentProfile M262Only(LayoutCatalog layout) =>
            new(Array.Empty<string>(), layout);

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
