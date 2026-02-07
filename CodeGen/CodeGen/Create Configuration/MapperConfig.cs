using System;
using System.IO;
using Newtonsoft.Json;

namespace CodeGen.Configuration
{
    public class MapperConfig
    {
        public string ControlXmlPath { get; set; } = string.Empty;
        public string TemplateDirectory { get; set; } = "Templates";
        public string OutputDirectory { get; set; } = "Output";
        public string EAEProjectPath { get; set; } = string.Empty;

        public static MapperConfig Load(string configPath = "config.json")
        {
            if (!File.Exists(configPath))
            {
                var defaultConfig = CreateDefault();
                defaultConfig.Save(configPath);
                return defaultConfig;
            }

            var json = File.ReadAllText(configPath);
            var config = JsonConvert.DeserializeObject<MapperConfig>(json);
            config.ExpandPaths();
            return config;
        }

        public void Save(string configPath = "config.json")
        {
            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(configPath, json);
        }

        private static MapperConfig CreateDefault()
        {
            return new MapperConfig
            {
                ControlXmlPath = "$(UserProfile)\\VueOne\\component\\Pusher\\Control.xml",
                TemplateDirectory = "Templates",
                OutputDirectory = "Output",
                EAEProjectPath = ""
            };
        }

        private void ExpandPaths()
        {
            ControlXmlPath = ExpandEnvironmentPath(ControlXmlPath);
            EAEProjectPath = ExpandEnvironmentPath(EAEProjectPath);
        }

        private string ExpandEnvironmentPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

            if (path.Contains("$(UserProfile)"))
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                path = path.Replace("$(UserProfile)", userProfile);
            }

            return path;
        }
    }
}