using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

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
        private const int ColVueOne = 0;
        private const int ColIec = 1;
        private const int ColType = 2;
        private const int ColRule = 3;
        private const int ColNotes = 4;

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
                    $"Mapping rules spreadsheet not found:\n{xlsxPath}\n\nSet MappingRulesPath in mapper_config.json.");

            var rawRows = ReadXlsx(xlsxPath);
            string currentSection = string.Empty;
            bool firstRow = true;

            foreach (var cells in rawRows)
            {
                if (firstRow) { firstRow = false; continue; }

                var vue = Get(cells, ColVueOne);
                var iec = Get(cells, ColIec);
                var type = Get(cells, ColType);
                var rule = Get(cells, ColRule);
                var notes = Get(cells, ColNotes);

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
                        IsImplemented: false);
                }

                yield return new MappingRuleEntry(
                    IsSection: false,
                    SectionTitle: string.Empty,
                    VueOneElement: vue,
                    IEC61499Element: iec,
                    Type: mappingType,
                    TransformationRule: rule,
                    IsImplemented: !IsFuturePhase(notes));
            }
        }

        private static List<List<string>> ReadXlsx(string path)
        {
            var rows = new List<List<string>>();

            using var zip = ZipFile.OpenRead(path);

            var sharedStrings = new List<string>();
            var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
            if (ssEntry != null)
            {
                using var ssStream = ssEntry.Open();
                var ssDoc = XDocument.Load(ssStream);
                XNamespace ssNs = ssDoc.Root!.GetDefaultNamespace();
                foreach (var si in ssDoc.Root.Elements(ssNs + "si"))
                    sharedStrings.Add(string.Concat(si.Descendants(ssNs + "t").Select(t => t.Value)));
            }

            var sheetEntry = zip.GetEntry("xl/worksheets/sheet1.xml");
            if (sheetEntry == null) return rows;

            using var sheetStream = sheetEntry.Open();
            var sheetDoc = XDocument.Load(sheetStream);
            XNamespace ns = sheetDoc.Root!.GetDefaultNamespace();

            foreach (var rowEl in sheetDoc.Descendants(ns + "row"))
            {
                var cells = new List<string>();
                int lastColIdx = 0;

                foreach (var cell in rowEl.Elements(ns + "c"))
                {
                    int colIdx = ParseColIndex((string?)cell.Attribute("r") ?? string.Empty);
                    while (lastColIdx < colIdx - 1) { cells.Add(string.Empty); lastColIdx++; }

                    string cellType = (string?)cell.Attribute("t") ?? string.Empty;
                    string cellValue = cell.Element(ns + "v")?.Value ?? string.Empty;

                    string val;
                    if (cellType == "s" && int.TryParse(cellValue, out int ssIdx) && ssIdx < sharedStrings.Count)
                        val = sharedStrings[ssIdx];
                    else if (cellType == "inlineStr")
                        val = string.Concat(cell.Descendants(ns + "t").Select(t => t.Value));
                    else
                        val = cellValue;

                    cells.Add(val.Trim());
                    lastColIdx = colIdx;
                }

                rows.Add(cells);
            }

            return rows;
        }

        private static int ParseColIndex(string cellRef)
        {
            int col = 0;
            foreach (char c in cellRef)
            {
                if (!char.IsLetter(c)) break;
                col = col * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
            }
            return col;
        }

        private static string Get(List<string> row, int col)
            => col < row.Count ? row[col] : string.Empty;

        private static bool TryParseType(string raw, out MappingType result)
        {
            result = MappingType.TRANSLATED;
            return !string.IsNullOrWhiteSpace(raw) && Enum.TryParse(raw.ToUpperInvariant(), out result);
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
        {
            if (!string.IsNullOrWhiteSpace(type)) return false;
            string upper = vue.ToUpperInvariant();
            return upper == "HARDCODED" || upper == "TRANSLATED" ||
                   upper == "ASSUMED" || upper == "DISCARDED" || upper == "ENCODED";
        }

        private static bool IsFuturePhase(string notes)
        {
            string n = notes.ToUpperInvariant();
            return n.Contains("PHASE 2") || n.Contains("PHASE 3") || n.Contains("PHASE 4");
        }
    }
}