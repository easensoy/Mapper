using CodeGen.Configuration;

namespace CodeGen.Translation.Interlocks
{
    /// <summary>
    /// Interlock generation policy from <c>Config/interlock.yaml</c> — the array size, the STRUCT
    /// interface flag, and the centre-home raw-state range. The rules come from Control.xml
    /// (<see cref="InterlockPlanner"/>); this is generation policy only.
    /// </summary>
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
