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

        // Ports the broker serves without TLS. mqtts:// against one of these is refused.
        public List<int> PlainMqttPorts { get; set; } = new();
        public int ValidateCert { get; set; }
        public string CaCert { get; set; } = string.Empty;
        public string ConnectionName { get; set; } = string.Empty;

        // Publish policy for the embedded MQTT_PUBLISH the CATs carry.
        public bool PublishEnabled { get; set; }
        public int Qos { get; set; }
        public bool Retain { get; set; }
        public string TopicRoot { get; set; } = string.Empty;

        private static readonly YamlConfigFile<TelemetrySettings> _file =
            new("Config", "telemetry.yml") { OnLoaded = Validate };

        // An undeclared plainMqttPorts would not read as "the broker serves TLS everywhere" - it
        // would read as "no port is known to be plain", which silently disarms the one check that
        // catches an mqtts:// URL pointed at a plain listener (ReturnCode 100 at runtime).
        static void Validate(TelemetrySettings t)
        {
            if (t.PlainMqttPorts.Count == 0)
                throw new InvalidOperationException(
                    "[Telemetry] telemetry.yml declares no plainMqttPorts, so nothing can tell a " +
                    "TLS URL pointed at a plain listener from a correct one. Declare the ports the " +
                    "broker serves without TLS.");
            foreach (var g in t.PlainMqttPorts.GroupBy(x => x).Where(g => g.Count() > 1))
                throw new InvalidOperationException(
                    $"[Telemetry] plainMqttPorts lists {g.Key} more than once.");
        }

        public List<MqttConnectionDeclaration> Connections { get; set; } = new();

        // For a caller that already knows which resource it is wiring; the shared canvas walks the
        // declaration instead, so nothing there has to name a target.
        public MqttConnectionDeclaration For(CodeGen.Translation.PlcAssignment plc) =>
            Connections.FirstOrDefault(c => c.Plc == plc)
            ?? throw new InvalidOperationException(
                $"[MQTT] telemetry.yml declares no connection for {plc}, so its resource would host " +
                "publishers with nothing to publish through.");

        public static TelemetrySettings Current => _file.Load();

        /// The same declaration read from a run's OWN profile bundle. A root of null is the
        /// bundle shipped beside CodeGen.dll, which is what a normal run reads.
        public static TelemetrySettings LoadFrom(string? root) => _file.Load(root);
    }

    // Where a connection's FB is drawn. Typed, so a misspelling fails the load rather than reading as
    // one of these by accident and silently moving the FB.
    public enum ConnectionPlacement { BandHead, Roster }

    // The resource role whose INITO starts it. None = this resource's own chain does, which is a real
    // choice rather than the absence of one, so it is written down.
    public enum ConnectionStarter { None, Area, Station, RingHead }

    // One resource's MQTT connection: what it is called, who it identifies as, where it is drawn and
    // what starts it. Every target-specific fact about a connection is here, so nothing that emits one
    // has to know which controller it belongs to.
    public sealed class MqttConnectionDeclaration
    {
        public CodeGen.Translation.PlcAssignment Plc { get; set; }
        public string Instance { get; set; } = string.Empty;
        public string RawInstance { get; set; } = string.Empty;
        public string Client { get; set; } = string.Empty;
        public ConnectionPlacement DrawnAt { get; set; } = ConnectionPlacement.BandHead;
        public ConnectionStarter BroughtUpBy { get; set; } = ConnectionStarter.None;

        public string NameFor(bool telemetryComposite) => telemetryComposite ? Instance : RawInstance;

        public bool IsDrawnAtRosterRow => DrawnAt == ConnectionPlacement.Roster;
    }
}
