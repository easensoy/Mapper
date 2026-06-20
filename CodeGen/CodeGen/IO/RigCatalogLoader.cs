namespace CodeGen.Configuration
{
    internal static class RigCatalogLoader
    {
        private static readonly YamlConfigFile<RigCatalog> _file =
            new("Config", "smc-rig.yml") { OnLoaded = RigCatalogValidator.Validate };

        public static RigCatalog Catalog => _file.Load();
    }
}
