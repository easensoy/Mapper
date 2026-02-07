using System.IO;
using Newtonsoft.Json;

namespace CodeGen.Configuration
{
    public class MapperConfig
    {
        public string ControlXmlPath { get; set; } = string.Empty;
        public string TemplatePath { get; set; } = string.Empty;
        public string OutputDirectory { get; set; } = string.Empty;
        public string EAEDeployPath { get; set; } = string.Empty;

        public static MapperConfig Load(string configPath = "config.json")
        {
            if (!File.Exists(configPath))
            {
                var defaultConfig = new MapperConfig
                {
                    ControlXmlPath = "C:\\VueOne\\component\\Pusher\\Control.xml",
                    TemplatePath = "C:\\SMC_Rig_Expo_20260112-165857725.sln\\IEC61499\\Five_State_Actuator.fbt",
                    OutputDirectory = "Output",
                    EAEDeployPath = "C:\\SMC_Rig_Expo_20260112-165857725.sln\\IEC61499"
                };
                defaultConfig.Save(configPath);
                return defaultConfig;
            }

            return JsonConvert.DeserializeObject<MapperConfig>(File.ReadAllText(configPath))!;
        }

        public void Save(string configPath = "config.json")
        {
            File.WriteAllText(configPath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }
}