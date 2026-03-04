namespace CodeGen.Models
{
    public class MappingRule
    {
        public string VueOneElement { get; set; } = string.Empty;
        public string IEC61499Element { get; set; } = string.Empty;
        public MappingType Type { get; set; }
        public string TransformationRule { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public bool Validated { get; set; } = true;
    }

    public enum MappingType
    {
        TRANSLATED,
        HARDCODED,
        ASSUMED,
        ENCODED,
        DISCARDED
    }
}