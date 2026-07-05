using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Translation
{
    // Reads the optional Instance_Name_Overrides sheet from the Excel mapping workbook and returns
    // override maps consumed by InstanceNameResolver. Returns empty maps if the sheet is absent or
    // on any read error; never throws.
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

                // Resolve column indices from the header row (tolerant to column reordering).
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
