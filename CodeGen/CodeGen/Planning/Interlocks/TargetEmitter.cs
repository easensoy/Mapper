using System.Collections.Generic;
using CodeGen.Mapping;

namespace CodeGen.Translation.Interlocks
{
    // Writes an actuator's target-state param: one Target : TargetStates, the shape the CAT exposes and
    // the evaluator compares against.
    public static class TargetEmitter
    {
        // Written only where the CAT declares a target map. A stop the CAT does not declare has no
        // scalar input; in struct mode the struct carries every member, so absent means 0.
        public static void Apply(Dictionary<string, string> p, CatProtocol? protocol)
        {
            var t = protocol?.Target;
            if (t == null || t.Count == 0) return;
            int work1 = t.TryGetValue(CatProtocol.Work1, out var w1) ? w1 : 0;
            int home  = t.TryGetValue(CatProtocol.Home,  out var h)  ? h  : 0;
            int? work2 = t.TryGetValue(CatProtocol.Work2, out var w2) ? w2 : (int?)null;
            p["Target"] = Iec61499Literal.FormatTargetStates(work1, work2 ?? 0, home);
        }
    }
}
