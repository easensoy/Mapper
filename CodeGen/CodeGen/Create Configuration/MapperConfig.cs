using System;
using System.IO;
using System.Text.Json;

namespace CodeGen.Configuration
{
    public class MapperConfig
    {
        public string ActuatorXmlPath { get; set; } = string.Empty;
        public string SensorXmlPathHopper { get; set; } = string.Empty;
        public string SensorXmlPathChecker { get; set; } = string.Empty;
        public string ActuatorTemplatePath { get; set; } = string.Empty;
        public string SensorTemplatePath { get; set; } = string.Empty;
        public string OutputDirectory { get; set; } = string.Empty;
        public string EAEDeployPath { get; set; } = string.Empty;

        public static MapperConfig Load()
        {
            string configPath = "mapper_config.json";

            if (!File.Exists(configPath))
            {
                var defaultConfig = new MapperConfig
                {
                    ActuatorXmlPath = @"C:\VueOne\component\Pusher\Control.xml",
                    SensorXmlPathHopper = @"C:\VueOne\component\Part_In_Hopper\Control.xml",
                    SensorXmlPathChecker = @"C:\VueOne\component\Part_In_Checker_\Control.xml",
                    ActuatorTemplatePath = @"C:\Station1 - Sensor and FiveStateActuator with symbolic links_20260203-120117390.sln (1)\IEC61499\Five_State_Actuator_CAT\Five_State_Actuator_CAT.fbt",
                    SensorTemplatePath = @"C:\Station1 - Sensor and FiveStateActuator with symbolic links_20260203-120117390.sln (1)\IEC61499\Sensor_Bool_CAT\Sensor_Bool_CAT.fbt",
                    OutputDirectory = "Output",
                    EAEDeployPath = @"C:\Station1 - Sensor and FiveStateActuator with symbolic links_20260203-120117390.sln (1)\IEC61499"
                };

                File.WriteAllText(configPath, JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions { WriteIndented = true }));
                return defaultConfig;
            }

            return JsonSerializer.Deserialize<MapperConfig>(File.ReadAllText(configPath))
                   ?? throw new Exception("Failed to load config");
        }
    }
}