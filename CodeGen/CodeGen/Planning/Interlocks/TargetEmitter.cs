using System.Collections.Generic;

namespace CodeGen.Translation.Interlocks
{
    // Writes an actuator's target-state params: the encapsulated Target : TargetStates struct or the legacy
    // scalar TargetWork1State/TargetWork2State/TargetHomeState, driven by interlock.yaml useTargetStruct.
    public static class TargetEmitter
    {
        // work2 is null for Five_State (no TargetWork2State scalar input); in struct mode it is 0.
        public static void Apply(Dictionary<string, string> p, int work1, int? work2, int home)
        {
            if (InterlockConfig.Current.UseTargetStruct)
            {
                p["Target"] = SyslayBuilder.FormatTargetStates(work1, work2 ?? 0, home);
            }
            else
            {
                p["TargetWork1State"] = SyslayBuilder.FormatInt(work1);
                if (work2.HasValue) p["TargetWork2State"] = SyslayBuilder.FormatInt(work2.Value);
                p["TargetHomeState"] = SyslayBuilder.FormatInt(home);
            }
        }
    }
}
