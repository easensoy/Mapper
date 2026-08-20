using System;
using System.Collections.Generic;
using System.Linq;
using static CodeGen.Translation.Process.Recipes.TransitionChainParser;
using CodeGen.Configuration;
using CodeGen.Models;
using CodeGen.Mapping;

namespace CodeGen.Translation.Process
{
    // Six parallel recipe arrays for ProcessRuntime_Generic_v1's ECC. StepType 1=CMD/2=WAIT/9=END; Wait1Id from in-scope sensors+actuators (out-of-scope conditions skipped).
    public sealed class RecipeArrays
    {
        public List<int> StepType       { get; } = new();
        public List<string> CmdTargetName { get; } = new();
        public List<int> CmdStateArr    { get; } = new();
        public List<int> Wait1Id        { get; } = new();
        public List<int> Wait1State     { get; } = new();
        public List<int> NextStep       { get; } = new();

        // ComponentID -> local id (sensors first, actuators next). Process is NOT in this map.
        public Dictionary<string, int> ComponentIds { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        // TELEMETRY ONLY, parallel to the six control arrays: a 1-based ordinal per row naming the VueOne
        // process state that owns it. Deliberately NOT the twin's State_Number, which is a broadcast slot
        // filled in only where a peer watches, so most states carry 0 and numbers repeat across phases.
        public List<int> ProcessStateByRow { get; } = new();

        // TELEMETRY ONLY. ordinal -> the state's name in the twin; emitted beside the project, never read by the runtime.
        public Dictionary<int, string> ProcessPhaseNames { get; } = new();


        public List<string> Warnings { get; } = new();

        public List<string> TransitionTable { get; } = new();

        public string OrderingSummary { get; set; } = string.Empty;

        public int Count => StepType.Count;
    }

    public static class ProcessRecipeArrayGenerator
    {
        public static int RecipeArraySize => GenerationConfig.Current.RecipeArraySize;

        // The same slots StateTableAllocation assigned, keyed by ComponentID. A projection, NOT a second
        // allocation: recipe Wait1Id, interlock SourceID and actuator_id are one number by construction.
        internal static Dictionary<string, int> ScopedIds(
            StationContents contents, IReadOnlyDictionary<string, int> slots)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in contents.Sensors.Concat(contents.Actuators))
            {
                if (string.IsNullOrEmpty(c.ComponentID)) continue;
                if (slots.TryGetValue(c.Name.Trim(), out int slot))
                    map[c.ComponentID.Trim()] = slot;
            }
            return map;
        }

        // Everything topological is decided before this runs, so compiling a recipe is a pure function.
        internal static RecipeArrays Generate(VueOneComponent process, int processId,
            Recipes.ProcessCompiler.Ctx inputs, Recipes.ProcessHandoffPlan handoffs)
        {
            var arrays = Recipes.ProcessCompiler.Compile(process, processId, inputs, handoffs);

            ValidateProcessIdInvariant(arrays, processId);
            ValidateSingleEndMarker(arrays);
            // EAE silently truncates a recipe past ArraySize -> the engine stalls on StepType=0.
            if (arrays.StepType.Count > RecipeArraySize)
                throw new InvalidOperationException(
                    $"[Recipe] Recipe length {arrays.StepType.Count} exceeds template ArraySize " +
                    $"{RecipeArraySize} (Process1_Generic.fbt / ProcessRuntime_Generic_v1.fbt).");
            return arrays;
        }

        private static void ValidateProcessIdInvariant(RecipeArrays arrays, int processId)
        {
            // Only a WAIT row reads Wait1Id; on every other row it carries the unset default, which
            // is a real slot the moment a process is allocated one low in the table.
            for (int i = 0; i < arrays.Wait1Id.Count; i++)
            {
                if (arrays.StepType[i] == StepType.Wait && arrays.Wait1Id[i] == processId)
                    throw new InvalidOperationException(
                        $"Recipe generator emitted Wait1Id[{i}]={processId} which equals " +
                        $"the Process FB's process_id ({processId}). Process is not a ring " +
                        "participant and cannot publish its own wait state. Likely cause: " +
                        "a stray ComponentID in Control.xml conditions landed on the process_id " +
                        "value via the registry. Inspect ComponentIds.");
            }
        }

        private static void ValidateSingleEndMarker(RecipeArrays arrays)
        {
            // StepType=9 exactly once and only at the final row (else the ECC halts early).
            int n = arrays.StepType.Count;
            if (n == 0)
                throw new InvalidOperationException("Recipe generator produced an empty StepType array.");

            for (int i = 0; i < n - 1; i++)
            {
                if (arrays.StepType[i] == StepType.End)
                    throw new InvalidOperationException(
                        $"Recipe generator emitted StepType[{i}]=9 (END) before the final row. " +
                        $"Array length={n}; END must appear only at index {n - 1}. Likely cause: " +
                        "an out-of-scope-skip path is still emitting placeholder END rows.");
            }
            if (arrays.StepType[n - 1] != StepType.End)
                throw new InvalidOperationException(
                    $"Recipe generator did not append a final END row — StepType[{n - 1}]=" +
                    $"{arrays.StepType[n - 1]}, expected 9.");
        }


    }
}