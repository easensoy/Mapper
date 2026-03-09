using System.Collections.Generic;

namespace MapperUI.Services
{
    public enum MappingType
    {
        TRANSLATED,
        DISCARDED,
        ASSUMED,
        ENCODED,
        HARDCODED,
        SECTION
    }

    /// <summary>
    /// A single row in the Mapping Rules table.
    /// When IsSection = true the row is a visual group header (no rule data).
    /// </summary>
    public class MappingRuleEntry
    {
        public bool IsSection { get; init; }
        public string SectionTitle { get; init; } = string.Empty;
        public string VueOneElement { get; init; } = string.Empty;
        public string IEC61499Element { get; init; } = string.Empty;
        public MappingType Type { get; init; }
        public string TransformationRule { get; init; } = string.Empty;

        /// <summary>
        /// True  = this rule is currently handled by the Mapper (shows ✓).
        /// False = planned but not yet implemented in this phase (shows ✗).
        /// Ignored for SECTION rows.
        /// </summary>
        public bool IsImplemented { get; init; }
    }

    public static class MappingRuleEngine
    {
        private static MappingRuleEntry Section(string title) => new()
        {
            IsSection = true,
            SectionTitle = title,
            Type = MappingType.SECTION
        };

        private static MappingRuleEntry Rule(
            string vue, string iec, MappingType type, string rule, bool impl) => new()
            {
                VueOneElement = vue,
                IEC61499Element = iec,
                Type = type,
                TransformationRule = rule,
                IsImplemented = impl
            };

        public static IEnumerable<MappingRuleEntry> GetAllRules()
        {
            // SECTION 1: System Level
            yield return Section("System Level");

            yield return Rule("SystemID", "GUID (FBType)", MappingType.ASSUMED,
                "Generate new UUID. Do not reuse VueOne ID.", impl: true);

            yield return Rule("System/Name", "FBType Name (in system file)", MappingType.TRANSLATED,
                "Copy to system FB Name, not template.", impl: true);

            yield return Rule("Version", "N/A", MappingType.DISCARDED,
                "Not used in IEC 61499.", impl: true);

            yield return Rule("Type", "N/A", MappingType.DISCARDED,
                "System type attribute not mapped.", impl: true);

            // SECTION 2: Component Level
            yield return Section("Component Level");

            yield return Rule("Component/ComponentID", "N/A", MappingType.DISCARDED,
                "IEC 61499 uses GUID instead.", impl: true);

            yield return Rule("Component/Name", "FB Name (in system file)", MappingType.TRANSLATED,
                "Maps directly to FB instance name.", impl: true);

            yield return Rule("Component/VcID", "N/A", MappingType.DISCARDED,
                "Visual Components metadata. Dropped.", impl: true);

            yield return Rule("Component/Description", "Comment (optional)", MappingType.ASSUMED,
                "Could map to Comment attribute. Accepted as-is.", impl: true);

            yield return Rule("Component/Library_ID", "N/A", MappingType.DISCARDED,
                "VueOne library reference. No IEC 61499 equivalent.", impl: true);

            yield return Rule("Component/Type", "FB Type reference", MappingType.TRANSLATED,
                "Actuator (5 state) → Five_State_Actuator_CAT\n" +
                "Sensor (2 state) → Sensor_Bool_CAT\n" +
                "Process → Process1_CAT\n" +
                "Robot → Robot_Task_CAT", impl: true);

            // SECTION 3: State Level
            yield return Section("State Level");

            yield return Rule("State/StateID", "N/A", MappingType.DISCARDED,
                "IEC 61499 references states by Name only.", impl: true);

            yield return Rule("State/Name", "ECC State Name", MappingType.HARDCODED,
                "ECC state names are fixed in the template. Not read from Control.xml.", impl: true);

            yield return Rule("State/State_Number", "state_val (InputVar)", MappingType.TRANSLATED,
                "Direct integer mapping. state_val := State_Number.", impl: true);

            yield return Rule("State/Initial_State", "ECTransition from START", MappingType.HARDCODED,
                "Initial ECC transition is fixed in the template. Not generated.", impl: true);

            yield return Rule("State/Time", "N/A (watchdog candidate)", MappingType.ASSUMED,
                "Could map to timeout logic in a future phase.", impl: true);

            yield return Rule("State/Type (Static/Dynamic)", "N/A", MappingType.DISCARDED,
                "ECC algorithm handles static/dynamic. Not mapped.", impl: true);

            yield return Rule("State/Duration", "N/A", MappingType.DISCARDED,
                "Timing lives in ECC action algorithms. Not mapped.", impl: true);

            yield return Rule("State/ErrorMessage", "N/A", MappingType.DISCARDED,
                "Simulation only field. No IEC 61499 equivalent.", impl: true);

            yield return Rule("State/Position", "N/A", MappingType.DISCARDED,
                "Physical position used in VueOne simulation only.", impl: true);

            yield return Rule("State/Speed", "N/A", MappingType.DISCARDED,
                "PLC controls physical speed. Not part of FB logic layer.", impl: true);

            yield return Rule("State/Operator", "N/A", MappingType.DISCARDED,
                "VueOne simulation feature. Not applicable to IEC 61499.", impl: true);

            yield return Rule("State/Counter", "N/A", MappingType.DISCARDED,
                "VueOne counting feature. Not applicable.", impl: true);

            yield return Rule("State/EndCounter", "N/A", MappingType.DISCARDED,
                "VueOne simulation feature. Not applicable.", impl: true);

            yield return Rule("State/StateColour", "N/A", MappingType.DISCARDED,
                "Visual representation only. Not used in control logic.", impl: true);

            yield return Rule("State/RobotAxes", "N/A", MappingType.DISCARDED,
                "Robot specific metadata. Not applicable to general actuators.", impl: true);

            yield return Rule("State/StaticState", "Motion state indicator", MappingType.ENCODED,
                "False = motion/transient state. True = position hold (static).", impl: true);

            // SECTION 4: Transition / Sequence Level
            yield return Section("Transition / Sequence Level");

            yield return Rule("Sequence_Condition/ProcessName", "EventConnection Source",
                MappingType.ENCODED,
                "Process name maps to driving process FB instance.", impl: true);

            yield return Rule("Sequence_Condition/StateName", "EventConnection Destination",
                MappingType.ENCODED,
                "State name maps to actuator FB event input.", impl: true);

            yield return Rule("Sequence_Condition/StateValue", "DataConnection (state_val)",
                MappingType.ENCODED,
                "State value maps to data variable binding.", impl: true);

            yield return Rule("Interlock_Condition", "N/A", MappingType.DISCARDED,
                "Interlocks not in Phase 1 scope.", impl: true);

            yield return Rule("Transition/Condition", "ECC Guard", MappingType.ENCODED,
                "Logic condition encoded as ECC transition guard expression.", impl: true);

            yield return Rule("Transition/TransitionID", "N/A", MappingType.DISCARDED,
                "VueOne specific ID. IEC 61499 does not use transition IDs.", impl: true);

            yield return Rule("Transition/Type", "N/A", MappingType.DISCARDED,
                "VueOne transition classification. Not used in IEC 61499.", impl: true);

            yield return Rule("Transition/Priority", "N/A", MappingType.DISCARDED,
                "VueOne scheduling feature. IEC 61499 uses event priority.", impl: true);

            // SECTION 5: EAE System Specifics
            yield return Section("EAE System Specifics (Hardcoded)");

            yield return Rule("FB Instance ID (syslay)", "Deterministic SHA256 hash",
                MappingType.HARDCODED,
                "8 byte hex ID from SHA256(\"syslay:<name>\")", impl: true);

            yield return Rule("FB Instance ID (sysres)", "Deterministic SHA256 hash",
                MappingType.HARDCODED,
                "Separate ID from SHA256(\"sysres:<name>\")", impl: true);

            yield return Rule("Namespace", "\"Main\"", MappingType.HARDCODED,
                "All injected FBs are in the Main namespace.", impl: true);

            yield return Rule("Mapping attribute (sysres)", "syslay FB ID",
                MappingType.HARDCODED,
                "sysres Mapping attribute references the matching syslay instance.", impl: true);

            yield return Rule("Layout position (x, y)", "Auto calculated", MappingType.HARDCODED,
                "Non overlapping grid positions per type group. Actuators left of sensors.", impl: true);

            yield return Rule("actuator_name parameter", "Component name (lowercase)",
                MappingType.HARDCODED,
                "Written into Five_State_Actuator_CAT Parameter element.", impl: true);

            yield return Rule("Text parameter (Process)", "State name array",
                MappingType.HARDCODED,
                "Written into Process1_CAT Parameter element.", impl: true);
        }

        /// <summary>
        /// Returns rules filtered to the component types present in the loaded XML.
        /// Always includes System/EAE sections.
        /// </summary>
        public static IEnumerable<MappingRuleEntry> GetRelevantRules(
            bool hasActuator, bool hasSensor, bool hasProcess) => GetAllRules();
    }
}