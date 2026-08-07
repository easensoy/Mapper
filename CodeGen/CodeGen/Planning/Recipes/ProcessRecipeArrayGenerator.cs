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

        // TELEMETRY ONLY. Parallel to the six control arrays: for each recipe row, a 1-based ordinal
        // identifying the VueOne process state that owns it. A state compiles to several rows, so
        // consecutive entries repeat. Nothing in the control path reads this — it exists so the engine
        // can publish the phase the model names rather than CurrentStep, which is a compiled row index.
        //
        // The ordinal is deliberately NOT the twin's State_Number. State_Number is a broadcast slot
        // announcing a phase to a peer process, so the twin only fills it in where a peer is watching:
        // most states carry 0 and a process reuses the same number for unrelated phases. That is correct
        // for handshakes and useless for telling phases apart, which is what telemetry needs.
        public List<int> ProcessStateByRow { get; } = new();

        // TELEMETRY ONLY. ordinal -> the state's name in the twin, so a subscriber can render the phase
        // rather than a bare number. Emitted alongside the generated project; never read by the runtime.
        public Dictionary<int, string> ProcessPhaseNames { get; } = new();

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
                // PartAtAssembly holds a RESERVED slot (the one the synth injection uses), so it must not
                // consume a positional one -- otherwise a twin that declares it pushes every actuator up by
                // one and the topmost actuator lands on the Assembly process_id.
                if (HandoffPlanner.IsPartAtAssembly(s.Name))
                {
                    map[s.ComponentID.Trim()] = HandoffPlanner.PartAtAssembly.Id;
                    continue;
                }
                map[s.ComponentID.Trim()] = next++;
            }
            foreach (var a in allowedActuators)
            {
                if (string.IsNullOrEmpty(a.ComponentID)) continue;
                map[a.ComponentID.Trim()] = next++;
            }
            return map;
        }

        // The model's process-to-process handoffs with their transports resolved, for the backend to render
        // wiring from. Derived from the same Ctx the recipe rows are compiled from, so the wiring and the
        // WAIT rows can never disagree about which producer reaches which consumer.
        internal static Recipes.ProcessHandoffPlan HandoffPlan(IReadOnlyList<VueOneComponent> allComponents) =>
            Recipes.ProcessCompiler.HandoffPlan(
                BuildCompilerCtx(allComponents, new Dictionary<string, int>()));

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
            // The covers are NOT listed here. They are ordinary components the injector numbers positionally, and
            // that positional number is exactly what it writes as their actuator_id -- so the scoped map already
            // gives the slot they report on. A static override could only ever be right for one layout: pinning
            // them to 14/15/16 matches the clamp models, but a no-clamp model has no Clamp to occupy a slot, so
            // its covers land on 13/14/15 and every cover WAIT read the NEXT component's slot. That is why a
            // no-clamp Assembly commanded coverpnp_vr and then waited on coverpnp_gripper, which nothing had yet
            // been told to move, and the cover sequence stopped dead with the gripper never gripping.
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