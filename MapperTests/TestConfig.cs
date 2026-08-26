using CodeGen.Configuration;

namespace MapperTests
{
    // THE TEST ASSEMBLY'S COMPOSITION ROOT. A test reads the declarations exactly the way a run does -
    // once - so a test and a generation cannot compile against different configurations, and no test
    // reaches a configuration singleton for itself.
    internal static class TestConfig
    {
        internal static CompilerConfiguration Cfg { get; } =
            CompilerConfiguration.Load(new MapperConfig());
    }
}
