using System.Linq;
using CodeGen.Translation;

namespace CodeGen.Mapping
{
    // Geometry for the SMC rig syslay canvas: (Plc, Column, Row) -> (X, Y) projection in shared-canvas
    // coordinates. Per-PLC sysres files apply a translateToOrigin shift elsewhere.
    public static class LayoutGrid
    {
        // Actuator CATs render ~1400 wide, so a 1700 pitch leaves a ~300 gap with no overlap.
        public const int ColumnPitchX = 1700;

        public const int FrameOriginY = 1700;

        public const int FrameHeight = 5300;

        // Padding past the rightmost column so EAE's MoveStyle="AnyContained" does not auto-grow
        // the frame westward to enclose an overflowing FB body.
        public const int FrameRightPadding = 600;

        // Left edge of each PLC zone; origins follow the prior zone's fitted right edge (~250 gap).
        public static int FrameOriginX(PlcAssignment plc) => plc switch
        {
            PlcAssignment.M262 => 1800,
            PlcAssignment.M580 => 10800,
            PlcAssignment.BX1  => 23200,
            _ => 0,
        };

        // X of column 0 for each PLC (FrameOriginX + 200 inside the frame); each zone starts right
        // after the prior zone's fitted right edge.
        public static int ColumnBaseX(PlcAssignment plc) => plc switch
        {
            PlcAssignment.M262 => 2000,
            PlcAssignment.M580 => 11000,
            PlcAssignment.BX1  => 23400,
            _ => 0,
        };

        // Y per row, spaced to clear the EAE-rendered FB body in the row above (Process1_Generic
        // ≈800, Five_State ≈1400) while keeping the canvas tight.
        public static int RowY(PlcAssignment plc, LayoutRow row) => row switch
        {
            LayoutRow.Floating => 200,
            LayoutRow.Hmi      => 1200,
            LayoutRow.Station  => 2100,
            LayoutRow.Process  => 2900,
            LayoutRow.Sensor   => 2900,
            LayoutRow.Actuator => 4100,
            _ => 0,
        };

        public static int MaxColumn(PlcAssignment plc) =>
            ComponentRegistry.ByName.Values
                .Where(e => e.Plc == plc && e.Column >= 0)
                .Select(e => e.Column)
                .DefaultIfEmpty(-1)
                .Max();

        // Frame width per PLC zone — three non-overlapping bands with small gaps. Combined with
        // MoveStyle="None" on every Frame, EAE renders them at exactly these bounds (no auto-grow).
        public static int FrameWidth(PlcAssignment plc) => plc switch
        {
            PlcAssignment.M262 => 8500,    // 1800..10300 (gap 1500 to M580 origin)
            PlcAssignment.M580 => 16000,   // 11800..27800 (gap 400 to BX1 origin)
            PlcAssignment.BX1  => 6600,    // 28200..34800 (last zone)
            _ => 0,
        };

        public static int FrameRightEdge(PlcAssignment plc) =>
            FrameOriginX(plc) + FrameWidth(plc);

        public static (int X, int Y)? PositionOf(string? name)
        {
            var e = ComponentRegistry.Get(name);
            return e is null ? null : (e.X, e.Y);
        }

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
