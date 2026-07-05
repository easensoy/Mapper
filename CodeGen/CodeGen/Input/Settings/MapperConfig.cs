using System;
using System.IO;
using System.Text.Json;

namespace CodeGen.Configuration
{
    public class MapperConfig
    {
        private const string ConfigFileName = "mapper_config.json";

        public static readonly bool StubSevenStateActuatorsAsFiveState = false;

        public static bool SimulatorRecipeMode = false;

        public static bool RecipeRunOnce = true;

        // Must stay false: cyclic restart re-fires each recipe's step-0 trigger on a held level and overlaps processes on shared actuators.
        public static bool EnableCyclicRestart = false;

        public static readonly string[] AutoRetractProcesses = new[] { "Feed_Station" };

        // Keep FALSE on the rig: if Bearing_PnP is already home, CMD Home is a no-op with no fresh report and the engine stalls at step 0.
        public static bool EnableSevenStateHomePreamble = false;

        // SAFETY: both-coils-on holds the swivel at centre ONLY if the cylinder has a mechanical mid-stop; with no mid-stop it drives toward an extreme.
        public static bool SwivelHomeHoldBothCoils = false;

        // SAFETY: directional brake (homing from AtWork1 only) reverses the driving coil toward AtWork1/away from the ejector; errs safe (a longer pulse only pushes toward AtWork1).
        public static bool SwivelBrakeHome = true;

        public static bool UnparkDisassembly = true;

        public static bool SerializeAssemblyDisassembly = false;

        public static bool MergeFeedRing = false;

        // Distinct from the handshake's 7; emitter (DisassemblyRecipe) and consumer (RecipeStateClassifier) share this one value.
        public const int MergeFeedRingBearingHomeState = 6;

        // A process's state_table slot carries its last CMD state (1/3/5/7), so the idle marker must be a value no recipe ever issues as a CMD state -- 0 is safe.
        public const int ProcessIdleSentinelState = 0;

        public static bool DataDrivenRecipes = false;

        // Each must sit above the component id space (ValidateProcessIdInvariant enforces it); the Assembly->Disassembly handshake rides AssemblyProcessId.
        public static int FeedStationProcessId    => RigCatalog.Current.ProcessIds.FeedStation;
        public static int AssemblyProcessId       => RigCatalog.Current.ProcessIds.Assembly;
        public static int DisassemblyProcessId    => RigCatalog.Current.ProcessIds.Disassembly;

        public static int RobotActuatorId         => RigCatalog.Current.RobotActuatorId;

        // Real rig DI sensors the twin doesn't model; kept OFF the M262 Feed ring, riding the M262->M580 cross-device segment so the report lands only in M580 state_table[id].
        public static (string Name, string Pin, int Id)[] M262SynthSensors =>
            RigCatalog.Current.SynthSensors
                .Select(s => (s.Name, s.Pin, s.Id))
                .ToArray();

        public static bool EnableRobotTaskTail => CodeGen.Translation.HandoffPlanner.DischargeActive;

        public static readonly string RecipeTestProcessName = "Assembly_Station";
        // Empty = no restriction (full recipe); populate with actuator names to park the rest.
        public static readonly string[] RecipeTestActuatorAllowlist = Array.Empty<string>();

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

        public string SyslayPathSim { get; set; } = string.Empty;
        public string SysresPathSim { get; set; } = string.Empty;

        public string IoBindingsPath { get; set; } = "Input/SMC_Rig_IO_Bindings.xlsx";

        // TM3 module slot/topology layout is fixed by physical rig wiring: copied verbatim; only the channel ParameterValue strings are overwritten from IoBindings.
        public string M262HardwareConfigBaselinePath { get; set; } = string.Empty;

        public string M262TargetIp { get; set; } = DeviceConfig.Current.M262.TargetIp;

        public string M262SubnetAddress { get; set; } = DeviceConfig.Current.M262.SubnetAddress;
        public string M262SubnetMask { get; set; } = DeviceConfig.Current.M262.SubnetMask;
        public string M262Gateway { get; set; } = DeviceConfig.Current.M262.Gateway;
        public string M262LogicalNetworkName { get; set; } = "DeviceNetwork_1";

        // EAE constraint: a device with no concrete IP is not listed in Deploy & Diagnostic, so this must be a real address, not a placeholder.
        public string M580TargetIp { get; set; } = DeviceConfig.Current.M580.TargetIp;

        // Must match Topology/BroadcastDomain_Default Network.json. M262 is intentionally left on NOCONF -- do not touch it.
        public string M580BroadcastDomainUuid { get; set; }
            = "2131fbdd-0a41-4e41-abfb-a14a5ca9218d";

        public string DefaultNetworkSubnetAddress { get; set; } = DeviceConfig.Current.DefaultNetwork.SubnetAddress;

        public string DefaultNetworkSubnetMask { get; set; } = DeviceConfig.Current.DefaultNetwork.SubnetMask;

        public string DefaultNetworkGateway { get; set; } = DeviceConfig.Current.DefaultNetwork.Gateway;

        // Must match Topology/BroadcastDomain_Default Network.json; same UUID on the M580 endpoint binding and the BroadcastDomain JSON so they cross-reference.
        public string DefaultNetworkUuid { get; set; }
            = "2131fbdd-0a41-4e41-abfb-a14a5ca9218d";

        // BX1 softdpac runtime IP (EAE deploys/logs in here); same Deploy & Diagnostic real-IP constraint as M580.
        public string BX1TargetIp { get; set; } = DeviceConfig.Current.Bx1.TargetIp;

        // HMIB1X panel host IP: setting this makes BX1 a REMOTE panel not a local Workstation (whose runtime EAE resolves to 127.0.0.1 -- the "cannot connect to BX1" error).
        public string BX1HostIp { get; set; } = DeviceConfig.Current.Bx1.HostIp;

        // EAE constraint: an FDT project copied verbatim from another solution can make the topology server throw a 500 on import.
        public bool EmitBx1EtherNetIpDevice { get; set; } = true;

        public bool DeployBx1IoBroker { get; set; } = true;

        // EAE-runtime unknown for the internalized (TRUE) path: whether a SYMLINKMULTIVAR with an ABSOLUTE cross-instance NAME (BX1_RES.CoverPNP_Vr.OutputToWork) resolves from INSIDE a composite type.
        public bool Bx1BridgeInsideComposite { get; set; } = false;

        // SAFETY: on start forces CoverPNP_Hr HOME (ToWork=0,ToHome=1) until the at-home sensor is TRUE so cover_hr can't auto-energise Work (swivel-collision). Run-time only: does NOT act on EAE Clean/STOP/fault. Homing while STOPPED needs the TM3BC coupler ToHome fallback (word 16#0002) set on its own web server (192.168.1.210).
        public bool Bx1CoverSafeStart { get; set; } = true;

        public string ResourceName { get; set; } = "M262_RES";

        // Per-PLC HCF templates: copied verbatim; only the DI/DO symbol bindings are rewritten from IoBindings.xlsx. Bus topology is fixed by rig wiring, never synthesised from Control.xml.
        public string IoFolderPath { get; set; } = string.Empty;

        public string M262HcfTemplatePath { get; set; } = string.Empty;

        public string M580HcfTemplatePath { get; set; } = string.Empty;

        public string BX1HcfTemplatePath { get; set; } = string.Empty;

        // CmdTargetName must be STRING[150] so long names (coverpnp_gripper) do not overflow.
        public bool UseRecipeStruct { get; set; } = true;

        public bool MqttPublishEnabled { get; set; } = true;

        // Host:port only; the URL scheme is forced from MqttSecureTls (never drifts). EAE 24.1 MQTT_CONNECTION is secure-by-default: plain mqtt:// needs the device "Insecure Application" override (else RC101); mqtts:// needs a TLS broker with certfile (else RC100).
        public string MqttBrokerUrl { get; set; } = TelemetrySettings.Current.BrokerUrl;

        // FALSE = insecure mqtt:// (needs the device "Insecure Application" override); TRUE = mqtts:// + TLS. The URL scheme is derived from this.
        public bool MqttSecureTls { get; set; } = TelemetrySettings.Current.SecureTls;

        public string MqttCaCert { get; set; } = TelemetrySettings.Current.CaCert;

        public int MqttValidateCert { get; set; } = TelemetrySettings.Current.ValidateCert;

        public string MqttClientId { get; set; } = TelemetrySettings.Current.ClientBx1;

        public string MqttClientM262 { get; set; } = TelemetrySettings.Current.ClientM262;

        public string MqttClientM580 { get; set; } = TelemetrySettings.Current.ClientM580;

        public string MqttConnectionName { get; set; } = TelemetrySettings.Current.ConnectionName;

        public bool UseTelemetryCat { get; set; } = TelemetrySettings.Current.UseTelemetryCat;

        public int MqttConnectionId { get; set; } = 1;

        public int MqttQueueDepth { get; set; } = 100;

        public int MqttQoS { get; set; } = 1;

        public bool MqttCleanSession { get; set; } = false;

        public bool MqttRetain { get; set; } = false;

        // EAE constraint: must reach the TIME port as a TIME literal (T#60000ms), not a bare INT (ERR_CAST_CONSTANT); an unset value defaults to T#0s and aborts the connect.
        public int MqttKeepAliveMs { get; set; } = 60000;

        // EAE constraint: an unset TIME port defaults to T#0s and aborts the connect before the first handshake completes.
        public int MqttConnectionTimeoutMs { get; set; } = 5000;

        public int MqttConnectionRetryCount { get; set; } = 999;

        public int MqttConnectionRetryTimeMs { get; set; } = 2000;

        public string MqttTopicRoot { get; set; } = "smc";

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
            ProcessCATTemplatePath = @"C:\Station1\IEC61499\Process1_Generic\Process1_Generic.fbt",
            RobotTemplatePath = @"C:\SMC_Rig\IEC61499\Robot_Task_CAT\Robot_Task_CAT.fbt",
            RobotBasicTemplatePath = @"C:\SMC_Rig\IEC61499\Robot_Task_Core.fbt",
            SyslayPath = @"C:\Station1\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000001\00000000-0000-0000-0000-000000000000.syslay",
            SysresPath = @"C:\Station1\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000002\00000000-0000-0000-0000-000000000000.sysres",
            SyslayPath2 = @"C:\Demonstrator\Demonstrator\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000001\00000000-0000-0000-0000-000000000000.syslay",
            SysresPath2 = @"C:\Demonstrator\Demonstrator\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000002\00000000-0000-0000-0000-000000000000.sysres",
            SyslayPathSim = @"C:\DemonstratorSim\Demonstrator\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000001\00000000-0000-0000-0000-000000000000.syslay",
            SysresPathSim = @"C:\DemonstratorSim\Demonstrator\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000002\00000000-0000-0000-0000-000000000000.sysres",
            IoBindingsPath = @"Input\SMC_Rig_IO_Bindings.xlsx",
            M262HardwareConfigBaselinePath = string.Empty,
            IoFolderPath = @"C:\VueOneMapper\IO",
            M262HcfTemplatePath = @"C:\VueOneMapper\IO\M262IO.hcf",
            M580HcfTemplatePath = @"C:\VueOneMapper\IO\M580IO.hcf",
            BX1HcfTemplatePath = @"C:\VueOneMapper\IO\BX1IO.ethernetip.hcf",
            M262TargetIp = DeviceConfig.Current.M262.TargetIp,
            M262SubnetAddress = DeviceConfig.Current.M262.SubnetAddress,
            M262SubnetMask = DeviceConfig.Current.M262.SubnetMask,
            M262Gateway = DeviceConfig.Current.M262.Gateway,
            M262LogicalNetworkName = "DeviceNetwork_1",
            ResourceName = "M262_RES",
        };

        private static void Save(string path, MapperConfig config)
        {
            var json = JsonSerializer.Serialize(config,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
    }
}
