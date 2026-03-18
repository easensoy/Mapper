// MapperUI/MapperUI/Services/MappingRuleEngine.cs
// Types live here. Xlsx reading lives in RuleEngine.cs (XlsxRuleLoader).

using System.Collections.Generic;

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

    /// <summary>
    /// A single row in the Mapping Rules table.
    /// When IsSection = true the row is a visual group header (no rule data).
    /// </summary>
    public class MappingRuleEntry
    {
        public bool IsSection { get; init; }
        public string SectionTitle { get; init; } = string.Empty;
        public string VueOneElement { get; init; } = string.Empty;
        public string IEC61499Element { get; init; } = string.Empty;
        public MappingType Type { get; init; }
        public string TransformationRule { get; init; } = string.Empty;

        /// <summary>
        /// True  = handled in current phase (shows ✓).
        /// False = planned but not yet implemented (shows ✗).
        /// Ignored for SECTION rows.
        /// </summary>
        public bool IsImplemented { get; init; }
    }

    public static class MappingRuleEngine
    {
        /// <summary>
        /// Loads all mapping rules from the xlsx spreadsheet at
        /// <paramref name="xlsxPath"/>. Delegates to XlsxRuleLoader in RuleEngine.cs.
        /// </summary>
        public static IEnumerable<MappingRuleEntry> GetAllRules(string xlsxPath)
            => XlsxRuleLoader.Load(xlsxPath);

        /// <summary>
        /// Same as GetAllRules — component-type filters reserved for a future phase.
        /// </summary>
        public static IEnumerable<MappingRuleEntry> GetRelevantRules(
            string xlsxPath,
            bool hasActuator, bool hasSensor, bool hasProcess)
            => XlsxRuleLoader.Load(xlsxPath);
    }
}