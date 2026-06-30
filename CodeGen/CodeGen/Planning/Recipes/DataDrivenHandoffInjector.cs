using System.Collections.Generic;
using CodeGen.Configuration;
using CodeGen.Models;

namespace CodeGen.Translation.Process
{
    /// <summary>
    /// DataDrivenRecipes path. The generic Control.xml walk has already DERIVED the Assembly_Station
    /// and Disassembly MOTION from the model state machine (bearing/shaft/covers, and covers/shaft/
    /// bearing/discharge). The model genuinely cannot express the CROSS-STATION handoffs:
    /// <list type="bullet">
    ///   <item>the Feed→Assembly material gate is on the cross-PLC Transfer / PartAtAssembly, which the
    ///   M580-local walk drops as out-of-scope;</item>
    ///   <item>the Assembly↔Disassembly handshake is a sentinel a process can't publish for itself
    ///   (the engine has no self-state-publish), so the walk drops the cross-process wait too.</item>
    /// </list>
    /// This injector wraps the walk's derived motion with EXACTLY those handoffs and nothing else — so
    /// the recipe stays fully model-driven (no hardcoded motion), it just gains the two signals the
    /// twin can't carry. Reuses the same HandoffPlanner gate and the same recipes.yml "handshake"
    /// blocks the proven hardcoded path uses, so the injected rows are identical to the proven ones.
    /// </summary>
    internal static class DataDrivenHandoffInjector
    {
        public static void InjectAssembly(VueOneComponent process,
            RecipeArrays arrays, IReadOnlyList<VueOneComponent> allComponents)
        {
            if (!NameIs(process, "Assembly_Station")) return;

            var motion = SnapshotMotion(arrays);
            ClearRows(arrays);
            var b = new RecipeBuilder(arrays);

            // Row 0 — Feed→Assembly material gate (HandoffPlanner: PartAtAssembly across the
            // cross-device segment when discharge is active, else the M580-local BearingSensor).
            var s = HandoffPlanner.AssemblyStart;
            if (s.WaitId >= 0)
                b.AddWait(s.WaitId, s.WaitState);
            else if (ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, s.SignalComponent, out var matId))
                b.AddWait(matId, s.WaitState);

            ReplayMotion(b, motion);

            // Assembly→Disassembly handshake sentinel (publishes {process_id, 7}) so Disassembly's
            // wait clears. Only when Disassembly actually runs.
            if (MapperConfig.UnparkDisassembly)
                RecipeStepEmitter.Emit(b, RecipeConfigLoader.Catalog.Recipe("Assembly_Station").Block("handshake"),
                    arrays, allComponents);

            b.AddEnd(b.Count); // NextStep finalized by the run-once/cyclic pass in Generate.

            arrays.Warnings.Add("[Recipe] DataDriven Assembly_Station: motion DERIVED from the Control.xml " +
                "walk; only the Feed→Assembly material gate and the Assembly→Disassembly handshake " +
                "sentinel are injected (the cross-station handoffs the model cannot express).");
        }

        public static void InjectDisassembly(VueOneComponent process,
            RecipeArrays arrays, IReadOnlyList<VueOneComponent> allComponents)
        {
            if (!NameIs(process, "Disassembly")) return;
            if (!MapperConfig.UnparkDisassembly) return;

            var motion = SnapshotMotion(arrays);
            ClearRows(arrays);
            var b = new RecipeBuilder(arrays);

            // Row 0 — wait on the Assembly handshake sentinel ({AssemblyProcessId, 7}); the model's
            // cross-process start gate the walk drops.
            RecipeStepEmitter.Emit(b, RecipeConfigLoader.Catalog.Recipe("Disassembly").Block("handshake"),
                arrays, allComponents);

            ReplayMotion(b, motion);

            b.AddEnd(b.Count); // NextStep finalized by the run-once/cyclic pass in Generate.

            arrays.Warnings.Add("[Recipe] DataDriven Disassembly: motion DERIVED from the Control.xml walk; " +
                "only the Assembly→Disassembly handshake WAIT is prepended (the cross-process start " +
                "gate the model cannot express).");
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────────

        private static bool NameIs(VueOneComponent p, string name) =>
            string.Equals((p.Name ?? string.Empty).Trim(), name, System.StringComparison.OrdinalIgnoreCase);

        /// <summary>Every recipe row the walk produced EXCEPT its trailing END (we re-add END after).</summary>
        private static List<(int St, string Cmd, int Cs, int Wid, int Wst)> SnapshotMotion(RecipeArrays a)
        {
            var rows = new List<(int, string, int, int, int)>();
            for (int i = 0; i < a.StepType.Count; i++)
            {
                if (a.StepType[i] == StepType.End) continue;
                rows.Add((a.StepType[i], a.CmdTargetName[i], a.CmdStateArr[i], a.Wait1Id[i], a.Wait1State[i]));
            }
            return rows;
        }

        private static void ClearRows(RecipeArrays a)
        {
            a.StepType.Clear(); a.CmdTargetName.Clear(); a.CmdStateArr.Clear();
            a.Wait1Id.Clear(); a.Wait1State.Clear(); a.NextStep.Clear();
        }

        private static void ReplayMotion(RecipeBuilder b, List<(int St, string Cmd, int Cs, int Wid, int Wst)> rows)
        {
            foreach (var r in rows)
            {
                if (r.St == StepType.Cmd) b.AddCmd(r.Cmd, r.Cs);
                else if (r.St == StepType.Wait) b.AddWait(r.Wid, r.Wst);
            }
        }
    }
}
