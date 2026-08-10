using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Hmi
{
    // Decides what an operator may actually be offered.
    //
    // The rules themselves live in hmi.yml; this class only applies them. Three independent gates,
    // all of which must pass before a command is drawn:
    //   1. the contract declares the output event carrying the right data  (can we send it at all?)
    //   2. the contract declares feedback that proves acceptance           (could we show it landed?)
    //   3. the controller can receive it - mode-chain reachability, and, for recipe stepping, an
    //      engine whose ECC actually reads the port
    // Failing any of them yields a capability marked unsupported WITH the reason, so the view planner
    // renders an honest "unavailable because ..." instead of a control that does nothing.
    //
    // Discovery is by signature only: no branch here looks at a component or CAT name, and no
    // capability, event, data word or feedback word is written down in C#.
    internal static class HmiCapabilityResolver
    {
        internal static IReadOnlyList<HmiCapability> Resolve(
            HmiContract contract,
            string instanceName,
            HmiModeReach reach,
            HmiEngineSupport engine,
            HmiDefinition def)
        {
            var result = new List<HmiCapability>();

            if (!contract.Exists)
            {
                result.Add(new HmiCapability(HmiCapabilityPurpose.Monitor, string.Empty,
                    Array.Empty<string>(), Array.Empty<string>(), false, HmiUnavailableReason.NoContract));
                return result;
            }

            // Monitoring needs no output event - a contract that exists can always be displayed.
            result.Add(new HmiCapability(HmiCapabilityPurpose.Monitor, string.Empty,
                Array.Empty<string>(), Array.Empty<string>(), true, HmiUnavailableReason.None));

            // Interlock explanation is a pure FEEDBACK capability: it commands nothing, so the
            // read-only policy never suppresses it. It needs the blocker identity, not just a flag.
            var interlock = def.InterlockFeedback;
            var hasInterlock = interlock.Count > 0 && interlock.All(contract.HasFeedback);
            result.Add(new HmiCapability(HmiCapabilityPurpose.InterlockDiagnostics, string.Empty,
                interlock, interlock, hasInterlock,
                hasInterlock ? HmiUnavailableReason.None : HmiUnavailableReason.NoAcceptedFeedback));

            foreach (var rule in def.Capabilities)
                result.Add(Evaluate(rule, contract, instanceName, reach, engine, def.ReadOnly));

            return result;
        }

        private static HmiCapability Evaluate(
            HmiCapabilityRule rule, HmiContract contract, string instanceName,
            HmiModeReach reach, HmiEngineSupport engine, bool readOnly)
        {
            // A descriptor may offer several accepted data shapes (a two- versus three-position jog);
            // the widest one the contract actually carries wins, which is also what tells the two
            // apart without counting states or reading a name.
            var shapes = rule.OutputDataVariants.Count > 0
                ? rule.OutputDataVariants.OrderByDescending(v => v.Count).ToList()
                : new List<IReadOnlyList<string>> { rule.OutputData };

            var data = shapes.FirstOrDefault(s => contract.OutputCarries(rule.OutputEvent, s.ToArray()))
                       ?? shapes[^1];
            var carries = contract.Output(rule.OutputEvent) != null &&
                          (data.Count == 0 || contract.OutputCarries(rule.OutputEvent, data.ToArray()));

            var reason =
                !carries ? HmiUnavailableReason.NoOutputEvent
                : rule.AlsoRequiresOutputEvent != null && contract.Output(rule.AlsoRequiresOutputEvent) == null
                    ? HmiUnavailableReason.NoOutputEvent
                : !rule.AnyFeedback.Any(contract.HasFeedback) ? HmiUnavailableReason.NoAcceptedFeedback
                : rule.NeedsRecipeEngine && !EngineHonours(rule.Purpose, engine)
                    ? HmiUnavailableReason.EngineDoesNotImplement
                : rule.NeedsModeChain && !reach.ReachesThrough(instanceName)
                    ? HmiUnavailableReason.ModeChainUnreachable
                : readOnly ? HmiUnavailableReason.ReadOnlyPolicy
                : HmiUnavailableReason.None;

            return new HmiCapability(rule.Purpose, rule.OutputEvent, data, rule.AnyFeedback,
                                     reason == HmiUnavailableReason.None, reason);
        }

        // Which descriptors depend on the engine is declared by the descriptor itself
        // (needsRecipeEngine); this only picks which engine fact answers it.
        private static bool EngineHonours(HmiCapabilityPurpose purpose, HmiEngineSupport engine) =>
            purpose == HmiCapabilityPurpose.ManualStep ? engine.ManualStepping : engine.AutoGating;
    }
}
