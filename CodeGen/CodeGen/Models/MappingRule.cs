namespace VueOneMapper.Models
{
    /// <summary>
    /// Represents a mapping rule from VueOne to IEC 61499
    /// Based on VueOne_IEC61499_Mapping.xlsx
    /// </summary>
    public class MappingRule
    {
        public string VueOneElement { get; set; }
        public string IEC61499Element { get; set; }
        public MappingType Type { get; set; }
        public string TransformationRule { get; set; }
        public string Notes { get; set; }
    }

    public enum MappingType
    {
        TRANSLATED,   // Direct 1:1 mapping
        ENCODED,      // Requires transformation
        DISCARDED,    // VueOne-specific, not used in IEC 61499
        ASSUMED       // Process engineer decision
    }
}