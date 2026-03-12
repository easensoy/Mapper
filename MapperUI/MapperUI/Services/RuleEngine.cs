using System;
using System.Collections.Generic;
using System.IO;
using ClosedXML.Excel;

namespace MapperUI.Services
{
    public enum MappingType { TRANSLATED, DISCARDED, ASSUMED, ENCODED, HARDCODED, SECTION }

    public class MappingRuleEntry
    {
        public bool IsSection { get; init; }
        public string SectionTitle { get; init; } = string.Empty;
        public string VueOneElement { get; init; } = string.Empty;
        public string IEC61499Element { get; init; } = string.Empty;
        public MappingType Type { get; init; }
        public string TransformationRule { get; init; } = string.Empty;
        public string Notes { get; init; } = string.Empty;
        public string Example { get; init; } = string.Empty;
        public bool IsImplemented { get; init; }
    }

    public static class RuleEngine
    {
        const string DefaultFileName = "VueOne_IEC61499_Mapping.xlsx";
        const string SheetName = "VueOne to IEC61499 Mapping";

        static List<MappingRuleEntry>? _cached;
        static string? _cachedPath;

        public static IEnumerable<MappingRuleEntry> GetAllRules(string? xlsxPath = null)
        {
            var path = xlsxPath ?? FindXlsx();
            if (path != null && path == _cachedPath && _cached != null)
                return _cached;

            if (path == null || !File.Exists(path))
                return Fallback();

            _cached = LoadFromXlsx(path);
            _cachedPath = path;
            return _cached;
        }

        static List<MappingRuleEntry> LoadFromXlsx(string path)
        {
            var rules = new List<MappingRuleEntry>();
            string? currentSection = null;

            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheet(SheetName);
            int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;

            for (int r = 2; r <= lastRow; r++)
            {
                var col1 = Cell(ws, r, 1);
                var col2 = Cell(ws, r, 2);
                var col3 = Cell(ws, r, 3);
                var col4 = Cell(ws, r, 4);
                var col5 = Cell(ws, r, 5);
                var col6 = Cell(ws, r, 6);

                if (string.IsNullOrWhiteSpace(col1) && string.IsNullOrWhiteSpace(col3))
                    continue;

                if (IsLegendRow(col1))
                    break;

                var section = DetectSection(col1);
                if (section != null && section != currentSection)
                {
                    currentSection = section;
                    rules.Add(new MappingRuleEntry { IsSection = true, SectionTitle = currentSection });
                }

                var type = ParseType(col3);
                bool implemented = type == MappingType.TRANSLATED || type == MappingType.DISCARDED
                    || type == MappingType.ASSUMED || type == MappingType.HARDCODED;

                rules.Add(new MappingRuleEntry
                {
                    VueOneElement = col1,
                    IEC61499Element = col2,
                    Type = type,
                    TransformationRule = col4,
                    Notes = col5,
                    Example = col6,
                    IsImplemented = implemented
                });
            }

            return rules;
        }

        static string? DetectSection(string vueElement)
        {
            if (string.IsNullOrWhiteSpace(vueElement)) return null;
            if (vueElement.StartsWith("System")) return "System Level";
            if (vueElement.StartsWith("Component/")) return "Component Level";
            if (vueElement.StartsWith("State/")) return "State Level";
            if (vueElement.StartsWith("Transition/") || vueElement.StartsWith("Sequence_Condition")
                || vueElement.StartsWith("Condition") || vueElement.StartsWith("Interlock"))
                return "Transition / Sequence Level";
            if (vueElement == "N/A" || string.IsNullOrWhiteSpace(vueElement))
                return "EAE Specifics (Hardcoded)";
            return null;
        }

        static bool IsLegendRow(string val) =>
            val == "HARDCODED" || val == "TRANSLATED" || val == "ASSUMED"
            || val == "DISCARDED" || val == "ENCODED";

        static MappingType ParseType(string val) => val?.Trim().ToUpperInvariant() switch
        {
            "TRANSLATED" => MappingType.TRANSLATED,
            "DISCARDED" => MappingType.DISCARDED,
            "ASSUMED" => MappingType.ASSUMED,
            "ENCODED" => MappingType.ENCODED,
            "HARDCODED" => MappingType.HARDCODED,
            _ => MappingType.DISCARDED
        };

        static string Cell(IXLWorksheet ws, int row, int col) =>
            ws.Cell(row, col).GetString()?.Trim() ?? string.Empty;

        static string? FindXlsx()
        {
            var candidates = new[]
            {
                Path.Combine(Environment.CurrentDirectory, DefaultFileName),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DefaultFileName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), DefaultFileName),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        static List<MappingRuleEntry> Fallback() => new()
        {
            new() { IsSection = true, SectionTitle = "Mapping rules file not found" },
            new() {
                VueOneElement = DefaultFileName,
                IEC61499Element = "Place alongside mapper_config.json",
                Type = MappingType.ASSUMED,
                TransformationRule = "Rules will load automatically from xlsx",
                IsImplemented = false
            }
        };
    }
}