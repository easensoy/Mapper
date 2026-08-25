namespace CodeGen.Configuration
{
    public sealed class DeviceConfig
    {
        public DeviceNet M262 { get; set; } = new();
        public DeviceNet M580 { get; set; } = new();
        public DeviceNet Bx1 { get; set; } = new();
        // TargetIp = Soft dPAC container (EAE deploy target); HostIp = RevPi host NIC. Must differ.
        public DeviceNet RevPi { get; set; } = new();
        public DeviceNet DefaultNetwork { get; set; } = new();

        // The BX1 coupler word, as the rig wired it. See Config/device.yml.
        public Bx1IoProfile Bx1Io { get; set; } = new();

        // EAE resource and device identity per target. Facts only; backend behaviour stays in C#.
        public System.Collections.Generic.List<TargetIdentity> Targets { get; set; } = new();

        // The system FBs every resource boots with, in emission order. Shape once; ids per target.
        public System.Collections.Generic.List<BootFbDeclaration> BootSequence { get; set; } = new();

        // The runtime bring-up wires every resource emits, in emission order. Endpoints name boot roles.
        public System.Collections.Generic.List<BringUpWire> BringUp { get; set; } = new();

        private static readonly YamlConfigFile<DeviceConfig> _file = new("Config", "device.yml");

        public static DeviceConfig Current => _file.Load();
    }

    public sealed class DeviceNet
    {
        public string TargetIp { get; set; } = string.Empty;

        // Components only this target's own I/O hardware can read, so it must host them.
        public System.Collections.Generic.List<string> AlwaysHosts { get; set; } = new();
        public string HostIp { get; set; } = string.Empty;
        public string CouplerIp { get; set; } = string.Empty;
        public string SubnetAddress { get; set; } = string.Empty;
        public string SubnetMask { get; set; } = string.Empty;
        public string Gateway { get; set; } = string.Empty;
    }


    public sealed class Bx1Signal
    {
        public string Signal { get; set; } = string.Empty;
        public int Bit { get; set; }
    }

    public sealed class Bx1Cover
    {
        public string Component { get; set; } = string.Empty;
        public string Event { get; set; } = string.Empty;
        public Bx1Signal? SensorFromHome { get; set; }
        public Bx1Signal? SensorFromWork { get; set; }
        public Bx1Signal? CoilToWork { get; set; }
        public Bx1Signal? CoilToHome { get; set; }
    }

    public sealed class TargetIdentity
    {
        public CodeGen.Translation.PlcAssignment Plc { get; set; }
        public string ResourceName { get; set; } = string.Empty;
        public string DeviceType { get; set; } = string.Empty;
        // Null where the Type already identifies the device on its own.
        public string? DeviceName { get; set; }
        public string HcfTemplate { get; set; } = string.Empty;

        // Each flag selects a path in a device emitter, so declare a target only once its emitter
        // exists; see TargetRegistry.
        public bool HostsFeedStation { get; set; }
        public bool DeviceLocalCanvas { get; set; }
        public bool ReceivesRelocatedComponents { get; set; }
        public bool OpensCoverSeam { get; set; }
        public bool CarriesDetouredChain { get; set; }

        // One frozen EAE instance id per bootSequence role, in that order.
        public System.Collections.Generic.List<TargetBootFb> BootFbs { get; set; } = new();
    }

    // One boot FB's SHAPE: what it is on every target. Its identity is per target, in TargetBootFb.
    public sealed class BootFbDeclaration
    {
        public string Role { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Namespace { get; set; } = string.Empty;
        // The layout.yml bootFbs row that gives this FB its canvas position.
        public string LayoutKey { get; set; } = string.Empty;
        // A list, not a map: parameters are emitted as child elements and their order is artefact bytes.
        public System.Collections.Generic.List<BootFbParameter> Parameters { get; set; } = new();
    }

    public sealed class BootFbParameter
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public sealed class TargetBootFb
    {
        public string Role { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
    }

    // One bring-up connection. Both endpoints are '<role>.<PORT>': a bootSequence role, or the START
    // pseudo-role that is the resource's own entry rather than an FB the Mapper emits.
    public sealed class BringUpWire
    {
        public const string ResourceEntry = "START";

        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;

        public static string? RoleOf(string endpoint)
        {
            int dot = (endpoint ?? string.Empty).IndexOf('.');
            return dot <= 0 || dot == endpoint!.Length - 1 ? null : endpoint.Substring(0, dot);
        }
    }

    public sealed class Bx1IoProfile
    {
        public System.Collections.Generic.List<Bx1Cover> Covers { get; set; } = new();
        public string SafeStartComponent { get; set; } = string.Empty;
    }
}
