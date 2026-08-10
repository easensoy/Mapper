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
        public string ClientBx1 { get; set; } = string.Empty;
        public string ClientM262 { get; set; } = string.Empty;
        public string ClientM580 { get; set; } = string.Empty;
        public string ClientRevPi { get; set; } = string.Empty;

        private static readonly YamlConfigFile<TelemetrySettings> _file = new("Config", "telemetry.yml");

        public static TelemetrySettings Current => _file.Load();
    }
}
