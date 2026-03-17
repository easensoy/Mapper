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

    public class MappingRuleEntry
    {
        public bool IsSection { get; init; }
        public string SectionTitle { get; init; } = string.Empty;
        public string VueOneElement { get; init; } = string.Empty;
        public string IEC61499Element { get; init; } = string.Empty;
        public MappingType Type { get; init; }
        public string TransformationRule { get; init; } = string.Empty;
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
            yield return Section("System Level");

            yield return Rule("SystemID", "GUID (FBType)", MappingType.ASSUMED,
                "Generate new UUID. Do not reuse VueOne ID.", impl: true);

            yield return Rule("System/Name", "FBType Name (in system file)", MappingType.TRANSLATED,
                "Copy to system FB Name, not template.", impl: true);

            yield return Rule("Version", "N/A", MappingType.DISCARDED,
                "Not used in IEC 61499.", impl: true);

            yield return Rule("Type", "N/A", MappingType.DISCARDED,
                "System type attribute not mapped.", impl: true);

            yield return Section("Component Level");

            yield return Rule("Component/ComponentID", "N/A", MappingType.DISCARDED,
                "IEC 61499 uses GUID instead.", impl: true);

            yield return Rule("Component/Name", "FB Name (in system file)", MappingType.TRANSLATED,
                "Maps directly to FB instance name.", impl: true);

            yield return Rule("Component/VcID", "N/A", MappingType.DISCARDED,
                "Visual Components metadata. Dropped.", impl: true);

            yield return Rule("Component/Description", "Comment (optional)", MappingType.ASSUMED,
                "Could map to Comment attribute.", impl: false);

            yield return Rule("Component/Library_ID", "N/A", MappingType.DISCARDED,
                "VueOne library reference. No IEC 61499 equivalent.", impl: true);

            yield return Rule("Component/Type", "Determines CAT template", MappingType.TRANSLATED,
                "Actuator + 5 states = Five_State_Actuator_CAT.", impl: true);

            yield return Section("State Level");

            yield return Rule("State/StateID", "N/A", MappingType.DISCARDED,
                "IEC 61499 ECC uses State Name only.", impl: true);

            yield return Rule("State/Name", "ECState Name (in ECC)", MappingType.ENCODED,
                "State name maps to ECC state name in template FB.", impl: true);

            yield return Rule("State/State_Number", "state_val parameter", MappingType.ENCODED,
                "Numeric state value passed to actuator.", impl: true);

            yield return Rule("State/Initial_State", "START transition target", MappingType.ENCODED,
                "Initial state determines ECC START transition.", impl: true);

            yield return Section("Conditions & Transitions");

            yield return Rule("Sequence_Condition", "EventConnections in syslay", MappingType.ENCODED,
                "Process state triggers actuator via pst_event wiring.", impl: true);

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

            yield return Section("EAE System Specifics (Hardcoded)");

            yield return Rule("", "Deterministic SHA256 hash (syslay ID)",
                MappingType.HARDCODED,
                "8 byte hex ID from SHA256(\"syslay:<n>\")", impl: true);

            yield return Rule("", "Deterministic SHA256 hash (sysres ID)",
                MappingType.HARDCODED,
                "Separate ID from SHA256(\"sysres:<n>\")", impl: true);

            yield return Rule("", "Namespace: \"Main\"", MappingType.HARDCODED,
                "All injected FBs are placed in the Main namespace.", impl: true);

            yield return Rule("", "sysres Mapping = syslay FB ID",
                MappingType.HARDCODED,
                "sysres Mapping attribute references the matching syslay instance.", impl: true);

            yield return Rule("", "Layout position (x, y) auto calculated",
                MappingType.HARDCODED,
                "Non overlapping grid positions per type group. Actuators left of sensors.", impl: true);

            yield return Rule("", "Five_State_Actuator_CAT: actuator_name parameter",
                MappingType.HARDCODED,
                "Written into Five_State_Actuator_CAT Parameter element.", impl: true);

            yield return Rule("", "Process1_CAT: Text parameter (state name array)",
                MappingType.HARDCODED,
                "Written into Process1_CAT Parameter element.", impl: true);
        }

        public static IEnumerable<MappingRuleEntry> GetRelevantRules(
            bool hasActuator, bool hasSensor, bool hasProcess) => GetAllRules();
    }
}