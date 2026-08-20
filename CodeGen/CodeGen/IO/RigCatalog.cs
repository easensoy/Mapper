using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Configuration
{
    public sealed class RigCatalog
    {
        // Boundary of the positional component id range; reserved process/task-arm slots sit above it.
        public int ComponentIdCeiling { get; set; } = 16;
        public List<SynthSensor> SynthSensors { get; set; } = new();

        // Each CAT's command vocabulary; see the smc-rig.yml section for what the columns mean.
        public List<CatProtocolDeclaration> Protocols { get; set; } = new();
        public List<string> CrossRingSegment { get; set; } = new();
        public List<DischargeChannel> DischargeChannels { get; set; } = new();

        // Part-presence gates inserted before each pick (active-low sensors = 0, active-high = 1).
        // The top-cover slot is not stored here; StateTableAllocation computes it per ring topology.
        public List<SensorInterlock> SensorInterlocks { get; set; } = new();

        public List<FeedbackMode> FeedbackModes { get; set; } = new();

        public List<ChannelBinding> M580Channels { get; set; } = new();
        public SwivelChannelSets SwivelChannels { get; set; } = new();

        public SemanticRoles Roles { get; set; } = new();

        public FeedbackMode? FeedbackFor(string? component) =>
            FeedbackModes.FirstOrDefault(f =>
                string.Equals(f.Component, component, System.StringComparison.OrdinalIgnoreCase));

        public static RigCatalog Current => RigCatalogLoader.Catalog;

    }

    // Roles the twin cannot express, named here so no compiler branch spells a plant component.
    public sealed class SemanticRoles
    {
        public string TaskArm { get; set; } = string.Empty;
        public List<string> TopCoverSensor { get; set; } = new();

        public bool Is(string? role, string? name) =>
            !string.IsNullOrEmpty(role) && string.Equals(role, (name ?? string.Empty).Trim(),
                System.StringComparison.OrdinalIgnoreCase);

        public bool IsTopCover(string? name) =>
            TopCoverSensor.Any(w => string.Equals(w, (name ?? string.Empty).Trim(),
                System.StringComparison.OrdinalIgnoreCase));
    }

    // One authored .hcf channel and its CAT port. An empty port is blanked, never left dangling.
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
        public string Sensor { get; set; } = string.Empty;  // Control.xml sensor instance name
        public int PresentState { get; set; }               // runtime state that means "part present"
    }

    // One M262 discharge-tail channel. Binder and parity validator read this same row, so they cannot drift.
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
    }

    public sealed class CatProtocolDeclaration
    {
        public string Cat { get; set; } = string.Empty;
        public List<int> StateCounts { get; set; } = new();
        public bool ServesBranched { get; set; }
        public bool StopsAreGeometric { get; set; }
        public Dictionary<string, int> Command { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Settled { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Interlock { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Target { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
        public int CrossingFaultTimeoutMs { get; set; }
        public RawStateRange? RawStateRange { get; set; }
    }

    // The values one CAT's core can publish as CurrentRawState. Declared only by a CAT whose core
    // has a range narrower than the rules its twin could name.
    public sealed class RawStateRange
    {
        public int Min { get; set; }
        public int Max { get; set; }
    }
}
