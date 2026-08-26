using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Translation;

namespace CodeGen.Configuration
{
    // Config/layout.yml: the deployment roster and the canvas geometry. Its own header says why the two orderings it declares must stay separate.
    public sealed class LayoutCatalog
    {
        public LayoutGeometry Geometry { get; set; } = new();
        public List<LayoutBand> Bands { get; set; } = new();
        public FrameStyle FrameStyle { get; set; } = new();
        public List<BootFb> BootFbs { get; set; } = new();
        public List<ResourceProfile> Resources { get; set; } = new();

        // The one controller a thing runs on when the model anchors it nowhere: declared, never a fallback.
        public PlcAssignment DefaultTarget { get; set; } = PlcAssignment.Unknown;
        public List<RoleRelation> ResourceRelations { get; set; } = new();

        // In declaration order: the emitted order of a resource's infrastructure stack.
        public List<string> InfraEmitOrder { get; set; } = new();
        public List<RosterEntry> Components { get; set; } = new();
        public FbBodySize FbBody { get; set; } = new();
        public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Read once at the entry point and carried on the run's DeploymentProfile, never reached ambiently.
        public static LayoutCatalog Load() => LayoutCatalogLoader.Catalog;

        public LayoutBand Band(PlcAssignment plc) =>
            Bands.FirstOrDefault(b => b.Plc == plc) ?? LayoutBand.OffCanvas;

        public int RowY(string row) => Geometry.RowY.TryGetValue(row, out int y) ? y : 0;

        // The fixed slot declared for a component, or -1 if it takes a positional one.
        public int StableSlotOf(string name) =>
            Components.FirstOrDefault(e =>
                string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))?.StableSlot ?? -1;
    }

    public sealed class LayoutGeometry
    {
        public int ColumnPitchX { get; set; }
        public int FrameOriginY { get; set; }
        public int FrameHeight { get; set; }
        public CanvasPoint DeviceCanvasOrigin { get; set; } = new();
        public FramePad FramePadding { get; set; } = new();
        public Dictionary<string, int> RowY { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Where a deployment-injected reporter is drawn: its own column, one pitch per reporter.
        public InjectedReporterGrid InjectedReporters { get; set; } = new();
    }

    public sealed class InjectedReporterGrid
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int RowPitch { get; set; }
    }

    public sealed class CanvasPoint
    {
        public int X { get; set; }
        public int Y { get; set; }
    }

    public sealed class FramePad
    {
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
    }

    // Frame-sizing allowance per FB Type. Only height varies; an unlisted type takes the default.
    public sealed class FbBodySize
    {
        public int Width { get; set; }
        public int DefaultHeight { get; set; }
        public Dictionary<string, int> HeightByType { get; set; } = new(StringComparer.Ordinal);

        public int HeightOf(string? fbType) =>
            fbType != null && HeightByType.TryGetValue(fbType, out int h) ? h : DefaultHeight;
    }

    public sealed class LayoutBand
    {
        public PlcAssignment Plc { get; set; }
        public int FrameOriginX { get; set; }
        public int ColumnBaseX { get; set; }
        public int FrameWidth { get; set; }

        // The zone rectangle drawn around this band, or null where the band shares another's zone.
        public BandFrame? Frame { get; set; }

        // An unallocated name draws at the origin rather than throwing: it is never emitted, so never read.
        public static readonly LayoutBand OffCanvas = new();
    }

    public sealed class BandFrame
    {
        public string Name { get; set; } = string.Empty;
        public string Colour { get; set; } = string.Empty;
        public string Caption { get; set; } = string.Empty;
    }

    public sealed class FrameStyle
    {
        public string Font { get; set; } = string.Empty;
        public string TextColour { get; set; } = string.Empty;
        public string TextAlignment { get; set; } = string.Empty;
    }

    // One controller's infrastructure: its report tag and the instance filling each role it declares.
    public sealed class ResourceProfile
    {
        public PlcAssignment Plc { get; set; }
        public string Label { get; set; } = string.Empty;
        public Dictionary<string, string> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    // An adapter wire between two roles. Rendered only where the resource declares both.
    public sealed class RoleRelation
    {
        public string From { get; set; } = string.Empty;
        public string FromPort { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
        public string ToPort { get; set; } = string.Empty;

        // Not a direct wire: it names the two ENDS of a component chain whose middle the layout decides.
        public string Chain { get; set; } = string.Empty;
        public bool IsChain => !string.IsNullOrWhiteSpace(Chain);
    }

    public sealed class BootFb
    {
        public string Name { get; set; } = string.Empty;
        public int X { get; set; }
        public int Y { get; set; }
        public int SysresX { get; set; }
        public int SysresY { get; set; }
    }

    // One declaration per known component; everything optional is an OVERRIDE, absent means "decide from the model".
    public sealed class RosterEntry
    {
        public string Name { get; set; } = string.Empty;
        public PlcAssignment Plc { get; set; }
        public int Column { get; set; }
        public string Row { get; set; } = string.Empty;

        // Position in the state_table id ordering, within its role. Absent = allocated after the ranked ones, so adding a component never renumbers an existing one.
        public int? IdRank { get; set; }

        // Position on the station-adapter chain. Absent = not threaded onto it.
        public int? CasBusRank { get; set; }

        // A fixed slot this reporter always takes, reserved before any positional placement.
        public int? StableSlot { get; set; }
    }

    internal static class LayoutCatalogLoader
    {
        private static readonly YamlConfigFile<LayoutCatalog> _file =
            new("Config", "layout.yml") { OnLoaded = LayoutCatalogValidator.Validate };

        public static LayoutCatalog Catalog => _file.Load();
    }

    internal static class LayoutCatalogValidator
    {
        public static void Validate(LayoutCatalog c)
        {
            var errors = new List<string>();
            if (c.Components.Count == 0) errors.Add("components is empty: nothing would be placed on the canvas");
            foreach (var g in c.Resources.GroupBy(r => r.Plc).Where(g => g.Count() > 1))
                errors.Add($"resource '{g.Key}' is declared {g.Count()} times");
            var declaredRoles = new HashSet<string>(
                c.Resources.SelectMany(r => r.Roles.Keys), StringComparer.OrdinalIgnoreCase);
            foreach (var r in c.Resources)
            {
                if (string.IsNullOrWhiteSpace(r.Label)) errors.Add($"resource '{r.Plc}' has no label");
                foreach (var kv in r.Roles)
                    if (string.IsNullOrWhiteSpace(kv.Value))
                        errors.Add($"resource '{r.Plc}' role '{kv.Key}' names no instance");
            }
            foreach (var rel in c.ResourceRelations)
            {
                if (string.IsNullOrWhiteSpace(rel.FromPort) || string.IsNullOrWhiteSpace(rel.ToPort))
                    errors.Add($"resourceRelations '{rel.From}'->'{rel.To}' is missing a port");
                foreach (var role in new[] { rel.From, rel.To })
                    if (!declaredRoles.Contains(role))
                        errors.Add($"resourceRelations references role '{role}', which no resource declares");
            }
            foreach (var g in c.Components.GroupBy(e => e.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
                errors.Add($"component '{g.Key}' is declared {g.Count()} times");
            foreach (var e in c.Components)
            {
                if (string.IsNullOrWhiteSpace(e.Name)) errors.Add("a component row has no name");
                if (!c.Geometry.RowY.ContainsKey(e.Row))
                    errors.Add($"component '{e.Name}' sits on row '{e.Row}', which geometry.rowY does not define");
                if (c.Bands.All(b => b.Plc != e.Plc))
                    errors.Add($"component '{e.Name}' is allocated to '{e.Plc}', which declares no band");
            }
            var known = new HashSet<string>(c.Components.Select(e => e.Name), StringComparer.Ordinal);
            // Two components on one rank or one fixed slot make a WAIT mean whichever reported last.
            foreach (var g in c.Components.Where(e => e.IdRank.HasValue)
                         .GroupBy(e => (Role: e.Row, Rank: e.IdRank!.Value))
                         .Where(g => g.Count() > 1))
                errors.Add($"idRank {g.Key.Rank} is claimed by {g.Count()} '{g.Key.Role}' components: " +
                           string.Join(", ", g.Select(e => e.Name)));
            foreach (var g in c.Components.Where(e => e.CasBusRank.HasValue)
                         .GroupBy(e => e.CasBusRank!.Value).Where(g => g.Count() > 1))
                errors.Add($"casBusRank {g.Key} is claimed by {g.Count()} components: " +
                           string.Join(", ", g.Select(e => e.Name)));
            foreach (var g in c.Components.Where(e => e.StableSlot.HasValue)
                         .GroupBy(e => e.StableSlot!.Value).Where(g => g.Count() > 1))
                errors.Add($"stableSlot {g.Key} is reserved by {g.Count()} components: " +
                           string.Join(", ", g.Select(e => e.Name)));
            foreach (var kv in c.Aliases)
            {
                if (!known.Contains(kv.Value))
                    errors.Add($"alias '{kv.Key}' points at '{kv.Value}', which is not a declared component");
                if (known.Contains(kv.Key))
                    errors.Add($"alias '{kv.Key}' is also a declared component, so the alias can never resolve");
            }

            if (c.Geometry.ColumnPitchX <= 0) errors.Add("geometry.columnPitchX must be positive");
            if (c.FbBody.Width <= 0) errors.Add("fbBody.width must be positive");
            if (c.FbBody.DefaultHeight <= 0) errors.Add("fbBody.defaultHeight must be positive");
            // A zero pad or origin produces frames that do not enclose their FBs, which EAE then grows westward around a neighbour's zone.
            var geo = c.Geometry;
            if (geo.DeviceCanvasOrigin.X <= 0 || geo.DeviceCanvasOrigin.Y <= 0)
                errors.Add("geometry.deviceCanvasOrigin must be positive in both axes");
            if (geo.FramePadding.Left <= 0 || geo.FramePadding.Top <= 0 ||
                geo.FramePadding.Right <= 0 || geo.FramePadding.Bottom <= 0)
                errors.Add("geometry.framePadding must be positive on all four sides");

            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "layout.yml is invalid:" + Environment.NewLine + "  - " +
                    string.Join(Environment.NewLine + "  - ", errors));
        }
    }
}
