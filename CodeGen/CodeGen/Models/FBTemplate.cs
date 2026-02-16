namespace CodeGen.Models
{
    public class FBTemplate
    {
        public string TemplateName { get; set; } = string.Empty;
        public string TemplateFilePath { get; set; } = string.Empty;
        public int ExpectedStateCount { get; set; }
        public string ComponentType { get; set; } = string.Empty;
    }

    public class GeneratedFB
    {
        public string FBName { get; set; } = string.Empty;
        public string GUID { get; set; } = string.Empty;
        public string ComponentName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string FbtFile { get; set; } = string.Empty;
        public string CompositeFile { get; set; } = string.Empty;
        public string DocFile { get; set; } = string.Empty;
        public string MetaFile { get; set; } = string.Empty;
        public bool IsValid { get; set; }
    }
}
