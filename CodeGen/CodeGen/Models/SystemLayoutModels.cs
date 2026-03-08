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

        /// <summary>FBs that were already correct and needed no change.</summary>
        public List<string> SkippedFBs { get; set; } = new();

        /// <summary>Components skipped due to unsupported type or batch limit.</summary>
        public List<string> UnsupportedComponents { get; set; } = new();

        /// <summary>
        /// True when MaxNewInsertionsPerRun was reached before all components were injected.
        /// The user should run Generate Code again to inject the next batch.
        /// </summary>
        public bool LimitReached { get; set; }
    }
}