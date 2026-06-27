using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Translation;

namespace CodeGen.Mapping
{
    /// <summary>
    /// Canonical row labels for the SMC rig syslay canvas. The Y coordinate
    /// for each row is per-PLC (see <see cref="LayoutGrid.RowY"/>).
    /// </summary>
    public enum LayoutRow
    {
        /// <summary>Bootstrap FBs (DPAC_FULLINIT / plcStart) — fixed coordinates, no column grid.</summary>
        Boot,
        /// <summary>Floating row at the top of the canvas (Y=200) — e.g. MqttConn.</summary>
        Floating,
        /// <summary>HMI faceplate row.</summary>
        Hmi,
        /// <summary>Structural row (Area / Station / Terminator).</summary>
        Station,
        /// <summary>Process FB row — Processes AND sensors share this row per the canonical SMC mapping table (both at Y=4000).</summary>
        Process,
        /// <summary>Legacy alias for the Process row — same Y, kept for callers that distinguish sensor placement semantically.</summary>
        Sensor,
        /// <summary>Actuator row — per-PLC Y (M262=5400, M580=6500, BX1=6500).</summary>
        Actuator,
    }

    /// <summary>
    /// One canonical SMC component registration. Positions are SYSLAY (shared-canvas)
    /// coordinates; per-PLC sysres files apply a translateToOrigin shift in
    /// <c>ResourceWireEmitter.ApplyCanonicalLayout</c> so M580/BX1 sysres canvases
    /// land at their own local origin (translation logic unchanged by this map).
    /// </summary>
    public sealed record ComponentEntry(
        string Name,
        PlcAssignment Plc,
        string Resource,
        int Column,
        LayoutRow Row,
        int X,
        int Y,
        string ProcessOwner);

    /// <summary>
    /// Single source of truth for the SMC rig partition — which PLC each component
    /// runs on, where it sits on the canvas, and which Process FB commands it.
    ///
    /// Before this registry the same partition lived scattered across:
    /// <list type="bullet">
    ///   <item><c>HcfSymbolIndex.NameBasedPlcGuess</c> — 17-name hardcoded fallback.</item>
    ///   <item><c>SysresFbMirror.BucketFor</c> — M580 structural names hard-routed.</item>
    ///   <item><c>SystemLayoutInjector.PlcZoneActuatorPosition/SensorPosition</c> — placeholders.</item>
    ///   <item><c>SystemLayoutInjector.AddFrame(FRAME_Station1/Station2_M580/Station2_BX1, …)</c>
    ///         — three frame X/Width values hand-edited.</item>
    ///   <item><c>ResourceWireEmitter.CanonicalLayout</c> — 28-entry final position rewrite.</item>
    ///   <item><c>BuildStation2Wiring</c> — M580 filter via <c>NameBasedPlcGuess</c>.</item>
    /// </list>
    /// When any drifted out of sync (most recently the BX1 frame at X=3340 W=32020),
    /// EAE's <c>MoveStyle="AnyContained"</c> auto-grew frames to find missing FBs and
    /// swallowed neighbouring stations.
    ///
    /// This registry collapses those 6 sites into one keyed-by-name table. Three lens
    /// classes project specific views from it:
    /// <list type="bullet">
    ///   <item><see cref="LayoutGrid"/> — geometry (column/row → X/Y, frame bounds).</item>
    ///   <item><see cref="ControllerMap"/> — PLC, EAE resource name, process owner.</item>
    ///   <item><see cref="TemplateMap"/> — CAT type assignment per component.</item>
    /// </list>
    ///
    /// Adding a new Control.xml component = one row in <see cref="Build"/>; positions,
    /// frame width, sysres bucket and recipe ownership all follow automatically.
    /// </summary>
    public static class ComponentRegistry
    {
        /// <summary>All canonical entries, keyed by component name (case-sensitive).</summary>
        public static IReadOnlyDictionary<string, ComponentEntry> ByName { get; } = Build();

        private static IReadOnlyDictionary<string, ComponentEntry> Build()
        {
            // X = LayoutGrid.ColumnBaseX(plc) + column * LayoutGrid.ColumnPitchX
            // Y = LayoutGrid.RowY(plc, row)
            // Resource = ControllerMap.ResourceForPlc(plc)
            // Verified against ResourceWireEmitter.CanonicalLayout (line 559) — every
            // (X, Y) below matches the deployed syslay byte-for-byte.
            var rows = new[]
            {
                // ── Bootstrap pair (no PLC / no column / fixed coords) ───────────
                Boot("FB1", 3000, 400),    // DPAC_FULLINIT — top-right
                Boot("FB2",  800, 1100),   // plcStart — below EAE's built-in START

                // ── M262 — Feed Station (M262_RES, frame X=1800, columns 0..3) ───
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
                // Stn1_Term on the Station row (its own column).
                M262("Stn1_Term",     column: 3, row: LayoutRow.Station,  owner: ""),

                // ── M580 — Assembly + Disassembly (RES0, frame X=11800, cols 0..6) ─
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

                // ── BX1 — Cover PnP (BX1_RES). The covers fold into the M580 Assembly/Disassembly flow
                //    over the cross-device detour; they have no BX1 Process engine of their own.
                BX1("TopCoverSenosr",    column: 0, row: LayoutRow.Process,  owner: ""),
                BX1("CoverPNP_Hr",       column: 0, row: LayoutRow.Actuator, owner: ""),
                BX1("CoverPNP_Vr",       column: 1, row: LayoutRow.Actuator, owner: ""),
                BX1("CoverPnp_Gripper",  column: 2, row: LayoutRow.Actuator, owner: ""),
                BX1("BX1_IO",            column: 3, row: LayoutRow.Actuator, owner: ""),  // EtherNet/IP cover-IO broker

                // MqttConn — MQTT broker FB, hosted on BX1 (Soft dPAC) because M262/
                // M580 have no MQTT runtime client (ReturnCode 50). Floats at the top
                // of BX1's zone (Y=200) above the Station1/Station2 frames. Routed to
                // BX1_RES by SysresFbMirror.BucketFor.
                BX1("MqttConn",          column: 0, row: LayoutRow.Floating, owner: ""),
                // The per-resource MQTT connections float above each PLC's HMI row. Registering them
                // (not just MqttConn) keeps them on the canonical grid so the per-PLC sysres
                // translateToOrigin moves them WITH the rest of the zone (else a fixed position
                // collides with the translated Station HMI).
                M262("MqttConn_M262",    column: 0, row: LayoutRow.Floating, owner: ""),
                M580("MqttConn_M580",    column: 0, row: LayoutRow.Floating, owner: ""),
                // Telemetry_CAT instances (cfg.UseTelemetryCat, default) sit on the HMI row NEXT TO the
                // Station/Area HMI faceplates (filling the otherwise-empty top band, not floating alone):
                // M262 col 2 (after Area_HMI/Station1_HMI), M580 col 1 (after Station2_HMI), BX1 col 0
                // (BX1 has no faceplate). Registered alongside the raw MqttConn* (revert path).
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

        /// <summary>Returns the entry for <paramref name="name"/>, or null if unknown.</summary>
        public static ComponentEntry? Get(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return ByName.TryGetValue(name!, out var e) ? e : null;
        }

        /// <summary>True if the registry knows about <paramref name="name"/>.</summary>
        public static bool Contains(string? name) =>
            !string.IsNullOrEmpty(name) && ByName.ContainsKey(name!);
    }
}
