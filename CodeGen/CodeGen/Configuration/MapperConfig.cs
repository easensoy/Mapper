using System;
using System.IO;
using System.Text.Json;

namespace CodeGen.Configuration
{
    public class MapperConfig
    {
        private const string ConfigFileName = "mapper_config.json";

        // ── Phase 1: Generate FB ──────────────────────────────────────────────
        public string ActuatorXmlPath { get; set; } = string.Empty;
        public string SensorXmlPathHopper { get; set; } = string.Empty;
        public string SensorXmlPathChecker { get; set; } = string.Empty;
        public string ActuatorTemplatePath { get; set; } = string.Empty;
        public string SensorTemplatePath { get; set; } = string.Empty;
        public string OutputDirectory { get; set; } = string.Empty;
        public string EAEDeployPath { get; set; } = string.Empty;

        // ── Phase 2: Inject System ────────────────────────────────────────────
        public string SystemXmlPath { get; set; } = string.Empty;
        public string SyslayPath { get; set; } = string.Empty;
        public string SysresPath { get; set; } = string.Empty;
        public string ProcessCATTemplatePath { get; set; } = string.Empty;

        /// <summary>
        /// Full path to Robot_Task_CAT.fbt.
        /// Leave empty to treat Robot components as unsupported (shows ✗ in UI).
        /// </summary>
        public string RobotTemplatePath { get; set; } = string.Empty;

        // ─────────────────────────────────────────────────────────────────────

        public static MapperConfig Load()
        {
            var configPath = Path.Combine(Environment.CurrentDirectory, ConfigFileName);

            if (!File.Exists(configPath))
            {
                var def = CreateDefault();
                WriteConfig(configPath, def);
                return def;
            }

            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<MapperConfig>(json)
                ?? throw new Exception($"Failed to deserialise config from '{configPath}'");

            return config;
        }

        private static MapperConfig CreateDefault() => new()
        {
            ActuatorXmlPath = @"C:\VueOne\component\Pusher\Control.xml",
            SensorXmlPathHopper = @"C:\VueOne\component\Part_In_Hopper\Control.xml",
            SensorXmlPathChecker = @"C:\VueOne\component\Part_In_Checker_\Control.xml",
            ActuatorTemplatePath = @"C:\SMC_Rig_Expo_20260112-165857725.sln\IEC61499\Five_State_Actuator_CAT\Five_State_Actuator_CAT.fbt",
            SensorTemplatePath = @"C:\Station1 - Sensor and FiveStateActuator with symbolic links_20260203-120117390.sln (1)\IEC61499\Sensor_Bool_CAT\Sensor_Bool_CAT.fbt",
            OutputDirectory = "Output",
            EAEDeployPath = @"C:\SMC_Rig_Expo_20260112-165857725.sln\IEC61499",
            SystemXmlPath = @"C:\VueOne\system\Control.xml",
            SyslayPath = @"C:\Station1 - Sensor and FiveStateActuator with symbolic links_20260203-120117390.sln (1)\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000001\00000000-0000-0000-0000-000000000000.syslay",
            SysresPath = @"C:\Station1 - Sensor and FiveStateActuator with symbolic links_20260203-120117390.sln (1)\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000002\00000000-0000-0000-0000-000000000000.sysres",
            ProcessCATTemplatePath = @"C:\Station1 - Sensor and FiveStateActuator with symbolic links_20260203-120117390.sln (1)\IEC61499\Process1_CAT\Process1_CAT.fbt",
            RobotTemplatePath = @"C:\SMC_Rig_Expo_20260112-165857725.sln\IEC61499\Robot_Task_CAT\Robot_Task_CAT.fbt"
        };

        private static void WriteConfig(string path, MapperConfig config)
        {
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
    }
}