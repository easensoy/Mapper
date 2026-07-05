using System.Collections.Generic;
using CodeGen.Configuration;
using CodeGen.Models;

namespace CodeGen.Translation.Process
{
    // DataDrivenRecipes path: the Control.xml walk derives Assembly/Disassembly MOTION; this injector
    // wraps it with ONLY the two cross-station handoffs the model can't express (Feed->Assembly
    // material gate on the cross-PLC Transfer/PartAtAssembly; Assembly<->Disassembly handshake sentinel).
    internal static class DataDrivenHandoffInjector
    {
        public static void InjectAssembly(VueOneComponent process,
            RecipeArrays arrays, IReadOnlyList<VueOneComponent> allComponents)
        {
            if (!NameIs(process, "Assembly_Station")) return;

            var motion = SnapshotMotion(arrays);
            ClearRows(arrays);
            var b = new RecipeBuilder(arrays);

            var s = HandoffPlanner.AssemblyStart;
            if (s.WaitId >= 0)
                b.AddWait(s.WaitId, s.WaitState);
            else if (ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, s.SignalComponent, out var matId))
                b.AddWait(matId, s.WaitState);

            ReplayMotion(b, motion);

            if (MapperConfig.UnparkDisassembly)
                RecipeStepEmitter.Emit(b, RecipeConfigLoader.Catalog.Recipe("Assembly_Station").Block("handshake"),
                    arrays, allComponents);

            b.AddEnd(b.Count);

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

            RecipeStepEmitter.Emit(b, RecipeConfigLoader.Catalog.Recipe("Disassembly").Block("handshake"),
                arrays, allComponents);

            ReplayMotion(b, motion);

            b.AddEnd(b.Count);

            arrays.Warnings.Add("[Recipe] DataDriven Disassembly: motion DERIVED from the Control.xml walk; " +
                "only the Assembly→Disassembly handshake WAIT is prepended (the cross-process start " +
                "gate the model cannot express).");
        }

        private static bool NameIs(VueOneComponent p, string name) =>
            string.Equals((p.Name ?? string.Empty).Trim(), name, System.StringComparison.OrdinalIgnoreCase);

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
