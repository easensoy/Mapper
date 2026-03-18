using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.IO;

namespace MapperUI.Services
{
    public enum MappingType
    {
        TRANSLATED,
        DISCARDED,
        ASSUMED,
        ENCODED,
        HARDCODED,
        SECTION
    }

    public record MappingRuleEntry(
        bool IsSection,
        string SectionTitle,
        string VueOneElement,
        string IEC61499Element,
        MappingType Type,
        string TransformationRule,
        bool IsImplemented
    );

    public static class MappingRuleEngine
    {
        public static IEnumerable<MappingRuleEntry> GetAllRules(string xlsxPath)
            => XlsxRuleLoader.Load(xlsxPath);

        public static IEnumerable<MappingRuleEntry> GetRelevantRules(
            string xlsxPath, bool hasActuator, bool hasSensor, bool hasProcess)
            => XlsxRuleLoader.Load(xlsxPath);
    }

    public static class XlsxRuleLoader
    {
        private const int ColVueOne = 1;
        private const int ColIec = 2;
        private const int ColType = 3;
        private const int ColRule = 4;
        private const int ColNotes = 5;

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

        public static IEnumerable<MappingRuleEntry> Load(string xlsxPath)
        {
            if (string.IsNullOrWhiteSpace(xlsxPath) || !File.Exists(xlsxPath))
                throw new FileNotFoundException(
                    $"Mapping rules spreadsheet not found:\n{xlsxPath}\n\n" +
                    "Set MappingRulesPath in mapper_config.json.");

            using var wb = new XLWorkbook(xlsxPath);
            var ws = wb.Worksheet(1);
            string currentSection = string.Empty;

            foreach (var row in ws.RowsUsed())
            {
                if (row.RowNumber() == 1) continue;

                var vue = Cell(row, ColVueOne);
                var iec = Cell(row, ColIec);
                var type = Cell(row, ColType);
                var rule = Cell(row, ColRule);
                var notes = Cell(row, ColNotes);

                if (IsLegendRow(vue, type)) break;

                if (string.IsNullOrWhiteSpace(vue) &&
                    string.IsNullOrWhiteSpace(iec) &&
                    string.IsNullOrWhiteSpace(type)) continue;

                if (!TryParseType(type, out var mappingType)) continue;

                var section = ResolveSection(vue, mappingType);
                if (!string.IsNullOrEmpty(section) && section != currentSection)
                {
                    currentSection = section;
                    yield return new MappingRuleEntry(
                        IsSection: true,
                        SectionTitle: section,
                        VueOneElement: string.Empty,
                        IEC61499Element: string.Empty,
                        Type: MappingType.SECTION,
                        TransformationRule: string.Empty,
                        IsImplemented: false
                    );
                }

                yield return new MappingRuleEntry(
                    IsSection: false,
                    SectionTitle: string.Empty,
                    VueOneElement: vue,
                    IEC61499Element: iec,
                    Type: mappingType,
                    TransformationRule: rule,
                    IsImplemented: !IsFuturePhase(notes)
                );
            }
        }

        private static string Cell(IXLRow row, int col)
            => row.Cell(col).GetString()?.Trim() ?? string.Empty;

        private static bool TryParseType(string raw, out MappingType result)
        {
            result = MappingType.TRANSLATED;
            return !string.IsNullOrWhiteSpace(raw) &&
                   Enum.TryParse(raw.ToUpperInvariant(), out result);
        }

        private static string ResolveSection(string vue, MappingType type)
        {
            if (string.IsNullOrWhiteSpace(vue))
                return type == MappingType.HARDCODED ? "EAE Template (Hardcoded)" : string.Empty;

            foreach (var (prefix, title) in SectionMap)
                if (vue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return title;

            return string.Empty;
        }

        private static bool IsLegendRow(string vue, string type)
            => string.IsNullOrWhiteSpace(type) &&
               vue.ToUpperInvariant() is "HARDCODED" or "TRANSLATED"
                   or "ASSUMED" or "DISCARDED" or "ENCODED";

        private static bool IsFuturePhase(string notes)
        {
            var n = notes.ToUpperInvariant();
            return n.Contains("PHASE 2") || n.Contains("PHASE 3") || n.Contains("PHASE 4");
        }
    }
}