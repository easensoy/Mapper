using CodeGen.Configuration;
using CodeGen.Translation;

namespace CodeGen.Mapping
{
    // Geometry for the SMC rig syslay canvas: (Plc, Column, Row) -> (X, Y) in shared-canvas coordinates,
    // projected from Config/layout.yml. Per-PLC sysres files apply a translateToOrigin shift elsewhere.
    public static class LayoutGrid
    {
        public static int ColumnPitchX => LayoutCatalog.Current.Geometry.ColumnPitchX;
        public static int FrameOriginY => LayoutCatalog.Current.Geometry.FrameOriginY;
        public static int FrameHeight => LayoutCatalog.Current.Geometry.FrameHeight;

        // Left edge of each controller's band, the X of its column 0, and its fitted width. The bands are
        // sized not to overlap; combined with MoveStyle="None" on every Frame, EAE renders them at exactly
        // these bounds instead of auto-growing one band around a neighbour's FBs.
        public static int FrameOriginX(PlcAssignment plc) => LayoutCatalog.Current.Band(plc).FrameOriginX;
        public static int ColumnBaseX(PlcAssignment plc) => LayoutCatalog.Current.Band(plc).ColumnBaseX;
        public static int FrameWidth(PlcAssignment plc) => LayoutCatalog.Current.Band(plc).FrameWidth;

        public static int RowY(PlcAssignment plc, LayoutRow row) => LayoutCatalog.Current.RowY(row.ToString());
    }
}
