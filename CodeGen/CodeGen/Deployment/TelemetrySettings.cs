using System;
using System.Collections.Generic;
using System.Linq;
namespace CodeGen.Configuration
{
    // Telemetry/MQTT settings from Config/telemetry.yml, which is their only source. No value is
    // restated here: a C# default is a second source of truth that silently wins when a key is
    // dropped, which is exactly how the configuration and the code drift apart.
    public sealed class TelemetrySettings
    {
        public bool UseTelemetryCat { get; set; }
        public string BrokerUrl { get; set; } = string.Empty;
        public bool SecureTls { get; set; }
        public int ValidateCert { get; set; }
        public string CaCert { get; set; } = string.Empty;
        public string ConnectionName { get; set; } = string.Empty;

        // Publish policy for the embedded MQTT_PUBLISH the CATs carry.
        public bool PublishEnabled { get; set; } = true;
        public int Qos { get; set; } = 1;
        public bool Retain { get; set; }
        public string TopicRoot { get; set; } = string.Empty;

        private static readonly YamlConfigFile<TelemetrySettings> _file = new("Config", "telemetry.yml");

        public List<MqttConnectionDeclaration> Connections { get; set; } = new();

        public MqttConnectionDeclaration For(CodeGen.Translation.PlcAssignment plc) =>
            Connections.FirstOrDefault(c => c.Plc == plc)
            ?? throw new InvalidOperationException(
                $"[MQTT] telemetry.yml declares no connection for {plc}, so its resource would host " +
                "publishers with nothing to publish through.");

        public static TelemetrySettings Current => _file.Load();
    }

    // One resource's MQTT connection: what it is called, who it identifies as, and what brings it up.
    public sealed class MqttConnectionDeclaration
    {
        public CodeGen.Translation.PlcAssignment Plc { get; set; }
        public string Instance { get; set; } = string.Empty;
        public string RawInstance { get; set; } = string.Empty;
        public string Client { get; set; } = string.Empty;
        // The infrastructure role whose INITO brings it up; "ringHead" = the report ring's own head.
        public string InitFrom { get; set; } = string.Empty;

        public string NameFor(bool telemetryComposite) => telemetryComposite ? Instance : RawInstance;
    }
}
