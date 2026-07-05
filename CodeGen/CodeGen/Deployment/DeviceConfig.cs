namespace CodeGen.Configuration
{
    public sealed class DeviceConfig
    {
        public DeviceNet M262 { get; set; } = new();
        public DeviceNet M580 { get; set; } = new();
        public DeviceNet Bx1 { get; set; } = new();
        public DeviceNet RevPi { get; set; } = new() { TargetIp = "192.168.1.6", HostIp = "192.168.1.2" };
        public DeviceNet DefaultNetwork { get; set; } = new();

        private static readonly YamlConfigFile<DeviceConfig> _file = new("Config", "device.yml");

        public static DeviceConfig Current => _file.Load();
    }

    public sealed class DeviceNet
    {
        public string TargetIp { get; set; } = string.Empty;
        public string HostIp { get; set; } = string.Empty;
        public string SubnetAddress { get; set; } = string.Empty;
        public string SubnetMask { get; set; } = string.Empty;
        public string Gateway { get; set; } = string.Empty;
    }
}
