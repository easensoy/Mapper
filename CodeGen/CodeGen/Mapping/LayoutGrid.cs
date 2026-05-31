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
        /// <summary>Horizontal pitch between columns (≥ widest FB body + margin).</summary>
        public const int ColumnPitchX = 2500;

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

        /// <summary>Left edge of the coloured frame for each PLC zone on the syslay.</summary>
        public static int FrameOriginX(PlcAssignment plc) => plc switch
        {
            PlcAssignment.M262 => 1800,
            PlcAssignment.M580 => 11800,
            PlcAssignment.BX1  => 28200,
            _ => 0,
        };

        /// <summary>X of column 0 for each PLC (sits FrameOriginX + 200 inside the frame).</summary>
        public static int ColumnBaseX(PlcAssignment plc) => plc switch
        {
            PlcAssignment.M262 => 2000,
            PlcAssignment.M580 => 12200,
            PlcAssignment.BX1  => 29000,
            _ => 0,
        };

        /// <summary>
        /// Y for each (PLC, Row) pair. M262 and M580 share the upper rows
        /// (HMI=2000, Station=2900, Process/Sensor=4000); their actuator rows
        /// differ (M262=5400, M580=6500) because M580 has the wider process row
        /// above. BX1 has no Station/Process rows — its sensor row sits at 4000
        /// and its actuator row at 6500 (matches M580's grid; the BX1 sysres
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
        /// Frame width for a PLC zone. Each frame ABUTS the next zone's origin
        /// (no overlap between M262 ↔ M580 ↔ BX1), so the three coloured bands
        /// stay in their own lanes and EAE's auto-grow (when ever re-enabled)
        /// has nothing to fight. The trailing zone (BX1) uses
        /// (MaxColumn + 1) × ColumnPitchX + FrameRightPadding since there is no
        /// next-zone origin to abut. Returns 0 when the PLC has no components.
        /// </summary>
        public static int FrameWidth(PlcAssignment plc)
        {
            int nextOrigin = NextZoneOriginX(plc);
            if (nextOrigin > 0) return nextOrigin - FrameOriginX(plc);

            int max = MaxColumn(plc);
            if (max < 0) return 0;
            return (max + 1) * ColumnPitchX + FrameRightPadding;
        }

        /// <summary>
        /// X coordinate of the next PLC zone to the right of <paramref name="plc"/>,
        /// or -1 if <paramref name="plc"/> is the rightmost zone. M262 → M580 origin;
        /// M580 → BX1 origin; BX1 → no next.
        /// </summary>
        private static int NextZoneOriginX(PlcAssignment plc) => plc switch
        {
            PlcAssignment.M262 => FrameOriginX(PlcAssignment.M580),
            PlcAssignment.M580 => FrameOriginX(PlcAssignment.BX1),
            _ => -1,
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
