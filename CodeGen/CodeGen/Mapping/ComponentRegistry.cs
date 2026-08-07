using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Translation;

namespace CodeGen.Mapping
{
    // Canonical row labels for the SMC rig syslay canvas; Y is per-PLC (see LayoutGrid.RowY).
    public enum LayoutRow
    {
        Boot,       // Bootstrap FBs (DPAC_FULLINIT / plcStart) — fixed coords, no column grid
        Floating,   // Top-of-canvas row (Y=200) — e.g. MqttConn
        Hmi,
        Station,    // Area / Station / Terminator
        Process,    // Processes AND sensors share this row
        Sensor,     // Alias for Process (same Y)
        Actuator,
    }

    // One canonical SMC component registration in SYSLAY (shared-canvas) coordinates.
    public sealed record ComponentEntry(
        string Name,
        PlcAssignment Plc,
        string Resource,
        int Column,
        LayoutRow Row,
        int X,
        int Y,
        string ProcessOwner);

    // The deployment roster, projected from Config/layout.yml: which controller each component runs on,
    // where it sits on the canvas, and which Process FB commands it. Lens classes LayoutGrid (geometry),
    // ControllerMap/ControllerAllocation (controller + resource) and TemplateMap (CAT type) read it.
    //
    // Adding a component to the rig is a row in layout.yml, not a change here. The ORDER of the two
    // projections below is load-bearing and layout.yml documents why.
    public static class ComponentRegistry
    {
        // All canonical entries for the active routing mode, keyed by component name.
        // M262 mode = the roster as declared. Full-RevPi relocates EVERY M262 component onto the RevPi
        // resource (device substitution, M262 deleted). Partial-RevPi relocates only the named subset
        // (RevPiComponents), M262 kept — unchanged canvas coordinates in every mode.
        public static IReadOnlyDictionary<string, ComponentEntry> ByName => Cached();

        private static readonly Dictionary<string, IReadOnlyDictionary<string, ComponentEntry>> _cache = new();

        // Routing-mode token: "M262" (default) | "RevPi-full" (whole-feed swap, M262 deleted) |
        // "RevPi-partial:<sorted set>" (only the named components on RevPi, M262 kept). One authority.
        private static IReadOnlyDictionary<string, ComponentEntry> Cached()
        {
            string key = MapperConfig.FeedStationController == FeedController.RevPi ? "RevPi-full"
                : MapperConfig.RevPiComponents.Count > 0
                    ? "RevPi-partial:" + string.Join(",", MapperConfig.RevPiComponents.OrderBy(n => n, StringComparer.Ordinal))
                    : "M262";
            if (_cache.TryGetValue(key, out var cached)) return cached;

            var declared = Build();
            IReadOnlyDictionary<string, ComponentEntry> result =
                MapperConfig.FeedStationController == FeedController.RevPi
                    ? Relocate(declared, _ => true)
                    : MapperConfig.RevPiComponents.Count > 0
                        ? Relocate(declared, e => MapperConfig.RevPiComponents.Contains(e.Name))
                        : declared;
            _cache[key] = result;
            return result;
        }

        // Move the selected M262 (Feed-station) entries onto the RevPi resource, keeping their canvas X/Y
        // so the Feed station renders in the same band. M580/BX1/Boot rows are untouched, and a name that
        // is not in the M262 partition is ignored.
        private static IReadOnlyDictionary<string, ComponentEntry> Relocate(
            IReadOnlyDictionary<string, ComponentEntry> src, Func<ComponentEntry, bool> selected)
        {
            var revPiResource = ControllerMap.ResourceForPlc(PlcAssignment.RevPi);
            return src.Values
                .Select(e => e.Plc == PlcAssignment.M262 && selected(e)
                    ? e with { Plc = PlcAssignment.RevPi, Resource = revPiResource }
                    : e)
                .ToDictionary(r => r.Name, r => r, StringComparer.Ordinal);
        }

        private static IReadOnlyDictionary<string, ComponentEntry> Build()
        {
            var layout = LayoutCatalog.Current;
            var rows = layout.BootFbs
                .Select(b => new ComponentEntry(b.Name, PlcAssignment.Unknown, string.Empty,
                    -1, LayoutRow.Boot, b.X, b.Y, string.Empty))
                .Concat(layout.Components.Select(e =>
                {
                    var band = layout.Band(e.Plc);
                    return new ComponentEntry(e.Name, e.Plc,
                        ControllerMap.ResourceForPlc(e.Plc),
                        e.Column, Enum.Parse<LayoutRow>(e.Row, ignoreCase: true),
                        band.ColumnBaseX + e.Column * layout.Geometry.ColumnPitchX,
                        layout.RowY(e.Row),
                        e.Owner ?? string.Empty);
                }));
            return rows.ToDictionary(r => r.Name, r => r, StringComparer.Ordinal);
        }

        // ── Ordered projections ──────────────────────────────────────────────────

        public static IReadOnlyList<string> IdOrderSensors => LayoutCatalog.Current.IdOrder.Sensors;

        // includeRobotTail appends the UR3e, which only participates when the cross-PLC discharge is on.
        public static IReadOnlyList<string> IdOrderActuators(bool includeRobotTail)
        {
            var order = LayoutCatalog.Current.IdOrder;
            return includeRobotTail
                ? order.Actuators.Concat(order.RobotTail).ToList()
                : order.Actuators;
        }

        public static IReadOnlyList<string> CaSBusOrder => LayoutCatalog.Current.CasBusOrder;

        // ── Generic lookup ───────────────────────────────────────────────────────

        public static ComponentEntry? Get(string? name) =>
            string.IsNullOrEmpty(name) ? null : ByName.TryGetValue(name!, out var e) ? e : null;

        public static bool Contains(string? name) =>
            !string.IsNullOrEmpty(name) && ByName.ContainsKey(name!);
    }
}
