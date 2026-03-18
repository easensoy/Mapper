// MapperUI/MapperUI/Services/MappingRuleEngine.cs
// Defines the data types and the public API.
// Xlsx reading is handled by XlsxRuleLoader in RuleEngine.cs.

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
    /// Records are immutable by default — no getters/setters needed.
    /// When IsSection = true the row is a visual group header (no rule data).
    /// </summary>
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
        /// <summary>
        /// Loads all mapping rules from the xlsx spreadsheet at
        /// <paramref name="xlsxPath"/>.
        /// </summary>
        public static IEnumerable<MappingRuleEntry> GetAllRules(string xlsxPath)
            => XlsxRuleLoader.Load(xlsxPath);

        /// <summary>
        /// Same as <see cref="GetAllRules"/> — component-type filters reserved for
        /// a future phase.
        /// </summary>
        public static IEnumerable<MappingRuleEntry> GetRelevantRules(
            string xlsxPath,
            bool hasActuator, bool hasSensor, bool hasProcess)
            => XlsxRuleLoader.Load(xlsxPath);
    }
}