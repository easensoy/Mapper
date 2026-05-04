using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CodeGen.Models;

namespace CodeGen.Translation
{
    public class StationComponentMap
    {
        public Dictionary<string, int> ComponentIdToLocalId { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ComponentNameToLocalId { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static class ProcessRecipeStGenerator
    {
        public static StationComponentMap BuildComponentMap(StationContents contents)
        {
            var map = new StationComponentMap();
            int next = 0;
            foreach (var s in contents.Sensors)
            {
                map.ComponentIdToLocalId[s.ComponentID] = next;
                map.ComponentNameToLocalId[s.Name] = next;
                next++;
            }
            foreach (var a in contents.Actuators)
            {
                map.ComponentIdToLocalId[a.ComponentID] = next;
                map.ComponentNameToLocalId[a.Name] = next;
                next++;
            }
            return map;
        }

        public static string GenerateInitializeInitSt(VueOneComponent process,
            StationContents stationContents, IReadOnlyList<VueOneComponent> allComponents)
        {
            var map = BuildComponentMap(stationContents);
            var states = process.States.OrderBy(s => s.StateNumber).ToList();
            var stateIdToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < states.Count; i++)
                stateIdToIndex[states[i].StateID] = i;

            var sb = new StringBuilder();
            sb.AppendLine("CurrentStep := 0;");
            sb.AppendLine("CurrentStepType := 0;");
            sb.AppendLine("WaitSatisfied := FALSE;");
            sb.AppendLine();

            if (map.ComponentNameToLocalId.TryGetValue("Feeder", out var feederId))
                sb.AppendLine($"PusherID := {feederId};");
            else if (map.ComponentNameToLocalId.TryGetValue("Pusher", out var pusherId))
                sb.AppendLine($"PusherID := {pusherId};");
            else
                sb.AppendLine("PusherID := 0;");
            sb.AppendLine();

            for (int i = 0; i < states.Count; i++)
            {
                var state = states[i];
                int stepKind = ClassifyStep(state, states, i);

                int nextIdx = ResolveNextStep(state, stateIdToIndex, i, states.Count);

                switch (stepKind)
                {
                    case 1:
                        var (target, cmdState) = ExtractCommandTarget(state, allComponents, map, stateIdToIndex);
                        sb.AppendLine($"StepType[{i}] := 1;");
                        sb.AppendLine($"CmdTargetName[{i}] := '{target}';");
                        sb.AppendLine($"CmdStateArr[{i}] := {cmdState};");
                        sb.AppendLine($"NextStep[{i}] := {nextIdx};");
                        break;
                    case 2:
                        var (waitId, waitState) = ExtractWaitTarget(state, allComponents, map);
                        sb.AppendLine($"StepType[{i}] := 2;");
                        sb.AppendLine($"Wait1Id[{i}] := {waitId};");
                        sb.AppendLine($"Wait1State[{i}] := {waitState};");
                        sb.AppendLine($"NextStep[{i}] := {nextIdx};");
                        break;
                    case 9:
                        sb.AppendLine($"StepType[{i}] := 9;");
                        break;
                }
                sb.AppendLine();
            }

            sb.AppendLine("cmd_target_name := '';");
            sb.AppendLine("cmd_state := 0;");
            sb.AppendLine();
            sb.AppendLine("PreviousStepText := '';");
            sb.AppendLine($"ThisStepText := '{Esc(states.Count > 0 ? states[0].Name : "Initialised")}';");
            sb.AppendLine($"NextStepText := '{Esc(states.Count > 1 ? states[1].Name : "")}';");

            return sb.ToString().TrimEnd() + "\n";
        }

        private static int ClassifyStep(VueOneState state, List<VueOneState> all, int index)
        {
            var trans = state.Transitions.FirstOrDefault();
            if (trans == null) return 9;

            bool hasConditions = trans.Conditions.Any(c => !string.IsNullOrEmpty(c.ComponentID));
            bool isFinal = string.Equals(trans.DestinationStateID,
                all.Count > 0 ? all[0].StateID : string.Empty, StringComparison.OrdinalIgnoreCase)
                && index == all.Count - 1;

            if (isFinal && !hasConditions) return 9;
            if (hasConditions) return 2;
            if (state.Time > 0) return 1;
            return 9;
        }

        private static int ResolveNextStep(VueOneState state, Dictionary<string, int> stateIdToIndex,
            int currentIdx, int total)
        {
            var trans = state.Transitions.FirstOrDefault();
            if (trans == null) return 0;
            if (stateIdToIndex.TryGetValue(trans.DestinationStateID, out var dst))
                return dst;
            return (currentIdx + 1) % total;
        }

        private static (string target, int cmdState) ExtractCommandTarget(VueOneState state,
            IReadOnlyList<VueOneComponent> allComponents, StationComponentMap map,
            Dictionary<string, int> stateIdToIndex)
        {
            var name = state.Name ?? string.Empty;
            string compName = "Pusher";
            foreach (var k in map.ComponentNameToLocalId.Keys)
            {
                if (name.Contains(k, StringComparison.OrdinalIgnoreCase)) { compName = k; break; }
            }
            return (compName.ToLowerInvariant(), 1);
        }

        private static (int waitId, int waitState) ExtractWaitTarget(VueOneState state,
            IReadOnlyList<VueOneComponent> allComponents, StationComponentMap map)
        {
            var trans = state.Transitions.FirstOrDefault();
            if (trans == null) return (0, 0);
            var cond = trans.Conditions.FirstOrDefault(c => !string.IsNullOrEmpty(c.ComponentID));
            if (cond == null) return (0, 0);

            int waitId = map.ComponentIdToLocalId.TryGetValue(cond.ComponentID, out var id) ? id : 0;
            int waitState = ResolveStateNumber(cond, allComponents);
            return (waitId, waitState);
        }

        private static int ResolveStateNumber(VueOneCondition cond, IReadOnlyList<VueOneComponent> all)
        {
            var target = all.FirstOrDefault(c =>
                string.Equals(c.ComponentID, cond.ComponentID, StringComparison.OrdinalIgnoreCase));
            if (target == null) return 0;
            var refState = target.States.FirstOrDefault(s =>
                string.Equals(s.StateID, cond.ID, StringComparison.OrdinalIgnoreCase));
            return refState?.StateNumber ?? 0;
        }

        private static string Esc(string s) => (s ?? string.Empty).Replace("'", "''");
    }
}
