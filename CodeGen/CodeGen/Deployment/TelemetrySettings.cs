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

        // For a caller that already knows which resource it is wiring; the shared canvas walks the
        // declaration instead, so nothing there has to name a target.
        public MqttConnectionDeclaration For(CodeGen.Translation.PlcAssignment plc) =>
            Connections.FirstOrDefault(c => c.Plc == plc)
            ?? throw new InvalidOperationException(
                $"[MQTT] telemetry.yml declares no connection for {plc}, so its resource would host " +
                "publishers with nothing to publish through.");

        public static TelemetrySettings Current => _file.Load();
    }

    // One resource's MQTT connection: what it is called, who it identifies as, where it is drawn and
    // what starts it. Every target-specific fact about a connection is here, so nothing that emits one
    // has to know which controller it belongs to.
    public sealed class MqttConnectionDeclaration
    {
        // Where the FB is drawn.
        public const string AtRosterRow = "roster";
        public const string AtBandHead = "bandHead";

        // The resource role whose INITO starts it.
        public const string ByArea = "area";
        public const string ByStation = "station";
        public const string ByRingHead = "ringHead";

        public CodeGen.Translation.PlcAssignment Plc { get; set; }
        public string Instance { get; set; } = string.Empty;
        public string RawInstance { get; set; } = string.Empty;
        public string Client { get; set; } = string.Empty;
        public string DrawnAt { get; set; } = AtBandHead;
        // Empty = this resource's own wiring already brings it up, so the shared canvas adds nothing.
        public string BroughtUpBy { get; set; } = string.Empty;

        public string NameFor(bool telemetryComposite) => telemetryComposite ? Instance : RawInstance;

        public bool IsDrawnAtRosterRow =>
            string.Equals(DrawnAt, AtRosterRow, StringComparison.OrdinalIgnoreCase);
    }
}
