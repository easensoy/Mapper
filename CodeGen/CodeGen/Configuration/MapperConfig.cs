using System;
using System.IO;
using System.Text.Json;

namespace CodeGen.Configuration
{
    public class MapperConfig
    {
        const string ConfigFileName = "mapper_config.json";

        public string SystemXmlPath { get; set; } = string.Empty;
        public string ActuatorTemplatePath { get; set; } = string.Empty;
        public string SensorTemplatePath { get; set; } = string.Empty;
        public string ProcessCATTemplatePath { get; set; } = string.Empty;
        public string RobotTemplatePath { get; set; } = string.Empty;
        public string RobotBasicTemplatePath { get; set; } = string.Empty;
        public string SyslayPath { get; set; } = string.Empty;
        public string SysresPath { get; set; } = string.Empty;
        public string SyslayPath2 { get; set; } = string.Empty;
        public string SysresPath2 { get; set; } = string.Empty;
        public string RuleEnginePath { get; set; } = string.Empty;

        public string ActiveSyslayPath => !string.IsNullOrEmpty(SyslayPath2) ? SyslayPath2 : SyslayPath;
        public string ActiveSysresPath => !string.IsNullOrEmpty(SysresPath2) ? SysresPath2 : SysresPath;
        public string TemplateIec61499Dir => Path.GetDirectoryName(Path.GetDirectoryName(ActuatorTemplatePath)) ?? string.Empty;
        public string TemplateHmiDir => Path.Combine(Path.GetDirectoryName(TemplateIec61499Dir) ?? string.Empty, "HMI");

        public static MapperConfig Load()
        {
            var configPath = Path.Combine(Environment.CurrentDirectory, ConfigFileName);
            if (!File.Exists(configPath)) { var d = CreateDefault(); Save(configPath, d); return d; }
            var json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<MapperConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new Exception($"Failed to deserialise {configPath}");
        }

        static MapperConfig CreateDefault() => new()
        {
            SystemXmlPath = @"C:\VueOne\system\Control.xml",
            ActuatorTemplatePath = @"C:\Station1 - Sensor and FiveStateActuator with symbolic links_20260203-120117390.sln (1)\IEC61499\Five_State_Actuator_CAT\Five_State_Actuator_CAT.fbt",
            SensorTemplatePath = @"C:\Station1 - Sensor and FiveStateActuator with symbolic links_20260203-120117390.sln (1)\IEC61499\Sensor_Bool_CAT\Sensor_Bool_CAT.fbt",
            ProcessCATTemplatePath = @"C:\Station1 - Sensor and FiveStateActuator with symbolic links_20260203-120117390.sln (1)\IEC61499\Process1_CAT\Process1_CAT.fbt",
            RobotTemplatePath = @"C:\SMC_Rig_Expo_20260112-165857725.sln\IEC61499\Robot_Task_CAT\Robot_Task_CAT.fbt",
            RobotBasicTemplatePath = @"C:\SMC_Rig_Expo_20260112-165857725.sln\IEC61499\Robot_Task_Core.fbt",
            SyslayPath = @"C:\Station1 - Sensor and FiveStateActuator with symbolic links_20260203-120117390.sln (1)\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000001\00000000-0000-0000-0000-000000000000.syslay",
            SysresPath = @"C:\Station1 - Sensor and FiveStateActuator with symbolic links_20260203-120117390.sln (1)\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000002\00000000-0000-0000-0000-000000000000.sysres",
            SyslayPath2 = @"C:\Demonstrator\Demonstator\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000001\00000000-0000-0000-0000-000000000000.syslay",
            SysresPath2 = @"C:\Demonstrator\Demonstator\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000002\00000000-0000-0000-0000-000000000000.sysres",
            RuleEnginePath = @"Input\VueOne_IEC61499_Mapping.xlsx",
        };

        static void Save(string path, MapperConfig config)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}