namespace CodeGen.Configuration
{
    public sealed class GenerationConfig
    {
        public int RecipeArraySize { get; set; }
        public int DefaultMotionMs { get; set; }
        public int CoverMotionMs { get; set; }
        public int CoverGripperAckMs { get; set; }
        public int BearingPnpHomeBrakeMs { get; set; }

        // Attempts for a generated-file write that EAE may be holding open.
        public int FileWriteRetries { get; set; }

        private static readonly YamlConfigFile<GenerationConfig> _file = new("Config", "config.yaml");

        public static GenerationConfig Current => _file.Load();
    }
}
