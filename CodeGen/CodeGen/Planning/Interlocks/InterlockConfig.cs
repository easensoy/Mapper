using CodeGen.Configuration;

namespace CodeGen.Translation.Interlocks
{
    // Interlock generation policy from Config/interlock.yaml (rules come from Control.xml via InterlockPlanner).
    public sealed class InterlockConfig
    {
        public int RuleArraySize { get; set; }
        public bool UseStruct { get; set; }
        public bool UseTargetStruct { get; set; }
        public CentreHomeRange CentreHome { get; set; } = new();

        private static readonly YamlConfigFile<InterlockConfig> _file = new("Config", "interlock.yaml");

        public static InterlockConfig Current => _file.Load();
    }

    public sealed class CentreHomeRange
    {
        public int MinState { get; set; }
        public int MaxState { get; set; }
    }
}
