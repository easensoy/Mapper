using CodeGen.Models;
using System.Collections.Generic;
using System.Linq;

namespace MapperUI.Services
{
    /// <summary>
    /// Full mapping rule table sourced from VueOne_IEC61499_Mapping.xlsx.
    /// Returns rows relevant to the component types that were loaded.
    /// </summary>
    public static class MappingRuleEngine
    {
        // ── Full rule catalogue ───────────────────────────────────────────────

        public static readonly IReadOnlyList<MappingRule> AllRules = new List<MappingRule>
        {
            // ── System-level ──────────────────────────────────────────────────
            new() { VueOneElement = "SystemID",               IEC61499Element = "GUID (FBType)",                  Type = MappingType.ASSUMED,     TransformationRule = "Generate new UUID, don't reuse VueOne ID" },
            new() { VueOneElement = "System/Name",            IEC61499Element = "FBType Name (in system file)",   Type = MappingType.TRANSLATED,  TransformationRule = "Copy to system FB Name, not template" },
            new() { VueOneElement = "Version",                IEC61499Element = "N/A",                            Type = MappingType.DISCARDED,   TransformationRule = "Not used in IEC 61499" },
            new() { VueOneElement = "Type",                   IEC61499Element = "N/A",                            Type = MappingType.DISCARDED,   TransformationRule = "System type not mapped" },

            // ── Component-level ───────────────────────────────────────────────
            new() { VueOneElement = "Component/ComponentID",  IEC61499Element = "N/A",                            Type = MappingType.DISCARDED,   TransformationRule = "IEC 61499 uses GUID instead" },
            new() { VueOneElement = "Component/Name",         IEC61499Element = "FB Name (in system file)",       Type = MappingType.TRANSLATED,  TransformationRule = "Maps to FB instance name" },
            new() { VueOneElement = "Component/VcID",         IEC61499Element = "N/A",                            Type = MappingType.DISCARDED,   TransformationRule = "Visual Components metadata" },
            new() { VueOneElement = "Component/Description",  IEC61499Element = "Comment (optional)",             Type = MappingType.ASSUMED,     TransformationRule = "Could map to Comment attribute" },
            new() { VueOneElement = "Component/Library_ID",   IEC61499Element = "N/A",                            Type = MappingType.DISCARDED,   TransformationRule = "VueOne library reference" },
            new() { VueOneElement = "Component/Type",         IEC61499Element = "FB Type reference",              Type = MappingType.TRANSLATED,  TransformationRule = "Actuator → Five_State_Actuator_CAT" },

            // ── State-level ───────────────────────────────────────────────────
            new() { VueOneElement = "State/StateID",          IEC61499Element = "N/A",                            Type = MappingType.DISCARDED,   TransformationRule = "IEC 61499 uses Name only" },
            new() { VueOneElement = "State/Name",             IEC61499Element = "ECState Name (in Actuator.fbt)", Type = MappingType.ENCODED,     TransformationRule = "Maps to ECC state name" },
            new() { VueOneElement = "State/State_Number",     IEC61499Element = "state_val (InputVar)",           Type = MappingType.TRANSLATED,  TransformationRule = "Direct integer mapping" },
            new() { VueOneElement = "State/Initial_State",    IEC61499Element = "ECTransition from START",        Type = MappingType.ENCODED,     TransformationRule = "True → START transition to this state" },
            new() { VueOneElement = "State/Time",             IEC61499Element = "N/A",                            Type = MappingType.ASSUMED,     TransformationRule = "A candidate watchdog value" },
            new() { VueOneElement = "State/Speed",            IEC61499Element = "N/A",                            Type = MappingType.DISCARDED,   TransformationRule = "PLC controls physical speed" },
            new() { VueOneElement = "State/Position",         IEC61499Element = "N/A",                            Type = MappingType.DISCARDED,   TransformationRule = "PLC setpoint, not FB logic" },
            new() { VueOneElement = "State/Operator",         IEC61499Element = "N/A",                            Type = MappingType.DISCARDED,   TransformationRule = "VueOne simulation feature" },
            new() { VueOneElement = "State/Counter",          IEC61499Element = "N/A",                            Type = MappingType.DISCARDED,   TransformationRule = "VueOne counting feature" },
            new() { VueOneElement = "State/StateColour",      IEC61499Element = "N/A",                            Type = MappingType.DISCARDED,   TransformationRule = "Visual representation only" },
            new() { VueOneElement = "State/RobotAxes",        IEC61499Element = "N/A",                            Type = MappingType.DISCARDED,   TransformationRule = "Robot-specific metadata" },
            new() { VueOneElement = "State/StaticState",      IEC61499Element = "Motion state indicator",         Type = MappingType.ENCODED,     TransformationRule = "False=motion, True=position hold" },

            // ── Transition-level ──────────────────────────────────────────────
            new() { VueOneElement = "Transition/TransitionID",      IEC61499Element = "N/A",                      Type = MappingType.DISCARDED,   TransformationRule = "VueOne-specific ID" },
            new() { VueOneElement = "Transition/Type",              IEC61499Element = "N/A",                      Type = MappingType.DISCARDED,   TransformationRule = "VueOne transition classification" },
            new() { VueOneElement = "Transition/Origin_State",      IEC61499Element = "ECTransition Source",      Type = MappingType.ENCODED,     TransformationRule = "StateID lookup → Name" },
            new() { VueOneElement = "Transition/Destination_State", IEC61499Element = "ECTransition Destination", Type = MappingType.ENCODED,     TransformationRule = "StateID lookup → Name" },
            new() { VueOneElement = "Transition/Priority",          IEC61499Element = "N/A",                      Type = MappingType.DISCARDED,   TransformationRule = "VueOne scheduling feature" },

            // ── Sequence / Interlock conditions ───────────────────────────────
            new() { VueOneElement = "Sequence_Condition",           IEC61499Element = "Event connection + guard",  Type = MappingType.ENCODED,    TransformationRule = "Parse condition tree → event wiring" },
            new() { VueOneElement = "ConditionGroup",               IEC61499Element = "AND logic in guard",        Type = MappingType.ENCODED,    TransformationRule = "Multiple conditions ANDed" },
            new() { VueOneElement = "Condition/Operator",           IEC61499Element = "Boolean operator",          Type = MappingType.ENCODED,    TransformationRule = "Empty=TRUE, '-'=NOT" },
            new() { VueOneElement = "Condition/ID",                 IEC61499Element = "Referenced state",          Type = MappingType.ENCODED,    TransformationRule = "StateID of condition source" },
            new() { VueOneElement = "Condition/Name",               IEC61499Element = "Event/state name",          Type = MappingType.ENCODED,    TransformationRule = "Maps to event trigger" },
            new() { VueOneElement = "Condition/ComponentID",        IEC61499Element = "Source FB reference",       Type = MappingType.ENCODED,    TransformationRule = "Which FB fires the event" },
            new() { VueOneElement = "Interlock_Condition",          IEC61499Element = "ECTransition guard",        Type = MappingType.ENCODED,    TransformationRule = "Parse → Boolean expression" },
            new() { VueOneElement = "Interlock/ConditionGroup",     IEC61499Element = "AND logic",                 Type = MappingType.ENCODED,    TransformationRule = "All interlocks must be satisfied" },
            new() { VueOneElement = "Interlock/Condition",          IEC61499Element = "Guard condition element",   Type = MappingType.ENCODED,    TransformationRule = "Component state check" },

            // ── Hardcoded IEC 61499 structure ─────────────────────────────────
            new() { VueOneElement = "N/A", IEC61499Element = "XML declaration",            Type = MappingType.HARDCODED, TransformationRule = "Standard XML preamble" },
            new() { VueOneElement = "N/A", IEC61499Element = "FBType opening tag",         Type = MappingType.HARDCODED, TransformationRule = "Standard structure, GUID varies" },
            new() { VueOneElement = "N/A", IEC61499Element = "Attribute (Configuration)",  Type = MappingType.HARDCODED, TransformationRule = "EAE metadata" },
            new() { VueOneElement = "N/A", IEC61499Element = "Identification Standard",    Type = MappingType.HARDCODED, TransformationRule = "Always 61499-2" },
            new() { VueOneElement = "N/A", IEC61499Element = "VersionInfo",                Type = MappingType.HARDCODED, TransformationRule = "Template metadata" },
            new() { VueOneElement = "N/A", IEC61499Element = "InterfaceList (complete)",   Type = MappingType.HARDCODED, TransformationRule = "Standardized for all 5-state actuators" },
            new() { VueOneElement = "N/A", IEC61499Element = "EventInputs: pst_event",     Type = MappingType.HARDCODED, TransformationRule = "Process state trigger event" },
            new() { VueOneElement = "N/A", IEC61499Element = "EventInputs: action_event",  Type = MappingType.HARDCODED, TransformationRule = "Sensor feedback event" },
            new() { VueOneElement = "N/A", IEC61499Element = "EventInputs: tohome",        Type = MappingType.HARDCODED, TransformationRule = "Emergency return event" },
            new() { VueOneElement = "N/A", IEC61499Element = "EventOutputs: pst_out",      Type = MappingType.HARDCODED, TransformationRule = "State change notification" },
            new() { VueOneElement = "N/A", IEC61499Element = "EventOutputs: plc_out",      Type = MappingType.HARDCODED, TransformationRule = "PLC status output" },
            new() { VueOneElement = "N/A", IEC61499Element = "InputVars (all)",            Type = MappingType.HARDCODED, TransformationRule = "Standard variable declarations" },
            new() { VueOneElement = "N/A", IEC61499Element = "OutputVars (all)",           Type = MappingType.HARDCODED, TransformationRule = "Standard variable declarations" },
            new() { VueOneElement = "N/A", IEC61499Element = "FBNetwork (complete)",       Type = MappingType.HARDCODED, TransformationRule = "Internal wiring structure" },
            new() { VueOneElement = "N/A", IEC61499Element = "FB instances (FB1, FB3)",    Type = MappingType.HARDCODED, TransformationRule = "Standard internal FBs" },
            new() { VueOneElement = "N/A", IEC61499Element = "EventConnections (internal)",Type = MappingType.HARDCODED, TransformationRule = "Standard wiring" },
            new() { VueOneElement = "N/A", IEC61499Element = "DataConnections (internal)", Type = MappingType.HARDCODED, TransformationRule = "Standard wiring" },
        };

        /// <summary>
        /// Returns the subset of rules that are relevant given which component
        /// types were loaded. Robots / unsupported types are filtered.
        /// System-level rules always shown. Component/State rules shown if
        /// any actuators or sensors present. Condition rules shown if any
        /// processes present.
        /// </summary>
        public static IEnumerable<MappingRule> GetRelevantRules(
            bool hasActuators, bool hasSensors, bool hasProcesses)
        {
            foreach (var rule in AllRules)
            {
                var v = rule.VueOneElement;

                // System-level always shown
                if (v.StartsWith("System") || v == "Version" || v == "Type" || v == "SystemID")
                { yield return rule; continue; }

                // Component-level always shown if any components loaded
                if (v.StartsWith("Component/"))
                { yield return rule; continue; }

                // State/Transition rules shown for actuators or sensors
                if ((v.StartsWith("State/") || v.StartsWith("Transition/"))
                    && (hasActuators || hasSensors))
                { yield return rule; continue; }

                // Condition rules shown when processes are present
                if ((v.StartsWith("Sequence") || v.StartsWith("Condition/")
                     || v.StartsWith("Interlock") || v == "ConditionGroup")
                    && hasProcesses)
                { yield return rule; continue; }

                // Hardcoded structure shown when actuators present
                if (v == "N/A" && hasActuators)
                { yield return rule; continue; }
            }
        }
    }
}