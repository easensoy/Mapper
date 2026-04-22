using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Models;

namespace CodeGen.Translation
{
    public static class ProcessCatGenerator
    {
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

        public static List<ProcessTransition> ExtractTransitions(VueOneComponent processComponent)
        {
            throw new NotImplementedException(
                "Transition extraction not yet implemented. Planned: parse Sequence_Condition and Interlock_Condition from Control.xml per state.");
        }

        public static string GenerateEccXml(ProcessCatDefinition def)
        {
            throw new NotImplementedException(
                "ECC XML generation not yet implemented. Planned: emit ECState and ECTransition elements driven by ProcessTransition list.");
        }

        public static Dictionary<string, string> BuildParameters(ProcessCatDefinition def)
        {
            var parameters = new Dictionary<string, string>();

            var names = def.States.OrderBy(s => s.StateNumber).Select(s => $"'{s.Name}'").ToList();
            int pad = Math.Max(0, 14 - names.Count);
            if (pad > 0) names.Add($"{pad}('')");
            parameters["Text"] = "[" + string.Join(",", names) + "]";

            return parameters;
        }

        public static bool FitsDefaultTemplate(ProcessCatDefinition def, int templateStateCapacity = 14)
        {
            return def.StateCount <= templateStateCapacity;
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
    }
}
