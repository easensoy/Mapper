// MapperUI/MapperUI/Services/RuleEngine.cs
// Reads VueOne → IEC 61499 mapping rules from VueOne_IEC61499_Mapping.xlsx.
// All types (MappingRuleEntry, MappingType) live in MappingRuleEngine.cs — NOT here.
// Requires NuGet: ClosedXML  (Install-Package ClosedXML)

using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.IO;

namespace MapperUI.Services
{
    /// <summary>
    /// Loads mapping rules from the VueOne_IEC61499_Mapping.xlsx spreadsheet.
    /// Call <see cref="MappingRuleEngine.LoadFromXlsx"/> — this class wires the
    /// xlsx reader into the existing engine.
    /// </summary>
    public static class XlsxRuleLoader
    {
        // ── Column indices (1-based) matching the spreadsheet ─────────────────
        private const int ColVueOne = 1; // "VueOne Element"
        private const int ColIec = 2; // "IEC 61499 Element"
        private const int ColType = 3; // "Mapping Type"
        private const int ColRule = 4; // "Transformation Rule"
        private const int ColNotes = 5; // "Notes / Phase"

        // ── Section prefix lookup ─────────────────────────────────────────────
        private static readonly (string Prefix, string Title)[] SectionMap =
        {
            ("SystemID",           "System Level"),
            ("System/",            "System Level"),
            ("Component/",         "Component Level"),
            ("State/",             "State Level"),
            ("Transition/",        "Transition / Sequence Level"),
            ("Sequence_Condition", "Sequence & Condition Level"),
            ("ConditionGroup",     "Sequence & Condition Level"),
            ("Condition/",         "Sequence & Condition Level"),
            ("Interlock",          "Sequence & Condition Level"),
        };

        /// <summary>
        /// Opens <paramref name="xlsxPath"/> and yields one
        /// <see cref="MappingRuleEntry"/> per data row, inserting section-header
        /// rows whenever the element group changes.
        /// </summary>
        public static IEnumerable<MappingRuleEntry> Load(string xlsxPath)
        {
            if (string.IsNullOrWhiteSpace(xlsxPath) || !File.Exists(xlsxPath))
                throw new FileNotFoundException(
                    $"Mapping rules spreadsheet not found:\n{xlsxPath}\n\n" +
                    "Set MappingRulesPath in mapper_config.json to point to " +
                    "VueOne_IEC61499_Mapping.xlsx.");

            using var wb = new XLWorkbook(xlsxPath);
            var ws = wb.Worksheet(1); // first sheet

            string currentSection = string.Empty;

            foreach (var row in ws.RowsUsed())
            {
                if (row.RowNumber() == 1) continue; // skip header row

                var vueRaw = GetCell(row, ColVueOne);
                var iecRaw = GetCell(row, ColIec);
                var typeRaw = GetCell(row, ColType);
                var rule = GetCell(row, ColRule);
                var notes = GetCell(row, ColNotes);

                // Stop at the legend block at the bottom of the sheet
                if (IsLegendRow(vueRaw, typeRaw)) break;

                // Skip fully empty rows
                if (string.IsNullOrWhiteSpace(vueRaw) &&
                    string.IsNullOrWhiteSpace(iecRaw) &&
                    string.IsNullOrWhiteSpace(typeRaw)) continue;

                // Must have a parseable mapping type
                if (!TryParseMappingType(typeRaw, out var mappingType)) continue;

                // Determine section from VueOne element prefix
                var section = ResolveSection(vueRaw, mappingType);

                // Emit section-header row when the group changes
                if (!string.IsNullOrEmpty(section) && section != currentSection)
                {
                    currentSection = section;
                    yield return new MappingRuleEntry
                    {
                        IsSection = true,
                        SectionTitle = section,
                        Type = MappingType.SECTION
                    };
                }

                // Phase 2/3/4 notes → not yet implemented in Phase 1
                bool implemented = !IsFuturePhase(notes);

                yield return new MappingRuleEntry
                {
                    IsSection = false,
                    VueOneElement = vueRaw,
                    IEC61499Element = iecRaw,
                    Type = mappingType,
                    TransformationRule = rule,
                    IsImplemented = implemented
                };
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string GetCell(IXLRow row, int col)
            => row.Cell(col).GetString()?.Trim() ?? string.Empty;

        private static bool TryParseMappingType(string raw, out MappingType result)
        {
            result = MappingType.TRANSLATED;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return Enum.TryParse(raw.Trim().ToUpperInvariant(), out result);
        }

        private static string ResolveSection(string vueElement, MappingType type)
        {
            if (string.IsNullOrWhiteSpace(vueElement))
                return type == MappingType.HARDCODED ? "EAE Template (Hardcoded)" : string.Empty;

            foreach (var (prefix, title) in SectionMap)
                if (vueElement.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return title;

            return string.Empty;
        }

        private static bool IsLegendRow(string vueElement, string typeRaw)
        {
            // Legend rows have the type keyword in the VueOne column and no type column value
            if (string.IsNullOrWhiteSpace(typeRaw) && !string.IsNullOrWhiteSpace(vueElement))
            {
                var upper = vueElement.ToUpperInvariant();
                return upper is "HARDCODED" or "TRANSLATED" or "ASSUMED"
                             or "DISCARDED" or "ENCODED";
            }
            return false;
        }

        private static bool IsFuturePhase(string notes)
        {
            if (string.IsNullOrWhiteSpace(notes)) return false;
            var n = notes.ToUpperInvariant();
            return n.Contains("PHASE 2") || n.Contains("PHASE 3") || n.Contains("PHASE 4");
        }
    }
}