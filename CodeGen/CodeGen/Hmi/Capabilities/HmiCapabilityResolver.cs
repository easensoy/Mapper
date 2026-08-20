using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Hmi
{
    // Decides what an operator may actually be offered.
    //
    // There is no global read-only switch. Every command is judged on its own, and only from what the
    // deployed artefacts prove. Four independent gates, all of which must pass:
    //
    //   1. CAN WE SEND IT?      the HMI contract declares the output event carrying the right data
    //   2. CAN WE SHOW IT?      the contract declares feedback that proves acceptance
    //   3. DOES IT ARRIVE?      the generated application delivers it (mode-chain reachability)
    //   4. IS IT HONOURED?      some deployed ECC actually guards on the value
    //
    // Gate 4 is the one that matters most and the easiest to skip. Area_CAT declares Mode, CycleType
    // and Fault_Reset; ProcessRuntime declares Mode, MREQ and NSREQ. Judging on gates 1-3 alone would
    // offer STOP and manual stepping, and both buttons would be inert. A port no ECC reads is a port
    // the controller ignores.
    //
    // Failing any gate yields a capability marked unsupported WITH a GENERATED reason naming the
    // element that was missing, so the operator is told why rather than shown a dead control.
    //
    // Discovery is by signature only: no branch here looks at a component or CAT name, and no
    // capability, event, data word, feedback word or ECC token is written down in C#.
    internal static class HmiCapabilityResolver
    {
        internal static IReadOnlyList<HmiCapability> Resolve(
            HmiContract contract,
            string instanceName,
            HmiModeReach reach,
            HmiEccIndex ecc,
            HmiDefinition def)
        {
            var result = new List<HmiCapability>();

            if (!contract.Exists)
            {
                result.Add(new HmiCapability(HmiCapabilityPurpose.Monitor, string.Empty,
                    Array.Empty<string>(), false, HmiUnavailableReason.NoContract, def.NoContractNotice));
                return result;
            }

            // Monitoring needs no output event - a contract that exists can always be displayed.
            result.Add(new HmiCapability(HmiCapabilityPurpose.Monitor, string.Empty,
                Array.Empty<string>(), true, HmiUnavailableReason.None, string.Empty));

            // Interlock explanation is a pure FEEDBACK capability: it commands nothing, so it is
            // judged on feedback alone. It needs the blocker identity, not just a flag.
            var interlock = def.InterlockFeedback;
            var missingFeedback = interlock.Where(f => !contract.HasFeedback(f)).ToList();
            var hasInterlock = interlock.Count > 0 && missingFeedback.Count == 0;
            result.Add(new HmiCapability(HmiCapabilityPurpose.InterlockDiagnostics, string.Empty,
                interlock, hasInterlock,
                hasInterlock ? HmiUnavailableReason.None : HmiUnavailableReason.NoAcceptedFeedback,
                hasInterlock ? string.Empty
                    : $"the contract exposes no {string.Join("/", missingFeedback)} feedback, " +
                      "so a blocked transition could not be explained"));

            foreach (var rule in def.Capabilities)
                result.Add(Evaluate(rule, contract, instanceName, reach, ecc, def));

            return result;
        }

        private static HmiCapability Evaluate(
            HmiCapabilityRule rule, HmiContract contract, string instanceName,
            HmiModeReach reach, HmiEccIndex ecc, HmiDefinition def)
        {
            // A descriptor may offer several accepted data shapes (a two- versus three-position jog);
            // the widest one the contract actually carries wins, which is also what tells the two
            // apart without counting states or reading a name.
            var shapes = rule.OutputData.OrderByDescending(v => v.Count).ToList();

            var data = shapes.FirstOrDefault(s => contract.OutputCarries(rule.OutputEvent, s.ToArray()))
                       ?? shapes[^1];
            var carries = contract.Output(rule.OutputEvent) != null &&
                          (data.Count == 0 || contract.OutputCarries(rule.OutputEvent, data.ToArray()));

            var (reason, detail) = Judge(rule, contract, instanceName, reach, ecc, carries, data, def);

            return new HmiCapability(rule.Purpose, rule.OutputEvent, data,
                                     reason == HmiUnavailableReason.None, reason, detail);
        }

        // The gates, in the order that gives the most specific answer: a contract that cannot send it
        // at all is a more useful message than one about reachability further downstream.
        private static (HmiUnavailableReason, string) Judge(
            HmiCapabilityRule rule, HmiContract contract, string instanceName,
            HmiModeReach reach, HmiEccIndex ecc, bool carries, IReadOnlyList<string> data,
            HmiDefinition def)
        {
            if (!carries)
                return (HmiUnavailableReason.NoOutputEvent,
                    contract.Output(rule.OutputEvent) == null
                        ? $"the deployed HMI contract declares no output event '{rule.OutputEvent}'"
                        : $"output event '{rule.OutputEvent}' does not carry {string.Join(", ", data)}");

            if (rule.AlsoRequiresOutputEvent != null && contract.Output(rule.AlsoRequiresOutputEvent) == null)
                return (HmiUnavailableReason.NoOutputEvent,
                    $"the deployed HMI contract declares no output event '{rule.AlsoRequiresOutputEvent}'");

            if (!rule.AnyFeedback.Any(contract.HasFeedback))
                return (HmiUnavailableReason.NoAcceptedFeedback,
                    $"the contract exposes none of {string.Join("/", rule.AnyFeedback)}, " +
                    "so acceptance could not be shown");

            if (rule.NeedsModeChain && !reach.ReachesThrough(instanceName))
                return (HmiUnavailableReason.ModeChainUnreachable,
                    $"the station mode chain does not reach '{instanceName}'");

            // The decisive gate. Restricting the search to one deployed type is what separates
            // "the station tracks the cycle" from "the recipe engine honours a STOP".
            var c = rule.Consumption;
            var unread = c.Tokens.Where(tok => !ecc.Consumes(tok, c.InType)).ToList();

            // An alternative clause is satisfied by any one of its tokens; if none is guarded on,
            // report them together so the reason names the whole set that was looked for.
            if (unread.Count == 0 && c.AnyOf.Count > 0 && !c.AnyOf.Any(tok => ecc.Consumes(tok, c.InType)))
                unread = c.AnyOf.ToList();

            if (unread.Count > 0)
            {
                var where = c.InType == null ? "any deployed ECC"
                    : ecc.KnowsType(c.InType) ? $"the deployed {c.InType} ECC"
                    : $"the deployed project (type '{c.InType}' was not found)";

                var elsewhere = c.InType != null
                    ? unread.SelectMany(ecc.ConsumersOf).Distinct(StringComparer.Ordinal)
                            .OrderBy(x => x, StringComparer.Ordinal).ToList()
                    : new List<string>();

                var note = elsewhere.Count > 0
                    ? $" - {string.Join("/", unread)} is read only by {string.Join(", ", elsewhere)}, " +
                      "which does not run the recipe"
                    : string.Empty;

                return (HmiUnavailableReason.NotConsumed,
                    $"{where} never reads {string.Join("/", unread)}, so the command would have no effect{note}");
            }

            return (HmiUnavailableReason.None, string.Empty);
        }
    }
}
