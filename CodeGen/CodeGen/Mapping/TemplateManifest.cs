using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Mapping
{
    // Where in the Template Library an artefact lives, which also fixes the order it deploys in:
    // a CAT cannot be extracted before the basics its FBNetwork instantiates.
    public enum ArtefactKind { Basic, Adapter, Composite, HmiCat, Cat, DataType }

    // What the emitted FB IS to the generator. Drives ring/station wiring, not deployment.
    public enum TypeRole { Actuator, Sensor, Process, Infrastructure }

    public sealed record TemplateType(
        string Name,
        ArtefactKind Kind,
        TypeRole Role,
        bool Deploy,
        bool MirrorToSysres,
        IReadOnlyList<string> Requires)
    {
        // Deploy-time patches reshape this artefact, so a copy-if-absent extraction would keep a
        // stale one. Delete before extracting. The interlock artefacts must refresh TOGETHER (they
        // flip to/from the RuleTable/Target struct as one) or EAE reports a missing struct member.
        public bool ForceRefresh { get; init; }

        // Boundary ports the deployed artefact must declare. Empty = not port-validated.
        public IReadOnlyList<string> Ports { get; init; } = Array.Empty<string>();

        // Which resource-infrastructure roles this type serves. layout.yml names the INSTANCE for a
        // role; this says which type realises it, so no emitter spells a template name to build a
        // station. One type may serve several roles (a terminator caps an area or a station).
        public IReadOnlyList<string> InfraRoles { get; init; } = Array.Empty<string>();

        // The parameter that carries the instance's own name, when the type has one.
        public string? NameParameter { get; init; }
    }

    // One row per FB type the Mapper owns, so adding a CAT is a one-row change rather than an
    // edit in the deployer, the mirror, the wiring emitter and the port validator. Declaration
    // ORDER IS LOAD-BEARING: DeployOrder walks it, and a basic must precede the CAT that
    // instantiates it.
    public static class TemplateManifest
    {
        static readonly string[] None = Array.Empty<string>();

        static readonly TemplateType[] Types =
        {
            // --- Basics: bodies the CATs instantiate ---------------------------------------
            new("FiveStateActuator",            ArtefactKind.Basic, TypeRole.Infrastructure, true,  false, None) { ForceRefresh = true },
            new("Sensor_Bool",                  ArtefactKind.Basic, TypeRole.Infrastructure, true,  false, None),
            new("Station_Core",                 ArtefactKind.Basic, TypeRole.Infrastructure, true,  false, None),
            new("Station_Fault",                ArtefactKind.Basic, TypeRole.Infrastructure, true,  false, None),
            new("Station_Status",               ArtefactKind.Basic, TypeRole.Infrastructure, true,  false, None),
            new("ProcessRuntime_Generic_v1",    ArtefactKind.Basic, TypeRole.Infrastructure, true,  false, None) { ForceRefresh = true },
            new("ProcessStateBusHandler",       ArtefactKind.Basic, TypeRole.Infrastructure, true,  false, None) { ForceRefresh = true },
            new("FaultLatch",                   ArtefactKind.Basic, TypeRole.Infrastructure, true,  false, None),
            new("actuatorStateEvents",          ArtefactKind.Basic, TypeRole.Infrastructure, true,  false, None),
            new("updateComponentState",         ArtefactKind.Basic, TypeRole.Infrastructure, true,  false, None),
            new("updateComponentState_Sensor",  ArtefactKind.Basic, TypeRole.Infrastructure, true,  false, None),
            new("No_Sensor_Handler",            ArtefactKind.Basic, TypeRole.Infrastructure, true,  false, None),
            new("CommonInterlockEvaluator",     ArtefactKind.Basic, TypeRole.Infrastructure, true,  false, None) { ForceRefresh = true },
            new("changeEventProcess1",          ArtefactKind.Basic, TypeRole.Infrastructure, true,  false, None),
            new("changeEventProcess2",          ArtefactKind.Basic, TypeRole.Infrastructure, true,  false, None),
            new("SevenStateActuator",           ArtefactKind.Basic, TypeRole.Infrastructure, true,  false, None),
            new("SevenStateActuator2",          ArtefactKind.Basic, TypeRole.Infrastructure, true,  false, None),
            new("SevenStateCentreHomeActuator", ArtefactKind.Basic, TypeRole.Infrastructure, true,  false, None) { ForceRefresh = true },
            new("No_Sensor_Handler_7SCH",       ArtefactKind.Basic, TypeRole.Infrastructure, true,  false, None) { ForceRefresh = true },
            new("FaultLatch_7SCH",              ArtefactKind.Basic, TypeRole.Infrastructure, true,  false, None),
            new("actuatorStateEvents_7SCH",     ArtefactKind.Basic, TypeRole.Infrastructure, true,  false, None),

            // --- Adapters -----------------------------------------------------------------
            new("CaSAdptr",                     ArtefactKind.Adapter, TypeRole.Infrastructure, true, false, None),
            new("AreaHMIAdptr",                 ArtefactKind.Adapter, TypeRole.Infrastructure, true, false, None),
            new("StationHMIAdptr",              ArtefactKind.Adapter, TypeRole.Infrastructure, true, false, None),
            new("stateRptCmdAdptr",             ArtefactKind.Adapter, TypeRole.Infrastructure, true, false, None),

            // --- Composites ---------------------------------------------------------------
            new("Area",                         ArtefactKind.Composite, TypeRole.Infrastructure, true, true,  None) { Ports = new[] { "AreaHMIAdptrIN", "AreaAdptrOUT" }, InfraRoles = new[] { "area" }, NameParameter = "AreaName" },
            new("Station",                      ArtefactKind.Composite, TypeRole.Infrastructure, true, true,  None) { Ports = new[] { "AreaAdptrIN", "StationHMIAdptrIN", "AreaAdptrOUT", "StationAdaptrOUT" }, InfraRoles = new[] { "station" }, NameParameter = "StationName" },
            new("CaSAdptrTerminator",           ArtefactKind.Composite, TypeRole.Infrastructure, true, true,  None) { Ports = new[] { "CasAdptrIN" }, InfraRoles = new[] { "terminator", "areaTerminator" } },
            new("faultDetection",               ArtefactKind.Composite, TypeRole.Infrastructure, true, false, None),
            new("faultDetection_7SCH",          ArtefactKind.Composite, TypeRole.Infrastructure, true, false, None),

            // --- HMI CATs (deployed before the control CATs, as EAE expects) ---------------
            new("Area_CAT",                     ArtefactKind.HmiCat, TypeRole.Infrastructure, true, true, None) { InfraRoles = new[] { "areaHmi" } },
            new("Station_CAT",                  ArtefactKind.HmiCat, TypeRole.Infrastructure, true, true,
                new[] { "Station_Core", "Station_Fault", "Station_Status" })
                { InfraRoles = new[] { "stationHmi" } },

            // --- Control CATs -------------------------------------------------------------
            new("Five_State_Actuator_CAT",      ArtefactKind.Cat, TypeRole.Actuator, true, true,
                new[] { "FiveStateActuator" })
                { ForceRefresh = true, Ports = new[] { "stationAdptr_in", "stateRprtCmd_in", "stationAdptr_out", "stateRprtCmd_out" } },
            new("Sensor_Bool_CAT",              ArtefactKind.Cat, TypeRole.Sensor,   true, true,
                new[] { "Sensor_Bool" })
                { ForceRefresh = true, Ports = new[] { "stateRprtCmd_in", "stateRprtCmd_out" } },
            new("Process1_Generic",             ArtefactKind.Cat, TypeRole.Process,  true, true,
                new[] { "ProcessRuntime_Generic_v1", "ProcessStateBusHandler" })
                { ForceRefresh = true, Ports = new[] { "stateRptCmdAdptr_in", "stationAdptr_in", "stateRptCmdAdptr_out", "stationAdptr_out" } },
            new("Seven_State_Actuator_CAT",     ArtefactKind.Cat, TypeRole.Actuator, true, true,
                new[] { "SevenStateActuator", "SevenStateActuator2" }),
            new(TemplateMap.SevenStateCentreHomeCat, ArtefactKind.Cat, TypeRole.Actuator, true, true,
                new[] { "SevenStateCentreHomeActuator", "No_Sensor_Handler_7SCH", "FaultLatch_7SCH",
                        "actuatorStateEvents_7SCH" })
                { ForceRefresh = true },

            // --- Types the Mapper emits or consumes but does not deploy from the library ---
            // Robot_Task_Core + Robot_Task_CAT ARE deployed unconditionally, but by an explicit pair
            // of calls positioned AFTER both CAT loops. Deploy stays false so the table cannot move
            // them: DeployResult preserves deploy order and DfbprojRegistrar appends, so promoting
            // them here would reorder the generated .dfbproj entries.
            // The UR3e task arm carries RING ports ONLY — no stationAdptr (it is off the CaSBus chain).
            new("Robot_Task_CAT",               ArtefactKind.Cat,   TypeRole.Actuator, false, true,
                new[] { "Robot_Task_Core" })
                { Ports = new[] { "stateRprtCmd_in", "stateRprtCmd_out" } },
            new("Five_State_Actuator_No_Sensors_CAT", ArtefactKind.Cat, TypeRole.Actuator, false, true, None),
            new("Vacuum_Gripper_CAT",           ArtefactKind.Cat,   TypeRole.Actuator, false, false, None),
            new("Actuator_Fault_CAT",           ArtefactKind.Cat,   TypeRole.Infrastructure, false, false,
                new[] { "FaultLatch" }),
            // Emitted by the device/telemetry emitters, mirrored onto their own resource.
            new("PLC_RW_M262",                  ArtefactKind.Composite, TypeRole.Infrastructure, false, true, None),
            new("MQTT_CONNECTION",              ArtefactKind.Basic, TypeRole.Infrastructure, false, true, None),
            new("Telemetry",                    ArtefactKind.Composite, TypeRole.Infrastructure, false, true, None),
            new("MqttStateFormatter",           ArtefactKind.Basic, TypeRole.Infrastructure, false, true, None),
            new("MQTT_PUBLISH_115480E69E664F878", ArtefactKind.Basic, TypeRole.Infrastructure, false, true, None),

            // --- Datatypes ----------------------------------------------------------------
            new("Component_State",              ArtefactKind.DataType, TypeRole.Infrastructure, true, false, None),
            new("Component_State_Msg",          ArtefactKind.DataType, TypeRole.Infrastructure, true, false, None),
        };

        static readonly Dictionary<string, TemplateType> ByName =
            Types.ToDictionary(t => t.Name, StringComparer.Ordinal);

        // The CAT a process engine is emitted as. One type carries TypeRole.Process, so nothing
        // downstream has to spell it.
        public static TemplateType ProcessType { get; } = Types.Single(t => t.Role == TypeRole.Process);

        // The one type that realises a resource-infrastructure role. Throws rather than guessing:
        // an unserved role means layout.yml names an instance the Mapper has no template for.
        public static TemplateType ForInfraRole(string role)
        {
            var hits = Types.Where(t => t.InfraRoles.Contains(role, StringComparer.Ordinal)).ToList();
            if (hits.Count == 1) return hits[0];
            throw new InvalidOperationException(hits.Count == 0
                ? $"[Manifest] no template serves the infrastructure role '{role}'."
                : $"[Manifest] {hits.Count} templates serve the infrastructure role '{role}': " +
                  string.Join(", ", hits.Select(t => t.Name)) + ".");
        }

        public static TemplateType? Find(string? name) =>
            name != null && ByName.TryGetValue(name, out var t) ? t : null;

        // Deployment inventory, in declaration order — the deployer walks kinds in dependency order.
        public static IReadOnlyList<string> Deployed(ArtefactKind kind) =>
            Types.Where(t => t.Deploy && t.Kind == kind).Select(t => t.Name).ToArray();

        // Basics a CAT's FBNetwork instantiates; they must already be deployed.
        public static IReadOnlyList<string> Requires(string catName) =>
            Find(catName)?.Requires ?? None;

        // Which syslay top-level FB Types are projected onto a per-PLC sysres. Read by the mirror
        // AND the parity validator so the two can never drift.
        public static IReadOnlySet<string> Mirrored { get; } =
            new HashSet<string>(Types.Where(t => t.MirrorToSysres).Select(t => t.Name), StringComparer.Ordinal);

        // FB types the sysres wiring treats as ring actuators / ring sensors.
        public static IReadOnlySet<string> ActuatorTypes { get; } =
            new HashSet<string>(Types.Where(t => t.Role == TypeRole.Actuator).Select(t => t.Name), StringComparer.Ordinal);

        // Artefacts a later deploy-time patch reshapes: ExtractToEae is copy-if-absent, so these
        // must be deleted first or a re-Generate keeps the stale, differently-shaped one.
        public static IReadOnlyList<string> ForceRefresh(ArtefactKind kind) =>
            Types.Where(t => t.ForceRefresh && t.Kind == kind).Select(t => t.Name).ToArray();

        // Type -> the boundary ports its deployed artefact must declare. Only port-validated types.
        public static IReadOnlyDictionary<string, IReadOnlyList<string>> PortContract { get; } =
            Types.Where(t => t.Ports.Count > 0)
                 .ToDictionary(t => t.Name, t => t.Ports, StringComparer.Ordinal);

        public static IReadOnlySet<string> SensorTypes { get; } =
            new HashSet<string>(Types.Where(t => t.Role == TypeRole.Sensor).Select(t => t.Name), StringComparer.Ordinal);
    }
}
