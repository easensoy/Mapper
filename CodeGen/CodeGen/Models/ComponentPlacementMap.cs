using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Translation;

namespace CodeGen.Models
{
    /// <summary>
    /// Canonical row labels for the SMC rig syslay canvas. Each row maps to a
    /// per-PLC Y coordinate (see <see cref="ComponentPlacementMap.RowY"/>).
    /// </summary>
    public enum LayoutRow
    {
        /// <summary>Bootstrap FBs (DPAC_FULLINIT / plcStart) — fixed coordinates, no column grid.</summary>
        Boot,
        /// <summary>HMI faceplate row (top), Y=2000 on M262/M580.</summary>
        Hmi,
        /// <summary>Structural row (Area / Station / Terminator), Y=2900.</summary>
        Station,
        /// <summary>Process FB row, Y=4000.</summary>
        Process,
        /// <summary>Sensor row — shares Y=4000 with Process on M262/M580; same Y=4000 on BX1.</summary>
        Sensor,
        /// <summary>Actuator row — Y=5400 on M262, Y=6500 on M580 and BX1.</summary>
        Actuator,
    }

    /// <summary>
    /// One canonical SMC component placement. Positions are SYSLAY (shared-canvas)
    /// coordinates; per-PLC sysres files apply a translateToOrigin shift in
    /// <c>ResourceWireEmitter.ApplyCanonicalLayout</c> so M580/BX1 sysres canvases
    /// land at their own local origin (translation logic unchanged by this map).
    /// </summary>
    public sealed record ComponentPlacement(
        string Name,
        PlcAssignment Plc,
        string Resource,
        int Column,
        LayoutRow Row,
        int X,
        int Y,
        string ProcessOwner);

    /// <summary>
    /// Single source of truth for Component → PLC / Resource / Position / Owner.
    ///
    /// Before this map, the same SMC partition was scattered across:
    /// <list type="bullet">
    ///   <item><c>HcfSymbolIndex.NameBasedPlcGuess</c> — 17-name hardcoded fallback.</item>
    ///   <item><c>SysresFbMirror.BucketFor</c> — M580 structural names hard-routed.</item>
    ///   <item><c>SystemLayoutInjector.PlcZoneActuatorPosition / PlcZoneSensorPosition</c>
    ///         — initial FB column placeholders.</item>
    ///   <item><c>SystemLayoutInjector.AddFrame(FRAME_Station1/Station2_M580/Station2_BX1, …)</c>
    ///         — three frame X/Width values, hand-edited.</item>
    ///   <item><c>ResourceWireEmitter.CanonicalLayout</c> — 28-entry final position rewrite.</item>
    ///   <item><c>BuildStation2Wiring</c> — M580 filter via <c>NameBasedPlcGuess</c>.</item>
    /// </list>
    /// When any of those drifted out of sync (most recently the BX1 frame at X=3340 W=32020
    /// because BX1-zone actuators sat past the previous frame right edge), EAE's
    /// <c>MoveStyle="AnyContained"</c> auto-grew the frame westward to find them and
    /// swallowed neighbouring stations.
    ///
    /// This map collapses the 6 sites into one keyed-by-name table. Downstream code
    /// derives everything:
    /// <list type="bullet">
    ///   <item>FB syslay X = <see cref="ColumnBaseX"/> + Column × <see cref="ColumnPitchX"/>.</item>
    ///   <item>FB syslay Y = <see cref="RowY"/>(Plc, Row).</item>
    ///   <item>Frame X = <see cref="FrameOriginX"/>(plc); Frame Width =
    ///         (<see cref="MaxColumn"/>(plc) + 1) × ColumnPitchX +
    ///         <see cref="FrameRightPadding"/>.</item>
    ///   <item>Sysres bucket = <see cref="ResourceOf"/>(name).</item>
    ///   <item>Recipe scope filter = <see cref="ComponentsOwnedBy"/>(processName).</item>
    /// </list>
    ///
    /// Adding a new Control.xml component = one row in <see cref="Build"/>; positions,
    /// frame width, sysres bucket and recipe ownership all follow automatically.
    /// </summary>
    public static class ComponentPlacementMap
    {
        /// <summary>Horizontal pitch between columns (≥ widest FB body + margin).</summary>
        public const int ColumnPitchX = 2500;

        /// <summary>Top edge of every frame on the syslay canvas.</summary>
        public const int FrameOriginY = 1700;

        /// <summary>Default frame height (covers HMI through actuator row + body height).</summary>
        public const int FrameHeight = 5300;

        /// <summary>
        /// Right-side padding past the rightmost column's X. Wide enough to cover the
        /// widest FB body (~3050 for Five_State) so EAE's MoveStyle=AnyContained does
        /// not need to auto-grow the frame.
        /// </summary>
        public const int FrameRightPadding = 600;

        /// <summary>Left edge of the coloured frame for each PLC zone on the syslay.</summary>
        public static int FrameOriginX(PlcAssignment plc) => plc switch
        {
            PlcAssignment.M262 => 1800,
            PlcAssignment.M580 => 11800,
            PlcAssignment.BX1  => 28200,
            _ => 0,
        };

        /// <summary>X coordinate of column 0 for each PLC. Sits FrameOriginX + 200 inside the frame.</summary>
        public static int ColumnBaseX(PlcAssignment plc) => plc switch
        {
            PlcAssignment.M262 => 2000,
            PlcAssignment.M580 => 12200,
            PlcAssignment.BX1  => 29000,
            _ => 0,
        };

        /// <summary>EAE resource name per PLC.</summary>
        public static string ResourceForPlc(PlcAssignment plc) => plc switch
        {
            PlcAssignment.M262 => "M262_RES",
            PlcAssignment.M580 => "RES0",
            PlcAssignment.BX1  => "BX1_RES",
            _ => string.Empty,
        };

        /// <summary>
        /// Y coordinate for each (PLC, Row) pair. M262 and M580 share the upper rows
        /// (HMI=2000, Station=2900, Process/Sensor=4000); their actuator rows differ
        /// (M262=5400, M580=6500) because M580 has the wider process row above. BX1
        /// has no Station/Process rows of its own — its sensor row reuses Y=4000 and
        /// its actuator row sits at Y=6500 (matching M580's grid; the BX1 sysres
        /// translation pulls these down to a local 2000/4500).
        /// </summary>
        public static int RowY(PlcAssignment plc, LayoutRow row) => (plc, row) switch
        {
            (_, LayoutRow.Hmi)     => 2000,
            (_, LayoutRow.Station) => 2900,
            (_, LayoutRow.Process) => 4000,
            (_, LayoutRow.Sensor)  => 4000,
            (PlcAssignment.M262, LayoutRow.Actuator) => 5400,
            (PlcAssignment.M580, LayoutRow.Actuator) => 6500,
            (PlcAssignment.BX1,  LayoutRow.Actuator) => 6500,
            _ => 0,
        };

        /// <summary>The 28 canonical entries, lazily built once and cached.</summary>
        public static IReadOnlyDictionary<string, ComponentPlacement> ByName { get; } = Build();

        private static IReadOnlyDictionary<string, ComponentPlacement> Build()
        {
            // X = ColumnBaseX(plc) + column * ColumnPitchX
            // Y = RowY(plc, row)
            // Resource = ResourceForPlc(plc)
            // Verified against ResourceWireEmitter.CanonicalLayout (line 559) — every
            // (X, Y) below matches the deployed syslay byte-for-byte.
            var rows = new[]
            {
                // ── Bootstrap pair (no PLC / no column / fixed coords) ───────────
                Boot("FB1", 3000, 400),    // DPAC_FULLINIT — top-right
                Boot("FB2",  800, 1100),   // plcStart — below EAE's built-in START

                // ── M262 — Feed Station (PLC_RW_M262, frame X=1800, columns 0..3) ─
                M262("Area_HMI",      column: 0, row: LayoutRow.Hmi,      owner: ""),
                M262("Station1_HMI",  column: 1, row: LayoutRow.Hmi,      owner: ""),
                M262("Area",          column: 0, row: LayoutRow.Station,  owner: ""),
                M262("Station1",      column: 1, row: LayoutRow.Station,  owner: ""),
                M262("Area_Term",     column: 2, row: LayoutRow.Station,  owner: ""),
                M262("PartInHopper",  column: 0, row: LayoutRow.Sensor,   owner: "Feed_Station"),
                M262("PartAtChecker", column: 1, row: LayoutRow.Sensor,   owner: "Feed_Station"),
                M262("Feed_Station",  column: 2, row: LayoutRow.Process,  owner: "Feed_Station"),
                M262("Feeder",        column: 0, row: LayoutRow.Actuator, owner: "Feed_Station"),
                M262("Checker",       column: 1, row: LayoutRow.Actuator, owner: "Feed_Station"),
                M262("Transfer",      column: 2, row: LayoutRow.Actuator, owner: "Feed_Station"),
                M262("Ejector",       column: 3, row: LayoutRow.Actuator, owner: "Feed_Station"),
                // Stn1_Term shares the column-3 actuator slot with Ejector — matches the
                // deployed CanonicalLayout (the terminator is visually compact so the
                // overlap is harmless on the canvas).
                M262("Stn1_Term",     column: 3, row: LayoutRow.Actuator, owner: ""),

                // ── M580 — Assembly + Disassembly (RES0, frame X=11800, columns 0..6) ─
                M580("Station2_HMI",     column: 0, row: LayoutRow.Hmi,      owner: ""),
                M580("Station2",         column: 0, row: LayoutRow.Station,  owner: ""),
                M580("Assembly_Station", column: 0, row: LayoutRow.Process,  owner: "Assembly_Station"),
                M580("Disassembly",      column: 1, row: LayoutRow.Process,  owner: "Disassembly"),
                M580("BearingSensor",    column: 2, row: LayoutRow.Sensor,   owner: "Assembly_Station"),
                M580("ShaftSensor",      column: 3, row: LayoutRow.Sensor,   owner: "Assembly_Station"),
                M580("Bearing_PnP",      column: 0, row: LayoutRow.Actuator, owner: "Assembly_Station"),
                M580("Bearing_Gripper",  column: 1, row: LayoutRow.Actuator, owner: "Assembly_Station"),
                M580("Shaft_Hr",         column: 2, row: LayoutRow.Actuator, owner: "Assembly_Station"),
                M580("Shaft_Vr",         column: 3, row: LayoutRow.Actuator, owner: "Assembly_Station"),
                M580("Shaft_Gripper",    column: 4, row: LayoutRow.Actuator, owner: "Assembly_Station"),
                M580("Clamp",            column: 5, row: LayoutRow.Actuator, owner: "Assembly_Station"),
                M580("Stn2_Term",        column: 6, row: LayoutRow.Actuator, owner: ""),

                // ── BX1 — Cover PnP (BX1_RES, frame X=28200, columns 0..2). No Process
                //    FB on BX1: Assembly_Station drives the BX1 actuators cross-PLC,
                //    so ProcessOwner = "Assembly_Station" for those.
                BX1Row("TopCoverSenosr",    column: 0, row: LayoutRow.Sensor,   owner: "Assembly_Station"),
                BX1Row("CoverPNP_Hr",       column: 0, row: LayoutRow.Actuator, owner: "Assembly_Station"),
                BX1Row("CoverPNP_Vr",       column: 1, row: LayoutRow.Actuator, owner: "Assembly_Station"),
                BX1Row("CoverPnp_Gripper",  column: 2, row: LayoutRow.Actuator, owner: "Assembly_Station"),
            };
            return rows.ToDictionary(r => r.Name, r => r, StringComparer.Ordinal);
        }

        // ── Row builders: resolve X/Y from (Plc, Column, Row) so the table reads as data.

        private static ComponentPlacement OnPlc(string name, PlcAssignment plc,
            int column, LayoutRow row, string owner) =>
            new(name, plc, ResourceForPlc(plc),
                column, row,
                ColumnBaseX(plc) + column * ColumnPitchX,
                RowY(plc, row),
                owner);

        private static ComponentPlacement M262(string name, int column, LayoutRow row, string owner) =>
            OnPlc(name, PlcAssignment.M262, column, row, owner);
        private static ComponentPlacement M580(string name, int column, LayoutRow row, string owner) =>
            OnPlc(name, PlcAssignment.M580, column, row, owner);
        private static ComponentPlacement BX1Row(string name, int column, LayoutRow row, string owner) =>
            OnPlc(name, PlcAssignment.BX1, column, row, owner);
        private static ComponentPlacement Boot(string name, int x, int y) =>
            new(name, PlcAssignment.Unknown, string.Empty,
                -1, LayoutRow.Boot, x, y, string.Empty);

        // ── Lookup APIs ──────────────────────────────────────────────────────────

        /// <summary>PLC that owns this component, or Unknown if the name is not in the map.</summary>
        public static PlcAssignment PlcOf(string? name)
        {
            if (string.IsNullOrEmpty(name)) return PlcAssignment.Unknown;
            return ByName.TryGetValue(name!, out var p) ? p.Plc : PlcAssignment.Unknown;
        }

        /// <summary>Syslay X/Y for this component, or null if the name is not in the map.</summary>
        public static (int X, int Y)? PositionOf(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return ByName.TryGetValue(name!, out var p) ? (p.X, p.Y) : null;
        }

        /// <summary>EAE resource name (M262_RES / RES0 / BX1_RES) for this component, or null.</summary>
        public static string? ResourceOf(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return ByName.TryGetValue(name!, out var p) ? p.Resource : null;
        }

        /// <summary>Process FB that commands this component, or "" if it's shared infra.</summary>
        public static string ProcessOwnerOf(string? name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            return ByName.TryGetValue(name!, out var p) ? p.ProcessOwner : string.Empty;
        }

        /// <summary>Largest column index used by any component on this PLC, or -1 if none.</summary>
        public static int MaxColumn(PlcAssignment plc)
        {
            int max = -1;
            foreach (var p in ByName.Values)
                if (p.Plc == plc && p.Column > max) max = p.Column;
            return max;
        }

        /// <summary>
        /// Frame width sized to fully enclose every column on this PLC plus a right-side
        /// pad: (<see cref="MaxColumn"/> + 1) × <see cref="ColumnPitchX"/> +
        /// <see cref="FrameRightPadding"/>. Returns 0 when the PLC has no components in
        /// the map.
        /// </summary>
        public static int FrameWidth(PlcAssignment plc)
        {
            int max = MaxColumn(plc);
            if (max < 0) return 0;
            return (max + 1) * ColumnPitchX + FrameRightPadding;
        }

        /// <summary>All components belonging to the given PLC, in declaration order.</summary>
        public static IEnumerable<ComponentPlacement> ComponentsOn(PlcAssignment plc) =>
            ByName.Values.Where(p => p.Plc == plc);

        /// <summary>All components commanded by the given Process FB.</summary>
        public static IEnumerable<ComponentPlacement> ComponentsOwnedBy(string processName) =>
            ByName.Values.Where(p =>
                !string.IsNullOrEmpty(p.ProcessOwner) &&
                string.Equals(p.ProcessOwner, processName, StringComparison.Ordinal));
    }
}
