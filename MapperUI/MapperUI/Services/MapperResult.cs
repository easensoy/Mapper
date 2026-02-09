using CodeGen.Models;
using CodeGen.Validation;

namespace MapperUI.Services
{
    public class MapperResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public string ComponentName { get; set; } = string.Empty;
        public ValidationResult? ValidationResult { get; set; }
        public GeneratedFB? GeneratedFB { get; set; }
        public string OutputPath { get; set; } = string.Empty;
        public string DeployPath { get; set; } = string.Empty;
    }
}