using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Models;

namespace CodeGen.Translation
{
    /// <summary>
    /// Phase 2: emits six parallel arrays (StepType, CmdTargetName, CmdStateArr,
    /// Wait1Id, Wait1State, NextStep) consumed by ProcessRuntime_Generic_v1's ECC.
    ///
    /// Encoding: 1 = CMD, 2 = WAIT, 9 = END.
    ///
    /// Unfold rule:
    ///   * Initialisation state                               -> dropped from recipe
    ///   * Transition target is an Actuator                    -> emit CMD then WAIT
    ///       CmdTargetName = actuator's canonical Name
    ///       CmdStateArr   = Wait1State - 1   (rig convention: transient is one less than static)
    ///       Wait1Id       = registry id of actuator
    ///       Wait1State    = actuator's resolved condition state number
    ///   * Transition target is a Sensor or Process            -> emit single WAIT
    ///       (collapsed if identical to the previously emitted WAIT — handles
    ///        cases like TransferReturned vs TransferReturning waiting on the
    ///        same condition)
    ///   * Transition is missing or has no condition           -> emit single END
    ///   * Always append a final END row (loops NextStep back to 0)
    ///
    /// Component registry: built from the union of all conditions referenced by
    /// the process's transitions, in first-encounter order. Sensors/actuators/
    /// processes share the same id space starting at 0. The local process is
    /// not in the registry — it has its own process_id parameter on the FB.
    ///
    /// Critical bug fix vs Phase 1: the previous implementation built the
    /// component map from <see cref="StationContents"/> (filtered by Button 2
    /// scope to Feeder + PartInHopper). When a transition referenced a
    /// Checker/Transfer/Assembly_Station that the scope filter had stripped,
    /// the lookup returned 0 silently. We now look up against the full
    /// allComponents list, with a normalised (trim + OrdinalIgnoreCase) key
    /// comparison.
    /// </summary>
    public sealed class RecipeArrays
    {
        public List<int> StepType       { get; } = new();
        public List<string> CmdTargetName { get; } = new();
        public List<int> CmdStateArr    { get; } = new();
        public List<int> Wait1Id        { get; } = new();
        public List<int> Wait1State     { get; } = new();
        public List<int> NextStep       { get; } = new();

        /// <summary>Local id chosen for the Feeder/Pusher actuator (or 0 if neither
        /// found). Kept for diagnostic continuity; recipe semantics no longer depend on it.</summary>
        public int PusherId { get; set; }

        /// <summary>Diagnostic registry: ComponentID → local id. Useful for tests
        /// to verify Wait1Id values point at the right component.</summary>
        public Dictionary<string, int> ComponentRegistry { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Warnings emitted during generation (lookup misses, fallback
        /// classification, etc.). Surfaced to the operator.</summary>
        public List<string> Warnings { get; } = new();

        public int Count => StepType.Count;
    }

    public static class ProcessRecipeArrayGenerator
    {
        // Action-verb patterns that suggest a state name describes an actuator action,
        // used as a soft hint by the fallback path. Real classification is structural
        // (transition target's Type), not name-based.
        private static readonly string[] ActionVerbs = new[]
        {
            "Advancing", "Returning", "Lowering", "Rising",
            "GoUp", "GoDown", "Picking", "Placing", "Gripping", "Opening",
            "Checking", "Extending", "Retracting",
        };

        public static StationComponentMap BuildComponentMap(StationContents contents)
            => ProcessRecipeStGenerator.BuildComponentMap(contents);

        public static RecipeArrays Generate(VueOneComponent process,
            StationContents stationContents, IReadOnlyList<VueOneComponent> allComponents)
        {
            var arrays = new RecipeArrays();
            var states = process.States.OrderBy(s => s.StateNumber).ToList();

            // 1. Build extended registry from this process's own conditions.
            //    First-encounter order, normalised key, OrdinalIgnoreCase.
            BuildExtendedRegistry(process, arrays.ComponentRegistry);

            // Diagnostic PusherId: legacy field, set if Feeder or Pusher in registry.
            arrays.PusherId =
                arrays.ComponentRegistry.FirstOrDefault(kv =>
                    LookupComponent(kv.Key, allComponents) is { } c &&
                    (NameEquals(c.Name, "Feeder") || NameEquals(c.Name, "Pusher"))).Value;

            // 2. Emit rows. Initialisation state(s) are skipped; final END is always appended.
            int? lastWaitId = null;
            int? lastWaitState = null;

            foreach (var state in states)
            {
                if (IsInitialisationState(state)) continue;

                var trans = state.Transitions.FirstOrDefault();
                var cond  = trans?.Conditions.FirstOrDefault(c => !string.IsNullOrEmpty(c.ComponentID));

                if (trans == null || cond == null)
                {
                    AppendEnd(arrays);
                    continue;
                }

                var target = LookupComponent(cond.ComponentID, allComponents);
                if (target == null)
                {
                    arrays.Warnings.Add(
                        $"State '{state.Name}': condition references ComponentID '{cond.ComponentID}' " +
                        "which was not found in allComponents. Emitting WAIT with id=0.");
                    EmitSingleWait(arrays, waitId: 0, waitState: 0, ref lastWaitId, ref lastWaitState);
                    continue;
                }

                int waitId    = arrays.ComponentRegistry.TryGetValue(cond.ComponentID, out var rid) ? rid : 0;
                int waitState = ResolveStateNumber(cond, target);
                var kind      = ClassifyComponentKind(target, state.Name);

                // Even when the first condition's target is an Actuator, a CMD+WAIT
                // unfold is only correct when the source state actually represents an
                // action on that actuator (e.g. "FeederAdvancing", "PartChecking").
                // States like "WaitingReleaseSt2" or "HandShake" happen to reference an
                // actuator's static state in their multi-condition sync but are not
                // commanding the actuator — they must remain a single WAIT.
                bool stateNameSuggestsAction =
                    StateNameSuggestsAction(state.Name, target.Name);

                switch (kind)
                {
                    case ComponentKind.Actuator when stateNameSuggestsAction:
                        EmitCmdThenWait(
                            arrays,
                            actuatorName: (target.Name ?? string.Empty).Trim(),
                            cmdState: Math.Max(waitState - 1, 0),
                            waitId: waitId,
                            waitState: waitState);
                        lastWaitId = waitId;
                        lastWaitState = waitState;
                        break;

                    case ComponentKind.Actuator:
                        // Multi-condition sync waiting on an actuator's static state — not a command.
                        EmitSingleWait(arrays, waitId, waitState, ref lastWaitId, ref lastWaitState);
                        break;

                    case ComponentKind.Sensor:
                    case ComponentKind.Process:
                        EmitSingleWait(arrays, waitId, waitState, ref lastWaitId, ref lastWaitState);
                        break;

                    case ComponentKind.Unknown:
                    default:
                        // Soft fallback: if the state name matches an action verb, treat it
                        // as an actuator action; otherwise WAIT.
                        bool actionVerb = ActionVerbs.Any(v =>
                            (state.Name ?? string.Empty).Contains(v, StringComparison.OrdinalIgnoreCase));
                        if (actionVerb)
                        {
                            arrays.Warnings.Add(
                                $"State '{state.Name}': target '{target.Name}' (Type '{target.Type}') " +
                                "is not classified as Actuator/Sensor/Process; falling back to CMD+WAIT " +
                                "based on action-verb name match.");
                            EmitCmdThenWait(arrays,
                                actuatorName: (target.Name ?? string.Empty).Trim(),
                                cmdState: Math.Max(waitState - 1, 0),
                                waitId: waitId,
                                waitState: waitState);
                            lastWaitId = waitId;
                            lastWaitState = waitState;
                        }
                        else
                        {
                            EmitSingleWait(arrays, waitId, waitState, ref lastWaitId, ref lastWaitState);
                        }
                        break;
                }
            }

            // 3. Always append an explicit END row at the end of the recipe.
            AppendEnd(arrays);

            // 4. Recompute NextStep so each non-END row points at the next index, and the
            //    final END row loops back to 0 (matches the runtime ECC's expectation).
            FixupNextStep(arrays);

            return arrays;
        }

        // -----------------------------------------------------------------
        // Row-emission helpers
        // -----------------------------------------------------------------

        private static void EmitCmdThenWait(RecipeArrays a,
            string actuatorName, int cmdState, int waitId, int waitState)
        {
            // CMD row (StepType=1). Wait1Id/Wait1State are 0 — runtime reads only
            // CmdTargetName/CmdStateArr on a CMD row.
            a.StepType.Add(1);
            a.CmdTargetName.Add(actuatorName);
            a.CmdStateArr.Add(cmdState);
            a.Wait1Id.Add(0);
            a.Wait1State.Add(0);
            a.NextStep.Add(0);   // populated by FixupNextStep

            // Following WAIT row (StepType=2).
            a.StepType.Add(2);
            a.CmdTargetName.Add(string.Empty);
            a.CmdStateArr.Add(0);
            a.Wait1Id.Add(waitId);
            a.Wait1State.Add(waitState);
            a.NextStep.Add(0);
        }

        private static void EmitSingleWait(RecipeArrays a,
            int waitId, int waitState, ref int? lastWaitId, ref int? lastWaitState)
        {
            // Collapse: skip a WAIT that's identical to the immediately-preceding WAIT
            // (handles patterns like TransferReturned waiting on the same condition as
            // TransferReturning's WAIT row).
            if (lastWaitId == waitId && lastWaitState == waitState)
                return;

            a.StepType.Add(2);
            a.CmdTargetName.Add(string.Empty);
            a.CmdStateArr.Add(0);
            a.Wait1Id.Add(waitId);
            a.Wait1State.Add(waitState);
            a.NextStep.Add(0);

            lastWaitId = waitId;
            lastWaitState = waitState;
        }

        private static void AppendEnd(RecipeArrays a)
        {
            a.StepType.Add(9);
            a.CmdTargetName.Add(string.Empty);
            a.CmdStateArr.Add(0);
            a.Wait1Id.Add(0);
            a.Wait1State.Add(0);
            a.NextStep.Add(0);
        }

        private static void FixupNextStep(RecipeArrays a)
        {
            for (int i = 0; i < a.NextStep.Count - 1; i++)
                a.NextStep[i] = i + 1;
            if (a.NextStep.Count > 0)
                a.NextStep[a.NextStep.Count - 1] = 0;   // END loops back to row 0
        }

        // -----------------------------------------------------------------
        // Component lookup + classification
        // -----------------------------------------------------------------

        private enum ComponentKind { Actuator, Sensor, Process, Unknown }

        private static ComponentKind ClassifyComponentKind(VueOneComponent target, string? sourceStateName)
        {
            var t = (target.Type ?? string.Empty).Trim();
            if (t.Contains("Actuator", StringComparison.OrdinalIgnoreCase)) return ComponentKind.Actuator;
            if (t.Contains("Process",  StringComparison.OrdinalIgnoreCase)) return ComponentKind.Process;
            if (t.Contains("Sensor",   StringComparison.OrdinalIgnoreCase)) return ComponentKind.Sensor;
            // Binary state machine (2 states) is treated as a sensor.
            if (target.States.Count == 2) return ComponentKind.Sensor;
            return ComponentKind.Unknown;
        }

        private static VueOneComponent? LookupComponent(string componentId,
            IReadOnlyList<VueOneComponent> all)
        {
            if (string.IsNullOrEmpty(componentId)) return null;
            var key = componentId.Trim();
            return all.FirstOrDefault(c =>
                string.Equals((c.ComponentID ?? string.Empty).Trim(), key,
                    StringComparison.OrdinalIgnoreCase));
        }

        private static int ResolveStateNumber(VueOneCondition cond, VueOneComponent target)
        {
            if (string.IsNullOrEmpty(cond.ID)) return 0;
            var key = cond.ID.Trim();
            var refState = target.States.FirstOrDefault(s =>
                string.Equals((s.StateID ?? string.Empty).Trim(), key,
                    StringComparison.OrdinalIgnoreCase));
            return refState?.StateNumber ?? 0;
        }

        private static void BuildExtendedRegistry(VueOneComponent process,
            Dictionary<string, int> registry)
        {
            int next = 0;
            foreach (var s in process.States.OrderBy(x => x.StateNumber))
                foreach (var t in s.Transitions)
                    foreach (var c in t.Conditions)
                    {
                        if (string.IsNullOrEmpty(c.ComponentID)) continue;
                        var key = c.ComponentID.Trim();
                        if (registry.ContainsKey(key)) continue;
                        registry[key] = next++;
                    }
        }

        private static bool IsInitialisationState(VueOneState s)
        {
            // Most reliable signal — InitialState attribute set in Control.xml.
            if (s.InitialState) return true;
            // Fallback by name (works once SystemXmlReader.ParseState fallback is in place).
            var n = (s.Name ?? string.Empty).Trim();
            return n.Equals("Initialisation", StringComparison.OrdinalIgnoreCase) ||
                   n.Equals("Initialization", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True if the source state's name plausibly refers to an action on the
        /// given actuator: contains the actuator's name (case-insensitive) OR
        /// contains a known motion verb (Advancing, Returning, …). When state.Name
        /// is empty (legacy parser bug or sparse Control.xml), falls back to true
        /// so we don't silently drop valid CMD rows — the operator gets a recipe
        /// at the cost of an occasional spurious unfold.
        /// </summary>
        private static bool StateNameSuggestsAction(string? stateName, string? actuatorName)
        {
            var n = (stateName ?? string.Empty).Trim();
            if (n.Length == 0) return true;
            if (!string.IsNullOrEmpty(actuatorName) &&
                n.Contains(actuatorName, StringComparison.OrdinalIgnoreCase))
                return true;
            return ActionVerbs.Any(v => n.Contains(v, StringComparison.OrdinalIgnoreCase));
        }

        private static bool NameEquals(string? a, string b) =>
            string.Equals((a ?? string.Empty).Trim(), b, StringComparison.OrdinalIgnoreCase);
    }
}
