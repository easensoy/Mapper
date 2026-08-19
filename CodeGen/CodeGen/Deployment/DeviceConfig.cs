namespace CodeGen.Configuration
{
    public sealed class DeviceConfig
    {
        public DeviceNet M262 { get; set; } = new();
        public DeviceNet M580 { get; set; } = new();
        public DeviceNet Bx1 { get; set; } = new();
        // TargetIp = Soft dPAC CONTAINER (macvlan child, runtime + EAE deploy target).
        // HostIp   = RevPi Linux/Docker HOST NIC (Soft dPAC Manager on 8080). Must differ from TargetIp.
        public DeviceNet RevPi { get; set; } = new();
        public DeviceNet DefaultNetwork { get; set; } = new();

        // The BX1 coupler word, as the rig wired it. See Config/device.yml.
        public Bx1IoProfile Bx1Io { get; set; } = new();

        private static readonly YamlConfigFile<DeviceConfig> _file = new("Config", "device.yml");

        public static DeviceConfig Current => _file.Load();
    }

    public sealed class DeviceNet
    {
        public string TargetIp { get; set; } = string.Empty;

        // Components this target must host whenever it hosts anything, because its own I/O hardware
        // is the only thing that can read them.
        public System.Collections.Generic.List<string> AlwaysHosts { get; set; } = new();
        public string HostIp { get; set; } = string.Empty;
        public string CouplerIp { get; set; } = string.Empty;
        public string SubnetAddress { get; set; } = string.Empty;
        public string SubnetMask { get; set; } = string.Empty;
        public string Gateway { get; set; } = string.Empty;
    }


    // One signal on the coupler word: the name the broker composite exposes and the bit it occupies.
    public sealed class Bx1Signal
    {
        public string Signal { get; set; } = string.Empty;
        public int Bit { get; set; }
    }

    // One cover actuator's physical I/O.
    public sealed class Bx1Cover
    {
        public string Component { get; set; } = string.Empty;
        public string Event { get; set; } = string.Empty;
        public Bx1Signal? SensorFromHome { get; set; }
        public Bx1Signal? SensorFromWork { get; set; }
        public Bx1Signal? CoilToWork { get; set; }
        public Bx1Signal? CoilToHome { get; set; }
    }

    public sealed class Bx1IoProfile
    {
        public System.Collections.Generic.List<Bx1Cover> Covers { get; set; } = new();
        public string SafeStartComponent { get; set; } = string.Empty;
    }
}
