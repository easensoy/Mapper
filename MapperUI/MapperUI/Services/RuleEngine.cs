using System;
using System.Collections.Generic;
using System.IO;
using ClosedXML.Excel;
using CodeGen.Configuration;

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
        const string SheetName = "VueOne to IEC61499 Mapping";
        static List<MappingRuleEntry>? _cached;
        static string? _cachedPath;

        public static IEnumerable<MappingRuleEntry> GetAllRules()
        {
            var path = MapperConfig.Load().RuleEnginePath;

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                MapperLogger.Warn($"[RuleEngine] xlsx not found: {path}");
                return Fallback();
            }

            if (path == _cachedPath && _cached != null) return _cached;

            _cached = LoadFromXlsx(path);
            _cachedPath = path;
            MapperLogger.Info($"[RuleEngine] Loaded {_cached.Count} rules from {Path.GetFileName(path)}");
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
                var col3 = Cell(ws, r, 3);

                if (string.IsNullOrWhiteSpace(col1) && string.IsNullOrWhiteSpace(col3)) continue;
                if (IsLegend(col1)) break;

                var section = DetectSection(col1);
                if (section != null && section != currentSection)
                {
                    currentSection = section;
                    rules.Add(new MappingRuleEntry { IsSection = true, SectionTitle = currentSection });
                }

                var type = ParseType(col3);

                rules.Add(new MappingRuleEntry
                {
                    VueOneElement = col1,
                    IEC61499Element = Cell(ws, r, 2),
                    Type = type,
                    TransformationRule = Cell(ws, r, 4),
                    Notes = Cell(ws, r, 5),
                    Example = Cell(ws, r, 6),
                    IsImplemented = type != MappingType.ENCODED
                });
            }
            return rules;
        }

        static string? DetectSection(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            if (v.StartsWith("System")) return "System Level";
            if (v.StartsWith("Component/")) return "Component Level";
            if (v.StartsWith("State/")) return "State Level";
            if (v.StartsWith("Transition/") || v.StartsWith("Sequence") || v.StartsWith("Condition") || v.StartsWith("Interlock"))
                return "Transition / Sequence Level";
            if (v == "N/A") return "EAE Specifics (Hardcoded)";
            return null;
        }

        static bool IsLegend(string v) => v is "HARDCODED" or "TRANSLATED" or "ASSUMED" or "DISCARDED" or "ENCODED";

        static MappingType ParseType(string v) => v?.Trim().ToUpperInvariant() switch
        {
            "TRANSLATED" => MappingType.TRANSLATED,
            "DISCARDED" => MappingType.DISCARDED,
            "ASSUMED" => MappingType.ASSUMED,
            "ENCODED" => MappingType.ENCODED,
            "HARDCODED" => MappingType.HARDCODED,
            _ => MappingType.DISCARDED
        };

        static string Cell(IXLWorksheet ws, int row, int col) => ws.Cell(row, col).GetString()?.Trim() ?? "";

        static List<MappingRuleEntry> Fallback() => new()
        {
            new() { IsSection = true, SectionTitle = "VueOne_IEC61499_Mapping.xlsx not found" },
            new()
            {
                VueOneElement = "Set RuleEnginePath in mapper_config.json",
                IEC61499Element = "Path to VueOne_IEC61499_Mapping.xlsx",
                Type = MappingType.ASSUMED,
                TransformationRule = "Rules load automatically from xlsx",
                IsImplemented = false
            }
        };
    }
}