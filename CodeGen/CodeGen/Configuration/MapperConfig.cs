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
            if (!File.Exists(configPath))
            {
                Save(configPath, new MapperConfig());
                throw new FileNotFoundException(
                    $"mapper_config.json not found. A blank template was created at:\n{configPath}\n\nFill in the paths and restart.");
            }
            var json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<MapperConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new Exception($"Failed to deserialise {configPath}");
        }

        static void Save(string path, MapperConfig config)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}