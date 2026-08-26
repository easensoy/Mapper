using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.IO;

namespace CodeGen.Configuration
{
    public class MapperConfig
    {
        private const string ConfigFileName = "mapper_config.json";

        // Real rig DI sensors the twin does not model; kept OFF the M262 Feed ring so the report lands only in M580 state_table[id]. The slot is the stableSlot on the sensor's own layout row.
        public static (string Name, int Id)[] M262SynthSensors =>
            RigCatalog.Current.SynthSensors
                .Select(s => (s.Name, Id: Configuration.LayoutCatalog.Load().StableSlotOf(s.Name)))
                .Where(s => s.Id >= 0)
                .ToArray();

        public string RequireTemplateLibraryPath() => Require(TemplateLibraryPath, nameof(TemplateLibraryPath));
        public string RequireIoFolderPath() => Require(IoFolderPath, nameof(IoFolderPath));

        private static string Require(string value, string key) =>
            !string.IsNullOrWhiteSpace(value) ? value
            : throw new InvalidOperationException(
                $"[Config] mapper_config.json declares no {key}, so the generator has no library to read " +
                "from. Set it beside the runner rather than relying on a path that only exists on one machine.");

        public string MappingRulesPath { get; set; } = string.Empty;
        public string TemplateLibraryPath { get; set; } = string.Empty;
        public string SyslayPath2 { get; set; } = string.Empty;
        public string SysresPath2 { get; set; } = string.Empty;

        public string IoBindingsPath { get; set; } = "Input/SMC_Rig_IO_Bindings.xlsx";

        public string M262TargetIp { get; set; } = DeviceConfig.Current.M262.TargetIp;

        public string M262LogicalNetworkName { get; set; } = "DeviceNetwork_1";

        // EAE constraint: a device with no concrete IP is not listed in Deploy & Diagnostic, so this must be a real address, not a placeholder.
        public string M580TargetIp { get; set; } = DeviceConfig.Current.M580.TargetIp;

        // The M580 endpoint binding and the BroadcastDomain JSON cross-reference one uuid, so it is
        // written once. Must match Topology/BroadcastDomain_Default Network.json. M262 is intentionally
        // left on NOCONF -- do not touch it.
        const string DefaultNetworkDomainUuid = "2131fbdd-0a41-4e41-abfb-a14a5ca9218d";

        public string M580BroadcastDomainUuid { get; set; } = DefaultNetworkDomainUuid;

        public string DefaultNetworkSubnetAddress { get; set; } = DeviceConfig.Current.DefaultNetwork.SubnetAddress;

        public string DefaultNetworkSubnetMask { get; set; } = DeviceConfig.Current.DefaultNetwork.SubnetMask;

        public string DefaultNetworkGateway { get; set; } = DeviceConfig.Current.DefaultNetwork.Gateway;

        public string DefaultNetworkUuid { get; set; } = DefaultNetworkDomainUuid;

        // BX1 softdpac runtime IP (EAE deploys/logs in here); same Deploy & Diagnostic real-IP constraint as M580.
        public string BX1TargetIp { get; set; } = DeviceConfig.Current.Bx1.TargetIp;

        // HMIB1X panel host IP: setting this makes BX1 a REMOTE panel not a local Workstation (whose runtime EAE resolves to 127.0.0.1 -- the "cannot connect to BX1" error).
        public string BX1HostIp { get; set; } = DeviceConfig.Current.Bx1.HostIp;

        // TargetIp = the Soft dPAC CONTAINER (EAE deploys here); HostIp = the RevPi Linux HOST NIC. The two
        // must differ: RevPiAddressValidator fails generation if they are equal or collide with an endpoint.
        public string RevPiTargetIp { get; set; } = DeviceConfig.Current.RevPi.TargetIp;
        public string RevPiHostIp { get; set; } = DeviceConfig.Current.RevPi.HostIp;

        // Retained because the prebuilt VueOne runner links this property; device.yml's targets entry is
        // what generation actually reads, so nothing here decides a resource name any more.
        public string ResourceName { get; set; } = string.Empty;

        // Per-PLC HCF templates: copied verbatim; only the DI/DO symbol bindings are rewritten from IoBindings.xlsx. Bus topology is fixed by rig wiring, never synthesised from Control.xml.
        public string IoFolderPath { get; set; } = string.Empty;

        public string M262HcfTemplatePath { get; set; } = string.Empty;

        public string M580HcfTemplatePath { get; set; } = string.Empty;

        public string BX1HcfTemplatePath { get; set; } = string.Empty;

        // The authored hardware configs this machine holds, by file name. WHICH file a target uses is
        // device.yml's answer; where it lives is this local config's, so the two meet by name and
        // nothing has to ask for a controller by name to find one.
        public System.Collections.Generic.IReadOnlyDictionary<string, string> HcfTemplatesByFileName =>
            new[] { M262HcfTemplatePath, M580HcfTemplatePath, BX1HcfTemplatePath }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToDictionary(p => System.IO.Path.GetFileName(p) ?? string.Empty, p => p,
                              StringComparer.OrdinalIgnoreCase);

        // Every MQTT setting is READ-ONLY here and owned by Config/telemetry.yml. They stay on MapperConfig
        // because the prebuilt VueOne runner links them, but having no setter is what stops a stale
        // mapper_config.json shadowing the broker or client identity. mqtt:// vs mqtts:// is derived from
        // SecureTls, never spelled in the URL; EAE 24.1 MQTT_CONNECTION is secure-by-default.
        public bool MqttPublishEnabled => TelemetrySettings.Current.PublishEnabled;
        public string MqttBrokerUrl => TelemetrySettings.Current.BrokerUrl;
        public bool MqttSecureTls => TelemetrySettings.Current.SecureTls;
        public string MqttCaCert => TelemetrySettings.Current.CaCert;
        public int MqttValidateCert => TelemetrySettings.Current.ValidateCert;
        public string MqttConnectionName => TelemetrySettings.Current.ConnectionName;
        public bool UseTelemetryCat => TelemetrySettings.Current.UseTelemetryCat;
        public int MqttQoS => TelemetrySettings.Current.Qos;
        public bool MqttRetain => TelemetrySettings.Current.Retain;
        public string MqttTopicRoot => TelemetrySettings.Current.TopicRoot;

        // The configured artefact roots. Generation refuses to run without them, so there is no
        // second pair to fall back to.
        public string ActiveSyslayPath => SyslayPath2;

        public string ActiveSysresPath => SysresPath2;

        // A run settles a few values without writing back into the caller's instance: MapperUI holds one
        // cached config for the life of the process, so mutating it leaks into the next run.
        public MapperConfig Clone() =>
            JsonSerializer.Deserialize<MapperConfig>(JsonSerializer.Serialize(this))
                ?? throw new InvalidOperationException("MapperConfig could not be cloned.");

        // An explicit configuration root: set this and nothing else is consulted.
        public static string? ConfigurationRoot { get; set; }

        // Where an authored mapper_config.json may sit, most specific first. The working directory is
        // included only because the prebuilt VueOne runner sets it; a disagreement is fatal, not ordered.
        private static IEnumerable<string> CandidateRoots()
        {
            if (!string.IsNullOrWhiteSpace(ConfigurationRoot)) { yield return ConfigurationRoot!; yield break; }
            yield return AppContext.BaseDirectory;
            yield return Environment.CurrentDirectory;
        }

        public static MapperConfig Load()
        {
            var found = new List<(string Path, string Json)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in CandidateRoots())
            {
                string path;
                try { path = Path.GetFullPath(Path.Combine(root, ConfigFileName)); }
                catch { continue; }
                if (!seen.Add(path) || !File.Exists(path)) continue;
                found.Add((path, File.ReadAllText(path)));
            }

            if (found.Count == 0)
                throw new InvalidOperationException(
                    $"No authored {ConfigFileName} was found in " +
                    string.Join(" or ", CandidateRoots()) +
                    $". It is generated into every build output from Config/{ConfigFileName}; a missing one " +
                    "means the build did not run or the deployment is incomplete. Generation stops rather " +
                    "than inventing defaults, because the defaults point at the live Demonstrator tree.");

            // Two copies that disagree would make the effective config depend on the launch directory.
            var distinct = found.Select(f => Normalise(f.Json)).Distinct(StringComparer.Ordinal).Count();
            if (distinct > 1)
                throw new InvalidOperationException(
                    $"Conflicting {ConfigFileName} copies: " + string.Join(" and ", found.Select(f => f.Path)) +
                    ". They differ, so the effective configuration would depend on the launch directory. " +
                    "Delete every copy but the one generated from Config/" + ConfigFileName + ".");

            return JsonSerializer.Deserialize<MapperConfig>(found[0].Json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException($"Failed to deserialise config from '{found[0].Path}'");
        }

        // Compare content, not bytes: line endings and key order are formatting, not configuration.
        private static string Normalise(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return string.Join("", doc.RootElement.EnumerateObject()
                .Select(p => p.Name + "=" + p.Value.ToString())
                .OrderBy(x => x, StringComparer.Ordinal));
        }
    }
}
