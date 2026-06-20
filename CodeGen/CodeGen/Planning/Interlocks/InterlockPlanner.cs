using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Models;

namespace CodeGen.Translation.Interlocks
{
    public static class InterlockPlanner
    {
        // Control.xml STATE <Interlock_Condition> elements -> InterlockManager rule arrays: each
        // condition blocks the state's From->To transition while the source holds the blocking state.
        // Source ids use the sensors-first scoped map; out-of-scope and home-rest rules are dropped.
        public static InterlockPlan BuildRules(VueOneComponent actuator,
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int> scopedIds)
        {
            int cap = GenerationConfig.Current.InterlockRuleCap;
            var from = new int[cap];
            var to = new int[cap];
            var src = new int[cap];
            var blk = new int[cap];
            int n = 0;

            foreach (var st in actuator.States)
            {
                if (st.InterlockConditions.Count == 0) continue;

                int toState = -1;
                foreach (var tr in st.Transitions)
                {
                    var dest = (tr.DestinationStateID ?? string.Empty).Trim();
                    if (dest.Length == 0) continue;
                    var ds = actuator.States.FirstOrDefault(s =>
                        string.Equals((s.StateID ?? string.Empty).Trim(), dest,
                            StringComparison.OrdinalIgnoreCase));
                    if (ds != null) { toState = ds.StateNumber; break; }
                }

                foreach (var c in st.InterlockConditions)
                {
                    var key = (c.ComponentID ?? string.Empty).Trim();
                    if (key.Length == 0) continue;
                    if (!scopedIds.TryGetValue(key, out var srcId)) continue;

                    var srcComp = allComponents.FirstOrDefault(x =>
                        string.Equals((x.ComponentID ?? string.Empty).Trim(), key,
                            StringComparison.OrdinalIgnoreCase));
                    int blockedState = srcComp?.States.FirstOrDefault(s =>
                        string.Equals((s.StateID ?? string.Empty).Trim(),
                            (c.ID ?? string.Empty).Trim(),
                            StringComparison.OrdinalIgnoreCase))?.StateNumber ?? -1;

                    if (toState < 0 || blockedState < 0) continue;
                    if (n >= cap) break;

                    // RuleFromState = the resting predecessor state the FB sees at REQ time (a rule
                    // matches only when CurrentRawState == RuleFromState).
                    var ownStateId = (st.StateID ?? string.Empty).Trim();
                    int fromState = st.StateNumber;
                    if (ownStateId.Length > 0)
                    {
                        var predecessor = actuator.States.FirstOrDefault(p =>
                            p.Transitions.Any(t =>
                                string.Equals(
                                    (t.DestinationStateID ?? string.Empty).Trim(),
                                    ownStateId, StringComparison.OrdinalIgnoreCase)));
                        if (predecessor != null)
                            fromState = predecessor.StateNumber;
                    }

                    // Home-family State_Number 4 publishes only momentarily; remap to the stable 0.
                    int blockedStateRuntime = blockedState;
                    if (blockedState == 4 && srcComp != null &&
                        string.Equals(srcComp.Type, "Actuator", StringComparison.OrdinalIgnoreCase))
                        blockedStateRuntime = 0;

                    // Drop "block-while-source-is-home" (Blocked==0): a source at rest is out of the
                    // crossing, so blocking on it is an inverted rule that deadlocks the recipe.
                    if (blockedStateRuntime == 0) continue;

                    from[n] = fromState;
                    to[n] = toState;
                    src[n] = srcId;
                    blk[n] = blockedStateRuntime;
                    n++;
                }
            }
            return new InterlockPlan(n, from, to, src, blk);
        }

        // Count of in-scope interlock conditions that survive the same drops BuildRules applies,
        // for the deploy-time guard (in-scope conditions present but RuleCount==0 => abort).
        public static int CountInScopeConditions(VueOneComponent actuator,
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int> scopedIds)
        {
            int n = 0;
            foreach (var st in actuator.States)
            foreach (var c in st.InterlockConditions)
            {
                var key = (c.ComponentID ?? string.Empty).Trim();
                if (key.Length == 0 || !scopedIds.ContainsKey(key)) continue;

                var srcComp = allComponents.FirstOrDefault(x =>
                    string.Equals((x.ComponentID ?? string.Empty).Trim(), key,
                        StringComparison.OrdinalIgnoreCase));
                int blockedState = srcComp?.States.FirstOrDefault(s =>
                    string.Equals((s.StateID ?? string.Empty).Trim(),
                        (c.ID ?? string.Empty).Trim(),
                        StringComparison.OrdinalIgnoreCase))?.StateNumber ?? -1;
                if (blockedState < 0) continue;
                if (blockedState == 4 && srcComp != null &&
                    string.Equals(srcComp.Type, "Actuator", StringComparison.OrdinalIgnoreCase))
                    blockedState = 0;
                if (blockedState == 0) continue;
                n++;
            }
            return n;
        }
    }
}
