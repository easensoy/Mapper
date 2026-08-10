using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Translation;

namespace CodeGen.Configuration
{
    // Config/layout.yml: the deployment roster and the canvas geometry. See the file's own header for
    // why the two orderings it declares must stay separate.
    public sealed class LayoutCatalog
    {
        public LayoutGeometry Geometry { get; set; } = new();
        public List<LayoutBand> Bands { get; set; } = new();
        public List<BootFb> BootFbs { get; set; } = new();
        public List<RosterEntry> Components { get; set; } = new();
        public IdOrder IdOrder { get; set; } = new();
        public List<string> CasBusOrder { get; set; } = new();
        public FbBodySize FbBody { get; set; } = new();
        public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Read once at the entry point and carried on the run's DeploymentProfile. Not an ambient
        // singleton: nothing below the entry point may reach the catalog without being handed it.
        public static LayoutCatalog Load() => LayoutCatalogLoader.Catalog;

        public LayoutBand Band(PlcAssignment plc) =>
            Bands.FirstOrDefault(b => b.Plc == plc) ?? LayoutBand.OffCanvas;

        public int RowY(string row) => Geometry.RowY.TryGetValue(row, out int y) ? y : 0;
    }

    public sealed class LayoutGeometry
    {
        public int ColumnPitchX { get; set; }
        public int FrameOriginY { get; set; }
        public int FrameHeight { get; set; }
        public CanvasPoint DeviceCanvasOrigin { get; set; } = new();
        public FramePad FramePadding { get; set; } = new();
        public Dictionary<string, int> RowY { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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

    // Frame-sizing allowance per FB Type. Only height varies, so a type is a single number and an
    // unlisted one takes the default -- no table entry is needed just to repeat the common case.
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

        // A component with no band (an unallocated name) draws at the origin rather than throwing: it is
        // never emitted, so its coordinates are never read.
        public static readonly LayoutBand OffCanvas = new();
    }

    public sealed class BootFb
    {
        public string Name { get; set; } = string.Empty;
        public int X { get; set; }
        public int Y { get; set; }
    }

    public sealed class RosterEntry
    {
        public string Name { get; set; } = string.Empty;
        public PlcAssignment Plc { get; set; }
        public int Column { get; set; }
        public string Row { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
    }

    public sealed class IdOrder
    {
        public List<string> Sensors { get; set; } = new();
        public List<string> Actuators { get; set; } = new();
        public List<string> RobotTail { get; set; } = new();
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
            // An id-order name that is not on the roster would be silently skipped, taking its slot with it
            // and shifting every id after it.
            var known = new HashSet<string>(c.Components.Select(e => e.Name), StringComparer.Ordinal);
            foreach (var n in c.IdOrder.Sensors.Concat(c.IdOrder.Actuators)
                         .Concat(c.IdOrder.RobotTail).Concat(c.CasBusOrder))
                if (!known.Contains(n)) errors.Add($"'{n}' is ordered but is not a declared component");
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
            // A zero pad or origin would silently produce frames that do not enclose their FBs, which
            // EAE then grows westward around a neighbour's zone.
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
