using System.Collections.Generic;
using CodeGen.Mapping;

namespace CodeGen.Translation.Interlocks
{
    // Writes an actuator's target-state params: the encapsulated Target : TargetStates struct or the legacy
    // scalar TargetWork1State/TargetWork2State/TargetHomeState, driven by interlock.yaml useTargetStruct.
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
            if (InterlockConfig.Current.UseTargetStruct)
            {
                p["Target"] = Iec61499Literal.FormatTargetStates(work1, work2 ?? 0, home);
            }
            else
            {
                p["TargetWork1State"] = Iec61499Literal.FormatInt(work1);
                if (work2.HasValue) p["TargetWork2State"] = Iec61499Literal.FormatInt(work2.Value);
                p["TargetHomeState"] = Iec61499Literal.FormatInt(home);
            }
        }
    }
}
