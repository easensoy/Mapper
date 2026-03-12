// CodeGen/CodeGen/Configuration/MapperConfig.cs
using System;
using System.IO;
using System.Text.Json;

namespace CodeGen.Configuration
{
    public class MapperConfig
    {
        private const string ConfigFileName = "mapper_config.json";

        // ── Source: VueOne Control.xml ─────────────────────────────────────
        public string SystemXmlPath { get; set; } = string.Empty;

        // ── Template library (source project with validated CAT types) ────
        public string ActuatorTemplatePath { get; set; } = string.Empty;
        public string SensorTemplatePath { get; set; } = string.Empty;
        public string ProcessCATTemplatePath { get; set; } = string.Empty;
        public string RobotTemplatePath { get; set; } = string.Empty;
        public string RobotBasicTemplatePath { get; set; } = string.Empty;

        // ── Target project (where Mapper writes output) ───────────────────
        public string SyslayPath { get; set; } = string.Empty;
        public string SysresPath { get; set; } = string.Empty;

        // ── Secondary target (blank EAE project, e.g. Demonstrator) ───────
        public string SyslayPath2 { get; set; } = string.Empty;
        public string SysresPath2 { get; set; } = string.Empty;

        // ── Derived helpers ───────────────────────────────────────────────

        /// <summary>
        /// Returns the active syslay path. Uses Path2 if set, else Path1.
        /// </summary>
        public string ActiveSyslayPath =>
            !string.IsNullOrEmpty(SyslayPath2) ? SyslayPath2 : SyslayPath;

        /// <summary>
        /// Returns the active sysres path. Uses Path2 if set, else Path1.
        /// </summary>
        public string ActiveSysresPath =>
            !string.IsNullOrEmpty(SysresPath2) ? SysresPath2 : SysresPath;

        /// <summary>
        /// Derives the IEC61499 root directory from the template path.
        /// ActuatorTemplatePath points to .../IEC61499/Five_State_Actuator_CAT/Five_State_Actuator_CAT.fbt
        /// This returns .../IEC61499/
        /// </summary>
        public string TemplateIec61499Dir =>
            Path.GetDirectoryName(Path.GetDirectoryName(ActuatorTemplatePath)) ?? string.Empty;

        /// <summary>
        /// Derives the HMI directory sibling to the template IEC61499 folder.
        /// </summary>
        public string TemplateHmiDir =>
            Path.Combine(Path.GetDirectoryName(TemplateIec61499Dir) ?? string.Empty, "HMI");

        // ── Load / Save ───────────────────────────────────────────────────

        public static MapperConfig Load()
        {
            var configPath = Path.Combine(Environment.CurrentDirectory, ConfigFileName);

            if (!File.Exists(configPath))
            {
                var def = CreateDefault();
                Save(configPath, def);
                return def;
            }

            var json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<MapperConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new Exception($"Failed to deserialise config from '{configPath}'");
        }

        private static MapperConfig CreateDefault() => new()
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
        };

        private static void Save(string path, MapperConfig config)
        {
            var json = JsonSerializer.Serialize(config,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
    }
}