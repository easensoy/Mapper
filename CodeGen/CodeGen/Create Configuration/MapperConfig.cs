using Newtonsoft.Json;
using System;
using System.IO;
using System.Xml;

namespace VueOneMapper.Configuration
{
    /// <summary>
    /// Configuration settings for the VueOne Mapper
    /// </summary>
    public class MapperConfig
    {
        public string ControlXmlPath { get; set; }
        public string TemplateDirectory { get; set; }
        public string OutputDirectory { get; set; }
        public string EAEProjectPath { get; set; }

        /// <summary>
        /// Load configuration from config.json
        /// </summary>
        public static MapperConfig Load(string configPath = "config.json")
        {
            if (!File.Exists(configPath))
            {
                // Create default config
                var defaultConfig = new MapperConfig
                {
                    ControlXmlPath = @"C:\VueOne\component\Pusher\Control.xml",
                    TemplateDirectory = @"Templates",
                    OutputDirectory = @"Output",
                    EAEProjectPath = @"C:\EAE\SMC_Rig_Expo\Composite"
                };

                // Save default config
                string json = JsonConvert.SerializeObject(defaultConfig, Formatting.Indented);
                File.WriteAllText(configPath, json);

                return defaultConfig;
            }

            string existingJson = File.ReadAllText(configPath);
            return JsonConvert.DeserializeObject<MapperConfig>(existingJson);
        }

        /// <summary>
        /// Save configuration to config.json
        /// </summary>
        public void Save(string configPath = "config.json")
        {
            string json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(configPath, json);
        }
    }
}