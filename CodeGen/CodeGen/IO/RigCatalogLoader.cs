namespace CodeGen.Configuration
{
    internal static class RigCatalogLoader
    {
        private static readonly YamlConfigFile<RigCatalog> _file =
            new("Config", "smc-rig.yml") { OnLoaded = RigCatalogValidator.Validate };

        public static RigCatalog Catalog => _file.Load();

        /// The same declaration read from a run's OWN profile bundle.
        public static RigCatalog LoadFrom(string? root) => _file.Load(root);
    }
}
