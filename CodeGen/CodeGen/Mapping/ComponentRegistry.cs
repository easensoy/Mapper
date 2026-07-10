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

    // Single source of truth for the SMC rig partition: which PLC each component runs on,
    // where it sits on the canvas, and which Process FB commands it. Lens classes LayoutGrid
    // (geometry), ControllerMap (PLC/resource/owner) and TemplateMap (CAT type) project from it.
    // A new Control.xml component = one row in Build(); positions/bucket/ownership follow.
    public static class ComponentRegistry
    {
        // All canonical entries for the active routing mode, keyed by component name.
        // M262 mode = the canonical partition (byte-identical). Full-RevPi relocates EVERY M262 component
        // onto the RevPi resource (device substitution, M262 deleted). Partial-RevPi relocates only the
        // named subset (RevPiComponents), M262 kept — unchanged canvas coordinates in every mode.
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
            var m262 = Build();
            IReadOnlyDictionary<string, ComponentEntry> result =
                MapperConfig.FeedStationController == FeedController.RevPi ? RelocateFeedToRevPi(m262)
                : MapperConfig.RevPiComponents.Count > 0 ? RelocateSelectedToRevPi(m262, MapperConfig.RevPiComponents)
                : m262;
            _cache[key] = result;
            return result;
        }

        // Move every M262 (Feed-station) entry onto the RevPi resource, keeping its canvas X/Y so the Feed
        // station renders in the same band. M580/BX1/Boot rows are untouched.
        private static IReadOnlyDictionary<string, ComponentEntry> RelocateFeedToRevPi(
            IReadOnlyDictionary<string, ComponentEntry> src)
        {
            var revPiResource = ControllerMap.ResourceForPlc(PlcAssignment.RevPi);
            return src.Values
                .Select(e => e.Plc == PlcAssignment.M262
                    ? e with { Plc = PlcAssignment.RevPi, Resource = revPiResource }
                    : e)
                .ToDictionary(r => r.Name, r => r, StringComparer.Ordinal);
        }

        // Partial swap: move ONLY the named M262 components onto the RevPi resource; every other component
        // (Transfer/Ejector/Robot/Feed_Station/PartAtAssembly + M580/BX1/Boot) stays put. Names not in the
        // M262 partition are ignored. Canvas X/Y unchanged so the relocated FBs render in the same band.
        private static IReadOnlyDictionary<string, ComponentEntry> RelocateSelectedToRevPi(
            IReadOnlyDictionary<string, ComponentEntry> src, IReadOnlySet<string> selected)
        {
            var revPiResource = ControllerMap.ResourceForPlc(PlcAssignment.RevPi);
            return src.Values
                .Select(e => (e.Plc == PlcAssignment.M262 && selected.Contains(e.Name))
                    ? e with { Plc = PlcAssignment.RevPi, Resource = revPiResource }
                    : e)
                .ToDictionary(r => r.Name, r => r, StringComparer.Ordinal);
        }

        private static IReadOnlyDictionary<string, ComponentEntry> Build()
        {
            // X = ColumnBaseX(plc) + column*ColumnPitchX ; Y = RowY(plc, row) ; Resource = ResourceForPlc(plc).
            var rows = new[]
            {
                Boot("FB1", 3000, 400),    // DPAC_FULLINIT
                Boot("FB2",  800, 1100),   // plcStart

                // M262 — Feed Station (M262_RES)
                M262("Area_HMI",      column: 0, row: LayoutRow.Hmi,      owner: ""),
                M262("Station1_HMI",  column: 1, row: LayoutRow.Hmi,      owner: ""),
                M262("Area",          column: 0, row: LayoutRow.Station,  owner: ""),
                M262("Station1",      column: 1, row: LayoutRow.Station,  owner: ""),
                M262("Area_Term",     column: 2, row: LayoutRow.Station,  owner: ""),
                M262("PartInHopper",  column: 0, row: LayoutRow.Process,  owner: "Feed_Station"),
                M262("PartAtChecker", column: 1, row: LayoutRow.Process,  owner: "Feed_Station"),
                M262("Feed_Station",  column: 2, row: LayoutRow.Process,  owner: "Feed_Station"),
                M262("PartAtAssembly", column: 3, row: LayoutRow.Sensor,   owner: "Feed_Station"), // synth discharge sensor (DI08)
                M262("Feeder",        column: 0, row: LayoutRow.Actuator, owner: "Feed_Station"),
                M262("Checker",       column: 1, row: LayoutRow.Actuator, owner: "Feed_Station"),
                M262("Transfer",      column: 2, row: LayoutRow.Actuator, owner: "Feed_Station"),
                M262("Ejector",       column: 3, row: LayoutRow.Actuator, owner: "Feed_Station"),
                M262("Robot",         column: 4, row: LayoutRow.Actuator, owner: ""),  // UR3e discharge tail (commanded cross-PLC by Disassembly)
                M262("Stn1_Term",     column: 3, row: LayoutRow.Station,  owner: ""),

                // M580 — Assembly + Disassembly (RES0)
                M580("Station2_HMI",     column: 0, row: LayoutRow.Hmi,      owner: ""),
                M580("Station2",         column: 0, row: LayoutRow.Station,  owner: ""),
                M580("Assembly_Station", column: 0, row: LayoutRow.Process,  owner: "Assembly_Station"),
                M580("Disassembly",      column: 1, row: LayoutRow.Process,  owner: "Disassembly"),
                M580("BearingSensor",    column: 2, row: LayoutRow.Process,  owner: "Assembly_Station"),
                M580("ShaftSensor",      column: 3, row: LayoutRow.Process,  owner: "Assembly_Station"),
                M580("Bearing_PnP",      column: 0, row: LayoutRow.Actuator, owner: "Assembly_Station"),
                M580("Bearing_Gripper",  column: 1, row: LayoutRow.Actuator, owner: "Assembly_Station"),
                M580("Shaft_Hr",         column: 2, row: LayoutRow.Actuator, owner: "Assembly_Station"),
                M580("Shaft_Vr",         column: 3, row: LayoutRow.Actuator, owner: "Assembly_Station"),
                M580("Shaft_Gripper",    column: 4, row: LayoutRow.Actuator, owner: "Assembly_Station"),
                M580("Clamp",            column: 5, row: LayoutRow.Actuator, owner: "Assembly_Station"),
                M580("Stn2_Term",        column: 6, row: LayoutRow.Actuator, owner: ""),

                // BX1 — Cover PnP (BX1_RES); covers fold into the M580 flow, no BX1 Process engine.
                BX1("TopCoverSenosr",    column: 0, row: LayoutRow.Process,  owner: ""),
                BX1("CoverPNP_Hr",       column: 0, row: LayoutRow.Actuator, owner: ""),
                BX1("CoverPNP_Vr",       column: 1, row: LayoutRow.Actuator, owner: ""),
                BX1("CoverPnp_Gripper",  column: 2, row: LayoutRow.Actuator, owner: ""),
                BX1("BX1_IO",            column: 3, row: LayoutRow.Actuator, owner: ""),  // EtherNet/IP cover-IO broker

                // MqttConn on BX1 (Soft dPAC) — M262/M580 have no MQTT runtime client (RC50).
                BX1("MqttConn",          column: 0, row: LayoutRow.Floating, owner: ""),
                M262("MqttConn_M262",    column: 0, row: LayoutRow.Floating, owner: ""),
                M580("MqttConn_M580",    column: 0, row: LayoutRow.Floating, owner: ""),
                M262("Telemetry_M262",   column: 2, row: LayoutRow.Hmi,      owner: ""),
                M580("Telemetry_M580",   column: 1, row: LayoutRow.Hmi,      owner: ""),
                BX1 ("Telemetry_BX1",    column: 0, row: LayoutRow.Hmi,      owner: ""),
            };
            return rows.ToDictionary(r => r.Name, r => r, StringComparer.Ordinal);
        }

        // ── Row builders ─────────────────────────────────────────────────────────

        private static ComponentEntry OnPlc(string name, PlcAssignment plc,
            int column, LayoutRow row, string owner) =>
            new(name, plc,
                ControllerMap.ResourceForPlc(plc),
                column, row,
                LayoutGrid.ColumnBaseX(plc) + column * LayoutGrid.ColumnPitchX,
                LayoutGrid.RowY(plc, row),
                owner);

        private static ComponentEntry M262(string name, int column, LayoutRow row, string owner) =>
            OnPlc(name, PlcAssignment.M262, column, row, owner);
        private static ComponentEntry M580(string name, int column, LayoutRow row, string owner) =>
            OnPlc(name, PlcAssignment.M580, column, row, owner);
        private static ComponentEntry BX1(string name, int column, LayoutRow row, string owner) =>
            OnPlc(name, PlcAssignment.BX1, column, row, owner);
        private static ComponentEntry Boot(string name, int x, int y) =>
            new(name, PlcAssignment.Unknown, string.Empty,
                -1, LayoutRow.Boot, x, y, string.Empty);

        // ── Generic lookup ───────────────────────────────────────────────────────

        public static ComponentEntry? Get(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return ByName.TryGetValue(name!, out var e) ? e : null;
        }

        public static bool Contains(string? name) =>
            !string.IsNullOrEmpty(name) && ByName.ContainsKey(name!);
    }
}
