namespace VueOneMapper.Models
{
    /// <summary>
    /// Represents the metadata needed to generate an IEC 61499 Function Block
    /// </summary>
    public class FBTemplate
    {
        public string TemplateName { get; set; }
        public string TemplateFilePath { get; set; }
        public int ExpectedStateCount { get; set; }
        public string ComponentType { get; set; }
    }

    /// <summary>
    /// Represents the generated Function Block output
    /// </summary>
    public class GeneratedFB
    {
        public string FBName { get; set; }
        public string GUID { get; set; }
        public string ComponentName { get; set; }
        public string FilePath { get; set; }
        public bool IsValid { get; set; }
    }
}