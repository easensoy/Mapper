using System;
using System.Linq;
using CodeGen.Domain.Twin;
using CodeGen.Mapping;
using static CodeGen.Translation.Process.Recipes.TransitionChainParser;

namespace CodeGen.Translation.Process.Recipes
{
    // Wiring topology, not recipe: do the per-controller report rings fold into one?
    //
    // The phase transport carries a station's cycle BOUNDARIES -- a producer announces that it has begun a
    // cycle and that it has reached the phase a peer waits on -- and the material sensor on the cross-ring
    // segment covers the other direction's arrival. Both are handshakes at the edges of a cycle, which is
    // all a station needs when it hands material on and is then done with it.
    //
    // A Feed-side process that waits on a process hosted by another controller in the MIDDLE of its own
    // cycle is stating something else: it is holding material for the downstream station and must observe
    // that station live while it holds it. A boundary handshake cannot express that, so the two rings have
    // to be one and every announcement becomes directly readable. Derived from the transition chain and the
    // controller allocation; no process name and no model shape participates.
    public static class FeedRingMerge
    {
        public static bool Needed(TwinModel twin, ControllerAllocation allocation)
        {
            foreach (var proc in twin.Processes)
            {
                if (!allocation.IsFeedSide(proc.Name)) continue;

                var chain = OrderStatesByTransitionChain(proc.Source.States);
                // Index 0 is the cycle entry and the last link is where the cycle closes; a dependency at
                // either is a boundary handshake the phase transport already carries.
                for (int i = 1; i < chain.Count - 1; i++)
                    if (WaitsOnAnotherController(chain[i], proc, twin, allocation))
                        return true;
            }
            return false;
        }

        private static bool WaitsOnAnotherController(Models.VueOneState state, TwinComponent proc,
            TwinModel twin, ControllerAllocation allocation) =>
            state.Transitions
                .SelectMany(t => t.Conditions)
                .Select(c => twin.ById(c.ComponentID))
                .Any(target => target != null
                    && target.IsProcess
                    && !string.Equals(target.Name, proc.Name, StringComparison.OrdinalIgnoreCase)
                    && allocation.Of(target.Name) != allocation.Of(proc.Name));
    }
}
