using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Models;

namespace CodeGen.Translation
{
    public class ProcessStepTableResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;

        public string StepType { get; set; } = string.Empty;
        public string CmdTarget { get; set; } = string.Empty;
        public string CmdState { get; set; } = string.Empty;
        public string WaitComp { get; set; } = string.Empty;
        public string WaitState { get; set; } = string.Empty;
        public string NextStep { get; set; } = string.Empty;
        public string CompNames { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public int NumSteps { get; set; }
        public int NumComps { get; set; }

        public List<string> StepDescriptions { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }

    public static class ProcessStepTableGenerator
    {
        public static ProcessStepTableResult Generate(
            VueOneComponent process,
            List<VueOneComponent> allComponents)
        {
            var result = new ProcessStepTableResult();

            try
            {
                var registry = BuildComponentRegistry(process, allComponents);

                var orderedStates = process.States
                    .OrderBy(s => s.StateNumber)
                    .ToList();

                if (orderedStates.Count == 0)
                {
                    result.ErrorMessage = $"Process '{process.Name}' has no states.";
                    return result;
                }

                var stepTypes = new List<int>();
                var cmdTargets = new List<string>();
                var cmdStates = new List<int>();
                var waitComps = new List<int>();
                var waitStates = new List<int>();
                var nextSteps = new List<int>();
                var texts = new List<string>();

                var stateIdToIndex = new Dictionary<string, int>();
                for (int i = 0; i < orderedStates.Count; i++)
                    stateIdToIndex[orderedStates[i].StateID] = i;

                for (int stepIdx = 0; stepIdx < orderedStates.Count; stepIdx++)
                {
                    var state = orderedStates[stepIdx];
                    var row = ProcessOneState(
                        state, stepIdx, orderedStates.Count,
                        registry, allComponents, stateIdToIndex);

                    stepTypes.Add(row.StepType);
                    cmdTargets.Add(row.CmdTargetName);
                    cmdStates.Add(row.CmdState);
                    waitComps.Add(row.WaitCompId);
                    waitStates.Add(row.WaitStateVal);
                    nextSteps.Add(row.NextStepIdx);
                    texts.Add(state.Name);

                    result.StepDescriptions.Add(
                        $"Step {stepIdx}: {row.TypeLabel} " +
                        $"comp={row.WaitCompId} state={row.WaitStateVal} " +
                        $"cmd='{row.CmdTargetName}'->{row.CmdState} next={row.NextStepIdx} " +
                        $"({state.Name})");

                    if (!string.IsNullOrEmpty(row.Warning))
                        result.Warnings.Add($"Step {stepIdx} ({state.Name}): {row.Warning}");
                }

                result.StepType = FormatIntArray(stepTypes);
                result.CmdTarget = FormatStringArray(cmdTargets);
                result.CmdState = FormatIntArray(cmdStates);
                result.WaitComp = FormatIntArray(waitComps);
                result.WaitState = FormatIntArray(waitStates);
                result.NextStep = FormatIntArray(nextSteps);
                result.CompNames = FormatStringArray(
                    registry.OrderBy(kv => kv.Value.CompId)
                            .Select(kv => kv.Value.Name)
                            .ToList());
                result.Text = FormatStringArray(texts);
                result.NumSteps = orderedStates.Count;
                result.NumComps = registry.Count;
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        private static Dictionary<string, RegistryEntry> BuildComponentRegistry(
            VueOneComponent process,
            List<VueOneComponent> allComponents)
        {
            var registry = new Dictionary<string, RegistryEntry>();
            int nextId = 0;

            foreach (var state in process.States)
            {
                foreach (var trans in state.Transitions)
                {
                    foreach (var cond in trans.Conditions)
                    {
                        if (string.IsNullOrEmpty(cond.ComponentID))
                            continue;

                        if (cond.ComponentID == process.ComponentID)
                            continue;

                        if (!registry.ContainsKey(cond.ComponentID))
                        {
                            var comp = allComponents.FirstOrDefault(
                                c => c.ComponentID == cond.ComponentID);

                            string name = comp?.Name ?? ExtractComponentName(cond.Name);
                            string type = comp?.Type ?? "Unknown";

                            registry[cond.ComponentID] = new RegistryEntry
                            {
                                CompId = nextId++,
                                Name = name,
                                Type = type,
                                ComponentID = cond.ComponentID
                            };
                        }
                    }
                }
            }

            return registry;
        }

        private static StepRow ProcessOneState(
            VueOneState state,
            int stepIdx,
            int totalSteps,
            Dictionary<string, RegistryEntry> registry,
            List<VueOneComponent> allComponents,
            Dictionary<string, int> stateIdToIndex)
        {
            var row = new StepRow();

            var transition = state.Transitions.FirstOrDefault();

            if (transition == null || !transition.Conditions.Any())
            {
                row.StepType = 9;
                row.TypeLabel = "END";
                row.NextStepIdx = 0;
                return row;
            }

            var condition = transition.Conditions
                .FirstOrDefault(c => !string.IsNullOrEmpty(c.ComponentID));

            if (condition == null)
            {
                row.StepType = 9;
                row.TypeLabel = "END";
                row.Warning = "No valid condition found.";
                row.NextStepIdx = 0;
                return row;
            }

            var targetComp = allComponents.FirstOrDefault(
                c => c.ComponentID == condition.ComponentID);

            string targetType = targetComp?.Type ?? "Unknown";

            if (stateIdToIndex.TryGetValue(transition.DestinationStateID, out int nextIdx))
                row.NextStepIdx = nextIdx;
            else
                row.NextStepIdx = (stepIdx + 1) % totalSteps;

            if (registry.TryGetValue(condition.ComponentID, out var regEntry))
                row.WaitCompId = regEntry.CompId;

            row.WaitStateVal = ResolveStateNumber(condition, targetComp);

            if (IsActuator(targetType))
            {
                row.StepType = 1;
                row.TypeLabel = "CMD";
                row.CmdTargetName = targetComp?.Name ?? ExtractComponentName(condition.Name);
                row.CmdState = InferCommandState(row.WaitStateVal);
            }
            else
            {
                row.StepType = 2;
                row.TypeLabel = "WAIT";
            }

            if (transition.Conditions.Count > 1)
            {
                row.Warning = $"Compound condition ({transition.Conditions.Count} conditions). " +
                              "Only the first is mapped. Remaining conditions are not enforced.";
            }

            return row;
        }

        private static int ResolveStateNumber(
            VueOneCondition condition,
            VueOneComponent? targetComp)
        {
            if (targetComp != null)
            {
                var targetState = targetComp.States
                    .FirstOrDefault(s => s.StateID == condition.ID);

                if (targetState != null)
                    return targetState.StateNumber;
            }

            string stateName = ExtractStateName(condition.Name);
            return InferStateNumberFromName(stateName);
        }

        private static int InferStateNumberFromName(string stateName)
        {
            string lower = stateName.ToLowerInvariant();

            if (lower == "on") return 1;
            if (lower == "off") return 0;

            if (lower.Contains("returnedhome") || lower.Contains("returnedfinished") ||
                lower.Contains("athome") || lower.Contains("home"))
                return 4;

            if (lower.Contains("advanced") || lower.Contains("risingfinished") ||
                lower.Contains("atwork") || lower.Contains("work"))
                return 2;

            if (lower.Contains("advancing") || lower.Contains("towork") ||
                lower.Contains("rising"))
                return 1;

            if (lower.Contains("returning") || lower.Contains("tohome") ||
                lower.Contains("falling"))
                return 3;

            return 0;
        }

        private static int InferCommandState(int doneState)
        {
            if (doneState <= 0) return 0;
            return doneState - 1;
        }

        private static string FormatIntArray(List<int> values)
        {
            return "[" + string.Join(",", values) + "]";
        }

        private static string FormatStringArray(List<string> values)
        {
            var quoted = values.Select(v => $"'{v}'");
            return "[" + string.Join(",", quoted) + "]";
        }

        private static string ExtractComponentName(string conditionName)
        {
            if (string.IsNullOrEmpty(conditionName)) return string.Empty;
            int slash = conditionName.IndexOf('/');
            return slash > 0 ? conditionName[..slash] : conditionName;
        }

        private static string ExtractStateName(string conditionName)
        {
            if (string.IsNullOrEmpty(conditionName)) return string.Empty;
            int slash = conditionName.IndexOf('/');
            return slash >= 0 && slash < conditionName.Length - 1
                ? conditionName[(slash + 1)..]
                : conditionName;
        }

        private static bool IsActuator(string type)
        {
            return string.Equals(type, "Actuator", StringComparison.OrdinalIgnoreCase);
        }

        private class RegistryEntry
        {
            public int CompId { get; set; }
            public string Name { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
            public string ComponentID { get; set; } = string.Empty;
        }

        private class StepRow
        {
            public int StepType { get; set; }
            public string TypeLabel { get; set; } = string.Empty;
            public string CmdTargetName { get; set; } = string.Empty;
            public int CmdState { get; set; }
            public int WaitCompId { get; set; }
            public int WaitStateVal { get; set; }
            public int NextStepIdx { get; set; }
            public string Warning { get; set; } = string.Empty;
        }
    }
}
