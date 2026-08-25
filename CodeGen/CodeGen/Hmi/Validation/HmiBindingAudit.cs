using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Hmi
{
    // Does the FACEPLATE agree with the DEPLOYED service interface?
    //
    // A faceplate's .cnv.xml is a promise about the CAT it will be connected to. The deployed
    // <Cat>_HMI.fbt is what the controller actually offers. Nothing forces the two to match: the
    // shipped Process1 faceplates were authored against a richer Process1_Generic_HMI than this
    // Mapper deploys, so they bind ModeCMD, ProcessComplete, CurrentStep and OperatorInstruction to
    // a service interface that declares none of them.
    //
    // EAE compiles that happily - a TagName is a string - so a clean build proves nothing about it.
    // The binding simply never updates, and the operator reads a field frozen at its initial value,
    // which is worse than a blank: a stale zero looks like data.
    //
    // This audit is the missing check. It is presentation-only: it reports and suppresses bindings,
    // and it never changes a contract, an FB or a wire.
    internal sealed record HmiDeadBinding(
        string CatType, string Symbol, string Tag, string Reason);

    internal static class HmiBindingAudit
    {
        // Every bound TagName on a symbol that the deployed contract cannot serve.
        //
        // A tag is legitimate when the deployed interface can DELIVER it (an InputVar, or a datum
        // carried by one of its input events) or the symbol itself OWNS it as a declared output.
        // Anything else is bound to nothing.
        internal static IReadOnlyList<HmiDeadBinding> DeadInputs(
            string catType, HmiSymbol symbol, HmiContract deployed)
        {
            if (!deployed.Exists || symbol.BoundTags.Count == 0)
                return Array.Empty<HmiDeadBinding>();

            var served = new HashSet<string>(deployed.InputVars, StringComparer.OrdinalIgnoreCase);
            foreach (var e in deployed.Inputs) served.UnionWith(e.With);
            foreach (var v in deployed.OutputVars) served.Add(v);

            var owned = new HashSet<string>(symbol.OutputTags, StringComparer.OrdinalIgnoreCase);

            return symbol.BoundTags
                .Where(t => !served.Contains(t) && !owned.Contains(t))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(t => t, StringComparer.Ordinal)
                .Select(t => new HmiDeadBinding(catType, symbol.Name, t,
                    $"the deployed {catType}_HMI interface declares no '{t}', so the control would " +
                    "display a value the controller never sends"))
                .ToList();
        }

        // An output event the symbol's contract promises but the deployed interface does not offer.
        // A control wired to one of these raises an event that lands nowhere.
        internal static IReadOnlyList<HmiDeadBinding> DeadOutputs(
            string catType, HmiSymbol symbol, HmiContract deployed)
        {
            if (!deployed.Exists) return Array.Empty<HmiDeadBinding>();

            // An event the deployed interface does not declare is dead - and so is every control
            // bound to the data it carries. A tag-bound control writes its output with no code at
            // all, so deleting the FireEvent call is only half the job: without this the panel still
            // writes ManualExecuteStep into an interface that has no such event.
            var undeclared = symbol.OutputEvents
                .Where(e => deployed.Output(e) == null)
                .OrderBy(e => e, StringComparer.Ordinal).ToList();

            var carried = undeclared
                .SelectMany(e => symbol.OutputEventData.TryGetValue(e, out var with)
                    ? with.Select(t => (Event: e, Tag: t))
                    : Enumerable.Empty<(string Event, string Tag)>())
                .Where(x => symbol.BoundTags.Contains(x.Tag, StringComparer.Ordinal))
                .Distinct();

            return undeclared
                .Select(e => new HmiDeadBinding(catType, symbol.Name, e,
                    $"the deployed {catType}_HMI interface declares no output event '{e}', so the " +
                    "command could not leave the panel"))
                .Concat(carried.Select(x => new HmiDeadBinding(catType, symbol.Name, x.Tag,
                    $"'{x.Tag}' is written by a control but is carried only by '{x.Event}', which the " +
                    $"deployed {catType}_HMI interface does not declare")))
                .Concat(symbol.OutputEvents
                    .Select(e => (Event: e, Declared: deployed.Output(e)))
                    .Where(x => x.Declared != null)
                    // Each datum the event carries must exist on the deployed side too: an event
                    // that matches by name while carrying different data is not the same event.
                    .SelectMany(x => symbol.OutputTags
                        .Where(t => ContractCarries(symbol, x.Event, t) &&
                                    !deployed.OutputCarries(x.Event, t))
                        .Select(t => new HmiDeadBinding(catType, symbol.Name, $"{x.Event}.{t}",
                            $"the deployed {catType}_HMI '{x.Event}' does not carry '{t}'"))))
                .ToList();
        }

        // Does the SYMBOL's own contract say this event carries this datum? Only then is a mismatch
        // against the deployed side meaningful - otherwise the tag belongs to a different event.
        private static bool ContractCarries(HmiSymbol symbol, string outputEvent, string tag) =>
            symbol.OutputEventData.TryGetValue(outputEvent, out var with) &&
            with.Any(w => w.Equals(tag, StringComparison.OrdinalIgnoreCase));
    }
}
