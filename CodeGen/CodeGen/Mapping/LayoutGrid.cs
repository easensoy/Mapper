using System.Linq;
using CodeGen.Translation;

namespace CodeGen.Mapping
{
    /// <summary>
    /// Geometry for the SMC rig syslay canvas — constants and the (Plc, Column, Row)
    /// → (X, Y) projection used by <see cref="ComponentRegistry"/> and downstream
    /// callers (FB placement, frame bounds, position validation).
    ///
    /// All values are SYSLAY (shared-canvas) coordinates. Per-PLC sysres files
    /// apply a translateToOrigin shift in <c>ResourceWireEmitter.ApplyCanonicalLayout</c>
    /// so M580/BX1 sysres canvases land at their own local origin — that translation
    /// logic stays in place; this class only describes the global syslay grid.
    /// </summary>
    public static class LayoutGrid
    {
        /// <summary>Horizontal pitch between columns. The data-driven actuator CATs render ~1400 wide
        /// (long port labels like enableToWorkFaultTimeout), so 1700 leaves a ~300 gap with no overlap.
        /// Horizontal compaction comes from closing the inter-zone gaps (origins below), not a tiny pitch.</summary>
        public const int ColumnPitchX = 1700;

        /// <summary>Top edge of every frame on the syslay canvas.</summary>
        public const int FrameOriginY = 1700;

        /// <summary>
        /// Default frame height — covers HMI through actuator row plus default FB
        /// body height (Five_State ~3050).
        /// </summary>
        public const int FrameHeight = 5300;

        /// <summary>
        /// Right-side padding past the rightmost column's X. Wide enough to cover
        /// the widest FB body so EAE's <c>MoveStyle="AnyContained"</c> does not need
        /// to auto-grow the frame westward to find FBs that overflow.
        /// </summary>
        public const int FrameRightPadding = 600;

        /// <summary>Left edge of the coloured frame for each PLC zone on the syslay. Origins are pulled
        /// close so the three coloured zones sit next to each other (≈250 white gap) — the M580/BX1
        /// origins follow the prior zone's fitted right edge (pitch 1700 + FbEstWidth ~1400 + padding).</summary>
        public static int FrameOriginX(PlcAssignment plc) => plc switch
        {
            PlcAssignment.M262 => 1800,
            PlcAssignment.M580 => 10800,
            PlcAssignment.BX1  => 23200,
            _ => 0,
        };

        /// <summary>X of column 0 for each PLC (sits FrameOriginX + 200 inside the frame).</summary>
        public static int ColumnBaseX(PlcAssignment plc) => plc switch
        {
            PlcAssignment.M262 => 2000,
            PlcAssignment.M580 => 11000,   // M262 zone fits within ~1800..10400; M580 starts right after
            PlcAssignment.BX1  => 23400,   // M580 zone fits ..~23000; BX1 starts right after
            _ => 0,
        };

        /// <summary>
        /// Y for each row, UNIFIED across PLCs so the three zones read as one hierarchy. Rows are
        /// spaced to clear the EAE-rendered FB body in the row above (the rendered heights are
        /// smaller than the old FbEstHeight model assumed — Process1_Generic ≈800, Five_State ≈1400),
        /// so the gaps are kept TIGHT to avoid a sparse canvas:
        ///   Floating 200 (MqttConn) · HMI 1300 · Station 2000 · Process/Sensor 2900 · Actuator 4000.
        /// Process at 2900 ends ≈3700 (rendered Process1_Generic), clearing the actuator row at 4000.
        /// </summary>
        public static int RowY(PlcAssignment plc, LayoutRow row) => row switch
        {
            LayoutRow.Floating => 200,
            LayoutRow.Hmi      => 1200,    // HMI faceplates + Telemetry_CAT (~800 tall) share this row
            LayoutRow.Station  => 2100,    // far enough below HMI that the taller Telemetry_CAT clears it
            LayoutRow.Process  => 2900,
            LayoutRow.Sensor   => 2900,
            LayoutRow.Actuator => 4100,   // each row clears the rendered FB above
            _ => 0,
        };

        /// <summary>
        /// Largest column index used by any registered component on this PLC, or
        /// -1 if none. Drives <see cref="FrameWidth"/>.
        /// </summary>
        public static int MaxColumn(PlcAssignment plc) =>
            ComponentRegistry.ByName.Values
                .Where(e => e.Plc == plc && e.Column >= 0)
                .Select(e => e.Column)
                .DefaultIfEmpty(-1)
                .Max();

        /// <summary>
        /// Frame width for a PLC zone. Hardcoded per the canonical SMC mapping
        /// table — three clearly-separated coloured bands with small gaps so the
        /// yellow/purple/green zones never overlap visually (zone end → next
        /// origin: M262 10300 → 11800 (gap 1500), M580 27800 → 28200 (gap 400)).
        /// Combined with <c>MoveStyle="None"</c> on every Frame, EAE renders the
        /// three bands at exactly these emitted bounds — no auto-grow, no green
        /// swallowing the yellow.
        /// </summary>
        public static int FrameWidth(PlcAssignment plc) => plc switch
        {
            PlcAssignment.M262 => 8500,    // 1800..10300 (gap 1500 to M580 origin)
            PlcAssignment.M580 => 16000,   // 11800..27800 (gap 400 to BX1 origin)
            PlcAssignment.BX1  => 6600,    // 28200..34800 (last zone)
            _ => 0,
        };

        /// <summary>Right edge X of the PLC's frame.</summary>
        public static int FrameRightEdge(PlcAssignment plc) =>
            FrameOriginX(plc) + FrameWidth(plc);

        /// <summary>
        /// Syslay X/Y for the registered component <paramref name="name"/>, or
        /// null if the name is not in the registry.
        /// </summary>
        public static (int X, int Y)? PositionOf(string? name)
        {
            var e = ComponentRegistry.Get(name);
            return e is null ? null : (e.X, e.Y);
        }

        /// <summary>
        /// True iff (x, y) sits inside the frame bounds for <paramref name="plc"/>:
        /// X ∈ [FrameOriginX, FrameOriginX + FrameWidth) and
        /// Y ∈ [FrameOriginY, FrameOriginY + FrameHeight).
        /// </summary>
        public static bool IsInsideFrame(PlcAssignment plc, int x, int y)
        {
            int x0 = FrameOriginX(plc);
            int w  = FrameWidth(plc);
            int y0 = FrameOriginY;
            int h  = FrameHeight;
            return x >= x0 && x < x0 + w && y >= y0 && y < y0 + h;
        }
    }
}
