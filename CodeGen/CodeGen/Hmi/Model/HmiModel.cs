using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Hmi
{
    // One canvas inside a faceplate template folder.
    // EAE distinguishes the two kinds by geometry alone: a placeable symbol declares SymbolSize,
    // a pop-up faceplate declares only Size. That is also what drives IsFaceplate in the CAT .cfg.
    //
    // CommandCapable comes from the symbol's own .cnv.xml contract: a symbol can reach the controller
    // exactly when the contract declares an <EventOutputs> event or an <Outputs> variable. That is the
    // authoritative test - hiding a button is visibility, not a write block.
    internal sealed record HmiSymbol(
        string Name,
        bool IsFaceplate,
        int Width,
        int Height,
        bool HasContract,
        IReadOnlyList<string> OutputEvents,
        IReadOnlyList<string> OutputTags,
        // The events the code-behind is actually observed to FIRE. A contract that declares an output
        // no handler raises is a canvas that still cannot command anything - the .cnv.xml is a
        // promise, this is the evidence.
        IReadOnlyList<string> FiredEvents,
        // The EXACT calls, e.g. "FireEvent_CTCNF(0)". One event carries several operator actions
        // with opposite meanings, so the payload is what identifies the action.
        IReadOnlyList<string> FiredCalls,
        // The output tags a control is actually BOUND to. A tag-bound control needs no code, so a
        // fired-call search alone would miss every jog button.
        IReadOnlyList<string> BoundTags,
        // Bound, but by a control the faceplate switches off and never switches back on. The
        // action is still REPORTED - silently dropping it would hide the fact from the operator -
        // and it is always withheld.
        IReadOnlyList<string> DeadTags,
        // What each declared output event CARRIES, per the symbol's own contract. An event that
        // matches the deployed interface by name while carrying different data is not the same
        // event, and only this map can tell the two apart.
        IReadOnlyDictionary<string, IReadOnlyList<string>> OutputEventData)
    {
        // The contract's PROMISE: this canvas is allowed to reach the controller.
        public bool CommandCapable => OutputEvents.Count > 0 || OutputTags.Count > 0;

        // What it can ACTUALLY raise, from its own source. A declared <Output> that no control binds
        // and no handler fires is a promise the canvas cannot keep, and treating it as a live command
        // would demand governance for something the operator can never press.
        public bool CanRaiseAnything =>
            FiredCalls.Count > 0 ||
            OutputTags.Any(t => BoundTags.Contains(t, StringComparer.Ordinal) &&
                                !DeadTags.Contains(t, StringComparer.Ordinal));

        // Gate 5, per event: declared on the contract AND raised by the code-behind.
        public bool CanSend(string outputEvent, IReadOnlyList<string> data) =>
            OutputEvents.Contains(outputEvent, StringComparer.OrdinalIgnoreCase) &&
            data.All(d => OutputTags.Contains(d, StringComparer.OrdinalIgnoreCase)) &&
            FiredEvents.Contains(outputEvent, StringComparer.OrdinalIgnoreCase);

        public string Outputs => string.Join(", ", OutputEvents.Concat(OutputTags).Distinct());

        // Does THIS symbol put THIS action in front of the operator? Either its code-behind makes the
        // exact call, or a control is bound to the tag the action writes. Anything else is a symbol
        // that simply does not offer the action, which is not a fault.
        public bool Presents(HmiOperatorAction a) =>
            a.Call != null
                ? FiredCalls.Contains(a.Call, StringComparer.Ordinal)
                : a.Writes != null &&
                  OutputTags.Contains(a.Writes, StringComparer.OrdinalIgnoreCase) &&
                  BoundTags.Contains(a.Writes, StringComparer.Ordinal);
    }

    // A CAT type's faceplate template (one folder under Template Library\HMI\Faceplates).
    internal sealed record HmiCatTemplate(string CatType, string SourceDir, IReadOnlyList<HmiSymbol> Symbols)
    {
        public HmiSymbol? Placeable(string name) =>
            Symbols.FirstOrDefault(s => !s.IsFaceplate && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        // The canvas a component shows when simply placed on a screen. The primary symbol name is
        // passed in rather than read from configuration: a domain record must not do file I/O.
        public HmiSymbol? Primary(string primarySymbol) =>
            Placeable(primarySymbol) ?? Symbols.FirstOrDefault(s => !s.IsFaceplate);
    }

    // A syslay FB drawn on a canvas. TagName is the syslay FB Id, which is the only
    // binding EAE needs to resolve the symbol to the running instance.
    internal sealed record HmiPlaceable(
        string Name, string TagName, string CatType, HmiSymbol Symbol, int X, int Y,
        // Every operator ACTION this placed symbol offers, with the ONE effective verdict.
        // Emission, validation, logging and the report all read this and none re-evaluates it.
        IReadOnlyList<HmiActionVerdict> Actions);

    // A literal, unbound label. Operator captions come from the syslay instance name - never from
    // actuator_name (the lower-case ring/MQTT key) and never from the CAT's untriggered name_event.
    // Emphasis picks the title style; everything else is the ordinary caption style. Model-derived
    // text - the state legend, the interlock explanations, the controller allocation - is rendered
    // with these too, on the read-only detail canvases, so it lands in the compiled panel.
    internal sealed record HmiCaption(string Name, string Text, int X, int Y, bool Emphasis);

    internal sealed record HmiNavButton(string Name, string CanvasName, string Text, int X, int Y);

    internal sealed record HmiScreen(
        string Name,
        string Title,
        IReadOnlyList<HmiPlaceable> Items,
        IReadOnlyList<HmiNavButton> Buttons,
        IReadOnlyList<HmiCaption> Captions);

    internal sealed record HmiSelectedSymbol(string CatType, string Symbol);

    internal sealed record HmiPlan(
        IReadOnlyList<HmiScreen> Screens,
        IReadOnlyList<HmiCatTemplate> UsedTemplates,
        IReadOnlyList<string> Diagnostics,
        // Exactly the (CatType, Symbol) pairs the plan actually places. The project emitter registers
        // THIS set rather than enumerating the directory, so a dormant command symbol that no screen
        // uses is never compiled into the deployed HMI.
        IReadOnlyList<HmiSelectedSymbol> SelectedSymbols,
        // Every (instance x symbol) verdict for the CATs this plan deploys - not only the symbol
        // finally placed. A symbol the CAT partial classes force into the build is compiled even
        // when no canvas places it, so its live calls have to be suppressed as well.
        IReadOnlyList<HmiActionVerdict> AllVerdicts,
        // Bindings the DEPLOYED service interface cannot serve. Suppressed in the staged
        // faceplate and reported; never left to display a value that never arrives.
        IReadOnlyList<HmiDeadBinding> DeadBindings)
    {
        public string FirstCanvas => Screens.Count > 0 ? Screens[0].Name : string.Empty;
    }


}
