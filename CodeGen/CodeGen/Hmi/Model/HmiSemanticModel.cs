using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Mapping;
using CodeGen.Translation;

namespace CodeGen.Hmi
{
    // The normalised, immutable HMI intermediate representation.
    //
    // Everything downstream (capability resolution, view planning, emission, validation) reads this and
    // nothing else. Semantics come from GenerationContext (the twin, parsed once); deployment bindings
    // come from the finished syslay and the deployed CAT contracts. Neither is re-derived by parsing a
    // serialised parameter back out of generated XML.

    // One runtime value an instance can report, with the operator-facing name the twin gave it.
    internal sealed record HmiStateName(int Value, string Name);

    // An interlock rule exactly as emitted, joined to names for display. The rule itself is never
    // recomputed here - it is read from the generated RuleTable and only annotated.
    internal sealed record HmiInterlockRule(
        int FromState,
        int ToState,
        int SourceSlot,
        int BlockedState,
        string? SourceComponent,       // null when the slot resolves to nothing on this ring
        string? SourceStateName,
        string FromStateName,
        string ToStateName)
    {
        // "Blocked: Clamp -> Work while Shaft_Hr is Advanced"
        internal string Explain(string owner) =>
            $"Blocked: {owner} {FromStateName} -> {ToStateName} while " +
            $"{SourceComponent ?? $"slot {SourceSlot}"} is {SourceStateName ?? BlockedState.ToString()}";
    }

    // One compiled recipe row, taken from ctx.Recipes rather than parsed from the syslay blob.
    // A capability the deployed contract advertises, plus whether it can honestly be offered.
    internal sealed record HmiCapability(
        HmiCapabilityPurpose Purpose,
        string OutputEvent,
        IReadOnlyList<string> OutputData,
        bool Supported,
        HmiUnavailableReason Reason,
        // Generated at resolve time, naming the exact element that was missing. Shown to the
        // operator instead of a dead control; the Reason enum only classifies it.
        string Detail);

    internal sealed record HmiComponent(
        string ComponentId,            // Control.xml identity
        string InstanceName,           // the emitted syslay FB name
        string DisplayName,            // operator label
        string CatType,                // the CAT actually emitted
        string TagName,                // syslay FB ID - the only EAE binding
        PlcAssignment Controller,
        string Resource,
        int Slot,                      // state_table slot, -1 when unallocated
        IReadOnlyList<HmiStateName> States,
        IReadOnlyList<HmiInterlockRule> Interlocks,
        IReadOnlyList<HmiCapability> Capabilities,
        string? Ring = null)           // report-ring identity; the scope a Slot is meaningful in
    {


        internal string StateName(int value) =>
            States.FirstOrDefault(s => s.Value == value)?.Name ?? value.ToString();
    }

    internal sealed record HmiProcess(
        string ComponentId,
        string InstanceName,
        string DisplayName,
        PlcAssignment Controller,
        string Resource,
        string TagName,
        string CatType,
        int Slot,
        IReadOnlyList<string> Owned,      // instance names this recipe commands
        IReadOnlyList<HmiCapability> Capabilities,
        string? Ring = null)
    {

    }

    // A station/area infrastructure instance - the mode chain nodes.
    internal sealed record HmiStation(
        string InstanceName,
        string TagName,
        string CatType,
        PlcAssignment Controller,
        string Resource,
        bool ReceivesModeBroadcast,
        IReadOnlyList<HmiCapability> Capabilities);

    internal sealed record HmiPlant(
        string ModelName,
        IReadOnlyList<HmiStation> Stations,
        IReadOnlyList<HmiProcess> Processes,
        IReadOnlyList<HmiComponent> Components,
        IReadOnlyList<string> Diagnostics)
    {

        // Every capability this plant resolved, with the instance that owns it. The planner, the
        // validator and the evidence writer each need the same walk; it lives here so they cannot
        // disagree about what the plant offers.
        internal IEnumerable<(string Owner, HmiCapability Cap)> AllCapabilities() =>
            Stations.SelectMany(s => s.Capabilities.Select(c => (s.InstanceName, c)))
                .Concat(Processes.SelectMany(p => p.Capabilities.Select(c => (p.InstanceName, c))))
                .Concat(Components.SelectMany(x => x.Capabilities.Select(c => (x.InstanceName, c))));

    }
}
