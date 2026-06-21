using System.Collections.Generic;

namespace CodeGen.Translation.Interlocks
{
    /// <summary>
    /// Writes an actuator's target-state params — either the encapsulated <c>Target : TargetStates</c>
    /// struct (Work1/Work2/Home) or the legacy scalar TargetWork1State/TargetWork2State/TargetHomeState,
    /// driven by <c>interlock.yaml useTargetStruct</c>. The struct flows whole into the interlock
    /// evaluator (a custom FB), so no struct-member connection is needed.
    /// </summary>
    public static class TargetEmitter
    {
        /// <param name="work2">null for Five_State (no TargetWork2State scalar input); in struct mode it is 0.</param>
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
