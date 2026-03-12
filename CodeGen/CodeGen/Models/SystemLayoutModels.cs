using System.Collections.Generic;

namespace MapperUI.Services
{
    public class SystemInjectionResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public string SyslayPath { get; set; } = string.Empty;
        public string SysresPath { get; set; } = string.Empty;
        public List<string> InjectedFBs { get; set; } = new();
        public List<string> SkippedFBs { get; set; } = new();
        public List<string> UnsupportedComponents { get; set; } = new();
    }
}