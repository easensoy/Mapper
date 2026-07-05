namespace CodeGen.Configuration
{
    // Telemetry/MQTT settings from Config/telemetry.yml. The defaults here mirror the prior values, so
    // a missing/empty YAML is behaviour-identical.
    public sealed class TelemetrySettings
    {
        public bool UseTelemetryCat { get; set; } = true;
        public string BrokerUrl { get; set; } = "mqtt://192.168.1.50:1883";
        public bool SecureTls { get; set; } = false;
        public string CaCert { get; set; } = "";
        public int ValidateCert { get; set; } = 0;
        public string ConnectionName { get; set; } = "SMC";
        public string ClientBx1 { get; set; } = "SMC_BX1";
        public string ClientM262 { get; set; } = "SMC_M262";
        public string ClientM580 { get; set; } = "SMC_M580";
        public string ClientRevPi { get; set; } = "SMC_RevPi";

        private static readonly YamlConfigFile<TelemetrySettings> _file = new("Config", "telemetry.yml");

        public static TelemetrySettings Current => _file.Load();
    }
}
