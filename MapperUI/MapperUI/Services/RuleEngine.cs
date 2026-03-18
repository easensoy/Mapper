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
                    $"Mapping rules spreadsheet not found:\n{xlsxPath}\n\n" +
                    "Set MappingRulesPath in mapper_config.json.");

            var rawRows = ReadXlsx(xlsxPath);
            string currentSection = string.Empty;
            bool firstRow = true;

            foreach (var row in rawRows)
            {
                if (firstRow) { firstRow = false; continue; }

                var vue = Get(row, ColVueOne);
                var iec = Get(row, ColIec);
                var type = Get(row, ColType);
                var rule = Get(row, ColRule);
                var notes = Get(row, ColNotes);

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

        private static List<List<string>> ReadXlsx(string path)
        {
            var rows = new List<List<string>>();

            using var zip = ZipFile.OpenRead(path);

            var sharedStrings = new List<string>();
            var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
            if (ssEntry != null)
            {
                using var ss = ssEntry.Open();
                var ssDoc = XDocument.Load(ss);
                XNamespace ns = ssDoc.Root!.GetDefaultNamespace();
                foreach (var si in ssDoc.Root.Elements(ns + "si"))
                    sharedStrings.Add(string.Join("",
                        si.Descendants(ns + "t").Select(t => t.Value)));
            }

            var sheetEntry = zip.GetEntry("xl/worksheets/sheet1.xml");
            if (sheetEntry == null) return rows;

            using var sheetStream = sheetEntry.Open();
            var doc = XDocument.Load(sheetStream);
            XNamespace sNs = doc.Root!.GetDefaultNamespace();

            foreach (var rowEl in doc.Descendants(sNs + "row"))
            {
                var rowData = new List<string>();
                int lastCol = 0;

                foreach (var cell in rowEl.Elements(sNs + "c"))
                {
                    var colIdx = ColIndex((string?)cell.Attribute("r") ?? "");

                    while (lastCol < colIdx - 1) { rowData.Add(""); lastCol++; }

                    var cellType = (string?)cell.Attribute("t") ?? "";
                    var valueText = cell.Element(sNs + "v")?.Value ?? "";

                    string val;
                    if (cellType == "s" && int.TryParse(valueText, out int si) && si < sharedStrings.Count)
                        val = sharedStrings[si];
                    else if (cellType == "inlineStr")
                        val = string.Join("", cell.Descendants(sNs + "t").Select(t => t.Value));
                    else
                        val = valueText;

                    rowData.Add(val.Trim());
                    lastCol = colIdx;
                }

                rows.Add(rowData);
            }

            return rows;
        }

        private static int ColIndex(string cellRef)
        {
            int col = 0;
            foreach (var c in cellRef)
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