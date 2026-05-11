using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Translation
{
    /// <summary>
    /// Reads the optional <c>Instance_Name_Overrides</c> sheet from the Excel
    /// mapping workbook and returns two override maps consumed by
    /// <see cref="InstanceNameResolver"/>.
    ///
    /// Sheet shape (case-sensitive header in row 1; rows after that are data):
    ///
    ///   | Type     | VueOne Name           | ComponentID                                    | IEC Instance Name | Notes              |
    ///   |----------|-----------------------|------------------------------------------------|-------------------|--------------------|
    ///   | Process  | Feed_Station_process  |                                                | Feed_Station      | strip _process     |
    ///   | Process  | Assembly_Station_proc | C-b455b9c6-47f7-4172-a1c1-4c43fc03b55e        | Assembly_Station  | by-id wins on tie  |
    ///   | Actuator |                       | C-db29c7cb-08df-46da-ab6a-28d2593bd5bb        | Pusher_Inlet      | rename for clarity |
    ///
    /// Either VueOne Name OR ComponentID may be filled (or both — ComponentID wins
    /// at lookup time per InstanceNameResolver's resolution order). IEC Instance Name
    /// is the FB instance name written into the .syslay / .sysres / wiring.
    ///
    /// If the sheet is absent or empty, both override maps come back empty and the
    /// resolver falls through to its default convention (strip "_process" on Process,
    /// pass through everything else).
    ///
    /// Returns empty maps on any read error; never throws — overrides are an
    /// optional convenience and the emit pipeline must keep working without them.
    /// </summary>
    public static class InstanceNameOverridesLoader
    {
        public sealed class Overrides
        {
            public Dictionary<string, string> ByComponentId { get; }
                = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> ByVueOneName { get; }
                = new(StringComparer.Ordinal);
        }

        const string SheetName = "Instance_Name_Overrides";

        public static Overrides Load(string xlsxPath)
        {
            var result = new Overrides();
            if (string.IsNullOrWhiteSpace(xlsxPath) || !System.IO.File.Exists(xlsxPath))
                return result;

            try
            {
                var sheets = XlsxRuleLoader.GetSheetNames(xlsxPath);
                if (!sheets.Any(s => string.Equals(s, SheetName, StringComparison.OrdinalIgnoreCase)))
                    return result;

                var rows = XlsxRuleLoader.ReadXlsxSheet(xlsxPath, SheetName);
                if (rows.Count == 0) return result;

                // Resolve column indices from the header row so the sheet is
                // tolerant to column reordering or trailing helper columns.
                var header = rows[0];
                int colName = IndexOf(header, "VueOne Name");
                int colId   = IndexOf(header, "ComponentID");
                int colIec  = IndexOf(header, "IEC Instance Name");

                if (colIec < 0) return result;   // sheet exists but mis-shaped

                for (int i = 1; i < rows.Count; i++)
                {
                    var row = rows[i];
                    var iec  = Cell(row, colIec).Trim();
                    if (iec.Length == 0) continue;
                    var name = colName >= 0 ? Cell(row, colName).Trim() : string.Empty;
                    var id   = colId   >= 0 ? Cell(row, colId).Trim()   : string.Empty;
                    if (id.Length   > 0) result.ByComponentId[id]   = iec;
                    if (name.Length > 0) result.ByVueOneName[name]  = iec;
                }
            }
            catch
            {
                // Best-effort. Caller falls through to the resolver's default convention.
            }
            return result;
        }

        private static int IndexOf(List<string> header, string columnName)
        {
            for (int i = 0; i < header.Count; i++)
                if (string.Equals(header[i].Trim(), columnName, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        private static string Cell(List<string> row, int col)
            => (col >= 0 && col < row.Count) ? (row[col] ?? string.Empty) : string.Empty;
    }
}
