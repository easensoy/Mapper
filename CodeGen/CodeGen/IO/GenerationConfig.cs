namespace CodeGen.Configuration
{
    public sealed class GenerationConfig
    {
        public int RecipeArraySize { get; set; }
        public int DefaultMotionMs { get; set; }
        public int CoverMotionMs { get; set; }

        private static readonly YamlConfigFile<GenerationConfig> _file = new("Config", "config.yaml");

        public static GenerationConfig Current => _file.Load();
    }
}
