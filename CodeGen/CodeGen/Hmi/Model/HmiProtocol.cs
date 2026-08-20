using System;

namespace CodeGen.Hmi
{
    // What an operator control is FOR. Discovery is by contract signature, so this is the vocabulary
    // the resolver reports against - it is never inferred from a component or CAT name.
    //
    // The VALUES behind these purposes (mode numbers, cycle numbers, runtime state numbers and their
    // labels) live in Config/hmi.yml. Nothing is duplicated here: a second copy in C# is exactly how
    // the configuration and the generator drift apart.
    internal enum HmiCapabilityPurpose
    {
        Monitor,
        ModeSelection,
        CycleControl,
        CycleStop,
        ManualStep,
        SetupJog,
        FaultReset,
        InterlockDiagnostics,
    }

    // Why a capability the contract advertises still cannot be offered to an operator.
    internal enum HmiUnavailableReason
    {
        None,
        NoContract,              // the CAT ships no _HMI.fbt
        NoOutputEvent,           // nothing on the contract can reach the controller
        NoAcceptedFeedback,      // we could send it but could never show it was accepted
        ModeChainUnreachable,    // the station/area mode chain does not reach this instance
        NotConsumed,             // the port is declared but no deployed ECC transition reads it
        // Gate 5 - the EMITTED canvas. The controller may honour a command perfectly and the panel
        // still be unable to send it, which is exactly what a monitoring faceplate does.
        SymbolCannotSend,        // the selected .cnv.xml declares no output events at all
        SymbolContractMismatch   // it sends, but not this event / not carrying this data
    }
}
