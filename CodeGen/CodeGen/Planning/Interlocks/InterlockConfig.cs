using CodeGen.Configuration;

namespace CodeGen.Translation.Interlocks
{
    // Interlock generation policy from Config/interlock.yaml (rules come from Control.xml via InterlockPlanner).
    public sealed class InterlockConfig
    {
        public int RuleArraySize { get; set; }

        private static readonly YamlConfigFile<InterlockConfig> _file = new("Config", "interlock.yaml");

        /// The same declaration read from a run's OWN profile bundle. A root of null is the
        /// bundle shipped beside CodeGen.dll, which is what a normal run reads.
        public static InterlockConfig LoadFrom(string? root) => _file.Load(root);
    }
}
