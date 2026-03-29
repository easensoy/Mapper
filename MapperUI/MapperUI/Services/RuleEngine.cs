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
            => XlsxRuleLoader.LoadAll(xlsxPath);

        public static IEnumerable<MappingRuleEntry> GetRulesForSheet(string xlsxPath, string sheetName)
            => XlsxRuleLoader.LoadSheet(xlsxPath, sheetName);

        public static IEnumerable<MappingRuleEntry> GetRelevantRules(
            string xlsxPath, bool hasActuator5, bool hasActuator7, bool hasSensor)
        {
            var sheets = new List<string>();
            // if (hasActuator5) sheets.Add("Five_State_Actuator_CAT");  // Five-state actuator commented out
            if (hasActuator7) sheets.Add("Seven_State_Actuator_CAT");
            // if (hasSensor) sheets.Add("Sensor_Bool_CAT");  // Sensor commented out

            // Fallback: if nothing matched, load all
            if (sheets.Count == 0)
                return XlsxRuleLoader.LoadAll(xlsxPath);

            return XlsxRuleLoader.LoadSheets(xlsxPath, sheets);
        }
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

        /// <summary>
        /// Loads rules from ALL sheets in the workbook.
        /// Each sheet gets a top-level section header with the sheet name.
        /// </summary>
        public static IEnumerable<MappingRuleEntry> LoadAll(string xlsxPath)
        {
            ValidatePath(xlsxPath);

            var sheetNames = ReadSheetNames(xlsxPath);
            foreach (var sheetName in sheetNames)
            {
                foreach (var entry in LoadSheetInternal(xlsxPath, sheetName))
                    yield return entry;
            }
        }

        /// <summary>
        /// Loads rules from a single named sheet.
        /// </summary>
        public static IEnumerable<MappingRuleEntry> LoadSheet(string xlsxPath, string sheetName)
        {
            ValidatePath(xlsxPath);
            return LoadSheetInternal(xlsxPath, sheetName);
        }

        /// <summary>
        /// Loads rules from multiple named sheets.
        /// </summary>
        public static IEnumerable<MappingRuleEntry> LoadSheets(string xlsxPath, List<string> sheetNames)
        {
            ValidatePath(xlsxPath);

            var available = ReadSheetNames(xlsxPath);
            foreach (var sheetName in sheetNames)
            {
                if (available.Any(s => s.Equals(sheetName, StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var entry in LoadSheetInternal(xlsxPath, sheetName))
                        yield return entry;
                }
            }
        }

        private static IEnumerable<MappingRuleEntry> LoadSheetInternal(string xlsxPath, string sheetName)
        {
            // Emit a top-level section header for this sheet
            yield return new MappingRuleEntry(
                IsSection: true,
                SectionTitle: $"━━━ {sheetName} ━━━",
                VueOneElement: string.Empty,
                IEC61499Element: string.Empty,
                Type: MappingType.SECTION,
                TransformationRule: string.Empty,
                IsImplemented: false);

            var rawRows = ReadXlsxSheet(xlsxPath, sheetName);
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

        // ── xlsx multi-sheet reading ────────────────────────────────────────

        /// <summary>
        /// Reads sheet names from the workbook in order.
        /// Parses xl/workbook.xml for sheet elements.
        /// </summary>
        private static List<string> ReadSheetNames(string xlsxPath)
        {
            var names = new List<string>();

            using var zip = ZipFile.OpenRead(xlsxPath);
            var wbEntry = zip.GetEntry("xl/workbook.xml");
            if (wbEntry == null) return names;

            using var stream = wbEntry.Open();
            var doc = XDocument.Load(stream);
            XNamespace ns = doc.Root!.GetDefaultNamespace();

            // Read sheet name → rId mapping from workbook.xml
            var sheets = doc.Descendants(ns + "sheet").ToList();
            foreach (var sheet in sheets)
            {
                var name = (string?)sheet.Attribute("name");
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }

            return names;
        }

        /// <summary>
        /// Resolves sheet name to the xl/worksheets/sheetN.xml path inside the zip.
        /// Uses xl/workbook.xml (name→rId) + xl/_rels/workbook.xml.rels (rId→target).
        /// </summary>
        private static string? ResolveSheetPath(ZipArchive zip, string sheetName)
        {
            // Step 1: workbook.xml — find rId for the named sheet
            var wbEntry = zip.GetEntry("xl/workbook.xml");
            if (wbEntry == null) return null;

            string? rId = null;
            using (var wbStream = wbEntry.Open())
            {
                var wbDoc = XDocument.Load(wbStream);
                XNamespace ns = wbDoc.Root!.GetDefaultNamespace();
                XNamespace rNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

                var sheetEl = wbDoc.Descendants(ns + "sheet")
                    .FirstOrDefault(s => string.Equals(
                        (string?)s.Attribute("name"), sheetName, StringComparison.OrdinalIgnoreCase));

                if (sheetEl == null) return null;
                rId = (string?)sheetEl.Attribute(rNs + "id");
            }

            if (string.IsNullOrEmpty(rId)) return null;

            // Step 2: workbook.xml.rels — resolve rId to target path
            var relsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
            if (relsEntry == null) return null;

            using (var relsStream = relsEntry.Open())
            {
                var relsDoc = XDocument.Load(relsStream);
                XNamespace relNs = relsDoc.Root!.GetDefaultNamespace();

                var rel = relsDoc.Descendants(relNs + "Relationship")
                    .FirstOrDefault(r => (string?)r.Attribute("Id") == rId);

                if (rel == null) return null;

                var target = (string?)rel.Attribute("Target");
                if (string.IsNullOrEmpty(target)) return null;

                // Target can be absolute ("/xl/worksheets/sheet1.xml")
                // or relative ("worksheets/sheet1.xml")
                if (target.StartsWith("/"))
                    return target.TrimStart('/');
                else
                    return "xl/" + target;
            }
        }

        /// <summary>
        /// Reads all rows from a specific named sheet.
        /// </summary>
        private static List<List<string>> ReadXlsxSheet(string path, string sheetName)
        {
            var rows = new List<List<string>>();

            using var zip = ZipFile.OpenRead(path);

            // Parse shared strings (common across all sheets)
            var sharedStrings = ReadSharedStrings(zip);

            // Resolve sheet name to actual file path inside zip
            var sheetPath = ResolveSheetPath(zip, sheetName);
            if (sheetPath == null) return rows;

            var sheetEntry = zip.GetEntry(sheetPath);
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

        /// <summary>
        /// Reads shared strings table (shared across all sheets in the workbook).
        /// </summary>
        private static List<string> ReadSharedStrings(ZipArchive zip)
        {
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
            return sharedStrings;
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static void ValidatePath(string xlsxPath)
        {
            if (string.IsNullOrWhiteSpace(xlsxPath) || !File.Exists(xlsxPath))
                throw new FileNotFoundException(
                    $"Mapping rules spreadsheet not found:\n{xlsxPath}\n\nSet MappingRulesPath in mapper_config.json.");
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