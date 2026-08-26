using System;
using System.IO;
using CodeGen.Configuration;

namespace MapperTests
{
    // THE TEST ASSEMBLY'S COMPOSITION ROOT. A test reads the declarations exactly the way a run does -
    // once - so a test and a generation cannot compile against different configurations, and no test
    // reaches a configuration singleton for itself.
    internal static class TestConfig
    {
        internal static CompilerConfiguration Cfg { get; } = Load();

        static CompilerConfiguration Load()
        {
            var paths = new MapperConfig();

            // The library is an INPUT that lives in the working tree, so a test that renders a template
            // document can find it without depending on how this machine's mapper_config.json is set.
            var repo = RepoRoot();
            if (repo != null)
            {
                paths.TemplateLibraryPath = Path.Combine(repo, "Template Library");
                paths.IoFolderPath = Path.Combine(repo, "IO");
            }
            return CompilerConfiguration.Load(paths);
        }

        static string? RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Template Library")))
                dir = dir.Parent;
            return dir?.FullName;
        }
    }
}
