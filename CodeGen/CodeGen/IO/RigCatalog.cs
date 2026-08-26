using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Configuration
{
    public sealed class RigCatalog
    {
        // Boundary of the positional component id range; reserved process/task-arm slots sit above it.
        public int ComponentIdCeiling { get; set; } = 16;
        public List<SynthSensor> SynthSensors { get; set; } = new();

        // How a CAT executes a movement, where its reported states are the CAT's own handshake rather
        // than the twin's stop numbering. Declaration ORDER decides which row claims a component.
        public List<CatExecutionDeclaration> Execution { get; set; } = new();

        // How a cross-process reference is compiled where the recipe cannot simply wait for the phase.
        // Every case here is a deployment DECISION, so it is declared and validated rather than assumed:
        // an undeclared deployment is refused instead of quietly taking one of the readings.
        public HandoffPolicy Handoff { get; set; } = new();
        public List<string> CrossRingSegment { get; set; } = new();
        public List<DischargeChannel> DischargeChannels { get; set; } = new();

        // Part-presence gates inserted before each pick (active-low sensors = 0, active-high = 1).
        // The top-cover slot is not stored here; StateTableAllocation computes it per ring topology.
        public List<SensorInterlock> SensorInterlocks { get; set; } = new();

        public List<FeedbackMode> FeedbackModes { get; set; } = new();

        public List<ChannelBinding> M580Channels { get; set; } = new();
        public SwivelChannelSets SwivelChannels { get; set; } = new();

        public SemanticRoles Roles { get; set; } = new();

public static RigCatalog Current => RigCatalogLoader.Catalog;

        /// The same declaration read from a run's OWN profile bundle.
        public static RigCatalog LoadFrom(string? root) => RigCatalogLoader.LoadFrom(root);

    }

    // Roles the twin cannot express, named here so no compiler branch spells a plant component.
    // What a reference to a PRODUCER'S ENTRY PHASE means for this deployment.
    public enum PeerEntryPhaseMeaning
    {
        // Undeclared. Refused: the two readings below drive the plant differently, and picking one
        // silently is the difference between a gate the twin asked for and no gate at all.
        Undeclared,

        // The producer's entry phase asserts that it is BOOT-READY, not that it completed a work cycle.
        // The consumer does not wait for it at runtime; the plant answers it by having started.
        ReadinessAssertion,

        // It is an ordinary phase and is waited for like any other, which then needs a transport that
        // carries it to the consumer - and is refused where none exists.
        RuntimePhase,
    }

    // One material carrier standing in for a producer's phase, stated in full. A carrier reports that
    // MATERIAL ARRIVED; a phase reports that a PRODUCER GOT SOMEWHERE. They are different propositions,
    // so the deployment has to say that they coincide here rather than the compiler assuming it.
    public sealed class CarrierSubstitution
    {
        // The producer whose phase is carried, and the state of it. Empty state = any phase of it.
        public string Producer { get; set; } = string.Empty;
        public string ProducerState { get; set; } = string.Empty;
        // The reporter that stands in, and the values that mean asserted and deasserted.
        public string Carrier { get; set; } = string.Empty;
        public int Asserted { get; set; } = 1;
        public int Deasserted { get; set; }
        // Why the two propositions coincide on THIS plant. Required: a substitution nobody can explain
        // is one nobody checked.
        public string Because { get; set; } = string.Empty;

        public bool Covers(string? producer, string? state) =>
            string.Equals(Producer, (producer ?? string.Empty).Trim(), System.StringComparison.OrdinalIgnoreCase) &&
            (ProducerState.Length == 0 ||
             string.Equals(ProducerState, (state ?? string.Empty).Trim(), System.StringComparison.OrdinalIgnoreCase));
    }

    public sealed class HandoffPolicy
    {
        public PeerEntryPhaseMeaning PeerEntryPhase { get; set; } = PeerEntryPhaseMeaning.Undeclared;

        // Substitutions this deployment authorises. Nothing else may replace a phase with a level.
        public List<CarrierSubstitution> Carriers { get; set; } = new();

        public CarrierSubstitution? CarrierFor(string? producer, string? state) =>
            Carriers.FirstOrDefault(c => c.Covers(producer, state));
    }

    public sealed class SemanticRoles
    {
        public string TaskArm { get; set; } = string.Empty;
        public List<string> TopCoverSensor { get; set; } = new();

        public bool Is(string? role, string? name) =>
            !string.IsNullOrEmpty(role) && string.Equals(role, (name ?? string.Empty).Trim(),
                System.StringComparison.OrdinalIgnoreCase);
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

    // A CAT's command vocabulary. Stops are the physical places the twin declares; the CAT answers with
    // a SETTLED value per stop and accepts a COMMAND value to drive there. Declared in templates.yml
    // beside the template it belongs to - one type, so the declaration and what reads it cannot drift.
    public sealed class CatProtocolDeclaration
    {
        public const string Home = "home";
        public const string Work = "work";
        public const string Work1 = "work1";
        public const string Work2 = "work2";

        public string Cat { get; set; } = string.Empty;
        // A graph matching no CAT is a failure, never a default.
        public List<int> StateCounts { get; set; } = new();
        public bool ServesBranched { get; set; }
        // A stop is identified by the twin's <Position>, not its State_Number (two branch numberings).
        public bool StopsAreGeometric { get; set; }
        // Settled and Interlock differ for the centre-home swivel's home: the core publishes 6 on arrival
        // then settles to 0, so a WAIT on 6 would miss a value that lives one run-to-stable tick.
        public Dictionary<string, int> Command { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Settled { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Interlock { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
        // What this CAT's OWN interlock manager compares against per stop. Empty = no such interface.
        public Dictionary<string, int> Target { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
        // Watchdog for a crossing between the two work stops, which outlasts a single leg.
        public int CrossingFaultTimeoutMs { get; set; }
        // The values this CAT's core can publish; a rule outside the range can never match.
        public RawStateRange? RawStateRange { get; set; }
        // The stops whose interlock verdict this CAT's core actually GATES A MOVE ON. A rule aimed at
        // any other stop would be evaluated by nobody, so it is refused rather than shipped inert.
        public List<string> EnforcedTargets { get; set; } = new();

        // The twin State_Numbers this CAT accepts as naming each canonical stop. A twin may number one
        // physical place more than once - a five-state cylinder gives its returned-complete rest a 4 and
        // its initial rest a 0, and the CAT passes through 4 and settles at 0 - so which numbers mean
        // which stop is the CAT's contract and is DECLARED, never a constant in the compiler.
        public Dictionary<string, List<int>> Stops { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);

        // The twin's MOTION state number for each leg. A leg's declared duration is read from that
        // state, so this is what says which of the twin's states times the move toward a stop.
        public Dictionary<string, int> Legs { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);

        // Which CAT wins where more than one could serve a graph. Higher wins; equal is refused as
        // ambiguous, so selection never depends on the order rows happen to be written in.
        public int Priority { get; set; }

        public bool Serves(int stateCount, bool branched) =>
            (branched && ServesBranched) || StateCounts.Contains(stateCount);

        // The canonical stop a twin State_Number names for this CAT, or null where it names none - a
        // motion state, or a number outside the CAT's vocabulary.
        public string? StopFor(int stateNumber) =>
            Stops.FirstOrDefault(kv => kv.Value.Contains(stateNumber)).Key;

        // Every twin State_Number this CAT accepts as one of its declared stops.
        public IEnumerable<int> StopNumbersFor(string stop) =>
            Stops.TryGetValue(stop, out var v) ? v : Enumerable.Empty<int>();

        // The twin's motion state for the leg toward a stop, or null where the CAT declares none.
        public int? LegFor(string stop) => Legs.TryGetValue(stop, out var n) ? n : null;

        public int CommandFor(string stop) => Command[stop];
        public int SettledFor(string stop) => Settled[stop];
        public bool Has(string stop) => Command.ContainsKey(stop);

        // Two work stops either side of a centre reference: the shared volume is crossed both ways,
        // so a rule guarding one direction has to guard the other.
        public bool CrossesBothWays => Has(Work1) && Has(Work2);

        // The stop a rule aimed at this state would guard, or null if the CAT compares against no such
        // target. Target is the raw core vocabulary a RULE is written in, which is why it is the map.
        public string? TargetStopFor(int state) =>
            Target.FirstOrDefault(kv => kv.Value == state).Key;

        // Whether a move toward that stop is actually gated by the interlock verdict.
        public bool Enforces(string stop) =>
            EnforcedTargets.Any(s => string.Equals(s, stop, System.StringComparison.OrdinalIgnoreCase));
    }

    // How a CAT executes a movement. One mode, not a set of flags: two booleans can be both set or
    // neither, and both of those are a silently different machine.
    //   stopDriven  walked to the stop the twin numbers, which is what a cylinder does
    //   runOnce     the whole sequence emitted the first time the recipe moves it, and never again
    //   alternate   one step per movement, resuming from wherever the last one settled
    public enum ExecutionMode { StopDriven, RunOnce, Alternate }

    // A fixed command sequence a CAT runs, instead of being walked to a stop the twin numbers. A row
    // claims a component when every field it DECLARES matches; rows must be disjoint, so exactly one
    // can claim a component and no order decides it.
    public sealed class CatExecutionDeclaration
    {
        public string Cat { get; set; } = string.Empty;
        public string ComponentType { get; set; } = string.Empty;
        public ExecutionMode Mode { get; set; }
        public List<ExecutionStepDeclaration> Steps { get; set; } = new();

        // Where it resumes: the step AFTER the one whose arrival value it is resting at, wrapping at the
        // end. Resting at none - or at a value no step produces - starts the sequence again. This holds
        // for a sequence of any length; two steps is simply the shortest rotation.
        public ExecutionStepDeclaration StepFrom(int? settledAt)
        {
            int at = settledAt.HasValue ? Steps.FindIndex(s => s.Settled == settledAt.Value) : -1;
            return Steps[at < 0 ? 0 : (at + 1) % Steps.Count];
        }

        public int FinalSettled => Steps[Steps.Count - 1].Settled;

        public bool Claims(string? catType, string? componentType) =>
            (Cat.Length == 0 || string.Equals(Cat, catType, System.StringComparison.OrdinalIgnoreCase)) &&
            (ComponentType.Length == 0 ||
             string.Equals(ComponentType, componentType, System.StringComparison.OrdinalIgnoreCase));
    }

    // A command value and the settled value that means the CAT arrived.
    public sealed class ExecutionStepDeclaration
    {
        public int Command { get; set; }
        public int Settled { get; set; }
    }

    // The values one CAT's core can publish as CurrentRawState. Declared only by a CAT whose core
    // has a range narrower than the rules its twin could name.
    public sealed class RawStateRange
    {
        public int Min { get; set; }
        public int Max { get; set; }
    }
}
