using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Mapping
{
    // Where an artefact lives, which also fixes deploy order: a CAT needs its basics extracted first.
    public enum ArtefactKind { Basic, Adapter, Composite, HmiCat, Cat, DataType }

    // What the emitted FB IS to the generator. Drives ring/station wiring, not deployment.
    public enum TypeRole { Actuator, Sensor, Process, Infrastructure }

    public sealed record TemplateType(
        string Name,
        ArtefactKind Kind,
        TypeRole Role,
        bool Deploy,
        bool MirrorToSysres)
    {
        // A deploy-time patch reshapes this artefact, so copy-if-absent would keep a stale one: delete
        // before extracting. The interlock artefacts must refresh TOGETHER or EAE reports a missing member.
        // The instance carries the sensor-fitted / motion-timer parameter set. A CAT without it is
        // driven purely by its declared command values.
        public bool SensorTimed { get; init; }

        public bool ForceRefresh { get; init; }

        // Boundary ports the deployed artefact must declare. Empty = not port-validated.
        public IReadOnlyList<string> Ports { get; init; } = Array.Empty<string>();

        // Instantiated on the shared canvas every run, so a stale instance must be swept first.
        public bool Emitted { get; init; }

        // Deployed after every other artefact of its kind; deploy order decides where its dfbproj entry lands.
        public bool DeployLast { get; init; }

        // Ships an HMI faceplate whose OPCUA frame the deployer must square up.
        public bool HmiFaceplate { get; init; }

        // The embedded telemetry tap. Absent = this CAT publishes nothing.
        public CatTelemetryTap? Telemetry { get; init; }

        // Resource-infrastructure roles this type serves; layout.yml names the instance for a role.
        public IReadOnlyList<string> InfraRoles { get; init; } = Array.Empty<string>();

        public string? NameParameter { get; init; }

        // A type without the stationAdptr port dangles on the CaSBus chain and EAE rejects the resource.
        public bool StationAdapter { get; init; } = true;

        // ONE owner for the command values: if compiler and layout disagree, the ECC gate never opens.
        public CatProtocol? Protocol { get; init; }
    }

    // A CAT's command vocabulary. Stops are the physical places the twin declares; the CAT answers with a
    // SETTLED value per stop and accepts a COMMAND value to drive there.
    public sealed record CatProtocol(
        // A graph matching no CAT is a failure, never a default.
        IReadOnlyList<int> StateCounts,
        bool ServesBranched,
        // Settled and Interlock differ for the centre-home swivel's home: the core publishes 6 on arrival
        // then settles to 0, so a WAIT on 6 would miss a value that lives one run-to-stable tick.
        IReadOnlyDictionary<string, int> Command,
        IReadOnlyDictionary<string, int> Settled,
        IReadOnlyDictionary<string, int> Interlock,
        // A stop is identified by the twin's <Position>, not its State_Number (two branch numberings).
        bool StopsAreGeometric = false,
        // The values this CAT's core can publish; a rule outside the range can never match.
        Configuration.RawStateRange? RawStateRange = null,
        // What this CAT's OWN interlock manager compares against per stop. Empty = no such
        // interface, so nothing is written for it.
        IReadOnlyDictionary<string, int>? Target = null,
        // Watchdog for a crossing between the two work stops, which outlasts a single leg.
        int CrossingFaultTimeoutMs = 0)
    {
        public const string Home = "home";
        public const string Work = "work";
        public const string Work1 = "work1";
        public const string Work2 = "work2";

        public bool Serves(int stateCount, bool branched) =>
            (branched && ServesBranched) || StateCounts.Contains(stateCount);

        public int CommandFor(string stop) => Command[stop];
        public int SettledFor(string stop) => Settled[stop];
        public int InterlockFor(string stop) => Interlock[stop];
        public bool Has(string stop) => Command.ContainsKey(stop);

        // Two work stops either side of a centre reference: the shared volume is crossed both ways,
        // so a rule guarding one direction has to guard the other.
        public bool CrossesBothWays => Has(Work1) && Has(Work2);

        // Whether the CAT gives this arrival value a stop of its own, rather than passing through it.
        public bool SettlesAt(int value) => Settled.Values.Contains(value);
    }

    // One row per FB type the Mapper owns. Declaration ORDER IS LOAD-BEARING: DeployOrder walks it, and a
    // basic must precede the CAT that instantiates it.
    // Where a CAT's own state lives, so the deployer can wire a publisher in without knowing the CAT.
    public sealed record CatTelemetryTap(
        string StateEventSource, string StateDataSource, string InitSource, string TopicNameSource);

    public static class TemplateManifest
    {

        static IReadOnlyDictionary<string, int> Stops(params (string Stop, int Value)[] rows) =>
            rows.ToDictionary(r => r.Stop, r => r.Value, StringComparer.OrdinalIgnoreCase);

        // A CAT with no declared protocol has no stop vocabulary: the task arm's two states are a handshake.
        static CatProtocol? DeclaredProtocol(string cat)
        {
            var d = Configuration.RigCatalog.Current.Protocols
                .FirstOrDefault(p => string.Equals(p.Cat, cat, StringComparison.OrdinalIgnoreCase));
            return d == null ? null : new CatProtocol(
                d.StateCounts, d.ServesBranched, d.Command, d.Settled, d.Interlock,
                d.StopsAreGeometric, d.RawStateRange, d.Target, d.CrossingFaultTimeoutMs);
        }

        static readonly TemplateType[] Types =
        {
            new("FiveStateActuator",            ArtefactKind.Basic, TypeRole.Infrastructure, true,  false) { ForceRefresh = true },
            new("Sensor_Bool",                  ArtefactKind.Basic, TypeRole.Infrastructure, true,  false),
            new("Station_Core",                 ArtefactKind.Basic, TypeRole.Infrastructure, true,  false),
            new("Station_Fault",                ArtefactKind.Basic, TypeRole.Infrastructure, true,  false),
            new("Station_Status",               ArtefactKind.Basic, TypeRole.Infrastructure, true,  false),
            new("ProcessRuntime_Generic_v1",    ArtefactKind.Basic, TypeRole.Infrastructure, true,  false) { ForceRefresh = true },
            new("ProcessStateBusHandler",       ArtefactKind.Basic, TypeRole.Infrastructure, true,  false) { ForceRefresh = true },
            new("FaultLatch",                   ArtefactKind.Basic, TypeRole.Infrastructure, true,  false),
            new("actuatorStateEvents",          ArtefactKind.Basic, TypeRole.Infrastructure, true,  false),
            new("updateComponentState",         ArtefactKind.Basic, TypeRole.Infrastructure, true,  false),
            new("updateComponentState_Sensor",  ArtefactKind.Basic, TypeRole.Infrastructure, true,  false),
            new("No_Sensor_Handler",            ArtefactKind.Basic, TypeRole.Infrastructure, true,  false),
            new("CommonInterlockEvaluator",     ArtefactKind.Basic, TypeRole.Infrastructure, true,  false) { ForceRefresh = true },
            new("changeEventProcess1",          ArtefactKind.Basic, TypeRole.Infrastructure, true,  false),
            new("changeEventProcess2",          ArtefactKind.Basic, TypeRole.Infrastructure, true,  false),
            new("SevenStateActuator",           ArtefactKind.Basic, TypeRole.Infrastructure, true,  false),
            new("SevenStateActuator2",          ArtefactKind.Basic, TypeRole.Infrastructure, true,  false),
            new("SevenStateCentreHomeActuator", ArtefactKind.Basic, TypeRole.Infrastructure, true,  false) { ForceRefresh = true },
            new("No_Sensor_Handler_7SCH",       ArtefactKind.Basic, TypeRole.Infrastructure, true,  false) { ForceRefresh = true },
            new("FaultLatch_7SCH",              ArtefactKind.Basic, TypeRole.Infrastructure, true,  false),
            new("actuatorStateEvents_7SCH",     ArtefactKind.Basic, TypeRole.Infrastructure, true,  false),

            new("CaSAdptr",                     ArtefactKind.Adapter, TypeRole.Infrastructure, true, false),
            new("AreaHMIAdptr",                 ArtefactKind.Adapter, TypeRole.Infrastructure, true, false),
            new("StationHMIAdptr",              ArtefactKind.Adapter, TypeRole.Infrastructure, true, false),
            new("stateRptCmdAdptr",             ArtefactKind.Adapter, TypeRole.Infrastructure, true, false),

            new("Area",                         ArtefactKind.Composite, TypeRole.Infrastructure, true, true) { Emitted = true, Ports = new[] { "AreaHMIAdptrIN", "AreaAdptrOUT" }, InfraRoles = new[] { "area" }, NameParameter = "AreaName" },
            new("Station",                      ArtefactKind.Composite, TypeRole.Infrastructure, true, true) { Emitted = true, Ports = new[] { "AreaAdptrIN", "StationHMIAdptrIN", "AreaAdptrOUT", "StationAdaptrOUT" }, InfraRoles = new[] { "station" }, NameParameter = "StationName" },
            new("CaSAdptrTerminator",           ArtefactKind.Composite, TypeRole.Infrastructure, true, true) { Emitted = true, Ports = new[] { "CasAdptrIN" }, InfraRoles = new[] { "terminator", "areaTerminator" } },
            new("faultDetection",               ArtefactKind.Composite, TypeRole.Infrastructure, true, false),
            new("faultDetection_7SCH",          ArtefactKind.Composite, TypeRole.Infrastructure, true, false),

            new("Area_CAT",                     ArtefactKind.HmiCat, TypeRole.Infrastructure, true, true) { Emitted = true, InfraRoles = new[] { "areaHmi" } },
            new("Station_CAT",                  ArtefactKind.HmiCat, TypeRole.Infrastructure, true, true)
                { Emitted = true, InfraRoles = new[] { "stationHmi" } },

            new("Five_State_Actuator_CAT",      ArtefactKind.Cat, TypeRole.Actuator, true, true)
                { ForceRefresh = true, Emitted = true, HmiFaceplate = true, SensorTimed = true,
                  Ports = new[] { "stationAdptr_in", "stateRprtCmd_in", "stationAdptr_out", "stateRprtCmd_out" },
                  Protocol = DeclaredProtocol("Five_State_Actuator_CAT"),
                  Telemetry = new CatTelemetryTap("ActuatorCore.pst_out", "ActuatorCore.current_state_to_process", "StateHandling.INITO", "actuator_name") },
            new("Sensor_Bool_CAT",              ArtefactKind.Cat, TypeRole.Sensor,   true, true)
                { ForceRefresh = true, Emitted = true, HmiFaceplate = true,
                  Ports = new[] { "stateRprtCmd_in", "stateRprtCmd_out" },
                  Telemetry = new CatTelemetryTap("FB1.CNF", "FB1.Status", "StateHandling.INITO", "name") },
            new("Process1_Generic",             ArtefactKind.Cat, TypeRole.Process,  true, true)
                { ForceRefresh = true, Emitted = true,
                  Ports = new[] { "stateRptCmdAdptr_in", "stationAdptr_in", "stateRptCmdAdptr_out", "stationAdptr_out" } },
            // Ring ports only; the centre-home variant below does declare stationAdptr.
            new("Seven_State_Actuator_CAT",     ArtefactKind.Cat, TypeRole.Actuator, true, true)
                { StationAdapter = false },
            new(TemplateMap.SevenStateCentreHomeCat, ArtefactKind.Cat, TypeRole.Actuator, true, true)
                { ForceRefresh = true, HmiFaceplate = true,
                  Protocol = DeclaredProtocol(TemplateMap.SevenStateCentreHomeCat),
                  Telemetry = new CatTelemetryTap("ActuatorCore.pst_out", "ActuatorCore.current_state_to_process", "StateHandling.INITO", "actuator_name") },

            new("Robot_Task_Core",              ArtefactKind.Basic, TypeRole.Infrastructure, true, false)
                { DeployLast = true },
            new("Robot_Task_CAT",               ArtefactKind.Cat,   TypeRole.Actuator, true, true)
                { DeployLast = true, HmiFaceplate = true, StationAdapter = false,
                  // The CAT that renders whatever instance the rig profile names as the task arm.
                  InfraRoles = new[] { "taskArm" },
                  Ports = new[] { "stateRprtCmd_in", "stateRprtCmd_out" },
                  Telemetry = new CatTelemetryTap("StateMachine.pst_out",
                      "StateMachine.current_state_to_process", "StateHandling.INITO", "actuator_name") },
            new("Five_State_Actuator_No_Sensors_CAT", ArtefactKind.Cat, TypeRole.Actuator, false, true)
                { Protocol = DeclaredProtocol("Five_State_Actuator_No_Sensors_CAT") },
            new("Vacuum_Gripper_CAT",           ArtefactKind.Cat,   TypeRole.Actuator, false, false),
            new("Actuator_Fault_CAT",           ArtefactKind.Cat,   TypeRole.Infrastructure, false, false),
            // Emitted by the device/telemetry emitters, mirrored onto their own resource.
            new("PLC_RW_M262",                  ArtefactKind.Composite, TypeRole.Infrastructure, false, true) { Emitted = true },
            new("MQTT_CONNECTION",              ArtefactKind.Basic, TypeRole.Infrastructure, false, true),
            new("Telemetry",                    ArtefactKind.Composite, TypeRole.Infrastructure, false, true),
            new("MqttStateFormatter",           ArtefactKind.Basic, TypeRole.Infrastructure, false, true),
            new("MQTT_PUBLISH_115480E69E664F878", ArtefactKind.Basic, TypeRole.Infrastructure, false, true),

            new("Component_State",              ArtefactKind.DataType, TypeRole.Infrastructure, true, false),
            new("Component_State_Msg",          ArtefactKind.DataType, TypeRole.Infrastructure, true, false),
        };

        static readonly Dictionary<string, TemplateType> ByName =
            Types.ToDictionary(t => t.Name, StringComparer.Ordinal);

        // One type carries TypeRole.Process, so nothing downstream has to spell its name.
        public static TemplateType ProcessType { get; } = Types.Single(t => t.Role == TypeRole.Process);

        // The one CAT that renders a sensor. A component's role picks its type; a name never does.
        public static TemplateType SensorType { get; } = Types.Single(
            t => t.Role == TypeRole.Sensor && t.Kind == ArtefactKind.Cat);

        // Throws rather than guessing: an unserved role means layout.yml names an instance with no template.
        public static TemplateType ForInfraRole(string role)
        {
            var hits = Types.Where(t => t.InfraRoles.Contains(role, StringComparer.Ordinal)).ToList();
            if (hits.Count == 1) return hits[0];
            throw new InvalidOperationException(hits.Count == 0
                ? $"[Manifest] no template serves the infrastructure role '{role}'."
                : $"[Manifest] {hits.Count} templates serve the infrastructure role '{role}': " +
                  string.Join(", ", hits.Select(t => t.Name)) + ".");
        }

        // Declaration order breaks a tie, so the sensored five-state CAT wins over the no-sensors variant.
        public static TemplateType? ForGraph(int stateCount, bool branched) =>
            Types.FirstOrDefault(t => t.Protocol is { } p && p.Serves(stateCount, branched));

        // A CAT commanded by a handshake rather than stop values declares no protocol; asking is not an error.
        public static CatProtocol? ProtocolOrNull(string? catType) => Find(catType)?.Protocol;

        public static CatProtocol ProtocolOf(string? catType) =>
            Find(catType)?.Protocol
            ?? throw new InvalidOperationException(
                $"[CAT] '{catType}' declares no command protocol, so nothing can say which value drives " +
                "it or which value means it arrived.");

        // Types the Mapper instantiates every run; a stale instance is swept before generation.
        public static IReadOnlySet<string> EmittedTypes { get; } =
            Types.Where(t => t.Emitted).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        public static TemplateType? Find(string? name) =>
            name != null && ByName.TryGetValue(name, out var t) ? t : null;

        // Deployment inventory, in declaration order, minus the ones held back to the end.
        public static IReadOnlyList<string> DeployedLast(ArtefactKind kind) =>
            Types.Where(t => t.Deploy && t.DeployLast && t.Kind == kind).Select(t => t.Name).ToList();

        // Every CAT whose faceplate or telemetry the deployer patches, so a new one is a manifest row.
        public static IReadOnlyList<TemplateType> WithHmiFaceplate =>
            Types.Where(t => t.HmiFaceplate).ToList();

        public static IReadOnlyList<TemplateType> WithTelemetryTap =>
            Types.Where(t => t.Telemetry != null).ToList();

        public static IReadOnlyList<string> Deployed(ArtefactKind kind) =>
            Types.Where(t => t.Deploy && !t.DeployLast && t.Kind == kind).Select(t => t.Name).ToArray();

        // The ring and station adapter ports a CAT declares. The process CAT spells the ring pair
        // differently from a component CAT, so the spelling is READ from the declaration rather than
        // inferred from the type's name. A CAT that declares no ports keeps the component spelling.
        public static string RingIn(string? cat)     => Port(cat, "stateR", "_in",  "stateRprtCmd_in");
        public static string RingOut(string? cat)    => Port(cat, "stateR", "_out", "stateRprtCmd_out");
        public static string StationIn(string? cat)  => Port(cat, "stationAdptr", "_in",  "stationAdptr_in");
        public static string StationOut(string? cat) => Port(cat, "stationAdptr", "_out", "stationAdptr_out");

        private static string Port(string? cat, string kind, string direction, string whenUndeclared) =>
            (cat == null ? null : Find(cat)?.Ports.FirstOrDefault(p =>
                p.Contains(kind, StringComparison.Ordinal) &&
                p.EndsWith(direction, StringComparison.Ordinal))) ?? whenUndeclared;

        // Read by the mirror AND the parity validator, so the two can never drift.
        public static IReadOnlySet<string> Mirrored { get; } =
            new HashSet<string>(Types.Where(t => t.MirrorToSysres).Select(t => t.Name), StringComparer.Ordinal);

        public static IReadOnlySet<string> ActuatorTypes { get; } =
            new HashSet<string>(Types.Where(t => t.Role == TypeRole.Actuator).Select(t => t.Name), StringComparer.Ordinal);

        public static IReadOnlyList<string> ForceRefresh(ArtefactKind kind) =>
            Types.Where(t => t.ForceRefresh && t.Kind == kind).Select(t => t.Name).ToArray();

        public static IReadOnlyDictionary<string, IReadOnlyList<string>> PortContract { get; } =
            Types.Where(t => t.Ports.Count > 0)
                 .ToDictionary(t => t.Name, t => t.Ports, StringComparer.Ordinal);

        public static IReadOnlySet<string> SensorTypes { get; } =
            new HashSet<string>(Types.Where(t => t.Role == TypeRole.Sensor).Select(t => t.Name), StringComparer.Ordinal);
    }
}
