using System.Collections.Generic;

namespace MapperUI.Services
{
    // ── Mapping types (superset of CodeGen.Models.MappingType) ───────────────
    public enum MappingType
    {
        TRANSLATED,
        DISCARDED,
        ASSUMED,
        ENCODED,
        HARDCODED,
        SECTION     // sentinel — used for visual section-header rows only
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
        /// True  → this rule is currently handled by the Mapper (shows ✓).
        /// False → planned but not yet implemented in this phase (shows ✗).
        /// Ignored for SECTION rows.
        /// </summary>
        public bool IsImplemented { get; init; }
    }

    public static class MappingRuleEngine
    {
        // ── Section helpers ───────────────────────────────────────────────────

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

        // ── Full catalogue ────────────────────────────────────────────────────

        public static IEnumerable<MappingRuleEntry> GetAllRules()
        {
            // ── SECTION 1: System Level ───────────────────────────────────────
            yield return Section("System Level");

            yield return Rule("SystemID", "GUID (FBType)", MappingType.ASSUMED,
                "Generate new UUID — do not reuse VueOne ID", impl: true);

            yield return Rule("System/Name", "FBType Name (in system file)", MappingType.TRANSLATED,
                "Copy to system FB Name, not template", impl: true);

            yield return Rule("Version", "N/A", MappingType.DISCARDED,
                "Not used in IEC 61499", impl: true);

            yield return Rule("Type", "N/A", MappingType.DISCARDED,
                "System type attribute not mapped", impl: true);

            // ── SECTION 2: Component Level ────────────────────────────────────
            yield return Section("Component Level");

            yield return Rule("Component/ComponentID", "N/A", MappingType.DISCARDED,
                "IEC 61499 uses GUID instead", impl: true);

            yield return Rule("Component/Name", "FB Name (in system file)", MappingType.TRANSLATED,
                "Maps directly to FB instance name", impl: true);

            yield return Rule("Component/VcID", "N/A", MappingType.DISCARDED,
                "Visual Components metadata — dropped", impl: true);

            yield return Rule("Component/Description", "Comment (optional)", MappingType.ASSUMED,
                "Could map to Comment attribute — not yet applied", impl: false);

            yield return Rule("Component/Library_ID", "N/A", MappingType.DISCARDED,
                "VueOne library reference — no IEC 61499 equivalent", impl: true);

            yield return Rule("Component/Type", "FB Type reference", MappingType.TRANSLATED,
                "Actuator (5-state) → Five_State_Actuator_CAT\n" +
                "Sensor (2-state) → Sensor_Bool_CAT\n" +
                "Process → Process1_CAT\n" +
                "Robot → Robot_Task_CAT", impl: true);

            // ── SECTION 3: State Level ────────────────────────────────────────
            yield return Section("State Level");

            yield return Rule("State/StateID", "N/A", MappingType.DISCARDED,
                "IEC 61499 references states by Name only", impl: true);

            yield return Rule("State/Name", "ECC State Name", MappingType.TRANSLATED,
                "Maps to ECC state name inside CAT FB", impl: true);

            yield return Rule("State/Number", "State Order", MappingType.ENCODED,
                "Determines state sequence in ECC; used for Text param", impl: true);

            yield return Rule("State/Type (Static/Dynamic)", "N/A", MappingType.DISCARDED,
                "ECC algorithm handles static/dynamic — not mapped", impl: true);

            yield return Rule("State/Duration", "N/A", MappingType.DISCARDED,
                "Timing lives in ECC action algorithms — not mapped", impl: true);

            yield return Rule("State/ErrorMessage", "N/A", MappingType.DISCARDED,
                "Simulation-only field — no IEC 61499 equivalent", impl: true);

            yield return Rule("State/Position", "N/A", MappingType.DISCARDED,
                "Physical position used in VueOne simulation only", impl: true);

            // ── SECTION 4: Transition / Sequence Level ────────────────────────
            yield return Section("Transition / Sequence Level");

            yield return Rule("Sequence_Condition/ProcessName", "EventConnection Source", MappingType.ENCODED,
                "Process name → driving process FB instance", impl: false);

            yield return Rule("Sequence_Condition/StateName", "EventConnection Destination", MappingType.ENCODED,
                "State name → actuator FB event input", impl: false);

            yield return Rule("Sequence_Condition/StateValue", "DataConnection (state_val)", MappingType.ENCODED,
                "State value → data variable binding", impl: false);

            yield return Rule("Interlock_Condition", "N/A", MappingType.DISCARDED,
                "Interlocks not in Phase 1 scope", impl: true);

            yield return Rule("Transition/Condition", "ECC Guard", MappingType.TRANSLATED,
                "Logic condition mapped to ECC transition guard", impl: false);

            // ── SECTION 5: EAE System Specifics ──────────────────────────────
            yield return Section("EAE System Specifics (Hardcoded)");

            yield return Rule("FB Instance ID (syslay)", "Deterministic SHA256 hash", MappingType.HARDCODED,
                "8-byte hex ID from SHA256(\"syslay:<name>\")", impl: true);

            yield return Rule("FB Instance ID (sysres)", "Deterministic SHA256 hash", MappingType.HARDCODED,
                "Separate ID from SHA256(\"sysres:<name>\")", impl: true);

            yield return Rule("Namespace", "\"Main\"", MappingType.HARDCODED,
                "All injected FBs are in the Main namespace", impl: true);

            yield return Rule("Mapping= attribute (sysres)", "syslay FB ID", MappingType.HARDCODED,
                "sysres.Mapping references the matching syslay instance", impl: true);

            yield return Rule("Layout position (x, y)", "Auto-calculated", MappingType.HARDCODED,
                "Non-overlapping grid positions per type group", impl: true);

            yield return Rule("actuator_name parameter", "Component name (lowercase)", MappingType.HARDCODED,
                "Written into Five_State_Actuator_CAT Parameter element", impl: true);

            yield return Rule("Text parameter (Process)", "State name array", MappingType.HARDCODED,
                "Written into Process1_CAT Parameter element", impl: true);
        }

        /// <summary>
        /// Returns rules filtered to the component types present in the loaded XML.
        /// Always includes System/EAE sections; omits component-type-specific rows
        /// when that type is absent.
        /// </summary>
        public static IEnumerable<MappingRuleEntry> GetRelevantRules(
            bool hasActuator, bool hasSensor, bool hasProcess) => GetAllRules();
    }
}