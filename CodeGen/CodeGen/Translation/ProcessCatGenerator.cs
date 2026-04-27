using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Models;

namespace CodeGen.Translation
{
    public static class ProcessCatGenerator
    {
        public const int DefaultStepTableCapacity = 14;

        public static ProcessCatDefinition Build(VueOneComponent processComponent)
        {
            var def = new ProcessCatDefinition
            {
                Name = processComponent.Name,
                ComponentId = processComponent.ComponentID,
                StateCount = processComponent.States.Count
            };

            foreach (var state in processComponent.States.OrderBy(s => s.StateNumber))
            {
                def.States.Add(new ProcessState
                {
                    StateNumber = state.StateNumber,
                    Name = state.Name,
                    StateId = state.StateID,
                    IsInitial = state.InitialState,
                    IsStatic = state.StaticState
                });
            }

            return def;
        }

        public static StepTable BuildStepTable(ProcessCatDefinition def, int capacity = DefaultStepTableCapacity)
        {
            var ordered = def.States.OrderBy(s => s.StateNumber).ToList();

            var table = new StepTable
            {
                Capacity = capacity,
                ActualStateCount = ordered.Count,
                Overflow = ordered.Count > capacity
            };

            foreach (var state in ordered.Take(capacity))
                table.Entries.Add(state.Name);

            int pad = Math.Max(0, capacity - table.Entries.Count);
            table.PaddingSlots = pad;

            return table;
        }

        public static string SerializeStepTable(StepTable table)
        {
            var names = table.Entries.Select(n => $"'{n}'").ToList();
            if (table.PaddingSlots > 0)
                names.Add($"{table.PaddingSlots}('')");
            return "[" + string.Join(",", names) + "]";
        }

        public static Dictionary<string, string> BuildParameters(ProcessCatDefinition def, int capacity = DefaultStepTableCapacity)
        {
            var parameters = new Dictionary<string, string>();
            var table = BuildStepTable(def, capacity);
            parameters["Text"] = SerializeStepTable(table);
            parameters["StateCount"] = table.ActualStateCount.ToString();
            return parameters;
        }

        public static bool FitsDefaultTemplate(ProcessCatDefinition def, int capacity = DefaultStepTableCapacity)
        {
            return def.StateCount <= capacity;
        }

        public static List<ProcessTransition> ExtractTransitions(VueOneComponent processComponent)
        {
            var transitions = new List<ProcessTransition>();

            foreach (var state in processComponent.States)
            {
                foreach (var t in state.Transitions)
                {
                    var pt = new ProcessTransition
                    {
                        SourceStateId = t.OriginStateID,
                        DestinationStateId = t.DestinationStateID,
                        Priority = t.Priority
                    };

                    if (t.Conditions.Count > 0)
                    {
                        var first = t.Conditions[0];
                        pt.Condition = first.Name;
                        pt.Wait1ComponentId = first.ComponentID;
                        pt.Wait1StateId = first.ID;
                    }

                    transitions.Add(pt);
                }
            }

            return transitions;
        }

        public static string GenerateEccXml(ProcessCatDefinition def)
        {
            throw new NotImplementedException(
                "ECC XML generation not yet implemented. Planned: emit ECState and ECTransition elements driven by ProcessTransition list.");
        }
    }

    public class ProcessCatDefinition
    {
        public string Name { get; set; } = string.Empty;
        public string ComponentId { get; set; } = string.Empty;
        public int StateCount { get; set; }
        public List<ProcessState> States { get; set; } = new();
    }

    public class ProcessState
    {
        public int StateNumber { get; set; }
        public string Name { get; set; } = string.Empty;
        public string StateId { get; set; } = string.Empty;
        public bool IsInitial { get; set; }
        public bool IsStatic { get; set; }
    }

    public class ProcessTransition
    {
        public string SourceStateId { get; set; } = string.Empty;
        public string DestinationStateId { get; set; } = string.Empty;
        public string Condition { get; set; } = string.Empty;
        public int Priority { get; set; }
        public string Wait1ComponentId { get; set; } = string.Empty;
        public string Wait1StateId { get; set; } = string.Empty;
    }

    public class StepTable
    {
        public int Capacity { get; set; }
        public int ActualStateCount { get; set; }
        public int PaddingSlots { get; set; }
        public bool Overflow { get; set; }
        public List<string> Entries { get; set; } = new();
    }
}
