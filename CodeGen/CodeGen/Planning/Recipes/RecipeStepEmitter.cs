using System;
using System.Collections.Generic;
using CodeGen.Configuration;
using CodeGen.Models;

namespace CodeGen.Translation.Process
{
    // Writes a recipe block's rows into RecipeArrays via RecipeBuilder. Each step must set EXACTLY ONE
    // of cmd / waitId / waitRef / waitConfig. END rows are NOT emitted here — the station owns AddEnd.
    internal static class RecipeStepEmitter
    {
        public static void Emit(RecipeBuilder b, IReadOnlyList<RecipeStepDefinition> steps,
            RecipeArrays arrays, IReadOnlyList<VueOneComponent> allComponents)
        {
            foreach (var s in steps)
            {
                int discriminators =
                    (s.Cmd != null ? 1 : 0) +
                    (s.WaitId.HasValue ? 1 : 0) +
                    (s.WaitRef != null ? 1 : 0) +
                    (s.WaitConfig != null ? 1 : 0);
                if (discriminators != 1)
                    throw new InvalidOperationException(
                        "[Recipe] recipe step must set EXACTLY ONE of cmd / waitId / waitRef / " +
                        $"waitConfig (saw {discriminators}).");

                if (s.Cmd != null)
                {
                    b.AddCmd(s.Cmd, s.State);
                }
                else if (s.WaitId.HasValue)
                {
                    b.AddWait(s.WaitId.Value, s.State);
                }
                else if (s.WaitRef != null)
                {
                    if (!ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, s.WaitRef, out var id))
                        throw new InvalidOperationException(
                            $"[Recipe] waitRef '{s.WaitRef}' did not resolve to a component id. The " +
                            "station class must gate the block on id resolution before emitting it.");
                    b.AddWait(id, s.State);
                }
                else // WaitConfig
                {
                    b.AddWait(ResolveConfigId(s.WaitConfig!), s.State);
                }
            }
        }

        private static int ResolveConfigId(string key) => key switch
        {
            "AssemblyProcessId"    => MapperConfig.AssemblyProcessId,
            "DisassemblyProcessId" => MapperConfig.DisassemblyProcessId,
            "RobotActuatorId"      => MapperConfig.RobotActuatorId,
            _ when RigCatalog.Current.CoverActuatorIds.TryGetValue(key, out var id) => id,
            _ => throw new InvalidOperationException(
                $"[Recipe] unknown waitConfig key '{key}' (expected a process id, RobotActuatorId, " +
                "or a cover actuator id from smc-rig.yml).")
        };
    }
}
