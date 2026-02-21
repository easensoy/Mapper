using System;
using System.IO;
using System.Text.Json;

namespace CodeGen.Configuration
{
    public class MapperConfig
    {
        private const string ConfigFileName = "mapper_config.json";

        public string ActuatorXmlPath { get; set; } = string.Empty;
        public string SensorXmlPathHopper { get; set; } = string.Empty;
        public string SensorXmlPathChecker { get; set; } = string.Empty;
        public string ActuatorTemplatePath { get; set; } = string.Empty;
        public string SensorTemplatePath { get; set; } = string.Empty;
        public string OutputDirectory { get; set; } = string.Empty;
        public string EAEDeployPath { get; set; } = string.Empty;

        public static MapperConfig Load()
        {
            var configPath = Path.Combine(Environment.CurrentDirectory, ConfigFileName);

            if (!File.Exists(configPath))
            {
                var defaultConfig = CreateDefault();
                WriteConfig(configPath, defaultConfig);
                return defaultConfig;
            }

            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<MapperConfig>(json);

            if (config == null)
            {
                throw new Exception($"Failed to load config from '{configPath}'");
            }

            return config;
        }
        private static MapperConfig CreateDefault()
        {
            return new MapperConfig
            {
                ActuatorXmlPath = @"C:\VueOne\component\Pusher\Control.xml",
                SensorXmlPathHopper = @"C:\VueOne\component\Part_In_Hopper\Control.xml",
                SensorXmlPathChecker = @"C:\VueOne\component\Part_In_Checker_\Control.xml",
                // FIX: was Five_State_Actuator.fbt (non-CAT). Must point to the CAT template subfolder.
                ActuatorTemplatePath = @"C:\SMC_Rig_Expo_20260112-165857725.sln\IEC61499\Five_State_Actuator_CAT\Five_State_Actuator_CAT.fbt",
                SensorTemplatePath = @"C:\Station1 - Sensor and FiveStateActuator with symbolic links_20260203-120117390.sln (1)\IEC61499\Sensor_Bool_CAT\Sensor_Bool_CAT.fbt",
                OutputDirectory = "Output",
                EAEDeployPath = @"C:\SMC_Rig_Expo_20260112-165857725.sln\IEC61499"
            };
        }

        private static void WriteConfig(string configPath, MapperConfig config)
        {
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(configPath, json);
        }
    }
}
