using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Models;
using CodeGen.Devices.Core;
using CodeGen.Mapping;
using static CodeGen.Translation.Process.Recipes.TransitionChainParser;
using static CodeGen.Translation.Process.Recipes.RecipeComponentLookup;

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
        public Dictionary<string, int> ComponentRegistry { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> SkippedConditions { get; } = new();

        public List<string> Warnings { get; } = new();

        public List<string> TransitionTable { get; } = new();

        public string OrderingSummary { get; set; } = string.Empty;

        public int Count => StepType.Count;
    }

    public static class ProcessRecipeArrayGenerator
    {
        public static int RecipeArraySize => GenerationConfig.Current.RecipeArraySize;

        // Sensors first (ids 0..N-1), actuators next (ids N..N+M-1). Process is NOT in the map.
        public static Dictionary<string, int> BuildScopedComponentMap(
            IReadOnlyList<VueOneComponent> allowedSensors,
            IReadOnlyList<VueOneComponent> allowedActuators)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int next = 0;
            foreach (var s in allowedSensors)
            {
                if (string.IsNullOrEmpty(s.ComponentID)) continue;
                map[s.ComponentID.Trim()] = next++;
            }
            foreach (var a in allowedActuators)
            {
                if (string.IsNullOrEmpty(a.ComponentID)) continue;
                map[a.ComponentID.Trim()] = next++;
            }
            return map;
        }

        public static RecipeArrays Generate(VueOneComponent process,
            StationContents stationContents, IReadOnlyList<VueOneComponent> allComponents,
            int processId = 10)
        {
            var scopedRegistry = BuildScopedComponentMap(stationContents.Sensors, stationContents.Actuators);
            var arrays = Recipes.ProcessCompiler.Compile(process, processId,
                BuildCompilerCtx(allComponents, scopedRegistry));

            ValidateProcessIdInvariant(arrays, processId);
            ValidateSingleEndMarker(arrays);
            // EAE silently truncates a recipe past ArraySize -> the engine stalls on StepType=0.
            if (arrays.StepType.Count > RecipeArraySize)
                throw new InvalidOperationException(
                    $"[Recipe] Recipe length {arrays.StepType.Count} exceeds template ArraySize " +
                    $"{RecipeArraySize} (Process1_Generic.fbt / ProcessRuntime_Generic_v1.fbt).");
            return arrays;
        }

        private static Recipes.ProcessCompiler.Ctx BuildCompilerCtx(
            IReadOnlyList<VueOneComponent> all, IReadOnlyDictionary<string, int> scopedIds)
        {
            var cat = RigCatalog.Current;
            var pids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Feed_Station"] = MapperConfig.FeedStationProcessId,
                ["Assembly_Station"] = MapperConfig.AssemblyProcessId,
                ["Disassembly"] = MapperConfig.DisassemblyProcessId,
            };
            var present = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var si in cat.SensorInterlocks) present[si.Sensor] = si.PresentState;
            // Deployment-allocated slots override the local positional ones: these components report on a slot the
            // injector pinned (cross-controller covers/robot/synth sensors, and the top-cover sensor whose slot is
            // computed per ring topology). A recipe that waited on the positional id would read a different
            // component's slot entirely.
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in cat.CoverActuatorIds) byName[kv.Key] = kv.Value;
            foreach (var s in cat.SynthSensors) byName[s.Name] = s.Id;
            byName["Robot"] = cat.RobotActuatorId;
            foreach (var n in TemplateMap.TopCoverSensorNames) byName[n] = MapperConfig.TopCoverSensorId;
            // The material bridge is whichever synthesised sensor rides the cross-controller ring segment: that
            // membership is what makes its level readable on the far controller, so it is taken from the topology
            // rather than named here. A merged (no-clamp) ring announces every process directly and needs none.
            var bridge = cat.SynthSensors.FirstOrDefault(s =>
                cat.CrossRingSegment.Any(n => string.Equals(n, s.Name, StringComparison.OrdinalIgnoreCase)));
            return new Recipes.ProcessCompiler.Ctx
            {
                Ids = scopedIds,
                IdsByName = byName,
                All = all,
                ProcessIdByName = pids,
                SensorPresent = present,
                FeedProcessId = MapperConfig.FeedStationProcessId,
                MergeFeedRing = Recipes.FeedRingMerge.Needed(all),
                MaterialBridgeId = bridge?.Id ?? -1,
            };
        }

        internal static bool TryGetComponentId(RecipeArrays arrays,
            IReadOnlyList<VueOneComponent> allComponents, string componentName, out int id)
        {
            foreach (var kv in arrays.ComponentRegistry)
            {
                var comp = LookupComponent(kv.Key, allComponents);
                if (comp != null &&
                    string.Equals((comp.Name ?? string.Empty).Trim(), componentName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    id = kv.Value;
                    return true;
                }
            }

            id = -1;
            return false;
        }

        private static void ValidateProcessIdInvariant(RecipeArrays arrays, int processId)
        {
            for (int i = 0; i < arrays.Wait1Id.Count; i++)
            {
                if (arrays.Wait1Id[i] == processId)
                    throw new InvalidOperationException(
                        $"Recipe generator emitted Wait1Id[{i}]={processId} which equals " +
                        $"the Process FB's process_id ({processId}). Process is not a ring " +
                        "participant and cannot publish its own wait state. Likely cause: " +
                        "a stray ComponentID in Control.xml conditions landed on the process_id " +
                        "value via the registry. Inspect ComponentRegistry / SkippedConditions.");
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