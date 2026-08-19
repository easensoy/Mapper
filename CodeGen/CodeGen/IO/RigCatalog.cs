using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Configuration
{
    public sealed class RigCatalog
    {
        // Control.xml process name -> its state_table slot. Allocation, so it is data: a twin that renames
        // or adds a process needs a row here, not a branch in the planner.
        public Dictionary<string, int> ProcessSlots { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
        public int RobotActuatorId { get; set; }

        // Boundary of the positional component id range; reserved process/task-arm slots sit above it.
        public int ComponentIdCeiling { get; set; } = 16;
        public List<SynthSensor> SynthSensors { get; set; } = new();
        public List<string> CrossRingSegment { get; set; } = new();
        public List<DischargeChannel> DischargeChannels { get; set; } = new();

        // Part-presence gates the Assembly recipe inserts before each pick (block -> sensor + the
        // runtime state that means "present"; active-low sensors = 0, active-high = 1). The top-cover
        // sensor's state_table slot is NOT stored here -- StateTableAllocation computes it per ring
        // topology, so the cover interlock is model-independent.
        public List<SensorInterlock> SensorInterlocks { get; set; } = new();

        public List<FeedbackMode> FeedbackModes { get; set; } = new();

        public List<ChannelBinding> M580Channels { get; set; } = new();
        public SwivelChannelSets SwivelChannels { get; set; } = new();

        public SemanticRoles Roles { get; set; } = new();

        // How an actuator confirms arrival, when the rig contradicts what the twin implies.
        public FeedbackMode? FeedbackFor(string? component) =>
            FeedbackModes.FirstOrDefault(f =>
                string.Equals(f.Component, component, System.StringComparison.OrdinalIgnoreCase));

        public static RigCatalog Current => RigCatalogLoader.Catalog;

    }

    // Instances filling the semantic roles the twin cannot express. Named here so no compiler branch
    // spells a plant component.
    public sealed class SemanticRoles
    {
        public string TaskArm { get; set; } = string.Empty;
        public string MaterialSensor { get; set; } = string.Empty;
        public List<string> TopCoverSensor { get; set; } = new();

        public bool Is(string? role, string? name) =>
            !string.IsNullOrEmpty(role) && string.Equals(role, (name ?? string.Empty).Trim(),
                System.StringComparison.OrdinalIgnoreCase);

        public bool IsTopCover(string? name) =>
            TopCoverSensor.Any(w => string.Equals(w, (name ?? string.Empty).Trim(),
                System.StringComparison.OrdinalIgnoreCase));
    }

    // One authored .hcf channel and what it binds to. The channel name is the rig's own wiring label;
    // the port is the CAT interface contract. An empty port means this CAT has no counterpart for the
    // channel, so it is blanked rather than left dangling.
    public sealed class ChannelBinding
    {
        public string Channel { get; set; } = string.Empty;
        public string Component { get; set; } = string.Empty;
        public string Port { get; set; } = string.Empty;
    }

    // The swivel's channels per CAT shape; which set applies is decided by the twin's state graph.
    public sealed class SwivelChannelSets
    {
        public List<ChannelBinding> CentreHome { get; set; } = new();
        public List<ChannelBinding> TwoPosition { get; set; } = new();
    }

    // An actuator whose arrival the rig cannot sense, so the CAT's motion timer acknowledges instead.
    public sealed class FeedbackMode
    {
        public string Component { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public int AckMs { get; set; }

        public bool IsTimerAcknowledged =>
            string.Equals(Mode, "timerAck", System.StringComparison.OrdinalIgnoreCase);
    }

    public sealed class SensorInterlock
    {
        public string Block { get; set; } = string.Empty;   // sensor-interlock block key (bearing/shaft/coverPlace)
        public string Sensor { get; set; } = string.Empty;  // Control.xml sensor instance name
        public int PresentState { get; set; }               // runtime state that means "part present"
    }

    // One physical M262 channel of the cross-PLC discharge tail, fully resolved: which pin, which
    // component answers on it, and which CAT port. The binder and the parity validator read this same
    // row, so what is emitted and what is checked cannot drift apart.
    public sealed class DischargeChannel
    {
        public string Channel { get; set; } = string.Empty;
        public string Component { get; set; } = string.Empty;
        public string Port { get; set; } = string.Empty;

        public bool IsInput => Channel.StartsWith("DI", System.StringComparison.OrdinalIgnoreCase);
        public string Meaning => $"{Component}.{Port}";
    }

    public sealed class SynthSensor
    {
        public string Name { get; set; } = string.Empty;
        public int Id { get; set; }
    }
}
