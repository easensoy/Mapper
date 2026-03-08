using System.Collections.Generic;

namespace MapperUI.Services
{
    public class SystemInjectionResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public string SyslayPath { get; set; } = string.Empty;
        public string SysresPath { get; set; } = string.Empty;

        /// <summary>FBs that were remapped or newly inserted in this run.</summary>
        public List<string> InjectedFBs { get; set; } = new();

        /// <summary>FBs already present and correct — no change needed.</summary>
        public List<string> SkippedFBs { get; set; } = new();

        /// <summary>Components skipped because their type has no CAT template.</summary>
        public List<string> UnsupportedComponents { get; set; } = new();
    }
}