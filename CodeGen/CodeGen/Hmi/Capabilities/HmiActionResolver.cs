using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Hmi
{
    // The ONE authoritative answer to "can the operator trigger this?", used by planning, emission,
    // validation, logging and reporting alike.
    //
    // A CAPABILITY proves the controller honours an EVENT. An ACTION is one PAYLOAD on that event -
    // which is what a person actually presses. The two are not interchangeable: CTCNF carries both
    // RUN and STOP, and the recipe engine honours neither, while MCNF carries Setup (real) and
    // Initial Position (interlock-bypassing) from the same button row. Judging per event can only
    // ever get one of each pair right, so every verdict below is per action.
    //
    // Effective means the whole path holds: the controller honours the event (capability gates 1-4),
    // the emitted symbol can raise it (gate 5), and the payload itself survives whatever extra proof
    // it declares. Anything short of that is withheld WITH the reason that failed, and the emitter
    // is required to make it non-fireable rather than merely report it.
    internal sealed record HmiActionVerdict(
        string ActionId,
        string Label,
        string Instance,
        // The CAT as well as the symbol: 'sDefault' and 'sSetup' exist on several CATs, so the
        // symbol name alone would let one CAT's verdict be checked against another CAT's source.
        string CatType,
        string Symbol,
        string? Call,
        string? Writes,
        bool Effective,
        HmiUnavailableReason Reason,
        string Detail);

    internal static class HmiActionResolver
    {
        // Every action the placed symbol puts in front of the operator, with its final verdict.
        // Actions the symbol does not present are absent: a symbol that offers no STOP button is not
        // failing a gate, it simply has nothing to gate.
        internal static IReadOnlyList<HmiActionVerdict> For(
            string instance, string catType, HmiSymbol symbol, IReadOnlyList<HmiCapability> capabilities,
            HmiEccIndex ecc, HmiDefinition def) =>
            def.Actions
                .Where(symbol.Presents)
                .Select(a => Judge(a, instance, catType, symbol, capabilities, ecc))
                .OrderBy(v => v.ActionId, StringComparer.Ordinal)
                .ToList();

        private static HmiActionVerdict Judge(
            HmiOperatorAction a, string instance, string catType, HmiSymbol symbol,
            IReadOnlyList<HmiCapability> capabilities, HmiEccIndex ecc)
        {
            HmiActionVerdict Verdict(bool ok, HmiUnavailableReason reason, string detail) =>
                new(a.Id, a.Label, instance, catType, symbol.Name, a.Call, a.Writes, ok, reason, detail);

            // A control the faceplate hides and never restores cannot be pressed, whatever the
            // controller would honour. Reported rather than dropped, so the panel and the report
            // agree that the operator has no way to reach it.
            if (a.Writes != null && symbol.DeadTags.Contains(a.Writes, StringComparer.Ordinal))
                return Verdict(false, HmiUnavailableReason.SymbolCannotSend,
                    $"the placed '{symbol.Name}' canvas hides this control and never restores it");

            // Gates 1-4: does the controller honour the event this action rides on?
            var cap = capabilities.FirstOrDefault(c => c.Purpose == a.ProvedBy);
            if (cap == null)
                return Verdict(false, HmiUnavailableReason.NoContract,
                    $"no '{a.ProvedBy}' capability was resolved for '{instance}'");
            if (!cap.Supported)
                return Verdict(false, cap.Reason, cap.Detail);

            // Gate 5, for a fired action: the contract must declare the event AND carry its data.
            // A tag-bound action already proved this by being presented at all - the control is
            // bound to a declared output, which is what raises the carrying event.
            if (a.Call != null && !symbol.CanSend(cap.OutputEvent, cap.OutputData))
                return Verdict(false, HmiUnavailableReason.SymbolContractMismatch,
                    $"the placed '{symbol.Name}' canvas does not raise '{cap.OutputEvent}' " +
                    $"carrying {string.Join(", ", cap.OutputData)}");

            // The payload's own proof. This is what separates two actions that share one event.
            if (a.AlsoRequires is { } proof && ecc.Refuses(proof) is { } why)
                return Verdict(false, HmiUnavailableReason.NotConsumed, why);

            return Verdict(true, HmiUnavailableReason.None, string.Empty);
        }
    }
}
