using System;
using System.IO;
using System.Text.Json;

namespace CodeGen.Configuration
{
    public class MapperConfig
    {
        private const string ConfigFileName = "mapper_config.json";

        public string SystemXmlPath { get; set; } = string.Empty;
        public string MappingRulesPath { get; set; } = string.Empty;
        public string TemplateLibraryPath { get; set; } = string.Empty;
        public string ActuatorTemplatePath { get; set; } = string.Empty;
        public string SensorTemplatePath { get; set; } = string.Empty;
        public string ProcessCATTemplatePath { get; set; } = string.Empty;
        public string RobotTemplatePath { get; set; } = string.Empty;
        public string RobotBasicTemplatePath { get; set; } = string.Empty;
        public string SyslayPath { get; set; } = string.Empty;
        public string SysresPath { get; set; } = string.Empty;
        public string SyslayPath2 { get; set; } = string.Empty;
        public string SysresPath2 { get; set; } = string.Empty;
        public string IoBindingsPath { get; set; } = "Input/SMC_Rig_IO_Bindings.xlsx";

        /// <summary>
        /// Path to a folder containing a validated M262 hardware-configuration baseline
        /// (an EAE project's <c>HwConfiguration/</c> folder plus the <c>.hcf</c> file
        /// under <c>IEC61499/System/{sys-guid}/{sysdev-guid}/{sysdev-guid}.hcf</c>).
        /// The TM3 module slot/topology layout — BMTM3 → TM262L01MDESE8T → TM3DI16_G →
        /// TM3DQ16T_G — is fixed by the physical SMC rig wiring and therefore cannot
        /// be synthesised from Control.xml; the deployer copies it verbatim from this
        /// path and only overwrites the channel ParameterValue strings (DI00, DI01,
        /// DO00) using IoBindings.
        /// </summary>
        public string M262HardwareConfigBaselinePath { get; set; } = string.Empty;

        /// <summary>
        /// IPV4 address of the M262 controller on the rig network. Written into the
        /// EcoRT_0 sysdev as <c>&lt;Parameter Name="IPV4Address" Value="..."/&gt;</c>
        /// so EAE's Physical Devices canvas shows the controller pre-populated. Operator
        /// can still override in EAE before deploy if the rig moves networks.
        /// </summary>
        public string M262TargetIp { get; set; } = "192.168.1.10";

        /// <summary>
        /// Subnet/network parameters used by the M262 topology emitter so the
        /// Physical Devices canvas shows the M262 wired to a logical network with
        /// a configured IP. Driven from the rig wiring — defaults are the SMC rig
        /// 192.168.1.0/24.
        /// </summary>
        public string M262SubnetAddress { get; set; } = "192.168.1.0";
        public string M262SubnetMask { get; set; } = "255.255.255.0";
        public string M262Gateway { get; set; } = "192.168.1.254";
        public string M262LogicalNetworkName { get; set; } = "DeviceNetwork_1";

        public string ActiveSyslayPath =>
            !string.IsNullOrEmpty(SyslayPath2) ? SyslayPath2 : SyslayPath;

        public string ActiveSysresPath =>
            !string.IsNullOrEmpty(SysresPath2) ? SysresPath2 : SysresPath;

        public string TemplateIec61499Dir =>
            Path.GetDirectoryName(Path.GetDirectoryName(ActuatorTemplatePath)) ?? string.Empty;

        public string TemplateHmiDir =>
            Path.Combine(Path.GetDirectoryName(TemplateIec61499Dir) ?? string.Empty, "HMI");

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
            MappingRulesPath = @"Input\VueOne_IEC61499_Mapping.xlsx",
            TemplateLibraryPath = @"C:\VueOneMapper\Template Library",
            ActuatorTemplatePath = @"C:\Station1\IEC61499\Five_State_Actuator_CAT\Five_State_Actuator_CAT.fbt",
            SensorTemplatePath = @"C:\Station1\IEC61499\Sensor_Bool_CAT\Sensor_Bool_CAT.fbt",
            // Process1_Generic.fbt is the new outer composite template (Phase 1+2);
            // the legacy Process1_CAT.fbt is no longer deployed by Mapper.
            ProcessCATTemplatePath = @"C:\Station1\IEC61499\Process1_Generic\Process1_Generic.fbt",
            RobotTemplatePath = @"C:\SMC_Rig\IEC61499\Robot_Task_CAT\Robot_Task_CAT.fbt",
            RobotBasicTemplatePath = @"C:\SMC_Rig\IEC61499\Robot_Task_Core.fbt",
            SyslayPath = @"C:\Station1\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000001\00000000-0000-0000-0000-000000000000.syslay",
            SysresPath = @"C:\Station1\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000002\00000000-0000-0000-0000-000000000000.sysres",
            SyslayPath2 = @"C:\Demonstrator\Demonstator\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000001\00000000-0000-0000-0000-000000000000.syslay",
            SysresPath2 = @"C:\Demonstrator\Demonstator\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000002\00000000-0000-0000-0000-000000000000.sysres",
            IoBindingsPath = @"Input\SMC_Rig_IO_Bindings.xlsx",
            M262HardwareConfigBaselinePath = string.Empty,
            M262TargetIp = "192.168.1.10",
            M262SubnetAddress = "192.168.1.0",
            M262SubnetMask = "255.255.255.0",
            M262Gateway = "192.168.1.254",
            M262LogicalNetworkName = "DeviceNetwork_1",
        };

        private static void Save(string path, MapperConfig config)
        {
            var json = JsonSerializer.Serialize(config,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
    }
}
