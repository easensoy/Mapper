using CodeGen.Configuration;

namespace CodeGen.Translation.Process
{
    internal static class RecipeConfigLoader
    {
        private static readonly YamlConfigFile<RecipeCatalog> _file = new("Config", "recipes.yml");

        public static RecipeCatalog Catalog => _file.Load();
    }
}
