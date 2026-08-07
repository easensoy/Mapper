using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CodeGen.Configuration
{
    public class MapperConfig
    {
        private const string ConfigFileName = "mapper_config.json";

        // RETIRED: nothing branches on this. It survives only as a binary contract — the prebuilt
        // VueOneMapperHiddenRunner writes it, and removing the field would fault that consumer at load.
        // Do not read it, do not migrate it into a config: it carries no meaning.
        public static bool SimulatorRecipeMode = false;

        // Engine END->0 loop-back (ProcessRuntimeTemplatePatcher).
        public static bool EnableCyclicRestart = true;

        public static int RobotActuatorId         => RigCatalog.Current.RobotActuatorId;

        // Real rig DI sensors the twin doesn't model; kept OFF the M262 Feed ring, riding the M262->M580 cross-device segment so the report lands only in M580 state_table[id].
        public static (string Name, string Pin, int Id)[] M262SynthSensors =>
            RigCatalog.Current.SynthSensors
                .Select(s => (s.Name, s.Pin, s.Id))
                .ToArray();

        public string MappingRulesPath { get; set; } = string.Empty;
        public string TemplateLibraryPath { get; set; } = string.Empty;
        public string SyslayPath { get; set; } = string.Empty;
        public string SysresPath { get; set; } = string.Empty;
        public string SyslayPath2 { get; set; } = string.Empty;
        public string SysresPath2 { get; set; } = string.Empty;

        public string IoBindingsPath { get; set; } = "Input/SMC_Rig_IO_Bindings.xlsx";

        public string M262TargetIp { get; set; } = DeviceConfig.Current.M262.TargetIp;

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

        // Revolution Pi (Soft_dPAC) Feed station. From Config/device.yml, which documents the roles:
        // RevPiTargetIp = the Soft dPAC CONTAINER on the EAE-managed macvlan (runtime; EAE deploys here).
        // RevPiHostIp   = the RevPi Linux HOST NIC (Soft dPAC Manager on 8080). The two must differ —
        // RevPiAddressValidator fails generation if they are equal or collide with another endpoint.
        public string RevPiTargetIp { get; set; } = DeviceConfig.Current.RevPi.TargetIp;
        public string RevPiHostIp { get; set; } = DeviceConfig.Current.RevPi.HostIp;

        public string HmiHostIp { get; set; } = DeviceConfig.Current.Hmi.HostIp;
        public string HmiInternalRuntimeIp { get; set; } = DeviceConfig.Current.Hmi.InternalRuntimeIp;
        public int HmiLogicalPort { get; set; } = DeviceConfig.Current.Hmi.LogicalPort;
        public int HmiSecurePort { get; set; } = DeviceConfig.Current.Hmi.SecurePort;

        // The generated HMI is a MONITORING panel and must not present a control the controller cannot
        // honour: the Station/Area STOP button fires CycleType=0, but ProcessRuntime_Generic_v1's ECC
        // has no transition that reads Mode or CycleType (INIT->IDLE1 and END->ADVANCE are
        // unconditional), so STOP cannot stop recipe execution. Setting this false does NOT enable
        // command screens -- generation fails until an approved command contract exists in the control
        // logic. A real operational Stop needs a process-engine change; the safety stop stays in the
        // certified hardware safety system either way.
        public bool HmiReadOnly { get; set; } = true;

        // EAE constraint: an FDT project copied verbatim from another solution can make the topology server throw a 500 on import.
        public bool EmitBx1EtherNetIpDevice { get; set; } = true;

        public bool DeployBx1IoBroker { get; set; } = true;

        // TRUE: the cover I/O bridge (BX1IO_Sense_*/BX1IO_Coil_*/BX1_IO_Cycle) lives INSIDE the
        // PLC_RW_BX1 composite, so BX1_RES instantiates only BX1_IO -- the same shape M262 has with
        // PLC_RW_M262. Same sensors, same coils, same 50 ms scan; only the nesting changes.
        // ⚠ Rig-verify: a SYMLINKMULTIVAR with an ABSOLUTE cross-instance NAME
        // (BX1_RES.CoverPNP_Vr.OutputToWork) must resolve from INSIDE a composite type. Proven by
        // spike (EIP_Output_Word 16#0004); set false to restore the external bridge in one rebuild.
        public bool Bx1BridgeInsideComposite { get; set; } = true;

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

        // Feed controller ClientIdentifier when the RevPi hosts the Feed station instead of M262.
        public string MqttClientRevPi { get; set; } = TelemetrySettings.Current.ClientRevPi;

        public string MqttClientM580 { get; set; } = TelemetrySettings.Current.ClientM580;

        public string MqttConnectionName { get; set; } = TelemetrySettings.Current.ConnectionName;

        public bool UseTelemetryCat { get; set; } = TelemetrySettings.Current.UseTelemetryCat;

        public int MqttQoS { get; set; } = 1;

        public bool MqttRetain { get; set; } = false;

        public string MqttTopicRoot { get; set; } = "smc";

        public string ActiveSyslayPath =>
            !string.IsNullOrEmpty(SyslayPath2) ? SyslayPath2 : SyslayPath;

        public string ActiveSysresPath =>
            !string.IsNullOrEmpty(SysresPath2) ? SysresPath2 : SysresPath;

        // A generation run needs to settle a few values (UseRecipeStruct) without writing back into the
        // caller's instance. MapperUI holds one cached config for the life of the process, so mutating it
        // in place made each run start from the last run's leftovers.
        public MapperConfig Clone() =>
            JsonSerializer.Deserialize<MapperConfig>(JsonSerializer.Serialize(this))
                ?? throw new InvalidOperationException("MapperConfig could not be cloned.");

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
            MappingRulesPath = @"Input\VueOne_IEC61499_Mapping.xlsx",
            TemplateLibraryPath = @"C:\VueOneMapper\Template Library",
            SyslayPath = @"C:\Station1\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000001\00000000-0000-0000-0000-000000000000.syslay",
            SysresPath = @"C:\Station1\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000002\00000000-0000-0000-0000-000000000000.sysres",
            SyslayPath2 = @"C:\Demonstrator\Demonstrator\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000001\00000000-0000-0000-0000-000000000000.syslay",
            SysresPath2 = @"C:\Demonstrator\Demonstrator\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000002\00000000-0000-0000-0000-000000000000.sysres",
            IoBindingsPath = @"Input\SMC_Rig_IO_Bindings.xlsx",
            IoFolderPath = @"C:\VueOneMapper\IO",
            M262HcfTemplatePath = @"C:\VueOneMapper\IO\M262IO.hcf",
            M580HcfTemplatePath = @"C:\VueOneMapper\IO\M580IO.hcf",
            BX1HcfTemplatePath = @"C:\VueOneMapper\IO\BX1IO.ethernetip.hcf",
            M262TargetIp = DeviceConfig.Current.M262.TargetIp,
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
